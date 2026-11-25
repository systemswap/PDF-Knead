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
/// Graphics objects and coordinate systems (ISO 32000-2, 8.1–8.3; Konformitätsabgleich M6, draft a-81-83): the order of operators in what
/// the program writes, the CropBox, 3D annotations, the transformation matrices (helpers of draft b-84: <see cref="B84_PathViolations"/>,
/// <see cref="B84_ContentStreams"/>, <see cref="B84_OwnStreams"/>).
/// </summary>
public sealed partial class M6NormTests
{
    /// <summary>A page of the size of A4 (TestPdf.Simple) with the given content.</summary>
    private static PdfDocument A81_Page(string content)
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(Encoding.ASCII.GetBytes(content)));
        return PdfDocument.Open(pdf.Build());
    }

    /// <summary>Saves page entries and opens the result.</summary>
    private static PdfDocument A81_Saved(PdfDocument doc, IReadOnlyList<PageItem> items)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, new PdfSaveOptions { CompressStreams = false });
        return PdfDocument.Open(output.ToArray());
    }

    /// <summary>The first page of a document rendered at 72 dpi.</summary>
    private static SKBitmap A81_Render(PdfDocument doc) =>
        SkiaRenderer.RenderPage(PageInterpreter.Build(doc, doc.Pages[0]), doc, doc.Pages[0], 1, new ImageCache());

    /// <summary>Every place in the content streams of a document that breaks the order of Figure 9, with the name of its stream.</summary>
    private static List<string> A81_Violations(PdfDocument doc) =>
        [.. B84_ContentStreams(doc).SelectMany(s => B84_PathViolations(s.Data).Select(v => $"{s.Name}: {v}"))];

    /// <summary>
    /// 8.2 sentences 26, 27 (writing): content the program writes keeps the order of Figure 9 — between the start of a path (m, re) and its
    /// painting operator only path construction and W/W*, no painting without a path — after moving all objects, deleting one, pasting a copy,
    /// redacting across a line and a word, and in every appearance of its own elements and annotations.
    /// </summary>
    [Fact]
    public void A81_WrittenContent_KeepsTheOrderOfFigure9()
    {
        const string content = "q 1 0 0 rg 100 100 50 50 re f Q q 0 0 1 RG 3 w 300 100 m 400 200 l S Q BT /F1 12 Tf 100 400 Td (Abc def) Tj ET " +
                               "q 2 0 0 2 0 0 cm 0 1 0 rg 150 150 20 20 re W n 140 140 50 50 re f Q 0.5 g 400 400 m 450 400 l 425 450 l h f";
        using var doc = A81_Page(content);
        Assert.Empty(A81_Violations(doc));   // the input itself keeps the order
        var item = PageItem.From(doc)[0];
        var found = ContentObjectEditor.ObjectsOf(item);
        var keys = Enumerable.Range(0, found.Count).Select(found.KeyOf).ToList();
        var moved = ContentObjectEditor.Transform(item, keys, Matrix.Translate(10, 20), found);
        using (var saved = A81_Saved(doc, [moved])) Assert.Empty(A81_Violations(saved));
        var deleted = ContentObjectEditor.Delete(item, [keys[0]], found);
        using (var saved = A81_Saved(doc, [deleted])) Assert.Empty(A81_Violations(saved));
        var copy = ContentObjectEditor.Copy(item, keys, found);
        Assert.NotNull(copy);
        var (pasted, _) = ContentObjectEditor.Insert(item, copy.Value.Form, Matrix.Translate(0, 300));
        using (var saved = A81_Saved(doc, [pasted])) Assert.Empty(A81_Violations(saved));
        using (var redacted = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(330, 90, 370, 210)), (0, new PdfRectangle(120, 395, 140, 415)))))
            Assert.Empty(A81_Violations(redacted));
        var own = B84_OwnStreams().SelectMany(s => B84_PathViolations(s.Data).Select(v => $"{s.Name}: {v}")).ToList();
        Assert.Empty(own);
    }

    /// <summary>
    /// 8.2 sentence 61 (Table 50): state operators at the level of the content stream belong to no graphics object — of „1 0 0 rg“ and two squares,
    /// deleting the first square (object editor) or redacting it leaves the second one red.
    /// </summary>
    [Fact]
    public void A81_StateOperatorBetweenObjects_BelongsToNone()
    {
        using var doc = A81_Page("1 0 0 rg 10 10 50 50 re f 100 10 50 50 re f");
        var item = PageItem.From(doc)[0];
        var found = ContentObjectEditor.ObjectsOf(item);
        Assert.Equal(2, found.Count);
        var first = Enumerable.Range(0, found.Count).Single(i => found.VisibleOf(i).Left < 50);
        var deleted = ContentObjectEditor.Delete(item, [found.KeyOf(first)], found);
        using (var saved = A81_Saved(doc, [deleted]))
        {
            using var b = A81_Render(saved);
            Assert.True(B84_Red(b, 125, 35));
            Assert.True(B84_White(b, 35, 35));
        }
        using var redacted = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(5, 5, 65, 65))));
        using var r = A81_Render(redacted);
        Assert.True(B84_Red(r, 125, 35));
    }

    /// <summary>
    /// 8.3.2.3 sentences 5, 14, 15: the CropBox is the part of the unbounded user space that is shown — MediaBox [0 0 300 300] with CropBox
    /// [100 100 200 200]: a picture of 100 × 100 with the red square at (100, 100) in its lower left corner; a square at (20, 20) unseen.
    /// </summary>
    [Fact]
    public void A81_CropBox_IsTheVisiblePart()
    {
        const string content = "1 0 0 rg 100 100 10 10 re f 0 0 1 rg 20 20 10 10 re f";
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /CropBox [100 100 200 200] /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        using var doc = PdfDocument.Open(file);
        using var b = A81_Render(doc);
        Assert.Equal((100, 100), (b.Width, b.Height));
        Assert.True(b.GetPixel(5, 94) is { Red: > 200, Green: < 60, Blue: < 60 });   // lower left: the red square
        for (var y = 0; y < b.Height; y++)
            for (var x = 0; x < b.Width; x++)
                Assert.False(b.GetPixel(x, y) is { Blue: > 200, Red: < 60 }, $"blau bei ({x}, {y})");
    }

    /// <summary>
    /// 8.3.2.4 sentence 18: 3D content lies in its own coordinates projected into an annotation (→ M10) — today's behaviour: of a /3D annotation
    /// with an appearance only the appearance is drawn (one entry), no 3D content.
    /// </summary>
    [Fact]
    public void A81_ThreeDAnnotation_OnlyItsAppearance()
    {
        const string appearance = "0 0 1 rg 0 0 100 100 re f";
        const string data = "U3D-Probedaten";
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [5 0 R] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Annot /Subtype /3D /Rect [50 50 150 150] /AP << /N 6 0 R >> /3DD 7 0 R >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Length {appearance.Length} >>\nstream\n{appearance}\nendstream",
            $"<< /Type /3D /Subtype /U3D /Length {data.Length} >>\nstream\n{data}\nendstream");
        using var doc = PdfDocument.Open(file);
        var list = PageInterpreter.Build(doc, doc.Pages[0]);
        Assert.Equal(1, list.Count);
        using var b = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
        Assert.True(B84_Blue(b, 100, 100));
        Assert.True(B84_White(b, 20, 20) && B84_White(b, 170, 170));
    }

    /// <summary>
    /// 8.3.3 sentences 6, 7: skewing [1 tan a tan b 1 0 0] — „1 0 0.5 1 0 0 cm“ turns the square 0 0 10 10 into a parallelogram whose upper edge
    /// runs from x 5 to 15.
    /// </summary>
    [Fact]
    public void A81_Skew_GivesAParallelogram()
    {
        var (doc, list) = InterpreterTests.Page("1 0 0.5 1 0 0 cm 0 0 10 10 re f");
        using (doc)
        {
            var item = list.Items.Span[0];
            Assert.Equal((0f, 0f, 15f, 10f), (item.Left, item.Top, item.Right, item.Bottom));
            var m = list.Matrices[item.Matrix];
            Assert.Equal((5d, 10d), m.Transform(0, 10));
            Assert.Equal((15d, 10d), m.Transform(10, 10));
            Assert.Equal((10d, 0d), m.Transform(10, 0));
        }
    }

    /// <summary>
    /// 8.3.4 sentences 3–8: a transformation changes the coordinate system, not what is painted — the square painted before „2 0 0 2 0 0 cm“
    /// stays at 0–10, the one after it lies at 40–60.
    /// </summary>
    [Fact]
    public void A81_Transformation_AffectsOnlyWhatFollows()
    {
        var (doc, list) = InterpreterTests.Page("0 0 10 10 re f 2 0 0 2 0 0 cm 20 20 10 10 re f");
        using (doc)
        {
            var before = list.Items.Span[0];
            var after = list.Items.Span[1];
            Assert.Equal((0f, 0f, 10f, 10f), (before.Left, before.Top, before.Right, before.Bottom));
            Assert.Equal((40f, 40f, 60f, 60f), (after.Left, after.Top, after.Right, after.Bottom));
        }
    }

    /// <summary>
    /// 8.3.4 sentences 20, 21, 27–34: the new matrix is multiplied before the existing one (M′ = MT × M) — „2 0 0 2 0 0 cm 1 0 0 1 10 0 cm“ puts
    /// the square 0 0 10 10 at x 20–40 (moved, then scaled), not at 10–30.
    /// </summary>
    [Fact]
    public void A81_TwoCm_NewMatrixFirst()
    {
        var (doc, list) = InterpreterTests.Page("2 0 0 2 0 0 cm 1 0 0 1 10 0 cm 0 0 10 10 re f");
        using (doc)
        {
            var item = list.Items.Span[0];
            Assert.Equal((20f, 40f), (item.Left, item.Right));
            Assert.Equal((0f, 20f), (item.Top, item.Bottom));
        }
    }
}
