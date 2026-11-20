using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The tab order of pages with annotations in tagged files follows the structure (/Tabs /S; ISO 14289-1, 7.18.3; ISO 14289-2, 8.9.3.3;
/// docs/ABLAUFPLAN.md, 3.3 c; docs/FEHLERGEDAECHTNIS.md, No. 491).
/// </summary>
public sealed class TaggedTabsTests
{
    /// <summary>A one-page file, tagged or not, whose page has the square annotations „A“ and „B“ (none if <paramref name="annotated"/> is false).</summary>
    private static byte[] File(bool tagged, bool annotated, string? tabs = null)
    {
        var pdf = Simple(1);
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        if (tagged)
        {
            catalog.Set(PdfName.Get("MarkInfo"), Dict(("Marked", PdfBoolean.True)));
            catalog.Set(PdfName.Get("StructTreeRoot"), Ref(pdf.Add(Dict(("Type", N("StructTreeRoot"))))));
        }
        var page = (PdfDictionary)pdf.Get(5);
        if (annotated)
        {
            // (with an appearance: annotations of other programs become elements only with one)
            var look = pdf.Add(new PdfStream(Dict(("Type", N("XObject")), ("Subtype", N("Form")), ("BBox", Box(0, 0, 100, 100))), "1 0 0 RG 0 0 100 100 re S"u8.ToArray()));
            page.Set(PdfName.Get("Annots"), new PdfArray([.. new[] { "A", "B" }.Select(name => (PdfObject)Ref(pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("Square")),
                ("Rect", Box(100, 100, 200, 200)), ("NM", new PdfString(System.Text.Encoding.ASCII.GetBytes(name))), ("F", PdfInteger.Of(4)),
                ("AP", Dict(("N", Ref(look)))))))) ]));
        }
        if (tabs is not null) page.Set(PdfName.Get("Tabs"), N(tabs));
        return pdf.Build();
    }

    private static (PdfDocument Doc, PdfSaveResult Result) Saved(byte[] file, Func<PageItem, PageItem> change)
    {
        using var doc = PdfDocument.Open(file);
        var items = PageItem.From(doc);
        items[0] = change(items[0]);
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, items, output);
        return (PdfDocument.Open(output.ToArray()), result);
    }

    private static PageItem Reversed(PageItem item)
    {
        return item.WithElements([.. item.Elements.OfType<ForeignAnnotation>().Reverse()]);
    }

    private static IEnumerable<string> Names(PdfDocument doc) =>
        doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Select((_, i) => ((PdfDictionary)doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(i)).GetString(PdfName.Get("NM"))!.ToText());

    /// <summary>A tagged page without /Tabs gets a stamp: /Tabs /S (before: none).</summary>
    [Fact]
    public void TaggedPage_NewAnnotation_TabsIsS()
    {
        var (doc, _) = Saved(File(tagged: true, annotated: false), item =>
            item.WithElements([.. item.Elements, new ShapeElement { Kind = ShapeKind.Rectangle, Rect = new PdfRectangle(300, 300, 400, 360), FillColor = 0xC00000 }]));
        using (doc)
        {
            Assert.NotNull(doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots")));
            Assert.Equal("S", doc.Pages[0].Dictionary.GetName(PdfName.Get("Tabs"))?.Value);
        }
    }

    /// <summary>
    /// The order changed on a tagged page with /Tabs /S: /Tabs stays, /Annots in the new order, and the result says the order follows the
    /// structure (before: /Tabs removed).
    /// </summary>
    [Fact]
    public void Reordered_TaggedPage_TabsStaysS()
    {
        var (doc, result) = Saved(File(tagged: true, annotated: true, tabs: "S"), Reversed);
        using (doc)
        {
            Assert.Equal("S", doc.Pages[0].Dictionary.GetName(PdfName.Get("Tabs"))?.Value);
            Assert.Equal(["B", "A"], Names(doc));
            Assert.True(result.TabOrderByStructure);
        }
    }

    /// <summary>In an untagged file the chosen order counts: /Tabs goes, as before.</summary>
    [Fact]
    public void Reordered_UntaggedPage_TabsRemoved()
    {
        var (doc, result) = Saved(File(tagged: false, annotated: true, tabs: "R"), Reversed);
        using (doc)
        {
            Assert.False(doc.Pages[0].Dictionary.ContainsKey(PdfName.Get("Tabs")));
            Assert.Equal(["B", "A"], Names(doc));
            Assert.False(result.TabOrderByStructure);
        }
    }
}
