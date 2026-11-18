using Aneiang.Yarp.Dashboard.Infrastructure.Observability;
using Aneiang.Yarp.Dashboard.Modules.Canary.Models;
using Aneiang.Yarp.Dashboard.Modules.Canary.Services;
using Aneiang.Yarp.Services;
using Aneiang.Yarp.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// End-to-end behaviour of the rollout state machine, driven by a fake clock instead of real
/// waiting — the point of the feature is what happens <em>after</em> the window closes, and that
/// should be testable without a two-minute sleep.
/// </summary>
public class CanaryRolloutServiceTests
{
    [Fact]
    public async Task Start_SwitchesPolicyToWeightedAndAppliesFirstStep()
    {
        var ctx = Create();

        var result = await ctx.Service.StartAsync(Request(canary: "d2", steps: new() { 10, 100 }));

        Assert.True(result.Success, result.Message);

        var cluster = ctx.Config.GetCluster("c1")!;
        Assert.Equal(WeightedLoadBalancingPolicy.PolicyName, cluster.LoadBalancingPolicy);
        Assert.Equal("1000", cluster.Destinations!["d2"].Metadata!["Weight"]);
        Assert.Equal("9000", cluster.Destinations!["d1"].Metadata!["Weight"]);
        Assert.Equal("RoundRobin", result.Rollout!.PreviousPolicy);
    }

    [Fact]
    public async Task Start_RejectsASecondActiveRolloutOnTheSameCluster()
    {
        var ctx = Create();
        await ctx.Service.StartAsync(Request(canary: "d2", steps: new() { 10, 100 }));

        var second = await ctx.Service.StartAsync(Request(canary: "d2", steps: new() { 20, 100 }));

        Assert.False(second.Success);
        Assert.Contains("already has rollout", second.Message);
    }

    [Fact]
    public async Task Start_RejectsEveryDestinationAsCanary()
    {
        var ctx = Create();

        var result = await ctx.Service.StartAsync(Request(canary: "d1", steps: new() { 10 } , extraCanary: "d2"));

        Assert.False(result.Success);
        Assert.Contains("baseline side", result.Message);
    }

    [Fact]
    public async Task Tick_HealthyCanary_AdvancesToTheNextStep()
    {
        var ctx = Create();
        await ctx.Service.StartAsync(Request(canary: "d2", steps: new() { 10, 100 }));

        Traffic(ctx.Metrics, canary: 60, canaryFailures: 0, baseline: 540, baselineFailures: 0);

        ctx.Clock.Advance(TimeSpan.FromSeconds(11));
        await ctx.Service.TickAsync();

        var rollout = ctx.Service.GetAll().Single();
        Assert.Equal(CanaryPhase.Observing, rollout.Phase);
        Assert.Equal(1, rollout.StepIndex);
        Assert.Equal(CanaryVerdict.Advance, rollout.LastVerdict);
        Assert.Equal("0", ctx.Config.GetCluster("c1")!.Destinations!["d1"].Metadata!["Weight"]);
    }

    [Fact]
    public async Task Tick_BadCanary_BaselineHealthy_RollsBackAndRestoresExactWeights()
    {
        var ctx = Create();
        await ctx.Service.StartAsync(Request(canary: "d2", steps: new() { 10, 50 }));

        // 60% failure on the canary, clean baseline: the only conclusion is "this release is bad".
        Traffic(ctx.Metrics, canary: 100, canaryFailures: 60, baseline: 900, baselineFailures: 4);

        ctx.Clock.Advance(TimeSpan.FromSeconds(11));
        await ctx.Service.TickAsync();

        var rollout = ctx.Service.GetAll().Single();
        Assert.Equal(CanaryPhase.RolledBack, rollout.Phase);
        Assert.Equal(CanaryReasonCodes.CanaryDegraded, rollout.LastCode);

        // d1 and d2 had no explicit weight before the rollout, so a correct rollback removes the key
        // rather than pinning both nodes to the value it happened to be.
        var cluster = ctx.Config.GetCluster("c1")!;
        Assert.False(cluster.Destinations!["d1"].Metadata?.ContainsKey("Weight") ?? false);
        Assert.False(cluster.Destinations!["d2"].Metadata?.ContainsKey("Weight") ?? false);
    }

    [Fact]
    public async Task Tick_BaselineAlsoFailing_PausesInsteadOfRollingBack()
    {
        var ctx = Create();
        await ctx.Service.StartAsync(Request(canary: "d2", steps: new() { 10, 50 }));

        Traffic(ctx.Metrics, canary: 100, canaryFailures: 55, baseline: 900, baselineFailures: 500);

        ctx.Clock.Advance(TimeSpan.FromSeconds(11));
        await ctx.Service.TickAsync();

        var rollout = ctx.Service.GetAll().Single();
        Assert.Equal(CanaryPhase.Paused, rollout.Phase);
        Assert.Equal(CanaryReasonCodes.BaselineDegraded, rollout.LastCode);

        // Weights deliberately untouched: the rollout is not implicated, only the environment is.
        Assert.Equal("1000", ctx.Config.GetCluster("c1")!.Destinations!["d2"].Metadata!["Weight"]);
    }

    [Fact]
    public async Task Tick_QuietCanary_ExtendsThenPausesWithoutAdvancing()
    {
        var ctx = Create();
        await ctx.Service.StartAsync(Request(canary: "d2", steps: new() { 10, 100 }));

        // Only the baseline sees traffic — the classic symptom of weights that never took effect.
        Traffic(ctx.Metrics, canary: 0, canaryFailures: 0, baseline: 900, baselineFailures: 0);

        for (var i = 0; i < 4; i++)
        {
            ctx.Clock.Advance(TimeSpan.FromSeconds(11));
            await ctx.Service.TickAsync();
        }

        var rollout = ctx.Service.GetAll().Single();
        Assert.Equal(CanaryPhase.Paused, rollout.Phase);
        Assert.Equal(CanaryReasonCodes.NoCanaryTraffic, rollout.LastCode);
        Assert.Equal(0, rollout.StepIndex);
    }

    [Fact]
    public async Task Abort_RestoresPolicyAndWeights()
    {
        var ctx = Create();
        var started = await ctx.Service.StartAsync(Request(canary: "d2", steps: new() { 10, 100 }));

        var aborted = await ctx.Service.AbortAsync(started.Rollout!.Id, "operator stopped it", "tester");

        Assert.True(aborted.Success, aborted.Message);
        Assert.Equal(CanaryPhase.Aborted, ctx.Service.GetAll().Single().Phase);

        var cluster = ctx.Config.GetCluster("c1")!;
        Assert.Equal("RoundRobin", cluster.LoadBalancingPolicy);
        Assert.False(cluster.Destinations!["d2"].Metadata?.ContainsKey("Weight") ?? false);
    }

    [Fact]
    public async Task Promote_ForcesTheNextStepWithoutWaiting()
    {
        var ctx = Create();
        var started = await ctx.Service.StartAsync(Request(canary: "d2", steps: new() { 10, 50 }));

        var promoted = await ctx.Service.PromoteAsync(started.Rollout!.Id, "tester");

        Assert.True(promoted.Success, promoted.Message);
        Assert.Equal(1, promoted.Rollout!.StepIndex);
        Assert.Equal(50, promoted.Rollout.CurrentPercent);
    }

    [Fact]
    public async Task Start_RejectsNonIncreasingSteps()
    {
        var ctx = Create();

        var result = await ctx.Service.StartAsync(Request(canary: "d2", steps: new() { 50, 10 }));

        Assert.False(result.Success);
        Assert.Contains("strictly increasing", result.Message);
    }

    [Fact]
    public async Task Start_FallsBackToTaggedDestinations()
    {
        var ctx = Create();
        ctx.Config.Clusters[0] = ctx.Config.Clusters[0] with
        {
            Destinations = ctx.Config.Clusters[0].Destinations!.ToDictionary(
                d => d.Key,
                d => d.Key == "d2"
                    ? new DestinationConfig
                    {
                        Address = d.Value.Address,
                        Metadata = new Dictionary<string, string> { ["Tag"] = "canary" }
                    }
                    : d.Value)
        };

        var request = Request(canary: null, steps: new() { 10, 100 });

        var result = await ctx.Service.StartAsync(request);

        Assert.True(result.Success, result.Message);
        Assert.Equal(new[] { "d2" }, result.Rollout!.CanaryDestinations);
    }

    #region Harness

    private static void Traffic(
        GatewayMetricsCollector metrics,
        int canary,
        int canaryFailures,
        int baseline,
        int baselineFailures)
    {
        for (var i = 0; i < canary; i++)
            metrics.OnDestinationEnd("c1", "d2", i < canaryFailures ? 502 : 200);

        for (var i = 0; i < baseline; i++)
            metrics.OnDestinationEnd("c1", "d1", i < baselineFailures ? 502 : 200);
    }

    private static CanaryStartRequest Request(string? canary, List<int> steps, string? extraCanary = null)
    {
        var destinations = new List<string>();
        if (canary is not null) destinations.Add(canary);
        if (extraCanary is not null) destinations.Add(extraCanary);

        return new CanaryStartRequest
        {
            ClusterId = "c1",
            CanaryDestinations = destinations,
            Steps = steps,
            ObservationWindowSeconds = 10,
            MinSamples = 10,
            Reason = "unit test"
        };
    }

    private static Context Create()
    {
        var config = new FakeDynamicConfig();
        config.Clusters.Add(new ClusterConfig
        {
            ClusterId = "c1",
            LoadBalancingPolicy = "RoundRobin",
            Destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
            {
                ["d1"] = new() { Address = "http://d1" },
                ["d2"] = new() { Address = "http://d2" }
            }
        });

        var metrics = new GatewayMetricsCollector();
        var clock = new FakeClock();

        var tempDb = Path.Combine(Path.GetTempPath(), "canary-tests", Guid.NewGuid().ToString("N"), "gateway-store.db");
        var storage = Options.Create(new StorageOptions
        {
            Sqlite = new SqliteStorageOptions { ConnectionString = "Data Source=" + tempDb }
        });

        var ledger = new CanaryLedgerStore(storage, NullLogger<CanaryLedgerStore>.Instance);

        var service = new CanaryRolloutService(
            config,
            metrics,
            ledger,
            Options.Create(new CanaryOptions { MaxWindowExtensions = 3 }),
            NullLogger<CanaryRolloutService>.Instance,
            notifications: null,
            timeProvider: clock);

        return new Context(service, config, metrics, clock);
    }

    private sealed record Context(
        CanaryRolloutService Service,
        FakeDynamicConfig Config,
        GatewayMetricsCollector Metrics,
        FakeClock Clock);

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    #endregion
}
