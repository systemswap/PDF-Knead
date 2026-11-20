using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;

namespace PdfKnead.Core.Pdf.Signatures;

/// <summary>What a certificate is for, as far as its own statements tell (EN 319 412-5, 4.2.3; ETSI TS 119 172-1, A.3.2.1).</summary>
public enum SignatureKind
{
    /// <summary>A signature of a natural person: a declaration of will.</summary>
    Signature,

    /// <summary>A seal of a legal person (an organisation): it confirms origin and integrity, it is no declaration of a person.</summary>
    Seal,

    /// <summary>A certificate for website authentication: never meant for signing documents.</summary>
    WebCertificate,

    /// <summary>A document time stamp: no person, no organisation, only a time.</summary>
    DocumentTimestamp,
}

/// <summary>Whether the key lies on a qualified signature or seal creation device (decision E: no „card“ or „software“, only what is declared).</summary>
public enum QscdState
{
    /// <summary>Nothing declares it.</summary>
    Unknown,

    /// <summary>Declared (QcSSCD, or the EU trusted list says so).</summary>
    Yes,

    /// <summary>Declared under the law of a country outside the EU and the EEA (QcQSCDlegislation).</summary>
    OtherCountry,

    /// <summary>The EU trusted list says it is not (QCNoQSCD, QCNoSSCD).</summary>
    No,
}

/// <summary>
/// The profile of a certificate as far as it matters for the kind and level of a signature, read from the certificate alone, offline: the
/// qualified certificate statements with their content (EN 319 412-5, 4.2: QcCompliance, QcSSCD, QcType, QcCClegislation, QcQSCDlegislation,
/// QcPDS), the semantics identifier (EN 319 412-1, 5.1), the certificate policies (EN 319 411-1 and 319 411-2), the subject's attributes
/// (EN 319 412-2, 4.2.4; 319 412-3, 4.2.1) and the key usage (EN 319 412-2, 4.3.2).
/// </summary>
/// <param name="QcCompliance">Declares itself a qualified certificate (QcCompliance).</param>
/// <param name="QcLegislation">Countries under whose law it is qualified (QcCClegislation); empty: EU.</param>
/// <param name="QcSscd">Declares its key on a qualified creation device (QcSSCD).</param>
/// <param name="QscdLegislation">Countries outside the EU/EEA under whose law the device is qualified (QcQSCDlegislation).</param>
/// <param name="QcType">The declared type (esign, eseal, web); null without QcType or with more than one value.</param>
/// <param name="LegalPerson">The semantics identifier says legal person.</param>
/// <param name="NaturalPerson">The semantics identifier says natural person.</param>
/// <param name="OrganizationIdentifier">The subject carries an organizationIdentifier (2.5.4.97).</param>
/// <param name="PersonName">The subject carries givenName or surname.</param>
/// <param name="Pseudonym">The subject carries a pseudonym (2.5.4.65).</param>
/// <param name="ContentCommitment">The key usage allows content commitment (nonRepudiation), or the certificate has no key usage.</param>
/// <param name="Policies">The certificate policy identifiers.</param>
/// <param name="ShortTerm">A short-term certificate (EN 319 412-1, 5.2).</param>
public sealed record CertificateProfile(bool QcCompliance, IReadOnlyList<string> QcLegislation, bool QcSscd, IReadOnlyList<string> QscdLegislation,
    SignatureKind? QcType, bool LegalPerson, bool NaturalPerson, bool OrganizationIdentifier, bool PersonName, bool Pseudonym, bool ContentCommitment,
    IReadOnlyList<string> Policies, bool ShortTerm)
{
    /// <summary>
    /// QcType is there but names no single known type (several values or unknown ones, EN 319 412-5, 4.2.3): the kind stays open — never
    /// qualified (docs/FEHLERGEDAECHTNIS.md, No. 483).
    /// </summary>
    public bool QcTypeUnclear { get; init; }

    /// <summary>Qualified under EU law as the certificate declares it: QcCompliance without QcCClegislation (EN 319 412-5, 4.2.1, Tab. 1).</summary>
    public bool EuQualified => QcCompliance && QcLegislation.Count == 0;

    /// <summary>
    /// The kind from the certificate: QcType decides; without it a qualified certificate is one for signatures (EN 319 412-5, Tab. 1);
    /// otherwise a legal person (semantics identifier, or an organisation identifier without a person's name or pseudonym) points to a seal.
    /// </summary>
    public SignatureKind Kind => QcType ?? (QcCompliance ? SignatureKind.Signature
        : LegalPerson || (OrganizationIdentifier && !PersonName && !Pseudonym && !NaturalPerson) ? SignatureKind.Seal
        : SignatureKind.Signature);

    /// <summary>What the certificate declares about the device holding the key.</summary>
    public QscdState Qscd => QcSscd ? QscdState.Yes : QscdLegislation.Count > 0 ? QscdState.OtherCountry : QscdState.Unknown;

    /// <summary>A profile that declares nothing.</summary>
    public static readonly CertificateProfile None = new(false, [], false, [], null, false, false, false, false, false, true, [], false);
}

/// <summary>Reads the <see cref="CertificateProfile"/> of a certificate.</summary>
public static class CertificateProfiles
{
    private const string QcStatements = "1.3.6.1.5.5.7.1.3", CertificatePolicies = "2.5.29.32";
    private const string QcCompliance = "0.4.0.1862.1.1", QcSscd = "0.4.0.1862.1.4", QcType = "0.4.0.1862.1.6", QcCcLegislation = "0.4.0.1862.1.7",
        QcQscdLegislation = "0.4.0.1862.1.9", SemanticsV2 = "1.3.6.1.5.5.7.11.2";
    private const string Natural = "0.4.0.194121.1.1", Legal = "0.4.0.194121.1.2", EidasNatural = "0.4.0.194121.1.3", EidasLegal = "0.4.0.194121.1.4";

    /// <summary>Limits against prepared certificates.</summary>
    private const int MaxStatements = 64, MaxValues = 32, MaxPolicies = 64;

    /// <summary>The profile of a certificate; parts that cannot be read declare nothing.</summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns>The profile.</returns>
    public static CertificateProfile Of(X509Certificate2 certificate)
    {
        var profile = CertificateProfile.None;
        foreach (var extension in certificate.Extensions)
        {
            switch (extension.Oid?.Value)
            {
                case QcStatements:
                    profile = Statements(extension.RawData, profile);
                    break;
                case CertificatePolicies:
                    profile = profile with { Policies = Policies(extension.RawData) };
                    break;
                case "0.4.0.194121.2.1":
                    profile = profile with { ShortTerm = true };
                    break;
            }
            if (extension is X509KeyUsageExtension usage)
                profile = profile with { ContentCommitment = (usage.KeyUsages & X509KeyUsageFlags.NonRepudiation) != 0 };
        }
        var (organization, person, pseudonym) = Subject(certificate.SubjectName.RawData);
        return profile with { OrganizationIdentifier = organization, PersonName = person, Pseudonym = pseudonym };
    }

    /// <summary>The statements of an encoded QCStatements extension, added to a profile (SEQUENCE OF SEQUENCE { statementId, statementInfo }).</summary>
    internal static CertificateProfile Statements(ReadOnlyMemory<byte> encoded, CertificateProfile profile)
    {
        try
        {
            var outer = new AsnReader(encoded, AsnEncodingRules.DER).ReadSequence();
            for (var i = 0; outer.HasData && i < MaxStatements; i++)
            {
                var statement = outer.ReadSequence();
                var id = statement.ReadObjectIdentifier();
                try
                {
                    profile = id switch
                    {
                        QcCompliance => profile with { QcCompliance = true },
                        QcSscd => profile with { QcSscd = true },
                        QcType => Type(statement) is { } kind ? profile with { QcType = kind } : profile with { QcTypeUnclear = true },
                        QcCcLegislation => profile with { QcLegislation = Countries(statement) },
                        QcQscdLegislation => profile with { QscdLegislation = Countries(statement) },
                        SemanticsV2 => Semantics(statement, profile),
                        _ => profile,
                    };
                }
                catch (AsnContentException)
                {
                    // a damaged statement info declares nothing; the statement's identifier alone counts only where it has no content
                }
            }
        }
        catch (AsnContentException)
        {
            // a damaged extension declares nothing more
        }
        return profile;
    }

    /// <summary>QcType: exactly one value (EN 319 412-5, 4.2.3); none, several or unknown ones declare nothing.</summary>
    private static SignatureKind? Type(AsnReader statement)
    {
        var values = statement.ReadSequence();
        var kinds = new List<SignatureKind?>();
        for (var i = 0; values.HasData && i < MaxValues; i++)
            kinds.Add(values.ReadObjectIdentifier() switch
            {
                "0.4.0.1862.1.6.1" => SignatureKind.Signature,
                "0.4.0.1862.1.6.2" => SignatureKind.Seal,
                "0.4.0.1862.1.6.3" => SignatureKind.WebCertificate,
                _ => null,
            });
        return kinds.Count == 1 ? kinds[0] : null;
    }

    /// <summary>A SEQUENCE OF PrintableString (SIZE 2): the country codes, upper case.</summary>
    private static IReadOnlyList<string> Countries(AsnReader statement)
    {
        var values = statement.ReadSequence();
        var countries = new List<string>();
        for (var i = 0; values.HasData && i < MaxValues; i++)
            if (values.ReadCharacterString(UniversalTagNumber.PrintableString) is { Length: 2 } code) countries.Add(code.ToUpperInvariant());
        return countries;
    }

    /// <summary>SemanticsInformation ::= SEQUENCE { semanticsIdentifier OID OPTIONAL, nameRegistrationAuthorities OPTIONAL } (RFC 3739, 3.2.6.1).</summary>
    private static CertificateProfile Semantics(AsnReader statement, CertificateProfile profile)
    {
        var info = statement.ReadSequence();
        if (!info.HasData || !info.PeekTag().HasSameClassAndValue(Asn1Tag.ObjectIdentifier)) return profile;
        return info.ReadObjectIdentifier() switch
        {
            Natural or EidasNatural => profile with { NaturalPerson = true },
            Legal or EidasLegal => profile with { LegalPerson = true },
            _ => profile,
        };
    }

    /// <summary>certificatePolicies ::= SEQUENCE OF PolicyInformation { policyIdentifier, policyQualifiers OPTIONAL } (RFC 5280, 4.2.1.4).</summary>
    private static IReadOnlyList<string> Policies(ReadOnlyMemory<byte> encoded)
    {
        var policies = new List<string>();
        try
        {
            var outer = new AsnReader(encoded, AsnEncodingRules.DER).ReadSequence();
            for (var i = 0; outer.HasData && i < MaxPolicies; i++)
                policies.Add(outer.ReadSequence().ReadObjectIdentifier());
        }
        catch (AsnContentException)
        {
            // what was read counts
        }
        return policies;
    }

    /// <summary>Which of organizationIdentifier, givenName/surname and pseudonym the subject carries.</summary>
    private static (bool Organization, bool Person, bool Pseudonym) Subject(byte[] name)
    {
        bool organization = false, person = false, pseudonym = false;
        try
        {
            var rdns = new AsnReader(name, AsnEncodingRules.DER).ReadSequence();
            for (var i = 0; rdns.HasData && i < 64; i++)
            {
                var set = rdns.ReadSetOf();
                while (set.HasData)
                {
                    var attribute = set.ReadSequence();
                    switch (attribute.ReadObjectIdentifier())
                    {
                        case "2.5.4.97":
                            organization = true;
                            break;
                        case "2.5.4.42" or "2.5.4.4":
                            person = true;
                            break;
                        case "2.5.4.65":
                            pseudonym = true;
                            break;
                    }
                }
            }
        }
        catch (AsnContentException)
        {
            // what was read counts
        }
        return (organization, person, pseudonym);
    }
}
