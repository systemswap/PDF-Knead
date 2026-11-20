using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace PdfKnead.Core.Scanning.Escl;

/// <summary>
/// Reads the XML a scanner sends (KONZEPT.md §5.25, S9, „XML vom Gerät“) — untrusted input like any file: size limited before parsing, no
/// document type processing (no entities, no external resources), comments and processing instructions dropped. Elements are found by their
/// local names only and without regard to case: devices mix up the prefixes of the two namespaces involved and older ones use other namespace
/// addresses. A document type declaration — some devices send one, some in lower case, which XML does not allow — is cut out before parsing;
/// the parser itself then refuses any that remains. Entities it declared stay undefined, so a document that uses them is refused.
/// </summary>
public static class EsclXml
{
    /// <summary>The deepest nesting accepted: answers of real devices nest about eight levels.</summary>
    public const int MaxDepth = 64;

    /// <summary>Parses an answer.</summary>
    /// <param name="data">The bytes as received.</param>
    /// <param name="max">The most bytes accepted.</param>
    /// <returns>The root element, or null if the answer is too large, nested deeper than <see cref="MaxDepth"/> or not well-formed XML.</returns>
    public static XElement? Parse(ReadOnlySpan<byte> data, int max)
    {
        if (data.Length == 0 || data.Length > max) return null;
        try
        {
            var bytes = WithoutDocumentType(data);
            // a first pass measures the nesting before a tree is built: deep trees cost stack wherever they are walked
            using (var probe = Reader(bytes, max))
                while (probe.Read())
                    if (probe.Depth > MaxDepth) return null;
            using var reader = Reader(bytes, max);
            return XDocument.Load(reader, LoadOptions.None).Root;
        }
        catch (Exception e) when (e is XmlException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static XmlReader Reader(byte[] bytes, int max) => XmlReader.Create(new MemoryStream(bytes, writable: false), new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = max,
        MaxCharactersFromEntities = 0,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    });

    /// <summary>
    /// A name or text from the device for display: control characters and the characters that reorder text (with which a name could pass
    /// itself off as another) removed, blanks collapsed, at most <paramref name="max"/> characters.
    /// </summary>
    public static string Clean(string text, int max = 128)
    {
        var b = new System.Text.StringBuilder(Math.Min(text.Length, max));
        foreach (var c in text)
        {
            if (b.Length >= max) break;
            if (char.IsWhiteSpace(c))
            {
                if (b.Length > 0 && b[^1] != ' ') b.Append(' ');   // line breaks and tabs become one blank
                continue;
            }
            if (char.IsControl(c) || c is '\u200E' or '\u200F' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069') or '\u061C' or '\uFEFF')
                continue;
            b.Append(c);
        }
        return b.ToString().TrimEnd();
    }

    /// <summary>The bytes without the first document type declaration (any case), including an internal subset in brackets.</summary>
    private static byte[] WithoutDocumentType(ReadOnlySpan<byte> data)
    {
        var start = IndexOfIgnoreCase(data, "<!doctype"u8);
        if (start < 0) return data.ToArray();
        var at = start + 9;
        var depth = 0;
        for (; at < data.Length; at++)
        {
            if (data[at] == '[') depth++;
            else if (data[at] == ']') depth = Math.Max(0, depth - 1);
            else if (data[at] == '>' && depth == 0) break;
        }
        if (at >= data.Length) return data.ToArray();   // not closed: left for the parser to refuse
        return [.. data[..start], .. data[(at + 1)..]];
    }

    private static int IndexOfIgnoreCase(ReadOnlySpan<byte> data, ReadOnlySpan<byte> lower)
    {
        for (var i = 0; i + lower.Length <= data.Length; i++)
        {
            var k = 0;
            while (k < lower.Length && (data[i + k] | (char.IsAsciiLetter((char)lower[k]) ? 0x20 : 0)) == lower[k]) k++;
            if (k == lower.Length) return i;
        }
        return -1;
    }

    /// <summary>Whether an element has the local name <paramref name="name"/> (any namespace, any case).</summary>
    public static bool Is(this XElement element, string name) => string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The first child element with the local name <paramref name="name"/>, or null.</summary>
    public static XElement? Child(this XElement? element, string name) => element?.Elements().FirstOrDefault(e => e.Is(name));

    /// <summary>All child elements with the local name <paramref name="name"/>.</summary>
    public static IEnumerable<XElement> Children(this XElement? element, string name) => element?.Elements().Where(e => e.Is(name)) ?? [];

    /// <summary>The trimmed text of the first child element with the local name <paramref name="name"/>, or null if missing or empty.</summary>
    public static string? Text(this XElement? element, string name) => element.Child(name)?.Value.Trim() is { Length: > 0 } t ? t : null;

    /// <summary>The value of the first child element with the local name <paramref name="name"/> as a whole number, or null.</summary>
    /// <remarks>Read tolerantly: surrounding blanks and a decimal fraction („300.0“) are accepted, other text yields null.</remarks>
    public static int? Int(this XElement? element, string name) => ToInt(element.Text(name));

    /// <summary>The value of an attribute by its local name (any namespace, any case), or null.</summary>
    public static string? AttributeValue(this XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => !a.IsNamespaceDeclaration && string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value.Trim();

    /// <summary>A whole number from a device's text; null if it is none or outside ±1 000 000 000.</summary>
    public static int? ToInt(string? text)
    {
        if (text is null) return null;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return Math.Abs(i) <= 1_000_000_000 ? i : null;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) && Math.Abs(d) <= 1e9
            ? (int)Math.Round(d) : null;
    }

    /// <summary>The XML shortened for the diagnosis log.</summary>
    public static string ForLog(ReadOnlySpan<byte> data)
    {
        var text = System.Text.Encoding.UTF8.GetString(data[..Math.Min(data.Length, EsclDiagnostics.MaxText)]);
        return data.Length > EsclDiagnostics.MaxText ? text + " …" : text;
    }
}
