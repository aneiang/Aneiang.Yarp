using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Configuration;

namespace Aneiang.Yarp.Validation;

/// <summary>One finding produced by a configuration guard.</summary>
/// <param name="Severity">Either <c>Error</c> or <c>Warning</c>.</param>
/// <param name="Field">Dotted path of the offending field, e.g. <c>Destinations.d1.Address</c>.</param>
/// <param name="Message">What is wrong, in the operator's language.</param>
/// <param name="Hint">Optional suggestion on how to fix it.</param>
public sealed record ConfigurationGuardIssue(
    string Severity,
    string Field,
    string Message,
    string? Hint = null)
{
    /// <summary>Severity name used for blocking findings.</summary>
    public const string ErrorSeverity = "Error";

    /// <summary>Severity name used for non-blocking findings.</summary>
    public const string WarningSeverity = "Warning";
}

/// <summary>Outcome of a pre-flight configuration check.</summary>
/// <param name="Ok">False when at least one <see cref="ConfigurationGuardIssue.ErrorSeverity"/> issue exists.</param>
/// <param name="Errors">Blocking issues; a non-empty list means the write must be refused.</param>
/// <param name="Warnings">Issues worth surfacing but that must not block the write.</param>
public sealed record ConfigurationGuardResult(
    bool Ok,
    IReadOnlyList<ConfigurationGuardIssue> Errors,
    IReadOnlyList<ConfigurationGuardIssue> Warnings)
{
    /// <summary>A pass with nothing to report.</summary>
    public static readonly ConfigurationGuardResult Allowed =
        new(true, Array.Empty<ConfigurationGuardIssue>(), Array.Empty<ConfigurationGuardIssue>());

    /// <summary>Splits a flat issue list into the blocking and non-blocking buckets.</summary>
    public static ConfigurationGuardResult From(IEnumerable<ConfigurationGuardIssue> issues)
    {
        var all = issues as IList<ConfigurationGuardIssue> ?? issues.ToList();
        var errors = all
            .Where(i => string.Equals(i.Severity, ConfigurationGuardIssue.ErrorSeverity, StringComparison.Ordinal))
            .ToList();
        var warnings = all
            .Where(i => !string.Equals(i.Severity, ConfigurationGuardIssue.ErrorSeverity, StringComparison.Ordinal))
            .ToList();
        return new ConfigurationGuardResult(errors.Count == 0, errors, warnings);
    }
}

/// <summary>
/// Pre-flight check applied before a configuration write is committed.
///
/// This abstraction lives in the core library rather than the dashboard because the
/// writes that matter most are the machine ones — client self-registration, batch
/// import, history rollback — and every one of those enters through the core. The
/// dashboard supplies the real implementation; a host without the dashboard gets
/// <see cref="PermissiveConfigurationGuard"/>, so adding the guard never changes the
/// behaviour of an existing deployment that has no UI.
/// </summary>
public interface IConfigurationGuard
{
    /// <summary>Checks a single route against the configuration currently in effect.</summary>
    /// <param name="route">Route about to be written.</param>
    /// <param name="clusterWillBeCreated">
    /// True when the same write also creates the referenced cluster, so "unknown cluster"
    /// must not be reported.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<ConfigurationGuardResult> CheckRouteAsync(
        RouteConfig route, bool clusterWillBeCreated, CancellationToken ct = default);

    /// <summary>Checks a single cluster in isolation.</summary>
    /// <param name="cluster">Cluster about to be written.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ConfigurationGuardResult> CheckClusterAsync(ClusterConfig cluster, CancellationToken ct = default);

    /// <summary>
    /// Checks a whole proposed configuration as one unit — the shape used by import and
    /// rollback, where a partially applied batch is worse than a refused one.
    /// </summary>
    /// <param name="routes">Proposed routes.</param>
    /// <param name="clusters">Proposed clusters (the universe routes are resolved against).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ConfigurationGuardResult> CheckBatchAsync(
        IReadOnlyList<RouteConfig> routes,
        IReadOnlyList<ClusterConfig> clusters,
        CancellationToken ct = default);

    /// <summary>Cross-checks the configuration that is currently in effect.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<ConfigurationGuardResult> CheckCurrentAsync(CancellationToken ct = default);
}

/// <summary>
/// Guard that allows everything. Registered by default so the core library keeps its
/// historical "accept what you are given" contract; the dashboard replaces it with a
/// real validator.
/// </summary>
public sealed class PermissiveConfigurationGuard : IConfigurationGuard
{
    /// <inheritdoc />
    public Task<ConfigurationGuardResult> CheckRouteAsync(
        RouteConfig route, bool clusterWillBeCreated, CancellationToken ct = default)
        => Task.FromResult(ConfigurationGuardResult.Allowed);

    /// <inheritdoc />
    public Task<ConfigurationGuardResult> CheckClusterAsync(ClusterConfig cluster, CancellationToken ct = default)
        => Task.FromResult(ConfigurationGuardResult.Allowed);

    /// <inheritdoc />
    public Task<ConfigurationGuardResult> CheckBatchAsync(
        IReadOnlyList<RouteConfig> routes,
        IReadOnlyList<ClusterConfig> clusters,
        CancellationToken ct = default)
        => Task.FromResult(ConfigurationGuardResult.Allowed);

    /// <inheritdoc />
    public Task<ConfigurationGuardResult> CheckCurrentAsync(CancellationToken ct = default)
        => Task.FromResult(ConfigurationGuardResult.Allowed);
}

/// <summary>
/// Shared reader for the optimistic-concurrency echo used by every configuration write.
///
/// The dashboard form captures the version when it opens and sends it back as
/// <c>X-Config-Version</c>; scripts may use the <c>expectedVersion</c> query parameter
/// instead. When neither is supplied the write is allowed through unchanged, which keeps
/// unattended callers working exactly as before.
/// </summary>
public static class ConfigConcurrency
{
    /// <summary>Request header carrying the version the caller last saw.</summary>
    public const string VersionHeaderName = "X-Config-Version";

    /// <summary>Query-string alternative to <see cref="VersionHeaderName"/>.</summary>
    public const string VersionQueryName = "expectedVersion";

    /// <summary>
    /// Reads the caller's expected version, or <c>null</c> when they did not supply one.
    /// </summary>
    /// <param name="request">The incoming request.</param>
    public static long? ReadExpectedVersion(HttpRequest request)
    {
        var raw = request.Headers[VersionHeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw))
            raw = request.Query[VersionQueryName].FirstOrDefault();

        return long.TryParse(raw, out var version) && version > 0 ? version : null;
    }

    /// <summary>
    /// True when the caller supplied a version and the stored configuration has moved on.
    /// </summary>
    /// <param name="expectedVersion">Version echoed by the caller, or <c>null</c>.</param>
    /// <param name="currentVersion">Version currently in effect.</param>
    public static bool IsStale(long? expectedVersion, long currentVersion)
        => expectedVersion is > 0 && expectedVersion.Value != currentVersion;

    /// <summary>
    /// The canonical 409 body, so the dashboard, the CLI and the tests all read the same shape.
    /// </summary>
    /// <param name="expectedVersion">Version echoed by the caller.</param>
    /// <param name="currentVersion">Version currently in effect.</param>
    public static object BuildConflictPayload(long expectedVersion, long currentVersion) => new
    {
        code = StatusCodes.Status409Conflict,
        message =
            $"Configuration was changed elsewhere (your version {expectedVersion}, current {currentVersion}). " +
            "Reload and re-apply your change.",
        currentVersion
    };
}

/// <summary>
/// Convenience helpers for turning a guard result into the JSON shape the APIs return.
/// </summary>
public static class ConfigurationGuardResultExtensions
{
    /// <summary>
    /// Builds the 400 payload used by every write endpoint: the first error doubles as the
    /// human message, and the full report rides along so a machine client can render it.
    /// </summary>
    /// <param name="result">Guard result to project.</param>
    public static object ToErrorPayload(this ConfigurationGuardResult result) => new
    {
        code = StatusCodes.Status400BadRequest,
        message = result.Errors.Count > 0
            ? $"{result.Errors[0].Field}: {result.Errors[0].Message}"
            : "Configuration validation failed",
        validation = new { ok = result.Ok, errors = result.Errors, warnings = result.Warnings }
    };
}
