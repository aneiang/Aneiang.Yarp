using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Yarp.ReverseProxy.Configuration;

namespace Aneiang.Yarp.Serialization;

/// <summary>
/// Single source of truth for serializing and deserializing native YARP
/// <see cref="RouteConfig"/> and <see cref="ClusterConfig"/> objects across the whole stack
/// (persistence, API parsing, import/export).
/// Output keys are PascalCase to match the canonical YARP appsettings layout, while reading
/// is case-insensitive and tolerant of JSON comments and trailing commas.
/// </summary>
public static class YarpJsonConfig
{
    /// <summary>
    /// Lenient options used everywhere: PascalCase output, case-insensitive input,
    /// comments skipped, trailing commas allowed, enums as strings, nulls omitted.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = BuildOptions(writeIndented: false);

    /// <summary>Same as <see cref="Options"/> but with indentation for human-friendly output.</summary>
    public static JsonSerializerOptions IndentedOptions { get; } = BuildOptions(writeIndented: true);

    /// <summary>
    /// Builds the options.
    /// </summary>
    /// <param name="writeIndented">If true, write indented.</param>
    /// <returns>A JsonSerializerOptions.</returns>
    private static JsonSerializerOptions BuildOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = writeIndented
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new VersionConverter());
        options.Converters.Add(new LenientBooleanConverterFactory());
        options.Converters.Add(new LenientTimeSpanConverterFactory());
        options.Converters.Add(new LenientStringConverter());
        return options;
    }

    /// <summary>Serialize a route to a PascalCase JSON string.</summary>
    public static string SerializeRoute(RouteConfig route, bool indented = false)
        => JsonSerializer.Serialize(route, indented ? IndentedOptions : Options);

    /// <summary>Serialize a cluster to a PascalCase JSON string.</summary>
    public static string SerializeCluster(ClusterConfig cluster, bool indented = false)
        => JsonSerializer.Serialize(cluster, indented ? IndentedOptions : Options);

    /// <summary>Deserialize a route from a JSON string (comments and trailing commas allowed).</summary>
    public static RouteConfig? DeserializeRoute(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<RouteConfig>(json, Options);

    /// <summary>Deserialize a cluster from a JSON string (comments and trailing commas allowed).</summary>
    public static ClusterConfig? DeserializeCluster(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ClusterConfig>(json, Options);

    /// <summary>Deserialize a route from a parsed JSON element.</summary>
    public static RouteConfig? DeserializeRoute(JsonElement element)
        => element.ValueKind == JsonValueKind.Undefined ? null : element.Deserialize<RouteConfig>(Options);

    /// <summary>Deserialize a cluster from a parsed JSON element.</summary>
    public static ClusterConfig? DeserializeCluster(JsonElement element)
        => element.ValueKind == JsonValueKind.Undefined ? null : element.Deserialize<ClusterConfig>(Options);

    /// <summary>
    /// Handles <see cref="Version"/> values written as relaxed strings such as "2" by
    /// normalizing them to a parseable form, and writes them back as plain strings.
    /// </summary>
    private sealed class VersionConverter : JsonConverter<Version>
    {
        /// <summary>
        /// Reads the.
        /// </summary>
        /// <param name="reader">The reader.</param>
        /// <param name="typeToConvert">The type to convert.</param>
        /// <param name="options">The options.</param>
        /// <returns>A Version? .</returns>
        public override Version? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var raw = reader.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var normalized = raw.Contains('.') ? raw : raw + ".0";
            return Version.TryParse(normalized, out var version) ? version : null;
        }

        /// <summary>
        /// Writes the.
        /// </summary>
        /// <param name="writer">The writer.</param>
        /// <param name="value">The value.</param>
        /// <param name="options">The options.</param>
        public override void Write(Utf8JsonWriter writer, Version value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }

    /// <summary>
    /// Accepts booleans written either as native JSON booleans or as relaxed strings
    /// (e.g. "true"/"false"/"True"/"False"), matching the canonical YARP appsettings style.
    /// Also tolerates 0/1 numeric forms. Writes back as native JSON booleans.
    /// <para>
    /// Nullable targets additionally accept an empty string as "not specified", which is
    /// what a dashboard form leaves behind when the user never touched the field. Without
    /// this the whole request failed with "Cannot convert string '' to boolean".
    /// </para>
    /// </summary>
    private sealed class LenientBooleanConverterFactory : JsonConverterFactory
    {
        /// <summary>Handles <see cref="bool"/> and <see cref="Nullable{Boolean}"/>.</summary>
        /// <param name="typeToConvert">Target type.</param>
        /// <returns>True when the type is supported.</returns>
        public override bool CanConvert(Type typeToConvert)
            => typeToConvert == typeof(bool) || typeToConvert == typeof(bool?);

        /// <summary>Creates the matching converter.</summary>
        /// <param name="typeToConvert">Target type.</param>
        /// <param name="options">Serializer options.</param>
        /// <returns>A converter instance.</returns>
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
            => typeToConvert == typeof(bool?)
                ? new NullableLenientBooleanConverter()
                : new LenientBooleanConverter();

        /// <summary>
        /// Shared parsing for both converter variants.
        /// </summary>
        /// <param name="reader">Reader positioned on the value.</param>
        /// <returns>The parsed boolean.</returns>
        internal static bool Parse(ref Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.True:
                    return true;
                case JsonTokenType.False:
                    return false;
                case JsonTokenType.String:
                    var raw = reader.GetString();
                    if (bool.TryParse(raw, out var parsed)) return parsed;
                    if (string.Equals(raw, "1", StringComparison.Ordinal)) return true;
                    if (string.Equals(raw, "0", StringComparison.Ordinal)) return false;
                    throw new JsonException(
                        $"Cannot convert string '{raw}' to boolean. Use true/false, or omit the property to keep the default.");
                case JsonTokenType.Number:
                    return reader.GetInt32() != 0;
                default:
                    throw new JsonException($"Cannot convert token '{reader.TokenType}' to boolean.");
            }
        }

        /// <summary>Non-nullable boolean converter.</summary>
        private sealed class LenientBooleanConverter : JsonConverter<bool>
        {
            /// <summary>Reads a boolean.</summary>
            /// <param name="reader">Reader.</param>
            /// <param name="typeToConvert">Target type.</param>
            /// <param name="options">Options.</param>
            /// <returns>The boolean.</returns>
            public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
                => Parse(ref reader);

            /// <summary>Writes a boolean.</summary>
            /// <param name="writer">Writer.</param>
            /// <param name="value">Value.</param>
            /// <param name="options">Options.</param>
            public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
                => writer.WriteBooleanValue(value);
        }

        /// <summary>Nullable boolean converter: blank means "not set".</summary>
        private sealed class NullableLenientBooleanConverter : JsonConverter<bool?>
        {
            /// <summary>Lets <see cref="Read"/> see the null token as well.</summary>
            public override bool HandleNull => true;

            /// <summary>Reads a nullable boolean.</summary>
            /// <param name="reader">Reader.</param>
            /// <param name="typeToConvert">Target type.</param>
            /// <param name="options">Options.</param>
            /// <returns>The boolean, or null when unspecified.</returns>
            public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.Null) return null;

                if (reader.TokenType == JsonTokenType.String)
                {
                    var raw = reader.GetString();
                    if (string.IsNullOrWhiteSpace(raw)) return null;
                    if (string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase)) return null;
                }

                return Parse(ref reader);
            }

            /// <summary>Writes a nullable boolean.</summary>
            /// <param name="writer">Writer.</param>
            /// <param name="value">Value.</param>
            /// <param name="options">Options.</param>
            public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
            {
                if (value.HasValue) writer.WriteBooleanValue(value.Value);
                else writer.WriteNullValue();
            }
        }
    }

    /// <summary>
    /// Parses durations written as "00:00:30", as a bare number of seconds ("30"), or with a
    /// unit suffix ("30s", "5m", "2h", "1d"). A blank value on a nullable target means
    /// "not specified". Without this, a hand-typed timeout failed with a raw format error.
    /// </summary>
    private sealed class LenientTimeSpanConverterFactory : JsonConverterFactory
    {
        private static readonly Regex ShortForm =
            new(@"^(\d+(?:\.\d+)?)\s*([smhd]?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Handles <see cref="TimeSpan"/> and <see cref="Nullable{TimeSpan}"/>.</summary>
        /// <param name="typeToConvert">Target type.</param>
        /// <returns>True when the type is supported.</returns>
        public override bool CanConvert(Type typeToConvert)
            => typeToConvert == typeof(TimeSpan) || typeToConvert == typeof(TimeSpan?);

        /// <summary>Creates the matching converter.</summary>
        /// <param name="typeToConvert">Target type.</param>
        /// <param name="options">Serializer options.</param>
        /// <returns>A converter instance.</returns>
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
            => typeToConvert == typeof(TimeSpan?)
                ? new NullableLenientTimeSpanConverter()
                : new LenientTimeSpanConverter();

        /// <summary>
        /// Shared parsing for both converter variants.
        /// </summary>
        /// <param name="raw">Raw text.</param>
        /// <returns>The parsed duration.</returns>
        internal static TimeSpan Parse(string raw)
        {
            var text = (raw ?? string.Empty).Trim();

            /* Checked first on purpose: TimeSpan.Parse("30") means 30 *days*, but someone
               typing 30 into a timeout box always means seconds. */
            var shortForm = ShortForm.Match(text);
            if (shortForm.Success)
            {
                var amount = double.Parse(shortForm.Groups[1].Value, CultureInfo.InvariantCulture);
                return shortForm.Groups[2].Value.ToLowerInvariant() switch
                {
                    "m" => TimeSpan.FromMinutes(amount),
                    "h" => TimeSpan.FromHours(amount),
                    "d" => TimeSpan.FromDays(amount),
                    _ => TimeSpan.FromSeconds(amount)
                };
            }

            if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span)) return span;

            throw new JsonException($"Cannot convert '{raw}' to a duration. Use 00:00:30 or 30s.");
        }

        /// <summary>Non-nullable duration converter.</summary>
        private sealed class LenientTimeSpanConverter : JsonConverter<TimeSpan>
        {
            /// <summary>Reads a duration.</summary>
            /// <param name="reader">Reader.</param>
            /// <param name="typeToConvert">Target type.</param>
            /// <param name="options">Options.</param>
            /// <returns>The duration.</returns>
            public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.String) return Parse(reader.GetString()!);
                // A bare number is read as seconds, matching Parse("30").
                if (reader.TokenType == JsonTokenType.Number) return TimeSpan.FromSeconds(reader.GetDouble());
                throw new JsonException($"Cannot convert token '{reader.TokenType}' to a duration.");
            }

            /// <summary>Writes a duration in round-trip format.</summary>
            /// <param name="writer">Writer.</param>
            /// <param name="value">Value.</param>
            /// <param name="options">Options.</param>
            public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
                => writer.WriteStringValue(value.ToString("c", CultureInfo.InvariantCulture));
        }

        /// <summary>Nullable duration converter: blank means "not set".</summary>
        private sealed class NullableLenientTimeSpanConverter : JsonConverter<TimeSpan?>
        {
            /// <summary>Lets <see cref="Read"/> see the null token as well.</summary>
            public override bool HandleNull => true;

            /// <summary>Reads a nullable duration.</summary>
            /// <param name="reader">Reader.</param>
            /// <param name="typeToConvert">Target type.</param>
            /// <param name="options">Options.</param>
            /// <returns>The duration, or null when unspecified.</returns>
            public override TimeSpan? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.Null) return null;
                if (reader.TokenType == JsonTokenType.Number) return TimeSpan.FromSeconds(reader.GetDouble());
                if (reader.TokenType != JsonTokenType.String)
                    throw new JsonException($"Cannot convert token '{reader.TokenType}' to a duration.");

                var raw = reader.GetString();
                if (string.IsNullOrWhiteSpace(raw)) return null;
                return Parse(raw);
            }

            /// <summary>Writes a nullable duration.</summary>
            /// <param name="writer">Writer.</param>
            /// <param name="value">Value.</param>
            /// <param name="options">Options.</param>
            public override void Write(Utf8JsonWriter writer, TimeSpan? value, JsonSerializerOptions options)
            {
                if (value.HasValue) writer.WriteStringValue(value.Value.ToString("c", CultureInfo.InvariantCulture));
                else writer.WriteNullValue();
            }
        }
    }

    /// <summary>
    /// Accepts string values written as native JSON booleans, numbers, or nulls, converting them
    /// to their string representation. This mirrors how Microsoft.Extensions.Configuration binds
    /// scalar values into <c>Dictionary&lt;string, string&gt;</c> (used by YARP Transforms and Metadata).
    /// For example, <c>"RequestHeadersCopy": true</c> becomes the string <c>"true"</c>.
    /// Native JSON strings pass through unchanged.
    /// </summary>
    private sealed class LenientStringConverter : JsonConverter<string>
    {
        /// <summary>
        /// Reads the.
        /// </summary>
        /// <param name="reader">The reader.</param>
        /// <param name="typeToConvert">The type to convert.</param>
        /// <param name="options">The options.</param>
        /// <returns>A string? .</returns>
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.True => "true",
                JsonTokenType.False => "false",
                JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString() : reader.GetDouble().ToString(),
                JsonTokenType.Null => null,
                _ => throw new JsonException($"Cannot convert token '{reader.TokenType}' to string.")
            };
        }

        /// <summary>
        /// Writes the.
        /// </summary>
        /// <param name="writer">The writer.</param>
        /// <param name="value">The value.</param>
        /// <param name="options">The options.</param>
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WriteStringValue(value);
    }
}
