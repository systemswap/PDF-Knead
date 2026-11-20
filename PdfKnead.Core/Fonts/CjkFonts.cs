using System.Collections.Concurrent;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Substitutes for non-embedded Chinese, Japanese and Korean fonts: Noto Sans/Serif CJK (OFL) live in the folder "Schriften-CJK"
/// next to the program, not inside the executable. If the folder is missing, <see cref="Get"/> returns null and the user
/// interface reports it (<see cref="Pdf.Document.PdfDocument.MissingCjkFonts"/>).
/// </summary>
internal static class CjkFonts
{
    public const string FolderName = "Schriften-CJK";
    private const string Sans = "NotoSansCJK-Regular.ttc", Serif = "NotoSerifCJK-Regular.ttc";

    /// <summary>For tests only: folder containing the fonts (otherwise next to the program).</summary>
    internal static string? FolderForTests;

    private static readonly ConcurrentDictionary<string, Lazy<byte[]?>> Files = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(string File, int Index), OpenTypeFont?> Fonts = new();

    /// <summary>Returns the font for a character collection (Japan1, GB1, CNS1, Korea1, KR), with or without serifs.</summary>
    /// <param name="ordering">The Ordering of the CIDSystemInfo.</param>
    /// <param name="serif">Whether a serif face is wanted; falls back to sans serif if the serif collection is missing.</param>
    /// <returns>The font, or null if the ordering is unknown or the fonts are not available.</returns>
    public static OpenTypeFont? Get(string ordering, bool serif)
    {
        // Order of the fonts in the collections: jp, kr, sc, tc, hk (Adobe-KR as Korea1: the glyphs are found through Unicode, not the CID)
        var index = ordering switch { "Japan1" => 0, "Korea1" or "KR" => 1, "GB1" => 2, "CNS1" => 3, _ => -1 };
        if (index < 0 || Folder() is not { } folder) return null;
        var file = Path.Combine(folder, serif && File.Exists(Path.Combine(folder, Serif)) ? Serif : Sans);
        return Fonts.GetOrAdd((file, index), key =>
        {
            if (Files.GetOrAdd(key.File, f => new Lazy<byte[]?>(() => Load(f))).Value is not { } data) return null;
            try
            {
                return OpenTypeFont.Parse(data, key.Index) is { Broken: false } font ? font : null;
            }
            catch (Exception e) when (e is FontFormatException or IndexOutOfRangeException or ArgumentException)
            {
                return null;
            }
        });
    }

    /// <summary>Decides whether a font has serifs (Mincho, Ming, Song, Batang … in the name, or the Serif flag in the FontDescriptor).</summary>
    /// <param name="baseFont">The BaseFont name.</param>
    /// <param name="flags">The /Flags of the FontDescriptor.</param>
    /// <returns>True for a serif font.</returns>
    public static bool IsSerif(string baseFont, int flags)
    {
        var lower = baseFont.ToLowerInvariant();
        return (flags & 2) != 0 || lower.Contains("mincho") || lower.Contains("ming") || lower.Contains("song") || lower.Contains("batang")
               || lower.Contains("myeongjo") || lower.Contains("ryumin") || lower.Contains("serif") || lower.Contains("kai");
    }

    private static byte[]? Load(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Finds the folder "Schriften-CJK": environment variable PDFKNEAD_CJK, next to the program, next to the AppImage file.</summary>
    private static string? Folder()
    {
        // Remembered (including "none found"): text layout asks for every character that neither the font nor Liberation has
        // (emoji while typing); checking the files per character cost 31 ms per layout with 200 emoji. Searched again when the
        // test folder or the environment variable changes.
        var key = (FolderForTests, Environment.GetEnvironmentVariable("PDFKNEAD_CJK"));
        if (_folder is { } known && known.Key == key) return known.Folder;
        string? found = null;
        foreach (var candidate in Candidates())
            if (candidate is not null && File.Exists(Path.Combine(candidate, Sans)))
            {
                found = candidate;
                break;
            }
        _folder = new FolderChoice(key, found);
        return found;
    }

    // One object (assigned atomically): rendering threads and the user interface ask at the same time
    private sealed record FolderChoice((string?, string?) Key, string? Folder);

    private static FolderChoice? _folder;

    private static IEnumerable<string?> Candidates()
    {
        yield return FolderForTests;
        yield return Environment.GetEnvironmentVariable("PDFKNEAD_CJK");
        yield return Path.Combine(AppContext.BaseDirectory, FolderName);
        if (Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } image && Path.GetDirectoryName(image) is { } dir)
            yield return Path.Combine(dir, FolderName);
    }
}
