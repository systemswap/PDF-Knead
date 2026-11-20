using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>Scales of pages (ISO 32000-2, 12.9): viewports, measures, number formats; kept when pages are converted (docs/FEHLERGEDAECHTNIS.md, No. 579).</summary>
public sealed class ScaleTests
{
    private static PdfString S(string text) => new(PdfTextEncoding.Encode(text));

    private static PdfDictionary Unit(string label, double factor, params (string Key, PdfObject Value)[] more) =>
        TestPdf.Dict([("U", S(label)), ("C", new PdfReal(factor)), .. more]);

    /// <summary>A measure in metres: one point of user space is <paramref name="metresPerPoint"/> m.</summary>
    private static PdfDictionary Metres(double metresPerPoint, params (string Key, PdfObject Value)[] more) => TestPdf.Dict([
        ("Type", TestPdf.N("Measure")), ("Subtype", TestPdf.N("RL")), ("R", S("1:100")),
        ("X", new PdfArray([Unit("m", metresPerPoint)])), ("D", new PdfArray([Unit("m", 1)])), ("A", new PdfArray([Unit("m²", 1)])), .. more]);

    /// <summary>The example of 12.9.2: 1.4505 miles as miles, feet and inches to an eighth.</summary>
    [Fact]
    public void NumberFormat_SeveralUnits_AsTheExampleOfTheStandard()
    {
        var formats = NumberFormats.Read(new PdfArray([Unit("mi", 1), Unit("ft", 5280), Unit("in", 12, ("F", TestPdf.N("F")), ("D", PdfInteger.Of(8)))]))!;
        Assert.Equal("1 mi 2,378 ft 7 5/8 in", formats.Format(1.4505));
    }

    [Fact]
    public void NumberFormat_DecimalsSeparatorsAndRounding()
    {
        var metres = NumberFormats.Read(new PdfArray([Unit("m", 1)]))!;
        Assert.Equal("3.53 m", metres.Format(3.527));
        Assert.Equal("3.5 m", metres.Format(3.5));          // low-order zeros dropped
        Assert.Equal("12,345.6 m", metres.Format(12345.6));
        Assert.Equal("-2 m", metres.Format(-2));
        var german = NumberFormats.Read(new PdfArray([Unit("m", 1, ("RD", S(",")), ("RT", S(".")), ("D", PdfInteger.Of(1000)), ("FD", PdfBoolean.Of(true)))]))!;
        Assert.Equal("12.345,600 m", german.Format(12345.6));
        var rounded = NumberFormats.Read(new PdfArray([Unit("mm", 1000, ("F", TestPdf.N("R")))]))!;
        Assert.Equal("1,235 mm", rounded.Format(1.2345));
    }

    [Fact]
    public void Viewport_LastContainingWins_BoxInEitherOrder()
    {
        var page = TestPdf.Dict(("VP", new PdfArray([
            TestPdf.Dict(("BBox", TestPdf.Box(0, 0, 500, 500)), ("Name", S("Gesamt")), ("Measure", Metres(0.1))),
            TestPdf.Dict(("BBox", TestPdf.Box(300, 300, 100, 100)), ("Name", S("Detail")), ("Measure", Metres(0.01))),
            TestPdf.Dict(("Name", S("ohne Box"))),
        ])));
        var viewports = PageScales.Of(page);
        Assert.Equal(2, viewports.Count);
        Assert.Equal("Detail", PageScales.At(viewports, 200, 200)!.Name);   // overlapping: the last one
        Assert.Equal("Gesamt", PageScales.At(viewports, 400, 50)!.Name);
        Assert.Null(PageScales.At(viewports, 600, 50));
    }

    [Fact]
    public void Measure_LengthAreaAndSeparateY()
    {
        var measure = RectilinearMeasure.Read(Metres(0.05))!;
        Assert.Equal(5, measure.Length([(0, 0), (60, 80)])!.Value, 9);                       // 100 pt
        Assert.Equal("5 m", measure.Distance.Format(measure.Length([(0, 0), (60, 80)])!.Value));
        Assert.Equal(25, measure.Surface([(0, 0), (100, 0), (100, 100), (0, 100)])!.Value, 9);   // 5 m × 5 m
        // y in other units: with /CYX distances work, without it they are not defined
        var withY = RectilinearMeasure.Read(Metres(0.05, ("Y", new PdfArray([Unit("cm", 10)])), ("CYX", new PdfReal(0.01))))!;
        Assert.Equal(Math.Sqrt(3 * 3 + 0.1 * 0.1), withY.Length([(0, 0), (60, 1)])!.Value, 9);   // 3 m along x, 10 cm = 0.1 m along y
        Assert.Null(RectilinearMeasure.Read(Metres(0.05, ("Y", new PdfArray([Unit("cm", 10)]))))!.Length([(0, 0), (1, 1)]));
        Assert.Null(RectilinearMeasure.Read(TestPdf.Dict(("Subtype", TestPdf.N("GEO")))));
    }

    /// <summary>
    /// A plan 20,000 pt wide with a scale (0.01 m per point) and a dimension line: as PDF/A its unit doubles and everything shrinks by 2 —
    /// the viewport, the factor of the measure and the leader lines follow, so the same line of the drawing still measures 20 m. Before: the
    /// viewport stayed as it was and measured half.
    /// </summary>
    [Fact]
    public void PdfA_LargePage_KeepsTheScale()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("1 w 1000 100 m 3000 100 l S"u8.ToArray()));
        var line = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Line")), ("Rect", TestPdf.Box(990, 90, 3010, 140)),
            ("L", TestPdf.Box(1000, 100, 3000, 100)), ("LL", PdfInteger.Of(20)), ("IT", TestPdf.N("LineDimension")), ("Measure", Metres(0.01)),
            ("F", PdfInteger.Of(4)), ("Contents", S("20 m"))));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.MediaBox, TestPdf.Box(0, 0, 20000, 842));
        page.Set(TestPdf.N("VP"), new PdfArray([TestPdf.Dict(("Type", TestPdf.N("Viewport")), ("BBox", TestPdf.Box(0, 0, 20000, 842)), ("Measure", Metres(0.01)))]));
        page.Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(line)]));
        var result = PdfATests.Write(pdf.Build());
        using var doc = PdfDocument.Open(result.Bytes);
        var written = doc.Pages[0].Dictionary;
        Assert.Equal(2, written.GetNumber(PdfName.UserUnit));
        var viewport = Assert.Single(PageScales.Of(written));
        Assert.Equal([0, 0, 10000, 421], viewport.Box);
        Assert.Equal(20, viewport.Measure!.Length([(500, 50), (1500, 50)])!.Value, 9);   // the line of the drawing, now at half the numbers
        var annotation = (PdfDictionary)written.GetArray(PdfName.Get("Annots"))!.Get(0)!;
        Assert.Equal(10, annotation.GetNumber(PdfName.Get("LL")));
        var dimension = RectilinearMeasure.Read(annotation.GetDictionary(PdfName.Get("Measure")))!;
        Assert.Equal(20, dimension.Length([(500, 50), (1500, 50)])!.Value, 9);
    }

    /// <summary>
    /// Writing a scale (calibrating, 7.6 d): appended as the last viewport over the whole page, so that it applies there; a detail viewport of
    /// the file stays (earlier in the order, it is overruled where they overlap); a whole-page viewport written before is replaced.
    /// </summary>
    [Fact]
    public void WithScale_AppendsOverTheWholePage_ReplacesItsOwn()
    {
        var page = TestPdf.Dict(("VP", new PdfArray([TestPdf.Dict(("BBox", TestPdf.Box(0, 0, 100, 100)), ("Name", S("Detail")), ("Measure", Metres(0.5)))])));
        var box = new PdfRectangle(0, 0, 595, 842);
        var once = PageScales.WithScale(page, box, 0.01, "m", "1:28");
        var twice = PageScales.WithScale(once, box, 0.02, "m", "1:57");
        Assert.Single(PageScales.Of(page));                    // the original is not changed
        var viewports = PageScales.Of(twice);
        Assert.Equal(["Detail", "1:57"], viewports.Select(v => v.Name));
        Assert.Equal("1:57", PageScales.At(viewports, 50, 50)!.Measure!.Ratio);   // the last one decides
        Assert.Equal("2 m", Measuring.Measure(MeasureKind.Length, [(0, 200), (100, 200)], viewports).Text);
    }

    /// <summary>
    /// Rounding carries into the larger unit (docs/FEHLERGEDAECHTNIS.md, No. 596): 2.9999 ft in feet and eighths of an inch is „3 ft“, not
    /// „2 ft 12 in“; in metres and whole centimetres 4.999 m is „5 m“; what is left after rounding is nothing — „1 m“, not „1 m 0 cm“.
    /// </summary>
    [Fact]
    public void NumberFormat_RoundingCarriesIntoTheLargerUnit()
    {
        var feet = NumberFormats.Read(new PdfArray([Unit("ft", 1), Unit("in", 12, ("F", TestPdf.N("F")), ("D", PdfInteger.Of(8)))]))!;
        Assert.Equal("3 ft", feet.Format(2.9999));
        Assert.Equal("2 ft 11 7/8 in", feet.Format(2.99));
        var metres = NumberFormats.Read(new PdfArray([Unit("m", 1), Unit("cm", 100, ("D", PdfInteger.Of(1)))]))!;
        Assert.Equal("5 m", metres.Format(4.999));
        Assert.Equal("1 m", metres.Format(1.001));
        Assert.Equal("1 m 2 cm", metres.Format(1.02));
        var rounded = NumberFormats.Read(new PdfArray([Unit("mi", 1), Unit("ft", 5280, ("F", TestPdf.N("R")))]))!;
        Assert.Equal("2 mi", rounded.Format(1.99999));
    }

    /// <summary>
    /// Factors of 0, below 0, not finite or absurdly large measure nothing (No. 596): the number format is not read; a value that overflows
    /// gives no text instead of „In,fin,ity.NaN m“.
    /// </summary>
    [Fact]
    public void NumberFormat_AbsurdFactors_MeasureNothing()
    {
        Assert.Null(NumberFormat.Read(Unit("m", 0)));
        Assert.Null(NumberFormat.Read(Unit("m", -1)));
        Assert.Null(NumberFormat.Read(Unit("m", 1e308)));
        var feet = NumberFormats.Read(new PdfArray([Unit("ft", 1), Unit("in", -12)]))!;
        Assert.Single(feet.Units);   // the second unit is left out
        Assert.Equal("", NumberFormats.Read(new PdfArray([Unit("m", 1e12)]))!.Format(1e300));
        var huge = Measuring.Measure(MeasureKind.Length, [(0, 0), (1e300, 0)], PageScales.Of(TestPdf.Dict(("VP", new PdfArray([TestPdf.Dict(
            ("BBox", TestPdf.Box(-1e301, -1e301, 1e301, 1e301)), ("Measure", Metres(1e12)))])))));
        Assert.Null(huge.Value);
        Assert.Equal("", huge.Text);
    }
}
