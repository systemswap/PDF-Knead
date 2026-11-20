using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Licensing;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>
/// The license window: enter a key (and the e-mail address of a key bound to one), see at once what it unlocks or why not, and — when a
/// feature was refused — why, with the ways on: a key, a later end of the introductory period from the version file, another kind of use.
/// </summary>
public sealed partial class LicenseViewModel : ObservableObject
{
    /// <summary>Where license inquiries go (LICENSE, section 3 (7)).</summary>
    public const string ProjectAddress = "https://github.com/systemswap/PDF-Knead";

    private readonly LicensedFeature? _locked;

    /// <param name="locked">The feature that was refused; null when opened to enter a key.</param>
    public LicenseViewModel(LicensedFeature? locked)
    {
        _locked = locked;
        _key = UserSettings.Current.LicenseKey ?? "";
        _email = UserSettings.Current.LicenseEmail ?? "";
    }

    /// <summary>Opens an address in the browser of the system (set by the main window).</summary>
    public Func<string, Task>? OpenAddress { get; init; }

    /// <summary>Set when „Nutzungsart ändern“ was chosen: the main window asks once this window is closed.</summary>
    public bool WantsUsageChange { get; private set; }

    /// <summary>Asks the window to close.</summary>
    public event Action? CloseRequested;

    /// <summary>Whether the window was opened because a feature was refused.</summary>
    public bool IsLocked => _locked is not null;

    public string Title => IsLocked ? Tr.T("Lizenz erforderlich") : Tr.T("Lizenz eintragen");

    /// <summary>Why a feature was refused.</summary>
    public string LockedText => _locked is { } feature
        ? Tr.T("„{0}“ braucht bei der Nutzungsart „{1}“ seit dem Ende der kostenlosen Zeit ({2:d}) eine Lizenz. Öffnen, Ansehen und Speichern gehen weiter wie gewohnt, auch für schon geschwärzte, signierte oder umgewandelte Dateien.",
            Licensing.FeatureName(feature), Licensing.UsageName(Licensing.Usage), Licensing.Context().FreeUntil)
        : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyText), nameof(KeyFine), nameof(NeedsEmail))]
    private string _key;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyText), nameof(KeyFine))]
    private string _email;

    /// <summary>A message after „Nach Verlängerung suchen“.</summary>
    [ObservableProperty]
    private string _extensionText = "";

    /// <summary>The state with the settings as they are (also after a later end of the introductory period was taken over).</summary>
    public string Status => Licensing.StatusText();

    /// <summary>Whether the key as typed is bound to an e-mail address (then the field for it is shown).</summary>
    public bool NeedsEmail => LicenseCodec.Decode(Key)?.BindsEmail == true;

    /// <summary>Whether the key as typed unlocks something on this computer and in this version.</summary>
    public bool KeyFine => KeyProblemNow() == KeyProblem.None;

    /// <summary>What the key as typed does, at once while typing.</summary>
    public string KeyText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Key)) return "";
            var code = LicenseCodec.Decode(Key);
            var problem = KeyProblemNow();
            if (problem != KeyProblem.None) return Licensing.ProblemText(problem, code);
            var company = code!.Company.Length > 0 ? code.Company : Tr.T("diesen Lizenznehmer");
            return code.HasUpdatesWithoutEnd
                ? Tr.T("Gültiger Aufbau — für {0} (Nr. {1}): {2}; Updates ohne Ende.", company, code.Serial, Licensing.FeatureNames(code.Features))
                : Tr.T("Gültiger Aufbau — für {0} (Nr. {1}): {2}; alle Versionen bis {3:d}.", company, code.Serial, Licensing.FeatureNames(code.Features), code.UpdatesUntil);
        }
    }

    private KeyProblem KeyProblemNow()
    {
        var context = Licensing.Context() with { Key = Key, Email = Email };
        var code = LicenseCodec.Decode(Key);
        // a key that unlocks only some features is fine for those
        return LicensePolicy.Check(code, code?.Features is { } f and not LicensedFeature.None ? f : LicensedFeature.All, context);
    }

    /// <summary>„Eintragen“: takes the key over (also one that does not fit yet — the status says why) and closes.</summary>
    [RelayCommand]
    private void Save()
    {
        Licensing.SetKey(Key, NeedsEmail ? Email : null);
        CloseRequested?.Invoke();
    }

    /// <summary>„Schlüssel entfernen“.</summary>
    [RelayCommand]
    private void Remove()
    {
        Key = "";
        Email = "";
        Licensing.SetKey(null, null);
        OnPropertyChanged(nameof(Status));
    }

    /// <summary>„Nutzungsart ändern“: closes; the main window asks next.</summary>
    [RelayCommand]
    private void ChangeUsage()
    {
        WantsUsageChange = true;
        CloseRequested?.Invoke();
    }

    /// <summary>„Projektseite öffnen“: license inquiries go there — only on this click.</summary>
    [RelayCommand]
    private async Task OpenProject()
    {
        if (OpenAddress is { } open) await open(ProjectAddress);
    }

    /// <summary>
    /// „Nach Verlängerung suchen“: fetches the signed version file once — only on this click — and takes over a later end of the
    /// introductory period announced there (LICENSE, section 3 (5)).
    /// </summary>
    [RelayCommand]
    private async Task SearchExtension()
    {
        var before = Licensing.Context().IntroductionEnd;
        ExtensionText = Tr.T("Suche …");
        await Updates.Instance.CheckAsync(onRequest: false);
        var after = Licensing.Context().IntroductionEnd;
        ExtensionText = after > before
            ? Tr.T("Die Einführungszeit wurde bis {0:d} verlängert.", after)
            : Updates.Instance.LastCheckReached ? Tr.T("Keine Verlängerung angekündigt.") : Tr.T("Keine Update-Quelle erreichbar — ohne Netz?");
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(LockedText));
    }
}
