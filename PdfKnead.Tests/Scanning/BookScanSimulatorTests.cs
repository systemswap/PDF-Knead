using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Scanning;
using SkiaSharp;

namespace PdfKnead.Tests.Scanning;

/// <summary>
/// The test pages of the book correction (<see cref="BookScanSimulator"/>): the model behaves as a flatbed scanner does — flat beyond the
/// curl, lines bending towards the optical axis near the spine with optics, compressed and darker at the spine. With
/// <c>PDFKNEAD_BUCHSCANS=&lt;folder&gt;</c> the test pages of the local test collection are written there.
/// </summary>
public sealed class BookScanSimulatorTests
{
    /// <summary>A page of 120 × 160 mm at 100 dpi with dark horizontal lines every 10 mm.</summary>
    private static ScannedImage Lines()
    {
        var (w, h) = (472, 630);
        var pixels = new byte[w * h];
        Array.Fill(pixels, (byte)240);
        for (var y = 20; y < h; y += 40)
            for (var t = 0; t < 3; t++)
                for (var x = 0; x < w; x++) pixels[(y + t) * w + x] = 20;
        return new ScannedImage(w, h, 1, 100, pixels);
    }

    /// <summary>The middle row of the dark line nearest to <paramref name="nearY"/> in column <paramref name="x"/>.</summary>
    private static double LineAt(ScannedImage img, int x, int nearY)
    {
        var (sum, n) = (0.0, 0);
        for (var y = Math.Max(0, nearY - 15); y < Math.Min(img.Height, nearY + 15); y++)
            if (img.Pixels[y * img.Width + x] < 128)
            {
                sum += y;
                n++;
            }
        return n == 0 ? double.NaN : sum / n;
    }

    [Fact]
    public void NoLift_PageUnchanged()
    {
        var flat = Lines();
        var page = BookScanSimulator.Page(flat, new BookLie(0, 40, 300, true), spineRight: true);
        Assert.Equal((flat.Width, flat.Height), (page.Width, page.Height));
        Assert.Equal(flat.Pixels, page.Pixels);
    }

    [Fact]
    public void Optics_SpineAlongSensor_LinesBendTowardsTheAxis_CompressedAndDarkerAtTheSpine()
    {
        var flat = Lines();
        var page = BookScanSimulator.Page(flat, new BookLie(15, 40, 300, SpineAlongSensor: true, BlurPerMm: 0), spineRight: true);
        // narrower: the lifted part is seen foreshortened
        Assert.InRange(page.Width, flat.Width - 30, flat.Width - 2);
        var far = 60;                    // flat part
        var near = page.Width - 4;       // at the spine
        // a line near the top bends down towards the middle at the spine, one near the bottom up; one at the axis stays
        Assert.Equal(61, LineAt(page, far, 61), 1);
        Assert.True(LineAt(page, near, 64) > LineAt(page, far, 61) + 1.5);
        Assert.True(LineAt(page, near, 576) < LineAt(page, far, 581) - 1.5);
        Assert.InRange(Math.Abs(LineAt(page, far, 301) - LineAt(page, near, 301)), 0, 1.5);   // 14 px from the axis: hardly moved
        // darker at the spine
        Assert.True(page.Pixels[10 * page.Width + near] < page.Pixels[10 * page.Width + far] - 40);
    }

    [Fact]
    public void ContactSensor_OnlyCompressed_LinesStayStraight()
    {
        var flat = Lines();
        var page = BookScanSimulator.Page(flat, new BookLie(10, 40, null, SpineAlongSensor: true, BlurPerMm: 0), spineRight: false);
        Assert.True(page.Width < flat.Width);
        Assert.Equal(LineAt(page, page.Width - 60, 61), LineAt(page, 3, 61), 0.6);
    }

    [Fact]
    public void Spread_TurnedLikeOnTheGlass()
    {
        var flat = Lines();
        var spread = BookScanSimulator.Spread(flat, flat, new BookLie(12, 35, 300, true), turned: true);
        Assert.True(spread.Height > spread.Width);   // two upright pages side by side, turned by a quarter
        Assert.InRange(spread.Height, 2 * flat.Width - 40, 2 * flat.Width);
    }

    /// <summary>Writes the test pages of the local test collection (book scans) from three pages of the layout test set.</summary>
    [Fact]
    public void WriteTestPages_WhenAsked()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_BUCHSCANS") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        var prose = Render("2aaef42186d504ec12fd8a9d81742cfc67b0c5886f4d0848aad4d2b6622ed349.pdf");
        var chart = Render("0f000a321e072f4128a6d708522f2be9ff3a7b8e2bac4171534cd954cf90ecd0.pdf");
        var list = Render("3a54f77cf8108096ff8d1b0714d4f022a92d5b255a42308bd56fd0dc66fbe4b3.pdf");
        var pages = new (string Name, ScannedImage Image, string Lie)[]
        {
            ("doppelseite-a5-auf-a4-optik", BookScanSimulator.Spread(prose, chart, new BookLie(12, 35, 320, true, SkewDegrees: 1.2), turned: true),
                "Doppelseite, quer aufs A4-Glas gelegt (Bund entlang der Sensorzeile), Scanner mit Optik 320 mm, Anhebung 12 mm auf 35 mm, 1,2° schräg; so geliefert, wie der Scanner es tut (um 90° gedreht)"),
            ("einzelseite-bund-rechts-optik", BookScanSimulator.Page(prose, new BookLie(15, 45, 300, true, AxisAt: 0.45, SkewDegrees: -0.8), spineRight: true),
                "Einzelseite, Bund rechts entlang der Sensorzeile, Scanner mit Optik 300 mm, Anhebung 15 mm auf 45 mm, Achse bei 45 %, 0,8° schräg"),
            ("einzelseite-bund-links-kontaktsensor", BookScanSimulator.Page(list, new BookLie(10, 40, null, false, Shade: 0.55, BlurPerMm: 0.6), spineRight: false),
                "Einzelseite, Bund links, Kontaktsensor (keine Perspektive), Anhebung 10 mm auf 40 mm, 55 % dunkler und unscharf am Bund"),
        };
        foreach (var (name, image, lie) in pages)
        {
            Save(image, Path.Combine(folder, name + ".png"));
            File.WriteAllText(Path.Combine(folder, name + ".txt"), lie + "\n");
        }
        Save(prose, Path.Combine(folder, "vorlage-fliesstext.flach.png"));
        Save(chart, Path.Combine(folder, "vorlage-diagramm.flach.png"));
        Save(list, Path.Combine(folder, "vorlage-verzeichnis.flach.png"));
    }

    /// <summary>A page of the layout test set at 200 dpi in grey, on a slightly grey paper as a scanner sees it.</summary>
    private static ScannedImage Render(string name)
    {
        using var doc = PdfDocument.Open(TestPdfs.File("doclaynet", name));
        using var bitmap = PdfKnead.Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(doc, doc.Pages[0]), doc, doc.Pages[0], 200 / 72.0,
            new PdfKnead.Rendering.ImageCache());
        var (w, h) = (bitmap.Width, bitmap.Height);
        var pixels = new byte[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var c = bitmap.GetPixel(x, y);
                pixels[y * w + x] = (byte)(((c.Red * 77 + c.Green * 150 + c.Blue * 29) >> 8) * 235 / 255);
            }
        return new ScannedImage(w, h, 1, 200, pixels);
    }

    private static void Save(ScannedImage image, string path)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(image.Pixels, 0, bitmap.GetPixels(), image.Pixels.Length);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }
}
