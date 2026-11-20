using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// „Ebenen“ and copying objects of a tagged page (docs/ABLAUFPLAN.md, 6.6 d; docs/FEHLERGEDAECHTNIS.md, No. 651): an object of the structure
/// (in marked content with an MCID) stays where it is — moving it into a form left an empty bracket behind and MCIDs in a form without keys
/// (content outside the structure, ISO 32000-2, 14.7.5.2; ISO 14289-1, 7.20) —, the move says why; an object outside the structure moves as
/// before; a copy keeps no MCID (pasted it named no element or a stranger's).
/// </summary>
public sealed class LayerStructureTests
{
    /// <summary>
    /// Three overlapping squares (red, green, blue, each in a block of its own); <paramref name="markedRed"/>: the red one is marked content
    /// with MCID 0 of a Figure on a tagged page (/StructParents), the others artifacts.
    /// </summary>
    private static PdfDocument Squares(bool markedRed)
    {
        var red = markedRed ? "/Figure <</MCID 0>> BDC q 1 0 0 rg 0 0 100 100 re f Q EMC" : "/Artifact BMC q 1 0 0 rg 0 0 100 100 re f Q EMC";
        var content = $"1 0 0 1 50 50 cm {red} /Artifact BMC q 0 1 0 rg 50 50 100 100 re f Q EMC /Artifact BMC q 0 0 1 rg 100 100 100 100 re f Q EMC";
        var pdf = new TestPdf();
        var stream = pdf.Add(new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(content)));
        var pages = pdf.Add(new PdfDictionary());
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pages)), ("Contents", TestPdf.Ref(stream)),
            ("MediaBox", TestPdf.Box(0, 0, 400, 400)), ("StructParents", PdfInteger.Of(0))));
        pdf.Replace(pages, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray([TestPdf.Ref(page)])), ("Count", PdfInteger.Of(1))));
        var root = pdf.Add(new PdfDictionary());
        var figure = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("Figure")), ("P", TestPdf.Ref(root)), ("Pg", TestPdf.Ref(page)),
            ("Alt", new PdfString("Rotes Quadrat"u8.ToArray())), ("K", PdfInteger.Of(0))));
        pdf.Replace(root, TestPdf.Dict(("Type", TestPdf.N("StructTreeRoot")), ("K", TestPdf.Ref(figure)),
            ("ParentTree", TestPdf.Dict(("Nums", new PdfArray([PdfInteger.Of(0), new PdfArray([TestPdf.Ref(figure)])])))), ("ParentTreeNextKey", PdfInteger.Of(1))));
        pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pages)), ("StructTreeRoot", TestPdf.Ref(root)),
            ("MarkInfo", TestPdf.Dict(("Marked", PdfBoolean.True)))));
        return PdfDocument.Open(pdf.Build());
    }

    /// <summary>The red square of the structure brought forward: it stays, the move says why. Before: moved into a form, its MCID with it.</summary>
    [Fact]
    public void TaggedObject_StaysWhereItIs_MoveSaysWhy()
    {
        using var doc = Squares(markedRed: true);
        var item = PageItem.From(doc)[0];
        var found = ContentObjectEditor.ObjectsOf(item);
        var refused = Assert.Throws<ArgumentException>(() => ContentObjectEditor.Restack(item, [found.KeyOf(0)], ContentLayerMove.Forward, found));
        Assert.Contains(Tr.T("Struktur für Bildschirmleser"), refused.Message);
    }

    /// <summary>The same square as an artifact (no part of the structure): it moves forward as before.</summary>
    [Fact]
    public void ArtifactObject_OnATaggedPage_Moves()
    {
        using var doc = Squares(markedRed: false);
        var item = PageItem.From(doc)[0];
        var found = ContentObjectEditor.ObjectsOf(item);
        Assert.NotNull(ContentObjectEditor.Restack(item, [found.KeyOf(0)], ContentLayerMove.Forward, found));
    }

    /// <summary>A copy of the red square of the structure keeps its marked content but no MCID.</summary>
    [Fact]
    public void Copy_KeepsNoMcid()
    {
        using var doc = Squares(markedRed: true);
        var item = PageItem.From(doc)[0];
        var found = ContentObjectEditor.ObjectsOf(item);
        var copied = ContentObjectEditor.Copy(item, [found.KeyOf(0)], found)!.Value;
        var data = Encoding.Latin1.GetString(copied.Form.RawData!);
        Assert.DoesNotContain("MCID", data);
        Assert.Contains("re", data);
    }
}
