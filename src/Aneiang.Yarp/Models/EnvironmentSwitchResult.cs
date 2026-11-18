namespace Aneiang.Yarp.Models;

/// <summary>Result of switching the control plane's editing view to another environment.</summary>
/// <param name="PreviousEnvironment">Environment that was being edited before the switch.</param>
/// <param name="CurrentEnvironment">Environment being edited now.</param>
/// <param name="RuntimeEnvironment">Environment the gateway is serving; unaffected by the switch.</param>
/// <param name="Published">
/// Whether the working set was pushed to the proxy pipeline. Only true when the newly selected
/// environment is the runtime one — editing a non-runtime environment writes to its own store and
/// must never touch live traffic.
/// </param>
/// <param name="RouteCount">Routes loaded from the newly selected store (including static ones).</param>
/// <param name="ClusterCount">Clusters loaded from the newly selected store (including static ones).</param>
public sealed record EnvironmentSwitchResult(
    string PreviousEnvironment,
    string CurrentEnvironment,
    string RuntimeEnvironment,
    bool Published,
    int RouteCount,
    int ClusterCount);
