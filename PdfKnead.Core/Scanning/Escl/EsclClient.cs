using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace PdfKnead.Core.Scanning.Escl;

/// <summary>The time limits of the eSCL access, one per kind of wait (KONZEPT.md §5.25, S9, „Übertragung“).</summary>
/// <param name="Connect">To open the connection.</param>
/// <param name="Request">For a whole request that only asks or controls (description, state, creating and deleting a job).</param>
/// <param name="PageWait">Until the device starts handing out the next page (the device may scan meanwhile).</param>
/// <param name="Block">Between two blocks of a page being received.</param>
public sealed record EsclTimeouts(TimeSpan Connect, TimeSpan Request, TimeSpan PageWait, TimeSpan Block)
{
    /// <summary>5 s to connect, 15 s per request, 5 minutes for a page to begin, 60 s per block.</summary>
    public static readonly EsclTimeouts Default = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(60));
}

/// <summary>How far a page has arrived.</summary>
/// <param name="Received">The bytes received.</param>
/// <param name="Length">The length the device announced, or null.</param>
/// <param name="Rows">The complete image rows in what has arrived (a JPEG in scan order), 0 if not known.</param>
/// <param name="Height">The height of the image from its header, 0 if not known yet.</param>
public readonly record struct EsclTransfer(long Received, long? Length, int Rows = 0, int Height = 0);

/// <summary>An answer of the device.</summary>
/// <param name="Status">The HTTP status code.</param>
/// <param name="Body">The body, at most the size the request allowed.</param>
/// <param name="ContentType">The media type of the body in lower case, without parameters.</param>
/// <param name="Location">The Location header as sent.</param>
/// <param name="ContentLocation">The Content-Location header as sent.</param>
/// <param name="RetryAfter">How long the device asks to wait, if it says.</param>
public sealed record EsclResponse(int Status, byte[] Body, string? ContentType, string? Location, string? ContentLocation, TimeSpan? RetryAfter);

/// <summary>
/// Talks HTTP or HTTPS to one eSCL scanner (KONZEPT.md §5.25, S9b). Every connection goes to the one address given at the start, which must
/// lie in the own network; the host in the requests is only a name for it. No proxy, no cookies, no automatic redirects: a redirect is
/// followed only to the same address (from HTTP to HTTPS, not back). The certificate of an HTTPS device must be the one the user confirmed
/// (<see cref="IEsclCertificatePins"/>); otherwise <see cref="EsclCertificateException"/> says which one it presented. User cancellation and
/// time limits stay apart: the first ends with <see cref="OperationCanceledException"/>, the second with a <see cref="ScanException"/>.
/// </summary>
public sealed class EsclClient : IDisposable
{
    private const int MaxRedirects = 3;

    /// <summary>
    /// The TLS cipher suites under Linux: the modern ones first, then — as the last choice — those with RSA key exchange, which many scanners
    /// offer as their only ones (Fehlergedächtnis 951). The runtime allows only suites with forward secrecy by default, and the
    /// connection to such a device fails. Without forward secrecy, a recorded connection could be read later by whoever obtains the device's
    /// key; for a scanner in the own network with a confirmed certificate that is still far better than the plain HTTP that would remain.
    /// Never suites without encryption, with RC4, 3DES or export strength. Under Windows the system decides and allows these suites itself.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static CipherSuitesPolicy DeviceCipherSuites => _deviceCipherSuites ??= new CipherSuitesPolicy(
    [
        TlsCipherSuite.TLS_AES_128_GCM_SHA256, TlsCipherSuite.TLS_AES_256_GCM_SHA384, TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256, TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256, TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256, TlsCipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256, TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA384,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA256, TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA384,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA, TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA, TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA,
        TlsCipherSuite.TLS_DHE_RSA_WITH_AES_128_GCM_SHA256, TlsCipherSuite.TLS_DHE_RSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_RSA_WITH_AES_128_GCM_SHA256, TlsCipherSuite.TLS_RSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_RSA_WITH_AES_128_CBC_SHA256, TlsCipherSuite.TLS_RSA_WITH_AES_256_CBC_SHA256,
        TlsCipherSuite.TLS_RSA_WITH_AES_128_CBC_SHA, TlsCipherSuite.TLS_RSA_WITH_AES_256_CBC_SHA,
    ]);

    private static CipherSuitesPolicy? _deviceCipherSuites;

    private readonly IPEndPoint _address;
    private readonly string _host;
    private readonly string _trustKey;
    private readonly IEsclCertificatePins? _pins;
    private readonly EsclTrustMode _mode;
    private readonly EsclDiagnostics? _log;
    private readonly HttpClient _http;
    private readonly Lock _lock = new();
    private string? _presented;
    private bool _tls;
    private int _port;

    /// <summary>Prepares the access to a scanner; nothing is sent yet.</summary>
    /// <param name="address">The device's address and port (link-local IPv6 with its zone).</param>
    /// <param name="tls">Whether to use HTTPS.</param>
    /// <param name="root">The resource path, e.g. „eSCL“.</param>
    /// <param name="trustKey">The device for the confirmed certificate: its UUID, else host and port.</param>
    /// <param name="pins">The confirmed certificates; without them every HTTPS request asks for confirmation (in <see cref="EsclTrustMode.Pinned"/>).</param>
    /// <param name="mode">How the certificate is treated.</param>
    /// <param name="log">Where requests and answers are noted, or null.</param>
    /// <param name="timeouts">The time limits; <see cref="EsclTimeouts.Default"/> if null.</param>
    /// <exception cref="ScanException">The address lies outside the own network.</exception>
    public EsclClient(IPEndPoint address, bool tls, string root, string trustKey, IEsclCertificatePins? pins, EsclTrustMode mode = EsclTrustMode.Pinned,
        EsclDiagnostics? log = null, EsclTimeouts? timeouts = null)
    {
        if (!EsclNetwork.IsLocal(address.Address))
            throw new ScanException(Tr.T("„{0}“ liegt nicht im eigenen Netzwerk. Scanner werden nur im eigenen Netzwerk angesprochen.", address.Address));
        _address = address;
        _tls = tls;
        _port = address.Port;
        Root = EsclSearch.SafeRoot(root) ?? "eSCL";
        _trustKey = trustKey;
        _pins = pins;
        _mode = mode;
        _log = log;
        Timeouts = timeouts ?? EsclTimeouts.Default;
        var plain = address.Address.IsIPv4MappedToIPv6 ? address.Address.MapToIPv4() : address.Address;
        _host = plain.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{new IPAddress(plain.GetAddressBytes())}]" : plain.ToString();
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = Timeouts.Connect,
            MaxResponseHeadersLength = 64,   // kilobytes
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectCallback = ConnectAsync,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) => Accept(certificate),
                CipherSuitesPolicy = OperatingSystem.IsLinux() ? DeviceCipherSuites : null,
            },
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>The resource path of the device.</summary>
    public string Root { get; }

    /// <summary>The closing of the last job at the device, while it runs in the background (<see cref="EsclScanJob"/>); the next job waits for it.</summary>
    public Task? Closing { get; internal set; }

    /// <summary>The time limits.</summary>
    public EsclTimeouts Timeouts { get; }

    /// <summary>Whether requests go over HTTPS (a redirect from HTTP to HTTPS switches for good).</summary>
    public bool Tls
    {
        get
        {
            lock (_lock) return _tls;
        }
    }

    /// <summary>The fingerprint of the certificate the device presented last, or null (no HTTPS contact yet).</summary>
    public string? PresentedFingerprint
    {
        get
        {
            lock (_lock) return _presented;
        }
    }

    /// <summary>The address of a resource: below the resource path for a relative name, as given for a path from the device.</summary>
    /// <param name="resource">„ScannerCapabilities“, or a path such as „/eSCL/ScanJobs/1“, or a full address naming this device.</param>
    /// <returns>The address, or null if the text names another host, another scheme, or an unsafe path.</returns>
    public Uri? Resolve(string resource)
    {
        string path;
        if (resource.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(resource, UriKind.Absolute, out var absolute) || absolute.Scheme is not ("http" or "https")
                || absolute.UserInfo.Length > 0 || !SameHost(absolute))
                return null;
            path = absolute.AbsolutePath;
        }
        else path = resource.StartsWith('/') ? resource : $"/{Root}/{resource}";
        if (path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\') || path.Contains('?') || path.Contains('#')
            || path.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || path.Length > 1024)
            return null;
        var segments = path.Split('/');
        if (segments.Any(s => s is "." or ".." || Uri.UnescapeDataString(s) is "." or ".." || Uri.UnescapeDataString(s).Contains('/'))) return null;
        bool tls;
        int port;
        lock (_lock) (tls, port) = (_tls, _port);
        return Uri.TryCreate($"{(tls ? "https" : "http")}://{_host}:{port.ToString(CultureInfo.InvariantCulture)}{path}", UriKind.Absolute, out var uri)
            ? uri : null;
    }

    private bool SameHost(Uri uri)
    {
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host.Trim('[', ']') : uri.Host;
        var percent = host.IndexOf('%');
        if (percent >= 0) host = host[..percent];
        var mine = _address.Address.IsIPv4MappedToIPv6 ? _address.Address.MapToIPv4() : _address.Address;
        return IPAddress.TryParse(host, out var other) && new IPAddress(other.GetAddressBytes()).Equals(new IPAddress(mine.GetAddressBytes()));
    }

    /// <summary>Asks for a resource.</summary>
    /// <param name="resource">The resource (<see cref="Resolve"/>).</param>
    /// <param name="maxBytes">The largest body accepted.</param>
    /// <param name="cancel">Cancels.</param>
    /// <returns>The answer, whatever its status.</returns>
    /// <exception cref="ScanException">Not reachable, time limit, broken connection, an answer too large, or an unconfirmed certificate.</exception>
    public Task<EsclResponse> GetAsync(string resource, int maxBytes, CancellationToken cancel = default) =>
        SendAsync(HttpMethod.Get, resource, null, null, maxBytes, Timeouts.Request, cancel);

    /// <summary>Sends a request.</summary>
    /// <param name="method">The method.</param>
    /// <param name="resource">The resource (<see cref="Resolve"/>).</param>
    /// <param name="body">The body, or null.</param>
    /// <param name="contentType">The media type of the body.</param>
    /// <param name="maxBytes">The largest body of the answer accepted.</param>
    /// <param name="timeout">The time limit for the whole request.</param>
    /// <param name="cancel">Cancels.</param>
    /// <returns>The answer, whatever its status.</returns>
    /// <exception cref="ScanException">Not reachable, time limit, broken connection, an answer too large, or an unconfirmed certificate.</exception>
    /// <exception cref="InvalidOperationException">Anything but reading in <see cref="EsclTrustMode.ReadOnly"/>.</exception>
    public Task<EsclResponse> SendAsync(HttpMethod method, string resource, byte[]? body, string? contentType, int maxBytes, TimeSpan timeout,
        CancellationToken cancel = default) =>
        SendCoreAsync(method, resource, body, contentType, maxBytes, timeout, null, null, cancel);

    /// <summary>
    /// Fetches a page: waits up to <see cref="EsclTimeouts.PageWait"/> for the device to begin (it may still be scanning), then up to
    /// <see cref="EsclTimeouts.Block"/> for each further block of data.
    /// </summary>
    /// <param name="resource">The page resource (<see cref="Resolve"/>).</param>
    /// <param name="maxBytes">The largest page accepted.</param>
    /// <param name="progress">Receives the bytes received, the length if announced, and the complete image rows of a JPEG page (now and
    /// then, not per block: rows at most every 1.5 s).</param>
    /// <param name="cancel">Cancels.</param>
    /// <returns>The answer, whatever its status.</returns>
    /// <exception cref="ScanException">Not reachable, time limit, broken connection, a page too large, or an unconfirmed certificate.</exception>
    public Task<EsclResponse> GetDocumentAsync(string resource, int maxBytes, IProgress<EsclTransfer>? progress, CancellationToken cancel = default) =>
        SendCoreAsync(HttpMethod.Get, resource, null, null, maxBytes, Timeouts.PageWait, Timeouts.Block, progress, cancel);

    private async Task<EsclResponse> SendCoreAsync(HttpMethod method, string resource, byte[]? body, string? contentType, int maxBytes,
        TimeSpan timeout, TimeSpan? block, IProgress<EsclTransfer>? progress, CancellationToken cancel)
    {
        if (_mode == EsclTrustMode.ReadOnly && method != HttpMethod.Get)
            throw new InvalidOperationException("Ohne bestätigtes Zertifikat wird nur gelesen.");
        var uri = Resolve(resource) ?? throw new EsclProtocolException(Tr.T("Der Scanner nennt eine ungültige Adresse."));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        var clock = Stopwatch.StartNew();
        try
        {
            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(method, uri) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                if (body is not null)
                {
                    request.Content = new ByteArrayContent(body);
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "text/xml");
                }
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                var location = response.Headers.Location?.OriginalString;
                if (status is 301 or 302 or 303 or 307 or 308 && location is not null)
                {
                    var target = Redirect(uri, location);
                    _log?.Add("Antwort", $"{method} {uri.AbsolutePath} → {status}, weiter nach {location}{(target is null ? " — abgelehnt" : "")}");
                    if (target is null || hop >= MaxRedirects) throw new EsclProtocolException(Tr.T("Der Scanner leitet auf eine andere Adresse um — das wird aus Sicherheitsgründen nicht befolgt."));
                    uri = target;
                    if (status is 303) (method, body) = (HttpMethod.Get, null);
                    continue;
                }
                var data = await ReadAsync(response.Content, maxBytes, limit, block, progress).ConfigureAwait(false);
                var answer = new EsclResponse(status, data, response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant(), location,
                    response.Content.Headers.ContentLocation?.OriginalString, RetryAfter(response.Headers.RetryAfter));
                _log?.Add("Antwort", $"{method} {uri.AbsolutePath} → {status}, {data.Length} Bytes, {clock.ElapsedMilliseconds} ms");
                return answer;
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            _log?.Add("Anfrage", $"{method} {uri.AbsolutePath} abgebrochen");
            throw;
        }
        catch (OperationCanceledException)
        {
            _log?.Add("Anfrage", $"{method} {uri.AbsolutePath}: Zeitgrenze überschritten");
            throw new EsclProtocolException(Tr.T("Der Scanner antwortet nicht rechtzeitig."), ScanErrorKind.Offline);
        }
        catch (HttpRequestException e)
        {
            var presented = PresentedFingerprint;
            var pinned = _pins?.Get(_trustKey);
            if (_mode == EsclTrustMode.Pinned && presented is not null && FindInner<AuthenticationException>(e) is not null
                && !string.Equals(presented, pinned, StringComparison.OrdinalIgnoreCase))
            {
                _log?.Add("Zertifikat", $"{(pinned is null ? "noch nicht bestätigt" : "geändert")}: {EsclTrust.Display(presented)}");
                throw new EsclCertificateException(_trustKey, presented, pinned);
            }
            _log?.Add("Anfrage", $"{method} {uri.AbsolutePath} gescheitert: {e.HttpRequestError} {FindInner<SocketException>(e)?.SocketErrorCode}");
            throw e.HttpRequestError is HttpRequestError.ConnectionError || FindInner<SocketException>(e) is not null
                ? new ScanException(Tr.T("Der Scanner ist nicht erreichbar — ist er eingeschaltet?"), ScanErrorKind.Offline)
                : new EsclProtocolException(Tr.T("Die Verbindung zum Scanner ist gestört."), ScanErrorKind.Offline);
        }
        catch (IOException)
        {
            _log?.Add("Anfrage", $"{method} {uri.AbsolutePath}: Verbindung mitten in der Antwort beendet");
            throw new EsclProtocolException(Tr.T("Die Verbindung zum Scanner ist gestört."), ScanErrorKind.Offline);
        }
    }

    private Uri? Redirect(Uri from, string location)
    {
        if (!Uri.TryCreate(from, location, out var target) || target.Scheme is not ("http" or "https") || target.UserInfo.Length > 0 || !SameHost(target))
            return null;
        lock (_lock)
        {
            if (_tls && target.Scheme == "http") return null;   // never from HTTPS back to HTTP
            if (!_tls && target.Scheme == "https") _log?.Add("Anfrage", "der Scanner verlangt HTTPS — weiter verschlüsselt");
            _tls = target.Scheme == "https";
            _port = target.Port;
        }
        return Resolve(target.AbsolutePath);
    }

    // with a block limit, the deadline starts anew before every read: a slow device that keeps sending is not cut off. A page of announced
    // length is read straight into an array of that size; otherwise the blocks are kept as they come and joined once — never a growing buffer
    // and a copy of it on top (a page of 60 MB took up to 190 MB that way).
    private static async Task<byte[]> ReadAsync(HttpContent content, int maxBytes, CancellationTokenSource limit, TimeSpan? block,
        IProgress<EsclTransfer>? progress)
    {
        // told every 256 KB: a page of 60 MB arrives in thousands of blocks, the window needs a few hundred updates at most; the rows of a
        // JPEG are counted at most every 1.5 s on the data so far (Fehlergedächtnis 957: devices stream the page while they scan)
        long told = 0;
        var counted = System.Diagnostics.Stopwatch.StartNew();
        (int Rows, int Height) rows = default;
        var countable = true;   // no longer once 64 KB give no header of a countable JPEG (PNG, progressive): no copies for nothing
        void Tell(long received, long? total, Func<(byte[] Data, int Length, bool Rented)>? prefix)
        {
            if (progress is null || (received - told < 256 << 10 && received != total)) return;
            told = received;
            // the first count as soon as data is there (once the header has arrived), then at most every 1.5 s
            if (prefix is not null && countable && (rows.Height == 0 || counted.ElapsedMilliseconds >= 1500))
            {
                counted.Restart();
                var (data, length, rented) = prefix();
                try
                {
                    if (Pdf.Filters.Jpeg.JpegLines.Count(data, length) is { } r) rows = r;
                    else if (rows.Height == 0 && length >= 64 << 10) countable = false;
                }
                finally
                {
                    if (rented) System.Buffers.ArrayPool<byte>.Shared.Return(data);
                }
            }
            progress.Report(new EsclTransfer(received, total, rows.Rows, rows.Height));
        }
        var length = content.Headers.ContentLength;
        if (length > maxBytes) throw new EsclProtocolException(Tr.T("Der Scanner hat eine zu große Antwort geschickt."));
        if (block is { } b) limit.CancelAfter(b);
        await using var stream = await content.ReadAsStreamAsync(limit.Token);
        if (length is { } known)
        {
            var exact = new byte[known];
            var at = 0;
            while (at < exact.Length)
            {
                var read = await stream.ReadAsync(exact.AsMemory(at), limit.Token).ConfigureAwait(false);
                if (read == 0) throw new IOException("Antwort kürzer als angekündigt");
                at += read;
                if (block is { } next) limit.CancelAfter(next);
                var upTo = at;
                Tell(at, known, () => (exact, upTo, false));
            }
            return exact;
        }
        var blocks = new List<byte[]>();
        long total = 0;
        while (true)
        {
            var chunk = new byte[blocks.Count == 0 ? 64 << 10 : 1 << 20];   // small answers stay small
            var filled = 0;
            int read;
            while (filled < chunk.Length && (read = await stream.ReadAsync(chunk.AsMemory(filled), limit.Token).ConfigureAwait(false)) > 0)
            {
                filled += read;
                if (total + filled > maxBytes) throw new EsclProtocolException(Tr.T("Der Scanner hat eine zu große Antwort geschickt."));
                if (block is { } next) limit.CancelAfter(next);
                var current = chunk;
                var inChunk = filled;
                Tell(total + filled, null, () => Joined(blocks, total, current, inChunk));
            }
            if (filled == 0) break;
            blocks.Add(filled == chunk.Length ? chunk : chunk[..filled]);
            total += filled;
            if (filled < chunk.Length) break;
        }
        var result = new byte[total];
        var offset = 0;
        foreach (var piece in blocks)
        {
            piece.CopyTo(result, offset);
            offset += piece.Length;
        }
        return result;
    }

    // the blocks so far and the part of the current one as one piece, in a borrowed array
    private static (byte[] Data, int Length, bool Rented) Joined(List<byte[]> blocks, long total, byte[] current, int filled)
    {
        var length = (int)(total + filled);
        var data = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
        var at = 0;
        foreach (var piece in blocks)
        {
            piece.CopyTo(data, at);
            at += piece.Length;
        }
        current.AsSpan(0, filled).CopyTo(data.AsSpan(at));
        return (data, length, true);
    }

    private static TimeSpan? RetryAfter(RetryConditionHeaderValue? value)
    {
        if (value?.Delta is { } delta) return delta;
        if (value?.Date is { } date) return date - DateTimeOffset.UtcNow is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        return null;
    }

    private static T? FindInner<T>(Exception? e) where T : Exception
    {
        for (var depth = 0; e is not null && depth < 8; e = e.InnerException, depth++)
            if (e is T found) return found;
        return null;
    }

    private bool Accept(System.Security.Cryptography.X509Certificates.X509Certificate? certificate)
    {
        if (certificate is null) return false;
        var fingerprint = EsclTrust.Fingerprint(certificate.GetRawCertData());
        lock (_lock) _presented = fingerprint;
        if (_mode == EsclTrustMode.ReadOnly) return true;
        return string.Equals(_pins?.Get(_trustKey), fingerprint, StringComparison.OrdinalIgnoreCase);
    }

    // every connection goes to the one address given at the start, whatever host the request names
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancel)
    {
        if (!string.Equals(context.DnsEndPoint.Host.Trim('[', ']'), _host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException(HttpRequestError.ConnectionError, "fremder Rechner");
        var socket = new Socket(_address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(_address.Address, context.DnsEndPoint.Port), cancel).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Reads the device's capabilities.</summary>
    /// <param name="cancel">Cancels.</param>
    /// <returns>The capabilities.</returns>
    /// <exception cref="ScanException">Not reachable, refused, or an answer that is no readable description.</exception>
    public async Task<EsclCapabilities> GetCapabilitiesAsync(CancellationToken cancel = default)
    {
        var answer = await GetAsync("ScannerCapabilities", EsclCapabilities.MaxBytes, cancel).ConfigureAwait(false);
        Check(answer);
        _log?.Add("Fähigkeiten", EsclXml.ForLog(answer.Body));
        var caps = EsclCapabilities.Read(answer.Body) ?? throw Unreadable();
        foreach (var u in caps.Unknown) _log?.Add("Fähigkeiten", "nicht verstanden: " + u);
        return caps;
    }

    /// <summary>Reads the device's state.</summary>
    /// <param name="cancel">Cancels.</param>
    /// <returns>The state.</returns>
    /// <exception cref="ScanException">Not reachable, refused, or an answer that is no readable state.</exception>
    public async Task<EsclStatus> GetStatusAsync(CancellationToken cancel = default)
    {
        var answer = await GetAsync("ScannerStatus", EsclStatus.MaxBytes, cancel).ConfigureAwait(false);
        Check(answer);
        var status = EsclStatus.Read(answer.Body) ?? throw Unreadable();
        _log?.Add("Zustand", $"{status.State}, Einzug {status.Feeder}, {status.Jobs.Count} Auftrag/Aufträge"
            + (status.Unknown.Count > 0 ? "; nicht verstanden: " + string.Join(", ", status.Unknown) : ""));
        return status;
    }

    private EsclProtocolException Unreadable()
    {
        _log?.Add("Antwort", "unverständlich (kein passendes XML, zu groß oder fehlerhaft)");
        return new EsclProtocolException(Tr.T("Der Scanner hat eine unverständliche Antwort geschickt."));
    }

    private static void Check(EsclResponse answer)
    {
        switch (answer.Status)
        {
            case 200:
                return;
            case 401 or 403:
                throw new EsclProtocolException(Tr.T("Der Scanner verlangt eine Anmeldung — das unterstützt PDF-Knead noch nicht."));
            case 503:
                throw new ScanException(Tr.T("Der Scanner ist gerade belegt — vielleicht von einem anderen Programm."), ScanErrorKind.Busy);
            default:
                throw new EsclProtocolException(Tr.T("Der Scanner meldet einen Fehler ({0}).", answer.Status));
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _http.Dispose();
}
