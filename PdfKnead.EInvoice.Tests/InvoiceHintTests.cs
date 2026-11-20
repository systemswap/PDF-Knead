namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// Stage 4, part g: the hints — what a profile of ZUGFeRD / Factur-X does not use (PDFK-FX-02), what lies outside the scope of EN 16931
/// (PDFK-EN-01), empty elements (PDFK-XML-01) — each with its place, as information, not as a violation. Only invented invoices and names.
/// </summary>
public sealed class InvoiceHintTests
{
    private const string En16931 = "urn:cen.eu:en16931:2017";
    private const string BuyerAddress = "/rsm:CrossIndustryInvoice/rsm:SupplyChainTradeTransaction/ram:ApplicableHeaderTradeAgreement/ram:BuyerTradeParty/ram:PostalTradeAddress";

    private static string Change(string xml, string from, string to)
    {
        Assert.Contains(from, xml, StringComparison.Ordinal);
        return xml.Replace(from, to, StringComparison.Ordinal);
    }

    private static string As(string id) => Change(InvoiceReaderTests.Cii, $"<ram:ID>{En16931}</ram:ID>", $"<ram:ID>{id}</ram:ID>");

    private static IReadOnlyList<InvoiceRuleViolation> Hints(string xml, string id) =>
        [.. InvoiceRules.Check(InvoiceReaderTests.Read(xml), int.MaxValue).Where(v => v.Rule.Id == id)];

    private static string[] Places(string xml, string id) => [.. Hints(xml, id).Select(v => v.Place ?? "")];   // a hint without a place shows as ""

    [Fact]
    public void Catalogue_HintsAreInformation()
    {
        foreach (var id in new[] { "PDFK-FX-02", "PDFK-EN-01", "PDFK-XML-01" })
            Assert.Equal(InvoiceRuleSeverity.Information, InvoiceRules.All.Single(r => r.Id == id).Severity);
    }

    [Fact]
    public void Profile_WhatItDoesNotUse()
    {
        var minimum = As("urn:factur-x.eu:1p0:minimum");
        // MINIMUM has no address of the buyer: the hint at the element, with its place
        Assert.Contains(BuyerAddress, Places(minimum, "PDFK-FX-02"));
        // an attribute it does not use: the place of the attribute, the value as written
        var scheme = Assert.Single(Hints(Change(minimum, "<ram:ID>RE-2026-0001</ram:ID>", "<ram:ID schemeID=\"X\">RE-2026-0001</ram:ID>"), "PDFK-FX-02"),
            v => v.Place!.EndsWith("/@schemeID", StringComparison.Ordinal));
        Assert.Equal(("/rsm:CrossIndustryInvoice/rsm:ExchangedDocument/ram:ID/@schemeID", "X"), (scheme.Place, scheme.Value));
        // EXTENDED uses the address; outside the profiles the hint is not given (XRechnung is measured against the standard)
        Assert.DoesNotContain(BuyerAddress, Places(As("urn:cen.eu:en16931:2017#conformant#urn:factur-x.eu:1p0:extended"), "PDFK-FX-02"));
        Assert.Empty(Hints(As("urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0"), "PDFK-FX-02"));
    }

    [Fact]
    public void OutsideTheStandard_OnlyWhereMeasuredAgainstIt()
    {
        // the name of the document has no term in EN 16931: a further value — for XRechnung a hint with its place and value
        var xrechnung = As("urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0");
        var name = Assert.Single(Hints(xrechnung, "PDFK-EN-01"));
        Assert.Equal(("/rsm:CrossIndustryInvoice/rsm:ExchangedDocument/ram:Name", "RECHNUNG"), (name.Place, name.Value));
        // an invoice of a profile is measured against its profile (PDFK-FX-02), not against the standard
        Assert.Empty(Hints(InvoiceReaderTests.Cii, "PDFK-EN-01"));
        // UBL is measured against the standard: nothing further in the sample, a copy indicator (no term of the standard) is
        Assert.Empty(Hints(InvoiceReaderTests.Ubl, "PDFK-EN-01"));
        var copy = Assert.Single(Hints(Change(InvoiceReaderTests.Ubl, "<cbc:ID>RE-2026-0002</cbc:ID>",
            "<cbc:ID>RE-2026-0002</cbc:ID><cbc:CopyIndicator>false</cbc:CopyIndicator>"), "PDFK-EN-01"));
        Assert.Equal(("/Invoice/cbc:CopyIndicator", "false"), (copy.Place, copy.Value));
    }

    [Fact]
    public void EmptyElements_HintButInXRechnungItsRule()
    {
        const string reference = "<ram:BuyerReference>04011000-12345-34</ram:BuyerReference>";
        var empty = Change(InvoiceReaderTests.Cii, reference, "<ram:BuyerReference> </ram:BuyerReference>");
        Assert.Equal(["/rsm:CrossIndustryInvoice/rsm:SupplyChainTradeTransaction/ram:ApplicableHeaderTradeAgreement/ram:BuyerReference"],
            Places(empty, "PDFK-XML-01"));
        // attributes do not make an element hold a value (as R008 reads it); an attachment with data is not empty, one without is (FG 849)
        Assert.Single(Hints(Change(InvoiceReaderTests.Cii, reference, "<ram:BuyerReference schemeID=\"x\"/>"), "PDFK-XML-01"));
        Assert.Empty(Hints(InvoiceReaderTests.Cii, "PDFK-XML-01"));   // the sample's empty delivery: the schema demands it, as in R008 of XRechnung
        Assert.Equal(["/rsm:CrossIndustryInvoice/rsm:SupplyChainTradeTransaction/ram:ApplicableHeaderTradeAgreement/ram:AdditionalReferencedDocument/ram:AttachmentBinaryObject"],
            Places(Change(InvoiceReaderTests.Cii, ">JVBERi0=</ram:AttachmentBinaryObject>", "></ram:AttachmentBinaryObject>"), "PDFK-XML-01"));
        // XRechnung has its rule for it — the hint would only repeat it
        var xrechnung = Change(As("urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0"), reference, "<ram:BuyerReference/>");
        Assert.Empty(Hints(xrechnung, "PDFK-XML-01"));
        Assert.NotEmpty(Hints(xrechnung, "PEPPOL-EN16931-R008"));
    }

    [Fact]
    public void ManyEmptyElements_PlaceMadeOnlyForTheListed()
    {
        // 100 000 empty notes: 100 hints listed, the last counts the others — the place, a path of some hundred bytes, made for the listed
        // ones only, not for all (FG 850)
        const string note = "<ram:IncludedNote><ram:Content>Vielen Dank für den Auftrag</ram:Content></ram:IncludedNote>";
        var data = InvoiceReaderTests.Read(Change(InvoiceReaderTests.Cii, note, note + string.Concat(Enumerable.Repeat("<ram:IncludedNote/>", 100_000))));
        InvoiceRules.PrepareSchemas();
        InvoiceRules.Check(InvoiceReaderTests.Read(InvoiceReaderTests.Cii));   // the tables of the rules loaded before counting
        var before = GC.GetAllocatedBytesForCurrentThread();
        var found = InvoiceRules.Check(data);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var listed = found.Where(v => v.Rule.Id == "PDFK-XML-01").ToList();
        Assert.Equal(100, listed.Count);
        Assert.Equal(99_900, listed[^1].Further);
        Assert.Equal("/rsm:CrossIndustryInvoice/rsm:ExchangedDocument/ram:IncludedNote[2]", listed[0].Place);
        Assert.True(allocated < 48L << 20, $"{allocated / 1024 / 1024} MB");   // measured 18 MB; with a path for every finding 117 MB
    }
}
