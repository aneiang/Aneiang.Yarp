using System.Collections.Concurrent;

namespace Aneiang.Yarp.Plugin.Waf;

/// <summary>A compiled quota: how many requests, over what period, counted per what.</summary>
/// <param name="Limit">Requests permitted per window per partition.</param>
/// <param name="Window">Window length.</param>
/// <param name="Partition">What the count is keyed on.</param>
public readonly record struct WafQuota(int Limit, TimeSpan Window, WafPartition Partition);

/// <summary>
/// Fixed-window counters for <c>action: rate-limit</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two deliberate departures from the route-level rate-limit plugin:
/// </para>
/// <para>
/// <b>The buckets are not shared with it.</b> The roadmap called for one quota model, and the
/// first implementation attempt did share <c>IRateLimiterStore</c>. That turned out to be wrong:
/// the two count different things. A route quota is a contract with a client ("you get 1000/min"),
/// while a WAF quota is a judgement about behaviour ("you have failed this rule five times").
/// Sharing a key space means a burst of blocked requests silently eats the client's legitimate
/// allowance, and neither side can be reasoned about afterwards. What is shared instead is the
/// vocabulary — same duration syntax, same algorithm semantics — because that is the part an
/// operator actually has to learn.
/// </para>
/// <para>
/// <b>The window is fixed, not sliding.</b> A sliding window is smoother, but its whole purpose is
/// to let a burst through as long as the long-run average holds. A throttle exists to stop the
/// burst. Fixed window also makes <c>Retry-After</c> exact, which is what a client needs to back off
/// politely.
/// </para>
/// <para>
/// Buckets live for the lifetime of the middleware, not of a compiled policy: editing an unrelated
/// rule must not hand an attacker a fresh quota.
/// </para>
/// </remarks>
public sealed class WafThrottle
{
    /// <summary>Upper bound on tracked buckets, so a spoofed-IP flood cannot grow the dictionary without limit.</summary>
    private const int MaxBuckets = 4096;

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BucketLifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private long _lastSweepTicks;

    /// <summary>
    /// Counts one request against <paramref name="key"/>.
    /// </summary>
    /// <param name="key">Bucket identity — route, rule and partition value.</param>
    /// <param name="quota">Quota to apply.</param>
    /// <param name="retryAfter">
    /// Time until the window that rejected the request rolls over. Set only when the call returns
    /// <c>false</c>; the caller writes it as <c>Retry-After</c>.
    /// </param>
    /// <returns><c>true</c> when the request is within quota.</returns>
    public bool TryAcquire(string key, WafQuota quota, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;

        var now = DateTime.UtcNow;
        SweepIfDue(now);

        var bucket = _buckets.GetOrAdd(key, _ => new Bucket(now.Ticks));

        lock (bucket)
        {
            var elapsed = now.Ticks - bucket.StartTicks;
            if (elapsed >= quota.Window.Ticks || elapsed < 0)
            {
                bucket.StartTicks = now.Ticks;
                bucket.Count = 0;
                elapsed = 0;
            }

            bucket.LastSeenTicks = now.Ticks;

            if (bucket.Count < quota.Limit)
            {
                bucket.Count++;
                return true;
            }

            // Rejected requests deliberately do not extend the window — otherwise a client that
            // keeps hammering would never be let back in, which turns a throttle into a ban.
            retryAfter = TimeSpan.FromTicks(Math.Max(0, quota.Window.Ticks - elapsed));
            if (retryAfter <= TimeSpan.Zero) retryAfter = quota.Window;
            return false;
        }
    }

    /// <summary>Number of live buckets. Exposed for tests and diagnostics.</summary>
    public int Count => _buckets.Count;

    private void SweepIfDue(DateTime now)
    {
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now.Ticks - last < SweepInterval.Ticks) return;
        if (Interlocked.CompareExchange(ref _lastSweepTicks, now.Ticks, last) != last) return;

        foreach (var pair in _buckets)
        {
            var idle = now.Ticks - Interlocked.Read(ref pair.Value.LastSeenTicks);
            if (idle <= BucketLifetime.Ticks) continue;

            // The lock makes "still idle?" and "remove" atomic with respect to TryAcquire, so a
            // bucket that just became active cannot be dropped between the two steps.
            lock (pair.Value)
            {
                if (now.Ticks - pair.Value.LastSeenTicks > BucketLifetime.Ticks)
                    _buckets.TryRemove(pair.Key, out _);
            }
        }

        if (_buckets.Count <= MaxBuckets) return;

        // Still oversized: drop the least recently seen half. Losing a counter here is far less
        // bad than letting an unbounded dictionary become the outage.
        var victims = _buckets
            .OrderBy(pair => Interlocked.Read(ref pair.Value.LastSeenTicks))
            .Take(_buckets.Count - MaxBuckets / 2)
            .Select(pair => pair.Key);

        foreach (var victim in victims)
            _buckets.TryRemove(victim, out _);
    }

    private sealed class Bucket(long startTicks)
    {
        public long StartTicks = startTicks;
        public int Count;
        public long LastSeenTicks = startTicks;
    }
}
