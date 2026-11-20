using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Revocation values in the signature block itself (docs/FEHLERGEDAECHTNIS.md, No. 481): signing programs put them into the signed attribute
/// adbe-revocationInfoArchival (ISO 32000-1, 12.8.3.3.2) or into SignedData.crls instead of the security store. They count like those of the
/// store — before, such a signature was „revocation status not checked“ (yellow).
/// </summary>
public sealed class RevocationInBlockTests
{
    private static InkElement Signature() => new()
    {
        Rect = new PdfRectangle(50, 100, 250, 180), Strokes = [[0, 20, 40, 70, 90, 10]], StrokeWidth = 3,
        Digital = new DigitalSigning("Max Mustermann"),
    };

    /// <summary>A file signed by the test authority whose block is made anew: with <paramref name="attribute"/> as a signed attribute, then <paramref name="rework"/>.</summary>
    private static byte[] Signed(LongTermValidationTests.TestCa ca, AsnEncodedData? attribute, Func<byte[], byte[]>? rework = null)
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var pages = PageItem.From(doc);
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pdf-knead-tests")).FullName, $"sperrblock-{Guid.NewGuid():N}.pdf");
        SignedSaving.Save(doc, [pages[0].WithElements([Signature()])], path, new PdfSaveOptions(), false, ca.Signer, DateTimeOffset.Now);
        var signed = File.ReadAllBytes(path);
        File.Delete(path);
        long[] range;
        using (var again = PdfDocument.Open(signed)) range = DigitalSignatures.Find(again).Single().ByteRange;
        byte[] content = [.. signed.AsSpan(0, (int)range[1]), .. signed.AsSpan((int)range[2], (int)range[3])];
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, ca.Signer.Certificate)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), IncludeOption = X509IncludeOption.EndCertOnly,
        };
        signer.Certificates.Add(X509CertificateLoader.LoadCertificate(ca.Root.RawData));   // the chain in the block, as signing programs put it
        signer.SignedAttributes.Add(new AsnEncodedData("1.2.840.113549.1.9.16.2.47", CmsSignature.SigningCertificateV2Value(ca.Signer.Certificate)));
        if (attribute is not null) signer.SignedAttributes.Add(attribute);
        cms.ComputeSignature(signer, silent: true);
        var block = cms.Encode();
        if (rework is not null) block = rework(block);
        var hex = Encoding.ASCII.GetBytes(Convert.ToHexString(block));
        var gap = (int)(range[2] - range[1] - 2);
        Assert.True(hex.Length <= gap);
        Array.Fill(signed, (byte)'0', (int)range[1] + 1, gap);
        hex.CopyTo(signed, (int)range[1] + 1);
        return signed;
    }

    private static byte[] Crl(LongTermValidationTests.TestCa ca) => ca.Crl(new Uri("http://crl.muster-ca.example/ca.crl"))!;

    /// <summary>adbe-revocationInfoArchival ::= SEQUENCE { crl [0] EXPLICIT SEQUENCE OF CertificateList, … }.</summary>
    private static AsnEncodedData Archival(byte[] crl)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
        using (writer.PushSequence())
            writer.WriteEncodedValue(crl);
        return new AsnEncodedData("1.2.840.113583.1.1.8", writer.Encode());
    }

    /// <summary>The block with the CRL inserted as SignedData.crls ([1] IMPLICIT SET OF, RFC 5652, 5.1) — not signed, so the value stays.</summary>
    private static byte[] WithCrls(byte[] block, byte[] crl)
    {
        var info = new AsnReader(block, AsnEncodingRules.BER).ReadSequence();
        var oid = info.ReadObjectIdentifier();
        var data = info.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        var parts = new List<ReadOnlyMemory<byte>>();
        while (data.HasData) parts.Add(data.ReadEncodedValue());
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(oid);
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (writer.PushSequence())
            {
                for (var i = 0; i < parts.Count - 1; i++) writer.WriteEncodedValue(parts[i].Span);
                using (writer.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 1, true))) writer.WriteEncodedValue(crl);
                writer.WriteEncodedValue(parts[^1].Span);   // signerInfos
            }
        }
        return writer.Encode();
    }

    private static SignatureCheck Check(byte[] file, LongTermValidationTests.TestCa ca)
    {
        using var doc = PdfDocument.Open(file);
        return Assert.Single(SignatureVerifier.Verify(doc, null, [X509CertificateLoader.LoadCertificate(ca.Root.RawData)], CancellationToken.None));
    }

    [Fact]
    public void ArchivalAttribute_CountsAsValidationData()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var without = Check(Signed(ca, null), ca);
        Assert.Equal(SignatureOutcome.Unconfirmed, without.Outcome);   // the counter-check: nothing in the block, nothing in the file
        var with = Check(Signed(ca, Archival(Crl(ca))), ca);
        Assert.Equal(SignatureOutcome.Valid, with.Outcome);
        Assert.Equal(RevocationInfo.Coverage.Complete, with.LongTerm);
        Assert.Equal("TOTAL-PASSED", SignatureTexts.Indication(with).ToString());
    }

    [Fact]
    public void SignedDataCrls_CountAsValidationData()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var check = Check(Signed(ca, null, block => WithCrls(block, Crl(ca))), ca);
        Assert.Equal(SignatureOutcome.Valid, check.Outcome);
        Assert.Equal(RevocationInfo.Coverage.Complete, check.LongTerm);
    }

    /// <summary>A CRL in the block that lists the signer: revoked, as from the store.</summary>
    [Fact]
    public void ArchivalAttribute_Revoked_NotValid()
    {
        using var ca = new LongTermValidationTests.TestCa { CrlMode = "gesperrt" };
        var check = Check(Signed(ca, Archival(Crl(ca))), ca);
        Assert.NotEqual(SignatureOutcome.Valid, check.Outcome);
        Assert.Equal(RevocationInfo.Coverage.Revoked, check.LongTerm);
    }
}
