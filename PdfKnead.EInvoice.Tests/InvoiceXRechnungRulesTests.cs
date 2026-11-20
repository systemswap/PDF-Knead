using System.Text.RegularExpressions;
using PdfKnead.Tests;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// Stage 4, part d1: the rules of XRechnung 3.0 — BR-DE of the specification and the rules taken over from Peppol BIS Billing 3.0 —
/// only for an invoice whose specification identifier is that of XRechnung; each rule finds what it is about and nothing in an invoice that
/// keeps it. Only invented invoices and names.
/// </summary>
public sealed class InvoiceXRechnungRulesTests
{
    private const string Cius = "urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0";

    /// <summary>The invented CII invoice as an XRechnung that keeps every rule: process, seller contact, electronic addresses, an invoicing period.</summary>
    internal static string Kept => InvoiceReaderTests.Cii
        .Replace("<ram:GuidelineSpecifiedDocumentContextParameter><ram:ID>urn:cen.eu:en16931:2017</ram:ID></ram:GuidelineSpecifiedDocumentContextParameter>",
            "<ram:BusinessProcessSpecifiedDocumentContextParameter><ram:ID>urn:fdc:peppol.eu:2017:poacc:billing:01:1.0</ram:ID></ram:BusinessProcessSpecifiedDocumentContextParameter>"
            + $"<ram:GuidelineSpecifiedDocumentContextParameter><ram:ID>{Cius}</ram:ID></ram:GuidelineSpecifiedDocumentContextParameter>", StringComparison.Ordinal)
        // the gross price 12.00 with its discount of 2.00 to the net price 10.00 (PEPPOL-EN16931-R046)
        .Replace("<ram:GrossPriceProductTradePrice><ram:ChargeAmount>12.00</ram:ChargeAmount><ram:BasisQuantity unitCode=\"H87\">1</ram:BasisQuantity>",
            "<ram:GrossPriceProductTradePrice><ram:ChargeAmount>12.00</ram:ChargeAmount><ram:BasisQuantity unitCode=\"H87\">1</ram:BasisQuantity>"
            + "<ram:AppliedTradeAllowanceCharge><ram:ChargeIndicator><udt:Indicator>false</udt:Indicator></ram:ChargeIndicator><ram:ActualAmount>2.00</ram:ActualAmount></ram:AppliedTradeAllowanceCharge>",
            StringComparison.Ordinal)
        .Replace("<ram:PostalTradeAddress><ram:PostcodeCode>12345</ram:PostcodeCode>",
            "<ram:DefinedTradeContact><ram:PersonName>Erika Mustermann</ram:PersonName><ram:TelephoneUniversalCommunication><ram:CompleteNumber>+49 30 1234567</ram:CompleteNumber>"
            + "</ram:TelephoneUniversalCommunication><ram:EmailURIUniversalCommunication><ram:URIID>rechnung@beispiel.example</ram:URIID></ram:EmailURIUniversalCommunication></ram:DefinedTradeContact>"
            + "<ram:PostalTradeAddress><ram:PostcodeCode>12345</ram:PostcodeCode>", StringComparison.Ordinal)
        .Replace("<ram:SpecifiedTaxRegistration><ram:ID schemeID=\"VA\">DE123456789</ram:ID></ram:SpecifiedTaxRegistration>",
            "<ram:URIUniversalCommunication><ram:URIID schemeID=\"EM\">verkauf@beispiel.example</ram:URIID></ram:URIUniversalCommunication>"
            + "<ram:SpecifiedTaxRegistration><ram:ID schemeID=\"VA\">DE123456789</ram:ID></ram:SpecifiedTaxRegistration>", StringComparison.Ordinal)
        .Replace("<ram:PostalTradeAddress><ram:PostcodeCode>54321</ram:PostcodeCode><ram:CityName>Beispielstadt</ram:CityName><ram:CountryID>DE</ram:CountryID></ram:PostalTradeAddress>",
            "<ram:PostalTradeAddress><ram:PostcodeCode>54321</ram:PostcodeCode><ram:CityName>Beispielstadt</ram:CityName><ram:CountryID>DE</ram:CountryID></ram:PostalTradeAddress>"
            + "<ram:URIUniversalCommunication><ram:URIID schemeID=\"EM\">einkauf@beispiel.example</ram:URIID></ram:URIUniversalCommunication>", StringComparison.Ordinal)
        // the date of the service (BR-DE-TMP-32)
        .Replace("<ram:SpecifiedTradeSettlementHeaderMonetarySummation>",
            "<ram:BillingSpecifiedPeriod><ram:StartDateTime><udt:DateTimeString format=\"102\">20261101</udt:DateTimeString></ram:StartDateTime></ram:BillingSpecifiedPeriod>"
            + "<ram:SpecifiedTradeSettlementHeaderMonetarySummation>", StringComparison.Ordinal);

    private static IReadOnlyList<InvoiceRuleViolation> Rules(string xml) =>
        [.. InvoiceRules.Check(InvoiceReaderTests.Read(xml)).Where(v => v.Rule.Source == InvoiceRules.XRechnungSource)];

    private static string[] Ids(string xml) => [.. Rules(xml).Select(v => v.Rule.Id).Distinct()];

    private static string Change(string xml, string from, string to)
    {
        Assert.Contains(from, xml, StringComparison.Ordinal);
        return xml.Replace(from, to, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalogue_BrDeAndPeppol()
    {
        var expected = new List<string>();
        foreach (var n in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 14, 15, 16, 17, 18, 19, 20, 21, 22, 26, 27, 28, 30, 31 }) expected.Add($"BR-DE-{n}");
        foreach (var n in new[] { 23, 24, 25 }) expected.AddRange([$"BR-DE-{n}-a", $"BR-DE-{n}-b"]);
        foreach (var n in new[] { 1, 5, 8, 10, 20, 40, 41, 42, 43, 44, 46, 53, 54, 55, 61, 101, 110, 111, 120, 121, 130 }) expected.Add($"PEPPOL-EN16931-R{n:000}");
        // the rules of part d1 (those of the extension, the temporary ones and those of CVD: InvoiceXRechnungExtensionRulesTests)
        var rules = InvoiceRules.All.Where(r => r.Source == InvoiceRules.XRechnungSource && !r.Id.Split('-').Any(p => p is "DEX" or "TMP" or "CVD")).ToList();
        Assert.Equal(expected.Order(StringComparer.Ordinal), rules.Select(r => r.Id).Order(StringComparer.Ordinal));
        Assert.Equal(["BR-DE-17", "BR-DE-19", "BR-DE-20", "BR-DE-21", "BR-DE-26", "BR-DE-27", "BR-DE-28", "PEPPOL-EN16931-R120"],
            rules.Where(r => r.Severity == InvoiceRuleSeverity.Warning).Select(r => r.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OnlyForXRechnung_AndTheKeptInvoiceKeepsThem()
    {
        Assert.Empty(Rules(InvoiceReaderTests.Cii));   // EN 16931 only: no XRechnung rule
        Assert.Empty(Rules(Kept));
        // without the additions the XRechnung asks for: contact, process, electronic addresses, date of the service
        var bare = Ids(InvoiceReaderTests.Cii.Replace("<ram:ID>urn:cen.eu:en16931:2017</ram:ID>", $"<ram:ID>{Cius}</ram:ID>", StringComparison.Ordinal));
        Assert.Equal(["BR-DE-2", "BR-DE-TMP-32", "PEPPOL-EN16931-R001", "PEPPOL-EN16931-R010", "PEPPOL-EN16931-R020", "PEPPOL-EN16931-R046"],
            bare.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NationalRules_BrDe()
    {
        Assert.Contains("BR-DE-15", Ids(Change(Kept, "<ram:BuyerReference>04011000-12345-34</ram:BuyerReference>", "")));
        var type = Rules(Change(Kept, "<ram:TypeCode>380</ram:TypeCode>", "<ram:TypeCode>393</ram:TypeCode>"));
        Assert.Equal(InvoiceRuleSeverity.Warning, Assert.Single(type, v => v.Rule.Id == "BR-DE-17").Rule.Severity);
        // the IBAN of a SEPA transfer with a wrong check digit
        Assert.Contains("BR-DE-19", Ids(Change(Kept, "<ram:IBANID>DE02120300000000202051</ram:IBANID>", "<ram:IBANID>DE03120300000000202051</ram:IBANID>")));
        Assert.Contains("BR-DE-27", Ids(Change(Kept, "+49 30 1234567", "ab")));
        Assert.Contains("BR-DE-28", Ids(Change(Kept, "rechnung@beispiel.example", "rechnung@beispiel")));
        // two attachments of the same name, only the case differs
        var twice = Change(Kept, "</ram:ApplicableHeaderTradeAgreement>",
            "<ram:AdditionalReferencedDocument><ram:IssuerAssignedID>Lieferschein-2</ram:IssuerAssignedID><ram:TypeCode>916</ram:TypeCode>"
            + "<ram:AttachmentBinaryObject mimeCode=\"application/pdf\" filename=\"LIEFERSCHEIN.PDF\">JVBERi0=</ram:AttachmentBinaryObject></ram:AdditionalReferencedDocument></ram:ApplicableHeaderTradeAgreement>");
        Assert.Contains("BR-DE-22", Ids(twice));
    }

    [Fact]
    public void CashDiscount_FormAndLineEnd()
    {
        string Terms(string text) => Change(Kept, "<ram:SpecifiedTradeSettlementHeaderMonetarySummation>",
            $"<ram:SpecifiedTradePaymentTerms><ram:Description>{text}</ram:Description></ram:SpecifiedTradePaymentTerms><ram:SpecifiedTradeSettlementHeaderMonetarySummation>");
        Assert.DoesNotContain("BR-DE-18", Ids(Terms("#SKONTO#TAGE=14#PROZENT=2.00#\n")));
        Assert.DoesNotContain("BR-DE-18", Ids(Terms("Zahlbar in 30 Tagen")));
        Assert.Contains("BR-DE-18", Ids(Terms("#SKONTO#TAGE=14#PROZENT=2.00#")));     // no line end after it
        Assert.Contains("BR-DE-18", Ids(Terms("#SKONTO#TAGE=14#PROZENT=2#\n")));      // two decimals asked
        Assert.Contains("BR-DE-18", Ids(Terms("#skonto#TAGE=14#PROZENT=2.00#\n")));   // capital letters asked
    }

    [Fact]
    public void PaymentMeans_EachWithItsOwnGroup()
    {
        // a SEPA transfer that also carries a card; a direct debit without mandate
        Assert.Contains("BR-DE-23-b", Ids(Change(Kept, "<ram:Information>Überweisung</ram:Information>",
            "<ram:Information>Überweisung</ram:Information><ram:ApplicableTradeSettlementFinancialCard><ram:ID>1234</ram:ID></ram:ApplicableTradeSettlementFinancialCard>")));
        var debit = Ids(Regex.Replace(Kept, "<ram:TypeCode>58</ram:TypeCode>[ \r\n]*<ram:PayeePartyCreditorFinancialAccount><ram:IBANID>DE02500105170137075030</ram:IBANID></ram:PayeePartyCreditorFinancialAccount>",
            "<ram:TypeCode>59</ram:TypeCode>"));
        Assert.Contains("BR-DE-25-a", debit);
        Assert.Contains("PEPPOL-EN16931-R061", debit);
        // a direct debit with mandate and debited account, but without the creditor identifier (BR-DE-30) — and its IBAN wrong (BR-DE-20)
        string Debit(string account, bool creditor) => Regex.Replace(Kept,
                "<ram:TypeCode>58</ram:TypeCode>[ \r\n]*<ram:PayeePartyCreditorFinancialAccount><ram:IBANID>DE02500105170137075030</ram:IBANID></ram:PayeePartyCreditorFinancialAccount>",
                $"<ram:TypeCode>59</ram:TypeCode><ram:PayerPartyDebtorFinancialAccount><ram:IBANID>{account}</ram:IBANID></ram:PayerPartyDebtorFinancialAccount>")
            .Replace("<ram:PaymentReference>RE-2026-0001</ram:PaymentReference>",
                (creditor ? "<ram:CreditorReferenceID>DE98ZZZ09999999999</ram:CreditorReferenceID>" : "") + "<ram:PaymentReference>RE-2026-0001</ram:PaymentReference>", StringComparison.Ordinal)
            .Replace("<ram:SpecifiedTradeSettlementHeaderMonetarySummation>",
                "<ram:SpecifiedTradePaymentTerms><ram:DirectDebitMandateID>MANDAT-1</ram:DirectDebitMandateID></ram:SpecifiedTradePaymentTerms><ram:SpecifiedTradeSettlementHeaderMonetarySummation>",
                StringComparison.Ordinal);
        Assert.DoesNotContain(Ids(Debit("DE02500105170137075030", creditor: true)),
            id => id is "BR-DE-20" or "BR-DE-25-a" or "BR-DE-25-b" or "BR-DE-30" or "BR-DE-31" or "PEPPOL-EN16931-R061");
        Assert.Contains("BR-DE-30", Ids(Debit("DE02500105170137075030", creditor: false)));
        Assert.Contains("BR-DE-20", Ids(Debit("DE03500105170137075030", creditor: true)));
        // the SEPA transfers of the invoice now carry a direct debit beside them: not allowed (BR-DE-23-b)
        Assert.Contains("BR-DE-23-b", Ids(Debit("DE02500105170137075030", creditor: true)));
        // a card payment without card information
        Assert.Contains("BR-DE-24-a", Ids(Regex.Replace(Kept,
            "<ram:TypeCode>58</ram:TypeCode>[ \r\n]*<ram:PayeePartyCreditorFinancialAccount><ram:IBANID>DE02500105170137075030</ram:IBANID></ram:PayeePartyCreditorFinancialAccount>",
            "<ram:TypeCode>48</ram:TypeCode>")));
    }

    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void PreparedInvoice_LinearAndListLimited()
    {
        // 20 000 SEPA transfers and 20 000 payment terms: the direct debit of the settlement is looked up once, not per payment means (FG 838)
        var means = new System.Text.StringBuilder();
        var terms = new System.Text.StringBuilder();
        for (var i = 0; i < 20000; i++)
        {
            means.Append("<ram:SpecifiedTradeSettlementPaymentMeans><ram:TypeCode>58</ram:TypeCode><ram:PayeePartyCreditorFinancialAccount><ram:IBANID>DE02120300000000202051</ram:IBANID>")
                .Append("</ram:PayeePartyCreditorFinancialAccount></ram:SpecifiedTradeSettlementPaymentMeans>");
            terms.Append("<ram:SpecifiedTradePaymentTerms><ram:Description>Zahlbar sofort</ram:Description></ram:SpecifiedTradePaymentTerms>");
        }
        var many = Change(Kept, "<ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode>", "<ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode>" + means)
            .Replace("<ram:SpecifiedTradeSettlementHeaderMonetarySummation>", terms + "<ram:SpecifiedTradeSettlementHeaderMonetarySummation>", StringComparison.Ordinal);
        var data = InvoiceReaderTests.Read(many);
        InvoiceRules.PrepareSchemas();   // every schema compiled before the clock runs: the check is measured, not the first compiling
        var watch = System.Diagnostics.Stopwatch.StartNew();
        InvoiceRules.Check(data);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3 * TestKind.Factor), $"{watch.Elapsed.TotalSeconds:F1} s");
        // 500 empty elements: 100 listed, the last of them counts the 400 others
        var empty = Change(Kept, "<ram:Name>Musterartikel</ram:Name>", "<ram:Name>Musterartikel</ram:Name>" + string.Concat(Enumerable.Repeat("<ram:Description/>", 500)));
        var listed = Rules(empty).Where(v => v.Rule.Id == "PEPPOL-EN16931-R008").ToList();
        Assert.Equal(InvoiceRules.MaxListedPerRule, listed.Count);
        Assert.Equal(400, listed[^1].Further);
        Assert.All(listed.SkipLast(1), v => Assert.Equal(0, v.Further));
    }

    [Fact]
    public void CorrectedInvoice_AsksForThePrecedingOne()
    {
        var corrected = Rules(Change(Kept, "<ram:TypeCode>380</ram:TypeCode>", "<ram:TypeCode>384</ram:TypeCode>"));
        Assert.Equal(InvoiceRuleSeverity.Warning, Assert.Single(corrected, v => v.Rule.Id == "BR-DE-26").Rule.Severity);
        Assert.DoesNotContain(Ids(Change(Kept, "<ram:TypeCode>380</ram:TypeCode>", "<ram:TypeCode>384</ram:TypeCode>").Replace("<ram:PaymentReference>",
            "<ram:InvoiceReferencedDocument><ram:IssuerAssignedID>RE-2026-0000</ram:IssuerAssignedID></ram:InvoiceReferencedDocument><ram:PaymentReference>", StringComparison.Ordinal)),
            id => id == "BR-DE-26");
    }

    [Fact]
    public void PeppolRules()
    {
        Assert.Contains("PEPPOL-EN16931-R005", Ids(Change(Kept, "<ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode>",
            "<ram:TaxCurrencyCode>EUR</ram:TaxCurrencyCode><ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode>")));
        Assert.Contains("PEPPOL-EN16931-R008", Ids(Change(Kept, "<ram:Name>Musterartikel</ram:Name>", "<ram:Name>Musterartikel</ram:Name><ram:Description/>")));
        Assert.DoesNotContain("PEPPOL-EN16931-R008", Ids(Kept));   // the empty delivery is allowed
        // an attachment without content is empty too, written open and closed or as one tag (FG 849)
        const string data = ">JVBERi0=</ram:AttachmentBinaryObject>";
        Assert.Contains("PEPPOL-EN16931-R008", Ids(Change(Kept, data, "></ram:AttachmentBinaryObject>")));
        Assert.Contains("PEPPOL-EN16931-R008", Ids(Change(Kept, data, ">  </ram:AttachmentBinaryObject>")));
        Assert.Contains("PEPPOL-EN16931-R008", Ids(Change(Kept, data, "/>")));
        static string Allowance(string inner) => "<ram:SpecifiedTradeAllowanceCharge><ram:ChargeIndicator><udt:Indicator>false</udt:Indicator></ram:ChargeIndicator>"
            + inner + "<ram:Reason>Rabatt</ram:Reason><ram:CategoryTradeTax><ram:TypeCode>VAT</ram:TypeCode><ram:CategoryCode>S</ram:CategoryCode>"
            + "<ram:RateApplicablePercent>19</ram:RateApplicablePercent></ram:CategoryTradeTax></ram:SpecifiedTradeAllowanceCharge>";
        string With(string allowance) => Kept.Replace("<ram:SpecifiedTradeSettlementHeaderMonetarySummation>", allowance + "<ram:SpecifiedTradeSettlementHeaderMonetarySummation>", StringComparison.Ordinal);
        Assert.Contains("PEPPOL-EN16931-R040", Ids(With(Allowance("<ram:CalculationPercent>10</ram:CalculationPercent><ram:BasisAmount>100.00</ram:BasisAmount><ram:ActualAmount>9.00</ram:ActualAmount>"))));
        Assert.DoesNotContain("PEPPOL-EN16931-R040", Ids(With(Allowance("<ram:CalculationPercent>10</ram:CalculationPercent><ram:BasisAmount>100.00</ram:BasisAmount><ram:ActualAmount>10.01</ram:ActualAmount>"))));
        Assert.Contains("PEPPOL-EN16931-R041", Ids(With(Allowance("<ram:CalculationPercent>10</ram:CalculationPercent><ram:ActualAmount>9.00</ram:ActualAmount>"))));
        Assert.Contains("PEPPOL-EN16931-R042", Ids(With(Allowance("<ram:BasisAmount>100.00</ram:BasisAmount><ram:ActualAmount>9.00</ram:ActualAmount>"))));
        Assert.Contains("PEPPOL-EN16931-R043", Ids(With(Allowance("<ram:ActualAmount>9.00</ram:ActualAmount>").Replace("<udt:Indicator>false</udt:Indicator>", "<udt:Indicator>0</udt:Indicator>", StringComparison.Ordinal))));
        // the price: gross 12.00 with a discount of 1.00 is not the net price 10.00
        Assert.Contains("PEPPOL-EN16931-R046", Ids(Change(Kept, "<ram:ActualAmount>2.00</ram:ActualAmount></ram:AppliedTradeAllowanceCharge>",
            "<ram:ActualAmount>1.00</ram:ActualAmount></ram:AppliedTradeAllowanceCharge>")));
        // a base quantity of zero; a base quantity in another unit than the invoiced quantity
        Assert.Contains("PEPPOL-EN16931-R121", Ids(Change(Kept, "<ram:BasisQuantity unitCode=\"C62\">2</ram:BasisQuantity>", "<ram:BasisQuantity unitCode=\"C62\">0</ram:BasisQuantity>")));
        Assert.Contains("PEPPOL-EN16931-R130", Ids(Change(Kept, "<ram:BasisQuantity unitCode=\"C62\">2</ram:BasisQuantity>", "<ram:BasisQuantity unitCode=\"H87\">2</ram:BasisQuantity>")));
    }
}
