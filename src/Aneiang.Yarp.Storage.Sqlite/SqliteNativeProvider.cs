namespace Aneiang.Yarp.Storage.Sqlite;

/// <summary>
/// Installs the bundled SQLCipher provider exactly once per process.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the project references <c>Microsoft.Data.Sqlite.Core</c> rather than
/// <c>Microsoft.Data.Sqlite</c>. The non-Core package's only added value is a static constructor
/// that calls <c>SQLitePCL.Batteries_V2.Init()</c>; without it, the Core package throws
/// "You need to call SQLitePCL.raw.SetProvider()" on the first connection.
/// </para>
/// <para>
/// Called from <see cref="SqliteConnectionFactory"/>'s constructor, which every repository goes
/// through before it can open anything. Not a module initializer: running side effects when a
/// library loads is exactly the kind of surprise consumers cannot see or opt out of, and the
/// analyzer flags it (CA2255) for that reason.
/// </para>
/// <para>
/// Why Core at all: <c>Microsoft.Data.Sqlite</c> transitively pulls
/// <c>SQLitePCLRaw.bundle_e_sqlite3</c>, and that bundle shipped the vulnerable
/// <c>SQLitePCLRaw.lib.e_sqlite3</c> 2.1.10 (GHSA-2m69-gcr7-jv3q). It also registered a
/// <em>second</em> provider alongside SQLCipher's, making it non-obvious which one answered
/// <c>Password=</c> in the connection string. Dropping the meta package removes the vulnerable
/// native library and leaves exactly one provider, so encryption either works or does not — it is
/// no longer a race between two <c>Batteries_V2.Init()</c> calls.
/// </para>
/// </remarks>
internal static class SqliteNativeProvider
{
    private static bool _initialized;
    private static readonly object InitLock = new();

    private static bool? _supportsEncryption;

    /// <summary>Installs the provider if that has not happened yet. Safe to call repeatedly.</summary>
    internal static void EnsureInitialized()
    {
        if (_initialized) return;

        lock (InitLock)
        {
            if (_initialized) return;
            SQLitePCL.Batteries_V2.Init();
            _initialized = true;
        }
    }

    /// <summary>
    /// True when the installed provider can encrypt — i.e. it is SQLCipher, which answers
    /// <c>PRAGMA cipher_version</c>. A plain e_sqlite3 build returns no rows for that pragma.
    /// </summary>
    /// <remarks>
    /// Probed once, on an in-memory database: it reads no file, so asking early costs nothing and
    /// can be answered before any real connection exists. This is the check that turns "the
    /// connection string says <c>Password=</c> but nothing enforces it" from a silent security
    /// downgrade into a startup failure.
    /// </remarks>
    internal static bool SupportsEncryption
    {
        get
        {
            if (_supportsEncryption is { } known) return known;

            EnsureInitialized();

            lock (InitLock)
            {
                if (_supportsEncryption is { } raced) return raced;

                bool supported;
                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:"))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "PRAGMA cipher_version;";
                    supported = command.ExecuteScalar() is string version && version.Length > 0;
                }

                _supportsEncryption = supported;
                return supported;
            }
        }
    }
}
