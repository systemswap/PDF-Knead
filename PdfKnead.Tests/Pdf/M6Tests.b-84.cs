using System.Text;
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
/// Graphics state (ISO 32000-2, 8.4; Konformitätsabgleich M6, draft b-84): the initial state, the operators of the graphics state, line
/// width, caps, joins and dashes, and the entries of the extended graphics state. A page of 200 × 200 at 72 dpi, one pixel per unit.
/// </summary>
public sealed partial class M6NormTests
{
    /// <summary>A file of one page of 200 × 200 with the given content and resources; <paramref name="objects"/> are numbered from 5 on.</summary>
    private static byte[] B84_File(string content, string resources = "", params string[] objects) =>
        DocumentStructureNormTests.FileOf([
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << {resources} >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream", .. objects]);

    /// <summary>The page of <see cref="B84_File"/> rendered (at <paramref name="scale"/> pixels per unit; optionally without the thin-line enhancement).</summary>
    private static SKBitmap B84_Render(string content, string resources = "", string[]? objects = null, double scale = 1, bool overprint = false,
        bool enhanceThinLines = true)
    {
        using var doc = PdfDocument.Open(B84_File(content, resources, objects ?? []));
        var page = doc.Pages[0];
        var list = PageInterpreter.Build(doc, page);
        if (enhanceThinLines) return SkiaRenderer.RenderPage(list, doc, page, scale, new ImageCache(), overprintPreview: overprint);
        var (w, h) = PageGeometry.DeviceSize(page, scale);
        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        SkiaRenderer.RenderTile(canvas, list, doc, PageGeometry.PageToDevice(page, scale), new SKRectI(0, 0, w, h), new ImageCache(), enhanceThinLines: false,
            overprintPreview: overprint);
        return bitmap;
    }

    /// <summary>The display list of the page of <see cref="B84_File"/>; the caller disposes the document.</summary>
    private static (PdfDocument Doc, DisplayList List) B84_List(string content, string resources = "", params string[] objects)
    {
        var doc = PdfDocument.Open(B84_File(content, resources, objects));
        return (doc, PageInterpreter.Build(doc, doc.Pages[0]));
    }

    /// <summary>The style of entry <paramref name="index"/> of a display list.</summary>
    private static Paint B84_Paint(DisplayList list, int index) => list.Paints[list.Items.Span[index].Paint];

    /// <summary>The pixel covering [x, x + 1] × [y, y + 1] in page space (y upwards).</summary>
    private static SKColor B84_At(SKBitmap b, int x, int y) => b.GetPixel(x, b.Height - 1 - y);

    private static bool B84_Blue(SKBitmap b, int x, int y) => B84_At(b, x, y) is { Blue: > 200, Red: < 60, Green: < 60 };

    private static bool B84_White(SKBitmap b, int x, int y) => B84_At(b, x, y) is { Red: > 250, Green: > 250, Blue: > 250 };

    private static bool B84_Red(SKBitmap b, int x, int y) => B84_At(b, x, y) is { Red: > 200, Green: < 60, Blue: < 60 };

    /// <summary>A form XObject of 200 × 200 with the given content and further entries.</summary>
    internal static string B84_Form(string content, string entries = "") =>
        $"<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] {entries} /Length {content.Length} >>\nstream\n{content}\nendstream";

    /// <summary>Whether two bitmaps have the same pixels.</summary>
    private static bool B84_Same(SKBitmap a, SKBitmap b) => a.Width == b.Width && a.Height == b.Height && a.Bytes.AsSpan().SequenceEqual(b.Bytes);

    /// <summary>
    /// Whether the content with the extended graphics state /G (<paramref name="state"/>) before it renders exactly as without it.
    /// </summary>
    private static bool B84_StateChangesNothing(string content, string state, string resources = "", params string[] objects)
    {
        using var with = B84_Render("/G gs " + content, $"/ExtGState << /G << /Type /ExtGState {state} >> >> {resources}", objects);
        using var without = B84_Render(content, resources, objects);
        return B84_Same(with, without);
    }

    /// <summary>
    /// The places where a content stream breaks the order of path objects (8.2, Figure 9; 8.5.2.1; 8.5.3.1): „start:op“ — a path begins with
    /// another operator than m or re; „inpath:op“ — an operator other than path construction and W/W* between the start of a path and its
    /// painting operator; „nopath:op“ — a painting or clipping operator without a current path; „open“ — a path never painted.
    /// </summary>
    internal static List<string> B84_PathViolations(byte[] content)
    {
        var found = new List<string>();
        var parser = new ContentParser(content);
        var inPath = false;
        while (parser.Next() is { } op)
        {
            switch (op)
            {
                case "m" or "re":
                    inPath = true;
                    break;
                case "l" or "c" or "v" or "y" or "h":
                    if (!inPath) found.Add("start:" + op);
                    inPath = true;
                    break;
                case "W" or "W*":
                    if (!inPath) found.Add("nopath:" + op);
                    break;
                case "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "n":
                    if (!inPath) found.Add("nopath:" + op);
                    inPath = false;
                    break;
                default:
                    if (inPath) found.Add("inpath:" + op);
                    break;
            }
        }
        if (inPath) found.Add("open");
        return found;
    }

    /// <summary>Every content stream of a document: the contents of its pages and every form XObject in it (annotation appearances among them).</summary>
    internal static List<(string Name, byte[] Data, PdfDictionary? Resources)> B84_ContentStreams(PdfDocument doc)
    {
        var streams = new List<(string, byte[], PdfDictionary?)>();
        var pageContents = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        foreach (var page in doc.Pages)
        {
            var contents = page.Dictionary.Get(PdfName.Contents);
            if (contents is PdfStream single) pageContents.Add(single);
            else if (contents is PdfArray parts)
                for (var i = 0; i < parts.Count; i++)
                    if (parts.Get(i) is PdfStream part) pageContents.Add(part);
            foreach (var stream in pageContents.ToList())
                streams.Add(("Seite", doc.GetStreamData(stream), page.Resources));
            pageContents.Clear();
        }
        for (var n = 1; n <= doc.HighestObjectNumber; n++)
        {
            PdfObject obj;
            try
            {
                obj = doc.GetObject(n);
            }
            catch (Exception)
            {
                continue;
            }
            if (obj is PdfStream { } form && form.Dictionary.GetName(PdfName.Subtype)?.Value == "Form")
                streams.Add(($"Form {n}", doc.GetStreamData(form), form.Dictionary.GetDictionary(PdfName.Resources)));
        }
        return streams;
    }

    /// <summary>
    /// The program's own elements — shapes (filled, stroked, dashed, half transparent, ellipse, line, arrow, star), text, freehand, image, QR
    /// code, note and the mark of a redaction not yet applied — on a blank page, saved; with the appearances PDF-Knead builds for them and for
    /// annotations without an appearance (square, circle, line, polyline, polygon, ink, highlight, note, file attachment).
    /// </summary>
    internal static List<(string Name, byte[] Data, PdfDictionary? Resources)> B84_OwnStreams()
    {
        var pixels = new byte[4 * 4 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 200;
            pixels[i + 1] = pixels[i + 2] = 30;
            pixels[i + 3] = (byte)(i % 8 == 0 ? 255 : 128);
        }
        PageElement[] elements =
        [
            new ShapeElement { Rect = new PdfRectangle(50, 700, 150, 780), FillColor = 0xC00000 },
            new ShapeElement { Rect = new PdfRectangle(200, 700, 300, 780), Kind = ShapeKind.Ellipse, Dashed = true, Opacity = 0.5 },
            new ShapeElement { Rect = new PdfRectangle(350, 700, 450, 780), Kind = ShapeKind.Line },
            new ShapeElement { Rect = new PdfRectangle(50, 600, 150, 680), Kind = ShapeKind.Arrow, StrokeWidth = 4 },
            new ShapeElement { Rect = new PdfRectangle(200, 600, 300, 680), Kind = ShapeKind.Star, FillColor = 0x00A000 },
            new TextElement { Rect = new PdfRectangle(50, 500, 300, 530), Text = "Probetext" },
            new InkElement { Rect = new PdfRectangle(350, 500, 450, 560), Strokes = [[0, 0, 20, 30, 50, 10, 90, 50]], StrokeWidth = 2 },
            new ImageElement { Rect = new PdfRectangle(50, 400, 90, 440), Image = new Core.Imaging.RgbaImage(4, 4, pixels) },
            new CodeElement { Rect = new PdfRectangle(200, 380, 260, 440), Text = "Probe 17" },
            new NoteElement { Rect = new PdfRectangle(350, 400, 370, 420), Text = "Notiz" },
            new RedactionElement { Rect = new PdfRectangle(50, 300, 250, 330) },
        ];
        var streams = new List<(string, byte[], PdfDictionary?)>();
        foreach (var element in elements)
        {
            ElementAppearance appearance;
            try
            {
                appearance = ElementAppearance.Final(element);
            }
            catch (NotSupportedException)
            {
                continue;   // saved without an appearance of its own
            }
            streams.Add((element.GetType().Name, appearance.Form.RawData!, appearance.Form.Dictionary.GetDictionary(PdfName.Resources)));
        }
        var item = BlankPage.Create(595.28, 841.89);
        using (var output = new MemoryStream())
        {
            PdfWriter.Write(item.Source, [item.WithElements(elements)], output, new PdfSaveOptions { CompressStreams = false });
            using var saved = PdfDocument.Open(output.ToArray());
            streams.AddRange(B84_ContentStreams(saved));
        }
        var rect = new PdfRectangle(0, 0, 100, 50);
        foreach (var (kind, key, value) in new (string, string?, PdfObject?)[]
                 {
                     ("Square", null, null), ("Circle", null, null), ("Line", "L", TestPdf.Box(0, 0, 100, 50)),
                     ("PolyLine", "Vertices", TestPdf.Box(0, 0, 50, 50, 100, 0)), ("Polygon", "Vertices", TestPdf.Box(0, 0, 50, 50, 100, 0)),
                     ("Ink", "InkList", new PdfArray([TestPdf.Box(0, 0, 10, 10, 20, 0)])), ("Highlight", "QuadPoints", TestPdf.Box(0, 50, 100, 50, 0, 0, 100, 0)),
                     ("Text", null, null), ("FileAttachment", null, null),
                 })
        {
            var annot = TestPdf.Dict(("Subtype", TestPdf.N(kind)), ("C", TestPdf.Box(1, 0, 0)), ("IC", TestPdf.Box(0, 0, 1)), ("CA", new PdfReal(0.5)),
                ("BS", TestPdf.Dict(("W", PdfInteger.Of(2)), ("S", TestPdf.N("D")), ("D", TestPdf.Box(3, 2)))));
            if (key is not null) annot.Set(PdfName.Get(key), value!);
            if (AnnotationAppearance.Build(annot, rect) is { } form)
                streams.Add(("Anmerkung " + kind, form.RawData!, form.Dictionary.GetDictionary(PdfName.Resources)));
        }
        return streams;
    }

    /// <summary>The extended graphics state dictionaries in the resources of a content stream.</summary>
    private static IEnumerable<PdfDictionary> B84_States(PdfDictionary? resources)
    {
        if (resources?.GetDictionary(PdfName.Get("ExtGState")) is not { } states) yield break;
        foreach (var (name, _) in states.Entries)
            if (states.GetDictionary(name) is { } state) yield return state;
    }

    // ------------------------------------------------------------------ 8.4.1

    /// <summary>
    /// 8.4.1 sentences 4, 15, 19, 24, 31, 37, 42, 44, 58, 62, 67, 79, 83 (Tables 51 and 52): every page begins with the initial state —
    /// whatever the page before set without q (width, caps, joins, mitre limit, dash, colours, opacity, blend mode, overprint) does not carry
    /// over: on the second page a stroke is 1 wide, black, opaque, solid, with mitred joins (limit 10), a fill black; content beyond the
    /// MediaBox stays unseen even under a larger CropBox.
    /// </summary>
    [Fact]
    public void B84_InitialState_AtTheStartOfEveryPage()
    {
        const string first = "5 w 1 J 1 j 2 M [4 4] 0 d 1 0 0 RG 0.3 g /G gs 0 0 100 100 re B";
        const string second = "50 50 m 150 50 l 150 150 l S 10 10 20 20 re f";
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /ExtGState << /G << /CA 0.5 /ca 0.5 /BM /Multiply /OP true /OPM 1 >> >> >> /Contents 4 0 R >>",
            $"<< /Length {first.Length} >>\nstream\n{first}\nendstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /CropBox [-50 -50 250 250] /Contents 6 0 R >>",
            $"<< /Length {second.Length} >>\nstream\n{second}\nendstream");
        using var doc = PdfDocument.Open(file);
        var list = PageInterpreter.Build(doc, doc.Pages[1]);
        var stroke = B84_Paint(list, 0);
        Assert.Equal(DisplayItemKind.Stroke, list.Items.Span[0].Kind);
        Assert.Equal((1f, (byte)0, (byte)0, 10f, -1, (byte)0, (byte)0), (stroke.LineWidth, stroke.Cap, stroke.Join, stroke.MiterLimit, stroke.Dash, stroke.BlendMode, stroke.Overprint));
        Assert.Equal(0xFF000000u, stroke.Stroke);
        Assert.Equal(0xFF000000u, B84_Paint(list, 1).Fill);
        using var bitmap = SkiaRenderer.RenderPage(list, doc, doc.Pages[1], 1, new ImageCache());
        Assert.Equal((200, 200), (bitmap.Width, bitmap.Height));   // nothing of the CropBox beyond the MediaBox is shown
        Assert.True(B84_At(bitmap, 20, 20) is { Red: < 30, Green: < 30, Blue: < 30, Alpha: 255 });
    }

    /// <summary>
    /// 8.4.1 sentence 7: content meant to be device independent sets none of the device-dependent parameters of Table 52 — no element of the
    /// program, no appearance it builds, contains the operator i or an extended graphics state with /OP, /op, /OPM, /BG, /BG2, /UCR, /UCR2,
    /// /TR, /TR2, /HT, /FL or /SM.
    /// </summary>
    [Fact]
    public void B84_OwnContent_SetsNoDeviceDependentParameter()
    {
        string[] keys = ["OP", "op", "OPM", "BG", "BG2", "UCR", "UCR2", "TR", "TR2", "HT", "FL", "SM"];
        var found = new List<string>();
        foreach (var (name, data, resources) in B84_OwnStreams())
        {
            var parser = new ContentParser(data);
            while (parser.Next() is { } op)
                if (op == "i") found.Add($"{name}: i");
            foreach (var state in B84_States(resources))
                found.AddRange(keys.Where(k => state.Get(PdfName.Get(k)) is not (null or PdfNull)).Select(k => $"{name}: /{k}"));
        }
        Assert.Empty(found);
    }

    /// <summary>
    /// 8.4.1 sentences 18, 23, 65: colour space, colour and opacity exist twice, for strokes and for everything else — with a blue stroke, a
    /// red fill and /CA 0.5 the fill is opaque red and the border half transparent blue; „0.5 g“ afterwards changes only the fill.
    /// </summary>
    [Fact]
    public void B84_StrokeAndFill_HaveTheirOwnColourAndOpacity()
    {
        var (doc, list) = B84_List("0 0 1 RG 1 0 0 rg /G gs 20 w 20 20 160 160 re B 0.5 g 30 30 140 140 re B", "/ExtGState << /G << /CA 0.5 >> >>");
        using (doc)
        {
            var first = B84_Paint(list, 0);
            Assert.Equal(0xFFFF0000u, first.Fill);
            Assert.Equal(0x0000FFu, first.Stroke & 0xFFFFFF);
            Assert.InRange(first.Stroke >> 24, 120u, 135u);
            var second = B84_Paint(list, 1);
            Assert.Equal(first.Stroke, second.Stroke);
            Assert.NotEqual(first.Fill, second.Fill);
            Assert.Equal(255u, second.Fill >> 24);
        }
    }

    /// <summary>
    /// 8.4.1 sentences 55, 61: at the start of a transparency group the blend mode and the soft mask start afresh — (a) under /BM /Multiply a
    /// group painting red over grey 0.5 blends once (dark red near 128), not inside and again as a whole (near 64); (b) under a luminosity mask of
    /// grey 0.5 a group painting red shows at half strength (green near 128), not a quarter (near 191).
    /// </summary>
    [Fact]
    public void B84_TransparencyGroup_BlendModeAndMaskStartAfresh()
    {
        var group = B84_Form("1 0 0 rg 50 50 100 100 re f", "/Group << /S /Transparency >>");
        using (var multiplied = B84_Render("0.5 g 0 0 200 200 re f /M gs /Fm Do", "/ExtGState << /M << /BM /Multiply >> >> /XObject << /Fm 5 0 R >>", [group]))
        {
            var c = B84_At(multiplied, 100, 100);
            Assert.True(c.Red is > 100 and < 160 && c.Green < 30 && c.Blue < 30, $"einmal multipliziert erwartet: {c}");
        }
        var mask = B84_Form("0.5 g 0 0 200 200 re f", "/Group << /S /Transparency /CS /DeviceGray >>");
        using var masked = B84_Render("/S gs /Fm Do", "/ExtGState << /S << /SMask << /Type /Mask /S /Luminosity /G 6 0 R >> >> >> /XObject << /Fm 5 0 R >>", [group, mask]);
        var m = B84_At(masked, 100, 100);
        Assert.True(m.Red > 240 && m.Green is > 100 and < 160, $"halbe Deckung erwartet: {m}");
    }

    /// <summary>
    /// 8.4.1 sentences 48–52 and 8.4.5 sentences 79–81 (/SA), 8.4.1 sentences
    /// 84–87 and 8.4.5 sentences 47–48 (/BG), 8.4.5 sentences 49–52 (/BG2), 8.4.1 sentences 88–91 and 8.4.5 sentences 53–54 (/UCR), 8.4.5
    /// sentences 55–58 (/UCR2), 8.4.1 sentences 92–95 and 8.4.5 sentences 59–62 (/TR), 8.4.5 sentences 63–66 (/TR2), 8.4.1 sentences 96–99, 112
    /// and 8.4.5 sentences 67–68 (/HT), 8.4.5 sentences 69–74 (/FL), 8.4.1 sentences 105–109 and 8.4.5 sentences 75–78 (/SM), 8.4.5 sentences
    /// 126–131 (/HTO): today's behaviour, these entries go to M8 — the display does not evaluate them, the page renders exactly as without
    /// them (an inverting transfer function leaves black black).
    /// </summary>
    [Theory]
    [InlineData("/SA true", "0 0 1 RG 0.6 w 20 100 m 180 103 l S")]
    [InlineData("/BG << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [0] /N 1 >>", "0.2 0.6 0.4 rg 20 20 160 160 re f")]
    [InlineData("/BG << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [0] /N 1 >> /BG2 /Default", "0.2 0.6 0.4 rg 20 20 160 160 re f")]
    [InlineData("/UCR << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>", "0.2 0.6 0.4 rg 20 20 160 160 re f")]
    [InlineData("/UCR << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> /UCR2 << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>", "0.2 0.6 0.4 rg 20 20 160 160 re f")]
    [InlineData("/TR << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>", "0 g 20 20 160 160 re f")]
    [InlineData("/TR /Identity /TR2 << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>", "0 g 20 20 160 160 re f")]
    [InlineData("/HT /Default", "0.5 g 20 20 160 160 re f")]
    [InlineData("/HT << /Type /Halftone /HalftoneType 1 /Frequency 10 /Angle 45 /SpotFunction /Round >>", "0.5 g 20 20 160 160 re f")]
    [InlineData("/FL 100", "0 0 1 RG 3 w 20 20 m 20 180 180 180 180 20 c S")]
    [InlineData("/SM 1", "/Sh sh")]
    [InlineData("/SM 0.5", "/Sh sh")]
    [InlineData("/HTO [10 10]", "0.5 g 20 20 160 160 re f")]
    public void B84_DeviceDependentEntry_LeavesTheRenderingAsItIs(string state, string content) =>
        Assert.True(B84_StateChangesNothing(content, state,
            "/Shading << /Sh << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >> >>"));

    /// <summary>
    /// 8.4.1 sentences 68–71 and 8.4.5 sentences 104–111: /AIS (→ M8) — today's behaviour: not evaluated, „/AIS true /ca 0.5“ before a red fill
    /// renders exactly as „/ca 0.5“ alone, half transparent.
    /// </summary>
    [Fact]
    public void B84_AlphaIsShape_NotEvaluated()
    {
        const string fill = "/G gs 1 0 0 rg 20 20 160 160 re f";
        using var withAis = B84_Render(fill, "/ExtGState << /G << /AIS true /ca 0.5 >> >>");
        using var without = B84_Render(fill, "/ExtGState << /G << /ca 0.5 >> >>");
        Assert.True(B84_Same(withAis, without));
        Assert.InRange(B84_At(without, 100, 100).Green, (byte)100, (byte)160);
    }

    /// <summary>
    /// 8.4.1 sentences 114–116: values out of range are forced into it — „7 J“ gives cap 2, „-1 j“ join 0, „0.2 M“ the limit 1, /CA 3 an opaque
    /// stroke, „1.5 0 0 rg“ the colour (1, 0, 0). (A negative line width: GraphicsNormTests.NegativeLineWidth_IsTheThinnestLine.)
    /// </summary>
    [Fact]
    public void B84_ValuesOutOfRange_AreForcedIntoIt()
    {
        var (doc, list) = B84_List("7 J -1 j 0.2 M /G gs 1.5 0 0 rg 0 0 1 RG 20 20 100 100 re B", "/ExtGState << /G << /CA 3 >> >>");
        using (doc)
        {
            var paint = B84_Paint(list, 0);
            Assert.Equal(((byte)2, (byte)0, 1f), (paint.Cap, paint.Join, paint.MiterLimit));
            Assert.Equal(255u, paint.Stroke >> 24);
            Assert.Equal(0xFFFF0000u, paint.Fill);
        }
    }

    // ------------------------------------------------------------------ 8.4.3

    /// <summary>
    /// 8.4.3.2 sentence 5: under a scaling that differs in x and y the line width depends on the direction — with x scaled by 4 a line 5 wide
    /// is 5 pixels high when horizontal and 20 pixels wide when vertical.
    /// </summary>
    [Fact]
    public void B84_LineWidth_UnderUnequalScaling_DependsOnTheDirection()
    {
        using var b = B84_Render("4 0 0 1 0 0 cm 0 0 1 RG 5 w 10 40 m 40 40 l S 10 100 m 10 180 l S");
        Assert.True(B84_Blue(b, 100, 38) && B84_Blue(b, 100, 41));     // horizontal: y 37.5 … 42.5
        Assert.True(B84_White(b, 100, 43) && B84_White(b, 100, 36));
        Assert.True(B84_Blue(b, 30, 140) && B84_Blue(b, 49, 140));     // vertical: x 30 … 50
        Assert.True(B84_White(b, 28, 140) && B84_White(b, 51, 140));
    }

    /// <summary>
    /// 8.4.3.2 sentence 8: width 0 is the thinnest line the device can show — exactly one row of pixels, at 1 and at 4 pixels per unit, with and
    /// without the enhancement of thin lines. (At 4 pixels per unit the line lies on the middle of a row, y 100.125, not on a row boundary.)
    /// </summary>
    [Theory]
    [InlineData(1, "100.5", true)]
    [InlineData(1, "100.5", false)]
    [InlineData(4, "100.125", true)]
    [InlineData(4, "100.125", false)]
    public void B84_LineWidthZero_IsOneDevicePixel(double scale, string y, bool enhance)
    {
        using var b = B84_Render($"0 w 0 0 1 RG 0 {y} m 200 {y} l S", scale: scale, enhanceThinLines: enhance);
        var x = b.Width / 2;
        var rows = Enumerable.Range(0, b.Height).Where(r => b.GetPixel(x, r) is { Blue: > 200, Red: < 60 }).ToList();
        Assert.Single(rows);
        Assert.True(b.GetPixel(x, rows[0] - 2) is { Red: > 250 } && b.GetPixel(x, rows[0] + 2) is { Red: > 250 });
    }

    /// <summary>
    /// 8.4.3.2 sentences 9–10: lines of width 0 should not be used — a reasoned deviation (docs/normabgleich/sonderregeln.md S-B B56): the mark
    /// of a redaction not yet applied draws its hatching and border with „0 w“ (FG 191: a hairline at every zoom); a square annotation with
    /// /BS /W 0 gets an appearance without any stroke.
    /// </summary>
    [Fact]
    public void B84_WidthZero_OnlyInTheRedactionMark()
    {
        var mark = ElementAppearance.Final(new RedactionElement { Rect = new PdfRectangle(50, 300, 250, 330) });
        Assert.Contains(" 0 w", Encoding.ASCII.GetString(mark.Form.RawData!));
        var square = TestPdf.Dict(("Subtype", TestPdf.N("Square")), ("C", TestPdf.Box(1, 0, 0)), ("BS", TestPdf.Dict(("W", PdfInteger.Of(0)))));
        if (AnnotationAppearance.Build(square, new PdfRectangle(0, 0, 100, 50)) is not { } form) return;
        var parser = new ContentParser(form.RawData!);
        while (parser.Next() is { } op)
            Assert.DoesNotContain(op, (string[])["S", "s", "B", "B*", "b", "b*", "w"]);
    }

    /// <summary>
    /// 8.4.3.3 sentences 8–9: a round cap is a half circle with the line width as its diameter around the end point — 9 units before the end of
    /// a line 20 wide it is blue, where a square cap would reach (7 above and 9 before the end) and 12 before it white.
    /// </summary>
    [Fact]
    public void B84_RoundCap_IsAHalfCircleAroundTheEnd()
    {
        using var b = B84_Render("0 0 1 RG 20 w 1 J 50 100 m 150 100 l S");
        Assert.True(B84_Blue(b, 41, 100));
        Assert.True(B84_White(b, 41, 107));
        Assert.True(B84_White(b, 38, 100));
    }

    /// <summary>
    /// 8.4.3.4 sentence 3: joins only where consecutive segments of one subpath meet at an angle — the peak stroked as one path has the mitre
    /// above y 160; the same two segments stroked one after the other (each with its own S) meet at (100, 150) without a join.
    /// </summary>
    [Fact]
    public void B84_Joins_OnlyBetweenConsecutiveSegments()
    {
        using var apart = B84_Render("0 0 1 RG 20 w 0 j 50 50 m 100 150 l S 100 150 m 150 50 l S");
        using var joined = B84_Render("0 0 1 RG 20 w 0 j 50 50 m 100 150 l 150 50 l S");
        Assert.True(B84_Blue(joined, 100, 165));
        Assert.True(B84_White(apart, 100, 165));
    }

    /// <summary>
    /// 8.4.3.4 sentences 6–10: a round join is a circle with the line width as its diameter around the corner — 8 units above the corner of a
    /// peak (100, 150) it is blue, 12 above white (a mitre would reach up to y 172).
    /// </summary>
    [Fact]
    public void B84_RoundJoin_IsACircleAroundTheCorner()
    {
        using var b = B84_Render("0 0 1 RG 20 w 1 j 50 50 m 100 150 l 150 50 l S");
        Assert.True(B84_Blue(b, 100, 158));
        Assert.True(B84_White(b, 100, 162));
        using var mitre = B84_Render("0 0 1 RG 20 w 0 j 50 50 m 100 150 l 150 50 l S");
        Assert.True(B84_Blue(mitre, 100, 162));
    }

    /// <summary>
    /// 8.4.3.4 sentence 14: a closed dashed subpath whose first segment begins in a dash and whose last ends in one joins them — the square
    /// (perimeter 480) with [30 10] and phase 5 begins and ends inside a dash: its corner (40, 40) is a mitred corner, the outer square of the
    /// corner (x 35–40, y 35–40) is blue. (With phase 0 the last segment ends in a gap.)
    /// </summary>
    [Fact]
    public void B84_ClosedDashedSubpath_JoinsTheFirstAndTheLastDash()
    {
        using var b = B84_Render("0 0 1 RG 10 w 0 j [30 10] 5 d 40 40 m 160 40 l 160 160 l 40 160 l h S");
        Assert.True(B84_Blue(b, 36, 36));
        Assert.True(B84_Blue(b, 38, 38));
    }

    /// <summary>
    /// 8.4.3.6 sentence 4: the elements of a dash array are not negative and not all 0 — reading, a negative element counts as 0 ([0 10]: dots
    /// of length 0) and [0 0] as a solid line; writing, a square annotation with /BS /S /D /D [0 0] gets an appearance without „d“.
    /// </summary>
    [Fact]
    public void B84_DashArray_NegativeOrOnlyZeros()
    {
        var (doc, list) = B84_List("4 w [-5 10] 0 d 50 100 m 150 100 l S [0 0] 0 d 50 50 m 150 50 l S");
        using (doc)
        {
            var dash = list.Dashes[B84_Paint(list, 0).Dash];
            Assert.Equal(new[] { 0f, 10f }, dash.Lengths);
            Assert.Equal(0f, dash.Phase);
            Assert.Equal(-1, B84_Paint(list, 1).Dash);
        }
        var square = TestPdf.Dict(("Subtype", TestPdf.N("Square")), ("C", TestPdf.Box(1, 0, 0)),
            ("BS", TestPdf.Dict(("W", PdfInteger.Of(2)), ("S", TestPdf.N("D")), ("D", TestPdf.Box(0, 0)))));
        var form = AnnotationAppearance.Build(square, new PdfRectangle(0, 0, 100, 50));
        Assert.NotNull(form);
        var parser = new ContentParser(form!.RawData!);
        while (parser.Next() is { } op) Assert.NotEqual("d", op);
    }

    /// <summary>
    /// 8.4.3.6 sentences 5–9: the phase is the distance into the pattern at which the line begins; a negative one is increased by twice the sum
    /// until it is positive; the values are in user space — [10 10] with phase 5 gives a dash at x 50–55 and a gap at 55–65; phase −5 a gap
    /// at 50–55 and a dash at 55–65; [5 5] under a scaling by 2 dashes 10 pixels long.
    /// </summary>
    [Fact]
    public void B84_DashPhase_PositiveNegativeAndInUserSpace()
    {
        using (var b = B84_Render("0 0 1 RG 4 w [10 10] 5 d 50 100 m 150 100 l S"))
        {
            Assert.True(B84_Blue(b, 51, 100) && B84_Blue(b, 53, 100));
            Assert.True(B84_White(b, 57, 100) && B84_White(b, 63, 100));
            Assert.True(B84_Blue(b, 67, 100));
        }
        using (var b = B84_Render("0 0 1 RG 4 w [10 10] -5 d 50 100 m 150 100 l S"))
        {
            Assert.True(B84_White(b, 51, 100) && B84_White(b, 53, 100));
            Assert.True(B84_Blue(b, 57, 100) && B84_Blue(b, 63, 100));
            Assert.True(B84_White(b, 67, 100));
        }
        using (var b = B84_Render("2 0 0 2 0 0 cm 0 0 1 RG 2 w [5 5] 0 d 25 50 m 75 50 l S"))
        {
            Assert.True(B84_Blue(b, 52, 100) && B84_Blue(b, 58, 100));
            Assert.True(B84_White(b, 62, 100) && B84_White(b, 68, 100));
            Assert.True(B84_Blue(b, 72, 100));
        }
    }

    /// <summary>
    /// 8.4.3.6 sentence 11: an empty dash array (with any phase) is a solid line — after a dashed line, „[] 0 d“ and „[] 7 d“ draw solid.
    /// </summary>
    [Fact]
    public void B84_EmptyDashArray_IsASolidLine()
    {
        using var b = B84_Render("0 0 1 RG 4 w [10 10] 0 d 50 50 m 150 50 l S [] 0 d 50 100 m 150 100 l S [10 10] 0 d [] 7 d 50 150 m 150 150 l S");
        Assert.True(B84_White(b, 65, 50));
        foreach (var y in (int[])[100, 150])
            Assert.True(B84_Blue(b, 55, y) && B84_Blue(b, 65, y) && B84_Blue(b, 75, y));
    }

    /// <summary>
    /// 8.4.3.6 sentences 19–20: dashes take the line cap at their ends and the line join at corners inside them — round caps reach 5 beyond a
    /// dash ending at x 70 (x 73 blue, the middle of the gap white); a corner in the middle of a dash is mitred (its outer corner blue, beyond
    /// the circle a round join would draw).
    /// </summary>
    [Fact]
    public void B84_Dashes_TakeCapsAndJoins()
    {
        using (var b = B84_Render("0 0 1 RG 10 w 1 J [20 30] 0 d 50 50 m 150 50 l S"))
        {
            Assert.True(B84_Blue(b, 73, 50));
            Assert.True(B84_White(b, 85, 50));
        }
        using var corner = B84_Render("0 0 1 RG 10 w 0 j [100 10] 0 d 50 50 m 100 50 l 100 150 l S");
        Assert.True(B84_Blue(corner, 104, 45));
    }

    /// <summary>
    /// 8.4.3.6 sentence 21: overlapping caps of dashes follow 11.6.2 (→ M8) — today's behaviour: a half transparent line of dots with round caps
    /// that overlap is painted once, where two caps overlap it is not darker than in the middle of one.
    /// </summary>
    [Fact]
    public void B84_OverlappingDashCaps_PaintedOnce()
    {
        using var b = B84_Render("/G gs 0 0 1 RG 10 w 1 J [0 6] 0 d 20 100 m 180 100 l S", "/ExtGState << /G << /CA 0.5 >> >>");
        var single = B84_At(b, 50, 100);
        var overlap = B84_At(b, 53, 100);
        Assert.InRange(single.Red, (byte)100, (byte)160);
        Assert.InRange(overlap.Red - single.Red, -8, 8);
    }

    /// <summary>
    /// 8.4.3.6 sentences 22–23: the dashes are not fitted to the path; a dash that ends exactly at a corner is ended with its cap before the
    /// corner — [50 50] on a path turning at 50: no mitre at the corner (its outer square white), the next dash begins only after the gap of
    /// 50 (at y 60 on the upward segment white).
    /// </summary>
    [Fact]
    public void B84_DashEndingAtACorner_EndsBeforeTheCorner()
    {
        using var b = B84_Render("0 0 1 RG 10 w [50 50] 0 d 50 50 m 100 50 l 100 150 l S");
        Assert.True(B84_Blue(b, 97, 50));
        Assert.True(B84_White(b, 102, 46));
        Assert.True(B84_White(b, 100, 60) && B84_White(b, 100, 95));
        Assert.True(B84_Blue(b, 100, 110));
    }

    /// <summary>
    /// 8.4.3.6 sentence 24: every subpath begins the pattern anew with the phase — the second subpath begins at x 50 with a dash, not in the gap
    /// the first one ended in.
    /// </summary>
    [Fact]
    public void B84_DashPattern_RestartsInEverySubpath()
    {
        using var b = B84_Render("0 0 1 RG 4 w [10 10] 0 d 50 100 m 65 100 l 50 120 m 80 120 l S");
        Assert.True(B84_Blue(b, 52, 120) && B84_Blue(b, 57, 120));
        Assert.True(B84_White(b, 63, 120) && B84_White(b, 67, 120));
        Assert.True(B84_Blue(b, 72, 120));
    }

    // ------------------------------------------------------------------ 8.4.4, 8.4.5

    /// <summary>
    /// 8.4.4 sentences 10–11: the matrix of cm is six separate numbers, not an array — reading, „[2 0 0 2 0 0] cm“ is passed over (the square
    /// stays at 10–30, nothing fails); writing, every cm in the program's own content has six numbers.
    /// </summary>
    [Fact]
    public void B84_Cm_TakesSixNumbers()
    {
        var (doc, list) = B84_List("[2 0 0 2 0 0] cm 10 10 20 20 re f");
        using (doc)
        {
            var item = list.Items.Span[0];
            Assert.Equal((10f, 10f, 30f, 30f), (item.Left, item.Top, item.Right, item.Bottom));
        }
        var wrong = new List<string>();
        foreach (var (name, data, _) in B84_OwnStreams())
        {
            var parser = new ContentParser(data);
            while (parser.Next() is { } op)
                if (op == "cm" && !(parser.Operands.Count == 6 && parser.Operands.All(o => o.IsNumber))) wrong.Add(name);
        }
        Assert.Empty(wrong);
    }

    /// <summary>8.4.5 sentence 8: gs is cumulative — /A (/CA 0.5, /LW 10) then /B (/LW 2): the stroke is 2 wide and still half transparent.</summary>
    [Fact]
    public void B84_Gs_IsCumulative()
    {
        var (doc, list) = B84_List("/A gs /B gs 0 0 1 RG 50 100 m 150 100 l S", "/ExtGState << /A << /CA 0.5 /LW 10 >> /B << /LW 2 >> >>");
        using (doc)
        {
            var paint = B84_Paint(list, 0);
            Assert.Equal(2f, paint.LineWidth);
            Assert.InRange(paint.Stroke >> 24, 120u, 135u);
        }
    }

    /// <summary>
    /// 8.4.5 sentences 16–18 (/LC), 19–21 (/LJ), 22–23 (/ML): the entries act like J, j and M — /LC 2 like „2 J“, /LJ 2 like „2 j“, /ML 1
    /// like „1 M“ (and each differs from the initial value).
    /// </summary>
    [Theory]
    [InlineData("/LC 2", "2 J", "20 w 50 100 m 150 100 l S")]
    [InlineData("/LJ 2", "2 j", "20 w 50 50 m 100 150 l 150 50 l S")]
    [InlineData("/ML 1", "1 M", "20 w 0 j 50 50 m 100 150 l 150 50 l S")]
    public void B84_LineStyleEntries_ActLikeTheirOperators(string entry, string op, string line)
    {
        using var viaState = B84_Render($"/G gs 0 0 1 RG {line}", $"/ExtGState << /G << {entry} >> >>");
        using var viaOperator = B84_Render($"{op} 0 0 1 RG {line}");
        using var initial = B84_Render($"0 0 1 RG {line}");
        Assert.True(B84_Same(viaState, viaOperator));
        Assert.False(B84_Same(viaState, initial));
    }

    /// <summary>
    /// 8.4.5 sentences 24–26: /D is [array phase] — /D [[10 10] 0] dashes like „[10 10] 0 d“; /D [5 0] (no array first) is passed over, the line
    /// stays solid.
    /// </summary>
    [Fact]
    public void B84_DashEntry_ArrayAndPhase()
    {
        const string line = "0 0 1 RG 4 w 50 100 m 150 100 l S";
        using var viaState = B84_Render("/G gs " + line, "/ExtGState << /G << /D [[10 10] 0] >> >>");
        using var viaOperator = B84_Render("[10 10] 0 d " + line);
        Assert.True(B84_Same(viaState, viaOperator));
        using var wrong = B84_Render("/G gs " + line, "/ExtGState << /G << /D [5 0] >> >>");
        using var solid = B84_Render(line);
        Assert.True(B84_Same(wrong, solid));
    }

    /// <summary>
    /// 8.4.5 sentences 36–39: without /op, /OP sets the overprint of fills as well — with only /OP true /OPM 1, yellow over cyan comes out green
    /// in the overprint preview.
    /// </summary>
    [Fact]
    public void B84_OverprintWithoutLowerCaseOp_AppliesToFills()
    {
        using var b = B84_Render("1 0 0 0 k 0 0 120 200 re f /O gs 0 0 1 0 k 80 0 120 200 re f", "/ExtGState << /O << /OP true /OPM 1 >> >>", overprint: true);
        var c = B84_At(b, 100, 100);
        Assert.True(c.Green > 120 && c.Red < 60 && c.Blue < 100, $"grün erwartet: {c}");
    }

    /// <summary>
    /// 8.4.5 sentence 94: gs replaces the soft mask instead of intersecting it — /SM1 (passing the left half) then /SM2 (passing the top half):
    /// the top is red on both sides, the bottom left white.
    /// </summary>
    [Fact]
    public void B84_SoftMask_IsReplacedNotIntersected()
    {
        static string Mask(string area) => B84_Form(area, "/Group << /S /Transparency /CS /DeviceGray >>");
        using var b = B84_Render("/SM1 gs /SM2 gs 1 0 0 rg 0 0 200 200 re f",
            "/ExtGState << /SM1 << /SMask << /Type /Mask /S /Luminosity /G 5 0 R >> >> /SM2 << /SMask << /Type /Mask /S /Luminosity /G 6 0 R >> >> >>",
            [Mask("1 g 0 0 100 200 re f"), Mask("1 g 0 100 200 100 re f")]);
        Assert.True(B84_Red(b, 150, 150) && B84_Red(b, 50, 150));
        Assert.True(B84_White(b, 50, 50) && B84_White(b, 150, 50));
    }

    /// <summary>
    /// 8.4.5 sentences 112–118: /TK (knockout of the glyphs of a text object, ISO 32000-2, 9.3.8; M7, docs/FEHLERGEDAECHTNIS.md, No. 1165) — two
    /// glyphs drawn on top of each other in one BT … ET under /ca 0.5 are darker where they overlap with /TK false; with /TK true the later replaces
    /// the earlier, the overlap is as light as one glyph.
    /// </summary>
    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public void B84_TextKnockout_FalseBlends_TrueKnocksOut(string knockout)
    {
        const string font = "/Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >>";
        var resources = $"/ExtGState << /G << /TK {knockout} /ca 0.5 >> >> {font}";
        static int Darkest(SKBitmap b)
        {
            var least = 255;
            for (var y = 40; y < 190; y++)
                for (var x = 40; x < 120; x++)
                    least = Math.Min(least, b.GetPixel(x, y).Red);
            return least;
        }
        using var once = B84_Render("/G gs 0 g BT /F1 100 Tf 50 50 Td (l) Tj ET", resources);
        using var twice = B84_Render("/G gs 0 g BT /F1 100 Tf 50 50 Td [(l) 222 (l)] TJ ET", resources);   // the second glyph on the first
        Assert.InRange(Darkest(once), 100, 160);
        if (knockout == "false") Assert.True(Darkest(twice) < Darkest(once) - 30, $"{Darkest(twice)} gegen {Darkest(once)}");
        else Assert.InRange(Darkest(twice), Darkest(once) - 8, Darkest(once) + 8);
    }
}
