using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Licensing;
using PdfKnead.Services;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The kind of use and the license in the program (LICENSE, sections 2 and 3): asked once at the first start; after the introductory period
/// the eight licensed features of a commercial use refuse to start — with the lock on their buttons and the license window — until a matching
/// key is entered; switching to a free kind of use stays possible, after the friendly question with the way to support the development.
/// The day after the introductory period is reached through the latest day the program ran (the clock cannot be turned back).
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class LicensingUiTests
{
    private const string AfterTheEnd = "2029-06-01";

    private static void Reset() =>
        UserSettings.Update(s => s with { Usage = UsageKind.NotAsked, LicenseKey = null, LicenseEmail = null, LatestSeenDay = null, IntroductionUntil = null, CommercialSince = null });

    [Fact]
    public void FirstStart_AsksOnce_ThenRemembers()
    {
        UiSession.Run(async () =>
        {
            Reset();
            try
            {
                var vm = new MainViewModel();
                var asked = 0;
                vm.AskUsage = current =>
                {
                    asked++;
                    Assert.Equal(UsageKind.NotAsked, current);
                    return Task.FromResult<UsageChoice?>(new UsageChoice(UsageKind.Education, false));
                };
                await vm.OfferUsageAsync();
                await vm.OfferUsageAsync();
                Assert.Equal(1, asked);
                Assert.Equal(UsageKind.Education, UserSettings.Current.Usage);
                Assert.NotNull(UserSettings.Current.LatestSeenDay);

                // closed without an answer: asked again at the next start
                Reset();
                vm.AskUsage = _ => Task.FromResult<UsageChoice?>(null);
                await vm.OfferUsageAsync();
                Assert.Equal(UsageKind.NotAsked, UserSettings.Current.Usage);
            }
            finally
            {
                Reset();
            }
        });
    }

    [Fact]
    public void AfterTheEnd_CommercialUse_LockedUntilAKey_SavingStaysPossible()
    {
        UiSession.Run(async () =>
        {
            Reset();
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                var shown = new List<LicenseViewModel>();
                vm.ShowLicense = model =>
                {
                    shown.Add(model);
                    return Task.CompletedTask;
                };

                // during the introductory period: allowed, nothing shown
                UserSettings.Update(s => s with { Usage = UsageKind.Commercial });
                Assert.True(vm.EnsureLicensed(LicensedFeature.Redaction));
                Assert.False(vm.RedactionLocked);

                // after its end: refused with the license window, the buttons show the lock
                PdfKnead.Services.Licensing.TakeIntroduction(null);
                // commercial since long before: the trial is over as well
                UserSettings.Update(s => s with { LatestSeenDay = AfterTheEnd, CommercialSince = "2027-01-01" });
                PdfKnead.Services.Licensing.SetUsage(UsageKind.Commercial);
                Assert.False(vm.EnsureLicensed(LicensedFeature.Redaction));
                await Task.Yield();
                var model = Assert.Single(shown);
                Assert.True(model.IsLocked);
                Assert.Contains(Tr.T("Schwärzen"), model.LockedText);
                Assert.True(vm.RedactionLocked && vm.MeasuringLocked && vm.SigningLocked && vm.PdfALocked && vm.ScanningLocked && vm.FormsLocked && vm.EditingLocked && vm.RestrictionsLocked);
                Assert.NotNull(vm.SigningLicenseRefusal);
                UiSession.Capture(window);
                var scan = window.GetVisualDescendants().OfType<RibbonButton>().First(b => b.Label == Tr.T("Scannen"));
                Assert.Same(window.FindResource("Icon.LockClosed"), scan.Icon);

                // the measuring tool does not switch on
                vm.MeasureCommand.Execute(null);
                Assert.NotEqual(ViewTool.Measure, vm.Tool);

                // creating forms: refused as well, „Formular bearbeiten“ shows the lock
                Assert.False(vm.EnsureLicensed(LicensedFeature.Forms));
                vm.RibbonTab = RibbonTab.Form;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var form = window.GetVisualDescendants().OfType<RibbonToggleButton>().First(b => b.Label == Tr.T("Formular bearbeiten"));
                Assert.Same(window.FindResource("Icon.LockClosed"), form.Icon);
                UiSession.Capture(window);

                // a key for the features, issued for versions up to well after this one
                var key = LicenseCodec.Encode(new LicenseCode { Company = "Musterfirma GmbH", Serial = 42, UpdatesUntil = new DateOnly(2040, 12, 31) });
                var entry = new LicenseViewModel(null) { Key = key };
                Assert.True(entry.KeyFine);
                Assert.Contains("Musterfirma GmbH", entry.KeyText);
                entry.SaveCommand.Execute(null);
                Assert.Equal(key, UserSettings.Current.LicenseKey);
                Assert.True(vm.EnsureLicensed(LicensedFeature.Redaction));
                Assert.False(vm.RedactionLocked);
                Assert.Null(vm.SigningLicenseRefusal);
                Assert.Contains("Musterfirma GmbH", vm.LicenseStatus);
                UiSession.Capture(window);
                Assert.NotSame(window.FindResource("Icon.LockClosed"), scan.Icon);
                Assert.NotSame(window.FindResource("Icon.LockClosed"), form.Icon);
                Assert.True(vm.EnsureLicensed(LicensedFeature.Forms));
                Assert.True(vm.EnsureLicensed(LicensedFeature.Editing));
                Assert.True(vm.EnsureLicensed(LicensedFeature.Restrictions));

                // a typing error is said at once
                entry.Key = key[..^2] + (key[^1] == 'A' ? "BB" : "AA");
                Assert.False(entry.KeyFine);
                Assert.Equal(Tr.T("Der Lizenzschlüssel ist nicht lesbar — bitte auf Tippfehler prüfen."), entry.KeyText);
            }
            finally
            {
                window.Close();
                Reset();
            }
        });
    }

    [Fact]
    public void SwitchToPrivate_AlsoWhenLocked_OnlyAfterTheFriendlyQuestion()
    {
        UiSession.Run(async () =>
        {
            Reset();
            try
            {
                var vm = new MainViewModel();
                UserSettings.Update(s => s with { Usage = UsageKind.Commercial, LatestSeenDay = AfterTheEnd, CommercialSince = "2027-01-01" });
                Assert.True(vm.ScanningLocked);
                vm.AskUsage = _ => Task.FromResult<UsageChoice?>(new UsageChoice(UsageKind.Private, false));
                var questions = 0;
                var answer = false;
                vm.ConfirmFreeUsage = usage =>
                {
                    questions++;
                    Assert.Equal(UsageKind.Private, usage);
                    return Task.FromResult(answer);
                };
                await vm.ChangeUsageCommand.ExecuteAsync(null);
                Assert.Equal(1, questions);
                Assert.Equal(UsageKind.Commercial, UserSettings.Current.Usage);
                answer = true;
                await vm.ChangeUsageCommand.ExecuteAsync(null);
                Assert.Equal(UsageKind.Private, UserSettings.Current.Usage);
                Assert.False(vm.ScanningLocked);
                Assert.True(vm.EnsureLicensed(LicensedFeature.Scanning));

                // from a free kind to another one: no question
                vm.AskUsage = _ => Task.FromResult<UsageChoice?>(new UsageChoice(UsageKind.NonProfit, false));
                await vm.ChangeUsageCommand.ExecuteAsync(null);
                Assert.Equal(2, questions);
                Assert.Equal(UsageKind.NonProfit, UserSettings.Current.Usage);
            }
            finally
            {
                Reset();
            }
        });
    }

    [Fact]
    public void Windows_UsageNotice_Support_License()
    {
        UiSession.Run(async () =>
        {
            Reset();
            var folder = UiSession.ScreenshotFolder();
            var usage = new UsageWindow(UsageKind.NotAsked);
            usage.Show();
            try
            {
                var next = usage.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NextButton");
                Assert.False(next.IsEnabled);
                if (folder is not null) UiSession.Capture(usage).Save(Path.Combine(folder, "nutzungsart.png"), PngBitmapEncoderOptions.Default);
                usage.GetVisualDescendants().OfType<RadioButton>().Single(b => b.Name == "CommercialBox").IsChecked = true;
                Assert.True(next.IsEnabled);
                next.Command?.Execute(null);
                next.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                var period = usage.FindControl<TextBlock>("PeriodText")!;
                Assert.True(period.IsEffectivelyVisible);
                var days = LicensePolicy.DaysLeft(DateOnly.FromDateTime(DateTime.Now), LicensePolicy.IntroductionEnd);
                Assert.Contains(days.ToString(Tr.Culture), period.Text);
                if (folder is not null) UiSession.Capture(usage).Save(Path.Combine(folder, "nutzungsart-hinweis.png"), PngBitmapEncoderOptions.Default);
            }
            finally
            {
                usage.Close();
            }

            var opened = 0;
            var support = new SupportWindow(UsageKind.Private, () =>
            {
                opened++;
                return Task.CompletedTask;
            });
            support.Show();
            try
            {
                var button = support.GetVisualDescendants().OfType<Button>().First(b => b.Content is StackPanel);
                button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await Task.Yield();
                Assert.Equal(1, opened);
                if (folder is not null) UiSession.Capture(support).Save(Path.Combine(folder, "auf-privat-umstellen.png"), PngBitmapEncoderOptions.Default);
            }
            finally
            {
                support.Close();
            }

            UserSettings.Update(s => s with { Usage = UsageKind.Authority, LatestSeenDay = AfterTheEnd, CommercialSince = "2027-01-01" });
            var license = new LicenseWindow(new LicenseViewModel(LicensedFeature.PdfA));
            license.Show();
            try
            {
                Assert.Equal(Tr.T("Lizenz erforderlich"), license.Title);
                if (folder is not null) UiSession.Capture(license).Save(Path.Combine(folder, "lizenz-erforderlich.png"), PngBitmapEncoderOptions.Default);
            }
            finally
            {
                license.Close();
                Reset();
            }
        });
    }
}
