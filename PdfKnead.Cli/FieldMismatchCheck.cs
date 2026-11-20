using System.Diagnostics;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Cli;

/// <summary>
/// "pdf-knead speichertest … --feldwerte": names every file in which „Dokument bereinigen“ finds form fields that show something other than
/// their stored value (docs/RECHERCHE-VERSTECKEN.md, B-7) — over a test collection, so that one sees whether the finding gives false alarms —,
/// cleans them up and checks the copy: no such field remains, and every field keeps its value.
/// </summary>
internal static class FieldMismatchCheck
{
    public static int Run(IReadOnlyList<string> files, Dictionary<string, string> passwords)
    {
        int found = 0, fields = 0, failed = 0, skipped = 0, withForm = 0;
        var total = Stopwatch.StartNew();
        foreach (var file in files)
        {
            var password = passwords.GetValueOrDefault(Path.GetFileName(file));
            PdfDocument original;
            try
            {
                original = PdfDocument.Open(file, password);
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
                    if (items.Any(i => i.Elements.Any(e => e is ForeignAnnotation { Kind: "Widget" }))) withForm++;
                    if (DocumentCleanup.Inspect(original, items).FirstOrDefault(f => f.Item == CleanupItems.FieldValueMismatch) is not { } finding) continue;
                    found++;
                    fields += finding.Count;
                    Console.WriteLine($"{Path.GetFileName(file)}: {finding.Count} Felder ({finding.Detail})");
                    using var output = new MemoryStream();
                    PdfWriter.Write(original, items, output, new PdfSaveOptions { Cleanup = CleanupItems.FieldValueMismatch });
                    using var copy = PdfDocument.Open(output.ToArray(), password);
                    var after = PageItem.From(copy);
                    if (DocumentCleanup.Inspect(copy, after).FirstOrDefault(f => f.Item == CleanupItems.FieldValueMismatch) is { } left)
                        problems.Add($"danach noch {left.Count} Felder ({left.Detail})");
                    var before = Values(items);
                    foreach (var (name, value) in Values(after))
                        if (before.TryGetValue(name, out var old) && old != value) problems.Add($"Wert von „{name}“ geändert");
                }
                catch (Exception e)
                {
                    problems.Add($"{e.GetType().Name}: {e.Message}");
                }
                if (problems.Count > 0)
                {
                    failed++;
                    Console.WriteLine($"  Fehler: {string.Join("; ", problems.Take(4))}");
                }
            }
        }
        Console.WriteLine($"Dateien: {files.Count}, mit Formularfeldern: {withForm}, mit abweichenden Feldern: {found} ({fields} Felder), Fehler: {failed}, " +
                          $"unlesbar: {skipped}, Zeit {total.Elapsed.TotalSeconds:0.0} s");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>The stored value of every field, by its full name (the first widget counts).</summary>
    private static Dictionary<string, string> Values(IEnumerable<PageItem> items)
    {
        var values = new Dictionary<string, string>();
        foreach (var item in items)
            foreach (var element in item.Elements)
                if (element is ForeignAnnotation foreign && FormField.Of(foreign) is { Kind: not (FormFieldKind.PushButton or FormFieldKind.Signature) } field)
                    values.TryAdd(field.Name, field.StoredValue);
        return values;
    }
}
