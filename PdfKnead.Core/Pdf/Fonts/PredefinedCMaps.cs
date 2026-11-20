using System.Collections.Concurrent;
using System.IO.Compression;

namespace PdfKnead.Core.Pdf.Fonts;

/// <summary>
/// Predefined CMaps (ISO 32000-2, 9.7.5.2) for Chinese, Japanese and Korean from Adobe's cmap-resources (BSD licence, embedded in the
/// program as Fonts/cmaps.zip, created with <c>pdf-knead-messen --cmaps-packen</c>, a local tool; Adobe-KR-9 added on 6.11.2026 —
/// a repacked archive must keep it, docs/FEHLERGEDAECHTNIS.md, No. 570). Needed for embedded fonts as well:
/// "UniGB-UCS2-H" and the like map codes to CIDs; treating unknown names as Identity would give wrong characters.
/// </summary>
internal static class PredefinedCMaps
{
    private const int MaxUseDepth = 4;
    private static readonly Lazy<byte[]?> Archive = new(() =>
    {
        using var stream = typeof(PredefinedCMaps).Assembly.GetManifestResourceStream("Fonts/cmaps.zip");
        if (stream is null) return null;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    });
    private static readonly ConcurrentDictionary<string, CMap> Cache = new(StringComparer.Ordinal);
    private static readonly Lock ZipLock = new();

    /// <summary>CMap by name (Identity-H/V built in); null if unknown. Thread-safe; parsed CMaps are cached for the process lifetime.</summary>
    public static CMap? Get(string name) => Get(name, 0);

    private static CMap? Get(string name, int depth)
    {
        if (name is "Identity-H" or "Identity-V") return CMap.Identity(name == "Identity-V");
        if (Cache.TryGetValue(name, out var cached)) return cached;
        if (depth > MaxUseDepth || Read(name) is not { } data) return null;   // do not cache unknown names (the names come from the file)
        var cmap = CMap.Parse(data, parent => Get(parent, depth + 1));
        return Cache.GetOrAdd(name, cmap);
    }

    private static readonly ConcurrentDictionary<string, int?> Supplements = new(StringComparer.Ordinal);

    /// <summary>The supplement of the character collection a predefined CMap names in its /CIDSystemInfo; null if unknown.</summary>
    /// <param name="name">The name of the CMap.</param>
    /// <returns>The supplement, or null.</returns>
    public static int? Supplement(string name) => Supplements.GetOrAdd(name, static n =>
    {
        if (Read(n) is not { } data) return null;
        var text = System.Text.Encoding.Latin1.GetString(data);
        var at = text.IndexOf("/Supplement", StringComparison.Ordinal);
        if (at < 0) return null;
        var digits = new string(text.AsSpan(at + 11).TrimStart().ToArray().TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, System.Globalization.CultureInfo.InvariantCulture, out var supplement) ? supplement : null;
    });

    private static byte[]? Read(string name)
    {
        if (Archive.Value is not { } archive || name.Length is 0 or > 64) return null;
        lock (ZipLock)
        {
            using var zip = new ZipArchive(new MemoryStream(archive, writable: false), ZipArchiveMode.Read);
            if (zip.GetEntry(name) is not { Length: > 0 and < 8 << 20 } entry) return null;
            using var stream = entry.Open();
            using var copy = new MemoryStream((int)entry.Length);
            stream.CopyTo(copy);
            return copy.ToArray();
        }
    }

    // ------------------------------------------------------------ CID → Unicode

    /// <summary>The character collection of Adobe (the ordering) a predefined CMap belongs to, by its name; null for Identity-H, Identity-V and unknown names.</summary>
    /// <param name="name">The name of the CMap.</param>
    /// <returns>"GB1", "CNS1", "Japan1", "Korea1", "KR" or null.</returns>
    public static string? Collection(string name) => name switch
    {
        "Identity-H" or "Identity-V" => null,
        _ when name.StartsWith("UniAKR", StringComparison.Ordinal) || name.StartsWith("Adobe-KR", StringComparison.Ordinal) => "KR",
        _ when name.Contains("GB", StringComparison.Ordinal) => "GB1",
        _ when name.Contains("CNS", StringComparison.Ordinal) || name.StartsWith("B5", StringComparison.Ordinal) ||
               name.Contains("ETen", StringComparison.Ordinal) || name.StartsWith("HKscs", StringComparison.Ordinal) => "CNS1",
        _ when name.Contains("KSC", StringComparison.Ordinal) || name.StartsWith("UniKS", StringComparison.Ordinal) => "Korea1",
        _ when name.Contains("RKSJ", StringComparison.Ordinal) || name.StartsWith("UniJIS", StringComparison.Ordinal) || name is "H" or "V" or "EUC-H" or "EUC-V" ||
               name.StartsWith("83pv", StringComparison.Ordinal) || name.StartsWith("90", StringComparison.Ordinal) || name.StartsWith("Add-", StringComparison.Ordinal) ||
               name.StartsWith("Ext-", StringComparison.Ordinal) || name is "Hankaku" or "Hiragana" or "Katakana" or "Roman" or "WP-Symbol" => "Japan1",
        _ => null,
    };

    /// <summary>
    /// Whether the ordering (CIDSystemInfo) is one of the CJK character collections Japan1, GB1, CNS1, Korea1 or KR (Adobe-KR-9, which
    /// ISO 32000-2, 9.7.5.2 requires a reader to support; its CIDs differ from Korea1; docs/FEHLERGEDAECHTNIS.md, No. 570).
    /// </summary>
    public static bool IsCjk(string? ordering) => ordering is "Japan1" or "GB1" or "CNS1" or "Korea1" or "KR";

    /// <summary>
    /// Text for a CID of the collection (Adobe's Adobe-*-UCS2 from mapping-resources-pdf, as common readers use it; the Uni… CMaps inverted
    /// do not reach, for example, the half-width CIDs 231 onwards of Japan1); null if there is none.
    /// </summary>
    public static string? Text(string? ordering, int cid)
    {
        if (!IsCjk(ordering) || cid is < 0 or > 0xFFFF) return null;
        return Get($"Adobe-{ordering}-UCS2")?.ToUnicode((uint)cid, 2);
    }
}
