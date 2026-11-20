using Aneiang.Yarp.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace AneiangYarpPlugin;

/// <summary>
/// Gateway plugin loaded out-of-tree by the host's plugin loader.
/// </summary>
/// <remarks>
/// <para>
/// This sample stamps a response header, which is the smallest thing that proves two separate
/// facts: the plugin is in the proxy pipeline, and the value it writes comes from its binding
/// configuration. Replace <see cref="AneiangYarpPluginMiddleware"/> with what your plugin does.
/// </para>
/// <para>
/// <b>The manifest is not declared here.</b> For an out-of-tree plugin the host reads
/// <c>plugin.json</c> during discovery, before any assembly is loaded - that copy is what the
/// Plugin Center shows and what bindings are validated against. Declaring a second manifest in this
/// class would give you two places to edit (and one of them silently ignored), so identity and
/// settings live in <c>plugin.manifest.json</c> and <c>.csproj</c> only. A plugin compiled into the
/// host itself does need to override <see cref="IGatewayPlugin.Manifest"/>; this template is not
/// that shape.
/// </para>
/// <para>
/// Four things about the contract are worth knowing before you go further:
/// </para>
/// <list type="bullet">
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
/// <item><description>
/// Configuration is read per request from the gateway snapshot, not passed in at startup. That is
/// what makes editing a binding take effect without reloading the plugin; see
/// <see cref="AneiangYarpPluginMiddleware"/>.
/// </description></item>
/// </list>
/// </remarks>
public sealed class AneiangYarpPluginEntry : IGatewayPlugin
{
    /// <summary>
    /// Plugin id. Written once: the template substitutes this literal, the middleware compares
    /// bindings against it, and <c>.csproj</c> renders it into <c>plugin.json</c>.
    /// </summary>
    public const string Id = "aneiang-yarp-plugin";

    /// <inheritdoc />
    public string PluginId => Id;

    /// <inheritdoc />
    public string DisplayName => "AneiangYarpPlugin";

    /// <inheritdoc />
    public string Version => "1.0.0";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, object? pluginOptions = null)
    {
        // Register what the middleware below needs: options, caches, typed clients.
    }

    /// <inheritdoc />
    public void ConfigureMiddleware(IApplicationBuilder app)
    {
        // Reserved: the host only applies this for the built-in WAF. Use ConfigureProxyPipeline.
    }

    /// <inheritdoc />
    public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline)
    {
        // Runs for every request that matched a route, in manifest "order", wrapped in a trace span
        // named after this plugin. The delegate already receives the real HttpContext — YARP hands
        // it down through the pipeline rather than through a DefaultHttpContext adapter.
        proxyPipeline.UseMiddleware<AneiangYarpPluginMiddleware>();
    }

    /// <inheritdoc />
    public void ConfigureDashboard(IPluginDashboardBuilder builder)
    {
        // builder.AddNavItem(new PluginNavItem("my-page", "menu.myPage", "/my-page", "bi-puzzle"));
        // builder.AddWidget(new PluginDashboardWidget("my-card", "widget.myCard", "~/Views/Shared/_MyCard.cshtml"));
        // Dashboard contributions also need PluginCapability.Dashboard in the manifest.
    }
}
