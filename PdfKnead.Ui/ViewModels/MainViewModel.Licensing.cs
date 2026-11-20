using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Licensing;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>The answer of the question about the kind of use: the kind, and whether the user wants to enter a license key right away.</summary>
public sealed record UsageChoice(UsageKind Usage, bool EnterLicense);

/// <summary>
/// The kind of use and the license (LICENSE, sections 2 and 3): the question at the first start, the lock of the eight licensed features in
/// commercial use after the introductory period — checked when a feature is started, never in the middle of it; opening, viewing and saving
/// always work — and the windows to enter a key or change the kind of use.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Asks for the kind of use (set by the view); the current one is preselected; null = closed without an answer.</summary>
    public Func<UsageKind, Task<UsageChoice?>>? AskUsage { get; set; }

    /// <summary>Before switching from a commercial kind of use to a free one (set by the view): true = switch.</summary>
    public Func<UsageKind, Task<bool>>? ConfirmFreeUsage { get; set; }

    /// <summary>Shows the license window (set by the view); returns when it is closed.</summary>
    public Func<LicenseViewModel, Task>? ShowLicense { get; set; }

    public bool RedactionLocked => IsLocked(LicensedFeature.Redaction);
    public bool MeasuringLocked => IsLocked(LicensedFeature.Measuring);
    public bool SigningLocked => IsLocked(LicensedFeature.CertificateSigning);
    public bool PdfALocked => IsLocked(LicensedFeature.PdfA);
    public bool ScanningLocked => IsLocked(LicensedFeature.Scanning);
    public bool FormsLocked => IsLocked(LicensedFeature.Forms);
    public bool EditingLocked => IsLocked(LicensedFeature.Editing);
    public bool RestrictionsLocked => IsLocked(LicensedFeature.Restrictions);

    /// <summary>The state of the kind of use and the license in one line (settings).</summary>
    public string LicenseStatus => Licensing.StatusText();

    /// <summary>The kind of use as declared (settings).</summary>
    public string UsageName => Licensing.UsageName(Licensing.Usage);

    /// <summary>Why „Digital signieren“ cannot be chosen because of the license; null when it can.</summary>
    public string? SigningLicenseRefusal => !Core.Policy.ActivePolicy.Allows("signature.digital") ? PolicyRefusal("signature.digital")
        : SigningLocked
        ? Tr.T("Signieren mit Zertifikat braucht nach der Einführungszeit bei gewerblicher Nutzung eine Lizenz — Datei → Einstellungen → Nutzung und Lizenz.")
        : null;

    private static bool IsLocked(LicensedFeature feature) => (Licensing.Locked() & feature) != 0;

    // the event is static: held weakly, it must not keep a window alive
    private void WatchLicensing()
    {
        Settings.ChangeUsage = ChangeUsageAsync;
        Settings.EnterLicense = EnterLicenseAsync;
        Settings.ShowPermissions = () => ShowPermissions?.Invoke() ?? Task.CompletedTask;
        Settings.ImportPolicy = ImportPolicyAsync;
        var self = new WeakReference<MainViewModel>(this);
        void OnChanged()
        {
            if (!self.TryGetTarget(out var vm))
            {
                Licensing.Changed -= OnChanged;
                return;
            }
            vm.RefreshLicense();
        }
        Licensing.Changed += OnChanged;
    }

    private void RefreshLicense()
    {
        foreach (var name in (string[])[nameof(RedactionLocked), nameof(MeasuringLocked), nameof(SigningLocked), nameof(PdfALocked), nameof(ScanningLocked), nameof(FormsLocked), nameof(EditingLocked), nameof(RestrictionsLocked),
                     nameof(LicenseStatus), nameof(UsageName), nameof(SigningLicenseRefusal)])
            OnPropertyChanged(name);
        Settings.RefreshLicense();
    }

    /// <summary>
    /// When a licensed feature is started: true when it may be; otherwise the license window explains why and how to go on, and false. In the
    /// last days of the introductory period a reminder (at most once a day).
    /// </summary>
    public bool EnsureLicensed(LicensedFeature feature)
    {
        var result = Licensing.Evaluate(feature);
        if (result.State == GateState.Introductory && result.DaysLeft <= LicensePolicy.ReminderDays) RemindOnce(result.DaysLeft);
        if (result.Allowed) return true;
        _ = OpenLicenseAsync(feature);
        return false;
    }

    private void RemindOnce(int daysLeft)
    {
        var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        if (UserSettings.Current.LicenseReminderDay == today) return;
        UserSettings.Update(s => s with { LicenseReminderDay = today });
        Message = Licensing.Context().IsTrial
            ? Tr.T("Die kostenlose Testzeit für {1} endet in {0} Tagen. Lizenz: Datei → Einstellungen → Nutzung und Lizenz.", daysLeft, Licensing.LicensedList())
            : Tr.T("Die kostenlose Einführungszeit für {1} endet in {0} Tagen. Lizenz: Datei → Einstellungen → Nutzung und Lizenz.", daysLeft, Licensing.LicensedList());
    }

    /// <summary>At the first start, before the other offers: asks once for the kind of use; closed without an answer, it asks again next time.</summary>
    public async Task OfferUsageAsync()
    {
        Licensing.NoteToday();
        // a function file of the organization sets the kind of use: no question
        if (Licensing.UsageFixed || UserSettings.Current.Usage != UsageKind.NotAsked || AskUsage is not { } ask) return;
        if (await ask(UsageKind.NotAsked) is not { } choice) return;
        Licensing.SetUsage(choice.Usage);
        if (choice.EnterLicense) await OpenLicenseAsync(null);
    }

    /// <summary>„Nutzungsart ändern“ (settings, license window): from a commercial to a free kind only after the friendly question.</summary>
    [RelayCommand]
    public async Task ChangeUsageAsync()
    {
        if (Licensing.UsageFixed)
        {
            ShowNotice(Tr.T("Die Nutzungsart ist durch die Funktionsdatei Ihrer Organisation festgelegt."));
            return;
        }
        if (AskUsage is not { } ask) return;
        var current = UserSettings.Current.Usage;
        if (await ask(current) is not { } choice) return;
        if (current is UsageKind.Commercial or UsageKind.Authority && LicensePolicy.IsFreeUse(choice.Usage)
            && ConfirmFreeUsage is { } confirm && !await confirm(choice.Usage))
            return;
        Licensing.SetUsage(choice.Usage);
        if (choice.EnterLicense) await OpenLicenseAsync(null);
    }

    /// <summary>„Lizenz eintragen“ (settings).</summary>
    [RelayCommand]
    public Task EnterLicenseAsync() => OpenLicenseAsync(null);

    /// <param name="locked">The feature that was refused; null when opened to enter a key.</param>
    private async Task OpenLicenseAsync(LicensedFeature? locked)
    {
        if (ShowLicense is not { } show) return;
        var model = new LicenseViewModel(locked) { OpenAddress = OpenAddress };
        await show(model);
        // „Nutzungsart ändern“ closes the license window first: one window at a time
        if (model.WantsUsageChange) await ChangeUsageAsync();
    }
}
