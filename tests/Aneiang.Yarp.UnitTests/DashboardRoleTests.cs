using Aneiang.Yarp.Dashboard.Infrastructure;
using Aneiang.Yarp.Dashboard.Infrastructure.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Covers the two halves of RBAC that can fail silently: which requests a role may issue, and which
/// role a given credential actually resolves to. Both are pure decisions, so they are tested without
/// an MVC pipeline — a filter whose rules are only exercised through HTTP is a filter whose rules are
/// mostly untested.
/// </summary>
public class DashboardRoleTests
{
    private const string Secret = "test-signing-secret-not-used-anywhere-else";

    // ──────────────── role parsing ────────────────

    [Theory]
    [InlineData("viewer", DashboardRole.Viewer)]
    [InlineData("VIEWER", DashboardRole.Viewer)]
    [InlineData("read-only", DashboardRole.Viewer)]
    [InlineData("readonly", DashboardRole.Viewer)]
    [InlineData("operator", DashboardRole.Operator)]
    [InlineData("ops", DashboardRole.Operator)]
    [InlineData("admin", DashboardRole.Admin)]
    public void Known_role_names_are_read_case_insensitively(string text, DashboardRole expected)
    {
        var role = DashboardRoles.Parse(text, DashboardRole.Admin, out var error);

        Assert.Equal(expected, role);
        Assert.Null(error);
    }

    [Fact]
    public void An_unknown_role_falls_back_and_says_so()
    {
        // A typo must not stop the dashboard from starting, but it must not pass unnoticed either.
        var role = DashboardRoles.Parse("superuser", DashboardRole.Operator, out var error);

        Assert.Equal(DashboardRole.Operator, role);
        Assert.NotNull(error);
        Assert.Contains("superuser", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_role_takes_the_fallback_without_an_error()
    {
        var role = DashboardRoles.Parse("   ", DashboardRole.Admin, out var error);

        Assert.Equal(DashboardRole.Admin, role);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(DashboardRole.Viewer, "viewer", false, false)]
    [InlineData(DashboardRole.Operator, "operator", true, false)]
    [InlineData(DashboardRole.Admin, "admin", true, true)]
    public void Capabilities_follow_the_role(DashboardRole role, string claim, bool canWrite, bool canAdminister)
    {
        Assert.Equal(claim, role.ToClaimValue());
        Assert.Equal(canWrite, role.CanWrite());
        Assert.Equal(canAdminister, role.CanAdminister());
    }

    // ──────────────── method gate ────────────────

    [Theory]
    [InlineData("GET", true)]
    [InlineData("HEAD", true)]
    [InlineData("OPTIONS", true)]
    [InlineData("POST", false)]
    [InlineData("PUT", false)]
    [InlineData("PATCH", false)]
    [InlineData("DELETE", false)]
    public void Only_safe_methods_count_as_reads(string method, bool readOnly)
        => Assert.Equal(readOnly, DashboardRoles.IsReadOnlyMethod(method));

    [Fact]
    public void A_missing_method_is_treated_as_a_write()
    {
        // Fail closed: an unknown verb must not be mistaken for a read, because that would hand a
        // viewer the one thing the role exists to withhold.
        Assert.False(DashboardRoles.IsReadOnlyMethod(null));
        Assert.False(DashboardRoles.IsReadOnlyMethod(string.Empty));
    }

    // ──────────────── policy ────────────────

    [Theory]
    [InlineData("GET", "/apigateway/api/config/routes")]
    [InlineData("HEAD", "/apigateway/api/clusters")]
    public void A_viewer_may_read(string method, string path)
        => Assert.Null(Policy(DashboardRole.Viewer, method, path));

    [Theory]
    [InlineData("POST", "/apigateway/api/config/routes")]
    [InlineData("PUT", "/apigateway/api/config/clusters/c1")]
    [InlineData("DELETE", "/apigateway/api/config/routes/r1")]
    [InlineData("POST", "/apigateway/api/environments/promote/apply")]
    public void A_viewer_may_not_write_anything(string method, string path)
        => Assert.Equal(DashboardRolePolicy.ReadOnlyDenied, Policy(DashboardRole.Viewer, method, path));

    [Theory]
    [InlineData("POST", "/apigateway/api/config/routes")]
    [InlineData("PUT", "/apigateway/api/config/clusters/c1")]
    [InlineData("DELETE", "/apigateway/api/config/routes/r1")]
    [InlineData("POST", "/apigateway/api/plugin-bindings")]
    [InlineData("POST", "/apigateway/api/environments/promote/apply")]
    public void An_operator_may_change_the_gateway(string method, string path)
        => Assert.Null(Policy(DashboardRole.Operator, method, path));

    [Theory]
    [InlineData("POST", "/apigateway/api/plugins/install")]
    [InlineData("POST", "/apigateway/api/plugins/waf/upgrade")]
    [InlineData("DELETE", "/apigateway/api/plugins/waf")]
    [InlineData("POST", "/apigateway/api/webhook/settings")]
    [InlineData("POST", "/apigateway/api/webhook/platform")]
    public void An_operator_may_not_change_who_can_write(string method, string path)
        => Assert.Equal(DashboardRolePolicy.AdminRequired, Policy(DashboardRole.Operator, method, path));

    [Fact]
    public void An_operator_may_still_read_the_admin_only_areas()
    {
        // Reading the notification setup is how an operator reports that alerts are misrouted.
        Assert.Null(Policy(DashboardRole.Operator, "GET", "/apigateway/api/webhook/settings"));
        Assert.Null(Policy(DashboardRole.Operator, "GET", "/apigateway/api/plugins/installed"));
    }

    [Theory]
    [InlineData("POST", "/apigateway/api/plugins/install")]
    [InlineData("DELETE", "/apigateway/api/plugins/waf")]
    [InlineData("POST", "/apigateway/api/webhook/settings")]
    public void An_admin_may_do_everything(string method, string path)
        => Assert.Null(Policy(DashboardRole.Admin, method, path));

    [Fact]
    public void Plugin_management_is_not_confused_with_a_plugin_binding()
    {
        // "api/plugins/{id}/..." is control-plane, "api/plugin-bindings/..." is per-route config.
        // A prefix check would have conflated them and locked operators out of their daily work.
        Assert.Null(Policy(DashboardRole.Operator, "POST", "/apigateway/api/plugin-bindings"));
        Assert.Equal(DashboardRolePolicy.AdminRequired,
            Policy(DashboardRole.Operator, "POST", "/apigateway/api/plugins/install"));
    }

    [Fact]
    public void A_path_without_an_api_segment_is_never_admin_only()
        => Assert.False(DashboardRolePolicy.IsAdminOnly("POST", "/apigateway/plugins"));

    // ──────────────── principal resolution ────────────────

    [Fact]
    public void Auth_switched_off_resolves_to_an_implicit_admin()
    {
        var principal = Resolve(new DashboardOptions());

        Assert.Equal(DashboardRole.Admin, principal.Role);
        Assert.True(principal.Implicit);
    }

    [Fact]
    public void An_api_key_resolves_to_an_implicit_admin()
    {
        // A machine credential has no account behind it. Inventing a viewer role for it would break
        // every deployment that drives the API from a script.
        var options = new DashboardOptions();
        options.Auth.AuthMode = DashboardAuthMode.ApiKey;
        options.Auth.ApiKey = "k";

        var principal = Resolve(options);

        Assert.Equal(DashboardRole.Admin, principal.Role);
        Assert.True(principal.Implicit);
    }

    [Fact]
    public void A_custom_delegate_resolves_to_an_implicit_admin()
    {
        var options = new DashboardOptions();
        options.Auth.AuthorizeRequest = _ => Task.FromResult(true);

        var principal = Resolve(options);

        Assert.Equal(DashboardRole.Admin, principal.Role);
        Assert.True(principal.Implicit);
    }

    [Theory]
    [InlineData(DashboardRole.Viewer)]
    [InlineData(DashboardRole.Operator)]
    [InlineData(DashboardRole.Admin)]
    public void The_role_travels_in_the_token(DashboardRole role)
    {
        var principal = Resolve(JwtOptions(), Token("alice", role));

        Assert.Equal("alice", principal.Username);
        Assert.Equal(role, principal.Role);
        Assert.False(principal.Implicit);
    }

    [Fact]
    public void A_token_from_before_roles_existed_takes_the_accounts_role()
    {
        // Tokens live eight hours, so one release has to read a token that carries no role claim.
        // It must fall back to what the account is configured as — not silently to admin.
        var options = JwtOptions();
        options.Auth.Users.Add(new DashboardUserOptions { Username = "bob", Password = "p", Role = "viewer" });

        var principal = Resolve(options, DashboardJwtHelper.GenerateToken("bob", Secret));

        Assert.Equal(DashboardRole.Viewer, principal.Role);
    }

    [Fact]
    public void A_token_from_before_roles_existed_for_an_unknown_account_keeps_working()
    {
        // The single-user deployments that predate accounts have no Users entry at all. Denying them
        // would lock the operator out of their own dashboard on upgrade.
        var principal = Resolve(JwtOptions(), DashboardJwtHelper.GenerateToken("legacy", Secret));

        Assert.Equal(DashboardRole.Admin, principal.Role);
    }

    [Fact]
    public void A_jwt_mode_request_without_a_usable_token_resolves_to_a_viewer()
    {
        // The filter only calls this after IsAuthorizedAsync passed, so this should be unreachable;
        // when it is reached, the safe answer is the one that cannot write.
        var principal = Resolve(JwtOptions(), token: null);

        Assert.Equal(DashboardRole.Viewer, principal.Role);
        Assert.False(principal.Implicit);
    }

    [Fact]
    public void A_forged_token_does_not_become_an_admin()
    {
        var options = JwtOptions();
        var forged = DashboardJwtHelper.GenerateToken("mallory", "a-different-secret", DashboardRole.Admin);

        var principal = Resolve(options, forged);

        Assert.Equal(DashboardRole.Viewer, principal.Role);
    }

    // ──────────────── helpers ────────────────

    private static string? Policy(DashboardRole role, string method, string path)
        => DashboardRolePolicy.Deny(new DashboardPrincipal("someone", role, Implicit: false), method, new PathString(path));

    private static DashboardOptions JwtOptions()
    {
        var options = new DashboardOptions();
        options.Auth.AuthMode = DashboardAuthMode.CustomJwt;
        options.Auth.JwtSecret = Secret;
        return options;
    }

    private static string Token(string username, DashboardRole role)
        => DashboardJwtHelper.GenerateToken(username, Secret, role);

    private static DashboardPrincipal Resolve(DashboardOptions options, string? token = null)
    {
        var service = new DashboardAuthorizationService(
            Options.Create(options), NullLogger<DashboardAuthorizationService>.Instance);

        var context = new DefaultHttpContext();
        if (token is not null)
            context.Request.Headers.Authorization = $"Bearer {token}";

        return service.ResolvePrincipal(context);
    }
}
