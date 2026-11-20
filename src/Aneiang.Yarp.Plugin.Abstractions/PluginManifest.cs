using System.Text.Json.Serialization;

namespace Aneiang.Yarp.Plugins;

/// <summary>Static metadata describing a gateway plugin without activating its runtime implementation.</summary>
/// <remarks>
/// <see cref="Schemas"/> is required for external plugins: one entry per configuration version, or an
/// empty array for a plugin that takes no configuration. Every <c>ConfigJsonSchema</c> is parsed and
/// shape-checked when the plugin is installed, because a schema the console cannot render is otherwise
/// indistinguishable from a plugin without settings.
/// </remarks>
public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    IReadOnlyList<PluginScope> Scopes,
    IReadOnlyList<PluginCapability> Capabilities,
    int Order,
    PluginResourceRequirements Resources,
    IReadOnlyList<PluginSchemaReference> Schemas,
    string Description = "")
{
    /// <summary>Other plugins that must be enabled before this plugin can be enabled.</summary>
    public IReadOnlyList<PluginDependency> Dependencies { get; init; } = [];

    /// <summary>Entry assembly path relative to plugin.json for an external plugin.</summary>
    public string? EntryAssembly { get; init; }

    /// <summary>Assembly-qualified or full type name implementing <see cref="IGatewayPlugin"/>.</summary>
    public string? EntryType { get; init; }
}

/// <summary>A versioned dependency on another installed plugin.</summary>
public sealed record PluginDependency(string PluginId, string? MinimumVersion = null);

/// <summary>Target types to which a plugin configuration can be bound.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginScope
{
    Route,
    Cluster
}

/// <summary>Host integration points used by a plugin.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginCapability
{
    RequestMiddleware,
    ProxyPipeline,
    Dashboard,
    BackgroundService,

    /// <summary>
    /// Contributes one or more <c>ILoadBalancingPolicy</c> implementations so clusters can select
    /// them by name. Must be registered through <see cref="IGatewayPluginHostServices"/> — the
    /// per-plugin runtime domain is built after the host container is frozen, so YARP never sees
    /// policies registered from <see cref="IGatewayPlugin.ConfigureServices"/>.
    /// </summary>
    LoadBalancingPolicy,

    /// <summary>
    /// Contributes <c>ITransformProvider</c> implementations that cluster/route transforms resolve
    /// by name. Subject to the same host-registration constraint as
    /// <see cref="LoadBalancingPolicy"/>.
    /// </summary>
    Transform
}

/// <summary>Resources a plugin may consume while active.</summary>
public sealed record PluginResourceRequirements(
    bool RequestMiddleware = false,
    bool BackgroundServices = false,
    bool Database = false,
    bool NetworkConnections = false);

/// <summary>Reference to a versioned configuration schema supplied by a plugin.</summary>
/// <param name="Version">
/// Integer configuration version, starting at 1 and increasing by 1 per breaking change.
/// Bindings store the version they were saved with; changing it on a binding runs the
/// matching <see cref="IPluginConfigurationMigrator"/>.
/// </param>
/// <param name="Reference">
/// Documentation pointer only (<c>builtin://plugins/{id}/schemas/config-v{n}.json</c> by convention).
/// Nothing reads it, so it is the one place worth spending a line on where the schema came from.
/// </param>
/// <param name="ConfigJsonSchema">
/// The schema itself, as a JSON **string** - the manifest is JSON, so this value is a string
/// containing JSON and its quotes are escaped. The console renders the binding form from it and
/// the server validates every save against it, so it has to follow the host's subset of JSON Schema:
/// <list type="bullet">
/// <item>root must be an object declaring <c>"type": "object"</c>; without it the form renders empty;</item>
/// <item>supported keywords: type, properties, required, additionalProperties, enum, const,
/// anyOf/oneOf/allOf, items, min/max Items+Length+Properties, minimum, maximum, pattern,
/// and <c>"format": "duration"</c>;</item>
/// <item><c>anyOf</c>/<c>oneOf</c> branches WITH a <c>title</c> render as a mode selector, branches
/// WITHOUT one are folded into a single field accepting any of the branch values;</item>
/// <item><c>$ref</c>, <c>$defs</c>, if/then/else, not and patternProperties are ignored -
/// a warning is logged at install time, nothing else happens;</item>
/// <item><c>title</c> is the field label (external plugins have no translation keys), and
/// <c>description</c> becomes the help text.</item>
/// </list>
/// Use <c>"{}"</c> (or an empty <c>schemas</c> array) only for a plugin that takes no configuration:
/// it validates nothing and renders no form. Full guide: docs page <c>plugin-config-schema</c>.
/// </param>
public sealed record PluginSchemaReference(
    int Version,
    string Reference,
    string ConfigJsonSchema = "{}");

/// <summary>Transforms persisted plugin configuration between two adjacent schema versions.</summary>
public interface IPluginConfigurationMigrator
{
    string PluginId { get; }
    int FromVersion { get; }
    int ToVersion { get; }
    bool TryMigrate(string configJson, out string migratedConfigJson, out string error);
}

/// <summary>Service that coordinates plugin configuration migrations.</summary>
public interface IPluginConfigurationMigrationService
{
    bool TryMigrate(string pluginId, int fromVersion, int toVersion, string configJson, out string migratedConfigJson, out string error);
}
