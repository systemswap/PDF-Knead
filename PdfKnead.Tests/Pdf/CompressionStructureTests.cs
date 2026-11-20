using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The search for duplicates when compressing and the structure (docs/ABLAUFPLAN.md, 6.2 step 9; docs/FEHLERGEDAECHTNIS.md, No. 630): objects
/// that carry tagged content are never merged — a form with MCIDs drawn from two pages as one object breaks ISO 14289-1, 7.20 (the form is
/// referenced more than once) and hands the content of one page to the elements of the other.
/// </summary>
public sealed class CompressionStructureTests
{
    /// <summary>
    /// Two pages (3, 11), each drawing a form of its own (5, 13) with the same content — a paragraph marked MCID 0 — and with the same
    /// <paramref name="formExtra"/>; the parent tree gives MCID 0 of each page to its own paragraph (8, 9).
    /// </summary>
    private static byte[] TwoPagesWithEqualForms(string formExtra = "")
    {
        const string content = "/P <</MCID 0>> BDC 0 0 m 100 100 l S EMC";
        return CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 6 0 R/MarkInfo<</Marked true>>/Lang(de)>>",
            [2] = "<</Type/Pages/Kids[3 0 R 11 0 R]/Count 2/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</XObject<</X1 5 0 R>>>>/StructParents 0>>",
            [4] = ("", "/X1 Do"),
            [5] = ($"/Type/XObject/Subtype/Form/BBox[0 0 200 200]{formExtra}", content),
            [6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 2>>",
            [7] = "<</Type/StructElem/S/Document/P 6 0 R/K[8 0 R 9 0 R]>>",
            [8] = "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K 0>>",
            [9] = "<</Type/StructElem/S/P/P 7 0 R/Pg 11 0 R/K 0>>",
            [11] = "<</Type/Page/Parent 2 0 R/Contents 12 0 R/Resources<</XObject<</X1 13 0 R>>>>/StructParents 1>>",
            [12] = ("", "/X1 Do"),
            [13] = ($"/Type/XObject/Subtype/Form/BBox[0 0 200 200]{formExtra}", content),
            [15] = "<</Nums[0[8 0 R] 1[9 0 R]]>>",
        });
    }

    private static PdfDocument Compressed(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Compression = CompressionSettings.For(CompressionPreset.Lossless) });
        return PdfDocument.Open(output.ToArray());
    }

    private static PdfObjectId FormOf(PdfDocument doc, int page) =>
        ((PdfReference)doc.Pages[page].Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("XObject"))!.GetRaw(PdfName.Get("X1"))!).Id;

    /// <summary>
    /// The two forms with MCIDs stay two objects; no form is drawn twice and each page's paragraph keeps its content (before: one form for
    /// both pages, reported under 7.20-2).
    /// </summary>
    [Fact]
    public void FormsWithMcids_NotMerged()
    {
        using var saved = Compressed(TwoPagesWithEqualForms());
        Assert.NotEqual(FormOf(saved, 0), FormOf(saved, 1));
        Assert.Equal(0, TaggedContent.Analyse(saved).FormsWithMcidsDrawnTwice);
        StructureAssert.Clean(saved);
    }

    /// <summary>Forms that are content items of their own (/StructParent) are not merged either, even with the same key; forms without marked content still are.</summary>
    [Fact]
    public void FormsWithStructParent_NotMerged_PlainFormsStillAre()
    {
        using (var saved = Compressed(TwoPagesWithEqualForms("/StructParent 5")))
            Assert.NotEqual(FormOf(saved, 0), FormOf(saved, 1));
        var plain = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R 11 0 R]/Count 2/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</XObject<</X1 5 0 R>>>>>>",
            [4] = ("", "/X1 Do"),
            [5] = ("/Type/XObject/Subtype/Form/BBox[0 0 200 200]", "0 0 m 100 100 l S"),
            [11] = "<</Type/Page/Parent 2 0 R/Contents 12 0 R/Resources<</XObject<</X1 13 0 R>>>>>>",
            [12] = ("", "/X1 Do"),
            [13] = ("/Type/XObject/Subtype/Form/BBox[0 0 200 200]", "0 0 m 100 100 l S"),
        });
        using var merged = Compressed(plain);
        Assert.Equal(FormOf(merged, 0), FormOf(merged, 1));
    }
}
