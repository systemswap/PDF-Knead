namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// The display shows plain text only (KONZEPT.md, section 5.24, „Bauvorgaben Sicherheit“ 8): control characters — also C1, which terminals
/// may take as commands —, bidi controls and zero-width characters are made visible as ⟨U+XXXX⟩ and reported; long values are cut with their
/// length; a value can be put between bidi isolates; an account is checked as an IBAN; a word that mixes scripts in a name or a payment detail
/// is a note. The texts of the findings name the term, never the value. Only invented invoices and names.
/// </summary>
public sealed class InvoicePlainTextTests
{
    private static InvoiceData Read(string xml) => InvoiceReaderTests.Read(xml);

    private static string Cii => InvoiceReaderTests.Cii;

    [Fact]
    public void Plain_HiddenCharactersVisible_TabAndLineFeedKept()
    {
        Assert.Equal("Musterfirma⟨U+202E⟩HbmG", InvoiceFormat.Plain("Musterfirma\u202EHbmG"));      // right-to-left override
        Assert.Equal("RE⟨U+200B⟩-1", InvoiceFormat.Plain("RE\u200B-1"));                         // zero width space
        Assert.Equal("a⟨U+009B⟩31mb", InvoiceFormat.Plain("a\u009B31mb"));                       // C1: a terminal's command introducer
        Assert.Equal("a⟨U+000D⟩b", InvoiceFormat.Plain("a\rb"));                                // carriage return alone: no overwriting
        Assert.Equal("Zeile 1\r\nZeile 2", InvoiceFormat.Plain("Zeile 1\r\nZeile 2"));          // before a line feed: the end of a line
        Assert.Equal("a⟨U+3164⟩b⟨U+FE0F⟩", InvoiceFormat.Plain("a\u3164b\uFE0F"));              // invisible letter, variation selector
        Assert.Equal("Zeile 1\nZeile 2\tEnde", InvoiceFormat.Plain("Zeile 1\nZeile 2\tEnde"));
        Assert.Equal("Grüße – €", InvoiceFormat.Plain("Grüße – €"));
        Assert.Same("unverändert", InvoiceFormat.Plain("unverändert"));                        // nothing to do: the same string
    }

    [Fact]
    public void Plain_LongValueCutWithItsLength()
    {
        var shown = InvoiceFormat.Plain(new string('x', 5000));
        Assert.StartsWith(new string('x', InvoiceFormat.MaxShown), shown, StringComparison.Ordinal);
        Assert.True(shown.EndsWith("(gekürzt, 5.000 Zeichen)", StringComparison.Ordinal), shown[^40..]);
        Assert.True(shown.Length < InvoiceFormat.MaxShown + 40, shown.Length.ToString());
        // a character above U+FFFF is not split at the cut
        var emoji = InvoiceFormat.Plain(new string('x', 9) + "😀😀", 10);
        Assert.StartsWith(new string('x', 9) + "😀", emoji, StringComparison.Ordinal);
        Assert.Equal("\u2068abc\u2069", InvoiceFormat.Isolated("abc"));
    }

    [Fact]
    public void Display_ValuesAndAttachmentNamesArePlain()
    {
        var data = Read(Cii.Replace("<ram:Name>Musterfirma Beispiel GmbH</ram:Name>", "<ram:Name>Musterfirma\u202EHbmG</ram:Name>")
            .Replace("filename=\"lieferschein.pdf\"", "filename=\"rechnung\u202Efdp.exe\""));
        Assert.Equal("Musterfirma⟨U+202E⟩HbmG", InvoiceFormat.Display(data.Groups("BG-4").Single().Field("BT-27")!, data));
        Assert.StartsWith("rechnung⟨U+202E⟩fdp.exe", InvoiceFormat.Display(data.Groups("BG-24").Single().Field("BT-125")!, data), StringComparison.Ordinal);
    }

    [Fact]
    public void Check_HiddenCharacters_NamedByTermNotByValue()
    {
        var data = Read(Cii.Replace("<ram:Name>Musterfirma Beispiel GmbH</ram:Name>", "<ram:Name>Musterfirma\u202EHbmG</ram:Name>")
            .Replace("<ram:ID>RE-2026-0001</ram:ID>", "<ram:ID>RE\u200B-2026-0001</ram:ID>"));
        var findings = InvoiceFormat.Check(data).Where(f => f.Kind == InvoiceFindingKind.HiddenCharacters).ToList();
        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal(InvoiceFindingWeight.Finding, f.Weight));
        Assert.Contains(findings, f => f.Text.Contains("(BT-27)", StringComparison.Ordinal) && f.Text.Contains("⟨U+202E⟩", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Text.Contains("(BT-1)", StringComparison.Ordinal) && f.Text.Contains("⟨U+200B⟩", StringComparison.Ordinal));
        Assert.DoesNotContain(findings, f => f.Text.Contains("Musterfirma", StringComparison.Ordinal));
        Assert.Empty(InvoiceFormat.Check(Read(Cii)));   // the plain invoice: nothing
    }

    [Fact]
    public void Check_HiddenCharactersInAttributesAndFurtherValues()
    {
        var data = Read(Cii.Replace("filename=\"lieferschein.pdf\"", "filename=\"liefer\u200Bschein.pdf\"")
            .Replace("<udt:DateTimeString format=\"102\">20261113", "<udt:DateTimeString format=\"102\u200D\">20261113")
            .Replace("</rsm:ExchangedDocument>", "<ram:Unbekannt>x\u2066y</ram:Unbekannt></rsm:ExchangedDocument>"));
        var findings = InvoiceFormat.Check(data).Where(f => f.Kind == InvoiceFindingKind.HiddenCharacters).ToList();
        var all = string.Join("\n", findings.Select(f => f.Text));
        // an attribute of a term (the file name of an attachment), an attribute that is no qualifier, an element no row takes
        Assert.True(findings.Any(f => f.Text.Contains("(BT-125", StringComparison.Ordinal) && f.Text.Contains("⟨U+200B⟩", StringComparison.Ordinal)), all);
        Assert.True(findings.Any(f => f.Text.Contains("@format", StringComparison.Ordinal) && f.Text.Contains("⟨U+200D⟩", StringComparison.Ordinal)), all);
        Assert.True(findings.Any(f => f.Text.Contains("Unbekannt", StringComparison.Ordinal) && f.Text.Contains("⟨U+2066⟩", StringComparison.Ordinal)), all);
    }

    [Fact]
    public void Check_ManyOfOneKind_ListedUpToTheLimitThenCounted()
    {
        const string open = "<ram:IncludedSupplyChainTradeLineItem>", close = "</ram:IncludedSupplyChainTradeLineItem>";
        var start = Cii.IndexOf(open, StringComparison.Ordinal);
        var end = Cii.IndexOf(close, StringComparison.Ordinal) + close.Length;
        var line = Cii[start..end].Replace("<ram:Name>Musterartikel</ram:Name>", "<ram:Name>Muster\u200Bartikel</ram:Name>");
        var data = Read(Cii[..start] + string.Concat(Enumerable.Repeat(line, 25)) + Cii[end..]);
        var findings = InvoiceFormat.Check(data).Where(f => f.Kind == InvoiceFindingKind.HiddenCharacters).ToList();
        Assert.Equal(InvoiceFormat.MaxFindingsPerKind + 1, findings.Count);
        Assert.Equal("… und 5 weitere dieser Art.", findings[^1].Text);
    }

    [Fact]
    public void Check_Iban_CheckDigitsAndCharacters()
    {
        const string good = "DE02120300000000202051";
        var data = Read(Cii);
        var account = data.Groups("BG-16").First().GroupsOf("BG-17").First().Field("BT-84")!;
        Assert.Equal("DE02 1203 0000 0000 2020 51", InvoiceFormat.Display(account, data));   // valid: in groups of four
        var wrong = Read(Cii.Replace(good, "DE03120300000000202051"));
        var digits = Assert.Single(InvoiceFormat.Check(wrong), f => f.Kind == InvoiceFindingKind.IbanCheckDigits);
        Assert.Contains("(BT-84)", digits.Text, StringComparison.Ordinal);
        Assert.Equal("DE03120300000000202051", InvoiceFormat.Display(wrong.Groups("BG-16").First().GroupsOf("BG-17").First().Field("BT-84")!, wrong));
        // a Cyrillic „Е“ instead of the Latin E: not plain, and a word of two scripts
        var cyrillic = InvoiceFormat.Check(Read(Cii.Replace(good, "DЕ02120300000000202051")));
        Assert.Contains(cyrillic, f => f.Kind == InvoiceFindingKind.AccountCharacters);
        Assert.Contains(cyrillic, f => f.Kind == InvoiceFindingKind.MixedScripts && f.Text.Contains("lateinisch, kyrillisch", StringComparison.Ordinal));
        // an account that is no IBAN at all is not judged as one
        Assert.DoesNotContain(InvoiceFormat.Check(Read(Cii.Replace(good, "0123456789"))), f => f.Kind == InvoiceFindingKind.IbanCheckDigits);
    }

    [Fact]
    public void Check_MixedScripts_InNamesNotInItems()
    {
        var name = Read(Cii.Replace("<ram:Name>Musterfirma Beispiel GmbH</ram:Name>", "<ram:Name>Musterfirmа Beispiel GmbH</ram:Name>"));
        var note = Assert.Single(InvoiceFormat.Check(name));
        Assert.Equal(InvoiceFindingKind.MixedScripts, note.Kind);
        Assert.Equal(InvoiceFindingWeight.Note, note.Weight);
        Assert.Contains("(BT-27)", note.Text, StringComparison.Ordinal);
        // a unit sign in an item name is common and harmless
        Assert.Empty(InvoiceFormat.Check(Read(Cii.Replace("<ram:Name>Musterartikel</ram:Name>", "<ram:Name>Widerstand 10kΩ</ram:Name>"))));
    }
}
