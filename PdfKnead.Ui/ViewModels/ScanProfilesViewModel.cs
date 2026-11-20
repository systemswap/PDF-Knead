using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Scanning;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>One scan profile in the list of the file area, with its box to tick.</summary>
public sealed partial class ScanProfileRow(ScanProfile profile) : ObservableObject
{
    /// <summary>The profile.</summary>
    public ScanProfile Profile { get; } = profile;

    /// <summary>Its name.</summary>
    public string Name => Profile.Name;

    /// <summary>What it sets, in one line: „Scanner · Einzug · 300 dpi · Graustufen · A4 · beidseitig“.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Profile.DeviceName is { Length: > 0 } device) parts.Add(device);
            if (Profile.Source is { Length: > 0 } source) parts.Add(ScanSource.FromName(source).DisplayName);
            parts.Add($"{Profile.Dpi} dpi");
            parts.Add(ColorModeChoice.For(Profile.Mode).DisplayName);
            parts.Add(ScanFormats.All.FirstOrDefault(f => f.Key == Profile.Format)?.DisplayName ?? Profile.Format);
            if (Profile.ManualDuplex) parts.Add(Tr.T("manueller Duplex"));
            if (Profile.SkipBlank) parts.Add(Tr.T("ohne leere Seiten"));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Whether it is ticked for exporting or deleting.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>
/// The page „Scanprofile“ of the file area (KONZEPT §5.25): the saved scan profiles, exporting the ticked or all into one file, importing
/// and deleting. Imported profiles never replace the user's own: one under a name already there gets a free name.
/// </summary>
public sealed partial class ScanProfilesViewModel : ObservableObject
{
    private readonly Func<ScanProfileStore> _store;

    /// <param name="store">The store of the profiles (read each time: tests and the settings may move it).</param>
    public ScanProfilesViewModel(Func<ScanProfileStore>? store = null) =>
        _store = store ?? (() => ScanProfileStore.Beside(UserSettings.File));

    /// <summary>Lets the user choose where to export (set by the view): takes title and suggested name; returns the path, or null.</summary>
    public Func<string, string, Task<string?>>? PickExportPath { get; set; }

    /// <summary>Lets the user choose a file to import (set by the view): takes the title; returns the path, or null.</summary>
    public Func<string, Task<string?>>? PickImportPath { get; set; }

    /// <summary>Asks a question (set by the view): heading, message, answer; true for the answer.</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>The profiles.</summary>
    public ObservableCollection<ScanProfileRow> Rows { get; } = [];

    /// <summary>Whether there are profiles.</summary>
    public bool HasProfiles => Rows.Count > 0;

    /// <summary>What was done last, or why it failed.</summary>
    [ObservableProperty]
    public partial string? Info { get; set; }

    /// <summary>Whether <see cref="Info"/> reports an error.</summary>
    [ObservableProperty]
    public partial bool InfoIsError { get; set; }

    /// <summary>Reads the profiles again (when the page is shown).</summary>
    public void Reload()
    {
        var ticked = Rows.Where(r => r.IsChecked).Select(r => r.Name).ToHashSet();
        Rows.Clear();
        foreach (var profile in _store().Load()) Rows.Add(new ScanProfileRow(profile) { IsChecked = ticked.Contains(profile.Name) });
        OnPropertyChanged(nameof(HasProfiles));
    }

    /// <summary>Exports the ticked profiles (none ticked: all) into one file.</summary>
    [RelayCommand]
    private async Task Export()
    {
        var profiles = Rows.Where(r => r.IsChecked).Select(r => r.Profile).ToList();
        if (profiles.Count == 0) profiles = [.. Rows.Select(r => r.Profile)];
        if (profiles.Count == 0 || PickExportPath is null) return;
        var name = profiles.Count == 1 ? $"{FileName(profiles[0].Name)}.json" : Tr.T("Scanprofile") + ".json";
        if (await PickExportPath(Tr.T("Scanprofile exportieren"), name) is not { } path) return;
        try
        {
            var temp = path + ".tmp";
            await using (var stream = File.Create(temp)) ScanProfileStore.Export(profiles, stream);
            File.Move(temp, path, overwrite: true);
            Tell(profiles.Count == 1 ? Tr.T("1 Scanprofil exportiert: {0}", Path.GetFileName(path))
                : Tr.T("{0} Scanprofile exportiert: {1}", profiles.Count, Path.GetFileName(path)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Tell(Tr.T("Die Scanprofile ließen sich nicht exportieren: {0}", e.Message), error: true);
        }
    }

    /// <summary>Imports the profiles of a file; one under a name already there gets a free name, the same profile again is left out.</summary>
    [RelayCommand]
    private async Task Import()
    {
        if (PickImportPath is null || await PickImportPath(Tr.T("Scanprofile importieren")) is not { } path) return;
        try
        {
            IReadOnlyList<ScanProfile> imported;
            await using (var stream = File.OpenRead(path)) imported = ScanProfileStore.Import(stream);
            var store = _store();
            var existing = store.Load();
            if (existing.Count + imported.Count > ScanProfileStore.MaxProfiles)
            {
                Tell(Tr.T("Es sind höchstens {0} Scanprofile möglich.", ScanProfileStore.MaxProfiles), error: true);
                return;
            }
            var added = ScanProfileStore.Merge(existing, imported);
            foreach (var profile in added) store.Save(profile);
            Reload();
            var renamed = added.Count(p => !imported.Any(i => i.Name == p.Name));
            Tell(added.Count switch
            {
                0 => Tr.T("Keine neuen Scanprofile — alle gibt es schon."),
                1 => Tr.T("1 Scanprofil importiert."),
                _ => Tr.T("{0} Scanprofile importiert.", added.Count),
            } + (renamed > 0 ? " " + Tr.T("Profile mit schon vergebenem Namen haben eine Nummer bekommen.") : ""));
        }
        catch (InvalidDataException e)
        {
            Tell(e.Message, error: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Tell(Tr.T("Die Datei ließ sich nicht lesen: {0}", e.Message), error: true);
        }
    }

    /// <summary>Deletes the ticked profiles after asking.</summary>
    [RelayCommand]
    private async Task Delete()
    {
        var rows = Rows.Where(r => r.IsChecked).ToList();
        if (rows.Count == 0)
        {
            Tell(Tr.T("Zuerst die Profile ankreuzen, die gelöscht werden sollen."), error: true);
            return;
        }
        var question = rows.Count == 1 ? Tr.T("Scanprofil „{0}“ löschen?", rows[0].Name) : Tr.T("{0} Scanprofile löschen?", rows.Count);
        if (Confirm is not null && !await Confirm(question, Tr.T("Das lässt sich nicht rückgängig machen."), Tr.T("Löschen"))) return;
        try
        {
            var store = _store();
            foreach (var row in rows) store.Delete(row.Name);
            Reload();
            Tell(rows.Count == 1 ? Tr.T("Scanprofil „{0}“ gelöscht.", rows[0].Name) : Tr.T("{0} Scanprofile gelöscht.", rows.Count));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Tell(Tr.T("Die Scanprofile ließen sich nicht löschen: {0}", e.Message), error: true);
        }
    }

    private void Tell(string text, bool error = false)
    {
        Info = text;
        InfoIsError = error;
    }

    /// <summary>A profile name as a file name: characters a file system may refuse become „_“.</summary>
    private static string FileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var safe = new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]).Trim().Trim('.');
        return safe.Length > 0 ? safe : Tr.T("Scanprofil");
    }
}
