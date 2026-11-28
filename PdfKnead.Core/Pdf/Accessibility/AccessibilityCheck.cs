using System.Xml;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Core.Pdf.Accessibility;

/// <summary>
/// The check „barrierefrei?“ (docs/ABLAUFPLAN.md, 6.7): the machine-checkable rules of PDF/UA-1 (ISO 14289-1) or PDF/UA-2 (ISO 14289-2), read
/// on the finished file like <see cref="PdfACheck"/>, built in stages — so far A (document: identification, version, tagging marks, metadata
/// and title, language, layers, embedded files, XFA, permissions), B (fonts by the check of PDF/A level u; characters of the private use
/// areas in replacement texts and text strings), C (content against the structure tree, <see cref="TaggedContent"/>) and D (annotations by
/// what they say of themselves), E (the structure tree: roles, figures, formulas, headings, notes, lists, MathML, languages of descriptions),
/// F (annotations in the structure tree) and G (containment and tables). What is not built
/// is not checked; the report names the stages. It never claims more than it found.
/// </summary>
public static partial class AccessibilityCheck
{
    /// <summary>The stages built (docs/ABLAUFPLAN.md, 6.7 step 3).</summary>
    public const string Stages = "A, B, C, D, E, F, G";

    private const int MaxElements = 500_000, MaxDepth = 256;

    /// <summary>The numbers of the rules built for a part (1 or 2): what a measurement may expect the check to find.</summary>
    /// <param name="part">The part of ISO 14289.</param>
    /// <returns>Such as „7.1-8“.</returns>
    public static IReadOnlyList<string> RuleIds(int part) =>
    [
        .. typeof(AccessibilityRules).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => f.GetValue(null)).OfType<AccessibilityRule>()
            .Concat(AccessibilityRules.Fonts.Values)
            .Where(r => !r.Heuristic)
            .Select(r => r.Id(part)).OfType<string>().Distinct(),
    ];

    /// <summary>Checks a document.</summary>
    /// <param name="doc">The document (opened from the bytes to check).</param>
    /// <param name="profile">The profile; null: PDF/UA-2 for a file in PDF 2.0, else PDF/UA-1.</param>
    /// <param name="cancel">Cancels the check.</param>
    /// <returns>What was found, each rule once.</returns>
    public static AccessibilityReport Check(PdfDocument doc, AccessibilityProfile? profile = null, CancellationToken cancel = default)
    {
        var twoZero = PdfWriter.CompareVersions(doc.Version, "2.0") >= 0;
        var chosen = profile ?? (twoZero ? AccessibilityProfile.PdfUa2 : AccessibilityProfile.PdfUa1);
        var part = chosen switch
        {
            AccessibilityProfile.PdfUa1 => 1,
            AccessibilityProfile.PdfUa2 => 2,
            _ => twoZero ? 2 : 1,
        };
        var run = new Run(doc, part, chosen == AccessibilityProfile.HintsOnly, cancel);
        run.Document();
        run.Fonts();
        run.PrivateUse();
        run.Content();
        run.Annotations();
        run.Structure();
        run.AnnotationsInStructure();
        run.Nesting();
        run.Destinations();
        return new AccessibilityReport(chosen, part, run.Findings, Stages);
    }

    private sealed partial class Run(PdfDocument doc, int part, bool hintsOnly, CancellationToken cancel)
    {
        public List<AccessibilityFinding> Findings { get; } = [];

        private readonly HashSet<string> _found = new(StringComparer.Ordinal);

        private void Add(AccessibilityRule rule, string text, int? page = null, string? suggestion = null)
        {
            if (rule.Id(part) is not { } id || !_found.Add(id)) return;
            var severity = hintsOnly ? AccessibilitySeverity.Hint : rule.Heuristic ? AccessibilitySeverity.Check : AccessibilitySeverity.Error;
            Findings.Add(new AccessibilityFinding(id, part == 1 ? rule.Matterhorn : null, severity, text, page, rule.Repair, suggestion));
        }

        private PdfDictionary? Catalog => doc.Catalog;

        // ------------------------------------------------------------ helpers

        /// <summary>The catalogue's XMP as XML; null if there is none or it cannot be read.</summary>
        private XmlDocument? Xmp()
        {
            if (Catalog?.Get(PdfName.Metadata) is not PdfStream { DataLength: <= 16 << 20 } stream) return null;
            try
            {
                return PdfAXmp.Read(doc.GetStreamData(stream));
            }
            catch (PdfException)
            {
                return null;
            }
        }

        /// <summary>The top-level properties of an XMP packet: of every rdf:Description, its attributes and child elements.</summary>
        private static IEnumerable<XmlNode> TopLevel(XmlDocument xml)
        {
            foreach (XmlElement rdf in xml.GetElementsByTagName("RDF", XmpSchemas.Rdf))
                foreach (var description in rdf.ChildNodes.OfType<XmlElement>().Where(d => d.NamespaceURI == XmpSchemas.Rdf && d.LocalName == "Description"))
                {
                    foreach (var attribute in description.Attributes.OfType<XmlAttribute>().Where(a => a.Prefix != "xmlns" && a.Name != "xmlns" &&
                                 a.NamespaceURI is not (XmpSchemas.Rdf or XmpSchemas.Xml) && a.NamespaceURI.Length > 0))
                        yield return attribute;
                    foreach (var child in description.ChildNodes.OfType<XmlElement>()) yield return child;
                }
        }

        /// <summary>A text string as it stands (a zero at its end stays — strict readers see it).</summary>
        private static string? Text(PdfObject? value) => value is PdfString text ? PdfACheck.RawText(text) : null;

        /// <summary>The structure elements of the tree, each once, within the budget.</summary>
        private List<PdfDictionary> Elements()
        {
            if (_elements is not null) return _elements;
            _elements = [];
            if (Catalog?.GetDictionary(PdfName.Get("StructTreeRoot")) is not { } root) return _elements;
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<(PdfObject Node, int Depth)>();
            stack.Push((root.Get(PdfName.Get("K")), 0));
            while (stack.Count > 0 && _elements.Count < MaxElements)
            {
                cancel.ThrowIfCancellationRequested();
                var (node, depth) = stack.Pop();
                if (depth > MaxDepth) continue;
                switch (node)
                {
                    case PdfArray kids:
                        for (var i = kids.Count - 1; i >= 0; i--) stack.Push((kids.Get(i), depth));
                        break;
                    case PdfDictionary element when element.ContainsKey(PdfName.Get("S")) && seen.Add(element):
                        _elements.Add(element);
                        stack.Push((element.Get(PdfName.Get("K")), depth + 1));
                        break;
                }
            }
            return _elements;
        }

        private List<PdfDictionary>? _elements;

        /// <summary>
        /// The values of a name tree (ISO 32000-2, 7.9.6), each node once, within the limits of <see cref="NameTree"/>: a walk of its own
        /// here had depth and width limits but did not remember the nodes, and a tree naming itself as its kid kept the check busy without
        /// end (docs/FEHLERGEDAECHTNIS.md, No. 1264).
        /// </summary>
        private static IEnumerable<PdfObject?> NameTreeValues(PdfDictionary node) => NameTree.Entries(node).Select(e => e.Value);

        /// <summary>
        /// Whether the language of an object attached to the structure through /StructParent can be told: /Lang on its structure element or an
        /// ancestor (the catalogue's /Lang is looked at by the caller).
        /// </summary>
        private bool LanguageInStructure(PdfDictionary attached, bool own = false)
        {
            if (NumberTree.KeyOf(attached.Get(PdfName.Get("StructParent"))) is not { } key ||
                Catalog?.GetDictionary(PdfName.Get("StructTreeRoot"))?.GetDictionary(PdfName.Get("ParentTree")) is not { } parents ||
                NumberTree.Value(parents, key) is not PdfDictionary found)
                return false;
            PdfDictionary? element = found;
            for (var i = 0; i < MaxDepth && element is not null; i++)
            {
                if (Text(element.Get(PdfName.Get("Lang"))) is { Length: > 0 }) return true;
                if (own) break;
                element = element.Get(PdfName.Get("P")) as PdfDictionary;
                if (element?.ContainsKey(PdfName.Get("S")) != true) break;
            }
            return false;
        }

        /// <summary>
        /// The /Lang values of marked content in a page's content (inline property lists of BDC, and lists named from /Properties) — read
        /// without running the content: a literal or hexadecimal string after /Lang.
        /// </summary>
        private IEnumerable<string> MarkedLanguages(PdfPage page)
        {
            var found = new List<string>();
            using (var content = PageInterpreter.PageContent(doc, page.Dictionary)) Scan(content.Span, found);
            if (page.Dictionary.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("Properties")) is { } properties)
                foreach (var (key, _) in properties.Entries.Take(10_000))
                    if (properties.Get(key) is PdfDictionary list && Text(list.Get(PdfName.Get("Lang"))) is { } lang)
                        found.Add(lang);
            return found;
        }

        /// <summary>The strings after each /Lang in content bytes (literal with escapes, or hexadecimal).</summary>
        private static void Scan(ReadOnlySpan<byte> span, List<string> found)
        {
            var at = span.IndexOf("/Lang"u8);
            while (at >= 0 && found.Count < 1000)
            {
                var i = at + 5;
                if (i >= span.Length || span[i] is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t' or (byte)'(' or (byte)'<')
                {
                    while (i < span.Length && span[i] is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t') i++;
                    if (i < span.Length && span[i] == (byte)'(')
                    {
                        var bytes = new List<byte>();
                        var depth = 1;
                        for (i++; i < span.Length; i++)
                        {
                            var b = span[i];
                            if (b == (byte)'\\' && i + 1 < span.Length)
                            {
                                bytes.Add(span[++i]);
                                continue;
                            }
                            if (b == (byte)'(') depth++;
                            else if (b == (byte)')' && --depth == 0) break;
                            bytes.Add(b);
                        }
                        found.Add(PdfACheck.RawText(new PdfString([.. bytes])));
                    }
                    else if (i + 1 < span.Length && span[i] == (byte)'<' && span[i + 1] != (byte)'<')
                    {
                        var end = span[i..].IndexOf((byte)'>');
                        if (end > 0)
                        {
                            var hex = System.Text.Encoding.ASCII.GetString(span.Slice(i + 1, end - 1)).Where(Uri.IsHexDigit).ToArray();
                            var bytes = new byte[hex.Length / 2];
                            for (var k = 0; k < bytes.Length; k++) bytes[k] = Convert.ToByte(new string(hex, 2 * k, 2), 16);
                            found.Add(PdfACheck.RawText(new PdfString(bytes)));
                        }
                    }
                }
                var rest = span[(at + 5)..].IndexOf("/Lang"u8);
                at = rest < 0 ? -1 : at + 5 + rest;
            }
        }

        /// <summary>Whether a page's content shows text (a text object with a showing operator; not in forms — never more than found).</summary>
        private bool ShowsText(PdfPage page)
        {
            using var content = PageInterpreter.PageContent(doc, page.Dictionary);
            var span = content.Span;
            for (var i = 0; i + 1 < span.Length; i++)
            {
                if (span[i] != (byte)'T' || span[i + 1] is not ((byte)'j' or (byte)'J')) continue;
                var before = i == 0 || Delimiter(span[i - 1]);
                var after = i + 2 >= span.Length || Delimiter(span[i + 2]);
                if (before && after) return true;
            }
            return false;

            static bool Delimiter(byte b) => b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t' or (byte)'\f' or 0 or (byte)')' or (byte)']' or (byte)'>';
        }
    }
}
