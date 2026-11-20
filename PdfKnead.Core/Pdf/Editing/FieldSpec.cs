using System.Globalization;
using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>Visibility of a field (/F of the widget, Table 167).</summary>
public enum FieldVisibility
{
    /// <summary>Visible and printed (Print).</summary>
    Visible,

    /// <summary>Hidden (Hidden).</summary>
    Hidden,

    /// <summary>Visible but not printed.</summary>
    NoPrint,

    /// <summary>Hidden on screen but printed (NoView + Print).</summary>
    PrintOnly,
}

/// <summary>Border style (/BS /S, Table 168).</summary>
public enum FieldBorderStyle
{
    /// <summary>Solid line (/S).</summary>
    Solid,
    /// <summary>Dashed line (/D).</summary>
    Dashed,
    /// <summary>Bevelled: light at top left, dark at bottom right (/B).</summary>
    Beveled,
    /// <summary>Inset: dark at top left, light at bottom right (/I).</summary>
    Inset,
    /// <summary>Underline only (/U).</summary>
    Underline,
}

/// <summary>Symbol in check boxes and radio buttons (/MK /CA, ZapfDingbats as in common PDF editors).</summary>
public enum CheckStyle
{
    /// <summary>Tick.</summary>
    Check,
    /// <summary>Filled circle.</summary>
    Circle,
    /// <summary>Cross.</summary>
    Cross,
    /// <summary>Filled diamond.</summary>
    Diamond,
    /// <summary>Filled square.</summary>
    Square,
    /// <summary>Filled star.</summary>
    Star,
}

/// <summary>Simple calculations of the standard form scripts (<c>AFSimple_Calculate</c>) that PDF Knead computes itself.</summary>
public enum CalcOperation
{
    /// <summary>Sum of the fields.</summary>
    Sum,
    /// <summary>Product of the fields.</summary>
    Product,
    /// <summary>Average of the fields.</summary>
    Average,
    /// <summary>Smallest value of the fields.</summary>
    Minimum,
    /// <summary>Largest value of the fields.</summary>
    Maximum,
    /// <summary>Formula in the simplified field notation of form scripts (<see cref="FieldFormula"/>).</summary>
    Formula,
}

/// <summary>Entry of a choice field.</summary>
/// <param name="Export">Export value (stored).</param>
/// <param name="Display">Text shown.</param>
public sealed record FieldOption(string Export, string Display);

/// <summary>
/// Calculation over other fields (full names). With <see cref="CalcOperation.Formula"/> the formula is in
/// <paramref name="Formula"/> (null while it is still empty), and <paramref name="Fields"/> are the fields it references.
/// </summary>
/// <param name="Operation">Kind of calculation.</param>
/// <param name="Fields">Full names of the fields used.</param>
/// <param name="Formula">The formula for <see cref="CalcOperation.Formula"/>; otherwise <see langword="null"/>.</param>
public sealed record FieldCalculation(CalcOperation Operation, IReadOnlyList<string> Fields, FieldFormula? Formula = null)
{
    /// <summary>Whether the calculation is computed and written (a formula exists, or at least one field is given).</summary>
    public bool IsActive => Operation == CalcOperation.Formula ? Formula is not null : Fields.Count > 0;

    /// <summary>Creates a calculation from a formula; the fields are those it references.</summary>
    /// <param name="formula">The formula.</param>
    /// <returns>The calculation.</returns>
    public static FieldCalculation Of(FieldFormula formula) => new(CalcOperation.Formula, formula.Fields, formula);

    /// <summary>Compares operation, fields (in order) and formula.</summary>
    /// <param name="other">The calculation to compare with.</param>
    /// <returns><see langword="true"/> if both describe the same calculation.</returns>
    public bool Equals(FieldCalculation? other) =>
        other is not null && Operation == other.Operation && Fields.SequenceEqual(other.Fields) && Equals(Formula, other.Formula);

    /// <summary>Hash code consistent with <see cref="Equals(FieldCalculation?)"/>.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() => HashCode.Combine(Operation, Fields.Count, Formula);

    /// <summary>
    /// Orders calculated fields so that each comes after the calculated fields it reads (a sum of sums after the sums), as PDF-Knead writes
    /// them into /CO and calculates them (ISO 32000-2, 12.7.3, Table 224; docs/FEHLERGEDAECHTNIS.md, No. 578). Stable: otherwise the given
    /// order; in a cycle the first remaining one goes next. Above 2,000 fields the given order (the sorting is quadratic).
    /// </summary>
    /// <typeparam name="T">The kind of entry.</typeparam>
    /// <param name="items">The calculated fields in their given order.</param>
    /// <param name="name">The full name of an entry.</param>
    /// <param name="calculation">The calculation of an entry.</param>
    /// <returns>The entries in calculation order.</returns>
    public static List<T> DependencyOrder<T>(IReadOnlyList<T> items, Func<T, string> name, Func<T, FieldCalculation> calculation)
    {
        if (items.Count < 2 || items.Count > 2_000) return [.. items];
        var names = new HashSet<string>(items.Select(name), StringComparer.Ordinal);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var remaining = new List<T>(items);
        var result = new List<T>(items.Count);
        while (remaining.Count > 0)
        {
            var next = remaining.FindIndex(item =>
                calculation(item).Fields.All(f => f == name(item) || !names.Contains(f) || placed.Contains(f)));
            if (next < 0) next = 0;   // cycle
            result.Add(remaining[next]);
            placed.Add(name(remaining[next]));
            remaining.RemoveAt(next);
        }
        return result;
    }
}

/// <summary>
/// All properties of a form field and its widget that PDF Knead edits: read from the standard entries (12.7, 12.5.6.19) — also
/// for fields of other programs — and written back there. Whatever PDF Knead does not know (custom scripts, further flags)
/// stays as it was when writing back.
/// </summary>
public sealed partial record FieldSpec
{
    /// <summary>Kind of field.</summary>
    public FormFieldKind Kind { get; init; } = FormFieldKind.Text;

    /// <summary>Name of the field (/T, its own part without the names of its parents); empty: assigned on saving.</summary>
    public string Name { get; init; } = "";

    /// <summary>Tooltip (/TU), also used by screen readers.</summary>
    public string Tooltip { get; init; } = "";

    /// <summary>The field cannot be changed (/Ff bit 1).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>The field must be filled in (/Ff bit 2).</summary>
    public bool Required { get; init; }

    /// <summary>Visibility on screen and in print.</summary>
    public FieldVisibility Visibility { get; init; } = FieldVisibility.Visible;

    /// <summary>Border colour 0xRRGGBB (/MK /BC); null = no border.</summary>
    public int? BorderColor { get; init; }

    /// <summary>Fill colour 0xRRGGBB (/MK /BG); null = transparent.</summary>
    public int? FillColor { get; init; }

    /// <summary>Border width in points (/BS /W).</summary>
    public double BorderWidth { get; init; } = 1;

    /// <summary>Border style (/BS /S).</summary>
    public FieldBorderStyle BorderStyle { get; init; } = FieldBorderStyle.Solid;

    /// <summary>Font of the field text.</summary>
    public ElementFont Font { get; init; } = ElementFont.Sans;

    /// <summary>Font size in points; 0 = automatic (fitted).</summary>
    public double FontSize { get; init; }

    /// <summary>
    /// Shrink the font automatically: <see cref="FontSize"/> is the starting size, which becomes smaller if the text does not fit
    /// (single-line or multi-line). PDF only knows fixed or automatic sizes, so this is saved as automatic (0 Tf) with the
    /// appearance PDF Knead has laid out, and the starting size in <see cref="StartSizeKey"/> on the widget.
    /// </summary>
    public bool ShrinkToFit { get; init; }

    /// <summary>Starting size of a field with <see cref="ShrinkToFit"/> (a private widget entry that other programs ignore).</summary>
    public static readonly PdfName StartSizeKey = PdfName.Get("PDFK_Startgroesse");

    /// <summary>The name of <see cref="StartSizeKey"/> in files of earlier versions (ISO 32000-2, Annex E.2; No. 599); still read, never written.</summary>
    public static readonly PdfName LegacyStartSizeKey = PdfName.Get("PDFKneadStartgroesse");

    /// <summary>The starting size of a widget (under either name); null without one.</summary>
    public static double? StartSize(PdfDictionary widget) => widget.GetNumber(StartSizeKey) ?? widget.GetNumber(LegacyStartSizeKey);

    /// <summary>Text colour (also of the symbol in check boxes) 0xRRGGBB.</summary>
    public int TextColor { get; init; }

    /// <summary>Horizontal alignment of the text (/Q).</summary>
    public ElementAlignment Alignment { get; init; } = ElementAlignment.Left;

    /// <summary>Default value (/DV): text, export value of the option, or on state ("checked by default").</summary>
    public string DefaultValue { get; init; } = "";

    // ---- Text field

    /// <summary>The text may span several lines.</summary>
    public bool Multiline { get; init; }

    /// <summary>No scrolling: no more text than fits.</summary>
    public bool DoNotScroll { get; init; }

    /// <summary>Password field: the text is shown as asterisks.</summary>
    public bool Password { get; init; }

    /// <summary>Maximum length (/MaxLen); 0 = none.</summary>
    public int MaxLength { get; init; }

    /// <summary>Comb field: one character per cell (requires <see cref="MaxLength"/>).</summary>
    public bool Comb { get; init; }

    /// <summary>Do not check spelling.</summary>
    public bool DoNotSpellCheck { get; init; }

    // ---- Check box and radio button

    /// <summary>Symbol shown when checked.</summary>
    public CheckStyle CheckStyle { get; init; } = CheckStyle.Check;

    /// <summary>Export value (name of the on state); never "Off".</summary>
    public string ExportValue { get; init; } = "Ja";

    /// <summary>Radio group: a selected button cannot be deselected again.</summary>
    public bool NoToggleToOff { get; init; }

    /// <summary>Radio buttons with the same export value are switched on and off together.</summary>
    public bool RadiosInUnison { get; init; }

    // ---- Combo box and list box

    /// <summary>Entries of the choice field.</summary>
    public IReadOnlyList<FieldOption> Options { get; init; } = [];

    /// <summary>Entries are sorted.</summary>
    public bool Sort { get; init; }

    /// <summary>Combo box: custom text is allowed.</summary>
    public bool Editable { get; init; }

    /// <summary>List box: several entries can be selected.</summary>
    public bool MultiSelect { get; init; }

    /// <summary>Commit the selection immediately.</summary>
    public bool CommitOnSelect { get; init; }

    // ---- Format and calculation (text field)

    /// <summary>Format recognised from the standard format script.</summary>
    public FieldFormat Format { get; init; } = FieldFormat.None;

    /// <summary>The field has a custom, unknown format script, which stays unchanged (read-only).</summary>
    public string? CustomFormatScript { get; init; }

    /// <summary>Calculation recognised from the standard calculation script; <see langword="null"/> for none.</summary>
    public FieldCalculation? Calculation { get; init; }

    /// <summary>Custom, unknown calculation script, which stays unchanged (read-only).</summary>
    public string? CustomCalculationScript { get; init; }

    // ---- Push button

    /// <summary>Caption of a push button (/MK /CA).</summary>
    public string Caption { get; init; } = "";

    /// <summary>The push button resets the form (/A /S /ResetForm, without a script).</summary>
    public bool ResetForm { get; init; }

    /// <summary>Flags (/Ff) that PDF Knead does not edit (file select, rich text, no export …); they stay as they were.</summary>
    public long OtherFlags { get; init; }

    // ---- Code field (KONZEPT.md, section 5.19, step 4)

    /// <summary>
    /// Code field („QR-Feld“, „Barcode-Feld“): a text field whose value is drawn as this code. In the file it is read-only, so other
    /// readers show the code saved last instead of letting the value be typed over as plain text; PDF Knead fills it in unless
    /// <see cref="ReadOnly"/> is set (stored with the look). Null for every other field.
    /// </summary>
    public CodeLook? Code { get; init; }

    /// <summary>Entry of a code field with its look and the read-only setting of PDF Knead (in the terminal field).</summary>
    public static readonly PdfName CodeKey = PdfName.Get("PDFK_Code");

    /// <summary>The name of <see cref="CodeKey"/> in files of earlier versions (ISO 32000-2, Annex E.2; No. 599); still read, never written.</summary>
    public static readonly PdfName LegacyCodeKey = PdfName.Get("PDFKneadCode");

    /// <summary>The code data of a field or widget (under either name).</summary>
    public static PdfDictionary? CodeData(PdfDictionary dict) => dict.GetDictionary(CodeKey) ?? dict.GetDictionary(LegacyCodeKey);

    private static readonly PdfName Gesperrt = PdfName.Get("Gesperrt");

    /// <summary>Whether a field of the file is a code field (a text field with a readable code entry).</summary>
    /// <param name="field">The form field.</param>
    /// <returns><see langword="true"/> for a code field.</returns>
    public static bool IsCodeField(FormField field) =>
        field.Kind == FormFieldKind.Text && (CodeData(field.Field) ?? CodeData(field.Widget)) is { } data && CodeLook.Read(data) is not null;

    /// <summary>Compares all properties, including the options in order.</summary>
    /// <param name="other">The properties to compare with.</param>
    /// <returns><see langword="true"/> if all properties are equal.</returns>
    public bool Equals(FieldSpec? other) =>
        other is not null && EqualityContract == other.EqualityContract && Kind == other.Kind && Name == other.Name && Tooltip == other.Tooltip &&
        ReadOnly == other.ReadOnly && Required == other.Required && Visibility == other.Visibility && BorderColor == other.BorderColor &&
        FillColor == other.FillColor && BorderWidth.Equals(other.BorderWidth) && BorderStyle == other.BorderStyle && Font == other.Font &&
        FontSize.Equals(other.FontSize) && ShrinkToFit == other.ShrinkToFit && TextColor == other.TextColor && Alignment == other.Alignment && DefaultValue == other.DefaultValue &&
        Multiline == other.Multiline && DoNotScroll == other.DoNotScroll && Password == other.Password && MaxLength == other.MaxLength &&
        Comb == other.Comb && DoNotSpellCheck == other.DoNotSpellCheck && CheckStyle == other.CheckStyle && ExportValue == other.ExportValue &&
        NoToggleToOff == other.NoToggleToOff && RadiosInUnison == other.RadiosInUnison && Options.SequenceEqual(other.Options) &&
        Sort == other.Sort && Editable == other.Editable && MultiSelect == other.MultiSelect && CommitOnSelect == other.CommitOnSelect &&
        Format == other.Format && CustomFormatScript == other.CustomFormatScript && Equals(Calculation, other.Calculation) &&
        CustomCalculationScript == other.CustomCalculationScript && Caption == other.Caption && ResetForm == other.ResetForm &&
        OtherFlags == other.OtherFlags && Equals(Code, other.Code);

    /// <summary>Hash code consistent with <see cref="Equals(FieldSpec?)"/>.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() => HashCode.Combine(Kind, Name, Tooltip, Options.Count, Format);

    /// <summary>
    /// Determines whether the field would look the same. If only name, tooltip, flags without effect on the appearance,
    /// default value, calculation or visibility have changed, the other program's appearance stays (redrawing it would lose its
    /// design).
    /// </summary>
    /// <param name="other">The properties to compare with.</param>
    /// <returns><see langword="true"/> if all properties that affect the appearance are equal.</returns>
    public bool SameLook(FieldSpec other) =>
        Kind == other.Kind && BorderColor == other.BorderColor && FillColor == other.FillColor && BorderWidth.Equals(other.BorderWidth) &&
        BorderStyle == other.BorderStyle && Font == other.Font && FontSize.Equals(other.FontSize) && ShrinkToFit == other.ShrinkToFit && TextColor == other.TextColor &&
        Alignment == other.Alignment && Multiline == other.Multiline && Password == other.Password && Comb == other.Comb &&
        MaxLength == other.MaxLength && CheckStyle == other.CheckStyle && ExportValue == other.ExportValue &&
        Options.SequenceEqual(other.Options) && MultiSelect == other.MultiSelect && Format == other.Format && Caption == other.Caption &&
        Equals(Code, other.Code);

    /// <summary>Writes only the visibility (/F) into the widget and nothing else (appearance and look stay).</summary>
    /// <param name="widget">The widget annotation dictionary; other /F bits stay.</param>
    public void WriteVisibility(PdfDictionary widget)
    {
        var f = widget.GetInteger(F) ?? 0;
        f &= ~(2L | 4 | 32);
        f |= Visibility switch
        {
            FieldVisibility.Hidden => 2L,
            FieldVisibility.NoPrint => 0L,
            FieldVisibility.PrintOnly => 32L | 4,
            _ => 4L,
        };
        widget.Set(F, PdfInteger.Of(f));
    }

    /// <summary>Whether the field shows text (font, size and alignment apply).</summary>
    public bool HasText => Kind is FormFieldKind.Text or FormFieldKind.ComboBox or FormFieldKind.ListBox or FormFieldKind.PushButton;

    // ------------------------------------------------------------ Flags (/Ff, Tables 226, 228, 230, 232)

    private const long FlagReadOnly = 1, FlagRequired = 1 << 1;
    private const long FlagMultiline = 1 << 12, FlagPassword = 1 << 13, FlagDoNotSpellCheck = 1 << 22, FlagDoNotScroll = 1 << 23, FlagComb = 1 << 24;
    private const long FlagNoToggleToOff = 1 << 14, FlagRadio = 1 << 15, FlagPushButton = 1 << 16, FlagRadiosInUnison = 1 << 25;
    private const long FlagCombo = 1 << 17, FlagEdit = 1 << 18, FlagSort = 1 << 19, FlagMultiSelect = 1 << 21, FlagCommitOnSelect = 1 << 26;

    /// <summary>Flags that PDF Knead edits per kind of field (the rest stays in <see cref="OtherFlags"/>).</summary>
    private static long Managed(FormFieldKind kind) => FlagReadOnly | FlagRequired | kind switch
    {
        FormFieldKind.Text => FlagMultiline | FlagPassword | FlagDoNotSpellCheck | FlagDoNotScroll | FlagComb,
        FormFieldKind.CheckBox or FormFieldKind.Radio or FormFieldKind.PushButton => FlagNoToggleToOff | FlagRadio | FlagPushButton | FlagRadiosInUnison,
        FormFieldKind.ComboBox or FormFieldKind.ListBox => FlagCombo | FlagEdit | FlagSort | FlagMultiSelect | FlagCommitOnSelect | FlagDoNotSpellCheck,
        _ => 0,
    };

    /// <summary>/Ff value for these properties, including <see cref="OtherFlags"/>.</summary>
    public long Flags
    {
        get
        {
            var f = OtherFlags & ~Managed(Kind);
            // a code field is read-only for other readers whatever PDF Knead's own setting; its content may span lines (GiroCode)
            if (ReadOnly || Code is not null) f |= FlagReadOnly;
            if (Code is not null && Kind == FormFieldKind.Text) f |= FlagMultiline;
            if (Required) f |= FlagRequired;
            switch (Kind)
            {
                case FormFieldKind.Text:
                    if (Multiline) f |= FlagMultiline;
                    if (Password) f |= FlagPassword;
                    if (DoNotSpellCheck) f |= FlagDoNotSpellCheck;
                    if (DoNotScroll) f |= FlagDoNotScroll;
                    if (Comb && MaxLength > 0 && !Multiline && !Password) f |= FlagComb;
                    break;
                case FormFieldKind.Radio:
                    f |= FlagRadio;
                    if (NoToggleToOff) f |= FlagNoToggleToOff;
                    if (RadiosInUnison) f |= FlagRadiosInUnison;
                    break;
                case FormFieldKind.PushButton:
                    f |= FlagPushButton;
                    break;
                case FormFieldKind.ComboBox:
                    f |= FlagCombo;
                    if (Editable) f |= FlagEdit;
                    if (Sort) f |= FlagSort;
                    if (CommitOnSelect) f |= FlagCommitOnSelect;
                    if (DoNotSpellCheck && Editable) f |= FlagDoNotSpellCheck;
                    break;
                case FormFieldKind.ListBox:
                    if (Sort) f |= FlagSort;
                    if (MultiSelect) f |= FlagMultiSelect;
                    if (CommitOnSelect) f |= FlagCommitOnSelect;
                    break;
            }
            return f;
        }
    }

    // ------------------------------------------------------------ Reading

    private static readonly PdfName T = PdfName.Get("T"), TU = PdfName.Get("TU"), DV = PdfName.Get("DV"), MK = PdfName.Get("MK"),
        BC = PdfName.Get("BC"), BG = PdfName.Get("BG"), CA = PdfName.Get("CA"), BS = PdfName.Get("BS"), W = PdfName.Get("W"), S = PdfName.Get("S"),
        D = PdfName.Get("D"), Border = PdfName.Get("Border"), A = PdfName.Get("A"), AA = PdfName.Get("AA"), C = PdfName.Get("C"),
        K = PdfName.Get("K"), F = PdfName.Get("F"), JS = PdfName.Get("JS"), FT = PdfName.Get("FT"), Ff = PdfName.Get("Ff"),
        MaxLen = PdfName.Get("MaxLen"), Opt = PdfName.Get("Opt"), Q = PdfName.Get("Q"), DA = PdfName.Get("DA"), R = PdfName.Get("R");

    /// <summary>Reads the properties of an existing field (widget and field, including inherited entries).</summary>
    /// <param name="field">The form field.</param>
    /// <param name="acroForm">The document's /AcroForm dictionary (for /DA and /Q defaults); may be <see langword="null"/>.</param>
    /// <returns>The properties.</returns>
    public static FieldSpec Of(FormField field, PdfDictionary? acroForm)
    {
        var widget = field.Widget;
        var (font, size, color) = Writing.FieldAppearance.ParseDefaultAppearance(field.DefaultAppearance(acroForm));
        // Automatic size with a private starting size: "shrink font automatically" (saved by PDF Knead)
        var shrink = size <= 0 && StartSize(widget) is { } start && double.IsFinite(start) && start >= 1;
        if (shrink) size = Math.Clamp(StartSize(widget)!.Value, 1, 500);
        var mk = widget.GetDictionary(MK);
        var bs = widget.GetDictionary(BS);
        var width = bs?.GetNumber(W) ?? (widget.GetArray(Border) is { Count: >= 3 } border ? border.GetNumber(2) : null) ?? 1;
        var style = bs?.GetName(S)?.Value switch
        {
            "D" => FieldBorderStyle.Dashed,
            "B" => FieldBorderStyle.Beveled,
            "I" => FieldBorderStyle.Inset,
            "U" => FieldBorderStyle.Underline,
            _ => FieldBorderStyle.Solid,
        };
        var annotationFlags = widget.GetInteger(F) ?? 0;
        var visibility = (annotationFlags & 2) != 0 ? FieldVisibility.Hidden
            : (annotationFlags & 32) != 0 ? FieldVisibility.PrintOnly
            : (annotationFlags & 4) == 0 ? FieldVisibility.NoPrint
            : FieldVisibility.Visible;
        var caption = mk?.GetString(CA)?.ToText() ?? "";
        var formatScript = Script(field.Widget, F) ?? Script(field.Field, F);
        var format = formatScript is null ? FieldFormat.None : FieldFormat.Parse(formatScript);
        var calcScript = Script(field.Field, C) ?? Script(field.Widget, C);
        var calculation = calcScript is null ? null : ParseCalculation(calcScript);
        var options = field.Options.Select(o => new FieldOption(o.Export, o.Display)).ToList();
        var flags = field.Flags;
        var codeData = field.Kind == FormFieldKind.Text ? CodeData(field.Field) ?? CodeData(widget) : null;   // see IsCodeField
        var code = codeData is null ? null : CodeLook.Read(codeData);
        return new FieldSpec
        {
            Kind = field.Kind,
            Name = field.Field.GetString(T)?.ToText() ?? "",
            Tooltip = (FormField.Inherited(widget, TU) as PdfString)?.ToText() ?? "",
            ReadOnly = code is not null ? codeData!.GetBoolean(Gesperrt) == true : (flags & FlagReadOnly) != 0,
            Required = (flags & FlagRequired) != 0,
            Visibility = visibility,
            BorderColor = Color(mk?.Get(BC)),
            FillColor = Color(mk?.Get(BG)),
            BorderWidth = double.IsFinite(width) ? Math.Clamp(width, 0, 12) : 1,
            BorderStyle = style,
            Font = font,
            FontSize = size,
            ShrinkToFit = shrink,
            TextColor = color,
            Alignment = field.Quadding(acroForm) switch { 1 => ElementAlignment.Center, 2 => ElementAlignment.Right, _ => ElementAlignment.Left },
            DefaultValue = FormField.Inherited(widget, DV) switch
            {
                PdfString s => s.ToText(),
                PdfName n => n.Value,
                PdfArray { Count: > 0 } list when list.Get(0) is PdfString first => first.ToText(),
                _ => "",
            },
            Multiline = code is null && (flags & FlagMultiline) != 0,
            DoNotScroll = (flags & FlagDoNotScroll) != 0,
            Password = (flags & FlagPassword) != 0,
            MaxLength = field.MaxLength,
            Comb = (flags & FlagComb) != 0,
            DoNotSpellCheck = (flags & FlagDoNotSpellCheck) != 0,
            CheckStyle = StyleOf(caption, field.Kind),
            ExportValue = field.OnState ?? "Ja",
            NoToggleToOff = (flags & FlagNoToggleToOff) != 0,
            RadiosInUnison = (flags & FlagRadiosInUnison) != 0,
            Options = options,
            Sort = (flags & FlagSort) != 0,
            Editable = (flags & FlagEdit) != 0,
            MultiSelect = (flags & FlagMultiSelect) != 0,
            CommitOnSelect = (flags & FlagCommitOnSelect) != 0,
            Format = format,
            CustomFormatScript = formatScript is not null && format.Kind == FieldFormatKind.None ? formatScript : null,
            Calculation = calculation,
            CustomCalculationScript = calcScript is not null && calculation is null ? calcScript : null,
            Caption = field.Kind == FormFieldKind.PushButton ? caption : "",
            ResetForm = widget.GetDictionary(A)?.GetName(S)?.Value == "ResetForm",
            OtherFlags = flags & ~Managed(field.Kind),
            Code = code,
        };
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PdfDictionary, FieldSpec> Read = new();

    /// <summary>Reads the properties of a field of another program as stored in the file (read once per widget).</summary>
    /// <param name="foreign">The widget.</param>
    /// <returns>The properties; <see langword="null"/> if the annotation is not a form field.</returns>
    public static FieldSpec? Of(ForeignAnnotation foreign) =>
        FormField.Of(foreign) is { } field
            ? Read.GetValue(foreign.Original, _ => Of(field, foreign.Document.Catalog?.GetDictionary(PdfName.Get("AcroForm"))))
            : null;

    /// <summary>Gets the properties of a simple text field element (<see cref="TextElement"/> with <c>IsField</c>).</summary>
    /// <param name="text">The text element.</param>
    /// <returns>The properties.</returns>
    public static FieldSpec Of(TextElement text) => new()
    {
        Kind = FormFieldKind.Text,
        Name = text.FieldName,
        Font = text.Font,
        FontSize = text.FontSize,
        TextColor = text.Color,
        Alignment = text.Alignment,
        Multiline = text.Multiline,
    };

    /// <summary>Text of a script from /AA (only strings; streams are rare and not read).</summary>
    private static string? Script(PdfDictionary dict, PdfName trigger) =>
        dict.GetDictionary(AA)?.GetDictionary(trigger)?.Get(JS) is PdfString s ? s.ToText() : null;

    private static CheckStyle StyleOf(string caption, FormFieldKind kind) => caption switch
    {
        "l" => CheckStyle.Circle,
        "8" => CheckStyle.Cross,
        "u" => CheckStyle.Diamond,
        "n" => CheckStyle.Square,
        "H" => CheckStyle.Star,
        "4" => CheckStyle.Check,
        _ => kind == FormFieldKind.Radio ? CheckStyle.Circle : CheckStyle.Check,
    };

    /// <summary>Gets the ZapfDingbats character for /MK /CA (other programs redraw the symbol with it).</summary>
    /// <param name="style">The symbol.</param>
    /// <returns>The character.</returns>
    public static string DingbatOf(CheckStyle style) => style switch
    {
        CheckStyle.Circle => "l",
        CheckStyle.Cross => "8",
        CheckStyle.Diamond => "u",
        CheckStyle.Square => "n",
        CheckStyle.Star => "H",
        _ => "4",
    };

    /// <summary>Colour from /MK (grey, RGB, CMYK) as 0xRRGGBB; null = none.</summary>
    private static int? Color(PdfObject? obj)
    {
        if (obj is not PdfArray { Count: 1 or 3 or 4 } a) return null;
        var v = new double[a.Count];
        for (var i = 0; i < a.Count; i++)
        {
            var n = a.Get(i)?.AsNumber() ?? 0;
            v[i] = double.IsFinite(n) ? Math.Clamp(n, 0, 1) : 0;
        }
        var (r, g, b) = a.Count switch
        {
            1 => (v[0], v[0], v[0]),
            3 => (v[0], v[1], v[2]),
            _ => ((1 - v[0]) * (1 - v[3]), (1 - v[1]) * (1 - v[3]), (1 - v[2]) * (1 - v[3])),
        };
        static int Byte(double x) => (int)Math.Round(x * 255);
        return (Byte(r) << 16) | (Byte(g) << 8) | Byte(b);
    }

    // Each name atomic (?>…): otherwise the regex distributes the spaces between two names over both \s* and, with a missing
    // parenthesis, tries every distribution — 10 names took 14 s, 12 names over a minute (ReDoS)
    [GeneratedRegex(@"^\s*AFSimple_Calculate\(\s*""(SUM|PRD|AVG|MIN|MAX)""\s*,\s*new\s+Array\s*\((?<names>(?>\s*""(?:[^""\\]|\\.)*""\s*,?)*)\)\s*\)\s*;?\s*$")]
    private static partial Regex CalculateCall();

    [GeneratedRegex(@"""((?:[^""\\]|\\.)*)""")]
    private static partial Regex Quoted();

    /// <summary>
    /// Recognises a calculation in a calculation script: a formula in simplified field notation, or exactly the one call common PDF editors
    /// write for "value is the sum … of the following fields"; everything else (including anything appended) counts as a
    /// custom script.
    /// </summary>
    /// <param name="script">The script text; scripts over 100 000 characters are ignored.</param>
    /// <returns>The calculation with at most 1 000 fields; <see langword="null"/> for a custom script.</returns>
    public static FieldCalculation? ParseCalculation(string script)
    {
        if (script.Length > 100_000) return null;
        // Formula in simplified field notation ("/** BVCALC … EVCALC **/…")
        if (FieldFormula.FromScript(script) is { } formula) return FieldCalculation.Of(formula);
        if (CalculateCall().Match(script) is not { Success: true } m) return null;
        var names = new List<string>();
        foreach (Match q in Quoted().Matches(m.Groups["names"].Value))
        {
            if (names.Count >= 1000) break;
            names.Add(FieldScripts.Unescape(q.Groups[1].Value));
        }
        var op = m.Groups[1].Value switch
        {
            "PRD" => CalcOperation.Product,
            "AVG" => CalcOperation.Average,
            "MIN" => CalcOperation.Minimum,
            "MAX" => CalcOperation.Maximum,
            _ => CalcOperation.Sum,
        };
        return new FieldCalculation(op, names);
    }

    // ------------------------------------------------------------ Writing

    /// <summary>
    /// Writes the field entries into <paramref name="field"/> (the terminal field; for a merged field also the widget): kind,
    /// name, flags, default value, maximum length, options, format and calculation scripts (standard calls, safely escaped).
    /// Other scripts and unknown entries stay.
    /// </summary>
    /// <param name="field">The terminal field.</param>
    /// <param name="before">The properties from the file (field of another program). Format, keystroke and calculation scripts
    /// are then only rewritten if format or calculation have changed, and are not deleted on renaming (otherwise a custom
    /// /AA /K, and /F, /K, /C on choice fields, would be lost). Null for new fields.</param>
    public void WriteField(PdfDictionary field, FieldSpec? before = null)
    {
        field.Set(FT, PdfName.Get(Kind switch
        {
            FormFieldKind.Text => "Tx",
            FormFieldKind.ComboBox or FormFieldKind.ListBox => "Ch",
            FormFieldKind.Signature => "Sig",
            _ => "Btn",
        }));
        if (Name.Length > 0) field.Set(T, Text(Name));
        SetOrRemove(field, TU, Tooltip.Length > 0 ? Text(Tooltip) : null);
        var flags = Flags;
        SetOrRemove(field, Ff, flags != 0 ? PdfInteger.Of(flags) : null);
        if (Code is not null && Kind == FormFieldKind.Text)
        {
            var data = new PdfDictionary();
            Code.Write(data);
            if (ReadOnly) data.Set(Gesperrt, PdfBoolean.Of(true));
            field.Set(CodeKey, data);
            field.Remove(LegacyCodeKey);
        }
        else
        {
            field.Remove(CodeKey);
            field.Remove(LegacyCodeKey);
        }
        SetOrRemove(field, MaxLen, Kind == FormFieldKind.Text && MaxLength > 0 ? PdfInteger.Of(Math.Clamp(MaxLength, 1, 100_000)) : null);
        SetOrRemove(field, DV, Kind switch
        {
            FormFieldKind.CheckBox or FormFieldKind.Radio => DefaultValue.Length > 0 && DefaultValue != "Off" ? PdfName.Get(DefaultValue) : null,
            FormFieldKind.Text or FormFieldKind.ComboBox or FormFieldKind.ListBox => DefaultValue.Length > 0 ? Text(DefaultValue) : null,
            _ => null,
        });
        if (Kind is FormFieldKind.ComboBox or FormFieldKind.ListBox)
        {
            var opt = new PdfArray();
            foreach (var o in Options.Take(10_000))
                opt.Add(o.Export == o.Display ? Text(o.Export) : new PdfArray([Text(o.Export), Text(o.Display)]));
            field.Set(Opt, opt);
        }
        // Check boxes and radio buttons: in files from common PDF editors /Opt holds the export values for the states "0", "1" … and stays
        // (otherwise even renaming made the value jump from "Ja" to "0")
        else if (Kind is not (FormFieldKind.CheckBox or FormFieldKind.Radio)) field.Remove(Opt);

        // Scripts: only the standard calls are written or removed; a custom script stays unchanged
        var aa = field.GetDictionary(AA) is { } existing ? Copy(existing) : new PdfDictionary();
        var kindChanged = before is null || before.Kind != Kind;
        if (CustomFormatScript is null && (kindChanged || before!.Format != Format))
        {
            var (format, keystroke) = Kind == FormFieldKind.Text ? FieldScripts.ForFormat(Format) : (null, null);
            SetOrRemove(aa, F, format is null ? null : FieldScripts.Action(format));
            SetOrRemove(aa, K, keystroke is null ? null : FieldScripts.Action(keystroke));
        }
        if (CustomCalculationScript is null && (kindChanged || !Equals(before!.Calculation, Calculation)))
            SetOrRemove(aa, C, Kind == FormFieldKind.Text && Calculation is { IsActive: true } calc ? FieldScripts.Action(FieldScripts.ForCalculation(calc)) : null);
        SetOrRemove(field, AA, aa.Count > 0 ? aa : null);
    }

    /// <summary>
    /// Writes the widget entries: visibility (/F, other bits stay), border and fill, symbol or caption and rotation (/MK, further
    /// entries stay), border style (/BS), default appearance (/DA) and alignment (/Q), and the push button's action.
    /// </summary>
    /// <param name="widget">The widget annotation dictionary.</param>
    /// <param name="rotation">Rotation for /MK /R in degrees (0, 90, 180 or 270); 0 removes the entry.</param>
    public void WriteWidget(PdfDictionary widget, int rotation)
    {
        WriteVisibility(widget);

        var mk = widget.GetDictionary(MK) is { } oldMk ? Copy(oldMk) : new PdfDictionary();
        SetOrRemove(mk, BC, BorderColor is { } bc ? ColorArray(bc) : null);
        SetOrRemove(mk, BG, FillColor is { } bg ? ColorArray(bg) : null);
        SetOrRemove(mk, R, rotation != 0 ? PdfInteger.Of(rotation) : null);
        SetOrRemove(mk, CA, Kind switch
        {
            FormFieldKind.CheckBox or FormFieldKind.Radio => Text(DingbatOf(CheckStyle)),
            FormFieldKind.PushButton => Caption.Length > 0 ? Text(Caption) : null,
            _ => mk.Get(CA),
        });
        SetOrRemove(widget, MK, mk.Count > 0 ? mk : null);

        var bs = new PdfDictionary();
        bs.Set(W, new PdfReal(Math.Round(Math.Clamp(BorderWidth, 0, 12), 3)));
        bs.Set(S, PdfName.Get(BorderStyle switch
        {
            FieldBorderStyle.Dashed => "D",
            FieldBorderStyle.Beveled => "B",
            FieldBorderStyle.Inset => "I",
            FieldBorderStyle.Underline => "U",
            _ => "S",
        }));
        if (BorderStyle == FieldBorderStyle.Dashed) bs.Set(D, new PdfArray([PdfInteger.Of(3)]));
        // Border style only with a border, as common PDF editors do (/BS without a border colour differs from their own fields)
        SetOrRemove(widget, BS, BorderColor is not null ? bs : null);
        widget.Remove(Border);

        widget.Set(DA, new PdfString(System.Text.Encoding.ASCII.GetBytes(DefaultAppearance)));
        SetOrRemove(widget, StartSizeKey, ShrinkToFit && FontSize > 0 ? new PdfReal(Math.Clamp(FontSize, 1, 500)) : null);
        widget.Remove(LegacyStartSizeKey);
        SetOrRemove(widget, Q, HasText && Alignment != ElementAlignment.Left ? PdfInteger.Of((int)Alignment) : null);

        if (Kind == FormFieldKind.PushButton)
        {
            if (ResetForm)
            {
                var action = new PdfDictionary();
                action.Set(PdfName.Type, PdfName.Get("Action"));
                action.Set(S, PdfName.Get("ResetForm"));
                widget.Set(A, action);
            }
            else if (widget.GetDictionary(A)?.GetName(S)?.Value == "ResetForm") widget.Remove(A);
        }
    }

    /// <summary>Default appearance (/DA): font from the default resources (Helv, TiRo, Cour; check boxes ZaDb), size and colour.</summary>
    public string DefaultAppearance
    {
        get
        {
            var font = Kind is FormFieldKind.CheckBox or FormFieldKind.Radio ? "ZaDb"
                : Font switch { ElementFont.Serif => "TiRo", ElementFont.Mono => "Cour", _ => "Helv" };
            var size = ShrinkToFit || !double.IsFinite(FontSize) ? 0 : Math.Clamp(FontSize, 0, 500);   // shrinking: automatic in the file
            int r = (TextColor >> 16) & 0xFF, g = (TextColor >> 8) & 0xFF, b = TextColor & 0xFF;
            // Grey (including black) as "0 g" like common PDF editors, otherwise RGB
            return r == g && g == b
                ? $"/{font} {ElementFace.Number(size)} Tf {ElementFace.Number(r / 255.0)} g"
                : $"/{font} {ElementFace.Number(size)} Tf {ElementFace.Number(r / 255.0)} {ElementFace.Number(g / 255.0)} {ElementFace.Number(b / 255.0)} rg";
        }
    }

    private static PdfString Text(string text) => new(PdfTextEncoding.Encode(text));

    private static PdfArray ColorArray(int rgb) =>
        new([new PdfReal(Math.Round(((rgb >> 16) & 0xFF) / 255.0, 4)), new PdfReal(Math.Round(((rgb >> 8) & 0xFF) / 255.0, 4)),
             new PdfReal(Math.Round((rgb & 0xFF) / 255.0, 4))]);

    private static void SetOrRemove(PdfDictionary dict, PdfName key, PdfObject? value)
    {
        if (value is null) dict.Remove(key);
        else dict.Set(key, value);
    }

    /// <summary>Shallow copy (references stay references).</summary>
    private static PdfDictionary Copy(PdfDictionary dict)
    {
        var copy = new PdfDictionary(dict.Count) { Resolver = dict.Resolver };
        foreach (var (key, _) in dict.Entries) copy.Set(key, dict.GetRaw(key)!);
        return copy;
    }
}
