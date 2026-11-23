using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfKnead.Core.Platform;
using PdfKnead.Core.Updates;

namespace PdfKnead.Services;

/// <summary>Where the update stands, for the hint in the window and the settings.</summary>
public enum UpdateState
{
    /// <summary>Nothing to show.</summary>
    Idle,

    /// <summary>A check runs.</summary>
    Checking,

    /// <summary>A newer version can be loaded.</summary>
    Available,

    /// <summary>It is being loaded.</summary>
    Downloading,

    /// <summary>Loaded and checked: exchanged at the next start.</summary>
    Ready,

    /// <summary>A newer version exists, but the program folder cannot be written (e.g. under „Programme“): only the download page.</summary>
    ManualOnly,
}

/// <summary>
/// Updates in the window (KONZEPT §5.26): the automatic check at the start (on at first, at most once a day, off in the settings or by the
/// IT), the check on request, loading, unpacking into the waiting area and „Jetzt neu starten“. One per run.
/// </summary>
public sealed partial class Updates : ObservableObject
{
    /// <summary>The one of this run.</summary>
    public static Updates Instance { get; } = new();

    /// <summary>Whether the last check reached a signed version file.</summary>
    public bool LastCheckReached { get; private set; }

    private readonly UpdateClient _client;
    private readonly UpdateInstaller _installer;
    private readonly string? _publicKey;

    /// <param name="client">For tests: answers instead of the network.</param>
    /// <param name="installer">For tests: a waiting area of their own.</param>
    /// <param name="publicKey">For tests: their own release key; the program's if null.</param>
    internal Updates(UpdateClient? client = null, UpdateInstaller? installer = null, string? publicKey = null)
    {
        _client = client ?? new UpdateClient();
        _installer = installer ?? new UpdateInstaller(UpdateStart.StateFolder);
        _publicKey = publicKey;
    }

    /// <summary>The state for the hint.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsHint), nameof(CanDownload), nameof(CanRestart), nameof(ShowsDownloadPage))]
    public partial UpdateState State { get; set; }

    /// <summary>The newer version found, or null.</summary>
    [ObservableProperty]
    public partial UpdateManifest? Found { get; set; }

    /// <summary>The text of the hint and of the settings („PDF-Knead 1.3.0 ist verfügbar.“ …); null when nothing to say.</summary>
    [ObservableProperty]
    public partial string? Message { get; set; }

    /// <summary>How far the loading is, 0 to 1.</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    /// <summary>The changes of the version found, cleaned for the window.</summary>
    public string Notes => Found is { } f ? UpdateManifest.CleanNotes(f.Notes) : "";

    /// <summary>Whether the hint in the window shows.</summary>
    public bool ShowsHint => State is UpdateState.Available or UpdateState.Downloading or UpdateState.Ready or UpdateState.ManualOnly && !_dismissed;

    /// <summary>Whether „Laden“ is offered.</summary>
    public bool CanDownload => State == UpdateState.Available;

    /// <summary>Whether „Jetzt neu starten“ is offered.</summary>
    public bool CanRestart => State == UpdateState.Ready;

    /// <summary>Whether „Download-Seite öffnen“ is offered (the program cannot replace its own files).</summary>
    public bool ShowsDownloadPage => State == UpdateState.ManualOnly;

    private bool _dismissed;

    /// <summary>
    /// Whether the IT switched the check off: the environment variable PDFKNEAD_UPDATES=aus, or a file „updates-aus“ next to the program.
    /// Then the switch in the settings is locked and nothing is checked, also not on request.
    /// </summary>
    public static bool OffByPolicy =>
        string.Equals(Environment.GetEnvironmentVariable("PDFKNEAD_UPDATES"), "aus", StringComparison.OrdinalIgnoreCase)
        || File.Exists(Path.Combine(UpdateStart.ProgramLocation().Folder, "updates-aus"));

    /// <summary>
    /// The automatic check at the start: only if switched on (off at first; asked once at the first start), not off by the IT, not yet today;
    /// after a short pause in the background.
    /// </summary>
    public async Task CheckAtStartAsync(TimeSpan? delay = null)
    {
        var settings = UserSettings.Current;
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!settings.UpdateCheck || OffByPolicy || settings.UpdateLastCheck == today) return;
        await Task.Delay(delay ?? TimeSpan.FromSeconds(10));
        UserSettings.Update(s => s with { UpdateLastCheck = today });
        await CheckAsync(onRequest: false);
    }

    /// <summary>
    /// Checks for a newer version. On request („Nach Updates suchen“) every outcome is said; at the start only a newer version shows.
    /// </summary>
    public async Task CheckAsync(bool onRequest = true)
    {
        if (State is UpdateState.Checking or UpdateState.Downloading) return;
        if (OffByPolicy)
        {
            if (onRequest) Message = Tr.T("Die Suche nach Updates ist auf diesem Rechner abgeschaltet.");
            return;
        }
        if (UpdateVersion.Parse(AppVersion.Raw) is not { } current) return;
        var before = State;
        State = UpdateState.Checking;
        if (onRequest) Message = Tr.T("Suche nach Updates …");
        var result = await _client.CheckAsync(current, UpdateManifest.ThisPlatform, _publicKey);
        LastCheckReached = result.Outcome is UpdateCheckOutcome.Current or UpdateCheckOutcome.Available;
        Found = result.Manifest is { } m && result.Outcome == UpdateCheckOutcome.Available ? m : null;
        OnPropertyChanged(nameof(Notes));
        _dismissed = false;
        switch (result.Outcome)
        {
            case UpdateCheckOutcome.Available:
                if (_installer.Waiting() is { } waiting && waiting.Version.CompareTo(result.Manifest!.Version) == 0)
                {
                    State = UpdateState.Ready;
                    Message = Tr.T("PDF-Knead {0} wird beim nächsten Start installiert.", waiting.Version);
                    break;
                }
                var writable = UpdateInstaller.CanWrite(UpdateStart.ProgramLocation().Folder);
                State = writable ? UpdateState.Available : UpdateState.ManualOnly;
                Message = writable ? Tr.T("PDF-Knead {0} ist verfügbar.", result.Manifest!.Version)
                    : Tr.T("PDF-Knead {0} ist verfügbar. Der Programmordner lässt sich nicht beschreiben — bitte von der Download-Seite installieren.", result.Manifest!.Version);
                break;
            case UpdateCheckOutcome.Current:
                State = before == UpdateState.Ready ? UpdateState.Ready : UpdateState.Idle;
                if (onRequest) Message = Tr.T("PDF-Knead ist auf dem neuesten Stand ({0}).", AppVersion.Display);
                break;
            case UpdateCheckOutcome.NoKey:
                State = UpdateState.Idle;
                if (onRequest) Message = Tr.T("Diese Fassung nimmt keine Updates an (kein Schlüssel für Updates hinterlegt).");
                break;
            case UpdateCheckOutcome.Invalid:
                State = UpdateState.Idle;
                if (onRequest) Message = Tr.T("Die Update-Quelle lieferte keine gültig signierte Versionsdatei — nichts wurde geladen.");
                break;
            default:
                State = UpdateState.Idle;
                if (onRequest) Message = Tr.T("Keine Update-Quelle erreichbar — ohne Netz, oder es sind noch keine Updates veröffentlicht.");
                break;
        }
    }

    /// <summary>Loads the version found, checks it and unpacks it into the waiting area: exchanged at the next start.</summary>
    public async Task DownloadAsync()
    {
        if (State != UpdateState.Available || Found is not { } manifest || manifest.PackageFor(UpdateManifest.ThisPlatform) is not { } package) return;
        State = UpdateState.Downloading;
        Message = Tr.T("Lade PDF-Knead {0} …", manifest.Version);
        Progress = 0;
        var folder = Path.Combine(_installer.Folder, "laden");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, package.File);
        var progress = new Progress<double>(p => Progress = p);
        var loaded = await _client.DownloadAsync(package, file, progress);
        IReadOnlyList<string>? files = null;
        if (loaded)
        {
            files = await Task.Run(() => _installer.UnpackPackage(file, manifest.Version));
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // removed with the next download
            }
        }
        if (files is null)
        {
            State = UpdateState.Available;
            Message = loaded ? Tr.T("Das Paket von PDF-Knead {0} ist beschädigt und wurde verworfen.", manifest.Version)
                : Tr.T("Laden fehlgeschlagen oder Prüfsumme falsch — nichts wurde geändert.");
            return;
        }
        _installer.MarkReady(manifest.Version, files);
        State = UpdateState.Ready;
        Message = Tr.T("PDF-Knead {0} wird beim nächsten Start installiert.", manifest.Version);
    }

    /// <summary>Hands over to the new version and ends the program (the caller closes the windows when this returns true).</summary>
    public bool RestartNow() => State == UpdateState.Ready && UpdateStart.HandOver(_installer);

    /// <summary>Hides the hint until the next check.</summary>
    public void Later()
    {
        _dismissed = true;
        OnPropertyChanged(nameof(ShowsHint));
    }

    /// <summary>The main window is open: an exchanged version counts as working (KONZEPT §5.26, restore otherwise).</summary>
    public void ConfirmStart() => _installer.Confirm();
}
