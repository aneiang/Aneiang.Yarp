using System.Globalization;
using Aneiang.Yarp.Dashboard.Modules.Tracing.Models;
using Aneiang.Yarp.Infrastructure.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Aneiang.Yarp.Dashboard.Modules.Tracing.Services;

/// <summary>
/// Builds the OpenTelemetry tracer provider at startup and owns its lifetime.
/// </summary>
/// <remarks>
/// The provider is created here rather than through <c>AddOpenTelemetry()</c> for one reason:
/// <b>the exporter must not exist when tracing is off.</b> A tracer provider registered
/// unconditionally will happily try to reach a collector nobody configured, and the resulting
/// connection errors are indistinguishable from a real upstream failure at 3am.
///
/// A start failure is captured, not thrown: a gateway should not refuse to boot because its
/// tracing endpoint is unreachable. The message is surfaced on the tracing page instead.
/// </remarks>
public sealed class GatewayTracingHost : IHostedService, IAsyncDisposable
{
    /// <summary>
    /// Activity sources kept in the in-memory buffer. Restricting this keeps the waterfall about
    /// the request; without it the buffer fills with unrelated hosted-service spans.
    /// </summary>
    public static IReadOnlyList<string> BufferSources { get; } = new[]
    {
        GatewayTracing.SourceName,
        "Yarp.ReverseProxy",
        "Yarp.ReverseProxy.Forwarder",
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Server.Kestrel",
        "System.Net.Http"
    };

    private readonly GatewayTracingOptions _options;
    private readonly RuntimeRatioSampler _sampler;
    private readonly TraceRingBuffer _buffer;
    private readonly ILogger<GatewayTracingHost> _logger;
    private readonly string _controlPlanePrefix;

    private TracerProvider? _provider;

    /// <summary>Initializes a new instance of the <see cref="GatewayTracingHost"/> class.</summary>
    public GatewayTracingHost(
        GatewayTracingOptions options,
        RuntimeRatioSampler sampler,
        TraceRingBuffer buffer,
        IOptions<Infrastructure.DashboardOptions> dashboardOptions,
        ILogger<GatewayTracingHost> logger)
    {
        _options = options;
        _sampler = sampler;
        _buffer = buffer;
        _logger = logger;
        _controlPlanePrefix = "/" + (dashboardOptions.Value.RoutePrefix ?? "apigateway").Trim('/');
    }

    /// <summary>True once a tracer provider was built successfully.</summary>
    public bool IsRunning => _provider is not null;

    /// <summary>Why the provider could not be built, or <c>null</c>. Shown on the tracing page.</summary>
    public string? StartError { get; private set; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The core library reads this per request; setting it here keeps the plugin-span decision
        // in one place instead of threading options through the pipeline builder.
        GatewayTracing.PluginSpansEnabled = _options.RecordPluginSpans;
        _sampler.Ratio = _options.SamplingRatio;

        if (!_options.Enabled)
        {
            _logger.LogInformation(
                "OpenTelemetry tracing is disabled. Set {Section}:Enabled=true to export spans.",
                GatewayTracingOptions.SectionName);
            return Task.CompletedTask;
        }

        try
        {
            _provider = Build();
            _logger.LogInformation(
                "OpenTelemetry tracing enabled: service={Service} export={Export} endpoint={Endpoint} sampling={Ratio:P0}",
                _options.ServiceName,
                _options.ExportEnabled ? _options.Protocol : "in-memory only",
                _options.ExportEnabled ? _options.Endpoint : "-",
                _sampler.Ratio);
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: a bad tracing endpoint is a degraded-observability
            // problem, not a reason to stop serving traffic.
            StartError = ex.Message;
            _logger.LogError(ex, "Failed to start OpenTelemetry tracing. Spans will not be exported.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Dispose flushes the batch processor, which is the only reason a graceful shutdown
        // matters here: without it the last few seconds of spans are simply lost.
        _provider?.Dispose();
        _provider = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask;
        _provider?.Dispose();
        _provider = null;
    }

    private TracerProvider Build()
    {
        var resource = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: _options.ServiceName,
                serviceVersion: _options.ServiceVersion ?? GatewayVersion,
                serviceInstanceId: Environment.MachineName);

        if (!string.IsNullOrWhiteSpace(_options.Environment))
            resource.AddAttributes(new[] { new KeyValuePair<string, object>("deployment.environment", _options.Environment) });

        var builder = Sdk.CreateTracerProviderBuilder()
            .SetSampler(_sampler)
            .SetResourceBuilder(resource)
            // The last resort for "the span is missing": if a source is not registered here, the
            // instrumentation may be installed and still produce nothing.
            .AddSource(GatewayTracing.SourceName)
            .AddSource("Yarp.ReverseProxy")
            .AddSource("Yarp.ReverseProxy.Forwarder")
            .AddProcessor(new RecentTraceProcessor(_buffer));

        if (_options.RecordAspNetCoreSpans)
        {
            builder.AddAspNetCoreInstrumentation(options =>
            {
                options.RecordException = true;

                // The tracing page polls every five seconds. Tracing those requests would fill the
                // buffer with the act of looking at it and push the real traffic out, so the
                // control plane and the health/scrape endpoints are excluded entirely - they are
                // not gateway traffic and nobody needs a waterfall for a health check.
                options.Filter = context => !IsControlPlaneRequest(context.Request.Path);
            });
        }

        if (_options.RecordHttpClientSpans)
            builder.AddHttpClientInstrumentation();

        if (_options.ExportEnabled)
        {
            var endpoint = ParseEndpoint(_options.Endpoint);
            var protocol = string.Equals(_options.Protocol, "http/protobuf", StringComparison.OrdinalIgnoreCase)
                ? OtlpExportProtocol.HttpProtobuf
                : OtlpExportProtocol.Grpc;

            var headers = _options.Headers.Count == 0
                ? null
                : string.Join(",", _options.Headers.Select(x => $"{x.Key}={x.Value}"));

            builder.AddOtlpExporter(options =>
            {
                options.Endpoint = endpoint;
                options.Protocol = protocol;
                if (headers is not null)
                    options.Headers = headers;
            });
        }

        return builder.Build();
    }

    /// <summary>
    /// Paths that belong to the gateway's own administration rather than to proxied traffic.
    /// </summary>
    private bool IsControlPlaneRequest(PathString path)
        => path.StartsWithSegments(_controlPlanePrefix, StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/_content", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/metrics", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/ready", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/live", StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/favicon.ico", StringComparison.OrdinalIgnoreCase);

    private static Uri ParseEndpoint(string endpoint)    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException("Tracing endpoint is empty; set Endpoint or ExportEnabled=false.");

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture,
                    $"Tracing endpoint '{endpoint}' is not an absolute http(s) URL."));

        return uri;
    }

    private static readonly string GatewayVersion =
        typeof(GatewayTracingHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
