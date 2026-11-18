using Aneiang.Yarp.Dashboard.Modules.Canary.Services;
using Yarp.ReverseProxy.Configuration;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// The weight planner is where "10%" either becomes 10% or becomes a vague gesture. These tests
/// assert exact arithmetic, because the load-balancing policy downstream trusts these numbers.
/// </summary>
public class CanaryWeightPlannerTests
{
    [Fact]
    public void TenPercent_SplitsExactlyInTenParts()
    {
        var cluster = Cluster(("d1", null), ("d2", null));

        Assert.True(CanaryWeightPlanner.TryPlan(cluster, new[] { "d2" }, 10, out var weights, out var error), error);

        Assert.Equal(1000, weights["d2"]);
        Assert.Equal(9000, weights["d1"]);
        Assert.Equal(CanaryWeightPlanner.TotalWeight, weights.Values.Sum());
    }

    [Fact]
    public void EveryStep_SumsToTheTotalWeight()
    {
        var cluster = Cluster(("d1", null), ("d2", null), ("d3", null));

        foreach (var percent in new[] { 1, 5, 10, 33, 50, 67, 99, 100 })
        {
            Assert.True(CanaryWeightPlanner.TryPlan(cluster, new[] { "d3" }, percent, out var weights, out var error), error);
            Assert.Equal(CanaryWeightPlanner.TotalWeight, weights.Values.Sum());
        }
    }

    [Fact]
    public void MultipleCanaryDestinations_ShareTheCanarySideEqually()
    {
        var cluster = Cluster(("d1", null), ("d2", null), ("d3", null));

        Assert.True(CanaryWeightPlanner.TryPlan(cluster, new[] { "d2", "d3" }, 40, out var weights, out var error), error);

        Assert.Equal(2000, weights["d2"]);
        Assert.Equal(2000, weights["d3"]);
        Assert.Equal(6000, weights["d1"]);
    }

    [Fact]
    public void BaselineKeepsItsOwnProportions()
    {
        // A cluster already weighted 90/10 must stay 90/10 inside the baseline share. Otherwise
        // starting a canary silently rewrites a weighting somebody else configured.
        var cluster = Cluster(("d1", 90), ("d2", 10), ("d3", null));

        Assert.True(CanaryWeightPlanner.TryPlan(cluster, new[] { "d3" }, 20, out var weights, out var error), error);

        Assert.Equal(2000, weights["d3"]);
        Assert.Equal(7200, weights["d1"]);
        Assert.Equal(800, weights["d2"]);
    }

    [Fact]
    public void FullRollout_ParksTheBaselineAtZero()
    {
        var cluster = Cluster(("d1", null), ("d2", null));

        Assert.True(CanaryWeightPlanner.TryPlan(cluster, new[] { "d2" }, 100, out var weights, out var error), error);

        Assert.Equal(0, weights["d1"]);
        Assert.Equal(CanaryWeightPlanner.TotalWeight, weights["d2"]);
    }

    [Fact]
    public void UnknownDestination_IsRejected()
    {
        var cluster = Cluster(("d1", null), ("d2", null));

        var ok = CanaryWeightPlanner.TryPlan(cluster, new[] { "ghost" }, 10, out _, out var error);

        Assert.False(ok);
        Assert.Contains("ghost", error);
    }

    [Fact]
    public void AllDestinationsAsCanary_IsRejected()
    {
        var cluster = Cluster(("d1", null), ("d2", null));

        var ok = CanaryWeightPlanner.TryPlan(cluster, new[] { "d1", "d2" }, 10, out _, out var error);

        Assert.False(ok);
        Assert.Contains("nothing to compare", error);
    }

    [Fact]
    public void ApplyWeights_PreservesAddressAndOtherMetadata()
    {
        var cluster = Cluster(("d1", 5), ("d2", null));

        var updated = CanaryWeightPlanner.ApplyWeights(cluster, new Dictionary<string, int>
        {
            ["d1"] = 1234,
            ["d2"] = 0
        });

        Assert.Equal("http://d1", updated.Destinations!["d1"].Address);
        Assert.Equal("blue", updated.Destinations!["d1"].Metadata!["Tag"]);
        Assert.Equal("1234", updated.Destinations!["d1"].Metadata!["Weight"]);
        Assert.Equal("0", updated.Destinations!["d2"].Metadata!["Weight"]);
    }

    [Fact]
    public void RestoreWeights_RemovesKeysThatWereNotThereBefore()
    {
        // A rollback must be exact. Leaving Weight="1" on every destination is a coat of paint the
        // operator never applied, and it quietly changes what the config diff shows.
        var cluster = Cluster(("d1", 5), ("d2", null));
        var weighted = CanaryWeightPlanner.ApplyWeights(cluster, new Dictionary<string, int>
        {
            ["d1"] = 1000,
            ["d2"] = 9000
        });

        var restored = CanaryWeightPlanner.RestoreWeights(
            weighted,
            new Dictionary<string, int> { ["d1"] = 5, ["d2"] = 1 },
            new[] { "d1" });

        Assert.Equal("5", restored.Destinations!["d1"].Metadata!["Weight"]);
        Assert.False(restored.Destinations!["d2"].Metadata?.ContainsKey("Weight") ?? false);
        // Everything else on the destination survives the round-trip.
        Assert.Equal("blue", restored.Destinations!["d2"].Metadata!["Tag"]);
    }

    [Fact]
    public void WeightKeyOverride_IsHonoured()
    {
        var cluster = Cluster(("d1", null), ("d2", null)) with
        {
            Metadata = new Dictionary<string, string> { [CanaryWeightPlanner.WeightKeyOverride] = "Share" }
        };

        Assert.Equal("Share", CanaryWeightPlanner.ResolveWeightKey(cluster));

        var updated = CanaryWeightPlanner.ApplyWeights(cluster, new Dictionary<string, int> { ["d1"] = 1, ["d2"] = 2 });

        Assert.Equal("2", updated.Destinations!["d2"].Metadata!["Share"]);
        Assert.False(updated.Destinations!["d2"].Metadata!.ContainsKey("Weight"));
    }

    private static ClusterConfig Cluster(params (string Id, int? Weight)[] destinations)
    {
        var dict = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal);
        foreach (var (id, weight) in destinations)
        {
            dict[id] = new DestinationConfig
            {
                Address = "http://" + id,
                Metadata = weight is null
                    ? new Dictionary<string, string> { ["Tag"] = "blue" }
                    : new Dictionary<string, string> { ["Tag"] = "blue", ["Weight"] = weight.Value.ToString() }
            };
        }

        return new ClusterConfig
        {
            ClusterId = "c1",
            Destinations = dict
        };
    }
}
