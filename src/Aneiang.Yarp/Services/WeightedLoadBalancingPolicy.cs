using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.LoadBalancing;
using Yarp.ReverseProxy.Model;

namespace Aneiang.Yarp.Services;

/// <summary>
/// Smooth weighted round-robin load balancing, built for staged rollouts (canary / blue-green).
/// </summary>
/// <remarks>
/// <para>
/// YARP's built-in <c>PowerOfTwoChoices</c> also reads a weight, but it uses it only as a tie-break
/// between two randomly sampled destinations — the resulting split is approximate and drifts from
/// request to request. For a canary you want the opposite: a <em>stated</em> ratio that actually
/// holds, so "10% of traffic on v2" is 10 of every 100 requests and not "roughly 10%, give or take".
/// </para>
/// <para>
/// This policy implements nginx-style smooth weighted round-robin. Over one full cycle each
/// destination is selected exactly <c>weight / sum(weights)</c> of the time, and the picks are
/// interleaved rather than bunched. State is per cluster and is rebuilt whenever the destination
/// set, a destination's weight, or the cluster's weight configuration changes — so moving a
/// rollout from 10% to 30% takes effect on the next request, not on the next restart.
/// </para>
/// <para>Configuration, all optional and read from cluster metadata:</para>
/// <list type="table">
/// <item><term>Weighted.WeightKey</term><description>Destination metadata key holding the weight. Default <c>Weight</c>.</description></item>
/// <item><term>Weighted.DefaultWeight</term><description>Weight for destinations without one. Default <c>1</c>.</description></item>
/// <item><term>Weighted.HeaderName</term><description>Request header that pins a caller to one destination. Default <c>X-Gateway-Canary</c>.</description></item>
/// <item><term>Weighted.TagKey</term><description>Destination metadata key matched against that header. Default <c>Tag</c>.</description></item>
/// </list>
/// <para>
/// The pin header accepts either a destination id (<c>d2</c>) or a tag (<c>v2</c>). It exists so a
/// rollout can be checked by a human before any real traffic is shifted — the classic sequence is
/// "pin yourself, verify, then move the weights".
/// </para>
/// <para>A weight of <c>0</c> means "registered but not receiving traffic", which is how a canary is
/// parked. When every destination weighs zero the policy falls back to the first available one
/// rather than throwing: a broken weight table must not turn into a 503 storm.</para>
/// </remarks>
public sealed class WeightedLoadBalancingPolicy : ILoadBalancingPolicy
{
    /// <summary>Name used in <c>ClusterConfig.LoadBalancingPolicy</c>.</summary>
    public const string PolicyName = "Weighted";

    /// <summary>Default destination metadata key holding the weight.</summary>
    public const string DefaultWeightKey = "Weight";

    /// <summary>Default destination metadata key holding the rollout tag.</summary>
    public const string DefaultTagKey = "Tag";

    /// <summary>Default request header used to pin a caller to one destination.</summary>
    public const string DefaultHeaderName = "X-Gateway-Canary";

    /// <summary>Cluster metadata key overriding <see cref="DefaultWeightKey"/>.</summary>
    public const string MetadataWeightKey = "Weighted.WeightKey";

    /// <summary>Cluster metadata key overriding <see cref="DefaultTagKey"/>.</summary>
    public const string MetadataTagKey = "Weighted.TagKey";

    /// <summary>Cluster metadata key overriding <see cref="DefaultHeaderName"/>.</summary>
    public const string MetadataHeaderName = "Weighted.HeaderName";

    /// <summary>Cluster metadata key supplying the weight applied to destinations that omit one.</summary>
    public const string MetadataDefaultWeight = "Weighted.DefaultWeight";

    /// <summary>Upper bound on remembered per-cluster state, so deleted clusters cannot leak.</summary>
    private const int MaxTrackedClusters = 1024;

    /// <summary>Weights are clamped here so the accumulator can never overflow.</summary>
    private const int MaxWeight = 1_000_000;

    private readonly ConcurrentDictionary<string, ClusterSelectionState> _states =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Name => PolicyName;

    /// <inheritdoc />
    public DestinationState? PickDestination(
        HttpContext context,
        ClusterState cluster,
        IReadOnlyList<DestinationState> availableDestinations)
    {
        var count = availableDestinations.Count;
        if (count == 0)
            return null;

        if (count == 1)
            return availableDestinations[0];

        var clusterMetadata = cluster.Model?.Config?.Metadata;
        var tagKey = ReadMetadata(clusterMetadata, MetadataTagKey) ?? DefaultTagKey;
        var headerName = ReadMetadata(clusterMetadata, MetadataHeaderName) ?? DefaultHeaderName;

        // A pinned caller bypasses the weights entirely. Checked first so a rollout can be verified
        // by name before any percentage moves, and so a bad pin value costs one header lookup only.
        if (TryReadPinnedTarget(context, headerName, out var pinned)
            && FindPinned(availableDestinations, pinned, tagKey) is { } target)
        {
            return target;
        }

        var weightKey = ReadMetadata(clusterMetadata, MetadataWeightKey) ?? DefaultWeightKey;
        var defaultWeight = ParseWeight(ReadMetadata(clusterMetadata, MetadataDefaultWeight), 1);
        var state = GetState(cluster.ClusterId);

        lock (state.Gate)
        {
            EnsureWeights(state, availableDestinations, weightKey, defaultWeight);

            var weights = state.Weights;
            var current = state.Current;
            var best = -1;
            var bestCurrent = int.MinValue;
            var total = 0;

            for (var i = 0; i < state.Count; i++)
            {
                var weight = weights[i];
                if (weight <= 0)
                    continue; // weight 0 = parked; it accumulates nothing and is never selected

                var value = current[i] += weight;
                total += weight;
                if (value > bestCurrent)
                {
                    bestCurrent = value;
                    best = i;
                }
            }

            if (best < 0 || total <= 0)
                return availableDestinations[0];

            current[best] -= total;
            return availableDestinations[best];
        }
    }

    private ClusterSelectionState GetState(string? clusterId)
    {
        if (string.IsNullOrEmpty(clusterId))
            return new ClusterSelectionState();

        if (_states.Count >= MaxTrackedClusters)
            _states.Clear();

        return _states.GetOrAdd(clusterId, static _ => new ClusterSelectionState());
    }

    /// <summary>
    /// Rebuilds the weight arrays when anything they were derived from has changed: the destination
    /// set, the weight of any destination, or the cluster-level weight configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The change signal is <see cref="DestinationState.Model"/>, not the <see cref="DestinationState"/>
    /// itself. YARP keeps destination state instances alive for the lifetime of the cluster and, on
    /// reload, replaces only the model:
    /// </para>
    /// <code>
    /// if (currentDestination.Model.HasChanged(incomingDestination)) { currentDestination.Model = new DestinationModel(...); }
    /// </code>
    /// <para>
    /// So two reads of <c>availableDestinations</c> across a weight change hand back the <em>same</em>
    /// objects. Caching on that identity freezes the weights at whatever the first request saw and
    /// makes every later weight change — i.e. every step of a rollout — silently inert.
    /// </para>
    /// <para>
    /// The model reference is also the cheapest correct signal: it is one reference compare per
    /// destination and it moves exactly when that destination's config was edited.
    /// </para>
    /// </remarks>
    private static void EnsureWeights(
        ClusterSelectionState state,
        IReadOnlyList<DestinationState> destinations,
        string weightKey,
        int defaultWeight)
    {
        var count = destinations.Count;
        if (Matches(state, destinations, count, weightKey, defaultWeight))
            return;

        if (state.Models.Length < count)
        {
            state.Models = new DestinationModel?[count];
            state.Ids = new string[count];
            state.Weights = new int[count];
            state.Current = new int[count];
        }
        else if (state.Models.Length > count)
        {
            Array.Clear(state.Models, count, state.Models.Length - count);
            Array.Clear(state.Ids, count, state.Ids.Length - count);
        }

        for (var i = 0; i < count; i++)
        {
            var destination = destinations[i];
            state.Models[i] = destination.Model;
            state.Ids[i] = destination.DestinationId;

            var metadata = destination.Model?.Config?.Metadata;
            var raw = metadata is not null && metadata.TryGetValue(weightKey, out var value) ? value : null;
            state.Weights[i] = string.IsNullOrWhiteSpace(raw) ? defaultWeight : ParseWeight(raw, defaultWeight);
            state.Current[i] = 0;
        }

        state.Count = count;
        state.WeightKey = weightKey;
        state.DefaultWeight = defaultWeight;
    }

    private static bool Matches(
        ClusterSelectionState state,
        IReadOnlyList<DestinationState> destinations,
        int count,
        string weightKey,
        int defaultWeight)
    {
        if (state.Count != count
            || state.DefaultWeight != defaultWeight
            || !string.Equals(state.WeightKey, weightKey, StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            var destination = destinations[i];
            if (!ReferenceEquals(state.Models[i], destination.Model)
                || !string.Equals(state.Ids[i], destination.DestinationId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadPinnedTarget(HttpContext context, string headerName, out string value)
    {
        if (context.Request.Headers.TryGetValue(headerName, out var raw) && raw.Count > 0)
        {
            var candidate = raw[0];
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                value = candidate.Trim();
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static DestinationState? FindPinned(
        IReadOnlyList<DestinationState> destinations,
        string value,
        string tagKey)
    {
        // Destination id first: "X-Gateway-Canary: d2" is the fastest way for an operator to say
        // "this exact node", and it does not depend on anyone remembering to tag the node.
        for (var i = 0; i < destinations.Count; i++)
        {
            if (string.Equals(destinations[i].DestinationId, value, StringComparison.OrdinalIgnoreCase))
                return destinations[i];
        }

        for (var i = 0; i < destinations.Count; i++)
        {
            var metadata = destinations[i].Model?.Config?.Metadata;
            if (metadata is not null
                && metadata.TryGetValue(tagKey, out var tag)
                && string.Equals(tag, value, StringComparison.OrdinalIgnoreCase))
            {
                return destinations[i];
            }
        }

        return null;
    }

    private static string? ReadMetadata(IReadOnlyDictionary<string, string>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }

    private static int ParseWeight(string? raw, int fallback)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var weight))
            return fallback;

        return Math.Clamp(weight, 0, MaxWeight);
    }

    private sealed class ClusterSelectionState
    {
        public object Gate { get; } = new();

        /// <summary>
        /// Destination models the cached weights were computed from. YARP swaps these out on a
        /// config reload while keeping the <see cref="DestinationState"/> instance, which is why the
        /// model — not the state — is what has to be compared.
        /// </summary>
        public DestinationModel?[] Models { get; set; } = Array.Empty<DestinationModel?>();

        /// <summary>Destination ids, so a reordering of the same set is not mistaken for no change.</summary>
        public string[] Ids { get; set; } = Array.Empty<string>();

        /// <summary>Number of valid entries at the front of the buffers.</summary>
        public int Count { get; set; }

        /// <summary>Destination metadata key the current weights were read with.</summary>
        public string WeightKey { get; set; } = DefaultWeightKey;

        /// <summary>Weight applied to destinations without an explicit one.</summary>
        public int DefaultWeight { get; set; } = 1;

        public int[] Weights { get; set; } = Array.Empty<int>();

        /// <summary>Smoothed accumulator; the sum across destinations is always zero after a pick.</summary>
        public int[] Current { get; set; } = Array.Empty<int>();
    }
}
