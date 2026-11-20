using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfKnead.Services;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The setting „Dateien öffnen“ in Datei → Einstellungen (decided on 16.11.2026): a window of its own (the default, as before) or the
/// window used last as a new tab; the choice is saved at once.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class OpenFilesSettingUiTests
{
    [Fact]
    public void Settings_OwnWindowByDefault_ChoiceSaved() => UiSession.Run(() =>
    {
        UserSettings.Update(s => s with { OpenInRunningWindow = false });
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        try
        {
            vm.ShowBackstageCommand.Execute(BackstagePage.Settings);
            Dispatcher.UIThread.RunJobs();
            var radios = window.GetVisualDescendants().OfType<RadioButton>().ToList();
            var own = radios.Single(r => r.Name == "OpenInOwnWindowOption");
            var running = radios.Single(r => r.Name == "OpenInRunningWindowOption");
            Assert.True(own.IsEffectivelyVisible);
            Assert.True(own.IsChecked);
            Assert.False(running.IsChecked);

            running.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(UserSettings.Current.OpenInRunningWindow);
            Assert.False(own.IsChecked);
            Assert.True(Core.Platform.AppSettings.Load(UserSettings.File).OpenInRunningWindow);   // saved at once

            own.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(UserSettings.Current.OpenInRunningWindow);
            Assert.False(running.IsChecked);
            if (UiSession.ScreenshotFolder() is { } shots)
                UiSession.Capture(window).Save(Path.Combine(shots, "05c-einstellungen-dateien-oeffnen.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        finally
        {
            window.Close();
            UserSettings.Update(s => s with { OpenInRunningWindow = false });
        }
    });
}
