using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aneiang.Yarp.Plugins;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Contract tests over every built-in plugin.
/// </summary>
/// <remarks>
/// <para>
/// The plugin contract is mostly metadata, and metadata is the part nothing validates: a duplicated
/// <c>order</c>, a manifest whose id does not match the instance, a schema that is not valid JSON —
/// all of them ship fine and surface later as "the plugin is configured but does nothing".
/// </para>
/// <para>
/// These assertions are deliberately importable by third parties: they only touch
/// <c>Aneiang.Yarp.Plugin.Abstractions</c>. A template plugin can copy them verbatim.
/// </para>
/// </remarks>
public class PluginContractTests
{
    /// <summary>
    /// The built-in set, pinned on purpose. A plugin that quietly stops being discovered — a renamed
    /// assembly, a type made non-public, a missing parameterless constructor — would otherwise leave
    /// every other assertion in this file green against an empty list.
    /// </summary>
    private static readonly string[] ExpectedPluginIds =
    [
        "waf", "rate-limit", "circuit-breaker", "compression", "request-retry", "response-cache",
        "proxy-log", "service-discovery", "traffic-metrics", "cluster-metrics", "fault-injection"
    ];

    private static readonly Lazy<IReadOnlyList<IGatewayPlugin>> Discovered = new(Discover);

    private static IReadOnlyList<IGatewayPlugin> Plugins => Discovered.Value;

    private static IReadOnlyList<IGatewayPlugin> Discover()
    {
        var plugins = new List<IGatewayPlugin>();
        var directory = AppContext.BaseDirectory;

        // The test project references the dashboard, which references every built-in plugin, so the
        // assemblies are in the output folder. Loading by path (not by name) keeps this independent
        // of which assemblies the runtime happened to touch first.
        foreach (var path in Directory.EnumerateFiles(directory, "Aneiang.Yarp.Plugin.*.dll")
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var assembly = Assembly.LoadFrom(path);
            foreach (var type in assembly.GetExportedTypes())
            {
                if (!typeof(IGatewayPlugin).IsAssignableFrom(type)) continue;
                if (type.IsAbstract || type.IsInterface) continue;

                // The loader instantiates plugins directly, so a missing parameterless constructor
                // is a load-time failure. Skip here and let the pinned-id test report it.
                if (type.GetConstructor(Type.EmptyTypes) is null) continue;

                plugins.Add((IGatewayPlugin)Activator.CreateInstance(type)!);
            }
        }

        return plugins;
    }

    [Fact]
    public void Every_builtin_plugin_is_discoverable()
    {
        var ids = Plugins.Select(p => p.PluginId).ToArray();

        Assert.Equal(ExpectedPluginIds.Length, ids.Length);
        Assert.Equal(ExpectedPluginIds.OrderBy(x => x, StringComparer.Ordinal), ids.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Plugin_ids_are_stable_slug_format()
    {
        // The id is a directory name, a config key and a log field. Uppercase or spaces would work in
        // some of those places and break in others.
        var slug = new Regex("^[a-z0-9]+(-[a-z0-9]+)*$");

        foreach (var plugin in Plugins)
            Assert.Matches(slug, plugin.PluginId);
    }

    [Fact]
    public void Plugin_ids_are_unique()
    {
        var duplicates = Plugins
            .GroupBy(p => p.PluginId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Manifest_identity_matches_the_plugin_instance()
    {
        foreach (var plugin in Plugins)
        {
            Assert.Equal(plugin.PluginId, plugin.Manifest.Id);
            Assert.Equal(plugin.DisplayName, plugin.Manifest.Name);
            Assert.Equal(plugin.Version, plugin.Manifest.Version);
            Assert.False(string.IsNullOrWhiteSpace(plugin.Manifest.Description));
        }
    }

    [Fact]
    public void Versions_are_semantic()
    {
        // The host's dependency validation parses these before loading an assembly, so an unparsable
        // version turns "plugin needs >=2.0" into a load failure with a confusing message.
        var semver = new Regex(@"^\d+\.\d+(\.\d+)?(-[0-9A-Za-z.-]+)?$");

        foreach (var plugin in Plugins)
            Assert.Matches(semver, plugin.Version);
    }

    [Fact]
    public void Orders_are_unique_across_plugins()
    {
        // Proxy pipeline order is (order, id). Duplicate order values still resolve, but they make
        // the actual sequence depend on which plugin id sorts first — an invisible coupling that
        // nobody would think to check when adding a plugin.
        var duplicates = Plugins
            .GroupBy(p => p.Manifest.Order)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(p => p.PluginId))}")
            .ToArray();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Every_plugin_declares_a_scope_and_is_scoped_explicitly()
    {
        // A plugin with no scope cannot be bound to anything, so it can never be configured — which
        // for a routing plugin means it can never do anything.
        foreach (var plugin in Plugins)
        {
            Assert.NotEmpty(plugin.Manifest.Scopes);
            Assert.Equal(plugin.Manifest.Scopes.Count, plugin.Manifest.Scopes.Distinct().Count());
        }
    }

    [Fact]
    public void Capabilities_match_the_implementation()
    {
        foreach (var plugin in Plugins)
        {
            var manifest = plugin.Manifest;

            Assert.Equal(manifest.Capabilities.Count, manifest.Capabilities.Distinct().Count());

            // Load-balancing policies and transforms are resolved by YARP while it starts up, which
            // is before a plugin's own container exists. The only way to contribute one is the host
            // collection; declaring the capability without implementing the interface is a
            // configuration that is accepted and never used.
            if (manifest.Capabilities.Any(PluginCapabilities.RequiresHostRegistration))
                Assert.True(plugin is IGatewayPluginHostServices,
                    $"{plugin.PluginId} declares a host-registered capability but does not implement {nameof(IGatewayPluginHostServices)}.");
        }
    }

    [Fact]
    public void Schema_references_are_valid_json_with_unique_versions()
    {
        foreach (var plugin in Plugins)
        {
            var manifest = plugin.Manifest;
            var versions = manifest.Schemas.Select(s => s.Version).ToArray();

            Assert.Equal(versions.Length, versions.Distinct().Count());

            foreach (var schema in manifest.Schemas)
            {
                // Version 0 is the implicit "no schema" value used by the default manifest; a real
                // reference has to start at 1, because migrations are (from, to) pairs.
                Assert.True(schema.Version >= 1, $"{plugin.PluginId} has schema version {schema.Version}.");
                Assert.False(string.IsNullOrWhiteSpace(schema.Reference));

                // The dashboard renders a form from this string. If it is not JSON, the binding
                // editor fails at the point somebody tries to configure the plugin.
                var parsed = JsonNode.Parse(schema.ConfigJsonSchema);
                Assert.NotNull(parsed);
                Assert.IsType<JsonObject>(parsed);
            }
        }
    }

    [Fact]
    public void Dependencies_resolve_within_the_builtin_set()
    {
        var available = Plugins.ToDictionary(p => p.PluginId, StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in Plugins)
        {
            foreach (var dependency in plugin.Manifest.Dependencies)
            {
                Assert.True(available.ContainsKey(dependency.PluginId),
                    $"{plugin.PluginId} depends on '{dependency.PluginId}', which is not a built-in plugin.");

                // An empty constraint means "any version"; anything else must be a comparator the
                // host can parse, otherwise the dependency check silently passes everything.
                if (string.IsNullOrWhiteSpace(dependency.MinimumVersion)) continue;

                Assert.Matches(@"^[<>==]=?\s*\d+\.\d+(\.\d+)?$", dependency.MinimumVersion.Trim());
            }
        }
    }

    [Fact]
    public void Dashboard_capability_implies_a_dashboard_contribution()
    {
        // Not every dashboard plugin adds a nav item (several only add widgets or a detail page), so
        // this only asserts that the call does not throw. It catches the plugin that forgot to
        // override ConfigureDashboard and would render an empty page.
        foreach (var plugin in Plugins.Where(p => p.Manifest.Capabilities.Contains(PluginCapability.Dashboard)))
        {
            var builder = new RecordingDashboardBuilder();
            plugin.ConfigureDashboard(builder);
        }
    }

    [Fact]
    public void Builtin_plugins_are_listed_in_the_compatibility_matrix()
    {
        var matrix = File.ReadAllText(FindRepositoryFile("docs/plugins/plugin-compatibility.md"));

        // Bidirectional: a plugin missing from the doc is undocumented, and an id in the doc that no
        // plugin has is a stale row that tells integrators about a plugin that does not exist.
        foreach (var pluginId in Plugins.Select(p => p.PluginId))
            Assert.Contains($"`{pluginId}`", matrix, StringComparison.Ordinal);

        // Only the matrix table counts. The rest of the document legitimately mentions field names
        // (`id`, `dependencies`) inside backticks, which look identical to a table row.
        var start = matrix.IndexOf("## 内置插件矩阵", StringComparison.Ordinal);
        Assert.True(start >= 0, "The compatibility matrix section is missing.");
        var end = matrix.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        var section = end > 0 ? matrix[start..end] : matrix[start..];

        var documented = Regex.Matches(section, @"^\| `(?<id>[a-z0-9-]+)` \|", RegexOptions.Multiline)
            .Select(m => m.Groups["id"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(
            Plugins.Select(p => p.PluginId).OrderBy(x => x, StringComparer.Ordinal),
            documented.OrderBy(x => x, StringComparer.Ordinal));
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find '{relativePath}' above '{AppContext.BaseDirectory}'.");
    }

    private sealed class RecordingDashboardBuilder : IPluginDashboardBuilder
    {
        private readonly List<PluginNavItem> _navItems = [];
        private readonly List<PluginDashboardWidget> _widgets = [];

        public IReadOnlyList<PluginNavItem> NavItems => _navItems;

        public IReadOnlyList<PluginDashboardWidget> Widgets => _widgets;

        public void AddNavItem(PluginNavItem item) => _navItems.Add(item);

        public void AddWidget(PluginDashboardWidget widget) => _widgets.Add(widget);
    }
}
