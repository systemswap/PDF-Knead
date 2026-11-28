using PdfKnead.Core.Fonts;

namespace PdfKnead.Core.Pdf.Fonts;

/// <summary>
/// The bitmaps of a glyph of an embedded font without outlines (<see cref="EmbeddedBitmaps"/>, docs/FEHLERGEDAECHTNIS.md, No. 1169): the glyph
/// is drawn from the strike whose size lies nearest to the size it is shown at, and <see cref="At"/> gives the glyph with the shape of that strike.
/// </summary>
internal sealed class GlyphBitmap
{
    private readonly EmbeddedBitmaps _bitmaps;
    private readonly int _glyph;
    private readonly PdfGlyph?[] _byStrike;
    private PdfGlyph? _basis;

    private GlyphBitmap(EmbeddedBitmaps bitmaps, int glyph)
    {
        _bitmaps = bitmaps;
        _glyph = glyph;
        _byStrike = new PdfGlyph?[bitmaps.Strikes.Count];
    }

    /// <summary>
    /// The glyph <paramref name="glyph"/> of a PDF font with the shape of its largest strike (for everything that does not know the size) and the
    /// link to its other strikes; the glyph unchanged if no strike holds an image of it.
    /// </summary>
    internal static PdfGlyph Attach(PdfGlyph basis, EmbeddedBitmaps bitmaps, int glyph)
    {
        if (bitmaps.StrikeFor(glyph, double.MaxValue) is var largest && largest < 0) return basis;
        var link = new GlyphBitmap(bitmaps, glyph);
        link._basis = basis;
        return link._byStrike[largest] = link.With(largest);
    }

    /// <summary>The glyph as drawn at an em of <paramref name="em"/> points (pixels at 72 dpi, as ppem counts them).</summary>
    internal PdfGlyph At(double em)
    {
        var strike = _bitmaps.StrikeFor(_glyph, double.IsFinite(em) ? em : double.MaxValue);
        return strike < 0 ? _basis! : _byStrike[strike] ??= With(strike);
    }

    private PdfGlyph With(int strike)
    {
        var shape = _bitmaps.Shape(_glyph, strike)!;
        return _basis! with { Outline = shape.Outline, Bitmap = this, Levels = shape.Levels };
    }
}
