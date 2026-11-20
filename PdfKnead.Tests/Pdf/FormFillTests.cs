using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>Filling in form fields of other programs: value, appearance, saving and reopening.</summary>
public sealed class FormFillTests
{
    private static int Form(TestPdf pdf, string content, double w, double h) =>
        pdf.Add(TestPdf.FlateStream(Encoding.ASCII.GetBytes(content),
            TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, w, h)))));

    private static PdfDictionary Widget(TestPdf pdf, double l, double b, double r, double t, params (string Key, PdfObject Value)[] more)
    {
        var entries = new List<(string, PdfObject)>
        {
            ("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("Rect", TestPdf.Box(l, b, r, t)), ("F", PdfInteger.Of(4)),
        };
        entries.AddRange(more);
        return TestPdf.Dict([.. entries]);
    }

    private static PdfString S(string text) => new(PdfTextEncoding.Encode(text));

    /// <summary>
    /// Form with all field kinds: Name (text, background and border), PLZ (comb field, 5 boxes), Bemerkung (multi-line),
    /// Zustimmung (check box), Zahlung (radio group: two widgets in the parent field), Land (combo box with export values),
    /// Gesperrt (read-only).
    /// </summary>
    internal static byte[] AllKinds()
    {
        var pdf = TestPdf.Simple(1);
        const int page = 5;
        var empty = Form(pdf, "", 200, 20);
        var name = pdf.Add(Widget(pdf, 72, 700, 272, 720, ("FT", TestPdf.N("Tx")), ("T", S("Name")), ("DA", S("/Helv 0 Tf 0 g")),
            ("MK", TestPdf.Dict(("BG", TestPdf.Box(0.9, 0.9, 1)), ("BC", TestPdf.Box(0, 0, 0)))), ("AP", TestPdf.Dict(("N", TestPdf.Ref(empty))))));
        var zip = pdf.Add(Widget(pdf, 72, 660, 172, 680, ("FT", TestPdf.N("Tx")), ("T", S("PLZ")), ("DA", S("/Cour 12 Tf 0 0 1 rg")),
            ("Ff", PdfInteger.Of(1 << 24)), ("MaxLen", PdfInteger.Of(5)), ("AP", TestPdf.Dict(("N", TestPdf.Ref(empty))))));
        var note = pdf.Add(Widget(pdf, 72, 560, 372, 640, ("FT", TestPdf.N("Tx")), ("T", S("Bemerkung")), ("DA", S("/Helv 10 Tf 0 g")),
            ("Ff", PdfInteger.Of(1 << 12)), ("AP", TestPdf.Dict(("N", TestPdf.Ref(empty))))));
        var yes = Form(pdf, "0 g 2 2 12 12 re f", 16, 16);
        var off = Form(pdf, "0 G 1 1 14 14 re S", 16, 16);
        var agree = pdf.Add(Widget(pdf, 72, 520, 88, 536, ("FT", TestPdf.N("Btn")), ("T", S("Zustimmung")), ("V", TestPdf.N("Off")),
            ("AS", TestPdf.N("Off")), ("AP", TestPdf.Dict(("N", TestPdf.Dict(("Ja", TestPdf.Ref(yes)), ("Off", TestPdf.Ref(off))))))));
        var payment = pdf.Add(TestPdf.Dict(("FT", TestPdf.N("Btn")), ("T", S("Zahlung")), ("Ff", PdfInteger.Of(1 << 15)), ("V", TestPdf.N("Off"))));
        var cash = pdf.Add(Widget(pdf, 72, 480, 88, 496, ("Parent", TestPdf.Ref(payment)), ("AS", TestPdf.N("Off")),
            ("AP", TestPdf.Dict(("N", TestPdf.Dict(("Bar", TestPdf.Ref(yes)), ("Off", TestPdf.Ref(off))))))));
        var card = pdf.Add(Widget(pdf, 172, 480, 188, 496, ("Parent", TestPdf.Ref(payment)), ("AS", TestPdf.N("Off")),
            ("AP", TestPdf.Dict(("N", TestPdf.Dict(("Karte", TestPdf.Ref(yes)), ("Off", TestPdf.Ref(off))))))));
        ((PdfDictionary)pdf.Get(payment)).Set(PdfName.Get("Kids"), new PdfArray([TestPdf.Ref(cash), TestPdf.Ref(card)]));
        var country = pdf.Add(Widget(pdf, 72, 430, 272, 450, ("FT", TestPdf.N("Ch")), ("T", S("Land")), ("Ff", PdfInteger.Of(1 << 17)),
            ("DA", S("/Helv 11 Tf 0 g")), ("Opt", new PdfArray([new PdfArray([S("de"), S("Deutschland")]), new PdfArray([S("at"), S("Österreich")])])),
            ("AP", TestPdf.Dict(("N", TestPdf.Ref(empty))))));
        var locked = pdf.Add(Widget(pdf, 72, 390, 272, 410, ("FT", TestPdf.N("Tx")), ("T", S("Gesperrt")), ("Ff", PdfInteger.Of(1)),
            ("V", S("fest")), ("AP", TestPdf.Dict(("N", TestPdf.Ref(empty))))));
        ((PdfDictionary)pdf.Get(page)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(name), TestPdf.Ref(zip), TestPdf.Ref(note),
            TestPdf.Ref(agree), TestPdf.Ref(cash), TestPdf.Ref(card), TestPdf.Ref(country), TestPdf.Ref(locked)]));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(
            ("Fields", new PdfArray([TestPdf.Ref(name), TestPdf.Ref(zip), TestPdf.Ref(note), TestPdf.Ref(agree), TestPdf.Ref(payment),
                TestPdf.Ref(country), TestPdf.Ref(locked)])),
            ("DA", S("/Helv 0 Tf 0 g"))));
        return pdf.Build();
    }

    /// <summary>
    /// A text field whose /MK points to a damaged object (arrays nested 300 deep): filled in or moved, it draws nothing instead of
    /// throwing — the PdfLimitException broke off building its look (found by fuzzing, docs/FEHLERGEDAECHTNIS.md, No. 185).
    /// </summary>
    [Fact]
    public void DamagedFieldObject_FilledAndMoved_DrawsNothing()
    {
        var pdf = TestPdf.Simple(1);
        // the writer of the tests refuses such nesting itself: a placeholder of the same length, replaced in the finished bytes
        var placeholder = "(" + new string('x', 598) + ")";
        var broken = pdf.Add(new PdfString(System.Text.Encoding.ASCII.GetBytes(new string('x', 598))));
        var field = pdf.Add(Widget(pdf, 72, 700, 272, 720, ("FT", TestPdf.N("Tx")), ("T", S("Name")), ("DA", S("/Helv 10 Tf 0 g")),
            ("MK", TestPdf.Ref(broken))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(field)]));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new PdfArray([TestPdf.Ref(field)]))));
        var bytes = pdf.Build();
        var at = bytes.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(placeholder));
        System.Text.Encoding.ASCII.GetBytes(new string('[', 300) + new string(']', 300)).CopyTo(bytes, at);
        using var doc = PdfDocument.Open(bytes);
        var foreign = Assert.Single(PageItem.From(doc)[0].Elements.OfType<ForeignAnnotation>());
        var filled = foreign with { Value = "Max Mustermann" };
        var appearance = ElementAppearance.For(filled);
        Assert.Empty(doc.GetStreamData(appearance.Form));
        Assert.NotNull(ElementAppearance.Moved(filled, new PdfRectangle(80, 600, 280, 620)));
    }

    private static ForeignAnnotation Field(PageItem item, string name) =>
        item.Elements.OfType<ForeignAnnotation>().First(f => FormField.Of(f) is { } field && field.Name == name);

    [Fact]
    public void Fields_KindsAndProperties()
    {
        using var doc = PdfDocument.Open(AllKinds());
        var item = PageItem.From(doc)[0];
        FormField Of(string name) => FormField.Of(Field(item, name))!;
        Assert.Equal(FormFieldKind.Text, Of("Name").Kind);
        Assert.True(Of("PLZ").Comb);
        Assert.Equal(5, Of("PLZ").MaxLength);
        Assert.True(Of("Bemerkung").Multiline);
        Assert.Equal(FormFieldKind.CheckBox, Of("Zustimmung").Kind);
        Assert.Equal("Ja", Of("Zustimmung").OnState);
        var radios = item.Elements.OfType<ForeignAnnotation>().Where(f => FormField.Of(f)?.Name == "Zahlung").ToList();
        Assert.Equal(2, radios.Count);
        Assert.All(radios, r => Assert.Equal(FormFieldKind.Radio, FormField.Of(r)!.Kind));
        Assert.Equal(["Bar", "Karte"], radios.Select(r => FormField.Of(r)!.OnState));
        Assert.Equal(FormFieldKind.ComboBox, Of("Land").Kind);
        Assert.Equal("Österreich", Of("Land").DisplayText("at"));
        Assert.False(Of("Gesperrt").Fillable);
        Assert.Equal("fest", Of("Gesperrt").StoredValue);
    }

    /// <summary>
    /// A rich text field filled in anew: its old rich text value (/RV) leaves the file — it contradicted the new /V (PDF/UA-2 8.10.3.3),
    /// readers that show rich text showed the old value, and the overwritten name stayed (docs/FEHLERGEDAECHTNIS.md, No. 445, Analyse J9).
    /// </summary>
    [Fact]
    public void RichTextField_FilledIn_OldRichValueGone()
    {
        var pdf = TestPdf.Simple(1);
        var field = pdf.Add(Widget(pdf, 72, 700, 372, 720, ("FT", TestPdf.N("Tx")), ("T", S("Name")), ("DA", S("/Helv 10 Tf 0 g")),
            ("Ff", PdfInteger.Of(1 << 25)), ("V", S("Max Mustermann")),
            ("RV", S("<?xml version=\"1.0\"?><body xmlns=\"http://www.w3.org/1999/xhtml\"><p><b>Max Mustermann</b></p></body>")),
            ("DS", S("font: Helvetica 10pt"))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(field)]));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new PdfArray([TestPdf.Ref(field)])),
            ("DR", TestPdf.Dict(("Font", TestPdf.Dict(("Helv", TestPdf.Ref(3))))))));
        using var doc = PdfDocument.Open(pdf.Build());
        var item = PageItem.From(doc)[0];
        var elements = item.Elements.Select(e => e is ForeignAnnotation f && FormField.Of(f) is { Name: "Name" } ? f with { Value = "Erika Mustermann" } : e).ToList();
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [item.WithElements(elements)], output);
        using var saved = PdfDocument.Open(output.ToArray());
        var written = Field(PageItem.From(saved)[0], "Name");
        Assert.Equal("Erika Mustermann", FormField.Of(written)!.StoredValue);
        Assert.DoesNotContain("Max Mustermann", RedactionTests.Everything(saved));
        Assert.Contains("font: Helvetica", RedactionTests.Everything(saved));   // the default style stays
    }

    [Fact]
    public void Filled_SavedAndReopened_ValuesAndPicturesAreThere()
    {
        using var doc = PdfDocument.Open(AllKinds());
        var item = PageItem.From(doc)[0];
        var values = new Dictionary<string, string> { ["Name"] = "Max Mustermann", ["PLZ"] = "12345", ["Bemerkung"] = "Erste Zeile\nZweite Zeile", ["Land"] = "at" };
        var elements = item.Elements.Select(e => e switch
        {
            ForeignAnnotation f when FormField.Of(f) is { } field && values.TryGetValue(field.Name, out var v) => f with { Value = v },
            ForeignAnnotation f when FormField.Of(f) is { Name: "Zustimmung" } => f with { Value = "Ja" },
            ForeignAnnotation f when FormField.Of(f) is { Name: "Zahlung" } field => f with { Value = field.OnState == "Karte" ? "Karte" : "Off" },
            _ => e,
        }).ToList();
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [item.WithElements(elements)], output);
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.False(saved.WasRepaired);

        var again = PageItem.From(saved)[0];
        FormField Of(string name) => FormField.Of(Field(again, name))!;
        Assert.Equal("Max Mustermann", Of("Name").StoredValue);
        Assert.Equal("12345", Of("PLZ").StoredValue);
        Assert.Equal("Erste Zeile\nZweite Zeile", Of("Bemerkung").StoredValue);
        Assert.Equal("at", Of("Land").StoredValue);
        Assert.Equal("Ja", Of("Zustimmung").StoredValue);
        Assert.Equal("Ja", Of("Zustimmung").State);
        Assert.Equal("Karte", Of("Zahlung").StoredValue);
        Assert.Equal(["Off", "Karte"], again.Elements.OfType<ForeignAnnotation>().Where(f => FormField.Of(f)?.Name == "Zahlung").Select(f => FormField.Of(f)!.State));
        Assert.Equal("fest", Of("Gesperrt").StoredValue);

        // Other readers see the values in the appearance of the fields (text of the page including annotations)
        var text = PageText.Create(PageInterpreter.Build(saved, saved.Pages[0]).Text).Text;
        Assert.Contains("Max Mustermann", text);
        Assert.Contains("Österreich", text);
        Assert.Contains("Zweite Zeile", text);
        foreach (var digit in "12345") Assert.Contains(digit, text);
        // Fields stay fillable: the same fields in the form, no empty entries
        var fields = saved.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!;
        Assert.Equal(7, fields.Count);
        Assert.All(Enumerable.Range(0, fields.Count), i => Assert.IsType<PdfDictionary>(fields.Get(i)));
    }

    [Theory]
    [InlineData("AFNumber_Format(2, 2, 0, 0, \" €\", false);", "1234,5", "1234.5", "1.234,50 €")]
    [InlineData("AFNumber_Format(2, 0, 0, 0, \"$\", true);", "1,234.5", "1234.5", "$1,234.50")]
    [InlineData("AFNumber_Format(0, 1, 2, 0, \"\", false);", "-42", "-42", "(42)")]
    [InlineData("AFPercent_Format(1, 2);", "12,5%", "0.125", "12,5%")]
    [InlineData("AFDate_FormatEx(\"dd.mm.yyyy\");", "17.10.2026", "17.10.2026", "17.10.2026")]
    [InlineData("AFDate_FormatEx(\"dd.mm.yyyy\");", "2026-10-17", "17.10.2026", "17.10.2026")]
    [InlineData("AFDate_FormatEx(\"mmmm d, yyyy\");", "10/17/2026", "October 17, 2026", "October 17, 2026")]
    [InlineData("AFDate_Format(2);", "17.10.2026", "10/17/26", "10/17/26")]
    [InlineData("AFTime_FormatEx(\"HH:MM\");", "9:05", "09:05", "09:05")]
    [InlineData("AFSpecial_Format(2);", "(030) 123-4567", "0301234567", "(030) 123-4567")]
    public void Formats_StandardCallsAreReadNotRun(string script, string input, string stored, string shown)
    {
        var format = FieldFormat.Parse(script);
        Assert.NotEqual(FieldFormatKind.None, format.Kind);
        Assert.Equal(stored, format.Normalize(input));
        Assert.Equal(shown, format.Display(stored));
    }

    [Fact]
    public void Formats_UnknownScriptsAndBadInput()
    {
        Assert.Equal(FieldFormatKind.None, FieldFormat.Parse("event.value = util.printf('%.2f', event.value);").Kind);
        Assert.Null(FieldFormat.Parse("AFDate_FormatEx(\"dd.mm.yyyy\");").Normalize("kein Datum"));
        Assert.Null(FieldFormat.Parse("AFNumber_Format(2, 2, 0, 0, \"\", false);").Normalize("abc"));
        Assert.Null(FieldFormat.Parse("AFSpecial_Format(0);").Normalize("123"));
        Assert.Equal("", FieldFormat.Parse("AFDate_FormatEx(\"dd.mm.yyyy\");").Normalize("  "));   // clearing always works
        // Crafted (fuzzing): huge numbers in the call must not overflow
        var huge = FieldFormat.Parse("AFNumber_Format(99999999999, 99999999999, 99999999999, 0, \"\", false);");
        Assert.Equal(FieldFormatKind.Number, huge.Kind);
        Assert.NotNull(huge.Display("1234.5"));
        Assert.Equal(FieldFormatKind.None, FieldFormat.Parse("AFDate_Format(99999999999);").Kind);
    }

    [Fact]
    public void Comb_LigaturesStayApart()
    {
        // The shaper would join "fi" and "ffl" into one glyph; in a comb field every letter belongs in its own box
        var pdf = TestPdf.Simple(1);
        var comb = pdf.Add(Widget(pdf, 72, 660, 172, 680, ("FT", TestPdf.N("Tx")), ("T", S("Kamm")), ("DA", S("/TiRo 12 Tf 0 g")),
            ("Ff", PdfInteger.Of(1 << 24)), ("MaxLen", PdfInteger.Of(5)), ("AP", TestPdf.Dict(("N", TestPdf.Ref(Form(pdf, "", 100, 20)))))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(comb)]));
        using var doc = PdfDocument.Open(pdf.Build());
        var item = PageItem.From(doc)[0];
        var zip = Field(item, "Kamm") with { Value = "fiffl" };
        var glyphs = PageInterpreter.BuildAnnotations(ElementAppearance.Document, item.Page, [ElementAppearance.For(zip).Annotation]).Text;
        Assert.Equal("fiffl", string.Concat(glyphs.Select(g => g.Text)));
        Assert.Equal(5, glyphs.Length);
    }

    [Fact]
    public void Comb_OneCharacterPerBox()
    {
        using var doc = PdfDocument.Open(AllKinds());
        var item = PageItem.From(doc)[0];
        var zip = Field(item, "PLZ") with { Value = "12345" };
        var glyphs = PageInterpreter.BuildAnnotations(ElementAppearance.Document, item.Page, [ElementAppearance.For(zip).Annotation]).Text;
        Assert.Equal(5, glyphs.Length);
        // Boxes 20 points wide (100 / 5): the centres of the characters are 20 points apart, in the first box at 82
        var centers = glyphs.Select(g => (g.X0 + g.X1) / 2).ToArray();
        Assert.Equal(82, centers[0], 1.5);
        for (var i = 1; i < 5; i++) Assert.Equal(20, centers[i] - centers[i - 1], 1.5);
    }
}
