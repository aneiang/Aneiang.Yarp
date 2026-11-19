using Aneiang.Yarp.Dashboard.Modules.Environments;
using Aneiang.Yarp.GatewayEnvironments;
using Aneiang.Yarp.Models;
using Aneiang.Yarp.Services;
using Aneiang.Yarp.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Covers the two things a promotion has to get right: showing what it will change, and refusing to
/// apply anything other than the change that was approved.
/// </summary>
public class EnvironmentPromotionTests
{
    private const string Prod = "prod";
    private const string Staging = "staging";

    // ──────────────── diff ────────────────

    [Fact]
    public void Promotions_are_described_from_the_targets_point_of_view()
    {
        var source = Config(("api", Route("api", "api-cluster", "/api/")), ("new", Route("new", "api-cluster", "/new/")));
        var target = Config(("api", Route("api", "api-cluster", "/api/")), ("old", Route("old", "api-cluster", "/old/")));

        var diff = new EnvironmentDiffService().Compare(Staging, source, Prod, target);

        var added = Assert.Single(diff.RouteChanges, entry => entry.Kind == "added");
        var removed = Assert.Single(diff.RouteChanges, entry => entry.Kind == "removed");

        Assert.Equal("new", added.RouteId);
        Assert.Equal("old", removed.RouteId);
        Assert.False(diff.Identical);
    }

    [Fact]
    public void A_changed_field_is_reported_as_before_and_after_text()
    {
        var source = Config(("api", Route("api", "api-cluster", "/v2/")));
        var target = Config(("api", Route("api", "api-cluster", "/v1/")));

        var diff = new EnvironmentDiffService().Compare(Staging, source, Prod, target);

        var entry = Assert.Single(diff.RouteChanges);
        Assert.Equal("changed", entry.Kind);

        var path = Assert.Single(entry.Changes, change => change.Field == "path");
        Assert.Equal("/v1/", path.Before);
        Assert.Equal("/v2/", path.After);
    }

    [Fact]
    public void Destination_changes_are_broken_out()
    {
        var source = Config(Cluster("c1", ("d1", "http://10.0.0.1"), ("d2", "http://10.0.0.2")));
        var target = Config(Cluster("c1", ("d1", "http://10.0.0.9"), ("d3", "http://10.0.0.3")));

        var diff = new EnvironmentDiffService().Compare(Staging, source, Prod, target);

        var cluster = Assert.Single(diff.ClusterChanges);
        Assert.Equal(["d2"], cluster.DestinationsAdded);
        Assert.Equal(["d3"], cluster.DestinationsRemoved);
        Assert.Equal("http://10.0.0.9", Assert.Single(cluster.DestinationsChanged).Before);
        Assert.Equal("http://10.0.0.1", Assert.Single(cluster.DestinationsChanged).After);
    }

    [Fact]
    public void Identical_configurations_produce_an_empty_diff_with_a_stable_fingerprint()
    {
        var config = Config(("api", Route("api", "api-cluster", "/api/")));
        var same = Config(("api", Route("api", "api-cluster", "/api/")));

        Assert.True(new EnvironmentDiffService().Compare(Staging, config, Prod, same).Identical);
        Assert.Equal(ConfigFingerprint.Of(config, same), ConfigFingerprint.Of(config, same));
    }

    [Fact]
    public void The_fingerprint_changes_when_anything_routing_relevant_changes()
    {
        var target = Config(Cluster("c1"), ("legacy", Route("legacy", "c1", "/legacy/")));

        var before = ConfigFingerprint.Of(Config(Cluster("c1"), ("api", Route("api", "c1", "/api/"))), target);
        var after = ConfigFingerprint.Of(Config(Cluster("c1"), ("api", Route("api", "c2", "/api/"))), target);

        Assert.NotEqual(before, after);
    }

    // ──────────────── candidate validation ────────────────

    [Fact]
    public void An_empty_source_is_refused_unless_explicitly_allowed()
    {
        var empty = new GatewayDynamicConfig();

        Assert.NotEmpty(EnvironmentCandidateValidator.Validate(empty, allowEmptySource: false));
        Assert.Empty(EnvironmentCandidateValidator.Validate(empty, allowEmptySource: true));
    }

    [Fact]
    public void A_route_pointing_at_a_cluster_from_the_other_environment_is_refused()
    {
        // The classic mistake: staging's route referencing a cluster only production has. Validating
        // against the live environment would have approved this.
        var config = Config(("api", Route("api", "prod-only-cluster", "/api/")));

        var problems = EnvironmentCandidateValidator.Validate(config, allowEmptySource: false);

        Assert.Contains(problems, problem => problem.Contains("prod-only-cluster"));
    }

    [Fact]
    public void A_cluster_without_destinations_is_refused()
    {
        var config = Config(Cluster("c1"), ("api", Route("api", "c1", "/api/")));

        Assert.Contains(EnvironmentCandidateValidator.Validate(config, allowEmptySource: false),
            problem => problem.Contains("no destinations"));
    }

    [Fact]
    public void A_destination_with_a_relative_address_is_refused()
    {
        var config = Config(Cluster("c1", ("d1", "10.0.0.1:8080")));

        Assert.Contains(EnvironmentCandidateValidator.Validate(config, allowEmptySource: false),
            problem => problem.Contains("invalid address"));
    }

    [Fact]
    public void A_sound_configuration_passes()
    {
        var config = Config(
            Cluster("c1", ("d1", "http://10.0.0.1:8080")),
            ("api", Route("api", "c1", "/api/")));

        Assert.Empty(EnvironmentCandidateValidator.Validate(config, allowEmptySource: false));
    }

    // ──────────────── approval flow ────────────────

    [Fact]
    public async Task Prepare_issues_a_token_and_apply_replaces_the_target()
    {
        var store = new FakeStore();
        store.Put(Staging, Config(Cluster("c1", ("d1", "http://10.0.0.1")), ("api", Route("api", "c1", "/api/"))));
        store.Put(Prod, Config(Cluster("c1", ("prod1", "http://10.0.0.9")), ("legacy", Route("legacy", "c1", "/legacy/"))));

        var (service, reloader, audit) = Create(store);

        var prepared = await service.PrepareAsync(new PreparePromotionRequest { Source = Staging, Target = Prod });

        Assert.True(prepared.CanApply);
        Assert.True(prepared.TargetIsRuntime);
        Assert.False(prepared.Diff.Identical);
        Assert.NotEmpty(prepared.Token);

        var result = await service.ApplyAsync(prepared.Token, "127.0.0.1");

        Assert.Equal(Prod, reloader.LastReloaded);
        Assert.True(result.Published);
        Assert.Single(store.Snapshot(Prod).Routes);
        Assert.Equal("api", store.Snapshot(Prod).Routes[0].Config.RouteId);
        Assert.Single(audit.Promoted);

        // The token is spent: replaying it must not apply twice.
        await Assert.ThrowsAsync<PromotionException>(() => service.ApplyAsync(prepared.Token, null));
    }

    [Fact]
    public async Task A_configuration_that_moved_after_approval_is_refused()
    {
        var store = new FakeStore();
        store.Put(Staging, Config(Cluster("c1", ("d1", "http://10.0.0.1")), ("api", Route("api", "c1", "/api/"))));
        store.Put(Prod, Config(Cluster("c1", ("prod1", "http://10.0.0.9")), ("legacy", Route("legacy", "c1", "/legacy/"))));

        var (service, reloader, audit) = Create(store);
        var prepared = await service.PrepareAsync(new PreparePromotionRequest { Source = Staging, Target = Prod });

        // Someone edits staging in the other tab while the operator reads the diff.
        store.Put(Staging, Config(("api", Route("api", "c1", "/api/v2/"))));

        var error = await Assert.ThrowsAsync<PromotionException>(() => service.ApplyAsync(prepared.Token, null));

        Assert.Equal(409, error.StatusCode);
        Assert.Null(reloader.LastReloaded);
        Assert.Single(audit.Refused);
        Assert.Single(store.Snapshot(Prod).Routes);
    }

    [Fact]
    public async Task A_target_that_moved_after_approval_is_refused()
    {
        var store = new FakeStore();
        store.Put(Staging, Config(Cluster("c1", ("d1", "http://10.0.0.1")), ("api", Route("api", "c1", "/api/"))));
        store.Put(Prod, new GatewayDynamicConfig());

        var (service, _, _) = Create(store);
        var prepared = await service.PrepareAsync(new PreparePromotionRequest { Source = Staging, Target = Prod, AllowEmptySource = true });

        store.Put(Prod, Config(Cluster("c1", ("x", "http://10.0.0.8")), ("other", Route("other", "c1", "/other/"))));

        var error = await Assert.ThrowsAsync<PromotionException>(() => service.ApplyAsync(prepared.Token, null));

        Assert.Equal(409, error.StatusCode);
    }

    [Fact]
    public async Task An_unknown_token_is_refused()
    {
        var (service, _, _) = Create(new FakeStore());

        var error = await Assert.ThrowsAsync<PromotionException>(() => service.ApplyAsync("nope", null));

        Assert.Equal(409, error.StatusCode);
    }

    [Fact]
    public async Task Source_and_target_must_be_different_known_environments()
    {
        var (service, _, _) = Create(new FakeStore());

        await Assert.ThrowsAsync<PromotionException>(() =>
            service.PrepareAsync(new PreparePromotionRequest { Source = Prod, Target = Prod }));

        await Assert.ThrowsAsync<PromotionException>(() =>
            service.PrepareAsync(new PreparePromotionRequest { Source = "nope", Target = Prod }));

        await Assert.ThrowsAsync<PromotionException>(() =>
            service.PrepareAsync(new PreparePromotionRequest { Source = Staging, Target = "nope" }));
    }

    [Fact]
    public async Task Promotion_cannot_happen_when_the_source_is_empty()
    {
        var store = new FakeStore();
        store.Put(Staging, new GatewayDynamicConfig());
        store.Put(Prod, Config(Cluster("c1", ("d1", "http://10.0.0.1")), ("api", Route("api", "c1", "/api/"))));

        var (service, _, _) = Create(store);

        var prepared = await service.PrepareAsync(new PreparePromotionRequest { Source = Staging, Target = Prod });

        Assert.False(prepared.CanApply);
        Assert.NotEmpty(prepared.Problems);

        // Even with a token in hand, apply re-validates: a prepared-but-blocked promotion is not a
        // bypass.
        await Assert.ThrowsAsync<PromotionException>(() => service.ApplyAsync(prepared.Token, null));
    }

    // ──────────────── helpers ────────────────

    private static (EnvironmentPromotionService Service, FakeReloader Reloader, FakeAudit Audit) Create(FakeStore store)
    {
        var registry = new GatewayEnvironmentRegistry(
            Options.Create(new StorageOptions
            {
                Environments = new StorageEnvironmentOptions { Runtime = Prod, Names = [Prod, Staging] }
            }),
            NullLogger<GatewayEnvironmentRegistry>.Instance);

        var reloader = new FakeReloader();
        var audit = new FakeAudit();

        var service = new EnvironmentPromotionService(
            store, reloader, registry, new EnvironmentDiffService(), audit,
            NullLogger<EnvironmentPromotionService>.Instance);

        return (service, reloader, audit);
    }

    private static GatewayDynamicConfig Config(params (string Id, RouteConfig Route)[] routes) =>
        Build(routes, null);

    private static GatewayDynamicConfig Config(ClusterConfig cluster, params (string Id, RouteConfig Route)[] routes) =>
        Build(routes, cluster);

    private static GatewayDynamicConfig Build(
        (string Id, RouteConfig Route)[] routes, ClusterConfig? cluster)
    {
        var config = new GatewayDynamicConfig();

        foreach (var (_, route) in routes)
            config.Routes.Add(new DynamicRouteConfig { Config = route });

        if (cluster is not null)
            config.Clusters.Add(new DynamicClusterConfig { Config = cluster });

        return config;
    }

    private static RouteConfig Route(string id, string clusterId, string path) => new()
    {
        RouteId = id,
        ClusterId = clusterId,
        Match = new RouteMatch { Path = path }
    };

    private static ClusterConfig Cluster(string id, params (string Name, string Address)[] destinations) => new()
    {
        ClusterId = id,
        Destinations = destinations.ToDictionary(
            destination => destination.Name,
            destination => new DestinationConfig { Address = destination.Address })
    };

    private sealed class FakeStore : IEnvironmentConfigStore
    {
        private readonly Dictionary<string, GatewayDynamicConfig> _environments = new(StringComparer.OrdinalIgnoreCase);

        public void Put(string environment, GatewayDynamicConfig config) => _environments[environment] = config;

        public GatewayDynamicConfig Snapshot(string environment) => _environments[environment];

        public Task<GatewayDynamicConfig> ReadAsync(string environment, CancellationToken ct = default) =>
            Task.FromResult(_environments.TryGetValue(environment, out var config)
                ? config
                : throw new IOException($"Environment '{environment}' could not be read."));

        public Task ReplaceAsync(string environment, GatewayDynamicConfig configuration, CancellationToken ct = default)
        {
            _environments[environment] = configuration;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReloader : IEnvironmentReloader
    {
        public string? LastReloaded { get; private set; }

        public Task<bool> ReloadAsync(string environment, CancellationToken ct = default)
        {
            LastReloaded = environment;
            return Task.FromResult(string.Equals(environment, Prod, StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class FakeAudit : IPromotionAudit
    {
        public List<(string Source, string Target)> Promoted { get; } = [];
        public List<string> Refused { get; } = [];

        public void RecordPromoted(string source, string target, string? clientIp, object before, object after) =>
            Promoted.Add((source, target));

        public void RecordRefused(string target, string reason) => Refused.Add(reason);
    }
}
