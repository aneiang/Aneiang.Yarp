using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aneiang.Yarp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Model;

namespace DemoPlayground;

/// <summary>
/// 按绑定配置给响应盖章：附加头、人为延时，或者干脆用当前配置回一段 JSON。
/// </summary>
/// <remarks>
/// <para><b>配置从哪来。</b>网关把「活着的」绑定编成一份不可变快照（<see cref="GatewaySnapshot"/>），
/// 中间件每个请求读一次当前快照，就能拿到本路由上属于本插件的那条绑定。启用插件、
/// 保存配置、改绑定都会发布新快照，所以不需要重启，也不需要自己缓存配置。</para>
///
/// <para><b>为什么不在启动时把配置读进来。</b>插件的中间件是随插件启停重建的，但绑定配置可以在
/// 不重建管线的情况下修改（同一个插件、同一条路由，只改了字段）。按请求读快照，改完立刻生效。</para>
/// </remarks>
public sealed class DemoPlaygroundMiddleware
{
    /// <summary>插件 id，也是快照里绑定记录的 pluginId。</summary>
    public const string PluginId = "demo-playground";

    /// <summary>只盖章：往响应上加头，请求照常转发。</summary>
    public const string ModeStamp = "stamp";

    /// <summary>回显：不转发上游，直接返回当前配置。</summary>
    public const string ModeEcho = "echo";

    /// <summary>静默：什么都不做，用来对比「配置在、但我不介入」。</summary>
    public const string ModeSilent = "silent";

    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// echo 用的序列化选项。默认的 Web 选项会把中文转成 \uXXXX，回显里全是转义序列就没法读了；
    /// 响应是 application/json、不嵌进 HTML，所以放开非 ASCII 转义是安全的。
    /// </summary>
    private static readonly JsonSerializerOptions EchoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>同上，不缩进。</summary>
    private static readonly JsonSerializerOptions EchoCompactJsonOptions = new(EchoJsonOptions) { WriteIndented = false };

    /// <summary>已经打过一次日志的路由，避免每请求一行。</summary>
    private readonly ConcurrentDictionary<string, byte> _announced = new(StringComparer.OrdinalIgnoreCase);

    private readonly RequestDelegate _next;
    private readonly ILogger<DemoPlaygroundMiddleware> _logger;
    private readonly IGatewaySnapshotPublisher _snapshots;

    /// <summary>初始化中间件。三个依赖都由宿主容器提供。</summary>
    public DemoPlaygroundMiddleware(
        RequestDelegate next,
        ILogger<DemoPlaygroundMiddleware> logger,
        IGatewaySnapshotPublisher snapshots)
    {
        _next = next;
        _logger = logger;
        _snapshots = snapshots;
    }

    /// <summary>处理一次代理请求。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var routeId = context.Features.Get<IReverseProxyFeature>()?.Route?.Config?.RouteId;
        if (string.IsNullOrEmpty(routeId) || !TryGetConfig(routeId, out var bindingId, out var config))
        {
            await _next(context);
            return;
        }

        if (!config.Enabled || IsSkipped(context, config) || !IsSampled(config.SamplePercent))
        {
            await _next(context);
            return;
        }

        if (config.DelayMs > 0 && !await DelayAsync(context, config.DelayMs))
            return;

        Announce(routeId, bindingId, config);

        if (IsMode(config, ModeSilent))
        {
            await _next(context);
            return;
        }

        if (IsMode(config, ModeEcho))
        {
            await WriteEchoAsync(context, routeId, bindingId, config);
            return;
        }

        // stamp：响应头必须在第一个字节写出之前设置，否则会被 Kestrel 丢掉。
        context.Response.OnStarting(() =>
        {
            ApplyHeaders(context, routeId, config);
            return Task.CompletedTask;
        });

        await _next(context);
    }

    /// <summary>从当前快照里取出本路由上属于本插件的绑定配置。</summary>
    /// <remarks>
    /// 快照里只会出现「绑定启用 + 插件启用」的配置，所以只要找到了，就说明这条绑定是活的。
    /// 配置 JSON 坏了不能影响转发：记一条警告，按默认值（不介入）继续。
    /// </remarks>
    private bool TryGetConfig(string routeId, out string bindingId, out DemoPlaygroundConfig config)
    {
        bindingId = string.Empty;
        config = new DemoPlaygroundConfig();

        if (!_snapshots.Current.RoutePlugins.TryGetValue(routeId, out var bindings))
            return false;

        foreach (var binding in bindings)
        {
            if (!string.Equals(binding.PluginId, PluginId, StringComparison.OrdinalIgnoreCase))
                continue;

            bindingId = binding.BindingId;
            if (string.IsNullOrWhiteSpace(binding.ConfigJson))
                return true;

            try
            {
                config = JsonSerializer.Deserialize<DemoPlaygroundConfig>(binding.ConfigJson, ConfigJsonOptions)
                    ?? new DemoPlaygroundConfig();
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "demo-playground: ignoring unreadable config on route {RouteId}", routeId);
                config = new DemoPlaygroundConfig();
            }

            return true;
        }

        return false;
    }

    private static bool IsMode(DemoPlaygroundConfig config, string mode) =>
        string.Equals(config.Mode, mode, StringComparison.OrdinalIgnoreCase);

    private static bool IsSkipped(HttpContext context, DemoPlaygroundConfig config)
    {
        if (config.SkipPaths.Count == 0)
            return false;

        var path = context.Request.Path.Value ?? string.Empty;
        foreach (var prefix in config.SkipPaths)
        {
            if (!string.IsNullOrWhiteSpace(prefix) && path.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>0 表示全量生效；1-100 表示按比例抽样，用来观察「只有一部分流量被改动」。</summary>
    private static bool IsSampled(int percent) =>
        percent <= 0 || Random.Shared.Next(100) < percent;

    /// <summary>人为延时；客户端先走了就不必再继续，已经没人接收响应了。</summary>
    private async Task<bool> DelayAsync(HttpContext context, int milliseconds)
    {
        try
        {
            await Task.Delay(milliseconds, context.RequestAborted);
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("demo-playground: client aborted during a {Delay} ms delay", milliseconds);
            return false;
        }
    }

    private void ApplyHeaders(HttpContext context, string routeId, DemoPlaygroundConfig config)
    {
        var headers = context.Response.Headers;
        headers["X-Demo-Plugin"] = PluginId;
        headers["X-Demo-Route"] = routeId;
        headers["X-Demo-Mode"] = config.Mode;

        if (!string.IsNullOrWhiteSpace(config.Badge))
            headers["X-Demo-Badge"] = config.Badge;

        if (config.DelayMs > 0)
            headers["X-Demo-Delay"] = config.DelayMs.ToString();

        foreach (var entry in config.ResponseHeaders)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
                continue;

            try
            {
                // 头名称由使用者填写，写错不能把整个响应打掉：跳过并记一条调试日志。
                headers[entry.Name.Trim()] = entry.Value ?? string.Empty;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                _logger.LogDebug(exception, "demo-playground: skipped response header '{HeaderName}'", entry.Name);
            }
        }
    }

    /// <summary>回显模式：把插件真正读到的配置原样返回，用来对照面板里存了什么。</summary>
    private async Task WriteEchoAsync(HttpContext context, string routeId, string bindingId, DemoPlaygroundConfig config)
    {
        ApplyHeaders(context, routeId, config);

        context.Response.StatusCode = config.Echo.StatusCode is >= 200 and <= 599 ? config.Echo.StatusCode : StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = new
        {
            plugin = PluginId,
            route = routeId,
            binding = bindingId,
            note = "这段 JSON 就是插件从网关快照里读到的绑定配置；改完配置刷新即可看到变化。",
            config
        };

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(payload, config.Echo.Pretty ? EchoJsonOptions : EchoCompactJsonOptions),
            context.RequestAborted);
    }

    /// <summary>每条路由第一次命中时记一条信息级日志，后面降为调试级。</summary>
    private void Announce(string routeId, string bindingId, DemoPlaygroundConfig config)
    {
        if (_announced.TryAdd(routeId, 0))
        {
            _logger.LogInformation(
                "demo-playground active on route {RouteId} (binding {BindingId}): mode={Mode}, delay={Delay} ms, sample={Sample}%",
                routeId, bindingId, config.Mode, config.DelayMs, config.SamplePercent);
        }
        else
        {
            _logger.LogDebug("demo-playground applied on route {RouteId}", routeId);
        }
    }
}
