using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// A form with marked content of its own (/StructParents) rewritten by a redaction (docs/ABLAUFPLAN.md, 6.6 a; docs/FEHLERGEDAECHTNIS.md,
/// No. 134, 647): the marked-content reference (MCR) naming the form names the copy the page now draws; a form invoked twice gets two copies,
/// one of them keeps the key, the other's marked content is artifact content. Before: /Stm became null — „in the page's content“ (ISO 32000-2,
/// 14.7.5.2) —, and with MCID 0 in the page too the element took foreign content; both copies kept the same key.
/// </summary>
public sealed class FormCopyStructureTests
{
    /// <summary>
    /// A page drawing form F0 (key 1: „Max Mustermann Strasse 1“ as MCID 0 of a paragraph by MCR) — <paramref name="twice"/>: a second time,
    /// 100 pt lower; <paramref name="pageText"/>: MCID 0 in the page content too (another paragraph, key 0).
    /// </summary>
    private static byte[] FormPage(bool twice = false, bool pageText = false)
    {
        var pdf = TestPdf.Simple(1);
        var form = pdf.Add(TestPdf.FlateStream(Encoding.ASCII.GetBytes("/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Max Mustermann Strasse 1) Tj ET EMC"),
            TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 595, 842)),
                ("StructParents", PdfInteger.Of(1)), ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))))))));
        var content = (pageText ? "/P <</MCID 0>> BDC BT /F1 12 Tf 72 760 Td (Seitentext) Tj ET EMC " : "") + "q /F0 Do Q" +
                      (twice ? " q 1 0 0 1 0 -100 cm /F0 Do Q" : "");
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(content)));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))), ("XObject", TestPdf.Dict(("F0", TestPdf.Ref(form))))));
        if (pageText) page.Set(PdfName.Get("StructParents"), PdfInteger.Of(0));
        var root = pdf.Add(new PdfDictionary());
        var document = pdf.Add(new PdfDictionary());
        var inForm = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("P")), ("P", TestPdf.Ref(document)), ("Pg", TestPdf.Ref(5)),
            ("K", TestPdf.Dict(("Type", TestPdf.N("MCR")), ("Stm", TestPdf.Ref(form)), ("MCID", PdfInteger.Of(0))))));
        var kids = new List<PdfObject> { TestPdf.Ref(inForm) };
        var pageArray = new PdfArray();
        if (pageText)
        {
            var inPage = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("P")), ("P", TestPdf.Ref(document)), ("Pg", TestPdf.Ref(5)),
                ("K", PdfInteger.Of(0))));
            kids.Insert(0, TestPdf.Ref(inPage));
            pageArray.Add(TestPdf.Ref(inPage));
        }
        pdf.Replace(document, TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("Document")), ("P", TestPdf.Ref(root)), ("K", new PdfArray(kids))));
        var parents = TestPdf.Dict(("Nums", pageText
            ? new PdfArray([PdfInteger.Of(0), pageArray, PdfInteger.Of(1), new PdfArray([TestPdf.Ref(inForm)])])
            : new PdfArray([PdfInteger.Of(1), new PdfArray([TestPdf.Ref(inForm)])])));
        pdf.Replace(root, TestPdf.Dict(("Type", TestPdf.N("StructTreeRoot")), ("K", TestPdf.Ref(document)), ("ParentTree", parents),
            ("ParentTreeNextKey", PdfInteger.Of(2))));
        var catalog = (PdfDictionary)pdf.Get(1);
        catalog.Set(PdfName.Get("StructTreeRoot"), TestPdf.Ref(root));
        catalog.Set(PdfName.Get("MarkInfo"), TestPdf.Dict(("Marked", PdfBoolean.True)));
        return pdf.Build();
    }

    /// <summary>The forms the page draws (its /XObject entries that are forms).</summary>
    private static List<PdfStream> FormsOf(PdfDocument doc)
    {
        var xobjects = doc.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!;
        return [.. xobjects.Entries.Select(e => xobjects.Get(e.Key)).OfType<PdfStream>().Where(s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "Form")];
    }

    /// <summary>The paragraph whose content lies in the form (its /K is an MCR).</summary>
    private static PdfDictionary FormParagraph(PdfDocument doc)
    {
        var document = (PdfDictionary)doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.Get(PdfName.Get("K"))!;
        var kids = (PdfArray)document.Get(PdfName.Get("K"))!;
        return Enumerable.Range(0, kids.Count).Select(kids.Get).OfType<PdfDictionary>().Single(e => e.Get(PdfName.Get("K")) is PdfDictionary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RedactedFormWithMarkedContent_McrPointsToTheCopy(bool pageText)
    {
        using var doc = PdfDocument.Open(FormPage(pageText: pageText));
        using var saved = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(180, 695, 330, 715))));
        var copy = Assert.Single(FormsOf(saved));
        var reference = (PdfDictionary)FormParagraph(saved).Get(PdfName.Get("K"))!;
        Assert.Same(copy, reference.Get(PdfName.Get("Stm")));
        Assert.Equal(1, copy.Dictionary.GetInteger(PdfName.Get("StructParents")));
        Assert.Empty(Core.Pdf.Accessibility.TaggedContent.ParentTree(saved));
    }

    [Fact]
    public void FormCalledTwice_OnlyOneCopyKeepsItsStructParents_TheOtherIsArtifactContent()
    {
        using var doc = PdfDocument.Open(FormPage(twice: true));
        using var saved = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(180, 590, 330, 715))));
        var forms = FormsOf(saved);
        Assert.Equal(2, forms.Count);
        var keyed = Assert.Single(forms, f => f.Dictionary.ContainsKey(PdfName.Get("StructParents")));
        var other = Assert.Single(forms, f => !f.Dictionary.ContainsKey(PdfName.Get("StructParents")));
        Assert.Same(keyed, ((PdfDictionary)FormParagraph(saved).Get(PdfName.Get("K"))!).Get(PdfName.Get("Stm")));
        var data = Encoding.Latin1.GetString(saved.GetStreamData(other));
        Assert.Contains("/Artifact BMC", data);
        Assert.DoesNotContain("MCID", data);
    }

    // ------------------------------------------------------------ 6.6 a part 2: edited forms (No. 648)

    /// <summary>
    /// A page drawing form „Logo“ (key 0) with <paramref name="formContent"/>; one element <paramref name="type"/> with the replacement
    /// <paramref name="alternate"/> (as /Alt for a Figure, else /ActualText) holds its MCID 0 by an MCR.
    /// </summary>
    private static byte[] TaggedForm(string formContent, string type, string alternate)
    {
        var pdf = TestPdf.Simple(1);
        var form = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 595, 842)),
            ("StructParents", PdfInteger.Of(0)), ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3))))))), Encoding.ASCII.GetBytes(formContent)));
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "q /Logo Do Q"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))), ("XObject", TestPdf.Dict(("Logo", TestPdf.Ref(form))))));
        var root = pdf.Add(new PdfDictionary());
        var element = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N(type)), ("P", TestPdf.Ref(root)), ("Pg", TestPdf.Ref(5)),
            (type == "Figure" ? "Alt" : "ActualText", new PdfString(Encoding.ASCII.GetBytes(alternate))),
            ("K", TestPdf.Dict(("Type", TestPdf.N("MCR")), ("Stm", TestPdf.Ref(form)), ("MCID", PdfInteger.Of(0))))));
        pdf.Replace(root, TestPdf.Dict(("Type", TestPdf.N("StructTreeRoot")), ("K", TestPdf.Ref(element)),
            ("ParentTree", TestPdf.Dict(("Nums", new PdfArray([PdfInteger.Of(0), new PdfArray([TestPdf.Ref(element)])])))), ("ParentTreeNextKey", PdfInteger.Of(1))));
        var catalog = (PdfDictionary)pdf.Get(1);
        catalog.Set(PdfName.Get("StructTreeRoot"), TestPdf.Ref(root));
        catalog.Set(PdfName.Get("MarkInfo"), TestPdf.Dict(("Marked", PdfBoolean.True)));
        return pdf.Build();
    }

    private static byte[] Save(PdfDocument doc, IReadOnlyList<Core.Pdf.Editing.PageItem> items, Core.Pdf.Writing.CleanupItems cleanup = default)
    {
        using var output = new MemoryStream();
        Core.Pdf.Writing.PdfWriter.Write(doc, items, output, new Core.Pdf.Writing.PdfSaveOptions { Cleanup = cleanup, CompressStreams = false, ObjectStreams = false });
        return output.ToArray();
    }

    /// <summary>
    /// A drawing in a form moved with „Bilder bearbeiten“: the form is rewritten, no character leaves it — the Figure keeps its /Alt, its MCR
    /// names the copy. Before: every rewritten form counted as edited text, the /Alt went.
    /// </summary>
    [Fact]
    public void DrawingInAFormEdited_FigureKeepsItsAlt()
    {
        using var doc = PdfDocument.Open(TaggedForm("/Figure <</MCID 0>> BDC 0.8 0 0 rg 100 600 40 20 re f EMC", "Figure", "Firmenlogo"));
        var items = Core.Pdf.Editing.PageItem.From(doc);
        var all = Core.Pdf.Editing.ContentObjectEditor.ObjectsOf(items[0]);
        items[0] = Core.Pdf.Editing.ContentObjectEditor.Transform(items[0], [all[0].Key], Core.Pdf.Graphics.Matrix.Translate(0, -100));
        using var saved = PdfDocument.Open(Save(doc, items));
        var figure = (PdfDictionary)saved.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.Get(PdfName.Get("K"))!;
        Assert.Equal("Firmenlogo", figure.GetString(PdfName.Get("Alt"))?.ToText());
        Assert.Same(Assert.Single(FormsOf(saved)), ((PdfDictionary)figure.Get(PdfName.Get("K"))!).Get(PdfName.Get("Stm")));
    }

    /// <summary>
    /// Hidden text „Max“ removed from a form that still shows „Max“ (the same character codes): characters left the form, the replacement text
    /// with the name goes — counted by the bytes shown, not by the codes still used (which would have kept it).
    /// </summary>
    [Fact]
    public void HiddenTextRemovedFromAForm_SameCodesStayVisible_ReplacementTextGoes()
    {
        using var doc = PdfDocument.Open(TaggedForm(
            "BT /F1 12 Tf 72 700 Td (Max) Tj ET /P <</MCID 0>> BDC BT 3 Tr /F1 10 Tf 72 600 Td (Max) Tj ET EMC", "P", "Max Mustermann"));
        var data = Save(doc, Core.Pdf.Editing.PageItem.From(doc), Core.Pdf.Writing.CleanupItems.InvisibleText);
        Assert.DoesNotContain("Mustermann", Encoding.Latin1.GetString(data));
    }
}
