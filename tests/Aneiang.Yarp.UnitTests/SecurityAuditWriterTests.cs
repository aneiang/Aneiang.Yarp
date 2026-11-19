using Aneiang.Yarp.Dashboard.Infrastructure.Security;
using Aneiang.Yarp.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Covers <see cref="SecurityAuditWriter"/> — the component that makes "someone tried to log in as
/// admin forty times last night" an answerable question (SEC-10).
/// </summary>
/// <remarks>
/// The write is deliberately fire-and-forget so a login cannot fail because an audit insert did.
/// That design is exactly why it needs a test: a task nobody awaits can stop running without
/// anything turning red, and the failure mode is an empty audit table that still looks healthy.
/// </remarks>
public class SecurityAuditWriterTests
{
    private sealed class RecordingAuditRepository : IAuditLogRepository
    {
        public readonly TaskCompletionSource<AuditLogEntity> Written =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<AuditLogEntity>> GetAuditLogsAsync(int limit = 200, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditLogEntity>>([]);

        public Task<IReadOnlyList<AuditLogEntity>> GetAuditLogsByTargetAsync(string target, int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditLogEntity>>([]);

        public Task SaveAuditLogAsync(AuditLogEntity audit, CancellationToken ct = default)
        {
            Written.TrySetResult(audit);
            return Task.CompletedTask;
        }

        public Task DeleteOldAuditLogsAsync(int keepCount, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ExplodingAuditRepository : IAuditLogRepository
    {
        public Task<IReadOnlyList<AuditLogEntity>> GetAuditLogsAsync(int limit = 200, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditLogEntity>>([]);

        public Task<IReadOnlyList<AuditLogEntity>> GetAuditLogsByTargetAsync(string target, int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditLogEntity>>([]);

        public Task SaveAuditLogAsync(AuditLogEntity audit, CancellationToken ct = default)
            => throw new InvalidOperationException("database is gone");

        public Task DeleteOldAuditLogsAsync(int keepCount, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static (SecurityAuditWriter Writer, IServiceProvider Services) Build(IAuditLogRepository? repository)
    {
        var collection = new ServiceCollection();
        if (repository is not null) collection.AddSingleton(repository);

        var services = collection.BuildServiceProvider();
        return (new SecurityAuditWriter(services, NullLogger<SecurityAuditWriter>.Instance), services);
    }

    [Fact]
    public async Task A_security_event_reaches_the_audit_table()
    {
        var repository = new RecordingAuditRepository();
        var (writer, services) = Build(repository);
        using var _ = services as IDisposable;

        writer.Write(new SecurityEvent(
            SecurityEventType.LoginFailed,
            Actor: "admin",
            Role: "admin",
            ClientIp: "203.0.113.7",
            UserAgent: "curl/8",
            Success: false,
            Reason: "invalid-password",
            Target: "admin"));

        var saved = await repository.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("LoginFailed", saved.Action);
        // The target type is what lets these rows be queried apart from configuration changes.
        Assert.Equal(SecurityAuditWriter.TargetType, saved.TargetType);
        Assert.Equal("admin", saved.Target);
        Assert.Equal("admin", saved.Operator);
        Assert.Equal("203.0.113.7", saved.ClientIp);
        Assert.False(saved.Success);
        Assert.Equal("invalid-password", saved.ErrorMessage);
        Assert.Contains("curl/8", saved.AfterData, StringComparison.Ordinal);
    }

    [Fact]
    public void The_event_is_also_kept_in_memory_for_diagnostics()
    {
        // The SQLite row survives a restart; this list is what a test or a support call can read
        // without one. Both are written from the same call.
        var (writer, services) = Build(repository: null);
        using var _ = services as IDisposable;

        writer.Write(new SecurityEvent(SecurityEventType.AccessDenied, Actor: "viewer", Reason: "admin-required"));

        Assert.Single(writer.Recent);
        Assert.Equal(SecurityEventType.AccessDenied, writer.Recent[0].Type);
    }

    [Fact]
    public void A_dashboard_without_storage_still_records_the_event()
    {
        // Storage is optional. Resolving the repository lazily is what keeps a dashboard without a
        // database from throwing on the first failed login.
        var (writer, services) = Build(repository: null);
        using var _ = services as IDisposable;

        var exception = Record.Exception(() =>
            writer.Write(new SecurityEvent(SecurityEventType.LoginSucceeded, Actor: "admin")));

        Assert.Null(exception);
        Assert.Single(writer.Recent);
    }

    [Fact]
    public void A_failing_audit_write_does_not_reach_the_caller()
    {
        // The contract is "must never throw": the caller is mid-request and has already been told
        // the outcome. A broken audit table turning a successful login into a 500 would mean an
        // attacker can disable logging by filling the disk.
        var (writer, services) = Build(new ExplodingAuditRepository());
        using var _ = services as IDisposable;

        var exception = Record.Exception(() =>
            writer.Write(new SecurityEvent(SecurityEventType.LoginSucceeded, Actor: "admin")));

        Assert.Null(exception);
    }

    [Fact]
    public void A_null_event_is_ignored()
    {
        var (writer, services) = Build(repository: null);
        using var _ = services as IDisposable;

        writer.Write(null!);

        Assert.Empty(writer.Recent);
    }
}
