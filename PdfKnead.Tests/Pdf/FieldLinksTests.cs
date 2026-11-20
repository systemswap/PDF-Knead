using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Linked fields in the file (KONZEPT.md, section 5.4a): fields of one kind with the same name are one field with several widgets; a widget
/// separated from its field becomes a field itself — also in forms of other programs.
/// </summary>
public sealed class FieldLinksTests
{
    private static PdfRectangle R(double l, double b, double w, double h) => new(l, b, l + w, b + h);

    private static PdfDocument Save(PdfDocument doc, IReadOnlyList<PageItem> items, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output);
        // PDFKNEAD_FELDER_ABLAGE: the files written, to look at
        if (Environment.GetEnvironmentVariable("PDFKNEAD_FELDER_ABLAGE") is { Length: > 0 } folder)
            File.WriteAllBytes(Path.Combine(folder, $"{name}-{doc.PageCount}-{items[0].Elements.Count}.pdf"), output.ToArray());
        var saved = PdfDocument.Open(output.ToArray());
        Assert.False(saved.WasRepaired);
        return saved;
    }

    /// <summary>The fields of the saved file: per widget its field and full name.</summary>
    private static List<FormField> Fields(PdfDocument doc) =>
        [.. PageItem.From(doc).SelectMany(p => p.Elements.OfType<ForeignAnnotation>()).Select(w => FormField.Of(w)!)];

    /// <summary>The names in /AcroForm /Fields.</summary>
    private static List<string> TopNames(PdfDocument doc)
    {
        var list = doc.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!;
        return [.. Enumerable.Range(0, list.Count).Select(i => ((PdfDictionary)list.Get(i)).GetString(PdfName.Get("T"))!.ToText())];
    }

    [Fact]
    public void TwoNewFieldsOfOneName_OneFieldWithTwoWidgets()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(2).Build());
        var items = PageItem.From(doc);
        var spec = new FieldSpec { Name = "Ort" };
        items = [items[0].WithElements([new FieldElement { Rect = R(72, 700, 150, 20), Value = "Köln", Spec = spec }]),
                 items[1].WithElements([new FieldElement { Rect = R(72, 400, 150, 20), Value = "Köln", Spec = spec }])];
        using var saved = Save(doc, items);
        var fields = Fields(saved);
        Assert.Equal(2, fields.Count);
        Assert.All(fields, f => Assert.Equal("Ort", f.Name));
        Assert.Same(fields[0].Field, fields[1].Field);
        Assert.Equal("Köln", ((PdfString)fields[0].Field.Get(PdfName.Get("V"))).ToText());
        Assert.Equal(["Ort"], TopNames(saved));
    }

    [Fact]
    public void NewFieldNamedLikeAFieldOfTheDocument_JoinsIt()
    {
        using var doc = PdfDocument.Open(FormFillTests.AllKinds());
        var item = PageItem.From(doc)[0];
        var plz = item.Elements.OfType<ForeignAnnotation>().First(f => FormField.Of(f)?.Name == "PLZ");
        var value = ElementAppearance.ValueOf(plz, FormField.Of(plz)!);
        var elements = item.Elements.Append(new FieldElement { Rect = R(300, 700, 100, 20), Value = value, Spec = FieldSpec.Of(plz)! }).ToList();
        using var saved = Save(doc, [item.WithElements(elements)]);
        var plzs = Fields(saved).Where(f => f.Name == "PLZ").ToList();
        Assert.Equal(2, plzs.Count);
        Assert.Same(plzs[0].Field, plzs[1].Field);
        Assert.DoesNotContain("PLZ1", Fields(saved).Select(f => f.Name));
        Assert.Single(TopNames(saved), n => n == "PLZ");
    }

    [Fact]
    public void FieldOfTheDocumentRenamedToAnother_TheyAreLinked()
    {
        using var doc = PdfDocument.Open(FormFillTests.AllKinds());
        var item = PageItem.From(doc)[0];
        var elements = item.Elements.Select(e => e is ForeignAnnotation f && FieldSpec.Of(f) is { Name: "Bemerkung" } spec ? f with { Spec = spec with { Name = "Name" } } : e).ToList();
        using var saved = Save(doc, [item.WithElements(elements)]);
        var names = Fields(saved).Where(f => f.Name == "Name").ToList();
        Assert.Equal(2, names.Count);
        Assert.Same(names[0].Field, names[1].Field);
        Assert.DoesNotContain("Bemerkung", TopNames(saved));
        Assert.Single(TopNames(saved), n => n == "Name");
    }

    [Fact]
    public void WidgetSeparatedFromItsField_BecomesAFieldItself_TheValueStays()
    {
        // first a field with two widgets, as linked new fields are written
        using var first = PdfDocument.Open(TestPdf.Simple(1).Build());
        var spec = new FieldSpec { Name = "Ort" };
        using var linked = Save(first, [PageItem.From(first)[0].WithElements(
            [new FieldElement { Rect = R(72, 700, 150, 20), Value = "Bonn", Spec = spec }, new FieldElement { Rect = R(72, 600, 150, 20), Value = "Bonn", Spec = spec }])]);
        var item = PageItem.From(linked)[0];
        var widgets = item.Elements.OfType<ForeignAnnotation>().ToList();
        Assert.Equal(2, widgets.Count);
        // the second one separated: a name of its own
        var elements = item.Elements.Select(e => ReferenceEquals(e, widgets[1]) ? widgets[1] with { Spec = FieldSpec.Of(widgets[1])! with { Name = "Ort_2" } } : e).ToList();
        using var saved = Save(linked, [item.WithElements(elements)]);
        var fields = Fields(saved);
        Assert.Equal(["Ort", "Ort_2"], fields.Select(f => f.Name).Order().ToList());
        Assert.NotSame(fields[0].Field, fields[1].Field);
        Assert.All(fields, f => Assert.Equal("Bonn", ((PdfString)f.Field.Get(PdfName.Get("V"))).ToText()));
        Assert.Equal(["Ort", "Ort_2"], TopNames(saved).Order().ToList());
        // the field of the document stays the field of the widget that keeps its name
        Assert.True(fields.Single(f => f.Name == "Ort").Field.ContainsKey(PdfName.Get("Kids")));
    }

    [Fact]
    public void DifferentKinds_AreNotLinked()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var item = PageItem.From(doc)[0];
        using var saved = Save(doc, [item.WithElements(
            [new FieldElement { Rect = R(72, 700, 150, 20), Value = "x", Spec = new FieldSpec { Name = "Feld" } },
             new FieldElement { Rect = R(72, 600, 14, 14), Value = "Off", Spec = new FieldSpec { Kind = FormFieldKind.CheckBox, Name = "Feld" } }])]);
        var fields = Fields(saved);
        Assert.Equal(2, fields.Count);
        Assert.NotSame(fields[0].Field, fields[1].Field);
        Assert.Equal(2, TopNames(saved).Distinct().Count());
    }

    [Fact(Timeout = 20_000)]
    public async Task ParentsInACircle_RenamedWidget_SavedInTime()
    {
        // a damaged form: the field's parent has the field as its parent; a widget separated from it must not loop over the chain
        await Task.Run(() =>
        {
            var pdf = TestPdf.Simple(1);
            var top = pdf.Add(TestPdf.Dict(("T", new PdfString("Kreis"u8.ToArray()))));
            var field = pdf.Add(TestPdf.Dict(("FT", TestPdf.N("Tx")), ("T", new PdfString("Ort"u8.ToArray())), ("Parent", TestPdf.Ref(top))));
            ((PdfDictionary)pdf.Get(top)).Set(PdfName.Get("Parent"), TestPdf.Ref(field));
            int Widget(double y) => pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("Rect", TestPdf.Box(72, y, 222, y + 20)),
                ("P", TestPdf.Ref(5)), ("Parent", TestPdf.Ref(field))));
            var w1 = Widget(700);
            var w2 = Widget(600);
            ((PdfDictionary)pdf.Get(field)).Set(PdfName.Get("Kids"), new PdfArray([TestPdf.Ref(w1), TestPdf.Ref(w2)]));
            ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(w1), TestPdf.Ref(w2)]));
            ((PdfDictionary)pdf.Get(1)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new PdfArray([TestPdf.Ref(top)]))));
            using var doc = PdfDocument.Open(pdf.Build());
            var item = PageItem.From(doc)[0];
            var widgets = item.Elements.OfType<ForeignAnnotation>().ToList();
            if (widgets.Count < 2 || FieldSpec.Of(widgets[1]) is not { } spec) return;   // not read as fields: nothing to separate
            var elements = item.Elements.Select(e => ReferenceEquals(e, widgets[1]) ? widgets[1] with { Spec = spec with { Name = spec.Name + "_2" } } : e).ToList();
            using var output = new MemoryStream();
            PdfWriter.Write(doc, [item.WithElements(elements)], output);
            using var saved = PdfDocument.Open(output.ToArray());
            Assert.Equal(2, PageItem.From(saved)[0].Elements.OfType<ForeignAnnotation>().Count());
        });
    }

    /// <summary>
    /// Two fields of one name that are each a field of their own (as merged forms bring them), one a comb of twenty cells, the other a box of one
    /// character: linked into one field, each keeps how it shows its value — length and flags stay on the widget that differs
    /// (docs/FEHLERGEDAECHTNIS.md, No. 343).
    /// </summary>
    [Fact]
    public void SameNamedFieldsOfDifferentLength_Linked_EachKeepsItsLook()
    {
        var pdf = Simple(1, title: null);
        var comb = pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("Widget")), ("Rect", Box(72, 700, 272, 720)), ("F", PdfInteger.Of(4)),
            ("FT", N("Tx")), ("T", new PdfString("Nr"u8.ToArray())), ("Ff", PdfInteger.Of(1 << 24)), ("MaxLen", PdfInteger.Of(20)),
            ("DA", new PdfString("/Helv 12 Tf 0 g"u8.ToArray())), ("P", Ref(5))));
        var box = pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("Widget")), ("Rect", Box(72, 600, 87, 620)), ("F", PdfInteger.Of(4)),
            ("FT", N("Tx")), ("T", new PdfString("Nr"u8.ToArray())), ("MaxLen", PdfInteger.Of(1)), ("Q", PdfInteger.Of(1)),
            ("DA", new PdfString("/Helv 12 Tf 0 g"u8.ToArray())), ("P", Ref(5))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([Ref(comb), Ref(box)]));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), Dict(("Fields", new PdfArray([Ref(comb), Ref(box)]))));
        using var doc = PdfDocument.Open(pdf.Build());
        var item = PageItem.From(doc)[0];
        using var saved = Save(doc, [item.WithElements([.. item.Elements.Select(e => e is ForeignAnnotation f ? f with { Value = "7" } : e)])]);
        var fields = Fields(saved).OrderByDescending(f => f.Widget.GetArray(PdfName.Get("Rect"))!.GetNumber(1)).ToList();
        Assert.Same(fields[0].Field, fields[1].Field);
        Assert.True(fields[0].Comb);
        Assert.Equal(20, fields[0].MaxLength);
        Assert.False(fields[1].Comb);
        Assert.Equal(1, fields[1].MaxLength);
        Assert.All(fields, f => Assert.Equal("7", f.StoredValue));
    }

    /// <summary>
    /// A text field needs its text style (/DA) as the field, inherited or from the form (ISO 32000-2, 12.7.4.3): the new field of a linked
    /// group takes it from its first widget — before, only the widgets had it (docs/FEHLERGEDAECHTNIS.md, No. 365).
    /// </summary>
    [Fact]
    public void LinkedTextFields_TheFieldHasATextStyle()
    {
        var pdf = Simple(1, title: null);
        var first = pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("Widget")), ("Rect", Box(72, 700, 272, 720)), ("F", PdfInteger.Of(4)),
            ("FT", N("Tx")), ("T", new PdfString("Ort"u8.ToArray())), ("DA", new PdfString("/Helv 12 Tf 0 g"u8.ToArray())), ("P", Ref(5))));
        var second = pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("Widget")), ("Rect", Box(72, 600, 272, 620)), ("F", PdfInteger.Of(4)),
            ("FT", N("Tx")), ("T", new PdfString("Ort"u8.ToArray())), ("DA", new PdfString("/Helv 10 Tf 0 g"u8.ToArray())), ("P", Ref(5))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([Ref(first), Ref(second)]));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), Dict(("Fields", new PdfArray([Ref(first), Ref(second)]))));
        using var doc = PdfDocument.Open(pdf.Build());
        var item = PageItem.From(doc)[0];
        using var saved = Save(doc, [item]);
        var fields = Fields(saved).OrderByDescending(f => f.Widget.GetArray(PdfName.Get("Rect"))!.GetNumber(1)).ToList();
        Assert.Same(fields[0].Field, fields[1].Field);
        Assert.NotSame(fields[0].Field, fields[0].Widget);
        var da = PdfName.Get("DA");
        var style = FormField.Inherited(fields[0].Field, da) ?? saved.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.Get(da);
        Assert.Equal("/Helv 12 Tf 0 g", (style as PdfString)?.ToText());
        Assert.Equal("/Helv 10 Tf 0 g", (FormField.Inherited(fields[1].Widget, da) as PdfString)?.ToText());   // each widget keeps its own

        // new fields of one name, too
        using var plain = PdfDocument.Open(TestPdf.Simple(1).Build());
        var spec = new FieldSpec { Name = "Ort" };
        var own = PageItem.From(plain)[0].WithElements([new FieldElement { Rect = R(72, 700, 150, 20), Spec = spec },
            new FieldElement { Rect = R(72, 400, 150, 20), Spec = spec }]);
        using var savedOwn = Save(plain, [own]);
        var ownFields = Fields(savedOwn);
        Assert.Same(ownFields[0].Field, ownFields[1].Field);
        Assert.NotNull(FormField.Inherited(ownFields[0].Field, da) ?? savedOwn.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.Get(da) as PdfString);
    }

    /// <summary>
    /// A check box of several widgets — each with a state of its own — whose widgets also carry a value (as some programs write it): checked one,
    /// every widget reads the value of the field, also those written after the one that is on (docs/FEHLERGEDAECHTNIS.md, No. 343).
    /// </summary>
    [Fact]
    public void CheckBoxWidgetsCarryingAValue_Filled_AllReadTheFieldsValue()
    {
        var pdf = Simple(1, title: null);
        var field = pdf.Add(new PdfDictionary());
        var kids = new PdfArray();
        foreach (var (state, i) in new[] { "A", "B", "C" }.Select((s, i) => (s, i)))
        {
            int Look(string content) => pdf.Add(new PdfStream(Dict(("Type", N("XObject")), ("Subtype", N("Form")), ("BBox", Box(0, 0, 10, 10))),
                System.Text.Encoding.ASCII.GetBytes(content)));
            kids.Add(Ref(pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("Widget")), ("Rect", Box(72 + 30 * i, 700, 82 + 30 * i, 710)),
                ("F", PdfInteger.Of(4)), ("Parent", Ref(field)), ("AS", N("Off")), ("V", N("Off")), ("P", Ref(5)),
                ("AP", Dict(("N", Dict(("Off", Ref(Look("0 G 1 1 8 8 re S"))), (state, Ref(Look("0 g 2 2 6 6 re f")))))))))));
        }
        pdf.Replace(field, Dict(("FT", N("Btn")), ("T", new PdfString("Stand"u8.ToArray())), ("Kids", kids)));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), kids);
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), Dict(("Fields", new PdfArray([Ref(field)]))));
        using var doc = PdfDocument.Open(pdf.Build());
        var item = PageItem.From(doc)[0];
        using var saved = Save(doc, [item.WithElements([.. item.Elements.Select(e => e is ForeignAnnotation f && FormField.Of(f) is { } ff
            ? f with { Value = ff.OnState == "B" ? "B" : "Off" } : e)])]);
        var fields = Fields(saved);
        Assert.Equal(3, fields.Count);
        Assert.All(fields, f => Assert.Equal("B", f.StoredValue));
        Assert.Equal(["Off", "B", "Off"], fields.OrderBy(f => f.Widget.GetArray(PdfName.Get("Rect"))!.GetNumber(0)).Select(f => f.State));
    }
}

