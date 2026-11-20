using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Polylines and areas with arcs saved as dimensions (Ablaufplan 7.6 j; docs/FEHLERGEDAECHTNIS.md, No. 620). On the test plan (1:100): a half
/// circle of 1 m and 2 m straight (π + 2 m), a square of 3 m with a half circle of 1.5 m on its right (9 + 1.125 π m², 9 + 1.5 π m). In PDF 2.0
/// the dimension carries its curves as /Path (ISO 32000-2, 12.5.6.9), below 2.0 its course finely divided as /Vertices — either way another
/// program measures the arcs; PDF Knead reads them back with their arc points, and a dimension another program changed counts by its points.
/// With PDFKNEAD_MESSPLAN=folder the files are written there (Arlington checkers, reference reader).
/// </summary>
public sealed class DimensionArcTests
{
    private static Measurement Measure(IReadOnlyList<Viewport> scales, MeasureKind kind, int[] arcs, params (double X, double Y)[] metres) =>
        Measuring.Measure(kind, [.. metres.Select(m => MeasurePlanTests.At(m, 100, 1))], scales, arcPoints: arcs);

    private static (double X, double Y)[] Pairs(PdfArray array) =>
        [.. Enumerable.Range(0, array.Count / 2).Select(i => (array.GetNumber(2 * i)!.Value, array.GetNumber(2 * i + 1)!.Value))];

    /// <summary>Writes the two dimensions in <paramref name="version"/> and opens the file again.</summary>
    private static (PdfDocument Saved, List<DimensionElement> Written) Save(string version)
    {
        using var doc = PdfDocument.Open(MeasurePlanTests.Build());
        var scales = PageScales.Of(doc.Pages[0].Dictionary);
        var polyline = Measure(scales, MeasureKind.Polyline, [1], (0, 0), (1, 1), (2, 0), (4, 0));
        var area = Measure(scales, MeasureKind.Area, [2], (5, 0), (8, 0), (9.5, 1.5), (8, 3), (5, 3));
        Assert.Equal(Math.PI + 2, polyline.Value!.Value, 9);
        Assert.Equal(9 + 1.125 * Math.PI, area.Value!.Value, 9);
        var written = new[] { polyline, area }.Select(m => ElementAppearance.Dimension(m, 0x1E7B34, 1.5, 0.2, segments: true, angles: true, 0)!).ToList();
        Assert.All(written, d => Assert.NotEmpty(d.ArcPoints));
        var items = PageItem.From(doc).ToArray();
        items[0] = items[0].WithElements([.. items[0].Elements, .. written]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, new PdfSaveOptions { Version = version });
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSPLAN") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, $"pruefplan-boegen-{version}.pdf"), output.ToArray());
        }
        return (PdfDocument.Open(output.ToArray()), written);
    }

    private static List<PdfDictionary> Annotations(PdfDocument saved)
    {
        var annots = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
        return [.. Enumerable.Range(0, annots.Count).Select(i => (PdfDictionary)annots.Get(i))];
    }

    /// <summary>
    /// Below 2.0: /Vertices with the arcs finely divided — the length another program measures from them is that of the arcs (to a thousandth),
    /// the area of the polygon that of the shape; no /Path (PDF 1.7 does not know it).
    /// </summary>
    [Fact]
    public void Pdf17_FinelyDividedVertices_OtherProgramsMeasureTheArcs()
    {
        var (saved, _) = Save("1.7");
        using (saved)
        {
            var annots = Annotations(saved);
            var polyline = Assert.Single(annots, a => a.GetName(PdfName.Subtype)?.Value == "PolyLine");
            var area = Assert.Single(annots, a => a.GetName(PdfName.Subtype)?.Value == "Polygon");
            Assert.False(polyline.ContainsKey(PdfName.Get("Path")));
            var scale = RectilinearMeasure.Read(polyline.GetDictionary(PdfName.Get("Measure")))!;
            var line = Pairs(polyline.GetArray(PdfName.Get("Vertices"))!);
            Assert.True(line.Length > 4);
            Assert.Equal(Math.PI + 2, scale.Length(line)!.Value, 3);
            var outline = Pairs(area.GetArray(PdfName.Get("Vertices"))!);
            Assert.Equal(9 + 1.125 * Math.PI, scale.Surface(outline)!.Value, 2);
        }
    }

    /// <summary>
    /// 2.0: /Path instead of /Vertices — the first point, a line for each straight piece and Bézier curves for each arc; measured along the
    /// curves, the lengths are those of the arcs; the area dimension ends back at its first point.
    /// </summary>
    [Fact]
    public void Pdf20_PathWithCurves_InsteadOfVertices()
    {
        var (saved, _) = Save("2.0");
        using (saved)
        {
            var annots = Annotations(saved);
            var polyline = Assert.Single(annots, a => a.GetName(PdfName.Subtype)?.Value == "PolyLine");
            var area = Assert.Single(annots, a => a.GetName(PdfName.Subtype)?.Value == "Polygon");
            foreach (var annot in new[] { polyline, area })
            {
                Assert.False(annot.ContainsKey(PdfName.Get("Vertices")));
                var path = annot.GetArray(PdfName.Get("Path"))!;
                Assert.Equal(2, ((PdfArray)path.Get(0)!).Count);
                Assert.Contains(Enumerable.Range(1, path.Count - 1), i => ((PdfArray)path.Get(i)!).Count == 6);
            }
            // the half circle: two quarter curves to the point it goes through and on to its end, then the straight piece
            var steps = polyline.GetArray(PdfName.Get("Path"))!;
            Assert.Equal([2, 6, 6, 2], Enumerable.Range(0, steps.Count).Select(i => ((PdfArray)steps.Get(i)!).Count));
            var scale = RectilinearMeasure.Read(polyline.GetDictionary(PdfName.Get("Measure")))!;
            var divided = Dimensions.FilePoints(polyline)!;
            Assert.Equal(Math.PI + 2, scale.Length(divided)!.Value, 3);
            var shape = Dimensions.FilePoints(area)!;
            Assert.Equal(9 + 1.125 * Math.PI, scale.Surface(shape)!.Value, 2);
        }
    }

    /// <summary>Read back, both are dimensions of PDF Knead with their arc points, values and pieces (in either version).</summary>
    [Theory]
    [InlineData("1.7")]
    [InlineData("2.0")]
    public void ReadBack_WithTheirArcPoints(string version)
    {
        var (saved, written) = Save(version);
        using (saved)
        {
            var read = ElementStore.For(saved, saved.Pages[0]).Elements.OfType<DimensionElement>().OrderBy(d => d.Kind).ToList();
            Assert.Equal(2, read.Count);
            foreach (var (w, r) in written.OrderBy(d => d.Kind).Zip(read))
            {
                Assert.Equal(w.Kind, r.Kind);
                Assert.Equal(w.ArcPoints, r.ArcPoints);
                Assert.Equal(w.Text, r.Text);
                Assert.Equal(w.Segments, r.Segments);
                Assert.Contains(r.Segments, s => s.Contains(" · r ", StringComparison.Ordinal));
                Assert.Equal(w.Corners, r.Corners);
            }
            // measured again from what was read: the same values
            var polyline = read.Single(d => d.Kind == MeasureKind.Polyline);
            // (the scale as written in /Measure: its factor to a few significant digits)
            Assert.Equal(Math.PI + 2, Dimensions.MeasureWith(polyline.Kind, polyline.PagePoints, polyline.Measure, arcPoints: polyline.ArcPoints).Value!.Value, 4);
        }
    }

    /// <summary>
    /// Another program moved the end of the polyline (its /Path or /Vertices, not the own data): the points of the file count — the arc comes
    /// back as its finely divided course, measured as such (the length of the moved shape), without arc points.
    /// </summary>
    [Theory]
    [InlineData("1.7")]
    [InlineData("2.0")]
    public void ChangedByAnotherProgram_ThePointsOfTheFileCount(string version)
    {
        var (saved, _) = Save(version);
        using (saved)
        {
            var polyline = Annotations(saved).Single(a => a.GetName(PdfName.Subtype)?.Value == "PolyLine");
            // the straight piece at the end 1 m longer (1 m of the plan to the right)
            var metre = MeasurePlanTests.At((1, 0), 100, 1).X - MeasurePlanTests.At((0, 0), 100, 1).X;
            if (version == "2.0")
            {
                var path = polyline.GetArray(PdfName.Get("Path"))!;
                var steps = Enumerable.Range(0, path.Count).Select(i => (PdfObject)path.Get(i)!).ToList();
                var last = (PdfArray)steps[^1];
                steps[^1] = new PdfArray([new PdfReal(last.GetNumber(0)!.Value + metre), new PdfReal(last.GetNumber(1)!.Value)]);
                polyline.Set(PdfName.Get("Path"), new PdfArray(steps));
            }
            else
            {
                var points = Pairs(polyline.GetArray(PdfName.Get("Vertices"))!);
                points[^1] = (points[^1].X + metre, points[^1].Y);
                polyline.Set(PdfName.Get("Vertices"), new PdfArray([.. points.SelectMany(p => new PdfObject[] { new PdfReal(p.X), new PdfReal(p.Y) })]));
            }
            var read = ElementStore.For(saved, saved.Pages[0]).Elements.OfType<DimensionElement>().Single(d => d.Kind == MeasureKind.Polyline);
            Assert.Empty(read.ArcPoints);
            var measured = Dimensions.MeasureWith(read.Kind, read.PagePoints, read.Measure);
            Assert.Equal(Math.PI + 3, measured.Value!.Value, 2);
        }
    }
}
