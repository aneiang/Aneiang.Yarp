using Aneiang.Yarp.Dashboard.Infrastructure.Policies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// The catalog is what turns four free-text boxes into dropdowns, and it is also what the
/// validator asks "does this name exist?". If it answers too confidently, a typo saves;
/// if it answers too strictly, every host-registered policy is rejected. Both directions
/// are pinned here.
/// </summary>
public class GatewayPolicyCatalogTests
{
    private static GatewayPolicyCatalog Catalog(IServiceProvider services)
        => new(services);

    private static ServiceProvider Empty => new ServiceCollection().BuildServiceProvider();

    // ---------- built-in set ----------

    [Fact]
    public void BuiltIns_CoverAllFourKinds()
    {
        var kinds = GatewayPolicyDefaults.All.Select(p => p.Kind).Distinct().ToList();

        Assert.Equal(4, kinds.Count);
        Assert.Contains(GatewayPolicyKind.Authorization, kinds);
        Assert.Contains(GatewayPolicyKind.Cors, kinds);
        Assert.Contains(GatewayPolicyKind.RateLimiter, kinds);
        Assert.Contains(GatewayPolicyKind.OutputCache, kinds);
    }

    [Fact]
    public void BuiltIns_HaveUniqueNames()
    {
        var names = GatewayPolicyDefaults.All.Select(p => p.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void BuiltIns_AllUseTheGwPrefix()
    {
        // A prefix keeps the dashboard's own policies from colliding with host ones.
        Assert.All(GatewayPolicyDefaults.All, p => Assert.StartsWith("gw-", p.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void BuiltIns_AreAllMarkedBuiltIn()
    {
        Assert.All(GatewayPolicyDefaults.All, p => Assert.True(p.BuiltIn));
    }

    [Fact]
    public void BuiltIns_AllHaveADescription()
    {
        // The description is the only thing explaining what the dropdown entry does.
        Assert.All(GatewayPolicyDefaults.All, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
    }

    [Fact]
    public void GetPolicies_FiltersByKind()
    {
        var catalog = Catalog(Empty);

        var rateLimiters = catalog.GetPolicies(GatewayPolicyKind.RateLimiter);

        Assert.Equal(3, rateLimiters.Count);
        Assert.All(rateLimiters, p => Assert.Equal(GatewayPolicyKind.RateLimiter, p.Kind));
    }

    [Fact]
    public void GetAll_ReturnsEverything()
    {
        Assert.Equal(GatewayPolicyDefaults.All.Count, Catalog(Empty).GetAll().Count);
    }

    [Theory]
    [InlineData("gw-anonymous")]
    [InlineData("gw-authenticated")]
    [InlineData("gw-cors-open")]
    [InlineData("gw-rl-100pm")]
    [InlineData("gw-rl-1000pm")]
    [InlineData("gw-rl-10ps")]
    [InlineData("gw-cache-30s")]
    [InlineData("gw-cache-5m")]
    [InlineData("gw-cache-1h")]
    public async Task BuiltInNames_ResolveAsFound(string name)
    {
        var info = GatewayPolicyDefaults.All.Single(p => p.Name == name);
        var resolution = await Catalog(Empty).ResolveAsync(info.Kind, name);

        Assert.Equal(PolicyResolutionStatus.Found, resolution.Status);
    }

    [Fact]
    public async Task BuiltInName_IsCaseSensitive()
    {
        // ASP.NET Core policy lookup is ordinal; pretending otherwise would save a name
        // that then fails at request time.
        var resolution = await Catalog(Empty).ResolveAsync(
            GatewayPolicyKind.RateLimiter, "GW-RL-100PM");

        Assert.NotEqual(PolicyResolutionStatus.Found, resolution.Status);
    }

    [Fact]
    public async Task EmptyName_ResolvesAsFoundWithNoName()
    {
        // "no policy" is a valid choice, not a missing one.
        var resolution = await Catalog(Empty).ResolveAsync(GatewayPolicyKind.Authorization, "   ");

        Assert.Equal(PolicyResolutionStatus.Found, resolution.Status);
        Assert.Null(resolution.Name);
    }

    [Fact]
    public async Task Names_AreTrimmed()
    {
        var resolution = await Catalog(Empty).ResolveAsync(GatewayPolicyKind.Cors, "  gw-cors-open  ");

        Assert.Equal(PolicyResolutionStatus.Found, resolution.Status);
        Assert.Equal("gw-cors-open", resolution.Name);
    }

    // ---------- resolvable kinds ----------

    [Fact]
    public async Task Authorization_UnknownName_IsUnknown()
    {
        // This provider is authoritative: a null answer means ASP.NET Core will throw.
        var services = new ServiceCollection()
            .AddSingleton<IAuthorizationPolicyProvider>(new StubAuthorizationPolicyProvider())
            .BuildServiceProvider();

        var resolution = await Catalog(services).ResolveAsync(
            GatewayPolicyKind.Authorization, "not-registered");

        Assert.Equal(PolicyResolutionStatus.Unknown, resolution.Status);
    }

    [Fact]
    public async Task Authorization_RegisteredName_IsFound()
    {
        var services = new ServiceCollection()
            .AddSingleton<IAuthorizationPolicyProvider>(
                new StubAuthorizationPolicyProvider("host-policy"))
            .BuildServiceProvider();

        var resolution = await Catalog(services).ResolveAsync(
            GatewayPolicyKind.Authorization, "host-policy");

        Assert.Equal(PolicyResolutionStatus.Found, resolution.Status);
    }

    [Fact]
    public async Task Cors_UnknownName_IsUnknown()
    {
        var services = new ServiceCollection()
            .AddSingleton<ICorsPolicyProvider>(new StubCorsPolicyProvider())
            .BuildServiceProvider();

        var resolution = await Catalog(services).ResolveAsync(GatewayPolicyKind.Cors, "nope");

        Assert.Equal(PolicyResolutionStatus.Unknown, resolution.Status);
    }

    [Fact]
    public async Task Cors_RegisteredName_IsFound()
    {
        var services = new ServiceCollection()
            .AddSingleton<ICorsPolicyProvider>(new StubCorsPolicyProvider("host-cors"))
            .BuildServiceProvider();

        var resolution = await Catalog(services).ResolveAsync(GatewayPolicyKind.Cors, "host-cors");

        Assert.Equal(PolicyResolutionStatus.Found, resolution.Status);
    }

    // ---------- unverifiable kinds ----------

    [Theory]
    [InlineData(GatewayPolicyKind.RateLimiter)]
    [InlineData(GatewayPolicyKind.OutputCache)]
    public async Task UnenumerableKinds_AreUnverifiableNotUnknown(GatewayPolicyKind kind)
    {
        // PolicyMap / Policies are internal, so claiming "unknown" here would reject every
        // custom policy a host registered — worse than the silent failure we started from.
        var resolution = await Catalog(Empty).ResolveAsync(kind, "whatever");

        Assert.Equal(PolicyResolutionStatus.Unverifiable, resolution.Status);
    }

    [Fact]
    public async Task MissingProviders_DowngradeToUnverifiable()
    {
        // No ASP.NET Core auth/CORS stack registered at all: absence proves nothing.
        Assert.Equal(PolicyResolutionStatus.Unverifiable,
            (await Catalog(Empty).ResolveAsync(GatewayPolicyKind.Authorization, "x")).Status);
        Assert.Equal(PolicyResolutionStatus.Unverifiable,
            (await Catalog(Empty).ResolveAsync(GatewayPolicyKind.Cors, "x")).Status);
    }

    // ---------- registration ----------

    [Fact]
    public void AddGatewayPolicies_RegistersCatalogAndAllBuiltIns()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGatewayPolicies();

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IGatewayPolicyCatalog>();

        Assert.Equal(GatewayPolicyDefaults.All.Count, catalog.GetAll().Count);
    }

    [Fact]
    public async Task AddGatewayPolicies_RegistersPoliciesThatActuallyResolve()
    {
        // Registration and resolution must agree: a built-in that the catalog lists but
        // the provider rejects would be a dropdown entry that always fails.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGatewayPolicies();

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IGatewayPolicyCatalog>();

        foreach (var info in GatewayPolicyDefaults.All.Where(p => p.Kind == GatewayPolicyKind.Authorization))
        {
            var resolution = await catalog.ResolveAsync(info.Kind, info.Name);
            Assert.Equal(PolicyResolutionStatus.Found, resolution.Status);
        }

        foreach (var info in GatewayPolicyDefaults.All.Where(p => p.Kind == GatewayPolicyKind.Cors))
        {
            var resolution = await catalog.ResolveAsync(info.Kind, info.Name);
            Assert.Equal(PolicyResolutionStatus.Found, resolution.Status);
        }
    }

    [Fact]
    public void AddGatewayPolicies_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGatewayPolicies();
        services.AddGatewayPolicies();

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IGatewayPolicyCatalog>());
    }

    [Fact]
    public void AddGatewayPolicies_WorksAlongsideHostCors()
    {
        // The extension must be additive; a host that already called AddCors keeps working.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCors(o => o.AddPolicy("host-cors", b => b.AllowAnyOrigin()));
        services.AddGatewayPolicies();

        using var provider = services.BuildServiceProvider();
        var corsOptions = provider.GetRequiredService<IOptions<CorsOptions>>().Value;

        Assert.NotNull(corsOptions.GetPolicy("host-cors"));
        Assert.NotNull(corsOptions.GetPolicy(GatewayPolicyDefaults.CorsOpen));
    }

    private sealed class StubAuthorizationPolicyProvider : IAuthorizationPolicyProvider
    {
        // AuthorizationPolicy rejects an empty requirement list, so build a trivial one.
        private static readonly AuthorizationPolicy Placeholder =
            new AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build();

        private readonly HashSet<string> _known;

        public StubAuthorizationPolicyProvider(params string[] known)
            => _known = new HashSet<string>(known, StringComparer.Ordinal);

        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
            => Task.FromResult(_known.Contains(policyName) ? Placeholder : null);

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => Task.FromResult(Placeholder);

        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync()
            => Task.FromResult<AuthorizationPolicy?>(null);
    }

    /// <summary>Named-name lookup only; enough to exercise the catalog's null/not-null mapping.</summary>
    private sealed class StubCorsPolicyProvider : ICorsPolicyProvider
    {
        private static readonly CorsPolicy Placeholder = new();

        private readonly HashSet<string> _known;

        public StubCorsPolicyProvider(params string[] known)
            => _known = new HashSet<string>(known, StringComparer.Ordinal);

        public Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
            => Task.FromResult(_known.Contains(policyName ?? string.Empty) ? Placeholder : null);
    }
}
