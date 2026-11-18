using System.Collections.Concurrent;
using Aneiang.Yarp.Models;
using Aneiang.Yarp.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.LoadBalancing;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// End-to-end load balancing checks: a real Kestrel proxy talking to two real Kestrel backends.
/// <para>
/// The unit tests around <see cref="WeightedLoadBalancingPolicy"/> prove the arithmetic of
/// <c>PickDestination</c>, but they cannot prove that YARP actually resolves the policy by name and
/// hands it a usable destination list. That plumbing — <c>ClusterConfig.LoadBalancingPolicy</c> in,
/// per-destination routing out — is exactly where a cluster can end up silently falling back to the
/// built-in default, so it needs a real pipeline to test.
/// </para>
/// <para>
/// The tests are deliberately split between "policy present at startup" and "policy changed after
/// startup". The gateway never has its clusters at startup: every cluster a user creates arrives
/// through a hot reload, which is a completely different code path inside YARP.
/// </para>
/// </summary>
public sealed class LoadBalancingIntegrationTests
{
    [Fact]
    public async Task Weighted_policy_splits_traffic_by_destination_weight()
    {
        await using var harness = await ProxyHarness.CreateAsync(policy: WeightedLoadBalancingPolicy.PolicyName,
            weights: new Dictionary<string, string> { ["a"] = "1000", ["b"] = "9000" });

        var counts = await harness.FireAsync("/probe", total: 100);

        // 10% / 90% over exactly 100 requests. Smooth WRR is exact over a full cycle, so anything
        // looser than this would hide a policy that is not actually in use.
        Assert.Equal(10, counts.GetValueOrDefault("a"));
        Assert.Equal(90, counts.GetValueOrDefault("b"));
    }

    [Fact]
    public async Task Weight_zero_parks_a_destination()
    {
        await using var harness = await ProxyHarness.CreateAsync(policy: WeightedLoadBalancingPolicy.PolicyName,
            weights: new Dictionary<string, string> { ["a"] = "0", ["b"] = "1" });

        var counts = await harness.FireAsync("/probe", total: 20);

        // Weight 0 means "registered but receives nothing" — the state a baseline is parked in
        // once a rollout reaches 100%.
        Assert.Equal(0, counts.GetValueOrDefault("a"));
        Assert.Equal(20, counts.GetValueOrDefault("b"));
    }

    [Fact]
    public async Task Deterministic_policy_proves_the_name_reaches_yarp()
    {
        // FirstAlphabetical always returns the first available destination. If traffic is spread,
        // the cluster's LoadBalancingPolicy never reached YARP and the default was used instead.
        await using var harness = await ProxyHarness.CreateAsync(policy: "FirstAlphabetical");

        var counts = await harness.FireAsync("/probe", total: 20);

        Assert.Single(counts);
        Assert.Equal(20, counts["a"]);
    }

    [Fact]
    public async Task Policy_changed_after_startup_reaches_yarp()
    {
        // Mirrors the canary flow: the cluster already exists and is serving traffic when the
        // policy changes underneath it.
        await using var harness = await ProxyHarness.CreateAsync(policy: "RoundRobin");

        var before = await harness.FireAsync("/probe", total: 10);
        Assert.Equal(2, before.Count);
        Assert.Equal(5, before["a"]);

        harness.Update(cluster => cluster with { LoadBalancingPolicy = "FirstAlphabetical" });

        await harness.WaitForAsync(async () =>
        {
            var counts = await harness.FireAsync("/probe", total: 10);
            return counts.Count == 1 && counts["a"] == 10;
        });

        var after = await harness.FireAsync("/probe", total: 10);
        Assert.Equal(10, after.GetValueOrDefault("a"));
        Assert.Equal(0, after.GetValueOrDefault("b"));
    }

    [Fact]
    public async Task Weights_changed_after_startup_reach_yarp()
    {
        await using var harness = await ProxyHarness.CreateAsync(
            policy: WeightedLoadBalancingPolicy.PolicyName,
            weights: new Dictionary<string, string> { ["a"] = "1", ["b"] = "1" });

        var before = await harness.FireAsync("/probe", total: 10);
        Assert.Equal(5, before["a"]);

        // Park "a" — the 100% step of a rollout.
        harness.Update(cluster => cluster with
        {
            Destinations = new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["a"] = WithWeight(cluster.Destinations!["a"], "0"),
                ["b"] = WithWeight(cluster.Destinations!["b"], "1")
            }
        });

        await harness.WaitForAsync(async () =>
        {
            var counts = await harness.FireAsync("/probe", total: 10);
            return counts.GetValueOrDefault("a") == 0 && counts.GetValueOrDefault("b") == 10;
        });

        var after = await harness.FireAsync("/probe", total: 10);
        Assert.Equal(0, after.GetValueOrDefault("a"));
        Assert.Equal(10, after.GetValueOrDefault("b"));
    }

    [Fact]
    public async Task No_load_balancing_warning_when_a_policy_is_configured()
    {
        // YARP logs "load balancing may not be configured correctly" when it sees a multi-destination
        // cluster with no usable policy. Asserting its absence pins down that a passing split above
        // is real routing, not a lucky fallback.
        await using var harness = await ProxyHarness.CreateAsync(policy: "FirstAlphabetical");
        await harness.FireAsync("/probe", total: 3);

        Assert.DoesNotContain(harness.Logs, x => x.Contains("load balancing may not be configured", StringComparison.Ordinal));
    }

    private static DestinationConfig WithWeight(DestinationConfig destination, string weight)
        => destination with
        {
            Metadata = new Dictionary<string, string>(destination.Metadata ?? new Dictionary<string, string>())
            {
                [WeightedLoadBalancingPolicy.DefaultWeightKey] = weight
            }
        };

    /// <summary>Two backend apps behind one in-process YARP proxy, all on ephemeral ports.</summary>
    private sealed class ProxyHarness : IAsyncDisposable
    {
        private readonly WebApplication _proxy;
        private readonly WebApplication _backendA;
        private readonly WebApplication _backendB;
        private readonly HttpClient _client;
        private readonly AneiangProxyConfigProvider _provider;
        private readonly CountingChangeListener _listener = new();
        private readonly List<RouteConfig> _routes = new();
        private ClusterConfig _cluster = null!;
        private long _version = 1;

        private ProxyHarness(
            WebApplication proxy,
            WebApplication a,
            WebApplication b,
            HttpClient client,
            AneiangProxyConfigProvider provider,
            ClusterLogSink sink,
            CountingChangeListener listener)
        {
            _proxy = proxy;
            _backendA = a;
            _backendB = b;
            _client = client;
            _provider = provider;
            _listener = listener;
            Logs = sink.Messages;
        }

        /// <summary>Every log line the proxy emitted, formatted as <c>[category] message</c>.</summary>
        public IReadOnlyList<string> Logs { get; }

        /// <summary>Number of config reloads YARP has actually applied.</summary>
        public int Reloads => _listener.Loaded;

        public static async Task<ProxyHarness> CreateAsync(
            string? policy,
            Dictionary<string, string>? weights = null)
        {
            var backendA = await StartBackendAsync("a");
            var backendB = await StartBackendAsync("b");

            var cluster = new ClusterConfig
            {
                ClusterId = "lb",
                LoadBalancingPolicy = policy,
                // Destination ids deliberately alphabetical: "a" is both the first destination and
                // the first id, so a FirstAlphabetical run is unambiguous.
                Destinations = new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["a"] = Destination(backendA, weights, "a"),
                    ["b"] = Destination(backendB, weights, "b")
                }
            };
            var route = new RouteConfig
            {
                RouteId = "probe",
                ClusterId = "lb",
                Match = new RouteMatch { Path = "/probe" }
            };

            var provider = new AneiangProxyConfigProvider(new[] { route }, new[] { cluster });

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var sink = new ClusterLogSink();
            builder.Logging.AddProvider(sink);
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Services.AddReverseProxy();
            builder.Services.AddSingleton<ILoadBalancingPolicy, WeightedLoadBalancingPolicy>();
            var listener = new CountingChangeListener();
            builder.Services.AddSingleton<IProxyConfigProvider>(provider);
            builder.Services.AddSingleton<IConfigChangeListener>(listener);

            var proxy = builder.Build();
            proxy.MapReverseProxy();
            await proxy.StartAsync();

            var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                BaseAddress = new Uri(AddressOf(proxy)),
                Timeout = TimeSpan.FromSeconds(10)
            };

            var harness = new ProxyHarness(proxy, backendA, backendB, client, provider, sink, listener);
            harness._routes.Add(route);
            harness._cluster = cluster;
            return harness;
        }

        /// <summary>Publish a new cluster definition the same way the gateway does on a config write.</summary>
        public void Update(Func<ClusterConfig, ClusterConfig> mutate)
        {
            _cluster = mutate(_cluster);
            _version++;

            var dynamicRoutes = _routes
                .Select(r => new DynamicRouteConfig { Config = r, Source = "test" })
                .ToList();
            var dynamicClusters = new List<DynamicClusterConfig>
            {
                new() { Config = _cluster, Source = "test" }
            };

            _provider.ApplyFromDynamic(dynamicRoutes, dynamicClusters, _version);
        }

        /// <summary>Re-fire requests until the assertion holds, so a slow YARP reload is not a flake.</summary>
        public async Task WaitForAsync(Func<Task<bool>> converged)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var attempts = 0;
            while (DateTime.UtcNow < deadline)
            {
                attempts++;
                if (await converged()) return;
                await Task.Delay(25);
            }
            var sample = await FireAsync("/probe", total: 10);
            Assert.Fail(
                $"Load balancing did not converge within 10s ({attempts} attempts, {Reloads} YARP reloads, " +
                $"last sample: {string.Join(", ", sample.Select(x => $"{x.Key}={x.Value}"))}). " +
                $"Logs: {string.Join(" | ", Logs)}");
        }

        /// <summary>Hit the proxied path and tally which backend served each response.</summary>
        public async Task<Dictionary<string, int>> FireAsync(string path, int total)
        {
            var counts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < total; i++)
            {
                var body = await _client.GetStringAsync(path);
                counts.AddOrUpdate(body, 1, (_, existing) => existing + 1);
            }
            return new Dictionary<string, int>(counts, StringComparer.Ordinal);
        }

        private static DestinationConfig Destination(WebApplication backend, Dictionary<string, string>? weights, string id)
            => new()
            {
                Address = AddressOf(backend),
                Metadata = weights is not null && weights.TryGetValue(id, out var weight)
                    ? new Dictionary<string, string> { [WeightedLoadBalancingPolicy.DefaultWeightKey] = weight }
                    : null
            };

        private static async Task<WebApplication> StartBackendAsync(string id)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Run(async context => await context.Response.WriteAsync(id));
            await app.StartAsync();
            return app;
        }

        private static string AddressOf(WebApplication app)
            => app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.First();

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _proxy.StopAsync();
            await _backendA.StopAsync();
            await _backendB.StopAsync();
            await _proxy.DisposeAsync();
            await _backendA.DisposeAsync();
            await _backendB.DisposeAsync();
        }
    }

    /// <summary>Counts the config reloads YARP applied, so "did it even reload" is never a guess.</summary>
    private sealed class CountingChangeListener : IConfigChangeListener
    {
        private int _loaded;

        public int Loaded => Volatile.Read(ref _loaded);

        public void ConfigurationLoadingFailed(IProxyConfigProvider configProvider, Exception exception)
        {
        }

        public void ConfigurationLoaded(IReadOnlyList<IProxyConfig> proxyConfigs)
        {
        }

        public void ConfigurationApplyingFailed(IReadOnlyList<IProxyConfig> proxyConfigs, Exception exception)
        {
        }

        public void ConfigurationApplied(IReadOnlyList<IProxyConfig> proxyConfigs) => Interlocked.Increment(ref _loaded);
    }

    /// <summary>Captures proxy log lines so tests can assert on what YARP complained about.</summary>
    private sealed class ClusterLogSink : ILoggerProvider
    {
        public List<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new SinkLogger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class SinkLogger : ILogger
        {
            private readonly ClusterLogSink _sink;
            private readonly string _category;

            public SinkLogger(ClusterLogSink sink, string category) => (_sink, _category) = (sink, category);

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Warning) return;
                lock (_sink.Messages)
                {
                    _sink.Messages.Add($"[{_category}] {formatter(state, exception)}");
                }
            }
        }
    }
}
