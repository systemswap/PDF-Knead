using System.Globalization;
using System.Security;
using System.Text;
using System.Xml;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// Changes to the document's XMP packet (docs/ABLAUFPLAN.md, 3.3 a; extended in phase 6.1 by the modification date, pdfuaid and
/// declarations): read as XML (<see cref="PdfAXmp.Read"/>, no regular expressions), changed in place, written in UTF-8; and a slim packet
/// for a file that has none (docs/FEHLERGEDAECHTNIS.md, No. 492).
/// </summary>
internal static class XmpEditor
{
    private const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#", Dc = "http://purl.org/dc/elements/1.1/", XmlNs = "http://www.w3.org/XML/1998/namespace";
    private const string Xmp = "http://ns.adobe.com/xap/1.0/", Pdf = "http://ns.adobe.com/pdf/1.3/";
    private const string MediaManagement = "http://ns.adobe.com/xap/1.0/mm/", Image = "http://ns.adobe.com/xap/1.0/g/img/";

    /// <summary>
    /// The packet with <c>xmp:ModifyDate</c> and <c>xmp:MetadataDate</c> set to <paramref name="modified"/> and <c>pdf:Producer</c> to
    /// <paramref name="producer"/> — written as elements of the first description, an earlier value in element or attribute form removed, the
    /// rest (other schemas included) as it was. The dates of the information dictionary and of the XMP must agree when both are written
    /// (ISO 32000-2, 14.3.4; PDF/A-1 checks it, ISO 19005-1, 6.7.3; docs/FEHLERGEDAECHTNIS.md, No. 552 and 613); the metadata date is not
    /// before the modification date (XMP part 1, 8.4). A packet with identifiers of the media management schema gets a new
    /// <c>xmpMM:InstanceID</c> — each saved state is an instance of its own (XMP part 1, 8.6); the document identifier stays. Null if the
    /// packet is not well-formed XMP — then it stays as it is.
    /// </summary>
    public static byte[]? SetSaved(byte[] packet, DateTimeOffset modified, string producer)
    {
        if (PdfAXmp.Read(packet) is not { } xml || xml.GetElementsByTagName("RDF", Rdf) is not { Count: > 0 } rdfs || rdfs[0] is not XmlElement rdf) return null;
        var date = PdfAMetadata.XmpDate(modified);
        var description = FirstDescription(xml, rdf);
        var oldInstance = Simple(rdf, MediaManagement, "InstanceID");
        var document = Simple(rdf, MediaManagement, "DocumentID");
        foreach (var (ns, prefix, name, value) in (ReadOnlySpan<(string, string, string, string)>)[(Xmp, "xmp", "ModifyDate", date), (Xmp, "xmp", "MetadataDate", date), (Pdf, "pdf", "Producer", producer)])
            Replace(xml, rdf, description, ns, prefix, name).InnerText = PdfAMetadata.Clean(value);
        if (oldInstance is not null || document is not null)
            Replace(xml, rdf, description, MediaManagement, "xmpMM", "InstanceID").InnerText = NewInstance(oldInstance ?? document!, date, producer);
        return Serialized(xml, packet);
    }

    /// <summary>
    /// A new instance identifier, derived from the old one and the time of the save: a fresh one each save, the same for the same save (the
    /// compressor writes a trial run and the save with one time; checksums of a run stay comparable).
    /// </summary>
    private static string NewInstance(string old, string date, string producer)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(old + "\n" + date + "\n" + producer));
        hash[6] = (byte)(hash[6] & 0x0F | 0x50);   // version 5 (name-based)
        hash[8] = (byte)(hash[8] & 0x3F | 0x80);   // RFC 4122 variant
        return "uuid:" + new Guid(hash.AsSpan(0, 16), bigEndian: true).ToString("D");
    }

    /// <summary>
    /// Removes a property (element anywhere in the packet, attribute at any description) and adds an empty element for it to the first
    /// description, under the prefix the description already uses for the namespace.
    /// </summary>
    private static XmlElement Replace(XmlDocument xml, XmlElement rdf, XmlElement description, string ns, string prefix, string name)
    {
        foreach (var old in xml.GetElementsByTagName(name, ns).OfType<XmlElement>().ToList()) old.ParentNode?.RemoveChild(old);
        foreach (var each in rdf.ChildNodes.OfType<XmlElement>()) each.RemoveAttribute(name, ns);
        var element = xml.CreateElement(description.GetPrefixOfNamespace(ns) is { Length: > 0 } known ? known : prefix, name, ns);
        description.AppendChild(element);
        return element;
    }

    /// <summary>The descriptions of the packet (direct children of rdf:RDF).</summary>
    private static IEnumerable<XmlElement> Descriptions(XmlElement rdf) =>
        rdf.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "Description" && e.NamespaceURI == Rdf);

    /// <summary>The element of a top-level property in any description; null if there is none.</summary>
    private static XmlElement? Property(XmlElement rdf, string ns, string name) =>
        Descriptions(rdf).SelectMany(d => d.ChildNodes.OfType<XmlElement>()).FirstOrDefault(e => e.LocalName == name && e.NamespaceURI == ns);

    /// <summary>A simple property as text — element or attribute form (trimmed); null if there is none.</summary>
    private static string? Simple(XmlElement rdf, string ns, string name)
    {
        foreach (var description in Descriptions(rdf))
            if (description.GetAttributeNode(name, ns) is { } attribute) return attribute.Value.Trim();
        return Property(rdf, ns, name) is { } element && !element.ChildNodes.OfType<XmlElement>().Any() ? element.InnerText.Trim() : null;
    }

    /// <summary>The x-default entry of a language alternative (else its first entry; a simple value as it is); null if there is none.</summary>
    private static string? Alternative(XmlElement rdf, string ns, string name)
    {
        if (Property(rdf, ns, name) is not { } element) return Simple(rdf, ns, name);
        var items = element.GetElementsByTagName("li", Rdf).OfType<XmlElement>().ToList();
        if (items.Count == 0) return element.InnerText.Trim();
        return (items.FirstOrDefault(i => i.GetAttribute("lang", XmlNs) == "x-default") ?? items[0]).InnerText.Trim();
    }

    /// <summary>The entries of an ordered or unordered list (a simple value as the only entry); empty if there is none.</summary>
    private static List<string> Items(XmlElement rdf, string ns, string name)
    {
        if (Property(rdf, ns, name) is not { } element) return Simple(rdf, ns, name) is { Length: > 0 } single ? [single] : [];
        var items = element.GetElementsByTagName("li", Rdf).OfType<XmlElement>().Select(i => i.InnerText.Trim()).Where(t => t.Length > 0).ToList();
        return items.Count > 0 || element.InnerText.Trim() is not { Length: > 0 } text ? items : [text];
    }

    /// <summary>The entries of the information dictionary the XMP holds (XMP part 3, 2.2, table 20), and its metadata date.</summary>
    /// <param name="Title">dc:title (x-default).</param>
    /// <param name="Author">dc:creator, the entries joined by „; “.</param>
    /// <param name="Subject">dc:description (x-default).</param>
    /// <param name="Keywords">pdf:Keywords.</param>
    /// <param name="Creator">xmp:CreatorTool.</param>
    /// <param name="MetadataDate">xmp:MetadataDate; null if missing or unreadable.</param>
    public sealed record InfoValues(string? Title, string? Author, string? Subject, string? Keywords, string? Creator, DateTimeOffset? MetadataDate)
    {
        /// <summary>The value for a key of the information dictionary (Title, Author, Subject, Keywords, Creator).</summary>
        public string? Of(string key) => key switch
        {
            "Title" => Title, "Author" => Author, "Subject" => Subject, "Keywords" => Keywords, "Creator" => Creator, _ => null,
        };
    }

    /// <summary>The keys of the information dictionary that have an XMP property besides the dates and the producer.</summary>
    public static readonly string[] InfoKeys = ["Title", "Author", "Subject", "Keywords", "Creator"];

    /// <summary>Reads what the packet says for the information dictionary; null if it is not well-formed XMP.</summary>
    public static InfoValues? ReadInfo(byte[] packet)
    {
        if (PdfAXmp.Read(packet) is not { } xml || xml.GetElementsByTagName("RDF", Rdf) is not { Count: > 0 } rdfs || rdfs[0] is not XmlElement rdf) return null;
        var creators = Items(rdf, Dc, "creator");
        DateTimeOffset? date = DateTimeOffset.TryParse(Simple(rdf, Xmp, "MetadataDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
        return new InfoValues(Alternative(rdf, Dc, "title"), creators.Count > 0 ? string.Join("; ", creators) : null, Alternative(rdf, Dc, "description"),
            Simple(rdf, Pdf, "Keywords"), Simple(rdf, Xmp, "CreatorTool"), date);
    }

    /// <summary>
    /// The packet with the given entries of the information dictionary written into their XMP properties (XMP part 3, 2.2, table 20): Title
    /// and Subject as a language alternative with the one entry x-default (other languages would contradict it), Author as the list of
    /// creators (kept if the author is its first entry or all entries joined), Keywords and Creator as simple values. Entries equal to the
    /// XMP stay untouched; nothing different: the same array. Null if the packet is not well-formed XMP.
    /// </summary>
    public static byte[]? SetInfo(byte[] packet, IReadOnlyList<(string Key, string Value)> values)
    {
        if (PdfAXmp.Read(packet) is not { } xml || xml.GetElementsByTagName("RDF", Rdf) is not { Count: > 0 } rdfs || rdfs[0] is not XmlElement rdf) return null;
        var changed = false;
        foreach (var (key, raw) in values)
        {
            var value = PdfAMetadata.Clean(raw).Trim();
            switch (key)
            {
                case "Title" or "Subject":
                    var name = key == "Title" ? "title" : "description";
                    if (Alternative(rdf, Dc, name) == value && Property(rdf, Dc, name) is { } existing && existing.GetElementsByTagName("li", Rdf).Count <= 1) continue;
                    LangAlt(xml, Replace(xml, rdf, FirstDescription(xml, rdf), Dc, "dc", name), value);
                    break;
                case "Author":
                    var creators = Items(rdf, Dc, "creator");
                    if (creators.Count > 0 && (creators[0] == value || string.Join("; ", creators) == value)) continue;
                    var seq = xml.CreateElement("rdf", "Seq", Rdf);
                    var li = xml.CreateElement("rdf", "li", Rdf);
                    li.InnerText = value;
                    seq.AppendChild(li);
                    Replace(xml, rdf, FirstDescription(xml, rdf), Dc, "dc", "creator").AppendChild(seq);
                    break;
                case "Keywords" or "Creator":
                    var (ns, prefix, local) = key == "Keywords" ? (Pdf, "pdf", "Keywords") : (Xmp, "xmp", "CreatorTool");
                    if (Simple(rdf, ns, local) == value) continue;
                    Replace(xml, rdf, FirstDescription(xml, rdf), ns, prefix, local).InnerText = value;
                    break;
                default:
                    continue;
            }
            changed = true;
        }
        return changed ? Serialized(xml, packet) : packet;
    }

    /// <summary>Fills a property element with a language alternative holding <paramref name="value"/> as x-default.</summary>
    private static void LangAlt(XmlDocument xml, XmlElement element, string value)
    {
        var alt = xml.CreateElement("rdf", "Alt", Rdf);
        var li = xml.CreateElement("rdf", "li", Rdf);
        li.SetAttribute("lang", XmlNs, "x-default");
        li.InnerText = value;
        alt.AppendChild(li);
        element.AppendChild(alt);
    }

    /// <summary>What in the media management schema describes the whole document and its history.</summary>
    private static readonly string[] OfTheWhole = ["DocumentID", "InstanceID", "OriginalDocumentID", "VersionID", "History", "DerivedFrom", "Ingredients",
        "Versions", "Manifest", "RenditionOf", "ManagedFrom"];

    /// <summary>
    /// The packet without the preview: xmp:Thumbnails (whatever prefix, xmp: or the older xap:) and every property of the image schema
    /// (xmpGImg:, the older xapGImg:) also outside it — an image of the first page as it was (docs/FEHLERGEDAECHTNIS.md, No. 235, 446, 613).
    /// With <paramref name="wholeDocument"/> also the identifiers and the history of the whole document (a part of it is a resource of its own,
    /// XMP part 1, 6.1). Nothing to remove: the same array. Null if the packet is not well-formed XMP.
    /// </summary>
    public static byte[]? RemoveThumbnails(byte[] packet, bool wholeDocument)
    {
        if (PdfAXmp.Read(packet) is not { } xml || xml.GetElementsByTagName("RDF", Rdf) is not { Count: > 0 }) return null;
        bool Goes(string ns, string name) => ns == Xmp && name == "Thumbnails" || ns == Image || wholeDocument && ns == MediaManagement && OfTheWhole.Contains(name);
        var changed = false;
        foreach (var element in xml.GetElementsByTagName("*").OfType<XmlElement>().ToList())
        {
            if (element.ParentNode is null) continue;   // inside an element already removed
            if (Goes(element.NamespaceURI, element.LocalName))
            {
                element.ParentNode.RemoveChild(element);
                changed = true;
                continue;
            }
            foreach (var attribute in element.Attributes.OfType<XmlAttribute>().ToList())
                if (Goes(attribute.NamespaceURI, attribute.LocalName))
                {
                    element.Attributes.Remove(attribute);
                    changed = true;
                }
        }
        return changed ? Serialized(xml, packet) : packet;
    }

    /// <summary>
    /// The packet without the properties of a schema (<paramref name="ns"/>): its elements and attributes anywhere, and its namespace
    /// declarations; the rest as it was. Null if the packet is not well-formed XMP (docs/FEHLERGEDAECHTNIS.md, No. 554).
    /// </summary>
    public static byte[]? RemoveNamespace(byte[] packet, string ns)
    {
        if (PdfAXmp.Read(packet) is not { } xml || xml.GetElementsByTagName("RDF", Rdf) is not { Count: > 0 }) return null;
        foreach (var element in xml.GetElementsByTagName("*").OfType<XmlElement>().ToList())
        {
            if (element.NamespaceURI == ns)
            {
                element.ParentNode?.RemoveChild(element);
                continue;
            }
            foreach (var attribute in element.Attributes.OfType<XmlAttribute>().ToList())
                if (attribute.NamespaceURI == ns || attribute.Prefix == "xmlns" && attribute.Value == ns) element.Attributes.Remove(attribute);
        }
        return Serialized(xml, packet);
    }

    /// <summary>
    /// The packet without the properties of these schemas and without the entries of the PDF/A extension schemas that declare them (an
    /// extension container left empty goes too); the rest as it was. The same array if nothing of them is in it; null if the packet is not
    /// well-formed XML (<see cref="AttachedFileMetadata"/>; docs/FEHLERGEDAECHTNIS.md, No. 865). Unlike the other edits it asks no valid RDF
    /// around them: the data of a file that went are taken out of any well-formed packet — a fault of the packet elsewhere must not keep them
    /// (No. 868: the namespace of RDF misspelt, the invoice data stayed and were still found).
    /// </summary>
    public static byte[]? RemoveDescribed(byte[] packet, IReadOnlySet<string> namespaces)
    {
        if (PdfAXmp.Read(packet) is not { } xml) return null;
        var changed = false;
        foreach (var element in xml.GetElementsByTagName("*").OfType<XmlElement>().ToList())
        {
            if (element.ParentNode is null) continue;   // inside a removed element
            if (namespaces.Contains(element.NamespaceURI))
            {
                element.ParentNode.RemoveChild(element);
                changed = true;
                continue;
            }
            foreach (var attribute in element.Attributes.OfType<XmlAttribute>().ToList())
                if (namespaces.Contains(attribute.NamespaceURI) || attribute.Prefix == "xmlns" && namespaces.Contains(attribute.Value))
                {
                    element.Attributes.Remove(attribute);
                    changed = true;
                }
        }
        // the extension schemas declaring them: each entry an rdf:li of the bag of pdfaExtension:schemas with its pdfaSchema:namespaceURI
        foreach (XmlElement container in xml.GetElementsByTagName("schemas", XmpSchemas.Extension).OfType<XmlElement>().ToList())
        {
            var removedHere = false;
            foreach (var entry in container.GetElementsByTagName("li", Rdf).OfType<XmlElement>().Where(li => li.ParentNode?.ParentNode == container).ToList())
                if (entry.GetElementsByTagName("namespaceURI", XmpSchemas.Schema).OfType<XmlElement>().FirstOrDefault() is { } uri
                    && namespaces.Contains(uri.InnerText.Trim()))
                {
                    entry.ParentNode!.RemoveChild(entry);
                    removedHere = changed = true;
                }
            if (removedHere && container.GetElementsByTagName("li", Rdf).Count == 0) container.ParentNode?.RemoveChild(container);
        }
        return changed ? Serialized(xml, packet) : packet;
    }

    /// <summary>The first description of the document (rdf:about="" or any), created if there is none.</summary>
    private static XmlElement FirstDescription(XmlDocument xml, XmlElement rdf)
    {
        var description = rdf.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == "Description" && e.NamespaceURI == Rdf);
        if (description is not null) return description;
        description = xml.CreateElement("rdf", "Description", Rdf);
        description.SetAttribute("about", Rdf, "");
        rdf.AppendChild(description);
        return description;
    }

    /// <summary>
    /// The packet with <c>dc:title</c> set to <paramref name="title"/> (an rdf:Alt with the one entry „x-default“; other languages of the
    /// title go, they would contradict it). Null if the packet is not well-formed XMP — then it stays as it is.
    /// </summary>
    public static byte[]? SetTitle(byte[] packet, string title)
    {
        if (PdfAXmp.Read(packet) is not { } xml || xml.GetElementsByTagName("RDF", Rdf) is not { Count: > 0 } rdfs || rdfs[0] is not XmlElement rdf) return null;
        foreach (var old in xml.GetElementsByTagName("title", Dc).OfType<XmlElement>().ToList()) old.ParentNode?.RemoveChild(old);
        if (title.Length == 0) return Serialized(xml, packet);   // cleared: no title
        // the first description of the document (rdf:about="" or any): the title goes there
        var description = FirstDescription(xml, rdf);
        var element = xml.CreateElement("dc", "title", Dc);
        LangAlt(xml, element, PdfAMetadata.Clean(title));
        description.AppendChild(element);
        return Serialized(xml, packet);
    }

    /// <summary>
    /// The changed packet in UTF-8 — with the padding of the original kept: the white space before „&lt;?xpacket end“ lets other programs
    /// change the packet in place (XMP, ISO 16684-1, 7.3); reading drops it, so it is put back (quality round of phase 5, 6.11.2026).
    /// </summary>
    private static byte[] Serialized(XmlDocument xml, byte[] original)
    {
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = true, Indent = false };
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, settings)) xml.Save(writer);
        var result = output.ToArray();
        var padding = Padding(original);
        var end = result.AsSpan().LastIndexOf("<?xpacket end"u8);
        if (padding == 0 || end < 0) return result;
        var filler = new byte[padding];
        for (var i = 0; i < padding; i++) filler[i] = (byte)(i % 100 == 99 || i == padding - 1 ? '\n' : ' ');
        return [.. result.AsSpan(0, end), .. filler, .. result.AsSpan(end)];
    }

    /// <summary>The changed document written back like the other changes (<see cref="Serialized"/>), for other writers of the packet.</summary>
    internal static byte[] Write(XmlDocument xml, byte[] original) => Serialized(xml, original);

    /// <summary>The white space right before „&lt;?xpacket end“ (at most 64 KiB); 0 if there is no end of packet.</summary>
    private static int Padding(byte[] packet)
    {
        var end = packet.AsSpan().LastIndexOf("<?xpacket end"u8);
        if (end < 0) return 0;
        var start = end;
        while (start > 0 && end - start < 1 << 16 && packet[start - 1] is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t') start--;
        return end - start;
    }

    /// <summary>Whether a packet holds nothing but the title (as a cleaned-up copy with „Titel behalten“ has it).</summary>
    public static bool TitleOnly(byte[] packet)
    {
        if (PdfAXmp.Read(packet) is not { } xml || xml.GetElementsByTagName("RDF", Rdf) is not { Count: > 0 } rdfs || rdfs[0] is not XmlElement rdf) return false;
        foreach (var description in rdf.ChildNodes.OfType<XmlElement>())
        {
            if (description.LocalName != "Description" || description.NamespaceURI != Rdf) return false;
            if (description.Attributes.OfType<XmlAttribute>().Any(a => a.NamespaceURI != Rdf && a.Prefix != "xmlns" && a.Name != "xmlns")) return false;
            if (description.ChildNodes.OfType<XmlElement>().Any(e => e.LocalName != "title" || e.NamespaceURI != Dc)) return false;
        }
        return true;
    }

    /// <summary>
    /// A slim packet for a file without one: the title (x-default), the modification and metadata date and the producing program — no
    /// identification of a standard, no history. Cleaned up: the title alone (no program, no date, as in the information dictionary).
    /// </summary>
    /// <param name="extra">Further descriptions (rdf:Description elements) to keep in the packet, e.g. what attached files that stay need
    /// (<see cref="PdfAXmp.KeptWithAttachedFiles"/>); null: none.</param>
    public static byte[] Slim(string title, DateTimeOffset? modified, string? producer, string? extra = null)
    {
        var sb = new StringBuilder();
        sb.Append("<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">\n");
        if (title.Length > 0) sb.Append(CultureInfo.InvariantCulture, $"<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{Xml(title)}</rdf:li></rdf:Alt></dc:title>\n");
        if (modified is { } when)
        {
            var date = PdfAMetadata.XmpDate(when);
            sb.Append(CultureInfo.InvariantCulture, $"<xmp:ModifyDate>{date}</xmp:ModifyDate>\n<xmp:MetadataDate>{date}</xmp:MetadataDate>\n");
        }
        if (producer is not null) sb.Append(CultureInfo.InvariantCulture, $"<pdf:Producer>{Xml(producer)}</pdf:Producer>\n");
        sb.Append("</rdf:Description>\n");
        if (extra is not null) sb.Append(extra);
        sb.Append("</rdf:RDF>\n</x:xmpmeta>\n<?xpacket end=\"w\"?>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string Xml(string text) => SecurityElement.Escape(PdfAMetadata.Clean(text)).Replace("\r", "&#xD;", StringComparison.Ordinal);
}
