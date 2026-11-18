using Aneiang.Yarp.Dashboard.Modules.Canary.Models;
using Aneiang.Yarp.Dashboard.Modules.Canary.Services;
using Aneiang.Yarp.Services;
using Microsoft.AspNetCore.Mvc;
using Yarp.ReverseProxy.Configuration;

namespace Aneiang.Yarp.Dashboard.Modules.Canary.Controllers;

/// <summary>
/// API for staged rollouts: start one, watch it, promote it, or stop it.
/// </summary>
/// <remarks>
/// Deliberately not wired into the <c>X-Config-Version</c> guard used by the config editors. A
/// rollout changes the config on its own as steps advance, so a version sampled when the page
/// loaded is stale by design — checking it here would reject every manual promotion on a healthy
/// rollout. Concurrency is handled instead by the "one active rollout per cluster" rule.
/// </remarks>
[Route("api/canary")]
[ApiController]
public sealed class CanaryController : ControllerBase
{
    private readonly ICanaryRolloutService _rollouts;
    private readonly IDynamicYarpConfigService _config;
    private readonly CanaryOptions _options;
    private readonly CanaryLedgerStore _ledger;

    public CanaryController(
        ICanaryRolloutService rollouts,
        IDynamicYarpConfigService config,
        CanaryLedgerStore ledger,
        Microsoft.Extensions.Options.IOptions<CanaryOptions> options)
    {
        _rollouts = rollouts;
        _config = config;
        _ledger = ledger;
        _options = options.Value;
    }

    /// <summary>Every retained rollout, newest first, plus the ledger location.</summary>
    [HttpGet("rollouts")]
    public IActionResult List()
        => Ok(new
        {
            code = 200,
            data = new
            {
                rollouts = _rollouts.GetAll().Select(ToDto).ToList(),
                ledgerPath = _ledger.Path,
                defaults = new
                {
                    observationWindowSeconds = _options.ObservationWindowSeconds,
                    errorRateThreshold = _options.ErrorRateThreshold,
                    minSamples = _options.MinSamples,
                    steps = _options.DefaultSteps
                }
            }
        });

    /// <summary>One rollout by id.</summary>
    [HttpGet("rollouts/{id}")]
    public IActionResult Get(string id)
    {
        var rollout = _rollouts.Get(id);
        return rollout is null
            ? NotFound(new { code = 404, message = $"Rollout '{id}' not found" })
            : Ok(new { code = 200, data = ToDto(rollout) });
    }

    /// <summary>
    /// Pre-flight for the start dialog: which destinations exist, which are tagged canary, and
    /// whether the cluster can be rolled out at all.
    /// </summary>
    [HttpGet("candidates/{clusterId}")]
    public IActionResult Candidates(string clusterId)
    {
        var cluster = _config.GetCluster(clusterId);
        if (cluster is null)
            return NotFound(new { code = 404, message = $"Cluster '{clusterId}' not found" });

        var weightKey = CanaryWeightPlanner.ResolveWeightKey(cluster);
        var defaultWeight = CanaryWeightPlanner.ResolveDefaultWeight(cluster);

        var destinations = (cluster.Destinations ?? new Dictionary<string, DestinationConfig>())
            .Select(d =>
            {
                var tag = d.Value.Metadata is { Count: > 0 } md && md.TryGetValue(_options.TagKey, out var t) ? t : null;
                return new
                {
                    id = d.Key,
                    address = d.Value.Address,
                    tag,
                    weight = CanaryWeightPlanner.ReadWeight(d.Value, weightKey, defaultWeight),
                    isCanary = string.Equals(tag, _options.CanaryTag, StringComparison.OrdinalIgnoreCase)
                };
            })
            .OrderBy(d => d.id, StringComparer.Ordinal)
            .ToList();

        var active = _rollouts.GetAll().FirstOrDefault(r =>
            r.IsActive && string.Equals(r.ClusterId, clusterId, StringComparison.OrdinalIgnoreCase));

        return Ok(new
        {
            code = 200,
            data = new
            {
                clusterId,
                loadBalancingPolicy = cluster.LoadBalancingPolicy,
                weightKey,
                destinations,
                activeRolloutId = active?.Id,
                canStart = destinations.Count > 1 && active is null,
                blockedReason = active is not null
                    ? $"Rollout {active.Id} is already running on this cluster"
                    : destinations.Count <= 1 ? "A canary needs at least two destinations" : null
            }
        });
    }

    /// <summary>Starts a rollout.</summary>
    [HttpPost("rollouts")]
    public async Task<IActionResult> Start([FromBody] CanaryStartRequest request, CancellationToken ct)
    {
        var result = await _rollouts.StartAsync(request, ResolveOperator(), ct);
        return result.Success
            ? Ok(new { code = 200, message = result.Message, data = ToDto(result.Rollout!) })
            : BadRequest(new { code = 400, message = result.Message, data = result.Rollout is null ? null : ToDto(result.Rollout) });
    }

    /// <summary>Forces the next step without waiting for the gate.</summary>
    [HttpPost("rollouts/{id}/promote")]
    public async Task<IActionResult> Promote(string id, CancellationToken ct)
        => ToActionResult(await _rollouts.PromoteAsync(id, ResolveOperator(), ct));

    /// <summary>Re-opens a paused rollout's window.</summary>
    [HttpPost("rollouts/{id}/resume")]
    public async Task<IActionResult> Resume(string id, CancellationToken ct)
        => ToActionResult(await _rollouts.ResumeAsync(id, ResolveOperator(), ct));

    /// <summary>Stops the rollout and restores the previous weights.</summary>
    [HttpPost("rollouts/{id}/abort")]
    public async Task<IActionResult> Abort(string id, [FromBody] CanaryAbortRequest? body, CancellationToken ct)
        => ToActionResult(await _rollouts.AbortAsync(id, body?.Reason, ResolveOperator(), ct));

    private IActionResult ToActionResult(CanaryOperationResult result)
        => result.Success
            ? Ok(new { code = 200, message = result.Message, data = result.Rollout is null ? null : ToDto(result.Rollout) })
            : BadRequest(new { code = 400, message = result.Message, data = result.Rollout is null ? null : ToDto(result.Rollout) });

    private string ResolveOperator()
    {
        try
        {
            var name = User?.Identity?.Name;
            return string.IsNullOrWhiteSpace(name) ? "dashboard-user" : name!;
        }
        catch
        {
            return "dashboard-user";
        }
    }

    /// <summary>
    /// Projects the immutable rollout into the JSON shape the dashboard reads: enums as strings so
    /// the browser never has to know their numeric order.
    /// </summary>
    private object ToDto(CanaryRollout rollout)
    {
        var secondsRemaining = rollout.Phase == CanaryPhase.Observing
            ? Math.Max(0, (int)Math.Ceiling((rollout.StepDeadline - DateTimeOffset.UtcNow).TotalSeconds))
            : 0;

        // The live window is what the operator actually wants to see: the closed-window history
        // only catches up once every observation window, which is too slow to watch a rollout by.
        var live = rollout.Phase == CanaryPhase.Observing ? _rollouts.GetLiveObservation(rollout.Id) : null;

        return new
        {
            id = rollout.Id,
            clusterId = rollout.ClusterId,
            canaryDestinations = rollout.CanaryDestinations,
            steps = rollout.Steps,
            stepIndex = rollout.StepIndex,
            stepCount = rollout.Steps.Count,
            currentPercent = rollout.CurrentPercent,
            phase = rollout.Phase.ToString(),
            autoAdvance = rollout.AutoAdvance,
            startedAt = rollout.StartedAt,
            stepStartedAt = rollout.StepStartedAt,
            stepDeadline = rollout.StepDeadline,
            secondsRemaining,
            windowExtensions = rollout.WindowExtensions,
            observationWindowSeconds = rollout.ObservationWindowSeconds,
            errorRateThreshold = rollout.ErrorRateThreshold,
            minSamples = rollout.MinSamples,
            baselineWeights = rollout.BaselineWeights,
            currentWeights = rollout.CurrentWeights,
            previousPolicy = rollout.PreviousPolicy,
            reason = rollout.Reason,
            createdBy = rollout.CreatedBy,
            endedAt = rollout.EndedAt,
            isActive = rollout.IsActive,
            lastVerdict = rollout.LastVerdict?.ToString(),
            live = live is null ? null : new
            {
                canaryRequests = live.Value.CanaryRequests,
                canaryFailures = live.Value.CanaryFailures,
                baselineRequests = live.Value.BaselineRequests,
                baselineFailures = live.Value.BaselineFailures,
                canaryErrorRate = live.Value.CanaryErrorRate,
                baselineErrorRate = live.Value.BaselineErrorRate,
                expectBaselineTraffic = live.Value.ExpectBaselineTraffic
            },
            lastCode = rollout.LastCode,
            lastReason = rollout.LastReason,
            history = rollout.History.Select(h => new
            {
                stepIndex = h.StepIndex,
                canaryPercent = h.CanaryPercent,
                observedSeconds = h.ObservedSeconds,
                canaryRequests = h.CanaryRequests,
                canaryFailures = h.CanaryFailures,
                baselineRequests = h.BaselineRequests,
                baselineFailures = h.BaselineFailures,
                canaryErrorRate = h.CanaryErrorRate,
                baselineErrorRate = h.BaselineErrorRate,
                verdict = h.Verdict.ToString(),
                code = h.Code,
                reason = h.Reason,
                closedAt = h.ClosedAt
            }).ToList()
        };
    }
}

/// <summary>Optional reasons supplied when aborting a rollout.</summary>
public sealed class CanaryAbortRequest
{
    /// <summary>Why it was stopped; recorded in the ledger and the notification.</summary>
    public string? Reason { get; set; }
}
