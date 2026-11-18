using System.Diagnostics;
using System.Globalization;
using Aneiang.Yarp.Dashboard.Modules.Tracing.Models;
using Aneiang.Yarp.Infrastructure.Observability;

namespace Aneiang.Yarp.Dashboard.Modules.Tracing.Services;

/// <summary>
/// Keeps the most recent completed traces in memory so the dashboard can render a waterfall
/// without anyone having to stand up Jaeger first.
/// </summary>
/// <remarks>
/// Two constraints shape this type:
/// <list type="number">
/// <item><b>Never retain an <see cref="Activity"/>.</b> An activity keeps the request's
/// <c>HttpContext</c> reachable through tags and baggage; holding a hundred of them would pin a
/// hundred request bodies. Every span is flattened into strings the moment it ends.</item>
/// <item><b>Spans arrive child-last-to-first.</b> A child span ends before its parent, so the root
/// arrives last and a trace is <em>always</em> incomplete while it is being filled. Nothing here
/// may assume the root has been seen.</item>
/// </list>
/// Bounded on every axis: trace count, spans per trace, tag count and tag length. A runaway
/// fan-out must cost a fixed number of bytes, not the process.
/// </remarks>
public sealed class TraceRingBuffer
{
    private const int MaxTagsPerSpan = 24;
    private const int MaxTagValueLength = 300;

    private readonly object _gate = new();
    private readonly List<TraceBuilder> _traces = new();
    private readonly Dictionary<string, TraceBuilder> _index = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sources;
    private readonly int _capacity;
    private readonly int _maxSpansPerTrace;

    private long _spansCaptured;
    private long _spansDropped;
    private long _tracesEvicted;

    /// <summary>Initializes a new instance of the <see cref="TraceRingBuffer"/> class.</summary>
    /// <param name="sources">Activity source names worth keeping; everything else is ignored.</param>
    /// <param name="capacity">Traces retained; the oldest is evicted.</param>
    /// <param name="maxSpansPerTrace">Spans retained per trace before the rest are counted as dropped.</param>
    public TraceRingBuffer(IEnumerable<string> sources, int capacity, int maxSpansPerTrace)
    {
        _sources = new HashSet<string>(sources, StringComparer.Ordinal);
        _capacity = Math.Max(1, capacity);
        _maxSpansPerTrace = Math.Max(1, maxSpansPerTrace);
    }

    /// <summary>Traces currently held.</summary>
    public int Count
    {
        get { lock (_gate) return _traces.Count; }
    }

    /// <summary>Spans stored since the process started, or since the last <see cref="Clear"/>.</summary>
    public long SpansCaptured => Interlocked.Read(ref _spansCaptured);

    /// <summary>Spans discarded because a trace hit <c>MaxSpansPerTrace</c>. Reset by <see cref="Clear"/>.</summary>
    public long SpansDropped => Interlocked.Read(ref _spansDropped);

    /// <summary>Traces evicted because the buffer was full. Reset by <see cref="Clear"/>.</summary>
    public long TracesEvicted => Interlocked.Read(ref _tracesEvicted);

    /// <summary>Whether spans from this source are retained.</summary>
    /// <remarks>
    /// Two allowances beyond the configured list, both learned the hard way from a real waterfall:
    /// <list type="bullet">
    /// <item><b>The empty source name.</b> ASP.NET Core's request activity is created directly
    /// (<c>new Activity("…HttpRequestIn")</c>) rather than through an <see cref="ActivitySource"/>,
    /// so it reports no source. It is the real root of every gateway trace, and dropping it leaves
    /// the whole waterfall orphaned.</item>
    /// <item><b>The framework namespace.</b> Prefix-matching avoids pinning a source name that
    /// changes between ASP.NET Core versions.</item>
    /// </list>
    /// </remarks>
    public bool Accepts(string sourceName)
        => sourceName.Length == 0
           || _sources.Contains(sourceName)
           || sourceName.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal);

    /// <summary>Stores one finished span. Cheap and lock-bounded; safe to call from any thread.</summary>
    public void Add(Activity activity)
    {
        // A trace id of all zeroes means no trace context was created at all (a span started
        // outside any sampled trace). Grouping those together would produce one endless trace.
        var traceId = activity.TraceId.ToHexString();
        if (string.IsNullOrEmpty(traceId) || traceId == "00000000000000000000000000000000")
            return;

        var snapshot = Snapshot(activity);

        lock (_gate)
        {
            if (!_index.TryGetValue(traceId, out var builder))
            {
                builder = new TraceBuilder(traceId, _maxSpansPerTrace);
                _index[traceId] = builder;
                _traces.Insert(0, builder);
                TrimLocked();
            }

            if (!builder.TryAdd(snapshot))
            {
                Interlocked.Increment(ref _spansDropped);
                return;
            }
        }

        Interlocked.Increment(ref _spansCaptured);
    }

    /// <summary>Most recent traces, newest first.</summary>
    public IReadOnlyList<GatewayTraceSummary> GetTraces(int limit)
    {
        List<TraceBuilder> copy;
        lock (_gate)
        {
            var take = Math.Min(Math.Max(limit, 1), _traces.Count);
            copy = _traces.Take(take).ToList();
        }

        return copy.Select(x => x.ToDetail().Summary).ToList();
    }

    /// <summary>One trace with its spans, or <c>null</c> when it has already been evicted.</summary>
    public GatewayTraceDetail? GetTrace(string traceId)
    {
        TraceBuilder? builder;
        lock (_gate)
        {
            _index.TryGetValue(traceId, out builder);
        }

        return builder?.ToDetail();
    }

    /// <summary>Drops everything held. Used by the dashboard's "clear" button.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _traces.Clear();
            _index.Clear();

            // The counters describe what this buffer holds, so a manual clear resets them as well.
            // Leaving them behind makes the page read "0 traces / 20 spans captured", which looks
            // like a bug in the numbers rather than the result of the click.
            Interlocked.Exchange(ref _spansCaptured, 0);
            Interlocked.Exchange(ref _spansDropped, 0);
            Interlocked.Exchange(ref _tracesEvicted, 0);
        }
    }

    private void TrimLocked()
    {
        while (_traces.Count > _capacity)
        {
            var oldest = _traces[^1];
            _traces.RemoveAt(_traces.Count - 1);
            _index.Remove(oldest.TraceId);
            Interlocked.Increment(ref _tracesEvicted);
        }
    }

    private static SpanSnapshot Snapshot(Activity activity)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in activity.TagObjects)
        {
            if (tags.Count >= MaxTagsPerSpan)
                break;
            if (tag.Key is null)
                continue;

            var value = tag.Value switch
            {
                null => null,
                string text => text,
                _ => Convert.ToString(tag.Value, CultureInfo.InvariantCulture)
            };

            if (string.IsNullOrEmpty(value))
                continue;

            tags[tag.Key] = value.Length > MaxTagValueLength ? value[..MaxTagValueLength] + "…" : value;
        }

        return new SpanSnapshot
        {
            SpanId = activity.SpanId.ToHexString(),
            // An unparented span reports an all-zero parent id rather than null.
            ParentSpanId = activity.ParentSpanId.ToHexString() is var parent && parent == "0000000000000000"
                ? null
                : parent,
            Name = activity.DisplayName,
            Source = activity.Source.Name,
            Kind = activity.Kind.ToString(),
            // StartTimeUtc is already UTC; wrap it without an offset conversion.
            StartedAt = new DateTimeOffset(activity.StartTimeUtc, TimeSpan.Zero),
            DurationMs = activity.Duration.TotalMilliseconds,
            Status = activity.Status switch
            {
                ActivityStatusCode.Ok => "ok",
                ActivityStatusCode.Error => "error",
                _ => "unset"
            },
            StatusMessage = activity.StatusDescription,
            Tags = tags
        };
    }

    private sealed class TraceBuilder
    {
        private readonly int _maxSpans;

        public TraceBuilder(string traceId, int maxSpans)
        {
            TraceId = traceId;
            _maxSpans = maxSpans;
        }

        public string TraceId { get; }

        public List<SpanSnapshot> Spans { get; } = new();

        public bool Truncated { get; private set; }

        public bool TryAdd(SpanSnapshot span)
        {
            if (Spans.Count >= _maxSpans)
            {
                Truncated = true;
                return false;
            }

            Spans.Add(span);
            return true;
        }

        public GatewayTraceDetail ToDetail()
        {
            // Copy under the caller's lock; ordering and depth belong to the reader.
            var spans = Spans.ToList();

            var start = spans.Count == 0 ? DateTimeOffset.UtcNow : spans.Min(x => x.StartedAt);
            var end = spans.Count == 0 ? start : spans.Max(x => x.StartedAt.AddMilliseconds(x.DurationMs));

            var gatewaySpan = spans.FirstOrDefault(x =>
                string.Equals(x.Name, GatewayTracing.ForwardSpanName, StringComparison.Ordinal));

            var depths = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var span in spans)
                depths[span.SpanId] = DepthOf(span, spans);

            // The real root is usually the ASP.NET Core request span, but it can be absent (not
            // sampled by the framework, or a request that never entered the host pipeline). Falling
            // back to "the first span in an unordered list" produced a waterfall whose title was a
            // random child, so pick the shallowest span and prefer the gateway's own.
            var roots = spans.Where(x => depths[x.SpanId] == 0).ToList();
            var root = roots.FirstOrDefault(x => string.Equals(x.Name, GatewayTracing.ForwardSpanName, StringComparison.Ordinal))
                       ?? roots.OrderBy(x => x.StartedAt).FirstOrDefault()
                       ?? spans.FirstOrDefault();

            var statusCode = gatewaySpan is not null
                             && gatewaySpan.Tags.TryGetValue(GatewayTracing.StatusCodeTag, out var codeText)
                             && int.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (int?)null;

            var summary = new GatewayTraceSummary
            {
                TraceId = TraceId,
                RootName = root?.Name ?? string.Empty,
                StartedAt = start,
                DurationMs = Math.Max(0, (end - start).TotalMilliseconds),
                Status = ResolveStatus(root, gatewaySpan, statusCode),
                StatusCode = statusCode,
                RouteId = TagOf(gatewaySpan, GatewayTracing.RouteIdTag),
                ClusterId = TagOf(gatewaySpan, GatewayTracing.ClusterIdTag),
                DestinationId = TagOf(gatewaySpan, GatewayTracing.DestinationIdTag),
                // Tag values are stringified with Convert.ToString, which renders a bool as "True";
                // parse instead of comparing to a literal so the casing cannot silently mislead.
                ShortCircuited = bool.TryParse(TagOf(gatewaySpan, GatewayTracing.ShortCircuitedTag), out var shortCircuited)
                                 && shortCircuited,
                SpanCount = spans.Count,
                Truncated = Truncated
            };

            var ordered = spans
                .OrderBy(x => x.StartedAt)
                .ThenByDescending(x => x.DurationMs)
                .Select(x => new GatewayTraceSpan
                {
                    SpanId = x.SpanId,
                    ParentSpanId = x.ParentSpanId,
                    Name = x.Name,
                    Source = x.Source,
                    Kind = x.Kind,
                    StartOffsetMs = (x.StartedAt - start).TotalMilliseconds,
                    DurationMs = x.DurationMs,
                    Status = x.Status,
                    StatusMessage = x.StatusMessage,
                    Depth = depths[x.SpanId],
                    Tags = x.Tags
                })
                .ToList();

            return new GatewayTraceDetail { Summary = summary, Spans = ordered };
        }

        /// <summary>
        /// A 5xx is a failed request regardless of which span noticed it: the gateway span can report
        /// 503 while the root span is still "unset", and the list would otherwise show a green row.
        /// </summary>
        private static string ResolveStatus(SpanSnapshot? root, SpanSnapshot? gatewaySpan, int? statusCode)
        {
            if (root?.Status == "error" || gatewaySpan?.Status == "error" || statusCode >= 500)
                return "error";

            return root?.Status ?? gatewaySpan?.Status ?? "unset";
        }

        private static string? TagOf(SpanSnapshot? span, string key)
            => span is not null && span.Tags.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

        /// <summary>Walks up the parent chain; bounded so a malformed graph cannot loop forever.</summary>
        private static int DepthOf(SpanSnapshot span, List<SpanSnapshot> all)
        {
            var depth = 0;
            var current = span;
            while (current.ParentSpanId is not null && depth < 32)
            {
                var parent = all.FirstOrDefault(x => x.SpanId == current.ParentSpanId);
                if (parent is null)
                    break;
                current = parent;
                depth++;
            }

            return depth;
        }
    }

    private sealed class SpanSnapshot
    {
        public string SpanId { get; init; } = string.Empty;
        public string? ParentSpanId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public DateTimeOffset StartedAt { get; init; }
        public double DurationMs { get; init; }
        public string Status { get; init; } = "unset";
        public string? StatusMessage { get; init; }
        public Dictionary<string, string> Tags { get; init; } = new(StringComparer.Ordinal);
    }
}
