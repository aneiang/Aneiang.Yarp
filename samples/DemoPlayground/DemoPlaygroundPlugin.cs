using Aneiang.Yarp.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DemoPlayground;

/// <summary>
/// 演示插件：把绑定里保存的配置变成响应头、延时或一段 JSON 回显，用来验证
/// 「插件市场安装 → 启用 → 绑定 → 编辑配置 → 生效」这条链路是否真的通。
/// </summary>
/// <remarks>
/// <para>
/// 这个插件的价值不在于它做了什么，而在于它<b>什么都照配置做</b>：面板里存进去的每个字段，
/// 都会在响应里留下痕迹。所以链路里任何一环断了，看一眼响应就知道断在哪：
/// 没有 <c>X-Demo-Badge</c> 说明中间件没进管线，值不对说明配置没到插件手里。
/// </para>
/// <para>
/// 配置 schema 唯一的定义在随包发布的 <c>plugin.json</c> 里（见 <c>plugin.manifest.json</c>）。
/// 这里不重复声明，因为宿主对外部插件读的就是 <c>plugin.json</c> 那一份——两处各写一遍，
/// 迟早会有一处先改，然后面板显示的和后端校验的对不上。
/// </para>
/// </remarks>
public sealed class DemoPlaygroundPlugin : IGatewayPlugin
{
    /// <summary>Plugin id，同时也是绑定里的 pluginId。</summary>
    public const string Id = DemoPlaygroundMiddleware.PluginId;

    /// <inheritdoc />
    public string PluginId => Id;

    /// <inheritdoc />
    public string DisplayName => "Demo Playground";

    /// <inheritdoc />
    public string Version => "1.0.0";

    /// <inheritdoc />
    public PluginManifest Manifest => new(
        PluginId,
        DisplayName,
        Version,
        [PluginScope.Route],
        [PluginCapability.ProxyPipeline],
        700,
        new PluginResourceRequirements(RequestMiddleware: true),
        [],
        "把绑定里保存的配置变成响应头和延时，用来验证插件市场安装、启用与配置编辑的完整链路。");

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, object? pluginOptions = null)
    {
        // 这里没有需要注册的服务：中间件只依赖宿主已经注册好的快照发布器。
        // 要读插件的全局选项（appsettings 里的 Gateway:Plugins:<id>:Options）时，
        // 才需要在这里把 pluginOptions 绑成强类型配置注册进去。
    }

    /// <inheritdoc />
    /// <remarks>应用级管线在启动时已构建完成，宿主只对内置 WAF 调用它。真正生效的入口是 <see cref="ConfigureProxyPipeline"/>。</remarks>
    public void ConfigureMiddleware(IApplicationBuilder app) { }

    /// <inheritdoc />
    public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline) =>
        proxyPipeline.UseMiddleware<DemoPlaygroundMiddleware>();

    /// <inheritdoc />
    public void ConfigureDashboard(IPluginDashboardBuilder builder) { }
}
