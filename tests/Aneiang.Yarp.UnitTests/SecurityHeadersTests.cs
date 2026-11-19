using Aneiang.Yarp.Dashboard.Infrastructure;
using Aneiang.Yarp.Dashboard.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// SEC-07: the control plane grew its own set of response headers, and they must not leak into the
/// traffic of the applications behind the gateway.
/// </summary>
public sealed class SecurityHeadersTests
{
    private static (SecurityHeadersMiddleware Middleware, DefaultHttpContext Context) Build(
        string path, Action<DashboardOptions>? configure = null, bool https = false, int statusCode = 200)
    {
        var options = new DashboardOptions();
        configure?.Invoke(options);

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Scheme = https ? "https" : "http";
        context.Response.Body = new MemoryStream();

        var middleware = new SecurityHeadersMiddleware(
            async ctx =>
            {
                ctx.Response.StatusCode = statusCode;
                await ctx.Response.WriteAsync("ok");
            },
            Options.Create(options));

        return (middleware, context);
    }

    [Fact]
    public async Task Dashboard_responses_carry_the_baseline_headers()
    {
        var (middleware, context) = Build("/apigateway/routes");

        await middleware.InvokeAsync(context);

        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", context.Response.Headers["X-Frame-Options"]);
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"]);
        Assert.Equal("same-origin", context.Response.Headers["Cross-Origin-Opener-Policy"]);
    }

    [Fact]
    public async Task Denied_requests_are_hardened_too()
    {
        // A 403 is exactly what a clickjacking attempt wants to render inside a frame.
        var (middleware, context) = Build("/apigateway/api/config/routes", statusCode: 403);

        await middleware.InvokeAsync(context);

        Assert.Equal("DENY", context.Response.Headers["X-Frame-Options"]);
    }

    [Fact]
    public async Task Proxied_traffic_does_not_get_the_dashboard_headers()
    {
        // Injecting XFO or a policy into someone else's response would break their site.
        var (middleware, context) = Build("/api/orders/42");

        await middleware.InvokeAsync(context);

        Assert.True(string.IsNullOrEmpty(context.Response.Headers["X-Frame-Options"].ToString()));
        Assert.True(string.IsNullOrEmpty(context.Response.Headers["Content-Security-Policy"].ToString()));
    }

    [Fact]
    public void The_policy_frames_nothing_and_pins_the_origin()
    {
        // The parts that matter for a panel with destructive buttons.
        Assert.Contains("frame-ancestors 'none'", SecurityHeadersMiddleware.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", SecurityHeadersMiddleware.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("base-uri 'self'", SecurityHeadersMiddleware.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("form-action 'self'", SecurityHeadersMiddleware.ContentSecurityPolicy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_policy_starts_in_report_only_mode()
    {
        var (middleware, context) = Build("/apigateway/");

        await middleware.InvokeAsync(context);

        Assert.True(string.IsNullOrEmpty(context.Response.Headers["Content-Security-Policy"].ToString()));
        Assert.Equal(SecurityHeadersMiddleware.ContentSecurityPolicy, context.Response.Headers["Content-Security-Policy-Report-Only"].ToString());
    }

    [Fact]
    public async Task Enforcing_swaps_the_header_name()
    {
        var (middleware, context) = Build("/apigateway/",
            options => options.Security.EnforceContentSecurityPolicy = true);

        await middleware.InvokeAsync(context);

        Assert.Equal(SecurityHeadersMiddleware.ContentSecurityPolicy, context.Response.Headers["Content-Security-Policy"].ToString());
        Assert.True(string.IsNullOrEmpty(context.Response.Headers["Content-Security-Policy-Report-Only"].ToString()));
    }

    [Fact]
    public async Task Hsts_only_appears_over_https()
    {
        var (httpMiddleware, httpContext) = Build("/apigateway/");
        await httpMiddleware.InvokeAsync(httpContext);
        Assert.True(string.IsNullOrEmpty(httpContext.Response.Headers["Strict-Transport-Security"].ToString()));

        var (httpsMiddleware, httpsContext) = Build("/apigateway/", https: true);
        await httpsMiddleware.InvokeAsync(httpsContext);
        Assert.Contains("max-age=31536000", httpsContext.Response.Headers["Strict-Transport-Security"].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hsts_can_be_switched_off_for_an_internal_http_deployment()
    {
        var (middleware, context) = Build("/apigateway/",
            options => options.Security.EnableHsts = false, https: true);

        await middleware.InvokeAsync(context);

        Assert.True(string.IsNullOrEmpty(context.Response.Headers["Strict-Transport-Security"].ToString()));
    }

    [Fact]
    public async Task The_login_page_is_covered_as_well()
    {
        var (middleware, context) = Build("/login");

        await middleware.InvokeAsync(context);

        Assert.Equal("DENY", context.Response.Headers["X-Frame-Options"]);
    }
}
