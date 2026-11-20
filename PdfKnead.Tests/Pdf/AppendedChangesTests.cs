using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Appending allowed changes to a digitally signed file (KONZEPT.md, section 5.17, step S5): filled-in fields of every kind, new
/// comments and signature fields — the signature stays valid, the values are there, a second signature can follow. Other changes
/// are not appended. With PDFKNEAD_ABLAGE=&lt;folder&gt; the files are stored for a cross-check in other readers.
/// </summary>
public sealed class AppendedChangesTests
{
    private static readonly Lazy<SigningCertificate> Own = new(() =>
        SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"anhaengen-{Guid.NewGuid():N}.p12")));

    private static readonly DateTimeOffset When = new(2026, 10, 26, 10, 0, 0, TimeSpan.FromHours(1));

    private static InkElement Signature(double left, double bottom) => new()
    {
        Rect = new PdfRectangle(left, bottom, left + 180, bottom + 60),
        Strokes = [[0, 20, 40, 50, 90, 10, 140, 45, 175, 25]],
        StrokeWidth = 3,
        Color = 0x1B3A8C,
        Digital = new DigitalSigning("Max Mustermann · 26.10.2026 10:00"),
    };

    /// <summary>The form with all field kinds, signed on its page.</summary>
    private static byte[] SignedForm()
    {
        using var doc = PdfDocument.Open(FormFillTests.AllKinds());
        return PdfSigner.Sign(doc, 0, Signature(350, 100), Own.Value, When);
    }

    private static void Store(byte[] file, string name)
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, $"angehaengt-{name}.pdf"), file);
    }

    private static SignatureCheck Check(byte[] file, Func<SignatureCheck, bool>? which = null)
    {
        using var doc = PdfDocument.Open(file);
        var checks = SignatureVerifier.Verify(doc, null, [], CancellationToken.None);
        return which is null ? checks.Single(c => !c.CoversWholeFile || checks.Count == 1) : checks.Single(which);
    }

    private static ForeignAnnotation Field(PageItem item, string name) =>
        item.Elements.OfType<ForeignAnnotation>().First(f => FormField.Of(f) is { } field && field.Name == name);

    [Fact]
    public void FilledFields_Appended_SignatureStaysValid_ValuesThere()
    {
        var signed = SignedForm();
        using var doc = PdfDocument.Open(signed);
        var saved = PageItem.From(doc);
        var values = new Dictionary<string, string> { ["Name"] = "Max Mustermann", ["PLZ"] = "12345", ["Bemerkung"] = "Erste Zeile\nZweite Zeile", ["Land"] = "at" };
        var filled = saved[0].WithElements([.. saved[0].Elements.Select(e => e switch
        {
            ForeignAnnotation f when FormField.Of(f) is { } field && values.TryGetValue(field.Name, out var v) => f with { Value = v },
            ForeignAnnotation f when FormField.Of(f) is { Name: "Zustimmung" } => f with { Value = "Ja" },
            ForeignAnnotation f when FormField.Of(f) is { Name: "Zahlung" } field => f with { Value = field.OnState == "Karte" ? "Karte" : "Off" },
            _ => e,
        })]);
        Assert.True(AppendedChanges.CanAppend(doc, saved, [filled]));
        Assert.True(SignedSaving.AppendsOnly(doc, saved, [filled]));

        var appended = AppendedChanges.Apply(doc, [filled], new PdfSaveOptions());
        Store(appended, "ausgefuellt");
        Assert.Equal(signed, appended.AsSpan(0, signed.Length).ToArray());
        var check = Check(appended);
        Assert.Equal(SignatureOutcome.ValidIdentityUnknown, check.Outcome);
        Assert.Equal(LaterChanges.FormsAndSignatures, check.Later);

        using var reopened = PdfDocument.Open(appended);
        Assert.False(reopened.WasRepaired);
        var again = PageItem.From(reopened)[0];
        FormField Of(string name) => FormField.Of(Field(again, name))!;
        Assert.Equal("Max Mustermann", Of("Name").StoredValue);
        Assert.Equal("12345", Of("PLZ").StoredValue);
        Assert.Equal("Erste Zeile\nZweite Zeile", Of("Bemerkung").StoredValue);
        Assert.Equal("at", Of("Land").StoredValue);
        Assert.Equal("Ja", Of("Zustimmung").State);
        Assert.Equal("Karte", Of("Zahlung").StoredValue);
        Assert.Equal(["Off", "Karte"], again.Elements.OfType<ForeignAnnotation>().Where(f => FormField.Of(f)?.Name == "Zahlung").Select(f => FormField.Of(f)!.State));
        Assert.Equal("fest", Of("Gesperrt").StoredValue);
        // other readers see the values in the appearances
        var text = PageText.Create(PageInterpreter.Build(reopened, reopened.Pages[0]).Text).Text;
        Assert.Contains("Max Mustermann", text);
        Assert.Contains("Österreich", text);

        // a second person signs afterwards: both signatures valid
        var twice = PdfSigner.Sign(reopened, 0, Signature(350, 200), Own.Value, When.AddHours(1));
        Store(twice, "ausgefuellt-zweite-unterschrift");
        using var both = PdfDocument.Open(twice);
        var checks = SignatureVerifier.Verify(both, null, [], CancellationToken.None);
        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.Equal(SignatureOutcome.ValidIdentityUnknown, c.Outcome));
    }

    [Fact]
    public void NewComments_AndSignatureField_Appended_EditableAfterwards()
    {
        var signed = SignedForm();
        using var doc = PdfDocument.Open(signed);
        var saved = PageItem.From(doc);
        PageElement[] added =
        [
            new TextElement { Rect = new PdfRectangle(320, 760, 560, 790), Text = "Geprüft", FontSize = 12 },
            new ShapeElement { Kind = ShapeKind.Rectangle, Rect = new PdfRectangle(300, 300, 400, 360), FillColor = 0xC00000 },
            new SignatureFieldElement { Rect = new PdfRectangle(60, 200, 260, 250), Label = "Unterschrift", FieldName = "Zweite" },
        ];
        var edited = saved[0].WithElements([.. saved[0].Elements, .. added]);
        Assert.True(AppendedChanges.CanAppend(doc, saved, [edited]));

        var appended = AppendedChanges.Apply(doc, [edited], new PdfSaveOptions());
        Store(appended, "kommentare");
        Assert.Equal(signed, appended.AsSpan(0, signed.Length).ToArray());
        var check = Check(appended);
        Assert.Equal(SignatureOutcome.ValidIdentityUnknown, check.Outcome);
        Assert.Equal(LaterChanges.Comments, check.Later);

        using var reopened = PdfDocument.Open(appended);
        var elements = PageItem.From(reopened)[0].Elements;
        Assert.Contains(elements, e => e is TextElement { Text: "Geprüft" });
        Assert.Contains(elements, e => e is ShapeElement { FillColor: 0xC00000 });
        Assert.Contains(elements, e => e is SignatureFieldElement);
        // nothing doubled: the fields of the file are there once (the new signature field is an own element again), the signature too
        Assert.Single(DigitalSignatures.Find(reopened));
        Assert.Equal(PageItem.From(doc)[0].Elements.OfType<ForeignAnnotation>().Count(), elements.OfType<ForeignAnnotation>().Count());
        Assert.Single(elements.OfType<SignatureFieldElement>());
    }

    private static PdfString S(string text) => new(PdfTextEncoding.Encode(text));

    private static PdfDictionary Widget(double l, double b, double r, double t, params (string Key, PdfObject Value)[] more) =>
        TestPdf.Dict([("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("Rect", TestPdf.Box(l, b, r, t)), ("F", PdfInteger.Of(4)), .. more]);

    /// <summary>
    /// Field structures found in third-party forms: „Ort“ is a widget on page 1 and at the same time the parent of a widget on page 2;
    /// the widget of „Summe“ sits below its field and carries a value of its own (against the standard); „Leer“ has no appearance yet.
    /// </summary>
    private static byte[] OddForm()
    {
        var pdf = TestPdf.Simple(pages: 2);
        const int page1 = 5, page2 = 7;
        var place = pdf.Add(Widget(72, 700, 272, 720, ("FT", TestPdf.N("Tx")), ("T", S("Ort")), ("DA", S("/Helv 12 Tf 0 g")), ("P", TestPdf.Ref(page1))));
        var placeKid = pdf.Add(Widget(72, 300, 272, 320, ("Parent", TestPdf.Ref(place)), ("P", TestPdf.Ref(page2))));
        ((PdfDictionary)pdf.Get(place)).Set(PdfName.Get("Kids"), new PdfArray([TestPdf.Ref(placeKid)]));
        var sum = pdf.Add(TestPdf.Dict(("FT", TestPdf.N("Tx")), ("T", S("Summe")), ("V", S("3")), ("DA", S("/Helv 12 Tf 0 g"))));
        var sumWidget = pdf.Add(Widget(72, 650, 272, 670, ("Parent", TestPdf.Ref(sum)), ("V", S("3")), ("P", TestPdf.Ref(page1))));
        ((PdfDictionary)pdf.Get(sum)).Set(PdfName.Get("Kids"), new PdfArray([TestPdf.Ref(sumWidget)]));
        var empty = pdf.Add(Widget(72, 600, 272, 620, ("FT", TestPdf.N("Tx")), ("T", S("Leer")), ("DA", S("/Helv 12 Tf 0 g")), ("P", TestPdf.Ref(page1))));
        ((PdfDictionary)pdf.Get(page1)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(place), TestPdf.Ref(sumWidget), TestPdf.Ref(empty)]));
        ((PdfDictionary)pdf.Get(page2)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(placeKid)]));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(
            ("Fields", new PdfArray([TestPdf.Ref(place), TestPdf.Ref(sum), TestPdf.Ref(empty)])), ("DA", S("/Helv 0 Tf 0 g"))));
        using var doc = PdfDocument.Open(pdf.Build());
        return PdfSigner.Sign(doc, 0, Signature(350, 100), Own.Value, When);
    }

    [Fact]
    public void OddFieldStructures_ValuesAndLooksAppended()
    {
        var signed = OddForm();
        using var doc = PdfDocument.Open(signed);
        var saved = PageItem.From(doc);
        var filled = saved.Select(item => item.WithElements([.. item.Elements.Select(e => e is ForeignAnnotation f && FormField.Of(f) is { } field
            ? field.Name switch { "Ort" => f with { Value = "Musterstadt" }, "Summe" => f with { Value = "42" }, _ => e }
            : e)])).ToArray();
        Assert.True(AppendedChanges.CanAppend(doc, saved, filled));
        var appended = AppendedChanges.Apply(doc, filled, new PdfSaveOptions());
        Store(appended, "felder-sonderfaelle");
        var check = Check(appended);
        Assert.Equal(SignatureOutcome.ValidIdentityUnknown, check.Outcome);
        Assert.Equal(LaterChanges.FormsAndSignatures, check.Later);

        using var reopened = PdfDocument.Open(appended);
        var pages = PageItem.From(reopened);
        // the value shows on both widgets of „Ort“, the parent's own appearance included
        Assert.Contains("Musterstadt", PageText.Create(PageInterpreter.Build(reopened, reopened.Pages[0]).Text).Text);
        Assert.Contains("Musterstadt", PageText.Create(PageInterpreter.Build(reopened, reopened.Pages[1]).Text).Text);
        Assert.Equal("Musterstadt", FormField.Of(Field(pages[1], "Ort"))!.StoredValue);
        // the widget's own value follows: read as 42, not as the old 3
        Assert.Equal("42", FormField.Of(Field(pages[0], "Summe"))!.StoredValue);
        // an untouched field without appearance may get one (as when written anew), its value stays empty
        Assert.Equal("", FormField.Of(Field(pages[0], "Leer"))!.StoredValue);
    }

    /// <summary>
    /// A signature whose /Reference /Data points to the catalog (FieldMDP, as written by other programs): the catalog is numbered anew when
    /// the file is written whole for comparison, the signature compared as text looked replaced and was written into the appended part —
    /// the next signature refused it as „ausgetauscht“ (docs/FEHLERGEDAECHTNIS.md, No. 547). A filled field appends only that field.
    /// </summary>
    [Fact]
    public void SignatureWithDataReference_NotRewrittenWhenAppending()
    {
        var pdf = TestPdf.Simple(pages: 1);
        const int page = 5;
        // the catalog late in the file, as the rewrite numbers it first
        var catalog = pdf.Add(pdf.Get(pdf.RootNumber));
        pdf.Replace(pdf.RootNumber, PdfNull.Instance);
        pdf.RootNumber = catalog;
        var signature = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Sig")), ("Filter", TestPdf.N("Adobe.PPKLite")), ("SubFilter", TestPdf.N("adbe.pkcs7.detached")),
            ("ByteRange", new PdfArray([PdfInteger.Of(0), PdfInteger.Of(10), PdfInteger.Of(20), PdfInteger.Of(10)])), ("Contents", new PdfString(new byte[64])),
            ("Reference", new PdfArray([TestPdf.Dict(("Type", TestPdf.N("SigRef")), ("TransformMethod", TestPdf.N("FieldMDP")), ("Data", TestPdf.Ref(catalog)),
                ("TransformParams", TestPdf.Dict(("Type", TestPdf.N("TransformParams")), ("Action", TestPdf.N("All")), ("V", TestPdf.N("1.2")))))]))));
        var signed = pdf.Add(Widget(300, 100, 480, 160, ("FT", TestPdf.N("Sig")), ("T", S("Vorher")), ("V", TestPdf.Ref(signature)), ("P", TestPdf.Ref(page))));
        var name = pdf.Add(Widget(72, 700, 272, 720, ("FT", TestPdf.N("Tx")), ("T", S("Name")), ("DA", S("/Helv 12 Tf 0 g")), ("P", TestPdf.Ref(page))));
        ((PdfDictionary)pdf.Get(page)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(signed), TestPdf.Ref(name)]));
        ((PdfDictionary)pdf.Get(catalog)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(
            ("Fields", new PdfArray([TestPdf.Ref(signed), TestPdf.Ref(name)])), ("DA", S("/Helv 0 Tf 0 g")), ("SigFlags", PdfInteger.Of(3))));
        var original = pdf.Build();
        using var doc = PdfDocument.Open(original);
        var saved = PageItem.From(doc);
        var filled = new[] { saved[0].WithElements([.. saved[0].Elements.Select(e => e is ForeignAnnotation f && FormField.Of(f)?.Name == "Name" ? f with { Value = "Erika Mustermann" } : e)]) };

        var appended = AppendedChanges.Apply(doc, filled, new PdfSaveOptions());
        var added = System.Text.Encoding.Latin1.GetString(appended, original.Length, appended.Length - original.Length);
        using (var reopened = PdfDocument.Open(appended))
            Assert.Equal("Erika Mustermann", FormField.Of(Field(PageItem.From(reopened)[0], "Name"))!.StoredValue);
        Assert.DoesNotContain("/ByteRange", added);
        Assert.DoesNotContain("SigRef", added);
    }

    [Fact]
    public void OtherChanges_NotAppended()
    {
        using var doc = PdfDocument.Open(SignedForm());
        var saved = PageItem.From(doc);
        var item = saved[0];
        var name = Field(item, "Name");
        bool Can(params PageElement[] elements) => AppendedChanges.CanAppend(doc, saved, [item.WithElements(elements)]);

        // a signature written fixed into the page
        Assert.False(Can([.. item.Elements, new SignatureElement { Rect = new PdfRectangle(60, 100, 260, 160), Name = "Max Mustermann", FontFile = "PatrickHand-Regular.ttf" }]));
        // a field of the file moved, or deleted
        Assert.False(Can([.. item.Elements.Select(e => ReferenceEquals(e, name) ? name with { Rect = new PdfRectangle(80, 700, 280, 720) } : e)]));
        Assert.False(Can([.. item.Elements.Where(e => !ReferenceEquals(e, name))]));
        // a new field to fill in changes the form itself
        Assert.False(Can([.. item.Elements, new TextElement { Rect = new PdfRectangle(60, 300, 260, 320), Text = "", IsField = true }]));
        // nothing changed: appending is enough, and nothing is added
        Assert.True(AppendedChanges.CanAppend(doc, saved, saved));
        Assert.Equal(doc.FileSize, AppendedChanges.Apply(doc, saved, new PdfSaveOptions()).Length);
    }

    /// <summary>
    /// A signed document with an own empty signature field „Zweite“ (S7-4): a second digital signature placed into it keeps the field, which
    /// is filled when signing — appended, both signatures valid; afterwards the field is no longer an own (movable) element.
    /// </summary>
    [Fact]
    public void OwnSignatureField_SecondSignatureInto_Appended()
    {
        var signed = SignedForm();
        byte[] withField;
        using (var doc = PdfDocument.Open(signed))
        {
            var pages = PageItem.From(doc);
            var edited = new[] { pages[0].WithElements([.. pages[0].Elements, new SignatureFieldElement { Rect = new PdfRectangle(60, 200, 260, 260), FieldName = "Zweite" }]) };
            withField = AppendedChanges.Apply(doc, edited, new PdfSaveOptions());
        }
        using var reopened = PdfDocument.Open(withField);
        var saved = PageItem.From(reopened);
        var own = Assert.Single(saved[0].Elements.OfType<SignatureFieldElement>());
        Assert.Equal("Zweite", own.FieldName);
        var second = Signature(70, 205) with { Rect = new PdfRectangle(70, 205, 250, 255), Digital = new DigitalSigning(null, own.FieldName) };
        var placed = new[] { saved[0].WithElements([.. saved[0].Elements, second]) };
        Assert.True(SignedSaving.AppendsOnly(reopened, saved, placed));

        var path = Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"zweite-{Guid.NewGuid():N}.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SignedSaving.Save(reopened, placed, path, new PdfSaveOptions(), appendOnly: true, Own.Value, When.AddHours(1));
        var final = File.ReadAllBytes(path);
        Assert.Equal(withField, final.AsSpan(0, withField.Length).ToArray());
        using var done = PdfDocument.Open(final);
        var checks = SignatureVerifier.Verify(done, null, [], CancellationToken.None);
        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.Equal(SignatureOutcome.ValidIdentityUnknown, c.Outcome));
        Assert.Contains(checks, c => c.FieldName == "Zweite");
        Assert.Empty(PageItem.From(done)[0].Elements.OfType<SignatureFieldElement>());
    }

    /// <summary>
    /// A file that repairs its cross-references only while being written (fuzzing, S7): appending fails with the failure that lets the
    /// program ask and write the file anew, instead of an error the save does not expect.
    /// </summary>
    [Fact]
    public void RepairedWhileWriting_NotAppendable()
    {
        using var doc = PdfDocument.Open(IncrementalUpdateTests.LatentlyBroken());
        var pages = PageItem.From(doc);
        Assert.True(AppendedChanges.CanAppend(doc, pages, pages));
        var commented = new[] { pages[0].WithElements([.. pages[0].Elements, new TextElement { Rect = new PdfRectangle(50, 700, 250, 730), Text = "Geprüft", FontSize = 12 }]) };
        Assert.Throws<NotAppendableException>(() => AppendedChanges.Apply(doc, commented, new PdfSaveOptions()));
    }
}
