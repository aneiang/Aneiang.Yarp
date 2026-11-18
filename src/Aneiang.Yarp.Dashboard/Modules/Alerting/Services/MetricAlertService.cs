using System.Collections.Concurrent;
using System.Globalization;
using Aneiang.Yarp.Dashboard.Infrastructure.Alert;
using Aneiang.Yarp.Dashboard.Infrastructure.Observability;
using Aneiang.Yarp.Dashboard.Modules.Alerting.Models;
using Aneiang.Yarp.Infrastructure.State;
using Aneiang.Yarp.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.Dashboard.Modules.Alerting.Services;

/// <summary>
/// The alerting loop: turn live metrics into notified incidents.
/// </summary>
/// <remarks>
/// <para>
/// Three things separate this from "if err &gt; 5% then send". First, a measurement needs a window
/// and a sample floor: one failed request is a 100% error rate, and a rate computed over an
/// unfilled window is a guess wearing a number's clothes. Second, a breach has to persist for
/// <see cref="MetricAlertRule.ForSeconds"/> before anyone is paged, so a single scrape blip is
/// not a phone call. Third, silence is not recovery — when there is no traffic to judge, the
/// incident holds in place instead of resolving itself.
/// </para>
/// <para>
/// Windows slide. Keeping a short history of counter snapshots and differencing against the
/// newest one that is at least a window old means every tick can decide, instead of a tumbling
/// window where a burst that straddles a boundary gets averaged away.
/// </para>
/// <para>
/// In-process, like the canary supervisor: the counters are per instance, so three gateway
/// instances evaluate the same rules three times, each with its own view of its own traffic.
/// </para>
/// </remarks>
public sealed class MetricAlertService : IMetricAlertService
{
    /// <summary>
    /// Event type used on the notification bus. Must also be listed in the dashboard's event
    /// catalogue, otherwise no platform can subscribe to it and every alert is delivered nowhere.
    /// </summary>
    public const string NotificationEventType = "MetricAlert";

    private readonly GatewayMetricsCollector _metrics;
    private readonly ICircuitStateStore _circuits;
    private readonly MetricAlertStore _store;
    private readonly IGatewayAlertService _alerts;
    private readonly TimeProvider _time;
    private readonly MetricAlertOptions _options;
    private readonly ILogger<MetricAlertService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Concurrent because reads (list, detail, validation) happen outside the write semaphore and
    /// must never observe a dictionary mid-mutation.
    /// </summary>
    private readonly ConcurrentDictionary<string, MetricAlertRule> _rules = new(StringComparer.Ordinal);

    private readonly List<MetricAlertIncident> _incidents = new();

    /// <summary>Ascending by time; each entry is one tick's counter snapshot.</summary>
    private readonly List<HistoryPoint> _history = new();

    public MetricAlertService(
        GatewayMetricsCollector metrics,
        ICircuitStateStore circuits,
        MetricAlertStore store,
        IGatewayAlertService alerts,
        IOptions<MetricAlertOptions> options,
        ILogger<MetricAlertService> logger,
        TimeProvider? timeProvider = null)
    {
        _metrics = metrics;
        _circuits = circuits;
        _store = store;
        _alerts = alerts;
        // Optional so tests can drive the clock and the host does not have to register one.
        _time = timeProvider ?? TimeProvider.System;
        _options = options.Value;
        _logger = logger;

        var document = _store.Load();
        foreach (var rule in document.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
                continue;
            _rules[rule.Id] = rule;
        }

        foreach (var incident in document.Incidents)
            _incidents.Add(incident);

        _incidents.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));

        // An incident that was still open when the process stopped can never be trusted again:
        // the counter snapshot it was measured against is gone, so the next comparison would read
        // the whole gap as fresh traffic. Close it and say why.
        var restartAt = _time.GetUtcNow();
        var repaired = false;
        for (var i = 0; i < _incidents.Count; i++)
        {
            if (!_incidents[i].IsActive)
                continue;

            _incidents[i] = _incidents[i] with
            {
                State = AlertIncidentState.Resolved,
                ResolvedAt = restartAt,
                ResolveReason = "gatewayRestarted"
            };
            repaired = true;
        }

        if (repaired)
            Persist();
    }

    /// <inheritdoc />
    public IReadOnlyList<MetricAlertRule> GetRules()
    {
        // Creation order, not dictionary order: the table must not reshuffle when a rule is edited.
        return _rules.Values.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<MetricAlertIncident> GetIncidents()
    {
        _gate.Wait();
        try
        {
            return _incidents.ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public MetricAlertRule? GetRule(string id)
        => _rules.TryGetValue(id, out var rule) ? rule : null;

    /// <inheritdoc />
    public async Task<MetricAlertResult> SaveRuleAsync(MetricAlertRule rule, string actor, CancellationToken ct = default)
    {
        rule = Normalize(rule, actor);

        var errors = MetricAlertRuleValidator.Validate(rule);
        if (errors.Count > 0)
            return new MetricAlertResult(false, string.Join("; ", errors), rule);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _rules[rule.Id] = rule;

            // A rule that changed metric or scope has nothing to hold over from its old incident.
            var activeIndex = _incidents.FindIndex(i =>
                i.RuleId == rule.Id && i.IsActive &&
                (i.Kind != rule.Kind || i.ScopeKind != rule.ScopeKind ||
                 !string.Equals(i.Scope, rule.Scope, StringComparison.OrdinalIgnoreCase)));

            if (activeIndex >= 0)
            {
                var now = _time.GetUtcNow();
                _incidents[activeIndex] = _incidents[activeIndex] with
                {
                    State = AlertIncidentState.Resolved,
                    ResolvedAt = now,
                    ResolveReason = "ruleChanged",
                    LastCode = "ruleChanged",
                    LastReason = "rule now watches a different metric or scope"
                };
            }

            // Force a fresh baseline: the new threshold should be judged on traffic that happened
            // after it was set, not on a window that opened under the old one.
            ResetHistory();

            Persist();
            _logger.LogInformation("Alert rule {RuleId} '{Name}' saved by {Actor}", rule.Id, rule.Name, actor);
            return new MetricAlertResult(true, $"Rule '{rule.Name}' saved", rule);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<MetricAlertResult> DeleteRuleAsync(string id, string actor, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_rules.Remove(id, out var rule))
                return new MetricAlertResult(false, $"Rule '{id}' not found");

            var now = _time.GetUtcNow();
            var index = _incidents.FindIndex(i => i.RuleId == id && i.IsActive);
            if (index >= 0)
            {
                // Silent on purpose: the operator asked for the rule to stop, so a "resolved"
                // notification would just be an echo of their own click.
                _incidents[index] = _incidents[index] with
                {
                    State = AlertIncidentState.Resolved,
                    ResolvedAt = now,
                    ResolveReason = "ruleDeleted",
                    LastCode = "ruleDeleted",
                    LastReason = "the rule was deleted"
                };
            }

            Trim();
            Persist();
            _logger.LogInformation("Alert rule {RuleId} '{Name}' deleted by {Actor}", id, rule.Name, actor);
            return new MetricAlertResult(true, $"Rule '{rule.Name}' deleted", rule);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<MetricAlertResult> MuteAsync(string incidentId, int minutes, string actor, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = _incidents.FindIndex(i => string.Equals(i.Id, incidentId, StringComparison.Ordinal));
            if (index < 0)
                return new MetricAlertResult(false, $"Incident '{incidentId}' not found");

            var clamped = Math.Clamp(minutes, 1, 10_080); // a week is the longest a mute may last
            var until = _time.GetUtcNow().AddMinutes(clamped);

            // The incident keeps being evaluated while muted — only notifications stop. Silencing
            // the measurement instead would mean the panel shows a calm gateway that is on fire.
            _incidents[index] = _incidents[index] with { MutedUntil = until };

            Persist();
            _logger.LogInformation(
                "Alert incident {IncidentId} muted for {Minutes}m by {Actor}", incidentId, clamped, actor);
            return new MetricAlertResult(true, $"Muted for {clamped} minutes", Incident: _incidents[index]);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<MetricAlertResult> TestAsync(string ruleId, string actor, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_rules.TryGetValue(ruleId, out var rule))
                return new MetricAlertResult(false, $"Rule '{ruleId}' not found");

            var sample = TakeSample(rule, _time.GetUtcNow());
            _alerts.AlertCustom(
                NotificationEventType,
                ScopeLabel(rule.ScopeKind, rule.Scope),
                $"[TEST] Rule '{rule.Name}' would report {MetricAlertEvaluator.DescribeKind(rule.Kind)} " +
                $"{MetricAlertEvaluator.Format(rule.Kind, sample.Value)} " +
                $"against a threshold of {MetricAlertEvaluator.Format(rule.Kind, rule.Threshold)} " +
                $"({sample.Samples} samples over {sample.ObservedSeconds}s). " +
                "No incident was opened; this only verifies the notification channel.",
                MapSeverity(rule.Severity));

            _logger.LogInformation("Test notification sent for alert rule {RuleId} by {Actor}", ruleId, actor);
            return new MetricAlertResult(true, "Test notification queued", rule);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task EvaluateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            RecordHistory(now);

            var changed = false;
            foreach (var rule in _rules.Values.ToList())
            {
                if (!rule.Enabled)
                {
                    changed |= CloseActiveIncident(rule, now, "ruleDisabled", "the rule is disabled");
                    continue;
                }

                var sample = TakeSample(rule, now);
                var signal = MetricAlertEvaluator.Evaluate(rule, sample);
                changed |= Apply(rule, signal, sample, now);
            }

            if (!changed)
                return;

            Trim();
            Persist();
        }
        finally
        {
            _gate.Release();
        }
    }

    #region Measurement

    /// <summary>
    /// One counter snapshot per tick, shared by every rule so their windows stay consistent with
    /// each other. Taken once here rather than per rule: three rules must not disagree about what
    /// "now" means.
    /// </summary>
    private void RecordHistory(DateTimeOffset now)
    {
        _history.Add(new HistoryPoint(now, _metrics.GetRouteCounters()));

        var keepSeconds = MaxWindowSeconds() + (_options.TickSeconds * 3);
        var cutoff = now - TimeSpan.FromSeconds(keepSeconds);
        while (_history.Count > 2 && _history[0].At < cutoff)
            _history.RemoveAt(0);
    }

    private int MaxWindowSeconds()
    {
        var max = 60;
        foreach (var rule in _rules.Values)
        {
            if (rule.Kind != MetricAlertKind.CircuitOpen && rule.WindowSeconds > max)
                max = rule.WindowSeconds;
        }

        return max;
    }

    private AlertSample TakeSample(MetricAlertRule rule, DateTimeOffset now)
    {
        if (rule.Kind == MetricAlertKind.CircuitOpen)
            return CircuitSample(rule);

        if (_history.Count == 0)
            return new AlertSample(0, 0, 0, WindowReady: false);

        var cutoff = now - TimeSpan.FromSeconds(rule.WindowSeconds);
        var current = _history[^1];

        // Newest snapshot that is at least a full window old. Older entries were already trimmed
        // to the longest configured window, so a rule that asks for more will simply wait.
        HistoryPoint? baseline = null;
        for (var i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].At <= cutoff)
            {
                baseline = _history[i];
                break;
            }
        }

        if (baseline is null || ReferenceEquals(baseline, current))
        {
            var covered = _history.Count > 1 ? (int)(current.At - _history[0].At).TotalSeconds : 0;
            return new AlertSample(0, 0, covered, WindowReady: false);
        }

        var observed = (int)(current.At - baseline.At).TotalSeconds;
        if (observed < rule.WindowSeconds * 0.8)
            return new AlertSample(0, 0, observed, WindowReady: false);

        return Aggregate(baseline.Series, current.Series, rule, observed);
    }

    private static AlertSample Aggregate(
        IReadOnlyList<RouteCounterSnapshot> baseline,
        IReadOnlyList<RouteCounterSnapshot> current,
        MetricAlertRule rule,
        int observedSeconds)
    {
        var previous = new Dictionary<string, RouteCounterSnapshot>(StringComparer.Ordinal);
        foreach (var snapshot in baseline)
            previous[SeriesKey(snapshot.Route, snapshot.Cluster)] = snapshot;

        long requests = 0;
        long failures = 0;
        long count = 0;
        var buckets = new long[GatewayMetricsCollector.BucketBounds.Length];

        foreach (var series in current)
        {
            if (!MatchesScope(series.Route, series.Cluster, rule))
                continue;

            previous.TryGetValue(SeriesKey(series.Route, series.Cluster), out var before);

            // A series missing from the baseline was created inside the window, so its whole
            // lifetime belongs to this window. A missing array (default struct) yields zero.
            var deltaRequests = series.Requests - before.Requests;
            var deltaFailures = series.Failures - before.Failures;
            var deltaCount = series.Count - before.Count;

            if (deltaRequests < 0) deltaRequests = 0;
            if (deltaFailures < 0) deltaFailures = 0;
            if (deltaCount < 0) deltaCount = 0;

            requests += deltaRequests;
            failures += deltaFailures;
            count += deltaCount;

            var baselineBuckets = before.BucketsCumulative;
            for (var i = 0; i < buckets.Length; i++)
            {
                var prior = baselineBuckets is not null && i < baselineBuckets.Length ? baselineBuckets[i] : 0;
                var delta = series.BucketsCumulative[i] - prior;
                buckets[i] += delta > 0 ? delta : 0;
            }
        }

        var value = rule.Kind switch
        {
            MetricAlertKind.ErrorRate => requests == 0 ? 0 : (double)failures / requests,
            MetricAlertKind.LatencyP99 => GatewayMetricsCollector.PercentileFromBuckets(buckets, count, 0.99),
            _ => 0
        };

        // Latency is a distribution, so its sample count is observations; a rate's is the requests
        // the ratio was computed from. Both are "how much evidence is behind this number".
        var samples = rule.Kind == MetricAlertKind.ErrorRate ? requests : count;

        return new AlertSample(value, samples, observedSeconds);
    }

    private AlertSample CircuitSample(MetricAlertRule rule)
    {
        var total = 0;
        var open = 0;

        foreach (var entry in _circuits.GetAll())
        {
            var state = entry.Value;
            if (state is null)
                continue;

            var inScope = rule.ScopeKind switch
            {
                AlertScopeKind.Global => true,
                AlertScopeKind.Cluster => ClusterMatches(state, rule.Scope),
                _ => false
            };

            if (!inScope)
                continue;

            total++;
            if (state.Status == CircuitStatus.Open)
                open++;
        }

        // Sample count here is "how many circuits exist in scope", so a cluster with no circuit
        // breaker configured reports insufficient data instead of a confident zero.
        return new AlertSample(open, total, 0);
    }

    private static bool ClusterMatches(CircuitState state, string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return false;

        return string.Equals(state.ClusterKeySnapshot, scope, StringComparison.OrdinalIgnoreCase)
               || string.Equals(state.ClusterUid, scope, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesScope(string route, string cluster, MetricAlertRule rule) => rule.ScopeKind switch
    {
        AlertScopeKind.Global => true,
        AlertScopeKind.Cluster => string.Equals(cluster, rule.Scope, StringComparison.OrdinalIgnoreCase),
        AlertScopeKind.Route => string.Equals(route, rule.Scope, StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static string SeriesKey(string route, string cluster) => route + "\u0001" + cluster;

    #endregion

    #region State machine

    /// <summary>
    /// Applies one signal to the rule's active incident. Returns true when the incident list changed
    /// in a way worth persisting.
    /// </summary>
    private bool Apply(MetricAlertRule rule, AlertSignal signal, AlertSample sample, DateTimeOffset now)
    {
        var index = _incidents.FindIndex(i => i.RuleId == rule.Id && i.IsActive);

        if (signal.Kind == AlertSignalKind.Healthy)
            return index >= 0 && Resolve(rule, index, sample, now, "recovered");

        if (signal.Kind == AlertSignalKind.InsufficientData)
        {
            if (index < 0)
                return false;

            // Hold. No data is not the same as good news: if upstream stops answering entirely,
            // the error count stops too, and resolving here would tell the operator "all clear"
            // at the exact moment everything went dark.
            _incidents[index] = _incidents[index] with
            {
                LastCode = signal.Code,
                LastReason = signal.Reason,
                LastCheckedAt = now
            };
            return true;
        }

        // Breaching.
        if (index < 0)
        {
            var incident = new MetricAlertIncident
            {
                Id = "inc_" + Guid.NewGuid().ToString("n")[..12],
                RuleId = rule.Id,
                RuleName = rule.Name,
                Kind = rule.Kind,
                ScopeKind = rule.ScopeKind,
                Scope = rule.Scope,
                Severity = rule.Severity,
                State = AlertIncidentState.Pending,
                Threshold = rule.Threshold,
                LastValue = sample.Value,
                PeakValue = sample.Value,
                LastSamples = sample.Samples,
                ObservedSeconds = sample.ObservedSeconds,
                StartedAt = now,
                LastCode = signal.Code,
                LastReason = signal.Reason,
                LastCheckedAt = now
            };

            if (rule.ForSeconds <= 0)
                incident = Promote(incident, now);

            // Insert before notifying: the notification bookkeeping (last sent, count) is written
            // back into the list entry, and an incident that is not in the list yet would silently
            // lose it.
            _incidents.Insert(0, incident);

            if (incident.State == AlertIncidentState.Firing)
                Notify(rule, incident, resolved: false);

            return true;
        }

        var active = _incidents[index];
        var updated = active with
        {
            LastValue = sample.Value,
            PeakValue = Math.Max(active.PeakValue, sample.Value),
            LastSamples = sample.Samples,
            ObservedSeconds = sample.ObservedSeconds,
            LastCode = signal.Code,
            LastReason = signal.Reason,
            LastCheckedAt = now
        };

        if (active.State == AlertIncidentState.Pending)
        {
            var breachedFor = (now - active.StartedAt).TotalSeconds;
            if (breachedFor < rule.ForSeconds)
            {
                _incidents[index] = updated;
                return true;
            }

            updated = Promote(updated, now);
            _incidents[index] = updated;
            Notify(rule, updated, resolved: false);
            return true;
        }

        // Already firing: keep the numbers fresh, and repeat the notification on the rule's cadence
        // so an alert that is still true does not go quiet after the first message.
        _incidents[index] = updated;
        if (ShouldNotifyAgain(rule, updated, now))
            Notify(rule, updated, resolved: false);

        return true;
    }

    private static MetricAlertIncident Promote(MetricAlertIncident incident, DateTimeOffset now)
        => incident with { State = AlertIncidentState.Firing, FiringAt = now };

    private bool Resolve(
        MetricAlertRule rule,
        int index,
        AlertSample sample,
        DateTimeOffset now,
        string reason)
    {
        var previous = _incidents[index];
        var resolved = previous with
        {
            State = AlertIncidentState.Resolved,
            ResolvedAt = now,
            ResolveReason = reason,
            LastValue = sample.Value,
            LastSamples = sample.Samples,
            ObservedSeconds = sample.ObservedSeconds,
            LastCode = "resolved",
            LastReason = "back within threshold",
            LastCheckedAt = now
        };

        _incidents[index] = resolved;

        // Only a firing incident owes anyone a "recovered" message. A breach that never lasted
        // long enough to notify would otherwise produce two messages for something nobody saw.
        if (previous.State == AlertIncidentState.Firing && rule.NotifyOnResolve)
            Notify(rule, resolved, resolved: true);

        return true;
    }

    private bool CloseActiveIncident(MetricAlertRule rule, DateTimeOffset now, string code, string reason)
    {
        var index = _incidents.FindIndex(i => i.RuleId == rule.Id && i.IsActive);
        if (index < 0)
            return false;

        _incidents[index] = _incidents[index] with
        {
            State = AlertIncidentState.Resolved,
            ResolvedAt = now,
            ResolveReason = code,
            LastCode = code,
            LastReason = reason,
            LastCheckedAt = now
        };

        return true;
    }

    private bool ShouldNotifyAgain(MetricAlertRule rule, MetricAlertIncident incident, DateTimeOffset now)
    {
        if (incident.IsMutedAt(now))
            return false;

        if (incident.LastNotifiedAt is null)
            return true;

        // A mute that has just run out owes a reminder even when re-notify is off: the operator
        // asked to be left alone for an hour, not to be left alone forever.
        if (incident.MutedUntil is { } until && incident.LastNotifiedAt < until && now >= until)
            return true;

        return rule.RenotifyMinutes > 0
               && now - incident.LastNotifiedAt.Value >= TimeSpan.FromMinutes(rule.RenotifyMinutes);
    }

    #endregion

    #region Notification

    private void Notify(MetricAlertRule rule, MetricAlertIncident incident, bool resolved)
    {
        // Muting silences notifications, not measurement: the incident keeps updating so the panel
        // still shows a gateway that is on fire while it is silenced.
        if (!resolved && incident.IsMutedAt(_time.GetUtcNow()))
            return;

        var kind = MetricAlertEvaluator.DescribeKind(rule.Kind);
        var current = MetricAlertEvaluator.Format(rule.Kind, incident.LastValue);
        var threshold = MetricAlertEvaluator.Format(rule.Kind, rule.Threshold);
        var scope = ScopeLabel(rule.ScopeKind, rule.Scope);

        string message;
        if (resolved)
        {
            var duration = Describe(incident.Elapsed(incident.ResolvedAt ?? _time.GetUtcNow()));
            var peak = MetricAlertEvaluator.Format(rule.Kind, incident.PeakValue);
            message = string.Create(
                CultureInfo.InvariantCulture,
                $"Rule '{rule.Name}' recovered after {duration}. {kind} is now {current} (threshold {threshold}), {incident.LastSamples} samples over {incident.ObservedSeconds}s, peak was {peak}. Scope: {scope}.");
        }
        else
        {
            var sustained = incident.FiringAt is { } firedAt
                ? (long)((_time.GetUtcNow() - firedAt).TotalSeconds)
                : 0;
            var sustainedText = sustained > 0
                ? ", sustained " + Describe(TimeSpan.FromSeconds(sustained))
                : string.Empty;
            var peak = MetricAlertEvaluator.Format(rule.Kind, incident.PeakValue);

            message = string.Create(
                CultureInfo.InvariantCulture,
                $"Rule '{rule.Name}' breached: {kind} {current} past {threshold}, {incident.LastSamples} samples over {incident.ObservedSeconds}s{sustainedText}. Scope: {scope}. Peak {peak}. Incident {incident.Id}.");
        }

        var severity = resolved ? "Info" : MapSeverity(rule.Severity);

        try
        {
            _alerts.AlertCustom(NotificationEventType, scope, message, severity);
        }
        catch (Exception ex)
        {
            // A broken notification channel must never take the evaluation loop down with it.
            _logger.LogWarning(ex, "Failed to dispatch alert notification for rule {RuleId}", rule.Id);
        }

        // The log is the fallback channel: if no platform is subscribed to MetricAlert, this line
        // is the only place the alert exists, and staying silent about it would be dishonest.
        _logger.Log(
            resolved ? LogLevel.Information : ToLogLevel(rule.Severity),
            "Metric alert {State}: {Message}", resolved ? "resolved" : "firing", message);

        var index = _incidents.FindIndex(i => string.Equals(i.Id, incident.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            _incidents[index] = _incidents[index] with
            {
                LastNotifiedAt = _time.GetUtcNow(),
                Notifications = _incidents[index].Notifications + (resolved ? 0 : 1)
            };
        }
    }

    private static string ScopeLabel(AlertScopeKind kind, string? scope) => kind switch
    {
        AlertScopeKind.Cluster => "cluster:" + scope,
        AlertScopeKind.Route => "route:" + scope,
        _ => "gateway"
    };

    /// <summary>
    /// Folds three severities onto the three tiers the webhook channels actually render.
    /// </summary>
    /// <remarks>
    /// <c>Critical</c> becomes <c>Error</c> rather than being passed through: the platforms match on
    /// the literal strings "Error" and "Warning" and fall through to a green checkmark for anything
    /// else, so an unknown word would paint a page-worthy alert as good news.
    /// </remarks>
    private static string MapSeverity(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => "Error",
        AlertSeverity.Warning => "Warning",
        _ => "Info"
    };

    private static LogLevel ToLogLevel(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => LogLevel.Error,
        AlertSeverity.Warning => LogLevel.Warning,
        _ => LogLevel.Information
    };

    private static string Describe(TimeSpan span)
    {
        if (span.TotalSeconds < 60)
            return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalSeconds}s");
        if (span.TotalMinutes < 60)
            return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}m {span.Seconds}s");
        return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}h {span.Minutes}m");
    }

    #endregion

    #region Housekeeping

    private MetricAlertRule Normalize(MetricAlertRule rule, string actor)
    {
        var now = _time.GetUtcNow();
        var isNew = string.IsNullOrWhiteSpace(rule.Id) || !_rules.ContainsKey(rule.Id);
        var scope = rule.ScopeKind == AlertScopeKind.Global ? null : rule.Scope?.Trim();

        return rule with
        {
            Id = isNew ? "rule_" + Guid.NewGuid().ToString("n")[..12] : rule.Id,
            Name = rule.Name?.Trim() ?? string.Empty,
            Scope = string.IsNullOrEmpty(scope) ? null : scope,
            CreatedAt = isNew || rule.CreatedAt == default ? now : rule.CreatedAt,
            CreatedBy = isNew || string.IsNullOrEmpty(rule.CreatedBy) ? actor : rule.CreatedBy
        };
    }

    private void ResetHistory() => _history.Clear();

    private void Trim()
    {
        var resolvedBudget = Math.Max(0, _options.MaxIncidents - _incidents.Count(i => i.IsActive));
        var resolvedSeen = 0;

        // Active incidents are never dropped; they are the thing the panel exists to show.
        for (var i = 0; i < _incidents.Count; i++)
        {
            if (_incidents[i].IsActive)
                continue;

            resolvedSeen++;
            if (resolvedSeen <= resolvedBudget)
                continue;

            _incidents.RemoveAt(i);
            i--;
        }
    }

    private void Persist()
        => _store.Save(new MetricAlertDocument
        {
            Rules = _rules.Values.ToList(),
            Incidents = _incidents.ToList()
        });

    #endregion

    private sealed record HistoryPoint(DateTimeOffset At, IReadOnlyList<RouteCounterSnapshot> Series);
}
