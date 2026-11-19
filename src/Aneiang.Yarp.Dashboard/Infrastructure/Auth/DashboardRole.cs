using Microsoft.AspNetCore.Http;

namespace Aneiang.Yarp.Dashboard.Infrastructure.Auth;

/// <summary>
/// What an account is allowed to do, in three levels that map to three kinds of person.
/// </summary>
/// <remarks>
/// The split is about blast radius, not seniority:
/// <list type="bullet">
/// <item><b>Viewer</b> — can watch. Every write, from any page, is refused. Useful for on-call
/// rotations and for people who need to answer "is it up?" without being able to break it.</item>
/// <item><b>Operator</b> — can change the gateway: routes, clusters, plugin bindings, environment
/// switches, promotions. This is the daily driver role.</item>
/// <item><b>Admin</b> — additionally manages the things that decide who can change the gateway:
/// installed plugins and notification destinations. Losing that separation means an operator can
/// quietly redirect every alert to an address nobody reads.</item>
/// </list>
/// </remarks>
public enum DashboardRole
{
    /// <summary>Read-only. Cannot issue any non-GET request.</summary>
    Viewer = 1,

    /// <summary>Can change routing and plugins.</summary>
    Operator = 2,

    /// <summary>Can also install/remove plugins and change notification settings.</summary>
    Admin = 3
}

/// <summary>Parsing and display helpers for <see cref="DashboardRole"/>.</summary>
public static class DashboardRoles
{
    public const string ClaimType = "role";

    /// <summary>
    /// Reads a role from configuration text. Unknown values fall back to <paramref name="fallback"/>
    /// and are reported through <paramref name="error"/> rather than throwing, because a typo in a
    /// role name must not stop the dashboard from starting — but it must be visible in the log.
    /// </summary>
    public static DashboardRole Parse(string? value, DashboardRole fallback, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        switch (value.Trim().ToLowerInvariant())
        {
            case "viewer":
            case "readonly":
            case "read-only":
                return DashboardRole.Viewer;
            case "operator":
            case "ops":
                return DashboardRole.Operator;
            case "admin":
                return DashboardRole.Admin;
            default:
                error = $"Unknown role '{value}', expected viewer, operator or admin.";
                return fallback;
        }
    }

    /// <summary>Stable lowercase name, used in the JWT claim and in the UI.</summary>
    public static string ToClaimValue(this DashboardRole role) => role switch
    {
        DashboardRole.Viewer => "viewer",
        DashboardRole.Operator => "operator",
        _ => "admin"
    };

    /// <summary>
    /// True when the role may issue writes at all. The UI uses this to decide whether to offer a
    /// save button; it is not the enforcement point — <see cref="DashboardRolePolicy"/> is.
    /// </summary>
    public static bool CanWrite(this DashboardRole role) => role != DashboardRole.Viewer;

    /// <summary>True when the role may change plugin inventory and notification destinations.</summary>
    public static bool CanAdminister(this DashboardRole role) => role == DashboardRole.Admin;

    /// <summary>True for the HTTP methods a viewer is still allowed to use.</summary>
    /// <remarks>
    /// An absent method counts as write. <c>HttpContext.Request.Method</c> is effectively never empty,
    /// but a missing value must fail closed — treating "unknown verb" as read-only would hand a viewer
    /// a write.
    /// </remarks>
    public static bool IsReadOnlyMethod(string? method) =>
        !string.IsNullOrEmpty(method) &&
        (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));
}

/// <summary>One dashboard account.</summary>
public sealed class DashboardUserOptions
{
    public string? Username { get; set; }

    /// <summary>
    /// Legacy plaintext password. Still honoured so an existing deployment keeps logging in, but it
    /// is compared in constant time and a warning is logged once per account. Use
    /// <see cref="PasswordHash"/> instead — a configuration file is copied, backed up and pasted
    /// into tickets far more often than anyone intends.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// PBKDF2 hash produced by <see cref="PasswordHasher.Hash"/>. Takes precedence over
    /// <see cref="Password"/> when both are present, which is what makes the migration a matter of
    /// adding a line rather than deleting one.
    /// </summary>
    public string? PasswordHash { get; set; }

    /// <summary><c>viewer</c>, <c>operator</c> or <c>admin</c>. Defaults to admin so an existing single-user setup keeps working.</summary>
    public string? Role { get; set; }

    /// <summary>
    /// Per-account TOTP secret. Falls back to the dashboard-wide secret when empty, which keeps 2FA
    /// working for deployments that already have one secret configured.
    /// </summary>
    public string? TwoFactorSecret { get; set; }
}

/// <summary>The identity behind a request, after authorization succeeded.</summary>
/// <param name="Username">Account name, or a synthetic name for the machine credentials.</param>
/// <param name="Role">Resolved role.</param>
/// <param name="Implicit">
/// True when the role was not chosen by anyone — auth is off, or an API key is in use. The UI uses
/// this to avoid showing a role badge that would imply an access control that does not exist.
/// </param>
public readonly record struct DashboardPrincipal(string Username, DashboardRole Role, bool Implicit);
