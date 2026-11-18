using Aneiang.Yarp.Serialization;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Regression tests for the lenient JSON layer.
///
/// The dashboard form posts strings where YARP expects booleans and TimeSpans, and an
/// unset field arrives as <c>""</c> rather than being absent. Before these converters the
/// whole save failed with "Cannot convert string '' to boolean" — a message that names no
/// field in a twenty-field form. Each case below is a payload shape that actually reached
/// production.
/// </summary>
public class YarpJsonConfigTests
{
    [Fact]
    public void DeserializeCluster_EmptyStringBoolean_BecomesNull()
    {
        var cluster = YarpJsonConfig.DeserializeCluster("""
        {
          "ClusterId": "c1",
          "Destinations": { "d1": { "Address": "http://localhost:5000" } },
          "HttpClient": { "DangerousAcceptAnyServerCertificate": "" }
        }
        """);

        Assert.NotNull(cluster);
        Assert.Null(cluster!.HttpClient!.DangerousAcceptAnyServerCertificate);
    }

    [Fact]
    public void DeserializeCluster_WhitespaceBoolean_BecomesNull()
    {
        var cluster = YarpJsonConfig.DeserializeCluster("""
        {
          "ClusterId": "c1",
          "Destinations": { "d1": { "Address": "http://localhost:5000" } },
          "HealthCheck": { "Active": { "Enabled": "   " } }
        }
        """);

        Assert.NotNull(cluster);
        Assert.Null(cluster!.HealthCheck!.Active!.Enabled);
    }

    [Theory]
    [InlineData("\"true\"", true)]
    [InlineData("\"false\"", false)]
    [InlineData("\"True\"", true)]
    public void DeserializeCluster_TruthyStrings_Parse(string literal, bool expected)
    {
        var cluster = YarpJsonConfig.DeserializeCluster($$"""
        {
          "ClusterId": "c1",
          "Destinations": { "d1": { "Address": "http://localhost:5000" } },
          "HealthCheck": { "Active": { "Enabled": {{literal}} } }
        }
        """);

        Assert.Equal(expected, cluster!.HealthCheck!.Active!.Enabled);
    }

    [Fact]
    public void DeserializeCluster_EmptyStringTimeSpan_BecomesNull()
    {
        var cluster = YarpJsonConfig.DeserializeCluster("""
        {
          "ClusterId": "c1",
          "Destinations": { "d1": { "Address": "http://localhost:5000" } },
          "HttpRequest": { "ActivityTimeout": "" }
        }
        """);

        Assert.Null(cluster!.HttpRequest!.ActivityTimeout);
    }

    [Theory]
    [InlineData("00:00:30", 30)]
    [InlineData("30", 30)]
    [InlineData("30s", 30)]
    [InlineData("5m", 300)]
    [InlineData("2h", 7200)]
    [InlineData("1d", 86400)]
    public void DeserializeCluster_TimeSpanFormats_Parse(string literal, int expectedSeconds)
    {
        var cluster = YarpJsonConfig.DeserializeCluster($$"""
        {
          "ClusterId": "c1",
          "Destinations": { "d1": { "Address": "http://localhost:5000" } },
          "HttpRequest": { "ActivityTimeout": "{{literal}}" }
        }
        """);

        Assert.Equal(expectedSeconds, cluster!.HttpRequest!.ActivityTimeout!.Value.TotalSeconds);
    }

    [Fact]
    public void DeserializeCluster_BareNumberTimeSpan_IsSecondsNotDays()
    {
        // TimeSpan.Parse("30") is 30 *days*; a form field meaning seconds must not
        // silently produce a month-long timeout.
        var cluster = YarpJsonConfig.DeserializeCluster("""
        {
          "ClusterId": "c1",
          "Destinations": { "d1": { "Address": "http://localhost:5000" } },
          "HttpRequest": { "ActivityTimeout": 30 }
        }
        """);

        Assert.Equal(TimeSpan.FromSeconds(30), cluster!.HttpRequest!.ActivityTimeout);
    }

    [Fact]
    public void DeserializeRoute_KeepsMatchAndPolicies()
    {
        var route = YarpJsonConfig.DeserializeRoute("""
        {
          "RouteId": "r1",
          "ClusterId": "c1",
          "Match": { "Path": "/api/{**catch-all}", "Methods": [ "GET", "POST" ] },
          "AuthorizationPolicy": "gw-authenticated",
          "CorsPolicy": "gw-cors-open",
          "RateLimiterPolicy": "gw-rl-100pm",
          "OutputCachePolicy": "gw-cache-30s"
        }
        """);

        Assert.NotNull(route);
        Assert.Equal("/api/{**catch-all}", route!.Match.Path);
        Assert.Equal(new[] { "GET", "POST" }, route.Match.Methods);
        Assert.Equal("gw-authenticated", route.AuthorizationPolicy);
        Assert.Equal("gw-cors-open", route.CorsPolicy);
        Assert.Equal("gw-rl-100pm", route.RateLimiterPolicy);
        Assert.Equal("gw-cache-30s", route.OutputCachePolicy);
    }

    [Fact]
    public void DeserializeRoute_SharedStringPaths_Survive()
    {
        var route = YarpJsonConfig.DeserializeRoute("""
        {
          "RouteId": "r1",
          "ClusterId": "c1",
          "Match": { "Path": "/\\ok" },
          "Metadata": { "Note": "back\\slash" }
        }
        """);

        Assert.NotNull(route);
        Assert.Contains("slash", route!.Metadata!["Note"]);
    }

    [Fact]
    public void DeserializeCluster_EmptyJson_DoesNotThrow()
    {
        var cluster = YarpJsonConfig.DeserializeCluster("{}");
        Assert.NotNull(cluster);
        Assert.Null(cluster!.Destinations);
    }

    [Fact]
    public void DeserializeRoute_InvalidJson_Throws()
    {
        Assert.ThrowsAny<Exception>(() => YarpJsonConfig.DeserializeRoute("{ not json"));
    }

    [Fact]
    public void RoundTrip_Cluster_PreservesSessionAffinity()
    {
        var original = YarpJsonConfig.DeserializeCluster("""
        {
          "ClusterId": "c1",
          "Destinations": { "d1": { "Address": "http://localhost:5000" } },
          "SessionAffinity": { "Enabled": true, "Policy": "Cookie", "AffinityKeyName": "gw.affinity" }
        }
        """);

        var json = YarpJsonConfig.SerializeCluster(original!);
        var again = YarpJsonConfig.DeserializeCluster(json);

        Assert.True(again!.SessionAffinity!.Enabled);
        Assert.Equal("gw.affinity", again.SessionAffinity.AffinityKeyName);
    }
}
