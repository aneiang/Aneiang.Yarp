namespace Aneiang.Yarp.Dashboard.Modules.Canary.Models;

/// <summary>
/// Lifecycle of one staged rollout. Only <see cref="Observing"/> and <see cref="Paused"/> are
/// still under the supervisor's control; the rest are terminal and exist for the audit trail.
/// </summary>
public enum CanaryPhase
{
    /// <summary>Traffic is split at the current step and the observation window is running.</summary>
    Observing,

    /// <summary>Automation stopped and is waiting for a human. Weights are left untouched.</summary>
    Paused,

    /// <summary>Every step passed; the canary set is now serving 100%.</summary>
    Completed,

    /// <summary>The gate tripped and the pre-rollout weights were restored.</summary>
    RolledBack,

    /// <summary>A human (or a vanished cluster) stopped it. Weights depend on how it stopped.</summary>
    Aborted
}

/// <summary>What the gate decided when an observation window closed.</summary>
public enum CanaryVerdict
{
    /// <summary>Not enough traffic to judge yet; the window is extended.</summary>
    Hold,

    /// <summary>Healthy — move to the next step.</summary>
    Advance,

    /// <summary>Cannot be attributed to the canary; stop and ask a human.</summary>
    Pause,

    /// <summary>The canary is materially worse than the baseline; restore the old weights.</summary>
    Rollback
}

/// <summary>
/// One closed observation window, kept so the UI can show how the decision was reached.
/// </summary>
/// <remarks>
/// A rollback notice that only says "rolled back" is impossible to argue with and impossible to
/// trust. The window's numbers are kept so an operator can check the gate's arithmetic — including
/// when they disagree with it.
/// </remarks>
public sealed record CanaryStepRecord
{
    /// <summary>Zero-based step this record belongs to.</summary>
    public int StepIndex { get; init; }

    /// <summary>The traffic percentage the canary was supposed to receive at this step.</summary>
    public int CanaryPercent { get; init; }

    /// <summary>How long the window actually ran, including extensions.</summary>
    public double ObservedSeconds { get; init; }

    /// <summary>Requests the canary destinations served inside the window.</summary>
    public long CanaryRequests { get; init; }

    /// <summary>Of those, 5xx responses.</summary>
    public long CanaryFailures { get; init; }

    /// <summary>Requests the baseline destinations served inside the window.</summary>
    public long BaselineRequests { get; init; }

    /// <summary>Of those, 5xx responses.</summary>
    public long BaselineFailures { get; init; }

    /// <summary>Decision taken.</summary>
    public CanaryVerdict Verdict { get; init; }

    /// <summary>Localizable reason code.</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>Technical explanation with the numbers, shown verbatim in the UI.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>UTC time the window closed.</summary>
    public DateTimeOffset ClosedAt { get; init; }

    /// <summary>Canary 5xx ratio observed in this window, or null when it served no traffic.</summary>
    public double? CanaryErrorRate => CanaryRequests > 0 ? (double)CanaryFailures / CanaryRequests : null;

    /// <summary>Baseline 5xx ratio observed in this window, or null when it served no traffic.</summary>
    public double? BaselineErrorRate => BaselineRequests > 0 ? (double)BaselineFailures / BaselineRequests : null;
}

/// <summary>
/// One staged rollout of a cluster: a list of traffic percentages applied in order, each held for
/// an observation window before the next one is applied.
/// </summary>
/// <remarks>
/// Immutable by design. Every change produces a new instance that replaces the old one in the
/// ledger, so an HTTP request reading the list while the supervisor acts never sees a half-applied
/// state — it either sees the previous rollout or the next one, never a mixture.
/// </remarks>
public sealed record CanaryRollout
{
    /// <summary>Short id used by the API and the UI.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Cluster under rollout.</summary>
    public string ClusterId { get; init; } = string.Empty;

    /// <summary>Destination ids that receive the canary share.</summary>
    public IReadOnlyList<string> CanaryDestinations { get; init; } = Array.Empty<string>();

    /// <summary>Percentage of traffic the canary receives at each step, in order.</summary>
    public IReadOnlyList<int> Steps { get; init; } = Array.Empty<int>();

    /// <summary>Index into <see cref="Steps"/> currently being observed.</summary>
    public int StepIndex { get; init; }

    /// <summary>Current lifecycle phase.</summary>
    public CanaryPhase Phase { get; init; } = CanaryPhase.Observing;

    /// <summary>
    /// Weights captured before the first step was applied. This is the rollback target, and it is
    /// why a rollback restores the exact previous split rather than "whatever looks like 50/50".
    /// </summary>
    public IReadOnlyDictionary<string, int> BaselineWeights { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Weights currently applied by the rollout.</summary>
    public IReadOnlyDictionary<string, int> CurrentWeights { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>
    /// Destinations that carried an explicit weight before the rollout started.
    /// </summary>
    /// <remarks>
    /// A rollback has to be exact, not merely equivalent. Destinations that had no weight
    /// metadata must get it removed again rather than pinned to the default value, otherwise every
    /// rolled-back cluster keeps a coat of paint the operator never applied.
    /// </remarks>
    public IReadOnlyList<string> BaselineExplicitWeights { get; init; } = Array.Empty<string>();

    /// <summary>Cluster load-balancing policy before the rollout; <c>null</c> when it was already Weighted.</summary>
    public string? PreviousPolicy { get; init; }

    /// <summary>When the rollout was created.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When the current step's weights were applied.</summary>
    public DateTimeOffset StepStartedAt { get; init; }

    /// <summary>When the current step's window closes and gets evaluated.</summary>
    public DateTimeOffset StepDeadline { get; init; }

    /// <summary>How many times the current window has been extended for lack of samples.</summary>
    public int WindowExtensions { get; init; }

    /// <summary>Length of one observation window, in seconds.</summary>
    public int ObservationWindowSeconds { get; init; }

    /// <summary>5xx ratio above which the canary is considered unhealthy (0..1).</summary>
    public double ErrorRateThreshold { get; init; }

    /// <summary>Requests a side must serve before any verdict other than Hold is allowed.</summary>
    public int MinSamples { get; init; }

    /// <summary>Whether the supervisor advances steps on its own.</summary>
    public bool AutoAdvance { get; init; } = true;

    /// <summary>Operator note recorded when the rollout was started.</summary>
    public string? Reason { get; init; }

    /// <summary>Who started it.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>When it reached a terminal phase.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>Closed windows, oldest first.</summary>
    public IReadOnlyList<CanaryStepRecord> History { get; init; } = Array.Empty<CanaryStepRecord>();

    /// <summary>Copy of the last verdict, for compact list rendering.</summary>
    public CanaryVerdict? LastVerdict { get; init; }

    /// <summary>Localizable reason code that goes with <see cref="LastVerdict"/>.</summary>
    public string? LastCode { get; init; }

    /// <summary>Explanation that goes with <see cref="LastVerdict"/>.</summary>
    public string? LastReason { get; init; }

    /// <summary>Whether the supervisor should still act on this rollout.</summary>
    public bool IsActive => Phase is CanaryPhase.Observing or CanaryPhase.Paused;

    /// <summary>Traffic percentage at the current step, or the last step for a finished rollout.</summary>
    public int CurrentPercent => Steps.Count == 0
        ? 0
        : Steps[Math.Clamp(StepIndex, 0, Steps.Count - 1)];
}

/// <summary>Request body for starting a rollout.</summary>
public sealed class CanaryStartRequest
{
    /// <summary>Cluster to roll out.</summary>
    public string ClusterId { get; set; } = string.Empty;

    /// <summary>
    /// Destination ids that carry the canary traffic. Defaults to every destination whose
    /// <c>Tag</c> metadata equals <see cref="CanaryOptions.CanaryTag"/>.
    /// </summary>
    public List<string>? CanaryDestinations { get; set; }

    /// <summary>Traffic percentages, in order. Defaults to a 10 / 30 / 50 / 100 ramp.</summary>
    public List<int>? Steps { get; set; }

    /// <summary>Observation window in seconds; falls back to the configured default.</summary>
    public int? ObservationWindowSeconds { get; set; }

    /// <summary>5xx ratio threshold (0..1); falls back to the configured default.</summary>
    public double? ErrorRateThreshold { get; set; }

    /// <summary>Minimum samples per side; falls back to the configured default.</summary>
    public int? MinSamples { get; set; }

    /// <summary>Whether steps advance automatically.</summary>
    public bool? AutoAdvance { get; set; }

    /// <summary>Free-form note.</summary>
    public string? Reason { get; set; }
}

/// <summary>Defaults for canary rollouts.</summary>
public sealed class CanaryOptions
{
    /// <summary>Configuration section that can override these defaults.</summary>
    public const string SectionName = "Aneiang:Yarp:Dashboard:Canary";

    /// <summary>Default observation window, in seconds.</summary>
    public int ObservationWindowSeconds { get; set; } = 120;

    /// <summary>Default 5xx ratio threshold (0..1).</summary>
    public double ErrorRateThreshold { get; set; } = 0.2;

    /// <summary>Default minimum samples per side before a verdict is allowed.</summary>
    public int MinSamples { get; set; } = 50;

    /// <summary>Default step ramp.</summary>
    public int[] DefaultSteps { get; set; } = { 10, 30, 50, 100 };

    /// <summary>Destination metadata key used to auto-detect canary nodes.</summary>
    public string TagKey { get; set; } = "Tag";

    /// <summary>Tag value that marks a destination as a canary node.</summary>
    public string CanaryTag { get; set; } = "canary";

    /// <summary>Maximum window extensions before automation gives up and pauses.</summary>
    public int MaxWindowExtensions { get; set; } = 3;

    /// <summary>How often the supervisor looks for expired windows.</summary>
    public int TickSeconds { get; set; } = 5;

    /// <summary>Upper bound on retained rollout records.</summary>
    public int MaxRetainedRollouts { get; set; } = 50;
}
