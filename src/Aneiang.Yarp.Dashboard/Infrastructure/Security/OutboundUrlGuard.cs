using System.Net;
using System.Net.Sockets;

namespace Aneiang.Yarp.Dashboard.Infrastructure.Security;

/// <summary>Outcome of an outbound-target check.</summary>
/// <param name="Allowed">True when the target may be contacted.</param>
/// <param name="Reason">Machine-readable cause when refused: <c>scheme</c>, <c>malformed</c>, <c>unresolved</c>, <c>private</c>, <c>loopback</c>.</param>
/// <param name="Addresses">Resolved addresses, for the log line that explains a refusal.</param>
public sealed record OutboundUrlCheck(bool Allowed, string? Reason = null, string? Addresses = null)
{
    public static OutboundUrlCheck Ok(string? addresses = null) => new(true, null, addresses);
    public static OutboundUrlCheck Deny(string reason, string? addresses = null) => new(false, reason, addresses);
}

/// <summary>
/// Decides whether an operator-supplied URL may be used as an outbound destination.
/// </summary>
/// <remarks>
/// <para>
/// Webhook targets are typed by an admin and then fetched by the gateway. Without this check that
/// is an SSRF primitive: <c>http://169.254.169.254/latest/meta-data/</c> reaches the cloud metadata
/// service, <c>http://127.0.0.1:8500/v1/...</c> reaches whatever is listening on loopback, and both
/// answer 200 to a POST often enough to be worth trying.
/// </para>
/// <para>
/// Every resolved address is checked, not just the first: a name can point at a public address and
/// a private one at the same time, and which one gets used depends on the resolver.
/// </para>
/// <para>
/// DNS rebinding is not defeated here (the address is resolved once, then used again by the
/// handler). The guard exists to stop the mistake and the easy pivot, and the escape hatch is
/// documented rather than hidden: internal webhooks are a legitimate deployment.
/// </para>
/// </remarks>
public static class OutboundUrlGuard
{
    /// <summary>Checks a URL without resolving it. Loopback and private literals are still refused.</summary>
    public static OutboundUrlCheck CheckLiteral(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return OutboundUrlCheck.Deny("malformed");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return OutboundUrlCheck.Deny("malformed");

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return OutboundUrlCheck.Deny("scheme");

        // A literal address needs no DNS round trip, and checking it here means a numeric URL is
        // refused even when resolution is unavailable.
        if (IPAddress.TryParse(uri.DnsSafeHost, out var literal))
            return IsPublic(literal) ? OutboundUrlCheck.Ok(literal.ToString()) : OutboundUrlCheck.Deny(ReasonFor(literal), literal.ToString());

        return OutboundUrlCheck.Ok();
    }

    /// <summary>Checks a URL including the addresses its host resolves to.</summary>
    public static async Task<OutboundUrlCheck> CheckAsync(string? url, CancellationToken ct = default)
    {
        var literal = CheckLiteral(url);
        if (!literal.Allowed || literal.Addresses is not null) return literal;

        var uri = new Uri(url!, UriKind.Absolute);

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // Refusing is the safe answer: an unresolvable target cannot be delivered to anyway, and
            // treating "cannot resolve" as "assume public" would be the one guess that matters.
            return OutboundUrlCheck.Deny("unresolved");
        }

        if (addresses.Length == 0) return OutboundUrlCheck.Deny("unresolved");

        var joined = string.Join(", ", addresses.Select(a => a.ToString()));

        foreach (var address in addresses)
        {
            if (!IsPublic(address)) return OutboundUrlCheck.Deny(ReasonFor(address), joined);
        }

        return OutboundUrlCheck.Ok(joined);
    }

    /// <summary>True for addresses that are routable on the public internet.</summary>
    private static bool IsPublic(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Unique local (fc00::/7) and the IPv4-mapped forms of everything below.
            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return false;
            if (address.IsIPv4MappedToIPv6) return IsPublic(address.MapToIPv4());
            return true;
        }

        var v4 = address.GetAddressBytes();
        return v4[0] switch
        {
            0 => false,                                        // 0.0.0.0/8
            10 => false,                                       // 10/8
            127 => false,                                      // loopback
            169 when v4[1] == 254 => false,                    // link-local + cloud metadata
            172 when v4[1] >= 16 && v4[1] <= 31 => false,      // 172.16/12
            192 when v4[1] == 168 => false,                    // 192.168/16
            100 when v4[1] >= 64 && v4[1] <= 127 => false,     // CGNAT 100.64/10
            >= 224 => false,                                   // multicast + reserved
            _ => true
        };
    }

    private static string ReasonFor(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || (address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes()[0] == 127))
            return "loopback";

        return "private";
    }
}
