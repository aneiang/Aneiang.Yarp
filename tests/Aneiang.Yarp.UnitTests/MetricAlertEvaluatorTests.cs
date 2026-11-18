using Aneiang.Yarp.Dashboard.Infrastructure.Observability;
using Aneiang.Yarp.Dashboard.Modules.Alerting.Models;
using Aneiang.Yarp.Dashboard.Modules.Alerting.Services;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// The arithmetic behind an alert. Kept separate from the state machine on purpose: "is this number
/// bad" and "has it been bad long enough to wake someone up" fail for different reasons, and this
/// half can be pinned down without a clock.
/// </summary>
public class MetricAlertEvaluatorTests
{
    [Theory]
    [InlineData(0.02, AlertSignalKind.Healthy)]
    [InlineData(0.04, AlertSignalKind.Healthy)]
    // 5% is the line, not the first failing value: a rule drawn at 5% means 5% is acceptable.
    [InlineData(0.05, AlertSignalKind.Healthy)]
    [InlineData(0.0501, AlertSignalKind.Breaching)]
    [InlineData(0.5, AlertSignalKind.Breaching)]
    public void Error_rate_compares_strictly_against_the_threshold(double rate, AlertSignalKind expected)
    {
        var rule = Rule(MetricAlertKind.ErrorRate, threshold: 0.05, minSamples: 10);

        var signal = MetricAlertEvaluator.Evaluate(rule, new AlertSample(rate, 200, 60));

        Assert.Equal(expected, signal.Kind);
    }

    [Theory]
    [InlineData(0, AlertSignalKind.Healthy)]
    // Counts, not ratios: a threshold of 1 means "one open breaker is the alert", so it must be
    // reachable — with a strict comparison that setting could never fire.
    [InlineData(1, AlertSignalKind.Breaching)]
    [InlineData(3, AlertSignalKind.Breaching)]
    public void Circuit_count_breaches_at_or_above_the_threshold(double open, AlertSignalKind expected)
    {
        var rule = Rule(MetricAlertKind.CircuitOpen, threshold: 1, minSamples: 1);

        var signal = MetricAlertEvaluator.Evaluate(rule, new AlertSample(open, 4, 0));

        Assert.Equal(expected, signal.Kind);
    }

    [Fact]
    public void Latency_breaches_above_the_threshold_in_seconds()
    {
        var rule = Rule(MetricAlertKind.LatencyP99, threshold: 1.0, minSamples: 20);

        Assert.Equal(AlertSignalKind.Healthy,
            MetricAlertEvaluator.Evaluate(rule, new AlertSample(0.98, 500, 60)).Kind);
        Assert.Equal(AlertSignalKind.Breaching,
            MetricAlertEvaluator.Evaluate(rule, new AlertSample(1.01, 500, 60)).Kind);
    }

    /// <summary>
    /// Insufficient data is its own verdict, not "healthy". If it collapsed into healthy the state
    /// machine would resolve a firing incident the moment traffic stopped, which is exactly the
    /// moment the gateway is most likely to be down.
    /// </summary>
    [Fact]
    public void Too_few_samples_is_undecided_rather_than_healthy()
    {
        var rule = Rule(MetricAlertKind.ErrorRate, threshold: 0.05, minSamples: 50);

        // One failed request out of one: a 100% error rate that must not page anyone.
        var signal = MetricAlertEvaluator.Evaluate(rule, new AlertSample(1.0, 1, 60));

        Assert.Equal(AlertSignalKind.InsufficientData, signal.Kind);
        Assert.Equal("lowSamples", signal.Code);
        Assert.Contains("50 required", signal.Reason);
    }

    [Fact]
    public void An_unfilled_window_is_undecided_even_with_plenty_of_samples()
    {
        var rule = Rule(MetricAlertKind.ErrorRate, threshold: 0.05, minSamples: 10);

        var signal = MetricAlertEvaluator.Evaluate(rule, new AlertSample(0.9, 5_000, 15, WindowReady: false));

        Assert.Equal(AlertSignalKind.InsufficientData, signal.Kind);
        Assert.Equal("windowNotReady", signal.Code);
    }

    [Fact]
    public void Reasons_quote_the_measurement_and_the_threshold()
    {
        var rule = Rule(MetricAlertKind.ErrorRate, threshold: 0.05, minSamples: 10);

        var signal = MetricAlertEvaluator.Evaluate(rule, new AlertSample(0.124, 143, 62));

        Assert.Equal("breaching", signal.Code);
        Assert.Contains("12.4%", signal.Reason);
        Assert.Contains("5%", signal.Reason);
        Assert.Contains("143 samples", signal.Reason);
    }

    [Theory]
    [InlineData(MetricAlertKind.ErrorRate, 0.124, "12.4%")]
    [InlineData(MetricAlertKind.ErrorRate, 0.05, "5%")]
    // Sub-second latency reads better in milliseconds: "0.32s" makes the reader do arithmetic.
    [InlineData(MetricAlertKind.LatencyP99, 0.32, "320ms")]
    [InlineData(MetricAlertKind.LatencyP99, 2.5, "2.5s")]
    [InlineData(MetricAlertKind.CircuitOpen, 3, "3")]
    public void Values_are_formatted_in_their_own_unit(MetricAlertKind kind, double value, string expected)
        => Assert.Equal(expected, MetricAlertEvaluator.Format(kind, value));

    private static MetricAlertRule Rule(MetricAlertKind kind, double threshold, int minSamples) => new()
    {
        Id = "r1",
        Name = "test",
        Kind = kind,
        ScopeKind = AlertScopeKind.Global,
        Threshold = threshold,
        WindowSeconds = 60,
        ForSeconds = 0,
        MinSamples = minSamples
    };
}

/// <summary>
/// Rules that would never fire are the dangerous kind of wrong: every layer accepts them and the
/// silence looks like health. These tests hold the door shut.
/// </summary>
public class MetricAlertRuleValidatorTests
{
    [Fact]
    public void A_reasonable_rule_passes()
    {
        Assert.Empty(MetricAlertRuleValidator.Validate(Valid()));
    }

    [Fact]
    public void An_error_rate_threshold_typed_as_a_percentage_is_rejected()
    {
        // The mistake everyone makes once: 5 in a field that means "ratio".
        var rule = Valid() with { Threshold = 5 };

        var errors = MetricAlertRuleValidator.Validate(rule);

        Assert.Contains(errors, e => e.Contains("ratio"));
    }

    [Fact]
    public void A_latency_rule_needs_enough_samples_for_a_p99_to_mean_anything()
    {
        var rule = Valid() with
        {
            Kind = MetricAlertKind.LatencyP99,
            Threshold = 1,
            MinSamples = 5
        };

        var errors = MetricAlertRuleValidator.Validate(rule);

        Assert.Contains(errors, e => e.Contains("at least 20 samples"));
    }

    [Fact]
    public void A_circuit_rule_cannot_be_scoped_to_a_route()
    {
        // There is no per-route circuit breaker, so this would match nothing and sit at
        // "insufficient data" forever while looking configured.
        var rule = Valid() with
        {
            Kind = MetricAlertKind.CircuitOpen,
            Threshold = 1,
            ScopeKind = AlertScopeKind.Route,
            Scope = "orders"
        };

        var errors = MetricAlertRuleValidator.Validate(rule);

        Assert.Contains(errors, e => e.Contains("not a route"));
    }

    [Fact]
    public void A_global_rule_may_not_carry_a_scope()
    {
        var rule = Valid() with { ScopeKind = AlertScopeKind.Global, Scope = "api" };

        Assert.Contains(MetricAlertRuleValidator.Validate(rule), e => e.Contains("must not carry a scope"));
    }

    [Fact]
    public void A_scoped_rule_requires_a_scope()
    {
        var rule = Valid() with { ScopeKind = AlertScopeKind.Cluster, Scope = null };

        Assert.Contains(MetricAlertRuleValidator.Validate(rule), e => e.Contains("scope is required"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(3601)]
    public void The_window_is_bounded(int windowSeconds)
    {
        var rule = Valid() with { WindowSeconds = windowSeconds };

        Assert.Contains(MetricAlertRuleValidator.Validate(rule), e => e.Contains("window"));
    }

    [Fact]
    public void A_missing_name_is_reported()
    {
        Assert.Contains(MetricAlertRuleValidator.Validate(Valid() with { Name = " " }), e => e.Contains("name"));
    }

    private static MetricAlertRule Valid() => new()
    {
        Id = "r1",
        Name = "API 5xx",
        Kind = MetricAlertKind.ErrorRate,
        ScopeKind = AlertScopeKind.Global,
        Threshold = 0.05,
        WindowSeconds = 60,
        ForSeconds = 60,
        MinSamples = 20,
        Severity = AlertSeverity.Warning
    };
}

/// <summary>
/// The histogram-to-quantile step. A rule and a Grafana panel must agree on the p99, which means one
/// implementation of the interpolation and the same bucket bounds.
/// </summary>
public class GatewayMetricsPercentileTests
{
    [Fact]
    public void No_observations_yields_zero()
        => Assert.Equal(0, GatewayMetricsCollector.PercentileFromBuckets(new long[13], 0, 0.99));

    [Fact]
    public void A_uniform_50ms_population_lands_in_the_50ms_bucket()
    {
        var collector = new GatewayMetricsCollector();
        for (var i = 0; i < 100; i++)
            collector.OnRequestEnd("r", "c", 200, 0.05);

        var series = Assert.Single(collector.GetRouteCounters());
        var p99 = GatewayMetricsCollector.PercentileFromBuckets(series.BucketsCumulative, series.Count, 0.99);

        // Buckets bracket 0.025 and 0.05, so the estimate must sit inside that interval rather than
        // in a neighbouring one.
        Assert.InRange(p99, 0.025, 0.05);
    }

    [Fact]
    public void Route_counters_carry_the_5xx_count_and_buckets()
    {
        var collector = new GatewayMetricsCollector();
        collector.OnRequestEnd("orders", "c1", 200, 0.01);
        collector.OnRequestEnd("orders", "c1", 502, 0.01);
        collector.OnRequestEnd("orders", "c1", 500, 0.02);

        var series = Assert.Single(collector.GetRouteCounters());

        Assert.Equal("orders", series.Route);
        Assert.Equal(3, series.Requests);
        Assert.Equal(2, series.Failures);
        Assert.Equal(3, series.Count);
    }

    [Fact]
    public void The_prometheus_output_exposes_the_same_failure_count()
    {
        var collector = new GatewayMetricsCollector();
        collector.OnRequestEnd("orders", "c1", 502, 0.01);

        var text = new System.Text.StringBuilder();
        collector.WriteRequestMetrics(text);

        // The number the alert quotes has to be visible to the operator, or the alert is unfalsifiable.
        Assert.Contains("gateway_route_failures_total{route=\"orders\",cluster=\"c1\"} 1", text.ToString());
    }
}
