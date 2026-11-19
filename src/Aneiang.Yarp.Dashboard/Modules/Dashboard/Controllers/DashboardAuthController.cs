using System.Text.Json;
using Aneiang.Yarp.Dashboard.Infrastructure;
using Aneiang.Yarp.Dashboard.Infrastructure.Auth;
using Aneiang.Yarp.Dashboard.Infrastructure.I18n;
using Aneiang.Yarp.Dashboard.Modules.Dashboard.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.Dashboard.Modules.Dashboard.Controllers;

/// <summary>
/// Authentication endpoints — login, logout, and two-factor authentication.
/// </summary>
public class DashboardAuthController : Controller
{
    private readonly IDashboardAuthorizationService _authService;

    private readonly string _defaultLocale;
    private readonly DashboardAuthMode _authMode;
    private readonly string? _jwtSecret;
    private readonly string? _jwtPassword;
    private readonly string? _jwtUsername;
    private readonly bool _enableTwoFactor;
    private readonly string? _twoFactorSecret;
    private readonly int _minPasswordLength;
    private readonly IReadOnlyList<DashboardUserOptions> _users;
    private readonly ILogger<DashboardAuthController> _logger;

    // Runtime 2FA state (persisted to file)
    private static readonly string _twoFactorStateFile = Path.Combine(AppContext.BaseDirectory, "twofactor-state.json");
    private static readonly object _twoFactorLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardAuthController"/> class.
    /// </summary>
    public DashboardAuthController(
        IDashboardAuthorizationService authService,
        IOptions<DashboardOptions> dashboardOptions,
        ILogger<DashboardAuthController> logger)
    {
        _authService = authService;
        _logger = logger;

        var opt = dashboardOptions.Value;
        _defaultLocale = opt.Locale;
        _authMode = opt.Auth.AuthMode;
        _jwtSecret = opt.Auth.JwtSecret;
        _jwtPassword = opt.Auth.JwtPassword;
        _jwtUsername = opt.Auth.JwtUsername;
        _enableTwoFactor = opt.Auth.EnableTwoFactor;
        _twoFactorSecret = opt.Auth.TwoFactorSecret;
        _minPasswordLength = opt.Auth.MinPasswordLength;
        _users = opt.Auth.Users;
    }

    #region Login Page

    /// <summary>Dashboard login page.</summary>
    [HttpGet("login")]
    public IActionResult Login()
    {
        ViewBag.DashboardRoutePrefix = DashboardPagesController.RoutePrefix;
        ViewBag.AuthMode = _authMode;
        var cookieLocale = Request.Cookies["dashboard_locale"];
        ViewBag.Locale = !string.IsNullOrEmpty(cookieLocale)
            ? (cookieLocale == "en-US" ? "en-US" : "zh-CN")
            : (_defaultLocale == "en-US" ? "en-US" : "zh-CN");
        ViewBag.AllI18nJson = DashboardI18n.AllAsJson(ViewBag.Locale);
        return View();
    }

    // ── Login / Logout ──

    /// <summary>Login POST — validate credentials and return JWT.</summary>
    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return Json(new { code = 400, message = "Username and password are required" });

        if (request.Password.Length < _minPasswordLength)
            return Json(new { code = 400, message = $"Password must be at least {_minPasswordLength} characters" });

        // Configured accounts take precedence; the legacy single username/password pair stays as the
        // fallback so an existing deployment keeps its login without touching configuration.
        DashboardUserOptions? account = _authMode is DashboardAuthMode.CustomJwt or DashboardAuthMode.DefaultJwt
            ? _users.FirstOrDefault(user =>
                string.Equals(user.Username, request.Username, StringComparison.OrdinalIgnoreCase))
            : null;

        bool valid;
        string? roleError;
        DashboardRole role;

        if (account is not null)
        {
            valid = string.Equals(account.Password, request.Password, StringComparison.Ordinal);
            role = DashboardRoles.Parse(account.Role, DashboardRole.Admin, out roleError);
        }
        else
        {
            valid = _authMode switch
            {
                DashboardAuthMode.CustomJwt =>
                    string.Equals(request.Username, _jwtUsername, StringComparison.Ordinal) &&
                    string.Equals(request.Password, _jwtPassword, StringComparison.Ordinal),
                DashboardAuthMode.DefaultJwt =>
                    string.Equals(request.Username, "admin", StringComparison.Ordinal) &&
                    string.Equals(request.Password, _jwtPassword, StringComparison.Ordinal),
                _ => false
            };
            role = DashboardRole.Admin;
            roleError = null;
        }

        if (!valid)
            return Json(new { code = 401, message = "Invalid credentials" });

        if (roleError is not null)
            _logger.LogWarning("Account '{Username}' has an unusable role: {Error}", request.Username, roleError);

        // 2FA verification: the account's own secret wins, the dashboard-wide one is the fallback
        // for deployments that enabled 2FA before accounts existed.
        var (twoFactorEnabled, twoFactorSecret) = GetTwoFactorState();
        var effectiveSecret = string.IsNullOrWhiteSpace(account?.TwoFactorSecret)
            ? twoFactorSecret
            : account!.TwoFactorSecret;

        if (twoFactorEnabled && !string.IsNullOrWhiteSpace(effectiveSecret))
        {
            if (string.IsNullOrWhiteSpace(request.TwoFactorCode))
                return Json(new { code = 202, message = "Two-factor authentication required", requiresTwoFactor = true });

            if (!TotpHelper.ValidateCode(effectiveSecret, request.TwoFactorCode))
                return Json(new { code = 401, message = "Invalid two-factor code" });
        }

        var token = DashboardJwtHelper.GenerateToken(request.Username, _jwtSecret!, role);

        Response.Cookies.Append("dashboard_token", token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTime.Now.AddHours(8)
        });

        return Json(new { code = 200, token });
    }

    /// <summary>Logout — clear the auth token cookie.</summary>
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("dashboard_token");
        return Json(new { code = 200, message = "Logged out successfully" });
    }

    #endregion

    #region Two-Factor Authentication

    /// <summary>Get 2FA status.</summary>
    [HttpGet("api/2fa/status")]
    public IActionResult GetTwoFactorStatus()
    {
        var (enabled, _) = GetTwoFactorState();
        return Json(new { code = 200, data = new { enabled, minPasswordLength = _minPasswordLength } });
    }

    /// <summary>Generate a new 2FA secret and QR URL.</summary>
    [HttpGet("api/2fa/setup")]
    public IActionResult SetupTwoFactor()
    {
        var secret = TotpHelper.GenerateSecret();
        var issuer = "Gateway Dashboard";
        var account = _jwtUsername ?? "admin";
        var qrUrl = TotpHelper.BuildOtpAuthUri(issuer, account, secret);
        return Json(new { code = 200, data = new { secret, qrUrl } });
    }

    /// <summary>Verify 2FA code and enable 2FA.</summary>
    [HttpPost("api/2fa/verify")]
    public IActionResult VerifyTwoFactor([FromBody] JsonElement body)
    {
        var code = body.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
        var secret = body.TryGetProperty("secret", out var secretEl) ? secretEl.GetString() : null;

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(secret))
            return Json(new { code = 400, message = "Code and secret are required" });

        if (!TotpHelper.ValidateCode(secret, code))
            return Json(new { code = 400, message = "Invalid two-factor code" });

        SaveTwoFactorState(true, secret);
        return Json(new { code = 200, message = "Two-factor authentication enabled" });
    }

    /// <summary>Disable 2FA.</summary>
    [HttpPost("api/2fa/disable")]
    public IActionResult DisableTwoFactor()
    {
        SaveTwoFactorState(false, null);
        return Json(new { code = 200, message = "Two-factor authentication disabled" });
    }

    // ── 2FA State Persistence ──

    private (bool enabled, string? secret) GetTwoFactorState()
    {
        try
        {
            if (System.IO.File.Exists(_twoFactorStateFile))
            {
                var json = System.IO.File.ReadAllText(_twoFactorStateFile);
                var state = System.Text.Json.JsonSerializer.Deserialize<TwoFactorState>(json);
                if (state != null)
                    return (state.Enabled, state.Secret);
            }
        }
        catch { }

        return (_enableTwoFactor, _twoFactorSecret);
    }

    private void SaveTwoFactorState(bool enabled, string? secret)
    {
        lock (_twoFactorLock)
        {
            try
            {
                var state = new TwoFactorState { Enabled = enabled, Secret = secret };
                var json = System.Text.Json.JsonSerializer.Serialize(state);
                System.IO.File.WriteAllText(_twoFactorStateFile, json);
            }
            catch { }
        }
    }

    private class TwoFactorState
    {
        public bool Enabled { get; set; }
        public string? Secret { get; set; }
    }

    #endregion

    #region Auth Status

    /// <summary>Get current authorization status and mode.</summary>
    /// <remarks>
    /// The role is reported for display only. Every endpoint still re-checks it, because this
    /// response is a statement about the past — a token can be replaced between page load and click.
    /// </remarks>
    [HttpGet("api/auth/status")]
    public IActionResult GetAuthStatus()
    {
        var authModeDescription = _authService.GetAuthModeDescription();
        var isAuthEnabled = _authMode != DashboardAuthMode.None;
        var principal = _authService.ResolvePrincipal(HttpContext);

        return Json(new
        {
            code = 200,
            data = new
            {
                IsAuthEnabled = isAuthEnabled,
                AuthMode = _authMode.ToString(),
                AuthModeDescription = authModeDescription,
                Locale = _defaultLocale,
                Username = principal.Username,
                Role = principal.Role.ToClaimValue(),
                // True when nobody chose this role (auth off, API key, custom delegate). The banner
                // that warns "read-only" would be a lie in that case, so the UI keys off this.
                IsImplicitRole = principal.Implicit,
                CanWrite = principal.Role.CanWrite(),
                CanAdminister = principal.Role.CanAdminister()
            }
        });
    }

    #endregion
}
