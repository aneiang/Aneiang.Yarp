using Aneiang.Yarp.Models;

namespace Aneiang.Yarp.Plugin.FaultInjection;

/// <summary>What a request should have done to it.</summary>
public enum FaultInjectionFailure
{
    /// <summary>Forward the request normally.</summary>
    None,

    /// <summary>Answer with an HTTP status code instead of forwarding.</summary>
    StatusCode,

    /// <summary>Abort the connection without answering.</summary>
    Abort
}

/// <summary>
/// The outcome of evaluating a <see cref="FaultInjectionExecutionConfig"/> for one request.
/// </summary>
/// <param name="Delay">How long to wait before answering or forwarding.</param>
/// <param name="Failure">Whether to answer locally, abort, or forward.</param>
/// <param name="StatusCode">Status code for <see cref="FaultInjectionFailure.StatusCode"/>.</param>
/// <param name="Body">Response body for <see cref="FaultInjectionFailure.StatusCode"/>.</param>
/// <param name="Announce">Whether to add <c>X-Fault-Injected</c> to the response.</param>
public readonly record struct FaultInjectionPlan(
    TimeSpan Delay,
    FaultInjectionFailure Failure,
    int StatusCode,
    string Body,
    bool Announce)
{
    /// <summary>The request should be forwarded untouched.</summary>
    public bool IsNoOp => Delay == TimeSpan.Zero && Failure == FaultInjectionFailure.None;
}

/// <summary>
/// Decides whether a request is hit by a fault, and with what.
/// </summary>
/// <remarks>
/// Separated from the middleware so the decision can be reviewed and tested without an
/// <c>HttpContext</c>: this is the part where a bug either breaks production traffic or makes a
/// drill silently do nothing, and both are worth reading as plain code.
///
/// Three gates, in this order, and the order is the point:
/// <list type="number">
/// <item><b>Enabled.</b> Off means off; nothing below runs.</item>
/// <item><b>Header gate.</b> A configured header turns the drill into an opt-in: production
/// traffic keeps working while you inject faults with one extra request header.</item>
/// <item><b>Probability.</b> A fraction only, so "10% of checkout fails" is expressible without
/// taking the route down to prove a point.</item>
/// </list>
/// Randomness is supplied by the caller, which keeps the probability test exhaustive instead of
/// statistical.
/// </remarks>
public static class FaultInjectionPlanner
{
    /// <summary>Smallest status code that can be injected; anything lower is a client mistake.</summary>
    public const int MinStatusCode = 400;

    /// <summary>Largest status code that can be injected.</summary>
    public const int MaxStatusCode = 599;

    /// <summary>Default body used when a status code is injected without an explicit body.</summary>
    public const string DefaultBody =
        "{\"error\":\"InjectedFailure\",\"message\":\"This response was injected by the gateway for a fault drill.\"}";

    /// <summary>Response header that marks an injected fault.</summary>
    public const string AnnounceHeader = "X-Fault-Injected";

    /// <summary>Upper bound for an injected delay, so a typo cannot park a request for an hour.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Evaluates one request.
    /// </summary>
    /// <param name="config">The route's fault injection config.</param>
    /// <param name="headerReader">Reads a request header by name; returns <c>null</c> when absent.</param>
    /// <param name="sample">A uniform sample in <c>[0, 1)</c> used for the probability gate.</param>
    public static FaultInjectionPlan Plan(
        FaultInjectionExecutionConfig config,
        Func<string, string?> headerReader,
        double sample)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!config.Enabled)
            return default;

        if (!HeaderGatePasses(config, headerReader))
            return default;

        if (!ProbabilityGatePasses(config.Probability, sample))
            return default;

        var delay = ResolveDelay(config.DelayMinMs, config.DelayMaxMs, sample);
        var failure = ResolveFailure(config);

        // Enabled with nothing configured is a drill that forgot to describe itself. Answering a
        // request with a 200 and no delay would be worse than doing nothing: it would look like a
        // passing drill.
        if (delay == TimeSpan.Zero && failure == FaultInjectionFailure.None)
            return default;

        return new FaultInjectionPlan(
            delay,
            failure,
            failure == FaultInjectionFailure.StatusCode ? config.ErrorStatusCode : 0,
            failure == FaultInjectionFailure.StatusCode
                ? (string.IsNullOrWhiteSpace(config.ErrorBody) ? DefaultBody : config.ErrorBody)
                : string.Empty,
            config.Announce);
    }

    private static bool HeaderGatePasses(FaultInjectionExecutionConfig config, Func<string, string?> headerReader)
    {
        if (string.IsNullOrWhiteSpace(config.RequiredHeader))
            return true;

        var value = headerReader(config.RequiredHeader.Trim());
        if (value is null)
            return false;

        return string.IsNullOrWhiteSpace(config.RequiredHeaderValue)
               || string.Equals(value, config.RequiredHeaderValue.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProbabilityGatePasses(double probability, double sample)
    {
        if (double.IsNaN(probability) || probability <= 0)
            return false;

        // 1.0 short-circuits so a full drill does not depend on how the sample was generated.
        if (probability >= 1)
            return true;

        return sample < probability;
    }

    private static TimeSpan ResolveDelay(int minMs, int maxMs, double sample)
    {
        var min = Math.Max(0, minMs);
        var max = Math.Max(0, maxMs);

        if (min == 0 && max == 0)
            return TimeSpan.Zero;

        // A single bound, or a reversed range, means "exactly this much": a range is a nicety, and
        // guessing which end the author meant is worse than honouring the number they wrote.
        if (max <= min)
            return Clamp(TimeSpan.FromMilliseconds(min));

        var span = max - min;
        var offset = (int)Math.Floor(sample * span);
        return Clamp(TimeSpan.FromMilliseconds(min + offset));
    }

    private static FaultInjectionFailure ResolveFailure(FaultInjectionExecutionConfig config)
    {
        // Abort wins over a status code: asking for a transport failure and a body at the same time
        // is contradictory, and dropping the connection is the more disruptive of the two.
        if (config.AbortConnection)
            return FaultInjectionFailure.Abort;

        if (config.ErrorStatusCode is >= MinStatusCode and <= MaxStatusCode)
            return FaultInjectionFailure.StatusCode;

        return FaultInjectionFailure.None;
    }

    private static TimeSpan Clamp(TimeSpan delay) => delay > MaxDelay ? MaxDelay : delay;
}
