namespace Aneiang.Yarp.Dashboard.Infrastructure.Security;

/// <summary>
/// Response hardening for the dashboard's own pages. Bound from
/// <c>Gateway:Dashboard:Security</c>.
/// </summary>
/// <remarks>
/// The static headers are always on: they cost nothing and none of them can break a page. The two
/// that can break something are opt-in with a safe default — HSTS is only emitted over HTTPS, and
/// the content policy ships in report-only mode until someone has read the violations.
/// </remarks>
public sealed class DashboardSecurityOptions
{
    /// <summary>
    /// Emit <c>Content-Security-Policy</c> instead of <c>-Report-Only</c>. Default false: the
    /// dashboard ships inline bootstrap scripts, so a policy strict enough to matter will report a
    /// lot before it stops anything. Turn this on after the report stream goes quiet.
    /// </summary>
    public bool EnforceContentSecurityPolicy { get; set; }

    /// <summary>Send <c>Strict-Transport-Security</c>. Only has an effect on HTTPS responses.</summary>
    public bool EnableHsts { get; set; } = true;

    /// <summary>HSTS max-age in seconds. Default one year.</summary>
    public int HstsMaxAgeSeconds { get; set; } = 31_536_000;
}
