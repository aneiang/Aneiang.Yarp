using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Aneiang.Yarp.Dashboard.Infrastructure;
using Aneiang.Yarp.Dashboard.Infrastructure.Auth;
using Aneiang.Yarp.Dashboard.Infrastructure.I18n;
using Aneiang.Yarp.Dashboard.Infrastructure.Security;
using Aneiang.Yarp.Dashboard.Modules.Dashboard.Services;
using Aneiang.Yarp.Infrastructure;
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
    private readonly ISecurityAuditWriter _audit;
    private readonly LoginThrottle _throttle;
    private readonly DashboardTokenRevocationStore _revocations;

    /// <summary>Accounts already warned about, so a plaintext password does not log on every login.</summary>
    private static readonly ConcurrentDictionary<string, byte> PlaintextWarned = new(StringComparer.OrdinalIgnoreCase);

    private static int _insecureCookieWarned;

    // Runtime 2FA state (persisted to file)
    private static readonly string _twoFactorStateFile = Path.Combine(AppContext.BaseDirectory, "twofactor-state.json");
    private static readonly object _twoFactorLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardAuthController"/> class.
    /// </summary>
    public DashboardAuthController(
        IDashboardAuthorizationService authService,
        IOptions<DashboardOptions> dashboardOptions,
        ISecurityAuditWriter audit,
        LoginThrottle throttle,
        DashboardTokenRevocationStore revocations,
        ILogger<DashboardAuthController> logger)
    {
        _authService = authService;
        _logger = logger;
        _audit = audit;
        _throttle = throttle;
        _revocations = revocations;

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

    /// <summary>Login POST — validate credentials and open a session cookie.</summary>
    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return Json(new { code = 400, message = "Username and password are required" });

        // Rate limit before the password check, not after: the point is to stop the guessing, not to
        // measure it. Both the account and the address carry a budget, so neither spraying one
        // account from one host nor spraying many accounts from one host gets unlimited tries.
        var clientIp = ClientIpResolver.GetClientIp(HttpContext);
        var userAgent = Request.Headers.UserAgent.ToString();

        var retryAfter = _throttle.RetryAfter(clientIp, request.Username);
        if (retryAfter is not null)
        {
            var seconds = (int)Math.Ceiling(retryAfter.Value.TotalSeconds);
            Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

            _audit.Write(new SecurityEvent(
                SecurityEventType.LoginThrottled, request.Username, null, clientIp, userAgent,
                Success: false, Reason: "too-many-failures"));

            return Json(new
            {
                code = 429,
                message = $"Too many failed attempts. Try again in {seconds} seconds.",
                retryAfterSeconds = seconds
            });
        }

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
            valid = VerifyAccountPassword(account, request.Password);
            role = DashboardRoles.Parse(account.Role, DashboardRole.Admin, out roleError);
        }
        else
        {
            valid = _authMode switch
            {
                DashboardAuthMode.CustomJwt =>
                    PasswordHasher.SecureEquals(request.Username, _jwtUsername) &&
                    PasswordHasher.SecureEquals(request.Password, _jwtPassword),
                DashboardAuthMode.DefaultJwt =>
                    PasswordHasher.SecureEquals(request.Username, "admin") &&
                    PasswordHasher.SecureEquals(request.Password, _jwtPassword),
                _ => false
            };
            role = DashboardRole.Admin;
            roleError = null;
        }

        if (!valid)
        {
            _throttle.RegisterFailure(clientIp, request.Username);
            _audit.Write(new SecurityEvent(
                SecurityEventType.LoginFailed, request.Username, null, clientIp, userAgent,
                Success: false, Reason: "invalid-credentials"));

            return Json(new { code = 401, message = "Invalid credentials" });
        }

        if (roleError is not null)
            _logger.LogWarning("Account '{Username}' has an unusable role: {Error}", request.Username, roleError);

        // 2FA verification: the account's own secret wins, the dashboard-wide one is the fallback
        // for deployments that enabled 2FA before accounts existed.
        var (twoFactorEnabled, twoFactorSecret, lastCounter) = GetTwoFactorState();
        var effectiveSecret = string.IsNullOrWhiteSpace(account?.TwoFactorSecret)
            ? twoFactorSecret
            : account!.TwoFactorSecret;

        if (twoFactorEnabled && !string.IsNullOrWhiteSpace(effectiveSecret))
        {
            if (string.IsNullOrWhiteSpace(request.TwoFactorCode))
                return Json(new { code = 202, message = "Two-factor authentication required", requiresTwoFactor = true });

            // Only the dashboard-wide secret has a persisted high-water mark; an account-level
            // secret lives in configuration, which is not somewhere a counter can be written back
            // to. That gap is stated in the docs rather than papered over.
            var usesSharedSecret = string.IsNullOrWhiteSpace(account?.TwoFactorSecret);
            var totp = TotpHelper.Validate(effectiveSecret, request.TwoFactorCode,
                usesSharedSecret ? lastCounter : null);

            if (!totp.Valid)
            {
                // Six digits is a million; without counting these the second factor is a formality.
                _throttle.RegisterFailure(clientIp, request.Username);
                _audit.Write(new SecurityEvent(
                    SecurityEventType.LoginFailed, request.Username, null, clientIp, userAgent,
                    Success: false, Reason: "invalid-totp"));

                return Json(new { code = 401, message = "Invalid two-factor code" });
            }

            // Remember the step so the same six digits cannot be replayed inside the window.
            if (usesSharedSecret)
                SaveTwoFactorState(true, twoFactorSecret, totp.Counter);
        }

        var token = DashboardJwtHelper.GenerateToken(request.Username, _jwtSecret!, role);

        var secure = Request.IsHttps;
        if (!secure && Interlocked.Exchange(ref _insecureCookieWarned, 1) == 0)
        {
            _logger.LogWarning(
                "Dashboard session cookie is being issued without Secure because this request arrived over plain HTTP. " +
                "Terminate TLS in front of the gateway, or the token travels in clear text.");
        }

        Response.Cookies.Append("dashboard_token", token, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Lax,
            // Explicit because the cookie is otherwise scoped to the request path, and the dashboard
            // serves pages and API from different paths.
            Path = "/",
            // UTC: DateTime.Now on a container whose clock is in another zone shortened or extended
            // the session by the offset.
            Expires = DateTimeOffset.UtcNow.Add(DashboardJwtHelper.Lifetime)
        });

        _throttle.RegisterSuccess(clientIp, request.Username);
        _audit.Write(new SecurityEvent(
            SecurityEventType.LoginSucceeded, request.Username, role.ToClaimValue(), clientIp, userAgent));

        // The token is deliberately absent from the body: it now lives only in an HttpOnly cookie,
        // which is the entire point. Returning it here would put it back within reach of any script
        // on the page and undo the HttpOnly attribute.
        return Json(new { code = 200, expiresInSeconds = 8 * 3600 });
    }

    /// <summary>
    /// Verifies a password against the account's hash, falling back to the legacy plaintext field.
    /// </summary>
    private bool VerifyAccountPassword(DashboardUserOptions account, string password)
    {
        if (PasswordHasher.IsHash(account.PasswordHash))
            return PasswordHasher.Verify(password, account.PasswordHash!);

        if (!string.IsNullOrEmpty(account.PasswordHash))
            _logger.LogWarning(
                "Account '{Username}' has a PasswordHash that is not in the expected 'pbkdf2-sha256$...' format; falling back to Password.",
                account.Username);

        if (string.IsNullOrEmpty(account.Password))
            return false;

        if (PlaintextWarned.TryAdd(account.Username ?? string.Empty, 0))
        {
            _logger.LogWarning(
                "Account '{Username}' stores a plaintext password in configuration. Replace it with PasswordHash " +
                "(generated by PasswordHasher.Hash) — config files are backed up, copied and pasted far more often than intended.",
                account.Username);
        }

        return PasswordHasher.SecureEquals(account.Password, password);
    }

    /// <summary>Logout — revoke the token and clear the cookie.</summary>
    /// <remarks>
    /// Deleting the cookie alone was a promise the server could not keep: anyone holding a copy of
    /// the token kept a working session. The id goes on the revocation list for the rest of its
    /// natural lifetime.
    /// </remarks>
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        if (Request.Cookies.TryGetValue("dashboard_token", out var token) && !string.IsNullOrEmpty(token))
            _revocations.Revoke(DashboardJwtHelper.ReadTokenId(token), DateTimeOffset.UtcNow.Add(DashboardJwtHelper.Lifetime));

        Response.Cookies.Delete("dashboard_token");
        return Json(new { code = 200, message = "Logged out successfully" });
    }

    #endregion

    #region Two-Factor Authentication

    /// <summary>Get 2FA status.</summary>
    [HttpGet("api/2fa/status")]
    public IActionResult GetTwoFactorStatus()
    {
        var (enabled, _, _) = GetTwoFactorState();
        return Json(new { code = 200, data = new { enabled, minPasswordLength = _minPasswordLength } });
    }

    /// <summary>Generate a new 2FA secret and QR URL.</summary>
    /// <remarks>
    /// POST rather than GET because it mints a secret. A read method is exactly what a viewer is
    /// allowed to issue, and "viewer may create credentials" contradicts the role contract even
    /// while nothing is persisted.
    /// </remarks>
    [HttpPost("api/2fa/setup")]
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

        var totp = TotpHelper.Validate(secret, code, lastAcceptedCounter: null);
        if (!totp.Valid)
            return Json(new { code = 400, message = "Invalid two-factor code" });

        SaveTwoFactorState(true, secret, totp.Counter);
        _audit.Write(new SecurityEvent(
            SecurityEventType.TwoFactorEnabled,
            Actor: _authService.ResolvePrincipal(HttpContext).Username,
            Role: _authService.ResolvePrincipal(HttpContext).Role.ToClaimValue(),
            ClientIp: ClientIpResolver.GetClientIp(HttpContext),
            UserAgent: Request.Headers.UserAgent.ToString(),
            Target: "global"));

        return Json(new { code = 200, message = "Two-factor authentication enabled" });
    }

    /// <summary>Disable 2FA.</summary>
    [HttpPost("api/2fa/disable")]
    public IActionResult DisableTwoFactor()
    {
        SaveTwoFactorState(false, null);
        _audit.Write(new SecurityEvent(
            SecurityEventType.TwoFactorDisabled,
            Actor: _authService.ResolvePrincipal(HttpContext).Username,
            Role: _authService.ResolvePrincipal(HttpContext).Role.ToClaimValue(),
            ClientIp: ClientIpResolver.GetClientIp(HttpContext),
            UserAgent: Request.Headers.UserAgent.ToString(),
            Target: "global"));

        return Json(new { code = 200, message = "Two-factor authentication disabled" });
    }

    // ── 2FA State Persistence ──

    private (bool enabled, string? secret, long? lastCounter) GetTwoFactorState()
    {
        try
        {
            if (System.IO.File.Exists(_twoFactorStateFile))
            {
                var json = System.IO.File.ReadAllText(_twoFactorStateFile);
                var state = System.Text.Json.JsonSerializer.Deserialize<TwoFactorState>(json);
                if (state != null)
                {
                    // Tighten a file left world-readable by an earlier version, without waiting for
                    // the next write. Cheap enough to do on a login attempt.
                    if (!OperatingSystem.IsWindows())
                        System.IO.File.SetUnixFileMode(_twoFactorStateFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);

                    return (state.Enabled, state.Secret, state.LastAcceptedCounter);
                }
            }
        }
        catch (Exception ex)
        {
            // This used to be a bare catch, which meant an unreadable state file silently reverted
            // 2FA to whatever the configuration said — a security downgrade with no trace.
            _logger.LogWarning(ex, "Two-factor state file {Path} could not be read; falling back to configuration.", _twoFactorStateFile);
        }

        return (_enableTwoFactor, _twoFactorSecret, null);
    }

    private void SaveTwoFactorState(bool enabled, string? secret, long? lastAcceptedCounter = null)
    {
        lock (_twoFactorLock)
        {
            try
            {
                var state = new TwoFactorState { Enabled = enabled, Secret = secret, LastAcceptedCounter = lastAcceptedCounter };
                var json = System.Text.Json.JsonSerializer.Serialize(state);
                System.IO.File.WriteAllText(_twoFactorStateFile, json);

                // The TOTP secret is as sensitive as the password it protects. On Unix the default
                // mode follows the umask, which on shared hosts is often world-readable.
                if (!OperatingSystem.IsWindows())
                    System.IO.File.SetUnixFileMode(_twoFactorStateFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Two-factor state could not be written to {Path}; the change is lost on restart.", _twoFactorStateFile);
            }
        }
    }

    private class TwoFactorState
    {
        public bool Enabled { get; set; }
        public string? Secret { get; set; }

        /// <summary>Highest TOTP time step already used; anything at or below it is a replay.</summary>
        public long? LastAcceptedCounter { get; set; }
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
