using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The reading order of a page as the structure gives it (docs/ABLAUFPLAN.md, 6.7 step 5, helps M1, M2, M6; docs/FEHLERGEDAECHTNIS.md, No. 670):
/// numbered in the order of the tree, not of the content stream; figures with their alternative text; artifacts as areas of their own.
/// </summary>
public sealed class ReadingOrderTests
{
    /// <summary>
    /// Content: „Oben“ (MCID 0) at y 700, „Unten“ (MCID 1) at y 600, a figure (MCID 2), a footer line as an artifact. The tree reads „Unten“
    /// first — a wrong order a person has to see.
    /// </summary>
    internal static byte[] Bytes() => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R/MarkInfo<</Marked true>>/StructTreeRoot 7 0 R>>",
        [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
        [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<</Font<</F1 3 0 R>>>>/StructParents 0>>",
        [6] = ("", "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Oben) Tj ET EMC " +
                   "/P <</MCID 1>> BDC BT /F1 12 Tf 72 600 Td (Unten) Tj ET EMC " +
                   "/Figure <</MCID 2>> BDC 300 300 100 100 re f EMC " +
                   "/Artifact BMC 72 40 m 520 40 l S EMC"),
        [7] = "<</Type/StructTreeRoot/K 8 0 R/ParentTree<</Nums[0[10 0 R 9 0 R 11 0 R]]>>>>",
        [8] = "<</Type/StructElem/S/Document/P 7 0 R/K[9 0 R 10 0 R 11 0 R]>>",
        [9] = "<</Type/StructElem/S/P/P 8 0 R/Pg 4 0 R/K 1>>",
        [10] = "<</Type/StructElem/S/P/P 8 0 R/Pg 4 0 R/K 0>>",
        [11] = "<</Type/StructElem/S/Figure/P 8 0 R/Pg 4 0 R/K 2/Alt(Ein Quadrat)>>",
    });

    private static PdfDocument File() => PdfDocument.Open(Bytes());

    [Fact]
    public void Of_TreeOrderNumbered_FigureWithAlt_ArtifactsApart()
    {
        using var doc = File();
        var result = ReadingOrder.Of(doc, doc.Pages[0]);
        Assert.Equal(3, result.Order.Count);
        Assert.Equal([1, 2, 3], result.Order.Select(b => b.Number));
        Assert.Equal(["P", "P", "Figure"], result.Order.Select(b => b.Type));
        // number 1 is „Unten“ (y about 600), number 2 „Oben“ (about 700): the order of the tree
        Assert.InRange(result.Order[0].Bounds.Top, 590, 615);
        Assert.InRange(result.Order[1].Bounds.Top, 690, 715);
        Assert.Equal("Ein Quadrat", result.Order[2].Alt);
        Assert.Null(result.Order[0].Alt);
        Assert.InRange(result.Order[2].Bounds.Left, 299, 301);
        // the footer line: one artifact area, not part of the order
        var artifact = Assert.Single(result.Artifacts);
        Assert.InRange(artifact.Top, 35, 45);
    }

    [Fact]
    public void Of_UntaggedPage_OnlyArtifactsAndNoOrder()
    {
        using var doc = PdfDocument.Open(CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<<>>>>",
            [6] = ("", "0 0 m 100 100 l S"),
        }));
        var result = ReadingOrder.Of(doc, doc.Pages[0]);
        Assert.Empty(result.Order);
        Assert.Empty(result.Artifacts);
    }
}
