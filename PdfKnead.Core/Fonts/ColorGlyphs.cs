using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Imaging;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Colour glyphs as bitmaps: one PNG image per glyph, from the first usable of the OpenType tables 'sbix' (Apple), 'CBDT'/'CBLC'
/// (Google) and 'SVG ' documents that embed a single PNG. Handwriting fonts made from scanned ink get their look only this way;
/// their outlines may be placeholders. If a font has several of these tables, the one with the highest resolution is used.
/// </summary>
/// <remarks>
/// Instances are shared per <see cref="OpenTypeFont"/> and are safe to use from several threads. Decoded glyph images are cached
/// up to 16 MB per font; when the budget is exceeded the cache starts again from empty.
/// </remarks>
public sealed class ColorGlyphs
{
    private static readonly ConditionalWeakTable<OpenTypeFont, ColorGlyphs?> All = new();

    private readonly IGlyphImages _source;
    private readonly OpenTypeFont _font;
    private readonly int _glyphCount;
    private readonly ConcurrentDictionary<int, (int X, int Y, RgbaImage Image)?> _glyphs = new();
    private long _glyphBytes;

    /// <summary>Maximum bytes of decoded glyph images (a name needs about 5 MB); beyond that the cache starts again.</summary>
    private const long GlyphBudget = 16L << 20;

    /// <summary>
    /// Pixels of one glyph image at most (2 048 × 2 048, 16 MB as RGBA — the cache budget): checked in the PNG header before decoding.
    /// Before, a glyph could decode up to the PNG limit (2²⁵ pixels, 128 MB) whatever the strike, and the cache kept it (No. 1138).
    /// </summary>
    internal const long MaxGlyphPixels = 1L << 22;

    /// <summary>Whether a PNG glyph image is within <see cref="MaxGlyphPixels"/> (and, if given, within the size its metrics allow).</summary>
    internal static bool Fits(ReadOnlySpan<byte> png, int maxSide = int.MaxValue) =>
        PngDecoder.Size(png) is { } size && size.Width <= maxSide && size.Height <= maxSide && size.Width * size.Height <= MaxGlyphPixels;

    private ColorGlyphs(IGlyphImages source, OpenTypeFont font)
    {
        _source = source;
        _font = font;
        _glyphCount = font.GlyphCount;
        PixelsPerEm = source.PixelsPerEm;
    }

    /// <summary>
    /// A glyph image of a smaller strike scaled to the strike in use (<paramref name="factor"/> = its pixels per em over theirs); null if it
    /// would exceed <see cref="MaxGlyphPixels"/>.
    /// </summary>
    internal static (int X, int Y, RgbaImage Image)? Scaled((int X, int Y, RgbaImage Image) glyph, double factor)
    {
        var width = Math.Max(1, (int)Math.Round(glyph.Image.Width * factor));
        var height = Math.Max(1, (int)Math.Round(glyph.Image.Height * factor));
        if ((long)width * height > MaxGlyphPixels) return null;
        return ((int)Math.Round(glyph.X * factor), (int)Math.Round(glyph.Y * factor), Lanczos.Resize(glyph.Image, width, height));
    }

    /// <summary>A JPEG glyph image (sbix 'jpg '), within <see cref="MaxGlyphPixels"/>; null if it cannot be decoded.</summary>
    internal static RgbaImage? Jpeg(ReadOnlySpan<byte> data)
    {
        var bytes = data.ToArray();
        try
        {
            if (Pdf.Filters.Jpeg.JpegDecoder.ReadFrame(bytes) is not { } frame || (long)frame.Width * frame.Height > MaxGlyphPixels) return null;
            var jpeg = Pdf.Filters.Jpeg.JpegDecoder.Decode(bytes);
            if (jpeg.Channels is not (1 or 3)) return null;
            var image = RgbaImage.Blank(jpeg.Width, jpeg.Height);
            for (var i = 0; i < jpeg.Width * jpeg.Height; i++)
            {
                var s = i * jpeg.Channels;
                image.Pixels[i * 4] = jpeg.Samples[s];
                image.Pixels[i * 4 + 1] = jpeg.Samples[s + (jpeg.Channels == 3 ? 1 : 0)];
                image.Pixels[i * 4 + 2] = jpeg.Samples[s + (jpeg.Channels == 3 ? 2 : 0)];
                image.Pixels[i * 4 + 3] = 255;
            }
            return image;
        }
        catch (Pdf.PdfException)
        {
            return null;
        }
    }

    /// <summary>Returns the colour glyphs of a font.</summary>
    /// <param name="font">The font.</param>
    /// <returns>The shared instance for the font, or null if the font has no usable sbix, CBDT/CBLC or SVG table with PNG images.</returns>
    public static ColorGlyphs? For(OpenTypeFont font) => All.GetValue(font, Create);

    private static ColorGlyphs? Create(OpenTypeFont font)
    {
        IGlyphImages? best = null;
        foreach (var source in (IGlyphImages?[])[SbixImages.TryCreate(font), CbdtImages.TryCreate(font), SvgPngImages.TryCreate(font)])
            if (source is not null && (best is null || source.PixelsPerEm > best.PixelsPerEm))
                best = source;
        return best is null ? null : new ColorGlyphs(best, font);
    }

    /// <summary>Pixels per em of the images in use.</summary>
    public int PixelsPerEm { get; }

    /// <summary>Returns the image of a glyph with the position of its lower left corner relative to the glyph origin.</summary>
    /// <param name="glyph">Glyph index.</param>
    /// <returns>Offset in px of the images and the image, or null if the glyph has no PNG image.</returns>
    public (int X, int Y, RgbaImage Image)? Glyph(int glyph)
    {
        if (_glyphs.TryGetValue(glyph, out var known)) return known;
        var loaded = glyph >= 0 && glyph < _glyphCount ? _source.Load(glyph) : null;
        if (loaded is { } big && big.Image.Pixels.Length > GlyphBudget) return loaded;   // larger than the budget: not kept
        if (loaded is { } l && Interlocked.Add(ref _glyphBytes, l.Image.Pixels.Length) > GlyphBudget)
        {
            // Too many different glyphs decoded: start again (callers currently using an image hold their own reference)
            _glyphs.Clear();
            Interlocked.Exchange(ref _glyphBytes, l.Image.Pixels.Length);
        }
        return _glyphs.GetOrAdd(glyph, loaded);
    }

    /// <summary>Composes shaped text into one image in pixels of the strike.</summary>
    /// <param name="glyphs">The shaped glyphs; advances and offsets in font units.</param>
    /// <param name="unitsPerEm">Units per em of the font.</param>
    /// <returns>The image, or null if no glyph has an image or the result would exceed 16 megapixels (64 MB as RGBA).</returns>
    public RgbaImage? Compose(IReadOnlyList<ShapedGlyph> glyphs, int unitsPerEm)
    {
        var k = (double)PixelsPerEm / unitsPerEm;
        var placed = new List<(double Left, double Bottom, RgbaImage Image)>();
        double pen = 0;
        var coloured = false;
        foreach (var g in glyphs)
        {
            if (Glyph(g.Glyph) is { } img)
            {
                placed.Add(((pen + g.XOffset) * k + img.X, g.YOffset * k + img.Y, img.Image));
                coloured = true;
            }
            // a glyph without a colour image is drawn with its outline (OpenType sbix 34.1, SVG 35.6) — before its place stayed empty
            // (docs/FEHLERGEDAECHTNIS.md, No. 1152)
            else if (g.Glyph >= 0 && g.Glyph < _glyphCount && OutlineRaster.Render(_font.Outline(g.Glyph), k) is { } drawn)
                placed.Add(((pen + g.XOffset) * k + drawn.X, g.YOffset * k + drawn.Y, drawn.Image));
            pen += g.XAdvance;
        }
        if (!coloured) return null;
        var left = placed.Min(p => p.Left);
        var right = placed.Max(p => p.Left + p.Image.Width);
        var bottom = placed.Min(p => p.Bottom);
        var top = placed.Max(p => p.Bottom + p.Image.Height);
        var width = (int)Math.Ceiling(right - left);
        var height = (int)Math.Ceiling(top - bottom);
        if (width <= 0 || height <= 0 || (long)width * height > 16L << 20) return null;   // 64 MB as RGBA
        var canvas = RgbaImage.Blank(width, height);
        foreach (var (l, b, image) in placed)
            canvas.Draw(image, (int)Math.Round(l - left), (int)Math.Round(top - (b + image.Height)));
        return canvas;
    }
}
