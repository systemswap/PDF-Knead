using System.Text;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Tests.Pdf;

/// <summary>Check digits of IBAN (ISO 13616, MOD 97-10) and card numbers (ISO/IEC 7812, Luhn), and word lists for „Suchen und schwärzen“.</summary>
public sealed class NumberChecksTests
{
    [Theory]
    [InlineData("DE89 3704 0044 0532 0130 00", true)]
    [InlineData("DE89370400440532013000", true)]
    [InlineData("GB82 WEST 1234 5698 7654 32", true)]
    [InlineData("AT61 1904 3002 3457 3201", true)]
    [InlineData("de89 3704 0044 0532 0130 00", true)]
    [InlineData("DE89 3704 0044 0532 0130 01", false)]   // a typo
    [InlineData("DE98 3704 0044 0532 0130 00", false)]   // digits swapped
    [InlineData("DE89 3704", false)]                     // too short
    [InlineData("DE89-3704-0044-0532-0130-00", false)]   // not an IBAN's spelling
    public void Iban(string text, bool valid) => Assert.Equal(valid, NumberChecks.IbanValid(text));

    [Theory]
    [InlineData("4111 1111 1111 1111", true)]
    [InlineData("5500-0000-0000-0004", true)]
    [InlineData("378282246310005", true)]
    [InlineData("4111 1111 1111 1112", false)]
    [InlineData("0000 0000 0000 0000", false)]   // one repeated digit, though the sum fits
    [InlineData("411111111111", false)]          // twelve digits
    [InlineData("4111 1111 1111 1111 1111", false)]
    public void Card(string text, bool valid) => Assert.Equal(valid, NumberChecks.CardValid(text));

    [Fact]
    public void WordList_LinesCellsQuotes_OncEach()
    {
        var terms = WordList.Parse("Max Mustermann\r\n\r\n\"Mustermann, Erika\";AZ 12/345\tProjekt Kranich\nMax Mustermann\n  Musterstadt  ");
        Assert.Equal(["Max Mustermann", "Mustermann, Erika", "AZ 12/345", "Projekt Kranich", "Musterstadt"], terms);
        Assert.Equal(["Jürgen"], WordList.Read([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("Jürgen\n")]));
        Assert.Equal(["Jürgen"], WordList.Read(Encoding.Latin1.GetBytes("Jürgen\n")));   // not UTF-8: Latin-1
        Assert.Equal(terms, WordList.Parse(Encoding.UTF8.GetString(WordList.Write(terms))));
        Assert.Throws<InvalidDataException>(() => WordList.Read(new byte[WordList.MaxBytes + 1]));
    }

    [Fact]
    public void WordList_Utf16AsSpreadsheetsAndEditorsSaveIt_Read()
    {
        // „Unicode-Text“ of a spreadsheet: UTF-16 little endian with BOM, tabs between the cells; big endian as well
        const string table = "Max Mustermann\tMusterstraße Köln\r\nErika Mustermann\r\n";
        Assert.Equal(["Max Mustermann", "Musterstraße Köln", "Erika Mustermann"], WordList.Read([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(table)]));
        Assert.Equal(["Max Mustermann", "Musterstraße Köln", "Erika Mustermann"],
            WordList.Read([.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(table)]));
        // without a BOM, but with a zero byte in every other place: UTF-16 all the same, not Latin-1 with zero characters
        Assert.Equal(["Max Mustermann", "Musterstraße Köln", "Erika Mustermann"], WordList.Read(Encoding.Unicode.GetBytes(table)));
        // a term never carries a control character
        Assert.Equal(["ab"], WordList.Read([(byte)'a', 0, (byte)'b', (byte)'\n']));
    }
}
