using Avalonia.Controls;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Core.Printing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Leak probe (not part of the normal test run; enable with PDFKNEAD_PRUEFUNG=lecks, restrict to one output path with PDFKNEAD_WEG):
/// a file with secret words in every critical place goes through every output path of the program, with marked redactions and a
/// pending cleanup (case A), with cleanup only (case B), and encrypted without copy and print permission (case C). The leak search
/// (<c>pdf-knead lecksuche</c>) scans every resulting file. Output: one line per path, „LECK …“, „SAUBER …“ or „LEER …“ (nothing
/// produced), also written to pdf-knead-lecks.txt in the temporary folder. The probe asserts nothing; it is an inventory.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class LeakMatrix
{
    // Redacted: page text (with ActualText), field value, comment, image pixels; the document title also contains the text word
    private static readonly string[] Redacted = ["SCHWARZTEXT", "SCHWARZFELD", "SCHWARZNOTIZ", "SCHWARZBILD"];

    // Cleaned up: everything containing „BEREINIGT“ (author, XMP, attachment, script, private data, thumbnail, comment, layer, invisible text, bookmark)
    private static readonly string[] Cleaned = ["BEREINIGT"];

    private static readonly CleanupItems AllCleanup = Enum.GetValues<CleanupItems>().Aggregate(CleanupItems.None, (a, b) => a | b);

    private static void WaitUntil(Window window, Func<bool> condition, int seconds = 60)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until)
        {
            UiSession.Capture(window);
            Thread.Sleep(20);
        }
        Assert.True(condition(), "Zeitüberschreitung");
    }

    private static PdfString S(string text) => new(PdfTextEncoding.Encode(text));

    /// <summary>Builds the probe file: two pages, with everything to redact on page 1, plus everything the cleanup knows about.</summary>
    /// <param name="cipher">Encryption of the file; with encryption the open password is „lesen“.</param>
    /// <returns>The PDF file as bytes.</returns>
    internal static byte[] Secrets(PdfCipher cipher = PdfCipher.None)
    {
        var pdf = TestPdf.Simple(2);
        var catalog = (PdfDictionary)pdf.Get(1);
        var page = (PdfDictionary)pdf.Get(5);
        var second = (PdfDictionary)pdf.Get(7);
        pdf.InfoNumber = pdf.Add(TestPdf.Dict(("Title", S("Vertrag SCHWARZTEXT")), ("Author", S("BEREINIGTAUTOR"))));

        // 220 × 20 greyscale image whose pixels are the bytes of „SCHWARZBILD“
        var pixels = new byte[220 * 20];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)"SCHWARZBILD"[i % 11];
        var image = pdf.Add(TestPdf.FlateStream(pixels, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(220)), ("Height", PdfInteger.Of(20)), ("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8)))));
        var layer = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", S("Kalkulation"))));
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.Latin1.GetBytes(
            "BT /F1 12 Tf 72 720 Td (Oeffentlich bleibt) Tj ET " +
            "/Span <</ActualText (SCHWARZTEXT)>> BDC BT /F1 12 Tf 72 680 Td (SCHWARZTEXT) Tj ET EMC " +
            "q 220 0 0 20 72 500 cm /Im1 Do Q " +
            "/OC /L1 BDC BT /F1 12 Tf 72 400 Td (BEREINIGTEBENE) Tj ET EMC " +
            "BT 3 Tr /F1 12 Tf 72 380 Td (BEREINIGTUNSICHTBAR) Tj ET " +
            "BT /F1 12 Tf 72 300 Td (Bleibt stehen) Tj ET")));
        page.Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))), ("XObject", TestPdf.Dict(("Im1", TestPdf.Ref(image)))),
            ("Properties", TestPdf.Dict(("L1", TestPdf.Ref(layer))))));

        // a text field with value and appearance, and a comment next to it (both under the second redaction)
        var look = pdf.Add(TestPdf.FlateStream("/Tx BMC BT /Helv 10 Tf 2 5 Td (SCHWARZFELD) Tj ET EMC"u8.ToArray(), TestPdf.Dict(
            ("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 150, 20)),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("Helv", TestPdf.Ref(3)))))))));
        var field = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("FT", TestPdf.N("Tx")), ("T", S("Name")),
            ("V", S("SCHWARZFELD")), ("Rect", TestPdf.Box(300, 600, 450, 620)), ("P", TestPdf.Ref(5)), ("AP", TestPdf.Dict(("N", TestPdf.Ref(look))))));
        var note = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Text")), ("Rect", TestPdf.Box(300, 560, 320, 580)),
            ("Contents", S("SCHWARZNOTIZ"))));
        page.Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(field), TestPdf.Ref(note)]));
        catalog.Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new PdfArray([TestPdf.Ref(field)])),
            ("DR", TestPdf.Dict(("Font", TestPdf.Dict(("Helv", TestPdf.Ref(3)))))), ("DA", S("/Helv 0 Tf 0 g"))));

        // what the cleanup knows about
        var comment = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Text")), ("Rect", TestPdf.Box(10, 10, 30, 30)),
            ("Contents", S("BEREINIGTKOMMENTAR"))));
        second.Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(comment)]));
        var xmp = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("Metadata")), ("Subtype", TestPdf.N("XML"))), "<x:xmpmeta>BEREINIGTXMP</x:xmpmeta>"u8.ToArray()));
        catalog.Set(PdfName.Get("Metadata"), TestPdf.Ref(xmp));
        var file = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("EmbeddedFile"))), "BEREINIGTANHANG"u8.ToArray()));
        var spec = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Filespec")), ("F", S("anhang.txt")), ("EF", TestPdf.Dict(("F", TestPdf.Ref(file))))));
        var script = pdf.Add(TestPdf.Dict(("S", TestPdf.N("JavaScript")), ("JS", S("app.alert('BEREINIGTSKRIPT')"))));
        catalog.Set(PdfName.Get("Names"), TestPdf.Dict(
            ("EmbeddedFiles", TestPdf.Dict(("Names", new PdfArray([S("anhang.txt"), TestPdf.Ref(spec)])))),
            ("JavaScript", TestPdf.Dict(("Names", new PdfArray([S("start"), TestPdf.Ref(script)]))))));
        catalog.Set(PdfName.Get("PieceInfo"), TestPdf.Dict(("Zeichner", TestPdf.Dict(("Private", S("BEREINIGTPRIVAT"))))));
        page.Set(PdfName.Get("Thumb"), TestPdf.Ref(pdf.Add(new PdfStream(TestPdf.Dict(("Width", PdfInteger.Of(1)), ("Height", PdfInteger.Of(1))), "BEREINIGTVORSCHAU"u8.ToArray()))));
        catalog.Set(PdfName.Get("OCProperties"), TestPdf.Dict(("OCGs", new PdfArray([TestPdf.Ref(layer)])), ("D", TestPdf.Dict(("OFF", new PdfArray([TestPdf.Ref(layer)]))))));
        var outlineRoot = pdf.Add(new PdfDictionary());
        var mark2 = pdf.Add(TestPdf.Dict(("Title", S(@"..\..\Ausbruch:x")), ("Parent", TestPdf.Ref(outlineRoot)), ("Dest", new PdfArray([TestPdf.Ref(7), TestPdf.N("Fit")]))));
        var mark1 = pdf.Add(TestPdf.Dict(("Title", S("Kapitel BEREINIGTLESEZEICHEN")), ("Parent", TestPdf.Ref(outlineRoot)), ("Next", TestPdf.Ref(mark2)),
            ("Dest", new PdfArray([TestPdf.Ref(5), TestPdf.N("Fit")]))));
        ((PdfDictionary)pdf.Get(mark2)).Set(PdfName.Get("Prev"), TestPdf.Ref(mark1));
        pdf.Replace(outlineRoot, TestPdf.Dict(("Type", TestPdf.N("Outlines")), ("First", TestPdf.Ref(mark1)), ("Last", TestPdf.Ref(mark2)), ("Count", PdfInteger.Of(2))));
        catalog.Set(PdfName.Get("Outlines"), TestPdf.Ref(outlineRoot));
        return pdf.Build(cipher: cipher, user: cipher == PdfCipher.None ? "" : "lesen", owner: "besitzer", permissions: PdfPermissions.Modify);
    }

    /// <summary>One output path: acts on the open document, writes files into the folder and returns any copied text.</summary>
    private sealed record Way(string Name, Func<MainViewModel, MainWindow, DocumentViewModel, string, Task<string?>> Run);

    private static IEnumerable<Way> Ways()
    {
        yield return new("Speichern unter", async (vm, _, _, dir) =>
        {
            vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(Path.Combine(dir, "unter.pdf"));
            await vm.SaveAsCommand.ExecuteAsync(null);
            return null;
        });
        yield return new("Schwärzen, Strg+S überschreiben", async (vm, _, doc, dir) =>
        {
            await vm.ApplyRedactionsCommand.ExecuteAsync(null);
            vm.ConfirmRedactedSave = _ => Task.FromResult(RedactedSaveAnswer.Overwrite);
            await vm.SaveCommand.ExecuteAsync(null);
            File.Copy(doc.Path, Path.Combine(dir, "ueberschrieben.pdf"));
            return null;
        });
        yield return new("Schwärzen, Strg+S als neue Datei", async (vm, _, _, dir) =>
        {
            await vm.ApplyRedactionsCommand.ExecuteAsync(null);
            vm.ConfirmRedactedSave = _ => Task.FromResult(RedactedSaveAnswer.SaveAsNew);
            vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(Path.Combine(dir, "neu.pdf"));
            await vm.SaveCommand.ExecuteAsync(null);
            return null;
        });
        foreach (var preset in new[] { CompressionPreset.Lossless, CompressionPreset.Screen, CompressionPreset.EBook, CompressionPreset.Print })
            yield return new($"Komprimieren {preset}", async (vm, window, _, dir) =>
            {
                CompressViewModel? compress = null;
                Task<string?>? result = null;
                vm.ShowCompress = c =>
                {
                    compress = c;
                    result = new CompressWindow(c).ShowDialog<string?>(window);
                    return Task.FromResult<string?>(null);
                };
                await vm.CompressCommand.ExecuteAsync(null);
                if (compress is null) return null;
                WaitUntil(window, () => compress.IsReady);
                // first a different preset (fills the cache), then the requested one
                compress.SelectedPreset = compress.Presets.First(p => p.Value != preset);
                WaitUntil(window, () => compress.IsReady);
                compress.SelectedPreset = compress.Presets.First(p => p.Value == preset);
                WaitUntil(window, () => compress.IsReady);
                compress.PickSavePath = (_, _, _) => Task.FromResult<string?>(Path.Combine(dir, $"komprimiert-{preset}.pdf"));
                await compress.SaveCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(120));
                await result!.WaitAsync(TimeSpan.FromSeconds(120));
                return null;
            });
        yield return new("Komprimieren in die offene Datei", async (vm, window, doc, dir) =>
        {
            CompressViewModel? compress = null;
            vm.ShowCompress = c =>
            {
                compress = c;
                return new CompressWindow(c).ShowDialog<string?>(window);
            };
            vm.ConfirmRedactedSave = _ => Task.FromResult(RedactedSaveAnswer.Overwrite);
            var run = vm.CompressCommand.ExecuteAsync(null);
            WaitUntil(window, () => compress is { IsReady: true } || run.IsCompleted);
            if (compress is null) return null;
            compress.PickSavePath = (_, _, _) => Task.FromResult<string?>(doc.Path);
            await compress.SaveCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(120));
            await run.WaitAsync(TimeSpan.FromSeconds(120));
            File.Copy(doc.Path, Path.Combine(dir, "komprimiert-selbst.pdf"));
            return null;
        });
        yield return new("Drucken als PDF", async (vm, window, _, dir) =>
        {
            PrintWindow? printWindow = null;
            PrintViewModel? print = null;
            Task<string?>? result = null;
            vm.ShowPrint = p =>
            {
                print = p;
                printWindow = new PrintWindow(p);
                result = printWindow.ShowDialog<string?>(window);
                return Task.FromResult<string?>(null);
            };
            await vm.PrintCommand.ExecuteAsync(null);
            if (print is null) return null;
            WaitUntil(window, () => print.IsReady);
            print.SelectedTarget = print.Targets.First(t => t.IsPdf);
            // without print permission the button stays disabled, which is correct and not a leak
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!(print.IsReady && print.PrintCommand.CanExecute(null)) && DateTime.UtcNow < until) { UiSession.Capture(window); Thread.Sleep(20); }
            if (!print.PrintCommand.CanExecute(null))
            {
                printWindow!.Close();
                await result!;
                vm.Message = "Drucken gesperrt (Rechte der Datei)";
                return null;
            }
            print.PickSavePath = (_, _, _) => Task.FromResult<string?>(Path.Combine(dir, "gedruckt.pdf"));
            await print.PrintCommand.ExecuteAsync(null);
            await result!.WaitAsync(TimeSpan.FromSeconds(120));
            return null;
        });
        yield return new("PDF als Bild", async (vm, _, _, dir) =>
        {
            vm.PickExportRaster = _ => Task.FromResult<Rendering.RasterSettings?>(new Rendering.RasterSettings(72));
            vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(Path.Combine(dir, "als-bild.pdf"));
            await vm.ExportImagePdfCommand.ExecuteAsync(null);
            return null;
        });
        // PDF/A: the copy as written, also one that does not conform (the leak search looks at what the writer produces)
        foreach (var part in (PdfAConformance[])[PdfAConformance.A2b, PdfAConformance.A3b, PdfAConformance.A2u, PdfAConformance.A4, PdfAConformance.A4f])
            yield return new($"PDF/A ({part})", async (vm, _, _, dir) =>
            {
                vm.PickPdfA = async model =>
                {
                    model.Part = part;
                    await model.CheckCommand.ExecuteAsync(null);
                    return model.LastResult;
                };
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(Path.Combine(dir, $"pdfa-{part}.pdf"));
                await vm.ExportPdfACommand.ExecuteAsync(null);
                return null;
            });
        yield return new("Seiten extrahieren", async (vm, _, doc, dir) =>
        {
            foreach (var p in doc.Pages) p.IsSelected = true;
            vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(Path.Combine(dir, "extrahiert.pdf"));
            await vm.ExtractPagesCommand.ExecuteAsync(null);
            return null;
        });
        yield return new("Teilen nach Lesezeichen", async (vm, _, _, dir) =>
        {
            var folder = Path.Combine(dir, "teile");
            Directory.CreateDirectory(folder);
            vm.ShowSplit = async split =>
            {
                split.PickFolder = () => Task.FromResult<string?>(folder);
                split.SplitAtBookmarks();
                if (split.Parts.Count == 0)
                {
                    split.EveryPages = 1;
                    split.SplitEvery();
                }
                await split.SaveCommand.ExecuteAsync(null);
                return true;
            };
            await vm.SplitCommand.ExecuteAsync(null);
            return null;
        });
        yield return new("Flachlegen als Bild (kompakt), Speichern unter", async (vm, _, doc, dir) =>
        {
            doc.RasterOnSave = new Rendering.RasterSettings(72, Compact: true);
            vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(Path.Combine(dir, "als-bild-sichern.pdf"));
            await vm.SaveAsCommand.ExecuteAsync(null);
            return null;
        });
        yield return new("Flachlegen, Speichern unter", async (vm, _, doc, dir) =>
        {
            doc.FlattenOnSave = true;
            vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(Path.Combine(dir, "flach.pdf"));
            await vm.SaveAsCommand.ExecuteAsync(null);
            return null;
        });
        yield return new("Kopieren (ganze Seite 1)", async (vm, _, doc, _) =>
        {
            var text = await doc.GetTextAsync(0);
            doc.Select(0, new Core.Pdf.Text.TextRange(0, text.Text.Length));
            string? copied = null;
            vm.SetClipboardText = value =>
            {
                copied = value;
                return Task.CompletedTask;
            };
            await vm.CopyTextCommand.ExecuteAsync(null);
            return copied ?? "";
        });
    }

    [Fact]
    public void Run()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_PRUEFUNG") != "lecks") return;
        var only = Environment.GetEnvironmentVariable("PDFKNEAD_WEG");
        var cases = new (string Name, bool Mark, bool Cleanup, bool Encrypted)[]
        {
            ("A geschwärzt+bereinigt", true, true, false),
            ("B nur bereinigt", false, true, false),
            ("C verschlüsselt, geschwärzt", true, false, true),
        };
        var lines = new List<string>();
        foreach (var (caseName, mark, cleanup, encrypted) in cases)
            foreach (var way in Ways())
            {
                if (only is not null && !way.Name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
                var dir = Directory.CreateTempSubdirectory("pdf-knead-leck-").FullName;
                var source = Path.Combine(dir, "geheim.pdf");
                File.WriteAllBytes(source, Secrets(encrypted ? PdfCipher.Aes256 : PdfCipher.None));
                var output = Path.Combine(dir, "ausgabe");
                Directory.CreateDirectory(output);
                string? copied = null;
                string? error = null;
                UiSession.Run(async () =>
                {
                    var vm = new MainViewModel();
                    var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
                    window.Show();
                    try
                    {
                        if (encrypted) vm.AskPassword = (_, _) => Task.FromResult<string?>("lesen");
                        await vm.OpenAsync([source]);
                        var doc = vm.SelectedDocument!;
                        vm.ConfirmRedaction = _ => Task.FromResult(new RedactionReply(RedactionAnswer.Apply, AlsoUnredacted: true, CleanElsewhere: true));
                        vm.ConfirmFixing = (_, _, _) => Task.FromResult(FixingReply.Save);
                        if (mark)
                        {
                            await doc.RedactHitsAsync(await doc.FindForRedactionAsync(["SCHWARZTEXT"], RedactionPatterns.None, false));
                            doc.AddElement(0, new RedactionElement { Rect = new PdfRectangle(60, 490, 300, 530) }, "Bild");
                            doc.AddElement(0, new RedactionElement { Rect = new PdfRectangle(295, 555, 460, 625) }, "Feld");
                        }
                        if (cleanup) await doc.CleanAsync(AllCleanup);
                        copied = await way.Run(vm, window, doc, output);
                        if (vm.Message is { Length: > 0 } message) error = message;
                    }
                    catch (Exception e)
                    {
                        error = e.GetType().Name + ": " + e.Message;
                    }
                    finally
                    {
                        vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                        window.Close();
                    }
                });
                var words = (mark ? Redacted : []).Concat(cleanup ? Cleaned : []).ToList();
                var files = Directory.GetFiles(output, "*.pdf", SearchOption.AllDirectories);
                var found = new List<string>();
                var notes = new List<string>();
                foreach (var file in files)
                {
                    try
                    {
                        var data = File.ReadAllBytes(file);
                        bool locked;
                        try
                        {
                            using (PdfDocument.Open(data)) locked = false;
                        }
                        catch (PdfPasswordException)
                        {
                            locked = true;
                        }
                        var hits = PdfKnead.Cli.LeakSearch.Find(data, words, locked ? "lesen" : null, out var summary);
                        found.AddRange(hits.Select(h => $"{Path.GetFileName(file)}: {h}"));
                        notes.Add($"{Path.GetFileName(file)} [{(locked ? "verschlüsselt" : "offen")}; {summary}]");
                    }
                    catch (Exception e)
                    {
                        found.Add($"{Path.GetFileName(file)}: nicht lesbar ({e.GetType().Name}: {e.Message})");
                    }
                }
                if (copied is not null)
                    foreach (var word in words)
                        if (copied.Contains(word, StringComparison.OrdinalIgnoreCase)) found.Add($"Zwischenablage: „{word}“");
                var leftovers = Directory.GetFiles(dir, "*.pdfknead-*", SearchOption.AllDirectories);
                if (leftovers.Length > 0) found.Add("Zwischendateien liegen: " + string.Join(", ", leftovers.Select(Path.GetFileName)));
                var status = found.Count > 0 ? "LECK  " : files.Length == 0 && copied is null ? "LEER  " : "SAUBER";
                var line = $"{status} {caseName} | {way.Name}{(error is not null ? $" | Meldung: {error}" : "")}";
                lines.Add(line);
                Console.WriteLine(line);
                foreach (var note in notes) lines.Add("         " + note);
                foreach (var hit in found) lines.Add("         - " + hit);
            }
        var report = Path.Combine(Path.GetTempPath(), "pdf-knead-lecks.txt");
        File.WriteAllLines(report, lines);
        Console.WriteLine("BERICHT " + report);
    }
}
