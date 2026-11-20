using System.Net;
using System.Text;
using PdfKnead.Core.Scanning;
using PdfKnead.Core.Scanning.Escl;

namespace PdfKnead.Tests.Scanning.Escl;

/// <summary>The HTTP/HTTPS access to an eSCL scanner against the device in software (KONZEPT.md §5.25, S9b).</summary>
[Collection(nameof(EsclDeviceCollection))]
public sealed class EsclClientTests
{
    private static readonly EsclTimeouts Quick = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));

    private static EsclClient Client(FakeEsclDevice device, bool tls = false, IEsclCertificatePins? pins = null, EsclTrustMode mode = EsclTrustMode.Pinned,
        EsclDiagnostics? log = null) =>
        new(device.EndPoint, tls, device.Root, "geraet-1", pins, mode, log, Quick);

    [Fact]
    public async Task Http_CapabilitiesAndStatus()
    {
        await using var device = new FakeEsclDevice();
        var log = new EsclDiagnostics();
        using var client = Client(device, log: log);
        var caps = await client.GetCapabilitiesAsync(CancellationToken.None);
        Assert.Equal("Beispiel Büro 1000", caps.MakeAndModel);
        var status = await client.GetStatusAsync(CancellationToken.None);
        Assert.Equal(EsclFeederState.Loaded, status.Feeder);
        Assert.Equal(["/eSCL/ScannerCapabilities", "/eSCL/ScannerStatus"], device.Requests.Select(r => r.Path));
        Assert.All(device.Requests, r => Assert.Equal($"127.0.0.1:{device.Port}", r.Headers["Host"]));
        Assert.Contains(log.Entries, e => e.Contains("ScannerCapabilities → 200"));
        Assert.Contains(log.Entries, e => e.Contains("Beispiel Büro 1000"));   // the description, shortened, for the diagnosis
    }

    [Fact]
    public async Task OtherRoot_AndChunkedAnswer()
    {
        await using var device = new FakeEsclDevice(root: "eSCL/v2");
        device.Handler = r => r.Path == "/eSCL/v2/ScannerStatus" ? FakeAnswer.Xml(EsclSamples.StatusWithJobs) with { Chunked = true } : null;
        using var client = Client(device);
        Assert.Equal(2, (await client.GetStatusAsync(CancellationToken.None)).Jobs.Count);
    }

    [Theory]
    [InlineData(503, ScanErrorKind.Busy)]
    [InlineData(401, ScanErrorKind.Other)]
    [InlineData(500, ScanErrorKind.Other)]
    [InlineData(404, ScanErrorKind.Other)]
    public async Task ErrorStatus_Named(int status, ScanErrorKind kind)
    {
        await using var device = new FakeEsclDevice { Handler = _ => FakeAnswer.Empty(status) };
        using var client = Client(device);
        var e = await Assert.ThrowsAnyAsync<ScanException>(() => client.GetCapabilitiesAsync(CancellationToken.None));
        Assert.Equal(kind, e.Kind);
    }

    [Fact]
    public async Task Unreadable_And_TooLarge()
    {
        await using var device = new FakeEsclDevice { Handler = _ => FakeAnswer.Xml("<html>keine Beschreibung</html>") };
        using var client = Client(device);
        await Assert.ThrowsAnyAsync<ScanException>(() => client.GetCapabilitiesAsync(CancellationToken.None));
        // too large with and without a length in advance: the reading stops at the limit
        var big = new byte[EsclStatus.MaxBytes + 1];
        device.Handler = _ => new FakeAnswer(200, big);
        var e = await Assert.ThrowsAnyAsync<ScanException>(() => client.GetStatusAsync(CancellationToken.None));
        Assert.Contains("zu große", e.Message);
        device.Handler = _ => new FakeAnswer(200, big, Chunked: true);
        e = await Assert.ThrowsAnyAsync<ScanException>(() => client.GetStatusAsync(CancellationToken.None));
        Assert.Contains("zu große", e.Message);
    }


    [Fact]
    public async Task CutAnswer_IsDisturbedConnection()
    {
        await using var device = new FakeEsclDevice();
        device.Handler = _ => FakeAnswer.Xml(EsclSamples.Capabilities) with { CutAfter = 300 };
        using var client = Client(device);
        var e = await Assert.ThrowsAnyAsync<ScanException>(() => client.GetCapabilitiesAsync(CancellationToken.None));
        Assert.Equal(ScanErrorKind.Offline, e.Kind);
        device.Handler = _ => FakeAnswer.Xml(EsclSamples.Capabilities) with { CutAfter = 300, Chunked = true };
        e = await Assert.ThrowsAnyAsync<ScanException>(() => client.GetCapabilitiesAsync(CancellationToken.None));
        Assert.Equal(ScanErrorKind.Offline, e.Kind);
    }

    [Fact]
    public async Task TimeLimit_And_UserCancel_StayApart()
    {
        await using var device = new FakeEsclDevice();
        device.Handler = _ => FakeAnswer.Xml(EsclSamples.StatusIdle) with { Delay = TimeSpan.FromSeconds(10) };
        using var client = Client(device);
        var e = await Assert.ThrowsAnyAsync<ScanException>(() => client.GetStatusAsync(CancellationToken.None));
        Assert.Equal(ScanErrorKind.Offline, e.Kind);
        Assert.Contains("rechtzeitig", e.Message);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetStatusAsync(cancel.Token));
    }

    [Fact]
    public async Task NotListening_IsOffline()
    {
        int port;
        await using (var gone = new FakeEsclDevice()) port = gone.Port;
        using var client = new EsclClient(new IPEndPoint(IPAddress.Loopback, port), false, "eSCL", "x", null, timeouts: Quick);
        var e = await Assert.ThrowsAnyAsync<ScanException>(() => client.GetStatusAsync(CancellationToken.None));
        Assert.Equal(ScanErrorKind.Offline, e.Kind);
    }

    [Fact]
    public void Outside_OwnNetwork_Refused()
    {
        Assert.ThrowsAny<ScanException>(() => new EsclClient(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 80), false, "eSCL", "x", null));
        Assert.ThrowsAny<ScanException>(() => new EsclClient(new IPEndPoint(IPAddress.Parse("::ffff:1.2.3.4"), 80), false, "eSCL", "x", null));
    }

    [Fact]
    public void Resolve_OnlyThisDevice()
    {
        using var client = new EsclClient(new IPEndPoint(IPAddress.Parse("192.168.1.20"), 8080), false, "eSCL", "x", null);
        Assert.Equal("http://192.168.1.20:8080/eSCL/ScannerStatus", client.Resolve("ScannerStatus")?.ToString());
        Assert.Equal("http://192.168.1.20:8080/ScanJobs/1", client.Resolve("/ScanJobs/1")?.ToString());
        Assert.Equal("http://192.168.1.20:8080/eSCL/ScanJobs/1", client.Resolve("https://192.168.1.20/eSCL/ScanJobs/1")?.ToString());
        Assert.Null(client.Resolve("http://192.168.1.21/eSCL/ScanJobs/1"));       // another device
        Assert.Null(client.Resolve("http://scanner.example/eSCL/ScanJobs/1"));    // a name
        Assert.Null(client.Resolve("ftp://192.168.1.20/x"));
        Assert.Null(client.Resolve("http://user@192.168.1.20/x"));
        Assert.Null(client.Resolve("/eSCL/../admin"));
        Assert.Null(client.Resolve("/eSCL/%2e%2e/admin"));
        Assert.Null(client.Resolve("/eSCL/a%2Fb"));
        Assert.Null(client.Resolve("//192.168.1.21/x"));
        Assert.Null(client.Resolve("/x?y=1"));
        Assert.Null(client.Resolve("/x y"));
        using var v6 = new EsclClient(new IPEndPoint(IPAddress.Parse("fe80::1%2"), 443), true, "eSCL", "x", null);
        Assert.Equal("https://[fe80::1]/eSCL/ScannerStatus", v6.Resolve("ScannerStatus")?.ToString());
        Assert.NotNull(v6.Resolve("https://[fe80::1%253]/eSCL/x"));
    }

    [Fact]
    public async Task Redirect_SameAddressFollowed_OtherRefused()
    {
        await using var device = new FakeEsclDevice();
        device.Handler = r => r.Path switch
        {
            "/eSCL/ScannerStatus" => FakeAnswer.Empty(301, ("Location", "/neu/ScannerStatus")),
            "/neu/ScannerStatus" => FakeAnswer.Xml(EsclSamples.StatusIdle),
            "/eSCL/ScannerCapabilities" => FakeAnswer.Empty(302, ("Location", "http://192.168.1.99/eSCL/ScannerCapabilities")),
            _ => null,
        };
        using var client = Client(device);
        Assert.Equal(EsclScannerState.Idle, (await client.GetStatusAsync(CancellationToken.None)).State);
        var before = device.Connections;
        var e = await Assert.ThrowsAnyAsync<ScanException>(() => client.GetCapabilitiesAsync(CancellationToken.None));
        Assert.Contains("andere Adresse", e.Message);
        Assert.Equal(before + 1, device.Connections);   // nothing went anywhere else
        // a loop ends at the limit
        device.Handler = _ => FakeAnswer.Empty(307, ("Location", "/eSCL/ScannerStatus"));
        await Assert.ThrowsAnyAsync<ScanException>(() => client.GetStatusAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Redirect_ToHttps_SwitchesForGood_NeverBack()
    {
        await using var secure = new FakeEsclDevice(tls: true);
        await using var plain = new FakeEsclDevice();
        plain.Handler = r => FakeAnswer.Empty(301, ("Location", $"https://127.0.0.1:{secure.Port}{r.Path}"));
        var pins = new EsclMemoryPins();
        pins.Set("geraet-1", secure.Fingerprint!);
        using var client = Client(plain, pins: pins);
        Assert.Equal(EsclScannerState.Idle, (await client.GetStatusAsync(CancellationToken.None)).State);
        Assert.True(client.Tls);
        Assert.NotNull(await client.GetCapabilitiesAsync(CancellationToken.None));
        Assert.Single(plain.Requests);   // the second request went straight to HTTPS
        secure.Handler = _ => FakeAnswer.Empty(301, ("Location", $"http://127.0.0.1:{plain.Port}/eSCL/ScannerStatus"));
        await Assert.ThrowsAnyAsync<ScanException>(() => client.GetStatusAsync(CancellationToken.None));
        Assert.Single(plain.Requests);
    }

    [Fact]
    public async Task Https_FirstContact_ConfirmThenPinned_ChangeDetected()
    {
        await using var device = new FakeEsclDevice(tls: true);
        var pins = new EsclMemoryPins();
        using (var client = Client(device, tls: true, pins: pins))
        {
            var first = await Assert.ThrowsAsync<EsclCertificateException>(() => client.GetStatusAsync(CancellationToken.None));
            Assert.Equal(device.Fingerprint, first.Fingerprint);
            Assert.Null(first.Previous);
            Assert.Equal("geraet-1", first.Key);
            Assert.Empty(device.Requests);   // nothing sent before the certificate is confirmed
            pins.Set(first.Key, first.Fingerprint);
            Assert.Equal(EsclScannerState.Idle, (await client.GetStatusAsync(CancellationToken.None)).State);
        }
        // another device under the same name: the change is named with both fingerprints
        await using var other = new FakeEsclDevice(tls: true);
        using var again = Client(other, tls: true, pins: pins);
        var changed = await Assert.ThrowsAsync<EsclCertificateException>(() => again.GetStatusAsync(CancellationToken.None));
        Assert.Equal(other.Fingerprint, changed.Fingerprint);
        Assert.Equal(device.Fingerprint, changed.Previous);
        Assert.Empty(other.Requests);
        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", EsclTrust.Display(changed.Fingerprint));
    }

    [Fact]
    public async Task Https_RsaKeyExchangeOnly_Connects()
    {
        // Fehlergedächtnis 951: a device offering only TLS 1.2 with RSA key exchange — under Linux refused by the runtime's default suites
        if (!OperatingSystem.IsLinux()) return;
        await using var device = new FakeEsclDevice(tls: true, rsaKeyExchangeOnly: true);
        var pins = new EsclMemoryPins();
        pins.Set("geraet-1", device.Fingerprint!);
        using var client = Client(device, tls: true, pins: pins);
        Assert.Equal(EsclScannerState.Idle, (await client.GetStatusAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task ReadOnly_AnyCertificate_OnlyReading()
    {
        await using var device = new FakeEsclDevice(tls: true);
        using var client = Client(device, tls: true, mode: EsclTrustMode.ReadOnly);
        Assert.Equal("Beispiel Büro 1000", (await client.GetCapabilitiesAsync(CancellationToken.None)).MakeAndModel);
        Assert.Equal(device.Fingerprint, client.PresentedFingerprint);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(HttpMethod.Post, "ScanJobs", "<x/>"u8.ToArray(), "text/xml", 1000,
            TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Single(device.Requests);
    }

    [Fact]
    public async Task Answer_HeadersRead()
    {
        await using var device = new FakeEsclDevice
        {
            Handler = _ => FakeAnswer.Empty(503, ("Retry-After", "7"), ("Location", "/eSCL/ScanJobs/5"), ("Content-Location", "/eSCL/ScanJobs/5/1")),
        };
        using var client = Client(device);
        var answer = await client.SendAsync(HttpMethod.Post, "ScanJobs", Encoding.UTF8.GetBytes("<x/>"), "text/xml", 1000, TimeSpan.FromSeconds(2),
            CancellationToken.None);
        Assert.Equal(503, answer.Status);
        Assert.Equal(TimeSpan.FromSeconds(7), answer.RetryAfter);
        Assert.Equal("/eSCL/ScanJobs/5", answer.Location);
        Assert.Equal("/eSCL/ScanJobs/5/1", answer.ContentLocation);
        var request = Assert.Single(device.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("<x/>", Encoding.UTF8.GetString(request.Body));
        Assert.StartsWith("text/xml", request.Headers["Content-Type"]);
    }
}
