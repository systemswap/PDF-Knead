using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Xml;
using PdfKnead.Core.Platform;

namespace PdfKnead.Core.Pdf.Signatures.TrustLists;

/// <summary>Where the trusted lists come from: the only way this part goes to the network (<see cref="Platform.HttpTrustListSource"/>).</summary>
public interface ITrustListSource
{
    /// <summary>Fetches a list.</summary>
    /// <param name="address">Its address.</param>
    /// <param name="cancel">Cancels the download.</param>
    /// <returns>The file.</returns>
    /// <exception cref="TrustListException">It could not be fetched.</exception>
    byte[] Fetch(Uri address, CancellationToken cancel = default);
}

/// <summary>The EU trusted lists as loaded and checked: the list of lists, the national lists, and what could not be used.</summary>
public sealed class TrustListSet
{
    internal TrustListSet(TrustedList lotl, IReadOnlyList<TrustedList> lists, IReadOnlyDictionary<string, string> failures)
    {
        Lotl = lotl;
        Lists = lists;
        Failures = failures;
    }

    /// <summary>The EU list of lists.</summary>
    public TrustedList Lotl { get; }

    /// <summary>The national lists whose signature checked out.</summary>
    public IReadOnlyList<TrustedList> Lists { get; }

    /// <summary>Countries whose list could not be used, with the reason.</summary>
    public IReadOnlyDictionary<string, string> Failures { get; }

    private static readonly DateTimeOffset Eidas = new(2016, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly HashSet<string> BeforeEidas =
    [
        "http://uri.etsi.org/TrstSvc/TrustedList/Svcstatus/undersupervision",
        "http://uri.etsi.org/TrstSvc/TrustedList/Svcstatus/accredited",
        "http://uri.etsi.org/TrstSvc/TrustedList/Svcstatus/supervisionincessation",
    ];

    private const string Granted = "http://uri.etsi.org/TrstSvc/TrustedList/Svcstatus/granted";

    /// <summary>
    /// The listed service a certificate depends on: for a signer, the qualified certificate authority (CA/QC) that issued it or a certificate
    /// above it; for a time-stamping unit, the qualified time-stamping service (TSA/QTST) it belongs to.
    /// </summary>
    /// <param name="certificate">The signer's or the time-stamping unit's certificate.</param>
    /// <param name="extra">The certificates that came with it.</param>
    /// <param name="type">The service type (<see cref="TrustedList.QualifiedCa"/> or <see cref="TrustedList.QualifiedTimestamps"/>).</param>
    /// <param name="at">The time that counts (the signing time, or the time stamp).</param>
    /// <returns>The service and whether it was qualified then (status granted; for a CA, for electronic signatures); null if not listed.</returns>
    public TrustListMatch? Find(X509Certificate2 certificate, X509Certificate2Collection extra, string type, DateTimeOffset at) =>
        Match(certificate, extra, type, at, checkPath: false, kind: SignatureKind.Signature);

    /// <summary>
    /// What the lists say about a certificate (ETSI TS 119 615; docs/FEHLERGEDAECHTNIS.md, No. 484), at its issuance and at the time of
    /// signing — qualified only if at both: the service must be granted and allow the kind of the certificate (ForeSignatures, ForeSeals,
    /// ForWebSiteAuthentication), its qualifiers whose criteria select the certificate decide before the certificate's statements:
    /// NotQualified / QCStatement whether qualified, QCForESig / QCForESeal / QCForWSA (QCForLegalPerson: a seal) the kind, QCWithQSCD /
    /// QCQSCDManagedOnBehalf / QCNoQSCD (and the SSCD forms of before eIDAS) the device. A service with contradicting qualifiers counts as
    /// none; services giving different answers make it open (not qualified).
    /// </summary>
    /// <param name="certificate">The signer's certificate.</param>
    /// <param name="extra">The certificates that came with it.</param>
    /// <param name="profile">What the certificate states.</param>
    /// <param name="signedAt">The time of signing that counts: a trusted time stamp's, else now.</param>
    /// <returns>The verdict; null if no qualified authority of the lists stands above the certificate at all.</returns>
    public ListVerdict? Qualify(X509Certificate2 certificate, X509Certificate2Collection extra, CertificateProfile profile, DateTimeOffset signedAt)
    {
        var issued = Evaluate(certificate, extra, profile, new DateTimeOffset(certificate.NotBefore));
        var signed = Evaluate(certificate, extra, profile, signedAt);
        if (issued is null && signed is null) return null;
        if (signed is null || !signed.Qualified) return signed ?? issued! with { Qualified = false };
        if (issued is null || !issued.Qualified)
            return signed with { Qualified = false, Reason = Tr.T("Bei der Ausstellung des Zertifikats war der Anbieter laut EU-Vertrauensliste nicht qualifiziert.") };
        return signed with { Kind = issued.Kind == signed.Kind ? signed.Kind : null };
    }

    private ListVerdict? Evaluate(X509Certificate2 certificate, X509Certificate2Collection extra, CertificateProfile profile, DateTimeOffset at)
    {
        var services = Services(certificate, extra, TrustedList.QualifiedCa);
        if (services.Count == 0) return null;
        var answers = new List<ListVerdict>();
        TrustListMatch? notGranted = null;
        foreach (var service in services)
        {
            var period = service.At(at);
            if (period is null || !IsGranted(period, at))
            {
                notGranted ??= new TrustListMatch(service, period, false);
                continue;
            }
            var qualifiers = period.Qualifications.Where(q => q.Criteria.Matches(certificate, profile.Policies) == true)
                .SelectMany(q => q.Qualifiers).Select(q => q.StartsWith(ListQualification.Prefix, StringComparison.Ordinal) ? q[ListQualification.Prefix.Length..] : q)
                .ToHashSet(StringComparer.Ordinal);
            bool Has(string name) => qualifiers.Contains(name);
            var forKinds = new[] { "QCForESig", "QCForESeal", "QCForWSA" }.Count(Has);
            // contradicting qualifiers (TS 119 615): this service tells nothing
            if (Has("QCStatement") && Has("NotQualified") || forKinds > 1 || Has("QCForESig") && Has("QCForLegalPerson")
                || (Has("QCWithQSCD") || Has("QCQSCDManagedOnBehalf")) && Has("QCNoQSCD") || Has("QCWithSSCD") && Has("QCNoSSCD"))
                continue;
            SignatureKind? kind = Has("QCForESig") ? SignatureKind.Signature : Has("QCForESeal") || Has("QCForLegalPerson") ? SignatureKind.Seal
                : Has("QCForWSA") ? SignatureKind.WebCertificate : profile.QcTypeUnclear ? null : profile.Kind;
            var match = new TrustListMatch(service, period, true);
            if (!period.Allows(kind))
            {
                answers.Add(new ListVerdict(false, kind, null, false, match with { Qualified = false }, kind == SignatureKind.Seal
                    ? Tr.T("Die EU-Vertrauensliste führt den Aussteller zur Unterschriftszeit nicht als qualifizierten Anbieter für Siegel.")
                    : Tr.T("Die EU-Vertrauensliste führt den Aussteller zur Unterschriftszeit nicht als qualifizierten Anbieter für Signaturen.")));
                continue;
            }
            var qualified = !Has("NotQualified") && (Has("QCStatement") || profile.EuQualified);
            QscdState? qscd = Has("QCWithQSCD") || Has("QCQSCDManagedOnBehalf") || Has("QCWithSSCD") ? QscdState.Yes
                : Has("QCNoQSCD") || Has("QCNoSSCD") ? QscdState.No : null;
            answers.Add(new ListVerdict(qualified, kind, qscd, Has("QCQSCDManagedOnBehalf"), match,
                qualified ? null : Tr.T("Die EU-Vertrauensliste führt dieses Zertifikat als nicht qualifiziert.")));
        }
        if (answers.Count == 0)
            return new ListVerdict(false, null, null, false, notGranted, Tr.T("Die EU-Vertrauensliste führt den Aussteller zur Unterschriftszeit nicht als qualifizierten Anbieter."));
        var first = answers[0];
        if (answers.Skip(1).Any(a => a.Qualified != first.Qualified || a.Kind != first.Kind || a.Qscd != first.Qscd))
            return first with { Qualified = false, Reason = Tr.T("Mehrere Einträge der EU-Vertrauensliste widersprechen sich für dieses Zertifikat.") };
        return answers.FirstOrDefault(a => a.Qualified) ?? first;
    }

    /// <summary>Whether a period's status counts as qualified at a time (granted; before eIDAS also the old statuses).</summary>
    private static bool IsGranted(TrustServicePeriod period, DateTimeOffset at) =>
        period.Status == Granted || (at < Eidas && BeforeEidas.Contains(period.Status));

    /// <summary>The services of a type whose certificates stand above the certificate (the links the chain verified).</summary>
    private List<TrustService> Services(X509Certificate2 certificate, X509Certificate2Collection extra, string type)
    {
        var links = new List<X509Certificate2>();
        using (var chain = new X509Chain())
        {
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
            chain.ChainPolicy.ExtraStore.AddRange(extra);
            chain.Build(certificate);
            links.AddRange(chain.ChainElements.Select(e => e.Certificate));
        }
        try
        {
            var verified = 1;
            while (verified < links.Count && RevocationInfo.IssuedBy(links[verified - 1], links[verified])) verified++;
            links.RemoveRange(verified, links.Count - verified);
            return Lists.SelectMany(l => l.Services).Where(s => s.Type == type && s.Certificates.Any(own => Covers(own, certificate, links, type, null))).ToList();
        }
        finally
        {
            foreach (var link in links) link.Dispose();
        }
    }

    /// <summary>
    /// Whether the lists confirm who signed (ETSI EN 319 102-1, 5.2.4: the trusted list as trust anchor; eIDAS Art. 22): a qualified
    /// certificate authority listed as granted at <paramref name="at"/> stands above the certificate, and the path to it holds at that time —
    /// each link issued by the next (signature checked), valid then, the authorities above the certificate marked as such and allowed to issue
    /// certificates (RFC 5280, 6.1.3 and 6.1.4).
    /// </summary>
    /// <param name="certificate">The signer's certificate.</param>
    /// <param name="extra">The certificates that came with it.</param>
    /// <param name="at">The time that counts: the time a trusted time stamp confirms, otherwise now.</param>
    /// <returns>true if the identity is confirmed.</returns>
    public bool ConfirmsIdentity(X509Certificate2 certificate, X509Certificate2Collection extra, DateTimeOffset at) =>
        // the identity does not hang on the kind: any granted qualified authority vouches for the name (docs/FEHLERGEDAECHTNIS.md, No. 484)
        Match(certificate, extra, TrustedList.QualifiedCa, at, checkPath: true, kind: null) is { Qualified: true };

    private TrustListMatch? Match(X509Certificate2 certificate, X509Certificate2Collection extra, string type, DateTimeOffset at, bool checkPath,
        SignatureKind? kind)
    {
        var links = new List<X509Certificate2>();
        using (var chain = new X509Chain())
        {
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
            chain.ChainPolicy.ExtraStore.AddRange(extra);
            chain.Build(certificate);
            links.AddRange(chain.ChainElements.Select(e => e.Certificate));
        }
        try
        {
            // The chain is built leniently (the lists, not the system, are the anchor): only the links up to the first one that did not
            // issue the one below count — a certificate merely naming a listed authority as its issuer is not covered by it
            var verified = 1;
            while (verified < links.Count && RevocationInfo.IssuedBy(links[verified - 1], links[verified])) verified++;
            links.RemoveRange(verified, links.Count - verified);
            TrustListMatch? listed = null;
            foreach (var service in Lists.SelectMany(l => l.Services).Where(s => s.Type == type))
            {
                if (!service.Certificates.Any(own => Covers(own, certificate, links, type, checkPath ? at : null))) continue;
                var period = service.At(at);
                var granted = period is not null && IsGranted(period, at) && (type != TrustedList.QualifiedCa || period.Allows(kind));
                var match = new TrustListMatch(service, period, granted);
                if (granted) return match;
                listed ??= match;
            }
            return listed;
        }
        finally
        {
            foreach (var link in links) link.Dispose();
        }
    }

    /// <summary>
    /// Whether a service's certificate stands for a certificate: for a time-stamping unit its own certificate or its issuer; for a signer one
    /// of the authorities above it (an issuer the chain did not reach counts when the signer's signature verifies with its key). With
    /// <paramref name="pathAt"/>, the links up to that authority must also hold at that time.
    /// </summary>
    private static bool Covers(X509Certificate2 service, X509Certificate2 certificate, List<X509Certificate2> links, string type, DateTimeOffset? pathAt)
    {
        if (type == TrustedList.QualifiedTimestamps && SameKey(service, certificate)) return true;
        for (var i = 1; i < links.Count; i++)
            if (SameKey(service, links[i])) return pathAt is not { } at || PathHolds(links, i, at);
        if (!certificate.IssuerName.RawData.AsSpan().SequenceEqual(service.SubjectName.RawData) || !RevocationInfo.IssuedBy(certificate, service)) return false;
        return pathAt is not { } when || PathHolds(links, 0, when);
    }

    /// <summary>The links below the anchor (index <paramref name="anchor"/>, itself not checked) valid at the time, those above the certificate authorities.</summary>
    private static bool PathHolds(List<X509Certificate2> links, int anchor, DateTimeOffset at)
    {
        for (var i = 0; i < Math.Max(1, anchor); i++)
        {
            var link = links[i];
            if (at.UtcDateTime < link.NotBefore.ToUniversalTime() || at.UtcDateTime > link.NotAfter.ToUniversalTime()) return false;
            if (i == 0) continue;
            if (link.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is not { CertificateAuthority: true } constraints) return false;
            if (constraints.HasPathLengthConstraint && constraints.PathLengthConstraint < i - 1) return false;
            if (link.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault() is { } usage && (usage.KeyUsages & X509KeyUsageFlags.KeyCertSign) == 0) return false;
        }
        return true;
    }

    private static bool SameKey(X509Certificate2 a, X509Certificate2 b) =>
        a.SubjectName.RawData.AsSpan().SequenceEqual(b.SubjectName.RawData) && a.PublicKey.EncodedKeyValue.RawData.AsSpan().SequenceEqual(b.PublicKey.EncodedKeyValue.RawData);
}

/// <summary>A listed trust service found for a certificate.</summary>
/// <param name="Service">The service.</param>
/// <param name="Period">Its status at the time that counts; null if it was not listed yet.</param>
/// <param name="Qualified">It was qualified then (granted; for a CA, for electronic signatures).</param>
public sealed record TrustListMatch(TrustService Service, TrustServicePeriod? Period, bool Qualified);

/// <summary>
/// Loads, checks and keeps the EU trusted lists (Regulation (EU) No 910/2014, Art. 22; Implementing Decision (EU) 2015/1505): only on the
/// user's click (KONZEPT.md, section 5.17, „EU-Vertrauensliste“). The list of lists must be signed with a key of the Commission — the pinned
/// anchors, or a later key announced through the pivot lists, each signed with the key before (ETSI TS 119 612, 5.3.13 note; the
/// Commission's „pivot LOTL“ scheme) —, each national list with a key the list of lists names for that country. Kept as published in the
/// user's folder and checked again when loaded, so a changed file is never used.
/// </summary>
public static class TrustListStore
{
    /// <summary>Address of the EU list of lists.</summary>
    public static readonly Uri Lotl = new("https://ec.europa.eu/tools/lotl/eu-lotl.xml");

    private const string LotlFile = "eu-lotl.xml";

    private static readonly Regex PivotName = new(@"^/tools/lotl/eu-lotl-pivot-(\d{1,6})\.xml$", RegexOptions.CultureInvariant);

    /// <summary>The folder the lists are kept in.</summary>
    public static string DefaultFolder => Path.Combine(AppLocations.UserDataFolder, "eu-vertrauensliste");

    private static readonly Lazy<IReadOnlyList<X509Certificate2>> PinnedAnchors = new(ReadAnchors);

    /// <summary>The pinned signing certificates of the list of lists (from the Official Journal of the EU).</summary>
    public static IReadOnlyList<X509Certificate2> Anchors => PinnedAnchors.Value;

    private static IReadOnlyList<X509Certificate2> ReadAnchors()
    {
        using var stream = typeof(TrustListStore).Assembly.GetManifestResourceStream("TrustLists/lotl-anker.crt")
            ?? throw new InvalidOperationException("TrustLists/lotl-anker.crt fehlt.");
        using var reader = new StreamReader(stream);
        var collection = new X509Certificate2Collection();
        collection.ImportFromPem(reader.ReadToEnd());
        return [.. collection];
    }

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, TrustListSet?> Checked = new(StringComparer.Ordinal);

    /// <summary>The lists kept in a folder, checked once per session and then remembered; null if none are kept or they do not check out.</summary>
    /// <param name="folder">The folder; null for <see cref="DefaultFolder"/>.</param>
    /// <param name="anchors">The anchors; null for <see cref="Anchors"/> (tests give their own).</param>
    /// <returns>The lists.</returns>
    public static TrustListSet? Current(string? folder = null, IReadOnlyList<X509Certificate2>? anchors = null)
    {
        folder ??= DefaultFolder;
        lock (Gate)
        {
            if (Checked.TryGetValue(folder, out var known)) return known;
        }
        TrustListSet? set;
        try
        {
            set = File.Exists(Path.Combine(folder, LotlFile)) ? Load(folder, anchors) : null;
        }
        catch (Exception e) when (e is TrustListException or IOException or UnauthorizedAccessException)
        {
            set = null;
        }
        lock (Gate) Checked[folder] = set;
        return set;
    }

    /// <summary>Loads the lists kept in a folder and checks them again.</summary>
    /// <param name="folder">The folder.</param>
    /// <param name="anchors">The anchors; null for <see cref="Anchors"/> (tests give their own).</param>
    /// <returns>The lists.</returns>
    /// <exception cref="TrustListException">The list of lists is missing or does not check out.</exception>
    public static TrustListSet Load(string folder, IReadOnlyList<X509Certificate2>? anchors = null) =>
        Assemble((name, _) => File.Exists(Path.Combine(folder, name)) ? File.ReadAllBytes(Path.Combine(folder, name)) : throw new TrustListException(Tr.T("Nicht geladen.")),
            null, anchors ?? Anchors, null, CancellationToken.None);

    /// <summary>
    /// Fetches the list of lists and all national lists, checks them, and keeps them in the folder (replacing what was there only when the list
    /// of lists checks out). A national list that cannot be fetched or does not check out is reported, the others are kept.
    /// </summary>
    /// <param name="source">Where to fetch.</param>
    /// <param name="folder">The folder; null for <see cref="DefaultFolder"/>.</param>
    /// <param name="progress">Receives the country being fetched.</param>
    /// <param name="cancel">Cancels; what was there stays.</param>
    /// <param name="anchors">The anchors; null for <see cref="Anchors"/> (tests give their own).</param>
    /// <param name="lotl">Address of the list of lists; null for <see cref="Lotl"/> (tests).</param>
    /// <returns>The lists.</returns>
    /// <exception cref="TrustListException">The list of lists cannot be fetched or does not check out; nothing is changed.</exception>
    public static TrustListSet Download(ITrustListSource source, string? folder = null, IProgress<string>? progress = null, CancellationToken cancel = default,
        IReadOnlyList<X509Certificate2>? anchors = null, Uri? lotl = null)
    {
        folder ??= DefaultFolder;
        var fresh = folder + ".neu";
        if (Directory.Exists(fresh)) Directory.Delete(fresh, recursive: true);
        Directory.CreateDirectory(fresh);
        try
        {
            var set = Assemble((_, address) => source.Fetch(address, cancel), (name, data) => File.WriteAllBytes(Path.Combine(fresh, name), data),
                anchors ?? Anchors, progress, cancel, lotl ?? Lotl);
            cancel.ThrowIfCancellationRequested();
            Replace(fresh, folder);
            lock (Gate) Checked[folder] = set;
            return set;
        }
        finally
        {
            if (Directory.Exists(fresh)) Directory.Delete(fresh, recursive: true);
        }
    }

    /// <summary>
    /// The fresh folder in place of the kept one: the kept one is first moved aside and removed only once the fresh one is in place, and put
    /// back if that fails; each move is tried a few times — a virus scanner still reading the freshly written files holds them for a moment
    /// (access denied). Before, the kept lists were deleted first, and a failed move lost the fresh ones as well (docs/FEHLERGEDAECHTNIS.md,
    /// No. 1093). A kept folder left aside by an interruption comes back first.
    /// </summary>
    internal static void Replace(string fresh, string folder)
    {
        var aside = folder + ".alt";
        if (!Directory.Exists(folder) && Directory.Exists(aside)) Retried(() => Move(aside, folder));
        if (Directory.Exists(aside)) Directory.Delete(aside, recursive: true);
        var kept = Directory.Exists(folder);
        if (kept) Retried(() => Move(folder, aside));
        try
        {
            Retried(() => Move(fresh, folder));
        }
        catch
        {
            if (kept && !Directory.Exists(folder)) Retried(() => Move(aside, folder));
            throw;
        }
        if (!kept) return;
        try
        {
            Directory.Delete(aside, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // removed before the next replacement
        }
    }

    /// <summary>Moves a folder (tests set their own).</summary>
    internal static Action<string, string> Move { get; set; } = Directory.Move;

    /// <summary>The wait before the next try of a move (tests set their own).</summary>
    internal static Action<int> RetryWait { get; set; } = n => Thread.Sleep(100 * (n + 1));

    private static void Retried(Action move)
    {
        for (var n = 0; ; n++)
        {
            try
            {
                move();
                return;
            }
            catch (Exception e) when (n < 5 && e is IOException or UnauthorizedAccessException)
            {
                RetryWait(n);
            }
        }
    }

    /// <summary>Removes the kept lists.</summary>
    /// <param name="folder">The folder; null for <see cref="DefaultFolder"/>.</param>
    public static void Remove(string? folder = null)
    {
        folder ??= DefaultFolder;
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        lock (Gate) Checked[folder] = null;
    }

    /// <summary>
    /// The common part of loading and fetching: the list of lists checked against the anchors (or the keys the pivot lists hand on), then
    /// every national list against the keys it names for that country.
    /// </summary>
    private static TrustListSet Assemble(Func<string, Uri, byte[]> get, Action<string, byte[]>? keep, IReadOnlyList<X509Certificate2> anchors,
        IProgress<string>? progress, CancellationToken cancel, Uri? lotlAddress = null)
    {
        progress?.Report("EU");
        var lotlData = get(LotlFile, lotlAddress ?? Lotl);
        var lotlDocument = XmlSignature.Load(lotlData);
        var lotl = TrustedList.Read(lotlDocument);
        if (lotl.Territory != "EU") throw new TrustListException(Tr.T("Die Gesamtliste der EU ist keine Liste der Listen."));
        var trusted = anchors;
        if (!SignedBy(lotlDocument, trusted))
        {
            // the Commission changed its keys: walk the pivot lists from the oldest, each signed with the key before, each naming the next
            foreach (var (number, address) in Pivots(lotl))
            {
                cancel.ThrowIfCancellationRequested();
                var name = $"eu-lotl-pivot-{number}.xml";
                byte[] data;
                try
                {
                    data = get(name, address);
                }
                catch (TrustListException)
                {
                    continue;
                }
                var document = XmlSignature.Load(data);
                if (!SignedBy(document, trusted)) continue;
                var pivot = TrustedList.Read(document);
                if (pivot.Pointers.FirstOrDefault(p => p.Territory == "EU") is not { Signers.Count: > 0 } self) continue;
                trusted = self.Signers;
                keep?.Invoke(name, data);
            }
            XmlSignature.Verify(lotlDocument, trusted);   // throws with the reason
        }
        keep?.Invoke(LotlFile, lotlData);

        var lists = new List<TrustedList>();
        var failures = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pointer in lotl.Pointers.Where(p => p.Territory is { Length: 2 } && p.Territory != "EU"))
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report(pointer.Territory);
            var name = pointer.Territory + ".xml";
            try
            {
                var data = get(name, pointer.Location);
                var document = XmlSignature.Load(data);
                XmlSignature.Verify(document, pointer.Signers);
                var list = TrustedList.Read(document);
                if (list.Territory != pointer.Territory) throw new TrustListException(Tr.T("Die Liste gehört zu einem anderen Land ({0}).", list.Territory));
                lists.Add(list);
                keep?.Invoke(name, data);
            }
            catch (TrustListException e)
            {
                failures[pointer.Territory] = e.Message;
            }
        }
        return new TrustListSet(lotl, lists, failures);
    }

    private static bool SignedBy(XmlDocument document, IReadOnlyList<X509Certificate2> keys)
    {
        try
        {
            XmlSignature.Verify(document, keys);
            return true;
        }
        catch (TrustListException)
        {
            return false;
        }
    }

    /// <summary>The pivot lists the list of lists names, oldest first — only those published by the Commission next to it.</summary>
    private static IEnumerable<(int Number, Uri Address)> Pivots(TrustedList lotl) =>
        lotl.SchemeUris.Where(u => u.Scheme == "https" && u.Host == Lotl.Host)
            .Select(u => (Match: PivotName.Match(u.AbsolutePath), Address: u)).Where(p => p.Match.Success)
            .Select(p => (int.Parse(p.Match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), p.Address))
            .OrderBy(p => p.Item1);
}
