using System.Diagnostics.Metrics;
using System.Text;
using Aneiang.Yarp.Dashboard.Infrastructure.Observability;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Connection-pool visibility is read through a <see cref="MeterListener"/> on the
/// <c>System.Net.Http</c> meter. These cases drive the listener with a deliberately named
/// test meter, which is the only way to verify the delta-vs-absolute handling without
/// saturating a real socket pool.
/// </summary>
public class UpstreamConnectionMonitorTests
{
    private static string Render(UpstreamConnectionMonitor monitor)
    {
        var sb = new StringBuilder();
        monitor.WriteMetrics(sb);
        return sb.ToString();
    }

    [Fact]
    public void BeforeStart_ReportsZeroAndUnavailable()
    {
        // "0 connections and metrics unavailable" must not be confused with
        // "0 connections and the pool is genuinely idle".
        using var monitor = new UpstreamConnectionMonitor();

        var text = Render(monitor);

        Assert.False(monitor.IsListening);
        Assert.Contains("gateway_upstream_connections{host=\"\",state=\"active\"} 0", text);
        Assert.Contains("gateway_upstream_metrics_available 0", text);
    }

    [Fact]
    public async Task StartAsync_IsIdempotent()
    {
        using var monitor = new UpstreamConnectionMonitor();

        await monitor.StartAsync(CancellationToken.None);
        await monitor.StartAsync(CancellationToken.None);

        Assert.True(monitor.IsListening);
        Assert.Contains("gateway_upstream_metrics_available 1", Render(monitor));
    }

    [Fact]
    public async Task CreatedConnections_AreSummedPerHost()
    {
        using var monitor = new UpstreamConnectionMonitor();
        await monitor.StartAsync(CancellationToken.None);

        using var meter = new Meter("System.Net.Http");
        var created = meter.CreateCounter<long>("http.client.connections.created");

        created.Add(3, new KeyValuePair<string, object?>("server.address", "api.internal"));
        created.Add(2, new KeyValuePair<string, object?>("server.address", "api.internal"));

        Assert.Contains("gateway_upstream_connections_created_total{host=\"api.internal\"} 5", Render(monitor));
    }

    [Fact]
    public async Task OpenConnections_AccumulateDeltas()
    {
        // .NET 8 publishes open_connections as a synchronous UpDownCounter: each
        // measurement is a *change*, not a level. Summing them is what makes the gauge
        // read as a level.
        using var monitor = new UpstreamConnectionMonitor();
        await monitor.StartAsync(CancellationToken.None);

        using var meter = new Meter("System.Net.Http");
        var open = meter.CreateUpDownCounter<long>("http.client.open_connections");

        var tags = new[]
        {
            new KeyValuePair<string, object?>("server.address", "api.internal"),
            new KeyValuePair<string, object?>("http.connection.state", "active")
        };

        open.Add(5, tags);
        open.Add(-2, tags);

        var text = Render(monitor);
        Assert.Contains("gateway_upstream_connections{host=\"api.internal\",state=\"active\"} 3", text);

        var byState = monitor.SnapshotByState();
        Assert.Equal(3, byState["active"]);
    }

    [Fact]
    public async Task OnlyTheHttpMeterIsObserved()
    {
        using var monitor = new UpstreamConnectionMonitor();
        await monitor.StartAsync(CancellationToken.None);

        using var meter = new Meter("Some.Other.Library");
        var created = meter.CreateCounter<long>("http.client.connections.created");
        created.Add(9, new KeyValuePair<string, object?>("server.address", "should.not.appear"));

        Assert.DoesNotContain("should.not.appear", Render(monitor));
    }

    [Fact]
    public async Task HostsBeyondTheCap_CollapseIntoOther()
    {
        using var monitor = new UpstreamConnectionMonitor();
        await monitor.StartAsync(CancellationToken.None);

        using var meter = new Meter("System.Net.Http");
        var created = meter.CreateCounter<long>("http.client.connections.created");

        for (var i = 0; i < UpstreamConnectionMonitor.MaxTrackedHosts + 5; i++)
        {
            created.Add(1, new KeyValuePair<string, object?>("server.address", $"host-{i}"));
        }

        var text = Render(monitor);

        Assert.Contains("gateway_upstream_connections_created_total{host=\"other\"}", text);
    }

    [Fact]
    public void Dispose_StopsListening()
    {
        var monitor = new UpstreamConnectionMonitor();
        monitor.Dispose();

        Assert.False(monitor.IsListening);
    }
}
