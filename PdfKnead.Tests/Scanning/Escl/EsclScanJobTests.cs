using System.Text;
using PdfKnead.Core.Scanning;
using PdfKnead.Core.Scanning.Escl;
using SkiaSharp;

namespace PdfKnead.Tests.Scanning.Escl;

/// <summary>Scan jobs against the scanner in software (KONZEPT.md §5.25, S9c): pages, waiting, ends, errors, cancelling — the job is always deleted once.</summary>
[Collection(nameof(EsclDeviceCollection))]
public sealed class EsclScanJobTests
{
    private static readonly EsclTimeouts Quick = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2));

    private static readonly ScanSettings Platen = new("Platen", 300, ScanColorMode.Color, 210, 297);
    private static readonly ScanSettings Feeder = new("Feeder", 200, ScanColorMode.Gray);

    private static EsclClient Client(FakeScanner scanner, EsclDiagnostics? log = null) =>
        new(scanner.Device.EndPoint, false, "eSCL", "geraet", null, EsclTrustMode.Pinned, log, Quick);

    private static EsclCapabilities Caps(FakeScanner scanner) =>
        EsclCapabilities.Read(Encoding.UTF8.GetBytes(scanner.Description)) ?? throw new InvalidOperationException();

    private static async Task<List<ScannedImage>> Scan(FakeScanner scanner, ScanSettings settings, EsclDiagnostics? log = null,
        CancellationToken cancel = default)
    {
        using var client = Client(scanner, log);
        var pages = new List<ScannedImage>();
        await foreach (var page in EsclScanJob.RunAsync(client, Caps(scanner), settings, null, log, cancel)) pages.Add(page);
        if (client.Closing is { } closing) await closing;   // the job closes in the background after the last page
        return pages;
    }

    /// <summary>Runs a job expected to fail and returns the error and the pages handed out before it.</summary>
    private static async Task<(ScanException Error, List<ScannedImage> Pages)> Failing(FakeScanner scanner, ScanSettings settings)
    {
        using var client = Client(scanner);
        var pages = new List<ScannedImage>();
        var e = await Assert.ThrowsAnyAsync<ScanException>(async () =>
        {
            await foreach (var page in EsclScanJob.RunAsync(client, Caps(scanner), settings)) pages.Add(page);
        });
        if (client.Closing is { } closing) await closing;
        return (e, pages);
    }

    [Fact]
    public async Task Platen_OnePage_RequestAndDelete()
    {
        await using var scanner = new FakeScanner();
        scanner.Pages.Add(new FakePage(FakeScanner.Image(240, 320, true)));
        var log = new EsclDiagnostics();
        var page = Assert.Single(await Scan(scanner, Platen, log));
        Assert.Equal((240, 320, 3, 300), (page.Width, page.Height, page.Channels, page.Dpi));
        Assert.Equal(1, scanner.Deletes);
        var xml = System.Xml.Linq.XElement.Parse(Assert.Single(scanner.Settings));
        Assert.Equal("Platen", xml.Text("InputSource"));
        Assert.Null(xml.Child("Duplex"));
        Assert.Equal("RGB24", xml.Text("ColorMode"));
        Assert.Equal(300, xml.Int("XResolution"));
        Assert.Equal("image/jpeg", xml.Text("DocumentFormatExt"));
        Assert.Equal("image/jpeg", xml.Text("DocumentFormat"));
        Assert.Equal("2.63", xml.Text("Version"));
        var region = xml.Child("ScanRegions").Child("ScanRegion");
        Assert.Equal(2480, region.Int("Width"));    // 210 mm
        Assert.Equal(3508, region.Int("Height"));   // 297 mm, the device's largest
        // the state first, then the job, the page, the end, the state again, and the deletion
        Assert.Equal(["GET /eSCL/ScannerStatus", "POST /eSCL/ScanJobs", "GET /eSCL/ScanJobs/job1/NextDocument", "GET /eSCL/ScanJobs/job1/NextDocument",
            "GET /eSCL/ScannerStatus", "DELETE /eSCL/ScanJobs/job1"], scanner.Device.Requests.Select(r => $"{r.Method} {r.Path}"));
        Assert.Contains(log.Entries, e => e.Contains("fertig nach 1 Seite"));
    }

    [Fact]
    public async Task Feeder_PagesWithWaiting_AndDuplicateSkipped()
    {
        await using var scanner = new FakeScanner();
        var gray = FakeScanner.Image(100, 140, false);
        scanner.Pages.Add(new FakePage(gray, ContentLocation: "/eSCL/ScanJobs/job1/1"));
        scanner.Pages.Add(new FakePage(gray, ContentLocation: "/eSCL/ScanJobs/job1/1"));   // the same page again
        scanner.Pages.Add(new FakePage(gray, BusyBefore: 3, ContentLocation: "/eSCL/ScanJobs/job1/2"));
        scanner.Pages.Add(new FakePage(gray));
        var pages = await Scan(scanner, Feeder);
        Assert.Equal(3, pages.Count);
        Assert.All(pages, p => Assert.Equal((1, 200), (p.Channels, p.Dpi)));
        Assert.Equal(1, scanner.Deletes);
        var xml = System.Xml.Linq.XElement.Parse(Assert.Single(scanner.Settings));
        Assert.Equal(("Feeder", "false", "Grayscale8", 200), (xml.Text("InputSource"), xml.Text("Duplex"), xml.Text("ColorMode"), xml.Int("XResolution")));
    }

    [Fact]
    public async Task Duplex_AsksForBothSides()
    {
        await using var scanner = new FakeScanner();
        scanner.Pages.Add(new FakePage(FakeScanner.Image(50, 60, false)));
        await Scan(scanner, Feeder with { Source = "Feeder" + ScanSource.DuplexMarker });
        Assert.Equal("true", System.Xml.Linq.XElement.Parse(scanner.Settings[0]).Text("Duplex"));
    }

    [Theory]
    [InlineData("ScannerAdfEmpty", ScanErrorKind.NoPaper)]
    [InlineData("ScannerAdfJam", ScanErrorKind.Jammed)]
    [InlineData("ScannerAdfHatchOpen", ScanErrorKind.CoverOpen)]
    [InlineData("ScannerAdfMultipickDetected", ScanErrorKind.Jammed)]
    public async Task Feeder_ProblemBeforeStart_NoJob(string feeder, ScanErrorKind kind)
    {
        await using var scanner = new FakeScanner { Feeder = feeder };
        scanner.Pages.Add(new FakePage(FakeScanner.Image(50, 60, false)));
        var (e, _) = await Failing(scanner, Feeder);
        Assert.Equal(kind, e.Kind);
        Assert.Equal(0, scanner.Creates);
        Assert.Equal(0, scanner.Deletes);
    }

    [Fact]
    public async Task Feeder_EmptyWithoutDetection_TriedAnyway()
    {
        await using var scanner = new FakeScanner { Feeder = "ScannerAdfEmpty" };
        scanner.Description = scanner.Description.Replace("<scan:AdfOption>DetectPaperLoaded</scan:AdfOption>", "");
        scanner.Pages.Add(new FakePage(FakeScanner.Image(50, 60, false)));
        Assert.Single(await Scan(scanner, Feeder));
    }

    [Fact]
    public async Task Platen_IgnoresFeederState()
    {
        await using var scanner = new FakeScanner { Feeder = "ScannerAdfJam" };
        scanner.Pages.Add(new FakePage(FakeScanner.Image(50, 60, true)));
        Assert.Single(await Scan(scanner, Platen));
    }

    [Fact]
    public async Task NoPages_NoImageData_JobDeleted()
    {
        await using var scanner = new FakeScanner { Feeder = "ScannerAdfLoaded" };
        var (e, pages) = await Failing(scanner, Feeder);
        Assert.Contains("Bilddaten", e.Message);
        Assert.Empty(pages);
        Assert.Equal(1, scanner.Deletes);
    }

    [Fact]
    public async Task Refused_FallsBackToJpeg_ThenGivesUp()
    {
        await using var scanner = new FakeScanner();
        scanner.Description = scanner.Description.Replace("<pwg:DocumentFormat>application/pdf</pwg:DocumentFormat>",
            "<pwg:DocumentFormat>image/png</pwg:DocumentFormat>");
        scanner.Pages.Add(new FakePage(FakeScanner.Image(50, 60, false)));
        scanner.CreateAnswers.Enqueue(409);
        var platenGray = Platen with { Mode = ScanColorMode.BlackWhite };
        Assert.Single(await Scan(scanner, platenGray));
        Assert.Equal(["image/png", "image/jpeg"], scanner.Settings.Select(x => System.Xml.Linq.XElement.Parse(x).Text("DocumentFormatExt")));
        Assert.Equal(["BlackAndWhite1", "Grayscale8"], scanner.Settings.Select(x => System.Xml.Linq.XElement.Parse(x).Text("ColorMode")));
        // with JPEG already there is nothing to give way on
        scanner.CreateAnswers.Enqueue(409);
        var (e, _) = await Failing(scanner, Platen);
        Assert.Contains("lehnt die Einstellung ab", e.Message);
        Assert.Equal(1, scanner.Deletes);   // only the job that existed
    }

    [Fact]
    public async Task Busy_OnCreate_RetriedThenReported()
    {
        await using var scanner = new FakeScanner();
        scanner.Pages.Add(new FakePage(FakeScanner.Image(50, 60, true)));
        scanner.CreateAnswers.Enqueue(503);
        scanner.CreateAnswers.Enqueue(503);
        Assert.Single(await Scan(scanner, Platen));
        // busy for good: reported once the time for „busy“ is used up
        foreach (var _ in Enumerable.Range(0, 1000)) scanner.CreateAnswers.Enqueue(503);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (e, _) = await Failing(scanner, Platen);
        Assert.Equal(ScanErrorKind.Busy, e.Kind);
        Assert.True(clock.Elapsed >= EsclScanJob.BusyOnCreate - TimeSpan.FromSeconds(1), $"gab nach {clock.Elapsed} auf");
    }

    [Theory]
    [InlineData(FakeLocation.AbsoluteSameHost, true)]
    [InlineData(FakeLocation.OtherHost, false)]
    [InlineData(FakeLocation.Missing, false)]
    public async Task JobAddress_OnlyFromThisDevice(FakeLocation location, bool works)
    {
        await using var scanner = new FakeScanner { Location = location };
        scanner.Pages.Add(new FakePage(FakeScanner.Image(50, 60, true)));
        if (works)
        {
            Assert.Single(await Scan(scanner, Platen));
            return;
        }
        await Failing(scanner, Platen);
        Assert.DoesNotContain(scanner.Device.Requests, r => r.Path.Contains("NextDocument"));
    }

    [Fact]
    public async Task CancelledAtDevice_AfterFirstPage_PageKept()
    {
        await using var scanner = new FakeScanner { EndState = "Canceled", EndReason = "JobCanceledByUser" };
        scanner.Pages.Add(new FakePage(FakeScanner.Image(50, 60, false)));
        var (e, pages) = await Failing(scanner, Feeder);
        Assert.Single(pages);
        Assert.Contains("am Scanner abgebrochen", e.Message);
        Assert.Equal(1, scanner.Deletes);
    }

    [Fact]
    public async Task AbortedWithJam_NamedAsJam()
    {
        await using var scanner = new FakeScanner { EndState = "Aborted", EndReason = "JobAbortedBySystem" };
        scanner.Pages.Add(new FakePage(FakeScanner.Image(50, 60, false)));
        using var client = Client(scanner);
        var pages = new List<ScannedImage>();
        var e = await Assert.ThrowsAnyAsync<ScanException>(async () =>
        {
            await foreach (var page in EsclScanJob.RunAsync(client, Caps(scanner), Feeder))
            {
                pages.Add(page);
                scanner.Feeder = "ScannerAdfJam";   // the paper jams after the first sheet
            }
        });
        Assert.Equal(ScanErrorKind.Jammed, e.Kind);
        Assert.Single(pages);
    }

    [Fact]
    public async Task UserCancel_WhileWaiting_JobDeletedOnce()
    {
        await using var scanner = new FakeScanner();
        scanner.Pages.Add(new FakePage(FakeScanner.Image(50, 60, true), Delay: TimeSpan.FromSeconds(10)));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scan(scanner, Platen, cancel: cancel.Token));
        Assert.Equal(1, scanner.Deletes);
    }

    [Fact]
    public async Task CallerStops_AfterFirstPage_JobDeletedOnce()
    {
        await using var scanner = new FakeScanner();
        foreach (var _ in Enumerable.Range(0, 3)) scanner.Pages.Add(new FakePage(FakeScanner.Image(50, 60, false)));
        using var client = Client(scanner);
        await foreach (var _ in EsclScanJob.RunAsync(client, Caps(scanner), Feeder)) break;
        Assert.Equal(1, scanner.Deletes);
        Assert.Single(scanner.Device.Requests, r => r.Path.EndsWith("NextDocument"));
    }

    [Fact]
    public async Task BrokenDelivery_JobStillActive_TakenUpAgain()
    {
        await using var scanner = new FakeScanner();
        scanner.Pages.Add(new FakePage(FakeScanner.Image(300, 400, true), CutOnce: true));
        scanner.Pages.Add(new FakePage(FakeScanner.Image(300, 400, true)));
        var log = new EsclDiagnostics();
        Assert.Equal(2, (await Scan(scanner, Feeder with { Mode = ScanColorMode.Color }, log)).Count);
        Assert.Contains(log.Entries, e => e.Contains("neuer Versuch"));
        Assert.Equal(1, scanner.Deletes);
    }

    [Fact]
    public async Task DamagedImage_Reported_JobDeleted()
    {
        await using var scanner = new FakeScanner();
        var data = FakeScanner.Image(200, 200, true);
        data = [.. data[..600], .. new byte[200], .. data[800..]];
        scanner.Pages.Add(new FakePage(data[..(data.Length / 3)]));
        var (e, _) = await Failing(scanner, Platen);
        Assert.Contains("beschädigt", e.Message);
        Assert.Equal(1, scanner.Deletes);
    }

    [Fact]
    public async Task WrongContentType_ContentDecides()
    {
        await using var scanner = new FakeScanner();
        scanner.Pages.Add(new FakePage(FakeScanner.Image(60, 80, true), ContentType: "image/png"));
        scanner.Pages.Add(new FakePage(FakeScanner.Image(60, 80, false, SKEncodedImageFormat.Png), ContentType: "application/octet-stream"));
        scanner.Pages.Add(new FakePage("%PDF-1.4\n"u8.ToArray(), ContentType: "image/jpeg"));
        var log = new EsclDiagnostics();
        using var client = Client(scanner, log);
        var pages = new List<ScannedImage>();
        var e = await Assert.ThrowsAnyAsync<ScanException>(async () =>
        {
            await foreach (var page in EsclScanJob.RunAsync(client, Caps(scanner), Feeder, null, log)) pages.Add(page);
        });
        Assert.Equal([3, 1], pages.Select(p => p.Channels));
        Assert.Contains("unbekanntes Bildformat", e.Message);
        Assert.Contains(log.Entries, l => l.Contains("die Daten sind aber JPEG"));
    }

    [Fact]
    public async Task EndWithGone_AfterCompletedPages_IsEnd()
    {
        await using var scanner = new FakeScanner { EndStatus = 410 };
        scanner.Pages.Add(new FakePage(FakeScanner.Image(50, 60, false)));
        Assert.Single(await Scan(scanner, Feeder));
    }

    [Fact]
    public async Task Dpi_FromFile_WhenPlausible()
    {
        static byte[] WithDensity(byte[] jpeg, int dpi)
        {
            // JFIF APP0 right after SOI: unit at offset 13, densities at 14 and 16
            var copy = (byte[])jpeg.Clone();
            if (copy[2] != 0xFF || copy[3] != 0xE0) throw new InvalidOperationException("kein JFIF");
            copy[13] = 1;
            copy[14] = (byte)(dpi >> 8); copy[15] = (byte)dpi; copy[16] = (byte)(dpi >> 8); copy[17] = (byte)dpi;
            return copy;
        }
        await using var scanner = new FakeScanner();
        var jpeg = FakeScanner.Image(50, 60, true);
        scanner.Pages.Add(new FakePage(WithDensity(jpeg, 150)));
        scanner.Pages.Add(new FakePage(WithDensity(jpeg, 72)));    // a mere default: the request counts
        scanner.Pages.Add(new FakePage(WithDensity(jpeg, 9000)));  // implausible
        Assert.Equal([150, 200, 200], (await Scan(scanner, Feeder)).Select(p => p.Dpi));
    }

    private static async Task<List<ScannedImage>> ScanPages(FakeScanner scanner, int maxPages)
    {
        using var client = Client(scanner);
        var pages = new List<ScannedImage>();
        await foreach (var page in EsclScanJob.RunAsync(client, Caps(scanner), Feeder, maxPages: maxPages)) pages.Add(page);
        if (client.Closing is { } closing) await closing;
        return pages;
    }

    [Fact]
    public async Task OnePage_DeviceDrawsOne_JobRunsToItsEnd()
    {
        // Fehlergedächtnis 952: deleting right after the page cancelled the job at the device, which then stayed busy
        await using var scanner = new FakeScanner();
        scanner.Description = scanner.Description.Replace("<scan:AdfOption>Duplex</scan:AdfOption>", "<scan:AdfOption>SelectSinglePage</scan:AdfOption>");
        foreach (var _ in Enumerable.Range(0, 3)) scanner.Pages.Add(new FakePage(FakeScanner.Image(50, 60, false)));
        Assert.Single(await ScanPages(scanner, 1));
        Assert.Contains("<scan:NumberOfPages>1</scan:NumberOfPages>", scanner.Settings[0]);
        Assert.Equal(["completed"], scanner.Endings);
        // a device that draws more than asked: the extra page is not handed out, the job is ended
        scanner.HonorsPageCount = false;
        Assert.Single(await ScanPages(scanner, 1));
        Assert.Equal(["completed", "deleted early"], scanner.Endings);
    }

    [Fact]
    public async Task OnePage_DeviceWithoutCount_StopsAfterIt()
    {
        await using var scanner = new FakeScanner();
        foreach (var _ in Enumerable.Range(0, 3)) scanner.Pages.Add(new FakePage(FakeScanner.Image(50, 60, false)));
        Assert.Single(await ScanPages(scanner, 1));
        Assert.DoesNotContain("NumberOfPages", scanner.Settings[0]);
        Assert.Single(scanner.Device.Requests, r => r.Path.EndsWith("NextDocument"));
        Assert.Equal(["deleted early"], scanner.Endings);
    }

    [Fact]
    public async Task Busy_AfterCancelledJob_WaitedFor()
    {
        // Fehlergedächtnis 952: after a cancelled job a device answered „busy“ for about eleven seconds
        await using var scanner = new FakeScanner();
        scanner.Pages.Add(new FakePage(FakeScanner.Image(50, 60, true)));
        foreach (var _ in Enumerable.Range(0, 8)) scanner.CreateAnswers.Enqueue(503);
        Assert.Single(await Scan(scanner, Platen));
        Assert.Equal(9, scanner.Creates);
    }

    [Fact]
    public async Task Platen_PageAtOnce_JobClosedInBackground_NextJobWaits()
    {
        await using var scanner = new FakeScanner { ClosingAnswers = 8 };   // the device needs a while to close the job after its page
        scanner.Pages.Add(new FakePage(FakeScanner.Image(40, 50, true)));
        using var client = Client(scanner);
        var phases = new List<ScanPhase>();
        var progress = new Recorder(phases);
        var pages = new List<ScannedImage>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await foreach (var page in EsclScanJob.RunAsync(client, Caps(scanner), Platen, progress)) pages.Add(page);
        var handedOver = clock.Elapsed;
        Assert.Single(pages);
        Assert.Equal(0, scanner.Deletes);   // still closing at the device
        Assert.NotNull(client.Closing);
        // the next job right away: it waits for the closing and says so
        scanner.Pages.Add(new FakePage(FakeScanner.Image(40, 50, true)));
        await foreach (var page in EsclScanJob.RunAsync(client, Caps(scanner), Platen with { Dpi = 150 }, progress)) pages.Add(page);
        await client.Closing!;
        Assert.Equal(2, pages.Count);   // from the glass one page per job
        Assert.Equal(2, scanner.Deletes);
        Assert.Contains(ScanPhase.Waiting, phases);
        Assert.DoesNotContain(ScanPhase.Finishing, phases);   // the window never shows the closing: it is free by then
        Assert.True(handedOver < TimeSpan.FromSeconds(1.5), $"Seite erst nach {handedOver}");
    }

    private sealed class Recorder(List<ScanPhase> phases) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value)
        {
            lock (phases) phases.Add(value.Phase);
        }
    }

    [Fact]
    public void Request_ChoosesFromDevice()
    {
        var caps = EsclCapabilities.Read(Encoding.UTF8.GetBytes(EsclSamples.Capabilities))!;
        var r = EsclScanRequest.Choose(caps, new ScanSettings("Platen", 330, ScanColorMode.Color));
        Assert.Equal(300, r.Dpi);                 // not on the 25-dpi grid: the closest of the choices offered
        Assert.Equal((2550, 3508), (r.Width, r.Height));   // the whole area
        r = EsclScanRequest.Choose(caps, new ScanSettings("Feeder", 600, ScanColorMode.BlackWhite, 500, 50));
        Assert.Equal((300, "Grayscale8", "image/jpeg"), (r.Dpi, r.ColorMode, r.Format));   // no lossless format: grey
        Assert.Equal((2550, 591), (r.Width, r.Height));   // clamped to the feeder
        r = EsclScanRequest.Choose(caps, new ScanSettings("Unbekannt", 250, ScanColorMode.Gray));
        Assert.Equal(EsclInput.Platen, r.Input);
        Assert.Equal(250, r.Dpi);
        var unknownVersion = EsclCapabilities.Read(Encoding.UTF8.GetBytes(EsclSamples.Capabilities.Replace(">2.63<", ">3.1<")))!;
        Assert.Equal("2.0", EsclScanRequest.Choose(unknownVersion, Platen).Version);
        var odd = EsclCapabilities.Read(Encoding.UTF8.GetBytes(EsclSamples.Capabilities.Replace("<scan:ColorMode>RGB24</scan:ColorMode>",
            "<scan:ColorMode>RGB24&lt;x&gt;</scan:ColorMode>")))!;
        var xml = Encoding.UTF8.GetString(EsclScanRequest.Choose(odd, Platen with { Source = "Feeder" }).ToXml());
        Assert.Contains("RGB24&lt;x&gt;", xml);   // written escaped
        Assert.NotNull(System.Xml.Linq.XElement.Parse(xml));
    }

    [Fact]
    public void Request_OddDevice_NoException()
    {
        // Fehlergedächtnis 958 (found by fuzzing): a smallest size larger than the largest, a wish of no sensible resolution
        var odd = EsclCapabilities.Read(Encoding.UTF8.GetBytes(EsclSamples.Capabilities.Replace("<scan:MinWidth>16</scan:MinWidth>", "<scan:MinWidth>9000</scan:MinWidth>")))!;
        var r = EsclScanRequest.Choose(odd, new ScanSettings("Platen", -4, ScanColorMode.Color, 50, 50));
        Assert.Equal(2550, r.Width);
        Assert.Equal(300, r.Dpi);
        Assert.Equal(300, EsclScanRequest.Choose(odd, new ScanSettings("Platen", 999_999, ScanColorMode.Color)).Dpi);
    }

    [Fact]
    public void Image_DamagedPng_AndTinyJpegClaimingAHugePage_Refused()
    {
        // Fehlergedächtnis 958: damaged compressed PNG data threw the inflater's own exception; a JPEG of a few hundred bytes claiming
        // 20 000 × 26 000 pixels took its full image memory before the decoder found no data
        // the sample the fuzzer found (a generated grey PNG, changed): its compressed data makes the inflater throw an exception of its own
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAGAAAACACAIAAAB7vvvtAAAAA3NCSVQICAjb4U/gAAAAzUlEQVR4II8KnfDFGlJe3Zzt0LEJwDAABLF3yP5BYYIECRIkKEyQIEGCBIUJEiRIkKAwQYIECRIUJkiQIEGCwgQJEiRIUJggQYIECQoTJEiQIEFhggQJEiQoTNCPDx6KM7nWlpXjAAAAAElFTkSuQmCCM2cCk/YKgSbQ2fbscPPuORtXggQJEiQoTJAgQYIEhQkSJEiQoDBBggQJEhQmSJAgQYLCBAkSJEhQmCBBggQJChMkSJAgQWGCBAkSJChMkCBBggSFCRIkSJCgMEGCBAkSFCZIkCBBgsIECRIkSFCYIEGCBAkKEyRIkCBBYYIECRIkKEyQIEGCBIUJEiRIkKAwQYIECRIUJkiQIEGCwgQJEiRIUJggQYIECQoTJEiQIEFhggQJEiQoTNCPDx6KM7nWlpXjAAAAAElFTkSuQmCC");
        Assert.ThrowsAny<ScanException>(() => EsclImage.Decode(png, "image/png", 300));
        var jpeg = FakeScanner.Image(16, 16, true);
        var sof = jpeg.AsSpan().IndexOf(new byte[] { 0xFF, 0xC0 });
        (jpeg[sof + 5], jpeg[sof + 6], jpeg[sof + 7], jpeg[sof + 8]) = (0x65, 0x90, 0x4E, 0x20);   // 26 000 × 20 000
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsAny<ScanException>(() => EsclImage.Decode(jpeg, "image/jpeg", 300));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 16 << 20, "Bildspeicher für eine Behauptung angelegt");
    }

    [Fact]
    public void Image_Limits()
    {
        Assert.ThrowsAny<ScanException>(() => EsclImage.Decode([], "image/jpeg", 300));
        Assert.ThrowsAny<ScanException>(() => EsclImage.Decode([0xFF, 0xD8, 0xFF, 0xC0, 0, 11, 8, 0xFF, 0xFF, 0xFF, 0xFF, 3], "image/jpeg", 300));
        var huge = FakeScanner.Image(16, 16, true);
        // a frame header claiming 65 535 × 65 535 pixels in colour: refused before memory is taken
        var at = huge.AsSpan().IndexOf(new byte[] { 0xFF, 0xC0 });
        huge[at + 5] = 0xFF; huge[at + 6] = 0xFF; huge[at + 7] = 0xFF; huge[at + 8] = 0xFF;
        Assert.ThrowsAny<ScanException>(() => EsclImage.Decode(huge, "image/jpeg", 300));
    }
}
