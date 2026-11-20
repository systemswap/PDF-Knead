using System.Text;
using System.Text.Unicode;
using System.Xml;

namespace PdfKnead.EInvoice;

/// <summary>
/// Limits of reading an invoice XML (KONZEPT.md, section 5.24, „Grenzen“; research on security 13.11.2026): the XML of an e-invoice comes from
/// strangers and is read only within these bounds. Real invoices stay far below them (the largest examples of the test collections: 61 MB,
/// mostly attachments in base64).
/// </summary>
public sealed record InvoiceXmlLimits
{
    /// <summary>Largest XML in bytes (hard).</summary>
    public long MaxBytes { get; init; } = 100L << 20;

    /// <summary>Deepest nesting of elements.</summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>Most elements.</summary>
    public int MaxElements { get; init; } = 2_000_000;

    /// <summary>Most attributes of one element.</summary>
    public int MaxAttributes { get; init; } = 32;

    /// <summary>Longest attribute value in characters.</summary>
    public int MaxAttributeLength { get; init; } = 4096;

    /// <summary>Longest element or attribute name (local name) in characters.</summary>
    public int MaxNameLength { get; init; } = 256;

    /// <summary>Most different element and attribute names.</summary>
    public int MaxDistinctNames { get; init; } = 4096;

    /// <summary>Most different namespaces.</summary>
    public int MaxNamespaces { get; init; } = 64;

    /// <summary>Longest text of an element in characters — except base64 attachments, which are read in blocks (<see cref="MaxBinaryBytes"/>).</summary>
    public int MaxTextLength { get; init; } = 1 << 20;

    /// <summary>Most bytes of one base64 attachment after decoding.</summary>
    public long MaxBinaryBytes { get; init; } = 80L << 20;

    /// <summary>The usual limits.</summary>
    public static InvoiceXmlLimits Default { get; } = new();
}

/// <summary>Why an invoice XML was not read. The message never quotes the content (it is foreign input).</summary>
public enum InvoiceXmlRejection
{
    TooLarge,
    NotUtf8,
    Doctype,
    Malformed,
    TooDeep,
    TooManyElements,
    TooManyAttributes,
    AttributeTooLong,
    NameTooLong,
    TooManyNames,
    TooManyNamespaces,
    TextTooLong,
    BinaryTooLarge,
    Cancelled,

    /// <summary>Reading it into the readable form would cost far more than its size allows (stage 2, a work limit; FG 826).</summary>
    TooComplex,
}

/// <summary>An invoice XML that was refused, with the reason (never the content).</summary>
public sealed class InvoiceXmlException(InvoiceXmlRejection reason) : Exception($"invoice XML refused: {reason}")
{
    public InvoiceXmlRejection Reason { get; } = reason;
}

/// <summary>An element of an invoice XML: namespace and local name (never the prefix), its attributes, its text and its children.</summary>
public sealed class InvoiceElement
{
    internal InvoiceElement(string ns, string local, InvoiceElement? parent)
    {
        Namespace = ns;
        Local = local;
        Parent = parent;
    }

    // exact arrays, set once by the reader: a tree of a million elements must not carry a list object and its spare room per element
    // (docs/FEHLERGEDAECHTNIS.md, No. 822)
    private (string Namespace, string Local, string Value)[]? _attributes;
    private InvoiceElement[]? _children;

    public string Namespace { get; }
    public string Local { get; }
    public InvoiceElement? Parent { get; }

    /// <summary>The attributes (namespace, local name, value), without namespace declarations.</summary>
    public IReadOnlyList<(string Namespace, string Local, string Value)> Attributes => _attributes ?? [];

    /// <summary>
    /// The text directly in the element (CDATA joined, nothing trimmed); null if it has none. White space between child elements is layout and
    /// not kept; text beside child elements (mixed content, which no invoice syntax uses) is joined.
    /// </summary>
    public string? Text { get; internal set; }

    /// <summary>Decoded base64 content of an attachment element (BT-125); null for other elements.</summary>
    public byte[]? Binary { get; internal set; }

    /// <summary>The child elements in document order (complete once the element is read to its end).</summary>
    public IReadOnlyList<InvoiceElement> Children => _children ?? [];

    /// <summary>Its place among the children of its parent (0 for the root): a sibling step finds it without searching (FG 826).</summary>
    public int Position { get; private set; }

    internal void SetAttributes((string Namespace, string Local, string Value)[] attributes) => _attributes = attributes;

    internal void SetChildren(InvoiceElement[] children)
    {
        _children = children;
        for (var i = 0; i < children.Length; i++) children[i].Position = i;
    }

    /// <summary>From how many children a step by name goes through an index instead of through all children.</summary>
    internal const int IndexFrom = 16;

    /// <summary>
    /// The indexes of elements with many children, by name: built when a step first asks, kept beside the tree (most elements have a few
    /// children and never need one; a field in every element would cost the whole tree memory).
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<InvoiceElement, Dictionary<(string, string), int[]>> Indexes = new();

    /// <summary>
    /// The places of the children of that name, ascending — through an index for an element with more than <see cref="IndexFrom"/> children.
    /// Without it a step from many elements to a parent with many children (a payment means to the settlement beside 40 000 others, a line to
    /// the transaction of 100 000 lines) went through all of them each time and made the reading quadratic (FG 826).
    /// </summary>
    internal int[] PositionsOf(string ns, string local)
    {
        var index = Indexes.GetValue(this, static e =>
        {
            var lists = new Dictionary<(string, string), List<int>>();
            var children = e.Children;
            for (var i = 0; i < children.Count; i++)
            {
                var key = (children[i].Namespace, children[i].Local);
                if (!lists.TryGetValue(key, out var list)) lists[key] = list = [];
                list.Add(i);
            }
            return lists.ToDictionary(p => p.Key, p => p.Value.ToArray());
        });
        return index.TryGetValue((ns, local), out var positions) ? positions : [];
    }

    /// <summary>The first child of that name.</summary>
    public InvoiceElement? Child(string ns, string local)
    {
        if (_children is null) return null;
        if (_children.Length > IndexFrom) return PositionsOf(ns, local) is [var first, ..] ? _children[first] : null;
        foreach (var c in _children)
            if (c.Local == local && c.Namespace == ns) return c;
        return null;
    }

    /// <summary>The children of that name.</summary>
    public IEnumerable<InvoiceElement> ChildrenNamed(string ns, string local) => Children.Where(c => c.Local == local && c.Namespace == ns);

    /// <summary>An attribute value (no namespace); null if it is not there.</summary>
    public string? Attribute(string local)
    {
        // the array itself, not the list view: no enumerator per question (30 000 identifiers asked for their scheme, FG 842)
        if (_attributes is not { } all) return null;
        foreach (var a in all)
            if (a.Local == local && a.Namespace.Length == 0) return a.Value;
        return null;
    }

    /// <summary>The path from the root, for messages („/CrossIndustryInvoice/SupplyChainTradeTransaction/…“).</summary>
    public string Path
    {
        get
        {
            var parts = new List<string>();
            for (var e = this; e is not null; e = e.Parent) parts.Add(e.Local);
            parts.Reverse();
            return "/" + string.Join("/", parts);
        }
    }
}

/// <summary>
/// What the reader noticed besides the elements: things a careful reader reports but never acts on. <see cref="DeclaredEncoding"/> is what
/// the XML declaration names; the data is read as UTF-8 whatever it says (EN 16931 syntaxes require UTF-8), so a different name is a finding
/// for the rules, never a reason to decode otherwise.
/// </summary>
public sealed record InvoiceXmlNotes(bool SchemaLocation, bool XmlBase, bool ProcessingInstructions, bool XInclude, bool Comments, string? DeclaredEncoding = null);

/// <summary>A read invoice XML: its root element and what was noticed.</summary>
public sealed record InvoiceXmlDocument(InvoiceElement Root, InvoiceXmlNotes Notes, int Elements, int MaxDepthSeen);

/// <summary>
/// Reads an invoice XML safely (KONZEPT.md, section 5.24, „Bauvorgaben Sicherheit“ 1–6): the only way invoice XML is read in PDF-Knead.
/// No DTD (a DOCTYPE is refused as a sign of an attack), no resolver (nothing is ever fetched), no schemaLocation, no inline schema; UTF-8 only;
/// every count and length within <see cref="InvoiceXmlLimits"/>; read in one loop with an own stack — no recursion over the input, so a deep
/// nesting cannot overflow the stack; base64 attachments decoded in blocks. Any failure refuses the file with a reason, never with content.
/// </summary>
public static class InvoiceXml
{
    /// <summary>Namespaces whose elements carry base64 attachment data (CII and UBL), by local name.</summary>
    private static readonly HashSet<string> BinaryElements = new(StringComparer.Ordinal) { "AttachmentBinaryObject", "EmbeddedDocumentBinaryObject" };

    private const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";
    private const string XIncludeNamespace = "http://www.w3.org/2001/XInclude";
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";
    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    /// <summary>
    /// The namespace the prefix of an element's xsi:type names, as declared where the element stood (namespace declarations are not kept in the
    /// tree): per document, by its root, only for the elements that carry an xsi:type — the schema check asks it (InvoiceRules.Schema.cs).
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<InvoiceElement, Dictionary<InvoiceElement, string?>> XsiTypes = new();

    /// <summary>The namespace of the prefix of an element's xsi:type (null if its prefix is not declared); false if the element has none.</summary>
    internal static bool TryXsiTypeNamespace(InvoiceElement element, out string? ns)
    {
        ns = null;
        var root = element;
        while (root.Parent is not null) root = root.Parent;
        return XsiTypes.TryGetValue(root, out var types) && types.TryGetValue(element, out ns);
    }

    /// <summary>The settings of the one reader: no DTD, no resolver, entities at most one character, nothing validated or fetched.</summary>
    internal static XmlReaderSettings Settings(InvoiceXmlLimits limits) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = XmlResolver.ThrowingResolver,
        MaxCharactersFromEntities = 1,
        MaxCharactersInDocument = limits.MaxBytes,
        ValidationType = ValidationType.None,
        ValidationFlags = System.Xml.Schema.XmlSchemaValidationFlags.None,
        IgnoreComments = false,
        IgnoreProcessingInstructions = false,
        IgnoreWhitespace = false,
        CloseInput = true,
        CheckCharacters = true,
    };

    /// <summary>
    /// The one other reader of the module: for a schema shipped with it (InvoiceRules.Schema.cs), never for an invoice — the internal subset of
    /// a DTD is skipped (the copy of the XML signature schema in UBL has one, unused), nothing is fetched by the reader itself, the schema set
    /// resolves its imports only from the module.
    /// </summary>
    internal static XmlReader SchemaReader(Stream schema, string baseUri) => XmlReader.Create(schema, new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    }, baseUri);

    /// <summary>
    /// Whether data looks like XML at all: after a byte order mark and white space it begins with „&lt;“ (UTF-8) or is UTF-16 with a byte order
    /// mark. Attachments that are not (PDF, images, office files) are other documents of the invoice, never read as XML.
    /// </summary>
    public static bool LooksLikeXml(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || data.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return true;
        if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) data = data[3..];
        foreach (var b in data)
        {
            if (b is 0x20 or 0x09 or 0x0D or 0x0A) continue;   // space, tab, carriage return, line feed
            return b == (byte)'<';
        }
        return false;
    }

    /// <summary>Reads an invoice XML.</summary>
    /// <param name="data">The bytes (UTF-8, with or without BOM).</param>
    /// <param name="limits">The limits; null for <see cref="InvoiceXmlLimits.Default"/>.</param>
    /// <param name="cancel">Cancels reading.</param>
    /// <exception cref="InvoiceXmlException">Refused; the reason says why.</exception>
    public static InvoiceXmlDocument Read(byte[] data, InvoiceXmlLimits? limits = null, CancellationToken cancel = default)
    {
        limits ??= InvoiceXmlLimits.Default;
        if (data.LongLength > limits.MaxBytes) throw new InvoiceXmlException(InvoiceXmlRejection.TooLarge);
        var start = data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
        // only UTF-8: a BOM of UTF-16/32 or bytes that are no UTF-8 are refused before the reader sees them
        if (data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) || !Utf8.IsValid(data.AsSpan(start)))
            throw new InvoiceXmlException(InvoiceXmlRejection.NotUtf8);
        try
        {
            // through a text reader of strict UTF-8: the XML reader switches encodings only on a byte stream, so an encoding declaration
            // („ISO-8859-1“, „UTF-16“) can never make it decode the checked bytes differently
            using var text = new StreamReader(new MemoryStream(data, start, data.Length - start, writable: false),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: false);
            using var reader = XmlReader.Create(text, Settings(limits));
            return Build(reader, limits, cancel);
        }
        catch (InvoiceXmlException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new InvoiceXmlException(InvoiceXmlRejection.Cancelled);
        }
        catch (XmlException) when (HasDoctype(data.AsSpan(start)))
        {
            // decided by the data, not by the wording of the message (an element named „DTD“ that is broken is no DTD)
            throw new InvoiceXmlException(InvoiceXmlRejection.Doctype);
        }
        catch (Exception e) when (e is not OutOfMemoryException and not InsufficientExecutionStackException)
        {
            // any other failure of the reader (it has thrown more than XmlException on crafted input): refused, without content
            throw new InvoiceXmlException(InvoiceXmlRejection.Malformed);
        }
    }

    /// <summary>
    /// Whether the prolog holds a document type declaration: after white space, the XML declaration, processing instructions and comments,
    /// „&lt;!DOCTYPE“ comes before the first element. Reads only the prolog, and each part once.
    /// </summary>
    internal static bool HasDoctype(ReadOnlySpan<byte> data)
    {
        var i = 0;
        while (i < data.Length)
        {
            var b = data[i];
            if (b is 0x20 or 0x09 or 0x0D or 0x0A) { i++; continue; }
            if (b != (byte)'<') return false;
            var rest = data[i..];
            if (rest.StartsWith("<!DOCTYPE"u8)) return true;
            int end, closing;
            if (rest.StartsWith("<?"u8)) (end, closing) = (rest.IndexOf("?>"u8), 2);
            else if (rest.StartsWith("<!--"u8)) (end, closing) = (rest.IndexOf("-->"u8), 3);
            else return false;   // the first element, or something that is neither
            if (end < 0) return false;
            i += end + closing;
        }
        return false;
    }

    /// <summary>
    /// An open element while it is read: its children and its text are collected here, in lists and builders reused for every element at the
    /// same depth, and handed to the element as exact arrays and one string when it ends.
    /// </summary>
    private sealed class Frame
    {
        public InvoiceElement Element = null!;
        public readonly List<InvoiceElement> Children = [];
        public readonly StringBuilder Text = new();
        public bool HasText;
    }

    private static InvoiceXmlDocument Build(XmlReader reader, InvoiceXmlLimits limits, CancellationToken cancel)
    {
        InvoiceElement? root = null;
        var frames = new List<Frame>();   // frames[0 .. depth-1] are the open elements
        var depth = 0;
        var run = new StringBuilder();   // character data since the last tag
        var attributes = new List<(string, string, string)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        bool schemaLocation = false, xmlBase = false, pis = false, xinclude = false, comments = false;
        Dictionary<InvoiceElement, string?>? xsiTypes = null;
        string? declaredEncoding = null;
        int elements = 0, maxDepth = 0;
        var chunk = new char[16 * 1024];
        byte[]? firstBlock = null;   // the first block of every attachment, one for the whole reading (FG 829)

        void Name(string ns, string local)
        {
            if (local.Length > limits.MaxNameLength) throw new InvoiceXmlException(InvoiceXmlRejection.NameTooLong);
            if (names.Count < limits.MaxDistinctNames || names.Contains(local)) names.Add(local);
            else throw new InvoiceXmlException(InvoiceXmlRejection.TooManyNames);
            if (ns.Length > 0 && !namespaces.Contains(ns))
            {
                if (namespaces.Count >= limits.MaxNamespaces) throw new InvoiceXmlException(InvoiceXmlRejection.TooManyNamespaces);
                namespaces.Add(ns);
            }
        }

        // a run of character data ends at a tag: white space before a child or beside children is layout and dropped; anything else is
        // collected for the open element — in the builder of its depth, so that many pieces stay linear (no string joined again and again)
        void EndRun(bool childFollows)
        {
            if (run.Length == 0) return;
            if (depth == 0) { run.Clear(); return; }
            var frame = frames[depth - 1];
            if ((childFollows || frame.Children.Count > 0) && IsWhiteSpace(run)) { run.Clear(); return; }
            if (frame.Text.Length + run.Length > limits.MaxTextLength) throw new InvoiceXmlException(InvoiceXmlRejection.TextTooLong);
            frame.Text.Append(run);
            frame.HasText = true;
            run.Clear();
        }

        var positioned = false;   // the reader already stands on the next node (after an attachment was read)
        while (true)
        {
            if ((elements & 1023) == 0) cancel.ThrowIfCancellationRequested();
            if (positioned)
            {
                positioned = false;
                if (reader.EOF) break;
            }
            else if (!reader.Read()) break;
            switch (reader.NodeType)
            {
                case XmlNodeType.DocumentType:
                    throw new InvoiceXmlException(InvoiceXmlRejection.Doctype);
                case XmlNodeType.XmlDeclaration:
                    declaredEncoding = reader.GetAttribute("encoding");
                    if (declaredEncoding?.Length > limits.MaxAttributeLength) throw new InvoiceXmlException(InvoiceXmlRejection.AttributeTooLong);
                    break;
                case XmlNodeType.Element:
                {
                    EndRun(childFollows: true);
                    if (++elements > limits.MaxElements) throw new InvoiceXmlException(InvoiceXmlRejection.TooManyElements);
                    if (depth + 1 > limits.MaxDepth) throw new InvoiceXmlException(InvoiceXmlRejection.TooDeep);
                    Name(reader.NamespaceURI, reader.LocalName);
                    if (reader.NamespaceURI == XIncludeNamespace) xinclude = true;
                    var element = new InvoiceElement(reader.NamespaceURI, reader.LocalName, depth > 0 ? frames[depth - 1].Element : null);
                    if (reader.HasAttributes)
                    {
                        // namespace declarations count with the reader, not with the invoice: some room for them, the real count below
                        if (reader.AttributeCount > limits.MaxAttributes + 16) throw new InvoiceXmlException(InvoiceXmlRejection.TooManyAttributes);
                        attributes.Clear();
                        for (var i = 0; i < reader.AttributeCount; i++)
                        {
                            reader.MoveToAttribute(i);
                            var ns = reader.NamespaceURI;
                            // a namespace declaration is no attribute of the invoice, but its value becomes the namespace of every element
                            // that uses it: the same length limit — a namespace of megabytes was hashed for each element (FG 829)
                            if (reader.Value.Length > limits.MaxAttributeLength) throw new InvoiceXmlException(InvoiceXmlRejection.AttributeTooLong);
                            if (ns == XmlnsNamespace) continue;
                            if (ns == XsiNamespace && reader.LocalName is "schemaLocation" or "noNamespaceSchemaLocation") schemaLocation = true;
                            if (ns == XsiNamespace && reader.LocalName == "type")
                            {
                                // the prefix of the type, resolved where it stands (namespaces declared on this element and above)
                                var value = reader.Value.Trim();
                                var colon = value.IndexOf(':');
                                (xsiTypes ??= new(ReferenceEqualityComparer.Instance))[element] = reader.LookupNamespace(colon > 0 ? value[..colon] : "");
                            }
                            if (ns == XmlNamespace && reader.LocalName == "base") xmlBase = true;
                            if (reader.Value.Length > limits.MaxAttributeLength) throw new InvoiceXmlException(InvoiceXmlRejection.AttributeTooLong);
                            Name(ns, reader.LocalName);
                            if (attributes.Count >= limits.MaxAttributes) throw new InvoiceXmlException(InvoiceXmlRejection.TooManyAttributes);
                            attributes.Add((ns, reader.LocalName, reader.Value));
                        }
                        reader.MoveToElement();
                        if (attributes.Count > 0) element.SetAttributes([.. attributes]);
                    }
                    if (depth > 0) frames[depth - 1].Children.Add(element);
                    else root ??= element;
                    var empty = reader.IsEmptyElement;
                    maxDepth = Math.Max(maxDepth, depth + 1);
                    if (!empty && BinaryElements.Contains(element.Local))
                    {
                        element.Binary = ReadBinary(reader, limits, firstBlock ??= new byte[64 * 1024], cancel);
                        // the end tag is consumed and the reader stands on the node after it: that node is handled next, not skipped
                        positioned = true;
                        break;
                    }
                    if (!empty)
                    {
                        if (depth == frames.Count) frames.Add(new Frame());
                        var frame = frames[depth++];
                        frame.Element = element;
                        frame.Children.Clear();
                        frame.Text.Clear();
                        frame.HasText = false;
                    }
                    break;
                }
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                case XmlNodeType.Whitespace:
                    if (depth == 0) break;
                    if (reader.CanReadValueChunk)
                    {
                        // in pieces: a text node of hundreds of MB is refused before it is held as one string
                        int got;
                        while ((got = reader.ReadValueChunk(chunk, 0, chunk.Length)) > 0)
                        {
                            if (run.Length + got > limits.MaxTextLength) throw new InvoiceXmlException(InvoiceXmlRejection.TextTooLong);
                            run.Append(chunk, 0, got);
                        }
                    }
                    else
                    {
                        if (run.Length + reader.Value.Length > limits.MaxTextLength) throw new InvoiceXmlException(InvoiceXmlRejection.TextTooLong);
                        run.Append(reader.Value);
                    }
                    break;
                case XmlNodeType.EndElement:
                    EndRun(childFollows: false);
                    if (depth > 0)
                    {
                        var frame = frames[--depth];
                        if (frame.Children.Count > 0) frame.Element.SetChildren([.. frame.Children]);
                        if (frame.HasText && frame.Text.Length > 0) frame.Element.Text = frame.Text.ToString();
                        frame.Children.Clear();
                        frame.Element = null!;
                    }
                    break;
                case XmlNodeType.ProcessingInstruction:
                    pis = true;
                    break;
                case XmlNodeType.Comment:
                    comments = true;
                    break;
            }
        }
        if (root is null) throw new InvoiceXmlException(InvoiceXmlRejection.Malformed);
        if (xsiTypes is not null) XsiTypes.AddOrUpdate(root, xsiTypes);
        return new InvoiceXmlDocument(root, new InvoiceXmlNotes(schemaLocation, xmlBase, pis, xinclude, comments, declaredEncoding), elements, maxDepth);
    }

    /// <summary>
    /// An attachment: decoded into blocks, never held as one string, and joined once at the end — a growing stream copied itself at every
    /// doubling and took three to four times the data at its peak, the blocks take twice (docs/FEHLERGEDAECHTNIS.md, No. 821).
    /// </summary>
    /// <summary>
    /// Decodes a base64 attachment in blocks and joins them once into an array of its exact size. <paramref name="firstBlock"/> is the first
    /// block, shared by all attachments of a reading: a new 64 KB block for each of 1 000 attachments of a few bytes took 100 times the
    /// file's size (FG 829). Only a longer attachment gets blocks of its own, each twice the one before, up to 4 MB.
    /// </summary>
    private static byte[] ReadBinary(XmlReader reader, InvoiceXmlLimits limits, byte[] firstBlock, CancellationToken cancel)
    {
        var blocks = new List<byte[]>();
        var block = firstBlock;
        int used = 0, read;
        long total = 0;
        while ((read = reader.ReadElementContentAsBase64(block, used, block.Length - used)) > 0)
        {
            total += read;
            if (total > limits.MaxBinaryBytes) throw new InvoiceXmlException(InvoiceXmlRejection.BinaryTooLarge);
            used += read;
            if (used == block.Length)
            {
                blocks.Add(block);
                block = new byte[Math.Min(block.Length * 2, 4 << 20)];
                used = 0;
            }
            cancel.ThrowIfCancellationRequested();
        }
        var joined = new byte[total];
        var at = 0;
        foreach (var b in blocks)
        {
            b.CopyTo(joined, at);
            at += b.Length;
        }
        Array.Copy(block, 0, joined, at, used);
        return joined;
    }

    private static bool IsWhiteSpace(StringBuilder text)
    {
        foreach (var piece in text.GetChunks())
            foreach (var c in piece.Span)
                if (c is not (' ' or '\t' or '\r' or '\n')) return false;
        return true;
    }
}
