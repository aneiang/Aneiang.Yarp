using System.Text.Json;
using Aneiang.Yarp.Dashboard.Modules.Canary.Models;
using Aneiang.Yarp.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.Dashboard.Modules.Canary.Services;

/// <summary>
/// Stores the rollout ledger beside the SQLite database, the same way the AI settings file is kept.
/// </summary>
/// <remarks>
/// <para>
/// The weights themselves are already durable — they live in the cluster's destination metadata and
/// survive a restart. What would be lost is the <em>intent</em>: which destinations were the canary,
/// what the previous split was, and which step the rollout had reached. Without that, a restart
/// leaves a cluster sitting at 10% with nobody knowing whether that is deliberate.
/// </para>
/// <para>
/// Persistence here is best-effort on purpose. Failing to write the ledger must never fail the
/// weight change that already went live: the config is the source of truth, the ledger is the memo.
/// </para>
/// </remarks>
public sealed class CanaryLedgerStore
{
    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly ILogger<CanaryLedgerStore> _logger;
    private readonly string _path;
    private readonly object _gate = new();

    public CanaryLedgerStore(
        IOptions<StorageOptions> storage,
        ILogger<CanaryLedgerStore> logger)
    {
        _logger = logger;
        _path = ResolvePath(storage.Value);
    }

    /// <summary>Absolute path of the ledger file, surfaced in diagnostics.</summary>
    public string Path => _path;

    /// <summary>Reads the ledger; returns an empty list when the file is missing or unreadable.</summary>
    public List<CanaryRollout> Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                    return new List<CanaryRollout>();

                var json = File.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(json))
                    return new List<CanaryRollout>();

                return JsonSerializer.Deserialize<List<CanaryRollout>>(json, FileJsonOptions)
                       ?? new List<CanaryRollout>();
            }
            catch (Exception ex)
            {
                // A corrupt ledger must not stop the gateway from starting. Discard it: the
                // weights in the live config are still correct, only the bookkeeping is gone.
                _logger.LogWarning(ex, "Canary ledger at {Path} could not be read; starting empty", _path);
                return new List<CanaryRollout>();
            }
        }
    }

    /// <summary>Writes the ledger. Never throws.</summary>
    public void Save(IReadOnlyCollection<CanaryRollout> rollouts)
    {
        lock (_gate)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // Write-then-move: a crash mid-write would otherwise leave a truncated file that
                // reads back as "no rollouts", which is worse than no file at all.
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(rollouts, FileJsonOptions));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Canary ledger could not be written to {Path}", _path);
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
        return System.IO.Path.Combine(dir, "canary-rollouts.json");
    }
}
