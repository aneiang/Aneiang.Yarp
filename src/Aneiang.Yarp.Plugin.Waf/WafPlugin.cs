using Aneiang.Yarp.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Aneiang.Yarp.Plugin.Waf;

public class WafPlugin : IGatewayPlugin
{
    public string PluginId => "waf";
    public string DisplayName => "Web Application Firewall";
    public string Version => "2.0";
    public PluginManifest Manifest => new(
        PluginId, DisplayName, Version,
        [PluginScope.Route],
        [PluginCapability.RequestMiddleware, PluginCapability.Dashboard],
        100,
        new PluginResourceRequirements(RequestMiddleware: true, Database: true),
        [new PluginSchemaReference(1, "builtin://plugins/waf/schemas/config-v1.json", """
            {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,"properties":{"enabled":{"type":"boolean","default":true},"mode":{"type":"string","enum":["enforce","monitor"],"default":"enforce"},"enableIpCheck":{"type":"boolean","default":true},"ipWhitelist":{"type":"array","items":{"type":"string"},"uniqueItems":true,"default":[]},"ipBlacklist":{"type":"array","items":{"type":"string"},"uniqueItems":true,"default":[]},"enableRequestSizeValidation":{"type":"boolean","default":true},"maxRequestBodySize":{"type":"integer","minimum":0,"default":10485760},"maxHeaderCount":{"type":"integer","minimum":0,"default":64},"maxHeaderSize":{"type":"integer","minimum":0,"default":8192},"maxUrlLength":{"type":"integer","minimum":0,"default":4096},"enableSqlInjectionDetection":{"type":"boolean","default":true},"enableXssDetection":{"type":"boolean","default":true},"enablePathTraversalDetection":{"type":"boolean","default":true},"rules":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{"id":{"type":"string"},"name":{"type":"string"},"enabled":{"type":"boolean","default":true},"builtin":{"type":"string","enum":["sql-injection","xss","path-traversal"]},"matchMode":{"type":"string","enum":["all","any"],"default":"all"},"action":{"type":"string","enum":["block","allow","log","tag","rate-limit"],"default":"block"},"tag":{"type":"string"},"statusCode":{"type":"integer","minimum":400,"maximum":599},"conditions":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{"source":{"type":"string","default":"path","enum":["method","path","query","body","header","cookie","userAgent","ip","contentType","bodySize"]},"op":{"type":"string","default":"contains","enum":["equals","contains","prefix","suffix","regex","cidr","greaterThan","lessThan"]},"name":{"type":"string"},"value":{"type":"string"},"ignoreCase":{"type":"boolean","default":true}},"required":["source","op","value"]},"default":[]},"rateLimit":{"type":"object","additionalProperties":false,"properties":{"limit":{"type":"integer","minimum":1,"default":60},"window":{"type":"string","default":"1m"},"partition":{"type":"string","enum":["ip","path","ip-path","global"],"default":"ip"}}}}},"default":[]},"bodyInspection":{"type":"object","additionalProperties":false,"properties":{"enabled":{"type":"boolean","default":true},"maxBytes":{"type":"integer","minimum":0,"default":65536},"contentTypes":{"type":"array","items":{"type":"string"},"default":["text/","application/json","application/x-www-form-urlencoded","application/xml","application/graphql"]},"inspectUnknownContentType":{"type":"boolean","default":false}}},"response":{"type":"object","additionalProperties":false,"properties":{"statusCode":{"type":"integer","minimum":400,"maximum":599,"default":403},"contentType":{"type":"string","default":"application/json; charset=utf-8"},"body":{"type":"string"},"headers":{"type":"object","additionalProperties":{"type":"string"}},"includeRuleHeader":{"type":"boolean","default":true}}}}}
            """)],
        "Route-scoped request inspection, blocking, audit, and configuration management.");

    public void ConfigureServices(IServiceCollection services, object? pluginOptions = null) { }
    public void ConfigureMiddleware(IApplicationBuilder app) => app.UseMiddleware<WafMiddleware>();
    public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline) { }

    public void ConfigureDashboard(IPluginDashboardBuilder builder) { }

}
