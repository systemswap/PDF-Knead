using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>
/// Asks before a save that invalidates digital signatures: PDF-Knead rewrites the whole file, so the signed bytes change.
/// Over the open file the default is “Save as new file”, so a hasty Enter keeps the signed original; for a new file only
/// “Save” and “Cancel” are offered, because the original stays untouched anyway.
/// </summary>
public partial class SignedSaveWindow : Window
{
    public SignedSaveWindow() => InitializeComponent();

    public SignedSaveWindow(SignedSaveQuestion question) : this()
    {
        FileText.Text = question.FileName;
        MainText.Text = question.Count == 1
            ? Tr.T("Das Dokument ist digital unterschrieben. Beim Speichern schreibt PDF-Knead die Datei neu — die digitale Unterschrift wird dadurch ungültig.")
            : Tr.T("Das Dokument hat {0} digitale Unterschriften. Beim Speichern schreibt PDF-Knead die Datei neu — die digitalen Unterschriften werden dadurch ungültig.", question.Count);
        // A restriction of the signatures is the reason (certified document, locked field): said first
        if (question.Reason is { } reason) MainText.Text = reason + " " + MainText.Text;
        if (question.OverwritesOriginal)
            AdviceText.Text = Tr.T("Wer das unterschriebene Original behalten möchte, speichert als neue Datei.");
        else
        {
            AdviceText.Text = Tr.T("Das unterschriebene Original bleibt unverändert; ungültig ist die Unterschrift nur in der neuen Datei.");
            SaveButton.Content = Tr.T("Speichern");
            SaveButton.Classes.Add("primary");
            SaveButton.IsDefault = true;
            SaveAsNewButton.IsVisible = false;
            SaveAsNewButton.IsDefault = false;
        }
        // A fixed signature is the reason: signed digitally, it would be appended and the existing signature stay valid
        if (question.FixedSignature)
            AdviceText.Text += " " + Tr.T("Tipp: Abbrechen und die Unterschrift mit „Digital signieren“ einsetzen — dann bleibt die vorhandene Unterschrift gültig.");
    }

    private void OnSaveAsNew(object? sender, RoutedEventArgs e) => Close(SignedSaveAnswer.SaveAsNew);

    private void OnSave(object? sender, RoutedEventArgs e) => Close(SignedSaveAnswer.Save);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(SignedSaveAnswer.Cancel);
}
