using System.Collections.Concurrent;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Cache with a byte budget for the glyphs of one font. Once the budget is used up, values are still built and returned but
/// no longer stored. Ordinary fonts stay far below the budget; crafted fonts (thousands of glyphs referring to the same huge
/// outline) would otherwise hold more than a gigabyte.
/// </summary>
/// <remarks>Thread-safe. The size function supplies the caller's estimate in bytes.</remarks>
internal sealed class BoundedCache<TKey, TValue>(long maxBytes, Func<TValue, long> size, IEqualityComparer<TKey>? comparer = null)
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, TValue> _entries = new(comparer);
    private long _bytes;

    /// <summary>Bytes currently held (the caller's estimate).</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>
    /// Looks up a stored value. Callers check here first and call <see cref="GetOrAdd"/> only on a miss, so that their lambda
    /// (which usually captures <c>this</c>) is only allocated when a value has to be built.
    /// </summary>
    public bool TryGetValue(TKey key, out TValue value) => _entries.TryGetValue(key, out value!);

    /// <summary>Returns the stored value for <paramref name="key"/> or builds it and stores it if the budget allows.</summary>
    /// <param name="key">Cache key.</param>
    /// <param name="build">Builds the value on a miss; may run concurrently for the same key on several threads.</param>
    /// <returns>The stored value, or the freshly built value if it did not fit into the budget.</returns>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> build)
    {
        if (_entries.TryGetValue(key, out var cached)) return cached;
        var value = build(key);
        var bytes = size(value);
        if (Interlocked.Add(ref _bytes, bytes) > maxBytes)
        {
            Interlocked.Add(ref _bytes, -bytes);
            return value;
        }
        if (_entries.TryAdd(key, value)) return value;
        Interlocked.Add(ref _bytes, -bytes);   // another thread stored a value first
        return _entries.TryGetValue(key, out cached) ? cached : value;
    }
}
