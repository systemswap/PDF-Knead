using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Pages taken out of a tagged file (delete, extract, split; docs/ABLAUFPLAN.md, 6.6 h; docs/FEHLERGEDAECHTNIS.md, No. 655): headings are not
/// re-levelled, but where their order breaks the save says so (and the PDF/UA claim goes); /Headers of table cells and /Ref of elements lose
/// what names an element that went. Before: the order broke silently, /Headers named IDs no element had any more, /Ref named nothing.
/// </summary>
public sealed class PageRemovalStructureTests
{
    private static readonly PdfName K = PdfName.Get("K"), S = PdfName.Get("S");

    private sealed record Spec(string Type, int Page, string? Id = null, string[]? Headers = null, int? RefTo = null, int? Row = null, bool Empty = false);

    /// <summary>
    /// Two tagged pages (5 and 7) with one element per spec (one text line each), under a Document; specs with a row number go into rows of
    /// one Table (in that order); an empty spec is an element without content, /K and /Pg (an empty cell of a form).
    /// </summary>
    private static byte[] Tagged(params Spec[] specs)
    {
        var pdf = Simple(2);
        var root = pdf.Add(new PdfDictionary());
        var document = pdf.Add(new PdfDictionary());
        var content = new Dictionary<int, StringBuilder> { [5] = new(), [7] = new() };
        var byPage = new Dictionary<int, PdfArray> { [5] = new(), [7] = new() };
        var numbers = specs.Select(_ => pdf.Add(new PdfDictionary())).ToList();
        var table = specs.Any(s => s.Row is not null) ? pdf.Add(new PdfDictionary()) : 0;
        var rows = specs.Where(s => s.Row is not null).Select(s => s.Row!.Value).Distinct().ToDictionary(r => r, _ => pdf.Add(new PdfDictionary()));
        var kids = new List<PdfObject>();
        if (table > 0) kids.Add(Ref(table));
        for (var i = 0; i < specs.Length; i++)
        {
            var spec = specs[i];
            if (spec.Empty)
            {
                pdf.Replace(numbers[i], Dict(("Type", N("StructElem")), ("S", N(spec.Type)), ("P", Ref(spec.Row is { } at ? rows[at] : document))));
                if (spec.Row is null) kids.Add(Ref(numbers[i]));
                continue;
            }
            var mcid = byPage[spec.Page].Count;
            content[spec.Page].Append($"/{spec.Type} <</MCID {mcid}>> BDC BT /F1 12 Tf 72 {700 - 20 * mcid} Td (Zeile {i}) Tj ET EMC ");
            byPage[spec.Page].Add(Ref(numbers[i]));
            var element = Dict(("Type", N("StructElem")), ("S", N(spec.Type)), ("P", Ref(spec.Row is { } row ? rows[row] : document)), ("Pg", Ref(spec.Page)),
                ("K", PdfInteger.Of(mcid)));
            if (spec.Id is not null) element.Set(PdfName.Get("ID"), new PdfString(Encoding.ASCII.GetBytes(spec.Id)));
            if (spec.Headers is not null)
                element.Set(PdfName.Get("A"), Dict(("O", N("Table")), ("Headers", new PdfArray([.. spec.Headers.Select(h => (PdfObject)new PdfString(Encoding.ASCII.GetBytes(h)))]))));
            if (spec.RefTo is { } target) element.Set(PdfName.Get("Ref"), new PdfArray([Ref(numbers[target])]));
            pdf.Replace(numbers[i], element);
            if (spec.Row is null) kids.Add(Ref(numbers[i]));
        }
        foreach (var (row, number) in rows)
            pdf.Replace(number, Dict(("Type", N("StructElem")), ("S", N("TR")), ("P", Ref(table)),
                ("K", new PdfArray([.. Enumerable.Range(0, specs.Length).Where(i => specs[i].Row == row).Select(i => (PdfObject)Ref(numbers[i]))]))));
        if (table > 0)
            pdf.Replace(table, Dict(("Type", N("StructElem")), ("S", N("Table")), ("P", Ref(document)), ("K", new PdfArray([.. rows.Values.Select(r => (PdfObject)Ref(r))]))));
        foreach (var page in new[] { 5, 7 })
        {
            pdf.Replace(page - 1, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(content[page].ToString())));
            ((PdfDictionary)pdf.Get(page)).Set(PdfName.Get("StructParents"), PdfInteger.Of(page == 5 ? 0 : 1));
        }
        pdf.Replace(document, Dict(("Type", N("StructElem")), ("S", N("Document")), ("P", Ref(root)), ("K", new PdfArray(kids))));
        var idTree = new PdfArray();
        foreach (var (spec, number) in specs.Zip(numbers).Where(e => e.First.Id is not null).OrderBy(e => e.First.Id, StringComparer.Ordinal))
        {
            idTree.Add(new PdfString(Encoding.ASCII.GetBytes(spec.Id!)));
            idTree.Add(Ref(number));
        }
        pdf.Replace(root, Dict(("Type", N("StructTreeRoot")), ("K", Ref(document)), ("IDTree", Dict(("Names", idTree))),
            ("ParentTree", Dict(("Nums", new PdfArray([PdfInteger.Of(0), byPage[5], PdfInteger.Of(1), byPage[7]])))), ("ParentTreeNextKey", PdfInteger.Of(2))));
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(PdfName.Get("StructTreeRoot"), Ref(root));
        catalog.Set(PdfName.Get("MarkInfo"), Dict(("Marked", PdfBoolean.True)));
        catalog.Set(PdfName.Get("Lang"), new PdfString("de"u8.ToArray()));
        return pdf.Build();
    }

    /// <summary>Only the page with <paramref name="index"/> written (as extracting it does).</summary>
    private static (PdfDocument Doc, PdfSaveResult Result) Only(byte[] tagged, int index)
    {
        using var doc = PdfDocument.Open(tagged);
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, [PageItem.From(doc)[index]], output);
        return (PdfDocument.Open(output.ToArray()), result);
    }

    private static IEnumerable<PdfDictionary> All(PdfDocument doc)
    {
        var stack = new Stack<PdfObject?>([doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.Get(K)]);
        while (stack.Count > 0)
            switch (stack.Pop())
            {
                case PdfArray a:
                    for (var i = a.Count - 1; i >= 0; i--) stack.Push(a.Get(i));
                    break;
                case PdfDictionary d when d.ContainsKey(S):
                    yield return d;
                    stack.Push(d.Get(K));
                    break;
            }
    }

    /// <summary>H1 on page 1, H2 on page 2: page 2 extracted begins with H2 — the save names the broken order. Page 1 alone breaks nothing.</summary>
    [Fact]
    public void ExtractPages_FirstHeadingGone_ClaimRemovedOrNoted()
    {
        var tagged = Tagged(new("H1", 5), new("P", 5), new("H2", 7), new("P", 7));
        var (second, broken) = Only(tagged, 1);
        using (second)
        {
            Assert.Contains(broken.AccessibilityBreaks, b => b.Contains("Überschrift", StringComparison.Ordinal));
            Assert.Equal(["H2", "P"], All(second).Where(e => e.GetName(S)!.Value != "Document").Select(e => e.GetName(S)!.Value));   // not re-levelled
        }
        var (first, kept) = Only(tagged, 0);
        using (first) Assert.DoesNotContain(kept.AccessibilityBreaks, b => b.Contains("Überschrift", StringComparison.Ordinal));
    }

    /// <summary>A header cell with ID „kopf“ on page 1, a cell naming it in /Headers on page 2: page 1 deleted, the cell names it no more.</summary>
    [Fact]
    public void DeletePage_TableHeaderGone_HeadersPruned()
    {
        var tagged = Tagged(new("TH", 5, Id: "kopf", Row: 0), new("TD", 7, Headers: ["kopf"], Row: 1), new("P", 7));
        var (doc, _) = Only(tagged, 1);
        using (doc)
        {
            var cell = Assert.Single(All(doc), e => e.GetName(S)!.Value == "TD");
            Assert.True(cell.GetDictionary(PdfName.Get("A")) is { } table && !table.ContainsKey(PdfName.Get("Headers")) && table.GetName(PdfName.Get("O"))?.Value == "Table");
            Assert.Equal(0, Core.Pdf.Accessibility.StructureReferences.DanglingHeaders(doc));
        }
    }

    /// <summary>A paragraph on page 2 whose /Ref names a note on page 1: page 1 deleted, the /Ref goes (never null left).</summary>
    [Fact]
    public void DeletePage_RefToElementGone_RefPruned()
    {
        var tagged = Tagged(new("Note", 5), new("P", 7, RefTo: 0));
        var (doc, _) = Only(tagged, 1);
        using (doc)
        {
            var paragraph = Assert.Single(All(doc), e => e.GetName(S)!.Value == "P");
            Assert.False(paragraph.ContainsKey(PdfName.Get("Ref")));
            Assert.Equal(0, Core.Pdf.Accessibility.StructureReferences.DanglingRefs(doc));
        }
    }

    /// <summary>
    /// A table on page 2 whose first row holds an empty cell (no content, no /Pg — as in the rows of a form): page 1 extracted, the table goes as
    /// a whole (No. 682). Before, the empty cell kept its row and the table: one row of one cell, irregular (ISO 14289-1, 7.2).
    /// </summary>
    [Fact]
    public void ExtractPage_TableOnTheOtherPageWithEmptyCell_TableGoesWhole()
    {
        var tagged = Tagged(new("P", 5), new("TD", 7, Row: 0), new("TD", 0, Row: 0, Empty: true), new("TD", 7, Row: 1), new("TD", 7, Row: 1));
        var (doc, _) = Only(tagged, 0);
        using (doc) Assert.Equal(["P"], All(doc).Where(e => e.GetName(S)!.Value != "Document").Select(e => e.GetName(S)!.Value));
    }

    /// <summary>
    /// A row across both pages (one cell on each), a second row on page 1: page 1 extracted, the first row keeps both cells, the one of page 2
    /// empty and without /Pg (No. 682). Before, the row was left with one cell, the second with two.
    /// </summary>
    [Fact]
    public void ExtractPage_RowAcrossBothPages_CellOfTheOtherPageStaysEmpty()
    {
        var tagged = Tagged(new("TD", 5, Row: 0), new("TD", 7, Row: 0), new("TD", 5, Row: 1), new("TD", 5, Row: 1));
        var (doc, _) = Only(tagged, 0);
        using (doc)
        {
            var rows = All(doc).Where(e => e.GetName(S)!.Value == "TR").ToList();
            Assert.Equal([2, 2], rows.Select(r => r.Get(K) is PdfArray cells ? cells.Count : 1));
            var empty = Assert.Single(All(doc), e => e.GetName(S)!.Value == "TD" && !e.ContainsKey(K));
            Assert.False(empty.ContainsKey(PdfName.Get("Pg")));
        }
    }
}
