using PdfKnead.Core.Imaging;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Images of the COLR version 0 colour glyphs of a font (<see cref="ColrLayers"/>) for the image signature: the layers filled into one image at
/// <see cref="Ppem"/> pixels per em, bottom layer first, each in its palette colour — the layers in the text colour black, the colour the ink image
/// starts from (docs/FEHLERGEDAECHTNIS.md, No. 1168).
/// </summary>
internal sealed class ColrImages : IGlyphImages
{
    /// <summary>Pixels per em of the images: about the size of the strikes of bitmap colour fonts, sharp enough for a signature.</summary>
    internal const int Ppem = 160;

    /// <summary>
    /// Pixels filled per glyph at most, over all its layers (eight images of the largest size <see cref="ColorGlyphs.MaxGlyphPixels"/>): with
    /// <see cref="ColrLayers.MaxLayers"/> large layers a glyph would otherwise fill gigapixels.
    /// </summary>
    internal const long MaxPixelsPerGlyph = 8 * ColorGlyphs.MaxGlyphPixels;

    private readonly OpenTypeFont _font;
    private readonly ColrLayers _layers;

    private ColrImages(OpenTypeFont font, ColrLayers layers)
    {
        _font = font;
        _layers = layers;
    }

    /// <inheritdoc/>
    public int PixelsPerEm => Ppem;

    /// <summary>Reads the colour glyphs of the font.</summary>
    /// <param name="font">The font.</param>
    /// <returns>The source, or null if the font has no usable COLR version 0 records with CPAL.</returns>
    public static ColrImages? TryCreate(OpenTypeFont font) => ColrLayers.For(font) is { } layers ? new ColrImages(font, layers) : null;

    /// <inheritdoc/>
    public (int X, int Y, RgbaImage Image)? Load(int glyph)
    {
        if (_layers.Layers(glyph) is not { } layers) return null;
        var k = (double)Ppem / Math.Max(1, _font.UnitsPerEm);
        var drawn = new List<(int X, int Y, RgbaImage Image, ColrLayer Layer)>(layers.Count);
        long pixels = 0;
        foreach (var layer in layers)
        {
            if (layer.Alpha == 0 || OutlineRaster.Render(_font.Outline(layer.Glyph), k) is not { } image) continue;
            pixels += (long)image.Image.Width * image.Image.Height;
            if (pixels > MaxPixelsPerGlyph) return null;
            drawn.Add((image.X, image.Y, image.Image, layer));
        }
        if (drawn.Count == 0) return null;
        int left = drawn.Min(d => d.X), bottom = drawn.Min(d => d.Y);
        int right = drawn.Max(d => d.X + d.Image.Width), top = drawn.Max(d => d.Y + d.Image.Height);
        if ((long)(right - left) * (top - bottom) > ColorGlyphs.MaxGlyphPixels) return null;
        var canvas = RgbaImage.Blank(right - left, top - bottom);
        foreach (var (x, y, image, layer) in drawn)
        {
            // the coverage of the outline (alpha of the opaque black image) times the alpha of the palette entry, in its colour
            var rgb = layer.Rgb ?? 0;
            for (var i = 0; i < image.Pixels.Length; i += 4)
            {
                image.Pixels[i] = (byte)(rgb >> 16);
                image.Pixels[i + 1] = (byte)(rgb >> 8);
                image.Pixels[i + 2] = (byte)rgb;
                image.Pixels[i + 3] = (byte)((image.Pixels[i + 3] * layer.Alpha + 127) / 255);
            }
            canvas.Draw(image, x - left, top - (y + image.Height));
        }
        return (left, bottom, canvas);
    }
}
