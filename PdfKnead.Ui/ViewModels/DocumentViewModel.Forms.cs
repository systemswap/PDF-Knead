using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.ViewModels;

/// <summary>
/// Filling in forms: fields created by other programs, and new fields even before saving. The value belongs to the field; a field
/// can have several widgets (the same field on several pages, radio button groups), which change in one step. Simple calculations
/// (sum, product …) are evaluated at the same time, in the same step. Scripts never run.
/// </summary>
public sealed partial class DocumentViewModel
{
    /// <summary>The field currently being filled in via the input box (<see cref="EditingElement"/> is then a stand-in text).</summary>
    public ElementRef? FillingField { get; private set; }

    private bool _scriptHintShown;

    /// <summary>
    /// Whether fields may be filled in, signature fields signed included (ISO 32000-2, 7.6.4.2, Table 22): bit 6 („Kommentieren“), from
    /// revision 3 also bit 9 alone — protected forms often permit only that; bit 4 („Ändern“) alone does not. Moving, deleting and changing
    /// fields need <see cref="CanDesignForms"/>.
    /// </summary>
    public bool CanFillForms => CanAnnotate || Granted(Core.Pdf.Security.PdfPermissions.FillForms);

    /// <summary>
    /// Whether the element can be filled in (a field, not read-only, not a push button or signature). For foreign fields, changed
    /// properties apply immediately: with „Schreibgeschützt“ cleared the field is fillable at once, not only after saving, because the
    /// edited properties are consulted rather than only the file.
    /// </summary>
    /// <param name="element">The element to check.</param>
    /// <returns>true if it can be filled in.</returns>
    public static bool IsFillable(PageElement element) => element switch
    {
        ForeignAnnotation { Spec: { } spec } foreign => FormField.Of(foreign) is not null && !spec.ReadOnly &&
                                                       spec.Kind is not (FormFieldKind.PushButton or FormFieldKind.Signature),
        // a code field is read-only in the file for other readers; PDF Knead fills it in unless its own setting locks it
        ForeignAnnotation foreign when FieldSpec.Of(foreign) is { Code: not null } code => !code.ReadOnly,
        ForeignAnnotation foreign => FormField.Of(foreign) is { Fillable: true },
        FieldElement { Spec: { ReadOnly: false, Kind: not (FormFieldKind.PushButton or FormFieldKind.Signature) } } => true,
        _ => false,
    };

    /// <summary>On state of a check box or radio button (foreign: from its appearance, new: the export value).</summary>
    private static string? OnStateOf(PageElement element) => element switch
    {
        ForeignAnnotation foreign when FormField.Of(foreign) is { } field => foreign.Spec is { } spec && ElementAppearance.NeedsNewLook(foreign)
            ? ElementAppearance.OnStateOf(spec)
            : field.OnState,
        FieldElement field => ElementAppearance.OnStateOf(field.Spec),
        _ => null,
    };

    /// <summary>The element with a new value.</summary>
    private static PageElement WithValue(PageElement element, string value) => element switch
    {
        ForeignAnnotation foreign => foreign with { Value = value },
        FieldElement field => field with { Value = value },
        _ => element,
    };

    /// <summary>Fills in a text or choice field via the input box (combo box with custom text, typed date).</summary>
    /// <param name="target">The field.</param>
    public void BeginFill(ElementRef target)
    {
        // code fields are filled in their own box below the field (DocumentView.ShowCodeInput), never as text in place
        if (!CanFillForms || !IsFillable(target.Element) || SpecOf(target.Element) is not { Code: null } spec) return;
        var value = ValueOf(target.Element);
        var shown = spec.Kind == FormFieldKind.Text ? value   // numbers are edited raw (as in common PDF readers); the format is only for display
            : spec.Options.FirstOrDefault(o => o.Export == value)?.Display ?? value;
        var proxy = target.Element is ForeignAnnotation { Spec: null } foreign && FormField.Of(foreign) is { } field
            ? ElementAppearance.FieldText(foreign, field, shown).Text
            : ElementAppearance.FieldText(target.Element, spec, shown);
        FillingField = target;
        EditingIsNew = false;
        EditingElement = new ElementRef(target.Page, proxy);
        SelectedElement = target;
    }

    /// <summary>
    /// Font size at which the field would currently show <paramref name="text"/> (while typing): it adapts with „Automatisch“ and
    /// „Schrift automatisch verkleinern“, so the text already shrinks in the input box, not only afterwards.
    /// </summary>
    /// <param name="text">The text typed so far.</param>
    /// <returns>The font size in points, or null if no text field is being filled in.</returns>
    public double? FillFontSize(string text)
    {
        if (FillingField is not { } target || SpecOf(target.Element) is not { Kind: FormFieldKind.Text } spec) return null;
        var proxy = target.Element is ForeignAnnotation { Spec: null } foreign && FormField.Of(foreign) is { } field
            ? ElementAppearance.FieldText(foreign, field, text).Text
            : ElementAppearance.FieldText(target.Element, spec, text);
        return proxy.FontSize;
    }

    /// <summary>Takes over the typed text (from <see cref="EndEdit"/>), checked against the field's format and maximum length.</summary>
    private void EndFill(ElementRef target, string text)
    {
        if (SpecOf(target.Element) is not { } spec) return;
        if (!spec.Multiline || spec.Kind != FormFieldKind.Text) text = text.ReplaceLineEndings(" ");
        if (spec.MaxLength > 0 && text.Length > spec.MaxLength) text = text[..spec.MaxLength];
        if (spec.Kind == FormFieldKind.Text && spec.Format.Kind != FieldFormatKind.None)
        {
            if (spec.Format.Normalize(text) is not { } normalized)
            {
                Notify(Tr.T("„{0}“ passt nicht zum Format des Felds.", text));
                SelectedElement = target;
                return;
            }
            text = normalized;
        }
        // Choice: map the displayed text to the export value (custom text in editable fields stays as it is)
        if (spec.Kind is FormFieldKind.ComboBox or FormFieldKind.ListBox && spec.Options.FirstOrDefault(o => o.Display == text) is { } option)
            text = option.Export;
        SetFieldValue(target, text);
    }

    /// <summary>Sets the value of a text or choice field in all its widgets, in one step (including the calculations).</summary>
    /// <param name="target">The field.</param>
    /// <param name="value">The new value (export value for choice fields).</param>
    /// <returns>false if nothing changed or filling in is not allowed.</returns>
    public bool SetFieldValue(ElementRef target, string value)
    {
        if (!CanFillForms || !IsFillable(target.Element)) return false;
        var changes = new List<(int, PageElement, PageElement)>();
        ElementRef? selected = null;
        foreach (var widget in SameField(target))
        {
            if (ValueOf(widget.Element) == value) continue;   // already so: no empty step
            var updated = WithValue(widget.Element, value);
            changes.Add((widget.Page, widget.Element, updated));
            if (ReferenceEquals(widget.Element, target.Element)) selected = new ElementRef(widget.Page, updated);
        }
        return Apply(changes, selected ?? target);
    }

    /// <summary>Toggles a check box or chooses a radio button (in a group the others switch off), in one step.</summary>
    /// <param name="target">The check box or radio button.</param>
    /// <returns>false if nothing changed or filling in is not allowed.</returns>
    public bool ToggleField(ElementRef target)
    {
        if (!CanFillForms || !IsFillable(target.Element) || SpecOf(target.Element) is not { Kind: FormFieldKind.CheckBox or FormFieldKind.Radio } spec ||
            OnStateOf(target.Element) is not { } on) return false;
        var isOn = ValueOf(target.Element) == on;
        // Radio button: clicked again it stays on unless the group may be entirely off; a check box toggles
        if (isOn && spec.Kind == FormFieldKind.Radio && spec.NoToggleToOff) return false;
        var chosen = isOn ? "Off" : on;
        var changes = new List<(int, PageElement, PageElement)>();
        ElementRef? selected = null;
        foreach (var widget in SameField(target))
        {
            var state = chosen != "Off" && OnStateOf(widget.Element) == chosen ? chosen : "Off";
            if (ValueOf(widget.Element) == state) continue;
            var updated = WithValue(widget.Element, state);
            changes.Add((widget.Page, widget.Element, updated));
            if (ReferenceEquals(widget.Element, target.Element)) selected = new ElementRef(widget.Page, updated);
        }
        return Apply(changes, selected ?? target);
    }

    private bool Apply(List<(int Page, PageElement Old, PageElement Updated)> changes, ElementRef selected)
    {
        if (changes.Count == 0)
        {
            SelectedElement = selected;
            return false;
        }
        Recalculate(changes);
        if (!History.ReplaceElements(changes, Tr.T("Feld ausfüllen"))) return false;
        SelectedElement = selected;
        HintScripts(selected.Element);
        return true;
    }

    // ------------------------------------------------------------ Calculation

    /// <summary>
    /// Re-evaluates simple calculations (like the form-script function <c>AFSimple_Calculate</c>): every field with a calculation receives the sum,
    /// product, average, minimum or maximum of its source fields, or the result of its formula, using the values after
    /// <paramref name="changes"/>; the results go into the same step. Fields with their own script are not calculated (scripts never
    /// run). One pass in the calculation order (ISO 32000-2, 12.7.3, Table 224; docs/FEHLERGEDAECHTNIS.md, No. 578): the calculations of the
    /// file in their order in /CO, then those set or changed in PDF-Knead in the order of their dependencies — as they are written to /CO
    /// on saving. A calculation of the file that is not in /CO is not calculated (as in the file); that is said once.
    /// </summary>
    private void Recalculate(List<(int Page, PageElement Old, PageElement Updated)> changes)
    {
        var current = new Dictionary<PageElement, (int Page, PageElement Now)>(ReferenceEqualityComparer.Instance);
        foreach (var (page, old, updated) in changes) current[old] = (page, updated);
        PageElement Now(PageElement element) => current.TryGetValue(element, out var c) ? c.Now : element;

        var calculated = new List<(int Page, PageElement Element, FieldCalculation Calc)>();
        // All widgets per name: for check boxes and radio groups the one switched on counts (taking only the first widget gave 0)
        var byName = new Dictionary<string, List<PageElement>>(StringComparer.Ordinal);
        for (var page = 0; page < History.Count; page++)
            foreach (var element in History.Pages[page].Elements)
            {
                if (!IsFormField(element)) continue;
                var name = FieldNameOf(element);
                if (name.Length > 0)
                {
                    if (!byName.TryGetValue(name, out var widgets)) byName[name] = widgets = [];
                    widgets.Add(element);
                }
                if (SpecOf(element) is { Kind: FormFieldKind.Text, Calculation: { IsActive: true } calc, CustomCalculationScript: null })
                    calculated.Add((page, element, calc));
            }
        if (calculated.Count == 0) return;
        calculated = CalculationOrder(calculated);

        // Read the value per name once (cache, cleared for a name when its result changes); work is limited, otherwise a crafted file
        // with thousands of calculations over 1000 names each would block for minutes
        var cache = new Dictionary<string, double>(StringComparer.Ordinal);
        double Number(string name)
        {
            if (cache.TryGetValue(name, out var cached)) return cached;
            var value = 0.0;
            if (byName.TryGetValue(name, out var widgets))
            {
                var text = "";
                foreach (var widget in widgets)
                {
                    text = ValueOf(Now(widget));
                    if (text is not ("" or "Off")) break;
                }
                if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && double.IsFinite(v))
                    value = v;
            }
            return cache[name] = value;
        }
        var work = 0;
        const int MaxWork = 200_000;

        {
            foreach (var (page, element, calc) in calculated)
            {
                if ((work += calc.Formula?.Size ?? calc.Fields.Count) > MaxWork) break;
                var values = new List<double>(calc.Fields.Count);
                if (calc.Formula is null)
                    foreach (var source in calc.Fields) values.Add(Number(source));
                var result = calc.Operation switch
                {
                    CalcOperation.Formula => calc.Formula!.Evaluate(Number),   // operator precedence, parentheses
                    CalcOperation.Product => values.Aggregate(1.0, (a, b) => a * b),
                    CalcOperation.Average => values.Count > 0 ? values.Average() : 0,
                    CalcOperation.Minimum => values.Count > 0 ? values.Min() : 0,
                    CalcOperation.Maximum => values.Count > 0 ? values.Max() : 0,
                    _ => values.Sum(),
                };
                var text = double.IsFinite(result) ? Math.Round(result, 10).ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture) : "";
                var before = Now(element);
                if (ValueOf(before) == text) continue;
                var updated = WithValue(before, text);
                current[element] = (page, updated);
                cache.Remove(FieldNameOf(element));
            }
        }
        changes.Clear();
        foreach (var (old, (page, now)) in current) changes.Add((page, old, now));
    }

    private bool _calculationOrderHintShown;

    /// <summary>
    /// The calculated fields in calculation order (see <see cref="Recalculate"/>): first those whose calculation comes from the file, by their
    /// place in /CO; then those whose calculation was set or changed here (own fields, fields of the file with a changed calculation), by
    /// their dependencies. Calculations of the file outside /CO are left out (and said once). A field of the file counts as „set here“ only
    /// if its calculation changed — other changed properties (font, required …) leave it where the file has it; the writer orders /CO the
    /// same way (docs/FEHLERGEDAECHTNIS.md, No. 588).
    /// </summary>
    private List<(int Page, PageElement Element, FieldCalculation Calc)> CalculationOrder(List<(int Page, PageElement Element, FieldCalculation Calc)> calculated)
    {
        // full names of the fields in /CO, with their place
        var places = new Dictionary<string, int>(StringComparer.Ordinal);
        if (AcroForm?.GetArray(PdfName.Get("CO")) is { } co)
            for (var i = 0; i < Math.Min(co.Count, 10_000); i++)
                if (co.Get(i) is PdfDictionary field && QualifiedName(field) is { Length: > 0 } name) places.TryAdd(name, i);
        var fromFile = new List<(int Place, (int Page, PageElement Element, FieldCalculation Calc) Entry)>();
        var setHere = new List<(int Page, PageElement Element, FieldCalculation Calc)>();
        string? outside = null;
        foreach (var entry in calculated)
        {
            if (entry.Element is ForeignAnnotation foreign && (foreign.Spec is null || Equals(foreign.Spec.Calculation, FieldSpec.Of(foreign)?.Calculation)))
            {
                if (places.TryGetValue(FieldNameOf(foreign), out var place)) fromFile.Add((place, entry));
                else outside ??= FieldNameOf(foreign);
            }
            else setHere.Add(entry);
        }
        if (outside is not null && !_calculationOrderHintShown)
        {
            _calculationOrderHintShown = true;
            Notify(Tr.T("Das Feld „{0}“ hat eine Berechnung, steht aber nicht in der Berechnungsreihenfolge der Datei — es wird nicht berechnet.", outside));
        }
        return [.. fromFile.OrderBy(f => f.Place).Select(f => f.Entry),
            .. FieldCalculation.DependencyOrder(setHere, e => FieldNameOf(e.Element), e => e.Calc)];

        static string QualifiedName(PdfDictionary field)
        {
            var parts = new List<string>();
            var current = field;
            for (var depth = 0; depth < 32 && current is not null; depth++)
            {
                if (current.Get(PdfName.Get("T")) is PdfString t) parts.Add(t.ToText());
                current = current.Get(PdfName.Parent) as PdfDictionary;
            }
            parts.Reverse();
            return string.Join('.', parts);
        }
    }

    /// <summary>
    /// Next fillable field (Tab) or previous one (Shift+Tab), in the order of the pages and their annotations; without a starting
    /// point, the first one from the current page on. At the end it wraps around.
    /// </summary>
    /// <param name="from">The current field, or null.</param>
    /// <param name="backwards">true = previous field.</param>
    /// <returns>The field, or null if the document has no fillable field.</returns>
    public ElementRef? NextField(ElementRef? from, bool backwards)
    {
        var fields = new List<ElementRef>();
        for (var page = 0; page < History.Count; page++)
            foreach (var element in History.Pages[page].Elements)
                if (IsFillable(element)) fields.Add(new ElementRef(page, element));
        if (fields.Count == 0) return null;
        var at = from is { } f ? fields.FindIndex(r => r.Page == f.Page && ReferenceEquals(r.Element, f.Element)) : -1;
        if (at < 0)
        {
            var start = CurrentPage?.Index ?? 0;
            var first = fields.FindIndex(r => r.Page >= start);
            return fields[first < 0 ? 0 : first];
        }
        return fields[((at + (backwards ? -1 : 1)) % fields.Count + fields.Count) % fields.Count];
    }

    /// <summary>
    /// Once per document: notes that the form has its own scripts, which PDF-Knead does not run (custom formats or calculations). The
    /// standard formats (date, number …) and simple calculations are reimplemented, so they get no notice.
    /// </summary>
    private void HintScripts(PageElement element)
    {
        if (_scriptHintShown || element is not ForeignAnnotation || SpecOf(element) is not { } spec) return;
        var calculatedByScript = AcroForm?.GetArray(PdfName.Get("CO")) is { Count: > 0 } && spec.CustomCalculationScript is not null;
        if (spec.CustomFormatScript is null && !calculatedByScript && !HasCustomCalculations()) return;
        _scriptHintShown = true;
        Notify(Tr.T("Dieses Formular enthält Skripte (etwa Berechnungen). PDF-Knead führt keine Skripte aus — berechnete Felder rechnen nicht nach."));
    }

    /// <summary>Whether a field of the document has its own calculation script (not one of the simple ones PDF-Knead re-evaluates).</summary>
    private bool HasCustomCalculations()
    {
        if (AcroForm?.GetArray(PdfName.Get("CO")) is not { Count: > 0 }) return false;
        for (var page = 0; page < History.Count; page++)
            foreach (var element in History.Pages[page].Elements)
                if (element is ForeignAnnotation && SpecOf(element) is { CustomCalculationScript: not null }) return true;
        return false;
    }
}
