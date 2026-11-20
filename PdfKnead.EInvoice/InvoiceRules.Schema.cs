using System.Reflection;
using System.Xml;
using System.Xml.Schema;

namespace PdfKnead.EInvoice;

/// <summary>
/// The check against the XML schema of the syntax (stage 4, part f; KONZEPT.md, „Weg für Stufe 4, Teil f“): UBL 2.1 for UBL, the profile
/// schema of Factur-X 1.09.2 / ZUGFeRD 2.5.2 for a CII invoice of one of the five profiles, the CII schema D16B of UN/CEFACT (the one of
/// EN 16931 and of XRechnung) for any other CII invoice. The schemas are shipped unchanged (PdfKnead.EInvoice/Schemata, SHA256SUMS) and read
/// from the module only — nothing is fetched, and the schemaLocation of an invoice is never followed. The tree the hardened reader built is
/// fed into the schema validator of .NET element by element (an own stack, no recursion over the input, no second reading of the XML); what
/// is wrong is told by the step that finds it, not by the wording of the validator: an element not allowed there, content missing, a value
/// not of its type, an attribute not allowed, a required attribute missing, text where none is allowed. ZUGFeRD 1 and EXTENDED-CTC-FR have
/// no schema here: a hint says so. Not shipped: the XAdES schemas of ETSI that UBL imports for the inside of a signature (their licence is
/// not settled) — the XAdES parts of a UBL signature are not checked against a schema.
/// </summary>
public static partial class InvoiceRules
{
    /// <summary>The source of the findings of the schema check.</summary>
    public const string SchemaSource = "XML-Schema (XSD 1.0): UBL 2.1, UN/CEFACT CII D16B, Factur-X 1.09.2";

    private enum SchemaFindingKind
    {
        Element = 1,
        Content = 2,
        Value = 3,
        Attribute = 4,
        RequiredAttribute = 5,
        Text = 6,
        Incomplete = 7,
    }

    private sealed record SchemaFinding(InvoiceElement Element, SchemaFindingKind Kind, string? Value, string? Attribute);

    /// <summary>The base of the addresses of the shipped schemas: a name that resolves nowhere (.invalid), only to read them from the module.</summary>
    private const string SchemaBase = "https://schemata.pdfknead.invalid/";

    private const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>The schema of an invoice: the set (folder of Schemata) and its main documents; null if none is shipped for it.</summary>
    private static (string Set, string[] Main)? SchemaOf(InvoiceKind kind) => kind.Syntax switch
    {
        InvoiceSyntax.UblInvoice => ("ubl-2.1", ["maindoc/UBL-Invoice-2.1.xsd"]),
        InvoiceSyntax.UblCreditNote => ("ubl-2.1", ["maindoc/UBL-CreditNote-2.1.xsd"]),
        InvoiceSyntax.Cii => kind.Profile switch
        {
            InvoiceProfile.Minimum => ("facturx-1.09.2/MINIMUM", ["Factur-X_1.09.2_MINIMUM.xsd"]),
            InvoiceProfile.BasicWl => ("facturx-1.09.2/BASICWL", ["Factur-X_1.09.2_BASICWL.xsd"]),
            InvoiceProfile.Basic => ("facturx-1.09.2/BASIC", ["Factur-X_1.09.2_BASIC.xsd"]),
            InvoiceProfile.En16931 => ("facturx-1.09.2/EN16931", ["Factur-X_1.09.2_EN16931.xsd"]),
            InvoiceProfile.Extended => ("facturx-1.09.2/EXTENDED", ["Factur-X_1.09.2_EXTENDED.xsd"]),
            InvoiceProfile.ExtendedCtcFr => null,
            _ => ("cii-d16b", ["CrossIndustryInvoice_100pD16B.xsd"]),
        },
        _ => null,
    };

    /// <summary>
    /// Compiles every shipped schema now (each is otherwise compiled when an invoice of its kind is first checked, which takes a moment): for
    /// the tests that measure the time of a check, and for a caller that wants the first check of each kind as fast as the others.
    /// </summary>
    internal static void PrepareSchemas()
    {
        foreach (var kind in new[]
                 {
                     new InvoiceKind(InvoiceSyntax.UblInvoice, null, InvoiceProfile.None, null), new InvoiceKind(InvoiceSyntax.UblCreditNote, null, InvoiceProfile.None, null),
                     new InvoiceKind(InvoiceSyntax.Cii, null, InvoiceProfile.None, null), new InvoiceKind(InvoiceSyntax.Cii, null, InvoiceProfile.Minimum, null),
                     new InvoiceKind(InvoiceSyntax.Cii, null, InvoiceProfile.BasicWl, null), new InvoiceKind(InvoiceSyntax.Cii, null, InvoiceProfile.Basic, null),
                     new InvoiceKind(InvoiceSyntax.Cii, null, InvoiceProfile.En16931, null), new InvoiceKind(InvoiceSyntax.Cii, null, InvoiceProfile.Extended, null),
                 })
            if (SchemaOf(kind) is { } schema) CompiledSchema(schema.Set, schema.Main);
    }

    /// <summary>Whether an invoice is of a kind PDF-Knead knows but has no schema for (ZUGFeRD 1, EXTENDED-CTC-FR).</summary>
    private static bool WithoutSchema(InvoiceKind kind) =>
        kind.Syntax == InvoiceSyntax.ZugferdV1 || kind.Syntax == InvoiceSyntax.Cii && kind.Profile == InvoiceProfile.ExtendedCtcFr;

    /// <summary>The shipped schemas by their path below Schemata („ubl-2.1/common/…“), whatever the separator of the build.</summary>
    private static readonly Lazy<Dictionary<string, string>> SchemaResources = new(() =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames().Where(n => n.StartsWith("Schemata", StringComparison.Ordinal))
            .ToDictionary(n => n["Schemata".Length..].Replace('\\', '/').TrimStart('/'), n => n, StringComparer.Ordinal));

    private static Stream OpenSchema(string path) =>
        SchemaResources.Value.TryGetValue(path, out var name) && Assembly.GetExecutingAssembly().GetManifestResourceStream(name) is { } stream
            ? stream : throw new InvalidDataException($"Schema {path} fehlt im Baustein");

    /// <summary>Reads a schema only from the module: an address outside the shipped schemas is refused (an import of XAdES is left out so).</summary>
    private sealed class SchemaResolver : XmlResolver
    {
        public override object GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn) =>
            absoluteUri.AbsoluteUri.StartsWith(SchemaBase, StringComparison.Ordinal) && SchemaResources.Value.ContainsKey(absoluteUri.AbsoluteUri[SchemaBase.Length..])
                ? OpenSchema(absoluteUri.AbsoluteUri[SchemaBase.Length..])
                : throw new XmlException("schema not shipped");
    }

    /// <summary>
    /// The compiled schemas by set and main documents — by both: an invoice and a credit note of UBL share the set but not the main document,
    /// and a set compiled for the one does not declare the root of the other (FG 845). Each compiled once when first asked; checked under its lock.
    /// </summary>
    private static readonly Dictionary<string, Lazy<XmlSchemaSet>> SchemaSets = new(StringComparer.Ordinal);

    private static XmlSchemaSet CompiledSchema(string set, string[] main)
    {
        var key = set + "|" + string.Join("|", main);
        Lazy<XmlSchemaSet> lazy;
        lock (SchemaSets)
            if (!SchemaSets.TryGetValue(key, out lazy!)) SchemaSets[key] = lazy = new Lazy<XmlSchemaSet>(() => CompileSchema(set, main));
        return lazy.Value;
    }

    /// <summary>
    /// Compiles a set: first the documents with a DTD subset (read by this reader, which skips it — the reader of the schema set would refuse
    /// it), then the main documents with what they import. A schema that does not compile is an error of the module, found by the tests.
    /// </summary>
    private static XmlSchemaSet CompileSchema(string set, string[] main)
    {
        var schemas = new XmlSchemaSet { XmlResolver = new SchemaResolver() };
        var errors = new List<string>();
        schemas.ValidationEventHandler += (_, e) =>
        {
            if (e.Severity == XmlSeverityType.Error) errors.Add(e.Message);
        };
        var paths = SchemaResources.Value.Keys.Where(p => p.StartsWith(set + "/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        foreach (var path in paths.Where(HasDtdSubset).Concat(main.Select(m => set + "/" + m)))
        {
            using var stream = OpenSchema(path);
            using var reader = InvoiceXml.SchemaReader(stream, SchemaBase + path);
            schemas.Add(null, reader);
        }
        schemas.Compile();
        if (errors.Count > 0 || !schemas.IsCompiled) throw new InvalidDataException($"Schema {set} lässt sich nicht übersetzen: {string.Join("; ", errors)}");
        return schemas;
    }

    private static bool HasDtdSubset(string path)
    {
        using var stream = OpenSchema(path);
        var head = new byte[4096];
        var n = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return head.AsSpan(0, n).IndexOf("<!DOCTYPE"u8) >= 0;
    }

    /// <summary>Answers the prefix of an xsi:type (the one name the validator asks to resolve), as the reader found it where it stood.</summary>
    private sealed class XsiTypeResolver : IXmlNamespaceResolver
    {
        public string? Prefix;
        public string? Namespace;

        public IDictionary<string, string> GetNamespacesInScope(XmlNamespaceScope scope) => new Dictionary<string, string>();

        public string? LookupNamespace(string prefix) =>
            prefix == "xml" ? "http://www.w3.org/XML/1998/namespace" : Prefix is not null && prefix == Prefix ? Namespace : null;

        public string? LookupPrefix(string namespaceName) => null;
    }

    /// <summary>
    /// The schema check of an invoice: the tree fed into the validator in document order — start of an element with its attributes, its text,
    /// its children, its end. Each finding with the element, what is wrong (by the step that found it) and the value as written. An attachment
    /// stands for its base64 text: it was decoded when read, so its text was base64. Where the content of an element holds what its schema does
    /// not provide for there, that element and the siblings after it are assessed laxly, as XSD 1.0 has it: against their global declaration if
    /// there is one (the validator of .NET leaves them unchecked; the one of the reference checkers checks them) — each in a walk of its own,
    /// from a queue, without recursion.
    /// </summary>
    private static List<SchemaFinding> SchemaFindingsOf(Context c) =>
        SchemaOf(c.Invoice.Kind) is { } schema && c.Root.Element is { } root ? SchemaFindingsOf(root, schema.Set, schema.Main) : [];

    /// <summary>The kinds and places of what a tree breaks of a schema of the module (for the tests: a tree against a schema not its own).</summary>
    internal static List<(string Id, string Place)> CheckAgainstSchema(InvoiceElement root, string set, string main) =>
        [.. SchemaFindingsOf(root, set, [main]).Select(f => ($"PDFK-XSD-{(int)f.Kind:00}", PlaceOf(f.Element, f.Attribute)))];

    /// <summary>
    /// How many elements the lax assessments may feed per element of the invoice (plus 1 000): each element is assessed again at most once per
    /// misplaced ancestor, a prepared invoice of nested misplaced elements must still not cost depth times elements — beyond it the check ends
    /// with the hint PDFK-XSD-07.
    /// </summary>
    internal const int LaxWorkPerElement = 8;

    private static List<SchemaFinding> SchemaFindingsOf(InvoiceElement root, string set, string[] main)
    {
        var found = new List<SchemaFinding>();
        var schemas = CompiledSchema(set, main);
        lock (schemas)
        {
            // one walker for the whole check: its validator, its records and its stack are made once and used again for each lax assessment
            // (a validator per assessment cost some kilobytes each — 55 000 siblings after one misplaced element took 180 MB, FG 846)
            var walker = new SchemaWalker(schemas, found);
            var budget = LaxWorkPerElement * walker.Walk(root, null, long.MaxValue) + 1000;
            while (walker.Queue.Count > 0)
            {
                var (element, declaration) = walker.Queue.Dequeue();
                budget -= walker.Walk(element, declaration, budget);
                if (budget >= 0) continue;
                found.Add(new SchemaFinding(root, SchemaFindingKind.Incomplete, null, null));
                break;
            }
        }
        return found;
    }

    /// <summary>
    /// Feeds trees into one validator: the whole tree (no declaration), or one element laxly assessed against its global declaration — the
    /// validator initialised again for each, nothing made anew per element but the findings.
    /// </summary>
    private sealed class SchemaWalker
    {
        private readonly XmlSchemaSet _schemas;
        private readonly List<SchemaFinding> _found;
        private readonly XsiTypeResolver _resolver = new();
        private readonly XmlSchemaValidator _validator;
        private readonly XmlSchemaInfo _info = new();
        private readonly XmlSchemaInfo _attributeInfo = new();
        private readonly List<(InvoiceElement Element, int Next, XmlSchemaContentType Content, bool Broken)> _path = [];
        private InvoiceElement? _current;
        private SchemaFindingKind _kind;
        private string? _value, _attribute;
        private long _fed;

        public SchemaWalker(XmlSchemaSet schemas, List<SchemaFinding> found)
        {
            _schemas = schemas;
            _found = found;
            _validator = new XmlSchemaValidator(schemas.NameTable, schemas, _resolver, XmlSchemaValidationFlags.ProcessIdentityConstraints);
            _validator.ValidationEventHandler += (_, e) =>
            {
                if (e.Severity == XmlSeverityType.Error && _current is not null) _found.Add(new SchemaFinding(_current, _kind, _value, _attribute));
            };
        }

        /// <summary>The elements to assess laxly: each with its global declaration.</summary>
        public Queue<(InvoiceElement Element, XmlSchemaElement Declaration)> Queue { get; } = new();

        /// <summary>One walk; returns how many elements it fed — more than the budget if the budget ended it.</summary>
        public long Walk(InvoiceElement start, XmlSchemaElement? declaration, long budget)
        {
            _fed = 0;
            _path.Clear();
            if (declaration is null) _validator.Initialize();
            else _validator.Initialize(declaration);
            var mark = _found.Count;
            var (startContent, startDeclared) = Start(start);
            // a root the schema does not declare: the validator only warns (it assesses laxly) — a finding, as XSD has it (FG 845)
            if (declaration is null && !startDeclared && _found.Count == mark) _found.Add(new SchemaFinding(start, SchemaFindingKind.Element, null, null));
            _path.Add((start, 0, startContent, false));
            while (_path.Count > 0)
            {
                if (_fed > budget) return _fed;
                var top = _path[^1];
                if (top.Next < top.Element.Children.Count)
                {
                    var child = top.Element.Children[top.Next];
                    mark = _found.Count;
                    var (content, declared) = Start(child);
                    // an element the content model does not provide for here breaks the content of its parent: it and the siblings after it
                    // are assessed laxly — against their global declaration, if there is one
                    var broken = top.Broken;
                    for (var i = mark; i < _found.Count && !broken; i++)
                        broken = _found[i].Kind == SchemaFindingKind.Element && ReferenceEquals(_found[i].Element, child);
                    _path[^1] = top with { Next = top.Next + 1, Broken = broken };
                    if (broken && !declared && _schemas.GlobalElements[new XmlQualifiedName(child.Local, child.Namespace)] is XmlSchemaElement global)
                        Queue.Enqueue((child, global));
                    _path.Add((child, 0, content, false));
                }
                else
                {
                    (_current, _attribute) = (top.Element, null);
                    (_kind, _value) = top.Content is XmlSchemaContentType.TextOnly or XmlSchemaContentType.Mixed
                        ? (SchemaFindingKind.Value, TextOf(top.Element) ?? "")
                        : top.Content == XmlSchemaContentType.Empty ? (SchemaFindingKind.Text, TextOf(top.Element)) : (SchemaFindingKind.Content, null);
                    _validator.ValidateEndElement(_info);
                    _path.RemoveAt(_path.Count - 1);
                }
            }
            _validator.EndValidation();
            return _fed;
        }

        // the start of an element — its name, its attributes, its text; what content it has by the schema and whether the schema declared it
        private (XmlSchemaContentType Content, bool Declared) Start(InvoiceElement e)
        {
            _fed++;
            (_current, _kind, _value, _attribute) = (e, SchemaFindingKind.Element, null, null);
            string? xsiType = null, xsiNil = null;
            var attributes = e.Attributes;
            for (var k = 0; k < attributes.Count; k++)
            {
                var a = attributes[k];
                if (a.Namespace != XsiNamespace) continue;
                if (a.Local == "type")
                {
                    xsiType = a.Value.Trim();
                    var colon = xsiType.IndexOf(':');
                    _resolver.Prefix = colon > 0 ? xsiType[..colon] : "";
                    _resolver.Namespace = InvoiceXml.TryXsiTypeNamespace(e, out var ns) ? ns : null;
                }
                else if (a.Local == "nil") xsiNil = a.Value;
            }
            _validator.ValidateElement(e.Local, e.Namespace, _info, xsiType, xsiNil, null, null);
            var declared = _info.SchemaElement is not null;
            var content = _info.ContentType;
            for (var k = 0; k < attributes.Count; k++)
            {
                var a = attributes[k];
                if (a.Namespace == XsiNamespace) continue;
                (_kind, _value, _attribute) = (SchemaFindingKind.Attribute, a.Value, a.Local);
                var before = _found.Count;
                _attributeInfo.SchemaAttribute = null;
                _validator.ValidateAttribute(a.Local, a.Namespace, a.Value, _attributeInfo);
                // a declared attribute whose value does not fit is a wrong value, not an attribute out of place
                if (_attributeInfo.SchemaAttribute is not null)
                    for (var i = before; i < _found.Count; i++) _found[i] = _found[i] with { Kind = SchemaFindingKind.Value };
            }
            (_kind, _value, _attribute) = (SchemaFindingKind.RequiredAttribute, null, null);
            _validator.ValidateEndOfAttributes(_info);
            if (TextOf(e) is { } text)
            {
                (_kind, _value) = (content is XmlSchemaContentType.TextOnly or XmlSchemaContentType.Mixed ? SchemaFindingKind.Value : SchemaFindingKind.Text, text);
                _validator.ValidateText(text);
            }
            return (content, declared);
        }
    }

    /// <summary>The text an element held as written; for an attachment a stand-in of its base64 text (empty if it was empty).</summary>
    private static string? TextOf(InvoiceElement e) => e.Binary is { } binary ? binary.Length == 0 ? "" : "AA==" : e.Text;

    /// <summary>The conventional prefixes of the namespaces of the syntaxes, for the place of a finding.</summary>
    private static string PrefixOf(string ns) => ns switch
    {
        Rsm => "rsm:",
        Ram => "ram:",
        Udt => "udt:",
        "urn:un:unece:uncefact:data:standard:QualifiedDataType:100" => "qdt:",
        Cbc => "cbc:",
        Cac => "cac:",
        "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2" => "ext:",
        "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2" or "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2" or "" => "",
        _ => "{" + ns + "}",
    };

    /// <summary>The place of an element (and attribute) in the XML: the path with the conventional prefixes, a repeated element with its number.</summary>
    private static string PlaceOf(InvoiceElement element, string? attribute)
    {
        var parts = new List<string>();
        for (var e = element; e is not null; e = e.Parent)
        {
            var name = PrefixOf(e.Namespace) + e.Local;
            if (e.Parent is { } parent && parent.Children.Count > 1)
            {
                // the number among the siblings of the same name (by the index of names of a large parent, FG 826)
                var same = parent.Children.Count > InvoiceElement.IndexFrom
                    ? parent.PositionsOf(e.Namespace, e.Local)
                    : [.. Enumerable.Range(0, parent.Children.Count).Where(i => parent.Children[i].Local == e.Local && parent.Children[i].Namespace == e.Namespace)];
                if (same.Length > 1) name += $"[{Array.BinarySearch(same, e.Position) + 1}]";
            }
            parts.Add(name);
        }
        parts.Reverse();
        return "/" + string.Join("/", parts) + (attribute is null ? "" : "/@" + attribute);
    }

    private static IEnumerable<RuleCheck> SchemaRules()
    {
        const InvoiceRuleSeverity F = InvoiceRuleSeverity.Fatal;
        yield return new RuleCheck(new InvoiceRule("PDFK-XSD-00", SchemaSource, InvoiceRuleSeverity.Information,
                "Für diese Rechnungsart liegt kein XML-Schema bei (ZUGFeRD 1, EXTENDED-CTC-FR): ihr Aufbau wurde nicht gegen ein Schema geprüft.",
                "No XML schema is shipped for this kind of invoice (ZUGFeRD 1, EXTENDED-CTC-FR): its structure was not checked against a schema."),
            c => WithoutSchema(c.Invoice.Kind) ? [(c.Root, null)] : []);
        foreach (var (kind, de, en) in new (SchemaFindingKind, string, string)[]
                 {
                     (SchemaFindingKind.Incomplete, "Die Schemaprüfung wurde nicht zu Ende geführt: die Rechnung ist so gebaut, dass die weitere Prüfung zu aufwendig würde.",
                         "The schema check was not carried to the end: the invoice is built so that checking further would cost too much."),
                     (SchemaFindingKind.Element, "Ein Element steht an einer Stelle, an der das Schema es nicht vorsieht.", "An element stands where the schema does not provide for it."),
                     (SchemaFindingKind.Content, "Einem Element fehlt Inhalt, den das Schema verlangt (ein Pflichtelement).", "An element lacks content the schema requires (a required element)."),
                     (SchemaFindingKind.Value, "Ein Wert passt nicht zu seinem Datentyp im Schema.", "A value does not fit its data type in the schema."),
                     (SchemaFindingKind.Attribute, "Ein Attribut steht, wo das Schema es nicht vorsieht.", "An attribute stands where the schema does not provide for it."),
                     (SchemaFindingKind.RequiredAttribute, "Einem Element fehlt ein Attribut, das das Schema verlangt.", "An element lacks an attribute the schema requires."),
                     (SchemaFindingKind.Text, "Ein Element enthält Text, wo das Schema keinen vorsieht.", "An element holds text where the schema provides for none."),
                 })
        {
            var which = kind;
            yield return new RuleCheck(new InvoiceRule($"PDFK-XSD-{(int)which:00}", SchemaSource, which == SchemaFindingKind.Incomplete ? InvoiceRuleSeverity.Warning : F, de, en), _ => [],
                FindWithPlace: c => ((List<SchemaFinding>)(c.SchemaFindings ??= SchemaFindingsOf(c))).Where(f => f.Kind == which)
                    .Select(f => (c.GroupOf(f.Element), f.Attribute is null ? c.FieldOf(f.Element) : null, f.Value, new XmlPlace(f.Element, f.Attribute))));
        }
    }
}
