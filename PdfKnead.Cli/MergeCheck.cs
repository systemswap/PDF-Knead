using System.Diagnostics;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;

namespace PdfKnead.Cli;

/// <summary>
/// <c>pdf-knead speichertest &lt;folder&gt; --zusammenfuehren</c> (KONZEPT.md, section 5.2, „Seiten zusammenführen“): the files of the folder
/// in pairs, file B appended to file A both ways the window offers — into the open file A, and as a new file (built on a copy of A in its
/// current state, as the program does) —, every third pair without the bookmarks of the files taken in. Each result is saved, reopened and
/// checked: number and look of the pages (pixel equal, same text), the field names (those of B renamed exactly as „Danach nicht mehr da“
/// announced), the field values, the bookmarks, and whether what the notes say is lost is really gone — and what is gone was announced.
/// </summary>
internal static class MergeCheck
{
    private static readonly PdfName Names = PdfName.Get("Names"), EmbeddedFiles = PdfName.Get("EmbeddedFiles"), PageLabels = PdfName.Get("PageLabels"),
        AcroForm = PdfName.Get("AcroForm"), Xfa = PdfName.Get("XFA"), Kids = PdfName.Get("Kids"), First = PdfName.Get("First"), Next = PdfName.Get("Next"),
        Annots = PdfName.Get("Annots"), Metadata = PdfName.Get("Metadata"), Parent = PdfName.Get("Parent"), T = PdfName.Get("T");

    public static int Run(List<string> files, Dictionary<string, string> passwords, int maxPages, string? keep)
    {
        int ok = 0, failed = 0, skipped = 0, pairs = 0;
        var watch = Stopwatch.StartNew();
        var readable = new List<(string File, string? Password)>();
        foreach (var file in files)
        {
            var password = passwords.GetValueOrDefault(Path.GetFileName(file));
            try
            {
                using var probe = PdfDocument.Open(file, password);
                if (probe.PageCount > 0 && probe.PageCount <= 200) readable.Add((file, password));
                else skipped++;
            }
            catch (Exception)
            {
                skipped++;
            }
        }
        for (var i = 0; i + 1 < readable.Count; i += 2)
        {
            pairs++;
            var (fileA, pwA) = readable[i];
            var (fileB, pwB) = readable[i + 1];
            var keepBookmarks = pairs % 3 != 0;
            var name = $"{Path.GetFileName(fileA)} + {Path.GetFileName(fileB)}";
            var problems = new List<string>();
            try
            {
                using var a = PdfDocument.Open(fileA, pwA);
                using var b = PdfDocument.Open(fileB, pwB);
                Check(a, pwA, b, pwB, keepBookmarks, asNewFile: false, maxPages, keep, name, problems);
                Check(a, pwA, b, pwB, keepBookmarks, asNewFile: true, maxPages, keep, name, problems);
            }
            catch (Exception e)
            {
                problems.Add($"{e.GetType().Name}: {e.Message}");
            }
            if (problems.Count == 0) ok++;
            else
            {
                failed++;
                Console.WriteLine($"{name}: {string.Join("; ", problems.Take(6))}{(problems.Count > 6 ? $" … ({problems.Count})" : "")}");
            }
        }
        Console.WriteLine($"Paare: {pairs} (je in die geöffnete Datei und als neue Datei), bestanden: {ok}, Abweichungen: {failed}, " +
                          $"übersprungen (unlesbar, ohne Seiten, über 200 Seiten): {skipped}, Zeit {watch.Elapsed.TotalSeconds:0.0} s");
        return failed == 0 ? 0 : 1;
    }

    private static void Check(PdfDocument a, string? pwA, PdfDocument b, string? pwB, bool keepBookmarks, bool asNewFile, int maxPages, string? keep,
        string name, List<string> problems)
    {
        var way = asNewFile ? "neu" : "in A";
        var itemsB = PageItem.From(b);
        PdfDocument main, partA;
        PageItem[] itemsA;
        PdfDocument? copy = null;
        if (asNewFile)
        {
            // as the program does: the open document's pages from a copy of its current state, the new file starts from an empty one
            var made = InDocumentRedaction.Apply(a, PageItem.From(a), new PdfSaveOptions { ImageCodec = SkiaImageCodec.Instance, Version = SaveCheck.Version }, pwA, redact: false);
            copy = made.Document;
            itemsA = made.Pages;
            partA = copy;
            main = copy;   // as the program does: the new file is built on the copy of the open document
        }
        else
        {
            itemsA = PageItem.From(a);
            partA = a;
            main = a;
        }
        try
        {
            // what „Danach nicht mehr da“ says about B (the target's names first, as the window counts them)
            var taken = asNewFile ? new HashSet<string>(StringComparer.Ordinal) : MergeNotes.FieldNames(a);
            if (asNewFile)
                foreach (var n in MergeNotes.FieldNames(partA)) taken.Add(n);   // in the window the open document's pages come first as well
            var notes = MergeNotes.For(b, pwB is not null, [.. b.Pages], keepBookmarks, taken, partA);
            HashSet<PdfDocument> leftOut = new(ReferenceEqualityComparer.Instance);
            if (!keepBookmarks) leftOut.Add(b);
            byte[] data;
            using (var output = new MemoryStream())
            {
                PdfWriter.Write(main, [.. itemsA, .. itemsB], output, new PdfSaveOptions
                {
                    ImageCodec = SkiaImageCodec.Instance,
                    OutlinesLeftOut = leftOut.Count > 0 ? leftOut : null,
                    Version = SaveCheck.Version,
                });
                data = output.ToArray();
            }
            if (keep is not null) File.WriteAllBytes(Path.Combine(keep, $"{Path.GetFileNameWithoutExtension(a.FilePath)}-{way}.pdf".Replace(' ', '_')), data);
            // the protection of the open file applies, also to a new file built on its copy (the result opens with its password)
            using var merged = PdfDocument.Open(data, pwA);
            if (merged.WasRepaired) problems.Add($"{way}: Ergebnis musste repariert werden");
            if (merged.PageCount != partA.PageCount + b.PageCount)
            {
                problems.Add($"{way}: Seiten {partA.PageCount} + {b.PageCount} → {merged.PageCount}");
                return;
            }
            if (a.Security is not null && merged.Security is null) problems.Add($"{way}: Schutz der geöffneten Datei fehlt");

            // pages: look and text as in their file
            var images = new ImageCache();
            var pageProblems = new List<string>();
            for (var i = 0; i < Math.Min(partA.PageCount, maxPages); i++)
                SaveCheck.ComparePage(partA, merged, i, i, images, pageProblems);
            problems.AddRange(pageProblems.Select(p => $"{way}: A {p}"));
            pageProblems.Clear();
            for (var i = 0; i < Math.Min(b.PageCount, maxPages); i++)
                SaveCheck.ComparePage(b, merged, i, partA.PageCount + i, images, pageProblems);
            problems.AddRange(pageProblems.Select(p => $"{way}: B {p}"));

            // field names: those of A, those of B renamed as announced — nothing else, no name twice (fields without a widget on any
            // page do not come along: an invisible signature, fields of a broken form)
            var names = MergeNotes.FieldNames(merged);
            var placed = WithWidget(partA);
            foreach (var missing in taken.Except(names).Where(n => placed.Contains(n) || !MergeNotes.FieldNames(partA).Contains(n)).Take(3))
                problems.Add($"{way}: Feld „{missing}“ fehlt");
            foreach (var extra in names.Except(taken).Take(3)) problems.Add($"{way}: Feld „{extra}“ nicht angekündigt");
            // field values per page in widget order
            for (var i = 0; i < Math.Min(b.PageCount, maxPages); i++)
                if (!Values(b.Pages[i]).SequenceEqual(Values(merged.Pages[partA.PageCount + i])))
                    problems.Add($"{way}: S. {partA.PageCount + i + 1}: Feldwerte weichen ab");

            // bookmarks: all of A (or its copy) and, if taken along, all of B — each inserted file's under a branch of its own (+1)
            int Branch(int n) => n > 0 ? n + 1 : 0;
            var expectedOutlines = Outlines(partA) + (keepBookmarks ? Branch(Outlines(b)) : 0);
            if (Outlines(merged) != expectedOutlines) problems.Add($"{way}: Lesezeichen {Outlines(merged)} statt {expectedOutlines}");

            // the notes against the result: announced as lost → gone; gone → announced (for B; A's own losses are the target's)
            var said = new HashSet<string>(notes, StringComparer.Ordinal);
            var mainHad = Attachments(a);
            if (Attachments(b) > 0 && !said.Contains(Tr.T("Dateianhänge kommen nicht mit"))) problems.Add($"{way}: Anhänge von B ohne Ansage");
            if (Attachments(merged) != mainHad) problems.Add($"{way}: Anhänge {Attachments(merged)} statt {mainHad}");
            if (merged.Catalog?.ContainsKey(PageLabels) == true && b.Catalog?.ContainsKey(PageLabels) == true)
                problems.Add($"{way}: Seitennummerierung von B angekündigt als entfallen, ist aber da");
            if (merged.Catalog?.GetDictionary(AcroForm)?.ContainsKey(Xfa) == true) problems.Add($"{way}: XFA ist noch da");
            var signatures = DigitalSignatures.Find(b).Count;
            if (signatures > 0 && !said.Contains(signatures == 1 ? Tr.T("Digitale Signatur → ungültig") : Tr.T("{0} digitale Signaturen → ungültig", signatures)))
                problems.Add($"{way}: Signatur von B ohne Ansage");
            if (a.Info.Title is { } title && merged.Info.Title != title) problems.Add($"{way}: Titel der geöffneten Datei fehlt");
            if (a.Catalog?.ContainsKey(Metadata) == true && merged.Catalog?.ContainsKey(Metadata) != true) problems.Add($"{way}: Metadaten der geöffneten Datei fehlen");
        }
        finally
        {
            copy?.Dispose();
        }
    }

    /// <summary>Names of the top-level fields that have a widget on one of the pages.</summary>
    private static HashSet<string> WithWidget(PdfDocument doc)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in doc.Pages)
            if (page.Dictionary.Get(Annots) is PdfArray annots)
                for (var i = 0; i < annots.Count && i < 10_000; i++)
                    if (annots.Get(i) is PdfDictionary annot && FormField.Of(annot) is { } field)
                    {
                        var top = field.Field;
                        for (var depth = 0; depth < 32 && top.GetDictionary(Parent) is { } parent; depth++) top = parent;
                        if (top.GetString(T)?.ToText() is { Length: > 0 } name) names.Add(name);
                    }
        return names;
    }

    /// <summary>Values of the widgets of a page in /Annots order.</summary>
    private static List<string> Values(PdfPage page)
    {
        var values = new List<string>();
        if (page.Dictionary.Get(Annots) is not PdfArray annots) return values;
        for (var i = 0; i < annots.Count && i < 10_000; i++)
            if (annots.Get(i) is PdfDictionary annot && FormField.Of(annot) is { } field)
                values.Add(field.Kind is FormFieldKind.CheckBox or FormFieldKind.Radio ? field.State : field.StoredValue);
        return values;
    }

    /// <summary>Number of bookmarks (all levels, at most 100 000).</summary>
    private static int Outlines(PdfDocument doc)
    {
        if (doc.Catalog?.GetDictionary(PdfName.Outlines) is not { } root) return 0;
        var count = 0;
        var stack = new Stack<PdfDictionary>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        if (root.GetDictionary(First) is { } first) stack.Push(first);
        while (stack.Count > 0 && count < 100_000)
        {
            var item = stack.Pop();
            if (!seen.Add(item)) continue;
            count++;
            if (item.GetDictionary(Next) is { } next) stack.Push(next);
            if (item.GetDictionary(First) is { } child) stack.Push(child);
        }
        return count;
    }

    /// <summary>Number of document attachments (name tree /EmbeddedFiles).</summary>
    private static int Attachments(PdfDocument doc)
    {
        if (doc.Catalog?.GetDictionary(Names)?.GetDictionary(EmbeddedFiles) is not { } tree) return 0;
        var count = 0;
        var stack = new Stack<(PdfDictionary Node, int Depth)>();
        stack.Push((tree, 0));
        while (stack.Count > 0 && count < 100_000)
        {
            var (node, depth) = stack.Pop();
            if (node.Get(Names) is PdfArray pairs) count += pairs.Count / 2;
            if (depth < 32 && node.Get(Kids) is PdfArray kids)
                for (var i = 0; i < kids.Count && i < 10_000; i++)
                    if (kids.Get(i) is PdfDictionary kid) stack.Push((kid, depth + 1));
        }
        return count;
    }
}
