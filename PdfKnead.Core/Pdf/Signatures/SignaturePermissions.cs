using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Signatures;

/// <summary>What a certification allows after signing (DocMDP, ISO 32000-2:2020, 12.8.2.2, values of /P).</summary>
public enum CertificationLevel
{
    /// <summary>No changes at all, not even further signatures (/P 1).</summary>
    NoChanges = 1,
    /// <summary>Filling in fields and signing (/P 2).</summary>
    FormsAndSigning = 2,
    /// <summary>In addition creating, changing and deleting comments (/P 3).</summary>
    FormsSigningAndComments = 3,
}

/// <summary>Fields a signature locks (FieldMDP, ISO 32000-2:2020, 12.8.2.4): all, the listed ones, or all but the listed ones.</summary>
/// <param name="Action">/All, /Include or /Exclude.</param>
/// <param name="Fields">Full names of the listed fields.</param>
public sealed record FieldLock(string Action, IReadOnlySet<string> Fields)
{
    /// <summary>
    /// The level a field's lock dictionary sets for the whole document after signing (/P of Table 236, PDF 2.0: 1 no changes, 2 filling in
    /// and signing, 3 also comments); null if it sets none. Only a field's /Lock has it, never a FieldMDP transform.
    /// </summary>
    public CertificationLevel? Level { get; init; }

    /// <summary>Whether a field with this full name is locked (a field locks its descendants too). An unknown /Action locks all (stricter).</summary>
    /// <param name="name">Full name of the field.</param>
    /// <returns>true if changing it breaks the signature.</returns>
    public bool Locks(string name) => Action switch
    {
        "Include" => Listed(name),
        "Exclude" => !Listed(name),
        _ => true,
    };

    private bool Listed(string name) => Fields.Any(f => name == f || name.StartsWith(f + ".", StringComparison.Ordinal));

    /// <summary>Reads the lock of a signature dictionary (its FieldMDP reference); null if it has none.</summary>
    /// <param name="signature">The signature dictionary.</param>
    /// <returns>The lock, or null.</returns>
    public static FieldLock? Of(PdfDictionary signature)
    {
        return SignaturePermissions.Reference(signature, "FieldMDP") is { } parameters ? Read(parameters) : null;
    }

    /// <summary>
    /// Reads the lock dictionary of a signature field (/Lock, Table 236): /Action and /Fields as for FieldMDP, and /P; null if there is none.
    /// The fields named there must not change once the field is signed — also when a signature does not carry them over (another program).
    /// </summary>
    /// <param name="field">The signature field (its /Lock is not inherited).</param>
    public static FieldLock? OfField(PdfDictionary? field) =>
        field?.Get(PdfName.Get("Lock")) is PdfDictionary lockDict ? Read(lockDict) with
        {
            Level = lockDict.GetInteger(PdfName.Get("P")) switch
            {
                1 => CertificationLevel.NoChanges,
                2 => CertificationLevel.FormsAndSigning,
                3 => CertificationLevel.FormsSigningAndComments,
                _ => null,
            },
        } : null;

    /// <summary>/Action (missing: /All, the stricter reading of a required key) and /Fields (at most 10 000 names).</summary>
    private static FieldLock Read(PdfDictionary parameters)
    {
        var action = parameters.GetName(PdfName.Get("Action"))?.Value ?? "All";
        var fields = new HashSet<string>(StringComparer.Ordinal);
        if (parameters.GetArray(PdfName.Get("Fields")) is { } list)
            for (var i = 0; i < Math.Min(list.Count, 10_000); i++)
                if (list.Get(i) is PdfString name) fields.Add(name.ToText());
        return new FieldLock(action, fields);
    }

    /// <summary>Whether two locks lock the same: the same action and the same fields.</summary>
    public bool SameAs(FieldLock other) => Action == other.Action && (Action == "All" || Fields.SetEquals(other.Fields));
}

/// <summary>
/// The restrictions the intact signatures of a file set for later changes: the certification level of the certification signature
/// (DocMDP) and the field locks (FieldMDP). PDF-Knead appends only what they allow; the check of a signature applies them too.
/// </summary>
/// <param name="Certification">The level of the certification; null = the document is not certified.</param>
/// <param name="Locks">The field locks of all intact signatures.</param>
public sealed record SignaturePermissions(CertificationLevel? Certification, IReadOnlyList<FieldLock> Locks)
{
    /// <summary>No restrictions (an uncertified document without locked fields).</summary>
    public static readonly SignaturePermissions None = new(null, []);

    private static readonly PdfName ReferenceKey = PdfName.Get("Reference"), TransformMethod = PdfName.Get("TransformMethod"),
        TransformParams = PdfName.Get("TransformParams"), Perms = PdfName.Get("Perms"), DocMdp = PdfName.Get("DocMDP");

    /// <summary>Whether further signatures and filling in fields are allowed.</summary>
    public bool AllowsFormsAndSigning => Certification is not CertificationLevel.NoChanges;

    /// <summary>Whether comments may be added, changed or deleted.</summary>
    public bool AllowsComments => Certification is null or CertificationLevel.FormsSigningAndComments;

    /// <summary>Whether changing a field breaks a signature.</summary>
    /// <param name="name">Full name of the field.</param>
    /// <returns>true if one of the locks covers it.</returns>
    public bool IsLocked(string name) => Locks.Any(l => l.Locks(name));

    /// <summary>Reads the restrictions of the intact signatures of a document.</summary>
    /// <param name="doc">The document.</param>
    /// <returns>The restrictions; <see cref="None"/> without signatures.</returns>
    public static SignaturePermissions Of(PdfDocument doc)
    {
        var entries = DigitalSignatures.Find(doc).Where(e => e.Intact && e.Signature is not null).ToList();
        if (entries.Count == 0) return None;
        // the signature's FieldMDP and the /Lock of its field (a signature of another program may not carry it over; Table 236 holds anyway)
        var locks = entries.SelectMany(e => new[] { FieldLock.Of(e.Signature!), FieldLock.OfField(e.Field) }).OfType<FieldLock>().ToList();
        return new SignaturePermissions(CertificationOf(doc, entries), locks);
    }

    /// <summary>
    /// The level of the certification signature: the signature the catalog's /Perms /DocMDP names and whose /Reference carries the
    /// DocMDP transform (/P, default 2). Only the first signature of a document may certify it; a later one is not counted.
    /// </summary>
    internal static CertificationLevel? CertificationOf(PdfDocument doc, IReadOnlyList<DigitalSignatureEntry> entries)
    {
        if (doc.Catalog?.GetDictionary(Perms)?.GetDictionary(DocMdp) is not { } certifying) return null;
        var first = entries.MinBy(e => e.ByteRange[2] + e.ByteRange[3]);
        if (first?.Signature is not { } signature || !ReferenceEquals(signature, certifying)) return null;
        return Level(signature);
    }

    /// <summary>The DocMDP level of a signature dictionary; null if it does not certify.</summary>
    /// <param name="signature">The signature dictionary.</param>
    /// <returns>The level, or null.</returns>
    public static CertificationLevel? Level(PdfDictionary signature) =>
        Reference(signature, "DocMDP") is not { } parameters ? null
        : parameters.GetInteger(PdfName.Get("P")) switch { 1 => CertificationLevel.NoChanges, 3 => CertificationLevel.FormsSigningAndComments, _ => CertificationLevel.FormsAndSigning };

    /// <summary>The transform parameters of the signature reference with this transform method; null if there is none.</summary>
    internal static PdfDictionary? Reference(PdfDictionary signature, string method)
    {
        if (signature.GetArray(ReferenceKey) is not { } references) return null;
        for (var i = 0; i < Math.Min(references.Count, 16); i++)
            if (references.Get(i) is PdfDictionary reference && reference.GetName(TransformMethod)?.Value == method)
                return reference.GetDictionary(TransformParams) ?? new PdfDictionary();
        return null;
    }
}
