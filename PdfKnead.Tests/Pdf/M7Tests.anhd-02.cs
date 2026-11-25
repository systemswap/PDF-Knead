using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// PDFDocEncoding (ISO 32000-2, Annex D.3, Table D.2 of the text strings; Konformitätsabgleich M7, draft anhd-02): every code read and written
/// as the table gives it — the unassigned codes read leniently as the equal control character and never written as such a byte (since
/// docs/FEHLERGEDAECHTNIS.md, No. 1145 their characters go as UTF-16BE) — and the characters that need special care in XML escaped in the XMP.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>
    /// A code with its character: it decodes to exactly that character, and the character encodes back to exactly that byte, both as a text
    /// string (<see cref="PdfTextEncoding.Encode"/>) and as PDFDocEncoding alone (<see cref="PdfTextEncoding.EncodeDocEncoding"/>).
    /// </summary>
    private static void AnhdD3_Assigned(int code, int unicode)
    {
        var character = ((char)unicode).ToString();
        Assert.Equal(character, PdfTextEncoding.Decode([(byte)code]));
        Assert.Equal([(byte)code], PdfTextEncoding.Encode(character));
        Assert.Equal([(byte)code], PdfTextEncoding.EncodeDocEncoding(character)!);
    }

    /// <summary>
    /// D.3 sentences 1–3 (the legend SR): characters that need a special form in XML — quotation mark and apostrophe as well as &lt;, &gt; and
    /// &amp; — are escaped in the XMP written for a title, and tab and line feed stay as they are (the existing tests cover &lt;, &gt;, &amp;,
    /// the dropped control character and the carriage return).
    /// </summary>
    [Fact]
    public void AnnexD3_Sentences1To3_QuoteAndApostropheEscapedInXmp_TabAndLineFeedKept()
    {
        var pdf = TestPdf.Simple(1, title: "Zitat \"Probe\" und 'Muster'\tmit\nUmbruch");
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "0 0 1 rg 72 600 200 100 re f"u8.ToArray()));
        var result = PdfATests.Write(pdf.Build());
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        var xmp = Encoding.UTF8.GetString(doc.GetStreamData((PdfStream)doc.Catalog!.Get(PdfName.Metadata)));
        Assert.Contains("Zitat &quot;Probe&quot; und &apos;Muster&apos;\tmit\nUmbruch", xmp);
    }

    /// <summary>
    /// D.3 sentences 4–14: the unassigned code 00h reads leniently as U+0000, except at the end of a string, where it is dropped; U+0000 is
    /// written as UTF-16BE, never as the byte 00h, and has no PDFDocEncoding.
    /// </summary>
    [Fact]
    public void AnnexD3_Sentences4To14_Code00_ReadLeniently_DroppedAtTheEnd_WrittenAsUtf16()
    {
        Assert.Equal("\0A", PdfTextEncoding.Decode([0x00, 0x41]));
        Assert.Equal("A", PdfTextEncoding.Decode([0x41, 0x00]));
        Assert.Equal([0xFE, 0xFF, 0x00, 0x00], PdfTextEncoding.Encode("\0"));
        Assert.Null(PdfTextEncoding.EncodeDocEncoding("\0"));
    }

    /// <summary>
    /// D.3 sentences 4–14, 15–32, 33–41, 42–61, 62–75 (01h–08h, 0Bh, 0Ch, 0Eh–17h), 187–199 (7Fh), 227–245 and 264–274 (9Fh), 295–302 (ADh):
    /// the codes the table marks U (unassigned) read leniently as the equal control character (as the Unicode column), but the character is
    /// written as UTF-16BE (FE FF 00 xx), never as the unassigned byte, and has no PDFDocEncoding (since docs/FEHLERGEDAECHTNIS.md, No. 1145).
    /// </summary>
    [Theory]
    [InlineData(0x01)]
    [InlineData(0x02)]
    [InlineData(0x03)]
    [InlineData(0x04)]
    [InlineData(0x05)]
    [InlineData(0x06)]
    [InlineData(0x07)]
    [InlineData(0x08)]
    [InlineData(0x0B)]
    [InlineData(0x0C)]
    [InlineData(0x0E)]
    [InlineData(0x0F)]
    [InlineData(0x10)]
    [InlineData(0x11)]
    [InlineData(0x12)]
    [InlineData(0x13)]
    [InlineData(0x14)]
    [InlineData(0x15)]
    [InlineData(0x16)]
    [InlineData(0x17)]
    [InlineData(0x7F)]
    [InlineData(0x9F)]
    [InlineData(0xAD)]
    public void AnnexD3_UnassignedCode_ReadLeniently_WrittenAsUtf16(int code)
    {
        var character = ((char)code).ToString();
        Assert.Equal(character, PdfTextEncoding.Decode([(byte)code]));
        Assert.Equal([0xFE, 0xFF, 0x00, (byte)code], PdfTextEncoding.Encode(character));
        Assert.Null(PdfTextEncoding.EncodeDocEncoding(character));
    }

    /// <summary>
    /// D.3 sentences 15–32, 33–41 and 42–61: the assigned control codes 09h, 0Ah and 0Dh are tab, line feed and carriage return, in both
    /// directions.
    /// </summary>
    [Theory]
    [InlineData(0x09)]
    [InlineData(0x0A)]
    [InlineData(0x0D)]
    public void AnnexD3_TabLineFeedCarriageReturn_BothWays(int code) => AnhdD3_Assigned(code, code);

    /// <summary>
    /// D.3 sentences 33–41 and 76–86: the codes 18h–1Fh are the spacing accents breve, caron, circumflex, dot above, double acute, ogonek, ring
    /// and small tilde (U+02D8, U+02C7, U+02C6, U+02D9, U+02DD, U+02DB, U+02DA, U+02DC), 20h and 21h space and exclamation mark — read and written
    /// as such.
    /// </summary>
    [Theory]
    [InlineData(0x18, 0x02D8)]
    [InlineData(0x19, 0x02C7)]
    [InlineData(0x1A, 0x02C6)]
    [InlineData(0x1B, 0x02D9)]
    [InlineData(0x1C, 0x02DD)]
    [InlineData(0x1D, 0x02DB)]
    [InlineData(0x1E, 0x02DA)]
    [InlineData(0x1F, 0x02DC)]
    [InlineData(0x20, 0x0020)]
    [InlineData(0x21, 0x0021)]
    public void AnnexD3_Codes18To21_AccentsSpaceExclam(int code, int unicode) => AnhdD3_Assigned(code, unicode);

    /// <summary>
    /// D.3 sentences 33–41: 1Bh is the dot accent in PDFDocEncoding, not the escape that begins a language marker — „A˙de˙B“ keeps both
    /// accents and the letters between them.
    /// </summary>
    [Fact]
    public void AnnexD3_Code1B_IsTheDotAccent_NotALanguageMarker() =>
        Assert.Equal("A˙de˙B", PdfTextEncoding.Decode([0x41, 0x1B, 0x64, 0x65, 0x1B, 0x42]));

    /// <summary>The codes 22h–7Eh with the characters U+0022–U+007E (the Unicode column of Table D.2 for them).</summary>
    public static TheoryData<int> AnhdD3_Codes22To7E
    {
        get
        {
            var data = new TheoryData<int>();
            for (var c = 0x22; c <= 0x7E; c++) data.Add(c);
            return data;
        }
    }

    /// <summary>
    /// D.3 sentences 87–104, 105–107, 108–127, 128–138, 139–158, 159–166 and 167–199 (as far as 22h–7Eh): each of these codes is the character
    /// U+0022–U+007E of the same number, read and written as that byte.
    /// </summary>
    [Theory]
    [MemberData(nameof(AnhdD3_Codes22To7E))]
    public void AnnexD3_Codes22To7E_AsTheirUnicodeCharacter(int code) => AnhdD3_Assigned(code, code);

    /// <summary>
    /// D.3 sentences 167–186, 187–199, 200–207, 208–226, 227–245, 246–253 (8Dh), 254–263 and 264–274: the codes 80h–9Eh are the characters of
    /// the Unicode column — bullet, daggers, ellipsis, dashes, florin, fraction slash, single guillemets, minus sign, per mille, quotation marks,
    /// trade mark, the ligatures fi and fl, and the letters Ł, Œ, Š, Ÿ, Ž, ı, ł, œ, š, ž —, read and written as that byte.
    /// </summary>
    [Theory]
    [InlineData(0x80, 0x2022)]
    [InlineData(0x81, 0x2020)]
    [InlineData(0x82, 0x2021)]
    [InlineData(0x83, 0x2026)]
    [InlineData(0x84, 0x2014)]
    [InlineData(0x85, 0x2013)]
    [InlineData(0x86, 0x0192)]
    [InlineData(0x87, 0x2044)]
    [InlineData(0x88, 0x2039)]
    [InlineData(0x89, 0x203A)]
    [InlineData(0x8A, 0x2212)]
    [InlineData(0x8B, 0x2030)]
    [InlineData(0x8C, 0x201E)]
    [InlineData(0x8D, 0x201C)]
    [InlineData(0x8E, 0x201D)]
    [InlineData(0x8F, 0x2018)]
    [InlineData(0x90, 0x2019)]
    [InlineData(0x91, 0x201A)]
    [InlineData(0x92, 0x2122)]
    [InlineData(0x93, 0xFB01)]
    [InlineData(0x94, 0xFB02)]
    [InlineData(0x95, 0x0141)]
    [InlineData(0x96, 0x0152)]
    [InlineData(0x97, 0x0160)]
    [InlineData(0x98, 0x0178)]
    [InlineData(0x99, 0x017D)]
    [InlineData(0x9A, 0x0131)]
    [InlineData(0x9B, 0x0142)]
    [InlineData(0x9C, 0x0153)]
    [InlineData(0x9D, 0x0161)]
    [InlineData(0x9E, 0x017E)]
    public void AnnexD3_Codes80To9E_AsTheUnicodeColumn(int code, int unicode) => AnhdD3_Assigned(code, unicode);

    /// <summary>The code A0h with the euro sign, and the codes A1h–FFh except ADh with the characters U+00A1–U+00FF of the same number.</summary>
    public static TheoryData<int, int> AnhdD3_CodesA0ToFF
    {
        get
        {
            var data = new TheoryData<int, int> { { 0xA0, 0x20AC } };
            for (var c = 0xA1; c <= 0xFF; c++)
                if (c != 0xAD) data.Add(c, c);
            return data;
        }
    }

    /// <summary>
    /// D.3 sentences 246–253, 264–274, 275–294, 295–302, 303–322, 323–335, 336–355, 356–366 and 367–382: A0h is the euro sign (U+20AC), A1h–FFh
    /// except the unassigned ADh are U+00A1–U+00FF — each read and written as that byte.
    /// </summary>
    [Theory]
    [MemberData(nameof(AnhdD3_CodesA0ToFF))]
    public void AnnexD3_CodesA0ToFF_EuroThenLatin1(int code, int unicode) => AnhdD3_Assigned(code, unicode);
}
