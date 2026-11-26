using System.Collections.Concurrent;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// TrueType and OpenType font (sfnt): table directory, cmap, glyf/loca (including
/// composite glyphs), hmtx, post names, name, OS/2 and CFF outlines. Lenient, because
/// subset fonts embedded in PDF files often contain only the most necessary tables.
/// </summary>
/// <remarks>
/// TrueType outlines are built on first use and cached up to 64 MB per font. Hinting instructions are skipped, never executed.
/// </remarks>
public sealed class OpenTypeFont
{
    private const int MaxComponentDepth = 8;

    private readonly byte[] _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables = new(StringComparer.Ordinal);
    private readonly int[] _loca = [];
    private readonly ushort[] _advances = [];
    private readonly List<(int Platform, int Encoding, int Offset)> _cmapRecords = [];
    private readonly ConcurrentDictionary<int, CmapTable> _cmaps = new();
    /// <summary>Outlines per glyph, at most 64 MB (<see cref="BoundedCache{TKey, TValue}"/>).</summary>
    private readonly BoundedCache<int, GlyphOutline> _outlines = new(64L << 20, o => o.EstimatedBytes);

    /// <summary>Bytes of cached outlines (for tests).</summary>
    internal long CachedOutlineBytes => _outlines.Bytes;
    private string[]? _postNames;
    private Dictionary<string, int>? _nameToGlyph;

    private OpenTypeFont(byte[] data, int offset)
    {
        _data = data;
        var r = new FontReader(data, offset);
        var version = r.U32();
        if (version is not (0x00010000 or 0x74727565 or 0x4F54544F)) // 1.0, "true", "OTTO"
            throw new FontFormatException("keine TrueType/OpenType-Schrift");
        int numTables = r.U16();
        r.Skip(6);
        var intact = r.CanRead(16 * numTables);
        for (var i = 0; i < numTables; i++)
        {
            var tag = r.Tag(r.Position);
            r.Skip(8);
            var tableOffset = (int)r.U32();
            var length = (int)r.U32();
            // what reaches beyond the data is read as far as it goes (the display draws what it can); a strict reader refuses the program
            intact &= tableOffset >= 0 && length >= 0 && (long)tableOffset + length <= data.Length;
            if (tableOffset < 0 || length < 0 || tableOffset > data.Length) continue;
            _tables.TryAdd(tag, (tableOffset, Math.Min(length, data.Length - tableOffset)));
        }
        Intact = intact && HasTable("head") && HasTable("maxp") && (HasTable("CFF ") || HasTable("loca") && HasTable("glyf"));

        if (Table("head") is { } head)
        {
            var h = new FontReader(data, head.Offset + 18);
            if (h.CanRead(2) && head.Length >= 20)
            {
                var units = (int)h.U16();
                UnitsPerEm = Math.Clamp(units, 16, 16384);
                Broken = units is < 16 or > 16384;
            }
            else Broken = true;
            h.Position = head.Offset + 50;
            // Every value other than 0 means long offsets (as in common font engines; the test file issue2537r has 256)
            if (h.CanRead(2) && head.Length >= 52) LongLoca = h.I16() != 0;
        }
        // the field only when the table holds it: checked against the file alone, a maxp of 5 bytes gave its fifth byte and the padding after
        // it as the glyph count (docs/FEHLERGEDAECHTNIS.md, No. 1149)
        if (Table("maxp") is { Length: >= 6 } maxp && new FontReader(data, maxp.Offset + 4) is { } m && m.CanRead(2))
            GlyphCount = m.U16();

        if (Table("hhea") is { } hhea && Table("hmtx") is { } hmtx)
        {
            var hh = new FontReader(data, hhea.Offset + 4);
            if (hh.CanRead(4))
            {
                Ascent = hh.I16();
                Descent = hh.I16();
            }
            hh.Position = hhea.Offset + 34;
            var metrics = hh.CanRead(2) && hhea.Length >= 36 ? hh.U16() : 0;
            metrics = Math.Min(metrics, hmtx.Length / 4);
            if (metrics > 0)
            {
                _advances = new ushort[metrics];
                var hr = new FontReader(data, hmtx.Offset);
                for (var i = 0; i < metrics; i++)
                {
                    _advances[i] = hr.U16();
                    hr.Skip(2);
                }
            }
        }

        if (Table("loca") is { } loca)
        {
            // one offset per glyph and one after the last: a shorter table leaves glyphs without outlines (strict readers refuse it)
            if (GlyphCount > 0 && loca.Length / (LongLoca ? 4 : 2) < GlyphCount + 1) Intact = false;
            var count = LocaEntries(GlyphCount, loca.Length, LongLoca);
            _loca = new int[Math.Max(0, count)];
            var lr = new FontReader(data, loca.Offset);
            for (var i = 0; i < _loca.Length; i++) _loca[i] = LongLoca ? (int)lr.U32() : lr.U16() * 2;
            if (GlyphCount == 0) GlyphCount = Math.Max(0, _loca.Length - 1);
        }

        if (Table("cmap") is { } cmap)
        {
            var cr = new FontReader(data, cmap.Offset + 2);
            if (cr.CanRead(2))
            {
                int n = cr.U16();
                // Only the first record per platform and encoding (only that one is ever used), at most MaxCmapRecords: crafted fonts
                // list tens of thousands, and every glyph lookup tried them all, at 50 … 150 ms per glyph
                for (var i = 0; i < n && cr.CanRead(8) && _cmapRecords.Count < MaxCmapRecords; i++)
                {
                    int platform = cr.U16();
                    int encoding = cr.U16();
                    var sub = (int)cr.U32();
                    if (CmapIndex(platform, encoding) < 0) _cmapRecords.Add((platform, encoding, cmap.Offset + sub));
                }
                _cmapSubtables = _cmapRecords.Select(c => (c.Platform, c.Encoding)).ToArray();
            }
        }

        if (Table("CFF ") is { } cff)
        {
            try
            {
                Cff = CffFont.Parse(new ArraySegment<byte>(data, cff.Offset, cff.Length));
                if (GlyphCount == 0) GlyphCount = Cff.GlyphCount;
            }
            catch (FontFormatException)
            {
            }
        }
        // CFF2 (OpenType 8), the outlines of variable CFF fonts: drawn in the default instance — before not read, and the text of such a font
        // stayed invisible (docs/FEHLERGEDAECHTNIS.md, No. 1158). Only without 'CFF ' and glyf, which common font engines take first
        else if (Table("CFF2") is { } cff2 && !(HasTable("glyf") && HasTable("loca")))
        {
            try
            {
                Cff2 = CffFont.ParseCff2(new ArraySegment<byte>(data, cff2.Offset, cff2.Length), UnitsPerEm);
                if (GlyphCount == 0) GlyphCount = Cff2.GlyphCount;
            }
            catch (FontFormatException)
            {
            }
        }

        if (Table("OS/2") is { } os2)
        {
            var o = new FontReader(data, os2.Offset + 4);
            if (o.CanRead(2) && os2.Length >= 6) WeightClass = o.U16();
            o.Position = os2.Offset + 62;
            if (o.CanRead(2) && os2.Length >= 64) Selection = o.U16();
        }
        FamilyName = ReadName(1) ?? ReadName(4) ?? "";
        PostScriptName = ReadName(6) ?? "";
    }

    /// <summary>Parses a font; from a collection (.ttc) the font number <paramref name="index"/>.</summary>
    /// <param name="data">The font file; the font keeps a reference to it and reads tables from it on demand.</param>
    /// <param name="index">Index of the font in a collection; an invalid index selects the first font. Ignored for single fonts.</param>
    /// <returns>The parsed font. Check <see cref="Broken"/> before using it.</returns>
    /// <exception cref="FontFormatException">The data is not a TrueType/OpenType font or is too short.</exception>
    public static OpenTypeFont Parse(byte[] data, int index = 0)
    {
        if (data.Length >= 16 && new FontReader(data).Tag(0) == "ttcf")
        {
            var r = new FontReader(data, 8);
            var count = (int)r.U32();
            r.Position = 12 + 4 * (index >= 0 && index < count ? index : 0);
            return new OpenTypeFont(data, (int)r.U32());
        }
        return new OpenTypeFont(data, 0);
    }

    /// <summary>Checks whether data looks like TrueType/OpenType (for FontFile entries with the wrong type).</summary>
    /// <param name="data">The start of the font data.</param>
    /// <returns>True for the signatures 0x00010000, "true", "OTTO" and "ttcf".</returns>
    public static bool LooksLikeSfnt(ReadOnlySpan<byte> data) =>
        data.Length >= 12 && (data[..4].SequenceEqual((byte[])[0, 1, 0, 0]) || data[..4].SequenceEqual("true"u8)
                              || data[..4].SequenceEqual("OTTO"u8) || data[..4].SequenceEqual("ttcf"u8));

    /// <summary>Units per em from the head table, clamped to 16 … 16384; 1000 if the font has no head table.</summary>
    public int UnitsPerEm { get; } = 1000;

    /// <summary>
    /// The head table is unusable (units per em outside 16 … 16384, or truncated). Usually the file is shifted or truncated and then
    /// none of the tables is right (test file bug1050040: one byte is missing in the stream, units per em read as 0, so the
    /// glyphs came out 64 times too large and nothing was visible).
    /// </summary>
    public bool Broken { get; }

    /// <summary>
    /// Every table lies wholly within the data, those a program needs are there (head, maxp, and the outlines: loca and glyf, or CFF), and loca
    /// has an offset for every glyph maxp counts —
    /// what a strict reader asks of a program before it reads it. A truncated program (its stream cut short) is not intact, though the display
    /// draws what it can of it (docs/FEHLERGEDAECHTNIS.md, No. 340).
    /// </summary>
    public bool Intact { get; }

    /// <summary>Whether the loca table uses 32-bit offsets (head.indexToLocFormat ≠ 0).</summary>
    public bool LongLoca { get; }

    /// <summary>Number of glyphs from maxp; without maxp from loca or the CFF table.</summary>
    public int GlyphCount { get; }

    /// <summary>
    /// The entries of loca that are read: one more than the glyphs of maxp, as far as loca reaches. Some fonts have a wrong or missing maxp
    /// table (0 glyphs): the loca table then determines the count. Shared by reading and by subsetting (<see cref="TrueTypeSubsetter"/>): a
    /// subset that took maxp's 0 wrote a font without glyphs (docs/FEHLERGEDAECHTNIS.md, No. 389).
    /// </summary>
    /// <param name="maxpGlyphs">The number of glyphs in maxp (0 if it has none).</param>
    /// <param name="locaLength">Length of the loca table in bytes.</param>
    /// <param name="longLoca">Whether loca holds 32-bit offsets.</param>
    /// <returns>The number of loca entries (glyphs + 1, or fewer if loca is too short); at most 65 536 — a glyph number has 16 bits, and
    /// without a count in maxp a loca of some megabytes (a stream of zeros) made millions of glyphs that the PDF/A check, the CIDSet and
    /// the subset went through (No. 1138).</returns>
    internal static int LocaEntries(int maxpGlyphs, int locaLength, bool longLoca) =>
        Math.Min(MaxLocaEntries, maxpGlyphs == 0 ? locaLength / (longLoca ? 4 : 2) : Math.Min(maxpGlyphs + 1, locaLength / (longLoca ? 4 : 2)));

    /// <summary>Glyph numbers 0 to 65 535 and the end of the last glyph.</summary>
    private const int MaxLocaEntries = 65_536;

    /// <summary>
    /// A name as a PostScript font name for /BaseFont: the characters 33–126 without [](){}&lt;&gt;/% and at most 63 of them (OpenType name,
    /// 30.4, name ID 6) — a name of the name table, or of a font file, was taken over with spaces, brackets and any length
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1145).
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The cleaned name; „Font“ if nothing is left.</returns>
    internal static string PostScriptSafe(string name)
    {
        var kept = new string(name.Where(c => c is >= '!' and <= '~' && "[](){}<>/%".IndexOf(c) < 0).Take(63).ToArray());
        return kept.Length > 0 ? kept : "Font";
    }

    /// <summary>Ascender from hhea in font units.</summary>
    public int Ascent { get; }

    /// <summary>Descender from hhea in font units (usually negative).</summary>
    public int Descent { get; }

    /// <summary>OS/2 usWeightClass; 400 if the font has no OS/2 table.</summary>
    public int WeightClass { get; } = 400;

    /// <summary>OS/2 fsSelection: bit 0 italic, bit 5 bold.</summary>
    public int Selection { get; }

    /// <summary>Family name (name ID 1, otherwise the full name, ID 4); empty if missing.</summary>
    public string FamilyName { get; }

    /// <summary>PostScript name (name ID 6); empty if missing.</summary>
    public string PostScriptName { get; }

    /// <summary>CFF outlines (OpenType "OTTO"), otherwise null (TrueType outlines in glyf).</summary>
    public CffFont? Cff { get; }

    /// <summary>
    /// CFF2 outlines (the table "CFF2" of a font without 'CFF ' and glyf, OpenType 8), drawn in the default instance; otherwise null. Unlike
    /// <see cref="Cff"/> CFF2 has no glyph names, charset or encoding of its own: the font finds its glyphs through cmap and post like a TrueType
    /// font and takes its advances from hmtx.
    /// </summary>
    /// <remarks>
    /// <see cref="Intact"/> stays false for such a font: a strict reader of PDF (ISO 32000-2, 9.9, Table 124 names "glyf" and "CFF " only) does not
    /// read CFF2, so PDF/A takes it as not embedded and rebuilds it from what the display draws.
    /// </remarks>
    public CffFont? Cff2 { get; }

    /// <summary>Maps font units to text space (1 = one em).</summary>
    public Matrix FontMatrix => Cff is { } cff ? cff.FontMatrix : Cff2 is { } cff2 ? cff2.FontMatrix : Matrix.Scale(1.0 / UnitsPerEm, 1.0 / UnitsPerEm);

    /// <summary>Whether the font has a table.</summary>
    /// <param name="tag">Four-character table tag, e.g. "glyf".</param>
    /// <returns>True if the table directory lists the tag.</returns>
    public bool HasTable(string tag) => _tables.ContainsKey(tag);

    /// <summary>Returns the position of a table in <see cref="FileData"/>.</summary>
    /// <param name="tag">Four-character table tag.</param>
    /// <returns>Offset and length in bytes (the length is cut off at the end of the file), or null if the table is missing.</returns>
    public (int Offset, int Length)? Table(string tag) => _tables.TryGetValue(tag, out var t) ? t : null;

    /// <summary>Returns the raw data of a table (GSUB, GPOS, sbix …).</summary>
    /// <param name="tag">Four-character table tag.</param>
    /// <returns>A view of the table inside the font file; empty if the table is missing.</returns>
    public ReadOnlySpan<byte> TableData(string tag) => Table(tag) is { } t ? _data.AsSpan(t.Offset, t.Length) : [];

    /// <summary>
    /// The whole font file (for collections the .ttc); must not be modified. Used for embedding and for tables
    /// that are read without a copy (<see cref="Table"/> gives their position).
    /// </summary>
    internal byte[] FileData => _data;

    /// <summary>Platform and encoding IDs of the cmap subtables, the first record of each pair only.</summary>
    public IReadOnlyList<(int Platform, int Encoding)> CmapSubtables => _cmapSubtables;

    private (int Platform, int Encoding)[] _cmapSubtables = [];

    /// <summary>Maximum number of cmap records (real fonts rarely have more than ten).</summary>
    private const int MaxCmapRecords = 64;

    // ------------------------------------------------------------ Character → glyph

    /// <summary>Returns the glyph for a Unicode character, using the Unicode cmaps in order of preference.</summary>
    /// <param name="codePoint">Unicode code point.</param>
    /// <returns>The glyph index; 0 if no Unicode cmap maps the character.</returns>
    public int GlyphForUnicode(int codePoint)
    {
        foreach (var (platform, encoding) in (ReadOnlySpan<(int, int)>)[(3, 10), (0, 4), (3, 1), (0, 3), (0, 6), (0, 1), (0, 0), (0, 2)])
            if (Lookup(platform, encoding, codePoint) is > 0 and var g) return g;
        return 0;
    }

    /// <summary>Looks up a code in a specific cmap (e.g. 3,0 for symbol fonts, 1,0 for Mac Roman).</summary>
    /// <param name="platform">Platform ID.</param>
    /// <param name="encoding">Encoding ID.</param>
    /// <param name="code">Character code in that cmap.</param>
    /// <returns>The glyph index (0 = not mapped), or -1 if the font has no such cmap.</returns>
    public int Lookup(int platform, int encoding, int code)
    {
        var index = CmapIndex(platform, encoding);
        if (index < 0) return -1;
        if (!_cmaps.TryGetValue(index, out var map)) map = _cmaps.GetOrAdd(index, i => CmapTable.Read(_data, _cmapRecords[i].Offset));
        return map.Map(code);
    }

    /// <summary>All codes a cmap subtable maps to a glyph (other than 0), with the glyph; at most a million.</summary>
    /// <param name="platform">Platform ID.</param>
    /// <param name="encoding">Encoding ID.</param>
    /// <returns>Code and glyph, ascending by code; nothing if the font has no such cmap.</returns>
    internal IEnumerable<(int Code, int Glyph)> Mappings(int platform, int encoding)
    {
        var index = CmapIndex(platform, encoding);
        if (index < 0) yield break;
        if (!_cmaps.TryGetValue(index, out var map)) map = _cmaps.GetOrAdd(index, i => CmapTable.Read(_data, _cmapRecords[i].Offset));
        var count = 0;
        foreach (var (first, last) in map.Ranges())
            for (var code = first; code <= last && count < 1_000_000; code++, count++)
                if (map.Map(code) is > 0 and var glyph) yield return (code, glyph);
    }

    /// <summary>Where the subtable of a cmap record lies in the file (records may share one).</summary>
    /// <param name="platform">Platform ID.</param>
    /// <param name="encoding">Encoding ID.</param>
    /// <returns>Its offset in the file; -1 if the font has no such record.</returns>
    internal int CmapOffset(int platform, int encoding) => CmapIndex(platform, encoding) is >= 0 and var i ? _cmapRecords[i].Offset : -1;

    /// <summary>Whether the font has a cmap for a platform and encoding.</summary>
    /// <param name="platform">Platform ID.</param>
    /// <param name="encoding">Encoding ID.</param>
    /// <returns>True if such a cmap subtable exists.</returns>
    public bool HasCmap(int platform, int encoding) => CmapIndex(platform, encoding) >= 0;

    // A loop instead of FindIndex with a lambda: this is asked several times per glyph
    private int CmapIndex(int platform, int encoding)
    {
        for (var i = 0; i < _cmapRecords.Count; i++)
            if (_cmapRecords[i].Platform == platform && _cmapRecords[i].Encoding == encoding) return i;
        return -1;
    }

    /// <summary>
    /// Whether a character is a variation selector (U+FE00–U+FE0F, U+E0100–U+E01EF, the Mongolian free variation selectors U+180B–U+180D and
    /// U+180F): it chooses a variant of the character before it (cmap format 14, OpenType 9.4.10) and has no glyph of its own.
    /// </summary>
    /// <param name="codePoint">Unicode code point.</param>
    internal static bool IsVariationSelector(int codePoint) =>
        codePoint is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF or >= 0x180B and <= 0x180D or 0x180F;

    /// <summary>
    /// The glyph of a Unicode variation sequence (base character and variation selector) from the format 14 subtable under (0,5) (OpenType
    /// 9.4.10): for a default sequence the glyph of the base character from the Unicode subtables, for a non-default one the glyph the table
    /// names (docs/FEHLERGEDAECHTNIS.md, No. 1160).
    /// </summary>
    /// <param name="baseChar">The base character.</param>
    /// <param name="selector">The variation selector.</param>
    /// <returns>The glyph index; -1 if the font has no format 14 subtable or it does not list the sequence.</returns>
    internal int GlyphForVariation(int baseChar, int selector)
    {
        var index = CmapIndex(0, 5);
        if (index < 0) return -1;
        if (!_cmaps.TryGetValue(index, out var map)) map = _cmaps.GetOrAdd(index, i => CmapTable.Read(_data, _cmapRecords[i].Offset));
        if (map is not Format14 variations) return -1;
        var glyph = variations.Variation(baseChar, selector);
        return glyph == 0 ? GlyphForUnicode(baseChar) : glyph;
    }

    /// <summary>
    /// The glyph of the first character of a text (the text of a code from /ToUnicode, when a substitute font draws it); if a variation selector
    /// follows it and the font lists the sequence (<see cref="GlyphForVariation"/>), the glyph of the sequence.
    /// </summary>
    /// <param name="text">The text; its first character counts, a variation selector after it chooses the variant.</param>
    /// <returns>The glyph index; 0 if the font lacks the character.</returns>
    internal int GlyphForText(string text)
    {
        if (System.Text.Rune.DecodeFromUtf16(text, out var first, out var used) != System.Buffers.OperationStatus.Done) return 0;
        if (used < text.Length && System.Text.Rune.DecodeFromUtf16(text.AsSpan(used), out var next, out _) == System.Buffers.OperationStatus.Done
            && IsVariationSelector(next.Value) && GlyphForVariation(first.Value, next.Value) is > 0 and var variant)
            return variant;
        return GlyphForUnicode(first.Value);
    }

    /// <summary>
    /// The variation sequences of the format 14 subtable under (0,5), for writing them again: per selector the base characters of the default
    /// sequences and the non-default ones with their glyphs, ascending; together at most <paramref name="budget"/> characters.
    /// </summary>
    /// <param name="budget">Characters left to read; decreased by those read.</param>
    /// <returns>Nothing if the font has no such subtable.</returns>
    internal List<(int Selector, List<int> Defaults, List<(int Code, int Glyph)> NonDefaults)> VariationSequences(ref int budget)
    {
        var result = new List<(int, List<int>, List<(int, int)>)>();
        var index = CmapIndex(0, 5);
        if (index < 0) return result;
        if (!_cmaps.TryGetValue(index, out var map)) map = _cmaps.GetOrAdd(index, i => CmapTable.Read(_data, _cmapRecords[i].Offset));
        if (map is Format14 variations) variations.All(result, ref budget);
        return result;
    }

    /// <summary>
    /// One cmap subtable. Formats 4 and 12 are not expanded but searched by binary search in their
    /// segments, because CJK fonts have tens of thousands of characters.
    /// </summary>
    private abstract class CmapTable
    {
        public abstract int Map(int code);

        /// <summary>Ranges of codes that may be mapped (inclusive), ascending.</summary>
        public abstract IEnumerable<(int First, int Last)> Ranges();

        public static CmapTable Read(byte[] data, int offset)
        {
            try
            {
                var r = new FontReader(data, offset);
                int format = r.U16();
                switch (format)
                {
                    case 0:
                    {
                        r.Skip(4);
                        var glyphs = new int[256];
                        for (var c = 0; c < 256; c++) glyphs[c] = r.U8();
                        return new ArrayTable(0, glyphs);
                    }
                    case 6:
                    {
                        r.Skip(4);
                        int first = r.U16();
                        int count = r.U16();
                        var glyphs = new int[count];
                        for (var i = 0; i < count; i++) glyphs[i] = r.U16();
                        return new ArrayTable(first, glyphs);
                    }
                    case 2:
                        return new Format2(data, offset);
                    case 4:
                        return new Format4(data, offset);
                    case 8:
                        // the groups of format 8 are those of format 12, after the bit array of 8 KB (OpenType 9.4.6)
                        return new Format12(data, offset, false, 12 + 8192, mixed: true);
                    case 10:
                    {
                        // a trimmed array of 32-bit codes (9.4.7): first code, count, one glyph each — the count is limited by the table and the data
                        r.Skip(10);
                        var first = r.U32();
                        var count = r.U32();
                        var end = Math.Min(data.Length, offset + Math.Max(20L, LengthField32(data, offset)));
                        count = (uint)Math.Min(count, Math.Min(MaxArrayCodes, Math.Max(0, (end - offset - 20) / 2)));
                        if (first > 0x10FFFF) return new ArrayTable(0, []);
                        count = Math.Min(count, 0x110000 - first);
                        var glyphs = new int[count];
                        for (var i = 0; i < count; i++) glyphs[i] = r.U16();
                        return new ArrayTable((int)first, glyphs);
                    }
                    case 12 or 13:
                        return new Format12(data, offset, format == 13);
                    case 14:
                        return new Format14(data, offset);
                }
            }
            catch (FontFormatException)
            {
            }
            return new ArrayTable(0, []);
        }

        /// <summary>The 32-bit length field of formats 8, 10, 12 and 13 (after format and reserved); 0 if it lies outside the data.</summary>
        protected static long LengthField32(byte[] data, int offset) =>
            offset >= 0 && offset + 8 <= data.Length ? (uint)(data[offset + 4] << 24 | data[offset + 5] << 16 | data[offset + 6] << 8 | data[offset + 7]) : 0;

        /// <summary>Codes a format 10 subtable holds at most (as many as a rewritten cmap reads, a million).</summary>
        private const int MaxArrayCodes = 1_000_000;
    }

    /// <summary>
    /// Format 2 (OpenType 9.4.3): mixed one- and two-byte codes of the old CJK encodings. The high byte chooses a subheader through
    /// subHeaderKeys; subheader 0 maps the one-byte codes, the others the second byte of two-byte codes — firstCode and entryCount give the
    /// range of second bytes, idRangeOffset the glyph array (counted from the field itself), idDelta is added modulo 65 536 to a glyph other
    /// than 0. Every subheader and array entry is checked against the table and the data (docs/FEHLERGEDAECHTNIS.md, No. 1160).
    /// </summary>
    private sealed class Format2 : CmapTable
    {
        private readonly byte[] _data;
        private readonly int _offset, _end;
        private readonly ushort[] _keys = new ushort[256];

        public Format2(byte[] data, int offset)
        {
            _data = data;
            _offset = offset;
            var r = new FontReader(data, offset + 2);
            int length = r.U16();
            r.Skip(2);
            // the length field may be short or wrong in damaged fonts: the table reaches at least over the keys, at most to the end of the data
            _end = (int)Math.Min(data.Length, Math.Max(offset + 6L + 512, offset + (long)length));
            for (var i = 0; i < 256; i++) _keys[i] = r.U16();
        }

        public override IEnumerable<(int First, int Last)> Ranges()
        {
            for (var high = 0; high < 256; high++)
            {
                if (_keys[high] == 0)
                {
                    // one-byte code: subheader 0, the byte as the second byte
                    if (Subheader(0) is { } s0 && high >= s0.First && high < s0.First + s0.Count) yield return (high, high);
                }
                else if (Subheader(_keys[high] / 8) is { } s && s.Count > 0)
                    yield return ((high << 8) + Math.Min(255, s.First), (high << 8) + Math.Min(255, s.First + s.Count - 1));
            }
        }

        public override int Map(int code)
        {
            if (code is < 0 or > 0xFFFF) return 0;
            int high = code >> 8, low = code & 0xFF;
            int subheader;
            if (high == 0 && _keys[low] == 0) subheader = 0;           // one-byte code
            else if (high != 0 && _keys[high] != 0) subheader = _keys[high] / 8;
            else return 0;                                             // a lead byte alone, or a second byte after a one-byte code
            if (Subheader(subheader) is not { } s || low < s.First || low >= s.First + s.Count) return 0;
            var at = s.RangeOffsetAt + (long)s.RangeOffset + 2L * (low - s.First);
            if (at < _offset || at + 2 > _end) return 0;
            var g = _data[at] << 8 | _data[at + 1];
            return g == 0 ? 0 : (g + s.Delta) & 0xFFFF;
        }

        /// <summary>Subheader <paramref name="index"/> (8 bytes after the keys); null if it lies outside the table.</summary>
        private (int First, int Count, short Delta, int RangeOffset, int RangeOffsetAt)? Subheader(int index)
        {
            var at = _offset + 6 + 512 + 8L * index;
            if (at + 8 > _end) return null;
            var p = (int)at;
            return (_data[p] << 8 | _data[p + 1], _data[p + 2] << 8 | _data[p + 3], (short)(_data[p + 4] << 8 | _data[p + 5]),
                _data[p + 6] << 8 | _data[p + 7], p + 6);
        }
    }

    /// <summary>
    /// Format 14 (OpenType 9.4.10): Unicode variation sequences. Records sorted by selector; behind each a default table (ranges of base
    /// characters whose sequence takes the glyph of the Unicode subtables) and a non-default one (base character and glyph). Searched in the
    /// data by binary search; every count is limited by the table and every offset checked against it (docs/FEHLERGEDAECHTNIS.md, No. 1160).
    /// </summary>
    private sealed class Format14 : CmapTable
    {
        private readonly byte[] _data;
        private readonly int _offset, _end, _records;

        public Format14(byte[] data, int offset)
        {
            _data = data;
            _offset = offset;
            var r = new FontReader(data, offset + 2);
            var length = r.U32();
            var count = r.U32();
            _end = (int)Math.Min(data.Length, offset + Math.Max(10L, length));
            _records = (int)Math.Min(count, (uint)Math.Max(0, (_end - offset - 10) / 11));
        }

        // a single code means nothing in this format: the sequences are asked through Variation
        public override int Map(int code) => 0;

        public override IEnumerable<(int First, int Last)> Ranges() => [];

        /// <summary>The glyph of a sequence: 0 for a default sequence, the glyph for a non-default one, -1 if the table does not list it.</summary>
        public int Variation(int baseChar, int selector)
        {
            if (Record(selector) is not { } record) return -1;
            var (defaults, nonDefaults) = record;
            if (Table(defaults, 4) is { } d)
            {
                // the last range starting at or before the base character
                int lo = 0, hi = d.Count - 1, found = -1;
                while (lo <= hi)
                {
                    var mid = (lo + hi) >> 1;
                    if (U24(d.At + 4 * mid) <= baseChar) { found = mid; lo = mid + 1; }
                    else hi = mid - 1;
                }
                if (found >= 0 && baseChar <= U24(d.At + 4 * found) + _data[d.At + 4 * found + 3]) return 0;
            }
            if (Table(nonDefaults, 5) is { } n)
            {
                int lo = 0, hi = n.Count - 1;
                while (lo <= hi)
                {
                    var mid = (lo + hi) >> 1;
                    var code = U24(n.At + 5 * mid);
                    if (code == baseChar) return _data[n.At + 5 * mid + 3] << 8 | _data[n.At + 5 * mid + 4];
                    if (code < baseChar) lo = mid + 1;
                    else hi = mid - 1;
                }
            }
            return -1;
        }

        /// <summary>Every sequence, ascending, at most <paramref name="budget"/> base characters (for writing the subtable again).</summary>
        public void All(List<(int Selector, List<int> Defaults, List<(int Code, int Glyph)> NonDefaults)> result, ref int budget)
        {
            for (var i = 0; i < _records && budget > 0; i++)
            {
                var at = _offset + 10 + 11 * i;
                var selector = U24(at);
                var defaults = new List<int>();
                var nonDefaults = new List<(int, int)>();
                if (Table(U32(at + 3), 4) is { } d)
                    for (var k = 0; k < d.Count && budget > 0; k++)
                    {
                        var start = U24(d.At + 4 * k);
                        for (var c = start; c <= Math.Min(0xFFFFFF, start + _data[d.At + 4 * k + 3]) && budget > 0; c++, budget--) defaults.Add(c);
                    }
                if (Table(U32(at + 7), 5) is { } n)
                    for (var k = 0; k < n.Count && budget > 0; k++, budget--)
                        nonDefaults.Add((U24(n.At + 5 * k), _data[n.At + 5 * k + 3] << 8 | _data[n.At + 5 * k + 4]));
                result.Add((selector, defaults, nonDefaults));
            }
        }

        /// <summary>The offsets of the default and non-default tables of a selector (binary search over the sorted records).</summary>
        private (long Defaults, long NonDefaults)? Record(int selector)
        {
            int lo = 0, hi = _records - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                var at = _offset + 10 + 11 * mid;
                var s = U24(at);
                if (s == selector) return (U32(at + 3), U32(at + 7));
                if (s < selector) lo = mid + 1;
                else hi = mid - 1;
            }
            return null;
        }

        /// <summary>A table behind an offset (0: none): where its entries start and how many lie inside the subtable.</summary>
        private (int At, int Count)? Table(long offset, int entry)
        {
            if (offset == 0 || offset < 10 || _offset + offset + 4 > _end) return null;
            var at = (int)(_offset + offset);
            var count = Math.Min(U32(at), (_end - at - 4) / entry);
            return count > 0 ? (at + 4, (int)count) : null;
        }

        private int U24(int at) => _data[at] << 16 | _data[at + 1] << 8 | _data[at + 2];

        private long U32(int at) => (uint)(_data[at] << 24 | _data[at + 1] << 16 | _data[at + 2] << 8 | _data[at + 3]);
    }

    private sealed class ArrayTable(int first, int[] glyphs) : CmapTable
    {
        public override int Map(int code) => (uint)(code - first) < (uint)glyphs.Length ? glyphs[code - first] : 0;

        public override IEnumerable<(int First, int Last)> Ranges()
        {
            if (glyphs.Length > 0) yield return (first, first + glyphs.Length - 1);
        }
    }

    /// <summary>Format 4: segments with end, start, delta and range offset (16-bit codes).</summary>
    private sealed class Format4 : CmapTable
    {
        private readonly byte[] _data;
        private readonly ushort[] _ends, _starts, _rangeOffsets;
        private readonly short[] _deltas;
        private readonly int _rangeOffsetsAt;

        public Format4(byte[] data, int offset)
        {
            _data = data;
            var r = new FontReader(data, offset + 6);
            int segCount = r.U16() / 2;
            r.Skip(6);
            _ends = new ushort[segCount];
            for (var i = 0; i < segCount; i++) _ends[i] = r.U16();
            r.Skip(2);
            _starts = new ushort[segCount];
            for (var i = 0; i < segCount; i++) _starts[i] = r.U16();
            _deltas = new short[segCount];
            for (var i = 0; i < segCount; i++) _deltas[i] = r.I16();
            _rangeOffsetsAt = r.Position;
            _rangeOffsets = new ushort[segCount];
            for (var i = 0; i < segCount; i++) _rangeOffsets[i] = r.U16();
        }

        public override IEnumerable<(int First, int Last)> Ranges()
        {
            for (var i = 0; i < _ends.Length; i++)
                if (_starts[i] <= _ends[i]) yield return (_starts[i], Math.Min((int)_ends[i], 0xFFFE));
        }

        public override int Map(int code)
        {
            if (code is < 0 or >= 0xFFFF) return 0;
            // First segment whose end is ≥ the code (the ends are ascending)
            int lo = 0, hi = _ends.Length - 1, seg = -1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                if (_ends[mid] >= code) { seg = mid; hi = mid - 1; }
                else lo = mid + 1;
            }
            if (seg < 0 || code < _starts[seg]) return 0;
            if (_rangeOffsets[seg] == 0) return (code + _deltas[seg]) & 0xFFFF;
            var at = _rangeOffsetsAt + seg * 2 + _rangeOffsets[seg] + (code - _starts[seg]) * 2;
            if (at < 0 || at + 1 >= _data.Length) return 0;
            var g = _data[at] << 8 | _data[at + 1];
            return g == 0 ? 0 : (g + _deltas[seg]) & 0xFFFF;
        }
    }

    /// <summary>
    /// Formats 12/13: groups of consecutive codes (also above 0xFFFF); format 13 maps a whole group to one glyph. Format 8 has the same groups
    /// after its bit array (<paramref name="countAt"/> 8204); its 32-bit codes are the two 16-bit halves of a surrogate pair, the high one first
    /// (OpenType 9.4.6) — a code beyond the BMP is looked up so, and as itself where a font wrote it so.
    /// </summary>
    private sealed class Format12 : CmapTable
    {
        private readonly uint[] _starts, _ends, _glyphs;
        private readonly bool _constant, _mixed;

        public Format12(byte[] data, int offset, bool constant, int countAt = 12, bool mixed = false)
        {
            _constant = constant;
            _mixed = mixed;
            var r = new FontReader(data, offset + countAt);
            // format 8 (read since No. 1160) within its length field; formats 12 and 13 as far as the data reach, as always
            var end = mixed && LengthField32(data, offset) is var length && length >= countAt + 4 && offset + length <= data.Length ? offset + length : data.Length;
            var groups = (int)Math.Min(r.U32(), (uint)Math.Max(0, (end - offset - countAt - 4) / 12));
            _starts = new uint[groups];
            _ends = new uint[groups];
            _glyphs = new uint[groups];
            for (var i = 0; i < groups; i++)
            {
                _starts[i] = r.U32();
                _ends[i] = r.U32();
                _glyphs[i] = r.U32();
            }
        }

        public override IEnumerable<(int First, int Last)> Ranges()
        {
            for (var i = 0; i < _starts.Length; i++)
                if (_starts[i] <= _ends[i] && _ends[i] <= 0x10FFFF) yield return ((int)_starts[i], (int)_ends[i]);
                else if (_mixed && _starts[i] <= _ends[i] && Scalar(_starts[i]) is { } first && Scalar(_ends[i]) is { } last && first <= last)
                    yield return (first, last);
        }

        public override int Map(int code)
        {
            if (code < 0) return 0;
            if (_mixed && code is > 0xFFFF and <= 0x10FFFF)
            {
                var v = code - 0x10000;
                var pair = (uint)(0xD800 + (v >> 10)) << 16 | (uint)(0xDC00 + (v & 0x3FF));
                if (Find(pair) is > 0 and var g) return g;
            }
            return Find((uint)code);
        }

        /// <summary>The code point of a surrogate pair written as a 32-bit code (format 8); null for another value.</summary>
        private static int? Scalar(uint pair) =>
            pair >> 16 is >= 0xD800 and <= 0xDBFF && (pair & 0xFFFF) is >= 0xDC00 and <= 0xDFFF
                ? 0x10000 + (int)(((pair >> 16) - 0xD800) << 10 | ((pair & 0xFFFF) - 0xDC00)) : null;

        private int Find(uint c)
        {
            int lo = 0, hi = _starts.Length - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                if (c < _starts[mid]) hi = mid - 1;
                else if (c > _ends[mid]) lo = mid + 1;
                else return (int)(_constant ? _glyphs[mid] : _glyphs[mid] + (c - _starts[mid]));
            }
            return 0;
        }
    }

    // ------------------------------------------------------------ Names

    /// <summary>Returns the glyph name from the post table (formats 1 and 2) or from the CFF table.</summary>
    /// <param name="glyph">Glyph index.</param>
    /// <returns>The name, or null if the font has no name for the glyph.</returns>
    public string? GlyphName(int glyph)
    {
        if (Cff is { } cff) return cff.GlyphName(glyph);
        var names = PostNames();
        return glyph >= 0 && glyph < names.Length ? names[glyph] : null;
    }

    /// <summary>Finds a glyph by its name (post table or CFF table).</summary>
    /// <param name="name">Glyph name.</param>
    /// <returns>The glyph index, or -1 if no glyph has that name.</returns>
    public int GlyphByName(string name)
    {
        if (Cff is { } cff) return cff.GlyphByName(name);
        if (_nameToGlyph is null)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            var names = PostNames();
            for (var i = 0; i < names.Length; i++)
                if (names[i] is { Length: > 0 } n) map.TryAdd(n, i);
            _nameToGlyph = map;
        }
        return _nameToGlyph.TryGetValue(name, out var g) ? g : -1;
    }

    private string[] PostNames()
    {
        if (_postNames is not null) return _postNames;
        var names = Array.Empty<string>();
        if (Table("post") is { } post)
        {
            try
            {
                var r = new FontReader(_data, post.Offset);
                var format = r.U32();
                if (format == 0x00010000)
                {
                    names = FontTables.MacGlyphNames.Take(GlyphCount > 0 ? GlyphCount : 258).ToArray();
                }
                else if (format == 0x00020000)
                {
                    r.Position = post.Offset + 32;
                    int count = r.U16();
                    var indices = new int[count];
                    for (var i = 0; i < count; i++) indices[i] = r.U16();
                    var extra = new List<string>();
                    var end = post.Offset + post.Length;
                    while (r.Position < end && r.CanRead(1))
                    {
                        int len = r.U8();
                        if (!r.CanRead(len)) break;
                        extra.Add(System.Text.Encoding.Latin1.GetString(r.Bytes(len)));
                    }
                    names = new string[count];
                    for (var i = 0; i < count; i++)
                        names[i] = indices[i] < 258 ? FontTables.MacGlyphNames[indices[i]]
                            : indices[i] - 258 < extra.Count ? extra[indices[i] - 258] : "";
                }
                else if (format == 0x00025000)
                {
                    // version 2.5 (deprecated, OpenType 32.5): per glyph a signed offset into the standard Macintosh order — before not read,
                    // and the glyphs had no names (docs/FEHLERGEDAECHTNIS.md, No. 1154)
                    r.Position = post.Offset + 32;
                    int count = r.U16();
                    names = new string[count];
                    for (var i = 0; i < count && r.CanRead(1); i++)
                        names[i] = i + (sbyte)r.U8() is >= 0 and < 258 and var at ? FontTables.MacGlyphNames[at] : "";
                }
            }
            catch (FontFormatException)
            {
            }
        }
        return _postNames = names;
    }

    // English first, Mac OS Roman decoded, old Windows code pages and unknown languages passed over (NameRecords; No. 1160)
    private string? ReadName(int nameId) => Table("name") is { } table ? NameRecords.Read(_data, table.Offset, nameId) : null;

    // ------------------------------------------------------------ Widths and outlines

    /// <summary>Returns the advance width of a glyph in font units.</summary>
    /// <param name="glyph">Glyph index; glyphs after the last hmtx entry use its width.</param>
    /// <returns>The advance width; without hmtx the CFF width, otherwise 0.</returns>
    public double AdvanceWidth(int glyph)
    {
        if (_advances.Length > 0 && glyph >= 0)
            return _advances[Math.Min(glyph, _advances.Length - 1)];
        return Cff?.AdvanceWidth(glyph) ?? 0;
    }

    /// <summary>Whether a glyph has data of its own: in a CFF-based font every glyph below the count, in a TrueType font a non-empty entry of „glyf“
    /// (an empty one is a glyph without outline, such as the space, or a slot a subset left free).</summary>
    /// <param name="glyph">Glyph index.</param>
    public bool HasGlyphData(int glyph) =>
        (Cff ?? Cff2) is { } cff ? glyph >= 0 && glyph < cff.GlyphCount
        : glyph >= 0 && glyph < _loca.Length - 1 && _loca[glyph + 1] > _loca[glyph] && Table("glyf") is { } glyf && _loca[glyph] < glyf.Length;

    /// <summary>
    /// The lower left corner (xMin, yMin) of the glyf header of a glyph with contours (simple or composite); null for one without data or
    /// contours and in a font without glyf. sbix places the image of such a glyph there (OpenType 34.3).
    /// </summary>
    internal (int XMin, int YMin)? ContourCorner(int glyph)
    {
        if (Cff is not null || Cff2 is not null || !HasGlyphData(glyph) || Table("glyf") is not { } glyf) return null;
        var at = glyf.Offset + _loca[glyph];
        if (_loca[glyph] + 10 > glyf.Length || at + 10 > _data.Length) return null;
        var r = new FontReader(_data, at);
        if (r.I16() == 0) return null;
        return (r.I16(), r.I16());
    }

    /// <summary>Returns the outline of a glyph in font units.</summary>
    /// <param name="glyph">Glyph index.</param>
    /// <returns>The outline; empty for unknown glyphs, recursive composites and glyphs above the work limits.</returns>
    public GlyphOutline Outline(int glyph)
    {
        if ((Cff ?? Cff2) is { } cff) return cff.Outline(glyph);
        // (compared without adding to the glyph: a glyph number of int.MaxValue from a damaged map overflowed and passed;
        // docs/FEHLERGEDAECHTNIS.md, No. 379)
        if (glyph < 0 || glyph >= _loca.Length - 1) return GlyphOutline.Empty;
        if (_outlines.TryGetValue(glyph, out var cached)) return cached;
        return _outlines.GetOrAdd(glyph, g =>
        {
            var outline = new GlyphOutline();
            try
            {
                _componentCalls = 0;
                _glyphPoints = 0;
                AppendGlyf(outline, g, Matrix.Identity, 0);
            }
            catch (RecursiveGlyphException)
            {
                // The glyph contains itself: discard it entirely, as common font engines do (e.g. the test file recursiveCompositGlyf),
                // instead of drawing it down to the depth limit
                outline = new GlyphOutline();
            }
            catch (FontFormatException)
            {
                // Too costly (crafted): empty instead of a half-built but still huge outline; for other damaged data keep what was read
                if (_componentCalls > MaxComponentCalls || _glyphPoints > MaxGlyphPoints) outline = new GlyphOutline();
            }
            return outline.Finish();
        });
    }

    // Components per glyph: composite glyphs that call the same components thousands of times in nested form grow
    // exponentially despite the depth limit. Points per glyph: 10 000 components of 65 536 points each took 14 s and 30 GB
    private const int MaxComponentCalls = 10_000, MaxGlyphPoints = 200_000;
    [ThreadStatic] private static int _componentCalls;
    [ThreadStatic] private static int _glyphPoints;

    // Glyphs on the path from the outermost to the current one (one per depth)
    [ThreadStatic] private static int[]? _chain;

    private sealed class RecursiveGlyphException() : FontFormatException("Glyphe enthält sich selbst");

    private void AppendGlyf(GlyphOutline outline, int glyph, Matrix m, int depth)
    {
        if (++_componentCalls > MaxComponentCalls) throw new FontFormatException("Glyphe zu aufwendig");
        if (depth > MaxComponentDepth || glyph < 0 || glyph >= _loca.Length - 1 || Table("glyf") is not { } glyf) return;
        var chain = _chain ??= new int[MaxComponentDepth + 1];
        for (var i = 0; i < depth; i++)
            if (chain[i] == glyph) throw new RecursiveGlyphException();
        chain[depth] = glyph;
        var start = _loca[glyph];
        var end = _loca[glyph + 1];
        // Equal: empty glyph (space). Smaller: loca is unsorted (test file issue11131); as in common font engines only the end of glyf is the limit then
        if (end == start || start >= glyf.Length) return;
        var r = new FontReader(_data, glyf.Offset + start);
        int contours = r.I16();
        r.Skip(8);                                          // bounding box
        if (contours >= 0) SimpleGlyph(outline, ref r, contours, m);
        else CompositeGlyph(outline, ref r, m, depth);
    }

    private static void SimpleGlyph(GlyphOutline outline, ref FontReader r, int contours, Matrix m)
    {
        if (contours == 0) return;
        var endPoints = new int[contours];
        for (var i = 0; i < contours; i++) endPoints[i] = r.U16();
        var count = endPoints[^1] + 1;
        if (count <= 0 || count > 65536) return;
        if ((_glyphPoints += count) > MaxGlyphPoints) throw new FontFormatException("Glyphe zu aufwendig");
        int instructions = r.U16();
        r.Skip(instructions);

        var flags = new byte[count];
        for (var i = 0; i < count;)
        {
            var f = r.U8();
            flags[i++] = f;
            if ((f & 8) != 0)
            {
                int repeat = r.U8();
                for (var k = 0; k < repeat && i < count; k++) flags[i++] = f;
            }
        }
        var xs = new int[count];
        var ys = new int[count];
        var v = 0;
        for (var i = 0; i < count; i++)
        {
            var f = flags[i];
            if ((f & 2) != 0) v += (f & 16) != 0 ? r.U8() : -r.U8();
            else if ((f & 16) == 0) v += r.I16();
            xs[i] = v;
        }
        v = 0;
        for (var i = 0; i < count; i++)
        {
            var f = flags[i];
            if ((f & 4) != 0) v += (f & 32) != 0 ? r.U8() : -r.U8();
            else if ((f & 32) == 0) v += r.I16();
            ys[i] = v;
        }

        var first = 0;
        foreach (var last in endPoints)
        {
            if (last < first || last >= count) break;
            Contour(outline, xs, ys, flags, first, last, m);
            first = last + 1;
        }
    }

    /// <summary>Draws one contour with on-curve and off-curve points (quadratic, implied midpoints).</summary>
    private static void Contour(GlyphOutline outline, int[] xs, int[] ys, byte[] flags, int first, int last, Matrix m)
    {
        var n = last - first + 1;
        if (n < 2) return;
        bool On(int i) => (flags[first + (i % n + n) % n] & 1) != 0;
        (double X, double Y) P(int i)
        {
            var k = first + (i % n + n) % n;
            return m.Transform(xs[k], ys[k]);
        }

        // Start point: an on-curve point (the first or the last), otherwise the midpoint between the last and the first
        int startIndex;
        (double X, double Y) start;
        if (On(0)) { startIndex = 0; start = P(0); }
        else if (On(n - 1)) { startIndex = -1; start = P(n - 1); }
        else
        {
            startIndex = -1;
            var (ax, ay) = P(n - 1);
            var (bx, by) = P(0);
            start = ((ax + bx) / 2, (ay + by) / 2);
        }
        outline.MoveTo((float)start.X, (float)start.Y);

        (double X, double Y)? control = null;
        for (var step = 1; step <= n; step++)
        {
            var i = startIndex + step;
            var p = P(i);
            if (On(i))
            {
                if (control is { } c) outline.QuadTo((float)c.X, (float)c.Y, (float)p.X, (float)p.Y);
                else outline.LineTo((float)p.X, (float)p.Y);
                control = null;
            }
            else
            {
                if (control is { } c)
                {
                    var mid = ((c.X + p.X) / 2, (c.Y + p.Y) / 2);
                    outline.QuadTo((float)c.X, (float)c.Y, (float)mid.Item1, (float)mid.Item2);
                }
                control = p;
            }
        }
        if (control is { } last2) outline.QuadTo((float)last2.X, (float)last2.Y, (float)start.X, (float)start.Y);
        outline.Close();
    }

    private void CompositeGlyph(GlyphOutline outline, ref FontReader r, Matrix m, int depth)
    {
        int flags;
        do
        {
            flags = r.U16();
            int component = r.U16();
            double dx, dy;
            if ((flags & 1) != 0) { dx = r.I16(); dy = r.I16(); }       // ARG_1_AND_2_ARE_WORDS
            else { dx = r.I8(); dy = r.I8(); }
            if ((flags & 2) == 0) dx = dy = 0;                           // point numbers instead of an offset: rare, treated as no offset
            double a = 1, b = 0, c = 0, d = 1;
            if ((flags & 8) != 0) a = d = r.F2Dot14();                   // WE_HAVE_A_SCALE
            else if ((flags & 0x40) != 0) { a = r.F2Dot14(); d = r.F2Dot14(); }
            else if ((flags & 0x80) != 0) { a = r.F2Dot14(); b = r.F2Dot14(); c = r.F2Dot14(); d = r.F2Dot14(); }
            if ((flags & 0x800) != 0) (dx, dy) = (a * dx + c * dy, b * dx + d * dy);   // SCALED_COMPONENT_OFFSET
            var local = new Matrix(a, b, c, d, dx, dy);
            AppendGlyf(outline, component, local * m, depth + 1);
        }
        while ((flags & 0x20) != 0);                                     // MORE_COMPONENTS
    }
}
