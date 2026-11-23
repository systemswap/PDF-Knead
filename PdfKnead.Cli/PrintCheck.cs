using System.Diagnostics;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Printing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Cli;

/// <summary>
/// "pdf-knead drucktest &lt;datei|ordner&gt; [--seiten &lt;n&gt;] [--dpi &lt;n&gt;] [--ablegen &lt;ordner&gt;]": every page as the printer gets it — the printed sheet of
/// „Druckbögen als PDF speichern“ and of printing through lp (one page per sheet, actual size, the paper as large as the page) — read again,
/// drawn and compared with the page drawn from the file. Images in an image format once lost their format on the sheet and came out as noise
/// (docs/FEHLERGEDAECHTNIS.md, No. 1004); the size of the sheets shows whether streams are copied as stored. With --ablegen, a differing page is
/// put down as two pictures (page and sheet).
/// </summary>
internal static class PrintCheck
{
    /// <summary>A pixel counts as different from this sum of the differences of its three channels on.</summary>
    private const int PixelTolerance = 60;

    /// <summary>A page counts as different from this share of different pixels on (edges move by a pixel between the two ways).</summary>
    private const double PageTolerance = 0.01;

    public static int Run(string path, int maxPages, int dpi, string? putDown = null)
    {
        var files = Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList() : [path];
        var watch = Stopwatch.StartNew();
        int documents = 0, pages = 0, differing = 0, failed = 0, unreadable = 0;
        long fileBytes = 0, sheetBytes = 0;
        var scale = dpi / 72.0;
        foreach (var file in files)
        {
            PdfDocument doc;
            try
            {
                doc = PdfDocument.Open(file);
            }
            catch (Exception e) when (e is Core.Pdf.PdfException or IOException)
            {
                unreadable++;
                continue;
            }
            using (doc)
            {
                documents++;
                fileBytes += new FileInfo(file).Length;
                var images = new ImageCache();
                try
                {
                    var items = PageItem.From(doc).Take(maxPages).ToList();
                    using var prepared = PrintPages.Prepare(doc, items);
                    for (var i = 0; i < items.Count; i++)
                    {
                        var (width, height) = prepared.Size(i);
                        var sheets = PrintLayout.Arrange([(width, height)],
                            new PrintLayoutOptions { Scaling = PrintScaling.ActualSize, Margin = 0, PaperWidth = width, PaperHeight = height });
                        using var output = new MemoryStream();
                        SheetPdf.Write([prepared.Pages[i]], sheets, null, output);
                        sheetBytes += output.Length;
                        using var sheet = PdfDocument.Open(output.ToArray());
                        using var before = RasterExport.Render(items[i], scale, images);
                        using var after = RasterExport.Render(PageItem.From(sheet)[0], scale, images);
                        pages++;
                        var share = Different(before, after);
                        if (share > PageTolerance)
                        {
                            differing++;
                            Console.WriteLine($"anders: {Path.GetFileName(file)} Seite {i + 1}: {share:P1} der Bildpunkte");
                            if (putDown is not null)
                            {
                                Directory.CreateDirectory(putDown);
                                var name = Path.Combine(putDown, $"{Path.GetFileNameWithoutExtension(file)}-{i + 1}");
                                Save(before, name + "-seite.png");
                                Save(after, name + "-bogen.png");
                            }
                        }
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    failed++;
                    Console.WriteLine($"Fehler: {Path.GetFileName(file)}: {e.GetType().Name}: {e.Message}");
                }
            }
        }
        Console.WriteLine($"Dateien: {documents}, Seiten: {pages}, anders: {differing}, Fehler: {failed}, nicht lesbar: {unreadable}, " +
                          $"Dateien {fileBytes >> 10} KB, Bögen {sheetBytes >> 10} KB, Zeit {watch.Elapsed.TotalSeconds:0.0} s");
        return differing == 0 && failed == 0 ? 0 : 1;
    }

    private static void Save(SKBitmap bitmap, string path)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    /// <summary>The share of pixels that differ, over the area both images cover (the two sizes may differ by a pixel from rounding).</summary>
    private static double Different(SKBitmap a, SKBitmap b)
    {
        int width = Math.Min(a.Width, b.Width), height = Math.Min(a.Height, b.Height);
        if (width == 0 || height == 0) return a.Width == b.Width && a.Height == b.Height ? 0 : 1;
        long count = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                SKColor p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                if (Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue) > PixelTolerance) count++;
            }
        return (double)count / ((long)width * height);
    }
}
