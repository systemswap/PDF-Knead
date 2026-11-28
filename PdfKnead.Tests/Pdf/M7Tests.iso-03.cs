using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package iso-03 (ISO 32000-2, 9.7.3 to 9.7.5.4): the tests the drafts of the sentence-by-sentence comparison asked for — character
/// collections, CIDFonts, their widths, the predefined and embedded CMaps.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (iso-03)

    private static string Iso03Repo([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static readonly Lazy<byte[]> Iso03SansFile = new(() => EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf"));
    private static readonly Lazy<OpenTypeFont> Iso03Sans = new(() => OpenTypeFont.Parse(Iso03SansFile.Value));

    /// <summary>The Japanese face of Noto Sans CJK (a CFF font, CID-keyed), read straight from the font folder of the repository.</summary>
    internal static readonly Lazy<OpenTypeFont> Iso03NotoJp = new(() => OpenTypeFont.Parse(File.ReadAllBytes(Path.Combine(Iso03Repo(), "Schriften", "CJK", "NotoSansCJK-Regular.ttc")), 0));

    /// <summary>The CFF table of Noto Sans CJK JP.</summary>
    internal static ArraySegment<byte> Iso03NotoCff()
    {
        var font = Iso03NotoJp.Value;
        var (offset, length) = font.Table("CFF ")!.Value;
        return new ArraySegment<byte>(font.FileData, offset, length);
    }

    /// <summary>A page font for typed text with CFF outlines (Noto Sans CJK JP), as text editing builds it.</summary>
    internal static ElementFace Iso03NotoFace() => ElementFace.For("M7-iso03-NotoSansCJK-JP", () => Iso03NotoJp.Value, false, false, false);

    private static PdfString Iso03Str(string s) => new(Encoding.ASCII.GetBytes(s));

    internal static bool Iso03SameOutline(GlyphOutline a, GlyphOutline b) => a.Verbs.SequenceEqual(b.Verbs) && a.Points.SequenceEqual(b.Points);

    /// <summary>Typed text saved: the font dictionary text editing writes for the glyphs of <paramref name="text"/>.</summary>
    internal static PdfDictionary Iso03Saved(ElementFace face, string text) =>
        face.SavedFont(text.EnumerateRunes().Select(r => r.Value).Distinct().ToDictionary(face.Glyph, char.ConvertFromUtf32));

    internal static PdfDictionary Iso03Descendant(PdfDictionary type0) => (PdfDictionary)type0.GetArray(PdfName.Get("DescendantFonts"))!.Get(0);

    /// <summary>
    /// The CFF data of a FontFile3 program: the stream itself for /CIDFontType0C, the 'CFF ' table for /OpenType (typed text in a font with CFF
    /// outlines is saved as OpenType since docs/FEHLERGEDAECHTNIS.md, No. 1168).
    /// </summary>
    internal static byte[] Iso03CffProgram(PdfStream program) => program.Dictionary.GetName(PdfName.Subtype)?.Value == "OpenType"
        ? OpenTypeFont.Parse(program.RawData!).TableData("CFF ").ToArray()
        : program.RawData!;

    private static PdfDictionary Iso03Collection(string registry, string ordering, int supplement) =>
        TestPdf.Dict(("Registry", Iso03Str(registry)), ("Ordering", Iso03Str(ordering)), ("Supplement", PdfInteger.Of(supplement)));

    /// <summary>Name INDEX and ROS of a CFF program: number of names, the first name, registry, ordering and supplement (null, -1 without ROS).</summary>
    internal static (int Names, string Name, string? Registry, string? Ordering, int Supplement) Iso03CffTop(byte[] cff)
    {
        var data = new ArraySegment<byte>(cff);
        var r = new FontReader(data);
        r.U8();
        r.U8();
        r.Position = r.U8();
        var names = CffFont.CffIndex.Read(data, ref r);
        var tops = CffFont.CffIndex.Read(data, ref r);
        var strings = CffFont.CffIndex.Read(data, ref r);
        var top = CffFont.Dict.Parse(tops.Item(data, 0));
        string? Sid(double sid) => sid >= 391 && sid - 391 < strings.Count ? Encoding.Latin1.GetString(strings.Item(data, (int)sid - 391)) : null;
        var ros = top.Get(1230);
        return (names.Count, names.Count > 0 ? Encoding.Latin1.GetString(names.Item(data, 0)) : "",
            ros is { Length: 3 } ? Sid(ros[0]) : null, ros is { Length: 3 } ? Sid(ros[1]) : null, ros is { Length: 3 } ? (int)ros[2] : -1);
    }

    /// <summary>A one-page document whose /F1 is the font <paramref name="font"/> builds; the document, its display list and the font.</summary>
    internal static (PdfDocument Doc, Core.Pdf.Graphics.DisplayList List, PdfFont Font) Iso03PageFont(string content, Func<TestPdf, PdfDictionary> font)
    {
        var (doc, list) = InterpreterTests.Page(content, pdf => TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(pdf.Add(font(pdf))))))));
        var dict = doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!;
        return (doc, list, PdfFont.Get(dict, doc));
    }

    /// <summary>A composite font over Liberation Sans embedded as CIDFontType2 (CIDToGIDMap Identity), with further entries of the CIDFont.</summary>
    internal static PdfDictionary Iso03CidTrueType(TestPdf pdf, PdfObject encoding, params (string Key, PdfObject Value)[] cidExtra)
    {
        var program = pdf.Add(TestPdf.FlateStream(Iso03SansFile.Value, TestPdf.Dict(("Length1", PdfInteger.Of(Iso03SansFile.Value.Length)))));
        var descriptor = TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("LiberationSans")), ("Flags", PdfInteger.Of(32)),
            ("FontBBox", TestPdf.Box(-203, -303, 1050, 910)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(905)), ("Descent", PdfInteger.Of(-212)),
            ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(program)));
        var cid = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType2")), ("BaseFont", TestPdf.N("LiberationSans")),
            ("CIDSystemInfo", Iso03Collection("Adobe", "Identity", 0)), ("FontDescriptor", descriptor), ("CIDToGIDMap", TestPdf.N("Identity")));
        foreach (var (key, value) in cidExtra) cid.Set(PdfName.Get(key), value);
        return TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("LiberationSans")), ("Encoding", encoding),
            ("DescendantFonts", new PdfArray([cid])));
    }

    /// <summary>An encoding CMap program with one-byte codes and the given body.</summary>
    private static byte[] Iso03CMapProgram(string body, string codespace = "<00> <FF>") => Encoding.ASCII.GetBytes(
        "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> def\n" +
        $"/CMapName /Probe-H def\n/CMapType 1 def\n1 begincodespacerange\n{codespace}\nendcodespacerange\n{body}\nendcmap\n" +
        "CMapName currentdict /CMap defineresource pop\nend\nend\n");

    private static string? Iso03RuleId(string name) => PdfARuleTests.Catalogue.Single(c => c.Name == name).Rule.Id(PdfAConformance.A2b);

    // ------------------------------------------------------------ 9.7.3

    /// <summary>
    /// 9.7.3 sentences 7–8 (checker): the /CIDSystemInfo of a CMap may be an array of dictionaries, and it must agree with the CIDFont. A CMap
    /// whose array names Japan1 over a CIDFont of Adobe-Identity does not agree — the check reports it.
    /// </summary>
    [Fact]
    public void CMapCollectionAsArray_CheckerReportsTheMismatch()
    {
        var a = PdfARuleCases.Gid('A');
        var b = PdfARuleCases.Gid('B');
        var program = "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
                      "/CIDSystemInfo [ << /Registry (Adobe) /Ordering (Japan1) /Supplement 2 >> ] def\n/CMapName /Test-H def\n/CMapType 1 def\n" +
                      $"1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n2 begincidchar\n<{a:X4}> {a}\n<{b:X4}> {b}\nendcidchar\n" +
                      "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n";
        var file = PdfARuleCases.WithCidTrueType(t => t.Replace("/Encoding/Identity-H", "/Encoding 9 0 R", StringComparison.Ordinal),
            more: PdfARuleCases.Stream(9, "/Type/CMap/CMapName/Test-H/CIDSystemInfo[<</Registry(Adobe)/Ordering(Japan1)/Supplement 2>>]", program))();
        using var doc = PdfDocument.Open(file);
        var found = PdfACheck.Check(doc, PdfAConformance.A2b);
        Assert.Contains(found, v => v.Id == Iso03RuleId("CidSystemInfo"));
    }

    /// <summary>
    /// 9.7.3 sentences 9–13, 9.7.4.1 sentences 9–12 and 21–23 (writer): the CIDFont of typed text is a font (/Type /Font) and names its
    /// character collection — Adobe-Identity-0, a registry of Adobe's.
    /// </summary>
    [Fact]
    public void TypedText_DescendantFont_IsAFontOfTheAdobeIdentityCollection()
    {
        var cid = Iso03Descendant(Iso03Saved(ElementFace.For(ElementFont.Sans), "Probe"));
        Assert.Equal("Font", cid.GetName(PdfName.Type)?.Value);
        var info = cid.GetDictionary(PdfName.Get("CIDSystemInfo"))!;
        Assert.Equal("Adobe", info.GetString(PdfName.Get("Registry"))!.ToText());
        Assert.Equal("Identity", info.GetString(PdfName.Get("Ordering"))!.ToText());
        Assert.Equal(0, info.GetInteger(PdfName.Get("Supplement")));
    }

    /// <summary>
    /// 9.7.3 sentences 9–13 (writer): a registry begins with a developer prefix and an underscore. The PDF/A copy of a CIDFont whose registry has
    /// that form („ABCD_Muster“) keeps it.
    /// </summary>
    [Fact]
    public void PdfACopy_CidFontWithAPrefixedRegistry_KeepsIt()
    {
        var file = PdfARuleCases.WithCidTrueType(cid: c => c.Replace("/Registry(Adobe)", "/Registry(ABCD_Muster)", StringComparison.Ordinal),
            shown: $"<0000{PdfARuleCases.Gid('B'):X4}>")();
        using var doc = PdfDocument.Open(file);
        var copy = PdfAExport.Write(doc, PageItem.From(doc), PdfAConformance.A2b);
        using var written = PdfDocument.Open(copy.Bytes);
        var font = written.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!;
        Assert.Equal("ABCD_Muster", Iso03Descendant(font).GetDictionary(PdfName.Get("CIDSystemInfo"))!.GetString(PdfName.Get("Registry"))!.ToText());
    }

    // ------------------------------------------------------------ 9.7.4.1

    /// <summary>
    /// 9.7.4.1 sentences 6–8 (reader, today's behaviour): a CIDFont is no operand of Tf. A Tf that names a CIDFontType2 directly is read
    /// leniently as a simple font — one byte per code.
    /// </summary>
    [Fact]
    public void TfNamingACidFontDirectly_ReadsItAsASimpleFont()
    {
        var (doc, list, font) = Iso03PageFont("BT /F1 20 Tf 20 100 Td <4142> Tj ET", _ => TestPdf.Dict(("Type", TestPdf.N("Font")),
            ("Subtype", TestPdf.N("CIDFontType2")), ("BaseFont", TestPdf.N("LiberationSans")), ("CIDSystemInfo", Iso03Collection("Adobe", "Identity", 0))));
        using (doc)
        {
            Assert.Equal(1, font.ReadCode([0x41, 0x42], 0, out var code));
            Assert.Equal(0x41u, code);
            Assert.Equal("AB", string.Concat(list.Text.Select(t => t.Text)));
        }
    }

    /// <summary>9.7.4.1 sentences 9–12 (checker): /Type /Font is required in a CIDFont as well; a CIDFont without it is reported (FontType).</summary>
    [Fact]
    public void CidFontWithoutType_CheckerReportsFontType()
    {
        var file = PdfARuleCases.WithCidTrueType(cid: c => c.Replace("<</Type/Font/Subtype/CIDFontType2", "<</Subtype/CIDFontType2", StringComparison.Ordinal))();
        using var doc = PdfDocument.Open(file);
        Assert.Contains(PdfACheck.Check(doc, PdfAConformance.A2b), v => v.Id == Iso03RuleId("FontType"));
    }

    /// <summary>
    /// 9.7.4.1 sentences 15–20 (writer): /BaseFont of a CIDFontType0 is the font name of its CFF program, with the subset prefix in front —
    /// typed CJK text saved: /BaseFont without the prefix is the name in the Name INDEX of the embedded CFF.
    /// </summary>
    [Fact]
    public void TypedCjk_BaseFontWithoutPrefix_IsTheNameInTheCffNameIndex()
    {
        var cid = Iso03Descendant(Iso03Saved(Iso03NotoFace(), "日本語"));
        var baseFont = cid.GetName(PdfName.Get("BaseFont"))!.Value;
        Assert.Matches("^[A-Z]{6}\\+", baseFont);
        var program = (PdfStream)cid.GetDictionary(PdfName.Get("FontDescriptor"))!.Get(PdfName.Get("FontFile3"))!;
        Assert.Equal(baseFont[7..], Iso03CffTop(Iso03CffProgram(program)).Name);
    }

    /// <summary>9.7.4.1 sentences 26–28 (reader): /DW is optional, its default 1000 — a CID that /W does not list is 1 em wide without /DW.</summary>
    [Fact]
    public void CidFontWithoutDw_CidOutsideW_IsOneEmWide()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        var font = PdfFont.Get(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("Test")),
            ("Encoding", TestPdf.N("Identity-H")), ("DescendantFonts", new PdfArray([TestPdf.Dict(("Type", TestPdf.N("Font")),
                ("Subtype", TestPdf.N("CIDFontType0")), ("BaseFont", TestPdf.N("Test")), ("CIDSystemInfo", Iso03Collection("Adobe", "Identity", 0)),
                ("W", new PdfArray([PdfInteger.Of(10), new PdfArray([PdfInteger.Of(500)])])))]))), doc);
        Assert.Equal(0.5, font.Glyph(10, 2).Width, 6);
        Assert.Equal(1.0, font.Glyph(11, 2).Width, 6);
    }

    // ------------------------------------------------------------ 9.7.4.2

    /// <summary>
    /// 9.7.4.2 sentences 1–3 (writer): the character collection a CFF program names is taken into the CIDFont dictionary — the PDF/A copy of a
    /// composite font that is not embedded embeds a CID-keyed CFF whose ROS equals the /CIDSystemInfo of the written CIDFont.
    /// </summary>
    [Fact]
    public void PdfACopy_NonEmbeddedCompositeFont_CffCarriesTheCidSystemInfo()
    {
        var (pdf, page) = PdfACheckTests.Page("BT /F1 24 Tf 72 700 Td <00010002> Tj ET", new PdfDictionary());
        var toUnicode = pdf.Add(new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /U def 1 begincodespacerange <0000> <FFFF> endcodespacerange " +
            "2 beginbfchar <0001> <0041> <0002> <0042> endbfchar endcmap CMapName currentdict /CMap defineresource pop end end")));
        var descriptor = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("Erfunden")), ("Flags", PdfInteger.Of(32)),
            ("FontBBox", TestPdf.Box(-200, -300, 1200, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)), ("Descent", PdfInteger.Of(-200)),
            ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80))));
        var cid = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType2")), ("BaseFont", TestPdf.N("Erfunden")),
            ("FontDescriptor", TestPdf.Ref(descriptor)), ("CIDSystemInfo", Iso03Collection("Adobe", "Identity", 0)), ("DW", PdfInteger.Of(667)));
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("Erfunden")),
            ("Encoding", TestPdf.N("Identity-H")), ("DescendantFonts", new PdfArray([cid])), ("ToUnicode", TestPdf.Ref(toUnicode))));
        page.Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(font))))));
        using var doc = PdfDocument.Open(pdf.Build());
        var copy = PdfAExport.Write(doc, PageItem.From(doc), PdfAConformance.A2b);
        Assert.True(copy.Conforms, PdfATests.Describe(copy.Violations));
        using var written = PdfDocument.Open(copy.Bytes);
        var writtenCid = Iso03Descendant(written.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!);
        Assert.Equal("CIDFontType0", writtenCid.GetName(PdfName.Subtype)?.Value);
        var info = writtenCid.GetDictionary(PdfName.Get("CIDSystemInfo"))!;
        var program = writtenCid.GetDictionary(PdfName.Get("FontDescriptor"))!.GetStream(PdfName.Get("FontFile3"))!;
        var top = Iso03CffTop(written.GetStreamData(program));
        Assert.Equal(info.GetString(PdfName.Get("Registry"))!.ToText(), top.Registry);
        Assert.Equal(info.GetString(PdfName.Get("Ordering"))!.ToText(), top.Ordering);
        Assert.Equal(info.GetInteger(PdfName.Get("Supplement")), top.Supplement);
    }

    /// <summary>
    /// 9.7.4.2 sentence 11 (reader): a CFF program without the CIDFont operators under CIDFontType0 — the CID is the glyph number in the
    /// CharStrings INDEX: without a CIDToGIDMap code n draws the outline of glyph n (a name-keyed CFF, the bundled symbol font).
    /// </summary>
    [Fact]
    public void NameKeyedCffUnderCidFontType0_WithoutMap_CodeIsTheGlyph()
    {
        var data = EmbeddedFonts.LoadStandard("FoxitSymbol.cff");
        var cff = CffFont.Parse(data);
        Assert.False(cff.IsCidKeyed);
        var (doc, _, font) = Iso03PageFont("", pdf => TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("Muster")),
            ("Encoding", TestPdf.N("Identity-H")), ("DescendantFonts", new PdfArray([TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType0")),
                ("BaseFont", TestPdf.N("Muster")), ("CIDSystemInfo", Iso03Collection("Adobe", "Identity", 0)),
                ("FontDescriptor", TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("Muster")), ("Flags", PdfInteger.Of(4)),
                    ("FontFile3", TestPdf.Ref(pdf.Add(TestPdf.FlateStream(data, TestPdf.Dict(("Subtype", TestPdf.N("CIDFontType0C"))))))))))]))));
        using (doc)
        {
            var compared = 0;
            foreach (var gid in (int[])[5, 20, 40, 80, 120])
            {
                if (gid >= cff.GlyphCount || cff.Outline(gid).IsEmpty) continue;
                Assert.True(Iso03SameOutline(cff.Outline(gid), font.Glyph((uint)gid, 2).Outline), $"Glyphe {gid}");
                compared++;
            }
            Assert.True(compared >= 3, $"nur {compared} Glyphen verglichen");
        }
    }

    /// <summary>
    /// 9.7.4.2 sentence 17 (reader): a CIDToGIDMap of a CIDFontType2 that is not embedded is ignored. Without /ToUnicode a map that sends CID 36
    /// to glyph 37 changes nothing: the glyph is the one of CID 36, as without the map.
    /// </summary>
    [Fact]
    public void NonEmbeddedCidTrueType_WithoutText_IgnoresTheCidToGidMap()
    {
        var map = new byte[2 * 40];
        for (var cid = 0; cid < 40; cid++) map[2 * cid + 1] = (byte)cid;
        map[2 * 36 + 1] = 37;
        PdfDictionary Font(TestPdf pdf, bool withMap)
        {
            var cid = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType2")), ("BaseFont", TestPdf.N("Arial")),
                ("CIDSystemInfo", Iso03Collection("Adobe", "Identity", 0)),
                ("FontDescriptor", TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("Arial")), ("Flags", PdfInteger.Of(32)))));
            if (withMap) cid.Set(PdfName.Get("CIDToGIDMap"), TestPdf.Ref(pdf.Add(TestPdf.FlateStream(map))));
            return TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("Arial")),
                ("Encoding", TestPdf.N("Identity-H")), ("DescendantFonts", new PdfArray([cid])));
        }
        var (withoutDoc, _, without) = Iso03PageFont("", pdf => Font(pdf, false));
        var (withDoc, _, with) = Iso03PageFont("", pdf => Font(pdf, true));
        using (withoutDoc)
        using (withDoc)
        {
            var expected = without.Glyph(36, 2).Outline;
            Assert.False(expected.IsEmpty);
            Assert.True(Iso03SameOutline(expected, with.Glyph(36, 2).Outline), "die CIDToGIDMap wurde benutzt");
        }
    }

    /// <summary>
    /// 9.7.4.2 sentence 22 (writer): every CIDFont has a glyph for CID 0 — the subsets text editing embeds (TrueType for Liberation Sans, CFF
    /// for Noto CJK) keep glyph 0 with the outline of the original.
    /// </summary>
    [Fact]
    public void TypedTextSubsets_KeepGlyphZero()
    {
        var sans = ElementFace.For(ElementFont.Sans);
        var trueType = OpenTypeFont.Parse(((PdfStream)Iso03Descendant(Iso03Saved(sans, "AB")).GetDictionary(PdfName.Get("FontDescriptor"))!
            .Get(PdfName.Get("FontFile2"))!).RawData!);
        Assert.False(sans.Font.Outline(0).IsEmpty);
        Assert.True(Iso03SameOutline(sans.Font.Outline(0), trueType.Outline(0)));

        var noto = Iso03NotoFace();
        var cff = CffFont.Parse(Iso03CffProgram((PdfStream)Iso03Descendant(Iso03Saved(noto, "日本")).GetDictionary(PdfName.Get("FontDescriptor"))!
            .Get(PdfName.Get("FontFile3"))!));
        var original = Iso03NotoJp.Value.Cff!;
        Assert.False(original.Outline(original.GlyphByCid(0)).IsEmpty);
        Assert.True(Iso03SameOutline(original.Outline(original.GlyphByCid(0)), cff.Outline(cff.GlyphByCid(0))));
    }

    // ------------------------------------------------------------ 9.7.4.3

    /// <summary>
    /// 9.7.4.3 sentences 1–2 (reader): in horizontal writing the width moves the origin along the baseline only — two glyphs of a composite font
    /// (Identity-H, /DW 600) at 10 points: the second starts 6 units further right, on the same baseline.
    /// </summary>
    [Fact]
    public void Type0IdentityH_SecondGlyphMovesByItsWidth_OnTheBaseline()
    {
        var a = Iso03Sans.Value.GlyphForUnicode('A');
        var (doc, list, _) = Iso03PageFont($"BT /F1 10 Tf 20 100 Td <{a:X4}{a:X4}> Tj ET",
            pdf => Iso03CidTrueType(pdf, TestPdf.N("Identity-H"), ("DW", PdfInteger.Of(600))));
        using (doc)
        {
            Assert.Equal(2, list.Text.Length);
            Assert.Equal(6.0, list.Text[1].X0 - list.Text[0].X0, 3);
            Assert.Equal(list.Text[0].Y0, list.Text[1].Y0, 3);
        }
    }

    /// <summary>
    /// 9.7.4.3 sentences 3–4 (writer): the widths of /W agree with those of the program — typed text saved: the width of each glyph is its
    /// advance from hmtx in thousandths of the em.
    /// </summary>
    [Fact]
    public void TypedText_WidthsInW_AreTheAdvancesOfTheProgram()
    {
        var face = ElementFace.For(ElementFont.Sans);
        var w = Iso03Descendant(Iso03Saved(face, "AWil 1")).GetArray(PdfName.W)!;
        var checkedOnes = 0;
        for (var i = 0; i + 1 < w.Count; i += 2)
        {
            var first = (int)w.GetNumber(i)!.Value;
            var list = (PdfArray)w.Get(i + 1)!;
            for (var k = 0; k < list.Count; k++, checkedOnes++)
                Assert.Equal(face.Font.AdvanceWidth(first + k) * 1000.0 / face.Font.UnitsPerEm, list.GetNumber(k)!.Value, 3);
        }
        Assert.Equal(6, checkedOnes);
    }

    private static PdfFont Iso03Vertical(PdfDocument doc, params (string Key, PdfObject Value)[] cidEntries)
    {
        var cid = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType0")), ("BaseFont", TestPdf.N("Test")),
            ("CIDSystemInfo", Iso03Collection("Adobe", "Identity", 0)));
        foreach (var (key, value) in cidEntries) cid.Set(PdfName.Get(key), value);
        return PdfFont.Get(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("Test")),
            ("Encoding", TestPdf.N("Identity-V")), ("DescendantFonts", new PdfArray([cid]))), doc);
    }

    /// <summary>
    /// 9.7.4.3 sentences 21–23 (reader): /DW2 gives the vertical part of the position vector and of the advance; the horizontal part of the
    /// position vector is half the width. /DW 300, /DW2 [900 −800], a CID outside /W2: v = (0.15, 0.9), w1y = −0.8.
    /// </summary>
    [Fact]
    public void CidOutsideW2_TakesDw2AndHalfTheWidth()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        var font = Iso03Vertical(doc, ("DW", PdfInteger.Of(300)), ("DW2", new PdfArray([PdfInteger.Of(900), PdfInteger.Of(-800)])));
        var g = font.Glyph(7, 2);
        Assert.Equal(0.15, g.OriginX, 6);
        Assert.Equal(0.9, g.OriginY, 6);
        Assert.Equal(-0.8, g.VerticalAdvance, 6);
    }

    /// <summary>
    /// 9.7.4.3 sentences 29–35 (reader): /W2 in the list form „c [w1y vx vy …]“ gives consecutive CIDs from c three numbers each —
    /// „40 [−900 300 800 −700 200 600]“: CID 40 and 41 with these values.
    /// </summary>
    [Fact]
    public void W2ListForm_GivesConsecutiveCidsTheirTriples()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        var font = Iso03Vertical(doc, ("W2", new PdfArray([PdfInteger.Of(40), new PdfArray([PdfInteger.Of(-900), PdfInteger.Of(300), PdfInteger.Of(800),
            PdfInteger.Of(-700), PdfInteger.Of(200), PdfInteger.Of(600)])])));
        var g40 = font.Glyph(40, 2);
        var g41 = font.Glyph(41, 2);
        Assert.Equal((-0.9, 0.3, 0.8), (Math.Round(g40.VerticalAdvance, 6), Math.Round(g40.OriginX, 6), Math.Round(g40.OriginY, 6)));
        Assert.Equal((-0.7, 0.2, 0.6), (Math.Round(g41.VerticalAdvance, 6), Math.Round(g41.OriginX, 6), Math.Round(g41.OriginY, 6)));
    }

    // ------------------------------------------------------------ 9.7.5.1, 9.7.5.4 (usefont)

    /// <summary>
    /// 9.7.5.1 sentences 4–5 and 9.7.5.4 sentence 3 (reader; with 1 today's behaviour): a CMap refers to its CIDFont by font number 0 — an
    /// embedded CMap with „0 usefont“ (or, against the rule, „1 usefont“) before its cidchar entries maps as without, and the glyph and width
    /// come from DescendantFonts[0].
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void EmbeddedCMapWithUsefont_ReadsAsFontZero(int number)
    {
        var body = "1 begincidchar\n<41> 5\nendcidchar\n";
        var withFont = Iso03CMapProgram($"{number} usefont\n" + body);
        Assert.Equal(CMap.Parse(Iso03CMapProgram(body)).ToCid(0x41, 1), CMap.Parse(withFont).ToCid(0x41, 1));
        var (doc, _, font) = Iso03PageFont("", pdf => Iso03CidTrueType(pdf, TestPdf.Ref(pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("CMap")),
                ("CMapName", TestPdf.N("Probe-H")), ("CIDSystemInfo", Iso03Collection("Adobe", "Identity", 0))), withFont))),
            ("W", new PdfArray([PdfInteger.Of(5), new PdfArray([PdfInteger.Of(250)])]))));
        using (doc)
        {
            Assert.Equal(5, font.Cid(0x41, 1));
            Assert.Equal(0.25, font.Glyph(0x41, 1).Width, 6);
            Assert.True(Iso03SameOutline(Iso03Sans.Value.Outline(5), font.Glyph(0x41, 1).Outline));
        }
    }

    // ------------------------------------------------------------ 9.7.5.2

    /// <summary>
    /// 9.7.5.2 sentences 1–3 (reader): the Unicode CMaps take the high byte first, „UTF16“ in the name means UTF-16BE — under UniJIS-UTF16-H
    /// the surrogate pair D840 DC0B is one code of four bytes with a CID.
    /// </summary>
    [Fact]
    public void UniJisUtf16_SurrogatePair_IsOneFourByteCode()
    {
        var cmap = PredefinedCMaps.Get("UniJIS-UTF16-H")!;
        Assert.Equal(4, cmap.ReadCode([0xD8, 0x40, 0xDC, 0x0B], 0, out var code));
        Assert.Equal(0xD840DC0Bu, code);
        Assert.NotEqual(0, cmap.ToCid(code, 4));
    }

    /// <summary>
    /// 9.7.5.2 sentences 5–118 and 141–143 (reader): each predefined CMap of Table 116 is known, belongs to its collection, and writes vertically
    /// exactly when its name ends in „V“ (the names „-V“ and „V“), horizontally otherwise.
    /// </summary>
    [Theory]
    [InlineData("GB-EUC-H", "GB1")]
    [InlineData("GB-EUC-V", "GB1")]
    [InlineData("GBpc-EUC-H", "GB1")]
    [InlineData("GBpc-EUC-V", "GB1")]
    [InlineData("GBK-EUC-H", "GB1")]
    [InlineData("GBK-EUC-V", "GB1")]
    [InlineData("GBKp-EUC-H", "GB1")]
    [InlineData("GBKp-EUC-V", "GB1")]
    [InlineData("GBK2K-H", "GB1")]
    [InlineData("GBK2K-V", "GB1")]
    [InlineData("UniGB-UCS2-H", "GB1")]
    [InlineData("UniGB-UCS2-V", "GB1")]
    [InlineData("UniGB-UTF16-H", "GB1")]
    [InlineData("UniGB-UTF16-V", "GB1")]
    [InlineData("B5pc-H", "CNS1")]
    [InlineData("B5pc-V", "CNS1")]
    [InlineData("HKscs-B5-H", "CNS1")]
    [InlineData("HKscs-B5-V", "CNS1")]
    [InlineData("ETen-B5-H", "CNS1")]
    [InlineData("ETen-B5-V", "CNS1")]
    [InlineData("ETenms-B5-H", "CNS1")]
    [InlineData("ETenms-B5-V", "CNS1")]
    [InlineData("CNS-EUC-H", "CNS1")]
    [InlineData("CNS-EUC-V", "CNS1")]
    [InlineData("UniCNS-UCS2-H", "CNS1")]
    [InlineData("UniCNS-UCS2-V", "CNS1")]
    [InlineData("UniCNS-UTF16-H", "CNS1")]
    [InlineData("UniCNS-UTF16-V", "CNS1")]
    [InlineData("83pv-RKSJ-H", "Japan1")]
    [InlineData("90ms-RKSJ-H", "Japan1")]
    [InlineData("90ms-RKSJ-V", "Japan1")]
    [InlineData("90msp-RKSJ-H", "Japan1")]
    [InlineData("90msp-RKSJ-V", "Japan1")]
    [InlineData("90pv-RKSJ-H", "Japan1")]
    [InlineData("Add-RKSJ-H", "Japan1")]
    [InlineData("Add-RKSJ-V", "Japan1")]
    [InlineData("EUC-H", "Japan1")]
    [InlineData("EUC-V", "Japan1")]
    [InlineData("Ext-RKSJ-H", "Japan1")]
    [InlineData("Ext-RKSJ-V", "Japan1")]
    [InlineData("H", "Japan1")]
    [InlineData("V", "Japan1")]
    [InlineData("UniJIS-UCS2-H", "Japan1")]
    [InlineData("UniJIS-UCS2-V", "Japan1")]
    [InlineData("UniJIS-UCS2-HW-H", "Japan1")]
    [InlineData("UniJIS-UCS2-HW-V", "Japan1")]
    [InlineData("UniJIS-UTF16-H", "Japan1")]
    [InlineData("UniJIS-UTF16-V", "Japan1")]
    [InlineData("KSC-EUC-H", "Korea1")]
    [InlineData("KSC-EUC-V", "Korea1")]
    [InlineData("KSCms-UHC-H", "Korea1")]
    [InlineData("KSCms-UHC-V", "Korea1")]
    [InlineData("KSCms-UHC-HW-H", "Korea1")]
    [InlineData("KSCms-UHC-HW-V", "Korea1")]
    [InlineData("KSCpc-EUC-H", "Korea1")]
    [InlineData("UniKS-UCS2-H", "Korea1")]
    [InlineData("UniKS-UCS2-V", "Korea1")]
    [InlineData("UniKS-UTF16-H", "Korea1")]
    [InlineData("UniKS-UTF16-V", "Korea1")]
    public void PredefinedCMapsOfTable116_KnownWithCollectionAndWritingMode(string name, string collection)
    {
        var cmap = PredefinedCMaps.Get(name);
        Assert.NotNull(cmap);
        Assert.Equal(collection, PredefinedCMaps.Collection(name));
        Assert.Equal(name == "V" || name.EndsWith("-V", StringComparison.Ordinal) ? 1 : 0, cmap.WMode);
    }

    /// <summary>
    /// 9.7.5.2 sentences 5–22 (reader): GBKp-EUC-H reads the one-byte code 24h as the dollar sign (proportional Latin), GBK-EUC-H as another
    /// CID (the yuan sign of GB 1988 in the glyphs of the collection; Adobe-GB1-UCS2 gives both the text „$“).
    /// </summary>
    [Fact]
    public void GbkpAndGbk_Code24_DollarAndYuan()
    {
        var dollar = PredefinedCMaps.Get("GBKp-EUC-H")!.ToCid(0x24, 1);
        var yuan = PredefinedCMaps.Get("GBK-EUC-H")!.ToCid(0x24, 1);
        Assert.Equal("$", PredefinedCMaps.Text("GB1", dollar));
        Assert.NotEqual(0, yuan);
        Assert.NotEqual(dollar, yuan);
    }

    /// <summary>
    /// 9.7.5.2 sentences 23–33 and 51–59 (reader): GBK2K-H (GB 18030) and UniCNS-UTF16-H have codes of four bytes — one of each is read as a
    /// whole and maps to a CID.
    /// </summary>
    [Theory]
    [InlineData("GBK2K-H")]
    [InlineData("UniCNS-UTF16-H")]
    public void FourByteCodes_ReadWholeAndMapped(string name)
    {
        var cmap = PredefinedCMaps.Get(name)!;
        var (code, _, cid) = cmap.CidMappings().First(m => m.Bytes == 4 && m.Cid > 0);
        byte[] bytes = [(byte)(code >> 24), (byte)(code >> 16), (byte)(code >> 8), (byte)code];
        Assert.Equal(4, cmap.ReadCode(bytes, 0, out var read));
        Assert.Equal(code, read);
        Assert.Equal(cid, cmap.ToCid(read, 4));
    }

    /// <summary>9.7.5.2 sentences 34–50 (reader): the Big Five CMaps map the code A440h to the CID of „一“ in CNS1.</summary>
    [Theory]
    [InlineData("B5pc-H")]
    [InlineData("B5pc-V")]
    [InlineData("HKscs-B5-H")]
    [InlineData("HKscs-B5-V")]
    [InlineData("ETen-B5-H")]
    [InlineData("ETen-B5-V")]
    [InlineData("ETenms-B5-H")]
    [InlineData("ETenms-B5-V")]
    public void BigFiveCMaps_MapACodeToItsCid(string name)
    {
        var cmap = PredefinedCMaps.Get(name)!;
        Assert.Equal(2, cmap.ReadCode([0xA4, 0x40], 0, out var code));
        var cid = cmap.ToCid(code, 2);
        Assert.NotEqual(0, cid);
        Assert.Equal("一", PredefinedCMaps.Text("CNS1", cid));
    }

    /// <summary>9.7.5.2 sentences 80–99 (reader): UniJIS-UCS2-HW-H gives „A“ another CID (half-width) than UniJIS-UCS2-H.</summary>
    [Fact]
    public void UniJisHw_GivesTheLatinLetterAnotherCid()
    {
        var proportional = PredefinedCMaps.Get("UniJIS-UCS2-H")!.ToCid('A', 2);
        var halfWidth = PredefinedCMaps.Get("UniJIS-UCS2-HW-H")!.ToCid('A', 2);
        Assert.NotEqual(0, proportional);
        Assert.NotEqual(0, halfWidth);
        Assert.NotEqual(proportional, halfWidth);
    }

    /// <summary>9.7.5.2 sentences 100–118 (reader): a Korea1 CID (KSC-EUC-H, code B0A1h) has its text through Adobe-Korea1-UCS2: „가“.</summary>
    [Fact]
    public void Korea1Cid_HasItsTextThroughAdobeKorea1Ucs2()
    {
        var cid = PredefinedCMaps.Get("KSC-EUC-H")!.ToCid(0xB0A1, 2);
        Assert.NotEqual(0, cid);
        Assert.Equal("가", PredefinedCMaps.Text("Korea1", cid));
    }

    /// <summary>9.7.5.2 sentence 129 (reader): Adobe-CNS1-7 is supported — a CID of supplement 7 (19 088 onwards) has its character.</summary>
    [Fact]
    public void Cns1Supplement7Cid_HasItsText()
    {
        Assert.NotNull(PredefinedCMaps.Text("CNS1", 19100));
    }

    // ------------------------------------------------------------ 9.7.5.3, 9.7.5.4 (embedded CMaps)

    /// <summary>
    /// 9.7.5.3 sentences 5–7, 8–10, 11–14 and 9.7.5.4 sentence 6 (writer): the encoding CMap of the PDF/A copy — /Type /CMap, /CMapName equal to
    /// the name in the program, /CIDSystemInfo equal to that of the program and of the CIDFont, and none of beginrearrangedfont,
    /// endrearrangedfont, beginusematrix, endusematrix in the program.
    /// </summary>
    [Fact]
    public void PdfACopy_OwnEncodingCMap_DictionaryMatchesTheProgram()
    {
        using var doc = PdfDocument.Open(PdfARuleCases.WithCidTrueType(shown: $"<0000{PdfARuleCases.Gid('B'):X4}>")());
        var copy = PdfAExport.Write(doc, PageItem.From(doc), PdfAConformance.A2b);
        Assert.True(copy.Conforms, PdfATests.Describe(copy.Violations));
        using var written = PdfDocument.Open(copy.Bytes);
        var font = written.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!;
        var encoding = Assert.IsType<PdfStream>(font.Get(PdfName.Get("Encoding")));
        var data = written.GetStreamData(encoding);
        var program = Encoding.Latin1.GetString(data);
        Assert.Equal("CMap", encoding.Dictionary.GetName(PdfName.Type)?.Value);
        var name = CMap.Parse(data).Name;
        Assert.NotEmpty(name);
        Assert.Equal(name, encoding.Dictionary.GetName(PdfName.Get("CMapName"))?.Value);
        var m = Regex.Match(program, @"/Registry\s*\(([^)]*)\)(?:\s*def)?\s*/Ordering\s*\(([^)]*)\)(?:\s*def)?\s*/Supplement\s+(\d+)");
        Assert.True(m.Success, "keine /CIDSystemInfo im Programm");
        foreach (var info in (PdfDictionary[])[encoding.Dictionary.GetDictionary(PdfName.Get("CIDSystemInfo"))!, Iso03Descendant(font).GetDictionary(PdfName.Get("CIDSystemInfo"))!])
        {
            Assert.Equal(m.Groups[1].Value, info.GetString(PdfName.Get("Registry"))!.ToText());
            Assert.Equal(m.Groups[2].Value, info.GetString(PdfName.Get("Ordering"))!.ToText());
            Assert.Equal(long.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture), info.GetInteger(PdfName.Get("Supplement")));
        }
        foreach (var op in (string[])["beginrearrangedfont", "endrearrangedfont", "beginusematrix", "endusematrix"])
            Assert.DoesNotContain(op, program, StringComparison.Ordinal);
    }

    private static PdfStream Iso03WModeCMap(string body) => new(TestPdf.Dict(("Type", TestPdf.N("CMap")), ("CMapName", TestPdf.N("Probe-H")),
        ("CIDSystemInfo", Iso03Collection("Adobe", "Japan1", 6)), ("WMode", PdfInteger.Of(1))), Encoding.ASCII.GetBytes(
        "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 6 >> def\n" +
        $"/CMapName /Probe-H def\n/CMapType 1 def\n/WMode 0 def\n{body}endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n"));

    /// <summary>
    /// 9.7.5.3 sentences 15–18 (reader): /WMode 1 in the dictionary of an embedded CMap whose program says 0 and uses UniJIS-UCS2-H — the codes
    /// keep the CIDs of the CMap they use.
    /// </summary>
    [Fact]
    public void EmbeddedCMap_DictionaryWMode1_UsecmapKeepsTheCidsOfItsBase()
    {
        var (doc, _, font) = Iso03PageFont("", pdf => Iso03CidTrueType(pdf, TestPdf.Ref(pdf.Add(Iso03WModeCMap("/UniJIS-UCS2-H usecmap\n")))));
        using (doc)
        {
            var expected = PredefinedCMaps.Get("UniJIS-UCS2-H")!.ToCid(0x8981, 2);
            Assert.NotEqual(0x8981, expected);
            Assert.Equal(expected, font.Cid(0x8981, 2));
        }
    }

    /// <summary>
    /// 9.7.5.3 sentences 15–18 (reader): /WMode 1 in the dictionary of an embedded CMap whose program says 0 and uses no other CMap — the
    /// dictionary would make the font vertical; two of three reference readers write it horizontally as its program says (special rule;
    /// probes K13, K13c, 26.11.2026; FG 1153), and so does PDF-Knead.
    /// </summary>
    [Fact]
    public void EmbeddedCMap_DictionaryWMode1_WithoutUsecmap_WritesAsTheProgramSays()
    {
        var (doc, _, font) = Iso03PageFont("", pdf => Iso03CidTrueType(pdf, TestPdf.Ref(pdf.Add(Iso03WModeCMap(
            "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n1 begincidrange\n<0000> <FFFF> 0\nendcidrange\n")))));
        using (doc) Assert.False(font.Vertical);
    }

    /// <summary>
    /// 9.7.5.4 sentence 5 (reader, special rule A198): the norm has a code whose CID has no glyph take the notdef CID of its code — code 05h
    /// maps to CID 7, which the embedded CID-keyed CFF lacks, its notdef range gives CID 100. Two of three reference readers draw nothing for
    /// a CID without a glyph (probes K7/K8, 25.11.2026), and so does PDF-Knead: otherwise subsets without a space glyph showed boxes where
    /// those readers show nothing.
    /// </summary>
    [Fact]
    public void CidWithoutGlyph_DrawsNothing_AsTwoOfThreeReaders()
    {
        var (doc, _, font) = Iso03NotoSubsetFont("1 beginnotdefrange\n<00> <1F> 100\nendnotdefrange\n1 begincidchar\n<05> 7\nendcidchar\n");
        using (doc)
        {
            Assert.False(Iso03NotoJp.Value.Cff!.Outline(Iso03NotoJp.Value.Cff!.GlyphByCid(100)).IsEmpty);
            Assert.True(font.Glyph(0x05, 1).Outline.IsEmpty);
        }
    }

    /// <summary>
    /// 9.7.4.2 sentences 16–17 with ISO 32000-1 9.7.4.1 sentence 50 and 9.7.4.2 sentence 16 (writer, FG 1167): a CIDFontType2 without an embedded
    /// program has no /CIDToGIDMap (1.x: not allowed; 2.0: ignored) — saved as 1.7 and as 2.0 the map is gone; an embedded CIDFontType2 keeps
    /// its map.
    /// </summary>
    [Fact]
    public void NonEmbeddedCidTrueType_SavedWithoutCidToGidMap_EmbeddedKeepsIt()
    {
        PdfDictionary Descendant(PdfDocument doc) =>
            (PdfDictionary)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F"))!
                .GetArray(PdfName.Get("DescendantFonts"))!.Get(0)!;
        byte[] File(bool embedded)
        {
            var pdf = TestPdf.Simple(1);
            PdfDictionary font;
            if (embedded) font = Iso03CidTrueType(pdf, TestPdf.N("Identity-H"));
            else
            {
                var cid = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType2")), ("BaseFont", TestPdf.N("Arial")),
                    ("CIDSystemInfo", Iso03Collection("Adobe", "Identity", 0)),
                    ("FontDescriptor", TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("Arial")), ("Flags", PdfInteger.Of(32)))),
                    ("CIDToGIDMap", TestPdf.Ref(pdf.Add(TestPdf.FlateStream(new byte[80])))));
                font = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("Arial")),
                    ("Encoding", TestPdf.N("Identity-H")), ("DescendantFonts", new PdfArray([cid])));
            }
            pdf.Replace(4, TestPdf.FlateStream("BT /F 20 Tf 72 700 Td <00240025> Tj ET"u8.ToArray()));
            ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F", font)))));
            return pdf.Build();
        }
        foreach (var version in (ReadOnlySpan<string>)["1.7", "2.0"])
        {
            using (var saved = DocumentStructureNormTests.Saved(File(false), new PdfSaveOptions { Version = version }))
                Assert.False(Descendant(saved).ContainsKey(PdfName.Get("CIDToGIDMap")), version);
            using (var saved = DocumentStructureNormTests.Saved(File(true), new PdfSaveOptions { Version = version }))
                Assert.True(Descendant(saved).ContainsKey(PdfName.Get("CIDToGIDMap")), version);
        }
    }

    /// <summary>
    /// A composite font over a subset of Noto Sans CJK JP (CID-keyed CFF with the CIDs 0 and 100, embedded as CIDFontType0C) with an embedded
    /// one-byte CMap of the given body.
    /// </summary>
    internal static (PdfDocument Doc, Core.Pdf.Graphics.DisplayList List, PdfFont Font) Iso03NotoSubsetFont(string cmapBody)
    {
        var cff = Iso03NotoJp.Value.Cff!;
        var subset = CffSubsetter.Subset(Iso03NotoCff(), [0, cff.GlyphByCid(100)]);
        return Iso03PageFont("", pdf => TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("ABCDEF+Noto")),
            ("Encoding", TestPdf.Ref(pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("CMap")), ("CMapName", TestPdf.N("Probe-H")),
                ("CIDSystemInfo", Iso03Collection("Adobe", "Identity", 0))), Iso03CMapProgram(cmapBody))))),
            ("DescendantFonts", new PdfArray([TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType0")),
                ("BaseFont", TestPdf.N("ABCDEF+Noto")), ("CIDSystemInfo", Iso03Collection("Adobe", "Identity", 0)),
                ("FontDescriptor", TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ABCDEF+Noto")), ("Flags", PdfInteger.Of(4)),
                    ("FontFile3", TestPdf.Ref(pdf.Add(TestPdf.FlateStream(subset, TestPdf.Dict(("Subtype", TestPdf.N("CIDFontType0C"))))))))))]))));
    }
}

/// <summary>
/// M7, package iso-03, the tests that need the CJK substitute fonts (Noto CJK for fonts that are not embedded): they switch the shared font
/// folder and therefore run in the collection of the CJK fonts, one after another.
/// </summary>
[Collection(CjkFontsCollection.Name)]
public sealed class M7NormTestsIso03Cjk
{
    private static string Repo([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static PdfDictionary Collection(int supplement) => TestPdf.Dict(("Registry", new PdfString("Adobe"u8.ToArray())),
        ("Ordering", new PdfString("Japan1"u8.ToArray())), ("Supplement", PdfInteger.Of(supplement)));

    private static PdfDictionary Japan1Font(string subtype, string encoding, int supplement) => TestPdf.Dict(("Type", TestPdf.N("Font")),
        ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("Musterschrift-JP")), ("Encoding", TestPdf.N(encoding)),
        ("DescendantFonts", new PdfArray([TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N(subtype)), ("BaseFont", TestPdf.N("Musterschrift-JP")),
            ("CIDSystemInfo", Collection(supplement)), ("DW", PdfInteger.Of(1000)),
            ("FontDescriptor", TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("Musterschrift-JP")), ("Flags", PdfInteger.Of(4)),
                ("FontBBox", TestPdf.Box(0, -120, 1000, 880)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(880)), ("Descent", PdfInteger.Of(-120)),
                ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)))))])));

    /// <summary>
    /// 9.7.3 sentences 16–21 (reader): the supplement does not decide how the codes are read — a Japan1 CIDFont with supplement 7 and one with
    /// supplement 2 under 90ms-RKSJ-H give the same CIDs, widths, texts and glyphs.
    /// </summary>
    [Fact]
    public void Japan1Supplement_DoesNotChangeCidsGlyphsOrText()
    {
        CjkFonts.FolderForTests = Path.Combine(Repo(), "Schriften", "CJK");
        try
        {
            var (doc7, _, font7) = M7NormTests.Iso03PageFont("", _ => Japan1Font("CIDFontType0", "90ms-RKSJ-H", 7));
            var (doc2, _, font2) = M7NormTests.Iso03PageFont("", _ => Japan1Font("CIDFontType0", "90ms-RKSJ-H", 2));
            using (doc7)
            using (doc2)
            {
                foreach (var (code, length) in ((uint, int)[])[(0x9776, 2), (0x815B, 2), (0x41, 1)])
                {
                    Assert.Equal(font7.Cid(code, length), font2.Cid(code, length));
                    var g7 = font7.Glyph(code, length);
                    var g2 = font2.Glyph(code, length);
                    Assert.Equal(g7.Width, g2.Width);
                    Assert.Equal(g7.Text, g2.Text);
                    Assert.True(M7NormTests.Iso03SameOutline(g7.Outline, g2.Outline));
                }
                Assert.Equal("要", font7.Glyph(0x9776, 2).Text);
                Assert.False(font7.Glyph(0x9776, 2).Outline.IsEmpty);
            }
        }
        finally
        {
            CjkFonts.FolderForTests = null;
        }
    }

    /// <summary>
    /// 9.7.4.2 sentences 18–20 (reader): a CIDFontType2 that is not embedded, under UniJIS-UCS2-H — the code U+8981 is the character „要“, and
    /// the glyph is the one of the substitute font for that character.
    /// </summary>
    [Fact]
    public void NonEmbeddedCidTrueType_UniJis_GlyphFromTheSubstituteByUnicode()
    {
        CjkFonts.FolderForTests = Path.Combine(Repo(), "Schriften", "CJK");
        try
        {
            var (doc, _, font) = M7NormTests.Iso03PageFont("", _ => Japan1Font("CIDFontType2", "UniJIS-UCS2-H", 6));
            using (doc)
            {
                var glyph = font.Glyph(0x8981, 2);
                Assert.Equal("要", glyph.Text);
                var noto = CjkFonts.Get("Japan1", false)!;
                var expected = noto.Outline(noto.GlyphForUnicode(0x8981));
                Assert.False(expected.IsEmpty);
                Assert.True(M7NormTests.Iso03SameOutline(expected, glyph.Outline));
            }
        }
        finally
        {
            CjkFonts.FolderForTests = null;
        }
    }
}
