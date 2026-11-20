using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Destinations to pages taken out (docs/FEHLERGEDAECHTNIS.md, No. 692; found by the run of all ways to write, 6.8 step 8, with the second
/// checker of the file model): a destination names a page (ISO 32000-2, 12.3.2.2) — named destinations (/Names /Dests and the older /Dests of
/// the catalogue) whose page is gone go, links on pages kept lose a destination whose page is gone. Before, they were written as
/// <c>[null /Fit]</c>.
/// </summary>
public sealed class NamedDestinationTests
{
    private static PdfString S(string text) => new(Encoding.ASCII.GetBytes(text));

    /// <summary>
    /// Three pages; named destinations „eins“ (page 1) and „drei“ (page 3) in the name tree, „alt“ (page 1) in the older /Dests; on page 3
    /// a link to page 1, one to the name „eins“ and one to the name „drei“.
    /// </summary>
    private static PdfDocument ThreePages()
    {
        var pdf = TestPdf.Simple(3);
        int[] pages = [5, 7, 9];   // catalogue 1, page tree 2, font 3, then content and page in turn
        PdfArray Fit(int page) => new([TestPdf.Ref(page), TestPdf.N("Fit")]);
        var tree = TestPdf.Dict(("Names", new PdfArray([S("drei"), Fit(pages[2]), S("eins"), Fit(pages[0])])));
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(PdfName.Get("Names"), TestPdf.Dict(("Dests", TestPdf.Ref(pdf.Add(tree)))));
        catalog.Set(PdfName.Get("Dests"), TestPdf.Dict(("alt", Fit(pages[0]))));
        PdfDictionary Link(double y, (string, PdfObject) target) => TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Link")),
            ("Rect", TestPdf.Box(72, y, 200, y + 20)), ("Border", new PdfArray([PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(0)])), target);
        var links = new PdfArray([
            TestPdf.Ref(pdf.Add(Link(700, ("Dest", Fit(pages[0]))))),
            TestPdf.Ref(pdf.Add(Link(650, ("A", TestPdf.Dict(("S", TestPdf.N("GoTo")), ("D", S("eins"))))))),
            TestPdf.Ref(pdf.Add(Link(600, ("Dest", S("drei"))))),
        ]);
        ((PdfDictionary)pdf.Get(pages[2])).Set(PdfName.Get("Annots"), links);
        return PdfDocument.Open(pdf.Build());
    }

    private static PdfDocument WithoutFirstPage()
    {
        using var doc = ThreePages();
        var items = PageItem.From(doc);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [items[1], items[2]], output);
        return PdfDocument.Open(output.ToArray());
    }

    /// <summary>The first page deleted: the name tree keeps „drei“ only, the older /Dests loses „alt“ (and goes, empty).</summary>
    [Fact]
    public void FirstPageDeleted_NamedDestinationsToItGo()
    {
        using var saved = WithoutFirstPage();
        var names = saved.Catalog!.GetDictionary(PdfName.Get("Names"))!.GetDictionary(PdfName.Get("Dests"))!.GetArray(PdfName.Get("Names"))!;
        Assert.Equal(2, names.Count);
        Assert.Equal("drei", ((PdfString)names.Get(0)!).ToText());
        Assert.Same(saved.Pages[1].Dictionary, ((PdfArray)names.Get(1)!).Get(0));
        Assert.Null(saved.Catalog!.GetDictionary(PdfName.Get("Dests"))?.Get(PdfName.Get("alt")) as PdfArray);
    }

    /// <summary>
    /// The first page deleted: no link on the page kept names null as its page — the link to page 1 and the one to the name „eins“ lose
    /// their destination; the link to „drei“ keeps it.
    /// </summary>
    [Fact]
    public void FirstPageDeleted_LinksToItLoseTheirDestination()
    {
        using var saved = WithoutFirstPage();
        var annots = saved.Pages[1].Dictionary.GetArray(PdfName.Get("Annots"))!;
        var links = Enumerable.Range(0, annots.Count).Select(annots.Get).OfType<PdfDictionary>().ToList();
        foreach (var link in links)
        {
            var dest = link.Get(PdfName.Get("Dest")) ?? link.GetDictionary(PdfName.Get("A"))?.Get(PdfName.Get("D"));
            Assert.False(dest is PdfArray { Count: > 0 } array && array.Get(0) is PdfNull, "Ziel mit null statt Seite");
        }
        Assert.Contains(links, l => l.Get(PdfName.Get("Dest")) is PdfString named && named.ToText() == "drei");
    }
}
