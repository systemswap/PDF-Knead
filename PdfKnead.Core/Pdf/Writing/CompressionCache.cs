using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// Keeps what hardly or not at all depends on the compression controls across several runs over the same pages (in the
/// compress window, every movement of a control is a complete trial run): image resolutions, font usage, checksums,
/// repacked streams, font subsets, the black-and-white detection and the finished images of the most recent settings.
/// From the second run on, a control movement costs only the images that actually change; the result is byte for byte
/// the same as without the cache.
/// <para>
/// Applies only to exactly the pages it was first used with (other pages are computed as without it), and only as long
/// as the documents do not change. One writer at a time (it locks the cache for the whole run). Stored data stays below
/// <see cref="Budget"/>.
/// </para>
/// </summary>
public sealed class CompressionCache
{
    /// <summary>Creates an empty cache.</summary>
    /// <param name="budgetBytes">Maximum number of bytes of stored data; if null, one eighth of the available memory
    /// (64 MiB … 1 GiB).</param>
    public CompressionCache(long? budgetBytes = null) =>
        Budget = budgetBytes ?? Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 8, 64L << 20, 1L << 30);

    /// <summary>Maximum number of bytes of stored data.</summary>
    public long Budget { get; }

    /// <summary>Bytes currently charged against <see cref="Budget"/> (stored stream data).</summary>
    public long Bytes { get; private set; }

    internal object Gate { get; } = new();

    /// <summary>The pages for which <see cref="ImagePpi"/> and <see cref="Fonts"/> apply.</summary>
    internal PageItem[]? Pages { get; private set; }

    internal Dictionary<(PdfDocument, int), double>? ImagePpi { get; set; }
    internal Dictionary<string, double>? PpiByContent { get; set; }
    internal HashSet<(PdfDocument, int)>? MaskImages { get; set; }
    internal (Dictionary<string, HashSet<int>> Glyphs, HashSet<string> Whole)? Fonts { get; set; }

    /// <summary>Checksum per source object (null = cannot be merged with duplicates).</summary>
    internal Dictionary<(PdfDocument, int), string?> ContentKeys { get; } = [];

    /// <summary>Repacked stream per object and packing kind (<see cref="CompressionSettings.PackKind"/>); null = stays as it is.</summary>
    internal Dictionary<(PdfDocument, int, int), (byte[] Data, PdfObject? Filter, PdfObject? Parms)?> Repacked { get; } = [];

    /// <summary>Font subset per object and packing kind; null = stays as it is.</summary>
    internal Dictionary<(PdfDocument, int, int), (byte[] Data, PdfDictionary Dict)?> Subsets { get; } = [];

    /// <summary>Black-and-white encoding per image, strictness (nearly black-and-white yes/no) and codec; null = the image is not black-and-white.</summary>
    internal Dictionary<(PdfDocument, int, bool, BilevelMethod), (byte[] Data, PdfDictionary Dict)?> Bilevel { get; } = [];

    /// <summary>Finished images per settings; only for the last three settings (so switching back and forth stays fast).</summary>
    internal Dictionary<(PdfDocument, int, ImageSettings), (byte[] Data, PdfDictionary Dict)?> Images { get; } = [];

    private readonly List<ImageSettings> _generations = [];

    /// <summary>What determines a repacked image.</summary>
    internal readonly record struct ImageSettings(int Dpi, int Quality, bool Bilevel, BilevelMethod Codec, int Pack, GraphicsMethod Graphics, int Colors, int Dither,
        int PaletteQuality, MrcSettings Mrc);

    /// <summary>What determines the layers of a scan; all 0 for "like photos".</summary>
    internal readonly record struct MrcSettings(int TextDpi, int BackgroundDpi, int Quality, int Sensitivity, int Colors, BilevelMethod Codec, bool Flatten);

    /// <summary>
    /// MRC layers per image and settings: content of the form XObject and its images (name, dictionary, data), not the
    /// finished form, because that refers to object numbers of one run. Null = not a text scan.
    /// </summary>
    internal Dictionary<(PdfDocument, int, MrcSettings), MrcParts?> Mrc { get; } = [];

    /// <summary>Layers of a scan as the form XObject needs them.</summary>
    internal sealed record MrcParts(byte[] Content, IReadOnlyList<(string Name, PdfDictionary Dict, byte[] Data)> Images)
    {
        public long Bytes => Content.Length + Images.Sum(i => (long)i.Data.Length);
    }

    /// <summary>Claims the cache for these pages: false if it already belongs to other pages (then resolutions and font usage do not apply).</summary>
    internal bool Matches(IReadOnlyList<PageItem> pages)
    {
        if (Pages is null)
        {
            Pages = [.. pages];
            return true;
        }
        if (Pages.Length != pages.Count) return false;
        for (var i = 0; i < Pages.Length; i++)
            if (!ReferenceEquals(Pages[i], pages[i])) return false;
        return true;
    }

    /// <summary>Is there room for this many bytes? If so, they are charged.</summary>
    internal bool Charge(long bytes)
    {
        if (Bytes + bytes > Budget) return false;
        Bytes += bytes;
        return true;
    }

    /// <summary>Stores a finished image; a fourth setting evicts the oldest one together with its images.</summary>
    internal void AddImage((PdfDocument, int, ImageSettings) key, (byte[] Data, PdfDictionary Dict)? image)
    {
        var settings = key.Item3;
        _generations.Remove(settings);
        _generations.Add(settings);
        if (_generations.Count > 3)
        {
            var oldest = _generations[0];
            _generations.RemoveAt(0);
            foreach (var old in Images.Keys.Where(k => k.Item3 == oldest).ToList())
            {
                Bytes -= Images[old]?.Data.Length ?? 0;
                Images.Remove(old);
            }
        }
        if (Charge(image?.Data.Length ?? 0)) Images[key] = image;
    }

    private readonly List<MrcSettings> _mrcGenerations = [];

    /// <summary>
    /// Stores MRC layers; as with the images, a fourth setting evicts the oldest one. Without eviction, every control
    /// position would stay until the budget is full, after which nothing else could be cached either.
    /// </summary>
    internal void AddMrc((PdfDocument, int, MrcSettings) key, MrcParts? parts)
    {
        var settings = key.Item3;
        _mrcGenerations.Remove(settings);
        _mrcGenerations.Add(settings);
        if (_mrcGenerations.Count > 3)
        {
            var oldest = _mrcGenerations[0];
            _mrcGenerations.RemoveAt(0);
            foreach (var old in Mrc.Keys.Where(k => k.Item3 == oldest).ToList())
            {
                Bytes -= Mrc[old]?.Bytes ?? 0;
                Mrc.Remove(old);
            }
        }
        if (Charge(parts?.Bytes ?? 0)) Mrc[key] = parts;
    }
}
