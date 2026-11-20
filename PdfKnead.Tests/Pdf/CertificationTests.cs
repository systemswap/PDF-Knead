using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Certifying and locking fields (KONZEPT.md, section 5.17, step S6; ISO 32000-2:2020, 12.8.2.2 DocMDP and 12.8.2.4 FieldMDP): what each
/// level allows is appended, what it forbids is not, and if it is forced in anyway, the check reports the signature as changed after
/// signing. Only the first signature can certify. With PDFKNEAD_ABLAGE=&lt;folder&gt; the files are stored for a cross-check.
/// </summary>
public sealed class CertificationTests
{
    private static readonly Lazy<SigningCertificate> Own = new(() =>
        SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"zertifizieren-{Guid.NewGuid():N}.p12")));

    private static InkElement Signature(double bottom, CertificationLevel? certify = null, bool lockFields = false) => new()
    {
        Rect = new PdfRectangle(350, bottom, 530, bottom + 60),
        Strokes = [[0, 20, 40, 50, 90, 10, 140, 45, 175, 25]],
        StrokeWidth = 3,
        Color = 0x1B3A8C,
        Digital = new DigitalSigning("Max Mustermann", null, certify, lockFields),
    };

    private static byte[] Sign(byte[] file, InkElement signature, string? store = null)
    {
        using var doc = PdfDocument.Open(file);
        var signed = PdfSigner.Sign(doc, 0, signature, Own.Value, DateTimeOffset.Now);
        if (store is not null && Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, $"zertifiziert-{store}.pdf"), signed);
        }
        return signed;
    }

    private static IReadOnlyList<SignatureCheck> Verify(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        return SignatureVerifier.Verify(doc, null, [], CancellationToken.None);
    }

    /// <summary>The pages of the file with the field „Name“ filled in.</summary>
    private static PageItem[] Filled(PdfDocument doc) =>
    [
        .. PageItem.From(doc).Select((item, i) => i > 0 ? item : item.WithElements([.. item.Elements.Select(e =>
            e is ForeignAnnotation f && FormField.Of(f)?.Name == "Name" ? f with { Value = "Erika Mustermann" } : e)])),
    ];

    private static PageItem[] Commented(PdfDocument doc) =>
    [
        .. PageItem.From(doc).Select((item, i) => i > 0 ? item : item.WithElements([.. item.Elements,
            new TextElement { Rect = new PdfRectangle(320, 760, 560, 790), Text = "Geprüft", FontSize = 12 }])),
    ];

    [Fact]
    public void FormsAndSigning_FillAndSignAppended_CommentsRefused()
    {
        var certified = Sign(FormFillTests.AllKinds(), Signature(100, CertificationLevel.FormsAndSigning), "ausfuellen");
        var check = Assert.Single(Verify(certified));
        Assert.Equal(CertificationLevel.FormsAndSigning, check.Certification);
        Assert.Equal(SignatureOutcome.ValidIdentityUnknown, check.Outcome);
        using var doc = PdfDocument.Open(certified);
        Assert.NotNull(doc.Catalog!.GetDictionary(PdfName.Get("Perms"))?.GetDictionary(PdfName.Get("DocMDP")));
        Assert.Equal(CertificationLevel.FormsAndSigning, SignaturePermissions.Of(doc).Certification);
        var saved = PageItem.From(doc);

        // filling in: appended, still valid
        Assert.True(SignedSaving.AppendsOnly(doc, saved, Filled(doc)));
        var filled = AppendedChanges.Apply(doc, Filled(doc), new PdfSaveOptions());
        Assert.Equal(SignatureOutcome.ValidIdentityUnknown, Assert.Single(Verify(filled)).Outcome);

        // a further signature: appended, both valid
        using (var afterFill = PdfDocument.Open(filled))
        {
            var twice = PdfSigner.Sign(afterFill, 0, Signature(200), Own.Value, DateTimeOffset.Now);
            var checks = Verify(twice);
            Assert.Equal(2, checks.Count);
            Assert.All(checks, c => Assert.Equal(SignatureOutcome.ValidIdentityUnknown, c.Outcome));
        }

        // a comment: not allowed, so not appended — and forced in anyway, the certification counts as broken
        Assert.False(SignedSaving.AppendsOnly(doc, saved, Commented(doc), out var reason));
        Assert.Equal(Tr.T("Das Dokument ist zertifiziert: danach sind nur Ausfüllen und Unterschreiben erlaubt, keine Kommentare."), reason);
        var forced = AppendedChanges.Apply(doc, Commented(doc), new PdfSaveOptions());
        var broken = Assert.Single(Verify(forced));
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, broken.Outcome);
        Assert.Equal(Tr.T("Das Dokument ist zertifiziert: danach sind nur Ausfüllen und Unterschreiben erlaubt, keine Kommentare und keine leeren Signaturfelder."),
            broken.LaterDetails[0]);
    }

    /// <summary>
    /// An empty signature field added to a document certified for filling and signing: no signing, but a look of its own over the page (the
    /// „sneaky signature“ of https://pdf-insecurity.org) — refused, and forced in anyway it breaks the certification; in an ordinary signed
    /// document it counts like a comment (docs/FEHLERGEDAECHTNIS.md, No. 334).
    /// </summary>
    [Fact]
    public void FormsAndSigning_EmptySignatureFieldRefused_ForcedDetected()
    {
        var certified = Sign(FormFillTests.AllKinds(), Signature(100, CertificationLevel.FormsAndSigning));
        using var doc = PdfDocument.Open(certified);
        var saved = PageItem.From(doc);
        PageItem[] WithField(PdfDocument d) =>
        [
            .. PageItem.From(d).Select((item, i) => i > 0 ? item : item.WithElements([.. item.Elements,
                new SignatureFieldElement { Rect = new PdfRectangle(60, 200, 260, 250), Label = "Unterschrift", FieldName = "Zweite" }])),
        ];
        Assert.False(SignedSaving.AppendsOnly(doc, saved, WithField(doc), out var reason));
        Assert.Equal(Tr.T("Das Dokument ist zertifiziert: danach sind nur Ausfüllen und Unterschreiben erlaubt, keine leeren Signaturfelder."), reason);
        var forced = AppendedChanges.Apply(doc, WithField(doc), new PdfSaveOptions());
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, Assert.Single(Verify(forced)).Outcome);
    }

    [Fact]
    public void NoChanges_NothingAppended_ForcedChangeDetected()
    {
        var certified = Sign(FormFillTests.AllKinds(), Signature(100, CertificationLevel.NoChanges), "keine-aenderungen");
        using var doc = PdfDocument.Open(certified);
        var saved = PageItem.From(doc);
        Assert.False(SignedSaving.AppendsOnly(doc, saved, Filled(doc), out var reason));
        Assert.Equal(Tr.T("Das Dokument ist zertifiziert: danach sind keine Änderungen erlaubt."), reason);
        // not even a further signature
        var signing = saved.Select((item, i) => i > 0 ? item : item.WithElements([.. item.Elements, Signature(200)])).ToArray();
        Assert.False(SignedSaving.AppendsOnly(doc, saved, signing, out reason));
        Assert.NotNull(reason);

        var forced = AppendedChanges.Apply(doc, Filled(doc), new PdfSaveOptions());
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, Assert.Single(Verify(forced)).Outcome);
    }

    [Fact]
    public void FormsSigningAndComments_CommentAppended()
    {
        var certified = Sign(FormFillTests.AllKinds(), Signature(100, CertificationLevel.FormsSigningAndComments), "kommentare");
        using var doc = PdfDocument.Open(certified);
        Assert.True(SignedSaving.AppendsOnly(doc, PageItem.From(doc), Commented(doc)));
        var commented = AppendedChanges.Apply(doc, Commented(doc), new PdfSaveOptions());
        var check = Assert.Single(Verify(commented));
        Assert.Equal(SignatureOutcome.ValidIdentityUnknown, check.Outcome);
        Assert.Equal(LaterChanges.Comments, check.Later);
    }

    [Fact]
    public void LockedFields_FillingRefused_ForcedFillingDetected_FurtherSignatureAllowed()
    {
        var locked = Sign(FormFillTests.AllKinds(), Signature(100, lockFields: true), "gesperrt");
        var check = Assert.Single(Verify(locked));
        Assert.Null(check.Certification);
        // every field of the form at signing, listed (/Include) so that fields added later — a document time stamp, a further signature —
        // are not taken as changes to locked ones; the field's /Lock names the same
        Assert.Equal("Include", check.Lock!.Action);
        Assert.Contains("Name", check.Lock.Fields);
        Assert.DoesNotContain(check.FieldName, check.Lock.Fields);
        using var doc = PdfDocument.Open(locked);
        var all = doc.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!;
        Assert.Equal(IncrementalParts.Walk(doc, all).Select(f => f.Name).Where(n => n != check.FieldName).ToHashSet(), check.Lock.Fields);
        var fieldLock = IncrementalParts.Walk(doc, all).Single(f => f.Name == check.FieldName).Field.GetDictionary(PdfName.Get("Lock"))!;
        Assert.Equal("Include", fieldLock.GetName(PdfName.Get("Action"))!.Value);
        Assert.Equal(check.Lock.Fields.Count, fieldLock.GetArray(PdfName.Get("Fields"))!.Count);
        var saved = PageItem.From(doc);
        Assert.False(SignedSaving.AppendsOnly(doc, saved, Filled(doc), out var reason));
        Assert.Equal(Tr.T("Das Feld „{0}“ ist durch eine Signatur gesperrt.", "Name"), reason);

        var forced = AppendedChanges.Apply(doc, Filled(doc), new PdfSaveOptions());
        var broken = Assert.Single(Verify(forced));
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, broken.Outcome);
        Assert.Contains(Tr.T("Das Feld „{0}“ ist durch diese Signatur gesperrt und wurde danach geändert.", "Name"), broken.LaterDetails);

        // a further signature in a new field does not change a locked field
        var twice = PdfSigner.Sign(doc, 0, Signature(200), Own.Value, DateTimeOffset.Now);
        Assert.All(Verify(twice), c => Assert.Equal(SignatureOutcome.ValidIdentityUnknown, c.Outcome));
    }

    [Fact]
    public void OnlyTheFirstSignatureCertifies()
    {
        var once = Sign(FormFillTests.AllKinds(), Signature(100));
        var twice = Sign(once, Signature(200, CertificationLevel.NoChanges));
        var checks = Verify(twice);
        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.Null(c.Certification));
        using var doc = PdfDocument.Open(twice);
        Assert.Null(doc.Catalog!.GetDictionary(PdfName.Get("Perms")));
        Assert.Null(SignaturePermissions.Of(doc).Certification);
    }

    /// <summary>
    /// Two signatures in one save, the certifying one placed after the other (S7-9): it is signed first, so the document is certified;
    /// before, the other one came first and the certification was silently dropped.
    /// </summary>
    [Fact]
    public void TwoPending_CertifyingSignedFirst()
    {
        using var doc = PdfDocument.Open(FormFillTests.AllKinds());
        var pages = PageItem.From(doc);
        var edited = new[] { pages[0].WithElements([.. pages[0].Elements, Signature(100), Signature(200, CertificationLevel.FormsAndSigning)]) };
        var path = Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"zwei-{Guid.NewGuid():N}.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SignedSaving.Save(doc, edited, path, new PdfSaveOptions(), appendOnly: false, Own.Value, DateTimeOffset.Now);
        var checks = Verify(File.ReadAllBytes(path));
        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.Equal(SignatureOutcome.ValidIdentityUnknown, c.Outcome));
        Assert.Single(checks, c => c.Certification == CertificationLevel.FormsAndSigning);
    }
    /// <summary>
    /// The PAdES extension in a file before PDF 2.0 (docs/FEHLERGEDAECHTNIS.md, No. 364): signing declares it in the catalog (ESIC, level
    /// 2 for ETSI.CAdES.detached), once; a second signature finds it declared and does not change the catalog for it, and the first
    /// signature still shows only allowed changes after it.
    /// </summary>
    [Fact]
    public void SigningAPdf17File_DeclaresThePadesExtension_SecondSignatureKeepsTheFirstValid()
    {
        var once = Sign(TestPdf.Simple(1).Build(), Signature(600));
        using (var doc = PdfDocument.Open(once))
        {
            var esic = doc.Catalog!.GetDictionary(PdfName.Get("Extensions"))?.GetDictionary(PdfName.Get("ESIC"));
            Assert.NotNull(esic);
            Assert.Equal("1.7", esic!.GetName(PdfName.Get("BaseVersion"))?.Value);
            Assert.Equal(2, esic.Get(PdfName.Get("ExtensionLevel"))?.AsNumber());
        }
        var twice = Sign(once, Signature(450));
        var checks = Verify(twice);
        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.Equal(SignatureOutcome.ValidIdentityUnknown, c.Outcome));   // the own certificate: the signature fits, the identity is unconfirmed
        Assert.All(checks, c => Assert.True(c.Later is LaterChanges.None or LaterChanges.FormsAndSignatures, $"{c.FieldName}: {c.Later} {string.Join("; ", c.LaterDetails)}"));
    }

    /// <summary>
    /// A certified file written anew (after „Trotzdem speichern“): /Perms goes — /DocMDP (and /UR3) named a signature over bytes the copy no
    /// longer has (ISO 32000-2, 12.8.2.3, 12.8.4; docs/ABLAUFPLAN.md, 4.4 d; docs/FEHLERGEDAECHTNIS.md, No. 539). Appending (signing,
    /// filling in) keeps it — the other tests of this class.
    /// </summary>
    [Fact]
    public void RewrittenCertifiedFile_PermsDropped()
    {
        var certified = Sign(TestPdf.Simple(1).Build(), Signature(100, CertificationLevel.FormsAndSigning));
        byte[] rewritten;
        using (var doc = PdfDocument.Open(certified))
        {
            Assert.NotNull(doc.Catalog!.GetDictionary(PdfName.Get("Perms")));
            using var output = new MemoryStream();
            PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions());
            rewritten = output.ToArray();
        }
        using var copy = PdfDocument.Open(rewritten);
        Assert.False(copy.Catalog!.ContainsKey(PdfName.Get("Perms")));
    }

    /// <summary>
    /// A file certified with „no changes“, written anew with a new signature, then signed once more: the certification is broken (its byte
    /// ranges no longer fit) and restricts nothing — appending allowed it, and the check judged the new signatures by it all the same:
    /// „zertifiziert: danach sind keine Änderungen erlaubt“ (found with the rundlauf over the signature test files; docs/FEHLERGEDAECHTNIS.md,
    /// No. 550).
    /// </summary>
    [Fact]
    public void BrokenCertification_RestrictsNothingAfterwards()
    {
        var certified = Sign(TestPdf.Simple(1).Build(), Signature(100, CertificationLevel.NoChanges));
        byte[] rewritten;
        using (var doc = PdfDocument.Open(certified))
        {
            using var output = new MemoryStream();
            PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions());
            rewritten = output.ToArray();
        }
        var once = Sign(rewritten, Signature(300));
        using (var signed = PdfDocument.Open(once))
            Assert.True(SignedSaving.AppendsOnly(signed, PageItem.From(signed), PageItem.From(signed)));
        var twice = Sign(once, Signature(500));
        var checks = Verify(twice);
        Assert.Equal(3, checks.Count);
        var ours = checks.Where(c => c.Outcome == SignatureOutcome.ValidIdentityUnknown).ToList();
        Assert.Equal(2, ours.Count);
        // still shown as the certification it was
        Assert.Contains(checks, c => c.Certification == CertificationLevel.NoChanges && c.Outcome != SignatureOutcome.ValidIdentityUnknown);
        Assert.All(ours, c => Assert.True(c.Later is LaterChanges.None or LaterChanges.FormsAndSignatures, $"{c.FieldName}: {c.Later} {string.Join("; ", c.LaterDetails)}"));
    }
}
