using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Paths (ISO 32000-2, 8.5; Konformitätsabgleich M6, draft c-85): construction, painting, fill rules and clipping. A page of 200 × 200 at
/// 72 dpi (helpers of draft b-84: <see cref="B84_Render"/>, <see cref="B84_At"/>; coordinates in page space, y upwards).
/// </summary>
public sealed partial class M6NormTests
{
    /// <summary>The verbs and coordinates of the path of entry <paramref name="index"/>.</summary>
    private static (PathVerb[] Verbs, float[] Points) C85_Path(DisplayList list, int index)
    {
        var (verb, count, point) = list.Paths[list.Items.Span[index].Path];
        var verbs = list.Verbs.AsSpan(verb, count).ToArray();
        var numbers = verbs.Sum(v => v switch { PathVerb.Move or PathVerb.Line => 2, PathVerb.Cubic => 6, _ => 0 });
        return (verbs, list.Points.AsSpan(point, numbers).ToArray());
    }

    /// <summary>The path of the only entry of a page with the given content.</summary>
    private static (PathVerb[] Verbs, float[] Points) C85_OnlyPath(string content)
    {
        var (doc, list) = InterpreterTests.Page(content);
        using (doc)
        {
            Assert.Equal(1, list.Count);
            return C85_Path(list, 0);
        }
    }

    /// <summary>Whether two contents render to the same pixels.</summary>
    private static bool C85_SameAs(string a, string b, string resources = "")
    {
        using var first = B84_Render(a, resources);
        using var second = B84_Render(b, resources);
        return B84_Same(first, second);
    }

    /// <summary>A circle of four Bézier curves around (cx, cy), anticlockwise or clockwise, as a closed subpath.</summary>
    private static string C85_Circle(double cx, double cy, double r, bool clockwise)
    {
        var k = 0.5523 * r;
        string P(double x, double y) => FormattableString.Invariant($"{x} {y}");
        return clockwise
            ? $"{P(cx + r, cy)} m {P(cx + r, cy - k)} {P(cx + k, cy - r)} {P(cx, cy - r)} c {P(cx - k, cy - r)} {P(cx - r, cy - k)} {P(cx - r, cy)} c " +
              $"{P(cx - r, cy + k)} {P(cx - k, cy + r)} {P(cx, cy + r)} c {P(cx + k, cy + r)} {P(cx + r, cy + k)} {P(cx + r, cy)} c h "
            : $"{P(cx + r, cy)} m {P(cx + r, cy + k)} {P(cx + k, cy + r)} {P(cx, cy + r)} c {P(cx - k, cy + r)} {P(cx - r, cy + k)} {P(cx - r, cy)} c " +
              $"{P(cx - r, cy - k)} {P(cx - k, cy - r)} {P(cx, cy - r)} c {P(cx + k, cy - r)} {P(cx + r, cy - k)} {P(cx + r, cy)} c h ";
    }

    // ------------------------------------------------------------------ 8.5.1

    /// <summary>
    /// 8.5.1 sentence 4 and 8.5.4 sentence 9: at the start of a page the clipping path is the MediaBox, the whole page — a CropBox larger than
    /// the MediaBox shows nothing beyond it (the picture is 200 × 200, all blue), and a fill far beyond the page covers every corner of it.
    /// </summary>
    [Fact]
    public void C85_InitialClip_IsTheMediaBox()
    {
        const string content = "0 0 1 rg -50 -50 300 300 re f";
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /CropBox [-50 -50 250 250] /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        using var doc = PdfDocument.Open(file);
        using (var b = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, doc.Pages[0]), doc, doc.Pages[0], 1, new ImageCache()))
        {
            Assert.Equal((200, 200), (b.Width, b.Height));
            Assert.True(B84_Blue(b, 0, 0) && B84_Blue(b, 199, 199) && B84_Blue(b, 100, 100));
        }
        using var whole = B84_Render("0 0 1 rg -100 -100 400 400 re f");
        Assert.True(B84_Blue(whole, 0, 0) && B84_Blue(whole, 199, 199) && B84_Blue(whole, 0, 199) && B84_Blue(whole, 199, 0));
    }

    /// <summary>
    /// 8.5.1 sentences 5–6: a path has any number of straight or curved segments, its subpaths may be disconnected — a triangle and a shape with
    /// a curve form one entry with two starts and both are filled; a path of only „m“ before „n“ does not fail.
    /// </summary>
    [Fact]
    public void C85_Path_OfDisconnectedSubpathsWithCurves()
    {
        const string content = "0 0 1 rg 20 20 m 60 20 l 60 60 l h 120 120 m 180 120 l 180 180 150 180 120 160 c f";
        var (verbs, _) = C85_OnlyPath(content);
        Assert.Equal(2, verbs.Count(v => v == PathVerb.Move));
        Assert.Contains(PathVerb.Cubic, verbs);
        using (var b = B84_Render(content))
        {
            Assert.True(B84_Blue(b, 45, 30) && B84_Blue(b, 150, 140));
            Assert.True(B84_White(b, 90, 90));
        }
        var (doc, list) = InterpreterTests.Page("10 10 m n 0 0 1 rg 50 50 10 10 re f");
        using (doc) Assert.Equal(1, list.Count);
    }

    /// <summary>
    /// 8.5.1 sentences 7–9 (also 8.5.3.2 sentences 1–3): two segments are joined only if the second follows the first and
    /// begins at its end — a peak drawn as one subpath has the mitre above (100, 160); drawn as two subpaths meeting at the same point it has
    /// two butt ends there and nothing above.
    /// </summary>
    [Fact]
    public void C85_Segments_JoinedOnlyWithinASubpath()
    {
        using var joined = B84_Render("0 0 1 RG 20 w 0 J 0 j 10 M 50 50 m 100 150 l 150 50 l S");
        using var apart = B84_Render("0 0 1 RG 20 w 0 J 0 j 10 M 50 50 m 100 150 l 100 150 m 150 50 l S");
        Assert.True(B84_Blue(joined, 100, 165));
        Assert.True(B84_White(apart, 100, 165));
        Assert.True(B84_Blue(apart, 100, 145));
    }

    /// <summary>8.5.1 sentences 12–13: h joins the end of a subpath with its start — the closing segment is stroked only with h.</summary>
    [Fact]
    public void C85_ClosePath_AddsTheClosingSegment()
    {
        using var closed = B84_Render("0 0 1 RG 4 w 50 50 m 150 50 l 100 150 l h S");
        using var open = B84_Render("0 0 1 RG 4 w 50 50 m 150 50 l 100 150 l S");
        Assert.True(B84_Blue(closed, 75, 100));
        Assert.True(B84_White(open, 75, 100));
    }

    // ------------------------------------------------------------------ 8.5.2

    /// <summary>
    /// 8.5.2.1 sentences 1–2 (writing): every path PDF-Knead writes begins with m or re — after redacting a stroke whose second path begins with
    /// „l“ (it starts at the current point), in the new content; in every appearance of its own elements and annotations.
    /// </summary>
    [Fact]
    public void C85_WrittenPaths_BeginWithMOrRe()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("2 w 0 0 0 RG 100 500 m 150 500 l S 250 500 l S"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var result = Redactor.Apply(doc, doc.Pages[0], [new PdfRectangle(180, 280, 280, 620)]);
        var written = doc.GetStreamData((PdfStream)result.Page.Get(PdfName.Contents)!);
        Assert.DoesNotContain(B84_PathViolations(written), v => v.StartsWith("start:"));
        var own = B84_OwnStreams().SelectMany(s => B84_PathViolations(s.Data).Where(v => v.StartsWith("start:")).Select(v => $"{s.Name}: {v}")).ToList();
        Assert.Empty(own);
    }

    /// <summary>
    /// 8.5.2.1 sentences 6–9: the current path is not part of the graphics state — q and Q between its construction and painting leave it, so the
    /// square is filled; after painting it is gone, a second „f“ paints nothing (one entry, no red).
    /// </summary>
    [Fact]
    public void C85_CurrentPath_NotInTheGraphicsState_AndGoneAfterPainting()
    {
        using (var b = B84_Render("0 0 1 rg 50 50 100 100 re q Q f"))
            Assert.True(B84_Blue(b, 100, 100));
        var (doc, list) = InterpreterTests.Page("0 0 1 rg 50 50 100 100 re f 1 0 0 rg f");
        using (doc)
        {
            Assert.Equal(1, list.Count);
            using var b = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
            Assert.True(B84_Blue(b, 100, 100));
        }
    }

    /// <summary>
    /// 8.5.2.1 sentences 10–12 (read leniently, docs/normabgleich/sonderregeln.md S-A A171): a segment without a current point is no error, and
    /// no point is invented — where none has been set yet (the start of the content) the segment begins at its first pair of coordinates:
    /// „100 100 l 180 100 l S“ draws from (100, 100) to (180, 100), nothing from the origin, which no reference reader draws (before, a line ran
    /// from (0, 0); docs/FEHLERGEDAECHTNIS.md, No. 1134); after a painting operator the last point of the stream stays the start: the second
    /// „l“ of „30 30 m 90 30 l S 150 150 l S“ draws from (90, 30).
    /// </summary>
    [Fact]
    public void C85_SegmentWithoutCurrentPoint_NoPointInvented_LastPointAfterPainting()
    {
        using (var b = B84_Render("0 0 1 RG 4 w 100 100 l 180 100 l S"))
        {
            Assert.True(B84_Blue(b, 140, 100));
            Assert.True(B84_White(b, 50, 50));
        }
        using (var b = B84_Render("0 0 1 RG 4 w 30 30 m 90 30 l S 150 150 l S"))
            Assert.True(B84_Blue(b, 120, 90));
        // a curve: from its first control point, as a rewritten „x1 y1 m“ before it gives every reader
        using (var b = B84_Render("0 0 1 RG 4 w 20 100 20 100 180 100 c S"))
        {
            Assert.True(B84_Blue(b, 100, 100));
            Assert.True(B84_White(b, 10, 50));
        }
    }

    /// <summary>8.5.2.1 sentences 13–16: m begins a subpath without a line; m after m replaces it without a trace.</summary>
    [Fact]
    public void C85_MoveAfterMove_Replaces()
    {
        var (verbs, points) = C85_OnlyPath("10 10 m 50 50 m 90 50 l S");
        Assert.Equal(new[] { PathVerb.Move, PathVerb.Line }, verbs);
        Assert.Equal(new[] { 50f, 50f, 90f, 50f }, points);
        using var b = B84_Render("0 0 1 RG 4 w 10 10 m 50 50 m 90 50 l S");
        Assert.True(B84_White(b, 30, 30));
    }

    /// <summary>8.5.2.1 sentences 20–23: c appends a curve with both control points; its end is the new current point.</summary>
    [Fact]
    public void C85_C_AppendsACurve()
    {
        var (verbs, points) = C85_OnlyPath("10 10 m 20 30 40 50 60 70 c 80 70 l S");
        Assert.Equal(new[] { PathVerb.Move, PathVerb.Cubic, PathVerb.Line }, verbs);
        Assert.Equal(new[] { 10f, 10f, 20f, 30f, 40f, 50f, 60f, 70f, 80f, 70f }, points);
    }

    /// <summary>8.5.2.1 sentences 24–27: v takes the current point as the first control point; its end is the new current point.</summary>
    [Fact]
    public void C85_V_FirstControlPointIsTheCurrentPoint()
    {
        var (verbs, points) = C85_OnlyPath("10 10 m 40 50 60 70 v 80 70 l S");
        Assert.Equal(new[] { PathVerb.Move, PathVerb.Cubic, PathVerb.Line }, verbs);
        Assert.Equal(new[] { 10f, 10f, 10f, 10f, 40f, 50f, 60f, 70f, 80f, 70f }, points);
    }

    /// <summary>8.5.2.1 sentences 28–29, 35–37: y takes the end point as the second control point; its end is the new current point.</summary>
    [Fact]
    public void C85_Y_SecondControlPointIsTheEnd()
    {
        var (verbs, points) = C85_OnlyPath("10 10 m 20 30 60 70 y 80 70 l S");
        Assert.Equal(new[] { PathVerb.Move, PathVerb.Cubic, PathVerb.Line }, verbs);
        Assert.Equal(new[] { 10f, 10f, 20f, 30f, 60f, 70f, 60f, 70f, 80f, 70f }, points);
    }

    /// <summary>
    /// 8.5.2.1 sentences 30–31, 38–41: h on a closed subpath does nothing (one close for „h h“); a segment after h begins a new subpath at the
    /// start of the closed one — the line to (10, 90) begins at (10, 10), and the drawn path has two starts.
    /// </summary>
    [Fact]
    public void C85_ClosePath_TwiceOnce_AndANewSubpathAtTheStart()
    {
        var (verbs, _) = C85_OnlyPath("10 10 m 50 10 l 50 50 l h h S");
        Assert.Equal(1, verbs.Count(v => v == PathVerb.Close));
        const string content = "0 0 1 RG 4 w 10 10 m 50 10 l 50 50 l h 10 90 l S";
        using (var b = B84_Render(content))
            Assert.True(B84_Blue(b, 10, 70));
        var (doc, list) = InterpreterTests.Page(content);
        using (doc)
        {
            using var path = new SKPath();
            SkiaRenderer.AppendPath(list, list.Items.Span[0].Path, path);
            using var iterator = path.CreateRawIterator();
            var points = new SKPoint[4];
            var starts = new List<SKPoint>();
            SKPathVerb verb;
            while ((verb = iterator.Next(points)) != SKPathVerb.Done)
                if (verb == SKPathVerb.Move) starts.Add(points[0]);
            Assert.Equal(2, starts.Count);
            Assert.Equal(starts[0], starts[1]);
        }
    }

    /// <summary>
    /// 8.5.2.1 sentences 32–34, 42–43: re is a complete subpath, the same as m, three l and h; the current point afterwards is its corner
    /// (x, y) — a line drawn after it begins there.
    /// </summary>
    [Fact]
    public void C85_Re_IsMoveThreeLinesAndClose()
    {
        Assert.Equal(C85_OnlyPath("10 20 30 40 re S"), C85_OnlyPath("10 20 m 40 20 l 40 60 l 10 60 l h S"), new C85_PathComparer());
        using var b = B84_Render("0 0 1 RG 4 w 10 20 30 40 re 190 20 l S");
        Assert.True(B84_Blue(b, 100, 20));
    }

    private sealed class C85_PathComparer : IEqualityComparer<(PathVerb[] Verbs, float[] Points)>
    {
        public bool Equals((PathVerb[] Verbs, float[] Points) x, (PathVerb[] Verbs, float[] Points) y) =>
            x.Verbs.AsSpan().SequenceEqual(y.Verbs) && x.Points.AsSpan().SequenceEqual(y.Points);

        public int GetHashCode((PathVerb[] Verbs, float[] Points) obj) => obj.Verbs.Length;
    }

    /// <summary>
    /// 8.5.2.2 sentences 1–6: a curved segment is the cubic Bézier curve R(t) between the current point and the end — the curve passes through
    /// R(0.5) = (100, 160), not through its control point (20, 180).
    /// </summary>
    [Fact]
    public void C85_Bezier_PassesThroughRHalf_NotTheControlPoint()
    {
        using var b = B84_Render("0 0 1 RG 2 w 20 100 m 20 180 180 180 180 100 c S");
        Assert.True(B84_Blue(b, 100, 160) || B84_Blue(b, 100, 159));
        Assert.True(B84_White(b, 20, 180) && B84_White(b, 25, 175));
    }

    /// <summary>
    /// 8.5.2.2 sentences 12–14: v and y name one control point — v draws as c with the first control point on the start, y as c with the second
    /// on the end (the same pixels).
    /// </summary>
    [Fact]
    public void C85_VAndY_DrawAsCWithACoincidentControlPoint()
    {
        Assert.True(C85_SameAs("0 0 1 RG 2 w 20 20 m 180 20 180 180 v S", "0 0 1 RG 2 w 20 20 m 20 20 180 20 180 180 c S"));
        Assert.True(C85_SameAs("0 0 1 RG 2 w 20 20 m 20 180 180 180 y S", "0 0 1 RG 2 w 20 20 m 20 180 180 180 180 180 c S"));
    }

    /// <summary>
    /// 8.5.2.1 sentences 44–45 (read leniently, S-A A172): all operands of the path operators are numbers — an operator with an operand of
    /// another kind is passed over, as the reference readers do (before, the operand counted as 0 and invented a point;
    /// docs/FEHLERGEDAECHTNIS.md, No. 1134): „/N 150 m“ sets no point at (0, 150), the path begins at its first segment; the same for the other
    /// operators of fixed arity: „0 /X 1 rg“ leaves the red fill, „(x) w“ the width 20.
    /// </summary>
    [Fact]
    public void C85_PathOperandNotANumber_IsPassedOver()
    {
        using (var b = B84_Render("0 0 1 RG 8 w /N 150 m 190 150 l 190 20 l S"))
        {
            Assert.True(B84_White(b, 100, 150));
            Assert.True(B84_Blue(b, 190, 80));
        }
        using (var b = B84_Render("1 0 0 rg 0 /X 1 rg 20 20 160 60 re f 0 0 1 RG 20 w (x) w 20 150 m 180 150 l S"))
        {
            Assert.True(B84_Red(b, 100, 50));
            Assert.True(B84_Blue(b, 100, 157));
        }
        Assert.True(PageInterpreter.LacksOperands("m", [new Operand(PdfName.Get("N")), new Operand(150)]));
        Assert.False(PageInterpreter.LacksOperands("Tf", [new Operand(PdfName.Get("F1")), new Operand(12)]));
    }

    // ------------------------------------------------------------------ 8.5.3

    /// <summary>
    /// 8.5.3.1 sentence 4: a painting operator without a current path (at the start, right after another painting operator) — reading, nothing
    /// fails and nothing is painted (one entry); writing, no stream of the program's own paints without a path.
    /// </summary>
    [Fact]
    public void C85_PaintingWithoutPath()
    {
        var (doc, list) = InterpreterTests.Page("f S 0 0 1 rg 50 50 100 100 re f f");
        using (doc) Assert.Equal(1, list.Count);
        var own = B84_OwnStreams().SelectMany(s => B84_PathViolations(s.Data).Where(v => v.StartsWith("nopath:")).Select(v => $"{s.Name}: {v}")).ToList();
        Assert.Empty(own);
    }

    /// <summary>8.5.3.1 sentences 11–14: s closes and strokes, the same as h S.</summary>
    [Fact]
    public void C85_S_IsCloseAndStroke()
    {
        Assert.True(C85_SameAs("0 0 1 RG 4 w 50 50 m 150 50 l 100 150 l s", "0 0 1 RG 4 w 50 50 m 150 50 l 100 150 l h S"));
        using var b = B84_Render("0 0 1 RG 4 w 50 50 m 150 50 l 100 150 l s");
        Assert.True(B84_Blue(b, 75, 100));
    }

    /// <summary>
    /// 8.5.3.1 sentences 15–18: f fills by the non-zero winding rule and closes open subpaths — an open triangle is filled; two squares in the same
    /// direction, one inside the other, are filled in the middle too.
    /// </summary>
    [Fact]
    public void C85_F_NonZero_ClosesOpenSubpaths()
    {
        using (var b = B84_Render("0 0 1 rg 50 50 m 150 50 l 100 150 l f"))
            Assert.True(B84_Blue(b, 100, 80));
        using var nested = B84_Render("0 0 1 rg 20 20 160 160 re 60 60 80 80 re f");
        Assert.True(B84_Blue(nested, 100, 100));
    }

    /// <summary>
    /// 8.5.3.1 sentences 19–22: F (obsolete) is read like f; the program's own content writes f, never F.
    /// </summary>
    [Fact]
    public void C85_CapitalF_ReadLikeF_NeverWritten()
    {
        Assert.True(C85_SameAs("0 0 1 rg 50 50 100 100 re F", "0 0 1 rg 50 50 100 100 re f"));
        var own = new List<string>();
        foreach (var (name, data, _) in B84_OwnStreams())
        {
            var parser = new ContentParser(data);
            while (parser.Next() is { } op)
                if (op == "F") own.Add(name);
        }
        Assert.Empty(own);
    }

    /// <summary>8.5.3.1 sentences 23–26: f* fills by the even-odd rule — the inner square of two in the same direction stays white.</summary>
    [Fact]
    public void C85_FStar_EvenOdd()
    {
        using var b = B84_Render("0 0 1 rg 20 20 160 160 re 60 60 80 80 re f*");
        Assert.True(B84_White(b, 100, 100));
        Assert.True(B84_Blue(b, 40, 100));
    }

    /// <summary>8.5.3.1 sentences 35–41: B* fills by the even-odd rule and strokes, the same as f* and then S on the same path.</summary>
    [Fact]
    public void C85_BStar_FillEvenOddThenStroke()
    {
        const string path = "20 20 160 160 re 60 60 80 80 re";
        using (var b = B84_Render($"0 0 1 rg 1 0 0 RG 4 w {path} B*"))
        {
            Assert.True(B84_White(b, 100, 100));
            Assert.True(B84_Blue(b, 40, 100));
            Assert.True(B84_Red(b, 60, 100));
        }
        Assert.True(C85_SameAs($"0 0 1 rg 1 0 0 RG 4 w {path} B*", $"0 0 1 rg 1 0 0 RG 4 w {path} f* {path} S"));
    }

    /// <summary>8.5.3.1 sentences 42–47: b closes, fills (non-zero) and strokes, the same as h B — the closing segment is stroked.</summary>
    [Fact]
    public void C85_LowerB_IsCloseFillStroke()
    {
        Assert.True(C85_SameAs("0 0 1 rg 1 0 0 RG 4 w 50 50 m 150 50 l 100 150 l b", "0 0 1 rg 1 0 0 RG 4 w 50 50 m 150 50 l 100 150 l h B"));
        using var b = B84_Render("0 0 1 rg 1 0 0 RG 4 w 50 50 m 150 50 l 100 150 l b");
        Assert.True(B84_Red(b, 75, 100));
    }

    /// <summary>
    /// 8.5.3.1 sentences 48–54: b* closes, fills (even-odd) and strokes, the same as h B* — only the last subpath is closed: the closing segment
    /// of the inner square is stroked, that of the outer one not.
    /// </summary>
    [Fact]
    public void C85_LowerBStar_ClosesOnlyTheLastSubpath()
    {
        const string path = "20 20 m 180 20 l 180 180 l 20 180 l 60 60 m 140 60 l 140 140 l 60 140 l";
        Assert.True(C85_SameAs($"0 0 1 rg 1 0 0 RG 4 w {path} b*", $"0 0 1 rg 1 0 0 RG 4 w {path} h B*"));
        using var b = B84_Render($"0 0 1 rg 1 0 0 RG 4 w {path} b*");
        Assert.True(B84_White(b, 100, 100));
        Assert.True(B84_Blue(b, 40, 100));
        Assert.True(B84_Red(b, 60, 100));
        Assert.True(B84_White(b, 18, 100));   // the outer closing segment (x 20) not stroked
    }

    /// <summary>
    /// 8.5.3.2 sentences 8–9: segments that merely meet get no special treatment — a triangle „closed“ with l has butt ends at (50, 50), with h a
    /// mitre (its tip near (34, 40)).
    /// </summary>
    [Fact]
    public void C85_ClosedWithLInsteadOfH_HasEndsNotACorner()
    {
        using var withL = B84_Render("0 0 1 RG 20 w 0 J 0 j 50 50 m 150 50 l 100 150 l 50 50 l S");
        using var withH = B84_Render("0 0 1 RG 20 w 0 J 0 j 50 50 m 150 50 l 100 150 l h S");
        Assert.True(B84_Blue(withH, 40, 44));
        Assert.True(B84_White(withL, 40, 44));
    }

    /// <summary>
    /// 8.5.3.2 sentence 10: today's behaviour (→ M8): a path is one object for the transparency — a half transparent stroke crossing itself is
    /// as light at the crossing as elsewhere; /SA changes nothing.
    /// </summary>
    [Fact]
    public void C85_SelfCrossingStroke_IsOneObject()
    {
        const string resources = "/ExtGState << /G << /CA 0.5 >> /A << /CA 0.5 /SA true >> >>";
        using var b = B84_Render("/G gs 0 0 1 RG 10 w 20 20 m 180 180 l 180 20 l 20 180 l S", resources);
        var single = B84_At(b, 60, 60);
        var crossing = B84_At(b, 100, 100);
        Assert.InRange(single.Red, (byte)100, (byte)160);
        Assert.InRange(crossing.Red - single.Red, -8, 8);
        using var adjusted = B84_Render("/A gs 0 0 1 RG 10 w 20 20 m 180 180 l 180 20 l 20 180 l S", resources);
        Assert.True(B84_Same(b, adjusted));
    }

    /// <summary>
    /// 8.5.3.2 sentences 11–12: a subpath of one point („m h“) is a filled circle with round caps and nothing with square caps.
    /// </summary>
    [Fact]
    public void C85_PointSubpath_OnlyRoundCapsPaint()
    {
        using var round = B84_Render("1 J 20 w 0 0 1 RG 100 100 m h S");
        Assert.True(B84_Blue(round, 100, 100));
        using var square = B84_Render("2 J 20 w 0 0 1 RG 100 100 m h S");
        Assert.True(B84_White(square, 100, 100));
    }

    /// <summary>
    /// 8.5.3.2 sentences 13–14: dashes of length 0 in a subpath that is not degenerate always get their caps — round dots at x 20, 60, 100 …,
    /// squares in the same places with square caps.
    /// </summary>
    [Fact]
    public void C85_ZeroLengthDashes_GetTheirCaps()
    {
        using var round = B84_Render("0 0 1 RG 20 w 1 J [0 40] 0 d 20 100 m 180 100 l S");
        Assert.True(B84_Blue(round, 60, 100) && B84_Blue(round, 100, 100));
        Assert.True(B84_White(round, 40, 100));
        Assert.True(B84_White(round, 68, 108));
        using var square = B84_Render("0 0 1 RG 20 w 2 J [0 40] 0 d 20 100 m 180 100 l S");
        Assert.True(B84_Blue(square, 60, 100) && B84_Blue(square, 68, 108));
        Assert.True(B84_White(square, 40, 100));
    }

    /// <summary>
    /// 8.5.3.3.1 sentences 1–3: f fills everything the path encloses with the fill colour, disconnected subpaths together, open ones closed; a
    /// last subpath of only „m“ changes nothing.
    /// </summary>
    [Fact]
    public void C85_Fill_AllSubpaths_TrailingMoveIgnored()
    {
        const string triangles = "0 0 1 rg 20 20 m 60 20 l 60 60 l 120 120 m 180 120 l 180 180 l";
        using var b = B84_Render(triangles + " 150 50 m f");
        Assert.True(B84_Blue(b, 50, 30) && B84_Blue(b, 170, 130));
        Assert.True(C85_SameAs(triangles + " 150 50 m f", triangles + " f"));
    }

    /// <summary>
    /// 8.5.3.3.1 sentence 4: today's behaviour (→ M8; design decision pending): a subpath of one point filled paints no pixel.
    /// </summary>
    [Fact]
    public void C85_PointSubpathFilled_PaintsNothing_Today()
    {
        using var b = B84_Render("0 0 1 rg 100.5 100.5 m 100.5 100.5 l f");
        for (var y = 95; y < 106; y++)
            for (var x = 95; x < 106; x++)
                Assert.True(B84_White(b, x, y), $"({x}, {y})");
    }

    /// <summary>
    /// 8.5.3.3.1 sentences 9–12 (also 8.5.3.3.2 sentences 1–5, 8.5.3.3.3 sentences 1–5, 8.5.4 sentences 14–19 and 20–23): non-zero winding for f
    /// and W, even-odd for f* and W* — two squares in the same direction, one inside the other: the middle is blue with f and W, white with f*
    /// and W*; the ring is blue in all four.
    /// </summary>
    [Theory]
    [InlineData("0 0 1 rg 20 20 160 160 re 60 60 80 80 re f", true)]
    [InlineData("0 0 1 rg 20 20 160 160 re 60 60 80 80 re f*", false)]
    [InlineData("20 20 160 160 re 60 60 80 80 re W n 0 0 1 rg 0 0 200 200 re f", true)]
    [InlineData("20 20 160 160 re 60 60 80 80 re W* n 0 0 1 rg 0 0 200 200 re f", false)]
    public void C85_FillRules_ForFillAndClip(string content, bool middle)
    {
        using var b = B84_Render(content);
        Assert.Equal(middle, B84_Blue(b, 100, 100));
        Assert.Equal(!middle, B84_White(b, 100, 100));
        Assert.True(B84_Blue(b, 40, 100));
        Assert.True(B84_White(b, 10, 100));
    }

    /// <summary>
    /// 8.5.3.3.2 sentences 6–10: consequences of the non-zero rule — a five-pointed star is filled with its pentagon; two circles in the same
    /// direction are filled entirely, in opposite directions only the ring.
    /// </summary>
    [Fact]
    public void C85_NonZero_StarAndCircles()
    {
        using (var star = B84_Render("0 0 1 rg 100 180 m 147 35 l 24 125 l 176 125 l 53 35 l h f"))
            Assert.True(B84_Blue(star, 100, 100));
        using (var same = B84_Render("0 0 1 rg " + C85_Circle(100, 100, 80, false) + C85_Circle(100, 100, 40, false) + "f"))
            Assert.True(B84_Blue(same, 100, 100) && B84_Blue(same, 100, 160));
        using var opposite = B84_Render("0 0 1 rg " + C85_Circle(100, 100, 80, false) + C85_Circle(100, 100, 40, true) + "f");
        Assert.True(B84_White(opposite, 100, 100));
        Assert.True(B84_Blue(opposite, 100, 160));
    }

    /// <summary>
    /// 8.5.3.3.3 sentences 6–8: consequences of the even-odd rule — of the star only the points, not the pentagon; of two circles only the ring,
    /// whatever their direction.
    /// </summary>
    [Fact]
    public void C85_EvenOdd_StarAndCircles()
    {
        using (var star = B84_Render("0 0 1 rg 100 180 m 147 35 l 24 125 l 176 125 l 53 35 l h f*"))
        {
            Assert.True(B84_White(star, 100, 100));
            Assert.True(B84_Blue(star, 100, 165));
        }
        foreach (var clockwise in (bool[])[false, true])
        {
            using var circles = B84_Render("0 0 1 rg " + C85_Circle(100, 100, 80, false) + C85_Circle(100, 100, 40, clockwise) + "f*");
            Assert.True(B84_White(circles, 100, 100));
            Assert.True(B84_Blue(circles, 100, 160));
        }
    }

    // ------------------------------------------------------------------ 8.5.4

    /// <summary>
    /// 8.5.4 sentences 1–5: inside the clipping path is what f would fill — an open clipping path is closed: below its diagonal blue, above white.
    /// (A degenerate rectangle: GraphicsNormTests.ClipThatOnlyLooksLikeARectangle_ClipsAsItsPath.)
    /// </summary>
    [Fact]
    public void C85_OpenClippingPath_IsClosedLikeAFill()
    {
        using var b = B84_Render("20 20 m 180 20 l 20 180 l W n 0 0 1 rg 0 0 200 200 re f");
        Assert.True(B84_Blue(b, 50, 50));
        Assert.True(B84_White(b, 150, 150));
    }

    /// <summary>
    /// 8.5.4 sentences 6–8: today's behaviour (→ M8): a transparency group under a clip — outside the clip nothing, inside the group at half
    /// opacity.
    /// </summary>
    [Fact]
    public void C85_GroupUnderAClip_ClippedAsAWhole()
    {
        using var b = B84_Render("20 20 80 80 re W n /G gs /Fm Do", "/ExtGState << /G << /ca 0.5 >> >> /XObject << /Fm 5 0 R >>",
            [B84_Form("0 0 1 rg 0 0 200 200 re f", "/Group << /S /Transparency >>")]);
        Assert.True(B84_White(b, 150, 150));
        var inside = B84_At(b, 50, 50);
        Assert.True(inside.Blue > 240 && inside.Red is > 100 and < 160, $"halb blau erwartet: {inside}");
    }

    /// <summary>
    /// 8.5.4 sentences 11–13: W does not clip the painting operator it belongs to, only what follows — the outer half of the stroke of the clipping
    /// square is blue, the red fill afterwards lies only inside it.
    /// </summary>
    [Fact]
    public void C85_W_ClipsOnlyAfterItsPaintingOperator()
    {
        using var b = B84_Render("0 0 1 RG 20 w 50 50 100 100 re W S 1 0 0 rg 0 0 200 200 re f");
        Assert.True(B84_Blue(b, 45, 100));
        Assert.True(B84_White(b, 30, 30));
        Assert.True(B84_Red(b, 100, 100));
    }
}
