namespace PdfKnead.Core.Pdf.Signatures;

/// <summary>The legal level of a signature or seal (ETSI TS 119 172-1, A.3.2.1; eIDAS Art. 3, 25, 26, 35, 36).</summary>
public enum NatureLevel
{
    /// <summary>Simple: nobody independent confirmed the name.</summary>
    Simple,

    /// <summary>Advanced at best: an authority confirmed the name.</summary>
    Advanced,

    /// <summary>Advanced with a qualified certificate: qualified certificate, the key not shown on a qualified device.</summary>
    AdvancedQc,

    /// <summary>Qualified: qualified certificate and the key on a qualified device (QES or qualified seal).</summary>
    Qualified,
}

/// <summary>
/// What kind of signature it is and at which level (docs/ABLAUFPLAN.md, 2.6): signature or seal, the level, the device, whether the EU
/// trusted list confirms it, and why the level is not higher.
/// </summary>
/// <param name="Kind">Signature, seal, website certificate or document time stamp.</param>
/// <param name="Level">The level.</param>
/// <param name="Qscd">What is declared about the device holding the key.</param>
/// <param name="Listed">The level rests on the loaded EU trusted list (else on the certificate's own statements, or on nothing).</param>
/// <param name="OtherLaw">The countries under whose law the certificate is qualified, if not the EU's (QcCClegislation).</param>
/// <param name="Pseudonym">The certificate names a pseudonym instead of the person.</param>
/// <param name="Reasons">Why the level is not higher, localised, most important first.</param>
public sealed record SignatureNature(SignatureKind Kind, NatureLevel Level, QscdState Qscd, bool Listed, IReadOnlyList<string> OtherLaw, bool Pseudonym,
    IReadOnlyList<string> Reasons)
{
    /// <summary>The key is managed by the provider on behalf of the signer, as the EU trusted list says (QCQSCDManagedOnBehalf).</summary>
    public bool Remote { get; init; }
}

/// <summary>Derives the <see cref="SignatureNature"/> of a checked signature, in this one place, offline (certificate and loaded list).</summary>
public static class SignatureNatures
{
    /// <summary>The nature of a checked signature; null without a certificate (a damaged block).</summary>
    /// <param name="check">The result.</param>
    /// <returns>The nature.</returns>
    public static SignatureNature? Of(SignatureCheck check)
    {
        if (check.IsDocumentTimestamp)
            // qualified only from a service the check trusts (a revoked one is not, docs/FEHLERGEDAECHTNIS.md, No. 483)
            return new SignatureNature(SignatureKind.DocumentTimestamp,
                check.TimestampService is { Qualified: true } && check.IdentityConfirmed ? NatureLevel.Qualified : NatureLevel.Simple,
                QscdState.Unknown, check.TimestampService is { Qualified: true } && check.IdentityConfirmed, [], false, []);
        if (check.Certificate is not { } certificate) return null;
        var profile = certificate.Profile;
        // with the lists loaded, their verdict decides — their qualifiers before the certificate's statements (No. 484)
        var listed = check.TrustListsLoaded ? check.Listed : null;
        var kind = listed?.Kind ?? profile.Kind;
        var qscd = listed?.Qscd ?? profile.Qscd;
        var reasons = new List<string>();
        NatureLevel level;
        if (certificate.SelfSigned)
        {
            level = NatureLevel.Simple;
            reasons.Add(Tr.T("Niemand Unabhängiges hat den Namen bestätigt (selbst ausgestelltes Zertifikat)."));
        }
        else
        {
            level = NatureLevel.Advanced;
            if (kind == SignatureKind.WebCertificate) reasons.Add(Tr.T("Das Zertifikat ist ein Webseiten-Zertifikat, nicht zum Unterschreiben bestimmt."));
            else if (!SignatureTexts.IsValid(check)) reasons.Add(Tr.T("Die Signatur ist nicht gültig."));
            else if (!check.IdentityConfirmed) reasons.Add(Tr.T("Die Identität ist nicht bestätigt."));
            // content commitment for declarations of a person; a seal may do with digitalSignature (EN 319 412-3)
            else if (!profile.ContentCommitment && kind == SignatureKind.Signature)
                reasons.Add(Tr.T("Das Zertifikat ist nur zur Anmeldung bestimmt, nicht für Willenserklärungen (Schlüsselverwendung)."));
            else if (check.TrustListsLoaded)
            {
                if (listed is not { Qualified: true })
                    reasons.Add(listed?.Reason ?? (profile.QcCompliance
                        ? Tr.T("Die EU-Vertrauensliste führt den Aussteller zur Unterschriftszeit nicht als qualifizierten Anbieter.")
                        : Tr.T("Das Zertifikat ist kein qualifiziertes Zertifikat.")));
                else if (listed.Kind is null && profile.QcTypeUnclear)
                    reasons.Add(Tr.T("Das Zertifikat nennt keine eindeutige Art (Signatur, Siegel oder Webseite)."));
                else
                {
                    level = NatureLevel.AdvancedQc;
                    if (qscd == QscdState.Yes) level = NatureLevel.Qualified;
                    else reasons.Add(qscd switch
                    {
                        QscdState.No => Tr.T("Laut EU-Vertrauensliste liegt der Schlüssel nicht auf einer sicheren Signaturerstellungseinheit."),
                        QscdState.OtherCountry => Tr.T("Die sichere Signaturerstellungseinheit ist nur nach dem Recht von {0} anerkannt.", string.Join(", ", profile.QscdLegislation)),
                        _ => Tr.T("Nichts erklärt, dass der Schlüssel auf einer sicheren Signaturerstellungseinheit liegt."),
                    });
                }
            }
            else if (!profile.QcCompliance) reasons.Add(Tr.T("Das Zertifikat ist kein qualifiziertes Zertifikat."));
            else if (profile.QcTypeUnclear) reasons.Add(Tr.T("Das Zertifikat nennt keine eindeutige Art (Signatur, Siegel oder Webseite)."));
            else if (profile.QcLegislation.Count > 0)
                reasons.Add(Tr.T("Das Zertifikat ist nach dem Recht von {0} qualifiziert, nicht nach EU-Recht.", string.Join(", ", profile.QcLegislation)));
            else
            {
                level = NatureLevel.AdvancedQc;
                if (profile.Qscd == QscdState.Yes) level = NatureLevel.Qualified;
                else reasons.Add(profile.Qscd == QscdState.OtherCountry
                    ? Tr.T("Die sichere Signaturerstellungseinheit ist nur nach dem Recht von {0} anerkannt.", string.Join(", ", profile.QscdLegislation))
                    : Tr.T("Nichts erklärt, dass der Schlüssel auf einer sicheren Signaturerstellungseinheit liegt."));
            }
        }
        return new SignatureNature(kind, level, qscd, listed is { Qualified: true } && level >= NatureLevel.AdvancedQc,
            profile.QcLegislation, profile.Pseudonym, reasons) { Remote = listed is { Remote: true, Qualified: true } };
    }
}
