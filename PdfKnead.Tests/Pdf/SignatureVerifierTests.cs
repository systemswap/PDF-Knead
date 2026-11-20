using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Core.Platform;
using PdfKnead.ViewModels;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Checking digital signatures (KONZEPT.md, section 5.17, step S4): valid, identity confirmed only through a trusted root, a later
/// signature or comment allowed, changed page content, a changed byte and a broken block detected. The trusted roots are passed in,
/// so the tests never depend on or change the system's certificate store.
/// </summary>
public sealed class SignatureVerifierTests
{
    private static readonly Lazy<SigningCertificate> Own = new(() =>
        SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"pruefen-{Guid.NewGuid():N}.p12")));

    private static readonly DateTimeOffset When = new(2026, 10, 25, 14, 30, 0, TimeSpan.FromHours(2));

    private static InkElement Signature(double left) => new()
    {
        Rect = new PdfRectangle(left, 100, left + 200, 180),
        Strokes = [[0, 20, 40, 70, 90, 10, 140, 60, 190, 30]],
        StrokeWidth = 3,
        Color = 0x1B3A8C,
        Digital = new DigitalSigning("Max Mustermann · 25.10.2026 14:30"),
    };

    private static byte[] Sign(byte[] file, double left, SigningCertificate? certificate = null)
    {
        using var doc = PdfDocument.Open(file);
        return PdfSigner.Sign(doc, 0, Signature(left), certificate ?? Own.Value, When);
    }

    private static IReadOnlyList<SignatureCheck> Verify(byte[] file, X509Certificate2Collection? roots = null)
    {
        using var doc = PdfDocument.Open(file);
        return SignatureVerifier.Verify(doc, null, roots ?? [], CancellationToken.None);
    }

    private static byte[] Append(byte[] file, Action<PdfDocument, IncrementalUpdate> change)
    {
        using var doc = PdfDocument.Open(file);
        var update = new IncrementalUpdate(doc);
        change(doc, update);
        using var output = new MemoryStream();
        update.WriteTo(output);
        return output.ToArray();
    }

    private static PdfDictionary CopyOf(PdfDictionary dict)
    {
        var copy = new PdfDictionary();
        foreach (var (key, value) in dict.Entries) copy.Set(key, value);
        return copy;
    }

    [Fact]
    public void OwnCertificate_ValidIdentityUnknown_WithDetails()
    {
        var check = Assert.Single(Verify(Sign(TestPdf.Simple(pages: 2).Build(), 100)));
        Assert.Equal(SignatureOutcome.ValidIdentityUnknown, check.Outcome);
        Assert.Null(check.Problem);
        Assert.True(check.CoversWholeFile);
        Assert.Equal(LaterChanges.None, check.Later);
        Assert.Equal("Max Mustermann", check.SignerName);
        Assert.Equal(When, check.SigningTime);
        Assert.Equal("ETSI.CAdES.detached", check.Method);
        Assert.Equal(0, check.Page);
        Assert.True(check.Certificate!.SelfSigned);
        Assert.Equal(16, check.Certificate.Fingerprint.Split(' ').Length);
    }

    [Fact]
    public void SecondSignature_FirstStaysValid_LaterOnlySigning()
    {
        var checks = Verify(Sign(Sign(TestPdf.Simple(pages: 1).Build(), 50), 300));
        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.Equal(SignatureOutcome.ValidIdentityUnknown, c.Outcome));
        var first = checks.Single(c => !c.CoversWholeFile);
        Assert.Equal(LaterChanges.FormsAndSignatures, first.Later);
        Assert.Empty(first.LaterDetails);
    }

    [Fact]
    public void LaterComment_Allowed_LaterInfo_Allowed()
    {
        var signed = Sign(TestPdf.Simple(pages: 1).Build(), 50);
        var commented = Append(signed, (doc, update) =>
        {
            var page = CopyOf(doc.Pages[0].Dictionary);
            var note = update.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Text")), ("Rect", TestPdf.Box(300, 700, 320, 720)),
                ("Contents", new PdfString("Bitte prüfen"u8.ToArray()))));
            var annots = new PdfArray(doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))?.ToArray() ?? []) { note };
            page.Set(PdfName.Get("Annots"), annots);
            update.Replace(doc.Pages[0].Id!.Value, page);
        });
        var check = Assert.Single(Verify(commented));
        Assert.Equal(SignatureOutcome.ValidIdentityUnknown, check.Outcome);
        Assert.Equal(LaterChanges.Comments, check.Later);

        var retitled = Append(signed, (doc, update) =>
        {
            var infoRef = (PdfReference)doc.Trailer.GetRaw(PdfName.Info)!;
            var info = CopyOf((PdfDictionary)doc.Resolve(infoRef.Id));
            info.Set(PdfName.Get("Title"), new PdfString("Neuer Titel"u8.ToArray()));
            update.Replace(infoRef.Id, info);
        });
        Assert.Equal(LaterChanges.FormsAndSignatures, Assert.Single(Verify(retitled)).Later);
    }

    [Fact]
    public void LaterPageContent_ChangedAfterSigning()
    {
        var signed = Sign(TestPdf.Simple(pages: 1).Build(), 50);
        var changed = Append(signed, (doc, update) =>
        {
            var page = CopyOf(doc.Pages[0].Dictionary);
            page.Set(PdfName.Contents, update.Add(TestPdf.FlateStream(Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (Neuer Betrag) Tj ET"))));
            update.Replace(doc.Pages[0].Id!.Value, page);
        });
        var check = Assert.Single(Verify(changed));
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, check.Outcome);
        Assert.Equal(LaterChanges.Other, check.Later);
        Assert.Contains(Tr.T("Der Inhalt von Seite {0} wurde geändert.", 1), check.LaterDetails);
    }

    [Fact]
    public void LaterHiddenSignatureField_ChangedAfterSigning()
    {
        var signed = Sign(TestPdf.Simple(pages: 1).Build(), 50);
        var changed = Append(signed, (doc, update) =>
        {
            // the visible signature hidden afterwards: the signed look is no longer what the reader shows
            var raw = (PdfReference)doc.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))![0];
            var field = CopyOf((PdfDictionary)doc.Resolve(raw.Id));
            field.Set(PdfName.F, PdfInteger.Of(2));
            update.Replace(raw.Id, field);
        });
        var check = Assert.Single(Verify(changed));
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, check.Outcome);
        Assert.Contains(Tr.T("Das Feld „{0}“ wurde geändert („{1}“).", check.FieldName, "F"), check.LaterDetails);
    }

    [Fact]
    public void ChangedByte_Invalid()
    {
        var signed = Sign(TestPdf.Simple(pages: 1).Build(), 50);
        var at = signed.AsSpan().IndexOf("Testdokument"u8);
        Assert.True(at > 0);
        signed[at + 4] = (byte)'X';
        var check = Assert.Single(Verify(signed));
        Assert.Equal(SignatureOutcome.Invalid, check.Outcome);
        Assert.Equal(Tr.T("Die Signatur passt nicht zum Inhalt: das Dokument wurde verändert."), check.Problem);
    }

    [Fact]
    public void BrokenBlock_Invalid()
    {
        var check = Assert.Single(Verify(SignedTestPdf.Build()));
        Assert.Equal(SignatureOutcome.Invalid, check.Outcome);
        Assert.NotNull(check.Problem);
    }

    [Fact]
    public void RewrittenFile_Invalid()
    {
        using var signed = PdfDocument.Open(Sign(TestPdf.Simple(pages: 1).Build(), 50));
        using var rewritten = new MemoryStream();
        PdfWriter.Write(signed, rewritten);
        var check = Assert.Single(Verify(rewritten.ToArray()));
        Assert.Equal(SignatureOutcome.Invalid, check.Outcome);
    }

    [Fact]
    public void IssuedByTrustedRoot_Valid_OnlyIfRootTrusted()
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Muster-Zertifizierungsstelle", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        using var ca = caRequest.CreateSelfSigned(from, from.AddYears(5));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Erika Mustermann", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        using var issued = request.Create(ca, from, from.AddYears(2), [1, 2, 3, 4]);
        using var signer = new SigningCertificate(issued.CopyWithPrivateKey(key), SigningCertificateSource.File);
        var signed = Sign(TestPdf.Simple(pages: 1).Build(), 50, signer);

        var trusted = Assert.Single(Verify(signed, [X509CertificateLoader.LoadCertificate(ca.RawData)]));
        // the identity confirmed, but no validation data: not confirmed (yellow) until the revocation status is known (docs/ABLAUFPLAN.md, 2.5)
        Assert.Equal(SignatureOutcome.Unconfirmed, trusted.Outcome);
        Assert.Equal(Tr.T("Sperrstatus nicht geprüft (keine Prüfdaten in der Datei)."), trusted.UnconfirmedReason);
        Assert.True(SignatureTexts.IsValid(trusted));
        Assert.True(trusted.IdentityConfirmed);
        Assert.False(trusted.Certificate!.SelfSigned);
        Assert.Equal("Muster-Zertifizierungsstelle", trusted.Certificate.Issuer);
        Assert.Equal(SignatureOutcome.ValidIdentityUnknown, Assert.Single(Verify(signed)).Outcome);
    }

    // ------------------------------------------------------------ Later revisions that must count as changed (round S7)

    /// <summary>A signed form; the later revision is built by <paramref name="change"/> on the signed file.</summary>
    private static byte[] SignedFormChanged(Action<PdfDocument, IncrementalUpdate> change)
    {
        byte[] signed;
        using (var form = PdfDocument.Open(FormFillTests.AllKinds()))
            signed = PdfSigner.Sign(form, 0, Signature(350), Own.Value, When);
        return Append(signed, change);
    }

    private static (PdfObjectId Id, PdfDictionary Widget) Widget(PdfDocument doc, string name)
    {
        var annots = doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
        for (var i = 0; i < annots.Count; i++)
            if (annots[i] is PdfReference r && annots.Get(i) is PdfDictionary w && w.GetString(PdfName.Get("T"))?.ToText() == name) return (r.Id, w);
        throw new InvalidOperationException(name);
    }

    /// <summary>
    /// A field made to show an object that was already in the signed revision (prepared there, unused or used elsewhere): not filling in,
    /// but switching visible content — counts as changed after signing.
    /// </summary>
    [Fact]
    public void LaterFieldLook_PointsToExistingObject_ChangedAfterSigning()
    {
        var changed = SignedFormChanged((doc, update) =>
        {
            var (id, widget) = Widget(doc, "Name");
            var agree = Widget(doc, "Zustimmung").Widget;
            var prepared = (PdfReference)agree.GetDictionary(PdfName.Get("AP"))!.GetDictionary(PdfName.N)!.GetRaw(PdfName.Get("Ja"))!;
            var copy = CopyOf(widget);
            copy.Set(PdfName.Get("AP"), TestPdf.Dict(("N", prepared)));
            update.Replace(id, copy);
        });
        var check = Verify(changed).Single(c => !c.CoversWholeFile);
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, check.Outcome);
    }

    /// <summary>Layers switched on or off afterwards (the catalog's optional content) change what is shown.</summary>
    [Fact]
    public void LaterLayerState_ChangedAfterSigning()
    {
        var changed = SignedFormChanged((doc, update) =>
        {
            var catalog = CopyOf(doc.Catalog!);
            catalog.Set(PdfName.Get("OCProperties"), TestPdf.Dict(("OCGs", new PdfArray()), ("D", TestPdf.Dict(("OFF", new PdfArray())))));
            update.Replace(((PdfReference)doc.Trailer.GetRaw(PdfName.Root)!).Id, catalog);
        });
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, Verify(changed).Single(c => !c.CoversWholeFile).Outcome);
    }

    /// <summary>A font of the page replaced under its number: the same text would show other glyphs.</summary>
    [Fact]
    public void LaterFontReplaced_ChangedAfterSigning()
    {
        var changed = SignedFormChanged((doc, update) =>
        {
            var font = (PdfReference)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetRaw(PdfName.Get("F1"))!;
            update.Replace(font.Id, TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Courier"))));
        });
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, Verify(changed).Single(c => !c.CoversWholeFile).Outcome);
    }

    /// <summary>A signature whose byte ranges leave out another signature's block: the block does not belong to it.</summary>
    [Fact]
    public void BorrowedBlock_Invalid()
    {
        var twice = Sign(Sign(TestPdf.Simple(pages: 1).Build(), 50), 300);
        using var doc = PdfDocument.Open(twice);
        var signatures = DigitalSignatures.Find(doc);
        var (first, second) = (signatures.Single(s => s.ByteRange[2] + s.ByteRange[3] < twice.Length), signatures.Single(s => s.ByteRange[2] + s.ByteRange[3] == twice.Length));
        // the first signature's /ByteRange rewritten in place to the second's gap (same length of digits padded)
        var text = System.Text.Encoding.Latin1.GetString(twice);
        var at = text.IndexOf($"[0 {first.ByteRange[1]} {first.ByteRange[2]} {first.ByteRange[3]}", StringComparison.Ordinal);
        Assert.True(at > 0);
        var old = text[at..(text.IndexOf(']', at) + 1)];   // padded with spaces by the signer
        var borrowed = $"[0 {second.ByteRange[1]} {second.ByteRange[2]} {second.ByteRange[3]}]";
        Assert.True(borrowed.Length <= old.Length);   // fits in place (the signer pads the ranges with spaces)
        var bytes = (byte[])twice.Clone();
        System.Text.Encoding.Latin1.GetBytes(borrowed.PadRight(old.Length)).CopyTo(bytes, at);
        using var tampered = PdfDocument.Open(bytes);
        var checks = SignatureVerifier.Verify(tampered, null, [], CancellationToken.None);
        Assert.DoesNotContain(checks, c => SignatureTexts.IsValid(c) && c.FieldName == "Unterschrift1");
    }

    /// <summary>A certificate whose name holds name and organisation in one part (S7-2): the check read it as a single element and crashed.</summary>
    [Fact]
    public void MultiValuedNamePart_NoCrash()
    {
        var writer = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSetOf())
        {
            foreach (var (oid, value) in new[] { ("2.5.4.3", "Max Mustermann"), ("2.5.4.10", "Muster GmbH") })
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(oid);
                    writer.WriteCharacterString(System.Formats.Asn1.UniversalTagNumber.UTF8String, value);
                }
        }
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(new X500DistinguishedName(writer.Encode()), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var signer = new SigningCertificate(request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1)), SigningCertificateSource.File);
        var check = Assert.Single(Verify(Sign(TestPdf.Simple(pages: 1).Build(), 50, signer)));
        Assert.Equal(SignatureOutcome.ValidIdentityUnknown, check.Outcome);
        Assert.False(string.IsNullOrEmpty(check.SignerName));
    }

    /// <summary>Many fields pointing at the same signature (S7-11): it is found and checked once, not once per field.</summary>
    [Fact]
    public void SameSignatureFromManyFields_CheckedOnce()
    {
        var signed = Sign(TestPdf.Simple(pages: 1).Build(), 50);
        var copied = Append(signed, (doc, update) =>
        {
            var formRaw = doc.Catalog!.GetRaw(PdfName.Get("AcroForm"));
            var form = CopyOf(doc.Catalog.GetDictionary(PdfName.Get("AcroForm"))!);
            var original = doc.Catalog.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!;
            var fields = new PdfArray(original);
            var value = ((PdfDictionary)original.Get(0)).GetRaw(PdfName.Get("V"))!;
            for (var i = 0; i < 50; i++)
                fields.Add(update.Add(TestPdf.Dict(("FT", TestPdf.N("Sig")), ("T", new PdfString(System.Text.Encoding.ASCII.GetBytes($"Kopie{i}"))), ("V", value))));
            form.Set(PdfName.Get("Fields"), fields);
            if (formRaw is PdfReference formRef) update.Replace(formRef.Id, form);
            else
            {
                var catalog = CopyOf(doc.Catalog);
                catalog.Set(PdfName.Get("AcroForm"), form);
                update.Replace(((PdfReference)doc.Trailer.GetRaw(PdfName.Root)!).Id, catalog);
            }
        });
        using var doc = PdfDocument.Open(copied);
        Assert.Single(DigitalSignatures.Find(doc));
        Assert.Single(SignatureVerifier.Verify(doc, null, [], CancellationToken.None));
    }

    /// <summary>
    /// Certificates of a trusted authority that must not confirm an identity (S7-1, S7-13): a web server certificate, an authority's own
    /// certificate, and an expired one with the signing time set back to when it was valid.
    /// </summary>
    [Fact]
    public void TrustedRoot_WrongPurposeOrExpired_NotConfirmed()
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Muster-Zertifizierungsstelle", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature, true));
        var now = DateTimeOffset.UtcNow;
        using var ca = caRequest.CreateSelfSigned(now.AddYears(-2), now.AddYears(5));
        X509Certificate2Collection Roots() => [X509CertificateLoader.LoadCertificate(ca.RawData)];

        SigningCertificate Issued(string name, X509KeyUsageFlags usage, string? purpose, DateTimeOffset from, DateTimeOffset to)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, true));
            if (purpose is not null) request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(purpose)], false));
            using var issued = request.Create(ca, from, to, Guid.NewGuid().ToByteArray()[..8]);
            return new SigningCertificate(issued.CopyWithPrivateKey(key), SigningCertificateSource.File);
        }
        // as another program would sign: PDF Knead itself refuses these certificates (No. 526)
        SignatureCheck Checked(SigningCertificate signer, DateTimeOffset when)
        {
            using var doc = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
            var signed = PdfSigner.SignUnchecked(doc, 0, Signature(50), signer, when);
            return Assert.Single(Verify(signed, Roots()));
        }

        // for comparison: a signing certificate of the same authority is confirmed
        using (var good = Issued("Erika Mustermann", X509KeyUsageFlags.DigitalSignature, "1.3.6.1.5.5.7.3.4", now.AddDays(-1), now.AddYears(1)))
            Assert.True(Checked(good, now).IdentityConfirmed);   // not confirmed only for the missing revocation status (2.5)

        using (var web = Issued("beispiel.de", X509KeyUsageFlags.KeyEncipherment, "1.3.6.1.5.5.7.3.1", now.AddDays(-1), now.AddYears(1)))
        {
            var check = Checked(web, now);
            Assert.Equal(SignatureOutcome.ValidIdentityUnknown, check.Outcome);
            Assert.NotEmpty(check.Warnings);
        }
        using (var authority = new SigningCertificate(X509CertificateLoader.LoadPkcs12(ca.Export(X509ContentType.Pfx), null), SigningCertificateSource.File))
            Assert.False(Checked(authority, now).IdentityConfirmed);

        using (var expired = Issued("Max Mustermann", X509KeyUsageFlags.DigitalSignature, null, now.AddDays(-30), now.AddDays(-10)))
        {
            var check = Checked(expired, now.AddDays(-20));   // the time set back to when the certificate was valid
            Assert.Equal(SignatureOutcome.ValidIdentityUnknown, check.Outcome);
            Assert.Contains(Tr.T("Das Zertifikat ist abgelaufen; ohne Zeitstempel bestätigt nichts, dass vorher unterschrieben wurde."), check.Warnings);
        }
        using (var future = Issued("Max Mustermann", X509KeyUsageFlags.DigitalSignature, null, now.AddDays(-1), now.AddYears(1)))
            Assert.Contains(Tr.T("Die angegebene Unterschriftszeit liegt in der Zukunft."), Checked(future, now.AddDays(30)).Warnings);
    }

    /// <summary>A signed file whose CMS block is replaced by one made over the same bytes with <paramref name="make"/>.</summary>
    private static byte[] WithBlock(Func<byte[], byte[]> make)
    {
        var signed = Sign(TestPdf.Simple(pages: 1).Build(), 50);
        using var doc = PdfDocument.Open(signed);
        var range = Assert.Single(DigitalSignatures.Find(doc)).ByteRange;
        var content = signed.AsSpan(0, (int)range[1]).ToArray().Concat(signed.AsSpan((int)range[2], (int)range[3]).ToArray()).ToArray();
        var hex = System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(make(content)));
        var gap = (int)(range[2] - range[1] - 2);
        Assert.True(hex.Length <= gap);
        var bytes = (byte[])signed.Clone();
        Array.Fill(bytes, (byte)'0', (int)range[1] + 1, gap);
        hex.CopyTo(bytes, (int)range[1] + 1);
        return bytes;
    }

    /// <summary>
    /// A method the streamed check leaves to <see cref="SignedCms"/> (RSA-PSS) is checked with the content: valid for the signed bytes,
    /// changed after them it is not.
    /// </summary>
    [Fact]
    public void OtherMethod_CheckedWithContent()
    {
        using var certificate = SelfSigned(2048);
        var file = WithBlock(content =>
        {
            var cms = new SignedCms(new ContentInfo(content), detached: true);
            cms.ComputeSignature(new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
                { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), SignaturePadding = RSASignaturePadding.Pss }, silent: true);
            return cms.Encode();
        });
        using (var doc = PdfDocument.Open(file))
            Assert.Equal(SignatureOutcome.ValidIdentityUnknown, Assert.Single(SignatureVerifier.Verify(doc)).Outcome);
        var changed = (byte[])file.Clone();
        changed[20] ^= 1;   // in the header comment, inside the signed bytes
        using (var doc = PdfDocument.Open(changed))
            Assert.Equal(SignatureOutcome.Invalid, Assert.Single(SignatureVerifier.Verify(doc)).Outcome);
    }

    private static byte[] Cms(byte[] content, X509Certificate2 certificate, string digest)
    {
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate) { DigestAlgorithm = new Oid(digest) };
        cms.ComputeSignature(signer, silent: true);
        return cms.Encode();
    }

    private static X509Certificate2 SelfSigned(int bits)
    {
        using var key = RSA.Create(bits);
        var request = new CertificateRequest("CN=Max Mustermann", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>
    /// A CMS block in BER with indefinite lengths (RFC 5652 allows BER for the outer structure; some signing programs write it): read and
    /// checked like DER (OFFENE-PUNKTE 8f (3)).
    /// </summary>
    [Fact]
    public void BerBlock_IndefiniteLengths_Checked()
    {
        using var certificate = SelfSigned(2048);
        var file = WithBlock(content =>
        {
            var der = Cms(content, certificate, "2.16.840.1.101.3.4.2.1");
            // ContentInfo SEQUENCE and its [0] content rewritten with indefinite length (end-of-contents octets)
            var outer = new System.Formats.Asn1.AsnReader(der, System.Formats.Asn1.AsnEncodingRules.DER).ReadSequence();
            var type = outer.ReadEncodedValue().ToArray();
            var explicitContent = outer.ReadSequence(new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, 0, true)).ReadEncodedValue().ToArray();
            return [0x30, 0x80, .. type, 0xA0, 0x80, .. explicitContent, 0x00, 0x00, 0x00, 0x00];
        });
        using var doc = PdfDocument.Open(file);
        var check = Assert.Single(SignatureVerifier.Verify(doc));
        Assert.Equal(SignatureOutcome.ValidIdentityUnknown, check.Outcome);
        var changed = (byte[])file.Clone();
        changed[20] ^= 1;
        using var tampered = PdfDocument.Open(changed);
        Assert.Equal(SignatureOutcome.Invalid, Assert.Single(SignatureVerifier.Verify(tampered)).Outcome);
    }

    /// <summary>
    /// A block of deeply nested ASN.1 with indefinite lengths (OFFENE-PUNKTE 8f (2): tief verschachtelte ASN.1-Längen) and one nested with
    /// definite lengths: no stack overflow, no hang — listed as invalid or not checked, never valid.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeeplyNestedBlock_RefusedWithoutCrash(bool indefinite)
    {
        const int depth = 3000;
        var file = WithBlock(_ =>
        {
            if (indefinite) return [.. Enumerable.Repeat<byte[]>([0x30, 0x80], depth).SelectMany(b => b), .. new byte[2 * depth]];
            // definite lengths from the inside out: each level a SEQUENCE around the one before
            byte[] inner = [0x05, 0x00];
            for (var i = 0; i < depth; i++)
            {
                var length = inner.Length;
                byte[] head = length < 0x80 ? [0x30, (byte)length] : length < 0x100 ? [0x30, 0x81, (byte)length] : [0x30, 0x82, (byte)(length >> 8), (byte)length];
                inner = [.. head, .. inner];
            }
            return inner;
        });
        var check = Assert.Single(Verify(file));
        Assert.False(SignatureTexts.IsValid(check));
    }

    /// <summary>A block with the given signed attributes (OID and DER value) besides the usual ones.</summary>
    private static byte[] WithAttributes(params (string Oid, byte[] Value)[] attributes)
    {
        using var certificate = SelfSigned(2048);
        return WithBlock(content =>
        {
            var cms = new SignedCms(new ContentInfo(content), detached: true);
            var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1") };
            foreach (var (oid, value) in attributes) signer.SignedAttributes.Add(new AsnEncodedData(oid, value));
            cms.ComputeSignature(signer, silent: true);
            return cms.Encode();
        });
    }

    private static byte[] Sequence(Action<System.Formats.Asn1.AsnWriter> write)
    {
        var writer = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
        using (writer.PushSequence()) write(writer);
        return writer.Encode();
    }

    /// <summary>
    /// The purpose the signer states (commitment-type-indication, ETSI EN 319 122-1, 5.2.3): shown as „Zweck“ — „Genehmigung“ for
    /// proofOfApproval; nothing is evaluated (I-13, docs/ABLAUFPLAN.md, 9.3.5, step 5).
    /// </summary>
    [Fact]
    public void CommitmentType_Shown()
    {
        var file = WithAttributes(("1.2.840.113549.1.9.16.2.16", Sequence(w => w.WriteObjectIdentifier("1.2.840.113549.1.9.16.6.5"))));
        var check = Assert.Single(Verify(file));
        Assert.Equal(Tr.T("Genehmigung"), check.Commitment);
        Assert.Contains(new SignatureRow(check).Details, d => d.Label == Tr.T("Zweck") && d.Value == Tr.T("Genehmigung"));
    }

    /// <summary>
    /// A signature policy (signature-policy-identifier): named with its OID and „not evaluated“ — PDF Knead checks by its own implicit
    /// constraints; the result does not change (I-13).
    /// </summary>
    [Fact]
    public void PolicyIdentifier_ShownNotEvaluated()
    {
        var policy = Sequence(w =>
        {
            w.WriteObjectIdentifier("1.2.3.4.5.6");
            using (w.PushSequence())
            {
                using (w.PushSequence()) w.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1");
                w.WriteOctetString(new byte[32]);
            }
        });
        var plain = Assert.Single(Verify(WithAttributes()));
        var check = Assert.Single(Verify(WithAttributes(("1.2.840.113549.1.9.16.2.15", policy))));
        Assert.Contains(Tr.T("Die Signatur nennt die Signaturrichtlinie {0}; sie wurde nicht ausgewertet.", "1.2.3.4.5.6"), check.Warnings);
        Assert.Equal(plain.Outcome, check.Outcome);
    }

    /// <summary>
    /// Elliptic curves of German signature cards (Brainpool, RFC 5639) besides the NIST curves: signed with the digest the curve asks for —
    /// SHA-256 up to 256 bits, SHA-384 for 384, SHA-512 for 512 and 521 (ISO/TS 32002, Table 3), the same for the content and the signed
    /// attributes — and checked (OFFENE-PUNKTE 8f (3); docs/ABLAUFPLAN.md, 4.3; docs/FEHLERGEDAECHTNIS.md, No. 535). Before always SHA-256.
    /// </summary>
    [Theory]
    [InlineData("nistP256", "2.16.840.1.101.3.4.2.1", "1.2.840.10045.4.3.2")]
    [InlineData("brainpoolP256r1", "2.16.840.1.101.3.4.2.1", "1.2.840.10045.4.3.2")]
    [InlineData("nistP384", "2.16.840.1.101.3.4.2.2", "1.2.840.10045.4.3.3")]
    [InlineData("brainpoolP384r1", "2.16.840.1.101.3.4.2.2", "1.2.840.10045.4.3.3")]
    [InlineData("nistP521", "2.16.840.1.101.3.4.2.3", "1.2.840.10045.4.3.4")]
    [InlineData("brainpoolP512r1", "2.16.840.1.101.3.4.2.3", "1.2.840.10045.4.3.4")]
    public void EcdsaCurves_HashMatchesCurve(string curve, string digest, string method)
    {
        using var key = ECDsa.Create(ECCurve.CreateFromFriendlyName(curve));
        var request = new CertificateRequest("CN=Max Mustermann", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var signer = new SigningCertificate(X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12, "x"), "x",
            X509KeyStorageFlags.EphemeralKeySet), SigningCertificateSource.File);
        var signed = Sign(TestPdf.Simple(pages: 1).Build(), 50, signer);
        using (var doc = PdfDocument.Open(signed))
        {
            var cms = new System.Security.Cryptography.Pkcs.SignedCms();
            cms.Decode(CmsSignature.Der(Assert.Single(DigitalSignatures.Find(doc)).Signature!.GetString(PdfName.Contents)!.Bytes)!);
            Assert.Equal((digest, method), (cms.SignerInfos[0].DigestAlgorithm.Value, cms.SignerInfos[0].SignatureAlgorithm.Value));
        }
        var check = Assert.Single(Verify(signed));
        Assert.Equal(SignatureOutcome.ValidIdentityUnknown, check.Outcome);
        signed[20] ^= 1;
        Assert.Equal(SignatureOutcome.Invalid, Assert.Single(Verify(signed)).Outcome);
    }

    /// <summary>Methods that no longer protect a signature (S7-8).</summary>
    [Fact]
    public void WeakMethods_InvalidOrWarned()
    {
        using var rsa2048 = SelfSigned(2048);
        using var rsa1024 = SelfSigned(1024);
        using var rsa768 = SelfSigned(768);
        // for comparison: the replacement itself is valid with SHA-256
        Assert.True(SignatureTexts.IsValid(Assert.Single(Verify(WithBlock(c => Cms(c, rsa2048, "2.16.840.1.101.3.4.2.1"))))));

        // unsafe: not confirmed (yellow), no longer proving the integrity (decision D, docs/ABLAUFPLAN.md, 2.5)
        var md5 = Assert.Single(Verify(WithBlock(c => Cms(c, rsa2048, "1.2.840.113549.2.5"))));
        Assert.Equal(SignatureOutcome.Unconfirmed, md5.Outcome);
        Assert.False(SignatureTexts.IsValid(md5));
        Assert.Equal(Tr.T("Die Signatur verwendet ein unsicheres Verfahren (MD5); sie schützt das Dokument nicht."), md5.UnconfirmedReason);
        Assert.Equal("INDETERMINATE/CRYPTO_CONSTRAINTS_FAILURE_NO_POE", SignatureTexts.Indication(md5).ToString());

        var sha1 = Assert.Single(Verify(WithBlock(c => Cms(c, rsa2048, "1.3.14.3.2.26"))));
        Assert.True(SignatureTexts.IsValid(sha1));
        Assert.Contains(Tr.T("Die Signatur verwendet SHA-1; das gilt heute als veraltet."), sha1.Warnings);

        var short1024 = Assert.Single(Verify(WithBlock(c => Cms(c, rsa1024, "2.16.840.1.101.3.4.2.1"))));
        Assert.True(SignatureTexts.IsValid(short1024));
        Assert.Contains(Tr.T("Der Schlüssel des Zertifikats ist nach heutigem Stand zu kurz ({0} Bit).", 1024), short1024.Warnings);
        var short768 = Assert.Single(Verify(WithBlock(c => Cms(c, rsa768, "2.16.840.1.101.3.4.2.1"))));
        Assert.Equal(SignatureOutcome.Unconfirmed, short768.Outcome);
        Assert.False(SignatureTexts.IsValid(short768));
        // a contradicting content type cannot be made with .NET's signer (it adds the matching one); checked with a hand-made block in the
        // round S7 (PRUEFUNGEN §6i)
    }

    /// <summary>
    /// A PKCS#12 file with certificate, intermediate authority and root (S7-3): the intermediate is embedded from the file (offline), so a
    /// reader trusting the root confirms the identity; before, only the certificate itself was embedded.
    /// </summary>
    [Fact]
    public void ChainFromFile_Embedded_IdentityConfirmed()
    {
        var now = DateTimeOffset.UtcNow;
        X509Certificate2 Authority(string name, RSA key, X509Certificate2? issuer, RSA? issuerKey)
        {
            var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            return issuer is null ? request.CreateSelfSigned(now.AddDays(-1), now.AddYears(5)) : request.Create(issuer, now.AddDays(-1), now.AddYears(4), [9, 1]);
        }
        using var rootKey = RSA.Create(2048);
        using var interKey = RSA.Create(2048);
        using var leafKey = RSA.Create(2048);
        using var root = Authority("Muster-Wurzel", rootKey, null, null);
        using var inter = Authority("Muster-Zwischenstelle", interKey, root, rootKey);
        var leafRequest = new CertificateRequest("CN=Erika Mustermann", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var interSigner = inter.CopyWithPrivateKey(interKey);
        using var leaf = leafRequest.Create(interSigner, now.AddDays(-1), now.AddYears(1), [9, 2]);
        using var leafWithKey = leaf.CopyWithPrivateKey(leafKey);
        var file = Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"kette-{Guid.NewGuid():N}.pfx");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var collection = new X509Certificate2Collection { leafWithKey, X509CertificateLoader.LoadCertificate(inter.RawData), X509CertificateLoader.LoadCertificate(root.RawData) };
        File.WriteAllBytes(file, collection.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, "geheim")!);

        using var signer = SigningCertificates.LoadFile(file, "geheim");
        Assert.Equal(2, signer.Chain.Count);
        var signed = Sign(TestPdf.Simple(pages: 1).Build(), 50, signer);
        var check = Assert.Single(Verify(signed, [X509CertificateLoader.LoadCertificate(root.RawData)]));
        Assert.Equal(SignatureOutcome.Unconfirmed, check.Outcome);   // no validation data (2.5)
        Assert.True(check.IdentityConfirmed);
    }

    /// <summary>
    /// A block with RSASSA-PSS and a salt length the program does not check (20 instead of 32): „not checked“ with the reason, never „changed“
    /// (docs/ABLAUFPLAN.md, 4.3, step 8; docs/FEHLERGEDAECHTNIS.md, No. 537); the same block with the usual salt is valid.
    /// </summary>
    [Fact]
    public void Pss_OtherSaltLength_NotChecked()
    {
        using var rsa = SelfSigned(2048);
        var valid = Assert.Single(Verify(WithBlock(c => CmsSignature.CreateFromDigest(SHA256.HashData(c), rsa, pss: true)!)));
        Assert.True(SignatureTexts.IsValid(valid) || valid.Outcome == SignatureOutcome.ValidIdentityUnknown, valid.Problem);
        var check = Assert.Single(Verify(WithBlock(c => CmsSignatureTests.WithPssSalt(CmsSignature.CreateFromDigest(SHA256.HashData(c), rsa, pss: true)!, 20))));
        Assert.Equal(SignatureOutcome.NotChecked, check.Outcome);
        Assert.Equal(Tr.T("Das Signaturverfahren {0} wird nicht geprüft.", Tr.T("RSASSA-PSS mit diesen Parametern")), check.Problem);
    }
}
