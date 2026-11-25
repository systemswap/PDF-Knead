using PdfKnead.Core.Imaging;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Images from the 'sbix' table (Apple, OpenType 1.9 "sbix — Standard Bitmap Graphics Table"): strikes of PNG images per size; the
/// largest strike (sharpest image) is used.
/// </summary>
internal sealed class SbixImages : IGlyphImages
{
    // The font file itself (no copy of the sbix table, which can take several MB); _strike is an absolute offset, _end the end of the table
    private readonly byte[] _data;
    private readonly int _strike;
    private readonly int _end;
    private readonly int _glyphCount;

    private SbixImages(byte[] data, int strike, int end, int ppem, int glyphCount)
    {
        _data = data;
        _strike = strike;
        _end = end;
        PixelsPerEm = ppem;
        _glyphCount = glyphCount;
    }

    /// <inheritdoc/>
    public int PixelsPerEm { get; }

    /// <summary>Reads the strike list of the font.</summary>
    /// <param name="font">The font.</param>
    /// <returns>The source, or null if the font has no usable sbix table.</returns>
    public static SbixImages? TryCreate(OpenTypeFont font)
    {
        if (font.Table("sbix") is not { Length: >= 8 } table) return null;
        var data = font.FileData;
        var t = new Layout.TableData(data);
        var (start, end) = (table.Offset, table.Offset + table.Length);
        var strikes = t.U32(start + 4);
        if (strikes <= 0) return null;
        int best = -1, bestPpem = 0;
        for (var i = 0; i < strikes && i < 64; i++)
        {
            var at = t.U32(start + 8 + i * 4);
            var ppem = t.U16(start + at);
            if (ppem > bestPpem && at > 0 && at < table.Length)
            {
                best = start + at;
                bestPpem = ppem;
            }
        }
        return best < 0 ? null : new SbixImages(data, best, end, bestPpem, font.GlyphCount);
    }

    /// <inheritdoc/>
    public (int X, int Y, RgbaImage Image)? Load(int glyph) => Load(glyph, 0);

    private (int X, int Y, RgbaImage Image)? Load(int glyph, int depth)
    {
        if (glyph < 0 || glyph >= _glyphCount || depth > 4) return null;
        var t = new Layout.TableData(_data);
        var start = t.U32(_strike + 4 + glyph * 4);
        var end = t.U32(_strike + 4 + (glyph + 1) * 4);
        // Offset32 from the strike: one of 2³¹ or more, read as negative, pointed before the strike (No. 1138)
        if (start < 0 || end < 0 || end - start <= 8) return null;
        var at = _strike + start;
        if (at < 0 || (long)_strike + end > _end) return null;
        var type = t.Tag(at + 4);
        if (type == "dupe") return Load(t.U16(at + 8), depth + 1);
        if (type != "png " || !ColorGlyphs.Fits(_data.AsSpan(at + 8, end - start - 8))) return null;
        try
        {
            var image = PngDecoder.Decode(_data.AsSpan(at + 8, end - start - 8));
            return (t.I16(at), t.I16(at + 2), image);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return null;
        }
    }
}
