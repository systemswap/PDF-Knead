using System.Diagnostics;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;

namespace PdfKnead.Cli;

/// <summary>
/// <c>pdf-knead speichertest &lt;folder&gt; --verborgen-schwaerzen</c> (see KONZEPT.md, section 5.18): per file, finds the hidden text
/// of the first pages, marks every spot as the „Alle“ boxes would, applies it as „Schwärzungen anwenden“ does and saves. Checked: no
/// hidden spot is left; no word of the removed text is still in the file (searched with the leak search, as far as the word does not
/// also appear visibly, in a font program, in the data of an embedded page or in a name of the file's syntax); no mark found nothing to remove. Reported besides: pages whose search was incomplete, and how far the visible
/// page changed (only allowed where the rounded gap moves visible text after a removed word by at most a quarter of a point).
/// </summary>
internal static class HiddenRedactCheck
{
    public static int Run(List<string> files, Dictionary<string, string> passwords, int maxPages, string? keep = null)
    {
        int ok = 0, skipped = 0, failed = 0, clean = 0, incomplete = 0, changedPages = 0, worstChange = 0, moved = 0;
        long spots = 0, glyphs = 0;
        var findTime = TimeSpan.Zero;
        var applyTime = TimeSpan.Zero;
        var watch = Stopwatch.StartNew();
        foreach (var file in files)
        {
            var password = passwords.GetValueOrDefault(Path.GetFileName(file));
            PdfDocument original;
            try
            {
                original = PdfDocument.Open(file, password);
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
                    var pages = Math.Min(original.PageCount, maxPages);
                    var items = PageItem.From(original);
                    var hiddenWords = new HashSet<string>(StringComparer.Ordinal);
                    var marked = new Dictionary<int, List<PdfRectangle>>();
                    var found = 0;
                    var partial = new HashSet<int>();   // pages whose search was incomplete: what is left there is not a failure
                    var t = Stopwatch.StartNew();
                    for (var i = 0; i < pages; i++)
                    {
                        var scan = HiddenText.Scan(original, original.Pages[i]);
                        if (!scan.Complete)
                        {
                            incomplete++;
                            partial.Add(i);
                            Console.WriteLine($"{Path.GetFileName(file)}: Seite {i + 1} nicht vollständig geprüft");
                        }
                        if (scan.Spots.Count == 0) continue;
                        found += scan.Spots.Count;
                        glyphs += scan.Spots.Sum(s => s.Glyphs);
                        foreach (var spot in scan.Spots)
                            foreach (var word in Words(spot.Text)) hiddenWords.Add(word);
                        marked[i] = [.. scan.Spots.SelectMany(HiddenText.MarkAreas)];
                        items[i] = items[i].WithElements([.. items[i].Elements,
                            .. scan.Spots.Select(s => RedactionElement.FromPageAreas(HiddenText.MarkAreas(s)) with { Hidden = true })]);
                    }
                    findTime += t.Elapsed;
                    if (found == 0)
                    {
                        clean++;
                        continue;
                    }
                    spots += found;
                    t.Restart();
                    var idle = new List<int>();
                    var (applied, adopted) = InDocumentRedaction.Apply(original, items, new PdfSaveOptions { ImageCodec = SkiaImageCodec.Instance }, password,
                        report: r => idle.AddRange(r.IdleHiddenMarks));
                    byte[] data;
                    using (applied)
                    {
                        using var output = new MemoryStream();
                        PdfWriter.Write(applied, adopted, output, new PdfSaveOptions { ImageCodec = SkiaImageCodec.Instance });
                        data = output.ToArray();
                    }
                    applyTime += t.Elapsed;
                    if (keep is not null) File.WriteAllBytes(Path.Combine(keep, Path.GetFileName(file)), data);
                    if (idle.Count > 0) problems.Add($"Markierung ohne Wirkung auf Seite {string.Join(", ", idle.Select(p => p + 1))}");

                    using var copy = PdfDocument.Open(data, password);
                    var visible = new System.Text.StringBuilder();
                    for (var i = 0; i < Math.Min(pages, copy.PageCount); i++)
                    {
                        var rest = HiddenText.Find(copy, copy.Pages[i]);
                        // left in a marked area: a failure; elsewhere: visible characters the rounded gap moved by up to 0.25 pt (under a
                        // cover, out of a clip) — reported, not counted
                        var areas = marked.GetValueOrDefault(i) ?? [];
                        var inside = rest.Where(r => areas.Any(a => a.Left - 1 <= (r.Bounds.Left + r.Bounds.Right) / 2 && (r.Bounds.Left + r.Bounds.Right) / 2 <= a.Right + 1 &&
                            a.Bottom - 1 <= (r.Bounds.Bottom + r.Bounds.Top) / 2 && (r.Bounds.Bottom + r.Bounds.Top) / 2 <= a.Top + 1)).ToList();
                        moved += rest.Count - inside.Count;
                        if (rest.Count > inside.Count)
                            Console.WriteLine($"{Path.GetFileName(file)}: Seite {i + 1}: {rest.Count - inside.Count} sichtbare Stellen danach verborgen („{Shorten(rest.First(r => !inside.Contains(r)).Text)}“)");
                        if (inside.Count > 0 && !partial.Contains(i)) problems.Add($"Seite {i + 1}: noch {inside.Count} verborgene Stellen („{Shorten(inside[0].Text)}“)");
                        visible.Append(PageText.Create(PageInterpreter.Build(copy, copy.Pages[i]).Text).Text).Append(' ');
                        var change = Change(original, original.Pages[i], copy, copy.Pages[i]);
                        if (change > 2)
                        {
                            changedPages++;
                            Console.WriteLine($"{Path.GetFileName(file)}: Seite {i + 1}: sichtbar verändert, {change} Stufen");
                        }
                        worstChange = Math.Max(worstChange, change);
                    }
                    // the pages not searched keep their hidden text
                    for (var i = pages; i < copy.PageCount; i++)
                        foreach (var g in PageInterpreter.BuildText(copy, copy.Pages[i])) visible.Append(g.Text);
                    // words of the removed text must be gone from the file — unless they also appear visibly, or outside the pages
                    // (document properties, bookmarks …: the same file with a blank page instead of all pages)
                    var shown = visible.ToString();
                    var elsewhere = Elsewhere(original) + " " + Names(copy);   // names of the syntax: see RunCleanup
                    var secret = hiddenWords.Where(w => !shown.Contains(w, StringComparison.OrdinalIgnoreCase) && !elsewhere.Contains(w, StringComparison.OrdinalIgnoreCase))
                        .Take(200).ToList();
                    if (secret.Count > 0)
                    {
                        using var blank = BlankPage.CreateDocument(612, 792, 1);
                        using var rest = new MemoryStream();
                        PdfWriter.Write(original, PageItem.From(blank), rest, new PdfSaveOptions());
                        var outsidePages = rest.ToArray();
                        secret = [.. secret.Where(w => LeakSearch.Find(outsidePages, [w], password, out _).Count == 0)];
                    }
                    var leaks = secret.Count == 0 ? [] : LeakSearch.Find(data, secret, password, out _);
                    if (leaks.Count > 0) problems.Add($"noch in der Datei: {string.Join(", ", leaks.Take(3).Select(Shorten))}");
                }
                catch (Exception e)
                {
                    problems.Add($"{e.GetType().Name}: {e.Message}");
                }
                if (problems.Count == 0)
                {
                    ok++;
                }
                else
                {
                    failed++;
                    Console.WriteLine($"{Path.GetFileName(file)}: {string.Join("; ", problems.Take(4))}");
                }
            }
        }
        Console.WriteLine($"Dateien: {files.Count}, ohne Verborgenes: {clean}, bestanden: {ok}, Abweichungen: {failed}, unlesbar: {skipped}");
        Console.WriteLine($"Stellen: {spots}, Zeichen: {glyphs}, Seiten nicht vollständig geprüft: {incomplete}, danach neu verborgen außerhalb der Markierungen: {moved}");
        Console.WriteLine($"Sichtbar verändert (mehr als 2 Stufen bei 72 dpi): {changedPages} Seiten, höchstens {worstChange} Stufen — erlaubt nur durch die gerundete Lücke");
        Console.WriteLine($"Zeit {watch.Elapsed.TotalSeconds:0.0} s (Finden {findTime.TotalSeconds:0.0} s, Anwenden {applyTime.TotalSeconds:0.0} s)");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// <c>pdf-knead speichertest &lt;folder&gt; --ocr-bereinigen</c>: per file, the words of the invisible text (text rendering mode 3,
    /// usually the OCR layer of a scan; also inside forms), then saved as „Dokument bereinigen“ with „Unsichtbarer Text“ does. Checked: the
    /// cleanup no longer finds invisible text, and no word of it is still in the file — searched with the leak search in every stream and
    /// string, as far as the word does not also appear visibly or outside the pages.
    /// </summary>
    public static int RunCleanup(List<string> files, Dictionary<string, string> passwords, string? keep = null)
    {
        int ok = 0, skipped = 0, failed = 0, clean = 0;
        long words = 0;
        var watch = Stopwatch.StartNew();
        foreach (var file in files)
        {
            var password = passwords.GetValueOrDefault(Path.GetFileName(file));
            PdfDocument original;
            try
            {
                original = PdfDocument.Open(file, password);
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
                    if (!DocumentCleanup.Inspect(original, items).Any(f => f.Item == CleanupItems.InvisibleText))
                    {
                        clean++;
                        continue;
                    }
                    var hiddenWords = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var page in original.Pages)
                    {
                        var run = new System.Text.StringBuilder();
                        foreach (var glyph in PageInterpreter.BuildText(original, page))
                            if (!glyph.Visible) run.Append(glyph.Text);
                            else if (run.Length > 0) run.Append(' ');
                        foreach (var word in Words(run.ToString())) hiddenWords.Add(word);
                    }
                    words += hiddenWords.Count;
                    byte[] data;
                    using (var output = new MemoryStream())
                    {
                        PdfWriter.Write(original, items, output, new PdfSaveOptions { ImageCodec = SkiaImageCodec.Instance, Cleanup = CleanupItems.InvisibleText });
                        data = output.ToArray();
                    }
                    if (keep is not null) File.WriteAllBytes(Path.Combine(keep, Path.GetFileName(file)), data);
                    using var copy = PdfDocument.Open(data, password);
                    if (DocumentCleanup.Inspect(copy, PageItem.From(copy)).Any(f => f.Item == CleanupItems.InvisibleText)) problems.Add("noch unsichtbarer Text");
                    var shown = new System.Text.StringBuilder();
                    foreach (var page in copy.Pages)
                        foreach (var glyph in PageInterpreter.BuildText(copy, page))
                            if (glyph.Visible) shown.Append(glyph.Text);
                            else shown.Append(' ');
                    var visible = shown.ToString();
                    // words that are also part of a name of the file's syntax (/BitsPerComponent, /ColorSpace, /BaseFont/Helvetica …) are
                    // found by the leak search there — not text
                    var elsewhere = Elsewhere(original) + " " + Names(copy);
                    var secret = hiddenWords.Where(w => !visible.Contains(w, StringComparison.OrdinalIgnoreCase) && !elsewhere.Contains(w, StringComparison.OrdinalIgnoreCase))
                        .Take(200).ToList();
                    if (secret.Count > 0)
                    {
                        using var blank = BlankPage.CreateDocument(612, 792, 1);
                        using var rest = new MemoryStream();
                        PdfWriter.Write(original, PageItem.From(blank), rest, new PdfSaveOptions());
                        var outsidePages = rest.ToArray();
                        secret = [.. secret.Where(w => LeakSearch.Find(outsidePages, [w], password, out _).Count == 0)];
                    }
                    var leaks = secret.Count == 0 ? [] : LeakSearch.Find(data, secret, password, out _);
                    if (leaks.Count > 0) problems.Add($"noch in der Datei: {string.Join(", ", leaks.Take(3).Select(Shorten))}");
                }
                catch (Exception e)
                {
                    problems.Add($"{e.GetType().Name}: {e.Message}");
                }
                if (problems.Count == 0)
                {
                    ok++;
                }
                else
                {
                    failed++;
                    Console.WriteLine($"{Path.GetFileName(file)}: {string.Join("; ", problems.Take(4))}");
                }
            }
        }
        Console.WriteLine($"Dateien: {files.Count}, ohne unsichtbaren Text: {clean}, bestanden: {ok}, Abweichungen: {failed}, unlesbar: {skipped}");
        Console.WriteLine($"Wörter des unsichtbaren Texts: {words}, Zeit {watch.Elapsed.TotalSeconds:0.0} s");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>All names of the objects of the file (keys and values), separated by spaces.</summary>
    private static string Names(PdfDocument doc)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var size = (int)Math.Clamp(doc.Trailer.GetInteger(Core.Pdf.Objects.PdfName.Size) ?? 0, 0, 2_000_000);
        for (var n = 1; n < size; n++)
        {
            Core.Pdf.Objects.PdfObject obj;
            try
            {
                obj = doc.GetObject(n);
            }
            catch (Core.Pdf.PdfException)
            {
                continue;
            }
            Collect(obj is Core.Pdf.Objects.PdfStream stream ? stream.Dictionary : obj, 0);
        }
        return string.Join(" ", names);

        void Collect(Core.Pdf.Objects.PdfObject? value, int depth)
        {
            switch (value)
            {
                case Core.Pdf.Objects.PdfName name:
                    names.Add(name.Value);
                    break;
                case Core.Pdf.Objects.PdfDictionary d when depth < 8:
                    foreach (var (key, v) in d.Entries)
                    {
                        names.Add(key.Value);
                        if (v is not Core.Pdf.Objects.PdfReference) Collect(v, depth + 1);
                    }
                    break;
                case Core.Pdf.Objects.PdfArray a when depth < 8:
                    for (var i = 0; i < a.Count && i < 10_000; i++)
                        if (a[i] is not Core.Pdf.Objects.PdfReference) Collect(a[i], depth + 1);
                    break;
            }
        }
    }

    /// <summary>
    /// Text the leak search also finds but that is not page content: embedded font programs (their names and sample texts) and the data
    /// of embedded pages and images (/PTEX.*, /Metadata — the gate „Schwärzen zuerst“ reports those, „Dokument bereinigen“ removes them).
    /// </summary>
    private static string Elsewhere(PdfDocument doc)
    {
        var text = new System.Text.StringBuilder();
        var fonts = new HashSet<Core.Pdf.Objects.PdfStream>(ReferenceEqualityComparer.Instance);
        foreach (var page in doc.Pages)
        {
            var resources = new List<Core.Pdf.Objects.PdfDictionary?> { page.Resources };
            foreach (var x in DocumentCleanup.EmbeddedForms(page.Resources))
            {
                resources.Add(x.Dictionary.GetDictionary(Core.Pdf.Objects.PdfName.Resources));
                foreach (var (key, _) in x.Dictionary.Entries)
                    if (DocumentCleanup.IsEmbeddingInfo(key) || key.Value == "Metadata") Append(x.Dictionary.Get(key), 0);
            }
            foreach (var r in resources)
                if (r?.GetDictionary(Core.Pdf.Objects.PdfName.Get("Font")) is { } dict)
                    foreach (var (key, _) in dict.Entries)
                        if (dict.Get(key) is Core.Pdf.Objects.PdfDictionary font)
                            foreach (var descriptor in Descriptors(font))
                                foreach (var file in (ReadOnlySpan<string>)["FontFile", "FontFile2", "FontFile3"])
                                    if (descriptor.Get(Core.Pdf.Objects.PdfName.Get(file)) is Core.Pdf.Objects.PdfStream program && fonts.Add(program))
                                        Append(program, 0);
        }
        return text.ToString();

        void Append(Core.Pdf.Objects.PdfObject? value, int depth)
        {
            switch (value)
            {
                case Core.Pdf.Objects.PdfString s:
                    text.Append(s.ToText()).Append(' ');
                    break;
                case Core.Pdf.Objects.PdfStream stream:
                    try
                    {
                        var data = doc.GetStreamData(stream);
                        text.Append(System.Text.Encoding.Latin1.GetString(data)).Append(' ').Append(System.Text.Encoding.BigEndianUnicode.GetString(data)).Append(' ');
                    }
                    catch (Core.Pdf.PdfException)
                    {
                    }
                    break;
                case Core.Pdf.Objects.PdfDictionary d when depth < 3:
                    foreach (var (key, _) in d.Entries) Append(d.Get(key), depth + 1);
                    break;
            }
        }

        static IEnumerable<Core.Pdf.Objects.PdfDictionary> Descriptors(Core.Pdf.Objects.PdfDictionary font)
        {
            if (font.GetDictionary(Core.Pdf.Objects.PdfName.Get("FontDescriptor")) is { } own) yield return own;
            if (font.GetArray(Core.Pdf.Objects.PdfName.Get("DescendantFonts")) is { Count: > 0 } kids &&
                kids.Get(0) is Core.Pdf.Objects.PdfDictionary cid && cid.GetDictionary(Core.Pdf.Objects.PdfName.Get("FontDescriptor")) is { } inner)
                yield return inner;
        }
    }

    /// <summary>Words of at least five letters or digits (shorter ones occur everywhere in a file).</summary>
    private static IEnumerable<string> Words(string text)
    {
        foreach (var word in text.Split([' ', '\n', ',', '.', ';', ':', '(', ')', '/', '"', '\''], StringSplitOptions.RemoveEmptyEntries))
            if (word.Length >= 5 && word.All(char.IsLetterOrDigit)) yield return word;
    }

    private static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length > 40 ? line[..40] + " …" : line;
    }

    /// <summary>Largest change of a colour channel between the two pages at 72 dpi.</summary>
    private static int Change(PdfDocument a, PdfPage pa, PdfDocument b, PdfPage pb)
    {
        using var first = SkiaRenderer.RenderPage(PageInterpreter.Build(a, pa), a, pa, 1, new ImageCache());
        using var second = SkiaRenderer.RenderPage(PageInterpreter.Build(b, pb), b, pb, 1, new ImageCache());
        if (first.Width != second.Width || first.Height != second.Height) return 255;
        var most = 0;
        var x1 = first.GetPixelSpan();
        var x2 = second.GetPixelSpan();
        for (var i = 0; i < x1.Length; i++)
            if ((i & 3) != 3) most = Math.Max(most, Math.Abs(x1[i] - x2[i]));
        return most;
    }
}
