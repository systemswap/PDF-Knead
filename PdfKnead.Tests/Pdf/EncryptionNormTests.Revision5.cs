using System.Security.Cryptography;
using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Revision 5 (AES-256 with a single SHA-256, the extension of ISO 32000-1 before PDF 2.0): read, never written new, and not carried into a
/// PDF 2.0 file (ISO 32000-2, 7.6.4.2, Table 21; Konformitätsabgleich M4). The file is built here independently of PDF-Knead's own
/// encryption, with SHA-256 and AES of the runtime.
/// </summary>
public sealed partial class EncryptionNormTests
{
    private static readonly byte[] R5Key = [.. Enumerable.Range(0, 32).Select(i => (byte)(i * 11 + 3))];

    /// <summary>
    /// A file of revision 5 with the user password „nutzer“ and the owner password „eigner“: /U = SHA-256(password ‖ validation salt) ‖ salts,
    /// /UE = the file key encrypted with SHA-256(password ‖ key salt) (AES-256-CBC, no padding, zero IV), /O and /OE the same with /U appended,
    /// /Perms with „adb“; object 4 a stream with „ABC“ encrypted with the file key (AES-256-CBC, random IV, PKCS#7 padding).
    /// </summary>
    private static byte[] Revision5File(int permissions = -3904, string userPassword = "nutzer")
    {
        static byte[] Sha(params byte[][] parts) => SHA256.HashData(parts.SelectMany(p => p).ToArray());
        static byte[] Wrap(byte[] key, byte[] data)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            return aes.EncryptCbc(data, new byte[16], PaddingMode.None);
        }
        var user = Encoding.UTF8.GetBytes(userPassword);
        var owner = "eigner"u8.ToArray();
        byte[] userValidation = [1, 2, 3, 4, 5, 6, 7, 8], userKey = [9, 10, 11, 12, 13, 14, 15, 16];
        byte[] ownerValidation = [17, 18, 19, 20, 21, 22, 23, 24], ownerKey = [25, 26, 27, 28, 29, 30, 31, 32];
        var u = Sha(user, userValidation).Concat(userValidation).Concat(userKey).ToArray();
        var ue = Wrap(Sha(user, userKey), R5Key);
        var o = Sha(owner, ownerValidation, u).Concat(ownerValidation).Concat(ownerKey).ToArray();
        var oe = Wrap(Sha(owner, ownerKey, u), R5Key);
        var perms = new byte[16];
        BitConverter.GetBytes(permissions).CopyTo(perms, 0);
        perms[4] = perms[5] = perms[6] = perms[7] = 0xFF;
        perms[8] = (byte)'T';
        "adb"u8.CopyTo(perms.AsSpan(9));
        byte[] sealedPerms;
        using (var aes = Aes.Create())
        {
            aes.Key = R5Key;
            sealedPerms = aes.EncryptEcb(perms, PaddingMode.None);
        }
        byte[] stream;
        using (var aes = Aes.Create())
        {
            aes.Key = R5Key;
            var iv = Enumerable.Range(0, 16).Select(i => (byte)(200 - i)).ToArray();
            stream = [.. iv, .. aes.EncryptCbc("ABC"u8, iv, PaddingMode.PKCS7)];
        }
        byte[] page;
        using (var aes = Aes.Create())
        {
            aes.Key = R5Key;
            var iv = Enumerable.Range(0, 16).Select(i => (byte)(100 + i)).ToArray();
            page = [.. iv, .. aes.EncryptCbc("BT /F1 18 Tf 20 100 Td (Revision 5) Tj ET"u8, iv, PaddingMode.PKCS7)];
        }
        static string Hex(byte[] b) => "<" + Convert.ToHexString(b) + ">";

        var output = new MemoryStream();
        void Write(string s) => output.Write(Encoding.Latin1.GetBytes(s));
        Write("%PDF-1.7\n");
        var offsets = new List<long>();
        void Object(string body)
        {
            offsets.Add(output.Position);
            Write($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        Object("<< /Type /Catalog /Pages 2 0 R /Extra 4 0 R /Extensions << /ADBE << /BaseVersion /1.7 /ExtensionLevel 3 >> >> >>");
        Object("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 6 0 R " +
               "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>");
        offsets.Add(output.Position);
        Write($"4 0 obj\n<< /Length {stream.Length} >>\nstream\n");
        output.Write(stream);
        Write("\nendstream\nendobj\n");
        Object($"<< /Filter /Standard /V 5 /R 5 /Length 256 /P {permissions} /O {Hex(o)} /U {Hex(u)} /OE {Hex(oe)} /UE {Hex(ue)} " +
               $"/Perms {Hex(sealedPerms)} /CF << /StdCF << /CFM /AESV3 /AuthEvent /DocOpen /Length 32 >> >> /StmF /StdCF /StrF /StdCF >>");
        offsets.Add(output.Position);
        Write($"6 0 obj\n<< /Length {page.Length} >>\nstream\n");
        output.Write(page);
        Write("\nendstream\nendobj\n");
        var xref = output.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets) Write($"{off:D10} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Encrypt 5 0 R /ID [<0102030405060708090A0B0C0D0E0F10> <0102030405060708090A0B0C0D0E0F10>] >>\n");
        Write($"startxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    /// <summary>
    /// A file of revision 5 opens with its user and its owner password (one SHA-256 instead of algorithm 2.B), the stream decrypts, the user
    /// gets the sealed permissions and the owner all; a wrong password is refused.
    /// </summary>
    [Theory]
    [InlineData("nutzer", false)]
    [InlineData("eigner", true)]
    public void Revision5_OpensWithBothPasswords(string password, bool owner)
    {
        using var doc = PdfDocument.Open(Revision5File(), password);
        Assert.Equal(5, doc.Security!.Revision);
        Assert.Equal(owner, doc.Security.IsOwner);
        Assert.Equal("ABC"u8.ToArray(), doc.DecodeStream(Assert.IsType<PdfStream>(doc.Catalog!.Get(PdfName.Get("Extra")))).Data);
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(Revision5File(), "falsch"));
    }

    /// <summary>
    /// A kept revision 5 is not carried into PDF 2.0: asked for 2.0, the copy is written as 1.7 (Table 21: revision 5 shall not be used;
    /// before, only revisions 2 to 4 were lowered — docs/FEHLERGEDAECHTNIS.md, No. 1098). The copy opens with the same password.
    /// </summary>
    [Fact]
    public void Revision5_Kept_WrittenAs17NotAs20()
    {
        using var doc = PdfDocument.Open(Revision5File(), "nutzer");
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = "2.0", KeepEncryption = true });
        using var saved = PdfDocument.Open(output.ToArray(), "nutzer");
        Assert.Equal(5, saved.Security!.Revision);
        Assert.Equal("1.7", saved.Version);
        Assert.Equal("ABC"u8.ToArray(), saved.DecodeStream(Assert.IsType<PdfStream>(saved.Catalog!.Get(PdfName.Get("Extra")))).Data);
    }
}
