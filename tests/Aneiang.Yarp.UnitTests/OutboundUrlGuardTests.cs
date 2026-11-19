using Aneiang.Yarp.Dashboard.Infrastructure.Security;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// SEC-08: a webhook target is operator-supplied and fetched by the gateway, which is an SSRF
/// primitive unless the destination is checked.
/// </summary>
public sealed class OutboundUrlGuardTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8080/hook")]
    [InlineData("http://10.1.2.3/hook")]
    [InlineData("http://172.16.5.4/hook")]
    [InlineData("http://192.168.1.10/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://100.64.0.1/hook")]
    [InlineData("http://0.0.0.0/hook")]
    [InlineData("http://[::1]/hook")]
    [InlineData("http://[fd00::1]/hook")]
    public void Private_and_link_local_targets_are_refused(string url)
    {
        var check = OutboundUrlGuard.CheckLiteral(url);

        Assert.False(check.Allowed);
        Assert.Contains(check.Reason, new[] { "loopback", "private" });
    }

    [Theory]
    [InlineData("ftp://example.com/hook", "scheme")]
    [InlineData("file:///etc/passwd", "scheme")]
    [InlineData("not-a-url", "malformed")]
    [InlineData("", "malformed")]
    public void Non_http_schemes_and_garbage_are_refused(string url, string reason)
    {
        var check = OutboundUrlGuard.CheckLiteral(url);

        Assert.False(check.Allowed);
        Assert.Equal(reason, check.Reason);
    }

    [Theory]
    [InlineData("https://hooks.slack.com/services/T000/B000/xxxx")]
    [InlineData("http://8.8.8.8/hook")]
    [InlineData("https://oapi.dingtalk.com/robot/send?access_token=abc")]
    public void Public_targets_pass(string url)
    {
        Assert.True(OutboundUrlGuard.CheckLiteral(url).Allowed);
    }

    [Fact]
    public void A_host_name_is_not_resolved_by_the_literal_check()
    {
        // Save-time validation cannot do DNS; the send-time check is what catches this one.
        Assert.True(OutboundUrlGuard.CheckLiteral("http://localhost/hook").Allowed);
    }

    [Fact]
    public async Task A_name_that_resolves_to_loopback_is_refused()
    {
        // "localhost" resolves differently per machine; going through resolution is what makes the
        // check meaningful for a name rather than a literal.
        var check = await OutboundUrlGuard.CheckAsync("http://localhost:9000/hook");

        Assert.False(check.Allowed);
        Assert.Equal("loopback", check.Reason);
    }

    [Fact]
    public async Task An_unresolvable_name_is_refused_rather_than_assumed_public()
    {
        var check = await OutboundUrlGuard.CheckAsync("http://does-not-exist.invalid/hook");

        Assert.False(check.Allowed);
        Assert.Equal("unresolved", check.Reason);
    }
}
