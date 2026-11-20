using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Core.Pdf.Signatures;

/// <summary>
/// The declaration of PAdES in a file before PDF 2.0 (docs/FEHLERGEDAECHTNIS.md, No. 364): the signature format ETSI.CAdES.detached, the
/// document security store (/DSS) and document timestamps (ETSI.RFC3161) became part of the standard only with PDF 2.0 (ISO 32000-2,
/// 12.8.3.4, 12.8.4.3); in a file of an earlier version they come from the PAdES extension, which the catalog declares as the developer
/// extension „ESIC“ (ISO 32000-2, 7.12; ETSI EN 319 142-1) — level 2 for CAdES signatures, level 5 for the security store and document
/// timestamps. Found with the reference model of ISO 32000-2, which accepts ETSI.CAdES.detached in a PDF 1.7 file only as that extension.
/// </summary>
internal static class PadesExtension
{
    private static readonly PdfName Extensions = ExtensionDeclarations.Key;

    /// <summary>Signatures (ETSI.CAdES.detached).</summary>
    public const int Signatures = 2;

    /// <summary>The document security store and document timestamps.</summary>
    public const int LongTerm = 5;

    /// <summary>
    /// Declares the extension in the catalog of an update, if the file is older than PDF 2.0 and does not declare it at that level — also
    /// not in an array (not allowed before 2.0, but written by other programs): then nothing changes; otherwise raised, never lowered, the
    /// other declarations kept and /Extensions written directly (docs/FEHLERGEDAECHTNIS.md, No. 488).
    /// </summary>
    /// <param name="catalog">The catalog as the update writes it (a copy).</param>
    /// <param name="doc">The document.</param>
    /// <param name="level">The level needed.</param>
    /// <returns>True if the catalog changed.</returns>
    public static bool Declare(PdfDictionary catalog, PdfDocument doc, int level)
    {
        if (PdfWriter.CompareVersions(doc.Version, "2.0") >= 0) return false;
        var extensions = ExtensionDeclarations.Resolved(catalog.Get(Extensions)) ?? new PdfDictionary();
        if (ExtensionDeclarations.EntriesOf(extensions, "ESIC").Any(e => ExtensionDeclarations.LevelOf(e) >= level)) return false;
        if (!ExtensionDeclarations.Raise(extensions, "ESIC", "1.7", level, doc.Version)) return false;
        catalog.Set(Extensions, extensions);
        return true;
    }

    /// <summary>
    /// Declares ISO/TS 32002 (ECDSA and the other signature methods of that specification) in the catalog of an update of a PDF 2.0 file: an
    /// element of the ISO_ array with base version 2.0, level 32002, revision „:2022“ and its /URL (ISO/TS 32002, clause 4). The other ISO_
    /// declarations stay; one at level 32002 is kept as it is. Nothing below 2.0: the base version would lie above the file's (ISO 32000-2,
    /// 7.12.4) — there ECDSA rests on PAdES alone (docs/FEHLERGEDAECHTNIS.md, No. 536).
    /// </summary>
    /// <param name="catalog">The catalog as the update writes it (a copy).</param>
    /// <param name="doc">The document.</param>
    /// <returns>True if the catalog changed.</returns>
    public static bool DeclareIso32002(PdfDictionary catalog, PdfDocument doc)
    {
        if (PdfWriter.CompareVersions(doc.Version, "2.0") < 0) return false;
        var extensions = ExtensionDeclarations.Resolved(catalog.Get(Extensions)) ?? new PdfDictionary();
        if (!ExtensionDeclarations.Raise(extensions, "ISO_", "2.0", Iso32002, doc.Version, Iso32002Url, ":2022")) return false;
        catalog.Set(Extensions, extensions);
        return true;
    }

    /// <summary>The level of ISO/TS 32002 among the ISO_ declarations.</summary>
    public const int Iso32002 = 32002;

    private const string Iso32002Url = "https://www.iso.org/standard/45875.html";

    /// <summary>
    /// Whether a change of the catalog's /Extensions after a signature only declares extensions (the developer extensions of PAdES or of
    /// Adobe raised or added, as one dictionary or an array; an ISO_ array that only grew; nothing else changed): harmless, it changes
    /// nothing that is shown or filled in (No. 364, 488).
    /// </summary>
    public static bool OnlyDeclarations(PdfDictionary before, PdfDictionary after)
    {
        var old = ExtensionDeclarations.Resolved(before.Get(Extensions));
        if (ExtensionDeclarations.Resolved(after.Get(Extensions)) is not { } now) return old is null;
        foreach (var (key, value) in now.Entries)
        {
            if (key.Value == "Type" && value is PdfName { Value: "Extensions" }) continue;   // the optional type of the dictionary (ISO 32000-2, Table 47)
            if (key.Value is "ESIC" or "ADBE")
            {
                var entries = value switch { PdfDictionary one => [one], PdfArray list => list.ToList(), _ => [] };
                if (entries.Count == 0 || entries.Any(e => ExtensionDeclarations.LevelOf(e) <= 0)) return false;
            }
            else if (key.Value == "ISO_" && value is PdfArray grown && old?.Get(key) is null or PdfArray or PdfNull)
            {
                // declared levels only, and the declarations of before all still there (each as written), new ones beside them
                if (grown.Any(e => ExtensionDeclarations.LevelOf(e) <= 0)) return false;
                var written = grown.Select(e => e.ToString()).ToList();
                if (old?.Get(key) is PdfArray was && was.Any(e => !written.Remove(e.ToString()))) return false;
            }
            else if (old?.Get(key) is not { } was || !ReferenceEquals(was, value) && was.ToString() != value.ToString()) return false;
        }
        if (old is not null)
            foreach (var (key, _) in old.Entries)
                if (!now.ContainsKey(key)) return false;
        return true;
    }
}
