using System.Security.Cryptography;
using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// SASLprep (RFC 4013 with the tables of RFC 3454) for the passwords of revision 6, table by table and step by step — running always,
/// without the text of the RFC (Konformitätsabgleich M4, package m-rfc4013-3454). The expected values are data of the RFCs or were
/// computed with Unicode 3.2, not with the code of PDF-Knead; hashes of algorithm 2.B are recomputed here with the primitives of the runtime.
/// </summary>
public sealed partial class EncryptionNormTests
{
    private const string M4fUnassigned = "Das Passwort enthält ein nicht zugewiesenes Zeichen.";
    private const string M4fDirections = "Schreibrichtungen";

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.4, algorithm 2.B, recomputed independently with SHA-256/384/512 and AES-128-CBC of the runtime: the hash of a
    /// prepared password with an 8-byte salt (and /U for the owner, empty for the user).
    /// </summary>
    private static byte[] M4fHash2B(byte[] password, byte[] salt, byte[] userKey)
    {
        var k = SHA256.HashData([.. password, .. salt, .. userKey]);
        using var aes = Aes.Create();
        byte[] e = [];
        for (var round = 0; round < 64 || e[^1] > round - 32; round++)
        {
            byte[] once = [.. password, .. k, .. userKey];
            var k1 = new byte[once.Length * 64];
            for (var i = 0; i < 64; i++) once.CopyTo(k1, i * once.Length);
            aes.Key = k[..16];
            e = aes.EncryptCbc(k1, k[16..32], PaddingMode.None);
            var mod = 0;
            for (var i = 0; i < 16; i++) mod += e[i];   // 256 ≡ 1 (mod 3): the 16 bytes as one big-endian number, modulo 3
            k = (mod % 3) switch { 0 => SHA256.HashData(e), 1 => SHA384.HashData(e), _ => SHA512.HashData(e) };
        }
        return k[..32];
    }

    /// <summary>The first 32 bytes of /U and the user validation salt of an opened AES-256 file.</summary>
    private static (byte[] Hash, byte[] Salt) M4fUserEntry(PdfDocument doc)
    {
        var u = doc.EncryptDictionary!.GetString(PdfName.Get("U"))!.Bytes;
        return (u[..32], u[32..40]);
    }

    /// <summary>
    /// RFC 4013, 3: the seven examples of the RFC — soft hyphen mapped to nothing, a plain word unchanged, capitals kept, U+00AA and
    /// U+2168 normalised (to „a“ and „IX“), U+0007 prohibited, Arabic followed by a digit refused by the bidirectional check.
    /// </summary>
    [Theory]
    [InlineData("I­X", "IX", null)]
    [InlineData("user", "user", null)]
    [InlineData("USER", "USER", null)]
    [InlineData("ª", "a", null)]
    [InlineData("Ⅸ", "IX", null)]
    [InlineData("\u0007", null, "Steuerzeichen")]
    [InlineData("ا" + "1", null, M4fDirections)]
    public void SaslPrep_ExamplesOfRfc4013Section3(string input, string? output, string? error)
    {
        var refusal = StandardSecurityHandler.SaslRefusal(input);
        if (output is not null)
        {
            Assert.Null(refusal);
            Assert.Equal(Encoding.UTF8.GetBytes(output), StandardSecurityHandler.PrepareAesPassword(input));
        }
        else Assert.Contains(error!, refusal ?? "(angenommen)", StringComparison.Ordinal);
    }

    /// <summary>
    /// RFC 4013, 2.2 and RFC 3454, 4: form KC after the mapping — compatibility characters (Roman numeral, ordinal indicator, ligature,
    /// circled digit, fullwidth letter, square unit) and canonical sequences become the composed form; a soft hyphen between a letter and its
    /// accent is mapped away first (RFC 3454, 2: mapping before normalisation), so the two compose. Expected values: NFKC of Unicode 3.2.
    /// </summary>
    [Theory]
    [InlineData("Ⅸ", "IX")]
    [InlineData("ª", "a")]
    [InlineData("é", "é")]
    [InlineData("é", "é")]
    [InlineData("ﬁ", "fi")]
    [InlineData("①", "1")]
    [InlineData("Ａ", "A")]
    [InlineData("㎒", "MHz")]
    [InlineData("Å", "Å")]
    [InlineData("ẛ̣", "ṩ")]
    [InlineData("e­́", "é")]
    public void SaslPrep_Nfkc_AfterTheMapping(string input, string expected) =>
        Assert.Equal(Encoding.UTF8.GetBytes(expected), StandardSecurityHandler.PrepareAesPassword(input));

    /// <summary>
    /// RFC 3454, C.1.2 with RFC 4013, 2.1: each of the 17 non-ASCII space characters becomes U+0020 and therefore never reaches the
    /// prohibition of RFC 4013, 2.3 — the password is allowed.
    /// </summary>
    [Theory]
    [InlineData(0x00A0)]
    [InlineData(0x1680)]
    [InlineData(0x2000)]
    [InlineData(0x2001)]
    [InlineData(0x2002)]
    [InlineData(0x2003)]
    [InlineData(0x2004)]
    [InlineData(0x2005)]
    [InlineData(0x2006)]
    [InlineData(0x2007)]
    [InlineData(0x2008)]
    [InlineData(0x2009)]
    [InlineData(0x200A)]
    [InlineData(0x200B)]
    [InlineData(0x202F)]
    [InlineData(0x205F)]
    [InlineData(0x3000)]
    public void SaslPrep_C12_MappedToSpace(int codePoint)
    {
        var password = "a" + char.ConvertFromUtf32(codePoint) + "b";
        Assert.Equal("a b"u8.ToArray(), StandardSecurityHandler.PrepareAesPassword(password));
        Assert.Null(StandardSecurityHandler.SaslRefusal(password));
    }

    /// <summary>
    /// RFC 4013, 2.3 with the tables C.2.1 to C.9 of RFC 3454 (C.5 see <see cref="SaslPrep_C5_LoneSurrogate_Refused"/>): a code point of each
    /// table, at the ends of its ranges, between two letters, is refused when the password is set, with the reason of its table. Code points in
    /// two tables (U+FFF9–U+FFFC in C.2.2 and C.6, U+206A–U+206F in C.2.2 and C.8) get the reason of the later one. For the noncharacters
    /// (C.4) any refusal counts: the category „unassigned“ of the runtime already refuses them.
    /// </summary>
    [Theory]
    [InlineData("C.2.1", 0x0000, "Steuerzeichen")]
    [InlineData("C.2.1", 0x0007, "Steuerzeichen")]
    [InlineData("C.2.1", 0x001F, "Steuerzeichen")]
    [InlineData("C.2.1", 0x007F, "Steuerzeichen")]
    [InlineData("C.2.2", 0x0080, "Steuerzeichen")]
    [InlineData("C.2.2", 0x0085, "Steuerzeichen")]
    [InlineData("C.2.2", 0x009F, "Steuerzeichen")]
    [InlineData("C.2.2", 0x06DD, "Steuerzeichen")]
    [InlineData("C.2.2", 0x070F, "Steuerzeichen")]
    [InlineData("C.2.2", 0x180E, "Steuerzeichen")]
    [InlineData("C.2.2", 0x2028, "Steuerzeichen")]
    [InlineData("C.2.2", 0x2029, "Steuerzeichen")]
    [InlineData("C.2.2", 0x2061, "Steuerzeichen")]
    [InlineData("C.2.2", 0x2062, "Steuerzeichen")]
    [InlineData("C.2.2", 0x2063, "Steuerzeichen")]
    [InlineData("C.2.2", 0x206A, "Steuerung der Darstellung")]
    [InlineData("C.2.2", 0xFFF9, "Steuerung der Darstellung")]
    [InlineData("C.2.2", 0xFFFC, "Steuerung der Darstellung")]
    [InlineData("C.2.2", 0x1D173, "Steuerzeichen")]
    [InlineData("C.2.2", 0x1D17A, "Steuerzeichen")]
    [InlineData("C.3", 0xE000, "privaten Bereich")]
    [InlineData("C.3", 0xF8FF, "privaten Bereich")]
    [InlineData("C.3", 0xF0000, "privaten Bereich")]
    [InlineData("C.3", 0xFFFFD, "privaten Bereich")]
    [InlineData("C.3", 0x100000, "privaten Bereich")]
    [InlineData("C.3", 0x10FFFD, "privaten Bereich")]
    [InlineData("C.4", 0xFDD0, "")]
    [InlineData("C.4", 0xFDEF, "")]
    [InlineData("C.4", 0xFFFE, "")]
    [InlineData("C.4", 0xFFFF, "")]
    [InlineData("C.4", 0x1FFFE, "")]
    [InlineData("C.4", 0xEFFFF, "")]
    [InlineData("C.4", 0x10FFFE, "")]
    [InlineData("C.4", 0x10FFFF, "")]
    [InlineData("C.6", 0xFFF9, "Steuerung der Darstellung")]
    [InlineData("C.6", 0xFFFA, "Steuerung der Darstellung")]
    [InlineData("C.6", 0xFFFB, "Steuerung der Darstellung")]
    [InlineData("C.6", 0xFFFC, "Steuerung der Darstellung")]
    [InlineData("C.6", 0xFFFD, "Steuerung der Darstellung")]
    [InlineData("C.7", 0x2FF0, "Steuerung der Darstellung")]
    [InlineData("C.7", 0x2FF5, "Steuerung der Darstellung")]
    [InlineData("C.7", 0x2FFB, "Steuerung der Darstellung")]
    [InlineData("C.8", 0x200E, "Steuerung der Darstellung")]
    [InlineData("C.8", 0x200F, "Steuerung der Darstellung")]
    [InlineData("C.8", 0x202A, "Steuerung der Darstellung")]
    [InlineData("C.8", 0x202B, "Steuerung der Darstellung")]
    [InlineData("C.8", 0x202C, "Steuerung der Darstellung")]
    [InlineData("C.8", 0x202D, "Steuerung der Darstellung")]
    [InlineData("C.8", 0x202E, "Steuerung der Darstellung")]
    [InlineData("C.8", 0x206A, "Steuerung der Darstellung")]
    [InlineData("C.8", 0x206F, "Steuerung der Darstellung")]
    [InlineData("C.9", 0xE0001, "Steuerung der Darstellung")]
    [InlineData("C.9", 0xE0020, "Steuerung der Darstellung")]
    [InlineData("C.9", 0xE0041, "Steuerung der Darstellung")]
    [InlineData("C.9", 0xE007F, "Steuerung der Darstellung")]
    public void SaslPrep_ProhibitedTables_RefusedWhenSet(string table, int codePoint, string reason)
    {
        var refusal = StandardSecurityHandler.SaslRefusal("a" + char.ConvertFromUtf32(codePoint) + "b");
        Assert.True(refusal is not null, $"{table} U+{codePoint:X4} angenommen");
        Assert.Contains(reason, refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// RFC 3454, C.5 (surrogate code points) with RFC 4013, 2.3: a lone high or low surrogate, or a low one before a high one, is no
    /// character and is refused when the password is set; a proper pair (a character outside the BMP) is not.
    /// </summary>
    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDBFF)]
    [InlineData(0xDC00)]
    [InlineData(0xDFFF)]
    public void SaslPrep_C5_LoneSurrogate_Refused(int surrogate)
    {
        Assert.Equal("Das Passwort enthält ein ungültiges Zeichen.", StandardSecurityHandler.SaslRefusal("a" + (char)surrogate + "b"));
        Assert.Equal("Das Passwort enthält ein ungültiges Zeichen.", StandardSecurityHandler.SaslRefusal("a" + (char)surrogate));
        Assert.Equal("Das Passwort enthält ein ungültiges Zeichen.", StandardSecurityHandler.SaslRefusal("a\uDC00\uD800b"));
        Assert.Null(StandardSecurityHandler.SaslRefusal("a𐌀b"));   // U+10300, assigned in Unicode 3.2
    }

    /// <summary>
    /// RFC 4013, 2.5 and RFC 3454, A.1 and 7: code points unassigned in Unicode 3.2 — at the ends of the table's ranges and across all planes,
    /// many assigned since (some with a compatibility decomposition today: U+03F9, U+1D2C, U+2150, U+FE10, U+1D7CA), a variation selector of
    /// plane 14 (not in B.1) and tags beside C.9 — are refused in a password that is set.
    /// </summary>
    [Theory]
    [InlineData(0x0221)]
    [InlineData(0x0234)]
    [InlineData(0x024F)]
    [InlineData(0x0370)]
    [InlineData(0x037F)]
    [InlineData(0x03F9)]
    [InlineData(0x03FF)]
    [InlineData(0x0560)]
    [InlineData(0x05A2)]
    [InlineData(0x0620)]
    [InlineData(0x093A)]
    [InlineData(0x0AF0)]
    [InlineData(0x0CBA)]
    [InlineData(0x0EDA)]
    [InlineData(0x1677)]
    [InlineData(0x18AA)]
    [InlineData(0x1D2C)]
    [InlineData(0x1E9E)]
    [InlineData(0x20BF)]
    [InlineData(0x2150)]
    [InlineData(0x2614)]
    [InlineData(0x2B50)]
    [InlineData(0x4DB6)]
    [InlineData(0x9FA6)]
    [InlineData(0xA500)]
    [InlineData(0xD7A4)]
    [InlineData(0xFA2E)]
    [InlineData(0xFB45)]
    [InlineData(0xFDFD)]
    [InlineData(0xFE10)]
    [InlineData(0x10000)]
    [InlineData(0x1D455)]
    [InlineData(0x1D547)]
    [InlineData(0x1D7CA)]
    [InlineData(0x1F600)]
    [InlineData(0x2A6D7)]
    [InlineData(0x2A700)]
    [InlineData(0x2FA1E)]
    [InlineData(0x30000)]
    [InlineData(0xE0000)]
    [InlineData(0xE0002)]
    [InlineData(0xE001F)]
    [InlineData(0xE0080)]
    [InlineData(0xE0100)]
    [InlineData(0xEFFFD)]
    public void SaslPrep_A1_UnassignedIn32_Refused(int codePoint) =>
        Assert.Equal(M4fUnassigned, StandardSecurityHandler.SaslRefusal("a" + char.ConvertFromUtf32(codePoint)));

    /// <summary>
    /// RFC 3454, A.1, the other side of its boundaries: code points assigned in Unicode 3.2 next to a range of A.1 (and some that NFKC
    /// changes) are allowed in a password that is set — the check refuses no more than the table.
    /// </summary>
    [Theory]
    [InlineData(0x0220)]
    [InlineData(0x0233)]
    [InlineData(0x0250)]
    [InlineData(0x02AD)]
    [InlineData(0x02B0)]
    [InlineData(0x02EE)]
    [InlineData(0x0374)]
    [InlineData(0x037A)]
    [InlineData(0x037E)]
    [InlineData(0x03F6)]
    [InlineData(0x0561)]
    [InlineData(0x0587)]
    [InlineData(0x05EA)]
    [InlineData(0x0621)]
    [InlineData(0x0905)]
    [InlineData(0x1E9B)]
    [InlineData(0x2052)]
    [InlineData(0x2057)]
    [InlineData(0x213A)]
    [InlineData(0x214B)]
    [InlineData(0x2153)]
    [InlineData(0x2613)]
    [InlineData(0x4DB5)]
    [InlineData(0x9FA5)]
    [InlineData(0xA48C)]
    [InlineData(0xD7A3)]
    [InlineData(0xFA2D)]
    [InlineData(0xFB4F)]
    [InlineData(0xFDFC)]
    [InlineData(0xFE20)]
    [InlineData(0xFFEE)]
    [InlineData(0x10300)]
    [InlineData(0x1D454)]
    [InlineData(0x1D456)]
    [InlineData(0x1D546)]
    [InlineData(0x1D7C9)]
    [InlineData(0x1D7CE)]
    [InlineData(0x20000)]
    [InlineData(0x2A6D6)]
    [InlineData(0x2F800)]
    [InlineData(0x2FA1D)]
    public void SaslPrep_A1_AssignedIn32_Allowed(int codePoint) =>
        Assert.Null(StandardSecurityHandler.SaslRefusal(char.ConvertFromUtf32(codePoint)));

    /// <summary>
    /// RFC 3454, D.1 and 6: a code point of each range of D.1 (classes R and AL) after a Latin letter makes the password mixed and refused;
    /// characters of the same scripts outside D.1 (marks, Arabic-Indic digits of class AN, an ornate parenthesis, a Hebrew plus sign) do not, nor do the isolated forms of Arabic marks in D.1 that NFKC turns into a space and a mark (the check runs
    /// on the normalised string).
    /// </summary>
    [Theory]
    [InlineData(0x05BE, true)]
    [InlineData(0x05C0, true)]
    [InlineData(0x05C3, true)]
    [InlineData(0x05D0, true)]
    [InlineData(0x05EA, true)]
    [InlineData(0x05F0, true)]
    [InlineData(0x05F4, true)]
    [InlineData(0x061B, true)]
    [InlineData(0x061F, true)]
    [InlineData(0x0621, true)]
    [InlineData(0x063A, true)]
    [InlineData(0x0640, true)]
    [InlineData(0x064A, true)]
    [InlineData(0x066D, true)]
    [InlineData(0x066F, true)]
    [InlineData(0x0671, true)]
    [InlineData(0x06D5, true)]
    [InlineData(0x06E5, true)]
    [InlineData(0x06E6, true)]
    [InlineData(0x06FA, true)]
    [InlineData(0x06FE, true)]
    [InlineData(0x0700, true)]
    [InlineData(0x070D, true)]
    [InlineData(0x0710, true)]
    [InlineData(0x0712, true)]
    [InlineData(0x072C, true)]
    [InlineData(0x0780, true)]
    [InlineData(0x07A5, true)]
    [InlineData(0x07B1, true)]
    [InlineData(0xFB1D, true)]
    [InlineData(0xFB1F, true)]
    [InlineData(0xFB28, true)]
    [InlineData(0xFB2A, true)]
    [InlineData(0xFB36, true)]
    [InlineData(0xFB38, true)]
    [InlineData(0xFB3E, true)]
    [InlineData(0xFB40, true)]
    [InlineData(0xFB43, true)]
    [InlineData(0xFB46, true)]
    [InlineData(0xFBB1, true)]
    [InlineData(0xFBD3, true)]
    [InlineData(0xFD3D, true)]
    [InlineData(0xFD50, true)]
    [InlineData(0xFD8F, true)]
    [InlineData(0xFD92, true)]
    [InlineData(0xFDC7, true)]
    [InlineData(0xFDF0, true)]
    [InlineData(0xFDFC, true)]
    [InlineData(0xFE80, true)]
    [InlineData(0xFEFB, true)]
    [InlineData(0xFEFC, true)]
    [InlineData(0x05BF, false)]
    [InlineData(0x05C1, false)]
    [InlineData(0x064B, false)]
    [InlineData(0x0660, false)]
    [InlineData(0x0670, false)]
    [InlineData(0x06D6, false)]
    [InlineData(0x06F0, false)]
    [InlineData(0x0711, false)]
    [InlineData(0x07A6, false)]
    [InlineData(0xFB1E, false)]
    [InlineData(0xFB29, false)]
    [InlineData(0xFD3E, false)]
    [InlineData(0xFE70, false)]
    [InlineData(0xFE74, false)]
    [InlineData(0xFE76, false)]
    public void SaslPrep_D1_RightToLeft(int codePoint, bool rightToLeft)
    {
        var refusal = StandardSecurityHandler.SaslRefusal("a" + char.ConvertFromUtf32(codePoint));
        if (rightToLeft) Assert.Contains(M4fDirections, refusal ?? "(angenommen)", StringComparison.Ordinal);
        else Assert.Null(refusal);
    }

    /// <summary>
    /// RFC 3454, D.2 and 6: a character of class L (D.2) between two Hebrew letters is refused (no LCat beside RandALCat), across scripts and
    /// planes and also where NFKC changes it; characters of the classes EN, ES, ET, AN, ON, WS and NSM are not.
    /// </summary>
    [Theory]
    [InlineData(0x0041, true)]
    [InlineData(0x007A, true)]
    [InlineData(0x00AA, true)]
    [InlineData(0x00B5, true)]
    [InlineData(0x00C0, true)]
    [InlineData(0x02B0, true)]
    [InlineData(0x0903, true)]
    [InlineData(0x0905, true)]
    [InlineData(0x0E01, true)]
    [InlineData(0x10A0, true)]
    [InlineData(0x3042, true)]
    [InlineData(0x4E00, true)]
    [InlineData(0xAC00, true)]
    [InlineData(0xFF21, true)]
    [InlineData(0x10300, true)]
    [InlineData(0x1D400, true)]
    [InlineData(0x20000, true)]
    [InlineData(0x2F800, true)]
    [InlineData(0x0020, false)]
    [InlineData(0x0021, false)]
    [InlineData(0x002B, false)]
    [InlineData(0x0030, false)]
    [InlineData(0x00A7, false)]
    [InlineData(0x00B2, false)]
    [InlineData(0x00D7, false)]
    [InlineData(0x02B9, false)]
    [InlineData(0x0300, false)]
    [InlineData(0x0591, false)]
    [InlineData(0x0660, false)]
    [InlineData(0x20AC, false)]
    [InlineData(0x2460, false)]
    [InlineData(0x3001, false)]
    public void SaslPrep_D2_LeftToRight(int codePoint, bool leftToRight)
    {
        var refusal = StandardSecurityHandler.SaslRefusal("א" + char.ConvertFromUtf32(codePoint) + "א");
        if (leftToRight) Assert.Contains(M4fDirections, refusal ?? "(angenommen)", StringComparison.Ordinal);
        else Assert.Null(refusal);
        Assert.Null(StandardSecurityHandler.SaslRefusal("a" + char.ConvertFromUtf32(codePoint)));   // no RandALCat: no check of direction
    }

    /// <summary>
    /// RFC 3454, 6, the three requirements on a string with RandALCat (and its example with Alef, digit, Beh): no LCat, and the first and the
    /// last character RandALCat; digits (class EN) or spaces between right-to-left letters are allowed, and strings without RandALCat are
    /// not checked.
    /// </summary>
    [Theory]
    [InlineData("א" + "1" + "א", false)]
    [InlineData("א" + "1", true)]
    [InlineData("1" + "א", true)]
    [InlineData("א" + "1" + "ב", false)]
    [InlineData("ا" + "1", true)]
    [InlineData("ا" + "1" + "ب", false)]
    [InlineData("א" + "a" + "ב", true)]
    [InlineData("aא", true)]
    [InlineData("א" + "a", true)]
    [InlineData("א ב", false)]
    [InlineData("א", false)]
    [InlineData("١٢٣", false)]
    [InlineData("123", false)]
    [InlineData("Abc", false)]
    public void SaslPrep_Bidi_RandAlFirstAndLastWithoutL(string password, bool refused)
    {
        var refusal = StandardSecurityHandler.SaslRefusal(password);
        if (refused) Assert.Contains(M4fDirections, refusal ?? "(angenommen)", StringComparison.Ordinal);
        else Assert.Null(refusal);
    }

    /// <summary>
    /// RFC 3454, 2 (and RFC 4013, 2): the steps run in their order — mapping before the prohibitions (U+200C, U+200D, U+2060 of C.2.2 are in
    /// B.1 and are gone first), normalisation before the prohibitions (U+0340, U+0341 of C.8 become U+0300, U+0301), the bidirectional check
    /// on the normalised string (U+2122 of class ON becomes „TM“ of class L, between Hebrew letters refused).
    /// </summary>
    [Theory]
    [InlineData("a‌b", null)]
    [InlineData("a‍b", null)]
    [InlineData("a⁠b", null)]
    [InlineData("à", null)]
    [InlineData("á", null)]
    [InlineData("א™א", M4fDirections)]
    public void SaslPrep_Order_MapNormaliseProhibitBidi(string password, string? reason)
    {
        var refusal = StandardSecurityHandler.SaslRefusal(password);
        if (reason is null) Assert.Null(refusal);
        else Assert.Contains(reason, refusal ?? "(angenommen)", StringComparison.Ordinal);
    }

    /// <summary>
    /// RFC 3454, 2 and 9.2: normalisation may make the output longer — U+FDFA (3 bytes) becomes 18 characters, 33 bytes in UTF-8; sixty of
    /// them (1980 bytes) are cut to 127 bytes (ISO 32000-2, 7.6.4.3.3), even inside a character, without an exception, and a file with that
    /// password is written and opens with it.
    /// </summary>
    [Fact]
    public void SaslPrep_LongerOutput_HandledAndCutAt127Bytes()
    {
        const string expanded = "صلى الله عليه وسلم";
        Assert.Equal(18, expanded.Length);
        var once = StandardSecurityHandler.PrepareAesPassword("ﷺ");
        Assert.Equal(Encoding.UTF8.GetBytes(expanded), once);
        Assert.Equal(33, once.Length);
        Assert.Null(StandardSecurityHandler.SaslRefusal("ﷺ"));

        var sixty = string.Concat(Enumerable.Repeat("ﷺ", 60));
        var expected = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(expanded, 60)));
        Assert.Equal(1980, expected.Length);
        Assert.Equal(expected[..127], StandardSecurityHandler.PrepareAesPassword(sixty));

        var bytes = Pdf.TestPdf.Simple(1).Build(cipher: PdfCipher.Aes256, user: sixty, owner: "Besitzer");
        using var doc = PdfDocument.Open(bytes, sixty);
        var (hash, salt) = M4fUserEntry(doc);
        Assert.Equal(hash, M4fHash2B(expected[..127], salt, []));
    }

    /// <summary>
    /// RFC 3454, 3.2 with RFC 4013, 2: SASLprep folds no case. A file protected with the user password „user“ has in /U the hash of the bytes
    /// „user“ (algorithm 2.B recomputed here), not of „USER“; it opens with „user“, and „USER“ is a wrong password.
    /// </summary>
    [Fact]
    public void SaslPrep_NoCaseFolding_FileOpensOnlyWithItsOwnCase()
    {
        var bytes = Pdf.TestPdf.Simple(1).Build(cipher: PdfCipher.Aes256, user: "user", owner: "Besitzer");
        using (var doc = PdfDocument.Open(bytes, "user"))
        {
            var (hash, salt) = M4fUserEntry(doc);
            Assert.Equal(hash, M4fHash2B("user"u8.ToArray(), salt, []));
            Assert.NotEqual(hash, M4fHash2B("USER"u8.ToArray(), salt, []));
        }
        Assert.True(Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(bytes, "USER")).WrongPassword);
    }

    /// <summary>
    /// RFC 3454, 7 and 7.1: a query may contain unassigned code points and treats them as assigned — the password given when opening is
    /// not checked against A.1 (no stricter pre-check, a local choice the RFC allows). A file whose user password holds U+1F600 (unassigned
    /// in 3.2; setting it through the program is refused, written here without that check) or U+0378 (unassigned still) opens with it, and /U
    /// is the hash of its UTF-8 bytes as they are.
    /// </summary>
    [Theory]
    [InlineData("a\U0001F600")]
    [InlineData("a͸")]
    public void SaslPrep_Query_UnassignedCodePointsTakenAsAssigned(string password)
    {
        Assert.Equal(M4fUnassigned, StandardSecurityHandler.SaslRefusal(password));   // as a stored string: refused
        var bytes = Pdf.TestPdf.Simple(1).Build(cipher: PdfCipher.Aes256, user: password, owner: "Besitzer");
        using var doc = PdfDocument.Open(bytes, password);
        var (hash, salt) = M4fUserEntry(doc);
        Assert.Equal(hash, M4fHash2B(Encoding.UTF8.GetBytes(password), salt, []));
        Assert.Single(doc.Pages);
    }

    /// <summary>
    /// RFC 3454, 4: the normalisation is that of Unicode 3.2. Five CJK compatibility ideographs were decomposed differently in 3.2 than since
    /// (Corrigendum #4; values of Unicode 3.2): PDF-Knead prepares them as Unicode 3.2 did — before, with the runtime's form, a password with
    /// one of them set by a writer of Unicode 3.2 did not open (docs/FEHLERGEDAECHTNIS.md, No. 1107). Setting such a password is refused
    /// (<see cref="SaslPrep_NormalisedOtherwiseThanIn32_Refused"/>).
    /// </summary>
    [Theory]
    [InlineData(0x2F868, 0x2136A)]
    [InlineData(0x2F874, 0x5F33)]
    [InlineData(0x2F91F, 0x43AB)]
    [InlineData(0x2F95F, 0x7AAE)]
    [InlineData(0x2F9BF, 0x4D57)]
    public void SaslPrep_Opening_NormalisesAsUnicode32(int input, int in32) =>
        Assert.Equal(Encoding.UTF8.GetBytes(char.ConvertFromUtf32(in32)), StandardSecurityHandler.PrepareAesPassword(char.ConvertFromUtf32(input)));
    /// <summary>
    /// A file whose writer hashed the password with the Unicode-3.2 form of a corrected ideograph opens with the ideograph typed; one whose
    /// writer used the later form opens too (the runtime's form tried second) — docs/FEHLERGEDAECHTNIS.md, No. 1107.
    /// </summary>
    [Theory]
    [InlineData(0x2136A)]
    [InlineData(0x36FC)]
    public void SaslPrep_Opening_CorrectedIdeograph_BothFormsOpen(int storedAs)
    {
        var bytes = Pdf.TestPdf.Simple(1).Build(cipher: PdfCipher.Aes256, user: char.ConvertFromUtf32(storedAs), owner: "Besitzer");
        using var doc = PdfDocument.Open(bytes, "㛼");
        Assert.False(doc.Security!.IsOwner);
        Assert.Single(doc.Pages);
    }
}
