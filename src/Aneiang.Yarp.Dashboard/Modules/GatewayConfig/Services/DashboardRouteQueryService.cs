using Aneiang.Yarp.Dashboard.Modules.Dashboard.Models;
using Aneiang.Yarp.Services;
using Aneiang.Yarp.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Aneiang.Yarp.Dashboard.Modules.GatewayConfig.Services;

/// <summary>
/// Lightweight DTO for destination info - eliminates reflection in mapper.
/// </summary>
internal readonly struct DestinationInfoEntry
{
    public string Name { get; init; }
    public string? Address { get; init; }

    public DestinationInfoEntry(string name, string? address)
    {
        Name = name;
        Address = address;
    }
}

/// <summary>
/// Implementation of dashboard route query service.
/// Uses <see cref="IMemoryCache"/> for unified caching (shared across all query services).
/// </summary>
internal sealed class DashboardRouteQueryService : IDashboardRouteQueryService
{
    private readonly DynamicYarpConfigService _dynamicConfig;
    private readonly IMemoryCache _memoryCache;
    private readonly IStorageEnvironmentResolver _environments;
    private readonly ILogger<DashboardRouteQueryService> _logger;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Initializes a new instance of DashboardRouteQueryService.
    /// </summary>
    /// <param name="dynamicConfig">Dynamic YARP config service.</param>
    /// <param name="memoryCache">Unified memory cache for all query services.</param>
    /// <param name="environments">Resolves which environment the cache key belongs to.</param>
    /// <param name="logger">Logger instance.</param>
    public DashboardRouteQueryService(
        DynamicYarpConfigService dynamicConfig,
        IMemoryCache memoryCache,
        IStorageEnvironmentResolver environments,
        ILogger<DashboardRouteQueryService> logger)
    {
        _dynamicConfig = dynamicConfig;
        _memoryCache = memoryCache;
        _environments = environments;
        _logger = logger;
    }

    /// <summary>
    /// The cache key carries the environment name on purpose. Switching the editing view changes
    /// what this query returns, and a key that ignored the environment would serve the previous
    /// environment's routes for the next five seconds — long enough to make someone believe the
    /// switch did not work, or worse, to save against the wrong list.
    /// </summary>
    private string CacheKey => "dashboard:routes:query:" + _environments.CurrentEnvironment;

    /// <inheritdoc />
    public IReadOnlyList<DashboardRouteResponse> GetRoutes()
    {
        var cacheKey = CacheKey;

        // Use IMemoryCache for unified caching with configurable TTL
        if (_memoryCache.TryGetValue(cacheKey, out IReadOnlyList<DashboardRouteResponse>? cached) && cached is not null)
        {
            return cached;
        }

        var routes = BuildRoutes();

        _memoryCache.Set(cacheKey, routes, CacheDuration);
        return routes;
    }

    /// <summary>
    /// Invalidates the routes query cache. Call after configuration changes.
    /// </summary>
    public void Invalidate() => _memoryCache.Remove(CacheKey);

    private IReadOnlyList<DashboardRouteResponse> BuildRoutes()
    {
        var routes = _dynamicConfig.GetRoutes();

        var clusterDest = _dynamicConfig.GetClusters()
            .ToDictionary(
                c => c.ClusterId,
                c => c.Destinations?.Select(d => new DestinationInfoEntry(d.Key, d.Value.Address)).ToList()
                    ?? new List<DestinationInfoEntry>(),
                StringComparer.OrdinalIgnoreCase);

        Dictionary<string, List<Dictionary<string, string>>>? configTransforms = null;
        Dictionary<string, string>? routeSources = null;
        Dictionary<string, Aneiang.Yarp.Models.DynamicRouteConfig>? dynamicRoutes = null;
        Dictionary<string, Aneiang.Yarp.Models.DynamicClusterConfig>? dynamicClusters = null;

        try
        {
            if (routes?.Count > 0)
            {
                configTransforms = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
                routeSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                var dynConfig = _dynamicConfig.GetDynamicConfig();
                dynamicRoutes = dynConfig?.Routes.ToDictionary(r => r.Config.RouteId ?? string.Empty, StringComparer.OrdinalIgnoreCase);
                dynamicClusters = dynConfig?.Clusters.ToDictionary(c => c.Config.ClusterId ?? string.Empty, StringComparer.OrdinalIgnoreCase);

                foreach (var r in routes)
                {
                    if (r.Transforms?.Count > 0)
                    {
                        configTransforms[r.RouteId] = r.Transforms
                            .Select(t => new Dictionary<string, string>(t))
                            .ToList();
                    }

                    var dynRoute = dynConfig?.Routes.FirstOrDefault(dr =>
                        string.Equals(dr.Config.RouteId, r.RouteId, StringComparison.OrdinalIgnoreCase));
                    if (dynRoute != null)
                    {
                        routeSources[r.RouteId] = dynRoute.Source;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transform extraction failed, continuing route rendering without transform info");
        }

        return routes?
            .Select(route => DashboardRouteMapper.MapToResponse(
                route,
                clusterDest,
                configTransforms,
                routeSources,
                dynamicRoutes,
                dynamicClusters))
            .OrderBy(r => r.Order)
            .ToList() ?? new List<DashboardRouteResponse>();
    }
}
