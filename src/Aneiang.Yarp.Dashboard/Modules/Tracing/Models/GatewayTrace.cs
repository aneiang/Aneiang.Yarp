namespace Aneiang.Yarp.Dashboard.Modules.Tracing.Models;

/// <summary>One span as the dashboard shows it: name, timing, status and attributes.</summary>
public sealed record GatewayTraceSpan
{
    /// <summary>Span id, unique within the trace.</summary>
    public string SpanId { get; init; } = string.Empty;

    /// <summary>Parent span id, or <c>null</c> for the root span.</summary>
    public string? ParentSpanId { get; init; }

    /// <summary>Span name, e.g. <c>gateway.forward</c> or <c>gateway.plugin waf</c>.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Activity source the span came from: <c>Aneiang.Yarp</c>, <c>Yarp.ReverseProxy</c>, …</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Span kind: Server, Client, Internal, Producer, Consumer.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Milliseconds from the start of the trace to the start of this span.</summary>
    public double StartOffsetMs { get; init; }

    /// <summary>Span duration in milliseconds.</summary>
    public double DurationMs { get; init; }

    /// <summary><c>unset</c>, <c>ok</c> or <c>error</c>.</summary>
    public string Status { get; init; } = "unset";

    /// <summary>Status description, which carries YARP's forwarding error on a failed hop.</summary>
    public string? StatusMessage { get; init; }

    /// <summary>Nesting depth in the waterfall, 0 for the root.</summary>
    public int Depth { get; init; }

    /// <summary>Span attributes, already stringified.</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>Enough of a trace to render a row in the list.</summary>
public sealed record GatewayTraceSummary
{
    /// <summary>W3C trace id, also the id used to look the trace up and to correlate with logs.</summary>
    public string TraceId { get; init; } = string.Empty;

    /// <summary>Name of the root span.</summary>
    public string RootName { get; init; } = string.Empty;

    /// <summary>When the earliest span started (UTC).</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>Wall-clock duration of the whole trace in milliseconds.</summary>
    public double DurationMs { get; init; }

    /// <summary><c>unset</c>, <c>ok</c> or <c>error</c> - taken from the root span.</summary>
    public string Status { get; init; } = "unset";

    /// <summary>HTTP status of the request, when the gateway span recorded one.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Resolved route id.</summary>
    public string? RouteId { get; init; }

    /// <summary>Resolved cluster id.</summary>
    public string? ClusterId { get; init; }

    /// <summary>Destination the load balancer chose.</summary>
    public string? DestinationId { get; init; }

    /// <summary>True when a plugin answered the request before it reached the forwarder.</summary>
    public bool ShortCircuited { get; init; }

    /// <summary>Number of spans retained for this trace.</summary>
    public int SpanCount { get; init; }

    /// <summary>True when spans were discarded because the per-trace cap was reached.</summary>
    public bool Truncated { get; init; }
}

/// <summary>A trace with its spans, ordered the way a waterfall renders them.</summary>
public sealed record GatewayTraceDetail
{
    /// <summary>List-level facts about the trace.</summary>
    public GatewayTraceSummary Summary { get; init; } = new();

    /// <summary>Spans ordered by start time, with <see cref="GatewayTraceSpan.Depth"/> filled in.</summary>
    public IReadOnlyList<GatewayTraceSpan> Spans { get; init; } = Array.Empty<GatewayTraceSpan>();
}
