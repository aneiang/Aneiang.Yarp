namespace Aneiang.Yarp.Models;

/// <summary>
/// Which forwarders may be believed when they say "the client is at X".
/// </summary>
/// <remarks>
/// <para>
/// Bound from <c>Gateway:Security</c>. Empty by default, and empty means <em>trust nothing</em>:
/// raw <c>X-Forwarded-For</c> is attacker-controlled, so honouring it unconditionally would let any
/// caller pick its own address — the exact failure the resolver was written to avoid.
/// </para>
/// <para>
/// Both fields are lists of the <b>immediate</b> proxy, not of every hop. When the gateway sits
/// behind nginx, list nginx; when it sits behind a cloud load balancer, list the balancer's subnet.
/// </para>
/// </remarks>
public sealed class TrustedProxyOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Gateway:Security";

    /// <summary>Proxy addresses to trust, e.g. <c>10.0.0.5</c> or <c>::1</c>.</summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary>Proxy subnets to trust, in CIDR form, e.g. <c>10.0.0.0/8</c>.</summary>
    public string[] TrustedNetworks { get; set; } = [];

    /// <summary>True when anything was configured. Nothing is trusted until it is.</summary>
    public bool IsConfigured => TrustedProxies.Length > 0 || TrustedNetworks.Length > 0;
}
