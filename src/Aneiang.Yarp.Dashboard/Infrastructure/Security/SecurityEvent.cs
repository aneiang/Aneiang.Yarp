namespace Aneiang.Yarp.Dashboard.Infrastructure.Security;

/// <summary>
/// Security-relevant things that happened, as codes rather than sentences.
/// </summary>
/// <remarks>
/// Separate from configuration changes on purpose: a config audit answers "what does the gateway do
/// now", this one answers "who got in, who was turned away, and who changed the door". The second
/// question is the one asked after an incident, and until now there was no record of it at all.
/// </remarks>
public enum SecurityEventType
{
    LoginSucceeded,
    LoginFailed,
    LoginThrottled,
    AccessDenied,
    TwoFactorEnabled,
    TwoFactorDisabled,
    PluginInstalled,
    PluginUninstalled,
    PluginUpgraded,
    WebhookTargetChanged,
    ExportCredentialChanged
}

/// <summary>One security event. <see cref="Reason"/> carries the machine-readable cause.</summary>
/// <param name="Type">What happened.</param>
/// <param name="Actor">Username or synthetic principal, when known.</param>
/// <param name="Role">Role the principal held at the time, when known.</param>
/// <param name="ClientIp">Client address after trusted-proxy resolution.</param>
/// <param name="UserAgent">Raw User-Agent, truncated by the writer.</param>
/// <param name="Success">Whether the action succeeded; a denial is a successful <em>record</em> of a failure.</param>
/// <param name="Reason">Denial reason code or failure cause, e.g. <c>admin-required</c>.</param>
/// <param name="Target">What was acted on — plugin id, webhook platform, account name.</param>
public sealed record SecurityEvent(
    SecurityEventType Type,
    string? Actor = null,
    string? Role = null,
    string? ClientIp = null,
    string? UserAgent = null,
    bool Success = true,
    string? Reason = null,
    string? Target = null);

/// <summary>Records <see cref="SecurityEvent"/>s. Implementations must never throw.</summary>
public interface ISecurityAuditWriter
{
    /// <summary>
    /// Records the event. Fire-and-forget by contract: an audit write failure must not turn a
    /// successful login into an error response.
    /// </summary>
    void Write(SecurityEvent securityEvent);

    /// <summary>Most recent events, newest last. Bounded; for diagnostics and tests.</summary>
    IReadOnlyList<SecurityEvent> Recent { get; }
}
