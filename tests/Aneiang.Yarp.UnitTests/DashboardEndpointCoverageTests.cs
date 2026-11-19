using System.Text;
using System.Text.RegularExpressions;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Every management API endpoint must have a caller in the dashboard's own front end.
/// </summary>
/// <remarks>
/// <para>
/// The failure this guards against is not "the API is wrong" but "the API is unreachable by a
/// human". Three families of endpoints shipped that way: the whole-config integrity check,
/// the circuit-breaker thresholds, and the bulk binding operations. All three worked, were
/// covered by other tests, and had no screen that called them - which from the outside is
/// indistinguishable from the feature not existing.
/// </para>
/// <para>
/// The check is static: it reads controller attributes and the shipped JS/views rather than
/// booting a host, so it keeps working without a running gateway and covers whatever the
/// files contain today.
/// </para>
/// <para>
/// Path parameters are matched loosely on purpose. Front-end call sites build URLs by
/// concatenation (<c>'/api/plugins/' + id + '/toggle'</c>) or template literals
/// (<c>`/api/x/${id}/y`</c>); requiring an exact literal would report every parameterised
/// endpoint as missing and train everyone to ignore the test.
/// </para>
/// </remarks>
public class DashboardEndpointCoverageTests
{
    /// <summary>Endpoints that legitimately have no panel caller.</summary>
    private static readonly HashSet<string> NotPanelEndpoints = new(StringComparer.Ordinal)
    {
        // Redundant reads: the collection endpoint already returns the same payload
        // (totals are part of GET /api/plugin-resources, presets carry configJson).
        "GET /api/plugin-resources/totals",
        "GET /api/plugin-resources/{pluginId}",
        "GET /api/presets/{id}",
    };

    /// <summary>
    /// Client self-registration. These are called by the services being proxied - a Go/Java
    /// client posts its own route - not by anything in this repository's UI.
    /// </summary>
    private const string ClientSdkPrefix = "/api/gateway/";

    [Fact]
    public void Management_endpoints_are_all_reachable_from_the_dashboard()
    {
        var endpoints = ReadBackendEndpoints();
        var frontend = ReadFrontendSource();

        // A parse regression that yields almost nothing must fail loudly instead of passing
        // every (zero) check - the same reasoning as the CI "fewer than N tests" gate.
        Assert.True(endpoints.Length >= 90,
            $"Only {endpoints.Length} endpoints were discovered; the controller parser is broken.");

        var unreferenced = new List<string>();
        foreach (var (method, template) in endpoints)
        {
            if (template.StartsWith(ClientSdkPrefix, StringComparison.Ordinal)) continue;
            if (NotPanelEndpoints.Contains($"{method} {template}")) continue;
            if (IsReferenced(template, frontend)) continue;
            unreferenced.Add($"{method} {template}");
        }

        Assert.True(unreferenced.Count == 0,
            "These management endpoints have no caller in the dashboard front end:" +
            Environment.NewLine + string.Join(Environment.NewLine, unreferenced.OrderBy(x => x, StringComparer.Ordinal)) +
            Environment.NewLine +
            "If the endpoint is internal (client SDK, redundant read), add it to NotPanelEndpoints " +
            "with a reason. Otherwise the feature is only half delivered: an operator cannot reach it.");
    }

    /// <summary>
    /// Guards the allow-list itself: an entry that no longer matches an endpoint is dead
    /// documentation, and a dead allow-list silently widens over time.
    /// </summary>
    [Fact]
    public void Allow_list_entries_still_correspond_to_real_endpoints()
    {
        var endpoints = ReadBackendEndpoints().Select(e => $"{e.Method} {e.Template}").ToHashSet(StringComparer.Ordinal);
        var stale = NotPanelEndpoints.Where(entry => !endpoints.Contains(entry)).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        Assert.True(stale.Length == 0,
            "These allow-list entries no longer match any endpoint: " + string.Join(", ", stale));
    }

    /// <summary>
    /// Builds a matcher from the route template: literal segments are escaped, each
    /// <c>{param}</c> becomes a bounded wildcard so concatenation and template literals both
    /// match. The bound keeps a wildcard from reaching across lines and pairing two
    /// unrelated URLs that happen to share a first and last segment.
    /// </summary>
    private static bool IsReferenced(string template, string frontend)
    {
        var builder = new StringBuilder();
        var parts = Regex.Split(template, @"\{[^}]*\}");
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) builder.Append("[^\\n]{0,160}?");
            builder.Append(Regex.Escape(parts[i]));
        }

        return Regex.IsMatch(frontend, builder.ToString(), RegexOptions.Singleline);
    }

    private static (string Method, string Template)[] ReadBackendEndpoints()
    {
        var root = FindRepositoryDirectory("src");
        var results = new List<(string, string)>();
        var routePattern = new Regex(@"\[Route\(""([^""]*)""\)\]", RegexOptions.Compiled);
        var actionPattern = new Regex(@"\[Http(Get|Post|Put|Delete|Patch)(?:\(""([^""]*)""\))?\]", RegexOptions.Compiled);

        foreach (var file in Directory.EnumerateFiles(root, "*Controller.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            var classRoute = routePattern.Match(source) is { Success: true } routeMatch ? routeMatch.Groups[1].Value : string.Empty;

            foreach (Match match in actionPattern.Matches(source))
            {
                var method = match.Groups[1].Value.ToUpperInvariant();
                var relative = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;

                string full;
                if (relative.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
                {
                    full = "/" + relative.TrimStart('/');
                }
                else if (classRoute.Length > 0)
                {
                    full = "/" + classRoute.Trim('/') + (relative.Length > 0 ? "/" + relative.Trim('/') : string.Empty);
                }
                else
                {
                    full = "/" + relative.TrimStart('/');
                }

                // Page routes are served by views, so "no JS caller" is normal there. This
                // test is about the API surface only.
                if (!full.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) continue;

                results.Add((method, Normalize(full)));
            }
        }

        return results.Distinct().ToArray();
    }

    /// <summary>Collapses double slashes and trailing slashes so templates compare cleanly.</summary>
    private static string Normalize(string path)
    {
        var normalized = Regex.Replace(path, "/{2,}", "/");
        return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
    }

    private static string ReadFrontendSource()
    {
        var dashboard = FindRepositoryDirectory("src/Aneiang.Yarp.Dashboard");
        var builder = new StringBuilder();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(dashboard, "wwwroot", "js"), "*.js", SearchOption.AllDirectories))
        {
            builder.AppendLine(NormalizeForMatching(File.ReadAllText(file)));
        }

        foreach (var file in Directory.EnumerateFiles(Path.Combine(dashboard, "Views"), "*.cshtml", SearchOption.AllDirectories))
        {
            builder.AppendLine(NormalizeForMatching(File.ReadAllText(file)));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Flattens the two idioms the page scripts use to build URLs so a route template can be
    /// matched against them as one run of characters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>var API = '/api/canary'; ... API + '/rollouts/' + id</c> puts the prefix and the
    /// suffix in unrelated string literals; matching the template literally would report
    /// every such endpoint as uncovered (which is exactly what happened to canary, alerts
    /// and environments on the first run of this test).
    /// </para>
    /// <para>
    /// So: inline each API-prefix variable, then drop quotes and <c>+</c> so concatenation
    /// reads as one string. Newlines are kept - the template matcher refuses to cross them,
    /// which is what stops a wildcard from pairing two unrelated URLs.
    /// </para>
    /// </remarks>
    private static string NormalizeForMatching(string source)
    {
        var prefixPattern = new Regex(@"(?:var|let|const)\s+(\w+)\s*=\s*['""`](/api/[^'""`]*)['""`]");
        foreach (Match match in prefixPattern.Matches(source))
        {
            var name = Regex.Escape(match.Groups[1].Value);
            var value = match.Groups[2].Value;

            // `API + '/rest'` becomes `/api/canary/rest`; a bare `API` becomes the prefix.
            source = Regex.Replace(source, @"\b" + name + @"\s*\+\s*", _ => value);
            source = Regex.Replace(source, @"\b" + name + @"\b", _ => value);
        }

        // Quotes and concatenation operators carry no meaning for substring matching, and
        // leaving them in would split every built URL at its first parameter.
        return Regex.Replace(source, @"['""`+]", string.Empty);
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

        throw new DirectoryNotFoundException($"Could not locate '{relativePath}' above '{AppContext.BaseDirectory}'.");
    }
}
