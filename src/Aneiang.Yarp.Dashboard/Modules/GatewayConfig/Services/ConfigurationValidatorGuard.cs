using Aneiang.Yarp.Validation;
using Yarp.ReverseProxy.Configuration;

namespace Aneiang.Yarp.Dashboard.Modules.GatewayConfig.Services;

/// <summary>
/// Bridges the dashboard's <see cref="IConfigurationValidator"/> into the core library's
/// <see cref="IConfigurationGuard"/> slot.
///
/// Why the indirection: the writes that most need a pre-flight check (client self-registration,
/// batch import, history rollback) all enter through the core library, which must not reference
/// the dashboard. Core therefore declares the guard with a permissive default, and this adapter
/// is what gets substituted when the dashboard is present — one implementation, every entry point.
/// </summary>
public sealed class ConfigurationValidatorGuard : IConfigurationGuard
{
    private readonly IConfigurationValidator _validator;

    public ConfigurationValidatorGuard(IConfigurationValidator validator)
    {
        _validator = validator;
    }

    /// <inheritdoc />
    public async Task<ConfigurationGuardResult> CheckRouteAsync(
        RouteConfig route, bool clusterWillBeCreated, CancellationToken ct = default)
        => Convert(await _validator.ValidateRouteAsync(route, clusterWillBeCreated, ct));

    /// <inheritdoc />
    public async Task<ConfigurationGuardResult> CheckClusterAsync(
        ClusterConfig cluster, CancellationToken ct = default)
        => Convert(await _validator.ValidateClusterAsync(cluster, ct));

    /// <inheritdoc />
    public async Task<ConfigurationGuardResult> CheckBatchAsync(
        IReadOnlyList<RouteConfig> routes,
        IReadOnlyList<ClusterConfig> clusters,
        CancellationToken ct = default)
        => Convert(await _validator.ValidateBatchAsync(routes, clusters, ct));

    /// <inheritdoc />
    public async Task<ConfigurationGuardResult> CheckCurrentAsync(CancellationToken ct = default)
        => Convert(await _validator.ValidateAllAsync(ct));

    private static ConfigurationGuardResult Convert(ConfigValidationReport report)
        => ConfigurationGuardResult.From(report.Errors.Concat(report.Warnings).Select(Convert));

    private static ConfigurationGuardIssue Convert(ConfigValidationIssue issue)
        => new(issue.Severity, issue.Field, issue.Message, issue.Hint);
}
