using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Scanning;
using PdfKnead.Core.Scanning.Escl;
using PdfKnead.Core.Platform;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>
/// The scan window with the own eSCL access (KONZEPT §5.25, S9d): the group „Netzwerk-Scanner (erweitert)“, the question about a scanner's
/// certificate, and the state of the feeder next to the source.
/// </summary>
public sealed partial class ScanViewModel
{
    /// <summary>How often the feeder state is read while the feeder is chosen and nothing is scanned.</summary>
    internal static TimeSpan FeederInterval { get; set; } = TimeSpan.FromSeconds(3);

    private CancellationTokenSource? _feederWatch;

    /// <summary>The group „Netzwerk-Scanner (erweitert)“; null when the scanner interface has no eSCL access (tests).</summary>
    public NetworkScannersViewModel? Network { get; private set; }

    /// <summary>Asks the user whether to trust a scanner's certificate (set by the view): device, question → trusted.</summary>
    public Func<ScanDevice, EsclCertificateException, Task<bool>>? ConfirmCertificate { get; set; }

    /// <summary>The state of the feeder for the user („Papier im Einzug“ …); null when unknown or not the feeder.</summary>
    [ObservableProperty]
    public partial string? FeederState { get; set; }

    /// <summary>Whether <see cref="FeederState"/> needs the user (no paper, jam, open cover).</summary>
    [ObservableProperty]
    public partial bool FeederProblem { get; set; }

    private void InitNetwork()
    {
        if (_backend is not CombinedScanBackend combined) return;
        Network = new NetworkScannersViewModel(() => NetworkScanning.Settings, NetworkScanning.Update, combined.Escl.Log);
        Network.Changed += searchNow =>
        {
            SearchNetworksCommand.NotifyCanExecuteChanged();
            // the list of devices is no longer complete: the next search is a real one — later, if the window is busy now (No. 926: it was
            // dropped silently while a device was being opened)
            Found.Remove(_backend.Name);
            if (!searchNow) return;
            if (CanSearch()) _ = RefreshDevicesAsync();
            else _searchPending = true;
        };
        Network.ChosenChanged += (scanner, chosen) =>
        {
            // at once in the list (design decision), not only after the search
            var id = EsclBackend.IdPrefix + scanner.Device;
            if (chosen && !Devices.Any(d => d.Has(id)) && combined.Escl.Remember(scanner) is { } device)
                Devices = [.. Devices, device];
            else if (!chosen && Devices.FirstOrDefault(d => d.Id == id && d.AlternateIds.Count == 0) is { } gone)
            {
                var selected = SelectedDevice == gone;
                Devices = [.. Devices.Where(d => d != gone)];
                if (selected) Select(Devices.FirstOrDefault());
            }
        };
    }

    /// <summary>
    /// „Netz durchsuchen“ (No. 926): searches the entered networks address by address and shows the scanners found in the group, where the
    /// green check takes one into the list of scanners. The list of scanners itself is not touched.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSearchNetworks))]
    public async Task SearchNetworksAsync()
    {
        if (_backend is not CombinedScanBackend combined || Network is not { } network) return;
        if (network.Networks.Count == 0)
        {
            network.SearchStatus = Tr.T("Erst ein Netz eintragen, zum Beispiel 192.168.178.0.");
            return;
        }
        IsBusy = true;
        network.IsSearchingNetworks = true;
        network.SearchStatus = Tr.T("Durchsucht die Netze …");
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (p.Total > 0) network.SearchStatus = Tr.T("Durchsucht die Netze … {0} %", p.Done * 100 / p.Total);
        });
        try
        {
            // the scanners at once, their host names afterwards: a list at 100 % without scanners looked like a hang
            var found = await combined.Escl.SearchNetworksAsync(progress, names: false);
            network.ShowSearchResult(found);
            if (found.Count > 0)
            {
                var status = network.SearchStatus;
                network.SearchStatus = Tr.T("Gerätenamen werden ermittelt …");
                network.ShowHostNames(await combined.Escl.HostNamesAsync(found));
                network.SearchStatus = status;
            }
        }
        catch (Exception e) when (e is ScanException or System.Net.Sockets.SocketException)
        {
            network.SearchStatus = e.Message;
        }
        catch (Exception e) when (Failure.Handled(e))
        {
            Unexpected(e);
        }
        finally
        {
            network.IsSearchingNetworks = false;
            IsBusy = false;
        }
    }

    private bool CanSearchNetworks() => CanSearch() && Network is { Direct: true };

    private bool _searchPending;

    // a search asked for while the window was busy runs as soon as it is free
    partial void OnIsBusyChanged(bool value)
    {
        if (value || !_searchPending || !CanSearch()) return;
        _searchPending = false;
        _ = RefreshDevicesAsync();
    }

    // ---------------------------------------------------------------- scanners removed by the user (No. 926)

    /// <summary>
    /// The „×“ of an entry in the list of scanners (decided on 18.11.2026): a scanner chosen from the network search is taken out of the
    /// choice (it stays out wherever the computer goes); every other way of the device — installed in the system, found by the multicast
    /// search, typed address — is hidden, since the system and the search report it again (a device installed in the system stays there after
    /// it is gone). The scanners stay in the list until then, also when they do not answer: switching networks does not remove them.
    /// </summary>
    [RelayCommand]
    public void RemoveDevice(ScanDevice? device)
    {
        if (device is null || IsScanning) return;
        var chosen = device.AllIds.Where(EsclBackend.Owns).Select(i => i[EsclBackend.IdPrefix.Length..])
            .Where(k => Network?.IsChosen(k) == true).ToList();
        var hide = device.AllIds.Where(i => !(EsclBackend.Owns(i) && chosen.Contains(i[EsclBackend.IdPrefix.Length..], StringComparer.OrdinalIgnoreCase))).ToList();
        if (hide.Count > 0)
            UserSettings.Update(s => s with { ScanHidden = [.. (s.ScanHidden ?? []).Where(i => !hide.Contains(i)).Concat(hide).TakeLast(AppSettings.MaxScanHidden)] });
        var rest = Devices.Where(d => d != device).ToList();
        if (Found.TryGetValue(_backend.Name, out var known)) Found[_backend.Name] = [.. known.Where(d => d != device)];
        var selected = SelectedDevice == device;
        Devices = rest;
        if (selected) Select(_wish.FindDevice(rest) ?? rest.FirstOrDefault());
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(ShowHiddenText));
        foreach (var key in chosen) Network!.Unchoose(key);   // searches the list again
    }

    /// <summary>How many scanners are hidden.</summary>
    public int HiddenCount => UserSettings.Current.ScanHiddenIds.Count;

    /// <summary>„Ausgeblendete wieder zeigen (2)“.</summary>
    public string ShowHiddenText => Tr.T("Ausgeblendete Scanner wieder zeigen ({0})", HiddenCount);

    /// <summary>Shows the hidden scanners again and searches the list anew.</summary>
    [RelayCommand]
    public async Task ShowHiddenDevicesAsync()
    {
        UserSettings.Update(s => s with { ScanHidden = null });
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(ShowHiddenText));
        Found.Remove(_backend.Name);
        if (CanSearch()) await RefreshDevicesAsync();
    }

    /// <summary>The devices without the hidden ones: an entry is left out only when every way to it is hidden — a new way (e.g. the device
    /// chosen from the network search later) brings it back.</summary>
    private static IReadOnlyList<ScanDevice> Visible(IReadOnlyList<ScanDevice> devices)
    {
        var hidden = UserSettings.Current.ScanHiddenIds;
        return hidden.Count == 0 ? devices : [.. devices.Where(d => !d.AllIds.All(hidden.Contains))];
    }

    /// <summary>
    /// The scanner presented a certificate the user has not confirmed: ask. Trusted — it is remembered and the device opened again; not
    /// trusted — the system's way for this device until the program ends, or, without one, the device cannot be used.
    /// </summary>
    /// <returns>Whether to open the device again.</returns>
    private async Task<bool> HandleCertificateAsync(ScanDevice device, EsclCertificateException question)
    {
        if (_backend is not CombinedScanBackend combined)
        {
            Report(question.Message, error: true);
            return false;
        }
        if (ConfirmCertificate is { } ask && await ask(device, question))
        {
            combined.Escl.Confirm(question.Key, question.Fingerprint);
            return true;
        }
        if (CombinedScanBackend.HasSystemWay(device))
        {
            combined.Avoid(device, "Zertifikat nicht bestätigt");
            return true;
        }
        Report(Tr.T("Ohne bestätigtes Zertifikat lässt sich dieser Scanner nicht verwenden."), error: true);
        return false;
    }

    /// <summary>Reads the feeder state now and then while the feeder is chosen, the device opened over eSCL and nothing is scanned.</summary>
    private void WatchFeeder(IScanSession session)
    {
        StopFeederWatch();
        if (CombinedScanBackend.EsclOf(session) is null) return;
        var watch = _feederWatch = new CancellationTokenSource();
        _ = WatchFeederAsync(session, watch.Token);
    }

    private void StopFeederWatch()
    {
        _feederWatch?.Cancel();
        _feederWatch = null;
        FeederState = null;
        FeederProblem = false;
    }

    private async Task WatchFeederAsync(IScanSession session, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            if (!IsScanning && SelectedSource?.Kind is ScanSourceKind.Feeder or ScanSourceKind.FeederDuplex
                && CombinedScanBackend.EsclOf(session) is { } escl)
            {
                try
                {
                    var status = await escl.StatusAsync(cancel);
                    if (cancel.IsCancellationRequested) return;
                    (FeederState, FeederProblem) = Describe(status.Feeder);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e) when (e is ScanException or ObjectDisposedException)
                {
                    (FeederState, FeederProblem) = (null, false);
                }
            }
            else (FeederState, FeederProblem) = (null, false);
            try
            {
                await Task.Delay(FeederInterval, cancel);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>The feeder state in words, and whether it needs the user.</summary>
    internal static (string? Text, bool Problem) Describe(EsclFeederState state) => state switch
    {
        EsclFeederState.Loaded => (Tr.T("Papier im Einzug"), false),
        EsclFeederState.Processing => (Tr.T("Einzug arbeitet"), false),
        EsclFeederState.Empty => (Tr.T("Kein Papier im Einzug"), true),
        EsclFeederState.Jam => (Tr.T("Papierstau im Einzug"), true),
        EsclFeederState.HatchOpen => (Tr.T("Klappe des Einzugs offen"), true),
        EsclFeederState.Mispick or EsclFeederState.MultipickDetected or EsclFeederState.DuplexPageTooShort or EsclFeederState.DuplexPageTooLong
            or EsclFeederState.InputTrayFailed or EsclFeederState.InputTrayOverloaded => (Tr.T("Problem mit dem Papier — bitte nachsehen"), true),
        _ => (null, false),
    };
}
