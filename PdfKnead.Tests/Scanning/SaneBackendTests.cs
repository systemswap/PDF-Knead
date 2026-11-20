using PdfKnead.Core.Scanning;
using PdfKnead.Core.Scanning.Sane;

namespace PdfKnead.Tests.Scanning;

/// <summary>SANE keeps global state for the process and reads its configuration once: these tests switch it and so run alone.</summary>
[CollectionDefinition(nameof(SaneCollection), DisableParallelization = true)]
public sealed class SaneCollection;

/// <summary>
/// Scanning through SANE with its virtual test device („test“ back end, part of sane-backends): a configuration folder of its own makes
/// SANE see only that device, nothing of the system. Skipped where SANE or the test back end is missing (Windows, minimal systems).
/// </summary>
[Collection(nameof(SaneCollection))]
public sealed class SaneBackendTests
{
    private static readonly bool Ready = Prepare();

    private static bool Prepare()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/lib/x86_64-linux-gnu/sane/libsane-test.so.1") && !Directory.EnumerateFiles("/usr/lib", "libsane-test.so*", SearchOption.AllDirectories).Any())
            return false;
        var folder = Directory.CreateTempSubdirectory("pdf-knead-sane-").FullName;
        File.WriteAllText(Path.Combine(folder, "dll.conf"), "test\n");
        SaneLibrary.ConfigDirectoryForTests = folder;
        return new SaneBackend().IsAvailable(out _);
    }

    private static async Task<IScanSession> OpenTestDevice()
    {
        var backend = new SaneBackend();
        var device = Assert.Single(await backend.FindDevicesAsync(), d => d.Id == "test:0");
        return await backend.OpenAsync(device);
    }

    [Fact]
    public async Task FindDevices_OnlyTheTestDevices()
    {
        if (!Ready) return;
        var devices = await new SaneBackend().FindDevicesAsync();
        Assert.NotEmpty(devices);
        Assert.All(devices, d => Assert.StartsWith("test:", d.Id));
        Assert.Contains("frontend-tester", devices[0].DisplayName);
    }

    [Fact]
    public async Task Capabilities_SourcesResolutionsModesArea()
    {
        if (!Ready) return;
        await using var session = await OpenTestDevice();
        var caps = session.Capabilities;
        Assert.Contains(caps.Sources, s => s.Kind == ScanSourceKind.Flatbed);
        Assert.Contains(caps.Sources, s => s.Kind == ScanSourceKind.Feeder);
        // the test device allows 1 … 1200 dpi in steps of 1: the usual values inside the range are offered
        Assert.Equal([50, 75, 100, 150, 200, 240, 300, 400, 600, 1200], caps.Resolutions);
        Assert.Equal([ScanColorMode.Color, ScanColorMode.Gray], caps.ColorModes);
        Assert.Equal(200, caps.MaxWidthMm, 3);
        Assert.Equal(200, caps.MaxHeightMm, 3);
        var feeder = await session.SelectSourceAsync(caps.Sources.First(s => s.Kind == ScanSourceKind.Feeder).Name);
        Assert.Equal(ScanSourceKind.Feeder, feeder.Source?.Kind);
    }

    [Theory]
    [InlineData(ScanColorMode.Color, 3)]
    [InlineData(ScanColorMode.Gray, 1)]
    public async Task Scan_Flatbed_OnePage_SizeFromAreaAndDpi(ScanColorMode mode, int channels)
    {
        if (!Ready) return;
        await using var session = await OpenTestDevice();
        var flatbed = session.Capabilities.Sources.First(s => s.Kind == ScanSourceKind.Flatbed).Name;
        var pages = new List<ScannedImage>();
        await foreach (var page in session.ScanAsync(new ScanSettings(flatbed, 50, mode, WidthMm: 100, HeightMm: 50))) pages.Add(page);
        var image = Assert.Single(pages);
        Assert.Equal(channels, image.Channels);
        Assert.Equal(50, image.Dpi);
        // 100 mm × 50 mm at 50 dpi: about 197 × 98 pixels (the device rounds to whole lines)
        Assert.InRange(image.Width, 195, 199);
        Assert.InRange(image.Height, 96, 100);
        Assert.Equal((long)image.Width * image.Height * channels, image.Pixels.LongLength);
        // the page size in points follows the pixels and the resolution (100 mm minus the rounding to whole pixels)
        Assert.InRange(image.WidthPoints, 100 * 72 / 25.4 - 2, 100 * 72 / 25.4 + 1);
    }

    [Fact]
    public async Task Scan_Feeder_AllPagesUntilEmpty()
    {
        if (!Ready) return;
        await using var session = await OpenTestDevice();
        var feeder = session.Capabilities.Sources.First(s => s.Kind == ScanSourceKind.Feeder).Name;
        var count = 0;
        var reported = new List<ScanProgress>();
        await foreach (var page in session.ScanAsync(new ScanSettings(feeder, 25, ScanColorMode.Gray, 50, 50), new SyncProgress(reported.Add)))
        {
            count++;
            Assert.Equal(1, page.Channels);
        }
        // the test device's feeder holds ten sheets, then reports that it is empty: that ends the job without an error
        Assert.Equal(10, count);
        Assert.Contains(reported, p => p.Page == 10);
    }

    [Fact]
    public async Task Scan_SigpipeStaysIgnored()
    {
        // Fehlergedächtnis 959: SANE's thread helper set SIGPIPE to „terminate“ for the whole process when a scan started; a later write
        // into a closed connection ended the test process (exit code 141)
        if (!Ready) return;
        await using var session = await OpenTestDevice();
        await foreach (var _ in session.ScanAsync(new ScanSettings(null, 25, ScanColorMode.Gray, 30, 30))) { }
        Assert.Equal("ignoriert", Sigpipe());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in session.ScanAsync(new ScanSettings(null, 25, ScanColorMode.Gray), cancel: cancel.Token)) { }
        });
        Assert.Equal("ignoriert", Sigpipe());
    }

    private static unsafe string Sigpipe()
    {
        var action = stackalloc byte[256];   // struct sigaction; the handler comes first
        if (SigactionNative(13, null, action) != 0) return "unbekannt";
        return *(nint*)action switch { 0 => "beendet den Prozess", 1 => "ignoriert", var h => $"Handler {h:X}" };
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "sigaction")]
    private static extern unsafe int SigactionNative(int signal, void* action, void* old);

    [Fact]
    public async Task Scan_Cancelled_StopsWithoutPages()
    {
        if (!Ready) return;
        await using var session = await OpenTestDevice();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in session.ScanAsync(new ScanSettings(null, 50, ScanColorMode.Gray), cancel: cancel.Token)) { }
        });
        // the device is free again afterwards
        var pages = 0;
        await foreach (var _ in session.ScanAsync(new ScanSettings(null, 25, ScanColorMode.Gray, 30, 30))) pages++;
        Assert.True(pages >= 1);
    }

    [Theory]
    [InlineData("SANE_STATUS_NO_DOCS", ScanErrorKind.NoPaper)]   // the empty feeder reported while reading: no page, „no paper“
    [InlineData("SANE_STATUS_JAMMED", ScanErrorKind.Jammed)]
    [InlineData("SANE_STATUS_COVER_OPEN", ScanErrorKind.CoverOpen)]
    public async Task Scan_Feeder_ErrorsWhileReading(string status, ScanErrorKind kind)
    {
        if (!Ready) return;
        await using var session = await OpenTestDevice();
        ((SaneSession)session).TestOptions = new() { ["read-return-value"] = status };
        var feeder = session.Capabilities.Sources.First(s => s.Kind == ScanSourceKind.Feeder).Name;
        var e = await Assert.ThrowsAsync<ScanException>(async () =>
        {
            await foreach (var _ in session.ScanAsync(new ScanSettings(feeder, 25, ScanColorMode.Gray, 30, 30))) { }
        });
        Assert.Equal(kind, e.Kind);
        // the device works again afterwards
        ((SaneSession)session).TestOptions = new() { ["read-return-value"] = "Default" };
        var pages = 0;
        await foreach (var _ in session.ScanAsync(new ScanSettings(null, 25, ScanColorMode.Gray, 30, 30))) pages++;
        Assert.True(pages >= 1);
    }

    [Theory]
    [InlineData("Color", ScanColorMode.Color)]
    [InlineData("24bit Color", ScanColorMode.Color)]
    [InlineData("Gray", ScanColorMode.Gray)]
    [InlineData("True Gray", ScanColorMode.Gray)]
    [InlineData("Lineart", ScanColorMode.BlackWhite)]
    [InlineData("Black & White", ScanColorMode.BlackWhite)]
    [InlineData("Halftone", null)]                 // dots imitating grey are no clean black and white
    [InlineData("Gray[Error Diffusion]", null)]
    [InlineData("Farbe", ScanColorMode.Color)]     // translated values of back ends
    [InlineData("Graustufen", ScanColorMode.Gray)]
    [InlineData("Strichzeichnung", ScanColorMode.BlackWhite)]
    [InlineData("Couleur", ScanColorMode.Color)]
    [InlineData("Niveaux de gris", ScanColorMode.Gray)]
    [InlineData("Infrared", null)]
    public void ModeOf_NamesOfBackEnds(string name, ScanColorMode? mode) => Assert.Equal(mode, SaneSession.ModeOf(name));

    [Theory]
    [InlineData(SaneLibrary.FrameRgb, 100, 300, 8, true)]
    [InlineData(SaneLibrary.FrameRgb, 100, 299, 8, false)]     // a row too short for its pixels: never read past it
    [InlineData(SaneLibrary.FrameGray, 100, 13, 1, true)]       // 100 bits = 13 bytes
    [InlineData(SaneLibrary.FrameGray, 100, 12, 1, false)]
    [InlineData(SaneLibrary.FrameGray, 100, 200, 16, true)]
    [InlineData(SaneLibrary.FrameGray, 100, 100, 4, false)]     // unknown depth
    [InlineData(7, 100, 300, 8, false)]                         // unknown format
    [InlineData(SaneLibrary.FrameGray, 0, 0, 8, false)]
    [InlineData(SaneLibrary.FrameGray, 2_000_000, 2_000_000, 8, false)]
    [InlineData(SaneLibrary.FrameGray, 100, 1_000_000, 8, false)]   // absurd padding
    public void FrameParameters_OnlyPlausibleAccepted(int format, int pixels, int bytesPerLine, int depth, bool plausible) =>
        Assert.Equal(plausible, SaneSession.Plausible(new SaneLibrary.Parameters
        {
            Format = format, PixelsPerLine = pixels, BytesPerLine = bytesPerLine, Depth = depth, Lines = 10, LastFrame = 1,
        }));

    /// <summary>Progress that reports at once on the reporting thread (Progress&lt;T&gt; would post to a later moment).</summary>
    private sealed class SyncProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value)
        {
            lock (this) report(value);
        }
    }
}

/// <summary>Choices of resolutions and sources from what drivers report.</summary>
public sealed class ScanChoicesTests
{
    [Fact]
    public void Resolutions_FromRange_UsualValuesInside()
    {
        Assert.Equal([75, 100, 150, 200, 240, 300, 400, 600], ResolutionChoices.FromRange(75, 600, 1));
        Assert.Equal([100, 200, 300, 400, 600], ResolutionChoices.FromRange(100, 600, 100));
        // no usual value inside: the bounds
        Assert.Equal([110, 130], ResolutionChoices.FromRange(110, 130, 0));
        // reversed bounds, absurd maximum
        Assert.Equal([300, 400, 600, 1200, 2400, 4800], ResolutionChoices.FromRange(100_000, 300, 1));
    }

    [Fact]
    public void Resolutions_FromList_SortedWithoutNonsense() =>
        Assert.Equal([75, 150, 300, 1200], ResolutionChoices.FromList([300, 75, 1200, 150, 300, 0, -5, 9600]));

    [Theory]
    [InlineData("Flatbed", ScanSourceKind.Flatbed)]
    [InlineData("ADF", ScanSourceKind.Feeder)]
    [InlineData("ADF Duplex", ScanSourceKind.FeederDuplex)]
    [InlineData("ADF Front", ScanSourceKind.Feeder)]
    [InlineData("Automatic Document Feeder", ScanSourceKind.Feeder)]
    [InlineData("Document Feeder (both sides)", ScanSourceKind.FeederDuplex)]
    [InlineData("Transparency Unit", ScanSourceKind.Other)]
    [InlineData("Flachbett", ScanSourceKind.Flatbed)]
    [InlineData("Automatischer Vorlageneinzug", ScanSourceKind.Feeder)]
    [InlineData("Vorlageneinzug beidseitig", ScanSourceKind.FeederDuplex)]
    [InlineData("Chargeur recto verso", ScanSourceKind.FeederDuplex)]
    [InlineData("Vitre", ScanSourceKind.Flatbed)]
    public void Sources_KindFromName(string name, ScanSourceKind kind) => Assert.Equal(kind, ScanSource.FromName(name).Kind);

    [Fact]
    public void Capabilities_ClosestResolution()
    {
        var caps = new ScanCapabilities([], null, [75, 150, 300], [ScanColorMode.Color], 216, 297);
        Assert.Equal(300, caps.ClosestResolution(1200));   // the feeder offers less than the profile asks for
        Assert.Equal(150, caps.ClosestResolution(160));
        Assert.Equal(75, caps.ClosestResolution(112));
        Assert.Equal(300, new ScanCapabilities([], null, [150, 300], [], 0, 0).ClosestResolution(225));   // tie → the higher one
        Assert.Equal(200, new ScanCapabilities([], null, [], [], 0, 0).ClosestResolution(200));
    }

    [Fact]
    public void DeviceName_WithoutRepeatedVendor()
    {
        Assert.Equal("HP Color LaserJet", new ScanDevice("airscan:e1", "HP", "HP Color LaserJet", "eSCL").DisplayName);
        Assert.Equal("Canon LiDE 300", new ScanDevice("pixma:x", "Canon", "LiDE 300", "flatbed").DisplayName);
        Assert.Equal("net:dev", new ScanDevice("net:dev", "", "", "").DisplayName);
        // a network back end reports the protocol as the vendor
        Assert.Equal("HP Color LaserJet MFP", new ScanDevice("airscan:e1:x", "eSCL", "HP Color LaserJet MFP", "ip=…").DisplayName);
    }

    [Fact]
    public void OwnShares_LeftOut_OtherNetworkScannersStay()
    {
        var own = new[] { System.Net.IPAddress.Parse("192.0.2.10"), System.Net.IPAddress.Loopback, System.Net.IPAddress.Parse("fe80::1") };
        var devices = ScanDevice.WithoutOwnShares(
        [
            new ScanDevice("escl:https://192.0.2.10:9443", "Canon", "LiDE", "flatbed"),        // this computer shares its USB scanner
            new ScanDevice("airscan:e2:Canon LiDE", "eSCL", "Canon LiDE", "ip=127.0.0.1"),
            new ScanDevice("escl:https://[fe80::1]:443", "Canon", "LiDE", "flatbed"),
            new ScanDevice("escl:https://192.0.2.6:443", "HP", "LaserJet", "platen,adf scanner"),
            new ScanDevice("airscan:e1:HP LaserJet", "eSCL", "HP LaserJet", "ip=192.0.2.6"),
            new ScanDevice("genesys:libusb:001:004", "Canon", "LiDE 120", "flatbed scanner"),   // no address, a USB device
        ], own);
        Assert.Equal(["escl:https://192.0.2.6:443", "airscan:e1:HP LaserJet", "genesys:libusb:001:004"], devices.Select(d => d.Id));
    }

    [Fact]
    public void DeviceName_SameDeviceThroughTwoBackEnds_OneEntry_BestWayFirst()
    {
        // found in this order: one entry for the device, the back end that reports the feeder's resolutions correctly gives the id, the other
        // stays as the way to fall back on (the program chooses the driver itself)
        var devices = ScanDevice.CombineBackEnds(
        [
            new ScanDevice("escl:https://192.0.2.1:443", "HP", "X", "platen,adf scanner"),
            new ScanDevice("pixma:canon", "Canon", "LiDE 300", "flatbed"),
            new ScanDevice("airscan:e1:HP X", "eSCL", "HP X", "ip"),
        ]);
        Assert.Equal(["HP X", "Canon LiDE 300"], devices.Select(d => d.DisplayName));
        Assert.Equal(["airscan:e1:HP X", "escl:https://192.0.2.1:443"], devices[0].AllIds);
        Assert.True(devices[0].Has("escl:https://192.0.2.1:443"));
        // two devices of the same model in one back end stay apart (which escl entry belongs to which could not be told), marked by back end
        var two = ScanDevice.CombineBackEnds(
        [
            new ScanDevice("airscan:e1:HP X", "eSCL", "HP X", "ip"),
            new ScanDevice("airscan:e2:HP X", "eSCL", "HP X", "ip"),
            new ScanDevice("escl:https://192.0.2.1:443", "HP", "X", ""),
        ]);
        Assert.Equal(3, two.Count);
        Assert.All(two, d => Assert.Null(d.Alternates));
        Assert.Equal(["HP X (airscan)", "HP X (airscan)", "HP X (escl)"], two.Select(d => d.DisplayName));
    }
}
