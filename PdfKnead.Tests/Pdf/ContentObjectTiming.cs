using System.Diagnostics;
using System.Globalization;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Measurement (not part of the normal test run) for „Bilder bearbeiten“ with very many objects (KONZEPT.md, section 5.20): finding the
/// objects of a page, hit tests, one step of moving, saving — on generated plans of 100,000 and 800,000 paths and on the largest file in
/// TestPDFs (an A0 plan, if present). Enable with PDFKNEAD_MESSEN=objekte (Release build); output with --logger "console;verbosity=detailed".
/// </summary>
public sealed class ContentObjectTiming
{
    private static void Step(string name, Action action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        using var process = Process.GetCurrentProcess();
        Console.WriteLine($"MESS {name,-34} {sw.ElapsedMilliseconds,7} ms  belegt {(GC.GetTotalAllocatedBytes(true) - before) / 1e6,8:0.0} MB  " +
                          $"verwaltet {GC.GetTotalMemory(true) / 1e6,7:0} MB  privat {process.PrivateMemorySize64 / 1e6,6:0} MB");
    }

    // the largest of the user's own files (TestPDFs/privat/), usually a plan
    private static string? Largest() => TestPdfs.Private().OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();

    /// <summary>An A0 page of short lines and small rectangles in blocks of 500 with a clip each, as a plan is drawn.</summary>
    internal static byte[] GeneratedPlan(int paths)
    {
        var random = new Random(7);
        var content = new StringBuilder();
        for (var i = 0; i < paths;)
        {
            content.Append("q 0 0 2384 3370 re W n 0.2 w\n");
            for (var k = 0; k < 500 && i < paths; k++, i++)
            {
                double x = random.NextDouble() * 2300, y = random.NextDouble() * 3300;
                content.Append(k % 3 == 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{x:F1} {y:F1} 3 2 re f\n")
                    : string.Create(CultureInfo.InvariantCulture, $"{x:F1} {y:F1} m {x + random.NextDouble() * 60:F1} {y + random.NextDouble() * 60:F1} l S\n"));
            }
            content.Append("Q\n");
        }
        var pdf = TestPdf.Simple(1, null);
        ((Core.Pdf.Objects.PdfDictionary)pdf.Get(5)).Set(Core.Pdf.Objects.PdfName.Get("MediaBox"), TestPdf.Box(0, 0, 2384, 3370));
        pdf.Replace(4, TestPdf.FlateStream(Encoding.ASCII.GetBytes(content.ToString())));
        return pdf.Build();
    }

    [Fact]
    public void Measure()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") != "objekte") return;
        foreach (var paths in new[] { 100_000, 800_000 }) Run($"Plan mit {paths} Pfaden", GeneratedPlan(paths));
        if (Largest() is { } file) Run("größte Datei in TestPDFs", File.ReadAllBytes(file));
    }

    private static void Run(string label, byte[] file)
    {
        Console.WriteLine($"MESS ===== {label} ({file.Length / 1e6:0.0} MB)");
        using var doc = PdfDocument.Open(file);
        var items = PageItem.From(doc);
        var item = items[0];
        PageObjects objects = PageObjects.Empty;
        Step("Finden (erstes Mal)", () => objects = ContentObjectEditor.ObjectsOf(item));
        Step("Finden (zweites Mal)", () => objects = ContentObjectEditor.ObjectsOf(item));
        Console.WriteLine($"MESS   {objects.Count} Objekte, davon {objects.Images.Count} Bilder{(objects.Incomplete ? ", UNVOLLSTÄNDIG" : "")}");
        var box = item.Page.CropBox;
        var hits = 0;
        Step("1000 Treffer samt Gruppe", () =>
        {
            var random = new Random(1);
            for (var k = 0; k < 1000; k++)
                if (objects.At(box.Left + random.NextDouble() * box.Width, box.Bottom + random.NextDouble() * box.Height, 3) is { } hit)
                {
                    hits++;
                    objects.GroupOf(hit);
                }
        });
        Console.WriteLine($"MESS   {hits} Treffer");
        if (objects.Count == 0) return;
        var keys = objects.GroupOf(objects.Count / 2).Select(objects.KeyOf).ToList();
        var moved = item;
        Step($"Schritt: {keys.Count} Objekte verschieben", () => moved = ContentObjectEditor.Transform(item, keys, Matrix.Translate(10, 0), objects));
        Step("Zweiter Schritt (Pfeiltaste)", () => moved = ContentObjectEditor.Transform(moved, keys, Matrix.Translate(1, 0), objects.WithEdits(moved.ObjectEdits)));
        Step("Sicht nach dem Schritt", () => objects.WithEdits(moved.ObjectEdits));
        (Core.Pdf.Objects.PdfStream Form, RectD Box)? copied = null;
        Step($"Kopieren (B7): {keys.Count} Objekte", () => copied = ContentObjectEditor.Copy(moved, keys, objects.WithEdits(moved.ObjectEdits)));
        Console.WriteLine($"MESS   Formular {copied?.Form.RawData?.Length / 1e3:0.0} kB");
        Step("Einfügen (B7)", () => ContentObjectEditor.Insert(moved, copied!.Value.Form, Matrix.Translate(10, -10)));
        items[0] = moved;
        Step("Speichern", () =>
        {
            using var output = new MemoryStream();
            PdfWriter.Write(doc, items, output);
        });
    }
}
