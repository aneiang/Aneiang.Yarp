using System.Collections.Immutable;
using Aneiang.Yarp.Models;
using Aneiang.Yarp.Plugin.FaultInjection;
using Aneiang.Yarp.Services;
using Aneiang.Yarp.Storage.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Yarp.ReverseProxy.Configuration;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Covers the decision half of fault injection: which requests get hit, and with what.
/// </summary>
/// <remarks>
/// The probability gate takes its sample as an argument rather than calling <c>Random</c>, which is
/// what makes "50%" testable as an exact boundary instead of a statistical hope.
/// </remarks>
public class FaultInjectionPlannerTests
{
    private static FaultInjectionExecutionConfig Config(Action<FaultInjectionExecutionConfigBuilder>? configure = null)
    {
        var builder = new FaultInjectionExecutionConfigBuilder();
        configure?.Invoke(builder);
        return builder.Build();
    }

    private static string? NoHeaders(string _) => null;

    private static Func<string, string?> Headers(params (string Name, string Value)[] headers)
        => name => headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    [Fact]
    public void Disabled_config_injects_nothing()
    {
        var config = Config(b => b.Enabled(false).Status(503).Delay(100));

        var plan = FaultInjectionPlanner.Plan(config, NoHeaders, 0);

        Assert.True(plan.IsNoOp);
    }

    [Fact]
    public void Enabled_without_any_rule_injects_nothing()
    {
        // A half-filled binding must not answer requests with a plain 200: that looks like a drill
        // that passed, which is worse than no drill at all.
        var config = Config(b => b.Enabled(true));

        var plan = FaultInjectionPlanner.Plan(config, NoHeaders, 0);

        Assert.True(plan.IsNoOp);
    }

    [Theory]
    [InlineData(0.0, 0.0, false)]
    [InlineData(0.5, 0.49, true)]
    [InlineData(0.5, 0.5, false)]
    [InlineData(1.0, 0.999, true)]
    [InlineData(double.NaN, 0.0, false)]
    public void Probability_gate_is_a_boundary_not_a_vibe(double probability, double sample, bool expected)
    {
        var config = Config(b => b.Enabled(true).Status(503).Probability(probability));

        var plan = FaultInjectionPlanner.Plan(config, NoHeaders, sample);

        Assert.Equal(expected, !plan.IsNoOp);
    }

    [Fact]
    public void Header_gate_turns_the_drill_into_an_opt_in()
    {
        var config = Config(b => b.Enabled(true).Status(503).RequireHeader("X-Chaos", "on"));

        Assert.True(FaultInjectionPlanner.Plan(config, NoHeaders, 0).IsNoOp);
        Assert.True(FaultInjectionPlanner.Plan(config, Headers(("X-Chaos", "off")), 0).IsNoOp);
        Assert.False(FaultInjectionPlanner.Plan(config, Headers(("X-Chaos", "on")), 0).IsNoOp);
    }

    [Fact]
    public void Header_gate_without_a_value_matches_any_value()
    {
        var config = Config(b => b.Enabled(true).Status(503).RequireHeader("X-Chaos", null));

        Assert.True(FaultInjectionPlanner.Plan(config, NoHeaders, 0).IsNoOp);
        Assert.False(FaultInjectionPlanner.Plan(config, Headers(("X-Chaos", "anything")), 0).IsNoOp);
    }

    [Fact]
    public void Fixed_delay_is_used_as_written()
    {
        var config = Config(b => b.Enabled(true).Delay(250, 0));

        var plan = FaultInjectionPlanner.Plan(config, NoHeaders, 0.7);

        Assert.Equal(TimeSpan.FromMilliseconds(250), plan.Delay);
        Assert.Equal(FaultInjectionFailure.None, plan.Failure);
    }

    [Fact]
    public void Delay_range_is_sampled_between_its_bounds()
    {
        var config = Config(b => b.Enabled(true).Delay(100, 200));

        Assert.Equal(TimeSpan.FromMilliseconds(100), FaultInjectionPlanner.Plan(config, NoHeaders, 0).Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(199), FaultInjectionPlanner.Plan(config, NoHeaders, 0.999).Delay);
    }

    [Fact]
    public void Reversed_delay_range_falls_back_to_the_lower_bound()
    {
        // Guessing which end the author meant is worse than honouring the number they wrote.
        var config = Config(b => b.Enabled(true).Delay(300, 100));

        Assert.Equal(TimeSpan.FromMilliseconds(300), FaultInjectionPlanner.Plan(config, NoHeaders, 0.5).Delay);
    }

    [Fact]
    public void Delay_is_capped_so_a_typo_cannot_park_a_request()
    {
        var config = Config(b => b.Enabled(true).Delay(10_000_000, 0));

        Assert.Equal(FaultInjectionPlanner.MaxDelay, FaultInjectionPlanner.Plan(config, NoHeaders, 0).Delay);
    }

    [Theory]
    [InlineData(0, FaultInjectionFailure.None)]
    [InlineData(200, FaultInjectionFailure.None)]
    [InlineData(302, FaultInjectionFailure.None)]
    [InlineData(400, FaultInjectionFailure.StatusCode)]
    [InlineData(503, FaultInjectionFailure.StatusCode)]
    [InlineData(599, FaultInjectionFailure.StatusCode)]
    public void Only_client_and_server_error_codes_are_injected(int statusCode, FaultInjectionFailure expected)
    {
        var config = Config(b => b.Enabled(true).Status(statusCode));

        var plan = FaultInjectionPlanner.Plan(config, NoHeaders, 0);

        Assert.Equal(expected, plan.Failure);
    }

    [Fact]
    public void Abort_wins_over_a_status_code()
    {
        var config = Config(b => b.Enabled(true).Status(503).Abort());

        var plan = FaultInjectionPlanner.Plan(config, NoHeaders, 0);

        Assert.Equal(FaultInjectionFailure.Abort, plan.Failure);
        Assert.Equal(0, plan.StatusCode);
    }

    [Fact]
    public void Empty_error_body_falls_back_to_the_generic_one()
    {
        var config = Config(b => b.Enabled(true).Status(502));

        var plan = FaultInjectionPlanner.Plan(config, NoHeaders, 0);

        Assert.Equal(FaultInjectionPlanner.DefaultBody, plan.Body);
    }

    [Fact]
    public void Custom_error_body_is_used_as_written()
    {
        var config = Config(b => b.Enabled(true).Status(502).Body("upstream exploded"));

        Assert.Equal("upstream exploded", FaultInjectionPlanner.Plan(config, NoHeaders, 0).Body);
    }

    [Fact]
    public void Delay_and_failure_can_be_combined()
    {
        var config = Config(b => b.Enabled(true).Delay(120, 0).Status(504));

        var plan = FaultInjectionPlanner.Plan(config, NoHeaders, 0);

        Assert.Equal(TimeSpan.FromMilliseconds(120), plan.Delay);
        Assert.Equal(504, plan.StatusCode);
    }

    [Fact]
    public void Announce_can_be_turned_off_for_realism()
    {
        var config = Config(b => b.Enabled(true).Status(503).Announce(false));

        Assert.False(FaultInjectionPlanner.Plan(config, NoHeaders, 0).Announce);
    }

    /// <summary>Small builder so each test states only the field it cares about.</summary>
    private sealed class FaultInjectionExecutionConfigBuilder
    {
        private bool _enabled;
        private double _probability = 1.0;
        private int _delayMin;
        private int _delayMax;
        private int _status;
        private string? _body;
        private bool _abort;
        private string? _header;
        private string? _headerValue;
        private bool _announce = true;

        public FaultInjectionExecutionConfigBuilder Enabled(bool value) { _enabled = value; return this; }
        public FaultInjectionExecutionConfigBuilder Probability(double value) { _probability = value; return this; }
        public FaultInjectionExecutionConfigBuilder Delay(int min, int max = 0) { _delayMin = min; _delayMax = max; return this; }
        public FaultInjectionExecutionConfigBuilder Status(int value) { _status = value; return this; }
        public FaultInjectionExecutionConfigBuilder Body(string value) { _body = value; return this; }
        public FaultInjectionExecutionConfigBuilder Abort() { _abort = true; return this; }
        public FaultInjectionExecutionConfigBuilder Announce(bool value) { _announce = value; return this; }

        public FaultInjectionExecutionConfigBuilder RequireHeader(string? name, string? value)
        {
            _header = name;
            _headerValue = value;
            return this;
        }

        public FaultInjectionExecutionConfig Build() => new()
        {
            Enabled = _enabled,
            Probability = _probability,
            DelayMinMs = _delayMin,
            DelayMaxMs = _delayMax,
            ErrorStatusCode = _status,
            ErrorBody = _body,
            AbortConnection = _abort,
            RequiredHeader = _header,
            RequiredHeaderValue = _headerValue,
            Announce = _announce
        };
    }
}

/// <summary>
/// Covers the plumbing between a plugin binding and the config the middleware reads.
/// </summary>
/// <remarks>
/// This is the seam where a schema mismatch or a renamed property would silently disable the plugin:
/// the binding would validate, save, appear bound — and inject nothing.
/// </remarks>
public class FaultInjectionExecutionPlanTests
{
    [Fact]
    public void Binding_json_is_compiled_into_the_plan_by_route_id()
    {
        var plan = Compile("""
            {"enabled":true,"probability":0.25,"delayMinMs":30,"delayMaxMs":90,
             "errorStatusCode":503,"errorBody":"drill","abortConnection":false,
             "requiredHeader":"X-Chaos","requiredHeaderValue":"on","announce":false}
            """);

        Assert.True(plan.FaultInjectionByRoute.TryGetValue("r1", out var config));
        Assert.True(config!.Enabled);
        Assert.Equal(0.25, config.Probability);
        Assert.Equal(30, config.DelayMinMs);
        Assert.Equal(90, config.DelayMaxMs);
        Assert.Equal(503, config.ErrorStatusCode);
        Assert.Equal("drill", config.ErrorBody);
        Assert.Equal("X-Chaos", config.RequiredHeader);
        Assert.Equal("on", config.RequiredHeaderValue);
        Assert.False(config.Announce);
    }

    [Fact]
    public void Omitted_fields_keep_their_safe_defaults()
    {
        // The schema only requires "enabled", so this is what a user gets after ticking the box.
        var plan = Compile("""{"enabled":true}""");

        Assert.True(plan.FaultInjectionByRoute.TryGetValue("r1", out var config));
        Assert.Equal(1.0, config!.Probability);
        Assert.Equal(0, config.DelayMinMs);
        Assert.Equal(0, config.ErrorStatusCode);
        Assert.False(config.AbortConnection);
        Assert.True(config.Announce);
    }

    [Fact]
    public void Invalid_binding_json_does_not_reach_the_plan()
    {
        var plan = Compile("{ this is not json");

        Assert.False(plan.FaultInjectionByRoute.ContainsKey("r1"));
    }

    [Fact]
    public void A_route_without_a_binding_has_no_config()
    {
        var plan = Compile(null);

        Assert.Empty(plan.FaultInjectionByRoute);
    }

    private static GatewayPluginExecutionPlan Compile(string? configJson)
    {
        var route = new RouteConfig
        {
            RouteId = "r1",
            ClusterId = "c1",
            Match = new RouteMatch { Path = "/{**catchAll}" }
        };

        var routePlugins = configJson is null
            ? ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty
            : ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>>.Empty.Add(
                "r1",
                [new PluginBindingSnapshot("b1", "fault-injection", PluginBindingScope.Route, "r1", configJson, 1, 1, 0)]);

        var snapshot = GatewaySnapshot.Empty with
        {
            Version = 1,
            Routes = [route],
            RoutePlugins = routePlugins
        };

        var provider = new GatewayPluginExecutionPlanProvider(
            new FakePublisher(snapshot),
            NullLogger<GatewayPluginExecutionPlanProvider>.Instance);

        return provider.Current;
    }

    private sealed class FakePublisher(GatewaySnapshot snapshot) : IGatewaySnapshotPublisher
    {
        public GatewaySnapshot Current { get; } = snapshot;

        public GatewaySnapshot Publish(GatewaySnapshot value) => Current;
    }
}
