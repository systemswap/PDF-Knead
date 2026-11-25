using System.Diagnostics;
using PdfKnead.Core.Fonts;

namespace PdfKnead.Tests.Fonts;

/// <summary>Tests for the limits that protect glyph reading against crafted fonts.</summary>
public sealed class GlyphLimitTests
{
    private static void U16(List<byte> b, int v) => b.AddRange([(byte)(v >> 8), (byte)v]);

    private static void U32(List<byte> b, int v) => b.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

    /// <summary>Builds a simple glyph with one contour of <paramref name="points"/> points, either distinct or all at the same position.</summary>
    private static List<byte> SimpleGlyph(int points, bool distinct)
    {
        var glyph = new List<byte>();
        U16(glyph, 1);
        glyph.AddRange(new byte[8]);
        U16(glyph, points - 1);   // end of the only contour
        U16(glyph, 0);            // no instructions
        // Flags (repeated): on curve, y same as the previous point; x likewise, or as a positive byte
        for (var left = points; left > 0;)
        {
            var run = Math.Min(left, 256);
            glyph.AddRange([(byte)(distinct ? 0x3B : 0x39), (byte)(run - 1)]);
            left -= run;
        }
        if (distinct)
            for (var i = 0; i < points; i++) glyph.Add((byte)(1 + i % 5));
        while (glyph.Count % 4 != 0) glyph.Add(0);
        return glyph;
    }

    /// <summary>Builds a minimal TrueType font from glyf and loca (long offsets).</summary>
    private static byte[] Sfnt(List<byte> glyf, IEnumerable<int> loca)
    {
        var locaBytes = new List<byte>();
        foreach (var offset in loca) U32(locaBytes, offset);
        var head = new byte[54];
        head[18] = 0x04;   // 1024 units per em
        head[51] = 1;      // long loca format
        var tables = new (string Tag, byte[] Data)[] { ("glyf", [.. glyf]), ("head", head), ("loca", [.. locaBytes]) };
        var font = new List<byte> { 0, 1, 0, 0 };
        U16(font, tables.Length);
        font.AddRange(new byte[6]);
        var at = 12 + 16 * tables.Length;
        foreach (var (tag, data) in tables)
        {
            font.AddRange(System.Text.Encoding.ASCII.GetBytes(tag));
            font.AddRange(new byte[4]);
            U32(font, at);
            U32(font, data.Length);
            at += (data.Length + 3) & ~3;
        }
        foreach (var (_, data) in tables)
        {
            font.AddRange(data);
            while (font.Count % 4 != 0) font.Add(0);
        }
        return [.. font];
    }

    /// <summary>Builds a font whose glyph 0 is a simple glyph with <paramref name="points"/> points and whose glyph 1 is a composite of <paramref name="parts"/> copies of glyph 0.</summary>
    private static byte[] CompositeFont(int points, int parts)
    {
        var simple = SimpleGlyph(points, distinct: false);
        var composite = new List<byte>();
        U16(composite, -1);
        composite.AddRange(new byte[8]);
        for (var i = 0; i < parts; i++)
        {
            U16(composite, (i + 1 < parts ? 0x20 : 0) | 0x2);   // more components follow, offsets as bytes
            U16(composite, 0);
            composite.AddRange([(byte)(i % 7), 0]);
        }
        while (composite.Count % 4 != 0) composite.Add(0);
        return Sfnt([.. simple, .. composite], [0, simple.Count, simple.Count + composite.Count]);
    }

    /// <summary>
    /// Builds a font with <paramref name="glyphs"/> glyphs: the even ones all point to the same outline with <paramref name="points"/>
    /// distinct points (loca alternates between its start and end), the odd ones are empty.
    /// </summary>
    private static byte[] SharedOutlineFont(int points, int glyphs)
    {
        var simple = SimpleGlyph(points, distinct: true);
        return Sfnt(simple, Enumerable.Range(0, glyphs + 1).Select(g => g % 2 == 0 ? 0 : simple.Count));
    }

    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void CompositeGlyph_HasAPointBudget()
    {
        // 10 000 components × 65 536 points: without the point budget this took 13.7 s and 29.7 GB for a single glyph
        var font = OpenTypeFont.Parse(CompositeFont(65536, 10_000));
        var before = GC.GetAllocatedBytesForCurrentThread();   // the glyph is built on this thread, so parallel tests are not counted
        var watch = Stopwatch.StartNew();
        var outline = font.Outline(1);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(watch.Elapsed.TotalSeconds < 2 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms, {allocated >> 20} MB belegt");
        Assert.True(allocated < 256L << 20, $"{allocated >> 20} MB belegt");
        Assert.True(outline.IsEmpty, "zu aufwendige Glyphe: leer statt halb");
        // an ordinary composite glyph is still drawn
        var normal = OpenTypeFont.Parse(CompositeFont(40, 3));
        Assert.False(normal.Outline(1).IsEmpty);
    }

    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void CmapWithThousandsOfRecords_IsCapped_RealRecordsStay()
    {
        // Found by fuzzing: a cmap claiming 65 535 encoding records (read from the data that follows). Without the cap every lookup
        // tried all of them, and text editing needed 12.7 s for the codes of one font
        var data = (byte[])EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf").Clone();
        var original = OpenTypeFont.Parse(data);
        int tables = data[4] << 8 | data[5];
        var cmap = Enumerable.Range(0, tables).Select(i => 12 + 16 * i)
            .Where(p => System.Text.Encoding.ASCII.GetString(data, p, 4) == "cmap")
            .Select(p => data[p + 8] << 24 | data[p + 9] << 16 | data[p + 10] << 8 | data[p + 11]).Single();
        data[cmap + 2] = data[cmap + 3] = 0xFF;
        var font = OpenTypeFont.Parse(data);
        Assert.InRange(font.CmapSubtables.Count, original.CmapSubtables.Count, 64);
        Assert.Equal(original.CmapSubtables, font.CmapSubtables.Take(original.CmapSubtables.Count));
        Assert.Equal(original.GlyphForUnicode('A'), font.GlyphForUnicode('A'));
        var watch = Stopwatch.StartNew();
        for (var code = 0; code < 256; code++)
            foreach (var (platform, encoding) in font.CmapSubtables) font.Lookup(platform, encoding, code);
        Assert.True(watch.Elapsed.TotalSeconds < 2 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void OutlineCache_HasAByteBudget()
    {
        // Glyphs sharing one outline with 30 000 points: without a byte budget each one is cached separately (4000 glyphs: 1031 MB).
        // Here 600 glyphs, which would take a little over 140 MB without the budget
        var font = OpenTypeFont.Parse(SharedOutlineFont(30_000, 1200));
        var drawn = 0;
        for (var g = 0; g < 1200; g += 2)
            if (!font.Outline(g).IsEmpty) drawn++;
        Assert.Equal(600, drawn);   // every glyph is still drawn; only the cache stops keeping all of them
        Assert.InRange(font.CachedOutlineBytes, 32L << 20, 64L << 20);
    }

    /// <summary>
    /// A glyph number of int.MaxValue (from a damaged map) reads as no glyph: checking it by adding one overflowed, passed, and the read went
    /// beyond the table (found by the redaction focus of the fuzzer; docs/FEHLERGEDAECHTNIS.md, No. 379).
    /// </summary>
    /// <summary>
    /// Without a glyph count in maxp a font has as many glyphs as loca holds — but never more than 65 535, the most a glyph number names:
    /// a loca of 12 MB (zeros, a few kilobytes compressed) gave three million glyphs, which the PDF/A check, the CIDSet and the subset
    /// went through (docs/FEHLERGEDAECHTNIS.md, No. 1138).
    /// </summary>
    [Fact]
    public void LocaWithoutGlyphCount_HoldsAtMost65535Glyphs()
    {
        var data = Sfnt([], Enumerable.Repeat(0, 3_000_000));
        Assert.Equal(65_535, OpenTypeFont.Parse(data).GlyphCount);
        Assert.Equal(65_535, OpenTypeFont.Parse(TrueTypeSubsetter.Subset(data, [0])).GlyphCount);
    }

    /// <summary>
    /// 64 cmap records with their own platform and encoding, all naming one format-13 subtable whose single group maps all of Unicode to
    /// one glyph that stays: cleaning the font read, held and wrote each of them with a million codes — some gigabytes from a few hundred
    /// bytes. Records sharing a subtable share the new one, and all together read at most a million codes (docs/FEHLERGEDAECHTNIS.md,
    /// No. 1138).
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void CmapRecordsSharingAWholeUnicodeGroup_StayWithinMemory()
    {
        var liberation = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var a = OpenTypeFont.Parse(liberation).GlyphForUnicode('A');
        var cmap = new List<byte>();
        U16(cmap, 0);
        U16(cmap, 64);
        var subtable = 4 + 64 * 8;
        for (var i = 0; i < 64; i++)
        {
            U16(cmap, 4);          // platform 4 (custom), encodings 0–63
            U16(cmap, i);
            U32(cmap, subtable);
        }
        U16(cmap, 13);
        U16(cmap, 0);
        U32(cmap, 28);
        U32(cmap, 0);
        U32(cmap, 1);              // format 13, one group: U+0000–U+10FFFF → „A“
        U32(cmap, 0);
        U32(cmap, 0x10FFFF);
        U32(cmap, a);
        var font = ColorFontBuilder.WithTables(liberation, new Dictionary<string, byte[]?> { ["cmap"] = [.. cmap] });
        var watch = Stopwatch.StartNew();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var cleaned = TrueTypeSubsetter.Scrub(font, [0, a]);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 400_000_000, $"{allocated / 1_000_000} MB angelegt");
        Assert.True(watch.ElapsedMilliseconds < 5000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
        Assert.True(cleaned.Length < liberation.Length + 20_000_000, $"{cleaned.Length} Bytes");
        Assert.Equal(a, OpenTypeFont.Parse(cleaned).Lookup(4, 63, 'A'));
    }

    /// <summary>
    /// OpenType head 23: indexToLocFormat names the format of loca. The first font of a collection got a head for long offsets while its
    /// short loca stayed — the glyph had no outline, and the PDF/A copy could not rebuild the font (docs/FEHLERGEDAECHTNIS.md, No. 1143).
    /// </summary>
    [Fact]
    public void FirstOfCollection_ShortLoca_BecomesLong()
    {
        var glyph = SimpleGlyph(3, distinct: true);
        while (glyph.Count % 4 != 0) glyph.Add(0);
        var head = new byte[54];
        head[18] = 0x04;                                  // 1024 units per em, short loca (index 50 stays 0)
        var maxp = new List<byte> { 0, 0, 0x50, 0 };      // version 0.5 …
        U16(maxp, 1);                                     // … one glyph
        var loca = new List<byte>();
        U16(loca, 0);
        U16(loca, glyph.Count / 2);
        var tables = new (string Tag, byte[] Data)[] { ("glyf", [.. glyph]), ("head", head), ("loca", [.. loca]), ("maxp", [.. maxp]) };
        // a collection of one font: ttcf header (12 bytes and one offset), the font's directory at 16, offsets from the start of the file
        var file = new List<byte>();
        file.AddRange("ttcf"u8.ToArray());
        U32(file, 0x00010000);
        U32(file, 1);
        U32(file, 16);
        file.AddRange([0, 1, 0, 0]);
        U16(file, tables.Length);
        file.AddRange(new byte[6]);
        var at = 16 + 12 + 16 * tables.Length;
        foreach (var (tag, data) in tables)
        {
            file.AddRange(System.Text.Encoding.ASCII.GetBytes(tag));
            file.AddRange(new byte[4]);
            U32(file, at);
            U32(file, data.Length);
            at += (data.Length + 3) & ~3;
        }
        foreach (var (_, data) in tables)
        {
            file.AddRange(data);
            while (file.Count % 4 != 0) file.Add(0);
        }
        var first = OpenTypeFont.Parse(TrueTypeSubsetter.FirstOfCollection([.. file]));
        Assert.True(first.Intact);
        Assert.False(first.Outline(0).IsEmpty);
    }

    [Fact]
    public void GlyphNumberAtTheLimitOfInt_ReadsAsNoGlyph()
    {
        var font = OpenTypeFont.Parse(EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf"));
        Assert.True(font.Outline(int.MaxValue).IsEmpty);
        Assert.False(font.HasGlyphData(int.MaxValue));
    }
}
