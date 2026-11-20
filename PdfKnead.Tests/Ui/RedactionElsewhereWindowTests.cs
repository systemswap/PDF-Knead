using Avalonia.Controls;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „Jetzt schwärzen?“ with an entry of a module (KONZEPT.md, section 5.24; decided on 15.11.2026 for the e-invoice): the places
/// where the text was found stay one list, the module's entry is a sentence of its own, and what stays if „Gleich mit entfernen“ is
/// unticked shows in the warning colour only then.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class RedactionElsewhereWindowTests
{
    [Fact]
    public void ModuleEntry_OwnSentence_WarningOnlyWhenUnticked()
    {
        UiSession.Run(() =>
        {
            var question = new RedactionQuestion(1, 1, null, true)
            {
                Elsewhere =
                [
                    new RedactionElsewhere(CleanupItems.Metadata, Tr.T("Dokumenteigenschaften")),
                    new RedactionElsewhere(CleanupItems.Attachments, "Ein Satz des Bausteins.", "Was dann bleibt.", Sentence: true),
                ],
            };
            var dialog = new RedactionWindow(question);
            dialog.Show();
            try
            {
                UiSession.Capture(dialog);
                Assert.Equal(Tr.T("Geschwärzter Text steht auch in: {0}", Tr.T("Dokumenteigenschaften")) + "\n" + "Ein Satz des Bausteins.",
                    dialog.FindControl<TextBlock>("ElsewhereText")!.Text);
                var warning = dialog.FindControl<TextBlock>("KeepWarning")!;
                var clean = dialog.FindControl<CheckBox>("CleanElsewhere")!;
                Assert.True(clean.IsChecked);
                Assert.False(warning.IsVisible);
                clean.IsChecked = false;
                Assert.True(warning.IsVisible);
                Assert.Equal("Was dann bleibt.", warning.Text);
                if (UiSession.ScreenshotFolder() is { } shots)
                    UiSession.Capture(dialog).Save(Path.Combine(shots, "97-schwaerzen-baustein.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                clean.IsChecked = true;
                Assert.False(warning.IsVisible);
            }
            finally
            {
                dialog.Close();
            }
            return Task.CompletedTask;
        });
    }
}
