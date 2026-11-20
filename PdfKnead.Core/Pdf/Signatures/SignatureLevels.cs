using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;

namespace PdfKnead.Core.Pdf.Signatures;

/// <summary>What kind of electronic signature a certificate gives (eIDAS, Regulation (EU) No 910/2014, Art. 3 and 26; KONZEPT.md, section 5.17).</summary>
public enum SignatureLevel
{
    /// <summary>Simple: nobody independent confirmed the name (a self-issued certificate).</summary>
    Simple,

    /// <summary>Advanced at best: a certification authority confirmed the name; whether the key is under the signer's sole control is not visible.</summary>
    Advanced,

    /// <summary>
    /// Qualified as the certificate declares it: a qualified certificate (QcCompliance) whose key lies on a qualified signature creation device
    /// (QcSSCD). Whether the issuer is on the EU trusted lists is not checked here.
    /// </summary>
    Qualified,
}

/// <summary>
/// The level a certificate gives a signature, from its own statements: the „qualified certificate statements“ (RFC 3739, extension
/// 1.3.6.1.5.5.7.1.3) with the ETSI identifiers of EN 319 412-5 (4.2.1 QcCompliance 0.4.0.1862.1.1, 4.2.2 QcSSCD 0.4.0.1862.1.4). Read without
/// any connection; the EU trusted lists, which confirm the issuer, are a separate step (docs/OFFENE-PUNKTE.md, „QES-Tauglichkeit“).
/// </summary>
public static class SignatureLevels
{
    private const string QcStatements = "1.3.6.1.5.5.7.1.3", QcCompliance = "0.4.0.1862.1.1", QcSscd = "0.4.0.1862.1.4";

    /// <summary>The level of a certificate.</summary>
    /// <param name="certificate">The signer's certificate.</param>
    /// <returns>
    /// Simple if self-issued; qualified if it declares both statements under EU law (no QcCClegislation, EN 319 412-5, 4.2.1), is meant for
    /// declarations (content commitment, EN 319 412-2, 4.3.2) and is no website certificate; advanced otherwise (docs/ABLAUFPLAN.md, 2.6).
    /// </returns>
    public static SignatureLevel Of(X509Certificate2 certificate)
    {
        if (certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData)) return SignatureLevel.Simple;
        var profile = CertificateProfiles.Of(certificate);
        return profile.EuQualified && profile.QcSscd && profile.ContentCommitment && profile.Kind != SignatureKind.WebCertificate && !profile.QcTypeUnclear
            ? SignatureLevel.Qualified : SignatureLevel.Advanced;
    }

    /// <summary>Whether the certificate declares itself qualified (QcCompliance) and its key on a qualified device (QcSSCD).</summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns>Both statements; false for each that is missing or unreadable.</returns>
    public static (bool Compliance, bool Sscd) Statements(X509Certificate2 certificate)
    {
        var extension = certificate.Extensions.Cast<X509Extension>().FirstOrDefault(e => e.Oid?.Value == QcStatements);
        return extension is null ? (false, false) : Statements(extension.RawData);
    }

    /// <summary>The statements of an encoded QCStatements extension (SEQUENCE OF SEQUENCE { statementId OID, statementInfo ANY OPTIONAL }).</summary>
    internal static (bool Compliance, bool Sscd) Statements(ReadOnlyMemory<byte> encoded)
    {
        bool compliance = false, sscd = false;
        try
        {
            var outer = new AsnReader(encoded, AsnEncodingRules.DER).ReadSequence();
            for (var i = 0; outer.HasData && i < 64; i++)
            {
                var statement = outer.ReadSequence();
                var id = statement.ReadObjectIdentifier();
                compliance |= id == QcCompliance;
                sscd |= id == QcSscd;
            }
        }
        catch (AsnContentException)
        {
            // a damaged extension declares nothing
        }
        return (compliance, sscd);
    }
}
