using System.Diagnostics;
using System.Text;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Measurement (not part of the normal test run): searching and redacting, plus the search for further unredacted occurrences,
/// in a large text document. Enable with PDFKNEAD_MESSEN=1; show the output with --logger "console;verbosity=detailed".
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class RedactionTiming
{
    private static readonly string[] Words =
    [
        "Angebot", "Lieferung", "Rechnung", "Musterfirma", "Projekt", "Termin", "Zeichnung", "Freigabe", "Bauteil", "Prüfung",
        "Abnahme", "Material", "Toleranz", "Oberfläche", "Gewinde", "Bohrung", "Montage", "Verpackung", "Versand", "Kunde",
    ];

    /// <summary>Builds a document with <paramref name="pages"/> pages of 45 lines of running text each, with placeholder names scattered at random.</summary>
    private static byte[] TextDocument(int pages)
    {
        var pdf = TestPdf.Simple(pages, title: null);
        var random = new Random(7);
        for (var p = 1; p <= pages; p++)
        {
            var content = new StringBuilder("BT /F1 9 Tf 50 800 Td 16 TL ");
            for (var line = 0; line < 45; line++)
            {
                var text = new StringBuilder();
                while (text.Length < 90)
                {
                    text.Append(random.Next(12) == 0 ? $"Name{random.Next(300):000} Mustermann" : Words[random.Next(Words.Length)]);
                    text.Append(' ');
                }
                content.Append('(').Append(text).Append(") Tj T* ");
            }
            content.Append("ET");
            pdf.Replace(4 + 2 * (p - 1), TestPdf.FlateStream(Encoding.Latin1.GetBytes(content.ToString())));
        }
        return pdf.Build();
    }

    /// <summary>
    /// Builds a document of <paramref name="pages"/> scanned pages: a grey image of 1240 × 1754 samples (150 dpi) over the whole page and an
    /// invisible text layer (OCR) of 45 lines over it.
    /// </summary>
    private static byte[] ScannedDocument(int pages)
    {
        var pdf = TestPdf.Simple(pages, title: null);
        var samples = new byte[1240 * 1754];
        new Random(3).NextBytes(samples.AsSpan(0, 4096));
        var image = pdf.Add(TestPdf.FlateStream(samples, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", Core.Pdf.Objects.PdfInteger.Of(1240)), ("Height", Core.Pdf.Objects.PdfInteger.Of(1754)), ("ColorSpace", TestPdf.N("DeviceGray")),
            ("BitsPerComponent", Core.Pdf.Objects.PdfInteger.Of(8)))));
        var random = new Random(7);
        for (var p = 1; p <= pages; p++)
        {
            var content = new StringBuilder("q 595 0 0 842 0 0 cm /Scan Do Q BT 3 Tr /F1 9 Tf 50 800 Td 16 TL ");
            for (var line = 0; line < 45; line++)
            {
                var text = new StringBuilder();
                while (text.Length < 90) text.Append(random.Next(12) == 0 ? $"Name{random.Next(300):000} Mustermann " : Words[random.Next(Words.Length)] + " ");
                content.Append('(').Append(text).Append(") Tj T* ");
            }
            content.Append("ET");
            pdf.Replace(4 + 2 * (p - 1), TestPdf.FlateStream(Encoding.Latin1.GetBytes(content.ToString())));
            ((Core.Pdf.Objects.PdfDictionary)pdf.Get(5 + 2 * (p - 1))).Set(Core.Pdf.Objects.PdfName.Resources, TestPdf.Dict(
                ("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))), ("XObject", TestPdf.Dict(("Scan", TestPdf.Ref(image))))));
        }
        return pdf.Build();
    }

    /// <summary>„Jetzt schwärzen“ (KONZEPT.md, section 5.6, „Schwärzen-Umbau“): only the bars, with hidden content and the old text recognition
    /// removed in the whole document, and only on the pages with redactions — time and memory allocated, each taken back afterwards.</summary>
    private static async Task MeasureApply(DocumentViewModel doc, string what)
    {
        var hidden = Core.Pdf.Writing.CleanupItems.HiddenContent | Core.Pdf.Writing.CleanupItems.HiddenLayers |
                     Core.Pdf.Writing.CleanupItems.HiddenImageParts | Core.Pdf.Writing.CleanupItems.InvisibleText;
        var marked = doc.History.Pages.Select((p, i) => (p, i)).Where(x => x.p.Elements.Any(e => e is Core.Pdf.Editing.RedactionElement)).Select(x => x.i).ToHashSet();
        foreach (var (name, cleanup, scoped) in new (string, Core.Pdf.Writing.CleanupItems, Core.Pdf.Writing.CleanupScope?)[]
                 {
                     ("nur Balken", Core.Pdf.Writing.CleanupItems.None, null),
                     ("ganzes Dokument", hidden, null),
                     ("nur Seiten mit Schwärzung", Core.Pdf.Writing.CleanupItems.None, new Core.Pdf.Writing.CleanupScope(hidden, marked)),
                     ("ganzes Dokument, nur Was niemand sieht", Core.Pdf.Writing.CleanupItems.HiddenContent, null),
                     ("ganzes Dokument, nur Ebenen", Core.Pdf.Writing.CleanupItems.HiddenLayers, null),
                     ("ganzes Dokument, nur Bildteile", Core.Pdf.Writing.CleanupItems.HiddenImageParts, null),
                     ("ganzes Dokument, nur Texterkennung", Core.Pdf.Writing.CleanupItems.InvisibleText, null),
                 })
        {
            GC.Collect();
            var alloc = GC.GetTotalAllocatedBytes(true);
            var sw = Stopwatch.StartNew();
            await doc.ApplyRedactionsAsync(false, cleanup, hideLengths: true, scoped: scoped);
            Console.WriteLine($"MESS {what}, Schwärzen {name}: {sw.ElapsedMilliseconds} ms, belegt {(GC.GetTotalAllocatedBytes(true) - alloc) >> 20} MB, " +
                              $"Prozess-Spitze {Process.GetCurrentProcess().PeakWorkingSet64 >> 20} MB");
            doc.History.Undo();
        }
    }

    /// <summary>The same for 300 scanned pages with a text layer: the old text recognition goes in the whole document.</summary>
    [Fact]
    public void MeasureScanned()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") is null) return;
        var pdf = TestFiles.Write(ScannedDocument(300), "schwaerzen-scan-gross.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var names = Enumerable.Range(0, 20).Select(i => $"Name{i:000} Mustermann").ToList();
                var hits = await doc.FindForRedactionAsync(names, RedactionPatterns.None, matchCase: false);
                await doc.RedactHitsAsync(hits.Where(h => h.Page < 10));
                await MeasureApply(doc, "Scan 300 Seiten");
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    [Fact]
    public void Measure()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") is null) return;
        var pdf = TestFiles.Write(TextDocument(500), "schwaerzen-gross.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var sw = Stopwatch.StartNew();
                for (var p = 0; p < doc.PageCount; p++) await doc.GetTextAsync(p);
                Console.WriteLine($"MESS Text aller 500 Seiten: {sw.ElapsedMilliseconds} ms");

                // redact 60 different names on the first 30 pages
                var names = Enumerable.Range(0, 60).Select(i => $"Name{i:000} Mustermann").ToList();
                sw.Restart();
                var hits = await doc.FindForRedactionAsync(names, RedactionPatterns.None, matchCase: false);
                Console.WriteLine($"MESS Suchen 60 Begriffe: {sw.ElapsedMilliseconds} ms, {hits.Count} Funde");
                await doc.RedactHitsAsync(hits.Where(h => h.Page < 30));

                var alloc = GC.GetAllocatedBytesForCurrentThread();
                sw.Restart();
                var open = await doc.FindUnredactedAsync();
                Console.WriteLine($"MESS Weitere Fundstellen: {sw.ElapsedMilliseconds} ms, {open.Count} Funde, belegt {(GC.GetAllocatedBytesForCurrentThread() - alloc) >> 20} MB");
                sw.Restart();
                open = await doc.FindUnredactedAsync();
                Console.WriteLine($"MESS Weitere Fundstellen (2. Mal): {sw.ElapsedMilliseconds} ms, {open.Count} Funde");

                // QR and bar codes: the question right away (the search ahead is stopped, the rest in parallel), then after it finished
                var peak = Process.GetCurrentProcess();
                GC.Collect();
                peak.Refresh();
                Console.WriteLine($"MESS vor den Codes: belegt {peak.WorkingSet64 >> 20} MB, Spitze bisher {peak.PeakWorkingSet64 >> 20} MB");
                sw.Restart();
                var codes = await doc.FindCodesToRedactAsync();
                peak.Refresh();
                Console.WriteLine($"MESS Codes sofort ({doc.PageCount} Seiten): {sw.ElapsedMilliseconds} ms, {codes.Count} Codes, Spitze {peak.PeakWorkingSet64 >> 20} MB");
                sw.Restart();
                codes = await doc.FindCodesToRedactAsync();
                Console.WriteLine($"MESS Codes 2. Mal: {sw.ElapsedMilliseconds} ms");

                await MeasureApply(doc, "Text 500 Seiten");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
