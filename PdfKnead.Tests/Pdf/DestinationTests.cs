using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Destinations with their view (ISO 32000-2, 12.3.2.2) and structure destinations (/SD, 12.3.2.3 and 12.6.4.2): bookmarks lead to the
/// place and zoom, not only to the page (docs/ABLAUFPLAN.md, 7.3; docs/FEHLERGEDAECHTNIS.md, No. 563).
/// </summary>
public sealed class DestinationTests
{
    /// <summary>Three pages (objects 3, 4, 5), the outline item (object 7) with <paramref name="item"/>, more objects from 10.</summary>
    private static PdfDocument Outline(string item, params (int Number, string Value)[] more)
    {
        var objects = new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/Outlines 6 0 R/StructTreeRoot 9 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R 4 0 R 5 0 R]/Count 3/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R>>",
            [4] = "<</Type/Page/Parent 2 0 R>>",
            [5] = "<</Type/Page/Parent 2 0 R>>",
            [6] = "<</Type/Outlines/First 7 0 R/Last 7 0 R/Count 1>>",
            [7] = "<</Title(Kapitel)/Parent 6 0 R" + item + ">>",
            [9] = "<</Type/StructTreeRoot>>",
        };
        foreach (var (number, value) in more) objects[number] = value;
        return PdfDocument.Open(CraftedFileTests.Build(objects));
    }

    private static OutlineItem Item(PdfDocument doc) => Assert.Single(PdfOutline.Read(doc));

    /// <summary>[page /XYZ left top null]: place kept, zoom null (keeps the current one); a zoom of 0 counts as null. Before: only the page.</summary>
    [Fact]
    public void XyzDestination_KeepsTheZoomWhenNull()
    {
        using (var doc = Outline("/Dest[4 0 R/XYZ 100 700 null]"))
            Assert.Equal(new PdfDestination(1, DestinationKind.Xyz, 100, 700, null), Item(doc).Target);
        using (var zero = Outline("/Dest[4 0 R/XYZ null 500 0]"))
            Assert.Equal(new PdfDestination(1, DestinationKind.Xyz, null, 500, null), Item(zero).Target);
        using var zoom = Outline("/Dest[5 0 R/XYZ 0 842 2]");
        Assert.Equal(2, Item(zoom).Target!.Zoom);
    }

    [Fact]
    public void OtherKinds_WithTheirValues()
    {
        using (var doc = Outline("/Dest[3 0 R/FitH 400]")) Assert.Equal(new PdfDestination(0, DestinationKind.FitH, Top: 400), Item(doc).Target);
        using (var doc = Outline("/Dest[3 0 R/FitR 300 400 100 200]"))
            Assert.Equal(new PdfRectangle(100, 200, 300, 400), Item(doc).Target!.Rect);
        using (var doc = Outline("/A<</S/GoTo/D[5 0 R/Fit]>>")) Assert.Equal(new PdfDestination(2, DestinationKind.Fit), Item(doc).Target);
    }

    /// <summary>
    /// A structure destination: the page of the first piece of content of the element (here a marked-content id below a kid on page 3),
    /// before /D (page 1). Before: page 1 from /D.
    /// </summary>
    [Fact]
    public void StructDestination_PageFromFirstContentItem()
    {
        using var doc = Outline("/A<</S/GoTo/SD[10 0 R/XYZ 50 600 null]/D[3 0 R/Fit]>>",
            (10, "<</Type/StructElem/S/H1/P 9 0 R/K[11 0 R]>>"),
            (11, "<</Type/StructElem/S/Span/P 10 0 R/Pg 5 0 R/K 0>>"));
        var target = Item(doc).Target!;
        Assert.Equal(2, target.PageIndex);
        Assert.Equal((50.0, 600.0), (target.Left!.Value, target.Top!.Value));
        Assert.Equal(2, Item(doc).PageIndex);
    }

    /// <summary>A marked-content reference with its own /Pg, and an element without any content: the first page.</summary>
    [Fact]
    public void StructDestination_McrAndWithoutContent()
    {
        using (var mcr = Outline("/A<</S/GoTo/SD[10 0 R/Fit]>>", (10, "<</Type/StructElem/S/P/P 9 0 R/K<</Type/MCR/Pg 4 0 R/MCID 3>>>>")))
            Assert.Equal(1, Item(mcr).Target!.PageIndex);
        using var empty = Outline("/A<</S/GoTo/SD[10 0 R/Fit]/D[5 0 R/Fit]>>", (10, "<</Type/StructElem/S/P/P 9 0 R>>"));
        Assert.Equal(0, Item(empty).Target!.PageIndex);
    }

    /// <summary>/SD that is no structure element (broken): /D counts.</summary>
    [Fact]
    public void StructDestination_Broken_FallsBackToD()
    {
        using var doc = Outline("/A<</S/GoTo/SD[4 0 R/Fit]/D[5 0 R/Fit]>>");
        Assert.Equal(2, Item(doc).Target!.PageIndex);
    }

    /// <summary>A cycle in the structure (an element that is its own kid) ends without hanging.</summary>
    [Fact]
    public void StructDestination_Cyclic_NoHang()
    {
        using var doc = Outline("/A<</S/GoTo/SD[10 0 R/Fit]>>", (10, "<</Type/StructElem/S/P/P 9 0 R/K[10 0 R]>>"));
        Assert.Equal(0, Item(doc).Target!.PageIndex);
    }
}
