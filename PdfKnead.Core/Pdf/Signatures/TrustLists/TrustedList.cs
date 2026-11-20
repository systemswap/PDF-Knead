using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Xml;

namespace PdfKnead.Core.Pdf.Signatures.TrustLists;

/// <summary>A pointer of the EU list of trusted lists to a national list (ETSI TS 119 612, 5.3.13).</summary>
/// <param name="Territory">Country code (ISO 3166-1, „EL“ for Greece, „UK“, „EU“ for the list itself).</param>
/// <param name="Location">Where the list is published.</param>
/// <param name="Signers">The certificates that may sign it.</param>
public sealed record TrustListPointer(string Territory, Uri Location, IReadOnlyList<X509Certificate2> Signers);

/// <summary>One period of a trust service: from when it had which status.</summary>
/// <param name="From">Start of the status.</param>
/// <param name="Status">Status URI (ETSI TS 119 612, D.5).</param>
/// <param name="ForSignatures">For a CA/QC service: whether it issues certificates for electronic signatures (not only seals or websites).</param>
public sealed record TrustServicePeriod(DateTimeOffset From, string Status, bool ForSignatures)
{
    /// <summary>What its certificates are for, as the list marks it (AdditionalServiceInformation URIs, ETSI TS 119 612, 5.5.9.4); empty if unmarked.</summary>
    public IReadOnlySet<string> Uses { get; init; } = new HashSet<string>();

    /// <summary>The qualifiers with their criteria (extension „Qualifications“, ETSI TS 119 612, 5.5.9.2).</summary>
    public IReadOnlyList<ListQualification> Qualifications { get; init; } = [];

    /// <summary>Whether the period allows certificates of this kind (an unmarked period from before the distinction: signatures).</summary>
    /// <param name="kind">The kind; null (open) passes.</param>
    public bool Allows(SignatureKind? kind) => kind switch
    {
        null => true,
        SignatureKind.Seal => Uses.Contains(TrustedList.ForSealsUri),
        SignatureKind.WebCertificate => Uses.Contains(TrustedList.ForWebsitesUri),
        _ => ForSignatures,
    };
}

/// <summary>A trust service of a national list that matters for signatures: issuing qualified certificates or qualified time stamps.</summary>
/// <param name="Territory">Country of the list.</param>
/// <param name="Provider">Name of the trust service provider.</param>
/// <param name="Name">Name of the service.</param>
/// <param name="Type">Service type URI.</param>
/// <param name="Certificates">The certificates the service is identified by (current and earlier).</param>
/// <param name="Periods">Its status over time, oldest first.</param>
public sealed record TrustService(string Territory, string Provider, string Name, string Type, IReadOnlyList<X509Certificate2> Certificates,
    IReadOnlyList<TrustServicePeriod> Periods)
{
    /// <summary>The period in force at a time; null if the service was not listed yet.</summary>
    /// <param name="at">The time.</param>
    /// <returns>The period.</returns>
    public TrustServicePeriod? At(DateTimeOffset at) => Periods.LastOrDefault(p => p.From <= at);
}

/// <summary>
/// A trusted list as published (ETSI TS 119 612 v2.x, the format of the EU trusted lists under Implementing Decision (EU) 2015/1505): the
/// EU list of lists with its pointers, or a national list with the services that issue qualified certificates (CA/QC) and qualified time
/// stamps (TSA/QTST). Read from a document whose signature is checked first (<see cref="XmlSignature.Verify"/>); other services are skipped.
/// </summary>
public sealed class TrustedList
{
    internal const string Ns = "http://uri.etsi.org/02231/v2#";
    private const string AdditionalTypesNs = "http://uri.etsi.org/02231/v2/additionaltypes#";

    /// <summary>Service type: issuing qualified certificates.</summary>
    public const string QualifiedCa = "http://uri.etsi.org/TrstSvc/Svctype/CA/QC";

    /// <summary>Service type: qualified time stamps.</summary>
    public const string QualifiedTimestamps = "http://uri.etsi.org/TrstSvc/Svctype/TSA/QTST";

    internal const string ForSignaturesUri = "http://uri.etsi.org/TrstSvc/TrustedList/SvcInfoExt/ForeSignatures";
    internal const string ForSealsUri = "http://uri.etsi.org/TrstSvc/TrustedList/SvcInfoExt/ForeSeals";
    internal const string ForWebsitesUri = "http://uri.etsi.org/TrstSvc/TrustedList/SvcInfoExt/ForWebSiteAuthentication";

    /// <summary>At most this many qualification entries are read per period.</summary>
    private const int MaxQualifications = 32;

    /// <summary>Limits against prepared lists.</summary>
    private const int MaxProviders = 2_000, MaxServices = 200, MaxHistory = 100, MaxIdentities = 50, MaxPointers = 200;

    private TrustedList(string territory, int sequence, DateTimeOffset? issued, DateTimeOffset? nextUpdate, IReadOnlyList<TrustListPointer> pointers,
        IReadOnlyList<TrustService> services, IReadOnlyList<Uri> schemeUris)
    {
        Territory = territory;
        Sequence = sequence;
        Issued = issued;
        NextUpdate = nextUpdate;
        Pointers = pointers;
        Services = services;
        SchemeUris = schemeUris;
    }

    /// <summary>Country code of the list („EU“ for the list of lists).</summary>
    public string Territory { get; }

    /// <summary>Sequence number (grows with each issue).</summary>
    public int Sequence { get; }

    /// <summary>When it was issued.</summary>
    public DateTimeOffset? Issued { get; }

    /// <summary>When the next issue is due at the latest.</summary>
    public DateTimeOffset? NextUpdate { get; }

    /// <summary>Pointers to other lists (only in the list of lists), XML lists only.</summary>
    public IReadOnlyList<TrustListPointer> Pointers { get; }

    /// <summary>Services issuing qualified certificates or qualified time stamps (only in national lists).</summary>
    public IReadOnlyList<TrustService> Services { get; }

    /// <summary>The scheme information URIs (in the list of lists: the Official Journal publication and the pivot lists).</summary>
    public IReadOnlyList<Uri> SchemeUris { get; }

    /// <summary>Reads a list from a parsed document.</summary>
    /// <param name="document">The document (its signature checked).</param>
    /// <returns>The list.</returns>
    /// <exception cref="TrustListException">It is not a trusted list.</exception>
    public static TrustedList Read(XmlDocument document)
    {
        var root = document.DocumentElement!;
        if (root.LocalName != "TrustServiceStatusList" || root.NamespaceURI != Ns) throw new TrustListException(Tr.T("Die Datei ist keine Vertrauensliste."));
        var scheme = Child(root, "SchemeInformation") ?? throw new TrustListException(Tr.T("Die Datei ist keine Vertrauensliste."));
        var territory = Text(Child(scheme, "SchemeTerritory")) ?? "";
        var sequence = int.TryParse(Text(Child(scheme, "TSLSequenceNumber")), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        var issued = Time(Text(Child(scheme, "ListIssueDateTime")));
        var next = Time(Text(Child(Child(scheme, "NextUpdate"), "dateTime")));
        var schemeUris = Elements(Child(scheme, "SchemeInformationURI"), "URI").Select(e => Uri.TryCreate(Text(e), UriKind.Absolute, out var u) ? u : null)
            .OfType<Uri>().ToList();

        var pointers = new List<TrustListPointer>();
        foreach (var pointer in Elements(Child(scheme, "PointersToOtherTSL"), "OtherTSLPointer").Take(MaxPointers))
        {
            var info = Elements(Child(pointer, "AdditionalInformation"), "OtherInformation").ToList();
            var mime = info.SelectMany(i => i.ChildNodes.OfType<XmlElement>()).FirstOrDefault(e => e.LocalName == "MimeType" && e.NamespaceURI == AdditionalTypesNs);
            if (Text(mime) != "application/vnd.etsi.tsl+xml") continue;   // the PDF editions of the lists
            var where = Text(info.SelectMany(i => i.ChildNodes.OfType<XmlElement>()).FirstOrDefault(e => e.LocalName == "SchemeTerritory"));
            if (!Uri.TryCreate(Text(Child(pointer, "TSLLocation")), UriKind.Absolute, out var location) || location.Scheme is not ("http" or "https")) continue;
            var signers = Elements(Child(pointer, "ServiceDigitalIdentities"), "ServiceDigitalIdentity").SelectMany(Certificates).ToList();
            pointers.Add(new TrustListPointer(where ?? "", location, signers));
        }

        var services = new List<TrustService>();
        foreach (var provider in Elements(Child(root, "TrustServiceProviderList"), "TrustServiceProvider").Take(MaxProviders))
        {
            var providerName = Name(Child(Child(provider, "TSPInformation"), "TSPName"));
            foreach (var service in Elements(Child(provider, "TSPServices"), "TSPService").Take(MaxServices))
            {
                var info = Child(service, "ServiceInformation");
                var type = Text(Child(info, "ServiceTypeIdentifier"));
                if (info is null || type is not (QualifiedCa or QualifiedTimestamps)) continue;
                var certificates = Certificates(Child(info, "ServiceDigitalIdentity")).ToList();
                var periods = new List<TrustServicePeriod>();
                if (Period(info) is { } current) periods.Add(current);
                foreach (var history in Elements(Child(service, "ServiceHistory"), "ServiceHistoryInstance").Take(MaxHistory))
                {
                    if (Text(Child(history, "ServiceTypeIdentifier")) != type) continue;
                    certificates.AddRange(Certificates(Child(history, "ServiceDigitalIdentity")));
                    if (Period(history) is { } earlier) periods.Add(earlier);
                }
                periods.Sort((a, b) => a.From.CompareTo(b.From));
                var distinct = certificates.DistinctBy(c => c.Thumbprint).ToList();
                if (distinct.Count > 0 && periods.Count > 0) services.Add(new TrustService(territory, providerName, Name(Child(info, "ServiceName")), type, distinct, periods));
            }
        }
        return new TrustedList(territory, sequence, issued, next, pointers, services, schemeUris);
    }

    /// <summary>Status and start of a service (current or historical), with what its certificates are for.</summary>
    private static TrustServicePeriod? Period(XmlElement information)
    {
        var status = Text(Child(information, "ServiceStatus"));
        var from = Time(Text(Child(information, "StatusStartingTime")));
        if (status is null || from is not { } start) return null;
        // ETSI TS 119 612, 5.5.9.4: a CA/QC service names what its certificates are for; without any of these it was before the distinction
        var extensions = Elements(Child(information, "ServiceInformationExtensions"), "Extension").SelectMany(e => e.ChildNodes.OfType<XmlElement>()).ToList();
        var uses = extensions.Where(e => e.LocalName == "AdditionalServiceInformation")
            .Select(e => Text(Child(e, "URI"))).OfType<string>().ToList();
        var marked = uses.Any(u => u is ForSignaturesUri or ForSealsUri or ForWebsitesUri);
        // the qualifiers (the namespace of the element differs between editions: read by local name)
        var qualifications = extensions.Where(e => e.LocalName == "Qualifications")
            .SelectMany(q => ListQualification.Children(q, "QualificationElement")).Take(MaxQualifications).Select(ListQualification.Read).ToList();
        return new TrustServicePeriod(start, status, !marked || uses.Contains(ForSignaturesUri))
        {
            Uses = uses.Where(u => u is ForSignaturesUri or ForSealsUri or ForWebsitesUri).ToHashSet(StringComparer.Ordinal),
            Qualifications = qualifications,
        };
    }

    /// <summary>The X.509 certificates of a digital identity (subject names and key identifiers alone are not used).</summary>
    private static IEnumerable<X509Certificate2> Certificates(XmlElement? identity)
    {
        foreach (var id in Elements(identity, "DigitalId").Take(MaxIdentities))
        {
            if (Text(Child(id, "X509Certificate")) is not { } text) continue;
            X509Certificate2? certificate = null;
            try
            {
                certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(text));
            }
            catch (Exception e) when (e is FormatException or System.Security.Cryptography.CryptographicException)
            {
                // an unreadable entry: left out
            }
            if (certificate is not null) yield return certificate;
        }
    }

    /// <summary>A multilingual name: English if there is one, else the first.</summary>
    private static string Name(XmlElement? names)
    {
        var all = Elements(names, "Name").ToList();
        return Text(all.FirstOrDefault(e => e.GetAttribute("xml:lang") == "en") ?? all.FirstOrDefault()) ?? "";
    }

    private static DateTimeOffset? Time(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;

    private static XmlElement? Child(XmlElement? parent, string name) =>
        parent?.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == name && e.NamespaceURI == Ns);

    private static IEnumerable<XmlElement> Elements(XmlElement? parent, string name) =>
        parent is null ? [] : parent.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == name && e.NamespaceURI == Ns);

    private static string? Text(XmlElement? element) => element?.InnerText.Trim() is { Length: > 0 } text ? text : null;
}
