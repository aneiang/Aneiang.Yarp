using System.Text.Json;
using Aneiang.Yarp.Dashboard.Infrastructure;
using Aneiang.Yarp.Dashboard.Infrastructure.OpenApi;
using Aneiang.Yarp.Dashboard.Modules.Dashboard.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.Dashboard.Modules.ApiDocs.Controllers;

/// <summary>
/// Serves the management API reference: a generated OpenAPI 3.1 document and a page that renders it.
/// </summary>
/// <remarks>
/// <para>
/// The document is generated per request rather than cached. It is built from the live route table, so
/// caching it would create exactly one new way for the reference to be wrong — and the request is
/// rare enough that the reflection cost never shows up in a profile.
/// </para>
/// <para>
/// The rendering page is intentionally dependency-free: no Swagger UI, no CDN script. A control plane
/// that cannot load its own documentation without outbound internet access is a control plane that
/// will fail to explain itself in the environment where that matters most.
/// </para>
/// </remarks>
public class ApiDocsController : Controller
{
    private readonly IEnumerable<EndpointDataSource> _endpointSources;
    private readonly DashboardOptions _options;
    private readonly ILogger<ApiDocsController> _logger;

    /// <summary>Creates the controller.</summary>
    /// <param name="endpointSources">Endpoint sources, enumerated when the document is built.</param>
    /// <param name="options">Dashboard options (route prefix, API key header name).</param>
    /// <param name="logger">Logger.</param>
    public ApiDocsController(
        IEnumerable<EndpointDataSource> endpointSources,
        IOptions<DashboardOptions> options,
        ILogger<ApiDocsController> logger)
    {
        _endpointSources = endpointSources;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The OpenAPI 3.1 document for the management API.</summary>
    [HttpGet("api/openapi.json")]
    public IActionResult Document()
    {
        try
        {
            var document = new OpenApiDocumentBuilder(_endpointSources, BuildOptions()).Build();
            return Content(document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "application/json");
        }
        catch (Exception ex)
        {
            // A documentation endpoint that throws is worse than a missing one: the person opening it
            // is usually already trying to work out why an integration fails.
            _logger.LogError(ex, "Failed to build the OpenAPI document");
            return StatusCode(500, new { code = 500, message = "Failed to build the OpenAPI document" });
        }
    }

    /// <summary>Human-readable API reference page.</summary>
    [HttpGet("api-docs")]
    public IActionResult Index()
    {
        ViewBag.DashboardRoutePrefix = DashboardPagesController.RoutePrefix;
        ViewBag.Locale = ResolveLocale();
        ViewBag.CurrentPage = "api-docs";
        return View("~/Views/ApiDocs/Index.cshtml");
    }

    private OpenApiBuilderOptions BuildOptions()
    {
        var options = new OpenApiBuilderOptions
        {
            BasePath = "/" + DashboardPagesController.RoutePrefix,
            ProductVersion = typeof(ApiDocsController).Assembly.GetName().Version?.ToString() ?? "2.3.0.40",
            ApiKeyHeaderName = _options.Auth.ApiKeyHeaderName,
            DocumentationAssembly = typeof(ApiDocsController).Assembly
        };

        options.TagDescriptions["DashboardAuth"] = "登录、登出与两步验证。";
        options.TagDescriptions["RouteConfig"] = "路由配置。写入前会做校验，指向不存在集群的路由会被拒绝。";
        options.TagDescriptions["ClusterConfig"] = "集群与目标节点配置。";
        options.TagDescriptions["ConfigState"] = "配置版本、预检与体检。写操作带 `X-Config-Version` 以防并发覆盖。";
        options.TagDescriptions["ConfigHistory"] = "配置快照与回滚。";
        options.TagDescriptions["ProxyConfigErrors"] = "最近一次配置应用失败的原因。";
        options.TagDescriptions["PluginBindings"] = "路由/集群上的插件绑定。";
        options.TagDescriptions["Plugins"] = "插件清单与装卸（装卸需要 admin）。";
        options.TagDescriptions["Environments"] = "多环境读取、切换、差异与提升。";
        options.TagDescriptions["WebhookSettings"] = "通知目标与投递设置（需要 admin）。";
        options.TagDescriptions["TrafficStats"] = "流量统计。";
        options.TagDescriptions["DashboardLog"] = "请求日志查询。";
        options.TagDescriptions["DashboardInfo"] = "网关运行信息。";
        options.TagDescriptions["OverviewSnapshot"] = "总览页聚合数据。";

        return options;
    }

    private string ResolveLocale()
    {
        var cookie = Request.Cookies["dashboard_locale"];
        if (!string.IsNullOrEmpty(cookie)) return cookie == "en-US" ? "en-US" : "zh-CN";
        return _options.Locale == "en-US" ? "en-US" : "zh-CN";
    }
}
