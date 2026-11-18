using Aneiang.Yarp.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Aneiang.Yarp.Plugin.FaultInjection;

/// <summary>
/// Route-scoped fault injection: adds latency, answers with a chosen status code, or drops the
/// connection, so retries, circuit breakers, alerts and canary rollback can be verified on purpose.
/// </summary>
/// <remarks>
/// Unlike the rest of the built-in plugins this one makes the gateway <em>worse</em> on purpose,
/// which is why it is route-scoped rather than global, why every rule is off by default, and why a
/// header can be required before anything is injected.
/// </remarks>
public sealed class FaultInjectionPlugin : IGatewayPlugin
{
    /// <summary>Plugin identifier used by bindings and the execution plan.</summary>
    public const string Id = FaultInjectionMiddleware.PluginId;

    /// <inheritdoc />
    public string PluginId => Id;

    /// <inheritdoc />
    public string DisplayName => "Fault Injection";

    /// <inheritdoc />
    public string Version => "1.0";

    /// <inheritdoc />
    public PluginManifest Manifest => new(
        PluginId, DisplayName, Version,
        [PluginScope.Route],
        [PluginCapability.ProxyPipeline],
        640,
        new PluginResourceRequirements(RequestMiddleware: true),
        [new PluginSchemaReference(1, "builtin://plugins/fault-injection/schemas/config-v1.json", Schema)],
        "Injects latency or failures into a route on purpose, for drills and resilience testing.");

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, object? pluginOptions = null) { }

    /// <inheritdoc />
    public void ConfigureMiddleware(IApplicationBuilder app) { }

    /// <inheritdoc />
    public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline)
        => proxyPipeline.UseMiddleware<FaultInjectionMiddleware>();

    /// <inheritdoc />
    public void ConfigureDashboard(IPluginDashboardBuilder builder) { }

    /// <summary>
    /// Validation schema for the binding editor. Every rule defaults to "do nothing", so saving a
    /// half-filled form cannot take a route down.
    /// </summary>
    private const string Schema = """
        {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,"required":["enabled"],"properties":{"enabled":{"type":"boolean","default":false,"description":"Turn injection on for this route."},"probability":{"type":"number","minimum":0,"maximum":1,"default":1,"description":"Fraction of matching requests affected."},"delayMinMs":{"type":"integer","minimum":0,"maximum":120000,"default":0,"description":"Lower bound of injected latency in milliseconds."},"delayMaxMs":{"type":"integer","minimum":0,"maximum":120000,"default":0,"description":"Upper bound of injected latency; below delayMinMs means a fixed delay."},"errorStatusCode":{"anyOf":[{"const":0},{"type":"integer","minimum":400,"maximum":599}],"default":0,"description":"Status code returned instead of forwarding; 0 disables it."},"errorBody":{"type":["string","null"],"maxLength":4096,"description":"Body returned with the status code; empty writes a generic JSON body."},"abortConnection":{"type":"boolean","default":false,"description":"Drop the connection instead of answering, simulating a transport failure."},"requiredHeader":{"type":["string","null"],"maxLength":128,"description":"Inject only when this request header is present."},"requiredHeaderValue":{"type":["string","null"],"maxLength":256,"description":"Also require this header value."},"announce":{"type":"boolean","default":true,"description":"Add X-Fault-Injected to the response so a drill is not mistaken for an outage."}}}
        """;
}
