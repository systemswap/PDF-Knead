using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The label of a measurement's value can be moved (decided on 10.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 703): within an area with at most
/// half outside, on a line with a tenth on either side and over it, within an angle — and it stays where it was moved when saved, in the own
/// data and for a line in /CO (ISO 32000-2, 12.5.6.7).
/// </summary>
public sealed class MeasureLabelTests
{
    [Fact]
    public void Area_TheMiddleStaysInsideOrOnTheEdge()
    {
        (double, double)[] square = [(0, 0), (100, 0), (100, 100), (0, 100)];
        Assert.Equal((50.0, 50.0), MeasureLabel.Clamp(MeasureKind.Area, square, (50, 50), 40, 10));
        Assert.Equal((100.0, 50.0), MeasureLabel.Clamp(MeasureKind.Area, square, (150, 50), 40, 10));   // half of it outside, no further
        Assert.Equal((0.0, 0.0), MeasureLabel.Clamp(MeasureKind.Area, square, (-20, -30), 40, 10));
    }

    [Fact]
    public void Line_ATenthOnEitherSideAndOverIt()
    {
        (double, double)[] line = [(0, 0), (100, 0)];
        // across: the line through the label with a tenth of its height (10) beyond it — the middle at most 4 from the line
        Assert.Equal((50.0, 4.0), MeasureLabel.Clamp(MeasureKind.Length, line, (50, 30), 40, 10));
        Assert.Equal((50.0, -4.0), MeasureLabel.Clamp(MeasureKind.Length, line, (50, -30), 40, 10));
        // along: a tenth of its width (40) still over the line — the middle at most 16 beyond the end
        Assert.Equal((116.0, 0.0), MeasureLabel.Clamp(MeasureKind.Length, line, (200, 0), 40, 10));
        // turned along a slanted line: the same in its direction
        (double, double)[] slanted = [(0, 0), (100, 100)];
        var turned = MeasureLabel.Clamp(MeasureKind.Length, slanted, (0, 100), 40, 10, Math.PI / 4);
        Assert.Equal(4, Math.Abs(turned.X * -Math.Sqrt(0.5) + turned.Y * Math.Sqrt(0.5)), 6);
        // a polyline: the nearest piece counts
        (double, double)[] polyline = [(0, 0), (100, 0), (100, 100)];
        Assert.Equal((104.0, 60.0), MeasureLabel.Clamp(MeasureKind.Polyline, polyline, (130, 60), 10, 40));
    }

    [Fact]
    public void Angle_TheMiddleBetweenTheLegs()
    {
        (double, double)[] angle = [(100, 0), (0, 0), (0, 100)];
        Assert.Equal((50.0, 50.0), MeasureLabel.Clamp(MeasureKind.Angle, angle, (50, 50), 20, 10));
        Assert.Equal((0.0, 50.0), MeasureLabel.Clamp(MeasureKind.Angle, angle, (-10, 50), 20, 10));
        Assert.Equal((0.0, 0.0), MeasureLabel.Clamp(MeasureKind.Angle, angle, (-10, -10), 20, 10));
    }

    private static (DimensionElement Dimension, PdfDictionary Annotation, DimensionElement Read) SavedAndRead(MeasureKind kind, (double X, double Y)[] points,
        (double X, double Y) labelAt)
    {
        var measured = Measuring.Measure(kind, points, []);
        var dimension = ElementAppearance.Dimension(measured, 0x1F4E9A, 1.5, 0.2, false, false, 0, labelAt: labelAt)!;
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var items = PageItem.From(doc).ToArray();
        items[0] = items[0].WithElements([.. items[0].Elements, dimension]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, new PdfSaveOptions { CompressStreams = false, ObjectStreams = false });
        var saved = PdfDocument.Open(output.ToArray());
        var annotation = (PdfDictionary)saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0)!;
        var read = PageItem.From(saved)[0].Elements.OfType<DimensionElement>().Single();
        return (dimension, annotation, read);
    }

    /// <summary>An area's label moved: the dimension keeps it there, saved in the own data and read back; outside the area it stops at the edge.</summary>
    [Fact]
    public void AreaDimension_LabelMoved_StaysThereWhenSavedAndOpened()
    {
        (double, double)[] square = [(100, 100), (300, 100), (300, 250), (100, 250)];
        var (dimension, _, read) = SavedAndRead(MeasureKind.Area, square, (130, 120));
        Assert.Equal((130.0, 120.0), (Math.Round(dimension.PageLabelAt!.Value.X, 6), Math.Round(dimension.PageLabelAt!.Value.Y, 6)));
        Assert.Equal(130, read.PageLabelAt!.Value.X, 3);
        Assert.Equal(120, read.PageLabelAt!.Value.Y, 3);
        var outside = ElementAppearance.Dimension(Measuring.Measure(MeasureKind.Area, square, []), 0x1F4E9A, 1.5, 0.2, false, false, 0, labelAt: (400, 180))!;
        Assert.Equal(300, outside.PageLabelAt!.Value.X, 6);   // on the edge: half of it outside
        Assert.Equal(180, outside.PageLabelAt!.Value.Y, 6);
    }

    /// <summary>
    /// A line dimension's caption moved along and away from the line: /CO says the offset from where it sits by itself (along, across); another
    /// program's line dimension with /CO comes in with its label there.
    /// </summary>
    [Fact]
    public void LineDimension_CaptionMoved_WrittenAsCO_ReadFromCO()
    {
        (double, double)[] line = [(100, 300), (300, 300)];
        var home = ElementAppearance.CaptionHome(line[0], line[1], 1.5, out _, out _);
        // 40 to the right, 2 above the line (within the rules: a tenth of the label still below it)
        var (_, annotation, _) = SavedAndRead(MeasureKind.Length, line, (home.X + 40, 302));
        var co = annotation.GetArray(PdfName.Get("CO"))!;
        Assert.Equal(40, co.GetNumber(0)!.Value, 3);
        Assert.Equal(302 - home.Y, co.GetNumber(1)!.Value, 3);

        // the same as another program writes it (without the own data)
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var annot = TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Line")), ("Rect", TestPdf.Box(90, 280, 310, 320)),
            ("L", TestPdf.Box(100, 300, 300, 300)), ("IT", TestPdf.N("LineDimension")), ("Cap", PdfBoolean.Of(true)), ("CP", TestPdf.N("Top")),
            ("CO", new PdfArray([new PdfReal(40), new PdfReal(302 - home.Y)])), ("C", new PdfArray([PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(1)])));
        var foreign = new ForeignAnnotation
        {
            Rect = new PdfRectangle(90, 280, 310, 320), Document = doc, Original = annot, OriginalRect = new PdfRectangle(90, 280, 310, 320), Kind = "Line",
        };
        var other = Dimensions.Of(foreign, 0)!;
        // /CO counts from where the caption sits by itself for that line (its width 1, without /BS)
        var otherHome = ElementAppearance.CaptionHome(line[0], line[1], 1, out _, out _);
        Assert.Equal(otherHome.X + 40, other.PageLabelAt!.Value.X, 3);
        Assert.Equal(otherHome.Y + (302 - home.Y), other.PageLabelAt!.Value.Y, 3);
    }
}
