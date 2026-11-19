using System.Collections.Concurrent;

namespace Aneiang.Yarp.Dashboard.Infrastructure.Security;

/// <summary>
/// Sliding-window failure counter for the login endpoint, keyed by both account and client address.
/// </summary>
/// <remarks>
/// <para>
/// Two keys, deliberately: counting per account alone lets an attacker spray one password across
/// many accounts from one host, and counting per address alone lets a distributed attacker take a
/// single admin account down slowly. A rejection needs <em>either</em> key to be over budget.
/// </para>
/// <para>
/// In-memory and per-process, which matches the documented single-control-plane deployment. Behind
/// several instances each one keeps its own budget — that is a real limitation, and it is stated
/// rather than hidden behind a distributed cache that does not exist yet (ADR-3).
/// </para>
/// </remarks>
public sealed class LoginThrottle
{
    private sealed class Budget
    {
        public int Failures;
        public DateTimeOffset StartedAt;
    }

    private readonly ConcurrentDictionary<string, Budget> _windows = new(StringComparer.Ordinal);

    /// <summary>Failures allowed inside one window before the key is refused.</summary>
    public int MaxFailures { get; init; } = 5;

    /// <summary>Length of the counting window.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Upper bound on tracked keys, so a spray cannot grow the dictionary without limit.</summary>
    public int MaxTrackedKeys { get; init; } = 10_000;

    /// <summary>
    /// Returns null when the attempt may proceed, otherwise how long the caller should wait.
    /// </summary>
    public TimeSpan? RetryAfter(string? clientIp, string? username)
    {
        var now = DateTimeOffset.UtcNow;
        TimeSpan? worst = null;

        foreach (var key in Keys(clientIp, username))
        {
            if (!_windows.TryGetValue(key, out var window)) continue;

            var remaining = Remaining(window, now);
            if (remaining <= TimeSpan.Zero || window.Failures < MaxFailures) continue;

            if (worst is null || remaining > worst) worst = remaining;
        }

        return worst;
    }

    /// <summary>Records a failed attempt against both keys.</summary>
    public void RegisterFailure(string? clientIp, string? username)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var key in Keys(clientIp, username))
        {
            // A spray from many addresses must not be able to evict the entry that protects the
            // account, so the cap is enforced by dropping everything rather than by refusing new
            // keys: after a reset the counters are back to zero, which is the safe direction.
            if (_windows.Count >= MaxTrackedKeys) _windows.Clear();

            var window = _windows.GetOrAdd(key, _ => new Budget { StartedAt = now });
            lock (window)
            {
                if (Remaining(window, now) <= TimeSpan.Zero)
                {
                    window.Failures = 0;
                    window.StartedAt = now;
                }

                window.Failures++;
            }
        }
    }

    /// <summary>Clears both keys after a successful login.</summary>
    public void RegisterSuccess(string? clientIp, string? username)
    {
        foreach (var key in Keys(clientIp, username))
            _windows.TryRemove(key, out _);
    }

    /// <summary>Remaining lockout for the account key, for tests and diagnostics.</summary>
    public int FailuresFor(string? clientIp, string? username)
    {
        var key = AccountKey(username);
        if (key is null || !_windows.TryGetValue(key, out var window)) return 0;

        lock (window)
        {
            return Remaining(window, DateTimeOffset.UtcNow) <= TimeSpan.Zero ? 0 : window.Failures;
        }
    }

    private IEnumerable<string> Keys(string? clientIp, string? username)
    {
        var account = AccountKey(username);
        if (account is not null) yield return account;

        if (!string.IsNullOrWhiteSpace(clientIp))
            yield return "ip:" + clientIp;
    }

    private static string? AccountKey(string? username)
        => string.IsNullOrWhiteSpace(username) ? null : "user:" + username.Trim().ToLowerInvariant();

    private TimeSpan Remaining(Budget window, DateTimeOffset now)
    {
        lock (window)
        {
            var elapsed = now - window.StartedAt;
            return elapsed >= Window ? TimeSpan.Zero : Window - elapsed;
        }
    }
}
