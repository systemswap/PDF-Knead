using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Signatures;

/// <summary>Signing refused because the prepared field requires something PDF Knead cannot meet (its seed values, ISO 32000-2:2020, Table 237).</summary>
public sealed class SeedValueException(string message) : PdfException(message);

/// <summary>What PDF Knead would write when signing, to be held against a field's seed values.</summary>
/// <param name="Filter">The signature handler (/Filter).</param>
/// <param name="SubFilter">The method (/SubFilter).</param>
/// <param name="Digest">The digest, as /DigestMethod names it (SHA256 …).</param>
/// <param name="TimeStamp">Whether a time-stamping service is set up and used.</param>
/// <param name="Certify">The certification asked for; null: an approval signature.</param>
/// <param name="CanCertify">Whether the document can still be certified (it has no signature yet).</param>
/// <param name="Certificate">The certificate signing; null: not known yet (when placing).</param>
public sealed record SigningPlan(string Filter, string SubFilter, string Digest, bool TimeStamp, CertificationLevel? Certify, bool CanCertify,
    X509Certificate2? Certificate)
{
    /// <summary>What PdfSigner writes: Adobe.PPKLite, ETSI.CAdES.detached, SHA-256.</summary>
    public static SigningPlan Of(bool timeStamp, CertificationLevel? certify, bool canCertify, X509Certificate2? certificate) =>
        new("Adobe.PPKLite", "ETSI.CAdES.detached", "SHA256", timeStamp, certify, canCertify, certificate);
}

/// <summary>
/// The seed values of a prepared signature field (/SV, ISO 32000-2:2020, Tables 237 and 238): what its author requires or recommends for the
/// signature. Phase 1 (docs/ABLAUFPLAN.md, 2.3): PDF Knead adapts nothing — a required value it cannot meet refuses signing into the field
/// (with the way out: sign beside it), a recommendation it does not follow is a hint. Validators do not enforce seed values (12.8.3.4.4),
/// so the check of signatures leaves them alone. An address of a time-stamping service in the file is never contacted (offline).
/// </summary>
/// <param name="Ff">The required flags (bit 1 Filter, 2 SubFilter, 3 V, 4 Reasons, 5 LegalAttestation, 6 AddRevInfo, 7 DigestMethod, 8 LockDocument, 9 AppearanceFilter).</param>
public sealed record SeedValues(int Ff, string? Filter, IReadOnlyList<string> SubFilters, IReadOnlyList<string> DigestMethods, int? V,
    IReadOnlyList<string> Reasons, IReadOnlyList<string> LegalAttestations, bool AddRevInfo, string? LockDocument, string? AppearanceFilter,
    int? MdpP, string? TimeStampUrl, bool TimeStampRequired, PdfDictionary? Cert)
{
    /// <summary>The highest /V whose entries PDF Knead recognises: all of PDF 2.0 (Table 237: 3).</summary>
    public const int RecognisedVersion = 3;

    private const int MaxEntries = 100;

    private bool Required(int bit) => (Ff & (1 << (bit - 1))) != 0;

    /// <summary>The seed values of a signature field (/SV, not inherited); null if it has none.</summary>
    public static SeedValues? Of(PdfDictionary? field)
    {
        if (field?.Get(PdfName.Get("SV")) is not PdfDictionary sv) return null;
        static List<string> Names(PdfDictionary dict, string key)
        {
            var list = new List<string>();
            if (dict.GetArray(PdfName.Get(key)) is { } array)
                for (var i = 0; i < Math.Min(array.Count, MaxEntries); i++)
                    switch (array.Get(i))
                    {
                        case PdfName name: list.Add(name.Value); break;
                        case PdfString text: list.Add(text.ToText()); break;
                    }
            return list;
        }
        var timeStamp = sv.Get(PdfName.Get("TimeStamp")) as PdfDictionary;
        return new SeedValues(
            (int)Math.Clamp(sv.GetInteger(PdfName.Get("Ff")) ?? 0, 0, int.MaxValue),
            sv.GetName(PdfName.Get("Filter"))?.Value,
            Names(sv, "SubFilter"), Names(sv, "DigestMethod"),
            sv.GetInteger(PdfName.Get("V")) is { } v ? (int)Math.Clamp(v, 0, int.MaxValue) : null,
            Names(sv, "Reasons"), Names(sv, "LegalAttestation"),
            sv.Get(PdfName.Get("AddRevInfo")) is PdfBoolean { Value: true },
            sv.GetName(PdfName.Get("LockDocument"))?.Value,
            sv.GetName(PdfName.Get("AppearanceFilter"))?.Value ?? (sv.Get(PdfName.Get("AppearanceFilter")) as PdfString)?.ToText(),
            (sv.Get(PdfName.Get("MDP")) as PdfDictionary)?.GetInteger(PdfName.Get("P")) is { } p ? (int)Math.Clamp(p, -1, 4) : null,
            (timeStamp?.Get(PdfName.Get("URL")) as PdfString)?.ToText(),
            timeStamp?.GetInteger(PdfName.Get("Ff")) == 1,
            sv.Get(PdfName.Get("Cert")) as PdfDictionary);
    }

    /// <summary>
    /// Holds the plan against the seed values: what cannot be met although required (signing refused) and what is recommended but not
    /// followed (hints), each a short localised sentence.
    /// </summary>
    public (List<string> Refusals, List<string> Hints) Check(SigningPlan plan)
    {
        var refusals = new List<string>();
        var hints = new List<string>();
        void Either(bool required, string text) => (required ? refusals : hints).Add(text);

        if (Filter is { } filter && filter != plan.Filter)
            Either(Required(1), Tr.T("Signaturverfahren „{0}“ (PDF-Knead unterschreibt mit „{1}“)", filter, plan.Filter));
        if (SubFilters.Count > 0 && !SubFilters.Contains(plan.SubFilter))
            Either(Required(2), Tr.T("Signaturformat {0} (PDF-Knead schreibt {1})", string.Join(", ", SubFilters), plan.SubFilter));
        if (V is > RecognisedVersion)
            Either(Required(3), Tr.T("Vorgaben einer neueren Fassung (V {0}), die PDF-Knead nicht kennt", V));
        // a single „.“ means: no /Reason — which PDF Knead writes anyway
        if (Reasons.Count > 0 && !(Reasons is ["."]))
            Either(Required(4), Tr.T("einen der Gründe „{0}“ (PDF-Knead schreibt keinen Grund in die Signatur)", string.Join("“, „", Reasons.Take(5))));
        if (LegalAttestations.Count > 0)
            Either(Required(5), Tr.T("eine rechtliche Erklärung (PDF-Knead schreibt keine)"));
        // the revocation data inside the signature value (CMS) — PDF Knead puts it into the security store, which is not the same
        if (AddRevInfo)
            Either(Required(6), Tr.T("Sperrinformationen im Signaturwert (PDF-Knead legt sie in den Sicherheitsspeicher der Datei)"));
        if (DigestMethods.Count > 0 && !DigestMethods.Any(d => string.Equals(d.Replace("-", ""), plan.Digest, StringComparison.OrdinalIgnoreCase)))
            Either(Required(7), Tr.T("Prüfsummenverfahren {0} (PDF-Knead nimmt {1})", string.Join(", ", DigestMethods), plan.Digest));
        if (LockDocument == "true")
            Either(Required(8), Tr.T("das Dokument beim Unterschreiben zu sperren (das kann PDF-Knead noch nicht)"));
        if (AppearanceFilter is { } look)
            Either(Required(9), Tr.T("das Erscheinungsbild „{0}“ (PDF-Knead zeichnet sein eigenes)", look));
        switch (MdpP)
        {
            case 0 when plan.Certify is not null:
                refusals.Add(Tr.T("eine gewöhnliche Signatur ohne Zertifizierung"));
                break;
            case 1 or 2 or 3 when plan.Certify is not { } certify || (int)certify != MdpP:
                refusals.Add(plan.CanCertify
                    ? Tr.T("eine Zertifizierung mit „danach erlaubt: {0}“ (im Unterschrift-Fenster so wählen)", SignatureTexts.Allowed((CertificationLevel)MdpP!))
                    : Tr.T("eine Zertifizierung — das Dokument hat aber schon Signaturen"));
                break;
        }
        if (TimeStampRequired && !plan.TimeStamp)
            refusals.Add(TimeStampUrl is { } url
                ? Tr.T("einen Zeitstempel (das Formular nennt {0}; PDF-Knead verbindet sich nicht von selbst — Zeitstempeldienst unter „Zertifikat“ einrichten)", url)
                : Tr.T("einen Zeitstempel (Zeitstempeldienst unter „Zertifikat“ einrichten)"));
        else if (TimeStampUrl is { } recommended && !plan.TimeStamp)
            hints.Add(Tr.T("einen Zeitstempel von {0}", recommended));
        if (Cert is { } cert && CertificateCheck(cert, plan.Certificate) is { } certificate) Either(certificate.Required, certificate.Text);
        return (refusals, hints);
    }

    /// <summary>
    /// The certificate seed values (Table 238): bits 1 Subject (the certificate itself), 2 Issuer, 3 OID (certificate policies), 4 SubjectDN,
    /// 6 KeyUsage. Null if the certificate fits or is not known yet; else whether it was required and what was asked.
    /// </summary>
    private static (bool Required, string Text)? CertificateCheck(PdfDictionary cert, X509Certificate2? certificate)
    {
        var ff = (int)Math.Clamp(cert.GetInteger(PdfName.Get("Ff")) ?? 0, 0, int.MaxValue);
        bool Bit(int b) => (ff & (1 << (b - 1))) != 0;
        if (certificate is null) return cert.Count > (cert.ContainsKey(PdfName.Type) ? 1 : 0) ? (false, Tr.T("bestimmte Zertifikate")) : null;
        var unmet = new List<int>();
        List<byte[]> Certificates(string key)
        {
            var list = new List<byte[]>();
            if (cert.GetArray(PdfName.Get(key)) is { } array)
                for (var i = 0; i < Math.Min(array.Count, MaxEntries); i++)
                    if (array.Get(i) is PdfString s) list.Add(s.Bytes);
            return list;
        }
        if (Certificates("Subject") is { Count: > 0 } subjects && !subjects.Any(s => s.AsSpan().SequenceEqual(certificate.RawData))) unmet.Add(1);
        if (Certificates("Issuer") is { Count: > 0 } issuers && !issuers.Any(i => Issued(i, certificate))) unmet.Add(2);
        if (cert.GetArray(PdfName.Get("OID")) is { Count: > 0 } oids)
        {
            var policies = Policies(certificate);
            var any = false;
            for (var i = 0; i < Math.Min(oids.Count, MaxEntries); i++)
                if (oids.Get(i) is PdfString oid && policies.Contains(System.Text.Encoding.ASCII.GetString(oid.Bytes))) any = true;
            if (!any) unmet.Add(3);
        }
        if (cert.GetArray(PdfName.Get("SubjectDN")) is { Count: > 0 } dns && !SubjectFits(dns, certificate)) unmet.Add(4);
        if (cert.GetArray(PdfName.Get("KeyUsage")) is { Count: > 0 } usages && !KeyUsageFits(usages, certificate)) unmet.Add(6);
        if (unmet.Count == 0) return null;
        return (unmet.Any(Bit), Tr.T("ein Zertifikat, das den Vorgaben des Formulars entspricht (Inhaber, Aussteller, Richtlinie oder Verwendung)"));
    }

    /// <summary>The policy identifiers of a certificate (certificatePolicies, RFC 5280, 4.2.1.4); none if it has none or they cannot be read.</summary>
    private static HashSet<string> Policies(X509Certificate2 certificate)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (certificate.Extensions.OfType<X509Extension>().FirstOrDefault(e => e.Oid?.Value == "2.5.29.32") is not { } extension) return found;
        try
        {
            var list = new System.Formats.Asn1.AsnReader(extension.RawData, System.Formats.Asn1.AsnEncodingRules.DER).ReadSequence();
            while (list.HasData && found.Count < MaxEntries) found.Add(list.ReadSequence().ReadObjectIdentifier());
        }
        catch (System.Formats.Asn1.AsnContentException)
        {
        }
        return found;
    }

    private static bool Issued(byte[] issuerDer, X509Certificate2 certificate)
    {
        try
        {
            using var issuer = X509CertificateLoader.LoadCertificate(issuerDer);
            return issuer.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private static readonly Dictionary<string, string> NameOids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CN"] = "2.5.4.3", ["O"] = "2.5.4.10", ["OU"] = "2.5.4.11", ["C"] = "2.5.4.6", ["L"] = "2.5.4.7", ["ST"] = "2.5.4.8",
        ["SN"] = "2.5.4.4", ["GN"] = "2.5.4.42", ["givenName"] = "2.5.4.42", ["serialNumber"] = "2.5.4.5", ["email"] = "1.2.840.113549.1.9.1",
    };

    /// <summary>One of the dictionaries of /SubjectDN fits: each of its attributes is in the certificate's subject (an unknown one never fits).</summary>
    private static bool SubjectFits(PdfArray dns, X509Certificate2 certificate)
    {
        var subject = certificate.SubjectName.EnumerateRelativeDistinguishedNames()
            .Select(r => (Oid: r.GetSingleElementType().Value, Value: r.GetSingleElementValue())).ToList();
        for (var i = 0; i < Math.Min(dns.Count, MaxEntries); i++)
        {
            if (dns.Get(i) is not PdfDictionary dn) continue;
            var fits = dn.Entries.All(e => NameOids.TryGetValue(e.Key.Value, out var oid)
                && dn.Get(e.Key) is PdfString wanted && subject.Any(s => s.Oid == oid && s.Value == wanted.ToText()));
            if (fits) return true;
        }
        return false;
    }

    /// <summary>One of the /KeyUsage strings fits: per bit (digitalSignature, nonRepudiation …) 1 set, 0 clear, X either.</summary>
    private static bool KeyUsageFits(PdfArray usages, X509Certificate2 certificate)
    {
        var flags = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault()?.KeyUsages ?? X509KeyUsageFlags.None;
        X509KeyUsageFlags[] order =
        [
            X509KeyUsageFlags.DigitalSignature, X509KeyUsageFlags.NonRepudiation, X509KeyUsageFlags.KeyEncipherment, X509KeyUsageFlags.DataEncipherment,
            X509KeyUsageFlags.KeyAgreement, X509KeyUsageFlags.KeyCertSign, X509KeyUsageFlags.CrlSign, X509KeyUsageFlags.EncipherOnly, X509KeyUsageFlags.DecipherOnly,
        ];
        for (var i = 0; i < Math.Min(usages.Count, MaxEntries); i++)
        {
            if (usages.Get(i) is not PdfString usage) continue;
            var text = usage.ToText();
            var fits = true;
            for (var b = 0; b < Math.Min(text.Length, order.Length); b++)
                fits &= text[b] switch { '1' => flags.HasFlag(order[b]), '0' => !flags.HasFlag(order[b]), _ => true };
            if (fits) return true;
        }
        return false;
    }

    /// <summary>
    /// What a prepared signature field of a document asks for, said when a signature is placed into it: the fields its /Lock locks after
    /// signing, and its seed values held against the plan (refusals and recommendations). Null if the field has neither or is not found.
    /// </summary>
    public static string? Notice(Document.PdfDocument doc, string fieldName, SigningPlan plan)
    {
        if (doc.Catalog?.GetDictionary(PdfName.Get("AcroForm"))?.GetArray(PdfName.Get("Fields")) is not { } fields) return null;
        var field = Writing.IncrementalParts.Walk(doc, fields).FirstOrDefault(f => f.Name == fieldName).Field;
        if (field is null) return null;
        var lines = new List<string>();
        if (FieldLock.OfField(field) is { } fieldLock) lines.Add(Tr.T("Dieses Feld sperrt nach dem Unterschreiben: {0}.", SignatureTexts.Locked(fieldLock)));
        if (Of(field) is { } seed)
        {
            var (refusals, hints) = seed.Check(plan);
            if (refusals.Count > 0) lines.Add(Refused(refusals));
            if (hints.Count > 0) lines.Add(Tr.T("Das Formular empfiehlt: {0}.", string.Join("; ", hints)));
        }
        return lines.Count == 0 ? null : string.Join(" ", lines);
    }

    /// <summary>The refusals as one message, with the way out.</summary>
    public static string Refused(IEnumerable<string> refusals) =>
        Tr.T("Das Signaturfeld verlangt {0} — das kann PDF-Knead nicht erfüllen. Um trotzdem zu unterschreiben, die Unterschrift außerhalb des Felds platzieren.",
            string.Join("; ", refusals));
}
