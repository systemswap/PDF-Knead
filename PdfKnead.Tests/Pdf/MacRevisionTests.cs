using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// MACs in later revisions (ISO/TS 32004; design decision, 24.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 1116): a signed revision carries its
/// token as the unsigned attribute id-attr-pdfMacData of the signature or document time stamp, /AuthCode says /AttachedToSig and names the
/// signature (6.5.2); an unsigned update gets a standalone token over the whole file (6.5.1). The digests are recomputed here independently:
/// dataDigest over the signature's byte range, signatureDigest over the signature value (6.6.3).
/// </summary>
public sealed class MacRevisionTests
{
    private static readonly Lazy<SigningCertificate> Certificate = new(() =>
        SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"mac-{Guid.NewGuid():N}.p12")));

    private static readonly DateTimeOffset When = new(2026, 11, 24, 10, 0, 0, TimeSpan.FromHours(1));

    private static InkElement Signature() => new()
    {
        Rect = new PdfRectangle(100, 100, 300, 180),
        Strokes = [[0, 20, 40, 70, 90, 10, 140, 60, 190, 30]],
        StrokeWidth = 3,
        Color = 0x1B3A8C,
        Digital = new DigitalSigning("Max Mustermann", null),
    };

    /// <summary>A file of ISO/TS 32004: AES-256, PDF 2.0, MAC required, with a real standalone MAC in its first revision.</summary>
    private static byte[] MacFile() =>
        TestPdf.Simple(1).Build(cipher: PdfCipher.Aes256, user: "", permissions: PdfPermissions.All, version: "2.0", macRequired: true, mac: TestMac.Real);

    private static byte[] SignedWithMac(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        Assert.Equal(MacState.Valid, doc.Integrity.State);
        return PdfSigner.SignUnchecked(doc, 0, Signature(), Certificate.Value, When, writeMac: true);
    }

    /// <summary>The token attached to the one signer, read here without PDF-Knead's reader: the attribute's single value.</summary>
    private static (byte[] Token, SignerInfo Signer) AttachedToken(byte[] contents)
    {
        var length = AsnDecoder.TryReadEncodedValue(contents, AsnEncodingRules.BER, out _, out _, out _, out var consumed) ? consumed : contents.Length;
        var cms = new SignedCms();
        cms.Decode(contents.AsSpan(0, length));
        var signer = Assert.Single(cms.SignerInfos.Cast<SignerInfo>());
        var attribute = Assert.Single(signer.UnsignedAttributes.Cast<CryptographicAttributeObject>(), a => a.Oid.Value == "1.0.32004.1.2");
        return (Assert.Single(attribute.Values.Cast<AsnEncodedData>()).RawData, signer);
    }

    /// <summary>dataDigest and signatureDigest of a token (PdfMacIntegrityInfo, 6.2), read from its encapsulated content.</summary>
    private static (byte[] Data, byte[]? Signature) Digests(byte[] token)
    {
        var outer = new AsnReader(token, AsnEncodingRules.DER).ReadSequence();
        Assert.Equal("1.2.840.113549.1.9.16.1.2", outer.ReadObjectIdentifier());
        var data = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        data.ReadInteger();
        data.ReadSetOf();
        data.ReadSequence();
        data.ReadEncodedValue();
        var encap = data.ReadSequence();
        Assert.Equal("1.0.32004.1.0", encap.ReadObjectIdentifier());
        var info = new AsnReader(encap.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadOctetString(), AsnEncodingRules.DER).ReadSequence();
        Assert.Equal(0, (int)info.ReadInteger());
        var dataDigest = info.ReadOctetString();
        var signatureDigest = info.HasData ? info.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 0)) : null;
        Assert.False(info.HasData);
        return (dataDigest, signatureDigest);
    }

    private static byte[] RangeDigest(byte[] file, PdfArray range)
    {
        long l1 = (long)range.GetNumber(1)!.Value, s = (long)range.GetNumber(2)!.Value, l2 = (long)range.GetNumber(3)!.Value;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(file, 0, (int)l1);
        hash.AppendData(file, (int)s, (int)l2);
        return hash.GetHashAndReset();
    }

    [Fact]
    public void Signed_MacAttachedToTheSignature_ValidAndDigestsRecomputed()
    {
        var file = MacFile();
        var signed = SignedWithMac(file);
        Assert.Equal(file, signed.AsSpan(0, file.Length).ToArray());
        using var doc = PdfDocument.Open(signed);
        Assert.Equal(MacState.Valid, doc.Integrity.State);
        var authCode = doc.NewestTrailer!.GetDictionary(PdfName.Get("AuthCode"))!;
        Assert.Equal("AttachedToSig", authCode.GetName(PdfName.Get("MACLocation"))!.Value);
        Assert.False(authCode.ContainsKey(PdfName.Get("ByteRange")));
        Assert.False(authCode.ContainsKey(PdfName.Get("MAC")));
        var reference = Assert.IsType<PdfReference>(authCode.GetRaw(PdfName.Get("SigObjRef")));
        var entry = Assert.Single(DigitalSignatures.Find(doc));
        Assert.Same(entry.Signature, doc.Resolve(reference.Id));
        var range = entry.Signature!.GetArray(PdfName.Get("ByteRange"))!;
        Assert.Equal(signed.Length, (long)range.GetNumber(2)!.Value + (long)range.GetNumber(3)!.Value);   // the whole file (6.5.2)
        var (token, signer) = AttachedToken(entry.Signature.GetString(PdfName.Contents)!.Bytes);
        var (data, signature) = Digests(token);
        Assert.Equal(RangeDigest(signed, range), data);
        Assert.Equal(SHA256.HashData(signer.GetSignature()), signature);
        // NOTE 2 of 6.6.3: with the same digest the dataDigest is the signature's messageDigest
        var messageDigest = signer.SignedAttributes.Cast<CryptographicAttributeObject>().Single(a => a.Oid.Value == "1.2.840.113549.1.9.4");
        Assert.Equal(data, new AsnReader(messageDigest.Values[0].RawData, AsnEncodingRules.DER).ReadOctetString());
        // the signature itself is untouched by the unsigned attribute
        var check = Assert.Single(SignatureVerifier.Verify(doc, null, [], CancellationToken.None));
        Assert.True(SignatureTexts.IsValid(check) || check.Outcome == SignatureOutcome.ValidIdentityUnknown, check.Problem);
    }

    [Fact]
    public void Signed_TwiceInARow_NewestMacValid()
    {
        var once = SignedWithMac(MacFile());
        var twice = SignedWithMac(once);
        using var doc = PdfDocument.Open(twice);
        Assert.Equal(MacState.Valid, doc.Integrity.State);
        Assert.Equal(2, DigitalSignatures.Find(doc).Count);
    }

    [Fact]
    public void Signed_SwitchedOff_RefusedAsBefore()
    {
        using var doc = PdfDocument.Open(MacFile());
        Assert.Equal(Tr.T("Diese Datei ist zusätzlich gegen Veränderung geschützt (ISO/TS 32004); PDF-Knead kann sie noch nicht signieren."),
            Assert.Throws<NotAppendableException>(() => PdfSigner.SignUnchecked(doc, 0, Signature(), Certificate.Value, When, writeMac: false)).Message);
    }

    [Fact]
    public void Signed_WithoutKdfSalt_Refused()
    {
        var file = TestPdf.Simple(1).Build(cipher: PdfCipher.Aes256, user: "", permissions: PdfPermissions.All, version: "2.0", macRequired: true,
            mac: TestMac.None, patchEncrypt: e => e.Remove(PdfName.Get("KDFSalt")));
        using var doc = PdfDocument.Open(file);
        Assert.Contains("/KDFSalt", Assert.Throws<NotAppendableException>(() => PdfSigner.SignUnchecked(doc, 0, Signature(), Certificate.Value, When, writeMac: true)).Message);
    }

    /// <summary>Changes after signing, each judged „violated“ with its reason — never valid.</summary>
    [Theory]
    [InlineData("angehängt")]
    [InlineData("eigenständig mit SigObjRef")]
    [InlineData("andere Signatur")]
    [InlineData("Attribut fehlt")]
    [InlineData("Signaturwert")]
    public void Signed_Changed_MacViolated(string change)
    {
        var signed = SignedWithMac(MacFile());
        var text = Encoding.Latin1.GetString(signed);
        byte[] changed;
        switch (change)
        {
            case "angehängt":
                changed = [.. signed, .. "% später\n"u8.ToArray()];
                break;
            case "eigenständig mit SigObjRef":
                changed = Replace(signed, text, "/AttachedToSig", "/Standalone   ");
                break;
            case "andere Signatur":
            {
                var at = text.LastIndexOf("/SigObjRef", StringComparison.Ordinal);
                var number = text[(at + 10)..].TrimStart().Split(' ')[0];
                using var doc = PdfDocument.Open(signed);
                var root = ((PdfReference)doc.Trailer.GetRaw(PdfName.Root)!).Id.Number.ToString().PadLeft(number.Length);
                changed = (byte[])signed.Clone();
                var start = text.IndexOf(number, at, StringComparison.Ordinal);
                Encoding.ASCII.GetBytes(root).CopyTo(changed, start);
                break;
            }
            case "Attribut fehlt":
                // the OID 1.0.32004.1.2 in the hexadecimal /Contents turned into 1.0.32004.1.3: the attribute is not the MAC any more
                changed = Replace(signed, text, "06062881FA040102", "06062881FA040103", ignoreCase: true);
                break;
            default:
            {
                using var doc = PdfDocument.Open(signed);
                var (_, signer) = AttachedToken(Assert.Single(DigitalSignatures.Find(doc)).Signature!.GetString(PdfName.Contents)!.Bytes);
                var value = Convert.ToHexString(signer.GetSignature());
                var flipped = value[..^1] + (value[^1] == '0' ? '1' : '0');
                changed = Replace(signed, text, value, flipped, ignoreCase: true);
                break;
            }
        }
        using var opened = PdfDocument.Open(changed);
        Assert.Equal(MacState.Invalid, opened.Integrity.State);
        Assert.False(string.IsNullOrEmpty(opened.Integrity.Detail));
    }

    private static byte[] Replace(byte[] bytes, string text, string from, string to, bool ignoreCase = false)
    {
        Assert.Equal(from.Length, to.Length);
        var at = text.IndexOf(from, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        Assert.True(at >= 0, from);
        var copy = (byte[])bytes.Clone();
        Encoding.ASCII.GetBytes(to).CopyTo(copy, at);
        return copy;
    }

    [Fact]
    public void DocumentTimestamp_MacOnTheTokensSigner_Valid()
    {
        using var authority = new SignatureTimestampTests.TestAuthority();
        var file = MacFile();
        byte[] stamped;
        using (var doc = PdfDocument.Open(file))
            stamped = [.. file, .. PdfSigner.DocumentTimestampUpdate(doc, authority, writeMac: true)];
        using var opened = PdfDocument.Open(stamped);
        Assert.Equal(MacState.Valid, opened.Integrity.State);
        var entry = Assert.Single(DigitalSignatures.Find(opened));
        var (token, signer) = AttachedToken(entry.Signature!.GetString(PdfName.Contents)!.Bytes);
        var (data, signature) = Digests(token);
        Assert.Equal(RangeDigest(stamped, entry.Signature.GetArray(PdfName.Get("ByteRange"))!), data);
        Assert.Equal(SHA256.HashData(signer.GetSignature()), signature);
    }

    /// <summary>An unsigned update of a file that asks for a MAC: a standalone token over the whole file in its trailer (6.5.1), with both forms of
    /// cross-references; its digest recomputed here.</summary>
    [Theory]
    [InlineData(XRefStyle.Table)]
    [InlineData(XRefStyle.Stream)]
    public void UnsignedUpdate_StandaloneMacOverTheWholeFile(XRefStyle style)
    {
        var file = TestPdf.Simple(1).Build(style: style, cipher: PdfCipher.Aes256, user: "", permissions: PdfPermissions.All, version: "2.0",
            macRequired: true, mac: TestMac.None);
        byte[] updated;
        using (var doc = PdfDocument.Open(file))
        {
            var update = new IncrementalUpdate(doc, writeMac: true);
            Assert.True(update.WritesMac);
            update.Add(TestPdf.Dict(("Hinweis", new PdfString("Nachtrag von Erika Musterfrau"u8.ToArray()))));
            using var output = new MemoryStream();
            update.WriteTo(output);
            updated = output.ToArray();
        }
        using var opened = PdfDocument.Open(updated);
        Assert.Equal(MacState.Valid, opened.Integrity.State);
        var authCode = opened.NewestTrailer!.GetDictionary(PdfName.Get("AuthCode"))!;
        Assert.Equal("Standalone", authCode.GetName(PdfName.Get("MACLocation"))!.Value);
        var range = authCode.GetArray(PdfName.Get("ByteRange"))!;
        Assert.Equal(updated.Length, (long)range.GetNumber(2)!.Value + (long)range.GetNumber(3)!.Value);
        var (data, signature) = Digests(authCode.GetString(PdfName.Get("MAC"))!.Bytes);
        Assert.Null(signature);
        Assert.Equal(RangeDigest(updated, range), data);
    }
}
