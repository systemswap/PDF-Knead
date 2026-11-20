using PdfKnead.Core.Imaging;

namespace PdfKnead.Core.Fonts;

/// <summary>One source of colour glyph images in a font (one bitmap table); <see cref="ColorGlyphs"/> picks and caches.</summary>
internal interface IGlyphImages
{
    /// <summary>Pixels per em of the images that <see cref="Load"/> returns.</summary>
    int PixelsPerEm { get; }

    /// <summary>Decodes the image of a glyph.</summary>
    /// <param name="glyph">Glyph index, already checked against the glyph count of the font.</param>
    /// <returns>Position of the lower left corner relative to the glyph origin in px (y upwards) and the image; null if the glyph has none.</returns>
    (int X, int Y, RgbaImage Image)? Load(int glyph);
}
