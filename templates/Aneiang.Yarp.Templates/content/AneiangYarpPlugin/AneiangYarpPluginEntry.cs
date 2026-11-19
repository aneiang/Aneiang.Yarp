using Aneiang.Yarp.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AneiangYarpPlugin;

/// <summary>
/// Gateway plugin loaded out-of-tree by the host's plugin loader.
/// </summary>
/// <remarks>
/// <para>
/// This sample stamps a response header, which is the smallest thing that proves the plugin is in
/// the pipeline. Replace the middleware below with what your plugin actually does.
/// </para>
/// <para>
/// Three things about the contract are worth knowing before you go further:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="Manifest"/> is metadata only. The host reads it during discovery without loading your
/// assembly, which is how the dashboard can list a plugin that is installed but disabled.
/// </description></item>
/// <item><description>
/// Middleware goes in <see cref="ConfigureProxyPipeline"/>, <em>not</em> in
/// <see cref="ConfigureMiddleware"/>. The app-level pipeline is built once at startup; plugins are
/// enabled and reloaded afterwards, so an app-level middleware cannot be added later. The host only
/// honours <see cref="ConfigureMiddleware"/> for the built-in WAF — implementing it and waiting for
/// it to run is a silent no-op.
/// </description></item>
/// <item><description>
/// <see cref="ConfigureServices"/> runs against a collection that is built <em>after</em> the host
/// container is frozen. Middleware, options and caches belong there; anything YARP resolves by
/// enumeration while it starts up (load-balancing policies, transform providers) does not — those
/// need <see cref="IGatewayPluginHostServices"/>, and a policy registered in the wrong place is
/// silently never used.
/// </description></item>
/// <item><description>
/// The plugin needs a public parameterless constructor. The loader instantiates it directly, and it
/// checks that <see cref="PluginId"/> matches the manifest id — a mismatch is refused at load time.
/// </description></item>
/// </list>
/// </remarks>
public sealed class AneiangYarpPluginEntry : IGatewayPlugin
{
    public string PluginId => "aneiang-yarp-plugin";

    public string DisplayName => "AneiangYarpPlugin";

    public string Version => "1.0.0";

    public PluginManifest Manifest => new(
        PluginId,
        DisplayName,
        Version,
        [PluginScope.Route],
        [PluginCapability.ProxyPipeline],
        750,
        new PluginResourceRequirements(RequestMiddleware: true),
        [],
        "Adds a response header so you can see the plugin is in the pipeline.");

    public void ConfigureServices(IServiceCollection services, object? pluginOptions = null)
    {
        // Register what the middleware below needs: options, caches, typed clients.
    }

    public void ConfigureMiddleware(IApplicationBuilder app)
    {
        // Reserved: the host only applies this for the built-in WAF. Use ConfigureProxyPipeline.
    }

    public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline)
    {
        // Runs for every request that matched a route, in manifest "order", wrapped in a trace span
        // named after this plugin. The delegate already receives the real HttpContext — YARP hands
        // it down through the pipeline rather than through a DefaultHttpContext adapter.
        proxyPipeline.Use(async (context, next) =>
        {
            // OnStarting runs before the first byte is written. Writing a header afterwards either
            // throws or is silently dropped, depending on how far the response has already gone.
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-Plugin-Id"] = PluginId;
                return Task.CompletedTask;
            });

            await next();
        });
    }

    public void ConfigureDashboard(IPluginDashboardBuilder builder)
    {
        // builder.AddNavItem(new PluginNavItem("my-page", "menu.myPage", "/my-page", "bi-puzzle"));
        // builder.AddWidget(new PluginDashboardWidget("my-card", "widget.myCard", "~/Views/Shared/_MyCard.cshtml"));
        // Dashboard contributions also need PluginCapability.Dashboard in the manifest.
    }
}
