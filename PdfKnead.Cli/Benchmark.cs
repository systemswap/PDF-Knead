using System.Diagnostics;
using System.Globalization;
using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Rendering;

namespace PdfKnead.Cli;

/// <summary>
/// "pdf-knead leistung": measures the whole chain over many files — opening, display list (time, allocated memory, size),
/// rendering with images, text for search — and shows the worst cases. Runs sequentially (clean numbers), with a time limit per
/// page (cancelled after 30 s, reported as hanging after 40 s).
/// </summary>
internal static class Benchmark
{
    private sealed record PageResult(string File, int Page, double OpenMs, double BuildMs, long BuildAlloc, int Items, int Text,
        long ListBytes, double RenderMs, long RenderAlloc, string? Error, double TextMs = 0, long TextBytes = 0);

    /// <param name="hashes">File for checksums of the rendered pages (pixel-exact before/after comparison).</param>
    public static int Run(string path, int maxPages, double dpi, string? csv, bool operators = false, bool allocations = false, string? hashes = null)
    {
        using var sampler = allocations ? new AllocationSampler() : null;
        sampler?.Start();
        // Time per operator over all pages (including nested content for Do)
        var opTicks = new Dictionary<string, (long Ticks, long Count)>();
        if (operators)
            PageInterpreter.OperatorTimer = (op, ticks) =>
            {
                lock (opTicks) opTicks[op] = (opTicks.GetValueOrDefault(op).Ticks + ticks, opTicks.GetValueOrDefault(op).Count + 1);
            };
        var files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [path];
        var results = new List<PageResult>();
        var hashList = hashes is null ? null : new List<string>();
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        var pause = GC.GetTotalPauseDuration();
        var allocated = GC.GetTotalAllocatedBytes();
        var total = Stopwatch.StartNew();
        var done = 0;
        foreach (var file in files)
        {
            results.AddRange(Measure(file, maxPages, dpi, sampler, hashList));
            if (++done % 50 == 0) Console.Error.WriteLine($"  {done}/{files.Count} Dateien …");
        }
        total.Stop();

        var ok = results.Where(r => r.Error is null).ToList();
        Console.WriteLine($"Dateien: {files.Count}, Seiten: {ok.Count}, Fehler/Hänger: {results.Count - ok.Count}, Gesamtzeit {total.Elapsed.TotalSeconds:0.0} s");
        Console.WriteLine($"Größter Arbeitsspeicher des Prozesses: {Process.GetCurrentProcess().PeakWorkingSet64 >> 20} MB");
        Console.WriteLine($"GC: {GC.CollectionCount(0) - gc0} / {GC.CollectionCount(1) - gc1} / {GC.CollectionCount(2) - gc2} Sammlungen (Gen 0 / 1 / 2), Pausen {(GC.GetTotalPauseDuration() - pause).TotalMilliseconds:0} ms, belegt {(GC.GetTotalAllocatedBytes() - allocated) >> 20} MB");
        if (ok.Count > 0)
        {
            Stats("Öffnen (ms, je Datei)", ok.GroupBy(r => r.File).Select(g => g.First().OpenMs));
            Stats("Zeichenliste (ms)", ok.Select(r => r.BuildMs));
            Stats("Zeichenliste belegt (KB)", ok.Select(r => r.BuildAlloc / 1024.0));
            Stats("Zeichenliste hält (KB)", ok.Select(r => r.ListBytes / 1024.0));
            Stats($"Zeichnen {dpi} dpi (ms)", ok.Select(r => r.RenderMs));
            Stats("Zeichnen belegt (KB)", ok.Select(r => r.RenderAlloc / 1024.0));
            Stats("Text für Suche (ms)", ok.Select(r => r.TextMs));
            Stats("Text für Suche hält (KB)", ok.Select(r => r.TextBytes / 1024.0));
            Top("Langsamster Aufbau", ok.OrderByDescending(r => r.BuildMs), r => $"{r.BuildMs:0} ms, {r.Items:N0} Einträge, {r.Text:N0} Zeichen");
            Top("Meister Speicher beim Aufbau", ok.OrderByDescending(r => r.BuildAlloc), r => $"{r.BuildAlloc >> 20} MB belegt, {r.Items:N0} Einträge");
            Top("Langsamstes Zeichnen", ok.OrderByDescending(r => r.RenderMs), r => $"{r.RenderMs:0} ms, {r.RenderAlloc >> 20} MB belegt");
            Top("Langsamstes Öffnen", ok.GroupBy(r => r.File).Select(g => g.First()).OrderByDescending(r => r.OpenMs), r => $"{r.OpenMs:0} ms");
        }
        if (operators)
        {
            Console.WriteLine("Zeit je Operator (Summe, inkl. Verschachteltem):");
            foreach (var (op, (ticks, count)) in opTicks.OrderByDescending(k => k.Value.Ticks).Take(25))
                Console.WriteLine($"  {op,-5} {ticks * 1000.0 / Stopwatch.Frequency,10:0.0} ms  {count,10:N0} mal  {ticks * 1e6 / Stopwatch.Frequency / Math.Max(1, count),8:0.00} µs je Aufruf");
            PageInterpreter.OperatorTimer = null;
        }
        if (sampler is not null)
        {
            Thread.Sleep(500);   // events arrive with a delay
            sampler.Print();
        }
        foreach (var error in results.Where(r => r.Error is not null).Take(30))
            Console.WriteLine($"  FEHLER {Path.GetFileName(error.File)} S. {error.Page}: {error.Error}");

        if (hashList is not null)
        {
            File.WriteAllLines(hashes!, hashList.Order(StringComparer.Ordinal));
            Console.WriteLine($"Prüfsummen: {hashes} ({hashList.Count} Seiten)");
        }
        if (csv is not null)
        {
            var sb = new StringBuilder("Datei;Seite;Oeffnen_ms;Aufbau_ms;Aufbau_KB;Eintraege;Zeichen;Liste_KB;Zeichnen_ms;Zeichnen_KB;Fehler\n");
            foreach (var r in results)
                sb.AppendLine(string.Join(';', Path.GetFileName(r.File), r.Page, F(r.OpenMs), F(r.BuildMs), r.BuildAlloc / 1024, r.Items, r.Text,
                    r.ListBytes / 1024, F(r.RenderMs), r.RenderAlloc / 1024, r.Error?.Replace(';', ',')));
            File.WriteAllText(csv, sb.ToString());
            Console.WriteLine($"CSV: {csv}");
        }
        return 0;

        static string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static IEnumerable<PageResult> Measure(string file, int maxPages, double dpi, AllocationSampler? sampler, List<string>? hashes)
    {
        var results = new List<PageResult>();
        PdfDocument doc;
        sampler?.Phase("Öffnen");
        var t = Stopwatch.StartNew();
        try
        {
            doc = PdfDocument.Open(file);
            _ = doc.PageCount;
        }
        catch (Exception e)
        {
            results.Add(new PageResult(file, 0, 0, 0, 0, 0, 0, 0, 0, 0, $"Öffnen: {e.GetType().Name}: {e.Message}"));
            return results;
        }
        var openMs = t.Elapsed.TotalMilliseconds;
        using (doc)
        {
            var images = new ImageCache();
            for (var i = 0; i < Math.Min(doc.PageCount, maxPages); i++)
            {
                var page = doc.Pages[i];
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var task = Task.Run(() =>
                {
                    sampler?.Phase("Aufbau");
                    var alloc = GC.GetAllocatedBytesForCurrentThread();
                    var watch = Stopwatch.StartNew();
                    var list = PageInterpreter.Build(doc, page, cancel.Token);
                    var buildMs = watch.Elapsed.TotalMilliseconds;
                    var buildAlloc = GC.GetAllocatedBytesForCurrentThread() - alloc;
                    sampler?.Phase("Zeichnen");
                    alloc = GC.GetAllocatedBytesForCurrentThread();
                    watch.Restart();
                    using (var bitmap = SkiaRenderer.RenderPage(list, doc, page, dpi / 72, images, cancel.Token))
                        if (hashes is not null)
                            lock (hashes) hashes.Add($"{Path.GetFileName(file)};{i + 1};{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bitmap.GetPixelSpan()))[..16]}");
                    var renderMs = watch.Elapsed.TotalMilliseconds;
                    var renderAlloc = GC.GetAllocatedBytesForCurrentThread() - alloc;
                    sampler?.Phase("Text");
                    watch.Restart();
                    var text = PdfKnead.Core.Pdf.Text.PageText.Create(list.Text);
                    text.Find("der");   // search as in the UI (builds the search text)
                    var textMs = watch.Elapsed.TotalMilliseconds;
                    sampler?.Phase("sonst");
                    return new PageResult(file, i + 1, openMs, buildMs, buildAlloc, list.Count, list.Text.Length, list.EstimatedBytes,
                        renderMs, renderAlloc, null, textMs, text.EstimatedBytes);
                }, cancel.Token);
                try
                {
                    results.Add(task.Wait(TimeSpan.FromSeconds(40))
                        ? task.Result
                        : new PageResult(file, i + 1, openMs, 0, 0, 0, 0, 0, 0, 0, "hängt (über 40 s)"));
                }
                catch (AggregateException e)
                {
                    var inner = e.InnerException!;
                    results.Add(new PageResult(file, i + 1, openMs, 0, 0, 0, 0, 0, 0, 0,
                        inner is OperationCanceledException ? "über 30 s abgebrochen" : $"{inner.GetType().Name}: {inner.Message}"));
                }
            }
        }
        return results;
    }

    private static void Stats(string name, IEnumerable<double> values)
    {
        var v = values.Order().ToList();
        if (v.Count == 0) return;
        double P(double q) => v[Math.Min(v.Count - 1, (int)(q * v.Count))];
        Console.WriteLine($"  {name,-28} Mittel {v.Average(),9:0.0}   Median {P(0.5),9:0.0}   95 % {P(0.95),9:0.0}   Max {v[^1],10:0.0}   Summe {v.Sum(),10:0}");
    }

    private static void Top(string title, IEnumerable<PageResult> rows, Func<PageResult, string> describe)
    {
        Console.WriteLine($"{title}:");
        foreach (var r in rows.Take(8)) Console.WriteLine($"  {Path.GetFileName(r.File)} S. {r.Page}: {describe(r)}");
    }
}
