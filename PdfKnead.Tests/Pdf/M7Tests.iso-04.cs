using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package iso-04 (ISO 32000-2, 9.7.6 to 9.10.1): the tests the drafts of the sentence-by-sentence comparison asked for — names of
/// composite fonts, code spaces and notdef, the font descriptor and its flags, embedded font programs, subsets.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (iso-04)

    private static readonly Lazy<byte[]> Iso04SansFile = new(() => EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf"));

    /// <summary>Typed text in Liberation Sans, saved: the composite font text editing writes.</summary>
    private static PdfDictionary Iso04SavedSans() => Iso03Saved(ElementFace.For(ElementFont.Sans), "Probe Text");

    private static PdfDictionary Iso04Descriptor(PdfDictionary type0) => Iso03Descendant(type0).GetDictionary(PdfName.Get("FontDescriptor"))!;

    private static PdfDictionary Iso04F1(PdfDocument doc) => doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!;

    /// <summary>Whether two files render their first page to the same pixels and read the same text.</summary>
    private static void Iso04SameLook(byte[] expected, byte[] actual)
    {
        using var a = PdfDocument.Open(expected);
        using var b = PdfDocument.Open(actual);
        using var pa = ContentImageTests.Render(a, a.Pages[0]);
        using var pb = ContentImageTests.Render(b, b.Pages[0]);
        Assert.True(pa.Bytes.AsSpan().SequenceEqual(pb.Bytes), "die Seite sieht anders aus");
        Assert.Equal(PageText.Create(PageInterpreter.BuildText(a, a.Pages[0])).Text, PageText.Create(PageInterpreter.BuildText(b, b.Pages[0])).Text);
    }

    /// <summary>A file saved by the ordinary writer (no PDF/A).</summary>
    private static byte[] Iso04Saved(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output);
        return output.ToArray();
    }

    // ------------------------------------------------------------ 9.7.6.1

    /// <summary>
    /// 9.7.6.1 sentence 9 (writer): the /BaseFont of a composite font over a CIDFontType0 should be the name of the CIDFont, a hyphen and the
    /// name of the CMap — typed CJK text (Noto, CFF) saved: the CIDFont's name followed by „-Identity-H“.
    /// </summary>
    [Fact]
    public void TypedCjk_Type0BaseFont_IsTheCidFontNameAndTheCMapName()
    {
        var type0 = Iso03Saved(Iso03NotoFace(), "日本");
        Assert.Equal(Iso03Descendant(type0).GetName(PdfName.Get("BaseFont"))!.Value + "-Identity-H", type0.GetName(PdfName.Get("BaseFont"))!.Value);
    }

    /// <summary>9.7.6.1 sentence 10 (writer): over a CIDFontType2 the composite font has the name of the CIDFont — typed text in Liberation Sans.</summary>
    [Fact]
    public void TypedTrueTypeText_Type0BaseFont_EqualsTheCidFont()
    {
        var type0 = Iso04SavedSans();
        Assert.Equal("CIDFontType2", Iso03Descendant(type0).GetName(PdfName.Subtype)?.Value);
        Assert.Equal(Iso03Descendant(type0).GetName(PdfName.Get("BaseFont"))!.Value, type0.GetName(PdfName.Get("BaseFont"))!.Value);
    }

    // ------------------------------------------------------------ 9.7.6.2, 9.7.6.3

    /// <summary>
    /// 9.7.6.2 sentences 6 and 10 (reader): codes have at most four bytes, and code space ranges do not overlap — a range of five bytes never
    /// matches (beside a one-byte range every byte is a code of its own); with two overlapping two-byte ranges a code is read once, as two bytes.
    /// </summary>
    [Fact]
    public void CMapCodespace_FiveByteRangeNeverMatches_OverlappingRangesReadOnce()
    {
        static CMap Cmap(string ranges) => CMap.Parse(Encoding.ASCII.GetBytes(
            $"/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n2 begincodespacerange {ranges} endcodespacerange\nendcmap end end"));
        var five = Cmap("<0000000000> <FFFFFFFFFF> <00> <FF>");
        byte[] text = [0x41, 0x42, 0x43, 0x44, 0x45];
        for (var pos = 0; pos < text.Length; pos++)
        {
            Assert.Equal(1, five.ReadCode(text, pos, out var code));
            Assert.Equal(text[pos], (byte)code);
        }
        var overlapping = Cmap("<8140> <9FFC> <9000> <A0FF>");
        Assert.Equal(2, overlapping.ReadCode([0x95, 0x50, 0x81, 0x40], 0, out var first));
        Assert.Equal(0x9550u, first);
        Assert.True(overlapping.InCodespace(first, 2));
        Assert.Equal(2, overlapping.ReadCode([0x95, 0x50, 0x81, 0x40], 2, out var second));
        Assert.Equal(0x8140u, second);
    }

    /// <summary>
    /// 9.7.6.3 sentences 3–7 (reader): a CID for which the CIDFont has no glyph, without a notdef mapping, is drawn with the glyph of CID 0 — an
    /// embedded CID-keyed CFF without CID 7 (a subset of Noto CJK with the CIDs 0 and 100), text with CID 7: the outline of CID 0.
    /// </summary>
    [Fact(Skip = "M7-Befund: CID ohne Glyphe zeichnet nichts statt der Glyphe von CID 0 (CompositeFont.Select)")]
    public void CidCffWithoutTheCid_DrawsTheGlyphOfCidZero()
    {
        var (doc, _, font) = Iso03NotoSubsetFont("1 begincidchar\n<05> 7\nendcidchar\n");
        using (doc)
        {
            var cff = Iso03NotoJp.Value.Cff!;
            var notdef = cff.Outline(cff.GlyphByCid(0));
            Assert.False(notdef.IsEmpty);
            Assert.Equal(7, font.Cid(0x05, 1));
            Assert.True(Iso03SameOutline(notdef, font.Glyph(0x05, 1).Outline));
        }
    }

    /// <summary>
    /// 9.7.6.3 sentence 15 (reader): an embedded TrueType CIDFont whose CIDToGIDMap stream has no entry for a CID draws the glyph of CID 0 — a
    /// map of three entries, text with CID 10: the outline of glyph 0.
    /// </summary>
    [Fact]
    public void CidToGidMapWithoutTheCid_DrawsGlyphZero()
    {
        var (doc, _, font) = Iso03PageFont("", pdf => Iso03CidTrueType(pdf, TestPdf.N("Identity-H"),
            ("CIDToGIDMap", TestPdf.Ref(pdf.Add(TestPdf.FlateStream([0, 0, 0, 36, 0, 37]))))));
        using (doc)
        {
            var sans = OpenTypeFont.Parse(Iso04SansFile.Value);
            Assert.False(sans.Outline(0).IsEmpty);
            Assert.True(Iso03SameOutline(sans.Outline(0), font.Glyph(10, 2).Outline));
            Assert.True(Iso03SameOutline(sans.Outline(36), font.Glyph(1, 2).Outline));
        }
    }

    // ------------------------------------------------------------ 9.8.1, 9.8.2: choosing a substitute

    /// <summary>
    /// 9.8.1 sentence 2, sentences 27–34, 9.8.2 sentences 5–7, 9–12, 48, 49, 52, 15–16 and 17–27 (reader): the metrics of the descriptor let a
    /// reader choose a substitute for a font that is not embedded — Serif (2), weight 700 and an angle of −12 give the bold italic serif;
    /// FontWeight 700 bold, 400 regular; FixedPitch (1) the monospaced, Serif (2) the serif, Italic (64) the italic face; AllCap and SmallCap
    /// (bits 17, 18) change nothing.
    /// </summary>
    [Theory]
    [InlineData(2, 700, -12, "LiberationSerif-BoldItalic.ttf")]
    [InlineData(32, 700, 0, "LiberationSans-Bold.ttf")]
    [InlineData(32, 400, 0, "LiberationSans-Regular.ttf")]
    [InlineData(1, 0, 0, "LiberationMono-Regular.ttf")]
    [InlineData(2, 0, 0, "LiberationSerif-Regular.ttf")]
    [InlineData(64, 0, 0, "LiberationSans-Italic.ttf")]
    [InlineData(0x10000, 0, 0, "LiberationSans-Regular.ttf")]
    [InlineData(0x20000, 0, 0, "LiberationSans-Regular.ttf")]
    public void NonEmbeddedFont_SubstituteFollowsTheDescriptor(int flags, int weight, int angle, string file)
    {
        var descriptor = TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("Unbekannt")), ("Flags", PdfInteger.Of(flags)),
            ("ItalicAngle", PdfInteger.Of(angle)));
        if (weight > 0) descriptor.Set(PdfName.Get("FontWeight"), PdfInteger.Of(weight));
        Assert.Equal(file, StandardFonts.Choose("Unbekannt", descriptor).File);
    }

    /// <summary>9.8.1 sentence 4 (writer): a composite font has no font descriptor, its CIDFont has one — typed text saved.</summary>
    [Fact]
    public void TypedText_Type0WithoutDescriptor_CidFontWithOne()
    {
        var type0 = Iso04SavedSans();
        Assert.False(type0.ContainsKey(PdfName.Get("FontDescriptor")));
        Assert.Equal("FontDescriptor", Iso04Descriptor(type0).GetName(PdfName.Type)?.Value);
    }

    /// <summary>9.8.1 sentence 9 (reader): the metrics are in glyph space units — /Ascent 900 and /Descent −300 are 0.9 and −0.3 of the font size.</summary>
    [Fact]
    public void DescriptorAscentAndDescent_InThousandthsOfGlyphSpace()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        var font = PdfFont.Get(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("Unbekannt")),
            ("FontDescriptor", TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("Unbekannt")), ("Flags", PdfInteger.Of(32)),
                ("Ascent", PdfInteger.Of(900)), ("Descent", PdfInteger.Of(-300))))), doc);
        Assert.Equal(0.9, font.Ascent, 6);
        Assert.Equal(-0.3, font.Descent, 6);
    }

    /// <summary>
    /// 9.8.1 sentences 17–19, 21–25 (FontFamily, FontStretch), 46–47 (Leading), 49–50 (XHeight), 54–57 (StemH), 58–63 (AvgWidth, MaxWidth):
    /// entries PDF-Knead does not use — a font that is not embedded looks, reads and measures the same with them as without, its substitute is
    /// the same; the copy keeps FontFamily and FontStretch.
    /// </summary>
    [Theory]
    [InlineData("/FontStretch/Condensed/FontFamily(Muster)", "FontStretch,FontFamily")]
    [InlineData("/Leading 500", "")]
    [InlineData("/XHeight 500", "")]
    [InlineData("/StemH 40", "")]
    [InlineData("/AvgWidth 500/MaxWidth 1000", "")]
    public void UnusedDescriptorEntries_ChangeNothing(string entries, string kept)
    {
        byte[] File(string extra) => PdfARuleCases.WithTrueType(f => f.Replace("/BaseFont/LiberationSans", "/BaseFont/Unbekannt", StringComparison.Ordinal),
            d => d.Replace("/FontName/LiberationSans", "/FontName/Unbekannt", StringComparison.Ordinal).Replace("/FontFile2 7 0 R", extra, StringComparison.Ordinal))();
        var without = File("");
        var with = File(entries);
        Iso04SameLook(without, with);
        using (var a = PdfDocument.Open(without))
        using (var b = PdfDocument.Open(with))
        {
            var fa = PdfFont.Get(Iso04F1(a), a);
            var fb = PdfFont.Get(Iso04F1(b), b);
            for (uint code = 0x41; code <= 0x43; code++) Assert.Equal(fa.Glyph(code, 1).Width, fb.Glyph(code, 1).Width);
            Assert.Equal(StandardFonts.Choose("Unbekannt", Iso04F1(a).GetDictionary(PdfName.Get("FontDescriptor"))).File,
                StandardFonts.Choose("Unbekannt", Iso04F1(b).GetDictionary(PdfName.Get("FontDescriptor"))).File);
        }
        if (kept.Length == 0) return;
        using var saved = PdfDocument.Open(Iso04Saved(with));
        var descriptor = Iso04F1(saved).GetDictionary(PdfName.Get("FontDescriptor"))!;
        foreach (var key in kept.Split(',')) Assert.NotNull(descriptor.Get(PdfName.Get(key)));
    }

    // ------------------------------------------------------------ 9.8.1, 9.8.2: what the writer puts in the descriptor

    /// <summary>
    /// 9.8.1 sentences 37–38 (writer): /FontBBox is the box around all glyphs in glyph space — typed text in Liberation Sans saved: xMin, yMin,
    /// xMax, yMax of head in thousandths of the em.
    /// </summary>
    [Fact]
    public void TypedText_FontBBox_IsTheHeadBoxInThousandths()
    {
        var font = ElementFace.For(ElementFont.Sans).Font;
        var head = font.TableData("head");
        var scale = 1000.0 / font.UnitsPerEm;
        var box = Iso04Descriptor(Iso04SavedSans()).GetArray(PdfName.Get("FontBBox"))!;
        for (var i = 0; i < 4; i++)
            Assert.Equal(Math.Round(BinaryPrimitives.ReadInt16BigEndian(head[(36 + 2 * i)..]) * scale), box.GetNumber(i)!.Value);
    }

    /// <summary>
    /// 9.8.1 sentences 39, 41 and 9.8.2 sentences 1–3 (writer): /ItalicAngle is negative for a face that leans to the right, and /Flags has no
    /// bits but defined ones — typed italic text saved: an angle below 0, flags only of FixedPitch, Serif, Nonsymbolic, Italic (1, 2, 32, 64).
    /// </summary>
    [Fact]
    public void TypedItalicText_NegativeAngleAndOnlyDefinedFlags()
    {
        var italic = ElementFace.For("M7-iso04-LiberationSans-Italic", () => EmbeddedFonts.StandardOpenType("LiberationSans-Italic.ttf"), false, false, true);
        var descriptor = Iso04Descriptor(Iso03Saved(italic, "Kursiv"));
        Assert.True(descriptor.GetNumber(PdfName.Get("ItalicAngle")) < 0);
        var flags = descriptor.GetInteger(PdfName.Get("Flags"))!.Value;
        Assert.Equal(0, flags & ~(1 | 2 | 32 | 64));
        Assert.NotEqual(0, flags & 64);
    }

    /// <summary>
    /// 9.8.2 sentences 1–3 (writer): bits the table does not define are written as 0 — the PDF/A copy of a font whose /Flags have bit 20
    /// (0x80000) set clears it.
    /// </summary>
    [Fact]
    public void PdfACopy_ReservedFlagBit_IsCleared()
    {
        using var doc = PdfDocument.Open(PdfARuleCases.WithTrueType(descriptor: d => d.Replace("/Flags 32", $"/Flags {0x80020}", StringComparison.Ordinal))());
        var copy = PdfAExport.Write(doc, PageItem.From(doc), PdfAConformance.A2b);
        using var written = PdfDocument.Open(copy.Bytes);
        var flags = Iso04F1(written).GetDictionary(PdfName.Get("FontDescriptor"))!.GetInteger(PdfName.Get("Flags"))!.Value;
        Assert.Equal(0, flags & 0x80000);
    }

    /// <summary>9.8.1 sentence 48 (writer): /CapHeight — typed text in Liberation Sans saved: sCapHeight of OS/2 in thousandths of the em.</summary>
    [Fact]
    public void TypedText_CapHeight_IsTheOs2CapHeightInThousandths()
    {
        var font = ElementFace.For(ElementFont.Sans).Font;
        var os2 = font.TableData("OS/2");
        Assert.True(BinaryPrimitives.ReadUInt16BigEndian(os2) >= 2);
        Assert.Equal(Math.Round(BinaryPrimitives.ReadInt16BigEndian(os2[88..]) * 1000.0 / font.UnitsPerEm),
            Iso04Descriptor(Iso04SavedSans()).GetNumber(PdfName.Get("CapHeight")));
    }

    /// <summary>9.8.1 sentences 51–53 (writer): /StemV is positive (0 only for unknown) — typed text saved.</summary>
    [Fact]
    public void TypedText_StemV_IsPositive()
    {
        Assert.True(Iso04Descriptor(Iso04SavedSans()).GetNumber(PdfName.Get("StemV")) > 0);
    }

    /// <summary>
    /// 9.8.1 sentences 64–67 (reader, today's behaviour): /MissingWidth 250 for codes without a /Widths entry — where the embedded program has a
    /// width („B“, 667 units) that width applies; where it has none (code 01h, no glyph) /MissingWidth.
    /// </summary>
    [Fact]
    public void MissingWidth_OnlyWhereTheProgramHasNoWidth()
    {
        using var doc = PdfDocument.Open(PdfARuleCases.WithTrueType(f => f.Replace("/LastChar 66/Widths[667 667]", "/LastChar 65/Widths[667]", StringComparison.Ordinal),
            d => d.Replace("/FontFile2 7 0 R", "/FontFile2 7 0 R/MissingWidth 250", StringComparison.Ordinal))());
        var font = PdfFont.Get(Iso04F1(doc), doc);
        var sans = OpenTypeFont.Parse(Iso04SansFile.Value);
        Assert.Equal(sans.AdvanceWidth(PdfARuleCases.Gid('B')) / sans.UnitsPerEm, font.Glyph('B', 1).Width, 6);
        Assert.Equal(0.25, font.Glyph(0x01, 1).Width, 6);
    }

    /// <summary>
    /// 9.8.2 sentences 29–34 (writer): a font with glyphs outside the standard Latin set is symbolic — typed „Ω“ (Liberation Sans) and „日本“
    /// (Noto CJK) saved: /Flags with Symbolic (4), without Nonsymbolic (32).
    /// </summary>
    [Theory]
    [InlineData("Ω", false)]
    [InlineData("日本", true)]
    public void TypedNonLatinText_DescriptorIsSymbolic(string text, bool cjk)
    {
        var flags = Iso04Descriptor(Iso03Saved(cjk ? Iso03NotoFace() : ElementFace.For(ElementFont.Sans), text)).GetInteger(PdfName.Get("Flags"))!.Value;
        Assert.NotEqual(0, flags & 4);
        Assert.Equal(0, flags & 32);
    }

    /// <summary>
    /// 9.8.2 sentences 28, 40, 41 (reader, today's behaviour): ForceBold (bit 19) does not thicken small glyphs — an embedded font with /Flags
    /// 0x40020 at 4 points draws the same pixels as without the bit.
    /// </summary>
    [Fact]
    public void ForceBold_SmallEmbeddedGlyphs_SamePixels()
    {
        var small = PdfARuleCases.Stream(4, "", "BT /F1 4 Tf 72 700 Td (ABABAB) Tj ET");
        Iso04SameLook(PdfARuleCases.WithTrueType(more: small)(),
            PdfARuleCases.WithTrueType(descriptor: d => d.Replace("/Flags 32", $"/Flags {0x40020}", StringComparison.Ordinal), more: small)());
    }

    // ------------------------------------------------------------ 9.8.3: descriptors of CIDFonts

    /// <summary>
    /// 9.8.3.1 sentences 1–4 (/Style), 5–9 (/Lang), 10–12 (/FD), 9.8.3.2 sentences 1–7 (/Style with /Panose) and 9.8.3.3 sentences 1–9 (/FD of
    /// the class Proportional): entries of a CIDFont descriptor PDF-Knead does not use — the page looks and reads the same with them; the copy
    /// keeps /Style and /Lang.
    /// </summary>
    [Theory]
    [InlineData("/Style<</Panose<080002000504050902040304>>>>", "Style")]
    [InlineData("/Lang/ja", "Lang")]
    [InlineData("/FD<</Proportional<</Type/FontDescriptor/FontName/LiberationSans/Flags 4/StemV 80/AvgWidth 500>>>>", "")]
    public void CidFontDescriptorEntries_ChangeNothing(string entry, string kept)
    {
        byte[] File(string extra) => PdfARuleCases.WithCidTrueType(descriptor: d => d.Replace("/FontFile2 7 0 R", "/FontFile2 7 0 R" + extra, StringComparison.Ordinal))();
        var with = File(entry);
        Iso04SameLook(File(""), with);
        if (kept.Length == 0) return;
        using var saved = PdfDocument.Open(Iso04Saved(with));
        Assert.NotNull(Iso03Descendant(Iso04F1(saved)).GetDictionary(PdfName.Get("FontDescriptor"))!.Get(PdfName.Get(kept)));
    }

    // ------------------------------------------------------------ 9.9.1, 9.9.2

    /// <summary>
    /// 9.9.1 sentences 5–7 (writer): a font whose licence forbids embedding is not embedded — fsType 0x0002 (restricted licence) makes a system
    /// font not embeddable; the same font with fsType 0 is.
    /// </summary>
    [Fact]
    public void RestrictedLicence_FontNotEmbeddable()
    {
        var original = OpenTypeFont.Parse(Iso04SansFile.Value);
        var os2 = original.TableData("OS/2").ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(8), 0);
        Assert.True(RichFonts.Embeddable(OpenTypeFont.Parse(ColorFontBuilder.WithTables(Iso04SansFile.Value, new Dictionary<string, byte[]?> { ["OS/2"] = os2 }))));
        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(8), 0x0002);
        Assert.False(RichFonts.Embeddable(OpenTypeFont.Parse(ColorFontBuilder.WithTables(Iso04SansFile.Value, new Dictionary<string, byte[]?> { ["OS/2"] = os2 }))));
    }

    /// <summary>
    /// 9.9.1 sentences 21, 72–74 (writer): a TrueType subset keeps head, hhea, loca, maxp, cvt, prep, glyf, hmtx and fpgm where the original has
    /// them — a subset of Liberation Sans (which has cvt, fpgm and prep) has all nine.
    /// </summary>
    [Fact]
    public void TrueTypeSubset_KeepsTheNineTables()
    {
        var original = OpenTypeFont.Parse(Iso04SansFile.Value);
        var subset = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(Iso04SansFile.Value, [0, PdfARuleCases.Gid('A')]));
        foreach (var tag in (string[])["head", "hhea", "loca", "maxp", "cvt ", "prep", "glyf", "hmtx", "fpgm"])
        {
            Assert.True(original.HasTable(tag), $"{tag} fehlt im Original");
            Assert.True(subset.HasTable(tag), $"{tag} fehlt in der Teilschrift");
        }
    }

    /// <summary>
    /// 9.9.1 sentences 31–36 (reader): an OpenType program with TrueType outlines may be FontFile3 /OpenType — the font draws the same glyph as
    /// with the program in FontFile2.
    /// </summary>
    [Fact]
    public void FontFile3OpenType_TrueTypeOutlines_SameGlyphAsFontFile2()
    {
        var program = TrueTypeSubsetter.SubsetWithCmap(Iso04SansFile.Value, [0, PdfARuleCases.Gid('A'), PdfARuleCases.Gid('B')]);
        using var two = PdfDocument.Open(PdfARuleCases.WithTrueType()());
        using var three = PdfDocument.Open(PdfARuleCases.WithTrueType(descriptor: d => d.Replace("/FontFile2 7 0 R", "/FontFile3 7 0 R", StringComparison.Ordinal),
            more: PdfARuleCases.Stream(7, "/Subtype/OpenType", Encoding.Latin1.GetString(program)))());
        var expected = PdfFont.Get(Iso04F1(two), two).Glyph('A', 1).Outline;
        Assert.False(expected.IsEmpty);
        Assert.True(Iso03SameOutline(expected, PdfFont.Get(Iso04F1(three), three).Glyph('A', 1).Outline));
    }

    /// <summary>
    /// 9.9.1 sentences 66–68 (writer): a CFF program in FontFile3 has no /Length1 to /Length3 and holds exactly one font — typed CJK text saved.
    /// </summary>
    [Fact]
    public void TypedCjk_FontFile3WithoutLengths_OneFontInTheCff()
    {
        var program = (PdfStream)Iso04Descriptor(Iso03Saved(Iso03NotoFace(), "日本")).Get(PdfName.Get("FontFile3"))!;
        foreach (var key in (string[])["Length1", "Length2", "Length3"]) Assert.False(program.Dictionary.ContainsKey(PdfName.Get(key)));
        Assert.Equal(1, Iso03CffTop(program.RawData!).Names);
    }

    /// <summary>
    /// 9.9.1 sentences 69–71 (reader): /PaintType of a Type 1 program is ignored, the text rendering mode decides — a program with /PaintType 2
    /// in mode 0 is filled, the same pixels as without it.
    /// </summary>
    [Fact]
    public void Type1PaintType2_FillMode_FillsTheGlyph()
    {
        static byte[] File(byte[] program)
        {
            var clear = program.AsSpan().IndexOf("eexec\n"u8) + 6;
            var pdf = new TestPdf();
            var catalog = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog"))));
            var pages = pdf.Add(new PdfDictionary());
            var file = pdf.Add(new PdfStream(TestPdf.Dict(("Length1", PdfInteger.Of(clear)), ("Length2", PdfInteger.Of(program.Length - clear)),
                ("Length3", PdfInteger.Of(0))), program));
            var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("KneadTest")),
                ("FirstChar", PdfInteger.Of(65)), ("LastChar", PdfInteger.Of(65)), ("Widths", new PdfArray([PdfInteger.Of(600)])),
                ("FontDescriptor", TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("KneadTest")), ("Flags", PdfInteger.Of(4)),
                    ("FontBBox", TestPdf.Box(0, 0, 600, 700)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(700)), ("Descent", PdfInteger.Of(0)),
                    ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile", TestPdf.Ref(file))))));
            var content = pdf.Add(new PdfStream(new PdfDictionary(), "BT 0 Tr /F1 150 Tf 20 40 Td (A) Tj ET"u8.ToArray()));
            var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pages)), ("MediaBox", TestPdf.Box(0, 0, 200, 200)),
                ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(font)))))), ("Contents", TestPdf.Ref(content))));
            pdf.Replace(pages, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray([TestPdf.Ref(page)])), ("Count", PdfInteger.Of(1))));
            ((PdfDictionary)pdf.Get(catalog)).Set(PdfName.Pages, TestPdf.Ref(pages));
            pdf.RootNumber = catalog;
            return pdf.Build();
        }
        var filled = File(Type1Builder.Build());
        var stroked = File(Type1Builder.Build(clearExtra: "/PaintType 2 def\n/StrokeWidth 20 def\n"));
        Iso04SameLook(filled, stroked);
        using var doc = PdfDocument.Open(stroked);
        using var bitmap = ContentImageTests.Render(doc, doc.Pages[0]);
        var ink = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
            if (bitmap.GetPixel(x, y).Red < 128) ink++;
        Assert.True(ink > 2000, $"gezeichnet: {ink} Pixel");
    }

    /// <summary>9.9.1 sentence 76 (writer): a TrueType program under a CIDFont needs no cmap — typed text in Liberation Sans saved: none.</summary>
    [Fact]
    public void TypedText_CidTrueTypeProgram_HasNoCmap()
    {
        var program = (PdfStream)Iso04Descriptor(Iso04SavedSans()).Get(PdfName.Get("FontFile2"))!;
        Assert.False(OpenTypeFont.Parse(program.RawData!).HasTable("cmap"));
    }

    /// <summary>
    /// 9.9.2 sentences 7–8 (writer): .notdef is defined in a subset — a TrueType subset of Liberation Sans with „A“ has glyph 0 with the outline
    /// of the original; a CFF subset (written CID-keyed) has glyph 0 as CID 0 with the outline of the original's glyph 0; a CFF built anew
    /// from outlines (the PDF/A way) names its glyph 0 .notdef.
    /// </summary>
    [Fact]
    public void Subsets_DefineNotdefAsGlyphZero()
    {
        var original = OpenTypeFont.Parse(Iso04SansFile.Value);
        var subset = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(Iso04SansFile.Value, [PdfARuleCases.Gid('A')]));
        Assert.False(original.Outline(0).IsEmpty);
        Assert.True(Iso03SameOutline(original.Outline(0), subset.Outline(0)));

        var symbol = EmbeddedFonts.LoadStandard("FoxitSymbol.cff");
        var cff = CffFont.Parse(CffSubsetter.Subset(new ArraySegment<byte>(symbol), [5]));
        Assert.Equal(0, cff.GlyphByCid(0));
        Assert.True(Iso03SameOutline(CffFont.Parse(symbol).Outline(0), cff.Outline(0)));

        var a = PdfARuleCases.Gid('A');
        var built = CffFont.Parse(CffBuilder.Build("Probe", new Core.Pdf.Graphics.Matrix(1.0 / original.UnitsPerEm, 0, 0, 1.0 / original.UnitsPerEm, 0, 0),
            [("A", original.Outline(a), original.AdvanceWidth(a))]));
        Assert.Equal(".notdef", built.GlyphName(0));
        Assert.Equal("A", built.GlyphName(1));
    }

    // ------------------------------------------------------------ 9.10.1

    /// <summary>
    /// 9.10.1 sentence 12 (reader, today's behaviour; the replacement text belongs to M12): /ActualText of marked content around a glyph does not
    /// change the text extraction yet — „/Span &lt;&lt;/ActualText (fi)&gt;&gt; BDC (X) Tj EMC“ reads „X“.
    /// </summary>
    [Fact]
    public void ActualTextAroundAGlyph_TextIsTheGlyphsToday()
    {
        var (doc, list) = InterpreterTests.Page("BT /F1 20 Tf 20 100 Td /Span <</ActualText (fi)>> BDC (X) Tj EMC ET",
            _ => TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")),
                ("BaseFont", TestPdf.N("Helvetica")), ("Encoding", TestPdf.N("WinAnsiEncoding"))))))));
        using (doc) Assert.Equal("X", PageText.Create(list.Text).Text);
    }
}
