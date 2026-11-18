using System.Text.Json;
using Aneiang.Yarp.Dashboard.Modules.Alerting.Models;
using Aneiang.Yarp.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.Dashboard.Modules.Alerting.Services;

/// <summary>What gets persisted: the rules, plus enough incident history to answer "what fired last night".</summary>
public sealed class MetricAlertDocument
{
    /// <summary>Configured rules.</summary>
    public List<MetricAlertRule> Rules { get; set; } = new();

    /// <summary>Recent incidents, newest last (the service sorts on read).</summary>
    public List<MetricAlertIncident> Incidents { get; set; } = new();
}

/// <summary>
/// Stores alert rules and incident history beside the SQLite database, the same way the canary
/// ledger is kept.
/// </summary>
/// <remarks>
/// <para>
/// Rules have to survive a restart for the obvious reason: an alert rule that forgets itself on
/// deploy is worse than no alert rule, because the silence looks like health.
/// </para>
/// <para>
/// Incidents are persisted for a subtler reason. Restarting the gateway in the middle of an
/// incident would otherwise erase it, and the next notification would look like a brand new problem
/// — losing the "it has been failing for two hours" that makes an alert actionable.
/// </para>
/// <para>
/// Writes are best-effort: an alerting rule store must never be able to fail a request or block a
/// config change.
/// </para>
/// </remarks>
public sealed class MetricAlertStore
{
    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly ILogger<MetricAlertStore> _logger;
    private readonly string _path;
    private readonly object _gate = new();

    public MetricAlertStore(
        IOptions<StorageOptions> storage,
        ILogger<MetricAlertStore> logger)
    {
        _logger = logger;
        _path = ResolvePath(storage.Value);
    }

    /// <summary>Absolute path of the store file, surfaced in diagnostics and the UI.</summary>
    public string Path => _path;

    /// <summary>Reads the document; an empty one when the file is missing or unreadable.</summary>
    public MetricAlertDocument Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                    return new MetricAlertDocument();

                var json = File.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(json))
                    return new MetricAlertDocument();

                return JsonSerializer.Deserialize<MetricAlertDocument>(json, FileJsonOptions)
                       ?? new MetricAlertDocument();
            }
            catch (Exception ex)
            {
                // A corrupt file must not stop the gateway from starting. Starting with no rules is
                // visible in the UI (an empty list); throwing on startup would hide the gateway.
                _logger.LogWarning(ex, "Alert store at {Path} could not be read; starting empty", _path);
                return new MetricAlertDocument();
            }
        }
    }

    /// <summary>Writes the document. Never throws.</summary>
    public void Save(MetricAlertDocument document)
    {
        lock (_gate)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // Write-then-move: a crash mid-write would otherwise leave a truncated file that
                // reads back as "no rules", quietly disarming every alert.
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(document, FileJsonOptions));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Alert store could not be written to {Path}", _path);
            }
        }
    }

    private static string ResolvePath(StorageOptions storage)
    {
        var dbFileName = "gateway-store.db";
        var connStr = storage?.Sqlite?.ConnectionString;

        if (!string.IsNullOrEmpty(connStr))
        {
            var idx = connStr.IndexOf("Data Source=", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var start = idx + "Data Source=".Length;
                var end = connStr.IndexOf(';', start);
                if (end < 0) end = connStr.Length;
                dbFileName = connStr[start..end].Trim();
                if (!System.IO.Path.IsPathRooted(dbFileName))
                    dbFileName = System.IO.Path.GetFullPath(dbFileName);
            }
        }

        var dir = System.IO.Path.GetDirectoryName(dbFileName);
        if (string.IsNullOrEmpty(dir)) dir = AppContext.BaseDirectory;
        return System.IO.Path.Combine(dir, "metric-alerts.json");
    }
}
