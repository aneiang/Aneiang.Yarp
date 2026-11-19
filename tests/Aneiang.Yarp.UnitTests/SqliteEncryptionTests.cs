using System.Text;
using Aneiang.Yarp.Storage;
using Aneiang.Yarp.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Proves whether <c>Password=</c> in the connection string actually encrypts the database file.
/// </summary>
/// <remarks>
/// <para>
/// SEC-15 in <c>docs/security/security-remediation.md</c> called this out as unprovable by reading
/// code: the project referenced both <c>bundle_e_sqlcipher</c> and, transitively through
/// <c>Microsoft.Data.Sqlite</c>, <c>bundle_e_sqlite3</c>, and both answer
/// <c>Batteries_V2.Init()</c>. Whichever ran last set the provider, so <c>Password=</c> either
/// encrypted the file or was silently ignored depending on load order. Every shipped sample
/// config sets <c>Password=123456</c>, so "silently ignored" would mean a deployment believing it
/// was encrypted when it was not.
/// </para>
/// <para>
/// These tests pin the answer down two ways, because they fail for different reasons: the plaintext
/// scan catches a provider that ignores the key, and the wrong-key test catches a provider that
/// stores the key but never enforces it. Keeping them means re-introducing the e_sqlite3 bundle
/// fails the build instead of quietly shipping an unencrypted database.
/// </para>
/// </remarks>
public class SqliteEncryptionTests
{
    private const string Sentinel = "SENTINEL-PLAINTEXT-MARKER-9F3A";
    private const string Key = "correct-horse-battery-staple";

    /// <summary>
    /// One connection string, with the file in a temp directory so the tests never touch a real
    /// store. Pooling is off so the file handle is released as soon as the connection closes —
    /// a pooled connection would keep the page cache alive and defeat the byte scan.
    /// </summary>
    private static string ConnectionString(string path, string? password)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            Password = password
        };
        return builder.ToString();
    }

    private static string TempDatabasePath()
        => Path.Combine(Path.GetTempPath(), $"sec15-{Guid.NewGuid():N}.db");

    private static void CreateTableWithSentinel(string path, string? password)
    {
        using var connection = new SqliteConnection(ConnectionString(path, password));
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE probe (v TEXT NOT NULL);";
        command.ExecuteNonQuery();

        command.CommandText = "INSERT INTO probe (v) VALUES ($v);";
        command.Parameters.AddWithValue("$v", Sentinel);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Reads the database and its sidecar files as raw bytes. The sentinel has to be searched in
    /// both ASCII and UTF-16LE: SQLite stores text as UTF-8, but an unencrypted page read by a
    /// tool that decoded it differently would still leak the marker, and looking for one encoding
    /// only would make a false "encrypted" verdict possible.
    /// </summary>
    private static string ReadRawDatabaseText(string path)
    {
        var builder = new StringBuilder();
        foreach (var candidate in new[] { path, path + "-wal", path + "-journal" })
        {
            if (!File.Exists(candidate)) continue;

            var bytes = File.ReadAllBytes(candidate);
            builder.Append(Encoding.UTF8.GetString(bytes));
            builder.Append(Encoding.Unicode.GetString(bytes));
        }
        return builder.ToString();
    }

    private static void DeleteDatabase(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            try { if (File.Exists(candidate)) File.Delete(candidate); }
            catch (IOException) { /* temp file, best effort */ }
        }
    }

    [Fact]
    public void The_provider_under_test_is_the_sqlcipher_one()
    {
        // Guards the premise of the other two tests: without this the suite could pass by never
        // installing a provider, or by installing e_sqlite3 and failing everything with a
        // misleading message.
        SqliteNativeProvider.EnsureInitialized();

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA cipher_version;";
        var version = command.ExecuteScalar() as string;

        Assert.False(string.IsNullOrWhiteSpace(version));
    }

    [Fact]
    public void A_password_connection_string_is_accepted_because_the_provider_can_honour_it()
    {
        // The negative case — a provider that ignores Password= — cannot be simulated from here,
        // but this pins the positive one: the guard in SqliteConnectionFactory does not fire on a
        // correctly built package. Re-introducing bundle_e_sqlite3 so that it wins the
        // Batteries_V2.Init() race turns this into a startup failure, which is the whole point.
        var options = Options.Create(new StorageOptions
        {
            Sqlite = new SqliteStorageOptions { ConnectionString = $"Data Source={TempDatabasePath()};Password={Key}" }
        });

        var factory = new SqliteConnectionFactory(
            options, new ServiceCollection().BuildServiceProvider());

        Assert.NotNull(factory);
        Assert.True(SqliteNativeProvider.SupportsEncryption);
    }

    [Fact]
    public void A_password_protected_database_stores_no_plaintext()
    {
        var path = TempDatabasePath();
        try
        {
            CreateTableWithSentinel(path, Key);

            var raw = ReadRawDatabaseText(path);

            Assert.DoesNotContain(Sentinel, raw, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public void A_plaintext_database_is_not_silently_opened_with_a_password()
    {
        // The upgrade path, pinned deliberately. A database written by an older build in which
        // e_sqlite3 won the provider race is plaintext; after this change SQLCipher definitely wins,
        // so opening it with Password= set now fails instead of continuing to work while pretending
        // to be encrypted. That failure is the correct outcome, but it is a breaking change, so it
        // gets a test rather than a surprise.
        var path = TempDatabasePath();
        try
        {
            CreateTableWithSentinel(path, password: null);

            var failure = Record.Exception(() =>
            {
                using var connection = new SqliteConnection(ConnectionString(path, Key));
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT v FROM probe;";
                command.ExecuteScalar();
            });

            var sqliteException = Assert.IsType<SqliteException>(failure);
            Assert.Equal(26, sqliteException.SqliteErrorCode);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public void A_password_protected_database_rejects_the_wrong_key()
    {
        var path = TempDatabasePath();
        try
        {
            CreateTableWithSentinel(path, Key);

            var failure = Record.Exception(() =>
            {
                using var connection = new SqliteConnection(ConnectionString(path, "not-the-key"));

                // The key is applied as part of opening the connection, so SQLCipher rejects a
                // wrong key there — error 26, "file is not a database" — rather than on the first
                // query. Catching both points keeps the assertion about the outcome instead of
                // about which line Microsoft.Data.Sqlite happens to fail on.
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT v FROM probe;";
                command.ExecuteScalar();
            });

            // 26 is SQLITE_NOTADB: the page did not decrypt. Any other failure (locked, IO) would
            // mean this test proved nothing about the key.
            var sqliteException = Assert.IsType<SqliteException>(failure);
            Assert.Equal(26, sqliteException.SqliteErrorCode);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }
}
