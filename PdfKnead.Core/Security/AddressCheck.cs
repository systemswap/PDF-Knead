using System.Globalization;
using PdfKnead.Core.Localization;

namespace PdfKnead.Core.Security;

/// <summary>What <see cref="AddressCheck.Check"/> found out about an address.</summary>
/// <param name="Text">The address as given.</param>
/// <param name="Target">What would be opened (the host in its ASCII form, as really contacted); null if PDF-Knead does not open it.</param>
/// <param name="Host">The server for display (Unicode, with the ASCII form where they differ); null if there is none.</param>
/// <param name="Scheme">The scheme in lower case; null if the address is not valid.</param>
/// <param name="Warnings">Everything that could make it look like another one or that PDF-Knead will not open (localised).</param>
public sealed record AddressInfo(string Text, string? Target, string? Host, string? Scheme, IReadOnlyList<string> Warnings);

/// <summary>
/// The check of web and mail addresses before anything is opened — for codes and for links alike (docs/ABLAUFPLAN.md, 7.3, step 4):
/// Punycode, mixed scripts, an IP address instead of a name, unencrypted http, something before an @ that is not the server, unusual length.
/// Opened are only http, https and mailto; everything else (file:, javascript:, data:, relative addresses) is only shown.
/// </summary>
public static class AddressCheck
{
    /// <summary>Checks an address; „www.…“ counts as https.</summary>
    public static AddressInfo Check(string text)
    {
        var warnings = new List<string>();
        var candidate = text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + text : text;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            warnings.Add(Tr.T("Die Adresse ist nicht gültig aufgebaut."));
            return new AddressInfo(text, null, null, null, warnings);
        }
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme == "mailto") return new AddressInfo(text, uri.AbsoluteUri, null, scheme, warnings);
        if (scheme is not ("http" or "https"))
        {
            warnings.Add(Tr.T("Die Adresse verwendet „{0}:“ — so etwas öffnet PDF-Knead nicht.", scheme));
            return new AddressInfo(text, null, null, scheme, warnings);
        }
        var host = uri.IdnHost;   // ASCII (Punycode) form: what is really contacted
        string unicodeHost;
        try
        {
            unicodeHost = new IdnMapping().GetUnicode(host);
        }
        catch (ArgumentException)
        {
            unicodeHost = host;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
            warnings.Add(Tr.T("Vor dem @ steht „{0}“ — das ist nicht der Server. Angesteuert wird „{1}“.", uri.UserInfo, unicodeHost));
        if (host.Contains("xn--", StringComparison.OrdinalIgnoreCase))
            warnings.Add(Tr.T("Der Servername enthält Sonderbuchstaben ({0}); solche Namen können bekannte Adressen täuschend ähnlich nachahmen.", host));
        if (MixedScripts(unicodeHost))
            warnings.Add(Tr.T("Der Servername mischt verschiedene Schriften (etwa lateinische und kyrillische Buchstaben)."));
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            warnings.Add(Tr.T("Die Adresse nennt statt eines Namens nur eine IP-Adresse."));
        if (scheme == "http") warnings.Add(Tr.T("Die Verbindung wäre unverschlüsselt (http)."));
        if (text.Length > 1000) warnings.Add(Tr.T("Die Adresse ist ungewöhnlich lang."));
        var target = new UriBuilder(uri) { Host = host }.Uri.AbsoluteUri;
        return new AddressInfo(text, target, unicodeHost == host ? host : $"{unicodeHost} ({host})", scheme, warnings);
    }

    private static bool MixedScripts(string host)
    {
        bool latin = false, other = false;
        foreach (var c in host)
        {
            if (c < 0x80) latin |= char.IsAsciiLetter(c);
            else if (char.IsLetter(c))
            {
                if (c is >= 'À' and <= 'ɏ') latin = true;
                else other = true;
            }
        }
        return latin && other;
    }
}
