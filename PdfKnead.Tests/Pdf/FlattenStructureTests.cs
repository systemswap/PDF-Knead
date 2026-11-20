using System.Text;
using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Flattening a tagged file (docs/ABLAUFPLAN.md, 6.4 step 1, D-B3 with G2; docs/FEHLERGEDAECHTNIS.md, No. 632): the appearance of an annotation
/// with an element goes into the content as marked content of that element (the object reference becomes its MCID, the key of the annotation
/// leaves the parent tree), a field gets the attribute PrintField with /TU as /Desc and never as /Alt, a comment that does not show its text
/// gets it as /Alt; one without an element becomes an element of its own (with /Contents) or an artifact. Before: drawn without marks, the
/// object references fetched the widgets as loose objects into the file, the pairs stayed.
/// </summary>
public sealed class FlattenStructureTests
{
    /// <summary>
    /// One page (3): a paragraph (8, MCID 0); a text field „Name“ with the value Max Mustermann and /TU (20, /StructParent 1) in a Form element
    /// (9); a note (21, /StructParent 2) in an Annot element (10); <paramref name="more"/> adds annotations to /Annots (objects from 30 on),
    /// <paramref name="change"/> changes objects.
    /// </summary>
    private static byte[] Tagged(string more = "", Dictionary<int, object>? extra = null, Action<Dictionary<int, object>>? change = null)
    {
        var objects = new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 6 0 R/MarkInfo<</Marked true>>/Lang(de-DE)/AcroForm<</Fields[20 0 R]>>>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = $"<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>/StructParents 0/Tabs/S/Annots[20 0 R 21 0 R{more}]>>",
            [4] = ("", "/P <</MCID 0>> BDC BT /F1 12 Tf 72 760 Td (Name:) Tj ET EMC"),
            [5] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
            [6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 3>>",
            [7] = "<</Type/StructElem/S/Document/P 6 0 R/K[8 0 R 9 0 R 10 0 R]>>",
            [8] = "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K 0>>",
            [9] = "<</Type/StructElem/S/Form/P 7 0 R/Pg 3 0 R/K<</Type/OBJR/Obj 20 0 R/Pg 3 0 R>>>>",
            [10] = "<</Type/StructElem/S/Annot/P 7 0 R/Pg 3 0 R/K<</Type/OBJR/Obj 21 0 R/Pg 3 0 R>>>>",
            [15] = "<</Nums[0[8 0 R] 1 9 0 R 2 10 0 R]>>",
            [20] = "<</Type/Annot/Subtype/Widget/FT/Tx/T(Name)/TU(Vor- und Nachname)/V(Max Mustermann)/Rect[120 750 320 770]/F 4/StructParent 1/P 3 0 R/AP<</N 22 0 R>>>>",
            [21] = "<</Type/Annot/Subtype/Text/Rect[400 750 420 770]/F 4/StructParent 2/Contents(Bitte pruefen)/P 3 0 R/AP<</N 23 0 R>>>>",
            [22] = ("/Type/XObject/Subtype/Form/BBox[0 0 200 20]/Resources<</Font<</F1 5 0 R>>>>", "/Tx BMC BT /F1 12 Tf 2 5 Td (Max Mustermann) Tj ET EMC"),
            [23] = ("/Type/XObject/Subtype/Form/BBox[0 0 20 20]", "1 1 0 rg 0 0 20 20 re f"),
        };
        foreach (var (number, value) in extra ?? []) objects[number] = value;
        change?.Invoke(objects);
        return CraftedFileTests.Build(objects);
    }

    private static PdfDocument Flatten(byte[] file, bool forPrint = false)
    {
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Flatten = true, ForPrint = forPrint });
        return PdfDocument.Open(output.ToArray());
    }

    private static string Content(PdfDocument doc)
    {
        using var content = PageInterpreter.PageContent(doc, doc.Pages[0].Dictionary);
        return Encoding.Latin1.GetString(content.Span);
    }

    /// <summary>The structure elements of a type, depth first.</summary>
    private static List<PdfDictionary> Elements(PdfDocument doc, string type)
    {
        var found = new List<PdfDictionary>();
        var stack = new Stack<PdfObject?>([doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.Get(PdfName.Get("K"))]);
        while (stack.TryPop(out var k))
            switch (k)
            {
                case PdfArray a:
                    for (var i = a.Count - 1; i >= 0; i--) stack.Push(a.Get(i));
                    break;
                case PdfDictionary e when e.GetName(PdfName.Get("S"))?.Value is { } s:
                    if (s == type) found.Add(e);
                    stack.Push(e.Get(PdfName.Get("K")));
                    break;
            }
        return found;
    }

    private static PdfDictionary Element(PdfDocument doc, string type) => Elements(doc, type).FirstOrDefault() ?? throw new InvalidOperationException(type + " fehlt");

    private static List<PdfObject> Kids(PdfDictionary element) => element.Get(PdfName.Get("K")) switch
    {
        PdfArray a => [.. Enumerable.Range(0, a.Count).Select(a.Get)],
        { } one => [one],
        null => [],
    };

    /// <summary>
    /// A text field: its appearance is marked content of its Form element in the page content, the object reference is gone and so is the
    /// widget, the element carries PrintField (role tv, /Desc from /TU) and no /Alt; the tree is clean.
    /// </summary>
    [Fact]
    public void TextField_FormElementGetsTheAppearanceAsMarkedContent()
    {
        using var flat = Flatten(Tagged());
        var form = Element(flat, "Form");
        var mcid = Assert.IsType<PdfInteger>(Assert.Single(Kids(form))).Value;
        Assert.Contains($"/Form <</MCID {mcid}>> BDC", Content(flat));
        var attributes = form.GetDictionary(PdfName.Get("A"))!;
        Assert.Equal("PrintField", attributes.GetName(PdfName.Get("O"))!.Value);
        Assert.Equal("tv", attributes.GetName(PdfName.Get("Role"))!.Value);
        Assert.Equal("Vor- und Nachname", attributes.GetString(PdfName.Get("Desc"))!.ToText());
        Assert.False(form.ContainsKey(PdfName.Get("Alt")));
        Assert.DoesNotContain(Enumerable.Range(1, flat.ObjectCount), n => flat.HasObject(n) && flat.GetObject(n) is PdfDictionary d && d.GetName(PdfName.Subtype)?.Value == "Widget");
        Assert.Contains("Max Mustermann", string.Concat(PageInterpreter.Build(flat, flat.Pages[0]).Text.Select(t => t.Text)));
        StructureAssert.Clean(flat);
    }

    /// <summary>A note that shows only its icon: its /Contents becomes /Alt of its Annot element; a free text shows its text and gets none.</summary>
    [Fact]
    public void Comment_ContentsBecomesAlt_FreeTextNot()
    {
        var free = new Dictionary<int, object>
        {
            [30] = "<</Type/Annot/Subtype/FreeText/Rect[72 600 300 620]/F 4/StructParent 3/Contents(Max Mustermann)/DA(/Helv 12 Tf 0 g)/P 3 0 R/AP<</N 31 0 R>>>>",
            [31] = ("/Type/XObject/Subtype/Form/BBox[0 0 228 20]/Resources<</Font<</F1 5 0 R>>>>", "BT /F1 12 Tf 2 5 Td (Max Mustermann) Tj ET"),
            [32] = "<</Type/StructElem/S/Annot/P 7 0 R/Pg 3 0 R/K<</Type/OBJR/Obj 30 0 R/Pg 3 0 R>>>>",
        };
        using var flat = Flatten(Tagged(" 30 0 R", free, o =>
        {
            o[7] = ((string)o[7]).Replace("/K[8 0 R 9 0 R 10 0 R]", "/K[8 0 R 9 0 R 10 0 R 32 0 R]", StringComparison.Ordinal);
            o[15] = ((string)o[15]).Replace("2 10 0 R]", "2 10 0 R 3 32 0 R]", StringComparison.Ordinal);
            o[6] = ((string)o[6]).Replace("/ParentTreeNextKey 3", "/ParentTreeNextKey 4", StringComparison.Ordinal);
        }));
        var annots = Elements(flat, "Annot");
        Assert.Equal("Bitte pruefen", annots[0].GetString(PdfName.Get("Alt"))!.ToText());
        Assert.False(annots[1].ContainsKey(PdfName.Get("Alt")));
        StructureAssert.Clean(flat);
    }

    /// <summary>
    /// An annotation without an element in a tagged file: with /Contents it becomes an element of its own (a Figure with /Alt, at a place the
    /// rules allow — not right under the root), without it an artifact.
    /// </summary>
    [Fact]
    public void WithoutElement_FigureOrArtifact()
    {
        var loose = new Dictionary<int, object>
        {
            [30] = "<</Type/Annot/Subtype/Square/Rect[72 500 172 550]/F 4/Contents(Markierung)/P 3 0 R/AP<</N 31 0 R>>>>",
            [31] = ("/Type/XObject/Subtype/Form/BBox[0 0 100 50]", "1 0 0 RG 0 0 100 50 re S"),
            [32] = "<</Type/Annot/Subtype/Square/Rect[72 400 172 450]/F 4/P 3 0 R/AP<</N 31 0 R>>>>",
        };
        using var flat = Flatten(Tagged(" 30 0 R 32 0 R", loose));
        var figure = Element(flat, "Figure");
        Assert.Equal("Markierung", figure.GetString(PdfName.Get("Alt"))!.ToText());
        Assert.Equal("Document", ((PdfDictionary)figure.Get(PdfName.Get("P"))!).GetName(PdfName.Get("S"))!.Value);
        Assert.Contains("/Artifact <</Type /Layout>> BDC", Content(flat));
        StructureAssert.Clean(flat);
    }

    /// <summary>
    /// A PDF/UA file flattened keeps its identification: the annotations are tagged content now, nothing broke (before: „Flachlegen“ was always
    /// a reason, the identification went). An untagged copy (structure cleaned up) still names the reason.
    /// </summary>
    [Fact]
    public void PdfUaFile_Flattened_KeepsTheIdentification()
    {
        const string xmp = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                           "<rdf:Description rdf:about=\"\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\"><pdfuaid:part>1</pdfuaid:part></rdf:Description>" +
                           "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        var file = Tagged(extra: new() { [40] = ("/Type/Metadata/Subtype/XML", xmp) },
            change: o => o[1] = ((string)o[1]).Replace("/Lang(de-DE)", "/Lang(de-DE)/Metadata 40 0 R", StringComparison.Ordinal));
        using var doc = PdfDocument.Open(file);
        foreach (var (cleanup, breaks) in new[] { (CleanupItems.None, false), (CleanupItems.Structure, true) })
        {
            using var output = new MemoryStream();
            var result = PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Flatten = true, Cleanup = cleanup });
            Assert.Equal(breaks, result.AccessibilityBreaks.Contains("Flachlegen"));
            Assert.Equal(breaks, result.AccessibilityClaimRemoved);
        }
    }

    /// <summary>For print, a field that is not printed is left out — its object reference and its key go from the tree (no loose widget).</summary>
    [Fact]
    public void ForPrint_HiddenField_LeavesTheTree()
    {
        using var flat = Flatten(Tagged(change: o => o[20] = ((string)o[20]).Replace("/F 4", "/F 2", StringComparison.Ordinal)), forPrint: true);
        Assert.DoesNotContain(Enumerable.Range(1, flat.ObjectCount), n => flat.HasObject(n) && flat.GetObject(n) is PdfDictionary d && d.GetName(PdfName.Subtype)?.Value == "Widget");
        Assert.Empty(TaggedContent.ParentTree(flat));
    }

    /// <summary>
    /// A hidden field flattened (docs/FEHLERGEDAECHTNIS.md, No. 673; found by the round trip of 6.8): it draws nothing and leaves the tree — and
    /// its Form element with it. Before, an empty Form stayed (ISO 14289-1, 7.18.4: one widget each), the identification with it.
    /// </summary>
    [Fact]
    public void HiddenField_Flattened_ItsEmptyFormGoes()
    {
        using var flat = Flatten(Tagged(change: o => o[20] = ((string)o[20]).Replace("/F 4", "/F 2", StringComparison.Ordinal)));
        Assert.Empty(Elements(flat, "Form"));
        Assert.Single(Elements(flat, "P"));
        Assert.Single(Elements(flat, "Annot"));
        Assert.DoesNotContain(AccessibilityCheck.Check(flat, AccessibilityProfile.PdfUa1).Findings, f => f.Id == "7.18.4-2");
    }

    /// <summary>
    /// A note of a type PDF/A does not know (/FREETEXT, as in the test files of PDF/UA-1) in a tagged file made PDF/A (docs/FEHLERGEDAECHTNIS.md,
    /// No. 678; found by the round trip of 6.8): it leaves the page and what it drew goes into the content — as marked content of its Annot
    /// element, as when flattening. Before, that content was untagged (ISO 14289-1, 7.1) and the identification stayed.
    /// </summary>
    [Fact]
    public void PdfA_UnknownAnnotationDrawnIntoItsElement()
    {
        var file = Tagged(change: o => o[21] = ((string)o[21]).Replace("/Subtype/Text", "/Subtype/FREETEXT", StringComparison.Ordinal));
        using var doc = PdfDocument.Open(file);
        var result = PdfAExport.Write(doc, PageItem.From(doc), PdfAConformance.A2b);
        using var copy = PdfDocument.Open(result.Bytes);
        Assert.DoesNotContain(Enumerable.Range(1, copy.ObjectCount), n => copy.HasObject(n) && copy.GetObject(n) is PdfDictionary d && d.GetName(PdfName.Subtype)?.Value == "FREETEXT");
        var annot = Element(copy, "Annot");
        Assert.Contains(Kids(annot), k => k is PdfInteger);
        Assert.DoesNotContain(AccessibilityCheck.Check(copy, AccessibilityProfile.PdfUa1).Findings, f => f.Id == "7.1-3");
    }
}
