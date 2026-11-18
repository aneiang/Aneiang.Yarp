using Microsoft.Extensions.DependencyInjection;

namespace Aneiang.Yarp.Plugins;

/// <summary>
/// Optional companion to <see cref="IGatewayPlugin"/> for contributions that have to land in the
/// <em>host</em> container instead of the plugin's own runtime domain.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IGatewayPlugin.ConfigureServices"/> is invoked against a dedicated service collection
/// that is built when the plugin is enabled, long after the host container has been frozen. That is
/// the right place for middleware, options and caches, but it cannot be used for anything YARP
/// resolves by enumeration while it starts up — most importantly
/// <c>Yarp.ReverseProxy.LoadBalancing.ILoadBalancingPolicy</c> and
/// <c>Yarp.ReverseProxy.Transforms.ITransformProvider</c>. A policy registered there is never seen,
/// which is exactly the "configured but silently inert" failure mode this project keeps fighting.
/// </para>
/// <para>
/// Implement this interface when a plugin needs one of those slots. The host instantiates the
/// plugin once during dependency-injection setup (so the type must expose a parameterless
/// constructor) and hands it the host collection.
/// </para>
/// <example>
/// <code>
/// public class MyPolicyPlugin : IGatewayPlugin, IGatewayPluginHostServices
/// {
///     public string PluginId => "my-policy";
///     public void ConfigureHostServices(IServiceCollection services)
///         => services.AddSingleton&lt;ILoadBalancingPolicy, MyPolicy&gt;();
/// }
/// </code>
/// </example>
/// </remarks>
public interface IGatewayPluginHostServices
{
    /// <summary>Identifier of the plugin making the contribution. Used for diagnostics only.</summary>
    string PluginId { get; }

    /// <summary>Registers host-level services. Called once, before the host container is built.</summary>
    /// <param name="services">The host service collection.</param>
    void ConfigureHostServices(IServiceCollection services);
}

/// <summary>Classification helpers for <see cref="PluginCapability"/>.</summary>
public static class PluginCapabilities
{
    /// <summary>
    /// True when satisfying the capability requires <see cref="IGatewayPluginHostServices"/> rather
    /// than <see cref="IGatewayPlugin.ConfigureServices"/>.
    /// </summary>
    public static bool RequiresHostRegistration(PluginCapability capability) => capability
        is PluginCapability.LoadBalancingPolicy
        or PluginCapability.Transform;

    /// <summary>Returns the capabilities in <paramref name="capabilities"/> that need host registration.</summary>
    public static IReadOnlyList<PluginCapability> GetHostScoped(IEnumerable<PluginCapability> capabilities)
        => capabilities.Where(RequiresHostRegistration).ToArray();
}
