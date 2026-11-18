using System.Diagnostics;
using Aneiang.Yarp.Dashboard.Modules.Tracing.Services;
using Aneiang.Yarp.Infrastructure.Observability;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Covers the in-memory trace buffer: ordering, nesting, and every bound that stops a pathological
/// trace from growing without limit.
/// </summary>
public class TraceRingBufferTests
{
    private const string Source = "test.tracing";

    [Fact]
    public void Child_spans_arriving_before_the_root_still_produce_a_complete_trace()
    {
        using var sink = new SourceSink(Source);
        var buffer = new TraceRingBuffer(new[] { Source }, capacity: 10, maxSpansPerTrace: 10);

        using var root = sink.Start("gateway.forward");
        var traceId = root.TraceId.ToHexString();

        using var child = sink.Start("gateway.plugin waf", root.Context);
        child.SetTag(GatewayTracing.PluginIdTag, "waf");

        // This is the real arrival order: a child span ends before its parent, and the root is
        // therefore the last thing the buffer ever sees.
        buffer.Add(child);
        buffer.Add(root);

        var detail = buffer.GetTrace(traceId);
        Assert.NotNull(detail);
        Assert.Equal(GatewayTracing.ForwardSpanName, detail!.Summary.RootName);
        Assert.Equal(2, detail.Summary.SpanCount);

        // Ordered by start time, so the root is first even though it was added last.
        Assert.Equal(GatewayTracing.ForwardSpanName, detail.Spans[0].Name);
        Assert.Equal(0, detail.Spans[0].Depth);
        Assert.Equal("gateway.plugin waf", detail.Spans[1].Name);
        Assert.Equal(1, detail.Spans[1].Depth);
        Assert.Equal(detail.Spans[0].SpanId, detail.Spans[1].ParentSpanId);
    }

    [Fact]
    public void Gateway_tags_are_surfaced_on_the_summary()
    {
        using var sink = new SourceSink(Source);
        var buffer = new TraceRingBuffer(new[] { Source }, capacity: 10, maxSpansPerTrace: 10);

        using var root = sink.Start("gateway.forward");
        root.SetTag(GatewayTracing.RouteIdTag, "route-1");
        root.SetTag(GatewayTracing.ClusterIdTag, "cluster-1");
        root.SetTag(GatewayTracing.DestinationIdTag, "d1");
        root.SetTag(GatewayTracing.StatusCodeTag, 503);
        root.SetTag(GatewayTracing.ShortCircuitedTag, false);
        var traceId = root.TraceId.ToHexString();
        buffer.Add(root);

        var summary = Assert.Single(buffer.GetTraces(10));

        Assert.Equal(traceId, summary.TraceId);
        Assert.Equal("route-1", summary.RouteId);
        Assert.Equal("cluster-1", summary.ClusterId);
        Assert.Equal("d1", summary.DestinationId);
        Assert.Equal(503, summary.StatusCode);
        Assert.False(summary.ShortCircuited);
    }

    [Fact]
    public void Trace_without_a_destination_is_reported_as_short_circuited()
    {
        using var sink = new SourceSink(Source);
        var buffer = new TraceRingBuffer(new[] { Source }, capacity: 10, maxSpansPerTrace: 10);

        using var root = sink.Start("gateway.forward");
        root.SetTag(GatewayTracing.ShortCircuitedTag, true);
        buffer.Add(root);

        Assert.True(Assert.Single(buffer.GetTraces(10)).ShortCircuited);
    }

    [Fact]
    public void Spans_beyond_the_cap_are_dropped_and_flagged()
    {
        using var sink = new SourceSink(Source);
        var buffer = new TraceRingBuffer(new[] { Source }, capacity: 10, maxSpansPerTrace: 2);

        using var root = sink.Start("gateway.forward");
        buffer.Add(root);
        for (var i = 0; i < 3; i++)
        {
            using var child = sink.Start($"child-{i}", root.Context);
            buffer.Add(child);
        }

        var detail = buffer.GetTrace(root.TraceId.ToHexString());
        Assert.NotNull(detail);
        Assert.Equal(2, detail!.Summary.SpanCount);
        Assert.True(detail.Summary.Truncated);

        // 1 root + 3 children, cap of 2: two kept, two dropped.
        Assert.Equal(2, buffer.SpansDropped);
    }

    [Fact]
    public void Oldest_trace_is_evicted_when_the_buffer_is_full()
    {
        using var sink = new SourceSink(Source);
        var buffer = new TraceRingBuffer(new[] { Source }, capacity: 2, maxSpansPerTrace: 10);

        string firstId;
        using (var first = sink.Start("gateway.forward"))
        {
            firstId = first.TraceId.ToHexString();
            buffer.Add(first);
        }

        for (var i = 0; i < 2; i++)
        {
            using var next = sink.Start("gateway.forward");
            buffer.Add(next);
        }

        Assert.Equal(2, buffer.Count);
        Assert.Null(buffer.GetTrace(firstId));
        Assert.Equal(1, buffer.TracesEvicted);
    }

    [Fact]
    public void Newest_trace_comes_first()
    {
        using var sink = new SourceSink(Source);
        var buffer = new TraceRingBuffer(new[] { Source }, capacity: 10, maxSpansPerTrace: 10);

        string older;
        using (var first = sink.Start("gateway.forward"))
        {
            older = first.TraceId.ToHexString();
            buffer.Add(first);
        }

        string newer;
        using (var second = sink.Start("gateway.forward"))
        {
            newer = second.TraceId.ToHexString();
            buffer.Add(second);
        }

        var traces = buffer.GetTraces(10);
        Assert.Equal(newer, traces[0].TraceId);
        Assert.Equal(older, traces[1].TraceId);
    }

    [Fact]
    public void Span_without_a_trace_id_is_ignored()
    {
        var buffer = new TraceRingBuffer(new[] { Source }, capacity: 10, maxSpansPerTrace: 10);

        // An activity that was never started has no trace id (all zeroes). Grouping every such
        // span under "000…0" would make one permanent trace that never stops growing.
        var unstarted = new Activity("no-trace");
        Assert.Equal("00000000000000000000000000000000", unstarted.TraceId.ToHexString());

        buffer.Add(unstarted);

        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void Only_configured_sources_are_accepted()
    {
        var buffer = new TraceRingBuffer(new[] { GatewayTracing.SourceName }, capacity: 10, maxSpansPerTrace: 10);

        Assert.True(buffer.Accepts(GatewayTracing.SourceName));
        Assert.False(buffer.Accepts("Microsoft.EntityFrameworkCore"));
    }

    [Fact]
    public void Unknown_trace_id_returns_null_instead_of_throwing()
    {
        var buffer = new TraceRingBuffer(new[] { Source }, capacity: 10, maxSpansPerTrace: 10);
        Assert.Null(buffer.GetTrace("ffffffffffffffffffffffffffffffff"));
        Assert.Null(buffer.GetTrace(""));
    }

    private sealed class SourceSink : IDisposable
    {
        private readonly ActivitySource _source;
        private readonly ActivityListener _listener;

        public SourceSink(string name)
        {
            _source = new ActivitySource(name);
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public Activity Start(string name, ActivityContext? parent = null)
        {
            var activity = parent is { } context
                ? _source.StartActivity(name, ActivityKind.Internal, context)
                : _source.StartActivity(name, ActivityKind.Server);

            Assert.NotNull(activity);
            return activity!;
        }

        public void Dispose()
        {
            _listener.Dispose();
            _source.Dispose();
        }
    }
}
