using Aneiang.Yarp.Models;
using Yarp.ReverseProxy.Configuration;

namespace Aneiang.Yarp.Dashboard.Modules.Environments;

/// <summary>One differing field, already rendered as text so the UI does not have to interpret types.</summary>
public sealed class FieldChange
{
    public required string Field { get; init; }
    public string? Before { get; init; }
    public string? After { get; init; }
}

/// <summary>One route that exists on only one side, or exists on both with different content.</summary>
public sealed class RouteDiffEntry
{
    /// <summary><c>added</c>, <c>removed</c> or <c>changed</c> — from the target's point of view a promotion applies this.</summary>
    public required string Kind { get; init; }

    public required string RouteId { get; init; }

    public List<FieldChange> Changes { get; init; } = [];
}

/// <summary>One cluster difference, with destinations broken out because that is what usually changed.</summary>
public sealed class ClusterDiffEntry
{
    public required string Kind { get; init; }

    public required string ClusterId { get; init; }

    public List<FieldChange> Changes { get; init; } = [];

    public List<string> DestinationsAdded { get; init; } = [];

    public List<string> DestinationsRemoved { get; init; } = [];

    public List<FieldChange> DestinationsChanged { get; init; } = [];
}

/// <summary>
/// The change a promotion would make: source → target.
/// </summary>
/// <remarks>
/// Deliberately not a JSON diff. The question an operator is answering at the approval step is
/// "which routes and clusters change, and how", so every field is already rendered as text —
/// <c>Destinations: 2 → 5</c> is reviewable, <c>-{...}+{...}</c> is not.
/// </remarks>
public sealed class EnvironmentDiff
{
    public required string Source { get; init; }
    public required string Target { get; init; }

    /// <summary>True when a promotion would write exactly what is already there.</summary>
    public bool Identical => RouteChanges.Count == 0 && ClusterChanges.Count == 0;

    public List<RouteDiffEntry> RouteChanges { get; init; } = [];
    public List<ClusterDiffEntry> ClusterChanges { get; init; } = [];

    public int RoutesAdded => RouteChanges.Count(entry => entry.Kind == "added");
    public int RoutesRemoved => RouteChanges.Count(entry => entry.Kind == "removed");
    public int RoutesChanged => RouteChanges.Count(entry => entry.Kind == "changed");
    public int ClustersAdded => ClusterChanges.Count(entry => entry.Kind == "added");
    public int ClustersRemoved => ClusterChanges.Count(entry => entry.Kind == "removed");
    public int ClustersChanged => ClusterChanges.Count(entry => entry.Kind == "changed");
}

/// <summary>Computes the diff between two environments' configurations.</summary>
public sealed class EnvironmentDiffService
{
    public EnvironmentDiff Compare(string source, GatewayDynamicConfig sourceConfig, string target, GatewayDynamicConfig targetConfig)
    {
        var diff = new EnvironmentDiff { Source = source, Target = target };

        var sourceRoutes = Index(sourceConfig.Routes);
        var targetRoutes = Index(targetConfig.Routes);

        foreach (var id in sourceRoutes.Keys.Union(targetRoutes.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            sourceRoutes.TryGetValue(id, out var inSource);
            targetRoutes.TryGetValue(id, out var inTarget);

            if (inSource is null)
            {
                diff.RouteChanges.Add(new RouteDiffEntry { Kind = "removed", RouteId = id });
                continue;
            }

            if (inTarget is null)
            {
                diff.RouteChanges.Add(new RouteDiffEntry { Kind = "added", RouteId = id });
                continue;
            }

            var changes = CompareRoute(inSource.Config, inTarget.Config);
            if (changes.Count > 0)
                diff.RouteChanges.Add(new RouteDiffEntry { Kind = "changed", RouteId = id, Changes = changes });
        }

        var sourceClusters = Index(sourceConfig.Clusters);
        var targetClusters = Index(targetConfig.Clusters);

        foreach (var id in sourceClusters.Keys.Union(targetClusters.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            sourceClusters.TryGetValue(id, out var inSource);
            targetClusters.TryGetValue(id, out var inTarget);

            if (inSource is null)
            {
                diff.ClusterChanges.Add(new ClusterDiffEntry { Kind = "removed", ClusterId = id });
                continue;
            }

            if (inTarget is null)
            {
                diff.ClusterChanges.Add(new ClusterDiffEntry { Kind = "added", ClusterId = id });
                continue;
            }

            var entry = CompareCluster(inSource.Config, inTarget.Config);
            if (entry is not null)
                diff.ClusterChanges.Add(entry);
        }

        return diff;
    }

    private static Dictionary<string, T> Index<T>(IEnumerable<T> items) where T : class
    {
        var map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var id = item switch
            {
                DynamicRouteConfig route => route.Config.RouteId,
                DynamicClusterConfig cluster => cluster.Config.ClusterId,
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(id)) map[id] = item;
        }

        return map;
    }

    /// <summary>
    /// Only fields that change what traffic does. A route's <c>Metadata</c> or a cluster's
    /// <c>HttpClient</c> tuning produce very noisy diffs and rarely decide a promotion; the ones
    /// below are the ones an operator checks before approving.
    /// </summary>
    private static List<FieldChange> CompareRoute(RouteConfig source, RouteConfig target)
    {
        var changes = new List<FieldChange>();

        Add(changes, "clusterId", target.ClusterId, source.ClusterId);
        Add(changes, "path", target.Match?.Path, source.Match?.Path);
        Add(changes, "hosts", Join(target.Match?.Hosts), Join(source.Match?.Hosts));
        Add(changes, "methods", Join(target.Match?.Methods), Join(source.Match?.Methods));
        Add(changes, "order", target.Order?.ToString(), source.Order?.ToString());
        Add(changes, "transforms", DescribeTransforms(target.Transforms), DescribeTransforms(source.Transforms));
        Add(changes, "authorizationPolicy", target.AuthorizationPolicy, source.AuthorizationPolicy);
        Add(changes, "corsPolicy", target.CorsPolicy, source.CorsPolicy);
        Add(changes, "rateLimiterPolicy", target.RateLimiterPolicy, source.RateLimiterPolicy);
        Add(changes, "outputCachePolicy", target.OutputCachePolicy, source.OutputCachePolicy);
        Add(changes, "timeout", target.Timeout?.ToString(), source.Timeout?.ToString());
        Add(changes, "maxRequestBodySize", target.MaxRequestBodySize?.ToString(), source.MaxRequestBodySize?.ToString());

        return changes;
    }

    private static ClusterDiffEntry? CompareCluster(ClusterConfig source, ClusterConfig target)
    {
        var changes = new List<FieldChange>();

        Add(changes, "loadBalancingPolicy", target.LoadBalancingPolicy, source.LoadBalancingPolicy);
        Add(changes, "sessionAffinity", DescribeAffinity(target.SessionAffinity), DescribeAffinity(source.SessionAffinity));
        Add(changes, "healthCheck",
            target.HealthCheck?.Active?.Enabled == true || target.HealthCheck?.Passive?.Enabled == true ? "on" : "off",
            source.HealthCheck?.Active?.Enabled == true || source.HealthCheck?.Passive?.Enabled == true ? "on" : "off");

        var sourceDestinations = source.Destinations ?? new Dictionary<string, DestinationConfig>();
        var targetDestinations = target.Destinations ?? new Dictionary<string, DestinationConfig>();

        var added = sourceDestinations.Keys.Except(targetDestinations.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k).ToList();
        var removed = targetDestinations.Keys.Except(sourceDestinations.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k).ToList();

        var moved = new List<FieldChange>();
        foreach (var name in sourceDestinations.Keys.Intersect(targetDestinations.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var before = targetDestinations[name].Address;
            var after = sourceDestinations[name].Address;
            if (!string.Equals(before, after, StringComparison.Ordinal))
                moved.Add(new FieldChange { Field = name, Before = before, After = after });
        }

        if (changes.Count == 0 && added.Count == 0 && removed.Count == 0 && moved.Count == 0)
            return null;

        return new ClusterDiffEntry
        {
            Kind = "changed",
            ClusterId = source.ClusterId ?? string.Empty,
            Changes = changes,
            DestinationsAdded = added,
            DestinationsRemoved = removed,
            DestinationsChanged = moved
        };
    }

    private static void Add(List<FieldChange> changes, string field, string? before, string? after)
    {
        if (string.Equals(before, after, StringComparison.Ordinal)) return;
        changes.Add(new FieldChange { Field = field, Before = before, After = after });
    }

    private static string? Join(IReadOnlyList<string>? values) =>
        values is { Count: > 0 } ? string.Join(", ", values) : null;

    /// <summary>
    /// Bound to a local: `x.SessionAffinity?.Enabled == true ? x.SessionAffinity.Policy` reads fine
    /// but the compiler cannot follow the null check through the conditional, and a warning here
    /// would hide the next real one.
    /// </summary>
    private static string DescribeAffinity(SessionAffinityConfig? affinity)
    {
        if (affinity is null || affinity.Enabled != true) return "off";
        return affinity.Policy?.ToString() ?? "enabled";
    }

    private static string? DescribeTransforms(IReadOnlyList<IReadOnlyDictionary<string, string>>? transforms)
    {
        if (transforms is not { Count: > 0 }) return null;

        return string.Join("; ", transforms.Select(transform =>
            string.Join(",", transform.Select(pair => pair.Key))));
    }
}
