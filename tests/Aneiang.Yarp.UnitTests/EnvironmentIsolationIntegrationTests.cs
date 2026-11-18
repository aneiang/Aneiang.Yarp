using Aneiang.Yarp.Dashboard.Modules.GatewayConfig.Services;
using Aneiang.Yarp.Extensions;
using Aneiang.Yarp.Models;
using Aneiang.Yarp.Services;
using Aneiang.Yarp.Storage.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Yarp.ReverseProxy.Configuration;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// End-to-end isolation checks over a real container and real SQLite files.
/// </summary>
/// <remarks>
/// These exist because the failure they guard against is invisible in unit tests of the pieces:
/// the working set, the stores and the published snapshot are three separate things, and the
/// dangerous bug is "all three are individually fine but editing one environment changed live
/// traffic". Only wiring them together shows that.
/// </remarks>
public class EnvironmentIsolationIntegrationTests : IDisposable
{
    private readonly string _root;
    private readonly ServiceProvider _provider;
    private readonly IDynamicYarpConfigService _config;
    private readonly AneiangProxyConfigProvider _snapshot;

    public EnvironmentIsolationIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "yarp-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var storePath = Path.Combine(_root, "gateway-store.db").Replace("\\", "/");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Storage:Sqlite:ConnectionString"] = $"Data Source={storePath}",
                ["Gateway:Storage:Environments:Runtime"] = "prod",
                ["Gateway:Storage:Environments:Names:0"] = "prod",
                ["Gateway:Storage:Environments:Names:1"] = "dev"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddAneiangYarp();
        services.AddAneiangStorage();

        // Registered by the dashboard package in a real host; the config service needs it.
        services.AddSingleton<IConfigChangeAuditLog, ConfigChangeAuditLog>();

        _provider = services.BuildServiceProvider();

        _config = _provider.GetRequiredService<IDynamicYarpConfigService>();
        _snapshot = _provider.GetRequiredService<AneiangProxyConfigProvider>();

        ((IHostedService)_provider.GetRequiredService<DynamicYarpConfigService>())
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _provider.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string ProdStore => Path.Combine(_root, "gateway-store.db");
    private string DevStore => Path.Combine(_root, "gateway-store.dev.db");

    [Fact]
    public async Task Switching_to_a_non_runtime_environment_does_not_publish_or_forward_its_config()
    {
        var result = await _config.SwitchEnvironmentAsync("dev");

        Assert.False(result.Published);
        Assert.Equal("dev", result.CurrentEnvironment);
        Assert.Equal("prod", result.RuntimeEnvironment);

        // Add a route while editing dev.
        var add = await _config.TryAddRoute(new RegisterRouteRequest
        {
            RouteName = "dev-only",
            ClusterName = "dev-cluster",
            MatchPath = "/dev-only/{**catchAll}",
            DestinationAddress = "http://127.0.0.1:59999"
        });
        Assert.True(add.Success, add.Message);

        // The working set has it...
        Assert.Contains(_config.GetRoutes(), r => r.RouteId == "dev-only");

        // ...but the published snapshot — i.e. what actually gets forwarded — does not.
        Assert.DoesNotContain(_snapshot.GetConfig().Routes ?? Array.Empty<RouteConfig>(),
            r => r.RouteId == "dev-only");
    }

    [Fact]
    public async Task Each_environment_writes_to_its_own_store()
    {
        await _config.SwitchEnvironmentAsync("dev");
        await _config.TryAddRoute(new RegisterRouteRequest
        {
            RouteName = "dev-only",
            ClusterName = "dev-cluster",
            MatchPath = "/dev-only/{**catchAll}",
            DestinationAddress = "http://127.0.0.1:59999"
        });

        Assert.True(File.Exists(DevStore), "the dev store should have been created");
        Assert.True(File.Exists(ProdStore), "the runtime store must not be replaced by the dev one");

        // Switching back loads the runtime store again: the dev route is gone from the work set.
        var back = await _config.SwitchEnvironmentAsync("prod");

        Assert.True(back.Published);
        Assert.DoesNotContain(_config.GetRoutes(), r => r.RouteId == "dev-only");
    }

    [Fact]
    public async Task Switching_back_to_the_runtime_environment_restores_what_is_served()
    {
        var before = _snapshot.GetConfig().Routes?.Count ?? 0;

        await _config.SwitchEnvironmentAsync("dev");
        var afterSwitch = _snapshot.GetConfig().Routes?.Count ?? 0;

        await _config.SwitchEnvironmentAsync("prod");
        var afterReturn = _snapshot.GetConfig().Routes?.Count ?? 0;

        // Switching the editing view never changes what is served — in either direction.
        Assert.Equal(before, afterSwitch);
        Assert.Equal(before, afterReturn);
    }

    [Fact]
    public async Task Environment_name_that_could_escape_the_store_directory_is_refused_before_any_write()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _config.SwitchEnvironmentAsync("../prod"));
    }
}
