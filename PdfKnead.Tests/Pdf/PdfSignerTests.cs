using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Signing digitally (KONZEPT.md, section 5.17, step S3): the signed byte ranges cover the whole file except the CMS block, the
/// block verifies, a second signature leaves the first intact, an empty field of the file is filled. With PDFKNEAD_ABLAGE=&lt;folder&gt;
/// the files are stored for a cross-check in other readers.
/// </summary>
public sealed class PdfSignerTests
{
    private static readonly Lazy<SigningCertificate> Certificate = new(() =>
        SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"signer-{Guid.NewGuid():N}.p12")));

    private static readonly DateTimeOffset When = new(2026, 10, 25, 14, 30, 0, TimeSpan.FromHours(2));

    private static InkElement Signature(double left, double bottom, string? field = null) => new()
    {
        Rect = new PdfRectangle(left, bottom, left + 200, bottom + 80),
        Strokes = [[0, 20, 40, 70, 90, 10, 140, 60, 190, 30]],
        StrokeWidth = 3,
        Color = 0x1B3A8C,
        Digital = new DigitalSigning("Max Mustermann · 25.10.2026 14:30", field),
    };

    private static byte[] Sign(byte[] file, PageElement signature, int page = 0, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        using var doc = PdfDocument.Open(file);
        var signed = PdfSigner.Sign(doc, page, signature, Certificate.Value, When);
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, $"signiert-{name}.pdf");
            for (var i = 2; File.Exists(target); i++) target = Path.Combine(folder, $"signiert-{name}-{i}.pdf");
            File.WriteAllBytes(target, signed);
        }
        return signed;
    }

    /// <summary>Checks one signature like a PDF reader: ranges, digest and CMS signature over exactly those bytes.</summary>
    private static void AssertValid(byte[] file, DigitalSignatureEntry signature, bool coversWholeFile)
    {
        Assert.True(signature.Intact);
        var r = signature.ByteRange;
        Assert.Equal(0, r[0]);
        if (coversWholeFile) Assert.Equal(file.Length, r[2] + r[3]);
        var content = file.AsSpan(0, (int)r[1]).ToArray().Concat(file.AsSpan((int)r[2], (int)r[3]).ToArray()).ToArray();
        // The block ends where its DER length says, not at the last non-zero digit (the block itself may end in a zero byte)
        var block = Convert.FromHexString(System.Text.Encoding.ASCII.GetString(file, (int)r[1] + 1, (int)(r[2] - r[1]) - 2));
        Assert.True(System.Formats.Asn1.AsnDecoder.TryReadEncodedValue(block, System.Formats.Asn1.AsnEncodingRules.DER, out _, out _, out _, out var length));
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        cms.Decode(block.AsSpan(0, length));
        cms.CheckSignature(verifySignatureOnly: true);
        Assert.Equal("Max Mustermann", cms.SignerInfos[0].Certificate!.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false));
        Assert.Contains(cms.SignerInfos[0].SignedAttributes.Cast<CryptographicAttributeObject>(), a => a.Oid.Value == "1.2.840.113549.1.9.16.2.47");
        Assert.DoesNotContain(cms.SignerInfos[0].SignedAttributes.Cast<CryptographicAttributeObject>(), a => a.Oid.Value == "1.2.840.113549.1.9.5");
    }

    [Fact]
    public void Sign_NewField_ValidAndVisible()
    {
        var original = TestPdf.Simple(pages: 2).Build();
        var signed = Sign(original, Signature(100, 100));
        Assert.Equal(original, signed.AsSpan(0, original.Length).ToArray());
        using var doc = PdfDocument.Open(signed);
        Assert.False(doc.WasRepaired);
        var signature = Assert.Single(DigitalSignatures.Find(doc));
        Assert.Equal(Tr.T("Unterschrift") + "1", signature.FieldName);
        AssertValid(signed, signature, coversWholeFile: true);
        Assert.Equal(3, doc.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetInteger(PdfName.Get("SigFlags")));

        // the widget sits on the first page with an appearance of the signature's size
        var widget = (PdfDictionary)doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0);
        Assert.Equal("Sig", widget.GetName(PdfName.Get("FT"))!.Value);
        var form = (PdfStream)widget.GetDictionary(PdfName.Get("AP"))!.Get(PdfName.N);
        Assert.NotEmpty(doc.GetStreamData(form));
        var sig = widget.GetDictionary(PdfName.Get("V"))!;
        Assert.Equal("ETSI.CAdES.detached", sig.GetName(PdfName.Get("SubFilter"))!.Value);
        Assert.Equal("D:20261025143000+02'00'", sig.GetString(PdfName.Get("M"))!.ToText());
    }

    [Fact]
    public void SecondSignature_FirstStaysValid()
    {
        var once = Sign(TestPdf.Simple(pages: 1).Build(), Signature(50, 100));
        var twice = Sign(once, Signature(300, 100));
        Assert.Equal(once, twice.AsSpan(0, once.Length).ToArray());
        using var doc = PdfDocument.Open(twice);
        var signatures = DigitalSignatures.Find(doc);
        Assert.Equal(2, signatures.Count);
        var first = signatures.Single(s => s.ByteRange[2] + s.ByteRange[3] == once.Length);
        var second = signatures.Single(s => s.ByteRange[2] + s.ByteRange[3] == twice.Length);
        AssertValid(twice, first, coversWholeFile: false);
        AssertValid(twice, second, coversWholeFile: true);
        Assert.NotEqual(first.FieldName, second.FieldName);
    }

    [Fact]
    public void EmptyFieldOfTheFile_IsFilled()
    {
        var pdf = TestPdf.Simple(pages: 1);
        var field = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("FT", TestPdf.N("Sig")),
            ("T", new PdfString("Kunde"u8.ToArray())), ("Rect", TestPdf.Box(100, 100, 300, 180))));
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new PdfArray { TestPdf.Ref(field) })));
        var signed = Sign(pdf.Build(), Signature(100, 100, field: "Kunde"));
        using var doc = PdfDocument.Open(signed);
        var signature = Assert.Single(DigitalSignatures.Find(doc));
        Assert.Equal("Kunde", signature.FieldName);
        AssertValid(signed, signature, coversWholeFile: true);
        Assert.Single(doc.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!);
    }

    [Fact]
    public void SignedSaving_UnsignedFile_WrittenAnewThenSigned()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 2).Build());
        var pages = PageItem.From(doc);
        var edited = pages.ToArray();
        edited[1] = pages[1].WithElements([Signature(100, 100)]);
        Assert.True(SignedSaving.HasPending(edited));
        Assert.False(SignedSaving.AppendsOnly(doc, pages, edited));   // nothing signed yet: written anew
        var path = Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"signiert-{Guid.NewGuid():N}.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var result = SignedSaving.Save(doc, edited, path, new PdfKnead.Core.Pdf.Writing.PdfSaveOptions(), appendOnly: false, Certificate.Value, When);
        var file = File.ReadAllBytes(path);
        Assert.Equal(file.Length, result.Length);
        using var saved = PdfDocument.Open(file);
        Assert.Equal(2, saved.PageCount);
        AssertValid(file, Assert.Single(DigitalSignatures.Find(saved)), coversWholeFile: true);
        Assert.NotNull(saved.Pages[1].Dictionary.GetArray(PdfName.Get("Annots")));
        // the pending element itself is not written into the page: only the field shows it
        Assert.DoesNotContain(PageItem.From(saved).SelectMany(p => p.Elements), e => e.Digital is not null);
    }

    [Fact]
    public void SignedSaving_SignedFile_AppendsOnlyWithoutOtherChanges()
    {
        var once = Sign(TestPdf.Simple(pages: 1).Build(), Signature(50, 100));
        using var doc = PdfDocument.Open(once);
        var pages = PageItem.From(doc);
        var signedAgain = new[] { pages[0].WithElements([.. pages[0].Elements, Signature(300, 100)]) };
        Assert.True(SignedSaving.AppendsOnly(doc, pages, signedAgain));
        // a signature written fixed into the page rewrites the file; a comment is appended (S5)
        var fixedSignature = new SignatureElement { Rect = new PdfRectangle(50, 700, 250, 760), Name = "Max Mustermann", FontFile = "PatrickHand-Regular.ttf" };
        Assert.False(SignedSaving.AppendsOnly(doc, pages, [pages[0].WithElements([.. pages[0].Elements, Signature(300, 100), fixedSignature])]));
        Assert.True(SignedSaving.AppendsOnly(doc, pages, [pages[0].WithElements([.. pages[0].Elements, new TextElement { Rect = new PdfRectangle(50, 700, 250, 720), Text = "Notiz" }])]));
        Assert.True(SignedSaving.AppendsOnly(doc, pages, pages));   // nothing changed

        var path = Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"signiert-{Guid.NewGuid():N}.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SignedSaving.Save(doc, signedAgain, path, new PdfKnead.Core.Pdf.Writing.PdfSaveOptions(), appendOnly: true, Certificate.Value, When);
        var file = File.ReadAllBytes(path);
        Assert.Equal(once, file.AsSpan(0, once.Length).ToArray());
        using var saved = PdfDocument.Open(file);
        Assert.Equal(2, DigitalSignatures.CountIntact(saved));
    }

    /// <summary>
    /// The form's field list as an object of its own (/Fields 12 0 R, as many writers do it): signing adds the signature field and keeps
    /// all other fields. A copy of the form dictionary could not resolve the reference, so the list was replaced by one holding only the
    /// new field (Fehlergedächtnis 105).
    /// </summary>
    [Fact]
    public void FieldListAsObject_SigningKeepsAllFields()
    {
        byte[] form;
        using (var source = PdfDocument.Open(FormFillTests.AllKinds()))
        {
            var update = new PdfKnead.Core.Pdf.Writing.IncrementalUpdate(source);
            var acroFormRaw = source.Catalog!.GetRaw(PdfName.Get("AcroForm"));
            var acroForm = new PdfDictionary();
            foreach (var (key, value) in source.Catalog.GetDictionary(PdfName.Get("AcroForm"))!.Entries) acroForm.Set(key, value);
            acroForm.Set(PdfName.Get("Fields"), update.Add(new PdfArray(source.Catalog.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!)));
            if (acroFormRaw is PdfReference formRef) update.Replace(formRef.Id, acroForm);
            else
            {
                var catalog = new PdfDictionary();
                foreach (var (key, value) in source.Catalog.Entries) catalog.Set(key, value);
                catalog.Set(PdfName.Get("AcroForm"), update.Add(acroForm));
                update.Replace(((PdfReference)source.Trailer.GetRaw(PdfName.Root)!).Id, catalog);
            }
            using var output = new MemoryStream();
            update.WriteTo(output);
            form = output.ToArray();
        }
        using (var check = PdfDocument.Open(form))
            Assert.IsType<PdfReference>(check.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetRaw(PdfName.Get("Fields")));

        var signed = Sign(form, Signature(300, 100));
        using var doc = PdfDocument.Open(signed);
        var fields = doc.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!;
        Assert.Equal(8, fields.Count);   // the seven fields of the form and the signature
        Assert.Single(DigitalSignatures.Find(doc));
    }

    /// <summary>A file with an empty signature field „Kunde“ at [100 100 300 180]; with <paramref name="separateWidget"/> the field has its widget as a kid.</summary>
    private static byte[] WithEmptyField(bool separateWidget)
    {
        var pdf = TestPdf.Simple(pages: 1);
        const int page = 5;
        int field, widget;
        if (separateWidget)
        {
            field = pdf.Add(TestPdf.Dict(("FT", TestPdf.N("Sig")), ("T", new PdfString("Kunde"u8.ToArray()))));
            widget = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("Rect", TestPdf.Box(100, 100, 300, 180)),
                ("Parent", TestPdf.Ref(field)), ("P", TestPdf.Ref(page))));
            ((PdfDictionary)pdf.Get(field)).Set(PdfName.Get("Kids"), new PdfArray { TestPdf.Ref(widget) });
        }
        else
        {
            field = widget = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("FT", TestPdf.N("Sig")),
                ("T", new PdfString("Kunde"u8.ToArray())), ("Rect", TestPdf.Box(100, 100, 300, 180)), ("P", TestPdf.Ref(page))));
        }
        ((PdfDictionary)pdf.Get(page)).Set(PdfName.Get("Annots"), new PdfArray { TestPdf.Ref(widget) });
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new PdfArray { TestPdf.Ref(field) })));
        return pdf.Build();
    }

    private static InkElement Placed(double left, double bottom, double right, double top) =>
        Signature(0, 0, field: "Kunde") with { Rect = new PdfRectangle(left, bottom, right, top) };

    /// <summary>
    /// A signature into a field of the file, smaller than the field (S7-5): the field is filled and keeps its place, the appearance has the
    /// field's size with the signature at its spot (before, it was stretched over the field). Moved out of the field: a field of its own.
    /// </summary>
    [Fact]
    public void FieldOfTheFile_SignatureWithinOrMovedOut()
    {
        var within = Sign(WithEmptyField(false), Placed(120, 110, 220, 150));
        using (var doc = PdfDocument.Open(within))
        {
            var signature = Assert.Single(DigitalSignatures.Find(doc));
            Assert.Equal("Kunde", signature.FieldName);
            var form = (PdfStream)signature.Field!.GetDictionary(PdfName.Get("AP"))!.Get(PdfName.N);
            var box = form.Dictionary.GetArray(PdfName.Get("BBox"))!;
            Assert.Equal(200, box.GetNumber(2)!.Value, 1);
            Assert.Equal(80, box.GetNumber(3)!.Value, 1);
        }
        var outside = Sign(WithEmptyField(false), Placed(350, 400, 450, 440));
        using (var doc = PdfDocument.Open(outside))
        {
            var signature = Assert.Single(DigitalSignatures.Find(doc));
            Assert.NotEqual("Kunde", signature.FieldName);
            Assert.Equal(2, doc.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!.Count);   // the empty one stays
        }
    }

    /// <summary>A field whose widget is a kid of it (S7-6): it is filled — value on the field, appearance on the widget — instead of a second field.</summary>
    [Fact]
    public void FieldWithSeparateWidget_IsFilled()
    {
        var signed = Sign(WithEmptyField(true), Placed(100, 100, 300, 180));
        using var doc = PdfDocument.Open(signed);
        var signature = Assert.Single(DigitalSignatures.Find(doc));
        Assert.Equal("Kunde", signature.FieldName);
        AssertValid(signed, signature, coversWholeFile: true);
        Assert.Single(doc.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!);
        var widget = (PdfDictionary)doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0);
        Assert.NotNull(widget.GetDictionary(PdfName.Get("AP")));
    }

    /// <summary>
    /// The signing time /M in the date form of the file's version: PDF 2.0 without the closing apostrophe, PDF 1.7 with it (ISO 32000-2,
    /// 7.9.4; docs/FEHLERGEDAECHTNIS.md, No. 487).
    /// </summary>
    [Theory]
    [InlineData("2.0", "D:20261025143000+02'00")]
    [InlineData("1.7", "D:20261025143000+02'00'")]
    public void SigningTime_InTheDateFormOfTheVersion(string version, string expected)
    {
        using var blank = BlankPage.CreateDocument(595, 842, 1);
        using var written = new MemoryStream();
        PdfWriter.Write(blank, PageItem.From(blank), written, new PdfSaveOptions { Version = version });
        var signed = Sign(written.ToArray(), Signature(100, 100));
        using var doc = PdfDocument.Open(signed);
        var field = doc.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!.Get(0);
        var sig = ((PdfDictionary)field).GetDictionary(PdfName.Get("V"))!;
        Assert.Equal(expected, sig.GetString(PdfName.Get("M"))!.ToText());
    }

    /// <summary>
    /// A 1.7 file that declares PAdES in an array (not allowed before 2.0, but written by other programs) at a sufficient level: signing
    /// leaves the declaration as it is — before it was replaced by a dictionary of a lower level (docs/FEHLERGEDAECHTNIS.md, No. 488).
    /// </summary>
    [Fact]
    public void EsicAsArray_SufficientLevel_Kept()
    {
        var pdf = TestPdf.Simple(1);
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(PdfName.Get("Extensions"), TestPdf.Dict(("ESIC", new PdfArray([TestPdf.Dict(("BaseVersion", TestPdf.N("1.7")), ("ExtensionLevel", PdfInteger.Of(5)))]))));
        using var doc = PdfDocument.Open(Sign(pdf.Build(), Signature(100, 100)));
        var esic = Assert.IsType<PdfArray>(doc.Catalog!.GetDictionary(PdfName.Get("Extensions"))!.Get(PdfName.Get("ESIC")));
        Assert.Equal(5, ((PdfDictionary)esic.Get(0)).GetInteger(PdfName.Get("ExtensionLevel")));
    }

    /// <summary>
    /// A certificate that every checker would reject signs nothing: expired, not yet valid, only for certification authorities (key usage
    /// keyCertSign), only for web servers (extended key usage serverAuth) — refused with the reason before anything is written (EN 319 122-1,
    /// 6.3; RFC 5280, 4.2.1.3 and 4.2.1.12; docs/ABLAUFPLAN.md, 4.1, H2; docs/FEHLERGEDAECHTNIS.md, No. 526). Before the signature was written,
    /// and PDF Knead itself then called it invalid.
    /// </summary>
    [Theory]
    [InlineData("abgelaufen")]
    [InlineData("noch nicht gültig")]
    [InlineData("nur Zertifizierung")]
    [InlineData("nur Webserver")]
    public void UnusableCertificate_NotSigned(string fault)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Max Mustermann", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            fault == "nur Zertifizierung" ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        if (fault == "nur Webserver") request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var now = DateTimeOffset.UtcNow;
        using var created = fault switch
        {
            "abgelaufen" => request.CreateSelfSigned(now.AddYears(-2), now.AddDays(-1)),
            "noch nicht gültig" => request.CreateSelfSigned(now.AddDays(2), now.AddYears(2)),
            _ => request.CreateSelfSigned(now.AddDays(-1), now.AddYears(2)),
        };
        using var signer = new SigningCertificate(X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null), SigningCertificateSource.File);
        Assert.NotNull(SigningCertificates.Unusable(signer.Certificate, now));
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var refused = Assert.Throws<SigningCertificateException>(() => PdfSigner.Sign(doc, 0, Signature(100, 100), signer, When));
        Assert.StartsWith(Tr.T("Mit diesem Zertifikat lässt sich nicht unterschreiben: {0}", ""), refused.Message);
        Assert.Contains(fault switch
        {
            "abgelaufen" => Tr.T("Das Zertifikat ist am {0} abgelaufen.", created.NotAfter.ToString("d", System.Globalization.CultureInfo.CurrentCulture)),
            "noch nicht gültig" => Tr.T("Das Zertifikat ist erst ab {0} gültig.", created.NotBefore.ToString("d", System.Globalization.CultureInfo.CurrentCulture)),
            "nur Zertifizierung" => Tr.T("Das Zertifikat ist nicht zum Unterschreiben bestimmt (Schlüsselverwendung)."),
            _ => Tr.T("Das Zertifikat ist für einen anderen Zweck ausgestellt (zum Beispiel für eine Webseite), nicht zum Unterschreiben."),
        }, refused.Message);
        // the own certificate of PDF Knead signs as before
        Assert.Null(SigningCertificates.Unusable(Certificate.Value.Certificate, now));
    }

    /// <summary>
    /// A signature in a PDF 2.0 file names the program that made it: /Prop_Build &lt;&lt; /App &lt;&lt; /Name /PDF-Knead /REx (version) &gt;&gt; &gt;&gt;, direct,
    /// nothing else — no operating system, no date (EN 319 142-1 V1.3.0, 5.3; docs/ABLAUFPLAN.md, 4.2; docs/FEHLERGEDAECHTNIS.md, No. 532).
    /// In a PDF 1.7 file none (the draft is about ISO 32000-2 signatures).
    /// </summary>
    [Theory]
    [InlineData("2.0", true)]
    [InlineData("1.7", false)]
    public void PropBuild_NamesProgramAndVersion_In20(string version, bool expected)
    {
        using var blank = BlankPage.CreateDocument(595, 842, 1);
        using var written = new MemoryStream();
        PdfWriter.Write(blank, PageItem.From(blank), written, new PdfSaveOptions { Version = version });
        var signed = Sign(written.ToArray(), Signature(100, 100));
        using var doc = PdfDocument.Open(signed);
        var field = doc.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!.Get(0);
        var sig = ((PdfDictionary)field).GetDictionary(PdfName.Get("V"))!;
        if (!expected)
        {
            Assert.False(sig.ContainsKey(PdfName.Get("Prop_Build")));
            return;
        }
        var build = Assert.IsType<PdfDictionary>(sig.GetRaw(PdfName.Get("Prop_Build")));
        Assert.Equal(["App"], build.Entries.Select(e => e.Key.Value));
        var app = Assert.IsType<PdfDictionary>(build.GetRaw(PdfName.Get("App")));
        Assert.Equal(["Name", "REx"], app.Entries.Select(e => e.Key.Value).Order());
        Assert.Equal("PDF-Knead", app.GetName(PdfName.Name)!.Value);
        Assert.Equal(AppVersion.Raw, app.GetString(PdfName.Get("REx"))!.ToText());
    }

    private static SigningCertificate EcdsaSigner(string curve)
    {
        using var key = ECDsa.Create(ECCurve.CreateFromFriendlyName(curve));
        var request = new CertificateRequest("CN=Max Mustermann", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return new SigningCertificate(X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12, "x"), "x",
            X509KeyStorageFlags.EphemeralKeySet), SigningCertificateSource.File);
    }

    private static byte[] Blank(string version)
    {
        using var blank = BlankPage.CreateDocument(595, 842, 1);
        using var written = new MemoryStream();
        PdfWriter.Write(blank, PageItem.From(blank), written, new PdfSaveOptions { Version = version });
        return written.ToArray();
    }

    private static PdfObject? Iso(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        return doc.Catalog!.GetDictionary(PdfName.Get("Extensions"))?.Get(PdfName.Get("ISO_")) is { } iso and not PdfNull ? iso : null;
    }

    /// <summary>
    /// ECDSA in a PDF 2.0 file declares ISO/TS 32002: /Extensions /ISO_ an array with one element — base version 2.0, level 32002, revision
    /// „:2022“, /URL (ISO/TS 32002, clause 4). In a PDF 1.7 file none: its base version would lie above the file's (ISO 32000-2, 7.12.4);
    /// RSA declares nothing (docs/ABLAUFPLAN.md, 4.3; docs/FEHLERGEDAECHTNIS.md, No. 536).
    /// </summary>
    [Fact]
    public void EcdsaIn20_DeclaresIso32002_NotIn17_NotForRsa()
    {
        using var signer = EcdsaSigner("nistP256");
        byte[] SignWith(byte[] file, SigningCertificate certificate)
        {
            using var doc = PdfDocument.Open(file);
            return PdfSigner.Sign(doc, 0, Signature(100, 100), certificate, When);
        }
        var iso = Assert.IsType<PdfArray>(Iso(SignWith(Blank("2.0"), signer)));
        var entry = Assert.IsType<PdfDictionary>(Assert.Single(Enumerable.Range(0, iso.Count).Select(iso.Get)));
        Assert.Equal("2.0", entry.GetName(PdfName.Get("BaseVersion"))!.Value);
        Assert.Equal(32002, entry.GetInteger(PdfName.Get("ExtensionLevel")));
        Assert.Equal(":2022", entry.GetString(PdfName.Get("ExtensionRevision"))!.ToText());
        Assert.Equal("https://www.iso.org/standard/45875.html", entry.GetString(PdfName.Get("URL"))!.ToText());
        Assert.Null(Iso(SignWith(Blank("1.7"), signer)));
        Assert.Null(Iso(SignWith(Blank("2.0"), Certificate.Value)));
    }

    /// <summary>
    /// A 2.0 file signed with RSA, then with a Brainpool key: the declaration the second signature adds is only a declaration — the first
    /// signature stays valid and unchanged (docs/ABLAUFPLAN.md, 4.3, step 5).
    /// </summary>
    [Fact]
    public void SecondSignatureWithBrainpool_DeclaresIso32002_FirstStaysValid()
    {
        using var brainpool = EcdsaSigner("brainpoolP256r1");
        var first = Sign(Blank("2.0"), Signature(100, 100));
        byte[] second;
        using (var doc = PdfDocument.Open(first))
            second = PdfSigner.Sign(doc, 0, Signature(100, 300), brainpool, When);
        Assert.IsType<PdfArray>(Iso(second));
        using var signed = PdfDocument.Open(second);
        var checks = SignatureVerifier.Verify(signed, null, [], CancellationToken.None);
        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.True(SignatureTexts.IsValid(c) || c.Outcome == SignatureOutcome.ValidIdentityUnknown, c.Problem));
        Assert.Equal(LaterChanges.FormsAndSignatures, Assert.Single(checks, c => !c.CoversWholeFile).Later);
    }

    /// <summary>
    /// /Name only where the name is not in the certificate (ISO 32000-2, Table 255; EN 319 142-1 V1.3.0, 5.3; docs/ABLAUFPLAN.md, 4.4 b;
    /// docs/FEHLERGEDAECHTNIS.md, No. 538): „CN=Max Mustermann“ — no /Name, the check names the holder from the certificate; a certificate
    /// without a readable name — /Name with the name the signer gave.
    /// </summary>
    [Fact]
    public void Name_OnlyWhenNotInTheCertificate()
    {
        PdfDictionary SigOf(byte[] file)
        {
            using var doc = PdfDocument.Open(file);
            return Assert.Single(DigitalSignatures.Find(doc)).Signature!;
        }
        var named = Sign(TestPdf.Simple(pages: 1).Build(), Signature(100, 100));
        Assert.False(SigOf(named).ContainsKey(PdfName.Name));
        using (var doc = PdfDocument.Open(named))
            Assert.Equal("Max Mustermann", Assert.Single(SignatureVerifier.Verify(doc, null, [], CancellationToken.None)).SignerName);

        using var key = RSA.Create(2048);
        var request = new CertificateRequest(new X500DistinguishedName(""), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var anonymous = new SigningCertificate(X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null), SigningCertificateSource.File);
        Assert.Equal("", anonymous.Name);
        using var plain = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        var signature = Signature(100, 100);
        var given = PdfSigner.Sign(plain, 0, signature with { Digital = signature.Digital! with { Signer = "Erika Mustermann" } }, anonymous, When);
        Assert.Equal("Erika Mustermann", SigOf(given).GetString(PdfName.Name)!.ToText());
    }

    /// <summary>
    /// In an encrypted file the signature value (/Contents of a signature dictionary) is not encrypted (ISO 32000-2, 7.6.2): written as plain
    /// hex, read as it stands — every other string encrypted as before (docs/ABLAUFPLAN.md, 4.4 e, B-6; docs/FEHLERGEDAECHTNIS.md, No. 542).
    /// Before it was encrypted on writing and decrypted on reading: a reader following the norm saw garbage there.
    /// </summary>
    [Fact]
    public void EncryptedFile_SignatureContents_PlainOnDiskAndRead()
    {
        var block = RandomNumberGenerator.GetBytes(64);
        var pdf = TestPdf.Simple(1);
        var sig = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Sig")), ("Filter", TestPdf.N("Adobe.PPKLite")), ("SubFilter", TestPdf.N("ETSI.CAdES.detached")),
            ("ByteRange", new PdfArray([PdfInteger.Of(0), PdfInteger.Of(10), PdfInteger.Of(20), PdfInteger.Of(30)])), ("Contents", new PdfString(block, isHex: true))));
        var other = pdf.Add(TestPdf.Dict(("Name", new PdfString("Geheimtext"u8.ToArray()))));
        var file = pdf.Build(cipher: Core.Pdf.Security.PdfCipher.Aes256, user: "");
        var text = System.Text.Encoding.Latin1.GetString(file);
        Assert.Contains(Convert.ToHexString(block), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Geheimtext", text);
        using var doc = PdfDocument.Open(file);
        Assert.Equal(block, ((PdfDictionary)doc.GetObject(sig)).GetString(PdfName.Contents)!.Bytes);
        Assert.Equal("Geheimtext", ((PdfDictionary)doc.GetObject(other)).GetString(PdfName.Name)!.ToText());
    }

    /// <summary>
    /// A signed file written anew with protection: its signature dictionary keeps /Contents as plain hex and stays out of the (encrypted) object
    /// streams (ISO 32000-2, 7.6.2; docs/ABLAUFPLAN.md, 4.4 e, B-6; docs/FEHLERGEDAECHTNIS.md, No. 542).
    /// </summary>
    [Fact]
    public void EncryptedRewrite_SignatureContentsStayPlain()
    {
        var signed = Sign(TestPdf.Simple(pages: 1).Build(), Signature(100, 100));
        byte[] block;
        byte[] rewritten;
        using (var doc = PdfDocument.Open(signed))
        {
            block = Assert.Single(DigitalSignatures.Find(doc)).Signature!.GetString(PdfName.Contents)!.Bytes;
            using var output = new MemoryStream();
            PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Protection = new PdfProtection("", "Besitzer-1", Core.Pdf.Security.PdfPermissions.Print) });
            rewritten = output.ToArray();
        }
        var text = System.Text.Encoding.Latin1.GetString(rewritten);
        Assert.Contains(Convert.ToHexString(block.AsSpan(0, 48)), text, StringComparison.OrdinalIgnoreCase);
        using var copy = PdfDocument.Open(rewritten);
        Assert.True(copy.IsEncrypted);
        Assert.Equal(block, Assert.Single(DigitalSignatures.Find(copy)).Signature!.GetString(PdfName.Contents)!.Bytes);
    }

    /// <summary>
    /// Encrypted files are signed (ISO 32000-2, 12.8 with 7.6): AES-256 with an empty open password (as official forms come) and AES-128 with
    /// a password — the update encrypted with the file's key, /Encrypt in its trailer, the signature value plain, the signature valid, the
    /// page still readable (docs/ABLAUFPLAN.md, 4.4 e; docs/FEHLERGEDAECHTNIS.md, No. 543). Before: „An diese Datei lässt sich nichts
    /// anhängen.“
    /// </summary>
    [Theory]
    [InlineData("Aes256", "")]
    [InlineData("Aes128", "geheim")]
    [InlineData("Aes256Gcm", "")]   // AES-GCM (5.4): the update sealed with GCM as well (docs/FEHLERGEDAECHTNIS.md, No. 555)
    public void Encrypted_SignedAndValid(string cipher, string user)
    {
        var file = TestPdf.Simple(1).Build(cipher: Enum.Parse<Core.Pdf.Security.PdfCipher>(cipher), user: user, permissions: Core.Pdf.Security.PdfPermissions.All);
        byte[] signed;
        using (var doc = PdfDocument.Open(file, user))
            signed = PdfSigner.Sign(doc, 0, Signature(100, 100), Certificate.Value, When);
        Assert.Equal(file, signed.AsSpan(0, file.Length).ToArray());
        var update = System.Text.Encoding.Latin1.GetString(signed, file.Length, signed.Length - file.Length);
        Assert.Contains("/Encrypt", update);
        Assert.DoesNotContain("Max Mustermann", update);   // the strings of the update (the field's text) encrypted
        using var saved = PdfDocument.Open(signed, user);
        Assert.True(saved.IsEncrypted);
        Assert.False(saved.WasRepaired);
        var entry = Assert.Single(DigitalSignatures.Find(saved));
        Assert.Contains(Convert.ToHexString(entry.Signature!.GetString(PdfName.Contents)!.Bytes.AsSpan(0, 32)), update, StringComparison.OrdinalIgnoreCase);
        var check = Assert.Single(SignatureVerifier.Verify(saved, null, [], CancellationToken.None));
        Assert.True(SignatureTexts.IsValid(check) || check.Outcome == SignatureOutcome.ValidIdentityUnknown, check.Problem);
        Assert.Equal(Tr.T("Unterschrift") + "1", entry.FieldName);
    }

    /// <summary>
    /// What is refused, with its reason, before anything is written: RC4 (PAdES no longer recommends it), a file that asks for a MAC per
    /// revision (ISO/TS 32004), and a file opened without the owner password whose permissions allow neither filling in nor annotating
    /// (ISO 32000-2, Table 22; docs/FEHLERGEDAECHTNIS.md, No. 543).
    /// </summary>
    [Fact]
    public void Encrypted_Rc4_Mac_NoRights_Refused()
    {
        var rc4 = TestPdf.Simple(1).Build(cipher: Core.Pdf.Security.PdfCipher.Rc4, user: "", permissions: Core.Pdf.Security.PdfPermissions.All);
        using (var doc = PdfDocument.Open(rc4))
            Assert.Equal(Tr.T("Mit RC4 verschlüsselte Dateien lassen sich nicht signieren. Bitte zuerst mit neuem Schutz (AES) speichern."),
                Assert.Throws<NotAppendableException>(() => PdfSigner.Sign(doc, 0, Signature(100, 100), Certificate.Value, When)).Message);

        var (encrypt, handler) = Core.Pdf.Security.StandardSecurityHandler.Create(Core.Pdf.Security.PdfCipher.Aes256, "", "Besitzer-1",
            Core.Pdf.Security.PdfPermissions.All, new byte[16]);
        Assert.Null(IncrementalUpdate.EncryptionRefusal(handler, encrypt, new PdfDictionary()));
        var mac = Tr.T("Diese Datei ist zusätzlich gegen Veränderung geschützt (ISO/TS 32004); PDF-Knead kann sie noch nicht signieren.");
        var withSalt = new PdfDictionary();
        foreach (var (key, value) in encrypt.Entries) withSalt.Set(key, value);
        withSalt.Set(PdfName.Get("KDFSalt"), new PdfString(new byte[32]));
        Assert.Equal(mac, IncrementalUpdate.EncryptionRefusal(handler, withSalt, new PdfDictionary()));
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Get("AuthCode"), new PdfDictionary());
        Assert.Equal(mac, IncrementalUpdate.EncryptionRefusal(handler, encrypt, trailer));

        var printOnly = TestPdf.Simple(1).Build(cipher: Core.Pdf.Security.PdfCipher.Aes256, user: "", permissions: Core.Pdf.Security.PdfPermissions.Print);
        using (var doc = PdfDocument.Open(printOnly))
            Assert.Equal(Tr.T("Die Rechte dieser Datei erlauben kein Unterschreiben."),
                Assert.Throws<Core.Pdf.PdfException>(() => PdfSigner.Sign(doc, 0, Signature(100, 100), Certificate.Value, When)).Message);
        // with the owner password the same file is signed
        using (var owner = PdfDocument.Open(printOnly, "owner"))
            Assert.NotEmpty(PdfSigner.Sign(owner, 0, Signature(100, 100), Certificate.Value, When));
    }
}
