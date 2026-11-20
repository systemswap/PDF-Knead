using PdfKnead.Tests;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// Stage 4, part e3: the business rules of the profile EXTENDED of ZUGFeRD / Factur-X (BR-FXEXT) in place of the rules of EN 16931 it does not
/// list — sub lines (only DETAIL lines are items), logistics service charges, amounts for third parties, one cent of slack per item, the
/// taxable amount per rate or per rate and exemption reason. Only invented invoices and names.
/// </summary>
public sealed class InvoiceExtendedRulesTests
{
    private const string En16931 = "urn:cen.eu:en16931:2017";
    private const string ExtendedId = "urn:cen.eu:en16931:2017#conformant#urn:factur-x.eu:1p0:extended";

    private static string Change(string xml, string from, string to)
    {
        Assert.Contains(from, xml, StringComparison.Ordinal);
        return xml.Replace(from, to, StringComparison.Ordinal);
    }

    /// <summary>A line: identifier, net amount, parent, subtype; a bare line (a group) without price, quantity and VAT.</summary>
    private static string Line(string id, string amount, string? parent = null, string? status = null, string rate = "19", bool bare = false,
        string price = "1.00", string category = "S", string reason = "") =>
        $"""
        <ram:IncludedSupplyChainTradeLineItem>
          <ram:AssociatedDocumentLineDocument><ram:LineID>{id}</ram:LineID>{(parent is null ? "" : $"<ram:ParentLineID>{parent}</ram:ParentLineID>")}{(status is null ? "" : $"<ram:LineStatusReasonCode>{status}</ram:LineStatusReasonCode>")}</ram:AssociatedDocumentLineDocument>
          <ram:SpecifiedTradeProduct><ram:Name>Musterartikel {id}</ram:Name></ram:SpecifiedTradeProduct>
          {(bare ? "" : $"<ram:SpecifiedLineTradeAgreement><ram:NetPriceProductTradePrice><ram:ChargeAmount>{price}</ram:ChargeAmount></ram:NetPriceProductTradePrice></ram:SpecifiedLineTradeAgreement>")}
          {(bare ? "" : "<ram:SpecifiedLineTradeDelivery><ram:BilledQuantity unitCode=\"H87\">1</ram:BilledQuantity></ram:SpecifiedLineTradeDelivery>")}
          <ram:SpecifiedLineTradeSettlement>
            {(bare ? "" : $"<ram:ApplicableTradeTax><ram:TypeCode>VAT</ram:TypeCode>{reason}<ram:CategoryCode>{category}</ram:CategoryCode><ram:RateApplicablePercent>{rate}</ram:RateApplicablePercent></ram:ApplicableTradeTax>")}
            <ram:SpecifiedTradeSettlementLineMonetarySummation><ram:LineTotalAmount>{amount}</ram:LineTotalAmount></ram:SpecifiedTradeSettlementLineMonetarySummation>
          </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        """;

    private static string WithLines(string xml, string lines)
    {
        var from = xml.IndexOf("<ram:IncludedSupplyChainTradeLineItem>", StringComparison.Ordinal);
        var to = xml.LastIndexOf("</ram:IncludedSupplyChainTradeLineItem>", StringComparison.Ordinal) + "</ram:IncludedSupplyChainTradeLineItem>".Length;
        return xml[..from] + lines + xml[to..];
    }

    /// <summary>
    /// The invented invoice as EXTENDED: a group line 1 (20.00) with its DETAIL lines 2 (12.00) and 3 (8.00) at 19 %, and line 4 without a
    /// subtype (10.00) at 7 % — the totals of the invoice (30.00) are those of the items, not of the group.
    /// </summary>
    private static string Extended => WithLines(Change(InvoiceReaderTests.Cii, $"<ram:ID>{En16931}</ram:ID>", $"<ram:ID>{ExtendedId}</ram:ID>"),
        Line("1", "20.00", status: "GROUP", bare: true) + Line("2", "12.00", "1", "DETAIL") + Line("3", "8.00", "1", "DETAIL") + Line("4", "10.00", rate: "7"));

    private static IReadOnlyList<InvoiceRuleViolation> All(string xml) => InvoiceRules.Check(InvoiceReaderTests.Read(xml));

    private static string[] Ids(string xml) => [.. All(xml).Select(v => v.Rule.Id).Where(i => !i.StartsWith("FX-SCH-A-", StringComparison.Ordinal)).Distinct()];

    private static string[] FxExt(string xml) => [.. Ids(xml).Where(i => i.StartsWith("BR-FXEXT-", StringComparison.Ordinal))];

    /// <summary>The settlement with something before the VAT breakdowns (logistics charges, financial adjustments: their place in the schema).</summary>
    private static string BeforeBreakdowns(string xml, string what)
    {
        var at = xml.LastIndexOf("<ram:ApplicableTradeTax>", xml.IndexOf("<ram:CalculatedAmount>3.80</ram:CalculatedAmount>", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(at > 0);
        return xml.Insert(at, what);
    }

    [Fact]
    public void Catalogue_EveryRuleOfTheSpecificationCheckedOrLeftOutForAReason()
    {
        var mine = InvoiceRules.All.Where(r => r.Id.StartsWith("BR-FXEXT-", StringComparison.Ordinal)).ToList();
        Assert.Equal(44, mine.Count);
        Assert.Equal(mine.Count, mine.Select(r => r.Id).Distinct().Count());
        Assert.All(mine, r => Assert.Equal(InvoiceRules.ProfileSource, r.Source));
        Assert.All(mine, r => Assert.Equal(r.Id == "BR-FXEXT-04" ? InvoiceRuleSeverity.Warning : InvoiceRuleSeverity.Fatal, r.Severity));
        // 05: the element rule FX-SCH-A-000335 checks the same list; 07, 09, 10: no condition; CO-06, CO-08: not to be checked on a free text.
        // BR-FXEXT-BR-27 stands in the sheet for no profile (and is no rule here)
        string[] leftOut = ["BR-FXEXT-05", "BR-FXEXT-07", "BR-FXEXT-09", "BR-FXEXT-10", "BR-FXEXT-CO-06", "BR-FXEXT-CO-08"];
        var listed = InvoiceRules.RulesListedFor(InvoiceProfile.Extended).Where(i => i.StartsWith("BR-FXEXT-", StringComparison.Ordinal)).ToList();
        Assert.Equal(50, listed.Count);
        Assert.Empty(InvoiceRules.ProfilesOf("BR-FXEXT-BR-27"));
        Assert.Equal(listed.Except(leftOut).Order(StringComparer.Ordinal), mine.Select(r => r.Id).Order(StringComparer.Ordinal));
        // after the rules of the standard and of XRechnung, before the element rules
        var ids = InvoiceRules.All.Select(r => r.Id).ToList();
        Assert.True(ids.IndexOf("PEPPOL-EN16931-R130") < ids.IndexOf("BR-FXEXT-01") && ids.IndexOf("BR-FXEXT-S-09b") < ids.IndexOf("FX-SCH-A-000019"));
    }

    [Fact]
    public void Extended_SubLines_OnlyDetailLinesAreItems()
    {
        Assert.Empty(FxExt(Extended));
        // the rules of the standard EXTENDED does not list are not asked: the group line has no price, quantity or VAT (BR-22 to BR-26,
        // BR-CO-04), the sums count only the items (BR-CO-10, BR-S-08)
        Assert.DoesNotContain(Ids(Extended), i => i is "BR-22" or "BR-23" or "BR-24" or "BR-26" or "BR-CO-04" or "BR-CO-10" or "BR-S-08" or "BR-S-01");
        // a DETAIL line without quantity, unit, price or VAT category: the rules of EXTENDED, not those of the standard
        var bare = Change(Extended, Line("2", "12.00", "1", "DETAIL"), Line("2", "12.00", "1", "DETAIL", bare: true));
        Assert.Equal(["BR-FXEXT-BR-22", "BR-FXEXT-BR-23", "BR-FXEXT-BR-26", "BR-FXEXT-CO-04"],
            FxExt(bare).Where(i => i.StartsWith("BR-FXEXT-BR-", StringComparison.Ordinal) || i == "BR-FXEXT-CO-04").Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Ids(bare), i => i is "BR-22" or "BR-26");
        // as the invoice of the standard (EN16931) the group line is a line like any other
        var asEn = Change(Extended, $"<ram:ID>{ExtendedId}</ram:ID>", $"<ram:ID>{En16931}</ram:ID>");
        Assert.Empty(FxExt(asEn));
        Assert.Contains("BR-22", Ids(asEn));
    }

    [Fact]
    public void Groups_SumParentSubtype()
    {
        // 08: the group line is the sum of its DETAIL and GROUP lines
        Assert.Contains("BR-FXEXT-08", FxExt(Change(Extended, Line("1", "20.00", status: "GROUP", bare: true), Line("1", "21.00", status: "GROUP", bare: true))));
        // an INFORMATION line below it does not count
        var info = Change(Extended, Line("4", "10.00", rate: "7"), Line("4", "10.00", rate: "7") + Line("5", "99.00", "1", "INFORMATION", bare: true));
        Assert.DoesNotContain("BR-FXEXT-08", FxExt(info));
        // 06: a line with a parent names its subtype; 11: the parent is another line of the invoice
        Assert.Contains("BR-FXEXT-06", FxExt(Change(Extended, Line("3", "8.00", "1", "DETAIL"), Line("3", "8.00", "1"))));
        var orphan = FxExt(Change(Extended, Line("3", "8.00", "1", "DETAIL"), Line("3", "8.00", "9", "DETAIL")));
        Assert.Contains("BR-FXEXT-11", orphan);
        Assert.Contains("BR-FXEXT-11", FxExt(Change(Extended, Line("3", "8.00", "1", "DETAIL"), Line("3", "8.00", "3", "DETAIL"))));   // itself
        // 12: below a group line with an amount every group line has one
        var nested = Change(Extended, Line("3", "8.00", "1", "DETAIL"),
            Line("3", "8.00", "1", "GROUP", bare: true) + Line("31", "8.00", "3", "DETAIL"));
        Assert.Empty(FxExt(nested));
        var unnamed = Change(nested, Line("3", "8.00", "1", "GROUP", bare: true), Line("3", "", "1", "GROUP", bare: true));
        Assert.Contains("BR-FXEXT-12", FxExt(unnamed));
        Assert.Contains("BR-FXEXT-08", FxExt(unnamed));   // and the amount of line 3 is missing in the sum of line 1
    }

    [Fact]
    public void Totals_OneCentPerItem()
    {
        const string sum = "<ram:LineTotalAmount>30.00</ram:LineTotalAmount><ram:TaxBasisTotalAmount>30.00</ram:TaxBasisTotalAmount>";
        // three items: up to three cents apart
        Assert.DoesNotContain("BR-FXEXT-CO-10", FxExt(Change(Extended, sum, sum.Replace("<ram:LineTotalAmount>30.00", "<ram:LineTotalAmount>30.03", StringComparison.Ordinal))));
        Assert.Contains("BR-FXEXT-CO-10", FxExt(Change(Extended, sum, sum.Replace("<ram:LineTotalAmount>30.00", "<ram:LineTotalAmount>30.04", StringComparison.Ordinal))));
        Assert.Contains("BR-FXEXT-CO-13", FxExt(Change(Extended, sum, sum.Replace("<ram:TaxBasisTotalAmount>30.00", "<ram:TaxBasisTotalAmount>30.04", StringComparison.Ordinal))));
        // the VAT total in another currency than the invoice's: the total with VAT is then the total without VAT
        Assert.Contains("BR-FXEXT-CO-15", FxExt(Change(Extended, "<ram:TaxTotalAmount currencyID=\"EUR\">", "<ram:TaxTotalAmount currencyID=\"USD\">")));
        // amounts collected for a third party go into the amount due
        var third = BeforeBreakdowns(Extended, "<ram:SpecifiedFinancialAdjustment><ram:ActualAmount>2.00</ram:ActualAmount></ram:SpecifiedFinancialAdjustment>");
        Assert.Contains("BR-FXEXT-CO-16", FxExt(third));
        Assert.DoesNotContain("BR-FXEXT-CO-16", FxExt(Change(third, "<ram:DuePayableAmount>34.50", "<ram:DuePayableAmount>36.50")));
    }

    [Fact]
    public void LogisticsCharges_CountAsCharges()
    {
        const string logistics = "<ram:SpecifiedLogisticsServiceCharge><ram:Description>Fracht</ram:Description><ram:AppliedAmount>5.00</ram:AppliedAmount>"
            + "<ram:AppliedTradeTax><ram:TypeCode>VAT</ram:TypeCode><ram:CategoryCode>S</ram:CategoryCode><ram:RateApplicablePercent>19</ram:RateApplicablePercent></ram:AppliedTradeTax></ram:SpecifiedLogisticsServiceCharge>";
        var with = BeforeBreakdowns(Extended, logistics);
        // the charge is not yet in the total without VAT and the breakdown (the sum of charges may be missing: no document level charge, as
        // the artefacts read it)
        Assert.Equal(["BR-FXEXT-CO-13", "BR-FXEXT-S-08b"], FxExt(with).Order(StringComparer.Ordinal));
        var fitting = Change(Change(Change(Change(with,
                    "<ram:LineTotalAmount>30.00</ram:LineTotalAmount><ram:TaxBasisTotalAmount>30.00</ram:TaxBasisTotalAmount>",
                    "<ram:LineTotalAmount>30.00</ram:LineTotalAmount><ram:ChargeTotalAmount>5.00</ram:ChargeTotalAmount><ram:TaxBasisTotalAmount>35.00</ram:TaxBasisTotalAmount>"),
                "<ram:CalculatedAmount>3.80</ram:CalculatedAmount><ram:TypeCode>VAT</ram:TypeCode><ram:BasisAmount>20.00</ram:BasisAmount>",
                "<ram:CalculatedAmount>4.75</ram:CalculatedAmount><ram:TypeCode>VAT</ram:TypeCode><ram:BasisAmount>25.00</ram:BasisAmount>"),
            "<ram:TaxTotalAmount currencyID=\"EUR\">4.50</ram:TaxTotalAmount><ram:GrandTotalAmount>34.50</ram:GrandTotalAmount>",
            "<ram:TaxTotalAmount currencyID=\"EUR\">5.45</ram:TaxTotalAmount><ram:GrandTotalAmount>40.45</ram:GrandTotalAmount>"),
            "<ram:DuePayableAmount>34.50</ram:DuePayableAmount>", "<ram:DuePayableAmount>40.45</ram:DuePayableAmount>");
        Assert.Empty(FxExt(fitting));
        // a logistics charge of a category without its breakdown
        Assert.Contains("BR-FXEXT-Z-01", FxExt(BeforeBreakdowns(Extended, logistics.Replace(">S<", ">Z<", StringComparison.Ordinal).Replace(">19<", ">0<", StringComparison.Ordinal))));
    }

    [Fact]
    public void Breakdowns_PerRateOrPerExemptionReason_StandardTax()
    {
        // two exempt breakdowns at the same rate with different exemption reasons, each adding up with its items: per reason it fits
        const string reasonA = "<ram:ExemptionReason>Befreiung A</ram:ExemptionReason>", reasonB = "<ram:ExemptionReason>Befreiung B</ram:ExemptionReason>";
        string Breakdown(string basis, string reason) =>
            $"<ram:ApplicableTradeTax><ram:CalculatedAmount>0.00</ram:CalculatedAmount><ram:TypeCode>VAT</ram:TypeCode>{reason}<ram:BasisAmount>{basis}</ram:BasisAmount><ram:CategoryCode>E</ram:CategoryCode><ram:RateApplicablePercent>0</ram:RateApplicablePercent></ram:ApplicableTradeTax>";
        var exempt = BeforeBreakdowns(WithLines(Extended,
                Line("1", "20.00", status: "GROUP", bare: true) + Line("2", "12.00", "1", "DETAIL") + Line("3", "8.00", "1", "DETAIL") + Line("4", "10.00", rate: "7")
                + Line("5", "6.00", rate: "0", category: "E", reason: reasonA) + Line("6", "4.00", rate: "0", category: "E", reason: reasonB)),
            Breakdown("6.00", reasonA) + Breakdown("4.00", reasonB));
        Assert.DoesNotContain(FxExt(exempt), i => i is "BR-FXEXT-E-08b" or "BR-FXEXT-E-01");
        // a basis that fits neither the reason nor the rate
        Assert.Contains("BR-FXEXT-E-08b", FxExt(Change(exempt, $"{reasonA}<ram:BasisAmount>6.00", $"{reasonA}<ram:BasisAmount>7.00")));
        // per rate alone it fits as well (one breakdown, two reasons on the items)
        var single = BeforeBreakdowns(WithLines(Extended,
                Line("1", "20.00", status: "GROUP", bare: true) + Line("2", "12.00", "1", "DETAIL") + Line("3", "8.00", "1", "DETAIL") + Line("4", "10.00", rate: "7")
                + Line("5", "6.00", rate: "0", category: "E", reason: reasonA) + Line("6", "4.00", rate: "0", category: "E", reason: reasonB)),
            Breakdown("10.00", reasonA));
        Assert.DoesNotContain("BR-FXEXT-E-08b", FxExt(single));
        // S-09b: the tax of the standard rate within a cent per item (two items at 19 %)
        const string tax = "<ram:CalculatedAmount>3.80</ram:CalculatedAmount>";
        Assert.DoesNotContain("BR-FXEXT-S-09b", FxExt(Change(Extended, tax, "<ram:CalculatedAmount>3.82</ram:CalculatedAmount>")));
        Assert.Contains("BR-FXEXT-S-09b", FxExt(Change(Extended, tax, "<ram:CalculatedAmount>3.83</ram:CalculatedAmount>")));
    }

    [Fact]
    public void NotesRegistrationsAttributesCharges()
    {
        // 01, 02: a note with a subject code has a text or a content code
        Assert.Contains("BR-FXEXT-01", FxExt(Change(Extended, "<ram:IncludedNote><ram:Content>Vielen Dank für den Auftrag</ram:Content></ram:IncludedNote>",
            "<ram:IncludedNote><ram:SubjectCode>AAI</ram:SubjectCode></ram:IncludedNote>")));
        Assert.Contains("BR-FXEXT-02", FxExt(Change(Extended, "<ram:LineID>4</ram:LineID>", "<ram:LineID>4</ram:LineID><ram:IncludedNote><ram:SubjectCode>AAI</ram:SubjectCode></ram:IncludedNote>")));
        // 03: outside the seller only VAT identifiers
        var buyerFc = Change(Extended, "<ram:Name>Max Mustermann</ram:Name>",
            "<ram:Name>Max Mustermann</ram:Name><ram:SpecifiedTaxRegistration><ram:ID schemeID=\"FC\">12/345/67890</ram:ID></ram:SpecifiedTaxRegistration>");
        Assert.Contains("BR-FXEXT-03", FxExt(buyerFc));
        Assert.DoesNotContain("BR-FXEXT-03", FxExt(Change(Extended, "<ram:SpecifiedTaxRegistration><ram:ID schemeID=\"VA\">DE123456789</ram:ID></ram:SpecifiedTaxRegistration>",
            "<ram:SpecifiedTaxRegistration><ram:ID schemeID=\"VA\">DE123456789</ram:ID></ram:SpecifiedTaxRegistration><ram:SpecifiedTaxRegistration><ram:ID schemeID=\"FC\">12/345/67890</ram:ID></ram:SpecifiedTaxRegistration>")));
        // item attributes: a name or code (54-1), one value (54-2), a code of the recommended list (04, a warning with the code as written)
        string Attribute(string inner) => Change(Extended, "<ram:Name>Musterartikel 4</ram:Name>",
            $"<ram:Name>Musterartikel 4</ram:Name><ram:ApplicableProductCharacteristic>{inner}</ram:ApplicableProductCharacteristic>");
        Assert.Empty(FxExt(Attribute("<ram:TypeCode>COLOR_TEXT</ram:TypeCode><ram:Value>Blau</ram:Value>")));
        Assert.Empty(FxExt(Attribute("<ram:Description>Gewicht</ram:Description><ram:ValueMeasure unitCode=\"KGM\">2</ram:ValueMeasure>")));
        Assert.Contains("BR-FXEXT-BR-54-1", FxExt(Attribute("<ram:Value>Blau</ram:Value>")));
        Assert.Contains("BR-FXEXT-BR-54-2", FxExt(Attribute("<ram:Description>Gewicht</ram:Description><ram:Value>2</ram:Value><ram:ValueMeasure unitCode=\"KGM\">2</ram:ValueMeasure>")));
        Assert.Contains("BR-FXEXT-BR-54-2", FxExt(Attribute("<ram:Description>Gewicht</ram:Description><ram:ValueMeasure>2</ram:ValueMeasure>")));
        var unknown = All(Attribute("<ram:TypeCode>XYZ</ram:TypeCode><ram:Value>Blau</ram:Value>")).Single(v => v.Rule.Id == "BR-FXEXT-04");
        Assert.Equal(("XYZ", InvoiceRuleSeverity.Warning), (unknown.Value, unknown.Rule.Severity));
        // a charge without reason or code: on the document (BR-38, CO-22) and on a line (BR-44, CO-24) — the rules of EXTENDED, not those of the standard
        var charge = BeforeBreakdowns(Extended, "<ram:SpecifiedTradeAllowanceCharge><ram:ChargeIndicator><udt:Indicator>true</udt:Indicator></ram:ChargeIndicator>"
            + "<ram:ActualAmount>0.00</ram:ActualAmount><ram:CategoryTradeTax><ram:TypeCode>VAT</ram:TypeCode><ram:CategoryCode>S</ram:CategoryCode>"
            + "<ram:RateApplicablePercent>19</ram:RateApplicablePercent></ram:CategoryTradeTax></ram:SpecifiedTradeAllowanceCharge>");
        Assert.Superset(new HashSet<string> { "BR-FXEXT-BR-38", "BR-FXEXT-CO-22" }, FxExt(charge).ToHashSet());
        Assert.DoesNotContain(Ids(charge), i => i is "BR-38" or "BR-CO-22");
        var lineCharge = Change(Extended, "<ram:SpecifiedTradeSettlementLineMonetarySummation><ram:LineTotalAmount>10.00",
            "<ram:SpecifiedTradeAllowanceCharge><ram:ChargeIndicator><udt:Indicator>true</udt:Indicator></ram:ChargeIndicator><ram:ActualAmount>0.00</ram:ActualAmount>"
            + "</ram:SpecifiedTradeAllowanceCharge><ram:SpecifiedTradeSettlementLineMonetarySummation><ram:LineTotalAmount>10.00");
        Assert.Superset(new HashSet<string> { "BR-FXEXT-BR-44", "BR-FXEXT-CO-24" }, FxExt(lineCharge).ToHashSet());
        Assert.DoesNotContain(Ids(lineCharge), i => i is "BR-44" or "BR-CO-24");
        // split payment on a logistics service charge (a charge in EXTENDED): BR-B-01 and BR-B-02 see it
        Assert.Superset(new HashSet<string> { "BR-B-01", "BR-B-02" }, Ids(BeforeBreakdowns(Extended,
            "<ram:SpecifiedLogisticsServiceCharge><ram:AppliedAmount>0.00</ram:AppliedAmount><ram:AppliedTradeTax><ram:TypeCode>VAT</ram:TypeCode>"
            + "<ram:CategoryCode>B</ram:CategoryCode><ram:RateApplicablePercent>19</ram:RateApplicablePercent></ram:AppliedTradeTax></ram:SpecifiedLogisticsServiceCharge>")).ToHashSet());
        // a negative net price: EXTENDED lists neither BR-27 nor BR-FXEXT-BR-27 (the specification gives it no profile)
        Assert.DoesNotContain(Ids(Change(Extended, Line("2", "12.00", "1", "DETAIL"), Line("2", "12.00", "1", "DETAIL", price: "-1.00"))),
            i => i is "BR-27" or "BR-FXEXT-BR-27");
    }

    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void PreparedInvoice_Linear()
    {
        // 3 000 groups of two DETAIL lines and 3 000 breakdowns (one rate each): the sums once per invoice, not per group or breakdown
        var lines = new System.Text.StringBuilder();
        var breakdowns = new System.Text.StringBuilder();
        for (var g = 0; g < 3000; g++)
        {
            var rate = $"{g % 50}.{g / 50:00}";
            lines.Append(Line($"G{g}", "2.00", status: "GROUP", bare: true)).Append(Line($"D{g}a", "1.00", $"G{g}", "DETAIL", rate))
                .Append(Line($"D{g}b", "1.00", $"G{g}", "DETAIL", rate));
            breakdowns.Append($"<ram:ApplicableTradeTax><ram:CalculatedAmount>0</ram:CalculatedAmount><ram:TypeCode>VAT</ram:TypeCode><ram:BasisAmount>2.00</ram:BasisAmount><ram:CategoryCode>S</ram:CategoryCode><ram:RateApplicablePercent>{rate}</ram:RateApplicablePercent></ram:ApplicableTradeTax>");
        }
        var data = InvoiceReaderTests.Read(BeforeBreakdowns(WithLines(Extended, lines.ToString()), breakdowns.ToString()));
        InvoiceRules.PrepareSchemas();   // every schema compiled before the clock runs: the check is measured, not the first compiling
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var found = InvoiceRules.Check(data);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3 * TestKind.Factor), $"{watch.Elapsed.TotalSeconds:F1} s");
        Assert.DoesNotContain(found, v => v.Rule.Id is "BR-FXEXT-08" or "BR-FXEXT-11" or "BR-FXEXT-06");
    }
}
