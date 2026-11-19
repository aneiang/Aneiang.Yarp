using System.Text.RegularExpressions;

namespace Aneiang.Yarp.Plugin.Waf;

/// <summary>Outcome of a built-in detector: whether it fired, which event it maps to, and the text that triggered it.</summary>
public readonly record struct WafDetection(bool Matched, string? EventType, string? Details)
{
    public static WafDetection None => default;

    public static WafDetection Hit(string eventType, string? details) => new(true, eventType, details);
}

/// <summary>
/// The three built-in detectors. Every pattern here is written to match an <em>attack shape</em>
/// rather than a keyword, because a keyword is what a legitimate payload also contains.
/// </summary>
/// <remarks>
/// The previous implementation matched a bare <c>--</c> preceded by any non-word character, which
/// is every line of every multipart envelope; and a bare <c>SELECT</c>, which is the word in
/// "select a size". Both blocked ordinary traffic. Nothing here treats a comment marker or a SQL
/// keyword as suspicious on its own — only in a construction that has no innocent reading.
/// </remarks>
public static class WafDetectors
{
    public const string SqlInjectionEvent = "SqlInjectionBlocked";
    public const string XssEvent = "XssBlocked";
    public const string PathTraversalEvent = "PathTraversalBlocked";
    public const string TimeoutEvent = "WafRuleTimeout";

    /// <summary>
    /// A detector that gives up on pathological input is treated as a hit. A timeout means the
    /// pattern met input it could not finish on, and letting that request through would let an
    /// attacker choose the one input shape the firewall cannot evaluate. The cost is that a
    /// legitimate 64KB pathological payload is rejected — recoverable, and visible in the logs.
    /// </summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    private const int ExcerptLength = 120;

    private static readonly Regex SqlPattern = new(
        """
        (?ix)
          (?: \b union \s+ (?: all \s+ )? select \b )
        | (?: ; \s* (?: drop | truncate | delete | update | insert | alter | create | exec | execute | shutdown ) \b )
        | (?: \b select \s+ (?: \* | (?: [\w."\[\]]+ \s* , \s* )* [\w."\[\]]+ ) \s+ from \b )
        | (?: \b insert \s+ into \b )
        | (?: \b update \s+ [\w."\[\]]+ \s+ set \b )
        | (?: \b delete \s+ from \b )
        | (?: ["'] [\s\S]{0,8} \b (?: or | and ) \b [\s\S]{0,40} = )
        | (?: ["'] [\t ]{0,2} (?: -- | \x23 | /\* ) )
        | (?: \b (?: sleep | benchmark | pg_sleep | waitfor ) \b )
        | (?: \b (?: information_schema | xp_cmdshell | load_file ) \b )
        | (?: \b into \s+ outfile \b )
        """,
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex XssPattern = new(
        """
        (?ix)
          < \s* script \b
        | < \s* iframe \b
        | < \s* (?: object | embed ) \b
        | javascript \s* :
        | data \s* : \s* text/html
        | \b on (?: abort | error | load | unload | click | dblclick | mouse \w+ | focus | blur
                 | submit | change | input | key \w+ | touch \w+ | pointer \w+ | drag \w+
                 | animation \w+ | transition \w+ | toggle | show ) \s* =
        """,
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex PathTraversalPattern = new(
        @"(?i)\.\.[\\/]|%2e%2e[\\/%]|%252e%252e|(?:^|[\\/])\.\.(?:[\\/]|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    /// <summary>
    /// Builds the compiled patterns up front.
    /// </summary>
    /// <remarks>
    /// A <see cref="RegexOptions.Compiled"/> pattern is JIT-compiled on first use, and that
    /// one-off cost lands inside the per-match timeout. On a loaded machine the first match
    /// can therefore exceed <see cref="RegexTimeout"/>, and since a timeout is treated as a
    /// hit, the very first request to pass a WAF route would be rejected as an attack while
    /// every later request is fine. Paying the build cost here moves it from a false block
    /// into type initialisation, where it is merely slow.
    /// </remarks>
    static WafDetectors()
    {
        Warmup(SqlPattern);
        Warmup(XssPattern);
        Warmup(PathTraversalPattern);
    }

    private static void Warmup(Regex pattern)
    {
        try
        {
            _ = pattern.IsMatch("warmup");
        }
        catch (RegexMatchTimeoutException)
        {
            /* The build outlasted this budget, but it is now cached - the next call reuses it.
               Swallowing is required: a throwing static constructor would poison the type for
               the lifetime of the process. */
        }
    }

    /// <summary>SQL injection in the decoded query string or the inspected body.</summary>
    public static WafDetection ScanSqlInjection(WafRequestView view)    {
        var hit = Evaluate(SqlPattern, view.DecodedQuery, SqlInjectionEvent);
        return hit.Matched ? hit : Evaluate(SqlPattern, view.BodyText, SqlInjectionEvent);
    }

    /// <summary>XSS in the decoded query string or the inspected body.</summary>
    public static WafDetection ScanXss(WafRequestView view)
    {
        var hit = Evaluate(XssPattern, view.DecodedQuery, XssEvent);
        return hit.Matched ? hit : Evaluate(XssPattern, view.BodyText, XssEvent);
    }

    /// <summary>
    /// Path traversal in the raw target (before ASP.NET normalises it), the decoded path, or the query.
    /// Raw and decoded are both checked on purpose: one catches <c>..%2f</c>, the other <c>../</c>.
    /// </summary>
    public static WafDetection ScanPathTraversal(WafRequestView view)
    {
        foreach (var candidate in new[] { view.RawPath, view.DecodedPath, view.DecodedQuery })
        {
            var hit = Evaluate(PathTraversalPattern, candidate, PathTraversalEvent);
            if (hit.Matched) return hit;
        }

        return WafDetection.None;
    }

    /// <summary>Runs one detector, converting a regex timeout into a hit rather than a bypass.</summary>
    internal static WafDetection Evaluate(Regex pattern, string? text, string eventType, string? timeoutEvent = null)
    {
        if (string.IsNullOrEmpty(text)) return WafDetection.None;

        try
        {
            var match = pattern.Match(text);
            return match.Success ? WafDetection.Hit(eventType, Excerpt(text, match.Index)) : WafDetection.None;
        }
        catch (RegexMatchTimeoutException)
        {
            return WafDetection.Hit(timeoutEvent ?? TimeoutEvent, $"pattern timed out after {RegexTimeout.TotalMilliseconds:0}ms on {text.Length} chars");
        }
    }

    internal static string Excerpt(string text, int index)
    {
        if (index >= text.Length) return text.Length <= ExcerptLength ? text : text[..ExcerptLength];
        var length = Math.Min(ExcerptLength, text.Length - index);
        return text.Substring(index, length);
    }
}
