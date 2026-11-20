using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The rest of 6.4 step 2 (docs/ABLAUFPLAN.md; docs/FEHLERGEDAECHTNIS.md, No. 636): a chosen tab order of a tagged page goes into the tree
/// (the object references in that order; ISO 14289-1, 7.18.3; ISO 14289-2, 8.9.3.3) where the elements share a parent, and an own label of a
/// check box or radio button names the field for screen readers (/TU, /Contents; ISO 14289-2, 8.10.2). Before: the order stayed only in
/// /Annots with a hint, the own labels were not used when writing.
/// </summary>
public sealed class TabOrderAndLabelTests
{
    /// <summary>
    /// A tagged page with the squares „A“ (20) and „B“ (21) in /Annots, each in an Annot element (10, 11) — both under the Document
    /// (<paramref name="sameParent"/>) or each in a paragraph of its own (8, 9).
    /// </summary>
    private static byte[] Tagged(bool sameParent) => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 6 0 R/MarkInfo<</Marked true>>/Lang(de-DE)>>",
        [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<<>>/StructParents 0/Tabs/S/Annots[20 0 R 21 0 R]>>",
        [4] = ("", "/P <</MCID 0>> BDC 0 0 m 10 10 l S EMC /P <</MCID 1>> BDC 0 20 m 10 30 l S EMC"),
        [6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 3>>",
        [7] = sameParent ? "<</Type/StructElem/S/Document/P 6 0 R/K[8 0 R 9 0 R 10 0 R 11 0 R]>>" : "<</Type/StructElem/S/Document/P 6 0 R/K[8 0 R 9 0 R]>>",
        [8] = sameParent ? "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K 0>>" : "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K[0 10 0 R]>>",
        [9] = sameParent ? "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K 1>>" : "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K[1 11 0 R]>>",
        [10] = $"<</Type/StructElem/S/Annot/P {(sameParent ? 7 : 8)} 0 R/Pg 3 0 R/K<</Type/OBJR/Obj 20 0 R/Pg 3 0 R>>>>",
        [11] = $"<</Type/StructElem/S/Annot/P {(sameParent ? 7 : 9)} 0 R/Pg 3 0 R/K<</Type/OBJR/Obj 21 0 R/Pg 3 0 R>>>>",
        [15] = "<</Nums[0[8 0 R 9 0 R] 1 10 0 R 2 11 0 R]>>",
        [20] = "<</Type/Annot/Subtype/Square/Rect[100 100 200 200]/NM(A)/F 4/StructParent 1/Contents(A)/AP<</N 22 0 R>>>>",
        [21] = "<</Type/Annot/Subtype/Square/Rect[300 100 400 200]/NM(B)/F 4/StructParent 2/Contents(B)/AP<</N 22 0 R>>>>",
        [22] = ("/Type/XObject/Subtype/Form/BBox[0 0 100 100]", "1 0 0 RG 0 0 100 100 re S"),
    });

    private static (PdfDocument Doc, PdfSaveResult Result) Reversed(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([.. items[0].Elements.OfType<ForeignAnnotation>().Reverse()]);
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, items, output);
        return (PdfDocument.Open(output.ToArray()), result);
    }

    /// <summary>The /Contents of the annotations each element of the parent names, in the order of the parent's kids.</summary>
    private static List<string> OrderUnder(PdfDictionary parent) =>
        [.. ((PdfArray)parent.Get(PdfName.Get("K"))!).Select((_, i) => ((PdfArray)parent.Get(PdfName.Get("K"))!).Get(i))
            .OfType<PdfDictionary>().Where(e => e.GetName(PdfName.Get("S"))?.Value == "Annot")
            .Select(e => ((PdfDictionary)((PdfDictionary)e.Get(PdfName.Get("K"))!).Get(PdfName.Get("Obj"))!).GetString(PdfName.Contents)!.ToText())];

    /// <summary>Both elements under the Document: put in the chosen order, no hint needed.</summary>
    [Fact]
    public void SameParent_OrderGoesIntoTheTree()
    {
        var (doc, result) = Reversed(Tagged(sameParent: true));
        using (doc)
        {
            var document = (PdfDictionary)doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.Get(PdfName.Get("K"))!;
            Assert.Equal(["B", "A"], OrderUnder(document));
            Assert.False(result.TabOrderByStructure);
            StructureAssert.Clean(doc);
        }
    }

    /// <summary>Each element in a paragraph of its own: the tree stays (its reading order is the content's), the save gives the hint.</summary>
    [Fact]
    public void DifferentParents_TreeStays_Hint()
    {
        var (doc, result) = Reversed(Tagged(sameParent: false));
        using (doc)
        {
            Assert.True(result.TabOrderByStructure);
            StructureAssert.Clean(doc);
        }
    }

    /// <summary>
    /// A check box with a technical name and an own label: /TU and /Contents say the label (before: nothing — the name „Feld1“ is technical);
    /// radio buttons say their own option labels.
    /// </summary>
    [Fact]
    public void OwnLabels_NameTheFields()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var box = new FieldElement { Rect = new PdfRectangle(100, 700, 112, 712), Spec = new FieldSpec { Kind = FormFieldKind.CheckBox, Name = "Feld1" } };
        var boxLabel = new TextElement { Rect = new PdfRectangle(120, 700, 300, 712), Text = "Ich stimme den Bedingungen zu", LabelFor = "Feld1" };
        var yes = new FieldElement { Rect = new PdfRectangle(100, 650, 112, 662), Spec = new FieldSpec { Kind = FormFieldKind.Radio, Name = "Gruppe1", ExportValue = "ja" } };
        var no = new FieldElement { Rect = new PdfRectangle(100, 630, 112, 642), Spec = new FieldSpec { Kind = FormFieldKind.Radio, Name = "Gruppe1", ExportValue = "nein" } };
        var yesLabel = new TextElement { Rect = new PdfRectangle(120, 650, 300, 662), Text = "Ja, per E-Mail", LabelFor = "Gruppe1", LabelState = "ja" };
        var noLabel = new TextElement { Rect = new PdfRectangle(120, 630, 300, 642), Text = "Nein, per Post", LabelFor = "Gruppe1", LabelState = "nein" };
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [PageItem.From(doc)[0].WithElements([box, boxLabel, yes, no, yesLabel, noLabel])], output);
        using var saved = PdfDocument.Open(output.ToArray());
        var annots = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
        var widgets = Enumerable.Range(0, annots.Count).Select(i => (PdfDictionary)annots.Get(i)).Where(a => a.GetName(PdfName.Subtype)?.Value == "Widget").ToList();
        var check = widgets.Single(w => w.GetString(PdfName.Get("T"))?.ToText() == "Feld1");
        Assert.Equal("Ich stimme den Bedingungen zu", check.GetString(PdfName.Get("TU"))!.ToText());
        Assert.Equal("Ich stimme den Bedingungen zu", check.GetString(PdfName.Contents)!.ToText());
        var radios = widgets.Where(w => w.GetDictionary(PdfName.Get("Parent"))?.GetString(PdfName.Get("T"))?.ToText() == "Gruppe1")
            .Select(w => w.GetString(PdfName.Contents)!.ToText()).ToList();
        Assert.Equal(["Ja, per E-Mail", "Nein, per Post"], radios);
    }
}
