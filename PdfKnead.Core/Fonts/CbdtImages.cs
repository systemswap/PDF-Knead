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
    private const int BitmapSizeLength = 48;
    private const int MaxStrikes = 256;
    private const int MaxIndexSubTables = 65536;

    private readonly byte[] _data;
    private readonly int _locationStart, _locationEnd, _dataStart, _dataEnd;
    private readonly (int Array, int SubTables, int Ppem, int PpemX, int Flags)[] _strikes;   // largest first

    private CbdtImages(byte[] data, (int Offset, int Length) location, (int Offset, int Length) bitmaps, (int, int, int, int, int)[] strikes)
    {
        _data = data;
        (_locationStart, _locationEnd) = (location.Offset, location.Offset + location.Length);
        (_dataStart, _dataEnd) = (bitmaps.Offset, bitmaps.Offset + bitmaps.Length);
        _strikes = strikes;
        PixelsPerEm = strikes[0].Item3;
    }

    /// <inheritdoc/>
    public int PixelsPerEm { get; }

    /// <summary>Reads the BitmapSize records of the font.</summary>
    /// <param name="font">The font.</param>
    /// <returns>The source, or null if the font has no usable CBLC and CBDT tables.</returns>
    public static CbdtImages? TryCreate(OpenTypeFont font)
    {
        if (font.Table("CBLC") is not { Length: >= 8 } location || font.Table("CBDT") is not { Length: >= 4 } bitmaps) return null;
        var data = font.FileData;
        var t = new Layout.TableData(data);
        var start = location.Offset;
        if (t.U16(start) is not (2 or 3)) return null;
        var sizes = Math.Min((uint)t.U32(start + 4), MaxStrikes);
        var found = new List<(int Array, int SubTables, int Ppem, int PpemX, int Flags)>();
        for (var i = 0; i < sizes; i++)
        {
            var record = start + 8 + i * BitmapSizeLength;
            if (record + BitmapSizeLength > start + location.Length) break;
            var array = (uint)t.U32(record);
            var count = (uint)t.U32(record + 8);
            var ppem = data[record + 45];
            // Only colour strikes (32 bits per pixel) with an index subtable array inside the table
            if (data[record + 46] != 32 || ppem == 0 || count == 0 || count > MaxIndexSubTables || array + 8L * count > location.Length) continue;
            // ppemX (+44) and the flags (+47) too: pixels need not be square, and small metrics may be vertical ones (OpenType 14.1; No. 1160)
            found.Add((start + (int)array, (int)count, ppem, data[record + 44] == 0 ? ppem : data[record + 44], data[record + 47]));
        }
        // every colour strike, largest first — before only the largest, and a glyph it does not hold (or holds in a format not read) had no
        // image although a smaller strike had one (5.2, 5.6; docs/FEHLERGEDAECHTNIS.md, No. 1152)
        return found.Count == 0 ? null : new CbdtImages(data, location, bitmaps, [.. found.OrderByDescending(s => s.Ppem)]);
    }

    /// <inheritdoc/>
    public (int X, int Y, RgbaImage Image)? Load(int glyph)
    {
        foreach (var (array, subTables, ppem, ppemX, flags) in _strikes)
        {
            if (Load(array, subTables, glyph, flags) is not { } image) continue;
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

    private (int X, int Y, RgbaImage Image)? Load(int array, int subTables, int glyph, int flags)
    {
        var t = new Layout.TableData(_data);
        for (var i = 0; i < subTables; i++)
        {
            var entry = array + i * 8;
            if (glyph < t.U16(entry) || glyph > t.U16(entry + 2)) continue;
            var header = array + (long)(uint)t.U32(entry + 4);
            return header + 8 > _locationEnd ? null : Image(t, (int)header, glyph - t.U16(entry), glyph, flags);
        }
        return null;
    }

    /// <summary>Finds the image data of a glyph through one index subtable and decodes it.</summary>
    private (int X, int Y, RgbaImage Image)? Image(Layout.TableData t, int header, int index, int glyph, int flags)
    {
        var indexFormat = t.U16(header);
        var imageFormat = t.U16(header + 2);
        // small glyph metrics (image formats 1, 2, 17) of a strike whose flags name them vertical only (0x02 without 0x01) give no place in
        // horizontal text: no image from this strike, the glyph shows its outline or the image of another strike (14.1; before read as
        // horizontal ones, No. 1160)
        if ((flags & 3) == 2 && imageFormat is 1 or 2 or 17) return null;
        var imageData = (uint)t.U32(header + 4);
        long offset, length;
        var metrics = -1;   // bigGlyphMetrics in the index subtable (formats 2 and 5), used by image format 19
        switch (indexFormat)
        {
            case 1 when Inside(header + 8L + (index + 1) * 4L, 4):
                offset = (uint)t.U32(header + 8 + index * 4);
                length = (uint)t.U32(header + 8 + (index + 1) * 4) - offset;
                break;
            case 3 when Inside(header + 8L + (index + 1) * 2L, 2):
                offset = t.U16(header + 8 + index * 2);
                length = t.U16(header + 8 + (index + 1) * 2) - offset;
                break;
            case 2 when Inside(header + 12, 8):
                length = (uint)t.U32(header + 8);
                offset = index * length;
                metrics = header + 12;
                break;
            case 4 when Inside(header + 8, 4):
            {
                var count = (uint)t.U32(header + 8);
                if (!Inside(header + 12 + count * 4L, 4)) return null;
                var k = 0;
                while (k < count && t.U16(header + 12 + k * 4) != glyph) k++;
                if (k == count) return null;
                offset = t.U16(header + 12 + k * 4 + 2);
                length = t.U16(header + 12 + (k + 1) * 4 + 2) - offset;
                break;
            }
            case 5 when Inside(header + 20, 4):
            {
                var count = (uint)t.U32(header + 20);
                if (count == 0 || !Inside(header + 24 + (count - 1) * 2L, 2)) return null;
                var k = 0;
                while (k < count && t.U16(header + 24 + k * 2) != glyph) k++;
                if (k == count) return null;
                length = (uint)t.U32(header + 8);
                offset = k * length;
                metrics = header + 12;
                break;
            }
            default:
                return null;
        }
        var at = _dataStart + (long)imageData + offset;
        if (length <= 0 || at < _dataStart || at + length > _dataEnd) return null;
        var p = (int)at;
        var end = (int)(at + length);
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

    private bool Inside(long at, int length) => at >= _locationStart && at + length <= _locationEnd;

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
