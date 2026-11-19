using Aneiang.Yarp.Dashboard.Infrastructure;
using Aneiang.Yarp.Dashboard.Infrastructure.Deployment;
using Aneiang.Yarp.Dashboard.Infrastructure.Middleware;
using Aneiang.Yarp.Dashboard.Infrastructure.Plugin;
using Aneiang.Yarp.Dashboard.Infrastructure.Realtime;
using Aneiang.Yarp.Dashboard.Infrastructure.Security;
using Aneiang.Yarp.Dashboard.Infrastructure.Yarp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.Dashboard.Extensions;

/// <summary>
/// IApplicationBuilder and IEndpointRouteBuilder extensions for Aneiang.Yarp.Dashboard.
/// </summary>
public static class DashboardApplicationBuilderExtensions
{
    private const string DashboardConfiguredKey = "__AneiangYarpDashboard_Configured";

    /// <summary>Controls which built-in middleware is automatically mounted by <see cref="UseAneiangYarpDashboard(IApplicationBuilder, DashboardUseOptions?, Action{IReverseProxyApplicationBuilder}?)"/>.</summary>
    public sealed class DashboardUseOptions
    {
        /// <summary>Override <see cref="DeploymentOptions.AutoUseMiddleware"/>. Null means use deployment configuration.</summary>
        public bool? AutoUseMiddleware { get; set; }

        /// <summary>Mount deployment endpoint-router and health-check middleware when auto-use is enabled.</summary>
        public bool UseDeploymentMiddleware { get; set; } = true;

        /// <summary>Mount proxy request/response capture middleware when auto-use is enabled.</summary>
        public bool UseProxyRequestCapture { get; set; } = true;

        /// <summary>Mount built-in proxy branch middleware when auto-use is enabled.</summary>
        public bool UseBuiltInProxyPipeline { get; set; } = true;

        /// <summary>
        /// Automatically register a default permissive CORS middleware (AllowAnyOrigin/Method/Header).
        /// Set to <c>false</c> and call <c>app.UseCors(...)</c> yourself before <c>app.UseAneiangYarpDashboard(...)</c>
        /// to use a custom CORS policy.
        /// </summary>
        public bool AutoUseCors { get; set; } = true;

        /// <summary>
        /// Automatically register authorization middleware (<c>UseAuthorization()</c>).
        /// Required for YARP routes with non-<c>Anonymous</c> <c>AuthorizationPolicy</c>.
        /// Set to <c>false</c> if you call <c>app.UseAuthorization()</c> yourself.
        /// Call <c>app.UseAuthentication()</c> before <c>UseAneiangYarpDashboard()</c> if your
        /// auth policies require authenticated users.
        /// </summary>
        public bool AutoUseAuthorization { get; set; } = true;

        /// <summary>
        /// Record per-request metrics for the Prometheus <c>/metrics</c> endpoint.
        /// Mounted in the reverse-proxy pipeline, so it keeps counting even when no
        /// observability plugin is bound. Disable only if you supply your own exporter.
        /// </summary>
        public bool UseMetricsCollection { get; set; } = true;

        /// <summary>
        /// Map the <c>/metrics</c> endpoint. Like <c>/health</c>, it is unauthenticated —
        /// restrict access at the network layer if the gateway is internet-facing.
        /// </summary>
        public bool MapMetricsEndpoint { get; set; } = true;

        /// <summary>
        /// Mount <c>UseRateLimiter()</c> so route <c>RateLimiterPolicy</c> metadata is
        /// actually enforced. Set to <c>false</c> only if you mount it yourself.
        /// </summary>
        public bool UseRateLimiterMiddleware { get; set; } = true;

        /// <summary>
        /// Open the <c>gateway.forward</c> span around each proxied request. The middleware
        /// no-ops when no tracer is listening, so leaving it on costs one boolean check;
        /// turn it off only if you instrument the pipeline yourself.
        /// </summary>
        public bool UseTracingMiddleware { get; set; } = true;
    }

    /// <summary>
    /// Register Dashboard middleware and map the YARP proxy endpoint with core middleware
    /// in the correct pipeline order. When <see cref="DeploymentOptions.Mode"/> is set,
    /// this method intelligently mounts only the components needed for the current mode
    /// (AllInOne / Split / ProxyOnly / DashboardOnly).
    /// </summary>
    public static IApplicationBuilder UseAneiangYarpDashboard(
        this IApplicationBuilder app,
        Action<IReverseProxyApplicationBuilder>? configureProxyPipeline = null)
        => app.UseAneiangYarpDashboard(useOptions: null, configureProxyPipeline);

    /// <summary>
    /// Register Dashboard middleware with explicit opt-out controls for advanced custom pipelines.
    /// </summary>
    public static IApplicationBuilder UseAneiangYarpDashboard(
        this IApplicationBuilder app,
        DashboardUseOptions? useOptions,
        Action<IReverseProxyApplicationBuilder>? configureProxyPipeline = null)
    {
        if (app.Properties.TryGetValue(DashboardConfiguredKey, out _))
        {
            return app;
        }
        app.Properties[DashboardConfiguredKey] = true;

        useOptions ??= new DashboardUseOptions();
        var mode = DeploymentMode.AllInOne;
        var autoUseMiddleware = true;
        try
        {
            var options = app.ApplicationServices.GetService<IOptions<DeploymentOptions>>();
            if (options != null)
            {
                mode = options.Value.Mode;
                autoUseMiddleware = options.Value.AutoUseMiddleware;
            }
        }
        catch
        {
            // Fall back to AllInOne if DeploymentOptions is not registered (backward compat)
        }

        autoUseMiddleware = useOptions.AutoUseMiddleware ?? autoUseMiddleware;

        // First in the dashboard pipeline: a 401 or a 403 is exactly the response a hostile iframe
        // or a sniffing browser would act on, so the headers must be attached before any of it.
        app.UseMiddleware<SecurityHeadersMiddleware>();

        var dashboardActive = mode != DeploymentMode.ProxyOnly;
        var proxyActive = mode != DeploymentMode.DashboardOnly;

        // CORS middleware must appear after UseRouting() but before endpoint execution.
        // YARP routes may carry CorsPolicy metadata (set via appsettings or Dashboard),
        // which requires an active CORS middleware in the pipeline.
        // Set AutoUseCors = false and call app.UseCors(...) yourself for a custom policy.
        if (useOptions.AutoUseCors)
        {
            app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
        }

        // Authorization middleware — same rationale as CORS above.
        // YARP routes may carry AuthorizationPolicy metadata; without UseAuthorization()
        // in the pipeline, ASP.NET Core will throw InvalidOperationException at runtime.
        // Set AutoUseAuthorization = false if you already call UseAuthorization() before
        // UseAneiangYarpDashboard(), or if you need UseAuthentication() before it.
        if (useOptions.AutoUseAuthorization)
        {
            app.UseAuthorization();
        }

        // Route RateLimiterPolicy metadata is only honoured when the rate limiter
        // middleware is in the pipeline. Without this the built-in gw-rl-* policies would
        // be registered but never enforced — the exact silent no-op we are removing.
        if (useOptions.UseRateLimiterMiddleware)
        {
            app.UseRateLimiter();
        }

        if (autoUseMiddleware && useOptions.UseDeploymentMiddleware)
        {
            UseDeploymentMiddlewareIfAvailable(app);
        }

        if (dashboardActive || proxyActive)
        {
            app.UseResponseCompression();
        }

        if (dashboardActive)
        {
            app.UseStaticFiles();
        }

        if (autoUseMiddleware)
        {
            app.Use((context, next) => context.RequestServices
                .GetRequiredService<PluginRuntimeDomainManager>()
                .InvokeMiddlewareAsync(context, next));
        }

        // Resolve DashboardOptions for route prefix (used by Hub mappings below)
        var dashOpts = app.ApplicationServices.GetService<IOptions<DashboardOptions>>()?.Value;
        var routePrefix = (dashOpts?.RoutePrefix ?? "apigateway").Trim('/');

        if (app is IEndpointRouteBuilder endpoints)
        {
            // Prometheus scrape endpoint. Mapped outside MVC on purpose: the dashboard
            // auth conventions must not apply, same as /health and /ready.
            if (useOptions.MapMetricsEndpoint)
            {
                endpoints.MapGet("/metrics", async context =>
                {
                    var renderer = context.RequestServices
                        .GetRequiredService<Infrastructure.Observability.GatewayMetricsRenderer>();

                    context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
                    context.Response.Headers.CacheControl = "no-store";
                    await context.Response.WriteAsync(renderer.Render(), context.RequestAborted);
                });
            }

            if (dashboardActive)
            {
                endpoints.Map("/_content/Aneiang.Yarp.Dashboard/{**path}", async context =>
                {
                    var path = context.Request.RouteValues["path"]?.ToString() ?? "";
                    var filePath = $"_content/Aneiang.Yarp.Dashboard/{path}";

                    var fileInfo = context.RequestServices
                        .GetRequiredService<IWebHostEnvironment>()
                        .WebRootFileProvider
                        .GetFileInfo(filePath);

                    if (fileInfo.Exists)
                    {
                        context.Response.ContentType = GetContentType(filePath);
                        await context.Response.SendFileAsync(fileInfo);
                    }
                    else
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                    }
                });

                endpoints.MapHub<TrafficHub>($"/{routePrefix}/hubs/traffic");
                endpoints.MapHub<OverviewHub>($"/{routePrefix}/hubs/overview");
            }

            if (proxyActive)
            {
                endpoints.MapReverseProxy(proxyPipeline =>
                {
                    // Tracing sits outside metrics so its span covers the whole request, including
                    // a plugin that short-circuits before the collector ever sees a response.
                    if (useOptions.UseTracingMiddleware)
                    {
                        proxyPipeline.UseMiddleware<Aneiang.Yarp.Infrastructure.Observability.GatewayTracingMiddleware>();
                    }

                    // Metrics sit first in the proxy branch so they measure the whole
                    // request, including any plugin that later short-circuits it.
                    if (useOptions.UseMetricsCollection)
                    {
                        proxyPipeline.UseMiddleware<Infrastructure.Observability.GatewayMetricsMiddleware>();
                    }

                    if (autoUseMiddleware && useOptions.UseBuiltInProxyPipeline)
                    {
                        proxyPipeline.UseMiddleware<BuiltinTransformMiddleware>();
                        proxyPipeline.Use((context, next) => context.RequestServices
                            .GetRequiredService<PluginRuntimeDomainManager>()
                            .InvokeProxyPipelineAsync(context, next));
                    }
                    configureProxyPipeline?.Invoke(proxyPipeline);
                });
            }
        }

        return app;
    }

    private static void UseDeploymentMiddlewareIfAvailable(IApplicationBuilder app)
    {
        var services = app.ApplicationServices;
        var resolver = services.GetService<EndpointRoleResolver>();
        var deploymentOptions = services.GetService<IOptions<DeploymentOptions>>()?.Value;

        if (resolver != null && deploymentOptions != null && deploymentOptions.Mode != DeploymentMode.AllInOne)
        {
            app.UseMiddleware<EndpointRouterMiddleware>();
        }

        if (deploymentOptions?.HealthCheck.Enabled == true)
        {
            app.UseMiddleware<HealthCheckMiddleware>();
        }
    }

    private static string GetContentType(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".js" => "application/javascript",
            ".css" => "text/css",
            ".html" => "text/html",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".eot" => "application/vnd.ms-fontobject",
            _ => "application/octet-stream"
        };
    }
}
