using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Aneiang.Yarp.Infrastructure.Observability;

/// <summary>
/// Opens the <c>gateway.forward</c> span around one request through the reverse-proxy pipeline.
/// </summary>
/// <remarks>
/// Mounted first in the proxy branch, before the plugin pipeline, so every plugin span and YARP's
/// own upstream span nest underneath it. It measures the whole request - including a plugin that
/// short-circuits it - which is what makes "where did the 800ms go" answerable.
///
/// When no tracer is listening this middleware is a single boolean check followed by
/// <c>await next()</c>; there is no allocation and no tag building.
/// </remarks>
public sealed class GatewayTracingMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>Initializes a new instance of the <see cref="GatewayTracingMiddleware"/> class.</summary>
    public GatewayTracingMiddleware(RequestDelegate next) => _next = next;

    /// <summary>Runs the pipeline inside a span.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!GatewayTracing.IsEnabled)
        {
            await _next(context);
            return;
        }

        using var activity = GatewayTracing.StartForward(context);
        if (activity is null)
        {
            await _next(context);
            return;
        }

        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            activity.SetTag(GatewayTracing.ExceptionTag, ex.GetType().Name);
            activity.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            GatewayTracing.CompleteForward(activity, context);
        }
    }
}
