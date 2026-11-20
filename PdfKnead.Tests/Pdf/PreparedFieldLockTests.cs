using System.Security.Cryptography.Pkcs;
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
/// The lock a form's author put into a prepared signature field (/Lock, ISO 32000-2:2020, Table 236; docs/ABLAUFPLAN.md, 2.3): copied word
/// for word into the signature's FieldMDP (12.8.2.4), also /All; never overwritten by „lock fields“; an own lock written as an indirect
/// object (Table 235). When checking, the field's /Lock holds also when a signature (of another program) does not carry it over, with a
/// hint; its /P acts like a certification of this signature (erratum EC3 #131: validation data and document time stamps stay allowed).
/// </summary>
public sealed class PreparedFieldLockTests
{
    private static readonly Lazy<SigningCertificate> Own = new(() =>
        SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"feldsperre-{Guid.NewGuid():N}.p12")));

    private static PdfDictionary Copy(PdfDictionary dict)
    {
        var copy = new PdfDictionary();
        foreach (var (key, value) in dict.Entries) copy.Set(key, value);
        return copy;
    }

    internal static PdfArray Names(params string[] names) => new(names.Select(n => (PdfObject)new PdfString(Encoding.ASCII.GetBytes(n))));

    /// <summary>The form of <see cref="FormFillTests.AllKinds"/> with a prepared, empty signature field „Kunde“, its /Lock as given (indirect).</summary>
    internal static byte[] Prepared(PdfDictionary? lockDict, PdfDictionary? seedValues = null)
    {
        using var doc = PdfDocument.Open(FormFillTests.AllKinds());
        var update = new IncrementalUpdate(doc);
        var page = doc.Pages[0];
        var field = TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("FT", TestPdf.N("Sig")),
            ("T", new PdfString("Kunde"u8.ToArray())), ("Rect", TestPdf.Box(40, 40, 560, 300)), ("F", PdfInteger.Of(4)), ("P", new PdfReference(page.Id!.Value)));
        if (lockDict is not null) field.Set(PdfName.Get("Lock"), update.Add(lockDict));
        if (seedValues is not null) field.Set(PdfName.Get("SV"), update.Add(seedValues));
        var fieldRef = update.Add(field);
        var annots = new PdfArray();
        if (page.Dictionary.GetArray(PdfName.Get("Annots")) is { } old)
            for (var i = 0; i < old.Count; i++) annots.Add(old[i]);
        annots.Add(fieldRef);
        if (page.Dictionary.GetRaw(PdfName.Get("Annots")) is PdfReference annotsRef) update.Replace(annotsRef.Id, annots);
        else
        {
            var pageDict = Copy(page.Dictionary);
            pageDict.Set(PdfName.Get("Annots"), annots);
            update.Replace(page.Id!.Value, pageDict);
        }
        var catalog = Copy(doc.Catalog!);
        var formRef = catalog.GetRaw(PdfName.Get("AcroForm")) as PdfReference;
        var form = Copy(catalog.GetDictionary(PdfName.Get("AcroForm"))!);
        var fields = new PdfArray();
        var oldFields = form.GetArray(PdfName.Get("Fields"))!;
        for (var i = 0; i < oldFields.Count; i++) fields.Add(oldFields[i]);
        fields.Add(fieldRef);
        if (form.GetRaw(PdfName.Get("Fields")) is PdfReference fieldsRef) update.Replace(fieldsRef.Id, fields);
        else form.Set(PdfName.Get("Fields"), fields);
        if (formRef is not null) update.Replace(formRef.Id, form);
        else
        {
            catalog.Set(PdfName.Get("AcroForm"), form);
            update.Replace(((PdfReference)doc.Trailer.GetRaw(PdfName.Root)!).Id, catalog);
        }
        using var output = new MemoryStream();
        update.WriteTo(output);
        return output.ToArray();
    }

    internal static PdfDictionary Lock(string action, PdfArray? fields = null, int? p = null)
    {
        var lockDict = TestPdf.Dict(("Type", TestPdf.N("SigFieldLock")), ("Action", TestPdf.N(action)));
        if (fields is not null) lockDict.Set(PdfName.Get("Fields"), fields);
        if (p is { } level) lockDict.Set(PdfName.Get("P"), PdfInteger.Of(level));
        return lockDict;
    }

    private static byte[] Sign(byte[] file, string? fieldName = "Kunde", bool lockFields = false, CertificationLevel? certify = null)
    {
        using var doc = PdfDocument.Open(file);
        return PdfSigner.Sign(doc, 0, new InkElement
        {
            Rect = new PdfRectangle(100, 100, 300, 180), Strokes = [[0, 20, 40, 70, 90, 10]], StrokeWidth = 3,
            Digital = new DigitalSigning("Max Mustermann", FieldName: fieldName, Certify: certify, LockFields: lockFields),
        }, Own.Value, DateTimeOffset.Now);
    }

    /// <summary>A text field filled in afterwards (appended).</summary>
    private static byte[] Fill(byte[] file, string name, string value)
    {
        using var doc = PdfDocument.Open(file);
        var update = new IncrementalUpdate(doc);
        var (id, field, _) = IncrementalParts.Walk(doc, doc.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!).First(f => f.Name == name);
        var filled = Copy(field);
        filled.Set(PdfName.Get("V"), new PdfString(Encoding.ASCII.GetBytes(value)));
        update.Replace(id, filled);
        using var output = new MemoryStream();
        update.WriteTo(output);
        return output.ToArray();
    }

    private static SignatureCheck CheckOf(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        return SignatureVerifier.Verify(doc, null, [], CancellationToken.None).Single(c => c.FieldName == "Kunde");
    }

    private static PdfDictionary FieldMdp(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        var sig = DigitalSignatures.Find(doc).Single().Signature!;
        return SignaturePermissions.Reference(sig, "FieldMDP")!;
    }

    // ------------------------------------------------------------ signing into a prepared field

    [Fact]
    public void PreparedFieldWithLock_Signed_LockBecomesFieldMdp()
    {
        var signed = Sign(Prepared(Lock("Include", Names("Name"))));
        var check = CheckOf(signed);
        Assert.True(SignatureTexts.IsValid(check), check.Problem);
        Assert.Equal("Include", check.Lock!.Action);
        Assert.Equal(["Name"], check.Lock.Fields);
        Assert.DoesNotContain(check.Warnings, w => w.Contains("Feldsperre"));
        // filling „Name“ is refused when appending, and breaks the signature when forced; „PLZ“ is free
        using (var doc = PdfDocument.Open(signed))
            Assert.True(SignaturePermissions.Of(doc).IsLocked("Name"));
        var broken = CheckOf(Fill(signed, "Name", "Erika Mustermann"));
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, broken.Outcome);
        Assert.Contains(Tr.T("Das Feld „{0}“ ist durch diese Signatur gesperrt und wurde danach geändert.", "Name"), broken.LaterDetails);
        Assert.True(SignatureTexts.IsValid(CheckOf(Fill(signed, "PLZ", "12345"))));
    }

    [Fact]
    public void PreparedFieldWithAuthorLockAll_CopiedVerbatim()
    {
        var fieldMdp = FieldMdp(Sign(Prepared(Lock("All"))));
        Assert.Equal("All", fieldMdp.GetName(PdfName.Get("Action"))!.Value);
        Assert.False(fieldMdp.ContainsKey(PdfName.Get("Fields")));
    }

    /// <summary>„Lock fields“ ticked, the author asks for /Exclude [(Bemerkung)]: the author's lock is kept on the field and copied, not widened.</summary>
    [Fact]
    public void PreparedFieldWithExcludeLock_AuthorsLockKept()
    {
        var signed = Sign(Prepared(Lock("Exclude", Names("Bemerkung"))), lockFields: true);
        var fieldMdp = FieldMdp(signed);
        Assert.Equal("Exclude", fieldMdp.GetName(PdfName.Get("Action"))!.Value);
        Assert.Equal("Bemerkung", Assert.Single(fieldMdp.GetArray(PdfName.Get("Fields"))!.OfType<PdfString>()).ToText());
        using var doc = PdfDocument.Open(signed);
        var field = IncrementalParts.Walk(doc, doc.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!).First(f => f.Name == "Kunde").Field;
        Assert.Equal("Exclude", field.GetDictionary(PdfName.Get("Lock"))!.GetName(PdfName.Get("Action"))!.Value);
    }

    [Fact]
    public void OwnFieldLock_WrittenIndirect()
    {
        var signed = Sign(FormFillTests.AllKinds(), fieldName: null, lockFields: true);
        using var doc = PdfDocument.Open(signed);
        var field = DigitalSignatures.Find(doc).Single().Field!;
        Assert.IsType<PdfReference>(field.GetRaw(PdfName.Get("Lock")));
    }

    // ------------------------------------------------------------ checking

    /// <summary>
    /// A signature of another program into a field with /Lock that does not carry it over (no FieldMDP): changing a field the /Lock names is
    /// a change all the same, with a hint that the signature lacks the lock.
    /// </summary>
    [Fact]
    public void ForeignSignature_FieldLockWithoutFieldMdp_HonouredWithHint()
    {
        var signed = Sign(Prepared(Lock("Include", Names("Name"))));
        // the /Reference of the signature blanked (same length), the block made anew over the bytes as they are now
        var text = Encoding.Latin1.GetString(signed);
        var at = text.LastIndexOf("/Reference", StringComparison.Ordinal);
        var end = text.IndexOf('[', at);
        for (var depth = 0; ; end++)
        {
            if (text[end] == '[') depth++;
            else if (text[end] == ']' && --depth == 0) break;
        }
        Array.Fill(signed, (byte)' ', at, end + 1 - at);
        var foreign = Reblocked(signed);
        var check = CheckOf(foreign);
        Assert.True(SignatureTexts.IsValid(check), check.Problem);
        Assert.Null(check.Lock);
        Assert.Contains(Tr.T("Das Signaturfeld sieht eine Feldsperre vor, die die Signatur nicht enthält."), check.Warnings);
        var broken = CheckOf(Fill(foreign, "Name", "Erika Mustermann"));
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, broken.Outcome);
    }

    /// <summary>The block of the file's signature made anew over its bytes (with the binding of the certificate, as PdfSigner writes it).</summary>
    private static byte[] Reblocked(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        var range = DigitalSignatures.Find(doc).Single().ByteRange;
        byte[] content = [.. file.AsSpan(0, (int)range[1]), .. file.AsSpan((int)range[2], (int)range[3])];
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, Own.Value.Certificate)
            { DigestAlgorithm = new System.Security.Cryptography.Oid("2.16.840.1.101.3.4.2.1"), IncludeOption = X509IncludeOption.EndCertOnly };
        signer.SignedAttributes.Add(new System.Security.Cryptography.AsnEncodedData("1.2.840.113549.1.9.16.2.47", CmsSignature.SigningCertificateV2Value(Own.Value.Certificate)));
        cms.ComputeSignature(signer, silent: true);
        var hex = Encoding.ASCII.GetBytes(Convert.ToHexString(cms.Encode()));
        var gap = (int)(range[2] - range[1] - 2);
        var copy = (byte[])file.Clone();
        Array.Fill(copy, (byte)'0', (int)range[1] + 1, gap);
        hex.CopyTo(copy, (int)range[1] + 1);
        return copy;
    }

    /// <summary>/Lock with /P 1 (PDF 2.0): after signing nothing may change — filling another field, not locked by name, breaks it.</summary>
    [Fact]
    public void FieldLockP1_LaterFillOfOtherField_ChangedAfterSigning()
    {
        var signed = Sign(Prepared(Lock("Include", Names("Name"), p: 1)));
        Assert.True(SignatureTexts.IsValid(CheckOf(signed)));
        var broken = CheckOf(Fill(signed, "PLZ", "12345"));
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, broken.Outcome);
        Assert.Contains(Tr.T("Das Signaturfeld sperrt das Dokument: danach sind keine Änderungen erlaubt."), broken.LaterDetails);
    }

    // ------------------------------------------------------------ seed values (/SV, Tables 237 and 238): refuse what cannot be met

    private static PdfDictionary Seed(params (string Key, PdfObject Value)[] entries) => TestPdf.Dict([("Type", TestPdf.N("SV")), .. entries]);

    private static PdfArray NameList(params string[] names) => new(names.Select(n => (PdfObject)TestPdf.N(n)));

    private static string Refusal(Func<byte[]> sign) => Assert.Throws<SeedValueException>(() => sign()).Message;

    private static InkElement InField(string? field = "Kunde", CertificationLevel? certify = null) => new()
    {
        Rect = new PdfRectangle(100, 100, 300, 180), Strokes = [[0, 20, 40, 70, 90, 10]], StrokeWidth = 3,
        Digital = new DigitalSigning("Max Mustermann", FieldName: field, Certify: certify),
    };

    [Fact]
    public void SeedValues_RequiredSubFilterNotOffered_NotSigned()
    {
        var file = Prepared(null, Seed(("SubFilter", NameList("adbe.pkcs7.sha1")), ("Ff", PdfInteger.Of(2))));
        Assert.Contains("adbe.pkcs7.sha1", Refusal(() => Sign(file)));
        // beside the field: signed in a field of its own
        using var doc = PdfDocument.Open(Sign(file, fieldName: null));
        Assert.True(SignatureTexts.IsValid(Assert.Single(SignatureVerifier.Verify(doc, null, [], CancellationToken.None))));
    }

    [Fact]
    public void SeedValues_RequiredDigestSha512_NotSigned() =>
        Assert.Contains("SHA512", Refusal(() => Sign(Prepared(null, Seed(("DigestMethod", NameList("SHA512")), ("Ff", PdfInteger.Of(64)))))));

    /// <summary>Revocation data required inside the signature value: PDF Knead keeps it in the security store — refused also with long-term validation on.</summary>
    [Fact]
    public void SeedValues_RequiredAddRevInfo_NotSigned_EvenWithLongTerm()
    {
        var file = Prepared(null, Seed(("AddRevInfo", PdfBoolean.True), ("Ff", PdfInteger.Of(32))));
        using var ca = new LongTermValidationTests.TestCa();
        using var doc = PdfDocument.Open(file);
        var pages = PageItem.From(doc);
        var path = Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"sv-{Guid.NewGuid():N}.pdf");
        Assert.Throws<SeedValueException>(() => SignedSaving.Save(doc, [pages[0].WithElements([.. pages[0].Elements, InField()])], path, new PdfSaveOptions(),
            false, ca.Signer, DateTimeOffset.Now, revocation: ca));
        Assert.False(File.Exists(path));   // nothing written
    }

    [Fact]
    public void SeedValues_MdpP0_CertificationRefused()
    {
        var file = Prepared(null, Seed(("MDP", TestPdf.Dict(("P", PdfInteger.Of(0))))));
        Refusal(() => Sign(file, certify: CertificationLevel.FormsAndSigning));
        Assert.True(SignatureTexts.IsValid(CheckOf(Sign(file))));   // an approval signature, as asked
    }

    [Fact]
    public void SeedValues_MdpP2_OnlyThatCertification()
    {
        var file = Prepared(null, Seed(("MDP", TestPdf.Dict(("P", PdfInteger.Of(2))))));
        Refusal(() => Sign(file));
        Assert.Equal(CertificationLevel.FormsAndSigning, CheckOf(Sign(file, certify: CertificationLevel.FormsAndSigning)).Certification);
    }

    [Fact]
    public void SeedValues_OnlyRecommendations_SignedWithHint()
    {
        var file = Prepared(Lock("Include", Names("Name")), Seed(("SubFilter", NameList("adbe.pkcs7.detached"))));
        using (var doc = PdfDocument.Open(file))
        {
            var notice = SeedValues.Notice(doc, "Kunde", SigningPlan.Of(false, null, true, null))!;
            Assert.Contains(Tr.T("Dieses Feld sperrt nach dem Unterschreiben: {0}.", SignatureTexts.Locked(new FieldLock("Include", new HashSet<string> { "Name" }))), notice);
            Assert.Contains(Tr.T("Das Formular empfiehlt: {0}.", Tr.T("Signaturformat {0} (PDF-Knead schreibt {1})", "adbe.pkcs7.detached", "ETSI.CAdES.detached")), notice);
        }
        Assert.True(SignatureTexts.IsValid(CheckOf(Sign(file))));
    }

    [Fact]
    public void SeedValues_ReasonsOnlyDot_SignedWithoutReason()
    {
        var signed = Sign(Prepared(null, Seed(("Reasons", new PdfArray([new PdfString("."u8.ToArray())])), ("Ff", PdfInteger.Of(8)))));
        using var doc = PdfDocument.Open(signed);
        Assert.False(DigitalSignatures.Find(doc).Single().Signature!.ContainsKey(PdfName.Get("Reason")));
    }

    /// <summary>A time stamp required, no service set up: refused, the form's address named — and never contacted; with a service set up, signed.</summary>
    [Fact]
    public void SeedValues_RequiredTimeStampWithoutService_NotSigned_AddressNotContacted()
    {
        var file = Prepared(null, Seed(("TimeStamp", TestPdf.Dict(("URL", new PdfString("http://zeitstempel.muster.example/"u8.ToArray())), ("Ff", PdfInteger.Of(1))))));
        Assert.Contains("http://zeitstempel.muster.example/", Refusal(() => Sign(file)));
        using var authority = new SignatureTimestampTests.TestAuthority();
        using var doc = PdfDocument.Open(file);
        var signed = PdfSigner.Sign(doc, 0, InField(), Own.Value, DateTimeOffset.Now, authority);
        Assert.Equal(1, authority.Requests);   // the service set up, not the address in the file
        Assert.NotNull(CheckOf(signed).Timestamp);
    }

    /// <summary>A required certificate (/Cert /Subject with /Ff 1): another one is refused, the listed one signs.</summary>
    [Fact]
    public void SeedValues_RequiredCertificate()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var only = Seed(("Cert", TestPdf.Dict(("Type", TestPdf.N("SVCert")), ("Subject", new PdfArray([new PdfString(ca.Signer.Certificate.RawData)])),
            ("Ff", PdfInteger.Of(1)))));
        Refusal(() => Sign(Prepared(null, only)));
        var own = Seed(("Cert", TestPdf.Dict(("Subject", new PdfArray([new PdfString(Own.Value.Certificate.RawData)])), ("Ff", PdfInteger.Of(1)))));
        Assert.True(SignatureTexts.IsValid(CheckOf(Sign(Prepared(null, own)))));
    }

    /// <summary>/Lock /P 1, then only a document time stamp: allowed (erratum EC3 #131, as for DocMDP).</summary>
    [Fact]
    public void FieldLockP1_ArchiveTimestamp_StaysValid()
    {
        var signed = Sign(Prepared(Lock("Include", Names("Name"), p: 1)));
        using var authority = new SignatureTimestampTests.TestAuthority();
        byte[] stamped;
        using (var doc = PdfDocument.Open(signed))
            stamped = [.. doc.FileBytes(0, doc.FileSize), .. PdfSigner.DocumentTimestampUpdate(doc, authority)];
        var check = CheckOf(stamped);
        Assert.True(SignatureTexts.IsValid(check), string.Join(" | ", check.LaterDetails));
        Assert.Equal(LaterChanges.ValidationData, check.Later);
    }
}
