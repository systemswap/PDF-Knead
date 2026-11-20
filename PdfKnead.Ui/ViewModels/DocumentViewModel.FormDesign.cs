using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.ViewModels;

/// <summary>Field type when inserting in the „Formular“ tab.</summary>
public enum FieldPreset
{
    Text,
    Date,
    Number,
    CheckBox,
    Radio,
    Signature,
    ComboBox,
    ListBox,
    ResetButton,

    /// <summary>Code field with a QR code (KONZEPT.md, section 5.19, step 4).</summary>
    QrCode,

    /// <summary>Code field with a bar code (Code 128; the kind can be changed on the right).</summary>
    Barcode,
}

/// <summary>Direction when arranging several fields.</summary>
public enum FieldArrange
{
    Left,
    Right,
    Top,
    Bottom,
    SameWidth,
    SameHeight,

    /// <summary>Centred horizontally (centres aligned vertically).</summary>
    Center,

    /// <summary>Centred vertically (centres on one line, e.g. check boxes in a row).</summary>
    Middle,

    /// <summary>Distribute horizontally (equal or chosen gap).</summary>
    DistributeHorizontally,

    /// <summary>Distribute vertically.</summary>
    DistributeVertically,
}

/// <summary>
/// Creating forms and editing field properties: in „Formular bearbeiten“ mode a click selects a field instead of filling it in,
/// and several fields of a page can be selected, moved, arranged and changed together. Field properties (name, flags, options,
/// format, calculation) apply to all of a field's widgets, widget properties (appearance) only to the selected ones. Every change
/// is one undo step.
/// </summary>
public sealed partial class DocumentViewModel
{
    /// <summary>Field type that the „Feld einfügen“ tool places.</summary>
    public FieldPreset FieldPreset { get; set; }

    /// <summary>Whether the form is being designed (fields are selected instead of filled in; frames and names visible).</summary>
    public bool IsFormDesign => Tool is ViewTool.DesignForm or ViewTool.InsertFormField;

    /// <summary>Whether fields are moved and resized rather than filled in: in „Formular bearbeiten“ and in „Bearbeiten“ (KONZEPT.md, section 5.22).</summary>
    public bool FieldsMovable => IsFormDesign || Tool == ViewTool.Edit;

    /// <summary>
    /// Whether fields are being designed right now: in „Formular bearbeiten“ always, in „Bearbeiten“ while a field or a field label is
    /// selected (then the field panel shows, and dragging moves all selected fields).
    /// </summary>
    public bool IsFieldDesign => IsFormDesign || Tool == ViewTool.Edit && SelectedElement is { } s && (IsFormField(s.Element) || IsFieldLabel(s.Element));

    // Further selected fields besides SelectedElement (same page); multiple selection with Ctrl or rubber band
    private readonly List<ElementRef> _moreSelected = [];
    private bool _keepMoreSelected;

    /// <summary>Selected fields: the rest of the multiple selection, followed by the one selected last.</summary>
    public IReadOnlyList<ElementRef> FieldSelection =>
        SelectedElement is { } primary ? [.. _moreSelected, primary] : [];

    /// <summary>Raised when the selection has changed (also when only the multiple selection changed).</summary>
    public event Action? FieldSelectionChanged;

    partial void OnSelectedElementChanged(ElementRef? value)
    {
        OnPropertyChanged(nameof(IsEditPanelVisible));   // an element's own panel takes the place of the „Bearbeiten“ panel
        OnPropertyChanged(nameof(IsReadingImageInfo));
        OnPropertyChanged(nameof(IsFieldDesign));
        if (!_keepMoreSelected) _moreSelected.Clear();
        FieldSelectionChanged?.Invoke();
        NoteClicked(value);
        if (value is null) MarkPicked = false;
    }

    /// <summary>Selects several fields (the last becomes the primary field); only fields on the same page as it are kept.</summary>
    /// <param name="fields">The fields; empty clears the selection.</param>
    public void SelectFields(IReadOnlyList<ElementRef> fields)
    {
        _keepMoreSelected = true;
        try
        {
            _moreSelected.Clear();
            if (fields.Count == 0)
            {
                SelectedElement = null;
                return;
            }
            var page = fields[^1].Page;
            foreach (var f in fields.Take(fields.Count - 1))
                if (f.Page == page && !ReferenceEquals(f.Element, fields[^1].Element)) _moreSelected.Add(f);
            SelectedElement = fields[^1];
        }
        finally
        {
            _keepMoreSelected = false;
        }
        FieldSelectionChanged?.Invoke();
    }

    /// <summary>Ctrl+click: adds the field to the selection or removes it; a field on another page starts a new selection.</summary>
    /// <param name="field">The field.</param>
    public void ToggleFieldSelection(ElementRef field)
    {
        var current = FieldSelection.ToList();
        var at = current.FindIndex(r => r.Page == field.Page && ReferenceEquals(r.Element, field.Element));
        if (at >= 0) current.RemoveAt(at);
        else if (current.Count == 0 || current[0].Page == field.Page) current.Add(field);
        else current = [field];
        SelectFields(current);
    }

    private void ValidateFieldSelection()
    {
        var before = _moreSelected.Count;
        _moreSelected.RemoveAll(r => !ElementsOn(r.Page).Contains(r.Element));
        if (before != _moreSelected.Count) FieldSelectionChanged?.Invoke();
    }

    /// <summary>Checks whether the element is a form field (own, new, or created by another program).</summary>
    /// <param name="element">The element.</param>
    /// <returns>true for a form field.</returns>
    public static bool IsFormField(PageElement element) => element switch
    {
        FieldElement or SignatureFieldElement or TextElement { IsField: true } => true,
        ForeignAnnotation foreign => FormField.Of(foreign) is not null,
        _ => false,
    };

    /// <summary>The file's form (/AcroForm), for default values (/DA, /Q).</summary>
    private PdfDictionary? AcroForm => Document.Catalog?.GetDictionary(PdfName.Get("AcroForm"));

    /// <summary>Returns the properties of a field as they currently apply.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The properties, or null if the element is not a field.</returns>
    public FieldSpec? SpecOf(PageElement element) => element switch
    {
        FieldElement field => field.Spec,
        ForeignAnnotation foreign => foreign.Spec ?? FieldSpec.Of(foreign),
        TextElement { IsField: true } text => FieldSpec.Of(text),
        SignatureFieldElement signature => new FieldSpec { Kind = FormFieldKind.Signature, Name = signature.FieldName, BorderColor = 0x999999, BorderWidth = 0.5 },
        _ => null,
    };

    /// <summary>Returns the value of a field as it currently applies.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The value; empty for other elements.</returns>
    public static string ValueOf(PageElement element) => element switch
    {
        FieldElement field => field.Value,
        ForeignAnnotation foreign when FormField.Of(foreign) is { } field => ElementAppearance.ValueOf(foreign, field),
        TextElement text => text.Text,
        _ => "",
    };

    /// <summary>The element with new properties. A simple text field (<see cref="TextElement"/> with IsField) becomes a <see cref="FieldElement"/> with the same content.</summary>
    private static PageElement WithSpec(PageElement element, FieldSpec spec) => element switch
    {
        FieldElement field => field with { Spec = spec },
        ForeignAnnotation foreign => foreign with { Spec = spec },
        TextElement { IsField: true } text => new FieldElement { Rect = text.Rect, Rotation = text.Rotation, Spec = spec, Value = text.Text },
        SignatureFieldElement signature => signature with { FieldName = spec.Name },
        _ => element,
    };

    /// <summary>Returns the name by which the field is found (the full name for fields created by other programs).</summary>
    /// <param name="element">The element.</param>
    /// <returns>The name; empty if there is none.</returns>
    public string FieldNameOf(PageElement element) => element switch
    {
        // Renamed: the own part is new, the names of the parents stay
        ForeignAnnotation foreign when FormField.Of(foreign) is not null => FieldLinks.CurrentName(foreign),
        _ => SpecOf(element)?.Name ?? "",
    };

    /// <summary>
    /// The name by which a field is linked to others of its kind (KONZEPT.md, section 5.4a): text fields, check boxes and lists with a name
    /// without points; null for other elements (radio buttons form their group by the name anyway, signature fields and buttons are never
    /// linked, names within other fields stay as they are).
    /// </summary>
    private string? LinkNameOf(PageElement element) =>
        element is FieldElement or ForeignAnnotation && SpecOf(element) is { } spec && FieldLinks.Linkable(spec.Kind) &&
        FieldNameOf(element) is { Length: > 0 } name && !name.Contains('.')
            ? name
            : null;

    /// <summary>
    /// The widgets of the document by what joins them to one field — the terminal field (foreign), the name of a new radio group, the name and
    /// kind of linked fields —, each with its place in page order. Built once per state of the pages (the history replaces its page array with
    /// every change), so that looking up the fields of a large selection stays linear.
    /// </summary>
    private sealed class FieldIndex(IReadOnlyList<PageItem> pages)
    {
        public IReadOnlyList<PageItem> Pages { get; } = pages;
        public Dictionary<PdfDictionary, List<(int Order, ElementRef Ref)>> ByTerminal { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<string, List<(int Order, ElementRef Ref)>> ByRadio { get; } = new(StringComparer.Ordinal);
        public Dictionary<(string Name, FormFieldKind Kind), List<(int Order, ElementRef Ref)>> ByLink { get; } = [];
        public HashSet<PageElement> Linked { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private FieldIndex? _fieldIndex;

    private FieldIndex Fields()
    {
        if (_fieldIndex is { } index && ReferenceEquals(index.Pages, History.Pages)) return index;
        index = new FieldIndex(History.Pages);
        static void Add<TKey>(Dictionary<TKey, List<(int, ElementRef)>> map, TKey key, (int, ElementRef) entry) where TKey : notnull =>
            (map.TryGetValue(key, out var list) ? list : map[key] = []).Add(entry);
        var order = 0;
        for (var page = 0; page < index.Pages.Count; page++)
            foreach (var element in index.Pages[page].Elements)
            {
                var entry = (order++, new ElementRef(page, element));
                if (element is ForeignAnnotation foreign && FormField.Of(foreign)?.Field is { } terminal) Add(index.ByTerminal, terminal, entry);
                if (element is FieldElement { Spec: { Kind: FormFieldKind.Radio, Name: var radio } }) Add(index.ByRadio, radio, entry);
                if (LinkNameOf(element) is { } link) Add(index.ByLink, (link, SpecOf(element)!.Kind), entry);
            }
        foreach (var group in index.ByLink.Values)
            if (group.Count > 1)
                foreach (var (_, r) in group) index.Linked.Add(r.Element);
        return _fieldIndex = index;
    }

    /// <summary>Whether a field is linked to others of its name and kind (for the mark in „Formular bearbeiten“).</summary>
    /// <param name="element">The field.</param>
    /// <returns>True for linked text fields, check boxes and lists.</returns>
    public bool IsLinkedField(PageElement element) => Fields().Linked.Contains(element);

    /// <summary>
    /// All widgets of the same field (all pages, in page order): foreign ones via the terminal field, new radio groups via the name, linked
    /// fields (the same name and kind) via the name.
    /// </summary>
    private List<ElementRef> SameField(ElementRef target)
    {
        var index = Fields();
        var found = new List<(int Order, ElementRef Ref)>();
        var seen = new HashSet<PageElement>(ReferenceEqualityComparer.Instance);
        void Take(List<(int Order, ElementRef Ref)>? list, Func<PageElement, bool>? when = null)
        {
            if (list is null) return;
            foreach (var entry in list)
                if ((when is null || ReferenceEquals(entry.Ref.Element, target.Element) || when(entry.Ref.Element)) && seen.Add(entry.Ref.Element))
                    found.Add(entry);
        }
        if (target.Element is ForeignAnnotation foreign && FormField.Of(foreign)?.Field is { } terminal)
        {
            // a widget separated from its field (a name of its own) is no longer part of it
            var name = FieldNameOf(target.Element);
            Take(index.ByTerminal.GetValueOrDefault(terminal), other => FieldNameOf(other) == name);
        }
        if (target.Element is FieldElement { Spec: { Kind: FormFieldKind.Radio, Name: var radio } }) Take(index.ByRadio.GetValueOrDefault(radio));
        if (LinkNameOf(target.Element) is { } link) Take(index.ByLink.GetValueOrDefault((link, SpecOf(target.Element)!.Kind)));
        if (!seen.Contains(target.Element)) return [target];
        found.Sort((a, b) => a.Order.CompareTo(b.Order));
        return [.. found.Select(f => f.Ref)];
    }

    /// <summary>The other widgets of the field (linked fields, or the options of a radio group), in page order.</summary>
    /// <param name="target">The field.</param>
    /// <returns>The others; empty for a field on its own.</returns>
    public IReadOnlyList<ElementRef> OthersOfField(ElementRef target) => [.. SameField(target).Where(r => !ReferenceEquals(r.Element, target.Element))];

    /// <summary>Scrolls to an element so that it is in view.</summary>
    /// <param name="target">The element.</param>
    public void ShowElement(ElementRef target)
    {
        var r = target.Element.Rect;
        ScrollToPage(target.Page);
        RevealRequested?.Invoke(target.Page, new TextQuad(r.Left, r.Top, r.Right, r.Top, r.Right, r.Bottom, r.Left, r.Bottom));
    }

    /// <summary>What renaming a field to a name would do (<see cref="RenameSelectedField"/>).</summary>
    public enum NameUse
    {
        /// <summary>No other field has the name.</summary>
        Free,

        /// <summary>A field of the same kind has it: the two are linked (they share their value).</summary>
        Link,

        /// <summary>A field of another kind, a radio group, a signature field or a button has it: not possible.</summary>
        Taken,
    }

    /// <summary>What renaming the selected field to <paramref name="name"/> would do.</summary>
    /// <param name="name">The new partial name.</param>
    /// <returns>Free, Link (with the number of fields it would join) or Taken.</returns>
    public (NameUse Use, int Count) NameUseFor(string name)
    {
        if (FieldsOfSelection is not { Count: > 0 } fields || SpecOf(fields[^1].Element) is not { } spec) return (NameUse.Free, 0);
        var selected = fields[^1];
        var full = FullNameFor(selected.Element, spec, name);
        var own = new HashSet<PageElement>(SameField(selected).Select(r => r.Element), ReferenceEqualityComparer.Instance);
        var others = new List<PageElement>();
        for (var page = 0; page < History.Count; page++)
            foreach (var element in History.Pages[page].Elements)
                if (!own.Contains(element) && IsFormField(element) && FieldNameOf(element) == full) others.Add(element);
        if (others.Count == 0) return (NameUse.Free, 0);
        var linkable = FieldLinks.Linkable(spec.Kind) && !full.Contains('.') && selected.Element is FieldElement or ForeignAnnotation;
        return linkable && others.All(o => o is FieldElement or ForeignAnnotation && SpecOf(o)?.Kind == spec.Kind)
            ? (NameUse.Link, others.Count)
            : (NameUse.Taken, others.Count);
    }

    /// <summary>The full name a field gets with a new partial name (the names of the parents of a foreign field stay).</summary>
    private string FullNameFor(PageElement element, FieldSpec spec, string name)
    {
        var oldFull = FieldNameOf(element);
        return oldFull.Length > spec.Name.Length && oldFull.EndsWith(spec.Name, StringComparison.Ordinal) ? oldFull[..^spec.Name.Length] + name : name;
    }

    /// <summary>
    /// Separates the selected field from the fields it is linked to (KONZEPT.md, section 5.4a): it gets a name of its own („Name_2“) and
    /// keeps its value; with <paramref name="all"/> every field of the group gets one (the first keeps the name). One step.
    /// </summary>
    /// <param name="all">true: all fields of the group; false: only the selected one.</param>
    /// <returns>false if the field is not linked or editing is not allowed.</returns>
    public bool SeparateSelectedField(bool all)
    {
        if (!CanEditElements || FieldsOfSelection is not { Count: > 0 } fields || LinkNameOf(fields[^1].Element) is not { } name) return false;
        var selected = fields[^1];
        var group = SameField(selected).ToList();
        if (group.Count < 2) return false;
        var names = FieldNames();
        var changes = new List<(int, PageElement, PageElement)>();
        foreach (var widget in all ? group.Skip(1) : [selected])
        {
            if (SpecOf(widget.Element) is not { } spec) continue;
            var full = FieldNameOf(widget.Element);
            var k = 2;
            while (names.Contains($"{full}_{k}")) k++;
            names.Add($"{full}_{k}");
            // the value stays: a foreign widget keeps the value of its field, also when it never was filled in here
            var element = WithSpec(widget.Element, spec with { Name = $"{spec.Name}_{k}" });
            if (element is ForeignAnnotation { Value: null } foreign) element = WithValue(foreign, ValueOf(widget.Element));
            changes.Add((widget.Page, widget.Element, element));
        }
        return ApplyFieldChanges(changes, all ? Tr.T("Alle Felder trennen") : Tr.T("Feld trennen"));
    }

    /// <summary>
    /// Changes the properties of the selected fields (one step).
    /// </summary>
    /// <param name="change">The change.</param>
    /// <param name="fieldLevel">true: the change belongs to the field (name, flags, options, format …) and applies to all its
    /// widgets; false: only to the selected widgets (appearance).</param>
    /// <param name="description">Name of the undo step.</param>
    /// <param name="adjust">Optional further adjustment of each changed element; null = none.</param>
    /// <returns>false if nothing changed or editing is not allowed.</returns>
    public bool ChangeFieldSpecs(Func<FieldSpec, FieldSpec> change, bool fieldLevel, string description,
        Func<PageElement, FieldSpec, PageElement>? adjust = null)
    {
        if (!CanEditElements) return false;
        var targets = new List<ElementRef>();
        var seen = new HashSet<PageElement>(ReferenceEqualityComparer.Instance);
        foreach (var selected in FieldsOfSelection)
            foreach (var target in fieldLevel ? SameField(selected) : [selected])
                if (seen.Add(target.Element)) targets.Add(target);
        var changes = new List<(int, PageElement, PageElement)>();
        foreach (var target in targets)
        {
            if (SpecOf(target.Element) is not { } spec) continue;
            var updated = change(spec);
            if (updated.Equals(spec)) continue;
            var element = WithSpec(target.Element, updated);
            if (adjust is not null) element = adjust(element, updated);
            changes.Add((target.Page, target.Element, element));
            // New export value of a radio button: its label is linked to it
            if (spec.Kind == FormFieldKind.Radio && updated.ExportValue != spec.ExportValue)
                RelinkLabels(changes, target.Page, FieldNameOf(target.Element), OnStateOf(target.Element) ?? "", FieldNameOf(target.Element),
                    ElementAppearance.OnStateOf(updated));
        }
        if (!ApplyFieldChanges(changes, description)) return false;
        if (!fieldLevel) RememberLook();   // appearance changed: new fields of this type receive it
        return true;
    }

    /// <summary>Replaces elements (one step) and transfers the selection to the new ones.</summary>
    private bool ApplyFieldChanges(List<(int Page, PageElement Old, PageElement Updated)> changes, string description)
    {
        if (changes.Count == 0) return false;
        FollowLabels(changes);
        var selection = FieldSelection;
        if (!History.ReplaceElements(changes, description)) return false;
        var map = new Dictionary<PageElement, PageElement>(ReferenceEqualityComparer.Instance);
        foreach (var (_, old, updated) in changes) map[old] = updated;
        SelectFields([.. selection.Select(r => map.TryGetValue(r.Element, out var now) ? new ElementRef(r.Page, now) : r)]);
        return true;
    }

    // ------------------------------------------------------------ Inserting fields

    // Radio buttons placed during one use of the tool form a group
    private string? _radioGroup;

    /// <summary>Restarts the „Feld einfügen“ tool (a new radio group).</summary>
    public void StartFieldTool() => _radioGroup = null;

    /// <summary>Default size of a field type in points (width, height), used when the user only clicks.</summary>
    /// <param name="preset">The field type.</param>
    /// <returns>Width and height in points.</returns>
    public static (double Width, double Height) DefaultFieldSize(FieldPreset preset) => preset switch
    {
        FieldPreset.CheckBox or FieldPreset.Radio => (14, 14),
        FieldPreset.Signature => (180, 50),
        FieldPreset.ListBox => (150, 60),
        FieldPreset.ResetButton => (110, 24),
        FieldPreset.Date or FieldPreset.Number => (110, 20),
        FieldPreset.QrCode => (25 * 72 / 25.4, 25 * 72 / 25.4),
        FieldPreset.Barcode => (50 * 72 / 25.4, 15 * 72 / 25.4),
        _ => (180, 20),
    };

    /// <summary>Creates a new field of the chosen type at the given spot and selects it; radio buttons join the group of this tool use.</summary>
    /// <param name="page">Page index.</param>
    /// <param name="rect">Rectangle in page space.</param>
    /// <param name="rotation">Rotation in degrees.</param>
    /// <returns>The new field, or null if editing is not allowed.</returns>
    public PageElement? CreateField(int page, PdfRectangle rect, int rotation)
    {
        if (!CanEditElements) return null;
        PageElement element;
        string description;
        if (FieldPreset == FieldPreset.Signature)
        {
            element = new SignatureFieldElement { Rect = rect, Rotation = rotation, Label = Tr.T("Unterschrift") };
            description = Tr.T("Unterschriftsfeld einfügen");
        }
        else
        {
            var spec = NewSpec(FieldPreset);
            element = new FieldElement
            {
                Rect = rect,
                Rotation = rotation,
                Spec = spec,
                Value = spec.Kind is FormFieldKind.CheckBox or FormFieldKind.Radio ? "Off" : "",
            };
            description = Tr.T("Feld einfügen");
        }
        // Check box: with a label right next to it straight away (one step)
        if (element is FieldElement { Spec.Kind: FormFieldKind.CheckBox or FormFieldKind.Radio } box)
        {
            var text = box.Spec.Kind == FormFieldKind.Radio
                ? Tr.T("Option {0}", ElementsOn(page).Count(e => e is FieldElement { Spec: { Kind: FormFieldKind.Radio } s } && s.Name == box.Spec.Name) + 1)
                : Tr.T("Beschriftung");
            var look = LookFor(box.Spec);
            if (look?.ShowLabel == false) History.AddElement(page, box, description);
            else History.AddElements(new Dictionary<int, IReadOnlyList<PageElement>> { [page] = [box, NewLabel(box, text, look)] }, description);
            SelectedElement = new ElementRef(page, box);
            return box;
        }
        AddElement(page, element, description);
        return element;
    }

    /// <summary>Properties of a new field: free name, border as in common PDF editors, format for date and number.</summary>
    private FieldSpec NewSpec(FieldPreset preset)
    {
        // Text, date and number fields without a border, the others with one; the appearance chosen last applies
        var border = new FieldSpec { BorderColor = preset is FieldPreset.Text or FieldPreset.Date or FieldPreset.Number ? null : 0x8C8C8C };
        // code fields: the look of the code chosen last (inserted or field) if it is of the same family, no border (the code needs its quiet zone)
        if (preset is FieldPreset.QrCode or FieldPreset.Barcode)
        {
            var last = CodeLook.Of(ElementDefaults.Code);
            var code = last.IsMatrix == (preset == FieldPreset.QrCode) ? last : new CodeLook { Format = preset == FieldPreset.QrCode ? Core.Barcodes.BarcodeFormat.Qr : Core.Barcodes.BarcodeFormat.Code128 };
            return new FieldSpec { Name = FreeFieldName(preset == FieldPreset.QrCode ? Tr.T("QR-Code") : Tr.T("Barcode")), Code = code };
        }
        var spec = preset switch
        {
            FieldPreset.Date => border with { Name = FreeFieldName(Tr.T("Datum")), Format = new FieldFormat(FieldFormatKind.Date, Pattern: "dd.mm.yyyy") },
            FieldPreset.Number => border with { Name = FreeFieldName(Tr.T("Zahl")), Alignment = ElementAlignment.Right, Format = new FieldFormat(FieldFormatKind.Number, 2, 2) },
            FieldPreset.CheckBox => border with { Kind = FormFieldKind.CheckBox, Name = FreeFieldName(Tr.T("Kontrollkästchen")), ExportValue = Tr.T("Ja") },
            FieldPreset.Radio => border with
            {
                Kind = FormFieldKind.Radio,
                Name = _radioGroup ??= FreeFieldName(Tr.T("Optionsgruppe")),
                CheckStyle = CheckStyle.Circle,
                ExportValue = FreeExportValue(_radioGroup),
                NoToggleToOff = true,   // as in common PDF editors: a second click does not deselect
            },
            FieldPreset.ComboBox => border with
            {
                Kind = FormFieldKind.ComboBox, Name = FreeFieldName(Tr.T("Kombinationsfeld")),
                Options = [new(Tr.T("Eintrag 1"), Tr.T("Eintrag 1")), new(Tr.T("Eintrag 2"), Tr.T("Eintrag 2"))],
            },
            FieldPreset.ListBox => border with
            {
                Kind = FormFieldKind.ListBox, Name = FreeFieldName(Tr.T("Listenfeld")),
                Options = [new(Tr.T("Eintrag 1"), Tr.T("Eintrag 1")), new(Tr.T("Eintrag 2"), Tr.T("Eintrag 2"))],
            },
            FieldPreset.ResetButton => border with
            {
                Kind = FormFieldKind.PushButton, Name = FreeFieldName(Tr.T("Zurücksetzen")), Caption = Tr.T("Zurücksetzen"), ResetForm = true,
                FillColor = 0xD4D4D4, BorderStyle = FieldBorderStyle.Beveled,
            },
            _ => border with { Name = FreeFieldName(Tr.T("Textfeld")) },
        };
        return LookFor(spec) is { } look ? look.ApplyTo(spec) : spec;
    }

    /// <summary>Returns all field names of the document (foreign ones with their full name, own ones, new ones).</summary>
    /// <returns>A new set of the names.</returns>
    public HashSet<string> FieldNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < History.Count; page++)
            foreach (var element in History.Pages[page].Elements)
                if (IsFormField(element) && FieldNameOf(element) is { Length: > 0 } name) names.Add(name);
        return names;
    }

    /// <summary>„Textfeld1“, „Textfeld2“ …: the first free name; it is added to <paramref name="names"/>.</summary>
    private string FreeFieldName(string stem, HashSet<string>? names = null)
    {
        names ??= FieldNames();
        var k = 1;
        while (names.Contains($"{stem}{k}")) k++;
        names.Add($"{stem}{k}");   // several copies in one step: each gets its own name
        return $"{stem}{k}";
    }

    /// <summary>Export value for a further radio button of the group: „Auswahl1“, „Auswahl2“ …</summary>
    private string FreeExportValue(string group, HashSet<string>? used = null)
    {
        used ??= ExportValuesOf(group);
        var k = 1;
        while (used.Contains(Tr.T("Auswahl") + k)) k++;
        used.Add(Tr.T("Auswahl") + k);
        return Tr.T("Auswahl") + k;
    }

    /// <summary>Returns the export values of the radio buttons of a group (own and foreign).</summary>
    /// <param name="group">Full name of the group.</param>
    /// <returns>A new set of the export values.</returns>
    public HashSet<string> ExportValuesOf(string group)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < History.Count; page++)
            foreach (var element in History.Pages[page].Elements)
                if (IsFormField(element) && SpecOf(element) is { Kind: FormFieldKind.Radio } s && FieldNameOf(element) == group) used.Add(OnStateOf(element) ?? s.ExportValue);
        return used;
    }

    // ------------------------------------------------------------ Editing

    /// <summary>Deletes the selected fields together with their labels (one step; the selection is always on one page).</summary>
    /// <returns>false if nothing was deleted.</returns>
    public bool DeleteSelectedFields()
    {
        var selection = FieldSelection;
        if (selection.Count == 0 || !CanEditElements) return false;
        // Together with their labels
        var remove = new List<PageElement>(selection.Select(r => r.Element));
        foreach (var selected in selection)
            foreach (var label in LabelsOf(selected))
                if (!remove.Contains(label.Element)) remove.Add(label.Element);
        SelectedElement = null;
        return History.RemoveElements(selection[0].Page, remove, remove.Count == 1 ? Tr.T("Element löschen") : Tr.T("Felder löschen"));
    }

    /// <summary>Duplicates the selected fields with an offset (Ctrl+D): new fields with free names, own radio buttons in the same group.</summary>
    /// <returns>false if nothing was duplicated.</returns>
    public bool DuplicateSelectedFields()
    {
        if (!CanEditElements || FieldSelection is not { Count: > 0 } selection) return false;
        var page = selection[0].Page;
        var copies = new List<PageElement>();
        var labels = new List<PageElement>();
        // Read names and export values once and keep them up to date; otherwise several copies would get the same name and share
        // their value
        var names = FieldNames();
        var exports = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var selected in FieldsOfSelection)
        {
            if (SpecOf(selected.Element) is not { } spec || selected.Element is SignatureFieldElement) continue;
            var r = selected.Element.Rect;
            var rect = new PdfRectangle(r.Left, r.Bottom - r.Height - 6, r.Right, r.Bottom - 6);
            // Own radio buttons: a further option of the same group. Foreign groups cannot receive new children, so the copy becomes
            // a group of its own (otherwise it would be named like the partial name and the writer would rename it)
            var copy = spec.Kind == FormFieldKind.Radio && selected.Element is FieldElement
                ? spec with { ExportValue = FreeExportValue(spec.Name, exports.TryGetValue(spec.Name, out var used) ? used : exports[spec.Name] = ExportValuesOf(spec.Name)) }
                : spec with { Name = FreeFieldName(StemOf(spec.Name), names) };
            var duplicate = new FieldElement
            {
                Rect = rect,
                Rotation = TurnOf(selected.Element),
                Spec = copy,
                Value = copy.Kind is FormFieldKind.CheckBox or FormFieldKind.Radio ? "Off" : "",
            };
            copies.Add(duplicate);
            // The label too: same text, same side and gap
            foreach (var label in LabelsOf(selected))
                if (label.Element is TextElement text && LabelSide(text, selected.Element) is var (left, gap))
                    labels.Add(PlaceLabel(text with
                    {
                        LabelFor = copy.Name,
                        LabelState = copy.Kind == FormFieldKind.Radio ? ElementAppearance.OnStateOf(copy) : "",
                    }, duplicate, left, gap));
        }
        if (copies.Count == 0) return false;
        History.AddElements(new Dictionary<int, IReadOnlyList<PageElement>> { [page] = [.. copies, .. labels] }, Tr.T("Felder verdoppeln"));
        SelectFields([.. copies.Select(c => new ElementRef(page, c))]);
        return true;
    }

    /// <summary>„Textfeld12“ → „Textfeld“ (stem for the next free name).</summary>
    private static string StemOf(string name) => name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9') is { Length: > 0 } stem ? stem : name;

    /// <summary>Rotation of the field; foreign widgets rotate via /MK /R (their Rotation is 0, so label and copy would otherwise lie crosswise).</summary>
    private static int TurnOf(PageElement element) => element is ForeignAnnotation foreign ? ElementAppearance.WidgetRotation(foreign.Original) : element.Rotation;

    /// <summary>Rotation with which a page is displayed (file and view, clockwise).</summary>
    private int DisplayRotation(int page) => (History.Pages[page].Page.Rotate + Rotation) % 360;

    /// <summary>Rectangle in page space → as it lies on the screen (rotated; „left“ is then small x).</summary>
    private static PdfRectangle ToDisplay(PdfRectangle r, int rotation) => Turn(r, rotation, inverse: false);

    private static PdfRectangle FromDisplay(PdfRectangle r, int rotation) => Turn(r, rotation, inverse: true);

    private static PdfRectangle Turn(PdfRectangle r, int rotation, bool inverse)
    {
        (double X, double Y) P(double x, double y) => (rotation, inverse) switch
        {
            (90, false) => (y, -x),
            (90, true) => (-y, x),
            (180, _) => (-x, -y),
            (270, false) => (-y, x),
            (270, true) => (y, -x),
            _ => (x, y),
        };
        var (x0, y0) = P(r.Left, r.Bottom);
        var (x1, y1) = P(r.Right, r.Top);
        return new PdfRectangle(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
    }

    /// <summary>Selected fields; a selected label stands for its check box (each field once).</summary>
    public IReadOnlyList<ElementRef> FieldsOfSelection
    {
        get
        {
            var result = new List<ElementRef>();
            foreach (var selected in FieldSelection)
            {
                var target = IsFieldLabel(selected.Element) && FieldOfLabel(selected) is { } box ? box : selected;
                if (!result.Any(r => ReferenceEquals(r.Element, target.Element))) result.Add(target);
            }
            return result;
        }
    }

    /// <summary>
    /// Aligns several fields of a page (left, centre, right, top, middle, bottom) or makes them the same size, relative to the field
    /// selected last, or distributes them. Calculations follow how the page lies on the screen; check boxes count together with their
    /// labels.
    /// </summary>
    /// <param name="arrange">The arrangement.</param>
    /// <param name="gap">For distributing: null = evenly between the first and last field (needs at least three), otherwise this
    /// gap in points from the first (clamped to −1000 … 1000).</param>
    /// <returns>false if nothing changed.</returns>
    public bool ArrangeFields(FieldArrange arrange, double? gap = null)
    {
        if (!CanEditElements || FieldsOfSelection is not { Count: > 1 } selection) return false;
        var page = selection[0].Page;
        var rotation = DisplayRotation(page);
        var changes = new List<(int, PageElement, PageElement)>();
        void Place(ElementRef field, PdfRectangle display)
        {
            var rect = FromDisplay(display, rotation);
            if (Math.Abs(rect.Left - field.Element.Rect.Left) + Math.Abs(rect.Bottom - field.Element.Rect.Bottom) +
                Math.Abs(rect.Right - field.Element.Rect.Right) + Math.Abs(rect.Top - field.Element.Rect.Top) < 1e-6) return;
            changes.Add((field.Page, field.Element, Resized(field.Element, rect)));
        }

        if (arrange is FieldArrange.DistributeHorizontally or FieldArrange.DistributeVertically)
        {
            var horizontal = arrange == FieldArrange.DistributeHorizontally;
            // Per field its area including labels, in screen order (left to right or top to bottom)
            var items = selection.Select(field =>
            {
                var own = ToDisplay(field.Element.Rect, rotation);
                var group = own;
                foreach (var label in LabelsOf(field))
                {
                    var l = ToDisplay(label.Element.Rect, rotation);
                    group = new PdfRectangle(Math.Min(group.Left, l.Left), Math.Min(group.Bottom, l.Bottom), Math.Max(group.Right, l.Right), Math.Max(group.Top, l.Top));
                }
                return (Field: field, Own: own, Group: group);
            }).OrderBy(i => horizontal ? i.Group.Left : -i.Group.Top).ToList();
            double Size((ElementRef, PdfRectangle, PdfRectangle Group) i) => horizontal ? i.Group.Width : i.Group.Height;
            double step;
            if (gap is { } fixedGap) step = Math.Clamp(fixedGap, -1000, 1000);
            else
            {
                if (items.Count < 3) return false;
                var span = horizontal ? items[^1].Group.Right - items[0].Group.Left : items[0].Group.Top - items[^1].Group.Bottom;
                step = (span - items.Sum(Size)) / (items.Count - 1);
            }
            var position = horizontal ? items[0].Group.Left : items[0].Group.Top;
            foreach (var (field, own, group) in items)
            {
                var shift = horizontal ? position - group.Left : position - group.Top;
                Place(field, horizontal
                    ? new PdfRectangle(own.Left + shift, own.Bottom, own.Right + shift, own.Top)
                    : new PdfRectangle(own.Left, own.Bottom + shift, own.Right, own.Top + shift));
                position += horizontal ? group.Width + step : -(group.Height + step);
            }
            return ApplyFieldChanges(changes, Tr.T("Felder verteilen"));
        }

        var anchor = ToDisplay(selection[^1].Element.Rect, rotation);
        foreach (var selected in selection.Take(selection.Count - 1))
        {
            var r = ToDisplay(selected.Element.Rect, rotation);
            Place(selected, arrange switch
            {
                FieldArrange.Left => r with { Left = anchor.Left, Right = anchor.Left + r.Width },
                FieldArrange.Right => r with { Left = anchor.Right - r.Width, Right = anchor.Right },
                FieldArrange.Center => r with { Left = (anchor.Left + anchor.Right - r.Width) / 2, Right = (anchor.Left + anchor.Right + r.Width) / 2 },
                FieldArrange.Top => r with { Bottom = anchor.Top - r.Height, Top = anchor.Top },
                FieldArrange.Bottom => r with { Bottom = anchor.Bottom, Top = anchor.Bottom + r.Height },
                FieldArrange.Middle => r with { Bottom = (anchor.Bottom + anchor.Top - r.Height) / 2, Top = (anchor.Bottom + anchor.Top + r.Height) / 2 },
                FieldArrange.SameWidth => r with { Right = r.Left + anchor.Width },
                _ => r with { Bottom = r.Top - anchor.Height },
            });
        }
        return ApplyFieldChanges(changes, Tr.T("Felder anordnen"));
    }

    /// <summary>
    /// Moves or resizes a field. Fields created by other programs receive their properties when resized (so the appearance is
    /// redrawn when saving instead of being distorted).
    /// </summary>
    /// <param name="element">The field.</param>
    /// <param name="rect">New rectangle in page space.</param>
    /// <returns>The moved or resized element.</returns>
    public PageElement Resized(PageElement element, PdfRectangle rect)
    {
        var moved = ElementAppearance.Moved(element, rect);
        if (moved is ForeignAnnotation { IsResized: true, Spec: null } foreign && SpecOf(foreign) is { } spec) moved = foreign with { Spec = spec };
        return moved;
    }

    // ------------------------------------------------------------ Labels (check boxes and radio buttons)

    /// <summary>Checks whether the element is the label of a check box or radio button.</summary>
    /// <param name="element">The element.</param>
    /// <returns>true for a label.</returns>
    public static bool IsFieldLabel(PageElement element) => element is TextElement { IsField: false, LabelFor.Length: > 0 };

    /// <summary>Default gap between label and check box in points.</summary>
    private const double LabelGap = 4;

    /// <summary>Whether the field has labels (check boxes and radio buttons).</summary>
    private static bool Labelled(FieldSpec? spec) => spec?.Kind is FormFieldKind.CheckBox or FormFieldKind.Radio;

    /// <summary>Returns the labels of a field on its page (name and, for radio buttons, export value match).</summary>
    /// <param name="field">The field.</param>
    /// <returns>The labels.</returns>
    public IEnumerable<ElementRef> LabelsOf(ElementRef field)
    {
        if (!Labelled(SpecOf(field.Element))) yield break;
        var name = FieldNameOf(field.Element);
        var state = SpecOf(field.Element)!.Kind == FormFieldKind.Radio ? OnStateOf(field.Element) ?? "" : "";
        foreach (var element in ElementsOn(field.Page))
            if (element is TextElement { IsField: false } label && label.LabelFor == name && label.LabelState == state)
                yield return new ElementRef(field.Page, element);
    }

    /// <summary>Finds the check box of a label (same page).</summary>
    /// <param name="label">The label.</param>
    /// <returns>The check box, or null if it no longer exists.</returns>
    public ElementRef? FieldOfLabel(ElementRef label)
    {
        if (label.Element is not TextElement { LabelFor.Length: > 0 } text) return null;
        foreach (var element in ElementsOn(label.Page))
            if (IsFormField(element) && Labelled(SpecOf(element)) && FieldNameOf(element) == text.LabelFor &&
                (SpecOf(element)!.Kind != FormFieldKind.Radio || OnStateOf(element) == text.LabelState))
                return new ElementRef(label.Page, element);
        return null;
    }

    /// <summary>Directions in page space for a field with this rotation: reading direction (right) and up.</summary>
    private static ((double X, double Y) Right, (double X, double Y) Up) Directions(int rotation) => rotation switch
    {
        90 => ((0, 1), (-1, 0)),
        180 => ((-1, 0), (0, -1)),
        270 => ((0, -1), (1, 0)),
        _ => ((1, 0), (0, 1)),
    };

    /// <summary>Places the label next to the field: left or right, vertically centred on it, with gap <paramref name="gap"/>.</summary>
    private static TextElement PlaceLabel(TextElement label, PageElement field, bool left, double gap)
    {
        var (right, _) = Directions(label.Rotation);
        var probe = label with { Rect = new PdfRectangle(0, 0, 1000, 100), Multiline = false };
        var face = ElementFace.For(label.Font);
        var width = ElementAppearance.Layout(probe, face).Sum(l => l.Width) + 2 * ElementAppearance.Padding + 1;
        var height = ElementAppearance.NeededHeight(probe);
        var r = field.Rect;
        double cx = (r.Left + r.Right) / 2, cy = (r.Bottom + r.Top) / 2;
        var fieldHalf = Math.Abs(right.X) * r.Width / 2 + Math.Abs(right.Y) * r.Height / 2;
        var distance = fieldHalf + gap + width / 2;
        var sign = left ? -1 : 1;
        double lx = cx + sign * right.X * distance, ly = cy + sign * right.Y * distance;
        // Upright w × h, rotated in page space according to the rotation
        var (w, h) = label.Rotation is 90 or 270 ? (height, width) : (width, height);
        return label with
        {
            Rect = new PdfRectangle(lx - w / 2, ly - h / 2, lx + w / 2, ly + h / 2),
            Multiline = false,
            Alignment = left ? ElementAlignment.Right : ElementAlignment.Left,
        };
    }

    /// <summary>Whether the label lies to the left of the field (in reading direction), and the gap between the edges (0 … 2000).</summary>
    private static (bool Left, double Gap) LabelSide(TextElement label, PageElement field)
    {
        var (right, _) = Directions(TurnOf(field));
        static (double Min, double Max) Project(PdfRectangle r, (double X, double Y) d)
        {
            double x0 = Math.Min(r.Left, r.Right), x1 = Math.Max(r.Left, r.Right), y0 = Math.Min(r.Bottom, r.Top), y1 = Math.Max(r.Bottom, r.Top);
            return d.X > 0 ? (x0, x1) : d.X < 0 ? (-x1, -x0) : d.Y > 0 ? (y0, y1) : (-y1, -y0);
        }
        var (l0, l1) = Project(label.Rect, right);
        var (f0, f1) = Project(field.Rect, right);
        var left = l0 + l1 < f0 + f1;
        return (left, Math.Clamp(left ? f0 - l1 : l0 - f1, 0, 2000));
    }

    /// <summary>New label for a check box (font size matching the height), on the right unless the remembered look says otherwise.</summary>
    private TextElement NewLabel(PageElement field, string text, FieldLook? look = null)
    {
        var spec = SpecOf(field)!;
        var (_, height) = field.UprightSize;
        var label = new TextElement
        {
            Rect = default,
            Rotation = TurnOf(field),
            Text = text,
            Font = look?.LabelFont ?? ElementFont.Sans,
            Color = look?.LabelColor ?? 0,
            FontSize = look is { LabelSize: > 0 } l ? l.LabelSize : MainViewModel.FontSizes.MinBy(s => Math.Abs(s - Math.Clamp(height * 0.72, 7, 14))),   // taken from the list of sizes (otherwise e.g. 13 would result, which is not in the list)
            LabelFor = FieldNameOf(field),
            LabelState = spec.Kind == FormFieldKind.Radio ? OnStateOf(field) ?? "" : "",
        };
        return PlaceLabel(label, field, look?.LabelLeft ?? false, look?.LabelGap ?? LabelGap);
    }

    /// <summary>Labels follow when their fields change: same side and same gap, vertically centred.</summary>
    private void FollowLabels(List<(int Page, PageElement Old, PageElement Updated)> changes)
    {
        var moved = new HashSet<PageElement>(ReferenceEqualityComparer.Instance);
        foreach (var (_, old, _) in changes) moved.Add(old);
        var added = new List<(int, PageElement, PageElement)>();
        foreach (var (page, old, updated) in changes)
        {
            if (!Labelled(SpecOf(old)) || old.Rect == updated.Rect) continue;
            foreach (var label in LabelsOf(new ElementRef(page, old)))
            {
                if (moved.Contains(label.Element) || label.Element is not TextElement text) continue;
                var (left, gap) = LabelSide(text, old);
                added.Add((page, text, PlaceLabel(text, updated, left, gap)));
                moved.Add(text);
            }
        }
        changes.AddRange(added);
    }

    /// <summary>Sets the label text of the selected check boxes (empty: removes it; none yet: creates one next to it), in one step.</summary>
    /// <param name="text">The text; line breaks become spaces.</param>
    /// <returns>false if nothing changed.</returns>
    public bool SetLabelText(string text)
    {
        if (!CanEditElements) return false;
        text = text.ReplaceLineEndings(" ").Trim();
        var adds = new List<PageElement>();
        var changes = new List<(int, PageElement, PageElement)>();
        var removes = new List<PageElement>();
        var page = -1;
        foreach (var selected in FieldsOfSelection)
        {
            if (!Labelled(SpecOf(selected.Element))) continue;
            page = selected.Page;
            var labels = LabelsOf(selected).ToList();
            if (labels.Count == 0)
            {
                if (text.Length > 0) adds.Add(NewLabel(selected.Element, text, LookFor(SpecOf(selected.Element)!)));
                continue;
            }
            foreach (var label in labels)
            {
                var old = (TextElement)label.Element;
                if (text.Length == 0) removes.Add(old);
                else if (old.Text != text)
                {
                    var (left, gap) = LabelSide(old, selected.Element);
                    changes.Add((label.Page, old, PlaceLabel(old with { Text = text }, selected.Element, left, gap)));
                }
            }
        }
        if (page < 0 || adds.Count + changes.Count + removes.Count == 0) return false;
        var elements = ElementsOn(page).ToList();
        foreach (var (_, old, updated) in changes) elements[elements.IndexOf(old)] = updated;
        elements.RemoveAll(removes.Contains);
        elements.AddRange(adds);
        var selection = FieldSelection;
        var fields = FieldsOfSelection;
        History.SetElements(page, elements, Tr.T("Beschriftung ändern"));
        var kept = Followed(selection, changes, removes);
        SelectFields(kept.Count > 0 ? kept : [.. fields]);   // only the deleted label was selected: select its check box
        return true;
    }

    /// <summary>
    /// Selection after a step: replaced elements by their new versions, deleted ones removed (otherwise, after changing the text or
    /// side of a label, the selection would point to the old instance: frame at the old position, arrow keys and dragging do nothing).
    /// </summary>
    private static List<ElementRef> Followed(IReadOnlyList<ElementRef> selection, List<(int, PageElement Old, PageElement Updated)> changes, List<PageElement> removed)
    {
        var map = new Dictionary<PageElement, PageElement>(ReferenceEqualityComparer.Instance);
        foreach (var (_, old, updated) in changes) map[old] = updated;
        var gone = new HashSet<PageElement>(removed, ReferenceEqualityComparer.Instance);
        return [.. selection.Where(r => !gone.Contains(r.Element)).Select(r => map.TryGetValue(r.Element, out var now) ? new ElementRef(r.Page, now) : r)];
    }

    /// <summary>Moves the labels of the selected check boxes to the left or right (same gap), in one step.</summary>
    /// <param name="left">true = left.</param>
    /// <returns>false if nothing changed.</returns>
    public bool SetLabelSide(bool left)
    {
        var changes = new List<(int, PageElement, PageElement)>();
        foreach (var selected in FieldsOfSelection)
            foreach (var label in LabelsOf(selected))
                if (label.Element is TextElement text && LabelSide(text, selected.Element) is var (isLeft, gap) && isLeft != left)
                    changes.Add((label.Page, text, PlaceLabel(text, selected.Element, left, gap)));
        if (changes.Count == 0) return false;
        var selection = FieldSelection;
        if (!History.ReplaceElements(changes, Tr.T("Beschriftung verschieben"))) return false;
        SelectFields(Followed(selection, changes, []));
        RememberLook();
        return true;
    }

    // Text of labels switched off (keyed by name and state of the check box), so that it returns when they are switched on again
    private readonly Dictionary<(string, string), string> _hiddenLabels = [];

    /// <summary>
    /// Switches the labels of the selected check boxes on or off (one step). When switched on again: the text used last, otherwise
    /// „Option n“ or „Beschriftung“.
    /// </summary>
    /// <param name="on">true = show.</param>
    /// <returns>false if nothing changed.</returns>
    public bool ShowLabels(bool on)
    {
        if (!CanEditElements || FieldsOfSelection is not { Count: > 0 } fields) return false;
        var page = fields[0].Page;
        var elements = ElementsOn(page).ToList();
        var changed = false;
        foreach (var field in fields)
        {
            if (field.Page != page || SpecOf(field.Element) is not { } spec || !Labelled(spec)) continue;
            var key = (FieldNameOf(field.Element), spec.Kind == FormFieldKind.Radio ? OnStateOf(field.Element) ?? "" : "");
            var labels = LabelsOf(field).ToList();
            if (on && labels.Count == 0)
            {
                var text = _hiddenLabels.GetValueOrDefault(key) ?? (spec.Kind == FormFieldKind.Radio
                    ? Tr.T("Option {0}", 1 + ElementsOn(page).TakeWhile(e => !ReferenceEquals(e, field.Element))
                        .Count(e => SpecOf(e) is { Kind: FormFieldKind.Radio } s && FieldNameOf(e) == key.Item1))
                    : Tr.T("Beschriftung"));
                elements.Add(NewLabel(field.Element, text, LookFor(spec)));
                changed = true;
            }
            else if (!on)
                foreach (var label in labels)
                {
                    _hiddenLabels[key] = ((TextElement)label.Element).Text;
                    elements.Remove(label.Element);
                    changed = true;
                }
        }
        if (!changed) return false;
        var selection = FieldSelection.Where(r => elements.Contains(r.Element)).ToList();
        History.SetElements(page, elements, on ? Tr.T("Beschriftung einblenden") : Tr.T("Beschriftung ausblenden"));
        SelectFields(selection.Count > 0 ? selection : fields);
        RememberLook();
        return true;
    }

    /// <summary>Changes font, size or colour of the labels of the selected check boxes (the width adapts), in one step.</summary>
    /// <param name="change">The change.</param>
    /// <param name="description">Name of the undo step.</param>
    /// <returns>false if nothing changed.</returns>
    public bool SetLabelStyle(Func<TextElement, TextElement> change, string description)
    {
        if (!CanEditElements) return false;
        var changes = new List<(int, PageElement, PageElement)>();
        foreach (var field in FieldsOfSelection)
            foreach (var label in LabelsOf(field))
                if (label.Element is TextElement text && change(text) is var styled && styled != text)
                {
                    var (left, gap) = LabelSide(text, field.Element);
                    changes.Add((label.Page, text, PlaceLabel(styled, field.Element, left, gap)));
                }
        if (changes.Count == 0) return false;
        var selection = FieldSelection;
        var map = new Dictionary<PageElement, PageElement>(ReferenceEqualityComparer.Instance);
        foreach (var (_, old, updated) in changes) map[old] = updated;
        if (!History.ReplaceElements(changes, description)) return false;
        SelectFields([.. selection.Select(r => map.TryGetValue(r.Element, out var now) ? new ElementRef(r.Page, now) : r)]);
        RememberLook();
        return true;
    }

    /// <summary>Sets the gap between label and check box for all selected ones, in one step.</summary>
    /// <param name="gap">Gap in points, clamped to 0 … 500.</param>
    /// <returns>false if nothing changed.</returns>
    public bool SetLabelGap(double gap)
    {
        if (!double.IsFinite(gap)) return false;
        gap = Math.Clamp(gap, 0, 500);
        var changes = new List<(int, PageElement, PageElement)>();
        foreach (var field in FieldsOfSelection)
            foreach (var label in LabelsOf(field))
                if (label.Element is TextElement text && LabelSide(text, field.Element) is var (left, old) && Math.Abs(old - gap) > 0.01)
                    changes.Add((label.Page, text, PlaceLabel(text, field.Element, left, gap)));
        if (changes.Count == 0) return false;
        var selection = FieldSelection;
        var map = new Dictionary<PageElement, PageElement>(ReferenceEqualityComparer.Instance);
        foreach (var (_, old, updated) in changes) map[old] = updated;
        if (!History.ReplaceElements(changes, Tr.T("Abstand der Beschriftung ändern"))) return false;
        SelectFields([.. selection.Select(r => map.TryGetValue(r.Element, out var now) ? new ElementRef(r.Page, now) : r)]);
        RememberLook();
        return true;
    }

    /// <summary>Returns the label gap of the primary field.</summary>
    /// <returns>The gap in points, rounded to 0.1; null without a label.</returns>
    public double? LabelGapOfSelected() =>
        FieldsOfSelection is { Count: > 0 } fields && LabelsOf(fields[^1]).FirstOrDefault() is { Element: TextElement text }
            ? Math.Round(LabelSide(text, fields[^1].Element).Gap, 1)
            : null;

    // ------------------------------------------------------------ Remembering the appearance for new fields

    /// <summary>Type of a field for the remembered appearance (date and number separate, signature fields not at all).</summary>
    private static string? LookKey(FieldSpec spec) => spec.Code is not null ? null : spec.Kind switch
    {
        FormFieldKind.CheckBox => nameof(FieldPreset.CheckBox),
        FormFieldKind.Radio => nameof(FieldPreset.Radio),
        FormFieldKind.ComboBox => nameof(FieldPreset.ComboBox),
        FormFieldKind.ListBox => nameof(FieldPreset.ListBox),
        FormFieldKind.PushButton => nameof(FieldPreset.ResetButton),
        FormFieldKind.Text => spec.Format.Kind switch
        {
            FieldFormatKind.Date => nameof(FieldPreset.Date),
            FieldFormatKind.Number or FieldFormatKind.Percent => nameof(FieldPreset.Number),
            _ => nameof(FieldPreset.Text),
        },
        _ => null,
    };

    /// <summary>Appearance chosen last for this field type; null if none has been remembered yet.</summary>
    private static FieldLook? LookFor(FieldSpec spec) =>
        LookKey(spec) is { } key ? Services.UserSettings.Current.FieldLooks?.FirstOrDefault(l => l.Kind == key) : null;

    /// <summary>
    /// Remembers the appearance of the primary field for new fields of its type (in the settings): border, fill, font, alignment,
    /// mark, and for check boxes also the label (on/off, font, size, colour, side, gap).
    /// </summary>
    private void RememberLook()
    {
        if (FieldsOfSelection is not { Count: > 0 } fields || SpecOf(fields[^1].Element) is not { } spec || LookKey(spec) is not { } key) return;
        var look = FieldLook.From(key, spec);
        if (Labelled(spec))
        {
            look = LabelsOf(fields[^1]).FirstOrDefault() is { Element: TextElement label }
                ? look with
                {
                    ShowLabel = true,
                    LabelFont = label.Font,
                    LabelSize = label.FontSize,
                    LabelColor = label.Color,
                    LabelLeft = LabelSide(label, fields[^1].Element).Left,
                    LabelGap = LabelSide(label, fields[^1].Element).Gap,
                }
                : LookFor(spec) is { } previous   // switched off: the other label settings stay as they were last
                    ? look with
                    {
                        ShowLabel = false,
                        LabelFont = previous.LabelFont,
                        LabelSize = previous.LabelSize,
                        LabelColor = previous.LabelColor,
                        LabelLeft = previous.LabelLeft,
                        LabelGap = previous.LabelGap,
                    }
                    : look with { ShowLabel = false };
        }
        if (LookFor(spec) == look) return;   // unchanged: do not write (otherwise every step of the gap control would write the settings file)
        Services.UserSettings.Update(s => s with
        {
            FieldLooks = [.. (s.FieldLooks ?? []).Where(l => l.Kind != key), look],
        });
    }

    /// <summary>Returns the label element of the primary field.</summary>
    /// <returns>The label, or null if it has none.</returns>
    public TextElement? LabelElementOfSelected() =>
        FieldsOfSelection is { Count: > 0 } fields && LabelsOf(fields[^1]).FirstOrDefault() is { Element: TextElement text } ? text : null;

    /// <summary>Returns text and side of the primary field's label (for the properties panel).</summary>
    /// <returns>Text and whether it is on the left; null without a label.</returns>
    public (string Text, bool Left)? LabelOfSelected()
    {
        if (FieldsOfSelection is not { Count: > 0 } fields || LabelsOf(fields[^1]).FirstOrDefault() is not { Element: TextElement text }) return null;
        var selected = fields[^1];
        return (text.Text, LabelSide(text, selected.Element).Left);
    }

    /// <summary>Relinks labels to a new name or export value (renaming, changing the export value).</summary>
    private void RelinkLabels(List<(int Page, PageElement Old, PageElement Updated)> changes, int page, string oldName, string oldState, string newName, string newState)
    {
        foreach (var element in ElementsOn(page))
            if (element is TextElement { IsField: false } label && label.LabelFor == oldName && label.LabelState == oldState &&
                !changes.Any(c => ReferenceEquals(c.Old, label)))
                changes.Add((page, label, label with { LabelFor = newName, LabelState = newState }));
    }

    // ------------------------------------------------------------ Renaming

    /// <summary>
    /// Renames the selected field: all its widgets, and the calculations of other fields that refer to it, follow (one step).
    /// For fields created by other programs only the own part of the name changes (the names of the parents stay). A name of fields of the
    /// same kind links it to them (it takes over their value, <see cref="NameUseFor"/>); a name of another kind of field is refused.
    /// </summary>
    /// <param name="name">The new partial name.</param>
    /// <returns>false if nothing changed.</returns>
    public bool RenameSelectedField(string name)
    {
        if (!CanEditElements || FieldsOfSelection is not { Count: > 0 } fields || fields[^1] is var selected && SpecOf(selected.Element) is not { } spec || name == spec.Name) return false;
        var oldFull = FieldNameOf(selected.Element);
        var newFull = FullNameFor(selected.Element, spec, name);
        // linked to fields of that name: it takes over their value (they are one field, with one value)
        var (use, _) = NameUseFor(name);
        if (use == NameUse.Taken) return false;
        string? joined = null;
        if (use == NameUse.Link)
            for (var page = 0; page < History.Count && joined is null; page++)
                foreach (var element in History.Pages[page].Elements)
                    if (IsFormField(element) && FieldNameOf(element) == newFull)
                    {
                        joined = ValueOf(element);
                        break;
                    }
        var changes = new List<(int, PageElement, PageElement)>();
        var renamed = new HashSet<PageElement>(ReferenceEqualityComparer.Instance);
        foreach (var widget in SameField(selected))
            if (SpecOf(widget.Element) is { } s)
            {
                var element = WithSpec(widget.Element, s with { Name = name });
                if (joined is not null && ValueOf(element) != joined) element = WithValue(element, joined);
                changes.Add((widget.Page, widget.Element, element));
                renamed.Add(widget.Element);
                var state = s.Kind == FormFieldKind.Radio ? OnStateOf(widget.Element) ?? "" : "";
                RelinkLabels(changes, widget.Page, oldFull, state, newFull, state);   // labels are linked by name
            }
        // Calculations that refer to the old name
        for (var page = 0; page < History.Count; page++)
            foreach (var element in History.Pages[page].Elements)
                if (!renamed.Contains(element) && IsFormField(element) && SpecOf(element) is { Calculation: { } calc } other && calc.Fields.Contains(oldFull))
                    changes.Add((page, element, WithSpec(element, other with
                    {
                        // Formula: rename it inside the formula as well
                        Calculation = calc.Formula is { } formula
                            ? FieldCalculation.Of(formula.Renamed(oldFull, newFull))
                            : calc with { Fields = [.. calc.Fields.Select(f => f == oldFull ? newFull : f)] },
                    })));
        return ApplyFieldChanges(changes, Tr.T("Feldname ändern"));
    }

    // ------------------------------------------------------------ Tab order

    /// <summary>Whether the tab order is shown (numbers on the fields); a click puts the field at the next position.</summary>
    public bool ShowTabOrder { get; private set; }

    // Per page the position at which the next click puts a field
    private readonly Dictionary<int, int> _tabNext = [];

    /// <summary>Shows or hides the tab order and restarts the numbering.</summary>
    /// <param name="on">true = show.</param>
    public void SetShowTabOrder(bool on)
    {
        ShowTabOrder = on;
        _tabNext.Clear();
        OnPropertyChanged(nameof(ShowTabOrder));
        FieldSelectionChanged?.Invoke();
    }

    /// <summary>Returns the number of the field in the tab order of its page.</summary>
    /// <param name="page">Page index.</param>
    /// <param name="element">The field.</param>
    /// <returns>The number from 1; 0 if it is not a field on that page.</returns>
    public int TabNumberOf(int page, PageElement element)
    {
        var n = 0;
        foreach (var e in ElementsOn(page))
        {
            if (!IsFormField(e)) continue;
            n++;
            if (ReferenceEquals(e, element)) return n;
        }
        return 0;
    }

    /// <summary>Puts the field at the next position of its page's tab order (click by click 1, 2, 3 …), in one step.</summary>
    /// <param name="field">The field.</param>
    /// <returns>false if nothing changed, also if the field is no longer on the page (outdated reference, for example after
    /// undoing).</returns>
    public bool PlaceInTabOrder(ElementRef field)
    {
        if (!CanEditElements || !IsFormField(field.Element)) return false;
        var elements = ElementsOn(field.Page).ToList();
        var slots = Enumerable.Range(0, elements.Count).Where(i => IsFormField(elements[i])).ToList();
        var fields = slots.Select(i => elements[i]).ToList();
        // Not among the fields: before, a page without fields divided by zero, and on another page the outdated field was
        // inserted while the last one dropped out
        if (!fields.Any(f => ReferenceEquals(f, field.Element))) return false;
        var at = _tabNext.GetValueOrDefault(field.Page) % fields.Count;
        _tabNext[field.Page] = at + 1;
        if (ReferenceEquals(fields[at], field.Element))
        {
            FieldSelectionChanged?.Invoke();
            return false;
        }
        fields.Remove(field.Element);
        fields.Insert(at, field.Element);
        for (var j = 0; j < slots.Count; j++) elements[slots[j]] = fields[j];
        History.SetElements(field.Page, elements, Tr.T("Tab-Reihenfolge ändern"));
        SelectedElement = field;
        return true;
    }

    // ------------------------------------------------------------ Reset and hints

    /// <summary>
    /// „Zurücksetzen“ button (/ResetForm, without script): resets all fields (or those listed in /Fields, or with bit 1 in /Flags all
    /// except those) to their default value (/DV); check boxes go off if their on state is not the default. One step.
    /// </summary>
    /// <param name="button">The button that was pressed.</param>
    /// <returns>false if nothing changed or filling in is not allowed.</returns>
    public bool ResetForm(PageElement button)
    {
        if (!CanFillForms) return false;
        HashSet<string>? listed = null;
        var exclude = false;
        if (button is ForeignAnnotation foreign && foreign.Original.GetDictionary(PdfName.Get("A")) is { } action)
        {
            exclude = ((action.GetInteger(PdfName.Get("Flags")) ?? 0) & 1) != 0;
            if (action.GetArray(PdfName.Get("Fields")) is { } fields)
            {
                listed = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < Math.Min(fields.Count, 10_000); i++)
                    switch (fields.Get(i))
                    {
                        case PdfString s:
                            listed.Add(s.ToText());
                            break;
                        case PdfDictionary d when FullName(d) is { Length: > 0 } full:
                            listed.Add(full);   // full name including parents (/T alone would not match children)
                            break;
                    }
            }
        }
        var changes = new List<(int, PageElement, PageElement)>();
        for (var page = 0; page < History.Count; page++)
            foreach (var element in History.Pages[page].Elements)
            {
                if (!IsFillable(element) || SpecOf(element) is not { } spec) continue;
                if (listed is not null && Listed(listed, FieldNameOf(element)) == exclude) continue;
                var value = spec.Kind is FormFieldKind.CheckBox or FormFieldKind.Radio
                    ? spec.DefaultValue.Length > 0 && spec.DefaultValue == OnStateOf(element) ? spec.DefaultValue : "Off"
                    : spec.DefaultValue;
                if (ValueOf(element) != value) changes.Add((page, element, WithValue(element, value)));
            }
        if (changes.Count == 0) return false;
        Recalculate(changes);
        return History.ReplaceElements(changes, Tr.T("Formular zurücksetzen"));
    }

    /// <summary>Whether the field is listed, itself or via a parent field („a“ matches „a.b“).</summary>
    private static bool Listed(HashSet<string> listed, string name)
    {
        if (listed.Contains(name)) return true;
        for (var dot = name.IndexOf('.'); dot > 0; dot = name.IndexOf('.', dot + 1))
            if (listed.Contains(name[..dot])) return true;
        return false;
    }

    /// <summary>Full name of a field dictionary (/T of the parents joined with full stops, at most 32 levels).</summary>
    private static string FullName(PdfDictionary field)
    {
        var parts = new List<string>();
        for (var (d, depth) = (field, 0); d is not null && depth < 32; d = d.GetDictionary(PdfName.Parent), depth++)
            if (d.GetString(PdfName.Get("T")) is { } t) parts.Insert(0, t.ToText());
        return string.Join(".", parts);
    }

    private bool _designHintShown;

    /// <summary>
    /// Once per document when designing the form: points out forms with XFA (after saving only the ordinary form applies) and digitally
    /// signed documents (every change invalidates the signature).
    /// </summary>
    public void HintFormDesign()
    {
        if (_designHintShown) return;
        _designHintShown = true;
        var hints = new List<string>();
        if (AcroForm?.ContainsKey(PdfName.Get("XFA")) == true)
            hints.Add(Tr.T("Dieses Formular enthält auch XFA-Daten (Adobe LiveCycle). Nach dem Bearbeiten gilt nur noch das gewöhnliche Formular — XFA wird beim Speichern entfernt."));
        if (IsSigned())
            hints.Add(Tr.T("Das Dokument ist digital unterschrieben — Änderungen am Formular machen die Unterschrift ungültig."));
        if (hints.Count > 0) Notify(string.Join(" ", hints));
    }

    /// <summary>Whether the file has a digital signature (a signature field with /ByteRange and /Contents), intact or not.</summary>
    private bool IsSigned() => PdfKnead.Core.Pdf.Signatures.DigitalSignatures.Find(Document).Count > 0;

    /// <summary>Moves the selected fields together by a distance in page space (one step).</summary>
    /// <param name="dx">Horizontal distance in points.</param>
    /// <param name="dy">Vertical distance in points.</param>
    /// <param name="description">Name of the undo step.</param>
    /// <param name="continueLast">Merge with the last step of the same kind (arrow keys).</param>
    /// <returns>false if nothing changed.</returns>
    public bool MoveSelectedFields(double dx, double dy, string description, bool continueLast = false)
    {
        if (!CanEditElements || FieldSelection is not { Count: > 0 } selection) return false;
        // Labels only: they stay level with their check box and can only be moved in reading direction
        if (selection.All(r => IsFieldLabel(r.Element)))
        {
            var (right, _) = Directions(selection[^1].Element.Rotation);
            var along = dx * right.X + dy * right.Y;
            (dx, dy) = (along * right.X, along * right.Y);
            if (Math.Abs(dx) + Math.Abs(dy) < 1e-9) return false;   // across the reading direction: no empty undo step
        }
        var changes = new List<(int, PageElement, PageElement)>();
        foreach (var selected in selection)
        {
            var r = selected.Element.Rect;
            changes.Add((selected.Page, selected.Element, ElementAppearance.Moved(selected.Element, new PdfRectangle(r.Left + dx, r.Bottom + dy, r.Right + dx, r.Top + dy))));
        }
        if (selection.Count == 1 && !LabelsOf(selection[0]).Any())
            return ReplaceElement(selection[0], changes[0].Item3, description, continueLast);
        return ApplyFieldChanges(changes, description);
    }

    /// <summary>Resizes a field (handles); its labels follow, in one step.</summary>
    /// <param name="field">The field.</param>
    /// <param name="rect">New rectangle in page space.</param>
    /// <returns>false if nothing changed.</returns>
    public bool ResizeField(ElementRef field, PdfRectangle rect)
    {
        if (!CanEditElements) return false;
        return ApplyFieldChanges([(field.Page, field.Element, Resized(field.Element, rect))], Tr.T("Feldgröße ändern"));
    }
}
