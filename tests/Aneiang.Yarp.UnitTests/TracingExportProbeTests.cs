using Aneiang.Yarp.Dashboard.Modules.Tracing.Models;
using Aneiang.Yarp.Dashboard.Modules.Tracing.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Covers the export connectivity probe: which URL it calls for each protocol, how a failure is
/// classified, and what it refuses to do. No real network — every case runs through a stub handler.
/// </summary>
[Collection("tracing-spans")]
public sealed class TracingExportProbeTests : IDisposable
{
    private readonly string _root;
    private readonly TracingAuthStore _store;

    public TracingExportProbeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "yarp-probe-tests-" + Guid.NewGuid().ToString("N"));
        _store = new TracingAuthStore(_root, NullLogger<TracingAuthStore>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- URL shape

    [Fact]
    public async Task Http_probe_calls_the_traces_path_and_reports_it_was_appended()
    {
        var handler = new StubHandler(_ => Json(200, "{}"));
        var probe = Probe(Http("http://collector:4318"), handler);

        var result = await probe.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("http://collector:4318/v1/traces", result.Url);
        // The exporter adds the segment itself now, so the page has to say which URL was tested —
        // a probe that quietly tested a different URL than the operator configured is worse than none.
        Assert.Equal(TracingProbeNote.PathAppended, result.Note);
    }

    [Fact]
    public async Task An_explicit_traces_path_is_not_appended_twice()
    {
        var probe = Probe(Http("http://collector:4318/v1/traces"), new StubHandler(_ => Json(200, "{}")));

        var result = await probe.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal("http://collector:4318/v1/traces", result.Url);
        Assert.Null(result.Note);
    }

    [Fact]
    public async Task A_backend_path_such_as_openobserve_keeps_its_prefix()
    {
        var probe = Probe(Http("http://obs:5080/api/default"), new StubHandler(_ => Json(200, "")));

        var result = await probe.RunAsync();

        Assert.Equal("http://obs:5080/api/default/v1/traces", result.Url);
    }

    [Fact]
    public async Task Grpc_uses_the_service_path_and_ignores_the_configured_path()
    {
        var handler = new StubHandler(_ => Json(200, ""));
        var probe = Probe(Grpc("http://obs:5080/api/default"), handler);

        var result = await probe.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(
            "http://obs:5080/opentelemetry.proto.collector.trace.v1.TraceService/Export",
            result.Url);
        Assert.Equal(TracingProbeNote.GrpcPathIgnored, result.Note);
        Assert.Equal("application/grpc", handler.Last!.ContentType);
        Assert.Equal("2.0", handler.Last.Version);
    }

    [Fact]
    public async Task Credentials_are_sent_but_never_echoed_back()
    {
        var handler = new StubHandler(_ => Json(200, ""));
        var options = Http("http://user:secret@collector:4318");
        options.Headers["Authorization"] = "Basic dXNlcjpwYXNz";

        var result = await Probe(options, handler).RunAsync();

        Assert.Equal("Basic dXNlcjpwYXNz", handler.Last!.AuthorizationValue);
        Assert.Equal(1, result.HeaderCount);
        Assert.DoesNotContain("secret", result.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("user", result.Url, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- failure kinds

    [Fact]
    public async Task A_rejected_batch_is_reported_with_the_status_and_body()
    {
        var probe = Probe(Http("http://collector:4318"), new StubHandler(_ => Json(401, "invalid token")));

        var result = await probe.RunAsync();

        Assert.False(result.Ok);
        Assert.Equal(TracingProbePhase.Http, result.Phase);
        Assert.Equal(401, result.StatusCode);
        Assert.Contains("invalid token", result.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_404_is_not_confused_with_a_credential_problem()
    {
        // This is the case that used to send people off rotating tokens: the path was one segment
        // short, the backend answered 404 to a perfectly valid credential.
        var probe = Probe(Http("http://collector:4318"), new StubHandler(_ => Json(404, "not found")));

        var result = await probe.RunAsync();

        Assert.False(result.Ok);
        Assert.Equal(404, result.StatusCode);
        Assert.Equal(TracingProbePhase.Http, result.Phase);
    }

    [Fact]
    public async Task A_401_carries_the_challenge_so_the_scheme_can_be_told_apart_from_a_bad_secret()
    {
        // OpenObserve answers a Basic credential on its gRPC route with 401 + "Bearer realm=...".
        // Reporting only "401" makes the operator rotate a password that was never wrong.
        var probe = Probe(Grpc("http://collector:5080"),
            new StubHandler(_ => Challenge(401, "Bearer", "realm=\"openobserve\"")));

        var result = await probe.RunAsync();

        Assert.False(result.Ok);
        Assert.Equal(401, result.StatusCode);
        Assert.Equal(TracingProbeNote.AuthChallenge, result.Note);
        Assert.Contains("Bearer", result.Challenge, StringComparison.Ordinal);
        Assert.Contains("openobserve", result.Challenge, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_401_without_a_challenge_is_not_reported_as_a_scheme_problem()
    {
        // A plain 401 with no WWW-Authenticate really is "the credential was refused"; inventing a
        // scheme hint here would send people looking for a token they do not need.
        var probe = Probe(Http("http://collector:4318"), new StubHandler(_ => Json(401, "invalid token")));

        var result = await probe.RunAsync();

        Assert.Equal(401, result.StatusCode);
        Assert.Null(result.Challenge);
        Assert.NotEqual(TracingProbeNote.AuthChallenge, result.Note);
    }

    [Fact]
    public async Task A_zero_grpc_status_with_http_200_is_success()
    {
        var probe = Probe(Grpc("http://collector:5080"), new StubHandler(_ => GrpcResponse("0", null)));

        var result = await probe.RunAsync();

        Assert.True(result.Ok);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task A_non_zero_grpc_status_fails_even_though_http_says_200()
    {
        // gRPC reports the real verdict in a trailer; trusting the HTTP status here is exactly the
        // false green the probe exists to prevent.
        var probe = Probe(Grpc("http://collector:5080"),
            new StubHandler(_ => GrpcResponse("16", "unauthenticated")));

        var result = await probe.RunAsync();

        Assert.False(result.Ok);
        Assert.Equal(TracingProbePhase.Http, result.Phase);
        Assert.Contains("grpc-status=16", result.Detail, StringComparison.Ordinal);
        Assert.Contains("unauthenticated", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_that_does_not_resolve_is_reported_as_dns()
    {
        var probe = Probe(Http("http://openobserve:5080/api/default"),
            new ThrowingHandler(new HttpRequestException(
                HttpRequestError.NameResolutionError,
                "No such host is known. (openobserve:5080)")));

        var result = await probe.RunAsync();

        Assert.False(result.Ok);
        Assert.Equal(TracingProbePhase.Dns, result.Phase);
        Assert.Contains("openobserve", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_connection_is_reported_as_connect()
    {
        var probe = Probe(Http("http://collector:4318"),
            new ThrowingHandler(new HttpRequestException(
                HttpRequestError.ConnectionError,
                "Connection refused (collector:4318)")));

        var result = await probe.RunAsync();

        Assert.False(result.Ok);
        Assert.Equal(TracingProbePhase.Connect, result.Phase);
    }

    [Fact]
    public async Task A_tls_failure_is_reported_as_tls()
    {
        var probe = Probe(Http("https://collector:443"),
            new ThrowingHandler(new HttpRequestException(
                HttpRequestError.SecureConnectionError,
                "The SSL connection could not be established.")));

        Assert.Equal(TracingProbePhase.Tls, (await probe.RunAsync()).Phase);
    }

    [Fact]
    public async Task A_server_that_speaks_http_1_1_is_reported_as_protocol_mismatch()
    {
        // gRPC needs HTTP/2; a response in the wrong version is a protocol fact, not a network one.
        // TCP already succeeded, so the page can say "this port will not do h2c" instead of
        // suggesting the host is unreachable.
        var probe = Probe(Grpc("http://collector:5080"),
            new ThrowingHandler(new HttpRequestException(
                HttpRequestError.VersionNegotiationError,
                "The server does not support the requested HTTP version.")));

        var result = await probe.RunAsync();

        Assert.Equal(TracingProbePhase.Protocol, result.Phase);
        Assert.Equal(TracingProbeNote.GrpcNoH2c, result.Note);
    }

    [Fact]
    public async Task A_gRPC_host_that_does_not_resolve_is_dns_not_a_protocol_problem()
    {
        // RequestVersionExact reports DNS failures as VersionNegotiationError too; without the TCP
        // pre-flight this would blame the protocol for a typo in the host name.
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not be called"));
        var probe = Probe(Grpc("http://collector:5080"), handler,
            tcp: new TcpProbeOutcome(TracingProbePhase.Dns, "No such host is known"));

        var result = await probe.RunAsync();

        Assert.Equal(TracingProbePhase.Dns, result.Phase);
        Assert.Contains("No such host", result.Detail, StringComparison.Ordinal);
        Assert.Null(handler.Last);
    }

    [Fact]
    public async Task A_gRPC_port_with_nothing_listening_is_connect_not_timeout()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not be called"));
        var probe = Probe(Grpc("http://collector:5080"), handler,
            tcp: new TcpProbeOutcome(TracingProbePhase.Connect, "Connection refused"));

        var result = await probe.RunAsync();

        Assert.Equal(TracingProbePhase.Connect, result.Phase);
        Assert.Null(handler.Last);
    }

    [Fact]
    public async Task A_silent_endpoint_is_reported_as_timeout()
    {
        var probe = Probe(Http("http://collector:4318"), new HangingHandler(), TimeSpan.FromMilliseconds(60));

        var result = await probe.RunAsync();

        Assert.False(result.Ok);
        Assert.Equal(TracingProbePhase.Timeout, result.Phase);
    }

    // ---------------------------------------------------------------- refusals

    [Fact]
    public async Task Export_disabled_makes_no_request_at_all()
    {
        var options = Http("http://collector:4318");
        options.ExportEnabled = false;

        var handler = new StubHandler(_ => throw new InvalidOperationException("must not be called"));
        var result = await Probe(options, handler).RunAsync();

        Assert.False(result.Ok);
        Assert.Equal(TracingProbePhase.Config, result.Phase);
        Assert.Null(handler.Last);
    }

    [Theory]
    [InlineData("carrier-pigeon")]
    [InlineData("")]
    public async Task An_unsupported_protocol_is_a_config_error(string protocol)
    {
        var options = Http("http://collector:4318");
        options.Protocol = protocol;

        var handler = new StubHandler(_ => throw new InvalidOperationException("must not be called"));
        var result = await Probe(options, handler).RunAsync();

        Assert.Equal(TracingProbePhase.Config, result.Phase);
        Assert.Null(handler.Last);
    }

    [Fact]
    public async Task A_relative_endpoint_is_rejected_without_a_request()
    {
        var options = Http("collector:4318");

        var handler = new StubHandler(_ => throw new InvalidOperationException("must not be called"));
        var result = await Probe(options, handler).RunAsync();

        Assert.Equal(TracingProbePhase.Config, result.Phase);
        Assert.Contains("absolute", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.Last);
    }

    // ---------------------------------------------------------------- helpers

    private static GatewayTracingOptions Http(string endpoint) => new()
    {
        Enabled = true,
        ExportEnabled = true,
        Endpoint = endpoint,
        Protocol = "http/protobuf"
    };

    private static GatewayTracingOptions Grpc(string endpoint)
    {
        var options = Http(endpoint);
        options.Protocol = "grpc";
        return options;
    }

    private TracingExportProbe Probe(
        GatewayTracingOptions options,
        HttpMessageHandler handler,
        TimeSpan? timeout = null,
        TcpProbeOutcome? tcp = null)
    {
        var probe = new TracingExportProbe(
            options,
            _store,
            new StubFactory(handler),
            NullLogger<TracingExportProbe>.Instance);

        if (timeout is not null) probe.Timeout = timeout.Value;

        // The pre-flight socket check is replaced everywhere: these tests must not touch a network,
        // and the gRPC cases would otherwise fail on a host name that only exists in the assertions.
        probe.TcpCheck = (_, _) => Task.FromResult(tcp ?? new TcpProbeOutcome(null, null));
        return probe;
    }

    private static HttpResponseMessage Json(int status, string body) => new((System.Net.HttpStatusCode)status)
    {
        Content = new StringContent(body)
    };

    /// <summary>A rejection that names the credential type it wants instead.</summary>
    private static HttpResponseMessage Challenge(int status, string scheme, string parameter)
    {
        var response = new HttpResponseMessage((System.Net.HttpStatusCode)status)
        {
            Content = new StringContent("Unauthorized Access")
        };

        response.Headers.WwwAuthenticate.Add(
            new System.Net.Http.Headers.AuthenticationHeaderValue(scheme, parameter));

        return response;
    }

    private static HttpResponseMessage GrpcResponse(string status, string? message)    {
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([])
        };

        response.TrailingHeaders.TryAddWithoutValidation("grpc-status", status);
        if (message is not null)
            response.TrailingHeaders.TryAddWithoutValidation("grpc-message", message);

        return response;
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    /// <summary>Answer with a canned response, and keep the facts the assertions need.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public CapturedRequest? Last { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = new CapturedRequest(
                request.Method.Method,
                request.RequestUri!.ToString(),
                request.Version.ToString(),
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.TryGetValues("Authorization", out var values) ? values.FirstOrDefault() : null,
                request.Headers.TryGetValues("TE", out var te) ? te.FirstOrDefault() : null,
                request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken));

            return _respond(request);
        }
    }

    private sealed record CapturedRequest(
        string Method,
        string Url,
        string Version,
        string? ContentType,
        string? AuthorizationValue,
        string? Te,
        byte[]? Body);

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _error;
        public ThrowingHandler(Exception error) => _error = error;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(_error);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
