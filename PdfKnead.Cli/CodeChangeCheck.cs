using System.Diagnostics;
using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;

namespace PdfKnead.Cli;

/// <summary>
/// <c>pdf-knead speichertest &lt;folder&gt; --codes-aendern</c> (KONZEPT.md, section 5.19, step 3): on the first pages of every file, every
/// code the page search finds is changed as „Code ändern …“ does — planned, its area emptied as when redacting (white), a new code with
/// another content in its place — and the file saved. Checked: the new contents are read on the page, the old ones nowhere; the old
/// contents are nowhere in the file any more (leak search), unless they also stand on the page as text; the visible page changes only in
/// the emptied areas. Codes that cannot be changed (askew, part of a chain) are counted, not failed.
/// </summary>
internal static class CodeChangeCheck
{
    /// <summary>Another valid content of the same kind (GS1 data for a code that holds GS1 data).</summary>
    private static string Changed(BarcodeFormat format, bool gs1, int n) => gs1 ? "(01)04012345678901(10)G" + n : format switch
    {
        BarcodeFormat.Ean13 => "4012345678901",
        BarcodeFormat.Ean8 => "96385074",
        BarcodeFormat.UpcA => "036000291452",
        BarcodeFormat.UpcE => "01234565",
        BarcodeFormat.Itf => "12345678901231",
        BarcodeFormat.Code39 => "GEAENDERT " + n,
        BarcodeFormat.Gs1_128 => "(01)04012345678901(10)G" + n,
        _ => "GEAENDERT-" + n,
    };

    public static int Run(List<string> files, Dictionary<string, string> passwords, int maxPages, string? keep = null)
    {
        int ok = 0, skipped = 0, failed = 0, without = 0, changed = 0, refused = 0;
        var watch = Stopwatch.StartNew();
        var images = new ImageCache();
        foreach (var file in files)
        {
            var password = passwords.GetValueOrDefault(Path.GetFileName(file));
            PdfDocument doc;
            try
            {
                doc = PdfDocument.Open(file, password);
                if (doc.PageCount == 0) throw new Core.Pdf.PdfException("keine Seiten");
            }
            catch (Exception)
            {
                skipped++;
                continue;
            }
            using (doc)
            {
                var problems = new List<string>();
                try
                {
                    var items = PageItem.From(doc).ToArray();
                    var expected = new Dictionary<int, List<string>>();
                    var old = new List<(int Page, string Text, bool Shown)>();
                    var areas = new Dictionary<int, List<PdfRectangle>>();
                    var n = 0;
                    for (var i = 0; i < Math.Min(items.Length, maxPages); i++)
                    {
                        var page = doc.Pages[i];
                        var list = PageInterpreter.Build(doc, page);
                        var codes = PageBarcodes.FindAll(doc, page, list, [], images);
                        if (codes.Count == 0)
                        {
                            if (Environment.GetEnvironmentVariable("PDFKNEAD_OHNE_CODE") == "1") Console.WriteLine($"{Path.GetFileName(file)}: S. {i + 1} ohne Code");
                            continue;
                        }
                        var text = string.Concat(list.Text.Select(g => g.Text)).Replace(" ", "");
                        var plans = new List<CodeReplacementPlan>();
                        foreach (var code in codes)
                        {
                            if (CodeReplacement.Plan(doc, page, list, code, out _, images) is not { } plan)
                            {
                                refused++;
                                continue;
                            }
                            plans.Add(plan);
                            old.Add((i, code.Code.Text, text.Contains(code.Code.Text.Replace(" ", ""), StringComparison.Ordinal)));
                        }
                        if (plans.Count == 0) continue;
                        var emptied = Redactor.Apply(doc, page, [.. plans.Select(p => p.Area)], paper: true);
                        var elements = new List<PageElement>(items[i].Elements);
                        foreach (var plan in plans)
                        {
                            var content = Changed(plan.Element.Format, plan.Element.Gs1, ++n);
                            var element = plan.Element with { Text = content };
                            elements.Add(element);
                            (expected.TryGetValue(i, out var e) ? e : expected[i] = []).Add(ElementAppearance.CodeSymbolOf(element).Symbol?.Encoded ?? content);
                        }
                        items[i] = items[i].WithEmptiedArea(emptied, elements);
                        areas[i] = [.. plans.Select(p => p.Area)];
                        changed += plans.Count;
                    }
                    if (expected.Count == 0)
                    {
                        without++;
                        continue;
                    }
                    byte[] data;
                    using (var output = new MemoryStream())
                    {
                        PdfWriter.Write(doc, items, output, new PdfSaveOptions { ImageCodec = SkiaImageCodec.Instance });
                        data = output.ToArray();
                    }
                    if (keep is not null) File.WriteAllBytes(Path.Combine(keep, Path.GetFileName(file)), data);
                    using var copy = PdfDocument.Open(data, password);
                    foreach (var (i, contents) in expected)
                    {
                        var found = PageBarcodes.FindAll(copy, copy.Pages[i], PageInterpreter.Build(copy, copy.Pages[i]), [], images).Select(f => f.Code.Text).ToList();
                        foreach (var c in contents)
                            if (!found.Contains(c)) problems.Add($"S. {i + 1}: neuer Code „{Shorten(c)}“ nicht gelesen");
                        foreach (var o in old.Where(o => o.Page == i))
                            if (found.Contains(o.Text) && !contents.Contains(o.Text)) problems.Add($"S. {i + 1}: alter Code „{Shorten(o.Text)}“ noch lesbar");
                    }
                    // the old contents in the file: only where they also stand on the page as text (and are at least five characters)
                    var secret = old.Where(o => !o.Shown && o.Text.Length >= 5 && !expected.Values.Any(v => v.Contains(o.Text))).Select(o => o.Text).Distinct().Take(50).ToList();
                    var leaks = secret.Count == 0 ? [] : LeakSearch.Find(data, secret, password, out _);
                    if (leaks.Count > 0) problems.Add($"noch in der Datei: {string.Join(", ", leaks.Take(3).Select(Shorten))}");
                }
                catch (Exception e)
                {
                    problems.Add($"{e.GetType().Name}: {e.Message}");
                }
                if (problems.Count == 0) ok++;
                else
                {
                    failed++;
                    Console.WriteLine($"{Path.GetFileName(file)}: {string.Join("; ", problems.Take(8))}{(problems.Count > 8 ? $" … ({problems.Count})" : "")}");
                }
            }
        }
        Console.WriteLine($"Dateien: {files.Count}, ohne Codes: {without}, bestanden: {ok}, Abweichungen: {failed}, unlesbar: {skipped}");
        Console.WriteLine($"Codes geändert: {changed}, nicht änderbar (schräg, Folge): {refused}, Zeit {watch.Elapsed.TotalSeconds:0.0} s");
        return failed == 0 ? 0 : 1;
    }

    private static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length > 40 ? line[..40] + " …" : line;
    }
}
