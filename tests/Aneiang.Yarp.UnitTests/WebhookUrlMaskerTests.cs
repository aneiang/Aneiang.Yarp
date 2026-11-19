using Aneiang.Yarp.Dashboard.Infrastructure.Security;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// SEC-09: webhook URLs carry credentials (DingTalk <c>access_token</c>, Slack path secret). The UI
/// needs to keep the shape, the log must not keep the secret.
/// </summary>
public sealed class WebhookUrlMaskerTests
{
    private const string DingTalkUrl =
        "https://oapi.dingtalk.com/robot/send?access_token=1a2b3c4d5e6f7g8h&foo=bar";

    [Fact]
    public void Display_masking_hides_the_token_but_keeps_the_url_recognisable()
    {
        var masked = WebhookUrlMasker.MaskForDisplay(DingTalkUrl);

        Assert.DoesNotContain("1a2b3c4d5e6f7g8h", masked, StringComparison.Ordinal);
        Assert.Contains("oapi.dingtalk.com", masked, StringComparison.Ordinal);
        Assert.Contains("access_token=***", masked, StringComparison.Ordinal);
        Assert.Contains("foo=bar", masked, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://x.example/hook?token=abc")]
    [InlineData("https://x.example/hook?key=abc")]
    [InlineData("https://x.example/hook?secret=abc")]
    [InlineData("https://x.example/hook?sign=abc")]
    [InlineData("https://x.example/hook?api_key=abc")]
    [InlineData("https://x.example/hook?TOKEN=abc")]
    public void Display_masking_covers_every_token_style_parameter(string url)
    {
        Assert.DoesNotContain("=abc", WebhookUrlMasker.MaskForDisplay(url), StringComparison.Ordinal);
    }

    [Fact]
    public void Log_masking_keeps_only_scheme_host_and_a_digest()
    {
        var masked = WebhookUrlMasker.MaskForLog(DingTalkUrl);

        Assert.DoesNotContain("1a2b3c4d5e6f7g8h", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("robot", masked, StringComparison.Ordinal);
        Assert.StartsWith("https://oapi.dingtalk.com#", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_masking_is_stable_for_the_same_target()
    {
        // Two log lines about the same endpoint have to be matchable, or the digest is useless.
        Assert.Equal(WebhookUrlMasker.MaskForLog(DingTalkUrl), WebhookUrlMasker.MaskForLog(DingTalkUrl));
    }

    [Fact]
    public void Log_masking_differs_for_different_targets()
    {
        var a = WebhookUrlMasker.MaskForLog("https://oapi.dingtalk.com/robot/send?access_token=aaa");
        var b = WebhookUrlMasker.MaskForLog("https://oapi.dingtalk.com/robot/send?access_token=bbb");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void A_secret_in_the_path_does_not_survive_log_masking()
    {
        // Slack puts the credential in the path, not the query.
        var masked = WebhookUrlMasker.MaskForLog("https://hooks.slack.com/services/T000/B000/verysecret");

        Assert.DoesNotContain("verysecret", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("services", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Garbage_is_replaced_rather_than_echoed()
    {
        Assert.Equal("[unparsable-url]", WebhookUrlMasker.MaskForLog("not a url"));
        Assert.Equal(string.Empty, WebhookUrlMasker.MaskForLog(null));
        Assert.Equal(string.Empty, WebhookUrlMasker.MaskForDisplay(""));
    }
}
