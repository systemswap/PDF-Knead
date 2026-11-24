using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Public-key security handlers (ISO 32000-2, 7.6.5; design decision, 24.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 1117): files of a public test
/// collection encrypted with adbe.pkcs7.s4 and s5 open with the certificate that comes with them; PDF-Knead's own files for recipients
/// (s5, 256-bit AES) open for each group with its permissions and for nobody else; the file key and the data are recomputed here
/// independently (seed from the enveloped data, SHA-256 over seed and recipient objects, AES-256-CBC with the IV in front, 7.6.5.3).
/// </summary>
public sealed class PublicKeyEncryptionTests
{
    // found from this source file, not from the build output: the guards build outside the repository (waechter.sh)
    private static string CollectionFolder([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var root = Path.GetDirectoryName(source)!;
        while (!Directory.Exists(Path.Combine(root, "TestPDFs"))) root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("TestPDFs");
        return Path.Combine(root, "TestPDFs", "pdfium", "fx", "FRC_3.5_part1");
    }

    /// <summary>The certificate of the collection (its password stands in the collection's introduction).</summary>
    private static X509Certificate2 CollectionCertificate() =>
        // as the program loads a certificate file: the private key only in memory, never put into a key store
        X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(CollectionFolder(), "pkcs_12_phantom.pfx"), "111111", X509KeyStorageFlags.EphemeralKeySet);

    private static byte[] FirstContent(PdfDocument doc)
    {
        var contents = doc.Pages[0].Dictionary.Get(PdfName.Get("Contents"));
        var stream = contents as PdfStream ?? (contents as PdfArray)?.Get(0) as PdfStream;
        return doc.GetStreamData(stream!);
    }

    [Theory]
    [InlineData("FRC_3.5_Filter_PubSec_Sub_SubFilter_s4.pdf", PdfCipher.Rc4, 3)]
    [InlineData("FRC_3.5_Filter_PubSec_SubFilter_s5.pdf", PdfCipher.Aes128, 4)]
    [InlineData("FRC_3.5_EncryptMetadata_T.pdf", PdfCipher.Aes128, 4)]
    [InlineData("FRC_3.5_CF_Strf_stmf_DefaultCryptFilter.pdf", PdfCipher.Rc4, 4)]
    public void ForeignFile_WithItsCertificate_Opens(string name, PdfCipher cipher, int revision)
    {
        using var certificate = CollectionCertificate();
        using var doc = PdfDocument.Open(Path.Combine(CollectionFolder(), name), null, [certificate]);
        var security = doc.Security!;
        Assert.True(security.IsPublicKey);
        Assert.Equal("Adobe.PubSec", security.PublicKeyFilter);
        Assert.Equal(cipher, security.StreamCipher);
        Assert.Equal(revision, security.Revision);
        Assert.StartsWith("q\nBT", Encoding.Latin1.GetString(FirstContent(doc)));
        Assert.Empty(doc.Problems);
    }

    [Fact]
    public void ForeignFile_WithoutItsCertificate_SaysSoWithTheHandlersName()
    {
        var error = Assert.Throws<PdfRecipientException>(() => PdfDocument.Open(Path.Combine(CollectionFolder(), "FRC_3.5_Filter_PubSec_SubFilter_s5.pdf")).Dispose());
        Assert.Equal("Adobe.PubSec", error.Filter);
        Assert.Contains("Adobe.PubSec", error.Message);
    }

    // ------------------------------------------------------------ PDF-Knead's own files for recipients

    private static X509Certificate2 Person(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet);
    }

    private static X509Certificate2 PublicPart(X509Certificate2 certificate) => X509CertificateLoader.LoadCertificate(certificate.RawData);

    private static byte[] Written(IReadOnlyList<PdfRecipientGroup> groups, int pages = 2)
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(pages).Build());
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = "2.0", Recipients = groups });
        return output.ToArray();
    }

    [Fact]
    public void OwnFile_EachGroupWithItsPermissions_NobodyElse()
    {
        using var chef = Person("Erika Musterfrau");
        using var leser = Person("Max Mustermann");
        using var fremd = Person("Otto Normalverbraucher");
        var file = Written([new PdfRecipientGroup([PublicPart(chef)], PdfPermissions.All, FullAccess: true),
            new PdfRecipientGroup([PublicPart(leser)], PdfPermissions.Print)]);

        using (var asChef = PdfDocument.Open(file, null, [chef]))
        {
            Assert.True(asChef.Security!.IsOwner);
            Assert.Equal(PdfPermissions.All, asChef.Security.Permissions & PdfPermissions.All);
            Assert.Equal(PdfCipher.Aes256, asChef.Security.StreamCipher);
            Assert.Equal(2, asChef.PageCount);
        }
        using (var asLeser = PdfDocument.Open(file, null, [leser]))
        {
            Assert.False(asLeser.Security!.IsOwner);
            Assert.True(asLeser.Security.Permissions.HasFlag(PdfPermissions.Print));
            Assert.False(asLeser.Security.Permissions.HasFlag(PdfPermissions.Modify));
            Assert.False(asLeser.Security.Permissions.HasFlag(PdfPermissions.Copy));
            Assert.False(asLeser.Security.CanGrantAllPermissions);
        }
        Assert.Throws<PdfRecipientException>(() => PdfDocument.Open(file, null, [fremd]).Dispose());
    }

    /// <summary>The dictionary as 7.6.5.2 and Table 27 ask, and the data recomputed without PDF-Knead's reader: the recipient's enveloped data
    /// gives seed and permissions, SHA-256 over seed and the recipient objects the key, AES-256-CBC (IV in front) the page content.</summary>
    [Fact]
    public void OwnFile_DictionaryAndDataRecomputedIndependently()
    {
        using var leser = Person("Max Mustermann");
        var file = Written([new PdfRecipientGroup([PublicPart(leser)], PdfPermissions.Print | PdfPermissions.Copy)]);
        using var doc = PdfDocument.Open(file, null, [leser]);
        var encrypt = doc.EncryptDictionary!;
        Assert.Equal("Adobe.PubSec", encrypt.GetName(PdfName.Filter)!.Value);
        Assert.Equal("adbe.pkcs7.s5", encrypt.GetName(PdfName.Get("SubFilter"))!.Value);
        Assert.Equal(5, encrypt.GetInteger(PdfName.Get("V")));
        Assert.False(encrypt.ContainsKey(PdfName.Get("R")));
        var filter = encrypt.GetDictionary(PdfName.Get("CF"))!.GetDictionary(PdfName.Get("DefaultCryptFilter"))!;
        Assert.Equal("AESV3", filter.GetName(PdfName.Get("CFM"))!.Value);
        Assert.Equal(256, filter.GetInteger(PdfName.Length));   // bits for public-key handlers
        var recipients = filter.GetArray(PdfName.Get("Recipients"))!;
        var cms = ((PdfString)recipients.Get(0)).Bytes;

        var envelope = new EnvelopedCms();
        envelope.Decode(cms);
        Assert.Equal("2.16.840.1.101.3.4.1.42", envelope.ContentEncryptionAlgorithm.Oid.Value);
        envelope.Decrypt([leser]);
        var content = envelope.ContentInfo.Content;
        Assert.Equal(24, content.Length);
        var permissions = BinaryPrimitives.ReadInt32BigEndian(content.AsSpan(20));
        Assert.Equal(0, permissions & 2);                 // no full access
        Assert.NotEqual(0, permissions & (1 << 2));       // bit 3: print
        Assert.NotEqual(0, permissions & (1 << 4));       // bit 5: copy
        Assert.NotEqual(0, permissions & (1 << 9));       // bit 10: always set by writers
        Assert.Equal(0, permissions & (1 << 3));          // bit 4: no modifying
        var key = SHA256.HashData([.. content.AsSpan(0, 20), .. cms]);

        // the first page's content as it lies in the file, decrypted here with that key (algorithm 1.A: the key itself, IV in front)
        var contents = (PdfReference)doc.Pages[0].Dictionary.GetRaw(PdfName.Get("Contents"))!;
        var raw = ((PdfStream)doc.Resolve(contents.Id)).RawData ?? doc.FileBytes(((PdfStream)doc.Resolve(contents.Id)).DataOffset, ((PdfStream)doc.Resolve(contents.Id)).DataLength).ToArray();
        using var aes = Aes.Create();
        aes.Key = key;
        var plain = aes.DecryptCbc(raw.AsSpan(16), raw.AsSpan(0, 16), PaddingMode.PKCS7);
        Assert.Equal(FirstContent(doc), Inflated(plain));
    }

    private static byte[] Inflated(byte[] data)
    {
        try
        {
            using var z = new System.IO.Compression.ZLibStream(new MemoryStream(data), System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream();
            z.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return data;
        }
    }

    /// <summary>Saved again with the encryption kept, the dictionary stays exactly (the key depends on the recipient objects, not on /ID):
    /// the copy opens for the same recipient; the crypt filter's /Length stays in bits.</summary>
    [Fact]
    public void OwnFile_SavedWithKeptEncryption_StillOpensForTheRecipients()
    {
        using var leser = Person("Max Mustermann");
        var file = Written([new PdfRecipientGroup([PublicPart(leser)], PdfPermissions.All, FullAccess: true)]);
        byte[] copy;
        using (var doc = PdfDocument.Open(file, null, [leser]))
        using (var output = new MemoryStream())
        {
            PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = "2.0", NewFileId = true });
            copy = output.ToArray();
        }
        using var again = PdfDocument.Open(copy, null, [leser]);
        Assert.True(again.Security!.IsPublicKey);
        Assert.Equal(2, again.PageCount);
        Assert.Equal(256, again.EncryptDictionary!.GetDictionary(PdfName.Get("CF"))!.GetDictionary(PdfName.Get("DefaultCryptFilter"))!.GetInteger(PdfName.Length));
        Assert.Throws<PdfRecipientException>(() => PdfDocument.Open(copy).Dispose());
    }

    [Fact]
    public void Recipients_WithoutRsaKey_OrWithoutCertificates_Refused()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var ec = new CertificateRequest("CN=Erika Musterfrau", ecdsa, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        Assert.Contains("RSA", Assert.Throws<PdfException>(() => Written([new PdfRecipientGroup([PublicPart(ec)], PdfPermissions.All)])).Message);
        Assert.Throws<PdfException>(() => Written([new PdfRecipientGroup([], PdfPermissions.All)]));
        Assert.Throws<PdfException>(() => StandardSecurityHandler.CreateForRecipients([], new byte[16]));
    }

    /// <summary>The file key of 7.6.5.3 computed here for each case: SHA-1 cut to the key length, with 0xFFFFFFFF where the metadata stay plain;
    /// SHA-256 for 256-bit AES.</summary>
    [Theory]
    [InlineData(true, false, 128)]
    [InlineData(false, false, 128)]
    [InlineData(true, false, 40)]
    [InlineData(false, true, 256)]
    public void FileKey_AsTheStandardSays(bool encryptMetadata, bool aes256, int keyBits)
    {
        var seed = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();
        byte[] a = [0x30, 0x01, 0xAA], b = [0x30, 0x02, 0xBB, 0xCC];
        var lists = new PdfArray([new PdfString(a), new PdfString(b)]);
        byte[] input = [.. seed, .. a, .. b, .. encryptMetadata ? Array.Empty<byte>() : [0xFF, 0xFF, 0xFF, 0xFF]];
        var expected = aes256 ? SHA256.HashData(input) : SHA1.HashData(input)[..(keyBits / 8)];
        Assert.Equal(expected, StandardSecurityHandler.PublicKeyFileKey(seed, lists, encryptMetadata, aes256, keyBits));
    }

    /// <summary>Damaged recipient data (not CMS, empty, a list of 10 000 entries) end in „no certificate fits“, never in an exception of the
    /// runtime or a long wait.</summary>
    [Theory]
    [InlineData("kaputt")]
    [InlineData("leer")]
    [InlineData("viele")]
    public void DamagedRecipients_SaidNotCrashed(string variant)
    {
        var entries = variant switch
        {
            "kaputt" => "<3082FFFF0102030405>",
            "leer" => "<>",
            _ => string.Concat(Enumerable.Repeat("<300100>", 10_000)),
        };
        var text = Encoding.Latin1.GetString(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Filter /Adobe.PubSec /SubFilter /adbe.pkcs7.s4 /V 2 /Length 128 /Recipients [{entries}] >>"));
        const string id = "<00112233445566778899AABBCCDDEEFF>";
        var file = Encoding.Latin1.GetBytes(text.Replace("/Root 1 0 R >>", $"/Root 1 0 R /Encrypt 3 0 R /ID [{id} {id}] >>"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<PdfRecipientException>(() => PdfDocument.Open(file).Dispose());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), watch.Elapsed.ToString());
    }
    /// <summary>Table 23: a recipient in more than one list gets the permissions of the first list that names it.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Recipient_InTwoLists_FirstListCounts(bool fullFirst)
    {
        using var beide = Person("Erika Musterfrau");
        var full = new PdfRecipientGroup([PublicPart(beide)], PdfPermissions.All, FullAccess: true);
        var printOnly = new PdfRecipientGroup([PublicPart(beide)], PdfPermissions.Print);
        var file = Written(fullFirst ? [full, printOnly] : [printOnly, full]);
        using var doc = PdfDocument.Open(file, null, [beide]);
        Assert.Equal(fullFirst, doc.Security!.IsOwner);
        Assert.Equal(fullFirst, doc.Security.Permissions.HasFlag(PdfPermissions.Copy));
    }

    /// <summary>7.6.5.2: s3 and s4 without crypt filters (V 1 to 3), s5 with them (V 4 and 5) — another combination is refused with its reason.</summary>
    [Theory]
    [InlineData("adbe.pkcs7.s5", 2)]
    [InlineData("adbe.pkcs7.s4", 4)]
    public void SubFilterAndVersion_NotMatching_Refused(string subFilter, int v)
    {
        var text = Encoding.Latin1.GetString(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Filter /Adobe.PubSec /SubFilter /{subFilter} /V {v} /Length 128 /Recipients [<3000>] >>"));
        const string id = "<00112233445566778899AABBCCDDEEFF>";
        var file = Encoding.Latin1.GetBytes(text.Replace("/Root 1 0 R >>", $"/Root 1 0 R /Encrypt 3 0 R /ID [{id} {id}] >>"));
        var error = Assert.Throws<PdfException>(() => PdfDocument.Open(file).Dispose());
        Assert.Contains(subFilter, error.Message);
    }

    /// <summary>A handler that is neither the standard one nor a public-key handler (no /SubFilter of 7.6.5.2, no recipients): refused, the
    /// message names it — PDF-Knead knows no other handlers (7.6.2).</summary>
    [Fact]
    public void UnknownHandler_RefusedWithItsName()
    {
        var text = Encoding.Latin1.GetString(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Filter /PDFK_Eigenes /V 2 /Length 128 >>"));
        const string id = "<00112233445566778899AABBCCDDEEFF>";
        var file = Encoding.Latin1.GetBytes(text.Replace("/Root 1 0 R >>", $"/Root 1 0 R /Encrypt 3 0 R /ID [{id} {id}] >>"));
        var error = Assert.ThrowsAny<PdfException>(() => PdfDocument.Open(file).Dispose());
        Assert.IsNotType<PdfRecipientException>(error);
        Assert.Contains("PDFK_Eigenes", error.Message);
    }
}
