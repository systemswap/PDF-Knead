namespace PdfKnead.Core.Fonts;

/// <summary>
/// Monochrome and grey glyph bitmaps from the tables 'EBLC' and 'EBDT' (OpenType 1.9, 13 and 14) — read only as the fallback for an embedded
/// font that has no outlines at all (neither glyf with loca nor 'CFF ' or CFF2), as in pure bitmap TrueType fonts of older CJK documents. A font
/// with outlines draws them; one with empty glyf glyphs draws nothing, as all three reference readers do (probes e1–e4; docs/FEHLERGEDAECHTNIS.md,
/// No. 1169). A glyph's bitmap becomes an outline in font units: every pixel a square of unitsPerEm / ppem, the boundary of the inked pixels
/// traced into closed contours, so that the glyph is filled in the colour of the text state like any other glyph (and stroked, clips and is
/// redacted by its shape). Grey bitmaps (bit depth 2, 4, 8) give one contour set per grey level with its opacity.
/// </summary>
/// <remarks>
/// The location of the image data is read as in the colour bitmaps (<see cref="BitmapLocation"/>, shared with <see cref="CbdtImages"/>). Image
/// formats 1, 2 (small metrics, byte or bit aligned), 5 (metrics of the index subtable), 6, 7 (big metrics) and the composites 8 and 9;
/// formats 3 and 4 are not part of OpenType and are not read. Limits: the metrics are bytes (at most 255 × 255 pixels), the data must lie
/// inside the image record, composites nest at most <see cref="MaxCompositeDepth"/> deep with at most <see cref="MaxComponentCalls"/>
/// components per glyph, and the traced shapes are cached within a budget.
/// </remarks>
internal sealed class EmbeddedBitmaps
{
    /// <summary>Nesting depth of composite bitmaps (13.2.8, 13.2.9); the specification sets none.</summary>
    internal const int MaxCompositeDepth = 4;

    /// <summary>Components decoded for one glyph at most, over all depths (a composite calling itself or fanning out stays bounded).</summary>
    internal const int MaxComponentCalls = 1024;

    /// <summary>Grey levels drawn at most: bit depth 8 is reduced to 16 levels of opacity (each level is one filled shape).</summary>
    private const int MaxLevels = 16;

    private readonly BitmapLocation.Tables _tables;
    private readonly BitmapLocation.Strike[] _strikes;   // largest first
    private readonly int _unitsPerEm;
    private readonly int _glyphCount;
    private readonly BoundedCache<(int Glyph, int Strike), BitmapShape?> _shapes = new(16L << 20, s => s?.EstimatedBytes ?? 64);

    private EmbeddedBitmaps(OpenTypeFont font, BitmapLocation.Tables tables, BitmapLocation.Strike[] strikes)
    {
        _tables = tables;
        _strikes = strikes;
        _unitsPerEm = font.UnitsPerEm;
        _glyphCount = font.GlyphCount;
    }

    /// <summary>The strikes read, largest first.</summary>
    internal IReadOnlyList<BitmapLocation.Strike> Strikes => _strikes;

    /// <summary>
    /// The bitmaps of a font without outlines: EBLC (major version 2) with at least one strike of bit depth 1, 2, 4 or 8 and an EBDT; null for a
    /// font with glyf and loca, 'CFF ' or CFF2, or without usable tables.
    /// </summary>
    internal static EmbeddedBitmaps? TryCreate(OpenTypeFont font)
    {
        if (HasOutlines(font)) return null;
        if (font.Table("EBLC") is not { Length: >= 8 } location || font.Table("EBDT") is not { Length: >= 4 } bitmaps) return null;
        var found = BitmapLocation.Strikes(font.FileData, location, depth => depth is 1 or 2 or 4 or 8);
        if (found is not { Count: > 0 }) return null;
        var tables = new BitmapLocation.Tables(font.FileData, location.Offset, location.Offset + location.Length, bitmaps.Offset, bitmaps.Offset + bitmaps.Length);
        return new EmbeddedBitmaps(font, tables, [.. found.OrderByDescending(s => s.Ppem)]);
    }

    /// <summary>Whether a font has outlines of its own: glyf with loca, 'CFF ' or CFF2 (then no bitmap fallback, even for empty glyphs).</summary>
    internal static bool HasOutlines(OpenTypeFont font) =>
        font.HasTable("glyf") && font.HasTable("loca") || font.HasTable("CFF ") || font.HasTable("CFF2") || font.Cff is not null || font.Cff2 is not null;

    /// <summary>
    /// The strike to draw a glyph from at an em of <paramref name="em"/> pixels (points at 72 dpi, as ppem counts them, 14.1): of the strikes
    /// that hold an image of the glyph the one whose ppemY lies nearest (of two equally near the larger); -1 if none holds one.
    /// </summary>
    internal int StrikeFor(int glyph, double em)
    {
        // nearest first; a strike is decoded only when the nearer ones hold no image of the glyph
        Span<bool> tried = stackalloc bool[_strikes.Length];
        for (var round = 0; round < _strikes.Length; round++)
        {
            var best = -1;
            for (var s = 0; s < _strikes.Length; s++)
                if (!tried[s] && (best < 0 || Math.Abs(_strikes[s].Ppem - em) < Math.Abs(_strikes[best].Ppem - em))) best = s;
            tried[best] = true;
            if (Shape(glyph, best) is not null) return best;
        }
        return -1;
    }

    /// <summary>The traced shape of a glyph in strike <paramref name="strike"/> (index into <see cref="Strikes"/>); null if it has no image there.</summary>
    internal BitmapShape? Shape(int glyph, int strike)
    {
        if (glyph < 0 || glyph >= _glyphCount || strike < 0 || strike >= _strikes.Length) return null;
        if (_shapes.TryGetValue((glyph, strike), out var known)) return known;
        return _shapes.GetOrAdd((glyph, strike), key => Decode(key.Glyph, key.Strike) is { } pixels ? Trace(pixels, _strikes[key.Strike]) : null);
    }

    /// <summary>A decoded bitmap: <see cref="Width"/> × <see cref="Height"/> levels from 0 (no ink) to <see cref="Max"/> (full ink), rows from the top.</summary>
    /// <param name="BearingX">Pixels from the horizontal origin to the left edge.</param>
    /// <param name="BearingY">Pixels from the baseline up to the top edge.</param>
    internal sealed record Pixels(int Width, int Height, int BearingX, int BearingY, byte[] Levels, int Max);

    /// <summary>Decodes the bitmap of a glyph in a strike; null if it has none or its data are damaged.</summary>
    internal Pixels? Decode(int glyph, int strike)
    {
        var budget = MaxComponentCalls;
        return Decode(glyph, _strikes[strike], 0, ref budget);
    }

    private Pixels? Decode(int glyph, BitmapLocation.Strike strike, int depth, ref int budget)
    {
        if (BitmapLocation.Find(_tables, strike, glyph) is not { } at) return null;
        var d = _tables.Data;
        var (p, end) = (at.At, at.At + at.Length);
        int height, width, bearingX, bearingY, bits;
        switch (at.ImageFormat)
        {
            // small metrics (13.1): horizontal ones unless the strike's flags name them vertical only — then no place in horizontal text, as
            // in the colour bitmaps (14.1; No. 1160)
            case 1 or 2 or 8 when at.Length >= 5 && (strike.Flags & 3) != 2:
                (height, width, bearingX, bearingY, bits) = (d[p], d[p + 1], (sbyte)d[p + 2], (sbyte)d[p + 3], p + 5);
                break;
            case 6 or 7 or 9 when at.Length >= 8:
                (height, width, bearingX, bearingY, bits) = (d[p], d[p + 1], (sbyte)d[p + 2], (sbyte)d[p + 3], p + 8);
                break;
            case 5 when at.Metrics >= 0:   // the big metrics of index subtable 2 or 5 for all its glyphs (13.2.5)
                (height, width, bearingX, bearingY, bits) = (d[at.Metrics], d[at.Metrics + 1], (sbyte)d[at.Metrics + 2], (sbyte)d[at.Metrics + 3], p);
                break;
            default:
                return null;
        }
        if (width == 0 || height == 0 || (long)width * height > ColorGlyphs.MaxGlyphPixels) return null;
        var max = (1 << strike.BitDepth) - 1;
        var levels = new byte[width * height];
        if (at.ImageFormat is 8 or 9)
        {
            // composite (13.2.8, 13.2.9): format 8 has a pad byte after its small metrics; each component (glyphID, xOffset, yOffset) is placed
            // with its top left corner at the offsets within this bitmap, from the same strike
            var count = at.ImageFormat == 8 ? bits + 1 : bits;
            if (count + 2 > end || depth >= MaxCompositeDepth) return null;
            var components = (d[count] << 8) | d[count + 1];
            if (count + 2 + components * 4L > end) return null;
            for (var c = 0; c < components; c++)
            {
                if (--budget < 0) return null;
                var r = count + 2 + c * 4;
                if (Decode((d[r] << 8) | d[r + 1], strike, depth + 1, ref budget) is not { } part) continue;
                Place(levels, width, height, part, (sbyte)d[r + 2], (sbyte)d[r + 3]);
            }
            return new Pixels(width, height, bearingX, bearingY, levels, max);
        }
        // plain data: byte aligned (1, 6: each row starts on a byte) or bit aligned (2, 5, 7: rows follow bit by bit), the most significant bits
        // first; the needed bytes must lie inside the image record
        var rowBits = (long)width * strike.BitDepth;
        var byteAligned = at.ImageFormat is 1 or 6;
        var stride = byteAligned ? (rowBits + 7) / 8 * 8 : rowBits;
        if (bits + (stride * height + 7) / 8 > end) return null;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var bit = y * stride + (long)x * strike.BitDepth;
                var value = 0;
                for (var b = 0; b < strike.BitDepth; b++, bit++)
                    value = (value << 1) | ((d[bits + (int)(bit >> 3)] >> (7 - (int)(bit & 7))) & 1);
                levels[y * width + x] = (byte)value;
            }
        return new Pixels(width, height, bearingX, bearingY, levels, max);
    }

    /// <summary>Places a component with its top left corner at (<paramref name="x0"/>, <paramref name="y0"/>); outside pixels are cut off.</summary>
    private static void Place(byte[] levels, int width, int height, Pixels part, int x0, int y0)
    {
        for (var y = 0; y < part.Height; y++)
        {
            var ty = y0 + y;
            if (ty < 0 || ty >= height) continue;
            for (var x = 0; x < part.Width; x++)
            {
                var tx = x0 + x;
                if (tx < 0 || tx >= width) continue;
                var v = part.Levels[y * part.Width + x];
                if (v > levels[ty * width + tx]) levels[ty * width + tx] = v;
            }
        }
    }

    /// <summary>The shape of a decoded bitmap in font units: the traced contours of all inked pixels and, for grey ones, per level.</summary>
    private BitmapShape? Trace(Pixels pixels, BitmapLocation.Strike strike)
    {
        var sx = (float)_unitsPerEm / strike.PpemX;
        var sy = (float)_unitsPerEm / strike.Ppem;
        var union = Contours(pixels, sx, sy, v => v > 0);
        if (union.IsEmpty) return null;
        // grey: the levels reduced to at most 16, each with its opacity; a bitmap whose inked pixels are all full needs none
        var top = Math.Min(pixels.Max, MaxLevels - 1);
        int Level(byte v) => v == 0 ? 0 : Math.Max(1, (int)Math.Round(v * (double)top / pixels.Max));
        var used = new bool[top + 1];
        foreach (var v in pixels.Levels) used[Level(v)] = true;
        List<(GlyphOutline, byte)>? levels = null;
        if (Enumerable.Range(1, top - 1).Any(l => used[l]))
        {
            levels = [];
            for (var l = 1; l <= top; l++)
                if (used[l])
                {
                    var level = l;
                    levels.Add((Contours(pixels, sx, sy, v => Level(v) == level), (byte)Math.Round(255.0 * l / top)));
                }
        }
        return new BitmapShape(union, levels);
    }

    /// <summary>
    /// The boundary of the pixels <paramref name="inked"/> as closed contours (straight edges between an inked and an empty pixel, joined and
    /// merged where they run on), mapped into font units: column x to (bearingX + x) · <paramref name="sx"/>, row y (from the top) to
    /// (bearingY − y) · <paramref name="sy"/>. Outer contours run clockwise and holes counter-clockwise in font units, as in TrueType.
    /// </summary>
    internal static GlyphOutline Contours(Pixels pixels, float sx, float sy, Func<byte, bool> inked)
    {
        int w = pixels.Width, h = pixels.Height, stride = w + 1;
        bool Set(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && inked(pixels.Levels[y * w + x]);
        // up to two edges leave a vertex (where two pixels touch only at a corner); stored as target + 1, 0 = none
        var first = new int[stride * (h + 1)];
        var second = new int[stride * (h + 1)];
        void Edge(int x0, int y0, int x1, int y1)
        {
            var from = y0 * stride + x0;
            if (first[from] == 0) first[from] = y1 * stride + x1 + 1;
            else second[from] = y1 * stride + x1 + 1;
        }
        for (var y = 0; y <= h; y++)
            for (var x = 0; x < w; x++)
            {
                bool above = Set(x, y - 1), below = Set(x, y);
                if (below && !above) Edge(x + 1, y, x, y);
                else if (above && !below) Edge(x, y, x + 1, y);
            }
        for (var y = 0; y < h; y++)
            for (var x = 0; x <= w; x++)
            {
                bool left = Set(x - 1, y), right = Set(x, y);
                if (right && !left) Edge(x, y, x, y + 1);
                else if (left && !right) Edge(x, y + 1, x, y);
            }
        var outline = new GlyphOutline();
        var points = new List<int>();
        for (var start = 0; start < first.Length; start++)
        {
            while (first[start] != 0 || second[start] != 0)
            {
                points.Clear();
                var at = start;
                do
                {
                    points.Add(at);
                    if (first[at] == 0 && second[at] == 0) break;   // (cannot happen: every vertex has as many edges in as out)
                    int next;
                    if (first[at] != 0) (next, first[at]) = (first[at] - 1, 0);
                    else (next, second[at]) = (second[at] - 1, 0);
                    at = next;
                } while (at != start);
                Emit(outline, points, stride, pixels, sx, sy);
            }
        }
        return outline.Finish();
    }

    /// <summary>Adds one closed contour, leaving out the vertices where it runs straight on.</summary>
    private static void Emit(GlyphOutline outline, List<int> points, int stride, Pixels pixels, float sx, float sy)
    {
        var n = points.Count;
        var started = false;
        for (var i = 0; i < n; i++)
        {
            int prev = points[(i + n - 1) % n], cur = points[i], next = points[(i + 1) % n];
            int dx0 = cur % stride - prev % stride, dy0 = cur / stride - prev / stride;
            int dx1 = next % stride - cur % stride, dy1 = next / stride - cur / stride;
            if (dx0 == dx1 && dy0 == dy1) continue;   // straight on
            var x = (pixels.BearingX + cur % stride) * sx;
            var y = (pixels.BearingY - cur / stride) * sy;
            if (!started) outline.MoveTo(x, y);
            else outline.LineTo(x, y);
            started = true;
        }
        if (started) outline.Close();
    }
}

/// <summary>The shape of a bitmap glyph in font units.</summary>
/// <param name="Outline">The contours of all inked pixels (what the glyph covers: drawn, clipped, stroked and redacted by).</param>
/// <param name="Levels">For grey bitmaps: per grey level its contours and opacity (0–255), drawn instead of <paramref name="Outline"/> when
/// filling; null when every inked pixel is full.</param>
internal sealed record BitmapShape(GlyphOutline Outline, IReadOnlyList<(GlyphOutline Outline, byte Alpha)>? Levels)
{
    /// <summary>Approximate memory, for the cache budget.</summary>
    internal long EstimatedBytes => Outline.EstimatedBytes + (Levels?.Sum(l => l.Outline.EstimatedBytes) ?? 0) + 64;
}
