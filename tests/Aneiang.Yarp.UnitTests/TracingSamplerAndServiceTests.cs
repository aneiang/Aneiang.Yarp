using System.Diagnostics;
using Aneiang.Yarp.Dashboard.Modules.Tracing.Models;
using Aneiang.Yarp.Dashboard.Modules.Tracing.Services;
using Aneiang.Yarp.Infrastructure.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Covers the sampling decision and the service that reports on it. Kept in the same collection as
/// <see cref="GatewayTracingTests"/>: both attach listeners to the same activity source, and running
/// them concurrently would let one test's spans appear in the other's assertions.
/// </summary>
[Collection("tracing-spans")]
public class TracingSamplerAndServiceTests
{
    private const string SamplerSource = "test.sampler";

    // ---------------------------------------------------------------- sampler

    [Fact]
    public void Ratio_zero_records_nothing()
    {
        var sampler = new RuntimeRatioSampler(0);
        using var provider = BuildProvider(sampler);
        using var source = new ActivitySource(SamplerSource);

        // A dropped trace is still created - only as "propagation data", so trace context keeps
        // flowing to the next hop. What matters is that it is not recorded.
        using var activity = source.StartActivity("span");

        Assert.False(activity is { Recorded: true });
        Assert.Equal(0, sampler.Sampled);
        Assert.True(sampler.Dropped > 0);
    }

    [Fact]
    public void Ratio_one_records_everything()
    {
        var sampler = new RuntimeRatioSampler(1);
        using var provider = BuildProvider(sampler);
        using var source = new ActivitySource(SamplerSource);

        using var activity = source.StartActivity("span");

        Assert.True(activity is { Recorded: true });
        Assert.Equal(1, sampler.Sampled);
    }

    [Fact]
    public void Ratio_lands_near_the_configured_fraction()
    {
        var sampler = new RuntimeRatioSampler(0.5);
        using var provider = BuildProvider(sampler);
        using var source = new ActivitySource(SamplerSource);

        var recorded = 0;
        for (var i = 0; i < 400; i++)
        {
            using var activity = source.StartActivity("span");
            if (activity is { Recorded: true })
                recorded++;
        }

        // 400 draws at p=0.5: the standard deviation is 10, so 140..260 is beyond six sigma. A
        // tighter bound would turn a correct implementation into a flaky test.
        Assert.InRange(recorded, 140, 260);
    }

    [Fact]
    public void Same_trace_id_always_gets_the_same_decision()
    {
        var sampler = new RuntimeRatioSampler(0.5);
        using var provider = BuildProvider(sampler);
        using var source = new ActivitySource(SamplerSource);

        // A random sampler would occasionally flip here; deriving the decision from the trace id is
        // what keeps retries and repeated exports consistent.
        var traceId = ActivityTraceId.CreateRandom();
        var parent = new ActivityContext(traceId, ActivitySpanId.CreateRandom(), ActivityTraceFlags.None);

        using var first = source.StartActivity("a", ActivityKind.Server, parent);
        using var second = source.StartActivity("b", ActivityKind.Server, parent);

        Assert.Equal(first is { Recorded: true }, second is { Recorded: true });
    }

    [Fact]
    public void Inbound_sampled_flag_wins_over_the_local_ratio()
    {
        var sampler = new RuntimeRatioSampler(0);
        using var provider = BuildProvider(sampler);
        using var source = new ActivitySource(SamplerSource);

        var sampledParent = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        var unsampledParent = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.None);

        // Local ratio is 0, but the caller already decided to trace this request: splicing the
        // trace in half at the gateway would be the worst place to do it.
        using var honoured = source.StartActivity("honoured", ActivityKind.Server, sampledParent);
        Assert.True(honoured is { Recorded: true });

        using var dropped = source.StartActivity("dropped", ActivityKind.Server, unsampledParent);
        Assert.False(dropped is { Recorded: true });
    }

    // ---------------------------------------------------------------- service

    [Fact]
    public async Task Disabled_tracing_reports_no_restart_needed_and_builds_no_provider()
    {
        var options = new GatewayTracingOptions { Enabled = false };
        await using var harness = Harness.Create(options);

        await harness.Host.StartAsync(CancellationToken.None);
        var status = harness.Service.GetStatus();

        Assert.False(status.Enabled);
        Assert.False(status.Running);
        Assert.False(status.RestartRequired);
        Assert.Null(status.StartError);
    }

    [Fact]
    public async Task Unreachable_exporter_configuration_is_reported_instead_of_escaping()
    {
        var options = new GatewayTracingOptions
        {
            Enabled = true,
            // Not an absolute URL, which is exactly the typo an operator makes.
            Endpoint = "localhost:4317",
            RecordAspNetCoreSpans = false,
            RecordHttpClientSpans = false
        };
        await using var harness = Harness.Create(options);

        // A bad tracing endpoint must not stop the gateway from booting.
        await harness.Host.StartAsync(CancellationToken.None);

        var status = harness.Service.GetStatus();
        Assert.False(status.Running);
        Assert.True(status.RestartRequired);
        Assert.Contains("endpoint", status.StartError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task In_memory_mode_records_spans_without_an_exporter()
    {
        var options = new GatewayTracingOptions
        {
            Enabled = true,
            ExportEnabled = false,
            RecordAspNetCoreSpans = false,
            RecordHttpClientSpans = false,
            SamplingRatio = 1.0,
            RecentTraceCapacity = 10,
            MaxSpansPerTrace = 10
        };
        await using var harness = Harness.Create(options);

        await harness.Host.StartAsync(CancellationToken.None);
        Assert.True(harness.Host.IsRunning);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/orders";

        using (var activity = GatewayTracing.StartForward(context))
        {
            Assert.NotNull(activity);
            GatewayTracing.CompleteForward(activity!, context);
        }

        var summary = Assert.Single(harness.Service.GetTraces(10));
        Assert.Equal(GatewayTracing.ForwardSpanName, summary.RootName);
        Assert.Equal(1, summary.SpanCount);

        var status = harness.Service.GetStatus();
        Assert.Null(status.Endpoint);
        Assert.Equal(1.0, status.SamplingRatio);
        Assert.Equal(1, status.BufferCount);
    }

    [Fact]
    public async Task Sampling_ratio_can_be_changed_at_runtime_and_validates_input()
    {
        var options = new GatewayTracingOptions { Enabled = false, SamplingRatio = 0.25 };
        await using var harness = Harness.Create(options);
        await harness.Host.StartAsync(CancellationToken.None);

        Assert.Equal(0.25, harness.Service.GetStatus().SamplingRatio);

        Assert.True(harness.Service.TrySetSamplingRatio(0.8, out var noError));
        Assert.Null(noError);
        Assert.Equal(0.8, harness.Service.GetStatus().SamplingRatio);

        Assert.False(harness.Service.TrySetSamplingRatio(1.5, out var tooHigh));
        Assert.False(string.IsNullOrEmpty(tooHigh));
        Assert.False(harness.Service.TrySetSamplingRatio(double.NaN, out _));

        // A rejected value must not have been applied.
        Assert.Equal(0.8, harness.Service.GetStatus().SamplingRatio);
    }

    [Fact]
    public async Task Clearing_empties_the_buffer_and_its_counters()
    {
        var options = new GatewayTracingOptions
        {
            Enabled = true,
            ExportEnabled = false,
            RecordAspNetCoreSpans = false,
            RecordHttpClientSpans = false,
            SamplingRatio = 1.0
        };
        await using var harness = Harness.Create(options);
        await harness.Host.StartAsync(CancellationToken.None);

        var context = new DefaultHttpContext();
        using (var activity = GatewayTracing.StartForward(context))
            GatewayTracing.CompleteForward(activity!, context);

        var before = harness.Service.GetStatus();
        Assert.Equal(1, before.BufferCount);
        Assert.Equal(1, before.SpansCaptured);

        harness.Service.ClearTraces();

        var after = harness.Service.GetStatus();
        Assert.Equal(0, after.BufferCount);
        Assert.Null(harness.Service.GetTrace("nope"));
        // The page reads these numbers next to the buffer size, so a clear that kept them would
        // report "0 traces / 1 span captured" and look broken.
        Assert.Equal(0, after.SpansCaptured);
        Assert.Equal(0, after.SpansDropped);
        Assert.Equal(0, after.TracesEvicted);
    }

    private static TracerProvider BuildProvider(Sampler sampler)
        => Sdk.CreateTracerProviderBuilder()
            .SetSampler(sampler)
            .AddSource(SamplerSource)
            .Build();

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _authRoot;

        private Harness(GatewayTracingOptions options, GatewayTracingHost host, GatewayTracingService service, string authRoot)
        {
            Host = host;
            Service = service;
            _authRoot = authRoot;
        }

        public GatewayTracingHost Host { get; }

        public GatewayTracingService Service { get; }

        public static Harness Create(GatewayTracingOptions options)
        {
            var sampler = new RuntimeRatioSampler(options.SamplingRatio);
            var buffer = new TraceRingBuffer(
                GatewayTracingHost.BufferSources,
                options.RecentTraceCapacity,
                options.MaxSpansPerTrace);

            // Isolated content root per harness: the store caches the override file, so sharing a
            // directory would let one test's headers leak into the next one's status.
            var authRoot = Path.Combine(Path.GetTempPath(), "yarp-tracing-auth-" + Guid.NewGuid().ToString("N"));
            var authStore = new TracingAuthStore(authRoot, NullLogger<TracingAuthStore>.Instance);

            var host = new GatewayTracingHost(
                options,
                sampler,
                buffer,
                authStore,
                Options.Create(new Aneiang.Yarp.Dashboard.Infrastructure.DashboardOptions()),
                NullLogger<GatewayTracingHost>.Instance);
            return new Harness(
                options,
                host,
                new GatewayTracingService(options, sampler, buffer, host, authStore),
                authRoot);
        }

        public async ValueTask DisposeAsync()
        {
            await Host.StopAsync(CancellationToken.None);
            await Host.DisposeAsync();

            try
            {
                if (Directory.Exists(_authRoot))
                    Directory.Delete(_authRoot, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
