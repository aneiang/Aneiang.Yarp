using Aneiang.Yarp.GatewayEnvironments;
using Aneiang.Yarp.Services;

namespace Aneiang.Yarp.Dashboard.Modules.Environments;

/// <summary>
/// Reloads an environment through the same path the dashboard's environment switcher uses, so a
/// promotion publishes exactly when — and only when — a switch to that environment would.
/// </summary>
/// <remarks>
/// Reusing the switch path instead of calling the publisher directly is the whole point: the
/// "publish only when the target is the runtime environment" rule lives in one place, and a second
/// implementation of it would eventually disagree with the first.
/// </remarks>
internal sealed class EnvironmentReloader : IEnvironmentReloader
{
    private readonly IDynamicYarpConfigService _config;

    public EnvironmentReloader(IDynamicYarpConfigService config) => _config = config;

    public async Task<bool> ReloadAsync(string environment, CancellationToken ct = default)
    {
        var result = await _config.SwitchEnvironmentAsync(environment, ct);
        return result.Published;
    }
}

/// <summary>Adapts promotions onto the existing configuration audit log.</summary>
internal sealed class PromotionAudit : IPromotionAudit
{
    private readonly IConfigChangeAuditLog _audit;

    public PromotionAudit(IConfigChangeAuditLog audit) => _audit = audit;

    public void RecordPromoted(string source, string target, string? clientIp, object before, object after) =>
        _audit.RecordSuccess("PromoteEnvironment", target, "dashboard-user", clientIp,
            new { source, configuration = before }, new { source, configuration = after });

    public void RecordRefused(string target, string reason) =>
        _audit.RecordFailure("PromoteEnvironment", target, reason);
}
