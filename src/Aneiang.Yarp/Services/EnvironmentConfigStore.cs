using Aneiang.Yarp.Models;
using Aneiang.Yarp.Storage;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Configuration;

namespace Aneiang.Yarp.Services;

/// <summary>
/// Reads and replaces the configuration of an environment that is <b>not</b> the one being edited.
/// </summary>
/// <remarks>
/// Exists for exactly two operations: showing a diff between two environments, and promoting one
/// environment's configuration into another. Both need to touch a store the control plane is not
/// pointing at, and neither may switch the editing view to get there.
/// </remarks>
public interface IEnvironmentConfigStore
{
    /// <summary>
    /// Loads the environment's routes and clusters.
    /// </summary>
    /// <exception cref="IOException">The store could not be read.</exception>
    Task<GatewayDynamicConfig> ReadAsync(string environment, CancellationToken ct = default);

    /// <summary>
    /// Makes <paramref name="configuration"/> the complete contents of the environment's store:
    /// routes and clusters that are not in it are deleted. This is the semantic a promotion needs —
    /// "the target now equals the source" — and it is deliberately not the merge that
    /// <c>ImportBatchAsync</c> performs.
    /// </summary>
    Task ReplaceAsync(string environment, GatewayDynamicConfig configuration, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class EnvironmentConfigStore : IEnvironmentConfigStore
{
    private readonly IRouteRepository _routes;
    private readonly IClusterRepository _clusters;
    private readonly ILogger<EnvironmentConfigStore> _logger;

    public EnvironmentConfigStore(
        IRouteRepository routes,
        IClusterRepository clusters,
        ILogger<EnvironmentConfigStore> logger)
    {
        _routes = routes;
        _clusters = clusters;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<GatewayDynamicConfig> ReadAsync(string environment, CancellationToken ct = default)
    {
        using var scope = StorageEnvironmentScope.Enter(environment);

        // Read through the repositories directly instead of the shared persister: that one logs
        // and returns an empty config when the store cannot be read, and an empty config is
        // indistinguishable from "this environment has nothing deployed". A diff built on that
        // would tell the operator the target is empty and invite a promotion that wipes it.
        var config = new GatewayDynamicConfig();

        try
        {
            config.Routes = (await _routes.GetAllRoutesAsync(ct)).ToRouteConfigs();

            foreach (var entity in await _clusters.GetAllClustersAsync(ct))
            {
                var cluster = entity.ToClusterConfig();
                var destinations = await _clusters.GetDestinationsAsync(entity.ClusterId, ct);
                cluster.Config = cluster.Config with { Destinations = destinations.ToDestinations() };
                config.Clusters.Add(cluster);
            }
        }
        catch (Exception ex)
        {
            throw new IOException(
                $"Environment '{environment}' could not be read: {ex.Message}. Nothing was changed.", ex);
        }

        return config;
    }

    /// <inheritdoc />
    public async Task ReplaceAsync(string environment, GatewayDynamicConfig configuration, CancellationToken ct = default)
    {
        using var scope = StorageEnvironmentScope.Enter(environment);

        var routeIds = new HashSet<string>(
            configuration.Routes.Select(r => r.Config.RouteId ?? string.Empty), StringComparer.OrdinalIgnoreCase);
        var clusterIds = new HashSet<string>(
            configuration.Clusters.Select(c => c.Config.ClusterId ?? string.Empty), StringComparer.OrdinalIgnoreCase);

        // Deletions first: a cluster cannot be removed while a route still points at it, and the
        // remaining routes are about to be rewritten anyway.
        foreach (var existing in await _routes.GetAllRoutesAsync(ct))
        {
            if (routeIds.Contains(existing.RouteId)) continue;
            await _routes.DeleteRouteAsync(existing.RouteId, ct);
            _logger.LogDebug("Promotion removed route '{RouteId}' from '{Environment}'", existing.RouteId, environment);
        }

        foreach (var existing in await _clusters.GetAllClustersAsync(ct))
        {
            if (clusterIds.Contains(existing.ClusterId)) continue;
            await _clusters.DeleteDestinationsAsync(existing.ClusterId, ct);
            await _clusters.DeleteClusterAsync(existing.ClusterId, ct);
            _logger.LogDebug("Promotion removed cluster '{ClusterId}' from '{Environment}'", existing.ClusterId, environment);
        }

        await _routes.SaveRoutesAsync(configuration.Routes.Select(r => r.ToEntity()), ct);
        await _clusters.SaveClustersAsync(configuration.Clusters.Select(c => c.ToEntity()), ct);

        foreach (var cluster in configuration.Clusters)
        {
            var clusterId = cluster.Config.ClusterId ?? string.Empty;
            var destinations = cluster.Config.Destinations ?? new Dictionary<string, DestinationConfig>();
            await _clusters.SaveDestinationsAsync(
                clusterId,
                destinations.Select(d => d.ToEntity(clusterId)),
                ct);
        }

        _logger.LogInformation(
            "Promotion wrote {Routes} route(s) and {Clusters} cluster(s) into '{Environment}'",
            configuration.Routes.Count, configuration.Clusters.Count, environment);
    }
}
