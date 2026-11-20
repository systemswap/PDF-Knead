using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.EInvoice;
using PdfKnead.Tests;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Signatures;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// Finding the invoice of a PDF and the cross-checks of stage 1 (KONZEPT.md, section 5.24): the XML of an invoice syntax counts, several are
/// reported, and the revisions tell whether the XML came later — added
/// (a note: generators attach it in an update) or replaced (an „Auffälligkeit“), or outside the signed revision of a signed PDF. Only
/// invented invoices and names.
/// </summary>
public sealed class InvoiceLocatorTests
{
    private static readonly Lazy<SigningCertificate> Own = new(() =>
        SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"erechnung-{Guid.NewGuid():N}.p12")));

    internal static string Xmp(string level = "EN 16931", string name = "factur-x.xml", string type = "INVOICE",
        string ns = "urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#") => $"""
        <?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
        <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
        <rdf:Description rdf:about="" xmlns:fx="{ns}">
        <fx:DocumentType>{type}</fx:DocumentType><fx:DocumentFileName>{name}</fx:DocumentFileName>
        <fx:Version>1.0</fx:Version><fx:ConformanceLevel>{level}</fx:ConformanceLevel>
        </rdf:Description></rdf:RDF></x:xmpmeta>
        <?xpacket end="w"?>
        """;

    internal static string Bytes(string text) => Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(text));

    /// <summary>A Factur-X-like PDF with a delivery note behind an annotation; the parts can be exchanged.</summary>
    internal static Dictionary<int, object> Pdf(string? xmp = null, string catalogExtra = "/AF[5 0 R]/Names<</EmbeddedFiles<</Names[(factur-x.xml) 5 0 R]>>>>",
        string invoice = "urn:cen.eu:en16931:2017") => new()
    {
        [1] = $"<</Type/Catalog/Pages 2 0 R{catalogExtra}/Metadata 7 0 R>>",
        [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<<>>/Annots[8 0 R]>>",
        [4] = ("", "0 0 1 rg 10 10 20 20 re f"),
        [5] = "<</Type/Filespec/F(factur-x.xml)/UF(factur-x.xml)/AFRelationship/Alternative/EF<</F 6 0 R/UF 6 0 R>>>>",
        [6] = ("/Type/EmbeddedFile/Subtype/text#2Fxml", Bytes(InvoiceKindTests.Cii(invoice))),
        [7] = ("/Type/Metadata/Subtype/XML", Bytes(xmp ?? Xmp())),
        [8] = "<</Type/Annot/Subtype/FileAttachment/Rect[10 10 30 30]/FS 9 0 R>>",
        [9] = "<</Type/Filespec/F(lieferschein.txt)/EF<</F 10 0 R>>>>",
        [10] = ("/Type/EmbeddedFile", "Lieferschein 1"),
    };

    private static InvoiceLocation Locate(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return InvoiceLocator.Locate(doc);
    }

    private static InvoiceLocation Locate(Dictionary<int, object> objects) => Locate(TestFiles.Build(objects));

    /// <summary>Appends an incremental update with the given objects (a classic cross-reference section, /Prev to the last one).</summary>
    internal static byte[] Append(byte[] pdf, Dictionary<int, object> objects)
    {
        int size, root;
        using (var doc = PdfDocument.Open(pdf))
        {
            size = (int)(doc.Trailer.GetInteger(PdfName.Size) ?? 0);
            root = ((PdfReference)doc.Trailer.GetRaw(PdfName.Get("Root"))!).Id.Number;
        }
        var prev = Regex.Matches(Encoding.Latin1.GetString(pdf), @"startxref\s+(\d+)")[^1].Groups[1].Value;
        var ms = new MemoryStream();
        ms.Write(pdf);
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        if (pdf[^1] != '\n') W("\n");
        var offsets = new SortedDictionary<int, long>();
        foreach (var (n, o) in objects.OrderBy(k => k.Key))
        {
            offsets[n] = ms.Position;
            if (o is string s) W($"{n} 0 obj\n{s}\nendobj\n");
            else if (o is (string dict, string content))
            {
                var bytes = Encoding.Latin1.GetBytes(content);
                W($"{n} 0 obj\n<<{dict}/Length {bytes.Length}>>\nstream\n");
                ms.Write(bytes);
                W("\nendstream\nendobj\n");
            }
        }
        var xref = ms.Position;
        W("xref\n");
        foreach (var (n, offset) in offsets) W($"{n} 1\n{offset:D10} 00000 n \n");
        W($"trailer\n<</Size {Math.Max(size, offsets.Keys.Max() + 1)}/Root {root} 0 R/Prev {prev}>>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    // ------------------------------------------------------------ finding

    [Fact]
    public void FacturX_Found_NoFinding_DeliveryNoteAnAttachment()
    {
        var location = Locate(Pdf());
        Assert.Equal("factur-x.xml", location.File!.DisplayName);
        Assert.Equal(InvoiceProfile.En16931, location.Kind!.Profile);
        Assert.Equal("lieferschein.txt", Assert.Single(location.Attachments).DisplayName);
        Assert.Empty(location.OtherInvoices);
        Assert.Empty(location.Findings);
    }

    [Fact]
    public void NoInvoice_NothingFound()
    {
        var location = Locate(TestFiles.MinimalPdfBytes());
        Assert.False(location.HasInvoice);
        Assert.Empty(location.Findings);
    }

    [Fact]
    public void LooseXml_Read()
    {
        var location = InvoiceLocator.Read(Encoding.UTF8.GetBytes(InvoiceKindTests.Cii("urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0")));
        Assert.Equal(InvoiceProfile.XRechnung, location.Kind!.Profile);
        Assert.Empty(location.Findings);
        var refused = InvoiceLocator.Read("<!DOCTYPE r><r/>"u8.ToArray());
        Assert.Equal(InvoiceXmlRejection.Doctype, refused.Refused);
        Assert.Contains(refused.Findings, f => f.Kind == InvoiceFindingKind.Refused && f.Weight == InvoiceFindingWeight.Finding);
    }

    [Fact]
    public void SeveralInvoices_Reported_TheOneOfTheXmpShown()
    {
        var objects = Pdf(catalogExtra: "/AF[11 0 R 5 0 R]/Names<</EmbeddedFiles<</Names[(alt.xml) 11 0 R (factur-x.xml) 5 0 R]>>>>");
        objects[11] = "<</Type/Filespec/F(alt.xml)/AFRelationship/Alternative/EF<</F 12 0 R>>>>";
        objects[12] = ("/Type/EmbeddedFile", Bytes(InvoiceKindTests.Cii("urn:factur-x.eu:1p0:minimum")));
        var location = Locate(objects);
        Assert.Equal("factur-x.xml", location.File!.DisplayName);
        Assert.Equal("alt.xml", Assert.Single(location.OtherInvoices).DisplayName);
        Assert.Contains(location.Findings, f => f.Kind == InvoiceFindingKind.SeveralInvoices);
    }

    [Fact]
    public void RefusedXml_StaysTheInvoice_WithItsReason()
    {
        var objects = Pdf();
        objects[6] = ("/Type/EmbeddedFile", "<!DOCTYPE r [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><r>&x;</r>");
        var location = Locate(objects);
        Assert.Equal("factur-x.xml", location.File!.DisplayName);
        Assert.Equal(InvoiceXmlRejection.Doctype, location.Refused);
        Assert.Null(location.Xml);
        Assert.Contains(location.Findings, f => f.Kind == InvoiceFindingKind.Refused);
    }

    // how the invoice is embedded and what the XMP say about it: rules of the specification since stage 4, part h (InvoiceHybridRulesTests)

    [Fact]
    public void XmpPromiseAnInvoice_ThePdfHoldsNone_Reported()
    {
        // the file the XMP name is missing
        var missing = Pdf(catalogExtra: "");
        missing[3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<<>>>>";
        var finding = Assert.Single(Locate(missing).Findings);
        Assert.Equal((InvoiceFindingKind.XmpWithoutInvoice, InvoiceFindingWeight.Finding), (finding.Kind, finding.Weight));
        Assert.Contains("factur-x.xml", finding.Text);
        // a file of that name is there, but it is no invoice
        var other = Pdf();
        other[6] = ("/Type/EmbeddedFile/Subtype/text#2Fplain", "Liebe Grüße");
        var location = Locate(other);
        Assert.False(location.HasInvoice);
        Assert.Equal(InvoiceFindingKind.XmpWithoutInvoice, Assert.Single(location.Findings).Kind);
        // no invoice data in the XMP and no invoice: nothing to report
        var plain = Pdf(catalogExtra: "");
        plain[1] = "<</Type/Catalog/Pages 2 0 R>>";
        Assert.Empty(Locate(plain).Findings);
    }

    [Fact]
    public void ReaderNotes_BecomeFindings()
    {
        var objects = Pdf();
        objects[6] = ("/Type/EmbeddedFile", Bytes(InvoiceKindTests.Cii("urn:cen.eu:en16931:2017")
            .Replace("encoding=\"UTF-8\"", "encoding=\"ISO-8859-1\"").Replace("<rsm:ExchangedDocument>", "<rsm:ExchangedDocument xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:schemaLocation=\"urn:x http://127.0.0.1:1/x.xsd\">")));
        var findings = Locate(objects).Findings;
        Assert.Contains(findings, f => f.Kind == InvoiceFindingKind.DeclaredEncoding && f.Weight == InvoiceFindingWeight.Finding);
        Assert.Contains(findings, f => f.Kind == InvoiceFindingKind.ReferencesNotFollowed && f.Weight == InvoiceFindingWeight.Note);
    }

    // ------------------------------------------------------------ revisions

    [Fact]
    public void RevisionEnds_EarlierStatesOnly()
    {
        var first = TestFiles.Build(Pdf());
        var both = Append(first, new() { [4] = ("", "1 0 0 rg 10 10 20 20 re f") });
        using (var doc = PdfDocument.Open(both))
            Assert.Equal(new[] { first.LongLength }, InvoiceLocator.RevisionEnds(doc));
        using (var doc = PdfDocument.Open([.. first, .. "\n\n  \n"u8]))
            Assert.Empty(InvoiceLocator.RevisionEnds(doc));   // white space after the end: still one state
    }

    [Fact]
    public void Revision_UnrelatedChange_NoFinding()
    {
        var first = TestFiles.Build(Pdf());
        Assert.Empty(Locate(Append(first, new() { [4] = ("", "1 0 0 rg 10 10 20 20 re f") })).Findings);
    }

    [Fact]
    public void Revision_InvoiceAddedLater_Note()
    {
        var plain = Pdf(catalogExtra: "");
        plain.Remove(5);
        plain.Remove(6);
        var first = TestFiles.Build(plain);
        var added = Append(first, new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/AF[5 0 R]/Names<</EmbeddedFiles<</Names[(factur-x.xml) 5 0 R]>>>>/Metadata 7 0 R>>",
            [5] = Pdf()[5],
            [6] = Pdf()[6],
        });
        var finding = Assert.Single(Locate(added).Findings);
        Assert.Equal(InvoiceFindingKind.LaterRevision, finding.Kind);
        Assert.Equal(InvoiceFindingWeight.Note, finding.Weight);
    }

    [Fact]
    public void Revision_InvoiceReplacedLater_Finding()
    {
        var first = TestFiles.Build(Pdf());
        var replaced = Append(first, new() { [6] = ("/Type/EmbeddedFile/Subtype/text#2Fxml", Bytes(InvoiceKindTests.Cii("urn:cen.eu:en16931:2017").Replace("RE-2026-0001", "RE-2026-0002"))) });
        var finding = Assert.Single(Locate(replaced).Findings);
        Assert.Equal(InvoiceFindingKind.LaterRevision, finding.Kind);
        Assert.Equal(InvoiceFindingWeight.Finding, finding.Weight);
    }

    [Fact]
    public void Signed_InvoiceReplacedAfterSigning_OutsideTheSignature()
    {
        byte[] signed;
        using (var doc = PdfDocument.Open(TestFiles.Build(Pdf())))
        {
            var ink = new InkElement
            {
                Rect = new PdfRectangle(350, 100, 530, 160),
                Strokes = [[0, 20, 40, 50, 90, 10, 140, 45, 175, 25]],
                StrokeWidth = 3,
                Color = 0x1B3A8C,
                Digital = new DigitalSigning("Max Mustermann · 13.11.2026"),
            };
            signed = PdfSigner.Sign(doc, 0, ink, Own.Value, new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(1)));
        }
        Assert.DoesNotContain(Locate(signed).Findings, f => f.Kind is InvoiceFindingKind.OutsideSignature or InvoiceFindingKind.LaterRevision);
        var replaced = Append(signed, new() { [6] = ("/Type/EmbeddedFile/Subtype/text#2Fxml", Bytes(InvoiceKindTests.Cii("urn:cen.eu:en16931:2017").Replace("RE-2026-0001", "RE-2026-0002"))) });
        var findings = Locate(replaced).Findings;
        Assert.Contains(findings, f => f.Kind == InvoiceFindingKind.OutsideSignature && f.Weight == InvoiceFindingWeight.Finding);
        Assert.Contains(findings, f => f.Kind == InvoiceFindingKind.LaterRevision && f.Weight == InvoiceFindingWeight.Finding);
    }

    [Theory]
    [InlineData("MINIMUM", false, InvoiceProfile.Minimum)]
    [InlineData("basic wl", false, InvoiceProfile.BasicWl)]
    [InlineData("BASIC", true, InvoiceProfile.ZugferdV1Basic)]
    [InlineData("COMFORT", true, InvoiceProfile.ZugferdV1Comfort)]
    [InlineData("COMFORT", false, InvoiceProfile.En16931)]
    [InlineData(" EN  16931 ", false, InvoiceProfile.En16931)]
    [InlineData("XRECHNUNG", false, InvoiceProfile.XRechnung)]
    public void XmpLevels(string level, bool v1, InvoiceProfile profile) => Assert.Equal(profile, InvoiceLocator.LevelOf(level, v1));
}
