using Microsoft.AspNetCore.Http;

namespace Aneiang.Yarp.Dashboard.Infrastructure.Auth;

/// <summary>
/// Decides whether a principal may issue a request. Kept separate from the filter so the rules can
/// be tested without building an MVC pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Two rules, deliberately lopsided:
/// </para>
/// <list type="number">
/// <item><b>Viewer is denied by method.</b> Any non-GET is refused. That cannot be bypassed by a
/// new endpoint, which matters because the alternative — an allow-list of read endpoints — breaks
/// the moment someone adds a page.</item>
/// <item><b>Operator is denied by path.</b> Only the small set of endpoints that decide <em>who can
/// change the gateway</em> (installing plugins, notification destinations) is admin-only. This list
/// is an allow-by-default: a newly added operational endpoint is available to operators without
/// anyone remembering to update a table. The trade is explicit — an operator is trusted to change
/// routing, so denying them new operational endpoints would break their work more often than it
/// would prevent an incident.</item>
/// </list>
/// <para>
/// When authentication is not configured at all, the principal is an implicit admin: there is no
/// account to attribute anything to, and pretending otherwise would lock every existing deployment
/// out of its own dashboard.
/// </para>
/// </remarks>
public static class DashboardRolePolicy
{
    /// <summary>Reason codes returned by <see cref="Deny"/>; the UI maps them to localized text.</summary>
    public const string ReadOnlyDenied = "read-only";
    public const string AdminRequired = "admin-required";

    /// <summary>
    /// Returns null when the request is allowed, otherwise the reason code.
    /// </summary>
    public static string? Deny(DashboardPrincipal principal, string? method, PathString path)
    {
        if (!principal.Role.CanWrite() && !DashboardRoles.IsReadOnlyMethod(method))
            return ReadOnlyDenied;

        // A viewer has already been turned away above unless the request is a read, so reaching
        // here with !CanAdminister() means operator.
        if (!principal.Role.CanAdminister() && IsAdminOnly(method, path))
            return AdminRequired;

        return null;
    }

    /// <summary>
    /// Endpoints that change the control plane's own configuration rather than the gateway's
    /// routing. An operator changing alerts could silence the very alert that would report them.
    /// </summary>
    public static bool IsAdminOnly(string? method, PathString path)
    {
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var apiIndex = Array.IndexOf(segments, "api");
        if (apiIndex < 0 || apiIndex + 1 >= segments.Length) return false;

        var area = segments[apiIndex + 1];

        if (string.Equals(area, "webhook", StringComparison.OrdinalIgnoreCase) &&
            !DashboardRoles.IsReadOnlyMethod(method))
            return true;

        if (!string.Equals(area, "plugins", StringComparison.OrdinalIgnoreCase)) return false;

        // POST api/plugins/install, POST api/plugins/{id}/upgrade, DELETE api/plugins/{id}
        if (segments.Length > apiIndex + 2)
        {
            var action = segments[apiIndex + 2];
            if (string.Equals(action, "install", StringComparison.OrdinalIgnoreCase)) return true;
            if (segments.Length > apiIndex + 3 &&
                string.Equals(segments[apiIndex + 3], "upgrade", StringComparison.OrdinalIgnoreCase)) return true;
            if (method is not null && HttpMethods.IsDelete(method) && segments.Length == apiIndex + 3) return true;
        }

        return false;
    }
}
