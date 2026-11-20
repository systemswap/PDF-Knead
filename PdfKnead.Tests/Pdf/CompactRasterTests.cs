using System.Diagnostics;
using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>Compact "Save as image" (MRC): text as 1-bit masks, background as JPEG.</summary>
public sealed class CompactRasterTests
{
    private static byte[] Raster(byte[] file, RasterSettings settings)
    {
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        RasterExport.Write(PageItem.From(doc), output, settings);
        return output.ToArray();
    }

    /// <summary>Mean difference (0 … 255) from the original at 100 dpi; <paramref name="edges"/>: only at places with contrast (text edges).</summary>
    private static double Difference(byte[] original, byte[] copy, bool edges = false)
    {
        using var a = PdfDocument.Open(original);
        using var b = PdfDocument.Open(copy);
        using var pa = RasterExport.Render(PageItem.From(a)[0], 100 / 72.0, new ImageCache());
        using var pb = RasterExport.Render(PageItem.From(b)[0], 100 / 72.0, new ImageCache());
        long sum = 0, n = 0;
        for (var y = 1; y < pa.Height - 1; y++)
            for (var x = 1; x < pa.Width - 1; x++)
            {
                var p = pa.GetPixel(x, y);
                if (edges && Math.Abs(p.Red - pa.GetPixel(x + 1, y).Red) < 60 && Math.Abs(p.Red - pa.GetPixel(x, y + 1).Red) < 60) continue;
                var q = pb.GetPixel(x, y);
                sum += Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue);
                n++;
            }
        return sum / (3.0 * Math.Max(1, n));
    }

    /// <summary>Text page: much smaller than JPEG, no text left, text as masks, at edges at least as faithful as JPEG.</summary>
    [Fact]
    public void TextPage_MasksInsteadOfJpeg()
    {
        var original = TestPdf.Simple(1).Build();
        var jpeg = Raster(original, new RasterSettings(Dpi: 300));
        var compact = Raster(original, new RasterSettings(Dpi: 300, Compact: true));
        using var doc = PdfDocument.Open(compact);
        Assert.Empty(PageInterpreter.Build(doc, doc.Pages[0]).Text);
        var text = Encoding.Latin1.GetString(compact);
        Assert.Contains("/ImageMask true", text);
        Assert.Contains("/JBIG2Decode", text);   // masks encoded with JBIG2
        Assert.True(compact.Length < jpeg.Length / 3, $"kompakt {compact.Length}, JPEG {jpeg.Length}");
        var (compactEdges, jpegEdges) = (Difference(original, compact, edges: true), Difference(original, jpeg, edges: true));
        Assert.True(compactEdges <= jpegEdges * 1.5 + 2, $"Kanten: kompakt {compactEdges:0.0}, JPEG {jpegEdges:0.0}");
        Assert.True(Difference(original, compact) < 6, $"gesamt {Difference(original, compact):0.0}");
    }

    /// <summary>Covered text (a white rectangle over it, like a cover-up) stays covered: it then belongs to the background.</summary>
    /// <summary>
    /// 30,000 entries lying over each other (a damaged file: 70,000 took 19 s): keeping the order of the layers would check every pair;
    /// beyond the budget the page is written as a single image, quickly and looking the same (docs/FEHLERGEDAECHTNIS.md, No. 278).
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void ManyOverlappingEntries_SingleImageInsteadOfLayers()
    {
        var pdf = TestPdf.Simple(1);
        // 30,000 letters on one spot (character spacing minus their width)
        var many = "BT /F1 40 Tf -26.68 Tc 100 400 Td (" + new string('X', 30_000) + ") Tj ET";
        var content = pdf.Add(TestPdf.FlateStream(Encoding.ASCII.GetBytes(many)));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Contents, TestPdf.Ref(content));
        var original = pdf.Build();
        var watch = Stopwatch.StartNew();
        var compact = Raster(original, new RasterSettings(Dpi: 150, Compact: true));
        Assert.True(watch.Elapsed.TotalSeconds < 8 * TestKind.Factor, $"{watch.Elapsed.TotalSeconds:0.0} s");
        Assert.DoesNotContain("/JBIG2Decode", Encoding.Latin1.GetString(compact));
        Assert.True(Difference(original, compact) < 6, $"{Difference(original, compact):0.0}");
    }

    [Fact]
    public void CoveredText_StaysCovered()
    {
        var pdf = TestPdf.Simple(1);
        var content = pdf.Add(TestPdf.FlateStream(Encoding.ASCII.GetBytes(
            "BT /F1 40 Tf 72 700 Td (VERDECKT) Tj ET 1 1 1 rg 60 680 400 80 re f BT /F1 40 Tf 72 500 Td (SICHTBAR) Tj ET")));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Contents, TestPdf.Ref(content));
        var original = pdf.Build();
        var compact = Raster(original, new RasterSettings(Dpi: 300, Compact: true));
        Assert.True(Difference(original, compact) < 3, $"{Difference(original, compact):0.0}");
        // The visible text is sharp as a mask
        Assert.Contains("/ImageMask true", Encoding.Latin1.GetString(compact));
    }

    /// <summary>Counts dark pixels (all channels below 100) in an area given in points (y upwards), at 100 dpi.</summary>
    private static int Dark(byte[] file, double left, double bottom, double right, double top)
    {
        using var doc = PdfDocument.Open(file);
        const double s = 100 / 72.0;
        using var bitmap = RasterExport.Render(PageItem.From(doc)[0], s, new ImageCache());
        var count = 0;
        for (var y = (int)((doc.Pages[0].Height - top) * s); y < (int)((doc.Pages[0].Height - bottom) * s); y++)
            for (var x = (int)(left * s); x < (int)(right * s); x++)
                if (bitmap.GetPixel(x, y) is { Red: < 100, Green: < 100, Blue: < 100 }) count++;
        return count;
    }

    /// <summary>
    /// Black text on a small white area (as in CAD plans): black is the most frequent colour, so its mask would come first, and the
    /// white mask on top would swallow the text. The drawing order must be preserved.
    /// </summary>
    [Fact]
    public void TextOnSmallWhiteBox_StaysVisible()
    {
        var pdf = TestPdf.Simple(1);
        var content = pdf.Add(TestPdf.FlateStream(Encoding.ASCII.GetBytes(
            "BT /F1 12 Tf 72 500 Td (Viele schwarze Zeichen machen Schwarz zur ersten Maske) Tj ET " +
            "1 1 1 rg 100 700 22 22 re f 0 0 0 rg BT /F1 18 Tf 104 705 Td (X) Tj ET")));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Contents, TestPdf.Ref(content));
        var original = pdf.Build();
        var compact = Raster(original, new RasterSettings(Dpi: 300, Compact: true));
        var (before, after) = (Dark(original, 100, 700, 122, 722), Dark(compact, 100, 700, 122, 722));
        Assert.True(before > 20 && after > before * 0.7, $"dunkle Pixel im Kästchen: vorher {before}, nachher {after}");
    }

    /// <summary>An own element that was saved and then deleted: compact "Save as image" must not bring it back from the file.</summary>
    [Fact]
    public void DeletedOwnElement_StaysGone()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        var element = new TextElement { Rect = new PdfRectangle(300, 600, 560, 660), Text = "GELOESCHT", FontSize = 40 };
        using var withElement = new MemoryStream();
        PdfWriter.Write(original, [PageItem.From(original)[0].WithElements([element])], withElement);
        using var saved = PdfDocument.Open(withElement.ToArray());
        var item = PageItem.From(saved)[0];
        Assert.Single(item.Elements);
        using var output = new MemoryStream();
        RasterExport.Write([item.WithElements([])], output, new RasterSettings(Dpi: 150, Compact: true));
        Assert.Equal(0, Dark(output.ToArray(), 300, 600, 560, 660));
        Assert.True(Dark(withElement.ToArray(), 300, 600, 560, 660) > 100);
    }

    /// <summary>Page with a photo: the background goes into a JPEG, the text above it into a mask.</summary>
    [Fact]
    public void PhotoAndText_BackgroundJpegPlusMask()
    {
        var file = CompressionWriterTests.WithImage(600, 400, 3, (x, y, c) => (byte)((x * (c + 2) + y * 5 + x * y / 7) & 255), inches: 4, caption: "Bildunterschrift");
        var compact = Raster(file, new RasterSettings(Dpi: 300, Compact: true));
        var text = Encoding.Latin1.GetString(compact);
        Assert.Contains("/DCTDecode", text);
        Assert.Contains("/ImageMask true", text);
        Assert.True(Difference(file, compact) < 8, $"{Difference(file, compact):0.0}");
    }
}
