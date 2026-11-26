using PdfKnead.Core.Imaging;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Images from the 'sbix' table (Apple, OpenType 1.9 "sbix — Standard Bitmap Graphics Table"): strikes of PNG or JPEG images per size; a
/// glyph takes its image from the largest strike that has one (strikes need not cover every glyph, 34.2), scaled to the largest.
/// </summary>
internal sealed class SbixImages : IGlyphImages
{
    // The font file itself (no copy of the sbix table, which can take several MB); _strike is an absolute offset, _end the end of the table
    private readonly byte[] _data;
    private readonly (int Offset, int Ppem)[] _strikes;   // largest first, of one size in the order of the table
    private readonly int _end;
    private readonly int _glyphCount;

    private SbixImages(byte[] data, (int Offset, int Ppem)[] strikes, int end, int glyphCount)
    {
        _data = data;
        _strikes = strikes;
        _end = end;
        PixelsPerEm = strikes[0].Ppem;
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
        // version 1 only (OpenType sbix 34.1; a reader does not use a version it does not know, 1.3 — No. 1149)
        if (t.U16(table.Offset) != 1) return null;
        var (start, end) = (table.Offset, table.Offset + table.Length);
        var strikes = t.U32(start + 4);
        if (strikes <= 0) return null;
        var found = new List<(int Offset, int Ppem)>();
        for (var i = 0; i < strikes && i < 64; i++)
        {
            var at = t.U32(start + 8 + i * 4);
            var ppem = t.U16(start + at);
            if (ppem > 0 && at > 0 && at < table.Length) found.Add((start + at, ppem));
        }
        // every strike, largest first (stable: of two strikes of one size the first) — before only the largest, and a glyph only in a
        // smaller one had no image (OpenType sbix 34.2; docs/FEHLERGEDAECHTNIS.md, No. 1152)
        return found.Count == 0 ? null : new SbixImages(data, [.. found.OrderByDescending(s => s.Ppem)], end, font.GlyphCount);
    }

    /// <inheritdoc/>
    public (int X, int Y, RgbaImage Image)? Load(int glyph)
    {
        foreach (var (strike, ppem) in _strikes)
        {
            if (Load(strike, glyph, 0) is not { } image) continue;
            return ppem == PixelsPerEm ? image : ColorGlyphs.Scaled(image, (double)PixelsPerEm / ppem);
        }
        return null;
    }

    private (int X, int Y, RgbaImage Image)? Load(int strike, int glyph, int depth)
    {
        if (glyph < 0 || glyph >= _glyphCount || depth > 4) return null;
        var t = new Layout.TableData(_data);
        var start = t.U32(strike + 4 + glyph * 4);
        var end = t.U32(strike + 4 + (glyph + 1) * 4);
        // Offset32 from the strike: one of 2³¹ or more, read as negative, pointed before the strike (No. 1138)
        if (start < 0 || end < 0 || end - start <= 8) return null;
        var at = strike + start;
        if (at < 0 || (long)strike + end > _end) return null;
        var type = t.Tag(at + 4);
        if (type == "dupe") return Load(strike, t.U16(at + 8), depth + 1);
        var bytes = _data.AsSpan(at + 8, end - start - 8);
        try
        {
            // PNG, and JPEG as the table allows (34.3; before only PNG, No. 1152)
            if (type == "png " && ColorGlyphs.Fits(bytes)) return (t.I16(at), t.I16(at + 2), PngDecoder.Decode(bytes));
            if (type == "jpg " && ColorGlyphs.Jpeg(bytes) is { } jpeg) return (t.I16(at), t.I16(at + 2), jpeg);
            return null;
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return null;
        }
    }
}
