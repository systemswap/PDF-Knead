using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// „Bereinigen: Struktur“ (docs/ABLAUFPLAN.md, 6.6 c; docs/FEHLERGEDAECHTNIS.md, No. 650): besides the tree, the replacement texts and MCIDs
/// of the property lists in the content go — written into the page, named in /Properties, in a form. Before: they stayed, with the name they
/// held (privacy); the PDF/UA identification goes with the reason „Struktur bereinigt“.
/// </summary>
public sealed class StructureCleanupTests
{
    /// <summary>
    /// A tagged page: „M. M.“ shown with /ActualText „Max Mustermann“ written into the content, and a form drawing „E. M.“ with the named list
    /// MC0 holding /ActualText „Erika Mustermann“.
    /// </summary>
    private static byte[] WithReplacementTexts(bool bookmark = false)
    {
        var pdf = TestPdf.Simple(1);
        var form = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 595, 842)),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))),
                ("Properties", TestPdf.Dict(("MC0", TestPdf.Dict(("ActualText", new PdfString("Erika Mustermann"u8.ToArray())), ("MCID", PdfInteger.Of(0))))))))),
            "/Span /MC0 BDC BT /F1 12 Tf 72 650 Td (E. M.) Tj ET EMC"u8.ToArray()));
        pdf.Replace(4, new PdfStream(new PdfDictionary(),
            "/Span <</ActualText (Max Mustermann) /MCID 0>> BDC BT /F1 12 Tf 72 700 Td (M. M.) Tj ET EMC q /Fm Do Q"u8.ToArray()));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))), ("XObject", TestPdf.Dict(("Fm", TestPdf.Ref(form))))));
        page.Set(PdfName.Get("StructParents"), PdfInteger.Of(0));
        var root = pdf.Add(new PdfDictionary());
        var span = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("Span")), ("P", TestPdf.Ref(root)), ("Pg", TestPdf.Ref(5)), ("K", PdfInteger.Of(0))));
        pdf.Replace(root, TestPdf.Dict(("Type", TestPdf.N("StructTreeRoot")), ("K", TestPdf.Ref(span)),
            ("ParentTree", TestPdf.Dict(("Nums", new PdfArray([PdfInteger.Of(0), new PdfArray([TestPdf.Ref(span)])])))), ("ParentTreeNextKey", PdfInteger.Of(1))));
        var catalog = (PdfDictionary)pdf.Get(1);
        catalog.Set(PdfName.Get("StructTreeRoot"), TestPdf.Ref(root));
        catalog.Set(PdfName.Get("MarkInfo"), TestPdf.Dict(("Marked", PdfBoolean.True)));
        if (bookmark)
        {
            // a bookmark naming the span as its structure element (/SE)
            var outlines = pdf.Add(new PdfDictionary());
            var item = pdf.Add(TestPdf.Dict(("Title", new PdfString("Kapitel"u8.ToArray())), ("Parent", TestPdf.Ref(outlines)),
                ("Dest", new PdfArray([TestPdf.Ref(5), TestPdf.N("Fit")])), ("SE", TestPdf.Ref(span))));
            pdf.Replace(outlines, TestPdf.Dict(("Type", TestPdf.N("Outlines")), ("First", TestPdf.Ref(item)), ("Last", TestPdf.Ref(item)), ("Count", PdfInteger.Of(1))));
            catalog.Set(PdfName.Get("Outlines"), TestPdf.Ref(outlines));
        }
        return pdf.Build();
    }

    [Fact]
    public void Structure_ReplacementTextsInTheContentGoToo_TextStays()
    {
        using var doc = PdfDocument.Open(WithReplacementTexts());
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Cleanup = CleanupItems.Structure });
        var data = output.ToArray();
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(data, ["Mustermann"], null, out _));
        using var saved = PdfDocument.Open(data);
        Assert.False(saved.Catalog!.ContainsKey(PdfName.Get("StructTreeRoot")));
        var text = Core.Pdf.Text.PageText.Create(Core.Pdf.Content.PageInterpreter.Build(saved, saved.Pages[0]).Text).Text;
        Assert.Contains("M. M.", text);
        Assert.Contains("E. M.", text);
        using var content = Core.Pdf.Content.PageInterpreter.PageContent(saved, saved.Pages[0].Dictionary);
        Assert.DoesNotContain("MCID", Encoding.Latin1.GetString(content.Span));
        Assert.Contains("Struktur bereinigt", result.AccessibilityBreaks);
    }

    /// <summary>
    /// A bookmark naming the span as its structure element (/SE, ISO 32000-2, Table 151): with the structure cleaned up, /SE goes — before it
    /// wrote the whole tree back into the file through the bookmark, with the replacement texts of its elements (found with the measurement:
    /// a reference file of PDF/UA kept its tree that way).
    /// </summary>
    [Fact]
    public void Structure_BookmarkStructureElementGoes_TreeNotWrittenBack()
    {
        using var doc = PdfDocument.Open(WithReplacementTexts(bookmark: true));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Cleanup = CleanupItems.Structure, CompressStreams = false, ObjectStreams = false });
        var written = Encoding.Latin1.GetString(output.ToArray());
        Assert.DoesNotContain("StructElem", written);
        Assert.DoesNotContain("/SE", written);
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(output.ToArray(), ["Mustermann"], null, out _));
    }
}
