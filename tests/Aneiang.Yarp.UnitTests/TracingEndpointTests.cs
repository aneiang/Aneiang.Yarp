using Aneiang.Yarp.Dashboard.Modules.Tracing.Services;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Covers how an OTLP/HTTP endpoint is turned into the URL the exporter actually calls.
/// </summary>
/// <remarks>
/// The exporter used here (1.15.3) does not append <c>/v1/traces</c> — this was confirmed against a
/// local capture server, which received the endpoint path verbatim. Most OTLP documentation still
/// describes the appending behaviour, so both spellings have to land on the same URL. These tests
/// exist because the failure mode is a 404 that reads like an authentication problem.
/// </remarks>
public class TracingEndpointTests
{
    [Fact]
    public void A_bare_host_and_port_gets_the_traces_path()
    {
        var result = GatewayTracingHost.NormalizeHttpTracesEndpoint(new Uri("http://collector:4318"));

        Assert.Equal("http://collector:4318/v1/traces", result.ToString());
    }

    [Fact]
    public void An_endpoint_that_already_names_the_traces_path_is_left_alone()
    {
        var result = GatewayTracingHost.NormalizeHttpTracesEndpoint(new Uri("http://collector:4318/v1/traces"));

        Assert.Equal("http://collector:4318/v1/traces", result.ToString());
    }

    [Fact]
    public void A_backend_base_path_is_kept_and_the_traces_path_added()
    {
        // OpenObserve serves OTLP/HTTP under /api/{organization}.
        var result = GatewayTracingHost.NormalizeHttpTracesEndpoint(new Uri("http://obs:5080/api/default"));

        Assert.Equal("http://obs:5080/api/default/v1/traces", result.ToString());
    }

    [Fact]
    public void A_trailing_slash_does_not_produce_a_doubled_separator()
    {
        var result = GatewayTracingHost.NormalizeHttpTracesEndpoint(new Uri("http://obs:5080/api/default/"));

        Assert.Equal("http://obs:5080/api/default/v1/traces", result.ToString());
    }

    [Fact]
    public void A_trailing_slash_after_the_traces_path_does_not_append_a_second_one()
    {
        var result = GatewayTracingHost.NormalizeHttpTracesEndpoint(new Uri("http://collector:4318/v1/traces/"));

        Assert.DoesNotContain("/v1/traces/v1/traces", result.ToString(), StringComparison.Ordinal);
        Assert.EndsWith("/v1/traces", result.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
    }

    [Fact]
    public void The_traces_path_is_recognised_regardless_of_case()
    {
        var result = GatewayTracingHost.NormalizeHttpTracesEndpoint(new Uri("http://collector:4318/V1/Traces"));

        Assert.Equal("http://collector:4318/V1/Traces", result.ToString());
    }

    [Fact]
    public void An_https_endpoint_keeps_its_scheme()
    {
        // Non-default port on purpose: Uri drops :443 from the textual form, which would make the
        // assertion about scheme and port indistinguishable.
        var result = GatewayTracingHost.NormalizeHttpTracesEndpoint(new Uri("https://ingest.example.com:8443"));

        Assert.Equal("https://ingest.example.com:8443/v1/traces", result.ToString());
    }

    [Fact]
    public void The_normalised_endpoint_keeps_the_host_and_port_unchanged()
    {
        var result = GatewayTracingHost.NormalizeHttpTracesEndpoint(new Uri("http://obs.internal:5080/api/default"));

        Assert.Equal("obs.internal", result.Host);
        Assert.Equal(5080, result.Port);
    }
}
