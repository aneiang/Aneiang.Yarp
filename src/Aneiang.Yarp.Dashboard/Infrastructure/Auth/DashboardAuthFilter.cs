using Aneiang.Yarp.Dashboard.Infrastructure.Security;
using Aneiang.Yarp.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Aneiang.Yarp.Dashboard.Infrastructure.Auth;

/// <summary>
/// Async auth filter for Dashboard controllers.
/// Applies to all controllers under the Aneiang.Yarp.Dashboard namespace.
/// Skips login/logout actions. Redirects browser to login; returns 401 JSON for API calls.
/// Uses IDashboardAuthorizationService for unified authorization check, then enforces the role:
/// a viewer cannot issue a write, and an operator cannot touch the endpoints that decide who else
/// can write.
/// </summary>
internal sealed class DashboardAuthFilter : IAsyncAuthorizationFilter
{
    /// <summary>HttpContext.Items key holding the resolved <see cref="DashboardPrincipal"/>.</summary>
    public const string PrincipalItemKey = "dashboard.principal";

    private readonly IDashboardAuthorizationService _authService;
    private readonly string _routePrefix;

    public DashboardAuthFilter(IDashboardAuthorizationService authService, string routePrefix)
    {
        _authService = authService;
        _routePrefix = routePrefix;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor { ControllerTypeInfo: var ci })
            return;

        if (!ci.Namespace?.StartsWith("Aneiang.Yarp.Dashboard.", StringComparison.Ordinal) == true)
            return;

        // Skip login/logout actions - they are public
        var actionName = ((ControllerActionDescriptor)context.ActionDescriptor).ActionName;
        if (string.Equals(actionName, "Login", StringComparison.OrdinalIgnoreCase)
            || string.Equals(actionName, "Logout", StringComparison.OrdinalIgnoreCase))
            return;

        // Use authorization service for unified check
        if (!await _authService.IsAuthorizedAsync(context.HttpContext))
        {
            Reject(context, 401, "Unauthorized", redirectToLogin: true);
            return;
        }

        var principal = _authService.ResolvePrincipal(context.HttpContext);
        context.HttpContext.Items[PrincipalItemKey] = principal;

        // CSRF: the session now lives in a cookie, and the browser attaches cookies to any request
        // another origin can make. A header the browser will not add cross-origin without a CORS
        // preflight is the cheap half of the double-submit pattern and needs no token plumbing.
        // Requests that authenticate with an Authorization header are exempt: a browser never
        // attaches those on its own, so the attack does not exist there.
        if (RequiresCsrfHeader(context.HttpContext) && !HasCsrfHeader(context.HttpContext.Request))
        {
            WriteAudit(context.HttpContext, principal, SecurityEventType.AccessDenied, "csrf-header-missing");
            Reject(context, 403,
                "This request is missing the X-Requested-With header that the dashboard sends; cross-site form posts are refused.",
                reason: "csrf-required");
            return;
        }

        var denial = DashboardRolePolicy.Deny(
            principal, context.HttpContext.Request.Method, context.HttpContext.Request.Path);

        if (denial is null) return;

        WriteAudit(context.HttpContext, principal, SecurityEventType.AccessDenied, denial);

        var message = denial == DashboardRolePolicy.ReadOnlyDenied
            ? $"This account is read-only ({principal.Role.ToClaimValue()}); it cannot change anything."
            : $"This operation requires the admin role; '{principal.Username}' is {principal.Role.ToClaimValue()}.";

        Reject(context, 403, message, reason: denial);
    }

    private static bool RequiresCsrfHeader(HttpContext context)
    {
        if (DashboardRoles.IsReadOnlyMethod(context.Request.Method)) return false;

        // Cookie-authenticated: no Authorization header, so the browser supplied the credential.
        return !context.Request.Headers.ContainsKey("Authorization");
    }

    private static bool HasCsrfHeader(HttpRequest request) =>
        request.Headers.ContainsKey("X-Requested-With") ||
        request.Headers.ContainsKey("X-CSRF");

    private static void WriteAudit(HttpContext context, DashboardPrincipal principal, SecurityEventType type, string reason)
    {
        var writer = context.RequestServices.GetService(typeof(ISecurityAuditWriter)) as ISecurityAuditWriter;
        if (writer is null) return;

        writer.Write(new SecurityEvent(
            type,
            Actor: principal.Username,
            Role: principal.Role.ToClaimValue(),
            ClientIp: ClientIpResolver.GetClientIp(context),
            UserAgent: context.Request.Headers.UserAgent.ToString(),
            Success: false,
            Reason: reason,
            Target: context.Request.Path));
    }

    /// <summary>
    /// Browsers navigating to a page get a redirect; XHR and API calls get JSON. Both branches carry
    /// the same reason code so the UI can say why rather than showing a bare 403.
    /// </summary>
    private void Reject(AuthorizationFilterContext context, int statusCode, string message, bool redirectToLogin = false, string? reason = null)
    {
        var request = context.HttpContext.Request;
        var isApi = (request.Headers["Accept"].FirstOrDefault()?.Contains("application/json") == true)
            || request.Path.StartsWithSegments($"/{_routePrefix}/api");

        if (isApi || !redirectToLogin)
        {
            context.Result = new JsonResult(new { code = statusCode, message, reason }) { StatusCode = statusCode };
            return;
        }

        context.Result = new RedirectResult($"/{_routePrefix}/login");
    }
}
