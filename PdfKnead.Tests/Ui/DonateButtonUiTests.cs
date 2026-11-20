using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The donate button at the top right (decided on 18.11.2026): 25 pixels left of the save button, it opens the donation page in the
/// browser of the system — only on the click, through the same way as every other address the program opens.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class DonateButtonUiTests
{
    [Fact]
    public void DonateButton_LeftOfTheSaveButton_OpensTheDonationPage()
    {
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var opened = new List<string>();
            vm.OpenAddress = address =>
            {
                opened.Add(address);
                return Task.CompletedTask;
            };
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                UiSession.Capture(window);
                // the window replaces the delegate with the one of the system when it opens: put the recording one back for the test
                vm.OpenAddress = address =>
                {
                    opened.Add(address);
                    return Task.CompletedTask;
                };
                var donate = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DonateButton");
                Assert.True(donate.IsVisible);
                Assert.Equal(Tr.T("Spenden"), Avalonia.Automation.AutomationProperties.GetName(donate));
                // the save button is the next one to the right; the gap between them is 25 pixels
                var row = (StackPanel)donate.GetVisualParent()!;
                var save = (Control)row.Children[row.Children.IndexOf(donate) + 1];
                var gap = save.Bounds.Left - donate.Bounds.Right;
                Assert.InRange(gap, 24.5, 25.5);
                // nothing is opened by itself; the click opens exactly the donation page
                Assert.Empty(opened);
                await vm.DonateCommand.ExecuteAsync(null);
                Assert.Equal([MainViewModel.DonationAddress], opened);
                Assert.Equal("https://ko-fi.com/systemswap", MainViewModel.DonationAddress);
                if (UiSession.ScreenshotFolder() is { } folder)
                    UiSession.Capture(window).Save(Path.Combine(folder, "spenden-knopf.png"), PngBitmapEncoderOptions.Default);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
