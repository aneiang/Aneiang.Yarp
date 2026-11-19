namespace Aneiang.Yarp.Dashboard.Infrastructure;

/// <summary>
/// Installs the SQLCipher provider for the dashboard assembly's own <c>SqliteConnection</c> use.
/// </summary>
/// <remarks>
/// <para>
/// The dashboard references <c>Microsoft.Data.Sqlite.Core</c>, which does not init a provider on
/// its own. Storage initializes one too, but relying on that would make
/// <c>DashboardPagesController.BackupDatabase</c> work only because some other component happened
/// to run first — an ordering dependency that stays invisible until it breaks.
/// </para>
/// <para>
/// So the controller's constructor calls <see cref="EnsureInitialized"/>: an instance must exist
/// before the action runs, which makes the ordering explicit and local. A module initializer would
/// also work, but running side effects on assembly load is the thing CA2255 warns about, and the
/// alternative is one line at the one call site that needs it.
/// </para>
/// </remarks>
internal static class DashboardSqliteProvider
{
    private static bool _initialized;
    private static readonly object InitLock = new();

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
}
