using PdfKnead.Core.Scanning.Escl;

namespace PdfKnead.Tests.Scanning.Escl;

/// <summary>
/// What receiving a large page allocates (Fehlergedächtnis 956, 957). The allocations are counted for the whole process, so these tests run
/// alone — tests running beside them would be counted too.
/// </summary>
[Collection(nameof(EsclMemoryCollection))]
public sealed class EsclMemoryTests
{
    [Theory]
    [InlineData(false, 1.3, false)]
    [InlineData(true, 2.5, false)]
    [InlineData(true, 2.6, true)]   // with progress: a page that is no countable JPEG is not copied again and again for the row count
    public async Task LargePage_ReadWithoutExtraCopies(bool chunked, double most, bool watched)
    {
        // a page of 40 MB: with its length announced read once into its array, in pieces at most once more for joining them
        var page = new byte[40 << 20];
        new Random(5).NextBytes(page);
        await using var device = new FakeEsclDevice { Handler = _ => new FakeAnswer(200, page, "image/jpeg", Chunked: chunked) };
        using var client = new EsclClient(device.EndPoint, false, device.Root, "geraet-1", null, EsclTrustMode.Pinned, null,
            new EsclTimeouts(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)));
        await client.GetStatusAsync(CancellationToken.None).ContinueWith(_ => { });   // connection and code warmed up
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var reports = 0;
        var answer = await client.GetDocumentAsync("ScanJobs/1/NextDocument", EsclImage.MaxBytes,
            watched ? new Counting(() => reports++) : null, CancellationToken.None);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.Equal(page, answer.Body);
        Assert.True(allocated < most * page.Length, $"{allocated / (double)page.Length:0.00} × die Seite belegt");
        Assert.True(!watched || reports > 10, "Fortschritt gemeldet");
    }

    private sealed class Counting(Action report) : IProgress<EsclTransfer>
    {
        public void Report(EsclTransfer value) => report();
    }
}

[CollectionDefinition(nameof(EsclMemoryCollection), DisableParallelization = true)]
public sealed class EsclMemoryCollection;

/// <summary>
/// Tests against the device in software with short time limits (two seconds per request, so that a test of a silent device ends
/// quickly; a cancelled search must end within three): beside the rest of the run under Windows a request to the loopback device took
/// longer now and then — a busy or slow device was reported offline, a cancelled search took five seconds (docs/FEHLERGEDAECHTNIS.md,
/// No. 917). They run alone; the limits stay short.
/// </summary>
[CollectionDefinition(nameof(EsclDeviceCollection), DisableParallelization = true)]
public sealed class EsclDeviceCollection;
