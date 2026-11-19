using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;

namespace Aneiang.Yarp.Dashboard.Infrastructure.OpenApi;

/// <summary>
/// Builds an OpenAPI 3.1 document from the endpoints that are actually mapped.
/// </summary>
/// <remarks>
/// <para>
/// The document is generated from <see cref="EndpointDataSource"/> rather than from attributes on a
/// separate contract, for one reason: the dashboard's controllers <em>are</em> the API. A hand-written
/// spec drifts the first time somebody adds an endpoint, and a spec that lies is worse than no spec —
/// it is a spec that makes a third party's integration fail in production.
/// </para>
/// <para>
/// Schemas are derived from the CLR parameter types with a deliberately conservative mapping. Where
/// the mapping cannot know better (open-ended JSON, dictionaries, deep graphs) it emits a permissive
/// schema instead of a precise-looking wrong one.
/// </para>
/// <para>
/// No dependency on <c>Microsoft.OpenApi</c> or Swashbuckle: this document is one JSON object, and a
/// package that pulls in its own versioning of the OpenAPI model is a lot of assembly for that.
/// </para>
/// </remarks>
public sealed class OpenApiDocumentBuilder
{
    /// <summary>OpenAPI version emitted. 3.1 is what the JSON Schema keywords below agree with.</summary>
    public const string OpenApiVersion = "3.1.0";

    private const int MaxSchemaDepth = 4;

    private readonly IEnumerable<EndpointDataSource> _endpointSources;
    private readonly OpenApiBuilderOptions _options;

    /// <summary>Creates a builder over the given endpoint sources.</summary>
    /// <param name="endpointSources">Sources enumerated at request time, so the document follows the app.</param>
    /// <param name="options">Naming and auth-scheme settings.</param>
    public OpenApiDocumentBuilder(IEnumerable<EndpointDataSource> endpointSources, OpenApiBuilderOptions options)
    {
        _endpointSources = endpointSources;
        _options = options;
    }

    /// <summary>Builds the document.</summary>
    public JsonObject Build()
    {
        // Normalized once here so every prefix check below can use a plain string concat. A trailing
        // slash would produce "//api/" and quietly exclude every endpoint.
        _options.BasePath = "/" + _options.BasePath.Trim('/');

        var paths = new JsonObject();
        var schemas = new JsonObject();
        var xml = XmlSummaryCache.TryLoad(_options.DocumentationAssembly);

        // Grouped by path so one path object can hold several operations, which is what the spec wants.
        var operations = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);

        foreach (var (descriptor, template) in EnumerateActions())
        {
            var path = "/" + template.TrimStart('/');

            // Scope by path, not by controller namespace: plugins ship their own controllers under
            // the same prefix, and a reference that silently omits them is a reference that wastes
            // the integrator's afternoon. Page routes are excluded by the /api/ requirement.
            if (!path.StartsWith(_options.BasePath + "/api/", StringComparison.OrdinalIgnoreCase)) continue;

            var methods = descriptor.ActionConstraints?
                .OfType<HttpMethodActionConstraint>()
                .SelectMany(c => c.HttpMethods)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];

            if (methods.Length == 0) continue;

            if (!operations.TryGetValue(path, out var pathItem))
            {
                pathItem = new JsonObject();
                operations[path] = pathItem;
            }

            foreach (var method in methods)
            {
                pathItem[method.ToLowerInvariant()] = BuildOperation(descriptor, path, xml, schemas);
            }
        }

        foreach (var (path, item) in operations) paths[path] = item;

        var document = new JsonObject
        {
            ["openapi"] = OpenApiVersion,
            ["info"] = new JsonObject
            {
                ["title"] = "Aneiang.Yarp 管理 API",
                ["version"] = _options.ProductVersion,
                ["description"] =
                    "由运行中的网关根据已映射的路由生成，不是手写契约：增删接口后本文件会跟着变。\n\n" +
                    "请求体与响应体结构由 CLR 类型推导；无法确定的地方给出宽松 schema，而不是看起来精确的错误 schema。\n\n" +
                    "`422` 之外的所有写操作都会做配置校验，校验失败返回 `400` 并在 `validation.errors` 里列出原因。"
            },
            ["servers"] = new JsonArray
            {
                new JsonObject { ["url"] = _options.BasePath, ["description"] = "本站控制面" }
            },
            ["tags"] = BuildTags(),
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = BuildSecuritySchemes(),
                ["schemas"] = schemas
            },
            ["security"] = new JsonArray
            {
                new JsonObject { [_options.BearerSchemeName] = new JsonArray() },
                new JsonObject { [_options.ApiKeySchemeName] = new JsonArray() }
            }
        };

        return document;
    }

    /// <summary>
    /// Yields each controller action together with the template it is actually reachable at.
    /// </summary>
    /// <remarks>
    /// The template comes from attribute routing when present and falls back to the endpoint's own
    /// route pattern. Both describe the same route in an attribute-routed app; the fallback keeps
    /// convention-routed controllers in the document instead of silently dropping them.
    /// </remarks>
    private IEnumerable<(ControllerActionDescriptor Descriptor, string Template)> EnumerateActions()
    {
        foreach (var source in _endpointSources)
        {
            foreach (var endpoint in source.Endpoints)
            {
                if (endpoint is not RouteEndpoint routeEndpoint) continue;
                var descriptor = routeEndpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
                if (descriptor is null) continue;

                var template = descriptor.AttributeRouteInfo?.Template ?? routeEndpoint.RoutePattern.RawText;
                if (string.IsNullOrEmpty(template)) continue;

                yield return (descriptor, template);
            }
        }
    }

    private JsonObject BuildOperation(
        ControllerActionDescriptor descriptor,
        string path,
        IReadOnlyDictionary<string, string> xml,
        JsonObject schemas)
    {
        var method = descriptor.MethodInfo;
        var summaryKey = XmlSummaryCache.Key(method);
        xml.TryGetValue(summaryKey, out var summary);

        var parameters = new JsonArray();
        var routeParameterNames = RouteParameterNames(path);
        JsonObject? requestBody = null;
        var nullability = new NullabilityInfoContext();

        foreach (var parameter in method.GetParameters())
        {
            // [FromBody] is checked before the service heuristic, and deliberately so: a payload type
            // almost always lives in one of this repository's own namespaces, which is exactly what
            // IsService uses to spot injected services. Testing for a service first silently dropped
            // every body whose model was declared in Aneiang.Yarp.* — nearly all of them.
            var fromBody = parameter.GetCustomAttribute<Microsoft.AspNetCore.Mvc.FromBodyAttribute>() is not null;
            if (fromBody)
            {
                var bodyType = parameter.ParameterType;
                var requestBodyNode = new JsonObject
                {
                    // A body is required unless it is annotated nullable or carries a default: that is
                    // MVC's own rule, and a contract that says "optional" for a required body makes
                    // callers skip it and collect a 400.
                    ["required"] = IsRequiredBody(parameter, nullability),
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject
                        {
                            ["schema"] = SchemaFor(bodyType, schemas, 0, new HashSet<Type>())
                        }
                    }
                };

                // Endpoints that accept raw JSON (the native YARP config shapes) cannot describe
                // themselves from a CLR type. Saying "any JSON" and pointing at the shape is honest;
                // emitting a plausible-looking but invented schema would not be.
                if (typeof(JsonElement).IsAssignableFrom(bodyType) || typeof(JsonNode).IsAssignableFrom(bodyType))
                {
                    requestBodyNode["description"] =
                        "原生 YARP 配置对象，结构见文档「配置参考」。本接口不校验字段名以外的形状，未知字段会被忽略。";
                }

                requestBody = requestBodyNode;
                continue;
            }

            if (IsService(parameter.ParameterType)) continue;

            if (routeParameterNames.Contains(parameter.Name!))
            {
                parameters.Add(ParameterObject(parameter, "path", required: true));
                continue;
            }

            // Anything else that is a scalar is a query parameter. Complex types without [FromBody]
            // are left out rather than guessed at: this API binds everything else from a service.
            if (IsScalar(parameter.ParameterType))
                parameters.Add(ParameterObject(parameter, "query", required: !IsOptional(parameter)));
        }

        var operation = new JsonObject
        {
            ["tags"] = new JsonArray(descriptor.ControllerName),
            ["summary"] = summary ?? Humanize(descriptor.ActionName),
            ["operationId"] = descriptor.ControllerName + "_" + descriptor.ActionName,
            ["responses"] = BuildResponses()
        };

        if (parameters.Count > 0) operation["parameters"] = parameters;
        if (requestBody is not null) operation["requestBody"] = requestBody;

        return operation;
    }

    private JsonObject ParameterObject(ParameterInfo parameter, string location, bool required)
    {
        var schema = SchemaFor(parameter.ParameterType, null, 0, null);
        return new JsonObject
        {
            ["name"] = parameter.Name,
            ["in"] = location,
            ["required"] = location == "path" || required,
            ["schema"] = schema
        };
    }

    private static JsonObject BuildResponses() => new()
    {
        ["200"] = new JsonObject { ["description"] = "成功。控制面统一返回 `{ code, data }`，`code` 与 HTTP 状态码一致。" },
        ["400"] = new JsonObject { ["description"] = "请求不合法或配置校验失败。" },
        ["401"] = new JsonObject { ["description"] = "未认证。" },
        ["403"] = new JsonObject
        {
            ["description"] = "角色不允许该操作。`reason` 为 `read-only`（只读账号）或 `admin-required`（需要管理员）。"
        }
    };

    private JsonArray BuildTags()
    {
        var tags = new JsonArray();
        foreach (var name in _options.TagDescriptions.Keys.OrderBy(k => k, StringComparer.Ordinal))
            tags.Add(new JsonObject { ["name"] = name, ["description"] = _options.TagDescriptions[name] });
        return tags;
    }

    private JsonObject BuildSecuritySchemes() => new()
    {
        [_options.BearerSchemeName] = new JsonObject
        {
            ["type"] = "http",
            ["scheme"] = "bearer",
            ["bearerFormat"] = "JWT",
            ["description"] = "`POST {prefix}/login` 返回的 token。token 里带角色，降权需要重新登录。"
        },
        [_options.ApiKeySchemeName] = new JsonObject
        {
            ["type"] = "apiKey",
            ["in"] = "header",
            ["name"] = _options.ApiKeyHeaderName,
            ["description"] = "仅在 `Gateway:Dashboard:Auth:AuthMode = ApiKey` 时可用；这种模式下没有账号，角色视为隐式管理员。"
        }
    };

    /// <summary>
    /// Maps a CLR type to a JSON Schema. Returns a permissive schema rather than throwing: a single
    /// unmappable property must not take the whole document down.
    /// </summary>
    private JsonObject? SchemaFor(Type type, JsonObject? components, int depth, HashSet<Type>? visiting)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        var nullable = underlying is not null || !type.IsValueType;
        if (underlying is not null) type = underlying;

        if (type == typeof(string) || type == typeof(char) || type == typeof(Guid) || type == typeof(Uri))
            return WithNullable(new JsonObject { ["type"] = "string" }, nullable);
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
            return WithNullable(new JsonObject { ["type"] = "string", ["format"] = "date-time" }, nullable);
        if (type == typeof(TimeSpan))
            return WithNullable(new JsonObject { ["type"] = "string", ["format"] = "duration" }, nullable);
        if (type == typeof(bool))
            return WithNullable(new JsonObject { ["type"] = "boolean" }, nullable);
        if (type == typeof(int) || type == typeof(short) || type == typeof(byte))
            return WithNullable(new JsonObject { ["type"] = "integer" }, nullable);
        if (type == typeof(long))
            return WithNullable(new JsonObject { ["type"] = "integer", ["format"] = "int64" }, nullable);
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
            return WithNullable(new JsonObject { ["type"] = "number" }, nullable);
        if (type == typeof(object) || typeof(JsonElement).IsAssignableFrom(type) || typeof(JsonNode).IsAssignableFrom(type))
            return new JsonObject();  // Anything goes — better empty than wrong.

        if (type.IsEnum)
        {
            var values = new JsonArray();
            foreach (var value in Enum.GetNames(type)) values.Add(value);
            return WithNullable(new JsonObject { ["type"] = "string", ["enum"] = values }, nullable);
        }

        var dictionaryValue = DictionaryValueType(type);
        if (dictionaryValue is not null)
        {
            return WithNullable(new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = SchemaFor(dictionaryValue, components, depth + 1, visiting) ?? new JsonObject()
            }, nullable);
        }

        var element = ElementType(type);
        if (element is not null)
        {
            return WithNullable(new JsonObject
            {
                ["type"] = "array",
                ["items"] = SchemaFor(element, components, depth + 1, visiting) ?? new JsonObject()
            }, nullable);
        }

        if (depth >= MaxSchemaDepth || type.IsPrimitive || type.IsPointer)
            return new JsonObject();

        visiting ??= [];
        if (!visiting.Add(type)) return new JsonObject();  // Cycle: stop, do not recurse forever.

        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0) continue;
            if (!property.CanRead) continue;

            var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            var propertySchema = SchemaFor(property.PropertyType, components, depth + 1, visiting);
            if (propertySchema is null) continue;

            // The controllers use Newtonsoft-style nullability in places; a reference type without a
            // non-nullable annotation is not required, which is the safe reading for a published contract.
            var isRequired = property.PropertyType.IsValueType
                             && Nullable.GetUnderlyingType(property.PropertyType) is null;
            if (isRequired) required.Add(name);

            properties[name] = propertySchema;
        }

        visiting.Remove(type);

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) schema["required"] = required;

        // Nested objects are inlined rather than pushed into components.schemas: the graph here is
        // shallow, and inlining keeps the document readable in a plain JSON viewer.
        return schema;
    }

    private static JsonObject WithNullable(JsonObject schema, bool nullable)
    {
        if (nullable) schema["type"] = new JsonArray(schema["type"]!.GetValue<string>(), "null");
        return schema;
    }

    private static Type? DictionaryValueType(Type type)
    {
        foreach (var candidate in Interfaces(type))
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>))
                return candidate.GetGenericArguments()[1];
        }
        return null;
    }

    private static Type? ElementType(Type type)
    {
        if (type.IsArray) return type.GetElementType();
        foreach (var candidate in Interfaces(type))
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return candidate.GetGenericArguments()[0];
        }
        return null;
    }

    private static IEnumerable<Type> Interfaces(Type type)
    {
        if (type.IsInterface) yield return type;
        foreach (var candidate in type.GetInterfaces()) yield return candidate;
    }

    private static bool IsScalar(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsPrimitive || underlying.IsEnum
            || underlying == typeof(string) || underlying == typeof(Guid)
            || underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset);
    }

    private static bool IsOptional(ParameterInfo parameter)
        => parameter.HasDefaultValue || parameter.IsOptional
           || Nullable.GetUnderlyingType(parameter.ParameterType) is not null
           || !parameter.ParameterType.IsValueType;

    /// <summary>
    /// Whether a <c>[FromBody]</c> parameter must be sent. Mirrors MVC: a default value or a
    /// nullable annotation makes it optional, and a nullable-oblivious type counts as required.
    /// </summary>
    private static bool IsRequiredBody(ParameterInfo parameter, NullabilityInfoContext context)
    {
        if (parameter.HasDefaultValue || parameter.IsOptional) return false;
        if (parameter.ParameterType.IsValueType)
            return Nullable.GetUnderlyingType(parameter.ParameterType) is null;

        return context.Create(parameter).WriteState != NullabilityState.Nullable;
    }

    private static bool IsService(Type type)
        => type.Namespace?.StartsWith("Microsoft.Extensions", StringComparison.Ordinal) == true
           || type.Namespace?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true
           || type.Namespace?.StartsWith("Aneiang.Yarp", StringComparison.Ordinal) == true
           || type == typeof(CancellationToken);

    private static HashSet<string> RouteParameterNames(string path)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length < 3 || segment[0] != '{' || segment[^1] != '}') continue;
            var name = segment[1..^1].TrimStart('*', '?').Split(':')[0];
            names.Add(name);
        }
        return names;
    }

    private static string Humanize(string actionName)
    {
        // "GetSystemInfo" → "Get System Info". A summary that is only the method name is not a
        // summary, but it is still better than an empty string in a rendered document.
        var builder = new System.Text.StringBuilder(actionName.Length + 4);
        for (var i = 0; i < actionName.Length; i++)
        {
            if (i > 0 && char.IsUpper(actionName[i]) && !char.IsUpper(actionName[i - 1])) builder.Append(' ');
            builder.Append(actionName[i]);
        }
        return builder.ToString();
    }
}

/// <summary>Settings for <see cref="OpenApiDocumentBuilder"/>.</summary>
public sealed class OpenApiBuilderOptions
{
    /// <summary>Path the document describes, e.g. <c>/apigateway</c>.</summary>
    public string BasePath { get; set; } = string.Empty;

    /// <summary>Product version, shown as the document version.</summary>
    public string ProductVersion { get; set; } = "0.0.0";

    /// <summary>Header carrying the API key in ApiKey mode.</summary>
    public string ApiKeyHeaderName { get; set; } = "X-Api-Key";

    /// <summary>Name of the bearer scheme in the document.</summary>
    public string BearerSchemeName { get; set; } = "bearerAuth";

    /// <summary>Name of the api-key scheme in the document.</summary>
    public string ApiKeySchemeName { get; set; } = "apiKeyAuth";

    /// <summary>Assembly whose XML documentation supplies operation summaries.</summary>
    public Assembly? DocumentationAssembly { get; set; }

    /// <summary>Tag descriptions, keyed by controller name.</summary>
    public Dictionary<string, string> TagDescriptions { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Reads <c>&lt;summary&gt;</c> text out of the XML documentation file that ships next to the assembly.
/// </summary>
/// <remarks>
/// The controllers already document themselves for IntelliSense; reusing that text is the only way a
/// generated reference has summaries that stay true. When the XML file is missing (trimmed
/// deployments, single-file publish) the document still builds — every operation falls back to a
/// humanized action name.
/// </remarks>
internal static class XmlSummaryCache
{
    private static readonly object Gate = new();
    private static Assembly? _loadedFor;
    private static Dictionary<string, string> _cache = new(StringComparer.Ordinal);

    /// <summary>Member id used by the C# compiler, e.g. <c>M:Ns.Type.Method(System.String)</c>.</summary>
    public static string Key(MethodInfo method)
    {
        var parameters = string.Join(",", method.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name));
        var type = method.DeclaringType?.FullName ?? method.DeclaringType?.Name;
        return $"M:{type}.{method.Name}{(parameters.Length > 0 ? $"({parameters})" : string.Empty)}";
    }

    public static IReadOnlyDictionary<string, string> TryLoad(Assembly? assembly)
    {
        if (assembly is null) return _cache;

        lock (Gate)
        {
            if (ReferenceEquals(_loadedFor, assembly)) return _cache;
            _loadedFor = assembly;

            var path = Path.ChangeExtension(assembly.Location, ".xml");
            if (!File.Exists(path))
            {
                _cache = new Dictionary<string, string>(StringComparer.Ordinal);
                return _cache;
            }

            try
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var member in XDocument.Load(path).Descendants("member"))
                {
                    var name = member.Attribute("name")?.Value;
                    var summary = member.Element("summary")?.Value;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(summary)) continue;
                    map[name!] = Normalize(summary);
                }
                _cache = map;
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
            {
                // Documentation is a nicety. A locked or corrupt file must not fail the request.
                _cache = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            return _cache;
        }
    }

    private static string Normalize(string summary)
    {
        var lines = summary.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);
        return string.Join(" ", lines);
    }
}
