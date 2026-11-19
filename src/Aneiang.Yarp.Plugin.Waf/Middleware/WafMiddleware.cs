using System.Text.Json;
using System.Text.Json.Serialization;
using Aneiang.Yarp.Infrastructure;
using Aneiang.Yarp.Infrastructure.Middleware;
using Aneiang.Yarp.Models;
using Aneiang.Yarp.Plugins;
using Aneiang.Yarp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Model;

namespace Aneiang.Yarp.Plugin.Waf;

/// <summary>
/// Web Application Firewall — a thin request-path shell around <see cref="WafPolicy"/>.
/// </summary>
/// <remarks>
/// All decisions live in the compiled policy; this type only resolves the binding, feeds the
/// request through, and writes the outcome. Keeping it thin is what makes the rules testable
/// without an HTTP server.
/// </remarks>
public sealed class WafMiddleware : GatewayMiddlewareBase
{
    public const string PluginId = "waf";

    /// <summary>Request header carrying the tags applied by <c>action: tag</c> rules.</summary>
    public const string TagHeader = "X-Waf-Tag";

    /// <summary>Response header naming the rule that blocked the request.</summary>
    public const string RuleHeader = "X-Waf-Rule";

    private const string DefaultMessage = "Blocked by security policy";

    /// <summary>Response header telling a throttled client when to come back.</summary>
    public const string RetryAfterHeader = "Retry-After";

    private static readonly JsonSerializerOptions BlockBodyJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<WafMiddleware> _logger;
    private readonly GatewayPluginExecutionPlanProvider _executionPlans;

    /// <summary>
    /// Counters for <c>action: rate-limit</c>. Owned by the middleware rather than by a compiled
    /// policy so that editing any rule does not reset an in-progress quota.
    /// </summary>
    private readonly WafThrottle _throttle = new();

    public WafMiddleware(
        RequestDelegate next,
        ILogger<WafMiddleware> logger,
        IOptions<GatewayMiddlewareOptions> dashOptions,
        IGatewayPluginManager pluginManager,
        GatewayPluginExecutionPlanProvider executionPlans)
        : base(next, dashOptions, pluginManager)
    {
        _logger = logger;
        _executionPlans = executionPlans;
        DashPrefix = "/" + dashOptions.Value.RoutePrefix.Trim('/');
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // The control plane must not be able to lock itself out with its own rules.
        if (IsDashboardRequest(context))
        {
            await Next(context);
            return;
        }

        if (!TryResolvePolicy(context, out var policy, out var routeId))
        {
            await Next(context);
            return;
        }

        var view = WafRequestView.Create(context);

        var preamble = policy.Preamble.Evaluate(view);
        if (preamble.Blocked)
        {
            LogEvent(context, preamble.EventType, null, preamble.Details, policy.Enforce);
            if (policy.Enforce)
            {
                await WriteBlockAsync(context, policy, null, MessageFor(preamble.EventType));
                return;
            }
        }

        if (policy.RequiresBody && WafBodyInspector.IsScannable(view.ContentType, policy.Body))
            view.BodyText = await WafBodyInspector.TryReadAsync(context, policy.Body, _logger);

        foreach (var rule in policy.Rules)
        {
            if (!rule.Matches(view, out var eventType, out var details)) continue;

            switch (rule.Action)
            {
                case WafAction.Allow:
                    // Stop evaluating: the operator said this request is out of scope.
                    await Next(context);
                    return;

                case WafAction.Tag:
                    AppendTag(context, rule.Tag!);
                    break;

                case WafAction.Log:
                    LogEvent(context, eventType, rule, details, enforced: false);
                    break;

                case WafAction.RateLimit:
                    var quota = rule.RateLimit!.Value;
                    var key = ThrottleKey(routeId, rule.Id, quota.Partition, view);
                    if (_throttle.TryAcquire(key, quota, out var retryAfter))
                        break; // within quota — the request is entirely ordinary

                    // One event name for every throttle, whatever detector scoped it: an operator
                    // grepping for abuse wants one term, and the rule id already says which rule.
                    LogEvent(context, "RateLimitBlocked", rule, details, policy.Enforce);
                    if (!policy.Enforce) break; // monitor: record the would-be 429 and move on

                    context.Response.Headers[RetryAfterHeader] =
                        Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                    await WriteBlockAsync(context, policy, rule, MessageFor("RateLimitBlocked"),
                        rule.StatusCode ?? StatusCodes.Status429TooManyRequests);
                    return;

                case WafAction.Block:
                    LogEvent(context, eventType, rule, details, policy.Enforce);
                    if (policy.Enforce)
                    {
                        await WriteBlockAsync(context, policy, rule, MessageFor(eventType));
                        return;
                    }
                    // Monitor mode: the hit is recorded, the request continues untouched.
                    break;
            }
        }

        await Next(context);
    }

    #region Policy resolution

    private bool TryResolvePolicy(HttpContext context, out WafPolicy policy, out string routeId)
    {
        policy = null!;
        routeId = string.Empty;

        var resolvedId = context.Features.Get<IReverseProxyFeature>()?.Route?.Config?.RouteId
            ?? context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config.RouteId;

        if (string.IsNullOrWhiteSpace(resolvedId)) return false;
        if (!_executionPlans.Current.WafByRoute.TryGetValue(resolvedId, out var options) || !options.Enabled) return false;

        policy = WafPolicyCache.For(options, _logger);
        if (policy.IsNoOp) return false;

        routeId = resolvedId;
        return true;
    }

    #endregion

    #region Actions

    private static void AppendTag(HttpContext context, string tag)
    {
        var existing = context.Request.Headers[TagHeader].ToString();
        context.Request.Headers[TagHeader] = string.IsNullOrEmpty(existing) ? tag : $"{existing},{tag}";
    }

    /// <summary>
    /// Route id is part of the key so two routes sharing a rule id never share a quota — the same
    /// rule on a public and an internal route are two separate judgements.
    /// </summary>
    private static string ThrottleKey(string routeId, string ruleId, WafPartition partition, WafRequestView view)
    {
        var ip = string.IsNullOrEmpty(view.ClientIp) ? "-" : view.ClientIp;

        var scope = partition switch
        {
            WafPartition.Path => view.DecodedPath,
            WafPartition.IpPath => ip + "|" + view.DecodedPath,
            WafPartition.Global => "*",
            _ => ip
        };

        return string.Concat(routeId, "\u001f", ruleId, "\u001f", scope);
    }

    private void LogEvent(HttpContext context, string eventType, WafCompiledRule? rule, string? details, bool enforced)
    {
        var clientIp = ClientIpResolver.GetClientIp(context);
        var requestUri = context.Request.Path.Value + context.Request.QueryString.Value;

        _logger.LogWarning(
            "WAF [{EventType}] rule={Rule} from IP: {Ip}, Path: {Path}, Enforced: {Enforced}, Details: {Details}",
            eventType, rule?.Id ?? "-", clientIp, requestUri, enforced, details ?? "(hidden)");
    }

    private static async Task WriteBlockAsync(
        HttpContext context,
        WafPolicy policy,
        WafCompiledRule? rule,
        string message,
        int? statusOverride = null)
    {
        if (context.Response.HasStarted) return;

        var options = policy.Response;

        context.Response.StatusCode = statusOverride ?? rule?.StatusCode ?? options.StatusCode;
        context.Response.ContentType = options.ContentType;

        if (options.Headers is { Count: > 0 })
        {
            foreach (var header in options.Headers)
                context.Response.Headers[header.Key] = header.Value;
        }

        if (options.IncludeRuleHeader && rule is not null)
            context.Response.Headers[RuleHeader] = rule.Id;

        var body = options.Body ?? JsonSerializer.Serialize(
            new BlockBody("Forbidden", message, rule?.Id),
            BlockBodyJson);

        await context.Response.WriteAsync(body, context.RequestAborted);
    }

    /// <summary>
    /// Kept deliberately close to the previous wording: operators and log queries already key on
    /// these event names, and renaming them would silently break existing alerting.
    /// </summary>
    private static string MessageFor(string eventType) => eventType switch
    {
        "IpBlocked" => "Access denied",
        "RequestSizeBlocked" => "Request entity too large",
        "MalformedHeadersBlocked" => "Too many headers",
        "UriTooLongBlocked" => "URI too long",
        "RateLimitBlocked" => "Too many requests",
        _ => DefaultMessage
    };

    #endregion

    /// <summary>Lowercase members on purpose: this is the wire shape clients already receive.</summary>
    private sealed record BlockBody(string error, string message, string? rule)
    {
        public bool waf => true;
    }
}
