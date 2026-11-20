using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// A bar within a paragraph of a tagged file with the namespace for PDF 2.0, written as 2.0 (docs/ABLAUFPLAN.md, 6.3 step 6;
/// docs/FEHLERGEDAECHTNIS.md, No. 659): an artifact meaningful only in the context of the text — an Artifact structure element (/Type /Inline,
/// /Subtype /Redaction) in the paragraph, right after the marked content it lay in (ISO 14289-2, 8.3.2; ISO 32000-2, Table 385). In a tree of
/// PDF 1.7 never. Before: an artifact of pagination like any bar.
/// </summary>
public sealed class InlineBarTests
{
    private static readonly PdfName K = PdfName.Get("K"), S = PdfName.Get("S");

    /// <summary>A tagged page, P (MCID 0) „Max Mustermann wohnt in Musterstadt“, with the namespace for PDF 2.0 where <paramref name="pdf20"/>.</summary>
    private static byte[] Tagged(bool pdf20)
    {
        var pdf = Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Max Mustermann wohnt in Musterstadt) Tj ET EMC"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("StructParents"), PdfInteger.Of(0));
        var root = pdf.Add(new PdfDictionary());
        var document = pdf.Add(new PdfDictionary());
        var ns = pdf20 ? pdf.Add(Dict(("Type", N("Namespace")), ("NS", new PdfString("http://iso.org/pdf2/ssn"u8.ToArray())))) : 0;
        var paragraph = pdf.Add(Dict(("Type", N("StructElem")), ("S", N("P")), ("P", Ref(document)), ("Pg", Ref(5)), ("K", PdfInteger.Of(0))));
        var documentDict = Dict(("Type", N("StructElem")), ("S", N("Document")), ("P", Ref(root)), ("K", new PdfArray([Ref(paragraph)])));
        if (pdf20)
        {
            documentDict.Set(PdfName.Get("NS"), Ref(ns));
            ((PdfDictionary)pdf.Get(paragraph)).Set(PdfName.Get("NS"), Ref(ns));
        }
        pdf.Replace(document, documentDict);
        var rootDict = Dict(("Type", N("StructTreeRoot")), ("K", Ref(document)),
            ("ParentTree", Dict(("Nums", new PdfArray([PdfInteger.Of(0), new PdfArray([Ref(paragraph)])])))), ("ParentTreeNextKey", PdfInteger.Of(1)));
        if (pdf20) rootDict.Set(PdfName.Get("Namespaces"), new PdfArray([Ref(ns)]));
        pdf.Replace(root, rootDict);
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(PdfName.Get("StructTreeRoot"), Ref(root));
        catalog.Set(PdfName.Get("MarkInfo"), Dict(("Marked", PdfBoolean.True)));
        catalog.Set(PdfName.Get("Lang"), new PdfString("de"u8.ToArray()));
        return pdf.Build();
    }

    /// <summary>The page with a bar over „Mustermann“, written as <paramref name="version"/>.</summary>
    private static PdfDocument Redacted(byte[] data, string version)
    {
        using var doc = PdfDocument.Open(data);
        var text = Core.Pdf.Text.PageText.Create(PageInterpreter.Build(doc, doc.Pages[0]).Text);
        var bounds = Assert.Single(text.Quads(Assert.Single(text.Find("Mustermann")))).Bounds;
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([new RedactionElement { Rect = new PdfRectangle(bounds.Left - 0.5, bounds.Top - 0.5, bounds.Right + 0.5, bounds.Bottom + 0.5) }]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true, Version = version });
        return PdfDocument.Open(output.ToArray());
    }

    private static PdfDictionary Paragraph(PdfDocument doc)
    {
        var top = doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.Get(K) switch
        {
            PdfArray { Count: 1 } one => (PdfDictionary)one.Get(0)!,
            var single => (PdfDictionary)single!,
        };
        return top.Get(K) switch
        {
            PdfArray kids => (PdfDictionary)kids.Get(0)!,
            var only => (PdfDictionary)only!,
        };
    }

    private static string Content(PdfDocument doc)
    {
        using var data = PageInterpreter.PageContent(doc, doc.Pages[0].Dictionary);
        return Encoding.Latin1.GetString(data.Span);
    }

    [Fact]
    public void Pdf20Tagged_BarInsideParagraph_IsArtifactElement()
    {
        using var doc = Redacted(Tagged(pdf20: true), "2.0");
        var kids = Assert.IsType<PdfArray>(Paragraph(doc).Get(K));
        Assert.Equal(0L, Assert.IsType<PdfInteger>(kids.Get(0)).Value);
        var artifact = Assert.IsType<PdfDictionary>(kids.Get(1));
        Assert.Equal("Artifact", artifact.GetName(S)!.Value);
        Assert.NotNull(artifact.Get(PdfName.Get("NS")));
        var attributes = Assert.IsType<PdfDictionary>(artifact.Get(PdfName.Get("A")));
        Assert.Equal(["Artifact", "Inline", "Redaction"], new[] { "O", "Type", "Subtype" }.Select(k => attributes.GetName(PdfName.Get(k))!.Value));
        var mcid = Assert.IsType<PdfInteger>(artifact.Get(K) is PdfArray one ? one.Get(0) : artifact.Get(K)).Value;
        Assert.Matches($@"/Artifact\s*<<\s*/MCID\s+{mcid}\s*>>\s*BDC\s+q [01] g\s+[-\d. ]+ re\s+f\s+Q\s+EMC", Content(doc));
        var page = Assert.Single(Core.Pdf.Accessibility.TaggedContent.Analyse(doc).Pages);
        Assert.Equal(0, page.Untagged);
        Assert.Equal(0, page.McidsWithoutElement);
    }

    /// <summary>The same in a tree of PDF 1.7: the bar stays an artifact of pagination, the paragraph gets no Artifact element.</summary>
    [Fact]
    public void Pdf17Tree_BarInsideParagraph_StaysPagination()
    {
        using var doc = Redacted(Tagged(pdf20: false), "1.7");
        Assert.IsType<PdfInteger>(Paragraph(doc).Get(K) is PdfArray kids ? kids.Get(0) : Paragraph(doc).Get(K));
        Assert.DoesNotMatch(@"/Artifact\s*<<\s*/MCID", Content(doc));
        Assert.Contains("/Subtype /Redaction", Content(doc));
    }
}
