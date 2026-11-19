using System.Collections.Concurrent;

namespace Aneiang.Yarp.Dashboard.Infrastructure.Auth;

/// <summary>
/// Remembers which token ids were logged out before they expired.
/// </summary>
/// <remarks>
/// <para>
/// A JWT is valid until it expires — that is the whole point of not looking the session up on every
/// request. The trade is that "log out" could only delete the cookie, leaving a copy of the token
/// fully usable. This store is the smallest thing that fixes it: a set of ids with their expiry,
/// pruned as they age out.
/// </para>
/// <para>
/// In-memory, so a restart forgets the list (tokens survive, revocations do not) and several
/// instances do not share it. Both are consequences of the single-control-plane deployment this
/// dashboard documents; saying so is better than pretending the guarantee is stronger than it is.
/// </para>
/// </remarks>
public sealed class DashboardTokenRevocationStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _revoked = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private int _lastPruneCount;

    public DashboardTokenRevocationStore() : this(TimeProvider.System) { }

    /// <summary>Test seam: the expiry prune is not observable without controlling the clock.</summary>
    internal DashboardTokenRevocationStore(TimeProvider time) => _time = time;

    /// <summary>Number of ids currently remembered, after pruning.</summary>
    public int Count
    {
        get
        {
            Prune();
            return _revoked.Count;
        }
    }

    /// <summary>Revokes a token id until its own expiry — after that the token is dead anyway.</summary>
    public void Revoke(string? tokenId, DateTimeOffset expiresAt)
    {
        if (string.IsNullOrEmpty(tokenId)) return;

        _revoked[tokenId] = expiresAt;
        Prune();
    }

    /// <summary>True when this token id was explicitly logged out.</summary>
    public bool IsRevoked(string? tokenId)
    {
        if (string.IsNullOrEmpty(tokenId)) return false;
        if (!_revoked.TryGetValue(tokenId, out var expiresAt)) return false;

        if (expiresAt <= _time.GetUtcNow())
        {
            _revoked.TryRemove(tokenId, out _);
            return false;
        }

        return true;
    }

    private void Prune()
    {
        // Cheap guard: pruning walks the dictionary, so only do it when it has grown.
        if (_revoked.Count < _lastPruneCount + 64) return;

        var now = _time.GetUtcNow();
        foreach (var (id, expiresAt) in _revoked)
        {
            if (expiresAt <= now) _revoked.TryRemove(id, out _);
        }

        _lastPruneCount = _revoked.Count;
    }
}
