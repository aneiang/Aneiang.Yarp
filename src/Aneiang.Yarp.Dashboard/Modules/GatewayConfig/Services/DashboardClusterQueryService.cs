using Aneiang.Yarp.Dashboard.Infrastructure.Plugin;
using Aneiang.Yarp.Dashboard.Modules.Dashboard.Models;
using Aneiang.Yarp.Services;
using Aneiang.Yarp.Storage;
using Microsoft.Extensions.Caching.Memory;
using Yarp.ReverseProxy;

namespace Aneiang.Yarp.Dashboard.Modules.GatewayConfig.Services;

/// <summary>
/// Implementation of dashboard cluster query service.
/// Uses <see cref="IMemoryCache"/> for unified caching (shared across all query services).
/// </summary>
internal sealed class DashboardClusterQueryService : IDashboardClusterQueryService
{
    private readonly DynamicYarpConfigService _dynamicConfig;
    private readonly GatewayPluginExecutionPlanProvider _executionPlans;
    private readonly IMemoryCache _memoryCache;
    private readonly IStorageEnvironmentResolver _environments;
    private readonly IProxyStateLookup _proxyStateLookup;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Initializes a new instance of DashboardClusterQueryService.
    /// </summary>
    /// <param name="dynamicConfig">Dynamic YARP config service.</param>
    /// <param name="executionPlans">Current immutable plugin execution plan provider.</param>
    /// <param name="memoryCache">Unified memory cache for all query services.</param>
    /// <param name="proxyStateLookup">YARP runtime state lookup for live health data.</param>
    /// <param name="environments">Resolves which environment the cache key belongs to.</param>
    public DashboardClusterQueryService(
        DynamicYarpConfigService dynamicConfig,
        GatewayPluginExecutionPlanProvider executionPlans,
        IMemoryCache memoryCache,
        IProxyStateLookup proxyStateLookup,
        IStorageEnvironmentResolver environments)
    {
        _dynamicConfig = dynamicConfig;
        _executionPlans = executionPlans;
        _memoryCache = memoryCache;
        _proxyStateLookup = proxyStateLookup;
        _environments = environments;
    }

    /// <summary>
    /// Scoped by environment for the same reason as the route query: switching the editing view
    /// changes the answer, and a shared key would keep serving the previous environment's clusters.
    /// </summary>
    private string CacheKey => "dashboard:clusters:query:" + _environments.CurrentEnvironment;

    /// <inheritdoc />
    public IReadOnlyList<DashboardClusterResponse> GetClusters()
    {
        var cacheKey = CacheKey;

        if (_memoryCache.TryGetValue(cacheKey, out IReadOnlyList<DashboardClusterResponse>? cached) && cached is not null)
        {
            return cached;
        }

        var clusters = _dynamicConfig.GetClusters();

        var responses = clusters?
            .Select(cluster =>
            {
                // Try to get runtime state for live health data
                _proxyStateLookup.TryGetCluster(cluster.ClusterId, out var runtimeState);
                return DashboardClusterMapper.MapToResponse(cluster, _dynamicConfig, _executionPlans.Current, runtimeState);
            })
            .ToList() ?? new List<DashboardClusterResponse>();

        _memoryCache.Set(cacheKey, responses, CacheDuration);
        return responses;
    }

    /// <summary>
    /// Invalidates the clusters query cache. Call after configuration changes.
    /// </summary>
    public void Invalidate() => _memoryCache.Remove(CacheKey);
}
