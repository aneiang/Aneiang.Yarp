using Aneiang.Yarp.Dashboard.Modules.Alerting.Models;

namespace Aneiang.Yarp.Dashboard.Modules.Alerting.Services;

/// <summary>Outcome of a rule or incident mutation, shaped for the controller to return.</summary>
/// <param name="Success">False when validation rejected the request.</param>
/// <param name="Message">Why it failed, or what happened.</param>
/// <param name="Rule">The affected rule, when there is one.</param>
/// <param name="Incident">The affected incident, when there is one.</param>
public sealed record MetricAlertResult(
    bool Success,
    string Message,
    MetricAlertRule? Rule = null,
    MetricAlertIncident? Incident = null);

/// <summary>
/// Evaluates threshold rules against the live metrics and turns breaches into notifications.
/// </summary>
public interface IMetricAlertService
{
    /// <summary>Configured rules, stable order (creation order).</summary>
    IReadOnlyList<MetricAlertRule> GetRules();

    /// <summary>Retained incidents, newest first.</summary>
    IReadOnlyList<MetricAlertIncident> GetIncidents();

    /// <summary>One rule, or null.</summary>
    MetricAlertRule? GetRule(string id);

    /// <summary>Creates or replaces a rule. Validation failures come back as <c>Success = false</c>.</summary>
    Task<MetricAlertResult> SaveRuleAsync(MetricAlertRule rule, string actor, CancellationToken ct = default);

    /// <summary>Deletes a rule and closes any incident it left open.</summary>
    Task<MetricAlertResult> DeleteRuleAsync(string id, string actor, CancellationToken ct = default);

    /// <summary>Silences one incident for a number of minutes without disarming the rule.</summary>
    Task<MetricAlertResult> MuteAsync(string incidentId, int minutes, string actor, CancellationToken ct = default);

    /// <summary>Sends a synthetic notification so the operator can verify the channel actually works.</summary>
    Task<MetricAlertResult> TestAsync(string ruleId, string actor, CancellationToken ct = default);

    /// <summary>Runs one evaluation pass. Called by the supervisor on a timer.</summary>
    Task EvaluateAsync(CancellationToken ct = default);
}
