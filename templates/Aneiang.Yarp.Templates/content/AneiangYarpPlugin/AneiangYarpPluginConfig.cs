namespace AneiangYarpPlugin;

/// <summary>
/// The binding configuration, field for field with the schema in <c>plugin.manifest.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every property has a default. A binding saved before a field existed, a field removed in a later
/// version, or an empty <c>{}</c> all deserialize into a usable object - a plugin that throws on
/// missing configuration takes the request down with it, and the configuration is edited far more
/// often than the code.
/// </para>
/// <para>
/// Names must match the schema: the console writes the JSON from the form, the serializer binds it
/// here. A rename in one place and not the other fails silently (the property keeps its default),
/// which is why <see cref="HeaderName"/> and <see cref="HeaderValue"/> appear in both files.
/// </para>
/// </remarks>
public sealed class AneiangYarpPluginConfig
{
    /// <summary>Turns this binding off without deleting it. Default: on.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Response header to write. Default: <c>X-Plugin-Badge</c>.</summary>
    public string HeaderName { get; set; } = "X-Plugin-Badge";

    /// <summary>Value for that header. Default: a greeting, so an unconfigured binding is visible.</summary>
    public string HeaderValue { get; set; } = "hello from the plugin";
}
