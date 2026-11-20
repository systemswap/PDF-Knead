using System.Diagnostics;
using PdfKnead.Ai;
using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;

namespace PdfKnead.Cli;

/// <summary>
/// „pdf-knead texterkennung“: recognises the text of pages and prints it line by line with confidence and times (docs/PRUEFUNGEN.md,
/// section 6o) — to check and measure the text recognition without the window; with „--ablegen“ also writes the document with the new
/// text layers.
/// </summary>
internal static class OcrCheck
{
    /// <summary>Runs the check.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">Only this page (1-based); 0 for all.</param>
    /// <param name="size">The model size.</param>
    /// <param name="models">A folder with the model folders as published; null for the folder „ocr“ of the program.</param>
    /// <param name="dpi">Resolution of the page images.</param>
    /// <param name="words">Also print the words with their boxes.</param>
    /// <param name="options">Settings of the recognition (threads, batch, memory) for measuring.</param>
    /// <param name="output">Write the document with the text layers there; null: do not write.</param>
    /// <param name="all">Also pages without an image (otherwise skipped: nothing to read).</param>
    /// <returns>Exit code.</returns>
    public static int Run(PdfDocument doc, int page, OcrModelSize size, string? models, int dpi, bool words, PaddleOcrOptions options, string? output, bool all)
    {
        var watch = Stopwatch.StartNew();
        using var ocr = models is null
            ? PaddleOcr.Open(size, options)
            : new PaddleOcr(Path.Combine(models, $"PP-OCRv6_{OcrModels.Key(size)}_det_onnx"), Path.Combine(models, $"PP-OCRv6_{OcrModels.Key(size)}_rec_onnx"), options);
        Console.WriteLine($"Modelle geladen: {watch.ElapsedMilliseconds} ms, {Process.GetCurrentProcess().WorkingSet64 >> 20} MB");
        var images = new ImageCache(64L << 20);   // as the program does: bounded, a scan page's image is decoded anew per page
        var items = PageItem.From(doc);
        var layers = new Dictionary<int, PdfStream>();
        long render = 0, detect = 0, read = 0;
        var lineCount = 0;
        for (var i = 0; i < items.Length; i++)
        {
            if (page != 0 && i + 1 != page) continue;
            watch.Restart();
            var list = PageInterpreter.Build(doc, items[i].Page, withoutOwnElements: true);
            if (!all && !OcrTextLayer.ShowsImages(list))
            {
                Console.WriteLine($"--- Seite {i + 1}: kein Bild, übersprungen ---");
                continue;
            }
            var (image, toPage) = PageRecognition.Render(items[i], list, images, dpi);
            render += watch.ElapsedMilliseconds;
            watch.Restart();
            var boxes = ocr.Detect(image, default);
            detect += watch.ElapsedMilliseconds;
            if (Environment.GetEnvironmentVariable("PDFKNEAD_OCR_SPEICHER") is not null) Console.WriteLine($"  nach Finden: {Process.GetCurrentProcess().WorkingSet64 >> 20} MB, Spitze {Process.GetCurrentProcess().PeakWorkingSet64 >> 20} MB, verwaltet {GC.GetTotalMemory(false) >> 20} MB, lebend {GC.GetTotalMemory(true) >> 20} MB, Heap {GC.GetGCMemoryInfo().HeapSizeBytes >> 20} MB");
            watch.Restart();
            var recognised = ocr.Read(image, boxes, default);
            read += watch.ElapsedMilliseconds;
            var lines = OcrTextLayer.WithoutText(OcrTextLayer.ToPage(recognised, toPage), list.Text);
            lineCount += lines.Count;
            Console.WriteLine($"--- Seite {i + 1} ({image.Width} × {image.Height}, {boxes.Count} Kästen, {recognised.Count} Zeilen, ohne vorhandenen Text {lines.Count}) ---");
            foreach (var line in lines)
            {
                Console.WriteLine($"{line.Confidence:0.00}  {line.Text}");
                if (words)
                    foreach (var w in line.Words)
                        Console.WriteLine($"        {w.Text}  ({w.Corners[3].X:0.#},{w.Corners[3].Y:0.#}) – ({w.Corners[1].X:0.#},{w.Corners[1].Y:0.#})");
            }
            if (OcrTextLayer.Form(lines, list.PageBox) is { } form) layers[i] = form;
        }
        Console.WriteLine($"Zeiten: Zeichnen {render} ms, Finden {detect} ms, Lesen {read} ms; {lineCount} Zeilen; Spitze {Process.GetCurrentProcess().PeakWorkingSet64 >> 20} MB");
        if (output is null) return 0;
        using (var file = File.Create(output))
            PdfWriter.Write(doc, items, file, new PdfSaveOptions { TextLayers = layers, ScopedCleanup = new CleanupScope(CleanupItems.InvisibleText, layers.Keys.ToHashSet()) });
        Console.WriteLine($"Gespeichert: {output}");
        return 0;
    }
}
