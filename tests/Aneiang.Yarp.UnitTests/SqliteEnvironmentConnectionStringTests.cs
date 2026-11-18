using Aneiang.Yarp.Storage;
using Aneiang.Yarp.Storage.Sqlite;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Tests for <see cref="SqliteConnectionFactory.ResolveConnectionString"/>. Two properties matter:
/// the runtime environment must keep using the configured string verbatim (so an existing
/// deployment upgrades in place, with its data still where it was), and other environments must
/// land in a different file (so editing one cannot touch another).
/// </summary>
public class SqliteEnvironmentConnectionStringTests
{
    private static StorageOptions Options(string connectionString, string runtime = "prod")
        => new()
        {
            Sqlite = new SqliteStorageOptions { ConnectionString = connectionString },
            Environments = new StorageEnvironmentOptions { Runtime = runtime, Names = new List<string> { runtime, "dev" } }
        };

    [Fact]
    public void Runtime_environment_uses_the_configured_string_verbatim()
    {
        var options = Options("Data Source=gateway-store.db");

        Assert.Equal("Data Source=gateway-store.db",
            SqliteConnectionFactory.ResolveConnectionString(options, "prod"));
    }

    [Fact]
    public void Runtime_environment_matching_is_case_insensitive()
    {
        var options = Options("Data Source=gateway-store.db");

        Assert.Equal("Data Source=gateway-store.db",
            SqliteConnectionFactory.ResolveConnectionString(options, "PROD"));
    }

    [Fact]
    public void Other_environments_get_a_suffixed_file_next_to_the_runtime_one()
    {
        var options = Options("Data Source=gateway-store.db");

        var resolved = SqliteConnectionFactory.ResolveConnectionString(options, "dev");

        Assert.Equal("gateway-store.dev.db", new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(resolved).DataSource);
    }

    [Fact]
    public void The_suffix_goes_before_the_extension_not_after_it()
    {
        // gateway-store.db.dev looks like a different database type to anything reading the
        // directory, and to the operator looking for it.
        var options = Options("Data Source=/var/lib/gateway/store.sqlite");

        var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(
            SqliteConnectionFactory.ResolveConnectionString(options, "staging")).DataSource;

        // Path.Combine normalises the separator to the current platform, so assert the file name
        // and keep the directory assertion platform-independent.
        Assert.Equal("store.staging.sqlite", Path.GetFileName(dataSource));
        Assert.Contains("gateway" + Path.DirectorySeparatorChar, dataSource);
    }

    [Fact]
    public void A_relative_path_with_a_directory_keeps_that_directory()
    {
        var options = Options("Data Source=data/gateway.db");

        var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(
            SqliteConnectionFactory.ResolveConnectionString(options, "dev")).DataSource;

        Assert.Equal(Path.Combine("data", "gateway.dev.db"), dataSource);
    }

    [Fact]
    public void A_file_name_without_an_extension_still_gets_a_distinct_name()
    {
        var options = Options("Data Source=store");

        Assert.Equal("store.dev", new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(
            SqliteConnectionFactory.ResolveConnectionString(options, "dev")).DataSource);
    }

    [Fact]
    public void Other_connection_string_parameters_survive_the_rewrite()
    {
        // A password (SQLCipher) that got dropped for non-runtime environments would produce a
        // store the gateway cannot open at all — and only when someone switches to it.
        var options = Options("Data Source=gateway-store.db;Password=s3cret;Cache=Shared");

        var resolved = SqliteConnectionFactory.ResolveConnectionString(options, "dev");
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(resolved);

        Assert.Equal("gateway-store.dev.db", builder.DataSource);
        Assert.Equal("s3cret", builder.Password);
        Assert.Equal(Microsoft.Data.Sqlite.SqliteCacheMode.Shared, builder.Cache);
    }

    [Fact]
    public void In_memory_databases_are_returned_unchanged()
    {
        var options = Options("Data Source=:memory:");

        Assert.Equal("Data Source=:memory:",
            SqliteConnectionFactory.ResolveConnectionString(options, "dev"));
    }

    [Fact]
    public void An_empty_connection_string_falls_back_to_the_default_file()
    {
        var options = Options("");

        Assert.Equal("Data Source=gateway-store.db",
            SqliteConnectionFactory.ResolveConnectionString(options, "prod"));
        Assert.Equal("gateway-store.dev.db", new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(
            SqliteConnectionFactory.ResolveConnectionString(options, "dev")).DataSource);
    }

    [Fact]
    public void An_unparseable_connection_string_is_returned_as_is_rather_than_guessed_at()
    {
        var options = Options("not a connection string");

        Assert.Equal("not a connection string",
            SqliteConnectionFactory.ResolveConnectionString(options, "dev"));
    }

    [Fact]
    public void Environments_never_collide_with_each_other()
    {
        var options = Options("Data Source=gateway-store.db");

        var files = new[] { "prod", "dev", "staging" }
            .Select(e => new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(
                SqliteConnectionFactory.ResolveConnectionString(options, e)).DataSource)
            .ToList();

        Assert.Equal(files.Count, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
