using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Inserted images: saved at 300 dpi of the final size (Lanczos), sharpened, with opacity; with the aspect-ratio lock open the
/// image stretches; the display marker never goes into the file; when reopened the image is not sharpened again.
/// See KONZEPT.md, section 5.13.
/// </summary>
public sealed class PictureTests
{
    private static RgbaImage Photo(int w, int h)
    {
        var image = RgbaImage.Blank(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2], image.Pixels[i + 3]) = ((byte)(x * 255 / w), (byte)(y * 255 / h), (byte)((x ^ y) & 0xFF), 255);
            }
        return image;
    }

    private static PdfDocument Save(PdfDocument main, IReadOnlyList<PageItem> pages, PdfSaveOptions? options = null)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(main, pages, output, options);
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { Length: > 0 } folder)
            File.WriteAllBytes(Path.Combine(folder, $"bilder-{(options?.Flatten == true ? "flach" : "anmerkung")}.pdf"), output.ToArray());
        return PdfDocument.Open(output.ToArray());
    }

    private static IEnumerable<PdfStream> Images(PdfDocument doc)
    {
        for (var i = 1; i < 200; i++)
            if (doc.GetObject(i) is PdfStream { } s && s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image" && s.Dictionary.GetName(PdfName.Get("ColorSpace"))?.Value == "DeviceRGB")
                yield return s;
    }

    [Fact]
    public void Saved_At300Dpi_WithoutDisplayMark_SettingsComeBack()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        // 4000 × 3000 pixels in 2 × 1.5 inches: saved as 600 × 450
        var picture = new ImageElement { Rect = new PdfRectangle(100, 500, 244, 608), Image = Photo(4000, 3000), Sharpness = 0.5, Opacity = 0.7 };
        Assert.Equal((600, 450), ElementAppearance.SavedPixels(picture));
        // A small image stays as it is
        var small = new ImageElement { Rect = new PdfRectangle(300, 500, 444, 644), Image = Photo(100, 100) };
        Assert.Equal((100, 100), ElementAppearance.SavedPixels(small));
        // Display: the original with the marker; saving: without it
        var shown = ElementAppearance.For(picture).Form.Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("XObject"))!.GetStream(PdfName.Get("Im1"))!;
        Assert.Equal(4000, shown.Dictionary.GetInteger(PdfName.Get("Width")));
        Assert.NotNull(shown.Dictionary.GetNumber(ElementAppearance.DisplayKey));
        Assert.Equal(0.5, ElementAppearance.DisplaySharpness(shown));
        // The same marker in a foreign file does not count
        var foreign = new PdfStream(new PdfDictionary(), []);
        foreign.Dictionary.Set(ElementAppearance.DisplayKey, new PdfReal(0.5));
        Assert.Null(ElementAppearance.DisplaySharpness(foreign));

        var page = PageItem.From(original)[0].WithElements([picture, small]);
        using var saved = Save(original, [page]);
        var widths = Images(saved).Select(s => (int)s.Dictionary.GetInteger(PdfName.Get("Width"))!.Value).OrderBy(w => w).ToList();
        Assert.Contains(600, widths);
        Assert.Contains(100, widths);
        Assert.DoesNotContain(4000, widths);
        Assert.All(Images(saved), s => Assert.Null(s.Dictionary.GetNumber(ElementAppearance.DisplayKey)));

        var back = PageItem.From(saved)[0].Elements;
        var p = Assert.IsType<ImageElement>(back[0]);
        Assert.Equal((600, 450, true, 0.7, 0.0), (p.Image.Width, p.Image.Height, p.KeepAspect, p.Opacity, p.Sharpness));   // already saved sharpened
        Assert.Equal(100, Assert.IsType<ImageElement>(back[1]).Image.Width);
    }

    [Fact]
    public void OpenChain_Stretches_Opacity_LooksTheSameFlattened()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        var red = RgbaImage.Blank(40, 10);
        for (var i = 0; i < red.Pixels.Length; i += 4) (red.Pixels[i], red.Pixels[i + 3]) = (255, 255);
        // 40 × 10 pixels in a 100 × 100 rectangle: fitted with the lock (100 × 25), stretched with the lock open (100 × 100)
        var fitted = new ImageElement { Rect = new PdfRectangle(50, 400, 150, 500), Image = red };
        var stretched = fitted with { Rect = new PdfRectangle(250, 400, 350, 500), KeepAspect = false, Opacity = 0.5 };
        var page = PageItem.From(original)[0].WithElements([fitted, stretched]);
        using var saved = Save(original, [page]);
        using var flat = Save(saved, PageItem.From(saved), new PdfSaveOptions { Flatten = true });
        foreach (var doc in new[] { saved, flat })
        {
            using var image = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, doc.Pages[0]), doc, doc.Pages[0], 1, new ImageCache());
            var device = PageGeometry.PageToDevice(doc.Pages[0], 1);
            SKColor At(double x, double y)
            {
                var pt = device.Transform(x, y);
                return image.GetPixel((int)pt.X, (int)pt.Y);
            }
            Assert.Equal(SKColors.White, At(100, 410));                        // fitted: free at top and bottom
            Assert.True(At(100, 450).Red > 240 && At(100, 450).Green < 20);    // red in the middle
            var half = At(300, 410);                                            // stretched to the bottom, half opaque: pink
            Assert.True(half.Red > 240 && half.Green is > 110 and < 145, half.ToString());
        }
    }
}
