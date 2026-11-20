using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Shapes: saved as PDF annotations (Square, Circle, Line, Polygon) with a finished appearance stream, read back on opening as
/// elements with all settings; other programs see the same and find the points in /L or /Vertices. See KONZEPT.md, section 5.13.
/// </summary>
public sealed class ShapeTests
{
    private static ShapeElement Shape(ShapeKind kind, double left, double bottom, double width = 120, double height = 80) =>
        new() { Kind = kind, Rect = new PdfRectangle(left, bottom, left + width, bottom + height), StrokeColor = 0xC00000, FillColor = 0xFFF6CC, StrokeWidth = 3 };

    private static PdfDocument Save(PdfDocument main, IReadOnlyList<PageItem> pages, PdfSaveOptions? options = null)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(main, pages, output, options);
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { Length: > 0 } folder)
            File.WriteAllBytes(Path.Combine(folder, $"formen-{(options?.Flatten == true ? "flach" : "anmerkungen")}.pdf"), output.ToArray());
        return PdfDocument.Open(output.ToArray());
    }

    private static IReadOnlyList<ShapeElement> AllKinds() =>
    [
        Shape(ShapeKind.Rectangle, 40, 680) with { Dashed = true },
        Shape(ShapeKind.Ellipse, 200, 680) with { Opacity = 0.5 },
        Shape(ShapeKind.Triangle, 360, 680) with { FillColor = null },
        Shape(ShapeKind.Diamond, 40, 560),
        Shape(ShapeKind.Star, 200, 560, 100, 100) with { StrokeColor = null, FillColor = 0x1F4E9A },
        Shape(ShapeKind.Callout, 360, 560),
        Shape(ShapeKind.Line, 40, 440) with { Start = (0.1, 0.2), End = (0.9, 0.8) },
        Shape(ShapeKind.Arrow, 200, 440) with { Start = (0, 0.5), End = (1, 0.5), StrokeWidth = 2 },
        Shape(ShapeKind.Rectangle, 360, 440) with { Rotation = 90 },
    ];

    [Fact]
    public void Saved_AsAnnotations_ComeBackWithAllSettings()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        var shapes = AllKinds();
        var page = PageItem.From(original)[0].WithElements([.. shapes]);
        using var saved = Save(original, [page]);

        // Ordinary annotations that other programs know
        var annots = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
        var subtypes = Enumerable.Range(0, annots.Count).Select(i => annots.Get(i).AsDictionary()!.GetName(PdfName.Subtype)!.Value).ToList();
        Assert.Equal(["Square", "Circle", "Polygon", "Polygon", "Polygon", "Polygon", "Line", "Line", "Square"], subtypes);
        var star = annots.Get(4).AsDictionary()!;
        Assert.Equal(20, star.GetArray(PdfName.Get("Vertices"))!.Count);   // 10 corners
        Assert.Empty(star.GetArray(PdfName.Get("C"))!);            // no border
        var arrow = annots.Get(7).AsDictionary()!;
        Assert.Equal("ClosedArrow", ((PdfName)arrow.GetArray(PdfName.Get("LE"))!.Get(1)).Value);
        var l = arrow.GetArray(PdfName.Get("L"))!;
        Assert.Equal((200.0, 480.0, 320.0, 480.0), (l.GetNumber(0)!.Value, l.GetNumber(1)!.Value, l.GetNumber(2)!.Value, l.GetNumber(3)!.Value));
        Assert.Equal("D", annots.Get(0).AsDictionary()!.GetDictionary(PdfName.Get("BS"))!.GetName(PdfName.Get("S"))!.Value);
        Assert.Equal(0.5, annots.Get(1).AsDictionary()!.GetNumber(PdfName.Get("CA"))!.Value, 3);

        // Reopened: the same shapes
        var back = PageItem.From(saved)[0].Elements;
        Assert.Equal(shapes.Count, back.Count);
        for (var i = 0; i < shapes.Count; i++)
        {
            var (a, b) = (shapes[i], Assert.IsType<ShapeElement>(back[i]));
            Assert.Equal((a.Kind, a.StrokeColor, a.FillColor, a.StrokeWidth, a.Dashed, a.Opacity, a.Rotation),
                (b.Kind, b.StrokeColor, b.FillColor, b.StrokeWidth, b.Dashed, b.Opacity, b.Rotation));
            Assert.Equal(a.Start.X, b.Start.X, 6);
            Assert.Equal(a.End.Y, b.End.Y, 6);
            Assert.Equal(a.Rect.Left, b.Rect.Left, 3);
            Assert.Equal(a.Rect.Top, b.Rect.Top, 3);
        }

        // Other programs see the same as PDF Knead
        using var other = SkiaRenderer.RenderPage(PageInterpreter.Build(saved, saved.Pages[0]), saved, saved.Pages[0], 1, new ImageCache());
        using var own = SkiaRenderer.RenderPage(PageInterpreter.Build(saved, saved.Pages[0], withoutOwnElements: true), saved, saved.Pages[0], 1, new ImageCache());
        using (var canvas = new SKCanvas(own)) ElementOverlay.Draw(canvas, saved.Pages[0], back, 1.0);
        var differing = 0;
        for (var y = 0; y < other.Height; y++)
            for (var x = 0; x < other.Width; x++)
                if (Math.Abs(other.GetPixel(x, y).Red - own.GetPixel(x, y).Red) > 24) differing++;
        Assert.True(differing < 30, $"{differing} Pixel weichen ab");

        // Flattened: fixed in the page, no annotations left, looks the same
        using var flat = Save(saved, PageItem.From(saved), new PdfSaveOptions { Flatten = true });
        Assert.Empty(PageItem.From(flat)[0].Elements);
        using var flatImage = SkiaRenderer.RenderPage(PageInterpreter.Build(flat, flat.Pages[0]), flat, flat.Pages[0], 1, new ImageCache());
        differing = 0;
        for (var y = 0; y < other.Height; y++)
            for (var x = 0; x < other.Width; x++)
                if (Math.Abs(other.GetPixel(x, y).Red - flatImage.GetPixel(x, y).Red) > 24) differing++;
        Assert.True(differing < 30, $"flach: {differing} Pixel weichen ab");
    }

    /// <summary>
    /// What another program would redraw from the saved data (here the own generator for annotations without an appearance stream)
    /// lies where the shape is: corners of the diamond, end of the arrow.
    /// </summary>
    [Fact]
    public void RedrawnFromAnnotationData_MatchesTheShape()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        var diamond = Shape(ShapeKind.Diamond, 100, 500, 200, 100) with { FillColor = 0x1F4E9A };
        var page = PageItem.From(original)[0].WithElements([diamond]);
        using var saved = Save(original, [page]);
        var annot = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0).AsDictionary()!;
        annot.Remove(PdfName.Get("AP"));
        using var redrawn = SkiaRenderer.RenderPage(PageInterpreter.Build(saved, saved.Pages[0], withoutOwnElements: false), saved, saved.Pages[0], 1, new ImageCache());
        var device = PageGeometry.PageToDevice(saved.Pages[0], 1);
        SKColor At(double x, double y)
        {
            var p = device.Transform(x, y);
            return redrawn.GetPixel((int)p.X, (int)p.Y);
        }
        Assert.True(At(200, 550).Blue > 120 && At(200, 550).Red < 80, At(200, 550).ToString());   // centre filled
        Assert.Equal(SKColors.White, At(110, 510));                                               // corner outside the diamond
    }

    [Fact]
    public void Appearance_EveryKind_StaysInsideItsBox()
    {
        foreach (var kind in Enum.GetValues<ShapeKind>())
        {
            var shape = Shape(kind, 0, 0, 100, 60) with { StrokeWidth = 4 };
            var form = ElementAppearance.For(shape).Form;
            var text = System.Text.Encoding.ASCII.GetString(form.RawData!);
            foreach (var number in System.Text.RegularExpressions.Regex.Matches(text, @"-?\d+(\.\d+)?").Select(m => double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture)))
                Assert.InRange(number, -0.001, 100.001);
        }
    }

    /// <summary>The arrowhead is proportional to the stroke width; on short arrows it takes at most a third of the length.</summary>
    [Fact]
    public void ArrowHead_ProportionalToStrokeAndLength()
    {
        ShapeElement Arrow(double width) => Shape(ShapeKind.Arrow, 0, 0) with { StrokeWidth = width };
        Assert.Equal((8.0, 4.0), ElementAppearance.ArrowHead(Arrow(2)));       // 2 pt line: 8 × 4
        Assert.Equal((24.0, 12.0), ElementAppearance.ArrowHead(Arrow(6)));     // grows proportionally
        Assert.Equal((4.0, 2.0), ElementAppearance.ArrowHead(Arrow(0.5)));     // thin line: still visible
        Assert.Equal((6.0, 3.0), ElementAppearance.ArrowHead(Arrow(2), 18));   // short arrow: one third
        Assert.Equal((12.0, 6.0), ElementAppearance.ArrowHead(Arrow(6), 9));   // never narrower than two stroke widths
    }
}
