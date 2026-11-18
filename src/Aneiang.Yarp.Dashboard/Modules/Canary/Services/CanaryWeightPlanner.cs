using System.Globalization;
using Yarp.ReverseProxy.Configuration;

namespace Aneiang.Yarp.Dashboard.Modules.Canary.Services;

/// <summary>
/// Turns "give the canary 10% of traffic" into concrete YARP destination weights.
/// </summary>
/// <remarks>
/// <para>
/// The split is computed against the <em>live</em> cluster config every time a step is applied,
/// not once at rollout start. A destination added mid-rollout then simply joins the baseline side
/// with its default weight, instead of being left out of the weight table and — depending on the
/// policy — silently starved.
/// </para>
/// <para>
/// Weights are relative, but they are scaled to a fixed total so that the promised percentage is
/// actually what the smooth weighted round-robin delivers: at 10%, one node of ten parts receives
/// exactly one pick in ten.
/// </para>
/// </remarks>
public static class CanaryWeightPlanner
{
    /// <summary>Total weight the plan is scaled to. Larger means finer granularity for small ramps.</summary>
    public const int TotalWeight = 10_000;

    /// <summary>Default destination metadata key holding the weight; mirrors the LB policy.</summary>
    public const string DefaultWeightKey = "Weight";

    /// <summary>Cluster metadata key that overrides the destination weight key.</summary>
    public const string WeightKeyOverride = "Weighted.WeightKey";

    /// <summary>Cluster metadata key supplying the weight used when a destination omits one.</summary>
    public const string DefaultWeightOverride = "Weighted.DefaultWeight";

    /// <summary>Resolves the metadata key that holds a destination's weight for this cluster.</summary>
    public static string ResolveWeightKey(ClusterConfig cluster)
        => cluster.Metadata is { Count: > 0 } md
           && md.TryGetValue(WeightKeyOverride, out var key)
           && !string.IsNullOrWhiteSpace(key)
            ? key!
            : DefaultWeightKey;

    /// <summary>Resolves the weight applied to destinations that do not declare one.</summary>
    public static int ResolveDefaultWeight(ClusterConfig cluster)
        => cluster.Metadata is { Count: > 0 } md
           && md.TryGetValue(DefaultWeightOverride, out var raw)
           && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
           && parsed > 0
            ? parsed
            : 1;

    /// <summary>Reads a destination's effective weight, falling back to the cluster default.</summary>
    public static int ReadWeight(DestinationConfig destination, string weightKey, int defaultWeight)
    {
        if (destination.Metadata is { Count: > 0 } md
            && md.TryGetValue(weightKey, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed < 0 ? 0 : parsed;
        }

        return defaultWeight;
    }

    /// <summary>
    /// Builds the weight table for one step.
    /// </summary>
    /// <param name="cluster">Live cluster configuration.</param>
    /// <param name="canaryIds">Destinations that should receive the canary share.</param>
    /// <param name="canaryPercent">Share of traffic for the canary side, 0..100.</param>
    /// <param name="weights">Resulting weight per destination id.</param>
    /// <param name="error">Why planning failed, when it did.</param>
    /// <returns><c>true</c> when a usable weight table was produced.</returns>
    public static bool TryPlan(
        ClusterConfig cluster,
        IReadOnlyCollection<string> canaryIds,
        int canaryPercent,
        out Dictionary<string, int> weights,
        out string? error)
    {
        weights = new Dictionary<string, int>(StringComparer.Ordinal);
        error = null;

        var destinations = cluster.Destinations;
        if (destinations is null || destinations.Count == 0)
        {
            error = "Cluster has no destinations";
            return false;
        }

        if (canaryIds.Count == 0)
        {
            error = "No canary destination selected";
            return false;
        }

        var canarySet = new HashSet<string>(canaryIds, StringComparer.Ordinal);
        var missing = canarySet.Where(id => !destinations.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            error = "Destination(s) not found in cluster: " + string.Join(", ", missing);
            return false;
        }

        var baselineIds = destinations.Keys.Where(id => !canarySet.Contains(id)).ToList();
        if (baselineIds.Count == 0)
        {
            error = "Every destination is a canary destination — there is nothing to compare against";
            return false;
        }

        if (canaryPercent is < 0 or > 100)
        {
            error = "Canary percent must be between 0 and 100";
            return false;
        }

        var weightKey = ResolveWeightKey(cluster);
        var defaultWeight = ResolveDefaultWeight(cluster);

        var canaryTotal = (int)Math.Round(TotalWeight * (canaryPercent / 100d), MidpointRounding.AwayFromZero);
        var baselineTotal = TotalWeight - canaryTotal;

        // The baseline keeps its own internal proportions — a cluster that was already split
        // 90/10 must stay 90/10 inside its share, otherwise starting a canary silently rewrites
        // a weighting somebody else configured.
        var baselineProportions = baselineIds
            .Select(id => Math.Max(0, ReadWeight(destinations[id], weightKey, defaultWeight)))
            .ToList();

        Distribute(canaryIds.ToList(), canaryTotal, null, weights);
        Distribute(baselineIds, baselineTotal, baselineProportions, weights);

        if (weights.Values.All(w => w <= 0))
        {
            error = "Computed weight table is all zero";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Applies a weight table to a cluster, preserving every other destination property.
    /// </summary>
    /// <param name="cluster">Cluster to copy.</param>
    /// <param name="weights">Weight per destination id.</param>
    /// <returns>A new cluster config; untouched destinations keep their metadata.</returns>
    public static ClusterConfig ApplyWeights(ClusterConfig cluster, IReadOnlyDictionary<string, int> weights)
    {
        if (cluster.Destinations is null || cluster.Destinations.Count == 0)
            return cluster;

        var weightKey = ResolveWeightKey(cluster);
        var updated = new Dictionary<string, DestinationConfig>(cluster.Destinations.Count, StringComparer.Ordinal);

        foreach (var pair in cluster.Destinations)
        {
            if (!weights.TryGetValue(pair.Key, out var weight))
            {
                updated[pair.Key] = pair.Value;
                continue;
            }

            var metadata = pair.Value.Metadata is { Count: > 0 }
                ? new Dictionary<string, string>(pair.Value.Metadata, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

            metadata[weightKey] = weight.ToString(CultureInfo.InvariantCulture);

            updated[pair.Key] = new DestinationConfig
            {
                Address = pair.Value.Address,
                Host = pair.Value.Host,
                Health = pair.Value.Health,
                Metadata = metadata
            };
        }

        return cluster with { Destinations = updated };
    }

    /// <summary>
    /// Restores a previously captured weight table, removing the key from destinations that did not
    /// declare one before.
    /// </summary>
    /// <param name="cluster">Cluster to copy.</param>
    /// <param name="weights">Weight per destination id captured before the rollout.</param>
    /// <param name="explicitIds">Ids that had an explicit weight key at capture time.</param>
    /// <returns>A new cluster config that matches the captured state exactly.</returns>
    public static ClusterConfig RestoreWeights(
        ClusterConfig cluster,
        IReadOnlyDictionary<string, int> weights,
        IReadOnlyCollection<string> explicitIds)
    {
        if (cluster.Destinations is null || cluster.Destinations.Count == 0)
            return cluster;

        var weightKey = ResolveWeightKey(cluster);
        var explicitSet = new HashSet<string>(explicitIds, StringComparer.Ordinal);
        var updated = new Dictionary<string, DestinationConfig>(cluster.Destinations.Count, StringComparer.Ordinal);

        foreach (var pair in cluster.Destinations)
        {
            if (!weights.TryGetValue(pair.Key, out var weight))
            {
                updated[pair.Key] = pair.Value;
                continue;
            }

            var metadata = pair.Value.Metadata is { Count: > 0 }
                ? new Dictionary<string, string>(pair.Value.Metadata, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

            if (explicitSet.Contains(pair.Key))
                metadata[weightKey] = weight.ToString(CultureInfo.InvariantCulture);
            else
                metadata.Remove(weightKey);

            updated[pair.Key] = new DestinationConfig
            {
                Address = pair.Value.Address,
                Host = pair.Value.Host,
                Health = pair.Value.Health,
                Metadata = metadata.Count == 0 ? null : metadata
            };
        }

        return cluster with { Destinations = updated };
    }

    /// <summary>
    /// Splits <paramref name="total"/> across <paramref name="ids"/> and writes the result.
    /// </summary>
    /// <remarks>
    /// The remainder is always folded back into the largest share so the parts sum to
    /// <paramref name="total"/> exactly. Without that, rounding could leave the canary at 9.98%
    /// of a 10% step, and "the ratio is off by a rounding error" is a bug report nobody enjoys.
    /// </remarks>
    private static void Distribute(
        IReadOnlyList<string> ids,
        int total,
        IReadOnlyList<int>? proportions,
        Dictionary<string, int> sink)
    {
        if (ids.Count == 0)
            return;

        if (total <= 0)
        {
            foreach (var id in ids)
                sink[id] = 0;
            return;
        }

        var shares = new int[ids.Count];
        var sum = proportions?.Sum() ?? 0;

        if (proportions is not null && sum > 0)
        {
            for (var i = 0; i < ids.Count; i++)
                shares[i] = (int)Math.Round(total * (double)proportions[i] / sum, MidpointRounding.AwayFromZero);
        }
        else
        {
            var baseShare = total / ids.Count;
            var remainder = total % ids.Count;
            for (var i = 0; i < ids.Count; i++)
                shares[i] = baseShare + (i < remainder ? 1 : 0);
        }

        var delta = total - shares.Sum();
        if (delta != 0)
        {
            var target = 0;
            for (var i = 1; i < shares.Length; i++)
            {
                if (shares[i] > shares[target]) target = i;
            }

            shares[target] = Math.Max(0, shares[target] + delta);
        }

        for (var i = 0; i < ids.Count; i++)
            sink[ids[i]] = shares[i];
    }
}
