using System.Globalization;
using System.Text;
using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Barcodes.DataMatrix;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Cli;

/// <summary>
/// „codemuster“: every kind of code PDF Knead makes, as PDF pages for a check with an independent reader (phase 8.3): one file per kind and
/// content, the code at its default size in four turns (0°, 90°, 180°, 270°), one page each, flattened into the page content as „Code
/// ändern …“ and saving leave it. A list beside them names the content each must read back as (the encoded text: GS1 with the group
/// separator, EAN/UPC with check digit).
/// </summary>
internal static class CodeSamples
{
    /// <summary>The samples: kind, content as typed, GS1, a short name for the file.</summary>
    private static readonly (BarcodeFormat Format, string Text, bool Gs1, string Name)[] Samples =
    [
        (BarcodeFormat.Qr, "https://example.org/pdf-knead", false, "qr-adresse"),
        (BarcodeFormat.Qr, "Größe, Übung: Max Mustermann", false, "qr-umlaute"),
        (BarcodeFormat.DataMatrix, "(01)04012345678901(17)261231", true, "datamatrix-gs1"),
        (BarcodeFormat.DataMatrix, "Max Mustermann 12345", false, "datamatrix-text"),
        (BarcodeFormat.Ean13, "4006381333931", false, "ean13"),
        (BarcodeFormat.Ean13, "400638133393", false, "ean13-ohne-pruefziffer"),
        (BarcodeFormat.Ean8, "52364228", false, "ean8"),
        (BarcodeFormat.UpcA, "036000291452", false, "upca"),
        (BarcodeFormat.UpcE, "01234565", false, "upce"),
        (BarcodeFormat.Code128, "RE-2026-0815", false, "code128"),
        (BarcodeFormat.Code128, "Abc123456789012", false, "code128-satz-c"),
        (BarcodeFormat.Gs1_128, "(01)04012345678901(10)A1", false, "gs1-128"),
        (BarcodeFormat.Code39, "PDF-KNEAD 42", false, "code39"),
        (BarcodeFormat.Itf, "12345678901231", false, "itf"),
    ];

    /// <summary>Writes the samples and the list „erwartet.tsv“ (file, kind, content with control characters as \xHH).</summary>
    /// <param name="folder">Output folder (created if missing).</param>
    /// <returns>Exit code.</returns>
    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var list = new StringBuilder("datei\tart\tinhalt\n");
        foreach (var (format, text, gs1, name) in Samples)
        {
            var symbol = CodeSymbol.Create(format, text, gs1, shape: DataMatrixShape.Square);
            var probe = new CodeElement { Format = format, Text = text, Gs1 = gs1, Rect = default };
            var (w, h) = ElementAppearance.DefaultCodeSize(probe);
            // four turns, each on a page of its own (a reader may stop at the first code of a page), in the middle of the page
            int[] turns = [0, 90, 180, 270];
            using var blank = BlankPage.CreateDocument(595, 842, 4);
            var pages = PageItem.From(blank).ToList();
            for (var i = 0; i < 4; i++)
            {
                var (cw, ch) = turns[i] is 90 or 270 ? (h, w) : (w, h);
                var scale = Math.Min(1, Math.Min(480 / cw, 700 / ch));
                (cw, ch) = (cw * scale, ch * scale);
                double x = (595 - cw) / 2, y = (842 - ch) / 2;
                pages[i] = pages[i].WithElements([probe with { Rect = new PdfRectangle(x, y, x + cw, y + ch), Rotation = turns[i] }]);
            }
            var file = name + ".pdf";
            using (var output = File.Create(Path.Combine(folder, file)))
                PdfWriter.Write(blank, pages, output, new PdfSaveOptions { Flatten = true });
            list.Append(file).Append('\t').Append(format).Append('\t').Append(Escape(symbol.Encoded)).Append('\n');
            Console.WriteLine($"{file}\t{format}\t{Escape(symbol.Encoded)}");
        }
        File.WriteAllText(Path.Combine(folder, "erwartet.tsv"), list.ToString(), new UTF8Encoding(false));
        return 0;
    }

    /// <summary>Control characters (the GS1 group separator) as \xHH, so that the list stays one line per code.</summary>
    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
            if (c < 0x20 || c == 0x7F) sb.Append("\\x").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            else sb.Append(c);
        return sb.ToString();
    }
}
