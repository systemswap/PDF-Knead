using System.Xml;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// The rules of PDF/A on an XMP packet (ISO 19005-2, 6.6.2; ISO 19005-4, 6.7.2): a header without „bytes“ and „encoding“, UTF-8,
/// well-formed XML and RDF; every property from a predefined schema (<see cref="XmpSchemas"/>) or one an extension schema of the packet — or
/// of the document's packet — declares, with the value type of its schema (6.6.2.3.1); extension schemas declared completely, with the
/// fields and prefixes of the PDF/A container schemas (6.6.2.3.2, 6.6.2.3.3). The rules apply by part (<see cref="PdfARule.Id"/>).
/// </summary>
internal static class PdfAXmp
{
    /// <summary>What the extension schemas of a packet declare: the properties per namespace (with their types) and the structured types.</summary>
    internal sealed class Declarations
    {
        public Dictionary<string, Dictionary<string, XmpType?>> Properties { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, XmpStruct> Types { get; } = new(StringComparer.Ordinal);

        /// <summary>The type a declaration gives a property; false if none declares it (a declared type that is not defined is null).</summary>
        public bool TryGet(string ns, string name, out XmpType? type)
        {
            type = null;
            return Properties.TryGetValue(ns, out var schema) && schema.TryGetValue(name, out type);
        }
    }

    /// <summary>A fault of a packet: the rule and what is wrong.</summary>
    /// <param name="Test">The test of the rule's clause, counted from its first number (1 = that number); 0 for the number itself
    /// (docs/FEHLERGEDAECHTNIS.md, No. 503).</param>
    internal readonly record struct Fault(PdfARule Rule, string Text, int Test = 0);

    /// <summary>
    /// The test of ISO 19005-2, 6.6.2.3.3 a fault of an extension schema breaks, by the field named („pdfaSchema:prefix“ → 4): one number per
    /// field of the container schemas, in the order schema, property, value type, field (part 1 counts one further: 6.7.8).
    /// </summary>
    private static int ContainerTest(string what) => what.Split(' ')[0] switch
    {
        "pdfaExtension:schemas" => 1,
        "pdfaSchema:schema" => 2,
        "pdfaSchema:namespaceURI" => 3,
        "pdfaSchema:prefix" => 4,
        "pdfaSchema:property" => 5,
        "pdfaSchema:valueType" => 6,
        "pdfaProperty:name" => 7,
        "pdfaProperty:valueType" => 8,
        "pdfaProperty:category" => 9,
        "pdfaProperty:description" => 10,
        "pdfaType:type" => 11,
        "pdfaType:namespaceURI" => 12,
        "pdfaType:prefix" => 13,
        "pdfaType:description" => 14,
        "pdfaType:field" => 15,
        "pdfaField:name" => 16,
        "pdfaField:valueType" => 17,
        "pdfaField:description" => 18,
        _ => 0,
    };

    /// <summary>
    /// The deepest nesting of a packet read: real XMP, extension schemas with structured types included, stays below 15 levels. Deeper packets
    /// are refused — everything after reading walks the tree recursively (cloning, writing, the value types), and a crafted packet of thousands
    /// of levels overflowed the stack, which no handler catches (docs/FEHLERGEDAECHTNIS.md, No. 866; the limit of the invoice XML as well).
    /// </summary>
    internal const int MaxDepth = 64;

    /// <summary>
    /// An XMP packet as XML, read without external entities or document types and at most <see cref="MaxDepth"/> levels deep; null if it is not
    /// well-formed or deeper.
    /// </summary>
    internal static XmlDocument? Read(byte[] data)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 << 20 };
            // the depth first, without building anything: the tree is built only when it is shallow enough
            using (var scan = XmlReader.Create(new MemoryStream(data), settings))
                while (scan.Read())
                    if (scan.Depth > MaxDepth) return null;
            var xml = new XmlDocument { XmlResolver = null };
            using var reader = XmlReader.Create(new MemoryStream(data), settings);
            xml.Load(reader);
            return xml;
        }
        catch (Exception e) when (e is XmlException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The faults of a packet.</summary>
    /// <param name="data">The packet as stored (decoded).</param>
    /// <param name="main">The declarations of the document's packet, for the packet of an object; null for the document's own.</param>
    /// <param name="declared">What the packet itself declares (null if it cannot be read).</param>
    /// <param name="part">The part checked: its predefined schemas (<see cref="XmpSchemas.For"/>).</param>
    /// <returns>The faults.</returns>
    internal static List<Fault> Faults(byte[] data, Declarations? main, out Declarations? declared, PdfAConformance part)
    {
        var predefinedSchemas = XmpSchemas.For(part);
        var faults = new List<Fault>();
        declared = null;
        var text = System.Text.Encoding.UTF8.GetString(data);
        // the packet header: the first processing instruction xpacket that is not its end (the attributes in any order)
        var header = System.Text.RegularExpressions.Regex.Match(text, @"<\?xpacket(?!\s+end\s*=)[^?]*\?>") is { Success: true } found ? found.Value : null;
        if (header is not null && header.Contains("bytes=", StringComparison.Ordinal)) faults.Add(new(PdfARules.XmpNoBytes, Tr.T("Der XMP-Paketkopf enthält „bytes“.")));
        if (header is not null && header.Contains("encoding=", StringComparison.Ordinal)) faults.Add(new(PdfARules.XmpNoEncoding, Tr.T("Der XMP-Paketkopf enthält „encoding“.")));
        if (!Utf8(data)) faults.Add(new(PdfARules.XmpUtf8, Tr.T("Die Metadaten sind nicht in UTF-8 kodiert.")));
        if (Read(data) is not { } xml || xml.GetElementsByTagName("RDF", XmpSchemas.Rdf).Count == 0)
        {
            faults.Add(new(PdfARules.XmpWellFormed, Tr.T("Die Metadaten sind kein lesbares XMP.")));
            return faults;
        }
        var properties = TopLevel(xml).ToList();
        declared = new Declarations();
        foreach (var (ns, name, node) in properties)
            if (ns == XmpSchemas.Extension && name == "schemas") Declare(node, declared, main, faults);
        foreach (var (ns, name, node) in properties)
        {
            if (ns == XmpSchemas.Extension && name == "schemas") continue;
            XmpType? type;
            if (predefinedSchemas.TryGetValue(ns, out var schema) && schema.TryGetValue(name, out var predefined)) type = predefined;
            else if (declared.TryGet(ns, name, out var own)) type = own;
            else if (main is not null && main.TryGet(ns, name, out var inMain)) type = inMain;
            else
            {
                faults.Add(new(PdfARules.XmpSchemas, Tr.T("Die Metadaten enthalten die Eigenschaft „{0}“ aus keinem bekannten oder erklärten Schema.", Label(node))));
                continue;
            }
            // a declared type that is not defined is named with the declaration
            if (type is not null && !Matches(Of(node), type))
                faults.Add(new(PdfARules.XmpValueTypes, Tr.T("Die Eigenschaft „{0}“ der Metadaten hat nicht den Werttyp ihres Schemas.", Label(node))));
        }
        return faults;
    }

    /// <summary>
    /// The top-level properties a PDF/A packet of <paramref name="part"/> can carry over from another packet: of a predefined schema of the part
    /// and with the value type of its schema (6.6.2.3.1; ISO 19005-1, 6.7.9), not the identification of PDF/A and none <paramref name="skip"/>
    /// names — each as an XML element with its namespace declarations (an attribute written as an element). docs/ABLAUFPLAN.md, 6.1 step 7.
    /// </summary>
    internal static List<string> Carryable(XmlDocument xml, PdfAConformance part, Func<string, string, bool> skip)
    {
        var schemas = XmpSchemas.For(part);
        var result = new List<string>();
        foreach (var (ns, name, node) in TopLevel(xml))
            if (ns != XmpSchemas.PdfAId && !skip(ns, name) && schemas.TryGetValue(ns, out var schema) && schema.TryGetValue(name, out var type) && Matches(Of(node), type))
                result.Add(AsElement(node));
        return result;
    }

    /// <summary>
    /// Identifications of other standards a copy cannot vouch for: PDF/UA (carried on its own terms, see the writer), the declarations, PDF/X,
    /// PDF/E and PDF/VT — never carried as declared properties.
    /// </summary>
    private static readonly HashSet<string> Claims = new(StringComparer.Ordinal)
    {
        AccessibilityClaims.PdfUaId, AccessibilityClaims.Declarations, "http://www.npes.org/pdfx/ns/id/", "http://www.aiim.org/pdfe/ns/id/",
        "http://www.npes.org/pdfvt/ns/id/",
    };

    /// <summary>
    /// The properties of schemas the packet declares itself in an extension schema — PDF/A allows them when declared (6.6.2.3.1), and files
    /// keep data there that other software reads (invoice data of a hybrid invoice, for instance) — with the declarations they need. Only if
    /// every extension schema of the packet is declared completely and correctly (otherwise none: the copy would break 6.6.2.3.2/3), only
    /// properties with the declared value type, none of a predefined schema of the part (<see cref="Carryable"/> takes those), of the PDF/A
    /// identification or of another standard's identification. The declarations come as one rdf:Description with the schema entries of the
    /// namespaces carried, as written in the source. docs/FEHLERGEDAECHTNIS.md, No. 863.
    /// </summary>
    /// <returns>The properties, each a self-contained XML element, and the description holding their declarations (null without any).</returns>
    internal static (List<string> Properties, string? Declarations) CarryableDeclared(XmlDocument xml, PdfAConformance part)
    {
        var predefined = XmpSchemas.For(part);
        var containers = TopLevel(xml).Where(p => p.Namespace == XmpSchemas.Extension && p.Name == "schemas").Select(p => p.Node).ToList();
        if (containers.Count == 0) return ([], null);
        var faults = new List<Fault>();
        var declared = new Declarations();
        foreach (var container in containers) Declare(container, declared, null, faults);
        if (faults.Count > 0) return ([], null);
        var properties = new List<string>();
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (ns, name, node) in TopLevel(xml))
        {
            if (ns is XmpSchemas.Extension or XmpSchemas.PdfAId || predefined.ContainsKey(ns) || Claims.Contains(ns)) continue;
            if (!declared.TryGet(ns, name, out var type) || type is null || !Matches(Of(node), type)) continue;
            properties.Add(AsElement(node));
            namespaces.Add(ns);
        }
        if (properties.Count == 0) return ([], null);
        var entries = containers.SelectMany(c => Of(c).Items)
            .Where(item => Text(Field(Of(item), XmpSchemas.Schema, "namespaceURI")) is { } uri && namespaces.Contains(uri))
            .Select(item => AsElement(WithContainerNamespaces(item)));
        return (properties, "<rdf:Description rdf:about=\"\" xmlns:pdfaExtension=\"" + XmpSchemas.Extension + "\">\n<pdfaExtension:schemas><rdf:Bag>\n"
                            + string.Join("\n", entries) + "\n</rdf:Bag></pdfaExtension:schemas>\n</rdf:Description>\n");
    }

    /// <summary>
    /// What a packet slimmed down by „Metadaten bereinigen“ keeps while attached files it describes stay (KONZEPT.md, section 5.24, decision
    /// (4) of 15.11.2026: an e-invoice kept stays whole): the PDF/A identification and the properties of <paramref name="namespaces"/>
    /// with the entries of the extension schemas declaring them, as descriptions for <see cref="XmpEditor.Slim"/>; empty if there is none.
    /// </summary>
    internal static string KeptWithAttachedFiles(XmlDocument xml, IReadOnlySet<string> namespaces)
    {
        var top = TopLevel(xml).ToList();
        if (!top.Any(p => namespaces.Contains(p.Namespace))) return "";
        var properties = top.Where(p => p.Namespace == XmpSchemas.PdfAId || namespaces.Contains(p.Namespace)).Select(p => AsElement(p.Node)).ToList();
        var entries = top.Where(p => p.Namespace == XmpSchemas.Extension && p.Name == "schemas").SelectMany(p => Of(p.Node).Items)
            .Where(item => Text(Field(Of(item), XmpSchemas.Schema, "namespaceURI")) is { } uri && namespaces.Contains(uri))
            .Select(item => AsElement(WithContainerNamespaces(item))).ToList();
        var result = "<rdf:Description rdf:about=\"\">\n" + string.Join("\n", properties) + "\n</rdf:Description>\n";
        if (entries.Count > 0)
            result += "<rdf:Description rdf:about=\"\" xmlns:pdfaExtension=\"" + XmpSchemas.Extension + "\">\n<pdfaExtension:schemas><rdf:Bag>\n"
                      + string.Join("\n", entries) + "\n</rdf:Bag></pdfaExtension:schemas>\n</rdf:Description>\n";
        return result;
    }

    /// <summary>
    /// A copy of a schema entry with the namespaces of the container schemas declared on it — written alone, its fields would otherwise each
    /// declare their namespace anew.
    /// </summary>
    private static XmlElement WithContainerNamespaces(XmlElement item)
    {
        var copy = (XmlElement)item.CloneNode(true);
        foreach (var (prefix, uri) in (ReadOnlySpan<(string, string)>)[("pdfaSchema", XmpSchemas.Schema), ("pdfaProperty", XmpSchemas.Property),
                     ("pdfaType", XmpSchemas.Type), ("pdfaField", XmpSchemas.Field)])
            if (copy.GetElementsByTagName("*", uri).Count > 0) copy.SetAttribute("xmlns:" + prefix, uri);
        return copy;
    }

    /// <summary>The top-level properties of a schema (<paramref name="ns"/>), each as an XML element (see <see cref="Carryable"/>).</summary>
    internal static List<string> PropertiesOf(XmlDocument xml, string ns) => [.. TopLevel(xml).Where(p => p.Namespace == ns).Select(p => AsElement(p.Node))];

    /// <summary>
    /// A property as a self-contained XML element: the namespaces it uses declared on it, a carriage return as a character reference (a reader
    /// would make a line feed of it).
    /// </summary>
    private static string AsElement(XmlNode node)
    {
        var settings = new XmlWriterSettings { OmitXmlDeclaration = true, ConformanceLevel = ConformanceLevel.Fragment, NewLineHandling = NewLineHandling.Entitize };
        using var text = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        using (var writer = XmlWriter.Create(text, settings))
        {
            if (node is XmlAttribute attribute)
            {
                writer.WriteStartElement(attribute.Prefix.Length > 0 ? attribute.Prefix : "ns", attribute.LocalName, attribute.NamespaceURI);
                writer.WriteString(attribute.Value);
                writer.WriteEndElement();
            }
            else node.WriteTo(writer);
        }
        return text.ToString();
    }

    /// <summary>The identification properties (pdfaid) written with another prefix than pdfaid (6.6.4).</summary>
    internal static IEnumerable<(string Label, string Name)> ForeignIdentificationPrefixes(XmlDocument xml) =>
        TopLevel(xml).Where(p => p.Namespace == XmpSchemas.PdfAId && p.Node.Prefix != "pdfaid").Select(p => (Label(p.Node), p.Node.LocalName));

    /// <summary>
    /// Whether the bytes are UTF-8 (6.6.2.1): no byte order mark of UTF-16 or UTF-32 and no byte sequence UTF-8 does not allow.
    /// </summary>
    private static bool Utf8(byte[] data)
    {
        var bytes = data.AsSpan();
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) || bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) ||
            bytes.StartsWith((ReadOnlySpan<byte>)[0, 0, 0xFE, 0xFF]))
            return false;
        try
        {
            _ = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(data);
            return true;
        }
        catch (System.Text.DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>The properties of the packet: of every rdf:Description under rdf:RDF, its attributes and child elements.</summary>
    private static IEnumerable<(string Namespace, string Name, XmlNode Node)> TopLevel(XmlDocument xml)
    {
        foreach (XmlElement rdf in xml.GetElementsByTagName("RDF", XmpSchemas.Rdf))
            foreach (var description in rdf.ChildNodes.OfType<XmlElement>().Where(d => d.NamespaceURI == XmpSchemas.Rdf && d.LocalName == "Description"))
            {
                foreach (var attribute in description.Attributes.OfType<XmlAttribute>().Where(IsProperty))
                    yield return (attribute.NamespaceURI, attribute.LocalName, attribute);
                foreach (var child in description.ChildNodes.OfType<XmlElement>())
                    yield return (child.NamespaceURI, child.LocalName, child);
            }
    }

    /// <summary>An attribute that is a property or a field: not a namespace declaration and none of RDF or XML.</summary>
    private static bool IsProperty(XmlAttribute a) =>
        a.Prefix != "xmlns" && a.Name != "xmlns" && a.NamespaceURI is not (XmpSchemas.Rdf or XmpSchemas.Xml) && a.NamespaceURI.Length > 0;

    private static string Label(XmlNode node) => node.Prefix.Length > 0 ? $"{node.Prefix}:{node.LocalName}" : node.LocalName;

    // ------------------------------------------------------------ values

    private enum Shape
    {
        Simple,
        Struct,
        Array,
        Invalid,
    }

    /// <summary>A value as RDF writes it: simple (text), a structure (fields) or an array (items of a container).</summary>
    private readonly record struct Value(Shape Shape, string Text, string? Container, IReadOnlyList<XmlElement> Items, IReadOnlyList<XmlNode> Fields);

    /// <summary>
    /// The value of a property, a field or an array item (XMP Specification 2005, Part 3, 7: the forms of RDF/XML in XMP): a structure with
    /// rdf:parseType="Resource", in an rdf:Description or as attributes of an empty element; an array as rdf:Bag, rdf:Seq or rdf:Alt; a
    /// simple value as text, as rdf:resource, or qualified (rdf:value in an rdf:Description).
    /// </summary>
    private static Value Of(XmlNode node)
    {
        if (node is XmlAttribute attribute) return new Value(Shape.Simple, attribute.Value, null, [], []);
        var e = (XmlElement)node;
        if (e.GetAttribute("parseType", XmpSchemas.Rdf) == "Resource")
            return new Value(Shape.Struct, "", null, [], [.. e.Attributes.OfType<XmlAttribute>().Where(IsProperty), .. e.ChildNodes.OfType<XmlElement>()]);
        var children = e.ChildNodes.OfType<XmlElement>().ToList();
        if (children.Count == 1 && children[0].NamespaceURI == XmpSchemas.Rdf)
        {
            var c = children[0];
            if (c.LocalName is "Bag" or "Seq" or "Alt")
            {
                var items = c.ChildNodes.OfType<XmlElement>().ToList();
                return items.All(i => i.NamespaceURI == XmpSchemas.Rdf && i.LocalName == "li")
                    ? new Value(Shape.Array, "", c.LocalName, items, [])
                    : new Value(Shape.Invalid, "", null, [], []);
            }
            if (c.LocalName == "Description")
            {
                // a qualified simple value
                if (c.GetAttributeNode("value", XmpSchemas.Rdf) is { } valueAttribute) return new Value(Shape.Simple, valueAttribute.Value, null, [], []);
                if (c.ChildNodes.OfType<XmlElement>().FirstOrDefault(x => x.NamespaceURI == XmpSchemas.Rdf && x.LocalName == "value") is { } valueElement)
                    return Of(valueElement);
                return new Value(Shape.Struct, "", null, [], [.. c.Attributes.OfType<XmlAttribute>().Where(IsProperty), .. c.ChildNodes.OfType<XmlElement>()]);
            }
        }
        if (children.Count > 0) return new Value(Shape.Invalid, "", null, [], []);
        var attributeFields = e.Attributes.OfType<XmlAttribute>().Where(IsProperty).ToList();
        if (attributeFields.Count > 0) return new Value(Shape.Struct, "", null, [], attributeFields);
        if (e.GetAttributeNode("resource", XmpSchemas.Rdf) is { } resource) return new Value(Shape.Simple, resource.Value, null, [], []);
        return new Value(Shape.Simple, e.InnerText, null, [], []);
    }

    /// <summary>Whether a value has a type: the form of a simple type, the container and items of an array, the fields of a structure.</summary>
    private static bool Matches(Value value, XmpType type) => type switch
    {
        XmpSimple simple => value.Shape == Shape.Simple && XmpSchemas.Lexical(simple.Name, value.Text.Trim()),
        XmpLangAlt => value is { Shape: Shape.Array, Container: "Alt" } &&
                      value.Items.All(i => Of(i).Shape == Shape.Simple && i.GetAttribute("lang", XmpSchemas.Xml).Length > 0),
        XmpArray array => value.Shape == Shape.Array && value.Container == array.Container && value.Items.All(i => Matches(Of(i), array.Item)),
        XmpStruct structure => value.Shape == Shape.Struct && value.Fields.All(f =>
            f.NamespaceURI == structure.Namespace && structure.Fields().TryGetValue(f.LocalName, out var fieldType) && Matches(Of(f), fieldType)),
        _ => false,
    };

    // ------------------------------------------------------------ extension schemas (6.6.2.3.2, 6.6.2.3.3)

    /// <summary>
    /// Reads the extension schemas of a pdfaExtension:schemas property into <paramref name="declared"/> and names what is missing or wrong in
    /// them: the container an rdf:Bag; each schema, property, value type and field a structure with the fields of its PDF/A schema and nothing
    /// else, written with their prefixes (pdfaSchema, pdfaProperty, pdfaType, pdfaField); texts, URIs, categories and value types valid.
    /// </summary>
    private static void Declare(XmlNode container, Declarations declared, Declarations? main, List<Fault> faults)
    {
        void Wrong(string what) => faults.Add(new(PdfARules.XmpExtensionContainer, Tr.T("Ein Erweiterungsschema der Metadaten ist unvollständig oder falsch erklärt ({0}).", what),
            ContainerTest(what)));
        void Undefined(string what) => faults.Add(new(PdfARules.XmpExtensionSchemas, Tr.T("Ein Erweiterungsschema der Metadaten enthält ein Feld, das sein Containerschema nicht kennt ({0}).", what)));

        var schemas = Of(container);
        if (schemas is not { Shape: Shape.Array, Container: "Bag" } || container.Prefix != "pdfaExtension")
        {
            Wrong("pdfaExtension:schemas");
            return;
        }
        // the structured types first: property types may name any of them
        var definitions = schemas.Items.Select(Of).Where(s => s.Shape == Shape.Struct).ToList();
        foreach (var schema in definitions)
            if (Field(schema, XmpSchemas.Schema, "valueType") is { } types && Of(types) is { Shape: Shape.Array } typeList)
                foreach (var item in typeList.Items.Select(Of).Where(t => t.Shape == Shape.Struct))
                    if (Text(Field(item, XmpSchemas.Type, "type")) is { Length: > 0 } typeName && Text(Field(item, XmpSchemas.Type, "namespaceURI")) is { } typeNs)
                    {
                        var fields = Field(item, XmpSchemas.Type, "field") is { } fieldList && Of(fieldList) is { Shape: Shape.Array } list
                            ? list.Items.Select(Of).Where(f => f.Shape == Shape.Struct).ToList()
                            : [];
                        var lazy = new Lazy<IReadOnlyDictionary<string, XmpType>>(() =>
                        {
                            var result = new Dictionary<string, XmpType>(StringComparer.Ordinal);
                            foreach (var f in fields)
                                if (Text(Field(f, XmpSchemas.Field, "name")) is { Length: > 0 } fieldName &&
                                    XmpSchemas.Parse(Text(Field(f, XmpSchemas.Field, "valueType")) ?? "", AllTypes(declared, main)) is { } fieldType)
                                    result.TryAdd(fieldName, fieldType);
                            return result;
                        });
                        declared.Types.TryAdd(typeName, new XmpStruct(typeName, typeNs, () => lazy.Value));
                    }
        if (definitions.Count != schemas.Items.Count) Wrong("pdfaExtension:schemas");
        foreach (var schema in definitions)
        {
            Only(schema, XmpSchemas.Schema, ["schema", "namespaceURI", "prefix", "property", "valueType"], "pdfaSchema", Undefined);
            TextField(schema, XmpSchemas.Schema, "schema", "pdfaSchema", Wrong);
            // (a missing or invalid namespaceURI is a fault of the container schema, 6.6.2.3.3, not an undefined field: No. 503)
            var ns = UriField(schema, XmpSchemas.Schema, "namespaceURI", "pdfaSchema", Wrong);
            TextField(schema, XmpSchemas.Schema, "prefix", "pdfaSchema", Wrong);
            if (Field(schema, XmpSchemas.Schema, "property") is { } propertyList)
            {
                var properties = Of(propertyList);
                if (properties is not { Shape: Shape.Array, Container: "Seq" } || propertyList.Prefix != "pdfaSchema") Wrong("pdfaSchema:property");
                foreach (var property in properties.Items.Select(Of))
                {
                    if (property.Shape != Shape.Struct)
                    {
                        Wrong("pdfaSchema:property");
                        continue;
                    }
                    Only(property, XmpSchemas.Property, ["name", "valueType", "category", "description"], "pdfaProperty", Undefined);
                    var name = TextField(property, XmpSchemas.Property, "name", "pdfaProperty", Wrong);
                    var typeName = TextField(property, XmpSchemas.Property, "valueType", "pdfaProperty", Wrong);
                    var type = typeName is null ? null : XmpSchemas.Parse(typeName, AllTypes(declared, main));
                    if (typeName is not null && type is null) Wrong($"pdfaProperty:valueType {typeName}");
                    if (TextField(property, XmpSchemas.Property, "category", "pdfaProperty", Wrong) is { } category && category is not ("external" or "internal"))
                        Wrong("pdfaProperty:category");
                    TextField(property, XmpSchemas.Property, "description", "pdfaProperty", Wrong);
                    if (ns is not null && name is not null)
                    {
                        if (!declared.Properties.TryGetValue(ns, out var schemaProperties)) declared.Properties[ns] = schemaProperties = new(StringComparer.Ordinal);
                        schemaProperties.TryAdd(name, type);
                    }
                }
            }
            if (Field(schema, XmpSchemas.Schema, "valueType") is { } typeList)
            {
                var types = Of(typeList);
                if (types is not { Shape: Shape.Array, Container: "Seq" } || typeList.Prefix != "pdfaSchema") Wrong("pdfaSchema:valueType");
                foreach (var valueType in types.Items.Select(Of))
                {
                    if (valueType.Shape != Shape.Struct)
                    {
                        Wrong("pdfaSchema:valueType");
                        continue;
                    }
                    Only(valueType, XmpSchemas.Type, ["type", "namespaceURI", "prefix", "description", "field"], "pdfaType", Undefined);
                    TextField(valueType, XmpSchemas.Type, "type", "pdfaType", Wrong);
                    UriField(valueType, XmpSchemas.Type, "namespaceURI", "pdfaType", Wrong);
                    TextField(valueType, XmpSchemas.Type, "prefix", "pdfaType", Wrong);
                    TextField(valueType, XmpSchemas.Type, "description", "pdfaType", Wrong);
                    if (Field(valueType, XmpSchemas.Type, "field") is not { } fieldList) continue;
                    var fields = Of(fieldList);
                    if (fields is not { Shape: Shape.Array, Container: "Seq" } || fieldList.Prefix != "pdfaType") Wrong("pdfaType:field");
                    foreach (var field in fields.Items.Select(Of))
                    {
                        if (field.Shape != Shape.Struct)
                        {
                            Wrong("pdfaType:field");
                            continue;
                        }
                        Only(field, XmpSchemas.Field, ["name", "valueType", "description"], "pdfaField", Undefined);
                        TextField(field, XmpSchemas.Field, "name", "pdfaField", Wrong);
                        if (TextField(field, XmpSchemas.Field, "valueType", "pdfaField", Wrong) is { } fieldType && XmpSchemas.Parse(fieldType, AllTypes(declared, main)) is null)
                            Wrong($"pdfaField:valueType {fieldType}");
                        TextField(field, XmpSchemas.Field, "description", "pdfaField", Wrong);
                    }
                }
            }
        }
    }

    /// <summary>The structured types declared in the packet and in the document's packet.</summary>
    private static IReadOnlyDictionary<string, XmpStruct> AllTypes(Declarations declared, Declarations? main)
    {
        if (main is null || main.Types.Count == 0) return declared.Types;
        var all = new Dictionary<string, XmpStruct>(main.Types, StringComparer.Ordinal);
        foreach (var (name, type) in declared.Types) all[name] = type;
        return all;
    }

    /// <summary>A field of a structure in a namespace; null if it has none.</summary>
    private static XmlNode? Field(Value structure, string ns, string name) =>
        structure.Fields.FirstOrDefault(f => f.NamespaceURI == ns && f.LocalName == name);

    /// <summary>The text of a simple value; null for none or another shape.</summary>
    private static string? Text(XmlNode? node) => node is not null && Of(node) is { Shape: Shape.Simple } value ? value.Text.Trim() : null;

    /// <summary>A required text field written with its prefix; its text, or null after naming the fault.</summary>
    private static string? TextField(Value structure, string ns, string name, string prefix, Action<string> fault)
    {
        var field = Field(structure, ns, name);
        if (field is null || field.Prefix != prefix || Text(field) is not { Length: > 0 } text)
        {
            fault($"{prefix}:{name}");
            return null;
        }
        return text;
    }

    /// <summary>A required field with an absolute URI, written with its prefix; the URI, or null after naming the fault.</summary>
    private static string? UriField(Value structure, string ns, string name, string prefix, Action<string> fault)
    {
        if (TextField(structure, ns, name, prefix, fault) is not { } text) return null;
        if (Uri.TryCreate(text, UriKind.Absolute, out _)) return text;
        fault($"{prefix}:{name}");
        return null;
    }

    /// <summary>A structure of a container schema with fields only of its namespace and names (6.6.2.3.2).</summary>
    private static void Only(Value structure, string ns, string[] names, string prefix, Action<string> fault)
    {
        foreach (var field in structure.Fields)
            if (field.NamespaceURI != ns || !names.Contains(field.LocalName)) fault($"{prefix}: {Label(field)}");
    }
}
