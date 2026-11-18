using System.Diagnostics;
using System.Globalization;
using OpenTelemetry.Trace;

namespace Aneiang.Yarp.Dashboard.Modules.Tracing.Services;

/// <summary>
/// Samples a configurable fraction of traces, and lets the dashboard change that fraction
/// without a restart.
/// </summary>
/// <remarks>
/// Two decisions worth spelling out:
/// <list type="bullet">
/// <item><b>Deterministic, not random.</b> The decision is derived from the trace id, so the same
/// trace always gets the same verdict. Retries and repeated exports then agree with each other;
/// a <see cref="Random"/> sampler would record a span and drop its sibling.</item>
/// <item><b>Inbound decisions are honoured.</b> If a caller sent a <c>traceparent</c> with the
/// sampled flag, that decision wins over the local ratio. Overriding it would splice traces in
/// half at the gateway - the one place a trace is most useful.</item>
/// </list>
/// </remarks>
public sealed class RuntimeRatioSampler : Sampler
{
    private const int BasisPoints = 10_000;

    private int _basisPoints;
    private long _sampled;
    private long _dropped;

    /// <summary>Initializes a new instance of the <see cref="RuntimeRatioSampler"/> class.</summary>
    /// <param name="ratio">Initial sampling ratio between 0 and 1.</param>
    public RuntimeRatioSampler(double ratio) => Ratio = ratio;

    /// <summary>Fraction of traces recorded, 0 to 1. Thread-safe; applied to the next trace.</summary>
    public double Ratio
    {
        get => Volatile.Read(ref _basisPoints) / (double)BasisPoints;
        set
        {
            var clamped = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
            Volatile.Write(ref _basisPoints, (int)Math.Round(clamped * BasisPoints, MidpointRounding.AwayFromZero));
        }
    }

    /// <summary>Sampling decisions this sampler resolved to "record".</summary>
    /// <remarks>
    /// A decision, not a captured trace. The sampler is asked before the instrumentation filters
    /// run, so requests excluded by <c>AddAspNetCoreInstrumentation(options =&gt; options.Filter)</c>
    /// are counted here and then thrown away - they never end, never reach the buffer and are never
    /// exported. The dashboard labels these as decisions for that reason; "how many traces do I
    /// have" is answered by <see cref="TraceRingBuffer.Count"/>.
    /// </remarks>
    public long Sampled => Interlocked.Read(ref _sampled);

    /// <summary>Sampling decisions this sampler resolved to "drop". See <see cref="Sampled"/>.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <inheritdoc />
    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        var parentTraceId = samplingParameters.ParentContext.TraceId.ToHexString();
        if (parentTraceId != "00000000000000000000000000000000")
        {
            var recorded = (samplingParameters.ParentContext.TraceFlags & ActivityTraceFlags.Recorded) != 0;
            return recorded
                ? new SamplingResult(SamplingDecision.RecordAndSample)
                : new SamplingResult(SamplingDecision.Drop);
        }

        var basisPoints = Volatile.Read(ref _basisPoints);
        if (basisPoints <= 0)
        {
            Interlocked.Increment(ref _dropped);
            return new SamplingResult(SamplingDecision.Drop);
        }

        if (basisPoints >= BasisPoints)
        {
            Interlocked.Increment(ref _sampled);
            return new SamplingResult(SamplingDecision.RecordAndSample);
        }

        var bucket = Bucket(samplingParameters.TraceId);
        if (bucket < basisPoints)
        {
            Interlocked.Increment(ref _sampled);
            return new SamplingResult(SamplingDecision.RecordAndSample);
        }

        Interlocked.Increment(ref _dropped);
        return new SamplingResult(SamplingDecision.Drop);
    }

    /// <summary>Maps a trace id onto a stable bucket in <c>[0, 10000)</c>.</summary>
    private static int Bucket(ActivityTraceId traceId)
    {
        var hex = traceId.ToHexString();
        if (!ulong.TryParse(hex.AsSpan(0, Math.Min(16, hex.Length)), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var value))
            return 0;

        return (int)(value % BasisPoints);
    }
}
