using Aneiang.Yarp.Dashboard.Modules.Dashboard.Models;

namespace Aneiang.Yarp.Dashboard.Modules.GatewayConfig.Services;

/// <summary>
/// Service for querying route information.
/// </summary>
public interface IDashboardRouteQueryService
{
    /// <summary>
    /// Gets all routes with their configurations and match criteria.
    /// </summary>
    /// <returns>List of route responses.</returns>
    IReadOnlyList<DashboardRouteResponse> GetRoutes();

    /// <summary>
    /// Drops the cached result for the environment currently being edited.
    /// Call after any configuration mutation so the next read reflects it.
    /// </summary>
    void Invalidate();
}
