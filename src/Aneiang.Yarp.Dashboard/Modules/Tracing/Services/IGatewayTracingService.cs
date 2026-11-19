using Aneiang.Yarp.Dashboard.Modules.Tracing.Models;

namespace Aneiang.Yarp.Dashboard.Modules.Tracing.Services;

/// <summary>Live state of the gateway's tracing setup, as shown on the tracing page.</summary>
public sealed record GatewayTracingStatus
{
    /// <summary>Whether tracing is configured on.</summary>
    public bool Enabled { get; init; }

    /// <summary>Whether a tracer provider was actually built.</summary>
    public bool Running { get; init; }

    /// <summary>Whether spans are exported to <see cref="Endpoint"/> (false = in-memory only).</summary>
    public bool ExportEnabled { get; init; }

    /// <summary>Configured <c>service.name</c>.</summary>
    public string ServiceName { get; init; } = string.Empty;

    /// <summary>OTLP endpoint, or <c>null</c> when export is off.</summary>
    public string? Endpoint { get; init; }

    /// <summary><c>grpc</c> or <c>http/protobuf</c>.</summary>
    public string Protocol { get; init; } = string.Empty;

    /// <summary>Current sampling ratio, which may differ from configuration if changed at runtime.</summary>
    public double SamplingRatio { get; init; }

    /// <summary>
    /// Sampling decisions the sampler resolved to "record" since start. Not a trace count: requests
    /// rejected by an instrumentation filter were already sampled, so they are counted here and
    /// then discarded.
    /// </summary>
    public long SampledTraces { get; init; }

    /// <summary>Sampling decisions the sampler resolved to "drop" since start. See <see cref="SampledTraces"/>.</summary>
    public long DroppedTraces { get; init; }

    /// <summary>Whether each plugin stage gets a span.</summary>
    public bool RecordPluginSpans { get; init; }

    /// <summary>Whether the ASP.NET Core request span is recorded.</summary>
    public bool RecordAspNetCoreSpans { get; init; }

    /// <summary>Whether outbound HttpClient spans are recorded.</summary>
    public bool RecordHttpClientSpans { get; init; }

    /// <summary>Why the provider failed to start, or <c>null</c>.</summary>
    public string? StartError { get; init; }

    /// <summary>Traces the buffer can hold.</summary>
    public int BufferCapacity { get; init; }

    /// <summary>Traces currently held.</summary>
    public int BufferCount { get; init; }

    /// <summary>Spans stored since start.</summary>
    public long SpansCaptured { get; init; }

    /// <summary>Spans dropped because a trace hit its span cap.</summary>
    public long SpansDropped { get; init; }

    /// <summary>Traces evicted because the buffer was full.</summary>
    public long TracesEvicted { get; init; }

    /// <summary>Activity sources retained in the buffer.</summary>
    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();

    /// <summary>Configuration section the settings come from, so the page can show where to edit.</summary>
    public string SectionName { get; init; } = string.Empty;

    /// <summary>True when the configuration changed but the provider needs a restart to pick it up.</summary>
    public bool RestartRequired { get; init; }

    /// <summary>
    /// Whether exports carry authentication headers, which ones, and where each value came from.
    /// Values are always masked — the page never receives a credential.
    /// </summary>
    public TracingAuthStatus Auth { get; init; } =
        new(false, 0, Array.Empty<TracingAuthHeader>(), string.Empty, null);
}

/// <summary>Read/write access to the gateway's tracing state.</summary>
public interface IGatewayTracingService
{
    /// <summary>Current status, including live sampler counters.</summary>
    GatewayTracingStatus GetStatus();

    /// <summary>Most recent traces, newest first.</summary>
    IReadOnlyList<GatewayTraceSummary> GetTraces(int limit);

    /// <summary>One trace with its spans, or <c>null</c> when evicted or unknown.</summary>
    GatewayTraceDetail? GetTrace(string traceId);

    /// <summary>Changes the sampling ratio for new traces. Returns false with a reason when invalid.</summary>
    bool TrySetSamplingRatio(double ratio, out string? error);

    /// <summary>Empties the in-memory buffer.</summary>
    void ClearTraces();
}
