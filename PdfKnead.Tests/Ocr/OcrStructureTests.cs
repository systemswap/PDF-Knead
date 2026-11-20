using System.Text;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using Xunit;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Ocr;

/// <summary>
/// The text layer of the text recognition put over a page of a tagged file (docs/ABLAUFPLAN.md, 6.6 i, item 2; docs/FEHLERGEDAECHTNIS.md,
/// No. 657): a paragraph beside the page's Figure, which keeps its description; nothing untagged, no break named for it. Before: the layer
/// stood untagged, and the save named „neuer Inhalt ohne Struktur“.
/// </summary>
public sealed class OcrStructureTests
{
    private static readonly PdfName K = PdfName.Get("K"), S = PdfName.Get("S");

    /// <summary>A tagged scan: one Figure (MCID 0, „Gescannter Brief“) holding the image over the whole page.</summary>
    private static byte[] TaggedScan()
    {
        var pdf = Simple(1);
        var image = pdf.Add(new PdfStream(Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(4)), ("Height", PdfInteger.Of(4)),
            ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", N("DeviceGray"))), Enumerable.Repeat((byte)230, 16).ToArray()));
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "/Figure <</MCID 0>> BDC q 595.28 0 0 841.89 0 0 cm /Im Do Q EMC"u8.ToArray()));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Resources, Dict(("XObject", Dict(("Im", Ref(image))))));
        page.Set(PdfName.Get("StructParents"), PdfInteger.Of(0));
        var root = pdf.Add(new PdfDictionary());
        var document = pdf.Add(new PdfDictionary());
        var figure = pdf.Add(Dict(("Type", N("StructElem")), ("S", N("Figure")), ("P", Ref(document)), ("Pg", Ref(5)), ("K", PdfInteger.Of(0)),
            ("Alt", new PdfString("Gescannter Brief"u8.ToArray()))));
        pdf.Replace(document, Dict(("Type", N("StructElem")), ("S", N("Document")), ("P", Ref(root)), ("K", new PdfArray([Ref(figure)]))));
        pdf.Replace(root, Dict(("Type", N("StructTreeRoot")), ("K", Ref(document)),
            ("ParentTree", Dict(("Nums", new PdfArray([PdfInteger.Of(0), new PdfArray([Ref(figure)])])))), ("ParentTreeNextKey", PdfInteger.Of(1))));
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(PdfName.Get("StructTreeRoot"), Ref(root));
        catalog.Set(PdfName.Get("MarkInfo"), Dict(("Marked", PdfBoolean.True)));
        catalog.Set(PdfName.Get("Lang"), new PdfString("de"u8.ToArray()));
        return pdf.Build();
    }

    private static OcrWord Word(string text, double x, double y, double w, double h) =>
        new(text, [new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)]);

    [Fact]
    public void OcrLayer_TaggedPage_TextIsAParagraph()
    {
        using var source = PdfDocument.Open(TaggedScan());
        var layer = OcrTextLayer.Form([new OcrLine([Word("Sehr", 100, 100, 60, 20), Word("geehrte", 170, 100, 90, 20), Word("Erika", 270, 100, 70, 20)], 0.95)],
            new RectD(0, 0, 595.28, 841.89))!;
        using var output = new MemoryStream();
        var result = PdfWriter.Write(source, PageItem.From(source), output, new PdfSaveOptions { TextLayers = new Dictionary<int, PdfStream> { [0] = layer } });
        using var doc = PdfDocument.Open(output.ToArray());
        var document = doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.Get(K) switch
        {
            PdfArray { Count: 1 } one => Assert.IsType<PdfDictionary>(one.Get(0)),
            var single => Assert.IsType<PdfDictionary>(single),
        };
        var kids = Assert.IsType<PdfArray>(document.Get(K));
        var types = Enumerable.Range(0, kids.Count).Select(i => ((PdfDictionary)kids.Get(i)).GetName(S)!.Value).ToList();
        Assert.Equal(["Figure", "P"], types);
        Assert.Equal("Gescannter Brief", ((PdfDictionary)kids.Get(0)).GetString(PdfName.Get("Alt"))!.ToText());
        using (var content = Core.Pdf.Content.PageInterpreter.PageContent(doc, doc.Pages[0].Dictionary))
            Assert.Matches(@"/P\s*<<\s*/MCID\s+1\s*>>\s*BDC", Encoding.Latin1.GetString(content.Span));
        var page = Assert.Single(Core.Pdf.Accessibility.TaggedContent.Analyse(doc).Pages);
        Assert.Equal(0, page.Untagged);
        Assert.Empty(result.AccessibilityBreaks);
    }
}
