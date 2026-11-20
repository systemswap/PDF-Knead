using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// What a file says about itself in the XMP of its catalogue (docs/ABLAUFPLAN.md, 6.1 step 6; docs/FEHLERGEDAECHTNIS.md, No. 614): the PDF/A
/// identification (ISO 19005), the PDF/UA identification (ISO 14289, clause 5) and the PDF Declarations (<c>pdfd:conformsTo</c>) — the file's
/// own statements, not checked.
/// </summary>
/// <param name="PdfA">The PDF/A part and level declared; null if none.</param>
/// <param name="PdfUaPart">The PDF/UA part; null if none.</param>
/// <param name="Declarations">The URIs of the declarations, as written (trimmed).</param>
public sealed record ConformanceClaims(PdfAConformance? PdfA, int? PdfUaPart, IReadOnlyList<string> Declarations)
{
    /// <summary>Reads the claims of a document; null if it makes none (or its XMP cannot be read).</summary>
    /// <param name="doc">The document.</param>
    /// <returns>The claims.</returns>
    public static ConformanceClaims? Read(PdfDocument doc)
    {
        AccessibilityClaims.Found? found = null;
        if (doc.Catalog?.Get(PdfName.Metadata) is PdfStream { DataLength: <= 16 << 20 } stream)
        {
            try
            {
                found = AccessibilityClaims.Read(doc.GetStreamData(stream));
            }
            catch (PdfException)
            {
            }
        }
        var pdfA = PdfACheck.Declared(doc);
        if (pdfA is null && found is not { PdfUaPart: not null } && found is not { ConformsTo.Count: > 0 }) return null;
        return new ConformanceClaims(pdfA, found?.PdfUaPart, found?.ConformsTo ?? []);
    }

    /// <summary>
    /// The name of a declaration the user knows (WTPDF, WCAG, ISO/TS 32005), read tolerantly like <see cref="AccessibilityClaims.IsAccessibility"/>;
    /// null for another.
    /// </summary>
    /// <param name="uri">The URI.</param>
    /// <returns>The name, or null.</returns>
    public static string? Label(string uri)
    {
        var u = uri.Trim().ToLowerInvariant().Replace("/#", "#", StringComparison.Ordinal);
        if (u.EndsWith("declarations/wtpdf#accessibility1.0", StringComparison.Ordinal)) return Tr.T("WTPDF 1.0 barrierefrei");
        if (u.EndsWith("declarations/wtpdf#reuse1.0", StringComparison.Ordinal)) return Tr.T("WTPDF 1.0 wiederverwendbar");
        if (u.EndsWith("#iso32005", StringComparison.Ordinal)) return "ISO/TS 32005";
        // #wcag22aa → WCAG 2.2 AA (the fragment WTPDF uses as its example)
        var hash = u.LastIndexOf("#wcag", StringComparison.Ordinal);
        if (hash >= 0 && u[(hash + 5)..] is { Length: >= 3 } rest && char.IsAsciiDigit(rest[0]) && char.IsAsciiDigit(rest[1]) && rest[2..].All(c => c == 'a'))
            return $"WCAG {rest[0]}.{rest[1]} {rest[2..].ToUpperInvariant()}";
        return null;
    }

    /// <summary>
    /// The claims in one line, such as „PDF/A-2b · PDF/UA-1 · WTPDF 1.0 barrierefrei · 1 weitere“ (unknown declarations counted).
    /// </summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (PdfA is { } pdfA) parts.Add(pdfA.Name());
            if (PdfUaPart is { } ua) parts.Add($"PDF/UA-{ua}");
            var unknown = 0;
            foreach (var uri in Declarations)
            {
                if (Label(uri) is { } label) parts.Add(label);
                else unknown++;
            }
            if (unknown > 0) parts.Add(Tr.T("{0} weitere", unknown));
            return string.Join(" · ", parts);
        }
    }
}
