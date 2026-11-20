using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// A file of some pages (extracting, splitting) takes only what belongs to those pages (decided on 3.11.2026;
/// docs/FEHLERGEDAECHTNIS.md, No. 447, Analyse K-09).
/// </summary>
public sealed class PartOfDocumentTests
{
    private const string Xmp =
        "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
        "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmpMM=\"http://ns.adobe.com/xap/1.0/mm/\" " +
        "xmlns:stEvt=\"http://ns.adobe.com/xap/1.0/sType/ResourceEvent#\" xmpMM:DocumentID=\"uuid:GANZESDOKUMENT\">" +
        "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Titel bleibt</rdf:li></rdf:Alt></dc:title>" +
        "<xmpMM:InstanceID>uuid:INSTANZDESGANZEN</xmpMM:InstanceID>" +
        "<xmpMM:History><rdf:Seq><rdf:li rdf:parseType=\"Resource\"><stEvt:action>saved</stEvt:action><stEvt:changed>/VERLAUFSEITE7</stEvt:changed></rdf:li></rdf:Seq></xmpMM:History>" +
        "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    /// <summary>Two pages; the source of the whole document in the catalogue's /AF and in the name tree, another file only in the tree.</summary>
    private static PdfDocument Document(string xmp = Xmp)
    {
        var pdf = TestPdf.Simple(2);
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        int Spec(string name, string content)
        {
            var file = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("EmbeddedFile"))), System.Text.Encoding.ASCII.GetBytes(content)));
            return pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Filespec")), ("F", new PdfString(System.Text.Encoding.ASCII.GetBytes(name))),
                ("AFRelationship", TestPdf.N("Source")), ("EF", TestPdf.Dict(("F", TestPdf.Ref(file))))));
        }
        var source = Spec("ganz.docx", "QUELLEDESGANZEN");
        var other = Spec("anlage.txt", "ANLAGEDESGANZEN");
        catalog.Set(PdfName.Get("AF"), new PdfArray([TestPdf.Ref(source)]));
        var files = TestPdf.Dict(("Names", new PdfArray([
            new PdfString("anlage.txt"u8.ToArray()), TestPdf.Ref(other), new PdfString("ganz.docx"u8.ToArray()), TestPdf.Ref(source)])));
        var dests = TestPdf.Dict(("Names", new PdfArray([new PdfString("Ziel"u8.ToArray()), new PdfArray([TestPdf.Ref(5), TestPdf.N("Fit")])])));
        catalog.Set(PdfName.Get("Names"), TestPdf.Dict(("EmbeddedFiles", files), ("Dests", dests)));
        catalog.Set(PdfName.Get("PieceInfo"), TestPdf.Dict(("Zeichenprogramm", TestPdf.Dict(("Private", new PdfString("PRIVATDESGANZEN"u8.ToArray()))))));
        var meta = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("Metadata")), ("Subtype", TestPdf.N("XML"))), System.Text.Encoding.UTF8.GetBytes(xmp)));
        catalog.Set(PdfName.Get("Metadata"), TestPdf.Ref(meta));
        return PdfDocument.Open(pdf.Build());
    }

    private static PdfDocument Saved(PdfDocument doc, PdfSaveOptions options)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc).Skip(1).ToList(), output, options with { CompressStreams = false });
        return PdfDocument.Open(output.ToArray());
    }

    [Fact]
    public void Part_LeavesTheWholeBehind_KeepsTitleAndDestinations()
    {
        using var doc = Document();
        Assert.Equal(2, DocumentCleanup.DocumentAttachments(doc));
        using var part = Saved(doc, new PdfSaveOptions { PartOfDocument = true });
        var all = RedactionTests.Everything(part);
        Assert.DoesNotContain("QUELLEDESGANZEN", all);
        Assert.DoesNotContain("ANLAGEDESGANZEN", all);
        Assert.DoesNotContain("PRIVATDESGANZEN", all);
        Assert.DoesNotContain("GANZESDOKUMENT", all);
        Assert.DoesNotContain("INSTANZDESGANZEN", all);
        Assert.DoesNotContain("VERLAUFSEITE7", all);
        Assert.Contains("Titel bleibt", all);
        Assert.Contains("</x:xmpmeta>", all);
        Assert.NotNull(part.Catalog!.GetDictionary(PdfName.Get("Names"))?.GetDictionary(PdfName.Get("Dests")));
        Assert.Equal(1, part.PageCount);
    }

    [Fact]
    public void Part_WithTheAttachments_WhenTheUserSaysSo()
    {
        using var doc = Document();
        using var part = Saved(doc, new PdfSaveOptions { PartOfDocument = true, KeepDocumentAttachments = true });
        var all = RedactionTests.Everything(part);
        Assert.Contains("QUELLEDESGANZEN", all);
        Assert.Contains("ANLAGEDESGANZEN", all);
        Assert.DoesNotContain("VERLAUFSEITE7", all);
    }

    /// <summary>Saving the document itself (not a part) keeps all of it, as before.</summary>
    [Fact]
    public void WholeDocument_KeepsAttachmentsAndHistory()
    {
        using var doc = Document();
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { CompressStreams = false });
        using var saved = PdfDocument.Open(output.ToArray());
        var all = RedactionTests.Everything(saved);
        Assert.Contains("QUELLEDESGANZEN", all);
        Assert.Contains("VERLAUFSEITE7", all);
        Assert.Contains("PRIVATDESGANZEN", all);
    }

    /// <summary>A schema whose properties describe an attached file (registered as a module would), and another one of the document.</summary>
    private const string Described = "http://example.org/ns/beilage/", Own = "http://example.org/ns/eigen/";

    private static string Schema(string uri, string prefix, string property) =>
        "<rdf:li rdf:parseType=\"Resource\"><pdfaSchema:schema>" + prefix + "</pdfaSchema:schema><pdfaSchema:namespaceURI>" + uri +
        "</pdfaSchema:namespaceURI><pdfaSchema:prefix>" + prefix + "</pdfaSchema:prefix><pdfaSchema:property><rdf:Seq><rdf:li rdf:parseType=\"Resource\">" +
        "<pdfaProperty:name>" + property + "</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external" +
        "</pdfaProperty:category><pdfaProperty:description>-</pdfaProperty:description></rdf:li></rdf:Seq></pdfaSchema:property></rdf:li>";

    private static readonly string DescribingXmp =
        "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
        "<rdf:Description rdf:about=\"\" xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\" xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\" " +
        "xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\"><pdfaExtension:schemas><rdf:Bag>" + Schema(Described, "bl", "Dateiname") + Schema(Own, "ei", "Ablage") +
        "</rdf:Bag></pdfaExtension:schemas></rdf:Description>" +
        $"<rdf:Description rdf:about=\"\" xmlns:bl=\"{Described}\" xmlns:ei=\"{Own}\"><bl:Dateiname>ganz.docx</bl:Dateiname><ei:Ablage>Ordner 7</ei:Ablage></rdf:Description>" +
        "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    /// <summary>
    /// Where the attachments of the document go (a part without them, „Anhänge“ cleaned up), the XMP properties registered as describing
    /// attached files go too, with their extension schema — the metadata name no file that is gone (decided on 15.11.2026;
    /// docs/FEHLERGEDAECHTNIS.md, No. 865); the document's other properties stay. Where the attachments stay, all stays.
    /// </summary>
    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void AttachmentsGo_TheirXmpDataGoToo(bool part, bool keepAttachments, bool cleanAttachments, bool stays)
    {
        AttachedFileMetadata.Register([Described]);
        using var doc = Document(DescribingXmp);
        var options = new PdfSaveOptions
        {
            PartOfDocument = part, KeepDocumentAttachments = keepAttachments, Cleanup = cleanAttachments ? CleanupItems.Attachments : CleanupItems.None,
            CompressStreams = false,
        };
        using var output = new MemoryStream();
        PdfWriter.Write(doc, part ? PageItem.From(doc).Skip(1).ToList() : PageItem.From(doc), output, options);
        using var saved = PdfDocument.Open(output.ToArray());
        var xmp = System.Text.Encoding.UTF8.GetString(saved.GetStreamData((PdfStream)saved.Catalog!.Get(PdfName.Get("Metadata"))!));
        Assert.NotNull(PdfAXmp.Read(System.Text.Encoding.UTF8.GetBytes(xmp)));   // well-formed
        Assert.Equal(stays, xmp.Contains("ganz.docx", StringComparison.Ordinal));
        Assert.Equal(stays, xmp.Contains(Described, StringComparison.Ordinal));
        Assert.Contains("Ordner 7", xmp, StringComparison.Ordinal);
        Assert.Contains($"<pdfaSchema:namespaceURI>{Own}</pdfaSchema:namespaceURI>", xmp, StringComparison.Ordinal);
    }

    /// <summary>
    /// The data of a file that went leave a well-formed packet even if its RDF is not valid (No. 868, found by the fuzzer: the namespace of RDF
    /// misspelt, the invoice data stayed and were still found); the other data stay, the packet stays well-formed.
    /// </summary>
    [Fact]
    public void AttachmentsGo_TheirXmpDataGoToo_RdfMisspelt()
    {
        AttachedFileMetadata.Register([Described]);
        using var doc = Document(DescribingXmp.Replace("http://www.w3.org/1999/02/22-rdf-syntax-ns#", "http://www.w34272org/1999/02/22-rdf-syntax-ns#",
            StringComparison.Ordinal));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Cleanup = CleanupItems.Attachments, CompressStreams = false });
        using var saved = PdfDocument.Open(output.ToArray());
        var xmp = System.Text.Encoding.UTF8.GetString(saved.GetStreamData((PdfStream)saved.Catalog!.Get(PdfName.Get("Metadata"))!));
        Assert.NotNull(PdfAXmp.Read(System.Text.Encoding.UTF8.GetBytes(xmp)));   // well-formed
        Assert.DoesNotContain("ganz.docx", xmp, StringComparison.Ordinal);
        Assert.Contains("Ordner 7", xmp, StringComparison.Ordinal);
    }

    /// <summary>
    /// „Anhänge“ cleaned up with a file that stays (KONZEPT.md, section 5.24, decision (4) of 15.11.2026: an e-invoice is a row of its
    /// own): the file kept stays with its entry in the name tree and in the catalogue's /AF, the other goes; without kept files all go.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CleanedAttachments_AKeptFileStays(bool keep)
    {
        using var doc = Document();
        var spec = (PdfDictionary)doc.Catalog!.GetArray(PdfName.Get("AF"))!.Get(0)!;
        var stream = ((PdfReference)spec.GetDictionary(PdfName.Get("EF"))!.GetRaw(PdfName.Get("F"))!).Id;
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions
        {
            Cleanup = CleanupItems.Attachments, KeptAttachments = keep ? new HashSet<PdfObjectId> { stream } : null, CompressStreams = false,
        });
        using var saved = PdfDocument.Open(output.ToArray());
        var all = RedactionTests.Everything(saved);
        Assert.Equal(keep, all.Contains("QUELLEDESGANZEN", StringComparison.Ordinal));
        Assert.DoesNotContain("ANLAGEDESGANZEN", all);
        Assert.Equal(keep ? 1 : 0, saved.Catalog!.GetArray(PdfName.Get("AF"))?.Count ?? 0);
        var names = saved.Catalog.GetDictionary(PdfName.Get("Names"))?.GetDictionary(PdfName.Get("EmbeddedFiles"))?.GetArray(PdfName.Get("Names"));
        Assert.Equal(keep ? ["ganz.docx"] : [], names is null ? [] : Enumerable.Range(0, names.Count / 2).Select(i => ((PdfString)names.Get(2 * i)!).ToText()).ToArray());
        Assert.NotNull(saved.Catalog.GetDictionary(PdfName.Get("Names"))?.GetDictionary(PdfName.Get("Dests")));   // the rest of /Names stays
    }
}
