using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace PdfKnead.Core.Pdf.Signatures;

/// <summary>
/// A time-stamping authority (RFC 3161): takes a request and returns the service's answer. The only way the signature goes to the network;
/// <see cref="Platform.HttpTimestampAuthority"/> sends it to the address the user entered, tests answer themselves.
/// </summary>
public interface ITimestampAuthority
{
    /// <summary>Sends a time-stamp request and returns the response.</summary>
    /// <param name="request">The DER-encoded TimeStampReq.</param>
    /// <param name="cancel">Cancels the request.</param>
    /// <returns>The DER-encoded TimeStampResp.</returns>
    /// <exception cref="TimestampException">The service could not be reached or answered with an error.</exception>
    byte[] Request(byte[] request, CancellationToken cancel = default);
}

/// <summary>The time stamp could not be obtained (no connection, the service refused, or its answer does not fit).</summary>
/// <param name="message">What to tell the user (translated).</param>
/// <param name="inner">The cause.</param>
public class TimestampException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A time stamp of a signature as found when checking it.</summary>
/// <param name="Time">The time the service confirms (UTC).</param>
/// <param name="Authority">The name of the service (subject of its certificate).</param>
/// <param name="Valid">The token is signed by the service and covers exactly this signature.</param>
/// <param name="Trusted">The service's certificate leads to a root this computer trusts.</param>
/// <param name="Accuracy">How exact the service says its time is (TSTInfo accuracy, RFC 3161, 2.4.2); zero when it says nothing.</param>
public sealed record SignatureTimestamp(DateTimeOffset Time, string Authority, bool Valid, bool Trusted, TimeSpan Accuracy = default)
{
    /// <summary>
    /// Trusted only through a later document time stamp: the service's path no longer holds today, but did at the time that stamp proves the
    /// token existed (EN 319 102-1, 5.6.2.3; docs/FEHLERGEDAECHTNIS.md, No. 712).
    /// </summary>
    public bool ThroughProof { get; init; }

    /// <summary>The accuracy of a token (zero without one; at most a day, a larger value is nonsense and widens nothing).</summary>
    internal static TimeSpan AccuracyOf(System.Security.Cryptography.Pkcs.Rfc3161TimestampTokenInfo info) =>
        info.AccuracyInMicroseconds is { } micro ? TimeSpan.FromMicroseconds(Math.Min(micro, 86_400_000_000L)) : TimeSpan.Zero;
}

/// <summary>
/// Signature time stamps (PAdES B-T, ETSI EN 319 142-1, 5.3; RFC 3161 and RFC 5816): after signing, the SHA-256 of the signature value goes
/// to a time-stamping authority, whose signed token — „this signature existed at this time“ — is added to the signer as the unsigned attribute
/// signature-time-stamp (RFC 3161, Appendix A). Only the digest of the signature leaves the computer, nothing of the document.
/// </summary>
public static class SignatureTimestamps
{
    /// <summary>id-aa-signatureTimeStampToken (RFC 3161, Appendix A).</summary>
    public const string Oid = "1.2.840.113549.1.9.16.2.14";

    /// <summary>Room for a token in the reserved CMS block: tokens with the service's certificates take some kilobytes.</summary>
    public const int ReservedBytes = 24 * 1024;

    /// <summary>Adds a signature time stamp to a CMS signature with one signer.</summary>
    /// <param name="cms">The DER-encoded CMS block.</param>
    /// <param name="authority">The time-stamping authority.</param>
    /// <param name="cancel">Cancels the request.</param>
    /// <returns>The CMS block with the token.</returns>
    /// <exception cref="TimestampException">No token could be obtained, or it does not fit the signature.</exception>
    public static byte[] AddTo(byte[] cms, ITimestampAuthority authority, CancellationToken cancel = default)
    {
        var signed = new SignedCms();
        signed.Decode(cms);
        var signer = signed.SignerInfos[0];
        var nonce = RandomNumberGenerator.GetBytes(8);
        nonce[0] &= 0x7F;   // a positive INTEGER
        var request = Rfc3161TimestampRequest.CreateFromSignerInfo(signer, HashAlgorithmName.SHA256, nonce: nonce, requestSignerCertificates: true);
        byte[] response;
        try
        {
            response = authority.Request(request.Encode(), cancel);
        }
        catch (Exception e) when (e is not TimestampException and not OperationCanceledException)
        {
            throw new TimestampException(Tr.T("Der Zeitstempeldienst ist nicht erreichbar."), e);
        }
        Rfc3161TimestampToken token;
        try
        {
            // checks the status, the nonce, the message imprint, and that the service signed it
            token = request.ProcessResponse(response, out _);
        }
        catch (CryptographicException e)
        {
            throw new TimestampException(Tr.T("Der Zeitstempeldienst hat keinen gültigen Zeitstempel geliefert."), e);
        }
        if (Unfit(token) is { } unfit) throw new TimestampException(unfit);
        signer.AddUnsignedAttribute(new AsnEncodedData(new Oid(Oid), token.AsSignedCms().Encode()));
        return signed.Encode();
    }

    /// <summary>
    /// What makes a token unfit to be built into a new signature, or null: signed with SHA-1 or MD5, without the service's certificate, the
    /// certificate not valid at the time it confirms or not meant for time stamps (RFC 3161, 2.3; EN 319 142-1, 6.2.1 and the draft V1.3.0,
    /// 6.2.1; docs/FEHLERGEDAECHTNIS.md, No. 528). The status, nonce, imprint and the service's signature are checked when the answer is
    /// processed; trust in the service stays a hint (a service of one's own may be entered).
    /// </summary>
    /// <param name="token">The token as the service sent it.</param>
    /// <returns>The reason (translated), or null.</returns>
    internal static string? Unfit(Rfc3161TimestampToken token)
    {
        var cms = token.AsSignedCms();
        var signer = cms.SignerInfos[0];
        if (WeakAlgorithms.Contains(signer.DigestAlgorithm.Value ?? "") || WeakAlgorithms.Contains(signer.SignatureAlgorithm.Value ?? ""))
            return Tr.T("Der Zeitstempeldienst signiert mit einem veralteten Verfahren (SHA-1 oder MD5); sein Zeitstempel würde die Zeit nicht sicher bestätigen.");
        if (signer.Certificate is not { } certificate)
            return Tr.T("Der Zeitstempeldienst hat sein Zertifikat nicht mitgeschickt.");
        var at = token.TokenInfo.Timestamp.UtcDateTime;
        if (at < certificate.NotBefore.ToUniversalTime() || at > certificate.NotAfter.ToUniversalTime())
            return Tr.T("Das Zertifikat des Zeitstempeldienstes war zur Zeit des Zeitstempels nicht gültig.");
        if (certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault() is not { } purposes
            || !purposes.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == TimeStampingPurpose))
            return Tr.T("Das Zertifikat des Zeitstempeldienstes ist nicht für Zeitstempel bestimmt.");
        return null;
    }

    // SHA-1 and MD5 as digest, and the signature algorithms built on them (RSA, DSA, ECDSA)
    private static readonly HashSet<string> WeakAlgorithms =
        ["1.3.14.3.2.26", "1.2.840.113549.2.5", "1.2.840.113549.1.1.5", "1.2.840.113549.1.1.4", "1.2.840.10040.4.3", "1.2.840.10045.4.1"];

    private const string TimeStampingPurpose = "1.3.6.1.5.5.7.3.8";

    /// <summary>
    /// A time-stamp token for a SHA-256 digest (for a document time stamp, ISO 32000-2:2020, 12.8.5): requested with a nonce and the service's
    /// certificates, the answer checked (status, nonce, imprint, the service's signature).
    /// </summary>
    /// <param name="digest">SHA-256 of what is stamped.</param>
    /// <param name="authority">The service.</param>
    /// <param name="cancel">Cancels the request.</param>
    /// <returns>The token (a DER-encoded CMS SignedData).</returns>
    /// <exception cref="TimestampException">No valid token.</exception>
    internal static byte[] TokenFor(byte[] digest, ITimestampAuthority authority, CancellationToken cancel = default)
    {
        var nonce = RandomNumberGenerator.GetBytes(8);
        nonce[0] &= 0x7F;
        var request = Rfc3161TimestampRequest.CreateFromHash(digest, HashAlgorithmName.SHA256, nonce: nonce, requestSignerCertificates: true);
        byte[] response;
        try
        {
            response = authority.Request(request.Encode(), cancel);
        }
        catch (Exception e) when (e is not TimestampException and not OperationCanceledException)
        {
            throw new TimestampException(Tr.T("Der Zeitstempeldienst ist nicht erreichbar."), e);
        }
        Rfc3161TimestampToken token;
        try
        {
            token = request.ProcessResponse(response, out _);
        }
        catch (CryptographicException e)
        {
            throw new TimestampException(Tr.T("Der Zeitstempeldienst hat keinen gültigen Zeitstempel geliefert."), e);
        }
        if (Unfit(token) is { } unfit) throw new TimestampException(unfit);
        return token.AsSignedCms().Encode();
    }

    /// <summary>Asks a service for a time stamp on a random value — to check the address before signing; nothing of a document is sent.</summary>
    /// <param name="authority">The service.</param>
    /// <param name="cancel">Cancels the request.</param>
    /// <returns>What the service answered: its time and name, and whether this computer trusts it.</returns>
    /// <exception cref="TimestampException">No valid answer.</exception>
    public static SignatureTimestamp Probe(ITimestampAuthority authority, CancellationToken cancel = default)
    {
        var request = Rfc3161TimestampRequest.CreateFromHash(RandomNumberGenerator.GetBytes(32), HashAlgorithmName.SHA256,
            nonce: RandomNumberGenerator.GetBytes(8).Select((b, i) => i == 0 ? (byte)(b & 0x7F) : b).ToArray(), requestSignerCertificates: true);
        byte[] response;
        try
        {
            response = authority.Request(request.Encode(), cancel);
        }
        catch (Exception e) when (e is not TimestampException and not OperationCanceledException)
        {
            throw new TimestampException(Tr.T("Der Zeitstempeldienst ist nicht erreichbar."), e);
        }
        try
        {
            var token = request.ProcessResponse(response, out _);
            // „Dienst prüfen“ says before signing what signing would refuse (No. 528)
            if (Unfit(token) is { } unfit) throw new TimestampException(unfit);
            var signer = token.AsSignedCms().SignerInfos[0].Certificate;
            var name = signer?.GetNameInfo(X509NameType.SimpleName, false) ?? token.TokenInfo.GetTimestampAuthorityName()?.ToString() ?? "";
            return new SignatureTimestamp(token.TokenInfo.Timestamp, name, true, signer is not null && Trusted(signer, token.AsSignedCms().Certificates),
                SignatureTimestamp.AccuracyOf(token.TokenInfo));
        }
        catch (CryptographicException e)
        {
            throw new TimestampException(Tr.T("Der Zeitstempeldienst hat keinen gültigen Zeitstempel geliefert."), e);
        }
    }

    /// <summary>The time stamp of a signer, checked; null if it has none or it cannot be read.</summary>
    /// <param name="signer">The signer of the signature.</param>
    /// <returns>The time stamp.</returns>
    public static SignatureTimestamp? Of(SignerInfo signer) => Of(signer, (authority, extra) => Trusted(authority, extra), null);

    /// <summary>
    /// The time stamp of a signer, its service checked against <paramref name="roots"/> today and, failing that, at the time
    /// <paramref name="proven"/> a later document time stamp proves the token existed.
    /// </summary>
    internal static SignatureTimestamp? Of(SignerInfo signer, X509Certificate2Collection roots, DateTimeOffset? proven)
    {
        var stamp = Of(signer, (authority, extra) => Trusted(authority, extra, roots, DateTimeOffset.Now), null);
        if (stamp is not { Valid: true, Trusted: false } || proven is not { } at) return stamp;
        return Of(signer, (authority, extra) => Trusted(authority, extra, roots, at), true) ?? stamp;
    }

    private static SignatureTimestamp? Of(SignerInfo signer, Func<X509Certificate2, X509Certificate2Collection, bool> isTrusted, bool? throughProof)
    {
        foreach (var attribute in signer.UnsignedAttributes)
        {
            if (attribute.Oid.Value != Oid || attribute.Values.Count == 0) continue;
            try
            {
                if (!Rfc3161TimestampToken.TryDecode(attribute.Values[0].RawData, out var token, out _)) return null;
                var valid = token.VerifySignatureForSignerInfo(signer, out var authority);
                var name = authority?.GetNameInfo(X509NameType.SimpleName, false) ?? token.TokenInfo.GetTimestampAuthorityName()?.ToString() ?? "";
                var trusted = valid && authority is not null && isTrusted(authority, token.AsSignedCms().Certificates);
                authority?.Dispose();
                return new SignatureTimestamp(token.TokenInfo.Timestamp, name, valid, trusted, SignatureTimestamp.AccuracyOf(token.TokenInfo))
                    { ThroughProof = trusted && throughProof == true };
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Whether the service's certificate names the purpose time stamping (RFC 3161, 2.3) and leads to one of <paramref name="roots"/> by a path
    /// that keeps the rules of RFC 5280 at <paramref name="at"/> (<see cref="CertificatePath"/>); offline.
    /// </summary>
    internal static bool Trusted(X509Certificate2 authority, X509Certificate2Collection extra, X509Certificate2Collection roots, DateTimeOffset at) =>
        authority.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.8"))
        && CertificatePath.Check(authority, extra, roots, at).Valid;

    /// <summary>Whether the service's certificate leads to a root this computer trusts, offline, valid for time-stamping.</summary>
    internal static bool Trusted(X509Certificate2 authority, X509Certificate2Collection extra)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;   // offline: no downloads
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ExtraStore.AddRange(extra);
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.8"));   // id-kp-timeStamping
        return chain.Build(authority);
    }
}
