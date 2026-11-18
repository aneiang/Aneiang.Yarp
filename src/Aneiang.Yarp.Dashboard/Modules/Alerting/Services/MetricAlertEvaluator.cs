using System.Globalization;
using Aneiang.Yarp.Dashboard.Modules.Alerting.Models;

namespace Aneiang.Yarp.Dashboard.Modules.Alerting.Services;

/// <summary>One measurement of one metric over one window.</summary>
/// <param name="Value">The metric in its own unit (ratio, seconds, count).</param>
/// <param name="Samples">How many observations the measurement is based on.</param>
/// <param name="ObservedSeconds">How much wall-clock time the window actually covered.</param>
/// <param name="WindowReady">
/// False while the gateway has not been collecting for a full window yet. A 60-second rate cannot
/// be claimed from 15 seconds of traffic, and quietly doing so is how a deploy (empty counters,
/// one failure) turns into a page.
/// </param>
public readonly record struct AlertSample(
    double Value,
    long Samples,
    int ObservedSeconds,
    bool WindowReady = true);

/// <summary>What a single measurement means for a rule.</summary>
public enum AlertSignalKind
{
    /// <summary>Below the threshold, or the metric is fine.</summary>
    Healthy = 0,

    /// <summary>Past the threshold.</summary>
    Breaching = 1,

    /// <summary>Not enough traffic to judge. Neither healthy nor breaching — it holds the state.</summary>
    InsufficientData = 2
}

/// <summary>The verdict for one rule at one instant, plus a human-readable reason.</summary>
/// <param name="Kind">Verdict.</param>
/// <param name="Value">Measured value, for the notification body.</param>
/// <param name="Samples">Samples behind the value, for the notification body.</param>
/// <param name="Code">Stable machine code (<c>breaching</c>, <c>healthy</c>, <c>lowSamples</c>).</param>
/// <param name="Reason">One sentence explaining the verdict.</param>
public readonly record struct AlertSignal(
    AlertSignalKind Kind,
    double Value,
    long Samples,
    string Code,
    string Reason);

/// <summary>
/// Turns a measurement into a verdict. Pure: no clock, no IO, no state.
/// </summary>
/// <remarks>
/// Deliberately separate from the incident state machine. "Is this number bad" and "has it been bad
/// long enough to wake someone up" are different questions, and only the first one is arithmetic.
/// </remarks>
public static class MetricAlertEvaluator
{
    /// <summary>
    /// Compares <paramref name="sample"/> against <paramref name="rule"/>.
    /// </summary>
    /// <remarks>
    /// Rate and latency rules breach on <c>&gt;</c>: a 5% threshold means "5% is still acceptable",
    /// which is also what a dashboard reader assumes when the line is drawn at 5%. The circuit rule
    /// uses <c>&gt;=</c> because it counts discrete things — a threshold of 1 means "one open
    /// circuit is already the alert", and <c>&gt;</c> would make that setting unable to ever fire.
    /// </remarks>
    public static AlertSignal Evaluate(MetricAlertRule rule, AlertSample sample)
    {
        if (!sample.WindowReady)
        {
            return new AlertSignal(
                AlertSignalKind.InsufficientData,
                sample.Value,
                sample.Samples,
                "windowNotReady",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"only {sample.ObservedSeconds}s of history for a {rule.WindowSeconds}s window"));
        }

        if (sample.Samples < rule.MinSamples)
        {
            return new AlertSignal(
                AlertSignalKind.InsufficientData,
                sample.Value,
                sample.Samples,
                "lowSamples",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"only {sample.Samples} samples over {sample.ObservedSeconds}s, {rule.MinSamples} required"));
        }

        var breached = rule.Kind switch
        {
            MetricAlertKind.ErrorRate => sample.Value > rule.Threshold,
            MetricAlertKind.LatencyP99 => sample.Value > rule.Threshold,
            MetricAlertKind.CircuitOpen => sample.Value >= rule.Threshold,
            _ => false
        };

        if (!breached)
        {
            return new AlertSignal(
                AlertSignalKind.Healthy,
                sample.Value,
                sample.Samples,
                "healthy",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Format(rule.Kind, sample.Value)} within {Format(rule.Kind, rule.Threshold)} over {sample.ObservedSeconds}s ({sample.Samples} samples)"));
        }

        return new AlertSignal(
            AlertSignalKind.Breaching,
            sample.Value,
            sample.Samples,
            "breaching",
            string.Create(
                CultureInfo.InvariantCulture,
                $"{Format(rule.Kind, sample.Value)} past {Format(rule.Kind, rule.Threshold)} over {sample.ObservedSeconds}s ({sample.Samples} samples)"));
    }

    /// <summary>Formats a value in the metric's own unit, for logs, notifications and the UI.</summary>
    public static string Format(MetricAlertKind kind, double value) => kind switch
    {
        // Latency under a second reads better in milliseconds — "0.32s" makes people do arithmetic.
        MetricAlertKind.LatencyP99 => value < 1
            ? (value * 1000).ToString("0", CultureInfo.InvariantCulture) + "ms"
            : value.ToString("0.##", CultureInfo.InvariantCulture) + "s",
        MetricAlertKind.ErrorRate => (value * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%",
        _ => value.ToString("0.##", CultureInfo.InvariantCulture)
    };

    /// <summary>Compact label for the metric, used in notification titles.</summary>
    public static string DescribeKind(MetricAlertKind kind) => kind switch
    {
        MetricAlertKind.ErrorRate => "5xx rate",
        MetricAlertKind.LatencyP99 => "p99 latency",
        MetricAlertKind.CircuitOpen => "open circuits",
        _ => kind.ToString()
    };
}
