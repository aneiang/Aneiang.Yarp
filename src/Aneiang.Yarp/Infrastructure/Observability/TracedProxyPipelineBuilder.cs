using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Aneiang.Yarp.Infrastructure.Observability;

/// <summary>
/// Wraps an <see cref="IReverseProxyApplicationBuilder"/> so every middleware a plugin registers
/// gets its own span, attributed to that plugin.
/// </summary>
/// <remarks>
/// The alternative - having each plugin instrument itself - would leave third-party plugins
/// invisible, which is exactly the case where a trace matters most. Wrapping the builder instead
/// means a plugin author writes nothing and still shows up in the waterfall.
///
/// <para>
/// A plugin may register several middlewares (a WAF that also normalises the header, a cache with
/// a background refresh branch); each becomes its own span rather than being merged, because the
/// interesting question is "which one returned the 429".
/// </para>
/// </remarks>
public sealed class TracedProxyPipelineBuilder : IReverseProxyApplicationBuilder
{
    // Typed as the base interface because New() only promises that much; the class still satisfies
    // IReverseProxyApplicationBuilder, which adds no members of its own.
    private readonly IApplicationBuilder _inner;
    private readonly string _pluginId;

    /// <summary>Initializes a new instance of the <see cref="TracedProxyPipelineBuilder"/> class.</summary>
    /// <param name="inner">Builder the plugin would otherwise have received.</param>
    /// <param name="pluginId">Plugin id used to name and tag the span.</param>
    public TracedProxyPipelineBuilder(IApplicationBuilder inner, string pluginId)
    {
        _inner = inner;
        _pluginId = pluginId;
    }

    /// <inheritdoc />
    public IServiceProvider ApplicationServices
    {
        get => _inner.ApplicationServices;
        set => _inner.ApplicationServices = value;
    }

    /// <inheritdoc />
    public IFeatureCollection ServerFeatures => _inner.ServerFeatures;

    /// <inheritdoc />
    public IDictionary<string, object?> Properties => _inner.Properties;

    /// <inheritdoc />
    public IApplicationBuilder Use(Func<RequestDelegate, RequestDelegate> middleware)
    {
        _inner.Use(next =>
        {
            var handler = middleware(next);
            return async context =>
            {
                using var activity = GatewayTracing.StartPlugin(context, _pluginId);
                if (activity is null)
                {
                    await handler(context);
                    return;
                }

                try
                {
                    await handler(context);
                }
                catch (Exception ex)
                {
                    activity.SetTag(GatewayTracing.ExceptionTag, ex.GetType().Name);
                    activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                    throw;
                }
                finally
                {
                    // Plugins short-circuit by simply not calling next; recording the status here
                    // is what makes "the WAF rejected it" visible instead of an empty span.
                    activity.SetTag(GatewayTracing.StatusCodeTag, context.Response.StatusCode);
                }
            };
        });

        return this;
    }

    /// <inheritdoc />
    public IApplicationBuilder New() => new TracedProxyPipelineBuilder(_inner.New(), _pluginId);

    /// <inheritdoc />
    public RequestDelegate Build() => _inner.Build();
}
