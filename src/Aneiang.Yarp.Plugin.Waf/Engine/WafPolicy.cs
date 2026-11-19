using System.Globalization;
using System.Text.RegularExpressions;
using Aneiang.Yarp.Models;
using Aneiang.Yarp.Plugin.Waf.Helpers;

namespace Aneiang.Yarp.Plugin.Waf;

/// <summary>
/// A binding configuration compiled into something the request path can execute without
/// parsing, allocating or reflecting. Built once per configuration version, not per request.
/// </summary>
public sealed class WafPolicy
{
    public required bool Enforce { get; init; }
    public required WafPreamble Preamble { get; init; }
    public required IReadOnlyList<WafCompiledRule> Rules { get; init; }
    public required WafBodyInspectionOptions Body { get; init; }
    public required WafBlockResponseOptions Response { get; init; }

    /// <summary>True when at least one rule reads the body, so it is worth reading at all.</summary>
    public required bool RequiresBody { get; init; }

    /// <summary>Configuration problems found while compiling. Logged once per configuration version.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    public bool IsNoOp => Rules.Count == 0 && !Preamble.Active;
}

/// <summary>
/// Checks that run before any rule and cannot be waived by one: where the request came from
/// and whether its shape is acceptable at all. A content rule that says "allow" must not be able
/// to override an IP ban, so these sit outside the pipeline.
/// </summary>
public sealed class WafPreamble
{
    public bool IpCheck { get; init; }
    public string[] IpWhitelist { get; init; } = [];
    public string[] IpBlacklist { get; init; } = [];
    public bool SizeValidation { get; init; }
    public long MaxRequestBodySize { get; init; }
    public int MaxHeaderCount { get; init; }
    public int MaxHeaderSize { get; init; }
    public int MaxUrlLength { get; init; }

    public bool Active => IpCheck || SizeValidation;

    public WafPreambleResult Evaluate(WafRequestView view)
    {
        if (IpCheck && !string.IsNullOrEmpty(view.ClientIp))
        {
            if (IpWhitelist.Length > 0 && !IpWhitelist.Any(pattern => IpMatcher.Matches(pattern, view.ClientIp)))
                return new WafPreambleResult(true, "IpBlocked", $"{view.ClientIp} not in whitelist");

            if (IpBlacklist.Length > 0 && IpBlacklist.Any(pattern => IpMatcher.Matches(pattern, view.ClientIp)))
                return new WafPreambleResult(true, "IpBlocked", $"{view.ClientIp} in blacklist");
        }

        if (!SizeValidation) return WafPreambleResult.Passed;

        if (view.BodySize > MaxRequestBodySize)
            return new WafPreambleResult(true, "RequestSizeBlocked", $"body {view.BodySize} > {MaxRequestBodySize}");

        var headers = view.Context.Request.Headers;
        if (headers.Count > MaxHeaderCount)
            return new WafPreambleResult(true, "MalformedHeadersBlocked", $"{headers.Count} headers > {MaxHeaderCount}");

        foreach (var header in headers)
        {
            // Duplicate header lines are legal (Accept, Cookie, X-Forwarded-For all arrive that way),
            // so what is limited is the size of each value, not how many values the client sent.
            foreach (var value in header.Value)
            {
                if (value is { Length: > 0 } && value.Length > MaxHeaderSize)
                    return new WafPreambleResult(true, "MalformedHeadersBlocked", $"{header.Key} value {value.Length} > {MaxHeaderSize}");
            }
        }

        if ((view.DecodedPath.Length > MaxUrlLength))
            return new WafPreambleResult(true, "UriTooLongBlocked", $"path {view.DecodedPath.Length} > {MaxUrlLength}");

        return WafPreambleResult.Passed;
    }
}

public readonly record struct WafPreambleResult(bool Blocked, string EventType, string? Details)
{
    public static WafPreambleResult Passed => default;
}

/// <summary>One compiled rule: an optional built-in detector, an optional set of conditions scoping it, and an action.</summary>
public sealed class WafCompiledRule
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public required WafAction Action { get; init; }
    public required WafBuiltin Builtin { get; init; }
    public string? Tag { get; init; }
    public int? StatusCode { get; init; }

    /// <summary>Quota for <see cref="WafAction.RateLimit"/>; null for every other action.</summary>
    public WafQuota? RateLimit { get; init; }

    public required bool MatchAll { get; init; }
    public required IReadOnlyList<WafCompiledCondition> Conditions { get; init; }

    /// <summary>True when evaluating this rule needs the request body, so the middleware knows whether to read it.</summary>
    public bool RequiresBody =>
        Builtin is WafBuiltin.SqlInjection or WafBuiltin.Xss ||
        Conditions.Any(condition => condition.Source is WafSource.Body or WafSource.BodySize);

    /// <summary>
    /// Conditions scope the detector: both must agree. With no built-in detector the conditions
    /// are the rule, which is how custom rules ("block this user agent on this path") are written.
    /// </summary>
    public bool Matches(WafRequestView view, out string eventType, out string? details)
    {
        details = null;

        if (!ConditionsSatisfied(view))
        {
            eventType = "";
            return false;
        }

        if (Builtin == WafBuiltin.None)
        {
            eventType = "RuleBlocked";
            details = Conditions.Count == 0 ? null : $"matched {Conditions.Count} condition(s)";
            return true;
        }

        var detection = Builtin switch
        {
            WafBuiltin.SqlInjection => WafDetectors.ScanSqlInjection(view),
            WafBuiltin.Xss => WafDetectors.ScanXss(view),
            WafBuiltin.PathTraversal => WafDetectors.ScanPathTraversal(view),
            _ => WafDetection.None
        };

        eventType = detection.EventType ?? "";
        details = detection.Details;
        return detection.Matched;
    }

    private bool ConditionsSatisfied(WafRequestView view)
    {
        if (Conditions.Count == 0) return true;
        return MatchAll
            ? Conditions.All(condition => condition.Matches(view))
            : Conditions.Any(condition => condition.Matches(view));
    }
}

/// <summary>One compiled comparison against one part of the request.</summary>
public sealed class WafCompiledCondition
{
    public required WafSource Source { get; init; }
    public required WafOperator Operator { get; init; }
    public string? Name { get; init; }
    public required string Value { get; init; }
    public required bool IgnoreCase { get; init; }
    public Regex? Pattern { get; init; }

    public bool Matches(WafRequestView view)
    {
        var actual = Read(view);
        if (actual is null) return false;

        var comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        switch (Operator)
        {
            case WafOperator.Equals:
                return string.Equals(actual, Value, comparison);
            case WafOperator.Contains:
                return actual.Contains(Value, comparison);
            case WafOperator.Prefix:
                return actual.StartsWith(Value, comparison);
            case WafOperator.Suffix:
                return actual.EndsWith(Value, comparison);
            case WafOperator.Cidr:
                return IpMatcher.Matches(Value, actual);
            case WafOperator.GreaterThan:
            case WafOperator.LessThan:
                if (!double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var actualNumber) ||
                    !double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var expected))
                    return false;
                return Operator == WafOperator.GreaterThan ? actualNumber > expected : actualNumber < expected;
            case WafOperator.Regex:
                return MatchesRegex(actual);
            default:
                return false;
        }
    }

    /// <summary>
    /// A timed-out condition counts as no match. These patterns are operator-authored routing
    /// logic, not the security boundary itself; treating a timeout as a hit would let a badly
    /// written pattern start rejecting traffic, which is a worse failure than skipping it.
    /// </summary>
    private bool MatchesRegex(string actual)
    {
        if (Pattern is null) return false;
        try
        {
            return Pattern.IsMatch(actual);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Returns <c>null</c> when the source cannot be read, which never satisfies a condition.</summary>
    private string? Read(WafRequestView view) => Source switch
    {
        WafSource.Method => view.Method,
        WafSource.Path => view.DecodedPath,
        WafSource.Query => view.DecodedQuery,
        WafSource.Body => view.BodyText,
        WafSource.Header => string.IsNullOrEmpty(Name) ? null : view.Header(Name),
        WafSource.Cookie => string.IsNullOrEmpty(Name) ? null : view.Cookie(Name),
        WafSource.UserAgent => view.Header("User-Agent"),
        WafSource.Ip => view.ClientIp,
        WafSource.ContentType => view.ContentType ?? "",
        WafSource.BodySize => view.BodySize.ToString(CultureInfo.InvariantCulture),
        _ => null
    };
}
