using Aneiang.Yarp.Services;
using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Model;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Covers the canary policy. These assertions are about *exact* ratios: the whole reason this
/// policy exists is that "roughly 10%" is not good enough when you are deciding whether to promote
/// a release, so a test that only checks "both destinations were picked" would be worthless.
/// </summary>
public class WeightedLoadBalancingPolicyTests
{
    private const string CanaryHeader = WeightedLoadBalancingPolicy.DefaultHeaderName;

    [Fact]
    public void SingleDestination_IsReturnedDirectly()
    {
        var policy = new WeightedLoadBalancingPolicy();
        var destinations = new[] { Destination("d1", weight: 1) };

        var picked = policy.PickDestination(Context(), Cluster(), destinations);

        Assert.Same(destinations[0], picked);
    }

    [Fact]
    public void NoDestinations_ReturnsNull()
    {
        var policy = new WeightedLoadBalancingPolicy();

        var picked = policy.PickDestination(Context(), Cluster(), Array.Empty<DestinationState>());

        Assert.Null(picked);
    }

    [Fact]
    public void EqualWeights_SplitEvenly()
    {
        var policy = new WeightedLoadBalancingPolicy();
        var destinations = new[]
        {
            Destination("d1", weight: 1),
            Destination("d2", weight: 1),
            Destination("d3", weight: 1)
        };

        var counts = Pick(policy, destinations, 30);

        Assert.Equal(10, counts["d1"]);
        Assert.Equal(10, counts["d2"]);
        Assert.Equal(10, counts["d3"]);
    }

    [Fact]
    public void NinetyTen_IsExactOverOneHundredPicks()
    {
        var policy = new WeightedLoadBalancingPolicy();
        var destinations = new[]
        {
            Destination("stable", weight: 90),
            Destination("canary", weight: 10)
        };

        var counts = Pick(policy, destinations, 100);

        Assert.Equal(90, counts["stable"]);
        Assert.Equal(10, counts["canary"]);
    }

    [Fact]
    public void NinetyNineToOne_StillHonoursTheSingleSlot()
    {
        var policy = new WeightedLoadBalancingPolicy();
        var destinations = new[]
        {
            Destination("stable", weight: 99),
            Destination("canary", weight: 1)
        };

        var counts = Pick(policy, destinations, 100);

        Assert.Equal(99, counts["stable"]);
        Assert.Equal(1, counts["canary"]);
    }

    [Fact]
    public void Canary_DoesNotGetTheFirstRequest()
    {
        // Smooth weighted round-robin interleaves instead of bunching, and it never hands the very
        // first request of a cycle to the 10% node. A canary that receives request #1 would make
        // every rollout look broken for a second before the weights settle.
        var policy = new WeightedLoadBalancingPolicy();
        var destinations = new[]
        {
            Destination("stable", weight: 9),
            Destination("canary", weight: 1)
        };

        var sequence = PickSequence(policy, destinations, 10);

        Assert.NotEqual("canary", sequence[0]);
        Assert.Equal(1, sequence.Count(id => id == "canary"));
    }

    [Fact]
    public void ZeroWeight_IsNeverSelected()
    {
        var policy = new WeightedLoadBalancingPolicy();
        var destinations = new[]
        {
            Destination("live", weight: 5),
            Destination("parked", weight: 0)
        };

        var counts = Pick(policy, destinations, 50);

        Assert.Equal(50, counts["live"]);
        Assert.False(counts.ContainsKey("parked"));
    }

    [Fact]
    public void AllZeroWeights_FallBackToFirstDestination()
    {
        // A weight table that sums to zero must not turn into a 503 storm.
        var policy = new WeightedLoadBalancingPolicy();
        var destinations = new[]
        {
            Destination("d1", weight: 0),
            Destination("d2", weight: 0)
        };

        var picked = policy.PickDestination(Context(), Cluster(), destinations);

        Assert.Same(destinations[0], picked);
    }

    [Fact]
    public void MissingWeight_UsesDefaultOfOne()
    {
        var policy = new WeightedLoadBalancingPolicy();
        var destinations = new[]
        {
            Destination("explicit", weight: 2),
            Destination("implicit", weight: null)
        };

        var counts = Pick(policy, destinations, 30);

        Assert.Equal(20, counts["explicit"]);
        Assert.Equal(10, counts["implicit"]);
    }

    [Fact]
    public void NonNumericWeight_FallsBackToDefaultInsteadOfThrowing()
    {
        var policy = new WeightedLoadBalancingPolicy();
        var destinations = new[]
        {
            Destination("a", weight: "abc"),
            Destination("b", weight: 1)
        };

        var counts = Pick(policy, destinations, 20);

        Assert.Equal(10, counts["a"]);
        Assert.Equal(10, counts["b"]);
    }

    [Fact]
    public void NegativeWeight_IsTreatedAsParked()
    {
        var policy = new WeightedLoadBalancingPolicy();
        var destinations = new[]
        {
            Destination("live", weight: 3),
            Destination("bad", weight: -5)
        };

        var counts = Pick(policy, destinations, 9);

        Assert.Equal(9, counts["live"]);
    }

    [Fact]
    public void ClusterDefaultWeight_AppliesToUntaggedDestinations()
    {
        var policy = new WeightedLoadBalancingPolicy();
        var cluster = Cluster(metadata: new Dictionary<string, string>
        {
            [WeightedLoadBalancingPolicy.MetadataDefaultWeight] = "3"
        });
        var destinations = new[]
        {
            Destination("d1", weight: null),
            Destination("d2", weight: null)
        };

        var counts = Pick(policy, destinations, 20, cluster);

        Assert.Equal(10, counts["d1"]);
        Assert.Equal(10, counts["d2"]);
    }

    [Fact]
    public void CustomWeightKey_IsHonoured()
    {
        var policy = new WeightedLoadBalancingPolicy();
        var cluster = Cluster(metadata: new Dictionary<string, string>
        {
            [WeightedLoadBalancingPolicy.MetadataWeightKey] = "TrafficShare"
        });
        var destinations = new[]
        {
            Destination("d1", weight: null, extraMetadata: new Dictionary<string, string> { ["TrafficShare"] = "3" }),
            Destination("d2", weight: null, extraMetadata: new Dictionary<string, string> { ["TrafficShare"] = "1" })
        };

        var counts = Pick(policy, destinations, 40, cluster);

        Assert.Equal(30, counts["d1"]);
        Assert.Equal(10, counts["d2"]);
    }

    [Fact]
    public void PinnedByDestinationId_WinsOverWeights()
    {
        var policy = new WeightedLoadBalancingPolicy();
        var destinations = new[]
        {
            Destination("stable", weight: 1000),
            Destination("canary", weight: 0)
        };
        var context = Context(CanaryHeader, "canary");

        for (var i = 0; i < 20; i++)
        {
            var picked = policy.PickDestination(context, Cluster(), destinations);
            Assert.Equal("canary", picked!.DestinationId);
        }
    }

    [Fact]
    public void PinnedByTag_WinsOverWeights()
    {
        var policy = new WeightedLoadBalancingPolicy();
        var destinations = new[]
        {
            Destination("stable", weight: 1000),
            Destination("canary", weight: 0, extraMetadata: new Dictionary<string, string> { ["Tag"] = "v2" })
        };
        var context = Context(CanaryHeader, "v2");

        var picked = policy.PickDestination(context, Cluster(), destinations);

        Assert.Equal("canary", picked!.DestinationId);
    }

    [Fact]
    public void PinIsCaseInsensitive()
    {
        var policy = new WeightedLoadBalancingPolicy();
        var destinations = new[]
        {
            Destination("stable", weight: 1),
            Destination("canary", weight: 0, extraMetadata: new Dictionary<string, string> { ["Tag"] = "v2" })
        };

        var picked = policy.PickDestination(Context(CanaryHeader, "V2"), Cluster(), destinations);

        Assert.Equal("canary", picked!.DestinationId);
    }

    [Fact]
    public void UnknownPin_FallsBackToWeights()
    {
        // A stale bookmark pointing at a removed canary must not produce a 503.
        var policy = new WeightedLoadBalancingPolicy();
        var destinations = new[]
        {
            Destination("a", weight: 1),
            Destination("b", weight: 1)
        };

        var picked = policy.PickDestination(Context(CanaryHeader, "does-not-exist"), Cluster(), destinations);

        Assert.NotNull(picked);
        Assert.Contains(picked!.DestinationId, new[] { "a", "b" });
    }

    [Fact]
    public void CustomHeaderName_IsHonoured()
    {
        var policy = new WeightedLoadBalancingPolicy();
        var cluster = Cluster(metadata: new Dictionary<string, string>
        {
            [WeightedLoadBalancingPolicy.MetadataHeaderName] = "X-Debug-Node"
        });
        var destinations = new[]
        {
            Destination("stable", weight: 1),
            Destination("canary", weight: 0)
        };

        var picked = policy.PickDestination(Context("X-Debug-Node", "canary"), cluster, destinations);

        Assert.Equal("canary", picked!.DestinationId);
    }

    [Fact]
    public void BlankPinHeader_IsIgnored()
    {
        var policy = new WeightedLoadBalancingPolicy();
        var destinations = new[]
        {
            Destination("a", weight: 1),
            Destination("b", weight: 1)
        };

        var picked = policy.PickDestination(Context(CanaryHeader, "   "), Cluster(), destinations);

        Assert.NotNull(picked);
    }

    [Fact]
    public void DestinationSetChange_ResetsTheAccumulator()
    {
        // Health transitions and config reloads hand over a different set; the accumulator must not
        // carry stale per-index values across, or the first picks after a reload are biased.
        var policy = new WeightedLoadBalancingPolicy();
        var first = new[]
        {
            Destination("a", weight: 1),
            Destination("b", weight: 1),
            Destination("c", weight: 1)
        };

        Pick(policy, first, 7);

        var second = new[]
        {
            Destination("a", weight: 1),
            Destination("b", weight: 1)
        };
        var counts = Pick(policy, second, 20);

        Assert.Equal(10, counts["a"]);
        Assert.Equal(10, counts["b"]);
    }

    [Fact]
    public void ConcurrentPicks_StillHonourTheExactRatio()
    {
        var policy = new WeightedLoadBalancingPolicy();
        var destinations = new[]
        {
            Destination("stable", weight: 1),
            Destination("canary", weight: 1)
        };

        var names = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 400, _ =>
        {
            var picked = policy.PickDestination(Context(), Cluster(), destinations);
            names.Add(picked!.DestinationId);
        });

        var materialized = names.ToArray();
        Assert.Equal(200, materialized.Count(n => n == "stable"));
        Assert.Equal(200, materialized.Count(n => n == "canary"));
    }

    [Fact]
    public void ClusterWithoutModel_DoesNotThrow()
    {
        var policy = new WeightedLoadBalancingPolicy();
        var destinations = new[]
        {
            Destination("a", weight: 1),
            Destination("b", weight: 1)
        };

        var picked = policy.PickDestination(Context(), new ClusterState("c1"), destinations);

        Assert.NotNull(picked);
    }

    [Fact]
    public void WeightChange_OnTheSameDestinationInstances_TakesEffect()
    {
        var policy = new WeightedLoadBalancingPolicy();
        var first = Destination("d1", weight: 1);
        var second = Destination("d2", weight: 1);
        var destinations = new[] { first, second };

        Assert.Equal(10, Pick(policy, destinations, 20)["d1"]);

        // This is the exact shape a canary weight change arrives in. YARP keeps DestinationState
        // alive for the lifetime of the cluster and only swaps its model on reload:
        //   currentDestination.Model = new DestinationModel(incomingDestination);
        // A cache keyed on the state instance would therefore never notice, and the weights would
        // stay frozen at whatever the first request saw.
        ReplaceModel(first, "d1", "0");
        ReplaceModel(second, "d2", "1");

        var counts = Pick(policy, destinations, 20);

        Assert.Equal(0, counts.GetValueOrDefault("d1"));
        Assert.Equal(20, counts.GetValueOrDefault("d2"));
    }

    [Fact]
    public void WeightKeyOverrideAppliedLater_TakesEffect()
    {
        var policy = new WeightedLoadBalancingPolicy();
        var destinations = new[]
        {
            Destination("d1", weight: 1),
            Destination("d2", weight: 1)
        };

        // Warm the cache against the default weight key.
        Assert.Equal(10, Pick(policy, destinations, 20)["d1"]);

        // Now point the cluster at a different metadata key and give only d2 a non-zero value there.
        ReplaceModel(destinations[0], "d1", "0", "CanaryWeight");
        ReplaceModel(destinations[1], "d2", "1", "CanaryWeight");

        var cluster = Cluster(new Dictionary<string, string>
        {
            [WeightedLoadBalancingPolicy.MetadataWeightKey] = "CanaryWeight"
        });

        var counts = Pick(policy, destinations, 20, cluster);

        Assert.Equal(0, counts.GetValueOrDefault("d1"));
        Assert.Equal(20, counts.GetValueOrDefault("d2"));
    }

    /// <summary>
    /// Swaps a destination's model in place, which is what YARP's config reload does
    /// (<c>currentDestination.Model = new DestinationModel(...)</c> in <c>ProxyConfigManager</c>).
    /// The setter is internal to YARP, so a reload cannot be reproduced from the public API without
    /// reflection — and reproducing it is the whole point of the two tests above.
    /// </summary>
    private static void ReplaceModel(
        DestinationState destination,
        string id,
        string weight,
        string weightKey = WeightedLoadBalancingPolicy.DefaultWeightKey)
    {
        var model = new DestinationModel(new DestinationConfig
        {
            Address = $"http://{id}.local",
            Metadata = new Dictionary<string, string> { [weightKey] = weight }
        });

        ModelProperty.SetValue(destination, model);
    }

    private static readonly System.Reflection.PropertyInfo ModelProperty =
        typeof(DestinationState).GetProperty(nameof(DestinationState.Model))!;

    private static Dictionary<string, int> Pick(
        WeightedLoadBalancingPolicy policy,
        DestinationState[] destinations,
        int count,
        ClusterState? cluster = null)
    {
        cluster ??= Cluster();
        var context = Context();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < count; i++)
        {
            var picked = policy.PickDestination(context, cluster, destinations);
            Assert.NotNull(picked);
            counts[picked!.DestinationId] = counts.GetValueOrDefault(picked.DestinationId) + 1;
        }

        return counts;
    }

    private static List<string> PickSequence(
        WeightedLoadBalancingPolicy policy,
        DestinationState[] destinations,
        int count)
    {
        var cluster = Cluster();
        var context = Context();
        var sequence = new List<string>(count);

        for (var i = 0; i < count; i++)
            sequence.Add(policy.PickDestination(context, cluster, destinations)!.DestinationId);

        return sequence;
    }

    private static DefaultHttpContext Context(string? headerName = null, string? headerValue = null)
    {
        var context = new DefaultHttpContext();
        if (headerName is not null)
            context.Request.Headers[headerName] = headerValue ?? string.Empty;

        return context;
    }

    private static ClusterState Cluster(Dictionary<string, string>? metadata = null, string clusterId = "c1")
    {
        var config = new ClusterConfig
        {
            ClusterId = clusterId,
            Metadata = metadata
        };

        return new ClusterState(clusterId, new ClusterModel(config, new HttpMessageInvoker(new HttpClientHandler())));
    }

    private static DestinationState Destination(
        string id,
        object? weight,
        Dictionary<string, string>? extraMetadata = null)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (weight is not null)
            metadata[WeightedLoadBalancingPolicy.DefaultWeightKey] = weight.ToString()!;

        if (extraMetadata is not null)
        {
            foreach (var (key, value) in extraMetadata)
                metadata[key] = value;
        }

        var config = new DestinationConfig
        {
            Address = $"http://{id}.local",
            Metadata = metadata
        };

        return new DestinationState(id, new DestinationModel(config));
    }
}
