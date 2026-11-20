using PdfKnead.Core.Pdf.Document;
using PdfKnead.EInvoice;
using PdfKnead.Tests;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// The rules for hybrid documents (stage 4, part h; ZUGFeRD 2.5.2 / Factur-X 1.09.2): PDF/A, the extension schema and the values of the XMP,
/// the embedded file (name, relationship, type), the profile against the countries. Built on the PDFs of <see cref="InvoiceLocatorTests"/>;
/// only invented invoices and names. These PDFs are no real PDF/A, so BR-HYBRID-02 is looked at on its own.
/// </summary>
public sealed class InvoiceHybridRulesTests
{
    private const string FacturX = InvoiceSources.FacturXNamespace;
    private const string En16931 = "urn:cen.eu:en16931:2017";
    private const string XRechnung = "urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0";
    private const string Minimum = "urn:factur-x.eu:1p0:minimum";

    /// <summary>An invoice of a profile between a seller and a buyer of the given countries, with or without a date of supply.</summary>
    internal static string Cii(string guideline = En16931, string seller = "DE", string buyer = "DE", string type = "380", bool delivery = true) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <rsm:CrossIndustryInvoice xmlns:rsm="urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100" xmlns:ram="urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100" xmlns:udt="urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100">
          <rsm:ExchangedDocumentContext><ram:GuidelineSpecifiedDocumentContextParameter><ram:ID>{guideline}</ram:ID></ram:GuidelineSpecifiedDocumentContextParameter></rsm:ExchangedDocumentContext>
          <rsm:ExchangedDocument><ram:ID>RE-2026-0001</ram:ID><ram:TypeCode>{type}</ram:TypeCode></rsm:ExchangedDocument>
          <rsm:SupplyChainTradeTransaction>
            <ram:ApplicableHeaderTradeAgreement>
              <ram:SellerTradeParty><ram:Name>Muster GmbH</ram:Name><ram:PostalTradeAddress><ram:CountryID>{seller}</ram:CountryID></ram:PostalTradeAddress></ram:SellerTradeParty>
              <ram:BuyerTradeParty><ram:Name>Beispiel AG</ram:Name><ram:PostalTradeAddress><ram:CountryID>{buyer}</ram:CountryID></ram:PostalTradeAddress></ram:BuyerTradeParty>
            </ram:ApplicableHeaderTradeAgreement>
            <ram:ApplicableHeaderTradeDelivery>{(delivery ? "<ram:ActualDeliverySupplyChainEvent><ram:OccurrenceDateTime><udt:DateTimeString format=\"102\">20261115</udt:DateTimeString></ram:OccurrenceDateTime></ram:ActualDeliverySupplyChainEvent>" : "")}</ram:ApplicableHeaderTradeDelivery>
          </rsm:SupplyChainTradeTransaction>
        </rsm:CrossIndustryInvoice>
        """;

    /// <summary>XMP with the PDF/A identification, the extension schema (null: none) and the invoice data.</summary>
    internal static string Xmp(string level = "EN 16931", string name = "factur-x.xml", string type = "INVOICE", string version = "1.0", string ns = FacturX,
        string prefix = "fx", string? schemaUri = FacturX, string schemaPrefix = "fx", string[]? properties = null)
    {
        var props = string.Concat((properties ?? ["DocumentType", "DocumentFileName", "Version", "ConformanceLevel"]).Select(p =>
            $"""<rdf:li rdf:parseType="Resource"><pdfaProperty:name>{p}</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>-</pdfaProperty:description></rdf:li>"""));
        var schema = schemaUri is null ? "" : $"""
            <rdf:Description rdf:about="" xmlns:pdfaExtension="http://www.aiim.org/pdfa/ns/extension/" xmlns:pdfaSchema="http://www.aiim.org/pdfa/ns/schema#" xmlns:pdfaProperty="http://www.aiim.org/pdfa/ns/property#">
            <pdfaExtension:schemas><rdf:Bag><rdf:li rdf:parseType="Resource"><pdfaSchema:schema>Rechnungsangaben</pdfaSchema:schema>
            <pdfaSchema:namespaceURI>{schemaUri}</pdfaSchema:namespaceURI><pdfaSchema:prefix>{schemaPrefix}</pdfaSchema:prefix>
            <pdfaSchema:property><rdf:Seq>{props}</rdf:Seq></pdfaSchema:property></rdf:li></rdf:Bag></pdfaExtension:schemas></rdf:Description>
            """;
        return $"""
            <?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description rdf:about="" xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/"><pdfaid:part>3</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance></rdf:Description>
            {schema}
            <rdf:Description rdf:about="" xmlns:{prefix}="{ns}">
            <{prefix}:DocumentType>{type}</{prefix}:DocumentType><{prefix}:DocumentFileName>{name}</{prefix}:DocumentFileName>
            <{prefix}:Version>{version}</{prefix}:Version><{prefix}:ConformanceLevel>{level}</{prefix}:ConformanceLevel>
            </rdf:Description></rdf:RDF></x:xmpmeta>
            <?xpacket end="w"?>
            """;
    }

    /// <summary>The PDF of the locator tests with this invoice and XMP, the file /Params /ModDate, under the given name and relationship.</summary>
    internal static Dictionary<int, object> Pdf(string? xml = null, string? xmp = null, string name = "factur-x.xml", string relationship = "/Alternative",
        string subtype = "/Subtype/text#2Fxml")
    {
        var objects = InvoiceLocatorTests.Pdf(xmp ?? Xmp(), $"/AF[5 0 R]/Names<</EmbeddedFiles<</Names[({name}) 5 0 R]>>>>");
        objects[5] = $"<</Type/Filespec/F({name})/UF({name}){(relationship.Length > 0 ? "/AFRelationship" + relationship : "")}/EF<</F 6 0 R/UF 6 0 R>>>>";
        objects[6] = ($"/Type/EmbeddedFile{subtype}/Params<</ModDate(D:20261115120000+01'00')>>", InvoiceLocatorTests.Bytes(xml ?? Cii()));
        return objects;
    }

    private static List<InvoiceRuleViolation> Check(Dictionary<int, object> objects)
    {
        using var doc = PdfDocument.Open(TestFiles.Build(objects));
        var location = InvoiceLocator.Locate(doc);
        var data = InvoiceReader.Read(location.Xml!, location.Kind!)!;
        return [.. InvoiceHybridRules.Check(doc, location, data)];
    }

    /// <summary>The identifiers found, without BR-HYBRID-02 (these PDFs are no real PDF/A).</summary>
    private static List<string> Ids(Dictionary<int, object> objects) =>
        [.. Check(objects).Where(v => v.Rule.Id != "BR-HYBRID-02").Select(v => v.Rule.Id)];

    [Fact]
    public void AsTheSpecificationAsks_NothingButPdfA()
    {
        Assert.Empty(Ids(Pdf()));
        // PDF/A-3b declared: the PDF/A check of the core runs and names its deviations; seller and buyer in Germany: a warning (BR-FX-DE-03)
        var pdfA = Assert.Single(Check(Pdf()), v => v.Rule.Id == "BR-HYBRID-02");
        Assert.StartsWith("PDF/A-3b: ", pdfA.Value);
        Assert.Equal(InvoiceRuleSeverity.Warning, pdfA.Rule.Severity);
        Assert.Equal(InvoiceRuleSeverity.Fatal, Assert.Single(Check(Pdf(Cii(seller: "FR", buyer: "FR"))), v => v.Rule.Id == "BR-HYBRID-02").Rule.Severity);
        // without any PDF/A identification
        var objects = Pdf();
        objects[7] = ("/Type/Metadata/Subtype/XML", InvoiceLocatorTests.Bytes(Xmp().Replace("<pdfaid:part>3</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance>", "")));
        Assert.Equal(Tr.T("kein PDF/A erklärt"), Assert.Single(Check(objects), v => v.Rule.Id == "BR-HYBRID-02").Value);
    }

    [Theory]
    [InlineData("BASIC", "factur-x.xml", "INVOICE", "1.0", "BR-HYBRID-15")]
    [InlineData("EN16931", "factur-x.xml", "INVOICE", "1.0", "BR-HYBRID-07")]
    [InlineData("BASIC_WL", "factur-x.xml", "INVOICE", "1.0", "BR-HYBRID-07")]
    [InlineData("EN 16931", "factur-x.xml", "ORDER", "1.0", "BR-HYBRID-06")]
    [InlineData("EN 16931", "factur-x.xml", "RECHNUNG", "1.0", "BR-HYBRID-06")]
    [InlineData("EN 16931", "factur-x.xml", "INVOICE", "2p1", "BR-HYBRID-10")]
    [InlineData("EN 16931", "factur-x.xml", "INVOICE", "3.0", "BR-HYBRID-09")]
    [InlineData("XRECHNUNG", "factur-x.xml", "INVOICE", "1.0", "BR-HYBRID-08,BR-HYBRID-15")]
    public void XmpValues_AgainstTheCodeListsAndTheXml(string level, string name, string type, string version, string expected)
    {
        Assert.Equal(expected.Split(','), Ids(Pdf(xmp: Xmp(level, name, type, version))));
    }

    [Fact]
    public void FileNames_FromTheCodeList_TheXmpNamesTheSame()
    {
        // another name: not in the code list, and not the one the XMP names
        Assert.Equal(["BR-HYBRID-13", "BR-HYBRID-14"], Ids(Pdf(name: "rechnung.xml")).Order(StringComparer.Ordinal));
        // XRechnung: xrechnung.xml with the level XRECHNUNG; the XRechnung version is what chapter 8 asks for (not 1.0)
        Assert.Empty(Ids(Pdf(Cii(XRechnung), Xmp("XRECHNUNG", "xrechnung.xml", version: "3.0"), "xrechnung.xml")));
        Assert.Equal(["BR-HYBRID-08", "BR-HYBRID-13"], Ids(Pdf(Cii(XRechnung), Xmp("XRECHNUNG", "factur-x.xml", version: "3.0"))));
    }

    [Fact]
    public void ExtensionSchema_Missing_OtherNamespace_OtherPrefix_PropertyMissing()
    {
        Assert.Equal(["BR-HYBRID-03"], Ids(Pdf(xmp: Xmp(schemaUri: null))));
        // the spelling of the rule table (without „:invoice“) is not the namespace of chapter 6 and of the sample file
        var uri = Assert.Single(Check(Pdf(xmp: Xmp(schemaUri: "urn:factur-x:pdfa:CrossIndustryDocument:1p0#"))), v => v.Rule.Id == "BR-HYBRID-04");
        Assert.Equal("urn:factur-x:pdfa:CrossIndustryDocument:1p0#", uri.Value);
        Assert.Equal(["BR-HYBRID-05"], Ids(Pdf(xmp: Xmp(schemaPrefix: "zf"))));
        var property = Assert.Single(Check(Pdf(xmp: Xmp(properties: ["DocumentType", "DocumentFileName", "ConformanceLevel"]))), v => v.Rule.Id == "BR-HYBRID-03");
        Assert.Equal(Tr.T("fehlt: {0}", "Version"), property.Value);
        // seller and buyer in Germany: the extension schema too only a warning (BR-FX-DE-03); elsewhere fatal
        Assert.Equal(InvoiceRuleSeverity.Warning, Check(Pdf(xmp: Xmp(schemaUri: null))).Single(v => v.Rule.Id == "BR-HYBRID-03").Rule.Severity);
        Assert.Equal(InvoiceRuleSeverity.Fatal, Check(Pdf(Cii(seller: "AT"), Xmp(schemaUri: null))).Single(v => v.Rule.Id == "BR-HYBRID-03").Rule.Severity);
    }

    [Fact]
    public void XmpMissing_EveryValueRuleSaysSo()
    {
        var objects = Pdf();
        objects[1] = "<</Type/Catalog/Pages 2 0 R/AF[5 0 R]/Names<</EmbeddedFiles<</Names[(factur-x.xml) 5 0 R]>>>>>>";
        Assert.Equal(["BR-HYBRID-03", "BR-HYBRID-06", "BR-HYBRID-07", "BR-HYBRID-08", "BR-HYBRID-09"], Ids(objects).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Relationship_AfterTheTable_FranceAllowsSourceAndData()
    {
        Assert.Equal(["BR-HYBRID-11"], Ids(Pdf(relationship: "/Data")));
        Assert.Equal(["BR-HYBRID-11"], Ids(Pdf(relationship: "")));
        Assert.Empty(Ids(Pdf(Cii(seller: "FR", buyer: "FR"), relationship: "/Source")));
        // MINIMUM: Data — between seller and buyer in Germany the profile itself is not allowed (BR-HYBRID-DE-01)
        Assert.Equal(["BR-HYBRID-DE-01"], Ids(Pdf(Cii(Minimum), Xmp("MINIMUM"), relationship: "/Data")));
        Assert.Empty(Ids(Pdf(Cii(Minimum, seller: "AT", buyer: "DE"), Xmp("MINIMUM"), relationship: "/Data")));
    }

    [Fact]
    public void Embedding_AfMissing_MimeType_ModDate()
    {
        var objects = Pdf();
        objects[1] = "<</Type/Catalog/Pages 2 0 R/Names<</EmbeddedFiles<</Names[(factur-x.xml) 5 0 R]>>>>/Metadata 7 0 R>>";
        var embedding = Assert.Single(Check(objects), v => v.Rule.Id == "BR-HYBRID-12");
        Assert.Equal(Tr.T("fehlt: {0}", Tr.T("/AF des Katalogs")), embedding.Value);
        Assert.Equal(["PDFK-PDF-01"], Ids(Pdf(subtype: "/Subtype/application#2Fxml")));
        Assert.Equal(["BR-HYBRID-12"], Ids(Pdf(subtype: "")));
        var noDate = Pdf();
        noDate[6] = ("/Type/EmbeddedFile/Subtype/text#2Fxml", InvoiceLocatorTests.Bytes(Cii()));
        Assert.Equal(["PDFK-PDF-02"], Ids(noDate));
    }

    [Fact]
    public void Zugferd20_ItsOwnValues_OnlyTheOldVersionWarned()
    {
        var legacy = Xmp(name: "zugferd-invoice.xml", version: "2p0", ns: InvoiceSources.Zugferd20Namespace, prefix: "zf",
            schemaUri: InvoiceSources.Zugferd20Namespace, schemaPrefix: "zf");
        Assert.Equal(["BR-HYBRID-10"], Ids(Pdf(xmp: legacy, name: "zugferd-invoice.xml")));
        // as the files of ZUGFeRD 2.0 itself write it (the official samples of 2.0): „…:invoice:2p0#“ with the prefix fx
        var written = Xmp(name: "zugferd-invoice.xml", version: "2p0", ns: InvoiceSources.Zugferd20NamespaceWritten,
            schemaUri: InvoiceSources.Zugferd20NamespaceWritten);
        Assert.Equal(["BR-HYBRID-10"], Ids(Pdf(xmp: written, name: "zugferd-invoice.xml")));
        // a generator writes zf with that namespace: the sources disagree on the prefix of 2.0, either counts; another one does not
        Assert.Equal(["BR-HYBRID-10"], Ids(Pdf(xmp: Xmp(name: "zugferd-invoice.xml", version: "2p0", ns: InvoiceSources.Zugferd20NamespaceWritten,
            schemaUri: InvoiceSources.Zugferd20NamespaceWritten, schemaPrefix: "zf"), name: "zugferd-invoice.xml")));
        Assert.Equal(["BR-HYBRID-05", "BR-HYBRID-10"], Ids(Pdf(xmp: Xmp(name: "zugferd-invoice.xml", version: "2p0", ns: InvoiceSources.Zugferd20NamespaceWritten,
            schemaUri: InvoiceSources.Zugferd20NamespaceWritten, schemaPrefix: "zugferd"), name: "zugferd-invoice.xml")));
    }

    [Fact]
    public void Ubl_NoHybridWithoutXmp_ReportedInAFacturXContainer()
    {
        const string Ubl = """
            <Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2" xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2">
              <cbc:CustomizationID>urn:cen.eu:en16931:2017</cbc:CustomizationID>
            </Invoice>
            """;
        Assert.Contains("PDFK-PDF-04", Ids(Pdf(Ubl)));
        var plain = Pdf(Ubl);
        plain[1] = "<</Type/Catalog/Pages 2 0 R/AF[5 0 R]/Names<</EmbeddedFiles<</Names[(factur-x.xml) 5 0 R]>>>>>>";
        Assert.Empty(Check(plain));
    }

    [Fact]
    public void SupplyDate_BetweenSellerAndBuyerInGermany()
    {
        Assert.Equal(["BR-FX-DE-04"], Ids(Pdf(Cii(delivery: false))));
        Assert.Empty(Ids(Pdf(Cii(delivery: false, type: "386"))));
        Assert.Empty(Ids(Pdf(Cii(delivery: false, seller: "FR", buyer: "DE"))));
    }

    [Fact]
    public void LooseXml_NoRulesOfThePdf()
    {
        var location = InvoiceLocator.Read(System.Text.Encoding.UTF8.GetBytes(Cii()));
        using var doc = PdfDocument.Open(TestFiles.MinimalPdfBytes());
        Assert.Empty(InvoiceHybridRules.Check(doc, location, InvoiceReader.Read(location.Xml!, location.Kind!)!));
    }
}
