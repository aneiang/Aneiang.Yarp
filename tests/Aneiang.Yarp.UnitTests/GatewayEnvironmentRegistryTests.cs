using Aneiang.Yarp.GatewayEnvironments;
using Aneiang.Yarp.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Tests for <see cref="GatewayEnvironmentRegistry"/>. The behaviour worth pinning here is the
/// split between "what we serve" and "what we edit" — everything else is bookkeeping.
/// </summary>
public class GatewayEnvironmentRegistryTests
{
    private static GatewayEnvironmentRegistry Create(string runtime = "prod", params string[] names)
        => new(
            Options.Create(new StorageOptions
            {
                Environments = new StorageEnvironmentOptions
                {
                    Runtime = runtime,
                    Names = names.Length == 0 ? new List<string> { runtime } : names.ToList()
                }
            }),
            NullLogger<GatewayEnvironmentRegistry>.Instance);

    [Fact]
    public void Default_configuration_has_one_environment_and_nothing_to_switch()
    {
        var registry = Create();

        Assert.Equal("prod", registry.RuntimeEnvironment);
        Assert.Equal("prod", registry.CurrentEnvironment);
        Assert.True(registry.IsEditingRuntime);
        Assert.Single(registry.Environments);
    }

    [Fact]
    public void Runtime_is_always_part_of_the_list_even_when_it_is_not_spelled_out()
    {
        // A config that lists only non-runtime environments would otherwise leave the operator
        // unable to switch back to the one they are actually serving.
        var registry = Create("prod", "dev", "staging");

        Assert.Equal(new[] { "prod", "dev", "staging" }, registry.Environments.Select(e => e.Name));
        Assert.True(registry.Environments.Single(e => e.Name == "prod").IsRuntime);
        Assert.False(registry.Environments.Single(e => e.Name == "dev").IsRuntime);
    }

    [Fact]
    public void Runtime_is_listed_first()
    {
        var registry = Create("staging", "dev", "prod");

        Assert.Equal("staging", registry.Environments[0].Name);
        Assert.True(registry.Environments[0].IsRuntime);
    }

    [Fact]
    public void Duplicate_names_are_collapsed_case_insensitively()
    {
        var registry = Create("prod", "dev", "DEV", "Prod");

        Assert.Equal(2, registry.Environments.Count);
        Assert.Equal(new[] { "prod", "dev" }, registry.Environments.Select(e => e.Name));
    }

    [Fact]
    public void Switching_changes_the_editing_view_and_leaves_the_runtime_alone()
    {
        var registry = Create("prod", "prod", "dev");

        registry.SwitchTo("dev");

        Assert.Equal("dev", registry.CurrentEnvironment);
        Assert.Equal("prod", registry.RuntimeEnvironment);
        Assert.False(registry.IsEditingRuntime);

        registry.SwitchTo("prod");

        Assert.True(registry.IsEditingRuntime);
    }

    [Fact]
    public void Switching_is_case_insensitive_and_idempotent()
    {
        var registry = Create("prod", "prod", "dev");

        registry.SwitchTo("DEV");
        registry.SwitchTo("dev");

        Assert.Equal("dev", registry.CurrentEnvironment);
    }

    [Fact]
    public void Unknown_environment_is_rejected_without_changing_the_view()
    {
        var registry = Create("prod", "prod", "dev");

        var ex = Assert.Throws<ArgumentException>(() => registry.SwitchTo("staging"));

        Assert.Contains("prod", ex.Message);
        Assert.Contains("dev", ex.Message);
        Assert.Equal("prod", registry.CurrentEnvironment);
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("dev/../../prod")]
    [InlineData("prod.db")]
    [InlineData("pr od")]
    [InlineData("生产")]
    public void Environment_names_that_could_escape_the_store_directory_are_rejected(string name)
    {
        // Names end up in a database file name, so this is the only place path traversal can be
        // stopped. Anything that is not [A-Za-z0-9_-] has to fail loudly at configuration time.
        Assert.Throws<ArgumentException>(() => Create("prod", "prod", name));
    }

    [Fact]
    public void Runtime_name_is_validated_too()
    {
        Assert.Throws<ArgumentException>(() => Create("../prod", "../prod"));
    }

    [Fact]
    public void Blank_entries_in_the_name_list_are_skipped_rather_than_becoming_an_environment()
    {
        var registry = Create("prod", "prod", "", "  ", "dev");

        Assert.Equal(new[] { "prod", "dev" }, registry.Environments.Select(e => e.Name));
    }
}
