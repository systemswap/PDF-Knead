using System.Security.Cryptography.X509Certificates;
using System.Xml;

namespace PdfKnead.Core.Pdf.Signatures.TrustLists;

/// <summary>
/// One entry of the extension „Qualifications“ of a trust service (ETSI TS 119 612, 5.5.9.2): the qualifiers that apply to the certificates
/// its criteria list selects. Read by local name (the namespace changed between the editions of the format). Evaluated only where every
/// criterion is understood — key usage, policy sets, nested lists (docs/ABLAUFPLAN.md, 2.6, step 4: cautious; docs/FEHLERGEDAECHTNIS.md,
/// No. 484).
/// </summary>
/// <param name="Criteria">Which certificates it applies to.</param>
/// <param name="Qualifiers">The qualifier URIs.</param>
public sealed record ListQualification(QualifierCriteria Criteria, IReadOnlyList<string> Qualifiers)
{
    /// <summary>The URIs of the qualifiers (ETSI TS 119 612, 5.5.9.2.3; TS 119 615).</summary>
    public const string Prefix = "http://uri.etsi.org/TrstSvc/TrustedList/SvcInfoExt/";

    private const int MaxQualifiers = 16;

    /// <summary>Reads a QualificationElement.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The entry; one with unknown criteria never applies.</returns>
    internal static ListQualification Read(XmlElement element)
    {
        var qualifiers = Children(element, "Qualifiers").SelectMany(q => Children(q, "Qualifier"))
            .Select(q => q.GetAttribute("uri").Trim()).Where(u => u.Length > 0).Take(MaxQualifiers).ToList();
        var criteria = Children(element, "CriteriaList").FirstOrDefault() is { } list ? QualifierCriteria.Read(list, 0) : QualifierCriteria.Unreadable;
        return new ListQualification(criteria, qualifiers);
    }

    internal static IEnumerable<XmlElement> Children(XmlElement parent, string localName) =>
        parent.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == localName);
}

/// <summary>
/// A criteria list (ETSI TS 119 612, 5.5.9.2.2): <c>assert</c> all, atLeastOne or none over key usage (each bit with its value), policy sets
/// (all policies of a set in the certificate) and nested lists. Anything else (other criteria, an unknown assert) makes it unreadable.
/// </summary>
public sealed record QualifierCriteria(string Assert, IReadOnlyList<IReadOnlyDictionary<X509KeyUsageFlags, bool>> KeyUsages,
    IReadOnlyList<IReadOnlyList<string>> PolicySets, IReadOnlyList<QualifierCriteria> Nested, bool Readable)
{
    /// <summary>A list that is not understood: never applies.</summary>
    public static readonly QualifierCriteria Unreadable = new("all", [], [], [], false);

    private const int MaxDepth = 8, MaxItems = 32;

    private static readonly Dictionary<string, X509KeyUsageFlags> Bits = new(StringComparer.Ordinal)
    {
        ["digitalSignature"] = X509KeyUsageFlags.DigitalSignature, ["nonRepudiation"] = X509KeyUsageFlags.NonRepudiation,
        ["contentCommitment"] = X509KeyUsageFlags.NonRepudiation, ["keyEncipherment"] = X509KeyUsageFlags.KeyEncipherment,
        ["dataEncipherment"] = X509KeyUsageFlags.DataEncipherment, ["keyAgreement"] = X509KeyUsageFlags.KeyAgreement,
        ["keyCertSign"] = X509KeyUsageFlags.KeyCertSign, ["crlSign"] = X509KeyUsageFlags.CrlSign,
        ["encipherOnly"] = X509KeyUsageFlags.EncipherOnly, ["decipherOnly"] = X509KeyUsageFlags.DecipherOnly,
    };

    internal static QualifierCriteria Read(XmlElement list, int depth)
    {
        var assert = list.GetAttribute("assert") is { Length: > 0 } a ? a : "all";
        if (depth > MaxDepth || assert is not ("all" or "atLeastOne" or "none")) return Unreadable;
        var keyUsages = new List<IReadOnlyDictionary<X509KeyUsageFlags, bool>>();
        var policySets = new List<IReadOnlyList<string>>();
        var nested = new List<QualifierCriteria>();
        foreach (var child in list.ChildNodes.OfType<XmlElement>().Take(MaxItems))
        {
            switch (child.LocalName)
            {
                case "KeyUsage":
                    var bits = new Dictionary<X509KeyUsageFlags, bool>();
                    foreach (var bit in ListQualification.Children(child, "KeyUsageBit"))
                    {
                        if (!Bits.TryGetValue(bit.GetAttribute("name"), out var flag) || !bool.TryParse(bit.InnerText.Trim(), out var value)) return Unreadable;
                        bits[flag] = value;
                    }
                    keyUsages.Add(bits);
                    break;
                case "PolicySet":
                    var policies = ListQualification.Children(child, "PolicyIdentifier")
                        .Select(p => ListQualification.Children(p, "Identifier").FirstOrDefault()?.InnerText.Trim() ?? "")
                        .Select(id => id.StartsWith("urn:oid:", StringComparison.OrdinalIgnoreCase) ? id[8..] : id).ToList();
                    if (policies.Count == 0 || policies.Any(p => p.Length == 0)) return Unreadable;
                    policySets.Add(policies);
                    break;
                case "CriteriaList":
                    var inner = Read(child, depth + 1);
                    if (!inner.Readable) return Unreadable;
                    nested.Add(inner);
                    break;
                case "Description":
                    break;
                default:   // otherCriteriaList (subject attributes, purposes, statements …) and anything else: not evaluated
                    return Unreadable;
            }
        }
        return new QualifierCriteria(assert, keyUsages, policySets, nested, true);
    }

    /// <summary>Whether the criteria select the certificate; null if they are not understood.</summary>
    /// <param name="certificate">The certificate.</param>
    /// <param name="policies">Its certificate policies.</param>
    public bool? Matches(X509Certificate2 certificate, IReadOnlyList<string> policies)
    {
        if (!Readable) return null;
        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault()?.KeyUsages ?? X509KeyUsageFlags.None;
        var results = new List<bool>();
        foreach (var bits in KeyUsages) results.Add(bits.All(b => (usage & b.Key) != 0 == b.Value));
        foreach (var set in PolicySets) results.Add(set.All(policies.Contains));
        foreach (var inner in Nested)
        {
            if (inner.Matches(certificate, policies) is not { } result) return null;
            results.Add(result);
        }
        return Assert switch
        {
            "atLeastOne" => results.Any(r => r),
            "none" => !results.Any(r => r),
            _ => results.All(r => r),
        };
    }
}

/// <summary>
/// What the EU trusted lists say about a certificate, at its issuance and at the time of signing (ETSI TS 119 615; EN 319 102-1, 5.2.4):
/// qualified or not, of which kind, on a qualified device or not — the list's qualifiers taking precedence over the certificate's own
/// statements (docs/FEHLERGEDAECHTNIS.md, No. 484).
/// </summary>
/// <param name="Qualified">A qualified certificate by the lists at both times.</param>
/// <param name="Kind">The kind; null if open (the lists or the certificate name none, or two different ones).</param>
/// <param name="Qscd">The device; null if the lists say nothing beyond the certificate.</param>
/// <param name="Remote">The key is managed by the provider on behalf of the signer (QCQSCDManagedOnBehalf).</param>
/// <param name="Service">The service found at the time of signing.</param>
/// <param name="Reason">Why not qualified, localised; null if qualified.</param>
public sealed record ListVerdict(bool Qualified, SignatureKind? Kind, QscdState? Qscd, bool Remote, TrustListMatch? Service, string? Reason);
