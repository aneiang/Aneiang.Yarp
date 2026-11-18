using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Aneiang.Yarp.Dashboard.Infrastructure.Observability;

/// <summary>Immutable copy of one destination's counters at a point in time.</summary>
/// <param name="Cluster">Cluster the destination belongs to.</param>
/// <param name="Destination">Destination identifier as configured.</param>
/// <param name="Requests">Requests served by this destination since process start.</param>
/// <param name="Failures">Of those, how many ended in 5xx since process start.</param>
public readonly record struct DestinationCounterSnapshot(
    string Cluster,
    string Destination,
    long Requests,
    long Failures);

/// <summary>Immutable copy of one (route, cluster) series at a point in time.</summary>
/// <param name="Route">Route identifier as configured.</param>
/// <param name="Cluster">Cluster identifier as configured.</param>
/// <param name="Requests">Requests on this series since process start.</param>
/// <param name="Failures">Of those, how many ended in 5xx since process start.</param>
/// <param name="BucketsCumulative">
/// Histogram buckets, already accumulated (<c>[i]</c> counts every request at or below
/// <see cref="GatewayMetricsCollector.BucketBounds"/>[i]). Cumulative because quantile math needs
/// it that way; differencing two snapshots keeps it cumulative.
/// </param>
/// <param name="Count">Total observations, equal to the last bucket plus everything above it.</param>
public readonly record struct RouteCounterSnapshot(
    string Route,
    string Cluster,
    long Requests,
    long Failures,
    long[] BucketsCumulative,
    long Count);

/// <summary>
/// Single source of truth for the Prometheus <c>/metrics</c> endpoint.
///
/// Why not reuse <c>LockFreeStatistics</c>: that accumulator is only written by the
/// metrics / proxy-log plugins, so it stays empty whenever neither plugin is bound.
/// The dashboard panels can live with that, but an external monitoring system cannot —
/// it would see a flat zero line and conclude the gateway is dead.
///
/// Recording therefore happens in the reverse-proxy pipeline itself (see
/// <see cref="GatewayMetricsMiddleware"/>), which runs for every proxied request
/// regardless of which plugins are active.
///
/// Cardinality is bounded on purpose: a misconfigured route template can otherwise
/// produce one series per URL and take down Prometheus. Anything beyond
/// <see cref="MaxTrackedSeries"/> collapses into a single "overflow" series.
/// </summary>
public sealed class GatewayMetricsCollector
{
    /// <summary>Upper bound on (route, cluster) combinations exposed as labels.</summary>
    public const int MaxTrackedSeries = 300;

    /// <summary>Upper bound on (cluster, destination) combinations exposed as labels.</summary>
    public const int MaxTrackedDestinationSeries = 500;

    /// <summary>
    /// Histogram upper bounds in seconds. Chosen to bracket a typical API call.
    /// </summary>
    /// <remarks>
    /// Public because anything that has to turn these buckets back into a latency (the alerting
    /// rules ask for a p99) must use the same bounds. Two copies of this array would silently
    /// drift apart and produce a p99 that disagrees with Grafana.
    /// </remarks>
    public static readonly double[] BucketBounds =
    {
        0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10
    };

    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly ConcurrentDictionary<string, RouteSeries> _series = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DestinationSeries> _destinationSeries = new(StringComparer.Ordinal);
    private long _destinationOverflow;

    private long _requests;
    private long _inFlight;
    private long _overflowSeriesCreated;
    private double _firstSuccessSeconds = -1; // -1 = no successful proxied request yet
    private readonly long[] _statusClasses = new long[6]; // 1xx..5xx, index 5 = unmapped

    /// <summary>Called when a proxied request starts.</summary>
    public void OnRequestStart() => Interlocked.Increment(ref _inFlight);

    /// <summary>Called when a proxied request finishes, whatever the outcome.</summary>
    public void OnRequestEnd(string? routeId, string? clusterId, int statusCode, double elapsedSeconds)
    {
        Interlocked.Decrement(ref _inFlight);
        Interlocked.Increment(ref _requests);

        var classIndex = statusCode switch
        {
            >= 100 and < 200 => 0,
            >= 200 and < 300 => 1,
            >= 300 and < 400 => 2,
            >= 400 and < 500 => 3,
            >= 500 and < 600 => 4,
            _ => 5
        };
        Interlocked.Increment(ref _statusClasses[classIndex]);

        var key = BuildKey(routeId, clusterId);
        if (!_series.TryGetValue(key, out var series))
        {
            // Refuse to grow without bound; new labels land in the overflow bucket.
            if (_series.Count >= MaxTrackedSeries)
            {
                Interlocked.Increment(ref _overflowSeriesCreated);
                key = OverflowKey;
                series = _series.GetOrAdd(key, _ => new RouteSeries(OverflowRoute, OverflowCluster));
            }
            else
            {
                series = _series.GetOrAdd(key, _ => new RouteSeries(Normalize(routeId), Normalize(clusterId)));
            }
        }

        series.Record(elapsedSeconds, failed: statusCode >= 500);

        // TTFR — time from start to the first request this instance actually served. The
        // product's north-star metric is "install → first proxied request", and it was
        // immeasurable before this: the console said "started" while nothing was routed.
        if (statusCode is >= 200 and < 400 && Interlocked.CompareExchange(ref _firstSuccessSeconds, -2, -1) == -1)
        {
            Interlocked.Exchange(ref _firstSuccessSeconds, (DateTimeOffset.UtcNow - _startedAt).TotalSeconds);
        }
    }

    /// <summary>
    /// Records the destination that actually served the request, when one was chosen.
    /// </summary>
    /// <remarks>
    /// Route/cluster counters answer "is the gateway healthy". During a canary the question is
    /// narrower: "is the new node getting the share I asked for, and is it failing more than the
    /// old one". That is per destination, which is why this exists as a second dimension rather
    /// than another label on the route series.
    /// </remarks>
    public void OnDestinationEnd(string? clusterId, string? destinationId, int statusCode)
    {
        if (string.IsNullOrWhiteSpace(destinationId))
            return;

        var key = Normalize(clusterId) + "\u0001" + destinationId;
        if (!_destinationSeries.TryGetValue(key, out var series))
        {
            if (_destinationSeries.Count >= MaxTrackedDestinationSeries)
            {
                Interlocked.Increment(ref _destinationOverflow);
                return;
            }

            series = _destinationSeries.GetOrAdd(
                key,
                _ => new DestinationSeries(Normalize(clusterId), destinationId!));
        }

        series.Record(failed: statusCode >= 500);
    }

    /// <summary>
    /// A point-in-time copy of every per-destination counter.
    /// </summary>
    /// <remarks>
    /// The Prometheus text output is enough for a dashboard, but a canary gate needs arithmetic:
    /// "how many 5xx did this node serve in the last 60 seconds". These counters are cumulative
    /// for the lifetime of the process, so a caller that wants a window has to take a copy when the
    /// window opens and subtract it when the window closes — which is exactly what
    /// <c>CanaryRolloutService</c> does. Returning copies (rather than the dictionaries themselves)
    /// keeps the caller from observing a half-updated series.
    /// </remarks>
    public IReadOnlyList<DestinationCounterSnapshot> GetDestinationCounters()
    {
        if (_destinationSeries.IsEmpty)
            return Array.Empty<DestinationCounterSnapshot>();

        var result = new List<DestinationCounterSnapshot>(_destinationSeries.Count);
        foreach (var pair in _destinationSeries)
        {
            result.Add(new DestinationCounterSnapshot(
                pair.Value.Cluster,
                pair.Value.Destination,
                Interlocked.Read(ref pair.Value.Requests),
                Interlocked.Read(ref pair.Value.Failures)));
        }

        return result;
    }

    /// <summary>
    /// A point-in-time copy of every per-route series: request volume, 5xx count and the latency
    /// histogram, all cumulative since process start.
    /// </summary>
    /// <remarks>
    /// Same arithmetic contract as <see cref="GetDestinationCounters"/>: the alerting rules need a
    /// window ("5xx rate over the last 60 s"), these counters are lifetime totals, so the caller
    /// snapshots when the window opens and subtracts when it closes.
    /// </remarks>
    public IReadOnlyList<RouteCounterSnapshot> GetRouteCounters()
    {
        if (_series.IsEmpty)
            return Array.Empty<RouteCounterSnapshot>();

        var result = new List<RouteCounterSnapshot>(_series.Count);
        foreach (var pair in _series)
        {
            var series = pair.Value;
            result.Add(new RouteCounterSnapshot(
                series.Route,
                series.Cluster,
                Interlocked.Read(ref series.Count),
                Interlocked.Read(ref series.Failures),
                series.SnapshotCumulativeBuckets(),
                Interlocked.Read(ref series.Count)));
        }

        return result;
    }

    /// <summary>
    /// Estimates a quantile from cumulative histogram buckets, using the same linear interpolation
    /// as Prometheus' <c>histogram_quantile</c> so a rule and a Grafana panel agree on the number.
    /// </summary>
    /// <param name="cumulative">Bucket counts where <c>[i]</c> includes everything at or below bound i.</param>
    /// <param name="total">Observations across all buckets.</param>
    /// <param name="quantile">0..1, e.g. 0.99 for p99.</param>
    /// <returns>The estimate in seconds; the last finite bound when the tail is open-ended.</returns>
    public static double PercentileFromBuckets(IReadOnlyList<long> cumulative, long total, double quantile)
    {
        if (total <= 0 || cumulative.Count == 0)
            return 0;

        var rank = Math.Clamp(quantile, 0, 1) * total;
        long previous = 0;

        for (var i = 0; i < cumulative.Count; i++)
        {
            if (cumulative[i] < rank)
            {
                previous = cumulative[i];
                continue;
            }

            var lower = i == 0 ? 0 : BucketBounds[i - 1];
            var upper = BucketBounds[i];
            var inBucket = cumulative[i] - previous;
            if (inBucket <= 0)
                return upper;

            return lower + (upper - lower) * ((rank - previous) / inBucket);
        }

        // Everything landed above the last finite bound; report the bound rather than infinity.
        return BucketBounds[^1];
    }

    /// <summary>Seconds from collector start to the first successful proxied request, or null.</summary>
    public double? FirstSuccessSeconds
    {
        get
        {
            var value = Interlocked.CompareExchange(ref _firstSuccessSeconds, 0, 0);
            return value < 0 ? null : value;
        }
    }

    /// <summary>Number of tracked (route, cluster) series, for diagnostics.</summary>
    public int TrackedSeriesCount => _series.Count;

    /// <summary>
    /// Renders the Prometheus text exposition (version 0.0.4) for the request metrics.
    /// Extra gauges that need DI (config version, circuit counts…) are appended by
    /// <see cref="GatewayMetricsRenderer"/> so this class stays dependency-free.
    /// </summary>
    public void WriteRequestMetrics(StringBuilder sb)
    {
        var requests = Interlocked.Read(ref _requests);

        sb.AppendLine("# HELP gateway_requests_total Proxied requests handled by this instance.");
        sb.AppendLine("# TYPE gateway_requests_total counter");
        foreach (var pair in _series)
        {
            sb.Append("gateway_requests_total{route=\"")
              .Append(Escape(pair.Value.Route))
              .Append("\",cluster=\"")
              .Append(Escape(pair.Value.Cluster))
              .Append("\"} ")
              .Append(pair.Value.Count.ToString(CultureInfo.InvariantCulture))
              .AppendLine();
        }
        if (_series.IsEmpty)
        {
            sb.AppendLine("gateway_requests_total{route=\"\",cluster=\"\"} 0");
        }

        // Per-route 5xx: the number an error-rate alert rule actually fires on. Exposed so the
        // operator can see the same figure the alert quoted, instead of being told "12% 5xx" and
        // finding nothing in Grafana that agrees.
        sb.AppendLine("# HELP gateway_route_failures_total 5xx responses per route/cluster.");
        sb.AppendLine("# TYPE gateway_route_failures_total counter");
        foreach (var pair in _series)
        {
            sb.Append("gateway_route_failures_total{route=\"")
              .Append(Escape(pair.Value.Route))
              .Append("\",cluster=\"")
              .Append(Escape(pair.Value.Cluster))
              .Append("\"} ")
              .Append(Interlocked.Read(ref pair.Value.Failures).ToString(CultureInfo.InvariantCulture))
              .AppendLine();
        }
        if (_series.IsEmpty)
        {
            sb.AppendLine("gateway_route_failures_total{route=\"\",cluster=\"\"} 0");
        }

        sb.AppendLine("# HELP gateway_request_duration_seconds Time spent in the proxy pipeline.");
        sb.AppendLine("# TYPE gateway_request_duration_seconds histogram");
        foreach (var pair in _series)
        {
            var series = pair.Value;
            var route = Escape(series.Route);
            var cluster = Escape(series.Cluster);
            var cumulative = 0L;
            long[] snapshot;
            long count;
            long sumMicros;
            lock (series.Gate)
            {
                snapshot = (long[])series.Buckets.Clone();
                count = series.Count;
                sumMicros = series.SumMicros;
            }

            for (var i = 0; i < BucketBounds.Length; i++)
            {
                cumulative += snapshot[i];
                sb.Append("gateway_request_duration_seconds_bucket{route=\"")
                  .Append(route)
                  .Append("\",cluster=\"")
                  .Append(cluster)
                  .Append("\",le=\"")
                  .Append(BucketBounds[i].ToString("0.####", CultureInfo.InvariantCulture))
                  .Append("\"} ")
                  .Append(cumulative.ToString(CultureInfo.InvariantCulture))
                  .AppendLine();
            }

            sb.Append("gateway_request_duration_seconds_bucket{route=\"")
              .Append(route)
              .Append("\",cluster=\"")
              .Append(cluster)
              .Append("\",le=\"+Inf\"} ")
              .Append(count.ToString(CultureInfo.InvariantCulture))
              .AppendLine();
            sb.Append("gateway_request_duration_seconds_sum{route=\"")
              .Append(route)
              .Append("\",cluster=\"")
              .Append(cluster)
              .Append("\"} ")
              .Append((sumMicros / 1_000_000d).ToString("0.######", CultureInfo.InvariantCulture))
              .AppendLine();
            sb.Append("gateway_request_duration_seconds_count{route=\"")
              .Append(route)
              .Append("\",cluster=\"")
              .Append(cluster)
              .Append("\"} ")
              .Append(count.ToString(CultureInfo.InvariantCulture))
              .AppendLine();
        }

        sb.AppendLine("# HELP gateway_responses_total Responses grouped by status class.");
        sb.AppendLine("# TYPE gateway_responses_total counter");
        var names = new[] { "1xx", "2xx", "3xx", "4xx", "5xx", "other" };
        for (var i = 0; i < names.Length; i++)
        {
            sb.Append("gateway_responses_total{class=\"")
              .Append(names[i])
              .Append("\"} ")
              .Append(Interlocked.Read(ref _statusClasses[i]).ToString(CultureInfo.InvariantCulture))
              .AppendLine();
        }

        sb.AppendLine("# HELP gateway_inflight_requests Requests currently being proxied.");
        sb.AppendLine("# TYPE gateway_inflight_requests gauge");
        sb.Append("gateway_inflight_requests ")
          .Append(Interlocked.Read(ref _inFlight).ToString(CultureInfo.InvariantCulture))
          .AppendLine();

        sb.AppendLine("# HELP gateway_uptime_seconds Seconds since this instance started.");
        sb.AppendLine("# TYPE gateway_uptime_seconds gauge");
        sb.Append("gateway_uptime_seconds ")
          .Append(((long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds).ToString(CultureInfo.InvariantCulture))
          .AppendLine();

        // TTFR: the gap between "the process is up" and "it actually routes something".
        // Every other metric here can look healthy while this one is still unsatisfied.
        var firstSuccess = FirstSuccessSeconds;
        sb.AppendLine("# HELP gateway_serving 1 once this instance has served at least one successful proxied request.");
        sb.AppendLine("# TYPE gateway_serving gauge");
        sb.Append("gateway_serving ").Append(firstSuccess.HasValue ? "1" : "0").AppendLine();

        if (firstSuccess.HasValue)
        {
            sb.AppendLine("# HELP gateway_first_success_seconds Seconds from start to the first successful proxied request (TTFR).");
            sb.AppendLine("# TYPE gateway_first_success_seconds gauge");
            sb.Append("gateway_first_success_seconds ")
              .Append(firstSuccess.Value.ToString("0.###", CultureInfo.InvariantCulture))
              .AppendLine();
        }

        if (Interlocked.Read(ref _overflowSeriesCreated) > 0)
        {
            sb.AppendLine("# HELP gateway_metrics_dropped_series Route/cluster pairs collapsed into \"other\" due to the cardinality cap.");
            sb.AppendLine("# TYPE gateway_metrics_dropped_series gauge");
            sb.Append("gateway_metrics_dropped_series ")
              .Append(Interlocked.Read(ref _overflowSeriesCreated).ToString(CultureInfo.InvariantCulture))
              .AppendLine();
        }
    }

    /// <summary>
    /// Renders the per-destination counters that make a canary readable: volume per node plus the
    /// 5xx count that decides whether the rollout continues.
    /// </summary>
    public void WriteDestinationMetrics(StringBuilder sb)
    {
        if (_destinationSeries.IsEmpty)
            return;

        sb.AppendLine("# HELP gateway_destination_requests_total Requests served by each destination.");
        sb.AppendLine("# TYPE gateway_destination_requests_total counter");
        foreach (var pair in _destinationSeries)
        {
            sb.Append("gateway_destination_requests_total{cluster=\"")
              .Append(Escape(pair.Value.Cluster))
              .Append("\",destination=\"")
              .Append(Escape(pair.Value.Destination))
              .Append("\"} ")
              .Append(Interlocked.Read(ref pair.Value.Requests).ToString(CultureInfo.InvariantCulture))
              .AppendLine();
        }

        sb.AppendLine("# HELP gateway_destination_failures_total 5xx responses served by each destination.");
        sb.AppendLine("# TYPE gateway_destination_failures_total counter");
        foreach (var pair in _destinationSeries)
        {
            sb.Append("gateway_destination_failures_total{cluster=\"")
              .Append(Escape(pair.Value.Cluster))
              .Append("\",destination=\"")
              .Append(Escape(pair.Value.Destination))
              .Append("\"} ")
              .Append(Interlocked.Read(ref pair.Value.Failures).ToString(CultureInfo.InvariantCulture))
              .AppendLine();
        }

        if (Interlocked.Read(ref _destinationOverflow) > 0)
        {
            sb.AppendLine("# HELP gateway_metrics_dropped_destination_series Destinations dropped by the cardinality cap.");
            sb.AppendLine("# TYPE gateway_metrics_dropped_destination_series gauge");
            sb.Append("gateway_metrics_dropped_destination_series ")
              .Append(Interlocked.Read(ref _destinationOverflow).ToString(CultureInfo.InvariantCulture))
              .AppendLine();
        }
    }

    private const string OverflowKey = "\u0000overflow";
    private const string OverflowRoute = "other";
    private const string OverflowCluster = "other";

    private static string BuildKey(string? routeId, string? clusterId)
        => Normalize(routeId) + "\u0001" + Normalize(clusterId);

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? "unmatched" : value!;

    /// <summary>Prometheus label values need backslash, quote and newline escaped.</summary>
    public static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    private sealed class DestinationSeries
    {
        public DestinationSeries(string cluster, string destination)
        {
            Cluster = cluster;
            Destination = destination;
        }

        public string Cluster { get; }
        public string Destination { get; }
        public long Requests;
        public long Failures;

        public void Record(bool failed)
        {
            Interlocked.Increment(ref Requests);
            if (failed)
                Interlocked.Increment(ref Failures);
        }
    }

    private sealed class RouteSeries
    {
        public RouteSeries(string route, string cluster)
        {
            Route = route;
            Cluster = cluster;
        }

        public string Route { get; }
        public string Cluster { get; }
        public object Gate { get; } = new();
        public long Count;
        public long Failures;
        public long SumMicros;
        public readonly long[] Buckets = new long[BucketBounds.Length];

        public void Record(double elapsedSeconds, bool failed)
        {
            // Negative elapsed (clock skew) would break the histogram; clamp it.
            if (elapsedSeconds < 0) elapsedSeconds = 0;
            var micros = (long)(elapsedSeconds * 1_000_000d);

            lock (Gate)
            {
                Count++;
                SumMicros += micros;
                if (failed)
                    Failures++;

                for (var i = 0; i < BucketBounds.Length; i++)
                {
                    if (elapsedSeconds <= BucketBounds[i])
                    {
                        Buckets[i]++;
                        break;
                    }
                }
            }
        }

        /// <summary>Accumulated bucket copy; <c>[i]</c> counts every observation at or below bound i.</summary>
        public long[] SnapshotCumulativeBuckets()
        {
            var cumulative = new long[BucketBounds.Length];
            lock (Gate)
            {
                long running = 0;
                for (var i = 0; i < BucketBounds.Length; i++)
                {
                    running += Buckets[i];
                    cumulative[i] = running;
                }
            }

            return cumulative;
        }
    }
}
