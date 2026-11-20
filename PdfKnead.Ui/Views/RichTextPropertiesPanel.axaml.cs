using Avalonia.Controls;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>Formatting of edited text (see KONZEPT.md, section 5.14).</summary>
public sealed partial class RichTextPropertiesPanel : UserControl
{
    public RichTextPropertiesPanel()
    {
        InitializeComponent();
    }

    // The font selector returns keyboard focus to the page only when the list closes, so the arrow keys can step through the fonts.
    private void OnFamilyClosed(object? sender, EventArgs e) => (DataContext as RichTextPropertiesViewModel)?.ReturnFocus();
}
