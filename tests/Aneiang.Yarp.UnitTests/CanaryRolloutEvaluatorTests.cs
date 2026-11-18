using Aneiang.Yarp.Dashboard.Modules.Canary.Models;
using Aneiang.Yarp.Dashboard.Modules.Canary.Services;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// The canary gate decides whether a bad release keeps serving real traffic. These tests pin the
/// four outcomes and, more importantly, pin the order they are checked in — sample size before
/// attribution, attribution before action.
/// </summary>
public class CanaryRolloutEvaluatorTests
{
    private const double Threshold = 0.2;
    private const int MinSamples = 50;
    private const int MaxExtensions = 3;

    [Fact]
    public void TooFewSamples_HoldsAndExtends()
    {
        var decision = Evaluate(canaryRequests: 10, canaryFailures: 9);

        Assert.Equal(CanaryVerdict.Hold, decision.Verdict);
        Assert.True(decision.ExtendWindow);
        Assert.Equal(CanaryReasonCodes.InsufficientSamples, decision.Code);
    }

    [Fact]
    public void TooFewSamples_DoesNotRollBackEvenAtHundredPercentFailure()
    {
        // Three requests, three failures = 100%. Rolling back on this would make the feature flap on
        // every quiet cluster, which is how people learn to disable it.
        var decision = Evaluate(canaryRequests: 3, canaryFailures: 3);

        Assert.Equal(CanaryVerdict.Hold, decision.Verdict);
    }

    [Fact]
    public void HealthyCanary_Advances()
    {
        var decision = Evaluate(canaryRequests: 100, canaryFailures: 1);

        Assert.Equal(CanaryVerdict.Advance, decision.Verdict);
        Assert.Equal(CanaryReasonCodes.Healthy, decision.Code);
    }

    [Fact]
    public void CanaryOverThreshold_BaselineHealthy_RollsBack()
    {
        var decision = Evaluate(canaryRequests: 100, canaryFailures: 40, baselineRequests: 400, baselineFailures: 4);

        Assert.Equal(CanaryVerdict.Rollback, decision.Verdict);
        Assert.Equal(CanaryReasonCodes.CanaryDegraded, decision.Code);
        Assert.Contains("40%", decision.Message);
    }

    [Fact]
    public void CanaryOverThreshold_BaselineAlsoOverThreshold_PausesInsteadOfRollingBack()
    {
        // Both sides at 50%: the upstream is broken, not the release. Rolling back would destroy the
        // rollout's evidence and send the operator to the wrong place.
        var decision = Evaluate(canaryRequests: 100, canaryFailures: 50, baselineRequests: 100, baselineFailures: 55);

        Assert.Equal(CanaryVerdict.Pause, decision.Verdict);
        Assert.Equal(CanaryReasonCodes.BaselineDegraded, decision.Code);
    }

    [Fact]
    public void NoCanaryTraffic_AfterMaxExtensions_PausesWithTheRightDiagnosis()
    {
        var observation = new CanaryObservation(0, 0, 500, 0, ExpectBaselineTraffic: true);

        var decision = CanaryRolloutEvaluator.Evaluate(observation, Threshold, MinSamples, MaxExtensions, MaxExtensions);

        Assert.Equal(CanaryVerdict.Pause, decision.Verdict);
        Assert.Equal(CanaryReasonCodes.NoCanaryTraffic, decision.Code);
        // The message has to point at the likely cause, not just say "no data".
        Assert.Contains("Weighted", decision.Message);
    }

    [Fact]
    public void HundredPercentStep_DoesNotRequireBaselineSamples()
    {
        // At the last step the baseline is parked at weight 0, so it can never reach MinSamples.
        // Demanding it would deadlock every rollout at 100%.
        var observation = new CanaryObservation(200, 2, 0, 0, ExpectBaselineTraffic: false);

        var decision = CanaryRolloutEvaluator.Evaluate(observation, Threshold, MinSamples, 0, MaxExtensions);

        Assert.Equal(CanaryVerdict.Advance, decision.Verdict);
    }

    [Fact]
    public void HundredPercentStep_StillRollsBackOnFailures()
    {
        var observation = new CanaryObservation(200, 100, 0, 0, ExpectBaselineTraffic: false);

        var decision = CanaryRolloutEvaluator.Evaluate(observation, Threshold, MinSamples, 0, MaxExtensions);

        Assert.Equal(CanaryVerdict.Rollback, decision.Verdict);
    }

    [Fact]
    public void ExactlyAtThreshold_IsHealthy()
    {
        // 20% threshold with 20% failures must not trip: the boundary is documented as "above".
        var observation = new CanaryObservation(100, 20, 400, 4, ExpectBaselineTraffic: true);

        var decision = CanaryRolloutEvaluator.Evaluate(observation, Threshold, MinSamples, 0, MaxExtensions);

        Assert.Equal(CanaryVerdict.Advance, decision.Verdict);
    }

    private static CanaryDecision Evaluate(
        long canaryRequests,
        long canaryFailures,
        long baselineRequests = 400,
        long baselineFailures = 0)
    {
        var observation = new CanaryObservation(
            canaryRequests, canaryFailures, baselineRequests, baselineFailures, ExpectBaselineTraffic: true);

        return CanaryRolloutEvaluator.Evaluate(observation, Threshold, MinSamples, 0, MaxExtensions);
    }
}
