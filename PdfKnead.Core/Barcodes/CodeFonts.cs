using PdfKnead.Core.Barcodes.Linear;

namespace PdfKnead.Core.Barcodes;

/// <summary>
/// Fonts that draw codes (bar code fonts): text set in them is not edited as text but through the code tool, which makes the code anew with
/// its check digit and quiet zone (KONZEPT.md, sections 5.14 and 5.19; phase 8.2). Told by the font name; symbol fonts (Symbol, dingbats)
/// are a different matter and stay locked.
/// </summary>
public static class CodeFonts
{
    /// <summary>
    /// Words in the name of a code font, with the kind they suggest (the first that matches decides). Not „ean“, „itf“ or „upc“ alone: they occur
    /// in ordinary names („Ocean“; the Thai fonts of Windows are called „AngsanaUPC“, „BrowalliaUPC“, „CordiaUPC“ … — No. 607).
    /// </summary>
    private static readonly (string Word, BarcodeFormat Format)[] Words =
    [
        ("code39", BarcodeFormat.Code39), ("code 39", BarcodeFormat.Code39), ("code-39", BarcodeFormat.Code39), ("3of9", BarcodeFormat.Code39),
        ("3 of 9", BarcodeFormat.Code39), ("code128", BarcodeFormat.Code128), ("code 128", BarcodeFormat.Code128), ("code-128", BarcodeFormat.Code128),
        ("ean13", BarcodeFormat.Ean13), ("ean-13", BarcodeFormat.Ean13), ("ean 13", BarcodeFormat.Ean13), ("ean8", BarcodeFormat.Ean8),
        ("ean-8", BarcodeFormat.Ean8), ("ean 8", BarcodeFormat.Ean8), ("upc-a", BarcodeFormat.UpcA), ("upc a", BarcodeFormat.UpcA),
        ("upc-e", BarcodeFormat.UpcE), ("upc e", BarcodeFormat.UpcE),
        ("interleaved", BarcodeFormat.Itf), ("2of5", BarcodeFormat.Itf), ("2 of 5", BarcodeFormat.Itf),
        ("qrcode", BarcodeFormat.Qr), ("qr code", BarcodeFormat.Qr), ("datamatrix", BarcodeFormat.DataMatrix), ("data matrix", BarcodeFormat.DataMatrix),
        ("barcode", BarcodeFormat.Code128), ("bar code", BarcodeFormat.Code128),
    ];

    /// <summary>The characters of Code 39 (ISO/IEC 16388) besides the start and stop character „*“.</summary>
    private const string Code39Characters = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";

    /// <summary>Determines whether a font draws codes.</summary>
    /// <param name="fontName">The font name (without subset prefix).</param>
    /// <returns>True for a bar code font.</returns>
    public static bool IsCodeFont(string fontName) => FormatOf(fontName) is not null;

    /// <summary>The kind of code a font suggests by its name.</summary>
    /// <param name="fontName">The font name (without subset prefix).</param>
    /// <returns>The kind; Code 128 for a bar code font that names none; null if the font draws no codes.</returns>
    public static BarcodeFormat? FormatOf(string fontName)
    {
        var lower = fontName.ToLowerInvariant();
        // PDF417 cannot be made anew here (no encoder): such a font stays a locked symbol font (No. 611)
        if (lower.Contains("pdf417", StringComparison.Ordinal)) return null;
        foreach (var (word, format) in Words)
            if (lower.Contains(word, StringComparison.Ordinal)) return format;
        return null;
    }

    /// <summary>
    /// The content of a code set in a code font, from its text where that is unambiguous: Code 39 between „*“ … „*“ (or only its characters),
    /// EAN-13, EAN-8 and UPC-A from their digits — with the check digit, which must be right, or without it. Code 128 fonts encode start, stop
    /// and check characters differently per maker: their content comes only from reading the code.
    /// </summary>
    /// <param name="format">The kind of code (<see cref="FormatOf"/>).</param>
    /// <param name="text">The text of the block.</param>
    /// <returns>The content as the code tool takes it; null if it cannot be told from the text.</returns>
    public static string? Derive(BarcodeFormat format, string text)
    {
        var t = text.Replace("\n", "", StringComparison.Ordinal).Trim();
        switch (format)
        {
            case BarcodeFormat.Code39:
            {
                if (t.Length >= 3 && t[0] == '*' && t[^1] == '*') t = t[1..^1];
                return t.Length > 0 && t.All(c => Code39Characters.Contains(c, StringComparison.Ordinal)) ? t : null;
            }
            case BarcodeFormat.Ean13 or BarcodeFormat.Ean8 or BarcodeFormat.UpcA:
            {
                // the digits only: EAN fonts set the guard bars as other characters
                var digits = new string(t.Where(char.IsAsciiDigit).ToArray());
                var full = format switch { BarcodeFormat.Ean13 => 13, BarcodeFormat.Ean8 => 8, _ => 12 };
                if (digits.Length == full - 1) return digits;
                if (digits.Length != full) return null;
                return LinearCodes.Gs1CheckDigit(digits.AsSpan(0, full - 1)) == digits[^1] - '0' ? digits : null;
            }
            default:
                return null;
        }
    }
}
