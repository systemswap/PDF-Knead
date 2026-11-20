using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// „Not listed“ means „good“ only on a list that covers the certificate (RFC 5280, 5.2 and 6.3.3; docs/FEHLERGEDAECHTNIS.md, No. 482): no
/// delta CRL alone, no unknown critical extension, an issuing distribution point that covers it, and after the certificate expired only with
/// ExpiredCertsOnCRL — lists drop expired certificates; the same for an OCSP „good“ given after expiry without archiveCutoff. Before, each
/// of these counted as „good“.
/// </summary>
public sealed class RevocationScopeTests
{
    private static void Extension(AsnWriter w, string oid, bool critical, byte[] value)
    {
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier(oid);
            if (critical) w.WriteBoolean(true);
            w.WriteOctetString(value);
        }
    }

    /// <summary>A CRL of the test authority without entries, issued at <paramref name="thisUpdate"/>, with the given extensions.</summary>
    private static byte[] Crl(LongTermValidationTests.TestCa ca, DateTimeOffset thisUpdate, Action<AsnWriter>? extensions = null)
    {
        var tbs = new AsnWriter(AsnEncodingRules.DER);
        using (tbs.PushSequence())
        {
            tbs.WriteInteger(1);
            using (tbs.PushSequence())
            {
                tbs.WriteObjectIdentifier("1.2.840.113549.1.1.11");
                tbs.WriteNull();
            }
            tbs.WriteEncodedValue(ca.Root.SubjectName.RawData);
            tbs.WriteGeneralizedTime(thisUpdate, omitFractionalSeconds: true);
            tbs.WriteGeneralizedTime(thisUpdate.AddDays(7), omitFractionalSeconds: true);
            if (extensions is not null)
                using (tbs.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                using (tbs.PushSequence())
                    extensions(tbs);
        }
        var encoded = tbs.Encode();
        using var key = ca.Root.GetRSAPrivateKey()!;
        var crl = new AsnWriter(AsnEncodingRules.DER);
        using (crl.PushSequence())
        {
            crl.WriteEncodedValue(encoded);
            using (crl.PushSequence())
            {
                crl.WriteObjectIdentifier("1.2.840.113549.1.1.11");
                crl.WriteNull();
            }
            crl.WriteBitString(key.SignData(encoded, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        return crl.Encode();
    }

    private static RevocationInfo.Coverage Coverage(LongTermValidationTests.TestCa ca, byte[] crl) =>
        RevocationInfo.Embedded(ca.Signer.Certificate, [X509CertificateLoader.LoadCertificate(ca.Root.RawData)], [], [crl], DateTimeOffset.Now).Coverage;

    /// <summary>An issuing distribution point with the given content.</summary>
    private static byte[] Idp(Action<AsnWriter> content)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence()) content(w);
        return w.Encode();
    }

    /// <summary>distributionPoint [0] { fullName [0] { uniformResourceIdentifier [6] } }.</summary>
    private static void Point(AsnWriter w, string uri)
    {
        using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
        using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            w.WriteCharacterString(UniversalTagNumber.IA5String, uri, new Asn1Tag(TagClass.ContextSpecific, 6));
    }

    [Fact]
    public void CompleteList_Good_PartsAndDeltasNot()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var now = DateTimeOffset.UtcNow.AddHours(-1);
        Assert.Equal(RevocationInfo.Coverage.Complete, Coverage(ca, Crl(ca, now)));
        Assert.Equal(RevocationInfo.Coverage.Complete, Coverage(ca, Crl(ca, now, w => Extension(w, "2.5.29.20", false, [0x02, 0x01, 0x05]))));
        // the distribution point the certificate names: covered; another one: a part of the list for other certificates
        Assert.Equal(RevocationInfo.Coverage.Complete,
            Coverage(ca, Crl(ca, now, w => Extension(w, "2.5.29.28", true, Idp(p => Point(p, "http://crl.muster-ca.example/ca.crl"))))));
        Assert.Equal(RevocationInfo.Coverage.Incomplete,
            Coverage(ca, Crl(ca, now, w => Extension(w, "2.5.29.28", true, Idp(p => Point(p, "http://crl.muster-ca.example/teil2.crl"))))));
        // a delta CRL alone
        Assert.Equal(RevocationInfo.Coverage.Incomplete, Coverage(ca, Crl(ca, now, w => Extension(w, "2.5.29.27", true, [0x02, 0x01, 0x04]))));
        // only some reasons, only authority certificates
        Assert.Equal(RevocationInfo.Coverage.Incomplete, Coverage(ca, Crl(ca, now, w => Extension(w, "2.5.29.28", true,
            Idp(p => p.WriteBitString([0x60], 0, new Asn1Tag(TagClass.ContextSpecific, 3)))))));
        Assert.Equal(RevocationInfo.Coverage.Incomplete, Coverage(ca, Crl(ca, now, w => Extension(w, "2.5.29.28", true,
            Idp(p => p.WriteBoolean(true, new Asn1Tag(TagClass.ContextSpecific, 2)))))));
        // an unknown critical extension
        Assert.Equal(RevocationInfo.Coverage.Incomplete, Coverage(ca, Crl(ca, now, w => Extension(w, "1.2.3.4.5", true, [0x05, 0x00]))));
    }

    [Fact]
    public void ListAfterExpiry_OnlyWithExpiredCertsOnCrl()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var after = new DateTimeOffset(ca.Signer.Certificate.NotAfter).AddDays(1);
        Assert.Equal(RevocationInfo.Coverage.Incomplete, Coverage(ca, Crl(ca, after)));
        var since = new AsnWriter(AsnEncodingRules.DER);
        since.WriteGeneralizedTime(new DateTimeOffset(ca.Signer.Certificate.NotBefore), omitFractionalSeconds: true);
        Assert.Equal(RevocationInfo.Coverage.Complete, Coverage(ca, Crl(ca, after, w => Extension(w, "2.5.29.60", false, since.Encode()))));
    }

    /// <summary>An OCSP „good“ given after the certificate expired: only with archiveCutoff — not embedded when signing either.</summary>
    [Fact]
    public void OcspAfterExpiry_OnlyWithArchiveCutoff()
    {
        static RevocationInfo.Coverage? Signed(string mode)
        {
            using var ca = new LongTermValidationTests.TestCa { OcspMode = mode, CrlMode = "keine" };
            using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
            var pages = PageItem.From(doc);
            var path = Path.Combine(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pdf-knead-tests")).FullName, $"umfang-{Guid.NewGuid():N}.pdf");
            SignedSaving.Save(doc, [pages[0].WithElements([new InkElement
            {
                Rect = new PdfRectangle(50, 100, 250, 180), Strokes = [[0, 20, 40, 70, 90, 10]], StrokeWidth = 3, Digital = new DigitalSigning("Max Mustermann"),
            }])], path, new PdfSaveOptions(), false, ca.Signer, DateTimeOffset.Now, revocation: ca);
            using var signed = PdfDocument.Open(File.ReadAllBytes(path));
            File.Delete(path);
            return Assert.Single(SignatureVerifier.Verify(signed, null, [X509CertificateLoader.LoadCertificate(ca.Root.RawData)], CancellationToken.None)).LongTerm;
        }
        Assert.Equal(RevocationInfo.Coverage.Incomplete, Signed("nach-ablauf"));
        Assert.Equal(RevocationInfo.Coverage.Complete, Signed("nach-ablauf-archiv"));
    }
}
