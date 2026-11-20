using System.Runtime.CompilerServices;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>Kind of a form field (12.7.4).</summary>
public enum FormFieldKind
{
    /// <summary>Text field (/FT /Tx).</summary>
    Text,
    /// <summary>Check box (/FT /Btn).</summary>
    CheckBox,
    /// <summary>Radio button (/FT /Btn with the Radio flag).</summary>
    Radio,
    /// <summary>Push button (/FT /Btn with the Pushbutton flag).</summary>
    PushButton,
    /// <summary>Combo box (/FT /Ch with the Combo flag).</summary>
    ComboBox,
    /// <summary>List box (/FT /Ch).</summary>
    ListBox,
    /// <summary>Signature field (/FT /Sig).</summary>
    Signature,
}

/// <summary>
/// Form field behind a widget of another program: kind, flags and value, also inherited from parent fields (12.7.4.1).
/// <see cref="Field"/> is the terminal field (it carries /V); for a merged field and widget it is the widget itself.
/// </summary>
public sealed class FormField
{
    private static readonly PdfName Parent = PdfName.Get("Parent"), FT = PdfName.Get("FT"), Ff = PdfName.Get("Ff"), V = PdfName.Get("V"),
        T = PdfName.Get("T"), MaxLenName = PdfName.Get("MaxLen"), Opt = PdfName.Get("Opt"), DA = PdfName.Get("DA"), Q = PdfName.Get("Q"),
        AP = PdfName.Get("AP"), AS = PdfName.Get("AS"), AA = PdfName.Get("AA"), Kids = PdfName.Get("Kids");

    private static readonly ConditionalWeakTable<PdfDictionary, FormField?> Cache = new();

    private FormField(PdfDictionary widget, PdfDictionary field, FormFieldKind kind, long flags)
    {
        Widget = widget;
        Field = field;
        Kind = kind;
        Flags = flags;
    }

    /// <summary>Gets the field of a widget (cached per widget).</summary>
    /// <param name="widget">The annotation dictionary.</param>
    /// <returns>The field; <see langword="null"/> if there is none (another annotation, broken).</returns>
    public static FormField? Of(PdfDictionary widget) => Cache.GetValue(widget, Read);

    /// <summary>Gets the field of an annotation element of another program.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The field; <see langword="null"/> if the element is not a widget or has no field.</returns>
    public static FormField? Of(ForeignAnnotation element) => element.Kind == "Widget" ? Of(element.Original) : null;

    private static FormField? Read(PdfDictionary widget)
    {
        try
        {
            var kind = Inherited(widget, FT) is PdfName type ? type.Value : null;
            if (kind is null) return null;
            var flags = Inherited(widget, Ff) switch { PdfInteger i => i.Value, PdfReal r => (long)r.Value, _ => 0 };
            // Terminal field: the widget if it has a name itself (merged) or no parent field; otherwise the parent field
            var field = widget.Get(T) is PdfString || widget.Get(Parent) is not PdfDictionary parent ? widget : parent;
            var fieldKind = kind switch
            {
                "Tx" => FormFieldKind.Text,
                "Btn" when (flags & (1 << 16)) != 0 => FormFieldKind.PushButton,
                "Btn" when (flags & (1 << 15)) != 0 => FormFieldKind.Radio,
                "Btn" => FormFieldKind.CheckBox,
                "Ch" when (flags & (1 << 17)) != 0 => FormFieldKind.ComboBox,
                "Ch" => FormFieldKind.ListBox,
                "Sig" => FormFieldKind.Signature,
                _ => (FormFieldKind?)null,
            };
            return fieldKind is { } k ? new FormField(widget, field, k, flags) : null;
        }
        catch (PdfException)
        {
            return null;
        }
    }

    /// <summary>The widget annotation.</summary>
    public PdfDictionary Widget { get; }

    /// <summary>Terminal field (carries /V); for a merged field and widget the widget itself.</summary>
    public PdfDictionary Field { get; }

    /// <summary>Kind of field.</summary>
    public FormFieldKind Kind { get; }

    /// <summary>/Ff (field flags, Tables 226, 228, 230, 232), inherited if necessary.</summary>
    public long Flags { get; }

    /// <summary>The field is read-only (/Ff bit 1).</summary>
    public bool ReadOnly => (Flags & 1) != 0;
    /// <summary>Multi-line text field (/Ff bit 13).</summary>
    public bool Multiline => Kind == FormFieldKind.Text && (Flags & (1 << 12)) != 0;
    /// <summary>Password text field (/Ff bit 14).</summary>
    public bool Password => Kind == FormFieldKind.Text && (Flags & (1 << 13)) != 0;
    /// <summary>Radio group whose selected button cannot be deselected (/Ff bit 15).</summary>
    public bool NoToggleToOff => (Flags & (1 << 14)) != 0;
    /// <summary>Combo box that accepts custom text (/Ff bit 19).</summary>
    public bool Editable => Kind == FormFieldKind.ComboBox && (Flags & (1 << 18)) != 0;

    /// <summary>Maximum length (/MaxLen, clamped to 100 000); 0 = none.</summary>
    public int MaxLength => Inherited(Widget, MaxLenName) switch { PdfInteger i => (int)Math.Clamp(i.Value, 0, 100_000), _ => 0 };

    /// <summary>Comb field: one character per cell (/MaxLen cells of equal width; single-line only, not for passwords).</summary>
    public bool Comb => Kind == FormFieldKind.Text && (Flags & (1 << 24)) != 0 && MaxLength > 0 && !Multiline && !Password;

    /// <summary>Whether the field can be filled in (not read-only, not a push button, not a signature field — that has its own tool).</summary>
    public bool Fillable => !ReadOnly && Kind is not (FormFieldKind.PushButton or FormFieldKind.Signature);

    /// <summary>Full name (names of the parents joined with points, 12.7.4.2).</summary>
    public string Name
    {
        get
        {
            var parts = new List<string>();
            var current = Field;
            for (var depth = 0; depth < 32 && current is not null; depth++)
            {
                if (current.Get(T) is PdfString t) parts.Add(t.ToText());
                current = current.Get(Parent) as PdfDictionary;
            }
            parts.Reverse();
            return string.Join('.', parts);
        }
    }

    /// <summary>Stored value: text, selected option (export value) or state of the field (/V); "" without one.</summary>
    public string StoredValue => Inherited(Widget, V) switch
    {
        PdfString s => s.ToText(),
        PdfName n => n.Value,
        // List box with multiple selection: all selected entries, one per line
        PdfArray { Count: > 0 } list => string.Join('\n', Enumerable.Range(0, Math.Min(list.Count, 10_000)).Select(i => (list.Get(i) as PdfString)?.ToText()).OfType<string>()),
        _ => "",
    };

    /// <summary>List box: several entries can be selected (/Ff bit 22).</summary>
    public bool MultiSelect => Kind == FormFieldKind.ListBox && (Flags & (1 << 21)) != 0;

    /// <summary>State of this widget (/AS) for check boxes and radio buttons; "Off" without one.</summary>
    public string State => Widget.Get(AS) is PdfName n ? n.Value : "Off";

    /// <summary>
    /// On state of this widget: the name in /AP /N other than "Off" (check boxes and radio buttons); without appearance states the state it is
    /// in (/AS) or, for a check box, the value of its field (/V) when either is not "Off" — the name the appearance states would carry
    /// (12.7.5.2.3; docs/FEHLERGEDAECHTNIS.md, No. 889); null otherwise.
    /// </summary>
    public string? OnState
    {
        get
        {
            if (Widget.GetDictionary(AP)?.Get(PdfName.N) is not PdfDictionary states)
                return State != "Off" ? State : Kind == FormFieldKind.CheckBox && Inherited(Widget, V) is PdfName { Value: not "Off" } v ? v.Value : null;
            foreach (var (key, _) in states.Entries)
                if (key.Value != "Off") return key.Value;
            return null;
        }
    }

    /// <summary>Options of a choice field (/Opt): export value and text shown; at most 10 000.</summary>
    public IReadOnlyList<(string Export, string Display)> Options
    {
        get
        {
            var list = new List<(string, string)>();
            if (Inherited(Widget, Opt) is not PdfArray options) return list;
            for (var i = 0; i < Math.Min(options.Count, 10_000); i++)
                switch (options.Get(i))
                {
                    case PdfString s:
                        list.Add((s.ToText(), s.ToText()));
                        break;
                    case PdfArray { Count: >= 2 } pair when pair.Get(0) is PdfString export && pair.Get(1) is PdfString display:
                        list.Add((export.ToText(), display.ToText()));
                        break;
                }
            return list;
        }
    }

    /// <summary>Gets the text shown for a value.</summary>
    /// <param name="value">The stored value.</param>
    /// <returns>For choice fields the display text of the matching option, otherwise the value itself.</returns>
    public string DisplayText(string value)
    {
        if (Kind is FormFieldKind.ComboBox or FormFieldKind.ListBox)
            foreach (var (export, display) in Options)
                if (export == value) return display;
        return value;
    }

    /// <summary>Gets the default appearance (/DA, inherited or from the form).</summary>
    /// <param name="acroForm">The document's /AcroForm dictionary; may be <see langword="null"/>.</param>
    /// <returns>The /DA string; empty if there is none.</returns>
    public string DefaultAppearance(PdfDictionary? acroForm) =>
        (Inherited(Widget, DA) as PdfString ?? acroForm?.GetString(DA))?.ToText() ?? "";

    /// <summary>Gets the alignment (/Q, inherited or from the form).</summary>
    /// <param name="acroForm">The document's /AcroForm dictionary; may be <see langword="null"/>.</param>
    /// <returns>0 left, 1 centred, 2 right.</returns>
    public int Quadding(PdfDictionary? acroForm) => (int)(Inherited(Widget, Q)?.AsNumber() ?? acroForm?.GetNumber(Q) ?? 0);

    /// <summary>
    /// Whether scripts are attached to the field (keystroke, format, validate, calculate). PDF Knead never executes them, so
    /// this is reported to the user (calculated fields then do not recalculate).
    /// </summary>
    public bool HasScripts => Field.GetDictionary(AA) is { Count: > 0 } || Widget.GetDictionary(AA) is { Count: > 0 };

    /// <summary>Enumerates all widgets of the terminal field (/Kids; for a merged field the widget itself).</summary>
    /// <returns>The widgets; at most 10 000, and kids that carry their own /T (fields, not widgets) are skipped.</returns>
    public IEnumerable<PdfDictionary> Widgets()
    {
        if (ReferenceEquals(Field, Widget) || Field.Get(Kids) is not PdfArray kids)
        {
            yield return Widget;
            yield break;
        }
        for (var i = 0; i < Math.Min(kids.Count, 10_000); i++)
            if (kids.Get(i) is PdfDictionary kid && kid.Get(T) is not PdfString) yield return kid;
    }

    /// <summary>Field property, also inherited from parent fields; depth limited against cycles.</summary>
    internal static PdfObject? Inherited(PdfDictionary field, PdfName key)
    {
        var current = field;
        for (var depth = 0; depth < 32 && current is not null; depth++)
        {
            if (current.Get(key) is not PdfNull and var value) return value;
            current = current.Get(Parent) as PdfDictionary;
        }
        return null;
    }
}
