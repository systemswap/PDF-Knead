using System.Security.Cryptography;
using PdfKnead.Tests;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// Stage 4, part f: the check against the XML schema of the syntax — UBL 2.1, the profile schema of Factur-X / ZUGFeRD, CII D16B — over the
/// tree of the hardened reader; the schemas shipped unchanged and read from the module only. Only invented invoices and names.
/// </summary>
public sealed class InvoiceSchemaTests
{
    private const string En16931 = "urn:cen.eu:en16931:2017";

    private static string Change(string xml, string from, string to)
    {
        Assert.Contains(from, xml, StringComparison.Ordinal);
        return xml.Replace(from, to, StringComparison.Ordinal);
    }

    private static IReadOnlyList<InvoiceRuleViolation> Schema(string xml) =>
        [.. InvoiceRules.Check(InvoiceReaderTests.Read(xml)).Where(v => v.Rule.Source == InvoiceRules.SchemaSource)];

    private static string[] Ids(string xml) => [.. Schema(xml).Select(v => v.Rule.Id).Distinct().Order(StringComparer.Ordinal)];

    private static string Cii(string id) => Change(InvoiceReaderTests.Cii, $"<ram:ID>{En16931}</ram:ID>", $"<ram:ID>{id}</ram:ID>");

    [Fact]
    public void ShippedSchemas_UnchangedAsPublished()
    {
        // every schema in the module is the file of the publisher, byte for byte (the licences allow copies, not changes)
        var sums = File.ReadAllLines(Path.Combine(InvoiceSourceRulesTests.Repo(), "PdfKnead.EInvoice", "Schemata", "SHA256SUMS"))
            .Where(l => l.Length > 0).ToDictionary(l => l[66..], l => l[..64], StringComparer.Ordinal);
        var assembly = typeof(InvoiceRules).Assembly;
        var shipped = assembly.GetManifestResourceNames().Where(n => n.StartsWith("Schemata", StringComparison.Ordinal)).ToList();
        Assert.Equal(sums.Count, shipped.Count);
        foreach (var name in shipped)
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var path = name["Schemata".Length..].Replace('\\', '/').TrimStart('/');
            Assert.Equal(sums[path], Convert.ToHexStringLower(SHA256.HashData(stream)));
        }
        // the XAdES schemas of ETSI are not shipped (their licence is not settled)
        Assert.DoesNotContain(shipped, n => n.Contains("XAdES", StringComparison.Ordinal));
    }

    [Fact]
    public void EachKind_ItsSchema()
    {
        // the invented invoices are valid where the schema has their elements: UBL 2.1; CII D16B for XRechnung, which knows the name of
        // the document; the profile EXTENDED knows it too, the profile EN16931 does not (it has only the terms of the standard)
        Assert.Empty(Ids(InvoiceReaderTests.Ubl));
        Assert.Empty(Ids(Cii("urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0")));
        Assert.Empty(Ids(Cii("urn:cen.eu:en16931:2017#conformant#urn:factur-x.eu:1p0:extended")));
        var en = Assert.Single(Schema(InvoiceReaderTests.Cii));
        Assert.Equal(("PDFK-XSD-01", "/rsm:CrossIndustryInvoice/rsm:ExchangedDocument/ram:Name"), (en.Rule.Id, en.Place));
        // MINIMUM, BASIC WL and BASIC have their schemas of fewer elements (each compiles and is asked)
        foreach (var profile in new[] { "minimum", "basicwl", "basic" })
            Assert.Contains("PDFK-XSD-01", Ids(Cii($"urn:factur-x.eu:1p0:{profile}")));
        // no schema shipped: said as information, nothing checked
        var ctc = Schema(Cii("urn:cen.eu:en16931:2017#conformant#urn:factur-x.eu:1p0:extended-ctc-fr"));
        Assert.Equal(("PDFK-XSD-00", InvoiceRuleSeverity.Information), (Assert.Single(ctc).Rule.Id, ctc[0].Rule.Severity));
    }

    [Fact]
    public void Ubl_EachKindOfFinding_WithItsPlaceAndValue()
    {
        var ubl = InvoiceReaderTests.Ubl;
        // 01: an element where the schema has none; 03: a value not of its type (with the value as written)
        var element = Assert.Single(Schema(Change(ubl, "<cbc:IssueDate>", "<cbc:Unbekannt>x</cbc:Unbekannt><cbc:IssueDate>")));
        Assert.Equal(("PDFK-XSD-01", "/Invoice/cbc:Unbekannt"), (element.Rule.Id, element.Place));
        var value = Assert.Single(Schema(Change(ubl, "<cbc:IssueDate>2026-11-13</cbc:IssueDate>", "<cbc:IssueDate>13.11.2026</cbc:IssueDate>")));
        Assert.Equal(("PDFK-XSD-03", "/Invoice/cbc:IssueDate", "13.11.2026"), (value.Rule.Id, value.Place, value.Value));
        // 04: an attribute the schema does not have; a declared attribute with a wrong value is 03
        var attribute = Schema(Change(ubl, "<cbc:ID>RE-2026-0002</cbc:ID>", "<cbc:ID fremd=\"1\">RE-2026-0002</cbc:ID>"));
        Assert.Equal(("PDFK-XSD-04", "/Invoice/cbc:ID/@fremd", "1"), (Assert.Single(attribute).Rule.Id, attribute[0].Place, attribute[0].Value));
        // 05: a required attribute missing (the currency of an amount)
        const string payable = "<cbc:PayableAmount currencyID=\"EUR\">";
        Assert.Equal(["PDFK-XSD-05"], Ids(Change(ubl, payable, "<cbc:PayableAmount>")));
        // 02: content missing — the totals without their required amount
        Assert.Contains("PDFK-XSD-02", Ids(System.Text.RegularExpressions.Regex.Replace(ubl, "<cbc:PayableAmount[^>]*>[^<]*</cbc:PayableAmount>", "",
            System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1))));
        // 06: text in an element of elements only
        Assert.Contains("PDFK-XSD-06", Ids(Change(ubl, "<cac:AccountingSupplierParty><cac:Party>", "<cac:AccountingSupplierParty>Text<cac:Party>")));
    }

    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void AfterAMisplacedElement_TheSiblingsAssessedLaxly()
    {
        // XSD 1.0: what follows an element out of place is assessed against its global declaration — the wrong date after it is found too
        // (the validator of .NET alone would leave it unchecked; the one of the reference checkers finds it)
        var both = Schema(Change(InvoiceReaderTests.Ubl, "<cbc:IssueDate>2026-11-13</cbc:IssueDate>",
            "<cbc:Unbekannt>x</cbc:Unbekannt><cbc:IssueDate>13.11.2026</cbc:IssueDate>"));
        Assert.Equal([("PDFK-XSD-01", "/Invoice/cbc:Unbekannt"), ("PDFK-XSD-03", "/Invoice/cbc:IssueDate")],
            both.Select(v => (v.Rule.Id, v.Place)).Order());
        // a party out of place, holding a party out of place, and so on: each assessed laxly once per level — a prepared chain ends with the
        // hint instead of costing depth times elements
        var party = string.Concat(Enumerable.Repeat("<cac:PartyName><cbc:Name>Musterfirma</cbc:Name></cac:PartyName>", 60));
        var chain = string.Concat(Enumerable.Repeat($"<cac:Party>{party}", 55)) + string.Concat(Enumerable.Repeat("</cac:Party>", 55));
        var prepared = Change(InvoiceReaderTests.Ubl, "<cbc:IssueDate>", chain + "<cbc:IssueDate>");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ids = Ids(prepared);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3 * TestKind.Factor), $"{watch.Elapsed.TotalSeconds:F1} s");
        Assert.Contains("PDFK-XSD-07", ids);
        Assert.Equal(InvoiceRuleSeverity.Warning, InvoiceRules.All.Single(r => r.Id == "PDFK-XSD-07").Severity);
    }

    [Fact]
    public void ManySiblingsAfterAMisplacedElement_OneValidatorForAll()
    {
        // 20 000 lines after one misplaced element: each assessed laxly, all with the one validator — a validator per assessment took some
        // kilobytes each (55 000 siblings, 180 MB; FG 846)
        var ubl = InvoiceReaderTests.Ubl;
        var line = ubl[ubl.IndexOf("<cac:InvoiceLine>", StringComparison.Ordinal)..(ubl.IndexOf("</cac:InvoiceLine>", StringComparison.Ordinal) + "</cac:InvoiceLine>".Length)];
        var many = Change(ubl, line, string.Concat(Enumerable.Repeat(line, 20000))).Replace("<cbc:IssueDate>", "<cbc:Unbekannt/><cbc:IssueDate>", StringComparison.Ordinal);
        var root = InvoiceReaderTests.Read(many).Root.Element!;
        InvoiceRules.CheckAgainstSchema(InvoiceReaderTests.Read(ubl).Root.Element!, "ubl-2.1", "maindoc/UBL-Invoice-2.1.xsd");   // compiled before
        var before = GC.GetAllocatedBytesForCurrentThread();
        var found = InvoiceRules.CheckAgainstSchema(root, "ubl-2.1", "maindoc/UBL-Invoice-2.1.xsd");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal([("PDFK-XSD-01", "/Invoice/cbc:Unbekannt")], found);
        Assert.True(allocated < 20000 * 1024, $"{allocated >> 10} KB");
    }

    [Fact]
    public void InvoiceAndCreditNote_EachItsMainSchema_UndeclaredRootAFinding()
    {
        // a credit note checked first must not leave the schema of credit notes to the invoices after it — the set of UBL is one, its main
        // documents are two (FG 845)
        var credit = InvoiceReaderTests.Ubl.Replace("<Invoice xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Invoice-2\"",
                "<CreditNote xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2\"", StringComparison.Ordinal)
            .Replace("</Invoice>", "</CreditNote>", StringComparison.Ordinal).Replace("<cbc:InvoiceTypeCode>380</cbc:InvoiceTypeCode>", "<cbc:CreditNoteTypeCode>381</cbc:CreditNoteTypeCode>", StringComparison.Ordinal)
            .Replace("cac:InvoiceLine>", "cac:CreditNoteLine>", StringComparison.Ordinal).Replace("cbc:InvoicedQuantity", "cbc:CreditedQuantity", StringComparison.Ordinal);
        Assert.Equal(InvoiceSyntax.UblCreditNote, InvoiceReaderTests.Read(credit).Kind.Syntax);
        InvoiceRules.Check(InvoiceReaderTests.Read(credit));
        var missing = Assert.Single(Schema(Change(InvoiceReaderTests.Ubl, "<cbc:IssueDate>2026-11-13</cbc:IssueDate>", "")));
        Assert.Equal(("PDFK-XSD-01", "/Invoice/cbc:InvoiceTypeCode"), (missing.Rule.Id, missing.Place));   // where the issue date was due
        // a root the schema does not declare: a finding (the validator of .NET only warns there), not a pass
        Assert.Equal([("PDFK-XSD-01", "/Invoice")],
            InvoiceRules.CheckAgainstSchema(InvoiceReaderTests.Read(InvoiceReaderTests.Ubl).Root.Element!, "ubl-2.1", "maindoc/UBL-CreditNote-2.1.xsd"));
    }

    [Fact]
    public void SchemaLocationNeverFollowed_XsiTypeResolvedWhereItStood()
    {
        // an invoice naming its own schema: not fetched, not read — the shipped one is asked
        var located = Change(InvoiceReaderTests.Ubl, "<cbc:CustomizationID>",
            "<cbc:CustomizationID xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:schemaLocation=\"urn:x http://192.0.2.1/boese.xsd\">");
        Assert.Empty(Ids(located));
        // an xsi:type with the prefix declared on its element: resolved there (the reader keeps it for exactly this)
        var typed = Change(InvoiceReaderTests.Ubl, "<cbc:IssueDate>2026-11-13</cbc:IssueDate>",
            "<cbc:IssueDate xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:u=\"urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2\" xsi:type=\"u:IssueDateType\">2026-11-13</cbc:IssueDate>");
        Assert.Empty(Ids(typed));
        // a type that is not derived from the element's: a finding, not a crash
        Assert.NotEmpty(Ids(typed.Replace("u:IssueDateType", "u:AmountType", StringComparison.Ordinal)));
    }

    [Fact]
    public void Attachment_StandsForItsBase64()
    {
        // the attachment was decoded when read: the schema sees base64 (no finding for the element of the binary object)
        Assert.DoesNotContain(Schema(Cii("urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0")),
            v => v.Place?.EndsWith("AttachmentBinaryObject", StringComparison.Ordinal) == true);
    }

    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void PreparedInvoice_Linear()
    {
        // 5 000 lines: one walk over the tree, the schema compiled once
        var line = InvoiceReaderTests.Cii[InvoiceReaderTests.Cii.IndexOf("<ram:IncludedSupplyChainTradeLineItem>", StringComparison.Ordinal)..
            (InvoiceReaderTests.Cii.IndexOf("</ram:IncludedSupplyChainTradeLineItem>", StringComparison.Ordinal) + "</ram:IncludedSupplyChainTradeLineItem>".Length)];
        var many = Cii("urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0").Replace(line, string.Concat(Enumerable.Repeat(line, 5000)), StringComparison.Ordinal);
        var data = InvoiceReaderTests.Read(many);
        InvoiceRules.PrepareSchemas();   // every schema compiled before the clock runs: the check is measured, not the first compiling
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var found = InvoiceRules.Check(data).Where(v => v.Rule.Source == InvoiceRules.SchemaSource).ToList();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3 * TestKind.Factor), $"{watch.Elapsed.TotalSeconds:F1} s");
        Assert.Empty(found);
    }
}
