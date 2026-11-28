using System.Globalization;
using System.Net;
using System.Text;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Security;

namespace PdfKnead.Core.Pdf.Signatures;

/// <summary>
/// The validation report of a file's signatures, as text or HTML (docs/ABLAUFPLAN.md, 9.3.5, step 4; ETSI EN 319 102-1, 5.1.3; the structure
/// after ETSI TS 119 102-2; BSI TR-03125, Annex VR): per signature the result (verdict, main indication and sub-indication, process, time of
/// the check, „applies to“ with its source), the signature (field, page, method, revision, later changes), the signer (name, kind and level,
/// purpose), the certificate and its chain (issuer, validity, fingerprint, anchor, a broken rule), the time stamp, the long term (revocation
/// status, PAdES level, renew by), the notes; then the implicit constraints PDF Knead checks by (I-13), the checker, and the fixed footer
/// „technical check, no legal advice“. No network, nothing leaves the computer.
/// </summary>
public static class ValidationReport
{
    /// <summary>One section of a signature: a heading and its lines (label, value).</summary>
    private sealed record Section(string Heading, List<(string Label, string Value)> Lines);

    /// <summary>The report as plain text.</summary>
    /// <param name="checks">The results of <see cref="SignatureVerifier.Verify(Document.PdfDocument, string?, CancellationToken)"/>.</param>
    /// <param name="fileName">The name of the file checked (without folders).</param>
    /// <param name="version">The version of PDF Knead.</param>
    /// <param name="checkedAt">The time of the check.</param>
    public static string ToText(IReadOnlyList<SignatureCheck> checks, string fileName, string version, DateTimeOffset checkedAt)
    {
        var text = new StringBuilder();
        text.AppendLine(Tr.T("Prüfbericht digitaler Signaturen"));
        text.AppendLine(new string('=', 40));
        foreach (var (label, value) in Head(checks, fileName, version, checkedAt)) text.AppendLine($"{label}: {value}");
        for (var i = 0; i < checks.Count; i++)
        {
            text.AppendLine();
            text.AppendLine(Tr.T("Signatur {0} von {1}: {2}", i + 1, checks.Count, SignatureTexts.OneLine(checks[i])));
            foreach (var section in Sections(checks[i], checkedAt))
            {
                text.AppendLine("  " + section.Heading);
                foreach (var (label, value) in section.Lines)
                    text.AppendLine(label is "!" or "·" or "–" ? $"    {label} {value}" : $"    {label}: {value}");
            }
        }
        text.AppendLine();
        text.AppendLine(Tr.T("Prüfvorgaben"));
        foreach (var (label, value) in Constraints(version)) text.AppendLine($"  {label}: {value}");
        text.AppendLine();
        text.AppendLine(Footer());
        // the names from the file (field, signer, subject, issuer, reason, place, authority) with their directional formatting characters
        // shown — the report's own texts have none (Unicode 9, UTR #36; docs/FEHLERGEDAECHTNIS.md, No. 1217)
        return DisplayNames.Safe(text.ToString());
    }

    /// <summary>The report as a self-contained HTML page (no scripts, no outside resources).</summary>
    /// <inheritdoc cref="ToText" path="/param"/>
    public static string ToHtml(IReadOnlyList<SignatureCheck> checks, string fileName, string version, DateTimeOffset checkedAt)
    {
        static string E(string s) => WebUtility.HtmlEncode(DisplayNames.Safe(s));
        static void Table(StringBuilder html, IEnumerable<(string Label, string Value)> lines)
        {
            html.AppendLine("<table>");
            foreach (var (label, value) in lines) html.AppendLine($"<tr><th>{E(label)}</th><td>{E(value)}</td></tr>");
            html.AppendLine("</table>");
        }
        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine($"<html lang=\"{E(Tr.Culture.TwoLetterISOLanguageName)}\"><head><meta charset=\"utf-8\">");
        html.AppendLine($"<title>{E(Tr.T("Prüfbericht digitaler Signaturen"))} – {E(fileName)}</title>");
        html.AppendLine("<style>body{font-family:sans-serif;max-width:60em;margin:2em auto;padding:0 1em;color:#1a1a1a}th{text-align:left;" +
                        "vertical-align:top;padding:2px 12px 2px 0;font-weight:normal;color:#555;width:16em}td{padding:2px 0}h2{margin-top:2em}" +
                        "h3{margin:1em 0 .3em;font-size:1em}footer{margin-top:2em;color:#555;font-size:.9em}</style></head><body>");
        html.AppendLine($"<h1>{E(Tr.T("Prüfbericht digitaler Signaturen"))}</h1>");
        Table(html, Head(checks, fileName, version, checkedAt));
        for (var i = 0; i < checks.Count; i++)
        {
            html.AppendLine($"<h2>{E(Tr.T("Signatur {0} von {1}: {2}", i + 1, checks.Count, SignatureTexts.OneLine(checks[i])))}</h2>");
            foreach (var section in Sections(checks[i], checkedAt))
            {
                html.AppendLine($"<h3>{E(section.Heading)}</h3>");
                Table(html, section.Lines);
            }
        }
        html.AppendLine($"<h2>{E(Tr.T("Prüfvorgaben"))}</h2>");
        Table(html, Constraints(version));
        html.AppendLine($"<footer>{E(Footer())}</footer></body></html>");
        return html.ToString();
    }

    private static string Utc(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static IEnumerable<(string, string)> Head(IReadOnlyList<SignatureCheck> checks, string fileName, string version, DateTimeOffset checkedAt)
    {
        yield return (Tr.T("Datei"), fileName);
        yield return (Tr.T("Geprüft am"), Utc(checkedAt));
        yield return (Tr.T("Prüfer"), Tr.T("PDF-Knead {0}, lokal, ohne Netzverbindung", version));
        yield return (Tr.T("Signaturen"), checks.Count.ToString(Tr.Culture));
    }

    private static List<Section> Sections(SignatureCheck check, DateTimeOffset checkedAt)
    {
        var sections = new List<Section>();
        var indication = SignatureTexts.Indication(check);
        var result = new List<(string, string)>
        {
            (Tr.T("Urteil"), SignatureTexts.Verdict(check)),
            (Tr.T("Ergebnis nach EN 319 102-1"), indication.ToString()),
            (Tr.T("Prozess"), Process(check)),
            (Tr.T("Prüfzeit"), Utc(checkedAt)),
            (Tr.T("Gilt für"), AppliesTo(check, checkedAt)),
        };
        if (SignatureTexts.WhyNot(check) is { } why) result.Add((check.Outcome == SignatureOutcome.Valid ? Tr.T("Trägt") : Tr.T("Warum nicht"), why));
        sections.Add(new Section(Tr.T("Ergebnis"), result));

        var signature = new List<(string, string)>
        {
            (Tr.T("Feld"), check.FieldName.Length > 0 ? check.FieldName : "–"),
            (Tr.T("Verfahren"), check.Method.Length > 0 ? check.Method : "–"),
            (Tr.T("Unterschrieben wurde"), check.CoversWholeFile ? Tr.T("die ganze Datei") : Tr.T("eine frühere Fassung der Datei")),
        };
        if (check.Page is { } page) signature.Add((Tr.T("Seite"), (page + 1).ToString(Tr.Culture)));
        if (check.ByteRange.Count == 4)
            signature.Add((Tr.T("Byte-Bereiche"), string.Format(CultureInfo.InvariantCulture, "[{0} {1} {2} {3}]", check.ByteRange[0], check.ByteRange[1], check.ByteRange[2], check.ByteRange[3])));
        if (check.SignedDigest is { } digest) signature.Add((Tr.T("Prüfsumme der unterschriebenen Bytes"), digest));
        if (check.SigningTime is { } time) signature.Add((Tr.T("Angegebene Zeit"), Utc(time)));
        if (SignatureTexts.Later(check) is { } later) signature.Add((Tr.T("Danach"), later));
        foreach (var detail in check.LaterDetails) signature.Add(("–", detail));
        if (check.Certification is { } level) signature.Add((Tr.T("Zertifiziert"), Tr.T("danach erlaubt: {0}", SignatureTexts.Allowed(level))));
        if (check.Reason is { } reason) signature.Add((Tr.T("Grund"), reason));
        if (check.Location is { } location) signature.Add((Tr.T("Ort"), location));
        sections.Add(new Section(check.IsDocumentTimestamp ? Tr.T("Dokument-Zeitstempel") : Tr.T("Signatur"), signature));

        if (!check.IsDocumentTimestamp)
        {
            var signer = new List<(string, string)> { (Tr.T("Name"), check.SignerName ?? "–") };
            if (SignatureTexts.Kind(check) is { } kind) signer.Add((Tr.T("Art und Stufe"), kind));
            if (check.Commitment is { } commitment) signer.Add((Tr.T("Zweck"), commitment));
            sections.Add(new Section(Tr.T("Unterzeichner"), signer));
        }

        if (check.Certificate is { } certificate)
        {
            var chain = new List<(string, string)>
            {
                (Tr.T("Inhaber"), certificate.Subject),
                (Tr.T("Ausgestellt von"), certificate.SelfSigned ? Tr.T("selbst ausgestellt") : certificate.Issuer),
                (Tr.T("Gültig"), Tr.T("{0} bis {1}", Utc(certificate.ValidFrom), Utc(certificate.ValidUntil))),
                (Tr.T("Fingerabdruck (SHA-256)"), certificate.Fingerprint),
                (Tr.T("Identität"), check.IdentityConfirmed ? Tr.T("bestätigt") : Tr.T("nicht bestätigt")),
                (Tr.T("Vertrauensanker"), check.TrustListsLoaded ? Tr.T("Stammzertifikate des Systems und die geladene EU-Vertrauensliste") : Tr.T("Stammzertifikate des Systems")),
                (Tr.T("Gültigkeitsmodell"), Tr.T("Schalenmodell")),
            };
            if (check.ChainProblem is { } problem) chain.Add((Tr.T("Kette"), problem));
            for (var i = 0; i < check.Chain.Count; i++)
            {
                var link = check.Chain[i];
                chain.Add((Tr.T("Glied {0}", i + 1), Tr.T("{0} · gültig {1} bis {2} · {3}", link.Subject, Utc(link.ValidFrom), Utc(link.ValidUntil), link.Fingerprint)
                    + (i == check.Chain.Count - 1 && check.IdentityConfirmed ? " · " + Tr.T("Vertrauensanker") : "")));
            }
            if (SignatureTexts.TrustList(check) is { } list) chain.Add((Tr.T("EU-Vertrauensliste"), list));
            sections.Add(new Section(Tr.T("Zertifikat und Kette"), chain));
        }

        var longTerm = new List<(string, string)>();
        if (check.Timestamp is { } stamp)
            longTerm.Add((Tr.T("Zeitstempel"), Tr.T("{0} von {1}", Utc(stamp.Time), stamp.Authority.Length > 0 ? stamp.Authority : "–") + " · " +
                (!stamp.Valid ? Tr.T("passt nicht") : stamp.Trusted ? stamp.ThroughProof ? Tr.T("vertraut über einen späteren Dokument-Zeitstempel") : Tr.T("vertraut")
                    : Tr.T("Dienst nicht vertraut"))));
        if (SignatureTexts.LongTerm(check) is { } revocation) longTerm.Add((Tr.T("Sperrstatus"), revocation));
        // each certificate of the signer's chain with the answer it rests on (ETSI TS 119 102-2; docs/FEHLERGEDAECHTNIS.md, No. 725)
        foreach (var link in check.Revocation?.Links ?? [])
            longTerm.Add(("– " + link.Name, link.Kind switch
            {
                RevocationInfo.LinkKind.Root => Tr.T("Wurzel, braucht keine Sperrauskunft"),
                RevocationInfo.LinkKind.NoAnswer => Tr.T("keine passende Sperrauskunft in der Datei"),
                _ => Tr.T("{0} laut {1} vom {2}", link.Kind switch
                    {
                        RevocationInfo.LinkKind.Good => Tr.T("nicht gesperrt"),
                        RevocationInfo.LinkKind.OnHold => Tr.T("vorübergehend gesperrt"),
                        _ => Tr.T("gesperrt seit {0}", link.RevokedAt is { } since ? Utc(since) : "?"),
                    }, link.Source == "OCSP" ? Tr.T("OCSP-Antwort") : Tr.T("Sperrliste"), link.ThisUpdate is { } given ? Utc(given) : "?")
                    + (link.NextUpdate is { } next ? ", " + Tr.T("gültig bis {0}", Utc(next)) : ""),
            }));
        if (SignatureTexts.PadesLevel(check) is { } pades) longTerm.Add((Tr.T("PAdES-Stufe"), pades));
        if (SignatureTexts.RenewBefore(check) is { } renew) longTerm.Add((Tr.T("Schutz erneuern bis spätestens"), renew));
        if (longTerm.Count > 0) sections.Add(new Section(Tr.T("Zeit und Langzeit"), longTerm));

        var notes = new List<(string, string)>();
        if (check.UnconfirmedReason is { } unconfirmed) notes.Add(("!", unconfirmed));
        if (check.Problem is { } failed) notes.Add(("!", failed));
        foreach (var warning in check.Warnings) notes.Add(("·", warning));
        if (notes.Count > 0) sections.Add(new Section(Tr.T("Hinweise"), notes));
        return sections;
    }

    /// <summary>The process of EN 319 102-1 the result rests on: with a proof from a document time stamp LTA (5.6), with a trusted time stamp the validation with time (5.5), else the basic one (5.3).</summary>
    private static string Process(SignatureCheck check) =>
        check.ProvenAt is not null ? Tr.T("Langzeitprüfung mit Archiv-Zeitstempel (EN 319 102-1, 5.6)")
        : check.Timestamp is { Valid: true, Trusted: true } ? Tr.T("Prüfung mit Zeitnachweis (EN 319 102-1, 5.5)")
        : Tr.T("Grundprüfung (EN 319 102-1, 5.3)");

    /// <summary>The time the result applies to, with its source.</summary>
    private static string AppliesTo(SignatureCheck check, DateTimeOffset checkedAt) =>
        check.Timestamp is { Valid: true, Trusted: true } stamp && !check.IsDocumentTimestamp && (check.ProvenAt is null || stamp.Time <= check.ProvenAt)
            ? Tr.T("{0} (Zeitstempel der Signatur)", Utc(stamp.Time))
        : check.ProvenAt is { } proven ? Tr.T("{0} (belegt durch einen späteren Dokument-Zeitstempel)", Utc(proven))
        : Tr.T("{0} (Zeitpunkt der Prüfung)", Utc(checkedAt));

    /// <summary>The implicit constraints PDF Knead checks by (ETSI TS 119 172-1; I-13).</summary>
    private static IEnumerable<(string, string)> Constraints(string version)
    {
        yield return (Tr.T("Grundlage"), Tr.T("Implizite Prüfvorgaben von PDF-Knead {0}; keine Signaturrichtlinie ausgewertet", version));
        yield return (Tr.T("Vertrauensanker"), Tr.T("Stammzertifikate des Systems; die EU-Vertrauensliste, wenn geladen"));
        yield return (Tr.T("Zertifikatspfad"), Tr.T("RFC 5280, 6.1, offline; Gültigkeit im Schalenmodell, auch die der Wurzel"));
        yield return (Tr.T("Sperrprüfung"), Tr.T("nur die Prüfdaten in der Datei (ohne Netz); ohne sie „nicht bestätigt“"));
        yield return (Tr.T("Verfahren"), Tr.T("Eignung nach ETSI TS 119 312 V2.1.1; MD5 und zu kurze Schlüssel nicht bestätigt, SHA-1 als Hinweis"));
        yield return (Tr.T("Zeitnachweise"), Tr.T("Zeitstempel nur von vertrauten oder qualifizierten Diensten; Dokument-Zeitstempel als Existenznachweis"));
    }

    private static string Footer() => Tr.T("Technische Prüfung, keine Rechtsberatung. Erstellt von PDF-Knead auf diesem Rechner; nichts wurde übertragen.");
}
