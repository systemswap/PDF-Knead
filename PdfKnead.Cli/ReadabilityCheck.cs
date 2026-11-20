using System.Diagnostics;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;

namespace PdfKnead.Cli;

/// <summary>
/// "pdf-knead lesbarkeit <file|folder> [--seiten <n>]": the pages whose text cannot be read (docs/RECHERCHE-VERSTECKEN.md, B-6) or reads
/// suspiciously (B-5), as „Suchen und schwärzen“ names them — over a test collection, to see how often the notes appear.
/// </summary>
internal static class ReadabilityCheck
{
    public static int Run(string path, int maxPages)
    {
        var files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [path];
        int pages = 0, unreadablePages = 0, misreadPages = 0, skipped = 0;
        var total = Stopwatch.StartNew();
        foreach (var file in files)
        {
            try
            {
                using var doc = PdfDocument.Open(file);
                var unreadable = new List<string>();
                var misread = new List<string>();
                for (var i = 0; i < Math.Min(doc.PageCount, maxPages); i++)
                {
                    var (_, u, m) = PageInterpreter.BuildTextAndUnreadable(doc, doc.Pages[i]);
                    pages++;
                    if (u > 0) unreadable.Add($"{i + 1} ({u})");
                    if (m > 0) misread.Add($"{i + 1} ({m})");
                }
                unreadablePages += unreadable.Count;
                misreadPages += misread.Count;
                if (unreadable.Count + misread.Count > 0)
                    Console.WriteLine($"{Path.GetFileName(file)}:" + (unreadable.Count > 0 ? $" nicht lesbar S. {string.Join(", ", unreadable.Take(5))}" : "") +
                                      (misread.Count > 0 ? $" auffällig S. {string.Join(", ", misread.Take(5))}" : ""));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                skipped++;
            }
        }
        Console.WriteLine($"Dateien: {files.Count}, Seiten: {pages}, nicht lesbar: {unreadablePages} Seiten, auffällig: {misreadPages} Seiten, " +
                          $"unlesbar: {skipped}, Zeit {total.Elapsed.TotalSeconds:0.0} s");
        return 0;
    }
}
