using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Document;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M8, FG 1254 (redaction, security; found in the sentence-by-sentence draft of the corrigenda of ISO 32000-2 bound with Annex Q, 14.13.5):
/// files associated with marked content in the corrected form — the property list a dictionary with /MCAF, an array of file specifications —
/// stayed in the redacted copy: the redaction knew only the older form, an array as the property list („/AF /MC0 BDC“, FG 444).
/// </summary>
public sealed class M8Fg1254Tests
{
    /// <summary>A page whose marked content „/AF … BDC“ associates a file „Anhangwort 4711“ through /MCAF — named in the resources or inline.</summary>
    private static PdfDocument Page(bool inline)
    {
        var pdf = TestPdf.Simple(1);
        var file = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("EmbeddedFile"))), "Anhangwort 4711"u8.ToArray()));
        var spec = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Filespec")), ("F", new PdfString("quelle.tex"u8.ToArray())),
            ("AFRelationship", TestPdf.N("Source")), ("EF", TestPdf.Dict(("F", TestPdf.Ref(file))))));
        var page = (PdfDictionary)pdf.Get(5);
        var resources = TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))));
        string property;
        if (inline) property = $"<< /MCAF [{spec} 0 R] >>";
        else
        {
            resources.Set(PdfName.Get("Properties"), TestPdf.Dict(("MF1", TestPdf.Dict(("MCAF", new PdfArray([TestPdf.Ref(spec)]))))));
            property = "/MF1";
        }
        page.Set(PdfName.Resources, resources);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes($"/AF {property} BDC BT /F1 20 Tf 100 600 Td (Formel) Tj ET EMC")));
        return PdfDocument.Open(pdf.Build());
    }

    /// <summary>14.13.5 as corrected (the files of marked content through /MCAF) with the principle of redaction that nothing describing the
    /// redacted content stays (FG 444, 624): under a bar the associated file leaves the copy, named and inline; the text drawn there goes too.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fg1254_MarkedContentFilesThroughMcaf_UnderABar_Leave(bool inline)
    {
        using var doc = Page(inline);
        Assert.Contains("Anhangwort", RedactionTests.Everything(doc));
        using var after = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(90, 590, 260, 630))));
        var everything = RedactionTests.Everything(after);
        Assert.DoesNotContain("Anhangwort", everything);
        Assert.DoesNotContain("Formel", everything);
    }
}
