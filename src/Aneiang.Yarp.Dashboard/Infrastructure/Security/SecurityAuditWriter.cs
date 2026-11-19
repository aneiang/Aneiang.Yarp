using System.Collections.Concurrent;
using Aneiang.Yarp.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aneiang.Yarp.Dashboard.Infrastructure.Security;

/// <summary>
/// Writes security events to the same audit table as configuration changes, under a
/// <c>security</c> target type so the two streams can be told apart.
/// </summary>
/// <remarks>
/// <para>
/// The repository is resolved lazily through <see cref="IServiceProvider"/> because storage is
/// optional: a dashboard without a database still has to be able to say "login failed" in its log.
/// </para>
/// <para>
/// Persistence is queued rather than awaited. A login must not fail because the audit insert
/// failed, and a request being denied must not be held open by a write that nobody is waiting for.
/// </para>
/// </remarks>
public sealed class SecurityAuditWriter : ISecurityAuditWriter
{
    /// <summary>Value stored in <see cref="AuditLogEntity.TargetType"/> for these rows.</summary>
    public const string TargetType = "security";

    private const int MaxRecent = 200;
    private const int MaxUserAgentLength = 200;

    private readonly ConcurrentQueue<SecurityEvent> _recent = new();
    private readonly IServiceProvider _services;
    private readonly ILogger<SecurityAuditWriter> _logger;

    public SecurityAuditWriter(IServiceProvider services, ILogger<SecurityAuditWriter> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<SecurityEvent> Recent => _recent.ToArray();

    /// <inheritdoc />
    public void Write(SecurityEvent securityEvent)
    {
        if (securityEvent is null) return;

        _recent.Enqueue(securityEvent);
        while (_recent.Count > MaxRecent && _recent.TryDequeue(out _)) { }

        // The log line is the fallback that survives a missing database, and it is also what makes
        // "someone is brute-forcing us" visible without anyone opening the dashboard.
        _logger.Log(
            securityEvent.Success ? LogLevel.Information : LogLevel.Warning,
            "Security event {Type} actor={Actor} role={Role} ip={ClientIp} success={Success} reason={Reason} target={Target}",
            securityEvent.Type, securityEvent.Actor, securityEvent.Role, securityEvent.ClientIp,
            securityEvent.Success, securityEvent.Reason, securityEvent.Target);

        var entity = new AuditLogEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Action = securityEvent.Type.ToString(),
            Target = Truncate(securityEvent.Target) ?? "-",
            TargetType = TargetType,
            Operator = securityEvent.Actor,
            ClientIp = securityEvent.ClientIp,
            Success = securityEvent.Success,
            ErrorMessage = securityEvent.Reason,
            AfterData = securityEvent.UserAgent is { Length: > 0 } ua
                ? $"{{\"userAgent\":\"{Escape(Truncate(ua, MaxUserAgentLength))}\",\"role\":\"{Escape(securityEvent.Role)}\"}}"
                : null,
            Timestamp = DateTime.UtcNow
        };

        _ = PersistAsync(entity);
    }

    private async Task PersistAsync(AuditLogEntity entity)
    {
        try
        {
            var repository = _services.GetService<IAuditLogRepository>();
            if (repository is null) return;

            await repository.SaveAuditLogAsync(entity).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Deliberately swallowed after logging: the caller is mid-request and has already been
            // told the outcome. Losing the audit row is bad; failing the request because of it is worse.
            _logger.LogWarning(ex, "Failed to persist security audit event {Type}", entity.Action);
        }
    }

    private static string? Truncate(string? value, int max = 400) =>
        value is null ? null : value.Length <= max ? value : value[..max];

    private static string Escape(string? value) =>
        value?.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal) ?? string.Empty;
}
