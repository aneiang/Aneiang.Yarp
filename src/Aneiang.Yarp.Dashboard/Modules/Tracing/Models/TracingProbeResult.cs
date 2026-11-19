namespace Aneiang.Yarp.Dashboard.Modules.Tracing.Models;

/// <summary>
/// Where a probe stopped. Codes rather than sentences: the page translates them, and the raw
/// exception text travels alongside as <see cref="TracingProbeResult.Detail"/> for the person who
/// needs the real message.
/// </summary>
public static class TracingProbePhase
{
    /// <summary>The configuration itself is unusable, so no request was made.</summary>
    public const string Config = "config";

    /// <summary>The host name did not resolve.</summary>
    public const string Dns = "dns";

    /// <summary>TCP connection was refused, reset, or otherwise unreachable.</summary>
    public const string Connect = "connect";

    /// <summary>TLS handshake failed.</summary>
    public const string Tls = "tls";

    /// <summary>The server answered, but not with the protocol that was asked for.</summary>
    public const string Protocol = "protocol";

    /// <summary>No answer inside the probe timeout.</summary>
    public const string Timeout = "timeout";

    /// <summary>The server answered with a failure status, or with a non-zero gRPC status.</summary>
    public const string Http = "http";

    /// <summary>Anything the other buckets do not explain.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// Extra facts about the URL that were derived rather than configured. Codes, not sentences, for
/// the same reason as <see cref="TracingProbePhase"/>.
/// </summary>
public static class TracingProbeNote
{
    /// <summary>The <c>/v1/traces</c> segment was added because the configured endpoint lacked it.</summary>
    public const string PathAppended = "path-appended";

    /// <summary>gRPC ignores the path in <c>Endpoint</c>, so only scheme/host/port were used.</summary>
    public const string GrpcPathIgnored = "grpc-path-ignored";

    /// <summary>
    /// The port answered on TCP but would not speak plaintext HTTP/2. Common on backends that put
    /// gRPC on a different port from their HTTP API.
    /// </summary>
    public const string GrpcNoH2c = "grpc-no-h2c";

    /// <summary>
    /// The endpoint rejected the request and said which scheme it wants instead
    /// (<c>WWW-Authenticate</c>). A <c>401</c> alone reads as "wrong password"; the challenge is
    /// what turns it into "this route does not accept the credential type you are sending".
    /// </summary>
    public const string AuthChallenge = "auth-challenge";
}

/// <summary>
/// Outcome of sending one empty OTLP batch to the configured export endpoint.
/// </summary>
/// <remarks>
/// <see cref="Ok"/> means "the endpoint accepted a batch with these credentials", not "spans are
/// stored". It cannot mean more than that: the probe sends an empty request, so there is nothing
/// for the backend to index. What it does prove is the two things that actually break in practice —
/// the URL reaches the ingest handler, and the credential is accepted.
/// </remarks>
public sealed class TracingProbeResult
{
    /// <summary>True when the endpoint answered with success and no gRPC error status.</summary>
    public bool Ok { get; init; }

    /// <summary>One of the <see cref="TracingProbePhase"/> codes.</summary>
    public string Phase { get; init; } = TracingProbePhase.Unknown;

    /// <summary>HTTP status code, when a response was received.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Raw diagnostic text: exception message or <c>grpc-status</c> line.</summary>
    public string? Detail { get; init; }

    /// <summary>The first part of an error response body, for 4xx/5xx answers.</summary>
    public string? Snippet { get; init; }

    /// <summary>The URL that was actually called, with any userinfo removed.</summary>
    public string? Url { get; init; }

    /// <summary>The configured protocol string, echoed as written.</summary>
    public string? Protocol { get; init; }

    /// <summary>How many authentication headers the request carried.</summary>
    public int HeaderCount { get; init; }

    /// <summary>Round-trip time in milliseconds.</summary>
    public long ElapsedMs { get; init; }

    /// <summary>One of the <see cref="TracingProbeNote"/> codes, or null.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// The <c>WWW-Authenticate</c> header the endpoint sent back, verbatim and truncated. Worth
    /// surfacing because it is the only thing that distinguishes "the password is wrong" from
    /// "this route wants a different credential type" — both arrive as a bare 401.
    /// </summary>
    public string? Challenge { get; init; }
}
