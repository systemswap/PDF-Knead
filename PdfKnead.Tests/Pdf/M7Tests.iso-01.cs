using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Konformitätsabgleich M7, package iso-01 (ISO 32000-2, 9.1 to 9.4: glyph positioning, text state, text objects, text space): the tests the
/// sentence-by-sentence drafts named as missing.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (package iso-01)

    private static PdfDictionary HelveticaIso01() =>
        TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica")),
            ("Encoding", TestPdf.N("WinAnsiEncoding")));

    private static readonly byte[] LiberationIso01 = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");

    /// <summary>The glyph of „I“ in the bundled Liberation Sans (a plain bar, also used as CID with Identity-V).</summary>
    private static int GidIIso01 { get; } = OpenTypeFont.Parse(LiberationIso01).GlyphForUnicode('I');

    private static int GidSpaceIso01 { get; } = OpenTypeFont.Parse(LiberationIso01).GlyphForUnicode(' ');

    private static string HexIso01(int gid) => gid.ToString("X4");

    private static PdfObject DescriptorIso01(TestPdf pdf)
    {
        var file = pdf.Add(TestPdf.FlateStream(LiberationIso01));
        return TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ABCDEF+Test")), ("Flags", PdfInteger.Of(32)),
            ("FontBBox", TestPdf.Box(-200, -300, 1200, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)),
            ("Descent", PdfInteger.Of(-200)), ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(file)));
    }

    /// <summary>A Type 0 font over the embedded Liberation Sans (CID = glyph) with the given encoding (a name or a CMap stream) and /W2.</summary>
    private static PdfDictionary Type0Iso01(TestPdf pdf, PdfObject encoding, PdfArray? w2 = null)
    {
        var cid = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType2")), ("BaseFont", TestPdf.N("ABCDEF+Test")),
            ("CIDSystemInfo", TestPdf.Dict(("Registry", new PdfString("Adobe"u8.ToArray())), ("Ordering", new PdfString("Identity"u8.ToArray())),
                ("Supplement", PdfInteger.Of(0)))),
            ("CIDToGIDMap", TestPdf.N("Identity")), ("DW", PdfInteger.Of(500)), ("FontDescriptor", DescriptorIso01(pdf)));
        if (w2 is not null) cid.Set(PdfName.Get("W2"), w2);
        return TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("ABCDEF+Test")),
            ("Encoding", encoding), ("DescendantFonts", new PdfArray([cid])));
    }

    /// <summary>An embedded CMap: the given code space ranges and single codes mapped to CIDs, horizontal or vertical (WMode).</summary>
    private static PdfObject CMapIso01(TestPdf pdf, string codespace, int ranges, (string Code, int Cid)[] chars, int wmode)
    {
        var body = new StringBuilder("/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n")
            .Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> def\n")
            .Append("/CMapName /KneadTest def /CMapType 1 def /WMode ").Append(wmode).Append(" def\n")
            .Append(ranges).Append(" begincodespacerange ").Append(codespace).Append(" endcodespacerange\n")
            .Append(chars.Length).Append(" begincidchar\n");
        foreach (var (code, cid) in chars) body.Append(code).Append(' ').Append(cid).Append('\n');
        body.Append("endcidchar\nendcmap CMapName currentdict /CMap defineresource pop end end\n");
        return TestPdf.Ref(pdf.Add(TestPdf.FlateStream(Encoding.ASCII.GetBytes(body.ToString()), TestPdf.Dict(("Type", TestPdf.N("CMap")),
            ("CMapName", TestPdf.N("KneadTest")), ("WMode", PdfInteger.Of(wmode)),
            ("CIDSystemInfo", TestPdf.Dict(("Registry", new PdfString("Adobe"u8.ToArray())), ("Ordering", new PdfString("Identity"u8.ToArray())),
                ("Supplement", PdfInteger.Of(0))))))));
    }

    /// <summary>A page with /F1 Helvetica, /V a Type 0 font with Identity-V (optionally with /W2) and /G an ExtGState, if given.</summary>
    private static (PdfDocument Doc, DisplayList List) PageIso01(string content, PdfArray? w2 = null, PdfDictionary? extGState = null,
        Func<TestPdf, PdfObject>? extraFont = null) =>
        InterpreterTests.Page(content, pdf =>
        {
            var fonts = TestPdf.Dict(("F1", HelveticaIso01()), ("V", Type0Iso01(pdf, TestPdf.N("Identity-V"), w2)));
            if (extraFont is not null) fonts.Set(PdfName.Get("X"), extraFont(pdf));
            var resources = TestPdf.Dict(("Font", fonts));
            if (extGState is not null) resources.Set(PdfName.Get("ExtGState"), TestPdf.Dict(("G", extGState)));
            return resources;
        });

    /// <summary>The origin of each drawn path in page space, in the order drawn (for glyphs the glyph origin: their matrices have no shift).</summary>
    private static List<(double X, double Y)> OriginsIso01(DisplayList list)
    {
        var result = new List<(double, double)>();
        foreach (var item in list.Items.Span)
            if (item.Path >= 0) result.Add(list.Matrices[item.Matrix].Transform(0, 0));
        return result;
    }

    private static string TextIso01(DisplayList list) => string.Concat(list.Text.Select(t => t.Text));

    private static SKBitmap RenderIso01(PdfDocument doc, DisplayList list, float scale = 1) =>
        SkiaRenderer.RenderPage(list, doc, doc.Pages[0], scale, new ImageCache());

    private static SKColor PixelIso01(SKBitmap b, double x, double pdfY, float scale = 1) =>
        b.GetPixel((int)(x * scale), b.Height - 1 - (int)(pdfY * scale));

    private static void ColorIso01(SKBitmap b, double x, double pdfY, SKColor expected, int tolerance = 40)
    {
        var actual = PixelIso01(b, x, pdfY);
        Assert.True(Math.Abs(actual.Red - expected.Red) < tolerance && Math.Abs(actual.Green - expected.Green) < tolerance &&
                    Math.Abs(actual.Blue - expected.Blue) < tolerance, $"bei ({x:0.#}, {pdfY:0.#}): erwartet {expected}, gefunden {actual}");
    }

    // ------------------------------------------------------------ 9.1, 9.2

    /// <summary>
    /// 9.1 sentence 2: a glyph is graphics and follows every transformation — scaled by the CTM (2 0 0 2 10 10 cm), the H starts at (10, 10),
    /// is twice 7.22 wide and twice as high as without the cm.
    /// </summary>
    [Fact]
    public void Iso01_Glyph_FollowsTheCtm()
    {
        var (plainDoc, plain) = PageIso01("BT /F1 10 Tf (H) Tj ET");
        var (doc, list) = PageIso01("2 0 0 2 10 10 cm BT /F1 10 Tf (H) Tj ET");
        using (plainDoc)
        using (doc)
        {
            var origin = OriginsIso01(list)[0];
            Assert.Equal(10, origin.X, 3);
            Assert.Equal(10, origin.Y, 3);
            var g = list.Text[0];
            Assert.Equal(2 * 7.22, g.X1 - g.X0, 1);
            Assert.Equal(2 * (plain.Text[0].Y3 - plain.Text[0].Y0), g.Y3 - g.Y0, 3);
        }
    }

    /// <summary>
    /// 9.2.2 sentences 37, 38: the font size is given in text space units; the size on the page follows from Tm and the CTM — 12 Tf with a
    /// text matrix scaling by 2 and with a CTM scaling by 2 both give a character 24 high.
    /// </summary>
    [Fact]
    public void Iso01_FontSize_ScaledByTextMatrixAndCtm()
    {
        foreach (var content in (ReadOnlySpan<string>)["BT /F1 12 Tf 2 0 0 2 0 0 Tm (H) Tj ET", "2 0 0 2 0 0 cm BT /F1 12 Tf (H) Tj ET"])
        {
            var (doc, list) = PageIso01(content);
            using (doc) Assert.Equal(24, list.Text[0].Size, 3);
        }
    }

    /// <summary>9.2.3 sentences 1, 4: without a colour operator the glyph is filled in black (the initial DeviceGray 0).</summary>
    [Fact]
    public void Iso01_TextWithoutColourOperator_FilledBlack()
    {
        var (doc, list) = PageIso01("BT /F1 50 Tf 10 10 Td (I) Tj ET");
        using (doc)
        {
            var item = Assert.Single(list.Items.ToArray());
            Assert.Equal(DisplayItemKind.Fill, item.Kind);
            Assert.Equal(0xFF000000u, list.Paints[item.Paint].Fill);
        }
    }

    /// <summary>
    /// 9.2.3 sentences 2, 3; 9.3.6 sentences 7 and 19 (Table 104, mode 1): the text takes the current fill or stroke colour as the mode says —
    /// with red as stroke and blue as fill colour, mode 0 gives one fill in blue, mode 1 one stroke (and no fill) in red, mode 5 a stroke in red.
    /// </summary>
    [Fact]
    public void Iso01_TextRenderingModes_TakeFillOrStrokeColour()
    {
        foreach (var (mode, kind) in (ReadOnlySpan<(int, DisplayItemKind)>)[(0, DisplayItemKind.Fill), (1, DisplayItemKind.Stroke), (5, DisplayItemKind.Stroke)])
        {
            var (doc, list) = PageIso01($"1 0 0 RG 0 0 1 rg BT /F1 50 Tf 10 10 Td {mode} Tr (I) Tj ET");
            using (doc)
            {
                var item = Assert.Single(list.Items.ToArray());
                Assert.Equal(kind, item.Kind);
                var paint = list.Paints[item.Paint];
                if (kind == DisplayItemKind.Fill) Assert.Equal(0xFF0000FFu, paint.Fill);
                else Assert.Equal(0xFFFF0000u, paint.Stroke);
            }
        }
    }

    /// <summary>
    /// 9.2.4 sentences 38, 39: the glyph bounding box is the smallest rectangle around the glyph — for a curve whose control points rise above
    /// it (0 0 → control points at height 1000 → 1000 0), the box ends at the top of the curve (750), not at the control points.
    /// </summary>
    [Fact]
    public void Iso01_GlyphBounds_AreTheSmallestRectangleAroundTheCurves()
    {
        var outline = new GlyphOutline();
        outline.MoveTo(0, 0);
        outline.CubicTo(0, 1000, 1000, 1000, 1000, 0);
        outline.Close();
        outline.Finish();
        Assert.Equal(750, outline.Bounds.Bottom, 1);   // largest y
        Assert.Equal(0, outline.Bounds.Top, 1);
    }

    /// <summary>
    /// 9.2.4 sentences 33, 34, 47–50 (Figure 55): in vertical writing the displacement has no horizontal part and the position vector v moves
    /// the origin — Identity-V with /W2 [g [-1000 500 880]] at 10 Tf from (100, 100): the first glyph's origin lies at (95, 91.2), v to the
    /// lower left, the second 10 lower at the same x.
    /// </summary>
    [Fact]
    public void Iso01_VerticalWriting_DisplacementAndPositionVector()
    {
        var g = GidIIso01;
        var w2 = new PdfArray([PdfInteger.Of(g), new PdfArray([PdfInteger.Of(-1000), PdfInteger.Of(500), PdfInteger.Of(880)])]);
        var (doc, list) = PageIso01($"BT /V 10 Tf 100 100 Td <{HexIso01(g)}{HexIso01(g)}> Tj ET", w2);
        using (doc)
        {
            var origins = OriginsIso01(list);
            Assert.Equal(2, origins.Count);
            Assert.Equal(95, origins[0].X, 3);
            Assert.Equal(91.2, origins[0].Y, 3);
            Assert.Equal(origins[0].X, origins[1].X, 3);
            Assert.Equal(origins[0].Y - 10, origins[1].Y, 3);
        }
    }

    // ------------------------------------------------------------ 9.3 text state

    private static PdfDictionary Type3Iso01(TestPdf pdf, int first, double[] widths, (int Code, string Name)[] names, (string Name, string Proc)[] procs)
    {
        var differences = new PdfArray();
        foreach (var (code, name) in names)
        {
            differences.Add(PdfInteger.Of(code));
            differences.Add(TestPdf.N(name));
        }
        var charProcs = new PdfDictionary();
        foreach (var (name, proc) in procs) charProcs.Set(PdfName.Get(name), TestPdf.Ref(pdf.Add(TestPdf.FlateStream(Encoding.ASCII.GetBytes(proc)))));
        return TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type3")), ("FontMatrix", TestPdf.Box(0.001, 0, 0, 0.001, 0, 0)),
            ("FontBBox", TestPdf.Box(0, 0, 1000, 1000)), ("CharProcs", charProcs),
            ("Encoding", TestPdf.Dict(("Type", TestPdf.N("Encoding")), ("Differences", differences))),
            ("FirstChar", PdfInteger.Of(first)), ("LastChar", PdfInteger.Of(first + widths.Length - 1)), ("Widths", TestPdf.Box(widths)));
    }

    private static Func<TestPdf, PdfObject> SquareType3Iso01 => pdf =>
        Type3Iso01(pdf, 97, [1000], [(97, "a")], [("a", "1000 0 0 0 1000 1000 d1 0 0 1000 1000 re f")]);

    /// <summary>
    /// 9.3.1 sentences 10, 11: the text state parameters apply to Type 3 fonts as well, and a negative font size is allowed — with 5 Tc the
    /// second glyph stands its width plus 5 further; at −20 Tf the glyph is turned by 180° and the text position moves to the left.
    /// </summary>
    [Fact]
    public void Iso01_Type3Font_CharacterSpacingAndNegativeSize()
    {
        var (doc, list) = PageIso01("BT /X 20 Tf 5 Tc 50 100 Td (aa) Tj ET", extraFont: SquareType3Iso01);
        using (doc)
        {
            Assert.Equal(2, list.Text.Length);
            Assert.Equal(50 + 20 + 5, list.Text[1].X0, 3);
        }
        var (negDoc, negative) = PageIso01("BT /X -20 Tf 100 100 Td (aa) Tj ET", extraFont: SquareType3Iso01);
        using (negDoc)
        {
            var first = negative.Text[0];
            Assert.Equal(first.X0 - 20, first.X1, 3);   // bottom edge runs to the left
            Assert.True(first.Y3 < first.Y0, "oben liegt unter unten (gedreht)");
            Assert.Equal(100 - 20, negative.Text[1].X0, 3);
        }
    }

    /// <summary>
    /// 9.3.1 sentence 15: the text state starts with its initial values on every page — page 1 sets 5 Tc, 50 Tz, 12 TL, 3 Tr and 4 Ts; page 2
    /// shows (AB) placed and visible as on a page alone.
    /// </summary>
    [Fact]
    public void Iso01_TextState_StartsAnewOnEveryPage()
    {
        const string second = "BT /F1 10 Tf 0 0 Td (AB) Tj ET";
        var pdf = new TestPdf();
        var cat = TestPdf.Dict(("Type", TestPdf.N("Catalog")));
        var catNumber = pdf.Add(cat);
        var pagesNumber = pdf.Add(new PdfDictionary());
        var resources = TestPdf.Dict(("Font", TestPdf.Dict(("F1", HelveticaIso01()))));
        int PageOf(string content) => pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pagesNumber)),
            ("MediaBox", TestPdf.Box(0, 0, 200, 200)), ("Resources", resources),
            ("Contents", TestPdf.Ref(pdf.Add(TestPdf.FlateStream(Encoding.Latin1.GetBytes(content)))))));
        var p1 = PageOf("5 Tc 50 Tz 12 TL 3 Tr 4 Ts");
        var p2 = PageOf(second);
        pdf.Replace(pagesNumber, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray([TestPdf.Ref(p1), TestPdf.Ref(p2)])), ("Count", PdfInteger.Of(2))));
        cat.Set(PdfName.Pages, TestPdf.Ref(pagesNumber));
        pdf.RootNumber = catNumber;
        using var doc = PdfDocument.Open(pdf.Build());
        PageInterpreter.Build(doc, doc.Pages[0]);
        var list = PageInterpreter.Build(doc, doc.Pages[1]);
        var (aloneDoc, alone) = PageIso01(second);
        using (aloneDoc)
        {
            Assert.Equal(alone.Text, list.Text);
            Assert.All(list.Text, t => Assert.True(t.Visible));
        }
    }

    /// <summary>9.3.1 sentences 25–28: Tz sets the horizontal scaling to its operand ÷ 100 — at 50 Tz the B follows the A at 6.67 · 0.5.</summary>
    [Fact]
    public void Iso01_Tz_ScalesHorizontally()
    {
        var (doc, list) = PageIso01("BT /F1 10 Tf 50 Tz 0 0 Td (AB) Tj ET");
        var (plainDoc, plain) = PageIso01("BT /F1 10 Tf 0 0 Td (AB) Tj ET");
        using (doc)
        using (plainDoc)
        {
            Assert.Equal(6.67 * 0.5, list.Text[1].X0, 2);
            Assert.Equal(6.67, plain.Text[1].X0, 2);
        }
    }

    /// <summary>
    /// 9.3.1 sentences 29–33: TL sets the leading, used by T* (and ' and "), initially 0 — with 14 TL the B after T* stands 14 lower; without TL
    /// at the same height; Tj does not change the line.
    /// </summary>
    [Fact]
    public void Iso01_TL_SetsTheLeadingForTStar()
    {
        var (doc, list) = PageIso01("BT /F1 10 Tf 14 TL 20 100 Td (A) Tj T* (B) Tj ET");
        var (plainDoc, plain) = PageIso01("BT /F1 10 Tf 20 100 Td (A) Tj T* (B) Tj ET");
        using (doc)
        using (plainDoc)
        {
            var o = OriginsIso01(list);
            Assert.Equal(100, o[0].Y, 3);
            Assert.Equal(86, o[1].Y, 3);
            Assert.Equal(20, o[1].X, 3);
            var p = OriginsIso01(plain);
            Assert.Equal(100, p[1].Y, 3);
        }
    }

    /// <summary>
    /// 9.3.1 sentence 36 (the reader's side; V14 vorgemerkt): without Tf the display shows the character with the substitute font in size 1.
    /// </summary>
    [Fact]
    public void Iso01_ShowWithoutTf_UsesTheSubstituteFontInSizeOne()
    {
        var (doc, list) = PageIso01("BT 10 10 Td (A) Tj ET");
        using (doc)
        {
            Assert.Equal("A", TextIso01(list));
            Assert.Equal(1, list.Text[0].Size, 3);
        }
    }

    /// <summary>9.3.1 sentences 42–46: Ts sets the text rise in unscaled units, initially 0 — the A at 3 Ts stands 3 higher than the B at 0 Ts.</summary>
    [Fact]
    public void Iso01_Ts_RaisesTheBaseline()
    {
        var (doc, list) = PageIso01("BT /F1 10 Tf 0 100 Td 3 Ts (A) Tj 0 Ts (B) Tj ET");
        using (doc)
        {
            var o = OriginsIso01(list);
            Assert.Equal(3, o[0].Y - o[1].Y, 3);
        }
    }

    /// <summary>9.3.2 sentence 2: in horizontal writing Tc is scaled by Th — at 50 Tz and 4 Tc the B stands at (6.67 + 4) · 0.5.</summary>
    [Fact]
    public void Iso01_CharacterSpacing_ScaledByHorizontalScaling()
    {
        var (doc, list) = PageIso01("BT /F1 10 Tf 50 Tz 4 Tc 0 0 Td (AB) Tj ET");
        using (doc) Assert.Equal((6.67 + 4) * 0.5, list.Text[1].X0, 2);
    }

    /// <summary>
    /// 9.3.2 sentences 1, 3, 5, 6: Tc is added to every displacement, also vertically, and there a negative Tc widens — with Identity-V at
    /// 10 Tf the glyphs stand 10 apart, at −2 Tc 12.
    /// </summary>
    [Fact]
    public void Iso01_CharacterSpacing_Vertical_NegativeWidens()
    {
        var g = HexIso01(GidIIso01);
        var (plainDoc, plain) = PageIso01($"BT /V 10 Tf 100 150 Td <{g}{g}> Tj ET");
        var (doc, list) = PageIso01($"BT /V 10 Tf -2 Tc 100 150 Td <{g}{g}> Tj ET");
        using (plainDoc)
        using (doc)
        {
            var p = OriginsIso01(plain);
            var o = OriginsIso01(list);
            Assert.Equal(10, p[0].Y - p[1].Y, 3);
            Assert.Equal(12, o[0].Y - o[1].Y, 3);
            Assert.Equal(o[0].X, o[1].X, 3);
        }
    }

    /// <summary>
    /// 9.3.3 sentence 4: in vertical writing a positive Tw makes word gaps smaller — a Type 0 font whose embedded CMap (WMode 1) maps the single
    /// byte 20: the glyph after it stands 3 higher at 3 Tw than without.
    /// </summary>
    [Fact]
    public void Iso01_WordSpacing_Vertical_PositiveNarrows()
    {
        (PdfDocument, DisplayList) Page(string tw) => InterpreterTests.Page($"BT /W 10 Tf {tw} 100 150 Td <2041> Tj ET", pdf =>
            TestPdf.Dict(("Font", TestPdf.Dict(("W", Type0Iso01(pdf, CMapIso01(pdf, "<00> <FF>", 1, [("<20>", GidSpaceIso01), ("<41>", GidIIso01)], 1)))))));
        var (plainDoc, plain) = Page("");
        var (doc, list) = Page("3 Tw");
        using (plainDoc)
        using (doc)
        {
            var p = OriginsIso01(plain)[^1];
            var o = OriginsIso01(list)[^1];
            Assert.Equal(p.Y + 3, o.Y, 3);
            Assert.Equal(p.X, o.X, 3);
        }
    }

    /// <summary>
    /// 9.3.3 sentences 6, 7: Tw applies to the single-byte code 32 of simple fonts (Type 3 as well) and of composite fonts whose CMap reads 32 as
    /// one byte, never to a byte 32 inside a multi-byte code — Identity-H with &lt;0020&gt;: no word spacing; a CMap with the one-byte range
    /// &lt;00&gt;–&lt;7F&gt; and the two-byte range &lt;8140&gt;–&lt;9FFC&gt; with &lt;20&gt;: 5; a Type 3 font with (a a): 5.
    /// </summary>
    [Fact]
    public void Iso01_WordSpacing_OnlyForTheSingleByteCode32()
    {
        double LastX(string content, Func<TestPdf, PdfDictionary> resources)
        {
            var (doc, list) = InterpreterTests.Page(content, resources);
            using (doc) return OriginsIso01(list)[^1].X;
        }
        var i = HexIso01(GidIIso01);
        Func<TestPdf, PdfDictionary> identity = pdf => TestPdf.Dict(("Font", TestPdf.Dict(("H", Type0Iso01(pdf, TestPdf.N("Identity-H"))))));
        Assert.Equal(LastX($"BT /H 10 Tf 0 100 Td <0020{i}> Tj ET", identity), LastX($"BT /H 10 Tf 5 Tw 0 100 Td <0020{i}> Tj ET", identity), 3);

        Func<TestPdf, PdfDictionary> mixed = pdf => TestPdf.Dict(("Font", TestPdf.Dict(("M",
            Type0Iso01(pdf, CMapIso01(pdf, "<00> <7F> <8140> <9FFC>", 2, [("<20>", GidSpaceIso01), ("<41>", GidIIso01)], 0))))));
        Assert.Equal(LastX("BT /M 10 Tf 0 100 Td <2041> Tj ET", mixed) + 5, LastX("BT /M 10 Tf 5 Tw 0 100 Td <2041> Tj ET", mixed), 3);

        Func<TestPdf, PdfDictionary> type3 = pdf => TestPdf.Dict(("Font", TestPdf.Dict(("T", Type3Iso01(pdf, 32, [250, .. new double[64], 1000],
            [(32, "space"), (97, "a")], [("space", "250 0 d0"), ("a", "1000 0 0 0 1000 1000 d1 0 0 1000 1000 re f")])))));
        Assert.Equal(LastX("BT /T 10 Tf 0 100 Td (a a) Tj ET", type3) + 5, LastX("BT /T 10 Tf 5 Tw 0 100 Td (a a) Tj ET", type3), 3);
    }

    /// <summary>
    /// 9.3.4 sentences 1–5: Th stretches the glyphs along the horizontal axis of text space whatever the writing mode, shape and horizontal
    /// displacement — at 200 Tz the I is twice as wide and the next character stands at 2 · 27.8; with Identity-V twice as wide while the
    /// vertical displacement stays.
    /// </summary>
    [Fact]
    public void Iso01_HorizontalScaling_StretchesShapeAndHorizontalDisplacement()
    {
        static double Width(DisplayList list) => list.Items.Span[0].Right - list.Items.Span[0].Left;
        var (plainDoc, plain) = PageIso01("BT /F1 100 Tf 0 0 Td (II) Tj ET");
        var (doc, list) = PageIso01("BT /F1 100 Tf 200 Tz 0 0 Td (II) Tj ET");
        using (plainDoc)
        using (doc)
        {
            Assert.Equal(2 * Width(plain), Width(list), 1);
            Assert.Equal(2 * 27.8, list.Text[1].X0, 1);
        }
        var g = HexIso01(GidIIso01);
        var (vPlainDoc, vPlain) = PageIso01($"BT /V 50 Tf 100 150 Td <{g}{g}> Tj ET");
        var (vDoc, v) = PageIso01($"BT /V 50 Tf 200 Tz 100 150 Td <{g}{g}> Tj ET");
        using (vPlainDoc)
        using (vDoc)
        {
            Assert.Equal(2 * Width(vPlain), Width(v), 1);
            var p = OriginsIso01(vPlain);
            var o = OriginsIso01(v);
            Assert.Equal(p[0].Y - p[1].Y, o[0].Y - o[1].Y, 3);
        }
    }

    /// <summary>
    /// 9.3.4 sentence 6 (and 9.4.4 sentences 13–16 with Th ≠ 1): in horizontal writing Th scales Tc, Tw and the numbers of TJ as well — at
    /// 50 Tz, [(A) -1000 (B)] puts the B at (6.67 + 10) · 0.5; with 4 Tw and (A B) the B stands at (6.67 + 2.78 + 4) · 0.5.
    /// </summary>
    [Fact]
    public void Iso01_HorizontalScaling_AppliesToTJNumbersAndWordSpacing()
    {
        var (doc, list) = PageIso01("BT /F1 10 Tf 50 Tz 0 0 Td [(A) -1000 (B)] TJ ET");
        using (doc) Assert.Equal((6.67 + 10) * 0.5, list.Text[1].X0, 2);
        var (wDoc, w) = PageIso01("BT /F1 10 Tf 50 Tz 4 Tw 0 0 Td (A B) Tj ET");
        using (wDoc) Assert.Equal((6.67 + 2.78 + 4) * 0.5, w.Text[2].X0, 2);
    }

    /// <summary>
    /// 9.3.5 sentences 2–5: TL is the vertical distance between baselines, used by T* and ', on the vertical axis of text space whatever the
    /// writing mode — B on y 86, C on y 72; with an Identity-V font T* moves 14 down to the start of the line as well.
    /// </summary>
    [Fact]
    public void Iso01_Leading_UsedByTStarAndQuote_AlsoVertically()
    {
        var (doc, list) = PageIso01("BT /F1 10 Tf 14 TL 20 100 Td (A) Tj T* (B) Tj (C) ' ET");
        using (doc)
        {
            var o = OriginsIso01(list);
            Assert.Equal(86, o[1].Y, 3);
            Assert.Equal(72, o[2].Y, 3);
            Assert.Equal(20, o[2].X, 3);
        }
        var g = HexIso01(GidIIso01);
        var (vDoc, v) = PageIso01($"BT /V 10 Tf 14 TL 100 150 Td <{g}> Tj T* <{g}> Tj ET");
        using (vDoc)
        {
            var o = OriginsIso01(v);
            Assert.Equal(o[0].X, o[1].X, 3);
            Assert.Equal(14, o[0].Y - o[1].Y, 3);
        }
    }

    /// <summary>Measures the run of dark pixels starting at (x, y) along a direction (device pixels).</summary>
    private static int DarkRunIso01(SKBitmap b, int x, int y, int dx, int dy)
    {
        var n = 0;
        while (x >= 0 && y >= 0 && x < b.Width && y < b.Height && b.GetPixel(x, y).Red < 128)
        {
            n++;
            x += dx;
            y += dy;
        }
        return n;
    }

    /// <summary>
    /// 9.3.6 sentence 3: the parameters of stroking (the line width) apply in user space — a stroked I at 300 Tz with 4 w: its vertical and its
    /// horizontal edges are both 4 wide (measured at twice the resolution: 8 pixels).
    /// </summary>
    [Fact]
    public void Iso01_StrokedText_LineWidthInUserSpace()
    {
        var (doc, list) = PageIso01("BT /F1 100 Tf 300 Tz 1 Tr 4 w 10 10 Td (I) Tj ET");
        using (doc)
        {
            var item = list.Items.Span[0];
            using var bitmap = RenderIso01(doc, list, 2);
            // bounds include the stroke (inflated by the width): the outline lies 4 inside
            var midY = bitmap.Height - 1 - (int)((item.Top + item.Bottom) / 2 * 2);
            var midX = (int)((item.Left + item.Right) / 2 * 2);
            var left = 0;
            for (var x = 0; x < bitmap.Width && left == 0; x++) left = bitmap.GetPixel(x, midY).Red < 128 ? x : 0;
            var top = 0;
            for (var y = 0; y < bitmap.Height && top == 0; y++) top = bitmap.GetPixel(midX, y).Red < 128 ? y : 0;
            var vertical = DarkRunIso01(bitmap, left, midY, 1, 0);
            var horizontal = DarkRunIso01(bitmap, midX, top, 0, 1);
            Assert.True(vertical is >= 6 and <= 10 && horizontal is >= 6 and <= 10 && Math.Abs(vertical - horizontal) <= 1,
                $"senkrechte Kante {vertical} px, waagerechte Kante {horizontal} px (erwartet je 8)");
        }
    }

    /// <summary>
    /// 9.3.6 sentences 8–10: filling and stroking are done glyph by glyph, overlapping glyphs one after the other like stacked opaque marks —
    /// two filled and stroked I (blue fill, red stroke of 6) overlapping by part of the stem: the right edge of the first lies under the blue
    /// fill of the second, the right edge of the second stays red. (The draft's [(l) 222 (l)] puts the second glyph exactly on the first, where
    /// the order cannot be seen; the shift here is 40/1000 em.)
    /// </summary>
    [Fact]
    public void Iso01_FillAndStroke_GlyphByGlyph()
    {
        var (doc, list) = PageIso01("0 0 1 rg 1 0 0 RG 6 w BT /F1 150 Tf 2 Tr 20 40 Td [(I) 238 (I)] TJ ET");
        using (doc)
        {
            var items = list.Items.ToArray();
            Assert.Equal(2, items.Length);
            Assert.All(items, i => Assert.Equal(DisplayItemKind.FillStroke, i.Kind));
            // the bounds are inflated by the line width (6): the outline's right edge lies 6 inside
            var firstRight = items[0].Right - 6;
            var secondRight = items[1].Right - 6;
            var midY = (items[0].Top + items[0].Bottom) / 2;
            using var bitmap = RenderIso01(doc, list);
            ColorIso01(bitmap, firstRight, midY, SKColors.Blue);
            ColorIso01(bitmap, secondRight, midY, SKColors.Red);
        }
    }

    /// <summary>
    /// 9.3.6 sentence 11 (→ M8; today's behaviour): filled and stroked text with /CA 0.5 and /ca 0.5 — where the stroke lies over the fill it
    /// covers once, as the stroke over the backdrop (as one knockout element), not the stroke over the fill.
    /// </summary>
    [Fact]
    public void Iso01_FillAndStrokeWithAlpha_StrokeCoversOnce()
    {
        var gs = TestPdf.Dict(("Type", TestPdf.N("ExtGState")), ("CA", new PdfReal(0.5)), ("ca", new PdfReal(0.5)));
        var (doc, list) = PageIso01("/G gs 0 0 1 rg 1 0 0 RG 6 w BT /F1 150 Tf 2 Tr 20 40 Td (I) Tj ET", extGState: gs);
        using (doc)
        {
            var item = list.Items.Span[0];
            var left = item.Left + 6;                       // outline's left edge
            var midY = (item.Top + item.Bottom) / 2;
            using var bitmap = RenderIso01(doc, list);
            var inner = PixelIso01(bitmap, left + 1.5, midY);   // inner half of the stroke, over the fill
            var outer = PixelIso01(bitmap, left - 1.5, midY);   // outer half, over white
            Assert.True(Math.Abs(inner.Red - outer.Red) < 20 && Math.Abs(inner.Blue - outer.Blue) < 20,
                $"innen {inner}, außen {outer}: der Strich deckt über der Füllung doppelt");
        }
    }

    /// <summary>
    /// 9.3.6 sentences 22–25 (Table 104, modes 4, 5, 6): fill, stroke, or fill and stroke, and add to the clip — the glyph entry is of that
    /// kind, and the red filled afterwards shows only in the stem of the I.
    /// </summary>
    [Theory]
    [InlineData(4, DisplayItemKind.Fill)]
    [InlineData(5, DisplayItemKind.Stroke)]
    [InlineData(6, DisplayItemKind.FillStroke)]
    public void Iso01_ClipModes_PaintAndClip(int mode, DisplayItemKind kind)
    {
        var (doc, list) = PageIso01($"BT {mode} Tr /F1 150 Tf 80 20 Td (I) Tj ET 1 0 0 rg 0 0 200 200 re f");
        using (doc)
        {
            Assert.Equal(kind, list.Items.Span[0].Kind);
            using var bitmap = RenderIso01(doc, list);
            ColorIso01(bitmap, 80 + 0.278 * 150 / 2, 80, SKColors.Red);
            ColorIso01(bitmap, 20, 80, SKColors.White);
        }
    }

    /// <summary>
    /// 9.3.6 sentence 27: at ET the collected outlines become one path of subpaths, clipped by the non-zero rule — two overlapping O in mode 7:
    /// where the rings of both cross (winding number 2) the red filled afterwards shows; even-odd would leave it white.
    /// </summary>
    [Fact]
    public void Iso01_TextClip_NonZeroRule()
    {
        const string fill = " 1 0 0 rg 0 0 200 200 re f";
        SKBitmap Render(string text)
        {
            var (doc, list) = PageIso01("BT 7 Tr /F1 150 Tf 20 20 Td " + text + " ET" + fill);
            using (doc) return RenderIso01(doc, list);
        }
        static bool Red(SKColor c) => c.Red > 200 && c.Green < 60 && c.Blue < 60;
        using var both = Render("[(O) 300 (O)] TJ");
        using var first = Render("(O) Tj");
        using var second = Render("[-478 (O)] TJ");   // the second O alone: shifted by 778 − 300
        var crossings = 0;
        for (var y = 0; y < both.Height; y++)
            for (var x = 0; x < both.Width; x++)
                if (Red(first.GetPixel(x, y)) && Red(second.GetPixel(x, y)))
                {
                    crossings++;
                    Assert.True(Red(both.GetPixel(x, y)), $"bei ({x}, {y}) nicht rot: {both.GetPixel(x, y)}");
                }
        Assert.True(crossings > 20, $"nur {crossings} Pixel, in denen sich die Ringe kreuzen");
    }

    /// <summary>
    /// 9.3.6 sentence 35: in modes 3 and 7 the text matrix advances glyph by glyph as in the other modes — the B after (AAAA) in mode 3 or 7
    /// stands where it stands with everything in mode 0.
    /// </summary>
    [Fact]
    public void Iso01_InvisibleModes_AdvanceTheTextMatrix()
    {
        var (plainDoc, plain) = PageIso01("BT /F1 20 Tf 20 100 Td (AAAA) Tj (B) Tj ET");
        using (plainDoc)
            foreach (var mode in (ReadOnlySpan<int>)[3, 7])
            {
                var (doc, list) = PageIso01($"BT /F1 20 Tf 20 100 Td {mode} Tr (AAAA) Tj 0 Tr (B) Tj ET");
                using (doc) Assert.Equal(plain.Text[4].X0, list.Text[4].X0, 3);
            }
    }

    /// <summary>
    /// 9.3.7 sentences 1, 2, 4: Trise moves the baseline by unscaled units, positive upwards, on the vertical axis of text space whatever the
    /// writing mode — 3 Ts puts it on y 103, −3 Ts on y 97; an Identity-V glyph moves 3 up as well, not sideways.
    /// </summary>
    [Fact]
    public void Iso01_Rise_MovesTheBaselineVertically()
    {
        foreach (var (rise, y) in (ReadOnlySpan<(int, double)>)[(3, 103), (-3, 97)])
        {
            var (doc, list) = PageIso01($"BT /F1 10 Tf 0 100 Td {rise} Ts (A) Tj ET");
            using (doc) Assert.Equal(y, OriginsIso01(list)[0].Y, 3);
        }
        var g = HexIso01(GidIIso01);
        var (plainDoc, plain) = PageIso01($"BT /V 10 Tf 100 150 Td <{g}> Tj ET");
        var (vDoc, v) = PageIso01($"BT /V 10 Tf 3 Ts 100 150 Td <{g}> Tj ET");
        using (plainDoc)
        using (vDoc)
        {
            Assert.Equal(OriginsIso01(plain)[0].Y + 3, OriginsIso01(v)[0].Y, 3);
            Assert.Equal(OriginsIso01(plain)[0].X, OriginsIso01(v)[0].X, 3);
        }
    }

    /// <summary>
    /// 9.3.8 sentences 12–16: graphics and transparency parameters set inside a text object are not saved or reset by BT/ET — a gs with
    /// /ca 0.5 and red set inside BT … ET still hold for the rectangle after ET.
    /// </summary>
    [Fact]
    public void Iso01_StateChangedInsideTextObject_HoldsAfterET()
    {
        var gs = TestPdf.Dict(("Type", TestPdf.N("ExtGState")), ("ca", new PdfReal(0.5)));
        var (doc, list) = PageIso01("BT /G gs 1 0 0 rg ET 0 0 50 50 re f", extGState: gs);
        using (doc)
        {
            var fill = list.Paints[Assert.Single(list.Items.ToArray()).Paint].Fill;
            Assert.Equal(0xFF0000u, fill & 0xFFFFFF);
            Assert.InRange(fill >> 24, 126u, 129u);
        }
    }

    // ------------------------------------------------------------ 9.4 text objects

    /// <summary>
    /// 9.4.1 sentences 1–4, 7–10: BT sets Tm and Tlm to the identity, nothing of the text matrix passes into the next text object — after
    /// „BT … 100 100 Td ET“ the A of the next object stands at the origin.
    /// </summary>
    [Fact]
    public void Iso01_BT_StartsWithTheIdentityMatrix()
    {
        var (doc, list) = PageIso01("BT /F1 10 Tf 100 100 Td ET BT (A) Tj ET");
        using (doc)
        {
            var o = OriginsIso01(list)[0];
            Assert.Equal(0, o.X, 3);
            Assert.Equal(0, o.Y, 3);
        }
    }

    /// <summary>
    /// 9.4.1 sentences 11–14 (the reader's side, tolerant): a BT inside a text object resets the matrices, the clip collected so far stays and
    /// is applied once at ET — the red after it shows only in the stem of the I, under one clip.
    /// </summary>
    [Fact]
    public void Iso01_NestedBT_ClipsOnceAtET()
    {
        var (doc, list) = PageIso01("BT 7 Tr /F1 150 Tf 80 20 Td (I) Tj BT 0 0 Td ET 1 0 0 rg 0 0 200 200 re f");
        using (doc)
        {
            var fill = list.Items.Span[^1];
            Assert.NotEqual(0, fill.Clip);
            Assert.Equal(0, list.Clips[fill.Clip].Parent);
            using var bitmap = RenderIso01(doc, list);
            ColorIso01(bitmap, 80 + 0.278 * 150 / 2, 80, SKColors.Red);
            ColorIso01(bitmap, 20, 80, SKColors.White);
        }
    }

    /// <summary>
    /// 9.4.1 sentences 19–25 (the reader's side): q/Q and BT/ET nest each for themselves — in „BT q … Tf … Td (A) Tj Q (B) Tj ET“ the B follows
    /// the A (the text matrix is no part of the graphics state) and is set with the font of before q (none: the substitute in size 1).
    /// </summary>
    [Fact]
    public void Iso01_QInsideTextObject_RestoresFontButNotTextMatrix()
    {
        var (doc, list) = PageIso01("BT q /F1 10 Tf 10 10 Td (A) Tj Q (B) Tj ET");
        using (doc)
        {
            Assert.Equal("AB", TextIso01(list));
            Assert.Equal(10 + 6.67, list.Text[1].X0, 2);
            Assert.Equal(1, list.Text[1].Size, 3);
        }
    }

    /// <summary>
    /// 9.4.1 sentences 19–25 (the writer's side): reducing a nesting of more than 28 levels, a pair of q/Q that a text object crosses (BT inside,
    /// ET after the Q) stays where it is and does not move into a form; a pair holding a whole text object does.
    /// </summary>
    [Fact]
    public void Iso01_ContentNesting_PairCrossingATextObjectStays()
    {
        static string Deep(string inner)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < ContentNesting.MaxDepth; i++) sb.Append("q 1 w ");
            sb.Append(inner);
            for (var i = 0; i < ContentNesting.MaxDepth; i++) sb.Append(" Q");
            return sb.ToString();
        }
        var crossing = Encoding.ASCII.GetBytes(Deep("q 2 w BT /F1 1 Tf (A) Tj Q ET"));
        var nested = 0;
        var reduced = ContentNesting.Reduce(crossing, _ => { nested++; return PdfName.Get("Fm" + nested); });
        Assert.Equal(0, nested);
        Assert.Equal(ContentNesting.MaxDepth + 1, ContentNesting.Depth(reduced));

        var whole = Encoding.ASCII.GetBytes(Deep("q 2 w BT /F1 1 Tf (A) Tj ET Q"));
        nested = 0;
        reduced = ContentNesting.Reduce(whole, _ => { nested++; return PdfName.Get("Fm" + nested); });
        Assert.Equal(1, nested);
        Assert.Equal(ContentNesting.MaxDepth, ContentNesting.Depth(reduced));
    }

    /// <summary>
    /// What PDF-Knead writes (a text element fixed in the page, a text field, a code with caption, the text layer of the text recognition,
    /// a redacted page): every content stream of the saved file, page content, forms and appearance streams.
    /// </summary>
    private static List<(string Where, byte[] Data)> WrittenContentsIso01()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        var pages = PageItem.From(original);
        TextElement Text(string text, double left, double bottom) =>
            new() { Rect = new PdfRectangle(left, bottom, left + 300, bottom + 60), Text = text, FontSize = 14 };
        pages[0] = pages[0].WithElements([
            Text("Fester Text", 100, 600),
            Text("Feldwert", 100, 500) with { IsField = true, FieldName = "Feld1" },
            new CodeElement { Rect = new PdfRectangle(100, 300, 250, 380), Format = Core.Barcodes.BarcodeFormat.Ean13, Text = "4006381333931" },
            new RedactionElement { Rect = new PdfRectangle(70, 755, 110, 790) },
        ]);
        static OcrWord Word(string text, double x, double y, double w, double h) =>
            new(text, [new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)]);
        var layer = OcrTextLayer.Form([new OcrLine([Word("Erkannt", 100, 100, 80, 20), Word("Wort", 190, 100, 50, 20)], 0.95)],
            new RectD(0, 0, 595.28, 841.89))!;
        using var output = new MemoryStream();
        PdfWriter.Write(original, pages, output, new PdfSaveOptions { TextLayers = new Dictionary<int, PdfStream> { [0] = layer } });
        using var doc = PdfDocument.Open(output.ToArray());

        var result = new List<(string, byte[])>();
        var seen = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        void Forms(PdfDictionary? resources, string where)
        {
            if (resources?.GetDictionary(PdfName.Get("XObject")) is not { } xobjects) return;
            foreach (var (key, _) in xobjects.Entries)
                if (xobjects.Get(key) is PdfStream form && form.Dictionary.GetName(PdfName.Subtype)?.Value == "Form" && seen.Add(form))
                {
                    result.Add(($"{where} /{key.Value}", doc.GetStreamData(form)));
                    Forms(form.Dictionary.GetDictionary(PdfName.Resources), $"{where} /{key.Value}");
                }
        }
        var page = doc.Pages[0];
        using (var content = PageInterpreter.PageContent(doc, page.Dictionary)) result.Add(("Seite", content.Span.ToArray()));
        Forms(page.Resources, "Seite");
        if (page.Dictionary.GetArray(PdfName.Get("Annots")) is { } annots)
            for (var i = 0; i < annots.Count; i++)
                if (annots.Get(i) is PdfDictionary annot && annot.GetDictionary(PdfName.Get("AP"))?.Get(PdfName.Get("N")) is PdfStream ap && seen.Add(ap))
                {
                    result.Add(($"Anmerkung {i}", doc.GetStreamData(ap)));
                    Forms(ap.Dictionary.GetDictionary(PdfName.Resources), $"Anmerkung {i}");
                }
        return result;
    }

    /// <summary>
    /// 9.3.1 sentence 36, 9.4.1 sentences 11–14, 9.4.2 sentence 5, 9.4.3 sentence 3 (the writer's side): in everything PDF-Knead writes, no BT
    /// stands inside BT … ET, positioning operators (Td, TD, Tm, T*) and showing operators (Tj, TJ, ', ") stand only between BT and ET, and a
    /// Tf comes before every showing operator.
    /// </summary>
    [Fact]
    public void Iso01_WrittenContent_TextObjectsWellFormed()
    {
        var contents = WrittenContentsIso01();
        Assert.Contains(contents, c => Encoding.Latin1.GetString(c.Data).Contains("BT"));
        Assert.True(contents.Count(c => Encoding.Latin1.GetString(c.Data).Contains("Tj") || Encoding.Latin1.GetString(c.Data).Contains("TJ")) >= 3,
            string.Join(", ", contents.Select(c => c.Where)));
        foreach (var (where, data) in contents)
        {
            var parser = new ContentParser(data);
            var inText = false;
            var font = new Stack<bool>();
            var hasFont = false;
            string? op;
            while ((op = parser.Next()) is not null)
                switch (op)
                {
                    case "BT":
                        Assert.False(inText, $"{where}: BT in BT … ET");
                        inText = true;
                        break;
                    case "ET":
                        Assert.True(inText, $"{where}: ET ohne BT");
                        inText = false;
                        break;
                    case "q":
                        font.Push(hasFont);
                        break;
                    case "Q":
                        if (font.Count > 0) hasFont = font.Pop();
                        break;
                    case "Tf":
                        hasFont = true;
                        break;
                    case "Td" or "TD" or "Tm" or "T*":
                        Assert.True(inText, $"{where}: {op} außerhalb eines Textobjekts");
                        break;
                    case "Tj" or "TJ" or "'" or "\"":
                        Assert.True(inText, $"{where}: {op} außerhalb eines Textobjekts");
                        Assert.True(hasFont, $"{where}: {op} ohne vorheriges Tf");
                        break;
                }
            Assert.False(inText, $"{where}: Textobjekt nicht beendet");
        }
    }

    /// <summary>
    /// 9.4.2 sentence 4: a rotated text space turns the glyphs with it — with Tm [0 1 −1 0 100 100] the B stands above the A at y 106.67 and the
    /// bottom edge of the A runs upwards.
    /// </summary>
    [Fact]
    public void Iso01_RotatedTextSpace_TurnsTheGlyphs()
    {
        var (doc, list) = PageIso01("BT /F1 10 Tf 0 1 -1 0 100 100 Tm (AB) Tj ET");
        using (doc)
        {
            var o = OriginsIso01(list);
            Assert.Equal(100, o[0].X, 3);
            Assert.Equal(100, o[0].Y, 3);
            Assert.Equal(100, o[1].X, 3);
            Assert.Equal(106.67, o[1].Y, 2);
            var a = list.Text[0];
            Assert.Equal(a.X0, a.X1, 3);
            Assert.Equal(6.67, a.Y1 - a.Y0, 2);
        }
    }

    /// <summary>
    /// 9.4.2 sentence 5 (the reader's side, tolerant): a Td before BT is undone by BT — „10 10 Td BT (A) Tj ET“ shows the A at the origin.
    /// </summary>
    [Fact]
    public void Iso01_TdOutsideTextObject_UndoneByBT()
    {
        var (doc, list) = PageIso01("10 10 Td BT (A) Tj ET");
        using (doc)
        {
            Assert.Equal(0, list.Text[0].X0, 3);
            Assert.InRange(list.Text[0].Y0, -0.3, 0);
        }
    }

    /// <summary>
    /// 9.4.2 sentences 6–10: Td moves to the start of the next line, offset from the start of the current line — after (AAAA), „0 −20 Td“
    /// puts the B at x 20, y 80, not after the A's.
    /// </summary>
    [Fact]
    public void Iso01_Td_FromTheStartOfTheLine()
    {
        var (doc, list) = PageIso01("BT /F1 10 Tf 20 100 Td (AAAA) Tj 0 -20 Td (B) Tj ET");
        using (doc)
        {
            var b = OriginsIso01(list)[4];
            Assert.Equal(20, b.X, 3);
            Assert.Equal(80, b.Y, 3);
        }
    }

    /// <summary>9.4.2 sentences 11–19: TD moves like Td and sets the leading to −ty — after „0 −14 TD“ the A stands on y 86, the B after T* on y 72.</summary>
    [Fact]
    public void Iso01_TD_SetsTheLeading()
    {
        var (doc, list) = PageIso01("BT /F1 10 Tf 20 100 Td 0 -14 TD (A) Tj T* (B) Tj ET");
        using (doc)
        {
            var o = OriginsIso01(list);
            Assert.Equal(86, o[0].Y, 3);
            Assert.Equal(72, o[1].Y, 3);
            Assert.Equal(20, o[1].X, 3);
        }
    }

    /// <summary>9.4.2 sentences 22, 23, 27–30: T* moves to the start of the next line like „0 −Tl TD“ — at 12 TL the B stands at x 20, y 88.</summary>
    [Fact]
    public void Iso01_TStar_MovesToTheNextLine()
    {
        var (doc, list) = PageIso01("BT /F1 10 Tf 12 TL 20 100 Td (A) Tj T* (B) Tj ET");
        using (doc)
        {
            var b = OriginsIso01(list)[1];
            Assert.Equal(20, b.X, 3);
            Assert.Equal(88, b.Y, 3);
        }
    }

    /// <summary>9.4.2 sentence 31: at the start of a text object text space has the origin of user space — after „1 0 0 1 50 50 cm“ the A stands at (50, 50).</summary>
    [Fact]
    public void Iso01_TextObjectStartsAtTheUserSpaceOrigin()
    {
        var (doc, list) = PageIso01("1 0 0 1 50 50 cm BT /F1 10 Tf (A) Tj ET");
        using (doc)
        {
            var a = OriginsIso01(list)[0];
            Assert.Equal(50, a.X, 3);
            Assert.Equal(50, a.Y, 3);
        }
    }

    /// <summary>
    /// 9.4.2 sentences 34–36: the line matrix Tlm keeps the start of the line — after (AAAA) and „14 TL T*“ the B stands at x 20, the start of
    /// the line.
    /// </summary>
    [Fact]
    public void Iso01_LineMatrix_KeepsTheStartOfTheLine()
    {
        var (doc, list) = PageIso01("BT /F1 10 Tf 20 100 Td (AAAA) Tj 14 TL T* (B) Tj ET");
        using (doc)
        {
            var b = OriginsIso01(list)[4];
            Assert.Equal(20, b.X, 3);
            Assert.Equal(86, b.Y, 3);
        }
    }

    /// <summary>
    /// 9.4.3 sentence 3 (the reader's side, tolerant): a Tj after ET shows with the text matrix where it stood — the A at (50, 50).
    /// </summary>
    [Fact]
    public void Iso01_TjOutsideTextObject_ShownWhereTheTextMatrixStood()
    {
        var (doc, list) = PageIso01("BT /F1 10 Tf 50 50 Td ET (A) Tj");
        using (doc)
        {
            var a = OriginsIso01(list)[0];
            Assert.Equal(50, a.X, 3);
            Assert.Equal(50, a.Y, 3);
        }
    }

    /// <summary>9.4.3 sentences 7–11: ' moves to the next line and shows the string like „T* string Tj“ — at 12 TL the B stands at x 20, y 88.</summary>
    [Fact]
    public void Iso01_Quote_NextLineAndShow()
    {
        var (doc, list) = PageIso01("BT /F1 10 Tf 12 TL 20 100 Td (A) Tj (B) ' ET");
        using (doc)
        {
            var b = OriginsIso01(list)[1];
            Assert.Equal(20, b.X, 3);
            Assert.Equal(88, b.Y, 3);
        }
    }

    /// <summary>
    /// 9.4.3 sentences 12–19: " sets word and character spacing in the text state, moves to the next line and shows — line y 88, spacing with
    /// Tw 5 and Tc 2, and both still hold for the C after it.
    /// </summary>
    [Fact]
    public void Iso01_DoubleQuote_SetsSpacingMovesAndShows()
    {
        var (doc, list) = PageIso01("BT /F1 10 Tf 12 TL 20 100 Td 5 2 (A B) \" (C) Tj ET");
        using (doc)
        {
            Assert.Equal("A BC", TextIso01(list));
            Assert.Equal(88, OriginsIso01(list)[0].Y, 3);
            var space = 20 + 6.67 + 2;
            var b = space + 2.78 + 2 + 5;
            Assert.Equal(20, list.Text[0].X0, 2);
            Assert.Equal(space, list.Text[1].X0, 2);
            Assert.Equal(b, list.Text[2].X0, 2);
            Assert.Equal(b + 6.67 + 2, list.Text[3].X0, 2);
        }
    }

    /// <summary>
    /// 9.4.3 sentences 31–37 (Figure 61): a number in TJ is subtracted from the vertical coordinate in vertical writing — Identity-V at 10 Tf,
    /// [&lt;g&gt; 500 &lt;g&gt;]: the second glyph stands 5 lower than without the number.
    /// </summary>
    [Fact]
    public void Iso01_TJNumber_Vertical_MovesDown()
    {
        var g = HexIso01(GidIIso01);
        var (plainDoc, plain) = PageIso01($"BT /V 10 Tf 100 150 Td [<{g}> <{g}>] TJ ET");
        var (doc, list) = PageIso01($"BT /V 10 Tf 100 150 Td [<{g}> 500 <{g}>] TJ ET");
        using (plainDoc)
        using (doc)
        {
            Assert.Equal(OriginsIso01(plain)[1].Y - 5, OriginsIso01(list)[1].Y, 3);
            Assert.Equal(OriginsIso01(plain)[1].X, OriginsIso01(list)[1].X, 3);
        }
    }

    /// <summary>
    /// 9.4.3 sentences 48–50: strings of any length (also empty) and splitting them changes nothing — (AB) and (A) () (B) give the same
    /// characters at the same places.
    /// </summary>
    [Fact]
    public void Iso01_SplitStrings_ShowTheSame()
    {
        var (oneDoc, one) = PageIso01("BT /F1 10 Tf 0 0 Td (AB) Tj ET");
        var (splitDoc, split) = PageIso01("BT /F1 10 Tf 0 0 Td (A) Tj () Tj (B) Tj ET");
        using (oneDoc)
        using (splitDoc) Assert.Equal(one.Text, split.Text);
    }

    /// <summary>
    /// 9.4.4 sentences 7–12: Trm = [Tfs·Th 0 0 Tfs 0 Trise] × Tm × CTM — at „2 0 0 2 0 0 cm“, 10 Tf, 50 Tz, 5 Ts and Tm at (10, 10) the I has
    /// its origin at (20, 30), is 2 · 2.78 · 0.5 wide and set in size 2 · 10.
    /// </summary>
    [Fact]
    public void Iso01_TextRenderingMatrix_ComposedOfStateTmAndCtm()
    {
        var (doc, list) = PageIso01("2 0 0 2 0 0 cm BT /F1 10 Tf 50 Tz 5 Ts 1 0 0 1 10 10 Tm (I) Tj ET");
        using (doc)
        {
            var o = OriginsIso01(list)[0];
            Assert.Equal(20, o.X, 3);
            Assert.Equal(30, o.Y, 3);
            Assert.Equal(2 * 2.78 * 0.5, list.Text[0].X1 - list.Text[0].X0, 2);
            Assert.Equal(20, list.Text[0].Size, 3);
        }
    }
}
