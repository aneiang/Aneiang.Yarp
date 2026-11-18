using System.Text.Json.Serialization;

namespace Aneiang.Yarp.Dashboard.Modules.Alerting.Models;

/// <summary>Which metric a rule watches.</summary>
/// <remarks>
/// Serialised as a name rather than an ordinal on purpose: these enums are part of the rule API and
/// of the on-disk store, and a number there means a future reordering silently reinterprets every
/// saved rule.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MetricAlertKind
{
    /// <summary>
    /// Share of proxied requests that ended in 5xx over the window, 0..1.
    /// The same number <c>gateway_route_failures_total / gateway_requests_total</c> expresses.
    /// </summary>
    ErrorRate = 0,

    /// <summary>99th percentile proxy latency in seconds over the window.</summary>
    LatencyP99 = 1,

    /// <summary>How many circuit breakers are currently open within the rule's scope.</summary>
    CircuitOpen = 2
}

/// <summary>How narrowly a rule looks at the traffic.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlertScopeKind
{
    /// <summary>Every proxied request on this gateway instance.</summary>
    Global = 0,

    /// <summary>Every route pointing at one cluster.</summary>
    Cluster = 1,

    /// <summary>One route.</summary>
    Route = 2
}

/// <summary>Severity, mapped onto the three tiers the notification channels actually render.</summary>
/// <remarks>
/// There are only three colors downstream (Error = red, Warning = orange, everything else = green
/// checkmark), so <see cref="Critical"/> is delivered as <c>Error</c> rather than as its own level:
/// an unrecognised severity would fall through to the "all good" branch and paint a page-worthy
/// alert green.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

/// <summary>Lifecycle of one rule's current breach.</summary>
/// <remarks>
/// <see cref="Pending"/> exists so a five-second spike does not become a phone call: a rule has to
/// stay breached for <see cref="MetricAlertRule.ForSeconds"/> before it pages anyone.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlertIncidentState
{
    /// <summary>Breached, but not for long enough yet. Nobody has been notified.</summary>
    Pending = 0,

    /// <summary>Breached long enough to be real. Notified at least once.</summary>
    Firing = 1,

    /// <summary>Recovered (or the rule went away). Kept for history.</summary>
    Resolved = 2
}

/// <summary>
/// One threshold on one metric. Immutable: the service replaces the instance instead of mutating it,
/// so a request thread reading the rule list never sees a half-written rule.
/// </summary>
public sealed record MetricAlertRule
{
    /// <summary>Stable identifier; also the key the persisted incident history references.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Operator-facing name, quoted in the notification title.</summary>
    public string Name { get; init; } = string.Empty;

    public MetricAlertKind Kind { get; init; }

    public AlertScopeKind ScopeKind { get; init; }

    /// <summary>Cluster id or route id; ignored (and should be empty) for a global rule.</summary>
    public string? Scope { get; init; }

    /// <summary>
    /// The line that must be crossed. Ratio (0..1) for <see cref="MetricAlertKind.ErrorRate"/>,
    /// seconds for <see cref="MetricAlertKind.LatencyP99"/>, an open-circuit count for
    /// <see cref="MetricAlertKind.CircuitOpen"/>.
    /// </summary>
    public double Threshold { get; init; }

    /// <summary>How much traffic the measurement covers.</summary>
    public int WindowSeconds { get; init; } = 60;

    /// <summary>How long the breach must persist before notifying. 0 = notify on the first sample.</summary>
    public int ForSeconds { get; init; } = 60;

    /// <summary>
    /// Samples the window must contain before the rule is allowed to fire.
    /// A single failed request is a 100% error rate; without this floor every cold start would page.
    /// </summary>
    public int MinSamples { get; init; } = 20;

    public AlertSeverity Severity { get; init; } = AlertSeverity.Warning;

    public bool Enabled { get; init; } = true;

    /// <summary>Send a second notification when the incident recovers.</summary>
    public bool NotifyOnResolve { get; init; } = true;

    /// <summary>
    /// Minutes between repeat notifications while the incident stays firing. 0 = notify once.
    /// </summary>
    /// <remarks>
    /// Firing once and then going quiet is how an alert gets missed: whoever was asleep at the
    /// time never learns the gateway is still broken. Repeating is bounded by the notification
    /// dispatcher's own cooldown, so the two together cannot storm.
    /// </remarks>
    public int RenotifyMinutes { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public string CreatedBy { get; init; } = string.Empty;
}

/// <summary>One breach episode of one rule. Immutable, like the rule.</summary>
public sealed record MetricAlertIncident
{
    public string Id { get; init; } = string.Empty;

    public string RuleId { get; init; } = string.Empty;

    /// <summary>Snapshot of the rule name, so history survives the rule being renamed or deleted.</summary>
    public string RuleName { get; init; } = string.Empty;

    public MetricAlertKind Kind { get; init; }

    public AlertScopeKind ScopeKind { get; init; }

    public string? Scope { get; init; }

    public AlertSeverity Severity { get; init; }

    public AlertIncidentState State { get; init; }

    public double Threshold { get; init; }

    /// <summary>Most recent measurement, in the metric's own unit.</summary>
    public double LastValue { get; init; }

    /// <summary>Worst measurement seen during the incident.</summary>
    public double PeakValue { get; init; }

    /// <summary>Samples behind <see cref="LastValue"/>, so "12% of how many requests" is answerable.</summary>
    public long LastSamples { get; init; }

    /// <summary>Seconds the last measurement actually covered (the tick can overshoot the window).</summary>
    public int ObservedSeconds { get; init; }

    /// <summary>When the rule first went breached.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When it crossed <see cref="MetricAlertRule.ForSeconds"/> and started paging.</summary>
    public DateTimeOffset? FiringAt { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>Silenced until this instant; the incident keeps updating but sends nothing.</summary>
    public DateTimeOffset? MutedUntil { get; init; }

    public DateTimeOffset? LastNotifiedAt { get; init; }

    /// <summary>How many notifications went out for this incident.</summary>
    public int Notifications { get; init; }

    /// <summary>Why it ended: <c>recovered</c>, <c>ruleDisabled</c>, <c>ruleDeleted</c>.</summary>
    public string? ResolveReason { get; init; }

    /// <summary>Machine code of the last evaluation (<c>breaching</c>, <c>healthy</c>, <c>lowSamples</c>…).</summary>
    public string? LastCode { get; init; }

    /// <summary>Sentence explaining the last evaluation, shown as-is in the panel.</summary>
    public string? LastReason { get; init; }

    /// <summary>When the rule last looked at this incident.</summary>
    public DateTimeOffset? LastCheckedAt { get; init; }

    /// <summary>Still breaching (pending or firing), as opposed to history.</summary>
    public bool IsActive => State is AlertIncidentState.Pending or AlertIncidentState.Firing;

    /// <summary>Whether notifications are currently suppressed by a mute.</summary>
    public bool IsMutedAt(DateTimeOffset now) => MutedUntil is { } until && until > now;

    /// <summary>How long the incident has been going, measured against <paramref name="now"/>.</summary>
    public TimeSpan Elapsed(DateTimeOffset now) => (ResolvedAt ?? now) - StartedAt;
}

/// <summary>Tunables for the alerting loop, bound from configuration.</summary>
public sealed class MetricAlertOptions
{
    public const string SectionName = "Aneiang:Yarp:Dashboard:Alerting";

    /// <summary>How often rules are evaluated. Also the granularity of the sustained-breach check.</summary>
    public int TickSeconds { get; set; } = 15;

    /// <summary>Retained incidents (active ones are never trimmed).</summary>
    public int MaxIncidents { get; set; } = 200;

    public int DefaultWindowSeconds { get; set; } = 60;

    public int DefaultForSeconds { get; set; } = 60;

    public int DefaultMinSamples { get; set; } = 20;

    /// <summary>5% 5xx, the usual "something is wrong" line.</summary>
    public double DefaultErrorRateThreshold { get; set; } = 0.05;

    /// <summary>One second p99 is generous for an API gateway and still catches stalls.</summary>
    public double DefaultLatencyP99Seconds { get; set; } = 1.0;
}
