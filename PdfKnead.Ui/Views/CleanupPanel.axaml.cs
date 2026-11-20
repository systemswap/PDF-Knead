using Avalonia.Controls;

namespace PdfKnead.Views;

/// <summary>„Dokument bereinigen“ as an area on the right (KONZEPT.md, section 5.18; see <see cref="ViewModels.DocumentViewModel.OpenCleanupAsync"/>).</summary>
public sealed partial class CleanupPanel : UserControl
{
    public CleanupPanel() => InitializeComponent();
}
