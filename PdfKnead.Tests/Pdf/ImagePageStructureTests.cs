using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// A redacted page of a tagged file saved „zusätzlich als Bild“ (docs/ABLAUFPLAN.md, 6.6 g; docs/FEHLERGEDAECHTNIS.md, No. 654): the elements of
/// the old page go as with a deleted page, the image becomes a Figure with a neutral /Alt at the place of the first element that went — never
/// inside a table row. Before: the elements stayed and named the image page with MCIDs it does not have, the image stood untagged.
/// </summary>
public sealed class ImagePageStructureTests
{
    private static readonly PdfName S = PdfName.Get("S"), K = PdfName.Get("K");

    /// <summary>
    /// Two tagged pages (5 and 7, one text line per element, MCIDs per page from 0) under a Document; <paramref name="top"/> lists the kids of
    /// the Document as (type, page) — „Table“ holds one row per following („TD“, page) entry until the next non-cell.
    /// </summary>
    private static byte[] Tagged(params (string Type, int Page)[] top) => Tagged(false, top);

    /// <param name="withLink">A text field on the second page (object 7): a widget in the form with /StructParent 2, a Form element with an
    /// object reference to it under the Document, key 2 in the parent tree.</param>
    private static byte[] Tagged(bool withLink, params (string Type, int Page)[] top)
    {
        var pdf = Simple(2);
        var root = pdf.Add(new PdfDictionary());
        var document = pdf.Add(new PdfDictionary());
        var content = new Dictionary<int, StringBuilder> { [5] = new(), [7] = new() };
        var byPage = new Dictionary<int, PdfArray> { [5] = new(), [7] = new() };
        var kids = new List<PdfObject>();
        int? table = null;
        var rows = new PdfArray();
        foreach (var (type, page) in top)
        {
            if (type == "Table")
            {
                table = pdf.Add(new PdfDictionary());
                kids.Add(Ref(table.Value));
                continue;
            }
            var cell = type == "TD" && table is not null;
            var mcid = byPage[page].Count;
            content[page].Append($"/{type} <</MCID {mcid}>> BDC BT /F1 12 Tf 72 {700 - 20 * mcid} Td (Zeile {mcid}) Tj ET EMC ");
            var row = cell ? pdf.Add(new PdfDictionary()) : 0;
            var element = pdf.Add(Dict(("Type", N("StructElem")), ("S", N(type)), ("P", Ref(cell ? row : document)), ("Pg", Ref(page)), ("K", PdfInteger.Of(mcid))));
            byPage[page].Add(Ref(element));
            if (cell)
            {
                pdf.Replace(row, Dict(("Type", N("StructElem")), ("S", N("TR")), ("P", Ref(table!.Value)), ("K", new PdfArray([Ref(element)]))));
                rows.Add(Ref(row));
            }
            else kids.Add(Ref(element));
        }
        if (table is { } t) pdf.Replace(t, Dict(("Type", N("StructElem")), ("S", N("Table")), ("P", Ref(document)), ("K", rows)));
        foreach (var page in new[] { 5, 7 })
        {
            pdf.Replace(page - 1, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(content[page].ToString())));
            ((PdfDictionary)pdf.Get(page)).Set(PdfName.Get("StructParents"), PdfInteger.Of(page == 5 ? 0 : 1));
        }
        var nums = new PdfArray([PdfInteger.Of(0), byPage[5], PdfInteger.Of(1), byPage[7]]);
        if (withLink)
        {
            // a text field (widget in the form) with its Form element, as in the reference form where the finding came from
            var annot = pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("Widget")), ("FT", N("Tx")), ("T", new PdfString("Name"u8.ToArray())),
                ("TU", new PdfString("Name"u8.ToArray())), ("Rect", Box(72, 500, 200, 520)), ("F", PdfInteger.Of(4)), ("P", Ref(7)), ("StructParent", PdfInteger.Of(2))));
            ((PdfDictionary)pdf.Get(7)).Set(PdfName.Get("Annots"), new PdfArray([Ref(annot)]));
            ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), Dict(("Fields", new PdfArray([Ref(annot)]))));
            var linkElement = pdf.Add(Dict(("Type", N("StructElem")), ("S", N("Form")), ("P", Ref(document)), ("Pg", Ref(7)),
                ("K", Dict(("Type", N("OBJR")), ("Obj", Ref(annot)), ("Pg", Ref(7))))));
            kids.Add(Ref(linkElement));
            nums.Add(PdfInteger.Of(2));
            nums.Add(Ref(linkElement));
        }
        pdf.Replace(document, Dict(("Type", N("StructElem")), ("S", N("Document")), ("P", Ref(root)), ("K", new PdfArray(kids))));
        pdf.Replace(root, Dict(("Type", N("StructTreeRoot")), ("K", Ref(document)),
            ("ParentTree", Dict(("Nums", nums))), ("ParentTreeNextKey", PdfInteger.Of(withLink ? 3 : 2))));
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(PdfName.Get("StructTreeRoot"), Ref(root));
        catalog.Set(PdfName.Get("MarkInfo"), Dict(("Marked", PdfBoolean.True)));
        catalog.Set(PdfName.Get("Lang"), new PdfString("de"u8.ToArray()));
        return pdf.Build();
    }

    /// <summary>The tagged file with its second page (or all pages) replaced by an image page (as the redaction does it), written and opened again.</summary>
    private static PdfDocument SecondPageAsImage(byte[] tagged, bool all = false)
    {
        using var doc = PdfDocument.Open(tagged);
        var image = Simple(2, null);
        image.Replace(4, new PdfStream(new PdfDictionary(), "q 0 g 72 72 400 600 re f Q"u8.ToArray()));
        image.Replace(6, new PdfStream(new PdfDictionary(), "q 0 g 72 72 400 600 re f Q"u8.ToArray()));
        using var imageDoc = PdfDocument.Open(image.Build());
        var pages = PageItem.From(doc);
        for (var i = all ? 0 : 1; i < 2; i++) pages[i] = new PageItem(imageDoc, imageDoc.Pages[i]).StandingFor(pages[i]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, pages, output);
        return PdfDocument.Open(output.ToArray());
    }

    private static PdfDictionary Document(PdfDocument doc) =>
        doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.Get(K) switch
        {
            PdfArray { Count: 1 } kids => Assert.IsType<PdfDictionary>(kids.Get(0)),
            var one => Assert.IsType<PdfDictionary>(one),
        };

    private static List<PdfDictionary> Kids(PdfDictionary element) => element.Get(K) switch
    {
        PdfArray a => [.. Enumerable.Range(0, a.Count).Select(a.Get).OfType<PdfDictionary>().Where(d => d.ContainsKey(S))],
        PdfDictionary one when one.ContainsKey(S) => [one],
        _ => [],
    };

    private static string Content(PdfDocument doc, int page)
    {
        using var data = Core.Pdf.Content.PageInterpreter.PageContent(doc, doc.Pages[page].Dictionary);
        return Encoding.Latin1.GetString(data.Span);
    }

    /// <summary>
    /// The elements of the second page go; the image is a Figure „Seite 2 als Bild (geschwärzt)“ where the first of them stood (between the
    /// heading and the paragraph of page 1), its content marked, the page with a key whose array names the Figure.
    /// </summary>
    [Fact]
    public void AdditionallyAsImage_TaggedPage_ElementsGo_ImageIsAFigure()
    {
        using var doc = SecondPageAsImage(Tagged(("H1", 5), ("P", 7), ("P", 7), ("P", 5)));
        var kids = Kids(Document(doc));
        Assert.Equal(["H1", "Figure", "P"], kids.Select(k => k.GetName(S)!.Value));
        var figure = kids[1];
        Assert.Equal("Seite 2 als Bild (geschwärzt)", figure.GetString(PdfName.Get("Alt"))!.ToText());
        Assert.Equal(0L, (figure.Get(K) is PdfArray one ? Assert.Single(one) : figure.Get(K)) is PdfInteger mcid ? mcid.Value : -1);
        Assert.Matches(@"^/Figure <</MCID 0>> BDC\s", Content(doc, 1));
        Assert.EndsWith("EMC", Content(doc, 1).TrimEnd());
        var page = Assert.Single(Core.Pdf.Accessibility.TaggedContent.Analyse(doc).Pages, p => p.Page == 1);
        Assert.Equal(0, page.Untagged);
        Assert.Equal(0, page.McidsWithoutElement);
    }

    /// <summary>
    /// A tagged second page with a text field (its Form element with an object reference, /StructParent 2) replaced by its image
    /// (docs/FEHLERGEDAECHTNIS.md, No. 693; found by the run of all ways to write, „Schwärzen als Bild“): the parent tree keeps no pair whose
    /// value is null — neither the field's (it is not on the image page) nor the old page's. Before, both stayed and named nothing.
    /// </summary>
    [Fact]
    public void AdditionallyAsImage_PageWithALink_ParentTreeWithoutNull()
    {
        using var doc = SecondPageAsImage(Tagged(withLink: true, ("H1", 5), ("P", 7)), all: true);
        var nums = doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.GetDictionary(PdfName.Get("ParentTree"))!.GetArray(PdfName.Get("Nums"))!;
        for (var i = 1; i < nums.Count; i += 2)
        {
            var value = nums.Get(i);
            Assert.False(value is null or PdfNull, $"Schlüssel {nums.Get(i - 1)} ohne Wert");
            if (value is PdfArray array) Assert.False(Enumerable.Range(0, array.Count).All(k => array.Get(k) is null or PdfNull), $"Schlüssel {nums.Get(i - 1)} nennt nichts");
        }
        Assert.DoesNotContain(Enumerable.Range(0, nums.Count / 2).Select(k => nums.Get(2 * k)), key => key is PdfInteger { Value: 2 });
    }

    /// <summary>
    /// All pages saved as images: the tree stays with a Document holding one Figure per page, in page order. In the first draft the tree went
    /// as a whole (nothing of the old content was left) — a measurement over short files found it.
    /// </summary>
    [Fact]
    public void AdditionallyAsImage_AllPages_TreeKeepsTheFigures()
    {
        using var doc = SecondPageAsImage(Tagged(("H1", 5), ("P", 7)), all: true);
        var top = Document(doc);
        Assert.Equal("Document", top.GetName(S)!.Value);
        var kids = Kids(top);
        Assert.Equal(["Seite 1 als Bild (geschwärzt)", "Seite 2 als Bild (geschwärzt)"], kids.Select(k => k.GetString(PdfName.Get("Alt"))!.ToText()));
        Assert.True(doc.Catalog!.GetDictionary(PdfName.Get("MarkInfo"))?.GetBoolean(PdfName.Get("Marked")) == true);
    }

    /// <summary>The row of a table on the replaced page goes; the Figure goes beside the table, never into a row or the table itself.</summary>
    [Fact]
    public void AdditionallyAsImage_TablePage_FigureNotInTr()
    {
        using var doc = SecondPageAsImage(Tagged(("Table", 0), ("TD", 5), ("TD", 7), ("P", 5)));
        var kids = Kids(Document(doc));
        Assert.Equal(["Table", "Figure", "P"], kids.Select(k => k.GetName(S)!.Value));
        Assert.Single(Kids(kids[0]));
    }
}
