using System.Collections.Immutable;
using System.Net;
using System.Text;
using Aneiang.Yarp.Infrastructure.Middleware;
using Aneiang.Yarp.Plugin.Waf;
using Aneiang.Yarp.Services;
using Aneiang.Yarp.Storage.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Model;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// What a request actually experiences: forwarded, blocked, or annotated.
/// </summary>
public class WafMiddlewareTests
{
    private const string RouteId = "r1";

    private const string MultipartEnvelope =
        "------WebKitFormBoundary7MA4YWxkTrZu0gW\r\n" +
        "Content-Disposition: form-data; name=\"file\"; filename=\"photo.png\"\r\n" +
        "Content-Type: image/png\r\n\r\n" +
        "\u0089PNG\r\n\u001a\n\u0000\u0000\u0000\rIHDR....\r\n" +
        "------WebKitFormBoundary7MA4YWxkTrZu0gW--\r\n";

    // ──────────────── The reported bug ────────────────

    [Fact]
    public async Task A_multipart_upload_is_forwarded_when_sql_detection_is_on()
    {
        var (middleware, next, context) = Create("""{"enableSqlInjectionDetection":true,"enableXssDetection":true}""");
        SetBody(context, MultipartEnvelope, "multipart/form-data; boundary=----WebKitFormBoundary7MA4YWxkTrZu0gW");

        await middleware.InvokeAsync(context);

        Assert.True(next.Called);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task A_binary_upload_is_not_pattern_matched_as_text()
    {
        var (middleware, next, context) = Create("{}");
        SetBody(context, "%PDF-1.4\n%\u00e2\u00e3\u00cf\u00d3\n1 0 obj\n<< /Type /Catalog >>", "application/pdf");

        await middleware.InvokeAsync(context);

        Assert.True(next.Called);
    }

    [Fact]
    public async Task A_query_string_with_a_cli_flag_is_forwarded()
    {
        var (middleware, next, context) = Create("{}");
        context.Request.Method = "GET";
        context.Request.QueryString = new QueryString("?args=--verbose&cmd=--help");

        await middleware.InvokeAsync(context);

        Assert.True(next.Called);
    }

    // ──────────────── Still a firewall ────────────────

    [Fact]
    public async Task An_injection_in_a_json_body_is_blocked_and_names_the_rule()
    {
        var (middleware, next, context) = Create("{}");
        SetBody(context, """{"user":"admin' OR '1'='1"}""", "application/json");

        await middleware.InvokeAsync(context);

        Assert.False(next.Called);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.Equal("sql-injection", context.Response.Headers[WafMiddleware.RuleHeader].ToString());
        Assert.Contains("Forbidden", ReadBody(context));
    }

    [Fact]
    public async Task An_injection_in_the_query_string_is_blocked()
    {
        var (middleware, next, context) = Create("{}");
        context.Request.Method = "GET";
        context.Request.QueryString = new QueryString("?q=1%27%20OR%20%271%27%3D%271");

        await middleware.InvokeAsync(context);

        Assert.False(next.Called);
        Assert.Equal(403, context.Response.StatusCode);
    }

    [Fact]
    public async Task The_body_is_readable_by_the_next_stage_after_inspection()
    {
        var (middleware, next, context) = Create("{}");
        SetBody(context, """{"name":"ordinary"}""", "application/json");
        next.OnInvoke = async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
            next.ObservedBody = await reader.ReadToEndAsync();
        };

        await middleware.InvokeAsync(context);

        Assert.True(next.Called);
        Assert.Equal("""{"name":"ordinary"}""", next.ObservedBody);
    }

    [Fact]
    public async Task A_body_larger_than_the_scan_budget_is_skipped_not_truncated()
    {
        var (middleware, next, context) = Create("""{"bodyInspection":{"maxBytes":16}}""");
        var payload = """{"note":"' OR '1'='1 padded so the body is longer than the budget"}""";
        SetBody(context, payload, "application/json");
        next.OnInvoke = async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
            next.ObservedBody = await reader.ReadToEndAsync();
        };

        await middleware.InvokeAsync(context);

        Assert.True(next.Called);
        Assert.Equal(payload, next.ObservedBody);
    }

    // ──────────────── Modes ────────────────

    [Fact]
    public async Task Monitor_mode_records_the_hit_but_forwards_the_request()
    {
        var (middleware, next, context) = Create("""{"mode":"monitor"}""");
        SetBody(context, """{"user":"admin' OR '1'='1"}""", "application/json");

        await middleware.InvokeAsync(context);

        Assert.True(next.Called);
        Assert.Equal(200, context.Response.StatusCode);
    }

    // ──────────────── Rule actions ────────────────

    [Fact]
    public async Task An_allow_rule_short_circuits_the_detectors_behind_it()
    {
        var (middleware, next, context) = Create("""
            {"rules":[
              {"id":"uploads-are-opaque","action":"allow",
               "conditions":[{"source":"path","op":"prefix","value":"/upload"}]},
              {"id":"sql","builtin":"sql-injection"}]}
            """);
        SetBody(context, """{"user":"admin' OR '1'='1"}""", "application/json", path: "/upload/file");

        await middleware.InvokeAsync(context);

        Assert.True(next.Called);
    }

    [Fact]
    public async Task A_tag_rule_stamps_the_request_and_keeps_going()
    {
        var (middleware, next, context) = Create("""
            {"rules":[{"id":"partner","action":"tag","tag":"partner",
                       "conditions":[{"source":"path","op":"prefix","value":"/api"}]}]}
            """);
        context.Request.Method = "GET";
        context.Request.Path = "/api/orders";

        await middleware.InvokeAsync(context);

        Assert.True(next.Called);
        Assert.Equal("partner", next.ObservedTag);
    }

    [Fact]
    public async Task A_log_rule_observes_without_blocking()
    {
        var (middleware, next, context) = Create("""
            {"rules":[{"id":"audit-admin","action":"log",
                       "conditions":[{"source":"path","op":"prefix","value":"/admin"}]}]}
            """);
        context.Request.Method = "GET";
        context.Request.Path = "/admin/panel";

        await middleware.InvokeAsync(context);

        Assert.True(next.Called);
        Assert.Equal(200, context.Response.StatusCode);
    }

    // ──────────────── Response shaping ────────────────

    [Fact]
    public async Task A_custom_block_response_is_used_verbatim()
    {
        var (middleware, next, context) = Create("""
            {"response":{"statusCode":429,"contentType":"text/plain","body":"slow down","headers":{"X-Reason":"waf"}},
             "rules":[{"id":"nope","action":"block","conditions":[{"source":"path","op":"prefix","value":"/api"}]}]}
            """);
        context.Request.Method = "GET";
        context.Request.Path = "/api/orders";

        await middleware.InvokeAsync(context);

        Assert.False(next.Called);
        Assert.Equal(429, context.Response.StatusCode);
        Assert.Equal("slow down", ReadBody(context));
        Assert.Equal("waf", context.Response.Headers["X-Reason"].ToString());
    }

    [Fact]
    public async Task The_rule_header_can_be_suppressed()
    {
        var (middleware, _, context) = Create("""
            {"response":{"includeRuleHeader":false},
             "rules":[{"id":"nope","action":"block","conditions":[{"source":"path","op":"prefix","value":"/api"}]}]}
            """);
        context.Request.Method = "GET";

        await middleware.InvokeAsync(context);

        Assert.False(context.Response.Headers.ContainsKey(WafMiddleware.RuleHeader));
    }

    // ──────────────── Preamble ────────────────

    [Fact]
    public async Task A_blacklisted_caller_is_rejected_before_any_rule_runs()
    {
        var (middleware, next, context) = Create("""{"ipBlacklist":["203.0.113.0/24"]}""");
        context.Request.Method = "GET";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");

        await middleware.InvokeAsync(context);

        Assert.False(next.Called);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.Contains("Access denied", ReadBody(context));
    }

    // ──────────────── Non-interference ────────────────

    [Fact]
    public async Task A_route_without_a_binding_is_left_alone()
    {
        var (middleware, next, context) = Create(null);
        SetBody(context, """{"user":"admin' OR '1'='1"}""", "application/json");

        await middleware.InvokeAsync(context);

        Assert.True(next.Called);
    }

    [Fact]
    public async Task A_disabled_binding_is_left_alone()
    {
        var (middleware, next, context) = Create("""{"enabled":false}""");
        SetBody(context, """{"user":"admin' OR '1'='1"}""", "application/json");

        await middleware.InvokeAsync(context);

        Assert.True(next.Called);
    }

    [Fact]
    public async Task The_dashboard_is_never_inspected()
    {
        var (middleware, next, context) = Create("{}");
        SetBody(context, """{"user":"admin' OR '1'='1"}""", "application/json", path: "/dashboard/plugins");

        await middleware.InvokeAsync(context);

        Assert.True(next.Called);
    }

    // ──────────────── Throttling ────────────────

    private const string ThrottleConfig = """
        {"rules":[
          {"id":"login-throttle","action":"rate-limit",
           "conditions":[{"source":"path","op":"prefix","value":"/login"}],
           "rateLimit":{"limit":2,"window":"1h","partition":"ip"}}]}
        """;

    [Fact]
    public async Task Requests_over_the_quota_get_429_with_retry_after()
    {
        var (middleware, next, _) = Create(ThrottleConfig);

        for (var i = 0; i < 2; i++)
        {
            var allowed = NewContext("/login");
            await middleware.InvokeAsync(allowed);
            Assert.Equal(200, allowed.Response.StatusCode);
        }

        var throttled = NewContext("/login");
        await middleware.InvokeAsync(throttled);

        Assert.Equal(429, throttled.Response.StatusCode);
        Assert.Equal("login-throttle", throttled.Response.Headers[WafMiddleware.RuleHeader].ToString());
        Assert.Equal("3600", throttled.Response.Headers[WafMiddleware.RetryAfterHeader].ToString());
        Assert.Contains("Too many requests", ReadBody(throttled));
        Assert.True(next.Called);
    }

    [Fact]
    public async Task Paths_outside_the_condition_are_not_counted()
    {
        var (middleware, _, _) = Create(ThrottleConfig);

        for (var i = 0; i < 5; i++)
        {
            var other = NewContext("/public/feed");
            await middleware.InvokeAsync(other);
            Assert.Equal(200, other.Response.StatusCode);
        }
    }

    [Fact]
    public async Task Monitor_mode_records_the_throttle_and_lets_the_request_through()
    {
        var (middleware, next, _) = Create("""
            {"mode":"monitor","rules":[
              {"id":"login-throttle","action":"rate-limit",
               "conditions":[{"source":"path","op":"prefix","value":"/login"}],
               "rateLimit":{"limit":1,"window":"1h","partition":"ip"}}]}
            """);

        await middleware.InvokeAsync(NewContext("/login"));
        var second = NewContext("/login");
        await middleware.InvokeAsync(second);

        Assert.Equal(200, second.Response.StatusCode);
        Assert.True(next.Called);
    }

    #region Helpers

    private static (WafMiddleware Middleware, RecordingNext Next, HttpContext Context) Create(string? configJson)
    {
        var route = new RouteConfig
        {
            RouteId = RouteId,
            ClusterId = "c1",
            Match = new RouteMatch { Path = "/{**catchAll}" }
        };

        var routePlugins = configJson is null
            ? ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty
            : ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add(
                RouteId,
                [new PluginBindingSnapshot("b1", "waf", PluginBindingScope.Route, RouteId, configJson, 1, 1, 0)]);

        var snapshot = GatewaySnapshot.Empty with
        {
            Version = 1,
            Routes = [route],
            RoutePlugins = routePlugins
        };

        var plans = new GatewayPluginExecutionPlanProvider(
            new FakePublisher(snapshot),
            NullLogger<GatewayPluginExecutionPlanProvider>.Instance);

        var next = new RecordingNext();
        var middleware = new WafMiddleware(
            next.InvokeAsync,
            NullLogger<WafMiddleware>.Instance,
            Options.Create(new GatewayMiddlewareOptions()),
            new UnusedPluginManager(),
            plans);

        var context = new DefaultHttpContext();
        context.Features.Set<IReverseProxyFeature>(new FakeReverseProxyFeature(RouteId));
        context.Response.Body = new MemoryStream();

        return (middleware, next, context);
    }

    /// <summary>
    /// A fresh context for the same middleware. Throttle counters live on the middleware, so the
    /// only way to observe a quota is to replay several requests through one instance.
    /// </summary>
    private static HttpContext NewContext(string path = "/")
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IReverseProxyFeature>(new FakeReverseProxyFeature(RouteId));
        context.Response.Body = new MemoryStream();
        context.Request.Path = path;
        context.Request.Method = "GET";
        return context;
    }

    private static void SetBody(HttpContext context, string body, string contentType, string? path = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Request.ContentType = contentType;
        if (path is not null) context.Request.Path = path;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private sealed class RecordingNext
    {
        public bool Called { get; private set; }
        public string? ObservedBody { get; set; }
        public string? ObservedTag { get; private set; }
        public Func<HttpContext, Task>? OnInvoke { get; set; }

        public async Task InvokeAsync(HttpContext context)
        {
            Called = true;
            ObservedTag = context.Request.Headers[WafMiddleware.TagHeader].ToString();
            if (OnInvoke is not null) await OnInvoke(context);
        }
    }

    private sealed class FakePublisher(GatewaySnapshot snapshot) : IGatewaySnapshotPublisher
    {
        public GatewaySnapshot Current { get; } = snapshot;

        public GatewaySnapshot Publish(GatewaySnapshot value) => Current;
    }

    private sealed class UnusedPluginManager : Aneiang.Yarp.Plugins.IGatewayPluginManager
    {
        public IReadOnlyList<Aneiang.Yarp.Plugins.PluginManifest> GetAllManifests() => [];

        public Aneiang.Yarp.Plugins.PluginManifest? GetManifest(string pluginId) => null;

        public IReadOnlyList<Aneiang.Yarp.Plugins.IGatewayPlugin> GetAllPlugins() => [];

        public Aneiang.Yarp.Plugins.IGatewayPlugin? GetPlugin(string pluginId) => null;

        public bool IsPluginEnabled(string pluginId) => true;

        public Aneiang.Yarp.Plugins.PluginStateChangeResult ValidatePluginStateChange(string pluginId, bool enabled)
            => Aneiang.Yarp.Plugins.PluginStateChangeResult.Success;

        public Aneiang.Yarp.Plugins.PluginStateChangeResult SetPluginEnabled(string pluginId, bool enabled)
            => Aneiang.Yarp.Plugins.PluginStateChangeResult.Success;

        public IReadOnlyList<Aneiang.Yarp.Plugins.PluginRuntimeState> GetPluginStates() => [];

        public void SaveState() { }

        public Aneiang.Yarp.Plugins.PluginStateChangeResult InstallPlugin(string sourceDirectory)
            => Aneiang.Yarp.Plugins.PluginStateChangeResult.Success;

        public Aneiang.Yarp.Plugins.PluginStateChangeResult UninstallPlugin(string pluginId)
            => Aneiang.Yarp.Plugins.PluginStateChangeResult.Success;

        public Aneiang.Yarp.Plugins.PluginStateChangeResult UpgradePlugin(string pluginId, string sourceDirectory)
            => Aneiang.Yarp.Plugins.PluginStateChangeResult.Success;
    }

    #endregion
}
