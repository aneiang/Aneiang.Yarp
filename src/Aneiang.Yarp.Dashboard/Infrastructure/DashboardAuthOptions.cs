using Aneiang.Yarp.Dashboard.Infrastructure.Auth;
using Microsoft.AspNetCore.Http;

namespace Aneiang.Yarp.Dashboard.Infrastructure;

/// <summary>
/// Authentication options for Dashboard access.
/// Extracted from DashboardOptions to separate auth concerns from other configuration.
/// </summary>
public class DashboardAuthOptions
{
    /// <summary>
    /// Authorization mode for accessing the dashboard.
    /// </summary>
    public DashboardAuthMode AuthMode { get; set; } = DashboardAuthMode.None;

    /// <summary>API key value. Clients pass it via the configured header (default: X-Api-Key).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Header name for ApiKey mode. Default: X-Api-Key.</summary>
    public string ApiKeyHeaderName { get; set; } = "X-Api-Key";

    /// <summary>JWT signing secret. Auto-generated if not set (invalidated on restart).</summary>
    public string? JwtSecret { get; set; }

    /// <summary>Username for CustomJwt mode.</summary>
    public string? JwtUsername { get; set; }

    /// <summary>Password for JWT login (required for both CustomJwt and DefaultJwt).</summary>
    public string? JwtPassword { get; set; }

    /// <summary>
    /// Enable Two-Factor Authentication (TOTP) for JWT login.
    /// Default: false.
    /// </summary>
    public bool EnableTwoFactor { get; set; }

    /// <summary>
    /// Base32-encoded TOTP shared secret for 2FA.
    /// If null while EnableTwoFactor is true, 2FA is skipped (development mode).
    /// </summary>
    public string? TwoFactorSecret { get; set; }

    /// <summary>
    /// Minimum password length for JWT login. Default: 6.
    /// </summary>
    public int MinPasswordLength { get; set; } = 6;

    /// <summary>Custom auth delegate. If set, takes precedence over all other auth modes.</summary>
    public Func<HttpContext, Task<bool>>? AuthorizeRequest { get; set; }

    /// <summary>
    /// Accounts and their roles. When empty, the single <see cref="JwtUsername"/>/<see cref="JwtPassword"/>
    /// pair is used and it is treated as an admin — the behaviour every existing deployment has.
    /// </summary>
    /// <remarks>
    /// Roles are enforced server-side by HTTP method and path, not by hiding buttons: a viewer's
    /// every non-GET request is refused whether or not the page they used still offered the button.
    /// </remarks>
    public List<DashboardUserOptions> Users { get; set; } = [];
}
