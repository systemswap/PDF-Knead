using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The signer's certificate bound to the signature (docs/ABLAUFPLAN.md, 2.2; ISO 32000-2:2020, 12.8.3.4.5 a; RFC 5035 and RFC 2634): the
/// certificates of a CMS block are not signed — a certificate with the same key, issuer and serial number but another holder, put in place
/// of the real one, showed „Erika Mustermann“ as a valid signer. The signed attribute signing-certificate (v2, v1) names the real one; a
/// wrong hash, serial number or a broken attribute make the signature invalid; a missing one under PAdES leaves the identity unconfirmed with
/// a hint. The other rules of PAdES are hints only (A-12).
/// </summary>
public sealed class CertificateBindingTests
{
    private static readonly Lazy<SigningCertificate> Own = new(() =>
        SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"bindung-{Guid.NewGuid():N}.p12")));

    private const string V2 = "1.2.840.113549.1.9.16.2.47", V1 = "1.2.840.113549.1.9.16.2.12", Sha256 = "2.16.840.1.101.3.4.2.1";

    private static byte[] Signed(SigningCertificate certificate, bool adbe = false)
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var file = PdfSigner.Sign(doc, 0, new InkElement
        {
            Rect = new PdfRectangle(50, 100, 250, 180), Strokes = [[0, 20, 40, 70, 90, 10]], StrokeWidth = 3,
            Digital = new DigitalSigning("Max Mustermann"),
        }, certificate, DateTimeOffset.Now);
        if (!adbe) return file;
        // the method named in the dictionary, the same length: the byte ranges stay
        var text = Encoding.Latin1.GetString(file);
        var at = text.IndexOf("/ETSI.CAdES.detached", StringComparison.Ordinal);
        Assert.True(at > 0);
        Encoding.ASCII.GetBytes("/adbe.pkcs7.detached").CopyTo(file, at);
        return file;
    }

    /// <summary>The block in the gap of the file's signature, without the zeros after it.</summary>
    private static byte[] BlockOf(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        var range = DigitalSignatures.Find(doc).Single().ByteRange;
        var bytes = Convert.FromHexString(Encoding.ASCII.GetString(file, (int)range[1] + 1, (int)(range[2] - range[1] - 2)));
        AsnDecoder.ReadEncodedValue(bytes, AsnEncodingRules.BER, out _, out _, out var length);
        return bytes[..length];
    }

    /// <summary>The file with another block in its gap, made by <paramref name="make"/> over the signed bytes.</summary>
    private static byte[] WithBlock(byte[] file, Func<byte[], byte[]> make)
    {
        using var doc = PdfDocument.Open(file);
        var range = DigitalSignatures.Find(doc).Single().ByteRange;
        byte[] content = [.. file.AsSpan(0, (int)range[1]), .. file.AsSpan((int)range[2], (int)range[3])];
        var hex = Encoding.ASCII.GetBytes(Convert.ToHexString(make(content)));
        var gap = (int)(range[2] - range[1] - 2);
        Assert.True(hex.Length <= gap);
        var copy = (byte[])file.Clone();
        Array.Fill(copy, (byte)'0', (int)range[1] + 1, gap);
        hex.CopyTo(copy, (int)range[1] + 1);
        return copy;
    }

    private static SignatureCheck Check(byte[] file, X509Certificate2Collection? roots = null)
    {
        using var doc = PdfDocument.Open(file);
        return Assert.Single(SignatureVerifier.Verify(doc, null, roots ?? [], CancellationToken.None));
    }

    /// <summary>A block over <paramref name="content"/> signed with <paramref name="certificate"/>, with the given signed attributes (none: no binding).</summary>
    private static byte[] Block(byte[] content, X509Certificate2 certificate, params (string Oid, byte[] Value)[] attributes)
    {
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate) { DigestAlgorithm = new Oid(Sha256), IncludeOption = X509IncludeOption.EndCertOnly };
        foreach (var (oid, value) in attributes) signer.SignedAttributes.Add(new AsnEncodedData(oid, value));
        cms.ComputeSignature(signer, silent: true);
        return cms.Encode();
    }

    /// <summary>
    /// signing-certificate-v2 with the given hash (SHA-256, the default, so without an algorithm field), issuer and serial number — the same
    /// shape is signing-certificate v1 when the hash is SHA-1.
    /// </summary>
    private static byte[] Binding(byte[] hash, byte[] issuer, ReadOnlySpan<byte> serial)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence())
        using (writer.PushSequence())
        {
            writer.WriteOctetString(hash);
            using (writer.PushSequence())
            {
                using (writer.PushSequence())
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, isConstructed: true)))
                    writer.WriteEncodedValue(issuer);
                writer.WriteInteger(serial);
            }
        }
        return writer.Encode();
    }

    // ------------------------------------------------------------ the attack and the wrong bindings: invalid

    [Fact]
    public void SignerCertificateSwapped_SameKey_ListedAsInvalid()
    {
        var real = Own.Value.Certificate;
        var signed = Signed(Own.Value);
        var cms = new SignedCms();
        cms.Decode(BlockOf(signed));
        // the same key, issuer and serial number, another holder — issued with a foreign key (no one needs the real issuer's key)
        using var foreignKey = RSA.Create(2048);
        var request = new CertificateRequest(new X500DistinguishedName("CN=Erika Mustermann"), real.PublicKey, HashAlgorithmName.SHA256);
        using var forged = request.Create(real.IssuerName, X509SignatureGenerator.CreateForRSA(foreignKey, RSASignaturePadding.Pkcs1),
            real.NotBefore, real.NotAfter, real.SerialNumberBytes.Span);
        cms.RemoveCertificate(cms.Certificates.Find(X509FindType.FindByThumbprint, real.Thumbprint, false)[0]);
        cms.AddCertificate(forged);
        var check = Check(WithBlock(signed, _ => cms.Encode()));
        Assert.Equal(SignatureOutcome.Invalid, check.Outcome);
        Assert.Equal(Tr.T("Der Signaturblock nennt ein anderes Zertifikat als das, mit dem unterschrieben wurde: das Zertifikat wurde ausgetauscht."), check.Problem);
    }

    /// <summary>
    /// The hash names the certificate, the serial number beside it another: the hash binds it all the same — valid with a hint (changed on
    /// purpose, docs/FEHLERGEDAECHTNIS.md, No. 483; before: invalid „exchanged“, which only a wrong hash proves).
    /// </summary>
    [Fact]
    public void SigningCertificateAttribute_WrongSerial_HashBinds_Hint()
    {
        var real = Own.Value.Certificate;
        var check = Check(WithBlock(Signed(Own.Value), content => Block(content, real,
            (V2, Binding(SHA256.HashData(real.RawData), real.IssuerName.RawData, [0x7F, 0x01])))));
        Assert.True(SignatureTexts.IsValid(check), check.Problem);
        Assert.Contains(Tr.T("Die Angabe zum Zertifikat im Signaturblock nennt einen anderen Aussteller oder eine andere Seriennummer; die Prüfsumme bindet das Zertifikat trotzdem."), check.Warnings);
    }

    [Fact]
    public void SigningCertificateAttribute_WrongHash_Invalid()
    {
        var real = Own.Value.Certificate;
        using var other = new LongTermValidationTests.TestCa();
        var check = Check(WithBlock(Signed(Own.Value), content => Block(content, real,
            (V2, Binding(SHA256.HashData(other.Root.RawData), real.IssuerName.RawData, real.SerialNumberBytes.Span)))));
        Assert.Equal(SignatureOutcome.Invalid, check.Outcome);
    }

    [Fact]
    public void SigningCertificateAttribute_Malformed_Invalid()
    {
        var check = Check(WithBlock(Signed(Own.Value), content => Block(content, Own.Value.Certificate, (V2, [0x30, 0x03, 0x02, 0x01, 0x05]))));
        Assert.Equal(SignatureOutcome.Invalid, check.Outcome);
        Assert.Equal(Tr.T("Der Signaturblock ist nicht normgerecht (Angabe zum Zertifikat des Unterzeichners)."), check.Problem);
    }

    // ------------------------------------------------------------ missing: a hint and the identity unconfirmed under PAdES

    [Fact]
    public void SigningCertificateAttribute_Missing_HintAndIdentityNotConfirmed()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var roots = new X509Certificate2Collection(X509CertificateLoader.LoadCertificate(ca.Root.RawData));
        var signed = Signed(ca.Signer);
        Assert.True(Check(signed, roots).IdentityConfirmed);   // with the binding PDF Knead writes
        var check = Check(WithBlock(signed, content => Block(content, ca.Signer.Certificate)), roots);
        Assert.True(SignatureTexts.IsValid(check), check.Problem);
        Assert.False(check.IdentityConfirmed);
        Assert.Contains(Tr.T("Der Signaturblock bindet das Zertifikat des Unterzeichners nicht (signing-certificate fehlt); bei PAdES ist das Pflicht."), check.Warnings);
    }

    // ------------------------------------------------------------ counter-checks (also green before)

    /// <summary>signing-certificate v1 (SHA-1, RFC 2634) naming the right certificate: valid.</summary>
    [Fact]
    public void SigningCertificateV1_Sha1_Matches()
    {
        var real = Own.Value.Certificate;
        var check = Check(WithBlock(Signed(Own.Value, adbe: true), content => Block(content, real,
            (V1, Binding(SHA1.HashData(real.RawData), real.IssuerName.RawData, real.SerialNumberBytes.Span)))));
        Assert.True(SignatureTexts.IsValid(check), check.Problem);
        Assert.DoesNotContain(check.Warnings, w => w.Contains("signing-certificate"));
    }

    /// <summary>adbe.pkcs7.detached without the attribute: optional there — no hint.</summary>
    [Fact]
    public void AdbePkcs7_WithoutAttribute_NoHint()
    {
        var check = Check(WithBlock(Signed(Own.Value, adbe: true), content => Block(content, Own.Value.Certificate)));
        Assert.True(SignatureTexts.IsValid(check), check.Problem);
        Assert.DoesNotContain(check.Warnings, w => w.Contains("signing-certificate"));
    }

    /// <summary>The other rules of PAdES (A-12): said as hints, the result unchanged.</summary>
    [Theory]
    [InlineData("signing-time")]
    [InlineData("content-hints")]
    public void PadesRules_ReportedAsHints(string rule)
    {
        var real = Own.Value.Certificate;
        var file = WithBlock(Signed(Own.Value), content =>
        {
            var cms = new SignedCms(new ContentInfo(content), detached: true);
            var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, real) { DigestAlgorithm = new Oid(Sha256), IncludeOption = X509IncludeOption.EndCertOnly };
            signer.SignedAttributes.Add(new AsnEncodedData(V2, Binding(SHA256.HashData(real.RawData), real.IssuerName.RawData, real.SerialNumberBytes.Span)));
            if (rule == "signing-time") signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));
            if (rule == "content-hints")
            {
                var hints = new AsnWriter(AsnEncodingRules.DER);
                using (hints.PushSequence()) hints.WriteObjectIdentifier("1.2.840.113549.1.7.1");
                signer.SignedAttributes.Add(new AsnEncodedData("1.2.840.113549.1.9.16.2.4", hints.Encode()));
            }
            cms.ComputeSignature(signer, silent: true);
            return cms.Encode();
        });
        var check = Check(file);
        Assert.True(SignatureTexts.IsValid(check), check.Problem);
        Assert.Contains(rule == "signing-time"
            ? Tr.T("Die Unterschriftszeit steht in /M und im Signaturblock; bei PAdES ist nur eine der beiden vorgesehen.")
            : Tr.T("Der Signaturblock trägt ein Attribut, das PAdES ausschließt (Gegensignatur oder Inhaltsverweis)."), check.Warnings);
    }
}
