using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Translation-file integrity: both locales say the same things, and the code only asks
/// for keys that exist.
/// </summary>
/// <remarks>
/// <para>
/// <c>DashboardI18n</c> merges one JSON file per domain and silently falls back to the key,
/// so a key that exists in only one language degrades to the other language at runtime and
/// nothing fails. A key that exists in neither degrades to a raw <c>trace.col.spans</c> in
/// the middle of the UI.
/// </para>
/// <para>
/// These assertions are static on purpose - they read the same files the app embeds, so a
/// new page or a new string is covered without anyone remembering to extend a fixture.
/// </para>
/// </remarks>
public class DashboardI18nTests
{
    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> Locales = new(LoadAll);

    private static Dictionary<string, string> Zh => Locales.Value["zh-CN"];
    private static Dictionary<string, string> En => Locales.Value["en-US"];

    /// <summary>
    /// Placeholder parity: a translation that drops <c>{count}</c> renders a sentence with a
    /// missing number, which reads worse than an untranslated one.
    /// </summary>
    [Fact]
    public void Placeholders_are_the_same_in_both_locales()
    {
        var problems = new List<string>();
        foreach (var key in Zh.Keys.Where(En.ContainsKey).OrderBy(k => k, StringComparer.Ordinal))
        {
            var zh = Placeholders(Zh[key]);
            var en = Placeholders(En[key]);
            if (!zh.SetEquals(en))
            {
                problems.Add($"{key}: zh=[{string.Join(",", zh)}] en=[{string.Join(",", en)}]");
            }
        }

        Assert.True(problems.Count == 0,
            "Placeholder mismatch between zh-CN and en-US:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    /// <summary>Every key defined in one locale is defined in the other.</summary>
    [Fact]
    public void Both_locales_define_the_same_keys()
    {
        var zhOnly = Zh.Keys.Except(En.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var enOnly = En.Keys.Except(Zh.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        Assert.True(zhOnly.Length == 0, "Keys only in zh-CN: " + string.Join(", ", zhOnly));
        Assert.True(enOnly.Length == 0, "Keys only in en-US: " + string.Join(", ", enOnly));
    }

    /// <summary>
    /// One key, one home. <c>DashboardI18n.LoadLocale</c> merges the domain files in a fixed
    /// order and the last writer silently wins, so a key defined twice has an effective value
    /// that depends on that order - edit the copy that loses and the UI keeps the old string
    /// with nothing failing. 46 keys had accumulated a shadow copy this way before this test
    /// existed (zh 34 of them with a different value than the one on screen).
    /// </summary>
    [Fact]
    public void No_key_is_defined_in_more_than_one_domain_file()
    {
        var root = FindRepositoryDirectory("src/Aneiang.Yarp.Dashboard/Infrastructure/I18n");
        var problems = new List<string>();

        foreach (var locale in new[] { "zh-CN", "en-US" })
        {
            var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, locale), "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                var domain = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))
                    ?? new Dictionary<string, string>();
                foreach (var key in domain.Keys)
                {
                    if (!owners.TryGetValue(key, out var list)) owners[key] = list = new List<string>();
                    list.Add(Path.GetFileName(file));
                }
            }

            problems.AddRange(owners
                .Where(kv => kv.Value.Count > 1)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{locale}/{kv.Key} in {string.Join(" + ", kv.Value)}"));
        }

        Assert.True(problems.Count == 0,
            "A translation key must live in exactly one domain file:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    /// <summary>An empty value is worse than a missing one: the fallback never fires.</summary>
    /// <remarks>
    /// Three keys are intentionally a single space or empty in English: they are counter words
    /// (<c>个</c>) that English does not have, and the caller concatenates them. They are listed
    /// here so a <em>new</em> blank still fails - the pattern is allowed, but only on purpose.
    /// </remarks>
    [Fact]
    public void No_translation_is_blank()
    {
        var intentionallyBlank = new HashSet<string>(StringComparer.Ordinal)
        {
            "malert.unit.count", "notif.pageUnit", "pc.state.unit"
        };

        var blanks = new List<string>();
        foreach (var (locale, dict) in Locales.Value)
        {
            blanks.AddRange(dict
                .Where(kv => string.IsNullOrWhiteSpace(kv.Value) && !intentionallyBlank.Contains(kv.Key))
                .Select(kv => $"{locale}/{kv.Key}"));
        }

        Assert.True(blanks.Count == 0, "Blank translations: " + string.Join(", ", blanks));
    }

    /// <summary>
    /// Every literal key a view asks for exists. Dynamic bindings (<c>data-i18n="@item.Key"</c>)
    /// are skipped - their key is only known at render time, and the components that build them
    /// are covered by the script check below.
    /// </summary>
    [Fact]
    public void Keys_used_by_views_exist()
    {
        var root = FindRepositoryDirectory("src/Aneiang.Yarp.Dashboard/Views");
        var pattern = new Regex("data-i18n(?:-title|-placeholder)?=\"([^\"]+)\"", RegexOptions.Compiled);
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(root, "*.cshtml", SearchOption.AllDirectories))
        {
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            {
                var key = match.Groups[1].Value.Trim();
                // Razor expressions and composed keys are not literals.
                if (key.Length == 0 || key.Contains('@') || key.Contains('<') || key.Contains('%')) continue;
                if (!Zh.ContainsKey(key)) missing.Add($"{key} ({Path.GetFileName(file)})");
            }
        }

        Assert.True(missing.Count == 0, "View references missing translations: " + string.Join(", ", missing));
    }

    /// <summary>
    /// Every literal key a page script asks for exists. This is the check that would have caught
    /// the tracing page shipping with an entire missing key family: the fallback strings are
    /// Chinese, so a zh-CN user saw a working page and an en-US user saw Chinese.
    /// </summary>
    /// <remarks>
    /// Several page modules wrap the global lookup with their own namespace, e.g.
    /// <c>t(key) =&gt; __('malert.' + key)</c>. The prefix is detected per file, and a key counts
    /// as resolved if either the prefixed or the bare form exists - both spellings are used in
    /// practice, and the failure this test cares about is a key that resolves to nothing at all.
    /// </remarks>
    [Fact]
    public void Keys_used_by_scripts_exist()
    {
        var root = FindRepositoryDirectory("src/Aneiang.Yarp.Dashboard/wwwroot/js");
        var callPattern = new Regex(@"\b(?:t|__)\(\s*'([A-Za-z0-9._]+)'", RegexOptions.Compiled);
        var prefixPattern = new Regex(@"__\(\s*'([A-Za-z0-9._]*)'\s*\+\s*(?:key|k|name|id)\b", RegexOptions.Compiled);
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(root, "*.js", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            var prefixMatch = prefixPattern.Match(source);
            var prefix = prefixMatch.Success ? prefixMatch.Groups[1].Value : string.Empty;

            foreach (Match match in callPattern.Matches(source))
            {
                var key = match.Groups[1].Value;
                // Prefixes of composed keys, e.g. t('plugin.name.' + id, ...) - the trailing dot
                // is the tell, and the composed result is covered by the plugin-name assertions.
                if (key.EndsWith(".", StringComparison.Ordinal)) continue;

                // Concatenated suffixes, e.g. t('verdict' + record.verdict) -> canary.verdictAdvance.
                // Only an identifier after the '+' counts: `'a.b' + ' text'` is still a real key.
                var tail = source[(match.Index + match.Length)..];
                if (Regex.IsMatch(tail, @"^\s*\+\s*[A-Za-z_$]")) continue;

                if (Zh.ContainsKey(key)) continue;
                if (prefix.Length > 0 && Zh.ContainsKey(prefix + key)) continue;
                missing.Add($"{key} ({Path.GetFileName(file)})");
            }
        }

        Assert.True(missing.Count == 0, "Script references missing translations: " + string.Join(", ", missing));
    }

    private static HashSet<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{([A-Za-z0-9_]+)\}").Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, Dictionary<string, string>> LoadAll()
    {
        var root = FindRepositoryDirectory("src/Aneiang.Yarp.Dashboard/Infrastructure/I18n");
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var locale in new[] { "zh-CN", "en-US" })
        {
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            var directory = Path.Combine(root, locale);
            Assert.True(Directory.Exists(directory), $"Translation directory not found: {directory}");

            foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                var domain = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))
                    ?? new Dictionary<string, string>();
                foreach (var (key, value) in domain)
                {
                    // Same precedence as DashboardI18n.LoadLocale: later domains win.
                    // No_key_is_defined_in_more_than_one_domain_file keeps that order irrelevant.
                    merged[key] = value;
                }
            }

            result[locale] = merged;
        }

        return result;
    }

    private static string FindRepositoryDirectory(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find '{relativePath}' above '{AppContext.BaseDirectory}'.");
    }
}
