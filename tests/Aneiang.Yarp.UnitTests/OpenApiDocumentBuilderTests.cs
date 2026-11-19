using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aneiang.Yarp.Dashboard.Infrastructure.OpenApi;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Primitives;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// The generated document is a published contract, so these tests assert the shape a third party
/// depends on: which endpoints appear, where each parameter sits, and whether a body is described
/// or honestly left open.
/// </summary>
public class OpenApiDocumentBuilderTests
{
    private const string BasePath = "/apigateway";

    // Stand-in for a real controller: the builder reads only reflection metadata and route
    // templates, so what follows is the entire input the document is generated from.
    private sealed class SampleController
    {
        public void GetRoutes() { }

        public void SaveRoute(string routeId, [FromBody] RouteModel model) { }

        public void SaveRaw([FromBody] JsonElement body) { }

        public void ListRoutes(Guid? clusterId, int limit, CancellationToken cancellationToken) { }

        public void ListAll(RouteModel model) { }

        public void Page() { }
    }

    private sealed class RouteModel
    {
        public string? Name { get; set; }

        public int Version { get; set; }

        public RouteMode Mode { get; set; }

        public List<string>? Hosts { get; set; }

        public Dictionary<string, string> Metadata { get; set; } = new();

        /// <summary>Self-reference: the cycle guard has to hold, not just the depth guard.</summary>
        public RouteModel? Self { get; set; }
    }

    private enum RouteMode
    {
        Active,
        Passive
    }

    private sealed class FakeEndpointSource(params Endpoint[] endpoints) : EndpointDataSource
    {
        public override IReadOnlyList<Endpoint> Endpoints { get; } = endpoints;

        public override IChangeToken GetChangeToken() => new CancellationChangeToken(CancellationToken.None);
    }

    private static ControllerActionDescriptor Descriptor(string actionName) => new()
    {
        ControllerName = "Sample",
        ActionName = actionName,
        ControllerTypeInfo = typeof(SampleController).GetTypeInfo(),
        MethodInfo = typeof(SampleController).GetMethod(actionName)!
    };

    private static RouteEndpoint Action(string template, string actionName, params string[] methods)
    {
        // AttributeRouteInfo and ActionConstraints are what MVC fills in for an attribute-routed
        // action; the builder reads the descriptor, not the endpoint metadata.
        var descriptor = Descriptor(actionName);
        descriptor.AttributeRouteInfo = new AttributeRouteInfo { Template = template };
        if (methods.Length > 0) descriptor.ActionConstraints = [new HttpMethodActionConstraint(methods)];

        var metadata = new List<object> { descriptor };
        if (methods.Length > 0) metadata.Add(new HttpMethodActionConstraint(methods));

        return new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(template),
            0,
            new EndpointMetadataCollection(metadata),
            $"SampleController.{actionName}");
    }

    private static OpenApiBuilderOptions Options(string basePath = BasePath) => new()
    {
        BasePath = basePath,
        ProductVersion = "2.3.0.40",
        ApiKeyHeaderName = "X-Api-Key",
        TagDescriptions = { ["Sample"] = "示例控制器" }
    };

    private static JsonObject Build(params Endpoint[] endpoints) =>
        new OpenApiDocumentBuilder([new FakeEndpointSource(endpoints)], Options()).Build();

    private static JsonObject Operation(JsonObject document, string path, string method) =>
        document["paths"]!.AsObject()[path]!.AsObject()[method]!.AsObject();

    private static JsonObject[] Parameters(JsonObject operation) =>
        operation["parameters"] is JsonArray array
            ? array.Select(p => p!.AsObject()).ToArray()
            : [];

    private static JsonObject BodySchema(JsonObject operation) =>
        operation["requestBody"]!.AsObject()["content"]!.AsObject()["application/json"]!.AsObject()["schema"]!.AsObject();

    private static JsonObject BodyProperties(JsonObject operation) =>
        BodySchema(operation)["properties"]!.AsObject();

    private static string[] Strings(JsonNode? node) =>
        node!.AsArray().Select(v => v!.GetValue<string>()).ToArray();

    /// <summary>
    /// Reads a schema's primary type. Nullable primitives emit <c>["string","null"]</c> — valid in
    /// OpenAPI 3.1 / JSON Schema 2020-12 — so the type may be either a value or a list.
    /// </summary>
    private static string SchemaType(JsonObject schema)
    {
        var node = schema["type"];
        if (node is JsonValue value) return value.GetValue<string>();
        return Strings(node).First(name => name != "null");
    }

    // ── scoping ───────────────────────────────────────────────────────────────

    [Fact]
    public void Build_IncludesOnlyEndpointsUnderTheApiPrefix()
    {
        var document = Build(
            Action("/apigateway/api/routes", nameof(SampleController.GetRoutes), "GET"),
            Action("/apigateway/routes", nameof(SampleController.Page), "GET"),
            Action("/otherapi/api/routes", nameof(SampleController.Page), "GET"));

        var paths = document["paths"]!.AsObject();
        Assert.Single(paths);
        Assert.True(paths.ContainsKey("/apigateway/api/routes"));
    }

    [Fact]
    public void Build_NormalizesBasePathWithTrailingSlash()
    {
        // A trailing slash would produce "//api/", which quietly excludes every endpoint.
        var document = new OpenApiDocumentBuilder(
            [new FakeEndpointSource(Action("/apigateway/api/routes", nameof(SampleController.GetRoutes), "GET"))],
            Options("/apigateway/")).Build();

        Assert.Single(document["paths"]!.AsObject());
        Assert.Equal("/apigateway", document["servers"]!.AsArray()[0]!.AsObject()["url"]!.GetValue<string>());
    }

    [Fact]
    public void Build_GroupsVerbsUnderOnePathItem()
    {
        var document = Build(
            Action("/apigateway/api/routes", nameof(SampleController.GetRoutes), "GET", "HEAD"),
            Action("/apigateway/api/routes/{routeId}", nameof(SampleController.SaveRoute), "PUT"));

        var collection = document["paths"]!.AsObject()["/apigateway/api/routes"]!.AsObject();
        Assert.True(collection.ContainsKey("get"));
        Assert.True(collection.ContainsKey("head"));
        Assert.Equal(2, document["paths"]!.AsObject().Count);
    }

    [Fact]
    public void Build_SkipsEndpointsWithoutAVerbConstraint()
    {
        // No HttpMethodActionConstraint means the endpoint answers every verb. Inventing "get"
        // would be a wrong contract, so it is omitted.
        var endpoint = new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/apigateway/api/ignored"),
            0,
            new EndpointMetadataCollection(Descriptor(nameof(SampleController.GetRoutes))),
            "ignored");

        Assert.Empty(Build(endpoint)["paths"]!.AsObject());
    }

    [Fact]
    public void Build_IgnoresEndpointsWithoutControllerMetadata()
    {
        var endpoint = new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/apigateway/api/plain"),
            0,
            new EndpointMetadataCollection(Array.Empty<object>()),
            "plain");

        Assert.Empty(Build(endpoint)["paths"]!.AsObject());
    }

    // ── operation metadata ────────────────────────────────────────────────────

    [Fact]
    public void Build_FallsBackToHumanizedNameWhenNoXmlDocumentation()
    {
        // DocumentationAssembly is null here, which is the trimmed / single-file deployment case.
        var document = Build(Action("/apigateway/api/routes", nameof(SampleController.GetRoutes), "GET"));
        var operation = Operation(document, "/apigateway/api/routes", "get");

        Assert.Equal("Get Routes", operation["summary"]!.GetValue<string>());
        Assert.Equal("Sample_GetRoutes", operation["operationId"]!.GetValue<string>());
    }

    [Fact]
    public void Build_EmitsSecuritySchemesAndTags()
    {
        var document = Build(Action("/apigateway/api/routes", nameof(SampleController.GetRoutes), "GET"));

        Assert.Equal("3.1.0", document["openapi"]!.GetValue<string>());
        Assert.Equal("2.3.0.40", document["info"]!.AsObject()["version"]!.GetValue<string>());

        var schemes = document["components"]!.AsObject()["securitySchemes"]!.AsObject();
        Assert.Equal("X-Api-Key", schemes["apiKeyAuth"]!.AsObject()["name"]!.GetValue<string>());
        Assert.Equal("bearer", schemes["bearerAuth"]!.AsObject()["scheme"]!.GetValue<string>());

        var tag = document["tags"]!.AsArray()[0]!.AsObject();
        Assert.Equal("Sample", tag["name"]!.GetValue<string>());
        Assert.Equal("示例控制器", tag["description"]!.GetValue<string>());
    }

    [Fact]
    public void Build_DocumentsTheRoleRefusalReason()
    {
        var responses = Operation(
            Build(Action("/apigateway/api/routes/{routeId}", nameof(SampleController.SaveRoute), "PUT")),
            "/apigateway/api/routes/{routeId}",
            "put")["responses"]!.AsObject();

        Assert.Contains("admin-required", responses["403"]!.AsObject()["description"]!.GetValue<string>());
    }

    // ── parameters ────────────────────────────────────────────────────────────

    [Fact]
    public void Build_MapsRouteTemplateParametersToPathParameters()
    {
        var routeId = Parameters(Operation(
                Build(Action("/apigateway/api/routes/{routeId}", nameof(SampleController.SaveRoute), "PUT")),
                "/apigateway/api/routes/{routeId}",
                "put"))
            .Single(p => p["name"]!.GetValue<string>() == "routeId");

        Assert.Equal("path", routeId["in"]!.GetValue<string>());
        Assert.True(routeId["required"]!.GetValue<bool>());
    }

    [Fact]
    public void Build_MapsScalarsToQueryParametersWithCorrectRequiredFlag()
    {
        var parameters = Parameters(Operation(
            Build(Action("/apigateway/api/routes", nameof(SampleController.ListRoutes), "GET")),
            "/apigateway/api/routes",
            "get"));

        var clusterId = parameters.Single(p => p["name"]!.GetValue<string>() == "clusterId");
        Assert.Equal("query", clusterId["in"]!.GetValue<string>());
        Assert.False(clusterId["required"]!.GetValue<bool>());

        // An int parameter is required unless it is nullable or carries a default.
        var limit = parameters.Single(p => p["name"]!.GetValue<string>() == "limit");
        Assert.True(limit["required"]!.GetValue<bool>());
        Assert.Equal("integer", limit["schema"]!.AsObject()["type"]!.GetValue<string>());
    }

    [Fact]
    public void Build_SkipsCancellationTokenAndComplexParametersWithoutFromBody()
    {
        var operation = Operation(
            Build(Action("/apigateway/api/routes", nameof(SampleController.ListRoutes), "GET")),
            "/apigateway/api/routes",
            "get");

        var names = Parameters(operation).Select(p => p["name"]!.GetValue<string>()).ToArray();
        Assert.Contains("clusterId", names);
        Assert.DoesNotContain("cancellationToken", names);
    }

    [Fact]
    public void Build_DropsComplexParametersThatAreNotBodies()
    {
        // ListAll(RouteModel model) — a complex type without [FromBody] binds from a service in
        // this API, so it is left out rather than guessed at.
        var operation = Operation(
            Build(Action("/apigateway/api/routes", nameof(SampleController.ListAll), "GET")),
            "/apigateway/api/routes",
            "get");

        Assert.False(operation.ContainsKey("parameters"));
        Assert.False(operation.ContainsKey("requestBody"));
    }

    // ── request bodies ────────────────────────────────────────────────────────

    [Fact]
    public void Build_DescribesBodyFromClrType()
    {
        var operation = Operation(
            Build(Action("/apigateway/api/routes/{routeId}", nameof(SampleController.SaveRoute), "PUT")),
            "/apigateway/api/routes/{routeId}",
            "put");

        Assert.True(operation["requestBody"]!.AsObject()["required"]!.GetValue<bool>());
        Assert.Equal("object", SchemaType(BodySchema(operation)));

        var properties = BodyProperties(operation);
        Assert.True(properties.ContainsKey("name"));
        Assert.True(properties.ContainsKey("version"));
        Assert.True(properties.ContainsKey("mode"));

        // Enum names, not the underlying integers — an integrator sends the name.
        Assert.Contains("Active", Strings(properties["mode"]!.AsObject()["enum"]));

        // Only value-type properties without a nullable annotation are required.
        var required = Strings(BodySchema(operation)["required"]);
        Assert.Contains("version", required);
        Assert.Contains("mode", required);
        Assert.DoesNotContain("name", required);
    }

    [Fact]
    public void Build_MapsCollectionsAndDictionaries()
    {
        var properties = BodyProperties(Operation(
            Build(Action("/apigateway/api/routes/{routeId}", nameof(SampleController.SaveRoute), "PUT")),
            "/apigateway/api/routes/{routeId}",
            "put"));

        Assert.Equal("array", SchemaType(properties["hosts"]!.AsObject()));
        Assert.Equal("string", SchemaType(properties["hosts"]!.AsObject()["items"]!.AsObject()));

        Assert.Equal("object", SchemaType(properties["metadata"]!.AsObject()));
        Assert.Equal("string", SchemaType(properties["metadata"]!.AsObject()["additionalProperties"]!.AsObject()));

        // A nullable property is emitted as ["array","null"] rather than dropped, which is what
        // OpenAPI 3.1 expects for "may be absent or null".
        Assert.Equal(["array", "null"], Strings(properties["hosts"]!.AsObject()["type"]));
    }

    [Fact]
    public void Build_EmitsSelfReferencingPropertiesWithoutRecursing()
    {
        // The cycle guard returns an empty schema instead of recursing: "anything" is a better
        // answer than a stack overflow, and the document stays finite.
        var properties = BodyProperties(Operation(
            Build(Action("/apigateway/api/routes/{routeId}", nameof(SampleController.SaveRoute), "PUT")),
            "/apigateway/api/routes/{routeId}",
            "put"));

        Assert.True(properties.ContainsKey("self"));
        Assert.Empty(properties["self"]!.AsObject());
    }

    [Fact]
    public void Build_LeavesRawJsonBodiesOpenAndExplainsWhy()
    {
        var operation = Operation(
            Build(Action("/apigateway/api/raw", nameof(SampleController.SaveRaw), "POST")),
            "/apigateway/api/raw",
            "post");

        Assert.Contains("原生 YARP 配置对象", operation["requestBody"]!.AsObject()["description"]!.GetValue<string>());

        // No "type": the schema is deliberately empty rather than a plausible-looking invention.
        Assert.Empty(BodySchema(operation));
    }

    [Fact]
    public void Build_KeepsPayloadTypesDeclaredInOwnNamespaces()
    {
        // Regression guard: [FromBody] is resolved before the injected-service heuristic. Checking
        // for a service first dropped every body whose model lived in an Aneiang.Yarp.* namespace,
        // which is nearly all of them.
        var document = Build(Action("/apigateway/api/routes/{routeId}", nameof(SampleController.SaveRoute), "PUT"));

        // RouteModel is declared in Aneiang.Yarp.UnitTests — a namespace the heuristic matches.
        Assert.True(Operation(document, "/apigateway/api/routes/{routeId}", "put").ContainsKey("requestBody"));
    }

    // ── robustness ────────────────────────────────────────────────────────────

    [Fact]
    public void Build_ProducesValidJson()
    {
        var document = Build(
            Action("/apigateway/api/routes", nameof(SampleController.GetRoutes), "GET"),
            Action("/apigateway/api/routes/{routeId}", nameof(SampleController.SaveRoute), "PUT"));

        // The page and every third-party client parse this with a plain JSON parser, so it has to
        // round-trip through one.
        var json = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var parsed = JsonNode.Parse(json)!;
        Assert.Equal(2, parsed["paths"]!.AsObject().Count);
    }
}
