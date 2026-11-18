using System.Collections.Immutable;
using System.Diagnostics;
using Aneiang.Yarp.Infrastructure.Middleware;
using Aneiang.Yarp.Plugin.FaultInjection;
using Aneiang.Yarp.Services;
using Aneiang.Yarp.Storage.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Model;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Covers what the middleware actually does to a request: answer, wait, abort, or get out of the way.
/// </summary>
public class FaultInjectionMiddlewareTests
{
    private const string RouteId = "r1";

    [Fact]
    public async Task Injects_the_configured_status_code_without_calling_the_next_stage()
    {
        var (middleware, next, context) = Create("""
            {"enabled":true,"errorStatusCode":503}
            """);

        await middleware.InvokeAsync(context);

        Assert.False(next.Called);
        Assert.Equal(503, context.Response.StatusCode);
        Assert.Equal(FaultInjectionPlanner.DefaultBody, ReadBody(context));
        Assert.Equal("503", context.Response.Headers[FaultInjectionPlanner.AnnounceHeader].ToString());
        Assert.Equal(1, middleware.InjectedCount);
    }

    [Fact]
    public async Task Custom_body_is_written_verbatim_as_plain_text()
    {
        var (middleware, next, context) = Create("""
            {"enabled":true,"errorStatusCode":502,"errorBody":"boom"}
            """);

        await middleware.InvokeAsync(context);

        Assert.False(next.Called);
        Assert.Equal("boom", ReadBody(context));
        Assert.StartsWith("text/plain", context.Response.ContentType);
    }

    [Fact]
    public async Task Announce_can_be_suppressed()
    {
        var (middleware, _, context) = Create("""
            {"enabled":true,"errorStatusCode":503,"announce":false}
            """);

        await middleware.InvokeAsync(context);

        Assert.False(context.Response.Headers.ContainsKey(FaultInjectionPlanner.AnnounceHeader));
    }

    [Fact]
    public async Task Delay_is_waited_out_and_the_request_still_reaches_the_next_stage()
    {
        var (middleware, next, context) = Create("""
            {"enabled":true,"delayMinMs":80}
            """);

        var stopwatch = Stopwatch.StartNew();
        await middleware.InvokeAsync(context);
        stopwatch.Stop();

        Assert.True(next.Called);
        Assert.True(stopwatch.ElapsedMilliseconds >= 60,
            $"expected at least a 60ms delay, got {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Abort_drops_the_connection_and_stops_the_pipeline()
    {
        var (middleware, next, context) = Create("""
            {"enabled":true,"abortConnection":true}
            """);
        var lifetime = new RecordingLifetimeFeature();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);

        await middleware.InvokeAsync(context);

        Assert.True(lifetime.Aborted);
        Assert.False(next.Called);
    }

    [Fact]
    public async Task Route_without_a_binding_is_forwarded_untouched()
    {
        var (middleware, next, context) = Create(null);

        await middleware.InvokeAsync(context);

        Assert.True(next.Called);
        Assert.Equal(0, middleware.InjectedCount);
    }

    [Fact]
    public async Task Disabled_binding_is_forwarded_untouched()
    {
        var (middleware, next, context) = Create("""
            {"enabled":false,"errorStatusCode":503}
            """);

        await middleware.InvokeAsync(context);

        Assert.True(next.Called);
    }

    [Fact]
    public async Task Header_gate_lets_ordinary_traffic_through()
    {
        var (middleware, next, context) = Create("""
            {"enabled":true,"errorStatusCode":503,"requiredHeader":"X-Chaos","requiredHeaderValue":"on"}
            """);

        await middleware.InvokeAsync(context);

        Assert.True(next.Called);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task Header_gate_injects_when_the_caller_opts_in()
    {
        var (middleware, next, context) = Create("""
            {"enabled":true,"errorStatusCode":503,"requiredHeader":"X-Chaos"}
            """);
        context.Request.Headers["X-Chaos"] = "on";

        await middleware.InvokeAsync(context);

        Assert.False(next.Called);
        Assert.Equal(503, context.Response.StatusCode);
    }

    [Fact]
    public async Task Dashboard_requests_are_never_injected()
    {
        // A drill that also breaks the page used to switch it off is a trap.
        var (middleware, next, context) = Create("""
            {"enabled":true,"errorStatusCode":503}
            """);
        context.Request.Path = "/dashboard/plugins";

        await middleware.InvokeAsync(context);

        Assert.True(next.Called);
    }

    private static (FaultInjectionMiddleware Middleware, RecordingNext Next, HttpContext Context) Create(string? configJson)
    {
        var route = new RouteConfig
        {
            RouteId = RouteId,
            ClusterId = "c1",
            Match = new RouteMatch { Path = "/{**catchAll}" }
        };

        var routePlugins = configJson is null
            ? ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty
            : ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add(
                RouteId,
                [new PluginBindingSnapshot("b1", "fault-injection", PluginBindingScope.Route, RouteId, configJson, 1, 1, 0)]);

        var snapshot = GatewaySnapshot.Empty with
        {
            Version = 1,
            Routes = [route],
            RoutePlugins = routePlugins
        };

        var plans = new GatewayPluginExecutionPlanProvider(
            new FakePublisher(snapshot),
            NullLogger<GatewayPluginExecutionPlanProvider>.Instance);

        var next = new RecordingNext();
        var middleware = new FaultInjectionMiddleware(
            next.InvokeAsync,
            NullLogger<FaultInjectionMiddleware>.Instance,
            Options.Create(new GatewayMiddlewareOptions()),
            plans);

        var context = new DefaultHttpContext();
        context.Features.Set<IReverseProxyFeature>(new FakeReverseProxyFeature(RouteId));
        context.Response.Body = new MemoryStream();

        return (middleware, next, context);
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private sealed class RecordingNext
    {
        public bool Called { get; private set; }

        public Task InvokeAsync(HttpContext context)
        {
            Called = true;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLifetimeFeature : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }

        public CancellationToken RequestAborted { get; set; } = CancellationToken.None;

        public void Abort() => Aborted = true;
    }

    private sealed class FakePublisher(GatewaySnapshot snapshot) : IGatewaySnapshotPublisher
    {
        public GatewaySnapshot Current { get; } = snapshot;

        public GatewaySnapshot Publish(GatewaySnapshot value) => Current;
    }
}
