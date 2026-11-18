namespace Aneiang.Yarp.Storage;

/// <summary>
/// Environment isolation settings. Binds from <c>Gateway:Storage:Environments</c>.
/// An "environment" is a separate store (for SQLite: a separate database file), so that
/// editing staging can never move traffic on production, and a broken dev store cannot
/// take out the production one.
/// </summary>
public class StorageEnvironmentOptions
{
    /// <summary>The environment name used when none is configured.</summary>
    public const string DefaultRuntime = "prod";

    /// <summary>
    /// The environment the gateway actually forwards traffic for. It is fixed for the
    /// lifetime of the process: switching the editing view never changes what is being served.
    /// </summary>
    public string Runtime { get; set; } = DefaultRuntime;

    /// <summary>
    /// Environments that exist and can be selected for editing. The runtime environment is
    /// always part of the list even if it is not spelled out here. Leaving this at its default
    /// single entry means multi-environment support is off and the gateway behaves exactly as before.
    /// </summary>
    public List<string> Names { get; set; } = new() { DefaultRuntime };
}

/// <summary>One selectable environment.</summary>
/// <param name="Name">Environment name, e.g. <c>prod</c> / <c>staging</c> / <c>dev</c>.</param>
/// <param name="IsRuntime">
/// True for the environment the gateway is serving. Editing this one publishes to live traffic;
/// editing any other one only touches its own store.
/// </param>
public sealed record StorageEnvironmentInfo(string Name, bool IsRuntime);

/// <summary>
/// Tells storage implementations which environment to open, and tells the gateway whether
/// the environment currently being edited is the one it is serving.
/// </summary>
/// <remarks>
/// Two distinct notions live here and mixing them up is the whole risk of this feature:
/// <list type="bullet">
/// <item><see cref="RuntimeEnvironment"/> — fixed, decides what the gateway forwards.</item>
/// <item><see cref="CurrentEnvironment"/> — mutable, decides what the Dashboard reads and writes.</item>
/// </list>
/// They are equal at startup. They stop being equal the moment someone switches the editing
/// view, and from that point on publishing a snapshot would be wrong.
/// </remarks>
public interface IStorageEnvironmentResolver
{
    /// <summary>The environment the gateway serves. Never changes after startup.</summary>
    string RuntimeEnvironment { get; }

    /// <summary>The environment the control plane is currently reading and writing.</summary>
    string CurrentEnvironment { get; }

    /// <summary>All selectable environments, runtime first.</summary>
    IReadOnlyList<StorageEnvironmentInfo> Environments { get; }

    /// <summary>Whether the editing view is currently pointed at the runtime environment.</summary>
    bool IsEditingRuntime { get; }

    /// <summary>
    /// Points the editing view at <paramref name="environment"/>. Does not affect forwarding.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown environment.</exception>
    void SwitchTo(string environment);
}

/// <summary>
/// Fallback resolver for hosts that register storage without the gateway core.
/// Single environment, editing is always the runtime one, <see cref="SwitchTo"/> is a no-op
/// unless the name matches.
/// </summary>
public sealed class SingleEnvironmentResolver : IStorageEnvironmentResolver
{
    public SingleEnvironmentResolver(string runtime)
    {
        RuntimeEnvironment = runtime;
        CurrentEnvironment = runtime;
        Environments = new[] { new StorageEnvironmentInfo(runtime, true) };
    }

    public string RuntimeEnvironment { get; }

    public string CurrentEnvironment { get; private set; }

    public IReadOnlyList<StorageEnvironmentInfo> Environments { get; }

    public bool IsEditingRuntime => string.Equals(CurrentEnvironment, RuntimeEnvironment, StringComparison.OrdinalIgnoreCase);

    public void SwitchTo(string environment)
    {
        if (!string.Equals(environment, RuntimeEnvironment, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unknown environment '{environment}'. This host only has '{RuntimeEnvironment}'.", nameof(environment));

        CurrentEnvironment = RuntimeEnvironment;
    }
}
