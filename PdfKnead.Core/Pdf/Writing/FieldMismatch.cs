using System.Globalization;
using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// Form fields whose stored value differs from what their appearance shows (docs/RECHERCHE-VERSTECKEN.md, B-7,
/// <see cref="CleanupItems.FieldValueMismatch"/>): a reader shows the appearance, while exporting, processing and a digital signature
/// use the value. Such a field is drawn anew from its value, as when it is filled in.
/// </summary>
/// <remarks>
/// Checked are text fields, combo boxes, check boxes and radio buttons with an appearance of their own; list boxes show all their entries,
/// password fields never their value. A text field matches if its appearance shows the value or the value as its format shows it
/// (<see cref="FieldFormat.Display"/>), compared without white space; a format script the program does not know, characters without
/// readable text, forms whose readers draw every field anew (/NeedAppearances, a field without text in its appearance) and check boxes or radio
/// buttons without a value give no finding — better nothing than a false alarm.
/// </remarks>
internal static class FieldMismatch
{
    private static readonly PdfName AcroForm = PdfName.Get("AcroForm"), NeedAppearances = PdfName.Get("NeedAppearances"),
        AA = PdfName.Get("AA"), F = PdfName.Get("F"), V = PdfName.Get("V");

    /// <summary>The unchanged foreign widgets of a page whose appearance does not show their stored value.</summary>
    /// <param name="item">The page.</param>
    /// <returns>The widgets (as page elements), in the order of the page.</returns>
    public static List<(ForeignAnnotation Widget, FormField Field)> Of(PageItem item)
    {
        var found = new List<(ForeignAnnotation, FormField)>();
        foreach (var element in item.Elements)
        {
            if (element is not ForeignAnnotation { Kind: "Widget", Value: null, Spec: null } foreign || FormField.Of(foreign) is not { } field) continue;
            try
            {
                if (Differs(item, foreign, field)) found.Add((foreign, field));
            }
            catch (Exception e) when (e is PdfException or IOException or InvalidDataException)
            {
                // a damaged appearance: nothing to compare
            }
        }
        return found;
    }

    /// <summary>The page with its mismatched fields drawn anew from their values (filled in with their own value).</summary>
    /// <param name="item">The page.</param>
    /// <param name="keep">Widgets to leave as they are, by their rectangle (the places the user keeps); null: none.</param>
    /// <returns>The page, unchanged if no field differs.</returns>
    public static PageItem Redrawn(PageItem item, Func<PdfRectangle, bool>? keep = null)
    {
        var mismatched = Of(item).Where(m => keep?.Invoke(m.Widget.Rect) != true).ToList();
        if (mismatched.Count == 0) return item;
        var elements = item.Elements.ToList();
        foreach (var (widget, field) in mismatched)
            elements[elements.IndexOf(widget)] = widget with { Value = ValueToShow(field) };
        return item.WithElements(elements);
    }

    /// <summary>What the widget is filled with: the text, or for check boxes and radio buttons the state its value asks for.</summary>
    private static string ValueToShow(FormField field) =>
        field.Kind is FormFieldKind.CheckBox or FormFieldKind.Radio
            ? field.OnState is { } on && field.StoredValue == on ? on : "Off"
            : field.StoredValue;

    private static bool Differs(PageItem item, ForeignAnnotation foreign, FormField field)
    {
        var widget = foreign.Original;
        if (((widget.GetInteger(F) ?? 0) & (2 | 32)) != 0) return false;   // hidden: „Was niemand sieht“ deals with it
        switch (field.Kind)
        {
            case FormFieldKind.CheckBox or FormFieldKind.Radio:
                // the state shown (/AS) against the value (/V): a box that shows a tick the data do not have, or the other way round. Without a
                // value the state is all there is (common in valid files, also in the PDF/A test suite) — no finding.
                if (field.OnState is not { } on || FormField.Inherited(widget, V) is not PdfName) return false;
                        return (field.State == on) != (field.StoredValue == on);
            case FormFieldKind.Text when !field.Password:
            case FormFieldKind.ComboBox:
                break;
            default:
                return false;
        }
        if (widget.GetDictionary(PdfName.Get("AP"))?.Get(PdfName.N) is not PdfStream) return false;
        var acroForm = foreign.Document.Catalog?.GetDictionary(AcroForm);
        // the own display draws the value over or instead of the appearance: what is shown is the value
        if (FieldAppearance.Replacement(foreign.Document, widget, acroForm, acroForm?.GetBoolean(NeedAppearances) == true) is not null ||
            FieldAppearance.ConstructedValue(widget, acroForm) is not null) return false;
        if (FieldSpec.IsCodeField(field)) return false;   // a code field shows its value as the code
        var format = field.Kind == FormFieldKind.Text ? FieldFormat.Of(field) : FieldFormat.None;
        if (format.Kind == FieldFormatKind.None && HasFormatScript(field)) return false;   // formatted by a script the program does not know

        var list = PageInterpreter.BuildAnnotations(foreign.Document, item.Page, [widget], maxOperations: PageInterpreter.MaxAnnotationOperations);
        if (list.UnreadableGlyphs > 0) return false;
        var shown = Normal(PageText.Create(list.Text, list.UnreadableGlyphs).Text);
        if (shown.Contains('\uFFFD')) return false;
        var value = field.StoredValue;
        var expected = field.Kind == FormFieldKind.Text ? format.Display(value) : field.DisplayText(value);
        return shown != Normal(expected) && shown != Normal(value);
    }

    /// <summary>Has the field a format action (/AA /F) of its own?</summary>
    private static bool HasFormatScript(FormField field) =>
        field.Field.GetDictionary(AA)?.ContainsKey(F) == true || field.Widget.GetDictionary(AA)?.ContainsKey(F) == true;

    /// <summary>The text without white space and in compatibility form (ligatures, full-width characters).</summary>
    private static string Normal(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
            if (!char.IsWhiteSpace(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Control) sb.Append(c);
        return sb.ToString();
    }
}
