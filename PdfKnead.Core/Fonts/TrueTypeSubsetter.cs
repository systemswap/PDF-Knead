using System.Buffers.Binary;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Subset of a TrueType font for embedding (9.9): the glyph indices stay the same, only
/// unused glyphs become empty, so text, widths (/W) and CIDToGIDMap Identity fit without
/// conversion. Composite glyphs keep their components. Only the tables a PDF reader needs
/// are copied (no cmap, name, post names, kerning or OpenType layout).
/// </summary>
public static class TrueTypeSubsetter
{
    // Order as recommended by the OpenType specification ("Recommendations")
    private static readonly string[] Keep = ["head", "hhea", "maxp", "OS/2", "hmtx", "cvt ", "fpgm", "prep", "loca", "glyf"];

    /// <summary>Builds a subset with the glyphs <paramref name="glyphs"/> (glyph 0 is always included).</summary>
    /// <param name="font">The TrueType font file (a single font, not a collection).</param>
    /// <param name="glyphs">Glyph indices to keep; indices out of range are ignored.</param>
    /// <param name="keepAllTables">
    /// Keep all tables, including cmap, name, post and layout (when compressing fonts of existing documents): only the outlines of unused
    /// glyphs become empty, so every reader finds the characters as before. The signature (DSIG) is dropped because it would no longer match.
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

    private static byte[] Subset(byte[] font, IEnumerable<int> glyphs, string[]? kept)
    {
        var (tables, keep, newGlyf, newLoca) = Outlines(font, glyphs);
        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var tag in kept ?? tables.Keys.Where(t => t != "DSIG").ToArray())
        {
            if (tag == "glyf") data[tag] = newGlyf;
            else if (tag == "loca") data[tag] = newLoca;
            else if (tables.TryGetValue(tag, out var t)) data[tag] = font.AsSpan(t.Offset, t.Length).ToArray();
        }
        return Finish(data);
    }

    /// <summary>
    /// Redaction: the font keeps only what its glyphs <paramref name="glyphs"/> need (glyph 0 and the components of composite glyphs stay),
    /// at the same glyph indices — the outlines of the others become empty, and nothing else in the file tells of them: the cmap maps only
    /// the glyphs kept, glyph names (post format 2) and metrics (hmtx, vmtx) of the others are cleared, and the tables that list pairs or
    /// classes of glyphs (kerning, OpenType layout) are left out — a PDF reader positions every glyph itself. A character that was only
    /// in redacted text can then not be read from the font (docs/FEHLERGEDAECHTNIS.md, No. 169).
    /// </summary>
    /// <remarks>An OpenType font with CFF outlines (a 'CFF ' table) is scrubbed the same way, its outlines by <see cref="CffScrubber"/>.</remarks>
    /// <param name="font">The TrueType or OpenType font file.</param>
    /// <param name="glyphs">Glyph indices still shown.</param>
    /// <returns>The font file.</returns>
    /// <exception cref="FontFormatException">The font has neither glyf, loca and head nor a CFF table, or head or loca is too short.</exception>
    public static byte[] Scrub(byte[] font, IEnumerable<int> glyphs)
    {
        Dictionary<string, (int Offset, int Length)> tables;
        HashSet<int> keep;
        var outlines = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int count;
        var directory = ReadDirectory(font);
        if (!directory.ContainsKey("glyf") && directory.TryGetValue("CFF ", out var cffTable))
        {
            (outlines["CFF "], keep) = CffScrubber.Scrub(new ArraySegment<byte>(font, cffTable.Offset, cffTable.Length), glyphs);
            tables = directory;
            count = directory.TryGetValue("maxp", out var maxp) && maxp.Length >= 6 ? BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(maxp.Offset + 4)) : 0;
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
        if (tables.ContainsKey("cmap")) data["cmap"] = CmapOf(OpenTypeFont.Parse(font), keep);
        return Finish(data);
    }

    /// <summary>Tables that list glyphs in pairs, classes or per size, or a signature: left out when scrubbing.</summary>
    private static readonly HashSet<string> Dropped = new(StringComparer.Ordinal)
    {
        "DSIG", "kern", "kerx", "GSUB", "GPOS", "GDEF", "JSTF", "BASE", "MATH", "morx", "mort", "LTSH", "hdmx", "VDMX", "feat", "prop", "trak",
        "just", "opbd", "bsln", "lcar", "Zapf", "fdsc", "COLR", "CPAL", "sbix", "CBDT", "CBLC", "EBDT", "EBLC", "EBSC", "SVG ", "meta",
    };

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
        data["hmtx"] = newMetrics;
        data["loca"] = newLoca;
        data["glyf"] = newGlyf.ToArray();
        data["post"] = newPost;
        if (cmap is not null) data["cmap"] = SymbolCmap(cmap);
        else if (keepCmap && tables.TryGetValue("cmap", out var own)) data["cmap"] = font.AsSpan(own.Offset, own.Length).ToArray();
        return Finish(data);
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
    /// written again — gigabytes from a few hundred bytes (No. 1138).
    /// </summary>
    private static byte[] CmapOf(OpenTypeFont font, HashSet<int> keep)
    {
        var budget = MaxCmapCodes;
        var bodies = new List<byte[]>();
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
            // a subtable of a format the reader does not read (2, 8, 10, 14 …) is left out: written as an empty format 4 it lost every
            // mapping, and under (0,5) only format 14 is allowed (cmap 9.3.2) (No. 1143)
            if (format is not (0 or 4 or 6 or 12 or 13)) continue;
            // a Macintosh subtable keeps its language (cmap 9.4.1: the Macintosh language code plus one, 0 for any language)
            var language = platform != 1 ? 0 : format is 12 or 13
                ? source + 12 <= data.Length ? (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(source + 8)) : 0
                : source + 6 <= data.Length ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(source + 4)) : 0;
            if (!bodyOf.TryGetValue((source, wide), out var body))
            {
                var map = new List<(int Code, int Glyph)>();
                foreach (var m in font.Mappings(platform, encoding))
                {
                    if (--budget < 0) break;
                    if (keep.Contains(m.Glyph)) map.Add(m);
                }
                var runs = Runs(map);
                var long12 = wide || map.Any(m => m.Code > 0xFFFF) || runs.Count > 8000;
                bodyOf[(source, wide)] = body = bodies.Count;
                bodies.Add(long12 ? Format12Of(runs, language) : Format4Of(runs, language));
            }
            records.Add((platform, encoding, body));
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
            if (offset + length <= font.Length) tables[tag] = ((int)offset, (int)length);
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
        var offset = headerLength;
        var headOffset = -1;
        for (var i = 0; i < n; i++)
        {
            var bytes = tables[tags[i]];
            var entry = 12 + i * 16;
            System.Text.Encoding.ASCII.GetBytes(tags[i], result.AsSpan(entry, 4));
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(entry + 4), Checksum(bytes));
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(entry + 8), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(entry + 12), (uint)bytes.Length);
            bytes.CopyTo(result, offset);
            if (tags[i] == "head") headOffset = offset;
            offset += (bytes.Length + 3) & ~3;
        }
        if (headOffset >= 0)
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(headOffset + 8), 0xB1B0AFBA - Checksum(result));
        return result;
    }

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
