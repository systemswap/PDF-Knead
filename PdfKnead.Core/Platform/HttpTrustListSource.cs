using PdfKnead.Core.Pdf.Signatures.TrustLists;

namespace PdfKnead.Core.Platform;

/// <summary>
/// Fetches trusted lists over HTTP: only the addresses the EU list of lists names, without cookies, each list at most 64 MB. Redirections
/// are followed only within the same domain and to https, at most three (some countries move from „www.“ to the bare domain). Used only when the user clicks „Laden“ (KONZEPT.md, section 5.17, „EU-Vertrauensliste“). Plain HTTP is accepted: every list is
/// signed and checked before it is used.
/// </summary>
/// <param name="handler">For tests: answers instead of the network.</param>
public sealed class HttpTrustListSource(HttpMessageHandler? handler = null) : ITrustListSource
{
    private const int MaxList = 64 << 20;

    /// <inheritdoc/>
    public byte[] Fetch(Uri address, CancellationToken cancel = default)
    {
        using var client = LimitedHttp.Client(handler);
        try
        {
            for (var hops = 0; ; hops++)
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, address);
                using var response = client.Send(message, HttpCompletionOption.ResponseHeadersRead, cancel);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location && hops < 3)
                {
                    var target = location.IsAbsoluteUri ? location : new Uri(address, location);
                    if (target.Scheme != "https" || !SameDomain(address.Host, target.Host))
                        throw new TrustListException(Tr.T("Der Server leitet woandershin um ({0}).", target.Host));
                    address = target;
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new TrustListException(Tr.T("Der Server antwortet mit {0}.", (int)response.StatusCode));
                return LimitedHttp.Read(response, MaxList, cancel) ?? throw new TrustListException(Tr.T("Die Liste ist zu groß."));
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            throw new TrustListException(Tr.T("Nicht erreichbar ({0}).", address.Host));
        }
    }

    /// <summary>The same host, or one a subdomain of the other („www.example.org“ and „example.org“).</summary>
    private static bool SameDomain(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || a.EndsWith("." + b, StringComparison.OrdinalIgnoreCase) || b.EndsWith("." + a, StringComparison.OrdinalIgnoreCase);
}
