using System.Text;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// The readable invoice of stage 2 (KONZEPT.md, section 5.24): business terms and groups read along the mapping of the KoSIT visualization,
/// the places built by hand (payment groups, base quantity, UBL notes, VAT point date), the qualifiers of the syntax and the values without a
/// mapping („Weitere Angaben“). Only invented invoices and names.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class InvoiceReaderTests
{
    internal const string Cii = """
        <rsm:CrossIndustryInvoice xmlns:rsm="urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100"
            xmlns:ram="urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100"
            xmlns:udt="urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100"
            xmlns:qdt="urn:un:unece:uncefact:data:standard:QualifiedDataType:100">
          <rsm:ExchangedDocumentContext>
            <ram:GuidelineSpecifiedDocumentContextParameter><ram:ID>urn:cen.eu:en16931:2017</ram:ID></ram:GuidelineSpecifiedDocumentContextParameter>
          </rsm:ExchangedDocumentContext>
          <rsm:ExchangedDocument>
            <ram:ID>RE-2026-0001</ram:ID>
            <ram:Name>RECHNUNG</ram:Name>
            <ram:TypeCode>380</ram:TypeCode>
            <ram:IssueDateTime><udt:DateTimeString format="102">20261113</udt:DateTimeString></ram:IssueDateTime>
            <ram:IncludedNote><ram:Content>Vielen Dank für den Auftrag</ram:Content></ram:IncludedNote>
          </rsm:ExchangedDocument>
          <rsm:SupplyChainTradeTransaction>
            <ram:IncludedSupplyChainTradeLineItem>
              <ram:AssociatedDocumentLineDocument><ram:LineID>1</ram:LineID></ram:AssociatedDocumentLineDocument>
              <ram:SpecifiedTradeProduct><ram:Name>Musterartikel</ram:Name></ram:SpecifiedTradeProduct>
              <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice><ram:ChargeAmount>12.00</ram:ChargeAmount><ram:BasisQuantity unitCode="H87">1</ram:BasisQuantity></ram:GrossPriceProductTradePrice>
                <ram:NetPriceProductTradePrice><ram:ChargeAmount>10.00</ram:ChargeAmount><ram:BasisQuantity unitCode="H87">1</ram:BasisQuantity></ram:NetPriceProductTradePrice>
              </ram:SpecifiedLineTradeAgreement>
              <ram:SpecifiedLineTradeDelivery><ram:BilledQuantity unitCode="H87">2</ram:BilledQuantity></ram:SpecifiedLineTradeDelivery>
              <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax><ram:TypeCode>VAT</ram:TypeCode><ram:CategoryCode>S</ram:CategoryCode><ram:RateApplicablePercent>19</ram:RateApplicablePercent></ram:ApplicableTradeTax>
                <ram:SpecifiedTradeSettlementLineMonetarySummation><ram:LineTotalAmount>20.00</ram:LineTotalAmount></ram:SpecifiedTradeSettlementLineMonetarySummation>
              </ram:SpecifiedLineTradeSettlement>
            </ram:IncludedSupplyChainTradeLineItem>
            <ram:IncludedSupplyChainTradeLineItem>
              <ram:AssociatedDocumentLineDocument><ram:LineID>2</ram:LineID></ram:AssociatedDocumentLineDocument>
              <ram:SpecifiedTradeProduct><ram:Name>Zweiter Musterartikel</ram:Name></ram:SpecifiedTradeProduct>
              <ram:SpecifiedLineTradeAgreement>
                <ram:NetPriceProductTradePrice><ram:ChargeAmount>5.00</ram:ChargeAmount><ram:BasisQuantity unitCode="C62">2</ram:BasisQuantity></ram:NetPriceProductTradePrice>
              </ram:SpecifiedLineTradeAgreement>
              <ram:SpecifiedLineTradeDelivery><ram:BilledQuantity unitCode="C62">4</ram:BilledQuantity></ram:SpecifiedLineTradeDelivery>
              <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax><ram:TypeCode>VAT</ram:TypeCode><ram:CategoryCode>S</ram:CategoryCode><ram:RateApplicablePercent>7</ram:RateApplicablePercent></ram:ApplicableTradeTax>
                <ram:SpecifiedTradeSettlementLineMonetarySummation><ram:LineTotalAmount>10.00</ram:LineTotalAmount></ram:SpecifiedTradeSettlementLineMonetarySummation>
              </ram:SpecifiedLineTradeSettlement>
            </ram:IncludedSupplyChainTradeLineItem>
            <ram:ApplicableHeaderTradeAgreement>
              <ram:BuyerReference>04011000-12345-34</ram:BuyerReference>
              <ram:SellerTradeParty>
                <ram:ID>L-1</ram:ID>
                <ram:Name>Musterfirma Beispiel GmbH</ram:Name>
                <ram:PostalTradeAddress><ram:PostcodeCode>12345</ram:PostcodeCode><ram:LineOne>Musterweg 1</ram:LineOne><ram:CityName>Musterstadt</ram:CityName><ram:CountryID>DE</ram:CountryID></ram:PostalTradeAddress>
                <ram:SpecifiedTaxRegistration><ram:ID schemeID="VA">DE123456789</ram:ID></ram:SpecifiedTaxRegistration>
              </ram:SellerTradeParty>
              <ram:BuyerTradeParty>
                <ram:Name>Max Mustermann</ram:Name>
                <ram:PostalTradeAddress><ram:PostcodeCode>54321</ram:PostcodeCode><ram:CityName>Beispielstadt</ram:CityName><ram:CountryID>DE</ram:CountryID></ram:PostalTradeAddress>
              </ram:BuyerTradeParty>
              <ram:AdditionalReferencedDocument>
                <ram:IssuerAssignedID>Lieferschein-1</ram:IssuerAssignedID>
                <ram:TypeCode>916</ram:TypeCode>
                <ram:AttachmentBinaryObject mimeCode="application/pdf" filename="lieferschein.pdf">JVBERi0=</ram:AttachmentBinaryObject>
              </ram:AdditionalReferencedDocument>
            </ram:ApplicableHeaderTradeAgreement>
            <ram:ApplicableHeaderTradeDelivery/>
            <ram:ApplicableHeaderTradeSettlement>
              <ram:PaymentReference>RE-2026-0001</ram:PaymentReference>
              <ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode>
              <ram:SpecifiedTradeSettlementPaymentMeans>
                <ram:TypeCode>58</ram:TypeCode>
                <ram:Information>Überweisung</ram:Information>
                <ram:PayeePartyCreditorFinancialAccount><ram:IBANID>DE02120300000000202051</ram:IBANID></ram:PayeePartyCreditorFinancialAccount>
              </ram:SpecifiedTradeSettlementPaymentMeans>
              <ram:SpecifiedTradeSettlementPaymentMeans>
                <ram:TypeCode>58</ram:TypeCode>
                <ram:PayeePartyCreditorFinancialAccount><ram:IBANID>DE02500105170137075030</ram:IBANID></ram:PayeePartyCreditorFinancialAccount>
              </ram:SpecifiedTradeSettlementPaymentMeans>
              <ram:ApplicableTradeTax>
                <ram:CalculatedAmount>3.80</ram:CalculatedAmount><ram:TypeCode>VAT</ram:TypeCode><ram:BasisAmount>20.00</ram:BasisAmount>
                <ram:CategoryCode>S</ram:CategoryCode><ram:TaxPointDate><udt:DateString format="102">20261101</udt:DateString></ram:TaxPointDate>
                <ram:RateApplicablePercent>19</ram:RateApplicablePercent>
              </ram:ApplicableTradeTax>
              <ram:ApplicableTradeTax>
                <ram:CalculatedAmount>0.70</ram:CalculatedAmount><ram:TypeCode>VAT</ram:TypeCode><ram:BasisAmount>10.00</ram:BasisAmount>
                <ram:CategoryCode>S</ram:CategoryCode><ram:TaxPointDate><udt:DateString format="102">20261101</udt:DateString></ram:TaxPointDate>
                <ram:RateApplicablePercent>7</ram:RateApplicablePercent>
              </ram:ApplicableTradeTax>
              <ram:SpecifiedTradeSettlementHeaderMonetarySummation>
                <ram:LineTotalAmount>30.00</ram:LineTotalAmount><ram:TaxBasisTotalAmount>30.00</ram:TaxBasisTotalAmount>
                <ram:TaxTotalAmount currencyID="EUR">4.50</ram:TaxTotalAmount><ram:GrandTotalAmount>34.50</ram:GrandTotalAmount>
                <ram:DuePayableAmount>34.50</ram:DuePayableAmount>
              </ram:SpecifiedTradeSettlementHeaderMonetarySummation>
            </ram:ApplicableHeaderTradeSettlement>
          </rsm:SupplyChainTradeTransaction>
        </rsm:CrossIndustryInvoice>
        """;

    internal const string Ubl = """
        <Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"
            xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2"
            xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2">
          <cbc:CustomizationID>urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0</cbc:CustomizationID>
          <cbc:ProfileID>urn:fdc:peppol.eu:2017:poacc:billing:01:1.0</cbc:ProfileID>
          <cbc:ID>RE-2026-0002</cbc:ID>
          <cbc:IssueDate>2026-11-13</cbc:IssueDate>
          <cbc:InvoiceTypeCode>380</cbc:InvoiceTypeCode>
          <cbc:Note>AAI</cbc:Note>
          <cbc:Note>Allgemeiner Hinweis</cbc:Note>
          <cbc:Note>Zweiter Hinweis</cbc:Note>
          <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
          <cbc:BuyerReference>991-01234-56</cbc:BuyerReference>
          <cac:AccountingSupplierParty><cac:Party>
            <cac:PostalAddress><cbc:CityName>Musterstadt</cbc:CityName><cbc:PostalZone>12345</cbc:PostalZone><cac:Country><cbc:IdentificationCode>DE</cbc:IdentificationCode></cac:Country></cac:PostalAddress>
            <cac:PartyTaxScheme><cbc:CompanyID>DE123456789</cbc:CompanyID><cac:TaxScheme><cbc:ID>VAT</cbc:ID></cac:TaxScheme></cac:PartyTaxScheme>
            <cac:PartyLegalEntity><cbc:RegistrationName>Musterfirma Beispiel GmbH</cbc:RegistrationName></cac:PartyLegalEntity>
          </cac:Party></cac:AccountingSupplierParty>
          <cac:AccountingCustomerParty><cac:Party>
            <cac:PostalAddress><cbc:CityName>Beispielstadt</cbc:CityName><cbc:PostalZone>54321</cbc:PostalZone><cac:Country><cbc:IdentificationCode>DE</cbc:IdentificationCode></cac:Country></cac:PostalAddress>
            <cac:PartyLegalEntity><cbc:RegistrationName>Max Mustermann</cbc:RegistrationName></cac:PartyLegalEntity>
          </cac:Party></cac:AccountingCustomerParty>
          <cac:PaymentMeans><cbc:PaymentMeansCode name="Überweisung">58</cbc:PaymentMeansCode><cbc:PaymentID>RE-2026-0002</cbc:PaymentID>
            <cac:PayeeFinancialAccount><cbc:ID>DE02120300000000202051</cbc:ID></cac:PayeeFinancialAccount></cac:PaymentMeans>
          <cac:PaymentMeans><cbc:PaymentMeansCode>58</cbc:PaymentMeansCode><cbc:PaymentID>RE-2026-0002</cbc:PaymentID>
            <cac:PayeeFinancialAccount><cbc:ID>DE02500105170137075030</cbc:ID></cac:PayeeFinancialAccount></cac:PaymentMeans>
          <cac:TaxTotal><cbc:TaxAmount currencyID="EUR">1.90</cbc:TaxAmount>
            <cac:TaxSubtotal><cbc:TaxableAmount currencyID="EUR">10.00</cbc:TaxableAmount><cbc:TaxAmount currencyID="EUR">1.90</cbc:TaxAmount>
              <cac:TaxCategory><cbc:ID>S</cbc:ID><cbc:Percent>19</cbc:Percent><cac:TaxScheme><cbc:ID>VAT</cbc:ID></cac:TaxScheme></cac:TaxCategory></cac:TaxSubtotal></cac:TaxTotal>
          <cac:LegalMonetaryTotal><cbc:LineExtensionAmount currencyID="EUR">10.00</cbc:LineExtensionAmount><cbc:TaxExclusiveAmount currencyID="EUR">10.00</cbc:TaxExclusiveAmount>
            <cbc:TaxInclusiveAmount currencyID="EUR">11.90</cbc:TaxInclusiveAmount><cbc:PayableAmount currencyID="EUR">11.90</cbc:PayableAmount></cac:LegalMonetaryTotal>
          <cac:InvoiceLine><cbc:ID>1</cbc:ID><cbc:InvoicedQuantity unitCode="H87">1</cbc:InvoicedQuantity><cbc:LineExtensionAmount currencyID="EUR">10.00</cbc:LineExtensionAmount>
            <cac:Item><cbc:Name>Musterartikel</cbc:Name><cac:ClassifiedTaxCategory><cbc:ID>S</cbc:ID><cbc:Percent>19</cbc:Percent><cac:TaxScheme><cbc:ID>VAT</cbc:ID></cac:TaxScheme></cac:ClassifiedTaxCategory></cac:Item>
            <cac:Price><cbc:PriceAmount currencyID="EUR">10.00</cbc:PriceAmount><cbc:BaseQuantity unitCode="H87">1</cbc:BaseQuantity></cac:Price></cac:InvoiceLine>
        </Invoice>
        """;

    internal static InvoiceData Read(string xml)
    {
        var doc = InvoiceXml.Read(Encoding.UTF8.GetBytes(xml));
        return InvoiceReader.Read(doc, InvoiceKind.Of(doc))!;
    }

    [Fact]
    public void Tables_AllLoad_EveryPathReadable()
    {
        foreach (var syntax in new[] { InvoiceSyntax.Cii, InvoiceSyntax.UblInvoice, InvoiceSyntax.UblCreditNote })
        {
            var map = InvoiceMap.Of(syntax)!;
            Assert.True(map.Rules.Count() > 200, $"{syntax}: {map.Rules.Count()}");
            Assert.Contains("KoSIT", map.Source);
        }
        Assert.Null(InvoiceMap.Of(InvoiceSyntax.FatturaPa));
        Assert.True(InvoiceLabels.Count > 200);
    }

    [Fact]
    public void Cii_TermsGroupsLines()
    {
        var data = Read(Cii);
        Assert.Equal("RE-2026-0001", data.Text("BT-1"));
        Assert.Equal("20261113", data.Text("BT-2"));
        Assert.Equal("380", data.Text("BT-3"));
        Assert.Equal("EUR", data.Text("BT-5"));
        Assert.Equal("04011000-12345-34", data.Text("BT-10"));
        var seller = Assert.Single(data.Groups("BG-4"));
        Assert.Equal("Musterfirma Beispiel GmbH", seller.Text("BT-27"));
        Assert.Equal("L-1", seller.Text("BT-29"));
        Assert.Equal("DE123456789", seller.Text("BT-31"));
        Assert.Equal("Musterstadt", Assert.Single(seller.GroupsOf("BG-5")).Text("BT-37"));
        Assert.Equal("Max Mustermann", Assert.Single(data.Groups("BG-7")).Text("BT-44"));
        var lines = data.Groups("BG-25").ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(["Musterartikel", "Zweiter Musterartikel"], lines.Select(l => l.GroupsOf("BG-31").Single().Text("BT-153")));
        Assert.Equal("4.50", data.Field("BT-110")!.Text);
        Assert.Equal("EUR", data.Field("BT-110")!.Attribute("currencyID"));
        Assert.Equal(2, data.Groups("BG-23").Count());
        Assert.Equal("Vielen Dank für den Auftrag", Assert.Single(data.Groups("BG-1")).Text("BT-22"));
        Assert.Equal("Verkäufer", seller.Label);
        Assert.Equal("Rechnungsnummer", data.Field("BT-1")!.Label);
    }

    [Fact]
    public void Cii_BuiltByHand_PaymentGroupBaseQuantityPointDate()
    {
        var data = Read(Cii);
        // payment means of one code: one group, both accounts, code and text of the first, the remittance information
        var payment = Assert.Single(data.Groups("BG-16"));
        Assert.Equal("58", payment.Text("BT-81"));
        Assert.Equal("Überweisung", payment.Text("BT-82"));
        Assert.Equal("RE-2026-0001", payment.Text("BT-83"));
        Assert.Equal(["DE02120300000000202051", "DE02500105170137075030"], payment.GroupsOf("BG-17").Select(g => g.Text("BT-84")));
        // the base quantity of the gross price where both prices have one; else of the net price
        var lines = data.Groups("BG-25").ToList();
        var first = lines[0].GroupsOf("BG-29").Single();
        Assert.Equal("1", first.Text("BT-149"));
        Assert.Equal("H87", first.Text("BT-150"));
        var second = lines[1].GroupsOf("BG-29").Single();
        Assert.Equal("2", second.Text("BT-149"));
        Assert.Equal("C62", second.Text("BT-150"));
        // the VAT point date once, although both breakdowns carry it
        Assert.Single(data.Root.Fields, f => f.Id == "BT-7");
        Assert.Equal("20261101", data.Text("BT-7"));
    }

    [Fact]
    public void Cii_AttachmentQualifiersFurther()
    {
        var data = Read(Cii);
        var document = Assert.Single(data.Groups("BG-24"));
        Assert.Equal("Lieferschein-1", document.Text("BT-122"));
        var attachment = document.Field("BT-125");
        Assert.NotNull(attachment);
        Assert.Equal("%PDF-"u8.ToArray(), attachment.Node.Element.Binary);
        Assert.Equal("lieferschein.pdf", attachment.Attribute("filename"));
        // the document's name is no term: shown as a further value; the qualifiers (VAT, 916) and the repeated point date are not
        var further = data.Further.Select(f => f.Path).ToList();
        Assert.Contains("/CrossIndustryInvoice/ExchangedDocument/Name", further);
        Assert.DoesNotContain(further, p => p.EndsWith("/TypeCode", StringComparison.Ordinal));
        Assert.DoesNotContain(further, p => p.Contains("TaxPointDate", StringComparison.Ordinal));
        Assert.DoesNotContain(further, p => p.Contains("BasisQuantity", StringComparison.Ordinal));
        Assert.Single(further);
    }

    [Fact]
    public void Ubl_NotesProcessPaymentsNoFurther()
    {
        var data = Read(Ubl);
        Assert.Equal("RE-2026-0002", data.Text("BT-1"));
        Assert.Equal("2026-11-13", data.Text("BT-2"));
        var notes = data.Groups("BG-1").ToList();
        Assert.Equal(2, notes.Count);
        Assert.Equal("AAI", notes[0].Text("BT-21"));
        Assert.Equal("Allgemeiner Hinweis", notes[0].Text("BT-22"));
        Assert.Null(notes[1].Text("BT-21"));
        Assert.Equal("Zweiter Hinweis", notes[1].Text("BT-22"));
        var process = Assert.Single(data.Groups("BG-2"));
        Assert.Equal("urn:fdc:peppol.eu:2017:poacc:billing:01:1.0", process.Text("BT-23"));
        Assert.StartsWith("urn:cen.eu:en16931:2017", process.Text("BT-24"));
        var payment = Assert.Single(data.Groups("BG-16"));
        Assert.Equal("58", payment.Text("BT-81"));
        Assert.Equal("Überweisung", payment.Text("BT-82"));
        Assert.Equal("RE-2026-0002", Assert.Single(payment.Fields, f => f.Id == "BT-83").Text);   // once, not per payment means
        Assert.Equal(2, payment.GroupsOf("BG-17").Count());
        Assert.Equal("Musterfirma Beispiel GmbH", Assert.Single(data.Groups("BG-4")).Text("BT-27"));
        var line = Assert.Single(data.Groups("BG-25"));
        Assert.Equal("1", line.GroupsOf("BG-29").Single().Text("BT-149"));
        Assert.Equal("H87", line.GroupsOf("BG-29").Single().Text("BT-150"));
        Assert.Empty(data.Further);
    }

    [Fact]
    public void Codes_InPlainText_GermanWhereKept_NeverGuessed()
    {
        Assert.True(InvoiceCodes.Count > 4000);
        var data = Read(Cii);
        Assert.Equal("Handelsrechnung", InvoiceCodes.NameOf(data.Field("BT-3")!));
        Assert.Equal("Normalsatz", InvoiceCodes.Name("5305", "S"));
        Assert.Equal("SEPA-Überweisung", InvoiceCodes.NameOf(data.Groups("BG-16").Single().Field("BT-81")!));
        Assert.Equal("Stück", InvoiceCodes.Name("Unit", "H87"));
        Assert.Equal("Euro", InvoiceCodes.NameOf(data.Field("BT-5")!));                 // from the list (English where no German is kept)
        Assert.Null(InvoiceCodes.Name("1001", "99999"));                                // unknown: stays a code
        Assert.Null(InvoiceCodes.NameOf(data.Field("BT-1")!));                          // no code list
        Assert.Equal("ICD", InvoiceCodes.ListOf("BT-29", "schemeID"));
        var before = Tr.Current;
        try
        {
            Tr.Use(English.Language);
            Assert.Equal("Commercial invoice", InvoiceCodes.Name("1001", "380"));
            Assert.Equal("Delivery date, actual", InvoiceCodes.Name("2005", "35"));
        }
        finally
        {
            Tr.Use(before);
        }
    }

    [Fact]
    public void ZugferdV1_ReadThroughCii_WithItsNote()
    {
        const string v1 = """
            <rsm:CrossIndustryDocument xmlns:rsm="urn:ferd:CrossIndustryDocument:invoice:1p0"
                xmlns:ram="urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:12"
                xmlns:udt="urn:un:unece:uncefact:data:standard:UnqualifiedDataType:15">
              <rsm:SpecifiedExchangedDocumentContext>
                <ram:GuidelineSpecifiedDocumentContextParameter><ram:ID>urn:ferd:CrossIndustryDocument:invoice:1p0:comfort</ram:ID></ram:GuidelineSpecifiedDocumentContextParameter>
              </rsm:SpecifiedExchangedDocumentContext>
              <rsm:HeaderExchangedDocument>
                <ram:ID>RE-2014-0001</ram:ID><ram:TypeCode>380</ram:TypeCode>
                <ram:IssueDateTime><udt:DateTimeString format="102">20140101</udt:DateTimeString></ram:IssueDateTime>
              </rsm:HeaderExchangedDocument>
              <rsm:SpecifiedSupplyChainTradeTransaction>
                <ram:ApplicableSupplyChainTradeAgreement>
                  <ram:SellerTradeParty><ram:Name>Musterfirma Beispiel GmbH</ram:Name></ram:SellerTradeParty>
                </ram:ApplicableSupplyChainTradeAgreement>
                <ram:ApplicableSupplyChainTradeSettlement>
                  <ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode>
                  <ram:ApplicableTradeTax><ram:CalculatedAmount>1.90</ram:CalculatedAmount><ram:TypeCode>VAT</ram:TypeCode><ram:BasisAmount>10.00</ram:BasisAmount>
                    <ram:CategoryCode>S</ram:CategoryCode><ram:ApplicablePercent>19</ram:ApplicablePercent></ram:ApplicableTradeTax>
                  <ram:SpecifiedTradeSettlementMonetarySummation><ram:LineTotalAmount>10.00</ram:LineTotalAmount><ram:GrandTotalAmount>11.90</ram:GrandTotalAmount></ram:SpecifiedTradeSettlementMonetarySummation>
                </ram:ApplicableSupplyChainTradeSettlement>
                <ram:IncludedSupplyChainTradeLineItem>
                  <ram:AssociatedDocumentLineDocument><ram:LineID>1</ram:LineID></ram:AssociatedDocumentLineDocument>
                  <ram:SpecifiedSupplyChainTradeSettlement>
                    <ram:SpecifiedTradeSettlementMonetarySummation><ram:LineTotalAmount>10.00</ram:LineTotalAmount></ram:SpecifiedTradeSettlementMonetarySummation>
                  </ram:SpecifiedSupplyChainTradeSettlement>
                  <ram:SpecifiedTradeProduct><ram:Name>Musterartikel</ram:Name></ram:SpecifiedTradeProduct>
                </ram:IncludedSupplyChainTradeLineItem>
              </rsm:SpecifiedSupplyChainTradeTransaction>
            </rsm:CrossIndustryDocument>
            """;
        var data = Read(v1);
        Assert.NotNull(data.Note);
        Assert.Equal("RE-2014-0001", data.Text("BT-1"));
        Assert.Equal("Musterfirma Beispiel GmbH", data.Groups("BG-4").Single().Text("BT-27"));
        Assert.Equal("19", data.Groups("BG-23").Single().Text("BT-119"));        // ApplicablePercent of ZUGFeRD 1
        Assert.Equal("10.00", data.Groups("BG-22").Single().Text("BT-106"));     // the header's summation
        Assert.Equal("10.00", data.Groups("BG-25").Single().Text("BT-131"));     // the line's summation
        Assert.Null(Read(Cii).Note);
    }

    [Fact]
    public void OtherSyntaxes_NoReadableForm()
    {
        var doc = InvoiceXml.Read("<p:FatturaElettronica xmlns:p=\"http://ivaservizi.agenziaentrate.gov.it/docs/xsd/fatture/v1.2\"/>"u8.ToArray());
        Assert.Null(InvoiceReader.Read(doc, InvoiceKind.Of(doc)));
    }

    [Fact]
    public void Labels_FollowTheLanguage()
    {
        var data = Read(Cii);
        var before = Tr.Current;
        try
        {
            Tr.Use(English.Language);
            Assert.Equal("Seller", data.Groups("BG-4").Single().Label);
            Assert.Equal("Invoice number", data.Field("BT-1")!.Label);
        }
        finally
        {
            Tr.Use(before);
        }
    }
}
