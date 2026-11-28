using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, tables that depend on glyphs and instance fonts (docs/FEHLERGEDAECHTNIS.md, No. 1172): compression with all tables leaves out the
/// tables that name glyphs or describe them per size (BASE, JSTF, LTSH, MATH, VDMX, EBSC, EBLC, EBDT), keeps hdmx and kern; scrubbing leaves
/// out MERG as well; a subset, the compressed and scrubbed copies and the PDF/A rebuild of a variable TrueType font end their prep with an IDEF
/// for GETVARIATION that gives the default instance; cleaning the notices keeps the names STAT and fvar refer to; a NULL offset to an
/// ItemVariationData of a CFF2 VariationStore means no data.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (FG 1172)

    private static byte[] Fg1172Sans => Ot27SansFile.Value;

    private static int Fg1172Glyphs => Ot27Sans.Value.GlyphCount;

    /// <summary>LTSH version 0 for every glyph: yPels 1, glyph „B“ 40.</summary>
    private static byte[] Fg1172Ltsh()
    {
        var w = new Ot18Writer().U16(0, Fg1172Glyphs);
        for (var g = 0; g < Fg1172Glyphs; g++) w.U8(g == Ot27Glyph('B') ? 40 : 1);
        return w.ToArray();
    }

    /// <summary>hdmx version 0 with device records for 9 and 12 ppem (every glyph 5 and 7 pixels wide), records padded to 4 bytes.</summary>
    private static byte[] Fg1172Hdmx()
    {
        var size = (2 + Fg1172Glyphs + 3) & ~3;
        var w = new Ot18Writer().U16(0, 2).U32(size);
        foreach (var (ppem, width) in (ReadOnlySpan<(int, int)>)[(9, 5), (12, 7)])
        {
            var start = w.Length;
            w.U8(ppem).U8(width);
            for (var g = 0; g < Fg1172Glyphs; g++) w.U8(width);
            while (w.Length - start < size) w.U8(0);
        }
        return w.ToArray();
    }

    /// <summary>VDMX version 1: one ratio group for every ratio, one record for 9 ppem.</summary>
    private static byte[] Fg1172Vdmx() =>
        new Ot18Writer().U16(1, 1, 1).U8(1).U8(0).U8(0).U8(0).U16(12).U16(1).U8(9).U8(9).U16(9, 9, -2 & 0xFFFF).ToArray();

    /// <summary>
    /// MERG version 0 with mergeClassCount 2: a class definition (format 1) with „A“ and „V“ in class 1, merge entry (1, 1) = 0x01 (merge
    /// left to right).
    /// </summary>
    private static byte[] Fg1172Merg()
    {
        int a = Ot27Glyph('A'), v = Ot27Glyph('V'), first = Math.Min(a, v), count = Math.Abs(v - a) + 1;
        var classDefLength = 6 + 2 * count;
        var w = new Ot18Writer().U16(0, 2, 12 + classDefLength, 1, 10).U16(12);
        w.U16(1, first, count);
        for (var g = first; g < first + count; g++) w.U16(g == a || g == v ? 1 : 0);
        w.U8(0).U8(0).U8(0).U8(1);
        return w.ToArray();
    }

    /// <summary>
    /// The tables that name glyphs (variants, stretch glyphs, contour points of baselines) or describe them per size (thresholds, heights, bitmaps)
    /// — each small but well formed.
    /// </summary>
    private static byte[] Fg1172Table(string tag)
    {
        var (eblc, ebdt, ebsc) = Ot18Bitmaps(Ot27Glyph('A'), 1, 1, 1, 0);
        return tag switch
        {
            // BASE 1.0 with a HorizAxis: BaseTagList romn and ideo, no scripts
            "BASE" => new Ot18Writer().U16(1, 0, 8, 0).U16(4, 0).U16(2).Tag("ideo").Tag("romn").ToArray(),
            // JSTF 1.0 without scripts
            "JSTF" => new Ot18Writer().U16(1, 0, 0).ToArray(),
            "LTSH" => Fg1172Ltsh(),
            // MATH 1.0: MathConstants, MathGlyphInfo and MathVariants left empty (offsets 0)
            "MATH" => new Ot18Writer().U16(1, 0, 0, 0, 0).ToArray(),
            "VDMX" => Fg1172Vdmx(),
            "EBSC" => ebsc,
            "EBLC" => eblc,
            "EBDT" => ebdt,
            _ => throw new ArgumentException(tag),
        };
    }

    /// <summary>The text of name ID <paramref name="id"/> (3, 1, 0x409) of a name table.</summary>
    private static string? Fg1172Name(byte[] name, int id)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(2)), strings = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(4));
        for (var i = 0; i < count; i++)
        {
            var r = name.AsSpan(6 + i * 12);
            if (BinaryPrimitives.ReadUInt16BigEndian(r) == 3 && BinaryPrimitives.ReadUInt16BigEndian(r[6..]) == id)
                return Encoding.BigEndianUnicode.GetString(name, strings + BinaryPrimitives.ReadUInt16BigEndian(r[10..]), BinaryPrimitives.ReadUInt16BigEndian(r[8..]));
        }
        return null;
    }

    /// <summary>
    /// What the IDEF for opcode 0x91 (GETVARIATION) that runs last in <paramref name="prep"/> pushes (null without one): the program is walked
    /// instruction by instruction (the push instructions with their inline data), the opcode an IDEF defines is the value pushed last before it.
    /// </summary>
    private static int[]? Fg1172LastGetVariation(byte[] prep)
    {
        var stack = new List<int>();
        int[]? result = null;
        var i = 0;
        // one instruction at i; the values it pushes into pushed (other instructions push nothing here)
        void Step(List<int> pushed)
        {
            int op = prep[i++];
            int bytes = 0, words = 0;
            if (op == 0x40) bytes = prep[i++];
            else if (op == 0x41) words = prep[i++];
            else if (op is >= 0xB0 and <= 0xB7) bytes = op - 0xAF;
            else if (op is >= 0xB8 and <= 0xBF) words = op - 0xB7;
            for (var k = 0; k < bytes; k++) pushed.Add(prep[i++]);
            for (var k = 0; k < words; k++, i += 2) pushed.Add(BinaryPrimitives.ReadInt16BigEndian(prep.AsSpan(i)));
        }
        while (i < prep.Length)
        {
            int op = prep[i];
            if (op is not (0x89 or 0x2C))
            {
                Step(stack);
                continue;
            }
            i++;
            var defined = stack.Count > 0 ? stack[^1] : -1;
            if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
            var body = new List<int>();
            while (i < prep.Length && prep[i] != 0x2D) Step(body);
            i++;
            if (op == 0x89 && defined == 0x91) result = [.. body];
        }
        return result;
    }

    // ------------------------------------------------------------ question 33: compression with all tables

    /// <summary>
    /// OT 43.1, 43.2, 43.3, 43.4 (BASE), OT 48.1 to 48.4 (JSTF), OT 49 sentences 15–21 (LTSH), OT 50.1, 50.2 (MATH), OT 58 sentences 25–44 and
    /// 66–69 (VDMX), OT 46.1, OT 14 (EBSC, EBLC, EBDT), writer: compressing a font keeps every glyph number but empties the outlines of the glyphs
    /// unused — the tables that name glyphs (variants, stretch glyphs, baseline contour points) or describe them per size (LTSH thresholds, VDMX
    /// heights, bitmaps) would then speak of glyphs no longer there (an LTSH threshold 40 for an emptied „B“). Liberation Sans with each of
    /// these tables: the subset keeping all tables (compression), the subset, the scrubbed font and the PDF/A rebuild have none of them (before,
    /// compression kept each byte for byte); the layout (GSUB) stays in the compressed copy.
    /// </summary>
    [Theory]
    [InlineData("BASE")]
    [InlineData("JSTF")]
    [InlineData("LTSH")]
    [InlineData("MATH")]
    [InlineData("VDMX")]
    [InlineData("EBSC")]
    [InlineData("EBLC")]
    [InlineData("EBDT")]
    public void Fg1172_Compression_LeavesOutTablesThatDependOnGlyphs(string tag)
    {
        var font = Ot27SansWith((tag, Fg1172Table(tag)));
        Assert.True(OpenTypeFont.Parse(font).HasTable(tag));
        // the tables are not used for own text either: line metrics and shaping as without them
        var face = Core.Pdf.Editing.ElementFace.For($"fg1172-{tag}", () => OpenTypeFont.Parse(font), false, false, false);
        var plain = Core.Pdf.Editing.ElementFace.For("fg1172-plain", () => OpenTypeFont.Parse(Fg1172Sans), false, false, false);
        Assert.Equal((plain.Ascent, plain.Descent), (face.Ascent, face.Descent));
        const string text = "AVA fi office";
        Assert.Equal(Core.Fonts.Layout.TextShaper.For(Ot27Sans.Value).Shape(text).Select(g => (g.Glyph, g.XAdvance, g.XOffset)),
            Core.Fonts.Layout.TextShaper.For(OpenTypeFont.Parse(font)).Shape(text).Select(g => (g.Glyph, g.XAdvance, g.XOffset)));
        var a = Ot27Glyph('A');
        var written = new Dictionary<string, byte[]>
        {
            ["Verkleinern"] = TrueTypeSubsetter.Subset(font, [a], keepAllTables: true),
            ["Teilschrift"] = TrueTypeSubsetter.Subset(font, [a]),
            ["Bereinigen"] = TrueTypeSubsetter.Scrub(font, [a]),
            ["Neubau"] = TrueTypeSubsetter.Rebuild(font, new Dictionary<int, int> { [a] = 1366 }, [], null),
        };
        foreach (var (writer, file) in written)
        {
            var result = OpenTypeFont.Parse(file);
            Assert.False(result.HasTable(tag), $"{writer}: {tag}");
            Assert.True(Ot27SameOutline(Ot27Sans.Value.Outline(a), result.Outline(a)), writer);
        }
        Assert.True(OpenTypeFont.Parse(written["Verkleinern"]).HasTable("GSUB"));
    }

    /// <summary>
    /// OT 47 sentences 1–6 and 18–27 (hdmx) and the kerning table: hdmx is not read — advances and shaping as without it; compression keeps the
    /// glyph count and every advance, so hdmx and kern stay right and stay byte for byte (Liberation Sans with an hdmx for 9 and 12 ppem:
    /// numGlyphs and the hmtx advances as in the source); the subset, the scrubbed font and the PDF/A rebuild carry no hdmx.
    /// </summary>
    [Fact]
    public void Fg1172_Compression_KeepsHdmxAndKern()
    {
        var hdmx = Fg1172Hdmx();
        var font = Ot27SansWith(("hdmx", hdmx));
        // not read: advances and shaping as without hdmx (no rounding to device pixels)
        var withHdmx = OpenTypeFont.Parse(font);
        for (var g = 0; g < Fg1172Glyphs; g++) Assert.Equal(Ot27Sans.Value.AdvanceWidth(g), withHdmx.AdvanceWidth(g));
        Assert.Equal(Core.Fonts.Layout.TextShaper.For(Ot27Sans.Value).Shape("AB").Select(s => (s.Glyph, s.XAdvance)),
            Core.Fonts.Layout.TextShaper.For(withHdmx).Shape("AB").Select(s => (s.Glyph, s.XAdvance)));
        var a = Ot27Glyph('A');
        var compressed = TrueTypeSubsetter.Subset(font, [a], keepAllTables: true);
        Assert.Equal(hdmx, Ot27Table(compressed, "hdmx"));
        Assert.Equal(Ot27Table(font, "kern"), Ot27Table(compressed, "kern"));
        Assert.NotEmpty(Ot27Table(compressed, "kern"));
        Assert.Equal(Ot27Table(font, "maxp")[4..6], Ot27Table(compressed, "maxp")[4..6]);
        var parsed = OpenTypeFont.Parse(compressed);
        for (var g = 0; g < Fg1172Glyphs; g++) Assert.Equal(Ot27Sans.Value.AdvanceWidth(g), parsed.AdvanceWidth(g));
        foreach (var file in (byte[][])[TrueTypeSubsetter.Subset(font, [a]), TrueTypeSubsetter.Scrub(font, [a]),
                     TrueTypeSubsetter.Rebuild(font, new Dictionary<int, int> { [a] = 1366 }, [], null)])
            Assert.False(OpenTypeFont.Parse(file).HasTable("hdmx"));
    }

    // ------------------------------------------------------------ question 34: MERG when scrubbing

    /// <summary>
    /// OT 51 sentences 1–27, OT 51.1, OT 51.2, OT 51.3 (MERG): PDF-Knead does not merge glyphs before smoothing — a page with „AVA“ in Liberation
    /// Sans with a MERG (classes of „A“ and „V“, merge entry (1, 1) = 0x01) gives one drawn path per glyph at the same origins as without it;
    /// compression keeps the MERG byte for byte (glyph numbers unchanged, like GSUB), the subset has none, and scrubbing to „A“ leaves the MERG
    /// out (it names the class of „V“, whose outline is removed; before it stayed byte for byte), as it leaves out PCLT.
    /// </summary>
    [Fact]
    public void Fg1172_Merg_NotUsedForDrawing_LeftOutWhenScrubbing()
    {
        var font = Ot27SansWith(("MERG", Fg1172Merg()));
        List<(double X, double Y)> Origins(byte[] program)
        {
            var (doc, list, _) = Iso03PageFont("BT /F1 40 Tf 10 20 Td (AVA) Tj ET", pdf =>
            {
                var stream = pdf.Add(TestPdf.FlateStream(program, TestPdf.Dict(("Length1", PdfInteger.Of(program.Length)))));
                var descriptor = TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ProbeMerg")), ("Flags", PdfInteger.Of(32)),
                    ("FontBBox", TestPdf.Box(0, -212, 1000, 905)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(905)),
                    ("Descent", PdfInteger.Of(-212)), ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(stream)));
                return TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("ProbeMerg")),
                    ("Encoding", TestPdf.N("WinAnsiEncoding")), ("FontDescriptor", descriptor));
            });
            using (doc) return OriginsIso01(list);
        }
        var with = Origins(font);
        Assert.Equal(3, with.Count);
        Assert.Equal(Origins(Fg1172Sans), with);
        var a = Ot27Glyph('A');
        // compression keeps every glyph number and the layout tables with their classes, and MERG byte for byte with them
        Assert.Equal(Fg1172Merg(), Ot27Table(TrueTypeSubsetter.Subset(font, [a], keepAllTables: true), "MERG"));
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Subset(font, [a])).HasTable("MERG"));
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(font, [a])).HasTable("MERG"));
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(font, [a, Ot27Glyph('V')])).HasTable("MERG"));
    }

    // ------------------------------------------------------------ question 35: IDEF for GETVARIATION in instance fonts

    /// <summary>
    /// OT 79.17.2 sentences 13–20 and OT 41.9 sentences 16–19 (writer): a font written from a variable TrueType font is an instance font of the
    /// default instance, so GETVARIATION must give the normalized coordinates of that instance (all 0, in the order of fvar) — Liberation Sans
    /// with fvar (two axes) and a prep whose IDEF for 0x91 gives −16384, 0: the subset, the compressed copy, the scrubbed font and the PDF/A
    /// rebuild keep that prep and end it with a new IDEF for 0x91 that pushes 0, 0 (the one run last counts), maxp counts one IDEF more and
    /// room for its values on the stack (before, prep was the source's byte for byte); a font without fvar keeps its prep unchanged.
    /// </summary>
    [Fact]
    public void Fg1172_InstanceFonts_EndPrepWithIdefForGetVariation()
    {
        var plainPrep = Ot27Table(Fg1172Sans, "prep");
        byte[] sourcePrep = [.. plainPrep, 0xB0, 0x91, 0x89, 0xB9, 0xC0, 0x00, 0x00, 0x00, 0x2D];
        Assert.Equal(new[] { -16384, 0 }, Fg1172LastGetVariation(sourcePrep));
        var maxp = Ot27Table(Fg1172Sans, "maxp");
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(22), 1);   // maxInstructionDefs: the IDEF of the source
        var fvar = Ot18Fvar([("wght", 100, 400, 900, 0, 256), ("wdth", 75, 100, 100, 0, 257)], []);
        var font = Ot27SansWith(("fvar", fvar), ("prep", sourcePrep), ("maxp", maxp));
        var a = Ot27Glyph('A');
        var written = new Dictionary<string, byte[]>
        {
            ["Teilschrift"] = TrueTypeSubsetter.Subset(font, [a]),
            ["Verkleinern"] = TrueTypeSubsetter.Subset(font, [a], keepAllTables: true),
            ["Bereinigen"] = TrueTypeSubsetter.Scrub(font, [a]),
            ["Neubau"] = TrueTypeSubsetter.Rebuild(font, new Dictionary<int, int> { [a] = 1366 }, [], null),
        };
        var stackBefore = BinaryPrimitives.ReadUInt16BigEndian(maxp.AsSpan(24));
        foreach (var (writer, file) in written)
        {
            var prep = Ot27Table(file, "prep");
            Assert.True(prep.AsSpan().StartsWith(sourcePrep), writer);
            Assert.Equal(new[] { 0, 0 }, Fg1172LastGetVariation(prep));
            var newMaxp = Ot27Table(file, "maxp");
            Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(newMaxp.AsSpan(22)));
            Assert.True(BinaryPrimitives.ReadUInt16BigEndian(newMaxp.AsSpan(24)) >= Math.Max((int)stackBefore, 3), writer);
            Assert.False(OpenTypeFont.Parse(file).HasTable("fvar"), writer);
        }
        Assert.Equal(plainPrep, Ot27Table(TrueTypeSubsetter.Subset(Fg1172Sans, [a]), "prep"));
        Assert.Equal(plainPrep, Ot27Table(TrueTypeSubsetter.Subset(Fg1172Sans, [a], keepAllTables: true), "prep"));
    }

    // ------------------------------------------------------------ question 36: names STAT and fvar refer to

    /// <summary>
    /// OT 57.4 sentences 10–14, OT 57.5 sentences 36–40 (STAT) and the name IDs of fvar (axisNameID, subfamilyNameID, postScriptNameID), writer:
    /// cleaning the copyright and licence texts of a font program keeps the names STAT or fvar refer to while those tables stay in the font —
    /// Liberation Sans with STAT (axis name 256 „Weight“, axis value name 257 „Bold“) and fvar (axis name 256, an instance with subfamily 258
    /// „Semibold“ and PostScript name 259): 256 to 259 stay, an unreferenced 300 and the copyright (ID 0) become spaces (before, all IDs from 256
    /// on became spaces); without STAT and fvar the same name table loses 256 to 259 as well.
    /// </summary>
    [Fact]
    public void Fg1172_NoticeCleaning_KeepsNamesOfStatAndFvar()
    {
        var name = Ot05Name((3, 1, 0x409, 0, "Copyright Beispiel"), (3, 1, 0x409, 1, "Liberation Sans"), (3, 1, 0x409, 2, "Regular"),
            (3, 1, 0x409, 256, "Weight"), (3, 1, 0x409, 257, "Bold"), (3, 1, 0x409, 258, "Semibold"), (3, 1, 0x409, 259, "LiberationSans-Semibold"),
            (3, 1, 0x409, 300, "Freier Text"));
        var stat = new Ot18Writer().U16(1, 1, 8, 1).U32(20).U16(1).U32(28).U16(2).Tag("wght").U16(256, 0).U16(2).U16(1, 0, 0, 257).Fixed(700).ToArray();
        var fvar = Ot18Fvar([("wght", 100, 400, 900, 0, 256)], [(258, [600], 259)], postScriptNames: true);
        var with = Ot27Table(Assert.IsType<byte[]>(ObjectMetadata.FontNotices(Ot27SansWith(("name", name), ("STAT", stat), ("fvar", fvar)))), "name");
        foreach (var (id, text) in (ReadOnlySpan<(int, string)>)[(256, "Weight"), (257, "Bold"), (258, "Semibold"), (259, "LiberationSans-Semibold")])
            Assert.Equal(text, Fg1172Name(with, id));
        Assert.Equal(new string(' ', "Freier Text".Length), Fg1172Name(with, 300));
        Assert.Equal(new string(' ', "Copyright Beispiel".Length), Fg1172Name(with, 0));
        Assert.Equal("Liberation Sans", Fg1172Name(with, 1));
        var without = Ot27Table(Assert.IsType<byte[]>(ObjectMetadata.FontNotices(Ot27SansWith(("name", name)))), "name");
        foreach (var id in (int[])[256, 257, 258, 259]) Assert.Equal(new string(' ', Fg1172Name(name, id)!.Length), Fg1172Name(without, id));
    }

    /// <summary>
    /// Security of the same reading (a damaged STAT and fvar): counts and offsets beyond the tables are bounded by their length — a STAT that
    /// counts 65 535 axes and axis values at offsets beyond its end, and an fvar that counts 65 535 axes and instances, keep no name and throw no
    /// exception (the unreferenced texts become spaces as without them).
    /// </summary>
    [Fact]
    public void Fg1172_NoticeCleaning_DamagedStatAndFvar_Bounded()
    {
        var name = Ot05Name((3, 1, 0x409, 0, "Copyright Beispiel"), (3, 1, 0x409, 256, "Weight"));
        var stat = new Ot18Writer().U16(1, 1, 8, 0xFFFF).U32(20).U16(0xFFFF).U32(0xFFF0).U16(2).ToArray();
        var fvar = new Ot18Writer().U16(1, 0, 16, 2, 0xFFFF, 20, 0xFFFF, 0xFFFF).ToArray();
        var cleaned = Ot27Table(Assert.IsType<byte[]>(ObjectMetadata.FontNotices(Ot27SansWith(("name", name), ("STAT", stat), ("fvar", fvar)))), "name");
        Assert.Equal(new string(' ', 6), Fg1172Name(cleaned, 256));
    }

    // ------------------------------------------------------------ question 50: NULL offset in the ItemVariationStore

    /// <summary>
    /// OT 42.3.4 sentences 9–11 (reader): an offset 0 to an ItemVariationData means that there is none — a CFF2 table whose VariationStore names
    /// its only ItemVariationData at offset 0: a glyph that blends with vsindex 0 is empty as without VariationStore (the regions are unknown;
    /// before, the start of the store was read as ItemVariationData, so k came from the offset of the region list — here 12, and a blend with
    /// twelve deltas drew a line), a glyph without blend draws; scrubbing writes the static table.
    /// </summary>
    [Fact]
    public void Fg1172_Cff2NullItemVariationDataOffset_MeansNoData()
    {
        const string line = "0 0 rmoveto 10 0 rlineto 0 10 rlineto";
        var cff2 = Cff2Ot08(new([[], CsOt08("0 0 rmoveto 10 5 5 5 5 5 5 5 5 5 5 5 5 1 blend 0 rlineto 0 10 rlineto"), CsOt08(line)],
            Variations: OneRegionOt08));
        // the VariationStore follows header (5), Top DICT (19) and the empty GlobalSubrINDEX (4): length, format 1, region list offset, count 1,
        // the offset of ItemVariationData 0
        const int store = 5 + 19 + 4;
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(cff2.AsSpan(store + 2)));
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32BigEndian(cff2.AsSpan(store + 4)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(cff2.AsSpan(store + 8)));
        BinaryPrimitives.WriteUInt32BigEndian(cff2.AsSpan(store + 10), 0);
        var font = Cff2FontOt08(cff2, 3, extra: new Dictionary<string, byte[]> { ["fvar"] = FvarOt08() });
        AssertCff2ReadOt08(new(font, [null, line]));
    }
}
