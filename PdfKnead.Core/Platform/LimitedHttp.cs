using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace PdfKnead.Core.Platform;

/// <summary>
/// The HTTP client of the signature services (time stamps, revocation status): only the given address, without redirections, cookies or
/// automatic decompression, answers up to a limit. Nothing about the computer or the user is sent.
/// </summary>
internal static class LimitedHttp
{
    /// <summary>How long to wait for a service.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>A client for one request.</summary>
    /// <param name="handler">For tests: answers instead of the network; null for the network.</param>
    /// <param name="clientCertificate">A certificate with key to log in with over https (a customer certificate of the service); null for none.</param>
    public static HttpClient Client(HttpMessageHandler? handler, X509Certificate2? clientCertificate = null)
    {
        var network = handler is null ? new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        } : null;
        if (network is not null && clientCertificate is not null) network.SslOptions.ClientCertificates = [clientCertificate];
        return new HttpClient(handler ?? network!, disposeHandler: handler is null)
        {
            Timeout = Timeout,
        };
    }

    /// <summary>Reads the body of an answer; null if it is larger than <paramref name="max"/>.</summary>
    public static byte[]? Read(HttpResponseMessage response, int max, CancellationToken cancel)
    {
        if (response.Content.Headers.ContentLength > max) return null;
        using var stream = response.Content.ReadAsStream(cancel);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
