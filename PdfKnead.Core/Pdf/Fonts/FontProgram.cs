using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Fonts;

/// <summary>
/// The font program behind a PDF font: embedded (FontFile, FontFile2, FontFile3)
/// or a substitute. The format is detected from the data, not from the key, because many PDFs
/// declare the wrong type.
/// </summary>
internal sealed class FontProgram
{
    private FontProgram(OpenTypeFont? openType, CffFont? cff, Type1Font? type1, bool embedded, bool system = false)
    {
        IsSystem = system;
        OpenType = openType;
        Cff = cff ?? openType?.Cff;
        Type1 = type1;
        IsEmbedded = embedded;
    }

    /// <summary>The OpenType/TrueType font, if the program is one.</summary>
    public OpenTypeFont? OpenType { get; }

    /// <summary>CFF, either standalone or taken from an OpenType font.</summary>
    public CffFont? Cff { get; }

    /// <summary>The Type 1 font, if the program is one.</summary>
    public Type1Font? Type1 { get; }

    /// <summary>True if the program comes from the PDF file.</summary>
    public bool IsEmbedded { get; }

    /// <summary>Font installed on the computer with the name of the PDF font (its glyph IDs are used as if embedded).</summary>
    public bool IsSystem { get; }

    /// <summary>TrueType outlines (glyf), not CFF.</summary>
    public bool IsTrueType => OpenType is not null && OpenType.Cff is null;

    /// <summary>Font matrix of the program; 0.001 scaling if none is known.</summary>
    public Matrix FontMatrix => OpenType?.FontMatrix ?? Cff?.FontMatrix ?? Type1?.FontMatrix ?? new Matrix(0.001, 0, 0, 0.001, 0, 0);

    /// <summary>Embedded font from the FontDescriptor; null if there is none or it is unreadable.</summary>
    public static FontProgram? Embedded(PdfDictionary? descriptor, PdfDocument doc)
    {
        if (descriptor is null) return null;
        foreach (var key in (ReadOnlySpan<string>)["FontFile2", "FontFile3", "FontFile"])
        {
            if (descriptor.GetStream(PdfName.Get(key)) is not { } stream) continue;
            byte[] data;
            try
            {
                // the decoded length is usually given (TrueType: Length1, Type 1: Length1–3), which avoids copying
                var lengths = stream.Dictionary;
                var hint = key switch
                {
                    "FontFile2" => lengths.GetInteger(PdfName.Get("Length1")) ?? 0,
                    "FontFile" => (lengths.GetInteger(PdfName.Get("Length1")) ?? 0) + (lengths.GetInteger(PdfName.Get("Length2")) ?? 0)
                                  + (lengths.GetInteger(PdfName.Get("Length3")) ?? 0),
                    _ => 0,
                };
                // (capped: broken files sometimes give nonsense; fonts compress to about half their size)
                data = doc.DecodeStream(stream, (int)Math.Clamp(hint, 0, Math.Min(64L << 20, (stream.RawData?.Length ?? stream.DataLength) * 16L + 4096))).Data;
            }
            catch (PdfException)
            {
                continue;
            }
            if (FromBytes(data) is { } program) return program;
        }
        return null;
    }

    /// <summary>Detects the format from the data and reads it; null if the format is unknown or the font is broken.</summary>
    public static FontProgram? FromBytes(byte[] data, bool embedded = true)
    {
        if (data.Length < 4) return null;
        try
        {
            // broken header: treat as not embedded (substitute by name), as common PDF readers do
            if (OpenTypeFont.LooksLikeSfnt(data)) return OpenTypeFont.Parse(data) is { Broken: false } ot ? new FontProgram(ot, null, null, embedded) : null;
            // CFF: major version 1 with any minor version (Adobe TN 5176, 6: a higher minor version stays readable — before only 1.0, and a
            // CFF 1.1 counted as not embedded, No. 1148), header size 4 (or at least plausibly small)
            if (data[0] == 1 && data[2] is >= 4 and <= 16) return new FontProgram(null, CffFont.Parse(data), null, embedded);
            if (data[0] == 0x80 || data.AsSpan(0, Math.Min(data.Length, 1024)).IndexOf("%!"u8) >= 0 || data.AsSpan().IndexOf("eexec"u8) >= 0)
                return new FontProgram(null, null, Type1Font.Parse(data), embedded);
        }
        catch (Exception e) when (e is FontFormatException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
        }
        return null;
    }

    /// <summary>Wraps a font installed on the computer.</summary>
    public static FontProgram System(OpenTypeFont font) => new(font, null, null, embedded: false, system: true);

    /// <summary>Substitute from an already loaded font (Noto CJK); glyphs are found through the text.</summary>
    public static FontProgram Substitute(OpenTypeFont font) => new(font, null, null, embedded: false);

    /// <summary>Substitute from one of the bundled standard fonts (a .cff or .ttf file name in Schriften/Standard).</summary>
    public static FontProgram Substitute(string file) =>
        file.EndsWith(".cff", StringComparison.Ordinal)
            ? new FontProgram(null, EmbeddedFonts.StandardCff(file), null, embedded: false)
            : new FontProgram(EmbeddedFonts.StandardOpenType(file), null, null, embedded: false);
}

/// <summary>
/// Chooses the substitute for fonts that are not embedded (ISO 32000-2, 9.6.2.2 and 9.8)
/// by name and FontDescriptor (flags, weight, italic angle).
/// </summary>
internal static class StandardFonts
{
    /// <summary>File in Schriften/Standard, and whether the font is Symbol or ZapfDingbats.</summary>
    public static (string File, bool Symbol, bool Dingbats) Choose(string baseFont, PdfDictionary? descriptor)
    {
        var name = baseFont.Contains('+') && baseFont.IndexOf('+') == 6 ? baseFont[7..] : baseFont;   // subset "ABCDEF+Name"
        var lower = name.ToLowerInvariant();
        var flags = (int)(descriptor?.GetInteger(PdfName.Get("Flags")) ?? 0);

        if (lower.Contains("dingbat") || lower.Contains("zapf")) return ("FoxitDingbats.cff", false, true);
        if (lower is "symbol" || lower.StartsWith("symbol,", StringComparison.Ordinal) || lower.StartsWith("symbolmt", StringComparison.Ordinal))
            return ("FoxitSymbol.cff", true, false);

        var family = lower.Contains("courier") || lower.Contains("mono") || lower.Contains("consol") || (flags & 1) != 0 ? "Mono"
            : lower.Contains("times") || lower.Contains("serif") && !lower.Contains("sans") || lower.Contains("roman") || lower.Contains("georgia")
              || lower.Contains("garamond") || lower.Contains("cambria") || ((flags & 2) != 0 && !lower.Contains("sans")) ? "Serif"
            : "Sans";

        var weight = descriptor?.GetNumber(PdfName.Get("FontWeight")) ?? 0;
        var bold = lower.Contains("bold") || lower.Contains("black") || lower.Contains("heavy") || lower.Contains("semibold") || lower.Contains("demi")
                   || weight >= 600 || (flags & (1 << 18)) != 0;
        var italicAngle = descriptor?.GetNumber(PdfName.Get("ItalicAngle")) ?? 0;
        var italic = lower.Contains("italic") || lower.Contains("oblique") || Math.Abs(italicAngle) > 1 || (flags & (1 << 6)) != 0;

        var style = (bold, italic) switch
        {
            (true, true) => "BoldItalic",
            (true, false) => "Bold",
            (false, true) => "Italic",
            _ => "Regular",
        };
        return ($"Liberation{family}-{style}.ttf", false, false);
    }

    /// <summary>
    /// Whether the name is exactly one of the 14 standard fonts (ISO 32000-2, 9.6.2.2), after a subset tag. Their metrics are the standard
    /// ones, in whole thousandths of the em, as other readers set them; the bundled substitute is metrically the same only to its own grid
    /// (Liberation: 569/2048 = 277.83 for 278), so its widths are rounded to the standard ones. Not Arial or Times New Roman: their own
    /// widths have fractions, and the substitute has them exactly. See docs/FEHLERGEDAECHTNIS.md, No. 884.
    /// </summary>
    /// <param name="baseFont">The /BaseFont of the font.</param>
    /// <returns>True for Times, Helvetica and Courier with their three styles, Symbol and ZapfDingbats.</returns>
    public static bool HasStandardMetrics(string baseFont)
    {
        var name = baseFont.Contains('+') && baseFont.IndexOf('+') == 6 ? baseFont[7..] : baseFont;
        return name is "Times-Roman" or "Times-Bold" or "Times-Italic" or "Times-BoldItalic" or "Helvetica" or "Helvetica-Bold"
            or "Helvetica-Oblique" or "Helvetica-BoldOblique" or "Courier" or "Courier-Bold" or "Courier-Oblique" or "Courier-BoldOblique"
            or "Symbol" or "ZapfDingbats";
    }

    /// <summary>Whether the font is one of the standard 14 (or Arial) whose widths match its substitute, so it need not be stretched.</summary>
    public static bool IsStandard14(string baseFont)
    {
        var name = baseFont.Contains('+') && baseFont.IndexOf('+') == 6 ? baseFont[7..] : baseFont;
        return name.StartsWith("Helvetica", StringComparison.Ordinal) || name.StartsWith("Times", StringComparison.Ordinal)
               || name.StartsWith("Courier", StringComparison.Ordinal) || name.StartsWith("Arial", StringComparison.Ordinal)
               || name is "Symbol" or "ZapfDingbats";
    }
}
