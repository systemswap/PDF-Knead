using Avalonia.Controls;

namespace PdfKnead.Views;

/// <summary>„Barrierefreiheit“ as an area on the right (docs/ABLAUFPLAN.md, 6.7; see <see cref="ViewModels.DocumentViewModel.OpenAccessibilityAsync"/>).</summary>
public sealed partial class AccessibilityPanel : UserControl
{
    public AccessibilityPanel() => InitializeComponent();
}
