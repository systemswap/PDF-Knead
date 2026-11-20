using System.Text.Json.Serialization;
using PdfKnead.Core.Pdf.Document;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Page format for File → New and "insert blank page": a known format (A0 … A6, Letter, Legal) or a custom size in mm,
/// portrait or landscape, together with the number of pages.
/// </summary>
/// <param name="Format">Name of the format (see <see cref="Formats"/>) or <see cref="Custom"/>.</param>
/// <param name="Landscape">Landscape (for known formats; for a custom size width and height apply as entered).</param>
/// <param name="CustomWidth">Custom width in mm.</param>
/// <param name="CustomHeight">Custom height in mm.</param>
/// <param name="Count">Number of pages; see <see cref="Pages"/> for the clamped value.</param>
public sealed record PageSetup(string Format = "A4", bool Landscape = false, double CustomWidth = 210, double CustomHeight = 297, int Count = 1)
{
    /// <summary>Format name for a custom size.</summary>
    public const string Custom = "Custom";

    /// <summary>Smallest and largest page edge in mm (PDF allows 3 to 14 400 points, i.e. about 1 mm to 5 m).</summary>
    public const double MinMillimeters = 10, MaxMillimeters = 5000;

    /// <summary>Formats to choose from, largest A format first.</summary>
    public static readonly IReadOnlyList<string> Formats = ["A0", "A1", "A2", "A3", "A4", "A5", "A6", "Letter", "Legal"];

    /// <summary>Whether this is a custom size.</summary>
    [JsonIgnore]
    public bool IsCustom => Format == Custom;

    /// <summary>Width and height in mm as the page lies (landscape: wider than high); custom sizes clamped to the limits.</summary>
    [JsonIgnore]
    public (double Width, double Height) Millimeters
    {
        get
        {
            // A custom size applies as entered (the window swaps width and height when switching to landscape)
            if (IsCustom) return (Size(CustomWidth), Size(CustomHeight));
            var (w, h) = PageFormat.Millimeters(Format) ?? (210, 297);
            return Landscape ? (h, w) : (w, h);
        }
    }

    private static double Size(double mm) => double.IsFinite(mm) ? Math.Clamp(mm, MinMillimeters, MaxMillimeters) : 210;

    /// <summary>Width and height in points (1/72 inch).</summary>
    [JsonIgnore]
    public (double Width, double Height) Points
    {
        get
        {
            var (w, h) = Millimeters;
            return (w * PageFormat.PointsPerMillimeter, h * PageFormat.PointsPerMillimeter);
        }
    }

    /// <summary>Number of pages within the permitted limits (1 … <see cref="BlankPage.MaxCount"/>).</summary>
    [JsonIgnore]
    public int Pages => Math.Clamp(Count, 1, BlankPage.MaxCount);

    /// <summary>Replaces unknown format names (old or corrupted settings) with A4.</summary>
    /// <returns>This setup, or a copy with format A4.</returns>
    public PageSetup Checked() => IsCustom || Formats.Contains(Format) ? this : this with { Format = "A4" };

    /// <summary>
    /// Parses the short form used in the menu, e.g. "A4" or "A3 quer" (the words are fixed and not translated).
    /// </summary>
    /// <param name="text">The short form.</param>
    /// <returns>The page setup with one page; <see langword="null"/> for anything unknown.</returns>
    public static PageSetup? Parse(string? text)
    {
        if (text is null) return null;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2 || !Formats.Contains(parts[0])) return null;
        if (parts.Length == 2 && parts[1] != "quer") return null;
        return new PageSetup(parts[0], parts.Length == 2);
    }
}
