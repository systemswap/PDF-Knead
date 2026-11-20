using System.Text;
using PdfKnead.EInvoice;
using PdfKnead.Tests;
using PdfKnead.Core.Pdf.Document;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// Where the PDF holds its invoice (KONZEPT.md, section 5.24, stage 1): the catalog's /AF, the name tree /EmbeddedFiles and file attachment
/// annotations, only embedded streams (never a file outside the PDF), each file once with every place it was found; the name tree walked
/// without recursion, loops and depth bounded; the XMP invoice data read with the hardened reader. Only invented invoices.
/// </summary>
public sealed class InvoiceSourcesTests
{
    private const string Xmp = """
        <?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
        <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
        <rdf:Description rdf:about="" xmlns:fx="urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#">
        <fx:DocumentType>INVOICE</fx:DocumentType><fx:DocumentFileName>factur-x.xml</fx:DocumentFileName>
        <fx:Version>1.0</fx:Version><fx:ConformanceLevel>EN 16931</fx:ConformanceLevel>
        </rdf:Description></rdf:RDF></x:xmpmeta>
        <?xpacket end="w"?>
        """;

    /// <summary>Content of a stream as the builder takes it (bytes carried in Latin-1).</summary>
    private static string Bytes(string text) => Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(text));

    private static PdfDocument Open(Dictionary<int, object> objects) => PdfDocument.Open(TestFiles.Build(objects));

    /// <summary>A Factur-X-like PDF: the invoice in /AF and in the name tree, a delivery note behind an annotation, an external file reference.</summary>
    private static Dictionary<int, object> Invoice(string xmp = Xmp) => new()
    {
        [1] = "<</Type/Catalog/Pages 2 0 R/AF[5 0 R]/Names<</EmbeddedFiles<</Names[(factur-x.xml) 5 0 R (extern.xml) 11 0 R]>>>>/Metadata 7 0 R>>",
        [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<<>>/Annots[8 0 R]>>",
        [4] = ("", "0 0 1 rg 10 10 20 20 re f"),
        [5] = "<</Type/Filespec/F(factur-x.xml)/UF(factur-x.xml)/AFRelationship/Alternative/EF<</F 6 0 R/UF 6 0 R>>>>",
        [6] = ("/Type/EmbeddedFile/Subtype/text#2Fxml", Bytes(InvoiceKindTests.Cii("urn:cen.eu:en16931:2017"))),
        [7] = ("/Type/Metadata/Subtype/XML", Bytes(xmp)),
        [8] = "<</Type/Annot/Subtype/FileAttachment/Rect[10 10 30 30]/FS 9 0 R>>",
        [9] = "<</Type/Filespec/F(lieferschein.txt)/EF<</F 10 0 R>>>>",
        [10] = ("/Type/EmbeddedFile", "Lieferschein 1"),
        // a file outside the PDF: never followed, never listed
        [11] = "<</Type/Filespec/F(http://127.0.0.1:1/rechnung.xml)/FS/URL>>",
    };

    [Fact]
    public void Files_EachOnce_WithEveryPlace()
    {
        using var doc = Open(Invoice());
        var search = InvoiceSources.Find(doc);
        Assert.Equal(2, search.Files.Count);
        var invoice = search.Files[0];
        Assert.Equal("factur-x.xml", invoice.DisplayName);
        Assert.Equal("Alternative", invoice.Relationship);
        Assert.Equal("text/xml", invoice.MimeType);
        Assert.Equal(new[] { (InvoiceFilePlace.CatalogAf, 0), (InvoiceFilePlace.EmbeddedFiles, 0) }, invoice.Places);
        var note = search.Files[1];
        Assert.Equal("lieferschein.txt", note.DisplayName);
        Assert.Equal(new[] { (InvoiceFilePlace.Annotation, 1) }, note.Places);
        Assert.Null(note.Relationship);
        Assert.Empty(search.Notes);
    }

    [Fact]
    public void Data_Read_And_Bounded()
    {
        using var doc = Open(Invoice());
        var invoice = InvoiceSources.Find(doc).Files[0];
        var data = InvoiceSources.Data(doc, invoice, InvoiceXmlLimits.Default.MaxBytes);
        Assert.NotNull(data);
        Assert.Equal(InvoiceProfile.En16931, InvoiceKind.Of(InvoiceXml.Read(data)).Profile);
        Assert.Null(InvoiceSources.Data(doc, invoice, 100));   // larger than allowed: not handed out
    }

    [Fact]
    public void Xmp_InvoiceData_Read()
    {
        using var doc = Open(Invoice());
        var xmp = InvoiceSources.Find(doc).Xmp;
        Assert.NotNull(xmp);
        Assert.Equal("urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#", xmp.Namespace);
        Assert.Equal("INVOICE", xmp.DocumentType);
        Assert.Equal("factur-x.xml", xmp.DocumentFileName);
        Assert.Equal("1.0", xmp.Version);
        Assert.Equal("EN 16931", xmp.ConformanceLevel);
    }

    [Fact]
    public void Xmp_WithAttack_Ignored_FilesStillFound()
    {
        var attack = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">]><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">&b;</x:xmpmeta>";
        using var doc = Open(Invoice(attack));
        var search = InvoiceSources.Find(doc);
        Assert.Null(search.Xmp);
        Assert.Equal(2, search.Files.Count);
    }

    [Fact]
    public void NoInvoice_NothingFound()
    {
        using var doc = PdfDocument.Open(TestFiles.MinimalPdfBytes());
        var search = InvoiceSources.Find(doc);
        Assert.Empty(search.Files);
        Assert.Null(search.Xmp);
    }

    [Fact]
    public void NameTree_Loop_Bounded()
    {
        var objects = Invoice();
        objects[1] = "<</Type/Catalog/Pages 2 0 R/Names<</EmbeddedFiles 12 0 R>>>>";
        objects[12] = "<</Kids[13 0 R]>>";
        objects[13] = "<</Kids[12 0 R]/Names[(factur-x.xml) 5 0 R]>>";
        using var doc = Open(objects);
        var search = InvoiceSources.Find(doc);
        Assert.NotEmpty(search.Notes);
        Assert.Contains(search.Files, f => f.DisplayName == "factur-x.xml");
    }

    [Fact]
    public void NameTree_TooDeep_RestPassedOver()
    {
        var objects = Invoice();
        objects[1] = "<</Type/Catalog/Pages 2 0 R/Names<</EmbeddedFiles 20 0 R>>>>";
        for (var i = 0; i < 40; i++) objects[20 + i] = $"<</Kids[{21 + i} 0 R]>>";
        objects[60] = "<</Names[(factur-x.xml) 5 0 R]>>";
        using var doc = Open(objects);
        var search = InvoiceSources.Find(doc);
        Assert.Contains(search.Notes, n => n.Contains("tiefer", StringComparison.Ordinal));
        Assert.DoesNotContain(search.Files, f => f.DisplayName == "factur-x.xml");
    }

    [Fact]
    public void NameTree_TooManyEntries_RestPassedOver()
    {
        var objects = Invoice();
        var pairs = string.Concat(Enumerable.Range(0, InvoiceSources.MaxEntries + 5).Select(i => $"(d{i}) 9 0 R "));
        objects[1] = $"<</Type/Catalog/Pages 2 0 R/Names<</EmbeddedFiles<</Names[{pairs}]>>>>>>";
        using var doc = Open(objects);
        var search = InvoiceSources.Find(doc);
        Assert.Contains(search.Notes, n => n.Contains("mehr als", StringComparison.Ordinal));
        var file = Assert.Single(search.Files, f => f.DisplayName == "lieferschein.txt");
        Assert.Equal(InvoiceSources.MaxEntries + 1, file.Places.Count);   // the entries taken plus the annotation
    }

    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void NameTree_SharedKids_VisitsBounded()
    {
        // 3 000 nodes, each with the same /Kids array of all 3 000: nine million visits from a file of some hundred kilobytes
        const int nodes = 3000;
        var objects = Invoice();
        objects[1] = "<</Type/Catalog/Pages 2 0 R/Names<</EmbeddedFiles 20 0 R>>>>";
        objects[20] = "<</Kids 21 0 R>>";
        objects[21] = "[" + string.Concat(Enumerable.Range(0, nodes).Select(i => $"{30 + i} 0 R ")) + "]";
        for (var i = 0; i < nodes; i++) objects[30 + i] = "<</Kids 21 0 R>>";
        using var doc = Open(objects);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var search = InvoiceSources.Find(doc);
        Assert.True(watch.ElapsedMilliseconds < 10_000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
        Assert.Contains(search.Notes, n => n.Contains("besucht", StringComparison.Ordinal));
        Assert.True(search.Notes.Count <= 4, string.Join("; ", search.Notes));   // each note once
    }
}
