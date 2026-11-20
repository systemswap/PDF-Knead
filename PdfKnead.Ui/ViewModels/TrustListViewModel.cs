using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf.Signatures.TrustLists;
using PdfKnead.Core.Platform;

namespace PdfKnead.ViewModels;

/// <summary>
/// The window „EU-Vertrauensliste“ (KONZEPT.md, section 5.17): whether the EU trusted lists are loaded and of which date, loading them — only
/// on a click, from the addresses the EU list of lists names, each list's signature checked — and removing them.
/// </summary>
/// <param name="folder">Where the lists are kept; null = the user's folder (tests give their own).</param>
public sealed partial class TrustListViewModel(string? folder = null) : ObservableObject
{
    private CancellationTokenSource? _cancel;

    /// <summary>Fetches the lists (set by the tests: a source that needs no network).</summary>
    internal ITrustListSource Source { get; set; } = new HttpTrustListSource();

    /// <summary>The trust anchors (set by the tests); null = the pinned ones.</summary>
    internal IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2>? Anchors { get; set; }

    /// <summary>Address of the list of lists (set by the tests); null = the Commission's.</summary>
    internal Uri? Lotl { get; set; }

    /// <summary>Whether the lists were loaded or removed in this window (the open documents are checked again).</summary>
    public bool Changed { get; private set; }

    /// <summary>The lists now kept; null if none.</summary>
    public TrustListSet? Lists => TrustListStore.Current(folder, Anchors);

    /// <summary>Whether lists are kept.</summary>
    public bool HasLists => Lists is not null;

    /// <summary>What is kept: date of the list, next issue, how many countries, which are missing.</summary>
    public string State => Lists is not { } set ? Tr.T("Nicht geladen. Ohne die Liste zeigt die Prüfung nur, was das Zertifikat über sich selbst sagt.")
        : Tr.T("Liste vom {0}, nächste Ausgabe spätestens {1}; {2} Länder geprüft.", Date(set.Lotl.Issued), Date(set.Lotl.NextUpdate), set.Lists.Count)
          + (set.Failures.Count == 0 ? "" : " " + Tr.T("Nicht verfügbar: {0}.", string.Join(", ", set.Failures.Select(f => $"{f.Key} ({f.Value})"))))
          + (set.Lotl.NextUpdate is { } next && next < DateTimeOffset.UtcNow ? " " + Tr.T("Die Liste ist veraltet — bitte neu laden.") : "");

    private static string Date(DateTimeOffset? date) => date?.LocalDateTime.ToString("d") ?? "–";

    /// <summary>Whether loading runs.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand), nameof(RemoveCommand), nameof(CancelCommand))]
    public partial bool Busy { get; private set; }

    /// <summary>What is happening, or how loading ended.</summary>
    [ObservableProperty] public partial string? Message { get; private set; }

    /// <summary>Whether the message is an error.</summary>
    [ObservableProperty] public partial bool IsError { get; private set; }

    private bool Idle => !Busy;

    /// <summary>„Laden“: fetches and checks all lists; what was kept stays if the list of lists fails.</summary>
    [RelayCommand(CanExecute = nameof(Idle))]
    private async Task Load()
    {
        Busy = true;
        IsError = false;
        _cancel = new CancellationTokenSource();
        var progress = new Progress<string>(territory => Message = Tr.T("Lade und prüfe {0} …", territory));
        try
        {
            var cancel = _cancel.Token;
            var set = await Task.Run(() => TrustListStore.Download(Source, folder, progress, cancel, Anchors, Lotl), cancel);
            Changed = true;
            Message = set.Failures.Count == 0 ? Tr.T("Geladen und geprüft.") : Tr.T("Geladen und geprüft; einige Länder fehlen (siehe oben).");
        }
        catch (OperationCanceledException)
        {
            Message = Tr.T("Abgebrochen — die bisherige Liste bleibt.");
        }
        catch (Exception e) when (e is TrustListException or IOException or UnauthorizedAccessException)
        {
            IsError = true;
            Message = Tr.T("Nicht geladen: {0}", e.Message);
        }
        finally
        {
            Busy = false;
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(HasLists));
        }
    }

    /// <summary>Stops loading.</summary>
    [RelayCommand(CanExecute = nameof(Busy))]
    private void Cancel() => _cancel?.Cancel();

    /// <summary>„Entfernen“: deletes the kept lists.</summary>
    [RelayCommand(CanExecute = nameof(Idle))]
    private void Remove()
    {
        try
        {
            TrustListStore.Remove(folder);
            Changed = true;
            IsError = false;
            Message = Tr.T("Entfernt.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            IsError = true;
            Message = e.Message;
        }
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(HasLists));
    }
}
