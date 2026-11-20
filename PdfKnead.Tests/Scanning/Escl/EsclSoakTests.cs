using System.Diagnostics;
using PdfKnead.Core.Scanning;
using PdfKnead.Core.Scanning.Escl;

namespace PdfKnead.Tests.Scanning.Escl;

/// <summary>
/// Many jobs in a row against the scanner in software (S9e, process stability): flatbed, feeder, cancelled while waiting, refused, a delivery
/// that breaks off — memory, open files and connections and threads must not grow with the number of jobs. Only with PDFKNEAD_MESSEN=escl-dauer
/// (takes minutes); alone, so that other tests do not count along.
/// </summary>
[Collection(nameof(EsclMemoryCollection))]
public sealed class EsclSoakTests
{
    [Fact]
    public async Task ManyJobs_NothingGrows()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") != "escl-dauer") return;
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("PDFKNEAD_RUNDEN"), out var r) ? r : 300;
        await using var scanner = new FakeScanner { ClosingAnswers = 2 };
        var timeouts = new EsclTimeouts(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));
        var caps = EsclCapabilities.Read(System.Text.Encoding.UTF8.GetBytes(scanner.Description))!;
        var image = FakeScanner.Image(400, 560, true);
        async Task Round(int i)
        {
            using var client = new EsclClient(scanner.Device.EndPoint, false, "eSCL", "dauer", null, EsclTrustMode.Pinned, null, timeouts);
            scanner.Pages.Clear();
            var kind = i % 5;
            for (var k = 0; k < (kind == 1 ? 3 : 1); k++)
                scanner.Pages.Add(new FakePage(image, CutOnce: kind == 4, Delay: kind == 2 ? TimeSpan.FromSeconds(5) : default));
            if (kind == 3) scanner.CreateAnswers.Enqueue(409);
            using var cancel = new CancellationTokenSource(kind == 2 ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(30));
            try
            {
                await foreach (var _ in EsclScanJob.RunAsync(client, caps, new ScanSettings(kind == 1 ? "Feeder" : "Platen", 150, ScanColorMode.Color),
                                   null, null, cancel.Token)) { }
            }
            catch (Exception e) when (e is ScanException or OperationCanceledException)
            {
            }
            if (client.Closing is { } closing) await closing;
        }
        for (var i = 0; i < 20; i++) await Round(i);   // warmed up: pools, code, connections
        var (memory, files, threads) = Measure();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < rounds; i++) await Round(i);
        var (memoryAfter, filesAfter, threadsAfter) = Measure();
        var report = $"{rounds} Aufträge in {clock.Elapsed.TotalSeconds:0} s: Speicher {memory >> 20} → {memoryAfter >> 20} MB, offene Dateien {files} → {filesAfter}, Threads {threads} → {threadsAfter}";
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "pdfknead-escl-dauer.txt"), report);
        Assert.True(memoryAfter - memory < 40L << 20, report);
        Assert.True(filesAfter - files < 30, report);
        Assert.True(threadsAfter - threads < 30, report);
    }

    private static (long Memory, int Files, int Threads) Measure()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Thread.Sleep(500);   // closed connections leave the pool
        using var process = Process.GetCurrentProcess();
        var files = OperatingSystem.IsLinux() ? Directory.GetFiles("/proc/self/fd").Length : process.HandleCount;
        return (GC.GetTotalMemory(true), files, process.Threads.Count);
    }
}
