using Avalonia.Controls;

namespace PdfKnead.Views;

/// <summary>„Felder erkennen“ as an area on the right (KONZEPT.md, section 5.9a; see <see cref="ViewModels.DocumentViewModel.RecognizeFieldsAsync"/>).</summary>
public sealed partial class FormProposalsPanel : UserControl
{
    public FormProposalsPanel() => InitializeComponent();
}
