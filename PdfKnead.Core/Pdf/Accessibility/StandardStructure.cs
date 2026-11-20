using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Accessibility;

/// <summary>The structure namespace a type belongs to (ISO 32000-2, 14.8.6): the two standard ones, MathML, or any other.</summary>
internal enum StructureSpace
{
    /// <summary>The standard structure namespace for PDF 1.7, the default for elements without /NS (ISO 32000-2, 14.8.6.1).</summary>
    Pdf17,

    /// <summary>The standard structure namespace for PDF 2.0.</summary>
    Pdf20,

    /// <summary>MathML 3.0, the one other namespace that needs no role map (ISO 32000-2, 14.8.6.3).</summary>
    MathML,

    /// <summary>Any other namespace: its types count only through a role map.</summary>
    Custom,
}

/// <summary>A standard structure type: its namespace and name (H1, H2 … as they stand).</summary>
internal readonly record struct StandardType(StructureSpace Space, string Name);

/// <summary>
/// The standard structure types of the two standard namespaces (ISO 32000-1, 14.8.4; ISO 32000-2, 14.8.4 and Annex M) and the resolution of a
/// structure element to the standard type it stands for, through /RoleMap and /RoleMapNS (ISO 32000-2, 14.7.3, 14.7.4.2, 14.8.6.2). The one
/// place that knows these lists: the PDF/A check, the writer, the redactor and the accessibility check use it.
/// </summary>
internal static class StandardStructure
{
    /// <summary>The name of the standard structure namespace for PDF 1.7.</summary>
    public const string Pdf17Namespace = "http://iso.org/pdf/ssn";

    /// <summary>The name of the standard structure namespace for PDF 2.0.</summary>
    public const string Pdf20Namespace = "http://iso.org/pdf2/ssn";

    /// <summary>The name of the MathML 3.0 namespace.</summary>
    public const string MathMLNamespace = "http://www.w3.org/1998/Math/MathML";

    /// <summary>The types both standard namespaces define (H1 to H6 apart, see <see cref="IsStandard"/>).</summary>
    private static readonly HashSet<string> Shared = new(["Document", "Part", "Sect", "Div", "NonStruct", "P", "H", "Caption", "L", "LI", "Lbl",
        "LBody", "Table", "TR", "TH", "TD", "THead", "TBody", "TFoot", "Span", "Link", "Annot", "Ruby", "RB", "RT", "RP", "Warichu", "WT", "WP",
        "Figure", "Formula", "Form"], StringComparer.Ordinal);

    /// <summary>The types only the namespace for PDF 1.7 defines (ISO 32000-2, Annex M).</summary>
    private static readonly HashSet<string> Pdf17Only = new(["Art", "BlockQuote", "TOC", "TOCI", "Index", "Private", "Quote", "Note", "Reference",
        "BibEntry", "Code"], StringComparer.Ordinal);

    /// <summary>The types only the namespace for PDF 2.0 defines (ISO 32000-2, Annex M; Hn with n above 6 apart).</summary>
    private static readonly HashSet<string> Pdf20Only = new(["DocumentFragment", "Aside", "Title", "FENote", "Sub", "Em", "Strong", "Artifact"],
        StringComparer.Ordinal);

    private static readonly PdfName S = PdfName.Get("S"), Ns = PdfName.Get("NS"), RoleMap = PdfName.Get("RoleMap"), RoleMapNs = PdfName.Get("RoleMapNS");

    /// <summary>Whether <paramref name="type"/> is a standard structure type of <paramref name="space"/>; MathML types all count.</summary>
    public static bool IsStandard(string type, StructureSpace space)
    {
        if (space == StructureSpace.MathML) return type.Length > 0;
        if (space == StructureSpace.Custom) return false;
        if (Shared.Contains(type)) return true;
        if (Heading(type) is { } level) return level <= 6 || space == StructureSpace.Pdf20;
        return space == StructureSpace.Pdf17 ? Pdf17Only.Contains(type) : Pdf20Only.Contains(type);
    }

    /// <summary>
    /// The type of PDF 1.7 a standard type of the namespace for PDF 2.0 is written as where a copy is 1.7 or older (docs/ABLAUFPLAN.md, 6.5
    /// step 13; decided on 8.11.2026). A definition of PDF-Knead's own — ISO/TS 32005 gives no mapping between the namespaces —:
    /// the types both define stay, Title → P, FENote → Note, DocumentFragment → Part, Aside → Div, Sub, Em and Strong → Span, Hn with n above
    /// 6 → H6. Null for Artifact (content that is no part of the document: it becomes artifact content, not an element) and for any other
    /// name.
    /// </summary>
    public static string? Pdf17Equivalent(string type)
    {
        if (Shared.Contains(type)) return type;
        if (Heading(type) is { } level) return level <= 6 ? type : "H6";
        return type switch
        {
            "Title" => "P",
            "FENote" => "Note",
            "DocumentFragment" => "Part",
            "Aside" => "Div",
            "Sub" or "Em" or "Strong" => "Span",
            _ => null,
        };
    }

    /// <summary>The level of a numbered heading (H1, H2 …; not H0 or H01), or null.</summary>
    private static int? Heading(string type) =>
        type.Length is >= 2 and <= 6 && type[0] == 'H' && type[1] != '0' && type.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0
            ? int.Parse(type.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture)
            : null;

    /// <summary>The namespace a namespace dictionary names (/NS); no dictionary: the default namespace, PDF 1.7.</summary>
    public static StructureSpace SpaceOf(PdfDictionary? ns)
    {
        if (ns is null) return StructureSpace.Pdf17;
        return ns.GetString(Ns)?.ToText() switch
        {
            Pdf17Namespace => StructureSpace.Pdf17,
            Pdf20Namespace => StructureSpace.Pdf20,
            MathMLNamespace => StructureSpace.MathML,
            _ => StructureSpace.Custom,
        };
    }

    /// <summary>
    /// The standard type a structure element stands for. Its /S in its namespace (/NS; without one the default namespace, PDF 1.7) is mapped
    /// through the role map of that namespace — /RoleMapNS of the namespace dictionary, /RoleMap of the structure tree root for the default
    /// one — whenever the map has an entry, even for a standard type (ISO 32000-2, 14.7.3); after that the chain is followed until it reaches a
    /// standard type. A single name in /RoleMapNS names a type of the default namespace, an array a type and its namespace (Table 356).
    /// </summary>
    /// <param name="element">The structure element.</param>
    /// <param name="root">The structure tree root (its /RoleMap); null: no role map.</param>
    /// <param name="resolve">
    /// Resolves the references met on the way (the namespace, the role maps); null: as the dictionaries resolve them themselves. The writer
    /// passes its own, as copies it made of a dictionary resolve nothing.
    /// </param>
    /// <returns>The standard type; null if the element has no type or its chain ends in a type of no standard namespace or runs in a circle.</returns>
    public static StandardType? Resolve(PdfDictionary element, PdfDictionary? root, Func<PdfObject?, PdfObject?>? resolve = null)
    {
        PdfObject? Entry(PdfDictionary? owner, PdfName key) => owner is null ? null : resolve is null ? owner.Get(key) : resolve(owner.GetRaw(key));
        PdfObject? Item(PdfArray array, int index) => resolve is null ? array.Get(index) : resolve(array[index]);
        PdfDictionary? Dictionary(PdfDictionary? owner, PdfName key) => Entry(owner, key) as PdfDictionary;
        if (Entry(element, S) is not PdfName { Value: var type }) return null;
        var ns = Dictionary(element, Ns);
        var seen = new HashSet<(string, PdfDictionary?)>();
        for (var step = 0; step < 64; step++)
        {
            var space = SpaceOf(ns);
            // a circle without a standard type on it (ISO 32000-2, 14.7.3, note 2): nothing a processor recognises
            if (!seen.Add((type, ns))) return null;
            var map = ns is null ? Dictionary(root, RoleMap) : Dictionary(ns, RoleMapNs);
            switch (Entry(map, PdfName.Get(type)))
            {
                case PdfName name:
                    type = name.Value;
                    ns = null;
                    break;
                case PdfArray { Count: >= 1 } target when Item(target, 0) is PdfName name:
                    type = name.Value;
                    ns = target.Count >= 2 ? Item(target, 1) as PdfDictionary : null;
                    break;
                default:
                    return IsStandard(type, space) ? new StandardType(space, type) : null;
            }
            if (IsStandard(type, SpaceOf(ns))) return new StandardType(SpaceOf(ns), type);
        }
        return null;
    }
}
