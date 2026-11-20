using System.Diagnostics;
using System.IO.Compression;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Measurement (not part of the normal test run; PDFKNEAD_MESSEN=1, Release build): compressing large files, measuring time, allocated
/// memory and process peak, twice in a row each (the window computes this often on every slider movement). PDFKNEAD_MESSEN=plan
/// additionally takes the A0 plans from TestPDFs and compact "Save as image" for them; =kompakt runs only that.
/// </summary>
public sealed class CompressTiming
{
    [Fact]
    public void Measure()
    {
        var mode = Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN");
        if (mode is null) return;
        var root = Root();
        var files = mode == "kompakt" ? [] : new List<(string Name, byte[] Data)>
        {
            ("Buch 352 S. (freeculture)", File.ReadAllBytes(Path.Combine(root, "TestPDFs", "pdfjs", "freeculture.pdf"))),
            ("Scan 60 S. 300 dpi grau", Scans(60)),
            ("Fotos 40 S. 3000×2000 JPEG", Photos(40)),
        };
        if (mode is "plan" or "kompakt")
            foreach (var plan in TestPdfs.Private().Take(1))
                files.Add(("A0-Plan", File.ReadAllBytes(plan)));
        foreach (var (name, data) in files)
        {
            using var doc = PdfDocument.Open(data);
            var items = PageItem.From(doc);
            Console.WriteLine($"MESS {name}: {data.Length / 1e6:0.0} MB, {doc.PageCount} Seiten");
            foreach (var preset in mode == "kompakt" ? [] : new[] { CompressionPreset.EBook, CompressionPreset.Lossless })
                for (var run = 1; run <= 2; run++)
                {
                    long size = 0;
                    Step($"  {preset} Lauf {run}", () =>
                    {
                        using var output = new MemoryStream();
                        PdfWriter.Write(doc, items, output, new PdfSaveOptions { Compression = CompressionSettings.For(preset), ImageCodec = SkiaImageCodec.Instance });
                        size = output.Length;
                        using var result = PdfDocument.Open(output.ToArray());
                        SizeReport.Of(result);
                    });
                    Console.WriteLine($"MESS      → {size / 1e6:0.00} MB");
                }
            // As in the window: one cache across all trial runs, each run one slider movement
            var cache = new CompressionCache();
            var moves = new[]
            {
                ("E-Book (erster Lauf)", CompressionSettings.For(CompressionPreset.EBook)),
                ("Qualität 60", CompressionSettings.For(CompressionPreset.EBook) with { Preset = CompressionPreset.Custom, JpegQuality = 60 }),
                ("120 dpi", CompressionSettings.For(CompressionPreset.EBook) with { Preset = CompressionPreset.Custom, ImageDpi = 120 }),
                ("Nur verlustfrei", CompressionSettings.For(CompressionPreset.Lossless)),
                ("zurück zu E-Book", CompressionSettings.For(CompressionPreset.EBook)),
            };
            foreach (var (move, settings) in mode == "kompakt" ? [] : moves)
            {
                long size = 0;
                Step($"  Cache: {move}", () =>
                {
                    using var output = new MemoryStream();
                    PdfWriter.Write(doc, items, output, new PdfSaveOptions { Compression = settings, ImageCodec = SkiaImageCodec.Instance, CompressionCache = cache });
                    size = output.Length;
                    using var result = PdfDocument.Open(output.ToArray());
                    SizeReport.Of(result);
                });
                Console.WriteLine($"MESS      → {size / 1e6:0.00} MB, Zwischenspeicher {cache.Bytes / 1e6:0.0} MB");
            }
            if (name == "A0-Plan")
            {
                using var output = new MemoryStream();
                Step("  Als Bild kompakt 300 dpi", () => RasterExport.Write(items, output, new RasterSettings(300, Compact: true)));
                Console.WriteLine($"MESS      → {output.Length / 1e6:0.00} MB");
            }
        }
    }

    /// <summary>
    /// PDFKNEAD_MESSEN=schrift: 200 pages with 2 000 text operators each (10 million characters) in an embedded TrueType font,
    /// lossless with and without font subsetting (the difference is the font usage per character).
    /// </summary>
    [Fact]
    public void LongText()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") != "schrift") return;
        var ttf = Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var pdf = TestPdf.Simple(200, null);
        var file = pdf.Add(TestPdf.FlateStream(ttf, TestPdf.Dict(("Length1", Core.Pdf.Objects.PdfInteger.Of(ttf.Length)))));
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("LiberationSans")),
            ("Encoding", TestPdf.N("WinAnsiEncoding")), ("FontDescriptor", TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")),
                ("FontName", TestPdf.N("LiberationSans")), ("Flags", Core.Pdf.Objects.PdfInteger.Of(32)), ("FontFile2", TestPdf.Ref(file))))));
        var text = new System.Text.StringBuilder("BT /TT 6 Tf 20 820 Td ");
        for (var i = 0; i < 2000; i++) text.Append("(Lorem ipsum dolor sit amet, consetetur sadipscing elitr) Tj 0 -0.4 Td ");
        text.Append("ET");
        var pages = (Core.Pdf.Objects.PdfDictionary)pdf.Get(2);
        pages.Set(Core.Pdf.Objects.PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("TT", TestPdf.Ref(font))))));
        for (var p = 0; p < 200; p++)   // every page has its own content
            ((Core.Pdf.Objects.PdfDictionary)pdf.Get(4 + 2 * p + 1)).Set(Core.Pdf.Objects.PdfName.Contents,
                TestPdf.Ref(pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(text + $" BT 20 20 Td ({p}) Tj ET")))));
        using var doc = PdfDocument.Open(pdf.Build());
        var items = PageItem.From(doc);
        foreach (var subset in new[] { true, false, true, false })
            Step($"  verlustfrei, kürzen {(subset ? "ja" : "nein")}", () =>
                PdfWriter.Write(doc, items, Stream.Null, new PdfSaveOptions { Compression = CompressionSettings.For(CompressionPreset.Lossless) with { SubsetFonts = subset } }));
    }

    /// <summary>Measures time, allocated memory and process peak (sampled every 10 ms) over the measured action.</summary>
    private static void Step(string name, Action action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var start = process.WorkingSet64;
        long peak = start;
        var done = false;
        var sampler = new Thread(() =>
        {
            using var p = Process.GetCurrentProcess();
            while (!Volatile.Read(ref done))
            {
                p.Refresh();
                peak = Math.Max(peak, p.WorkingSet64);
                Thread.Sleep(10);
            }
        }) { IsBackground = true };
        sampler.Start();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var gen2 = GC.CollectionCount(2);
        var watch = Stopwatch.StartNew();
        action();
        watch.Stop();
        Volatile.Write(ref done, true);
        sampler.Join();
        Console.WriteLine($"MESS {name,-28} {watch.ElapsedMilliseconds,7} ms  belegt {(GC.GetTotalAllocatedBytes(true) - allocated) / 1e6,8:0} MB  " +
                          $"Spitze +{(peak - start) / 1e6,6:0} MB  Gen2 {GC.CollectionCount(2) - gen2}");
    }

    /// <summary>Scanned text pages: A4 greyscale image at 300 dpi, Flate, random "letters" with soft edges.</summary>
    internal static byte[] Scans(int pages)
    {
        const int w = 2480, h = 3508;
        using var output = new MemoryStream();
        var builder = new RasterPdfBuilder(output);
        var random = new Random(3);
        var pixels = new byte[w * h];
        for (var page = 0; page < pages; page++)
        {
            pixels.AsSpan().Fill(250);
            for (var line = 0; line < h / 60 - 4; line++)
                for (var x = 200; x < w - 200; x += random.Next(22, 34))
                {
                    var top = 240 + line * 60 + random.Next(0, 4);
                    var (cw, ch) = (random.Next(10, 20), random.Next(22, 32));
                    for (var y = top; y < top + ch; y++)
                        for (var xx = x; xx < x + cw; xx++)
                            if ((xx - x) % 7 < 3 || (y - top) % 11 < 3) pixels[y * w + xx] = (byte)random.Next(10, 40);
                }
            builder.AddPage(595.28, 841.89, Deflate(pixels), "FlateDecode", w, h, gray: true);
        }
        builder.Finish();
        return output.ToArray();
    }

    /// <summary>Photo pages: soft colour gradients with texture, as JPEG.</summary>
    internal static byte[] Photos(int pages)
    {
        const int w = 3000, h = 2000;
        using var output = new MemoryStream();
        var builder = new RasterPdfBuilder(output);
        var pixels = new byte[w * h * 3];
        for (var page = 0; page < pages; page++)
        {
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = (y * w + x) * 3;
                    pixels[i] = (byte)(128 + 100 * Math.Sin((x + page * 50) / 90.0));
                    pixels[i + 1] = (byte)(128 + 100 * Math.Cos(y / 70.0));
                    pixels[i + 2] = (byte)((x ^ y) & 255);
                }
            var jpeg = SkiaImageCodec.Instance.EncodeJpeg(w, h, 3, pixels, 90)!;
            builder.AddPage(720, 480, jpeg, "DCTDecode", w, h);
        }
        builder.Finish();
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Fastest)) z.Write(data);
        return output.ToArray();
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }
}
