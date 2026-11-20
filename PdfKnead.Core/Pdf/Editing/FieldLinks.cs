namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Linked fields (KONZEPT.md, section 5.4a): fields of one kind with the same full name share their value — in the file they are one field
/// with several widgets (ISO 32000-2, 12.7.4). Radio buttons share their name on purpose (a group), signature fields and push buttons are
/// never linked.
/// </summary>
public static class FieldLinks
{
    /// <summary>Whether fields of this kind can be linked by their name.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>True for text, check box, combo box and list box.</returns>
    public static bool Linkable(FormFieldKind kind) => kind is FormFieldKind.Text or FormFieldKind.CheckBox or FormFieldKind.ComboBox or FormFieldKind.ListBox;

    /// <summary>
    /// The full name of a field of another program as it currently applies: renamed, the own part is new and the names of the parents stay.
    /// </summary>
    /// <param name="foreign">The widget.</param>
    /// <returns>The full name; empty if it is no field.</returns>
    public static string CurrentName(ForeignAnnotation foreign)
    {
        if (FormField.Of(foreign) is not { } field) return "";
        if (foreign.Spec is { } spec && FieldSpec.Of(foreign) is { } original && spec.Name != original.Name)
            return field.Name.Length > original.Name.Length && field.Name.EndsWith(original.Name, StringComparison.Ordinal)
                ? field.Name[..^original.Name.Length] + spec.Name
                : spec.Name;
        return field.Name;
    }
}
