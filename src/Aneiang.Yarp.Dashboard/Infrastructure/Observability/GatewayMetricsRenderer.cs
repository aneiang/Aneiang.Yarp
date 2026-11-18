using System.Globalization;
using System.Reflection;
using System.Text;
using Aneiang.Yarp.Infrastructure.State;
using Aneiang.Yarp.Models;
using Aneiang.Yarp.Services;

namespace Aneiang.Yarp.Dashboard.Infrastructure.Observability;

/// <summary>
/// Composes the full Prometheus exposition text: request metrics from
/// <see cref="GatewayMetricsCollector"/> plus configuration and circuit-breaker gauges
/// that require resolved services.
///
/// Kept separate from the collector so the collector itself stays a plain, unit-testable
/// accumulator with no DI dependency.
/// </summary>
public sealed class GatewayMetricsRenderer
{
    private readonly GatewayMetricsCollector _collector;
    private readonly IServiceProvider _services;

    public GatewayMetricsRenderer(GatewayMetricsCollector collector, IServiceProvider services)
    {
        _collector = collector;
        _services = services;
    }

    /// <summary>Renders the whole exposition body (text format version 0.0.4).</summary>
    public string Render()
    {
        var sb = new StringBuilder(8192);

        _collector.WriteRequestMetrics(sb);
        _collector.WriteDestinationMetrics(sb);
        WriteBuildInfo(sb);
        WriteConfigurationGauges(sb);
        WriteCircuitGauges(sb);
        WriteConnectionPoolGauges(sb);

        return sb.ToString();
    }

    private void WriteConnectionPoolGauges(StringBuilder sb)
    {
        // Optional: present only when the dashboard registered the monitor. Monitoring a
        // gateway without it should still render the rest of the exposition.
        var monitor = _services.GetService(typeof(UpstreamConnectionMonitor)) as UpstreamConnectionMonitor;
        monitor?.WriteMetrics(sb);
    }

    private static void WriteBuildInfo(StringBuilder sb)
    {
        var assembly = typeof(GatewayMetricsRenderer).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "unknown";

        // Strip the "+<commit sha>" source-revision suffix: it would create a new series
        // on every rebuild, which is exactly the kind of churn Prometheus dislikes.
        var plusIndex = version.IndexOf('+');
        if (plusIndex > 0) version = version[..plusIndex];

        sb.AppendLine("# HELP gateway_build_info Build identity of the running gateway.");
        sb.AppendLine("# TYPE gateway_build_info gauge");
        sb.Append("gateway_build_info{version=\"")
          .Append(GatewayMetricsCollector.Escape(version))
          .Append("\",runtime=\"")
          .Append(GatewayMetricsCollector.Escape(Environment.Version.ToString()))
          .AppendLine("\"} 1");
    }

    private void WriteConfigurationGauges(StringBuilder sb)
    {
        var config = _services.GetService(typeof(IDynamicYarpConfigService)) as IDynamicYarpConfigService;

        var routeCount = 0;
        var clusterCount = 0;
        long version = 0;

        if (config != null)
        {
            try
            {
                routeCount = config.GetRoutes().Count;
                clusterCount = config.GetClusters().Count;
                version = config.GetDynamicConfig()?.Version ?? 0;
            }
            catch
            {
                // Monitoring must never take the process down because config is mid-swap.
            }
        }

        sb.AppendLine("# HELP gateway_routes_configured Number of routes currently loaded.");
        sb.AppendLine("# TYPE gateway_routes_configured gauge");
        sb.Append("gateway_routes_configured ").Append(routeCount.ToString(CultureInfo.InvariantCulture)).AppendLine();

        sb.AppendLine("# HELP gateway_clusters_configured Number of clusters currently loaded.");
        sb.AppendLine("# TYPE gateway_clusters_configured gauge");
        sb.Append("gateway_clusters_configured ").Append(clusterCount.ToString(CultureInfo.InvariantCulture)).AppendLine();

        sb.AppendLine("# HELP gateway_config_version Monotonic version of the dynamic configuration. A version that keeps climbing on its own means something is rewriting config.");
        sb.AppendLine("# TYPE gateway_config_version gauge");
        sb.Append("gateway_config_version ").Append(version.ToString(CultureInfo.InvariantCulture)).AppendLine();
    }

    private void WriteCircuitGauges(StringBuilder sb)
    {
        var store = _services.GetService(typeof(ICircuitStateStore)) as ICircuitStateStore;
        if (store == null) return;

        int total = 0, open = 0, halfOpen = 0;
        try
        {
            foreach (var entry in store.GetAll())
            {
                total++;
                if (entry.Value.Status == CircuitStatus.Open) open++;
                else if (entry.Value.Status == CircuitStatus.HalfOpen) halfOpen++;
            }
        }
        catch
        {
            return;
        }

        sb.AppendLine("# HELP gateway_circuits Circuit breaker entries by state.");
        sb.AppendLine("# TYPE gateway_circuits gauge");
        sb.Append("gateway_circuits{state=\"closed\"} ").Append((total - open - halfOpen).ToString(CultureInfo.InvariantCulture)).AppendLine();
        sb.Append("gateway_circuits{state=\"open\"} ").Append(open.ToString(CultureInfo.InvariantCulture)).AppendLine();
        sb.Append("gateway_circuits{state=\"half_open\"} ").Append(halfOpen.ToString(CultureInfo.InvariantCulture)).AppendLine();
    }
}
