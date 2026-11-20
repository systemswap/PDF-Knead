using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// JPEGs under a bar (Stufe A, decided on 12.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 805): read by the own decoder in their own
/// channels — CMYK stays CMYK — blacked out there and written back losslessly at their own size; only really coarse images (pixels over 2 pt)
/// are refined. Display and redaction read the JPEG the same way, so the page beside the bar looks exactly as before.
/// </summary>
public sealed class RedactJpegTests
{
    private static string PdfJs(string name, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return Path.Combine(dir.FullName, "TestPDFs", "pdfjs", name);
        throw new DirectoryNotFoundException();
    }

    /// <summary>
    /// A CMYK JPEG (its left half under a bar): before, it went the way of the display and came back as RGB; now DeviceCMYK, Flate, its own
    /// size, and the rendered page beside the bar unchanged (the samples beside the bar as decoded:
    /// <c>JpegDecoderTests.Redacted_CmykSamplesBesideTheBar_AsDecoded</c>).
    /// </summary>
    [TestPdfsFact]
    public void CmykJpeg_UnderABar_StaysCmykLossless_RestUnchanged()
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(PdfJs("cmykjpeg.pdf")));
        var page = doc.Pages[0];
        var list = PageInterpreter.Build(doc, page);
        var image = list.Items.ToArray().First(i => i.Kind == DisplayItemKind.Image &&
                                                    list.Images[i.Image].Stream?.Dictionary.Get(PdfName.Filter) is PdfName { Value: "DCTDecode" });
        var source = list.Images[image.Image].Stream!;
        var size = ((int)source.Dictionary.GetInteger(PdfName.Get("Width"))!, (int)source.Dictionary.GetInteger(PdfName.Get("Height"))!);
        double left = image.Left, right = image.Left + (image.Right - image.Left) / 2;
        using var after = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(left, image.Top, right, image.Bottom))));

        var written = PageInterpreter.Build(after, after.Pages[0]);
        // the image drawn where the original was (whatever way it was written)
        var item = written.Items.ToArray().First(i => i.Kind == DisplayItemKind.Image && i.Right > image.Left && i.Left < image.Right &&
                                                      i.Bottom > image.Top && i.Top < image.Bottom);
        var blanked = written.Images[item.Image].Stream!;
        Assert.Equal("FlateDecode", blanked.Dictionary.GetName(PdfName.Filter)?.Value);
        Assert.Equal(4, ColorSpace.Create(blanked.Dictionary.Get(PdfName.Get("ColorSpace")), after.Pages[0].Resources, after).Components);
        Assert.Equal(size, ((int)blanked.Dictionary.GetInteger(PdfName.Get("Width"))!, (int)blanked.Dictionary.GetInteger(PdfName.Get("Height"))!));

        using var before = ContentImageTests.Render(doc, page);
        using var now = ContentImageTests.Render(after, after.Pages[0]);
        var box = page.CropBox;
        var scale = before.Width / box.Width;
        var differ = 0;
        for (var y = (int)((box.Top - image.Bottom) * scale) + 1; y < (int)((box.Top - image.Top) * scale) - 1; y++)
            for (var x = (int)(right * scale) + 2; x < (int)(image.Right * scale) - 1; x++)
            {
                SKColor a = before.GetPixel(x, y), b = now.GetPixel(x, y);
                if (a != b) differ++;
            }
        Assert.Equal(0, differ);
    }

    /// <summary>
    /// An image with pixels of 1.5 pt is no longer refined (only above <see cref="ImageBlanker.RefineAbove"/> = 2 pt): it keeps its 100 × 100
    /// pixels, the bar blackens the pixels it touches. Before (1 pt) it came back refined sixfold, 600 × 600.
    /// </summary>
    [Fact]
    public void PixelsBelowTwoPoints_NotRefined()
    {
        var pdf = TestPdf.Simple(1);
        var pixels = new byte[100 * 100 * 3];
        new Random(7).NextBytes(pixels);
        var image = pdf.Add(TestPdf.FlateStream(pixels, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(100)), ("Height", PdfInteger.Of(100)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8)))));
        pdf.Replace(4, TestPdf.FlateStream("q 150 0 0 150 100 300 cm /Im0 Do Q"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image))))));
        using var doc = PdfDocument.Open(pdf.Build());
        using var saved = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(130, 330, 160, 360))));
        var copy = Enumerable.Range(1, saved.ObjectCount + 8).Select(saved.GetObject).OfType<PdfStream>()
            .Single(s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image");
        Assert.Equal((100, 100), ((int)copy.Dictionary.GetInteger(PdfName.Get("Width"))!, (int)copy.Dictionary.GetInteger(PdfName.Get("Height"))!));
    }
}
