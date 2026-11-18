using Aneiang.Yarp.Dashboard.Infrastructure.Policies;
using Aneiang.Yarp.Dashboard.Modules.GatewayConfig.Services;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Pre-flight validation is the difference between "the save failed and told me which
/// field" and "the save succeeded and the first real request 500s". These cases pin the
/// checks that used to be missing entirely.
///
/// YARP's config types are init-only records, so every case builds a complete object
/// through these helpers rather than mutating one afterwards.
/// </summary>
public class ConfigurationValidatorTests
{
    private static ConfigurationValidator Create(
        FakeDynamicConfig config,
        FakePolicyCatalog? policies = null)
        => new(config, policies ?? new FakePolicyCatalog());

    private static RouteConfig Route(
        string id = "r1",
        string? clusterId = "c1",
        string? path = "/api/{**catch-all}",
        string[]? hosts = null,
        string[]? methods = null,
        RouteHeader[]? headers = null,
        string? authorizationPolicy = null,
        string? corsPolicy = null,
        string? rateLimiterPolicy = null,
        string? outputCachePolicy = null,
        TimeSpan? timeout = null,
        string? timeoutPolicy = null,
        Dictionary<string, string>? metadata = null)
        => new()
        {
            RouteId = id,
            ClusterId = clusterId,
            Match = new RouteMatch { Path = path, Hosts = hosts, Methods = methods, Headers = headers },
            AuthorizationPolicy = authorizationPolicy,
            CorsPolicy = corsPolicy,
            RateLimiterPolicy = rateLimiterPolicy,
            OutputCachePolicy = outputCachePolicy,
            Timeout = timeout,
            TimeoutPolicy = timeoutPolicy,
            Metadata = metadata
        };

    private static ClusterConfig Cluster(
        string id = "c1",
        string? address = "http://localhost:5000",
        string? loadBalancingPolicy = null,
        SessionAffinityConfig? sessionAffinity = null,
        HealthCheckConfig? healthCheck = null,
        HttpClientConfig? httpClient = null,
        ForwarderRequestConfig? httpRequest = null)
        => new()
        {
            ClusterId = id,
            Destinations = address == null
                ? null
                : new Dictionary<string, DestinationConfig> { ["d1"] = new() { Address = address } },
            LoadBalancingPolicy = loadBalancingPolicy,
            SessionAffinity = sessionAffinity,
            HealthCheck = healthCheck,
            HttpClient = httpClient,
            HttpRequest = httpRequest
        };

    private static bool HasError(ConfigValidationReport report, string field)
        => report.Errors.Any(e => e.Field == field);

    private static bool HasWarning(ConfigValidationReport report, string field)
        => report.Warnings.Any(w => w.Field == field);

    /// <summary>Builds a cluster whose destinations carry the canary weight/tag metadata.</summary>
    private static ClusterConfig WeightedCluster(
        string loadBalancingPolicy,
        params (string Id, string? Weight, string? Tag)[] destinations)
    {
        var map = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal);

        foreach (var (id, weight, tag) in destinations)
        {
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            if (weight is not null)
                metadata["Weight"] = weight;
            if (tag is not null)
                metadata["Tag"] = tag;

            map[id] = new DestinationConfig
            {
                Address = $"http://{id}.local",
                Metadata = metadata
            };
        }

        return new ClusterConfig
        {
            ClusterId = "c1",
            Destinations = map,
            LoadBalancingPolicy = loadBalancingPolicy
        };
    }

    // ---------- route: identity and match ----------

    [Fact]
    public async Task ValidRoute_Passes()
    {
        var report = await Create(new FakeDynamicConfig()).ValidateRouteAsync(Route(), true);

        Assert.True(report.Ok);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public async Task Route_WithoutId_IsRejected()
    {
        var report = await Create(new FakeDynamicConfig()).ValidateRouteAsync(Route(id: ""), true);

        Assert.False(report.Ok);
        Assert.True(HasError(report, "RouteId"));
    }

    [Fact]
    public async Task Route_WithoutCluster_IsRejected()
    {
        var report = await Create(new FakeDynamicConfig()).ValidateRouteAsync(Route(clusterId: null), true);

        Assert.True(HasError(report, "ClusterId"));
    }

    [Fact]
    public async Task Route_WithNeitherPathNorHosts_IsRejected()
    {
        // Without at least one of them YARP builds a route that can never be matched —
        // it appears in the list, looks healthy, and forwards nothing.
        var report = await Create(new FakeDynamicConfig()).ValidateRouteAsync(Route(path: null), true);

        Assert.True(HasError(report, "Match"));
    }

    [Fact]
    public async Task Route_WithHostOnly_IsAccepted()
    {
        var report = await Create(new FakeDynamicConfig())
            .ValidateRouteAsync(Route(path: null, hosts: new[] { "api.example.com" }), true);

        Assert.True(report.Ok);
    }

    [Fact]
    public async Task Route_WithEmptyMethodList_IsRejected()
    {
        var report = await Create(new FakeDynamicConfig())
            .ValidateRouteAsync(Route(methods: Array.Empty<string>()), true);

        Assert.True(HasError(report, "Match.Methods"));
    }

    [Fact]
    public async Task Route_WithMethods_IsAccepted()
    {
        var report = await Create(new FakeDynamicConfig())
            .ValidateRouteAsync(Route(methods: new[] { "GET", "POST" }), true);

        Assert.True(report.Ok);
    }

    [Fact]
    public async Task Route_HeaderWithoutName_IsRejected()
    {
        var headers = new[] { new RouteHeader { Name = "", Values = new[] { "x" } } };

        var report = await Create(new FakeDynamicConfig())
            .ValidateRouteAsync(Route(headers: headers), true);

        Assert.True(HasError(report, "Match.Headers[0].Name"));
    }

    [Fact]
    public async Task Route_HeaderWithName_IsAccepted()
    {
        var headers = new[] { new RouteHeader { Name = "X-Tenant", Values = new[] { "a" } } };

        var report = await Create(new FakeDynamicConfig())
            .ValidateRouteAsync(Route(headers: headers), true);

        Assert.True(report.Ok);
    }

    // ---------- route: timeout ----------

    [Fact]
    public async Task Route_WithBothTimeoutAndTimeoutPolicy_IsRejected()
    {
        var route = Route(timeout: TimeSpan.FromSeconds(5), timeoutPolicy: "some-policy");

        var report = await Create(new FakeDynamicConfig()).ValidateRouteAsync(route, true);

        Assert.True(HasError(report, "TimeoutPolicy"));
    }

    [Fact]
    public async Task Route_WithNonPositiveTimeout_IsRejected()
    {
        var report = await Create(new FakeDynamicConfig())
            .ValidateRouteAsync(Route(timeout: TimeSpan.Zero), true);

        Assert.True(HasError(report, "Timeout"));
    }

    [Fact]
    public async Task Route_WithPositiveTimeout_IsAccepted()
    {
        var report = await Create(new FakeDynamicConfig())
            .ValidateRouteAsync(Route(timeout: TimeSpan.FromSeconds(30)), true);

        Assert.True(report.Ok);
    }

    // ---------- route: cluster reference ----------

    [Fact]
    public async Task Route_ToMissingCluster_IsRejected_WithHint()
    {
        var report = await Create(new FakeDynamicConfig()).ValidateRouteAsync(Route(clusterId: "ghost"), false);

        var issue = Assert.Single(report.Errors, e => e.Field == "ClusterId");
        Assert.Contains("ghost", issue.Message);
        Assert.NotNull(issue.Hint);
    }

    [Fact]
    public async Task Route_ToClusterCreatedInSamePayload_IsAccepted()
    {
        // Creating a route and its cluster in one submit is the normal path; the
        // "cluster does not exist" check must not fire on it.
        var report = await Create(new FakeDynamicConfig()).ValidateRouteAsync(Route(clusterId: "ghost"), true);

        Assert.False(HasError(report, "ClusterId"));
    }

    [Fact]
    public async Task Route_ToExistingCluster_IsAccepted()
    {
        var config = new FakeDynamicConfig();
        config.Clusters.Add(Cluster("c1"));

        var report = await Create(config).ValidateRouteAsync(Route(clusterId: "c1"), false);

        Assert.True(report.Ok);
    }

    // ---------- route: policy references ----------

    [Fact]
    public async Task Route_WithUnknownAuthorizationPolicy_IsRejected()
    {
        var policies = new FakePolicyCatalog()
            .Answer(GatewayPolicyKind.Authorization, "typo-policy", PolicyResolutionStatus.Unknown);

        var route = Route(authorizationPolicy: "typo-policy");

        var report = await Create(new FakeDynamicConfig(), policies).ValidateRouteAsync(route, true);

        Assert.False(report.Ok);
        Assert.True(HasError(report, "AuthorizationPolicy"));
    }

    [Fact]
    public async Task Route_WithUnverifiableRateLimiterPolicy_OnlyWarns()
    {
        // Rate limiter policy names cannot be enumerated, so an unknown name must not
        // block the save — it would break every custom host-registered policy.
        var policies = new FakePolicyCatalog()
            .Answer(GatewayPolicyKind.RateLimiter, "my-custom", PolicyResolutionStatus.Unverifiable);

        var report = await Create(new FakeDynamicConfig(), policies)
            .ValidateRouteAsync(Route(rateLimiterPolicy: "my-custom"), true);

        Assert.True(report.Ok);
        Assert.True(HasWarning(report, "RateLimiterPolicy"));
    }

    [Fact]
    public async Task Route_WithEmptyPolicyFields_IsAccepted()
    {
        var policies = new FakePolicyCatalog();
        var route = Route(
            authorizationPolicy: "",
            corsPolicy: "",
            rateLimiterPolicy: "",
            outputCachePolicy: "");

        var report = await Create(new FakeDynamicConfig(), policies).ValidateRouteAsync(route, true);

        Assert.True(report.Ok);
        Assert.Empty(policies.Queried);
    }

    [Fact]
    public async Task Route_ChecksAllFourPolicyFields()
    {
        var policies = new FakePolicyCatalog();
        var route = Route(
            authorizationPolicy: "a",
            corsPolicy: "b",
            rateLimiterPolicy: "c",
            outputCachePolicy: "d");

        await Create(new FakeDynamicConfig(), policies).ValidateRouteAsync(route, true);

        Assert.Equal(4, policies.Queried.Count);
    }

    [Fact]
    public async Task Route_DisabledByMetadata_WarnsButSaves()
    {
        var route = Route(metadata: new Dictionary<string, string> { ["Enabled"] = "False" });

        var report = await Create(new FakeDynamicConfig()).ValidateRouteAsync(route, true);

        Assert.True(report.Ok);
        Assert.True(HasWarning(report, "Metadata.Enabled"));
    }

    // ---------- cluster ----------

    [Fact]
    public async Task ValidCluster_Passes()
    {
        var report = await Create(new FakeDynamicConfig()).ValidateClusterAsync(Cluster());

        Assert.True(report.Ok);
    }

    [Fact]
    public async Task Cluster_WithoutDestinations_IsRejected()
    {
        var report = await Create(new FakeDynamicConfig()).ValidateClusterAsync(Cluster(address: null));

        Assert.True(HasError(report, "Destinations"));
    }

    [Fact]
    public async Task Cluster_WithEmptyAddress_IsRejected_AgainstTheNodePath()
    {
        var report = await Create(new FakeDynamicConfig()).ValidateClusterAsync(Cluster(address: ""));

        Assert.True(HasError(report, "Destinations.d1.Address"));
    }

    [Theory]
    [InlineData("localhost:5000")]
    [InlineData("ftp://localhost:21")]
    [InlineData("//localhost")]
    public async Task Cluster_WithNonHttpAddress_IsRejected(string address)
    {
        var report = await Create(new FakeDynamicConfig()).ValidateClusterAsync(Cluster(address: address));

        var issue = Assert.Single(report.Errors, e => e.Field == "Destinations.d1.Address");
        Assert.NotNull(issue.Hint);
    }

    [Fact]
    public async Task Cluster_WithUnknownLoadBalancingPolicy_WarnsOnly()
    {
        var report = await Create(new FakeDynamicConfig())
            .ValidateClusterAsync(Cluster(loadBalancingPolicy: "MyConsistentHashing"));

        Assert.True(report.Ok);
        Assert.True(HasWarning(report, "LoadBalancingPolicy"));
    }

    [Theory]
    [InlineData("RoundRobin")]
    [InlineData("PowerOfTwoChoices")]
    [InlineData("LeastRequests")]
    [InlineData("Random")]
    [InlineData("FirstAlphabetical")]
    public async Task Cluster_WithBuiltInLoadBalancingPolicy_IsSilent(string policy)
    {
        var report = await Create(new FakeDynamicConfig())
            .ValidateClusterAsync(Cluster(loadBalancingPolicy: policy));

        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task Cluster_SessionAffinityWithoutKey_IsRejected()
    {
        // YARP throws at endpoint build time; catching it here keeps the gateway serving.
        var affinity = new SessionAffinityConfig { Enabled = true, Policy = "Cookie" };

        var report = await Create(new FakeDynamicConfig())
            .ValidateClusterAsync(Cluster(sessionAffinity: affinity));

        Assert.True(HasError(report, "SessionAffinity.AffinityKeyName"));
    }

    [Fact]
    public async Task Cluster_SessionAffinityWithUnknownPolicy_IsRejected()
    {
        var affinity = new SessionAffinityConfig
        {
            Enabled = true,
            Policy = "Sticky",
            AffinityKeyName = "gw.affinity"
        };

        var report = await Create(new FakeDynamicConfig())
            .ValidateClusterAsync(Cluster(sessionAffinity: affinity));

        Assert.True(HasError(report, "SessionAffinity.Policy"));
    }

    [Fact]
    public async Task Cluster_SessionAffinityDisabled_IsIgnored()
    {
        var affinity = new SessionAffinityConfig { Enabled = false };

        var report = await Create(new FakeDynamicConfig())
            .ValidateClusterAsync(Cluster(sessionAffinity: affinity));

        Assert.True(report.Ok);
    }

    [Fact]
    public async Task Cluster_WithValidSessionAffinity_IsAccepted()
    {
        var affinity = new SessionAffinityConfig
        {
            Enabled = true,
            Policy = "Cookie",
            AffinityKeyName = "gw.affinity"
        };

        var report = await Create(new FakeDynamicConfig())
            .ValidateClusterAsync(Cluster(sessionAffinity: affinity));

        Assert.True(report.Ok);
    }

    [Fact]
    public async Task Cluster_WithZeroHealthCheckInterval_IsRejected()
    {
        var healthCheck = new HealthCheckConfig
        {
            Active = new ActiveHealthCheckConfig
            {
                Enabled = true,
                Interval = TimeSpan.Zero,
                Timeout = TimeSpan.Zero
            }
        };

        var report = await Create(new FakeDynamicConfig())
            .ValidateClusterAsync(Cluster(healthCheck: healthCheck));

        Assert.True(HasError(report, "HealthCheck.Active.Interval"));
        Assert.True(HasError(report, "HealthCheck.Active.Timeout"));
    }

    [Fact]
    public async Task Cluster_WithDisabledHealthCheck_IsIgnored()
    {
        var healthCheck = new HealthCheckConfig
        {
            Active = new ActiveHealthCheckConfig { Enabled = false, Interval = TimeSpan.Zero }
        };

        var report = await Create(new FakeDynamicConfig())
            .ValidateClusterAsync(Cluster(healthCheck: healthCheck));

        Assert.True(report.Ok);
    }

    [Fact]
    public async Task Cluster_WithCertificateValidationDisabled_WarnsOnly()
    {
        var httpClient = new HttpClientConfig { DangerousAcceptAnyServerCertificate = true };

        var report = await Create(new FakeDynamicConfig())
            .ValidateClusterAsync(Cluster(httpClient: httpClient));

        Assert.True(report.Ok);
        Assert.True(HasWarning(report, "HttpClient.DangerousAcceptAnyServerCertificate"));
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(0, 9)]
    public async Task Cluster_WithUnsupportedHttpVersion_IsRejected(int major, int minor)
    {
        var httpRequest = new ForwarderRequestConfig { Version = new Version(major, minor) };

        var report = await Create(new FakeDynamicConfig())
            .ValidateClusterAsync(Cluster(httpRequest: httpRequest));

        Assert.True(HasError(report, "HttpRequest.Version"));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    public async Task Cluster_WithSupportedHttpVersion_IsAccepted(int major, int minor)
    {
        var httpRequest = new ForwarderRequestConfig { Version = new Version(major, minor) };

        var report = await Create(new FakeDynamicConfig())
            .ValidateClusterAsync(Cluster(httpRequest: httpRequest));

        Assert.True(report.Ok);
    }

    // ---------- cluster: canary weights ----------

    [Theory]
    [InlineData("Weighted")]
    [InlineData("IpBased")]
    public async Task Cluster_WithGatewayShippedPolicy_IsSilent(string policy)
    {
        var report = await Create(new FakeDynamicConfig())
            .ValidateClusterAsync(Cluster(loadBalancingPolicy: policy));

        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task Cluster_WeightedWithValidWeights_IsAccepted()
    {
        var cluster = WeightedCluster("Weighted", ("stable", "90", null), ("canary", "10", "v2"));

        var report = await Create(new FakeDynamicConfig()).ValidateClusterAsync(cluster);

        Assert.True(report.Ok);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task Cluster_WeightedWithAllZeroWeights_IsRejected()
    {
        // The gateway keeps serving, so nobody notices until they read the access log:
        // every request silently lands on the first destination.
        var cluster = WeightedCluster("Weighted", ("a", "0", null), ("b", "0", null));

        var report = await Create(new FakeDynamicConfig()).ValidateClusterAsync(cluster);

        Assert.False(report.Ok);
        Assert.True(HasError(report, "Destinations"));
    }

    [Fact]
    public async Task Cluster_NonNumericWeight_IsRejectedAgainstTheNodePath()
    {
        var cluster = WeightedCluster("Weighted", ("stable", "90", null), ("canary", "ten", null));

        var report = await Create(new FakeDynamicConfig()).ValidateClusterAsync(cluster);

        Assert.False(report.Ok);
        Assert.True(HasError(report, "Destinations.canary.Metadata.Weight"));
    }

    [Fact]
    public async Task Cluster_NegativeWeight_IsRejected()
    {
        var cluster = WeightedCluster("Weighted", ("stable", "-1", null));

        var report = await Create(new FakeDynamicConfig()).ValidateClusterAsync(cluster);

        Assert.True(HasError(report, "Destinations.stable.Metadata.Weight"));
    }

    [Fact]
    public async Task Cluster_TagWithoutWeightedPolicy_Warns()
    {
        var cluster = WeightedCluster("RoundRobin", ("stable", "90", null), ("canary", "10", "v2"));

        var report = await Create(new FakeDynamicConfig()).ValidateClusterAsync(cluster);

        Assert.True(report.Ok);
        Assert.True(HasWarning(report, "Destinations"));
    }

    [Fact]
    public async Task Cluster_WeightsOnNonWeightedPolicy_DoNotWarn()
    {
        // PowerOfTwoChoices also reads Weight, so a weight table is legitimate there.
        var cluster = WeightedCluster("PowerOfTwoChoices", ("a", "3", null), ("b", "1", null));

        var report = await Create(new FakeDynamicConfig()).ValidateClusterAsync(cluster);

        Assert.True(report.Ok);
        Assert.Empty(report.Warnings);
    }

    // ---------- whole-configuration integrity ----------

    [Fact]
    public async Task Integrity_RoutePointingAtMissingCluster_IsReported()
    {
        var config = new FakeDynamicConfig();
        config.Routes.Add(Route(id: "r1", clusterId: "ghost"));

        var report = await Create(config).ValidateAllAsync();

        Assert.False(report.Ok);
        Assert.True(HasError(report, "Routes.r1.ClusterId"));
    }

    [Fact]
    public async Task Integrity_EmptyCluster_IsReportedOnce()
    {
        // ValidateClusterAsync also reports "Destinations"; the aggregate check must not
        // duplicate it with different wording.
        var config = new FakeDynamicConfig();
        config.Clusters.Add(Cluster(id: "c1", address: null));

        var report = await Create(config).ValidateAllAsync();

        Assert.Single(report.Errors, e => e.Field.Contains("Destinations"));
        Assert.True(HasError(report, "Clusters.c1.Destinations"));
    }

    [Fact]
    public async Task Integrity_OrphanCluster_Warns()
    {
        var config = new FakeDynamicConfig();
        config.Clusters.Add(Cluster(id: "orphan"));

        var report = await Create(config).ValidateAllAsync();

        Assert.True(HasWarning(report, "Clusters.orphan"));
    }

    [Fact]
    public async Task Integrity_ReferencedCluster_DoesNotWarn()
    {
        var config = new FakeDynamicConfig();
        config.Clusters.Add(Cluster(id: "c1"));
        config.Routes.Add(Route(id: "r1", clusterId: "c1"));

        var report = await Create(config).ValidateAllAsync();

        Assert.False(HasWarning(report, "Clusters.c1"));
    }

    [Fact]
    public async Task Integrity_DuplicateMatchPath_Warns()
    {
        // Only the highest-priority route ever matches; the other is dead but still
        // shows up in the dashboard as healthy.
        var config = new FakeDynamicConfig();
        config.Clusters.Add(Cluster("c1"));
        config.Routes.Add(Route(id: "r1", path: "/api/{**catch-all}"));
        config.Routes.Add(Route(id: "r2", path: "/api/{**catch-all}"));

        var report = await Create(config).ValidateAllAsync();

        Assert.Contains(report.Warnings, w => w.Field == "Routes./api/{**catch-all}.Match.Path");
    }

    [Fact]
    public async Task Integrity_CleanConfig_IsOk()
    {
        var config = new FakeDynamicConfig();
        config.Clusters.Add(Cluster("c1"));
        config.Routes.Add(Route(id: "r1", clusterId: "c1"));

        var report = await Create(config).ValidateAllAsync();

        Assert.True(report.Ok);
        Assert.Empty(report.Warnings);
    }

    // ---------- batch (import / rollback) ----------

    [Fact]
    public async Task Batch_RouteAndClusterInSameBatch_Passes()
    {
        // The whole reason batch validation exists: import and rollback hand over a
        // snapshot where the cluster is not live yet, and a per-route check against the
        // running config would call every single one of them broken.
        var report = await Create(new FakeDynamicConfig())
            .ValidateBatchAsync(new[] { Route(id: "r1", clusterId: "c1") }, new[] { Cluster("c1") });

        Assert.True(report.Ok);
    }

    [Fact]
    public async Task Batch_RouteToClusterOutsideTheBatch_IsRejected_WithPrefixedField()
    {
        var report = await Create(new FakeDynamicConfig())
            .ValidateBatchAsync(new[] { Route(id: "r1", clusterId: "ghost") }, Array.Empty<ClusterConfig>());

        Assert.False(report.Ok);
        Assert.True(HasError(report, "Routes.r1.ClusterId"));
    }

    [Fact]
    public async Task Batch_RouteToClusterAlreadyLive_IsAccepted()
    {
        // Import merges into the current configuration, so an existing cluster still counts.
        var config = new FakeDynamicConfig();
        config.Clusters.Add(Cluster("legacy"));

        var report = await Create(config)
            .ValidateBatchAsync(new[] { Route(id: "r1", clusterId: "legacy") }, Array.Empty<ClusterConfig>());

        Assert.True(report.Ok);
    }

    [Fact]
    public async Task Batch_BadDestination_IsPrefixedWithTheClusterId()
    {
        // Without the prefix, "30 clusters failed" is a list of identical field paths.
        var report = await Create(new FakeDynamicConfig())
            .ValidateBatchAsync(Array.Empty<RouteConfig>(), new[] { Cluster("c1", address: "localhost:5000") });

        Assert.False(report.Ok);
        Assert.True(HasError(report, "Clusters.c1.Destinations.d1.Address"));
    }

    [Fact]
    public async Task Batch_ClusterWithoutDestinations_IsRejected()
    {
        var report = await Create(new FakeDynamicConfig())
            .ValidateBatchAsync(Array.Empty<RouteConfig>(), new[] { Cluster("c1", address: null) });

        Assert.True(HasError(report, "Clusters.c1.Destinations"));
    }

    [Fact]
    public async Task Batch_DuplicateMatchPath_Warns()
    {
        var report = await Create(new FakeDynamicConfig()).ValidateBatchAsync(
            new[]
            {
                Route(id: "r1", clusterId: "c1", path: "/api/{**catch-all}"),
                Route(id: "r2", clusterId: "c1", path: "/api/{**catch-all}")
            },
            new[] { Cluster("c1") });

        Assert.True(report.Ok);
        Assert.Contains(report.Warnings, w => w.Field == "Routes./api/{**catch-all}.Match.Path");
    }

    [Fact]
    public async Task Batch_EmptyInput_IsOk()
    {
        var report = await Create(new FakeDynamicConfig())
            .ValidateBatchAsync(Array.Empty<RouteConfig>(), Array.Empty<ClusterConfig>());

        Assert.True(report.Ok);
    }
}
