namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Most recently chosen look of a kind of field, so that new fields of the same kind receive it straight away: border, fill,
/// font, alignment, check symbol and, for check boxes, the label. Stored in the user settings.
/// </summary>
/// <param name="Kind">Kind of field: Text, Date, Number, CheckBox, Radio, ComboBox, ListBox, ResetButton.</param>
public sealed record FieldLook(string Kind)
{
    /// <summary>Border colour as 0xRRGGBB; <see langword="null"/> for no border.</summary>
    public int? BorderColor { get; init; }

    /// <summary>Fill colour as 0xRRGGBB; <see langword="null"/> for a transparent background.</summary>
    public int? FillColor { get; init; }

    /// <summary>Border width in points.</summary>
    public double BorderWidth { get; init; } = 1;

    /// <summary>Border style.</summary>
    public FieldBorderStyle BorderStyle { get; init; }

    /// <summary>Font of the field text.</summary>
    public ElementFont Font { get; init; }

    /// <summary>Font size in points; 0 = automatic.</summary>
    public double FontSize { get; init; }

    /// <summary>Shrink the font automatically (starting from <see cref="FontSize"/>).</summary>
    public bool ShrinkToFit { get; init; }

    /// <summary>Text colour as 0xRRGGBB.</summary>
    public int TextColor { get; init; }

    /// <summary>Horizontal alignment of the text.</summary>
    public ElementAlignment Alignment { get; init; }

    /// <summary>Check symbol of check boxes and radio buttons.</summary>
    public CheckStyle CheckStyle { get; init; }

    /// <summary>Check boxes: add a label on insertion (on unless switched off).</summary>
    public bool ShowLabel { get; init; } = true;

    /// <summary>Font of the label.</summary>
    public ElementFont LabelFont { get; init; }

    /// <summary>Font size of the label in points; 0 = matching the height of the check box.</summary>
    public double LabelSize { get; init; }

    /// <summary>Colour of the label as 0xRRGGBB.</summary>
    public int LabelColor { get; init; }

    /// <summary>Whether the label is placed to the left of the check box instead of the right.</summary>
    public bool LabelLeft { get; init; }

    /// <summary>Gap between label and check box in points.</summary>
    public double LabelGap { get; init; } = 4;

    /// <summary>Applies the look to field properties; name, format, options and so on stay.</summary>
    /// <param name="spec">The field properties.</param>
    /// <returns>The properties with this look; shrinking applies only with a fixed font size, the check symbol only to check boxes and radio buttons.</returns>
    public FieldSpec ApplyTo(FieldSpec spec) => spec with
    {
        BorderColor = BorderColor,
        FillColor = FillColor,
        BorderWidth = BorderWidth,
        BorderStyle = BorderStyle,
        Font = Font,
        FontSize = FontSize,
        ShrinkToFit = ShrinkToFit && FontSize > 0,
        TextColor = TextColor,
        Alignment = Alignment,
        CheckStyle = spec.Kind is FormFieldKind.CheckBox or FormFieldKind.Radio ? CheckStyle : spec.CheckStyle,
    };

    /// <summary>Takes the look from a field's properties; the label settings are set separately.</summary>
    /// <param name="kind">Kind of field (see <see cref="Kind"/>).</param>
    /// <param name="spec">The field properties.</param>
    /// <returns>The look with default label settings.</returns>
    public static FieldLook From(string kind, FieldSpec spec) => new(kind)
    {
        BorderColor = spec.BorderColor,
        FillColor = spec.FillColor,
        BorderWidth = spec.BorderWidth,
        BorderStyle = spec.BorderStyle,
        Font = spec.Font,
        FontSize = spec.FontSize,
        ShrinkToFit = spec.ShrinkToFit,
        TextColor = spec.TextColor,
        Alignment = spec.Alignment,
        CheckStyle = spec.CheckStyle,
    };
}
