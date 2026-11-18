using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace Aneiang.Yarp.Infrastructure.Observability;

/// <summary>
/// The gateway's own <see cref="ActivitySource"/>, plus the tag names its spans use.
/// </summary>
/// <remarks>
/// This type deliberately has no exporter, no configuration and no dependency beyond
/// <c>System.Diagnostics.DiagnosticSource</c>, which ships with the framework. A gateway that
/// nobody instruments pays one <see cref="ActivitySource.HasListeners"/> check per request and
/// nothing else; wiring the spans to a collector is the management plane's job
/// (<c>Aneiang.Yarp.Dashboard</c>).
///
/// The span layout answers "which hop spent the time", not "how much traffic was there":
/// <code>
/// gateway.forward            (this middleware, one per proxied request)
/// ├── gateway.plugin waf     (one per plugin in the proxy pipeline)
/// ├── gateway.plugin circuit-breaker
/// ├── gateway.plugin request-retry
/// │   └── Yarp.ReverseProxy (the upstream call, emitted by YARP itself)
/// </code>
/// </remarks>
public static class GatewayTracing
{
    /// <summary>Name registered with the tracer provider. Consumers must <c>AddSource</c> it.</summary>
    public const string SourceName = "Aneiang.Yarp";

    /// <summary>Span covering one request through the reverse-proxy pipeline.</summary>
    public const string ForwardSpanName = "gateway.forward";

    /// <summary>Span covering one plugin in the proxy pipeline; the plugin id is appended.</summary>
    public const string PluginSpanName = "gateway.plugin";

    // Attribute names. OTel semantic conventions are used where they exist so a stock Jaeger or
    // Tempo UI groups gateway spans the same way it groups everything else; anything the
    // conventions do not cover gets a gateway.* name of its own.
    /// <summary>HTTP method, per OTel semantic conventions.</summary>
    public const string MethodTag = "http.request.method";

    /// <summary>Request path, per OTel semantic conventions.</summary>
    public const string PathTag = "url.path";

    /// <summary>Response status code, per OTel semantic conventions.</summary>
    public const string StatusCodeTag = "http.response.status_code";

    /// <summary>Resolved route id.</summary>
    public const string RouteIdTag = "gateway.route.id";

    /// <summary>Resolved cluster id.</summary>
    public const string ClusterIdTag = "gateway.cluster.id";

    /// <summary>Destination chosen by the load balancer.</summary>
    public const string DestinationIdTag = "gateway.destination.id";

    /// <summary>
    /// True when the request never reached a destination and no forwarding error was recorded,
    /// i.e. a plugin answered it (WAF block, rate limit, open circuit).
    /// </summary>
    public const string ShortCircuitedTag = "gateway.short_circuited";

    /// <summary>Plugin id on a <see cref="PluginSpanName"/> span.</summary>
    public const string PluginIdTag = "gateway.plugin.id";

    /// <summary>Forwarding error name reported by YARP, when the upstream call failed.</summary>
    public const string ForwarderErrorTag = "gateway.forwarder.error";

    /// <summary>Exception type that escaped the pipeline.</summary>
    public const string ExceptionTag = "gateway.exception";

    private static readonly string Version =
        typeof(GatewayTracing).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static readonly ActivitySource ActivitySource = new(SourceName, Version);

    private static volatile bool _pluginSpansEnabled = true;

    /// <summary>
    /// Whether plugin stages get their own span. Read per request, set once at startup from
    /// <c>Gateway:Dashboard:Tracing:RecordPluginSpans</c>.
    /// </summary>
    public static bool PluginSpansEnabled
    {
        get => _pluginSpansEnabled;
        set => _pluginSpansEnabled = value;
    }

    /// <summary>True when any tracer is listening; callers use it to skip work entirely.</summary>
    public static bool IsEnabled => ActivitySource.HasListeners();

    /// <summary>
    /// Starts the span for one proxied request. Returns <c>null</c> when nobody is listening, in
    /// which case the caller must still run the pipeline untouched.
    /// </summary>
    public static Activity? StartForward(HttpContext context)
    {
        if (!ActivitySource.HasListeners())
            return null;

        var activity = ActivitySource.StartActivity(ForwardSpanName, ActivityKind.Server);
        if (activity is null)
            return null;

        // Only cheap, already-available fields go in at start. Route and cluster are resolved by
        // YARP's own middleware further down the pipeline, so they are recorded on completion
        // (see CompleteForward) rather than read as null here.
        activity.SetTag(MethodTag, context.Request.Method);
        activity.SetTag(PathTag, context.Request.Path.Value ?? "/");

        return activity;
    }

    /// <summary>Starts a span for one plugin stage, nested under the current span.</summary>
    public static Activity? StartPlugin(HttpContext context, string pluginId)
    {
        if (!PluginSpansEnabled || !ActivitySource.HasListeners())
            return null;

        var activity = ActivitySource.StartActivity($"{PluginSpanName} {pluginId}", ActivityKind.Internal);
        activity?.SetTag(PluginIdTag, pluginId);
        return activity;
    }

    /// <summary>
    /// Fills in the outcome of a request and marks the span as failed when it was. Called from a
    /// <c>finally</c> block so a short-circuited or thrown request still produces a usable span.
    /// </summary>
    public static void CompleteForward(Activity activity, HttpContext context)
    {
        var feature = context.Features.Get<IReverseProxyFeature>();
        var routeId = feature?.Route?.Config?.RouteId;
        var clusterId = feature?.Cluster?.Config?.ClusterId;
        var destinationId = feature?.ProxiedDestination?.DestinationId;

        if (!string.IsNullOrEmpty(routeId))
            activity.SetTag(RouteIdTag, routeId);
        if (!string.IsNullOrEmpty(clusterId))
            activity.SetTag(ClusterIdTag, clusterId);
        if (!string.IsNullOrEmpty(destinationId))
            activity.SetTag(DestinationIdTag, destinationId);

        var error = context.Features.Get<IForwarderErrorFeature>();
        if (error is not null)
            activity.SetTag(ForwarderErrorTag, error.Error.ToString());

        // No destination chosen and no forwarding error means the request was answered before it
        // ever reached YARP's forwarder: a plugin blocked it. That distinction is the whole point
        // of the span - "503 from the circuit breaker" and "503 from upstream" are different bugs.
        var shortCircuited = destinationId is null && error is null;
        activity.SetTag(ShortCircuitedTag, shortCircuited);

        var status = context.Response.StatusCode;
        activity.SetTag(StatusCodeTag, status);

        if (status >= 500 || error is not null)
        {
            var message = error is not null ? error.Error.ToString() : $"HTTP {status}";
            activity.SetStatus(ActivityStatusCode.Error, message);
        }
    }
}
