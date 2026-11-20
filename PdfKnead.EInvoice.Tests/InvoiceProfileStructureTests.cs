using PdfKnead.Tests;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// Stage 4, part e2: the element rules of the profiles of ZUGFeRD / Factur-X (FX-SCH-A) — how often an element may stand, which attributes a
/// profile requires, which codes it takes; per pattern only the first rule whose context fits, only for a CII invoice of its profile.
/// Only invented invoices and names.
/// </summary>
public sealed class InvoiceProfileStructureTests
{
    private const string En16931 = "urn:cen.eu:en16931:2017";

    private static string As(string id) => Change(InvoiceReaderTests.Cii, $"<ram:ID>{En16931}</ram:ID>", $"<ram:ID>{id}</ram:ID>");

    private static string Minimum => As("urn:factur-x.eu:1p0:minimum");

    private static IReadOnlyList<InvoiceRuleViolation> Structure(string xml) =>
        [.. InvoiceRules.Check(InvoiceReaderTests.Read(xml)).Where(v => v.Rule.Source == InvoiceRules.ProfileSource)];

    private static string[] Ids(string xml) => [.. Structure(xml).Select(v => v.Rule.Id).Distinct()];

    private static string Change(string xml, string from, string to)
    {
        Assert.Contains(from, xml, StringComparison.Ordinal);
        return xml.Replace(from, to, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalogue_OneRulePerIdentifier()
    {
        // our own hint that the walk was ended (too costly) stands beside them, as a warning
        Assert.Equal(InvoiceRuleSeverity.Warning, Assert.Single(InvoiceRules.All, r => r.Id == "PDFK-FX-01").Severity);
        var rules = InvoiceRules.All.Where(r => r.Id.StartsWith("FX-SCH-A-", StringComparison.Ordinal)).ToList();
        // 37 + 137 + 175 + 218 + 718 checks of the five profiles, 899 identifiers (the profiles share some)
        Assert.Equal(899, rules.Count);
        Assert.All(rules, r => Assert.Matches("^FX-SCH-A-[0-9]{6}$", r.Id));
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
        Assert.All(rules, r => Assert.False(string.IsNullOrWhiteSpace(r.German) || string.IsNullOrWhiteSpace(r.English)));
        // the artefacts flag none of them as a warning
        Assert.All(rules, r => Assert.Equal(InvoiceRuleSeverity.Fatal, r.Severity));
        // after the rules of Peppol
        var ids = InvoiceRules.All.Select(r => r.Id).ToList();
        Assert.True(ids.IndexOf("PEPPOL-EN16931-R130") < ids.IndexOf("FX-SCH-A-000019"));
    }

    [Fact]
    public void Minimum_CountsAttributesCodes()
    {
        Assert.Empty(Structure(Minimum));
        // the invoice type code twice: once is asked; and the second one is no code of the list of MINIMUM
        var twice = Structure(Change(Minimum, "<ram:TypeCode>380</ram:TypeCode>", "<ram:TypeCode>380</ram:TypeCode><ram:TypeCode>999</ram:TypeCode>"));
        Assert.Equal(["FX-SCH-A-000020", "FX-SCH-A-000023"], twice.Select(v => v.Rule.Id).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal("999", Assert.Single(twice, v => v.Rule.Id == "FX-SCH-A-000023").Value);
        // the date without its format attribute; another format than 102
        const string date = "<udt:DateTimeString format=\"102\">20261113</udt:DateTimeString>";
        Assert.Contains("FX-SCH-A-000021", Ids(Change(Minimum, date, "<udt:DateTimeString>20261113</udt:DateTimeString>")));
        Assert.Equal("610", Assert.Single(Structure(Change(Minimum, date, "<udt:DateTimeString format=\"610\">20261113</udt:DateTimeString>")),
            v => v.Rule.Id == "FX-SCH-A-000022").Value);
        // a variant: the VAT registration of the seller at most once (the rule asks the registrations with scheme VA only)
        const string vat = "<ram:SpecifiedTaxRegistration><ram:ID schemeID=\"VA\">DE123456789</ram:ID></ram:SpecifiedTaxRegistration>";
        Assert.Contains("FX-SCH-A-000033", Ids(Change(Minimum, vat, vat + vat)));
        Assert.DoesNotContain("FX-SCH-A-000033", Ids(Change(Minimum, vat, vat + vat.Replace("VA", "FC", StringComparison.Ordinal))));
    }

    [Fact]
    public void OnlyForItsProfile()
    {
        const string type = "<ram:TypeCode>380</ram:TypeCode>";
        // the same wrong code: in MINIMUM its rule, in EN16931 the rule of that profile, outside the profiles none
        Assert.Contains("FX-SCH-A-000023", Ids(Change(Minimum, type, "<ram:TypeCode>999</ram:TypeCode>")));
        Assert.Contains("FX-SCH-A-000555", Ids(Change(InvoiceReaderTests.Cii, type, "<ram:TypeCode>999</ram:TypeCode>")));
        Assert.DoesNotContain(Ids(Change(InvoiceReaderTests.Cii, type, "<ram:TypeCode>999</ram:TypeCode>")), id => id == "FX-SCH-A-000023");
        Assert.Empty(Structure(Change(As("urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0"), type, "<ram:TypeCode>999</ram:TypeCode>")));
        Assert.Empty(Structure(InvoiceReaderTests.Ubl));
    }

    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void PreparedInvoice_Linear()
    {
        // 5 000 lines in the profile EN16931: one walk, each element found by its path
        var line = InvoiceReaderTests.Cii[InvoiceReaderTests.Cii.IndexOf("<ram:IncludedSupplyChainTradeLineItem>", StringComparison.Ordinal)..
            (InvoiceReaderTests.Cii.IndexOf("</ram:IncludedSupplyChainTradeLineItem>", StringComparison.Ordinal) + "</ram:IncludedSupplyChainTradeLineItem>".Length)];
        var many = InvoiceReaderTests.Cii.Replace(line, string.Concat(Enumerable.Repeat(line, 5000)), StringComparison.Ordinal);
        var data = InvoiceReaderTests.Read(many);
        InvoiceRules.PrepareSchemas();   // every schema compiled before the clock runs: the check is measured, not the first compiling
        var watch = System.Diagnostics.Stopwatch.StartNew();
        InvoiceRules.Check(data);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3 * TestKind.Factor), $"{watch.Elapsed.TotalSeconds:F1} s");
    }
}
