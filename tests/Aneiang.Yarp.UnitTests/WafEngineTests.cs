using System.Net;
using System.Text.Json;
using Aneiang.Yarp.Models;
using Aneiang.Yarp.Plugin.Waf;
using Microsoft.AspNetCore.Http;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Covers the two halves of the redesign that can go wrong independently: what the detectors
/// consider an attack, and what the compiler makes of a rule set.
/// </summary>
public class WafEngineTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static WafBindingOptions Options(string json) =>
        JsonSerializer.Deserialize<WafBindingOptions>(json, Json)!;

    private static WafRequestView View(
        string query = "",
        string? body = null,
        string path = "/api/items",
        string? rawPath = null,
        string method = "POST",
        string? contentType = "application/json",
        string? ip = "127.0.0.1")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        return new WafRequestView
        {
            Context = context,
            Method = method,
            RawPath = rawPath ?? path,
            DecodedPath = WebUtility.UrlDecode(path),
            DecodedQuery = query,
            ClientIp = ip,
            ContentType = contentType,
            BodySize = body?.Length ?? 0,
            BodyText = body
        };
    }

    // ──────────────── Detector regressions ────────────────
    // Every case in this region was either reported against the previous implementation or
    // is the reason it was reported.

    public static TheoryData<string> InnocentPayloads => new()
    {
        // A multipart envelope is all `------` boundaries. This is the reported bug: it made
        // every file upload to a WAF route return 403.
        "------WebKitFormBoundary7MA4YWxkTrZu0gW",
        "Content-Disposition: form-data; name=\"file\"; filename=\"a.txt\"\r\n------WebKitFormBoundary7MA4YWxkTrZu0gW--\r\n",
        // Binary uploads, which used to be decoded as UTF-8 and pattern-matched.
        "%PDF-1.4\n%\u00e2\u00e3\u00cf\u00d3\n1 0 obj\n<< /Type /Catalog >>\nendobj",
        // SQL keywords used as English.
        "please select a size from the list",
        "{\"query\":\"select the plan\",\"note\":\"update later\"}",
        // CLI-style flags in a query string.
        "q=--help",
        "args=--verbose&mode=--dry-run",
        // Em dashes typed as two hyphens, and comment-looking prose.
        "well -- known",
        "// this is fine",
        "/* not an injection */",
        // A JSON body containing an apostrophe, which the old `' OR` rule could trip on.
        "{\"promo\":\"it's a deal\"}",
    };

    [Theory]
    [MemberData(nameof(InnocentPayloads))]
    public void Ordinary_traffic_is_not_flagged_as_sql_injection(string payload)
    {
        var view = View(query: payload, body: payload);

        Assert.False(WafDetectors.ScanSqlInjection(view).Matched);
    }

    public static TheoryData<string> AttackPayloads => new()
    {
        "' OR '1'='1",
        "1' OR '1'='1' --",
        "admin'--",
        "UNION SELECT username, password FROM users",
        "; DROP TABLE users",
        "SELECT * FROM users WHERE id = 1",
        "INSERT INTO users (name) VALUES ('x')",
        "1; WAITFOR DELAY '0:0:5'",
        "x' AND (SELECT 1 FROM information_schema.tables) --",
    };

    [Theory]
    [MemberData(nameof(AttackPayloads))]
    public void Real_injection_is_still_flagged(string payload)
    {
        var view = View(query: "q=" + payload);

        var detection = WafDetectors.ScanSqlInjection(view);

        Assert.True(detection.Matched, payload);
        Assert.Equal(WafDetectors.SqlInjectionEvent, detection.EventType);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<SCRIPT src=//x.js>")]
    [InlineData("<iframe src=//evil>")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("<img src=x onerror=alert(1)>")]
    public void Real_xss_is_still_flagged(string payload)
    {
        Assert.True(WafDetectors.ScanXss(View(query: "q=" + payload)).Matched, payload);
    }

    [Theory]
    [InlineData("onboarding=true")]
    [InlineData("{\"onlyOne\":true}")]
    [InlineData("position=3")]
    public void Attribute_lookalikes_are_not_xss(string payload)
    {
        Assert.False(WafDetectors.ScanXss(View(query: payload)).Matched, payload);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..%2f..%2fetc%2fpasswd")]
    [InlineData("%2e%2e%2f%2e%2e%2fetc")]
    public void Path_traversal_is_still_flagged(string payload)
    {
        var view = View(query: "", path: "/files/" + payload, rawPath: "/files/" + payload);

        Assert.True(WafDetectors.ScanPathTraversal(view).Matched, payload);
    }

    [Theory]
    [InlineData("/files/report..final.pdf")]
    [InlineData("/files/a..b")]
    [InlineData("/v1.0/items")]
    public void Dots_inside_a_file_name_are_not_traversal(string path)
    {
        Assert.False(WafDetectors.ScanPathTraversal(View(path: path)).Matched, path);
    }

    // ──────────────── Compiler ────────────────

    [Fact]
    public void Legacy_switches_synthesise_the_classic_pipeline_in_a_stable_order()
    {
        var policy = WafPolicyCompiler.Compile(Options("{}"));

        Assert.Equal(
            ["path-traversal", "sql-injection", "xss"],
            policy.Rules.Select(rule => rule.Id));
        Assert.All(policy.Rules, rule => Assert.Equal(WafAction.Block, rule.Action));
        Assert.True(policy.Enforce);
    }

    [Fact]
    public void Disabling_the_legacy_switches_leaves_nothing_to_evaluate()
    {
        var policy = WafPolicyCompiler.Compile(Options("""
            {"enableSqlInjectionDetection":false,"enableXssDetection":false,"enablePathTraversalDetection":false,
             "enableIpCheck":false,"enableRequestSizeValidation":false}
            """));

        Assert.Empty(policy.Rules);
        Assert.True(policy.IsNoOp);
        Assert.False(policy.RequiresBody);
    }

    [Fact]
    public void An_explicit_rule_list_replaces_the_legacy_switches()
    {
        var policy = WafPolicyCompiler.Compile(Options("""
            {"rules":[{"id":"only-ip","conditions":[{"source":"ip","op":"cidr","value":"10.0.0.0/8"}],"action":"block"}]}
            """));

        Assert.Single(policy.Rules);
        Assert.Equal(WafBuiltin.None, policy.Rules[0].Builtin);
    }

    [Fact]
    public void Only_rules_that_read_the_body_make_the_body_worth_reading()
    {
        var sql = WafPolicyCompiler.Compile(Options("""{"rules":[{"builtin":"sql-injection"}]}"""));
        var traversal = WafPolicyCompiler.Compile(Options("""{"rules":[{"builtin":"path-traversal"}]}"""));
        var headerOnly = WafPolicyCompiler.Compile(Options("""
            {"rules":[{"conditions":[{"source":"header","name":"X-Key","value":"v"}],"action":"block"}]}
            """));

        Assert.True(sql.RequiresBody);
        Assert.False(traversal.RequiresBody);
        Assert.False(headerOnly.RequiresBody);
    }

    [Fact]
    public void Monitor_mode_compiles_the_same_rules_but_does_not_enforce()
    {
        var policy = WafPolicyCompiler.Compile(Options("""{"mode":"monitor"}"""));

        Assert.False(policy.Enforce);
        Assert.Equal(3, policy.Rules.Count);
    }

    [Fact]
    public void An_unknown_mode_falls_back_to_enforcing_and_says_so()
    {
        var policy = WafPolicyCompiler.Compile(Options("""{"mode":"passthrough"}"""));

        Assert.True(policy.Enforce);
        Assert.Contains(policy.Warnings, warning => warning.Contains("passthrough"));
    }

    [Fact]
    public void A_rule_with_neither_detector_nor_conditions_is_dropped_not_applied_to_everything()
    {
        var policy = WafPolicyCompiler.Compile(Options("""
            {"rules":[{"id":"oops","action":"block"}],"enableSqlInjectionDetection":false,
             "enableXssDetection":false,"enablePathTraversalDetection":false}
            """));

        Assert.Empty(policy.Rules);
        Assert.Contains(policy.Warnings, warning => warning.Contains("oops"));
    }

    [Fact]
    public void A_broken_regex_disables_that_rule_only()
    {
        var policy = WafPolicyCompiler.Compile(Options("""
            {"rules":[
              {"id":"broken","conditions":[{"source":"query","op":"regex","value":"([a-"}],"action":"block"},
              {"id":"fine","conditions":[{"source":"path","op":"prefix","value":"/admin"}],"action":"block"}
            ]}
            """));

        Assert.Equal(["fine"], policy.Rules.Select(rule => rule.Id));
        Assert.Contains(policy.Warnings, warning => warning.Contains("broken"));
    }

    [Fact]
    public void A_tag_rule_without_a_tag_is_dropped()
    {
        var policy = WafPolicyCompiler.Compile(Options("""
            {"rules":[{"id":"t","action":"tag","conditions":[{"source":"path","op":"prefix","value":"/a"}]}]}
            """));

        Assert.Empty(policy.Rules);
        Assert.Contains(policy.Warnings, warning => warning.Contains("tag"));
    }

    [Fact]
    public void Out_of_range_status_codes_are_reported_and_ignored()
    {
        var policy = WafPolicyCompiler.Compile(Options("""
            {"response":{"statusCode":200},"rules":[{"id":"r","action":"block","statusCode":302,
             "conditions":[{"source":"path","op":"prefix","value":"/a"}]}]}
            """));

        Assert.Equal(403, policy.Response.StatusCode);
        Assert.Null(policy.Rules[0].StatusCode);
        Assert.Equal(2, policy.Warnings.Count);
    }

    [Fact]
    public void Body_scanning_is_clamped_to_the_hard_ceiling()
    {
        var policy = WafPolicyCompiler.Compile(Options("""{"bodyInspection":{"maxBytes":99999999}}"""));

        Assert.Equal(WafPolicyCompiler.HardMaxScanBytes, policy.Body.MaxBytes);
        Assert.Contains(policy.Warnings, warning => warning.Contains("maxBytes"));
    }

    [Fact]
    public void Zero_scan_budget_disables_body_inspection_instead_of_reading_nothing()
    {
        var policy = WafPolicyCompiler.Compile(Options("""{"bodyInspection":{"maxBytes":0}}"""));

        Assert.False(policy.Body.Enabled);
    }

    [Fact]
    public void Duplicate_rule_ids_are_reported_because_logs_cannot_tell_them_apart()
    {
        var policy = WafPolicyCompiler.Compile(Options("""
            {"rules":[
              {"id":"same","conditions":[{"source":"path","op":"prefix","value":"/a"}],"action":"block"},
              {"id":"same","conditions":[{"source":"path","op":"prefix","value":"/b"}],"action":"block"}
            ]}
            """));

        Assert.Equal(2, policy.Rules.Count);
        Assert.Contains(policy.Warnings, warning => warning.Contains("same"));
    }

    // ──────────────── Conditions ────────────────

    [Fact]
    public void Conditions_scope_a_detector_instead_of_replacing_it()
    {
        var policy = WafPolicyCompiler.Compile(Options("""
            {"rules":[{"id":"sql-on-api","builtin":"sql-injection",
                       "conditions":[{"source":"path","op":"prefix","value":"/api/"}]}]}
            """));

        var rule = policy.Rules[0];

        Assert.False(rule.Matches(View(query: "q=' OR '1'='1", path: "/public/x"), out _, out _));
        Assert.True(rule.Matches(View(query: "q=' OR '1'='1", path: "/api/x"), out var eventType, out _));
        Assert.Equal(WafDetectors.SqlInjectionEvent, eventType);
    }

    [Fact]
    public void Match_mode_any_accepts_a_single_condition()
    {
        var policy = WafPolicyCompiler.Compile(Options("""
            {"rules":[{
              "id":"scanner","matchMode":"any","action":"block",
              "conditions":[
                {"source":"userAgent","op":"contains","value":"sqlmap"},
                {"source":"path","op":"prefix","value":"/admin"}
              ]}]}
            """));

        var rule = policy.Rules[0];

        var byUserAgent = View(method: "GET", path: "/public/x");
        byUserAgent.Context.Request.Headers.UserAgent = "sqlmap/1.7";
        Assert.True(rule.Matches(byUserAgent, out _, out _));

        var byPath = View(method: "GET", path: "/admin/x");
        Assert.True(rule.Matches(byPath, out _, out _));

        Assert.False(rule.Matches(View(method: "GET", path: "/public/x"), out _, out _));
    }

    [Fact]
    public void A_header_condition_matches_the_named_header_only()
    {
        var policy = WafPolicyCompiler.Compile(Options("""
            {"rules":[{"id":"partner","action":"allow",
                       "conditions":[{"source":"header","name":"X-Partner","op":"equals","value":"acme"}]}]}
            """));

        var rule = policy.Rules[0];
        var view = View(method: "GET");
        view.Context.Request.Headers["X-Partner"] = "acme";

        Assert.True(rule.Matches(view, out _, out _));

        view.Context.Request.Headers["X-Partner"] = "other";
        Assert.False(rule.Matches(view, out _, out _));
    }

    [Fact]
    public void Body_size_conditions_compare_numbers_not_strings()
    {
        var policy = WafPolicyCompiler.Compile(Options("""
            {"rules":[{"id":"big","action":"block",
                       "conditions":[{"source":"bodySize","op":"greaterThan","value":"9"}]}]}
            """));

        var rule = policy.Rules[0];

        Assert.False(rule.Matches(View(body: "123456789"), out _, out _));   // 9 bytes, not > 9
        Assert.True(rule.Matches(View(body: "1234567890"), out _, out _));   // 10 bytes
    }

    [Fact]
    public void An_ip_condition_accepts_cidr_and_exact_addresses()
    {
        var policy = WafPolicyCompiler.Compile(Options("""
            {"rules":[{"id":"internal","action":"allow",
                       "conditions":[{"source":"ip","op":"cidr","value":"10.0.0.0/8"}]}]}
            """));

        var rule = policy.Rules[0];

        Assert.True(rule.Matches(View(ip: "10.4.5.6"), out _, out _));
        Assert.False(rule.Matches(View(ip: "192.168.1.1"), out _, out _));
    }

    [Fact]
    public void A_body_condition_never_matches_when_the_body_was_not_inspected()
    {
        var policy = WafPolicyCompiler.Compile(Options("""
            {"rules":[{"id":"b","action":"block",
                       "conditions":[{"source":"body","op":"contains","value":"x"}]}]}
            """));

        // BodyText null models "not inspected", which must not be read as "empty and therefore fine".
        Assert.False(policy.Rules[0].Matches(View(body: null), out _, out _));
    }

    // ──────────────── Preamble ────────────────

    [Fact]
    public void Duplicate_header_lines_are_legal_and_do_not_trip_the_header_limit()
    {
        var policy = WafPolicyCompiler.Compile(Options("""{"maxHeaderCount":64,"maxHeaderSize":8192}"""));
        var view = View(method: "GET");
        view.Context.Request.Headers.Append("Accept", "text/html");
        view.Context.Request.Headers.Append("Accept", "application/json");

        Assert.False(policy.Preamble.Evaluate(view).Blocked);
    }

    [Fact]
    public void An_oversized_header_value_is_still_rejected()
    {
        var policy = WafPolicyCompiler.Compile(Options("""{"maxHeaderSize":16}"""));
        var view = View(method: "GET");
        view.Context.Request.Headers["X-Long"] = new string('a', 64);

        var result = policy.Preamble.Evaluate(view);

        Assert.True(result.Blocked);
        Assert.Equal("MalformedHeadersBlocked", result.EventType);
    }

    [Fact]
    public void A_whitelist_that_does_not_contain_the_caller_blocks()
    {
        var policy = WafPolicyCompiler.Compile(Options("""{"ipWhitelist":["10.0.0.0/8"]}"""));

        Assert.True(policy.Preamble.Evaluate(View(ip: "203.0.113.9")).Blocked);
        Assert.False(policy.Preamble.Evaluate(View(ip: "10.9.9.9")).Blocked);
    }

    // ──────────────── rate-limit action ────────────────

    [Fact]
    public void A_throttle_compiles_its_quota()
    {
        var policy = WafPolicyCompiler.Compile(Options("""
            {"rules":[
              {"id":"login-throttle","action":"rate-limit",
               "conditions":[{"source":"path","op":"prefix","value":"/login"}],
               "rateLimit":{"limit":5,"window":"30s","partition":"ip-path"}}]}
            """));

        var rule = Assert.Single(policy.Rules);
        Assert.Equal(WafAction.RateLimit, rule.Action);

        Assert.True(rule.RateLimit.HasValue);
        var quota = rule.RateLimit.GetValueOrDefault();
        Assert.Equal(5, quota.Limit);
        Assert.Equal(TimeSpan.FromSeconds(30), quota.Window);
        Assert.Equal(WafPartition.IpPath, quota.Partition);
    }

    [Fact]
    public void A_throttle_without_a_quota_is_dropped_rather_than_given_a_default()
    {
        // A throttle nobody configured must not quietly become "60 per minute": it would look
        // like it was working, which is worse than being absent.
        var policy = WafPolicyCompiler.Compile(Options("""
            {"rules":[
              {"id":"login-throttle","action":"rate-limit",
               "conditions":[{"source":"path","op":"prefix","value":"/login"}]}]}
            """));

        Assert.Empty(policy.Rules);
        Assert.Contains(policy.Warnings, warning => warning.Contains("rateLimit"));
    }

    [Fact]
    public void A_blanket_throttle_survives_where_a_blanket_block_would_not()
    {
        var block = WafPolicyCompiler.Compile(Options("""{"rules":[{"action":"block"}]}"""));
        var throttle = WafPolicyCompiler.Compile(Options("""
            {"rules":[{"action":"rate-limit","rateLimit":{"limit":10,"window":"1m"}}]}
            """));

        Assert.Empty(block.Rules);
        Assert.Single(throttle.Rules);
    }

    [Fact]
    public void An_unreadable_window_falls_back_to_one_minute_with_a_warning()
    {
        var policy = WafPolicyCompiler.Compile(Options("""
            {"rules":[{"action":"rate-limit","rateLimit":{"limit":3,"window":"300"}}]}
            """));

        // A bare number is minutes in the rate-limit plugin. Here it is rejected on purpose:
        // "300" silently meaning five hours on a login throttle is a lockout, not a limit.
        Assert.Equal(TimeSpan.FromMinutes(1), policy.Rules[0].RateLimit!.Value.Window);
        Assert.Contains(policy.Warnings, warning => warning.Contains("window"));
    }

    [Fact]
    public void An_unknown_partition_falls_back_to_ip_with_a_warning()
    {
        var policy = WafPolicyCompiler.Compile(Options("""
            {"rules":[{"action":"rate-limit","rateLimit":{"limit":3,"window":"1m","partition":"tenant"}}]}
            """));

        Assert.Equal(WafPartition.Ip, policy.Rules[0].RateLimit!.Value.Partition);
        Assert.Contains(policy.Warnings, warning => warning.Contains("partition"));
    }

    [Fact]
    public void A_zero_or_negative_limit_is_refused()
    {
        foreach (var limit in new[] { 0, -1 })
        {
            var policy = WafPolicyCompiler.Compile(Options(
                """{"rules":[{"action":"rate-limit","rateLimit":{"limit":""" + limit + """}}]}"""));

            Assert.Empty(policy.Rules);
        }
    }

    [Fact]
    public void The_throttle_allows_the_quota_then_rejects_with_a_retry_after()
    {
        var throttle = new WafThrottle();
        var quota = new WafQuota(3, TimeSpan.FromHours(1), WafPartition.Ip);

        Assert.True(throttle.TryAcquire("route\u001frule\u001f1.2.3.4", quota, out _));
        Assert.True(throttle.TryAcquire("route\u001frule\u001f1.2.3.4", quota, out _));
        Assert.True(throttle.TryAcquire("route\u001frule\u001f1.2.3.4", quota, out _));

        Assert.False(throttle.TryAcquire("route\u001frule\u001f1.2.3.4", quota, out var retryAfter));
        Assert.InRange(retryAfter, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1));
    }

    [Fact]
    public void A_rejected_request_does_not_extend_the_window()
    {
        var throttle = new WafThrottle();
        var quota = new WafQuota(1, TimeSpan.FromHours(1), WafPartition.Ip);
        const string key = "k";

        Assert.True(throttle.TryAcquire(key, quota, out _));

        Assert.False(throttle.TryAcquire(key, quota, out var first));
        Thread.Sleep(20);
        Assert.False(throttle.TryAcquire(key, quota, out var second));

        // If rejects counted, the window would restart on every attempt and the client would
        // never be let back in — a throttle that behaves like a permanent ban.
        Assert.True(second < first);
    }

    [Fact]
    public void Separate_buckets_do_not_share_a_quota()
    {
        var throttle = new WafThrottle();
        var quota = new WafQuota(1, TimeSpan.FromHours(1), WafPartition.Ip);

        Assert.True(throttle.TryAcquire("a", quota, out _));
        Assert.False(throttle.TryAcquire("a", quota, out _));
        Assert.True(throttle.TryAcquire("b", quota, out _));

        Assert.Equal(2, throttle.Count);
    }
}
