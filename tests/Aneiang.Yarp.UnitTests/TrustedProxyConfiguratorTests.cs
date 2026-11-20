using System.Net;
using Aneiang.Yarp.Infrastructure.Security;
using Aneiang.Yarp.Models;
using Microsoft.AspNetCore.HttpOverrides;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// SEC-03: forwarded headers are only believed when the proxy in front is named. "Not configured"
/// has to be a real code path, not an empty allow-list that still rewrites the client address.
/// </summary>
public sealed class TrustedProxyConfiguratorTests
{
    [Fact]
    public void Nothing_configured_installs_no_middleware()
    {
        var result = TrustedProxyConfigurator.Build(new TrustedProxyOptions(), out var warning);

        Assert.Null(result);
        Assert.Null(warning);
    }

    [Fact]
    public void A_blank_entry_does_not_count_as_configuration()
    {
        var result = TrustedProxyConfigurator.Build(
            new TrustedProxyOptions { TrustedProxies = ["", "   "] }, out _);

        Assert.Null(result);
    }

    [Fact]
    public void A_trusted_proxy_is_listed_and_the_hop_limit_is_one()
    {
        var result = TrustedProxyConfigurator.Build(
            new TrustedProxyOptions { TrustedProxies = ["10.0.0.5"] }, out var warning);

        Assert.NotNull(result);
        Assert.Null(warning);
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, result!.ForwardedHeaders);

        // Two hops would mean walking past the trusted proxy into client-supplied text.
        Assert.Equal(1, result.ForwardLimit);
        Assert.Single(result.KnownProxies);
        Assert.Equal(0, TrustedProxyConfigurator.NetworkCount(result));
        Assert.Equal(IPAddress.Parse("10.0.0.5"), result.KnownProxies.Single());
    }

    [Fact]
    public void Loopback_is_not_trusted_unless_it_is_written_down()
    {
        // The framework trusts ::1 and 127.0.0.1 by default. Inheriting that here would make the
        // trust list depend on a framework default rather than on configuration.
        var result = TrustedProxyConfigurator.Build(
            new TrustedProxyOptions { TrustedProxies = ["10.0.0.5"] }, out _);

        Assert.DoesNotContain(IPAddress.Loopback, result!.KnownProxies);
        Assert.DoesNotContain(IPAddress.IPv6Loopback, result.KnownProxies);
    }

    [Fact]
    public void A_cidr_network_becomes_a_trusted_network()
    {
        var result = TrustedProxyConfigurator.Build(
            new TrustedProxyOptions { TrustedNetworks = ["10.0.0.0/8"] }, out var warning);

        Assert.NotNull(result);
        Assert.Null(warning);
        Assert.Equal(1, TrustedProxyConfigurator.NetworkCount(result!));
        Assert.Empty(result.KnownProxies);
    }

    [Theory]
    [InlineData("10.0.0.0/99")]
    [InlineData("not-an-ip/8")]
    [InlineData("10.0.0.0/")]
    [InlineData("/8")]
    [InlineData("10.0.0.1")]
    public void A_malformed_network_entry_is_skipped_with_a_warning(string entry)
    {
        var result = TrustedProxyConfigurator.Build(
            new TrustedProxyOptions { TrustedNetworks = [entry] }, out var warning);

        Assert.Null(result);
        Assert.NotNull(warning);
        Assert.Contains(entry, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_valid_entry_survives_a_neighbouring_typo()
    {
        // Partial configuration is the common case (one line edited by hand). Silently dropping the
        // typo and keeping the rest beats either ignoring everything or starting up trustingly.
        var result = TrustedProxyConfigurator.Build(
            new TrustedProxyOptions { TrustedProxies = ["10.0.0.5", "10.0.0.O"] }, out var warning);

        Assert.NotNull(result);
        Assert.Single(result!.KnownProxies);
        Assert.NotNull(warning);
        Assert.Contains("10.0.0.O", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cidr_pasted_into_the_proxy_list_is_reported_instead_of_silently_parseable()
    {
        // IPAddress.TryParse("10.0.0.0/8") is false, so this would already fail; the point is that
        // it is reported rather than treated as "no proxies configured".
        var result = TrustedProxyConfigurator.Build(
            new TrustedProxyOptions { TrustedProxies = ["10.0.0.0/8"] }, out var warning);

        Assert.Null(result);
        Assert.NotNull(warning);
    }
}
