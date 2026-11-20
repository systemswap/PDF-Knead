using System.Text;
using PdfKnead.EInvoice;
using PdfKnead.Tests;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// What an invoice XML is (KONZEPT.md, section 5.24, stage 1): the syntax by the namespace of the root element (never by prefix or file name),
/// the profile by the declared specification identifier, the version of the EN 16931, and whether the profile is an e-invoice in Germany
/// (BMF letters: MINIMUM, BASIC WL and ZUGFeRD 1.0 are not; ZUGFeRD is named only from 2.0.1 on). Only invented invoices.
/// </summary>
public sealed class InvoiceKindTests
{
    internal static string Cii(string guideline, string prefix = "rsm") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <{prefix}:CrossIndustryInvoice xmlns:{prefix}="urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100" xmlns:ram="urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100">
          <{prefix}:ExchangedDocumentContext>
            <ram:GuidelineSpecifiedDocumentContextParameter>
              <ram:ID>{guideline}</ram:ID>
            </ram:GuidelineSpecifiedDocumentContextParameter>
          </{prefix}:ExchangedDocumentContext>
          <{prefix}:ExchangedDocument>
            <ram:ID>RE-2026-0001</ram:ID>
            <ram:TypeCode>380</ram:TypeCode>
          </{prefix}:ExchangedDocument>
        </{prefix}:CrossIndustryInvoice>
        """;

    private static string ZugferdV1(string guideline, string ns = "urn:ferd:CrossIndustryDocument:invoice:1p0") => $"""
        <rsm:CrossIndustryDocument xmlns:rsm="{ns}" xmlns:ram="urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:12">
          <rsm:SpecifiedExchangedDocumentContext>
            <ram:GuidelineSpecifiedDocumentContextParameter><ram:ID>{guideline}</ram:ID></ram:GuidelineSpecifiedDocumentContextParameter>
          </rsm:SpecifiedExchangedDocumentContext>
        </rsm:CrossIndustryDocument>
        """;

    private static string Ubl(string root, string guideline) => $"""
        <{root} xmlns="urn:oasis:names:specification:ubl:schema:xsd:{root}-2" xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2">
          <cbc:CustomizationID>{guideline}</cbc:CustomizationID>
          <cbc:ID>RE-2026-0001</cbc:ID>
        </{root}>
        """;

    private static InvoiceKind Kind(string xml) => InvoiceKind.Of(InvoiceXml.Read(Encoding.UTF8.GetBytes(xml)));

    [Theory]
    [InlineData("urn:factur-x.eu:1p0:minimum", InvoiceProfile.Minimum, false)]
    [InlineData("urn:factur-x.eu:1p0:basicwl", InvoiceProfile.BasicWl, false)]
    [InlineData("urn:cen.eu:en16931:2017#compliant#urn:factur-x.eu:1p0:basic", InvoiceProfile.Basic, true)]
    [InlineData("urn:cen.eu:en16931:2017", InvoiceProfile.En16931, true)]
    [InlineData("urn:cen.eu:en16931:2017#conformant#urn:factur-x.eu:1p0:extended", InvoiceProfile.Extended, true)]
    [InlineData("urn:cen.eu:en16931:2017#conformant#urn:factur-x.eu:1p0:extended-ctc-fr", InvoiceProfile.ExtendedCtcFr, true)]
    [InlineData("urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0", InvoiceProfile.XRechnung, true)]
    [InlineData("urn:cen.eu:en16931:2017#conformant#urn:xeinkauf.de:kosit:extension:xrechnung_3.0", InvoiceProfile.XRechnung, true)]
    [InlineData("urn:cen.eu:en16931:2017#compliant#urn:xoev-de:kosit:standard:xrechnung_1.2", InvoiceProfile.XRechnung, true)]
    [InlineData("urn:cen.eu:en16931:2017#compliant#urn:zugferd.de:2p0:basic", InvoiceProfile.Basic, null)]      // ZUGFeRD 2.0: not named
    [InlineData("urn:cen.eu:en16931:2017#conformant#urn:zugferd.de:2p0:extended", InvoiceProfile.Extended, null)]
    [InlineData("urn:zugferd.de:2p0:minimum", InvoiceProfile.Minimum, false)]
    [InlineData("urn:beispiel:eigenes-profil", InvoiceProfile.Other, null)]
    [InlineData("", InvoiceProfile.None, null)]
    public void Cii_Profiles(string guideline, InvoiceProfile profile, bool? german)
    {
        var kind = Kind(Cii(guideline));
        Assert.Equal(InvoiceSyntax.Cii, kind.Syntax);
        Assert.Equal(profile, kind.Profile);
        Assert.Equal(german, kind.EInvoiceInGermany);
    }

    [Fact]
    public void Cii_ByNamespace_NotByPrefix()
    {
        Assert.Equal(InvoiceProfile.En16931, Kind(Cii("urn:cen.eu:en16931:2017", prefix: "x")).Profile);
        // the right local name in a wrong namespace, or in none, is no invoice
        Assert.Equal(InvoiceSyntax.Unknown, Kind("<CrossIndustryInvoice/>").Syntax);
        Assert.Equal(InvoiceSyntax.Unknown, Kind("<CrossIndustryInvoice xmlns=\"urn:beispiel:CrossIndustryInvoice:100\"/>").Syntax);
    }

    [Fact]
    public void Guideline_Trimmed_Norm_Read()
    {
        var kind = Kind(Cii("\n   urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0  \n"));
        Assert.Equal("urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0", kind.Guideline);
        Assert.Equal("2017", kind.Norm);
        Assert.Equal("2026", InvoiceKind.NormOf("urn:cen.eu:en16931:2026#compliant#urn:beispiel"));
        Assert.Null(InvoiceKind.NormOf("urn:factur-x.eu:1p0:minimum"));
    }

    [Theory]
    [InlineData("urn:ferd:CrossIndustryDocument:invoice:1p0:basic", InvoiceProfile.ZugferdV1Basic)]
    [InlineData("urn:ferd:CrossIndustryDocument:invoice:1p0:comfort", InvoiceProfile.ZugferdV1Comfort)]
    [InlineData("urn:ferd:CrossIndustryDocument:invoice:1p0:extended", InvoiceProfile.ZugferdV1Extended)]
    public void ZugferdV1_NeverAnEInvoiceInGermany(string guideline, InvoiceProfile profile)
    {
        var kind = Kind(ZugferdV1(guideline));
        Assert.Equal(InvoiceSyntax.ZugferdV1, kind.Syntax);
        Assert.Equal(profile, kind.Profile);
        Assert.False(kind.EInvoiceInGermany);
    }

    [Fact]
    public void ZugferdV1_Drafts_And_Orders()
    {
        // a draft of 2014 with a namespace of its own under urn:ferd:
        Assert.Equal(InvoiceSyntax.ZugferdV1, Kind(ZugferdV1("urn:ferd:CrossIndustryDocument:invoice:1p0:comfort", "urn:ferd:CrossIndustryDocument:invoice:1p0:entwurf")).Syntax);
        // an order in an invoice tree: not an invoice profile
        var order = Kind(ZugferdV1("urn:ferd:CrossIndustryDocument:order:1p0:extended"));
        Assert.Equal(InvoiceProfile.Other, order.Profile);
        Assert.Null(order.EInvoiceInGermany);
    }

    [Theory]
    [InlineData("Invoice", "urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0", InvoiceSyntax.UblInvoice, InvoiceProfile.XRechnung)]
    [InlineData("Invoice", "urn:cen.eu:en16931:2017#compliant#urn:fdc:peppol.eu:2017:poacc:billing:3.0", InvoiceSyntax.UblInvoice, InvoiceProfile.Peppol)]
    [InlineData("CreditNote", "urn:cen.eu:en16931:2017", InvoiceSyntax.UblCreditNote, InvoiceProfile.En16931)]
    public void Ubl_Profiles(string root, string guideline, InvoiceSyntax syntax, InvoiceProfile profile)
    {
        var kind = Kind(Ubl(root, guideline));
        Assert.Equal(syntax, kind.Syntax);
        Assert.Equal(profile, kind.Profile);
        Assert.True(kind.EInvoiceInGermany);
    }

    [Fact]
    public void OtherSyntaxes_Recognised_NotJudged()
    {
        var fattura = Kind("<p:FatturaElettronica xmlns:p=\"http://ivaservizi.agenziaentrate.gov.it/docs/xsd/fatture/v1.2\" versione=\"FPR12\"/>");
        Assert.Equal(InvoiceSyntax.FatturaPa, fattura.Syntax);
        Assert.Null(fattura.EInvoiceInGermany);
        var order = Kind("<rsm:SCRDMCCBDACIOMessageStructure xmlns:rsm=\"urn:un:unece:uncefact:data:standard:SCRDMCCBDACIOMessageStructure:100\"/>");
        Assert.Equal(InvoiceSyntax.OrderX, order.Syntax);
        Assert.Null(order.EInvoiceInGermany);
        Assert.Equal(InvoiceSyntax.Unknown, Kind("<Rechnung xmlns=\"urn:beispiel\"/>").Syntax);
    }
}
