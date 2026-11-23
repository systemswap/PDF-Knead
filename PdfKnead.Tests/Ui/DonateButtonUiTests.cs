using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PdfKnead.Core.Policy;
using PdfKnead.Services;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The donate button at the top right (decided on 18.11.2026, changed on 22.11.2026): 25 pixels left of the save button; it opens the donation
/// window first, only „Unterstützen“ there opens the donation page in the browser of the system; it goes with the donation window — switched off
/// in the function file, there is no button.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class DonateButtonUiTests
{
    private static readonly DateOnly Day = new(2027, 3, 15);

    [Fact]
    public void DonateButton_LeftOfTheSaveButton_OpensTheDonationWindow()
    {
        UiSession.Run(async () =>
        {
            ActivePolicy.Use(new PolicyLoad(PolicyLoadState.NotThere, null, ""));
            UserSettings.Update(s => s with { FirstUseDay = "2026-01-01", SupportNextDay = "2027-12-15" });
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                UiSession.Capture(window);
                // the window replaces the delegates with its own when it opens: put recording ones in for the test
                var opened = new List<string>();
                var answers = new Queue<bool?>([null, false, true]);
                var shown = 0;
                vm.Today = () => Day;
                vm.OpenAddress = address =>
                {
                    opened.Add(address);
                    return Task.CompletedTask;
                };
                vm.ShowSupportInvitation = () =>
                {
                    shown++;
                    return Task.FromResult(answers.Dequeue());
                };
                var donate = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DonateButton");
                Assert.True(donate.IsVisible);
                Assert.Equal(Tr.T("Spenden"), Avalonia.Automation.AutomationProperties.GetName(donate));
                // the save button is the next one to the right; the gap between them is 25 pixels
                var row = (StackPanel)donate.GetVisualParent()!;
                var save = (Control)row.Children[row.Children.IndexOf(donate) + 1];
                Assert.InRange(save.Bounds.Left - donate.Bounds.Right, 24.5, 25.5);
                Assert.Same(vm.ShowDonationWindowCommand, donate.Command);
                Assert.Empty(opened);

                // closed and „Vielleicht später“: the window came, nothing opened, the timetable of the window stays as it was
                await vm.ShowDonationWindowCommand.ExecuteAsync(null);
                await vm.ShowDonationWindowCommand.ExecuteAsync(null);
                Assert.Equal(2, shown);
                Assert.Empty(opened);
                Assert.Equal("2027-12-15", UserSettings.Current.SupportNextDay);

                // „Unterstützen“: the donation page, and the window waits 18 months
                await vm.ShowDonationWindowCommand.ExecuteAsync(null);
                Assert.Equal(3, shown);
                Assert.Equal([MainViewModel.DonationAddress], opened);
                Assert.Equal("https://ko-fi.com/systemswap", MainViewModel.DonationAddress);
                Assert.Equal("2028-09-15", UserSettings.Current.SupportNextDay);
                if (UiSession.ScreenshotFolder() is { } folder)
                    UiSession.Capture(window).Save(Path.Combine(folder, "spenden-knopf.png"), PngBitmapEncoderOptions.Default);
            }
            finally
            {
                window.Close();
                UserSettings.Update(s => s with { FirstUseDay = null, SupportNextDay = null });
            }
        });
    }

    [Fact]
    public void DonationWindowSwitchedOff_NoButton_NothingOpens()
    {
        UiSession.Run(async () =>
        {
            ActivePolicy.Use(new PolicyLoad(PolicyLoadState.Loaded, new FeaturePolicy { Denied = new HashSet<string> { SupportPrompt.PolicyId } },
                "berechtigungen.json"));
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                UiSession.Capture(window);
                var opened = new List<string>();
                var shown = 0;
                vm.OpenAddress = address =>
                {
                    opened.Add(address);
                    return Task.CompletedTask;
                };
                vm.ShowSupportInvitation = () =>
                {
                    shown++;
                    return Task.FromResult<bool?>(true);
                };
                var donate = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DonateButton");
                Assert.False(donate.IsVisible);
                Assert.False(vm.DonateButtonVisible);
                // even if the command were reached some other way: nothing
                await vm.ShowDonationWindowCommand.ExecuteAsync(null);
                Assert.Equal(0, shown);
                Assert.Empty(opened);
            }
            finally
            {
                window.Close();
                ActivePolicy.Use(new PolicyLoad(PolicyLoadState.NotThere, null, ""));
            }
        });
    }
}
