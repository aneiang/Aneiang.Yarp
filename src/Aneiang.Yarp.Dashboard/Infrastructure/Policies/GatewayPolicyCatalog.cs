using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aneiang.Yarp.Dashboard.Infrastructure.Policies;

/// <summary>The four policy-reference fields a YARP route can carry.</summary>
public enum GatewayPolicyKind
{
    /// <summary><c>RouteConfig.AuthorizationPolicy</c></summary>
    Authorization,

    /// <summary><c>RouteConfig.CorsPolicy</c></summary>
    Cors,

    /// <summary><c>RouteConfig.RateLimiterPolicy</c></summary>
    RateLimiter,

    /// <summary><c>RouteConfig.OutputCachePolicy</c></summary>
    OutputCache
}

/// <summary>Outcome of looking a policy name up.</summary>
public enum PolicyResolutionStatus
{
    /// <summary>The name is known to be registered.</summary>
    Found,

    /// <summary>The provider was asked and does not know this name — using it will fail at runtime.</summary>
    Unknown,

    /// <summary>ASP.NET Core exposes no way to enumerate this kind, so a miss proves nothing.</summary>
    Unverifiable
}

/// <summary>A policy the dashboard can offer in a dropdown.</summary>
public sealed record GatewayPolicyInfo(
    string Name,
    GatewayPolicyKind Kind,
    string Description,
    bool BuiltIn);

/// <summary>Result of resolving one policy reference.</summary>
public sealed record PolicyResolution(PolicyResolutionStatus Status, string? Name);

/// <summary>
/// Knows which named ASP.NET Core policies exist, so a route's
/// <c>AuthorizationPolicy</c> / <c>CorsPolicy</c> / <c>RateLimiterPolicy</c> /
/// <c>OutputCachePolicy</c> can be offered as a dropdown instead of free text.
///
/// This exists because those four fields used to be free-text boxes: typing a name that
/// was never registered saved fine and then blew up at request time (or at proxy startup),
/// with no hint about what went wrong. That is the worst possible failure mode, so the
/// catalog is now the authoritative answer to "can I use this name?".
/// </summary>
public interface IGatewayPolicyCatalog
{
    /// <summary>Built-in plus currently referenced policies of one kind.</summary>
    IReadOnlyList<GatewayPolicyInfo> GetPolicies(GatewayPolicyKind kind);

    /// <summary>Every known policy, for the picker.</summary>
    IReadOnlyList<GatewayPolicyInfo> GetAll();

    /// <summary>
    /// Resolves a name. Authorization and CORS can be probed through their providers;
    /// rate limiter and output cache cannot be enumerated, so an unknown name there
    /// comes back as <see cref="PolicyResolutionStatus.Unverifiable"/> rather than a lie.
    /// </summary>
    Task<PolicyResolution> ResolveAsync(GatewayPolicyKind kind, string? name);
}

/// <summary>Default catalog backed by the ASP.NET Core providers plus the built-in set.</summary>
public sealed class GatewayPolicyCatalog : IGatewayPolicyCatalog
{
    private readonly IServiceProvider _services;
    private readonly List<GatewayPolicyInfo> _builtIns;

    public GatewayPolicyCatalog(IServiceProvider services)
    {
        _services = services;
        _builtIns = new List<GatewayPolicyInfo>(GatewayPolicyDefaults.All);
    }

    public IReadOnlyList<GatewayPolicyInfo> GetPolicies(GatewayPolicyKind kind)
        => _builtIns.Where(p => p.Kind == kind).ToList();

    public IReadOnlyList<GatewayPolicyInfo> GetAll() => _builtIns.ToList();

    public async Task<PolicyResolution> ResolveAsync(GatewayPolicyKind kind, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new PolicyResolution(PolicyResolutionStatus.Found, null);

        var trimmed = name.Trim();

        if (_builtIns.Any(p => p.Kind == kind && string.Equals(p.Name, trimmed, StringComparison.Ordinal)))
            return new PolicyResolution(PolicyResolutionStatus.Found, trimmed);

        switch (kind)
        {
            case GatewayPolicyKind.Authorization:
            {
                var provider = _services.GetService<IAuthorizationPolicyProvider>();
                if (provider == null)
                    return new PolicyResolution(PolicyResolutionStatus.Unverifiable, trimmed);

                // Unlike the others, this provider is authoritative: a null answer means
                // ASP.NET Core will throw "The AuthorizationPolicy named 'x' was not found".
                var policy = await provider.GetPolicyAsync(trimmed);
                return policy != null
                    ? new PolicyResolution(PolicyResolutionStatus.Found, trimmed)
                    : new PolicyResolution(PolicyResolutionStatus.Unknown, trimmed);
            }

            case GatewayPolicyKind.Cors:
            {
                var provider = _services.GetService<ICorsPolicyProvider>();
                if (provider == null)
                    return new PolicyResolution(PolicyResolutionStatus.Unverifiable, trimmed);

                // A throwaway context is enough: name-based providers ignore it, and the
                // default provider only needs it for the (unused) default policy path.
                var policy = await provider.GetPolicyAsync(new DefaultHttpContext(), trimmed);
                return policy != null
                    ? new PolicyResolution(PolicyResolutionStatus.Found, trimmed)
                    : new PolicyResolution(PolicyResolutionStatus.Unknown, trimmed);
            }

            default:
                // RateLimiterOptions.PolicyMap and OutputCacheOptions.Policies are internal,
                // so there is no public enumeration API. Refusing to guess keeps custom
                // host-registered policies working.
                return new PolicyResolution(PolicyResolutionStatus.Unverifiable, trimmed);
        }
    }
}

/// <summary>
/// The policies Aneiang.Yarp ships so the four reference fields are usable out of the box.
/// Without these, the only valid value was "empty" — which is exactly why users typed
/// plausible-looking names and got silent breakage.
/// </summary>
public static class GatewayPolicyDefaults
{
    public const string AuthorizationAnonymous = "gw-anonymous";
    public const string AuthorizationAuthenticated = "gw-authenticated";
    public const string CorsOpen = "gw-cors-open";
    public const string RateLimit100PerMinute = "gw-rl-100pm";
    public const string RateLimit1000PerMinute = "gw-rl-1000pm";
    public const string RateLimit10PerSecond = "gw-rl-10ps";
    public const string Cache30Seconds = "gw-cache-30s";
    public const string Cache5Minutes = "gw-cache-5m";
    public const string Cache1Hour = "gw-cache-1h";

    /// <summary>Every built-in policy, in the order the picker shows them.</summary>
    public static IReadOnlyList<GatewayPolicyInfo> All { get; } = new List<GatewayPolicyInfo>
    {
        new(AuthorizationAnonymous, GatewayPolicyKind.Authorization,
            "允许匿名访问；用于覆盖全局的 fallback 授权策略", true),
        new(AuthorizationAuthenticated, GatewayPolicyKind.Authorization,
            "要求已认证用户；需宿主先接入认证方案", true),

        new(CorsOpen, GatewayPolicyKind.Cors,
            "允许任意来源、方法与请求头", true),

        new(RateLimit100PerMinute, GatewayPolicyKind.RateLimiter,
            "按客户端 IP 限流：100 次/分钟", true),
        new(RateLimit1000PerMinute, GatewayPolicyKind.RateLimiter,
            "按客户端 IP 限流：1000 次/分钟", true),
        new(RateLimit10PerSecond, GatewayPolicyKind.RateLimiter,
            "按客户端 IP 限流：10 次/秒", true),

        new(Cache30Seconds, GatewayPolicyKind.OutputCache,
            "缓存响应 30 秒", true),
        new(Cache5Minutes, GatewayPolicyKind.OutputCache,
            "缓存响应 5 分钟", true),
        new(Cache1Hour, GatewayPolicyKind.OutputCache,
            "缓存响应 1 小时", true)
    };
}

/// <summary>Registration helpers for the built-in policy set and the catalog.</summary>
public static class GatewayPolicyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the built-in named policies plus <see cref="IGatewayPolicyCatalog"/>.
    /// Safe to call alongside host-side <c>AddAuthorization</c>/<c>AddCors</c> calls:
    /// every API here is additive.
    /// </summary>
    public static IServiceCollection AddGatewayPolicies(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            options.AddPolicy(GatewayPolicyDefaults.AuthorizationAnonymous, policy =>
                policy.RequireAssertion(_ => true));
            options.AddPolicy(GatewayPolicyDefaults.AuthorizationAuthenticated, policy =>
                policy.RequireAuthenticatedUser());
        });

        services.AddCors(options =>
        {
            options.AddPolicy(GatewayPolicyDefaults.CorsOpen, policy =>
                policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
        });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            AddIpFixedWindow(options, GatewayPolicyDefaults.RateLimit100PerMinute, 100, TimeSpan.FromMinutes(1));
            AddIpFixedWindow(options, GatewayPolicyDefaults.RateLimit1000PerMinute, 1000, TimeSpan.FromMinutes(1));
            AddIpFixedWindow(options, GatewayPolicyDefaults.RateLimit10PerSecond, 10, TimeSpan.FromSeconds(1));
        });

        services.AddOutputCache(options =>
        {
            options.AddPolicy(GatewayPolicyDefaults.Cache30Seconds, builder => builder.Expire(TimeSpan.FromSeconds(30)));
            options.AddPolicy(GatewayPolicyDefaults.Cache5Minutes, builder => builder.Expire(TimeSpan.FromMinutes(5)));
            options.AddPolicy(GatewayPolicyDefaults.Cache1Hour, builder => builder.Expire(TimeSpan.FromHours(1)));
        });

        services.TryAddSingleton<IGatewayPolicyCatalog, GatewayPolicyCatalog>();
        return services;
    }

    private static void AddIpFixedWindow(
        RateLimiterOptions options,
        string policyName,
        int permitLimit,
        TimeSpan window)
    {
        options.AddPolicy(policyName, context => RateLimitPartition.GetFixedWindowLimiter(
            // Behind a proxy the connection address is the proxy; ClientIpResolver already
            // understands X-Forwarded-For, so reuse it instead of reading the socket.
            Aneiang.Yarp.Infrastructure.ClientIpResolver.GetClientIp(context) ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = window,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));
    }
}
