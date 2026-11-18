using System.Text.Json;
using Aneiang.Yarp.Dashboard.Modules.GatewayConfig.Services;
using Aneiang.Yarp.Validation;
using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Configuration;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// The optimistic-concurrency contract is deliberately optional: a caller that sends no
/// version keeps the old last-write-wins behaviour, and only a caller that echoes a stale
/// version is refused. These cases pin that asymmetry, because "reject everything without a
/// header" would break every existing script while looking like a safety improvement.
/// </summary>
public class ConfigConcurrencyTests
{
    private static HttpRequest Request(string? header = null, string? query = null)
    {
        var context = new DefaultHttpContext();
        if (header != null)
            context.Request.Headers[ConfigConcurrency.VersionHeaderName] = header;
        if (query != null)
            context.Request.QueryString = new QueryString("?" + ConfigConcurrency.VersionQueryName + "=" + query);
        return context.Request;
    }

    [Fact]
    public void NoHeaderAndNoQuery_MeansNoExpectation()
        => Assert.Null(ConfigConcurrency.ReadExpectedVersion(Request()));

    [Fact]
    public void Header_IsRead()
        => Assert.Equal(5, ConfigConcurrency.ReadExpectedVersion(Request(header: "5")));

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("0")]
    [InlineData("-3")]
    public void UnusableHeader_IsTreatedAsAbsent(string raw)
        => Assert.Null(ConfigConcurrency.ReadExpectedVersion(Request(header: raw)));

    [Fact]
    public void QueryString_IsAValidAlternative()
        => Assert.Equal(7, ConfigConcurrency.ReadExpectedVersion(Request(query: "7")));

    [Fact]
    public void Header_WinsOverQueryString()
        => Assert.Equal(5, ConfigConcurrency.ReadExpectedVersion(Request(header: "5", query: "7")));

    [Fact]
    public void StaleHeader_WithGarbageQuery_StillReadsHeader()
        => Assert.Equal(5, ConfigConcurrency.ReadExpectedVersion(Request(header: "5", query: "nope")));

    [Fact]
    public void NoExpectation_IsNeverStale()
        => Assert.False(ConfigConcurrency.IsStale(null, 42));

    [Fact]
    public void MatchingVersion_IsNotStale()
        => Assert.False(ConfigConcurrency.IsStale(42, 42));

    [Theory]
    [InlineData(41)]
    [InlineData(43)]
    public void DivergingVersion_IsStale(long expected)
        => Assert.True(ConfigConcurrency.IsStale(expected, 42));

    [Fact]
    public void ConflictPayload_CarriesBothVersions()
    {
        var json = JsonSerializer.Serialize(ConfigConcurrency.BuildConflictPayload(3, 9));

        Assert.Contains("\"code\":409", json);
        Assert.Contains("\"currentVersion\":9", json);
        Assert.Contains("your version 3", json);
    }
}

/// <summary>
/// The adapter is the seam that lets one validator serve both the dashboard forms and the
/// core write paths. If it dropped warnings, or promoted them to errors, every
/// "unverifiable policy name" would start blocking saves.
/// </summary>
public class ConfigurationValidatorGuardTests
{
    private static ConfigurationValidatorGuard Guard(ConfigValidationReport report)
        => new(new StubValidator(report));

    [Fact]
    public async Task Errors_MakeTheResultNotOk()
    {
        var guard = Guard(ConfigValidationReport.From(new[]
        {
            new ConfigValidationIssue("Error", "ClusterId", "boom")
        }));

        var result = await guard.CheckRouteAsync(new RouteConfig(), false);

        Assert.False(result.Ok);
        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task Warnings_DoNotBlock()
    {
        var guard = Guard(ConfigValidationReport.From(new[]
        {
            new ConfigValidationIssue("Warning", "LoadBalancingPolicy", "not built in", "register it")
        }));

        var result = await guard.CheckClusterAsync(new ClusterConfig());

        Assert.True(result.Ok);
        Assert.Empty(result.Errors);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task Hint_IsPreserved()
    {
        var guard = Guard(ConfigValidationReport.From(new[]
        {
            new ConfigValidationIssue("Error", "Destinations.d1.Address", "bad", "http://host:port")
        }));

        var result = await guard.CheckClusterAsync(new ClusterConfig());

        Assert.Equal("http://host:port", result.Errors[0].Hint);
    }

    [Fact]
    public async Task BatchAndCurrent_RouteThroughTheSameValidator()
    {
        var stub = new StubValidator(ConfigurationGuardResult_ToReport());
        var guard = new ConfigurationValidatorGuard(stub);

        await guard.CheckBatchAsync(Array.Empty<RouteConfig>(), Array.Empty<ClusterConfig>());
        await guard.CheckCurrentAsync();

        Assert.Equal(2, stub.Calls);
    }

    private static ConfigValidationReport ConfigurationGuardResult_ToReport()
        => ConfigValidationReport.From(Array.Empty<ConfigValidationIssue>());

    private sealed class StubValidator : IConfigurationValidator
    {
        private readonly ConfigValidationReport _report;

        public StubValidator(ConfigValidationReport report) => _report = report;

        public int Calls { get; private set; }

        public Task<ConfigValidationReport> ValidateRouteAsync(
            RouteConfig route, bool clusterWillBeCreated, CancellationToken ct = default)
            => Task.FromResult(_report);

        public Task<ConfigValidationReport> ValidateClusterAsync(ClusterConfig cluster, CancellationToken ct = default)
            => Task.FromResult(_report);

        public Task<ConfigValidationReport> ValidateAllAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(_report);
        }

        public Task<ConfigValidationReport> ValidateBatchAsync(
            IReadOnlyList<RouteConfig> routes,
            IReadOnlyList<ClusterConfig> clusters,
            CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(_report);
        }
    }
}
