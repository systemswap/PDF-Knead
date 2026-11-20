using System.Diagnostics;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Cli;

/// <summary>
/// "pdf-knead speichertest … --anmerkungen": in every file, moves all foreign annotations, saves and reopens — they must stand
/// offset and look as they did over the page before; then deletes all of them — none may remain, the page must look as without
/// them, and the form must have no empty entries. With --ablegen both copies are kept (…-verschoben.pdf, …-geloescht.pdf) for the checkers of
/// tools/erzeugungswege.
/// </summary>
internal static class ForeignCheck
{
    private const double Dx = 24, Dy = -18;

    public static int Run(IReadOnlyList<string> files, Dictionary<string, string> passwords, int maxPages, string? keep = null)
    {
        int ok = 0, skipped = 0, failed = 0, without = 0, annotations = 0;
        var total = Stopwatch.StartNew();
        foreach (var file in files)
        {
            PdfDocument original;
            try
            {
                original = PdfDocument.Open(file, passwords.GetValueOrDefault(Path.GetFileName(file)));
                if (original.PageCount == 0) throw new Core.Pdf.PdfException("keine Seiten");
            }
            catch (Exception)
            {
                skipped++;
                continue;
            }
            using (original)
            {
                var problems = new List<string>();
                try
                {
                    var items = PageItem.From(original);
                    var pages = Math.Min(items.Length, maxPages);
                    var count = 0;
                    for (var i = 0; i < pages; i++) count += items[i].Elements.Count(e => e is ForeignAnnotation);
                    if (count == 0)
                    {
                        without++;
                        continue;
                    }
                    annotations += count;
                    var moved = (PageItem[])items.Clone();
                    var deleted = (PageItem[])items.Clone();
                    for (var i = 0; i < pages; i++)
                    {
                        moved[i] = items[i].WithElements([.. items[i].Elements.Select(e => e is ForeignAnnotation
                            ? ElementAppearance.Moved(e, new PdfRectangle(e.Rect.Left + Dx, e.Rect.Bottom + Dy, e.Rect.Right + Dx, e.Rect.Top + Dy)) : e)]);
                        deleted[i] = items[i].WithElements([.. items[i].Elements.Where(e => e is not ForeignAnnotation)]);
                    }
                    var password = passwords.GetValueOrDefault(Path.GetFileName(file));
                    var stem = Path.GetFileNameWithoutExtension(file);
                    using (var copy = Save(original, moved, password, problems, keep is null ? null : Path.Combine(keep, stem + "-verschoben.pdf")))
                        for (var i = 0; copy is not null && i < pages; i++) CheckMoved(original, moved[i], copy, i, problems);
                    using (var copy = Save(original, deleted, password, problems, keep is null ? null : Path.Combine(keep, stem + "-geloescht.pdf")))
                        for (var i = 0; copy is not null && i < pages; i++) CheckDeleted(original, items[i], copy, i, problems);
                }
                catch (Exception e)
                {
                    problems.Add($"{e.GetType().Name}: {e.Message}");
                }
                if (problems.Count == 0) ok++;
                else
                {
                    failed++;
                    Console.WriteLine($"{Path.GetFileName(file)}: {string.Join("; ", problems.Take(4))}");
                }
            }
        }
        Console.WriteLine($"Dateien: {files.Count}, mit fremden Anmerkungen: {ok + failed} ({annotations} Anmerkungen), richtig: {ok}, Abweichungen: {failed}, " +
                          $"ohne: {without}, Original unlesbar: {skipped}, Zeit {total.Elapsed.TotalSeconds:0.0} s");
        return failed == 0 ? 0 : 1;
    }

    private static PdfDocument? Save(PdfDocument original, PageItem[] items, string? password, List<string> problems, string? keepAs = null)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(original, items, output, new PdfSaveOptions { ImageCodec = SkiaImageCodec.Instance });
        if (keepAs is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(keepAs)!);
            File.WriteAllBytes(keepAs, output.ToArray());
        }
        var copy = PdfDocument.Open(output.ToArray(), password);
        if (copy.WasRepaired) problems.Add("Kopie musste repariert werden");
        if (copy.PageCount == items.Length) return copy;
        problems.Add($"Seiten {items.Length} → {copy.PageCount}");
        copy.Dispose();
        return null;
    }

    /// <summary>Moved: the same annotations, each offset by (Dx, Dy), the image as displayed before saving.</summary>
    private static void CheckMoved(PdfDocument original, PageItem moved, PdfDocument copy, int index, List<string> problems)
    {
        var expected = moved.Elements.OfType<ForeignAnnotation>().ToList();
        var now = ForeignAnnotations.For(copy, copy.Pages[index]);
        if (now.Count != expected.Count)
        {
            problems.Add($"S. {index + 1}: {expected.Count} → {now.Count} Anmerkungen");
            return;
        }
        for (var k = 0; k < now.Count; k++)
        {
            var (a, b) = (expected[k].Rect, now[k].Rect);
            if (Math.Abs(a.Left - b.Left) > 0.01 || Math.Abs(a.Bottom - b.Bottom) > 0.01 || Math.Abs(a.Right - b.Right) > 0.01 || Math.Abs(a.Top - b.Top) > 0.01)
            {
                problems.Add($"S. {index + 1}: {expected[k].Kind} steht bei {b.Left:0.#},{b.Bottom:0.#} statt {a.Left:0.#},{a.Bottom:0.#}");
                return;
            }
        }
        // Display before saving (page without them, offset annotations drawn on top) against the saved page with its annotations
        Compare(original, moved, copy, index, problems, "verschoben");
    }

    /// <summary>Deleted: no foreign annotation left, the page as without them, no empty entries in the form.</summary>
    private static void CheckDeleted(PdfDocument original, PageItem item, PdfDocument copy, int index, List<string> problems)
    {
        if (ForeignAnnotations.For(copy, copy.Pages[index]).Count is var left and > 0) problems.Add($"S. {index + 1}: {left} Anmerkungen nicht gelöscht");
        if (copy.Catalog?.GetDictionary(PdfName.Get("AcroForm"))?.GetArray(PdfName.Get("Fields")) is { } fields)
            for (var i = 0; i < fields.Count; i++)
                if (fields.Get(i) is not PdfDictionary)
                {
                    problems.Add("leerer Eintrag in /Fields");
                    break;
                }
        Compare(original, item.WithElements([]), copy, index, problems, "gelöscht");
    }

    private static void Compare(PdfDocument original, PageItem before, PdfDocument copy, int index, List<string> problems, string what)
    {
        var page = original.Pages[index];
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var scale = Math.Min(0.5, 1500 / Math.Max(page.Width, page.Height));
        using var a = SkiaRenderer.RenderPage(PageInterpreter.Build(original, page, cancel.Token, withoutOwnElements: true), original, page, scale, new ImageCache());
        using (var canvas = new SKCanvas(a)) ElementOverlay.Draw(canvas, page, before.Elements, scale);
        using var b = SkiaRenderer.RenderPage(PageInterpreter.Build(copy, copy.Pages[index], cancel.Token), copy, copy.Pages[index], scale, new ImageCache());
        if (SaveCheck.Differing(a, b) is var differing and > 0) problems.Add($"S. {index + 1} {what}: {differing} Pixel anders");
    }
}
