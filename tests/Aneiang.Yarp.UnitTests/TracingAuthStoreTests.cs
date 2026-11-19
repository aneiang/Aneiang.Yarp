using Aneiang.Yarp.Dashboard.Infrastructure;
using Aneiang.Yarp.Dashboard.Modules.Tracing.Models;
using Aneiang.Yarp.Dashboard.Modules.Tracing.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Covers OTLP export authentication: how values are masked, where they come from, and what is
/// refused. The store never returns a credential, so every assertion here is about the masked form.
/// </summary>
/// <remarks>
/// Shares the <c>tracing-spans</c> collection because two tests here mutate process-wide
/// environment variables that the other tracing tests read while building their provider.
/// </remarks>
[Collection("tracing-spans")]
public sealed class TracingAuthStoreTests : IDisposable
{
    private readonly string _root;
    private readonly TracingAuthStore _store;

    public TracingAuthStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "yarp-auth-tests-" + Guid.NewGuid().ToString("N"));
        _store = new TracingAuthStore(_root, NullLogger<TracingAuthStore>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- masking

    [Theory]
    [InlineData("Basic YWRTaW5AYWRzLmNu0jk4NkdpalE5eHlycFFxb1Y=", "Basic ****cFFxb1Y=")]
    [InlineData("short", "****")]
    [InlineData("", "")]
    public void Mask_keeps_the_scheme_and_tail_only(string input, string expected)
        => Assert.Equal(expected, TracingAuthStore.Mask(input));

    // ---------------------------------------------------------------- precedence

    [Fact]
    public void Configuration_wins_over_the_panel_file()
    {
        Assert.True(_store.TrySave([new("Authorization", "Basic from-panel")], out var error), error);

        var options = OptionsWith(("Authorization", "Basic from-config"));

        Assert.Equal("Basic from-config", _store.ResolveEffective(options)["Authorization"]);
    }

    [Fact]
    public void Panel_file_wins_over_the_environment()
    {
        var previous = SwapEnvironment("OTEL_EXPORTER_OTLP_HEADERS", "Authorization=Basic from-env");
        try
        {
            Assert.True(_store.TrySave([new("Authorization", "Basic from-panel")], out var error), error);

            Assert.Equal("Basic from-panel", _store.ResolveEffective(new GatewayTracingOptions())["Authorization"]);
        }
        finally
        {
            SwapEnvironment("OTEL_EXPORTER_OTLP_HEADERS", previous);
        }
    }

    [Fact]
    public void Standard_environment_variables_are_a_fallback()
    {
        var tracesPrevious = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_HEADERS");
        var generalPrevious = SwapEnvironment(
            "OTEL_EXPORTER_OTLP_HEADERS",
            "Authorization=Basic env-token,X-Scope-OrgID=tenant-a");

        try
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_HEADERS", null);

            var effective = _store.ResolveEffective(new GatewayTracingOptions());

            // The documented format is key=value,key=value — and base64 padding must survive.
            Assert.Equal("Basic env-token", effective["Authorization"]);
            Assert.Equal("tenant-a", effective["X-Scope-OrgID"]);
        }
        finally
        {
            SwapEnvironment("OTEL_EXPORTER_OTLP_HEADERS", generalPrevious);
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_HEADERS", tracesPrevious);
        }
    }

    [Fact]
    public void Environment_value_with_base64_padding_is_kept_intact()
    {
        var previous = SwapEnvironment("OTEL_EXPORTER_OTLP_HEADERS", "Authorization=Basic YWJjZA==");
        try
        {
            var effective = _store.ResolveEffective(new GatewayTracingOptions());
            Assert.Equal("Basic YWJjZA==", effective["Authorization"]);
        }
        finally
        {
            SwapEnvironment("OTEL_EXPORTER_OTLP_HEADERS", previous);
        }
    }

    // ---------------------------------------------------------------- write semantics

    [Fact]
    public void Saved_value_is_never_returned_in_clear()
    {
        Assert.True(_store.TrySave([new("Authorization", "Basic super-secret-token-value")], out var error), error);

        var status = _store.Describe(new GatewayTracingOptions());

        Assert.True(status.Configured);
        var header = Assert.Single(status.Headers);
        Assert.Equal("Authorization", header.Name);
        Assert.Equal("panel", header.Source);
        Assert.DoesNotContain("super-secret", header.MaskedValue, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_value_withdraws_a_panel_override_but_keeps_configuration()
    {
        Assert.True(_store.TrySave([new("Authorization", "Basic from-panel")], out _));

        // The editor submits every row, but only fills the ones being changed.
        Assert.True(_store.TrySave([new("Authorization", string.Empty)], out var error), error);

        var options = OptionsWith(("Authorization", "Basic from-config"));
        Assert.Equal("Basic from-config", _store.ResolveEffective(options)["Authorization"]);
    }

    [Fact]
    public void Clearing_every_row_removes_the_override_file()
    {
        Assert.True(_store.TrySave([new("Authorization", "Basic x")], out _));
        Assert.True(File.Exists(_store.FilePath));

        Assert.True(_store.TrySave([], out var error), error);

        Assert.False(File.Exists(_store.FilePath));
    }

    [Fact]
    public void Describe_marks_where_each_value_came_from()
    {
        var options = OptionsWith(("X-Scope-OrgID", "tenant-a"));

        var status = _store.Describe(options);

        Assert.Equal("config", Assert.Single(status.Headers).Source);
    }

    // ---------------------------------------------------------------- refusals

    [Fact]
    public void Masked_placeholder_is_rejected()
    {
        // The page can only show a masked value; echoing it back must not overwrite a live token
        // with literal asterisks.
        Assert.False(_store.TrySave([new("Authorization", "Basic ****cFFxb1Y=")], out var error));

        Assert.NotNull(error);
        Assert.Contains("masked", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Bad Header", "value")]
    [InlineData("X-Bad:Injected", "value")]
    [InlineData("", "value")]
    public void Invalid_header_name_is_rejected(string name, string value)
        => Assert.False(_store.TrySave([new(name, value)], out _));

    [Fact]
    public void Line_break_in_value_is_rejected()
    {
        // Header injection: a CRLF here would let the caller append arbitrary headers to every
        // export the gateway sends.
        Assert.False(_store.TrySave([new("Authorization", "Basic abc\r\nX-Evil: 1")], out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Too_many_headers_are_rejected()
    {
        var many = Enumerable.Range(0, 40)
            .Select(i => new KeyValuePair<string, string>("X-H" + i, "v"))
            .ToList();
        Assert.False(_store.TrySave(many, out _));
    }

    // ---------------------------------------------------------------- protocol

    [Theory]
    [InlineData("http/protobuf", null)]
    [InlineData("HTTP-Protobuf", null)]
    [InlineData("httpprotobuf", null)]
    [InlineData("grpc", null)]
    [InlineData("httpprotobuf-typo", "not supported")]
    [InlineData("udp", "not supported")]
    public async Task Protocol_is_normalised_and_unknown_values_fail_visibly(string protocol, string? expectedError)
    {
        var options = new GatewayTracingOptions
        {
            Enabled = true,
            ExportEnabled = true,
            Endpoint = "http://127.0.0.1:4318",
            Protocol = protocol
        };

        var sampler = new RuntimeRatioSampler(0);
        var buffer = new TraceRingBuffer(GatewayTracingHost.BufferSources, 10, 8);
        var store = new TracingAuthStore(_root, NullLogger<TracingAuthStore>.Instance);

        await using var host = new GatewayTracingHost(
            options,
            sampler,
            buffer,
            store,
            Options.Create(new DashboardOptions()),
            NullLogger<GatewayTracingHost>.Instance);

        await host.StartAsync(CancellationToken.None);

        if (expectedError is null)
            Assert.True(host.StartError is null, $"expected a successful start but got: {host.StartError}");
        else
            Assert.Contains(expectedError, host.StartError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static GatewayTracingOptions OptionsWith(params (string Name, string Value)[] headers)
    {
        var options = new GatewayTracingOptions();
        foreach (var (name, value) in headers)
            options.Headers[name] = value;
        return options;
    }

    /// <summary>Sets an environment variable and returns what was there before, for restoration.</summary>
    private static string? SwapEnvironment(string name, string? value)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        return previous;
    }
}
