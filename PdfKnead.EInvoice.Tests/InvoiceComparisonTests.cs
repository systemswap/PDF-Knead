using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Tests;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// Stage 3, the comparison of the XML with the visible text of the PDF (KONZEPT.md, section 5.24, „Weg für Stufe 3“): the search automaton
/// finds what a plain search finds; the page text is prepared with its way back; numbers stand on their own and carry their sign; weak
/// values count only on the row of a strong value of their group; text that is invisible, white on white or beside the page is never
/// „gefunden“ but reported; codes and attachments cannot be checked. Only invented invoices and names.
/// </summary>
public sealed class InvoiceComparisonTests
{
    // ------------------------------------------------------------ search text and automaton

    [Fact]
    public void Patterns_FindWhatAPlainSearchFinds()
    {
        var random = new Random(824);
        for (var round = 0; round < 50; round++)
        {
            var text = new string([.. Enumerable.Range(0, 400).Select(_ => "AB1,. "[random.Next(6)])]);
            var forms = Enumerable.Range(0, 12).Select(_ => new string([.. Enumerable.Range(0, random.Next(1, 5)).Select(_ => "AB1,. "[random.Next(6)])]))
                .Where(f => f.Trim().Length > 0).Distinct().ToList();
            var patterns = new InvoicePatterns();
            var ids = forms.Select(patterns.Add).ToList();
            var found = new SortedSet<(int, int)>();
            patterns.Search(text, (id, start) => found.Add((id, start)));
            var expected = new SortedSet<(int, int)>();
            for (var k = 0; k < forms.Count; k++)
                for (var at = text.IndexOf(forms[k], StringComparison.Ordinal); at >= 0; at = text.IndexOf(forms[k], at + 1, StringComparison.Ordinal))
                    expected.Add((ids[k], at));
            Assert.Equal(expected, found);
        }
    }

    [Fact]
    public void SearchText_SpacesCollapsed_HyphensJoined_WayBack()
    {
        var nbsp = (char)0xA0;
        var page = $"Summe 1{nbsp}234,50  EUR\nMuster-\nmann Max-\nPlanck";
        var search = InvoiceSearchText.Of(page);
        Assert.Equal("SUMME 1 234,50 EUR MUSTERMANN MAX-PLANCK", search.Text);
        var at = search.Text.IndexOf("1 234,50", StringComparison.Ordinal);
        var range = search.Range(at, 8);
        Assert.Equal($"1{nbsp}234,50", page.Substring(range.Start, range.Length));
        Assert.Equal("SUMME1234,50EURMUSTERMANNMAX-PLANCK", search.Compact);
        var compactAt = search.Compact.IndexOf("MUSTERMANN", StringComparison.Ordinal);
        var back = search.CompactRange(compactAt, 10);
        Assert.Equal("Muster-\nmann", page.Substring(back.Start, back.Length));
    }

    [Theory]
    [InlineData("Summe 112,50", "12,50", false)]          // part of a larger number
    [InlineData("Summe 12,505", "12,50", false)]
    [InlineData("Summe 1.212,50", "212,50", false)]
    [InlineData("Summe 12,50.", "12,50", true)]           // end of a sentence
    [InlineData("-5,10 321,40 19 61,07", "321,40", true)] // columns of a table: the fraction before is no group of thousands
    [InlineData("-5,10 321,40 19 61,07", "61,07", true)]  // two digits after the space: no group either
    [InlineData("Bleistift", "STIFT", false)]
    [InlineData("Stift, rot", "STIFT", true)]
    public void Bounded_NumbersAndWordsStandOnTheirOwn(string page, string form, bool expected)
    {
        var text = InvoiceSearchText.Of(page).Text;
        var at = text.IndexOf(InvoiceSearchText.Prepare(form), StringComparison.Ordinal);
        Assert.True(at >= 0);
        Assert.Equal(expected, InvoiceComparison.Bounded(text, at, InvoiceSearchText.Prepare(form).Length));
    }

    [Fact]
    public void Bounded_SpaceBeforeThreeDigits_GroupUnlessTheGapIsWide()
    {
        var text = InvoiceSearchText.Of("3.600,00 19 684,00").Text;
        var at = text.IndexOf("684,00", StringComparison.Ordinal);
        Assert.False(InvoiceComparison.Bounded(text, at, 6));                    // unknown gap: in doubt part of „19 684,00“
        Assert.False(InvoiceComparison.Bounded(text, at, 6, _ => true));         // narrow: a group of thousands
        Assert.True(InvoiceComparison.Bounded(text, at, 6, _ => false));         // wide: a column of a table
    }

    // ------------------------------------------------------------ forms

    [Fact]
    public void Forms_NumbersDatesIdentifiers()
    {
        var amount = InvoiceForms.NumberForms("1234.5", amount: true).Select(f => f.Text).ToList();
        Assert.Contains("1.234,50", amount);
        Assert.Contains("1234,50", amount);
        Assert.Contains("1 234,50", amount);
        Assert.Contains("1,234.50", amount);
        Assert.Contains("1'234.50", amount);
        Assert.DoesNotContain("1.234.50", amount);                                // the group separator is never the decimal one
        var price = InvoiceForms.NumberForms("10.1234", amount: true);
        Assert.Contains(price, f => f.Text == "10,1234" && !f.Rounded);
        Assert.Contains(price, f => f.Text == "10,12" && f.Rounded);
        Assert.Contains("2", InvoiceForms.NumberForms("2.0000", amount: false).Select(f => f.Text));
        Assert.Contains("100,-", InvoiceForms.NumberForms("100", amount: true).Select(f => f.Text));
        Assert.True(InvoiceForms.WeakNumber("19"));
        Assert.True(InvoiceForms.WeakNumber("0.00"));
        Assert.True(InvoiceForms.WeakNumber("13.00"));
        Assert.False(InvoiceForms.WeakNumber("7.90"));
        Assert.False(InvoiceForms.WeakNumber("120"));
        var dates = InvoiceForms.DateForms(new DateOnly(2026, 11, 1)).Select(f => f.Text).ToList();
        Assert.Contains("01.11.2026", dates);
        Assert.Contains("1. November 2026", dates);
        Assert.Contains("November 1, 2026", dates);
        Assert.Contains("1er novembre 2026", dates);
        Assert.Contains("2026-11-01", dates);
    }

    // ------------------------------------------------------------ the comparison on a page

    /// <summary>
    /// A page in Helvetica: each line (x, y, text, operators before it such as „3 Tr“ or „1 1 1 rg“), each in its own graphics state — the
    /// rendering mode and the colour outlast „ET“.
    /// </summary>
    private static PdfDocument Page(params (double X, double Y, string Text, string Before)[] lines)
    {
        var content = new StringBuilder();
        foreach (var (x, y, text, before) in lines)
            content.Append($"q BT {before} /F1 10 Tf {x.ToString(System.Globalization.CultureInfo.InvariantCulture)} {y.ToString(System.Globalization.CultureInfo.InvariantCulture)} Td ({text}) Tj ET Q\n");
        var pdf = TestFiles.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            [3] = "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Resources<</Font<</F1 4 0 R>>>>/Contents 5 0 R>>",
            [4] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
            [5] = ("", content.ToString()),
        });
        return PdfDocument.Open(pdf);
    }

    private static InvoiceMatch Of(InvoiceComparisonResult result, string id, int nth = 0) => result.Matches.Where(m => m.Field.Id == id).ElementAt(nth);

    private static readonly (double, double, string, string)[] Visible =
    [
        (50, 800, "Rechnung RE-2026-0001 vom 13.11.2026", ""),
        (50, 780, "Musterfirma Beispiel GmbH, Musterweg 1, 12345 Musterstadt", ""),
        (50, 700, "1    Musterartikel    2 St\\374ck    10,00    20,00", ""),
        (50, 680, "2    Zweiter Musterartikel    4 St\\374ck    5,00    10,00", ""),
        (50, 580, "Gesamt 34,50 EUR", ""),
        (50, 560, "IBAN DE02 1203 0000 0000 2020 51", ""),
    ];

    [Fact]
    public void Compare_VisibleFound_CodesNotCheckable()
    {
        using var doc = Page(Visible);
        var result = InvoiceComparison.Compare(doc, InvoiceReaderTests.Read(InvoiceReaderTests.Cii));
        Assert.True(result.HasVisibleText);
        var number = Of(result, "BT-1");
        Assert.Equal(InvoiceMatchStatus.Found, number.Status);
        Assert.Equal(1, number.Place!.Page);
        Assert.NotEmpty(number.Place.Quads);
        Assert.True(number.VisibilityChecked);
        Assert.Equal(InvoiceMatchStatus.Found, Of(result, "BT-2").Status);                    // 20261113 as 13.11.2026
        Assert.Equal(InvoiceMatchStatus.Found, Of(result, "BT-27").Status);
        Assert.Equal(InvoiceMatchStatus.Found, Of(result, "BT-112").Status);
        Assert.Equal(InvoiceMatchStatus.Found, Of(result, "BT-84").Status);                   // the IBAN in groups
        Assert.Equal(InvoiceMatchStatus.NotCheckable, Of(result, "BT-3").Status);             // invoice type code
        Assert.Equal(InvoiceMatchStatus.NotCheckable, Of(result, "BT-125").Status);           // attachment
        // weak values of a line on its row: quantity 2, net price 10,00
        Assert.Equal(InvoiceMatchStatus.Found, Of(result, "BT-129").Status);
        Assert.Equal(InvoiceMatchStatus.Found, Of(result, "BT-146").Status);
        Assert.Equal(InvoiceMatchStatus.Found, Of(result, "BT-131").Status);
    }

    /// <summary>
    /// A value visible twice (decided on 15.11.2026: an amount of 275,00 twice on a page): both places are kept, the first as
    /// <see cref="InvoiceMatch.Place"/> — which belongs to the field the comparison does not say (Bauvorgabe P2), the user sees both.
    /// </summary>
    [Fact]
    public void Compare_ValueTwice_BothPlacesKept()
    {
        using var doc = Page([.. Visible, (50, 400, "Ihre Rechnung RE-2026-0001 bitte angeben", "")]);
        var number = Of(InvoiceComparison.Compare(doc, InvoiceReaderTests.Read(InvoiceReaderTests.Cii)), "BT-1");
        Assert.Equal(InvoiceMatchStatus.Found, number.Status);
        Assert.Equal(2, number.AllPlaces.Count);
        Assert.Same(number.Place, number.AllPlaces[0]);
        Assert.NotEqual(number.AllPlaces[0].Quads[0].Y0, number.AllPlaces[1].Quads[0].Y0);
        // a value found once has one place, as before
        using var once = Page(Visible);
        Assert.Single(Of(InvoiceComparison.Compare(once, InvoiceReaderTests.Read(InvoiceReaderTests.Cii)), "BT-1").AllPlaces);
    }

    /// <summary>
    /// An account in a grouping of its own is found (found in the collections, 15.11.2026: „Kontonr. 1234 5678 90“ for the account 1234567890
    /// of the XML was reported as not visible); another number stays not found.
    /// </summary>
    [Fact]
    public void Compare_AccountInItsOwnGrouping_Found()
    {
        var invoice = InvoiceReaderTests.Read(InvoiceReaderTests.Cii.Replace("DE02500105170137075030", "1234567890", StringComparison.Ordinal));
        using var doc = Page([.. Visible[..5], (50, 560, "IBAN DE02 12030000 0000202051", ""), (50, 540, "Kontonr.: 1234 5678 90", "")]);
        var result = InvoiceComparison.Compare(doc, invoice);
        Assert.Equal(InvoiceMatchStatus.Found, Of(result, "BT-84").Status);       // the IBAN in groups of the bank code and the account
        Assert.Equal(InvoiceMatchStatus.Found, Of(result, "BT-84", 1).Status);    // the account number in groups of its own
        using var other = Page([.. Visible, (50, 540, "Kontonr.: 1234 5678 91", "")]);
        Assert.Equal(InvoiceMatchStatus.NotFound, Of(InvoiceComparison.Compare(other, invoice), "BT-84", 1).Status);
    }

    [Fact]
    public void Compare_HiddenText_NeverFound_ButReported()
    {
        using var doc = Page([.. Visible,
            (50, 540, "DE02500105170137075030", "3 Tr"),        // invisible (rendering mode 3): the second IBAN only here
            (50, 520, "Steuer 4,50", "1 1 1 rg"),               // white on white: the tax total only here
            (-400, 500, "Kennung L-1", ""),                     // beside the page: the seller identifier only here
        ]);
        var result = InvoiceComparison.Compare(doc, InvoiceReaderTests.Read(InvoiceReaderTests.Cii));
        foreach (var (id, nth) in new[] { ("BT-84", 1), ("BT-110", 0), ("BT-29", 0) })
        {
            var m = Of(result, id, nth);
            Assert.True(m.Status == InvoiceMatchStatus.NotFound, $"{id}: {m.Status}");
            Assert.Equal("nur in unsichtbarem oder verdecktem Text", m.Note);
        }
    }

    [Fact]
    public void Compare_WeakValueOffItsRow_NotFound()
    {
        // the quantity 2 of line 1 stands only far away from the line
        using var doc = Page(
            (50, 700, "1    Musterartikel    10,00    20,00", ""),
            (300, 300, "Menge 2", ""));
        var result = InvoiceComparison.Compare(doc, InvoiceReaderTests.Read(InvoiceReaderTests.Cii));
        var quantity = Of(result, "BT-129");
        Assert.Equal(InvoiceMatchStatus.NotFound, quantity.Status);
        Assert.Equal("nur ohne den Zusammenhang seiner Zeile", quantity.Note);
        // where it stands is kept beside (the box „Zahlung“ marks it), but it is no place of the field
        Assert.True(quantity.VisibleOutOfContext);
        Assert.Equal(1, Assert.Single(quantity.OutOfContextPlaces).Page);
        Assert.Empty(quantity.AllPlaces);
    }

    [Fact]
    public void Compare_Sign_RoundedAndPartial()
    {
        var xml = InvoiceReaderTests.Cii.Replace("<ram:ChargeAmount>10.00</ram:ChargeAmount>", "<ram:ChargeAmount>10.1234</ram:ChargeAmount>", StringComparison.Ordinal);
        using var doc = Page(
            (50, 700, "1    Musterartikel    10,12    20,00", ""),
            (50, 580, "Gesamt -34,50 EUR", ""),
            (50, 560, "Beispiel GmbH", ""));
        var invoice = InvoiceReaderTests.Read(xml);
        var result = InvoiceComparison.Compare(doc, invoice);
        Assert.Equal(InvoiceMatchStatus.Rounded, Of(result, "BT-146").Status);                // 10.1234 shown as 10,12
        var total = Of(result, "BT-112");
        Assert.Equal(InvoiceMatchStatus.NotFound, total.Status);                               // an invoice (380): 34,50 is not -34,50
        Assert.Equal("nur mit anderem Vorzeichen", total.Note);
        var seller = Of(result, "BT-27");
        Assert.Equal(InvoiceMatchStatus.Partial, seller.Status);                               // „Musterfirma Beispiel GmbH“
        Assert.Equal("2 von 3 Wörtern", seller.Note);
        // a credit note (381): either sign
        var credit = InvoiceReaderTests.Read(xml.Replace("<ram:TypeCode>380</ram:TypeCode>", "<ram:TypeCode>381</ram:TypeCode>", StringComparison.Ordinal));
        Assert.Equal(InvoiceMatchStatus.Found, Of(InvoiceComparison.Compare(doc, credit), "BT-112").Status);
    }

    [Fact]
    public void Compare_Cancelled_ResultsSoFar_RestNotChecked()
    {
        // the user interface cancels after its time limit: nothing is lost that was found, the rest is „nicht geprüft“, never „nicht gefunden“
        using var doc = Page(Visible);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var result = InvoiceComparison.Compare(doc, InvoiceReaderTests.Read(InvoiceReaderTests.Cii), cancel.Token);
        Assert.Equal(0, result.AbortedAfter);
        var number = Of(result, "BT-1");
        Assert.Equal(InvoiceMatchStatus.NotChecked, number.Status);
        Assert.Equal("abgebrochen – 0 von 1 Seiten geprüft", number.Note);
        Assert.Equal(InvoiceMatchStatus.NotCheckable, Of(result, "BT-3").Status);   // a code stays what it is
        Assert.DoesNotContain(result.Matches, m => m.Status == InvoiceMatchStatus.NotFound);
    }

    [Fact]
    public void Compare_NoVisibleText_NothingCheckable()
    {
        using var doc = Page((50, 700, "RE-2026-0001 34,50", "3 Tr"));   // only invisible text (as over a scan)
        var result = InvoiceComparison.Compare(doc, InvoiceReaderTests.Read(InvoiceReaderTests.Cii));
        Assert.False(result.HasVisibleText);
        Assert.All(result.Matches, m => Assert.Equal(InvoiceMatchStatus.NotCheckable, m.Status));
    }
}
