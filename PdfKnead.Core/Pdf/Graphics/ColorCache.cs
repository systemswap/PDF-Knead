using System.Collections.Concurrent;

namespace PdfKnead.Core.Pdf.Graphics;

/// <summary>
/// Converted single colours per key (separation colours, ICC), at most 65,536; further entries are not stored. Reads take no lock:
/// images are converted in parallel bands, and with a lock (Dictionary) the threads waited for each other (DeviceN 3000 × 2000:
/// 950 ms instead of 265 ms).
/// </summary>
internal sealed class ColorCache
{
    private const int MaxEntries = 65536;
    private readonly ConcurrentDictionary<long, Rgb> _entries = new();
    private int _count;

    public bool TryGet(long key, out Rgb rgb) => _entries.TryGetValue(key, out rgb);

    public void Add(long key, Rgb rgb)
    {
        if (Volatile.Read(ref _count) >= MaxEntries) return;
        if (_entries.TryAdd(key, rgb)) Interlocked.Increment(ref _count);
    }
}
