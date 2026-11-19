using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Aneiang.Yarp.Dashboard.Infrastructure.Security;

/// <summary>
/// Removes credentials from webhook URLs before they leave the process.
/// </summary>
/// <remarks>
/// <para>
/// Two audiences, two formats. The UI keeps the shape of the URL — an operator has to recognise
/// which endpoint is broken — but never the token. A log keeps only scheme, authority and a hash of
/// the path: enough to correlate two lines about the same endpoint, useless to whoever ends up
/// reading the log file.
/// </para>
/// <para>
/// DingTalk puts its signature in <c>access_token</c> and Slack puts a secret in the path, so both
/// forms have to be handled. Masking one and not the other would look like it worked.
/// </para>
/// </remarks>
public static partial class WebhookUrlMasker
{
    /// <summary>Masks token-bearing query parameters, keeping the URL readable.</summary>
    public static string MaskForDisplay(string? url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;

        try
        {
            return TokenParameterPattern().Replace(url, "***");
        }
        catch (RegexMatchTimeoutException)
        {
            return "[unreadable-url]";
        }
    }

    /// <summary>
    /// Reduces a URL to <c>scheme://host:port</c> plus a short digest of the path and query.
    /// </summary>
    /// <remarks>
    /// The digest is truncated SHA-256: it is deterministic, so two log lines about the same target
    /// can be matched, and it is not reversible, so a leaked log does not leak the token.
    /// </remarks>
    public static string MaskForLog(string? url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "[unparsable-url]";

        var digest = ShortHash(uri.AbsolutePath + uri.Query);

        return $"{uri.Scheme}://{uri.Authority}{digest}";
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "#" + Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    [GeneratedRegex(
        "(?<=[?&](?:access_token|token|key|secret|sign|password|apikey|api_key)=)[^&]+",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex TokenParameterPattern();
}
