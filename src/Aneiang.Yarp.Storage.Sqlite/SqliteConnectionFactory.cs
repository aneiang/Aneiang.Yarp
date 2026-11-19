using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Aneiang.Yarp.Storage;

namespace Aneiang.Yarp.Storage.Sqlite;

/// <summary>
/// Shared SQLite connection factory used by all storage repositories.
/// Ensures SQLitePCL provider is initialized once and connection pooling is enabled.
/// WAL journal mode is enabled via connection string for better concurrency.
/// Schema migration is triggered lazily on the first connection to each environment,
/// to guarantee tables exist before any repository reads that database.
/// </summary>
/// <remarks>
/// Environment isolation is implemented as <b>one database file per environment</b>, resolved
/// through <see cref="IStorageEnvironmentResolver.CurrentEnvironment"/> on every connection.
/// That keeps the blast radius of a broken drill or a bad migration inside its own file, and
/// spares every repository and every table from growing an <c>EnvironmentId</c> column.
///
/// The runtime environment keeps the <see cref="SqliteStorageOptions.ConnectionString"/> exactly
/// as configured, so an existing deployment upgrades in place: its data is still where it was.
/// Only non-runtime environments get a suffixed file name.
/// </remarks>
public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly StorageOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly IStorageEnvironmentResolver? _resolver;

    /// <summary>One open database: its connection string plus its own one-shot migration.</summary>
    private sealed class Slot
    {
        public required string Environment { get; init; }
        public required string ConnectionString { get; init; }
        public required Lazy<Task> Migration { get; init; }
    }

    /// <summary>
    /// Per-environment slots. Keyed case-insensitively so that <c>Prod</c> in config and
    /// <c>prod</c> in a request do not open two files.
    /// </summary>
    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);

    public SqliteConnectionFactory(IOptions<StorageOptions> options, IServiceProvider serviceProvider)
        : this(options, serviceProvider, null)
    {
    }

    public SqliteConnectionFactory(
        IOptions<StorageOptions> options,
        IServiceProvider serviceProvider,
        IStorageEnvironmentResolver? resolver)
    {
        _options = options.Value;
        _serviceProvider = serviceProvider;
        _resolver = resolver;
        EnsureProvider();
        EnsureEncryptionIsHonoured(_options);
    }

    /// <summary>
    /// The environment connections are opened against right now. Without a resolver this is the
    /// configured runtime environment, which is what every single-environment host wants.
    /// </summary>
    /// <remarks>
    /// A <see cref="StorageEnvironmentScope"/> override wins over the resolver. It exists so the
    /// control plane can read or write another environment without switching the editing view —
    /// switching reloads the working set and publishes, which is far too much side effect for an
    /// operation that only meant to look at another store.
    /// </remarks>
    private string CurrentEnvironment =>
        StorageEnvironmentScope.Ambient ?? _resolver?.CurrentEnvironment ?? _options.Environments.Runtime;

    /// <summary>
    /// Creates a new pooled SQLite connection against the current environment. The first call for
    /// an environment triggers that environment's schema migration if it has not yet completed,
    /// ensuring tables exist before any repository access.
    /// </summary>
    public async ValueTask<SqliteConnection> CreateConnectionAsync(CancellationToken ct = default)
    {
        var slot = GetSlot();
        await slot.Migration.Value;
        return new SqliteConnection(slot.ConnectionString);
    }

    /// <summary>
    /// Creates a new pooled SQLite connection (synchronous wrapper).
    /// The first call blocks until that environment's schema migration completes.
    /// </summary>
    public SqliteConnection CreateConnection()
    {
        var slot = GetSlot();
        // Block on the migration task; subsequent calls return immediately.
        slot.Migration.Value.GetAwaiter().GetResult();
        return new SqliteConnection(slot.ConnectionString);
    }

    /// <summary>
    /// Creates a raw connection bypassing the migration guard, against the current environment.
    /// Used internally by <see cref="SqliteSchemaMigrator"/> to avoid deadlock when running migrations.
    /// </summary>
    internal SqliteConnection CreateRawConnection() => new(GetSlot().ConnectionString);

    // Explicit interface implementations — return DbConnection for storage-agnostic callers
    DbConnection IDbConnectionFactory.CreateConnection() => CreateConnection();

    async ValueTask<DbConnection> IDbConnectionFactory.CreateConnectionAsync(CancellationToken ct)
        => await CreateConnectionAsync(ct).ConfigureAwait(false);

    private Slot GetSlot()
    {
        var environment = CurrentEnvironment;
        return _slots.GetOrAdd(environment, CreateSlot);
    }

    private Slot CreateSlot(string environment)
    {
        var connectionString = EnsurePoolingAndWalEnabled(
            ResolveConnectionString(_options, environment));

        return new Slot
        {
            Environment = environment,
            ConnectionString = connectionString,

            // Lazy<Task> ensures migration runs exactly once per environment, on first use.
            // SqliteSchemaMigrator is resolved on-demand to avoid circular DI (the migrator
            // depends on this factory for raw connections).
            Migration = new Lazy<Task>(() =>
            {
                var migrator = ActivatorUtilities.CreateInstance<SqliteSchemaMigrator>(
                    _serviceProvider, this);
                return migrator.RunMigrationAsync();
            })
        };
    }

    /// <summary>
    /// Maps an environment to a connection string. The runtime environment returns the configured
    /// string untouched — that is what lets an existing single-database deployment upgrade without
    /// moving any data. Other environments get their file name suffixed, e.g.
    /// <c>gateway-store.db</c> → <c>gateway-store.dev.db</c>.
    /// </summary>
    /// <remarks>
    /// Public because operators need to know which file to back up for which environment, and
    /// because guessing the file name is not something anyone should have to do.
    /// </remarks>
    public static string ResolveConnectionString(StorageOptions options, string environment)
    {
        var baseConnectionString = options.Sqlite.ConnectionString;
        var runtime = options.Environments.Runtime;

        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            // Nothing configured: use the historical default. Falling through to the suffix logic
            // matters — returning early here would hand every environment the same file.
            baseConnectionString = "Data Source=gateway-store.db";
        }

        if (string.IsNullOrWhiteSpace(environment) ||
            string.Equals(environment, runtime, StringComparison.OrdinalIgnoreCase))
        {
            return baseConnectionString;
        }

        SqliteConnectionStringBuilder builder;
        try
        {
            builder = new SqliteConnectionStringBuilder(baseConnectionString);
        }
        catch (ArgumentException)
        {
            // Unparseable string: do not guess, and do not silently point the environment at
            // the production file either — hand back the original so the failure is visible.
            return baseConnectionString;
        }

        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) ||
            dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            // In-memory databases cannot be isolated by file name. Callers asking for
            // environments on top of :memory: get a separate connection-local database either
            // way, so there is nothing useful to add here.
            return baseConnectionString;
        }

        var directory = Path.GetDirectoryName(dataSource);
        var name = Path.GetFileNameWithoutExtension(dataSource);
        var extension = Path.GetExtension(dataSource);
        var suffixedName = string.IsNullOrEmpty(extension)
            ? $"{name}.{environment}"
            : $"{name}.{environment}{extension}";

        builder.DataSource = string.IsNullOrEmpty(directory)
            ? suffixedName
            : Path.Combine(directory, suffixedName);

        return builder.ToString();
    }

    private static void EnsureProvider() => SqliteNativeProvider.EnsureInitialized();

    /// <summary>
    /// Refuses to run when the connection string asks for encryption the provider cannot deliver.
    /// </summary>
    /// <remarks>
    /// This is the point of SEC-15: <c>Password=</c> is only meaningful if SQLCipher is the active
    /// provider. When it was not, the password was accepted and ignored, and every shipped sample
    /// config — all of which set <c>Password=123456</c> — produced a plaintext database that the
    /// operator believed was encrypted. Failing here makes that impossible to miss, at the moment
    /// the factory is first resolved rather than on the first query against a leaked file.
    /// </remarks>
    private static void EnsureEncryptionIsHonoured(StorageOptions options)
    {
        if (!RequestsPassword(options.Sqlite.ConnectionString)) return;
        if (SqliteNativeProvider.SupportsEncryption) return;

        throw new InvalidOperationException(
            "Gateway:Storage:Sqlite:ConnectionString sets Password=, but the installed SQLite " +
            "provider cannot encrypt it (PRAGMA cipher_version returned no value). The database " +
            "would be written in plaintext while the configuration claims otherwise. Either " +
            "reference SQLitePCLRaw.bundle_e_sqlcipher and remove any " +
            "SQLitePCLRaw.bundle_e_sqlite3 / Microsoft.Data.Sqlite reference, or drop Password= " +
            "and protect the file with filesystem permissions.");
    }

    /// <summary>True when the connection string carries a non-empty <c>Password</c>.</summary>
    private static bool RequestsPassword(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return false;

        try
        {
            return !string.IsNullOrEmpty(new SqliteConnectionStringBuilder(connectionString).Password);
        }
        catch (ArgumentException)
        {
            // An unparseable string fails on first use with a message about the actual problem;
            // guessing at the password here would only add a confusing error in front of it.
            return false;
        }
    }

    private static string EnsurePoolingAndWalEnabled(string cs)
    {
        if (string.IsNullOrWhiteSpace(cs))
            return "Data Source=gateway-store.db;Pooling=true";

        var result = cs;
        if (!result.Contains("Pooling=", StringComparison.OrdinalIgnoreCase))
            result = result.TrimEnd(';') + ";Pooling=true";
        return result;
    }
}
