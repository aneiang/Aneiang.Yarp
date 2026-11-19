using Aneiang.Yarp.Dashboard.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.Dashboard.Infrastructure.Security;

/// <summary>
/// Adds the standard security response headers to dashboard responses.
/// </summary>
/// <remarks>
/// <para>
/// Scoped to the dashboard's own path prefix on purpose. A gateway also carries the traffic of the
/// applications behind it, and injecting <c>X-Frame-Options</c> or a content policy into someone
/// else's response would break their site — a control that damages the thing it protects is not a
/// control.
/// </para>
/// <para>
/// Headers are written before the next middleware runs, using indexers so a later component that
/// sets its own value replaces ours instead of producing a duplicate header. The alternative
/// (<c>OnStarting</c>) reads better but only fires when a real server starts the response, which
/// makes the behaviour untestable without a socket.
/// </para>
/// </remarks>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly DashboardOptions _options;

    public SecurityHeadersMiddleware(RequestDelegate next, IOptions<DashboardOptions> options)
    {
        _next = next;
        _options = options.Value;
    }

    /// <summary>
    /// The policy, as a constant so tests can assert on it without re-deriving the string.
    /// </summary>
    /// <remarks>
    /// <c>'unsafe-inline'</c> for scripts and styles is an honest admission: the dashboard renders
    /// inline <c>&lt;script&gt;</c> blocks. What this policy still buys is the part that matters for
    /// this app — no external script origins, no <c>&lt;base&gt;</c> rewriting, no form posts
    /// elsewhere, no framing, no plugins.
    /// </remarks>
    public const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self' data:; " +
        "connect-src 'self'; " +
        "worker-src 'self' blob:; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    /// <summary>Applies the headers when the request is for a dashboard path.</summary>
    public Task InvokeAsync(HttpContext context)
    {
        if (IsDashboardPath(context.Request.Path))
            ApplyHeaders(context);

        return _next(context);
    }

    private bool IsDashboardPath(PathString path)
    {
        var prefix = "/" + _options.RoutePrefix.Trim('/');
        if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)) return true;

        // The login POST and the pages it redirects to live under the same prefix, but a
        // deployment using a custom prefix still needs the login route hardened.
        return path.StartsWithSegments("/login", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyHeaders(HttpContext context)
    {
        var headers = context.Response.Headers;

        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";

        // Both, deliberately: XFO is what older browsers honour, frame-ancestors is what modern ones
        // do, and the panel has buttons that delete clusters.
        headers[_options.Security.EnforceContentSecurityPolicy
            ? "Content-Security-Policy"
            : "Content-Security-Policy-Report-Only"] = ContentSecurityPolicy;

        if (_options.Security.EnableHsts && context.Request.IsHttps)
        {
            headers["Strict-Transport-Security"] =
                $"max-age={_options.Security.HstsMaxAgeSeconds}; includeSubDomains";
        }
    }
}
