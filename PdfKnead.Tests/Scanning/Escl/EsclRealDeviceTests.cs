using PdfKnead.Core.Scanning;
using PdfKnead.Core.Scanning.Escl;
using Xunit.Abstractions;

namespace PdfKnead.Tests.Scanning.Escl;

/// <summary>
/// The own eSCL access at a real device, together with the system's way as the program uses it (KONZEPT.md §5.25, S9; on Windows WIA in the
/// helper process, on Linux SANE). Only with PDFKNEAD_ESCL_GERAET set (a part of the device's name) and PDFKNEAD_ESCL_SCANNEN naming the run:
/// „suche“ (only searching), „flachbett“ (a sheet on the glass), „einzug“ (several sheets in the feeder), „leer“ (an empty feeder),
/// „abbrechen“ (a sheet on the glass; cancelled while scanning, then at once a new scan), „rueckfall“ (a sheet on the glass; the certificate
/// declined and the switch off — the system's way scans), „rueckfall-einzug“ (several sheets in the feeder; the switch off — the system's way
/// scans the stack). Certificates are confirmed in memory only, never in the user's settings. Pages are
/// measured, never saved. They use the one scanner and run alone (PRUEFUNGEN, „eSCL unter Windows am Gerät“).
/// </summary>
[Collection(nameof(WiaDeviceCollection))]
public sealed class EsclRealDeviceTests(ITestOutputHelper output)
{
    private static readonly string? Wanted = Environment.GetEnvironmentVariable("PDFKNEAD_ESCL_GERAET");
    private static readonly string? Run = Environment.GetEnvironmentVariable("PDFKNEAD_ESCL_SCANNEN");

    private static bool Active(string run) => Wanted is { Length: > 0 } && Run == run;

    private (CombinedScanBackend Backend, EsclMemoryPins Pins) Backend(EsclSettings? settings = null)
    {
        var pins = new EsclMemoryPins();
        return (new CombinedScanBackend(ScanBackends.ForThisSystem(), new EsclBackend(() => settings ?? new EsclSettings(), pins)), pins);
    }

    private async Task<ScanDevice> Find(CombinedScanBackend backend)
    {
        var devices = await backend.FindDevicesAsync();
        foreach (var d in devices) output.WriteLine($"{d.DisplayName} | {string.Join(" ; ", d.AllIds)}");
        return devices.First(d => d.DisplayName.Contains(Wanted!, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Opens the device as the program does when the user trusts the certificate („Vertrauen“).</summary>
    private async Task<IScanSession> OpenTrusting(CombinedScanBackend backend, ScanDevice device)
    {
        try
        {
            return await backend.OpenAsync(device);
        }
        catch (EsclCertificateException e)
        {
            output.WriteLine($"Zertifikat: {EsclTrust.Display(e.Fingerprint)} — bestätigt (nur im Speicher)");
            backend.Escl.Confirm(e.Key, e.Fingerprint);
            return await backend.OpenAsync(device);
        }
    }

    private async Task<List<ScannedImage>> ScanWith(IScanSession session, ScanSourceKind kind, int dpi, ScanColorMode mode, List<ScanProgress>? seen = null,
        CancellationToken cancel = default)
    {
        var source = session.Capabilities.Sources.First(s => s.Kind == kind);
        await session.SelectSourceAsync(source.Name, cancel);
        var pages = new List<ScannedImage>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var progress = new Progress<ScanProgress>(p => seen?.Add(p));
        await foreach (var page in session.ScanAsync(new ScanSettings(source.Name, dpi, mode, 210, 297), progress, cancel))
        {
            pages.Add(page);
            output.WriteLine($"  {kind} {mode} {dpi} dpi: Seite {pages.Count} {page.Width} × {page.Height} × {page.Channels}, {watch.ElapsedMilliseconds} ms");
        }
        return pages;
    }

    private static void AssertA4(ScannedImage page, int dpi, int channels)
    {
        Assert.Equal((channels, dpi), (page.Channels, page.Dpi));
        var slack = Math.Max(2, dpi / 100);
        Assert.InRange(page.Width, (int)(210 / 25.4 * dpi) - slack, (int)(210 / 25.4 * dpi) + slack);
        Assert.InRange(page.Height, (int)(297 / 25.4 * dpi) - slack, (int)(297 / 25.4 * dpi) + slack);
    }

    private async Task Status(IScanSession session, string when)
    {
        if (CombinedScanBackend.EsclOf(session) is not { } escl) return;
        var status = await escl.StatusAsync();
        output.WriteLine($"Zustand {when}: {status.State}, Einzug {status.Feeder}, Gründe [{string.Join(", ", status.Reasons)}], Aufträge {status.Jobs.Count}");
    }

    [Fact]
    public async Task Search_OneEntryWithBothWays()
    {
        if (!Active("suche")) return;
        var (backend, _) = Backend();
        ScanDevice device;
        try
        {
            device = await Find(backend);
        }
        finally
        {
            foreach (var line in backend.Escl.Log.Entries) output.WriteLine("  " + line);
        }
        Assert.Contains(device.AllIds, EsclBackend.Owns);
        Assert.True(CombinedScanBackend.HasSystemWay(device), "das Gerät soll auch über den Weg des Systems da sein (Rückfall)");
    }

    [Fact]
    public async Task Flatbed_OverEscl_GrayAndColour_WithProgressAndStatus()
    {
        if (!Active("flachbett")) return;
        var (backend, _) = Backend();
        var device = await Find(backend);
        await using var session = await OpenTrusting(backend, device);
        Assert.NotNull(CombinedScanBackend.EsclOf(session));
        await Status(session, "vorher");
        var seen = new List<ScanProgress>();
        AssertA4(Assert.Single(await ScanWith(session, ScanSourceKind.Flatbed, 150, ScanColorMode.Gray, seen)), 150, 1);
        output.WriteLine($"  Fortschritt: {seen.Count} Meldungen, Phasen {string.Join(" → ", seen.Select(p => p.Phase).Distinct())}, höchster Anteil {seen.Select(p => p.Fraction).DefaultIfEmpty().Max():0.00}");
        Assert.NotEmpty(seen);
        AssertA4(Assert.Single(await ScanWith(session, ScanSourceKind.Flatbed, 300, ScanColorMode.Color)), 300, 3);
        await Status(session, "nachher");
        Assert.False(backend.IsDisturbed(device));
    }

    [Fact]
    public async Task Feeder_OverEscl_WholeStack_ThenEmpty()
    {
        if (!Active("einzug")) return;
        var (backend, _) = Backend();
        var device = await Find(backend);
        await using var session = await OpenTrusting(backend, device);
        Assert.NotNull(CombinedScanBackend.EsclOf(session));
        await Status(session, "vorher");
        var pages = await ScanWith(session, ScanSourceKind.Feeder, 150, ScanColorMode.Gray);
        Assert.True(pages.Count >= 2, $"{pages.Count} Seiten");
        Assert.All(pages, p => AssertA4(p, 150, 1));
        await Status(session, "nachher");
        Assert.False(backend.IsDisturbed(device));
    }

    [Fact]
    public async Task Feeder_Empty_ReportedAsNoPaper()
    {
        if (!Active("leer")) return;
        var (backend, _) = Backend();
        var device = await Find(backend);
        await using var session = await OpenTrusting(backend, device);
        await Status(session, "vorher");
        var e = await Assert.ThrowsAnyAsync<ScanException>(() => ScanWith(session, ScanSourceKind.Feeder, 150, ScanColorMode.Gray));
        output.WriteLine($"  Meldung: {e.Kind} — {e.Message}");
        Assert.Equal(ScanErrorKind.NoPaper, e.Kind);
        Assert.False(backend.IsDisturbed(device));
    }

    [Fact]
    public async Task Cancel_WhileScanning_ThenNewScanAtOnce()
    {
        if (!Active("abbrechen")) return;
        var (backend, _) = Backend();
        var device = await Find(backend);
        await using var session = await OpenTrusting(backend, device);
        Assert.NotNull(CombinedScanBackend.EsclOf(session));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScanWith(session, ScanSourceKind.Flatbed, 600, ScanColorMode.Color, null, cancel.Token));
        output.WriteLine($"  abgebrochen nach {watch.ElapsedMilliseconds} ms");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"Abbruch dauerte {watch.Elapsed}");
        await Status(session, "nach dem Abbruch");
        watch.Restart();
        AssertA4(Assert.Single(await ScanWith(session, ScanSourceKind.Flatbed, 75, ScanColorMode.Gray)), 75, 1);
        output.WriteLine($"  neuer Scan fertig nach {watch.ElapsedMilliseconds} ms");
        Assert.False(backend.IsDisturbed(device));
    }

    [Fact]
    public async Task Fallback_CertificateDeclinedOrSwitchOff_SystemWayScans()
    {
        if (!Active("rueckfall")) return;
        // 1. the certificate declined („Nicht vertrauen“), where the device asks; a device without TLS asks nothing
        var (backend, _) = Backend();
        var device = await Find(backend);
        try
        {
            var direct = await backend.OpenAsync(device);
            output.WriteLine("  keine Zertifikatsfrage (eSCL ohne TLS) — nur der Schalter wird geprüft");
            await direct.DisposeAsync();
        }
        catch (EsclCertificateException e)
        {
            output.WriteLine($"  Zertifikatsfrage ({EsclTrust.Display(e.Fingerprint)}) — „Nicht vertrauen“");
            backend.Avoid(device, "Zertifikat nicht bestätigt");
            await ScanOverTheSystemWay(backend, device);
        }

        // 2. the switch „Netzwerk-Scanner direkt ansprechen“ off: the same device over the system's way, no request to it over eSCL
        var (off, _) = Backend(new EsclSettings(Direct: false));
        var plain = await Find(off);
        output.WriteLine("  Schalter aus:");
        Assert.DoesNotContain(plain.AllIds, EsclBackend.Owns);
        await ScanOverTheSystemWay(off, plain);
        Assert.Empty(off.Escl.Log.Entries);
    }

    [Fact]
    public async Task Fallback_Feeder_SystemWayScansTheStack()
    {
        if (!Active("rueckfall-einzug")) return;
        var (off, _) = Backend(new EsclSettings(Direct: false));
        var device = await Find(off);
        await using var session = await off.OpenAsync(device);
        Assert.Null(CombinedScanBackend.EsclOf(session));
        var pages = await ScanWith(session, ScanSourceKind.Feeder, 150, ScanColorMode.Gray);
        Assert.True(pages.Count >= 2, $"{pages.Count} Seiten");
        Assert.All(pages, p => AssertA4(p, 150, 1));
        Assert.Empty(off.Escl.Log.Entries);
    }

    /// <summary>
    /// „Netz durchsuchen“ as the window does it (No. 926), in the network named by PDFKNEAD_ESCL_NETZ (e.g. „10.242.66.0“): the scanners
    /// found with their host names, timings and every exception — for a network the multicast search does not reach. Only on request: the
    /// search asks every address of the network on the usual eSCL ports.
    /// </summary>
    [Fact]
    public async Task NetworkSearch_FindsScanners_WithHostNames()
    {
        if (Run != "netzsuche" || Environment.GetEnvironmentVariable("PDFKNEAD_ESCL_NETZ") is not { Length: > 0 } network) return;
        var settings = new EsclSettings(Networks: [network]);
        var escl = new EsclBackend(() => settings, new EsclMemoryPins());
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var progress = new Progress<(int Done, int Total)>(p => { if (p.Done % 100 == 0) output.WriteLine($"  {p.Done}/{p.Total} nach {watch.ElapsedMilliseconds} ms"); });
        IReadOnlyList<EsclFoundScanner> found;
        try
        {
            found = await escl.SearchNetworksAsync(progress);
        }
        finally
        {
            foreach (var line in escl.Log.Entries) output.WriteLine("  " + line);
        }
        output.WriteLine($"fertig nach {watch.ElapsedMilliseconds} ms: {found.Count} Scanner");
        foreach (var (device, host) in found)
            output.WriteLine($"  {device.Name} | {string.Join(", ", device.Services.Select(s => $"{(s.Tls ? "https" : "http")} {string.Join("/", s.Addresses)}:{s.Port}"))} | {host ?? "(kein Name)"}");
        Assert.NotEmpty(found);
    }

    private async Task ScanOverTheSystemWay(CombinedScanBackend backend, ScanDevice device)
    {
        await using var session = await backend.OpenAsync(device);
        Assert.Null(CombinedScanBackend.EsclOf(session));
        output.WriteLine($"  geöffnet über {session.GetType().Name}: {string.Join(", ", session.Capabilities.Sources.Select(s => s.Name))}");
        AssertA4(Assert.Single(await ScanWith(session, ScanSourceKind.Flatbed, 150, ScanColorMode.Gray)), 150, 1);
    }
}
