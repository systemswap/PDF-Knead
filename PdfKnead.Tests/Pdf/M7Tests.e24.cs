using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, four display regressions the pixel comparison against the state „M6 completed“ found, decided after measuring the real files and new
/// small probes against the three reference readers (docs/FEHLERGEDAECHTNIS.md, No. 1198–1201): /ToUnicode /Identity-H as a name with a CID
/// TrueType font that is not embedded, Differences names without a glyph in a TrueType font with (3,1), translucent text in a knockout group,
/// and the soft mask of a graphics state inside a d1 glyph.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ No. 1198: /ToUnicode /Identity-H as a name, CIDFontType2 not embedded

    /// <summary>A composite Arial font that is not embedded, Identity-H, CIDToGIDMap 41 → Z, 42 → X, with the given /ToUnicode.</summary>
    private static PdfDictionary E24ArialCid(TestPdf pdf, PdfObject? toUnicode)
    {
        var map = new byte[2 * 0x60];
        map[2 * 0x41 + 1] = 61;   // Z of the usual TrueType glyph order
        map[2 * 0x42 + 1] = 59;   // X
        var cid = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType2")), ("BaseFont", TestPdf.N("Arial")),
            ("CIDSystemInfo", Iso03Collection("Adobe", "Identity", 0)), ("CIDToGIDMap", TestPdf.Ref(pdf.Add(TestPdf.FlateStream(map)))),
            ("FontDescriptor", TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("Arial")), ("Flags", PdfInteger.Of(32)))));
        var font = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("Arial")),
            ("Encoding", TestPdf.N("Identity-H")), ("DescendantFonts", new PdfArray([cid])));
        if (toUnicode is PdfStream stream) font.Set(PdfName.Get("ToUnicode"), TestPdf.Ref(pdf.Add(stream)));
        else if (toUnicode is not null) font.Set(PdfName.Get("ToUnicode"), toUnicode);
        return font;
    }

    /// <summary>
    /// ISO 32000-2, 9.10.3 (/ToUnicode is a CMap stream) and 9.7.4.2 (a CIDToGIDMap belongs to an embedded program): /ToUnicode /Identity-H as a
    /// name with a CID TrueType font that is not embedded reads the codes as Unicode, and the installed font draws those characters — as one reference
    /// reader draws it and two show the real file issue11915 (codes 0041 0042 give „AB“; probes U1a, U1d, U1e). The text is „AB“ as all
    /// three readers read it. Before, the CID was taken as a glyph of the installed font („^_“), as only one reference reader draws it.
    /// </summary>
    [Fact]
    public void E24_NonEmbeddedCidTrueType_ToUnicodeIdentityAsAName_ReadsTheCodesAsUnicode()
    {
        var bfchar = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /U def /CMapType 2 def\n" +
                     "1 begincodespacerange <0000> <FFFF> endcodespacerange\n2 beginbfchar <0041> <0041> <0042> <0042> endbfchar\n" +
                     "endcmap CMapName currentdict /CMap defineresource pop end end\n";
        var (nameDoc, nameList, named) = Iso03PageFont("BT /F1 20 Tf 20 100 Td <00410042> Tj ET", pdf => E24ArialCid(pdf, TestPdf.N("Identity-H")));
        var (streamDoc, _, streamed) = Iso03PageFont("", pdf => E24ArialCid(pdf, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(bfchar))));
        using (nameDoc)
        using (streamDoc)
        {
            foreach (var code in (uint[])[0x41, 0x42])
            {
                var expected = streamed.Glyph(code, 2).Outline;
                Assert.False(expected.IsEmpty);
                Assert.True(Iso03SameOutline(expected, named.Glyph(code, 2).Outline), $"Code {code:X4}: nicht die Glyphe des Zeichens");
            }
            Assert.Equal("AB", string.Concat(nameList.Text.Select(t => t.Text)));
        }
    }

    // ------------------------------------------------------------ No. 1199: Differences names without a glyph, TrueType with (3,1)

    /// <summary>
    /// ISO 32000-2, 9.6.5.4 sentences 27–31 with what the reference readers add: in a non-symbolic embedded TrueType font with a (3,1) subtable a
    /// /Differences name that gives no glyph — outside the glyph list (g5167, foo) or with a value (3,1) lacks (Euro) — and that post does not
    /// name takes the code itself through (3,1), as all three reference readers draw names outside the glyph list and two of them the name Euro
    /// (probes N2a–N2h; real file issue13316: „A C E F“). Not through (1,0) (the old „XZ“); a code (3,1) lacks as well draws glyph 0 (N2d).
    /// Before, glyph 0 for each.
    /// </summary>
    [Fact]
    public void E24_NonSymbolicTrueType_NameWithoutAGlyph_TakesTheCodeThrough31()
    {
        int a = E7LiberationFont.GlyphForUnicode('A'), b = E7LiberationFont.GlyphForUnicode('B');
        int x = E7LiberationFont.GlyphForUnicode('X'), z = E7LiberationFont.GlyphForUnicode('Z');
        var mac = E7Format0(new Dictionary<int, int> { [65] = x, [66] = z });
        var program = E7WithCmap(true, ((1, 0), mac), ((3, 1), E7Format4(new Dictionary<int, int> { [0x41] = a, [0x42] = b })));
        const string outside = "<< /BaseEncoding /WinAnsiEncoding /Differences [65 /g5167 /g11927] >>";
        Assert.Equal(E7GlyphBounds('A'), E7TrueTypeGlyph(program, 32, outside, 65));
        Assert.Equal(E7GlyphBounds('B'), E7TrueTypeGlyph(program, 32, outside, 66));
        Assert.Equal(E7GlyphBounds('A'), E7TrueTypeGlyph(program, 32, "<< /Differences [65 /foo /bar] >>", 65));
        // a name with a Unicode value (3,1) lacks: the code through (3,1) as well
        var swapped = E7WithCmap(true, ((1, 0), E7Format0(new Dictionary<int, int> { [65] = a, [66] = b })),
            ((3, 1), E7Format4(new Dictionary<int, int> { [0x41] = x, [0x42] = z })));
        Assert.Equal(E7GlyphBounds('X'), E7TrueTypeGlyph(swapped, 32, "<< /Differences [65 /Euro /B] >>", 65));
        Assert.Equal(E7GlyphBounds('Z'), E7TrueTypeGlyph(swapped, 32, "<< /Differences [65 /Euro /B] >>", 66));
        // (3,1) without the codes: glyph 0, not the glyph (1,0) gives
        var without = E7WithCmap(true, ((1, 0), mac), ((3, 1), E7Format4(new Dictionary<int, int> { [0x30] = a })));
        Assert.Equal(E7LiberationFont.Outline(0).Bounds, E7TrueTypeGlyph(without, 32, outside, 65));
    }

    // ------------------------------------------------------------ No. 1200: translucent text in a knockout group

    /// <summary>
    /// A page with a transparency group: a red square at /ca 0.5 (20–180), then a text object with a blue square glyph of a Type 3 font (50–150)
    /// at /ca 0.5.
    /// </summary>
    private static byte[] E24KnockoutFile(bool isolated, bool knockout) =>
        E7File("/X Do", "/XObject << /X 5 0 R >>",
            E7Stream($"/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /I {(isolated ? "true" : "false")} " +
                     $"/K {(knockout ? "true" : "false")} >> /Resources << /ExtGState << /H << /ca 0.5 >> >> /Font << /F3 6 0 R >> >>",
                "/H gs 1 0 0 rg 20 20 160 160 re f 0 0 1 rg BT /F3 100 Tf 50 50 Td (a) Tj ET"),
            "<< /Type /Font /Subtype /Type3 /FontMatrix [0.001 0 0 0.001 0 0] /FontBBox [0 0 1000 1000] /CharProcs << /g 7 0 R >> " +
            "/Encoding << /Differences [97 /g] >> /FirstChar 97 /LastChar 97 /Widths [1000] >>",
            E7Stream("", "1000 0 d0 0 0 1000 1000 re f"));

    /// <summary>
    /// ISO 32000-2, 11.4.6 and 11.6.6 (in a knockout group every element is composited with the group's initial backdrop) with 9.3.8 (a text object
    /// with /TK true is itself a non-isolated knockout group, an element of the group around it): translucent text in a knockout group knocks out
    /// what the group drew before beneath it — the blue square at /ca 0.5 over the red square comes out light blue as over white, as two of three
    /// reference readers draw it (probes KO1, KO2, KO4; real file knockout_groups_test). In a normal group it blends with the red (KO3). Before, the
    /// group of the text object was composited onto the knockout group as a whole and blended with the red.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void E24_KnockoutGroup_TranslucentText_KnocksOutWhatLiesBeneath(bool isolated)
    {
        var c = E7Pixel(E24KnockoutFile(isolated, true), 100, 100);
        Assert.True(c is { Red: > 110 and < 145, Green: > 110 and < 145, Blue: > 240 }, $"{c}");
        var mixed = E7Pixel(E24KnockoutFile(isolated, false), 100, 100);
        Assert.True(mixed is { Red: > 110, Green: < 90, Blue: > 170 }, $"{mixed}");
    }

    /// <summary>
    /// 9.3.8: the knockout of a text object only shows where its glyphs overlap. Translucent glyphs side by side look the same with and without
    /// it, so the text object gets no layer and its glyphs are drawn as before (real file knockout_groups_test: in the normal groups the text came
    /// out up to 38 levels different at its edges). Overlapping translucent glyphs keep the group (E7_TextKnockout_True_…).
    /// </summary>
    [Fact]
    public void E24_TextKnockout_TranslucentGlyphsSideBySide_NoLayer()
    {
        using var doc = PdfDocument.Open(E7File($"/G gs 0 g BT /F1 100 Tf 20 50 Td (lI) Tj ET", $"/ExtGState << /G << /ca 0.5 >> >> {E7Helvetica}"));
        Assert.DoesNotContain(PageInterpreter.Build(doc, doc.Pages[0]).Groups.ToArray(), g => g.NeedsLayer);
        using var overlapping = PdfDocument.Open(E7File($"/G gs 0 g {E7Overlap}", $"/ExtGState << /G << /ca 0.5 >> >> {E7Helvetica}"));
        Assert.Contains(PageInterpreter.Build(overlapping, overlapping.Pages[0]).Groups.ToArray(), g => g.NeedsLayer);
    }
}
