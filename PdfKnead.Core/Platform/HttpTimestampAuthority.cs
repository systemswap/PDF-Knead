using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfKnead.Core.Pdf.Signatures;

namespace PdfKnead.Core.Platform;

/// <summary>
/// A time-stamping authority reached over HTTP (RFC 3161, section 3.4): the request is posted to the address the user entered — only there,
/// without redirections, cookies or anything about the computer or the user; the answer may be at most 1 MB. Used only when the user has
/// switched time stamps on (KONZEPT.md, section 5.17, „Zeitstempel“). Plain HTTP is allowed: the token is signed by the service, so a
/// changed answer is refused when it is checked.
/// </summary>
/// <remarks>
/// Services that charge for their time stamps (qualified ones, eIDAS) want a login: user name and password (HTTP Basic, RFC 7617) or a
/// customer certificate (TLS client authentication), both only over https so that nothing is readable on the way. A refused login is a
/// <see cref="TimestampLoginException"/>.
/// </remarks>
/// <param name="server">The address of the service (http or https).</param>
/// <param name="handler">For tests: answers instead of the network.</param>
/// <param name="login">The login at the service; null for none.</param>
public sealed class HttpTimestampAuthority(Uri server, HttpMessageHandler? handler = null, TimestampLogin? login = null) : ITimestampAuthority
{
    /// <summary>The free service offered first in „Bekannte Dienste …“ (its root is trusted by the common systems); entered, never switched on.</summary>
    public const string Suggested = "http://timestamp.digicert.com";

    /// <summary>The largest answer accepted.</summary>
    private const int MaxResponse = 1 << 20;

    /// <summary>How long to wait for the service.</summary>
    public static TimeSpan Timeout => LimitedHttp.Timeout;

    /// <summary>Whether a text is a usable address of a service: absolute, http or https, with a host.</summary>
    /// <param name="text">The address as entered.</param>
    /// <param name="address">The address.</param>
    /// <returns>true if it can be used.</returns>
    public static bool TryParse(string? text, out Uri address)
    {
        address = null!;
        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0) return false;
        address = uri;
        return true;
    }

    /// <inheritdoc/>
    public byte[] Request(byte[] request, CancellationToken cancel = default)
    {
        if (login is { HasAny: true } && server.Scheme != "https")
            throw new TimestampException(Tr.T("Mit Anmeldung nur über eine https-Adresse: über http wäre das Passwort unterwegs lesbar."));
        using var client = LimitedHttp.Client(handler, login?.Certificate);
        using var content = new ByteArrayContent(request);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
        using var message = new HttpRequestMessage(HttpMethod.Post, server) { Content = content };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/timestamp-reply"));
        if (login?.User is { Length: > 0 } user)
            message.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + (login.Password ?? ""))));
        try
        {
            using var response = client.Send(message, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new TimestampLoginException(login is { HasAny: true }
                    ? Tr.T("Der Zeitstempeldienst hat die Anmeldung abgelehnt.")
                    : Tr.T("Der Zeitstempeldienst verlangt eine Anmeldung (Benutzername oder Kundenzertifikat in „Zertifikat“ eintragen)."));
            if (!response.IsSuccessStatusCode)
                throw new TimestampException(Tr.T("Der Zeitstempeldienst hat die Anfrage abgelehnt ({0}).", (int)response.StatusCode));
            return LimitedHttp.Read(response, MaxResponse, cancel)
                ?? throw new TimestampException(Tr.T("Die Antwort des Zeitstempeldienstes ist zu groß."));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            throw new TimestampException(Tr.T("Der Zeitstempeldienst ist nicht erreichbar ({0}).", server.Host), e);
        }
    }
}

/// <summary>The login at a time-stamping authority: user name and password, a customer certificate, or both.</summary>
/// <param name="User">User name (HTTP Basic); null for none.</param>
/// <param name="Password">Its password; asked once per session, never stored.</param>
/// <param name="Certificate">A customer certificate with key (TLS client authentication); null for none.</param>
public sealed record TimestampLogin(string? User, string? Password, X509Certificate2? Certificate)
{
    /// <summary>Whether there is anything to log in with.</summary>
    public bool HasAny => User is { Length: > 0 } || Certificate is not null;

    /// <summary>Opens a customer certificate file (.p12, .pfx) with its password.</summary>
    /// <param name="path">The file.</param>
    /// <param name="password">Its password.</param>
    /// <returns>The certificate with its key.</returns>
    /// <exception cref="CertificatePasswordException">The password is wrong (or the file damaged).</exception>
    /// <exception cref="NoPrivateKeyException">The file holds no certificate with a key.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static X509Certificate2 OpenCertificate(string path, string password)
    {
        X509Certificate2Collection all;
        try
        {
            all = X509CertificateLoader.LoadPkcs12CollectionFromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException)
        {
            throw new CertificatePasswordException(Tr.T("Das Passwort des Kundenzertifikats stimmt nicht, oder die Datei ist beschädigt."));
        }
        var withKey = all.FirstOrDefault(c => c.HasPrivateKey);
        foreach (var other in all)
            if (!ReferenceEquals(other, withKey)) other.Dispose();
        return withKey ?? throw new NoPrivateKeyException(Tr.T("Die Datei enthält kein Zertifikat mit Schlüssel."));
    }
}

/// <summary>The time-stamping authority refused the login, or wants one: the password of the session is to be asked again.</summary>
/// <param name="message">What to tell the user (translated).</param>
public sealed class TimestampLoginException(string message) : TimestampException(message);

/// <summary>A time-stamping authority as set up in „Zertifikat“: its address and, for services that want one, the login (without passwords).</summary>
/// <param name="Server">The address as entered.</param>
/// <param name="User">User name; null for none.</param>
/// <param name="Certificate">Path of the customer certificate file; null for none.</param>
public sealed record TimestampService(string Server, string? User = null, string? Certificate = null)
{
    /// <summary>Whether a login is set up.</summary>
    public bool HasLogin => User is { Length: > 0 } || Certificate is { Length: > 0 };
}
