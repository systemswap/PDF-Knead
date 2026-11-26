using System.Buffers.Binary;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-12 (OpenType, cmap 9.2 to 9.4): the tests the drafts of the sentence-by-sentence comparison asked for — encoding records
/// and platforms, the subtable formats the reader reads (0, 4, 6, 12, 13) and those it does not (2, 8, 10, 14), and the cmap the scrubber
/// writes anew (formats 4 and 12).
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (ot-12)

    private static readonly Lazy<byte[]> Ot12SansFile = new(() => EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf"));
    private static readonly Lazy<OpenTypeFont> Ot12Sans = new(() => OpenTypeFont.Parse(Ot12SansFile.Value));

    /// <summary>The Japanese face of Noto Sans CJK as a font file of its own (CFF outlines, the cmap of the collection's first font).</summary>
    private static readonly Lazy<byte[]> Ot12NotoFile = new(() =>
    {
        var noto = Iso03NotoJp.Value;
        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var tag in (string[])["CFF ", "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post"])
            if (noto.Table(tag) is { } t) tables[tag] = noto.FileData.AsSpan(t.Offset, t.Length).ToArray();
        return TrueTypeSubsetter.Finish(tables);
    });

    private static int Ot12Gid(char c) => Ot12Sans.Value.GlyphForUnicode(c);

    /// <summary>Big-endian bytes for hand-built subtables.</summary>
    private sealed class Ot12Bytes
    {
        private readonly List<byte> _data = [];

        public int Length => _data.Count;

        public Ot12Bytes U8(int v)
        {
            _data.Add((byte)v);
            return this;
        }

        public Ot12Bytes U16(int v) => U8(v >> 8).U8(v);

        public Ot12Bytes U24(int v) => U8(v >> 16).U16(v);

        public Ot12Bytes U32(long v) => U16((int)(v >> 16)).U16((int)v);

        public Ot12Bytes Bytes(ReadOnlySpan<byte> data)
        {
            foreach (var b in data) _data.Add(b);
            return this;
        }

        public void SetU16(int at, int v)
        {
            _data[at] = (byte)(v >> 8);
            _data[at + 1] = (byte)v;
        }

        public void SetU32(int at, long v)
        {
            SetU16(at, (int)(v >> 16));
            SetU16(at + 2, (int)v);
        }

        public byte[] ToArray() => [.. _data];
    }

    /// <summary>A cmap with the records in the order given, each with a subtable of its own.</summary>
    private static byte[] Ot12Cmap(params (int Platform, int Encoding, byte[] Subtable)[] records)
    {
        var w = new Ot12Bytes().U16(0).U16(records.Length);
        var offset = 4 + 8 * records.Length;
        foreach (var (platform, encoding, subtable) in records)
        {
            w.U16(platform).U16(encoding).U32(offset);
            offset += subtable.Length;
        }
        foreach (var (_, _, subtable) in records) w.Bytes(subtable);
        return w.ToArray();
    }

    /// <summary>A font file with its cmap replaced.</summary>
    private static byte[] Ot12With(byte[] font, params (int Platform, int Encoding, byte[] Subtable)[] records) =>
        ColorFontBuilder.WithTables(font, new Dictionary<string, byte[]?> { ["cmap"] = Ot12Cmap(records) });

    /// <summary>The bytes of a font's subtable for a platform and encoding (length after its format).</summary>
    private static byte[] Ot12Sub(OpenTypeFont font, int platform, int encoding)
    {
        var data = font.FileData;
        var at = font.CmapOffset(platform, encoding);
        int format = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
        var length = format switch
        {
            0 or 2 or 4 or 6 => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 2)),
            14 => (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 2)),
            _ => (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 4)),
        };
        return data.AsSpan(at, length).ToArray();
    }

    /// <summary>The records of a font's cmap in file order: platform, encoding, format, language (-1 for format 14) and the subtable offset.</summary>
    private static List<(int Platform, int Encoding, int Format, long Language, int Offset)> Ot12Records(byte[] font)
    {
        var parsed = OpenTypeFont.Parse(font);
        var (cmap, _) = parsed.Table("cmap")!.Value;
        var data = parsed.FileData;
        int count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(cmap + 2));
        var list = new List<(int, int, int, long, int)>();
        for (var i = 0; i < count; i++)
        {
            var r = cmap + 4 + 8 * i;
            int platform = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(r));
            int encoding = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(r + 2));
            var at = cmap + (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(r + 4));
            int format = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
            long language = format switch
            {
                0 or 2 or 4 or 6 => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 4)),
                14 => -1,
                _ => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 8)),
            };
            list.Add((platform, encoding, format, language, at));
        }
        return list;
    }

    /// <summary>Format 4 from segments (sorted by end) and a glyph array; the search values derived from the segment count unless given.</summary>
    private static byte[] Ot12Format4((int Start, int End, int Delta, int RangeOffset)[] segments, int[]? glyphArray = null,
        (int Range, int Selector, int Shift)? search = null, int language = 0)
    {
        var n = segments.Length;
        var selector = (int)Math.Floor(Math.Log2(n));
        var (range, sel, shift) = search ?? (2 << selector, selector, 2 * n - (2 << selector));
        var w = new Ot12Bytes().U16(4).U16(0).U16(language).U16(2 * n).U16(range).U16(sel).U16(shift);
        foreach (var s in segments) w.U16(s.End);
        w.U16(0);
        foreach (var s in segments) w.U16(s.Start);
        foreach (var s in segments) w.U16(s.Delta & 0xFFFF);
        foreach (var s in segments) w.U16(s.RangeOffset);
        foreach (var g in glyphArray ?? []) w.U16(g);
        w.SetU16(2, w.Length);
        return w.ToArray();
    }

    /// <summary>Format 4 with one delta segment for consecutive codes on consecutive glyphs, and the closing segment.</summary>
    private static byte[] Ot12Format4Run(int first, int last, int glyph) =>
        Ot12Format4([(first, last, glyph - first, 0), (0xFFFF, 0xFFFF, 1, 0)]);

    private static byte[] Ot12Format6(int first, int[] glyphs, int language = 0)
    {
        var w = new Ot12Bytes().U16(6).U16(10 + 2 * glyphs.Length).U16(language).U16(first).U16(glyphs.Length);
        foreach (var g in glyphs) w.U16(g);
        return w.ToArray();
    }

    private static byte[] Ot12Format0(IReadOnlyDictionary<int, int> map)
    {
        var w = new Ot12Bytes().U16(0).U16(262).U16(0);
        for (var c = 0; c < 256; c++) w.U8(map.TryGetValue(c, out var g) ? g : 0);
        return w.ToArray();
    }

    /// <summary>Format 12 (or 13 with <paramref name="format"/>) from groups (start, end, glyph).</summary>
    private static byte[] Ot12Format12((int Start, int End, int Glyph)[] groups, int format = 12)
    {
        var w = new Ot12Bytes().U16(format).U16(0).U32(16 + 12 * groups.Length).U32(0).U32(groups.Length);
        foreach (var (start, end, glyph) in groups) w.U32(start).U32(end).U32(glyph);
        return w.ToArray();
    }

    /// <summary>A Liberation Sans whose cmap gets one further record (after its own three).</summary>
    private static byte[] Ot12SansPlus(int platform, int encoding, byte[] subtable)
    {
        var sans = Ot12Sans.Value;
        return Ot12With(Ot12SansFile.Value, (0, 3, Ot12Sub(sans, 0, 3)), (1, 0, Ot12Sub(sans, 1, 0)), (3, 1, Ot12Sub(sans, 3, 1)),
            (platform, encoding, subtable));
    }

    /// <summary>A simple TrueType font (Liberation Sans embedded, no /Encoding) with the given flags; the document must be disposed.</summary>
    private static (Core.Pdf.Document.PdfDocument Doc, Core.Pdf.Fonts.PdfFont Font) Ot12SimpleFont(int flags)
    {
        var (doc, _, font) = Iso03PageFont("BT /F1 12 Tf 10 10 Td (A) Tj ET", pdf =>
        {
            var program = pdf.Add(TestPdf.FlateStream(Ot12SansFile.Value, TestPdf.Dict(("Length1", PdfInteger.Of(Ot12SansFile.Value.Length)))));
            var descriptor = TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ProbeSans")), ("Flags", PdfInteger.Of(flags)),
                ("FontBBox", TestPdf.Box(-203, -303, 1050, 910)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(905)),
                ("Descent", PdfInteger.Of(-212)), ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(program)));
            return TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("ProbeSans")),
                ("FontDescriptor", descriptor));
        });
        return (doc, font);
    }

    /// <summary>Noto Sans CJK JP scrubbed down to the glyphs of 葛 (U+845B) and 𠮟 (U+20B9F) and „A“.</summary>
    private static readonly Lazy<byte[]> Ot12NotoScrubbed = new(() =>
    {
        var noto = OpenTypeFont.Parse(Ot12NotoFile.Value);
        return TrueTypeSubsetter.Scrub(Ot12NotoFile.Value, [noto.GlyphForUnicode(0x845B), noto.GlyphForUnicode(0x20B9F), noto.GlyphForUnicode('A')]);
    });

    // ------------------------------------------------------------ 9.3

    /// <summary>
    /// 9.3 sentence 8 (reader and writer, today's behaviour): for the Macintosh platform the language field belongs to the encoding — a second
    /// (1,0) record whose format 6 subtable has language 18 (Turkish) and maps 0x41 to „B“, ahead of the record with language 0: the reader
    /// takes the first record of the pair without looking at the language, lists (1,0) once, and the scrubbed font keeps one (1,0) subtable —
    /// since No. 1143 with the language of that first record (18); the other language version is gone.
    /// </summary>
    [Fact]
    public void MacSubtablesOfOnePair_FirstRecordCounts_ScrubKeepsOneWithItsLanguage()
    {
        var sans = Ot12Sans.Value;
        var (a, b) = (Ot12Gid('A'), Ot12Gid('B'));
        var font = Ot12With(Ot12SansFile.Value, (0, 3, Ot12Sub(sans, 0, 3)), (1, 0, Ot12Format6(0x41, [b], language: 18)),
            (1, 0, Ot12Sub(sans, 1, 0)), (3, 1, Ot12Sub(sans, 3, 1)));
        var parsed = OpenTypeFont.Parse(font);
        Assert.Equal(b, parsed.Lookup(1, 0, 0x41));
        Assert.Single(parsed.CmapSubtables, r => r == (1, 0));
        var scrubbed = TrueTypeSubsetter.Scrub(font, [0, a, b]);
        var mac = Assert.Single(Ot12Records(scrubbed), r => r.Platform == 1);
        Assert.Equal(18, mac.Language);
        Assert.Equal(b, OpenTypeFont.Parse(scrubbed).Lookup(1, 0, 0x41));
    }

    /// <summary>
    /// 9.3 sentence 9 (reader): the records are sorted by platform and encoding, but the reader does not depend on it — Liberation Sans with its
    /// records in the order (3,1), (0,3), (1,0) maps „A“ and every pair as before. (The writer: FontScrubTests.ScrubbedCmap_SortedWithoutUnreadFormatsAndWithTheMacLanguage.)
    /// </summary>
    [Fact]
    public void UnsortedCmapRecords_AreReadLikeSortedOnes()
    {
        var sans = Ot12Sans.Value;
        var parsed = OpenTypeFont.Parse(Ot12With(Ot12SansFile.Value, (3, 1, Ot12Sub(sans, 3, 1)), (0, 3, Ot12Sub(sans, 0, 3)), (1, 0, Ot12Sub(sans, 1, 0))));
        Assert.Equal(sans.GlyphForUnicode('A'), parsed.GlyphForUnicode('A'));
        foreach (var (platform, encoding) in sans.CmapSubtables)
            Assert.Equal(sans.Lookup(platform, encoding, 0x41), parsed.Lookup(platform, encoding, 0x41));
    }

    /// <summary>
    /// 9.3 sentence 10: each combination of platform, encoding and language at most once — with two (3,1) records, the first to a subtable
    /// mapping „A“ to glyph 5, the reader takes the first (glyph 5), lists (3,1) once, and the scrubbed font has one (3,1) record.
    /// </summary>
    [Fact]
    public void DuplicateCmapRecords_TheFirstCounts_ScrubWritesOne()
    {
        var sans = Ot12Sans.Value;
        var font = Ot12With(Ot12SansFile.Value, (0, 3, Ot12Sub(sans, 0, 3)), (1, 0, Ot12Sub(sans, 1, 0)), (3, 1, Ot12Format4Run(0x41, 0x41, 5)),
            (3, 1, Ot12Sub(sans, 3, 1)));
        var parsed = OpenTypeFont.Parse(font);
        Assert.Equal(5, parsed.GlyphForUnicode('A'));
        Assert.Single(parsed.CmapSubtables, r => r == (3, 1));
        Assert.Single(Ot12Records(TrueTypeSubsetter.Scrub(font, [0, 5, Ot12Gid('A')])), r => (r.Platform, r.Encoding) == (3, 1));
    }

    /// <summary>
    /// 9.3 sentence 12, 9.3.2 sentence 8 and 9.4.10 sentences 1–3, 5–8, 9–14, 23–28 (reader): the format 14 subtable under (0,5) of Noto Sans CJK
    /// is read — a single code in it gives 0, the non-default sequence 葛 U+E0100 shapes as one glyph, 62001, the one the table names, with the
    /// text of both characters; the default sequence 葛 U+E0101 as the glyph of the Unicode subtable; a selector the table does not list for 葛
    /// (U+E01EF) is passed over instead of becoming glyph 0 (FG 1160).
    /// </summary>
    [Fact]
    public void Format14VariationSequences_AreRead()
    {
        var noto = Iso03NotoJp.Value;
        var kanji = noto.GlyphForUnicode(0x845B);
        Assert.True(noto.HasCmap(0, 5));
        Assert.Equal(0, noto.Lookup(0, 5, 0x845B));
        var shaper = TextShaper.For(noto);
        var variant = Assert.Single(shaper.Shape("葛\U000E0100"));
        Assert.Equal(62001, variant.Glyph);
        Assert.NotEqual(kanji, variant.Glyph);
        Assert.Equal("葛\U000E0100", variant.Text);
        Assert.Equal(kanji, Assert.Single(shaper.Shape("葛\U000E0101")).Glyph);
        Assert.Equal(kanji, Assert.Single(shaper.Shape("葛\U000E01EF")).Glyph);
        Assert.Equal(new[] { kanji, 62001, kanji }, shaper.Shape("葛葛\U000E0100葛").Select(g => g.Glyph));
    }

    /// <summary>
    /// 9.4.10 sentences 5–8 (reader, substitute font): the text of a code from /ToUnicode as a substitute font draws it — „葛“ followed by U+E0100
    /// gives glyph 62001 of Noto Sans CJK, followed by U+E0101 (default) or by U+E01EF (not listed) the glyph of „葛“, and a lone surrogate gives 0
    /// instead of an exception (FG 1160).
    /// </summary>
    [Fact]
    public void Format14_SubstituteFontTakesTheSequenceOfTheText()
    {
        var noto = Iso03NotoJp.Value;
        var method = typeof(OpenTypeFont).GetMethod("GlyphForText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        int Glyph(string text) => (int)method.Invoke(noto, [text])!;
        Assert.Equal(62001, Glyph("葛\U000E0100"));
        Assert.Equal(noto.GlyphForUnicode(0x845B), Glyph("葛\U000E0101"));
        Assert.Equal(noto.GlyphForUnicode(0x845B), Glyph("葛\U000E01EF"));
        Assert.Equal(noto.GlyphForUnicode(0x845B), Glyph("葛"));
        Assert.Equal(0, Glyph("\uD800"));
    }

    /// <summary>
    /// 9.4.10 sentences 15–22, 9.4.10.1 sentences 1–17 and 9.4.10.2 sentences 1–16 (reader): a hand-built format 14 subtable in Noto Sans CJK —
    /// a record with both offsets 0, a default table with the range U+4E4D + 2 for U+E0100 and a range 0xFFFFF0 + 0xFF that runs past 24 bits,
    /// a non-default table mapping 葛 U+E0100 to glyph 100 — is read within its bounds: 乏 U+E0100 (in the default range) shapes as the base
    /// glyph alone, 葛 U+E0100 as glyph 100, 葛 U+E0101 (both offsets 0) and 乏 U+E0102 (default) as the base glyph; with a record count far
    /// beyond the table nothing is read outside it and nothing fails (FG 1160).
    /// </summary>
    [Fact]
    public void CraftedFormat14_IsReadWithinItsBounds()
    {
        var noto = OpenTypeFont.Parse(Ot12NotoFile.Value);
        // header 10 bytes, three selector records of 11 bytes, then the default table (2 ranges) and the non-default table (1 mapping)
        const int defaultAt = 10 + 3 * 11, nonDefaultAt = defaultAt + 4 + 2 * 4;
        var f14 = new Ot12Bytes().U16(14).U32(0).U32(3)
            .U24(0xE0100).U32(defaultAt).U32(nonDefaultAt)
            .U24(0xE0101).U32(0).U32(0)                                           // both offsets 0: no sequence
            .U24(0xE0102).U32(defaultAt).U32(0)
            .U32(2).U24(0x4E4D).U8(2).U24(0xFFFFF0).U8(0xFF)                       // default UVS: U+4E4D–U+4E4F, and a range past 0xFFFFFF
            .U32(1).U24(0x845B).U16(100);                                          // non-default UVS: 葛 → glyph 100
        f14.SetU32(2, f14.Length);
        var font = OpenTypeFont.Parse(Ot12With(Ot12NotoFile.Value, (0, 5, f14.ToArray()), (3, 1, Ot12Sub(noto, 3, 1)), (3, 10, Ot12Sub(noto, 3, 10))));
        Assert.Equal(0, font.Lookup(0, 5, 0x4E4D));
        Assert.Equal(0, font.Lookup(0, 5, 0x845B));
        var shaper = TextShaper.For(font);
        Assert.Equal(new[] { noto.GlyphForUnicode(0x4E4F) }, shaper.Shape("乏\U000E0100").Select(g => g.Glyph));
        Assert.Equal(new[] { 100 }, shaper.Shape("葛\U000E0100").Select(g => g.Glyph));
        Assert.Equal(new[] { noto.GlyphForUnicode(0x845B) }, shaper.Shape("葛\U000E0101").Select(g => g.Glyph));
        Assert.Equal(new[] { noto.GlyphForUnicode(0x4E4F) }, shaper.Shape("乏\U000E0102").Select(g => g.Glyph));
        Assert.Equal(new[] { noto.GlyphForUnicode(0x4E4D) }, shaper.Shape("乍\U000E0102").Select(g => g.Glyph));
        var huge = f14.ToArray();
        huge[6] = huge[7] = huge[8] = huge[9] = 0x7F;                            // records: about two billion
        huge[2] = huge[3] = huge[4] = huge[5] = 0xFF;                            // length: beyond the data
        var crafted = OpenTypeFont.Parse(Ot12With(Ot12NotoFile.Value, (0, 5, huge), (3, 1, Ot12Sub(noto, 3, 1)), (3, 10, Ot12Sub(noto, 3, 10))));
        Assert.Equal(noto.GlyphForUnicode(0x845B), Assert.Single(TextShaper.For(crafted).Shape("葛\U000E0103")).Glyph);
    }

    /// <summary>
    /// 9.3 sentence 13: where 16-bit and 32-bit Unicode subtables exist, the 32-bit one is used — Liberation Sans with a further (3,10) format 12
    /// subtable mapping „A“ to glyph 5: „A“ gives glyph 5, not the glyph of (3,1).
    /// </summary>
    [Fact]
    public void FullRepertoireSubtable_IsPreferredOverTheBmpOne()
    {
        var font = OpenTypeFont.Parse(Ot12SansPlus(3, 10, Ot12Format12([(0x41, 0x41, 5)])));
        Assert.NotEqual(5, Ot12Gid('A'));
        Assert.Equal(5, font.GlyphForUnicode('A'));
    }

    /// <summary>
    /// 9.3 sentences 14–15 (writer): not formats 4 and 6 together nor 10 and 12 together — the cmap of scrubbed Liberation Sans ((1,0) is format 6
    /// in the source) and of scrubbed Noto Sans CJK ((1,1) format 6) has only formats 4 and 12 for character codes — beside them format 14 under
    /// (0,5) for the variation sequences (FG 1160).
    /// </summary>
    [Fact]
    public void ScrubbedCmap_HasOnlyFormats4And12()
    {
        var sans = TrueTypeSubsetter.Scrub(Ot12SansFile.Value, [0, Ot12Gid('A')]);
        Assert.All(Ot12Records(sans), r => Assert.Contains(r.Format, (int[])[4, 12]));
        Assert.All(Ot12Records(Ot12NotoScrubbed.Value).Where(r => (r.Platform, r.Encoding) != (0, 5)), r => Assert.Contains(r.Format, (int[])[4, 12]));
    }

    /// <summary>
    /// 9.3 sentence 16: with Unicode subtables of the same format under two platforms the choice is the same every time — Liberation Sans whose
    /// (0,3) maps „A“ to glyph 5: „A“ gives the glyph of (3,1) on every call.
    /// </summary>
    [Fact]
    public void SameFormatUnderTwoPlatforms_TheChoiceIsAlwaysTheSame()
    {
        var sans = Ot12Sans.Value;
        var font = OpenTypeFont.Parse(Ot12With(Ot12SansFile.Value, (0, 3, Ot12Format4Run(0x41, 0x41, 5)), (1, 0, Ot12Sub(sans, 1, 0)), (3, 1, Ot12Sub(sans, 3, 1))));
        for (var i = 0; i < 3; i++) Assert.Equal(Ot12Gid('A'), font.GlyphForUnicode('A'));
    }

    // ------------------------------------------------------------ 9.3.1, 9.3.4, 9.3.6

    /// <summary>
    /// 9.3.1 sentences 5, 7, 9.3.4 sentences 1–6 and 9.3.6 sentences 3–4, 10–11 (reader): the ISO platform (2, obsolete) and the custom platform (4)
    /// are not asked for Unicode — Liberation Sans whose only record is (2,0) or (4,0) with a format 0 subtable mapping 0x41 to „A“ gives no glyph
    /// for U+0041, a further such record beside the font's own leaves „A“ as it was; a symbolic simple TrueType font without /Encoding draws code
    /// 0x41 through that record in the last fallback (every subtable with the raw code).
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void IsoAndCustomPlatforms_OnlyTheLastFallbackOfSimpleFontsAsksThem(int platform)
    {
        var a = Ot12Gid('A');
        var only = OpenTypeFont.Parse(Ot12With(Ot12SansFile.Value, (platform, 0, Ot12Format0(new Dictionary<int, int> { [0x41] = a }))));
        Assert.Equal(0, only.GlyphForUnicode('A'));
        var beside = OpenTypeFont.Parse(Ot12SansPlus(platform, 0, Ot12Format0(new Dictionary<int, int> { [0x41] = 5 })));
        Assert.Equal(a, beside.GlyphForUnicode('A'));
        var (doc, font) = Ot12SimpleFont(4);
        using (doc) Assert.Equal(a, font.DrawnGlyphWith(only, 0x41, 1));
    }

    /// <summary>
    /// 9.3.1 sentence 8 (reader and writer): platform IDs 240–255 are for custom use — a further (240,0) record is read like any other and kept by
    /// scrubbing (its subtable written anew with the glyphs kept).
    /// </summary>
    [Fact]
    public void CustomPlatform240_IsReadAndKeptLikeAnyOther()
    {
        var a = Ot12Gid('A');
        var font = Ot12SansPlus(240, 0, Ot12Format6(0x41, [a]));
        Assert.Equal(a, OpenTypeFont.Parse(font).Lookup(240, 0, 0x41));
        var scrubbed = TrueTypeSubsetter.Scrub(font, [0, a]);
        Assert.Single(Ot12Records(scrubbed), r => (r.Platform, r.Encoding) == (240, 0));
        Assert.Equal(a, OpenTypeFont.Parse(scrubbed).Lookup(240, 0, 0x41));
    }

    /// <summary>9.3.6 sentence 9 (writer): the encoding ID of platform 4 (the Windows character set of the PFM file) is kept — (4,238) stays (4,238) after scrubbing.</summary>
    [Fact]
    public void CustomPlatformEncodingId_IsKeptByScrubbing()
    {
        var a = Ot12Gid('A');
        var scrubbed = TrueTypeSubsetter.Scrub(Ot12SansPlus(4, 238, Ot12Format6(0x41, [a])), [0, a]);
        Assert.Single(Ot12Records(scrubbed), r => (r.Platform, r.Encoding) == (4, 238));
    }

    /// <summary>
    /// 9.3.6 sentence 12 (writer): the subtable of platform 4 needs format 0 or 6 — an OpenType font with CFF outlines (Noto Sans CJK) with a further
    /// (4,0) format 6 subtable, scrubbed: the (4,0) subtable should still be format 0 or 6.
    /// </summary>
    [Fact]
    public void ScrubbedCustomPlatformSubtable_StaysFormat0Or6()
    {
        var noto = OpenTypeFont.Parse(Ot12NotoFile.Value);
        var a = noto.GlyphForUnicode('A');
        var font = Ot12With(Ot12NotoFile.Value, (3, 1, Ot12Sub(noto, 3, 1)), (3, 10, Ot12Sub(noto, 3, 10)), (4, 0, Ot12Format6(0x41, [a])));
        var record = Assert.Single(Ot12Records(TrueTypeSubsetter.Scrub(font, [0, a])), r => r.Platform == 4);
        Assert.Contains(record.Format, (int[])[0, 6]);
    }

    // ------------------------------------------------------------ 9.3.2

    /// <summary>
    /// 9.3.2 sentences 1–7, 9: encoding 3 of the Unicode platform is the BMP only, encoding 4 the full repertoire — Noto Sans CJK with only its (0,4)
    /// subtable finds 𠮟 U+20B9F; with only its (0,3) subtable it finds 葛 U+845B but not U+20B9F.
    /// </summary>
    [Fact]
    public void UnicodePlatformEncodings_BmpOnlyAndFullRepertoire()
    {
        var noto = OpenTypeFont.Parse(Ot12NotoFile.Value);
        var supplementary = noto.GlyphForUnicode(0x20B9F);
        Assert.True(supplementary > 0);
        var full = OpenTypeFont.Parse(Ot12With(Ot12NotoFile.Value, (0, 4, Ot12Sub(noto, 0, 4))));
        Assert.Equal(supplementary, full.GlyphForUnicode(0x20B9F));
        var bmp = OpenTypeFont.Parse(Ot12With(Ot12NotoFile.Value, (0, 3, Ot12Sub(noto, 0, 3))));
        Assert.Equal(0, bmp.GlyphForUnicode(0x20B9F));
        Assert.Equal(noto.GlyphForUnicode(0x845B), bmp.GlyphForUnicode(0x845B));
    }

    /// <summary>
    /// 9.3.2 sentence 11 and 9.3.5 sentences 17–19 (writer, the part that holds): the scrubbed Liberation Sans has its (0,3) and (3,1) subtables as format 4.
    /// </summary>
    [Fact]
    public void ScrubbedBmpSubtables_AreFormat4()
    {
        var records = Ot12Records(TrueTypeSubsetter.Scrub(Ot12SansFile.Value, "Text".Select(Ot12Gid).Prepend(0)));
        Assert.Equal(4, records.Single(r => (r.Platform, r.Encoding) == (0, 3)).Format);
        Assert.Equal(4, records.Single(r => (r.Platform, r.Encoding) == (3, 1)).Format);
    }

    /// <summary>
    /// 9.3.2 sentence 11 and 9.3.5 sentences 17–19 (writer): a font with BMP characters only must have (3,1) as format 4, and (0,3) should be format 4
    /// or 6 — Liberation Sans whose (0,3) and (3,1) map 9 000 consecutive codes to glyphs that never follow each other (one format 4 segment with a
    /// glyph array in the source), scrubbed with all those glyphs kept: (3,1) should stay format 4, (0,3) format 4 or 6.
    /// </summary>
    [Fact]
    public void ScrubbedBmpSubtableWithManyRuns_StaysFormat4()
    {
        const int count = 9000;
        var glyphs = Enumerable.Range(0, count).Select(i => 1 + i * 7 % 2000).ToArray();
        // two segments (the run and the closing one): the run's range offset points from its own field to the glyph array
        var many = Ot12Format4([(0x4E00, 0x4E00 + count - 1, 0, 4), (0xFFFF, 0xFFFF, 1, 0)], glyphs);
        var font = Ot12With(Ot12SansFile.Value, (0, 3, many), (3, 1, many));
        Assert.Equal(glyphs[8999], OpenTypeFont.Parse(font).Lookup(3, 1, 0x4E00 + 8999));
        var scrubbed = TrueTypeSubsetter.Scrub(font, Enumerable.Range(0, 2001));
        var records = Ot12Records(scrubbed);
        Assert.Equal(4, records.Single(r => (r.Platform, r.Encoding) == (3, 1)).Format);
        Assert.Contains(records.Single(r => (r.Platform, r.Encoding) == (0, 3)).Format, (int[])[4, 6]);
        // the glyph array carries every mapping (FG 1150)
        var read = OpenTypeFont.Parse(scrubbed);
        for (var i = 0; i < count; i++) Assert.Equal(glyphs[i], read.Lookup(3, 1, 0x4E00 + i));
    }

    /// <summary>
    /// 9.3.2 sentences 12–14, 9.3.5 sentences 17–19, 9.4.4 sentence 2 and 9.4.10 sentence 4 (writer): scrubbed Noto Sans CJK with the glyphs of
    /// 葛 U+845B and 𠮟 U+20B9F kept — (0,4) and (3,10) are format 12 and map both, (0,3) and (3,1) are format 4 and map only U+845B; the
    /// variation sequences stay in format 14 under (0,5) and only there, filtered: the default sequence 葛 U+E0101 is kept, the non-default
    /// 葛 U+E0100 (glyph 62001, removed) is gone, and every sequence left shows a glyph kept (FG 1160; before the subtable was left out).
    /// </summary>
    [Fact]
    public void ScrubbedCjkFont_FullRepertoireFormat12_BmpFormat4_VariationSequencesFiltered()
    {
        var noto = OpenTypeFont.Parse(Ot12NotoFile.Value);
        var (kanji, supplementary) = (noto.GlyphForUnicode(0x845B), noto.GlyphForUnicode(0x20B9F));
        var scrubbed = Ot12NotoScrubbed.Value;
        var records = Ot12Records(scrubbed);
        var font = OpenTypeFont.Parse(scrubbed);
        foreach (var (platform, encoding) in (ReadOnlySpan<(int, int)>)[(0, 4), (3, 10)])
        {
            Assert.Equal(12, records.Single(r => (r.Platform, r.Encoding) == (platform, encoding)).Format);
            Assert.Equal(kanji, font.Lookup(platform, encoding, 0x845B));
            Assert.Equal(supplementary, font.Lookup(platform, encoding, 0x20B9F));
        }
        foreach (var (platform, encoding) in (ReadOnlySpan<(int, int)>)[(0, 3), (3, 1)])
        {
            Assert.Equal(4, records.Single(r => (r.Platform, r.Encoding) == (platform, encoding)).Format);
            Assert.Equal(kanji, font.Lookup(platform, encoding, 0x845B));
            Assert.Equal(0, font.Lookup(platform, encoding, 0x20B9F));
        }
        Assert.Equal(14, records.Single(r => (r.Platform, r.Encoding) == (0, 5)).Format);
        Assert.DoesNotContain(records, r => r.Format == 14 && (r.Platform, r.Encoding) != (0, 5));
        var shaper = TextShaper.For(font);
        Assert.Equal(kanji, Assert.Single(shaper.Shape("葛\U000E0101")).Glyph);
        Assert.Equal(kanji, Assert.Single(shaper.Shape("葛\U000E0100")).Glyph);
        // every sequence written: a default one whose base character maps to a glyph kept, a non-default one to a glyph kept
        var kept = new HashSet<int> { kanji, supplementary, noto.GlyphForUnicode('A') };
        var f14 = Ot12Sub(font, 0, 5);
        int U24(int at) => f14[at] << 16 | f14[at + 1] << 8 | f14[at + 2];
        int U32(int at) => (int)BinaryPrimitives.ReadUInt32BigEndian(f14.AsSpan(at));
        var selectors = new List<int>();
        for (var i = 0; i < U32(6); i++)
        {
            var at = 10 + 11 * i;
            selectors.Add(U24(at));
            if (U32(at + 3) is > 0 and var d)
                for (var k = 0; k < U32(d); k++)
                    for (var c = U24(d + 4 + 4 * k); c <= U24(d + 4 + 4 * k) + f14[d + 7 + 4 * k]; c++) Assert.Contains(font.GlyphForUnicode(c), kept);
            if (U32(at + 7) is > 0 and var n)
                for (var k = 0; k < U32(n); k++) Assert.Contains(BinaryPrimitives.ReadUInt16BigEndian(f14.AsSpan(n + 7 + 5 * k)), kept.Select(g => (ushort)g));
        }
        Assert.Contains(0xE0101, selectors);
        Assert.Equal(selectors.Order(), selectors);
    }

    /// <summary>
    /// 9.3.2 sentence 15 (writer): encoding 6 only with format 13 and format 13 only under (0,6) — Liberation Sans with a further (0,6) format 13
    /// subtable mapping 0x41–0x5A to „A“, scrubbed: (0,6) should still be format 13 (one group).
    /// </summary>
    [Fact]
    public void ScrubbedFormat13Subtable_StaysFormat13()
    {
        var a = Ot12Gid('A');
        var scrubbed = TrueTypeSubsetter.Scrub(Ot12SansPlus(0, 6, Ot12Format12([(0x41, 0x5A, a)], format: 13)), [0, a]);
        var record = Assert.Single(Ot12Records(scrubbed), r => (r.Platform, r.Encoding) == (0, 6));
        Assert.Equal(13, record.Format);
    }

    // ------------------------------------------------------------ 9.3.5, 9.4.3

    /// <summary>
    /// 9.3.5 sentences 8–15 and 9.4.3 sentences 1–34 (reader and writer): format 2 (mixed one- and two-byte codes, the usual table of the old CJK
    /// encodings) is read — Liberation Sans with a further (3,2) format 2 subtable (subheader 0 maps the one-byte 0x41 to „A“, subheader 1 for
    /// the first byte 0x81 maps 0x8140 and 0x8141 to glyphs 5 and 6, a further one with idDelta 0xFFFE): 0x41 gives „A“, 0x8140 and 0x8141 give
    /// 5 and 6, 0x8142 (beyond entryCount) and the lead byte 0x81 alone give 0, 0x8240 gives 1 + 0xFFFE modulo 65 536; a subheader key pointing
    /// beyond the table gives 0 without error; the scrubbed font leaves the record out as since No. 1143 (its byte codes are not those of format 4)
    /// (FG 1160).
    /// </summary>
    [Fact]
    public void Format2Subtable_IsRead_ScrubLeavesItOut()
    {
        var a = Ot12Gid('A');
        // header 6, subHeaderKeys 512, three subheaders of 8 bytes; glyph arrays from 542: 256 for subheader 0, 2 for subheader 1, 1 for subheader 2
        var w = new Ot12Bytes().U16(2).U16(0).U16(0);
        for (var k = 0; k < 256; k++) w.U16(k == 0x81 ? 8 : k == 0x82 ? 16 : 0);
        const int arrays = 6 + 512 + 3 * 8;
        w.U16(0).U16(256).U16(0).U16(arrays - (6 + 512 + 6));                       // subheader 0: all one-byte codes
        w.U16(0x40).U16(2).U16(0).U16(arrays + 512 - (6 + 512 + 8 + 6));            // subheader 1: 0x8140, 0x8141
        w.U16(0x40).U16(1).U16(0xFFFE).U16(arrays + 516 - (6 + 512 + 16 + 6));      // subheader 2: 0x8240 with idDelta 0xFFFE
        for (var c = 0; c < 256; c++) w.U16(c == 0x41 ? a : 0);
        w.U16(5).U16(6).U16(1);
        w.SetU16(2, w.Length);
        var font = Ot12SansPlus(3, 2, w.ToArray());
        var parsed = OpenTypeFont.Parse(font);
        Assert.True(parsed.HasCmap(3, 2));
        Assert.Equal(new[] { a, 5, 6, 0, 0xFFFF, 0 }, ((int[])[0x41, 0x8140, 0x8141, 0x8142, 0x8240, 0x81]).Select(c => parsed.Lookup(3, 2, c)));
        var beyond = w.ToArray();
        beyond[6 + 2 * 0x83 + 1] = 0xF8;                                    // key of 0x83: subheader 31, beyond the table
        Assert.Equal(0, OpenTypeFont.Parse(Ot12SansPlus(3, 2, beyond)).Lookup(3, 2, 0x8340));
        Assert.DoesNotContain(Ot12Records(TrueTypeSubsetter.Scrub(font, [0, a, 5, 6])), r => (r.Platform, r.Encoding) == (3, 2));
    }

    /// <summary>
    /// 9.3.5 sentence 23 (reader, today's behaviour): in old use the one-byte code 0x20 of a symbol font maps to OS/2.usFirstCharIndex — a symbolic
    /// simple TrueType font whose program has only a (3,0) subtable from 0xF041 and usFirstCharIndex 0xF041: code 0x41 draws the glyph of 0xF041,
    /// code 0x20 draws nothing (the prefixes of ISO 32000-2 9.6.5.4 are used, not usFirstCharIndex).
    /// </summary>
    [Fact]
    public void SymbolCmap_Code0x20_IsNotMappedThroughUsFirstCharIndex()
    {
        var sans = Ot12Sans.Value;
        var a = Ot12Gid('A');
        var (os2At, os2Length) = sans.Table("OS/2")!.Value;
        var os2 = sans.FileData.AsSpan(os2At, os2Length).ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(64), 0xF041);
        var program = OpenTypeFont.Parse(ColorFontBuilder.WithTables(Ot12SansFile.Value, new Dictionary<string, byte[]?>
        {
            ["cmap"] = Ot12Cmap((3, 0, Ot12Format4Run(0xF041, 0xF05A, a))),
            ["OS/2"] = os2,
        }));
        var (doc, font) = Ot12SimpleFont(4);
        using (doc)
        {
            Assert.Equal(a, font.DrawnGlyphWith(program, 0x41, 1));
            Assert.Equal(-1, font.DrawnGlyphWith(program, 0x20, 1));
        }
    }

    // ------------------------------------------------------------ 9.4.1, 9.4.2

    /// <summary>
    /// 9.4.1 sentences 1–2 (writer): outside the Macintosh platform the language field is 0 — in the cmaps of scrubbed Liberation Sans and Noto Sans
    /// CJK and of a font rebuilt with a (3,0) cmap every subtable not of platform 1 has language 0 (format 14 under (0,5) has no language field).
    /// </summary>
    [Fact]
    public void WrittenSubtables_LanguageZeroOutsideTheMacintoshPlatform()
    {
        var a = Ot12Gid('A');
        var rebuilt = TrueTypeSubsetter.Rebuild(Ot12SansFile.Value, new Dictionary<int, int> { [a] = 1000 }, [], [(0xF041, a)]);
        foreach (var font in new[] { TrueTypeSubsetter.Scrub(Ot12SansFile.Value, [0, a]), Ot12NotoScrubbed.Value, rebuilt })
            Assert.All(Ot12Records(font).Where(r => r.Platform != 1 && r.Format != 14), r => Assert.Equal(0, r.Language));
        var symbol = Assert.Single(Ot12Records(rebuilt));
        Assert.Equal((3, 0), (symbol.Platform, symbol.Encoding));
    }

    /// <summary>
    /// 9.4.2 sentences 1–10 (reader): format 0 maps the 256 one-byte codes to glyph numbers of one byte — Liberation Sans whose (1,0) is a format 0
    /// subtable mapping 0x41 to „A“: 0x41 gives „A“, 256 gives 0; a format 0 subtable cut to 100 bytes at the end of the file gives 0 without error.
    /// </summary>
    [Fact]
    public void Format0_MapsOneByteCodes_ACutTableGivesNothing()
    {
        var sans = Ot12Sans.Value;
        var a = Ot12Gid('A');
        var f0 = Ot12Format0(new Dictionary<int, int> { [0x41] = a });
        var font = OpenTypeFont.Parse(Ot12With(Ot12SansFile.Value, (0, 3, Ot12Sub(sans, 0, 3)), (1, 0, f0), (3, 1, Ot12Sub(sans, 3, 1))));
        Assert.Equal(a, font.Lookup(1, 0, 0x41));
        Assert.Equal(0, font.Lookup(1, 0, 256));
        // the cut cmap moved to the end of the file: the reader must not read past it
        var file = Ot12With(Ot12SansFile.Value, (1, 0, f0[..100]));
        var parsed = OpenTypeFont.Parse(file);
        var (at, length) = parsed.Table("cmap")!.Value;
        int tables = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(4));
        var record = Enumerable.Range(0, tables).Select(i => 12 + 16 * i).Single(p => file.AsSpan(p, 4).SequenceEqual("cmap"u8));
        var moved = file.Concat(file.AsSpan(at, length).ToArray()).ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(moved.AsSpan(record + 8), (uint)file.Length);
        var cut = OpenTypeFont.Parse(moved);
        Assert.Equal(0, cut.Lookup(1, 0, 0x41));
    }

    // ------------------------------------------------------------ 9.4.4

    /// <summary>
    /// 9.4.4 sentences 23–25, 31–35, 40 (writer): searchRange, entrySelector and rangeShift follow from the segment count — scrubbed Liberation Sans
    /// keeping „A“, „K“, „T“ and „z“ (four runs and the closing segment) has 8, 2, 2 in its (3,1) subtable; a font rebuilt with 39 symbol codes
    /// (40 segments) has 64, 5, 16.
    /// </summary>
    [Fact]
    public void WrittenFormat4_SearchValuesFollowTheSegmentCount()
    {
        static (int Segments, int Range, int Selector, int Shift) Header(byte[] font, int platform, int encoding)
        {
            var record = Ot12Records(font).Single(r => (r.Platform, r.Encoding) == (platform, encoding));
            var data = OpenTypeFont.Parse(font).FileData;
            int U16(int k) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(record.Offset + k));
            return (U16(6) / 2, U16(8), U16(10), U16(12));
        }
        var scrubbed = TrueTypeSubsetter.Scrub(Ot12SansFile.Value, "AKTz".Select(Ot12Gid).Prepend(0));
        Assert.Equal((5, 8, 2, 2), Header(scrubbed, 3, 1));
        var a = Ot12Gid('A');
        var rebuilt = TrueTypeSubsetter.Rebuild(Ot12SansFile.Value, new Dictionary<int, int> { [a] = 1000 }, [],
            Enumerable.Range(0, 39).Select(i => (0xF020 + 2 * i, a)).ToList());
        Assert.Equal((40, 64, 5, 16), Header(rebuilt, 3, 0));
    }

    /// <summary>
    /// 9.4.4 sentence 39 (reader): searchRange, entrySelector and rangeShift are not relied on — Liberation Sans whose (3,1) has all three set to
    /// 0xFFFF maps „A“ as before.
    /// </summary>
    [Fact]
    public void Format4SearchValues_AreNotRelied()
    {
        var sans = Ot12Sans.Value;
        var sub = Ot12Sub(sans, 3, 1);
        for (var k = 8; k < 14; k++) sub[k] = 0xFF;
        var font = OpenTypeFont.Parse(Ot12With(Ot12SansFile.Value, (3, 1, sub)));
        Assert.Equal(Ot12Gid('A'), font.GlyphForUnicode('A'));
        Assert.Equal(Ot12Gid('z'), font.GlyphForUnicode('z'));
    }

    /// <summary>
    /// 9.4.4 sentences 45–48 (writer and reader): the last segment is 0xFFFF–0xFFFF — every format 4 subtable of scrubbed Liberation Sans ends with
    /// it and maps 0xFFFF to 0; a hand-built format 4 subtable without that segment maps a code above its last end to 0 without error.
    /// </summary>
    [Fact]
    public void Format4_ClosingSegmentIsWritten_AndNotNeededToRead()
    {
        var scrubbed = TrueTypeSubsetter.Scrub(Ot12SansFile.Value, "Text".Select(Ot12Gid).Prepend(0));
        var data = OpenTypeFont.Parse(scrubbed).FileData;
        foreach (var record in Ot12Records(scrubbed).Where(r => r.Format == 4))
        {
            int segments = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(record.Offset + 6)) / 2;
            var lastEnd = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(record.Offset + 14 + (segments - 1) * 2));
            var lastStart = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(record.Offset + 16 + segments * 2 + (segments - 1) * 2));
            Assert.Equal((0xFFFF, 0xFFFF), (lastStart, lastEnd));
            Assert.Equal(0, OpenTypeFont.Parse(scrubbed).Lookup(record.Platform, record.Encoding, 0xFFFF));
        }
        var a = Ot12Gid('A');
        var open = OpenTypeFont.Parse(Ot12With(Ot12SansFile.Value, (3, 1, Ot12Format4([(0x41, 0x5A, a - 0x41, 0)]))));
        Assert.Equal(a, open.Lookup(3, 1, 0x41));
        Assert.Equal(0, open.Lookup(3, 1, 0x7A));
        Assert.Equal(0, open.Lookup(3, 1, 0xFFFF));
    }

    /// <summary>
    /// 9.4.4 sentences 49–56 (reader): with a range offset other than 0 the glyph comes from the glyph array, a value other than 0 plus idDelta —
    /// Noto Sans CJK (range offsets in many segments of (3,1)) maps every code 0x20–0xFFFD in (3,1) as in its (3,10) format 12; a hand-built segment
    /// for 0x41–0x42 with the array values 0 and 7 and idDelta 5 maps 0x41 to 0 and 0x42 to 12.
    /// </summary>
    [Fact]
    public void Format4RangeOffsets_ReadTheGlyphArray()
    {
        var noto = Iso03NotoJp.Value;
        var differing = Enumerable.Range(0x20, 0xFFFE - 0x20).Where(c => noto.Lookup(3, 1, c) != noto.Lookup(3, 10, c)).Take(5).ToList();
        Assert.Empty(differing);
        // two segments: the first's range offset points from its own field over the second's to the array
        var font = OpenTypeFont.Parse(Ot12With(Ot12SansFile.Value, (3, 1, Ot12Format4([(0x41, 0x42, 5, 4), (0xFFFF, 0xFFFF, 1, 0)], [0, 7]))));
        Assert.Equal(0, font.Lookup(3, 1, 0x41));
        Assert.Equal(12, font.Lookup(3, 1, 0x42));
    }

    /// <summary>
    /// 9.4.4 sentences 57, 59–60 (reader and writer): idDelta arithmetic is modulo 65536 — code 10 with idDelta −20 gives glyph 65526, code 0xF041 with
    /// idDelta 0x0FC2 gives glyph 3; every segment the scrubber writes has idDelta = (glyph − start code) modulo 65536.
    /// </summary>
    [Fact]
    public void Format4Deltas_AreModulo65536()
    {
        var font = OpenTypeFont.Parse(Ot12With(Ot12SansFile.Value, (3, 1, Ot12Format4([(10, 10, -20, 0), (0xF041, 0xF041, 0x0FC2, 0), (0xFFFF, 0xFFFF, 1, 0)]))));
        Assert.Equal(65526, font.Lookup(3, 1, 10));
        Assert.Equal(3, font.Lookup(3, 1, 0xF041));

        var scrubbed = TrueTypeSubsetter.Scrub(Ot12SansFile.Value, "Text".Select(Ot12Gid).Prepend(0));
        var parsed = OpenTypeFont.Parse(scrubbed);
        var record = Ot12Records(scrubbed).Single(r => (r.Platform, r.Encoding) == (3, 1));
        var data = parsed.FileData;
        int segments = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(record.Offset + 6)) / 2;
        for (var i = 0; i < segments - 1; i++)
        {
            int start = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(record.Offset + 16 + segments * 2 + i * 2));
            int delta = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(record.Offset + 16 + segments * 4 + i * 2));
            Assert.Equal((parsed.Lookup(3, 1, start) - start) & 0xFFFF, delta);
        }
    }

    // ------------------------------------------------------------ 9.4.5 to 9.4.9

    /// <summary>
    /// 9.4.5 sentences 1–12 (reader): format 6 maps one range (first code, count, glyph array), codes outside give glyph 0 — the (1,0) format 6
    /// subtable of Liberation Sans maps 0x41 like „A“ in Unicode, the code after its range and the one before it give 0.
    /// </summary>
    [Fact]
    public void Format6_MapsOneRange_NothingOutside()
    {
        var sans = Ot12Sans.Value;
        var sub = Ot12Sub(sans, 1, 0);
        Assert.Equal(6, BinaryPrimitives.ReadUInt16BigEndian(sub));
        int first = BinaryPrimitives.ReadUInt16BigEndian(sub.AsSpan(6));
        int count = BinaryPrimitives.ReadUInt16BigEndian(sub.AsSpan(8));
        Assert.Equal(sans.GlyphForUnicode('A'), sans.Lookup(1, 0, 0x41));
        Assert.Equal(0, sans.Lookup(1, 0, first + count));
        Assert.Equal(0, sans.Lookup(1, 0, first - 1));
    }

    /// <summary>
    /// 9.4.6 sentences 1–37 (reader): format 8 (mixed 16- and 32-bit codes) is read — Liberation Sans with a further (3,10) format 8 subtable
    /// mapping 0x41–0x5A and, as a 32-bit code (the halves D800 DC00 of its surrogate pair, marked in is32), U+10000: „A“ and „B“ come from it,
    /// U+10000 gives glyph 9; a group count far beyond the table reads only the groups inside it, without error (FG 1160).
    /// </summary>
    [Fact]
    public void Format8Subtable_IsRead()
    {
        var a = Ot12Gid('A');
        var is32 = new byte[8192];
        is32[0xD800 / 8] = 0x80;
        var w = new Ot12Bytes().U16(8).U16(0).U32(12 + 8192 + 4 + 24).U32(0).Bytes(is32).U32(2).U32(0x41).U32(0x5A).U32(a)
            .U32(0xD800DC00L).U32(0xD800DC00L).U32(9);
        var font = OpenTypeFont.Parse(Ot12SansPlus(3, 10, w.ToArray()));
        Assert.Equal(a, font.Lookup(3, 10, 0x41));
        Assert.Equal(a + 1, font.GlyphForUnicode('B'));
        Assert.Equal(9, font.GlyphForUnicode(0x10000));
        var huge = w.ToArray();
        huge[12 + 8192] = 0x7F;                                              // numGroups about two billion
        Assert.Equal(a, OpenTypeFont.Parse(Ot12SansPlus(3, 10, huge)).Lookup(3, 10, 0x41));
    }

    /// <summary>
    /// 9.4.7 sentences 1–14 (reader): format 10 (trimmed array of 32-bit codes) is read — Liberation Sans with a further (3,10) format 10 subtable
    /// for U+10000 and U+10001: they give „A“ and the glyph after it, U+FFFF and U+10002 give 0; a count of chars far beyond the table reads only
    /// the glyphs inside it, without error (FG 1160).
    /// </summary>
    [Fact]
    public void Format10Subtable_IsRead()
    {
        var a = Ot12Gid('A');
        var w = new Ot12Bytes().U16(10).U16(0).U32(24).U32(0).U32(0x10000).U32(2).U16(a).U16(a + 1);
        var font = OpenTypeFont.Parse(Ot12SansPlus(3, 10, w.ToArray()));
        Assert.Equal(new[] { 0, a, a + 1, 0 }, ((int[])[0xFFFF, 0x10000, 0x10001, 0x10002]).Select(c => font.Lookup(3, 10, c)));
        var huge = w.ToArray();
        huge[16] = 0x7F;                                                     // numChars about two billion
        Assert.Equal(a + 1, OpenTypeFont.Parse(Ot12SansPlus(3, 10, huge)).Lookup(3, 10, 0x10001));
    }

    /// <summary>
    /// 9.4.8 sentences 22–23 (writer and reader, the part that holds): groups ascending by start code, each end below the next start — the format 12
    /// subtables of scrubbed Noto Sans CJK are so; a hand-built format 12 subtable with its groups out of order is read without error (a code gives
    /// its glyph or 0).
    /// </summary>
    [Fact]
    public void Format12Groups_WrittenAscending_UnsortedReadWithoutError()
    {
        var scrubbed = Ot12NotoScrubbed.Value;
        var data = OpenTypeFont.Parse(scrubbed).FileData;
        foreach (var record in Ot12Records(scrubbed).Where(r => r.Format == 12))
        {
            var groups = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record.Offset + 12));
            for (var i = 1; i < groups; i++)
            {
                var previousEnd = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record.Offset + 16 + (i - 1) * 12 + 4));
                var start = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record.Offset + 16 + i * 12));
                Assert.True(start > previousEnd, $"Gruppe {i} von ({record.Platform},{record.Encoding})");
            }
        }
        var (a, lower) = (Ot12Gid('A'), Ot12Gid('a'));
        var font = OpenTypeFont.Parse(Ot12SansPlus(3, 10, Ot12Format12([(0x61, 0x7A, lower), (0x41, 0x5A, a)])));
        Assert.Contains(font.Lookup(3, 10, 0x41), (int[])[0, a]);
        Assert.Contains(font.Lookup(3, 10, 0x61), (int[])[0, lower]);
    }

    /// <summary>
    /// 9.4.8 sentences 22–23 (writer): the groups the scrubber writes must not overlap — Liberation Sans with a further (3,10) format 12 subtable whose
    /// groups 0x41–0x50 and 0x48–0x60 overlap, scrubbed: the groups of the new (3,10) subtable should be strictly ascending and without overlap.
    /// </summary>
    [Fact]
    public void OverlappingSourceGroups_AreNotWrittenOverlapping()
    {
        var a = Ot12Gid('A');
        var font = Ot12SansPlus(3, 10, Ot12Format12([(0x41, 0x50, a), (0x48, 0x60, a + 7)]));
        var scrubbed = TrueTypeSubsetter.Scrub(font, Enumerable.Range(0, a + 40));
        var record = Ot12Records(scrubbed).Single(r => (r.Platform, r.Encoding) == (3, 10));
        var data = OpenTypeFont.Parse(scrubbed).FileData;
        var groups = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record.Offset + 12));
        for (var i = 1; i < groups; i++)
            Assert.True(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record.Offset + 16 + i * 12))
                        > BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record.Offset + 16 + (i - 1) * 12 + 4)), $"Gruppe {i}");
    }

    /// <summary>
    /// 9.4.9 sentences 1, 4–19 (reader): format 13 maps a whole group to one glyph — Liberation Sans with a further (0,6) format 13 subtable, one group
    /// 0x4E00–0x9FFF to glyph 5: 0x4E00 and 0x9FFF give 5, 0xA000 gives 0.
    /// </summary>
    [Fact]
    public void Format13_MapsTheWholeGroupToOneGlyph()
    {
        var font = OpenTypeFont.Parse(Ot12SansPlus(0, 6, Ot12Format12([(0x4E00, 0x9FFF, 5)], format: 13)));
        Assert.Equal(5, font.Lookup(0, 6, 0x4E00));
        Assert.Equal(5, font.Lookup(0, 6, 0x9FFF));
        Assert.Equal(0, font.Lookup(0, 6, 0xA000));
    }
}
