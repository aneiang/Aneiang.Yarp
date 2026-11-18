using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Hosting;

namespace Aneiang.Yarp.Dashboard.Infrastructure.Observability;

/// <summary>
/// Exposes the upstream HTTP connection pool as Prometheus gauges.
///
/// YARP does not surface pool state, but <c>System.Net.Http</c> has published its own
/// metrics since .NET 8, and a <see cref="MeterListener"/> can read them without pulling in
/// an OpenTelemetry exporter. That matters because "the pool is saturated" and "the upstream
/// is slow" look identical from the request side — the first needs more sockets, the second
/// needs a faster backend, and operators were guessing between them.
///
/// Instrument semantics differ by framework version: .NET 8 publishes
/// <c>http.client.open_connections</c> as a synchronous UpDownCounter (measurements are
/// deltas), while later versions publish it as an observable gauge (measurements are absolute).
/// Both are detected through <see cref="Instrument.IsObservable"/> so the level stays correct
/// on either runtime.
/// </summary>
public sealed class UpstreamConnectionMonitor : IHostedService, IDisposable
{
    private const string MeterName = "System.Net.Http";
    private const string OpenConnections = "http.client.open_connections";
    private const string ConnectionsCreated = "http.client.connections.created";

    /// <summary>Hosts kept as individual series; the rest collapse into <c>other</c>.</summary>
    public const int MaxTrackedHosts = 100;

    private readonly ConcurrentDictionary<string, long> _open = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _created = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _knownHosts = new(StringComparer.Ordinal);
    private MeterListener? _listener;
    private int _started;

    /// <summary>True once the listener is attached, i.e. this runtime publishes client metrics.</summary>
    public bool IsListening => Volatile.Read(ref _started) == 1;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return Task.CompletedTask;

        try
        {
            var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == MeterName)
                        l.EnableMeasurementEvents(instrument);
                }
            };

            listener.SetMeasurementEventCallback<long>(OnMeasurement);
            listener.SetMeasurementEventCallback<double>(OnMeasurementDouble);
            listener.Start();
            _listener = listener;
        }
        catch
        {
            // Instrumentation must never block startup: on an unknown runtime we simply
            // report that the listener is not attached and move on.
            Volatile.Write(ref _started, 0);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _listener?.Dispose();
        _listener = null;
        Volatile.Write(ref _started, 0);
    }

    private void OnMeasurement(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        => Record(instrument, value, tags);

    private void OnMeasurementDouble(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        => Record(instrument, value, tags);

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var host = Bucket(TagValue(tags, "server.address") ?? TagValue(tags, "host") ?? "unknown");

        switch (instrument.Name)
        {
            case OpenConnections:
            {
                var state = TagValue(tags, "http.connection.state") ?? "unknown";
                var key = host + "\u0001" + state;
                // Observable gauge → absolute value; UpDownCounter → delta to accumulate.
                if (instrument.IsObservable)
                    _open[key] = (long)value;
                else
                    _open.AddOrUpdate(key, (long)value, (_, current) => current + (long)value);
                break;
            }

            case ConnectionsCreated:
                _created.AddOrUpdate(host, (long)value, (_, current) => current + (long)value);
                break;
        }
    }

    /// <summary>Snapshot of open connections grouped by state, for tests and diagnostics.</summary>
    public IReadOnlyDictionary<string, long> SnapshotByState()
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (key, value) in _open)
        {
            var state = key[(key.IndexOf('\u0001') + 1)..];
            result[state] = result.TryGetValue(state, out var current) ? current + value : value;
        }
        return result;
    }

    /// <summary>Writes the pool gauges. Called from the <c>/metrics</c> render path.</summary>
    /// <param name="sb">Destination buffer.</param>
    public void WriteMetrics(StringBuilder sb)
    {
        sb.AppendLine("# HELP gateway_upstream_connections Open HTTP connections to upstreams, by state.");
        sb.AppendLine("# TYPE gateway_upstream_connections gauge");

        if (_open.IsEmpty)
        {
            sb.AppendLine("gateway_upstream_connections{host=\"\",state=\"active\"} 0");
            sb.AppendLine("gateway_upstream_connections{host=\"\",state=\"idle\"} 0");
        }
        else
        {
            foreach (var (key, value) in _open.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var separator = key.IndexOf('\u0001');
                sb.Append("gateway_upstream_connections{host=\"")
                  .Append(GatewayMetricsCollector.Escape(key[..separator]))
                  .Append("\",state=\"")
                  .Append(GatewayMetricsCollector.Escape(key[(separator + 1)..]))
                  .Append("\"} ")
                  .Append(value.ToString(CultureInfo.InvariantCulture))
                  .AppendLine();
            }
        }

        sb.AppendLine("# HELP gateway_upstream_connections_created_total Connections opened since start (cumulative).");
        sb.AppendLine("# TYPE gateway_upstream_connections_created_total counter");
        if (_created.IsEmpty)
        {
            sb.AppendLine("gateway_upstream_connections_created_total{host=\"\"} 0");
        }
        else
        {
            foreach (var (host, value) in _created.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                sb.Append("gateway_upstream_connections_created_total{host=\"")
                  .Append(GatewayMetricsCollector.Escape(host))
                  .Append("\"} ")
                  .Append(value.ToString(CultureInfo.InvariantCulture))
                  .AppendLine();
            }
        }

        // 1 = the runtime publishes client metrics and the listener is attached; 0 = this
        // runtime does not, so the zeroes above mean "unknown" rather than "no connections".
        sb.AppendLine("# HELP gateway_upstream_metrics_available Whether the runtime publishes System.Net.Http metrics.");
        sb.AppendLine("# TYPE gateway_upstream_metrics_available gauge");
        sb.Append("gateway_upstream_metrics_available ")
          .Append(IsListening ? "1" : "0")
          .AppendLine();

        // Observable instruments only fire on demand.
        _listener?.RecordObservableInstruments();
    }

    private string Bucket(string host)
    {
        if (host.Length == 0) return "unknown";

        if (_knownHosts.ContainsKey(host) || _knownHosts.Count < MaxTrackedHosts)
        {
            _knownHosts.TryAdd(host, 0);
            return host;
        }

        return "other";
    }

    private static string? TagValue(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, key, StringComparison.Ordinal))
                return tag.Value?.ToString();
        }
        return null;
    }
}
