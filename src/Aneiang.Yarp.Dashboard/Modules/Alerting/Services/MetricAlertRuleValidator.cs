using Aneiang.Yarp.Dashboard.Modules.Alerting.Models;

namespace Aneiang.Yarp.Dashboard.Modules.Alerting.Services;

/// <summary>
/// Rejects rules that would never fire, or would fire on nonsense.
/// </summary>
/// <remarks>
/// This exists because the worst outcome for an alert is not "noisy" — it is "looks configured and
/// silently never fires". A threshold of <c>50</c> on an error-rate rule (someone typing 50 for 50%)
/// is accepted by every layer below this one and then waits forever, so the only defence is to
/// reject it at the door with a message that says what the field means.
/// </remarks>
public static class MetricAlertRuleValidator
{
    /// <summary>
    /// Validates <paramref name="rule"/>. Returns every problem at once so the editor can show them
    /// together instead of one per round-trip.
    /// </summary>
    public static IReadOnlyList<string> Validate(MetricAlertRule rule)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(rule.Name))
            errors.Add("name is required");
        else if (rule.Name.Length > 80)
            errors.Add("name must be 80 characters or fewer");

        if (!Enum.IsDefined(rule.Kind))
            errors.Add($"unknown metric kind '{rule.Kind}'");

        switch (rule.ScopeKind)
        {
            case AlertScopeKind.Global:
                if (!string.IsNullOrWhiteSpace(rule.Scope))
                    errors.Add("a global rule must not carry a scope id");
                break;

            case AlertScopeKind.Cluster:
            case AlertScopeKind.Route:
                if (string.IsNullOrWhiteSpace(rule.Scope))
                    errors.Add($"scope is required for a {rule.ScopeKind.ToString().ToLowerInvariant()} rule");
                break;

            default:
                errors.Add($"unknown scope kind '{rule.ScopeKind}'");
                break;
        }

        switch (rule.Kind)
        {
            case MetricAlertKind.ErrorRate:
                // A ratio, not a percentage: 0.05 = 5%. Accepting 5 here would create a rule that
                // needs an impossible 500% error rate to fire.
                if (rule.Threshold is <= 0 or > 1)
                    errors.Add("error-rate threshold is a ratio between 0 and 1 (0.05 = 5%)");
                break;

            case MetricAlertKind.LatencyP99:
                if (rule.Threshold is <= 0 or > 3600)
                    errors.Add("latency threshold must be greater than 0 and at most 3600 seconds");
                // p99 over 5 samples is the maximum of 5 samples. Requiring a real sample floor is
                // the difference between "p99 latency" and "the slowest request I happened to see".
                if (rule.MinSamples < 20)
                    errors.Add("latency rules need at least 20 samples; a p99 over fewer is just the slowest request");
                break;

            case MetricAlertKind.CircuitOpen:
                if (rule.Threshold < 1)
                    errors.Add("circuit threshold is a count of open circuits and must be at least 1");
                // Circuits are tracked per cluster/destination, never per route. A route-scoped
                // circuit rule matches nothing and would sit at "insufficient data" forever.
                if (rule.ScopeKind == AlertScopeKind.Route)
                    errors.Add("circuit rules watch a cluster or the whole gateway, not a route");
                break;
        }

        if (rule.WindowSeconds is < 10 or > 3600)
            errors.Add("window must be between 10 and 3600 seconds");

        if (rule.ForSeconds is < 0 or > 3600)
            errors.Add("duration must be between 0 and 3600 seconds");

        if (rule.MinSamples is < 1 or > 1_000_000)
            errors.Add("minimum samples must be between 1 and 1000000");

        if (rule.RenotifyMinutes is < 0 or > 1440)
            errors.Add("re-notify interval must be between 0 and 1440 minutes");

        if (!Enum.IsDefined(rule.Severity))
            errors.Add($"unknown severity '{rule.Severity}'");

        return errors;
    }
}
