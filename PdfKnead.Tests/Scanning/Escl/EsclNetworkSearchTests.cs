using System.Net;
using PdfKnead.Core.Scanning.Escl;

namespace PdfKnead.Tests.Scanning.Escl;

/// <summary>Searching given networks address by address (KONZEPT.md §5.25, S9, „Netz der Scanner durchsuchen“).</summary>
[Collection(nameof(EsclDeviceCollection))]
public sealed class EsclNetworkSearchTests
{
    [Theory]
    [InlineData("192.168.10", "192.168.10.0/24", "192.168.10.1–254")]
    [InlineData(" 192.168.10.0 ", "192.168.10.0/24", "192.168.10.1–254")]
    [InlineData("192.168.10.77", "192.168.10.0/24", "192.168.10.1–254")]
    [InlineData("10.1.2.x", "10.1.2.0/24", "10.1.2.1–254")]
    [InlineData("10.1.2.*", "10.1.2.0/24", "10.1.2.1–254")]
    [InlineData("172.16.5.0/24", "172.16.5.0/24", "172.16.5.1–254")]
    [InlineData("172.31.5.40/28", "172.31.5.32/28", "172.31.5.33–46")]
    [InlineData("169.254.7", "169.254.7.0/24", "169.254.7.1–254")]
    public void Parse_Accepted(string text, string stored, string display)
    {
        var range = EsclNetworkRange.Parse(text, out var error);
        Assert.Null(error);
        Assert.Equal(stored, range!.ToString());
        Assert.Equal(display, range.Display);
        Assert.Equal(range.HostCount, range.Hosts.Count());
    }

    [Theory]
    [InlineData("192.168.10.0/16", "zu groß")]
    [InlineData("10.0.0.0/8", "zu groß")]
    [InlineData("8.8.8", "nicht im eigenen Netzwerk")]
    [InlineData("100.64.1", "nicht im eigenen Netzwerk")]
    [InlineData("127.0.0", "nicht im eigenen Netzwerk")]
    [InlineData("169.254.0", "nicht im eigenen Netzwerk")]
    [InlineData("192.168.300", "kein Netz")]
    [InlineData("192.168", "kein Netz")]
    [InlineData("1.2.3.4.5", "kein Netz")]
    [InlineData("192.168.10.0/31", "kein Netz")]
    [InlineData("192.168.10.0/x", "kein Netz")]
    [InlineData("+192.168.10", "kein Netz")]
    [InlineData("scanner.local", "kein Netz")]
    [InlineData("fd00::/64", "kein Netz")]
    public void Parse_Refused(string text, string reason)
    {
        Assert.Null(EsclNetworkRange.Parse(text, out var error));
        Assert.Contains(reason, error);
    }

    [Fact]
    public void ParseList_SeparatorsDuplicatesErrors()
    {
        var ranges = EsclNetworkRange.ParseList("192.168.10, 192.168.10.5;10.0.5\n8.8.8\t172.20.1.0/24", out var errors);
        Assert.Equal(["192.168.10.0/24", "10.0.5.0/24", "172.20.1.0/24"], ranges.Select(r => r.ToString()));
        Assert.Single(errors);
        Assert.Empty(EsclNetworkRange.ParseList("  ", out errors));
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Search_FindsDevices_JoinsByUuid_IgnoresOthers()
    {
        await using var secure = new FakeEsclDevice(tls: true);
        await using var plain = new FakeEsclDevice();
        await using var noUuid = new FakeEsclDevice { Capabilities = EsclSamples.Capabilities.Replace("<scan:UUID>", "<scan:Other>").Replace("</scan:UUID>", "</scan:Other>") };
        await using var website = new FakeEsclDevice { Handler = _ => FakeAnswer.Xml("<html><body>Anmeldung</body></html>") with { ContentType = "text/html" } };
        int closed;
        await using (var gone = new FakeEsclDevice()) closed = gone.Port;
        var ports = new[] { new EsclProbePort(secure.Port, true), new EsclProbePort(plain.Port, false), new EsclProbePort(noUuid.Port, false),
            new EsclProbePort(website.Port, false), new EsclProbePort(closed, false) };
        var reports = new List<(int Done, int Total)>();
        var log = new EsclDiagnostics();
        var devices = await EsclNetworkSearch.SearchAsync([IPAddress.Loopback, IPAddress.Parse("8.8.8.8")], ports, EsclNetworkSearch.Timeouts,
            new SyncProgress(reports), log);
        var device = Assert.Single(devices);
        Assert.Equal("11111111-2222-3333-4444-555555555555", device.Key);
        Assert.Equal("Beispiel Büro 1000", device.Name);
        Assert.Equal([true, false], device.Services.Select(s => s.Tls));   // TLS first
        Assert.Equal([secure.Port, plain.Port], device.Services.Select(s => s.Port));
        Assert.All(device.Services, s => Assert.Equal("eSCL", s.Root));
        Assert.Equal(device.Key, device.Services[0].Uuid);
        // the address outside the own network is not asked at all
        Assert.Equal(5, reports.Max(r => r.Total));
        Assert.Equal(5, reports.Max(r => r.Done));
        Assert.Contains(log.Entries, e => e.Contains("fertig") && e.Contains("1 Gerät"));
        // reading only: nothing but the description was asked
        Assert.All(secure.Requests.Concat(plain.Requests), r => Assert.Equal(("GET", "/eSCL/ScannerCapabilities"), (r.Method, r.Path)));
    }

    [Fact]
    public async Task Search_Cancelled_ReturnsWithoutError()
    {
        await using var device = new FakeEsclDevice { Handler = _ => FakeAnswer.Xml(EsclSamples.Capabilities) with { Delay = TimeSpan.FromSeconds(10) } };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var devices = await EsclNetworkSearch.SearchAsync([IPAddress.Loopback], [new EsclProbePort(device.Port, false)], EsclNetworkSearch.Timeouts,
            cancel: cancel.Token);
        Assert.Empty(devices);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"dauerte {clock.Elapsed}");
    }

    [Fact]
    public void DefaultRanges_ExpandToHosts()
    {
        var ranges = EsclNetworkRange.ParseList("192.168.10, 192.168.11", out _);
        Assert.Equal(508, ranges.SelectMany(r => r.Hosts).Distinct().Count());
        Assert.DoesNotContain(IPAddress.Parse("192.168.10.0"), ranges[0].Hosts);
        Assert.DoesNotContain(IPAddress.Parse("192.168.10.255"), ranges[0].Hosts);
    }

    private sealed class SyncProgress(List<(int, int)> reports) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value)
        {
            lock (reports) reports.Add(value);
        }
    }
}
