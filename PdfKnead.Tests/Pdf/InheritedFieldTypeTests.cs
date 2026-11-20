using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The field type of a signature field is inheritable in any depth (ISO 32000-2:2020, 12.7.4.1; docs/ABLAUFPLAN.md, 2.1, A-7 and G1 N3): a
/// signature in a kid whose parent is the /FT /Sig field is found, checked and counted (it was not — the file counted as unsigned and was
/// rewritten without asking, the signature broken); a prepared field two levels below its type can be signed; a kid without its own /T is a
/// widget of its field (erratum EC3 #28).
/// </summary>
public sealed class InheritedFieldTypeTests
{
    private static readonly Lazy<SigningCertificate> Own = new(() =>
        SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"geerbt-{Guid.NewGuid():N}.p12")));

    private static InkElement Signature(string field) => new()
    {
        Rect = new PdfRectangle(50, 100, 250, 180),
        Strokes = [[0, 20, 40, 70, 90, 10, 140, 60]],
        StrokeWidth = 3,
        Digital = new DigitalSigning("Max Mustermann", FieldName: field),
    };

    private static byte[] Sign(byte[] file, string field)
    {
        using var doc = PdfDocument.Open(file);
        return PdfSigner.Sign(doc, 0, Signature(field), Own.Value, DateTimeOffset.Now);
    }

    private static PdfDictionary Widget(TestPdf pdf, int parent, string? title)
    {
        var widget = TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("Rect", TestPdf.Box(40, 90, 260, 190)),
            ("F", PdfInteger.Of(4)), ("Parent", TestPdf.Ref(parent)), ("P", TestPdf.Ref(5)));
        if (title is not null) widget.Set(PdfName.Get("T"), new PdfString(System.Text.Encoding.ASCII.GetBytes(title)));
        return widget;
    }

    /// <summary>A page 1 with the field tree <paramref name="top"/> and the widget hung onto the page.</summary>
    private static void Hang(TestPdf pdf, int top, int widget)
    {
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(widget)]));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new PdfArray([TestPdf.Ref(top)]))));
    }

    [Fact]
    public void InheritedFieldType_SignatureFoundAndChecked()
    {
        var pdf = TestPdf.Simple(1);
        var parent = pdf.Add(TestPdf.Dict(("FT", TestPdf.N("Sig")), ("T", new PdfString("Unterschriften"u8.ToArray()))));
        var kid = pdf.Add(Widget(pdf, parent, "A"));
        ((PdfDictionary)pdf.Get(parent)).Set(PdfName.Get("Kids"), new PdfArray([TestPdf.Ref(kid)]));
        Hang(pdf, parent, kid);
        var signed = Sign(pdf.Build(), "Unterschriften.A");
        using var doc = PdfDocument.Open(signed);
        Assert.IsType<PdfDictionary>(((PdfDictionary)doc.GetObject(kid)).Get(PdfName.Get("V")));   // signed into the kid
        Assert.Single(DigitalSignatures.Find(doc));
        Assert.Equal(1, DigitalSignatures.CountIntact(doc));   // a full rewrite asks first (S0)
        var check = Assert.Single(SignatureVerifier.Verify(doc, null, [], CancellationToken.None));
        Assert.True(SignatureTexts.IsValid(check), check.Problem);
        Assert.Equal("Unterschriften.A", check.FieldName);
    }

    [Fact]
    public void GrandparentFieldType_EmptyFieldSignable()
    {
        var pdf = TestPdf.Simple(1);
        var top = pdf.Add(TestPdf.Dict(("FT", TestPdf.N("Sig")), ("T", new PdfString("Unterschriften"u8.ToArray()))));
        var middle = pdf.Add(TestPdf.Dict(("T", new PdfString("Abteilung"u8.ToArray())), ("Parent", TestPdf.Ref(top))));
        var widget = pdf.Add(Widget(pdf, middle, "Leitung"));
        ((PdfDictionary)pdf.Get(top)).Set(PdfName.Get("Kids"), new PdfArray([TestPdf.Ref(middle)]));
        ((PdfDictionary)pdf.Get(middle)).Set(PdfName.Get("Kids"), new PdfArray([TestPdf.Ref(widget)]));
        Hang(pdf, top, widget);
        using var doc = PdfDocument.Open(Sign(pdf.Build(), "Unterschriften.Abteilung.Leitung"));
        Assert.IsType<PdfDictionary>(((PdfDictionary)doc.GetObject(widget)).Get(PdfName.Get("V")));   // the prepared field, no new one
        var check = Assert.Single(SignatureVerifier.Verify(doc, null, [], CancellationToken.None));
        Assert.True(SignatureTexts.IsValid(check), check.Problem);
        Assert.Equal("Unterschriften.Abteilung.Leitung", check.FieldName);
    }

    /// <summary>A kid without /T is a widget of its field (erratum EC3 #28): signing by the field's name signs the field (counter-check).</summary>
    [Fact]
    public void KidWithoutTitle_IsWidgetOfField()
    {
        var pdf = TestPdf.Simple(1);
        var field = pdf.Add(TestPdf.Dict(("FT", TestPdf.N("Sig")), ("T", new PdfString("Unterschrift"u8.ToArray()))));
        var widget = pdf.Add(Widget(pdf, field, null));
        ((PdfDictionary)pdf.Get(field)).Set(PdfName.Get("Kids"), new PdfArray([TestPdf.Ref(widget)]));
        Hang(pdf, field, widget);
        using var doc = PdfDocument.Open(Sign(pdf.Build(), "Unterschrift"));
        Assert.IsType<PdfDictionary>(((PdfDictionary)doc.GetObject(field)).Get(PdfName.Get("V")));
        Assert.True(SignatureTexts.IsValid(Assert.Single(SignatureVerifier.Verify(doc, null, [], CancellationToken.None))));
    }
}
