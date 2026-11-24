using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Security;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Findings of the second M4 probe on public-key handlers (ISO 32000-2, 7.6.5; docs/FEHLERGEDAECHTNIS.md, No. 1119), each with a file built
/// here: an enveloped-data object made with the runtime's CMS for a certificate of the test, the encryption dictionary written by hand; the
/// documents hold no encrypted strings or streams, so only the way to the key and the permissions is exercised.
/// </summary>
public sealed class PublicKeyProbeTests
{
    private static X509Certificate2 Person(string name)
    {
        using var rsa = RSA.Create(2048);
        using var created = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet);
    }

    /// <summary>An enveloped-data object for the certificate: 20 bytes of seed and, unless null, 4 bytes of permissions (most significant first).</summary>
    private static string Recipient(X509Certificate2 certificate, int? permissions)
    {
        var data = new byte[permissions is null ? 20 : 24];
        RandomNumberGenerator.Fill(data.AsSpan(0, 20));
        if (permissions is { } p) BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(20), p);
        var envelope = new EnvelopedCms(new ContentInfo(data), new AlgorithmIdentifier(new Oid("2.16.840.1.101.3.4.1.2")));
        envelope.Encrypt(new CmsRecipient(SubjectIdentifierType.IssuerAndSerialNumber, X509CertificateLoader.LoadCertificate(certificate.RawData)));
        return "<" + Convert.ToHexString(envelope.Encode()) + ">";
    }

    private static byte[] FileWith(string encrypt)
    {
        var text = Encoding.Latin1.GetString(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>", encrypt));
        const string id = "<00112233445566778899AABBCCDDEEFF>";
        return Encoding.Latin1.GetBytes(text.Replace("/Root 1 0 R >>", $"/Root 1 0 R /Encrypt 3 0 R /ID [{id} {id}] >>"));
    }

    /// <summary>7.6.5.3: with adbe.pkcs7.s3 only the permissions of revision 2 count — the handler stands as revision 2 (bits 9–12 unknown there,
    /// the user interface leaves them out: PermissionRulesUiTests).</summary>
    [Fact]
    public void S3_PermissionsOfRevision2()
    {
        using var leser = Person("Max Mustermann");
        var file = FileWith($"<< /Filter /Adobe.PubSec /SubFilter /adbe.pkcs7.s3 /V 1 /Length 40 /Recipients [{Recipient(leser, (1 << 2) | (1 << 8))}] >>");
        using var doc = PdfDocument.Open(file, null, [leser]);
        Assert.Equal(2, doc.Security!.Revision);
        Assert.Equal(PdfCipher.Rc4, doc.Security.StreamCipher);
        Assert.True(doc.Security.Permissions.HasFlag(PdfPermissions.Print));
    }

    /// <summary>7.6.5.2: without /SubFilter the kind is taken from the entries (here /Recipients in the dictionary with V 2: s4) and said.</summary>
    [Fact]
    public void WithoutSubFilter_TakenFromTheEntries_Said()
    {
        using var leser = Person("Max Mustermann");
        using var doc = PdfDocument.Open(FileWith($"<< /Filter /Adobe.PubSec /V 2 /Length 128 /Recipients [{Recipient(leser, -1)}] >>"), null, [leser]);
        Assert.Equal(3, doc.Security!.Revision);
        Assert.Contains(doc.Security.Notes, n => n.Contains("adbe.pkcs7.s4", StringComparison.Ordinal));
    }

    /// <summary>Table 24: only bits 2–6 and 9–12 count — bits 1, 7, 8 and 13–32 set give nothing; bit 2 gives everything.</summary>
    [Theory]
    [InlineData(unchecked((int)0xFFFFF0C1), false)]
    [InlineData(2, true)]
    public void OnlyTheDefinedBits_Count(int permissions, bool everything)
    {
        using var leser = Person("Max Mustermann");
        using var doc = PdfDocument.Open(FileWith($"<< /Filter /Adobe.PubSec /SubFilter /adbe.pkcs7.s4 /V 2 /Length 128 /Recipients [{Recipient(leser, permissions)}] >>"), null, [leser]);
        Assert.Equal(everything, doc.Security!.IsOwner);
        Assert.Equal(everything ? PdfPermissions.All : PdfPermissions.Accessibility, doc.Security.Permissions & PdfPermissions.All);
    }

    /// <summary>s5 with the recipients in the dictionary instead of the crypt filter (Table 23 asks for the filter): read, and said.</summary>
    [Fact]
    public void S5_RecipientsInTheDictionary_ReadAndSaid()
    {
        using var leser = Person("Max Mustermann");
        var file = FileWith("<< /Filter /Adobe.PubSec /SubFilter /adbe.pkcs7.s5 /V 4 /CF << /DefaultCryptFilter << /CFM /AESV2 /Length 128 >> >> " +
                            $"/StmF /DefaultCryptFilter /StrF /DefaultCryptFilter /Recipients [{Recipient(leser, -1)}] >>");
        using var doc = PdfDocument.Open(file, null, [leser]);
        Assert.Equal(PdfCipher.Aes128, doc.Security!.StreamCipher);
        Assert.Contains(doc.Security.Notes, n => n.Contains("Verschlüsselungswörterbuch", StringComparison.Ordinal));
    }

    /// <summary>A crypt filter only single streams can name, with recipients of its own: left out and said — its streams would decrypt to
    /// garbage with the document's key (probe A3).</summary>
    [Fact]
    public void FilterWithOwnRecipients_LeftOutAndSaid()
    {
        using var leser = Person("Max Mustermann");
        using var anders = Person("Erika Musterfrau");
        var file = FileWith("<< /Filter /Adobe.PubSec /SubFilter /adbe.pkcs7.s5 /V 4 /CF << " +
                            $"/DefaultCryptFilter << /CFM /AESV2 /Length 128 /Recipients [{Recipient(leser, -1)}] >> " +
                            $"/Eigener << /CFM /AESV2 /Length 128 /Recipients [{Recipient(anders, -1)}] >> >> " +
                            "/StmF /DefaultCryptFilter /StrF /DefaultCryptFilter >>");
        using var doc = PdfDocument.Open(file, null, [leser]);
        Assert.Contains(doc.Security!.Notes, n => n.Contains("Eigener", StringComparison.Ordinal));
        Assert.Null(doc.Security.CipherNamed(Core.Pdf.Objects.PdfName.Get("Eigener")));
        Assert.Equal(PdfCipher.Aes128, doc.Security.CipherNamed(Core.Pdf.Objects.PdfName.Get("DefaultCryptFilter")));
    }

    /// <summary>The 4 bytes of permissions missing in the data of the document's list (allowed only for a stream's own filter): damaged — only
    /// viewing, said; before, every permission was granted (probe A3).</summary>
    [Fact]
    public void PermissionsMissingAtDocumentLevel_OnlyViewing()
    {
        using var leser = Person("Max Mustermann");
        using var doc = PdfDocument.Open(FileWith($"<< /Filter /Adobe.PubSec /SubFilter /adbe.pkcs7.s4 /V 2 /Length 128 /Recipients [{Recipient(leser, null)}] >>"), null, [leser]);
        Assert.False(doc.Security!.IsOwner);
        Assert.Equal(PdfPermissions.Accessibility, doc.Security.Permissions & PdfPermissions.All);
        Assert.Contains(doc.Security.Notes, n => n.Contains("Rechte", StringComparison.Ordinal));
    }

    /// <summary>Only the attachments encrypted for recipients (/StmF and /StrF Identity, /EFF with /AuthEvent /EFOpen): without a certificate the
    /// document opens, the attachments stay locked (7.6.6; probe A4); /AuthEvent /DocOpen asks for the certificate at once.</summary>
    [Theory]
    [InlineData("EFOpen", true)]
    [InlineData("DocOpen", false)]
    public void OnlyAttachments_WithoutCertificate_OpensLocked(string authEvent, bool opens)
    {
        using var leser = Person("Max Mustermann");
        var file = FileWith("<< /Filter /Adobe.PubSec /SubFilter /adbe.pkcs7.s5 /V 4 /CF << " +
                            $"/DefEmbeddedFile << /CFM /AESV2 /Length 128 /AuthEvent /{authEvent} /Recipients [{Recipient(leser, -1)}] >> >> " +
                            "/StmF /Identity /StrF /Identity /EFF /DefEmbeddedFile >>");
        if (!opens)
        {
            Assert.Throws<PdfRecipientException>(() => PdfDocument.Open(file).Dispose());
            return;
        }
        using var doc = PdfDocument.Open(file);
        Assert.True(doc.Security!.AttachmentsLocked);
        Assert.True(doc.Security.IsPublicKey);
    }

    /// <summary>A recipient without full access, the certificate given (not in the store): asking whether the file opens without a password
    /// answers no without trying (before: the exception of the reader, or a decryption with the private key), and the integrity check of the
    /// document does not fail (probe A1).</summary>
    [Fact]
    public void Recipient_OpensWithoutPassword_NoWithoutTrying()
    {
        using var leser = Person("Max Mustermann");
        var file = FileWith($"<< /Filter /Adobe.PubSec /SubFilter /adbe.pkcs7.s4 /V 2 /Length 128 /Recipients [{Recipient(leser, 1 << 2)}] >>");
        using var doc = PdfDocument.Open(file, null, [leser]);
        Assert.False(StandardSecurityHandler.OpensWithoutPassword(doc.EncryptDictionary!, null));
        Assert.False(StandardSecurityHandler.IsOwnerPassword(doc.EncryptDictionary!, null, "irgendwas"));
        Assert.Equal(MacState.None, doc.Integrity.State);
    }

    /// <summary>S-A A67 (7.3.9; docs/FEHLERGEDAECHTNIS.md, No. 1120): /SubFilter into the void counts as absent — the kind is taken from the
    /// entries and said, as without the key; /Recipients into the void in the document's crypt filter counts as absent — the recipients in the
    /// dictionary are read. Before, the bare key counted: the file was taken for an unknown handler.</summary>
    [Fact]
    public void SubFilterAndRecipientsIntoTheVoid_CountAsAbsent()
    {
        using var leser = Person("Max Mustermann");
        using (var doc = PdfDocument.Open(FileWith($"<< /Filter /Adobe.PubSec /SubFilter 990 0 R /V 2 /Length 128 /Recipients [{Recipient(leser, -1)}] >>"), null, [leser]))
        {
            Assert.True(doc.Security!.IsPublicKey);
            Assert.Contains(doc.Security.Notes, n => n.Contains("adbe.pkcs7.s4", StringComparison.Ordinal));
        }
        var s5 = FileWith("<< /Filter /Adobe.PubSec /SubFilter /adbe.pkcs7.s5 /V 4 /CF << /DefaultCryptFilter << /CFM /AESV2 /Length 128 /Recipients 991 0 R >> >> " +
                          $"/StmF /DefaultCryptFilter /StrF /DefaultCryptFilter /Recipients [{Recipient(leser, -1)}] >>");
        using (var doc = PdfDocument.Open(s5, null, [leser]))
        {
            Assert.True(doc.Security!.IsOwner);
            Assert.Contains(doc.Security.Notes, n => n.Contains("Verschlüsselungswörterbuch", StringComparison.Ordinal));
        }
    }
}
