using System.Diagnostics;
using OpenTelemetry;

namespace Aneiang.Yarp.Dashboard.Modules.Tracing.Services;

/// <summary>
/// Feeds finished spans into the dashboard's in-memory trace buffer.
/// </summary>
/// <remarks>
/// This is a processor rather than a second <see cref="ActivityListener"/> on purpose. A listener
/// would have to answer the sampling question itself, and two listeners that disagree resolve to
/// "record" - which would force every span into memory and quietly defeat the sampling ratio that
/// the exporter is honouring. As a processor it sees exactly the spans that were sampled, so the
/// dashboard shows what was actually traced.
/// </remarks>
public sealed class RecentTraceProcessor : BaseProcessor<Activity>
{
    private readonly TraceRingBuffer _buffer;

    /// <summary>Initializes a new instance of the <see cref="RecentTraceProcessor"/> class.</summary>
    /// <param name="buffer">Buffer that retains the traces.</param>
    public RecentTraceProcessor(TraceRingBuffer buffer) => _buffer = buffer;

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        // A dropped trace still produces an activity (marked "propagation only" so trace context
        // keeps flowing). It must not land in the buffer: the page states that what it shows is
        // what was traced, and that only holds if unsampled spans are filtered out here.
        if (!data.Recorded)
            return;

        if (!_buffer.Accepts(data.Source.Name))
            return;

        _buffer.Add(data);
    }
}
