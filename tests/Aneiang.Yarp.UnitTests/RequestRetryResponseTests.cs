using System.Collections.Immutable;
using Aneiang.Yarp.Infrastructure.Middleware;
using Aneiang.Yarp.Infrastructure.Resilience;
using Aneiang.Yarp.Infrastructure.State;
using Aneiang.Yarp.Models;
using Aneiang.Yarp.Plugin.Retry;
using Aneiang.Yarp.Services;
using Aneiang.Yarp.Storage.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IO;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Model;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Pins the ordering that decides whether an exhausted retry ends with the upstream's 5xx or with a
/// broken connection: the retry-count header has to be set <em>before</em> the buffered response is
/// copied to the client, because copying starts the response and Kestrel makes headers read-only
/// from that moment on.
/// </summary>
/// <remarks>
/// Found by drilling the fault-injection plugin: an injected 503 was retried to exhaustion, the copy
/// out started the response, and the header write that followed threw
/// <c>InvalidOperationException: Headers are read-only, response has already started</c> — turning a
/// clean 503 into an unhandled exception. The report was a 500-shaped failure with no obvious link
/// to retries, which is exactly why it is worth a test.
/// </remarks>
public class RequestRetryResponseTests
{
    private const string RouteId = "r1";

    [Fact]
    public async Task Retry_count_header_exists_before_the_response_body_is_copied_out()
    {
        var (middleware, probe, context, next) = Create(maxRetries: 2);

        await middleware.InvokeAsync(context);

        Assert.Equal(3, next.Attempts);
        Assert.True(probe.HeaderPresentOnFirstByte,
            "X-Retry-Count must be set before the buffered response is copied: after the copy starts, " +
            "headers are read-only and setting it throws.");
        Assert.Equal("2", context.Response.Headers["X-Retry-Count"].ToString());
    }

    [Fact]
    public async Task Failed_attempts_are_replayed_to_the_client_once()
    {
        var (middleware, probe, context, next) = Create(maxRetries: 2);

        await middleware.InvokeAsync(context);

        // Three attempts, one response: the client must not receive three copies of the body.
        Assert.Equal(3, next.Attempts);
        Assert.Equal("upstream-down", probe.Text.Trim());
        Assert.Equal(503, context.Response.StatusCode);
    }

    private static (RequestRetryMiddleware Middleware, HeaderProbeStream Probe, HttpContext Context, CountingNext Next) Create(
        int maxRetries)
    {
        var routePlugins = ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add(
            RouteId,
            [new PluginBindingSnapshot(
                "b1",
                "request-retry",
                PluginBindingScope.Route,
                RouteId,
                $$"""
                {"enabled":true,"maxRetries":{{maxRetries}},"backoffBaseMs":1,"backoffJitterMs":0,
                 "timeoutSeconds":30,"retryOnStatusCodes":[503]}
                """,
                1, 1, 0)]);

        var snapshot = GatewaySnapshot.Empty with
        {
            Version = 1,
            Routes =
            [
                new RouteConfig
                {
                    RouteId = RouteId,
                    ClusterId = "c1",
                    Match = new RouteMatch { Path = "/{**catchAll}" }
                }
            ],
            RoutePlugins = routePlugins
        };

        var plans = new GatewayPluginExecutionPlanProvider(
            new FakePublisher(snapshot),
            NullLogger<GatewayPluginExecutionPlanProvider>.Instance);

        var context = new DefaultHttpContext();
        context.Features.Set<IReverseProxyFeature>(new FakeReverseProxyFeature(RouteId));
        context.Request.Method = "GET";

        var probe = new HeaderProbeStream(() => context.Response.Headers);
        context.Response.Body = probe;

        var next = new CountingNext(() =>
        {
            context.Response.StatusCode = 503;
            return context.Response.WriteAsync("upstream-down");
        });

        var middleware = new RequestRetryMiddleware(
            next.InvokeAsync,
            NullLogger<RequestRetryMiddleware>.Instance,
            Options.Create(new GatewayMiddlewareOptions()),
            new UnusedPluginManager(),
            new RecyclableMemoryStreamManager(),
            new UnusedCircuitStore(),
            new SingleDestinationCoordinator(),
            plans);

        return (middleware, probe, context, next);
    }

    private sealed class CountingNext(Func<Task> respond)
    {
        public int Attempts { get; private set; }

        public async Task InvokeAsync(HttpContext context)
        {
            Attempts++;
            await respond();
        }
    }

    /// <summary>
    /// Passes writes through to memory while recording whether the retry header was already present
    /// on the first byte — the observable form of "the header was set in time".
    /// </summary>
    private sealed class HeaderProbeStream(Func<IHeaderDictionary> headers) : Stream
    {
        private readonly MemoryStream _inner = new();

        public bool HeaderPresentOnFirstByte { get; private set; }

        public string Text
        {
            get
            {
                _inner.Position = 0;
                using var reader = new StreamReader(_inner, leaveOpen: true);
                return reader.ReadToEnd();
            }
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            Capture();
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Capture();
            _inner.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Capture();
            return _inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Capture();
            return _inner.WriteAsync(buffer, cancellationToken);
        }

        private void Capture()
        {
            if (_inner.Length == 0 && _inner.Position == 0)
                HeaderPresentOnFirstByte = headers().ContainsKey("X-Retry-Count");
        }
    }

    /// <summary>Returns one candidate so the retry loop actually runs an attempt.</summary>
    private sealed class SingleDestinationCoordinator : IDestinationCandidateCoordinator
    {
        public ValueTask<IReadOnlyList<DestinationState>> ApplyAsync(
            HttpContext context, bool excludeAttempted, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<DestinationState>>([new DestinationState("d1")]);

        public void MarkAttempted(HttpContext context, DestinationState? destination) { }
    }

    /// <summary>Only <c>RecordOutcome</c> is reachable from the retry path; the rest must stay unused.</summary>
    private sealed class UnusedCircuitStore : ICircuitStateStore
    {
        public int Count => 0;

        public CircuitState GetOrAdd(string key, Func<string, CircuitState> factory) => throw new NotSupportedException();

        public bool TryGet(string key, out CircuitState? state) => throw new NotSupportedException();

        public bool ContainsKey(string key) => throw new NotSupportedException();

        public IReadOnlyList<KeyValuePair<string, CircuitState>> GetAll() => throw new NotSupportedException();

        public bool TryRemove(string key) => throw new NotSupportedException();

        public void RemoveForCluster(string clusterId, string? clusterUid) => throw new NotSupportedException();

        public void ResetAll() => throw new NotSupportedException();

        public void RenameClusterKey(string oldClusterId, string newClusterId) => throw new NotSupportedException();

        public void EnsureCircuitExists(string clusterId, CircuitBreakerConfig cbConfig, string? clusterUid) => throw new NotSupportedException();

        public bool IsCircuitOpen(string clusterId, string? destinationId = null, string? clusterUid = null) => false;

        public bool TryAcquire(string clusterId, string destinationId, string? clusterUid = null, CircuitBreakerConfig? config = null) => true;

        public void RecordOutcome(string clusterId, string destinationId, bool failed, string? clusterUid = null, CircuitBreakerConfig? config = null) { }

        public IReadOnlyList<CircuitStateInfo> GetAllStateInfos() => throw new NotSupportedException();

        public void CleanupStale(TimeSpan threshold) => throw new NotSupportedException();
    }

    private sealed class UnusedPluginManager : Aneiang.Yarp.Plugins.IGatewayPluginManager
    {
        public IReadOnlyList<Aneiang.Yarp.Plugins.PluginManifest> GetAllManifests() => [];

        public Aneiang.Yarp.Plugins.PluginManifest? GetManifest(string pluginId) => null;

        public IReadOnlyList<Aneiang.Yarp.Plugins.IGatewayPlugin> GetAllPlugins() => [];

        public Aneiang.Yarp.Plugins.IGatewayPlugin? GetPlugin(string pluginId) => null;

        public bool IsPluginEnabled(string pluginId) => true;

        public Aneiang.Yarp.Plugins.PluginStateChangeResult ValidatePluginStateChange(string pluginId, bool enabled)
            => Aneiang.Yarp.Plugins.PluginStateChangeResult.Success;

        public Aneiang.Yarp.Plugins.PluginStateChangeResult SetPluginEnabled(string pluginId, bool enabled)
            => Aneiang.Yarp.Plugins.PluginStateChangeResult.Success;

        public IReadOnlyList<Aneiang.Yarp.Plugins.PluginRuntimeState> GetPluginStates() => [];

        public void SaveState() { }

        public Aneiang.Yarp.Plugins.PluginStateChangeResult InstallPlugin(string sourceDirectory) => throw new NotSupportedException();

        public Aneiang.Yarp.Plugins.PluginStateChangeResult UninstallPlugin(string pluginId) => throw new NotSupportedException();

        public Aneiang.Yarp.Plugins.PluginStateChangeResult UpgradePlugin(string pluginId, string sourceDirectory) => throw new NotSupportedException();
    }

    private sealed class FakePublisher(GatewaySnapshot snapshot) : IGatewaySnapshotPublisher
    {
        public GatewaySnapshot Current { get; } = snapshot;

        public GatewaySnapshot Publish(GatewaySnapshot value) => Current;
    }
}
