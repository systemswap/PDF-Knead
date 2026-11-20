using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Localization;
using Xunit;

namespace PdfKnead.Tests.Barcodes;

/// <summary>What the content of a code means, and that nothing in it can deceive or act: look-alike addresses, hidden characters, wrong check digits.</summary>
public class BarcodeMeaningTests
{
    private static string Value(BarcodeMeaning m, string label) => m.Rows.Single(r => r.Label == label).Value;

    /// <summary>A GS1 row by its identifier (the name after it follows the language).</summary>
    private static string Ai(BarcodeMeaning m, string code) => m.Rows.Single(r => r.Label.StartsWith("(" + code + ")", StringComparison.Ordinal)).Value;

    [Fact]
    public void GiroCode_FieldsAndIbanCheck()
    {
        var m = BarcodeInterpreter.Interpret(BarcodeFormat.Qr, "BCD\n002\n1\nSCT\nCOBADEFFXXX\nMax Mustermann\nDE89370400440532013000\nEUR12.5\n\n\nRechnung 4711\n");
        Assert.Equal(Tr.T("Überweisung (GiroCode)"), m.Kind);
        Assert.Equal("Max Mustermann", Value(m, Tr.T("Empfänger")));
        Assert.Equal("DE89 3704 0044 0532 0130 00", Value(m, Tr.T("IBAN")));
        Assert.Equal(12.5m.ToString("N2", Tr.Culture) + " €", Value(m, Tr.T("Betrag")));
        Assert.Equal("Rechnung 4711", Value(m, Tr.T("Verwendungszweck")));
        Assert.Empty(m.Warnings);
        Assert.Null(m.Action);
        // one digit changed: the IBAN check catches it
        var wrong = BarcodeInterpreter.Interpret(BarcodeFormat.Qr, "BCD\n002\n1\nSCT\n\nMax Mustermann\nDE89370400440532013001\nEUR1\n\n\n\n");
        Assert.Contains(Tr.T("Die Prüfziffer der IBAN stimmt nicht — die IBAN ist falsch."), wrong.Warnings);
    }

    [Fact]
    public void SwissQrBill()
    {
        var text = string.Join("\n", "SPC", "0200", "1", "CH4431999123000889012", "S", "Max Muster & Söhne", "Musterstrasse", "123", "8000", "Seldwyla", "CH",
            "", "", "", "", "", "", "", "1949.75", "CHF", "S", "Simon Muster", "Musterstrasse", "1", "8000", "Seldwyla", "CH", "QRR",
            "210000000003139471430009017", "Auftrag vom 15.06.2020", "EPD");
        var m = BarcodeInterpreter.Interpret(BarcodeFormat.Qr, text);
        Assert.Equal(Tr.T("Schweizer QR-Rechnung"), m.Kind);
        Assert.Contains("Max Muster & Söhne", Value(m, Tr.T("Empfänger")));
        Assert.Equal("CH44 3199 9123 0008 8901 2", Value(m, Tr.T("IBAN")));
        Assert.StartsWith(1949.75m.ToString("N2", Tr.Culture), Value(m, Tr.T("Betrag")));
        Assert.Equal("210000000003139471430009017", Value(m, Tr.T("QR-Referenz")));
        Assert.Empty(m.Warnings);
    }

    [Fact]
    public void WebAddress_OfferedToOpen_ExactlyAsWritten()
    {
        var m = BarcodeInterpreter.Interpret(BarcodeFormat.Qr, "https://example.org/rechnung?nr=4711");
        Assert.Equal(Tr.T("Web-Adresse"), m.Kind);
        Assert.Equal("https://example.org/rechnung?nr=4711", m.Action!.Target);
        Assert.Empty(m.Warnings);
    }

    [Theory]
    [InlineData("https://meine-bank.example@angreifer.example/login", "Vor dem @ steht „{0}“ — das ist nicht der Server. Angesteuert wird „{1}“.")]
    [InlineData("https://xn--pple-43d.com/", "Der Servername enthält Sonderbuchstaben ({0}); solche Namen können bekannte Adressen täuschend ähnlich nachahmen.")]
    [InlineData("https://аpple.com/", "Der Servername enthält Sonderbuchstaben ({0}); solche Namen können bekannte Adressen täuschend ähnlich nachahmen.")]   // Cyrillic а
    [InlineData("http://192.168.0.1/admin", "Die Adresse nennt statt eines Namens nur eine IP-Adresse.")]
    [InlineData("http://example.org/", "Die Verbindung wäre unverschlüsselt (http).")]
    public void WebAddress_Deceptions_Warned(string address, string warning)
    {
        var m = BarcodeInterpreter.Interpret(BarcodeFormat.Qr, address);
        var start = Tr.T(warning).Split('{')[0];
        Assert.Contains(m.Warnings, w => w.StartsWith(start, StringComparison.Ordinal));
        // the target is the ASCII form of the server, so nothing hides behind look-alike letters
        if (m.Action is { } action) Assert.True(action.Target.All(c => c < 0x80), action.Target);
    }

    [Theory]
    [InlineData("javascript:alert(document.cookie)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
    [InlineData("intent://scan/#Intent;scheme=zxing;end")]
    public void OtherSchemes_NeverOffered(string text)
    {
        var m = BarcodeInterpreter.Interpret(BarcodeFormat.Qr, text);
        Assert.Null(m.Action);
    }

    [Fact]
    public void MailAddress_OnlyWhatIsShown_IsHandedOver()
    {
        // a blind copy and an attachment the display would not show, a header line smuggled into the subject, an unknown parameter
        var m = BarcodeInterpreter.Interpret(BarcodeFormat.Qr,
            "mailto:info@example.org?subject=Angebot%0D%0ABcc:%20dieb@example.net&bcc=dieb@example.net&attach=~/.ssh/id_ed25519&x-foo=1&body=Guten%20Tag%0Aich");
        var target = m.Action!.Target;
        Assert.StartsWith("mailto:info@example.org?", target);
        Assert.DoesNotContain("bcc=", target, StringComparison.OrdinalIgnoreCase);   // "Bcc:" in the subject is only text now
        Assert.DoesNotContain("attach", target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("x-foo", target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%0D", target, StringComparison.OrdinalIgnoreCase);   // the subject is one line; the text keeps its break
        Assert.Contains("body=Guten%20Tag%0Aich", target);
        Assert.Equal(3, m.Warnings.Count);
        Assert.Contains(m.Warnings, w => w.Contains("dieb@example.net"));
        Assert.Contains(m.Warnings, w => w.Contains("id_ed25519"));
        Assert.Contains(m.Warnings, w => w.Contains("x-foo"));
        Assert.Contains(m.Rows, r => r.Value == "Angebot  Bcc: dieb@example.net");
        // recipients in the parameters too, and copies: shown and handed over
        var both = BarcodeInterpreter.Interpret(BarcodeFormat.Qr, "mailto:?to=a@example.org,b@example.org&cc=c@example.org");
        Assert.Equal("mailto:a@example.org,b@example.org?cc=c@example.org", both.Action!.Target);
        Assert.Contains(both.Rows, r => r.Value == "a@example.org, b@example.org");
        Assert.Empty(both.Warnings);
        // nobody to write to: nothing offered
        Assert.Null(BarcodeInterpreter.Interpret(BarcodeFormat.Qr, "mailto:?subject=Hallo").Action);
    }

    [Fact]
    public void HiddenAndDirectionCharacters_Visible_AndWarned()
    {
        // "Rechnung<RLO>fdp.exe" looks like "Rechnungexe.pdf"
        var m = BarcodeInterpreter.Interpret(BarcodeFormat.Qr, "Rechnung‮fdp.exe\u0007​");
        Assert.NotEmpty(m.Warnings);
        var shown = m.Rows.Single().Value;
        Assert.Contains("⟨U+202E⟩", shown);
        Assert.Contains("⟨U+200B⟩", shown);
        Assert.Contains("␇", shown);
        Assert.DoesNotContain('‮', shown);
        // very long contents are cut for display
        Assert.True(BarcodeInterpreter.Safe(new string('x', 100_000)).Length < 2100);
    }

    [Fact]
    public void WifiContactEvent()
    {
        var wifi = BarcodeInterpreter.Interpret(BarcodeFormat.Qr, @"WIFI:T:WPA;S:Gast\;netz;P:geheim 123;H:true;;");
        Assert.Equal("Gast;netz", Value(wifi, Tr.T("Netzname")));
        Assert.Equal("geheim 123", Value(wifi, Tr.T("Passwort")));
        var card = BarcodeInterpreter.Interpret(BarcodeFormat.Qr, "BEGIN:VCARD\r\nVERSION:3.0\r\nN:Mustermann;Max;;;\r\nFN:Max Mustermann\r\nORG:Beispiel GmbH\r\nTEL;TYPE=work:+49 30 1234567\r\nEMAIL:max@example.org\r\nNOTE:Zeile eins\\nZeile\r\n  zwei\r\nEND:VCARD");
        Assert.Equal("Max Mustermann", Value(card, Tr.T("Name")));
        Assert.Equal("+49 30 1234567", Value(card, Tr.T("Telefon")));
        Assert.Equal("Zeile eins\nZeile zwei", Value(card, Tr.T("Notiz")));
        var mecard = BarcodeInterpreter.Interpret(BarcodeFormat.Qr, "MECARD:N:Mustermann,Max;TEL:0301234;EMAIL:max@example.org;;");
        Assert.Equal("Max Mustermann", Value(mecard, Tr.T("Name")));
        var ev = BarcodeInterpreter.Interpret(BarcodeFormat.Qr, "BEGIN:VEVENT\nSUMMARY:Abnahme\nDTSTART:20261028T100000\nLOCATION:Raum 2\nEND:VEVENT");
        Assert.Equal("Abnahme", Value(ev, Tr.T("Titel")));
        Assert.Equal(new DateTime(2026, 10, 28, 10, 0, 0).ToString("g", Tr.Culture), Value(ev, Tr.T("Beginn")));
    }

    [Fact]
    public void Gs1_ElementStrings()
    {
        var m = BarcodeInterpreter.Interpret(BarcodeFormat.DataMatrix, "01040123451234561726013110ABC12\u001D21XYZ\u001D3103000250", gs1: true);
        Assert.Equal(Tr.T("GS1-Daten"), m.Kind);
        Assert.Equal("04012345123456", Ai(m, "01"));
        Assert.Equal(new DateTime(2026, 1, 31).ToString("d", Tr.Culture), Ai(m, "17"));
        Assert.Equal("ABC12", Ai(m, "10"));
        Assert.Equal("XYZ", Ai(m, "21"));
        Assert.Equal(0.25m.ToString("0.#########", Tr.Culture), Ai(m, "3103"));
        Assert.Empty(m.Warnings);
        // the last digit of the GTIN changed: its check digit no longer fits
        var wrong = BarcodeInterpreter.Interpret(BarcodeFormat.DataMatrix, "0104012345123457", gs1: true);
        Assert.Contains(wrong.Warnings, w => w.Contains("(01)"));
    }

    [Fact]
    public void Articles_PrefixAndIsbn()
    {
        var ean = BarcodeInterpreter.Interpret(BarcodeFormat.Ean13, "4006381333931");
        Assert.Contains(Value(ean, Tr.T("Nummernkreis")), new[] { "GS1 Deutschland", "GS1 Germany" });
        var book = BarcodeInterpreter.Interpret(BarcodeFormat.Ean13, "9783161484100");
        Assert.Equal(Tr.T("Buch (ISBN)"), book.Kind);
        Assert.Equal("316148410X", Value(book, Tr.T("ISBN-10")));
        var upcE = BarcodeInterpreter.Interpret(BarcodeFormat.UpcE, "01234565");
        Assert.Equal("012345000065", Value(upcE, Tr.T("Ausgeschrieben (UPC-A)")));
    }
}
