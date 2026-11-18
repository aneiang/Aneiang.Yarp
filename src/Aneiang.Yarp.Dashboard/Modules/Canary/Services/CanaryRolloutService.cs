using Aneiang.Yarp.Dashboard.Infrastructure.Notifications;
using Aneiang.Yarp.Dashboard.Infrastructure.Observability;
using Aneiang.Yarp.Dashboard.Modules.Canary.Models;
using Aneiang.Yarp.Models;
using Aneiang.Yarp.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;

namespace Aneiang.Yarp.Dashboard.Modules.Canary.Services;

/// <summary>
/// Owns the rollout state machine and is the only thing that writes canary weights.
/// </summary>
/// <remarks>
/// <para>
/// The service deliberately does <em>not</em> own a timer: <see cref="TickAsync"/> is called by
/// <see cref="CanaryRolloutSupervisor"/> and can be called directly from a test, which is what makes
/// "wait two minutes and see whether it rolls back" a unit test instead of a manual ritual.
/// </para>
/// <para>
/// Everything is guarded by one semaphore. Rollouts are human-paced and the tick runs every few
/// seconds, so the contention is nil and the alternative — fine-grained locking around four
/// different transitions — would buy nothing but new ways to get it wrong.
/// </para>
/// </remarks>
public sealed class CanaryRolloutService : ICanaryRolloutService
{
    private const string Source = "canary";

    private readonly IDynamicYarpConfigService _config;
    private readonly GatewayMetricsCollector _metrics;
    private readonly CanaryLedgerStore _ledger;
    private readonly NotificationDispatcher? _notifications;
    private readonly CanaryOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<CanaryRolloutService> _logger;

    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly List<CanaryRollout> _rollouts;

    /// <summary>
    /// Cumulative counters captured when a window opened, per rollout. Not persisted: after a
    /// restart the delta would be meaningless, which is exactly why restored rollouts come back
    /// paused instead of resuming on their own.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, Counter>> _windowBaseline =
        new(StringComparer.Ordinal);

    public CanaryRolloutService(
        IDynamicYarpConfigService config,
        GatewayMetricsCollector metrics,
        CanaryLedgerStore ledger,
        IOptions<CanaryOptions> options,
        ILogger<CanaryRolloutService> logger,
        NotificationDispatcher? notifications = null,
        TimeProvider? timeProvider = null)
    {
        _config = config;
        _metrics = metrics;
        _ledger = ledger;
        _options = options.Value;
        _logger = logger;
        _notifications = notifications;
        _time = timeProvider ?? TimeProvider.System;

        _rollouts = _ledger.Load();

        // A rollout that was mid-window when the process died cannot be resumed safely: the window's
        // baseline counters are gone, so any error-rate computed now would be nonsense. Restore it
        // as Paused with the weights untouched and let a human decide.
        var repaired = false;
        for (var i = 0; i < _rollouts.Count; i++)
        {
            var rollout = _rollouts[i];
            if (rollout.Phase != CanaryPhase.Observing) continue;

            _rollouts[i] = rollout with
            {
                Phase = CanaryPhase.Paused,
                LastVerdict = CanaryVerdict.Pause,
                LastCode = "restarted",
                LastReason = "Gateway restarted while this rollout was being observed. Weights were left "
                             + "at the last applied step; resume to open a fresh window, or abort to restore."
            };
            repaired = true;
        }

        if (repaired) _ledger.Save(_rollouts);
    }

    /// <inheritdoc />
    public IReadOnlyList<CanaryRollout> GetAll()
    {
        var locked = _mutex.Wait(TimeSpan.FromSeconds(2));
        try
        {
            return _rollouts.OrderByDescending(r => r.StartedAt).ToList();
        }
        finally
        {
            if (locked) _mutex.Release();
        }
    }

    /// <inheritdoc />
    public CanaryRollout? Get(string id)
        => GetAll().FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public CanaryObservation? GetLiveObservation(string id)
    {
        var locked = _mutex.Wait(TimeSpan.FromSeconds(2));
        try
        {
            var rollout = FindLocked(id);
            if (rollout is null || rollout.Phase != CanaryPhase.Observing) return null;

            var cluster = _config.GetCluster(rollout.ClusterId);
            if (cluster is null) return null;

            return BuildObservation(rollout, cluster);
        }
        finally
        {
            if (locked) _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CanaryOperationResult> StartAsync(
        CanaryStartRequest request,
        string? createdBy = null,
        CancellationToken ct = default)
    {
        if (request is null) return CanaryOperationResult.Fail("Request body is required");

        var clusterId = request.ClusterId?.Trim();
        if (string.IsNullOrWhiteSpace(clusterId))
            return CanaryOperationResult.Fail("clusterId is required");

        await _mutex.WaitAsync(ct);
        try
        {
            var cluster = _config.GetCluster(clusterId!);
            if (cluster is null)
                return CanaryOperationResult.Fail($"Cluster '{clusterId}' not found");

            var running = _rollouts.FirstOrDefault(r =>
                r.IsActive && string.Equals(r.ClusterId, clusterId, StringComparison.OrdinalIgnoreCase));
            if (running is not null)
                return CanaryOperationResult.Fail(
                    $"Cluster '{clusterId}' already has rollout {running.Id} in phase {running.Phase}", running);

            if (!TryResolveCanaryDestinations(cluster, request, out var canaryIds, out var resolveError))
                return CanaryOperationResult.Fail(resolveError!);

            if (!TryNormalizeSteps(request.Steps, out var steps, out var stepError))
                return CanaryOperationResult.Fail(stepError!);

            var window = Math.Clamp(
                request.ObservationWindowSeconds ?? _options.ObservationWindowSeconds, 10, 7200);
            var threshold = Math.Clamp(
                request.ErrorRateThreshold ?? _options.ErrorRateThreshold, 0.001, 1);
            var minSamples = Math.Clamp(request.MinSamples ?? _options.MinSamples, 1, 1_000_000);

            var weightKey = CanaryWeightPlanner.ResolveWeightKey(cluster);
            var defaultWeight = CanaryWeightPlanner.ResolveDefaultWeight(cluster);
            var baseline = cluster.Destinations!.ToDictionary(
                d => d.Key,
                d => CanaryWeightPlanner.ReadWeight(d.Value, weightKey, defaultWeight),
                StringComparer.Ordinal);
            var explicitIds = cluster.Destinations!
                .Where(d => d.Value.Metadata is { Count: > 0 } md && md.ContainsKey(weightKey))
                .Select(d => d.Key)
                .ToList();

            if (!CanaryWeightPlanner.TryPlan(cluster, canaryIds, steps[0], out var weights, out var planError))
                return CanaryOperationResult.Fail(planError!);

            // Weights are only honoured by the Weighted policy. Switching it here rather than asking
            // the operator to do it first removes the single most likely reason for "I set 10% and
            // nothing happened".
            var previousPolicy = string.Equals(
                cluster.LoadBalancingPolicy, WeightedLoadBalancingPolicy.PolicyName, StringComparison.OrdinalIgnoreCase)
                ? null
                : cluster.LoadBalancingPolicy;

            var applied = await ApplyWeightsAsync(cluster, weights, createdBy, ct);
            if (!applied.Success)
                return CanaryOperationResult.Fail("Could not apply the first step: " + applied.Message);

            var now = _time.GetUtcNow();
            var rollout = new CanaryRollout
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                ClusterId = clusterId!,
                CanaryDestinations = canaryIds,
                Steps = steps,
                StepIndex = 0,
                Phase = CanaryPhase.Observing,
                BaselineWeights = baseline,
                CurrentWeights = weights,
                BaselineExplicitWeights = explicitIds,
                PreviousPolicy = previousPolicy,
                StartedAt = now,
                StepStartedAt = now,
                StepDeadline = now.AddSeconds(window),
                ObservationWindowSeconds = window,
                ErrorRateThreshold = threshold,
                MinSamples = minSamples,
                AutoAdvance = request.AutoAdvance ?? true,
                Reason = request.Reason,
                CreatedBy = createdBy
            };

            CaptureWindowBaseline(rollout);
            _rollouts.Add(rollout);
            Trim();
            _ledger.Save(_rollouts);

            _logger.LogInformation(
                "Canary rollout {RolloutId} started on cluster {ClusterId}: canary={Canary}, steps={Steps}",
                rollout.Id, rollout.ClusterId, string.Join(",", canaryIds), string.Join(",", steps));

            return new CanaryOperationResult(true,
                $"Rollout {rollout.Id} started at {steps[0]}% (window {window}s)", rollout);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CanaryOperationResult> PromoteAsync(
        string id,
        string? createdBy = null,
        CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var rollout = FindLocked(id);
            if (rollout is null) return CanaryOperationResult.Fail($"Rollout '{id}' not found");
            if (!rollout.IsActive) return CanaryOperationResult.Fail($"Rollout {rollout.Id} already ended ({rollout.Phase})", rollout);

            var cluster = _config.GetCluster(rollout.ClusterId);
            if (cluster is null)
            {
                var aborted = await AbortInternalAsync(rollout, "Cluster no longer exists", restoreWeights: false, ct);
                return CanaryOperationResult.Fail("Cluster no longer exists", aborted.Rollout);
            }

            var result = await AdvanceAsync(rollout, cluster, createdBy ?? "manual-promote", ct);
            return result;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CanaryOperationResult> ResumeAsync(
        string id,
        string? createdBy = null,
        CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var rollout = FindLocked(id);
            if (rollout is null) return CanaryOperationResult.Fail($"Rollout '{id}' not found");
            if (rollout.Phase != CanaryPhase.Paused)
                return CanaryOperationResult.Fail($"Rollout {rollout.Id} is {rollout.Phase}, not Paused", rollout);

            var cluster = _config.GetCluster(rollout.ClusterId);
            if (cluster is null)
            {
                var aborted = await AbortInternalAsync(rollout, "Cluster no longer exists", restoreWeights: false, ct);
                return CanaryOperationResult.Fail("Cluster no longer exists", aborted.Rollout);
            }

            // Fresh window on purpose: the previous window's counters described a period we can no
            // longer trust, and carrying its extension count over would let a rollout skip the
            // sample-size guard.
            var now = _time.GetUtcNow();
            var resumed = rollout with
            {
                Phase = CanaryPhase.Observing,
                StepStartedAt = now,
                StepDeadline = now.AddSeconds(rollout.ObservationWindowSeconds),
                WindowExtensions = 0,
                LastCode = "resumed",
                LastReason = $"Window reopened by {createdBy ?? "operator"}"
            };

            CaptureWindowBaseline(resumed);
            ReplaceLocked(resumed);
            _ledger.Save(_rollouts);

            _logger.LogInformation("Canary rollout {RolloutId} resumed", resumed.Id);
            return new CanaryOperationResult(true, $"Rollout {resumed.Id} resumed", resumed);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CanaryOperationResult> AbortAsync(
        string id,
        string? reason = null,
        string? createdBy = null,
        CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var rollout = FindLocked(id);
            if (rollout is null) return CanaryOperationResult.Fail($"Rollout '{id}' not found");

            var stopped = await AbortInternalAsync(rollout, reason ?? $"Aborted by {createdBy ?? "operator"}", true, ct);
            return stopped;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task TickAsync(CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();

            foreach (var snapshot in _rollouts.ToList())
            {
                ct.ThrowIfCancellationRequested();

                if (snapshot.Phase != CanaryPhase.Observing) continue;
                if (now < snapshot.StepDeadline) continue;

                var cluster = _config.GetCluster(snapshot.ClusterId);
                if (cluster is null)
                {
                    await AbortInternalAsync(snapshot, "Cluster no longer exists", false, ct);
                    continue;
                }

                await EvaluateAndActAsync(snapshot, cluster, ct);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    #region Core transitions

    private async Task EvaluateAndActAsync(CanaryRollout rollout, ClusterConfig cluster, CancellationToken ct)
    {
        var observation = BuildObservation(rollout, cluster);
        var decision = CanaryRolloutEvaluator.Evaluate(
            observation,
            rollout.ErrorRateThreshold,
            rollout.MinSamples,
            rollout.WindowExtensions,
            _options.MaxWindowExtensions);

        var record = new CanaryStepRecord
        {
            StepIndex = rollout.StepIndex,
            CanaryPercent = rollout.CurrentPercent,
            ObservedSeconds = Math.Round((_time.GetUtcNow() - rollout.StepStartedAt).TotalSeconds, 1),
            CanaryRequests = observation.CanaryRequests,
            CanaryFailures = observation.CanaryFailures,
            BaselineRequests = observation.BaselineRequests,
            BaselineFailures = observation.BaselineFailures,
            Verdict = decision.Verdict,
            Code = decision.Code,
            Reason = decision.Message,
            ClosedAt = _time.GetUtcNow()
        };

        var updated = rollout with
        {
            History = rollout.History.Append(record).ToList(),
            LastVerdict = decision.Verdict,
            LastCode = decision.Code,
            LastReason = decision.Message
        };

        switch (decision.Verdict)
        {
            case CanaryVerdict.Hold:
                // Accumulate rather than restart: the point of extending is to reach the sample
                // size, and resetting the counters each time would guarantee it never gets there.
                ReplaceLocked(updated with
                {
                    StepDeadline = _time.GetUtcNow().AddSeconds(updated.ObservationWindowSeconds),
                    WindowExtensions = updated.WindowExtensions + 1
                });
                _ledger.Save(_rollouts);
                _logger.LogDebug("Rollout {RolloutId} extended: {Reason}", rollout.Id, decision.Message);
                break;

            case CanaryVerdict.Advance:
                if (!updated.AutoAdvance && updated.StepIndex < updated.Steps.Count - 1)
                {
                    ReplaceLocked(updated with
                    {
                        Phase = CanaryPhase.Paused,
                        LastCode = "awaitingPromotion",
                        LastReason = "Step is healthy; auto-advance is off, so this waits for a manual promotion."
                    });
                    _ledger.Save(_rollouts);
                    break;
                }

                if (updated.StepIndex >= updated.Steps.Count - 1)
                {
                    var completed = updated with
                    {
                        Phase = CanaryPhase.Completed,
                        EndedAt = _time.GetUtcNow(),
                        LastCode = "completed",
                        LastReason = $"All {updated.Steps.Count} steps passed; canary is serving {updated.CurrentPercent}%"
                    };
                    ReplaceLocked(completed);
                    _ledger.Save(_rollouts);
                    _notifications?.EnqueueAlert("CanaryCompleted", completed.ClusterId,
                        $"Rollout {completed.Id} completed: {string.Join(",", completed.CanaryDestinations)} now serve {completed.CurrentPercent}%",
                        "Info");
                    _logger.LogInformation("Canary rollout {RolloutId} completed", completed.Id);
                    break;
                }

                await AdvanceAsync(updated, cluster, "auto", ct);
                break;

            case CanaryVerdict.Pause:
                ReplaceLocked(updated with { Phase = CanaryPhase.Paused });
                _ledger.Save(_rollouts);
                _notifications?.EnqueueAlert("CanaryPaused", rollout.ClusterId,
                    $"Rollout {rollout.Id} paused at {record.CanaryPercent}%: {decision.Message}", "Warning");
                _logger.LogWarning("Canary rollout {RolloutId} paused: {Reason}", rollout.Id, decision.Message);
                break;

            case CanaryVerdict.Rollback:
                var restored = await RestoreBaselineAsync(updated, ct);
                var rolledBack = updated with
                {
                    Phase = CanaryPhase.RolledBack,
                    EndedAt = _time.GetUtcNow(),
                    LastCode = restored ? decision.Code : decision.Code + "-restoreFailed",
                    LastReason = restored
                        ? decision.Message
                        : decision.Message + " — but restoring the previous weights failed; check the cluster manually"
                };
                ReplaceLocked(rolledBack);
                _ledger.Save(_rollouts);
                _windowBaseline.Remove(rolledBack.Id);
                _notifications?.EnqueueAlert("CanaryRolledBack", rolledBack.ClusterId,
                    $"Rollout {rolledBack.Id} rolled back at {record.CanaryPercent}%: {decision.Message}",
                    "Critical");
                _logger.LogWarning("Canary rollout {RolloutId} rolled back: {Reason}", rolledBack.Id, decision.Message);
                break;
        }
    }

    private async Task<CanaryOperationResult> AdvanceAsync(
        CanaryRollout rollout,
        ClusterConfig cluster,
        string trigger,
        CancellationToken ct)
    {
        var nextIndex = rollout.StepIndex + 1;

        if (nextIndex >= rollout.Steps.Count)
        {
            var completed = rollout with
            {
                Phase = CanaryPhase.Completed,
                EndedAt = _time.GetUtcNow(),
                LastCode = "completed",
                LastReason = $"Last step reached ({trigger})"
            };
            ReplaceLocked(completed);
            _ledger.Save(_rollouts);
            _windowBaseline.Remove(completed.Id);
            return new CanaryOperationResult(true, $"Rollout {completed.Id} completed", completed);
        }

        var percent = rollout.Steps[nextIndex];
        if (!CanaryWeightPlanner.TryPlan(cluster, rollout.CanaryDestinations, percent, out var weights, out var planError))
        {
            var paused = rollout with
            {
                Phase = CanaryPhase.Paused,
                LastCode = "planFailed",
                LastReason = "Cannot compute weights for the next step: " + planError
            };
            ReplaceLocked(paused);
            _ledger.Save(_rollouts);
            return CanaryOperationResult.Fail(paused.LastReason!, paused);
        }

        var applied = await ApplyWeightsAsync(cluster, weights, rollout.CreatedBy, ct);
        if (!applied.Success)
        {
            var paused = rollout with
            {
                Phase = CanaryPhase.Paused,
                LastCode = "applyFailed",
                LastReason = "Could not apply the next step: " + applied.Message
            };
            ReplaceLocked(paused);
            _ledger.Save(_rollouts);
            return CanaryOperationResult.Fail(paused.LastReason!, paused);
        }

        var now = _time.GetUtcNow();
        var advanced = rollout with
        {
            StepIndex = nextIndex,
            CurrentWeights = weights,
            StepStartedAt = now,
            StepDeadline = now.AddSeconds(rollout.ObservationWindowSeconds),
            WindowExtensions = 0,
            Phase = CanaryPhase.Observing,
            LastCode = "advanced",
            LastReason = $"Stepped to {percent}% ({trigger})"
        };

        CaptureWindowBaseline(advanced);
        ReplaceLocked(advanced);
        _ledger.Save(_rollouts);

        _logger.LogInformation("Canary rollout {RolloutId} advanced to {Percent}% ({Trigger})",
            advanced.Id, percent, trigger);

        return new CanaryOperationResult(true, $"Rollout {advanced.Id} now at {percent}%", advanced);
    }

    private async Task<CanaryOperationResult> AbortInternalAsync(
        CanaryRollout rollout,
        string reason,
        bool restoreWeights,
        CancellationToken ct)
    {
        var restored = true;
        if (restoreWeights)
            restored = await RestoreBaselineAsync(rollout, ct);

        var aborted = rollout with
        {
            Phase = CanaryPhase.Aborted,
            EndedAt = _time.GetUtcNow(),
            LastVerdict = CanaryVerdict.Pause,
            LastCode = restored ? "aborted" : "aborted-restoreFailed",
            LastReason = restored
                ? reason
                : reason + " — restoring the previous weights failed; check the cluster manually"
        };

        ReplaceLocked(aborted);
        _ledger.Save(_rollouts);
        _windowBaseline.Remove(aborted.Id);

        _logger.LogInformation("Canary rollout {RolloutId} aborted: {Reason}", aborted.Id, reason);

        return new CanaryOperationResult(true,
            restored ? $"Rollout {aborted.Id} aborted and previous weights restored" : $"Rollout {aborted.Id} aborted (weights not restored)",
            aborted);
    }

    private async Task<RouteOperationResult> ApplyWeightsAsync(
        ClusterConfig cluster,
        IReadOnlyDictionary<string, int> weights,
        string? createdBy,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var target = cluster with { LoadBalancingPolicy = WeightedLoadBalancingPolicy.PolicyName };
        target = CanaryWeightPlanner.ApplyWeights(target, weights);

        return await _config.TryAddClusterConfig(target, Source, createdBy ?? "canary");
    }

    private async Task<bool> RestoreBaselineAsync(CanaryRollout rollout, CancellationToken ct)
    {
        var cluster = _config.GetCluster(rollout.ClusterId);
        if (cluster is null) return false;

        var restored = CanaryWeightPlanner.RestoreWeights(
            cluster, rollout.BaselineWeights, rollout.BaselineExplicitWeights);

        if (rollout.PreviousPolicy is not null)
            restored = restored with { LoadBalancingPolicy = rollout.PreviousPolicy };

        var result = await _config.TryAddClusterConfig(restored, Source, rollout.CreatedBy ?? "canary");
        if (!result.Success)
            _logger.LogWarning("Restoring baseline weights for cluster {ClusterId} failed: {Message}",
                rollout.ClusterId, result.Message);

        return result.Success;
    }

    #endregion

    #region Metrics

    private void CaptureWindowBaseline(CanaryRollout rollout)
    {
        var counters = new Dictionary<string, Counter>(StringComparer.Ordinal);
        foreach (var item in _metrics.GetDestinationCounters())
        {
            if (!string.Equals(item.Cluster, rollout.ClusterId, StringComparison.OrdinalIgnoreCase))
                continue;

            counters[item.Destination] = new Counter(item.Requests, item.Failures);
        }

        _windowBaseline[rollout.Id] = counters;
    }

    private CanaryObservation BuildObservation(CanaryRollout rollout, ClusterConfig cluster)
    {
        var canarySet = new HashSet<string>(rollout.CanaryDestinations, StringComparer.Ordinal);

        var baselineCounters = _windowBaseline.TryGetValue(rollout.Id, out var stored)
            ? stored
            : new Dictionary<string, Counter>(StringComparer.Ordinal);

        long canaryRequests = 0, canaryFailures = 0, baselineRequests = 0, baselineFailures = 0;

        foreach (var item in _metrics.GetDestinationCounters())
        {
            if (!string.Equals(item.Cluster, rollout.ClusterId, StringComparison.OrdinalIgnoreCase))
                continue;

            // Counters are cumulative for the process, so only the delta since the window opened
            // describes this step. A destination that appeared mid-window simply starts from zero.
            var before = baselineCounters.TryGetValue(item.Destination, out var c) ? c : default;
            var requests = item.Requests - before.Requests;
            var failures = item.Failures - before.Failures;

            if (requests < 0) requests = 0;   // counter reset (process restart) — do not go negative
            if (failures < 0) failures = 0;

            if (canarySet.Contains(item.Destination))
            {
                canaryRequests += requests;
                canaryFailures += failures;
            }
            else if (cluster.Destinations is null || cluster.Destinations.ContainsKey(item.Destination))
            {
                baselineRequests += requests;
                baselineFailures += failures;
            }
        }

        var expectBaseline = rollout.CurrentPercent < 100;

        return new CanaryObservation(
            canaryRequests, canaryFailures, baselineRequests, baselineFailures, expectBaseline);
    }

    #endregion

    #region Helpers

    private bool TryResolveCanaryDestinations(
        ClusterConfig cluster,
        CanaryStartRequest request,
        out List<string> canaryIds,
        out string? error)
    {
        canaryIds = new List<string>();
        error = null;

        if (cluster.Destinations is null || cluster.Destinations.Count == 0)
        {
            error = "Cluster has no destinations";
            return false;
        }

        if (request.CanaryDestinations is { Count: > 0 })
        {
            canaryIds = request.CanaryDestinations
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        else
        {
            // Fall back to the tag convention so a rollout can be started from a manifest that only
            // marks the new node, without also having to list it again.
            canaryIds = cluster.Destinations
                .Where(d => d.Value.Metadata is { Count: > 0 } md
                            && md.TryGetValue(_options.TagKey, out var tag)
                            && string.Equals(tag, _options.CanaryTag, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Key)
                .ToList();

            if (canaryIds.Count == 0)
            {
                error = $"No canary destinations given and none is tagged {_options.TagKey}={_options.CanaryTag}";
                return false;
            }
        }

        var unknown = canaryIds.Where(id => !cluster.Destinations.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
        {
            error = "Destination(s) not in cluster: " + string.Join(", ", unknown);
            return false;
        }

        if (canaryIds.Count == cluster.Destinations.Count)
        {
            error = "Every destination would be the canary; leave at least one on the baseline side";
            return false;
        }

        return true;
    }

    private bool TryNormalizeSteps(List<int>? input, out List<int> steps, out string? error)
    {
        steps = new List<int>();
        error = null;

        var source = input is { Count: > 0 }
            ? input
            : (_options.DefaultSteps is { Length: > 0 } ? _options.DefaultSteps.ToList() : new List<int> { 10, 30, 50, 100 });

        if (source.Count > 12)
        {
            error = "At most 12 steps are supported";
            return false;
        }

        var previous = 0;
        foreach (var value in source)
        {
            if (value is < 1 or > 100)
            {
                error = $"Step percentages must be between 1 and 100 (got {value})";
                return false;
            }

            if (value <= previous)
            {
                error = "Step percentages must be strictly increasing";
                return false;
            }

            previous = value;
            steps.Add(value);
        }

        return true;
    }

    private CanaryRollout? FindLocked(string id)
        => _rollouts.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    private void ReplaceLocked(CanaryRollout updated)
    {
        var index = _rollouts.FindIndex(r => string.Equals(r.Id, updated.Id, StringComparison.Ordinal));
        if (index >= 0) _rollouts[index] = updated;
        else _rollouts.Add(updated);
    }

    private void Trim()
    {
        var excess = _rollouts.Count - Math.Max(1, _options.MaxRetainedRollouts);
        if (excess <= 0) return;

        // Only finished rollouts are evictable; an active one is never dropped for storage reasons.
        var removable = _rollouts
            .Where(r => !r.IsActive)
            .OrderBy(r => r.StartedAt)
            .Take(excess)
            .Select(r => r.Id)
            .ToHashSet(StringComparer.Ordinal);

        if (removable.Count == 0) return;
        _rollouts.RemoveAll(r => removable.Contains(r.Id));
    }

    private readonly record struct Counter(long Requests, long Failures);

    #endregion
}
