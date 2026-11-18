using Aneiang.Yarp.Dashboard.Modules.Canary.Models;

namespace Aneiang.Yarp.Dashboard.Modules.Canary.Services;

/// <summary>Outcome of a canary operation.</summary>
/// <param name="Success">Whether the operation applied.</param>
/// <param name="Message">Explanation, safe to show to the operator.</param>
/// <param name="Rollout">The rollout after the operation, when there is one.</param>
public sealed record CanaryOperationResult(bool Success, string Message, CanaryRollout? Rollout = null)
{
    /// <summary>Convenience factory for failures.</summary>
    public static CanaryOperationResult Fail(string message, CanaryRollout? rollout = null)
        => new(false, message, rollout);
}

/// <summary>
/// Drives staged rollouts: applies weights, watches the error rate, and puts the old weights back
/// when the canary turns out to be worse.
/// </summary>
public interface ICanaryRolloutService
{
    /// <summary>Snapshot of every retained rollout, newest first.</summary>
    IReadOnlyList<CanaryRollout> GetAll();

    /// <summary>One rollout by id, or null.</summary>
    CanaryRollout? Get(string id);

    /// <summary>
    /// Live counters for a rollout's current, unclosed window — the same numbers the gate will use
    /// when the window closes. Returns null when the rollout is not being observed.
    /// </summary>
    CanaryObservation? GetLiveObservation(string id);

    /// <summary>
    /// Starts a rollout. Fails when the cluster is unknown, the canary set is empty or equal to the
    /// whole cluster, the steps are invalid, or another rollout is already running on that cluster.
    /// </summary>
    Task<CanaryOperationResult> StartAsync(CanaryStartRequest request, string? createdBy = null, CancellationToken ct = default);

    /// <summary>Forces the next step without waiting for the gate. Human override.</summary>
    Task<CanaryOperationResult> PromoteAsync(string id, string? createdBy = null, CancellationToken ct = default);

    /// <summary>Re-opens a paused rollout's observation window.</summary>
    Task<CanaryOperationResult> ResumeAsync(string id, string? createdBy = null, CancellationToken ct = default);

    /// <summary>Stops the rollout and restores the pre-rollout weights.</summary>
    Task<CanaryOperationResult> AbortAsync(string id, string? reason = null, string? createdBy = null, CancellationToken ct = default);

    /// <summary>Evaluates every expired window. Called by the supervisor.</summary>
    Task TickAsync(CancellationToken ct = default);
}
