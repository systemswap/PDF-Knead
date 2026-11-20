using PdfKnead.Core.Pdf.Document;

namespace PdfKnead.Cli;

/// <summary>
/// "pdf-knead uebersprungen &lt;folder&gt;": the files the checks of the command line pass over, each with its reason — not readable (the
/// exception), encrypted, repaired, without pages (docs/normabgleich/sonderregeln.md, C01: does „skipped“ hide a reading error?). The counts
/// the checks print say how many; this says which, so that each can be compared with other readers.
/// </summary>
internal static class SkippedFiles
{
    public static int Run(string path)
    {
        var files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [path];
        int unreadable = 0, encrypted = 0, repaired = 0, empty = 0;
        foreach (var file in files)
        {
            var name = Path.GetRelativePath(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? ".", file);
            try
            {
                using var doc = PdfDocument.Open(file);
                var reasons = new List<string>();
                if (doc.IsEncrypted)
                {
                    encrypted++;
                    reasons.Add("verschlüsselt");
                }
                if (doc.WasRepaired)
                {
                    repaired++;
                    reasons.Add("repariert");
                }
                if (doc.PageCount == 0)
                {
                    empty++;
                    reasons.Add("ohne Seiten");
                }
                if (reasons.Count > 0) Console.WriteLine($"{name}\t{string.Join(", ", reasons)}");
            }
            catch (Exception e)
            {
                unreadable++;
                Console.WriteLine($"{name}\tunlesbar: {e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}");
            }
        }
        Console.WriteLine($"Dateien: {files.Count}, unlesbar: {unreadable}, verschlüsselt: {encrypted}, repariert: {repaired}, ohne Seiten: {empty}");
        return 0;
    }
}
