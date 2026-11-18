using Aneiang.Yarp.Dashboard.Modules.Canary.Models;

namespace Aneiang.Yarp.Dashboard.Modules.Canary.Services;

/// <summary>
/// Traffic observed on both sides during one window.
/// </summary>
/// <param name="CanaryRequests">Requests served by the canary destinations.</param>
/// <param name="CanaryFailures">Of those, 5xx responses.</param>
/// <param name="BaselineRequests">Requests served by the baseline destinations.</param>
/// <param name="BaselineFailures">Of those, 5xx responses.</param>
/// <param name="ExpectBaselineTraffic">
/// False when the step sends 100% to the canary, in which case there is no baseline traffic to
/// compare against and demanding samples from it would deadlock the rollout at the last step.
/// </param>
public readonly record struct CanaryObservation(
    long CanaryRequests,
    long CanaryFailures,
    long BaselineRequests,
    long BaselineFailures,
    bool ExpectBaselineTraffic)
{
    /// <summary>Canary 5xx ratio, or null when it served no traffic.</summary>
    public double? CanaryErrorRate => CanaryRequests > 0 ? (double)CanaryFailures / CanaryRequests : null;

    /// <summary>Baseline 5xx ratio, or null when it served no traffic.</summary>
    public double? BaselineErrorRate => BaselineRequests > 0 ? (double)BaselineFailures / BaselineRequests : null;
}

/// <summary>Reason codes so the UI can localize instead of printing English.</summary>
public static class CanaryReasonCodes
{
    /// <summary>Too few requests to judge.</summary>
    public const string InsufficientSamples = "insufficientSamples";

    /// <summary>The canary received no requests at all — likely the weights never took effect.</summary>
    public const string NoCanaryTraffic = "noCanaryTraffic";

    /// <summary>Error rate within threshold.</summary>
    public const string Healthy = "healthy";

    /// <summary>Canary is over threshold but so is the baseline: an environment problem, not the rollout.</summary>
    public const string BaselineDegraded = "baselineDegraded";

    /// <summary>Canary is over threshold while the baseline is not.</summary>
    public const string CanaryDegraded = "canaryDegraded";
}

/// <summary>
/// Outcome of one gate evaluation.
/// </summary>
/// <param name="Verdict">What to do next.</param>
/// <param name="Code">Localizable reason code.</param>
/// <param name="Message">Technical one-liner with the numbers, for logs and webhooks.</param>
/// <param name="ExtendWindow">True when the window should run again rather than conclude.</param>
public sealed record CanaryDecision(CanaryVerdict Verdict, string Code, string Message, bool ExtendWindow = false);

/// <summary>
/// The canary gate: turns two error rates into Advance / Hold / Pause / Rollback.
///
/// Kept free of IO and time so the decision itself is testable — this is the part that decides
/// whether a bad deploy keeps serving traffic, and it is the wrong place to be clever.
/// </summary>
public static class CanaryRolloutEvaluator
{
    /// <summary>
    /// Evaluates one observation window.
    /// </summary>
    /// <param name="observation">Counters accumulated during the window.</param>
    /// <param name="errorRateThreshold">Absolute 5xx ratio above which the canary is unhealthy.</param>
    /// <param name="minSamples">Requests each side must serve before a verdict other than Hold.</param>
    /// <param name="windowExtensions">How many times this window already ran.</param>
    /// <param name="maxWindowExtensions">How many times it may run in total.</param>
    /// <returns>The decision.</returns>
    /// <remarks>
    /// The ordering matters. Sample size is checked first: rolling back on three requests and one
    /// failure is how an auto-rollback feature earns a reputation for flapping. Then the absolute
    /// threshold. Then, and only then, attribution — if the baseline is failing at the same rate,
    /// the canary is not the cause and rolling it back would not fix anything, so the rollout
    /// pauses instead. A rollback that cannot help is worse than no rollback: it destroys the
    /// rollout's evidence and sends the operator looking in the wrong place.
    /// </remarks>
    public static CanaryDecision Evaluate(
        CanaryObservation observation,
        double errorRateThreshold,
        int minSamples,
        int windowExtensions,
        int maxWindowExtensions)
    {
        if (minSamples < 1) minSamples = 1;

        var canaryShort = observation.CanaryRequests < minSamples;
        var baselineShort = observation.ExpectBaselineTraffic && observation.BaselineRequests < minSamples;

        if (canaryShort || baselineShort)
            return HoldOrPause(observation, minSamples, windowExtensions, maxWindowExtensions);

        var canaryRate = observation.CanaryErrorRate!.Value;
        var canaryPercent = FormatPercent(canaryRate);

        if (canaryRate <= errorRateThreshold)
        {
            return new CanaryDecision(
                CanaryVerdict.Advance,
                CanaryReasonCodes.Healthy,
                $"canary 5xx {canaryPercent} over {observation.CanaryRequests} requests (threshold {FormatPercent(errorRateThreshold)})");
        }

        // Over threshold. Decide whether the rollout is to blame before acting on it.
        if (observation.ExpectBaselineTraffic && observation.BaselineErrorRate is { } baselineRate
            && baselineRate >= errorRateThreshold)
        {
            return new CanaryDecision(
                CanaryVerdict.Pause,
                CanaryReasonCodes.BaselineDegraded,
                $"canary 5xx {canaryPercent} but baseline 5xx {FormatPercent(baselineRate)} — upstream degraded, rollout is not the cause");
        }

        var baselineText = observation.BaselineErrorRate is { } b
            ? $"baseline {FormatPercent(b)}"
            : "no baseline traffic";

        return new CanaryDecision(
            CanaryVerdict.Rollback,
            CanaryReasonCodes.CanaryDegraded,
            $"canary 5xx {canaryPercent} over {observation.CanaryRequests} requests, {baselineText} (threshold {FormatPercent(errorRateThreshold)})");
    }

    private static CanaryDecision HoldOrPause(
        CanaryObservation observation,
        int minSamples,
        int windowExtensions,
        int maxWindowExtensions)
    {
        var canarySilent = observation.CanaryRequests == 0;

        if (windowExtensions < maxWindowExtensions)
        {
            var detail = canarySilent
                ? $"canary received 0 requests in this window (needs {minSamples})"
                : $"canary {observation.CanaryRequests}/{minSamples}, baseline {observation.BaselineRequests}/{minSamples} requests — extending window {windowExtensions + 1}/{maxWindowExtensions}";

            return new CanaryDecision(CanaryVerdict.Hold, CanaryReasonCodes.InsufficientSamples, detail, ExtendWindow: true);
        }

        // Out of extensions. A canary that never received a single request is a different problem
        // from a canary that is simply quiet: the first almost always means the weights were never
        // applied (wrong policy, destination not routable), and it needs a different fix.
        return canarySilent
            ? new CanaryDecision(
                CanaryVerdict.Pause,
                CanaryReasonCodes.NoCanaryTraffic,
                $"canary received no traffic after {windowExtensions + 1} windows — check that LoadBalancingPolicy is Weighted and the destination is healthy")
            : new CanaryDecision(
                CanaryVerdict.Pause,
                CanaryReasonCodes.InsufficientSamples,
                $"still below {minSamples} samples after {windowExtensions + 1} windows (canary {observation.CanaryRequests}, baseline {observation.BaselineRequests}) — extend the window or lower minSamples");
    }

    private static string FormatPercent(double ratio)
        => (ratio * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%";
}
