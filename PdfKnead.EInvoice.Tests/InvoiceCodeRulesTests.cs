namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// Stage 4, part c: the code list rules (BR-CL) on the elements of each syntax, with the code lists of the Commission — a code counts after
/// its surrounding space is taken off, one with space inside or in the wrong case is no code (VATEX excepted, MIME exactly as written); each
/// violation names the code. Only invented invoices and names.
/// </summary>
public sealed class InvoiceCodeRulesTests
{
    private static string Cii => InvoiceReaderTests.Cii;

    private static string Ubl => InvoiceReaderTests.Ubl;

    private static IReadOnlyList<InvoiceRuleViolation> Codes(string xml) =>
        [.. InvoiceRules.Check(InvoiceReaderTests.Read(xml)).Where(v => v.Rule.Id.StartsWith("BR-CL-", StringComparison.Ordinal))];

    private static string[] Ids(string xml) => [.. Codes(xml).Select(v => v.Rule.Id).Distinct()];

    private static string Change(string xml, string from, string to)
    {
        Assert.Contains(from, xml, StringComparison.Ordinal);
        return xml.Replace(from, to, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalogue_23Rules_WithTheirSource()
    {
        var expected = new[] { 1, 3, 4, 5, 6, 7, 8, 10, 11, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26 }.Select(n => $"BR-CL-{n:00}");
        var codes = InvoiceRules.All.Where(r => r.Id.StartsWith("BR-CL-", StringComparison.Ordinal)).ToList();
        Assert.Equal(expected, codes.Select(r => r.Id));
        Assert.All(codes, r => Assert.Equal(InvoiceRules.CodeSource, r.Source));
        // EN 16931; XRechnung: InvoiceXRechnungRulesTests; profiles of ZUGFeRD: InvoiceProfileStructureTests
        Assert.Equal(220, InvoiceRules.All.Count(r => r.Source is InvoiceRules.Source or InvoiceRules.CodeSource));
    }

    [Fact]
    public void KeptInvoices_NoCodeFinding()
    {
        Assert.Empty(Codes(Cii));
        Assert.Empty(Codes(Ubl));
    }

    [Fact]
    public void Cii_CodesAsTheArtefactsReadThem()
    {
        var wrongCurrency = Codes(Change(Cii, "<ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode>", "<ram:InvoiceCurrencyCode>EUX</ram:InvoiceCurrencyCode>"));
        Assert.Equal("EUX", Assert.Single(wrongCurrency, v => v.Rule.Id == "BR-CL-04").Value);
        // the currency of the VAT total in small letters is no code (the lists are case sensitive)
        Assert.Contains("BR-CL-03", Ids(Change(Cii, "currencyID=\"EUR\">4.50", "currencyID=\"eur\">4.50")));
        // space around a code does not count, space inside does
        Assert.Empty(Codes(Change(Cii, "<ram:BilledQuantity unitCode=\"H87\">2", "<ram:BilledQuantity unitCode=\"  H87 \">2")));
        Assert.Contains("BR-CL-23", Ids(Change(Cii, "<ram:BilledQuantity unitCode=\"H87\">2", "<ram:BilledQuantity unitCode=\"H 87\">2")));
        Assert.Contains("BR-CL-23", Ids(Change(Cii, "<ram:BilledQuantity unitCode=\"H87\">2", "<ram:BilledQuantity unitCode=\"\">2")));
        Assert.Contains("BR-CL-14", Ids(Change(Cii, "<ram:CityName>Musterstadt</ram:CityName><ram:CountryID>DE</ram:CountryID>",
            "<ram:CityName>Musterstadt</ram:CityName><ram:CountryID>de</ram:CountryID>")));
        Assert.Contains("BR-CL-16", Ids(Change(Cii, "<ram:TypeCode>58</ram:TypeCode>", "<ram:TypeCode>999</ram:TypeCode>")));
        // split payment (B) is a category of the artefacts, not of the list of the Commission
        Assert.DoesNotContain("BR-CL-18", Ids(Change(Cii, "<ram:CategoryCode>S</ram:CategoryCode><ram:RateApplicablePercent>7</ram:RateApplicablePercent>",
            "<ram:CategoryCode>B</ram:CategoryCode><ram:RateApplicablePercent>7</ram:RateApplicablePercent>")));
        Assert.Contains("BR-CL-18", Ids(Change(Cii, "<ram:CategoryCode>S</ram:CategoryCode><ram:RateApplicablePercent>7</ram:RateApplicablePercent>",
            "<ram:CategoryCode>X</ram:CategoryCode><ram:RateApplicablePercent>7</ram:RateApplicablePercent>")));
        // the MIME code exactly as written
        Assert.Contains("BR-CL-24", Ids(Change(Cii, "mimeCode=\"application/pdf\"", "mimeCode=\"application/zip\"")));
        Assert.Contains("BR-CL-24", Ids(Change(Cii, "mimeCode=\"application/pdf\"", "mimeCode=\" application/pdf\"")));
    }

    [Fact]
    public void TaxSchemeAndIndicator_ReadAsTheArtefactsReadThem()
    {
        // the tax scheme without regard to case and space (FG 834): the seller VAT identifier and the category of the breakdown stay terms
        var ubl = InvoiceReaderTests.Read(Ubl.Replace("<cbc:ID>VAT</cbc:ID>", "<cbc:ID> vat </cbc:ID>", StringComparison.Ordinal));
        Assert.Equal("DE123456789", ubl.Field("BT-31")?.Text);
        Assert.Equal("S", ubl.Groups("BG-23").Single().Field("BT-118")?.Text);
        Assert.DoesNotContain(ubl.Further, f => f.Text?.Trim() == "vat");   // a qualifier, not a further value
        var ids = InvoiceRules.Check(ubl).Select(v => v.Rule.Id).ToList();
        Assert.DoesNotContain("BR-47", ids);
        Assert.DoesNotContain("BR-S-02", ids);
        // the indicator as a boolean: „1“ with space is a charge
        const string charge = "<ram:SpecifiedTradeAllowanceCharge><ram:ChargeIndicator><udt:Indicator> 1 </udt:Indicator></ram:ChargeIndicator>"
            + "<ram:ActualAmount>0.00</ram:ActualAmount><ram:Reason>Fracht</ram:Reason><ram:CategoryTradeTax><ram:TypeCode>VAT</ram:TypeCode>"
            + "<ram:CategoryCode>S</ram:CategoryCode><ram:RateApplicablePercent>19</ram:RateApplicablePercent></ram:CategoryTradeTax></ram:SpecifiedTradeAllowanceCharge>";
        var cii = InvoiceReaderTests.Read(Change(Cii, "<ram:SpecifiedTradeSettlementHeaderMonetarySummation>", charge + "<ram:SpecifiedTradeSettlementHeaderMonetarySummation>"));
        Assert.Single(cii.Groups("BG-21"));
        Assert.Empty(cii.Groups("BG-20"));
    }

    [Fact]
    public void Cii_ExemptionReasonCode_WithoutRegardToCase()
    {
        string Exempt(string code) => Change(Cii, "<ram:SpecifiedTradeSettlementHeaderMonetarySummation>",
            $"<ram:ApplicableTradeTax><ram:CalculatedAmount>0.00</ram:CalculatedAmount><ram:TypeCode>VAT</ram:TypeCode><ram:ExemptionReasonCode>{code}</ram:ExemptionReasonCode>"
            + "<ram:BasisAmount>0.00</ram:BasisAmount><ram:CategoryCode>E</ram:CategoryCode><ram:RateApplicablePercent>0</ram:RateApplicablePercent></ram:ApplicableTradeTax>"
            + "<ram:SpecifiedTradeSettlementHeaderMonetarySummation>");
        Assert.DoesNotContain("BR-CL-22", Ids(Exempt("VATEX-EU-132")));
        Assert.DoesNotContain("BR-CL-22", Ids(Exempt("vatex-eu-132")));
        Assert.Contains("BR-CL-22", Ids(Exempt("VATEX-EU-999")));
    }

    [Fact]
    public void Ubl_InvoiceAndCreditNoteTypes_CurrencyOnEveryAmount_NoteSubject_Sepa()
    {
        // an invoice takes the invoice types (and 81), not the credit note types
        Assert.Contains("BR-CL-01", Ids(Change(Ubl, "<cbc:InvoiceTypeCode>380</cbc:InvoiceTypeCode>", "<cbc:InvoiceTypeCode>381</cbc:InvoiceTypeCode>")));
        Assert.DoesNotContain("BR-CL-01", Ids(Change(Ubl, "<cbc:InvoiceTypeCode>380</cbc:InvoiceTypeCode>", "<cbc:InvoiceTypeCode>81</cbc:InvoiceTypeCode>")));
        // UBL binds a currency to every amount: one without is no code
        var noCurrency = Codes(Change(Ubl, "<cbc:PriceAmount currencyID=\"EUR\">", "<cbc:PriceAmount>"));
        Assert.Contains(noCurrency, v => v.Rule.Id == "BR-CL-03" && v.Value is null);
        // a subject code of three characters between the first two „#“
        Assert.Contains("BR-CL-08", Ids(Change(Ubl, "<cbc:Note>Allgemeiner Hinweis</cbc:Note>", "<cbc:Note>#QQQ#Allgemeiner Hinweis</cbc:Note>")));
        Assert.DoesNotContain("BR-CL-08", Ids(Change(Ubl, "<cbc:Note>Allgemeiner Hinweis</cbc:Note>", "<cbc:Note>#ZZZ#Allgemeiner Hinweis</cbc:Note>")));   // „mutually defined“
        Assert.DoesNotContain("BR-CL-08", Ids(Change(Ubl, "<cbc:Note>Allgemeiner Hinweis</cbc:Note>", "<cbc:Note>#AAI#Allgemeiner Hinweis</cbc:Note>")));
        Assert.DoesNotContain("BR-CL-08", Ids(Change(Ubl, "<cbc:Note>Allgemeiner Hinweis</cbc:Note>", "<cbc:Note>Nummer #12345# siehe oben</cbc:Note>")));
        // SEPA as scheme of an identifier: for the seller (creditor identifier), not for the buyer
        const string supplier = "<cac:AccountingSupplierParty><cac:Party>";
        const string customer = "<cac:AccountingCustomerParty><cac:Party>";
        const string sepa = "<cac:PartyIdentification><cbc:ID schemeID=\"SEPA\">DE98ZZZ09999999999</cbc:ID></cac:PartyIdentification>";
        Assert.DoesNotContain("BR-CL-10", Ids(Change(Ubl, supplier, supplier + sepa)));
        Assert.Contains("BR-CL-10", Ids(Change(Ubl, customer, customer + sepa)));
    }
}
