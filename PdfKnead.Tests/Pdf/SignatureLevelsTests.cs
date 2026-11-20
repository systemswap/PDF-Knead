using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Pdf.Signatures;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The level a certificate gives a signature (eIDAS; KONZEPT.md, section 5.17): self-issued simple, from an authority advanced, qualified only
/// with both statements QcCompliance and QcSSCD (EN 319 412-5) — read offline from the certificate.
/// </summary>
public sealed class SignatureLevelsTests
{
    private static byte[] QcStatements(params string[] ids)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
            foreach (var id in ids)
                using (writer.PushSequence())
                    writer.WriteObjectIdentifier(id);
        return writer.Encode();
    }

    private static X509Certificate2 Issued(byte[]? qcStatements)
    {
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=Test CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Max Mustermann", key, HashAlgorithmName.SHA256);
        if (qcStatements is not null) request.CertificateExtensions.Add(new X509Extension("1.3.6.1.5.5.7.1.3", qcStatements, false));
        return request.Create(ca, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddMonths(6), [1, 2, 3, 4]);
    }

    [Fact]
    public void SelfIssued_Simple()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var own = new CertificateRequest("CN=Max Mustermann", key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(1));
        Assert.Equal(SignatureLevel.Simple, SignatureLevels.Of(own));
    }

    [Fact]
    public void FromAnAuthority_Advanced_QualifiedOnlyWithBothStatements()
    {
        using (var plain = Issued(null)) Assert.Equal(SignatureLevel.Advanced, SignatureLevels.Of(plain));
        using (var qualified = Issued(QcStatements("0.4.0.1862.1.1", "0.4.0.1862.1.4"))) Assert.Equal(SignatureLevel.Qualified, SignatureLevels.Of(qualified));
        // a qualified certificate whose key is not on a qualified device (a software certificate): advanced
        using (var soft = Issued(QcStatements("0.4.0.1862.1.1", "0.4.0.1862.1.6"))) Assert.Equal(SignatureLevel.Advanced, SignatureLevels.Of(soft));
        using (var broken = Issued([0x30, 0x05, 0x30, 0x03, 0x06])) Assert.Equal(SignatureLevel.Advanced, SignatureLevels.Of(broken));
    }

    [Fact]
    public void Texts_ForEachLevel_NoLegalAdviceButTheLimit()
    {
        Assert.Contains("Schriftform", SignatureTexts.LevelMeaning(SignatureLevel.Simple));
        Assert.Contains("Vertrauensliste", SignatureTexts.LevelMeaning(SignatureLevel.Qualified));
        Assert.Contains("qualifiziert", SignatureTexts.Level(SignatureLevel.Qualified));
    }
}
