using Aneiang.Yarp.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.LoadBalancing;
using Yarp.ReverseProxy.Model;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Covers the host-registration escape hatch. The point of the interface is that a policy registered
/// from <see cref="IGatewayPlugin.ConfigureServices"/> is never seen by YARP, so these tests assert on
/// the host collection rather than on behaviour.
/// </summary>
public class PluginHostServiceTests
{
    [Fact]
    public void Contributor_RegistersItsPolicyInTheHostCollection()
    {
        var services = new ServiceCollection();

        services.AddPluginHostServices(new[] { typeof(PolicyContributingPlugin) });

        Assert.Contains(services, d =>
            d.ServiceType == typeof(ILoadBalancingPolicy) &&
            d.ImplementationType == typeof(StubLoadBalancingPolicy));
    }

    [Fact]
    public void PluginWithoutTheInterface_IsIgnored()
    {
        var services = new ServiceCollection();
        var before = services.Count;

        services.AddPluginHostServices(new[] { typeof(PlainPlugin) });

        Assert.Equal(before, services.Count);
    }

    [Fact]
    public void NonPluginTypes_AreIgnored()
    {
        var services = new ServiceCollection();
        var before = services.Count;

        services.AddPluginHostServices(new[] { typeof(string), typeof(Uri) });

        Assert.Equal(before, services.Count);
    }

    [Fact]
    public void AbstractContributor_IsIgnored()
    {
        var services = new ServiceCollection();
        var before = services.Count;

        services.AddPluginHostServices(new[] { typeof(AbstractContributor) });

        Assert.Equal(before, services.Count);
    }

    [Fact]
    public void ContributorWithoutParameterlessConstructor_FailsLoudly()
    {
        // Silently skipping would leave the cluster pointing at a policy that does not exist,
        // which only shows up as a runtime failure for whoever enabled the plugin.
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddPluginHostServices(new[] { typeof(NeedsDependenciesPlugin) }));

        Assert.Contains(nameof(IGatewayPluginHostServices), error.Message);
    }

    [Fact]
    public void DuplicateTypes_AreInstantiatedOnce()
    {
        var services = new ServiceCollection();
        PolicyContributingPlugin.ConstructionCount = 0;

        services.AddPluginHostServices(new[] { typeof(PolicyContributingPlugin), typeof(PolicyContributingPlugin) });

        Assert.Equal(1, PolicyContributingPlugin.ConstructionCount);
    }

    [Theory]
    [InlineData(PluginCapability.LoadBalancingPolicy, true)]
    [InlineData(PluginCapability.Transform, true)]
    [InlineData(PluginCapability.ProxyPipeline, false)]
    [InlineData(PluginCapability.RequestMiddleware, false)]
    [InlineData(PluginCapability.Dashboard, false)]
    [InlineData(PluginCapability.BackgroundService, false)]
    public void Capabilities_ClassifyHostScopedOnes(PluginCapability capability, bool expected)
    {
        Assert.Equal(expected, PluginCapabilities.RequiresHostRegistration(capability));
    }

    [Fact]
    public void GetHostScoped_ReturnsOnlyHostScopedCapabilities()
    {
        var scoped = PluginCapabilities.GetHostScoped(new[]
        {
            PluginCapability.ProxyPipeline,
            PluginCapability.LoadBalancingPolicy,
            PluginCapability.Transform
        });

        Assert.Equal(2, scoped.Count);
        Assert.Contains(PluginCapability.LoadBalancingPolicy, scoped);
        Assert.Contains(PluginCapability.Transform, scoped);
    }

    private sealed class StubLoadBalancingPolicy : ILoadBalancingPolicy
    {
        public string Name => "Stub";

        public DestinationState? PickDestination(
            Microsoft.AspNetCore.Http.HttpContext context,
            ClusterState cluster,
            IReadOnlyList<DestinationState> availableDestinations)
            => availableDestinations.FirstOrDefault();
    }

    private sealed class PolicyContributingPlugin : IGatewayPlugin, IGatewayPluginHostServices
    {
        public static int ConstructionCount;

        public PolicyContributingPlugin() => Interlocked.Increment(ref ConstructionCount);

        public string PluginId => "policy-plugin";
        public string DisplayName => "Policy plugin";
        public string Version => "1.0";

        public PluginManifest Manifest => new(
            PluginId,
            DisplayName,
            Version,
            Array.Empty<PluginScope>(),
            new[] { PluginCapability.LoadBalancingPolicy },
            0,
            new PluginResourceRequirements(),
            Array.Empty<PluginSchemaReference>());

        public void ConfigureHostServices(IServiceCollection services)
            => services.AddSingleton<ILoadBalancingPolicy, StubLoadBalancingPolicy>();

        public void ConfigureServices(IServiceCollection services, object? pluginOptions = null) { }
        public void ConfigureMiddleware(IApplicationBuilder app) { }
        public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline) { }
    }

    private sealed class PlainPlugin : IGatewayPlugin
    {
        public string PluginId => "plain";
        public string DisplayName => "Plain";
        public string Version => "1.0";

        public void ConfigureServices(IServiceCollection services, object? pluginOptions = null) { }
        public void ConfigureMiddleware(IApplicationBuilder app) { }
        public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline) { }
    }

    private abstract class AbstractContributor : IGatewayPlugin, IGatewayPluginHostServices
    {
        public abstract string PluginId { get; }
        public string DisplayName => "Abstract";
        public string Version => "1.0";

        public abstract void ConfigureHostServices(IServiceCollection services);

        public void ConfigureServices(IServiceCollection services, object? pluginOptions = null) { }
        public void ConfigureMiddleware(IApplicationBuilder app) { }
        public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline) { }
    }

    private sealed class NeedsDependenciesPlugin : IGatewayPlugin, IGatewayPluginHostServices
    {
        public NeedsDependenciesPlugin(IServiceProvider services) => _ = services;

        public string PluginId => "needs-deps";
        public string DisplayName => "Needs deps";
        public string Version => "1.0";

        public void ConfigureHostServices(IServiceCollection services) { }

        public void ConfigureServices(IServiceCollection services, object? pluginOptions = null) { }
        public void ConfigureMiddleware(IApplicationBuilder app) { }
        public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline) { }
    }
}
