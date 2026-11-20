using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfKnead.Core.Pdf.Editing;

namespace PdfKnead.ViewModels;

/// <summary>A list entry with a German label (for the shared template in the properties panel).</summary>
public interface ILabeled
{
    /// <summary>German label (shown translated).</summary>
    string Label { get; }
}

/// <summary>A field linked to the selected one, in the list of the properties panel: the field and where it is.</summary>
public sealed record LinkedFieldRow(ElementRef Field, string Text);

/// <summary>A choice in a list of the properties panel: value and German label (shown translated).</summary>
public sealed record Choice<T>(T Value, string Label) : ILabeled;

/// <summary>A colour in the properties panel; <see cref="Rgb"/> null = none.</summary>
public sealed record FieldColorOption(int? Rgb, string Label) : ILabeled
{
    public Avalonia.Media.IBrush Brush { get; } = Rgb is { } c
        ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c))
        : Avalonia.Media.Brushes.Transparent;

    /// <summary>Whether this is a custom colour (colour picker, from the file); shown in the list with its colour code so that several can be told apart.</summary>
    public bool IsCustom => Label == "Eigene Farbe";

    public string Code => Rgb is { } c ? $"#{c:X6}" : "";
}

/// <summary>
/// Properties of the selected form fields, in the right-hand panel in „Formular bearbeiten“ mode. Shows the field selected last;
/// changes apply to all selected ones (field-level properties to all of a field's widgets). Each change is one step.
/// </summary>
public sealed partial class FieldPropertiesViewModel : ObservableObject
{
    private DocumentViewModel? _doc;
    private FieldSpec _spec = new();
    private PageElement? _element;
    private int _count;
    private bool _refreshing;

    public static IReadOnlyList<Choice<FieldVisibility>> Visibilities { get; } =
    [
        new(FieldVisibility.Visible, "Sichtbar"),
        new(FieldVisibility.Hidden, "Ausgeblendet"),
        new(FieldVisibility.NoPrint, "Sichtbar, nicht drucken"),
        new(FieldVisibility.PrintOnly, "Ausgeblendet, aber drucken"),
    ];

    public static IReadOnlyList<Choice<FieldBorderStyle>> BorderStyles { get; } =
    [
        new(FieldBorderStyle.Solid, "Durchgezogen"),
        new(FieldBorderStyle.Dashed, "Gestrichelt"),
        new(FieldBorderStyle.Beveled, "Abgeschrägt"),
        new(FieldBorderStyle.Inset, "Eingelassen"),
        new(FieldBorderStyle.Underline, "Unterstrichen"),
    ];

    public static IReadOnlyList<Choice<double>> BorderWidths { get; } = [new(1, "Dünn"), new(2, "Mittel"), new(3, "Dick")];

    public static IReadOnlyList<FieldColorOption> Colors { get; } =
    [
        new(null, "Keine"),
        new(0x000000, "Schwarz"),
        new(0x8C8C8C, "Grau"),
        new(0xD4D4D4, "Hellgrau"),
        new(0xFFFFFF, "Weiß"),
        new(0x1A4FA0, "Blau"),
        new(0xE6EEFF, "Hellblau"),
        new(0xC00000, "Rot"),
        new(0x1E7B34, "Grün"),
        new(0xFFF6CC, "Hellgelb"),
    ];

    public static IReadOnlyList<Choice<double>> FontSizes { get; } =
        [new(0, "Automatisch"), .. new double[] { 6, 7, 8, 9, 10, 11, 12, 14, 16, 18, 20, 24 }.Select(s => new Choice<double>(s, s.ToString(CultureInfo.InvariantCulture)))];

    public static IReadOnlyList<Choice<CheckStyle>> CheckStyles { get; } =
    [
        new(CheckStyle.Check, "Häkchen"),
        new(CheckStyle.Cross, "Kreuz"),
        new(CheckStyle.Circle, "Kreis"),
        new(CheckStyle.Diamond, "Raute"),
        new(CheckStyle.Square, "Quadrat"),
        new(CheckStyle.Star, "Stern"),
    ];

    public static IReadOnlyList<Choice<FieldFormatKind>> FormatKinds { get; } =
    [
        new(FieldFormatKind.None, "Keins"),
        new(FieldFormatKind.Number, "Zahl"),
        new(FieldFormatKind.Percent, "Prozent"),
        new(FieldFormatKind.Date, "Datum"),
        new(FieldFormatKind.Time, "Uhrzeit"),
        new(FieldFormatKind.Special, "Spezial (US)"),
    ];

    public static IReadOnlyList<string> DatePatterns { get; } = ["dd.mm.yyyy", "d.m.yyyy", "dd.mm.yy", "yyyy-mm-dd", "mm/dd/yyyy", "d. mmmm yyyy", "mmmm yyyy"];

    public static IReadOnlyList<string> TimePatterns { get; } = ["HH:MM", "HH:MM:ss", "h:MM tt"];

    public static IReadOnlyList<Choice<int>> Separators { get; } =
        [new(2, "1.234,56"), new(3, "1234,56"), new(0, "1,234.56"), new(1, "1234.56"), new(4, "1'234.56")];

    public static IReadOnlyList<Choice<int>> Specials { get; } =
        [new(0, "Postleitzahl (US)"), new(1, "Postleitzahl + 4 (US)"), new(2, "Telefonnummer (US)"), new(3, "Sozialversicherungsnummer (US)")];

    public static IReadOnlyList<Choice<CalcOperation?>> Operations { get; } =
    [
        new(null, "Keine"),
        new(CalcOperation.Sum, "Summe (+)"),
        new(CalcOperation.Product, "Produkt (×)"),
        new(CalcOperation.Average, "Mittelwert"),
        new(CalcOperation.Minimum, "Minimum"),
        new(CalcOperation.Maximum, "Maximum"),
        new(CalcOperation.Formula, "Formel"),
    ];

    /// <summary>All German labels of the lists (for the translation check).</summary>
    public static IEnumerable<string> AllLabels =>
        Visibilities.Select(c => c.Label).Concat(BorderStyles.Select(c => c.Label)).Concat(BorderWidths.Select(c => c.Label))
            .Concat(Colors.Select(c => c.Label)).Concat(FontSizes.Select(c => c.Label)).Concat(CheckStyles.Select(c => c.Label))
            .Concat(FormatKinds.Select(c => c.Label)).Concat(Separators.Select(c => c.Label)).Concat(Specials.Select(c => c.Label))
            .Concat(Operations.Select(c => c.Label)).Concat(DecimalChoices.Select(c => c.Label));

    /// <summary>Rereads the properties (selection, undo, another document).</summary>
    /// <param name="doc">The current document, or null.</param>
    public void Refresh(DocumentViewModel? doc)
    {
        _doc = doc;
        var selection = doc is { IsFieldDesign: true } ? doc.FieldsOfSelection.Where(r => DocumentViewModel.IsFormField(r.Element)).ToList() : [];
        _count = selection.Count;
        _element = selection.Count > 0 ? selection[^1].Element : null;
        _selected = selection.Count > 0 ? selection[^1] : null;
        _anyCode = selection.Any(r => doc!.SpecOf(r.Element)?.Code is not null);
        _codes = _anyCode && selection.All(r => doc!.SpecOf(r.Element)?.Code is not null);
        _spec = _element is not null && doc!.SpecOf(_element) is { } spec ? spec : new FieldSpec();
        _kinds = [.. selection.Select(r => doc!.SpecOf(r.Element)?.Kind ?? FormFieldKind.Text).Distinct()];
        _allText = selection.All(r => doc!.SpecOf(r.Element) is { HasText: true, Code: null });   // a code field draws no text
        _ownSignature = selection.Any(r => r.Element is SignatureFieldElement);
        _others = selection.Count == 1 ? doc!.OthersOfField(selection[0]) : [];
        _refreshing = true;
        try
        {
            OnPropertyChanged(string.Empty);
        }
        finally
        {
            _refreshing = false;
        }
        StartCodeCheck();
    }

    /// <summary>Whether the panel is docked on the right: always in „Formular bearbeiten“ mode (otherwise the page would jump in width with every selection).</summary>
    public bool IsVisible => _doc is { IsFormDesign: true };

    /// <summary>
    /// Whether the panel floats over the page on the right, like the other panels of „Bearbeiten“: there while a field is selected
    /// (KONZEPT.md, section 5.22). Docked it would change the width of the view with the click that selects the field, and the page would
    /// move under the mouse while the field is being dragged.
    /// </summary>
    public bool IsFloating => _doc is { IsFormDesign: false, IsFieldDesign: true };

    /// <summary>Whether a field is selected (otherwise a hint is shown).</summary>
    public bool HasField => _element is not null;

    public bool IsSingle => _count == 1;

    public string Title => _count > 1
        ? Tr.T("{0} Felder", _count)
        : _spec.Code is { } code ? CodeHints.FieldTitle(code) : _spec.Kind switch
        {
            FormFieldKind.CheckBox => Tr.T("Kontrollkästchen"),
            FormFieldKind.Radio => Tr.T("Optionsfeld"),
            FormFieldKind.ComboBox => Tr.T("Kombinationsfeld"),
            FormFieldKind.ListBox => Tr.T("Listenfeld"),
            FormFieldKind.PushButton => Tr.T("Schaltfläche"),
            FormFieldKind.Signature => Tr.T("Unterschriftsfeld"),
            _ => _spec.Format.Kind switch
            {
                FieldFormatKind.Date => Tr.T("Datumsfeld"),
                FieldFormatKind.Number or FieldFormatKind.Percent => Tr.T("Zahlenfeld"),
                _ => Tr.T("Textfeld"),
            },
        };

    /// <summary>Icon of the field type for the panel header (mixed selection: form icon).</summary>
    public Avalonia.Media.Geometry? KindIcon
    {
        get
        {
            var key = _kinds.Length > 1 ? "Icon.Form" : _spec.Code is { } code ? code.IsMatrix ? "Icon.QrCode" : "Icon.Barcode" : _spec.Kind switch
            {
                FormFieldKind.CheckBox => "Icon.CheckboxChecked",
                FormFieldKind.Radio => "Icon.RadioButton",
                FormFieldKind.ComboBox => "Icon.TextBulletListSquare",
                FormFieldKind.ListBox => "Icon.List",
                FormFieldKind.PushButton => "Icon.ArrowReset",
                FormFieldKind.Signature => "Icon.Signature",
                _ => _spec.Format.Kind switch
                {
                    FieldFormatKind.Date => "Icon.Calendar",
                    FieldFormatKind.Number or FieldFormatKind.Percent => "Icon.NumberSymbol",
                    _ => "Icon.TextField",
                },
            };
            return Avalonia.Application.Current?.TryGetResource(key, null, out var icon) == true ? icon as Avalonia.Media.Geometry : null;
        }
    }

    /// <summary>Full name (for fields created by other programs including the names of the parents).</summary>
    public string FullName => _element is not null && _doc is not null ? _doc.FieldNameOf(_element) : "";

    // Types of all selected fields: with a mixed selection the sections show only what all of them have (so a change never hits a
    // field that does not know the property)
    private FormFieldKind[] _kinds = [];
    private bool _allText;
    private bool _ownSignature;

    private bool All(Func<FormFieldKind, bool> kind) => _kinds.Length > 0 && _kinds.All(kind);

    public bool IsSignature => All(k => k == FormFieldKind.Signature);
    public bool IsText => All(k => k == FormFieldKind.Text);

    /// <summary>Text fields without code fields (options, format and calculation are for text only).</summary>
    public bool IsPlainText => IsText && !_anyCode;
    public bool IsCheck => All(k => k is FormFieldKind.CheckBox or FormFieldKind.Radio);
    public bool IsRadio => All(k => k == FormFieldKind.Radio);
    public bool IsChoice => All(k => k is FormFieldKind.ComboBox or FormFieldKind.ListBox);
    public bool IsCombo => All(k => k == FormFieldKind.ComboBox);
    public bool IsList => All(k => k == FormFieldKind.ListBox);
    public bool IsButton => All(k => k == FormFieldKind.PushButton);
    public bool HasTextStyle => _allText;
    public bool HasAppearance => !_ownSignature && !_kinds.Contains(FormFieldKind.Signature);

    /// <summary>Whether fields of different types are selected (then only what they share is shown).</summary>
    public bool IsMixed => _kinds.Length > 1;

    /// <summary>Border, fill and text of the field (code fields have their colours with the code).</summary>
    public bool ShowAppearance => !IsOwnSignature && !_codes;

    /// <summary>Own signature field: only name and label.</summary>
    public bool IsOwnSignature => _ownSignature && _count == 1;

    // ------------------------------------------------------------ Changing

    private void Change(Func<FieldSpec, FieldSpec> change, bool fieldLevel, string description, Func<PageElement, FieldSpec, PageElement>? adjust = null)
    {
        if (_refreshing || _doc is null) return;
        _doc.ChangeFieldSpecs(change, fieldLevel, description, adjust);
        Refresh(_doc);
    }

    // ---- General

    public string Name
    {
        get => _spec.Name;
        set
        {
            var name = (value ?? "").Trim().Replace(".", "");   // a full stop separates the names of the parents
            if (name.Length == 0 || name == _spec.Name || _count != 1) return;
            if (_refreshing) return;
            // A name of fields of the same kind links the field to them (KONZEPT.md, section 5.4a) — asked first, it takes over their value;
            // a name of another kind of field is refused (in the file they would have to be one field)
            switch (_doc!.NameUseFor(name))
            {
                case (DocumentViewModel.NameUse.Taken, _):
                    _doc.Notify(Tr.T("Ein Feld „{0}“ anderer Art gibt es schon — bitte einen anderen Namen wählen.", name));
                    OnPropertyChanged(nameof(Name));
                    return;
                case (DocumentViewModel.NameUse.Link, var count):
                    _ = LinkAsync(_doc, name, count);
                    return;
            }
            _doc.RenameSelectedField(name);
            Refresh(_doc);
        }
    }

    /// <summary>Shows a question with two answers and „Abbrechen“ (set by the main window's view model): heading, message, answers → 1, 2 or 0.</summary>
    public Func<string, string, string, string?, Task<int>>? AskChoice { get; set; }

    /// <summary>
    /// Renaming to the name of fields of the same kind: asked first, then linked (one step). The question is asked when the name box loses
    /// focus — often by a click on the page, which changes the selection or starts drawing a field before the answer arrives; the field
    /// that was being renamed is selected again, so that the answer „Verbinden“ reaches it (docs/FEHLERGEDAECHTNIS.md, No. 360).
    /// </summary>
    private async Task LinkAsync(DocumentViewModel doc, string name, int count)
    {
        var selected = _selected;
        // the click on the page that made the name box lose its focus may have started a selection frame: its release goes to the question
        doc.CancelDrag();
        var answer = AskChoice is null ? 1 : await AskChoice(Tr.T("Mit „{0}“ verbinden?", name),
            (count == 1 ? Tr.T("Ein Feld „{0}“ gibt es schon.", name) : Tr.T("{1} Felder „{0}“ gibt es schon.", name, count)) + " " +
            Tr.T("Felder mit gleichem Namen sind ein Feld: sie zeigen denselben Wert, und was man in eins schreibt, steht auch in den anderen. Dieses Feld übernimmt ihren Wert."),
            Tr.T("Verbinden"), Tr.T("Anderen Namen wählen"));
        if (answer == 1 && ReferenceEquals(doc, _doc) && selected is { } field)
        {
            if (!ReferenceEquals(field.Element, _element) && doc.History.Pages.Count > field.Page && doc.History.Pages[field.Page].Elements.Contains(field.Element))
            {
                doc.SelectFields([field]);
                Refresh(doc);
            }
            if (ReferenceEquals(field.Element, _element)) doc.RenameSelectedField(name);
        }
        if (ReferenceEquals(doc, _doc)) Refresh(doc);
    }

    // ---- Linked fields (KONZEPT.md, section 5.4a)

    private IReadOnlyList<ElementRef> _others = [];

    /// <summary>Whether the field shares its name with others (linked fields or a radio group).</summary>
    public bool IsLinked => IsSingle && _others.Count > 0;

    /// <summary>Linked fields of one kind (not a radio group): they can be separated.</summary>
    public bool CanSeparate => IsLinked && _spec.Kind != FormFieldKind.Radio;

    /// <summary>What the link means, for the panel.</summary>
    public string LinkText => !IsLinked ? "" : _spec.Kind == FormFieldKind.Radio
        ? Tr.T("Optionsgruppe mit {0} Optionen — es ist immer nur eine gewählt.", _others.Count + 1)
        : _others.Count == 1
            ? Tr.T("Verbundenes Feld — teilt seinen Wert mit einem weiteren Feld.")
            : Tr.T("Verbundenes Feld — teilt seinen Wert mit {0} weiteren Feldern.", _others.Count);

    /// <summary>The other fields of the name, for the list in the panel.</summary>
    public IReadOnlyList<LinkedFieldRow> LinkedFields => [.. _others.Select(r => new LinkedFieldRow(r, _doc?.SpecOf(r.Element) is { } s
        ? Tr.T("Seite {0}", r.Page + 1) + " · " + PositionText(r.Element) + (s.Kind == FormFieldKind.Radio && s.ExportValue.Length > 0 ? " · " + s.ExportValue : "")
        : Tr.T("Seite {0}", r.Page + 1)))];

    /// <summary>Where a field is on its page, in mm from the top left (as a person would describe it).</summary>
    private string PositionText(PageElement element)
    {
        var page = _doc?.History.Pages is { } pages && _others.FirstOrDefault(r => ReferenceEquals(r.Element, element)) is { } r ? pages[r.Page] : null;
        var height = page?.Page.CropBox.Top ?? 0;
        static string Mm(double pt) => Math.Round(pt / 72 * 25.4).ToString(CultureInfo.CurrentCulture);
        return Tr.T("{0} mm von links, {1} mm von oben", Mm(element.Rect.Left), Mm(height - element.Rect.Top));
    }

    /// <summary>Jumps to a linked field and selects it.</summary>
    /// <param name="row">The row of the list.</param>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public void GoToLinked(LinkedFieldRow row)
    {
        if (_doc is null) return;
        _doc.SelectFields([row.Field]);
        _doc.ShowElement(row.Field);
        Refresh(_doc);
    }

    /// <summary>Separates the field from the others („Von den anderen trennen“) or all of the group („Alle trennen“), one step.</summary>
    /// <param name="all">true: all fields of the group.</param>
    public void Separate(bool all)
    {
        if (_doc is null) return;
        _doc.SeparateSelectedField(all);
        Refresh(_doc);
    }

    /// <summary>„Von den anderen trennen“.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void SeparateOne() => Separate(false);

    /// <summary>„Alle trennen“.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void SeparateAll() => Separate(true);

    public string Tooltip
    {
        get => _spec.Tooltip;
        set
        {
            if ((value ?? "").Trim() == Tooltip) return;   // unchanged (focus left, saving): do not write to all selected fields
            Change(s => s with { Tooltip = (value ?? "").Trim() }, true, Tr.T("Quickinfo ändern"));
        }
    }

    public bool Required
    {
        get => _spec.Required;
        set => Change(s => s with { Required = value }, true, Tr.T("Pflichtfeld ändern"));
    }

    public bool ReadOnly
    {
        get => _spec.ReadOnly;
        set => Change(s => s with { ReadOnly = value }, true, Tr.T("Schreibschutz ändern"));
    }

    public Choice<FieldVisibility>? Visibility
    {
        get => Visibilities.FirstOrDefault(v => v.Value == _spec.Visibility);
        set
        {
            if (value is not null) Change(s => s with { Visibility = value.Value }, false, Tr.T("Sichtbarkeit ändern"));
        }
    }

    // ---- Appearance

    public FieldColorOption? BorderColor
    {
        get => ColorOf(_spec.BorderColor);
        set
        {
            if (value is not null) Change(s => s with { BorderColor = value.Rgb }, false, Tr.T("Rahmenfarbe ändern"));
        }
    }

    public FieldColorOption? FillColor
    {
        get => ColorOf(_spec.FillColor);
        set
        {
            if (value is not null) Change(s => s with { FillColor = value.Rgb }, false, Tr.T("Füllfarbe ändern"));
        }
    }

    public Choice<double>? BorderWidth
    {
        get => BorderWidths.MinBy(w => Math.Abs(w.Value - _spec.BorderWidth));
        set
        {
            if (value is not null) Change(s => s with { BorderWidth = value.Value }, false, Tr.T("Rahmenbreite ändern"));
        }
    }

    public Choice<FieldBorderStyle>? BorderStyle
    {
        get => BorderStyles.FirstOrDefault(b => b.Value == _spec.BorderStyle);
        set
        {
            if (value is not null) Change(s => s with { BorderStyle = value.Value }, false, Tr.T("Rahmenstil ändern"));
        }
    }

    public FontOption? Font
    {
        get => MainViewModel.FontOptions.FirstOrDefault(f => f.Font == _spec.Font);
        set
        {
            if (value is not null) Change(s => s with { Font = value.Font }, false, Tr.T("Schrift ändern"));
        }
    }

    public Choice<double>? FontSize
    {
        get => FontSizes.FirstOrDefault(f => Math.Abs(f.Value - _spec.FontSize) < 0.01) ?? new Choice<double>(_spec.FontSize, FieldNumber(_spec.FontSize));
        set
        {
            if (value is not null) Change(s => s with { FontSize = value.Value }, false, Tr.T("Schriftgröße ändern"));
        }
    }

    /// <summary>
    /// Shrink text automatically: the chosen size is the starting size and becomes smaller when there is too much text. Only with a
    /// fixed size (with „Automatisch“ the field shrinks anyway, as in common PDF editors).
    /// </summary>
    public bool ShrinkToFit
    {
        get => _spec.ShrinkToFit && _spec.FontSize > 0;
        set => Change(s => s with { ShrinkToFit = value && s.FontSize > 0 }, false, value ? Tr.T("Schrift automatisch verkleinern") : Tr.T("Feste Schriftgröße"));
    }

    public bool CanShrinkToFit => _spec.FontSize > 0;

    public FieldColorOption? TextColor
    {
        get => ColorOf(_spec.TextColor);
        set
        {
            if (value is { Rgb: { } rgb }) Change(s => s with { TextColor = rgb }, false, Tr.T("Textfarbe ändern"));
        }
    }

    /// <summary>
    /// A button of a group (left/centre/right) clicked again stays pressed; otherwise all would appear off. The notification is
    /// posted after the click, because Avalonia would ignore it during the binding update.
    /// </summary>
    private void Restore(string property) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(property));

    public bool AlignLeft
    {
        get => _spec.Alignment == ElementAlignment.Left;
        set { if (value) Change(s => s with { Alignment = ElementAlignment.Left }, false, Tr.T("Ausrichtung ändern")); else Restore(nameof(AlignLeft)); }
    }

    public bool AlignCenter
    {
        get => _spec.Alignment == ElementAlignment.Center;
        set { if (value) Change(s => s with { Alignment = ElementAlignment.Center }, false, Tr.T("Ausrichtung ändern")); else Restore(nameof(AlignCenter)); }
    }

    public bool AlignRight
    {
        get => _spec.Alignment == ElementAlignment.Right;
        set { if (value) Change(s => s with { Alignment = ElementAlignment.Right }, false, Tr.T("Ausrichtung ändern")); else Restore(nameof(AlignRight)); }
    }

    // ---- Text field

    public string DefaultValue
    {
        get => _spec.DefaultValue;
        set
        {
            if (value == DefaultValue) return;   // unchanged (focus left, saving): do not write to all selected fields
            Change(s => s with { DefaultValue = value ?? "" }, true, Tr.T("Standardwert ändern"));
        }
    }

    public bool Multiline
    {
        get => _spec.Multiline;
        set => Change(s => s with { Multiline = value }, true, Tr.T("Mehrzeilig ändern"));
    }

    public bool DoNotScroll
    {
        get => _spec.DoNotScroll;
        set => Change(s => s with { DoNotScroll = value }, true, Tr.T("Bildlauf ändern"));
    }

    public bool Password
    {
        get => _spec.Password;
        set => Change(s => s with { Password = value }, true, Tr.T("Kennwortfeld ändern"));
    }

    public bool Comb
    {
        get => _spec.Comb;
        set => Change(s => s with { Comb = value, MaxLength = value && s.MaxLength == 0 ? 10 : s.MaxLength }, true, Tr.T("Kammfeld ändern"));
    }

    public bool DoNotSpellCheck
    {
        get => _spec.DoNotSpellCheck;
        set => Change(s => s with { DoNotSpellCheck = value }, true, Tr.T("Rechtschreibprüfung ändern"));
    }

    /// <summary>Maximum length as text (empty = none; clamped to 0 … 100,000).</summary>
    public string MaxLength
    {
        get => _spec.MaxLength > 0 ? _spec.MaxLength.ToString(CultureInfo.CurrentCulture) : "";
        set
        {
            var length = int.TryParse((value ?? "").Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) ? Math.Clamp(n, 0, 100_000) : 0;
            if (length == _spec.MaxLength) return;
            Change(s => s with { MaxLength = length, Comb = s.Comb && length > 0 }, true, Tr.T("Höchstlänge ändern"));
        }
    }

    // ---- Check boxes

    public Choice<CheckStyle>? Mark
    {
        get => CheckStyles.FirstOrDefault(c => c.Value == _spec.CheckStyle);
        set
        {
            if (value is not null) Change(s => s with { CheckStyle = value.Value }, false, Tr.T("Kästchenstil ändern"));
        }
    }

    /// <summary>Export value (per widget); a check box that is on stays on.</summary>
    public string ExportValue
    {
        get => _spec.ExportValue;
        set
        {
            var export = (value ?? "").Trim();
            if (export.Length == 0 || export == "Off" || export == _spec.ExportValue) return;
            if (_spec.Kind == FormFieldKind.Radio && _doc is { SelectedElement: { } primary } && _doc.ExportValuesOf(_doc.FieldNameOf(primary.Element)).Contains(export))
            {
                // The same export value in a group: both would switch on together
                _doc.Notify(Tr.T("Die Gruppe hat schon eine Option „{0}“ — bitte einen anderen Export-Wert wählen.", export));
                OnPropertyChanged(nameof(ExportValue));
                return;
            }
            var old = _spec.ExportValue;
            Change(s => s with { ExportValue = export, DefaultValue = s.DefaultValue == old ? export : s.DefaultValue }, false, Tr.T("Export-Wert ändern"),
                // Checked stays checked, also for foreign fields (otherwise the box would look empty but be saved as checked)
                (element, _) => element switch
                {
                    FieldElement { Value: var v } fe when v == old => fe with { Value = export },
                    ForeignAnnotation fa when DocumentViewModel.ValueOf(fa) == old => fa with { Value = export },
                    _ => element,
                });
        }
    }

    public bool DefaultOn
    {
        get => _spec.DefaultValue.Length > 0 && _spec.DefaultValue == _spec.ExportValue;
        set
        {
            var export = _spec.ExportValue;
            Change(s => s with { DefaultValue = value ? export : "" }, true, Tr.T("Standardwert ändern"));
        }
    }

    /// <summary>Whether a label is shown next to the check box (on when inserting).</summary>
    public bool ShowLabel
    {
        get => _doc?.LabelElementOfSelected() is not null;
        set
        {
            if (_refreshing || _doc is null) return;
            _doc.ShowLabels(value);
            Refresh(_doc);
        }
    }

    public FontOption? LabelFont
    {
        get => _doc?.LabelElementOfSelected() is { } l ? MainViewModel.FontOptions.FirstOrDefault(f => f.Font == l.Font) : null;
        set
        {
            if (_refreshing || _doc is null || value is null) return;
            _doc.SetLabelStyle(t => t with { Font = value.Font }, Tr.T("Schrift ändern"));
            Refresh(_doc);
        }
    }

    public double LabelSize
    {
        get => _doc?.LabelElementOfSelected()?.FontSize ?? 10;
        set
        {
            if (_refreshing || _doc is null || value is < 1 or > 200 || double.IsNaN(value)) return;
            _doc.SetLabelStyle(t => t with { FontSize = value }, Tr.T("Schriftgröße ändern"));
            Refresh(_doc);
        }
    }

    public ColorOption? LabelColor
    {
        get => _doc?.LabelElementOfSelected() is { } l
            ? MainViewModel.ColorOptions.FirstOrDefault(c => c.Rgb == l.Color) ?? new ColorOption(l.Color, "Eigene Farbe")
            : null;
        set
        {
            if (_refreshing || _doc is null || value is null) return;
            _doc.SetLabelStyle(t => t with { Color = value.Rgb }, Tr.T("Textfarbe ändern"));
            Refresh(_doc);
        }
    }

    /// <summary>Label next to the check box (text on the page); empty = none.</summary>
    public string LabelText
    {
        get => _doc?.LabelOfSelected()?.Text ?? "";
        set
        {
            if (_refreshing || _doc is null || (value ?? "") == LabelText) return;
            _doc.SetLabelText(value ?? "");
            Refresh(_doc);
        }
    }

    /// <summary>Gap between label and check box in points (for precise alignment).</summary>
    public decimal? LabelGap
    {
        get => _doc?.LabelGapOfSelected() is { } gap ? (decimal)gap : null;
        set
        {
            if (_refreshing || _doc is null || value is not { } gap) return;
            _doc.SetLabelGap((double)gap);
            Refresh(_doc);
        }
    }

    public bool LabelLeft
    {
        get => _doc?.LabelOfSelected()?.Left == true;
        set
        {
            if (!value) Restore(nameof(LabelLeft));
            if (_refreshing || _doc is null || !value) return;
            _doc.SetLabelSide(true);
            Refresh(_doc);
        }
    }

    public bool LabelRight
    {
        get => _doc?.LabelOfSelected() is { Left: false };
        set
        {
            if (!value) Restore(nameof(LabelRight));
            if (_refreshing || _doc is null || !value) return;
            _doc.SetLabelSide(false);
            Refresh(_doc);
        }
    }

    public bool NoToggleToOff
    {
        get => _spec.NoToggleToOff;
        set => Change(s => s with { NoToggleToOff = value }, true, Tr.T("Abwählen ändern"));
    }

    public bool RadiosInUnison
    {
        get => _spec.RadiosInUnison;
        set => Change(s => s with { RadiosInUnison = value }, true, Tr.T("Optionen gemeinsam ändern"));
    }

    // ---- Choice fields

    /// <summary>Entries, one per line; a different export value after „|“ („Österreich|at“); at most 10,000.</summary>
    public string OptionsText
    {
        get => string.Join("\n", _spec.Options.Select(o => o.Export == o.Display ? o.Display : $"{o.Display}|{o.Export}"));
        set
        {
            var options = new List<FieldOption>();
            foreach (var line in (value ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                var bar = trimmed.LastIndexOf('|');
                options.Add(bar > 0 && bar < trimmed.Length - 1
                    ? new FieldOption(trimmed[(bar + 1)..].Trim(), trimmed[..bar].Trim())
                    : new FieldOption(trimmed, trimmed));
                if (options.Count >= 10_000) break;
            }
            if (options.SequenceEqual(_spec.Options)) return;   // unchanged: do not write to all selected fields
            Change(s => s with { Options = options }, true, Tr.T("Einträge ändern"));
        }
    }

    public bool Sort
    {
        get => _spec.Sort;
        set => Change(s => s with { Sort = value }, true, Tr.T("Sortieren ändern"));
    }

    public bool Editable
    {
        get => _spec.Editable;
        set => Change(s => s with { Editable = value }, true, Tr.T("Eigenen Text erlauben ändern"));
    }

    public bool MultiSelect
    {
        get => _spec.MultiSelect;
        set => Change(s => s with { MultiSelect = value }, true, Tr.T("Mehrfachauswahl ändern"));
    }

    public bool CommitOnSelect
    {
        get => _spec.CommitOnSelect;
        set => Change(s => s with { CommitOnSelect = value }, true, Tr.T("Sofort übernehmen ändern"));
    }

    // ---- Push button

    public string Caption
    {
        get => _spec.Caption;
        set
        {
            if (value == Caption) return;   // unchanged (focus left, saving): do not write to all selected fields
            Change(s => s with { Caption = value ?? "" }, false, Tr.T("Beschriftung ändern"));
        }
    }

    /// <summary>Label of an own signature field.</summary>
    public string SignatureLabel
    {
        get => _element is SignatureFieldElement s ? s.Label : "";
        set
        {
            if (_refreshing || _doc is not { SelectedElement: { Element: SignatureFieldElement sig } selected } doc || sig.Label == (value ?? "")) return;
            doc.ReplaceElement(selected, sig with { Label = value ?? "" }, Tr.T("Beschriftung ändern"));
            Refresh(doc);
        }
    }

    // ---- Format

    public bool HasCustomFormat => _spec.CustomFormatScript is not null;

    public string CustomFormatScript => _spec.CustomFormatScript ?? "";

    public bool CanFormat => IsText && !HasCustomFormat;

    public Choice<FieldFormatKind>? FormatKind
    {
        get => FormatKinds.FirstOrDefault(f => f.Value == _spec.Format.Kind);
        set
        {
            if (value is null || value.Value == _spec.Format.Kind) return;
            var format = value.Value switch
            {
                FieldFormatKind.Number => new FieldFormat(FieldFormatKind.Number, 2, 2),
                FieldFormatKind.Percent => new FieldFormat(FieldFormatKind.Percent, 0, 2),
                FieldFormatKind.Date => new FieldFormat(FieldFormatKind.Date, Pattern: "dd.mm.yyyy"),
                FieldFormatKind.Time => new FieldFormat(FieldFormatKind.Time, Pattern: "HH:MM"),
                FieldFormatKind.Special => new FieldFormat(FieldFormatKind.Special, Special: 0),
                _ => FieldFormat.None,
            };
            Change(s => s with { Format = format, Alignment = format.Kind is FieldFormatKind.Number or FieldFormatKind.Percent ? ElementAlignment.Right : s.Alignment },
                true, Tr.T("Format ändern"));
        }
    }

    public bool IsNumberFormat => _spec.Format.Kind is FieldFormatKind.Number or FieldFormatKind.Percent;
    public bool IsCurrencyFormat => _spec.Format.Kind == FieldFormatKind.Number;
    public bool IsDateFormat => _spec.Format.Kind == FieldFormatKind.Date;
    public bool IsTimeFormat => _spec.Format.Kind == FieldFormatKind.Time;
    public bool IsSpecialFormat => _spec.Format.Kind == FieldFormatKind.Special;

    public Choice<int>? Decimals
    {
        get => DecimalChoices.FirstOrDefault(d => d.Value == _spec.Format.Decimals);
        set
        {
            if (value is not null) Change(s => s with { Format = s.Format with { Decimals = value.Value } }, true, Tr.T("Format ändern"));
        }
    }

    public static IReadOnlyList<Choice<int>> DecimalChoices { get; } = [.. Enumerable.Range(0, 6).Select(i => new Choice<int>(i, i.ToString(CultureInfo.InvariantCulture)))];

    public Choice<int>? Separator
    {
        get => Separators.FirstOrDefault(s => s.Value == _spec.Format.Separators);
        set
        {
            if (value is not null) Change(s => s with { Format = s.Format with { Separators = value.Value } }, true, Tr.T("Format ändern"));
        }
    }

    public string Currency
    {
        get => _spec.Format.Currency;
        set
        {
            if (value == Currency) return;   // unchanged (focus left, saving): do not write to all selected fields
            Change(s => s with { Format = s.Format with { Currency = (value ?? "")[..Math.Min((value ?? "").Length, 10)] } }, true, Tr.T("Format ändern"));
        }
    }

    public bool CurrencyFirst
    {
        get => _spec.Format.CurrencyFirst;
        set => Change(s => s with { Format = s.Format with { CurrencyFirst = value } }, true, Tr.T("Format ändern"));
    }

    public string Pattern
    {
        get => _spec.Format.Pattern;
        set
        {
            var pattern = (value ?? "").Trim();
            if (pattern.Length is 0 or > 40) return;
            Change(s => s with { Format = s.Format with { Pattern = pattern } }, true, Tr.T("Format ändern"));
        }
    }

    public Choice<int>? Special
    {
        get => Specials.FirstOrDefault(s => s.Value == _spec.Format.Special);
        set
        {
            if (value is not null) Change(s => s with { Format = s.Format with { Special = value.Value } }, true, Tr.T("Format ändern"));
        }
    }

    /// <summary>How a sample value looks in the format.</summary>
    public string FormatExample => _spec.Format.Kind switch
    {
        FieldFormatKind.Number => _spec.Format.Display("-1234.5"),
        FieldFormatKind.Percent => _spec.Format.Display("0.125"),
        FieldFormatKind.Date or FieldFormatKind.Time => _spec.Format.Format(new DateTime(2026, 10, 18, 14, 30, 0)),
        FieldFormatKind.Special => _spec.Format.Special switch { 1 => "12345-6789", 2 => "(555) 123-4567", 3 => "123-45-6789", _ => "12345" },
        _ => "",
    };

    // ---- Calculation

    public bool HasCustomCalculation => _spec.CustomCalculationScript is not null;

    public string CustomCalculationScript => _spec.CustomCalculationScript ?? "";

    public bool CanCalculate => IsText && !HasCustomCalculation;

    public Choice<CalcOperation?>? Operation
    {
        get => Operations.FirstOrDefault(o => o.Value == _spec.Calculation?.Operation);
        set
        {
            if (value is null) return;
            Change(s => s with
            {
                Calculation = value.Value is { } op ? Switched(s.Calculation, op) : null,
                // Read-only with a calculation; „Keine“ lifts that again (otherwise the field could no longer be filled in)
                ReadOnly = value.Value is not null || (s.ReadOnly && s.Calculation is null),
            }, true, Tr.T("Berechnung ändern"));
        }
    }

    /// <summary>
    /// Different operation: the fields stay. Switching to a formula turns the chosen fields into a sum or product („A + B“); switching
    /// from a formula makes its fields the list.
    /// </summary>
    private static FieldCalculation Switched(FieldCalculation? old, CalcOperation op)
    {
        var fields = old?.Fields ?? [];
        if (op != CalcOperation.Formula) return new FieldCalculation(op, fields);
        if (old?.Formula is { } kept) return FieldCalculation.Of(kept);
        var joined = string.Join(old?.Operation == CalcOperation.Product ? " * " : " + ", fields.Select(FieldFormula.Escape));
        return FieldFormula.Parse(joined) is { } formula ? FieldCalculation.Of(formula) : new FieldCalculation(CalcOperation.Formula, []);
    }

    public bool HasCalculation => _spec.Calculation is not null;

    /// <summary>Whether the calculation works over a list of fields (sum, product …): show the check-box list.</summary>
    public bool IsListCalculation => _spec.Calculation is { Operation: not CalcOperation.Formula };

    /// <summary>Whether the calculation uses a formula: show the input box.</summary>
    public bool IsFormula => _spec.Calculation is { Operation: CalcOperation.Formula };

    // Typed formula with an error (not applied, but kept until another field is selected)
    private string? _formulaDraft;
    private PageElement? _formulaDraftFor;

    /// <summary>The formula in simplified field notation; an erroneous input stays and is explained below it.</summary>
    public string FormulaText
    {
        get => _formulaDraft is not null && ReferenceEquals(_formulaDraftFor, _element) ? _formulaDraft : _spec.Calculation?.Formula?.Notation ?? "";
        set
        {
            if (_refreshing || _doc is null || _spec.Calculation is not { Operation: CalcOperation.Formula } calc) return;
            value ??= "";
            if (value.Trim() == (calc.Formula?.Notation ?? "") && _formulaDraft is null) return;
            string? error = null;
            var formula = FieldFormula.Parse(value, out var parseError, out var at);
            if (formula is null) error = Tr.T("{0} (Zeichen {1})", parseError ?? "", at + 1);
            else
            {
                var own = FullName;
                var known = _doc.FieldNames();
                if (formula.Fields.Contains(own)) error = Tr.T("Ein Feld kann nicht mit sich selbst rechnen.");
                else if (formula.Fields.FirstOrDefault(f => !known.Contains(f)) is { } unknown)
                    error = Tr.T("Ein Feld „{0}“ gibt es nicht. Namen mit Leer- oder Sonderzeichen brauchen „\\“ davor — oder „Feld einfügen“ nehmen.", unknown);
            }
            if (error is not null)
            {
                _formulaDraft = value;
                _formulaDraftFor = _element;
                FormulaError = error;
                OnPropertyChanged(nameof(FormulaText));
                return;
            }
            _formulaDraft = null;
            FormulaError = null;
            if (formula!.Equals(calc.Formula)) return;
            Change(s => s with { Calculation = FieldCalculation.Of(formula) }, true, Tr.T("Berechnung ändern"));
        }
    }

    /// <summary>What is wrong with the typed formula (otherwise null).</summary>
    public string? FormulaError
    {
        get => ReferenceEquals(_formulaDraftFor, _element) ? field : null;
        private set
        {
            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasFormulaError));
        }
    }

    public bool HasFormulaError => FormulaError is not null;

    /// <summary>Field names for inserting into the formula (escaped, without this field).</summary>
    public IReadOnlyList<string> FormulaFields =>
        _doc is null || !IsFormula ? [] : [.. _doc.FieldNames().Where(n => n != FullName).Order(StringComparer.CurrentCulture).Select(FieldFormula.Escape)];

    /// <summary>Fields the calculation works over (chosen from all fields of the document except this one).</summary>
    public IReadOnlyList<CalculationSource> CalculationSources
    {
        get
        {
            if (_doc is null || _spec.Calculation is not { } calc) return [];
            // Once per history state and field (rebuilding the list on every notification, with the UI rebuilding 2,000 check boxes
            // each time, made clicking a sum field take 2.2 s and 345 MB). With the same list the UI rebuilds nothing.
            var key = (_doc.History.Pages, FullName, calc);
            if (_sources is { } cached && Equals(_sourcesKey, key)) return cached;
            var chosen = new HashSet<string>(calc.Fields, StringComparer.Ordinal);
            _sourcesKey = key;
            return _sources = [.. _doc.FieldNames().Where(n => n != key.Item2).Order(StringComparer.CurrentCulture)
                .Select(n => new CalculationSource(this, n, chosen.Contains(n)))];
        }
    }

    private IReadOnlyList<CalculationSource>? _sources;
    private object? _sourcesKey;

    internal void SetCalculationSource(string name, bool included)
    {
        Change(s => s.Calculation is not { } calc ? s : s with
        {
            Calculation = calc with { Fields = included ? [.. calc.Fields.Where(f => f != name), name] : [.. calc.Fields.Where(f => f != name)] },
        }, true, Tr.T("Berechnung ändern"));
    }

    // ------------------------------------------------------------ Helpers

    private static FieldColorOption ColorOf(int? rgb) =>
        Colors.FirstOrDefault(c => c.Rgb == rgb) ?? new FieldColorOption(rgb, "Eigene Farbe");

    private static string FieldNumber(double value) => value.ToString("0.#", CultureInfo.CurrentCulture);
}

/// <summary>A field as a source of a calculation (check box in the list).</summary>
public sealed class CalculationSource(FieldPropertiesViewModel owner, string name, bool included)
{
    public string Name { get; } = name;

    public bool Included
    {
        get => included;
        set
        {
            if (value == included) return;
            included = value;
            owner.SetCalculationSource(Name, value);
        }
    }
}
