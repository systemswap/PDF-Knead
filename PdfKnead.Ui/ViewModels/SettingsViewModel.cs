using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>A choice of appearance; <see cref="Label"/> is the German text (shown translated).</summary>
public sealed record ThemeOption(ThemeChoice Choice, string Label);

/// <summary>A module of its own on the „Einstellungen“ page: whether its tab is shown (the key is kept in <see cref="Core.Platform.AppSettings.HiddenModules"/>).</summary>
public sealed partial class ModuleSetting : ObservableObject
{
    private readonly Modules.ModuleTab _tab;
    private readonly Action _changed;

    internal ModuleSetting(Modules.ModuleTab tab, Action changed)
    {
        (_tab, _changed) = (tab, changed);
        Tr.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Label));
    }

    /// <summary>„Reiter „E-Rechnung“ zeigen“ in the current language.</summary>
    public string Label => Tr.T("Reiter „{0}“ zeigen", _tab.Header());

    /// <summary>Whether the tab is shown.</summary>
    public bool Shown
    {
        get => !(UserSettings.Current.HiddenModules ?? []).Contains(_tab.Key);
        set
        {
            if (value == Shown) return;
            var hidden = (UserSettings.Current.HiddenModules ?? []).Where(k => k != _tab.Key).ToList();
            if (!value) hidden.Add(_tab.Key);
            UserSettings.Update(s => s with { HiddenModules = hidden });
            OnPropertyChanged();
            _changed();
        }
    }
}

/// <summary>The „Einstellungen“ page in the File area: language, appearance, translation template.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>The modules of their own, each with whether its tab is shown (KONZEPT.md, section 5.24, „Aufbau und Entfernen“).</summary>
    public IReadOnlyList<ModuleSetting> Modules => _modules ??= [.. PdfKnead.Modules.UiModules.Tabs.Select(t => new ModuleSetting(t, () => ModulesChanged?.Invoke()))];

    private IReadOnlyList<ModuleSetting>? _modules;

    /// <summary>Whether there is any module to show or hide.</summary>
    public bool HasModules => Modules.Count > 0;

    /// <summary>A module tab was shown or hidden.</summary>
    public event Action? ModulesChanged;

    /// <summary>„Berechtigungen anzeigen“ (set by the main view model).</summary>
    public Func<Task>? ShowPermissions { get; set; }

    /// <summary>„Funktionsdatei einspielen …“ (set by the main view model).</summary>
    public Func<Task>? ImportPolicy { get; set; }

    /// <summary>One line about the function file of the organization (or that there is none).</summary>
    public string PolicySummary => MainViewModel.PolicySummary();

    [RelayCommand]
    private Task ShowPermissionsNow() => ShowPermissions?.Invoke() ?? Task.CompletedTask;

    [RelayCommand]
    private Task ImportPolicyNow() => ImportPolicy?.Invoke() ?? Task.CompletedTask;

    public static readonly IReadOnlyList<ThemeOption> ThemeOptions =
    [
        new(ThemeChoice.System, "Wie das System"),
        new(ThemeChoice.Light, "Hell"),
        new(ThemeChoice.Dark, "Dunkel"),
    ];

    public SettingsViewModel()
    {
        ReloadLanguages();
        ReloadCodeSets();
        _selectedTheme = ThemeOptions.First(o => o.Choice == UserSettings.Current.Theme);
    }

    public ObservableCollection<Language> Languages { get; } = [];

    public IReadOnlyList<ThemeOption> Themes => ThemeOptions;

    /// <summary>Folder into which finished translations are placed.</summary>
    public string LanguageFolder => Core.Localization.Languages.Folder;

    /// <summary>This device's own code (computer part and user part), as shown under signatures.</summary>
    public string DeviceCode => Core.Platform.DeviceCode.Current.ToString();

    public string VersionText => $"PDF-Knead {AppVersion.Display}";

    [ObservableProperty] public partial string LanguageProblems { get; set; } = "";

    [ObservableProperty] public partial string TemplateMessage { get; set; } = "";

    public Language? SelectedLanguage
    {
        get => Languages.FirstOrDefault(l => l.Code == Tr.Current.Code);
        set
        {
            if (value is null || value.Code == Tr.Current.Code) return;
            Tr.Use(value);
            UserSettings.Update(s => s with { Language = value.Code });
            OnPropertyChanged();
        }
    }

    /// <summary>„Automatisch nach Updates suchen“ (KONZEPT §5.26; on at first, design decision).</summary>
    public bool UpdateCheck
    {
        get => UserSettings.Current.UpdateCheck && !UpdateLocked;
        set
        {
            if (UpdateLocked || UserSettings.Current.UpdateCheck == value) return;
            UserSettings.Update(s => s with { UpdateCheck = value });
            OnPropertyChanged();
        }
    }

    /// <summary>Whether the IT switched updates off on this computer (the switch is then locked).</summary>
    public bool UpdateLocked => Services.Updates.OffByPolicy;

    /// <summary>The updates of this run, for „Jetzt nach Updates suchen“ and its message.</summary>
    public Services.Updates Updates => Services.Updates.Instance;

    /// <summary>„Jetzt nach Updates suchen“.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public Task CheckForUpdatesAsync() => Updates.CheckAsync(onRequest: true);

    /// <summary>„Dateien öffnen“: each file in a window of its own (the default, as before).</summary>
    public bool OpenInOwnWindow
    {
        get => !UserSettings.Current.OpenInRunningWindow;
        set
        {
            if (value) SetOpenInRunningWindow(false);
        }
    }

    /// <summary>„Dateien öffnen“: in the window used last, as a new tab (<see cref="Core.Platform.FileHandoff"/>).</summary>
    public bool OpenInRunningWindow
    {
        get => UserSettings.Current.OpenInRunningWindow;
        set
        {
            if (value) SetOpenInRunningWindow(true);
        }
    }

    private void SetOpenInRunningWindow(bool on)
    {
        if (UserSettings.Current.OpenInRunningWindow == on) return;
        UserSettings.Update(s => s with { OpenInRunningWindow = on });
        OnPropertyChanged(nameof(OpenInOwnWindow));
        OnPropertyChanged(nameof(OpenInRunningWindow));
    }

    private ThemeOption _selectedTheme;

    public ThemeOption SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (value == _selectedTheme) return;
            _selectedTheme = value;
            UserSettings.Update(s => s with { Theme = value.Choice });
            UserSettings.ApplyTheme();
            OnPropertyChanged();
        }
    }

    /// <summary>Rereads the languages folder, e.g. after a translation has been placed there.</summary>
    public void ReloadLanguages()
    {
        var all = Core.Localization.Languages.All(out var problems);
        Languages.Clear();
        foreach (var l in all) Languages.Add(l);
        LanguageProblems = string.Join("\n", problems);
        OnPropertyChanged(nameof(SelectedLanguage));
    }

    /// <summary>Writes the translation template; the outcome is reported in <see cref="TemplateMessage"/>.</summary>
    /// <param name="fromGerman">true = texts prefilled in German, false = in English.</param>
    /// <param name="file">Target file.</param>
    public void ExportTemplate(bool fromGerman, string file)
    {
        try
        {
            var path = Core.Localization.Languages.WriteTemplate(fromGerman, file);
            TemplateMessage = Tr.T("Vorlage gespeichert: {0}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TemplateMessage = Tr.T("Vorlage nicht gespeichert: {0}", ex.Message);
        }
    }

    // ------------------------------------------------------------ Redaction codes (wish of 4.11.2026)

    /// <summary>The code sets: built in, replaced or added by the user's own files (<see cref="Core.Pdf.Editing.RedactionCodes"/>).</summary>
    public ObservableCollection<Core.Pdf.Editing.RedactionCodeSet> CodeSets { get; } = [];

    /// <summary>Folder of the own code files.</summary>
    public string CodesFolder => Core.Pdf.Editing.RedactionCodes.Folder;

    [ObservableProperty] public partial string CodesMessage { get; set; } = "";

    /// <summary>The set offered first when redacting (remembered; the block „Schwärzungsgrund“ starts with it).</summary>
    public Core.Pdf.Editing.RedactionCodeSet? SelectedCodeSet
    {
        get
        {
            var id = UserSettings.Current.RedactionCodeSet ?? Core.Pdf.Editing.RedactionCodes.DefaultFor(System.Globalization.CultureInfo.CurrentCulture.Name);
            return CodeSets.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? CodeSets.FirstOrDefault();
        }
        set
        {
            if (value is null) return;
            UserSettings.Update(s => s with { RedactionCodeSet = value.Id });
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRemoveOwnCodes));
        }
    }

    /// <summary>Whether the chosen set is an own file that replaces a built-in one (it can be removed, the built-in one applies again).</summary>
    public bool CanRemoveOwnCodes => SelectedCodeSet is { Own: true } set && Core.Pdf.Editing.RedactionCodes.BuiltInIds.Contains(set.Id);

    /// <summary>Rereads the code files (after editing one outside).</summary>
    public void ReloadCodeSets()
    {
        CodeSets.Clear();
        foreach (var set in Core.Pdf.Editing.RedactionCodes.Load()) CodeSets.Add(set);
        OnPropertyChanged(nameof(SelectedCodeSet));
        OnPropertyChanged(nameof(CanRemoveOwnCodes));
    }

    /// <summary>
    /// The file of the chosen set to be edited: a built-in one is copied into the own folder first (it then replaces the built-in one).
    /// </summary>
    /// <returns>The path; null if it could not be written (reported in <see cref="CodesMessage"/>).</returns>
    public string? CodesFileToEdit()
    {
        if (SelectedCodeSet is not { } set) return null;
        try
        {
            var path = set.Own ? Path.Combine(CodesFolder, set.Id + ".txt") : Core.Pdf.Editing.RedactionCodes.CopyForEditing(set.Id);
            CodesMessage = Tr.T("Zum Bearbeiten: {0} — nach dem Speichern „Neu laden“.", path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CodesMessage = Tr.T("Die Code-Datei ließ sich nicht anlegen: {0}", ex.Message);
            return null;
        }
    }

    /// <summary>Adds a code file (a text file as the built-in ones, e.g. from a colleague) to the own folder; one of the same name is replaced.</summary>
    public void ImportCodes(string file)
    {
        try
        {
            if (new FileInfo(file).Length > 1 << 20) throw new IOException(Tr.T("Die Datei ist größer als 1 MB."));
            var set = Core.Pdf.Editing.RedactionCodes.Parse(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file));
            if (set.Codes.Count == 0) throw new IOException(Tr.T("Sie enthält keinen Code."));
            Directory.CreateDirectory(CodesFolder);
            File.Copy(file, Path.Combine(CodesFolder, Path.GetFileNameWithoutExtension(file) + ".txt"), overwrite: true);
            ReloadCodeSets();
            CodesMessage = Tr.T("„{0}“ übernommen ({1} Codes).", set.Country, set.Codes.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CodesMessage = Tr.T("Die Code-Datei ließ sich nicht übernehmen: {0}", ex.Message);
        }
    }

    /// <summary>Saves the chosen set as a file (to pass it on).</summary>
    public void ExportCodes(string file)
    {
        if (SelectedCodeSet is not { } set) return;
        try
        {
            var own = Path.Combine(CodesFolder, set.Id + ".txt");
            var text = set.Own && File.Exists(own) ? File.ReadAllText(own) : Core.Pdf.Editing.RedactionCodes.BuiltInText(set.Id) ?? "";
            var bytes = new System.Text.UTF8Encoding(false).GetBytes(text);
            Core.Pdf.Writing.PdfFileSaver.SaveFile(file, output => output.Write(bytes));
            CodesMessage = Tr.T("Gespeichert: {0}", file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CodesMessage = Tr.T("Die Code-Datei ließ sich nicht speichern: {0}", ex.Message);
        }
    }

    /// <summary>Puts the own file of a built-in set aside (renamed to .bak, nothing lost): the built-in codes apply again.</summary>
    public void RemoveOwnCodes()
    {
        if (!CanRemoveOwnCodes || SelectedCodeSet is not { } set) return;
        try
        {
            var own = Path.Combine(CodesFolder, set.Id + ".txt");
            File.Move(own, own + ".bak", overwrite: true);
            ReloadCodeSets();
            CodesMessage = Tr.T("Eigene Fassung beiseitegelegt ({0}) — es gelten wieder die mitgelieferten Codes für „{1}“.", Path.GetFileName(own) + ".bak", SelectedCodeSet?.Country ?? set.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CodesMessage = Tr.T("Die Code-Datei ließ sich nicht entfernen: {0}", ex.Message);
        }
    }
}
