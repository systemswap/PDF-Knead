using System.Diagnostics;
using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;

namespace PdfKnead.Cli;

/// <summary>
/// <c>pdf-knead speichertest &lt;folder&gt; --seiten-loeschen [--ablegen &lt;folder&gt;]</c> (docs/ABLAUFPLAN.md, 6.2 and 6.8; docs/FEHLERGEDAECHTNIS.md,
/// No. 626): every file of two pages or more written twice as deleting and extracting pages do — without its second page, and only its first
/// page. Each result is reopened and checked: number, look and text of the pages left, and its structure — by the check helpers of the core
/// (<see cref="TaggedContent.ParentTree"/>, <see cref="TaggedContent.Nesting"/>; 6.2c) nothing the source did not have already: no pair of the
/// parent tree and no entry of the /IDTree naming nothing, no /Pg to a page not written; and the file claims to be tagged (/MarkInfo
/// /Marked true) only with a structure tree that still has content. With <c>--ablegen</c> the results are kept for the reference checkers.
/// </summary>
internal static class PageDeleteCheck
{
    private static readonly PdfName StructTreeRoot = PdfName.Get("StructTreeRoot"), MarkInfo = PdfName.Get("MarkInfo"), Marked = PdfName.Get("Marked"),
        K = PdfName.Get("K");

    public static int Run(List<string> files, Dictionary<string, string> passwords, int maxPages, string? keep)
    {
        int ok = 0, failed = 0, skipped = 0, tagged = 0, untaggedAfter = 0;
        var watch = Stopwatch.StartNew();
        foreach (var file in files)
        {
            var password = passwords.GetValueOrDefault(Path.GetFileName(file));
            var problems = new List<string>();
            using var doc = TryOpen(file, password);
            if (doc is null)
            {
                skipped++;   // not readable, or protected without a known password: nothing to delete pages from
                continue;
            }
            try
            {
                if (doc.PageCount < 2 || doc.PageCount > 500)
                {
                    skipped++;
                    continue;
                }
                if (doc.Catalog?.ContainsKey(StructTreeRoot) == true) tagged++;
                var items = PageItem.From(doc);
                var kept = Enumerable.Range(0, doc.PageCount).Where(i => i != 1).ToArray();
                if (!Check(doc, password, items, kept, "ohne-s2", maxPages, keep, problems)) untaggedAfter++;
                if (!Check(doc, password, items, [0], "nur-s1", maxPages, keep, problems)) untaggedAfter++;
            }
            catch (Exception e)
            {
                problems.Add($"{e.GetType().Name}: {e.Message}");
            }
            if (problems.Count == 0) ok++;
            else
            {
                failed++;
                Console.WriteLine($"{Path.GetFileName(file)}: {string.Join("; ", problems.Take(6))}{(problems.Count > 6 ? $" … ({problems.Count})" : "")}");
            }
        }
        Console.WriteLine($"Dateien: {ok + failed} (je ohne Seite 2 und nur Seite 1), bestanden: {ok}, Abweichungen: {failed}, mit Strukturbaum: {tagged}, " +
                          $"davon Ergebnisse ohne getaggten Inhalt: {untaggedAfter}, übersprungen (unlesbar, unter 2 oder über 500 Seiten): {skipped}, " +
                          $"Zeit {watch.Elapsed.TotalSeconds:0.0} s");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>The file opened; null if it cannot be read or is protected without a known password.</summary>
    private static PdfDocument? TryOpen(string file, string? password)
    {
        try
        {
            return PdfDocument.Open(file, password);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Writes the pages <paramref name="kept"/> and checks the result; false if the source was tagged and the result is not.</summary>
    private static bool Check(PdfDocument doc, string? password, PageItem[] items, int[] kept, string way, int maxPages, string? keep, List<string> problems)
    {
        byte[] data;
        PdfSaveResult result;
        using (var output = new MemoryStream())
        {
            result = PdfWriter.Write(doc, [.. kept.Select(i => items[i])], output, new PdfSaveOptions { ImageCodec = SkiaImageCodec.Instance });
            data = output.ToArray();
        }
        if (keep is not null) File.WriteAllBytes(Path.Combine(keep, $"{Path.GetFileNameWithoutExtension(doc.FilePath)}-{way}.pdf".Replace(' ', '_')), data);
        using var saved = PdfDocument.Open(data, password);
        if (saved.WasRepaired) problems.Add($"{way}: Ergebnis musste repariert werden");
        if (saved.PageCount != kept.Length)
        {
            problems.Add($"{way}: {saved.PageCount} Seiten statt {kept.Length}");
            return true;
        }
        var images = new ImageCache();
        var pageProblems = new List<string>();
        for (var i = 0; i < Math.Min(kept.Length, maxPages); i++) SaveCheck.ComparePage(doc, saved, kept[i], i, images, pageProblems);
        problems.AddRange(pageProblems.Select(p => $"{way}: {p}"));

        // the structure: no target written as null, tagged only with content (No. 363, 523, 626)
        var catalog = saved.Catalog;
        var root = catalog?.GetDictionary(StructTreeRoot);
        var marked = catalog?.GetDictionary(MarkInfo)?.GetBoolean(Marked) == true;
        if (root is null)
        {
            if (marked && doc.Catalog?.ContainsKey(StructTreeRoot) == true) problems.Add($"{way}: als getaggt gekennzeichnet ohne Strukturbaum");
            return doc.Catalog?.ContainsKey(StructTreeRoot) != true;
        }
        if (root.Get(K) is null or PdfNull || root.Get(K) is PdfArray { Count: 0 }) problems.Add($"{way}: Strukturbaum ohne Inhalt");
        // what the result has more of than the source: each kind of the tree check, and breaks of the containment rules
        var before = TaggedContent.ParentTree(doc).CountBy(i => i.Kind).ToDictionary();
        foreach (var (kind, count) in TaggedContent.ParentTree(saved).CountBy(i => i.Kind))
            if (count > before.GetValueOrDefault(kind)) problems.Add($"{way}: {kind} ({count - before.GetValueOrDefault(kind)} neu)");
        var nesting = TaggedContent.Nesting(doc).ToHashSet();
        foreach (var broken in TaggedContent.Nesting(saved).Where(v => !nesting.Contains(v)).Take(3)) problems.Add($"{way}: Einschluss {broken} neu");
        // headings and references after pages went (6.6 h): an order that held before and breaks now without the save naming it, /Headers and
        // /Ref naming nothing
        if (!StructureReferences.OrderBroken(StructureReferences.HeadingLevels(doc)) && StructureReferences.OrderBroken(StructureReferences.HeadingLevels(saved)) &&
            !result.AccessibilityBreaks.Contains(PdfKnead.Core.Localization.Tr.T("Überschriftenfolge gebrochen")))
            problems.Add($"{way}: Überschriftenfolge gebrochen ohne Grund ({string.Join(" ", StructureReferences.HeadingLevels(saved).Take(4).Select(l => "H" + l))} …)");
        if (StructureReferences.DanglingHeaders(saved) - StructureReferences.DanglingHeaders(doc) is > 0 and var headers) problems.Add($"{way}: /Headers ins Leere ({headers} neu)");
        if (StructureReferences.DanglingRefs(saved) - StructureReferences.DanglingRefs(doc) is > 0 and var refs) problems.Add($"{way}: /Ref ins Leere ({refs} neu)");
        return true;
    }
}
