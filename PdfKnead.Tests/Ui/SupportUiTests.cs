using Avalonia.Controls;
using Avalonia.VisualTree;
using PdfKnead.Core.Policy;
using PdfKnead.Tests.Pdf;
using PdfKnead.Services;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The window that invites support (decided on 21.11.2026): 30 days after the first start at the earliest, only right after a successful
/// save, then after 9 months („Vielleicht später“) or 18 months („Unterstützen“); no way to switch it off in the program, an organization
/// switches it off with its function file. „Unterstützen“ opens the page in the browser of the system.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class SupportUiTests
{
    private static readonly DateOnly Day = new(2027, 3, 15);

    private static void Reset()
    {
        ActivePolicy.Use(new PolicyLoad(PolicyLoadState.NotThere, null, ""));
        UserSettings.Update(s => s with { FirstUseDay = null, SupportNextDay = null });
    }

    [Fact]
    public void Rule_ThirtyDaysAfterTheFirstStart_ThenNineOrEighteenMonths()
    {
        Assert.False(SupportPrompt.Due(Day, null, null));                                   // first start not known yet
        Assert.False(SupportPrompt.Due(Day, "2027-02-14", null));                           // 29 days
        Assert.True(SupportPrompt.Due(Day, "2027-02-13", null));                            // 30 days
        Assert.False(SupportPrompt.Due(Day, "2026-01-01", "2027-03-16"));                   // the next day not reached
        Assert.True(SupportPrompt.Due(Day, "2026-01-01", "2027-03-15"));
        Assert.False(SupportPrompt.Due(Day, "kaputt", null));
        Assert.True(SupportPrompt.Due(Day, "2026-01-01", "kaputt"));                        // an unreadable next day counts as none
        Assert.Equal(new DateOnly(2027, 12, 15), SupportPrompt.NextDay(Day, supported: false));
        Assert.Equal(new DateOnly(2028, 9, 15), SupportPrompt.NextDay(Day, supported: true));
    }

    [Fact]
    public void AfterSaving_WhenDue_TheWindowComes_ThenWaitsNineOrEighteenMonths()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(1).Build(), "unterstuetzen-speichern.pdf");
        UiSession.Run(async () =>
        {
            Reset();
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                var today = Day;
                var answers = new Queue<bool?>([false, true]);
                var shown = 0;
                var opened = new List<string>();
                // the window sets its own delegates when it opens: the recording ones afterwards
                vm.Today = () => today;
                vm.ShowSupportInvitation = () =>
                {
                    shown++;
                    return Task.FromResult(answers.Dequeue());
                };
                vm.OpenAddress = address =>
                {
                    opened.Add(address);
                    return Task.CompletedTask;
                };
                await vm.OpenAsync([pdf]);

                // first start 10 days ago: not yet
                UserSettings.Update(s => s with { FirstUseDay = "2027-03-05" });
                await vm.SaveCommand.ExecuteAsync(null);
                Assert.Equal(0, shown);

                // 40 days: after the save, once; „Vielleicht später“ → 9 months, nothing opened
                UserSettings.Update(s => s with { FirstUseDay = "2027-02-03" });
                await vm.SaveCommand.ExecuteAsync(null);
                Assert.Equal(1, shown);
                Assert.Equal("2027-12-15", UserSettings.Current.SupportNextDay);
                Assert.Empty(opened);
                await vm.SaveCommand.ExecuteAsync(null);
                Assert.Equal(1, shown);

                // 9 months later: again; „Unterstützen“ → the page in the browser, then 18 months
                today = new DateOnly(2027, 12, 15);
                await vm.SaveCommand.ExecuteAsync(null);
                Assert.Equal(2, shown);
                Assert.Equal([MainViewModel.DonationAddress], opened);
                Assert.Equal("2029-06-15", UserSettings.Current.SupportNextDay);
            }
            finally
            {
                window.Close();
                Reset();
            }
        });
    }

    [Fact]
    public void FunctionFile_SwitchesTheWindowOff()
    {
        Reset();
        try
        {
            UserSettings.Update(s => s with { FirstUseDay = "2026-01-01" });
            Assert.True(SupportPrompt.Due(Day));
            ActivePolicy.Use(new PolicyLoad(PolicyLoadState.Loaded, new FeaturePolicy { Denied = new HashSet<string> { SupportPrompt.PolicyId } },
                "berechtigungen.json"));
            Assert.False(SupportPrompt.Due(Day));
            Assert.Contains(PolicyCatalog.All, f => f.Id == SupportPrompt.PolicyId);
        }
        finally
        {
            Reset();
        }
    }

    [Fact]
    public void Window_TheTextAndTwoButtons_NoWayToSwitchItOff()
    {
        UiSession.Run(() =>
        {
            var window = new SupportWindow();
            window.Show();
            try
            {
                var shot = UiSession.Capture(window);
                if (UiSession.ScreenshotFolder() is { } folder)
                    shot.Save(Path.Combine(folder, "unterstuetzen-fenster.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                Assert.Contains(Tr.T("Vielen Dank, dass Sie PDF-Knead nutzen."), texts);
                Assert.Contains(Tr.T("Ich hoffe, es erleichtert Ihnen die Arbeit mit Ihren Dokumenten."), texts);
                Assert.Contains(Tr.T("Wenn Sie als Firma das Projekt sponsern möchten, stelle ich Ihnen gern eine Rechnung aus."), texts);
                var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
                Assert.Equal(["SupportButton", "LaterButton", "ContactButton"], buttons.Select(b => b.Name));
                Assert.DoesNotContain(texts, t => t is not null && t.Contains("Nicht mehr", StringComparison.Ordinal));
                Assert.Equal(Tr.T("PDF-Knead unterstützen"), window.Title);
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// The contact address: shown as it is, a click opens the mail program with it (and leaves the window open), the context menu copies
    /// it; the program itself sends nothing.
    /// </summary>
    internal static async Task ContactWorks(Window window)
    {
        var launched = new List<Uri>();
        var copied = new List<string>();
        var (launch, copy) = (Contact.Launch, Contact.Copy);
        Contact.Launch = (_, uri) => { launched.Add(uri); return Task.CompletedTask; };
        Contact.Copy = (_, text) => { copied.Add(text); return Task.CompletedTask; };
        try
        {
            Assert.Equal("pdf-knead@tuta.io", Contact.Address);
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ContactButton");
            Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Contact.Address);
            Assert.Equal(Tr.T("Kontakt per E-Mail"), Avalonia.Automation.AutomationProperties.GetName(button));
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Task.Yield();
            Assert.Equal([new Uri("mailto:pdf-knead@tuta.io")], launched);
            Assert.True(window.IsVisible);
            var item = Assert.Single(button.ContextMenu!.Items.OfType<MenuItem>());
            Assert.Equal(Tr.T("Adresse kopieren"), item.Header);
            item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            await Task.Yield();
            Assert.Equal(["pdf-knead@tuta.io"], copied);
        }
        finally
        {
            (Contact.Launch, Contact.Copy) = (launch, copy);
        }
    }

    [Fact]
    public void Window_ContactAddress_OpensTheMailProgram_OrCopies()
    {
        UiSession.Run(async () =>
        {
            var window = new SupportWindow();
            window.Show();
            try
            {
                await ContactWorks(window);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
