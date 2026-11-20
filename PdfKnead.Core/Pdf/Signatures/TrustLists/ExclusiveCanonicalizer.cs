using System.Text;
using System.Xml;

namespace PdfKnead.Core.Pdf.Signatures.TrustLists;

/// <summary>
/// Exclusive XML Canonicalization 1.0 without comments (W3C Recommendation of 18 July 2002, http://www.w3.org/2001/10/xml-exc-c14n#), as
/// the trusted lists sign with it: the bytes over which an XML signature is computed. Covers what a parsed document holds once DTDs are
/// refused — elements, attributes, text, CDATA, processing instructions; comments are left out. No InclusiveNamespaces prefix list (the
/// trusted lists use none).
/// </summary>
/// <remarks>
/// Rules (C14N 1.0, section 2.3, with the exclusive changes of section 3): UTF-8; start and end tag for every element; namespace declarations
/// only where a prefix is visibly utilized (by the element or one of its attributes) and not already rendered with the same value by an
/// output ancestor, sorted by prefix with the default namespace first; attributes sorted by namespace URI, then local name; characters escaped
/// as the recommendation lists. Nesting deeper than <see cref="MaxDepth"/> is refused (prepared lists).
/// </remarks>
public static class ExclusiveCanonicalizer
{
    /// <summary>The algorithm identifier.</summary>
    public const string Algorithm = "http://www.w3.org/2001/10/xml-exc-c14n#";

    private const string XmlnsUri = "http://www.w3.org/2000/xmlns/";

    /// <summary>Deepest nesting accepted.</summary>
    public const int MaxDepth = 256;

    /// <summary>The canonical form of a node: an element with its subtree, or a whole document.</summary>
    /// <param name="node">An element or a document.</param>
    /// <param name="omit">An element left out with its subtree (the signature for the enveloped-signature transform); null for none.</param>
    /// <returns>The UTF-8 bytes.</returns>
    /// <exception cref="XmlException">The nesting is too deep.</exception>
    public static byte[] Canonicalize(XmlNode node, XmlElement? omit = null)
    {
        var output = new StringBuilder();
        var rendered = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is XmlDocument document)
        {
            var seenRoot = false;
            foreach (XmlNode child in document.ChildNodes)
            {
                switch (child)
                {
                    case XmlElement element:
                        Element(element, omit, rendered, output, 0);
                        seenRoot = true;
                        break;
                    case XmlProcessingInstruction instruction:
                        // section 2.3: a line break between the document's top-level nodes
                        if (seenRoot) output.Append('\n');
                        Instruction(instruction, output);
                        if (!seenRoot) output.Append('\n');
                        break;
                }
            }
        }
        else if (node is XmlElement element)
        {
            Element(element, omit, rendered, output, 0);
        }
        else
        {
            throw new ArgumentException("An element or a document is expected.", nameof(node));
        }
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    private static void Element(XmlElement element, XmlElement? omit, Dictionary<string, string> rendered, StringBuilder output, int depth)
    {
        if (ReferenceEquals(element, omit)) return;
        if (depth > MaxDepth) throw new XmlException(Tr.T("Die XML-Datei ist zu tief verschachtelt."));
        output.Append('<').Append(element.Name);

        // namespace declarations: the visibly utilized prefixes that the output ancestors do not already declare the same way
        var utilized = new SortedDictionary<string, string>(StringComparer.Ordinal) { [element.Prefix] = element.NamespaceURI };
        foreach (XmlAttribute attribute in element.Attributes)
            if (attribute.NamespaceURI != XmlnsUri && attribute.Prefix.Length > 0 && attribute.Prefix != "xml")
                utilized[attribute.Prefix] = attribute.NamespaceURI;
        List<(string Prefix, string? Before)>? undo = null;
        foreach (var (prefix, uri) in utilized)
        {
            var before = rendered.GetValueOrDefault(prefix);
            // an empty default namespace needs xmlns="" only to undo a non-empty one rendered above
            if (before == uri || (prefix.Length == 0 && uri.Length == 0 && before is null)) continue;
            output.Append(prefix.Length == 0 ? " xmlns=\"" : $" xmlns:{prefix}=\"");
            Escape(uri, output, attribute: true);
            output.Append('"');
            (undo ??= []).Add((prefix, before));
            rendered[prefix] = uri;
        }

        var attributes = new List<XmlAttribute>(element.Attributes.Count);
        foreach (XmlAttribute attribute in element.Attributes)
            if (attribute.NamespaceURI != XmlnsUri) attributes.Add(attribute);
        attributes.Sort((a, b) =>
        {
            var byUri = string.CompareOrdinal(a.NamespaceURI, b.NamespaceURI);
            return byUri != 0 ? byUri : string.CompareOrdinal(a.LocalName, b.LocalName);
        });
        foreach (var attribute in attributes)
        {
            output.Append(' ').Append(attribute.Name).Append("=\"");
            Escape(attribute.Value, output, attribute: true);
            output.Append('"');
        }
        output.Append('>');

        foreach (XmlNode child in element.ChildNodes)
        {
            switch (child)
            {
                case XmlElement inner:
                    Element(inner, omit, rendered, output, depth + 1);
                    break;
                case XmlText or XmlCDataSection or XmlWhitespace or XmlSignificantWhitespace:
                    Escape(child.Value ?? "", output, attribute: false);
                    break;
                case XmlProcessingInstruction instruction:
                    Instruction(instruction, output);
                    break;
            }
        }
        output.Append("</").Append(element.Name).Append('>');

        if (undo is null) return;
        foreach (var (prefix, before) in undo)
        {
            if (before is null) rendered.Remove(prefix);
            else rendered[prefix] = before;
        }
    }

    private static void Instruction(XmlProcessingInstruction instruction, StringBuilder output)
    {
        output.Append("<?").Append(instruction.Target);
        if (instruction.Data.Length > 0) output.Append(' ').Append(instruction.Data);
        output.Append("?>");
    }

    private static void Escape(string text, StringBuilder output, bool attribute)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': output.Append("&amp;"); break;
                case '<': output.Append("&lt;"); break;
                case '>' when !attribute: output.Append("&gt;"); break;
                case '"' when attribute: output.Append("&quot;"); break;
                case '\t' when attribute: output.Append("&#x9;"); break;
                case '\n' when attribute: output.Append("&#xA;"); break;
                case '\r': output.Append("&#xD;"); break;
                default: output.Append(c); break;
            }
        }
    }
}
