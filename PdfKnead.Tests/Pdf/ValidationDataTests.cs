using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Validation data and document time stamps after a signature (docs/ABLAUFPLAN.md, 2.1; ISO 32000-2, 12.8.2.2.1 with Table 257, 12.8.5.3,
/// Tables 261/262): appended revisions that only hold the data of the security store (/DSS, /VRI) or an invisible document time stamp are no
/// change at any level of certification, also „keine Änderungen“ (P = 1), and also when another program extends an existing store under its
/// number. Everything beyond — a visible „time stamp“ field, one with an action, a store of another shape, a stream of the store replaced, a
/// list of the store that is also the content of a page, the form's /NeedAppearances, the document information — stays a change, at P = 1
/// with its reason named.
/// </summary>
public sealed partial class ValidationDataTests
{
    private static readonly Lazy<SigningCertificate> Own = new(() =>
        SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"pruefdaten-{Guid.NewGuid():N}.p12")));

    private static readonly PdfName Dss = PdfName.Get("DSS");

    private static InkElement Signature(CertificationLevel? certify = null) => new()
    {
        Rect = new PdfRectangle(50, 100, 250, 180),
        Strokes = [[0, 20, 40, 70, 90, 10, 140, 60]],
        StrokeWidth = 3,
        Digital = new DigitalSigning("Max Mustermann", Certify: certify),
    };

    private static string TempPath() => Path.Combine(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pdf-knead-tests")).FullName, $"pruefdaten-{Guid.NewGuid():N}.pdf");

    /// <summary>Signed (appended to <paramref name="source"/>) with the own certificate, without validation data.</summary>
    private static byte[] Signed(byte[] source, CertificationLevel? certify)
    {
        using var doc = PdfDocument.Open(source);
        return PdfSigner.Sign(doc, 0, Signature(certify), Own.Value, DateTimeOffset.Now);
    }

    /// <summary>Signed with long-term validation data (B-LT), and with <paramref name="authority"/> a document time stamp after it (B-LTA).</summary>
    private static byte[] SignedLongTerm(LongTermValidationTests.TestCa ca, CertificationLevel? certify, SignatureTimestampTests.TestAuthority? authority = null,
        byte[]? source = null)
    {
        using var doc = PdfDocument.Open(source ?? TestPdf.Simple(1).Build());
        var pages = PageItem.From(doc);
        var path = TempPath();
        SignedSaving.Save(doc, [pages[0].WithElements([.. pages[0].Elements, Signature(certify)]), .. pages.Skip(1)], path, new PdfSaveOptions(), false, ca.Signer,
            DateTimeOffset.Now, timestamps: authority, revocation: ca, archive: authority is not null);
        var file = File.ReadAllBytes(path);
        File.Delete(path);
        return file;
    }

    private static IReadOnlyList<SignatureCheck> Verify(byte[] file, LongTermValidationTests.TestCa? ca = null)
    {
        using var doc = PdfDocument.Open(file);
        return SignatureVerifier.Verify(doc, null, ca is null ? [] : [X509CertificateLoader.LoadCertificate(ca.Root.RawData)], CancellationToken.None);
    }

    /// <summary>The check of the (first) signature that is no document time stamp.</summary>
    private static SignatureCheck SignatureOf(byte[] file, LongTermValidationTests.TestCa? ca = null) =>
        Verify(file, ca).First(c => !c.IsDocumentTimestamp && c.SignerName is { Length: > 0 });

    /// <summary>Appends a revision made by <paramref name="change"/>.</summary>
    private static byte[] Append(byte[] file, Action<PdfDocument, IncrementalUpdate> change)
    {
        using var doc = PdfDocument.Open(file);
        var update = new IncrementalUpdate(doc);
        change(doc, update);
        using var output = new MemoryStream();
        update.WriteTo(output);
        return output.ToArray();
    }

    private static PdfDictionary Copy(PdfDictionary dict)
    {
        var copy = new PdfDictionary();
        foreach (var (key, value) in dict.Entries) copy.Set(key, value);
        return copy;
    }

    /// <summary>
    /// A file whose security store lies in a signed revision, as another program meets it: a first signature with validation data (B-LT,
    /// the store appended after it), then a second signature appended — its revision holds the store.
    /// </summary>
    private static byte[] StoreThenSigned(LongTermValidationTests.TestCa ca)
    {
        using var doc = PdfDocument.Open(SignedLongTerm(ca, null));
        return PdfSigner.Sign(doc, 0, Signature() with { Rect = new PdfRectangle(300, 100, 500, 180) }, Own.Value, DateTimeOffset.Now);
    }

    /// <summary>The check of the latest signature (no document time stamp): the one whose revision holds the store in <see cref="StoreThenSigned"/>.</summary>
    private static SignatureCheck LatestSignatureOf(byte[] file, LongTermValidationTests.TestCa ca) =>
        Verify(file, ca).Where(c => !c.IsDocumentTimestamp).OrderBy(c => c.SigningTime).ThenBy(c => c.CoversWholeFile).Last();

    /// <summary>
    /// What another program does when it adds validation data: the existing store replaced under its own number, with one certificate more
    /// (a new stream) and a /VRI entry for the signature; with <paramref name="tuAndTs"/> the entry also has /TU (a date) and /TS (a stream).
    /// </summary>
    private static void ExtendStoreInPlace(PdfDocument doc, IncrementalUpdate update, bool tuAndTs = false, Action<PdfDictionary>? more = null)
    {
        var dssRef = (PdfReference)doc.Catalog!.GetRaw(Dss)!;
        var dss = Copy((PdfDictionary)doc.Resolve(dssRef.Id)!);
        var certs = new PdfArray();
        if (dss.GetArray(PdfName.Get("Certs")) is { } old)
            for (var i = 0; i < old.Count; i++) certs.Add(old[i]);
        var extra = update.Add(new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes("Zertifikat einer Zwischenstelle (Platzhalter)")));
        certs.Add(extra);
        dss.Set(PdfName.Get("Certs"), certs);
        var entry = TestPdf.Dict(("Type", TestPdf.N("VRI")), ("Cert", new PdfArray([extra])));
        if (tuAndTs)
        {
            entry.Set(PdfName.Get("TU"), new PdfString("D:20261105120000+01'00'"u8.ToArray()));
            entry.Set(PdfName.Get("TS"), update.Add(new PdfStream(new PdfDictionary(), "Zeitstempel (Platzhalter)"u8.ToArray())));
        }
        dss.Set(PdfName.Get("VRI"), TestPdf.Dict(("0123456789ABCDEF0123456789ABCDEF01234567", entry)));
        more?.Invoke(dss);
        update.Replace(dssRef.Id, dss);
    }

    // ------------------------------------------------------------ allowed: validation data and invisible document time stamps

    [Fact]
    public void CertifiedNoChanges_Archived_StaysValid()
    {
        using var ca = new LongTermValidationTests.TestCa();
        using var authority = new SignatureTimestampTests.TestAuthority();
        var checks = Verify(SignedLongTerm(ca, CertificationLevel.NoChanges, authority, FormFillTests.AllKinds()), ca);
        var signature = Assert.Single(checks, c => !c.IsDocumentTimestamp);
        Assert.True(SignatureTexts.IsValid(signature), string.Join(" | ", signature.LaterDetails));
        Assert.Equal(CertificationLevel.NoChanges, signature.Certification);
        Assert.Equal(LaterChanges.ValidationData, signature.Later);
        var stamp = Assert.Single(checks, c => c.IsDocumentTimestamp);
        Assert.True(SignatureTexts.IsValid(stamp), stamp.Problem);
    }

    [Fact]
    public void CertifiedNoChanges_Renewed_AllStayValid()
    {
        using var ca = new LongTermValidationTests.TestCa();
        using var authority = new SignatureTimestampTests.TestAuthority();
        var once = SignedLongTerm(ca, CertificationLevel.NoChanges, authority);
        using var doc = PdfDocument.Open(once);
        var path = TempPath();
        SignedSaving.AddArchiveTimestamp(doc, path, authority, ca);
        var twice = File.ReadAllBytes(path);
        File.Delete(path);
        var checks = Verify(twice, ca);
        Assert.Equal(3, checks.Count);
        Assert.All(checks, c => Assert.True(SignatureTexts.IsValid(c), c.FieldName + ": " + string.Join(" | ", c.LaterDetails)));
        Assert.All(checks.Where(c => !c.CoversWholeFile), c => Assert.Equal(LaterChanges.ValidationData, c.Later));
    }

    [Fact]
    public void CertifiedNoChanges_LongTerm_StaysValid()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var check = SignatureOf(SignedLongTerm(ca, CertificationLevel.NoChanges), ca);
        Assert.True(SignatureTexts.IsValid(check), string.Join(" | ", check.LaterDetails));
        Assert.Equal(LaterChanges.ValidationData, check.Later);
        Assert.Equal(Tr.T("Danach nur Prüfdaten für die Langzeitprüfung oder Dokument-Zeitstempel hinzugefügt."), SignatureTexts.Later(check));
    }

    /// <summary>
    /// Another program extends the store under its number after a signature whose revision holds it: no change (it was „Ein Objekt der Art
    /// „DSS“ wurde geändert“ and red, for every signature before).
    /// </summary>
    [Fact]
    public void DssExtendedInPlace_ByAnotherProgram_StaysValid()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var file = Append(StoreThenSigned(ca), (doc, update) => ExtendStoreInPlace(doc, update));
        var check = LatestSignatureOf(file, ca);
        Assert.True(SignatureTexts.IsValid(check), string.Join(" | ", check.LaterDetails));
        Assert.Equal(LaterChanges.ValidationData, check.Later);
        Assert.All(Verify(file, ca), c => Assert.True(SignatureTexts.IsValid(c), c.FieldName + ": " + string.Join(" | ", c.LaterDetails)));
    }

    /// <summary>
    /// Certified „no changes“ with validation data, a document time stamp appended (its revision holds the store), then the store extended in
    /// place: the certification, the signature and the time stamp all stay valid.
    /// </summary>
    [Fact]
    public void CertifiedNoChanges_StampThenDssExtendedInPlace_AllStayValid()
    {
        using var ca = new LongTermValidationTests.TestCa();
        using var authority = new SignatureTimestampTests.TestAuthority();
        byte[] stamped;
        using (var doc = PdfDocument.Open(SignedLongTerm(ca, CertificationLevel.NoChanges)))
        {
            var stamp = PdfSigner.DocumentTimestampUpdate(doc, authority);
            stamped = [.. doc.FileBytes(0, doc.FileSize), .. stamp];
        }
        var checks = Verify(Append(stamped, (doc, update) => ExtendStoreInPlace(doc, update)), ca);
        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.True(SignatureTexts.IsValid(c), c.FieldName + ": " + string.Join(" | ", c.LaterDetails)));
        Assert.All(checks, c => Assert.Equal(LaterChanges.ValidationData, c.Later));
    }

    [Fact]
    public void VriWithTuAndTs_StaysValidationData()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var check = LatestSignatureOf(Append(StoreThenSigned(ca), (doc, update) => ExtendStoreInPlace(doc, update, tuAndTs: true)), ca);
        Assert.True(SignatureTexts.IsValid(check), string.Join(" | ", check.LaterDetails));
        Assert.Equal(LaterChanges.ValidationData, check.Later);
    }

    // ------------------------------------------------------------ still a change

    [Fact]
    public void DssWithUnknownKey_CountsAsChange()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var file = Append(StoreThenSigned(ca), (doc, update) => ExtendStoreInPlace(doc, update, more: dss => dss.Set(PdfName.Get("Fremd"), PdfInteger.Of(1))));
        var check = LatestSignatureOf(file, ca);
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, check.Outcome);
        Assert.Contains(Tr.T("Der Sicherheitsspeicher (/DSS) hat eine unbekannte Form."), check.LaterDetails);
    }

    [Fact]
    public void CertifiedNoChanges_InfoChanged_ReasonNamed()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var file = Append(SignedLongTerm(ca, CertificationLevel.NoChanges), (doc, update) =>
        {
            ExtendStoreInPlace(doc, update);
            var infoRef = (PdfReference)doc.Trailer.GetRaw(PdfName.Info)!;
            var info = Copy((PdfDictionary)doc.Resolve(infoRef.Id)!);
            info.Set(PdfName.Get("Title"), new PdfString("Anderer Titel"u8.ToArray()));
            update.Replace(infoRef.Id, info);
        });
        var check = SignatureOf(file, ca);
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, check.Outcome);
        Assert.Contains(Tr.T("Danach wurden die Dokumenteigenschaften (Titel, Autor, Datum …) geändert."), check.LaterDetails);
    }

    /// <summary>
    /// The document's XMP metadata updated after signing, as saving programs do (the reference counts it harmless): like the document
    /// information — filling in, the signature stays valid; a certification „no changes“ forbids it, the reason named. Both ways: the stream
    /// replaced under its number, and a new stream set in the catalog (docs/FEHLERGEDAECHTNIS.md, No. 477).
    /// </summary>
    [Fact]
    public void MetadataChanged_LikeInfo()
    {
        using var ca = new LongTermValidationTests.TestCa();
        static PdfStream Xmp(string title) => new(TestPdf.Dict(("Type", TestPdf.N("Metadata")), ("Subtype", TestPdf.N("XML"))),
            Encoding.UTF8.GetBytes($"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><dc:title>{title}</dc:title></x:xmpmeta>"));
        void SetMetadata(PdfDocument doc, IncrementalUpdate update)
        {
            var rootRef = (PdfReference)doc.Trailer.GetRaw(PdfName.Root)!;
            var catalog = Copy((PdfDictionary)doc.Resolve(rootRef.Id)!);
            catalog.Set(PdfName.Metadata, update.Add(Xmp("Anderer Titel")));
            update.Replace(rootRef.Id, catalog);
        }
        var message = Tr.T("Danach wurden die Metadaten des Dokuments (XMP) geändert.");
        // a new stream set in the catalog
        var added = SignatureOf(Append(SignedLongTerm(ca, null), SetMetadata), ca);
        Assert.True(SignatureTexts.IsValid(added), string.Join(" | ", added.LaterDetails));
        Assert.Equal(LaterChanges.FormsAndSignatures, added.Later);   // allowed changes are summed up, not listed
        // the stream of the signed revision replaced under its number
        var source = Append(TestPdf.Simple(1).Build(), SetMetadata);
        var replaced = SignatureOf(Append(SignedLongTerm(ca, null, source: source), (doc, update) =>
        {
            var catalog = (PdfDictionary)doc.Resolve(((PdfReference)doc.Trailer.GetRaw(PdfName.Root)!).Id)!;
            var metadataRef = Assert.IsType<PdfReference>(catalog.GetRaw(PdfName.Metadata));
            update.Replace(metadataRef.Id, Xmp("Noch ein Titel"));
        }), ca);
        Assert.True(SignatureTexts.IsValid(replaced), string.Join(" | ", replaced.LaterDetails));
        Assert.Equal(LaterChanges.FormsAndSignatures, replaced.Later);
        // certified „no changes“: forbidden, the reason named
        var certified = SignatureOf(Append(SignedLongTerm(ca, CertificationLevel.NoChanges), SetMetadata), ca);
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, certified.Outcome);
        Assert.Contains(message, certified.LaterDetails);
    }

    /// <summary>A new widget added to page 1 and to the form's list of fields (the shape of a time stamp field, or whatever a test makes of it).</summary>
    private static void AddWidget(PdfDocument doc, IncrementalUpdate update, PdfDictionary widget)
    {
        var page = doc.Pages[0];
        widget.Set(PdfName.Get("P"), new PdfReference(page.Id!.Value));
        var widgetRef = update.Add(widget);
        var annots = new PdfArray();
        if (page.Dictionary.GetArray(PdfName.Get("Annots")) is { } old)
            for (var i = 0; i < old.Count; i++) annots.Add(old[i]);
        annots.Add(widgetRef);
        if (page.Dictionary.GetRaw(PdfName.Get("Annots")) is PdfReference annotsRef) update.Replace(annotsRef.Id, annots);
        else
        {
            var pageDict = Copy(page.Dictionary);
            pageDict.Set(PdfName.Get("Annots"), annots);
            update.Replace(page.Id!.Value, pageDict);
        }
        var catalog = Copy(doc.Catalog!);
        var formRef = catalog.GetRaw(PdfName.Get("AcroForm")) as PdfReference;
        var form = Copy(catalog.GetDictionary(PdfName.Get("AcroForm")) ?? new PdfDictionary());
        var fields = new PdfArray();
        if (form.GetArray(PdfName.Get("Fields")) is { } oldFields)
            for (var i = 0; i < oldFields.Count; i++) fields.Add(oldFields[i]);
        fields.Add(widgetRef);
        if (form.GetRaw(PdfName.Get("Fields")) is PdfReference fieldsRef) update.Replace(fieldsRef.Id, fields);
        else form.Set(PdfName.Get("Fields"), fields);
        if (formRef is not null) update.Replace(formRef.Id, form);
        else
        {
            catalog.Set(PdfName.Get("AcroForm"), form);
            update.Replace(((PdfReference)doc.Trailer.GetRaw(PdfName.Root)!).Id, catalog);
        }
    }

    /// <summary>A widget that calls itself a document time stamp: /FT /Sig, a value of /Type /DocTimeStamp with /SubFilter /ETSI.RFC3161.</summary>
    private static PdfDictionary StampLikeWidget(IncrementalUpdate update, double width, double height) => TestPdf.Dict(("Type", TestPdf.N("Annot")),
        ("Subtype", TestPdf.N("Widget")), ("FT", TestPdf.N("Sig")), ("T", new PdfString("Zeitstempel"u8.ToArray())), ("F", PdfInteger.Of(132)),
        ("Rect", TestPdf.Box(100, 600, 100 + width, 600 + height)),
        ("V", update.Add(TestPdf.Dict(("Type", TestPdf.N("DocTimeStamp")), ("Filter", TestPdf.N("Adobe.PPKLite")), ("SubFilter", TestPdf.N("ETSI.RFC3161"))))));

    /// <summary>
    /// A „time stamp“ field that can be seen — 100 × 40 with a look naming someone — is no validation data: at P = 1 a change (the shadow
    /// attack the exception must not open).
    /// </summary>
    [Fact]
    public void CertifiedNoChanges_VisibleStampField_StaysDetected()
    {
        var file = Append(Signed(TestPdf.Simple(1).Build(), CertificationLevel.NoChanges), (doc, update) =>
        {
            var widget = StampLikeWidget(update, 100, 40);
            widget.Set(PdfName.Get("F"), PdfInteger.Of(4));
            var look = update.Add(TestPdf.FlateStream("BT /Helv 10 Tf 2 4 Td (Erika Mustermann) Tj ET"u8.ToArray(),
                TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 100, 40)))));
            widget.Set(PdfName.Get("AP"), TestPdf.Dict(("N", look)));
            AddWidget(doc, update, widget);
        });
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, SignatureOf(file).Outcome);
    }

    [Fact]
    public void CertifiedNoChanges_StampFieldWithAction_StaysDetected()
    {
        var file = Append(Signed(TestPdf.Simple(1).Build(), CertificationLevel.NoChanges), (doc, update) =>
        {
            var widget = StampLikeWidget(update, 0, 0);
            widget.Set(PdfName.Get("AA"), TestPdf.Dict(("PO", TestPdf.Dict(("S", TestPdf.N("JavaScript")), ("JS", new PdfString("app.alert(1)"u8.ToArray()))))));
            AddWidget(doc, update, widget);
        });
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, SignatureOf(file).Outcome);
    }

    /// <summary>
    /// A prepared file whose page content is an indirect array that the security store also lists as /Certs: extending „the store's list“
    /// adds a content stream to the page — reachable from the pages, it is no validation data.
    /// </summary>
    [Fact]
    public void CertifiedNoChanges_DssListAlsoPageContents_Detected()
    {
        var prepared = Append(TestPdf.Simple(1).Build(), (doc, update) =>
        {
            var page = Copy(doc.Pages[0].Dictionary);
            var contents = page.GetRaw(PdfName.Contents)!;
            var shared = update.Add(new PdfArray([contents]));
            page.Set(PdfName.Contents, shared);
            update.Replace(doc.Pages[0].Id!.Value, page);
            var catalog = Copy(doc.Catalog!);
            catalog.Set(Dss, update.Add(TestPdf.Dict(("Type", TestPdf.N("DSS")), ("Certs", shared))));
            update.Replace(((PdfReference)doc.Trailer.GetRaw(PdfName.Root)!).Id, catalog);
        });
        var file = Append(Signed(prepared, CertificationLevel.NoChanges), (doc, update) =>
        {
            var shared = (PdfReference)doc.Pages[0].Dictionary.GetRaw(PdfName.Contents)!;
            var list = (PdfArray)doc.Resolve(shared.Id)!;
            update.Replace(shared.Id, new PdfArray([list[0], update.Add(TestPdf.FlateStream("BT /F1 24 Tf 72 400 Td (Erika Mustermann) Tj ET"u8.ToArray()))]));
        });
        var check = SignatureOf(file);
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, check.Outcome);
        Assert.Equal(LaterChanges.Other, check.Later);
    }

    [Fact]
    public void CertifiedNoChanges_DssPlusNeedAppearances_Detected()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var file = Append(SignedLongTerm(ca, CertificationLevel.NoChanges, source: FormFillTests.AllKinds()), (doc, update) =>
        {
            ExtendStoreInPlace(doc, update);
            var catalog = Copy(doc.Catalog!);
            var form = Copy(catalog.GetDictionary(PdfName.Get("AcroForm"))!);
            form.Set(PdfName.Get("NeedAppearances"), PdfBoolean.True);
            if (catalog.GetRaw(PdfName.Get("AcroForm")) is PdfReference formRef) update.Replace(formRef.Id, form);
            else
            {
                catalog.Set(PdfName.Get("AcroForm"), form);
                update.Replace(((PdfReference)doc.Trailer.GetRaw(PdfName.Root)!).Id, catalog);
            }
        });
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, SignatureOf(file, ca).Outcome);
    }

    [Fact]
    public void DssStreamReplacedInPlace_Detected()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var file = Append(StoreThenSigned(ca), (doc, update) =>
        {
            var dss = (PdfDictionary)doc.Resolve(((PdfReference)doc.Catalog!.GetRaw(Dss)!).Id)!;
            var first = (PdfReference)dss.GetArray(PdfName.Get("Certs"))![0];
            update.Replace(first.Id, new PdfStream(new PdfDictionary(), "anderes Zertifikat"u8.ToArray()));
        });
        var check = LatestSignatureOf(file, ca);
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, check.Outcome);
        Assert.Equal(LaterChanges.Other, check.Later);
    }
}
