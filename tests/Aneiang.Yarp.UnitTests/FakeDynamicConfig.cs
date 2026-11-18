using Aneiang.Yarp.Models;
using Aneiang.Yarp.Services;
using Yarp.ReverseProxy.Configuration;
using HealthCheckConfig = Aneiang.Yarp.Models.HealthCheckConfig;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Minimal <see cref="IDynamicYarpConfigService"/> stand-in. Reads are real; writes are rejected
/// unless a test opts in — <see cref="TryAddClusterConfig"/> is implemented because the canary
/// service legitimately rewrites a whole cluster to change destination weights.
/// </summary>
internal sealed class FakeDynamicConfig : IDynamicYarpConfigService
{
    public List<RouteConfig> Routes { get; } = new();
    public List<ClusterConfig> Clusters { get; } = new();
    public GatewayDynamicConfig DynamicConfig { get; } = new();

    /// <summary>Cluster ids written through <see cref="TryAddClusterConfig"/>, in order.</summary>
    public List<string> WrittenClusters { get; } = new();

    /// <summary>When false, <see cref="TryAddClusterConfig"/> fails without touching state.</summary>
    public bool AllowClusterWrites { get; set; } = true;

    public ClusterConfig? GetCluster(string clusterId)
        => Clusters.FirstOrDefault(c => string.Equals(c.ClusterId, clusterId, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<ClusterConfig> GetClusters() => Clusters;

    public GatewayDynamicConfig? GetDynamicConfig() => DynamicConfig;

    public IReadOnlyList<RouteConfig> GetRoutes() => Routes;

    public Task<RouteOperationResult> TryAddRoute(RegisterRouteRequest request, string source = "dynamic", string? createdBy = null)
        => throw new NotSupportedException();

    public Task<RouteOperationResult> TryAddRouteConfig(RouteConfig route, string source = "dashboard", string? createdBy = "dashboard-user")
        => throw new NotSupportedException();

    public Task<RouteOperationResult> TryAddClusterConfig(ClusterConfig cluster, string source = "dashboard", string? createdBy = "dashboard-user")
    {
        if (!AllowClusterWrites)
            return Task.FromResult(new RouteOperationResult(false, "cluster writes disabled for this test"));

        var index = Clusters.FindIndex(c =>
            string.Equals(c.ClusterId, cluster.ClusterId, StringComparison.OrdinalIgnoreCase));

        if (index >= 0) Clusters[index] = cluster;
        else Clusters.Add(cluster);

        WrittenClusters.Add(cluster.ClusterId ?? string.Empty);
        return Task.FromResult(new RouteOperationResult(true, "ok"));
    }

    public Task<RouteOperationResult> TryRemoveRoute(string routeName, string? clientIp = null, bool removeOrphanedCluster = true)
        => throw new NotSupportedException();

    public Task<RouteOperationResult> TryAddCluster(string clusterId, Dictionary<string, string> destinations,
        string? loadBalancingPolicy = null, HealthCheckConfig? healthCheck = null,
        string source = "dynamic", string? createdBy = null, Dictionary<string, string>? metadata = null)
        => throw new NotSupportedException();

    public Task<RouteOperationResult> TryAddCluster(CreateClusterRequest request, string source = "dynamic", string? createdBy = null)
        => throw new NotSupportedException();

    public Task<RouteOperationResult> TryUpdateCluster(string clusterId, UpdateClusterRequest request)
        => throw new NotSupportedException();

    public Task<RouteOperationResult> TryRemoveCluster(string clusterId)
        => throw new NotSupportedException();

    public Task<RouteOperationResult> TryRenameCluster(string oldClusterId, string newClusterId,
        Dictionary<string, string> destinations, string? loadBalancingPolicy = null,
        HealthCheckConfig? healthCheck = null, string source = "dashboard", string? createdBy = "dashboard-user")
        => throw new NotSupportedException();

    public void RefreshConfig() { }

    public Task SaveDynamicConfig() => Task.CompletedTask;

    public Task ReplaceAllConfig(IReadOnlyList<RouteConfig> newRoutes, IReadOnlyList<ClusterConfig> newClusters,
        string source = "rollback", string? createdBy = "dashboard-user")
        => throw new NotSupportedException();

    public Task<RouteOperationResult> ImportBatchAsync(IReadOnlyList<ClusterConfig> clusters, IReadOnlyList<RouteConfig> routes,
        string source = "import", string? createdBy = "dashboard-user")
        => throw new NotSupportedException();

    public bool UpdateHeartbeat(string routeName, string? clientIp = null) => false;

    public Task<RouteOperationResult> TryRenameRoute(string oldRouteId, string newRouteId, RegisterRouteRequest request,
        string source = "dashboard", string? createdBy = "dashboard-user")
        => throw new NotSupportedException();

    public Task<RouteOperationResult> TrySetRouteEnabled(string routeId, bool enabled, string? createdBy = "dashboard-user")
        => throw new NotSupportedException();

    public Task<BatchOperationResult> BatchDeleteClustersAsync(IReadOnlyList<string> clusterIds, string? createdBy = "dashboard-user")
        => throw new NotSupportedException();

    public Task<BatchOperationResult> BatchDeleteRoutesAsync(IReadOnlyList<string> routeIds, bool removeOrphanedClusters = false,
        string? createdBy = "dashboard-user")
        => throw new NotSupportedException();

    public Task<BatchOperationResult> BatchSetRoutesEnabledAsync(IReadOnlyList<string> routeIds, bool enabled, string? createdBy = "dashboard-user")
        => throw new NotSupportedException();

    public Task<EnvironmentSwitchResult> SwitchEnvironmentAsync(string environment, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
