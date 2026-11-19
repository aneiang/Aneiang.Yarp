using Microsoft.AspNetCore.Http;

namespace Aneiang.Yarp.Infrastructure;

/// <summary>
/// Unified client IP resolution from HTTP headers.
/// Centralizes the X-Forwarded-For / X-Real-IP parsing that was previously duplicated across
/// WAF middleware, rate-limit middleware, controllers, and load-balancing policies.
/// </summary>
public static class ClientIpResolver
{
    /// <summary>
    /// Resolves the client IP established by ASP.NET Core's trusted forwarded-header middleware.
    /// Raw forwarding headers are deliberately ignored here because they are attacker-controlled
    /// unless the application has validated the immediate proxy against KnownProxies/KnownNetworks.
    /// </summary>
    public static string? GetClientIp(HttpContext context)
    {
        return context.Connection.RemoteIpAddress?.ToString();
    }

    /// <summary>
    /// Resolves the connection-facing IP, preferring the direct connection endpoint.
    /// This is intended for load-balancing scenarios (e.g. sticky sessions) where the
    /// actual network peer is more relevant than a potentially spoofed forwarded header.
    /// </summary>
    /// <remarks>
    /// There used to be a fallback to raw <c>X-Forwarded-For</c> here when
    /// <c>RemoteIpAddress</c> was null. It was removed: the other method in this class documents
    /// those headers as attacker-controlled, and a second code path that trusts them means the
    /// guarantee only holds for callers who picked the right method. Proxied deployments get their
    /// address from <c>UseForwardedHeaders</c> with a configured trust list, which writes to
    /// <c>RemoteIpAddress</c> anyway.
    /// </remarks>
    public static string? GetConnectionIp(HttpContext context)
    {
        return context.Connection.RemoteIpAddress?.ToString();
    }
}
