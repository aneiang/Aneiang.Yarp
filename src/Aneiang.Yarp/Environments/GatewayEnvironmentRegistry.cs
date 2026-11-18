using Aneiang.Yarp.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.GatewayEnvironments;

/// <summary>
/// Owns the two environment facts the gateway needs: which environment it is <b>serving</b>
/// (fixed for the process lifetime) and which one the control plane is <b>editing</b> (switchable).
/// </summary>
/// <remarks>
/// <para>
/// These are deliberately two separate pieces of state. If switching the editing view also
/// switched what the gateway forwards, then opening the dashboard on staging would silently
/// redirect production traffic — the single most dangerous thing a multi-environment feature
/// can do. Switching only ever changes which store the dashboard reads and writes.
/// </para>
/// <para>
/// The invariant established in the constructor is <see cref="CurrentEnvironment"/> ==
/// <see cref="RuntimeEnvironment"/>. Startup config loading relies on it, so nothing may switch
/// the view before the host has finished starting.
/// </para>
/// </remarks>
public sealed class GatewayEnvironmentRegistry : IStorageEnvironmentResolver
{
    /// <summary>
    /// Environment names end up in a database file name, so they are restricted to characters
    /// that cannot escape the directory. Rejecting here — at the only place names are accepted —
    /// keeps path traversal out of the storage layer entirely.
    /// </summary>
    private static readonly char[] AllowedNameChars =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-".ToCharArray();

    private readonly object _gate = new();
    private readonly ILogger<GatewayEnvironmentRegistry> _logger;
    private readonly StorageEnvironmentInfo[] _environments;
    private string _current;

    public GatewayEnvironmentRegistry(
        IOptions<StorageOptions> options,
        ILogger<GatewayEnvironmentRegistry> logger)
    {
        _logger = logger;

        var settings = options.Value.Environments;
        var runtime = Normalize(settings.Runtime, StorageEnvironmentOptions.DefaultRuntime, "Runtime");

        var names = new List<string> { runtime };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { runtime };

        foreach (var raw in settings.Names ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var name = Normalize(raw, runtime, "Names");
            if (seen.Add(name)) names.Add(name);
            else _logger.LogDebug("Environment '{Environment}' is listed more than once; ignoring the duplicate", name);
        }

        _environments = names
            .Select(name => new StorageEnvironmentInfo(name, string.Equals(name, runtime, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        RuntimeEnvironment = runtime;
        _current = runtime;

        if (_environments.Length > 1)
        {
            _logger.LogInformation(
                "Multi-environment mode: serving '{Runtime}', editable [{Environments}]. Switching the editing view does not change what is forwarded",
                RuntimeEnvironment, string.Join(", ", names));
        }
    }

    /// <inheritdoc />
    public string RuntimeEnvironment { get; }

    /// <inheritdoc />
    public string CurrentEnvironment
    {
        get { lock (_gate) return _current; }
    }

    /// <inheritdoc />
    public IReadOnlyList<StorageEnvironmentInfo> Environments => _environments;

    /// <inheritdoc />
    public bool IsEditingRuntime
        => string.Equals(CurrentEnvironment, RuntimeEnvironment, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void SwitchTo(string environment)
    {
        if (string.IsNullOrWhiteSpace(environment))
            throw new ArgumentException("Environment is required.", nameof(environment));

        var match = _environments.FirstOrDefault(e =>
            string.Equals(e.Name, environment, StringComparison.OrdinalIgnoreCase));

        if (match == null)
        {
            throw new ArgumentException(
                $"Unknown environment '{environment}'. Available: {string.Join(", ", _environments.Select(e => e.Name))}.",
                nameof(environment));
        }

        lock (_gate)
        {
            if (string.Equals(_current, match.Name, StringComparison.Ordinal))
                return;

            var previous = _current;
            _current = match.Name;

            _logger.LogInformation(
                "Editing view switched {Previous} → {Current} (serving '{Runtime}' unchanged)",
                previous, match.Name, RuntimeEnvironment);
        }
    }

    private static string Normalize(string? name, string fallback, string settingName)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        var trimmed = name.Trim();
        if (trimmed.Any(c => Array.IndexOf(AllowedNameChars, c) < 0))
        {
            throw new ArgumentException(
                $"Invalid environment name '{name}' in Gateway:Storage:Environments:{settingName}. " +
                "Only letters, digits, '_' and '-' are allowed (the name becomes part of a database file name).",
                settingName);
        }

        return trimmed;
    }
}
