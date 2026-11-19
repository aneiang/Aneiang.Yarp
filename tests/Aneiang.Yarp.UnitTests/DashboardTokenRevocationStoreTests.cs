using Aneiang.Yarp.Dashboard.Infrastructure.Auth;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Covers <see cref="DashboardTokenRevocationStore"/> — the list that makes "log out" mean something
/// before the token expires (SEC-11).
/// </summary>
/// <remarks>
/// The store is in-memory, so its two real risks are unbounded growth and forgetting entries too
/// early (which would silently resurrect a logged-out session). Both are decided by the clock, so
/// these tests drive the clock instead of waiting on it.
/// </remarks>
public class DashboardTokenRevocationStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now;

        public ManualClock(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    [Fact]
    public void A_revoked_id_is_remembered()
    {
        var store = new DashboardTokenRevocationStore(new ManualClock(Start));

        store.Revoke("token-a", Start.AddHours(8));

        Assert.True(store.IsRevoked("token-a"));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void An_unknown_id_is_not_revoked()
    {
        var store = new DashboardTokenRevocationStore(new ManualClock(Start));
        store.Revoke("token-a", Start.AddHours(8));

        Assert.False(store.IsRevoked("token-b"));
    }

    [Fact]
    public void Revoking_the_same_id_twice_does_not_grow_the_list()
    {
        var store = new DashboardTokenRevocationStore(new ManualClock(Start));

        store.Revoke("token-a", Start.AddHours(8));
        store.Revoke("token-a", Start.AddHours(9));

        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void An_id_is_forgotten_once_its_own_expiry_passes()
    {
        // Past its expiry the token is dead by signature anyway, so keeping the id would only grow
        // the list forever.
        var clock = new ManualClock(Start);
        var store = new DashboardTokenRevocationStore(clock);
        store.Revoke("token-a", Start.AddHours(1));

        clock.Advance(TimeSpan.FromHours(2));

        Assert.False(store.IsRevoked("token-a"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void A_short_lived_id_does_not_evict_a_long_lived_one()
    {
        // Pruning is per-entry: one token expiring must not clear the list.
        var clock = new ManualClock(Start);
        var store = new DashboardTokenRevocationStore(clock);

        store.Revoke("short", Start.AddHours(1));
        store.Revoke("long", Start.AddHours(8));

        clock.Advance(TimeSpan.FromHours(2));

        Assert.False(store.IsRevoked("short"));
        Assert.True(store.IsRevoked("long"));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void An_empty_id_is_not_tracked()
    {
        // A token without a jti cannot be revoked by id. Recording "" would revoke every such
        // token at once, which is worse than not revoking it.
        var store = new DashboardTokenRevocationStore(new ManualClock(Start));

        store.Revoke(null, Start.AddHours(8));
        store.Revoke("", Start.AddHours(8));

        Assert.Equal(0, store.Count);
        Assert.False(store.IsRevoked(null));
        Assert.False(store.IsRevoked(""));
    }
}
