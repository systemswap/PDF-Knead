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
/// Annotations without an appearance stream: PDF Knead draws rectangles, circles, lines, polygons, ink and text markup itself,
/// and sets field values without /AP (NeedAppearances) and free text as when filling in.
/// </summary>
public sealed class AnnotationAppearanceTests
{
    [TestPdfsFact]
    public void SquareAndCircle_WithoutAppearance_AreDrawn()
    {
        using var doc = Open("annotation-square-circle-without-appearance.pdf");
        using var page = Render(doc);
        // Filled rectangle (IC grey, blue border 5 pt): grey in the middle, blue at the border
        var mid = At(page, doc, (261.73 + 449.47) / 2, (616.55 + 704.11) / 2);
        Assert.True(mid.Red is > 140 and < 180 && Math.Abs(mid.Red - mid.Blue) < 10, mid.ToString());
        var edge = At(page, doc, 261.73 + 2.5, (616.55 + 704.11) / 2);
        Assert.True(edge.Blue > 120 && edge.Red < 60, edge.ToString());
    }

    [Fact]
    public void Generated_Stream_ForEachKind()
    {
        var rect = new PdfRectangle(0, 0, 100, 50);
        foreach (var (kind, key, value) in new (string, string, PdfObject)[]
                 {
                     ("Line", "L", TestPdf.Box(0, 0, 100, 50)), ("PolyLine", "Vertices", TestPdf.Box(0, 0, 50, 50, 100, 0)),
                     ("Ink", "InkList", new PdfArray([TestPdf.Box(0, 0, 10, 10, 20, 0)])),
                     ("Highlight", "QuadPoints", TestPdf.Box(0, 50, 100, 50, 0, 0, 100, 0)), ("Squiggly", "QuadPoints", TestPdf.Box(0, 50, 100, 50, 0, 0, 100, 0)),
                 })
        {
            var annot = TestPdf.Dict(("Subtype", TestPdf.N(kind)), ("C", TestPdf.Box(1, 0, 0)), (key, value));
            var form = AnnotationAppearance.Build(annot, rect);
            Assert.NotNull(form);
            Assert.True(form!.RawData!.Length > 10, kind);
        }
        // No appearance without a colour; unknown kinds stay untouched
        Assert.Null(AnnotationAppearance.Build(TestPdf.Dict(("Subtype", TestPdf.N("Line")), ("L", TestPdf.Box(0, 0, 1, 1))), rect));
        Assert.Null(AnnotationAppearance.Build(TestPdf.Dict(("Subtype", TestPdf.N("Sound")), ("C", TestPdf.Box(1, 0, 0))), rect));
        // Crafted: huge point lists are limited, not rejected
        var many = new PdfArray(Enumerable.Range(0, 1_000_000).Select(i => (PdfObject)PdfInteger.Of(i % 100)));
        var ink = TestPdf.Dict(("Subtype", TestPdf.N("Ink")), ("C", TestPdf.Box(0)), ("InkList", new PdfArray([many])));
        Assert.True(AnnotationAppearance.Build(ink, rect)!.RawData!.Length < 4_000_000);
    }

    /// <summary>
    /// A square or circle with a border colour, border width 0 and no interior colour asks for neither border nor fill: no appearance. Before, a
    /// path was filled without a colour of its own — a black area (found in the M6 tests of 8.4.3.2; docs/FEHLERGEDAECHTNIS.md, No. 1126).
    /// </summary>
    [Theory]
    [InlineData("Square")]
    [InlineData("Circle")]
    public void SquareOrCircle_WidthZeroWithoutInterior_NoAppearance(string kind)
    {
        var annot = TestPdf.Dict(("Subtype", TestPdf.N(kind)), ("C", TestPdf.Box(1, 0, 0)), ("BS", TestPdf.Dict(("W", PdfInteger.Of(0)))));
        Assert.Null(AnnotationAppearance.Build(annot, new PdfRectangle(0, 0, 100, 50)));
    }

    /// <summary>
    /// issue13447: /Rect does not match the points (another program had rotated the page). The other readers still draw ink,
    /// lines and polylines completely, two of the reference readers also the arrowheads. Clipping to /Rect would cut everything off.
    /// </summary>
    [TestPdfsFact]
    public void PointsOutsideRect_AreDrawn_WithLineEndings()
    {
        using var doc = Open("issue13447.pdf");
        using var page = Render(doc);
        static void Blue(SKColor c, string what) => Assert.True(c.Blue > 180 && c.Red < 140, $"{what}: {c}");
        Blue(At(page, doc, 290.4, 446.9), "Freihand (Rect 152…237 × 260…313)");
        Blue(At(page, doc, 177, 70), "Linie (Rect 537…598 × 146…316)");
        Blue(At(page, doc, 172, 140), "geschlossene Pfeilspitze am Ende der Linie");

        // The bounding box encloses the points including the arrowhead; if they lie inside /Rect, it stays /Rect
        var rect = new PdfRectangle(0, 0, 100, 50);
        var arrow = TestPdf.Dict(("Subtype", TestPdf.N("Line")), ("C", TestPdf.Box(1, 0, 0)), ("L", TestPdf.Box(300, 300, 400, 300)),
            ("LE", new PdfArray([TestPdf.N("None"), TestPdf.N("OpenArrow")])));
        var bbox = PdfRectangle.From(AnnotationAppearance.Build(arrow, rect)!.Dictionary.Get(PdfName.Get("BBox")))!.Value;
        Assert.True(bbox.Right >= 401 && bbox.Top >= 304.5 && bbox.Left <= 0, bbox.ToString());
        var inside = TestPdf.Dict(("Subtype", TestPdf.N("Line")), ("C", TestPdf.Box(1, 0, 0)), ("L", TestPdf.Box(10, 10, 90, 40)));
        Assert.Equal(rect, PdfRectangle.From(AnnotationAppearance.Build(inside, rect)!.Dictionary.Get(PdfName.Get("BBox"))));
    }

    /// <summary>
    /// bug2082236: an annotation with NoRotate (/F 16) on a page with /Rotate 270 stays upright, rotated about the upper
    /// left corner of /Rect (120, 50); it then lies in the green frame at x 100…120, y 40…50 instead of rotating with the page.
    /// </summary>
    [TestPdfsFact]
    public void NoRotate_StaysUpright_AroundUpperLeftCorner()
    {
        using var doc = Open("bug2082236.pdf");
        using var page = Render(doc);
        static bool Dark(SKColor c) => c.Red < 80 && c.Green < 80 && c.Blue < 80;
        Assert.True(Dark(At(page, doc, 103, 42)), At(page, doc, 103, 42).ToString());
        Assert.False(Dark(At(page, doc, 127, 33)), "an der alten Stelle (/Rect 120…130 × 30…50) liegt nichts mehr");
    }

    /// <summary>
    /// rc_annotation: a note without an appearance stream and with an empty /Rect [50 50 50 50] gets an icon of 20 × 20 pt to the
    /// right of and below (50, 50), light yellow without /C; otherwise nothing would be visible. Replies (/IRT) get none.
    /// </summary>
    [TestPdfsFact]
    public void TextNote_WithoutAppearance_ShowsIcon()
    {
        using var doc = Open("rc_annotation.pdf");
        using var page = Render(doc);
        var inside = At(page, doc, 58, 46);
        Assert.True(inside.Red > 200 && inside.Green > 180 && inside.Blue < 150, $"hellgelb erwartet: {inside}");
        Assert.Equal(SKColors.White, At(page, doc, 45, 45));
        Assert.Equal(SKColors.White, At(page, doc, 55, 55));
        var icon = AnnotationAppearance.Build(TestPdf.Dict(("Subtype", TestPdf.N("Text")), ("Name", TestPdf.N("Comment"))), new PdfRectangle(10, 10, 10, 10))!;
        Assert.Equal(new PdfRectangle(10, -10, 30, 10), PdfRectangle.From(icon.Dictionary.Get(PdfName.Get("BBox"))));
        Assert.Null(AnnotationAppearance.Build(TestPdf.Dict(("Subtype", TestPdf.N("Text")), ("IRT", TestPdf.Dict())), new PdfRectangle(10, 10, 10, 10)));
    }

    /// <summary>
    /// A line without an appearance and with an empty /Rect [0 0 0 0] (as in the public test file annotation-line-without-appearance-empty-Rect):
    /// other readers draw it from /L, so does the display; „Was niemand sieht“ must not take it for invisible (docs/FEHLERGEDAECHTNIS.md, No. 244).
    /// </summary>
    [Fact]
    public void Line_EmptyRect_WithoutAppearance_IsDrawn_AndStaysOnCleanup()
    {
        var pdf = TestPdf.Simple(1);
        var page = (PdfDictionary)pdf.Get(5);
        var line = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Line")), ("Rect", TestPdf.Box(0, 0, 0, 0)),
            ("L", TestPdf.Box(24, 572, 594, 572)), ("C", TestPdf.Box(1, 0, 0)), ("F", PdfInteger.Of(4)), ("BS", TestPdf.Dict(("W", PdfInteger.Of(3))))));
        page.Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(line)]));
        using var doc = PdfDocument.Open(pdf.Build());
        static void Red(SKColor c) => Assert.True(c.Red > 200 && c.Green < 80 && c.Blue < 80, c.ToString());
        using (var shown = Render(doc)) Red(At(shown, doc, 300, 572));

        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Cleanup = CleanupItems.HiddenContent });
        using var cleaned = PdfDocument.Open(output.ToArray());
        Assert.Equal(1, cleaned.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))?.Count);
        using var after = Render(cleaned);
        Red(At(after, cleaned, 300, 572));
    }

    [TestPdfsFact]
    public void FieldValues_WithoutAppearance_AreShown_CombPerCell()
    {
        // bug2055455: NeedAppearances, fields without /AP with the value "8" and a comb field "8888" (MaxLen 4)
        using var doc = Open("bug2055455.pdf");
        var list = PageInterpreter.Build(doc, doc.Pages[0]);
        Assert.Equal(5, list.Text.Count(t => t.Text == "8"));
    }

    [TestPdfsFact]
    public void FreeText_WithoutAppearance_ShowsContents_AlsoInArabic()
    {
        using (var doc = Open("bug1865341.pdf"))
            Assert.Equal("Załącznik", string.Concat(PageInterpreter.Build(doc, doc.Pages[0]).Text.Select(t => t.Text)).Trim());
        // Arabic free text: the font of /DA lacks the characters — set with Noto Arabic (before, nothing was drawn; docs/FEHLERGEDAECHTNIS.md,
        // No. 910)
        using (var doc = Open("freetext_no_appearance.pdf"))
        {
            var text = string.Concat(PageInterpreter.Build(doc, doc.Pages[0]).Text.Select(t => t.Text));
            Assert.Contains(text, c => c is >= '\u0600' and <= '\u06FF');
        }
    }

    [TestPdfsTheory]
    [InlineData("annotation-square-circle-without-appearance.pdf")]
    [InlineData("bug1538111.pdf")]
    [InlineData("bug2055455.pdf")]
    [InlineData("bug1865341.pdf")]
    [InlineData("bug2082236.pdf")]   // NoRotate on a rotated page
    public void Flatten_KeepsWhatTheViewShows(string file)
    {
        // What the view draws without /AP must go into the content on flattening; otherwise it would be lost (data loss)
        using var doc = Open(file);
        using var before = Render(doc);
        using var output = new MemoryStream();
        PdfKnead.Core.Pdf.Writing.PdfWriter.Write(doc, PdfKnead.Core.Pdf.Editing.PageItem.From(doc), output,
            new PdfKnead.Core.Pdf.Writing.PdfSaveOptions { Flatten = true });
        using var flat = PdfDocument.Open(output.ToArray());
        Assert.True(flat.Pages[0].Dictionary.GetArray(PdfName.Get("Annots")) is null or { Count: 0 });
        using var after = Render(flat);
        var differing = 0;
        for (var y = 0; y < before.Height; y += 2)
        for (var x = 0; x < before.Width; x += 2)
        {
            SKColor p = before.GetPixel(x, y), q = after.GetPixel(x, y);
            if (Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue) > 60) differing++;
        }
        Assert.True(differing < 20, $"{differing} Pixel anders");
    }

    private static SKBitmap Render(PdfDocument doc) =>
        SkiaRenderer.RenderPage(PageInterpreter.Build(doc, doc.Pages[0]), doc, doc.Pages[0], 1, new ImageCache());

    private static SKColor At(SKBitmap page, PdfDocument doc, double x, double y)
    {
        var p = PageGeometry.PageToDevice(doc.Pages[0], 1).Transform(x, y);
        return page.GetPixel((int)p.X, (int)p.Y);
    }

    private static PdfDocument Open(string name) => PdfDocument.Open(File.ReadAllBytes(Path.Combine(Root(), "TestPDFs", "pdfjs", name)));

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }
}
