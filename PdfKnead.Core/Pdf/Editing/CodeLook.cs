using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Barcodes.DataMatrix;
using PdfKnead.Core.Barcodes.Qr;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// How a code is drawn, without its content: kind and its settings, colours, quiet zone and human-readable text. Shared by inserted codes
/// (<see cref="CodeElement"/>) and code fields (<see cref="FieldSpec.Code"/>, KONZEPT.md, section 5.19, step 4), and stored in the file
/// with the same keys for both.
/// </summary>
public sealed record CodeLook
{
    private static readonly PdfName Codeart = PdfName.Get("Codeart"), Gs1Key = PdfName.Get("GS1"), Stufe = PdfName.Get("Stufe"),
        Gestalt = PdfName.Get("Gestalt"), Farbe = PdfName.Get("Farbe"), Hintergrund = PdfName.Get("Hintergrund"), Ruhezone = PdfName.Get("Ruhezone"),
        Klartext = PdfName.Get("Klartext");

    /// <summary>The kind.</summary>
    public BarcodeFormat Format { get; init; } = BarcodeFormat.Qr;

    /// <summary>QR and Data Matrix: the content is GS1 data (GS1-128 always is).</summary>
    public bool Gs1 { get; init; }

    /// <summary>QR: error correction level.</summary>
    public QrErrorCorrection Level { get; init; } = QrErrorCorrection.M;

    /// <summary>Data Matrix: square or rectangular symbol.</summary>
    public DataMatrixShape Shape { get; init; } = DataMatrixShape.Square;

    /// <summary>Colour of modules, bars and text as 0xRRGGBB.</summary>
    public int Color { get; init; }

    /// <summary>Background of the whole rectangle as 0xRRGGBB; null = transparent.</summary>
    public int? Background { get; init; } = 0xFFFFFF;

    /// <summary>Leave the quiet zone every kind needs around the code (inside the rectangle).</summary>
    public bool QuietZone { get; init; } = true;

    /// <summary>Bar codes: the human-readable text below the bars.</summary>
    public bool Caption { get; init; } = true;

    /// <summary>Whether it is a QR or Data Matrix symbol (kept square).</summary>
    public bool IsMatrix => CodeSymbol.IsMatrix(Format);

    /// <summary>The look of an inserted code.</summary>
    /// <param name="code">The code element.</param>
    /// <returns>Its look.</returns>
    public static CodeLook Of(CodeElement code) => new()
    {
        Format = code.Format, Gs1 = code.Gs1, Level = code.Level, Shape = code.Shape, Color = code.Color, Background = code.Background,
        QuietZone = code.QuietZone, Caption = code.Caption,
    };

    /// <summary>A code element with this look.</summary>
    /// <param name="code">The element to take position, size, rotation, content and alternative text from.</param>
    /// <returns>The element with this look.</returns>
    public CodeElement Apply(CodeElement code) => code with
    {
        Format = Format, Gs1 = Gs1, Level = Level, Shape = Shape, Color = Color, Background = Background, QuietZone = QuietZone, Caption = Caption,
    };

    /// <summary>Reads the look from a dictionary; unknown or damaged values become the defaults.</summary>
    /// <param name="data">The dictionary (element data or the code entry of a field).</param>
    /// <returns>The look; <see langword="null"/> if the kind is missing or unknown.</returns>
    public static CodeLook? Read(PdfDictionary data)
    {
        if (!Enum.TryParse<BarcodeFormat>(data.GetName(Codeart)?.Value, out var format) || !Enum.IsDefined(format)) return null;
        return new CodeLook
        {
            Format = format,
            Gs1 = data.GetBoolean(Gs1Key) ?? false,
            Level = Enum.TryParse<QrErrorCorrection>(data.GetName(Stufe)?.Value, out var level) && Enum.IsDefined(level) ? level : QrErrorCorrection.M,
            Shape = Enum.TryParse<DataMatrixShape>(data.GetName(Gestalt)?.Value, out var shape) && Enum.IsDefined(shape) ? shape : DataMatrixShape.Square,
            Color = (int)((data.GetInteger(Farbe) ?? 0) & 0xFFFFFF),
            Background = data.GetInteger(Hintergrund) is { } back ? (int)(back & 0xFFFFFF) : null,
            QuietZone = data.GetBoolean(Ruhezone) ?? true,
            Caption = data.GetBoolean(Klartext) ?? true,
        };
    }

    /// <summary>Writes the look into a dictionary (the content is not part of it).</summary>
    /// <param name="data">The dictionary.</param>
    public void Write(PdfDictionary data)
    {
        data.Set(Codeart, PdfName.Get(Format.ToString()));
        if (Gs1) data.Set(Gs1Key, PdfBoolean.Of(true));
        else data.Remove(Gs1Key);
        data.Set(Stufe, PdfName.Get(Level.ToString()));
        data.Set(Gestalt, PdfName.Get(Shape.ToString()));
        data.Set(Farbe, PdfInteger.Of(Color));
        if (Background is { } background) data.Set(Hintergrund, PdfInteger.Of(background));
        else data.Remove(Hintergrund);
        data.Set(Ruhezone, PdfBoolean.Of(QuietZone));
        data.Set(Klartext, PdfBoolean.Of(Caption));
    }
}
