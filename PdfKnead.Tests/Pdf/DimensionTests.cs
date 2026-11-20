using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Measurements saved as dimensions (Ablaufplan 7.6 f; docs/FEHLERGEDAECHTNIS.md, No. 587): on the test plan (1:100) a length, a polyline,
/// an area, an arc and an angle as dimensions, saved and opened again. What another program sees — the annotation, its /IT, its points and
/// its /Measure (ISO 32000-2, 12.5.6.7, 12.5.6.9, 12.9) — measures the real size again; PDF Knead reads them back as its elements. With
/// PDFKNEAD_MESSPLAN=folder the file is written there (to check it with the Arlington checkers and in a reference reader).
/// </summary>
public sealed class DimensionTests
{
    private static readonly double Tan30 = Math.Tan(Math.PI / 6);

    private static Measurement Measure(IReadOnlyList<Viewport> scales, MeasureKind kind, params (double X, double Y)[] metres) =>
        Measuring.Measure(kind, [.. metres.Select(m => MeasurePlanTests.At(m, 100, 1))], scales);

    private static (double X, double Y)[] Pairs(PdfArray array) =>
        [.. Enumerable.Range(0, array.Count / 2).Select(i => (array.GetNumber(2 * i)!.Value, array.GetNumber(2 * i + 1)!.Value))];

    [Fact]
    public void SavedDimensions_OtherProgramsMeasureTheSame_ReadBackAsElements()
    {
        using var doc = PdfDocument.Open(MeasurePlanTests.Build());
        var scales = PageScales.Of(doc.Pages[0].Dictionary);
        var measurements = new[]
        {
            Measure(scales, MeasureKind.Length, (0, 0), (4, 3)),
            Measure(scales, MeasureKind.Polyline, (6, 4), (8, 4), (8, 5.5), (11, 5.5)),
            Measure(scales, MeasureKind.Area, (0, 0), (4, 0), (4, 3), (0, 3)),
            Measure(scales, MeasureKind.Radius, (4.5, 6.5), (2, 9), (-0.5, 6.5), (2, 4)),
            Measure(scales, MeasureKind.Angle, (9, 0), (6, 0), (9, 3 * Tan30)),
        };
        Assert.Equal(["5 m", "6.5 m", "12 m²; 14 m", "r 2.5 m; ⌀ 5 m", "30°"], measurements.Select(m => m.Text));
        var dimensions = measurements.Select((m, i) => ElementAppearance.Dimension(m, 0x1F4E9A, 1.5, 0.2, segments: true, angles: true, 0,
            m.Arc is { } arc ? $"Bogen {arc.LengthText} · {arc.SweepText}" : "")!).ToList();
        // the length moved by 20 points: it measures the same, its points went along
        var moved = (DimensionElement)ElementAppearance.Moved(dimensions[0], dimensions[0].Rect with
        {
            Left = dimensions[0].Rect.Left + 20, Right = dimensions[0].Rect.Right + 20,
        });
        Assert.Equal(dimensions[0].PagePoints[0].X + 20, moved.PagePoints[0].X, 6);
        dimensions[0] = moved;

        var items = PageItem.From(doc).ToArray();
        items[0] = items[0].WithElements([.. items[0].Elements, .. dimensions]);
        var path = Path.Combine(Path.GetTempPath(), "pdfknead-bemassung-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            using (var output = File.Create(path)) PdfWriter.Write(doc, items, output, new PdfSaveOptions());
            if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSPLAN") is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
                File.Copy(path, Path.Combine(folder, "pruefplan-bemassungen.pdf"), overwrite: true);
            }
            using var saved = PdfDocument.Open(File.ReadAllBytes(path));
            var annots = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
            var all = Enumerable.Range(0, annots.Count).Select(i => (PdfDictionary)annots.Get(i)).ToList();
            PdfDictionary Find(string subtype, string? it, Func<PdfDictionary, bool>? also = null) =>
                Assert.Single(all, a => a.GetName(PdfName.Subtype)?.Value == subtype && a.GetName(PdfName.Get("IT"))?.Value == it && (also?.Invoke(a) ?? true));
            RectilinearMeasure Scale(PdfDictionary a) => RectilinearMeasure.Read(a.GetDictionary(PdfName.Get("Measure")))!;
            string Contents(PdfDictionary a) => a.GetString(PdfName.Contents)!.ToText();

            // length: a line dimension; another program measures /L with /Measure
            var line = Find("Line", "LineDimension");
            Assert.Equal("5 m", Scale(line).Distance.Format(Scale(line).Length(Pairs(line.GetArray(PdfName.Get("L"))!))!.Value));
            Assert.Equal("5 m", Contents(line));
            Assert.Equal(["ClosedArrow", "ClosedArrow"], Enumerable.Range(0, 2).Select(i => ((PdfName)line.GetArray(PdfName.Get("LE"))!.Get(i)).Value));
            Assert.Equal("1:100", Scale(line).Ratio);

            // polyline and arc: polyline dimensions — the arc finely divided, so that its length is that of the arc
            var polyline = Find("PolyLine", "PolyLineDimension", a => Contents(a) == "6.5 m");
            Assert.Equal("6.5 m", Scale(polyline).Distance.Format(Scale(polyline).Length(Pairs(polyline.GetArray(PdfName.Get("Vertices"))!))!.Value));
            var arc = Find("PolyLine", "PolyLineDimension", a => Contents(a).StartsWith("r ", StringComparison.Ordinal));
            Assert.Equal("11.78 m", Scale(arc).Distance.Format(Scale(arc).Length(Pairs(arc.GetArray(PdfName.Get("Vertices"))!))!.Value));
            Assert.Equal("r 2.5 m; ⌀ 5 m\nBogen 11.78 m · 270°", Contents(arc));
            // the drawing has every sign it shows: Liberation Sans has no „⌀“ (glyph 0), the „Ø“ stands for it
            var drawn = ElementAppearance.For(dimensions[3]).Glyphs;
            Assert.DoesNotContain(0, drawn.Keys);
            Assert.Contains("Ø", drawn.Values);

            // area: a polygon dimension, its area and perimeter again
            var area = Find("Polygon", "PolygonDimension");
            var vertices = Pairs(area.GetArray(PdfName.Get("Vertices"))!);
            Assert.Equal("12 m²", Scale(area).Area.Format(Scale(area).Surface(vertices)!.Value));
            Assert.Equal("14 m", Scale(area).Distance.Format(Scale(area).Length([.. vertices, vertices[0]])!.Value));

            // angle: a polyline without a scale
            var angle = Find("PolyLine", null);
            Assert.Null(angle.GetDictionary(PdfName.Get("Measure")));
            Assert.Equal("30°", Contents(angle));

            // PDF Knead reads them back as its elements, with their points, values and settings
            var own = ElementStore.For(saved, saved.Pages[0]).Elements.OfType<DimensionElement>().ToList();
            Assert.Equal(5, own.Count);
            foreach (var (written, read) in dimensions.Zip(own.OrderBy(d => dimensions.FindIndex(w => w.Kind == d.Kind))))
            {
                Assert.Equal(written.Kind, read.Kind);
                Assert.Equal(written.Text, read.Text);
                Assert.Equal(written.Color, read.Color);
                Assert.Equal(written.ShowSegments, read.ShowSegments);
                Assert.Equal(written.Segments, read.Segments);
                Assert.Equal(written.Corners, read.Corners);
                for (var i = 0; i < written.Points.Count; i++)
                {
                    Assert.Equal(written.PagePoints[i].X, read.PagePoints[i].X, 2);
                    Assert.Equal(written.PagePoints[i].Y, read.PagePoints[i].Y, 2);
                }
                Assert.Equal(written.Measure?.Ratio, read.Measure?.Ratio);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Another program moved points of saved dimensions (Ablaufplan 7.6 h; docs/FEHLERGEDAECHTNIS.md, No. 605): the end of the length and a
    /// corner of the area — only /L and /Vertices, the own data stay as saved. Read back, the points of the file count and the values are
    /// measured anew with the scale of the dimension; the arc, untouched, comes back as saved (its finely divided points match). Before, the
    /// dimensions came back from the own data alone, and the change of the other program was lost when PDF Knead saved again.
    /// </summary>
    [Fact]
    public void OtherProgramMovedPoints_ThePointsOfTheFileCount()
    {
        using var doc = PdfDocument.Open(MeasurePlanTests.Build());
        var scales = PageScales.Of(doc.Pages[0].Dictionary);
        var dimensions = new[]
        {
            Measure(scales, MeasureKind.Length, (0, 0), (4, 3)),
            Measure(scales, MeasureKind.Area, (0, 0), (4, 0), (4, 3), (0, 3)),
            Measure(scales, MeasureKind.Radius, (4.5, 6.5), (2, 9), (-0.5, 6.5), (2, 4)),
        }.Select(m => ElementAppearance.Dimension(m, 0x1F4E9A, 1.5, 0.2, false, false, 0, Measuring.Detail(m))!).ToList();
        var items = PageItem.From(doc).ToArray();
        items[0] = items[0].WithElements([.. items[0].Elements, .. dimensions]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, new PdfSaveOptions());

        using var saved = PdfDocument.Open(output.ToArray());
        var annots = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
        var all = Enumerable.Range(0, annots.Count).Select(i => (PdfDictionary)annots.Get(i)).ToList();
        var line = all.Single(a => a.GetName(PdfName.Subtype)?.Value == "Line");
        var polygon = all.Single(a => a.GetName(PdfName.Subtype)?.Value == "Polygon");
        static PdfArray Array(IEnumerable<(double X, double Y)> points) => new([.. points.SelectMany(p => new PdfObject[] { new PdfReal(p.X), new PdfReal(p.Y) })]);
        // the other program: the end of the line to (8, 6) m, the third corner of the area to (6, 3) m
        var end = MeasurePlanTests.At((8, 6), 100, 1);
        line.Set(PdfName.Get("L"), Array([Pairs(line.GetArray(PdfName.Get("L"))!)[0], end]));
        var corners = Pairs(polygon.GetArray(PdfName.Get("Vertices"))!);
        corners[2] = MeasurePlanTests.At((6, 3), 100, 1);
        polygon.Set(PdfName.Get("Vertices"), Array(corners));

        var read = ElementStore.For(saved, saved.Pages[0]).Elements.OfType<DimensionElement>().ToList();
        var length = read.Single(d => d.Kind == MeasureKind.Length);
        Assert.Equal("10 m", length.Text);
        Assert.Equal(end.X, length.PagePoints[1].X, 3);
        Assert.Equal(end.Y, length.PagePoints[1].Y, 3);
        Assert.Equal("1:100", length.Measure?.Ratio);
        Assert.Equal(0x1F4E9A, length.Color);
        var area = read.Single(d => d.Kind == MeasureKind.Area);
        Assert.StartsWith("15 m²; ", area.Text, StringComparison.Ordinal);
        Assert.Equal(corners[2].X, area.PagePoints[2].X, 3);
        var arc = read.Single(d => d.Kind == MeasureKind.Radius);
        Assert.Equal(dimensions[2].Text, arc.Text);
        Assert.Equal(4, arc.Points.Count);
    }

    /// <summary>
    /// Dimensions of another program, written as the standard says (Ablaufplan 7.6 h, No. 605): a line with /Measure and /IT /LineDimension,
    /// moved in this session, is a length with its colour, width and scale; a polygon with /Measure but no /IT an area; a line without
    /// either, a line with a geospatial scale and an ink annotation are none.
    /// </summary>
    [Fact]
    public void ForeignDimensions_AreReadAsMeasurements()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        PdfDictionary Unit(string u, double c) => TestPdf.Dict(("U", new PdfString(PdfTextEncoding.Encode(u))), ("C", new PdfReal(c)));
        PdfDictionary Scale() => TestPdf.Dict(("Type", TestPdf.N("Measure")), ("Subtype", TestPdf.N("RL")), ("R", new PdfString("1:100"u8.ToArray())),
            ("X", new PdfArray([Unit("m", 0.01)])), ("D", new PdfArray([Unit("m", 1)])), ("A", new PdfArray([Unit("m²", 1)])));
        ForeignAnnotation Foreign(PdfDictionary annot, double moved = 0)
        {
            var rect = PdfRectangle.From(annot.Get(PdfName.Get("Rect")))!.Value;
            return new ForeignAnnotation
            {
                Rect = rect with { Left = rect.Left + moved, Right = rect.Right + moved }, Document = doc, Original = annot, OriginalRect = rect,
                Kind = annot.GetName(PdfName.Subtype)!.Value,
            };
        }

        var line = TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Line")), ("Rect", TestPdf.Box(90, 90, 410, 510)),
            ("L", TestPdf.Box(100, 100, 400, 500)), ("IT", TestPdf.N("LineDimension")), ("Measure", Scale()),
            ("C", new PdfArray([PdfInteger.Of(1), PdfInteger.Of(0), PdfInteger.Of(0)])), ("BS", TestPdf.Dict(("W", PdfInteger.Of(2)))));
        var length = Dimensions.Of(Foreign(line, moved: 10), 0)!;
        Assert.Equal(MeasureKind.Length, length.Kind);
        Assert.Equal("5 m", length.Text);
        Assert.Equal(0xFF0000, length.Color);
        Assert.Equal(2, length.StrokeWidth);
        Assert.Equal("1:100", length.Measure?.Ratio);
        Assert.Equal(110, length.PagePoints[0].X, 3);
        Assert.Equal(410, length.PagePoints[1].X, 3);

        var polygon = TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Polygon")), ("Rect", TestPdf.Box(0, 0, 100, 100)),
            ("Vertices", new PdfArray([.. new double[] { 0, 0, 100, 0, 100, 100, 0, 100 }.Select(v => (PdfObject)new PdfReal(v))])), ("Measure", Scale()));
        var area = Dimensions.Of(Foreign(polygon), 0)!;
        Assert.Equal(MeasureKind.Area, area.Kind);
        Assert.Equal("1 m²; 4 m", area.Text);

        var plain = TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Line")), ("Rect", TestPdf.Box(90, 90, 410, 510)), ("L", TestPdf.Box(100, 100, 400, 500)));
        Assert.Null(Dimensions.Of(Foreign(plain), 0));
        var geo = TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Line")), ("Rect", TestPdf.Box(90, 90, 410, 510)), ("L", TestPdf.Box(100, 100, 400, 500)),
            ("Measure", TestPdf.Dict(("Type", TestPdf.N("Measure")), ("Subtype", TestPdf.N("GEO")))));
        Assert.Null(Dimensions.Of(Foreign(geo), 0));
        var ink = TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Ink")), ("Rect", TestPdf.Box(90, 90, 410, 510)), ("Measure", Scale()));
        Assert.Null(Dimensions.Of(Foreign(ink), 0));
    }

    /// <summary>A measure written and read again formats as before (number formats with their non-default entries; Ablaufplan 7.6 f).</summary>
    [Fact]
    public void Measure_WrittenAndReadAgain_FormatsTheSame()
    {
        var formats = new NumberFormats([new NumberFormat("mi", 1), new NumberFormat("ft", 5280, Thousands: "."),
            new NumberFormat("in", 12, Fraction: 'F', Precision: 8, Exact: true, Before: "", After: "", Prefix: true)]);
        var measure = new RectilinearMeasure("1 in = 0.1 mi", new NumberFormats([new NumberFormat("mi", 0.00139)]), null, formats,
            new NumberFormats([new NumberFormat("acres", 640, Precision: 1000)]), new NumberFormats([new NumberFormat("grd", 400.0 / 360)]), (10, 20), null);
        var again = RectilinearMeasure.Read(measure.ToDictionary())!;
        Assert.Equal(measure.Distance.Format(1.4505), again.Distance.Format(1.4505));
        Assert.Equal(measure.Area.Format(0.123456), again.Area.Format(0.123456));
        Assert.Equal(measure.Angle!.Format(90), again.Angle!.Format(90));
        Assert.Equal(measure.Ratio, again.Ratio);
        Assert.Equal(measure.Origin, again.Origin);
        Assert.Equal(measure.Length([(0, 0), (300, 400)]), again.Length([(0, 0), (300, 400)]));
    }

    /// <summary>
    /// Crafted data of a dimension (docs/FEHLERGEDAECHTNIS.md, No. 596): a polyline of two points with three pieces listed and „Maße“ on — read
    /// back, the pieces that do not fit are left out and the dimension is drawn. Before, drawing reached past the points and failed.
    /// </summary>
    [Fact]
    public void CraftedDimensionData_DoesNotBreakDrawing()
    {
        static PdfString S(string t) => new(PdfTextEncoding.Encode(t));
        var data = TestPdf.Dict(("Art", TestPdf.N("Bemassung")), ("Messart", TestPdf.N("Polyline")), ("Punkte", TestPdf.Box(0, 0, 10, 10)),
            ("Wert", S("1 m")), ("Abschnitte", new PdfArray([S("a"), S("b"), S("c")])), ("Masse", PdfBoolean.True));
        var pdf = TestPdf.Simple(1);
        var annot = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("PolyLine")), ("Rect", TestPdf.Box(100, 100, 200, 200)),
            ("Vertices", TestPdf.Box(100, 100, 110, 110)), (ElementStore.Key.Value, data)));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(annot)]));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(ElementStore.CatalogKey, PdfBoolean.True);
        using var doc = PdfDocument.Open(pdf.Build());
        var dimension = Assert.Single(ElementStore.For(doc, doc.Pages[0]).Elements.OfType<DimensionElement>());
        Assert.Empty(dimension.Segments);
        Assert.NotNull(ElementAppearance.For(dimension).Form);
    }

    /// <summary>
    /// A dimension on a page too large for PDF/A (20 000 points wide): the page shrinks by 2 and its unit doubles; the dimension stays a
    /// dimension — /L, /Rect and /Measure shrink together, so it measures the same — but is no longer an element of PDF Knead, whose points would
    /// stay at the old size and be drawn twice as large (docs/FEHLERGEDAECHTNIS.md, No. 597).
    /// </summary>
    [Fact]
    public void PdfA_LargePage_DimensionStaysADimension()
    {
        var pdf = TestPdf.Simple(1);
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.MediaBox, TestPdf.Box(0, 0, 20000, 842));
        PdfDictionary Unit(string u, double c) => TestPdf.Dict(("U", new PdfString(PdfTextEncoding.Encode(u))), ("C", new PdfReal(c)));
        page.Set(PdfName.Get("VP"), new PdfArray([TestPdf.Dict(("Type", TestPdf.N("Viewport")), ("BBox", TestPdf.Box(0, 0, 20000, 842)), ("Measure", TestPdf.Dict(
            ("Type", TestPdf.N("Measure")), ("Subtype", TestPdf.N("RL")), ("R", new PdfString("1:100"u8.ToArray())),
            ("X", new PdfArray([Unit("m", 0.01)])), ("D", new PdfArray([Unit("m", 1)])), ("A", new PdfArray([Unit("m²", 1)])))))]));
        using var doc = PdfDocument.Open(pdf.Build());
        var measured = Measuring.Measure(MeasureKind.Length, [(500, 100), (1500, 100)], PageScales.Of(doc.Pages[0].Dictionary));
        Assert.Equal("10 m", measured.Text);
        var dimension = ElementAppearance.Dimension(measured, 0x1F4E9A, 1.5, 0.2, false, false, 0)!;
        var items = PageItem.From(doc).ToArray();
        items[0] = items[0].WithElements([.. items[0].Elements, dimension]);
        using var plain = new MemoryStream();
        PdfWriter.Write(doc, items, plain, new PdfSaveOptions());
        var result = PdfATests.Write(plain.ToArray());
        using var saved = PdfDocument.Open(result.Bytes);
        Assert.Equal(2, saved.Pages[0].Dictionary.GetNumber(PdfName.UserUnit));
        Assert.Empty(ElementStore.For(saved, saved.Pages[0]).Elements.OfType<DimensionElement>());
        var annots = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
        var line = Enumerable.Range(0, annots.Count).Select(i => (PdfDictionary)annots.Get(i)).Single(a => a.GetName(PdfName.Get("IT"))?.Value == "LineDimension");
        Assert.Null(line.GetDictionary(ElementStore.Key));
        var scale = RectilinearMeasure.Read(line.GetDictionary(PdfName.Get("Measure")))!;
        var l = Pairs(line.GetArray(PdfName.Get("L"))!);
        Assert.Equal(250, l[0].X, 3);
        Assert.Equal("10 m", scale.Distance.Format(scale.Length(l)!.Value));
    }
}
