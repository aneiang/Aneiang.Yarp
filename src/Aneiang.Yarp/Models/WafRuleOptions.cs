namespace Aneiang.Yarp.Models;

// ──────────────── Declarative WAF rules ────────────────
//
// None of these types use JsonUnmappedMemberHandling.Disallow on purpose. An unknown
// member used to make the whole binding fail to deserialize, which silently turned the
// firewall off for that route — a typo in one field must not disable protection for
// everything else. Unknown members are ignored, and the compiler reports what it could
// not understand through warnings instead.

/// <summary>
/// One step in a WAF rule pipeline. A rule either delegates detection to a built-in
/// detector (<see cref="Builtin"/>) or describes its own conditions (<see cref="Conditions"/>),
/// and states what to do when it fires (<see cref="Action"/>).
/// </summary>
/// <remarks>
/// <see cref="Builtin"/> and <see cref="Conditions"/> combine: conditions scope where the
/// detector runs, so "sql-injection only on /api/*" is expressed by putting both on one rule.
/// </remarks>
public sealed class WafRuleOptions
{
    /// <summary>Stable identifier, reported in logs and in the <c>X-Waf-Rule</c> response header.</summary>
    public string? Id { get; set; }

    /// <summary>Human-readable name, for dashboards and logs.</summary>
    public string? Name { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Built-in detector: <c>sql-injection</c>, <c>xss</c>, <c>path-traversal</c>.</summary>
    public string? Builtin { get; set; }

    /// <summary>How conditions combine: <c>all</c> (default) or <c>any</c>.</summary>
    public string? MatchMode { get; set; }

    /// <summary>Conditions evaluated before the rule fires. Requires at least one when there is no built-in detector.</summary>
    public List<WafConditionOptions>? Conditions { get; set; }

    /// <summary>
    /// What to do on a match: <c>block</c> (default), <c>allow</c>, <c>log</c>, <c>tag</c>,
    /// <c>rate-limit</c>.
    /// </summary>
    public string? Action { get; set; }

    /// <summary>Tag value for <c>action: tag</c>.</summary>
    public string? Tag { get; set; }

    /// <summary>Overrides the policy-level status code for <c>action: block</c> and <c>action: rate-limit</c>.</summary>
    public int? StatusCode { get; set; }

    /// <summary>Quota for <c>action: rate-limit</c>. Required by that action, ignored by every other.</summary>
    public WafRateLimitOptions? RateLimit { get; set; }
}

/// <summary>
/// The quota behind <c>action: rate-limit</c>.
/// </summary>
/// <remarks>
/// This is a throttle, not traffic shaping: the point is to cap how fast someone may <em>retry</em>
/// a rule that keeps matching, so a login endpoint can return 429 after five bad attempts instead
/// of feeding a password spray. It deliberately has no queue — queueing an attacker's request
/// only converts their flood into our latency. The duration syntax matches the rate-limit plugin
/// (<c>30s</c>, <c>1m</c>, <c>1h</c>) so operators do not have to learn a second dialect.
/// </remarks>
public sealed class WafRateLimitOptions
{
    /// <summary>Requests allowed per window, per partition key. Zero or less disables the action.</summary>
    public int Limit { get; set; } = 60;

    /// <summary>Window length: <c>30s</c>, <c>1m</c>, <c>1h</c>. Fixed window, not sliding.</summary>
    public string? Window { get; set; }

    /// <summary>
    /// What the quota is counted per: <c>ip</c> (default), <c>path</c>, <c>ip-path</c>, <c>global</c>.
    /// <c>ip-path</c> is the one that stops a spray across many endpoints while leaving a single
    /// endpoint usable, which is the common case for login-style rules.
    /// </summary>
    public string? Partition { get; set; }
}

/// <summary>
/// A single comparison against one part of the request. One shape covers every source
/// instead of a field per combination, which is what keeps the configuration readable
/// as the rule set grows.
/// </summary>
public sealed class WafConditionOptions
{
    /// <summary>
    /// Where to look: <c>method</c>, <c>path</c>, <c>query</c>, <c>body</c>, <c>header</c>,
    /// <c>cookie</c>, <c>userAgent</c>, <c>ip</c>, <c>contentType</c>, <c>bodySize</c>.
    /// </summary>
    public string Source { get; set; } = "path";

    /// <summary>
    /// How to compare: <c>equals</c>, <c>contains</c>, <c>prefix</c>, <c>suffix</c>, <c>regex</c>,
    /// <c>cidr</c>, <c>greaterThan</c>, <c>lessThan</c>.
    /// </summary>
    public string Op { get; set; } = "contains";

    /// <summary>Header or cookie name, for those two sources.</summary>
    public string? Name { get; set; }

    /// <summary>Expected value; for <c>cidr</c> it is the CIDR range, for numeric ops a number.</summary>
    public string? Value { get; set; }

    /// <summary>Case-insensitive comparison for the string operators. Ignored by <c>regex</c>, which carries its own options.</summary>
    public bool IgnoreCase { get; set; } = true;
}

/// <summary>
/// Which request bodies the detectors are allowed to read.
/// Bodies are only read when they can be read whole: a prefix scan of a large upload
/// costs the same as a full buffer and finds almost nothing the tail would hide.
/// </summary>
public sealed class WafBodyInspectionOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Upper bound on the body size that will be inspected. Larger bodies are skipped, not truncated.</summary>
    public int MaxBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Content types eligible for inspection, matched as prefixes (so <c>text/</c> covers every text type).
    /// Defaults deliberately exclude <c>multipart/</c>: a multipart envelope is transport metadata,
    /// not payload, and scanning it produces false positives without adding coverage.
    /// </summary>
    public List<string> ContentTypes { get; set; } =
    [
        "text/",
        "application/json",
        "application/x-www-form-urlencoded",
        "application/xml",
        "application/graphql"
    ];

    /// <summary>Inspect requests with no <c>Content-Type</c> at all. Off by default: the format is unknown, so nothing can be validated.</summary>
    public bool InspectUnknownContentType { get; set; }
}

/// <summary>What a blocked request looks like to the caller.</summary>
public sealed class WafBlockResponseOptions
{
    public int StatusCode { get; set; } = 403;

    public string ContentType { get; set; } = "application/json; charset=utf-8";

    /// <summary>Custom body. Left empty, a small JSON document describing the block is returned.</summary>
    public string? Body { get; set; }

    /// <summary>Extra response headers, applied to every blocked response.</summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// Emit <c>X-Waf-Rule</c> naming the rule that fired. On by default: without it an operator
    /// looking at a 403 cannot tell which rule produced it, and the first question is always that one.
    /// </summary>
    public bool IncludeRuleHeader { get; set; } = true;
}
