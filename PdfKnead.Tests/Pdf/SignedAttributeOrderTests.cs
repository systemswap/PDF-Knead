using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.ViewModels;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Signed attributes must be DER encoded (RFC 5652, 5.3), so the elements of their SET OF sorted (X.690, 11.6). A block whose signer signed
/// them in another order checks out with the bytes as they stand — the document is unchanged — but validators that re-encode the attributes
/// reject it. Design decision: valid with a hint (docs/FEHLERGEDAECHTNIS.md, No. 476). The same block with sorted attributes has no hint.
/// </summary>
public sealed class SignedAttributeOrderTests
{
    private static InkElement Signature() => new()
    {
        Rect = new PdfRectangle(50, 100, 250, 180), Strokes = [[0, 20, 40, 70, 90, 10]], StrokeWidth = 3,
        Digital = new DigitalSigning("Max Mustermann"),
    };

    /// <summary>A file signed by the test authority whose block is replaced by one built by hand: content-type and message-digest in the given order.</summary>
    /// <param name="sha1Method">The older method adbe.pkcs7.sha1 (the block carries the SHA-1 of the bytes), with a signature algorithm
    /// (sha1WithRSAEncryption) named as the digest algorithm — what some signers wrote.</param>
    private static byte[] WithBlock(LongTermValidationTests.TestCa ca, bool sorted, bool sha1Method = false, bool withDigest = true, bool withContentType = true)
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var pages = PageItem.From(doc);
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pdf-knead-tests")).FullName, $"attribute-{Guid.NewGuid():N}.pdf");
        SignedSaving.Save(doc, [pages[0].WithElements([Signature()])], path, new PdfSaveOptions(), false, ca.Signer, DateTimeOffset.Now);
        var signed = File.ReadAllBytes(path);
        File.Delete(path);
        if (sha1Method)
        {
            var name = Encoding.ASCII.GetBytes("/ETSI.CAdES.detached");
            var at = signed.AsSpan().IndexOf(name);
            Encoding.ASCII.GetBytes("/adbe.pkcs7.sha1    ").CopyTo(signed, at);
        }
        long[] range;
        using (var again = PdfDocument.Open(signed)) range = DigitalSignatures.Find(again).Single().ByteRange;
        byte[] content = [.. signed.AsSpan(0, (int)range[1]), .. signed.AsSpan((int)range[2], (int)range[3])];

        var certificate = ca.Signer.Certificate;
        byte[] Attribute(string oid, Action<AsnWriter> value)
        {
            var w = new AsnWriter(AsnEncodingRules.DER);
            using (w.PushSequence())
            {
                w.WriteObjectIdentifier(oid);
                using (w.PushSetOf()) value(w);
            }
            return w.Encode();
        }
        var contentType = Attribute("1.2.840.113549.1.9.3", w => w.WriteObjectIdentifier("1.2.840.113549.1.7.1"));
        var carried = SHA1.HashData(content);
        var digestOid = sha1Method ? "1.2.840.113549.1.1.5" : "2.16.840.1.101.3.4.2.1";
        var messageDigest = Attribute("1.2.840.113549.1.9.4", w => w.WriteOctetString(sha1Method ? SHA1.HashData(carried) : SHA256.HashData(content)));
        // DER order: content-type (shorter) before message-digest; the other order is what some signers wrote
        byte[][] attributes = sorted ? [contentType, messageDigest] : [messageDigest, contentType];
        attributes = [.. attributes.Where(a => (withDigest || a != messageDigest) && (withContentType || a != contentType))];
        var set = new List<byte> { 0x31 };
        var body = attributes.SelectMany(a => a).ToArray();
        set.AddRange(Length(body.Length));
        set.AddRange(body);
        using var key = certificate.GetRSAPrivateKey()!;
        var value = key.SignData(set.ToArray(), sha1Method ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var cms = new AsnWriter(AsnEncodingRules.BER);
        using (cms.PushSequence())
        {
            cms.WriteObjectIdentifier("1.2.840.113549.1.7.2");
            using (cms.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (cms.PushSequence())
            {
                cms.WriteInteger(1);
                using (cms.PushSetOf()) using (cms.PushSequence()) cms.WriteObjectIdentifier(digestOid);
                using (cms.PushSequence())
                {
                    cms.WriteObjectIdentifier("1.2.840.113549.1.7.1");
                    if (sha1Method) using (cms.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0))) cms.WriteOctetString(carried);
                }
                using (cms.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0))) cms.WriteEncodedValue(certificate.RawData);
                using (cms.PushSetOf())
                using (cms.PushSequence())
                {
                    cms.WriteInteger(1);
                    using (cms.PushSequence())
                    {
                        cms.WriteEncodedValue(certificate.IssuerName.RawData);
                        cms.WriteInteger(certificate.SerialNumberBytes.Span);
                    }
                    using (cms.PushSequence()) cms.WriteObjectIdentifier(digestOid);
                    // [0] IMPLICIT with the elements exactly in the chosen order (the writer would sort a SET OF itself)
                    var implicitSet = set.ToArray();
                    implicitSet[0] = 0xA0;
                    cms.WriteEncodedValue(implicitSet);
                    using (cms.PushSequence())
                    {
                        cms.WriteObjectIdentifier("1.2.840.113549.1.1.1");
                        cms.WriteNull();
                    }
                    cms.WriteOctetString(value);
                }
            }
        }
        var hex = Encoding.ASCII.GetBytes(Convert.ToHexString(cms.Encode()));
        var gap = (int)(range[2] - range[1] - 2);
        Assert.True(hex.Length <= gap);
        Array.Fill(signed, (byte)'0', (int)range[1] + 1, gap);
        hex.CopyTo(signed, (int)range[1] + 1);
        return signed;
    }

    private static IEnumerable<byte> Length(int n) => n < 0x80 ? [(byte)n] : n < 0x100 ? [0x81, (byte)n] : [0x82, (byte)(n >> 8), (byte)n];

    private static SignatureCheck Check(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        return Assert.Single(SignatureVerifier.Verify(doc, null, [], CancellationToken.None));
    }

    [Fact]
    public void UnsortedSignedAttributes_ValidWithHint_SortedWithout()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var hint = Tr.T("Die signierten Attribute stehen nicht in der vorgeschriebenen Reihenfolge (DER, RFC 5652); strenge Prüfprogramme lehnen diese Signatur deshalb ab.");
        var sorted = Check(WithBlock(ca, sorted: true));
        Assert.True(SignatureTexts.IsValid(sorted), sorted.Problem);
        Assert.DoesNotContain(hint, sorted.Warnings);
        var unsorted = Check(WithBlock(ca, sorted: false));
        Assert.True(SignatureTexts.IsValid(unsorted), unsorted.Problem);
        Assert.Contains(hint, unsorted.Warnings);
    }

    /// <summary>
    /// The older method adbe.pkcs7.sha1 with a signature algorithm where the digest algorithm belongs: the same rule as for the detached
    /// methods (RFC 5652, 5.3; docs/FEHLERGEDAECHTNIS.md, No. 476) — before, only those were checked, this one counted as valid.
    /// </summary>
    [Fact]
    public void Sha1Method_SignatureAlgorithmAsDigest_Invalid()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var check = Check(WithBlock(ca, sorted: true, sha1Method: true));
        Assert.Equal("adbe.pkcs7.sha1", check.Method);
        Assert.Equal(SignatureOutcome.Invalid, check.Outcome);
        Assert.Equal(Tr.T("Der Signaturblock nennt ein Signaturverfahren, wo ein Prüfsummenverfahren stehen muss (RFC 5652) — so ist er nicht normgerecht."), check.Problem);
    }

    /// <summary>
    /// Signed attributes without a message digest: a form error of the block (FORMAT_FAILURE), not a change of the document; without
    /// content-type: valid, said (RFC 5652, 5.3; docs/FEHLERGEDAECHTNIS.md, No. 483).
    /// </summary>
    [Fact]
    public void MissingDigest_FormError_MissingContentType_Hint()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var noDigest = Check(WithBlock(ca, sorted: true, withDigest: false));
        Assert.Equal(SignatureOutcome.Invalid, noDigest.Outcome);
        Assert.Equal(Tr.T("Der Signaturblock ist nicht normgerecht (die Prüfsumme der unterschriebenen Daten fehlt)."), noDigest.Problem);
        Assert.Equal("TOTAL-FAILED/FORMAT_FAILURE", SignatureTexts.Indication(noDigest).ToString());
        var noType = Check(WithBlock(ca, sorted: true, withContentType: false));
        Assert.True(SignatureTexts.IsValid(noType), noType.Problem);
        Assert.Contains(Tr.T("Den signierten Attributen fehlt die Art des Inhalts (content-type); RFC 5652 verlangt sie."), noType.Warnings);
    }
}
