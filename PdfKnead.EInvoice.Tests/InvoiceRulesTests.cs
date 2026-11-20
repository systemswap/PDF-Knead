using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Tests;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// Stage 4, parts a and b: the business rules of EN 16931 on the readable form (KONZEPT.md, section 5.24, „Weg für Stufe 4“) — the catalogue
/// holds the rules the validation artefacts check (and BR-CO-25 of the standard as a warning), each rule finds what it is about and nothing in
/// an invoice that keeps it; where the artefacts of CII and UBL read a rule alike it is read so, where they differ as the standard says
/// (ENTSCHEIDUNGEN.md, „Mutationstest“). The differential tests against the artefacts run over the collections (Zuordnung/regelprobe.py) and
/// over systematically changed invoices (Zuordnung/regelmutation.py). Only invented invoices and names.
/// </summary>
public sealed class InvoiceRulesTests
{
    private static string Cii => InvoiceReaderTests.Cii;

    private static string[] Ids(string xml) => [.. InvoiceRules.Check(InvoiceReaderTests.Read(xml)).Select(v => v.Rule.Id).Distinct()];

    [Fact]
    public void Catalogue_TheRulesOfTheArtefacts_AndBrCo25()
    {
        var expected = new List<string>();
        foreach (var n in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33,
                     36, 37, 38, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 61, 62, 63, 64, 65 })
            expected.Add($"BR-{n:00}");
        for (var n = 3; n <= 26; n++) expected.Add($"BR-CO-{n:00}");
        foreach (var n in new[] { 1, 2, 5, 6, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 23, 24, 25, 27, 28 }) expected.Add($"BR-DEC-{n:00}");
        expected.AddRange(["BR-B-01", "BR-B-02"]);
        expected.RemoveAll(id => id is "BR-CO-05" or "BR-CO-06" or "BR-CO-07" or "BR-CO-08");   // „same kind of reason“: text, not checkable
        // part b: the VAT categories, each 01 to 10; intra-community supply also 11 and 12, „not subject to VAT“ 11 to 14 — 96 rules
        foreach (var family in new[] { "S", "Z", "E", "AE", "IC", "G", "O", "IG", "IP" })
            for (var n = 1; n <= 10; n++) expected.Add($"BR-{family}-{n:00}");
        expected.AddRange(["BR-IC-11", "BR-IC-12", "BR-O-11", "BR-O-12", "BR-O-13", "BR-O-14"]);
        Assert.Equal(197, expected.Count);
        expected.AddRange(InvoiceRules.All.Select(r => r.Id).Where(id => id.StartsWith("BR-CL-", StringComparison.Ordinal)));   // part c (InvoiceCodeRulesTests)
        Assert.Equal(expected.Order(StringComparer.Ordinal), InvoiceRules.All.Where(r => r.Source is InvoiceRules.Source or InvoiceRules.CodeSource).Select(r => r.Id).Order(StringComparer.Ordinal));
        Assert.Equal(InvoiceRuleSeverity.Warning, InvoiceRules.All.Single(r => r.Id == "BR-CO-25").Severity);
        Assert.All(InvoiceRules.All, r => Assert.False(string.IsNullOrWhiteSpace(r.German) || string.IsNullOrWhiteSpace(r.English), r.Id));
    }

    [Fact]
    public void KeptInvoice_OnlyTheWarningAboutPaymentTerms()
    {
        // the invented invoice keeps every business rule — only it names neither due date nor payment terms (BR-CO-25, a warning); and the
        // schema of the profile EN16931 has no name of the document (its ram:Name stands for the further values of stage 2)
        Assert.Equal(["BR-CO-25", "PDFK-XSD-01"], Ids(Cii));
        var withTerms = Cii.Replace("<ram:SpecifiedTradeSettlementHeaderMonetarySummation>",
            "<ram:SpecifiedTradePaymentTerms><ram:Description>30 Tage netto</ram:Description></ram:SpecifiedTradePaymentTerms><ram:SpecifiedTradeSettlementHeaderMonetarySummation>", StringComparison.Ordinal);
        Assert.Equal(["PDFK-XSD-01"], Ids(withTerms));
    }

    [Fact]
    public void Missing_Terms()
    {
        Assert.Contains("BR-02", Ids(Cii.Replace("<ram:ID>RE-2026-0001</ram:ID>", "", StringComparison.Ordinal)));
        Assert.Contains("BR-06", Ids(Cii.Replace("<ram:Name>Musterfirma Beispiel GmbH</ram:Name>", "", StringComparison.Ordinal)));
        Assert.Contains("BR-25", Ids(Cii.Replace("<ram:Name>Musterartikel</ram:Name>", "", StringComparison.Ordinal)));
        Assert.Contains("BR-61", Ids(Cii.Replace("<ram:IBANID>DE02120300000000202051</ram:IBANID>", "", StringComparison.Ordinal)
            .Replace("<ram:IBANID>DE02500105170137075030</ram:IBANID>", "", StringComparison.Ordinal)));
    }

    [Fact]
    public void Calculations_OnTheXmlValues()
    {
        // lines 20.00 + 10.00 = 30.00; a wrong sum of lines, total and amount due
        var ids = Ids(Cii.Replace("<ram:LineTotalAmount>30.00</ram:LineTotalAmount>", "<ram:LineTotalAmount>31.00</ram:LineTotalAmount>", StringComparison.Ordinal)
            .Replace("<ram:DuePayableAmount>34.50</ram:DuePayableAmount>", "<ram:DuePayableAmount>30.00</ram:DuePayableAmount>", StringComparison.Ordinal));
        Assert.Contains("BR-CO-10", ids);
        Assert.Contains("BR-CO-13", ids);
        Assert.Contains("BR-CO-16", ids);
        // VAT 20.00 × 19 % = 3.80: less than one unit of the currency off is allowed, as both validation artefacts read it (rounded per
        // line); one unit is not, nor a missing tax amount; the sign does not count
        string Tax(string amount) => Cii.Replace("<ram:CalculatedAmount>3.80</ram:CalculatedAmount>", amount, StringComparison.Ordinal);
        Assert.DoesNotContain("BR-CO-17", Ids(Tax("<ram:CalculatedAmount>4.79</ram:CalculatedAmount>")));
        Assert.DoesNotContain("BR-CO-17", Ids(Tax("<ram:CalculatedAmount>-3.80</ram:CalculatedAmount>")));
        Assert.Contains("BR-CO-17", Ids(Tax("<ram:CalculatedAmount>4.80</ram:CalculatedAmount>")));
        Assert.Contains("BR-CO-17", Ids(Tax("")));
        Assert.Contains("BR-S-09", Ids(Tax("<ram:CalculatedAmount>4.80</ram:CalculatedAmount>")));
    }

    [Fact]
    public void Calculations_SumsRoundedToTheCent_WhereBothArtefactsRound()
    {
        // a line of 20.004: the sum of the lines rounds to 30.00 — BT-106 fits (BR-DEC-23 says what is wrong); the breakdown S 19 % (BR-S-08)
        // is compared exactly, as the standard writes it (the artefacts of CII and UBL differ there)
        var ids = Ids(Cii.Replace("<ram:LineTotalAmount>20.00</ram:LineTotalAmount></ram:SpecifiedTradeSettlementLineMonetarySummation>",
            "<ram:LineTotalAmount>20.004</ram:LineTotalAmount></ram:SpecifiedTradeSettlementLineMonetarySummation>", StringComparison.Ordinal));
        Assert.Contains("BR-DEC-23", ids);
        Assert.DoesNotContain("BR-CO-10", ids);
        Assert.Contains("BR-S-08", ids);
    }

    [Fact]
    public void MissingValues_DoNotFit()
    {
        // a line without a net price has none of zero or more (BR-27 beside BR-26), as both artefacts count it
        var noPrice = Ids(Cii.Replace("<ram:NetPriceProductTradePrice><ram:ChargeAmount>5.00</ram:ChargeAmount><ram:BasisQuantity unitCode=\"C62\">2</ram:BasisQuantity></ram:NetPriceProductTradePrice>",
            "", StringComparison.Ordinal));
        Assert.Contains("BR-26", noPrice);
        Assert.Contains("BR-27", noPrice);
        // a breakdown without taxable amount does not add up (08) and its tax does not fit (BR-CO-17, 09)
        var noBasis = Ids(Cii.Replace("<ram:BasisAmount>20.00</ram:BasisAmount>", "", StringComparison.Ordinal));
        Assert.Contains("BR-45", noBasis);
        Assert.Contains("BR-S-08", noBasis);
        Assert.Contains("BR-S-09", noBasis);
        Assert.Contains("BR-CO-17", noBasis);
        // the totals: without BT-109 the total with VAT is not confirmed (BR-CO-15), without BT-112 the amount due not (BR-CO-16)
        Assert.Contains("BR-CO-15", Ids(Cii.Replace("<ram:TaxBasisTotalAmount>30.00</ram:TaxBasisTotalAmount>", "", StringComparison.Ordinal)));
        Assert.Contains("BR-CO-16", Ids(Cii.Replace("<ram:GrandTotalAmount>34.50</ram:GrandTotalAmount>", "", StringComparison.Ordinal)));
        // the optional VAT total may be missing (BR-CO-14 only where it is given; BR-CO-15 counts it as zero and finds the difference)
        var noVatTotal = Ids(Cii.Replace("<ram:TaxTotalAmount currencyID=\"EUR\">4.50</ram:TaxTotalAmount>", "", StringComparison.Ordinal));
        Assert.DoesNotContain("BR-CO-14", noVatTotal);
        Assert.Contains("BR-CO-15", noVatTotal);
        // without the invoice currency the VAT total cannot be told apart: only BR-05, not BR-CO-14 or BR-CO-15
        var noCurrency = Ids(Cii.Replace("<ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode>", "", StringComparison.Ordinal));
        Assert.Contains("BR-05", noCurrency);
        Assert.DoesNotContain("BR-CO-14", noCurrency);
        Assert.DoesNotContain("BR-CO-15", noCurrency);
    }

    [Fact]
    public void AllowanceTotal_MayOnlyBeMissing_WithoutAllowances()
    {
        // a document level allowance of 0.00 without the sum of allowances (BT-107): both artefacts ask for the sum
        const string allowance = "<ram:SpecifiedTradeAllowanceCharge><ram:ChargeIndicator><udt:Indicator>false</udt:Indicator></ram:ChargeIndicator>"
            + "<ram:ActualAmount>0.00</ram:ActualAmount><ram:Reason>Rabatt</ram:Reason><ram:CategoryTradeTax><ram:TypeCode>VAT</ram:TypeCode>"
            + "<ram:CategoryCode>S</ram:CategoryCode><ram:RateApplicablePercent>19</ram:RateApplicablePercent></ram:CategoryTradeTax></ram:SpecifiedTradeAllowanceCharge>";
        var ids = Ids(Cii.Replace("<ram:SpecifiedTradeSettlementHeaderMonetarySummation>", allowance + "<ram:SpecifiedTradeSettlementHeaderMonetarySummation>", StringComparison.Ordinal));
        Assert.Contains("BR-CO-11", ids);
        Assert.DoesNotContain("BR-CO-11", Ids(Cii));
    }

    [Fact]
    public void TaxScheme_OnlyVatCounts()
    {
        // a line or breakdown whose tax element names no VAT (TypeCode) has no VAT category — as the syntax binding and both artefacts read it
        var line = Ids(Cii.Replace("<ram:ApplicableTradeTax><ram:TypeCode>VAT</ram:TypeCode><ram:CategoryCode>S</ram:CategoryCode><ram:RateApplicablePercent>7</ram:RateApplicablePercent>",
            "<ram:ApplicableTradeTax><ram:TypeCode>GST</ram:TypeCode><ram:CategoryCode>S</ram:CategoryCode><ram:RateApplicablePercent>7</ram:RateApplicablePercent>", StringComparison.Ordinal));
        Assert.Contains("BR-CO-04", line);
        var breakdown = Ids(Cii.Replace("<ram:CalculatedAmount>0.70</ram:CalculatedAmount><ram:TypeCode>VAT</ram:TypeCode>",
            "<ram:CalculatedAmount>0.70</ram:CalculatedAmount>", StringComparison.Ordinal));
        Assert.Contains("BR-47", breakdown);
        Assert.Contains("BR-48", breakdown);
        Assert.DoesNotContain("BR-CO-04", Ids(Cii.Replace("<ram:TypeCode>VAT</ram:TypeCode>", "<ram:TypeCode> vat </ram:TypeCode>", StringComparison.Ordinal)));
    }

    [Fact]
    public void CategoryRule01_FromTheItemsOnly()
    {
        // a breakdown „exempt“ of 0.00 without exempt items: the standard asks the breakdown only of the items (the artefacts of CII also
        // the other way round, those of UBL not) — nothing to find; with a taxable amount 08 finds it
        string Exempt(string basis) => Cii.Replace("<ram:SpecifiedTradeSettlementHeaderMonetarySummation>",
            $"<ram:ApplicableTradeTax><ram:CalculatedAmount>0.00</ram:CalculatedAmount><ram:TypeCode>VAT</ram:TypeCode><ram:ExemptionReason>Befreit</ram:ExemptionReason><ram:BasisAmount>{basis}</ram:BasisAmount>"
            + "<ram:CategoryCode>E</ram:CategoryCode><ram:RateApplicablePercent>0</ram:RateApplicablePercent></ram:ApplicableTradeTax><ram:SpecifiedTradeSettlementHeaderMonetarySummation>",
            StringComparison.Ordinal);
        Assert.DoesNotContain(Ids(Exempt("0.00")), id => id.StartsWith("BR-E-", StringComparison.Ordinal));
        var withAmount = Ids(Exempt("5.00"));
        Assert.Contains("BR-E-08", withAmount);
        Assert.DoesNotContain("BR-E-01", withAmount);
        // items S without a breakdown S: BR-S-01
        Assert.Contains("BR-S-01", Ids(Cii.Replace("<ram:CategoryCode>S</ram:CategoryCode><ram:TaxPointDate>", "<ram:CategoryCode>Z</ram:CategoryCode><ram:TaxPointDate>", StringComparison.Ordinal)));
    }

    [Fact]
    public void Narrowed_AsTheBindingsSay()
    {
        // BR-50 only for payment by credit transfer (30, 58), as both artefacts
        string Means(string code) => Regex.Replace(Cii,
            "<ram:TypeCode>58</ram:TypeCode>[ \r\n]*<ram:PayeePartyCreditorFinancialAccount><ram:IBANID>DE02500105170137075030</ram:IBANID></ram:PayeePartyCreditorFinancialAccount>",
            $"<ram:TypeCode>{code}</ram:TypeCode><ram:PayeePartyCreditorFinancialAccount><ram:AccountName>Konto</ram:AccountName></ram:PayeePartyCreditorFinancialAccount>");
        Assert.NotEqual(Cii, Means("58"));
        Assert.Contains("BR-50", Ids(Means("58")));
        Assert.DoesNotContain("BR-50", Ids(Means("42")));
        // BR-61 per payment means of its own, as both artefacts: the other transfer of code 58 with its IBAN does not hide the missing one
        Assert.Contains("BR-61", Ids(Means("58")));
        Assert.DoesNotContain("BR-61", Ids(Means("42")));
        // BR-52 only for a supporting document (type code 916 in CII)
        string Document(string type) => Regex.Replace(Cii, "<ram:IssuerAssignedID>Lieferschein-1</ram:IssuerAssignedID>[ \r\n]*<ram:TypeCode>916</ram:TypeCode>",
            $"<ram:TypeCode>{type}</ram:TypeCode>");
        Assert.NotEqual(Cii, Document("916"));
        Assert.Contains("BR-52", Ids(Document("916")));
        Assert.DoesNotContain("BR-52", Ids(Document("917")));
        // the tax representative's VAT identifier in CII only with the scheme VA
        string Representative(string scheme) => Cii.Replace("<ram:BuyerTradeParty>",
            $"<ram:SellerTaxRepresentativeTradeParty><ram:Name>Vertreter Beispiel</ram:Name><ram:PostalTradeAddress><ram:CountryID>DE</ram:CountryID></ram:PostalTradeAddress>"
            + $"<ram:SpecifiedTaxRegistration><ram:ID{scheme}>DE987654321</ram:ID></ram:SpecifiedTaxRegistration></ram:SellerTaxRepresentativeTradeParty><ram:BuyerTradeParty>",
            StringComparison.Ordinal);
        Assert.DoesNotContain("BR-56", Ids(Representative(" schemeID=\"VA\"")));
        Assert.Contains("BR-56", Ids(Representative("")));
    }

    [Fact]
    public void PatternsTheMutationTestDoesNotReach()
    {
        // what no base invoice of the mutation test brings (docs/PRUEFUNGEN.md): allowances of a category with their identifiers and
        // rates, a payment card, VAT point date and its code together
        static string Allowance(string category, string rate) =>
            "<ram:SpecifiedTradeAllowanceCharge><ram:ChargeIndicator><udt:Indicator>false</udt:Indicator></ram:ChargeIndicator>"
            + $"<ram:ActualAmount>0.00</ram:ActualAmount><ram:Reason>Rabatt</ram:Reason><ram:CategoryTradeTax><ram:TypeCode>VAT</ram:TypeCode>"
            + $"<ram:CategoryCode>{category}</ram:CategoryCode><ram:RateApplicablePercent>{rate}</ram:RateApplicablePercent></ram:CategoryTradeTax></ram:SpecifiedTradeAllowanceCharge>";
        string With(string allowance) =>
            Cii.Replace("<ram:SpecifiedTradeSettlementHeaderMonetarySummation>", allowance + "<ram:SpecifiedTradeSettlementHeaderMonetarySummation>", StringComparison.Ordinal);
        // an allowance of the standard rate without any identifier of the seller (S-03); of the zero rate with 19 % (Z-06)
        Assert.Contains("BR-S-03", Ids(With(Allowance("S", "19")).Replace("<ram:SpecifiedTaxRegistration><ram:ID schemeID=\"VA\">DE123456789</ram:ID></ram:SpecifiedTaxRegistration>", "", StringComparison.Ordinal)));
        Assert.DoesNotContain("BR-S-03", Ids(With(Allowance("S", "19"))));
        Assert.Contains("BR-Z-06", Ids(With(Allowance("Z", "19"))));
        Assert.DoesNotContain("BR-Z-06", Ids(With(Allowance("Z", "0"))));
        // the VAT point date (BT-7, in the breakdowns) and its code (BT-8) together
        Assert.Contains("BR-CO-03", Ids(Cii.Replace("<ram:CategoryCode>S</ram:CategoryCode><ram:TaxPointDate>",
            "<ram:DueDateTypeCode>5</ram:DueDateTypeCode><ram:CategoryCode>S</ram:CategoryCode><ram:TaxPointDate>", StringComparison.Ordinal)));
        // a payment card number of more than six digits (only the last digits may be given)
        Assert.Contains("BR-51", Ids(Cii.Replace("<ram:Information>Überweisung</ram:Information>",
            "<ram:Information>Überweisung</ram:Information><ram:ApplicableTradeSettlementFinancialCard><ram:ID>1234567890</ram:ID></ram:ApplicableTradeSettlementFinancialCard>",
            StringComparison.Ordinal)));
    }

    [Fact]
    public void EmptyPeriod_IsFound_AlthoughItLeavesNoGroup()
    {
        // an empty period element holds nothing to show, so the readable form has no group for it — BR-CO-19 is about exactly that (FG 836)
        Assert.Contains("BR-CO-19", Ids(Cii.Replace("<ram:PaymentReference>", "<ram:BillingSpecifiedPeriod/><ram:PaymentReference>", StringComparison.Ordinal)));
        Assert.DoesNotContain("BR-CO-19", Ids(Cii.Replace("<ram:PaymentReference>",
            "<ram:BillingSpecifiedPeriod><ram:StartDateTime><udt:DateTimeString format=\"102\">20261101</udt:DateTimeString></ram:StartDateTime></ram:BillingSpecifiedPeriod><ram:PaymentReference>",
            StringComparison.Ordinal)));
        Assert.Contains("BR-CO-20", Ids(Cii.Replace("<ram:SpecifiedTradeSettlementLineMonetarySummation><ram:LineTotalAmount>10.00",
            "<ram:BillingSpecifiedPeriod/><ram:SpecifiedTradeSettlementLineMonetarySummation><ram:LineTotalAmount>10.00", StringComparison.Ordinal)));
        // in UBL the period element also carries the VAT point date code (BT-8): with only that it is no invoicing period
        var ubl = InvoiceReaderTests.Ubl;
        Assert.DoesNotContain("BR-CO-19", Ids(ubl.Replace("<cbc:DocumentCurrencyCode>", "<cac:InvoicePeriod><cbc:DescriptionCode>3</cbc:DescriptionCode></cac:InvoicePeriod><cbc:DocumentCurrencyCode>", StringComparison.Ordinal)));
        Assert.Contains("BR-CO-19", Ids(ubl.Replace("<cbc:DocumentCurrencyCode>", "<cac:InvoicePeriod></cac:InvoicePeriod><cbc:DocumentCurrencyCode>", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void ManyBreakdownsAndLines_LinearNotQuadratic()
    {
        // a prepared invoice with 4000 lines and 4000 breakdowns of one category: the sums are added once (FG 832), not per breakdown
        const int count = 4000;
        var lines = new StringBuilder();
        var breakdowns = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            lines.Append("<ram:IncludedSupplyChainTradeLineItem><ram:AssociatedDocumentLineDocument><ram:LineID>").Append(i + 3)
                .Append("</ram:LineID></ram:AssociatedDocumentLineDocument><ram:SpecifiedTradeProduct><ram:Name>Teil</ram:Name></ram:SpecifiedTradeProduct>")
                .Append("<ram:SpecifiedLineTradeSettlement><ram:ApplicableTradeTax><ram:TypeCode>VAT</ram:TypeCode><ram:CategoryCode>S</ram:CategoryCode>")
                .Append("<ram:RateApplicablePercent>").Append(i % 50 + 1).Append("</ram:RateApplicablePercent></ram:ApplicableTradeTax>")
                .Append("<ram:SpecifiedTradeSettlementLineMonetarySummation><ram:LineTotalAmount>1.00</ram:LineTotalAmount></ram:SpecifiedTradeSettlementLineMonetarySummation>")
                .Append("</ram:SpecifiedLineTradeSettlement></ram:IncludedSupplyChainTradeLineItem>");
            breakdowns.Append("<ram:ApplicableTradeTax><ram:CalculatedAmount>0.01</ram:CalculatedAmount><ram:TypeCode>VAT</ram:TypeCode><ram:BasisAmount>1.00</ram:BasisAmount>")
                .Append("<ram:CategoryCode>S</ram:CategoryCode><ram:RateApplicablePercent>").Append(i % 50 + 1).Append("</ram:RateApplicablePercent></ram:ApplicableTradeTax>");
        }
        var xml = Cii.Replace("<ram:ApplicableHeaderTradeAgreement>", lines + "<ram:ApplicableHeaderTradeAgreement>", StringComparison.Ordinal)
            .Replace("<ram:SpecifiedTradeSettlementHeaderMonetarySummation>", breakdowns + "<ram:SpecifiedTradeSettlementHeaderMonetarySummation>", StringComparison.Ordinal);
        var data = InvoiceReaderTests.Read(xml);
        var watch = Stopwatch.StartNew();
        var violations = InvoiceRules.Check(data);
        watch.Stop();
        Assert.Contains(violations, v => v.Rule.Id == "BR-S-08");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5 * TestKind.Factor), $"{watch.Elapsed.TotalSeconds:F1} s");
    }

    [Fact]
    public void Values_SignPrefixDecimals()
    {
        Assert.Contains("BR-27", Ids(Cii.Replace("<ram:ChargeAmount>10.00</ram:ChargeAmount>", "<ram:ChargeAmount>-10.00</ram:ChargeAmount>", StringComparison.Ordinal)));
        Assert.Contains("BR-CO-09", Ids(Cii.Replace("schemeID=\"VA\">DE123456789", "schemeID=\"VA\">123456789", StringComparison.Ordinal)));
        Assert.Contains("BR-DEC-14", Ids(Cii.Replace("<ram:GrandTotalAmount>34.50</ram:GrandTotalAmount>", "<ram:GrandTotalAmount>34.500</ram:GrandTotalAmount>", StringComparison.Ordinal)));
    }

    [Fact]
    public void SplitPayment_NotBesideTheStandardRate()
    {
        var ids = Ids(Cii.Replace("<ram:CategoryCode>S</ram:CategoryCode><ram:RateApplicablePercent>7</ram:RateApplicablePercent>",
            "<ram:CategoryCode>B</ram:CategoryCode><ram:RateApplicablePercent>7</ram:RateApplicablePercent>", StringComparison.Ordinal));
        Assert.Contains("BR-B-01", ids);   // and not an Italian domestic invoice either
        Assert.Contains("BR-B-02", ids);
    }
}
