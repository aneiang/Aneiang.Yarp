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

        var denial = DashboardRolePolicy.Deny(
            principal, context.HttpContext.Request.Method, context.HttpContext.Request.Path);

        if (denial is null) return;

        var message = denial == DashboardRolePolicy.ReadOnlyDenied
            ? $"This account is read-only ({principal.Role.ToClaimValue()}); it cannot change anything."
            : $"This operation requires the admin role; '{principal.Username}' is {principal.Role.ToClaimValue()}.";

        Reject(context, 403, message, reason: denial);
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
