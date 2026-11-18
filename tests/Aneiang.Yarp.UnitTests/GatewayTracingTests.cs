using System.Diagnostics;
using Aneiang.Yarp.Infrastructure.Observability;
using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Forwarder;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Covers the gateway's own spans: what they are named, what they record, and - the part that
/// matters when reading a waterfall - how a plugin short-circuit is told apart from an upstream
/// failure.
/// </summary>
/// <remarks>
/// Shares a collection with <see cref="TracingSamplerAndServiceTests"/> because both attach global
/// activity listeners to the same source; in parallel they would capture each other's spans.
/// </remarks>
[Collection("tracing-spans")]
public class GatewayTracingTests
{
    [Fact]
    public async Task Forward_span_records_method_and_path()
    {
        using var sink = new SpanSink();
        var context = Context("/api/orders", "POST");

        await new GatewayTracingMiddleware(_ => Task.CompletedTask).InvokeAsync(context);

        var span = Assert.Single(sink.Stopped);
        Assert.Equal(GatewayTracing.ForwardSpanName, span.DisplayName);
        Assert.Equal("POST", span.GetTagItem(GatewayTracing.MethodTag));
        Assert.Equal("/api/orders", span.GetTagItem(GatewayTracing.PathTag));
    }

    [Fact]
    public async Task Request_that_never_reached_a_destination_is_marked_short_circuited()
    {
        using var sink = new SpanSink();
        var context = Context("/blocked", "GET");

        // A plugin answering on its own: no destination was chosen and no forwarding error exists.
        await new GatewayTracingMiddleware(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }).InvokeAsync(context);

        var span = Assert.Single(sink.Stopped);
        Assert.Equal(true, span.GetTagItem(GatewayTracing.ShortCircuitedTag));
        Assert.Equal(StatusCodes.Status403Forbidden, span.GetTagItem(GatewayTracing.StatusCodeTag));
        Assert.Null(span.GetTagItem(GatewayTracing.DestinationIdTag));

        // 403 is the gateway doing its job, not a failed span.
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
    }

    [Fact]
    public async Task Forwarding_error_is_distinguished_from_a_short_circuit()
    {
        using var sink = new SpanSink();
        var context = Context("/api/orders", "GET");
        context.Features.Set<IForwarderErrorFeature>(new FakeForwarderError(ForwarderError.Request));

        await new GatewayTracingMiddleware(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
            return Task.CompletedTask;
        }).InvokeAsync(context);

        var span = Assert.Single(sink.Stopped);

        // The upstream was tried and failed, so this is not a plugin block - the distinction the
        // whole span exists to make.
        Assert.Equal(false, span.GetTagItem(GatewayTracing.ShortCircuitedTag));
        Assert.Equal(nameof(ForwarderError.Request), span.GetTagItem(GatewayTracing.ForwarderErrorTag));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task Server_error_marks_the_span_as_failed()
    {
        using var sink = new SpanSink();
        var context = Context("/api/orders", "GET");

        await new GatewayTracingMiddleware(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Task.CompletedTask;
        }).InvokeAsync(context);

        var span = Assert.Single(sink.Stopped);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task Thrown_exception_is_recorded_and_rethrown()
    {
        using var sink = new SpanSink();
        var context = Context("/api/boom", "GET");

        var middleware = new GatewayTracingMiddleware(_ => throw new InvalidOperationException("kaboom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        var span = Assert.Single(sink.Stopped);
        Assert.Equal(nameof(InvalidOperationException), span.GetTagItem(GatewayTracing.ExceptionTag));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task Plugin_span_is_named_after_the_plugin_and_nested_under_the_request()
    {
        using var sink = new SpanSink();
        var context = Context("/api/orders", "GET");

        using var outer = GatewayTracing.StartForward(context);
        Assert.NotNull(outer);

        using (var plugin = GatewayTracing.StartPlugin(context, "waf"))
        {
            Assert.NotNull(plugin);
            Assert.Equal("gateway.plugin waf", plugin!.DisplayName);
            Assert.Equal("waf", plugin.GetTagItem(GatewayTracing.PluginIdTag));
            Assert.Equal(outer!.SpanId, plugin.ParentSpanId);
        }

        await Task.CompletedTask;
    }

    [Fact]
    public void Plugin_spans_can_be_switched_off()
    {
        using var sink = new SpanSink();
        var context = Context("/api/orders", "GET");
        var original = GatewayTracing.PluginSpansEnabled;

        try
        {
            GatewayTracing.PluginSpansEnabled = false;
            using var outer = GatewayTracing.StartForward(context);
            Assert.Null(GatewayTracing.StartPlugin(context, "waf"));
        }
        finally
        {
            GatewayTracing.PluginSpansEnabled = original;
        }
    }

    private static DefaultHttpContext Context(string path, string method)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Response.StatusCode = StatusCodes.Status200OK;
        return context;
    }

    private sealed class FakeForwarderError(ForwarderError error) : IForwarderErrorFeature
    {
        public ForwarderError Error { get; } = error;
        public Exception? Exception => null;
    }

    /// <summary>Collects finished spans from the gateway's source for the duration of one test.</summary>
    private sealed class SpanSink : IDisposable
    {
        private readonly ActivityListener _listener;

        public SpanSink()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == GatewayTracing.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    lock (Stopped)
                        Stopped.Add(activity);
                }
            };

            ActivitySource.AddActivityListener(_listener);
        }

        public List<Activity> Stopped { get; } = new();

        public void Dispose() => _listener.Dispose();
    }
}
