using Aneiang.Yarp.Dashboard.Modules.Tracing.Models;

namespace Aneiang.Yarp.Dashboard.Modules.Tracing.Services;

/// <inheritdoc />
public sealed class GatewayTracingService : IGatewayTracingService
{
    private const int MaxTracesPerQuery = 200;

    private readonly GatewayTracingOptions _options;
    private readonly RuntimeRatioSampler _sampler;
    private readonly TraceRingBuffer _buffer;
    private readonly GatewayTracingHost _host;

    /// <summary>Initializes a new instance of the <see cref="GatewayTracingService"/> class.</summary>
    public GatewayTracingService(
        GatewayTracingOptions options,
        RuntimeRatioSampler sampler,
        TraceRingBuffer buffer,
        GatewayTracingHost host)
    {
        _options = options;
        _sampler = sampler;
        _buffer = buffer;
        _host = host;
    }

    /// <inheritdoc />
    public GatewayTracingStatus GetStatus() => new()
    {
        Enabled = _options.Enabled,
        Running = _host.IsRunning,
        ExportEnabled = _options.ExportEnabled,
        ServiceName = _options.ServiceName,
        Endpoint = _options.ExportEnabled ? _options.Endpoint : null,
        Protocol = _options.Protocol,
        SamplingRatio = _sampler.Ratio,
        SampledTraces = _sampler.Sampled,
        DroppedTraces = _sampler.Dropped,
        RecordPluginSpans = _options.RecordPluginSpans,
        RecordAspNetCoreSpans = _options.RecordAspNetCoreSpans,
        RecordHttpClientSpans = _options.RecordHttpClientSpans,
        StartError = _host.StartError,
        BufferCapacity = _options.RecentTraceCapacity,
        BufferCount = _buffer.Count,
        SpansCaptured = _buffer.SpansCaptured,
        SpansDropped = _buffer.SpansDropped,
        TracesEvicted = _buffer.TracesEvicted,
        Sources = GatewayTracingHost.BufferSources,
        SectionName = GatewayTracingOptions.SectionName,
        RestartRequired = _options.Enabled != _host.IsRunning
    };

    /// <inheritdoc />
    public IReadOnlyList<GatewayTraceSummary> GetTraces(int limit)
        => _buffer.GetTraces(Math.Clamp(limit, 1, MaxTracesPerQuery));

    /// <inheritdoc />
    public GatewayTraceDetail? GetTrace(string traceId)
        => string.IsNullOrWhiteSpace(traceId) ? null : _buffer.GetTrace(traceId.Trim());

    /// <inheritdoc />
    public bool TrySetSamplingRatio(double ratio, out string? error)
    {
        if (double.IsNaN(ratio) || ratio < 0 || ratio > 1)
        {
            error = "sampling ratio must be between 0 and 1";
            return false;
        }

        _sampler.Ratio = ratio;
        error = null;
        return true;
    }

    /// <inheritdoc />
    public void ClearTraces() => _buffer.Clear();
}
