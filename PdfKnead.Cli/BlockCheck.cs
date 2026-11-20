using System.Diagnostics;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Cli;

/// <summary>
/// <c>pdf-knead bloecke &lt;file&gt; [&lt;page&gt;] [--ohne-text]</c>: text blocks per page as "edit text" detects them (see KONZEPT.md,
/// section 5.14) — position, alignment, line spacing, runs with font and formatting, hard breaks, lock reason. <c>--ohne-text</c>
/// masks letters and digits so that private files can be inspected for structure only.
/// </summary>
internal static class BlockCheck
{
    public static int Run(PdfDocument doc, int onlyPage, bool hideText)
    {
        for (var i = 0; i < doc.PageCount; i++)
        {
            if (onlyPage != 0 && i + 1 != onlyPage) continue;
            var watch = Stopwatch.StartNew();
            var blocks = TextBlocks.Find(doc, doc.Pages[i]);
            Console.WriteLine(Tr.T("--- Seite {0}: {1} Blöcke ({2:0} ms) ---", i + 1, blocks.Count, watch.Elapsed.TotalMilliseconds));
            var n = 0;
            foreach (var block in blocks)
            {
                var b = block.Bounds;
                Console.WriteLine(I(
                    $"#{++n} [{b.Left:0.#} {b.Top:0.#} {b.Right:0.#} {b.Bottom:0.#}] {block.Angle:0}° {block.Alignment} Abstand {block.LineSpacing:0.##} Zeilen {block.Lines.Count} Zeichen {block.Glyphs.Count}") +
                    (block.Paths.Count > 0 ? $" Linien {block.Paths.Count}" : "") + (block.Locked is { } locked ? $" GESPERRT: {locked}" : ""));
                foreach (var line in block.Lines)
                {
                    var runs = string.Join(" | ", line.Runs.Select(r => $"{Describe(r.Format)} «{Show(r.Text, hideText)}»"));
                    Console.WriteLine($"    {(line.HardBreak ? "¶" : "↩")} {runs}");
                }
            }
        }
        return 0;
    }

    private static string I(FormattableString s) => FormattableString.Invariant(s);

    private static string Describe(TextFormat f) =>
        I($"{f.Family} {f.Size:0.#}pt{(f.Bold ? f.FakeBold ? " fett*" : " fett" : "")}{(f.Italic ? f.FakeItalic ? " kursiv*" : " kursiv" : "")} #{f.Color:X6}") +
        (Math.Abs(f.Stretch - 1) > 0.02 ? I($" {f.Stretch * 100:0}%") : "") + (Math.Abs(f.Rise) > 0.05 ? I($" hoch {f.Rise:0.#}") : "") +
        (Math.Abs(f.CharSpacing) > 0.01 ? I($" Tc {f.CharSpacing:0.##}") : "") + (Math.Abs(f.WordSpacing) > 0.01 ? I($" Tw {f.WordSpacing:0.##}") : "") +
        (f.Underline ? " unterstrichen" : "") + (f.Strikeout ? " durchgestrichen" : "") + (f.Font is null ? " (ohne Schrift)" : "");

    private static string Show(string text, bool hide) =>
        !hide ? text : string.Concat(text.Select(c => char.IsLetter(c) ? (char.IsUpper(c) ? 'X' : 'x') : char.IsDigit(c) ? '9' : c));
}
