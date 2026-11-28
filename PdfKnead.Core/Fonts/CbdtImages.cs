using PdfKnead.Core.Imaging;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Images from the 'CBLC'/'CBDT' tables (OpenType 1.9, "CBLC — Color Bitmap Location Table" and "CBDT — Color Bitmap Data
/// Table"): per size one BitmapSize record with index subtables (formats 1 to 5) that point to PNG images (formats 17, 18, 19) or to
/// uncompressed BGRA images (the EBDT formats 1, 2, 5, 6, 7 with 32 bits per pixel, 5.2). A glyph takes its image from the largest 32-bit
/// strike that has one, scaled to the largest.
/// </summary>
/// <remarks>
/// Every offset is checked against its table, so a damaged or prepared font only loses glyph images and never reads outside the file.
/// </remarks>
internal sealed class CbdtImages : IGlyphImages
{
    private readonly byte[] _data;
    private readonly BitmapLocation.Tables _tables;
    private readonly BitmapLocation.Strike[] _strikes;   // largest first

    private CbdtImages(byte[] data, (int Offset, int Length) location, (int Offset, int Length) bitmaps, BitmapLocation.Strike[] strikes)
    {
        _data = data;
        _tables = new BitmapLocation.Tables(data, location.Offset, location.Offset + location.Length, bitmaps.Offset, bitmaps.Offset + bitmaps.Length);
        _strikes = strikes;
        PixelsPerEm = strikes[0].Ppem;
    }

    /// <inheritdoc/>
    public int PixelsPerEm { get; }

    /// <summary>Reads the BitmapSize records of the font.</summary>
    /// <param name="font">The font.</param>
    /// <returns>The source, or null if the font has no usable CBLC and CBDT tables.</returns>
    public static CbdtImages? TryCreate(OpenTypeFont font)
    {
        if (font.Table("CBLC") is not { Length: >= 8 } location || font.Table("CBDT") is not { Length: >= 4 } bitmaps) return null;
        // only colour strikes (32 bits per pixel), read as in the EBLC, whose structure the CBLC takes over (OpenType 6; No. 1169)
        var found = BitmapLocation.Strikes(font.FileData, location, depth => depth == 32);
        // every colour strike, largest first — before only the largest, and a glyph it does not hold (or holds in a format not read) had no
        // image although a smaller strike had one (5.2, 5.6; docs/FEHLERGEDAECHTNIS.md, No. 1152)
        return found is not { Count: > 0 } ? null : new CbdtImages(font.FileData, location, bitmaps, [.. found.OrderByDescending(s => s.Ppem)]);
    }

    /// <inheritdoc/>
    public (int X, int Y, RgbaImage Image)? Load(int glyph)
    {
        foreach (var strike in _strikes)
        {
            if (BitmapLocation.Find(_tables, strike, glyph) is not { } located || Image(located, strike.Flags) is not { } image) continue;
            var (ppem, ppemX) = (strike.Ppem, strike.PpemX);
            // pixels of another width than height (ppemX ≠ ppemY, 14.1): the image widened or narrowed to square pixels of the strike's ppemY
            if (ppemX != ppem && Stretched(image, (double)ppem / ppemX) is { } square) image = square;
            else if (ppemX != ppem) continue;
            return ppem == PixelsPerEm ? image : ColorGlyphs.Scaled(image, (double)PixelsPerEm / ppem);
        }
        return null;
    }

    /// <summary>A glyph image stretched horizontally by <paramref name="factor"/>; null if it would exceed the pixel limit.</summary>
    private static (int X, int Y, RgbaImage Image)? Stretched((int X, int Y, RgbaImage Image) glyph, double factor)
    {
        var width = Math.Max(1, (int)Math.Round(glyph.Image.Width * factor));
        if ((long)width * glyph.Image.Height > ColorGlyphs.MaxGlyphPixels) return null;
        return ((int)Math.Round(glyph.X * factor), glyph.Y, Lanczos.Resize(glyph.Image, width, glyph.Image.Height));
    }

    /// <summary>Decodes the image data of a glyph that <see cref="BitmapLocation.Find"/> located (the index subtable formats 1–5, 14.3).</summary>
    private (int X, int Y, RgbaImage Image)? Image(BitmapLocation.Located located, int flags)
    {
        var t = new Layout.TableData(_data);
        var imageFormat = located.ImageFormat;
        // small glyph metrics (image formats 1, 2, 17) of a strike whose flags name them vertical only (0x02 without 0x01) give no place in
        // horizontal text: no image from this strike, the glyph shows its outline or the image of another strike (14.1; before read as
        // horizontal ones, No. 1160)
        if ((flags & 3) == 2 && imageFormat is 1 or 2 or 17) return null;
        var (p, length, metrics) = (located.At, located.Length, located.Metrics);   // metrics: bigGlyphMetrics of formats 2 and 5, for image format 19
        var end = p + length;
        int bearingX, bearingY, png, metricHeight, metricWidth;
        switch (imageFormat)
        {
            // uncompressed BGRA, premultiplied (5.2: the EBDT formats with 32 bits per pixel, where bit and byte alignment agree): small
            // metrics before the data (1, 2), big ones (6, 7), or those of the index subtable (5) — before not read (No. 1152)
            case 1 or 2 when length >= 5:
                return Bgra(_data[p + 1], _data[p], (sbyte)_data[p + 2], (sbyte)_data[p + 3], p + 5, end);
            case 6 or 7 when length >= 8:
                return Bgra(_data[p + 1], _data[p], (sbyte)_data[p + 2], (sbyte)_data[p + 3], p + 8, end);
            case 5 when metrics >= 0:
                return Bgra(_data[metrics + 1], _data[metrics], (sbyte)_data[metrics + 2], (sbyte)_data[metrics + 3], p, end);
            // the metrics only when the image data hold them: read before, they lay behind the file when the table ended there (No. 1138)
            case 17 when length >= 5:   // smallGlyphMetrics: height, width, bearingX, bearingY, advance
                (metricHeight, metricWidth, bearingX, bearingY, png) = (_data[p], _data[p + 1], (sbyte)_data[p + 2], (sbyte)_data[p + 3], p + 5);
                break;
            case 18 when length >= 8:   // bigGlyphMetrics: height, width, horiBearingX, horiBearingY, horiAdvance, vertical metrics
                (metricHeight, metricWidth, bearingX, bearingY, png) = (_data[p], _data[p + 1], (sbyte)_data[p + 2], (sbyte)_data[p + 3], p + 8);
                break;
            case 19 when metrics >= 0:
                (metricHeight, metricWidth, bearingX, bearingY, png) = (_data[metrics], _data[metrics + 1], (sbyte)_data[metrics + 2], (sbyte)_data[metrics + 3], p);
                break;
            default:
                return null;
        }
        if (png + 4 > end) return null;
        var size = (uint)t.U32(png);
        if (size > end - png - 4) return null;
        // the image has the size of its metrics, which are bytes (OpenType, CBDT 5.3): at most 255 × 255 (No. 1138)
        if (!ColorGlyphs.Fits(_data.AsSpan(png + 4, (int)size), 255)) return null;
        try
        {
            var image = PngDecoder.Decode(_data.AsSpan(png + 4, (int)size));
            // the image has the size of its metrics (5.3): one of another size is set at that size — before at its own (No. 1152); metrics of
            // no size describe no image
            if (metricWidth == 0 || metricHeight == 0) return null;
            if (image.Width != metricWidth || image.Height != metricHeight) image = Lanczos.Resize(image, metricWidth, metricHeight);
            // The bearing gives the top edge above the baseline; callers expect the lower left corner
            return (bearingX, bearingY - image.Height, image);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return null;
        }
    }

    /// <summary>An uncompressed image of <paramref name="width"/> × <paramref name="height"/> BGRA pixels, premultiplied, at <paramref name="p"/>.</summary>
    private (int X, int Y, RgbaImage Image)? Bgra(int width, int height, int bearingX, int bearingY, int p, int end)
    {
        if (width == 0 || height == 0 || p + width * height * 4L > end) return null;
        var image = RgbaImage.Blank(width, height);
        for (var i = 0; i < width * height; i++)
        {
            int b = _data[p + i * 4], g = _data[p + i * 4 + 1], r = _data[p + i * 4 + 2], a = _data[p + i * 4 + 3];
            // to straight alpha (a premultiplied component above its alpha is damage: clamped)
            image.Pixels[i * 4] = (byte)(a == 0 ? 0 : Math.Min(255, (r * 255 + a / 2) / a));
            image.Pixels[i * 4 + 1] = (byte)(a == 0 ? 0 : Math.Min(255, (g * 255 + a / 2) / a));
            image.Pixels[i * 4 + 2] = (byte)(a == 0 ? 0 : Math.Min(255, (b * 255 + a / 2) / a));
            image.Pixels[i * 4 + 3] = (byte)a;
        }
        return (bearingX, bearingY - height, image);
    }
}
