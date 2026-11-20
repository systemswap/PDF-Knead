using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfKnead.Core.Pdf.Signatures;

/// <summary>
/// Where revocation information comes from: OCSP responders and CRL distribution points named in the certificates. The only way this part
/// goes to the network; <see cref="Platform.HttpRevocationSource"/> asks the addresses, tests answer themselves.
/// </summary>
public interface IRevocationSource
{
    /// <summary>Asks an OCSP responder (RFC 6960, Appendix A.1: HTTP POST).</summary>
    /// <param name="responder">Its address from the certificate.</param>
    /// <param name="request">The DER-encoded OCSPRequest.</param>
    /// <param name="cancel">Cancels the request.</param>
    /// <returns>The DER-encoded OCSPResponse; null if there is no answer.</returns>
    byte[]? Ocsp(Uri responder, byte[] request, CancellationToken cancel = default);

    /// <summary>Fetches a certificate revocation list.</summary>
    /// <param name="address">Its address from the certificate.</param>
    /// <param name="cancel">Cancels the request.</param>
    /// <returns>The DER-encoded CRL; null if there is none.</returns>
    byte[]? Crl(Uri address, CancellationToken cancel = default);

    /// <summary>Fetches the certificate of an issuer from the address its certificates name (RFC 5280, 4.2.2.1: caIssuers).</summary>
    /// <param name="address">The address from the certificate.</param>
    /// <param name="cancel">Cancels the request.</param>
    /// <returns>A DER certificate or a PKCS#7 „certs-only“ bundle; null if there is none.</returns>
    byte[]? Issuer(Uri address, CancellationToken cancel = default) => null;
}

/// <summary>A certificate the signature depends on is revoked: signing with it makes no sense.</summary>
/// <param name="message">What to tell the user (translated).</param>
public sealed class CertificateRevokedException(string message) : Exception(message);

/// <summary>
/// Revocation information for long-term validation (PAdES B-LT, ETSI EN 319 142-1, 5.4): for each certificate of a chain, the status from
/// its issuer's OCSP responder (RFC 6960) or, failing that, its CRL (RFC 5280) — checked here: the answer is signed by the issuer (or a
/// responder it authorised), concerns exactly this certificate, and says „good“. A revoked certificate stops the signing.
/// </summary>
public static class RevocationInfo
{
    private const string OcspOid = "1.3.6.1.5.5.7.48.1";
    private const string CaIssuersOid = "1.3.6.1.5.5.7.48.2";
    private const string AiaOid = "1.3.6.1.5.5.7.1.1";
    private const string CrlDistributionOid = "2.5.29.31";
    private const string OcspBasicOid = "1.3.6.1.5.5.7.48.1.1";
    private const string OcspSigningOid = "1.3.6.1.5.5.7.3.9";
    private const string Sha1Oid = "1.3.14.3.2.26";

    /// <summary>What was found for a chain.</summary>
    /// <param name="Certificates">The certificates of the chain and of OCSP responders (DER).</param>
    /// <param name="Ocsps">OCSP responses (DER OCSPResponse) that say „good“.</param>
    /// <param name="Crls">CRLs (DER) on which the certificates are not listed.</param>
    /// <param name="Missing">Certificates (subject names) for which no status could be obtained.</param>
    public sealed record Found(List<byte[]> Certificates, List<byte[]> Ocsps, List<byte[]> Crls, List<string> Missing);

    /// <summary>
    /// The revocation information of chains: each end certificate with the certificates that came with it, built into a chain offline (the
    /// roots of the system included), each link but the root asked about.
    /// </summary>
    /// <param name="chains">End certificates with the certificates that came with them (signer and time-stamping authority).</param>
    /// <param name="source">Where to ask.</param>
    /// <param name="at">The time the status must hold (the signing time).</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>What was found.</returns>
    /// <exception cref="CertificateRevokedException">A certificate is revoked.</exception>
    public static Found Collect(IEnumerable<(X509Certificate2 End, X509Certificate2Collection Extra)> chains, IRevocationSource source, DateTimeOffset at,
        CancellationToken cancel = default)
    {
        var found = new Found([], [], [], []);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (end, extra) in chains)
        {
            var fetched = new List<X509Certificate2>();
            var links = Links(end, extra, fetched);
            try
            {
                // An issuer that did not come along (signature cards often carry only their own certificate): fetched from the address the
                // last link names, checked to have issued it — without it the chain ends there and no status can be asked for
                for (var depth = 0; depth < MaxFetchedIssuers && links.Count > 0 && !SelfIssued(links[^1]); depth++)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (FetchIssuer(links[^1], source, cancel) is not { } issuer) break;
                    fetched.Add(issuer);
                    foreach (var link in links) link.Dispose();
                    links = Links(end, extra, fetched);
                }
                for (var i = 0; i < links.Count; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var certificate = links[i];
                    if (!seen.Add(certificate.Thumbprint)) continue;   // a chain shared by signer and service is asked about once
                    found.Certificates.Add(certificate.RawData);
                    if (SelfIssued(certificate)) continue;   // the root (or a self-signed one) has no status
                    // without its issuer (an intermediate authority not at hand) nobody can be asked
                    if (i + 1 >= links.Count || !StatusOf(certificate, links[i + 1], source, at, found, cancel))
                        found.Missing.Add(certificate.GetNameInfo(X509NameType.SimpleName, false) is { Length: > 0 } name ? name : certificate.Subject);
                }
            }
            finally
            {
                foreach (var link in links) link.Dispose();
                foreach (var issuer in fetched) issuer.Dispose();
            }
        }
        return found;
    }

    /// <summary>
    /// The issuers of a signing certificate that are not at hand, fetched from the addresses its chain names (caIssuers) — before signing, so
    /// they go into the CMS block: a checker without a connection builds the chain from it (EN 319 142-1, 6.3; signature cards often carry only
    /// their own certificate; docs/FEHLERGEDAECHTNIS.md, No. 533). Only with the long-term validation switched on (it may use the network);
    /// what cannot be fetched is left out, signing goes on as before.
    /// </summary>
    /// <param name="end">The signing certificate.</param>
    /// <param name="extra">The certificates that came with it.</param>
    /// <param name="source">Where to fetch.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>The fetched issuers (owned by the caller); empty if the chain is complete or nothing could be fetched.</returns>
    internal static List<X509Certificate2> MissingIssuers(X509Certificate2 end, X509Certificate2Collection extra, IRevocationSource source,
        CancellationToken cancel = default)
    {
        var fetched = new List<X509Certificate2>();
        var links = Links(end, extra, fetched);
        try
        {
            for (var depth = 0; depth < MaxFetchedIssuers && links.Count > 0 && !SelfIssued(links[^1]); depth++)
            {
                cancel.ThrowIfCancellationRequested();
                X509Certificate2? issuer;
                try
                {
                    issuer = FetchIssuer(links[^1], source, cancel);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    break;   // no connection or a broken answer: signed without it, as before
                }
                if (issuer is null) break;
                fetched.Add(issuer);
                foreach (var link in links) link.Dispose();
                links = Links(end, extra, fetched);
            }
        }
        finally
        {
            foreach (var link in links) link.Dispose();
        }
        return fetched;
    }

    /// <summary>Whether a certificate (DER) is issued by itself: a root, or an own certificate.</summary>
    internal static bool SelfIssued(byte[] der)
    {
        using var certificate = X509CertificateLoader.LoadCertificate(der);
        return SelfIssued(certificate);
    }

    /// <summary>At most this many issuers are fetched for one chain.</summary>
    private const int MaxFetchedIssuers = 4;

    /// <summary>The links of a chain, built offline from the certificates at hand and those of the system.</summary>
    private static List<X509Certificate2> Links(X509Certificate2 end, X509Certificate2Collection extra, List<X509Certificate2> fetched)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;   // only the links matter here, not whether it is trusted
        chain.ChainPolicy.ExtraStore.AddRange(extra);
        chain.ChainPolicy.ExtraStore.AddRange(fetched.ToArray());
        chain.Build(end);
        return chain.ChainElements.Select(e => e.Certificate).ToList();
    }

    /// <summary>
    /// The issuer of a certificate from its caIssuers addresses: a DER certificate or a PKCS#7 bundle (RFC 5280, 4.2.2.1), of which only a
    /// certificate with the issuer's name whose key verifies the certificate's signature is taken.
    /// </summary>
    private static X509Certificate2? FetchIssuer(X509Certificate2 certificate, IRevocationSource source, CancellationToken cancel)
    {
        foreach (var address in AccessLocations(certificate, CaIssuersOid))
        {
            if (source.Issuer(address, cancel) is not { Length: > 0 } bytes) continue;
            foreach (var candidate in Candidates(bytes))
            {
                if (candidate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData) && IssuedBy(certificate, candidate))
                    return candidate;
                candidate.Dispose();
            }
        }
        return null;
    }

    /// <summary>The certificates of a caIssuers answer (at most 16); none if it is neither form.</summary>
    private static List<X509Certificate2> Candidates(byte[] bytes)
    {
        var list = new List<X509Certificate2>();
        try
        {
            list.Add(X509CertificateLoader.LoadCertificate(bytes));
            return list;
        }
        catch (CryptographicException)
        {
            // not a single certificate: a PKCS#7 bundle
        }
        try
        {
            var cms = new System.Security.Cryptography.Pkcs.SignedCms();
            cms.Decode(bytes);
            foreach (var certificate in cms.Certificates.Take(16)) list.Add(X509CertificateLoader.LoadCertificate(certificate.RawData));
        }
        catch (CryptographicException)
        {
            // neither: nothing usable
        }
        return list;
    }

    /// <summary>What validation data carried in a file says about a chain.</summary>
    public enum Coverage
    {
        /// <summary>Not every certificate of the chain has a status in the data.</summary>
        Incomplete,

        /// <summary>Each certificate of the chain (but the root) is confirmed „good“ by an embedded OCSP response or CRL.</summary>
        Complete,

        /// <summary>An embedded answer says a certificate of the chain is revoked (or on hold: see <see cref="Embedded"/>).</summary>
        Revoked,
    }

    /// <summary>
    /// What the validation data says about a chain, in detail (docs/ABLAUFPLAN.md, 2.5): the coverage and, for a revoked certificate, when it
    /// was revoked and why (CRLReason, RFC 5280, 5.3.1), whether it is the end certificate itself (else an issuer up the chain), and whether it
    /// is only on hold (certificateHold).
    /// </summary>
    public sealed record EmbeddedStatus(Coverage Coverage, DateTimeOffset? RevokedAt = null, int? Reason = null, bool EndRevoked = false, bool OnHold = false)
    {
        /// <summary>The status of each certificate of the signer's chain with the answer it rests on, for the report (docs/FEHLERGEDAECHTNIS.md, No. 725).</summary>
        public IReadOnlyList<LinkStatus> Links { get; init; } = [];
    }

    /// <summary>What the validation data say about one certificate of a chain.</summary>
    /// <param name="Name">The holder in short.</param>
    /// <param name="Kind">Good, revoked, on hold, no answer — or the root, which needs none.</param>
    /// <param name="Source">The answer it rests on: OCSP or CRL; null without one.</param>
    /// <param name="ThisUpdate">When the answer was given.</param>
    /// <param name="NextUpdate">Until when it holds; null if it does not say.</param>
    /// <param name="RevokedAt">Since when it is revoked.</param>
    public sealed record LinkStatus(string Name, LinkKind Kind, string? Source = null, DateTimeOffset? ThisUpdate = null, DateTimeOffset? NextUpdate = null,
        DateTimeOffset? RevokedAt = null);

    /// <summary>The status of one certificate of a chain.</summary>
    public enum LinkKind
    {
        Good,
        Revoked,
        OnHold,
        NoAnswer,
        Root,
    }

    /// <summary>
    /// Checks a chain against validation data carried in a file (the document security store): built offline from the certificates that came
    /// with the signature and those of the store, each link but the root needs an answer, checked as when it was obtained.
    /// </summary>
    /// <param name="end">The end certificate (signer or time-stamping authority).</param>
    /// <param name="extra">Certificates that came with it and those of the store.</param>
    /// <param name="ocsps">The OCSP responses of the store.</param>
    /// <param name="crls">The CRLs of the store.</param>
    /// <param name="at">The time the status must hold (the signing time).</param>
    /// <returns>Whether the data covers the chain.</returns>
    public static EmbeddedStatus Embedded(X509Certificate2 end, X509Certificate2Collection extra, IReadOnlyList<byte[]> ocsps, IReadOnlyList<byte[]> crls,
        DateTimeOffset at)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
        chain.ChainPolicy.ExtraStore.AddRange(extra);
        try
        {
            chain.Build(end);
            var links = chain.ChainElements.Select(e => e.Certificate).ToList();
            var complete = links.Count > 0 && SelfIssued(links[^1]);   // up to a root, else the issuer of the last one is missing
            var statuses = new List<LinkStatus>(links.Count);
            for (var i = 0; i + 1 < links.Count; i++)
            {
                if (SelfIssued(links[i]))
                {
                    statuses.Add(new LinkStatus(NameOf(links[i]), LinkKind.Root));
                    continue;
                }
                // fresh answers only (Fresh); of those, the newest counts — an older „revoked“ does not beat a newer „good“, an older „good“
                // not a newer „revoked“ (5.2.6.4); at the same time „revoked“ wins
                var (newest, source) = ocsps.Take(MaxEmbedded).Select(o => (Status: CheckOcsp(o, links[i], links[i + 1], at, out _), Source: "OCSP"))
                    .Concat(crls.Take(MaxEmbedded).Select(c => (Status: CheckCrl(c, links[i], links[i + 1], at), Source: "CRL")))
                    .Where(s => Fresh(s.Status, at))
                    .OrderByDescending(s => s.Status.ThisUpdate).ThenByDescending(s => s.Status.Kind != StatusKind.Good).FirstOrDefault();
                statuses.Add(newest.Kind switch
                {
                    StatusKind.Good => new LinkStatus(NameOf(links[i]), LinkKind.Good, source, newest.ThisUpdate, newest.NextUpdate),
                    StatusKind.Revoked => new LinkStatus(NameOf(links[i]), LinkKind.Revoked, source, newest.ThisUpdate, newest.NextUpdate, newest.RevokedAt),
                    StatusKind.OnHold => new LinkStatus(NameOf(links[i]), LinkKind.OnHold, source, newest.ThisUpdate, newest.NextUpdate, newest.RevokedAt),
                    _ => new LinkStatus(NameOf(links[i]), LinkKind.NoAnswer),
                });
                if (newest.Kind is StatusKind.Revoked or StatusKind.OnHold)
                    return new EmbeddedStatus(Coverage.Revoked, newest.RevokedAt, newest.Reason, i == 0, newest.Kind == StatusKind.OnHold) { Links = statuses };
                complete &= newest.Kind == StatusKind.Good;
            }
            if (links.Count > 0 && SelfIssued(links[^1])) statuses.Add(new LinkStatus(NameOf(links[^1]), LinkKind.Root));
            return new EmbeddedStatus(complete ? Coverage.Complete : Coverage.Incomplete) { Links = statuses };
        }
        finally
        {
            foreach (var element in chain.ChainElements)
                if (!ReferenceEquals(element.Certificate, end)) element.Certificate.Dispose();
        }
    }

    /// <summary>
    /// Whether an answer counts for the status at <paramref name="at"/> (EN 319 102-1, 5.2.5): one with nextUpdate as long as it is current
    /// then (CheckOcsp and CheckCrl refuse those already out of date); one without nextUpdate — many responders leave it out — only when it was
    /// given at or after that time: the validation policy of PDF-Knead sets the accepted freshness to the signing time itself (5.2.5.4, step 1),
    /// a few minutes allowed for clocks that differ. The same rule when collecting the data while signing and when checking, so a file
    /// PDF-Knead writes never carries data it would not count itself (docs/FEHLERGEDAECHTNIS.md, No. 478).
    /// </summary>
    private static bool Fresh(Status status, DateTimeOffset at) =>
        status.Kind != StatusKind.Unusable && (status.NextUpdate is not null || status.ThisUpdate >= at - ClockSlack);

    /// <summary>How far the clock of a responder may lag behind the signing time.</summary>
    private static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(5);

    /// <summary>At most this many answers of a store are tried per certificate (prepared files).</summary>
    private const int MaxEmbedded = 200;

    /// <summary>At most this many entries of a CRL are read (large real lists have some hundred thousand).</summary>
    private const int MaxCrlEntries = 5_000_000;

    private static bool SelfIssued(X509Certificate2 c) => c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData);

    private static string NameOf(X509Certificate2 c) => c.GetNameInfo(X509NameType.SimpleName, false) is { Length: > 0 } simple ? simple : c.Subject;

    /// <summary>The status of one certificate: OCSP first, then the CRLs; true if one says „good“, false if none could be obtained.</summary>
    private static bool StatusOf(X509Certificate2 certificate, X509Certificate2 issuer, IRevocationSource source, DateTimeOffset at, Found found,
        CancellationToken cancel)
    {
        foreach (var responder in Addresses(certificate, ocsp: true))
        {
            byte[]? response;
            try
            {
                response = source.Ocsp(responder, OcspRequest(certificate, issuer), cancel);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                continue;   // another responder or the CRL
            }
            if (response is null) continue;
            var answer = CheckOcsp(response, certificate, issuer, at, out var responderCertificate);
            // an answer the check would not count as fresh is not embedded: another responder or the CRL
            switch (answer.Kind == StatusKind.Good && !Fresh(answer, at) ? StatusKind.Unusable : answer.Kind)
            {
                case StatusKind.Good:
                    found.Ocsps.Add(response);
                    if (responderCertificate is not null) found.Certificates.Add(responderCertificate);
                    return true;
                case StatusKind.Revoked or StatusKind.OnHold:
                    throw new CertificateRevokedException(Tr.T("Das Zertifikat „{0}“ ist gesperrt (Auskunft der Zertifizierungsstelle).", certificate.GetNameInfo(X509NameType.SimpleName, false)));
            }
        }
        foreach (var address in Addresses(certificate, ocsp: false))
        {
            byte[]? crl;
            try
            {
                crl = source.Crl(address, cancel);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                continue;
            }
            if (crl is null) continue;
            var listed = CheckCrl(crl, certificate, issuer, at);
            switch (listed.Kind == StatusKind.Good && !Fresh(listed, at) ? StatusKind.Unusable : listed.Kind)
            {
                case StatusKind.Good:
                    found.Crls.Add(crl);
                    return true;
                case StatusKind.Revoked or StatusKind.OnHold:
                    throw new CertificateRevokedException(Tr.T("Das Zertifikat „{0}“ ist gesperrt (Sperrliste der Zertifizierungsstelle).", certificate.GetNameInfo(X509NameType.SimpleName, false)));
            }
        }
        return false;
    }

    private enum StatusKind { Unusable, Good, Revoked, OnHold }

    /// <summary>One answer about one certificate: its kind, when it was given and how long it holds, and for a revoked one when and why.</summary>
    private readonly record struct Status(StatusKind Kind, DateTimeOffset ThisUpdate = default, DateTimeOffset? NextUpdate = null,
        DateTimeOffset? RevokedAt = null, int? Reason = null)
    {
        public static readonly Status Unusable = new(StatusKind.Unusable);

        /// <summary>Revoked, or only on hold (CRLReason certificateHold, 6).</summary>
        public static Status RevokedOrHeld(DateTimeOffset thisUpdate, DateTimeOffset? nextUpdate, DateTimeOffset revokedAt, int? reason) =>
            new(reason == 6 ? StatusKind.OnHold : StatusKind.Revoked, thisUpdate, nextUpdate, revokedAt, reason);
    }

    /// <summary>The value of an ENUMERATED (CRLReason: 0 … 10).</summary>
    private static int? Enumerated(AsnReader reader) => reader.ReadEnumeratedBytes().Span is { Length: > 0 and <= 2 } bytes ? bytes[^1] : null;

    /// <summary>The http(s) addresses of OCSP responders (authority information access) or CRL distribution points of a certificate.</summary>
    internal static List<Uri> Addresses(X509Certificate2 certificate, bool ocsp) => AccessLocations(certificate, ocsp ? OcspOid : null);

    /// <summary>
    /// The http(s) addresses of a certificate (at most four): of the access method in its Authority Information Access (RFC 5280, 4.2.2.1),
    /// or, with <paramref name="accessMethod"/> null, of its CRL distribution points (4.2.1.13).
    /// </summary>
    private static List<Uri> AccessLocations(X509Certificate2 certificate, string? accessMethod)
    {
        var ocsp = accessMethod is not null;
        var list = new List<Uri>();
        var extension = certificate.Extensions[ocsp ? AiaOid : CrlDistributionOid];
        if (extension is null) return list;
        try
        {
            var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
            while (reader.HasData && list.Count < 4)
            {
                if (ocsp)
                {
                    // AccessDescription ::= SEQUENCE { accessMethod OID, accessLocation GeneralName }
                    var description = reader.ReadSequence();
                    var method = description.ReadObjectIdentifier();
                    var location = description.PeekTag();
                    if (method == accessMethod && location.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 6))) Add(description.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 6)));
                }
                else
                {
                    // DistributionPoint ::= SEQUENCE { distributionPoint [0] { fullName [0] GeneralNames } … }
                    var point = reader.ReadSequence();
                    if (!point.HasData || !point.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0, true))) continue;
                    var name = point.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
                    if (!name.HasData || !name.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0, true))) continue;
                    var names = name.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
                    while (names.HasData)
                    {
                        var tag = names.PeekTag();
                        if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 6))) Add(names.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 6)));
                        else names.ReadEncodedValue();
                    }
                }
            }
        }
        catch (AsnContentException)
        {
            // a damaged extension: no addresses
        }
        return list;

        void Add(string text)
        {
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") list.Add(uri);
        }
    }

    /// <summary>An OCSP request for one certificate (RFC 6960, 4.1.1): its CertID with SHA-1, as responders expect, without a nonce.</summary>
    internal static byte[] OcspRequest(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())                 // OCSPRequest
        using (writer.PushSequence())                 // tbsRequest
        using (writer.PushSequence())                 // requestList
        using (writer.PushSequence())                 // Request
            WriteCertId(writer, certificate, issuer);
        return writer.Encode();
    }

    private static void WriteCertId(AsnWriter writer, X509Certificate2 certificate, X509Certificate2 issuer)
    {
        using (writer.PushSequence())
        {
            using (writer.PushSequence()) writer.WriteObjectIdentifier(Sha1Oid);
            writer.WriteOctetString(SHA1.HashData(issuer.SubjectName.RawData));
            writer.WriteOctetString(SHA1.HashData(issuer.PublicKey.EncodedKeyValue.RawData));
            writer.WriteInteger(certificate.SerialNumberBytes.Span);
        }
    }

    /// <summary>
    /// Checks an OCSP response: successful, a basic response signed by the issuer or by a responder certificate the issuer issued for OCSP
    /// signing, with a single response for exactly this certificate that was still current at the time (its nextUpdate not before it): a status
    /// of „good“ given later also shows the certificate was not revoked when it was used.
    /// </summary>
    private static Status CheckOcsp(byte[] response, X509Certificate2 certificate, X509Certificate2 issuer, DateTimeOffset at, out byte[]? responderCertificate)
    {
        responderCertificate = null;
        try
        {
            var outer = new AsnReader(response, AsnEncodingRules.DER).ReadSequence();
            if (outer.ReadEnumeratedValue<OcspStatus>() != OcspStatus.Successful || !outer.HasData) return Status.Unusable;
            var bytes = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)).ReadSequence();
            if (bytes.ReadObjectIdentifier() != OcspBasicOid) return Status.Unusable;
            var basic = new AsnReader(bytes.ReadOctetString(), AsnEncodingRules.DER).ReadSequence();
            var tbs = basic.ReadEncodedValue();
            var algorithm = basic.ReadSequence().ReadObjectIdentifier();
            var signature = basic.ReadBitString(out _);
            // who signed: the issuer itself, or a responder certificate in the response, issued by the issuer for OCSP signing
            var signer = issuer;
            X509Certificate2? delegated = null;
            if (basic.HasData)
            {
                var certs = basic.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)).ReadSequence();
                while (certs.HasData)
                {
                    var candidate = X509CertificateLoader.LoadCertificate(certs.ReadEncodedValue().ToArray());
                    if (candidate.IssuerName.RawData.AsSpan().SequenceEqual(issuer.SubjectName.RawData) && IssuedBy(candidate, issuer) &&
                        candidate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages[OcspSigningOid] is not null))
                    {
                        delegated = candidate;
                        break;
                    }
                    candidate.Dispose();
                }
            }
            using (delegated)
            {
                var key = delegated ?? signer;
                if (!Verify(tbs.Span, algorithm, signature, key)) return Status.Unusable;
                responderCertificate = delegated?.RawData;
            }
            // ResponseData ::= SEQUENCE { version [0] EXPLICIT …, responderID, producedAt, responses SEQUENCE OF SingleResponse, … }
            var data = new AsnReader(tbs, AsnEncodingRules.DER).ReadSequence();
            if (data.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0, true))) data.ReadEncodedValue();
            data.ReadEncodedValue();             // responderID
            data.ReadGeneralizedTime();          // producedAt
            var responses = data.ReadSequence();
            var archiveCutoff = ArchiveCutoff(data);
            var expected = new AsnWriter(AsnEncodingRules.DER);
            WriteCertId(expected, certificate, issuer);
            var id = expected.Encode();
            while (responses.HasData)
            {
                var single = responses.ReadSequence();
                var certId = single.ReadEncodedValue();
                var status = single.PeekTag();
                var statusValue = single.ReadEncodedValue();
                var thisUpdate = single.ReadGeneralizedTime();
                DateTimeOffset? nextUpdate = single.HasData && single.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0, true))
                    ? single.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)).ReadGeneralizedTime() : null;
                if (!SameCertId(certId.Span, id)) continue;
                if (nextUpdate < at) return Status.Unusable;   // already out of date when the certificate was used
                if (status.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 1, true)))
                {
                    // RevokedInfo ::= SEQUENCE { revocationTime GeneralizedTime, revocationReason [0] EXPLICIT CRLReason OPTIONAL } (RFC 6960, 4.2.1)
                    var info = new AsnReader(statusValue, AsnEncodingRules.DER).ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1, true));
                    var revokedAt = info.ReadGeneralizedTime();
                    int? reason = info.HasData ? Enumerated(info.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true))) : null;
                    return Status.RevokedOrHeld(thisUpdate, nextUpdate, revokedAt, reason);
                }
                if (!status.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))) return Status.Unusable;
                // „good“ given after the certificate expired says nothing unless the responder keeps the status of expired ones
                // (archiveCutoff, RFC 6960, 4.4.4; docs/FEHLERGEDAECHTNIS.md, No. 482)
                if (thisUpdate > new DateTimeOffset(certificate.NotAfter) && !(archiveCutoff <= new DateTimeOffset(certificate.NotAfter))) return Status.Unusable;
                return new Status(StatusKind.Good, thisUpdate, nextUpdate);
            }
            return Status.Unusable;
        }
        catch (Exception e) when (e is AsnContentException or CryptographicException)
        {
            return Status.Unusable;
        }
    }

    private enum OcspStatus { Successful = 0 }

    /// <summary>The same certificate: serial number and issuer hashes equal (a responder may answer with another hash algorithm — then not).</summary>
    private static bool SameCertId(ReadOnlySpan<byte> a, byte[] b) => a.SequenceEqual(b);

    /// <summary>Checks a CRL: signed by the issuer, current at the time, and whether the certificate is listed.</summary>
    private static Status CheckCrl(byte[] crl, X509Certificate2 certificate, X509Certificate2 issuer, DateTimeOffset at)
    {
        try
        {
            var outer = new AsnReader(crl, AsnEncodingRules.DER).ReadSequence();
            var tbs = outer.ReadEncodedValue();
            var algorithm = outer.ReadSequence().ReadObjectIdentifier();
            var signature = outer.ReadBitString(out _);
            if (!Verify(tbs.Span, algorithm, signature, issuer)) return Status.Unusable;
            // TBSCertList ::= SEQUENCE { version INTEGER OPTIONAL, signature, issuer Name, thisUpdate Time, nextUpdate Time OPTIONAL, … }
            var list = new AsnReader(tbs, AsnEncodingRules.DER).ReadSequence();
            if (list.PeekTag().HasSameClassAndValue(Asn1Tag.Integer)) list.ReadInteger();
            list.ReadSequence();
            if (!list.ReadEncodedValue().Span.SequenceEqual(issuer.SubjectName.RawData)) return Status.Unusable;
            var thisUpdate = ReadTime(list);
            DateTimeOffset? nextUpdate = null;
            if (list.HasData && list.PeekTag() is var next && (next.HasSameClassAndValue(Asn1Tag.UtcTime) || next.HasSameClassAndValue(Asn1Tag.GeneralizedTime)))
            {
                nextUpdate = ReadTime(list);
                if (nextUpdate < at) return Status.Unusable;   // already out of date
            }
            // revokedCertificates SEQUENCE OF SEQUENCE { userCertificate, revocationDate, crlEntryExtensions OPTIONAL } (RFC 5280, 5.1): read
            // here with the date and the reasonCode (2.5.29.21) of the entry — the platform's reader gives neither
            var serial = certificate.SerialNumberBytes.Span.TrimStart((byte)0);
            if (list.HasData && list.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
            {
                var entries = list.ReadSequence();
                for (var count = 0; entries.HasData && count < MaxCrlEntries; count++)
                {
                    var entry = entries.ReadSequence();
                    var number = entry.ReadIntegerBytes();
                    var revokedAt = ReadTime(entry);
                    if (!number.Span.TrimStart((byte)0).SequenceEqual(serial)) continue;
                    int? reason = null;
                    if (entry.HasData)
                    {
                        var extensions = entry.ReadSequence();
                        while (extensions.HasData)
                        {
                            var extension = extensions.ReadSequence();
                            var oid = extension.ReadObjectIdentifier();
                            if (extension.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean)) extension.ReadBoolean();
                            var value = extension.ReadOctetString();
                            if (oid == "2.5.29.21") reason = Enumerated(new AsnReader(value, AsnEncodingRules.DER));
                        }
                    }
                    return Status.RevokedOrHeld(thisUpdate, nextUpdate, revokedAt, reason);
                }
            }
            // not listed: „good“ only from a complete list that covers the certificate (docs/FEHLERGEDAECHTNIS.md, No. 482)
            if (!CompleteFor(list, certificate, thisUpdate)) return Status.Unusable;
            return new Status(StatusKind.Good, thisUpdate, nextUpdate);
        }
        catch (Exception e) when (e is AsnContentException or CryptographicException)
        {
            return Status.Unusable;
        }
    }

    /// <summary>
    /// Whether a CRL in which the certificate is not listed says „good“ for it (RFC 5280, 5.2 and 6.3.3): no delta CRL (DeltaCRLIndicator —
    /// only together with its base), no unknown critical extension, an issuing distribution point that covers the certificate (not only
    /// some reasons, not indirect, not only attribute certificates, user or authority certificates as the certificate is, its distribution
    /// point among those of the certificate), and, issued after the certificate expired, only with ExpiredCertsOnCRL from before the expiry —
    /// lists drop expired certificates.
    /// </summary>
    /// <param name="list">The TBSCertList reader after the revoked certificates.</param>
    private static bool CompleteFor(AsnReader list, X509Certificate2 certificate, DateTimeOffset thisUpdate)
    {
        DateTimeOffset? expiredOnList = null;
        if (list.HasData && list.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
        {
            var extensions = list.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)).ReadSequence();
            for (var i = 0; extensions.HasData && i < 64; i++)
            {
                var extension = extensions.ReadSequence();
                var oid = extension.ReadObjectIdentifier();
                var critical = extension.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean) && extension.ReadBoolean();
                var value = extension.ReadOctetString();
                switch (oid)
                {
                    case "2.5.29.27":   // deltaCRLIndicator
                        return false;
                    case "2.5.29.28":   // issuingDistributionPoint
                        if (!Covers(value, certificate)) return false;
                        break;
                    case "2.5.29.60":   // expiredCertsOnCRL
                        expiredOnList = ReadTime(new AsnReader(value, AsnEncodingRules.DER));
                        break;
                    case "2.5.29.20" or "2.5.29.35" or "2.5.29.46" or "1.3.6.1.5.5.7.1.1":   // number, key id, freshest CRL, access
                        break;
                    default:
                        if (critical) return false;
                        break;
                }
            }
        }
        var notAfter = new DateTimeOffset(certificate.NotAfter);
        return thisUpdate <= notAfter || expiredOnList <= notAfter;
    }

    /// <summary>Whether an issuing distribution point (RFC 5280, 5.2.5) covers the certificate.</summary>
    private static bool Covers(byte[] encoded, X509Certificate2 certificate)
    {
        var point = new AsnReader(encoded, AsnEncodingRules.DER).ReadSequence();
        var authority = certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault()?.CertificateAuthority == true;
        while (point.HasData)
        {
            var tag = point.PeekTag();
            if (tag.TagClass != TagClass.ContextSpecific) return false;
            switch (tag.TagValue)
            {
                case 0:   // distributionPoint: its names must be among those the certificate gives
                    var names = Uris(point.ReadEncodedValue());
                    var own = certificate.Extensions.FirstOrDefault(e => e.Oid?.Value == "2.5.29.31") is { } points ? Uris(points.RawData) : [];
                    if (names.Count == 0 || own.Count > 0 && !names.Overlaps(own)) return false;
                    break;
                case 1 or 2 or 4 or 5:   // only user / only authority / indirect / only attribute certificates
                    var flag = point.ReadBoolean(new Asn1Tag(TagClass.ContextSpecific, tag.TagValue));
                    if (flag && (tag.TagValue is 4 or 5 || tag.TagValue == 1 && authority || tag.TagValue == 2 && !authority)) return false;
                    break;
                default:   // onlySomeReasons and anything else: a part of the list only
                    return false;
            }
        }
        return true;
    }

    /// <summary>The URIs (GeneralName [6]) anywhere in an encoded structure, bounded.</summary>
    private static HashSet<string> Uris(ReadOnlyMemory<byte> encoded)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(ReadOnlyMemory<byte> data, int depth)
        {
            if (depth > 8) return;
            var reader = new AsnReader(data, AsnEncodingRules.BER);
            while (reader.HasData && found.Count < 32)
            {
                var tag = reader.PeekTag();
                var value = reader.ReadEncodedValue();
                if (tag is { TagClass: TagClass.ContextSpecific, TagValue: 6, IsConstructed: false })
                    found.Add(System.Text.Encoding.ASCII.GetString(ContentOf(value).Span));
                else if (tag.IsConstructed) Walk(ContentOf(value), depth + 1);
            }
        }
        Walk(encoded, 0);
        return found;
    }

    /// <summary>The content octets of an encoded value (definite length).</summary>
    private static ReadOnlyMemory<byte> ContentOf(ReadOnlyMemory<byte> encoded)
    {
        var span = encoded.Span;
        var i = 1;
        if ((span[0] & 0x1F) == 0x1F) while ((span[i++] & 0x80) != 0) { }
        int first = span[i++], length = first;
        if ((first & 0x80) != 0)
        {
            length = 0;
            for (var k = 0; k < (first & 0x7F); k++) length = (length << 8) | span[i++];
        }
        return encoded.Slice(i, Math.Min(length, encoded.Length - i));
    }

    /// <summary>The archiveCutoff of an OCSP response (responseExtensions, RFC 6960, 4.4.4); null without one.</summary>
    private static DateTimeOffset? ArchiveCutoff(AsnReader data)
    {
        if (!data.HasData || !data.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 1, true))) return null;
        var extensions = data.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1, true)).ReadSequence();
        while (extensions.HasData)
        {
            var extension = extensions.ReadSequence();
            var oid = extension.ReadObjectIdentifier();
            if (extension.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean)) extension.ReadBoolean();
            var value = extension.ReadOctetString();
            if (oid == "1.3.6.1.5.5.7.48.1.6") return new AsnReader(value, AsnEncodingRules.DER).ReadGeneralizedTime();
        }
        return null;
    }

    private static DateTimeOffset ReadTime(AsnReader reader) =>
        reader.PeekTag().HasSameClassAndValue(Asn1Tag.UtcTime) ? reader.ReadUtcTime() : reader.ReadGeneralizedTime();

    /// <summary>Whether a certificate's signature verifies with another certificate's key.</summary>
    internal static bool IssuedBy(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        try
        {
            var outer = new AsnReader(certificate.RawData, AsnEncodingRules.DER).ReadSequence();
            var tbs = outer.ReadEncodedValue();
            var algorithm = outer.ReadSequence().ReadObjectIdentifier();
            return Verify(tbs.Span, algorithm, outer.ReadBitString(out _), issuer);
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    /// <summary>Verifies signed data with the key of a certificate (RSA with SHA-1/256/384/512, ECDSA with SHA-256/384/512).</summary>
    internal static bool Verify(ReadOnlySpan<byte> data, string algorithm, ReadOnlySpan<byte> signature, X509Certificate2 key)
    {
        HashAlgorithmName? hash = algorithm switch
        {
            "1.2.840.113549.1.1.5" or "1.2.840.10045.4.1" => HashAlgorithmName.SHA1,
            "1.2.840.113549.1.1.11" or "1.2.840.10045.4.3.2" => HashAlgorithmName.SHA256,
            "1.2.840.113549.1.1.12" or "1.2.840.10045.4.3.3" => HashAlgorithmName.SHA384,
            "1.2.840.113549.1.1.13" or "1.2.840.10045.4.3.4" => HashAlgorithmName.SHA512,
            _ => null,
        };
        if (hash is not { } name) return false;
        if (algorithm.StartsWith("1.2.840.113549", StringComparison.Ordinal))
        {
            using var rsa = CertificateKeys.Rsa(key);
            return rsa is not null && rsa.VerifyData(data, signature, name, RSASignaturePadding.Pkcs1);
        }
        using var ecdsa = key.GetECDsaPublicKey();
        return ecdsa is not null && ecdsa.VerifyData(data, signature, name, DSASignatureFormat.Rfc3279DerSequence);
    }
}
