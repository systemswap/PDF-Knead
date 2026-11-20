using System.Globalization;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Creates blank pages for insertion: a tiny in-memory document whose pages have no content, so that the
/// viewer and the writer treat them like any other inserted page.
/// </summary>
public static class BlankPage
{
    /// <summary>Maximum number of blank pages created at once (File → New, inserting blank pages).</summary>
    public const int MaxCount = 999;

    /// <summary>Creates a single blank page.</summary>
    /// <param name="width">Page width in points (1/72 inch), e.g. 595.28 for A4; clamped to 3 … 14 400.</param>
    /// <param name="height">Page height in points, e.g. 841.89 for A4; clamped to 3 … 14 400.</param>
    /// <returns>The page, backed by its own in-memory PDF 1.4 document.</returns>
    public static PageItem Create(double width, double height)
    {
        var doc = CreateDocument(width, height, 1, "1.4");
        return new PageItem(doc, doc.Pages[0]);
    }

    /// <summary>
    /// Creates an in-memory document with <paramref name="count"/> blank pages of the same size (File → New, inserting
    /// blank pages).
    /// </summary>
    /// <param name="width">Page width in points (1/72 inch); clamped to 3 … 14 400.</param>
    /// <param name="height">Page height in points; clamped to 3 … 14 400.</param>
    /// <param name="count">Number of pages; clamped to 1 … <see cref="MaxCount"/>.</param>
    /// <param name="version">PDF version written into the file header, e.g. "1.7".</param>
    /// <param name="title">The title of the document (/Info /Title); null: none.</param>
    /// <param name="language">The language of the document (/Lang of the catalog, a language tag); null or not valid: none
    /// (docs/FEHLERGEDAECHTNIS.md, No. 492).</param>
    /// <returns>The opened document.</returns>
    public static PdfDocument CreateDocument(double width, double height, int count, string version = "1.7", string? title = null, string? language = null)
    {
        count = Math.Clamp(count, 1, MaxCount);
        var w = PdfSerializer.FormatReal(Math.Clamp(width, 3, 14400));
        var h = PdfSerializer.FormatReal(Math.Clamp(height, 3, 14400));
        var kids = new StringBuilder();
        for (var i = 0; i < count; i++) kids.Append(CultureInfo.InvariantCulture, $"{(i == 0 ? "" : " ")}{i + 3} 0 R");
        var objects = new List<string>(count + 2)
        {
            PdfATagging.CleanLanguage(language, 2) is { } lang ? $"<</Type/Catalog/Pages 2 0 R/Lang {Literal(lang)}>>" : "<</Type/Catalog/Pages 2 0 R>>",
            $"<</Type/Pages/Kids[{kids}]/Count {count}>>",
        };
        for (var i = 0; i < count; i++) objects.Add($"<</Type/Page/Parent 2 0 R/MediaBox[0 0 {w} {h}]/Resources<<>>>>");
        if (title is { Length: > 0 }) objects.Add($"<</Title {Literal(title)}>>");
        var text = new StringBuilder($"%PDF-{version}\n");
        var offsets = new List<int>(objects.Count);
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(text.Length);
            text.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = text.Length;
        text.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) text.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        var info = title is { Length: > 0 } ? $"/Info {objects.Count} 0 R" : "";
        text.Append(CultureInfo.InvariantCulture, $"trailer\n<</Size {objects.Count + 1}/Root 1 0 R{info}>>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(text.ToString()));

        // a text string as written in a file (UTF-16 or PDFDocEncoding, escaped), one character per byte
        static string Literal(string value) => Encoding.Latin1.GetString(PdfSerializer.ToBytes(new Objects.PdfString(Objects.PdfTextEncoding.Encode(value))));
    }
}
