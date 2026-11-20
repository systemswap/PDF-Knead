using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// A structure tree with the namespace for PDF 2.0 exported to PDF/A-2a (based on PDF 1.7; docs/ABLAUFPLAN.md, 6.5 f, step 13;
/// docs/FEHLERGEDAECHTNIS.md, No. 645): the types written as PDF-Knead's own mapping says (Title → P, FENote → Note with an ID, H7 → H6), an
/// Artifact element as artifact content, no /NS and no /Namespaces. Before: H7 fell to P by chance, the Artifact element became an element
/// whose content was read out, the note had no ID, /NS and /Namespaces stayed in a file of PDF 1.7.
/// </summary>
public sealed class PdfAPdf20TreeTests
{
    /// <summary>
    /// One page: a heading H1, a Title, a paragraph, a line number marked as an Artifact element, a note FENote, a heading H7 — MCIDs 0 to 5,
    /// all elements in the namespace for PDF 2.0 under a Document.
    /// </summary>
    private static byte[] Pdf20Tree()
    {
        var pdf = Simple(1);
        var font = pdf.Add(Dict(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N("Helvetica")), ("Encoding", N("WinAnsiEncoding"))));
        string[] texts = ["Kapitel", "Titel des Berichts", "Ein Absatz.", "12", "Siehe Anhang.", "Unterpunkt"];
        string[] tags = ["H1", "Title", "P", "Span", "FENote", "H7"];
        var content = new StringBuilder();
        for (var i = 0; i < texts.Length; i++) content.Append($"/{tags[i]} <</MCID {i}>> BDC BT /T1 11 Tf 72 {740 - 20 * i} Td ({texts[i]}) Tj ET EMC ");
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(content.ToString())));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Resources, Dict(("Font", Dict(("T1", Ref(font))))));
        page.Set(PdfName.Get("StructParents"), PdfInteger.Of(0));
        var ns = pdf.Add(Dict(("Type", N("Namespace")), ("NS", new PdfString("http://iso.org/pdf2/ssn"u8.ToArray()))));
        var root = pdf.Add(new PdfDictionary());
        var document = pdf.Add(new PdfDictionary());
        string[] types = ["H1", "Title", "P", "Artifact", "FENote", "H7"];
        var leaves = types.Select((type, i) => pdf.Add(Dict(("Type", N("StructElem")), ("S", N(type)), ("NS", Ref(ns)), ("P", Ref(document)), ("Pg", Ref(5)),
            ("K", PdfInteger.Of(i))))).ToList();
        pdf.Replace(document, Dict(("Type", N("StructElem")), ("S", N("Document")), ("NS", Ref(ns)), ("P", Ref(root)), ("K", new PdfArray([.. leaves.Select(l => (PdfObject)Ref(l))]))));
        var parents = pdf.Add(Dict(("Nums", new PdfArray([PdfInteger.Of(0), new PdfArray([.. leaves.Select(l => (PdfObject)Ref(l))])]))));
        pdf.Replace(root, Dict(("Type", N("StructTreeRoot")), ("K", Ref(document)), ("ParentTree", Ref(parents)), ("ParentTreeNextKey", PdfInteger.Of(1)),
            ("Namespaces", new PdfArray([Ref(ns)]))));
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(PdfName.Get("StructTreeRoot"), Ref(root));
        catalog.Set(PdfName.Get("MarkInfo"), Dict(("Marked", PdfBoolean.True)));
        catalog.Set(PdfName.Get("Lang"), new PdfString("de"u8.ToArray()));
        return pdf.Build();
    }

    private static List<PdfDictionary> Kids(PdfDictionary element) => element.Get(PdfName.Get("K")) switch
    {
        PdfArray a => [.. Enumerable.Range(0, a.Count).Select(a.Get).OfType<PdfDictionary>()],
        PdfDictionary one => [one],
        _ => [],
    };

    [Fact]
    public void LevelA_Pdf20Tree_TypesMappedArtifactBecomesArtifactContent_NoteGetsAnId()
    {
        var result = PdfATests.Write(Pdf20Tree(), PdfAConformance.A2a);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        var tree = doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!;
        Assert.False(tree.ContainsKey(PdfName.Get("Namespaces")));
        var roles = tree.GetDictionary(PdfName.Get("RoleMap"))!;
        Assert.Equal("P", roles.GetName(PdfName.Get("Title"))?.Value);
        Assert.Equal("Note", roles.GetName(PdfName.Get("FENote"))?.Value);
        Assert.Equal("H6", roles.GetName(PdfName.Get("H7"))?.Value);
        Assert.False(roles.ContainsKey(PdfName.Get("Artifact")));

        // the Artifact element left the tree, its content is artifact content
        var document = (PdfDictionary)tree.Get(PdfName.Get("K"))!;
        var kids = Kids(document);
        Assert.Equal(["H1", "Title", "P", "FENote", "H7"], kids.Select(k => k.GetName(PdfName.Get("S"))!.Value));
        Assert.All(kids.Append(document), k => Assert.False(k.ContainsKey(PdfName.Get("NS"))));
        var page = doc.Pages[0];
        using var written = Core.Pdf.Content.PageInterpreter.PageContent(doc, page.Dictionary);
        var data = Encoding.Latin1.GetString(written.Span);
        Assert.Contains("/Artifact BMC", data);
        Assert.DoesNotContain("MCID 3", data);
        Assert.Contains("12", Core.Pdf.Text.PageText.Create(Core.Pdf.Content.PageInterpreter.Build(doc, page).Text).Text);   // still shown

        // the note has an ID, named in the /IDTree
        var note = kids[3];
        var id = Assert.IsType<PdfString>(note.Get(PdfName.Get("ID")));
        var pair = Assert.Single(NameTree.Pairs(tree.GetDictionary(PdfName.Get("IDTree"))!));
        Assert.Equal(id.Bytes, pair.Key);
        Assert.Same(note, doc.Resolve(((PdfReference)pair.Value).Id));
    }

    /// <summary>
    /// A tree with numbered headings (H1) and an own type named like a heading: it gets P, never H beside H1–H6 (H and Hn do not mix,
    /// ISO 14289-1, 7.4; 6.5 f). Before: H.
    /// </summary>
    [Fact]
    public void LevelA_NumberedHeadings_OwnHeadingTypeNeverH()
    {
        var pdf = Simple(1);
        var font = pdf.Add(Dict(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N("Helvetica")), ("Encoding", N("WinAnsiEncoding"))));
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(
            "/H1 <</MCID 0>> BDC BT /T1 11 Tf 72 720 Td (Kapitel) Tj ET EMC /Span <</MCID 1>> BDC BT /T1 11 Tf 72 700 Td (Abschnitt) Tj ET EMC")));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Resources, Dict(("Font", Dict(("T1", Ref(font))))));
        page.Set(PdfName.Get("StructParents"), PdfInteger.Of(0));
        var root = pdf.Add(new PdfDictionary());
        var document = pdf.Add(new PdfDictionary());
        var h1 = pdf.Add(Dict(("Type", N("StructElem")), ("S", N("H1")), ("P", Ref(document)), ("Pg", Ref(5)), ("K", PdfInteger.Of(0))));
        var own = pdf.Add(Dict(("Type", N("StructElem")), ("S", N("Ueberschrift")), ("P", Ref(document)), ("Pg", Ref(5)), ("K", PdfInteger.Of(1))));
        pdf.Replace(document, Dict(("Type", N("StructElem")), ("S", N("Document")), ("P", Ref(root)), ("K", new PdfArray([Ref(h1), Ref(own)]))));
        var parents = pdf.Add(Dict(("Nums", new PdfArray([PdfInteger.Of(0), new PdfArray([Ref(h1), Ref(own)])]))));
        pdf.Replace(root, Dict(("Type", N("StructTreeRoot")), ("K", Ref(document)), ("ParentTree", Ref(parents)), ("ParentTreeNextKey", PdfInteger.Of(1))));
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(PdfName.Get("StructTreeRoot"), Ref(root));
        catalog.Set(PdfName.Get("MarkInfo"), Dict(("Marked", PdfBoolean.True)));
        catalog.Set(PdfName.Get("Lang"), new PdfString("de"u8.ToArray()));

        var result = PdfATests.Write(pdf.Build(), PdfAConformance.A2a);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        var roles = doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.GetDictionary(PdfName.Get("RoleMap"))!;
        Assert.Equal("P", roles.GetName(PdfName.Get("Ueberschrift"))?.Value);
    }
}
