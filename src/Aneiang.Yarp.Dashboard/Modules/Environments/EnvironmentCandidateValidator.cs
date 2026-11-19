using Aneiang.Yarp.Models;
using Yarp.ReverseProxy.Configuration;

namespace Aneiang.Yarp.Dashboard.Modules.Environments;

/// <summary>
/// Structural checks on a configuration that is about to become another environment's configuration.
/// </summary>
/// <remarks>
/// Deliberately self-contained: the candidate must be judged against its own clusters, never against
/// whatever happens to be running. Validating a staging route against production's live cluster list
/// produces errors that are artefacts of the comparison, not of the configuration.
/// </remarks>
public static class EnvironmentCandidateValidator
{
    /// <summary>
    /// Returns the reasons this configuration must not be promoted. An empty list means it is
    /// structurally sound.
    /// </summary>
    /// <param name="config">The candidate — normally the source environment's configuration.</param>
    /// <param name="allowEmptySource">
    /// Permit a configuration with no routes. Off by default: an empty source is far more often a
    /// store that failed to load than an intentional "deploy nothing", and the difference is every
    /// route in the target.
    /// </param>
    public static List<string> Validate(GatewayDynamicConfig config, bool allowEmptySource)
    {
        var problems = new List<string>();

        if (config.Routes.Count == 0 && !allowEmptySource)
        {
            problems.Add("The source environment has no routes. Promoting it would delete every route in the target; " +
                         "approve replacing the target with an empty configuration if that is really the intent.");
        }

        var clusterIds = new HashSet<string>(
            config.Clusters.Select(c => c.Config.ClusterId ?? string.Empty).Where(id => id.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        var seenRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var route in config.Routes)
        {
            var id = route.Config.RouteId;
            if (string.IsNullOrWhiteSpace(id))
            {
                problems.Add("A route has no id.");
                continue;
            }

            if (!seenRoutes.Add(id))
                problems.Add($"Route '{id}' appears more than once.");

            if (string.IsNullOrWhiteSpace(route.Config.ClusterId))
            {
                problems.Add($"Route '{id}' has no cluster.");
            }
            else if (!clusterIds.Contains(route.Config.ClusterId))
            {
                problems.Add($"Route '{id}' points at cluster '{route.Config.ClusterId}', which does not exist in this environment.");
            }

            var match = route.Config.Match;
            if (match is null || (string.IsNullOrWhiteSpace(match.Path) && (match.Hosts is null || match.Hosts.Count == 0)))
                problems.Add($"Route '{id}' has no path and no host, so it can never match.");
        }

        foreach (var cluster in config.Clusters)
        {
            var id = cluster.Config.ClusterId ?? string.Empty;
            if (id.Length == 0)
            {
                problems.Add("A cluster has no id.");
                continue;
            }

            if (cluster.Config.Destinations is not { Count: > 0 })
                problems.Add($"Cluster '{id}' has no destinations.");

            foreach (var destination in cluster.Config.Destinations ?? new Dictionary<string, DestinationConfig>())
            {
                var address = destination.Value?.Address;
                if (string.IsNullOrWhiteSpace(address) ||
                    !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    problems.Add($"Cluster '{id}' destination '{destination.Key}' has an invalid address '{address}'.");
                }
            }
        }

        return problems;
    }
}
