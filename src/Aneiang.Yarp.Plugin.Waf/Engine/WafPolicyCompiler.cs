using System.Text.RegularExpressions;
using Aneiang.Yarp.Models;

namespace Aneiang.Yarp.Plugin.Waf;

/// <summary>
/// Turns a binding configuration into a <see cref="WafPolicy"/>.
/// </summary>
/// <remarks>
/// Compilation never throws. A configuration error disables the part it affects and is reported
/// through <see cref="WafPolicy.Warnings"/>, because failing the whole policy on one bad regex
/// would silently remove every other rule on that route — the failure mode this design is
/// specifically trying to avoid.
/// </remarks>
public static class WafPolicyCompiler
{
    /// <summary>Ceiling on how much body may be inspected, regardless of what the configuration asks for.</summary>
    public const int HardMaxScanBytes = 1024 * 1024;

    private const int MinStatusCode = 400;
    private const int MaxStatusCode = 599;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    public static WafPolicy Compile(WafBindingOptions options)
    {
        var warnings = new List<string>();

        var enforce = ParseMode(options.Mode, warnings);
        var preamble = BuildPreamble(options);
        var body = BuildBodyInspection(options.BodyInspection, warnings);
        var response = BuildResponse(options.Response, warnings);
        var rules = options.Rules is { Count: > 0 }
            ? CompileRules(options.Rules, warnings)
            : SynthesizeRules(options);

        var requiresBody = rules.Any(rule => rule.RequiresBody);

        return new WafPolicy
        {
            Enforce = enforce,
            Preamble = preamble,
            Rules = rules,
            Body = body,
            Response = response,
            RequiresBody = requiresBody,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Monitor mode runs the same rules and logs the same events but blocks nothing. Trying a rule
    /// set against real traffic before enforcing it is the only way to find out which legitimate
    /// requests it would have rejected.
    /// </summary>
    private static bool ParseMode(string? mode, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(mode)) return true;

        if (mode.Trim().Equals("monitor", StringComparison.OrdinalIgnoreCase)) return false;
        if (mode.Trim().Equals("enforce", StringComparison.OrdinalIgnoreCase)) return true;

        warnings.Add($"Unknown mode '{mode}', expected 'enforce' or 'monitor'. Enforcing.");
        return true;
    }

    private static WafPreamble BuildPreamble(WafBindingOptions options) => new()
    {
        IpCheck = options.EnableIpCheck,
        IpWhitelist = Clean(options.IpWhitelist),
        IpBlacklist = Clean(options.IpBlacklist),
        SizeValidation = options.EnableRequestSizeValidation,
        MaxRequestBodySize = options.MaxRequestBodySize,
        MaxHeaderCount = options.MaxHeaderCount,
        MaxHeaderSize = options.MaxHeaderSize,
        MaxUrlLength = options.MaxUrlLength
    };

    private static WafBodyInspectionOptions BuildBodyInspection(WafBodyInspectionOptions? configured, List<string> warnings)
    {
        var body = configured ?? new WafBodyInspectionOptions();

        if (body.MaxBytes <= 0)
            return new WafBodyInspectionOptions { Enabled = false, MaxBytes = 0, ContentTypes = body.ContentTypes, InspectUnknownContentType = body.InspectUnknownContentType };

        if (body.MaxBytes > HardMaxScanBytes)
        {
            warnings.Add($"body.maxBytes {body.MaxBytes} exceeds the {HardMaxScanBytes} byte ceiling and was clamped.");
            body = new WafBodyInspectionOptions
            {
                Enabled = body.Enabled,
                MaxBytes = HardMaxScanBytes,
                ContentTypes = body.ContentTypes,
                InspectUnknownContentType = body.InspectUnknownContentType
            };
        }

        return body;
    }

    private static WafBlockResponseOptions BuildResponse(WafBlockResponseOptions? configured, List<string> warnings)
    {
        var response = configured ?? new WafBlockResponseOptions();

        if (response.StatusCode is < MinStatusCode or > MaxStatusCode)
        {
            warnings.Add($"response.statusCode {response.StatusCode} is outside {MinStatusCode}-{MaxStatusCode}; using 403.");
            response = new WafBlockResponseOptions
            {
                StatusCode = 403,
                ContentType = response.ContentType,
                Body = response.Body,
                Headers = response.Headers,
                IncludeRuleHeader = response.IncludeRuleHeader
            };
        }

        if (string.IsNullOrWhiteSpace(response.ContentType))
        {
            response = new WafBlockResponseOptions
            {
                StatusCode = response.StatusCode,
                ContentType = "application/json; charset=utf-8",
                Body = response.Body,
                Headers = response.Headers,
                IncludeRuleHeader = response.IncludeRuleHeader
            };
        }

        return response;
    }

    private static IReadOnlyList<WafCompiledRule> SynthesizeRules(WafBindingOptions options)
    {
        var rules = new List<WafCompiledRule>(3);

        // Cheap and path-only first, then the two that need the body. The order also decides which
        // event a request is attributed to when it trips several detectors at once.
        if (options.EnablePathTraversalDetection)
            rules.Add(BuiltinRule("path-traversal", WafBuiltin.PathTraversal));
        if (options.EnableSqlInjectionDetection)
            rules.Add(BuiltinRule("sql-injection", WafBuiltin.SqlInjection));
        if (options.EnableXssDetection)
            rules.Add(BuiltinRule("xss", WafBuiltin.Xss));

        return rules;

        static WafCompiledRule BuiltinRule(string id, WafBuiltin builtin) => new()
        {
            Id = id,
            Name = id,
            Action = WafAction.Block,
            Builtin = builtin,
            MatchAll = true,
            Conditions = []
        };
    }

    private static IReadOnlyList<WafCompiledRule> CompileRules(IEnumerable<WafRuleOptions> configured, List<string> warnings)
    {
        var rules = new List<WafCompiledRule>();
        var index = 0;

        foreach (var rule in configured)
        {
            index++;
            if (!rule.Enabled) continue;

            var id = string.IsNullOrWhiteSpace(rule.Id) ? $"rule-{index}" : rule.Id.Trim();

            if (!TryParseBuiltin(rule.Builtin, out var builtin))
            {
                warnings.Add($"rule '{id}': unknown builtin '{rule.Builtin}'.");
                continue;
            }

            if (!TryParseAction(rule.Action, out var action))
            {
                warnings.Add($"rule '{id}': unknown action '{rule.Action}'.");
                continue;
            }

            if (action == WafAction.Tag && string.IsNullOrWhiteSpace(rule.Tag))
            {
                warnings.Add($"rule '{id}': action 'tag' needs a tag value.");
                continue;
            }

            var conditions = CompileConditions(id, rule.Conditions, warnings);

            // A rule with neither a detector nor conditions matches every request. With
            // action=block that takes the route down, so it is dropped. With action=rate-limit it
            // is a blanket throttle on the whole route — a legitimate thing to ask for, and safe
            // because it has an explicit number in it.
            if (builtin == WafBuiltin.None && conditions.Count == 0 && action != WafAction.RateLimit)
            {
                warnings.Add($"rule '{id}': no builtin detector and no conditions; the rule matches everything, so it was dropped.");
                continue;
            }

            WafQuota? quota = null;
            if (action == WafAction.RateLimit && !TryCompileQuota(id, rule.RateLimit, warnings, out quota))
                continue;

            var matchAll = true;
            if (!string.IsNullOrWhiteSpace(rule.MatchMode) &&
                rule.MatchMode.Trim().Equals("any", StringComparison.OrdinalIgnoreCase))
                matchAll = false;
            else if (!string.IsNullOrWhiteSpace(rule.MatchMode) &&
                     !rule.MatchMode.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
                warnings.Add($"rule '{id}': unknown matchMode '{rule.MatchMode}', expected 'all' or 'any'. Using 'all'.");

            int? statusCode = null;
            if (rule.StatusCode is { } code)
            {
                if (code is >= MinStatusCode and <= MaxStatusCode) statusCode = code;
                else warnings.Add($"rule '{id}': statusCode {code} is outside {MinStatusCode}-{MaxStatusCode} and was ignored.");
            }

            rules.Add(new WafCompiledRule
            {
                Id = id,
                Name = rule.Name,
                Action = action,
                Builtin = builtin,
                Tag = rule.Tag,
                StatusCode = statusCode,
                RateLimit = quota,
                MatchAll = matchAll,
                Conditions = conditions
            });
        }

        var duplicates = rules.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        foreach (var duplicate in duplicates)
            warnings.Add($"rule id '{duplicate}' is used more than once; logs will not tell the two apart.");

        return rules;
    }

    private static IReadOnlyList<WafCompiledCondition> CompileConditions(
        string ruleId,
        List<WafConditionOptions>? configured,
        List<string> warnings)
    {
        if (configured is not { Count: > 0 }) return [];

        var conditions = new List<WafCompiledCondition>(configured.Count);

        foreach (var condition in configured)
        {
            if (!TryParseEnum(condition.Source, WafSource.Path, out var source) || condition.Source == null)
            {
                warnings.Add($"rule '{ruleId}': unknown condition source '{condition.Source}'.");
                continue;
            }

            if (!TryParseEnum(condition.Op, WafOperator.Contains, out var op))
            {
                warnings.Add($"rule '{ruleId}': unknown condition operator '{condition.Op}'.");
                continue;
            }

            if (string.IsNullOrEmpty(condition.Value))
            {
                warnings.Add($"rule '{ruleId}': condition on '{source}' has no value.");
                continue;
            }

            if (source is WafSource.Header or WafSource.Cookie && string.IsNullOrWhiteSpace(condition.Name))
            {
                warnings.Add($"rule '{ruleId}': condition on '{source}' needs a name.");
                continue;
            }

            if (op is WafOperator.GreaterThan or WafOperator.LessThan &&
                !double.TryParse(condition.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                warnings.Add($"rule '{ruleId}': '{op}' needs a numeric value, got '{condition.Value}'.");
                continue;
            }

            Regex? pattern = null;
            if (op == WafOperator.Regex)
            {
                try
                {
                    pattern = new Regex(
                        condition.Value,
                        RegexOptions.Compiled | RegexOptions.CultureInvariant |
                        (condition.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
                        RegexTimeout);
                }
                catch (ArgumentException exception)
                {
                    warnings.Add($"rule '{ruleId}': invalid regex '{condition.Value}' ({exception.Message}).");
                    continue;
                }
            }

            conditions.Add(new WafCompiledCondition
            {
                Source = source,
                Operator = op,
                Name = condition.Name,
                Value = condition.Value,
                IgnoreCase = condition.IgnoreCase,
                Pattern = pattern
            });
        }

        return conditions;
    }

    /// <summary>
    /// A rate-limit rule whose quota cannot be understood is dropped rather than silently given a
    /// default. A throttle with an accidental "60 per minute" in it looks like it is working and
    /// is not, which is exactly the class of failure this plugin is trying to remove.
    /// </summary>
    private static bool TryCompileQuota(
        string ruleId,
        WafRateLimitOptions? configured,
        List<string> warnings,
        out WafQuota? quota)
    {
        quota = null;

        if (configured is null)
        {
            warnings.Add($"rule '{ruleId}': action 'rate-limit' needs a rateLimit block (limit + window).");
            return false;
        }

        if (configured.Limit <= 0)
        {
            warnings.Add($"rule '{ruleId}': rateLimit.limit must be at least 1, got {configured.Limit}.");
            return false;
        }

        if (!TryParseDuration(configured.Window, out var window))
        {
            warnings.Add($"rule '{ruleId}': rateLimit.window '{configured.Window}' is not a duration (use 30s, 1m, 1h); using 1m.");
            window = TimeSpan.FromMinutes(1);
        }

        if (!TryParsePartition(configured.Partition, out var partition))
        {
            warnings.Add($"rule '{ruleId}': unknown rateLimit.partition '{configured.Partition}', expected ip, path, ip-path or global; using ip.");
            partition = WafPartition.Ip;
        }

        quota = new WafQuota(configured.Limit, window, partition);
        return true;
    }

    /// <summary>
    /// Same shorthand the rate-limit plugin accepts, so a duration copied between the two plugins
    /// keeps its meaning. A bare number is read as minutes there; it is rejected here instead —
    /// "300" meaning "300 minutes" on a login throttle is a five-hour ban nobody intended.
    /// </summary>
    private static bool TryParseDuration(string? value, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();

        // A bare number is deliberately refused before TimeSpan sees it: TimeSpan.Parse("300")
        // is 300 *days*, so the sloppy reading would be a lot worse than the sloppy units.
        if (text.All(char.IsDigit)) return false;

        if (TimeSpan.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            result = parsed;
            return parsed > TimeSpan.Zero;
        }

        if (text.Length < 2) return false;

        if (!double.TryParse(text[..^1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number) || number <= 0)
            return false;

        result = char.ToLowerInvariant(text[^1]) switch
        {
            's' => TimeSpan.FromSeconds(number),
            'm' => TimeSpan.FromMinutes(number),
            'h' => TimeSpan.FromHours(number),
            'd' => TimeSpan.FromDays(number),
            _ => TimeSpan.Zero
        };

        return result > TimeSpan.Zero;
    }

    private static bool TryParsePartition(string? value, out WafPartition partition)
    {
        partition = WafPartition.Ip;
        if (string.IsNullOrWhiteSpace(value)) return true;

        switch (value.Trim().ToLowerInvariant())
        {
            case "ip":
            case "ipaddress":
                return true;
            case "path":
                partition = WafPartition.Path;
                return true;
            case "ip-path":
            case "ippath":
                partition = WafPartition.IpPath;
                return true;
            case "global":
                partition = WafPartition.Global;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Action names are kebab-case in configuration (<c>rate-limit</c>) while the enum is PascalCase,
    /// so the same mapping problem as <see cref="TryParseBuiltin"/> applies. Getting this wrong drops
    /// the rule silently, which is how the previous version of this file lost every throttle rule.
    /// </summary>
    private static bool TryParseAction(string? value, out WafAction action)
    {
        action = WafAction.Block;
        if (string.IsNullOrWhiteSpace(value)) return true;

        switch (value.Trim().ToLowerInvariant())
        {
            case "block":
                return true;
            case "allow":
                action = WafAction.Allow;
                return true;
            case "log":
                action = WafAction.Log;
                return true;
            case "tag":
                action = WafAction.Tag;
                return true;
            case "rate-limit":
            case "ratelimit":
                action = WafAction.RateLimit;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Detector names are kebab-case in configuration (<c>sql-injection</c>) but PascalCase in the
    /// enum, so generic <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> cannot read them.
    /// </summary>
    private static bool TryParseBuiltin(string? value, out WafBuiltin builtin)
    {
        builtin = WafBuiltin.None;
        if (string.IsNullOrWhiteSpace(value)) return true;

        switch (value.Trim().ToLowerInvariant())
        {
            case "none":
                return true;
            case "sql-injection":
            case "sqlinjection":
                builtin = WafBuiltin.SqlInjection;
                return true;
            case "xss":
                builtin = WafBuiltin.Xss;
                return true;
            case "path-traversal":
            case "pathtraversal":
                builtin = WafBuiltin.PathTraversal;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseEnum<T>(string? value, T fallback, out T result) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = fallback;
            return true;
        }

        return Enum.TryParse(value.Trim(), ignoreCase: true, out result) && Enum.IsDefined(result);
    }

    private static string[] Clean(List<string>? values) =>
        values is null
            ? []
            : values.Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
}
