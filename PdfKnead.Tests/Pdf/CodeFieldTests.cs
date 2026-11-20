using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Code fields (KONZEPT.md, section 5.19, step 4): a text field whose value is drawn as a QR or bar code. Saved as a field that is
/// read-only for other readers, with the code as its appearance and the value in /V; PDF Knead reads it back as a code field and fills
/// it in, unless its own setting locks it.
/// </summary>
public sealed class CodeFieldTests
{
    private static readonly PdfName Ff = PdfName.Get("Ff"), V = PdfName.Get("V");

    private static PdfDocument Save(PdfDocument main, IReadOnlyList<PageItem> pages, PdfSaveOptions? options = null)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(main, pages, output, options);
        return PdfDocument.Open(output.ToArray());
    }

    private static IReadOnlyList<PageBarcode> Codes(PdfDocument doc) =>
        PageBarcodes.FindAll(doc, doc.Pages[0], PageInterpreter.Build(doc, doc.Pages[0]), [], new ImageCache());

    private static FieldElement Field(CodeLook look, string value, PdfRectangle rect, string name = "", bool locked = false) => new()
    {
        Rect = rect,
        Spec = new FieldSpec { Name = name, Code = look, ReadOnly = locked },
        Value = value,
    };

    [Fact]
    public void New_SavedReadOnlyWithTheCode_ComesBackAsCodeField_FilledIn()
    {
        using var doc = BlankPage.CreateDocument(595, 842, 1);
        var qr = new CodeLook { Level = Core.Barcodes.Qr.QrErrorCorrection.Q, Color = 0x1F4E9A };
        var ean = new CodeLook { Format = BarcodeFormat.Ean13, QuietZone = true };
        using var saved = Save(doc, [PageItem.From(doc)[0].WithElements(
        [
            Field(qr, "https://example.org/rechnung?nr=4711", new PdfRectangle(60, 600, 160, 700), "Rechnung"),
            Field(ean, "4006381333931", new PdfRectangle(300, 600, 410, 674), "Artikel"),
            Field(new CodeLook { Format = BarcodeFormat.DataMatrix }, "", new PdfRectangle(60, 400, 130, 470), "Leer"),
        ])]);

        // the codes of the values are on the page; the empty field draws nothing that looks like a code
        var found = Codes(saved);
        Assert.Contains(found, f => f.Code.Format == BarcodeFormat.Qr && f.Code.Text == "https://example.org/rechnung?nr=4711");
        Assert.Contains(found, f => f.Code.Format == BarcodeFormat.Ean13 && f.Code.Text == "4006381333931");
        Assert.Equal(2, found.Count);

        // in the file: text fields, read-only for other readers, the value in /V
        var widgets = PageItem.From(saved)[0].Elements.OfType<ForeignAnnotation>().ToList();
        Assert.Equal(3, widgets.Count);
        foreach (var widget in widgets)
        {
            var field = FormField.Of(widget)!;
            Assert.Equal(FormFieldKind.Text, field.Kind);
            Assert.Equal(1, field.Field.GetInteger(Ff)!.Value & 1);
        }
        var rechnung = widgets.Single(w => FieldSpec.Of(w)!.Name == "Rechnung");
        Assert.Equal("https://example.org/rechnung?nr=4711", FormField.Of(rechnung)!.Field.GetString(V)!.ToText());

        // … and in PDF Knead code fields again, with their look, fillable
        Assert.Equal(qr, FieldSpec.Of(rechnung)!.Code);
        Assert.False(FieldSpec.Of(rechnung)!.ReadOnly);
        Assert.Equal(ean, FieldSpec.Of(widgets.Single(w => FieldSpec.Of(w)!.Name == "Artikel"))!.Code);

        // filled in: the new value as its code, the old one gone
        var items = PageItem.From(saved);
        items[0] = items[0].WithElements([.. items[0].Elements.Select(e => ReferenceEquals(e, rechnung) ? rechnung with { Value = "Neu 2026\nZweite Zeile" } : e)]);
        using var filled = Save(saved, items);
        var now = Codes(filled);
        Assert.Contains(now, f => f.Code.Format == BarcodeFormat.Qr && f.Code.Text == "Neu 2026\nZweite Zeile");
        Assert.DoesNotContain(now, f => f.Code.Text.Contains("rechnung", StringComparison.Ordinal));
        var again = PageItem.From(filled)[0].Elements.OfType<ForeignAnnotation>().Single(w => FieldSpec.Of(w)!.Name == "Rechnung");
        Assert.Equal("Neu 2026\nZweite Zeile", FormField.Of(again)!.StoredValue);
        Assert.Equal(qr, FieldSpec.Of(again)!.Code);
        Assert.Equal(1, FormField.Of(again)!.Field.GetInteger(Ff)!.Value & 1);
    }

    [Fact]
    public void Locked_StaysLocked_AndFlattenedTheCodeStays()
    {
        using var doc = BlankPage.CreateDocument(595, 842, 1);
        using var saved = Save(doc, [PageItem.From(doc)[0].WithElements(
            [Field(new CodeLook(), "Lager 17", new PdfRectangle(60, 600, 160, 700), "Lager", locked: true)])]);
        var widget = PageItem.From(saved)[0].Elements.OfType<ForeignAnnotation>().Single();
        Assert.True(FieldSpec.Of(widget)!.ReadOnly);
        Assert.NotNull(FieldSpec.Of(widget)!.Code);

        using var flat = Save(saved, PageItem.From(saved), new PdfSaveOptions { Flatten = true });
        Assert.Empty(PageItem.From(flat)[0].Elements);
        Assert.Equal("Lager 17", Assert.Single(Codes(flat)).Code.Text);
    }

    /// <summary>
    /// With /NeedAppearances other readers draw every field anew from its value — a code field would show its content as text. Saving with a
    /// code field settles the appearances: the flag goes, a field without an appearance gets the one the own display draws (its value once),
    /// and the code stays the code, in the own display as well.
    /// </summary>
    [Fact]
    public void NeedAppearances_SettledOnSaving_TheCodeStays()
    {
        using var doc = BlankPage.CreateDocument(595, 842, 1);
        using var saved = Save(doc, [PageItem.From(doc)[0].WithElements(
        [
            Field(new CodeLook(), "LAGER-17", new PdfRectangle(60, 600, 160, 700)),
            new FieldElement { Rect = new PdfRectangle(60, 500, 300, 520), Spec = new FieldSpec { Name = "Name" }, Value = "Max Mustermann" },
        ])]);
        var form = saved.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!;
        form.Set(PdfName.Get("NeedAppearances"), PdfBoolean.True);
        var name = PageItem.From(saved)[0].Elements.OfType<ForeignAnnotation>().Single(w => FieldSpec.Of(w)!.Name == "Name");
        name.Original.Remove(PdfName.Get("AP"));   // as other programs leave fields with NeedAppearances
        // the own display: the code, and the value of the field without an appearance as text
        Assert.Equal("LAGER-17", Assert.Single(Codes(saved)).Code.Text);
        Assert.DoesNotContain(PageInterpreter.Build(saved, saved.Pages[0]).Text, g => g.Text == "L");

        using var again = Save(saved, PageItem.From(saved));
        Assert.False(again.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.ContainsKey(PdfName.Get("NeedAppearances")));
        var widget = PageItem.From(again)[0].Elements.OfType<ForeignAnnotation>().Single(w => FieldSpec.Of(w)!.Name == "Name");
        Assert.IsType<PdfStream>(widget.Original.GetDictionary(PdfName.Get("AP"))!.Get(PdfName.N));
        Assert.Equal("Max Mustermann", FormField.Of(widget)!.StoredValue);
        var text = string.Concat(PageInterpreter.Build(again, again.Pages[0]).Text.Select(g => g.Text));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(text, "Mustermann"));
        Assert.Equal("LAGER-17", Assert.Single(Codes(again)).Code.Text);
    }

    /// <summary>
    /// A signed form with a code field: filled in, the change is appended — the signature stays valid, the new code is on the page, the field
    /// stays a code field.
    /// </summary>
    [Fact]
    public void SignedForm_CodeFieldFilledIn_Appended_SignatureStaysValid()
    {
        using var doc = BlankPage.CreateDocument(595, 842, 1);
        var form = Save(doc, [PageItem.From(doc)[0].WithElements([Field(new CodeLook(), "Lager 17", new PdfRectangle(60, 600, 160, 700), "Lager")])]);
        var certificate = Core.Platform.SigningCertificates.CreateOwn("Max Mustermann",
            Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"codefeld-{Guid.NewGuid():N}.p12"));
        var ink = new InkElement
        {
            Rect = new PdfRectangle(350, 100, 530, 160), Strokes = [[0, 20, 40, 50, 90, 10, 140, 45, 175, 25]], StrokeWidth = 3, Color = 0x1B3A8C,
            Digital = new DigitalSigning("Max Mustermann · 28.10.2026 10:00"),
        };
        var signed = Core.Pdf.Signatures.PdfSigner.Sign(form, 0, ink, certificate, new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.FromHours(1)));
        using var signedDoc = PdfDocument.Open(signed);
        var saved = PageItem.From(signedDoc);
        var filled = saved[0].WithElements([.. saved[0].Elements.Select(e => e is ForeignAnnotation f && FieldSpec.Of(f)?.Code is not null ? f with { Value = "Lager 18" } : e)]);
        Assert.True(AppendedChanges.CanAppend(signedDoc, saved, [filled]));
        var appended = AppendedChanges.Apply(signedDoc, [filled], new PdfSaveOptions());
        Assert.Equal(signed, appended.AsSpan(0, signed.Length).ToArray());
        using var reopened = PdfDocument.Open(appended);
        var check = Assert.Single(Core.Pdf.Signatures.SignatureVerifier.Verify(reopened, null, [], CancellationToken.None));
        Assert.Equal(Core.Pdf.Signatures.SignatureOutcome.ValidIdentityUnknown, check.Outcome);
        Assert.Equal("Lager 18", Assert.Single(Codes(reopened)).Code.Text);
        var widget = PageItem.From(reopened)[0].Elements.OfType<ForeignAnnotation>().Single(w => FieldSpec.Of(w)?.Code is not null);
        Assert.Equal("Lager 18", FormField.Of(widget)!.StoredValue);
    }

    /// <summary>
    /// A signed form with a code field and /NeedAppearances (as other programs leave forms): filled in and appended, the update settles the
    /// appearances and drops the flag as writing anew does — otherwise other readers drew the code field anew as text (docs/FEHLERGEDAECHTNIS.md,
    /// No. 288). The signature stays valid, and the change counts as filling in the form.
    /// </summary>
    [Fact]
    public void SignedForm_WithNeedAppearances_CodeFieldFilledIn_FlagDroppedInTheUpdate()
    {
        using var doc = BlankPage.CreateDocument(595, 842, 1);
        var form = Save(doc, [PageItem.From(doc)[0].WithElements([Field(new CodeLook(), "Lager 17", new PdfRectangle(60, 600, 160, 700), "Lager")])]);
        // the flag set in an update of its own, before signing
        var flagged = new IncrementalUpdate(form);
        var catalog = IncrementalParts.Copy(form.Catalog!);
        var acroForm = IncrementalParts.Copy(form.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!);
        acroForm.Set(PdfName.Get("NeedAppearances"), PdfBoolean.True);
        if (form.Catalog.GetRaw(PdfName.Get("AcroForm")) is PdfReference formRef) flagged.Replace(formRef.Id, acroForm);
        else
        {
            catalog.Set(PdfName.Get("AcroForm"), acroForm);
            flagged.Replace(((PdfReference)form.Trailer.GetRaw(PdfName.Root)!).Id, catalog);
        }
        using var withFlag = new MemoryStream();
        flagged.WriteTo(withFlag);
        using var unsigned = PdfDocument.Open(withFlag.ToArray());
        Assert.True(unsigned.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetBoolean(PdfName.Get("NeedAppearances")));
        var certificate = Core.Platform.SigningCertificates.CreateOwn("Max Mustermann",
            Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"codefeld-na-{Guid.NewGuid():N}.p12"));
        var ink = new InkElement
        {
            Rect = new PdfRectangle(350, 100, 530, 160), Strokes = [[0, 20, 40, 50, 90, 10, 140, 45, 175, 25]], StrokeWidth = 3, Color = 0x1B3A8C,
            Digital = new DigitalSigning("Max Mustermann · 30.10.2026 10:00"),
        };
        var signed = Core.Pdf.Signatures.PdfSigner.Sign(unsigned, 0, ink, certificate, new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.FromHours(1)));
        using var signedDoc = PdfDocument.Open(signed);
        var saved = PageItem.From(signedDoc);
        var filled = saved[0].WithElements([.. saved[0].Elements.Select(e => e is ForeignAnnotation f && FieldSpec.Of(f)?.Code is not null ? f with { Value = "Lager 18" } : e)]);
        Assert.True(AppendedChanges.CanAppend(signedDoc, saved, [filled]));
        var appended = AppendedChanges.Apply(signedDoc, [filled], new PdfSaveOptions());
        Assert.Equal(signed, appended.AsSpan(0, signed.Length).ToArray());
        using var reopened = PdfDocument.Open(appended);
        Assert.False(reopened.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.ContainsKey(PdfName.Get("NeedAppearances")));
        var check = Assert.Single(Core.Pdf.Signatures.SignatureVerifier.Verify(reopened, null, [], CancellationToken.None));
        Assert.Equal(Core.Pdf.Signatures.SignatureOutcome.ValidIdentityUnknown, check.Outcome);
        Assert.Equal("Lager 18", Assert.Single(Codes(reopened)).Code.Text);
    }

    /// <summary>Crafted code entries: an unknown kind makes an ordinary (read-only) text field; absurd values draw at worst the placeholder.</summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void Crafted_CodeEntry_NeverThrows()
    {
        var pdf = TestPdf.Simple(1);
        PdfDictionary Widget(string kind, string value, double height = 100)
        {
            var code = TestPdf.Dict(("Codeart", TestPdf.N(kind)), ("Farbe", PdfInteger.Of(-1)), ("Gestalt", TestPdf.N("Kreis")), ("Gesperrt", PdfInteger.Of(7)));
            return TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("FT", TestPdf.N("Tx")), ("T", new PdfString("f"u8.ToArray())),
                ("Ff", PdfInteger.Of(1)), ("Rect", TestPdf.Box(10, 10, 300, 10 + height)), ("V", new PdfString(System.Text.Encoding.Latin1.GetBytes(value))),
                ("PDFKneadCode", code));
        }
        PdfDictionary[] crafted =
        [
            Widget("Unsinn", "x"),
            Widget("Qr", new string('A', 9_000)),
            Widget("Code128", new string('\u0001', 5_000)),
            Widget("Ean13", "4006381333931", height: 2),
            Widget("Itf", new string('9', 8_000)),
        ];
        var annots = new PdfArray();
        foreach (var widget in crafted) annots.Add(TestPdf.Ref(pdf.Add(widget)));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), annots);
        using var doc = PdfDocument.Open(pdf.Build());
        var widgets = PageItem.From(doc)[0].Elements.OfType<ForeignAnnotation>().ToList();
        Assert.Equal(5, widgets.Count);
        Assert.Null(FieldSpec.Of(widgets[0])!.Code);
        Assert.True(FieldSpec.Of(widgets[0])!.ReadOnly);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var widget in widgets.Skip(1))
        {
            Assert.NotNull(FieldSpec.Of(widget)!.Code);
            Assert.InRange(FieldSpec.Of(widget)!.Code!.Color, 0, 0xFFFFFF);
            _ = ElementAppearance.For(widget with { Value = FormField.Of(widget)!.StoredValue + "1" });
        }
        _ = Codes(doc);
        Assert.True(watch.ElapsedMilliseconds < 10_000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
    }
}
