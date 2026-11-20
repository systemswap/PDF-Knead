using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Core.Pdf.Signatures;

/// <summary>
/// Long-term validation (PAdES B-LT, ETSI EN 319 142-1, 5.4; ISO 32000-2:2020, 12.8.4.3): after signing, the certificates of the signer and
/// of the time-stamping authority together with their revocation status (<see cref="RevocationInfo"/>) are appended as the document
/// security store /DSS, so the signature can still be checked when the certification authority no longer answers. The /DSS is a new object
/// each time, merged with the one the file had; the catalog names it — a change every signature allows (12.8.2.2).
/// </summary>
public static class LongTermValidation
{
    private static readonly PdfName Dss = PdfName.Get("DSS"), Certs = PdfName.Get("Certs"), Ocsps = PdfName.Get("OCSPs"), Crls = PdfName.Get("CRLs");

    private const int MaxEntries = 10_000;

    /// <summary>The chains a CMS signature depends on: its signer, and the time-stamping authority of its signature time stamp.</summary>
    /// <param name="cms">The DER-encoded CMS block.</param>
    /// <returns>End certificates with the certificates that came with them.</returns>
    public static List<(X509Certificate2 End, X509Certificate2Collection Extra)> ChainsOf(byte[] cms)
    {
        var chains = new List<(X509Certificate2, X509Certificate2Collection)>();
        var signed = new SignedCms();
        signed.Decode(cms);
        if (signed.SignerInfos.Count == 0) return chains;
        var signer = signed.SignerInfos[0];
        if (signer.Certificate is { } certificate) chains.Add((certificate, signed.Certificates));
        foreach (var attribute in signer.UnsignedAttributes)
        {
            if (attribute.Oid.Value != SignatureTimestamps.Oid || attribute.Values.Count == 0) continue;
            if (!Rfc3161TimestampToken.TryDecode(attribute.Values[0].RawData, out var token, out _)) continue;
            var tokenCms = token.AsSignedCms();
            if (tokenCms.SignerInfos.Count > 0 && tokenCms.SignerInfos[0].Certificate is { } authority) chains.Add((authority, tokenCms.Certificates));
        }
        return chains;
    }

    /// <summary>The certificates a CMS block carries itself (SignedData.certificates, DER), not those of its time-stamp tokens.</summary>
    /// <param name="cms">The DER-encoded CMS block.</param>
    internal static List<byte[]> CertificatesOf(byte[] cms)
    {
        var signed = new SignedCms();
        signed.Decode(cms);
        return [.. signed.Certificates.Select(c => c.RawData)];
    }

    /// <summary>
    /// Writes the update that adds the validation data to a document: each item once, also against what the file's /DSS already holds.
    /// </summary>
    /// <param name="doc">The signed document; it must take an incremental update.</param>
    /// <param name="found">The validation data.</param>
    /// <returns>The bytes to append to the document's bytes.</returns>
    /// <exception cref="NotAppendableException">The file does not take an update.</exception>
    public static byte[] Update(PdfDocument doc, RevocationInfo.Found found)
    {
        var update = new IncrementalUpdate(doc);
        var old = doc.Catalog?.GetDictionary(Dss);
        var dss = new PdfDictionary();
        dss.Set(PdfName.Type, PdfName.Get("DSS"));
        Merge(doc, dss, Certs, old, found.Certificates, update);
        Merge(doc, dss, Ocsps, old, found.Ocsps, update);
        Merge(doc, dss, Crls, old, found.Crls, update);
        // further entries of an existing store (/VRI and the like) stay as they were
        if (old is not null)
            foreach (var (key, value) in old.Entries)
                if (!dss.ContainsKey(key)) dss.Set(key, value);
        var catalog = IncrementalParts.Copy(doc.Catalog!);
        catalog.Set(Dss, update.Add(dss));
        PadesExtension.Declare(catalog, doc, PadesExtension.LongTerm);   // the security store before PDF 2.0
        update.Replace(((PdfReference)doc.Trailer.GetRaw(PdfName.Root)!).Id, catalog);
        using var output = new MemoryStream();
        update.WriteUpdateTo(output);
        return output.ToArray();
    }

    /// <summary>One list of the store: the existing streams by reference, then new ones for data not yet in it.</summary>
    private static void Merge(PdfDocument doc, PdfDictionary dss, PdfName key, PdfDictionary? old, List<byte[]> items, IncrementalUpdate update)
    {
        var list = new PdfArray();
        var known = new HashSet<string>(StringComparer.Ordinal);
        if (old?.GetArray(key) is { } existing)
            for (var i = 0; i < Math.Min(existing.Count, MaxEntries); i++)
            {
                if (existing[i] is not PdfReference reference) continue;
                list.Add(reference);
                if (existing.Get(i) is PdfStream stream && TryData(doc, stream) is { } data) known.Add(Convert.ToHexString(SHA256.HashData(data)));
            }
        foreach (var item in items)
        {
            if (!known.Add(Convert.ToHexString(SHA256.HashData(item)))) continue;
            list.Add(update.Add(new PdfStream(new PdfDictionary(), item)));
        }
        if (list.Count > 0) dss.Set(key, list);
    }

    private static byte[]? TryData(PdfDocument doc, PdfStream stream)
    {
        try
        {
            return doc.GetStreamData(stream);
        }
        catch (Exception e) when (e is PdfException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The validation data a document carries in its /DSS: certificates, OCSP responses and CRLs, each up to a limit.</summary>
    /// <param name="doc">The document.</param>
    /// <returns>The data; null if it has no /DSS.</returns>
    public static RevocationInfo.Found? Read(PdfDocument doc)
    {
        if (doc.Catalog?.GetDictionary(Dss) is not { } dss) return null;
        return new RevocationInfo.Found(Items(doc, dss, Certs), Items(doc, dss, Ocsps), Items(doc, dss, Crls), []);
    }

    private static List<byte[]> Items(PdfDocument doc, PdfDictionary dss, PdfName key)
    {
        var items = new List<byte[]>();
        if (dss.GetArray(key) is not { } list) return items;
        long total = 0;
        for (var i = 0; i < Math.Min(list.Count, MaxRead); i++)
            if (list.Get(i) is PdfStream stream && TryData(doc, stream) is { } data && (total += data.Length) <= MaxReadBytes) items.Add(data);
        return items;
    }

    /// <summary>Limits when reading a store (prepared files): entries per list and bytes per list.</summary>
    private const int MaxRead = 500, MaxReadBytes = 64 << 20;
}
