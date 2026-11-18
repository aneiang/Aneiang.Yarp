using System.Text;
using Aneiang.Yarp.Dashboard.Infrastructure.Observability;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// The /metrics endpoint is consumed by Prometheus, so a wrong shape is not a cosmetic
/// bug — the scrape either fails to parse or silently reports zeros. These cases pin the
/// exposition format, the histogram accumulation and the cardinality cap.
/// </summary>
public class GatewayMetricsCollectorTests
{
    private static string Render(GatewayMetricsCollector collector)
    {
        var sb = new StringBuilder();
        collector.WriteRequestMetrics(sb);
        return sb.ToString();
    }

    private static string RenderDestinations(GatewayMetricsCollector collector)
    {
        var sb = new StringBuilder();
        collector.WriteDestinationMetrics(sb);
        return sb.ToString();
    }

    [Fact]
    public void EmptyCollector_StillExposesAZeroSeries()
    {
        // A counter that disappears when idle makes Grafana draw "no data" instead of 0.
        var text = Render(new GatewayMetricsCollector());

        Assert.Contains("gateway_requests_total{route=\"\",cluster=\"\"} 0", text);
        Assert.Contains("# TYPE gateway_request_duration_seconds histogram", text);
    }

    [Fact]
    public void Request_ProducesRouteAndClusterLabels()
    {
        var collector = new GatewayMetricsCollector();
        collector.OnRequestEnd("r1", "c1", 200, 0.05);

        var text = Render(collector);

        Assert.Contains("gateway_requests_total{route=\"r1\",cluster=\"c1\"} 1", text);
        Assert.Contains("gateway_request_duration_seconds_count{route=\"r1\",cluster=\"c1\"} 1", text);
    }

    [Fact]
    public void Requests_WithoutRouteOrCluster_FallIntoUnmatched()
    {
        var collector = new GatewayMetricsCollector();
        collector.OnRequestEnd(null, "  ", 404, 0.01);

        Assert.Contains("route=\"unmatched\",cluster=\"unmatched\"", Render(collector));
    }

    [Theory]
    [InlineData(100, "1xx")]
    [InlineData(204, "2xx")]
    [InlineData(301, "3xx")]
    [InlineData(418, "4xx")]
    [InlineData(503, "5xx")]
    [InlineData(99, "other")]
    [InlineData(700, "other")]
    public void StatusCodes_AreBucketedByClass(int status, string expectedClass)
    {
        var collector = new GatewayMetricsCollector();
        collector.OnRequestEnd("r1", "c1", status, 0.01);

        Assert.Contains($"gateway_responses_total{{class=\"{expectedClass}\"}} 1", Render(collector));
    }

    [Fact]
    public void Histogram_BucketsAreCumulative()
    {
        var collector = new GatewayMetricsCollector();
        collector.OnRequestEnd("r1", "c1", 200, 0.0005); // <= 1ms
        collector.OnRequestEnd("r1", "c1", 200, 0.02);   // <= 25ms
        collector.OnRequestEnd("r1", "c1", 200, 0.4);    // <= 500ms

        var text = Render(collector);

        // 1ms bucket holds only the first call, but every later bucket must include it.
        Assert.Contains("le=\"0.001\"} 1", text);
        Assert.Contains("le=\"0.025\"} 2", text);
        Assert.Contains("le=\"0.5\"} 3", text);
        Assert.Contains("le=\"+Inf\"} 3", text);
    }

    [Fact]
    public void Histogram_SumIsInSeconds()
    {
        var collector = new GatewayMetricsCollector();
        collector.OnRequestEnd("r1", "c1", 200, 0.25);
        collector.OnRequestEnd("r1", "c1", 200, 0.25);

        Assert.Contains("gateway_request_duration_seconds_sum{route=\"r1\",cluster=\"c1\"} 0.5", Render(collector));
    }

    [Fact]
    public void NegativeElapsed_IsClampedAndDoesNotCorruptTheHistogram()
    {
        // Clock skew between stopwatch reads must not produce a negative sum.
        var collector = new GatewayMetricsCollector();
        collector.OnRequestEnd("r1", "c1", 200, -0.5);

        var text = Render(collector);

        Assert.Contains("gateway_request_duration_seconds_sum{route=\"r1\",cluster=\"c1\"} 0", text);
        Assert.Contains("le=\"0.001\"} 1", text);
    }

    [Fact]
    public void InFlight_TracksStartAndEnd()
    {
        var collector = new GatewayMetricsCollector();
        collector.OnRequestStart();
        collector.OnRequestStart();

        Assert.Contains("gateway_inflight_requests 2", Render(collector));

        collector.OnRequestEnd("r1", "c1", 200, 0.01);
        Assert.Contains("gateway_inflight_requests 1", Render(collector));
    }

    [Fact]
    public void Labels_AreEscaped()
    {
        var collector = new GatewayMetricsCollector();
        collector.OnRequestEnd("a\"b", "c\\d", 200, 0.01);

        var text = Render(collector);

        Assert.Contains("route=\"a\\\"b\"", text);
        Assert.Contains("cluster=\"c\\\\d\"", text);
    }

    [Fact]
    public void NewlineInLabel_IsEscaped()
    {
        // An unescaped newline would break the line-oriented exposition format and the
        // whole scrape would fail.
        Assert.Equal("a\\nb", GatewayMetricsCollector.Escape("a\nb"));
    }

    [Fact]
    public void Cardinality_IsCappedAndOverflowIsCounted()
    {
        var collector = new GatewayMetricsCollector();

        for (var i = 0; i < GatewayMetricsCollector.MaxTrackedSeries + 5; i++)
            collector.OnRequestEnd($"r{i}", "c1", 200, 0.01);

        // The cap counts real (route, cluster) pairs; the shared overflow series is one extra.
        Assert.True(
            collector.TrackedSeriesCount <= GatewayMetricsCollector.MaxTrackedSeries + 1,
            $"tracked {collector.TrackedSeriesCount} series, cap is {GatewayMetricsCollector.MaxTrackedSeries}");

        var text = Render(collector);
        Assert.Contains("gateway_metrics_dropped_series 5", text);
        Assert.Contains("route=\"other\",cluster=\"other\"", text);
    }

    [Fact]
    public void Cardinality_IsNotReportedWhenNothingWasDropped()
    {
        var collector = new GatewayMetricsCollector();
        collector.OnRequestEnd("r1", "c1", 200, 0.01);

        Assert.DoesNotContain("gateway_metrics_dropped_series", Render(collector));
    }

    [Fact]
    public void HelpAndTypeLines_AreEmittedForEveryMetric()
    {
        var text = Render(new GatewayMetricsCollector());

        foreach (var name in new[]
                 {
                     "gateway_requests_total",
                     "gateway_request_duration_seconds",
                     "gateway_responses_total",
                     "gateway_inflight_requests",
                     "gateway_uptime_seconds"
                 })
        {
            Assert.Contains($"# HELP {name} ", text);
            Assert.Contains($"# TYPE {name} ", text);
        }
    }

    [Fact]
    public void Uptime_IsANonNegativeGauge()
    {
        var text = Render(new GatewayMetricsCollector());
        var line = text.Split('\n').First(l => l.StartsWith("gateway_uptime_seconds ", StringComparison.Ordinal));

        Assert.True(long.Parse(line.Split(' ')[1].Trim()) >= 0);
    }

    // ---------- TTFR ----------

    [Fact]
    public void BeforeAnyTraffic_ServingIsZero()
    {
        var collector = new GatewayMetricsCollector();

        Assert.Null(collector.FirstSuccessSeconds);
        Assert.Contains("gateway_serving 0", Render(collector));
        Assert.DoesNotContain("gateway_first_success_seconds ", Render(collector));
    }

    [Fact]
    public void FirstSuccessfulRequest_FlipsServingAndStampsTtfr()
    {
        // TTFR is "process up" → "actually routes something". Without it, every other
        // metric looks fine on a gateway that has never forwarded a single request.
        var collector = new GatewayMetricsCollector();

        collector.OnRequestStart();
        collector.OnRequestEnd("r1", "c1", 200, 0.01);

        Assert.NotNull(collector.FirstSuccessSeconds);
        var text = Render(collector);
        Assert.Contains("gateway_serving 1", text);
        Assert.Contains("gateway_first_success_seconds ", text);
    }

    [Fact]
    public void ClientErrors_DoNotCountAsServed()
    {
        var collector = new GatewayMetricsCollector();

        collector.OnRequestStart();
        collector.OnRequestEnd("r1", "c1", 404, 0.01);

        Assert.Null(collector.FirstSuccessSeconds);
        Assert.Contains("gateway_serving 0", Render(collector));
    }

    [Fact]
    public void Ttfr_IsStampedOnlyOnce()
    {
        var collector = new GatewayMetricsCollector();

        collector.OnRequestStart();
        collector.OnRequestEnd("r1", "c1", 200, 0.01);
        var first = collector.FirstSuccessSeconds;

        Thread.Sleep(5);
        collector.OnRequestStart();
        collector.OnRequestEnd("r1", "c1", 200, 0.01);

        Assert.Equal(first, collector.FirstSuccessSeconds);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(301)]
    [InlineData(304)]
    public void RedirectsAndSuccesses_BothCountAsServed(int status)
    {
        var collector = new GatewayMetricsCollector();

        collector.OnRequestStart();
        collector.OnRequestEnd("r1", "c1", status, 0.01);

        Assert.NotNull(collector.FirstSuccessSeconds);
    }

    // ---------- per-destination counters (canary) ----------

    [Fact]
    public void NoDestinationTraffic_RendersNothing()
    {
        var text = RenderDestinations(new GatewayMetricsCollector());

        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void DestinationTraffic_IsCountedPerNode()
    {
        var collector = new GatewayMetricsCollector();

        for (var i = 0; i < 9; i++)
            collector.OnDestinationEnd("c1", "stable", 200);
        collector.OnDestinationEnd("c1", "canary", 200);

        var text = RenderDestinations(collector);

        Assert.Contains("gateway_destination_requests_total{cluster=\"c1\",destination=\"stable\"} 9", text);
        Assert.Contains("gateway_destination_requests_total{cluster=\"c1\",destination=\"canary\"} 1", text);
    }

    [Fact]
    public void Only5xx_CountsAsDestinationFailure()
    {
        var collector = new GatewayMetricsCollector();

        collector.OnDestinationEnd("c1", "d1", 200);
        collector.OnDestinationEnd("c1", "d1", 404);
        collector.OnDestinationEnd("c1", "d1", 503);

        var text = RenderDestinations(collector);

        Assert.Contains("gateway_destination_failures_total{cluster=\"c1\",destination=\"d1\"} 1", text);
        Assert.Contains("gateway_destination_requests_total{cluster=\"c1\",destination=\"d1\"} 3", text);
    }

    [Fact]
    public void MissingDestination_IsIgnored()
    {
        // A request that never reached a node (no healthy destination, early forwarding error)
        // must not create a phantom "unmatched" destination series.
        var collector = new GatewayMetricsCollector();

        collector.OnDestinationEnd("c1", null, 503);
        collector.OnDestinationEnd("c1", "  ", 503);

        Assert.Equal(string.Empty, RenderDestinations(collector));
    }

    [Fact]
    public void DestinationLabels_AreEscaped()
    {
        var collector = new GatewayMetricsCollector();

        collector.OnDestinationEnd("c\"1", "d\\1", 200);

        var text = RenderDestinations(collector);

        Assert.Contains("cluster=\"c\\\"1\"", text);
        Assert.Contains("destination=\"d\\\\1\"", text);
    }

    [Fact]
    public void DestinationCardinality_IsCapped()
    {
        var collector = new GatewayMetricsCollector();

        for (var i = 0; i < GatewayMetricsCollector.MaxTrackedDestinationSeries + 5; i++)
            collector.OnDestinationEnd("c1", "d" + i, 200);

        var text = RenderDestinations(collector);

        Assert.Contains("gateway_metrics_dropped_destination_series 5", text);
    }
}
