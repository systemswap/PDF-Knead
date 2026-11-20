using System.Globalization;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Document;

/// <summary>Rectangle in PDF units (1/72 inch), always normalised to lower-left and upper-right corners.</summary>
/// <param name="Left">Left edge (smaller x).</param>
/// <param name="Bottom">Bottom edge (smaller y).</param>
/// <param name="Right">Right edge (larger x).</param>
/// <param name="Top">Top edge (larger y).</param>
public readonly record struct PdfRectangle(double Left, double Bottom, double Right, double Top)
{
    /// <summary>Width in PDF units.</summary>
    public double Width => Right - Left;

    /// <summary>Height in PDF units.</summary>
    public double Height => Top - Bottom;

    /// <summary>True if the width or height is zero or negative.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Creates a rectangle from an array [x1 y1 x2 y2] with the corners in any order.</summary>
    /// <param name="obj">The array object.</param>
    /// <returns>The normalised rectangle, or null if the object is not an array of at least four finite numbers
    /// or the rectangle is empty.</returns>
    public static PdfRectangle? From(PdfObject obj)
    {
        if (obj is not PdfArray { Count: >= 4 } a) return null;
        var v = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (a.GetNumber(i) is not { } n || !double.IsFinite(n)) return null;
            v[i] = n;
        }
        var r = new PdfRectangle(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
        return r.IsEmpty ? null : r;
    }

    /// <summary>Intersects this rectangle with another.</summary>
    /// <param name="other">The other rectangle.</param>
    /// <returns>The intersection, or this rectangle unchanged if the intersection is empty.</returns>
    public PdfRectangle Intersect(PdfRectangle other)
    {
        var r = new PdfRectangle(Math.Max(Left, other.Left), Math.Max(Bottom, other.Bottom), Math.Min(Right, other.Right), Math.Min(Top, other.Top));
        return r.IsEmpty ? this : r;
    }

    /// <summary>Formats the rectangle as "[left bottom right top]" with at most two decimals, culture-invariant.</summary>
    /// <returns>The formatted text.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{Left:0.##} {Bottom:0.##} {Right:0.##} {Top:0.##}]");
}

/// <summary>
/// A page with its inherited attributes (ISO 32000-2, 7.7.3.4): /Resources, /MediaBox, /CropBox and /Rotate
/// also apply when they are set only on an ancestor /Pages node.
/// </summary>
public sealed class PdfPage
{
    /// <summary>US Letter (612 × 792 pt), used when a page has no valid /MediaBox.</summary>
    public static readonly PdfRectangle DefaultMediaBox = new(0, 0, 612, 792);

    internal PdfPage(int index, PdfDictionary dictionary, PdfObjectId? id, PdfDictionary? resources,
        PdfRectangle mediaBox, PdfRectangle? cropBox, int rotate)
    {
        Index = index;
        Dictionary = dictionary;
        Id = id;
        Resources = resources;
        MediaBox = mediaBox;
        CropBox = cropBox is { } c ? c.Intersect(mediaBox) : mediaBox;
        Rotate = ((rotate % 360) + 360) % 360 / 90 * 90;
        UserUnit = dictionary.GetNumber(PdfName.UserUnit) is { } u && u > 0 && double.IsFinite(u) ? u : 1;
    }

    /// <summary>Zero-based position in the document.</summary>
    public int Index { get; }

    /// <summary>Page number, starting at 1.</summary>
    public int Number => Index + 1;

    /// <summary>The page dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>Object identifier of the page dictionary, or null if it is not an indirect object.</summary>
    public PdfObjectId? Id { get; }

    /// <summary>The resources of the page, inherited if necessary; null if there are none.</summary>
    public PdfDictionary? Resources { get; }

    /// <summary>The media box, inherited if necessary.</summary>
    public PdfRectangle MediaBox { get; }

    /// <summary>Visible area (default: the media box), clipped to the media box.</summary>
    public PdfRectangle CropBox { get; }

    /// <summary>Rotation for display: 0, 90, 180 or 270 degrees clockwise.</summary>
    /// <remarks>Other values are normalised: negative values wrap around, values that are not multiples of 90
    /// are rounded down to the next lower multiple of 90.</remarks>
    public int Rotate { get; }

    /// <summary>Size of one user space unit in multiples of 1/72 inch (/UserUnit, PDF 1.6, for very large
    /// drawings); 1 if missing or invalid.</summary>
    public double UserUnit { get; }

    /// <summary>Displayed width in points (1/72 inch), after rotation.</summary>
    public double Width => (Rotate is 90 or 270 ? CropBox.Height : CropBox.Width) * UserUnit;

    /// <summary>Displayed height in points, after rotation.</summary>
    public double Height => (Rotate is 90 or 270 ? CropBox.Width : CropBox.Height) * UserUnit;

    /// <summary>
    /// Returns the same page with a different rotation. Content and dictionary stay the same objects, so the
    /// page's display list remains valid.
    /// </summary>
    /// <param name="rotate">The new rotation in degrees; normalised as for <see cref="Rotate"/>.</param>
    /// <returns>A new page object.</returns>
    public PdfPage WithRotate(int rotate) => new(Index, Dictionary, Id, Resources, MediaBox, CropBox, rotate);

    /// <summary>The same page with another page dictionary (for entries that change neither content nor boxes, such as /VP).</summary>
    /// <param name="dictionary">The new page dictionary.</param>
    /// <returns>The page.</returns>
    public PdfPage WithDictionary(PdfDictionary dictionary) => new(Index, dictionary, Id, Resources, MediaBox, CropBox, Rotate);
}

/// <summary>Recognises paper formats ("A4 portrait", "Letter landscape") or falls back to the dimensions.</summary>
public static class PageFormat
{
    /// <summary>Points (1/72 inch) per millimetre.</summary>
    public const double PointsPerMillimeter = 72 / 25.4;

    private static readonly (string Name, double Width, double Height)[] Formats =
    [
        ("A0", 841, 1189), ("A1", 594, 841), ("A2", 420, 594), ("A3", 297, 420), ("A4", 210, 297),
        ("A5", 148, 210), ("A6", 105, 148), ("B4", 250, 353), ("B5", 176, 250),
        ("Letter", 215.9, 279.4), ("Legal", 215.9, 355.6), ("Tabloid", 279.4, 431.8),
    ];

    /// <summary>Detects a known paper format, with a tolerance of ±2 mm.</summary>
    /// <param name="widthPt">Width in points.</param>
    /// <param name="heightPt">Height in points.</param>
    /// <returns>The format name and whether it is landscape, or null if it is none of the known formats.</returns>
    public static (string Name, bool Landscape)? Detect(double widthPt, double heightPt)
    {
        var w = widthPt / PointsPerMillimeter;
        var h = heightPt / PointsPerMillimeter;
        foreach (var (name, fw, fh) in Formats)
        {
            if (Near(w, fw) && Near(h, fh)) return (name, false);
            if (Near(w, fh) && Near(h, fw)) return (name, true);
        }
        return null;
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) <= 2;

    /// <summary>Returns width and height of a known format in millimetres, in portrait orientation.</summary>
    /// <param name="name">The format name, case-insensitive (for example "A4").</param>
    /// <returns>The dimensions, or null if the name is unknown.</returns>
    public static (double Width, double Height)? Millimeters(string name)
    {
        foreach (var (n, w, h) in Formats)
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return (w, h);
        return null;
    }

    /// <summary>Describes a page size in the current UI language, such as "A4 portrait · 210 × 297 mm" or
    /// "1000 × 700 mm" for unknown formats.</summary>
    /// <param name="widthPt">Width in points.</param>
    /// <param name="heightPt">Height in points.</param>
    /// <returns>The localised description.</returns>
    public static string Describe(double widthPt, double heightPt)
    {
        var mm = Tr.T("{0} × {1} mm",
            Math.Round(widthPt / PointsPerMillimeter).ToString("0", Tr.Culture),
            Math.Round(heightPt / PointsPerMillimeter).ToString("0", Tr.Culture));
        return Detect(widthPt, heightPt) is { } f
            ? Tr.T("{0} {1} · {2}", f.Name, f.Landscape ? Tr.T("quer") : Tr.T("hoch"), mm)
            : mm;
    }
}
