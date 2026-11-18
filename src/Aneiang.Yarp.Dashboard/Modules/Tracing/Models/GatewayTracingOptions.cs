namespace Aneiang.Yarp.Dashboard.Modules.Tracing.Models;

/// <summary>
/// OpenTelemetry tracing configuration for the gateway.
/// </summary>
/// <remarks>
/// Disabled by default. Turning it on is a deliberate act because it decides where every request's
/// span data goes - and a gateway that starts exporting traces to an endpoint nobody configured is
/// both a surprise and a privacy problem.
/// </remarks>
public sealed class GatewayTracingOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Gateway:Dashboard:Tracing";

    /// <summary>Master switch. A restart is required: the tracer provider is built once at startup.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Exports spans to <see cref="Endpoint"/>. Set to <c>false</c> to keep spans only in the
    /// dashboard's in-memory buffer, which is how you look at a trace without standing up a collector.
    /// </summary>
    public bool ExportEnabled { get; set; } = true;

    /// <summary><c>service.name</c> resource attribute.</summary>
    public string ServiceName { get; set; } = "aneiang-gateway";

    /// <summary><c>service.version</c> resource attribute. Falls back to the assembly version.</summary>
    public string? ServiceVersion { get; set; }

    /// <summary><c>deployment.environment</c> resource attribute, e.g. <c>production</c>.</summary>
    public string? Environment { get; set; }

    /// <summary>OTLP collector endpoint. Port 4317 for gRPC, 4318 for HTTP.</summary>
    public string Endpoint { get; set; } = "http://localhost:4317";

    /// <summary><c>grpc</c> or <c>http/protobuf</c>.</summary>
    public string Protocol { get; set; } = "grpc";

    /// <summary>Extra OTLP headers, e.g. a Grafana Cloud API token.</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fraction of traces recorded, 0 to 1. Adjustable at runtime from the dashboard, because the
    /// right value changes with traffic: 1.0 while debugging, 0.01 once it is understood.
    /// </summary>
    public double SamplingRatio { get; set; } = 0.1;

    /// <summary>Give each plugin stage its own span. Turn off if the waterfall gets too wide.</summary>
    public bool RecordPluginSpans { get; set; } = true;

    /// <summary>Record the ASP.NET Core request span, which becomes the parent of the gateway span.</summary>
    public bool RecordAspNetCoreSpans { get; set; } = true;

    /// <summary>Record outbound <c>HttpClient</c> spans. Disable for WebSocket-heavy gateways.</summary>
    public bool RecordHttpClientSpans { get; set; } = true;

    /// <summary>How many completed traces the dashboard keeps in memory.</summary>
    public int RecentTraceCapacity { get; set; } = 100;

    /// <summary>Spans kept per trace before the rest are dropped as "truncated".</summary>
    public int MaxSpansPerTrace { get; set; } = 64;
}
