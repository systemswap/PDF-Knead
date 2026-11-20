using System.Diagnostics;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Cli;

/// <summary>
/// <c>pdf-knead speichertest &lt;folder&gt; --textbearbeiten</c> (see KONZEPT.md, section 5.14): per file, converts up to two blocks per
/// page on the first pages as "edit text" does, replaces their text with a marker, saves and reopens. Checked: the marker is on the
/// page; the old text occurs once less (it is removed from the file, not just covered); all other text is still there (characters
/// counted, order irrelevant); after reopening, the block is editable again and the page itself no longer contains it.
/// </summary>
internal static class TextEditCheck
{
    private const int PagesPerFile = 3;

    public static int Run(List<string> files, Dictionary<string, string> passwords, int maxPages, string? keep = null)
    {
        int ok = 0, skipped = 0, failed = 0, edited = 0, noBlock = 0;
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
                    var markers = new Dictionary<int, List<(string Marker, string Old, int OldCount)>>();
                    var before = new Dictionary<int, string>();
                    for (var i = 0; i < Math.Min(Math.Min(items.Length, maxPages), PagesPerFile); i++)
                    {
                        var item = items[i];
                        var text = Squeeze(PageText.Create(PageInterpreter.BuildText(item.Source, item.Page)).Text);
                        // Blocks with enough text of their own that appears exactly like that on the page (otherwise "gone" cannot be checked)
                        var candidates = TextBlocks.Find(item.Source, item.Page)
                            .Where(b => b.Locked is null && Squeeze(b.Text).Length >= 8 && Count(text, Squeeze(b.Text)) > 0)
                            .OrderByDescending(b => b.Glyphs.Count).Take(2).ToList();
                        if (candidates.Count == 0) continue;
                        // Remove all glyphs of the chosen blocks at once (like two edits in a row)
                        var removed = Redactor.RemoveGlyphs(item.Source, item.Page, candidates.SelectMany(b => b.Glyphs).ToHashSet(),
                            candidates.SelectMany(b => b.Paths).ToHashSet());
                        if (removed is null)
                        {
                            problems.Add($"S. {i + 1}: Zeichen im Inhalt nicht gefunden");
                            continue;
                        }
                        var elements = new List<PageElement>(item.Elements);
                        var list = new List<(string, string, int)>();
                        foreach (var block in candidates)
                        {
                            // short enough for any block chosen (at least 8 characters): a longer marker wrapped inside a narrow block, as a long
                            // word does, and was not found in one piece (annotation-underline.pdf, issue19389.pdf — not a fault of the program)
                            var marker = $"PKE{i}x{list.Count}";
                            var element = EditedText.FromBlock(item.Source, block);
                            var style = element.Content.Spans[0].Style;
                            // the marked content whose characters this block takes, as the user interface gives it (one block at a time; 6.6 i)
                            var replaces = Redactor.RemoveGlyphs(item.Source, item.Page, block.Glyphs.ToHashSet(), block.Paths.ToHashSet())?.AlternateTextMcids;
                            elements.Add(RichLayout.Fitted(element with { Content = new RichText(marker, style), ReplacesMcids = replaces is null ? [] : [.. replaces.Order()] }));
                            list.Add((marker, Squeeze(block.Text), Count(text, Squeeze(block.Text))));
                        }
                        items[i] = item.WithEditedPage(removed, elements);
                        markers[i] = list;
                        // What should remain is the page without exactly the removed characters (overprinted copies of a text belong to the block)
                        before[i] = Subtract(text, Squeeze(removed.RemovedText));
                        edited += list.Count;
                    }
                    if (markers.Count == 0)
                    {
                        noBlock++;
                        continue;
                    }
                    using var output = new MemoryStream();
                    PdfWriter.Write(original, items, output, new PdfSaveOptions { Version = SaveCheck.Version });
                    if (keep is not null) File.WriteAllBytes(Path.Combine(keep, Path.GetFileName(file)), output.ToArray());
                    using var copy = PdfDocument.Open(output.ToArray(), password);
                    var reloaded = PageItem.From(copy);
                    foreach (var (i, list) in markers)
                    {
                        var after = Squeeze(PageText.Create(PageInterpreter.BuildText(copy, copy.Pages[i])).Text);
                        var expected = before[i];
                        foreach (var (marker, old, oldCount) in list)
                        {
                            if (Count(after, marker) != 1) problems.Add($"S. {i + 1}: neuer Text fehlt");
                            // The old text occurred oldCount times; one occurrence is replaced (if it also appears in another block, that one stays)
                            if (Count(after, old) > oldCount - 1) problems.Add($"S. {i + 1}: alter Text noch in der Datei");
                        }
                        // Remaining text: nothing is missing (characters counted, the order may change — the new block is at the end of the content).
                        // More is no loss: the text extraction merges overprinted copies (simulated bold) into one, but all are removed;
                        // old text that remains is caught by the check above
                        var rest = list.Aggregate(after, (t, m) => t.Replace(m.Marker, ""));
                        if (Subtract(expected, rest).Length > 0) problems.Add($"S. {i + 1}: übriger Text fehlt ({Difference(expected, rest)})");
                        // Reopened: the blocks are elements, the page itself is without them
                        var blocks = reloaded[i].Elements.OfType<RichTextElement>().Select(e => e.Content.Text).ToList();
                        foreach (var (marker, _, _) in list)
                            if (!blocks.Contains(marker)) problems.Add($"S. {i + 1}: nach dem Öffnen nicht bearbeitbar");
                        var bare = Squeeze(PageText.Create(PageInterpreter.BuildText(reloaded[i].Source, reloaded[i].Page)).Text);
                        if (list.Any(m => bare.Contains(m.Marker, StringComparison.Ordinal))) problems.Add($"S. {i + 1}: Block doppelt (Seite und Element)");
                    }
                    // a tagged file: nothing untagged and no marked content without an element that the original did not have (6.6 i)
                    if (original.Catalog?.ContainsKey(Core.Pdf.Objects.PdfName.Get("StructTreeRoot")) == true)
                    {
                        var was = Core.Pdf.Accessibility.TaggedContent.Analyse(original).Pages.ToDictionary(p => p.Page);
                        foreach (var page in Core.Pdf.Accessibility.TaggedContent.Analyse(copy).Pages)
                        {
                            var had = was.GetValueOrDefault(page.Page);
                            if (page.Untagged > (had?.Untagged ?? 0)) problems.Add($"S. {page.Page + 1}: ungetaggt neu ({page.Untagged - (had?.Untagged ?? 0)})");
                            if (page.McidsWithoutElement > (had?.McidsWithoutElement ?? 0)) problems.Add($"S. {page.Page + 1}: MCID ohne Element neu");
                        }
                    }
                }
                catch (Exception e)
                {
                    problems.Add($"{e.GetType().Name}: {e.Message}");
                }
                if (problems.Count == 0) ok++;
                else
                {
                    failed++;
                    Console.WriteLine($"{Path.GetFileName(file)}: {string.Join("; ", problems.Distinct().Take(4))}");
                }
            }
        }
        Console.WriteLine($"Dateien: {files.Count}, richtig: {ok}, Abweichungen: {failed}, ohne bearbeitbaren Block: {noBlock}, Original unlesbar: {skipped}; " +
                          $"{edited} Blöcke ersetzt, Zeit {watch.Elapsed.TotalSeconds:0.0} s");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Text without whitespace (block detection and text extraction place spaces and breaks differently).</summary>
    private static string Squeeze(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    private static int Count(string text, string part)
    {
        if (part.Length == 0) return 0;
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    /// <summary>Text without the characters of <paramref name="removed"/> (one each, wherever it occurs).</summary>
    private static string Subtract(string text, string removed)
    {
        var counts = new Dictionary<char, int>();
        foreach (var c in removed) counts[c] = counts.GetValueOrDefault(c) + 1;
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
            if (counts.TryGetValue(c, out var n) && n > 0) counts[c] = n - 1;
            else sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Which characters are missing or were added (abbreviated).</summary>
    private static string Difference(string expected, string actual)
    {
        var counts = new Dictionary<char, int>();
        foreach (var c in expected) counts[c] = counts.GetValueOrDefault(c) + 1;
        foreach (var c in actual) counts[c] = counts.GetValueOrDefault(c) - 1;
        string Show(IEnumerable<KeyValuePair<char, int>> chars) =>
            string.Concat(chars.Take(30).Select(k => new string(k.Key, Math.Min(Math.Abs(k.Value), 5))));
        return $"fehlt „{Show(counts.Where(k => k.Value > 0))}“, dazu „{Show(counts.Where(k => k.Value < 0))}“";
    }
}
