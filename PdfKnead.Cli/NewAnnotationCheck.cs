using System.Diagnostics;
using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Cli;

/// <summary>
/// <c>pdf-knead speichertest &lt;folder&gt; --neue-anmerkungen [--ablegen &lt;folder&gt;]</c> (docs/ABLAUFPLAN.md, 6.4 step 2;
/// docs/FEHLERGEDAECHTNIS.md, No. 633): every tagged file gets a note in the middle of its first page and a text field at its foot, is saved
/// and saved again. Each result is checked: both annotations have a /StructParent whose element names them (Annot, Form), the structure has
/// nothing the source did not have (<see cref="TaggedContent.ParentTree"/>, <see cref="TaggedContent.Nesting"/>), the second save leaves no
/// element behind, and the save names no reason „neue Anmerkungen oder Felder ohne Struktur“. With <c>--ablegen</c> the results of the first
/// save are kept for the reference checkers.
/// </summary>
internal static class NewAnnotationCheck
{
    public static int Run(List<string> files, Dictionary<string, string> passwords, string? keep)
    {
        int ok = 0, failed = 0, skipped = 0;
        var watch = Stopwatch.StartNew();
        foreach (var file in files)
        {
            var password = passwords.GetValueOrDefault(Path.GetFileName(file));
            PdfDocument? doc;
            try
            {
                doc = PdfDocument.Open(file, password);
            }
            catch (Exception)
            {
                skipped++;
                continue;
            }
            using (doc)
            {
                // tagged as the writer sees it (/MarkInfo /Marked true and a tree): only there do new annotations get an element
                if (!StructureRoles.IsTagged(doc.Catalog) || doc.PageCount == 0)
                {
                    skipped++;
                    continue;
                }
                var problems = new List<string>();
                try
                {
                    Check(doc, password, keep, problems);
                }
                catch (Exception e)
                {
                    problems.Add($"{e.GetType().Name}: {e.Message}");
                }
                if (problems.Count == 0) ok++;
                else
                {
                    failed++;
                    Console.WriteLine($"{Path.GetFileName(file)}: {string.Join("; ", problems.Take(6))}");
                }
            }
        }
        Console.WriteLine($"getaggte Dateien: {ok + failed}, bestanden: {ok}, Abweichungen: {failed}, übersprungen (unlesbar oder nicht getaggt): {skipped}, " +
                          $"Zeit {watch.Elapsed.TotalSeconds:0.0} s");
        return failed == 0 ? 0 : 1;
    }

    private static void Check(PdfDocument doc, string? password, string? keep, List<string> problems)
    {
        var box = doc.Pages[0].MediaBox;
        var (cx, cy) = ((box.Left + box.Right) / 2, (box.Bottom + box.Top) / 2);
        var note = new NoteElement { Rect = new PdfRectangle(cx, cy, cx + NoteElement.Size, cy + NoteElement.Size), Text = "Bitte prüfen" };
        var field = new TextElement { Rect = new PdfRectangle(box.Left + 72, box.Bottom + 36, box.Left + 272, box.Bottom + 56), IsField = true, FieldName = "Prüffeld" };
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([.. items[0].Elements, note, field]);
        byte[] data;
        PdfSaveResult result;
        using (var output = new MemoryStream())
        {
            result = PdfWriter.Write(doc, items, output, new PdfSaveOptions());
            data = output.ToArray();
        }
        if (keep is not null) File.WriteAllBytes(Path.Combine(keep, $"{Path.GetFileNameWithoutExtension(doc.FilePath)}-anmerkungen.pdf".Replace(' ', '_')), data);
        if (result.AccessibilityBreaks.Contains("neue Anmerkungen oder Felder ohne Struktur")) problems.Add("Grund „neue Anmerkungen oder Felder ohne Struktur“");
        using var saved = PdfDocument.Open(data, password);
        if (saved.Catalog?.GetDictionary(PdfName.Get("StructTreeRoot")) is null) return;   // nothing tagged was left to hang them into
        Tagged(saved, "erstes Speichern", problems);
        NothingNew(doc, saved, "erstes Speichern", problems);
        using var output2 = new MemoryStream();
        PdfWriter.Write(saved, PageItem.From(saved), output2, new PdfSaveOptions());
        using var again = PdfDocument.Open(output2.ToArray(), password);
        Tagged(again, "zweites Speichern", problems);
        NothingNew(doc, again, "zweites Speichern", problems);
        if (Count(again) != Count(saved)) problems.Add($"zweites Speichern: {Count(again)} Elemente statt {Count(saved)}");
    }

    /// <summary>The note and the field of the first page each have an element (Annot, Form) that names them.</summary>
    private static void Tagged(PdfDocument doc, string way, List<string> problems)
    {
        var tree = doc.Catalog?.GetDictionary(PdfName.Get("StructTreeRoot"))?.GetDictionary(PdfName.Get("ParentTree"));
        var found = 0;
        var annots = doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots")) ?? [];
        for (var i = 0; i < annots.Count; i++)
        {
            // only the two added here: the note and the field „Prüffeld“ (other annotations of the file keep theirs)
            if (annots.Get(i) is not PdfDictionary annot) continue;
            var subtype = annot.GetName(PdfName.Subtype)?.Value;
            var ours = subtype == "Text" && annot.GetString(PdfName.Contents)?.ToText() == "Bitte prüfen" ||
                       subtype == "Widget" && annot.GetString(PdfName.Get("T"))?.ToText()?.StartsWith("Prüffeld", StringComparison.Ordinal) == true;
            if (!ours) continue;
            found++;
            var expected = subtype == "Widget" ? "Form" : "Annot";
            if (tree is null || annot.GetInteger(PdfName.Get("StructParent")) is not { } key || NumberTree.Value(tree, key) is not PdfDictionary element)
            {
                problems.Add($"{way}: {expected} ohne Element");
                continue;
            }
            if (element.GetName(PdfName.Get("S"))?.Value != expected) problems.Add($"{way}: Element {element.GetName(PdfName.Get("S"))?.Value} statt {expected}");
        }
        if (found != 2) problems.Add($"{way}: {found} statt 2 neuer Anmerkungen");
    }

    /// <summary>What the result has more of than the source: each kind of the tree check, and breaks of the containment rules.</summary>
    private static void NothingNew(PdfDocument source, PdfDocument result, string way, List<string> problems)
    {
        var before = TaggedContent.ParentTree(source).CountBy(i => i.Kind).ToDictionary();
        foreach (var (kind, count) in TaggedContent.ParentTree(result).CountBy(i => i.Kind))
            if (count > before.GetValueOrDefault(kind)) problems.Add($"{way}: {kind} ({count - before.GetValueOrDefault(kind)} neu)");
        var nesting = TaggedContent.Nesting(source).ToHashSet();
        foreach (var broken in TaggedContent.Nesting(result).Where(v => !nesting.Contains(v)).Take(3)) problems.Add($"{way}: Einschluss {broken} neu");
    }

    /// <summary>The number of structure elements (each once).</summary>
    private static int Count(PdfDocument doc)
    {
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfObject?>([doc.Catalog?.GetDictionary(PdfName.Get("StructTreeRoot"))?.Get(PdfName.Get("K"))]);
        while (stack.TryPop(out var k) && seen.Count < 1_000_000)
            switch (k)
            {
                case PdfArray a:
                    for (var i = 0; i < a.Count; i++) stack.Push(a.Get(i));
                    break;
                case PdfDictionary e when e.ContainsKey(PdfName.Get("S")) && seen.Add(e):
                    stack.Push(e.Get(PdfName.Get("K")));
                    break;
            }
        return seen.Count;
    }
}
