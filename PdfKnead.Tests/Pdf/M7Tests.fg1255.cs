using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, FG 1255 (writer, data kept; found by the data-keep check of the guard m7-abschluss): a standard font with a /FontDescriptor but without
/// /Widths gets its widths when saved (FG 1151) — the range /FirstChar … /LastChar the source names was moved to the codes the display draws
/// (a file of a test collection, issue5470: /FirstChar 32 became another value). The range of the source stays; only its widths are added.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>ISO 32000-2, 9.6.2.1 (the four entries of a standard 14 font stand all or none): /FirstChar 32 and /LastChar 255 of the source
    /// stay, /Widths gets 224 entries — the width of the space at code 32 and of „A“ at code 65 as the display sets them.</summary>
    [Fact]
    public void Fg1255_StandardFontWithoutWidths_SavedWithItsOwnRange()
    {
        var pdf = new TestPdf();
        var cat = TestPdf.Dict(("Type", TestPdf.N("Catalog")));
        var catNumber = pdf.Add(cat);
        var pagesNumber = pdf.Add(new PdfDictionary());
        var descriptor = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("Arial")), ("Flags", PdfInteger.Of(32)),
            ("FontBBox", TestPdf.Box(-665, -325, 2000, 1006)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(728)),
            ("Descent", PdfInteger.Of(-210)), ("CapHeight", PdfInteger.Of(500)), ("StemV", PdfInteger.Of(0))));
        var font = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("Arial")),
            ("FirstChar", PdfInteger.Of(32)), ("LastChar", PdfInteger.Of(255)), ("Encoding", TestPdf.N("WinAnsiEncoding")),
            ("FontDescriptor", TestPdf.Ref(descriptor)));
        var content = pdf.Add(TestPdf.FlateStream("BT /F1 12 Tf 20 100 Td (A B) Tj ET"u8.ToArray()));
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pagesNumber)), ("MediaBox", TestPdf.Box(0, 0, 200, 200)),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", font))))), ("Contents", TestPdf.Ref(content))));
        pdf.Replace(pagesNumber, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray([TestPdf.Ref(page)])), ("Count", PdfInteger.Of(1))));
        cat.Set(PdfName.Pages, TestPdf.Ref(pagesNumber));
        pdf.RootNumber = catNumber;
        using var doc = PdfDocument.Open(pdf.Build());

        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = "1.7" });
        using var copy = PdfDocument.Open(output.ToArray());
        var saved = copy.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!;
        Assert.Equal(32, saved.GetInteger(PdfName.Get("FirstChar")));
        Assert.Equal(255, saved.GetInteger(PdfName.Get("LastChar")));
        var widths = Assert.IsType<PdfArray>(saved.Get(PdfName.Get("Widths")));
        Assert.Equal(224, widths.Count);
        double Width(int code) => widths.Get(code - 32) switch { PdfInteger i => i.Value, PdfReal r => r.Value, _ => double.NaN };
        Assert.Equal(278, Width(32), 0);   // the space of the standard metrics (Helvetica, as Arial is read)
        Assert.Equal(667, Width(65), 0);   // „A“
    }
}
