using Aneiang.Yarp.Dashboard.Modules.Dashboard.Models;

namespace Aneiang.Yarp.Dashboard.Modules.GatewayConfig.Services;

/// <summary>
/// Service for querying cluster information.
/// </summary>
public interface IDashboardClusterQueryService
{
    /// <summary>
    /// Gets all clusters with their configurations and health status.
    /// </summary>
    /// <returns>List of cluster responses.</returns>
    IReadOnlyList<DashboardClusterResponse> GetClusters();

    /// <summary>
    /// Drops the cached result for the environment currently being edited.
    /// Call after any configuration mutation so the next read reflects it.
    /// </summary>
    void Invalidate();
}
