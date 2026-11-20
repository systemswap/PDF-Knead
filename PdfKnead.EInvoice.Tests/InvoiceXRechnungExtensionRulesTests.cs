using PdfKnead.Tests;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// Stage 4, part d2: the rules of the extension of XRechnung (BR-DEX), the temporary rules (BR-TMP, BR-DE-TMP-32) and those of CVD — each
/// only for the specification it belongs to; in the extension and in CVD the rules that take the place of code list rules and of BR-CO-16.
/// Only invented invoices and names.
/// </summary>
public sealed class InvoiceXRechnungExtensionRulesTests
{
    private const string Cius = "urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0";
    private const string Extension = Cius + "#conformant#urn:xeinkauf.de:kosit:extension:xrechnung_3.0";
    private const string Cvd = Cius + "#compliant#urn:xeinkauf.de:kosit:xrechnung:cvd_0.9";

    private static string Kept => InvoiceXRechnungRulesTests.Kept;

    /// <summary>The kept CII invoice under another specification identifier.</summary>
    private static string As(string xml, string id) => Change(xml, $"<ram:ID>{Cius}</ram:ID>", $"<ram:ID>{id}</ram:ID>");

    /// <summary>The invented UBL invoice under a specification identifier of XRechnung.</summary>
    private static string Ubl(string id) => Change(InvoiceReaderTests.Ubl, $"<cbc:CustomizationID>{Cius}</cbc:CustomizationID>", $"<cbc:CustomizationID>{id}</cbc:CustomizationID>");

    private static IReadOnlyList<InvoiceRuleViolation> All(string xml) => InvoiceRules.Check(InvoiceReaderTests.Read(xml));

    private static string[] Ids(string xml) => [.. All(xml).Select(v => v.Rule.Id).Distinct()];

    private static bool IsPartD2(string id) => id.Split('-').Any(p => p is "DEX" or "TMP" or "CVD");

    private static string Change(string xml, string from, string to)
    {
        Assert.Contains(from, xml, StringComparison.Ordinal);
        return xml.Replace(from, to, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalogue_ExtensionTemporaryCvd()
    {
        string[] expected =
        [
            .. Enumerable.Range(1, 15).Select(n => $"BR-DEX-{n:00}"), .. Enumerable.Range(2, 6).Select(n => $"BR-TMP-{n}"), "BR-DE-TMP-32",
            "BR-DE-CVD-01", "BR-DE-CVD-02", "BR-DE-CVD-03", "BR-DE-CVD-04", "BR-DE-CVD-05", "BR-DE-CVD-06-a", "BR-DE-CVD-06-b", "BR-TMP-CVD-01",
        ];
        var rules = InvoiceRules.All.Where(r => r.Source == InvoiceRules.XRechnungSource && IsPartD2(r.Id)).ToList();
        Assert.Equal(expected.Order(StringComparer.Ordinal), rules.Select(r => r.Id).Order(StringComparer.Ordinal));
        Assert.Equal(["BR-DEX-02", "BR-DEX-15", "BR-TMP-4", "BR-TMP-5", "BR-TMP-6", "BR-TMP-7"],
            rules.Where(r => r.Severity == InvoiceRuleSeverity.Warning).Select(r => r.Id).Order(StringComparer.Ordinal));
        Assert.Equal("BR-DE-TMP-32", Assert.Single(rules, r => r.Severity == InvoiceRuleSeverity.Information).Id);
        Assert.Equal(82, InvoiceRules.All.Count(r => r.Source == InvoiceRules.XRechnungSource));
        // the order: BR-DE-TMP-32 after BR-DE-31, then BR-DEX, BR-TMP, those of CVD, Peppol
        var ids = InvoiceRules.All.Select(r => r.Id).ToList();
        Assert.True(ids.IndexOf("BR-DE-31") < ids.IndexOf("BR-DE-TMP-32") && ids.IndexOf("BR-DE-TMP-32") < ids.IndexOf("BR-DEX-01"));
        Assert.True(ids.IndexOf("BR-TMP-7") < ids.IndexOf("BR-DE-CVD-01") && ids.IndexOf("BR-TMP-CVD-01") < ids.IndexOf("PEPPOL-EN16931-R001"));
    }

    [Fact]
    public void OnlyForTheirSpecification()
    {
        Assert.DoesNotContain(Ids(Kept), IsPartD2);
        Assert.DoesNotContain(All(As(Kept, Extension)), v => v.Rule.Source == InvoiceRules.XRechnungSource);
        // CVD asks for contract, tender and a vehicle line; the CIUS and the extension do not
        Assert.Equal(["BR-DE-CVD-01", "BR-DE-CVD-02", "BR-DE-CVD-03"], Ids(As(Kept, Cvd)).Where(IsPartD2).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(All(KeptCvd), v => v.Rule.Source == InvoiceRules.XRechnungSource);
    }

    /// <summary>The kept CII invoice with codes the extension adds: XR01 to XR03 for identifiers and addresses, application/xml for the attachment.</summary>
    private static string Coded(string id, string code = "XR0") => As(Kept, id)
        .Replace("<ram:Name>Musterfirma Beispiel GmbH</ram:Name>", $"<ram:GlobalID schemeID=\"{code}1\">4000001000005</ram:GlobalID><ram:Name>Musterfirma Beispiel GmbH</ram:Name>"
            + $"<ram:SpecifiedLegalOrganization><ram:ID schemeID=\"{code}3\">123456789</ram:ID></ram:SpecifiedLegalOrganization>", StringComparison.Ordinal)
        .Replace("schemeID=\"EM\">verkauf@beispiel.example", $"schemeID=\"{code}2\">verkauf@beispiel.example", StringComparison.Ordinal)
        .Replace("<ram:SpecifiedTradeProduct><ram:Name>Musterartikel</ram:Name>", $"<ram:SpecifiedTradeProduct><ram:GlobalID schemeID=\"{code}1\">P-1</ram:GlobalID><ram:Name>Musterartikel</ram:Name>",
            StringComparison.Ordinal)
        .Replace("<ram:ApplicableHeaderTradeDelivery/>",
            $"<ram:ApplicableHeaderTradeDelivery><ram:ShipToTradeParty><ram:GlobalID schemeID=\"{code}1\">L-9</ram:GlobalID><ram:Name>Lager</ram:Name></ram:ShipToTradeParty></ram:ApplicableHeaderTradeDelivery>",
            StringComparison.Ordinal)
        .Replace("mimeCode=\"application/pdf\"", "mimeCode=\"application/xml\"", StringComparison.Ordinal);

    [Fact]
    public void Extension_WiderCodeListsTakeThePlaceOfBrCl()
    {
        string[] replaced = ["BR-CL-10", "BR-CL-11", "BR-CL-21", "BR-CL-24", "BR-CL-25", "BR-CL-26"];
        // the codes of the extension: as the CIUS each of them is a finding of the code lists, in the extension none
        Assert.Equal(replaced, Ids(Coded(Cius)).Where(id => id.StartsWith("BR-CL-", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        var extension = Ids(Coded(Extension));
        Assert.DoesNotContain(extension, id => id.StartsWith("BR-CL-", StringComparison.Ordinal) || id.StartsWith("BR-DEX-", StringComparison.Ordinal));
        // a scheme in neither list: the rule of the extension finds it, not the one whose place it takes
        var wrong = All(Coded(Extension, code: "QQ0"));
        Assert.Equal(["BR-DEX-04", "BR-DEX-05", "BR-DEX-06", "BR-DEX-07", "BR-DEX-08"],
            wrong.Select(v => v.Rule.Id).Where(id => id.StartsWith("BR-DEX-", StringComparison.Ordinal) || id.StartsWith("BR-CL-", StringComparison.Ordinal)).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal("QQ01", wrong.First(v => v.Rule.Id == "BR-DEX-04").Value);
        // an attachment without MIME code: none of the list of the extension (the rule of EN 16931 asks only a given one)
        var unnamed = Change(Coded(Extension), "mimeCode=\"application/xml\" ", "");
        Assert.Null(Assert.Single(All(unnamed), v => v.Rule.Id == "BR-DEX-01").Value);
        Assert.DoesNotContain("BR-CL-24", Ids(Change(Coded(Cius), "mimeCode=\"application/xml\" ", "")));
        Assert.Contains("BR-DEX-01", Ids(Change(Coded(Extension), "application/xml", "text/xml")));
    }

    private static string SubLine(string id, string amount, int taxes = 1, string inner = "", string category = "S", string percent = "19", string price = "", string baseUnit = "") =>
        $"<cac:SubInvoiceLine><cbc:ID>{id}</cbc:ID><cbc:InvoicedQuantity unitCode=\"H87\">1</cbc:InvoicedQuantity><cbc:LineExtensionAmount currencyID=\"EUR\">{amount}</cbc:LineExtensionAmount>"
        + "<cac:Item><cbc:Name>Teil</cbc:Name>"
        + string.Concat(Enumerable.Repeat($"<cac:ClassifiedTaxCategory><cbc:ID>{category}</cbc:ID><cbc:Percent>{percent}</cbc:Percent><cac:TaxScheme><cbc:ID>VAT</cbc:ID></cac:TaxScheme></cac:ClassifiedTaxCategory>", taxes))
        + $"</cac:Item><cac:Price><cbc:PriceAmount currencyID=\"EUR\">{(price.Length > 0 ? price : amount)}</cbc:PriceAmount>"
        + (baseUnit.Length > 0 ? $"<cbc:BaseQuantity unitCode=\"{baseUnit}\">1</cbc:BaseQuantity>" : "") + $"</cac:Price>{inner}</cac:SubInvoiceLine>";

    private static string WithSubLines(string id, string subLines) => Change(Ubl(id), "</cac:Price></cac:InvoiceLine>", "</cac:Price>" + subLines + "</cac:InvoiceLine>");

    [Fact]
    public void Extension_SubLines_AddUpAndHaveOneVatInformation()
    {
        Assert.DoesNotContain(Ids(WithSubLines(Extension, SubLine("1.1", "6.00") + SubLine("1.2", "4.00"))), id => id is "BR-DEX-02" or "BR-DEX-03");
        var notAdding = All(WithSubLines(Extension, SubLine("1.1", "6.00") + SubLine("1.2", "5.00")));
        Assert.Equal("BG-25", Assert.Single(notAdding, v => v.Rule.Id == "BR-DEX-02").Group.Id);
        Assert.Equal(InvoiceRuleSeverity.Warning, notAdding.First(v => v.Rule.Id == "BR-DEX-02").Rule.Severity);
        // a sub line with sub lines of its own that do not add up to it
        var nested = All(WithSubLines(Extension, SubLine("1.1", "6.00", inner: SubLine("1.1.1", "3.00") + SubLine("1.1.2", "2.00")) + SubLine("1.2", "4.00")));
        Assert.Equal("BG-DEX-01", Assert.Single(nested, v => v.Rule.Id == "BR-DEX-02").Group.Id);
        Assert.Contains("BR-DEX-03", Ids(WithSubLines(Extension, SubLine("1.1", "6.00", taxes: 0) + SubLine("1.2", "4.00"))));
        Assert.Contains("BR-DEX-03", Ids(WithSubLines(Extension, SubLine("1.1", "6.00", taxes: 2) + SubLine("1.2", "4.00"))));
        // the CIUS knows no sub lines: none of these rules
        Assert.DoesNotContain(Ids(WithSubLines(Cius, SubLine("1.1", "6.00", taxes: 0) + SubLine("1.2", "5.00"))), id => id is "BR-DEX-02" or "BR-DEX-03");
    }

    [Fact]
    public void SubLines_LinesDoNotBorrowTheirTerms()   // FG 839
    {
        var subs = SubLine("1.1", "6.00") + SubLine("1.2", "4.00");
        // the line without its item, without its price: the item name, VAT category and price of its sub lines do not stand in for its own
        var noItem = Ids(Change(WithSubLines(Extension, subs), "<cac:Item><cbc:Name>Musterartikel</cbc:Name><cac:ClassifiedTaxCategory><cbc:ID>S</cbc:ID><cbc:Percent>19</cbc:Percent>"
            + "<cac:TaxScheme><cbc:ID>VAT</cbc:ID></cac:TaxScheme></cac:ClassifiedTaxCategory></cac:Item>", ""));
        Assert.Contains("BR-25", noItem);
        Assert.Contains("BR-CO-04", noItem);
        Assert.Contains("BR-26", Ids(Change(WithSubLines(Extension, subs),
            "<cac:Price><cbc:PriceAmount currencyID=\"EUR\">10.00</cbc:PriceAmount><cbc:BaseQuantity unitCode=\"H87\">1</cbc:BaseQuantity></cac:Price>", "")));
        // nor does the negative price of a sub line count as one of the line (the rule is about the lines; the artefacts ask only those)
        Assert.DoesNotContain("BR-27", Ids(WithSubLines(Extension, SubLine("1.1", "6.00", price: "-6.00") + SubLine("1.2", "4.00"))));
        // CII: a line held below another (ParentLineID) is a line of its own — the item name of the one held below is not that of its parent
        const string second = "<ram:LineID>2</ram:LineID>";
        var nested = Change(Change(Kept, second, second + "<ram:ParentLineID>1</ram:ParentLineID>"),
            "<ram:SpecifiedTradeProduct><ram:Name>Musterartikel</ram:Name></ram:SpecifiedTradeProduct>", "<ram:SpecifiedTradeProduct><ram:Description>ohne Namen</ram:Description></ram:SpecifiedTradeProduct>");
        Assert.Contains("BR-25", Ids(nested));
    }

    [Fact]
    public void SubLines_TheirCategoriesAndPricesAsTheArtefactsAskThem()
    {
        // a sub line of reverse charge: the invoice has that category, the breakdown not (as both artefacts count it)
        Assert.Contains("BR-AE-01", Ids(WithSubLines(Extension, SubLine("1.1", "6.00", category: "AE", percent: "0") + SubLine("1.2", "4.00"))));
        // the rate of a line (05) only of the lines, as the artefacts of UBL ask it
        Assert.DoesNotContain("BR-S-05", Ids(WithSubLines(Extension, SubLine("1.1", "6.00", percent: "0") + SubLine("1.2", "4.00"))));
        // the unit of the base quantity of a sub line against the quantity of that sub line (PEPPOL-EN16931-R130, every price)
        Assert.Contains("PEPPOL-EN16931-R130", Ids(WithSubLines(Extension, SubLine("1.1", "6.00", baseUnit: "C62") + SubLine("1.2", "4.00"))));
        Assert.DoesNotContain("PEPPOL-EN16931-R130", Ids(WithSubLines(Extension, SubLine("1.1", "6.00", baseUnit: "H87") + SubLine("1.2", "4.00"))));
    }

    private const string Payment ="<cac:PrepaidPayment><cbc:ID>Kurtaxe</cbc:ID><cbc:PaidAmount currencyID=\"EUR\">2.00</cbc:PaidAmount>"
                                   + "<cbc:InstructionID>Kurtaxe der Gemeinde</cbc:InstructionID></cac:PrepaidPayment>";

    /// <summary>The UBL invoice with a third party payment of 2.00 and that amount due.</summary>
    private static string WithPayment(string id, string payment = Payment, string due = "13.90") =>
        Change(Change(Ubl(id), "<cac:TaxTotal>", payment + "<cac:TaxTotal>"), "<cbc:PayableAmount currencyID=\"EUR\">11.90</cbc:PayableAmount>",
            $"<cbc:PayableAmount currencyID=\"EUR\">{due}</cbc:PayableAmount>");

    [Fact]
    public void Extension_ThirdPartyPayments()
    {
        static bool OfPayments(string id) => id is "BR-CO-16" || id.StartsWith("BR-DEX-", StringComparison.Ordinal);
        // in the extension the third party payment counts to the amount due (BR-DEX-09 takes the place of BR-CO-16), in the CIUS it does not
        Assert.DoesNotContain(Ids(WithPayment(Extension)), OfPayments);
        Assert.Equal(["BR-CO-16"], Ids(WithPayment(Cius)).Where(OfPayments));
        Assert.Equal(["BR-DEX-09"], Ids(WithPayment(Extension, due: "11.90")).Where(OfPayments));
        // type, amount and description of the payment; the amount with at most two decimals and in the invoice currency
        var untyped = All(WithPayment(Extension, Change(Payment, "<cbc:ID>Kurtaxe</cbc:ID>", "")));
        Assert.Equal("BG-DEX-09", Assert.Single(untyped, v => v.Rule.Id == "BR-DEX-10").Group.Id);
        Assert.Contains("BR-DEX-12", Ids(WithPayment(Extension, Change(Payment, "<cbc:InstructionID>Kurtaxe der Gemeinde</cbc:InstructionID>", ""))));
        var noAmount = Ids(WithPayment(Extension, Change(Payment, "<cbc:PaidAmount currencyID=\"EUR\">2.00</cbc:PaidAmount>", "")));
        Assert.Equal(["BR-DEX-09", "BR-DEX-11", "BR-DEX-14"], noAmount.Where(OfPayments).Order(StringComparer.Ordinal));
        Assert.Contains("BR-DEX-13", Ids(WithPayment(Extension, Change(Payment, ">2.00<", ">2.001<"), due: "13.901")));
        Assert.Contains("BR-DEX-14", Ids(WithPayment(Extension, Change(Payment, "currencyID=\"EUR\"", "currencyID=\"USD\""))));
        Assert.DoesNotContain(Ids(WithPayment(Cius, Change(Payment, "<cbc:ID>Kurtaxe</cbc:ID>", ""))), id => id.StartsWith("BR-DEX-", StringComparison.Ordinal));
    }

    [Fact]
    public void Extension_CiiSubLinesNotSupported()
    {
        const string second = "<ram:LineID>2</ram:LineID>";
        var sub = Change(As(Kept, Extension), second, second + "<ram:ParentLineID>1</ram:ParentLineID>");
        Assert.Equal(InvoiceRuleSeverity.Warning, Assert.Single(All(sub), v => v.Rule.Id == "BR-DEX-15").Rule.Severity);
        Assert.DoesNotContain("BR-DEX-15", Ids(Change(Kept, second, second + "<ram:ParentLineID>1</ram:ParentLineID>")));
    }

    [Fact]
    public void Temporary_ExternalLocationIsAnAbsoluteUrl()
    {
        string Cii(string uri) => Change(Kept, "<ram:IssuerAssignedID>Lieferschein-1</ram:IssuerAssignedID>",
            $"<ram:IssuerAssignedID>Lieferschein-1</ram:IssuerAssignedID><ram:URIID>{uri}</ram:URIID>");
        Assert.DoesNotContain("BR-TMP-2", Ids(Cii("https://beispiel.example/lieferschein.pdf")));
        Assert.DoesNotContain("BR-TMP-2", Ids(Cii("urn:beispiel:lieferschein:1")));
        Assert.Contains("BR-TMP-2", Ids(Cii("www.beispiel.example/lieferschein.pdf")));
        Assert.Contains("BR-TMP-2", Ids(Cii(@"C:\Belege\lieferschein.pdf")));   // a drive letter is no scheme
        Assert.Contains("BR-TMP-2", Ids(Cii(" https://beispiel.example/lieferschein.pdf")));
        string External(string reference) => Change(Ubl(Cius), "<cac:AccountingSupplierParty>",
            $"<cac:AdditionalDocumentReference><cbc:ID>Beleg-1</cbc:ID><cac:Attachment><cac:ExternalReference>{reference}</cac:ExternalReference></cac:Attachment></cac:AdditionalDocumentReference>"
            + "<cac:AccountingSupplierParty>");
        Assert.DoesNotContain("BR-TMP-2", Ids(External("<cbc:URI>https://beispiel.example/beleg.pdf</cbc:URI>")));
        Assert.Contains("BR-TMP-2", Ids(External("<cbc:URI>beispiel.example/beleg.pdf</cbc:URI>")));
        Assert.Contains("BR-TMP-2", Ids(External("<cbc:MimeCode>application/pdf</cbc:MimeCode>")));   // a reference without location
    }

    [Fact]
    public void Temporary_PriceBasesSupportingDocumentsDates()
    {
        const string net = "<ram:NetPriceProductTradePrice><ram:ChargeAmount>10.00</ram:ChargeAmount><ram:BasisQuantity unitCode=\"H87\">1</ram:BasisQuantity>";
        Assert.Contains("BR-TMP-3", Ids(Change(Kept, net, net.Replace(">1<", ">1.0<", StringComparison.Ordinal))));   // compared as written
        Assert.Contains("BR-TMP-3", Ids(Change(Kept, net, net.Replace("H87", "C62", StringComparison.Ordinal))));
        // only where both carry a unit, as the test of the artefacts compares (ENTSCHEIDUNGEN)
        Assert.DoesNotContain("BR-TMP-3", Ids(Change(Kept, "<ram:ChargeAmount>12.00</ram:ChargeAmount><ram:BasisQuantity unitCode=\"H87\">1</ram:BasisQuantity>",
            "<ram:ChargeAmount>12.00</ram:ChargeAmount><ram:BasisQuantity>1</ram:BasisQuantity>")));
        const string document = "<ram:IssuerAssignedID>Lieferschein-1</ram:IssuerAssignedID>";
        Assert.Contains("BR-TMP-4", Ids(Change(Kept, document, document + "<ram:Name>Lieferschein</ram:Name><ram:Name>Zweiter Name</ram:Name>")));
        const string attachment = "filename=\"lieferschein.pdf\">JVBERi0=</ram:AttachmentBinaryObject>";
        Assert.Contains("BR-TMP-5", Ids(Change(Kept, attachment, attachment + "<ram:AttachmentBinaryObject mimeCode=\"application/pdf\" filename=\"zwei.pdf\">JVBERi0=</ram:AttachmentBinaryObject>")));
        const string issued = "<udt:DateTimeString format=\"102\">20261113</udt:DateTimeString>";
        // another format: no BT-2 then (the mapping reads the date only with format 102), the date element is still found
        Assert.Null(Assert.Single(All(Change(Kept, issued, "<udt:DateTimeString format=\"610\">202611</udt:DateTimeString>")), v => v.Rule.Id == "BR-TMP-7").Field);
        var dashes = All(Change(Kept, issued, "<udt:DateTimeString format=\"102\">2026-11-13</udt:DateTimeString>"));
        Assert.Equal("BT-2", Assert.Single(dashes, v => v.Rule.Id == "BR-TMP-7").Field?.Id);
        // the digits 0 to 9 only (XPath's \d would also take those of other scripts, ENTSCHEIDUNGEN)
        Assert.Contains("BR-TMP-7", Ids(Change(Kept, issued, "<udt:DateTimeString format=\"102\">\uFF12\uFF10\uFF12\uFF16\uFF11\uFF11\uFF11\uFF13</udt:DateTimeString>")));
        const string ublIssued = "<cbc:IssueDate>2026-11-13</cbc:IssueDate>";
        Assert.DoesNotContain("BR-TMP-6", Ids(Ubl(Cius)));
        Assert.Contains("BR-TMP-6", Ids(Change(Ubl(Cius), ublIssued, "<cbc:IssueDate>20261113</cbc:IssueDate>")));
        Assert.DoesNotContain("BR-TMP-6", Ids(Change(Ubl(Cius), ublIssued, "<cbc:IssueDate> 2026-11-13 </cbc:IssueDate>")));
    }

    [Fact]
    public void Temporary_DateOfTheServiceStated()
    {
        var bare = InvoiceReaderTests.Cii.Replace("<ram:ID>urn:cen.eu:en16931:2017</ram:ID>", $"<ram:ID>{Cius}</ram:ID>", StringComparison.Ordinal);
        Assert.Equal(InvoiceRuleSeverity.Information, Assert.Single(All(bare), v => v.Rule.Id == "BR-DE-TMP-32").Rule.Severity);
        Assert.DoesNotContain("BR-DE-TMP-32", Ids(Kept));   // the invoicing period
        const string lineSum = "<ram:SpecifiedTradeSettlementLineMonetarySummation>";
        const string period = "<ram:BillingSpecifiedPeriod><ram:StartDateTime><udt:DateTimeString format=\"102\">20261101</udt:DateTimeString></ram:StartDateTime></ram:BillingSpecifiedPeriod>";
        Assert.DoesNotContain("BR-DE-TMP-32", Ids(Change(bare, lineSum, period + lineSum)));   // a period in every line
        var first = bare.IndexOf(lineSum, StringComparison.Ordinal);
        Assert.Contains("BR-DE-TMP-32", Ids(bare[..first] + period + bare[first..]));   // in one line only
        Assert.DoesNotContain("BR-DE-TMP-32", Ids(Change(bare, "<ram:ApplicableHeaderTradeDelivery/>",
            "<ram:ApplicableHeaderTradeDelivery><ram:ActualDeliverySupplyChainEvent><ram:OccurrenceDateTime><udt:DateTimeString format=\"102\">20261110</udt:DateTimeString>"
            + "</ram:OccurrenceDateTime></ram:ActualDeliverySupplyChainEvent></ram:ApplicableHeaderTradeDelivery>")));
        Assert.Contains("BR-DE-TMP-32", Ids(Ubl(Cius)));
        Assert.DoesNotContain("BR-DE-TMP-32", Ids(Change(Ubl(Cius), "</cac:AccountingCustomerParty>",
            "</cac:AccountingCustomerParty><cac:Delivery><cbc:ActualDeliveryDate>2026-11-10</cbc:ActualDeliveryDate></cac:Delivery>")));
    }

    /// <summary>The kept CII invoice as CVD: contract, tender, a line with the vehicle category and the attribute „cva“.</summary>
    private static string KeptCvd => As(Kept, Cvd)
        .Replace("<ram:AdditionalReferencedDocument>", "<ram:ContractReferencedDocument><ram:IssuerAssignedID>V-2026-1</ram:IssuerAssignedID></ram:ContractReferencedDocument>"
            + "<ram:AdditionalReferencedDocument><ram:IssuerAssignedID>LOS-7</ram:IssuerAssignedID><ram:TypeCode>50</ram:TypeCode></ram:AdditionalReferencedDocument>"
            + "<ram:AdditionalReferencedDocument>", StringComparison.Ordinal)
        .Replace("<ram:SpecifiedTradeProduct><ram:Name>Musterartikel</ram:Name></ram:SpecifiedTradeProduct>",
            "<ram:SpecifiedTradeProduct><ram:Name>Musterartikel</ram:Name>" + Cva("clean") + Vehicle("M1") + "</ram:SpecifiedTradeProduct>", StringComparison.Ordinal);

    private static string Cva(string value, string name = "cva") =>
        $"<ram:ApplicableProductCharacteristic><ram:Description>{name}</ram:Description><ram:Value>{value}</ram:Value></ram:ApplicableProductCharacteristic>";

    private static string Vehicle(string category, string scheme = "CVD") =>
        $"<ram:DesignatedProductClassification><ram:ClassCode listID=\"{scheme}\">{category}</ram:ClassCode></ram:DesignatedProductClassification>";

    private static string[] CvdIds(string xml) => [.. Ids(xml).Where(id => id.Contains("CVD", StringComparison.Ordinal) || id == "BR-CL-13").Order(StringComparer.Ordinal)];

    [Fact]
    public void Cvd_ContractTenderAndVehicleLine()
    {
        Assert.Empty(CvdIds(KeptCvd));
        // the scheme CVD is no code of UNTDID 7143: as the CIUS a finding of the code lists, in CVD the rule of CVD takes its place
        Assert.Equal(["BR-CL-13"], CvdIds(Change(KeptCvd, $"<ram:ID>{Cvd}</ram:ID>", $"<ram:ID>{Cius}</ram:ID>")));
        Assert.Equal(["BR-DE-CVD-01"], CvdIds(Change(KeptCvd, "<ram:IssuerAssignedID>V-2026-1</ram:IssuerAssignedID>", "<ram:IssuerAssignedID> </ram:IssuerAssignedID>")));
        Assert.Equal(["BR-DE-CVD-02"], CvdIds(Change(KeptCvd, "<ram:TypeCode>50</ram:TypeCode>", "<ram:TypeCode>51</ram:TypeCode>")));
        Assert.Equal(["BR-DE-CVD-04"], CvdIds(Change(KeptCvd, Vehicle("M1"), Vehicle("M4"))));
        Assert.Equal(["BR-DE-CVD-05"], CvdIds(Change(KeptCvd, Cva("clean"), Cva("sauber"))));
        Assert.Equal(["BR-DE-CVD-06-a"], CvdIds(Change(KeptCvd, Cva("clean"), Cva("clean") + Cva("other"))));
        Assert.Equal(["BR-DE-CVD-06-b"], CvdIds(Change(KeptCvd, Vehicle("M1"), Vehicle("M1") + Vehicle("N1"))));
        // the attribute named otherwise: no vehicle line any more, and the vehicle category without its attribute
        Assert.Equal(["BR-DE-CVD-03", "BR-DE-CVD-06-a"], CvdIds(Change(KeptCvd, Cva("clean"), Cva("clean", name: "CVA"))));
        var scheme = All(Change(KeptCvd, Vehicle("M1"), Vehicle("M1", scheme: "QQ")));
        Assert.Equal("QQ", Assert.Single(scheme, v => v.Rule.Id == "BR-TMP-CVD-01").Value);
        Assert.Equal(["BR-DE-CVD-03", "BR-DE-CVD-06-b", "BR-TMP-CVD-01"], scheme.Select(v => v.Rule.Id).Where(id => id.Contains("CVD", StringComparison.Ordinal)).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Cvd_Ubl()
    {
        var cvd = Change(Change(Change(Ubl(Cvd), "<cac:AccountingSupplierParty>",
                    "<cac:OriginatorDocumentReference><cbc:ID>LOS-7</cbc:ID></cac:OriginatorDocumentReference>"
                    + "<cac:ContractDocumentReference><cbc:ID>V-2026-1</cbc:ID></cac:ContractDocumentReference><cac:AccountingSupplierParty>"),
                "<cac:Item><cbc:Name>Musterartikel</cbc:Name>",
                "<cac:Item><cbc:Name>Musterartikel</cbc:Name><cac:CommodityClassification><cbc:ItemClassificationCode listID=\"CVD\">N1</cbc:ItemClassificationCode></cac:CommodityClassification>"),
            "</cac:ClassifiedTaxCategory></cac:Item>",
            "</cac:ClassifiedTaxCategory><cac:AdditionalItemProperty><cbc:Name>cva</cbc:Name><cbc:Value>zero-emission</cbc:Value></cac:AdditionalItemProperty></cac:Item>");
        Assert.Empty(CvdIds(cvd));
        Assert.Equal(["BR-DE-CVD-01"], CvdIds(Change(cvd, "<cbc:ID>V-2026-1</cbc:ID>", "")));
        Assert.Equal(["BR-DE-CVD-02"], CvdIds(Change(cvd, "<cbc:ID>LOS-7</cbc:ID>", "")));
        Assert.Equal(["BR-DE-CVD-05"], CvdIds(Change(cvd, "zero-emission", "elektrisch")));
    }

    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void PreparedInvoice_ManySubLinesLinearAndListLimited()
    {
        // 20 000 sub lines without VAT information: found in one walk, 100 listed, the last of them counts the others
        var many = WithSubLines(Extension, string.Concat(Enumerable.Range(1, 20000).Select(i => SubLine($"1.{i}", "0.00", taxes: 0))));
        var data = InvoiceReaderTests.Read(many);
        InvoiceRules.PrepareSchemas();   // every schema compiled before the clock runs: the check is measured, not the first compiling
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var found = InvoiceRules.Check(data);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3 * TestKind.Factor), $"{watch.Elapsed.TotalSeconds:F1} s");
        var listed = found.Where(v => v.Rule.Id == "BR-DEX-03").ToList();
        Assert.Equal(InvoiceRules.MaxListedPerRule, listed.Count);
        Assert.Equal(20000 - InvoiceRules.MaxListedPerRule, listed[^1].Further);
    }
}
