using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Aneiang.Yarp.Dashboard.Modules.Tracing.Models;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;

namespace Aneiang.Yarp.Dashboard.Modules.Tracing.Services;

/// <summary>
/// Sends one empty OTLP batch to the configured export endpoint and reports what came back.
/// </summary>
/// <remarks>
/// <para>
/// The trace list on the page is fed by the tracer provider's processor, not by the exporter, so a
/// wrong endpoint leaves the page looking perfectly healthy while every span is dropped on the
/// floor. Nothing in the pipeline says otherwise: the OTLP exporter is asynchronous, retries, then
/// discards, and its failures go to an EventSource instead of the log. This probe is the only place
/// where "is the export path actually usable" can be answered on demand.
/// </para>
/// <para>
/// The target is always the configured endpoint — there is no URL parameter. That is deliberate:
/// the exporter already reaches whatever the operator configured, so the probe cannot be pointed
/// somewhere they could not already reach, and no request body can turn it into an open relay.
/// </para>
/// <para>
/// The request is wrapped in <see cref="SuppressInstrumentationScope"/> so probing the exporter does
/// not itself produce a span that lands in the buffer and shows up as gateway traffic.
/// </para>
/// </remarks>
public sealed class TracingExportProbe
{
    /// <summary>Name of the HTTP client used by the probe. Registered without a base address.</summary>
    public const string HttpClientName = "tracing-probe";

    /// <summary>gRPC resolves the service path from the root; a path in Endpoint is ignored.</summary>
    private const string GrpcExportPath = "/opentelemetry.proto.collector.trace.v1.TraceService/Export";

    /// <summary>A zero-length gRPC message: 1 flag byte + 4 length bytes, all zero.</summary>
    private static readonly byte[] EmptyGrpcFrame = [0x00, 0x00, 0x00, 0x00, 0x00];

    private const int MaxSnippetLength = 240;
    private const int MaxDetailLength = 400;

    private readonly GatewayTracingOptions _options;
    private readonly TracingAuthStore _authStore;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<TracingExportProbe> _logger;

    /// <summary>Initializes a new instance of the <see cref="TracingExportProbe"/> class.</summary>
    public TracingExportProbe(
        GatewayTracingOptions options,
        TracingAuthStore authStore,
        IHttpClientFactory httpFactory,
        ILogger<TracingExportProbe> logger)
    {
        _options = options;
        _authStore = authStore;
        _httpFactory = httpFactory;
        _logger = logger;
        TcpCheck = ProbeTcpAsync;
    }

    /// <summary>
    /// How long to wait for an answer. Settable so tests do not spend five seconds proving a timeout.
    /// </summary>
    internal TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// TCP reachability check, run before the gRPC exchange. Replaced in tests, which have no socket.
    /// </summary>
    internal Func<Uri, CancellationToken, Task<TcpProbeOutcome>> TcpCheck { get; set; }

    /// <summary>
    /// Probes the configured export endpoint once.
    /// </summary>
    /// <param name="cancellationToken">Cancels the probe; distinct from the probe's own timeout.</param>
    /// <returns>What happened, in the form the tracing page renders.</returns>
    public async Task<TracingProbeResult> RunAsync(CancellationToken cancellationToken = default)
    {
        return await RunCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TracingProbeResult> RunCoreAsync(CancellationToken cancellationToken)
    {
        if (!_options.ExportEnabled)
        {
            return new TracingProbeResult
            {
                Phase = TracingProbePhase.Config,
                Detail = "ExportEnabled is false, so nothing is sent to any collector.",
                Protocol = _options.Protocol
            };
        }

        OtlpExportProtocol protocol;
        Uri endpoint;
        try
        {
            // Same parser and same normalisation the exporter uses. A probe that disagreed with the
            // real export path would be worse than no probe at all.
            protocol = GatewayTracingHost.ParseProtocol(_options.Protocol);
            endpoint = GatewayTracingHost.ParseEndpoint(_options.Endpoint);
        }
        catch (InvalidOperationException ex)
        {
            return new TracingProbeResult
            {
                Phase = TracingProbePhase.Config,
                Detail = ex.Message,
                Protocol = _options.Protocol
            };
        }

        var isGrpc = protocol == OtlpExportProtocol.Grpc;
        var (target, note) = BuildTarget(endpoint, isGrpc);

        var headers = _authStore.ResolveEffective(_options);
        var clock = Stopwatch.StartNew();

        if (isGrpc)
        {
            // RequestVersionExact reports *every* failure as VersionNegotiationError — a closed
            // port and an unresolvable host included. Without this check a typo in the host name
            // would be reported as "the server does not speak HTTP/2", which is the same class of
            // misleading diagnosis this probe exists to remove.
            var tcp = await TcpCheck(endpoint, cancellationToken).ConfigureAwait(false);
            if (tcp.Phase is not null)
            {
                clock.Stop();
                return Failure(tcp.Phase, null, target, note, headers.Count, clock,
                    Detail(tcp.Detail ?? "TCP connection failed."), null);
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, target)
        {
            // Exact version, not "or lower": gRPC over plaintext needs HTTP/2 without an upgrade
            // dance, and a silent downgrade to HTTP/1.1 would report a protocol error that is
            // actually the server's honest answer.
            Version = isGrpc ? HttpVersion.Version20 : HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        if (isGrpc)
        {
            request.Content = new ByteArrayContent(EmptyGrpcFrame);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
            request.Headers.TryAddWithoutValidation("TE", "trailers");
        }
        else
        {
            // An empty ExportTraceServiceRequest is a valid protobuf message (every field is
            // repeated and optional), so this exercises the real ingest path without inventing a
            // span the backend would then have to store.
            request.Content = new ByteArrayContent([]);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        }

        foreach (var header in headers)
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);

        var client = _httpFactory.CreateClient(HttpClientName);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(Timeout);

        try
        {
            HttpResponseMessage response;
            using (SuppressInstrumentationScope.Begin())
            {
                response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                    .ConfigureAwait(false);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                var body = await ReadBodyAsync(response, timeoutSource.Token).ConfigureAwait(false);
                clock.Stop();

                // With gRPC the HTTP status is 200 even for a rejected credential: the real result
                // arrives as a trailer. Reporting "200 OK" there would be exactly the false green
                // this probe exists to prevent.
                var grpcStatus = isGrpc ? TrailerValue(response, "grpc-status") : null;
                var grpcMessage = isGrpc ? TrailerValue(response, "grpc-message") : null;

                // A 401 on its own is ambiguous: bad password, or a route that wants a different
                // credential type. The challenge header is what separates the two, and it is the
                // difference between rotating a secret that was fine and changing the scheme.
                var challenge = ChallengeValue(response);

                if (grpcStatus is not null && grpcStatus != "0")
                    return Failure(TracingProbePhase.Http, status, target, note, headers.Count, clock,
                        Detail($"grpc-status={grpcStatus} {grpcMessage}".Trim()),
                        Snippet(body), challenge);

                if (!response.IsSuccessStatusCode)
                {
                    if (challenge is not null && status is 401 or 403)
                        note = TracingProbeNote.AuthChallenge;

                    return Failure(TracingProbePhase.Http, status, target, note, headers.Count, clock,
                        Detail($"{status} {response.ReasonPhrase}".Trim()),
                        Snippet(body), challenge);
                }

                _logger.LogInformation(
                    "Tracing export probe succeeded: {Status} from {Url} in {Elapsed} ms.",
                    status, target, clock.ElapsedMilliseconds);

                return new TracingProbeResult
                {
                    Ok = true,
                    Phase = TracingProbePhase.Http,
                    StatusCode = status,
                    Url = Display(target),
                    Protocol = _options.Protocol,
                    HeaderCount = headers.Count,
                    ElapsedMs = clock.ElapsedMilliseconds,
                    Note = note
                };
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            clock.Stop();
            return Failure(TracingProbePhase.Timeout, null, target, note, headers.Count, clock,
                $"No response within {Timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s.",
                null);
        }
        catch (HttpRequestException ex)
        {
            clock.Stop();

            var phase = ex.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => TracingProbePhase.Dns,
                HttpRequestError.SecureConnectionError => TracingProbePhase.Tls,
                HttpRequestError.VersionNegotiationError => TracingProbePhase.Protocol,
                HttpRequestError.HttpProtocolError => TracingProbePhase.Protocol,
                _ => TracingProbePhase.Connect
            };

            // The inner message is the one that names the host and the socket error; the outer one
            // is a generic wrapper ("An error occurred while sending the request").
            var detail = ex.InnerException?.Message is { Length: > 0 } inner ? inner : ex.Message;

            // The TCP check already passed, so a version error here really is the endpoint refusing
            // plaintext HTTP/2 — not an unreachable host wearing the same exception.
            if (isGrpc && phase == TracingProbePhase.Protocol)
                note = TracingProbeNote.GrpcNoH2c;

            _logger.LogInformation(ex, "Tracing export probe failed ({Phase}) for {Url}.", phase, target);

            return Failure(phase, null, target, note, headers.Count, clock, Detail(detail), null);
        }
    }

    /// <summary>
    /// Opens a plain TCP connection to the endpoint's host and port, only to classify the failure.
    /// </summary>
    /// <remarks>
    /// Nothing is written. The connection exists so that "the host does not resolve", "nothing is
    /// listening" and "the port answers but not with HTTP/2" stop collapsing into one message.
    /// </remarks>
    private async Task<TcpProbeOutcome> ProbeTcpAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(Timeout);

        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, source.Token).ConfigureAwait(false);
            return new TcpProbeOutcome(null, null);
        }
        catch (SocketException ex)
        {
            var phase = ex.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
                ? TracingProbePhase.Dns
                : TracingProbePhase.Connect;

            return new TcpProbeOutcome(phase, $"TCP {endpoint.Host}:{endpoint.Port} — {ex.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TcpProbeOutcome(
                TracingProbePhase.Timeout,
                $"TCP connect to {endpoint.Host}:{endpoint.Port} timed out.");
        }
    }

    /// <summary>
    /// Works out the URL the exporter would actually call, and whether that differs from what was
    /// configured.
    /// </summary>
    private static (Uri Target, string? Note) BuildTarget(Uri endpoint, bool isGrpc)
    {
        if (isGrpc)
        {
            // Mirrors the exporter: GrpcChannel combines the base address with a rooted path, so
            // whatever was appended to Endpoint (OpenObserve's /api/default, for instance) is
            // dropped. Saying so is more useful than silently testing a different URL than the one
            // the operator typed.
            var authority = new UriBuilder(endpoint.Scheme, endpoint.Host, endpoint.Port).Uri;
            var target = new Uri(authority, GrpcExportPath);
            var hadPath = endpoint.AbsolutePath.Trim('/').Length > 0;
            return (target, hadPath ? TracingProbeNote.GrpcPathIgnored : null);
        }

        var http = GatewayTracingHost.NormalizeHttpTracesEndpoint(endpoint);
        var appended = !string.Equals(http.AbsolutePath, endpoint.AbsolutePath, StringComparison.Ordinal);
        return (http, appended ? TracingProbeNote.PathAppended : null);
    }

    private TracingProbeResult Failure(
        string phase,
        int? status,
        Uri target,
        string? note,
        int headerCount,
        Stopwatch clock,
        string detail,
        string? snippet,
        string? challenge = null)
        => new()
        {
            Ok = false,
            Phase = phase,
            StatusCode = status,
            Detail = detail,
            Snippet = snippet,
            Url = Display(target),
            Protocol = _options.Protocol,
            HeaderCount = headerCount,
            ElapsedMs = clock.ElapsedMilliseconds,
            Note = note,
            Challenge = challenge
        };

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            // Bounded on purpose: an error body is a sentence, and a hostile or misrouted endpoint
            // should not be able to make the probe allocate a document.
            var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            return bytes.Length <= 4096
                ? System.Text.Encoding.UTF8.GetString(bytes)
                : System.Text.Encoding.UTF8.GetString(bytes, 0, 4096);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static string? TrailerValue(HttpResponseMessage response, string name)
        => response.TrailingHeaders.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>
    /// The <c>WWW-Authenticate</c> value(s), e.g. <c>Bearer realm="openobserve"</c>.
    /// </summary>
    /// <remarks>
    /// Backends answer "I do not take that credential type here" with a 401 that is otherwise
    /// indistinguishable from "wrong password". Echoing the challenge is what lets the operator
    /// stop rotating secrets that were never the problem.
    /// </remarks>
    private static string? ChallengeValue(HttpResponseMessage response)
    {
        var challenges = response.Headers.WwwAuthenticate;
        if (challenges.Count == 0) return null;

        var joined = string.Join(", ", challenges.Select(x => x.ToString()));
        return string.IsNullOrWhiteSpace(joined) ? null : Detail(joined);
    }

    private static string? Snippet(string body)
    {
        var trimmed = body?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= MaxSnippetLength ? trimmed : trimmed[..MaxSnippetLength] + "…";
    }

    private static string Detail(string value)
        => value.Length <= MaxDetailLength ? value : value[..MaxDetailLength] + "…";

    /// <summary>
    /// Scheme + authority + path, without userinfo. A credential pasted into the URL by mistake
    /// must not be echoed back to the browser by a diagnostic endpoint.
    /// </summary>
    private static string Display(Uri uri) => $"{uri.Scheme}://{uri.Authority}{uri.PathAndQuery}";
}

/// <summary>
/// Outcome of the pre-flight TCP check: a phase when it failed, both null when the port answered.
/// </summary>
internal readonly record struct TcpProbeOutcome(string? Phase, string? Detail);
