using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Annotations, forms and actions in a PDF/A copy (docs/PDFA-REGELN.md, 6.3–6.5): what a part allows stays — the rule cases
/// (<see cref="PdfARuleCases"/>) show only that what it forbids goes.
/// </summary>
public sealed class PdfAAnnotationTests
{
    private static PdfDocument Written(byte[] original, PdfAConformance part)
    {
        using var doc = PdfDocument.Open(original);
        var copy = PdfAExport.Write(doc, PageItem.From(doc), part);
        Assert.True(copy.Conforms, PdfATests.Describe(copy.Violations));
        return PdfDocument.Open(copy.Bytes);
    }

    private static PdfDictionary Annotation(PdfDocument doc) => (PdfDictionary)doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0);

    [Theory]
    [InlineData(PdfAConformance.A2b, false)]
    [InlineData(PdfAConformance.A4, true)]
    public void ScriptsAndFieldTriggers_StayWhereThePartAllowsThem(PdfAConformance part, bool kept)
    {
        // part 4 allows scripts (6.6.1) and every trigger of a widget (6.6.3); parts 2 and 3 neither
        var file = PdfARuleCases.WithField("/AA<</K 8 0 R>>", more: PdfARuleCases.Script())();
        using var doc = Written(file, part);
        Assert.Equal(kept, Annotation(doc).GetDictionary(PdfName.Get("AA"))?.ContainsKey(PdfName.Get("K")) == true);
    }

    [Fact]
    public void TriggersOfAnotherAnnotation_StayInPart2_WithAnAllowedAction()
    {
        // parts 2 and 3 forbid /AA on the catalogue, pages, widgets and fields — not on other annotations; a script among them goes
        var file = PdfARuleCases.WithSquare("/F 4/AA<</U 8 0 R/D 9 0 R>>", more: [Obj("<</S/URI/URI(http://beispiel.invalid)>>", 8), PdfARuleCases.Script(9)])();
        using var doc = Written(file, PdfAConformance.A2b);
        var triggers = Annotation(doc).GetDictionary(PdfName.Get("AA"))!;
        Assert.True(triggers.ContainsKey(PdfName.Get("U")));
        Assert.False(triggers.ContainsKey(PdfName.Get("D")));
    }

    [Fact]
    public void AttachmentAnnotation_StaysInPart2_WithoutItsFile()
    {
        // the annotation type is allowed (6.3.1), the embedded file is not known to be PDF/A (6.8): the icon stays, the file goes
        var file = PdfARuleCases.WithAnnotation("<</Type/Annot/Subtype/FileAttachment/Rect[100 600 200 650]/F 4/AP<</N 6 0 R>>/FS 7 0 R>>", more:
            [PdfARuleCases.RedLook(), Obj("<</Type/Filespec/F(a.txt)/UF(a.txt)/EF<</F 9 0 R>>>>", 7), PdfARuleCases.Stream(9, "/Type/EmbeddedFile", "Inhalt")])();
        using var doc = Written(file, PdfAConformance.A2b);
        var annot = Annotation(doc);
        Assert.Equal("FileAttachment", annot.GetName(PdfName.Subtype)?.Value);
        Assert.False(annot.GetDictionary(PdfName.Get("FS"))!.ContainsKey(PdfName.Get("EF")));
    }

    /// <summary>
    /// The appearances a PDF/A copy draws for annotations without one carry a resource dictionary, even an empty one: ISO 32000-2, 8.10.1
    /// (Table 93) requires it, and ISO 19005-2, 6.2.2 asks for explicitly associated resources (docs/FEHLERGEDAECHTNIS.md, No. 366).
    /// </summary>
    [Theory]
    [InlineData("<</Type/Annot/Subtype/Widget/FT/Tx/T(Feld)/F 4/Rect[100 600 200 650]/P 3 0 R>>")]
    [InlineData("<</Type/Annot/Subtype/Widget/FT/Btn/T(Feld)/F 4/Rect[100 600 120 620]/P 3 0 R/AS/Off>>")]
    [InlineData("<</Type/Annot/Subtype/Square/F 4/Rect[100 600 200 650]/C[1 0 0]>>")]
    public void AppearancesDrawnForTheCopy_HaveResources(string annotation)
    {
        var form = annotation.Contains("/Widget") ? "/AcroForm<</Fields[5 0 R]>>" : "";
        using var doc = Written(PdfARuleCases.WithAnnotation(annotation, form, "")(), PdfAConformance.A2b);
        var normal = Annotation(doc).GetDictionary(PdfName.Get("AP"))!.Get(PdfName.Get("N"));
        var looks = normal is PdfStream one ? [one] : ((PdfDictionary)normal).Entries.Select(e => ((PdfDictionary)normal).Get(e.Key)).OfType<PdfStream>().ToList();
        Assert.NotEmpty(looks);
        Assert.All(looks, look => Assert.IsType<PdfDictionary>(look.Dictionary.Get(PdfName.Get("Resources"))));
    }

    /// <summary>
    /// Free text whose appearance holds a standard font directly in its font map: the PDF/A-4 copy embeds it as shown — describing it for PDF
    /// 2.0 beforehand hid it from the embedding, and the copy failed 6.2.10.4.1 (docs/FEHLERGEDAECHTNIS.md, No. 372).
    /// </summary>
    [Fact]
    public void FreeTextWithAStandardFontInItsAppearance_ConformsAsPdfA4()
    {
        var file = PdfARuleCases.WithAnnotation("<</Type/Annot/Subtype/FreeText/F 4/Rect[100 600 300 650]/Contents(Hinweis)/DA(/Helv 12 Tf 0 g)/AP<</N 6 0 R>>>>", "", "",
            PdfARuleCases.Stream(6, "/Type/XObject/Subtype/Form/BBox[0 0 200 50]/Resources<</Font<</Helv<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>>>>>",
                "BT /Helv 12 Tf 2 20 Td (Hinweis) Tj ET"))();
        using var doc = Written(file, PdfAConformance.A4);
        Assert.NotNull(Annotation(doc).GetDictionary(PdfName.Get("AP")));
    }

    private static PdfARuleCases.RawObject Obj(string body, int number) => PdfARuleCases.Obj(number, body);

    /// <summary>
    /// A file attachment of width 0 and height 550 without an appearance (ISO 19005-2, 6.3.3: an area in one direction is enough): the copy
    /// gets one — before it had none, and the check passed it (docs/FEHLERGEDAECHTNIS.md, No. 499).
    /// </summary>
    [Fact]
    public void ZeroWidthAttachment_CopyGetsAnAppearance()
    {
        using var written = Written(PdfARuleCases.WithAnnotation("<</Type/Annot/Subtype/FileAttachment/Rect[50 600 50 50]/F 4/FS<</Type/Filespec/F(a.txt)>>>>")(),
            PdfAConformance.A2b);
        Assert.NotNull(Annotation(written).GetDictionary(PdfName.Get("AP"))?.Get(PdfName.Get("N")) as PdfStream);
    }

    /// <summary>Part 4 does not ask an annotation whose rectangle is a point for its flags (ISO 19005-4, 6.3.2; before: rejected).</summary>
    [Fact]
    public void PointWithoutFlags_Part4_Accepted()
    {
        using var doc = PdfDocument.Open(PdfARuleCases.WithAnnotation("<</Type/Annot/Subtype/Text/Rect[0 0 0 0]/Contents(Hinweis)>>")());
        Assert.DoesNotContain(PdfACheck.Check(doc, PdfAConformance.A4), v => v.Id == "6.3.2-1");
        Assert.Contains(PdfACheck.Check(doc, PdfAConformance.A2b), v => v.Id == "6.3.2-1");
    }
}
