namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// Stage 4, part e1: the business rules per profile of ZUGFeRD / Factur-X — an invoice of MINIMUM, BASIC WL or BASIC is checked by the rules
/// its profile lists (specification Factur-X 1.09.2 / ZUGFeRD 2.5.2, Zuordnung/profile.tsv), EN16931, XRechnung and UBL by all as before.
/// Only invented invoices and names.
/// </summary>
public sealed class InvoiceProfileRulesTests
{
    private const string En16931 = "urn:cen.eu:en16931:2017";

    private static string As(string id) => Change(InvoiceReaderTests.Cii, $"<ram:ID>{En16931}</ram:ID>", $"<ram:ID>{id}</ram:ID>");

    private static string[] Ids(string xml) => [.. InvoiceRules.Check(InvoiceReaderTests.Read(xml)).Select(v => v.Rule.Id).Distinct()];

    private static string Change(string xml, string from, string to)
    {
        Assert.Contains(from, xml, StringComparison.Ordinal);
        return xml.Replace(from, to, StringComparison.Ordinal);
    }

    [Fact]
    public void Table_EveryRuleOfTheStandardHasItsProfiles()
    {
        foreach (var rule in InvoiceRules.All.Where(r => r.Source is InvoiceRules.Source or InvoiceRules.CodeSource))
        {
            var profiles = InvoiceRules.ProfilesOf(rule.Id);
            Assert.True(profiles.Count > 0, $"{rule.Id} fehlt in profile.tsv");
            Assert.Contains(InvoiceProfile.En16931, profiles);   // the profile EN16931 is the whole standard
        }
        // as the specification lists them: the invoice number everywhere, lines not in MINIMUM and BASIC WL, the code lists from BASIC WL on
        Assert.Equal([InvoiceProfile.Minimum, InvoiceProfile.BasicWl, InvoiceProfile.Basic, InvoiceProfile.En16931, InvoiceProfile.Extended], InvoiceRules.ProfilesOf("BR-02"));
        Assert.DoesNotContain(InvoiceProfile.BasicWl, InvoiceRules.ProfilesOf("BR-16"));
        Assert.DoesNotContain(InvoiceProfile.Minimum, InvoiceRules.ProfilesOf("BR-CL-04"));
        Assert.Contains(InvoiceProfile.BasicWl, InvoiceRules.ProfilesOf("BR-CL-04"));
        Assert.DoesNotContain(InvoiceProfile.Basic, InvoiceRules.ProfilesOf("BR-52"));
        // IGIC and IPSI under the identifiers PDF-Knead uses (the specification writes BR-AF and BR-AG)
        Assert.NotEmpty(InvoiceRules.ProfilesOf("BR-IG-01"));
        Assert.Empty(InvoiceRules.ProfilesOf("BR-AF-01"));
    }

    [Fact]
    public void Minimum_OnlyItsRules()
    {
        // without lines: a finding of EN 16931 (BR-16), none in MINIMUM, which has no lines by design
        var noLines = System.Text.RegularExpressions.Regex.Replace(InvoiceReaderTests.Cii, "<ram:IncludedSupplyChainTradeLineItem>.*?</ram:IncludedSupplyChainTradeLineItem>", "",
            System.Text.RegularExpressions.RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        Assert.Contains("BR-16", Ids(noLines));
        var minimum = Change(noLines, $"<ram:ID>{En16931}</ram:ID>", "<ram:ID>urn:factur-x.eu:1p0:minimum</ram:ID>");
        Assert.DoesNotContain("BR-16", Ids(minimum));
        // of the rules of the standard only those MINIMUM lists (the rules of PDF-Knead itself belong to no table — its schema finds the invoice
        // of the standard is no MINIMUM invoice, the hints say what MINIMUM does not use)
        Assert.DoesNotContain(Ids(minimum), id => !id.StartsWith("PDFK-", StringComparison.Ordinal) && !InvoiceRules.ProfilesOf(id).Contains(InvoiceProfile.Minimum));
        // what MINIMUM asks it still asks: the invoice number; a code of no list only from BASIC WL on
        Assert.Contains("BR-02", Ids(Change(minimum, "<ram:ID>RE-2026-0001</ram:ID>", "")));
        Assert.DoesNotContain("BR-CL-04", Ids(Change(minimum, "<ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode>", "<ram:InvoiceCurrencyCode>QQQ</ram:InvoiceCurrencyCode>")));
        // the form of ZUGFeRD 2.0 is MINIMUM as well
        Assert.DoesNotContain("BR-16", Ids(Change(noLines, $"<ram:ID>{En16931}</ram:ID>", "<ram:ID>urn:zugferd.de:2p0:minimum</ram:ID>")));
    }

    [Fact]
    public void BasicWl_NoLineRules_CodeListsYes()
    {
        const string lineId = "<ram:AssociatedDocumentLineDocument><ram:LineID>1</ram:LineID></ram:AssociatedDocumentLineDocument>";
        Assert.Contains("BR-21", Ids(Change(InvoiceReaderTests.Cii, lineId, "")));
        Assert.DoesNotContain("BR-21", Ids(Change(As("urn:factur-x.eu:1p0:basicwl"), lineId, "")));
        Assert.Contains("BR-CL-04", Ids(Change(As("urn:factur-x.eu:1p0:basicwl"), "<ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode>",
            "<ram:InvoiceCurrencyCode>QQQ</ram:InvoiceCurrencyCode>")));
        // no lines and no VAT breakdown: in EN 16931 BR-16 says what is wrong; BASIC WL has no lines by design, there BR-CO-18 asks for the
        // breakdown all the same
        static string Without(string xml, string element) => System.Text.RegularExpressions.Regex.Replace(xml, $"<ram:{element}>.*?</ram:{element}>", "",
            System.Text.RegularExpressions.RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        var bare = Without(Without(InvoiceReaderTests.Cii, "IncludedSupplyChainTradeLineItem"), "ApplicableTradeTax");
        Assert.DoesNotContain("BR-CO-18", Ids(bare));
        Assert.Contains("BR-CO-18", Ids(Change(bare, $"<ram:ID>{En16931}</ram:ID>", "<ram:ID>urn:factur-x.eu:1p0:basicwl</ram:ID>")));
    }

    [Fact]
    public void Basic_WithoutBr51And52_EnAndXRechnungAsBefore()
    {
        // a supporting document without its reference: BR-52 in EN 16931, not in BASIC (the profile has no supporting documents)
        var noReference = Change(InvoiceReaderTests.Cii, "<ram:IssuerAssignedID>Lieferschein-1</ram:IssuerAssignedID>", "");
        Assert.Contains("BR-52", Ids(noReference));
        Assert.DoesNotContain("BR-52", Ids(Change(noReference, $"<ram:ID>{En16931}</ram:ID>", "<ram:ID>urn:cen.eu:en16931:2017#compliant#urn:factur-x.eu:1p0:basic</ram:ID>")));
        // the form of Factur-X 1.0 is BASIC as well
        Assert.DoesNotContain("BR-52", Ids(Change(noReference, $"<ram:ID>{En16931}</ram:ID>", "<ram:ID>urn:cen.eu:en16931:2017:compliant:factur-x.eu:1p0:basic</ram:ID>")));
        // XRechnung and UBL: every rule as before
        Assert.Contains("BR-52", Ids(Change(noReference, $"<ram:ID>{En16931}</ram:ID>", "<ram:ID>urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0</ram:ID>")));
    }
}
