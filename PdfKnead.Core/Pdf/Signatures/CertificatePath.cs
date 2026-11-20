using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Localization;

namespace PdfKnead.Core.Pdf.Signatures;

/// <summary>
/// The certificate path of a signature, checked rule by rule (RFC 5280, 6.1; docs/ABLAUFPLAN.md, 9.3.2): the system's chain engine only
/// finds the candidate path to one of the given roots, offline; each rule is then checked here with a reason — validity at the time checked,
/// basicConstraints and keyUsage of every authority in between, pathLenConstraint — and what the engine still reports (a broken signature
/// of a link, a critical extension nobody understands, a name constraint) is taken over with a reason as well. Nothing the engine rejects
/// passes: whatever it reports that no rule here names is a violation too.
/// </summary>
internal static class CertificatePath
{
    /// <summary>How a path ends.</summary>
    internal enum Verdict
    {
        /// <summary>A path to a given root that keeps every rule at the time checked.</summary>
        Valid,

        /// <summary>No path to any of the given roots (EN 319 102-1: NO_CERTIFICATE_CHAIN_FOUND).</summary>
        NoChain,

        /// <summary>The path keeps every rule but the time: a certificate of it was not valid at the time checked (OUT_OF_BOUNDS_…).</summary>
        OutOfBounds,

        /// <summary>A path to a root that breaks a rule (CERTIFICATE_CHAIN_GENERAL_FAILURE, EN 319 102-1, 5.2.6.4, step 4 e).</summary>
        Violated,
    }

    /// <summary>The verdict with its reason: the rule broken, or the certificate of the chain out of its validity (null for the signer's own, which is said elsewhere).</summary>
    internal readonly record struct Result(Verdict Verdict, string? Reason)
    {
        /// <summary>The certificates of the path found (signer first); empty without one.</summary>
        public IReadOnlyList<CertificateLink> Links { get; init; } = [];

        public bool Valid => Verdict == Verdict.Valid;
    }

    // Reported by the engine and checked here by own rules (time, basicConstraints), or not asked for (revocation, which is checked offline elsewhere)
    private const X509ChainStatusFlags OwnRules = X509ChainStatusFlags.NotTimeValid | X509ChainStatusFlags.NotTimeNested |
        X509ChainStatusFlags.InvalidBasicConstraints | X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;

    private const X509ChainStatusFlags NameRules = X509ChainStatusFlags.HasNotPermittedNameConstraint |
        X509ChainStatusFlags.HasExcludedNameConstraint | X509ChainStatusFlags.HasNotSupportedNameConstraint |
        X509ChainStatusFlags.HasNotDefinedNameConstraint | X509ChainStatusFlags.InvalidNameConstraints;

    /// <summary>Checks the path of <paramref name="certificate"/> to one of <paramref name="roots"/> at <paramref name="at"/>.</summary>
    /// <param name="certificate">The certificate whose path is checked (the signer's or a service's).</param>
    /// <param name="embedded">Certificates that came with it (the signature block, the store of the file).</param>
    /// <param name="roots">The trusted roots; none means no path.</param>
    /// <param name="at">The time the path has to hold.</param>
    internal static Result Check(X509Certificate2 certificate, X509Certificate2Collection embedded, X509Certificate2Collection roots, DateTimeOffset at)
    {
        if (roots.Count == 0) return new(Verdict.NoChain, null);
        using var chain = new X509Chain();
        chain.ChainPolicy.VerificationTime = at.LocalDateTime;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        chain.ChainPolicy.ExtraStore.AddRange(embedded);
        // the engine is asked for the path only; whether it holds is decided below
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
        try
        {
            chain.Build(certificate);
            return Judge(chain, roots, at) with { Links = LinksOf(chain) };
        }
        catch (CryptographicException)
        {
            return new(Verdict.NoChain, null);
        }
        finally
        {
            foreach (var element in chain.ChainElements) element.Certificate.Dispose();
        }
    }

    /// <summary>The certificates of the path the engine found, from the signer's to the last (for the report; empty without a path).</summary>
    private static IReadOnlyList<CertificateLink> LinksOf(X509Chain chain) =>
        [.. chain.ChainElements.Select(e => new CertificateLink(Name(e.Certificate), e.Certificate.Subject, new DateTimeOffset(e.Certificate.NotBefore),
            new DateTimeOffset(e.Certificate.NotAfter), Platform.SigningCertificate.FingerprintOf(e.Certificate)))];

    private static Result Judge(X509Chain chain, X509Certificate2Collection roots, DateTimeOffset at)
    {
        var elements = chain.ChainElements;
        var n = elements.Count;
        if (n == 0) return new(Verdict.NoChain, null);
        var anchor = elements[n - 1].Certificate;
        if (!roots.Cast<X509Certificate2>().Any(r => r.RawData.AsSpan().SequenceEqual(anchor.RawData))) return new(Verdict.NoChain, null);
        // the links: each signed by the next (the engine verifies the signatures and reports a broken one) — a certificate of that name did not
        // issue it, so there is no chain at all (only the names matched, as with two authorities of the same name); said as the reason
        for (var i = 0; i < n; i++)
            if (Flags(elements[i]).HasFlag(X509ChainStatusFlags.NotSignatureValid))
                return new(Verdict.NoChain, Tr.T("Die Unterschrift des Ausstellers auf dem Zertifikat „{0}“ ist ungültig.", Name(elements[i].Certificate)));
        // the root is the anchor and taken as given (RFC 5280, 6.1.1 d) — unless it says itself that it is no authority, which the chain
        // engines refuse as well
        if (anchor.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is { CertificateAuthority: false })
            return Violated(Tr.T("Das Zertifikat „{0}“ stellt Zertifikate aus, ist laut basicConstraints aber keine Zertifizierungsstelle.", Name(anchor)));
        // the authorities in between (6.1.4 k, n): marked as authorities, allowed to issue certificates
        for (var i = n - 2; i >= 1; i--)
        {
            var authority = elements[i].Certificate;
            var constraints = authority.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
            if (constraints is null)
                return Violated(Tr.T("Das Zertifikat „{0}“ stellt Zertifikate aus, ist aber nicht als Zertifizierungsstelle ausgewiesen (basicConstraints fehlt).", Name(authority)));
            if (!constraints.CertificateAuthority)
                return Violated(Tr.T("Das Zertifikat „{0}“ stellt Zertifikate aus, ist laut basicConstraints aber keine Zertifizierungsstelle.", Name(authority)));
            if (authority.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault() is { } usage && !usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign))
                return Violated(Tr.T("Das Zertifikat „{0}“ darf laut Schlüsselverwendung keine Zertifikate ausstellen.", Name(authority)));
        }
        // pathLenConstraint (6.1.4 l, m): counted from the root down over the authorities in between that are not self-issued; the root's own
        // constraint counts as well (as the chain engines do)
        var (left, setBy) = (int.MaxValue, (X509Certificate2?)null);
        for (var i = n - 1; i >= 1; i--)
        {
            var authority = elements[i].Certificate;
            if (i < n - 1 && !SelfIssued(authority))
            {
                if (left <= 0)
                    return Violated(Tr.T("Die Kette ist länger, als das Zertifikat „{0}“ erlaubt (pathLenConstraint).", Name(setBy!)));
                left--;
            }
            if (authority.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is { HasPathLengthConstraint: true } own && own.PathLengthConstraint < left)
                (left, setBy) = (own.PathLengthConstraint, authority);
        }
        // what else the engine reports about a link
        for (var i = 0; i < n; i++)
        {
            var flags = Flags(elements[i]);
            var name = Name(elements[i].Certificate);
            // revocation is not asked of the engine (checked offline elsewhere): „revoked“ here is the system's list of distrusted certificates
            if ((flags & (X509ChainStatusFlags.ExplicitDistrust | X509ChainStatusFlags.Revoked)) != 0)
                return Violated(Tr.T("Das Zertifikat „{0}“ ist auf diesem Rechner ausdrücklich als nicht vertrauenswürdig gesperrt.", name));
            if (flags.HasFlag(X509ChainStatusFlags.HasNotSupportedCriticalExtension))
                return Violated(Tr.T("Das Zertifikat „{0}“ trägt eine als kritisch markierte Erweiterung, die die Prüfung nicht kennt.", name));
            if ((flags & NameRules) != 0)
                return Violated(Tr.T("Das Zertifikat „{0}“ verletzt die Namensbeschränkung eines Ausstellers.", name));
            if ((flags & ~OwnRules) is var other and not X509ChainStatusFlags.NoError)
                return Violated(Tr.T("Die Kette des Zertifikats „{0}“ ist ungültig ({1}).", name, other));
        }
        // the time (shell model: every certificate of the path valid at the time checked, the root included, as before); out of it is
        // OUT_OF_BOUNDS, as for the signer's own certificate — the time, not the structure of the path
        for (var i = n - 1; i >= 1; i--)
        {
            var link = elements[i].Certificate;
            if (!ValidAt(link, at))
                return new(Verdict.OutOfBounds, Tr.T("Das Zertifikat „{0}“ der Kette war am {1} nicht gültig (gültig vom {2} bis {3}).", Name(link),
                    at.LocalDateTime.ToString("d", Tr.Culture), link.NotBefore.ToString("d", Tr.Culture), link.NotAfter.ToString("d", Tr.Culture)));
        }
        return ValidAt(elements[0].Certificate, at) ? new(Verdict.Valid, null) : new(Verdict.OutOfBounds, null);
    }

    private static Result Violated(string reason) => new(Verdict.Violated, reason);

    private static X509ChainStatusFlags Flags(X509ChainElement element) =>
        element.ChainElementStatus.Aggregate(X509ChainStatusFlags.NoError, (all, status) => all | status.Status);

    private static bool ValidAt(X509Certificate2 certificate, DateTimeOffset at) =>
        certificate.NotBefore <= at.LocalDateTime && at.LocalDateTime <= certificate.NotAfter;

    private static bool SelfIssued(X509Certificate2 certificate) => certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);

    private static string Name(X509Certificate2 certificate) =>
        certificate.GetNameInfo(X509NameType.SimpleName, false) is { Length: > 0 } simple ? simple : certificate.Subject;
}

/// <summary>One certificate of a path, as the report shows it (docs/FEHLERGEDAECHTNIS.md, No. 723).</summary>
/// <param name="Name">The holder in short.</param>
/// <param name="Subject">The full name of the holder.</param>
/// <param name="ValidFrom">Valid from.</param>
/// <param name="ValidUntil">Valid until.</param>
/// <param name="Fingerprint">SHA-256 in groups of four.</param>
public sealed record CertificateLink(string Name, string Subject, DateTimeOffset ValidFrom, DateTimeOffset ValidUntil, string Fingerprint);
