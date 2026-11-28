using System.Globalization;
using System.Security;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// The XMP metadata of a PDF/A file (ISO 19005-2, 6.6): the identification of the part and level, and the entries of the information
/// dictionary repeated in the predefined schemas (Table 3), what the source carries over, and the extension schemas this needs.
/// </summary>
internal static class PdfAMetadata
{
    /// <summary>The packet for the catalogue's /Metadata.</summary>
    /// <param name="part">The PDF/A part.</param>
    /// <param name="info">The information dictionary as written (see <see cref="PdfWriter"/>).</param>
    /// <param name="carried">Properties taken over from the source's packet, each a self-contained XML element (docs/ABLAUFPLAN.md, 6.1 step 7).</param>
    /// <param name="pdfUaExtension">The carried properties include the PDF/UA identification, which PDF/A-1 to -3 must declare in an extension
    /// schema (ISO 19005-2, 6.6.2.3.3; ISO 19005-1, 6.7.8).</param>
    /// <param name="languages">
    /// The source's further language versions of dc:title and dc:description (beside x-default, which the information dictionary gives): kept,
    /// since they may be the only place the file names its language (ISO 14289-1, 7.2; docs/FEHLERGEDAECHTNIS.md, No. 677).
    /// </param>
    /// <param name="declarations">The extension schemas of carried properties of schemas the source declared itself, as one rdf:Description
    /// (<see cref="PdfAXmp.CarryableDeclared"/>); null for none.</param>
    /// <returns>The packet in UTF-8.</returns>
    public static byte[] Build(PdfAConformance part, PdfDictionary info, IReadOnlyList<string>? carried = null, bool pdfUaExtension = false,
        IReadOnlyDictionary<string, IReadOnlyList<(string Language, string Text)>>? languages = null, string? declarations = null)
    {
        string Others(string property) =>
            languages?.GetValueOrDefault(property) is { } list
                ? string.Concat(list.Select(l => $"<rdf:li xml:lang=\"{Xml(l.Language)}\">{Xml(l.Text)}</rdf:li>"))
                : "";
        string? Text(string key) => info.GetString(PdfName.Get(key))?.ToText();
        string? Date(string key) => PdfDate.Parse(Text(key)) is { } date ? XmpDate(date) : null;
        var sb = new StringBuilder();
        // the header without "bytes" and "encoding" (6.6.2.2)
        sb.Append("<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"");
        sb.Append(" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">\n");
        sb.Append(CultureInfo.InvariantCulture, $"<pdfaid:part>{part.Part()}</pdfaid:part>\n");
        if (part.Level() is { } level) sb.Append(CultureInfo.InvariantCulture, $"<pdfaid:conformance>{level}</pdfaid:conformance>\n");
        if (part.Part() == 4) sb.Append("<pdfaid:rev>2020</pdfaid:rev>\n");
        sb.Append("<dc:format>application/pdf</dc:format>\n");
        if (Text("Title") is { } title) sb.Append($"<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{Xml(title)}</rdf:li>{Others("title")}</rdf:Alt></dc:title>\n");
        // dc:creator is a list; the information dictionary's /Author must be its only entry (6.6.2.3.1)
        if (Text("Author") is { } author) sb.Append($"<dc:creator><rdf:Seq><rdf:li>{Xml(author)}</rdf:li></rdf:Seq></dc:creator>\n");
        if (Text("Subject") is { } subject) sb.Append($"<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">{Xml(subject)}</rdf:li>{Others("description")}</rdf:Alt></dc:description>\n");
        if (Text("Keywords") is { } keywords) sb.Append($"<pdf:Keywords>{Xml(keywords)}</pdf:Keywords>\n");
        if (Text("Producer") is { } producer) sb.Append($"<pdf:Producer>{Xml(producer)}</pdf:Producer>\n");
        if (Text("Creator") is { } creator) sb.Append($"<xmp:CreatorTool>{Xml(creator)}</xmp:CreatorTool>\n");
        if (Date("CreationDate") is { } created) sb.Append($"<xmp:CreateDate>{created}</xmp:CreateDate>\n");
        if (Date("ModDate") is { } modified) sb.Append($"<xmp:ModifyDate>{modified}</xmp:ModifyDate>\n<xmp:MetadataDate>{modified}</xmp:MetadataDate>\n");
        sb.Append("</rdf:Description>\n");
        if (carried is { Count: > 0 })
        {
            sb.Append("<rdf:Description rdf:about=\"\">\n");
            foreach (var property in carried) sb.Append(property).Append('\n');
            sb.Append("</rdf:Description>\n");
        }
        if (pdfUaExtension) sb.Append(PdfUaExtensionSchema);
        if (declarations is not null) sb.Append(declarations);
        sb.Append("</rdf:RDF>\n</x:xmpmeta>\n<?xpacket end=\"w\"?>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// The extension schema of the PDF/UA identification (ISO 14289-1, 5, Table 1; ISO 14289-2, 5: part and rev integers, amd and corr texts),
    /// declared as PDF/A-1 to -3 require for a schema that is not predefined.
    /// </summary>
    private const string PdfUaExtensionSchema =
        "<rdf:Description rdf:about=\"\" xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\" xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\" " +
        "xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\">\n<pdfaExtension:schemas><rdf:Bag><rdf:li rdf:parseType=\"Resource\">\n" +
        "<pdfaSchema:schema>PDF/UA Universal Accessibility Schema</pdfaSchema:schema>\n" +
        "<pdfaSchema:namespaceURI>http://www.aiim.org/pdfua/ns/id/</pdfaSchema:namespaceURI>\n<pdfaSchema:prefix>pdfuaid</pdfaSchema:prefix>\n" +
        "<pdfaSchema:property><rdf:Seq>\n" +
        "<rdf:li rdf:parseType=\"Resource\"><pdfaProperty:name>part</pdfaProperty:name><pdfaProperty:valueType>Integer</pdfaProperty:valueType>" +
        "<pdfaProperty:category>internal</pdfaProperty:category><pdfaProperty:description>Part of ISO 14289 the file conforms to</pdfaProperty:description></rdf:li>\n" +
        "<rdf:li rdf:parseType=\"Resource\"><pdfaProperty:name>rev</pdfaProperty:name><pdfaProperty:valueType>Integer</pdfaProperty:valueType>" +
        "<pdfaProperty:category>internal</pdfaProperty:category><pdfaProperty:description>Year of the edition of the part</pdfaProperty:description></rdf:li>\n" +
        "<rdf:li rdf:parseType=\"Resource\"><pdfaProperty:name>amd</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType>" +
        "<pdfaProperty:category>internal</pdfaProperty:category><pdfaProperty:description>Amendment of the part</pdfaProperty:description></rdf:li>\n" +
        "<rdf:li rdf:parseType=\"Resource\"><pdfaProperty:name>corr</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType>" +
        "<pdfaProperty:category>internal</pdfaProperty:category><pdfaProperty:description>Corrigendum of the part</pdfaProperty:description></rdf:li>\n" +
        "</rdf:Seq></pdfaSchema:property>\n</rdf:li></rdf:Bag></pdfaExtension:schemas>\n</rdf:Description>\n";

    /// <summary>A date as XMP writes it (ISO 8601 with the offset), the same instant as the PDF date.</summary>
    internal static string XmpDate(DateTimeOffset date)
    {
        var offset = date.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"{date:yyyy-MM-ddTHH:mm:ss}{sign}{offset.Hours:00}:{offset.Minutes:00}");
    }

    /// <summary>A text without the characters XML 1.0 cannot hold (controls except tab and line breaks, unpaired surrogates, U+FFFE/U+FFFF).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text with those characters left out.</returns>
    public static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(c).Append(text[++i]);
                continue;
            }
            if (char.IsSurrogate(c) || c is '￾' or '￿' || c < 0x20 && c is not ('\t' or '\n' or '\r')) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A text as XML content: the markup characters escaped, and a carriage return as a character reference — a reader of XML turns a line
    /// end written as it is into a line feed, and the text would no longer equal the information dictionary's (PDF/A-1, 6.7.3).
    /// </summary>
    private static string Xml(string text) => SecurityElement.Escape(text).Replace("\r", "&#xD;", StringComparison.Ordinal);
}
