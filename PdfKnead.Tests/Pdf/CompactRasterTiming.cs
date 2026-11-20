using System.Diagnostics;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Measurement (not part of the normal test run; PDFKNEAD_MESSEN=1, Release build): "Save as image" in the standard mode (JPEG 300 dpi),
/// compact (MRC), and standard plus compression (e-book), comparing size and fidelity to the original (overall and at text edges)
/// across files of the public test collection (TestPDFs).
/// </summary>
public sealed class CompactRasterTiming
{
    [Fact]
    public void Measure()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") is null) return;
        var folder = Path.Combine(Root(), "TestPDFs", "pdfjs");
        var files = Directory.GetFiles(folder, "*.pdf").Order(StringComparer.Ordinal).Where((_, i) => i % (Dpi > 150 ? 15 : 5) == 0).ToList();
        var totals = new Dictionary<string, (long Bytes, double Diff, double Edges, long Ms, int Pages)>();
        foreach (var file in files)
        {
            PdfDocument doc;
            try
            {
                doc = PdfDocument.Open(file);
            }
            catch
            {
                continue;
            }
            using (doc)
            {
                if (doc.IsEncrypted || doc.PageCount == 0) continue;
                var pages = PageItem.From(doc).Take(2).ToList();
                foreach (var (name, make) in new (string, Func<byte[]>)[]
                {
                    ("JPEG 300", () => Raster(pages, new RasterSettings(300))),
                    ("kompakt 300", () => Raster(pages, new RasterSettings(300, Compact: true))),
                    ("JPEG 300 + E-Book", () => Compress(Raster(pages, new RasterSettings(300)))),
                })
                {
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        var bytes = make();
                        sw.Stop();
                        using var copy = PdfDocument.Open(bytes);
                        var (diff, edges) = Difference(pages[0], PageItem.From(copy)[0]);
                        var t = totals.GetValueOrDefault(name);
                        totals[name] = (t.Bytes + bytes.Length, t.Diff + diff, t.Edges + edges, t.Ms + sw.ElapsedMilliseconds, t.Pages + 1);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        Console.WriteLine($"MESS {Path.GetFileName(file)} {name}: {e.GetType().Name}");
                    }
                }
            }
        }
        foreach (var (name, t) in totals)
            Console.WriteLine($"MESS {name,-20} {t.Bytes / 1e6,8:0.00} MB  Abweichung {t.Diff / t.Pages,5:0.00}  an Kanten {t.Edges / t.Pages,5:0.00}  {t.Ms,7} ms  ({t.Pages} Dateien)");
    }

    /// <summary>
    /// Measurement (PDFKNEAD_MESSEN=gruppen): one page with 400 transparency groups under a soft mask and text in 16 colours; this case
    /// is expensive when the mask passes of compact mode (one per colour) reopen every group together with its mask.
    /// </summary>
    [Fact]
    public void ManyGroups()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") != "gruppen") return;
        var pdf = TestPdf.Simple(1);
        var mask = pdf.Add(TestPdf.FlateStream("0.6 g 0 0 40 40 re f"u8.ToArray(), TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")),
            ("BBox", TestPdf.Box(0, 0, 40, 40)), ("Group", TestPdf.Dict(("S", TestPdf.N("Transparency")), ("CS", TestPdf.N("DeviceGray")))))));
        var form = pdf.Add(TestPdf.FlateStream("0.2 0.5 0.8 rg 0 0 40 40 re f 0.9 0.3 0.1 rg 10 10 20 20 re f"u8.ToArray(), TestPdf.Dict(
            ("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 40, 40)),
            ("Group", TestPdf.Dict(("S", TestPdf.N("Transparency")))))));
        var content = new System.Text.StringBuilder();
        for (var i = 0; i < 400; i++)
            content.Append(FormattableString.Invariant($"q /GS0 gs 1 0 0 1 {20 + i % 20 * 28} {60 + i / 20 * 38} cm /Fm0 Do Q "));
        for (var i = 0; i < 16 * 20; i++)
            content.Append(FormattableString.Invariant($"{i % 16 / 16.0:0.00} 0 {1 - i % 16 / 16.0:0.00} rg BT /F1 9 Tf {30 + i % 16 * 34} {70 + i / 16 * 38} Td (Text) Tj ET "));
        var contentRef = pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(content.ToString())));
        var page = (Core.Pdf.Objects.PdfDictionary)pdf.Get(5);
        page.Set(Core.Pdf.Objects.PdfName.Contents, TestPdf.Ref(contentRef));
        page.Set(Core.Pdf.Objects.PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))),
            ("XObject", TestPdf.Dict(("Fm0", TestPdf.Ref(form)))),
            ("ExtGState", TestPdf.Dict(("GS0", TestPdf.Dict(("SMask", TestPdf.Dict(("S", TestPdf.N("Luminosity")), ("G", TestPdf.Ref(mask))))))))));
        using var doc = PdfDocument.Open(pdf.Build());
        var pages = PageItem.From(doc);
        for (var run = 1; run <= 3; run++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var size = Raster(pages, new RasterSettings(300, Compact: true)).Length;
            Console.WriteLine($"MESS 400 Gruppen mit Maske, 16 Textfarben, kompakt 300 dpi: {watch.ElapsedMilliseconds} ms, {size / 1e3:0} KB");
        }
    }

    /// <summary>Resolution of the comparison (PDFKNEAD_MESSEN=300: as when zooming in and in printing); 100 dpi by default.</summary>
    private static readonly int Dpi = int.TryParse(Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN"), out var d) && d > 1 ? d : 100;

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }

    private static byte[] Raster(IReadOnlyList<PageItem> pages, RasterSettings settings)
    {
        using var output = new MemoryStream();
        RasterExport.Write(pages, output, settings);
        return output.ToArray();
    }

    private static byte[] Compress(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Compression = CompressionSettings.For(CompressionPreset.EBook), ImageCodec = SkiaImageCodec.Instance });
        return output.ToArray();
    }

    /// <summary>Mean difference at the comparison resolution <see cref="Dpi"/>, overall and only at edges (contrast in the original).</summary>
    private static (double All, double Edges) Difference(PageItem original, PageItem copy)
    {
        using var a = RasterExport.Render(original, Dpi / 72.0, new ImageCache());
        using var b = RasterExport.Render(copy, Dpi / 72.0, new ImageCache());
        if (a.Width != b.Width || a.Height != b.Height) return (255, 255);
        long all = 0, edge = 0, n = 0, e = 0;
        for (var y = 1; y < a.Height - 1; y += 2)
            for (var x = 1; x < a.Width - 1; x += 2)
            {
                var (p, q) = (a.GetPixel(x, y), b.GetPixel(x, y));
                var d = Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue);
                all += d;
                n++;
                if (Math.Abs(p.Red - a.GetPixel(x + 1, y).Red) >= 60 || Math.Abs(p.Red - a.GetPixel(x, y + 1).Red) >= 60)
                {
                    edge += d;
                    e++;
                }
            }
        return (all / (3.0 * Math.Max(1, n)), edge / (3.0 * Math.Max(1, e)));
    }
}
