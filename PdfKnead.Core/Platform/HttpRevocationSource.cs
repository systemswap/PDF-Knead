using System.Net.Http.Headers;
using PdfKnead.Core.Pdf.Signatures;

namespace PdfKnead.Core.Platform;

/// <summary>
/// Revocation status over HTTP: OCSP requests posted to the responder named in the certificate (RFC 6960, Appendix A.1), revocation
/// lists fetched from its distribution point (RFC 5280, 4.2.1.13), and the certificate of an issuer that did not come along from its
/// caIssuers address (4.2.2.1). Only those addresses, without redirections or cookies, answers up to 64 KB (OCSP), 16 MB (CRL) or 1 MB
/// (issuer). Used only when the user has switched long-term validation on (KONZEPT.md, section 5.17,
/// „Langzeitprüfung“). Plain HTTP is the rule for these services: the answers are signed by the certification authority and checked.
/// </summary>
/// <param name="handler">For tests: answers instead of the network.</param>
public sealed class HttpRevocationSource(HttpMessageHandler? handler = null) : IRevocationSource
{
    private const int MaxOcsp = 64 * 1024, MaxCrl = 16 << 20, MaxIssuer = 1 << 20;

    /// <inheritdoc/>
    public byte[]? Ocsp(Uri responder, byte[] request, CancellationToken cancel = default)
    {
        using var client = LimitedHttp.Client(handler);
        using var content = new ByteArrayContent(request);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/ocsp-request");
        using var message = new HttpRequestMessage(HttpMethod.Post, responder) { Content = content };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/ocsp-response"));
        return Send(client, message, MaxOcsp, cancel);
    }

    /// <inheritdoc/>
    public byte[]? Crl(Uri address, CancellationToken cancel = default)
    {
        using var client = LimitedHttp.Client(handler);
        using var message = new HttpRequestMessage(HttpMethod.Get, address);
        return Send(client, message, MaxCrl, cancel);
    }

    /// <inheritdoc/>
    public byte[]? Issuer(Uri address, CancellationToken cancel = default)
    {
        using var client = LimitedHttp.Client(handler);
        using var message = new HttpRequestMessage(HttpMethod.Get, address);
        return Send(client, message, MaxIssuer, cancel);
    }

    private static byte[]? Send(HttpClient client, HttpRequestMessage message, int max, CancellationToken cancel)
    {
        try
        {
            using var response = client.Send(message, HttpCompletionOption.ResponseHeadersRead, cancel);
            return response.IsSuccessStatusCode ? LimitedHttp.Read(response, max, cancel) : null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            return null;   // not reachable: another address, or the status stays missing
        }
    }
}
