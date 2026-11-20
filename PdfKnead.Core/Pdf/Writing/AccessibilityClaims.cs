using System.Xml;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// What a file's XMP claims about accessibility: the PDF/UA identification (<c>pdfuaid:part</c>, with PDF/UA-2 <c>pdfuaid:rev</c>; ISO 14289-1
/// and -2, clause 5) and the PDF Declarations (<c>pdfd:declarations</c>, each with a <c>pdfd:conformsTo</c> URI — WTPDF, ISO/TS 32005 and
/// others). After a change that breaks accessibility the claims about it are removed; other declarations stay, PDF Knead cannot check them
/// (docs/ABLAUFPLAN.md, 6.1; decision G: removed without asking, said afterwards).
/// </summary>
internal static class AccessibilityClaims
{
    /// <summary>The namespace of the PDF/UA identification.</summary>
    public const string PdfUaId = "http://www.aiim.org/pdfua/ns/id/";

    /// <summary>The namespace of the PDF Declarations.</summary>
    public const string Declarations = "http://pdfa.org/declarations/";

    /// <summary>The claims of a packet.</summary>
    /// <param name="PdfUaPart">The PDF/UA part (1, 2); null without identification.</param>
    /// <param name="ConformsTo">The URIs of the declarations, as written (trimmed).</param>
    public sealed record Found(int? PdfUaPart, IReadOnlyList<string> ConformsTo);

    /// <summary>Reads the claims; null if the packet is not well-formed XMP.</summary>
    /// <param name="packet">The XMP packet.</param>
    /// <returns>The claims.</returns>
    public static Found? Read(byte[] packet)
    {
        if (PdfAXmp.Read(packet) is not { } xml) return null;
        int? part = null;
        foreach (var element in xml.GetElementsByTagName("*").OfType<XmlElement>())
        {
            // element form <pdfuaid:part>1</pdfuaid:part> or attribute form pdfuaid:part="1"
            if (element.NamespaceURI == PdfUaId && element.LocalName == "part" && int.TryParse(element.InnerText.Trim(), out var p)) part = p;
            if (element.GetAttributeNode("part", PdfUaId) is { } attribute && int.TryParse(attribute.Value.Trim(), out var q)) part = q;
        }
        var uris = xml.GetElementsByTagName("conformsTo", Declarations).OfType<XmlElement>().Select(e => e.InnerText.Trim()).Where(u => u.Length > 0).ToList();
        return new Found(part, uris);
    }

    /// <summary>
    /// Whether a declaration claims accessibility (and so goes with a break): WTPDF (accessibility and reuse), ISO/TS 32005, anything naming
    /// WCAG or PDF/UA. Read tolerantly — white space around it, http and https alike, the slash before „#“ optional (WTPDF writes
    /// „…/wtpdf/#accessibility1.0“, files in circulation „…/wtpdf#accessibility1.0“).
    /// </summary>
    /// <param name="uri">The URI.</param>
    /// <returns>True for an accessibility declaration.</returns>
    public static bool IsAccessibility(string uri)
    {
        var u = uri.Trim().ToLowerInvariant().Replace("/#", "#", StringComparison.Ordinal);
        return u.Contains("declarations/wtpdf", StringComparison.Ordinal) || u.Contains("#iso32005", StringComparison.Ordinal) ||
               u.Contains("wcag", StringComparison.Ordinal) || u.Contains("pdfua", StringComparison.Ordinal);
    }

    /// <summary>
    /// The packet without the accessibility claims: the properties of <c>pdfuaid</c> (element and attribute form, with their namespace
    /// declarations), and of <c>pdfd:declarations</c> the entries that claim accessibility — the whole property if none is left. The rest as
    /// it was. Null if the packet is not well-formed XMP.
    /// </summary>
    /// <param name="packet">The XMP packet.</param>
    /// <param name="removed">Whether anything was removed.</param>
    /// <returns>The packet; unchanged content if there was nothing to remove.</returns>
    public static byte[]? Remove(byte[] packet, out bool removed)
    {
        removed = false;
        if (PdfAXmp.Read(packet) is not { } xml || xml.GetElementsByTagName("RDF", XmpSchemas.Rdf) is not { Count: > 0 }) return null;
        foreach (var element in xml.GetElementsByTagName("*").OfType<XmlElement>().ToList())
        {
            if (element.NamespaceURI == PdfUaId)
            {
                element.ParentNode?.RemoveChild(element);
                removed = true;
                continue;
            }
            foreach (var attribute in element.Attributes.OfType<XmlAttribute>().ToList())
            {
                if (attribute.NamespaceURI == PdfUaId) removed = true;
                if (attribute.NamespaceURI == PdfUaId || attribute.Prefix == "xmlns" && attribute.Value == PdfUaId) element.Attributes.Remove(attribute);
            }
        }
        foreach (var declarations in xml.GetElementsByTagName("declarations", Declarations).OfType<XmlElement>().ToList())
        {
            // each entry (rdf:li) of the bag with its conformsTo; an entry claiming accessibility goes
            foreach (var conformsTo in declarations.GetElementsByTagName("conformsTo", Declarations).OfType<XmlElement>().ToList())
            {
                if (!IsAccessibility(conformsTo.InnerText)) continue;
                var entry = conformsTo.ParentNode as XmlElement;
                while (entry is not null && !(entry.LocalName == "li" && entry.NamespaceURI == XmpSchemas.Rdf)) entry = entry.ParentNode as XmlElement;
                (entry ?? conformsTo).ParentNode?.RemoveChild(entry ?? conformsTo);
                removed = true;
            }
            if (declarations.GetElementsByTagName("conformsTo", Declarations).Count == 0) declarations.ParentNode?.RemoveChild(declarations);
        }
        return removed ? XmpEditor.Write(xml, packet) : packet;
    }
}
