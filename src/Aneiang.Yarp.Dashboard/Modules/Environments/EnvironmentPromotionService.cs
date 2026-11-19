using System.Collections.Concurrent;
using Aneiang.Yarp.GatewayEnvironments;
using Aneiang.Yarp.Models;
using Aneiang.Yarp.Services;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Configuration;

namespace Aneiang.Yarp.Dashboard.Modules.Environments;

/// <summary>Request to prepare a promotion.</summary>
public sealed class PreparePromotionRequest
{
    /// <summary>Environment to take the configuration from. Defaults to the one being edited.</summary>
    public string? Source { get; set; }

    /// <summary>Environment to write into.</summary>
    public string? Target { get; set; }

    /// <summary>
    /// Approve replacing the target with an empty configuration. Off by default: an empty source is
    /// usually a store that failed to load, and treating it as "deploy nothing" would delete every
    /// route in the target.
    /// </summary>
    public bool AllowEmptySource { get; set; }
}

/// <summary>Request to apply a prepared promotion.</summary>
public sealed class ApplyPromotionRequest
{
    /// <summary>Token returned by <c>POST api/environments/promote/prepare</c>.</summary>
    public string? Token { get; set; }
}

/// <summary>What prepare hands back: the change, the problems, and the ticket to apply it.</summary>
public sealed class PreparedPromotion
{
    public required string Token { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required EnvironmentDiff Diff { get; init; }
    public required IReadOnlyList<string> Problems { get; init; }
    public required bool CanApply { get; init; }
    public required bool TargetIsRuntime { get; init; }
}

/// <summary>Outcome of an applied promotion.</summary>
public sealed record PromotionResult(
    string Source,
    string Target,
    int Routes,
    int Clusters,
    bool Published,
    EnvironmentDiff Diff);

/// <summary>
/// What a promotion needs from the running gateway: make the target environment the one the process
/// serves from (and the one the dashboard shows), publishing when it is the runtime environment.
/// </summary>
/// <remarks>
/// Narrow on purpose. Promotion only needs "reload this store", but the service behind it can
/// rewrite routing, publish a snapshot and bump a version — none of which a promotion test should
/// have to stub to prove that a stale token is refused.
/// </remarks>
public interface IEnvironmentReloader
{
    Task<bool> ReloadAsync(string environment, CancellationToken ct = default);
}

/// <summary>Audit seam for promotions, kept to the two facts worth recording.</summary>
public interface IPromotionAudit
{
    void RecordPromoted(string source, string target, string? clientIp, object before, object after);

    void RecordRefused(string target, string reason);
}

/// <summary>Thrown when a promotion cannot proceed for a reason the caller can act on.</summary>
public sealed class PromotionException(string message, int statusCode = 400) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Two-step promotion of one environment's configuration into another.
/// </summary>
/// <remarks>
/// <para>
/// The two-step shape is the whole point. A one-shot "copy staging to prod" button is a loaded gun:
/// the operator has no idea what it will change until it has changed it. Preparing returns the
/// diff, the validation problems, and a token; applying requires that token and re-checks that the
/// diff is still byte-for-byte the one that was shown. If a route moved on either side in the
/// meantime, the promotion is refused and the operator re-approves — which is the behaviour you
/// want for the one operation that can rewrite production routing in a single call.
/// </para>
/// <para>
/// Everything here runs against stores, never against the in-memory working set: the working set
/// belongs to whichever environment the dashboard is editing and must not be disturbed by a
/// promotion into a third one.
/// </para>
/// </remarks>
public sealed class EnvironmentPromotionService
{
    /// <summary>How long an approval stays valid. Short on purpose: it is a confirmation, not a permission.</summary>
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);

    private readonly IEnvironmentConfigStore _store;
    private readonly IEnvironmentReloader _reloader;
    private readonly GatewayEnvironmentRegistry _registry;
    private readonly EnvironmentDiffService _diffService;
    private readonly IPromotionAudit _audit;
    private readonly ILogger<EnvironmentPromotionService> _logger;

    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    public EnvironmentPromotionService(
        IEnvironmentConfigStore store,
        IEnvironmentReloader reloader,
        GatewayEnvironmentRegistry registry,
        EnvironmentDiffService diffService,
        IPromotionAudit audit,
        ILogger<EnvironmentPromotionService> logger)
    {
        _store = store;
        _reloader = reloader;
        _registry = registry;
        _diffService = diffService;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>Computes the diff without creating a token — the read-only view.</summary>
    public async Task<EnvironmentDiff> DiffAsync(string? source, string? target, CancellationToken ct = default)
    {
        var (from, to) = Resolve(source, target);
        return await BuildDiffAsync(from, to, ct);
    }

    /// <summary>Validates and issues a single-use approval token bound to the diff that was shown.</summary>
    public async Task<PreparedPromotion> PrepareAsync(PreparePromotionRequest request, CancellationToken ct = default)
    {
        var (from, to) = Resolve(request.Source, request.Target);

        var sourceConfig = await _store.ReadAsync(from, ct);
        var targetConfig = await _store.ReadAsync(to, ct);

        var diff = _diffService.Compare(from, sourceConfig, to, targetConfig);
        var problems = EnvironmentCandidateValidator.Validate(sourceConfig, request.AllowEmptySource);

        if (diff.Identical && problems.Count == 0)
            problems = ["The target already matches the source; there is nothing to promote."];

        var token = Guid.NewGuid().ToString("N");
        var expiresAt = DateTimeOffset.UtcNow.Add(TokenLifetime);

        SweepExpired();

        _pending[token] = new Pending
        {
            Source = from,
            Target = to,
            Fingerprint = ConfigFingerprint.Of(sourceConfig, targetConfig),
            ExpiresAt = expiresAt
        };

        return new PreparedPromotion
        {
            Token = token,
            ExpiresAt = expiresAt,
            Diff = diff,
            Problems = problems,
            CanApply = problems.Count == 0,
            TargetIsRuntime = string.Equals(to, _registry.RuntimeEnvironment, StringComparison.OrdinalIgnoreCase)
        };
    }

    /// <summary>
    /// Applies a prepared promotion. The token is consumed even when the apply is refused, so a
    /// stale approval cannot be retried against a config that has since changed.
    /// </summary>
    public async Task<PromotionResult> ApplyAsync(string token, string? clientIp, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || !_pending.TryRemove(token, out var pending))
            throw new PromotionException("Unknown or already used approval token. Prepare the promotion again.", 409);

        if (pending.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new PromotionException("The approval expired. Prepare the promotion again.", 409);

        var sourceConfig = await _store.ReadAsync(pending.Source, ct);
        var targetConfig = await _store.ReadAsync(pending.Target, ct);
        var diff = _diffService.Compare(pending.Source, sourceConfig, pending.Target, targetConfig);

        // The operator approved this exact diff. Anything else — a route edited in staging, a
        // cluster changed in prod by someone else — has to be looked at again.
        if (!string.Equals(ConfigFingerprint.Of(sourceConfig, targetConfig), pending.Fingerprint, StringComparison.Ordinal))
        {
            _audit.RecordRefused(pending.Target,
                $"The configuration changed after approval ({pending.Source} → {pending.Target}).");
            throw new PromotionException(
                "The configuration changed after it was approved. Prepare the promotion again to see the current diff.", 409);
        }

        // Re-validate rather than trusting the prepared result: this is the last gate before a
        // write that can take the target environment down.
        var problems = EnvironmentCandidateValidator.Validate(sourceConfig, allowEmptySource: false);
        if (problems.Count > 0)
        {
            _audit.RecordRefused(pending.Target, "Promotion refused: " + problems[0]);
            throw new PromotionException("Promotion refused: " + problems[0], 400);
        }

        await _store.ReplaceAsync(pending.Target, sourceConfig, ct);

        // Reloading through the normal switch path is what publishes when the target happens to be
        // the runtime environment — and what keeps the editing view honest when it was pointing at
        // the target. Doing it here means the publish semantics are the ones already tested.
        var published = await _reloader.ReloadAsync(pending.Target, ct);

        _audit.RecordPromoted(
            pending.Source,
            pending.Target,
            clientIp,
            new { routes = targetConfig.Routes.Count, clusters = targetConfig.Clusters.Count },
            new { routes = sourceConfig.Routes.Count, clusters = sourceConfig.Clusters.Count });

        _logger.LogInformation(
            "Promoted '{Source}' → '{Target}': {Routes} route(s), {Clusters} cluster(s), published={Published}",
            pending.Source, pending.Target, sourceConfig.Routes.Count, sourceConfig.Clusters.Count, published);

        return new PromotionResult(
            pending.Source,
            pending.Target,
            sourceConfig.Routes.Count,
            sourceConfig.Clusters.Count,
            published,
            diff);
    }

    #region Internals

    private (string Source, string Target) Resolve(string? source, string? target)
    {
        var from = string.IsNullOrWhiteSpace(source) ? _registry.CurrentEnvironment : source.Trim();
        var to = string.IsNullOrWhiteSpace(target) ? _registry.RuntimeEnvironment : target.Trim();

        var names = _registry.Environments.Select(e => e.Name).ToArray();
        if (!names.Contains(from, StringComparer.OrdinalIgnoreCase))
            throw new PromotionException($"Unknown source environment '{from}'. Available: {string.Join(", ", names)}.");

        if (!names.Contains(to, StringComparer.OrdinalIgnoreCase))
            throw new PromotionException($"Unknown target environment '{to}'. Available: {string.Join(", ", names)}.");

        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            throw new PromotionException("Source and target are the same environment.");

        return (from, to);
    }

    private async Task<EnvironmentDiff> BuildDiffAsync(string from, string to, CancellationToken ct)
    {
        var sourceConfig = await _store.ReadAsync(from, ct);
        var targetConfig = await _store.ReadAsync(to, ct);
        return _diffService.Compare(from, sourceConfig, to, targetConfig);
    }

    private void SweepExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _pending)
        {
            if (pair.Value.ExpiresAt <= now) _pending.TryRemove(pair.Key, out _);
        }
    }

    private sealed class Pending
    {
        public required string Source { get; init; }
        public required string Target { get; init; }
        public required string Fingerprint { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
    }

    #endregion
}
