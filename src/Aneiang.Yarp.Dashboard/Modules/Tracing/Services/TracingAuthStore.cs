using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Aneiang.Yarp.Dashboard.Modules.Tracing.Services;

/// <summary>
/// One OTLP export header, as the page is allowed to see it.
/// </summary>
/// <param name="Name">Header name, e.g. <c>Authorization</c>.</param>
/// <param name="MaskedValue">Value with everything but the scheme and the last 8 characters replaced.</param>
/// <param name="Source">Where the effective value came from: <c>config</c>, <c>panel</c> or <c>env</c>.</param>
public sealed record TracingAuthHeader(string Name, string MaskedValue, string Source);

/// <summary>
/// Whether the exporter authenticates, and how — never the credentials themselves.
/// </summary>
/// <param name="Configured">True when at least one header will be attached to exports.</param>
/// <param name="Count">Number of effective headers.</param>
/// <param name="Headers">Effective headers, values masked.</param>
/// <param name="OverrideFilePath">Where the dashboard writes values it is given.</param>
/// <param name="EnvVariableName">Standard variable that is honoured as a fallback, if any was set.</param>
public sealed record TracingAuthStatus(
    bool Configured,
    int Count,
    IReadOnlyList<TracingAuthHeader> Headers,
    string OverrideFilePath,
    string? EnvVariableName);

/// <summary>
/// Resolves the OTLP export headers and owns the file the dashboard writes them to.
/// </summary>
/// <remarks>
/// <para>
/// Three sources, because each exists for a different reason and none can be removed:
/// </para>
/// <list type="number">
/// <item><b>Configuration</b> (<c>Gateway:Dashboard:Tracing:Headers</c>) — the deployment's own
/// decision, and the only one an operator can review in the same file as everything else.</item>
/// <item><b>Panel override</b> (<c>Data/tracing-auth.json</c>) — written from the dashboard. Kept in
/// its own file so writing a credential never reformats or comments-out the main configuration.</item>
/// <item><b>Standard environment variables</b> (<c>OTEL_EXPORTER_OTLP_HEADERS</c>) — honoured
/// because container deployments already store the token there as a secret. Requiring those
/// deployments to rewrite it into a project-specific nested key would only teach people that this
/// gateway does not support authenticated export.</item>
/// </list>
/// <para>
/// Precedence is explicit over implicit, and specific over generic: configuration beats the panel
/// file, which beats the environment. Every layer is merged per header name, so setting
/// <c>Authorization</c> in the panel does not discard an <c>X-Scope-OrgID</c> that came from config.
/// </para>
/// <para>
/// Nothing here ever returns a full value to the caller. <see cref="Mask"/> is the only way out.
/// </para>
/// </remarks>
public sealed class TracingAuthStore
{
    /// <summary>File the dashboard writes overrides to, relative to the content root.</summary>
    public const string RelativeFilePath = "Data/tracing-auth.json";

    /// <summary>Standard OTLP variables, in the order they are consulted.</summary>
    private static readonly string[] EnvNames =
    [
        "OTEL_EXPORTER_OTLP_TRACES_HEADERS",
        "OTEL_EXPORTER_OTLP_HEADERS"
    ];

    private const int MaxHeaders = 32;
    private const int MaxNameLength = 64;
    private const int MaxValueLength = 4096;
    private const int VisibleTailLength = 8;

    /// <summary>The mask token; its presence in a submitted value means the caller echoed the view.</summary>
    private const string MaskedSentinel = "****";

    /// <summary>RFC 7230 token characters. Anything else is not a header name.</summary>
    private static readonly Regex HeaderNamePattern = new(@"^[A-Za-z0-9!#$%&'*+\-.^_`|~]+$", RegexOptions.Compiled);

    private readonly string _filePath;
    private readonly ILogger<TracingAuthStore> _logger;
    private readonly object _sync = new();
    private Dictionary<string, string>? _cachedOverride;

    /// <summary>Initializes a new instance of the <see cref="TracingAuthStore"/> class.</summary>
    /// <param name="contentRootPath">
    /// Host content root. Taken as a plain path rather than the hosting environment abstraction so
    /// the store can be exercised in a unit test without standing up a host.
    /// </param>
    /// <param name="logger">Logger. A failed write is reported, never thrown.</param>
    public TracingAuthStore(string contentRootPath, ILogger<TracingAuthStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        _logger = logger;
        _filePath = Path.Combine(contentRootPath, RelativeFilePath);
    }

    /// <summary>Absolute path of the override file, shown on the tracing page.</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// The headers that will actually be attached to every OTLP export, merged across the three
    /// sources with configuration winning.
    /// </summary>
    public IReadOnlyDictionary<string, string> ResolveEffective(Models.GatewayTracingOptions options)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Lowest precedence first: later writes win, so the order below is deliberate.
        foreach (var pair in ParseEnvironmentHeaders())
            merged[pair.Key] = pair.Value;

        foreach (var pair in ReadOverride())
            merged[pair.Key] = pair.Value;

        if (options.Headers is not null)
        {
            foreach (var pair in options.Headers)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrEmpty(pair.Value)) continue;
                // A value pointing at an unresolved placeholder is worse than no value: it would be
                // sent as-is and produce a 401 that looks like a credentials problem.
                if (pair.Value.Contains("${", StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "OTLP header '{Header}' still contains a configuration placeholder and will be sent literally.",
                        pair.Key);
                }

                merged[pair.Key] = pair.Value;
            }
        }

        return merged;
    }

    /// <summary>Builds the page-facing view: counts, masked values, and where each one came from.</summary>
    public TracingAuthStatus Describe(Models.GatewayTracingOptions options)
    {
        var fromConfig = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (options.Headers is not null)
        {
            foreach (var key in options.Headers.Keys.Where(k => !string.IsNullOrWhiteSpace(k)))
                fromConfig.Add(key);
        }

        var fromPanel = new HashSet<string>(ReadOverride().Keys, StringComparer.OrdinalIgnoreCase);
        var effective = ResolveEffective(options);

        var headers = effective
            .Select(pair => new TracingAuthHeader(
                pair.Key,
                Mask(pair.Value),
                fromConfig.Contains(pair.Key) ? "config" : fromPanel.Contains(pair.Key) ? "panel" : "env"))
            .OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var envName = EnvNames.FirstOrDefault(name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)));

        return new TracingAuthStatus(headers.Length > 0, headers.Length, headers, _filePath, envName);
    }

    /// <summary>
    /// Replaces the panel-written overrides. An empty list clears them, falling back to
    /// configuration and the environment.
    /// </summary>
    /// <returns>False with a reason when a header is invalid; nothing is written in that case.</returns>
    public bool TrySave(IEnumerable<KeyValuePair<string, string>>? headers, out string? error)
    {
        var incoming = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in headers ?? [])
        {
            var name = pair.Key?.Trim() ?? string.Empty;
            var value = pair.Value?.Trim() ?? string.Empty;

            if (name.Length == 0)
            {
                // A fully blank row is padding in the editor and means nothing. A value without a
                // name is a mistake — dropping it silently would look like a successful save.
                if (value.Length == 0) continue;
                error = "A header value was provided without a header name.";
                return false;
            }

            // Empty means "do not override from the panel". The page can only show a masked value,
            // so it must not round-trip one; leaving a row empty is how an operator withdraws a
            // panel-written header and lets configuration or the environment win again.
            if (value.Length == 0) continue;

            // Defence against the editor submitting the placeholder it was shown as if it were the
            // real credential — that would replace a working token with literal asterisks.
            if (value.Contains(MaskedSentinel, StringComparison.Ordinal))
            {
                error = $"Header '{name}' looks like a masked value. Enter the real credential.";
                return false;
            }

            if (name.Length > MaxNameLength || !HeaderNamePattern.IsMatch(name))
            {
                error = $"'{name}' is not a valid HTTP header name.";
                return false;
            }

            // Header injection: a CR/LF in the value would let the caller append arbitrary headers
            // to the gRPC metadata / HTTP request the exporter sends.
            if (value.Length > MaxValueLength || value.Contains('\r') || value.Contains('\n'))
            {
                error = $"Header '{name}' has an invalid value (too long, or contains a line break).";
                return false;
            }

            incoming[name] = value;
        }

        if (incoming.Count > MaxHeaders)
        {
            error = $"At most {MaxHeaders} export headers are allowed.";
            return false;
        }

        error = null;

        if (incoming.Count == 0)
        {
            DeleteOverride();
            return true;
        }

        lock (_sync)
        {
            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var payload = new OverrideFile
                {
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Headers = incoming
                };

                var json = JsonSerializer.Serialize(payload, SerializerOptions);
                File.WriteAllText(_filePath, json);

                // The file holds a credential. On Unix, 0600 means an operator can still inspect it
                // but nothing else on the box can read it.
                if (!OperatingSystem.IsWindows())
                {
                    try
                    {
                        File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                    {
                        _logger.LogWarning(ex, "Could not restrict permissions on {File}", _filePath);
                    }
                }

                _cachedOverride = new Dictionary<string, string>(incoming, StringComparer.OrdinalIgnoreCase);
                _logger.LogInformation(
                    "OTLP export headers updated from the dashboard ({Count} header(s)). A restart is required for the exporter to pick them up.",
                    incoming.Count);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                error = $"Could not write {_filePath}: {ex.Message}";
                _logger.LogError(ex, "Failed to persist OTLP export headers to {File}", _filePath);
                return false;
            }
        }
    }

    /// <summary>
    /// Masks a credential for display: keeps the auth scheme and the last few characters so an
    /// operator can tell two tokens apart, and nothing else.
    /// </summary>
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var spaceIndex = value.IndexOf(' ');
        var scheme = spaceIndex > 0 ? value[..(spaceIndex + 1)] : string.Empty;
        var secret = spaceIndex > 0 ? value[(spaceIndex + 1)..] : value;

        // Short values have no safe tail — revealing 8 of 10 characters is the same as revealing all.
        if (secret.Length <= VisibleTailLength * 2)
            return scheme + MaskedSentinel;

        return string.Create(CultureInfo.InvariantCulture,
            $"{scheme}{MaskedSentinel}{secret[^VisibleTailLength..]}");
    }

    private IReadOnlyDictionary<string, string> ReadOverride()
    {
        lock (_sync)
        {
            if (_cachedOverride is not null) return _cachedOverride;

            if (!File.Exists(_filePath))
                return _cachedOverride = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var json = File.ReadAllText(_filePath);
                var parsed = JsonSerializer.Deserialize<OverrideFile>(json, SerializerOptions);
                _cachedOverride = parsed?.Headers is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(parsed.Headers, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A corrupt file must not take the gateway down; it means "no overrides" plus a
                // warning that points at the file the operator may want to inspect.
                _logger.LogWarning(ex, "Could not read {File}; export headers from the dashboard are ignored", _filePath);
                _cachedOverride = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return _cachedOverride;
        }
    }

    private void DeleteOverride()
    {
        lock (_sync)
        {
            _cachedOverride = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (File.Exists(_filePath))
                {
                    File.Delete(_filePath);
                    _logger.LogInformation("OTLP export header overrides cleared; falling back to configuration and environment.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete {File}", _filePath);
            }
        }
    }

    /// <summary>
    /// Parses the standard OTLP header variables. The format is documented as
    /// <c>key1=value1,key2=value2</c>; splitting on the first <c>=</c> keeps base64 padding intact.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string>> ParseEnvironmentHeaders()
    {
        foreach (var name in EnvNames)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw)) continue;

            foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = entry.IndexOf('=');
                if (separator <= 0) continue;

                var key = entry[..separator].Trim();
                var value = entry[(separator + 1)..].Trim();
                if (key.Length > 0 && value.Length > 0)
                    yield return new KeyValuePair<string, string>(key, value);
            }
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private sealed class OverrideFile
    {
        public DateTimeOffset UpdatedAt { get; set; }

        public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
