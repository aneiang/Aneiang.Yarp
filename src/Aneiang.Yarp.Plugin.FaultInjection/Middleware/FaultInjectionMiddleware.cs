using System.Collections.Concurrent;
using Aneiang.Yarp.Infrastructure.Middleware;
using Aneiang.Yarp.Models;
using Aneiang.Yarp.Plugins;
using Aneiang.Yarp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Model;

namespace Aneiang.Yarp.Plugin.FaultInjection;

/// <summary>
/// Injects latency or failures into a route on purpose, so the gateway's own resilience can be
/// exercised instead of hoped for.
/// </summary>
/// <remarks>
/// <para><b>Why it sits at the end of the proxy pipeline.</b> This middleware runs closest to the
/// forwarder, which makes an injected fault indistinguishable from a broken upstream to everything
/// outside it: the retry plugin retries it, the circuit breaker counts it, the metrics record a 5xx
/// and the alert rules fire. Sending the fault from the edge instead would produce a drill that
/// proves nothing — the very components being drilled would never see it.</para>
///
/// <para><b>Why the delay honours <c>RequestAborted</c>.</b> A client that gives up must stop
/// costing a pending timer, otherwise a long injected delay turns into a pile of tasks that only
/// the timeout cleans up.</para>
///
/// <para><b>Why the binding is the off switch, not the plugin.</b> The snapshot compiler drops the
/// bindings of a disabled plugin <em>and</em> the plugin manager refuses to disable a plugin that
/// still has bindings ("remove plugin bindings first"), so that a mis-click cannot silently discard
/// a drill configuration. The switch you reach for mid-drill is therefore the binding's own enabled
/// flag: the next request stops being injected, the configuration stays for the next drill.</para>
/// </remarks>
public sealed class FaultInjectionMiddleware : GatewayMiddlewareBase
{
    /// <summary>Plugin id, also the key in the compiled execution plan.</summary>
    public const string PluginId = "fault-injection";

    private readonly ILogger<FaultInjectionMiddleware> _logger;
    private readonly GatewayPluginExecutionPlanProvider _executionPlans;

    /// <summary>Routes already reported once, so a 100% drill cannot flood the log.</summary>
    private readonly ConcurrentDictionary<string, byte> _announced = new(StringComparer.OrdinalIgnoreCase);

    private long _injected;

    /// <summary>Initializes a new instance of the <see cref="FaultInjectionMiddleware"/> class.</summary>
    public FaultInjectionMiddleware(
        RequestDelegate next,
        ILogger<FaultInjectionMiddleware> logger,
        IOptions<GatewayMiddlewareOptions> dashOptions,
        GatewayPluginExecutionPlanProvider executionPlans,
        IGatewayPluginManager? pluginManager = null,
        IDynamicYarpConfigService? yarpConfig = null)
        : base(next, dashOptions, pluginManager, yarpConfig)
    {
        _logger = logger;
        _executionPlans = executionPlans;
    }

    /// <summary>Requests answered with an injected fault since start. Deliberately not persisted.</summary>
    public long InjectedCount => Interlocked.Read(ref _injected);

    /// <inheritdoc />
    public async Task InvokeAsync(HttpContext context)
    {
        if (IsDashboardRequest(context))
        {
            await Next(context);
            return;
        }

        var routeId = context.Features.Get<IReverseProxyFeature>()?.Route?.Config?.RouteId;
        if (string.IsNullOrWhiteSpace(routeId) ||
            !_executionPlans.Current.FaultInjectionByRoute.TryGetValue(routeId, out var config))
        {
            await Next(context);
            return;
        }

        var plan = FaultInjectionPlanner.Plan(
            config,
            name => context.Request.Headers.TryGetValue(name, out var values) ? values.ToString() : null,
            Random.Shared.NextDouble());

        if (plan.IsNoOp)
        {
            await Next(context);
            return;
        }

        Interlocked.Increment(ref _injected);
        AnnounceOnce(routeId, plan);

        if (plan.Delay > TimeSpan.Zero && !await DelayAsync(context, plan.Delay))
            return;

        if (plan.Failure == FaultInjectionFailure.None)
        {
            await Next(context);
            return;
        }

        if (plan.Failure == FaultInjectionFailure.Abort)
        {
            _logger.LogDebug("Fault drill: aborting connection for route {RouteId}", routeId);
            context.Abort();
            return;
        }

        context.Response.StatusCode = plan.StatusCode;
        if (plan.Announce)
            context.Response.Headers[FaultInjectionPlanner.AnnounceHeader] = Describe(plan);

        // Only claim JSON when we are writing the default body; a caller-supplied body is theirs to
        // describe and guessing the content type would corrupt it.
        context.Response.ContentType = string.IsNullOrWhiteSpace(config.ErrorBody)
            ? "application/json; charset=utf-8"
            : "text/plain; charset=utf-8";

        await context.Response.WriteAsync(plan.Body, context.RequestAborted);
    }

    /// <summary>
    /// Waits for the injected delay. Returns <c>false</c> when the client went away, in which case
    /// the pipeline must not continue: there is nobody left to answer.
    /// </summary>
    private async Task<bool> DelayAsync(HttpContext context, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, context.RequestAborted);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Expected: the caller hung up during the injected delay. Nothing to log at warning
            // level - a drill on a slow route produces this for every client that gives up.
            _logger.LogDebug("Fault drill: client aborted during an injected {Delay} ms delay", delay.TotalMilliseconds);
            return false;
        }
    }

    /// <summary>
    /// Logs the first injection per route at warning level. Everything after that is debug, because
    /// a drill at 100% would otherwise drown the log that is supposed to explain the drill.
    /// </summary>
    private void AnnounceOnce(string routeId, FaultInjectionPlan plan)
    {
        if (!_announced.TryAdd(routeId, 0))
        {
            _logger.LogDebug("Fault drill on route {RouteId}: {Description}", routeId, Describe(plan));
            return;
        }

        _logger.LogWarning(
            "Fault drill active on route {RouteId}: {Description}. Turn this binding off to stop it.",
            routeId, Describe(plan));
    }

    private static string Describe(FaultInjectionPlan plan) => plan.Failure switch
    {
        FaultInjectionFailure.Abort => plan.Delay > TimeSpan.Zero
            ? $"delay {plan.Delay.TotalMilliseconds:0} ms then abort"
            : "abort",
        FaultInjectionFailure.StatusCode => plan.Delay > TimeSpan.Zero
            ? $"delay {plan.Delay.TotalMilliseconds:0} ms then {plan.StatusCode}"
            : plan.StatusCode.ToString(),
        _ => $"delay {plan.Delay.TotalMilliseconds:0} ms"
    };
}
