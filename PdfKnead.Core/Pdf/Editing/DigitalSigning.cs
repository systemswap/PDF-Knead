namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Marks a signature as to be signed digitally when saving (KONZEPT.md, section 5.17). Signing itself needs the final bytes of
/// the file, so it happens while saving; until then the element is an ordinary signature on the page.
/// </summary>
/// <param name="Origin">Text of the origin line below the signature (name and date); null = no line.</param>
/// <param name="FieldName">Name of the signature field to sign into (an empty one of the file or an own one); null = a new field.</param>
/// <param name="Certify">Certify the document with this level (only the first signature of a document can); null = an ordinary signature.</param>
/// <param name="LockFields">Lock all fields of the form at signing: changing one afterwards breaks this signature (FieldMDP).</param>
/// <param name="Signer">
/// The name the signer gave (shown in the signature or its origin line); null if none. Where the certificate names somebody else, the
/// signature shows the holder of the certificate as well (<see cref="Signatures.PdfSigner"/>).
/// </param>
/// <param name="With">
/// The certificate for this signature only (chosen in the signature window without „als Standard merken“; docs/ABLAUFPLAN.md, 9.4.0, step 2);
/// null = the default of the settings.
/// </param>
public sealed record DigitalSigning(string? Origin, string? FieldName = null, Signatures.CertificationLevel? Certify = null, bool LockFields = false,
    string? Signer = null, Platform.SigningCertificateChoice? With = null);
