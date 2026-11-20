using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Structure elements whose content a redaction removed entirely (docs/ABLAUFPLAN.md, 6.6 b; ISO 32000-2, 14.8.2.4; docs/FEHLERGEDAECHTNIS.md,
/// No. 649): such an element goes, unless it is a table cell or list item (the table or list would become irregular); an element with
/// content left keeps all its marked content. Before: it stayed, and screen readers announced an empty paragraph.
/// </summary>
public sealed class EmptiedElementTests
{
    /// <summary>
    /// A tagged page: per line (text at y = 700, 680, 660 …) marked content MCID i of an element of the given type under a Document; the text
    /// of a line may be given in two parts (two MCIDs of one element). Types like „TD“ hang in TR in Table; „TD>P“ is a cell holding a paragraph
    /// that holds the marked content.
    /// </summary>
    private static byte[] Page(params (string Type, string[] Parts)[] lines)
    {
        var pdf = TestPdf.Simple(1);
        var content = new StringBuilder();
        var mcid = 0;
        var mcidsOf = new List<List<int>>();
        for (var i = 0; i < lines.Length; i++)
        {
            var ids = new List<int>();
            var x = 72;
            foreach (var part in lines[i].Parts)
            {
                content.Append($"/{(lines[i].Type == "TD>P" ? "P" : lines[i].Type)} <</MCID {mcid}>> BDC BT /F1 12 Tf {x} {700 - 20 * i} Td ({part}) Tj ET EMC ");
                ids.Add(mcid++);
                x += 200;
            }
            mcidsOf.Add(ids);
        }
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(content.ToString())));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("StructParents"), PdfInteger.Of(0));
        var root = pdf.Add(new PdfDictionary());
        var document = pdf.Add(new PdfDictionary());
        var elements = new List<int>();
        var cellsOf = new List<int>();   // the element in the row: the cell itself, or the TD around the paragraph
        var top = new List<PdfObject>();
        int? row = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var cell = lines[i].Type is "TD" or "TH" or "TD>P";
            if (cell && row is null)
            {
                var table = pdf.Add(new PdfDictionary());
                row = pdf.Add(new PdfDictionary());
                pdf.Replace(table, TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("Table")), ("P", TestPdf.Ref(document)), ("K", TestPdf.Ref(row.Value))));
                top.Add(TestPdf.Ref(table));
            }
            var ids = mcidsOf[i];
            var nested = lines[i].Type == "TD>P";
            var holder = nested ? pdf.Add(new PdfDictionary()) : 0;
            var element = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N(nested ? "P" : lines[i].Type)),
                ("P", TestPdf.Ref(nested ? holder : cell ? row!.Value : document)),
                ("Pg", TestPdf.Ref(5)), ("K", ids.Count == 1 ? PdfInteger.Of(ids[0]) : new PdfArray([.. ids.Select(id => (PdfObject)PdfInteger.Of(id))]))));
            if (nested)
                pdf.Replace(holder, TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("TD")), ("P", TestPdf.Ref(row!.Value)), ("K", TestPdf.Ref(element))));
            elements.Add(element);
            cellsOf.Add(nested ? holder : element);
            if (!cell) top.Add(TestPdf.Ref(element));
        }
        if (row is { } r)
        {
            var cells = Enumerable.Range(0, lines.Length).Where(i => lines[i].Type is "TD" or "TH" or "TD>P").Select(i => (PdfObject)TestPdf.Ref(cellsOf[i]));
            var tableRef = (PdfReference)top.First(t => t is PdfReference tr && pdf.Get(tr.Id.Number) is PdfDictionary d && d.GetName(PdfName.Get("S"))?.Value == "Table");
            pdf.Replace(r, TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("TR")), ("P", tableRef), ("K", new PdfArray([.. cells]))));
        }
        pdf.Replace(document, TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("Document")), ("P", TestPdf.Ref(root)), ("K", new PdfArray(top))));
        var byMcid = new PdfArray();
        for (var i = 0; i < lines.Length; i++)
            foreach (var _ in mcidsOf[i]) byMcid.Add(TestPdf.Ref(elements[i]));
        pdf.Replace(root, TestPdf.Dict(("Type", TestPdf.N("StructTreeRoot")), ("K", TestPdf.Ref(document)),
            ("ParentTree", TestPdf.Dict(("Nums", new PdfArray([PdfInteger.Of(0), byMcid])))), ("ParentTreeNextKey", PdfInteger.Of(1))));
        var catalog = (PdfDictionary)pdf.Get(1);
        catalog.Set(PdfName.Get("StructTreeRoot"), TestPdf.Ref(root));
        catalog.Set(PdfName.Get("MarkInfo"), TestPdf.Dict(("Marked", PdfBoolean.True)));
        return pdf.Build();
    }

    private static List<PdfDictionary> Kids(PdfDictionary element) => element.Get(PdfName.Get("K")) switch
    {
        PdfArray a => [.. Enumerable.Range(0, a.Count).Select(a.Get).OfType<PdfDictionary>().Where(d => d.ContainsKey(PdfName.Get("S")))],
        PdfDictionary one when one.ContainsKey(PdfName.Get("S")) => [one],
        _ => [],
    };

    private static PdfDictionary DocumentOf(PdfDocument doc) =>
        (PdfDictionary)doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.Get(PdfName.Get("K"))!;

    /// <summary>A heading and a paragraph; the paragraph's line wholly under a bar: the paragraph goes, the heading stays. Before: an empty P.</summary>
    [Fact]
    public void WholeParagraphRedacted_ItsElementGoes()
    {
        using var doc = PdfDocument.Open(Page(("H1", ["Kapitel"]), ("P", ["Erika Mustermann"])));
        using var saved = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(60, 675, 400, 695))));
        Assert.Equal(["H1"], Kids(DocumentOf(saved)).Select(e => e.GetName(PdfName.Get("S"))!.Value));
        Assert.Empty(Core.Pdf.Accessibility.TaggedContent.ParentTree(saved));
    }

    /// <summary>A table cell wholly under a bar: it stays (empty), the table regular.</summary>
    [Fact]
    public void WholeCellRedacted_TableCellStays()
    {
        using var doc = PdfDocument.Open(Page(("TD", ["Name"]), ("TD", ["Erika Mustermann"])));
        using var saved = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(60, 675, 400, 695))));
        var table = Assert.Single(Kids(DocumentOf(saved)));
        Assert.Equal(["TD", "TD"], Kids(Assert.Single(Kids(table))).Select(e => e.GetName(PdfName.Get("S"))!.Value));
    }

    /// <summary>
    /// A table cell holding a paragraph, the paragraph wholly under a bar (docs/FEHLERGEDAECHTNIS.md, No. 681; found in 6.8 with a tax form):
    /// the paragraph goes, the cell stays (empty) — before the cell went with it and the row lost a cell (ISO 14289-1, 7.2: rows regular).
    /// </summary>
    [Fact]
    public void ParagraphInACellRedacted_CellStaysEmpty()
    {
        using var doc = PdfDocument.Open(Page(("TD>P", ["Name"]), ("TD>P", ["Erika Mustermann"])));
        using var saved = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(60, 675, 400, 695))));
        var table = Assert.Single(Kids(DocumentOf(saved)));
        var cells = Kids(Assert.Single(Kids(table)));
        Assert.Equal(["TD", "TD"], cells.Select(e => e.GetName(PdfName.Get("S"))!.Value));
        Assert.Single(Kids(cells[0]));   // „Name“ keeps its paragraph
        Assert.Empty(Kids(cells[1]));
    }

    /// <summary>A paragraph of two parts, one under a bar: it stays with both MCIDs (the parent tree names it for each).</summary>
    [Fact]
    public void PartOfAParagraphRedacted_ElementKeepsAllItsMarkedContent()
    {
        using var doc = PdfDocument.Open(Page(("H1", ["Kapitel"]), ("P", ["Erika Mustermann", "wohnt hier"])));
        using var saved = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(60, 675, 250, 695))));
        var paragraph = Kids(DocumentOf(saved))[1];
        Assert.Equal([1L, 2L], ((PdfArray)paragraph.Get(PdfName.Get("K"))!).Select(k => ((PdfInteger)k).Value));
        Assert.Empty(Core.Pdf.Accessibility.TaggedContent.ParentTree(saved));
    }
}
