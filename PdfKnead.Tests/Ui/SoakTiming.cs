using System.Diagnostics;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Soak test (not part of the normal test run; enable with PDFKNEAD_MESSEN=dauer, use a release build): repeatedly open a document,
/// redact in it, open and close the compression window (trial run) and the print preview, and close the tab. Afterwards memory must
/// return to the baseline, measured after a full garbage collection: managed memory and the private memory of the process
/// (which also contains memory-mapped files and Skia images).
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class SoakTiming
{
    private static void WaitUntil(Avalonia.Controls.Window window, Func<bool> condition, int seconds = 120)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until)
        {
            UiSession.Capture(window);
            Thread.Sleep(20);
        }
        Assert.True(condition(), "Zeitüberschreitung");
    }

    [Fact]
    public void OpenRedactCompressPrintClose()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") != "dauer") return;
        var root = Root();
        var source = Path.Combine(root, "TestPDFs", "pdfjs", "freeculture.pdf");
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("PDFKNEAD_RUNDEN"), out var r) ? r : 25;
        // PDFKNEAD_DAUER narrows the test down: alles (default) | offen (open and close only) | schwaerzen | komprimieren | drucken | fenster | ohnefenster | sofortzu
        var parts = Environment.GetEnvironmentVariable("PDFKNEAD_DAUER") ?? "alles";
        bool Part(string name) => parts == "alles" || parts == name;
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                vm.ConfirmRedaction = _ => Task.FromResult(new RedactionReply(RedactionAnswer.Apply));
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                (long Managed, long Private) Measure()
                {
                    for (var i = 0; i < 3; i++)
                    {
                        UiSession.Capture(window);
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                    GC.Collect();
                    using var process = Process.GetCurrentProcess();
                    return (GC.GetTotalMemory(true), process.PrivateMemorySize64);
                }
                var baseline = Measure();
                if (parts == "fenster")
                {
                    // Does the environment keep closed dialogs alive? An empty window with a large DataContext
                    var probes = new List<WeakReference>();
                    for (var i = 0; i < 4; i++)
                    {
                        var probe = new SaveChangesWindow("probe.pdf") { DataContext = new byte[50 << 20] };
                        var shown = probe.ShowDialog<object?>(window);
                        UiSession.Capture(probe);
                        probe.Close();
                        await shown;
                        probes.Add(new WeakReference(probe));
                    }
                    Measure();
                    Console.WriteLine($"MESS lebt noch: leeres Fenster {probes.Count(p => p.IsAlive)} von 4");
                    return;
                }
                Console.WriteLine($"MESS Grundlinie: verwaltet {baseline.Managed / 1e6:0} MB, privat {baseline.Private / 1e6:0} MB");
                var watch = Stopwatch.StartNew();
                var alive = new List<(string, WeakReference)>();
                for (var round = 1; round <= rounds; round++)
                {
                    var copy = Path.Combine(Directory.CreateTempSubdirectory("pdf-knead-dauer-").FullName, "buch.pdf");
                    File.Copy(source, copy);
                    await vm.OpenAsync([copy]);
                    var doc = vm.SelectedDocument!;
                    CompressWindow? compressWindow = null;
                    CompressViewModel? compress = null;
                    PrintWindow? printWindow = null;
                    PrintViewModel? print = null;
                    doc.AddElement(0, new RedactionElement { Rect = new PdfRectangle(50, 50, 300, 300) }, "Dauertest");
                    // The compress window is only opened after the gate; without it, it would refuse the mark
                    if (parts is "schwaerzen" or "ohnefenster" or "sofortzu") await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                    if (parts == "ohnefenster")
                    {
                        var c = new CompressViewModel(doc);
                        WaitUntil(window, () => c.IsReady);
                        c.Dispose();
                        await c.Released;
                        alive.Add(("CompressViewModel ohne Fenster", new WeakReference(c)));
                    }
                    if (parts == "sofortzu")
                    {
                        var c = new CompressViewModel(doc);
                        var w = new CompressWindow(c);
                        var shown = w.ShowDialog<string?>(window);
                        UiSession.Capture(w);
                        w.Close();
                        await shown;
                        await c.Released;
                        alive.Add(("CompressWindow sofort zu", new WeakReference(w)));
                        alive.Add(("CompressViewModel sofort zu", new WeakReference(c)));
                    }

                    if (Part("komprimieren"))
                    {
                    // Compress: redactions are applied through the redact-first gate, then a trial run, then close without saving
                    Task<string?>? compressed = null;
                    vm.ShowCompress = c =>
                    {
                        compress = c;
                        compressWindow = new CompressWindow(c);
                        compressed = compressWindow.ShowDialog<string?>(window);
                        return Task.FromResult<string?>(null);
                    };
                    await vm.CompressCommand.ExecuteAsync(null);
                    WaitUntil(window, () => compress is { IsReady: true });
                    compressWindow!.Close();
                    await compressed!.WaitAsync(TimeSpan.FromSeconds(60));
                    var compressReleased = await Task.WhenAny(compress!.Released, Task.Delay(10_000)) == compress.Released;
                    if (round == 1) Console.WriteLine($"MESS Komprimieren freigegeben: {compressReleased}");
                    }

                    if (Part("drucken"))
                    {
                    // open and close the print preview
                    Task<string?>? printed = null;
                    vm.ShowPrint = p =>
                    {
                        print = p;
                        printWindow = new PrintWindow(p);
                        printed = printWindow.ShowDialog<string?>(window);
                        return Task.FromResult<string?>(null);
                    };
                    await vm.PrintCommand.ExecuteAsync(null);
                    WaitUntil(window, () => print is { IsReady: true, PreviewImage: not null });
                    printWindow!.Close();
                    await printed!.WaitAsync(TimeSpan.FromSeconds(60));
                    var printReleased = await Task.WhenAny(print!.Released, Task.Delay(10_000)) == print.Released;
                    if (round == 1) Console.WriteLine($"MESS Drucken freigegeben: {printReleased}");
                    }

                    // undo, redact again (two redacted states in the history), then close
                    if (parts == "alles")
                    {
                        vm.UndoCommand.Execute(null);
                    await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                    }
                    await vm.CloseDocumentCommand.ExecuteAsync(doc);
                    alive.Add(("Dokument", new WeakReference(doc)));
                    if (compress is not null) alive.Add(("CompressViewModel", new WeakReference(compress)));
                    if (compressWindow is not null) alive.Add(("CompressWindow", new WeakReference(compressWindow)));
                    if (print is not null) alive.Add(("PrintViewModel", new WeakReference(print)));
                    if (printWindow is not null) alive.Add(("PrintWindow", new WeakReference(printWindow)));
                    if (round == 1 || round % 5 == 0)
                    {
                        var now = Measure();
                        Console.WriteLine($"MESS Runde {round,3}: verwaltet {now.Managed / 1e6:0} MB ({(now.Managed - baseline.Managed) / 1e6:+0;-0} MB), " +
                                          $"privat {now.Private / 1e6:0} MB ({(now.Private - baseline.Private) / 1e6:+0;-0} MB), {watch.Elapsed.TotalSeconds:0} s");
                    }
                }
                Measure();
                foreach (var group in alive.GroupBy(a => a.Item1))
                    Console.WriteLine($"MESS lebt noch: {group.Key} {group.Count(a => a.Item2.IsAlive)} von {group.Count()}");
                if (alive.FirstOrDefault(a => a.Item2.IsAlive && a.Item1.Contains("Window")).Item2?.Target is { } held)
                    Console.WriteLine("MESS Weg: " + (RootFinder.PathTo(held) ?? "keiner über statische Felder"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }
}
