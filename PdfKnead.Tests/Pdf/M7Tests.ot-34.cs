using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-34 (OpenType: vhea, vmtx, VORG). PDF-Knead reads none of the three: vertical metrics come from /W2 and /DW2 of the CIDFont;
/// scrubbing clears vmtx entries of glyphs not kept and hands vhea and VORG on unchanged. Fonts from Noto Sans CJK and Liberation Sans.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (package ot-34)

    private static readonly byte[] Ot34Liberation = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");

    private static string Ot34Repo([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    /// <summary>An OpenType font with CFF outlines from the first font of the Noto Sans CJK collection, with vhea, vmtx, VORG and BASE.</summary>
    private static readonly Lazy<byte[]> Ot34Noto = new(() =>
    {
        var noto = OpenTypeFont.Parse(File.ReadAllBytes(Path.Combine(Ot34Repo(), "Schriften", "CJK", "NotoSansCJK-Regular.ttc")));
        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var tag in (string[])["CFF ", "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "vhea", "vmtx", "VORG", "BASE"])
            if (noto.Table(tag) is { } t) tables[tag] = noto.FileData.AsSpan(t.Offset, t.Length).ToArray();
        return TrueTypeSubsetter.Finish(tables);
    });

    private static readonly string Ot34Kept = "漢字A";

    /// <summary>The Noto font scrubbed with „漢字A“ kept, and the glyphs kept (with glyph 0).</summary>
    private static readonly Lazy<(OpenTypeFont Source, OpenTypeFont Result, HashSet<int> Kept)> Ot34Scrubbed = new(() =>
    {
        var source = OpenTypeFont.Parse(Ot34Noto.Value);
        var kept = Ot34Kept.Select(c => source.GlyphForUnicode(c)).ToHashSet();
        var result = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(Ot34Noto.Value, kept));
        kept.Add(0);
        return (source, result, kept);
    });

    private static int Ot34U16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt16BigEndian(data[at..]);

    /// <summary>vmtx as scrubbing must leave it: entries of glyphs not kept 0, the advance of the last full entry kept (it applies to the rest).</summary>
    private static byte[] Ot34ExpectedVmtx(byte[] vmtx, int full, int glyphCount, HashSet<int> kept)
    {
        var expected = vmtx.ToArray();
        for (var g = 0; g < glyphCount; g++)
        {
            if (kept.Contains(g)) continue;
            if (g < full)
            {
                if (g < full - 1) expected[g * 4] = expected[g * 4 + 1] = 0;
                expected[g * 4 + 2] = expected[g * 4 + 3] = 0;
            }
            else
            {
                var at = full * 4 + (g - full) * 2;
                expected[at] = expected[at + 1] = 0;
            }
        }
        return expected;
    }

    /// <summary>A VORG table (version 1.0) with the default and the entries given.</summary>
    private static byte[] Ot34Vorg(int defaultY, params (int Glyph, int Y)[] entries)
    {
        var t = new byte[8 + 4 * entries.Length];
        BinaryPrimitives.WriteUInt16BigEndian(t, 1);
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(4), (short)defaultY);
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(6), (ushort)entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(8 + 4 * i), (ushort)entries[i].Glyph);
            BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(10 + 4 * i), (short)entries[i].Y);
        }
        return t;
    }

    private static void Ot34SameGlyphs(OpenTypeFont expected, OpenTypeFont actual, string text)
    {
        Assert.Equal(expected.GlyphCount, actual.GlyphCount);
        foreach (var c in text)
        {
            var g = expected.GlyphForUnicode(c);
            Assert.True(g > 0);
            Assert.Equal(g, actual.GlyphForUnicode(c));
            Assert.Equal(expected.AdvanceWidth(g), actual.AdvanceWidth(g));
            Assert.Equal(expected.Outline(g).Verbs, actual.Outline(g).Verbs);
            Assert.Equal(expected.Outline(g).Points, actual.Outline(g).Points);
        }
    }

    /// <summary>A Type 0 font over an embedded TrueType program (CIDFontType2, CID = glyph) with the encoding given and a ToUnicode for „I“.</summary>
    private static PdfDictionary Ot34Type0(TestPdf pdf, byte[] program, string encoding)
    {
        var i = OpenTypeFont.Parse(program).GlyphForUnicode('I');
        var toUnicode = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def /CMapName /Adobe-Identity-UCS def /CMapType 2 def\n" +
            $"1 begincodespacerange <0000> <FFFF> endcodespacerange\n1 beginbfchar <{i:X4}> <0049> endbfchar\n" +
            "endcmap CMapName currentdict /CMap defineresource pop end end\n";
        var file = pdf.Add(TestPdf.FlateStream(program));
        var descriptor = TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ABCDEF+Erfunden")), ("Flags", PdfInteger.Of(32)),
            ("FontBBox", TestPdf.Box(-200, -300, 1200, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)),
            ("Descent", PdfInteger.Of(-200)), ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(file)));
        var cid = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType2")), ("BaseFont", TestPdf.N("ABCDEF+Erfunden")),
            ("CIDSystemInfo", TestPdf.Dict(("Registry", new PdfString("Adobe"u8.ToArray())), ("Ordering", new PdfString("Identity"u8.ToArray())),
                ("Supplement", PdfInteger.Of(0)))),
            ("CIDToGIDMap", TestPdf.N("Identity")), ("FontDescriptor", descriptor));
        return TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("ABCDEF+Erfunden")),
            ("Encoding", TestPdf.N(encoding)), ("DescendantFonts", new PdfArray([cid])),
            ("ToUnicode", TestPdf.Ref(pdf.Add(TestPdf.FlateStream(Encoding.ASCII.GetBytes(toUnicode))))));
    }

    private static PdfDictionary Ot34Font(PdfDocument doc, string name) =>
        doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get(name))!;

    // ------------------------------------------------------------ OT 36 (vhea)

    /// <summary>
    /// OT 36.1 sentences 6–7, OT 36.2 sentences 13–18, 35–46, 48–53 and the scrubbing part of 19–33 and 54–68 (today's behaviour, question 7
    /// of the M7 questions: residues in tables): scrubbing the Noto font (vhea 1.1) with „漢字A“ kept hands vhea on byte for byte —
    /// advanceHeightMax, the minimum side bearings, yMaxExtent, the vertTypo values, caret, reserved fields and metricDataFormat are not
    /// recomputed from the scrubbed vmtx.
    /// </summary>
    [Fact]
    public void Ot34_ScrubOfNotoCjk_VheaStaysAsInTheSource()
    {
        var (source, result, _) = Ot34Scrubbed.Value;
        var vhea = source.TableData("vhea").ToArray();
        Assert.Equal(0x00011000u, BinaryPrimitives.ReadUInt32BigEndian(vhea));
        Assert.Equal(vhea, result.TableData("vhea").ToArray());
    }

    /// <summary>
    /// OT 36.2 sentence 69, OT 37.2 sentences 1–8, 9–12, 13–18, 19–21: vmtx holds numOfLongVerMetrics full entries (advance, top side bearing)
    /// and then top side bearings only; scrubbing the Noto font (65 158 full entries for 65 535 glyphs) with „漢字A“ kept leaves the entries of the
    /// kept glyphs as in the source, sets advance and side bearing of the others to 0 — of the last full entry only the side bearing, its advance
    /// applies to all glyphs after it — and of the glyphs in the second array exactly their two bytes; the table keeps its length.
    /// </summary>
    [Fact]
    public void Ot34_ScrubOfNotoCjk_VmtxOfRemovedGlyphsCleared_KeptAsInTheSource()
    {
        var (source, result, kept) = Ot34Scrubbed.Value;
        var vmtx = source.TableData("vmtx").ToArray();
        var full = Ot34U16(source.TableData("vhea"), 34);
        Assert.True(full < source.GlyphCount);
        Assert.Equal(full * 4 + (source.GlyphCount - full) * 2, vmtx.Length);
        var scrubbed = result.TableData("vmtx").ToArray();
        Assert.Equal(vmtx.Length, scrubbed.Length);
        Assert.Equal(Ot34ExpectedVmtx(vmtx, full, source.GlyphCount, kept), scrubbed);
        foreach (var c in Ot34Kept)
        {
            var g = source.GlyphForUnicode(c);
            Assert.Equal(vmtx.AsSpan(g * 4, 4).ToArray(), scrubbed.AsSpan(g * 4, 4).ToArray());
        }
        foreach (var c in "秘密Z")
        {
            var g = source.GlyphForUnicode(c);
            Assert.True(g < full - 1);
            Assert.Equal(new byte[4], scrubbed.AsSpan(g * 4, 4).ToArray());
        }
        // the last full entry: advance stays, side bearing 0
        Assert.Equal(vmtx.AsSpan((full - 1) * 4, 2).ToArray(), scrubbed.AsSpan((full - 1) * 4, 2).ToArray());
        Assert.Equal(new byte[2], scrubbed.AsSpan((full - 1) * 4 + 2, 2).ToArray());
    }

    /// <summary>
    /// OT 36.2 sentences 1–7 and 8–12: vhea 1.0 and 1.1 differ only in the meaning of three fields — scrubbing the Noto font with vhea 1.1 and
    /// the same font with version 1.0 (ascent 880, descent −120, lineGap 0) clears the same vmtx entries, and vhea stays byte for byte as in its
    /// source in both cases. (The vertical metrics PDF-Knead draws with come from /W2 and /DW2:
    /// <c>FontWidthTests.CidFont_UsesListsRangesAndDefaults</c>.)
    /// </summary>
    [Fact]
    public void Ot34_ScrubOfVhea10And11_SameVmtx_VheaUnchanged()
    {
        var (source, result, kept) = Ot34Scrubbed.Value;
        var vhea10 = source.TableData("vhea").ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(vhea10, 0x00010000);
        BinaryPrimitives.WriteInt16BigEndian(vhea10.AsSpan(4), 880);
        BinaryPrimitives.WriteInt16BigEndian(vhea10.AsSpan(6), -120);
        BinaryPrimitives.WriteInt16BigEndian(vhea10.AsSpan(8), 0);
        var font10 = ColorFontBuilder.WithTables(Ot34Noto.Value, new Dictionary<string, byte[]?> { ["vhea"] = vhea10 });
        var result10 = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(font10, kept));
        Assert.Equal(vhea10, result10.TableData("vhea").ToArray());
        Assert.Equal(result.TableData("vmtx").ToArray(), result10.TableData("vmtx").ToArray());
    }

    /// <summary>
    /// OT 36.2 sentence 34: numOfLongVerMetrics gives the number of full vmtx entries — a font of five glyphs with vhea 1.0, numOfLongVerMetrics 3,
    /// scrubbed with glyph 1 kept: entries 0 and 1 unchanged, entry 2 keeps its advance and loses its side bearing, the side bearings of glyphs 3
    /// and 4 (from byte 12) become 0.
    /// </summary>
    [Fact]
    public void Ot34_Scrub_NumOfLongVerMetrics3_FiveGlyphs()
    {
        var vhea = new byte[36];
        BinaryPrimitives.WriteUInt32BigEndian(vhea, 0x00010000);
        BinaryPrimitives.WriteInt16BigEndian(vhea.AsSpan(4), 1024);
        BinaryPrimitives.WriteInt16BigEndian(vhea.AsSpan(6), -1024);
        BinaryPrimitives.WriteUInt16BigEndian(vhea.AsSpan(34), 3);
        var vmtx = new byte[3 * 4 + 2 * 2];
        short[] values = [2048, 100, 2048, 110, 1900, 120, 130, 140];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt16BigEndian(vmtx.AsSpan(i * 2), values[i]);
        var maxp = OpenTypeFont.Parse(Ot34Liberation).TableData("maxp").ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), 5);
        var font = ColorFontBuilder.WithTables(Ot34Liberation, new Dictionary<string, byte[]?> { ["vhea"] = vhea, ["vmtx"] = vmtx, ["maxp"] = maxp });
        Assert.Equal(5, OpenTypeFont.Parse(font).GlyphCount);
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(font, [1])).TableData("vmtx").ToArray();
        short[] expected = [2048, 100, 2048, 110, 1900, 0, 0, 0];
        Assert.Equal(expected, Enumerable.Range(0, 8).Select(i => BinaryPrimitives.ReadInt16BigEndian(scrubbed.AsSpan(i * 2))));
    }

    /// <summary>
    /// OT 36.2 sentences 19–33, 47 and 54–68 (the text part): caret and line spacing of vertical text are not needed — PDF-Knead does not edit
    /// or set vertical text: a page with a Type 0 font in Identity-V gives a block locked as vertical, and the font is no original font for new
    /// text (the same font in Identity-H is one).
    /// </summary>
    [Fact]
    public void Ot34_VerticalText_LockedAndNoOriginalFace()
    {
        var gid = OpenTypeFont.Parse(Ot34Liberation).GlyphForUnicode('I');
        var (doc, _) = InterpreterTests.Page($"BT /V 20 Tf 100 150 Td <{gid:X4}{gid:X4}> Tj ET BT /H 20 Tf 20 20 Td <{gid:X4}> Tj ET",
            pdf => TestPdf.Dict(("Font", TestPdf.Dict(("V", Ot34Type0(pdf, Ot34Liberation, "Identity-V")), ("H", Ot34Type0(pdf, Ot34Liberation, "Identity-H"))))));
        using (doc)
        {
            var blocks = TextBlocks.Find(doc, doc.Pages[0]);
            Assert.Contains(blocks, b => b.Locked == "Senkrechte Schrift — nicht bearbeitbar.");
            Assert.Null(OriginalFace.For(new OriginalFont(doc, Ot34Font(doc, "V"), false, false)));
            Assert.NotNull(OriginalFace.For(new OriginalFont(doc, Ot34Font(doc, "H"), false, false)));
        }
    }

    /// <summary>
    /// OT 36.3 sentences 1–11: variable fonts may adjust vhea values per instance through MVAR (tags vasc …) — PDF-Knead reads neither: the Noto
    /// font with an MVAR (tag vasc) reads with the same glyphs, advances and outlines; a subset (of Liberation Sans with that MVAR) leaves MVAR out.
    /// </summary>
    [Fact]
    public void Ot34_Mvar_ChangesNothing_SubsetLeavesItOut()
    {
        var mvar = new byte[12 + 8];
        BinaryPrimitives.WriteUInt16BigEndian(mvar, 1);
        BinaryPrimitives.WriteUInt16BigEndian(mvar.AsSpan(6), 8);       // valueRecordSize
        BinaryPrimitives.WriteUInt16BigEndian(mvar.AsSpan(8), 1);       // valueRecordCount
        "vasc"u8.CopyTo(mvar.AsSpan(12));
        var with = OpenTypeFont.Parse(ColorFontBuilder.WithTables(Ot34Noto.Value, new Dictionary<string, byte[]?> { ["MVAR"] = mvar }));
        Assert.True(with.HasTable("MVAR"));
        Ot34SameGlyphs(OpenTypeFont.Parse(Ot34Noto.Value), with, "漢字Aｱ");
        var liberation = ColorFontBuilder.WithTables(Ot34Liberation, new Dictionary<string, byte[]?> { ["MVAR"] = mvar });
        var subset = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(liberation, [OpenTypeFont.Parse(liberation).GlyphForUnicode('A')]));
        Assert.False(subset.HasTable("MVAR"));
    }

    // ------------------------------------------------------------ OT 37 (vmtx)

    /// <summary>
    /// OT 37.1 sentence 16: vertical setting may align glyphs in x with the BASE baselines — PDF-Knead sets no vertical text; scrubbing leaves
    /// BASE out (the Noto font has one).
    /// </summary>
    [Fact]
    public void Ot34_ScrubOfNotoCjk_LeavesBaseOut()
    {
        var (source, result, _) = Ot34Scrubbed.Value;
        Assert.True(source.HasTable("BASE"));
        Assert.False(result.HasTable("BASE"));
    }

    // ------------------------------------------------------------ OT 38 (VORG)

    /// <summary>
    /// OT 38 sentences 1–2, OT 38.1 sentences 1–8 and 9–13 (today's behaviour, question 7 of the M7 questions): VORG is not read; scrubbing the
    /// Noto font (VORG 1.0, default 880) hands it on byte for byte — also the entries of glyphs not kept (a full-width Latin letter).
    /// </summary>
    [Fact]
    public void Ot34_ScrubOfNotoCjk_VorgStaysByteForByte_AlsoEntriesOfRemovedGlyphs()
    {
        var (source, result, kept) = Ot34Scrubbed.Value;
        var vorg = source.TableData("VORG").ToArray();
        Assert.Equal((1, 0), (Ot34U16(vorg, 0), Ot34U16(vorg, 2)));
        Assert.Equal(880, BinaryPrimitives.ReadInt16BigEndian(vorg.AsSpan(4)));
        var count = Ot34U16(vorg, 6);
        Assert.True(count > 0);
        var removed = Enumerable.Range(0, count).Select(i => Ot34U16(vorg, 8 + 4 * i)).First(g => !kept.Contains(g));
        Assert.Equal(vorg, result.TableData("VORG").ToArray());
        Assert.Contains(removed, Enumerable.Range(0, count).Select(i => Ot34U16(result.TableData("VORG"), 8 + 4 * i)));
    }

    /// <summary>OT 38 sentences 1–2: a subset carries no VORG (Liberation Sans with a VORG added: the subset lists no such table).</summary>
    [Fact]
    public void Ot34_SubsetOfAFontWithVorg_HasNoVorg()
    {
        var font = ColorFontBuilder.WithTables(Ot34Liberation, new Dictionary<string, byte[]?> { ["VORG"] = Ot34Vorg(880, (10, 900)) });
        Assert.True(OpenTypeFont.Parse(font).HasTable("VORG"));
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Subset(font, [10, 20])).HasTable("VORG"));
    }

    /// <summary>
    /// OT 38 sentence 3: in a font with TrueType outlines VORG must be ignored — Liberation Sans with a VORG (default 100, one entry for „I“) as
    /// CIDFontType2 in Identity-V: outline, width, vertical advance and origin of „I“ and „A“ as without VORG.
    /// </summary>
    [Fact]
    public void Ot34_VorgInATrueTypeFont_IsIgnored()
    {
        var parsed = OpenTypeFont.Parse(Ot34Liberation);
        var (i, a) = (parsed.GlyphForUnicode('I'), parsed.GlyphForUnicode('A'));
        var withVorg = ColorFontBuilder.WithTables(Ot34Liberation, new Dictionary<string, byte[]?> { ["VORG"] = Ot34Vorg(100, (i, 50)) });
        var (doc, _) = InterpreterTests.Page("",
            pdf => TestPdf.Dict(("Font", TestPdf.Dict(("P", Ot34Type0(pdf, Ot34Liberation, "Identity-V")), ("Q", Ot34Type0(pdf, withVorg, "Identity-V"))))));
        using (doc)
        {
            var plain = PdfFont.Get(Ot34Font(doc, "P"), doc);
            var vorg = PdfFont.Get(Ot34Font(doc, "Q"), doc);
            Assert.True(vorg.Vertical);
            foreach (var g in (int[])[i, a])
            {
                var p = plain.Glyph((uint)g, 2);
                var q = vorg.Glyph((uint)g, 2);
                Assert.Equal(p.Outline.Verbs, q.Outline.Verbs);
                Assert.Equal(p.Outline.Points, q.Outline.Points);
                Assert.Equal(p.Width, q.Width);
                Assert.Equal(p.VerticalAdvance, q.VerticalAdvance);
                Assert.Equal(p.OriginX, q.OriginX);
                Assert.Equal(p.OriginY, q.OriginY);
            }
        }
    }

    /// <summary>
    /// OT 38.1 sentence 14: the entries are sorted by glyph ID without duplicates — a rule for the font builder; scrubbing a font whose VORG has
    /// entries for glyphs 10, 12 and 13 gives the same entries in the same order.
    /// </summary>
    [Fact]
    public void Ot34_Scrub_VorgEntriesKeepTheirOrder()
    {
        var vorg = Ot34Vorg(880, (10, 900), (12, 870), (13, 860));
        var font = ColorFontBuilder.WithTables(Ot34Liberation, new Dictionary<string, byte[]?> { ["VORG"] = vorg });
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(font, [10, 12])).TableData("VORG").ToArray();
        Assert.Equal(vorg, scrubbed);
        Assert.Equal(new[] { 10, 12, 13 }, Enumerable.Range(0, 3).Select(k => Ot34U16(scrubbed, 8 + 4 * k)));
    }

    /// <summary>
    /// OT 38.1 sentences 15–18: size-optimised fonts leave out entries of glyphs with the default (8 bytes when all use it) — the Noto font with
    /// its VORG cut to the 8-byte form (default 880 only) reads with the same glyphs, advances and outlines.
    /// </summary>
    [Fact]
    public void Ot34_VorgOfEightBytes_ReadsAsTheFull()
    {
        var short8 = OpenTypeFont.Parse(ColorFontBuilder.WithTables(Ot34Noto.Value, new Dictionary<string, byte[]?> { ["VORG"] = Ot34Vorg(880) }));
        Assert.Equal(8, short8.TableData("VORG").Length);
        Ot34SameGlyphs(OpenTypeFont.Parse(Ot34Noto.Value), short8, "漢字Aｱ");
    }
}
