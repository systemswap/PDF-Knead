using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Scanning.Escl;

namespace PdfKnead.ViewModels;

/// <summary>A network in the list of the group „Netzwerk-Scanner (erweitert)“.</summary>
/// <param name="Range">The network.</param>
public sealed record NetworkRow(EsclNetworkRange Range)
{
    /// <summary>„192.168.178.1 – 254“.</summary>
    public string Display => Range.Display.Replace("–", " – ", StringComparison.Ordinal);
}

/// <summary>A scanner address in the list of the group „Netzwerk-Scanner (erweitert)“.</summary>
/// <param name="Text">The address as typed (normalised).</param>
public sealed record AddressRow(string Text);

/// <summary>
/// A scanner the search of the networks found (docs/FEHLERGEDAECHTNIS.md, No. 926): name and address, and the green check on the right —
/// checked, it comes into the list of scanners below.
/// </summary>
public sealed partial class FoundScannerRow : ObservableObject
{
    private readonly Action<FoundScannerRow> _toggled;
    private bool _chosen;

    internal FoundScannerRow(EsclChosenScanner scanner, bool chosen, Action<FoundScannerRow> toggled)
    {
        Scanner = scanner;
        _chosen = chosen;
        _toggled = toggled;
    }

    /// <summary>The entry as it is saved when checked.</summary>
    public EsclChosenScanner Scanner { get; internal set; }

    /// <summary>The make and model.</summary>
    public string Name => EsclXml.Clean(Scanner.Name) is { Length: > 0 } n ? n : Tr.T("Scanner");

    /// <summary>The address for the user, with the host name behind it (design decision): „10.242.66.21 · PRN-2OG-01.example.local“.</summary>
    public string Address => (EsclAddress.Parse(Scanner.Address)?.Host ?? "") + (EsclHostNames.Short(Scanner.HostName) is { } name ? " · " + name : "");

    /// <summary>The address with the full host name, for the tooltip.</summary>
    public string AddressFull => (EsclAddress.Parse(Scanner.Address)?.Host ?? "") + (EsclHostNames.Clean(Scanner.HostName) is { } name ? " · " + name : "");

    /// <summary>The green check: in the list of scanners below.</summary>
    public bool IsChosen
    {
        get => _chosen;
        set
        {
            if (!SetProperty(ref _chosen, value)) return;
            _toggled(this);
        }
    }

    /// <summary>What the user should know about the row, e.g. that the last search did not find it; null when nothing.</summary>
    [ObservableProperty]
    public partial string? Note { get; set; }

    /// <summary>For screen readers: name, address and whether it is in the list.</summary>
    public string CheckName => Tr.T("{0} ({1}) in die Scannerauswahl", Name, Address);

    internal void Update(EsclChosenScanner scanner)
    {
        Scanner = scanner;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(AddressFull));
        OnPropertyChanged(nameof(CheckName));
    }
}

/// <summary>
/// The group „Netzwerk-Scanner (erweitert)“ of the scan window (KONZEPT §5.25, S9d; layout decided by the user): the switch for the own
/// eSCL access, the networks searched address by address (an input with „+“ and a list below, each network a row with „×“), single scanner
/// addresses, and „Diagnose speichern …“. Saves every change at once in the computer's settings.
/// </summary>
public sealed partial class NetworkScannersViewModel : ObservableObject
{
    private readonly Func<EsclSettings> _load;
    private readonly Action<Func<EsclSettings, EsclSettings>> _save;
    private readonly EsclDiagnostics _log;

    /// <param name="load">Reads the settings of the eSCL access.</param>
    /// <param name="save">Changes and saves them.</param>
    /// <param name="log">The diagnosis log of the eSCL access.</param>
    public NetworkScannersViewModel(Func<EsclSettings> load, Action<Func<EsclSettings, EsclSettings>> save, EsclDiagnostics log)
    {
        _load = load;
        _save = save;
        _log = log;
        var settings = load();
        _direct = settings.Direct;
        foreach (var range in settings.Ranges) Networks.Add(new NetworkRow(range));
        foreach (var address in settings.Addresses ?? []) if (EsclAddress.Parse(address) is not null) Addresses.Add(new AddressRow(address));
        // the chosen scanners stand in the list before any search of the networks: there the user takes them out again
        foreach (var (scanner, _) in settings.ChosenScanners) Found.Add(new FoundScannerRow(scanner, true, Toggled));
    }

    /// <summary>„Netzwerk-Scanner direkt ansprechen“.</summary>
    public bool Direct
    {
        get => _direct;
        set
        {
            if (!SetProperty(ref _direct, value)) return;
            _save(s => s with { Direct = value });
            Changed?.Invoke(true);
        }
    }

    private bool _direct;

    /// <summary>The networks searched address by address.</summary>
    public ObservableCollection<NetworkRow> Networks { get; } = [];

    /// <summary>The single scanner addresses.</summary>
    public ObservableCollection<AddressRow> Addresses { get; } = [];

    /// <summary>The scanners the search of the networks found, and the chosen ones (green check: in the list of scanners).</summary>
    public ObservableCollection<FoundScannerRow> Found { get; } = [];

    /// <summary>What the search of the networks is doing or found („Durchsucht … 40 %“, „3 Scanner gefunden“); null before the first.</summary>
    [ObservableProperty]
    public partial string? SearchStatus { get; set; }

    /// <summary>Whether the networks are being searched.</summary>
    [ObservableProperty]
    public partial bool IsSearchingNetworks { get; set; }

    /// <summary>
    /// Shows the result of „Netz durchsuchen“: new scanners without the check; a chosen scanner found at another address is saved with the new
    /// one (an address given out anew by the network); a chosen one not found stays chosen with a note.
    /// </summary>
    /// <param name="found">The scanners found, with the host names of their addresses.</param>
    /// <param name="cancelled">Whether the search was stopped before its end.</param>
    public void ShowSearchResult(IReadOnlyList<EsclFoundScanner> found, bool cancelled = false)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moved = false;
        foreach (var (device, hostName) in found)
        {
            if (EsclChosenScanner.Of(device, hostName) is not { } scanner || !seen.Add(scanner.Device)) continue;
            var row = Found.FirstOrDefault(r => string.Equals(r.Scanner.Device, scanner.Device, StringComparison.OrdinalIgnoreCase));
            // the names come after the scanners (ShowHostNames): until then the name known at the same address stays
            if (scanner.HostName is null && row?.Scanner is { HostName: { } known } old && old.Address == scanner.Address)
                scanner = scanner with { HostName = known };
            if (row is null)
            {
                Found.Add(new FoundScannerRow(scanner, false, Toggled));
                continue;
            }
            row.Note = null;
            if (row.Scanner == scanner) continue;
            row.Update(scanner);
            if (row.IsChosen)
            {
                _save(s => s.WithChosen(scanner, true));
                moved = true;
            }
        }
        if (!cancelled)
            foreach (var row in Found.Where(r => !seen.Contains(r.Scanner.Device)).ToList())
            {
                if (row.IsChosen) row.Note = Tr.T("bei der letzten Suche nicht gefunden");
                else Found.Remove(row);
            }
        var sorted = Found.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Address, StringComparer.Ordinal).ToList();
        for (var i = 0; i < sorted.Count; i++)
            if (!ReferenceEquals(Found[i], sorted[i])) Found.Move(Found.IndexOf(sorted[i]), i);
        SearchStatus = (cancelled ? Tr.T("Abgebrochen.") + " " : "")
            + (seen.Count == 0 ? Tr.T("Kein Scanner gefunden.") : Tr.T("{0} Scanner gefunden — mit dem Haken kommen sie in die Scannerauswahl.", seen.Count));
        if (moved) Changed?.Invoke(true);
    }

    /// <summary>Whether a device (its UUID) is chosen.</summary>
    public bool IsChosen(string device) => _load().ChosenScanners.Any(c => string.Equals(c.Scanner.Device, device, StringComparison.OrdinalIgnoreCase));

    /// <summary>Takes a chosen scanner out (the „×“ in the list of scanners): the check goes off, and the list is searched again.</summary>
    public void Unchoose(string device)
    {
        if (Found.FirstOrDefault(r => r.IsChosen && string.Equals(r.Scanner.Device, device, StringComparison.OrdinalIgnoreCase)) is { } row)
        {
            row.IsChosen = false;   // saves, and raises Changed
            return;
        }
        if (_load().ChosenScanners.FirstOrDefault(c => string.Equals(c.Scanner.Device, device, StringComparison.OrdinalIgnoreCase)) is { Scanner: { } scanner })
        {
            _save(s => s.WithChosen(scanner, false));
            Changed?.Invoke(true);
        }
    }

    /// <summary>
    /// The host names after the search (they take some seconds more): each row gets its name, a chosen scanner is saved with it; the status
    /// says when the names are there.
    /// </summary>
    /// <param name="found">The scanners with their names.</param>
    public void ShowHostNames(IReadOnlyList<EsclFoundScanner> found)
    {
        foreach (var (device, hostName) in found)
        {
            if (hostName is null || EsclChosenScanner.Of(device, hostName) is not { } scanner) continue;
            if (Found.FirstOrDefault(r => string.Equals(r.Scanner.Device, scanner.Device, StringComparison.OrdinalIgnoreCase)) is not { } row
                || row.Scanner.Address != scanner.Address || row.Scanner.HostName == scanner.HostName)
                continue;
            row.Update(row.Scanner with { HostName = scanner.HostName });
            if (row.IsChosen) _save(s => s.WithChosen(row.Scanner, true));
        }
    }

    private void Toggled(FoundScannerRow row)
    {
        _save(s => s.WithChosen(row.Scanner, row.IsChosen));
        ChosenChanged?.Invoke(row.Scanner, row.IsChosen);
        if (!row.IsChosen && row.Note is not null) Found.Remove(row);   // not found and not wanted: nothing left to show
        Changed?.Invoke(true);
    }

    /// <summary>What is being typed into the network field.</summary>
    [ObservableProperty]
    public partial string NetworkInput { get; set; } = "";

    /// <summary>Why the text in the network field is not taken; null when it is fine or empty.</summary>
    [ObservableProperty]
    public partial string? NetworkError { get; set; }

    /// <summary>What is being typed into the address field.</summary>
    [ObservableProperty]
    public partial string AddressInput { get; set; } = "";

    /// <summary>Why the text in the address field is not taken.</summary>
    [ObservableProperty]
    public partial string? AddressError { get; set; }

    /// <summary>
    /// Raised when the switch, the networks or the addresses changed; true when the window should search at once (the switch), false when the
    /// next search („Netz durchsuchen“) is enough — a search of a network takes seconds and is started by the user.
    /// </summary>
    public event Action<bool>? Changed;

    /// <summary>Raised when a scanner got or lost the green check (before <see cref="Changed"/>): the window shows it in the list at once.</summary>
    public event Action<EsclChosenScanner, bool>? ChosenChanged;

    /// <summary>Asks where to save the diagnosis (set by the view): title, suggested name → path or null.</summary>
    public Func<string, string, Task<string?>>? PickSaveFile { get; set; }

    /// <summary>
    /// Takes what the network field holds: every network in it becomes a row (also several pasted at once, separated by commas, blanks or
    /// lines); what is not a network stays in the field with the reason below.
    /// </summary>
    [RelayCommand]
    public void AddNetwork()
    {
        var pieces = NetworkInput.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var refused = new List<string>();
        string? reason = null;
        var added = false;
        foreach (var piece in pieces)
        {
            if (EsclNetworkRange.Parse(piece, out var error) is { } range)
            {
                if (Networks.Any(n => n.Range == range)) continue;
                if (Networks.Count >= EsclSettings.MaxEntries)
                {
                    reason ??= Tr.T("Es lassen sich höchstens {0} Netze eintragen.", EsclSettings.MaxEntries);
                    refused.Add(piece);
                    continue;
                }
                Networks.Add(new NetworkRow(range));
                added = true;
            }
            else
            {
                refused.Add(piece);
                reason ??= error;
            }
        }
        NetworkInput = string.Join(", ", refused);
        NetworkError = reason;
        if (added) SaveNetworks();
    }

    /// <summary>Checks the network field when it is left: the reason below if something in it is not a network, nothing is taken yet.</summary>
    public void CheckNetworkInput()
    {
        NetworkError = null;
        foreach (var piece in NetworkInput.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            if (EsclNetworkRange.Parse(piece, out var error) is null)
            {
                NetworkError = error;
                return;
            }
    }

    /// <summary>Removes a network.</summary>
    [RelayCommand]
    public void RemoveNetwork(NetworkRow? row)
    {
        if (row is null || !Networks.Remove(row)) return;
        SaveNetworks();
    }

    private void SaveNetworks()
    {
        var networks = Networks.Select(n => n.Range.ToString()).ToList();
        _save(s => s with { Networks = networks });
        Changed?.Invoke(false);
    }

    /// <summary>Takes the address field: a host name or IP address, optionally with port or as http(s) address.</summary>
    [RelayCommand]
    public void AddAddress()
    {
        var text = AddressInput.Trim();
        if (text.Length == 0) return;
        if (EsclAddress.Parse(text) is null)
        {
            AddressError = Tr.T("„{0}“ ist keine Scanner-Adresse. Beispiele: 192.168.178.20, scanner.local, https://192.168.178.20", text);
            return;
        }
        AddressError = null;
        AddressInput = "";
        if (Addresses.Any(a => string.Equals(a.Text, text, StringComparison.OrdinalIgnoreCase))) return;
        if (Addresses.Count >= EsclSettings.MaxEntries)
        {
            AddressError = Tr.T("Es lassen sich höchstens {0} Adressen eintragen.", EsclSettings.MaxEntries);
            return;
        }
        Addresses.Add(new AddressRow(text));
        SaveAddresses();
    }

    /// <summary>Removes an address.</summary>
    [RelayCommand]
    public void RemoveAddress(AddressRow? row)
    {
        if (row is null || !Addresses.Remove(row)) return;
        SaveAddresses();
    }

    private void SaveAddresses()
    {
        var addresses = Addresses.Select(a => a.Text).ToList();
        _save(s => s with { Addresses = addresses });
        Changed?.Invoke(false);
    }

    /// <summary>Whether the diagnosis log has entries.</summary>
    public bool HasDiagnosis => _log.Entries.Count > 0;

    /// <summary>Saves the diagnosis log where the user chooses — it names devices and addresses in the own network, so only on request.</summary>
    [RelayCommand]
    public async Task SaveDiagnosisAsync()
    {
        if (PickSaveFile is null) return;
        // a command of a button: an exception here would reach the window's thread and end the program — the dialog of the system included
        try
        {
            if (await PickSaveFile(Tr.T("Diagnose speichern"), "PDF-Knead-Netzwerkscanner-Diagnose.txt") is not { } path) return;
            await File.WriteAllTextAsync(path, _log.Text());
        }
        catch (Exception e) when (Services.Failure.Handled(e))
        {
            AddressError = Tr.T("Die Diagnose ließ sich nicht speichern: {0}", Services.Failure.Text(e));
        }
    }

    /// <summary>The settings as saved now (for tests).</summary>
    internal EsclSettings Saved => _load();
}
