using System.Runtime.CompilerServices;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// SASLprep (RFC 4013 with RFC 3454) for the passwords of revision 6 — the parts that run always, without the text of the RFC
/// (Konformitätsabgleich M4, rows to RFC 3454 B and RFC 4013; docs/FEHLERGEDAECHTNIS.md, No. 1095).
/// </summary>
public sealed partial class EncryptionNormTests
{
    /// <summary>
    /// Table B.1 of RFC 3454 (commonly mapped to nothing), code point by code point: between two letters it is gone; U+200B also stands in
    /// C.1.2 and becomes a space (RFC 4013, 2.1 before 2.2).
    /// </summary>
    [Theory]
    [InlineData(0x00AD)]
    [InlineData(0x034F)]
    [InlineData(0x1806)]
    [InlineData(0x180B)]
    [InlineData(0x180C)]
    [InlineData(0x180D)]
    [InlineData(0x200B)]
    [InlineData(0x200C)]
    [InlineData(0x200D)]
    [InlineData(0x2060)]
    [InlineData(0xFE00)]
    [InlineData(0xFE01)]
    [InlineData(0xFE02)]
    [InlineData(0xFE03)]
    [InlineData(0xFE04)]
    [InlineData(0xFE05)]
    [InlineData(0xFE06)]
    [InlineData(0xFE07)]
    [InlineData(0xFE08)]
    [InlineData(0xFE09)]
    [InlineData(0xFE0A)]
    [InlineData(0xFE0B)]
    [InlineData(0xFE0C)]
    [InlineData(0xFE0D)]
    [InlineData(0xFE0E)]
    [InlineData(0xFE0F)]
    [InlineData(0xFEFF)]
    public void SaslPrep_B1_MappedToNothing(int codePoint)
    {
        var prepared = StandardSecurityHandler.PrepareAesPassword("a" + char.ConvertFromUtf32(codePoint) + "b");
        Assert.Equal(codePoint == 0x200B ? "a b"u8.ToArray() : "ab"u8.ToArray(), prepared);
    }

    /// <summary>
    /// SASLprep folds no case (RFC 4013, 2: only C.1.2 to a space and B.1 to nothing; the tables B.2 and B.3 of RFC 3454 are for other
    /// profiles): capitals, final sigma, sharp s and a ligature keep their form — only NFKC applies (the ligature becomes two letters).
    /// </summary>
    [Theory]
    [InlineData("USER", "USER")]
    [InlineData("Σς", "Σς")]
    [InlineData("Straße", "Straße")]
    [InlineData("ﬀ", "ff")]
    [InlineData("Ⅸ", "IX")]
    public void SaslPrep_NoCaseFolding(string password, string expected)
    {
        Assert.Equal(Encoding.UTF8.GetBytes(expected), StandardSecurityHandler.PrepareAesPassword(password));
        Assert.NotEqual(StandardSecurityHandler.PrepareAesPassword("user"), StandardSecurityHandler.PrepareAesPassword("USER"));
    }

    /// <summary>
    /// Code points unassigned in Unicode 3.2 (RFC 3454, A.1) are refused in a password that is set even where NFKC of the runtime turns
    /// them into assigned ones (U+1D2C into „A“, U+03F9 into Σ, U+2150 into 1⁄7) — checked on the input, as RFC 3454, 7 asks. Before, the
    /// check ran on the normalised form only and let 662 such code points through (docs/FEHLERGEDAECHTNIS.md, No. 1095).
    /// </summary>
    [Theory]
    [InlineData("Passᴬ")]
    [InlineData("Ϲ")]
    [InlineData("x⅐")]
    public void SaslPrep_UnassignedIn32_RefusedBeforeNormalisation(string password)
    {
        Assert.Equal("Das Passwort enthält ein nicht zugewiesenes Zeichen.", StandardSecurityHandler.SaslRefusal(password));
    }

    /// <summary>
    /// Input that NFKC of the runtime and of Unicode 3.2 (RFC 3454, 4) normalise differently is refused when set: the five CJK compatibility
    /// ideographs of Corrigendum #4 and a character composing with an earlier one across a combining mark (Corrigendum #5). Ordinary
    /// combining sequences pass (docs/FEHLERGEDAECHTNIS.md, No. 1095).
    /// </summary>
    [Theory]
    [InlineData("a\U0002F868", true)]
    [InlineData("a\U0002F874", true)]
    [InlineData("a\U0002F91F", true)]
    [InlineData("a\U0002F95F", true)]
    [InlineData("a\U0002F9BF", true)]
    [InlineData("\u0B47\u0300\u0B3E", true)]
    [InlineData("ᄀ\u0300ᅡ", true)]
    [InlineData("Cafe\u0301 au lait", false)]
    [InlineData("\u0B47\u0B3E", false)]
    [InlineData("가", false)]
    [InlineData("n\u0303o", false)]
    public void SaslPrep_NormalisedOtherwiseThanIn32_Refused(string password, bool refused)
    {
        var refusal = StandardSecurityHandler.SaslRefusal(password);
        if (refused) Assert.Equal("Das Passwort enthält eine Zeichenfolge, die nach älteren und neueren Unicode-Regeln verschieden normalisiert wird.", refusal);
        else Assert.Null(refusal);
    }

    /// <summary>
    /// 7.6.2, Table 20: /Length belongs to V 2 and 3 (default 40); a file with crypt filters (V 4, AES-128) and no /Length opens with the key
    /// length of its filter (128 bits) — before, 40 bits were assumed and the password was wrong (docs/FEHLERGEDAECHTNIS.md, No. 1097).
    /// </summary>
    [Theory]
    [InlineData("geheim")]
    [InlineData("Besitzer")]
    public void V4_WithoutLength_OpensWithTheKeyLengthOfItsFilter(string password)
    {
        var bytes = Pdf.TestPdf.Simple(1).Build(cipher: PdfCipher.Aes128, user: "geheim", owner: "Besitzer",
            patchEncrypt: e => e.Remove(PdfName.Length));
        using var doc = PdfDocument.Open(bytes, password);
        Assert.Null(doc.EncryptDictionary!.GetInteger(PdfName.Length));
        Assert.Equal(128, doc.Security!.KeyBits);
        Assert.Single(doc.Pages);
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped when the text of RFC 3454 (Normen-ISO/grundlagen/rfc/rfc3454.txt, kept locally only, not in
/// Git; or the folder PDFKNEAD_RFC names) cannot be found.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class Rfc3454FactAttribute : FactAttribute
{
    /// <summary>Marks the test as skipped if the text is missing.</summary>
    /// <param name="source">Path of the test's source file (filled in by the compiler).</param>
    public Rfc3454FactAttribute([CallerFilePath] string source = "")
    {
        if (Path(source) is null) Skip = "Normen-ISO/grundlagen/rfc/rfc3454.txt: the text of the RFC is kept locally only, not in the repository.";
    }

    /// <summary>The path of rfc3454.txt; null if it cannot be found.</summary>
    /// <param name="source">Path of the calling source file (filled in by the compiler).</param>
    public static string? Path([CallerFilePath] string source = "")
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_RFC") is { Length: > 0 } folder)
            return File.Exists(System.IO.Path.Combine(folder, "rfc3454.txt")) ? System.IO.Path.Combine(folder, "rfc3454.txt") : null;
        for (var dir = new DirectoryInfo(System.IO.Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(System.IO.Path.Combine(dir.FullName, "PdfKnead.sln"))) continue;
            var file = System.IO.Path.Combine(dir.FullName, "Normen-ISO", "grundlagen", "rfc", "rfc3454.txt");
            return File.Exists(file) ? file : null;
        }
        return null;
    }
}
