using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;

namespace PdfKnead.Core.Pdf.Signatures.TrustLists;

/// <summary>A trusted list is unusable: it cannot be read, its signature does not verify, or it is not signed by whom it must be.</summary>
/// <param name="message">What to tell the user (translated).</param>
public sealed class TrustListException(string message) : Exception(message);

/// <summary>
/// Checks the enveloped XML signature of a trusted list (XML Signature 1.1 with XAdES, ETSI TS 119 612, 5.7): the narrow set the lists use —
/// Exclusive C14N, the enveloped-signature transform, SHA-256/384/512, RSA (PKCS#1 v1.5 and PSS) and ECDSA. Anything else is refused rather
/// than guessed.
/// </summary>
/// <remarks>
/// Against signature wrapping, the signature must be a direct child of the document element and carry a reference with URI="" — the whole
/// document without the signature —, every Id a reference points to must exist exactly once, and the content is read afterwards from the
/// same parsed document. The signing certificate must be one of those given (from the list one level up, or the pinned anchors).
/// </remarks>
public static class XmlSignature
{
    internal const string DsigNs = "http://www.w3.org/2000/09/xmldsig#";
    private const string Enveloped = "http://www.w3.org/2000/09/xmldsig#enveloped-signature";

    /// <summary>Parses a document safely: no DTD, no external resources, whitespace kept (it is signed), at most 128 M characters.</summary>
    /// <param name="data">The file.</param>
    /// <returns>The document.</returns>
    /// <exception cref="TrustListException">It is not well-formed XML or uses a DTD.</exception>
    /// <summary>The deepest nesting of a list accepted (as for XMP and the invoice XML).</summary>
    internal const int MaxDepth = 64;

    public static XmlDocument Load(byte[] data)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 128L << 20,
            IgnoreComments = false,
        };
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        try
        {
            // the depth first, without building anything: a list comes from the network, and the canonicalization of the signature walks
            // the tree recursively — some thousands of levels would overflow the stack, which no handler catches (docs/FEHLERGEDAECHTNIS.md,
            // No. 866, open there; No. 869). Real lists stay below 20 levels.
            using (var scan = XmlReader.Create(new MemoryStream(data, writable: false), settings))
                while (scan.Read())
                    if (scan.Depth > MaxDepth) throw new TrustListException(Tr.T("Die Liste ist tiefer verschachtelt als {0} Ebenen.", MaxDepth));
            using var reader = XmlReader.Create(new MemoryStream(data, writable: false), settings);
            document.Load(reader);
        }
        catch (XmlException e)
        {
            throw new TrustListException(Tr.T("Die Liste ist keine gültige XML-Datei: {0}", e.Message));
        }
        return document.DocumentElement is null ? throw new TrustListException(Tr.T("Die Liste ist leer.")) : document;
    }

    /// <summary>Verifies the enveloped signature of a document.</summary>
    /// <param name="document">The parsed document (<see cref="Load"/>).</param>
    /// <param name="allowed">The certificates that may have signed it.</param>
    /// <returns>The certificate that signed it (one of <paramref name="allowed"/>).</returns>
    /// <exception cref="TrustListException">The signature is missing, uses something not supported, does not verify, or is by someone else.</exception>
    public static X509Certificate2 Verify(XmlDocument document, IReadOnlyCollection<X509Certificate2> allowed)
    {
        var root = document.DocumentElement!;
        var signatures = Children(root, "Signature").ToList();
        if (signatures.Count != 1) throw new TrustListException(Tr.T("Die Liste trägt keine (oder mehr als eine) Signatur."));
        var signature = signatures[0];
        var signedInfo = Single(signature, "SignedInfo");
        if (Single(signedInfo, "CanonicalizationMethod").GetAttribute("Algorithm") != ExclusiveCanonicalizer.Algorithm)
            throw Unsupported();
        var method = Single(signedInfo, "SignatureMethod").GetAttribute("Algorithm");

        try
        {
            var references = Children(signedInfo, "Reference").ToList();
            if (references.Count is 0 or > 8) throw Unsupported();
            var whole = false;
            foreach (var reference in references)
            {
                var uri = reference.GetAttribute("URI");
                var transforms = reference.SelectNodes("*[local-name()='Transforms' and namespace-uri()='" + DsigNs + "']/*") is { } list
                    ? list.Cast<XmlElement>().Select(t => t.GetAttribute("Algorithm")).ToList() : [];
                byte[] content;
                if (uri.Length == 0 && reference.HasAttribute("URI"))
                {
                    if (transforms is not [Enveloped, ExclusiveCanonicalizer.Algorithm]) throw Unsupported();
                    content = ExclusiveCanonicalizer.Canonicalize(document, signature);
                    whole = true;
                }
                else if (uri.StartsWith('#'))
                {
                    if (transforms is not ([] or [ExclusiveCanonicalizer.Algorithm])) throw Unsupported();
                    content = ExclusiveCanonicalizer.Canonicalize(ById(document, uri[1..]));
                }
                else
                {
                    throw Unsupported();
                }
                var digest = Hash(Single(reference, "DigestMethod").GetAttribute("Algorithm")) ?? throw Unsupported();
                var expected = Convert.FromBase64String(Single(reference, "DigestValue").InnerText.Trim());
                if (!CryptographicOperations.FixedTimeEquals(Digest(digest, content), expected))
                    throw new TrustListException(Tr.T("Die Liste wurde nach dem Signieren verändert."));
            }
            if (!whole) throw new TrustListException(Tr.T("Die Signatur deckt nicht die ganze Liste ab."));

            var value = Convert.FromBase64String(Single(signature, "SignatureValue").InnerText.Trim());
            var signed = ExclusiveCanonicalizer.Canonicalize(signedInfo);
            // the certificate named in the signature first, then the others allowed
            var named = signature.SelectSingleNode(".//*[local-name()='X509Certificate' and namespace-uri()='" + DsigNs + "']")?.InnerText;
            var namedRaw = named is null ? null : Convert.FromBase64String(named.Trim());
            foreach (var certificate in allowed.OrderBy(c => namedRaw is not null && c.RawData.AsSpan().SequenceEqual(namedRaw) ? 0 : 1))
                if (Verifies(method, signed, value, certificate)) return certificate;
            throw new TrustListException(Tr.T("Die Liste ist nicht von einer berechtigten Stelle signiert."));
        }
        catch (FormatException)
        {
            throw new TrustListException(Tr.T("Die Signatur der Liste ist beschädigt."));
        }
        catch (XmlException e)
        {
            throw new TrustListException(e.Message);
        }
    }

    private static TrustListException Unsupported() => new(Tr.T("Die Signatur der Liste verwendet ein nicht unterstütztes Verfahren."));

    internal static IEnumerable<XmlElement> Children(XmlElement parent, string name, string ns = DsigNs) =>
        parent.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == name && e.NamespaceURI == ns);

    private static XmlElement Single(XmlElement parent, string name) =>
        Children(parent, name).ToList() is [var one] ? one : throw new TrustListException(Tr.T("Die Signatur der Liste ist unvollständig ({0}).", name));

    /// <summary>The one element with this Id; two with the same Id are refused (a wrapped copy).</summary>
    private static XmlElement ById(XmlDocument document, string id)
    {
        XmlElement? found = null;
        foreach (XmlElement element in document.GetElementsByTagName("*"))
        {
            if (element.GetAttribute("Id") != id && element.GetAttribute("ID") != id && element.GetAttribute("id") != id) continue;
            if (found is not null) throw new TrustListException(Tr.T("Die Liste enthält eine Kennung doppelt."));
            found = element;
        }
        return found ?? throw new TrustListException(Tr.T("Die Signatur der Liste verweist auf einen fehlenden Teil."));
    }

    private static HashAlgorithmName? Hash(string algorithm) => algorithm switch
    {
        "http://www.w3.org/2001/04/xmlenc#sha256" => HashAlgorithmName.SHA256,
        "http://www.w3.org/2001/04/xmldsig-more#sha384" => HashAlgorithmName.SHA384,
        "http://www.w3.org/2001/04/xmlenc#sha512" => HashAlgorithmName.SHA512,
        _ => null,
    };

    private static byte[] Digest(HashAlgorithmName hash, byte[] data) =>
        hash == HashAlgorithmName.SHA256 ? SHA256.HashData(data) : hash == HashAlgorithmName.SHA384 ? SHA384.HashData(data) : SHA512.HashData(data);

    /// <summary>Verifies the signature value over the canonical SignedInfo (RFC 6931 identifiers; ECDSA as r‖s, XML Signature 1.1, 6.4.3).</summary>
    private static bool Verifies(string method, byte[] signed, byte[] value, X509Certificate2 certificate)
    {
        (HashAlgorithmName Hash, string Kind)? scheme = method switch
        {
            "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256" => (HashAlgorithmName.SHA256, "rsa"),
            "http://www.w3.org/2001/04/xmldsig-more#rsa-sha384" => (HashAlgorithmName.SHA384, "rsa"),
            "http://www.w3.org/2001/04/xmldsig-more#rsa-sha512" => (HashAlgorithmName.SHA512, "rsa"),
            "http://www.w3.org/2007/05/xmldsig-more#sha256-rsa-MGF1" => (HashAlgorithmName.SHA256, "pss"),
            "http://www.w3.org/2007/05/xmldsig-more#sha384-rsa-MGF1" => (HashAlgorithmName.SHA384, "pss"),
            "http://www.w3.org/2007/05/xmldsig-more#sha512-rsa-MGF1" => (HashAlgorithmName.SHA512, "pss"),
            "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256" => (HashAlgorithmName.SHA256, "ecdsa"),
            "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha384" => (HashAlgorithmName.SHA384, "ecdsa"),
            "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha512" => (HashAlgorithmName.SHA512, "ecdsa"),
            _ => null,
        };
        if (scheme is not { } s) throw Unsupported();
        try
        {
            if (s.Kind == "ecdsa")
            {
                using var ecdsa = certificate.GetECDsaPublicKey();
                return ecdsa is not null && ecdsa.VerifyData(signed, value, s.Hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            using var rsa = CertificateKeys.Rsa(certificate);
            // PSS with MGF1 of the same hash and a salt as long as the hash (RFC 6931, 2.3.10)
            return rsa is not null && rsa.VerifyData(signed, value, s.Hash, s.Kind == "pss" ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
