using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Model;

namespace Aneiang.Yarp.Dashboard.Infrastructure.Observability;

/// <summary>
/// Records one observation per proxied request. Mounted directly in the reverse-proxy
/// pipeline (not in the plugin pipeline) so the numbers stay meaningful even when no
/// observability plugin is bound — an external Prometheus should never see a dead gateway.
/// </summary>
public sealed class GatewayMetricsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly GatewayMetricsCollector _collector;

    public GatewayMetricsMiddleware(RequestDelegate next, GatewayMetricsCollector collector)
    {
        _next = next;
        _collector = collector;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        _collector.OnRequestStart();

        try
        {
            await _next(context);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;

            // The proxy feature carries the resolved route/cluster. On an early failure
            // (bad destination, forwarding error) it is still populated — only a request
            // that never matched would be missing it.
            var feature = context.Features.Get<IReverseProxyFeature>();
            var routeId = feature?.Route?.Config?.RouteId;
            var clusterId = feature?.Cluster?.Config?.ClusterId;

            _collector.OnRequestEnd(routeId, clusterId, context.Response.StatusCode, elapsed);

            // ProxiedDestination is only set after the load balancer has picked a node, so it is
            // read here rather than at request start. Null when the request never reached a
            // destination (no healthy node, forwarding failed before selection).
            _collector.OnDestinationEnd(
                clusterId,
                feature?.ProxiedDestination?.DestinationId,
                context.Response.StatusCode);
        }
    }
}
