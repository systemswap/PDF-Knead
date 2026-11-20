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

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Attacks on the check of signatures through later revisions (the shadow and certification attacks of https://pdf-insecurity.org, analysis
/// round 8r.1): an appearance stream replaced under its number shows something else without touching the field — for a locked field, a
/// comment of a certified document, a signed signature (also with a widget of its own) and a stream the page content draws too. Each must
/// count as „changed after signing“; filling an unlocked field the same way stays allowed (docs/FEHLERGEDAECHTNIS.md, „Erscheinungsbilder an
/// Ort und Stelle ersetzt“).
/// </summary>
public sealed class SignatureAttackTests
{
    private static readonly Lazy<SigningCertificate> Own = new(() =>
        SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"angriff-{Guid.NewGuid():N}.p12")));

    private static InkElement Signature(double left, DigitalSigning? digital = null) => new()
    {
        Rect = new PdfRectangle(left, 100, left + 200, 180),
        Strokes = [[0, 20, 40, 70, 90, 10, 140, 60]],
        StrokeWidth = 3,
        Digital = digital ?? new DigitalSigning("Max Mustermann"),
    };

    private static byte[] Sign(byte[] file, InkElement signature)
    {
        using var doc = PdfDocument.Open(file);
        return PdfSigner.Sign(doc, 0, signature, Own.Value, DateTimeOffset.Now);
    }

    private static SignatureCheck CheckOfFirst(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        return SignatureVerifier.Verify(doc, null, [], CancellationToken.None).Single(c => !c.CoversWholeFile);
    }

    /// <summary>Appends a revision that replaces one stream under its number with a form showing other text.</summary>
    private static byte[] ReplaceStream(byte[] file, int number)
    {
        using var doc = PdfDocument.Open(file);
        var update = new IncrementalUpdate(doc);
        update.Replace(new PdfObjectId(number, 0), TestPdf.FlateStream(Encoding.ASCII.GetBytes("BT /Helv 10 Tf 2 4 Td (Erika Mustermann) Tj ET"),
            TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 200, 20)))));
        using var output = new MemoryStream();
        update.WriteTo(output);
        return output.ToArray();
    }

    private static int NormalAppearance(PdfDocument doc, Func<PdfDictionary, bool> which)
    {
        var annots = doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
        for (var i = 0; i < annots.Count; i++)
            if (annots.Get(i) is PdfDictionary annotation && which(annotation))
                return ((PdfReference)annotation.GetDictionary(PdfName.Get("AP"))!.GetRaw(PdfName.N)!).Id.Number;
        throw new InvalidOperationException();
    }

    [Fact]
    public void LockedField_LookReplacedInPlace_Detected()
    {
        var signed = Sign(FormFillTests.AllKinds(), Signature(350, new DigitalSigning("Max Mustermann", LockFields: true)));
        int look;
        using (var doc = PdfDocument.Open(signed)) look = NormalAppearance(doc, a => a.GetString(PdfName.Get("T"))?.ToText() == "Name");
        var check = CheckOfFirst(ReplaceStream(signed, look));
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, check.Outcome);
        Assert.Contains(Tr.T("Das Feld „{0}“ ist durch diese Signatur gesperrt und wurde danach geändert.", "Name"), check.LaterDetails);
    }

    [Fact]
    public void UnlockedField_LookReplacedInPlace_StaysFilling()
    {
        var signed = Sign(FormFillTests.AllKinds(), Signature(350));
        int look;
        using (var doc = PdfDocument.Open(signed)) look = NormalAppearance(doc, a => a.GetString(PdfName.Get("T"))?.ToText() == "Name");
        var check = CheckOfFirst(ReplaceStream(signed, look));
        Assert.True(SignatureTexts.IsValid(check), check.Problem);
        Assert.Equal(LaterChanges.FormsAndSignatures, check.Later);
    }

    [Fact]
    public void SignedSignature_LookReplacedInPlace_Detected()
    {
        var once = Sign(TestPdf.Simple(1).Build(), Signature(50));
        int look;
        using (var doc = PdfDocument.Open(once)) look = NormalAppearance(doc, a => a.GetName(PdfName.Get("FT"))?.Value == "Sig");
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, CheckOfFirst(ReplaceStream(once, look)).Outcome);
    }

    /// <summary>A signature field and its widget apart (the widget a kid of the field): the widget's look is part of the signature as well.</summary>
    [Fact]
    public void SignedSignature_WidgetApart_LookChanged_Detected()
    {
        var pdf = TestPdf.Simple(1);
        var field = pdf.Add(TestPdf.Dict(("FT", TestPdf.N("Sig")), ("T", new PdfString("Unterschrift"u8.ToArray()))));
        var widget = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("Rect", TestPdf.Box(40, 90, 260, 190)),
            ("F", PdfInteger.Of(4)), ("Parent", TestPdf.Ref(field)), ("P", TestPdf.Ref(5))));
        ((PdfDictionary)pdf.Get(field)).Set(PdfName.Get("Kids"), new PdfArray([TestPdf.Ref(widget)]));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(widget)]));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new PdfArray([TestPdf.Ref(field)]))));
        var signed = Sign(pdf.Build(), Signature(50, new DigitalSigning("Max Mustermann", FieldName: "Unterschrift")));
        using var doc = PdfDocument.Open(signed);
        Assert.IsType<PdfDictionary>(((PdfDictionary)doc.GetObject(field)).Get(PdfName.Get("V")));   // the field of the file was signed
        var update = new IncrementalUpdate(doc);
        var look = update.Add(TestPdf.FlateStream(Encoding.ASCII.GetBytes("BT /Helv 10 Tf 2 4 Td (Erika Mustermann) Tj ET"),
            TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 220, 100)))));
        var copy = new PdfDictionary();
        foreach (var (key, value) in ((PdfDictionary)doc.GetObject(widget)).Entries) copy.Set(key, value);
        copy.Set(PdfName.Get("AP"), TestPdf.Dict(("N", look)));
        update.Replace(new PdfObjectId(widget, 0), copy);
        using var output = new MemoryStream();
        update.WriteTo(output);
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, CheckOfFirst(output.ToArray()).Outcome);
    }

    /// <summary>A comment's look replaced in a document certified for filling and signing only: a change to a comment, not filling in.</summary>
    [Fact]
    public void Certified_CommentLookReplacedInPlace_Detected()
    {
        var pdf = TestPdf.Simple(1);
        var form = pdf.Add(TestPdf.FlateStream("1 0 0 rg 0 0 50 50 re f"u8.ToArray(),
            TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 50, 50)))));
        var square = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Square")), ("Rect", TestPdf.Box(300, 600, 350, 650)),
            ("AP", TestPdf.Dict(("N", TestPdf.Ref(form))))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(square)]));
        var certified = Sign(pdf.Build(), Signature(50, new DigitalSigning("Max Mustermann", Certify: CertificationLevel.FormsAndSigning)));
        var check = CheckOfFirst(ReplaceStream(certified, form));
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, check.Outcome);
        // in an ordinary signed document the same is a comment
        var ordinary = Sign(pdf.Build(), Signature(50));
        var commented = CheckOfFirst(ReplaceStream(ordinary, form));
        Assert.True(SignatureTexts.IsValid(commented), commented.Problem);
        Assert.Equal(LaterChanges.Comments, commented.Later);
    }

    /// <summary>A stream that is a field's look and drawn by the page content too: replacing it changes the page.</summary>
    [Fact]
    public void LookAlsoDrawnByThePage_Replaced_Detected()
    {
        var pdf = TestPdf.Simple(1);
        var form = pdf.Add(TestPdf.FlateStream("0 0 1 rg 0 0 200 20 re f"u8.ToArray(),
            TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 200, 20)))));
        var content = pdf.Add(TestPdf.FlateStream("q 1 0 0 1 72 400 cm /X1 Do Q"u8.ToArray()));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Contents, TestPdf.Ref(content));
        page.Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("X1", TestPdf.Ref(form))))));
        var field = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("Rect", TestPdf.Box(72, 700, 272, 720)),
            ("FT", TestPdf.N("Tx")), ("T", new PdfString("Name"u8.ToArray())), ("AP", TestPdf.Dict(("N", TestPdf.Ref(form))))));
        page.Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(field)]));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new PdfArray([TestPdf.Ref(field)]))));
        var signed = Sign(pdf.Build(), Signature(300));
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, CheckOfFirst(ReplaceStream(signed, form)).Outcome);
    }

    /// <summary>
    /// Universal signature forgery: a signature field whose value looks like a signature, but without usable byte ranges or block — its look
    /// on the page may claim „signed by …“. Listed as an invalid signature, not left out (else the document would seem unsigned but show a
    /// signature).
    /// </summary>
    [Theory]
    [InlineData("ohne")]        // no /ByteRange
    [InlineData("negativ")]     // /ByteRange with a negative number
    [InlineData("drei")]        // three numbers
    [InlineData("null")]        // /Contents null
    public void ForgedSignatureValue_ListedAsInvalid(string variant)
    {
        var pdf = TestPdf.Simple(1);
        var value = TestPdf.Dict(("Type", TestPdf.N("Sig")), ("Filter", TestPdf.N("Adobe.PPKLite")), ("SubFilter", TestPdf.N("ETSI.CAdES.detached")),
            ("Name", new PdfString("Max Mustermann"u8.ToArray())));
        if (variant != "null") value.Set(PdfName.Contents, new PdfString(new byte[16], isHex: true));
        else value.Set(PdfName.Contents, PdfNull.Instance);
        switch (variant)
        {
            case "negativ": value.Set(PdfName.Get("ByteRange"), new PdfArray([PdfInteger.Of(0), PdfInteger.Of(-5), PdfInteger.Of(10), PdfInteger.Of(5)])); break;
            case "drei": value.Set(PdfName.Get("ByteRange"), new PdfArray([PdfInteger.Of(0), PdfInteger.Of(5), PdfInteger.Of(10)])); break;
            case "null": value.Set(PdfName.Get("ByteRange"), new PdfArray([PdfInteger.Of(0), PdfInteger.Of(5), PdfInteger.Of(10), PdfInteger.Of(5)])); break;
        }
        var signature = pdf.Add(value);
        var field = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("Rect", TestPdf.Box(72, 100, 272, 160)),
            ("FT", TestPdf.N("Sig")), ("T", new PdfString("Unterschrift"u8.ToArray())), ("V", TestPdf.Ref(signature))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(field)]));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new PdfArray([TestPdf.Ref(field)]))));
        using var doc = PdfDocument.Open(pdf.Build());
        Assert.Empty(DigitalSignatures.Find(doc));
        var check = Assert.Single(SignatureVerifier.Verify(doc, null, [], CancellationToken.None));
        Assert.Equal(SignatureOutcome.Invalid, check.Outcome);
        Assert.False(SignatureTexts.IsValid(check));
        Assert.Equal("Unterschrift", check.FieldName);
        Assert.Equal(Tr.T("Die Signatur ist unvollständig: Byte-Bereiche oder Signaturblock fehlen oder sind ungültig."), check.Problem);
    }

    /// <summary>
    /// The form dictionary after signing: an XFA form put in (some readers show it instead of the fields) or another key changes what the form
    /// shows; the default resources, as filling in may extend them, do not (docs/FEHLERGEDAECHTNIS.md, No. 334).
    /// </summary>
    [Theory]
    [InlineData("XFA", false)]
    [InlineData("CO", false)]
    [InlineData("DR", true)]
    [InlineData("NeedAppearances", true)]
    public void FormDictionaryChanged_OnlyFillingKeysAllowed(string key, bool allowed)
    {
        var signed = Sign(FormFillTests.AllKinds(), Signature(350));
        using var doc = PdfDocument.Open(signed);
        var update = new IncrementalUpdate(doc);
        var catalog = new PdfDictionary();
        foreach (var (k, v) in doc.Catalog!.Entries) catalog.Set(k, v);
        var form = new PdfDictionary();
        foreach (var (k, v) in doc.Catalog.GetDictionary(PdfName.Get("AcroForm"))!.Entries) form.Set(k, v);
        form.Set(PdfName.Get(key), key switch
        {
            "XFA" => update.Add(TestPdf.FlateStream("<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\"/>"u8.ToArray())),
            "CO" => new PdfArray(),
            "DR" => TestPdf.Dict(("Font", TestPdf.Dict(("Helv", TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica"))))))),
            _ => PdfBoolean.True,
        });
        if (doc.Catalog.GetRaw(PdfName.Get("AcroForm")) is PdfReference formRef) update.Replace(formRef.Id, form);
        else
        {
            catalog.Set(PdfName.Get("AcroForm"), form);
            update.Replace(((PdfReference)doc.Trailer.GetRaw(PdfName.Root)!).Id, catalog);
        }
        using var output = new MemoryStream();
        update.WriteTo(output);
        var check = CheckOfFirst(output.ToArray());
        Assert.Equal(allowed, SignatureTexts.IsValid(check));
    }

    /// <summary>The signed file with another block in the gap of its signature (hexadecimal, padded with zeros as a signer leaves it).</summary>
    private static byte[] WithBlock(byte[] signed, byte[] der)
    {
        using var doc = PdfDocument.Open(signed);
        var range = DigitalSignatures.Find(doc).Single().ByteRange;
        var hex = Encoding.ASCII.GetBytes(Convert.ToHexString(der));
        var room = (int)(range[2] - range[1] - 2);
        Assert.True(hex.Length <= room);
        var copy = (byte[])signed.Clone();
        Array.Fill(copy, (byte)'0', (int)range[1] + 1, room);
        hex.CopyTo(copy, (int)range[1] + 1);
        return copy;
    }

    /// <summary>The bytes a signature of the file covers.</summary>
    private static byte[] Covered(byte[] signed)
    {
        using var doc = PdfDocument.Open(signed);
        var r = DigitalSignatures.Find(doc).Single().ByteRange;
        return [.. signed.AsSpan((int)r[0], (int)r[1]), .. signed.AsSpan((int)r[2], (int)r[3])];
    }

    /// <summary>A block that carries content of its own (not detached), signed with the own certificate.</summary>
    private static byte[] Enveloping(byte[] content)
    {
        var cms = new SignedCms(new ContentInfo(content), detached: false);
        cms.ComputeSignature(new CmsSigner(Own.Value.Certificate) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), IncludeOption = X509IncludeOption.EndCertOnly });
        return cms.Encode();
    }

    /// <summary>
    /// Blocks a detached signature must not be, which the platform's check passes over (docs/FEHLERGEDAECHTNIS.md, No. 344): content of its own
    /// signed in place of the file, a signature algorithm named as digest algorithm, a block of zeros — each listed invalid with its reason; the
    /// bytes of the file carried once more in the block are signed all the same (valid, with a warning).
    /// </summary>
    [Theory]
    [InlineData("eigene")]
    [InlineData("gleiche")]
    [InlineData("verfahren")]
    [InlineData("leer")]
    public void DetachedBlockOfAnotherShape_ToldApart(string variant)
    {
        // signed with a time stamp: its room in the gap takes a block that carries the whole file once more
        using var authority = new SignatureTimestampTests.TestAuthority();
        byte[] signed;
        using (var original = PdfDocument.Open(TestPdf.Simple(1).Build()))
            signed = PdfSigner.Sign(original, 0, Signature(100), Own.Value, DateTimeOffset.Now, authority);
        var der = (byte[])CheckBlock(signed).Clone();
        var file = variant switch
        {
            "eigene" => WithBlock(signed, Enveloping([0])),
            "gleiche" => WithBlock(signed, Enveloping(Covered(signed))),
            "verfahren" => WithBlock(signed, SignatureOidAsDigest(der)),
            _ => WithBlock(signed, []),
        };
        using var doc = PdfDocument.Open(file);
        var check = Assert.Single(SignatureVerifier.Verify(doc, null, [], CancellationToken.None));
        var expected = variant switch
        {
            "eigene" => Tr.T("Der Signaturblock unterschreibt eigene Daten statt der Datei."),
            "verfahren" => Tr.T("Der Signaturblock nennt ein Signaturverfahren, wo ein Prüfsummenverfahren stehen muss (RFC 5652) — so ist er nicht normgerecht."),
            "leer" => Tr.T("Der Signaturblock ist leer: hier wurde nie unterschrieben."),
            _ => null,
        };
        Assert.Equal(expected, check.Problem);
        Assert.Equal(expected is null, SignatureTexts.IsValid(check));
        if (expected is null)
            Assert.Contains(Tr.T("Der Signaturblock trägt die unterschriebenen Daten noch einmal in sich; bei diesem Verfahren ist das nicht vorgesehen."), check.Warnings);
    }

    /// <summary>The block in the gap of the file's one signature.</summary>
    private static byte[] CheckBlock(byte[] signed)
    {
        using var doc = PdfDocument.Open(signed);
        return CmsSignature.Der(Convert.FromHexString(Encoding.ASCII.GetString(signed, (int)DigitalSignatures.Find(doc).Single().ByteRange[1] + 1,
            (int)(DigitalSignatures.Find(doc).Single().ByteRange[2] - DigitalSignatures.Find(doc).Single().ByteRange[1] - 2))))!;
    }

    /// <summary>The block with sha256WithRSAEncryption wherever sha256 names the digest algorithm (both identifiers are 11 bytes long).</summary>
    private static byte[] SignatureOidAsDigest(byte[] der)
    {
        byte[] sha256 = [0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01];
        byte[] withRsa = [0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x0B];
        var copy = (byte[])der.Clone();
        for (var at = copy.AsSpan().IndexOf(sha256); at >= 0; at = copy.AsSpan().IndexOf(sha256)) withRsa.CopyTo(copy, at);
        return copy;
    }

    /// <summary>
    /// A second signature field whose value claims the byte ranges of the first, with a block of its own (as an edited file brings it): listed
    /// as invalid — the block in the file is not its own — instead of being left out as a copy of the first (docs/FEHLERGEDAECHTNIS.md, No. 344).
    /// </summary>
    [Fact]
    public void SecondSignatureBorrowingTheByteRanges_ListedAsInvalid()
    {
        var signed = Sign(TestPdf.Simple(1).Build(), Signature(100));
        byte[] file;
        using (var doc = PdfDocument.Open(signed))
        {
            var first = DigitalSignatures.Find(doc).Single();
            var update = new IncrementalUpdate(doc);
            var value = TestPdf.Dict(("Type", TestPdf.N("Sig")), ("Filter", TestPdf.N("Adobe.PPKLite")), ("SubFilter", TestPdf.N("ETSI.CAdES.detached")),
                ("ByteRange", new PdfArray([.. first.ByteRange.Select(n => (PdfObject)PdfInteger.Of(n))])),
                ("Contents", new PdfString(Enveloping([1, 2, 3]), isHex: true)));
            var signature = update.Add(value);
            var field = update.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("Rect", TestPdf.Box(0, 0, 0, 0)),
                ("FT", TestPdf.N("Sig")), ("T", new PdfString("Zweite"u8.ToArray())), ("V", signature)));
            // the form dictionary where it is: an object of its own, or inside the catalogue
            var form = doc.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!;
            var fields = form.GetArray(PdfName.Get("Fields"))!;
            var copy = new PdfDictionary();
            foreach (var (key, _) in form.Entries) copy.Set(key, form.GetRaw(key)!);
            copy.Set(PdfName.Get("Fields"), new PdfArray([.. Enumerable.Range(0, fields.Count).Select(i => fields[i]), field]));
            if (doc.Catalog.GetRaw(PdfName.Get("AcroForm")) is PdfReference formRef) update.Replace(formRef.Id, copy);
            else
            {
                var catalog = new PdfDictionary();
                foreach (var (key, _) in doc.Catalog.Entries) catalog.Set(key, doc.Catalog.GetRaw(key)!);
                catalog.Set(PdfName.Get("AcroForm"), copy);
                update.Replace(((PdfReference)doc.Trailer.GetRaw(PdfName.Root)!).Id, catalog);
            }
            using var output = new MemoryStream();
            update.WriteTo(output);
            file = output.ToArray();
        }
        using var saved = PdfDocument.Open(file);
        Assert.Equal(2, DigitalSignatures.Find(saved).Count);
        var second = SignatureVerifier.Verify(saved, null, [], CancellationToken.None).Single(c => c.FieldName == "Zweite");
        Assert.Equal(Tr.T("Der Signaturblock gehört nicht zu dieser Signatur."), second.Problem);
    }

    /// <summary>
    /// Hide-and-replace through the cross-reference table alone (the shadow attacks of https://pdf-insecurity.org): before signing the page
    /// content was replaced in an update, so the file still holds the old body; after signing a revision of nothing but a cross-reference
    /// section points the content back to the old body — no object is written anew. It must count as „changed after signing“
    /// (OFFENE-PUNKTE 8f (2); docs/ABLAUFPLAN.md, 9.3.2).
    /// </summary>
    [Fact]
    public void XrefOnlyRevision_PointingBackToAnOlderBody_Detected()
    {
        var first = TestPdf.Simple(1).Build();
        int content;
        using (var doc = PdfDocument.Open(first)) content = ((PdfReference)doc.Pages[0].Dictionary.GetRaw(PdfName.Contents)!).Id.Number;
        var oldBody = Encoding.ASCII.GetString(first).IndexOf($"\n{content} 0 obj", StringComparison.Ordinal) + 1;
        Assert.True(oldBody > 0);
        byte[] replaced;
        using (var doc = PdfDocument.Open(first))
        {
            var update = new IncrementalUpdate(doc);
            update.Replace(new PdfObjectId(content, 0), TestPdf.FlateStream("BT /F1 24 Tf 72 700 Td (Harmlos) Tj ET"u8.ToArray()));
            using var output = new MemoryStream();
            update.WriteTo(output);
            replaced = output.ToArray();
        }
        var signed = Sign(replaced, Signature(350));
        string tail;
        using (var doc = PdfDocument.Open(signed))
        {
            var root = (PdfReference)doc.Trailer.GetRaw(PdfName.Root)!;
            var text = Encoding.ASCII.GetString(signed);
            var startxref = text.LastIndexOf("startxref", StringComparison.Ordinal);
            var prev = long.Parse(text[(startxref + 9)..text.IndexOf("%%EOF", startxref, StringComparison.Ordinal)].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            tail = $"xref\n0 1\n0000000000 65535 f \n{content} 1\n{oldBody:D10} 00000 n \ntrailer\n<< /Size {doc.Trailer.GetInteger(PdfName.Get("Size"))} " +
                   $"/Root {root.Id.Number} {root.Id.Generation} R /Prev {prev} >>\nstartxref\n{signed.Length}\n%%EOF\n";
        }
        var attacked = signed.Concat(Encoding.ASCII.GetBytes(tail)).ToArray();
        using (var doc = PdfDocument.Open(attacked))
            Assert.Equal(TestPdf.PageContent(1), doc.GetStreamData((PdfStream)doc.Pages[0].Dictionary.Get(PdfName.Contents)!));   // the attack works
        var check = CheckOfFirst(attacked);
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, check.Outcome);
    }
}
