using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// An XMP packet nested thousands of levels deep (found by the analysis run of the e-invoice, 15.11.2026; docs/FEHLERGEDAECHTNIS.md,
/// No. 866): the reader refuses it, and nothing that walks the tree recursively — the PDF/A check, a PDF/A copy, cleaning up with
/// attached files kept — overflows the stack any more (a stack overflow ends the program, no handler catches it). Real packets stay readable.
/// </summary>
public sealed class XmpDepthTests
{
    private const string Described = "http://example.org/ns/tief/";

    private static string Deep(int depth) =>
        "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
        "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\"><pdfaid:part>3</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance></rdf:Description>" +
        $"<rdf:Description rdf:about=\"\" xmlns:t=\"{Described}\"><t:Wert>" + string.Concat(Enumerable.Repeat("<rdf:Bag><rdf:li>", depth)) + "x" +
        string.Concat(Enumerable.Repeat("</rdf:li></rdf:Bag>", depth)) + "</t:Wert></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    private static byte[] Pdf(string xmp) => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R/Metadata 7 0 R>>",
        [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<<>>>>",
        [6] = ("", "0 0 m 100 100 l S"),
        [7] = ("/Type/Metadata/Subtype/XML", xmp),
    });

    [Fact]
    public void DeepPacket_Refused_NothingOverflows_ShallowStaysReadable()
    {
        Assert.NotNull(PdfAXmp.Read(Encoding.UTF8.GetBytes(Deep(20))));                 // 45 levels: real packets are far shallower
        Assert.Null(PdfAXmp.Read(Encoding.UTF8.GetBytes(Deep(PdfAXmp.MaxDepth))));
        AttachedFileMetadata.Register([Described]);
        using var doc = PdfDocument.Open(Pdf(Deep(5000)));
        // the PDF/A check names it as unreadable XMP
        Assert.Contains(PdfACheck.Check(doc, PdfAConformance.A3b), v => v.Id == PdfARules.XmpWellFormed.Id(PdfAConformance.A3b));
        foreach (var options in new[]
                 {
                     new PdfSaveOptions { PdfA = PdfAConformance.A3b },
                     new PdfSaveOptions { Cleanup = CleanupItems.Metadata | CleanupItems.Attachments, AttachedFileMetadataStays = true },
                     new PdfSaveOptions { Cleanup = CleanupItems.Attachments },
                     new PdfSaveOptions(),
                 })
        {
            using var output = new MemoryStream();
            PdfWriter.Write(doc, PageItem.From(doc), output, options);
            using var saved = PdfDocument.Open(output.ToArray());
            Assert.Equal(1, saved.PageCount);
        }
    }
}
