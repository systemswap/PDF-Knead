using PdfKnead.Core.Ocr;

namespace PdfKnead.Cli;

/// <summary>
/// „pdf-knead modelle“: which models of the text recognition and the field detector are there, each checked by size and SHA-256. Nothing is
/// loaded — the models come with the program (decided on 13.11.2026); the publish scripts copy them from „Modelle/ocr“.
/// </summary>
internal static class ModelCheck
{
    /// <summary>Runs the command.</summary>
    /// <param name="folder">The folder of the models; null for the one next to the program.</param>
    /// <returns>Exit code: 0 if every file is there and has its checksum, 1 otherwise.</returns>
    public static int Run(string? folder)
    {
        var target = folder ?? OcrModels.ProgramFolder;
        var ok = true;
        foreach (var f in OcrModels.Everything)
        {
            var path = Path.Combine(target, f.Folder, f.Name);
            string state;
            if (!File.Exists(path)) state = "fehlt";
            else if (new FileInfo(path).Length != f.Size) state = "falsche Größe";
            else
            {
                using var stream = File.OpenRead(path);
                state = OcrModels.Matches(f, stream) ? "da, geprüft" : "falsche Prüfsumme";
            }
            ok &= state == "da, geprüft";
            Console.WriteLine($"{state,-18} {f.Size,12:N0}  {f.Folder}/{f.Name}");
        }
        Console.WriteLine($"Ordner: {target}");
        return ok ? 0 : 1;
    }
}
