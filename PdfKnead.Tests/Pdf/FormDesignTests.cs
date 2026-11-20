using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>Creating fields and editing their properties: script calls safe, saving and reading back.</summary>
public sealed class FormDesignTests
{
    private static readonly PdfName AcroFormKey = PdfName.Get("AcroForm");

    // Attempts to break out of the string literal of a script (common PDF readers execute the calls)
    public static TheoryData<string> Attacks =>
    [
        "dd.mm.yyyy\"); app.launchURL(\"http://example.com\"); (\"",
        "a'b\\\"c\\\\",
        "zeile\nzweite\r" + (char)0x2028 + (char)0x2029,
        "</script><script>alert(1)</script>",
        "\u0000\u001f\u007f€ä",
        "\\u0022); app.alert(1); //",
    ];

    [Theory]
    [MemberData(nameof(Attacks))]
    public void Quote_StaysOneStringLiteral(string attack)
    {
        var quoted = FieldScripts.Quote(attack);
        Assert.StartsWith("\"", quoted);
        Assert.EndsWith("\"", quoted);
        var inner = quoted[1..^1];
        Assert.DoesNotContain('"', inner);
        Assert.DoesNotContain('\'', inner);
        Assert.DoesNotContain('<', inner);
        Assert.All(inner, c => Assert.InRange(c, ' ', '~'));
        // Every backslash introduces exactly one \uXXXX sequence
        for (var i = inner.IndexOf('\\'); i >= 0; i = inner.IndexOf('\\', i + 6))
            Assert.Matches("^\\\\u[0-9A-F]{4}", inner[i..]);
        // … and common PDF readers (like the own reader) read back exactly the text
        var (format, _) = FieldScripts.ForFormat(new FieldFormat(FieldFormatKind.Date, Pattern: attack));
        Assert.Equal(attack, FieldFormat.Parse(format!).Pattern);
        var (number, _) = FieldScripts.ForFormat(new FieldFormat(FieldFormatKind.Number, 2, 2, Currency: attack));
        Assert.Equal(attack, FieldFormat.Parse(number!).Currency);
        var calc = FieldScripts.ForCalculation(new FieldCalculation(CalcOperation.Sum, ["Preis", attack]));
        Assert.Equal(["Preis", attack], FieldSpec.ParseCalculation(calc)!.Fields);
    }

    [Fact]
    public void Calculation_OnlyTheKnownCall_ElseCustom()
    {
        Assert.Equal(CalcOperation.Average, FieldSpec.ParseCalculation("AFSimple_Calculate(\"AVG\", new Array (\"a\", \"b\"));")!.Operation);
        Assert.Null(FieldSpec.ParseCalculation("AFSimple_Calculate(\"SUM\", new Array (\"a\")); app.launchURL(\"x\");"));
        Assert.Null(FieldSpec.ParseCalculation("event.value = 1;"));
        // Numbers only within limits
        var (script, _) = FieldScripts.ForFormat(new FieldFormat(FieldFormatKind.Number, 99, 77, -5));
        Assert.Equal("AFNumber_Format(10, 4, 0, 0, \"\", false);", script);
    }

    // Crafted scripts: regex backtracking, escapes that .NET rejects but JavaScript knows
    [Trait(TestKind.Name, TestKind.Time)]
    [Theory]
    [InlineData(10)]
    [InlineData(40)]
    public void Calculation_PreparedScript_ParsesFast(int names)
    {
        var script = "AFSimple_Calculate(\"SUM\", new Array (" + string.Concat(Enumerable.Repeat("\"a\"     ", names));   // without ")"
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(FieldSpec.ParseCalculation(script));
        Assert.True(watch.ElapsedMilliseconds < 200 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Scripts_JavaScriptEscapes_DoNotThrow()
    {
        Assert.Equal("\\q", FieldFormat.Parse("AFDate_FormatEx(\"\\\\q\");").Pattern);
        Assert.Equal("q", FieldFormat.Parse("AFDate_FormatEx(\"\\q\");").Pattern);   // in JavaScript "\q" is simply "q"
        Assert.Equal("dd.mm.yyyy x", FieldFormat.Parse("AFDate_FormatEx(\"dd.mm.yyyy\\x20x\");").Pattern);
        Assert.Equal(FieldFormatKind.Date, FieldFormat.Parse("AFDate_FormatEx(\"\\u12\");").Kind);
        Assert.Equal(["a\"b", "€"], FieldSpec.ParseCalculation("AFSimple_Calculate(\"SUM\", new Array (\"a\\\"b\", \"\\u20ac\"));")!.Fields);
        // Extra code after the standard call: a custom script (kept when saving)
        Assert.Equal(FieldFormatKind.None, FieldFormat.Parse("AFNumber_Format(2, 0, 0, 0, \"\", true); eigenerCode();").Kind);
    }

    // ------------------------------------------------------------ Formula

    [Theory]
    [InlineData("2 + 3 * 4", 14)]                 // multiplication before addition
    [InlineData("(2 + 3) * 4", 20)]               // parentheses
    [InlineData("10 - 4 - 3", 3)]                 // same precedence from the left
    [InlineData("100 / 10 / 5", 2)]
    [InlineData("10 - (4 - 3)", 9)]
    [InlineData("-2 * 3", -6)]                    // sign
    [InlineData("2 * -3", -6)]
    [InlineData("a - -b", 7)]
    [InlineData("-(a + b) * 2", -14)]
    [InlineData("a * a", 16)]                     // squaring
    [InlineData("((a + b) * (a - b)) / 7", 1)]
    [InlineData("1,5 * 2 + .5", 3.5)]             // comma and point
    [InlineData("Summe\\ 1 + N\\.0501", 12)]      // escaped names
    public void Formula_Evaluates(string text, double expected)
    {
        var values = new Dictionary<string, double> { ["a"] = 4, ["b"] = 3, ["Summe 1"] = 10, ["N.0501"] = 2 };
        var formula = FieldFormula.Parse(text, out var error, out _);
        Assert.True(formula is not null, error);
        Assert.Equal(expected, formula!.Evaluate(n => values.GetValueOrDefault(n)), 10);
        // Written uniformly and read back: the same calculation
        var again = FieldFormula.Parse(formula.Notation)!;
        Assert.Equal(formula.Notation, again.Notation);
        Assert.Equal(expected, again.Evaluate(n => values.GetValueOrDefault(n)), 10);
        // Cross-check with a JavaScript engine (without a PDF reader): scripts written to a file on request
        if (Environment.GetEnvironmentVariable("PDFKNEAD_JSPRUEFUNG") is { Length: > 0 } file)
            File.AppendAllText(file, expected.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t" + FieldScripts.ForCalculation(FieldCalculation.Of(formula)) + "\n");
    }

    [Theory]
    [InlineData("")]
    [InlineData("2 +")]
    [InlineData("(2 + 3")]
    [InlineData("2 + 3)")]
    [InlineData("a b")]
    [InlineData("2 ** 3")]
    [InlineData("a ^ 2")]
    [InlineData("a % b")]
    [InlineData("a == b")]
    [InlineData("1.2.3")]
    [InlineData("12abc")]
    [InlineData("a\\")]
    public void Formula_Errors_AreReported(string text)
    {
        Assert.Null(FieldFormula.Parse(text, out var error, out var at));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.InRange(at, 0, text.Length);
    }

    [Fact]
    public void Formula_ScriptInAfNotation_AndReadBack()
    {
        // This is how common PDF editors write "(1-Discount)*Average" (as documented in a public user forum): a comment with the formula, followed by the JavaScript
        const string written = "/** BVCALC (1-Discount)*Average EVCALC **/event.value=(1-AFMakeNumber(getField(\"Discount\").value))*AFMakeNumber(getField(\"Average\").value)";
        var read = FieldSpec.ParseCalculation(written)!;
        Assert.Equal(CalcOperation.Formula, read.Operation);
        Assert.Equal(["Discount", "Average"], read.Fields);
        Assert.Equal("(1 - Discount) * Average", read.Formula!.Notation);
        Assert.Equal(
            "/** BVCALC (1 - Discount) * Average EVCALC **/event.value=(1-AFMakeNumber(getField(\"Discount\").value))*AFMakeNumber(getField(\"Average\").value)",
            FieldScripts.ForCalculation(read));
        // Field with points (common PDF editors escape them) and several lines of JavaScript after it
        var dotted = FieldSpec.ParseCalculation("/** BVCALC N\\.0501+N\\.0505-N\\.0513 EVCALC **/event.value=AFMakeNumber(getField(\"N.0501\").value)+\nAFMakeNumber(getField(\"N.0505\").value)")!;
        Assert.Equal(["N.0501", "N.0505", "N.0513"], dotted.Fields);
        // Sign on the right: in JavaScript "a-(-b)", never "a--b" (operator --)
        Assert.EndsWith("event.value=AFMakeNumber(getField(\"a\").value)-(-AFMakeNumber(getField(\"b\").value))",
            FieldScripts.ForCalculation(FieldCalculation.Of(FieldFormula.Parse("a - -b")!)));
        // Custom scripts that merely start like this stay custom
        Assert.Null(FieldSpec.ParseCalculation("/** BVCALC a + EVCALC **/event.value=1"));
        Assert.Null(FieldSpec.ParseCalculation("/* BVCALC a EVCALC */event.value=1"));
    }

    [Theory]
    [MemberData(nameof(Attacks))]
    public void Formula_FieldNames_CannotBreakOut(string attack)
    {
        var formula = FieldFormula.Parse(FieldFormula.Escape(attack) + " * 2 + " + FieldFormula.Escape("x*/y"))!;
        Assert.Equal([attack, "x*/y"], formula.Fields);
        var script = FieldScripts.ForCalculation(FieldCalculation.Of(formula));
        // The first "*/" is the end of the comment (after it, names appear only in JavaScript strings, where they are harmless)
        var end = script.IndexOf("**/", StringComparison.Ordinal);
        Assert.Equal(end + 1, script.IndexOf("*/", StringComparison.Ordinal));
        Assert.Contains("getField(" + FieldScripts.Quote(attack) + ")", script);
        Assert.Equal(formula.Notation, FieldFormula.FromScript(script)!.Notation);
    }

    /// <summary>Random formulas from all characters that can occur: never an exception; whatever is read writes back the same.</summary>
    [Fact]
    public void Formula_RandomInput_NeverThrows()
    {
        var rng = new Random(4711);
        const string alphabet = "ab1.,+-*/() \\x_ä€\"'*/%^=\n";
        for (var i = 0; i < 20_000; i++)
        {
            var chars = new char[rng.Next(0, 40)];
            for (var k = 0; k < chars.Length; k++) chars[k] = alphabet[rng.Next(alphabet.Length)];
            var text = new string(chars);
            var formula = FieldFormula.Parse(text, out _, out _);
            _ = FieldFormula.FromScript("/** BVCALC " + text + " EVCALC **/event.value=0");
            if (formula is null) continue;
            Assert.Equal(formula.Notation, FieldFormula.Parse(formula.Notation)!.Notation);
            var script = formula.Script();
            Assert.Equal(script.IndexOf("**/", StringComparison.Ordinal) + 1, script.IndexOf("*/", StringComparison.Ordinal));
            _ = formula.Evaluate(_ => 1);
        }
    }

    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void Formula_PreparedInput_StaysFast()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(FieldFormula.Parse(new string('(', 10_000) + "1" + new string(')', 10_000)));
        Assert.Null(FieldFormula.Parse(string.Concat(Enumerable.Repeat("a + ", 5_000)) + "a"));
        Assert.Null(FieldFormula.FromScript("/** BVCALC " + new string('(', 50_000) + " EVCALC **/"));
        var long64 = FieldFormula.Parse(new string('(', 60) + "1" + new string(')', 60));
        Assert.NotNull(long64);
        Assert.True(watch.ElapsedMilliseconds < 500 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Formula_SavedAndReopened_InCalculationOrder()
    {
        var number = new FieldFormat(FieldFormatKind.Number, 2, 2);
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var item = PageItem.From(doc)[0];
        var formula = FieldFormula.Parse("(Menge * Preis - Rabatt) * 1.19")!;
        List<PageElement> elements =
        [
            .. item.Elements,
            new FieldElement { Rect = R(72, 700, 100, 20), Value = "3", Spec = new FieldSpec { Name = "Menge", Format = number } },
            new FieldElement { Rect = R(72, 670, 100, 20), Value = "10", Spec = new FieldSpec { Name = "Preis", Format = number } },
            new FieldElement { Rect = R(72, 640, 100, 20), Value = "5", Spec = new FieldSpec { Name = "Rabatt", Format = number } },
            new FieldElement { Rect = R(72, 610, 100, 20), Value = "29.75", Spec = new FieldSpec { Name = "Gesamt", Format = number, ReadOnly = true, Calculation = FieldCalculation.Of(formula) } },
        ];
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [item.WithElements(elements)], output);
        using var saved = PdfDocument.Open(output.ToArray());
        var acroForm = saved.Catalog!.GetDictionary(AcroFormKey)!;
        var total = PageItem.From(saved)[0].Elements.OfType<ForeignAnnotation>().Select(FormField.Of).First(f => f!.Name == "Gesamt")!;
        var spec = FieldSpec.Of(total, acroForm);
        Assert.Equal(formula.Notation, spec.Calculation!.Formula!.Notation);
        Assert.Null(spec.CustomCalculationScript);
        Assert.Same(total.Field, acroForm.GetArray(PdfName.Get("CO"))!.Get(0));
        var js = total.Field.GetDictionary(PdfName.Get("AA"))!.GetDictionary(PdfName.Get("C"))!.GetString(PdfName.Get("JS"))!.ToText();
        Assert.StartsWith("/** BVCALC (Menge * Preis - Rabatt) * 1.19 EVCALC **/event.value=", js);
    }

    /// <summary>
    /// Written like common PDF editors, because otherwise a widely used reader does not show changed numbers: fonts of the form as separate objects,
    /// /BS only with a border, black as "0 g". With PDFKNEAD_ABLAGE=&lt;folder&gt; the file is stored for a cross-check.
    /// </summary>
    [Fact]
    public void NewFields_WrittenLikeReferenceEditor()
    {
        var number = new FieldFormat(FieldFormatKind.Number, 2, 2);
        using var doc = PdfDocument.Open(TestPdf.Simple(1, title: null).Build());
        var item = PageItem.From(doc)[0];
        List<PageElement> elements =
        [
            new FieldElement { Rect = R(72, 700, 110, 20), Value = "1", Spec = new FieldSpec { Name = "Zahl1", Format = number, Alignment = ElementAlignment.Right } },
            new FieldElement { Rect = R(72, 670, 110, 20), Value = "3", Spec = new FieldSpec { Name = "Zahl2", Format = number, Alignment = ElementAlignment.Right } },
            new FieldElement { Rect = R(72, 640, 110, 20), Value = "4", Spec = new FieldSpec { Name = "Summe", Format = number, Alignment = ElementAlignment.Right, ReadOnly = true,
                Calculation = new FieldCalculation(CalcOperation.Sum, ["Zahl1", "Zahl2"]) } },
            new FieldElement { Rect = R(72, 610, 110, 20), Value = "3.57", Spec = new FieldSpec { Name = "Formel", Format = number, Alignment = ElementAlignment.Right, ReadOnly = true,
                Calculation = FieldCalculation.Of(FieldFormula.Parse("(Zahl1 * Zahl2 - 0.5) * 1.19")!) } },
            new FieldElement { Rect = R(72, 570, 110, 20), Value = "7", Spec = new FieldSpec { Name = "MitRahmen", Format = number, Alignment = ElementAlignment.Right, BorderColor = 0x808080 } },
        ];
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [item.WithElements(elements)], output);
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { Length: > 0 } folder)
            File.WriteAllBytes(Path.Combine(folder, "test3-neue-felder.pdf"), output.ToArray());
        using var saved = PdfDocument.Open(output.ToArray());
        var form = saved.Catalog!.GetDictionary(AcroFormKey)!;
        var fonts = form.GetDictionary(PdfName.Get("DR"))!.GetDictionary(PdfName.Get("Font"))!;
        Assert.All(fonts.Entries, e => Assert.IsType<PdfReference>(e.Value));
        var widgets = PageItem.From(saved)[0].Elements.OfType<ForeignAnnotation>().ToDictionary(w => FormField.Of(w)!.Name, w => w.Original);
        Assert.False(widgets["Zahl1"].ContainsKey(PdfName.Get("BS")));
        Assert.True(widgets["MitRahmen"].ContainsKey(PdfName.Get("BS")));
        Assert.EndsWith("Tf 0 g", widgets["Zahl1"].GetString(PdfName.Get("DA"))!.ToText());
    }

    private static PdfRectangle R(double l, double b, double w, double h) => new(l, b, l + w, b + h);

    [Fact]
    public void NewFields_SavedAndReopened_AsOrdinaryFields()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var item = PageItem.From(doc)[0];
        var radio = new FieldSpec { Kind = FormFieldKind.Radio, Name = "Zahlung", BorderColor = 0, FillColor = 0xFFFFFF };
        var elements = new List<PageElement>
        {
            new FieldElement
            {
                Rect = R(72, 700, 150, 20), Value = "18.10.2026",
                Spec = new FieldSpec { Name = "Datum", Tooltip = "Datum der Bestellung", Required = true, BorderColor = 0x1A4FA0, FontSize = 11,
                    Format = new FieldFormat(FieldFormatKind.Date, Pattern: "dd.mm.yyyy") },
            },
            new FieldElement { Rect = R(72, 660, 14, 14), Value = "Ja", Spec = new FieldSpec { Kind = FormFieldKind.CheckBox, Name = "AGB", CheckStyle = CheckStyle.Cross, BorderColor = 0 } },
            new FieldElement { Rect = R(72, 620, 14, 14), Value = "Off", Spec = radio with { ExportValue = "Bar" } },
            new FieldElement { Rect = R(172, 620, 14, 14), Value = "Karte", Spec = radio with { ExportValue = "Karte" } },
            new FieldElement
            {
                Rect = R(72, 580, 150, 20), Value = "at",
                Spec = new FieldSpec { Kind = FormFieldKind.ComboBox, Name = "Land", Options = [new("de", "Deutschland"), new("at", "Österreich")], Editable = true },
            },
            new FieldElement
            {
                Rect = R(72, 500, 150, 60), Value = "b",
                Spec = new FieldSpec { Kind = FormFieldKind.ListBox, Name = "Größe", Options = [new("a", "Klein"), new("b", "Mittel"), new("c", "Groß")] },
            },
            new FieldElement { Rect = R(72, 460, 150, 20), Value = "3", Spec = new FieldSpec { Name = "Menge", Format = new FieldFormat(FieldFormatKind.Number, 0, 2) } },
            new FieldElement
            {
                Rect = R(72, 430, 150, 20), Spec = new FieldSpec { Name = "Summe", ReadOnly = true,
                    Format = new FieldFormat(FieldFormatKind.Number, 2, 2, Currency: " €"), Calculation = new FieldCalculation(CalcOperation.Sum, ["Menge"]) },
            },
            new FieldElement
            {
                Rect = R(72, 390, 120, 24),
                Spec = new FieldSpec { Kind = FormFieldKind.PushButton, Name = "Leeren", Caption = "Zurücksetzen", ResetForm = true, FillColor = 0xC0C0C0,
                    BorderStyle = FieldBorderStyle.Beveled, BorderColor = 0x808080 },
            },
        };
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [item.WithElements(elements)], output);
        // Store for a cross-check with other readers
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { Length: > 0 } folder) File.WriteAllBytes(Path.Combine(folder, "neue-felder.pdf"), output.ToArray());
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.False(saved.WasRepaired);

        var acroForm = saved.Catalog!.GetDictionary(AcroFormKey)!;
        var widgets = PageItem.From(saved)[0].Elements.OfType<ForeignAnnotation>().ToList();
        Assert.Equal(elements.Count, widgets.Count);
        FormField Of(string name) => widgets.Select(FormField.Of).First(f => f!.Name == name)!;
        FieldSpec SpecOf(string name) => FieldSpec.Of(Of(name), acroForm);

        var date = SpecOf("Datum");
        Assert.Equal(("Datum der Bestellung", true, 0x1A4FA0, 11.0), (date.Tooltip, date.Required, date.BorderColor, date.FontSize));
        Assert.Equal(new FieldFormat(FieldFormatKind.Date, Pattern: "dd.mm.yyyy"), date.Format);
        Assert.Equal("18.10.2026", Of("Datum").StoredValue);

        Assert.Equal((FormFieldKind.CheckBox, "Ja", "Ja", CheckStyle.Cross), (Of("AGB").Kind, Of("AGB").State, Of("AGB").StoredValue, SpecOf("AGB").CheckStyle));
        // Radio group: one field, two widgets, "Karte" selected
        var payment = widgets.Where(w => FormField.Of(w)!.Name == "Zahlung").Select(w => FormField.Of(w)!).ToList();
        Assert.Equal(2, payment.Count);
        Assert.Same(payment[0].Field, payment[1].Field);
        Assert.Equal((FormFieldKind.Radio, "Karte"), (payment[0].Kind, payment[0].StoredValue));
        Assert.Equal(["Off", "Karte"], payment.Select(p => p.State));
        Assert.Equal(["Bar", "Karte"], payment.Select(p => p.OnState));

        Assert.Equal((FormFieldKind.ComboBox, "at", "Österreich", true), (Of("Land").Kind, Of("Land").StoredValue, Of("Land").DisplayText("at"), SpecOf("Land").Editable));
        Assert.Equal((FormFieldKind.ListBox, "b"), (Of("Größe").Kind, Of("Größe").StoredValue));
        Assert.Equal(new FieldCalculation(CalcOperation.Sum, ["Menge"]), SpecOf("Summe").Calculation);
        Assert.True(SpecOf("Summe").ReadOnly);
        Assert.Equal((FormFieldKind.PushButton, "Zurücksetzen", true), (Of("Leeren").Kind, SpecOf("Leeren").Caption, SpecOf("Leeren").ResetForm));

        // The calculation order names the calculated field; check box font in the default resources; 8 fields (the group once)
        var co = acroForm.GetArray(PdfName.Get("CO"))!;
        Assert.Single(co);
        Assert.Same(Of("Summe").Field, co.Get(0));
        Assert.NotNull(acroForm.GetDictionary(PdfName.Get("DR"))!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("ZaDb")));
        Assert.Equal(8, acroForm.GetArray(PdfName.Get("Fields"))!.Count);
        // Every widget has an appearance; check boxes have both states
        Assert.All(widgets, w => Assert.NotNull(ForeignAnnotations.Appearance(w.Original)));
        var agb = Of("AGB").Widget.GetDictionary(PdfName.Get("AP"))!.GetDictionary(PdfName.N)!;
        Assert.True(agb.ContainsKey(PdfName.Get("Ja")) && agb.ContainsKey(PdfName.Get("Off")));
    }

    [Fact]
    public void ForeignField_PropertiesChanged_SavedAndReopened()
    {
        using var doc = PdfDocument.Open(FormFillTests.AllKinds());
        var acroForm = doc.Catalog!.GetDictionary(AcroFormKey);
        var item = PageItem.From(doc)[0];
        var elements = item.Elements.Select(e =>
        {
            if (e is not ForeignAnnotation f || FormField.Of(f) is not { } field) return e;
            var spec = FieldSpec.Of(field, acroForm);
            return field.Name switch
            {
                "Name" => f with
                {
                    Rect = f.Rect with { Right = f.Rect.Right + 50 },
                    Spec = spec with { Tooltip = "Vor- und Nachname", Required = true, BorderColor = 0xC00000, FontSize = 14, Name = "Vollständiger Name" },
                    Value = "Max Mustermann",
                },
                "Zustimmung" => f with { Spec = spec with { CheckStyle = CheckStyle.Star, ExportValue = "Yes" }, Value = "Ja" },
                "Zahlung" => f with { Spec = spec with { Required = true } },
                "Land" => f with { Spec = spec with { Options = [.. spec.Options, new FieldOption("ch", "Schweiz")], Sort = true } },
                _ => e,
            };
        }).ToList();
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [item.WithElements(elements)], output);
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.False(saved.WasRepaired);
        var savedForm = saved.Catalog!.GetDictionary(AcroFormKey);
        var widgets = PageItem.From(saved)[0].Elements.OfType<ForeignAnnotation>().ToList();
        FormField Of(string name) => widgets.Select(FormField.Of).First(f => f!.Name == name)!;

        var name = FieldSpec.Of(Of("Vollständiger Name"), savedForm);
        Assert.Equal(("Vor- und Nachname", true, 0xC00000, 14.0), (name.Tooltip, name.Required, name.BorderColor, name.FontSize));
        Assert.Equal(0xE6E6FF, name.FillColor);   // taken over unchanged
        Assert.Equal("Max Mustermann", Of("Vollständiger Name").StoredValue);
        Assert.Equal(250, widgets.First(w => FormField.Of(w)!.Name == "Vollständiger Name").Rect.Width, 3);

        // New export value: state and value take this name, the symbol is a star
        var agree = Of("Zustimmung");
        Assert.Equal(("Yes", "Yes", "Yes", CheckStyle.Star), (agree.OnState, agree.State, agree.StoredValue, FieldSpec.Of(agree, savedForm).CheckStyle));
        // Property on the parent field of the group; both widgets keep their states
        Assert.All(widgets.Where(w => FormField.Of(w)!.Name == "Zahlung"), w => Assert.True(FieldSpec.Of(FormField.Of(w)!, savedForm).Required));
        Assert.Equal(["Bar", "Karte"], widgets.Where(w => FormField.Of(w)!.Name == "Zahlung").Select(w => FormField.Of(w)!.OnState));
        var land = FieldSpec.Of(Of("Land"), savedForm);
        Assert.Equal(["de", "at", "ch"], land.Options.Select(o => o.Export));
        Assert.True(land.Sort);
        // Unchanged field stays unchanged
        Assert.Equal("fest", Of("Gesperrt").StoredValue);
        Assert.Equal(7, savedForm!.GetArray(PdfName.Get("Fields"))!.Count);
    }

    /// <summary>
    /// Renaming foreign fields: /Opt of a check box (export values, as common PDF editors use them), a custom keystroke script and
    /// format/calculation of a choice list are kept.
    /// </summary>
    [Fact]
    public void ForeignField_Renamed_KeepsOptAndOwnScripts()
    {
        using var doc = PdfDocument.Open(FormFillTests.AllKinds());
        var item = PageItem.From(doc)[0];
        ForeignAnnotation Widget(string name) => item.Elements.OfType<ForeignAnnotation>().First(f => FormField.Of(f)?.Name == name);
        static PdfDictionary Js(string script) => FieldScripts.Action(script);
        var agree = Widget("Zustimmung").Original;
        agree.Set(PdfName.Get("Opt"), new PdfArray([new PdfString("Einverstanden"u8.ToArray())]));
        var aa = new PdfDictionary();
        aa.Set(PdfName.Get("K"), Js("if (event.change == \"x\") event.rc = false;"));
        Widget("Name").Original.Set(PdfName.Get("AA"), aa);
        var choiceAa = new PdfDictionary();
        choiceAa.Set(PdfName.Get("F"), Js("AFNumber_Format(2, 0, 0, 0, \"\", true);"));
        Widget("Land").Original.Set(PdfName.Get("AA"), choiceAa);

        var elements = item.Elements.Select(e => e is ForeignAnnotation f && FieldSpec.Of(f) is { } spec && spec.Name is "Zustimmung" or "Name" or "Land"
            ? f with { Spec = spec with { Name = spec.Name + "2", Required = true } }
            : e).ToList();
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [item.WithElements(elements)], output);
        using var saved = PdfDocument.Open(output.ToArray());
        var widgets = PageItem.From(saved)[0].Elements.OfType<ForeignAnnotation>().ToList();
        PdfDictionary Saved(string name) => widgets.Select(FormField.Of).First(f => f!.Name == name)!.Field;
        Assert.Equal("Einverstanden", ((PdfString)Saved("Zustimmung2").GetArray(PdfName.Get("Opt"))!.Get(0)).ToText());
        Assert.Contains("event.rc", Saved("Name2").GetDictionary(PdfName.Get("AA"))!.GetDictionary(PdfName.Get("K"))!.GetString(PdfName.Get("JS"))!.ToText());
        Assert.NotNull(Saved("Land2").GetDictionary(PdfName.Get("AA"))?.GetDictionary(PdfName.Get("F")));
    }

    /// <summary>A foreign field is renamed and a new one gets the old name: both keep their names.</summary>
    [Fact]
    public void ForeignField_Renamed_FreesItsOldName()
    {
        using var doc = PdfDocument.Open(FormFillTests.AllKinds());
        var item = PageItem.From(doc)[0];
        var elements = item.Elements.Select(e => e is ForeignAnnotation f && FieldSpec.Of(f) is { Name: "PLZ" } spec ? f with { Spec = spec with { Name = "Postleitzahl" } } : e).ToList();
        elements.Add(new FieldElement { Rect = R(300, 700, 100, 20), Value = "", Spec = new FieldSpec { Name = "PLZ" } });
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [item.WithElements(elements)], output);
        using var saved = PdfDocument.Open(output.ToArray());
        var names = PageItem.From(saved)[0].Elements.OfType<ForeignAnnotation>().Select(w => FormField.Of(w)!.Name).ToList();
        Assert.Contains("Postleitzahl", names);
        Assert.Contains("PLZ", names);
        Assert.DoesNotContain("PLZ1", names);
    }

    /// <summary>Export values with characters that must be escaped in PDF names: state, value and appearance match after saving.</summary>
    [Theory]
    [InlineData("Grün")]
    [InlineData("Preis in €")]
    [InlineData("a/b (c) #1 <x> [y] {z} %")]
    public void ExportValue_SpecialCharacters_StayConsistent(string export)
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var item = PageItem.From(doc)[0];
        var box = new FieldElement { Rect = R(72, 700, 14, 14), Value = export, Spec = new FieldSpec { Kind = FormFieldKind.CheckBox, Name = "Kästchen", ExportValue = export } };
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [item.WithElements([box])], output);
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.False(saved.WasRepaired);
        var field = FormField.Of(PageItem.From(saved)[0].Elements.OfType<ForeignAnnotation>().Single())!;
        Assert.NotNull(field.OnState);
        Assert.Equal(field.OnState, field.State);
        Assert.Equal(field.OnState, field.StoredValue);
        if (export.All(c => c <= 0xFF)) Assert.Equal(export, field.OnState);
    }

    /// <summary>List box with multiple selection: saved as an array of values and read back; also a foreign list box.</summary>
    [Fact]
    public void ListBox_MultiSelect_SavedAsArray()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var item = PageItem.From(doc)[0];
        var list = new FieldElement
        {
            Rect = R(72, 600, 150, 60), Value = "a\nc",
            Spec = new FieldSpec { Kind = FormFieldKind.ListBox, Name = "Farben", MultiSelect = true, Options = [new("a", "Rot"), new("b", "Grün"), new("c", "Blau")] },
        };
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [item.WithElements([list])], output);
        using var saved = PdfDocument.Open(output.ToArray());
        var widget = PageItem.From(saved)[0].Elements.OfType<ForeignAnnotation>().Single();
        var field = FormField.Of(widget)!;
        Assert.True(field.MultiSelect);
        Assert.IsType<PdfArray>(field.Field.Get(PdfName.Get("V")));
        Assert.Equal("a\nc", field.StoredValue);

        // Foreign list box filled in differently afterwards: again as an array
        var item2 = PageItem.From(saved)[0];
        using var output2 = new MemoryStream();
        PdfWriter.Write(saved, [item2.WithElements([.. item2.Elements.Select(e => e is ForeignAnnotation f ? f with { Value = "b\nc" } : e)])], output2);
        using var again = PdfDocument.Open(output2.ToArray());
        Assert.Equal("b\nc", FormField.Of(PageItem.From(again)[0].Elements.OfType<ForeignAnnotation>().Single())!.StoredValue);
    }

    [Fact]
    public void ForeignField_Edited_DropsXfa()
    {
        var bytes = FormFillTests.AllKinds();
        using var doc = PdfDocument.Open(bytes);
        doc.Catalog!.GetDictionary(AcroFormKey)!.Set(PdfName.Get("XFA"), new PdfString("<xdp/>"u8.ToArray()));
        var item = PageItem.From(doc)[0];
        var acroForm = doc.Catalog.GetDictionary(AcroFormKey);
        var elements = item.Elements.Select(e => e is ForeignAnnotation f && FormField.Of(f) is { Name: "Name" } field
            ? f with { Spec = FieldSpec.Of(field, acroForm) with { Tooltip = "neu" } }
            : e).ToList();
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [item.WithElements(elements)], output);
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.False(saved.Catalog!.GetDictionary(AcroFormKey)!.ContainsKey(PdfName.Get("XFA")));
    }
}
