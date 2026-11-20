using Avalonia.Controls;
using Avalonia.Input;

namespace PdfKnead.Views;

/// <summary>Properties of the selected form fields.</summary>
public sealed partial class FieldPropertiesPanel : UserControl
{
    public FieldPropertiesPanel()
    {
        InitializeComponent();
        FormulaInsert.SelectionChanged += (_, _) => InsertField();
        // Enter in the name box takes the name over (the box binds on losing focus — Enter alone did nothing, docs/FEHLERGEDAECHTNIS.md, No. 360)
        NameBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
            e.Handled = true;
            Focus();
        };
    }

    /// <summary>“Insert field”: inserts the field name into the formula at the caret, with a space before and after where needed, and returns focus to the formula.</summary>
    private void InsertField()
    {
        if (FormulaInsert.SelectedItem is not string name) return;
        FormulaInsert.SelectedItem = null;
        var text = FormulaBox.Text ?? "";
        var at = Math.Clamp(FormulaBox.CaretIndex, 0, text.Length);
        var before = at > 0 && !char.IsWhiteSpace(text[at - 1]) ? " " : "";
        var after = at < text.Length && !char.IsWhiteSpace(text[at]) ? " " : "";
        FormulaBox.Text = text[..at] + before + name + after + text[at..];
        FormulaBox.CaretIndex = at + before.Length + name.Length;
        FormulaBox.Focus();
    }
}
