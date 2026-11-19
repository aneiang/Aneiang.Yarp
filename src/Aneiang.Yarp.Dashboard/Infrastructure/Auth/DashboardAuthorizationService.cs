using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.Dashboard.Infrastructure.Auth;

/// <summary>
/// Standard dashboard authorization service.
/// Supports all auth modes: None, ApiKey, CustomJwt, DefaultJwt, and custom delegate.
/// </summary>
public sealed class DashboardAuthorizationService : IDashboardAuthorizationService
{
    private readonly DashboardOptions _options;
    private readonly ILogger<DashboardAuthorizationService> _logger;

    /// <summary>
    /// Creates the authorization service.
    /// </summary>
    public DashboardAuthorizationService(IOptions<DashboardOptions> options, ILogger<DashboardAuthorizationService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> IsAuthorizedAsync(HttpContext context)
    {
        // Priority 1: Custom delegate (highest)
        if (_options.Auth.AuthorizeRequest != null)
        {
            return await _options.Auth.AuthorizeRequest(context);
        }

        // Priority 2: API Key
        if (_options.Auth.AuthMode == DashboardAuthMode.ApiKey && !string.IsNullOrEmpty(_options.Auth.ApiKey))
        {
            return CheckApiKey(context);
        }

        // Priority 3: JWT (DefaultJwt / CustomJwt)
        if (_options.Auth.AuthMode is DashboardAuthMode.CustomJwt or DashboardAuthMode.DefaultJwt)
        {
            return CheckJwt(context);
        }

        // AuthMode.None - no authorization required
        return true;
    }

    /// <inheritdoc />
    public string GetAuthModeDescription()
    {
        if (_options.Auth.AuthorizeRequest != null)
            return "Custom Delegate";

        var accounts = _options.Auth.Users.Count;
        var suffix = accounts > 0 ? $" · {accounts} account(s)" : string.Empty;

        return _options.Auth.AuthMode switch
        {
            DashboardAuthMode.None => "None",
            DashboardAuthMode.ApiKey => "API Key",
            DashboardAuthMode.CustomJwt => $"JWT (User: {_options.Auth.JwtUsername ?? "N/A"}){suffix}",
            DashboardAuthMode.DefaultJwt => $"JWT (Admin){suffix}",
            _ => "Unknown"
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Role resolution follows the credential: a token carries its role, an API key is a machine
    /// credential and is treated as admin, and an unauthenticated deployment is an implicit admin.
    /// Tokens minted before roles existed fall back to the configured account of the same name —
    /// they expire within eight hours, so this is a one-release bridge rather than a policy.
    /// </remarks>
    public DashboardPrincipal ResolvePrincipal(HttpContext context)
    {
        // A host-provided delegate decides everything; we cannot infer a role from it, and guessing
        // "viewer" would break every existing integration that authorizes service accounts there.
        if (_options.Auth.AuthorizeRequest != null)
            return new DashboardPrincipal("delegate", DashboardRole.Admin, Implicit: true);

        if (_options.Auth.AuthMode == DashboardAuthMode.ApiKey)
            return new DashboardPrincipal("api-key", DashboardRole.Admin, Implicit: true);

        if (_options.Auth.AuthMode is DashboardAuthMode.CustomJwt or DashboardAuthMode.DefaultJwt)
        {
            var secret = _options.Auth.JwtSecret;
            var token = secret is null ? null : ExtractJwtToken(context);

            if (token is not null)
            {
                var details = DashboardJwtHelper.ValidateTokenDetails(token, secret!);
                if (details.Valid)
                {
                    var username = details.Username ?? "unknown";
                    var role = details.Role ?? RoleOfConfiguredUser(username);
                    return new DashboardPrincipal(username, role, Implicit: false);
                }
            }

            // Authenticated by something we could not attribute (should not happen: the filter runs
            // after IsAuthorizedAsync). Treat as the lowest privilege that can still see the pages.
            return new DashboardPrincipal("unknown", DashboardRole.Viewer, Implicit: false);
        }

        return new DashboardPrincipal("anonymous", DashboardRole.Admin, Implicit: true);
    }

    private DashboardRole RoleOfConfiguredUser(string username)
    {
        var account = _options.Auth.Users.FirstOrDefault(user =>
            string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase));

        if (account is not null)
            return DashboardRoles.Parse(account.Role, DashboardRole.Admin, out _);

        return DashboardRole.Admin;
    }

    /// <summary>
    /// Checks API key authorization.
    /// </summary>
    private bool CheckApiKey(HttpContext context)
    {
        var apiKey = _options.Auth.ApiKey!;
        var headerName = _options.Auth.ApiKeyHeaderName;

        // Check header
        if (context.Request.Headers.TryGetValue(headerName, out var hv) && 
            hv.Any(v => v == apiKey))
        {
            return true;
        }

        // Query-string credentials are intentionally rejected because URLs commonly leak
        // through access logs, browser history and referrers.
        return false;
    }

    /// <summary>
    /// Checks JWT authorization.
    /// </summary>
    private bool CheckJwt(HttpContext context)
    {
        var secret = _options.Auth.JwtSecret;
        if (string.IsNullOrEmpty(secret))
            return false;

        var token = ExtractJwtToken(context);
        if (string.IsNullOrEmpty(token))
            return false;

        var (valid, _) = DashboardJwtHelper.ValidateToken(token, secret);
        if (!valid)
        {
            _logger.LogWarning(
                "Dashboard JWT validation failed for {Path}. RemoteIp: {RemoteIp}",
                context.Request.Path,
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        }

        return valid;
    }

    private static string? ExtractJwtToken(HttpContext context)
    {
        // Authorization header (for XHR/API calls)
        var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authHeader[7..];
        }

        // SignalR only: browser WebSocket clients cannot always send Authorization headers.
        // Restrict query-string tokens to the two dashboard hub endpoints.
        if (context.Request.Path.Value?.Contains("/hubs/", StringComparison.OrdinalIgnoreCase) == true)
        {
            var queryToken = context.Request.Query["access_token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(queryToken))
                return queryToken;
        }

        // dashboard_token cookie (for browser page loads)
        if (context.Request.Cookies.TryGetValue("dashboard_token", out var cookieToken)
            && !string.IsNullOrEmpty(cookieToken))
        {
            return cookieToken;
        }

        return null;
    }
}
