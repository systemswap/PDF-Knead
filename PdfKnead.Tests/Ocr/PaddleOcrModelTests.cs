using PdfKnead.Ai;
using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Rendering;
using PdfKnead.Tests.Pdf;
using PdfKnead.Core.Pdf.Objects;
using Xunit;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Ocr;

/// <summary>
/// The text recognition with the real models (docs/PRUEFUNGEN.md, section 6o): only when PDFKNEAD_OCR_MODELLE names a folder with the
/// model folders as published — the tests never load anything.
/// </summary>
public class PaddleOcrModelTests
{
    private static string? Models => Environment.GetEnvironmentVariable("PDFKNEAD_OCR_MODELLE") is { Length: > 0 } m && Directory.Exists(m) ? m : null;

    private static PaddleOcr Open(string models, OcrModelSize size) =>
        new(Path.Combine(models, $"PP-OCRv6_{OcrModels.Key(size)}_det_onnx"), Path.Combine(models, $"PP-OCRv6_{OcrModels.Key(size)}_rec_onnx"));

    /// <summary>An A4 page with Helvetica in WinAnsi (umlauts as single bytes).</summary>
    private static byte[] Page(string content)
    {
        var pdf = new TestPdf();
        var font = pdf.Add(Dict(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N("Helvetica")), ("Encoding", N("WinAnsiEncoding"))));
        var pages = pdf.Add(new PdfDictionary());
        var stream = pdf.Add(new PdfStream(new PdfDictionary(), System.Text.Encoding.Latin1.GetBytes(content)));
        var page = pdf.Add(Dict(("Type", N("Page")), ("Parent", Ref(pages)), ("Contents", Ref(stream)), ("MediaBox", Box(0, 0, 595.28, 841.89)),
            ("Resources", Dict(("Font", Dict(("F1", Ref(font))))))));
        pdf.Replace(pages, Dict(("Type", N("Pages")), ("Kids", new PdfArray([Ref(page)])), ("Count", PdfInteger.Of(1))));
        pdf.RootNumber = pdf.Add(Dict(("Type", N("Catalog")), ("Pages", Ref(pages))));
        return pdf.Build();
    }

    [Theory]
    [InlineData(OcrModelSize.Small)]
    [InlineData(OcrModelSize.Medium)]
    public void PrintedGermanText_ReadWordForWord(OcrModelSize size)
    {
        if (Models is not { } models) return;
        using var doc = PdfDocument.Open(Page(
            "BT /F1 14 Tf 72 760 Td (Rechnung Nummer 4711 vom 30. Oktober) Tj 0 -30 Td (Gr\\374\\337e aus K\\366ln, Stra\\337e 12) Tj ET " +
            "q 0.866 0.5 -0.5 0.866 300 400 cm BT /F1 18 Tf 0 0 Td (Gedreht um drei\\337ig Grad) Tj ET Q"));
        var item = PageItem.From(doc)[0];
        var (image, _) = PageRecognition.Render(item, PageInterpreter.Build(doc, doc.Pages[0]), new ImageCache());
        using var ocr = Open(models, size);
        var lines = ocr.Recognize(image);
        var text = string.Join("\n", lines.Select(l => l.Text));
        Assert.Contains("Rechnung Nummer 4711 vom 30. Oktober", text);
        Assert.Contains("Grüße aus Köln, Straße 12", text);
        Assert.Contains("Gedreht um dreißig Grad", text);
        Assert.All(lines, l => Assert.InRange(l.Confidence, 0.8, 1));
    }

    [Fact]
    public void Cancelled_StopsWithoutResult()
    {
        if (Models is not { } models) return;
        using var ocr = Open(models, OcrModelSize.Small);
        var image = new OcrImage(1000, 1000, Enumerable.Repeat((byte)255, 4_000_000).ToArray());
        Assert.ThrowsAny<OperationCanceledException>(() => ocr.Recognize(image, new CancellationToken(true)));
    }

    [Fact]
    public void BlankAndTinyImages_NoLines()
    {
        if (Models is not { } models) return;
        using var ocr = Open(models, OcrModelSize.Small);
        Assert.Empty(ocr.Recognize(new OcrImage(800, 600, Enumerable.Repeat((byte)255, 800 * 600 * 4).ToArray())));
        Assert.Empty(ocr.Recognize(new OcrImage(1, 1, [0, 0, 0, 255])));
        Assert.Empty(ocr.Recognize(new OcrImage(10, 10, new byte[3])));   // too few samples for its size
    }
}
