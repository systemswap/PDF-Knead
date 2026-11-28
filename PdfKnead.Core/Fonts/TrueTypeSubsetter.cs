using System.Buffers.Binary;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Subset of a TrueType font for embedding (9.9): the glyph indices stay the same, only
/// unused glyphs become empty, so text, widths (/W) and CIDToGIDMap Identity fit without
/// conversion. Composite glyphs keep their components. Only the tables a PDF reader needs
/// are copied (no cmap, name, post names, kerning or OpenType layout).
/// </summary>
public static partial class TrueTypeSubsetter
{
    // Order as recommended by the OpenType specification ("Recommendations")
    private static readonly string[] Keep = ["head", "hhea", "maxp", "OS/2", "hmtx", "cvt ", "fpgm", "prep", "loca", "glyf"];

    /// <summary>Builds a subset with the glyphs <paramref name="glyphs"/> (glyph 0 is always included).</summary>
    /// <param name="font">The TrueType font file (a single font, not a collection).</param>
    /// <param name="glyphs">Glyph indices to keep; indices out of range are ignored.</param>
    /// <param name="keepAllTables">
    /// Keep all tables, including cmap, name, post and layout (when compressing fonts of existing documents): only the outlines of unused
    /// glyphs become empty, so every reader finds the characters as before. The signature (DSIG) is dropped because it would no longer match,
    /// the tables of the variations (<see cref="Cff2Scrubber.VariationTables"/>): the subset is the static default instance a PDF reader draws, and
    /// the colour tables (<see cref="ColourTables"/>), which a PDF reader does not draw and whose layer glyphs would be empty.
    /// </param>
    /// <returns>The font file of the subset, with long loca offsets and new checksums.</returns>
    /// <exception cref="FontFormatException">The font has no glyf, loca or head table, or head or loca is too short.</exception>
    public static byte[] Subset(byte[] font, IEnumerable<int> glyphs, bool keepAllTables = false) =>
        Subset(font, glyphs, keepAllTables ? null : Keep);

    /// <summary>
    /// A subset for a simple TrueType font (one byte per code) that finds its glyphs through the font's cmap: besides the tables of
    /// <see cref="Subset(byte[], IEnumerable{int}, bool)"/> also cmap, post and name (PDF/A, ISO 19005-2, 6.2.11.6, asks for the cmap).
    /// </summary>
    /// <param name="font">The TrueType font file.</param>
    /// <param name="glyphs">Glyph indices to keep.</param>
    /// <returns>The font file of the subset.</returns>
    /// <exception cref="FontFormatException">The font has no glyf, loca or head table, or head or loca is too short.</exception>
    public static byte[] SubsetWithCmap(byte[] font, IEnumerable<int> glyphs) => Subset(font, glyphs, [.. Keep, "cmap", "post", "name"]);

    /// <summary>The colour tables (OpenType COLR, CPAL, sbix, CBDT/CBLC, SVG): left out of a compressed font (No. 1168).</summary>
    internal static readonly IReadOnlySet<string> ColourTables = new HashSet<string>(["COLR", "CPAL", "sbix", "CBDT", "CBLC", "SVG "], StringComparer.Ordinal);

    private static byte[] Subset(byte[] font, IEnumerable<int> glyphs, string[]? kept)
    {
        var (tables, _, newGlyf, newLoca) = Outlines(font, glyphs);
        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        // with all tables (compression) still the static default instance: the tables of the variations would hold deltas of the glyphs
        // emptied (docs/FEHLERGEDAECHTNIS.md, No. 1159; before they were kept); without the colour tables, whose layer glyphs and images
        // tell of glyphs emptied and which a PDF reader does not draw (No. 1168); and without the tables that name glyphs (No. 1172)
        foreach (var tag in kept ?? tables.Keys.Where(t => t != "DSIG" && !Cff2Scrubber.VariationTables.Contains(t) && !ColourTables.Contains(t)
                     && !NameGlyphs.Contains(t)).ToArray())
        {
            if (tag == "glyf") data[tag] = newGlyf;
            else if (tag == "loca") data[tag] = newLoca;
            else if (tables.TryGetValue(tag, out var t)) data[tag] = font.AsSpan(t.Offset, t.Length).ToArray();
        }
        if (data.TryGetValue("hmtx", out var hmtx) && data.TryGetValue("hhea", out var hhea) && hhea.Length >= 36)
            EmptyGlyphsWithoutBearing(hmtx, BinaryPrimitives.ReadUInt16BigEndian(hhea.AsSpan(34)), newLoca);
        FitMetrics(data, newLoca.Length / 4 - 1, GlyfBox(newGlyf, newLoca));
        DefaultInstanceVariation(data, font, tables);
        return Finish(data);
    }

    /// <summary>
    /// Tables that name glyphs (baseline contour points, stretch glyphs, variants) or describe them per size (thresholds, heights, bitmaps): left
    /// out when compressing with all tables, which empties the outlines of the glyphs unused but keeps their numbers — the tables would speak of
    /// glyphs no longer there (docs/FEHLERGEDAECHTNIS.md, No. 1172; before they stayed byte for byte). hdmx and kern stay: the glyph count and
    /// every advance stay the same.
    /// </summary>
    private static readonly HashSet<string> NameGlyphs = new(["BASE", "JSTF", "LTSH", "MATH", "VDMX", "EBSC", "EBLC", "EBDT"], StringComparer.Ordinal);

    /// <summary>
    /// A font written from a variable TrueType font (its source has fvar) is an instance font of the default instance: GETVARIATION (opcode 0x91)
    /// must give its normalized coordinates, all 0, one per axis of fvar in its order (OpenType 79.17.2). An IDEF for 0x91 that pushes those zeros
    /// is appended to prep (the one run last counts, so an IDEF of the source is overridden), prep made if there is none; maxp counts the IDEF
    /// and room for its values on the stack. Before, prep stayed the source's — instructions with GETVARIATION ran into nothing (No. 1172).
    /// </summary>
    /// <param name="data">The tables of the font being written (with TrueType outlines).</param>
    /// <param name="font">The source font file.</param>
    /// <param name="tables">The directory of the source.</param>
    private static void DefaultInstanceVariation(Dictionary<string, byte[]> data, byte[] font, Dictionary<string, (int Offset, int Length)> tables)
    {
        if (!data.ContainsKey("glyf") || !tables.TryGetValue("fvar", out var fvar) || fvar.Length < 16) return;
        var header = font.AsSpan(fvar.Offset, fvar.Length);
        int axesAt = BinaryPrimitives.ReadUInt16BigEndian(header[4..]), axisSize = BinaryPrimitives.ReadUInt16BigEndian(header[10..]);
        // only the axes the table holds (a damaged count is bounded by its length)
        var axes = axisSize == 0 || axesAt > fvar.Length ? 0 : Math.Min(BinaryPrimitives.ReadUInt16BigEndian(header[8..]), (fvar.Length - axesAt) / axisSize);
        if (axes == 0) return;
        var idef = new List<byte>(axes + 8) { 0xB0, 0x91, 0x89 };   // PUSHB[0] 0x91, IDEF
        for (var left = axes; left > 0;)
        {
            var n = Math.Min(left, 255);
            if (n <= 8) idef.Add((byte)(0xB0 + n - 1));             // PUSHB[n-1]
            else idef.AddRange([0x40, (byte)n]);                     // NPUSHB n
            idef.AddRange(new byte[n]);
            left -= n;
        }
        idef.Add(0x2D);                                              // ENDF
        data["prep"] = [.. data.TryGetValue("prep", out var prep) ? prep : [], .. idef];
        // maxp 1.0: maxInstructionDefs (offset 22) and maxStackElements (24) — the values of the IDEF on top of 0x91 pushed
        if (data.TryGetValue("maxp", out var maxp) && maxp.Length >= 32 && BinaryPrimitives.ReadUInt32BigEndian(maxp) == 0x00010000)
        {
            BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(22), (ushort)Math.Min(BinaryPrimitives.ReadUInt16BigEndian(maxp.AsSpan(22)) + 1, 0xFFFF));
            BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(24), (ushort)Math.Min(Math.Max(BinaryPrimitives.ReadUInt16BigEndian(maxp.AsSpan(24)), axes + 1), 0xFFFF));
        }
    }

    // ------------------------------------------------------------ tables that describe the glyphs (docs/FEHLERGEDAECHTNIS.md, No. 1159)

    /// <summary>
    /// The limits of hhea and vhea computed anew from the glyphs the written font has (OpenType hhea 24, vhea 36.2: they must fit hmtx and
    /// vmtx), and usMaxContext 0 in a font without GSUB and GPOS (OS/2 31.2.33). Before, a subset, a scrubbed copy and a rebuilt copy kept the
    /// values of the full font — they told of glyphs no longer in it.
    /// </summary>
    /// <param name="data">The tables of the font being written.</param>
    /// <param name="count">Number of glyphs.</param>
    /// <param name="box">Bounding box of a glyph with contours, null for one without.</param>
    private static void FitMetrics(Dictionary<string, byte[]> data, int count, Func<int, (int XMin, int YMin, int XMax, int YMax)?> box)
    {
        if (data.TryGetValue("hhea", out var hhea) && data.TryGetValue("hmtx", out var hmtx))
            FitHeader(hhea, hmtx, count, g => box(g) is { } b ? (b.XMin, b.XMax) : null);
        if (data.TryGetValue("vhea", out var vhea) && data.TryGetValue("vmtx", out var vmtx))
            FitHeader(vhea, vmtx, count, g => box(g) is { } b ? (b.YMin, b.YMax) : null);
        // usMaxContext from version 2 on (offset 92 + 2 bytes: version 2 has 96 bytes)
        if (!data.ContainsKey("GSUB") && !data.ContainsKey("GPOS") && data.TryGetValue("OS/2", out var os2) && os2.Length >= 96 &&
            BinaryPrimitives.ReadUInt16BigEndian(os2) >= 2)
            BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(94), 0);
    }

    /// <summary>
    /// advance…Max (offset 10), the smallest leading side bearing (12), the smallest advance − (bearing + extent) (14) and the largest bearing +
    /// extent (16) of a hhea or vhea from its metrics table — the advance over every full record, the others over the glyphs with contours only
    /// (all 0 if there is none).
    /// </summary>
    private static void FitHeader(byte[] header, byte[] metrics, int count, Func<int, (int Low, int High)?> extent)
    {
        if (header.Length < 36) return;
        var full = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(34)), metrics.Length / 4);
        if (full == 0) return;
        var advanceMax = 0;
        for (var g = 0; g < full; g++) advanceMax = Math.Max(advanceMax, BinaryPrimitives.ReadUInt16BigEndian(metrics.AsSpan(g * 4)));
        int minBearing = int.MaxValue, minOther = int.MaxValue, maxExtent = int.MinValue;
        for (var g = 0; g < count; g++)
        {
            if (extent(g) is not { } e) continue;
            var at = g < full ? g * 4 + 2 : full * 4 + (g - full) * 2;
            if (at + 2 > metrics.Length) break;
            var advance = BinaryPrimitives.ReadUInt16BigEndian(metrics.AsSpan(Math.Min(g, full - 1) * 4));
            int bearing = BinaryPrimitives.ReadInt16BigEndian(metrics.AsSpan(at)), size = e.High - e.Low;
            minBearing = Math.Min(minBearing, bearing);
            minOther = Math.Min(minOther, advance - (bearing + size));
            maxExtent = Math.Max(maxExtent, bearing + size);
        }
        if (minBearing == int.MaxValue) minBearing = minOther = maxExtent = 0;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), (ushort)advanceMax);
        BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(12), Clamp16(minBearing));
        BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(14), Clamp16(minOther));
        BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(16), Clamp16(maxExtent));
    }

    private static short Clamp16(int value) => (short)Math.Clamp(value, short.MinValue, short.MaxValue);

    /// <summary>The bounding box in the header of a glyph of <paramref name="glyf"/> (long <paramref name="loca"/>); null for one without contours.</summary>
    private static Func<int, (int, int, int, int)?> GlyfBox(byte[] glyf, byte[] loca) => g =>
    {
        if (g < 0 || g * 4 + 8 > loca.Length) return null;
        uint start = BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(g * 4)), end = BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(g * 4 + 4));
        if (end < start + 10 || end > glyf.Length) return null;
        var h = glyf.AsSpan((int)start);
        if (BinaryPrimitives.ReadInt16BigEndian(h) == 0) return null;
        return (BinaryPrimitives.ReadInt16BigEndian(h[2..]), BinaryPrimitives.ReadInt16BigEndian(h[4..]),
            BinaryPrimitives.ReadInt16BigEndian(h[6..]), BinaryPrimitives.ReadInt16BigEndian(h[8..]));
    };

    /// <summary>
    /// xAvgCharWidth (OS/2 31.2.2): the mean of the advances other than 0 of all glyphs, rounded — after scrubbing and rebuilding, which change
    /// the advances (before the mean of the full font stayed).
    /// </summary>
    private static void FitAverageWidth(Dictionary<string, byte[]> data, int count)
    {
        if (!data.TryGetValue("OS/2", out var os2) || os2.Length < 4 || !data.TryGetValue("hmtx", out var hmtx) ||
            !data.TryGetValue("hhea", out var hhea) || hhea.Length < 36) return;
        var full = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(hhea.AsSpan(34)), hmtx.Length / 4);
        if (full == 0) return;
        long sum = 0, glyphs = 0;
        for (var g = 0; g < count; g++)
        {
            var advance = BinaryPrimitives.ReadUInt16BigEndian(hmtx.AsSpan(Math.Min(g, full - 1) * 4));
            if (advance == 0) continue;
            sum += advance;
            glyphs++;
        }
        BinaryPrimitives.WriteInt16BigEndian(os2.AsSpan(2), glyphs == 0 ? (short)0 : Clamp16((int)((sum * 2 + glyphs) / (glyphs * 2))));
    }

    /// <summary>
    /// The VORG entries (OpenType 38.1) of the glyphs kept, in their order; version and default stay. Before the entries of the glyphs scrubbed
    /// stayed — a glyph number with its vertical origin, for every glyph of the full font.
    /// </summary>
    private static byte[] VorgOf(byte[] vorg, HashSet<int> keep)
    {
        if (vorg.Length < 8) return vorg;
        var count = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(vorg.AsSpan(6)), (vorg.Length - 8) / 4);
        var entries = new List<int>(Math.Min(count, keep.Count));
        for (var i = 0; i < count; i++)
            if (keep.Contains(BinaryPrimitives.ReadUInt16BigEndian(vorg.AsSpan(8 + i * 4)))) entries.Add(i);
        var result = new byte[8 + entries.Count * 4];
        vorg.AsSpan(0, 6).CopyTo(result);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(6), (ushort)entries.Count);
        for (var k = 0; k < entries.Count; k++) vorg.AsSpan(8 + entries[k] * 4, 4).CopyTo(result.AsSpan(8 + k * 4));
        return result;
    }

    /// <summary>
    /// The glyphs without outline (in a long <paramref name="loca"/>) get left side bearing 0 (OpenType hmtx 25: should be 0 for an empty glyph)
    /// — a subset and a rebuild kept the bearing of the glyph they emptied (docs/FEHLERGEDAECHTNIS.md, No. 1151).
    /// </summary>
    private static void EmptyGlyphsWithoutBearing(byte[] hmtx, int full, byte[] loca)
    {
        for (var g = 0; g + 1 < loca.Length / 4; g++)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(g * 4)) != BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(g * 4 + 4))) continue;
            var at = g < full ? g * 4 + 2 : full * 4 + (g - full) * 2;
            if (at + 2 <= hmtx.Length) BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(at), 0);
        }
    }

    /// <summary>
    /// Redaction: the font keeps only what its glyphs <paramref name="glyphs"/> need (glyph 0 and the components of composite glyphs stay),
    /// at the same glyph indices — the outlines of the others become empty, and nothing else in the file tells of them: the cmap maps only
    /// the glyphs kept, glyph names (post format 2) and metrics (hmtx, vmtx) of the others are cleared, and the tables that list pairs or
    /// classes of glyphs (kerning, OpenType layout) are left out — a PDF reader positions every glyph itself. A character that was only
    /// in redacted text can then not be read from the font (docs/FEHLERGEDAECHTNIS.md, No. 169). The font is written as the static default
    /// instance, and the values that describe its glyphs (limits of hhea and vhea, VORG, mean width, first and last character, usMaxContext)
    /// follow the glyphs kept (No. 1159).
    /// </summary>
    /// <remarks>An OpenType font with CFF outlines (a 'CFF ' table) is scrubbed the same way, its outlines by <see cref="CffScrubber"/>; one with
    /// a 'CFF2' table by <see cref="Cff2Scrubber"/> (a static CFF2 table in the default instance); one whose glyphs exist only as images
    /// (EBLC/EBDT, CBLC/CBDT, sbix) by <see cref="BitmapScrubber"/>, its image tables written anew. The tables of the variations are left
    /// out with every kind of outline.</remarks>
    /// <param name="font">The TrueType or OpenType font file.</param>
    /// <param name="glyphs">Glyph indices still shown.</param>
    /// <param name="shownCids">For a CID-keyed 'CFF ' table: the CIDs still shown, which no removed glyph takes as its new CID (<see cref="CffScrubber.Scrub"/>).</param>
    /// <returns>The font file.</returns>
    /// <exception cref="FontFormatException">The font has neither glyf, loca and head nor a CFF or readable CFF2 table nor only glyph images, or head
    /// or loca is too short.</exception>
    public static byte[] Scrub(byte[] font, IEnumerable<int> glyphs, IReadOnlySet<int>? shownCids = null)
    {
        Dictionary<string, (int Offset, int Length)> tables;
        HashSet<int> keep;
        var outlines = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int count;
        var directory = ReadDirectory(font);
        if (!directory.ContainsKey("glyf") && directory.TryGetValue("CFF ", out var cffTable))
        {
            (outlines["CFF "], keep) = CffScrubber.Scrub(new ArraySegment<byte>(font, cffTable.Offset, cffTable.Length), glyphs, shownCids);
            tables = directory;
            count = directory.TryGetValue("maxp", out var maxp) && maxp.Length >= 6 ? BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(maxp.Offset + 4)) : 0;
        }
        else if (!directory.ContainsKey("glyf") && directory.ContainsKey("CFF2"))
        {
            // CFF2: written as the static default instance, without the tables of the variations (docs/FEHLERGEDAECHTNIS.md, No. 1158)
            if (OpenTypeFont.Parse(font).Cff2 is not { } cff2) throw new FontFormatException("CFF2 nicht lesbar");
            (outlines["CFF2"], keep) = Cff2Scrubber.Scrub(cff2, glyphs);
            tables = new Dictionary<string, (int Offset, int Length)>(directory, StringComparer.Ordinal);
            foreach (var tag in Cff2Scrubber.VariationTables) tables.Remove(tag);
            count = directory.TryGetValue("maxp", out var maxp) && maxp.Length >= 6 ? BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(maxp.Offset + 4)) : 0;
        }
        else if (!(directory.ContainsKey("glyf") && directory.ContainsKey("loca")) && OpenTypeFont.Parse(font) is { Broken: false } images
                 && BitmapScrubber.ImagesOnly(images))
        {
            // glyphs only as images (EBLC/EBDT, CBLC/CBDT, sbix): those tables written anew with the images of the kept glyphs; a lone glyf or
            // loca, which no reader can draw from, goes as well (docs/FEHLERGEDAECHTNIS.md, No. 1169)
            keep = BitmapScrubber.Kept(images, glyphs);
            foreach (var (tag, table) in BitmapScrubber.Rebuild(images, keep)) outlines[tag] = table;
            tables = new Dictionary<string, (int Offset, int Length)>(directory, StringComparer.Ordinal);
            tables.Remove("glyf");
            tables.Remove("loca");
            count = images.GlyphCount;
        }
        else
        {
            (tables, keep, outlines["glyf"], outlines["loca"]) = Outlines(font, glyphs);
            count = outlines["loca"].Length / 4 - 1;
        }
        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (tag, (offset, length)) in tables)
        {
            if (Dropped.Contains(tag)) continue;
            data[tag] = font.AsSpan(offset, length).ToArray();
        }
        foreach (var (tag, table) in outlines) data[tag] = table;
        if (data.TryGetValue("hmtx", out var hmtx) && data.TryGetValue("hhea", out var hhea) && hhea.Length >= 36)
            ClearMetrics(hmtx, BinaryPrimitives.ReadUInt16BigEndian(hhea.AsSpan(34)), count, keep);
        if (data.TryGetValue("vmtx", out var vmtx) && data.TryGetValue("vhea", out var vhea) && vhea.Length >= 36)
            ClearMetrics(vmtx, BinaryPrimitives.ReadUInt16BigEndian(vhea.AsSpan(34)), count, keep);
        if (data.TryGetValue("post", out var post) && PostWithout(post, count, keep) is { } cleared) data["post"] = cleared;
        if (data.TryGetValue("VORG", out var vorg)) data["VORG"] = VorgOf(vorg, keep);
        // what describes the glyphs is computed from those kept (docs/FEHLERGEDAECHTNIS.md, No. 1159): the limits of hhea and vhea, the mean
        // width, the first and last character of the new cmap (0xFFFF, the code of the closing segment, when it maps none — or without cmap)
        OpenTypeFont? parsed = null;
        if (tables.ContainsKey("cmap"))
        {
            parsed = OpenTypeFont.Parse(font);
            data["cmap"] = CmapOf(parsed, keep, out var first, out var last);
            SetCharRange(data, first, last);
        }
        else SetCharRange(data, -1, -1);
        if (outlines.TryGetValue("loca", out var newLoca)) FitMetrics(data, count, GlyfBox(outlines["glyf"], newLoca));
        else
        {
            // 'CFF ' and CFF2: the glyphs kept draw as in the source, the others are empty
            var source = parsed ?? OpenTypeFont.Parse(font);
            FitMetrics(data, count, g => keep.Contains(g) && source.Outline(g) is { IsEmpty: false } o
                ? ((int)Math.Floor(o.Bounds.Left), (int)Math.Floor(o.Bounds.Top), (int)Math.Ceiling(o.Bounds.Right), (int)Math.Ceiling(o.Bounds.Bottom))
                : null);
        }
        FitAverageWidth(data, count);
        DefaultInstanceVariation(data, font, directory);
        return Finish(data);
    }

    /// <summary>usFirstCharIndex and usLastCharIndex (OS/2 31.2.21, 31.2.22); without a code mapped both 0xFFFF.</summary>
    private static void SetCharRange(Dictionary<string, byte[]> data, int first, int last)
    {
        if (!data.TryGetValue("OS/2", out var os2) || os2.Length < 68) return;
        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(64), (ushort)(first < 0 ? 0xFFFF : Math.Min(first, 0xFFFF)));
        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(66), (ushort)(last < 0 ? 0xFFFF : Math.Min(last, 0xFFFF)));
    }

    /// <summary>
    /// Tables that list glyphs in pairs, classes or per size, or a signature: left out when scrubbing. So are the tables of the variations
    /// (<see cref="Cff2Scrubber.VariationTables"/>: the scrubbed font is the static default instance, also with glyf and 'CFF ' outlines — before
    /// gvar, HVAR, VVAR and cvar kept the deltas of the glyphs scrubbed), the bitmaps and Graphite tables of other systems (bdat, bloc, Silf,
    /// Glat, Gloc: data per glyph) and PCLT, which describes the full font (the advance of the space, the character sets) (No. 1159), and MERG,
    /// whose merge classes name the glyphs scrubbed (No. 1172).
    /// </summary>
    private static readonly HashSet<string> Dropped = new(
    [
        "DSIG", "kern", "kerx", "GSUB", "GPOS", "GDEF", "JSTF", "BASE", "MATH", "morx", "mort", "LTSH", "hdmx", "VDMX", "feat", "prop", "trak",
        "just", "opbd", "bsln", "lcar", "Zapf", "fdsc", "COLR", "CPAL", "sbix", "CBDT", "CBLC", "EBDT", "EBLC", "EBSC", "SVG ", "meta",
        "bdat", "bloc", "Silf", "Glat", "Gloc", "PCLT", "MERG", .. Cff2Scrubber.VariationTables,
    ], StringComparer.Ordinal);

    /// <summary>The kept glyphs (with glyph 0 and the components of composite glyphs) and new glyf and loca with the others empty.</summary>
    private static (Dictionary<string, (int Offset, int Length)> Tables, HashSet<int> Keep, byte[] Glyf, byte[] Loca) Outlines(byte[] font, IEnumerable<int> glyphs)
    {
        var tables = ReadDirectory(font);
        if (!tables.TryGetValue("glyf", out var glyf) || !tables.TryGetValue("loca", out var loca) || !tables.TryGetValue("head", out var head))
            throw new FontFormatException("keine TrueType-Umrisse");
        // Check the bounds explicitly, so that damaged fonts fail with FontFormatException instead of ArgumentOutOfRangeException (found by fuzzing)
        if (head.Length < 54) throw new FontFormatException("head zu kurz");
        // Every value other than 0 means long offsets, as in the reader (OpenTypeFont) and in common font engines
        var longLoca = BinaryPrimitives.ReadInt16BigEndian(font.AsSpan(head.Offset + 50)) != 0;
        var count = tables.TryGetValue("maxp", out var maxp) && maxp.Length >= 6 ? BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(maxp.Offset + 4)) : 0;
        // without a count in maxp, as many glyphs as loca holds — as the reader counts them (OpenTypeFont.LocaEntries)
        if (count == 0) count = Math.Max(0, OpenTypeFont.LocaEntries(0, loca.Length, longLoca) - 1);
        if (loca.Length < (count + 1) * (longLoca ? 4 : 2)) throw new FontFormatException("loca zu kurz");
        var offsets = new int[count + 1];
        for (var i = 0; i <= count; i++)
            offsets[i] = longLoca
                ? (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(loca.Offset + i * 4))
                : BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(loca.Offset + i * 2)) * 2;
        // Offsets outside glyf (including those above 2 GB, which are negative as int): the glyph counts as empty
        for (var i = 0; i <= count; i++)
            if (offsets[i] < 0 || offsets[i] > glyf.Length) offsets[i] = -1;
        var ends = GlyphEnds(offsets, count, glyf.Length);

        // Requested glyphs plus the components of composite glyphs
        var keep = new HashSet<int> { 0 };
        var pending = new Stack<int>(glyphs.Where(g => g >= 0 && g < count));
        while (pending.Count > 0)
        {
            var g = pending.Pop();
            if (!keep.Add(g) && g != 0) continue;
            foreach (var component in Components(font, glyf.Offset, offsets, ends, g))
                if (component < count && !keep.Contains(component)) pending.Push(component);
        }

        // New glyf: kept glyphs at their length (aligned to 4 bytes), the others empty; loca always long
        using var newGlyf = new MemoryStream();
        var newLoca = new byte[(count + 1) * 4];
        for (var g = 0; g < count; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(g * 4), (uint)newGlyf.Length);
            var start = offsets[g];
            var end = ends[g];
            if (!keep.Contains(g) || start < 0 || end <= start) continue;
            newGlyf.Write(font, glyf.Offset + start, end - start);
            while (newGlyf.Length % 4 != 0) newGlyf.WriteByte(0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(count * 4), (uint)newGlyf.Length);
        return (tables, keep, newGlyf.ToArray(), newLoca);
    }

    /// <summary>
    /// PDF/A (rebuilding a font whose program does not match its PDF dictionary): the glyphs <paramref name="advances"/> keep their indices and
    /// their data byte for byte, the others become empty; <paramref name="appended"/> are copies of glyphs added at the end (a glyph two codes use
    /// with different widths, or glyph 0 where a code draws it). Every glyph gets the advance given (the width the PDF gives its code), the
    /// cmap only <paramref name="cmap"/> (a symbolic (3,0) table) or none, the glyph names go (post format 3), and so do the tables that list
    /// glyphs (layout, kerning, device metrics) — a PDF reader positions every glyph itself.
    /// </summary>
    /// <param name="font">The TrueType font file.</param>
    /// <param name="advances">Glyph index → advance in font units, for the glyphs kept at their index.</param>
    /// <param name="appended">Per glyph added at the end (index = glyph count + position): the glyph copied and its advance.</param>
    /// <param name="cmap">Code → glyph index for a (3,0) cmap; null for none (a CIDFontType2 finds its glyphs through the PDF).</param>
    /// <param name="keepCmap">Without <paramref name="cmap"/>: keep the font's own cmap (readers take the text from it where the PDF gives none).</param>
    /// <returns>The font file.</returns>
    /// <exception cref="FontFormatException">The font has no glyf, loca, head, maxp or hhea table, or one is too short.</exception>
    internal static byte[] Rebuild(byte[] font, IReadOnlyDictionary<int, int> advances, IReadOnlyList<(int Glyph, int Advance)> appended,
        IReadOnlyList<(int Code, int Glyph)>? cmap, bool keepCmap = false)
    {
        var (tables, _, glyf, loca) = Outlines(font, advances.Keys.Concat(appended.Select(a => a.Glyph)));
        if (!tables.TryGetValue("maxp", out var maxp) || maxp.Length < 6 || !tables.TryGetValue("hhea", out var hhea) || hhea.Length < 36)
            throw new FontFormatException("maxp oder hhea fehlt");
        var count = loca.Length / 4 - 1;
        var total = count + appended.Count;
        if (total > 65535) throw new FontFormatException("zu viele Glyphen");
        uint Start(int g) => BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(g * 4));

        // glyf and loca with the copies at the end
        var newGlyf = new MemoryStream(glyf.Length + 1024);
        newGlyf.Write(glyf);
        var newLoca = new byte[(total + 1) * 4];
        loca.AsSpan(0, count * 4).CopyTo(newLoca);
        for (var i = 0; i < appended.Count; i++)
        {
            while (newGlyf.Length % 4 != 0) newGlyf.WriteByte(0);
            BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan((count + i) * 4), (uint)newGlyf.Length);
            var g = appended[i].Glyph;
            if (g >= 0 && g < count) newGlyf.Write(glyf.AsSpan((int)Start(g), (int)(Start(g + 1) - Start(g))));
        }
        while (newGlyf.Length % 4 != 0) newGlyf.WriteByte(0);
        BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(total * 4), (uint)newGlyf.Length);

        // hmtx: one full record per glyph; the side bearing as before, the advance as given (0 for the glyphs left empty)
        var oldMetrics = tables.TryGetValue("hmtx", out var hmtx) ? font.AsSpan(hmtx.Offset, hmtx.Length).ToArray() : [];
        var full = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(hhea.Offset + 34));
        short Bearing(int g)
        {
            var at = g < full ? g * 4 + 2 : full * 4 + (g - full) * 2;
            return g >= 0 && at + 2 <= oldMetrics.Length ? BinaryPrimitives.ReadInt16BigEndian(oldMetrics.AsSpan(at)) : (short)0;
        }
        var newMetrics = new byte[total * 4];
        var widest = 0;
        for (var g = 0; g < total; g++)
        {
            var (advance, bearing) = g < count
                ? (advances.TryGetValue(g, out var a) ? a : 0, Bearing(g))
                : (appended[g - count].Advance, Bearing(appended[g - count].Glyph));
            advance = Math.Clamp(advance, 0, 65535);
            widest = Math.Max(widest, advance);
            BinaryPrimitives.WriteUInt16BigEndian(newMetrics.AsSpan(g * 4), (ushort)advance);
            BinaryPrimitives.WriteInt16BigEndian(newMetrics.AsSpan(g * 4 + 2), bearing);
        }
        var newMaxp = font.AsSpan(maxp.Offset, maxp.Length).ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(newMaxp.AsSpan(4), (ushort)total);
        var newHhea = font.AsSpan(hhea.Offset, hhea.Length).ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(newHhea.AsSpan(10), (ushort)widest);
        BinaryPrimitives.WriteUInt16BigEndian(newHhea.AsSpan(34), (ushort)total);
        // post format 3: no glyph names (they would name the old glyphs)
        var newPost = new byte[32];
        if (tables.TryGetValue("post", out var post) && post.Length >= 32) font.AsSpan(post.Offset, 32).CopyTo(newPost);
        BinaryPrimitives.WriteUInt32BigEndian(newPost, 0x00030000);

        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var tag in (string[])["head", "OS/2", "cvt ", "fpgm", "prep", "name"])
            if (tables.TryGetValue(tag, out var t)) data[tag] = font.AsSpan(t.Offset, t.Length).ToArray();
        data["hhea"] = newHhea;
        data["maxp"] = newMaxp;
        EmptyGlyphsWithoutBearing(newMetrics, total, newLoca);
        data["hmtx"] = newMetrics;
        data["loca"] = newLoca;
        data["glyf"] = newGlyf.ToArray();
        data["post"] = newPost;
        if (cmap is not null)
        {
            data["cmap"] = SymbolCmap(cmap);
            Symbolic(data, cmap);
        }
        else if (keepCmap && tables.TryGetValue("cmap", out var own)) data["cmap"] = font.AsSpan(own.Offset, own.Length).ToArray();
        // the limits of hhea, the mean width and usMaxContext from the glyphs and advances of the new font (docs/FEHLERGEDAECHTNIS.md, No. 1159)
        FitMetrics(data, total, GlyfBox(data["glyf"], newLoca));
        FitAverageWidth(data, total);
        DefaultInstanceVariation(data, font, tables);
        return Finish(data);
    }

    /// <summary>
    /// A font made symbolic (its only cmap is (3,0)): the Windows names take the same encoding 0, PANOSE names the family kind „pictorial“ (5),
    /// usFirstCharIndex and usLastCharIndex the codes of the new cmap (OpenType name 30.2, OS/2 31.2.12 and 31.2.21/22, recommendations 3.27).
    /// Before, the names, PANOSE and the range of the text font it was made from stayed (docs/FEHLERGEDAECHTNIS.md, No. 1146).
    /// </summary>
    private static void Symbolic(Dictionary<string, byte[]> data, IReadOnlyList<(int Code, int Glyph)> cmap)
    {
        var codes = cmap.Select(m => m.Code).Where(c => c is >= 0 and < 0xFFFF).ToList();
        if (data.TryGetValue("OS/2", out var os2) && os2.Length >= 68)
        {
            os2[32] = 5;   // bFamilyType: pictorial
            if (codes.Count > 0)
            {
                BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(64), (ushort)codes.Min());
                BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(66), (ushort)codes.Max());
            }
        }
        if (data.TryGetValue("name", out var name) && name.Length >= 6)
        {
            int count = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(2));
            if (6 + count * 12 > name.Length) return;
            var records = new List<byte[]>();
            for (var i = 0; i < count; i++)
            {
                var record = name.AsSpan(6 + i * 12, 12).ToArray();
                if (BinaryPrimitives.ReadUInt16BigEndian(record) == 3 && BinaryPrimitives.ReadUInt16BigEndian(record.AsSpan(2)) == 1)
                    BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(2), 0);
                records.Add(record);
            }
            // the records stay sorted by platform, encoding, language and name ID (30.2)
            records.Sort((a, b) =>
            {
                for (var k = 0; k < 8; k += 2)
                {
                    var d = BinaryPrimitives.ReadUInt16BigEndian(a.AsSpan(k)).CompareTo(BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(k)));
                    if (d != 0) return d;
                }
                return 0;
            });
            for (var i = 0; i < count; i++) records[i].CopyTo(name, 6 + i * 12);
        }
    }

    /// <summary>A cmap with one (3,0) table of format 4, one segment per code (at most a few hundred).</summary>
    private static byte[] SymbolCmap(IReadOnlyList<(int Code, int Glyph)> mappings)
    {
        var sorted = mappings.Where(m => m.Code is >= 0 and < 0xFFFF).DistinctBy(m => m.Code).OrderBy(m => m.Code).ToList();
        var segments = sorted.Count + 1;   // the last one ends at 0xFFFF
        var length = 16 + segments * 8;
        var table = new byte[12 + length];
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2), 1);           // one table
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4), 3);           // platform 3
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6), 0);           // encoding 0 (symbol)
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8), 12);
        var f = table.AsSpan(12);
        BinaryPrimitives.WriteUInt16BigEndian(f, 4);
        BinaryPrimitives.WriteUInt16BigEndian(f[2..], (ushort)length);
        BinaryPrimitives.WriteUInt16BigEndian(f[6..], (ushort)(segments * 2));
        var power = 1;
        var log = 0;
        while (power * 2 <= segments)
        {
            power *= 2;
            log++;
        }
        BinaryPrimitives.WriteUInt16BigEndian(f[8..], (ushort)(power * 2));
        BinaryPrimitives.WriteUInt16BigEndian(f[10..], (ushort)log);
        BinaryPrimitives.WriteUInt16BigEndian(f[12..], (ushort)(segments * 2 - power * 2));
        var ends = 14;
        var starts = ends + segments * 2 + 2;
        var deltas = starts + segments * 2;
        var offsets = deltas + segments * 2;
        for (var i = 0; i < segments; i++)
        {
            var (code, glyph) = i < sorted.Count ? sorted[i] : (0xFFFF, 0);
            BinaryPrimitives.WriteUInt16BigEndian(f[(ends + i * 2)..], (ushort)code);
            BinaryPrimitives.WriteUInt16BigEndian(f[(starts + i * 2)..], (ushort)code);
            BinaryPrimitives.WriteUInt16BigEndian(f[(deltas + i * 2)..], i < sorted.Count ? (ushort)((glyph - code) & 0xFFFF) : (ushort)1);
            BinaryPrimitives.WriteUInt16BigEndian(f[(offsets + i * 2)..], 0);
        }
        return table;
    }

    /// <summary>head with long loca and its checksum adjustment cleared (recomputed in Assemble), then the file.</summary>
    /// <exception cref="FontFormatException">The font has no head table, or it is too short.</exception>
    internal static byte[] Finish(Dictionary<string, byte[]> data)
    {
        // a damaged font with CFF outlines may lack it (found by fuzzing: KeyNotFoundException while saving)
        if (!data.TryGetValue("head", out var newHead) || newHead.Length < 54) throw new FontFormatException("head fehlt oder ist zu kurz");
        BinaryPrimitives.WriteInt16BigEndian(newHead.AsSpan(50), 1);
        BinaryPrimitives.WriteUInt32BigEndian(newHead.AsSpan(8), 0);
        return Assemble(data);
    }

    /// <summary>
    /// Advance and side bearing of the glyphs not kept become 0 (hmtx, vmtx). The advance of the last full record stays: it applies to
    /// every glyph after it as well.
    /// </summary>
    private static void ClearMetrics(byte[] table, int full, int count, HashSet<int> keep)
    {
        for (var g = 0; g < count; g++)
        {
            if (keep.Contains(g)) continue;
            if (g < full)
            {
                if (g * 4 + 4 > table.Length) break;
                if (g < full - 1) BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(g * 4), 0);
                BinaryPrimitives.WriteInt16BigEndian(table.AsSpan(g * 4 + 2), 0);
            }
            else
            {
                var at = full * 4 + (g - full) * 2;
                if (at + 2 > table.Length) break;
                BinaryPrimitives.WriteInt16BigEndian(table.AsSpan(at), 0);
            }
        }
    }

    /// <summary>post format 2 with the names of the glyphs not kept cleared (index 0, ".notdef") and their strings gone; null if it has no names of its own.</summary>
    private static byte[]? PostWithout(byte[] post, int count, HashSet<int> keep)
    {
        if (post.Length < 34 || BinaryPrimitives.ReadUInt32BigEndian(post) != 0x00020000) return null;
        int numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(post.AsSpan(32));
        if (34 + numGlyphs * 2 > post.Length) return null;
        var indices = new int[numGlyphs];
        for (var g = 0; g < numGlyphs; g++) indices[g] = BinaryPrimitives.ReadUInt16BigEndian(post.AsSpan(34 + g * 2));
        var strings = new List<byte[]>();
        for (var at = 34 + numGlyphs * 2; at < post.Length;)
        {
            var length = post[at];
            if (at + 1 + length > post.Length) break;
            strings.Add(post.AsSpan(at + 1, length).ToArray());
            at += 1 + length;
        }
        var kept = new List<byte[]>();
        var renumbered = new Dictionary<int, int>();
        using var output = new MemoryStream();
        output.Write(post, 0, 32);
        Span<byte> two = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(two, (ushort)numGlyphs);
        output.Write(two);
        for (var g = 0; g < numGlyphs; g++)
        {
            var index = indices[g];
            if (!keep.Contains(g) || index >= 258 && index - 258 >= strings.Count) index = 0;
            else if (index >= 258)
            {
                if (!renumbered.TryGetValue(index, out var now))
                {
                    renumbered[index] = now = 258 + kept.Count;
                    kept.Add(strings[index - 258]);
                }
                index = now;
            }
            BinaryPrimitives.WriteUInt16BigEndian(two, (ushort)index);
            output.Write(two);
        }
        foreach (var name in kept)
        {
            output.WriteByte((byte)name.Length);
            output.Write(name);
        }
        return output.ToArray();
    }

    /// <summary>
    /// A new cmap with every subtable of the font, mapping only to the glyphs kept: format 4, format 12 for codes above 0xFFFF. Records
    /// that share a subtable share the new one too, and all subtables together read at most a million codes, as many as one subtable
    /// may give (<see cref="OpenTypeFont.Mappings"/>): before, 64 records naming one group over all of Unicode were each read, held and
    /// written again — gigabytes from a few hundred bytes (No. 1138). <paramref name="first"/> and <paramref name="last"/> give the smallest
    /// and the largest code the Unicode subtables (platform 0, and 3 with encoding 0, 1 or 10) map to a glyph other than 0, −1 for none.
    /// </summary>
    private static byte[] CmapOf(OpenTypeFont font, HashSet<int> keep, out int first, out int last)
    {
        var budget = MaxCmapCodes;
        (first, last) = (-1, -1);
        var bodies = new List<byte[]>();
        var rangeOf = new List<(int First, int Last)>();
        var bodyOf = new Dictionary<(int Offset, bool Long), int>();
        var records = new List<(int Platform, int Encoding, int Body)>();
        var data = font.FileData;
        // sorted by platform, then encoding (OpenType cmap 9.3: required) — before, in the order of the source (No. 1143)
        foreach (var (platform, encoding) in font.CmapSubtables.OrderBy(r => r.Platform).ThenBy(r => r.Encoding))
        {
            // format 4 holds at most 64 KB (about 8 000 runs); beyond that, and for codes above 0xFFFF, format 12
            var wide = (platform, encoding) is (3, 10) or (0, 4) or (0, 6);
            var source = font.CmapOffset(platform, encoding);
            var format = source >= 0 && source + 2 <= data.Length ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(source)) : -1;
            // the variation sequences under (0,5) stay in format 14, filtered on the glyphs kept (cmap 9.4.10; before left out, No. 1160)
            if ((platform, encoding) == (0, 5))
            {
                if (format == 14 && !bodyOf.ContainsKey((source, false)) && Format14Of(font, keep, ref budget) is { } sequences)
                {
                    bodyOf[(source, false)] = bodies.Count;
                    bodies.Add(sequences);
                    rangeOf.Add((-1, -1));
                }
                if (format == 14 && bodyOf.TryGetValue((source, false), out var shared)) records.Add((platform, encoding, shared));
                continue;
            }
            // formats 8 and 10 (Unicode code points, read since No. 1160) are written as the others; a subtable of format 2 (the byte codes of
            // the old encodings, which format 4 would read as 16-bit codes) or of an unknown one is left out: written as an empty format 4 it
            // lost every mapping (No. 1143)
            if (format is not (0 or 4 or 6 or 8 or 10 or 12 or 13)) continue;
            // a Macintosh subtable keeps its language (cmap 9.4.1: the Macintosh language code plus one, 0 for any language)
            var language = platform != 1 ? 0 : format is 8 or 10 or 12 or 13
                ? source + 12 <= data.Length ? (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(source + 8)) : 0
                : source + 6 <= data.Length ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(source + 4)) : 0;
            if (!bodyOf.TryGetValue((source, wide), out var body))
            {
                var read = new List<(int Code, int Glyph)>();
                foreach (var m in font.Mappings(platform, encoding))
                {
                    if (--budget < 0) break;
                    if (keep.Contains(m.Glyph)) read.Add(m);
                }
                // each code once, the first mapping of it (overlapping groups of a damaged source were written overlapping, cmap 9.4.8)
                var map = read.DistinctBy(m => m.Code).OrderBy(m => m.Code).ToList();
                var runs = Runs(map);
                bodyOf[(source, wide)] = body = bodies.Count;
                // formats 4 and 12 as for new fonts (cmap 9.1; not 4 and 6 together, 9.3), with the exceptions the norm makes: a format 13 of a
                // last-resort font stays 13 (9.3.2, 9.1), the custom platform 4 keeps format 0 or 6 like its CFF encoding (9.3.6); codes above
                // 0xFFFF and the full repertoire in format 12; else format 4 — with a glyph array where runs alone would not fit (9.3.5: (3,1)
                // is format 4; before format 12 beyond 8 000 runs) (No. 1150)
                var beyond = map.Any(m => m.Code > 0xFFFF);
                bodies.Add(format == 13 ? Format13Of(map, language)
                    : platform == 4 && format is 0 or 6 && !beyond && Format6Of(map, language) is { } trimmed ? trimmed
                    : wide || beyond ? Format12Of(runs, language)
                    : runs.Count <= 8000 ? Format4Of(runs, language)
                    : Format4WithArray(map, language) ?? Format12Of(runs, language));
                // the map is sorted by code: the first and the last entry with a glyph other than 0
                var low = map.FindIndex(m => m.Glyph != 0);
                rangeOf.Add(low < 0 ? (-1, -1) : (map[low].Code, map[map.FindLastIndex(m => m.Glyph != 0)].Code));
            }
            records.Add((platform, encoding, body));
            if ((platform == 0 || (platform, encoding) is (3, 0) or (3, 1) or (3, 10)) && rangeOf[body] is { First: >= 0 } range)
                (first, last) = (first < 0 ? range.First : Math.Min(first, range.First), Math.Max(last, range.Last));
        }
        using var output = new MemoryStream();
        var header = new byte[4 + 8 * records.Count];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)records.Count);
        var offsets = new int[bodies.Count];
        var offset = header.Length;
        for (var b = 0; b < bodies.Count; b++)
        {
            offsets[b] = offset;
            offset += bodies[b].Length;
        }
        for (var i = 0; i < records.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4 + i * 8), (ushort)records[i].Platform);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6 + i * 8), (ushort)records[i].Encoding);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8 + i * 8), (uint)offsets[records[i].Body]);
        }
        output.Write(header);
        foreach (var body in bodies) output.Write(body);
        return output.ToArray();
    }

    /// <summary>Codes read from all cmap subtables together when the cmap is written again.</summary>
    private const int MaxCmapCodes = 1_000_000;

    /// <summary>
    /// The format 14 subtable of the font, keeping only what the glyphs kept show (cmap 9.4.10): a default sequence where the Unicode subtables
    /// still map its base character (to a glyph kept), a non-default one where its glyph is kept; selectors left without sequences are dropped.
    /// Selectors, ranges and base characters ascending and each once.
    /// </summary>
    /// <returns>The subtable; null if no sequence is left.</returns>
    private static byte[]? Format14Of(OpenTypeFont font, HashSet<int> keep, ref int budget)
    {
        var records = new List<(int Selector, List<(int Start, int Extra)> Ranges, List<(int Code, int Glyph)> Mappings)>();
        foreach (var (selector, defaults, nonDefaults) in font.VariationSequences(ref budget).DistinctBy(s => s.Selector).OrderBy(s => s.Selector))
        {
            if (selector > 0xFFFFFF) continue;
            var ranges = new List<(int Start, int Extra)>();
            foreach (var code in defaults.Distinct().Order())
            {
                if (font.GlyphForUnicode(code) is var g && (g <= 0 || !keep.Contains(g))) continue;
                if (ranges.Count > 0 && ranges[^1].Start + ranges[^1].Extra + 1 == code && ranges[^1].Extra < 255) ranges[^1] = (ranges[^1].Start, ranges[^1].Extra + 1);
                else ranges.Add((code, 0));
            }
            var mappings = nonDefaults.Where(m => m.Glyph > 0 && keep.Contains(m.Glyph)).DistinctBy(m => m.Code).OrderBy(m => m.Code).ToList();
            if (ranges.Count > 0 || mappings.Count > 0) records.Add((selector, ranges, mappings));
        }
        if (records.Count == 0) return null;
        using var output = new MemoryStream();
        using var tables = new MemoryStream();
        var head = new byte[10 + 11 * records.Count];
        BinaryPrimitives.WriteUInt16BigEndian(head, 14);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(6), (uint)records.Count);
        Span<byte> b = stackalloc byte[5];
        for (var i = 0; i < records.Count; i++)
        {
            var (selector, ranges, mappings) = records[i];
            var at = head.AsSpan(10 + 11 * i);
            at[0] = (byte)(selector >> 16);
            BinaryPrimitives.WriteUInt16BigEndian(at[1..], (ushort)selector);
            if (ranges.Count > 0)
            {
                BinaryPrimitives.WriteUInt32BigEndian(at[3..], (uint)(head.Length + tables.Length));
                BinaryPrimitives.WriteUInt32BigEndian(b, (uint)ranges.Count);
                tables.Write(b[..4]);
                foreach (var (start, extra) in ranges)
                {
                    b[0] = (byte)(start >> 16);
                    BinaryPrimitives.WriteUInt16BigEndian(b[1..], (ushort)start);
                    b[3] = (byte)extra;
                    tables.Write(b[..4]);
                }
            }
            if (mappings.Count > 0)
            {
                BinaryPrimitives.WriteUInt32BigEndian(at[7..], (uint)(head.Length + tables.Length));
                BinaryPrimitives.WriteUInt32BigEndian(b, (uint)mappings.Count);
                tables.Write(b[..4]);
                foreach (var (code, glyph) in mappings)
                {
                    b[0] = (byte)(code >> 16);
                    BinaryPrimitives.WriteUInt16BigEndian(b[1..], (ushort)code);
                    BinaryPrimitives.WriteUInt16BigEndian(b[3..], (ushort)glyph);
                    tables.Write(b);
                }
            }
        }
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(2), (uint)(head.Length + tables.Length));
        output.Write(head);
        tables.Position = 0;
        tables.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Runs of consecutive codes with consecutive glyphs.</summary>
    private static List<(int First, int Last, int Glyph)> Runs(List<(int Code, int Glyph)> map)
    {
        var runs = new List<(int, int, int)>();
        foreach (var (code, glyph) in map.OrderBy(m => m.Code))
        {
            if (runs.Count > 0 && runs[^1].Item2 == code - 1 && runs[^1].Item3 + (code - runs[^1].Item1) == glyph) runs[^1] = (runs[^1].Item1, code, runs[^1].Item3);
            else runs.Add((code, code, glyph));
        }
        return runs;
    }

    private static byte[] Format4Of(List<(int First, int Last, int Glyph)> all, int language = 0)
    {
        // code 0xFFFF is the closing segment's
        var runs = all.Where(r => r.First < 0xFFFF).Select(r => (r.First, Math.Min(r.Last, 0xFFFE), r.Glyph)).ToList();
        runs.Add((0xFFFF, 0xFFFF, 0));   // the closing segment
        var segments = runs.Count;
        var entrySelector = (int)Math.Floor(Math.Log2(segments));
        var searchRange = 2 * (1 << entrySelector);
        var body = new byte[16 + segments * 8];
        BinaryPrimitives.WriteUInt16BigEndian(body, 4);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2), (ushort)Math.Min(body.Length, 0xFFFF));
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(4), (ushort)language);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(6), (ushort)(segments * 2));
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(8), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(10), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(12), (ushort)(segments * 2 - searchRange));
        for (var i = 0; i < segments; i++)
        {
            var (first, last, glyph) = runs[i];
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(14 + i * 2), (ushort)last);                           // endCode
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(16 + segments * 2 + i * 2), (ushort)first);          // startCode
            BinaryPrimitives.WriteInt16BigEndian(body.AsSpan(16 + segments * 4 + i * 2),                            // idDelta
                (short)(first == 0xFFFF ? 1 : (glyph - first) & 0xFFFF));
            // idRangeOffset stays 0
        }
        return body;
    }

    /// <summary>
    /// Format 4 for many runs: a segment per span of consecutive codes, with idDelta where its glyphs are consecutive and else an entry per
    /// code in the glyph array (idRangeOffset, cmap 9.4.4); null if even that exceeds the 64 KB of the format.
    /// </summary>
    private static byte[]? Format4WithArray(List<(int Code, int Glyph)> map, int language)
    {
        var bmp = map.Where(m => m.Code < 0xFFFF).ToList();
        var segments = new List<(int First, int Last, int Delta, int[]? Glyphs)>();
        for (var i = 0; i < bmp.Count;)
        {
            var j = i;
            while (j + 1 < bmp.Count && bmp[j + 1].Code == bmp[j].Code + 1) j++;
            var runs = new List<(int From, int To)>();
            for (var k = i; k <= j;)
            {
                var e = k;
                while (e + 1 <= j && bmp[e + 1].Glyph == bmp[e].Glyph + 1) e++;
                runs.Add((k, e));
                k = e + 1;
            }
            // the smaller form: a segment per run, or one segment with an entry per code
            if (runs.Count * 8 <= 8 + 2 * (j - i + 1))
                foreach (var (from, to) in runs) segments.Add((bmp[from].Code, bmp[to].Code, (bmp[from].Glyph - bmp[from].Code) & 0xFFFF, null));
            else
                segments.Add((bmp[i].Code, bmp[j].Code, 0, bmp.Skip(i).Take(j - i + 1).Select(m => m.Glyph).ToArray()));
            i = j + 1;
        }
        segments.Add((0xFFFF, 0xFFFF, 1, null));   // the closing segment
        var n = segments.Count;
        var length = 16 + n * 8 + 2 * segments.Sum(s => s.Glyphs?.Length ?? 0);
        if (length > 0xFFFF) return null;
        var body = new byte[length];
        var entrySelector = (int)Math.Floor(Math.Log2(n));
        var searchRange = 2 * (1 << entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(body, 4);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2), (ushort)length);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(4), (ushort)language);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(6), (ushort)(n * 2));
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(8), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(10), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(12), (ushort)(n * 2 - searchRange));
        var array = 16 + n * 8;
        for (var s = 0; s < n; s++)
        {
            var (first, last, delta, glyphs) = segments[s];
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(14 + s * 2), (ushort)last);
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(16 + n * 2 + s * 2), (ushort)first);
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(16 + n * 4 + s * 2), (ushort)delta);
            if (glyphs is null) continue;
            var rangeOffsetAt = 16 + n * 6 + s * 2;
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(rangeOffsetAt), (ushort)(array - rangeOffsetAt));   // from the entry itself
            foreach (var g in glyphs)
            {
                BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(array), (ushort)g);
                array += 2;
            }
        }
        return body;
    }

    /// <summary>Format 13 (many-to-one, cmap 9.4.9): a group per span of consecutive codes with the same glyph.</summary>
    private static byte[] Format13Of(List<(int Code, int Glyph)> map, int language)
    {
        var groups = new List<(int First, int Last, int Glyph)>();
        foreach (var (code, glyph) in map)
            if (groups.Count > 0 && groups[^1].Last == code - 1 && groups[^1].Glyph == glyph) groups[^1] = (groups[^1].First, code, glyph);
            else groups.Add((code, code, glyph));
        var body = new byte[16 + groups.Count * 12];
        BinaryPrimitives.WriteUInt16BigEndian(body, 13);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4), (uint)body.Length);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8), (uint)language);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(12), (uint)groups.Count);
        for (var i = 0; i < groups.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16 + i * 12), (uint)groups[i].First);
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(20 + i * 12), (uint)groups[i].Last);
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(24 + i * 12), (uint)groups[i].Glyph);
        }
        return body;
    }

    /// <summary>Format 6 (trimmed table, cmap 9.4.5) from the first to the last code; null if it would exceed 64 KB or nothing is mapped.</summary>
    private static byte[]? Format6Of(List<(int Code, int Glyph)> map, int language)
    {
        if (map.Count == 0) return null;
        int first = map[0].Code, count = map[^1].Code - first + 1;
        if (10 + 2L * count > 0xFFFF) return null;
        var body = new byte[10 + 2 * count];
        BinaryPrimitives.WriteUInt16BigEndian(body, 6);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2), (ushort)body.Length);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(4), (ushort)language);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(6), (ushort)first);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(8), (ushort)count);
        foreach (var (code, glyph) in map) BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(10 + 2 * (code - first)), (ushort)glyph);
        return body;
    }

    private static byte[] Format12Of(List<(int First, int Last, int Glyph)> runs, int language = 0)
    {
        var body = new byte[16 + runs.Count * 12];
        BinaryPrimitives.WriteUInt16BigEndian(body, 12);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4), (uint)body.Length);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8), (uint)language);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(12), (uint)runs.Count);
        for (var i = 0; i < runs.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16 + i * 12), (uint)runs[i].First);
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(20 + i * 12), (uint)runs[i].Last);
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(24 + i * 12), (uint)runs[i].Glyph);
        }
        return body;
    }

    /// <summary>
    /// The first font of a TrueType collection ('ttcf') as a font file of its own — PDF does not allow collections as embedded programs
    /// (ISO 32000-1, 9.9), and the display reads the first font of one.
    /// </summary>
    /// <param name="collection">The collection.</param>
    /// <returns>The font file.</returns>
    /// <exception cref="FontFormatException">It is no collection, or its first font cannot be read.</exception>
    internal static byte[] FirstOfCollection(byte[] collection)
    {
        if (collection.Length < 16 || !collection.AsSpan(0, 4).SequenceEqual("ttcf"u8)) throw new FontFormatException("keine Sammlung");
        var start = BinaryPrimitives.ReadUInt32BigEndian(collection.AsSpan(12));
        if (start > collection.Length - 12) throw new FontFormatException("Sammlung beschädigt");
        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (tag, (offset, length)) in ReadDirectory(collection, (int)start))
            if (tag != "DSIG") data[tag] = collection.AsSpan(offset, length).ToArray();
        // Finish writes head for long loca offsets: short ones become long (OpenType head 23: indexToLocFormat names the loca format) —
        // before, head and loca disagreed, and the font could not be rebuilt for a PDF/A copy (No. 1143)
        if (data.TryGetValue("head", out var head) && head.Length >= 54 && BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(50)) == 0 &&
            data.TryGetValue("loca", out var loca))
        {
            var wide = new byte[loca.Length / 2 * 4];
            for (var i = 0; i + 1 < loca.Length; i += 2)
                BinaryPrimitives.WriteUInt32BigEndian(wide.AsSpan(i * 2), BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(i)) * 2u);
            data["loca"] = wide;
        }
        return Finish(data);
    }

    private static Dictionary<string, (int Offset, int Length)> ReadDirectory(byte[] font, int start = 0)
    {
        var tables = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        if (font.Length < start + 12) throw new FontFormatException("zu kurz");
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(start + 4));
        for (var i = 0; i < numTables; i++)
        {
            var at = start + 12 + i * 16;
            if (at + 16 > font.Length) break;   // directory truncated: use what is there
            var tag = System.Text.Encoding.ASCII.GetString(font, at, 4);
            var offset = (long)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(at + 8));
            var length = (long)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(at + 12));
            // of two records with one tag the first, as the display reads it — before the last, and a subset or a cleaned font could take
            // another table than the one shown (docs/FEHLERGEDAECHTNIS.md, No. 1149); one that reaches beyond the file cut to its end, as the
            // display reads it too — before passed over, and the writers took a second record of the tag (No. 1156)
            if (offset <= font.Length) tables.TryAdd(tag, ((int)offset, (int)Math.Min(length, font.Length - offset)));
        }
        return tables;
    }

    /// <summary>
    /// End of every glyph in glyf. loca need not be ascending (test file issue11131): if the next offset is smaller than the start
    /// (or invalid), the glyph extends to the next larger start, otherwise to the end of glyf; the reader reads it the same way (as
    /// common font engines do). Otherwise such glyphs become empty when subsetting, and most letters are missing after lossless compression.
    /// </summary>
    private static int[] GlyphEnds(int[] offsets, int count, int glyfLength)
    {
        var starts = offsets.Where(o => o >= 0).Append(glyfLength).Distinct().Order().ToArray();
        var ends = new int[count];
        for (var g = 0; g < count; g++)
        {
            var (start, end) = (offsets[g], offsets[g + 1]);
            if (start < 0) ends[g] = -1;
            else if (end >= start) ends[g] = end;   // equal: empty (space)
            else
            {
                var index = Array.BinarySearch(starts, start);
                ends[g] = index >= 0 && index + 1 < starts.Length ? starts[index + 1] : glyfLength;
            }
        }
        return ends;
    }

    /// <summary>Components of a composite glyph (glyf data with a negative contour count).</summary>
    private static IEnumerable<int> Components(byte[] font, int glyfOffset, int[] offsets, int[] ends, int glyph)
    {
        if (offsets[glyph] < 0 || ends[glyph] <= offsets[glyph]) yield break;
        var start = glyfOffset + offsets[glyph];
        if (start + 10 > font.Length) yield break;
        if (BinaryPrimitives.ReadInt16BigEndian(font.AsSpan(start)) >= 0) yield break;   // simple glyph
        var p = start + 10;
        for (var guard = 0; guard < 256 && p + 4 <= font.Length; guard++)
        {
            int flags = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(p));
            yield return BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(p + 2));
            p += 4 + ((flags & 0x0001) != 0 ? 4 : 2);                       // arguments: words or bytes
            if ((flags & 0x0008) != 0) p += 2;                              // one scale
            else if ((flags & 0x0040) != 0) p += 4;                         // x and y scale
            else if ((flags & 0x0080) != 0) p += 8;                         // 2×2 matrix
            if ((flags & 0x0020) == 0) yield break;                        // no more components
        }
    }

    /// <summary>
    /// Assembles the file from its tables: directory, aligned tables, checksums. Its version tells the outlines (ISO/IEC 14496-22, 5.2):
    /// "OTTO" with CFF outlines, 1.0 with TrueType outlines — with CFF under 1.0 a reference reader took the font for TrueType
    /// (docs/FEHLERGEDAECHTNIS.md, No. 411).
    /// </summary>
    internal static byte[] Assemble(Dictionary<string, byte[]> tables)
    {
        var tags = tables.Keys.Order(StringComparer.Ordinal).ToList();   // directory sorted by tag
        var n = tags.Count;
        var entrySelector = (int)Math.Floor(Math.Log2(n));
        var searchRange = (1 << entrySelector) * 16;
        var headerLength = 12 + n * 16;
        var total = headerLength + tables.Values.Sum(t => (t.Length + 3) & ~3);
        var result = new byte[total];
        var cff = !tables.ContainsKey("glyf") && (tables.ContainsKey("CFF ") || tables.ContainsKey("CFF2"));
        BinaryPrimitives.WriteUInt32BigEndian(result, cff ? 0x4F54544Fu : 0x00010000u);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), (ushort)n);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(10), (ushort)(n * 16 - searchRange));
        // the data in the order OpenType recommends for TrueType or CFF outlines (OpenType 3.26), the other tables after them by tag — before
        // sorted by tag like the directory (docs/FEHLERGEDAECHTNIS.md, No. 1151)
        var recommended = cff ? CffOrder : TrueTypeOrder;
        var dataOrder = tags.OrderBy(t => Array.IndexOf(recommended, t) is >= 0 and var at ? at : recommended.Length).ThenBy(t => t, StringComparer.Ordinal);
        var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
        var offset = headerLength;
        var headOffset = -1;
        foreach (var tag in dataOrder)
        {
            offsets[tag] = offset;
            tables[tag].CopyTo(result, offset);
            if (tag == "head") headOffset = offset;
            offset += (tables[tag].Length + 3) & ~3;
        }
        for (var i = 0; i < n; i++)
        {
            var bytes = tables[tags[i]];
            var entry = 12 + i * 16;
            System.Text.Encoding.ASCII.GetBytes(tags[i], result.AsSpan(entry, 4));
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(entry + 4), Checksum(bytes));
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(entry + 8), (uint)offsets[tags[i]]);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(entry + 12), (uint)bytes.Length);
        }
        if (headOffset >= 0)
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(headOffset + 8), 0xB1B0AFBA - Checksum(result));
        return result;
    }

    private static readonly string[] TrueTypeOrder =
        ["head", "hhea", "maxp", "OS/2", "hmtx", "LTSH", "VDMX", "hdmx", "cmap", "fpgm", "prep", "cvt ", "loca", "glyf", "kern", "name", "post", "gasp", "PCLT", "DSIG"];

    private static readonly string[] CffOrder = ["head", "hhea", "maxp", "OS/2", "name", "cmap", "post", "CFF "];

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (var k = 0; k < 4; k++) word = (word << 8) | (i + k < data.Length ? data[i + k] : (byte)0);
            sum += word;
        }
        return sum;
    }
}
