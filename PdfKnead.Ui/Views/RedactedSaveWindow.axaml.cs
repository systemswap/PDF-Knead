using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>
/// Asks before saving over the open file after redacting in the document: the content under the redactions is then permanently
/// gone from that file as well. The default is “Save as new file”, so a hasty Enter destroys nothing.
/// </summary>
public partial class RedactedSaveWindow : Window
{
    public RedactedSaveWindow() => InitializeComponent();

    public RedactedSaveWindow(RedactedSaveQuestion question) : this()
    {
        FileText.Text = question.FileName;
        DeleteText.Text = Tr.T("Die geschwärzten Inhalte werden in „{0}“ endgültig gelöscht.", question.FileName);
        SyncHint.IsVisible = question.InSyncFolder;
    }

    private void OnSaveAsNew(object? sender, RoutedEventArgs e) => Close(RedactedSaveAnswer.SaveAsNew);

    private void OnOverwrite(object? sender, RoutedEventArgs e) => Close(RedactedSaveAnswer.Overwrite);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(RedactedSaveAnswer.Cancel);
}
