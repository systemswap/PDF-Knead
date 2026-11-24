using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Pdf.Signatures.TrustLists;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Signatures;

/// <summary>Overall result of checking a digital signature.</summary>
public enum SignatureOutcome
{
    /// <summary>Signature fits the signed bytes, the certificate leads to a trusted root of the system.</summary>
    Valid,
    /// <summary>Signature fits the signed bytes, but nothing confirms who holds the certificate (self-signed, unknown issuer).</summary>
    ValidIdentityUnknown,
    /// <summary>The signature itself fits, but a later revision changed more than filling fields, signing or commenting.</summary>
    ChangedAfterSigning,
    /// <summary>The signature does not fit (see <see cref="SignatureCheck.Problem"/>).</summary>
    Invalid,
    /// <summary>The method is not supported or the file is too large to check.</summary>
    NotChecked,
    /// <summary>
    /// The signature fits the signed bytes, but a condition cannot be confirmed (EN 319 102-1: INDETERMINATE) — a certificate revoked
    /// without proof that it was signed before, the revocation status not checked, an unsafe method …: yellow, the reason in
    /// <see cref="SignatureCheck.UnconfirmedReason"/> (docs/ABLAUFPLAN.md, 2.5).
    /// </summary>
    Unconfirmed,
}

/// <summary>The result of checking one digital signature (see <see cref="SignatureVerifier"/>).</summary>
/// <param name="FieldName">Full name of the signature field.</param>
/// <param name="Outcome">The overall result.</param>
/// <param name="Problem">Why the signature is invalid or not checked (localised); otherwise null.</param>
/// <param name="Later">What later revisions of the file changed.</param>
/// <param name="LaterDetails">What exactly changed outside the allowed changes (localised, at most a few lines).</param>
/// <param name="SignerName">Name of the signer from the certificate, otherwise from <c>/Name</c>.</param>
/// <param name="SigningTime">Time of signing as the signer stated it (<c>/M</c> or the signing-time attribute); not confirmed by a third party.</param>
/// <param name="Reason">Stated reason (<c>/Reason</c>).</param>
/// <param name="Location">Stated location (<c>/Location</c>).</param>
/// <param name="Method">The signature method (<c>/SubFilter</c>).</param>
/// <param name="Certificate">The signer's certificate, as far as it could be read.</param>
/// <param name="IdentityConfirmed">The certificate leads to a root certificate the system trusts, or to a qualified authority the loaded EU
/// trusted lists grant — at the time a trusted time stamp confirms, otherwise today.</param>
/// <param name="CoversWholeFile">The signature covers the file up to its end (no later revisions).</param>
/// <param name="Page">Page of the visible field (zero-based); null if it is invisible or not found.</param>
/// <param name="Certification">For the certification signature: what it allows afterwards; null for an ordinary signature.</param>
/// <param name="Lock">The fields this signature locks; null if it locks none.</param>
public sealed record SignatureCheck(
    string FieldName,
    SignatureOutcome Outcome,
    string? Problem,
    LaterChanges Later,
    IReadOnlyList<string> LaterDetails,
    string? SignerName,
    DateTimeOffset? SigningTime,
    string? Reason,
    string? Location,
    string Method,
    CertificateSummary? Certificate,
    bool IdentityConfirmed,
    bool CoversWholeFile,
    int? Page,
    CertificationLevel? Certification = null,
    FieldLock? Lock = null)
{
    /// <summary>Things to know that do not make the signature invalid (certificate not meant for signing, expired, outdated method …), localised.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Why the result is <see cref="SignatureOutcome.Unconfirmed"/> (shown first), localised; null otherwise.</summary>
    public string? UnconfirmedReason { get; init; }

    /// <summary>
    /// The cause of a result short of „valid“ as a sub-indication of EN 319 102-1 (<see cref="SubIndication"/>); null when the default for
    /// the outcome applies. Read through <see cref="SignatureTexts.Indication"/>, never on its own.
    /// </summary>
    public string? Cause { get; init; }

    /// <summary>
    /// Not confirmed and the integrity not proven either: an unsafe method (MD5, too short a key) or no signer certificate to check the value
    /// with — then it does not count as valid (<see cref="SignatureTexts.IsValid"/>).
    /// </summary>
    public bool Unproven { get; init; }

    /// <summary>What the validation data of the file says in detail (revoked: since when, why, which certificate); null without data.</summary>
    public RevocationInfo.EmbeddedStatus? Revocation { get; init; }

    /// <summary>
    /// The lock dictionary of the signed field (/Lock, set by the form's author, ISO 32000-2:2020, Table 236), with its /P; null if it has
    /// none. Its fields must not change after signing, whether or not the signature carries it over as FieldMDP (docs/ABLAUFPLAN.md, 2.3).
    /// </summary>
    public FieldLock? FieldLockOfField { get; init; }

    /// <summary>The time stamp of a time-stamping authority on the signature (PAdES B-T), checked; null if it has none.</summary>
    public SignatureTimestamp? Timestamp { get; init; }

    /// <summary>
    /// What the validation data of the file (/DSS, PAdES B-LT) says about the signer's chain and that of its time-stamping authority; null if
    /// the file carries none.
    /// </summary>
    public RevocationInfo.Coverage? LongTerm { get; init; }

    /// <summary>Whether the EU trusted lists were at hand for the check (loaded by the user, KONZEPT.md, section 5.17, „EU-Vertrauensliste“).</summary>
    public bool TrustListsLoaded { get; init; }

    /// <summary>The listed certification service (CA/QC) the signer's certificate comes from; null if none is listed or the lists are not loaded.</summary>
    public TrustListMatch? Issuer { get; init; }

    /// <summary>The listed qualified time-stamping service (TSA/QTST) of the time stamp; null if none, not listed, or the lists are not loaded.</summary>
    public TrustListMatch? TimestampService { get; init; }

    /// <summary>What the loaded EU trusted lists say about the signer's certificate (qualified, kind, device; docs/FEHLERGEDAECHTNIS.md, No. 484).</summary>
    public ListVerdict? Listed { get; init; }

    /// <summary>
    /// A document time stamp (ISO 32000-2:2020, 12.8.5; PAdES B-LTA): a time-stamping authority confirms that the file up to here existed at
    /// <see cref="SignatureCheck.SigningTime"/>; no person signs. <see cref="SignatureCheck.SignerName"/> is the service.
    /// </summary>
    public bool IsDocumentTimestamp { get; init; }

    /// <summary>
    /// For a document time stamp: renew the protection by then at the latest — the earlier of the end of the service's certificate and the end
    /// of suitability of the methods of its token (<see cref="AlgorithmSuitability"/>; TR-ESOR; docs/FEHLERGEDAECHTNIS.md, No. 714); null for
    /// a signature or when nothing ends.
    /// </summary>
    public DateTimeOffset? RenewBefore { get; init; }

    /// <summary>The time a later document time stamp that holds proves this entry existed (docs/FEHLERGEDAECHTNIS.md, No. 712); null without one.</summary>
    public DateTimeOffset? ProvenAt { get; init; }

    /// <summary>Whether a later document time stamp whose token fits covers this entry (trusted or not) — the structure of PAdES B-LTA.</summary>
    public bool CoveredByDocumentTimestamp { get; init; }

    /// <summary>Which rule of RFC 5280 the path to a trusted root breaks, or which link was not valid (docs/FEHLERGEDAECHTNIS.md, No. 711); null otherwise.</summary>
    public string? ChainProblem { get; init; }

    /// <summary>The signed byte ranges (offset and length of the part before and after the block), for the report.</summary>
    public IReadOnlyList<long> ByteRange { get; init; } = [];

    /// <summary>The digest of the signed bytes the block names (signed attribute message-digest), with its method, for the report; null if none.</summary>
    public string? SignedDigest { get; init; }

    /// <summary>The certificates of the path to a root the check found (signer first), for the report; empty without one (docs/FEHLERGEDAECHTNIS.md, No. 723).</summary>
    public IReadOnlyList<CertificateLink> Chain { get; init; } = [];

    /// <summary>
    /// The purpose the signer states (signed attribute commitment-type-indication, ETSI EN 319 122-1, 5.2.3), localised — „Herkunft“, „Genehmigung“
    /// …; shown, not evaluated (I-13); null without one.
    /// </summary>
    public string? Commitment { get; init; }
}

/// <summary>What is shown of a certificate.</summary>
/// <param name="Subject">Holder (common name, otherwise the whole name).</param>
/// <param name="Issuer">Issuer (common name, otherwise the whole name).</param>
/// <param name="SelfSigned">Holder and issuer are the same.</param>
/// <param name="ValidFrom">Start of validity.</param>
/// <param name="ValidUntil">End of validity.</param>
/// <param name="Fingerprint">SHA-256 fingerprint in groups of four hexadecimal digits.</param>
/// <param name="Level">The level of signature the certificate gives by its own statements (<see cref="SignatureLevels"/>).</param>
public sealed record CertificateSummary(string Subject, string Issuer, bool SelfSigned, DateTimeOffset ValidFrom, DateTimeOffset ValidUntil, string Fingerprint,
    SignatureLevel Level = SignatureLevel.Simple)
{
    /// <summary>What the certificate states about kind and level (QC statements, semantics, subject, key usage; docs/ABLAUFPLAN.md, 2.6).</summary>
    public CertificateProfile Profile { get; init; } = CertificateProfile.None;
}

/// <summary>
/// Checks the digital signatures of a document (ISO 32000-2:2020, section 12.8; PAdES, ETSI EN 319 142-1): that the byte ranges
/// leave out exactly the signature's own <c>/Contents</c> and end at the end of a revision, that the CMS signature fits these bytes,
/// whether the certificate leads to a trusted root, and what later revisions changed (<see cref="RevisionChanges"/>).
/// </summary>
/// <remarks>
/// Works offline: no certificates are downloaded and revocation is not checked, trust comes only from the root certificates
/// installed in the system. The signing time is the one the signer stated; without a time stamp nothing confirms it. The signed
/// bytes are held in memory while checking.
/// </remarks>
public static class SignatureVerifier
{
    /// <summary>Signed parts larger than this are not checked (they would be held in memory).</summary>
    public const long MaxSignedBytes = 1L << 30;

    /// <summary>At most this many signatures of a file are checked (each one hashes its signed part).</summary>
    public const int MaxSignatures = 100;

    private static readonly PdfName SubFilter = PdfName.Get("SubFilter"), Contents = PdfName.Get("Contents"), Modified = PdfName.Get("M"),
        Reason = PdfName.Get("Reason"), Location = PdfName.Get("Location"), Page = PdfName.Get("P"), Annots = PdfName.Get("Annots");

    private const string SigningTimeOid = "1.2.840.113549.1.9.5";

    /// <summary>Checks all signatures of the document.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="password">Password of an encrypted document (to read earlier revisions); null if none.</param>
    /// <param name="cancel">Cancels between signatures.</param>
    /// <returns>One result per signature, in field order; empty if the document has none.</returns>
    public static IReadOnlyList<SignatureCheck> Verify(PdfDocument doc, string? password = null, CancellationToken cancel = default) =>
        Verify(doc, password, null, cancel);

    /// <param name="trustedRoots">Root certificates to trust instead of those of the system (tests); null = the system's.</param>
    /// <param name="trustLists">The EU trusted lists; null = those the user loaded (<see cref="TrustListStore.Current"/>), but none when
    /// <paramref name="trustedRoots"/> are given (tests).</param>
    internal static IReadOnlyList<SignatureCheck> Verify(PdfDocument doc, string? password, X509Certificate2Collection? trustedRoots, CancellationToken cancel,
        Func<TrustListSet?>? trustLists = null)
    {
        var entries = DigitalSignatures.Find(doc);
        // fields that only look signed (a signature dictionary without usable byte ranges or block): listed as broken, never left out
        var unusable = DigitalSignatures.Unusable(doc).Take(MaxSignatures)
            .Select(e => Start(doc, e, null, false) with { Problem = Tr.T("Die Signatur ist unvollständig: Byte-Bereiche oder Signaturblock fehlen oder sind ungültig.") })
            .ToList();
        if (entries.Count == 0) return unusable;
        if (entries.Count > MaxSignatures) entries = [.. entries.Take(MaxSignatures)];   // each check hashes the signed part
        var revisions = new Dictionary<long, PdfDocument?>();
        // The first signature may certify the document; its level (from its own, signed dictionary) holds for everything after it — as
        // long as its byte ranges still fit the file: a certified file written anew (its certification broken, /Perms gone with 539) is not
        // judged by it any more, as appending is not either (SignaturePermissions); it is still shown as a certification
        // (docs/FEHLERGEDAECHTNIS.md, No. 550)
        var first = entries.MinBy(e => e.ByteRange[2] + e.ByteRange[3])!;
        var certification = first.Signature is { } firstSignature ? SignaturePermissions.Level(firstSignature) : null;
        var restriction = first.Intact ? certification : null;
        var roots = trustedRoots is null ? new Lazy<X509Certificate2Collection>(SystemRoots) : new Lazy<X509Certificate2Collection>(() => trustedRoots);
        var store = new Lazy<RevocationInfo.Found?>(() => LongTermValidation.Read(doc));
        var lists = new Lazy<TrustListSet?>(trustLists ?? (trustedRoots is null ? () => TrustListStore.Current() : () => null));
        try
        {
            var results = new List<SignatureCheck>(entries.Count);
            var covered = new HashSet<DigitalSignatureEntry>(ReferenceEqualityComparer.Instance);
            var proofs = Proofs(doc, entries, roots, store, lists, covered, cancel);
            foreach (var entry in entries)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    results.Add(Check(doc, entry, password, revisions, roots, store, lists, certification, restriction, ReferenceEquals(entry, first),
                        proofs.TryGetValue(entry, out var proven) ? proven : null) with
                        {
                            ProvenAt = proofs.TryGetValue(entry, out var at) ? at : null, CoveredByDocumentTimestamp = covered.Contains(entry),
                        });
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Whatever a prepared signature makes the libraries throw: this signature is damaged, the others are still checked
                    results.Add(Start(doc, entry, certification, ReferenceEquals(entry, first)) with { Problem = Tr.T("Der Signaturblock ist beschädigt.") });
                }
            }
            TimestampOrder(entries, results);
            results.AddRange(unusable);
            return results;
        }
        finally
        {
            foreach (var revision in revisions.Values) revision?.Dispose();
            if (trustedRoots is null && roots.IsValueCreated)
                foreach (var root in roots.Value) root.Dispose();
        }
    }

    /// <summary>
    /// The proofs of existence the document time stamps give (EN 319 102-1, 5.6.2.3 and 5.6.3.4, steps 5–7; docs/FEHLERGEDAECHTNIS.md, No. 712):
    /// from the newest to the oldest, each stamp is checked — its token made for exactly its byte ranges, its service trusted and not revoked —
    /// at the time a newer one proves it existed (the newest today); a stamp that holds proves at its time every signature and stamp whose
    /// revision ends before its block. The earliest proof per entry. Only stamps that hold give proofs, so a forged older stamp proves nothing.
    /// </summary>
    private static Dictionary<DigitalSignatureEntry, DateTimeOffset> Proofs(PdfDocument doc, IReadOnlyList<DigitalSignatureEntry> entries,
        Lazy<X509Certificate2Collection> roots, Lazy<RevocationInfo.Found?> store, Lazy<TrustListSet?> lists, HashSet<DigitalSignatureEntry> covered,
        CancellationToken cancel)
    {
        var proofs = new Dictionary<DigitalSignatureEntry, DateTimeOffset>(ReferenceEqualityComparer.Instance);
        var stamps = entries.Where(e => e.Intact && e.Signature?.GetName(SubFilter)?.Value == "ETSI.RFC3161" && e.ByteRange[2] + e.ByteRange[3] <= MaxSignedBytes)
            .OrderByDescending(e => e.ByteRange[2] + e.ByteRange[3]).ToList();
        foreach (var stamp in stamps)
        {
            cancel.ThrowIfCancellationRequested();
            var at = proofs.TryGetValue(stamp, out var known) ? known : DateTimeOffset.Now;
            var (fits, proves) = StampProves(doc, stamp, roots, store, lists, at);
            if (fits)
                foreach (var entry in entries)
                    if (!ReferenceEquals(entry, stamp) && entry.ByteRange[2] + entry.ByteRange[3] <= stamp.ByteRange[1]) covered.Add(entry);
            if (proves is not { } time) continue;
            foreach (var entry in entries)
                if (!ReferenceEquals(entry, stamp) && entry.ByteRange[2] + entry.ByteRange[3] <= stamp.ByteRange[1]
                    && (!proofs.TryGetValue(entry, out var earlier) || time < earlier))
                    proofs[entry] = time;
        }
        return proofs;
    }

    /// <summary>
    /// Whether a document time stamp's token fits its byte ranges, and the time it proves, checked at <paramref name="at"/> (null if it does not
    /// hold then).
    /// </summary>
    private static (bool Fits, DateTimeOffset? Proves) StampProves(PdfDocument doc, DigitalSignatureEntry entry, Lazy<X509Certificate2Collection> roots,
        Lazy<RevocationInfo.Found?> store, Lazy<TrustListSet?> lists, DateTimeOffset at)
    {
        try
        {
            var range = entry.ByteRange;
            if (!EndsRevision(doc, range[2] + range[3]) || Hex(doc.FileBytes(range[1] + 1, range[2] - range[1] - 2)) is not { } block
                || (!doc.IsEncrypted && !block.AsSpan().SequenceEqual(entry.Signature!.GetString(Contents)!.Bytes))
                || CmsSignature.Der(block) is not { } der || !Rfc3161TimestampToken.TryDecode(der, out var token, out _)
                || StampHash(token) is not { } name)
                return (false, null);
            var tokenCms = token.AsSignedCms();
            if (!token.VerifySignatureForHash(Digest(doc, range, name), name, out var authority, tokenCms.Certificates) || authority is null) return (false, null);
            using (authority)
            {
                var time = token.TokenInfo.Timestamp;
                var trusted = (roots.Value.Count > 0 && SignatureTimestamps.Trusted(authority, tokenCms.Certificates, roots.Value, at))
                              || lists.Value?.Find(authority, tokenCms.Certificates, TrustedList.QualifiedTimestamps, time) is { Qualified: true };
                return (true, trusted && !ServiceRevoked(authority, tokenCms.Certificates, store.Value, time) ? time : null);
            }
        }
        catch (Exception e) when (e is CryptographicException or PdfException or ArgumentException or InvalidOperationException)
        {
            return (false, null);   // a damaged stamp proves nothing; the check of the entry itself says why
        }
    }

    /// <summary>The digest a time-stamp token names, if it is one PDF Knead checks.</summary>
    private static HashAlgorithmName? StampHash(Rfc3161TimestampToken token) => token.TokenInfo.HashAlgorithmId.Value switch
    {
        "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
        "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
        "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
        "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
        _ => null,
    };

    /// <summary>Whether the file's validation data say the service's certificate was revoked at or before the time it stamped.</summary>
    private static bool ServiceRevoked(X509Certificate2 authority, X509Certificate2Collection came, RevocationInfo.Found? data, DateTimeOffset time)
    {
        if (data is null) return false;
        var extra = new X509Certificate2Collection(came);
        var loaded = new List<X509Certificate2>();
        try
        {
            foreach (var raw in data.Certificates)
                try
                {
                    var certificate = X509CertificateLoader.LoadCertificate(raw);
                    loaded.Add(certificate);
                    extra.Add(certificate);
                }
                catch (CryptographicException)
                {
                    // an unreadable entry of the store
                }
            var revocation = RevocationInfo.Embedded(authority, extra, data.Ocsps, data.Crls, time);
            return revocation.Coverage == RevocationInfo.Coverage.Revoked && (revocation.RevokedAt is not { } revokedAt || revokedAt <= time);
        }
        finally
        {
            foreach (var certificate in loaded) certificate.Dispose();
        }
    }

    private static SignatureCheck Check(PdfDocument doc, DigitalSignatureEntry entry, string? password, Dictionary<long, PdfDocument?> revisions,
        Lazy<X509Certificate2Collection> roots, Lazy<RevocationInfo.Found?> store, Lazy<TrustListSet?> lists, CertificationLevel? certification,
        CertificationLevel? restriction, bool isFirst, DateTimeOffset? proven)
    {
        var sig = entry.Signature!;
        var method = sig.GetName(SubFilter)?.Value ?? "";
        var result = Start(doc, entry, certification, isFirst);
        if (!entry.Intact)
            return result with { Problem = Tr.T("Die Byte-Bereiche der Signatur passen nicht zur Datei: sie wurde nach dem Unterschreiben neu geschrieben oder verändert.") };

        var range = entry.ByteRange;
        var end = range[2] + range[3];
        if (!EndsRevision(doc, end))
            return result with { Problem = Tr.T("Die Signatur deckt keine vollständige Fassung der Datei ab.") };
        var wholeFile = IsBlank(doc.FileBytes(end, doc.FileSize - end));
        result = result with { CoversWholeFile = wholeFile };
        if (end > MaxSignedBytes)
            return result with { Outcome = SignatureOutcome.NotChecked, Problem = Tr.T("Die Datei ist zu groß zum Prüfen.") };

        // The gap holds exactly this dictionary's /Contents: a signature cannot borrow the block of another one
        if (Hex(doc.FileBytes(range[1] + 1, range[2] - range[1] - 2)) is not { } block
            || (!doc.IsEncrypted && !block.AsSpan().SequenceEqual(sig.GetString(Contents)!.Bytes)))
            return result with { Problem = Tr.T("Der Signaturblock gehört nicht zu dieser Signatur.") };
        // a block of zeros is the space reserved for a signature that was never made (docs/FEHLERGEDAECHTNIS.md, No. 344)
        if (!block.AsSpan().ContainsAnyExcept((byte)0)) return result with { Problem = Tr.T("Der Signaturblock ist leer: hier wurde nie unterschrieben.") };
        if (CmsSignature.Der(block) is not { } der || !Decodable(der)) return result with { Problem = Tr.T("Der Signaturblock ist beschädigt.") };
        if (method == "ETSI.RFC3161")
        {
            // the token's protected statement of the algorithms (RFC 6211, also in a time stamp's SignedData; No. 1119)
            if (!CmsSignature.AlgorithmProtectionHolds(der))
                return result with { Problem = Tr.T("Die geschützte Angabe der Verfahren im Signaturblock passt nicht zu den verwendeten: der Signaturblock ist verändert oder fehlerhaft."), Cause = SubIndication.SigCryptoFailure };
            var documentStamp = DocumentTimestamp(doc, range, der, result, roots, store, lists, proven);
            return documentStamp.Problem is not null ? documentStamp : Finish(doc, documentStamp, range, end, wholeFile, password, revisions, restriction);
        }

        SignedCms cms;
        var carriesCopy = false;
        try
        {
            switch (method)
            {
                case "adbe.pkcs7.detached" or "ETSI.CAdES.detached":
                    // a detached block signs the bytes of the file only: content of its own would be signed in their place, and the
                    // platform's check takes it without a word (docs/FEHLERGEDAECHTNIS.md, No. 344)
                    switch (CmsSignature.FlawOf(der))
                    {
                        // the bytes of the file carried once more in the block: signed all the same, though not as the method asks
                        case CmsSignature.Flaw.CarriesContent when CmsSignature.Carried(der) is { } carried && carried.AsSpan().SequenceEqual(Content(doc, range)):
                            carriesCopy = true;
                            break;
                        case CmsSignature.Flaw.CarriesContent:
                            return result with { Problem = Tr.T("Der Signaturblock unterschreibt eigene Daten statt der Datei.") };
                        case CmsSignature.Flaw.SignatureAsDigest:
                            return result with { Problem = Tr.T("Der Signaturblock nennt ein Signaturverfahren, wo ein Prüfsummenverfahren stehen muss (RFC 5652) — so ist er nicht normgerecht.") };
                    }
                    // The digest is streamed over the byte ranges (no copy of the file); methods not handled there are checked with the
                    // content as one array
                    cms = new SignedCms(new ContentInfo([]), detached: true);
                    cms.Decode(der);
                    var check = cms.SignerInfos.Count == 1 && cms.SignerInfos[0].Certificate is { } signer
                        ? CmsSignature.CheckDigest(der, signer, hash => Digest(doc, range, hash))
                        : CmsSignature.DigestCheck.Unsupported;
                    if (check == CmsSignature.DigestCheck.Invalid) throw new CryptographicException();
                    // RSASSA-PSS with parameters the platform does not verify: not checked, never „changed“ (No. 537)
                    if (check == CmsSignature.DigestCheck.UnsupportedParameters)
                        return result with { Outcome = SignatureOutcome.NotChecked, Problem = Tr.T("Das Signaturverfahren {0} wird nicht geprüft.", Tr.T("RSASSA-PSS mit diesen Parametern")) };
                    // signed attributes without a message digest: a form error of the block, not a change of the document (No. 483)
                    if (check == CmsSignature.DigestCheck.NoMessageDigest)
                        return result with { Problem = Tr.T("Der Signaturblock ist nicht normgerecht (die Prüfsumme der unterschriebenen Daten fehlt).") };
                    // CMSAlgorithmProtection naming other algorithms than the signer info (RFC 6211): an algorithm substituted — broken (No. 1113)
                    if (check == CmsSignature.DigestCheck.AlgorithmMismatch)
                        return result with { Problem = Tr.T("Die geschützte Angabe der Verfahren im Signaturblock passt nicht zu den verwendeten: der Signaturblock ist verändert oder fehlerhaft."), Cause = SubIndication.SigCryptoFailure };
                    // the bytes are those signed, the value is not the signer's: no change of the document, a forged or broken block
                    if (check == CmsSignature.DigestCheck.ValueMismatch)
                        return result with { Problem = Tr.T("Der Signaturwert passt nicht zum Zertifikat des Unterzeichners: der Signaturblock ist gefälscht oder beschädigt."), Cause = SubIndication.SigCryptoFailure };
                    if (cms.SignerInfos.Count == 1 && cms.SignerInfos[0].Certificate is null)
                        return NoSignerCertificate(result);
                    if (check == CmsSignature.DigestCheck.Unsupported)
                    {
                        // a method the platform cannot verify: not checked — its throw is no proof of a change (docs/ABLAUFPLAN.md, 2.4)
                        if (cms.SignerInfos.Count == 1 && CmsSignature.NotComputable(cms.SignerInfos[0]) is { } unsupported)
                            return result with { Outcome = SignatureOutcome.NotChecked, Problem = Tr.T("Das Signaturverfahren {0} wird nicht geprüft.", unsupported) };
                        cms = new SignedCms(new ContentInfo(Content(doc, range)), detached: true);
                        cms.Decode(der);
                        // (decoding a block with content of its own replaces the bytes of the file with it)
                        if (!cms.Detached && !carriesCopy) return result with { Problem = Tr.T("Der Signaturblock unterschreibt eigene Daten statt der Datei.") };
                        cms.CheckSignature(verifySignatureOnly: true);
                        // the protected statement of the algorithms on this way too (RFC 6211; No. 1119)
                        if (!CmsSignature.AlgorithmProtectionHolds(der))
                            return result with { Problem = Tr.T("Die geschützte Angabe der Verfahren im Signaturblock passt nicht zu den verwendeten: der Signaturblock ist verändert oder fehlerhaft."), Cause = SubIndication.SigCryptoFailure };
                    }
                    break;
                case "adbe.pkcs7.sha1":
                    // Older method: the CMS block signs the SHA-1 digest of the bytes, carried inside the block
                    cms = new SignedCms();
                    cms.Decode(der);
                    // the same rule as for the detached methods (RFC 5652, 5.3): a signature algorithm is no digest algorithm
                    if (CmsSignature.SignerFlaws(der).SignatureAsDigest)
                        return result with { Problem = Tr.T("Der Signaturblock nennt ein Signaturverfahren, wo ein Prüfsummenverfahren stehen muss (RFC 5652) — so ist er nicht normgerecht.") };
                    if (cms.SignerInfos.Count == 1 && CmsSignature.NotComputable(cms.SignerInfos[0]) is { } unsupportedSha1)
                        return result with { Outcome = SignatureOutcome.NotChecked, Problem = Tr.T("Das Signaturverfahren {0} wird nicht geprüft.", unsupportedSha1) };
                    cms.CheckSignature(verifySignatureOnly: true);
                    // the protected statement of the algorithms (RFC 6211; No. 1119)
                    if (!CmsSignature.AlgorithmProtectionHolds(der))
                        return result with { Problem = Tr.T("Die geschützte Angabe der Verfahren im Signaturblock passt nicht zu den verwendeten: der Signaturblock ist verändert oder fehlerhaft."), Cause = SubIndication.SigCryptoFailure };
                    if (!cms.ContentInfo.Content.AsSpan().SequenceEqual(Digest(doc, range, HashAlgorithmName.SHA1)))
                        return result with { Problem = Tr.T("Die Signatur passt nicht zum Inhalt: das Dokument wurde verändert."), Cause = SubIndication.HashFailure };
                    break;
                default:
                    return result with
                    {
                        Outcome = SignatureOutcome.NotChecked,
                        Problem = Tr.T("Das Verfahren {0} wird nicht geprüft.", method),
                    };
            }
        }
        catch (CryptographicException)
        {
            // only methods the platform can verify reach this (CmsSignature.NotComputable before each check): their failure is a change
            return result with { Problem = Tr.T("Die Signatur passt nicht zum Inhalt: das Dokument wurde verändert."), Cause = SubIndication.HashFailure };
        }
        var unsortedAttributes = CmsSignature.SignerFlaws(der).UnsortedAttributes;
        if (cms.SignerInfos.Count > 1)
            // a PDF signature has exactly one signer (ISO 32000-2, 12.8.3.3): which of them signed stays open — not confirmed
            return NoSignerCertificate(result, Tr.T("Der Signaturblock enthält mehr als einen Unterzeichner; vorgesehen ist genau einer."));
        if (cms.SignerInfos.Count != 1 || cms.SignerInfos[0].Certificate is not { } certificate)
            return NoSignerCertificate(result);
        var warnings = new List<string>();
        // signed attributes not in DER order (RFC 5652, 5.3): the signer signed exactly these bytes and the document is unchanged — valid, but
        // said: validators that re-encode the attributes before checking (the reference among them, mostly) find another value and reject it
        // (decided on 4.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 476)
        if (unsortedAttributes) warnings.Add(Tr.T("Die signierten Attribute stehen nicht in der vorgeschriebenen Reihenfolge (DER, RFC 5652); strenge Prüfprogramme lehnen diese Signatur deshalb ab."));
        if (carriesCopy) warnings.Add(Tr.T("Der Signaturblock trägt die unterschriebenen Daten noch einmal in sich; bei diesem Verfahren ist das nicht vorgesehen."));
        // the certificates of a block are not signed: the signed attribute signing-certificate binds the one the signature was made with
        // (ISO 32000-2:2020, 12.8.3.4.5 a) — without it, a certificate with the same key, issuer and serial number but another holder can be
        // put in its place (docs/ABLAUFPLAN.md, 2.2)
        var binding = CmsSignature.BindingOf(cms.SignerInfos[0], certificate);
        if (binding == CmsSignature.CertificateBinding.Mismatch)
            return result with { Problem = Tr.T("Der Signaturblock nennt ein anderes Zertifikat als das, mit dem unterschrieben wurde: das Zertifikat wurde ausgetauscht."), Cause = SubIndication.SigCryptoFailure };
        if (binding == CmsSignature.CertificateBinding.Malformed)
            return result with { Problem = Tr.T("Der Signaturblock ist nicht normgerecht (Angabe zum Zertifikat des Unterzeichners).") };
        var bound = binding is CmsSignature.CertificateBinding.Matches or CmsSignature.CertificateBinding.IssuerSerialDiffers
                    || binding == CmsSignature.CertificateBinding.Missing && method != "ETSI.CAdES.detached";
        if (binding == CmsSignature.CertificateBinding.IssuerSerialDiffers)
            warnings.Add(Tr.T("Die Angabe zum Zertifikat im Signaturblock nennt einen anderen Aussteller oder eine andere Seriennummer; die Prüfsumme bindet das Zertifikat trotzdem."));
        else if (binding == CmsSignature.CertificateBinding.UnknownHash)
            warnings.Add(Tr.T("Welches Zertifikat unterschrieben hat, lässt sich nicht prüfen (unbekanntes Prüfsummenverfahren der Angabe zum Zertifikat)."));
        else if (!bound)
            warnings.Add(Tr.T("Der Signaturblock bindet das Zertifikat des Unterzeichners nicht (signing-certificate fehlt); bei PAdES ist das Pflicht."));
        if (method == "ETSI.CAdES.detached") warnings.AddRange(PadesHints(cms.SignerInfos[0], sig));
        // the form's author asked for a lock the signature does not carry over (signed by another program): it holds all the same (Table 236)
        if (result.FieldLockOfField is { } fieldLock && (result.Lock is null || !fieldLock.SameAs(result.Lock)))
            warnings.Add(Tr.T("Das Signaturfeld sieht eine Feldsperre vor, die die Signatur nicht enthält."));
        // an unsafe method or a too short key: not confirmed (EN 319 102-1, 5.2.8.4.1: CRYPTO_CONSTRAINTS_FAILURE_NO_POE), the other checks go
        // on; the integrity is no longer proven (decision D, docs/ABLAUFPLAN.md, 2.5)
        var weak = Weakness(cms.SignerInfos[0], certificate, warnings);
        if (!ContentTypeMatches(cms)) return result with { Problem = Tr.T("Der Signaturblock widerspricht sich (Art des unterschriebenen Inhalts).") };
        // signed attributes without content-type (RFC 5652, 5.3: required with signed attributes): said, the signature is still checked
        if (cms.SignerInfos[0].SignedAttributes.Count > 0
            && !cms.SignerInfos[0].SignedAttributes.Cast<CryptographicAttributeObject>().Any(a => a.Oid.Value == "1.2.840.113549.1.9.3"))
            warnings.Add(Tr.T("Den signierten Attributen fehlt die Art des Inhalts (content-type); RFC 5652 verlangt sie."));

        var time = result.SigningTime ?? SigningTimeOf(cms.SignerInfos[0]);
        // the policy and the purpose the signer names: said, not evaluated — PDF Knead checks by its own implicit constraints (I-13)
        var (policy, commitment) = PolicyAndCommitment(cms.SignerInfos[0]);
        if (policy is not null) warnings.Add(Tr.T("Die Signatur nennt die Signaturrichtlinie {0}; sie wurde nicht ausgewertet.", policy));
        result = result with { Commitment = commitment, SignedDigest = MessageDigest(cms.SignerInfos[0]) };
        // a time stamp confirms the time by a third party (PAdES B-T)
        var stamp = SignatureTimestamps.Of(cms.SignerInfos[0], roots.Value, proven);
        result = result with
        {
            SignerName = Name(certificate.SubjectName, certificate) ?? result.SignerName,
            SigningTime = time,
            Certificate = Summary(certificate),
            Timestamp = stamp,
        };
        // the EU trusted lists, if loaded: is the time-stamping service a qualified one (then its time counts even without a system root)
        var trust = lists.Value;
        var chains = LongTermValidation.ChainsOf(cms.Encode());
        var timestampService = trust is not null && stamp is { Valid: true } && chains.Skip(1).FirstOrDefault() is ({ } unit, var came)
            ? trust.Find(unit, came, TrustedList.QualifiedTimestamps, stamp.Time) : null;
        // only the time of a service this computer trusts counts: anyone can run a service of their own and stamp any time (Fehlergedächtnis 326)
        var stampTrusted = stamp is { Valid: true } && (stamp.Trusted || timestampService is { Qualified: true });
        // the time a third party proves: the signature's own trusted stamp, else a later document time stamp that covers it — the earlier of
        // the two (EN 319 102-1, 5.6.2.3; docs/FEHLERGEDAECHTNIS.md, No. 712)
        DateTimeOffset? provenTime = stampTrusted && (proven is null || stamp!.Time <= proven) ? stamp!.Time : proven;
        var stamped = provenTime is { } provenAt && provenAt.LocalDateTime >= certificate.NotBefore && provenAt.LocalDateTime <= certificate.NotAfter;
        if (stamped && (stamp is { ThroughProof: true } || provenTime == proven && !stampTrusted))
            warnings.Add(Tr.T("Ein späterer Dokument-Zeitstempel belegt, dass die Signatur am {0} schon bestand; geprüft zu diesem Zeitpunkt.",
                proven!.Value.LocalDateTime.ToString("g", Tr.Culture)));
        // a trusted time stamp before the certificate began proves it was signed before the certificate was valid (EN 319 102-1, Tab. 6:
        // NOT_YET_VALID); the signature existed at the stamp's time at the latest, so its accuracy only moves the line
        if (stampTrusted && stamp!.Time + stamp.Accuracy < new DateTimeOffset(certificate.NotBefore))
            return result with
            {
                Problem = Tr.T("Ein vertrauenswürdiger Zeitstempel belegt die Signatur vor Beginn der Gültigkeit des Zertifikats."),
                Warnings = warnings, Cause = SubIndication.NotYetValid,
            };
        if (stamp is { Valid: false }) warnings.Add(Tr.T("Der Zeitstempel passt nicht zu dieser Signatur."));
        else if (stamp is { Valid: true } && !stampTrusted) warnings.Add(Tr.T("Den Zeitstempeldienst kennt dieser Rechner nicht als vertrauenswürdig."));
        // Identity: a certificate meant for signing, leading to a trusted root today — the stated time is the signer's own claim, so an
        // expired certificate with a time set back does not count (without a time stamp nothing confirms when it was signed)
        var suitable = SuitableForSigning(certificate, out var unsuitable);
        if (unsuitable is not null) warnings.Add(unsuitable);
        var now = DateTimeOffset.Now;
        if (certificate.NotAfter < now.LocalDateTime && !stamped)
            warnings.Add(stamp is { Valid: true } && !stampTrusted
                ? Tr.T("Das Zertifikat ist abgelaufen; der Zeitstempel stammt von einem Dienst, dem dieser Rechner nicht vertraut, und bestätigt deshalb nichts.")
                : Tr.T("Das Zertifikat ist abgelaufen; ohne Zeitstempel bestätigt nichts, dass vorher unterschrieben wurde."));
        if (time is { } stated)
        {
            if (stated > now.AddDays(1)) warnings.Add(Tr.T("Die angegebene Unterschriftszeit liegt in der Zukunft."));
            // the stamp is taken after signing: a stated time after it is the signer's clock or a wrong claim — said, nothing more
            if (stamp is { Valid: true } && stated > stamp.Time + stamp.Accuracy + StatedTimeSlack)
                warnings.Add(Tr.T("Die angegebene Unterschriftszeit liegt nach der Zeit des Zeitstempels."));
            else if (stated.LocalDateTime < certificate.NotBefore || stated.LocalDateTime > certificate.NotAfter)
                warnings.Add(Tr.T("Zur angegebenen Unterschriftszeit war das Zertifikat nicht gültig."));
        }
        // the validation data of the file's security store and what the block carries itself (CRLs and OCSP responses in SignedData and
        // in the signed attribute adbe-revocationInfoArchival, docs/FEHLERGEDAECHTNIS.md, No. 481)
        var (blockOcsps, blockCrls) = CmsSignature.RevocationValues(der);
        var validationData = store.Value;
        if (blockOcsps.Count + blockCrls.Count > 0)
            validationData = new RevocationInfo.Found([.. validationData?.Certificates ?? []], [.. validationData?.Ocsps ?? [], .. blockOcsps],
                [.. validationData?.Crls ?? [], .. blockCrls], []);
        var revocation = validationData is { } data ? LongTerm(cms, data, time ?? now) : null;
        var longTerm = revocation?.Coverage;
        // A revoked certificate (EN 319 102-1, 5.2.6.4, 5.3.4, 5.5.4): revoked after a time a trusted time stamp proves — valid, said so;
        // revoked before it — invalid; nothing proves when it was signed — not confirmed; an issuer revoked or a certificate on hold — not
        // confirmed. Never „valid“ with an answer „revoked“ and no earlier proof (docs/ABLAUFPLAN.md, 2.5, I-1)
        string? unconfirmed = null;
        string? unconfirmedCause = null;
        if (revocation is { Coverage: RevocationInfo.Coverage.Revoked } revoked)
        {
            var verdict = RevokedVerdict(revoked, stamped ? provenTime : null);
            if (verdict.Problem is { } problem)
                return result with { Problem = problem, Warnings = warnings, LongTerm = longTerm, Revocation = revocation, Cause = SubIndication.Revoked };
            unconfirmed = verdict.Unconfirmed;
            if (unconfirmed is not null)
                unconfirmedCause = revoked.OnHold ? SubIndication.TryLater : revoked.EndRevoked ? SubIndication.RevokedNoPoe : SubIndication.RevokedCaNoPoe;
            if (verdict.Hint is { } hint) warnings.Add(hint);
        }
        var revokedOrHeld = unconfirmed is not null;   // then nothing confirms the identity either
        // what the lists say: at the issuance of the certificate and at the time of signing a trusted third party confirms (else now — the
        // time the signer states proves nothing; EN 319 102-1, 5.2.4; docs/FEHLERGEDAECHTNIS.md, No. 484)
        TrustListMatch? issuer = null;
        ListVerdict? listed = null;
        if (trust is not null)
        {
            listed = trust.Qualify(certificate, cms.Certificates, CertificateProfiles.Of(certificate), provenTime ?? now);
            issuer = listed?.Service ?? trust.Find(certificate, cms.Certificates, TrustedList.QualifiedCa, provenTime ?? now);
            if (SignatureLevels.Of(certificate) == SignatureLevel.Qualified && listed is not { Qualified: true })
                warnings.Add(Tr.T("Das Zertifikat nennt sich qualifiziert, die EU-Vertrauensliste führt den Aussteller zur Unterschriftszeit aber nicht als qualifizierten Anbieter."));
            // a list past its next update: the check may miss a later withdrawal (docs/ABLAUFPLAN.md, 2.6, step 5)
            var list = trust.Lists.FirstOrDefault(l => l.Territory == issuer?.Service.Territory) ?? trust.Lotl;
            if (list.NextUpdate is { } due && due < now)
                warnings.Add(Tr.T("Die geladene EU-Vertrauensliste ({0}, Nr. {1}) ist veraltet: die nächste Ausgabe war am {2} fällig. Laden Sie sie neu.",
                    list.Territory, list.Sequence, due.LocalDateTime.ToString("d", Tr.Culture)));
        }
        // Identity: at the time a trusted time stamp confirms (the certificate may have expired since — that is what the stamp is for),
        // otherwise today; anchored in a root of the system or in the EU trusted lists (a qualified authority granted at that time)
        DateTimeOffset? confirmedAt = stamped ? provenTime : null;
        var path = CertificatePath.Check(certificate, cms.Certificates, roots.Value, confirmedAt ?? now);
        var anchored = path.Valid || trust is not null && trust.ConfirmsIdentity(certificate, cms.Certificates, confirmedAt ?? now);
        // a path to a trusted root that breaks a rule of RFC 5280: which certificate breaks which rule (docs/FEHLERGEDAECHTNIS.md, No. 711)
        var chainProblem = anchored ? null : path.Reason;
        if (chainProblem is not null) warnings.Add(chainProblem);
        // the order of the reasons (docs/ABLAUFPLAN.md, 2.5, step 5): revoked > unsafe method > identity not confirmed > revocation status
        // not checked; the status only matters once the identity is confirmed — of a certificate nobody vouches for it says nothing
        if (unconfirmed is null && weak is not null) (unconfirmed, unconfirmedCause) = (weak, SubIndication.CryptoConstraintsFailureNoPoe);
        if (unconfirmed is null && suitable && anchored && bound && longTerm != RevocationInfo.Coverage.Complete)
        {
            if (NoRevocationCheckNeeded(certificate)) warnings.Add(Tr.T("Sperrprüfung nicht nötig (laut Zertifikat)."));
            else
            {
                unconfirmed = longTerm == RevocationInfo.Coverage.Incomplete
                    ? Tr.T("Sperrstatus nicht geprüft: nicht für jedes Zertifikat ein Sperrstatus in der Datei.")
                    : Tr.T("Sperrstatus nicht geprüft (keine Prüfdaten in der Datei).");
                unconfirmedCause = SubIndication.TryLater;
            }
        }
        // why the identity is not confirmed, in the terms of EN 319 102-1 (5.2.6, 5.2.7; Tab. 6): first the chain to an anchor — none at all
        // is NO_CERTIFICATE_CHAIN_FOUND; one that breaks a rule of the path, CERTIFICATE_CHAIN_GENERAL_FAILURE (5.2.6.4, step 4 e); one that
        // holds but for the time — a certificate of it expired, nothing proves signing before — OUT_OF_BOUNDS_NO_POE; then the constraints on
        // the chain and on the signature
        var identityCause = !anchored
                ? path.Verdict switch
                {
                    CertificatePath.Verdict.Violated => SubIndication.CertificateChainGeneralFailure,
                    CertificatePath.Verdict.OutOfBounds => SubIndication.OutOfBoundsNoPoe,
                    _ => SubIndication.NoCertificateChainFound,
                }
            : !suitable ? SubIndication.ChainConstraintsFailure
            : !bound ? SubIndication.SigConstraintsFailure
            : null;
        result = result with
        {
            // the identity rests on the anchor; a revocation status not checked or an unsafe method leave it, a revocation does not
            IdentityConfirmed = suitable && anchored && bound && !revokedOrHeld, ChainProblem = chainProblem, Chain = path.Links,
            Warnings = warnings, LongTerm = longTerm, UnconfirmedReason = unconfirmed, Revocation = revocation, Unproven = weak is not null,
            Cause = unconfirmedCause ?? identityCause,
            TrustListsLoaded = trust is not null, Issuer = issuer, TimestampService = timestampService, Listed = listed,
        };

        return Finish(doc, result, range, end, wholeFile, password, revisions, restriction);
    }

    /// <summary>
    /// The rules of PAdES for the block and the signature dictionary that change nothing of the result, said as hints (docs/ABLAUFPLAN.md, 2.2,
    /// A-12): no /Cert (ISO 32000-2:2020, 12.8.3.4.2); the time in /M or as signing-time, not both; the content of type id-data (12.8.3.4.3 a);
    /// no counter-signature, content-reference, content-identifier, content-hints (12.8.3.4.3 i); content and attributes digested with the
    /// same hash (ISO/TS 32002, 5.1.4). PDF Knead writes none of them.
    /// </summary>
    private static List<string> PadesHints(SignerInfo signer, PdfDictionary sig)
    {
        var hints = new List<string>();
        if (sig.ContainsKey(PdfName.Get("Cert"))) hints.Add(Tr.T("Das Signaturwörterbuch trägt ein Zertifikat (/Cert); bei PAdES ist das nicht vorgesehen."));
        var signed = signer.SignedAttributes.Cast<CryptographicAttributeObject>().Select(a => a.Oid?.Value).ToHashSet(StringComparer.Ordinal);
        if (sig.ContainsKey(PdfName.Get("M")) && signed.Contains("1.2.840.113549.1.9.5"))
            hints.Add(Tr.T("Die Unterschriftszeit steht in /M und im Signaturblock; bei PAdES ist nur eine der beiden vorgesehen."));
        foreach (var attribute in signer.SignedAttributes)
            if (attribute.Oid?.Value == "1.2.840.113549.1.9.3" && attribute.Values.Count == 1 && ContentTypeOf(attribute.Values[0].RawData) is { } type
                && type != "1.2.840.113549.1.7.1")
                hints.Add(Tr.T("Die Art des unterschriebenen Inhalts ist nicht „Daten“ (id-data); bei PAdES ist das vorgesehen."));
        var forbidden = signed.Where(o => o is "1.2.840.113549.1.9.16.2.10" or "1.2.840.113549.1.9.16.2.7" or "1.2.840.113549.1.9.16.2.4")
            .Concat(signer.UnsignedAttributes.Cast<CryptographicAttributeObject>().Select(a => a.Oid?.Value).Where(o => o == "1.2.840.113549.1.9.6"));
        if (forbidden.Any()) hints.Add(Tr.T("Der Signaturblock trägt ein Attribut, das PAdES ausschließt (Gegensignatur oder Inhaltsverweis)."));
        string? attributesHash = signer.SignatureAlgorithm.Value switch
        {
            "1.2.840.113549.1.1.5" or "1.2.840.10045.4.1" or "1.2.840.10040.4.3" => "1.3.14.3.2.26",
            "1.2.840.113549.1.1.11" or "1.2.840.10045.4.3.2" or "2.16.840.1.101.3.4.3.2" => "2.16.840.1.101.3.4.2.1",
            "1.2.840.113549.1.1.12" or "1.2.840.10045.4.3.3" => "2.16.840.1.101.3.4.2.2",
            "1.2.840.113549.1.1.13" or "1.2.840.10045.4.3.4" => "2.16.840.1.101.3.4.2.3",
            _ => null,   // rsaEncryption, ecPublicKey …: the digest algorithm serves both
        };
        if (attributesHash is not null && attributesHash != signer.DigestAlgorithm.Value)
            hints.Add(Tr.T("Inhalt und signierte Attribute sind mit verschiedenen Prüfsummenverfahren gesichert; vorgesehen ist dasselbe."));
        return hints;
    }

    private static string? ContentTypeOf(byte[] value)
    {
        try
        {
            return new System.Formats.Asn1.AsnReader(value, System.Formats.Asn1.AsnEncodingRules.BER).ReadObjectIdentifier();
        }
        catch (System.Formats.Asn1.AsnContentException)
        {
            return null;
        }
    }

    /// <summary>
    /// A document time stamp (ISO 32000-2:2020, 12.8.5): the token (RFC 3161) must be signed by the service and name the digest of exactly the
    /// signed byte ranges; whether the service is trusted (a root of the system for time-stamping, or a qualified service of the EU trusted
    /// lists), and what the file's validation data say about its chain.
    /// </summary>
    private static SignatureCheck DocumentTimestamp(PdfDocument doc, long[] range, byte[] der, SignatureCheck result, Lazy<X509Certificate2Collection> roots,
        Lazy<RevocationInfo.Found?> store, Lazy<TrustListSet?> lists, DateTimeOffset? proven)
    {
        result = result with { IsDocumentTimestamp = true };
        if (!Rfc3161TimestampToken.TryDecode(der, out var token, out _))
            return result with { Problem = Tr.T("Der Dokument-Zeitstempel ist beschädigt.") };
        if (StampHash(token) is not { } name)
            return result with { Outcome = SignatureOutcome.NotChecked, Problem = Tr.T("Das Prüfsummenverfahren des Dokument-Zeitstempels wird nicht geprüft.") };
        var digest = Digest(doc, range, name);
        var tokenCms = token.AsSignedCms();
        if (!token.VerifySignatureForHash(digest, name, out var authority, tokenCms.Certificates) || authority is null)
            return result with { Problem = Tr.T("Der Dokument-Zeitstempel passt nicht zum Inhalt: das Dokument wurde verändert."), Cause = SubIndication.HashFailure };
        var warnings = new List<string>();
        if (name == HashAlgorithmName.SHA1) warnings.Add(Tr.T("Der Dokument-Zeitstempel verwendet SHA-1; das gilt heute als veraltet."));
        var time = token.TokenInfo.Timestamp;
        var serviceName = authority.GetNameInfo(X509NameType.SimpleName, false) is { Length: > 0 } simple ? simple : authority.Subject;
        var trust = lists.Value;
        var qualified = trust?.Find(authority, tokenCms.Certificates, TrustedList.QualifiedTimestamps, time);
        // the service today, or at the time a newer document time stamp proves this one existed (docs/FEHLERGEDAECHTNIS.md, No. 712)
        var trustedNow = roots.Value.Count > 0 && SignatureTimestamps.Trusted(authority, tokenCms.Certificates, roots.Value, DateTimeOffset.Now);
        var trustedThen = !trustedNow && proven is { } at && roots.Value.Count > 0 && SignatureTimestamps.Trusted(authority, tokenCms.Certificates, roots.Value, at);
        var trusted = trustedNow || trustedThen || qualified is { Qualified: true };
        if (!trusted) warnings.Add(Tr.T("Den Zeitstempeldienst kennt dieser Rechner nicht als vertrauenswürdig."));
        else if (trustedThen && qualified is not { Qualified: true })
            warnings.Add(Tr.T("Ein späterer Dokument-Zeitstempel belegt, dass dieser Zeitstempel am {0} schon bestand; geprüft zu diesem Zeitpunkt.",
                proven!.Value.LocalDateTime.ToString("g", Tr.Culture)));
        RevocationInfo.Coverage? longTerm = null;
        if (store.Value is { } data)
        {
            var extra = new X509Certificate2Collection(tokenCms.Certificates);
            foreach (var raw in data.Certificates)
                try
                {
                    extra.Add(X509CertificateLoader.LoadCertificate(raw));
                }
                catch (CryptographicException)
                {
                    // an unreadable entry of the store
                }
            var revocation = RevocationInfo.Embedded(authority, extra, data.Ocsps, data.Crls, time);
            longTerm = revocation.Coverage;
            if (revocation.Coverage == RevocationInfo.Coverage.Revoked && (revocation.RevokedAt is not { } revokedAt || revokedAt <= time))
            {
                trusted = false;
                warnings.Add(Tr.T("Das Zertifikat des Zeitstempeldiensts ist laut den eingebetteten Prüfdaten gesperrt; der Zeitstempel zählt nicht als vertrauenswürdig."));
            }
        }
        return result with
        {
            SignerName = serviceName,
            SigningTime = time,
            Certificate = Summary(authority),
            Timestamp = new SignatureTimestamp(time, serviceName, true, trusted, SignatureTimestamp.AccuracyOf(token.TokenInfo)),
            IdentityConfirmed = trusted,
            // renew before the service's certificate ends or a method of the token stops protecting (TR-ESOR; ETSI TS 119 312)
            RenewBefore = AlgorithmSuitability.Earliest(new DateTimeOffset(authority.NotAfter), AlgorithmSuitability.Hash(token.TokenInfo.HashAlgorithmId.Value),
                AlgorithmSuitability.Hash(tokenCms.SignerInfos[0].DigestAlgorithm.Value), AlgorithmSuitability.Key(authority)),
            TrustListsLoaded = trust is not null,
            TimestampService = qualified,
            LongTerm = longTerm,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// The last step of every check: what later revisions changed (ISO 32000-2:2020, 12.8.2.2), measured against what the certification and
    /// the field lock allow, then the outcome.
    /// </summary>
    private static SignatureCheck Finish(PdfDocument doc, SignatureCheck result, long[] range, long end, bool wholeFile, string? password,
        Dictionary<long, PdfDocument?> revisions, CertificationLevel? certification)
    {
        if (!wholeFile)
        {
            if (Revision(doc, end, password, revisions) is not { } earlier)
                return result with { Outcome = SignatureOutcome.ChangedAfterSigning, Later = LaterChanges.Other, LaterDetails = [Tr.T("Die unterschriebene Fassung lässt sich nicht lesen.")] };
            // the signature must already be part of the revision it signs (and not be put in afterwards)
            if (!DigitalSignatures.Find(earlier).Any(e => e.ByteRange.AsSpan().SequenceEqual(range)))
                return result with { Problem = Tr.T("Die Signatur stammt nicht aus der Fassung, die sie unterschreibt.") };
            var report = RevisionChanges.Classify(earlier, doc);
            var details = report.Details.ToList();
            var violated = report.Changes == LaterChanges.Other;
            // What the certification and this signature's field lock forbid (ISO 32000-2:2020, 12.8.2.2 and 12.8.2.4); validation data and
            // document time stamps are allowed at every level, also „no changes“ (12.8.2.2.1, Table 257; 12.8.5.3). The /P of the signed
            // field's /Lock acts like a certification of this signature (Table 236, erratum EC3 #131); the stricter of both counts
            var lockLevel = result.FieldLockOfField?.Level;
            var level = certification is { } c && lockLevel is { } l ? (CertificationLevel)Math.Min((int)c, (int)l) : certification ?? lockLevel;
            if (!violated && !RevisionChanges.Permitted(level, report.Changes))
            {
                violated = true;
                var byField = lockLevel is not null && (certification is null || lockLevel < certification);
                details.Insert(0, (level, byField) switch
                {
                    (CertificationLevel.NoChanges, false) => Tr.T("Das Dokument ist zertifiziert: danach sind keine Änderungen erlaubt."),
                    (CertificationLevel.NoChanges, true) => Tr.T("Das Signaturfeld sperrt das Dokument: danach sind keine Änderungen erlaubt."),
                    (_, true) => Tr.T("Das Signaturfeld erlaubt danach nur Ausfüllen und Unterschreiben, keine Kommentare und keine leeren Signaturfelder."),
                    _ => Tr.T("Das Dokument ist zertifiziert: danach sind nur Ausfüllen und Unterschreiben erlaubt, keine Kommentare und keine leeren Signaturfelder."),
                });
                // what the certification forbids, named where the classification knows it (the document information, decision C)
                foreach (var (named, text) in report.Named)
                    if (!RevisionChanges.Permitted(level, named) && !details.Contains(text)) details.Add(text);
            }
            // the fields the signature locks (FieldMDP) and those its field locks (/Lock, also when not carried over)
            var lockedNames = report.ChangedFields.Where(n => result.Lock?.Locks(n) == true || result.FieldLockOfField?.Locks(n) == true);
            foreach (var name in lockedNames.Order(StringComparer.Ordinal).Take(6))
            {
                violated = true;
                details.Add(Tr.T("Das Feld „{0}“ ist durch diese Signatur gesperrt und wurde danach geändert.", name));
            }
            result = result with { Later = violated ? LaterChanges.Other : report.Changes, LaterDetails = details };
            if (violated) return result with { Outcome = SignatureOutcome.ChangedAfterSigning };
        }
        return result with
        {
            Outcome = result.UnconfirmedReason is not null ? SignatureOutcome.Unconfirmed
                : result.IdentityConfirmed ? SignatureOutcome.Valid : SignatureOutcome.ValidIdentityUnknown,
        };
    }

    /// <summary>The result before anything is checked: what the signature dictionary states, outcome invalid.</summary>
    private static SignatureCheck Start(PdfDocument doc, DigitalSignatureEntry entry, CertificationLevel? certification, bool isFirst)
    {
        var sig = entry.Signature!;
        return new SignatureCheck(entry.FullName, SignatureOutcome.Invalid, null, LaterChanges.None, [], sig.GetString(PdfName.Name)?.ToText(),
            PdfDate.Parse(sig.GetString(Modified)?.ToText()), Text(sig, Reason), Text(sig, Location), sig.GetName(SubFilter)?.Value ?? "", null, false, false,
            PageOf(doc, entry.Field!), isFirst ? certification : null, FieldLock.Of(sig))
        {
            // known from the dictionary already, so even a broken one is shown as what it is
            IsDocumentTimestamp = sig.GetName(SubFilter)?.Value == "ETSI.RFC3161" || sig.GetName(PdfName.Type)?.Value == "DocTimeStamp",
            ByteRange = entry.ByteRange,
            FieldLockOfField = FieldLock.OfField(entry.Field),
        };
    }

    /// <summary>The bytes before <paramref name="end"/> close a revision: „%%EOF“, then at most a line end.</summary>
    private static bool EndsRevision(PdfDocument doc, long end)
    {
        if (end < 5 || end > doc.FileSize) return false;
        var tail = doc.FileBytes(Math.Max(0, end - 32), Math.Min(32, end));
        var length = tail.Length;
        while (length > 0 && tail[length - 1] is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t' or 0) length--;
        return tail[..length].EndsWith("%%EOF"u8);
    }

    private static bool IsBlank(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
            if (b is not ((byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t' or (byte)'\f' or 0)) return false;
        return true;
    }

    /// <summary>Decodes the hexadecimal digits between „&lt;“ and „&gt;“ (white space allowed, an odd last digit counts as followed by 0).</summary>
    private static byte[]? Hex(ReadOnlySpan<byte> text)
    {
        var bytes = new byte[(text.Length + 1) / 2];
        var (count, high, pending) = (0, 0, false);
        foreach (var c in text)
        {
            int digit = c switch
            {
                >= (byte)'0' and <= (byte)'9' => c - '0',
                >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
                (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or (byte)'\f' or 0 => -1,
                _ => -2,
            };
            if (digit == -2) return null;
            if (digit < 0) continue;
            if (pending) bytes[count++] = (byte)(high << 4 | digit);
            else high = digit;
            pending = !pending;
        }
        if (pending) bytes[count++] = (byte)(high << 4);
        return bytes.AsSpan(0, count).ToArray();
    }

    /// <summary>The digest of the signed bytes, streamed over the two byte ranges.</summary>
    private static byte[] Digest(PdfDocument doc, long[] range, HashAlgorithmName algorithm)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);
        foreach (var (start, length) in new[] { (range[0], range[1]), (range[2], range[3]) })
            for (long at = 0; at < length; at += 1 << 20)
                hash.AppendData(doc.FileBytes(start + at, Math.Min(1 << 20, length - at)));
        return hash.GetHashAndReset();
    }

    /// <summary>The signed bytes as one array, for methods <see cref="CmsSignature.CheckDigest"/> does not handle.</summary>
    private static byte[] Content(PdfDocument doc, long[] range)
    {
        var content = new byte[range[1] + range[3]];
        doc.FileBytes(0, range[1]).CopyTo(content);
        doc.FileBytes(range[2], range[3]).CopyTo(content.AsSpan((int)range[1]));
        return content;
    }

    private static PdfDocument? Revision(PdfDocument doc, long end, string? password, Dictionary<long, PdfDocument?> revisions)
    {
        if (revisions.TryGetValue(end, out var known)) return known;
        PdfDocument? revision;
        try
        {
            revision = doc.OpenRevision(end, password);
            if (revision.WasRepaired)
            {
                revision.Dispose();
                revision = null;
            }
        }
        catch (Exception e) when (e is PdfException or InvalidDataException or IOException)
        {
            revision = null;
        }
        revisions[end] = revision;
        return revision;
    }

    /// <summary>What the file's validation data says about the chains of a signature (signer and time-stamping authority).</summary>
    /// <summary>
    /// Whether the platform reads the block at all: a block it cannot read is broken, not a signature over other bytes (its check would say
    /// „changed“ otherwise).
    /// </summary>
    private static bool Decodable(byte[] der)
    {
        try
        {
            new SignedCms().Decode(der);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>How far a stated signing time may lie after the time stamp before it is mentioned (the signer's clock may run ahead).</summary>
    private static readonly TimeSpan StatedTimeSlack = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The time stamps must keep their order (EN 319 102-1, 5.5.4, step 4e: TIMESTAMP_ORDER_FAILURE): a document time stamp covers every
    /// signature and stamp before it in the file, so its time cannot lie before theirs (within the accuracy of both, at least a second).
    /// A signature or stamp contradicted that way is not confirmed — the file says two incompatible things about time. Only stamps whose
    /// token checks out count.
    /// </summary>
    private static void TimestampOrder(IReadOnlyList<DigitalSignatureEntry> entries, List<SignatureCheck> results)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (results[i].Timestamp is not { Valid: true } own) continue;
            var end = entries[i].ByteRange[2] + entries[i].ByteRange[3];
            for (var j = 0; j < entries.Count; j++)
            {
                if (!results[j].IsDocumentTimestamp || results[j].Timestamp is not { Valid: true } later) continue;
                if (entries[j].ByteRange[2] + entries[j].ByteRange[3] <= end) continue;
                var tolerance = own.Accuracy + later.Accuracy;
                if (tolerance < TimeSpan.FromSeconds(1)) tolerance = TimeSpan.FromSeconds(1);
                if (later.Time + tolerance >= own.Time) continue;
                var reason = Tr.T("Die Zeitstempel widersprechen sich: der spätere Dokument-Zeitstempel „{0}“ trägt eine frühere Zeit.", results[j].FieldName);
                var check = results[i];
                results[i] = check.Outcome is SignatureOutcome.Valid or SignatureOutcome.ValidIdentityUnknown
                    ? check with { Outcome = SignatureOutcome.Unconfirmed, UnconfirmedReason = reason, Cause = SubIndication.TimestampOrderFailure }
                    : check with { Warnings = [.. check.Warnings, reason] };
                break;
            }
        }
    }

    /// <summary>
    /// A block without the signer's certificate: the signature value cannot be checked — not confirmed (EN 319 102-1:
    /// NO_SIGNING_CERTIFICATE_FOUND), and nothing proves the integrity.
    /// </summary>
    private static SignatureCheck NoSignerCertificate(SignatureCheck result, string? reason = null) => result with
    {
        Outcome = SignatureOutcome.Unconfirmed, Unproven = true, Cause = SubIndication.NoSigningCertificateFound,
        UnconfirmedReason = reason ?? Tr.T("Der Signaturblock enthält kein Zertifikat des Unterzeichners."),
    };

    /// <summary>
    /// Whether the certificate says a revocation check is not needed: a short-term certificate (EN 319 412-1, 5.2: extension
    /// 0.4.0.194121.2.1) or one with noRevAvail (RFC 9608: 2.5.29.56).
    /// </summary>
    internal static bool NoRevocationCheckNeeded(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509Extension>().Any(e => e.Oid?.Value is "0.4.0.194121.2.1" or "2.5.29.56");

    /// <summary>
    /// What a revoked certificate of the signature means (EN 319 102-1, 5.2.6.4, 5.3.4, 5.5.4; docs/ABLAUFPLAN.md, 2.5, I-1), given the time a
    /// trusted time stamp proves (null: none): the signer's own certificate revoked after it — a hint, still valid; at or before it — a
    /// problem (invalid, REVOKED); without proof — not confirmed (REVOKED_NO_POE); an issuer revoked (REVOKED_CA_NO_POE) or a certificate on
    /// hold (TRY_LATER) — not confirmed.
    /// </summary>
    internal static (string? Problem, string? Unconfirmed, string? Hint) RevokedVerdict(RevocationInfo.EmbeddedStatus revoked, DateTimeOffset? proven)
    {
        var since = revoked.RevokedAt is { } at ? at.LocalDateTime.ToString("g", Tr.Culture) : "?";
        var why = SignatureTexts.RevocationReason(revoked.Reason);
        if (revoked.OnHold)
            return (null, Tr.T("Ein Zertifikat der Signatur ist laut den eingebetteten Prüfdaten vorübergehend gesperrt (seit {0}).", since), null);
        if (!revoked.EndRevoked)
            return (null, Tr.T("Ein Aussteller in der Kette der Signatur oder ihres Zeitstempels ist laut den eingebetteten Prüfdaten gesperrt (seit {0}, Grund: {1}).", since, why), null);
        if (proven is { } time && revoked.RevokedAt is { } when && time < when)
            return (null, null, Tr.T("Das Zertifikat wurde am {0} gesperrt; die Unterschrift ist durch einen Zeitstempel früher belegt.", since));
        if (proven is not null)
            return (Tr.T("Das Zertifikat war bei der belegten Unterschrift schon gesperrt (seit {0}, Grund: {1}).", since, why), null, null);
        return (null, Tr.T("Das Zertifikat ist seit {0} gesperrt (Grund: {1}); nichts belegt, dass vorher unterschrieben wurde.", since, why), null);
    }

    /// <summary>
    /// The validation data against the chains of a signature: the signer's first (whose own certificate counts as <c>EndRevoked</c>), then
    /// that of its time-stamping service; a revoked one is returned at once, else complete only if all are.
    /// </summary>
    private static RevocationInfo.EmbeddedStatus LongTerm(SignedCms cms, RevocationInfo.Found data, DateTimeOffset at)
    {
        var extra = new X509Certificate2Collection();
        try
        {
            foreach (var raw in data.Certificates)
            {
                try
                {
                    extra.Add(X509CertificateLoader.LoadCertificate(raw));
                }
                catch (CryptographicException)
                {
                    // an unreadable entry of the store
                }
            }
            var coverage = RevocationInfo.Coverage.Complete;
            var first = true;
            IReadOnlyList<RevocationInfo.LinkStatus> signerLinks = [];
            foreach (var (end, came) in LongTermValidation.ChainsOf(cms.Encode()))
            {
                var all = new X509Certificate2Collection(extra);
                all.AddRange(came);
                var one = RevocationInfo.Embedded(end, all, data.Ocsps, data.Crls, at);
                if (one.Coverage == RevocationInfo.Coverage.Revoked) return first ? one : one with { EndRevoked = false };
                if (one.Coverage == RevocationInfo.Coverage.Incomplete) coverage = one.Coverage;
                if (first) signerLinks = one.Links;
                first = false;
            }
            return new RevocationInfo.EmbeddedStatus(coverage) { Links = signerLinks };
        }
        finally
        {
            foreach (var certificate in extra) certificate.Dispose();
        }
    }

    private static readonly HashSet<string> BrokenDigests = ["1.2.840.113549.2.5", "1.2.840.113549.2.2"];   // MD5, MD2
    private static readonly HashSet<string> BrokenSignatures = ["1.2.840.113549.1.1.4", "1.2.840.113549.1.1.2"];   // MD5, MD2 with RSA
    private static readonly HashSet<string> OldDigests = ["1.3.14.3.2.26"];   // SHA-1
    private static readonly HashSet<string> OldSignatures = ["1.2.840.113549.1.1.5", "1.2.840.10045.4.1", "1.2.840.10040.4.3"];   // SHA-1 with RSA, ECDSA, DSA

    /// <summary>
    /// Methods that no longer protect a signature: MD5/MD2 and RSA keys below 1024 bits or elliptic curves below 256 bits make it invalid
    /// (the problem is returned); SHA-1 and RSA keys below 2048 bits are reported as outdated (added to <paramref name="warnings"/>), so that
    /// older signatures made with what was usual then still count.
    /// </summary>
    private static string? Weakness(SignerInfo signer, X509Certificate2 certificate, List<string> warnings)
    {
        var digest = signer.DigestAlgorithm.Value ?? "";
        var algorithm = signer.SignatureAlgorithm.Value ?? "";
        if (BrokenDigests.Contains(digest) || BrokenSignatures.Contains(algorithm))
            return Tr.T("Die Signatur verwendet ein unsicheres Verfahren (MD5); sie schützt das Dokument nicht.");
        if (OldDigests.Contains(digest) || OldSignatures.Contains(algorithm))
            warnings.Add(Tr.T("Die Signatur verwendet SHA-1; das gilt heute als veraltet."));
        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is not null)
        {
            if (rsa.KeySize < 1024) return Tr.T("Der Schlüssel des Zertifikats ist zu kurz ({0} Bit); die Signatur schützt das Dokument nicht.", rsa.KeySize);
            if (rsa.KeySize < 2048) warnings.Add(Tr.T("Der Schlüssel des Zertifikats ist nach heutigem Stand zu kurz ({0} Bit).", rsa.KeySize));
        }
        using var ec = certificate.GetECDsaPublicKey();
        if (ec is not null && ec.KeySize < 256)
            return Tr.T("Der Schlüssel des Zertifikats ist zu kurz ({0} Bit); die Signatur schützt das Dokument nicht.", ec.KeySize);
        return null;
    }

    /// <summary>The digest the signer's signed attributes name for the signed bytes (message-digest), as „SHA-256: 0A1B…“; null if none.</summary>
    private static string? MessageDigest(SignerInfo signer)
    {
        foreach (var attribute in signer.SignedAttributes)
            if (attribute.Oid.Value == "1.2.840.113549.1.9.4" && attribute.Values.Count == 1)
            {
                var digest = new Pkcs9MessageDigest();
                digest.CopyFrom(attribute.Values[0]);
                var method = signer.DigestAlgorithm.FriendlyName is { Length: > 0 } name ? name.ToUpperInvariant().Replace("SHA", "SHA-", StringComparison.Ordinal)
                    : signer.DigestAlgorithm.Value;
                return $"{method}: {Convert.ToHexString(digest.MessageDigest)}";
            }
        return null;
    }

    /// <summary>
    /// The signature policy (signature-policy-identifier: its OID, „implied“ for signaturePolicyImplied) and the purpose (commitment-type-indication)
    /// among the signed attributes (ETSI EN 319 122-1, 5.2.8 and 5.2.3); null where absent or unreadable.
    /// </summary>
    private static (string? Policy, string? Commitment) PolicyAndCommitment(SignerInfo signer)
    {
        string? policy = null, commitment = null;
        foreach (var attribute in signer.SignedAttributes)
        {
            if (attribute.Values.Count != 1) continue;
            try
            {
                var reader = new System.Formats.Asn1.AsnReader(attribute.Values[0].RawData, System.Formats.Asn1.AsnEncodingRules.BER);
                switch (attribute.Oid.Value)
                {
                    case "1.2.840.113549.1.9.16.2.15":
                        policy = reader.PeekTag().HasSameClassAndValue(System.Formats.Asn1.Asn1Tag.Null) ? Tr.T("„implizit“")
                            : reader.ReadSequence().ReadObjectIdentifier();
                        break;
                    case "1.2.840.113549.1.9.16.2.16":
                        commitment = reader.ReadSequence().ReadObjectIdentifier() switch
                        {
                            "1.2.840.113549.1.9.16.6.1" => Tr.T("Herkunft"),
                            "1.2.840.113549.1.9.16.6.2" => Tr.T("Empfang"),
                            "1.2.840.113549.1.9.16.6.3" => Tr.T("Zustellung"),
                            "1.2.840.113549.1.9.16.6.4" => Tr.T("Versand"),
                            "1.2.840.113549.1.9.16.6.5" => Tr.T("Genehmigung"),
                            "1.2.840.113549.1.9.16.6.6" => Tr.T("Erstellung"),
                            var other => other,
                        };
                        break;
                }
            }
            catch (System.Formats.Asn1.AsnContentException)
            {
                // an unreadable attribute: nothing to show
            }
        }
        return (policy, commitment);
    }

    /// <summary>The signed content-type attribute, if present, names the type of the content (RFC 5652, 11.1).</summary>
    private static bool ContentTypeMatches(SignedCms cms)
    {
        foreach (var attribute in cms.SignerInfos[0].SignedAttributes)
        {
            if (attribute.Oid.Value != "1.2.840.113549.1.9.3" || attribute.Values.Count != 1) continue;
            var type = new Pkcs9ContentType();
            type.CopyFrom(attribute.Values[0]);
            return type.ContentType.Value == cms.ContentInfo.ContentType.Value;
        }
        return true;
    }

    // Purposes that cover signing documents (RFC 5280, 4.2.1.12): any, e-mail protection (the usual one for personal certificates),
    // document signing (RFC 9336), Microsoft and Adobe document signing
    private static readonly HashSet<string> SigningPurposes =
        ["2.5.29.37.0", "1.3.6.1.5.5.7.3.4", "1.3.6.1.5.5.7.3.36", "1.3.6.1.4.1.311.10.3.12", "1.2.840.113583.1.1.5"];

    /// <summary>
    /// Whether a certificate is meant for signing: not a certification authority, key usage (if stated) allowing digital signatures or
    /// non-repudiation, extended key usage (if stated) covering documents. A web server certificate of a trusted authority, which
    /// anyone can get for a domain, must not confirm an identity.
    /// </summary>
    internal static bool SuitableForSigning(X509Certificate2 certificate, out string? reason)
    {
        reason = null;
        foreach (var extension in certificate.Extensions)
        {
            switch (extension)
            {
                case X509BasicConstraintsExtension { CertificateAuthority: true }:
                    reason = Tr.T("Das Zertifikat gehört einer Zertifizierungsstelle und ist nicht zum Unterschreiben bestimmt.");
                    return false;
                case X509KeyUsageExtension usage when (usage.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0:
                    reason = Tr.T("Das Zertifikat ist nicht zum Unterschreiben bestimmt (Schlüsselverwendung).");
                    return false;
                case X509EnhancedKeyUsageExtension purposes when !purposes.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value is { } v && SigningPurposes.Contains(v)):
                    reason = Tr.T("Das Zertifikat ist für einen anderen Zweck ausgestellt (zum Beispiel für eine Webseite), nicht zum Unterschreiben.");
                    return false;
            }
        }
        return true;
    }

    /// <summary>The root certificates of the system and of the user (read only, never changed).</summary>
    private static X509Certificate2Collection SystemRoots()
    {
        var roots = new X509Certificate2Collection();
        foreach (var location in (StoreLocation[])[StoreLocation.LocalMachine, StoreLocation.CurrentUser])
        {
            try
            {
                using var store = new X509Store(StoreName.Root, location, OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                roots.AddRange(store.Certificates);
            }
            catch (CryptographicException)
            {
                // store missing (for example no user store on Linux)
            }
        }
        return roots;
    }

    private static CertificateSummary Summary(X509Certificate2 certificate) => new(
        Name(certificate.SubjectName, certificate) ?? certificate.Subject,
        Name(certificate.IssuerName, null) ?? certificate.Issuer,
        certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData),
        new DateTimeOffset(certificate.NotBefore),
        new DateTimeOffset(certificate.NotAfter),
        Platform.SigningCertificate.FingerprintOf(certificate),
        SignatureLevels.Of(certificate)) { Profile = CertificateProfiles.Of(certificate) };

    /// <summary>The common name of a distinguished name, otherwise (for the holder) the simple name .NET derives.</summary>
    private static string? Name(X500DistinguishedName name, X509Certificate2? holder)
    {
        // a part may hold several attributes (for example name and organisation together): those are left to the simple name below
        foreach (var part in name.EnumerateRelativeDistinguishedNames())
            if (!part.HasMultipleElements && part.GetSingleElementType().Value == "2.5.4.3" && part.GetSingleElementValue() is { Length: > 0 } common) return common;
        return holder?.GetNameInfo(X509NameType.SimpleName, forIssuer: false) is { Length: > 0 } simple ? simple : null;
    }

    private static DateTimeOffset? SigningTimeOf(SignerInfo signer)
    {
        foreach (var attribute in signer.SignedAttributes)
            if (attribute.Oid.Value == SigningTimeOid && attribute.Values.Count == 1)
                try
                {
                    return new Pkcs9SigningTime(attribute.Values[0].RawData).SigningTime;
                }
                catch (CryptographicException)
                {
                }
        return null;
    }

    private static string? Text(PdfDictionary sig, PdfName key) => sig.GetString(key)?.ToText() is { Length: > 0 } text ? text : null;

    /// <summary>The page of the visible field: its /P, otherwise the page whose /Annots holds it.</summary>
    private static int? PageOf(PdfDocument doc, PdfDictionary field)
    {
        var pages = doc.Pages;
        if (field.GetDictionary(Page) is { } page)
            for (var i = 0; i < pages.Count; i++)
                if (ReferenceEquals(pages[i].Dictionary, page)) return i;
        for (var i = 0; i < pages.Count; i++)
            if (pages[i].Dictionary.GetArray(Annots) is { } annots)
                for (var j = 0; j < Math.Min(annots.Count, 10_000); j++)
                    if (ReferenceEquals(annots.Get(j), field)) return i;
        return null;
    }
}

/// <summary>Texts for the results of <see cref="SignatureVerifier"/> (shown in the program and on the command line).</summary>
public static class SignatureTexts
{
    /// <summary>The overall result in a few words, for example “Gültig, Identität nicht bestätigt”.</summary>
    /// <param name="check">The result.</param>
    /// <returns>The localised text.</returns>
    public static string Outcome(SignatureCheck check) => check.Outcome switch
    {
        SignatureOutcome.Valid => Tr.T("Gültig"),
        SignatureOutcome.ValidIdentityUnknown => Tr.T("Gültig, Identität nicht bestätigt"),
        SignatureOutcome.ChangedAfterSigning => Tr.T("Nach dem Unterschreiben geändert"),
        SignatureOutcome.Invalid => Tr.T("Ungültig"),
        SignatureOutcome.Unconfirmed => Tr.T("Nicht bestätigt"),
        _ => Tr.T("Nicht geprüft"),
    };

    /// <summary>
    /// The verdict from the main indication (EN 319 102-1, 5.1.3; docs/ABLAUFPLAN.md, 9.3.5): „Gültig“ (TOTAL-PASSED), „Ungültig“ and „Nach dem
    /// Unterschreiben geändert“ (TOTAL-FAILED), „Nicht bestätigt“ (INDETERMINATE — also when only the identity is not confirmed), „Nicht
    /// geprüft“ (a method that cannot be checked).
    /// </summary>
    public static string Verdict(SignatureCheck check) => check.Outcome switch
    {
        SignatureOutcome.Valid => Tr.T("Gültig"),
        SignatureOutcome.ChangedAfterSigning => Tr.T("Nach dem Unterschreiben geändert"),
        SignatureOutcome.Invalid => Tr.T("Ungültig"),
        SignatureOutcome.NotChecked => Tr.T("Nicht geprüft"),
        _ => Tr.T("Nicht bestätigt"),
    };

    /// <summary>
    /// Why it is not „valid“, in exactly one sentence from the most important cause (error, changed, revoked, no signer's certificate, no
    /// chain, out of validity, unsafe method, revocation status not checked); for a valid signature what carries it (revocation status
    /// embedded, the time confirmed or stated).
    /// </summary>
    public static string? WhyNot(SignatureCheck check) => check.Outcome switch
    {
        SignatureOutcome.Invalid or SignatureOutcome.NotChecked => check.Problem,
        SignatureOutcome.ChangedAfterSigning => Later(check) ?? check.Problem,
        SignatureOutcome.Unconfirmed => check.UnconfirmedReason ?? check.Problem,
        SignatureOutcome.ValidIdentityUnknown => check.Cause switch
        {
            SubIndication.OutOfBoundsNoPoe => check.ChainProblem ?? Tr.T("Das Zertifikat ist abgelaufen, und nichts belegt, dass vorher unterschrieben wurde."),
            SubIndication.CertificateChainGeneralFailure => check.ChainProblem ?? Tr.T("Die Kette des Zertifikats verletzt eine Regel."),
            SubIndication.ChainConstraintsFailure => Tr.T("Das Zertifikat ist nicht zum Unterschreiben bestimmt."),
            SubIndication.SigConstraintsFailure => Tr.T("Der Signaturblock bindet das Zertifikat des Unterzeichners nicht."),
            _ => check.IsDocumentTimestamp ? Tr.T("Den Zeitstempeldienst kennt dieser Rechner nicht als vertrauenswürdig.")
                : Tr.T("Wem das Zertifikat gehört, bestätigt niemand."),
        },
        _ => Carried(check),
    };

    private static string? Carried(SignatureCheck check)
    {
        var parts = new List<string>();
        if (check.LongTerm == RevocationInfo.Coverage.Complete) parts.Add(Tr.T("Sperrstatus eingebettet"));
        if (check.Timestamp is { Valid: true, Trusted: true } stamp && !check.IsDocumentTimestamp)
            parts.Add(Tr.T("Zeit bestätigt am {0}", stamp.Time.LocalDateTime.ToString("g", Tr.Culture)));
        else if (check.SigningTime is { } time) parts.Add(Tr.T("unterschrieben am {0}", time.LocalDateTime.ToString("g", Tr.Culture)));
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    /// <summary>The kind and level for the line: „Dokument-Zeitstempel“ or the level of the signature, capitalised; null if unknown.</summary>
    public static string? Kind(SignatureCheck check) =>
        check.IsDocumentTimestamp ? Tr.T("Dokument-Zeitstempel") : Level(check) is { Length: > 0 } level ? char.ToUpper(level[0], Tr.Culture) + level[1..] : null;

    /// <summary>
    /// The result in one line (docs/ABLAUFPLAN.md, 9.3.5, I-10): „Wer — Urteil · Art/Stufe · warum nicht“, for example „Max Mustermann —
    /// Nicht bestätigt · Einfache elektronische Signatur · Wem das Zertifikat gehört, bestätigt niemand.“
    /// </summary>
    public static string OneLine(SignatureCheck check)
    {
        var who = check.IsDocumentTimestamp
            ? Tr.T("Dokument-Zeitstempel von {0}", check.SignerName is { Length: > 0 } service ? service : Tr.T("einem Zeitstempeldienst"))
            : check.SignerName is { Length: > 0 } name ? name : check.FieldName;
        return who + " — " + Summary(check);
    }

    /// <summary><see cref="OneLine"/> without who: „Urteil · Art/Stufe · warum nicht“.</summary>
    public static string Summary(SignatureCheck check) =>
        string.Join(" · ", new[] { Verdict(check), check.IsDocumentTimestamp ? null : Kind(check), WhyNot(check) }.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>What later revisions did, if anything worth saying; null if nothing changed.</summary>
    /// <param name="check">The result.</param>
    /// <returns>The localised text, or null.</returns>
    public static string? Later(SignatureCheck check) => check.Later switch
    {
        LaterChanges.ValidationData => Tr.T("Danach nur Prüfdaten für die Langzeitprüfung oder Dokument-Zeitstempel hinzugefügt."),
        LaterChanges.FormsAndSignatures => Tr.T("Danach nur Felder ausgefüllt oder weitere Signaturen hinzugefügt."),
        LaterChanges.Comments => Tr.T("Danach Kommentare hinzugefügt oder geändert; der unterschriebene Inhalt ist unverändert."),
        LaterChanges.Other => Tr.T("Danach wurde mehr geändert als Felder, Signaturen oder Kommentare."),
        _ => null,
    };

    /// <summary>The level of a signature in a few words, for example „einfache elektronische Signatur“.</summary>
    /// <param name="level">The level.</param>
    /// <returns>The localised text.</returns>
    public static string Level(SignatureLevel level) => level switch
    {
        SignatureLevel.Qualified => Tr.T("qualifizierte elektronische Signatur (laut Zertifikat)"),
        SignatureLevel.Advanced => Tr.T("fortgeschrittene elektronische Signatur (höchstens)"),
        _ => Tr.T("einfache elektronische Signatur"),
    };

    /// <summary>
    /// The level of a checked signature: qualified as confirmed by the EU trusted lists when the certificate declares itself qualified, the
    /// lists grant its authority, the identity is confirmed and the signature is valid (the conditions of a qualified electronic signature,
    /// eIDAS Art. 3 (12), checked as far as a file allows); otherwise what the certificate states (<see cref="Level(SignatureLevel)"/>).
    /// </summary>
    /// <param name="check">The result.</param>
    /// <returns>The localised text; null without a certificate.</returns>
    public static string? Level(SignatureCheck check) => SignatureNatures.Of(check) is { } nature ? Level(nature) : null;

    /// <summary>
    /// The kind and level in a few words (ETSI TS 119 172-1, A.3.2.1): „qualifizierte elektronische Signatur“, „qualifiziertes elektronisches
    /// Siegel“, „… mit qualifiziertem Zertifikat“, „fortgeschritten (höchstens)“, „einfach“; a qualified level says what it rests on — the EU
    /// trusted list or only the certificate. Never „legally valid“ (KONZEPT.md, section 5.17).
    /// </summary>
    /// <param name="nature">The nature.</param>
    /// <returns>The localised text.</returns>
    public static string Level(SignatureNature nature)
    {
        var seal = nature.Kind == SignatureKind.Seal;
        var text = nature.Kind == SignatureKind.DocumentTimestamp
            ? nature.Level == NatureLevel.Qualified ? Tr.T("qualifizierter elektronischer Zeitstempel") : Tr.T("Dokument-Zeitstempel")
            : nature.Level switch
            {
                NatureLevel.Qualified => seal ? Tr.T("qualifiziertes elektronisches Siegel") : Tr.T("qualifizierte elektronische Signatur"),
                NatureLevel.AdvancedQc => seal ? Tr.T("fortgeschrittenes elektronisches Siegel mit qualifiziertem Zertifikat")
                    : Tr.T("fortgeschrittene elektronische Signatur mit qualifiziertem Zertifikat"),
                NatureLevel.Advanced => seal ? Tr.T("fortgeschrittenes elektronisches Siegel (höchstens)") : Tr.T("fortgeschrittene elektronische Signatur (höchstens)"),
                _ => seal ? Tr.T("einfaches elektronisches Siegel") : Tr.T("einfache elektronische Signatur"),
            };
        if (nature.Kind == SignatureKind.DocumentTimestamp || nature.Level < NatureLevel.AdvancedQc) return text;
        return nature.Listed ? Tr.T("{0} (Anbieter laut EU-Vertrauensliste qualifiziert)", text) : Tr.T("{0} (laut Zertifikat)", text);
    }

    /// <summary>
    /// Whether the key is on a qualified creation device, as declared (decision E: never „card“ or „software“): „ja“, „unbekannt“, „nur nach
    /// dem Recht von …“; null where nothing is declared qualified (then the question does not arise) and for document time stamps.
    /// </summary>
    /// <param name="nature">The nature.</param>
    /// <returns>The localised text.</returns>
    public static string? Qscd(SignatureNature nature) => nature.Kind == SignatureKind.DocumentTimestamp ? null : nature.Qscd switch
    {
        QscdState.Yes => Tr.T("ja"),
        QscdState.OtherCountry => Tr.T("nur nach dem Recht eines Landes außerhalb der EU"),
        QscdState.No => Tr.T("nein (laut EU-Vertrauensliste)"),
        _ => nature.Level >= NatureLevel.AdvancedQc ? Tr.T("unbekannt") : null,
    };

    /// <summary>What to know about the kind and level: why it is not higher, a pseudonym.</summary>
    /// <param name="check">The result.</param>
    /// <returns>The localised notes.</returns>
    public static IReadOnlyList<string> NatureNotes(SignatureCheck check)
    {
        if (SignatureNatures.Of(check) is not { } nature) return [];
        var notes = new List<string>(nature.Reasons);
        if (nature.Pseudonym) notes.Add(Tr.T("Das Zertifikat nennt ein Pseudonym, nicht den Namen der Person."));
        if (nature.Remote) notes.Add(Tr.T("Der Schlüssel wird laut EU-Vertrauensliste beim Anbieter verwaltet (Fernsignatur)."));
        return notes;
    }

    /// <summary>
    /// What the level means, in two sentences, without legal advice: only a qualified signature can replace a handwritten one where the law
    /// asks for the written form (eIDAS Art. 25 (2), in Germany § 126a BGB); the trusted lists are not checked.
    /// </summary>
    /// <param name="level">The level.</param>
    /// <returns>The localised text.</returns>
    public static string LevelMeaning(SignatureLevel level) => level switch
    {
        SignatureLevel.Qualified => Tr.T("Das Zertifikat erklärt sich als qualifiziert, der Schlüssel liegt laut Zertifikat auf einer sicheren Signaturerstellungseinheit. Eine solche Signatur kann die handschriftliche Unterschrift ersetzen; ob der Anbieter auf der EU-Vertrauensliste steht, zeigt die Prüfung, wenn die Liste geladen ist."),
        SignatureLevel.Advanced => Tr.T("Eine Zertifizierungsstelle hat den Namen bestätigt. Wo das Gesetz die Schriftform verlangt, reicht das nicht — dafür braucht es eine qualifizierte Signatur (Zertifikat eines Anbieters auf der EU-Vertrauensliste, Schlüssel auf einer sicheren Signaturerstellungseinheit)."),
        _ => Tr.T("Sie zeigt, dass das Dokument seitdem unverändert ist, aber niemand Unabhängiges hat Ihren Namen bestätigt. Wo das Gesetz die Schriftform verlangt, reicht das nicht."),
    };

    /// <summary>What the level means for a certificate of this kind: a seal confirms the origin from an organisation, it replaces no signature of a person.</summary>
    /// <param name="level">The level.</param>
    /// <param name="kind">The kind the certificate states.</param>
    /// <returns>The localised text.</returns>
    public static string LevelMeaning(SignatureLevel level, SignatureKind kind) => kind == SignatureKind.Seal
        ? Tr.T("Ein Siegel bestätigt, dass das Dokument von einer Organisation stammt und seitdem unverändert ist. Es ist keine Erklärung einer Person und ersetzt keine persönliche Unterschrift.")
        : LevelMeaning(level);

    /// <summary>What a certification allows afterwards, for example „nur Ausfüllen und Unterschreiben“.</summary>
    /// <param name="level">The level.</param>
    /// <returns>The localised text.</returns>
    public static string Allowed(CertificationLevel level) => level switch
    {
        CertificationLevel.NoChanges => Tr.T("keine Änderungen"),
        CertificationLevel.FormsSigningAndComments => Tr.T("Ausfüllen, Unterschreiben und Kommentare"),
        _ => Tr.T("nur Ausfüllen und Unterschreiben"),
    };

    /// <summary>Which fields a signature locks, for example „alle Felder“.</summary>
    /// <param name="fieldLock">The lock.</param>
    /// <returns>The localised text.</returns>
    public static string Locked(FieldLock fieldLock) => fieldLock.Action switch
    {
        "Include" when fieldLock.Fields.Count == 0 => Tr.T("keine Felder"),
        "Include" => Tr.T("die Felder {0}", Names(fieldLock.Fields)),
        "Exclude" => Tr.T("alle Felder außer {0}", Names(fieldLock.Fields)),
        _ => Tr.T("alle Felder"),
    };

    /// <summary>The first eight names in order, „…“ for the rest.</summary>
    private static string Names(IReadOnlySet<string> names) =>
        string.Join(", ", names.Order(StringComparer.Ordinal).Take(8)) + (names.Count > 8 ? " …" : "");

    /// <summary>What the EU trusted lists say about the signer's issuer; null if they are not loaded.</summary>
    /// <param name="check">The result.</param>
    /// <returns>The text.</returns>
    public static string? TrustList(SignatureCheck check) => !check.TrustListsLoaded ? null : check.Issuer switch
    {
        { Qualified: true } m => Tr.T("qualifizierter Anbieter: {0} ({1})", m.Service.Provider, m.Service.Territory),
        { } m => Tr.T("{0} ({1}) — zur Unterschriftszeit nicht als qualifiziert geführt", m.Service.Provider, m.Service.Territory),
        _ => Tr.T("Aussteller nicht als qualifizierter Anbieter geführt"),
    };

    /// <summary>What the file's validation data (PAdES B-LT) says about the signature; null if the file carries none.</summary>
    /// <param name="check">The result.</param>
    /// <returns>The text.</returns>
    public static string? LongTerm(SignatureCheck check) => check.LongTerm switch
    {
        RevocationInfo.Coverage.Complete => Tr.T("Sperrstatus eingebettet — auch später ohne Verbindung prüfbar"),
        RevocationInfo.Coverage.Incomplete => Tr.T("unvollständig — nicht für jedes Zertifikat ein Sperrstatus in der Datei"),
        RevocationInfo.Coverage.Revoked => Tr.T("laut eingebetteter Auskunft gesperrt"),
        _ when check.Certificate is not null && !check.IsDocumentTimestamp => Tr.T("Sperrstatus nicht geprüft (keine Prüfdaten in der Datei)"),
        _ => null,
    };

    /// <summary>Whether the result counts as good (valid, also with unconfirmed identity).</summary>
    /// <param name="check">The result.</param>
    /// <returns>true if valid.</returns>
    /// <summary>
    /// The result in the terms of EN 319 102-1 (5.1.3, Tab. 5 and 6), derived here and nowhere else (docs/ABLAUFPLAN.md, 2.5, step 11): valid
    /// is TOTAL-PASSED; „identity not confirmed“ and „not confirmed“ are INDETERMINATE with their cause; invalid is TOTAL-FAILED, a damaged
    /// or contradictory block FORMAT_FAILURE unless a more exact cause is known. A change after signing that the certification or a field
    /// lock forbids and a method not checked have no sub-indication of the norm (PAdES-specific, reported as CUSTOM).
    /// </summary>
    public static ValidationIndication Indication(SignatureCheck check) => check.Outcome switch
    {
        SignatureOutcome.Valid => new(ValidationMain.TotalPassed, null),
        SignatureOutcome.ValidIdentityUnknown => new(ValidationMain.Indeterminate, check.Cause ?? SubIndication.NoCertificateChainFound),
        SignatureOutcome.Unconfirmed => new(ValidationMain.Indeterminate, check.Cause ?? SubIndication.TryLater),
        SignatureOutcome.ChangedAfterSigning => new(ValidationMain.TotalFailed, SubIndication.Custom),
        SignatureOutcome.NotChecked => new(ValidationMain.Indeterminate, SubIndication.Custom),
        _ => new(ValidationMain.TotalFailed, check.Cause ?? SubIndication.FormatFailure),
    };

    /// <summary>
    /// The baseline level of PAdES a signature reaches (ETSI EN 319 142-1; I-12, docs/ABLAUFPLAN.md, 9.3.4): B-B — CAdES block
    /// (ETSI.CAdES.detached); B-T — with a time stamp that fits, its own or a later document time stamp; B-LT — and validation data in the
    /// file (the structure, as the norm defines the level; whether they are complete says <see cref="LongTerm"/>); B-LTA — and covered by a
    /// later document time stamp. Null for a document time stamp and for other kinds of blocks.
    /// </summary>
    public static string? PadesLevel(SignatureCheck check)
    {
        if (check.IsDocumentTimestamp || check.Method != "ETSI.CAdES.detached" || check.Outcome == SignatureOutcome.Invalid) return null;
        if (check.Timestamp is not { Valid: true } && !check.CoveredByDocumentTimestamp) return "B-B";
        if (check.LongTerm is null) return "B-T";
        return check.CoveredByDocumentTimestamp ? "B-LTA" : "B-LT";
    }

    /// <summary>
    /// After an archive time stamp: the hint that the file carries qualified signatures but the newest document time stamp does not come from a
    /// qualified service of the loaded EU trusted lists (TR-ESOR, 2.1: the quality of the renewal at least that of the signatures; docs/ABLAUFPLAN.md,
    /// 9.3.4, step 2) — a hint, no refusal; null otherwise or without the lists.
    /// </summary>
    public static string? ArchiveServiceHint(IReadOnlyList<SignatureCheck> checks)
    {
        var newest = checks.Where(c => c.IsDocumentTimestamp).MaxBy(c => c.SigningTime);
        if (newest is not { TrustListsLoaded: true } || newest.TimestampService is { Qualified: true }) return null;
        return checks.Any(c => !c.IsDocumentTimestamp && c.Listed is { Qualified: true })
            ? Tr.T("Die Datei trägt qualifizierte Signaturen, der Dienst des Archiv-Zeitstempels ist laut EU-Vertrauensliste aber nicht qualifiziert; für den Beweiswert wäre ein qualifizierter Dienst besser.")
            : null;
    }

    /// <summary>„Schutz erneuern bis spätestens …“ for a document time stamp, „jetzt“ when the date has passed; null without a date.</summary>
    public static string? RenewBefore(SignatureCheck check) => check.RenewBefore switch
    {
        null => null,
        { } due when due <= DateTimeOffset.Now => Tr.T("jetzt (die Frist ist abgelaufen)"),
        { } due => due.LocalDateTime.ToString("d", Tr.Culture),
    };

    public static bool IsValid(SignatureCheck check) =>
        check.Outcome is SignatureOutcome.Valid or SignatureOutcome.ValidIdentityUnknown || check is { Outcome: SignatureOutcome.Unconfirmed, Unproven: false };

    /// <summary>The reason of a revocation in words (CRLReason, RFC 5280, 5.3.1); „nicht angegeben“ without one.</summary>
    public static string RevocationReason(int? reason) => reason switch
    {
        1 => Tr.T("Schlüssel kompromittiert"),
        2 => Tr.T("Zertifizierungsstelle kompromittiert"),
        3 => Tr.T("Zugehörigkeit geändert"),
        4 => Tr.T("ersetzt"),
        5 => Tr.T("Tätigkeit eingestellt"),
        6 => Tr.T("vorübergehend gesperrt"),
        9 => Tr.T("Berechtigung entzogen"),
        10 => Tr.T("Attributstelle kompromittiert"),
        _ => Tr.T("nicht angegeben"),
    };
}

/// <summary>The main indication of a validation (EN 319 102-1, 5.1.3).</summary>
public enum ValidationMain
{
    /// <summary>TOTAL-PASSED: every check passed.</summary>
    TotalPassed,

    /// <summary>TOTAL-FAILED: proven wrong (format, hash, signature value, revoked before the proven time …).</summary>
    TotalFailed,

    /// <summary>INDETERMINATE: what is known does not allow either.</summary>
    Indeterminate,
}

/// <summary>A result in the terms of EN 319 102-1: main indication and sub-indication (null with TOTAL-PASSED).</summary>
/// <param name="Main">The main indication.</param>
/// <param name="Sub">The sub-indication as the norm writes it (<see cref="SubIndication"/>).</param>
public sealed record ValidationIndication(ValidationMain Main, string? Sub)
{
    /// <summary>As the norm writes it, e.g. „INDETERMINATE/TRY_LATER“.</summary>
    public override string ToString()
    {
        var main = Main switch
        {
            ValidationMain.TotalPassed => "TOTAL-PASSED",
            ValidationMain.TotalFailed => "TOTAL-FAILED",
            _ => "INDETERMINATE",
        };
        return Sub is null ? main : main + "/" + Sub;
    }
}

/// <summary>The sub-indications of EN 319 102-1 (Tab. 6) PDF-Knead gives, as the norm writes them.</summary>
public static class SubIndication
{
    public const string FormatFailure = "FORMAT_FAILURE";
    public const string HashFailure = "HASH_FAILURE";
    public const string SigCryptoFailure = "SIG_CRYPTO_FAILURE";
    public const string Revoked = "REVOKED";
    public const string NotYetValid = "NOT_YET_VALID";
    public const string SigConstraintsFailure = "SIG_CONSTRAINTS_FAILURE";
    public const string ChainConstraintsFailure = "CHAIN_CONSTRAINTS_FAILURE";
    public const string CryptoConstraintsFailureNoPoe = "CRYPTO_CONSTRAINTS_FAILURE_NO_POE";
    public const string OutOfBoundsNoPoe = "OUT_OF_BOUNDS_NO_POE";
    public const string RevokedNoPoe = "REVOKED_NO_POE";
    public const string RevokedCaNoPoe = "REVOKED_CA_NO_POE";
    public const string TryLater = "TRY_LATER";
    public const string NoSigningCertificateFound = "NO_SIGNING_CERTIFICATE_FOUND";
    public const string NoCertificateChainFound = "NO_CERTIFICATE_CHAIN_FOUND";
    public const string CertificateChainGeneralFailure = "CERTIFICATE_CHAIN_GENERAL_FAILURE";
    public const string TimestampOrderFailure = "TIMESTAMP_ORDER_FAILURE";

    /// <summary>No sub-indication of the norm fits (a PAdES-specific change, a method not checked).</summary>
    public const string Custom = "CUSTOM";
}
