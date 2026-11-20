using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Where a new structure element goes so that the tree keeps the containment rules (<see cref="Containment.PlaceFor"/>; docs/ABLAUFPLAN.md,
/// 6.2 step 3; ISO 32000-2, Annex L; ISO/TS 32005, 7.2). The trees are built of direct dictionaries: a handle is the dictionary itself.
/// </summary>
public class PlaceForTests
{
    private readonly PdfDictionary _root = new();

    public PlaceForTests() => _root.Set(PdfName.Type, PdfName.Get("StructTreeRoot"));

    private StructureView View => new(_root, o => o);

    /// <summary>An element of <paramref name="type"/> under <paramref name="parent"/> (the root if null), with an MCID if it holds content.</summary>
    private PdfDictionary Add(string type, PdfDictionary? parent = null, int? mcid = null)
    {
        var element = new PdfDictionary();
        element.Set(PdfName.Type, PdfName.Get("StructElem"));
        element.Set(PdfName.Get("S"), PdfName.Get(type));
        element.Set(PdfName.Get("P"), parent ?? _root);
        if (mcid is { } m) element.Set(PdfName.Get("K"), new PdfArray([PdfInteger.Of(m)]));
        var holder = parent ?? _root;
        if (holder.Get(PdfName.Get("K")) is not PdfArray kids) holder.Set(PdfName.Get("K"), kids = new PdfArray());
        kids.Add(element);
        return element;
    }

    /// <summary>A new paragraph beside a paragraph becomes its sibling right after it, not its child (P &gt; P is not allowed).</summary>
    [Fact]
    public void ParagraphBesideParagraph_IsSiblingRightAfter()
    {
        var document = Add("Document");
        Add("P", document, 0);
        var anchor = Add("P", document, 1);
        Add("P", document, 2);
        var place = Containment.PlaceFor(View, anchor, "P");
        Assert.NotNull(place);
        Assert.Same(document, place.Value.Parent);
        Assert.Equal(2, place.Value.Index);
    }

    /// <summary>Up through the branch: from a Span in a paragraph, the new paragraph goes after that paragraph in the Document.</summary>
    [Fact]
    public void FromSpanInParagraph_AfterTheParagraph()
    {
        var document = Add("Document");
        Add("P", document, 0);
        var paragraph = Add("P", document);
        var span = Add("Span", paragraph, 1);
        Add("P", document, 2);
        var place = Containment.PlaceFor(View, span, "P");
        Assert.Same(document, place!.Value.Parent);
        Assert.Equal(2, place.Value.Index);
    }

    /// <summary>An annotation on a list item goes into the item's body (LI &gt; Annot is not allowed), at its end.</summary>
    [Fact]
    public void AnnotOnListItem_GoesIntoTheBody()
    {
        var document = Add("Document");
        var list = Add("L", document);
        var item = Add("LI", list);
        Add("Lbl", item, 0);
        var body = Add("LBody", item, 1);
        var place = Containment.PlaceFor(View, item, "Annot");
        Assert.Same(body, place!.Value.Parent);
        Assert.Equal(1, place.Value.Index);
        // from the list: down to the body of its last item
        var last = Add("LI", list);
        var lastBody = Add("LBody", last, 2);
        Assert.Same(lastBody, Containment.PlaceFor(View, list, "Annot")!.Value.Parent);
    }

    /// <summary>A figure in a table row goes into the row's last cell (TR and Table may hold no Figure), from a table into its last row's last cell.</summary>
    [Fact]
    public void FigureInTable_GoesIntoTheLastCell()
    {
        var document = Add("Document");
        var table = Add("Table", document);
        var row = Add("TR", table);
        Add("TH", row, 0);
        var cell = Add("TD", row, 1);
        Assert.Same(cell, Containment.PlaceFor(View, row, "Figure")!.Value.Parent);
        Assert.Same(cell, Containment.PlaceFor(View, table, "Figure")!.Value.Parent);
    }

    /// <summary>NonStruct takes the rules of its parent: a paragraph beside a paragraph in NonStruct under Document stays in the NonStruct.</summary>
    [Fact]
    public void NonStruct_TakesTheRulesOfItsParent()
    {
        var document = Add("Document");
        var group = Add("NonStruct", document);
        var anchor = Add("P", group, 0);
        var place = Containment.PlaceFor(View, anchor, "P");
        Assert.Same(group, place!.Value.Parent);
        Assert.Equal(1, place.Value.Index);
    }

    /// <summary>
    /// What the rules limit counts: Document holds one H at most; with one there, a second finds no place below the root — never the root
    /// itself (null: the caller puts it at the end of the top Document).
    /// </summary>
    [Fact]
    public void LimitReached_NoPlace_NeverTheRoot()
    {
        var document = Add("Document");
        Add("H", document, 0);
        var anchor = Add("P", document, 1);
        Assert.Null(Containment.PlaceFor(View, anchor, "H"));
        Assert.NotNull(Containment.PlaceFor(View, anchor, "P"));
    }

    /// <summary>
    /// Beside the anchor only: content that belongs to no element goes after the anchor's branch, not into it — an Annot could hold a Figure,
    /// but the figure is not part of that comment (docs/FEHLERGEDAECHTNIS.md, No. 632).
    /// </summary>
    [Fact]
    public void Beside_NeverIntoTheAnchor()
    {
        var document = Add("Document");
        var note = Add("Annot", document, 0);
        Add("P", document, 1);
        Assert.Same(note, Containment.PlaceFor(View, note, "Figure")!.Value.Parent);
        var beside = Containment.PlaceFor(View, note, "Figure", beside: true);
        Assert.Same(document, beside!.Value.Parent);
        Assert.Equal(1, beside.Value.Index);
    }

    /// <summary>A broken tree (a row right under the root) gives no place, and a type the role map does not resolve takes nothing.</summary>
    [Fact]
    public void BrokenTree_NoPlace()
    {
        var row = Add("TR");
        Assert.Null(Containment.PlaceFor(View, row, "P"));
        var document = Add("Document");
        var own = Add("Sidebar", document, 0);
        var place = Containment.PlaceFor(View, own, "P");
        Assert.Same(document, place!.Value.Parent);
        Assert.Null(Containment.PlaceFor(View, _root, "P"));
    }
}
