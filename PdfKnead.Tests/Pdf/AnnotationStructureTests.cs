using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// New annotations and fields in a tagged file (docs/ABLAUFPLAN.md, 6.4 step 2; ISO 14289-1, 7.18.1; ISO 14289-2, 8.9.2.1, 8.9.3.2;
/// docs/FEHLERGEDAECHTNIS.md, No. 633): each gets an element of its own (Annot, Form for a field) next to the content it sits on, an object
/// reference to it and a /StructParent; written anew on the next save, the old element goes. Before: no element, no /StructParent — and the
/// save took the PDF/UA identification for „neue Anmerkungen oder Felder ohne Struktur“.
/// </summary>
public sealed class AnnotationStructureTests
{
    /// <summary>
    /// One page: a heading (8, MCID 0) at the top and a paragraph (9, MCID 1) lower down, under a Document (7); <paramref name="change"/>
    /// changes objects.
    /// </summary>
    private static byte[] Tagged(Action<Dictionary<int, object>>? change = null)
    {
        var objects = new Dictionary<int, object>
        {
        [1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 6 0 R/MarkInfo<</Marked true>>/Lang(de-DE)>>",
        [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>/StructParents 0>>",
        [4] = ("", "/H1 <</MCID 0>> BDC BT /F1 20 Tf 72 760 Td (Rechnung) Tj ET EMC /P <</MCID 1>> BDC BT /F1 12 Tf 72 600 Td (Max Mustermann, Musterstadt) Tj ET EMC"),
        [5] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
        [6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 1>>",
        [7] = "<</Type/StructElem/S/Document/P 6 0 R/K[8 0 R 9 0 R]>>",
        [8] = "<</Type/StructElem/S/H1/P 7 0 R/Pg 3 0 R/K 0>>",
        [9] = "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K 1>>",
        [15] = "<</Nums[0[8 0 R 9 0 R]]>>",
        };
        change?.Invoke(objects);
        return CraftedFileTests.Build(objects);
    }

    private static PdfDocument Save(PdfDocument doc, IReadOnlyList<PageItem> pages, out PdfSaveResult result)
    {
        using var output = new MemoryStream();
        result = PdfWriter.Write(doc, pages, output, new PdfSaveOptions());
        return PdfDocument.Open(output.ToArray());
    }

    /// <summary>The element the parent tree gives an annotation's /StructParent.</summary>
    private static PdfDictionary ElementOf(PdfDocument doc, PdfDictionary annot)
    {
        var tree = doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.GetDictionary(PdfName.Get("ParentTree"))!;
        return (PdfDictionary)NumberTree.Value(tree, annot.GetInteger(PdfName.Get("StructParent"))!.Value)!;
    }

    private static List<PdfDictionary> Annotations(PdfDocument doc)
    {
        var annots = doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
        return [.. Enumerable.Range(0, annots.Count).Select(i => (PdfDictionary)annots.Get(i))];
    }

    /// <summary>
    /// A note on the paragraph: an Annot element inside that paragraph (an annotation is inline content of the block it sits on), with an object
    /// reference to the note; the tree is clean and the identification-breaking reason is gone.
    /// </summary>
    [Fact]
    public void Note_AnnotElementInTheParagraphItSitsOn()
    {
        using var doc = PdfDocument.Open(Tagged());
        var note = new NoteElement { Rect = new PdfRectangle(80, 600, 100, 620), Text = "Adresse prüfen" };
        using var saved = Save(doc, [PageItem.From(doc)[0].WithElements([note])], out var result);
        var annot = Assert.Single(Annotations(saved));
        var element = ElementOf(saved, annot);
        Assert.Equal("Annot", element.GetName(PdfName.Get("S"))!.Value);
        Assert.Equal("P", ((PdfDictionary)element.Get(PdfName.Get("P"))!).GetName(PdfName.Get("S"))!.Value);
        var objr = Assert.IsType<PdfDictionary>(((PdfArray)element.Get(PdfName.Get("K"))!).Get(0));
        Assert.Same(annot, objr.Get(PdfName.Get("Obj")));
        Assert.DoesNotContain("neue Anmerkungen oder Felder ohne Struktur", result.AccessibilityBreaks);
        StructureAssert.Clean(saved);
    }

    /// <summary>A new text field far from any content: a Form element beside the last element of the page, never inside it nor under the root.</summary>
    [Fact]
    public void TextField_FormElementBesideTheLastElement()
    {
        using var doc = PdfDocument.Open(Tagged());
        var field = new TextElement { Rect = new PdfRectangle(300, 100, 500, 120), Text = "", IsField = true, FieldName = "Kundennummer" };
        using var saved = Save(doc, [PageItem.From(doc)[0].WithElements([field])], out _);
        var element = ElementOf(saved, Assert.Single(Annotations(saved)));
        Assert.Equal("Form", element.GetName(PdfName.Get("S"))!.Value);
        Assert.Equal("Document", ((PdfDictionary)element.Get(PdfName.Get("P"))!).GetName(PdfName.Get("S"))!.Value);
        Assert.Equal("S", saved.Pages[0].Dictionary.GetName(PdfName.Get("Tabs"))?.Value);
        StructureAssert.Clean(saved);
    }

    /// <summary>Saved twice: the note is written anew with a new element, the old element is gone — one Annot element, a clean tree.</summary>
    [Fact]
    public void SavedTwice_OldElementGoes()
    {
        using var doc = PdfDocument.Open(Tagged());
        var note = new NoteElement { Rect = new PdfRectangle(80, 600, 100, 620), Text = "Adresse prüfen" };
        using var once = Save(doc, [PageItem.From(doc)[0].WithElements([note])], out _);
        using var twice = Save(once, PageItem.From(once), out _);
        var annot = Assert.Single(Annotations(twice));
        var root = twice.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!;
        var annotElements = 0;
        var stack = new Stack<PdfObject?>([root.Get(PdfName.Get("K"))]);
        while (stack.TryPop(out var k))
            switch (k)
            {
                case PdfArray a:
                    for (var i = 0; i < a.Count; i++) stack.Push(a.Get(i));
                    break;
                case PdfDictionary e when e.GetName(PdfName.Get("S")) is { } s:
                    if (s.Value == "Annot") annotElements++;
                    stack.Push(e.Get(PdfName.Get("K")));
                    break;
            }
        Assert.Equal(1, annotElements);
        Assert.Equal("Annot", ElementOf(twice, annot).GetName(PdfName.Get("S"))!.Value);
        StructureAssert.Clean(twice);
    }

    /// <summary>
    /// A tree holding only an empty Document (the content untagged): the note gets its element in the Document; saved again, the old element
    /// goes and the new one comes — the copy stays tagged (with the old elements taken out by pruning, the tree was left empty and the copy
    /// untagged; docs/FEHLERGEDAECHTNIS.md, No. 633).
    /// </summary>
    [Fact]
    public void OnlyAnEmptyDocument_StaysTaggedWhenSavedAgain()
    {
        using var doc = PdfDocument.Open(Tagged(o =>
        {
            o[4] = ("", "/Artifact BMC BT /F1 12 Tf 72 600 Td (Max Mustermann) Tj ET EMC");
            o[7] = "<</Type/StructElem/S/Document/P 6 0 R>>";
            o[15] = "<</Nums[]>>";
            o[3] = ((string)o[3]).Replace("/StructParents 0", "", StringComparison.Ordinal);
            o.Remove(8);
            o.Remove(9);
        }));
        var note = new NoteElement { Rect = new PdfRectangle(80, 600, 100, 620), Text = "Adresse prüfen" };
        using var once = Save(doc, [PageItem.From(doc)[0].WithElements([note])], out _);
        using var twice = Save(once, PageItem.From(once), out var result);
        Assert.DoesNotContain("kein getaggter Inhalt mehr", result.AccessibilityBreaks);
        var element = ElementOf(twice, Assert.Single(Annotations(twice)));
        Assert.Equal("Document", ((PdfDictionary)element.Get(PdfName.Get("P"))!).GetName(PdfName.Get("S"))!.Value);
        StructureAssert.Clean(twice);
    }

    /// <summary>An untagged file: the new note gets no /StructParent (nothing to hang it into).</summary>
    [Fact]
    public void UntaggedFile_NoStructParent()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var note = new NoteElement { Rect = new PdfRectangle(80, 600, 100, 620), Text = "Adresse prüfen" };
        using var saved = Save(doc, [PageItem.From(doc)[0].WithElements([note])], out _);
        Assert.False(Assert.Single(Annotations(saved)).ContainsKey(PdfName.Get("StructParent")));
    }

    /// <summary>The MCID at a point: inside the bounds of the marked content, else the nearest within reach, else none.</summary>
    [Fact]
    public void MarkedContentGeometry_FindsTheMcidAtAPoint()
    {
        using var doc = PdfDocument.Open(Tagged());
        var bounds = MarkedContentGeometry.Of(doc, doc.Pages[0]);
        Assert.Equal([0, 1], bounds.Keys.Order());
        Assert.Equal(1, MarkedContentGeometry.At(bounds, 90, 604));
        Assert.Equal(0, MarkedContentGeometry.At(bounds, 80, 765));
        Assert.Null(MarkedContentGeometry.At(bounds, 500, 100));
    }
}
