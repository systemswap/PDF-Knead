namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// The display of the values (KONZEPT.md, section 5.24, stage 2): dates, amounts with currency, percentages, quantities with unit, codes
/// and country names in the language of the program; attachments by their first bytes, never by name. What does not parse stays as written.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class InvoiceFormatTests
{
    private static InvoiceField First(InvoiceData data, string id) => data.Field(id)!;

    [Fact]
    public void German_DatesAmountsPercentQuantityCodes()
    {
        var data = InvoiceReaderTests.Read(InvoiceReaderTests.Cii);
        Assert.Equal("13.11.2026", InvoiceFormat.Display(First(data, "BT-2"), data));
        Assert.Equal("34,50 EUR", InvoiceFormat.Display(First(data, "BT-112"), data));            // currency of the invoice (BT-5)
        Assert.Equal("4,50 EUR", InvoiceFormat.Display(First(data, "BT-110"), data));             // its own currencyID
        Assert.Equal("19 %", InvoiceFormat.Display(data.Groups("BG-23").First().Field("BT-119")!, data));
        Assert.Equal("2 Stück", InvoiceFormat.Display(data.Groups("BG-25").First().Field("BT-129")!, data));
        Assert.Equal("380 (Handelsrechnung)", InvoiceFormat.Display(First(data, "BT-3"), data));
        Assert.Equal("DE (Deutschland)", InvoiceFormat.Display(data.Groups("BG-4").Single().GroupsOf("BG-5").Single().Field("BT-40")!, data));
        Assert.Equal("RE-2026-0001", InvoiceFormat.Display(First(data, "BT-1"), data));
    }

    [Fact]
    public void Numbers_DecimalsAsGiven_NothingInvented()
    {
        Assert.Equal(1234567.8m, InvoiceFormat.Number("1234567.8"));
        Assert.Null(InvoiceFormat.Number("1.234,56"));          // a comma is no decimal separator in XML
        Assert.Null(InvoiceFormat.Number("12 EUR"));
        Assert.Equal(new DateOnly(2026, 11, 13), InvoiceFormat.Date("2026-11-13"));
        Assert.Equal(new DateOnly(2026, 11, 13), InvoiceFormat.Date("20261113"));
        Assert.Null(InvoiceFormat.Date("20261340"));            // no real day
        // only these two forms (FG 831): a sign, a dash in another place or a dash in the eight digits is no date
        Assert.Null(InvoiceFormat.Date("-20261113"));
        Assert.Null(InvoiceFormat.Date("2026-1113"));
        Assert.Null(InvoiceFormat.Date("202611-13"));
        Assert.Null(InvoiceFormat.Date("2-0-2-6-1-1-1-3"));
        Assert.Null(InvoiceFormat.Date("20261113.37"));
        var data = InvoiceReaderTests.Read(InvoiceReaderTests.Cii.Replace("<ram:ChargeAmount>10.00</ram:ChargeAmount>", "<ram:ChargeAmount>10.1234</ram:ChargeAmount>")
            .Replace("<ram:GrandTotalAmount>34.50</ram:GrandTotalAmount>", "<ram:GrandTotalAmount>1234567.8</ram:GrandTotalAmount>")
            .Replace("<udt:DateTimeString format=\"102\">20261113</udt:DateTimeString>", "<udt:DateTimeString format=\"102\">2026-13-45</udt:DateTimeString>"));
        Assert.Equal("10,1234 EUR", InvoiceFormat.Display(data.Groups("BG-25").First().GroupsOf("BG-29").Single().Field("BT-146")!, data));
        Assert.Equal("1.234.567,80 EUR", InvoiceFormat.Display(data.Field("BT-112")!, data));
        Assert.Equal("2026-13-45", InvoiceFormat.Display(data.Field("BT-2")!, data));           // not a date: as written
    }

    [Fact]
    public void English_FollowsTheLanguage()
    {
        var data = InvoiceReaderTests.Read(InvoiceReaderTests.Cii);
        var before = Tr.Current;
        try
        {
            Tr.Use(English.Language);
            Assert.Equal("34.50 EUR", InvoiceFormat.Display(First(data, "BT-112"), data));
            Assert.Equal("DE (Germany)", InvoiceFormat.Display(data.Groups("BG-4").Single().GroupsOf("BG-5").Single().Field("BT-40")!, data));
            Assert.StartsWith("A representation", InvoiceData.Disclaimer);
        }
        finally
        {
            Tr.Use(before);
        }
        Assert.Equal("Kosovo", InvoiceCodes.Name("Country", "1A"));
        Assert.Equal("Nordirland", InvoiceCodes.Name("Country", "XI"));
    }

    [Theory]
    [InlineData(new byte[] { (byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-' }, InvoiceAttachment.Kind.Pdf)]
    [InlineData(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, InvoiceAttachment.Kind.Png)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, InvoiceAttachment.Kind.Jpeg)]
    [InlineData(new byte[] { (byte)'P', (byte)'K', 3, 4 }, InvoiceAttachment.Kind.Zip)]
    [InlineData(new byte[] { (byte)'M', (byte)'Z', 0x90, 0 }, InvoiceAttachment.Kind.Program)]
    [InlineData(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }, InvoiceAttachment.Kind.Program)]
    [InlineData(new byte[] { (byte)'#', (byte)'!', (byte)'/', (byte)'b' }, InvoiceAttachment.Kind.Program)]
    [InlineData(new byte[] { (byte)'a', (byte)';', (byte)'b' }, InvoiceAttachment.Kind.Text)]
    [InlineData(new byte[] { 0, 1, 2, 3 }, InvoiceAttachment.Kind.Unknown)]
    public void Attachment_KindByFirstBytes(byte[] data, InvoiceAttachment.Kind kind) => Assert.Equal(kind, InvoiceAttachment.Of(data));

    [Fact]
    public void Attachment_DeclaredTypeComparedProgramsNamed()
    {
        var pdf = "%PDF-1.7"u8.ToArray();
        Assert.False(InvoiceAttachment.Contradicts(pdf, "application/pdf"));
        Assert.True(InvoiceAttachment.Contradicts([0x89, (byte)'P', (byte)'N', (byte)'G'], "application/pdf"));
        Assert.EndsWith("(8 Bytes, application/pdf)", InvoiceAttachment.Describe(pdf, "application/pdf", "lieferschein.pdf"));
        Assert.Contains("passt nicht", InvoiceAttachment.Describe([0x89, (byte)'P', (byte)'N', (byte)'G'], "application/pdf", "rechnung.pdf"));
        Assert.Contains("Programm", InvoiceAttachment.Describe("MZ\u0090\0"u8.ToArray(), "application/pdf", "rechnung.pdf"));
        var data = InvoiceReaderTests.Read(InvoiceReaderTests.Cii);
        Assert.StartsWith("lieferschein.pdf", InvoiceFormat.Display(data.Groups("BG-24").Single().Field("BT-125")!, data));
    }

    [Fact]
    public void PaymentTerms_CashDiscountOfXRechnungSpelledOut()
    {
        const string terms = "#SKONTO#TAGE=14#PROZENT=2.25#\n#SKONTO#TAGE=1#PROZENT=1.00#BASISBETRAG=357.93#\n#SKONTO#TAGE=x#PROZENT=2#\nZahlbar ohne Abzug in 30 Tagen";
        // before the document totals, where the schema has the payment terms — by the tag alone, whatever the line ends of the sample (FG 844)
        var data = InvoiceReaderTests.Read(InvoiceReaderTests.Cii.Replace("<ram:SpecifiedTradeSettlementHeaderMonetarySummation>",
            $"<ram:SpecifiedTradePaymentTerms><ram:Description>{terms}</ram:Description></ram:SpecifiedTradePaymentTerms><ram:SpecifiedTradeSettlementHeaderMonetarySummation>",
            StringComparison.Ordinal));
        var field = data.Field("BT-20")!;
        Assert.Equal("Skonto 2,25 % bei Zahlung innerhalb von 14 Tagen\nSkonto 1,00 % auf 357,93 EUR bei Zahlung innerhalb von 1 Tag\n"
            + "#SKONTO#TAGE=x#PROZENT=2#\nZahlbar ohne Abzug in 30 Tagen", InvoiceFormat.Display(field, data));   // not the form: as written
        var before = Tr.Current;
        try
        {
            Tr.Use(English.Language);
            Assert.StartsWith("Cash discount of 2.25 % for payment within 14 days\n", InvoiceFormat.Display(field, data), StringComparison.Ordinal);
        }
        finally
        {
            Tr.Use(before);
        }
        Assert.Null(InvoiceFormat.Discount("#SKONTO#TAGE=14#PROZENT=-2.00#", "EUR"));
        Assert.Null(InvoiceFormat.Discount("#SKONTO#TAGE=14#PROZENT=2.00#BASIS=1#", "EUR"));
        Assert.Null(InvoiceFormat.Discount("#VERZUG#TAGE=14#PROZENT=2.00#", "EUR"));
    }

    [Fact]
    public void Currencies_NamedFromCldr()
    {
        var data = InvoiceReaderTests.Read(InvoiceReaderTests.Cii);
        Assert.Equal("EUR (Euro)", InvoiceFormat.Display(data.Field("BT-5")!, data));
        Assert.Equal("US-Dollar", InvoiceCodes.Name("Currency", "USD"));
        Assert.Equal("Schweizer Franken", InvoiceCodes.Name("Currency", "CHF"));
        var before = Tr.Current;
        try
        {
            Tr.Use(English.Language);
            Assert.Equal("US Dollar", InvoiceCodes.Name("Currency", "USD"));
        }
        finally
        {
            Tr.Use(before);
        }
    }

    [Fact]
    public void ManyDecimals_AtMostWhatADecimalHolds()
    {
        var data = InvoiceReaderTests.Read(InvoiceReaderTests.Cii.Replace("<ram:ChargeAmount>10.00</ram:ChargeAmount>",
            $"<ram:ChargeAmount>10.{new string('0', 100_000)}</ram:ChargeAmount>", StringComparison.Ordinal));
        var shown = InvoiceFormat.Display(data.Groups("BG-25").First().GroupsOf("BG-29").Single().Field("BT-146")!, data);
        Assert.True(shown.Length < 60, shown.Length.ToString());
    }

    [Fact]
    public void ManyLinesWithoutCurrency_DisplayStaysLinear()
    {
        // FG 825: every amount without currencyID asked for BT-5, and without BT-5 each question walked the whole invoice — 3 000 lines took
        // some 600 MB and seconds, 8 000 lines 2 GB and over a minute. Now one walk: the display of all values allocates a few MB.
        const string open = "<ram:IncludedSupplyChainTradeLineItem>", close = "</ram:IncludedSupplyChainTradeLineItem>";
        var start = InvoiceReaderTests.Cii.IndexOf(open, StringComparison.Ordinal);
        var end = InvoiceReaderTests.Cii.IndexOf(close, StringComparison.Ordinal) + close.Length;
        var xml = InvoiceReaderTests.Cii[..end] + string.Concat(Enumerable.Repeat(InvoiceReaderTests.Cii[start..end], 3000)) + InvoiceReaderTests.Cii[end..];
        xml = xml.Replace("<ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode>", "", StringComparison.Ordinal);
        var data = InvoiceReaderTests.Read(xml);
        Assert.Null(data.Field("BT-5"));
        Assert.True(data.Groups("BG-25").Count() > 3000);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var amounts = 0;
        foreach (var field in data.Root.AllFields())
            if (InvoiceFormat.Display(field, data).Length > 0 && field.Id is "BT-131" or "BT-146" or "BT-148") amounts++;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(amounts > 9000, amounts.ToString());
        Assert.True(allocated < 40L << 20, $"{allocated >> 20} MB");
    }
}
