using System.Net;
using Aneiang.Yarp.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
#if !NET10_0_OR_GREATER
using Network = Microsoft.AspNetCore.HttpOverrides.IPNetwork;
#endif

namespace Aneiang.Yarp.Infrastructure.Security;

/// <summary>
/// Turns <see cref="TrustedProxyOptions"/> into <see cref="ForwardedHeadersOptions"/>, or into
/// nothing at all.
/// </summary>
/// <remarks>
/// <para>
/// Returning <c>null</c> is a meaningful result, not a failure: it means "do not install the
/// middleware". The alternative — installing it with an empty trust list — would still rewrite
/// <c>RemoteIpAddress</c> from a header the very first hop chose, and an empty allow-list is easy to
/// mistake for "configured".
/// </para>
/// <para>
/// <c>ForwardLimit = 1</c> is deliberate. With a higher limit the middleware walks further left in
/// <c>X-Forwarded-For</c>, and everything left of the first trusted hop is client-supplied text.
/// One hop, one proxy, one believable address.
/// </para>
/// </remarks>
public static class TrustedProxyConfigurator
{
    /// <summary>
    /// Builds the middleware options, or returns null when no proxy is configured.
    /// </summary>
    /// <param name="options">Bound configuration.</param>
    /// <param name="warning">Set when an entry could not be parsed; it is skipped, not trusted.</param>
    public static ForwardedHeadersOptions? Build(TrustedProxyOptions options, out string? warning)
    {
        warning = null;

        if (!options.IsConfigured)
            return null;

        var forwarded = new ForwardedHeadersOptions
        {
            // X-Forwarded-Host is deliberately absent: the dashboard's origin does not depend on it,
            // and honouring it turns a header into a redirect target.
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1
        };

        // The framework trusts loopback out of the box. Starting from an empty list keeps the rule
        // simple to state: an address is trusted because it was written down, nothing else.
        forwarded.KnownProxies.Clear();
        ClearNetworks(forwarded);

        var rejected = new List<string>();

        foreach (var entry in options.TrustedProxies)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;

            // An entry with a slash belongs in the other list; silently accepting it here would
            // look like a working configuration that trusts nothing.
            if (entry.Contains('/', StringComparison.Ordinal) || !IPAddress.TryParse(entry.Trim(), out var address))
            {
                rejected.Add(entry);
                continue;
            }

            forwarded.KnownProxies.Add(address);
        }

        foreach (var entry in options.TrustedNetworks)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;

            if (!TryParseNetwork(entry.Trim(), out var prefix, out var length))
            {
                rejected.Add(entry);
                continue;
            }

            AddNetwork(forwarded, prefix, length);
        }

        if (rejected.Count > 0)
            warning = $"Ignored {rejected.Count} unparsable trusted proxy entr{(rejected.Count == 1 ? "y" : "ies")}: {string.Join(", ", rejected)}.";

        if (forwarded.KnownProxies.Count == 0 && NetworkCount(forwarded) == 0)
            return null;

        return forwarded;
    }

    /// <summary>Number of trusted networks on either framework generation.</summary>
    internal static int NetworkCount(ForwardedHeadersOptions options)
#if NET10_0_OR_GREATER
        => options.KnownIPNetworks.Count;
#else
        => options.KnownNetworks.Count;
#endif

    /// <remarks>
    /// .NET 10 replaced <c>KnownNetworks</c> (element type <c>Microsoft.AspNetCore.HttpOverrides.IPNetwork</c>)
    /// with <c>KnownIPNetworks</c> (element type <c>System.Net.IPNetwork</c>). Same job, different names:
    /// the old pair is obsolete on net10, the new pair does not exist before it. One split here beats a
    /// blanket <c>NoWarn</c> that would also hide the next rename.
    /// </remarks>
    private static void AddNetwork(ForwardedHeadersOptions options, IPAddress prefix, int length)
    {
#if NET10_0_OR_GREATER
        options.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, length));
#else
        options.KnownNetworks.Add(new Network(prefix, length));
#endif
    }

    private static void ClearNetworks(ForwardedHeadersOptions options)
    {
#if NET10_0_OR_GREATER
        options.KnownIPNetworks.Clear();
#else
        options.KnownNetworks.Clear();
#endif
    }

    private static bool TryParseNetwork(string value, out IPAddress prefix, out int length)
    {
        prefix = IPAddress.None;
        length = 0;

        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == value.Length - 1) return false;

        if (!IPAddress.TryParse(value[..slash], out var address)) return false;
        if (!int.TryParse(value[(slash + 1)..], out var parsedLength)) return false;

        var maxLength = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (parsedLength < 0 || parsedLength > maxLength) return false;

        prefix = address;
        length = parsedLength;
        return true;
    }
}
