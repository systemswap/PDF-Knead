using System.Globalization;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Content;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// A test plan with known dimensions (docs/FEHLERGEDAECHTNIS.md, No. 583): a rectangle 4 m × 3 m, a right triangle with 30°, a circle with
/// r = 2.5 m, a polyline of 6.5 m — drawn at 1:100 and 1:50, on a rotated page, without a scale (the paper), with /UserUnit 2, and with two
/// viewports. Clicks beside the corners snap onto the drawing; what „Messen“ shows must be the real size. The expected values come from the
/// geometry, not from PDF-Knead. With PDFKNEAD_MESSPLAN=folder the plan is written there (to measure it in a reference reader as well).
/// </summary>
public sealed class MeasurePlanTests
{
    /// <summary>A page of the plan: its ratio (1:R), user unit, whether the scale is in the file, rotation, and a detail viewport 1:20.</summary>
    internal sealed record PlanPage(string Title, double Ratio, double UserUnit, bool Scale, int Rotate = 0, bool Detail = false);

    internal static readonly PlanPage[] Pages =
    [
        new("1:100, Maßstab in der Datei", 100, 1, true),
        new("1:50, Maßstab in der Datei, Seite gedreht", 50, 1, true, Rotate: 90),
        new("1:100 ohne Maßstab (gemessen wird das Papier)", 100, 1, false),
        new("1:100 ohne Maßstab, UserUnit 2", 100, 2, false),
        new("1:200, Maßstab in der Datei, UserUnit 2", 200, 2, true),
        new("links 1:100, rechts Detail 1:20", 100, 1, true, Detail: true),
    ];

    private const double Margin = 30;                       // mm of paper around the drawing
    private static readonly double Tan30 = Math.Tan(Math.PI / 6);

    /// <summary>The figures in metres (real size); x from -0.5 to 11, y from 0 to 9.</summary>
    internal static readonly (string Name, MeasureKind Kind, (double X, double Y)[] Points, double Value, string Text)[] Figures =
    [
        ("Rechteck Breite", MeasureKind.Length, [(0, 0), (4, 0)], 4, "4 m"),
        ("Rechteck Höhe", MeasureKind.Length, [(4, 0), (4, 3)], 3, "3 m"),
        ("Rechteck Diagonale", MeasureKind.Length, [(0, 0), (4, 3)], 5, "5 m"),
        ("Rechteck Fläche", MeasureKind.Area, [(0, 0), (4, 0), (4, 3), (0, 3)], 12, "12 m²; 14 m"),
        ("Dreieck Winkel 30°", MeasureKind.Angle, [(9, 0), (6, 0), (9, 3 * Tan30)], 30, "30°"),
        ("Dreieck rechter Winkel", MeasureKind.Angle, [(6, 0), (9, 0), (9, 3 * Tan30)], 90, "90°"),
        ("Dreieck Hypotenuse", MeasureKind.Length, [(6, 0), (9, 3 * Tan30)], 2 * Math.Sqrt(3), "3.46 m"),
        ("Kreis Radius", MeasureKind.Radius, [(4.5, 6.5), (2, 9), (-0.5, 6.5)], 2.5, "r 2.5 m; ⌀ 5 m"),
        ("Kreisbogen aus vier Punkten", MeasureKind.Radius, [(4.5, 6.5), (2, 9), (-0.5, 6.5), (2, 4)], 2.5, "r 2.5 m; ⌀ 5 m"),
        ("Linienzug", MeasureKind.Polyline, [(6, 4), (8, 4), (8, 5.5), (11, 5.5)], 6.5, "6.5 m"),
    ];

    /// <summary>User space units per metre of the drawing on a page.</summary>
    private static double UnitsPerMetre(double ratio, double userUnit) => 1000 / ratio / Measuring.MillimetresPerPoint / userUnit;

    /// <summary>A point of the drawing (metres) in user space of the page, drawn at 1:<paramref name="ratio"/> from <paramref name="left"/> mm.</summary>
    internal static (double X, double Y) At((double X, double Y) metres, double ratio, double userUnit, double left = Margin) =>
        (((metres.X + 0.5) * 1000 / ratio + left) / Measuring.MillimetresPerPoint / userUnit,
         (metres.Y * 1000 / ratio + Margin + 15) / Measuring.MillimetresPerPoint / userUnit);

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>The drawing at 1:<paramref name="ratio"/>: lines, the triangle, the circle (four Bézier arcs whose ends lie on it), the polyline.</summary>
    private static string Drawing(double ratio, double userUnit, double left = Margin)
    {
        var s = new StringBuilder();
        string P((double X, double Y) m) { var (x, y) = At(m, ratio, userUnit, left); return F(x) + " " + F(y); }
        s.Append(CultureInfo.InvariantCulture, $"{F(0.6 / userUnit)} w 0 0 0.5 RG\n");
        s.Append(P((0, 0))).Append(" m ").Append(P((4, 0))).Append(" l ").Append(P((4, 3))).Append(" l ").Append(P((0, 3))).Append(" l h S\n");
        s.Append(P((6, 0))).Append(" m ").Append(P((9, 0))).Append(" l ").Append(P((9, 3 * Tan30))).Append(" l h S\n");
        const double k = 0.5522847498307936 * 2.5;
        (double, double) C(double x, double y) => (2 + x, 6.5 + y);
        s.Append(P(C(2.5, 0))).Append(" m ");
        s.Append(P(C(2.5, k))).Append(' ').Append(P(C(k, 2.5))).Append(' ').Append(P(C(0, 2.5))).Append(" c ");
        s.Append(P(C(-k, 2.5))).Append(' ').Append(P(C(-2.5, k))).Append(' ').Append(P(C(-2.5, 0))).Append(" c ");
        s.Append(P(C(-2.5, -k))).Append(' ').Append(P(C(-k, -2.5))).Append(' ').Append(P(C(0, -2.5))).Append(" c ");
        s.Append(P(C(k, -2.5))).Append(' ').Append(P(C(2.5, -k))).Append(' ').Append(P(C(2.5, 0))).Append(" c S\n");
        s.Append(P((6, 4))).Append(" m ").Append(P((8, 4))).Append(" l ").Append(P((8, 5.5))).Append(" l ").Append(P((11, 5.5))).Append(" l S\n");
        return s.ToString();
    }

    private static string Label(double x, double y, double size, string text) =>
        $"BT /F1 {F(size)} Tf {F(x)} {F(y)} Td ({text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")}) Tj ET\n";

    private static PdfString S(string text) => new(PdfTextEncoding.Encode(text));

    /// <summary>A rectilinear measure in metres for a drawing at 1:<paramref name="ratio"/> on a page with <paramref name="userUnit"/>.</summary>
    private static PdfDictionary Metres(double ratio, double userUnit) => TestPdf.Dict(("Type", TestPdf.N("Measure")), ("Subtype", TestPdf.N("RL")),
        ("R", S("1:" + F(ratio))),
        ("X", new PdfArray([TestPdf.Dict(("U", S("m")), ("C", new PdfReal(1 / UnitsPerMetre(ratio, userUnit))))])),
        ("D", new PdfArray([TestPdf.Dict(("U", S("m")), ("C", new PdfReal(1)))])),
        ("A", new PdfArray([TestPdf.Dict(("U", S("m²")), ("C", new PdfReal(1)))])));

    /// <summary>The page size in user space units: the drawing (11.5 m × 9 m) with margins, twice for a detail.</summary>
    private static (double Width, double Height) Size(PlanPage p)
    {
        var width = 11.5 * 1000 / p.Ratio + 2 * Margin + (p.Detail ? 11.5 * 1000 / 20 + Margin : 0);
        var height = 9 * 1000 / (p.Detail ? 20 : p.Ratio) + 2 * Margin + 15;
        return (width / Measuring.MillimetresPerPoint / p.UserUnit, height / Measuring.MillimetresPerPoint / p.UserUnit);
    }

    /// <summary>Where the detail (1:20) begins on the paper, in mm.</summary>
    private static double DetailLeft => 11.5 * 1000 / 100 + 2 * Margin;

    /// <summary>The plan as PDF.</summary>
    /// <param name="foreignDimension">
    /// With a line dimension of another program on the first page (Ablaufplan 7.6 h): the top of the rectangle, 4 m, red, /IT /LineDimension
    /// with the scale of the page as /Measure — no own data of PDF Knead.
    /// </param>
    internal static byte[] Build(bool foreignDimension = false)
    {
        var pdf = new TestPdf();
        var catalog = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog"))));
        var pagesNode = pdf.Add(new PdfDictionary());
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica")),
            ("Encoding", TestPdf.N("WinAnsiEncoding"))));
        var kids = new PdfArray();
        foreach (var p in Pages)
        {
            var (w, h) = Size(p);
            var u = p.UserUnit;
            var content = new StringBuilder(Drawing(p.Ratio, u));
            content.Append(Label(Margin / Measuring.MillimetresPerPoint / u, (Margin - 12) / Measuring.MillimetresPerPoint / u, 11 / u, "Prüfplan PDF-Knead: " + p.Title));
            content.Append(Label(Margin / Measuring.MillimetresPerPoint / u, (Margin - 18) / Measuring.MillimetresPerPoint / u, 8 / u,
                "Soll: Rechteck 4 m × 3 m (Diagonale 5 m, Fläche 12 m², Umfang 14 m); Dreieck 30°/90°, Hypotenuse 3,46 m;"));
            content.Append(Label(Margin / Measuring.MillimetresPerPoint / u, (Margin - 23) / Measuring.MillimetresPerPoint / u, 8 / u,
                "Kreis r = 2,5 m (Durchmesser 5 m); Linienzug 6,5 m."));
            var page = TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pagesNode)), ("MediaBox", TestPdf.Box(0, 0, w, h)));
            var viewports = new PdfArray();
            if (p.Scale)
                viewports.Add(TestPdf.Dict(("Type", TestPdf.N("Viewport")), ("BBox", TestPdf.Box(0, 0, p.Detail ? (DetailLeft - Margin / 2) / Measuring.MillimetresPerPoint : w, h)),
                    ("Name", S("Plan 1:" + F(p.Ratio))), ("Measure", Metres(p.Ratio, u))));
            if (p.Detail)
            {
                content.Append(Drawing(20, u, DetailLeft));
                viewports.Add(TestPdf.Dict(("Type", TestPdf.N("Viewport")), ("BBox", TestPdf.Box((DetailLeft - Margin / 2) / Measuring.MillimetresPerPoint, 0, w, h)),
                    ("Name", S("Detail 1:20")), ("Measure", Metres(20, u))));
            }
            if (viewports.Count > 0) page.Set(PdfName.Get("VP"), viewports);
            if (foreignDimension && kids.Count == 0)
            {
                var (ax, ay) = At((0, 3), p.Ratio, u);
                var (bx, by) = At((4, 3), p.Ratio, u);
                var look = TestPdf.FlateStream(Encoding.ASCII.GetBytes($"1 0 0 RG 1 w {F(ax)} {F(ay)} m {F(bx)} {F(by)} l S"),
                    TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(ax - 5, ay - 5, bx + 5, by + 5))));
                var line = TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Line")), ("Rect", TestPdf.Box(ax - 5, ay - 5, bx + 5, by + 5)),
                    ("L", TestPdf.Box(ax, ay, bx, by)), ("IT", TestPdf.N("LineDimension")), ("Measure", Metres(p.Ratio, u)),
                    ("C", new PdfArray([PdfInteger.Of(1), PdfInteger.Of(0), PdfInteger.Of(0)])), ("BS", TestPdf.Dict(("W", PdfInteger.Of(1)))),
                    ("Contents", S("4 m")), ("AP", TestPdf.Dict(("N", TestPdf.Ref(pdf.Add(look))))));
                page.Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(pdf.Add(line))]));
            }
            if (u != 1) page.Set(PdfName.UserUnit, new PdfReal(u));
            if (p.Rotate != 0) page.Set(PdfName.Rotate, PdfInteger.Of(p.Rotate));
            page.Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(font))))));
            page.Set(PdfName.Contents, TestPdf.Ref(pdf.Add(TestPdf.FlateStream(Encoding.Latin1.GetBytes(content.ToString())))));
            kids.Add(TestPdf.Ref(pdf.Add(page)));
        }
        pdf.Replace(pagesNode, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", kids), ("Count", PdfInteger.Of(Pages.Length))));
        ((PdfDictionary)pdf.Get(catalog)).Set(PdfName.Pages, TestPdf.Ref(pagesNode));
        pdf.RootNumber = catalog;
        return pdf.Build();
    }

    /// <summary>The figure's points as the user would click them: a little beside each corner (up to 1.5 mm of paper).</summary>
    internal static (double X, double Y)[] Clicks((double X, double Y)[] metres, double ratio, double userUnit, double left = Margin) =>
        [.. metres.Select((m, i) =>
        {
            var (x, y) = At(m, ratio, userUnit, left);
            var off = 1.5 / Measuring.MillimetresPerPoint / userUnit * (i % 2 == 0 ? 0.7 : -0.6);
            return (x + off, y - off * 0.8);
        })];

    /// <summary>
    /// Every figure on every page: clicked beside its corners, snapped onto the drawing (within 3 mm of paper), measured — the real size. On
    /// the pages without a scale the paper in mm: the size divided by the ratio (also with /UserUnit 2, where before it measured half).
    /// </summary>
    [Fact]
    public void KnownDimensions_MeasureTheirRealSize_OnEveryPage()
    {
        var bytes = Build();
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSPLAN") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "pruefplan-messen.pdf"), bytes);
        }
        using var doc = PdfDocument.Open(bytes);
        var wrong = new List<string>();
        for (var n = 0; n < Pages.Length; n++)
        {
            var p = Pages[n];
            var page = doc.Pages[n];
            Assert.Equal(p.UserUnit, page.UserUnit);
            var list = PageInterpreter.Build(doc, page);
            var scales = PageScales.Of(page.Dictionary);
            var regions = p.Detail ? new[] { (Ratio: p.Ratio, Left: Margin), (Ratio: 20.0, Left: DetailLeft) } : [(Ratio: p.Ratio, Left: Margin)];
            foreach (var (ratio, left) in regions)
                foreach (var (name, kind, metres, value, text) in Figures)
                {
                    var snapped = Clicks(metres, ratio, p.UserUnit, left)
                        .Select(c => MeasureSnap.Find(list, c.X, c.Y, 3 / Measuring.MillimetresPerPoint / p.UserUnit)).ToList();
                    var measured = Measuring.Measure(kind, [.. snapped.Select(s => s.Point)], scales, page.UserUnit);
                    // the paper: metres of the drawing are 1000/ratio mm of paper, square metres that squared
                    var paper = !p.Scale;
                    var expected = kind switch
                    {
                        MeasureKind.Angle => value,
                        MeasureKind.Area when paper => value * Math.Pow(1000 / ratio, 2),
                        _ when paper => value * 1000 / ratio,
                        _ => value,
                    };
                    var expectedText = paper ? PaperText(kind, value, ratio) : text;
                    var where = $"Seite {n + 1} ({p.Title}), 1:{ratio}, {name}";
                    if (snapped.Any(s => s.Kind != SnapKind.Corner)) wrong.Add($"{where}: nicht an jeder Ecke eingerastet");
                    if (measured.Value is not { } v || Math.Abs(v - expected) > 1e-6 * Math.Max(1, expected))
                        wrong.Add($"{where}: {measured.Value} statt {expected}");
                    if (measured.Text != expectedText) wrong.Add($"{where}: „{measured.Text}“ statt „{expectedText}“");
                }
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    /// <summary>The text on the paper (mm with one decimal, cm² with two) for a figure of the drawing at 1:<paramref name="ratio"/>.</summary>
    private static string PaperText(MeasureKind kind, double value, double ratio)
    {
        string Mm(double metres) => (Math.Round(metres * 1000 / ratio, 1)).ToString("#,0.#", CultureInfo.InvariantCulture) + " mm";
        return kind switch
        {
            MeasureKind.Angle => value.ToString("0.#", CultureInfo.InvariantCulture) + "°",
            MeasureKind.Area => Math.Round(value * Math.Pow(1000 / ratio, 2) / 100, 2).ToString("#,0.##", CultureInfo.InvariantCulture) + " cm²; " + Mm(14),
            MeasureKind.Radius => "r " + Mm(value) + "; ⌀ " + Mm(2 * value),
            _ => Mm(value),
        };
    }
}
