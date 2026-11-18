using Aneiang.Yarp.Dashboard.Infrastructure.Alert;
using Aneiang.Yarp.Dashboard.Infrastructure.Observability;
using Aneiang.Yarp.Dashboard.Modules.Alerting.Models;
using Aneiang.Yarp.Dashboard.Modules.Alerting.Services;
using Aneiang.Yarp.Infrastructure.State;
using Aneiang.Yarp.Models;
using Aneiang.Yarp.Plugin.CircuitBreaker;
using Aneiang.Yarp.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// The alerting state machine, driven by a fake clock. The whole point is what happens after a
/// window closes and after a breach has persisted, neither of which should need a real sleep.
/// </summary>
public class MetricAlertServiceTests
{
    [Fact]
    public async Task A_rule_does_not_decide_until_the_window_has_been_collected()
    {
        var ctx = Create();
        await ctx.Service.SaveRuleAsync(ErrorRateRule(), "test");

        // Baseline with empty counters, then traffic, then only 15 seconds of history.
        await ctx.Service.EvaluateAsync();
        ctx.Failing(200, 20);
        ctx.Clock.Advance(TimeSpan.FromSeconds(15));
        await ctx.Service.EvaluateAsync();

        Assert.Empty(ctx.Service.GetIncidents());

        // A full window later the same traffic is enough to judge.
        ctx.Clock.Advance(TimeSpan.FromSeconds(45));
        await ctx.Service.EvaluateAsync();

        Assert.Single(ctx.Service.GetIncidents());
    }

    [Fact]
    public async Task A_breach_fires_immediately_when_no_sustain_time_is_required()
    {
        var ctx = Create();
        await ctx.Service.SaveRuleAsync(ErrorRateRule(severity: AlertSeverity.Critical), "test");

        await ctx.BreachAsync();

        var incident = Assert.Single(ctx.Service.GetIncidents());
        Assert.Equal(AlertIncidentState.Firing, incident.State);
        Assert.Equal(0.1, incident.LastValue, 3);
        Assert.Equal(200, incident.LastSamples);

        var sent = Assert.Single(ctx.Alerts.Sent);
        // Critical is delivered as "Error": the channels only recognise Error/Warning and paint
        // anything else as a green checkmark.
        Assert.Equal("Error", sent.Severity);
        Assert.Equal("MetricAlert", sent.Type);
        Assert.Contains("10%", sent.Message);
        Assert.Contains("5%", sent.Message);
    }

    [Fact]
    public async Task A_breach_must_survive_the_sustain_window_before_it_pages()
    {
        var ctx = Create();
        await ctx.Service.SaveRuleAsync(ErrorRateRule(forSeconds: 30), "test");
        await ctx.Service.EvaluateAsync();

        ctx.Failing(200, 20);
        ctx.Clock.Advance(TimeSpan.FromSeconds(60));
        await ctx.Service.EvaluateAsync();

        var pending = Assert.Single(ctx.Service.GetIncidents());
        Assert.Equal(AlertIncidentState.Pending, pending.State);
        Assert.Empty(ctx.Alerts.Sent);

        // 15s in: still short of the 30s gate.
        ctx.Clock.Advance(TimeSpan.FromSeconds(15));
        await ctx.Service.EvaluateAsync();
        Assert.Equal(AlertIncidentState.Pending, Assert.Single(ctx.Service.GetIncidents()).State);
        Assert.Empty(ctx.Alerts.Sent);

        // 30s in: now it is real.
        ctx.Clock.Advance(TimeSpan.FromSeconds(15));
        await ctx.Service.EvaluateAsync();

        var firing = Assert.Single(ctx.Service.GetIncidents());
        Assert.Equal(AlertIncidentState.Firing, firing.State);
        Assert.Single(ctx.Alerts.Sent);
    }

    [Fact]
    public async Task A_breach_that_recovers_before_it_fires_is_closed_without_a_notification()
    {
        var ctx = Create();
        await ctx.Service.SaveRuleAsync(ErrorRateRule(forSeconds: 300), "test");
        await ctx.Service.EvaluateAsync();

        ctx.Failing(200, 20);
        ctx.Clock.Advance(TimeSpan.FromSeconds(60));
        await ctx.Service.EvaluateAsync();
        Assert.Equal(AlertIncidentState.Pending, Assert.Single(ctx.Service.GetIncidents()).State);

        ctx.Healthy(2_000);
        ctx.Clock.Advance(TimeSpan.FromSeconds(60));
        await ctx.Service.EvaluateAsync();

        var incident = Assert.Single(ctx.Service.GetIncidents());
        Assert.Equal(AlertIncidentState.Resolved, incident.State);
        Assert.Equal("recovered", incident.ResolveReason);

        // Nobody was ever told it started, so nobody should be told it stopped.
        Assert.Empty(ctx.Alerts.Sent);
    }

    [Fact]
    public async Task Recovery_after_firing_sends_exactly_one_resolved_notification()
    {
        var ctx = Create();
        await ctx.Service.SaveRuleAsync(ErrorRateRule(), "test");
        await ctx.BreachAsync();
        Assert.Single(ctx.Alerts.Sent);

        ctx.Healthy(2_000);
        ctx.Clock.Advance(TimeSpan.FromSeconds(60));
        await ctx.Service.EvaluateAsync();

        Assert.Equal(2, ctx.Alerts.Sent.Count);
        Assert.Equal("Info", ctx.Alerts.Sent[1].Severity);
        Assert.Contains("recovered", ctx.Alerts.Sent[1].Message);

        var incident = Assert.Single(ctx.Service.GetIncidents());
        Assert.Equal(AlertIncidentState.Resolved, incident.State);
        Assert.Equal("recovered", incident.ResolveReason);
    }

    /// <summary>
    /// The failure mode this guards against: traffic stops entirely because upstream died, the
    /// error counter stops with it, a naive rule reads "0% errors" and announces recovery at the
    /// worst possible moment.
    /// </summary>
    [Fact]
    public async Task Going_silent_holds_a_firing_incident_instead_of_resolving_it()
    {
        var ctx = Create();
        await ctx.Service.SaveRuleAsync(ErrorRateRule(), "test");
        await ctx.BreachAsync();

        // Two minutes with no traffic at all.
        ctx.Clock.Advance(TimeSpan.FromSeconds(120));
        await ctx.Service.EvaluateAsync();

        var incident = Assert.Single(ctx.Service.GetIncidents());
        Assert.Equal(AlertIncidentState.Firing, incident.State);
        Assert.Equal("lowSamples", incident.LastCode);
        Assert.Single(ctx.Alerts.Sent); // still just the original alert
    }

    [Fact]
    public async Task A_circuit_rule_fires_when_a_breaker_is_open()
    {
        var ctx = Create();
        ctx.Circuits.Add(OpenCircuit("c1", "d1"));
        await ctx.Service.SaveRuleAsync(new MetricAlertRule
        {
            Name = "breaker open",
            Kind = MetricAlertKind.CircuitOpen,
            ScopeKind = AlertScopeKind.Cluster,
            Scope = "c1",
            Threshold = 1,
            WindowSeconds = 60,
            ForSeconds = 0,
            MinSamples = 1,
            Severity = AlertSeverity.Critical
        }, "test");

        await ctx.Service.EvaluateAsync();

        var incident = Assert.Single(ctx.Service.GetIncidents());
        Assert.Equal(AlertIncidentState.Firing, incident.State);
        Assert.Equal(1, incident.LastValue);
        Assert.Single(ctx.Alerts.Sent);
    }

    [Fact]
    public async Task Muting_silences_notifications_without_freezing_the_measurement()
    {
        var ctx = Create();
        await ctx.Service.SaveRuleAsync(ErrorRateRule(), "test");
        await ctx.BreachAsync();

        var incident = Assert.Single(ctx.Service.GetIncidents());
        await ctx.Service.MuteAsync(incident.Id, 60, "test");
        var afterMute = Assert.Single(ctx.Alerts.Sent);

        // Worse traffic while muted: the numbers must keep moving even though nothing is sent.
        ctx.Failing(100, 90);
        ctx.Clock.Advance(TimeSpan.FromSeconds(60));
        await ctx.Service.EvaluateAsync();

        var updated = Assert.Single(ctx.Service.GetIncidents());
        Assert.Equal(AlertIncidentState.Firing, updated.State);
        Assert.True(updated.LastValue > incident.LastValue);
        Assert.True(updated.IsMutedAt(ctx.Clock.GetUtcNow()));
        Assert.Single(ctx.Alerts.Sent);
        Assert.Equal(afterMute, ctx.Alerts.Sent[0]);
    }

    [Fact]
    public async Task A_rule_with_re_notify_reminds_while_the_problem_persists()
    {
        var ctx = Create();
        await ctx.Service.SaveRuleAsync(ErrorRateRule(renotifyMinutes: 10), "test");
        await ctx.BreachAsync();
        Assert.Single(ctx.Alerts.Sent);

        // Keep the same 10% failure mix flowing for 22 minutes.
        for (var i = 0; i < 22; i++)
        {
            ctx.Failing(200, 20);
            ctx.Clock.Advance(TimeSpan.FromSeconds(60));
            await ctx.Service.EvaluateAsync();
        }

        Assert.True(ctx.Alerts.Sent.Count >= 3,
            $"expected at least three notifications over 22 minutes, got {ctx.Alerts.Sent.Count}");
    }

    [Fact]
    public async Task A_rule_without_re_notify_says_it_once()
    {
        var ctx = Create();
        await ctx.Service.SaveRuleAsync(ErrorRateRule(renotifyMinutes: 0), "test");
        await ctx.BreachAsync();

        for (var i = 0; i < 22; i++)
        {
            ctx.Failing(200, 20);
            ctx.Clock.Advance(TimeSpan.FromSeconds(60));
            await ctx.Service.EvaluateAsync();
        }

        Assert.Single(ctx.Alerts.Sent);
    }

    [Fact]
    public async Task Disabling_a_rule_closes_its_incident_without_paging_anyone()
    {
        var ctx = Create();
        var rule = ErrorRateRule();
        var saved = await ctx.Service.SaveRuleAsync(rule, "test");
        await ctx.BreachAsync();

        // The save must carry the id, otherwise it creates a second rule instead of updating.
        await ctx.Service.SaveRuleAsync(rule with { Id = saved.Rule!.Id, Enabled = false }, "test");
        await ctx.Service.EvaluateAsync();

        var incident = Assert.Single(ctx.Service.GetIncidents());
        Assert.Equal(AlertIncidentState.Resolved, incident.State);
        Assert.Equal("ruleDisabled", incident.ResolveReason);
        Assert.Single(ctx.Alerts.Sent); // only the original alert
    }

    [Fact]
    public async Task Deleting_a_rule_closes_its_incident()
    {
        var ctx = Create();
        var rule = ErrorRateRule();
        await ctx.Service.SaveRuleAsync(rule, "test");
        await ctx.BreachAsync();

        var saved = ctx.Service.GetRules().Single();
        await ctx.Service.DeleteRuleAsync(saved.Id, "test");

        Assert.Empty(ctx.Service.GetRules());
        var incident = Assert.Single(ctx.Service.GetIncidents());
        Assert.Equal("ruleDeleted", incident.ResolveReason);
    }

    [Fact]
    public async Task Changing_the_metric_retires_the_old_incident()
    {
        var ctx = Create();
        var rule = ErrorRateRule();
        var saved = await ctx.Service.SaveRuleAsync(rule, "test");
        await ctx.BreachAsync();

        await ctx.Service.SaveRuleAsync(
            rule with { Id = saved.Rule!.Id, Kind = MetricAlertKind.LatencyP99, Threshold = 1, MinSamples = 20 },
            "test");

        var incident = Assert.Single(ctx.Service.GetIncidents());
        Assert.Equal(AlertIncidentState.Resolved, incident.State);
        Assert.Equal("ruleChanged", incident.ResolveReason);
    }

    [Fact]
    public async Task Invalid_rules_are_refused_at_the_door()
    {
        var ctx = Create();

        var result = await ctx.Service.SaveRuleAsync(ErrorRateRule() with { Threshold = 5 }, "test");

        Assert.False(result.Success);
        Assert.Contains("ratio", result.Message);
        Assert.Empty(ctx.Service.GetRules());
    }

    [Fact]
    public async Task Rules_and_history_survive_a_restart()
    {
        var ctx = Create();
        var rule = ErrorRateRule();
        await ctx.Service.SaveRuleAsync(rule, "test");
        await ctx.BreachAsync();

        var restarted = ctx.Restart();

        Assert.Single(restarted.GetRules());

        // An incident that was open when the process died cannot be trusted any more: the counter
        // snapshot it was measured against is gone. It is closed, and says why.
        var incident = Assert.Single(restarted.GetIncidents());
        Assert.Equal(AlertIncidentState.Resolved, incident.State);
        Assert.Equal("gatewayRestarted", incident.ResolveReason);
    }

    [Fact]
    public async Task Test_notification_reports_the_current_measurement_without_opening_an_incident()
    {
        var ctx = Create();
        var saved = await ctx.Service.SaveRuleAsync(ErrorRateRule(), "test");

        var result = await ctx.Service.TestAsync(saved.Rule!.Id, "test");

        Assert.True(result.Success);
        // Verifying the channel must not fabricate an incident: it is a delivery test, not a probe.
        Assert.Empty(ctx.Service.GetIncidents());
        var sent = Assert.Single(ctx.Alerts.Sent);
        Assert.StartsWith("[TEST]", sent.Message);
    }

    #region helpers

    private static MetricAlertRule ErrorRateRule(
        int forSeconds = 0,
        int renotifyMinutes = 0,
        AlertSeverity severity = AlertSeverity.Warning) => new()
    {
        Name = "API 5xx",
        Kind = MetricAlertKind.ErrorRate,
        ScopeKind = AlertScopeKind.Cluster,
        Scope = "c1",
        Threshold = 0.05,
        WindowSeconds = 60,
        ForSeconds = forSeconds,
        MinSamples = 20,
        Severity = severity,
        RenotifyMinutes = renotifyMinutes
    };

    private static CircuitState OpenCircuit(string cluster, string destination) => new()
    {
        ClusterKeySnapshot = cluster,
        ClusterUid = "uid-" + cluster,
        DestinationKeySnapshot = destination,
        Status = CircuitStatus.Open,
        OpenedAt = DateTime.Now
    };

    private sealed class Context
    {
        // Every reference is the instance the service actually holds: a test that records traffic
        // into a second collector would assert against a gateway nobody is watching.
        public FakeClock Clock { get; init; } = null!;
        public GatewayMetricsCollector Metrics { get; init; } = null!;
        public FakeCircuitStore Circuits { get; init; } = null!;
        public RecordingAlerts Alerts { get; init; } = null!;
        public MetricAlertStore Store { get; init; } = null!;
        public MetricAlertService Service { get; init; } = null!;

        /// <summary>Records healthy traffic on the watched cluster.</summary>
        public void Healthy(int count)
        {
            for (var i = 0; i < count; i++)
                Metrics.OnRequestEnd("r1", "c1", 200, 0.01);
        }

        /// <summary>Records traffic where <paramref name="failures"/> of <paramref name="count"/> ended in 5xx.</summary>
        public void Failing(int count, int failures)
        {
            for (var i = 0; i < count; i++)
                Metrics.OnRequestEnd("r1", "c1", i < failures ? 502 : 200, 0.01);
        }

        /// <summary>Baseline, then a full 60-second window at 10% 5xx, then one evaluation.</summary>
        public async Task BreachAsync()
        {
            await Service.EvaluateAsync();
            Failing(200, 20);
            Clock.Advance(TimeSpan.FromSeconds(60));
            await Service.EvaluateAsync();
        }

        public MetricAlertService Restart()
        {
            var service = new MetricAlertService(
                Metrics,
                Circuits,
                Store,
                Alerts,
                Options.Create(new MetricAlertOptions()),
                NullLogger<MetricAlertService>.Instance,
                Clock);
            return service;
        }
    }

    private static Context Create()
    {
        var temp = Path.Combine(Path.GetTempPath(), "alert-tests", Guid.NewGuid().ToString("N"));
        var storage = Options.Create(new StorageOptions
        {
            Sqlite = new SqliteStorageOptions
            {
                ConnectionString = "Data Source=" + Path.Combine(temp, "gateway-store.db")
            }
        });

        var store = new MetricAlertStore(storage, NullLogger<MetricAlertStore>.Instance);
        var clock = new FakeClock();
        var metrics = new GatewayMetricsCollector();
        var circuits = new FakeCircuitStore();
        var alerts = new RecordingAlerts();

        var service = new MetricAlertService(
            metrics,
            circuits,
            store,
            alerts,
            Options.Create(new MetricAlertOptions()),
            NullLogger<MetricAlertService>.Instance,
            clock);

        return new Context
        {
            Clock = clock,
            Metrics = metrics,
            Circuits = circuits,
            Alerts = alerts,
            Store = store,
            Service = service
        };
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class RecordingAlerts : IGatewayAlertService
    {
        public List<(string Type, string Target, string Message, string Severity)> Sent { get; } = new();

        public void AlertCircuitBreakerOpen(string clusterId, string? destinationId = null)
            => Sent.Add(("CircuitBreakerOpen", clusterId, string.Empty, "Error"));

        public void AlertRetryExhausted(string clusterId, string routeId, int attempts, int statusCode)
            => Sent.Add(("RetryExhausted", clusterId, string.Empty, "Error"));

        public void AlertWafBlock(string clientIp, string blockReason, string? uri = null)
            => Sent.Add(("WafBlock", clientIp, string.Empty, "Warning"));

        public void AlertProxyError(string clusterId, string? destinationId, string errorMessage, string? stackTrace = null)
            => Sent.Add(("ProxyError", clusterId, errorMessage, "Error"));

        public void AlertRateLimitExceeded(string clientIp, string? routeId = null)
            => Sent.Add(("RateLimitExceeded", clientIp, string.Empty, "Warning"));

        public void AlertCustom(string alertType, string title, string message, string severity = "Info")
            => Sent.Add((alertType, title, message, severity));
    }

    /// <summary>Only the circuit snapshot is needed; everything else is inert.</summary>
    private sealed class FakeCircuitStore : ICircuitStateStore
    {
        private readonly Dictionary<string, CircuitState> _circuits = new(StringComparer.Ordinal);

        public void Add(CircuitState state)
            => _circuits[state.ClusterKeySnapshot + "|" + state.DestinationKeySnapshot] = state;

        public int Count => _circuits.Count;

        public CircuitState GetOrAdd(string key, Func<string, CircuitState> factory)
            => _circuits.TryGetValue(key, out var existing) ? existing : _circuits[key] = factory(key);

        public bool TryGet(string key, out CircuitState? state)
            => _circuits.TryGetValue(key, out state);

        public bool ContainsKey(string key) => _circuits.ContainsKey(key);

        public IReadOnlyList<KeyValuePair<string, CircuitState>> GetAll() => _circuits.ToList();

        public bool TryRemove(string key) => _circuits.Remove(key);

        public void RemoveForCluster(string clusterId, string? clusterUid) { }

        public void ResetAll()
        {
            foreach (var state in _circuits.Values)
                state.Status = CircuitStatus.Closed;
        }

        public void RenameClusterKey(string oldClusterId, string newClusterId) { }

        public void EnsureCircuitExists(string clusterId, CircuitBreakerConfig cbConfig, string? clusterUid) { }

        public bool IsCircuitOpen(string clusterId, string? destinationId = null, string? clusterUid = null)
            => _circuits.Values.Any(c => c.Status == CircuitStatus.Open);

        public bool TryAcquire(string clusterId, string destinationId, string? clusterUid = null,
            CircuitBreakerConfig? config = null) => true;

        public void RecordOutcome(string clusterId, string destinationId, bool failed,
            string? clusterUid = null, CircuitBreakerConfig? config = null) { }

        public IReadOnlyList<CircuitStateInfo> GetAllStateInfos() => Array.Empty<CircuitStateInfo>();

        public void CleanupStale(TimeSpan threshold) { }
    }

    #endregion
}
