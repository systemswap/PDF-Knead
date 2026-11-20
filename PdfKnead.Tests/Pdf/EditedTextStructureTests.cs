using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Edited text in a tagged file (docs/ABLAUFPLAN.md, 6.6 i; docs/FEHLERGEDAECHTNIS.md, No. 656): the new text goes into the element whose
/// characters it took, at their place; its old bracket, left empty, becomes an artifact; reopened and saved again, nothing stacks; a new block
/// gets a paragraph of its own beside the others, never inside one. Before: the block was drawn without any marked content (untagged content,
/// and the element of the old text was left empty or went).
/// </summary>
public sealed class EditedTextStructureTests
{
    private static readonly PdfName K = PdfName.Get("K"), S = PdfName.Get("S");

    /// <summary>A tagged page: H1 „Titel“ (MCID 0), P „Alter Text im Absatz“ (MCID 1) under a Document — or the second element of another type, with /Alt.</summary>
    private static byte[] Tagged(string second = "P", string? alt = null)
    {
        var pdf = Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(
            "/H1 <</MCID 0>> BDC BT /F1 18 Tf 72 760 Td (Titel) Tj ET EMC /P <</MCID 1>> BDC BT /F1 12 Tf 72 700 Td (Alter Text im Absatz) Tj ET EMC")));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("StructParents"), PdfInteger.Of(0));
        var root = pdf.Add(new PdfDictionary());
        var document = pdf.Add(new PdfDictionary());
        var heading = pdf.Add(Dict(("Type", N("StructElem")), ("S", N("H1")), ("P", Ref(document)), ("Pg", Ref(5)), ("K", PdfInteger.Of(0))));
        var paragraph = pdf.Add(Dict(("Type", N("StructElem")), ("S", N(second)), ("P", Ref(document)), ("Pg", Ref(5)), ("K", PdfInteger.Of(1))));
        if (alt is not null) ((PdfDictionary)pdf.Get(paragraph)).Set(PdfName.Get("Alt"), new PdfString(Encoding.ASCII.GetBytes(alt)));
        pdf.Replace(document, Dict(("Type", N("StructElem")), ("S", N("Document")), ("P", Ref(root)), ("K", new PdfArray([Ref(heading), Ref(paragraph)]))));
        pdf.Replace(root, Dict(("Type", N("StructTreeRoot")), ("K", Ref(document)),
            ("ParentTree", Dict(("Nums", new PdfArray([PdfInteger.Of(0), new PdfArray([Ref(heading), Ref(paragraph)])])))), ("ParentTreeNextKey", PdfInteger.Of(1))));
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(PdfName.Get("StructTreeRoot"), Ref(root));
        catalog.Set(PdfName.Get("MarkInfo"), Dict(("Marked", PdfBoolean.True)));
        catalog.Set(PdfName.Get("Lang"), new PdfString("de"u8.ToArray()));
        return pdf.Build();
    }

    /// <summary>The block of <paramref name="text"/> edited to <paramref name="replacement"/> as the user interface does it, written.</summary>
    private static byte[] Edit(byte[] data, string text, string replacement)
    {
        using var doc = PdfDocument.Open(data);
        var page = PageItem.From(doc)[0];
        var block = Assert.Single(TextBlocks.Find(doc, page.Page), b => b.Text == text);
        var removed = Redactor.RemoveGlyphs(doc, page.Page, block.Glyphs.ToHashSet(), block.Paths.ToHashSet())!;
        var element = EditedText.FromBlock(doc, block);
        element = element with { Content = new RichText(replacement, element.Content.Spans[0].Style), ReplacesMcids = [.. removed.AlternateTextMcids.Order()] };
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [page.WithEditedPage(removed, [.. page.Elements, element])], output);
        return output.ToArray();
    }

    /// <summary>Opened again (the block taken out of the content as on opening) and its text changed once more, written.</summary>
    private static byte[] EditAgain(byte[] data, string replacement)
    {
        using var doc = PdfDocument.Open(data);
        var page = PageItem.From(doc)[0];
        var block = Assert.IsType<RichTextElement>(Assert.Single(page.Elements, e => e is RichTextElement));
        var changed = block with { Content = new RichText(replacement, block.Content.Spans[0].Style) };
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [page.WithElements([.. page.Elements.Where(e => e != block), changed])], output);
        return output.ToArray();
    }

    private static List<PdfDictionary> Kids(PdfDictionary element) => element.Get(K) switch
    {
        PdfArray a => [.. Enumerable.Range(0, a.Count).Select(a.Get).OfType<PdfDictionary>().Where(d => d.ContainsKey(S))],
        PdfDictionary one when one.ContainsKey(S) => [one],
        _ => [],
    };

    private static PdfDictionary Document(PdfDocument doc) => doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.Get(K) switch
    {
        PdfArray { Count: 1 } kids => Assert.IsType<PdfDictionary>(kids.Get(0)),
        var one => Assert.IsType<PdfDictionary>(one),
    };

    /// <summary>The MCIDs in an element's /K.</summary>
    private static List<long> Mcids(PdfDictionary element) => element.Get(K) switch
    {
        PdfInteger one => [one.Value],
        PdfArray a => [.. Enumerable.Range(0, a.Count).Select(a.Get).OfType<PdfInteger>().Select(i => i.Value)],
        _ => [],
    };

    private static string Content(PdfDocument doc)
    {
        using var data = Core.Pdf.Content.PageInterpreter.PageContent(doc, doc.Pages[0].Dictionary);
        return Encoding.Latin1.GetString(data.Span);
    }

    private static void NothingUntagged(PdfDocument doc)
    {
        var page = Assert.Single(Core.Pdf.Accessibility.TaggedContent.Analyse(doc).Pages);
        Assert.Equal(0, page.Untagged);
        Assert.Equal(0, page.McidsWithoutElement);
    }

    /// <summary>
    /// The paragraph edited: its element now holds the new marked content around the block's form (in place of the old MCID), the old bracket
    /// is an artifact, nothing is untagged, and the save names no break for it.
    /// </summary>
    [Fact]
    public void EditedBlockInTaggedPage_NewTextInTheOldElement()
    {
        using var doc = PdfDocument.Open(Edit(Tagged(), "Alter Text im Absatz", "Neuer Text im Absatz"));
        var kids = Kids(Document(doc));
        Assert.Equal(["H1", "P"], kids.Select(k => k.GetName(S)!.Value));
        var mcid = Assert.Single(Mcids(kids[1]));
        Assert.NotEqual(1, mcid);
        Assert.Matches($@"/P\s*<<\s*/MCID\s+{mcid}\s*>>\s*BDC[^E]*?Do", Content(doc));
        Assert.DoesNotMatch(@"/MCID\s+1\b", Content(doc));
        NothingUntagged(doc);
    }

    /// <summary>
    /// The text of a formula edited (docs/FEHLERGEDAECHTNIS.md, No. 676; found by the round trip of 6.8): its old /Alt (which could tell the old
    /// text) goes and a neutral one takes its place — before, the formula was left without (ISO 14289-1, 7.7); and where the element was
    /// refilled with the new text alone, the old /Alt stayed, the old text in it.
    /// </summary>
    [Fact]
    public void EditedFormula_OldAltGoes_NeutralOneInstead()
    {
        using var doc = PdfDocument.Open(Edit(Tagged("Formula", "Alter Text im Absatz"), "Alter Text im Absatz", "a + b = c"));
        var formula = Kids(Document(doc))[1];
        Assert.Equal("Formula", formula.GetName(S)!.Value);
        Assert.Equal("Bearbeiteter Inhalt", formula.GetString(PdfName.Get("Alt"))?.ToText());
        Assert.DoesNotContain(Core.Pdf.Accessibility.AccessibilityCheck.Check(doc, Core.Pdf.Accessibility.AccessibilityProfile.PdfUa1).Findings, f => f.Id == "7.7-1");
    }

    /// <summary>Opened again and edited once more: still one MCID in the paragraph, no empty bracket with an MCID left in the content.</summary>
    [Fact]
    public void EditedTwice_NoStackedMarkedContent()
    {
        var once = Edit(Tagged(), "Alter Text im Absatz", "Neuer Text im Absatz");
        using var doc = PdfDocument.Open(EditAgain(once, "Noch neuerer Text"));
        var paragraph = Kids(Document(doc))[1];
        var mcid = Assert.Single(Mcids(paragraph));
        Assert.Single(Regex.Matches(Content(doc), @"/MCID\s+\d+"), m => m.Value.EndsWith($" {mcid}", StringComparison.Ordinal));
        Assert.Equal(2, Regex.Matches(Content(doc), @"/MCID\s").Count);   // the heading's and the paragraph's, nothing stacked
        NothingUntagged(doc);
    }

    /// <summary>A new block below the paragraph: a P of its own beside it (a kid of the Document), never inside the paragraph.</summary>
    [Fact]
    public void TaggedPage_NewTextBelowParagraph_IsSiblingNotChild()
    {
        using var source = PdfDocument.Open(Tagged());
        var page = PageItem.From(source)[0];
        var block = new RichTextElement { Rect = new PdfRectangle(72, 600, 300, 630), Content = new RichText("Ganz neuer Absatz", new RichStyle { Family = "Liberation Sans", Size = 12 }) };
        using var output = new MemoryStream();
        PdfWriter.Write(source, [page.WithElements([block])], output);
        using var doc = PdfDocument.Open(output.ToArray());
        var kids = Kids(Document(doc));
        Assert.Equal(["H1", "P", "P"], kids.Select(k => k.GetName(S)!.Value));
        Assert.Empty(Kids(kids[1]));
        NothingUntagged(doc);
    }
}
