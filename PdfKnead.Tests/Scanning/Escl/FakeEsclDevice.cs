using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfKnead.Core.Scanning.Escl;

namespace PdfKnead.Tests.Scanning.Escl;

/// <summary>A request as the device in software received it.</summary>
internal sealed record FakeRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>
/// What the device in software answers. <paramref name="CutAfter"/> ends the connection after that many body bytes (a device that drops out
/// mid-answer); <paramref name="Chunked"/> sends the body in pieces without a length; <paramref name="Delay"/> waits before the answer.
/// </summary>
internal sealed record FakeAnswer(int Status, byte[] Body, string? ContentType = "text/xml", IReadOnlyList<(string Name, string Value)>? Headers = null,
    bool Chunked = false, int CutAfter = -1, TimeSpan Delay = default)
{
    public static FakeAnswer Xml(string xml, int status = 200) => new(status, Encoding.UTF8.GetBytes(xml));

    public static FakeAnswer Empty(int status, params (string Name, string Value)[] headers) => new(status, [], null, headers);
}

/// <summary>
/// An eSCL scanner in software for the tests (KONZEPT.md §5.25, S9, „Prüfen“): a small HTTP/1.1 server on the loopback address, with or
/// without TLS (a certificate it issues itself, like real devices). It answers the description and the state from <see cref="Capabilities"/>
/// and <see cref="Status"/>; <see cref="Handler"/> replaces any answer to play a device's quirks.
/// </summary>
internal sealed class FakeEsclDevice : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2? _certificate;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly ConcurrentQueue<FakeRequest> _requests = new();
    private readonly bool _rsaOnly;

    /// <param name="tls">With TLS and a certificate the device issued itself.</param>
    /// <param name="root">The resource path.</param>
    /// <param name="rsaKeyExchangeOnly">Under Linux: TLS 1.2 with RSA key exchange only, as many scanners offer it.</param>
    public FakeEsclDevice(bool tls = false, string root = "eSCL", bool rsaKeyExchangeOnly = false)
    {
        Root = root;
        _rsaOnly = rsaKeyExchangeOnly;
        if (tls)
        {
            using AsymmetricAlgorithm key = rsaKeyExchangeOnly ? RSA.Create(2048) : ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = key is RSA rsa ? new CertificateRequest("CN=Beispielscanner", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                : new CertificateRequest("CN=Beispielscanner", (ECDsa)key, HashAlgorithmName.SHA256);
            using var issued = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            // through PKCS#12, so that the key is usable for the server side on every system
            _certificate = X509CertificateLoader.LoadPkcs12(issued.Export(X509ContentType.Pkcs12), null);
            Fingerprint = EsclTrust.Fingerprint(_certificate.RawData);
        }
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public string Root { get; }

    /// <summary>The fingerprint of the device's certificate (with TLS).</summary>
    public string? Fingerprint { get; }

    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public int Port => EndPoint.Port;

    public string Capabilities { get; set; } = EsclSamples.Capabilities;

    public string Status { get; set; } = EsclSamples.StatusIdle;

    /// <summary>Answers instead of the normal ones; null falls back to them.</summary>
    public Func<FakeRequest, FakeAnswer?>? Handler { get; set; }

    /// <summary>The requests received, oldest first.</summary>
    public IReadOnlyList<FakeRequest> Requests => [.. _requests];

    /// <summary>How many connections were accepted.</summary>
    public int Connections;

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
            Interlocked.Increment(ref Connections);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                Stream stream = client.GetStream();
                if (_certificate is not null)
                {
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    var options = new SslServerAuthenticationOptions { ServerCertificate = _certificate };
                    if (_rsaOnly && OperatingSystem.IsLinux())
                    {
                        options.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12;
                        options.CipherSuitesPolicy = new CipherSuitesPolicy([TlsCipherSuite.TLS_RSA_WITH_AES_256_GCM_SHA384]);
                    }
                    await ssl.AuthenticateAsServerAsync(options, _stop.Token);
                    stream = ssl;
                }
                await using (stream)
                {
                    var request = await ReadRequestAsync(stream);
                    if (request is null) return;
                    _requests.Enqueue(request);
                    var answer = Handler?.Invoke(request) ?? Default(request);
                    if (answer.Delay > TimeSpan.Zero) await Task.Delay(answer.Delay, _stop.Token);
                    await WriteAsync(stream, answer);
                }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException
                                          or ObjectDisposedException)
            {
                // the client went away or refused the certificate — as with a real device, nothing more to do
            }
        }
    }

    private FakeAnswer Default(FakeRequest request) => (request.Method, request.Path) switch
    {
        ("GET", var p) when p == $"/{Root}/ScannerCapabilities" => FakeAnswer.Xml(Capabilities),
        ("GET", var p) when p == $"/{Root}/ScannerStatus" => FakeAnswer.Xml(Status),
        _ => FakeAnswer.Empty(404),
    };

    private static async Task<FakeRequest?> ReadRequestAsync(Stream stream)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
        {
            if (await stream.ReadAsync(one) == 0 || head.Count > 65536) return null;
            head.Add(one[0]);
        }
        var lines = Encoding.ASCII.GetString([.. head]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var first = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
            if (line.IndexOf(':') is var colon and > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        var body = Array.Empty<byte>();
        if (headers.TryGetValue("Content-Length", out var length) && int.TryParse(length, CultureInfo.InvariantCulture, out var n) && n > 0)
        {
            body = new byte[n];
            await stream.ReadExactlyAsync(body);
        }
        return new FakeRequest(first[0], first[1], headers, body);
    }

    private static async Task WriteAsync(Stream stream, FakeAnswer answer)
    {
        var head = new StringBuilder($"HTTP/1.1 {answer.Status} {Reason(answer.Status)}\r\nConnection: close\r\n");
        if (answer.ContentType is not null) head.Append($"Content-Type: {answer.ContentType}\r\n");
        foreach (var (name, value) in answer.Headers ?? []) head.Append($"{name}: {value}\r\n");
        head.Append(answer.Chunked ? "Transfer-Encoding: chunked\r\n" : $"Content-Length: {answer.Body.Length}\r\n").Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
        Memory<byte> body = answer.CutAfter >= 0 ? answer.Body.AsMemory(0, Math.Min(answer.CutAfter, answer.Body.Length)) : answer.Body;
        if (answer.Chunked)
        {
            // in pieces of 64 KB, written from the body itself (no copies: the tests measure what the client allocates)
            for (var at = 0; at < body.Length; at += 65536)
            {
                var length = Math.Min(65536, body.Length - at);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"{length:X}\r\n"));
                await stream.WriteAsync(body.Slice(at, length));
                await stream.WriteAsync(CrLf);
            }
            if (answer.CutAfter < 0) await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
        }
        else await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static readonly byte[] CrLf = "\r\n"u8.ToArray();

    private static string Reason(int status) => status switch
    {
        200 => "OK", 201 => "Created", 301 => "Moved Permanently", 302 => "Found", 307 => "Temporary Redirect", 401 => "Unauthorized",
        404 => "Not Found", 409 => "Conflict", 410 => "Gone", 500 => "Internal Server Error", 503 => "Service Unavailable", _ => "Status",
    };

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }
        _certificate?.Dispose();
        _stop.Dispose();
    }
}
