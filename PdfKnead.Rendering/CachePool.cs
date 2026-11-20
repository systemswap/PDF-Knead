namespace PdfKnead.Rendering;

/// <summary>A holder of memory from a <see cref="CachePool"/> that releases it on request.</summary>
public interface ICacheUser
{
    /// <summary>Last use (Stopwatch timestamp): the user idle for the longest time releases first.</summary>
    long LastActive { get; }

    /// <summary>Releases about <paramref name="bytes"/> from this pool, oldest entries first.</summary>
    /// <param name="pool">The pool that requests the release.</param>
    /// <param name="bytes">Number of bytes to release.</param>
    void Free(CachePool pool, long bytes);
}

/// <summary>
/// Memory limit shared by all open documents (display lists, tiles, images), so that the total does not grow with every open tab as
/// it would with separate limits per document. When the limit is exceeded, the document not viewed for the longest time releases
/// first; the visible one last. Thread-safe.
/// </summary>
/// <param name="name">Name for diagnostics.</param>
/// <param name="limit">Limit in bytes.</param>
public sealed class CachePool(string name, long limit)
{
    private readonly List<WeakReference<ICacheUser>> _users = [];
    private long _used;

    [ThreadStatic] private static bool _enforcing;

    /// <summary>Name for diagnostics.</summary>
    public string Name { get; } = name;

    /// <summary>Limit in bytes.</summary>
    public long Limit { get; } = limit;

    /// <summary>Bytes currently accounted to this pool.</summary>
    public long Used => Interlocked.Read(ref _used);

    private static long Available()
    {
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return available > 0 ? available : 8L << 30;
    }

    /// <summary>Display lists of all documents: an eighth of the available memory, 128 MB to 1 GB.</summary>
    public static CachePool Lists { get; } = new("Zeichenlisten", Math.Clamp(Available() / 8, 128L << 20, 1L << 30));

    /// <summary>Finished tiles of all documents: a sixteenth, 96 to 512 MB.</summary>
    public static CachePool Tiles { get; } = new("Kacheln", Math.Clamp(Available() / 16, 96L << 20, 512L << 20));

    /// <summary>Decoded images of all documents: a sixteenth, 96 to 512 MB.</summary>
    public static CachePool Images { get; } = new("Bilder", Math.Clamp(Available() / 16, 96L << 20, 512L << 20));

    /// <summary>Registers a user; it is held only weakly.</summary>
    /// <param name="user">The user.</param>
    public void Register(ICacheUser user)
    {
        lock (_users) _users.Add(new WeakReference<ICacheUser>(user));
    }

    /// <summary>Removes a user (and users that have been collected).</summary>
    /// <param name="user">The user.</param>
    public void Unregister(ICacheUser user)
    {
        lock (_users) _users.RemoveAll(w => !w.TryGetTarget(out var u) || ReferenceEquals(u, user));
    }

    /// <summary>Accounts bytes to the pool (negative to release); does not enforce the limit.</summary>
    /// <param name="bytes">Number of bytes.</param>
    public void Add(long bytes) => Interlocked.Add(ref _used, bytes);

    /// <summary>
    /// Above the limit: asks the users to release memory in order of idle time, <paramref name="current"/> last. Never call this while
    /// holding a lock of one's own (the users take their locks while releasing). Re-entrant calls on the same thread return at once.
    /// </summary>
    /// <param name="current">The user that just added memory; released last.</param>
    public void Enforce(ICacheUser current)
    {
        if (Used <= Limit || _enforcing) return;
        _enforcing = true;
        try
        {
            List<ICacheUser> users;
            lock (_users)
            {
                users = [];
                foreach (var w in _users)
                    if (w.TryGetTarget(out var u) && !ReferenceEquals(u, current)) users.Add(u);
            }
            users.Sort((a, b) => a.LastActive.CompareTo(b.LastActive));
            users.Add(current);
            foreach (var user in users)
            {
                var excess = Used - Limit;
                if (excess <= 0) break;
                user.Free(this, excess);
            }
        }
        finally
        {
            _enforcing = false;
        }
    }
}
