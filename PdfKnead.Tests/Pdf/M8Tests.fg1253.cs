using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M8, FG 1253 (reader and writer; found in the sentence-by-sentence draft of ISO 32000-2, 11.3.5.2): /Compatible was not among the known
/// blend modes — in an array of blend modes it was skipped and the next name counted (`[/Compatible /Multiply]` multiplied instead of mixing
/// normally), and saving as PDF 2.0 wrote that wrong name; a single /BM /Compatible stayed in a 2.0 file, which must name /Normal instead.
/// </summary>
public sealed class M8Fg1253Tests
{
    /// <summary>11.3.5.2 sentence 10 (reader): /Compatible is Normal — as a name, and in an array it is the first known name.</summary>
    [Fact]
    public void Fg1253_Compatible_IsNormal_AlsoFirstInAnArray()
    {
        Assert.Equal(0, BlendModes.From(PdfName.Get("Compatible")));
        Assert.Equal(0, BlendModes.From(new PdfArray([PdfName.Get("Compatible"), PdfName.Get("Multiply")])));
        Assert.Equal(1, BlendModes.From(new PdfArray([PdfName.Get("Erfunden"), PdfName.Get("Multiply")])));   // an unknown name is skipped
    }

    /// <summary>11.3.5.2 sentence 11 (writer): saved as PDF 2.0, /BM /Compatible and an array starting with it are written as /Normal; saved as
    /// 1.7 they stay as they are.</summary>
    [Theory]
    [InlineData("/Compatible")]
    [InlineData("[/Compatible /Multiply]")]
    public void Fg1253_Compatible_SavedAs20_Normal(string blend)
    {
        var pdf = new TestPdf();
        var cat = TestPdf.Dict(("Type", TestPdf.N("Catalog")));
        var catNumber = pdf.Add(cat);
        var pagesNumber = pdf.Add(new PdfDictionary());
        var value = blend.StartsWith('[') ? (PdfObject)new PdfArray([PdfName.Get("Compatible"), PdfName.Get("Multiply")]) : PdfName.Get("Compatible");
        var state = TestPdf.Dict(("Type", TestPdf.N("ExtGState")), ("BM", value));
        var content = pdf.Add(TestPdf.FlateStream("/GS0 gs 1 0 0 rg 20 20 160 160 re f"u8.ToArray()));
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pagesNumber)), ("MediaBox", TestPdf.Box(0, 0, 200, 200)),
            ("Resources", TestPdf.Dict(("ExtGState", TestPdf.Dict(("GS0", state))))), ("Contents", TestPdf.Ref(content))));
        pdf.Replace(pagesNumber, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray([TestPdf.Ref(page)])), ("Count", PdfInteger.Of(1))));
        cat.Set(PdfName.Pages, TestPdf.Ref(pagesNumber));
        pdf.RootNumber = catNumber;
        using var doc = PdfDocument.Open(pdf.Build());

        PdfObject? Saved(string version)
        {
            using var output = new MemoryStream();
            PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = version });
            using var copy = PdfDocument.Open(output.ToArray());
            return copy.Pages[0].Resources?.GetDictionary(PdfName.Get("ExtGState"))?.GetDictionary(PdfName.Get("GS0"))?.Get(PdfName.Get("BM"));
        }

        Assert.Equal("Normal", Assert.IsType<PdfName>(Saved("2.0")).Value);
        var kept = Saved("1.7");
        if (blend.StartsWith('[')) Assert.Equal("Compatible", Assert.IsType<PdfName>(Assert.IsType<PdfArray>(kept).Get(0)).Value);
        else Assert.Equal("Compatible", Assert.IsType<PdfName>(kept).Value);
    }
}
