using PdfKnead.Core.Barcodes.DataMatrix;
using PdfKnead.Core.Barcodes.Linear;
using PdfKnead.Core.Barcodes.Qr;

namespace PdfKnead.Core.Barcodes;

/// <summary>A line of human-readable text below a bar code, centred between two module positions (counted from the first bar).</summary>
/// <param name="Text">The characters.</param>
/// <param name="From">Left end in modules; negative inside the left quiet zone.</param>
/// <param name="To">Right end in modules.</param>
public sealed record CodeCaption(string Text, double From, double To);

/// <summary>
/// A code ready to be drawn (KONZEPT.md, section 5.19): the modules of a 2D symbol or the bars of a bar code, the quiet zone each kind
/// needs, the bars that reach down between the digits (EAN/UPC guard patterns) and the human-readable text with its place. Built from the
/// content with the own encoders, and read back by the own decoders: <see cref="Encoded"/> is what a reader gets from it.
/// </summary>
public sealed class CodeSymbol
{
    private CodeSymbol(BarcodeFormat format, BitMatrix? matrix, bool[]? bars, int quietLeft, int quietRight, IReadOnlyList<(int Start, int Length)> longBars,
        IReadOnlyList<CodeCaption> captions, string encoded)
    {
        Format = format;
        Matrix = matrix;
        Bars = bars;
        QuietLeft = quietLeft;
        QuietRight = quietRight;
        LongBars = longBars;
        Captions = captions;
        Encoded = encoded;
    }

    /// <summary>The kind.</summary>
    public BarcodeFormat Format { get; }

    /// <summary>Modules of a QR or Data Matrix symbol; null for bar codes.</summary>
    public BitMatrix? Matrix { get; }

    /// <summary>Modules of a bar code from the first to the last bar (true = bar); null for 2D symbols.</summary>
    public bool[]? Bars { get; }

    /// <summary>Whether it is a bar code (one row of bars).</summary>
    public bool IsLinear => Bars is not null;

    /// <summary>Quiet zone in modules on the left (2D symbols: on every side).</summary>
    public int QuietLeft { get; }

    /// <summary>Quiet zone in modules on the right.</summary>
    public int QuietRight { get; }

    /// <summary>Runs of modules whose bars reach down between the digits (EAN/UPC guard patterns; UPC-A also its first and last digit).</summary>
    public IReadOnlyList<(int Start, int Length)> LongBars { get; }

    /// <summary>Human-readable text below a bar code.</summary>
    public IReadOnlyList<CodeCaption> Captions { get; }

    /// <summary>What a reader gets from the code (EAN/UPC with check digit, GS1 group separators as U+001D).</summary>
    public string Encoded { get; }

    /// <summary>
    /// Bearer bars at the top and bottom of the bars: ITF-14 (ITF with fourteen digits, the carrier of a GTIN-14). GS1 makes them mandatory
    /// unless technically not feasible; printing without plates needs only these two, at least twice the narrow bar thick, butting against
    /// the bars (GS1 General Specifications, 5.3.2.4; docs/FEHLERGEDAECHTNIS.md, No. 608).
    /// </summary>
    public bool BearerBars => Format == BarcodeFormat.Itf && Encoded.Length == 14;

    /// <summary>Modules across, quiet zones included.</summary>
    public int ModulesAcross => (Matrix?.Width ?? Bars!.Length) + QuietLeft + QuietRight;

    /// <summary>Modules down, quiet zones included (2D symbols; 0 for bar codes).</summary>
    public int ModulesDown => Matrix is null ? 0 : Matrix.Height + 2 * QuietLeft;

    /// <summary>The name of a kind for the user.</summary>
    /// <param name="format">The kind.</param>
    /// <returns>„QR-Code“, „EAN-13“ …</returns>
    public static string Name(BarcodeFormat format) => format switch
    {
        BarcodeFormat.Qr => Tr.T("QR-Code"),
        BarcodeFormat.DataMatrix => "Data Matrix",
        BarcodeFormat.Ean13 => "EAN-13",
        BarcodeFormat.Ean8 => "EAN-8",
        BarcodeFormat.UpcA => "UPC-A",
        BarcodeFormat.UpcE => "UPC-E",
        BarcodeFormat.Code128 => "Code 128",
        BarcodeFormat.Gs1_128 => "GS1-128",
        BarcodeFormat.Code39 => "Code 39",
        _ => "Interleaved 2 of 5 (ITF)",
    };

    /// <summary>GS1 data read from a code (element string, group separators as U+001D) as typed: identifiers in brackets.</summary>
    /// <param name="elements">The element string.</param>
    /// <returns>„(01)…(10)…“; the string itself if it cannot be split into known identifiers.</returns>
    public static string Gs1Brackets(string elements) => Gs1Elements.ToBracketed(elements);

    /// <summary>Whether the kind is a 2D symbol.</summary>
    /// <param name="format">The kind.</param>
    /// <returns>True for QR and Data Matrix.</returns>
    public static bool IsMatrix(BarcodeFormat format) => format is BarcodeFormat.Qr or BarcodeFormat.DataMatrix;

    /// <summary>Whether the content is GS1 data written with identifiers in brackets.</summary>
    /// <param name="format">The kind.</param>
    /// <param name="gs1">GS1 chosen for a 2D symbol.</param>
    /// <returns>True for GS1-128, and for QR or Data Matrix with GS1.</returns>
    public static bool UsesGs1(BarcodeFormat format, bool gs1) => format == BarcodeFormat.Gs1_128 || gs1 && IsMatrix(format);

    /// <summary>
    /// Smallest module size in millimetres that common scanners read reliably: 0.25 mm for QR and Data Matrix, 80 % of the nominal
    /// 0.33 mm for EAN/UPC (ISO/IEC 15420, magnification 0.8), 0.19 mm for the other bar codes (GS1 General Specifications, 5.12).
    /// </summary>
    /// <param name="format">The kind.</param>
    /// <returns>The size in millimetres.</returns>
    public static double MinimumModuleMm(BarcodeFormat format) => format switch
    {
        BarcodeFormat.Qr or BarcodeFormat.DataMatrix => 0.25,
        BarcodeFormat.Ean13 or BarcodeFormat.Ean8 or BarcodeFormat.UpcA or BarcodeFormat.UpcE => 0.264,
        _ => 0.19,
    };

    /// <summary>Module size in millimetres for a newly placed code: 0.5 mm for 2D symbols, 0.33 mm (EAN nominal) for bar codes.</summary>
    /// <param name="format">The kind.</param>
    /// <returns>The size in millimetres.</returns>
    public static double NominalModuleMm(BarcodeFormat format) => IsMatrix(format) ? 0.5 : 0.33;

    /// <summary>
    /// Longest content of a bar code in characters. Scanners at tills and in stores expect short codes (GS1 allows 48 data characters in
    /// GS1-128); and a bar code of thousands of characters — possible in a crafted file — would have tens of thousands of bars thinner than
    /// a pixel, which take a minute to draw.
    /// </summary>
    public const int MaxLinearLength = 80;

    /// <summary>Encodes a content.</summary>
    /// <param name="format">The kind.</param>
    /// <param name="text">The content; GS1 data with identifiers in brackets, EAN/UPC digits with or without check digit.</param>
    /// <param name="gs1">QR and Data Matrix: GS1 data.</param>
    /// <param name="level">QR: error correction level.</param>
    /// <param name="shape">Data Matrix: square or rectangular.</param>
    /// <returns>The symbol.</returns>
    /// <exception cref="ArgumentException">The content does not fit the kind (message for the user).</exception>
    public static CodeSymbol Create(BarcodeFormat format, string text, bool gs1 = false, QrErrorCorrection level = QrErrorCorrection.M,
        DataMatrixShape shape = DataMatrixShape.Square)
    {
        if (text.Length == 0) throw new ArgumentException(Tr.T("Bitte den Inhalt eingeben."));
        var useGs1 = UsesGs1(format, gs1);
        var content = useGs1 ? Gs1Elements.FromBracketed(text) : text;
        switch (format)
        {
            case BarcodeFormat.Qr:
            {
                var matrix = QrEncoder.Encode(content, level, gs1: useGs1);
                var read = QrDecoder.Decode(matrix)?.Text ?? throw new ArgumentException(Tr.T("Der Code ließ sich nicht erzeugen."));
                return new CodeSymbol(format, matrix, null, 4, 4, [], [], read);
            }
            case BarcodeFormat.DataMatrix:
            {
                var matrix = DataMatrixCodec.Encode(content, shape, useGs1);
                var read = DataMatrixCodec.Decode(matrix)?.Text ?? throw new ArgumentException(Tr.T("Der Code ließ sich nicht erzeugen."));
                return new CodeSymbol(format, matrix, null, 1, 1, [], [], read);
            }
        }
        var symbology = format switch
        {
            BarcodeFormat.Ean13 => LinearSymbology.Ean13,
            BarcodeFormat.Ean8 => LinearSymbology.Ean8,
            BarcodeFormat.UpcA => LinearSymbology.UpcA,
            BarcodeFormat.UpcE => LinearSymbology.UpcE,
            BarcodeFormat.Code128 => LinearSymbology.Code128,
            BarcodeFormat.Gs1_128 => LinearSymbology.Gs1_128,
            BarcodeFormat.Code39 => LinearSymbology.Code39,
            _ => LinearSymbology.Itf,
        };
        if (content.Length > MaxLinearLength) throw new ArgumentException(Tr.T("Strichcodes fassen höchstens {0} Zeichen — für längere Inhalte einen QR-Code nehmen.", MaxLinearLength));
        var bars = LinearCodes.Encode(symbology, content);
        var decoded = LinearCodes.Decode(Runs(bars))?.Text ?? throw new ArgumentException(Tr.T("Der Code ließ sich nicht erzeugen."));
        var n = bars.Length;
        // EAN/UPC (ISO/IEC 15420, 5.2.5 and figures 5–8): quiet zones, guard bars reaching down, the digits in groups
        switch (format)
        {
            case BarcodeFormat.Ean13:
                return new CodeSymbol(format, null, bars, 11, 7, [(0, 3), (45, 5), (92, 3)],
                    [new(decoded[..1], -10, -1), new(decoded[1..7], 3, 45), new(decoded[7..], 50, 92)], decoded);
            case BarcodeFormat.Ean8:
                return new CodeSymbol(format, null, bars, 7, 7, [(0, 3), (31, 5), (64, 3)],
                    [new(decoded[..4], 3, 31), new(decoded[4..], 36, 64)], decoded);
            case BarcodeFormat.UpcA:
            {
                // the reader gives UPC-A as 12 digits (or as an EAN-13 with a leading 0)
                var digits = decoded.Length == 13 ? decoded[1..] : decoded;
                return new CodeSymbol(format, null, bars, 9, 9, [(0, 10), (45, 5), (85, 10)],
                    [new(digits[..1], -8, -1), new(digits[1..6], 10, 45), new(digits[6..11], 50, 85), new(digits[11..], n + 1, n + 8)], digits);
            }
            case BarcodeFormat.UpcE:
                return new CodeSymbol(format, null, bars, 9, 7, [(0, 3), (45, 6)],
                    [new(decoded[..1], -8, -1), new(decoded[1..7], 3, 45), new(decoded[7..], n + 1, n + 7)], decoded);
        }
        var shown = format == BarcodeFormat.Gs1_128 ? Gs1Elements.ToBracketed(decoded) : decoded;
        return new CodeSymbol(format, null, bars, 10, 10, [], [new(shown, 0, n)], decoded);
    }

    /// <summary>Widths along a line through the bars, with quiet zones (light first and last), for the decoder.</summary>
    private static double[] Runs(bool[] bars)
    {
        var runs = new List<double> { 12 };
        var i = 0;
        while (i < bars.Length)
        {
            var start = i;
            while (i < bars.Length && bars[i] == bars[start]) i++;
            runs.Add(i - start);
        }
        runs.Add(12);
        return [.. runs];
    }
}
