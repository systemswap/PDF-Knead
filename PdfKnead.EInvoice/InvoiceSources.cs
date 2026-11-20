using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.EInvoice;

/// <summary>Where a file of the PDF was found.</summary>
public enum InvoiceFilePlace
{
    /// <summary>In the /AF array of the catalog (associated with the whole document, as ZUGFeRD and Factur-X require).</summary>
    CatalogAf,

    /// <summary>In the name tree /EmbeddedFiles.</summary>
    EmbeddedFiles,

    /// <summary>Behind a file attachment annotation of a page.</summary>
    Annotation,
}

/// <summary>
/// An embedded file that may be the invoice: its names (/F, /UF), its relationship (/AFRelationship), its declared type (/Subtype of the
/// stream), where it was found (also several places for the same file), the object of its data and its stored length, and whether the
/// stream names its modification date (/Params /ModDate, which ZUGFeRD and Factur-X ask for).
/// </summary>
public sealed record InvoiceFile(string? Name, string? UnicodeName, string? Relationship, string? MimeType, PdfObjectId StreamId, long StoredLength,
    bool HasModDate = false)
{
    /// <summary>The object number of its data stream.</summary>
    public int ObjectNumber => StreamId.Number;

    /// <summary>Every place it was found (catalog /AF, name tree, annotation on page n).</summary>
    public List<(InvoiceFilePlace Place, int Page)> Places { get; } = [];

    /// <summary>The name to show: /UF if there is one, else /F.</summary>
    public string DisplayName => UnicodeName ?? Name ?? "";
}

/// <summary>
/// The invoice data of the XMP metadata (ZUGFeRD 1, ZUGFeRD 2.0 „zf“, ZUGFeRD 2.1 and later and Factur-X „fx“): what the PDF says about its
/// invoice, and the PDF/A extension schemas the XMP describes (PDF/A asks for one for every namespace outside the predefined schemas).
/// </summary>
public sealed record InvoiceXmp(string Namespace, string? DocumentType, string? DocumentFileName, string? Version, string? ConformanceLevel)
{
    /// <summary>The PDF/A extension schemas of the XMP (pdfaExtension:schemas), in the order written.</summary>
    public IReadOnlyList<InvoiceXmpSchema> Extensions { get; init; } = [];
}

/// <summary>A PDF/A extension schema of the XMP: the namespace it describes, its prefix and the names of its properties (as written; any may be missing).</summary>
public sealed record InvoiceXmpSchema(string? NamespaceUri, string? Prefix, IReadOnlyList<string> Properties);

/// <summary>What the PDF holds of an e-invoice: the embedded files that may be one, the XMP data, and what was noticed on the way.</summary>
public sealed record InvoiceSearch(IReadOnlyList<InvoiceFile> Files, InvoiceXmp? Xmp, IReadOnlyList<string> Notes);

/// <summary>
/// Finds the embedded files of a PDF that may hold an e-invoice (KONZEPT.md, section 5.24, stage 1): the catalog's /AF, the name tree
/// /EmbeddedFiles and file attachment annotations — only files embedded as streams (/EF), never a file outside the PDF, a URL or an
/// action. The name tree is walked with an own stack and a list of the objects seen (loops, depth ≤ 32, at most 10 000 entries); nothing is
/// decided by the file name, which only checks what the XML itself says.
/// </summary>
public static class InvoiceSources
{
    /// <summary>Deepest name tree walked.</summary>
    public const int MaxTreeDepth = 32;

    /// <summary>Most entries of the name tree taken.</summary>
    public const int MaxEntries = 10_000;

    /// <summary>The XMP namespace of the invoice data of Factur-X and ZUGFeRD 2.1 and later (prefix fx).</summary>
    public const string FacturXNamespace = "urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#";

    /// <summary>The XMP namespace of ZUGFeRD 2.0 as ZUGFeRD 2.5.2 names it for the files still supported (6.3.2; prefix zf, version 2p0).</summary>
    public const string Zugferd20Namespace = "urn:zugferd:pdfa:CrossIndustryDocument:invoice:1p0#";

    /// <summary>The XMP namespace of ZUGFeRD 2.0 as files of that version write it.</summary>
    public const string Zugferd20NamespaceWritten = "urn:zugferd:pdfa:CrossIndustryDocument:invoice:2p0#";

    /// <summary>The XMP namespace of ZUGFeRD 1.</summary>
    public const string ZugferdV1Namespace = "urn:ferd:pdfa:CrossIndustryDocument:invoice:1p0#";

    /// <summary>The XMP namespaces of the invoice data, the newest first.</summary>
    public static readonly string[] XmpNamespaces = [FacturXNamespace, Zugferd20Namespace, Zugferd20NamespaceWritten, ZugferdV1Namespace];

    /// <summary>
    /// Tells the core that the invoice data of the XMP describe an attached file: where a save drops the attachments, they go too — the file
    /// must not claim an invoice that is gone (decided on 15.11.2026; Fehlergedächtnis 865). Called when the module is docked.
    /// </summary>
    public static void RegisterXmpData() => Core.Pdf.Writing.AttachedFileMetadata.Register(XmpNamespaces);

    private const string PdfaSchemaNamespace = "http://www.aiim.org/pdfa/ns/schema#", PdfaPropertyNamespace = "http://www.aiim.org/pdfa/ns/property#";

    private static readonly PdfName Af = PdfName.Get("AF"), Names = PdfName.Get("Names"), EmbeddedFiles = PdfName.Get("EmbeddedFiles"),
        Kids = PdfName.Get("Kids"), Ef = PdfName.Get("EF"), F = PdfName.Get("F"), Uf = PdfName.Get("UF"), AfRelationship = PdfName.Get("AFRelationship"),
        Subtype = PdfName.Get("Subtype"), Annots = PdfName.Get("Annots"), Fs = PdfName.Get("FS"), FileAttachment = PdfName.Get("FileAttachment"),
        Metadata = PdfName.Get("Metadata"), Length = PdfName.Get("Length"), Params = PdfName.Get("Params"), ModDate = PdfName.Get("ModDate");

    /// <summary>Searches a document.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="cancel">Cancels the search.</param>
    public static InvoiceSearch Find(PdfDocument doc, CancellationToken cancel = default)
    {
        var files = new Dictionary<int, InvoiceFile>();
        var notes = new List<string>();
        // each note once: a crafted tree repeats the same node a million times
        void Note(string note)
        {
            if (!notes.Contains(note)) notes.Add(note);
        }
        PdfDictionary? catalog;
        try
        {
            catalog = doc.Catalog;
        }
        catch (PdfException)
        {
            return new InvoiceSearch([], null, [Tr.T("Katalog beschädigt: keine Anhänge lesbar")]);
        }

        // each source on its own: a damaged one is passed over with a note, the others are still searched
        void Guard(string source, Action search)
        {
            try
            {
                search();
            }
            catch (Exception e) when (e is PdfException or InvalidCastException or InvalidDataException)
            {
                Note(Tr.T("{0} beschädigt, Rest übergangen", source));
            }
        }

        void Take(PdfObject spec, InvoiceFilePlace place, int page)
        {
            var dict = Resolve(doc, spec) as PdfDictionary;
            if (dict is null) return;
            // only an embedded stream counts; a file specification without /EF points outside the PDF and is never followed
            if (dict.GetDictionary(Ef) is not { } ef) return;
            var raw = ef.GetRaw(Uf) as PdfReference ?? ef.GetRaw(F) as PdfReference;
            if (raw is null || Resolve(doc, raw) is not PdfStream stream) return;
            var number = raw.Id.Number;
            if (!files.TryGetValue(number, out var file))
            {
                var mime = stream.Dictionary.GetName(Subtype)?.Value;
                files[number] = file = new InvoiceFile(Text(dict.GetString(F)), Text(dict.GetString(Uf)), dict.GetName(AfRelationship)?.Value,
                    mime, raw.Id, stream.Dictionary.GetInteger(Length) ?? 0, stream.Dictionary.GetDictionary(Params)?.GetRaw(ModDate) is not null);
            }
            file.Places.Add((place, page));
        }

        Guard(Tr.T("/AF des Katalogs"), () =>
        {
            if (catalog?.GetArray(Af) is { } af)
                for (var i = 0; i < Math.Min(af.Count, MaxEntries); i++) Take(af[i], InvoiceFilePlace.CatalogAf, 0);
        });

        Guard(Tr.T("Namensbaum /EmbeddedFiles"), () =>
        {
            if (catalog?.GetDictionary(Names)?.GetDictionary(EmbeddedFiles) is not { } tree) return;
            // the name tree with an own stack: no recursion over the input, loops and depth bounded
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<(PdfDictionary Node, int Depth)>();
            pending.Push((tree, 0));
            int entries = 0, visits = 0;
            while (pending.Count > 0 && entries < MaxEntries)
            {
                cancel.ThrowIfCancellationRequested();
                // the visits are bounded as a whole: shared /Kids arrays let a small file name the same nodes a hundred million times
                if (++visits > 4 * MaxEntries) { Note(Tr.T("Namensbaum /EmbeddedFiles: mehr als {0} Knoten besucht, Rest übergangen", 4 * MaxEntries)); break; }
                var (node, depth) = pending.Pop();
                if (!seen.Add(node)) { Note(Tr.T("Namensbaum /EmbeddedFiles: Schleife übergangen")); continue; }
                if (depth > MaxTreeDepth) { Note(Tr.T("Namensbaum /EmbeddedFiles: tiefer als erlaubt, Rest übergangen")); continue; }
                if (node.GetArray(Names) is { } pairs)
                    for (var i = 1; i < pairs.Count && entries < MaxEntries; i += 2, entries++) Take(pairs[i], InvoiceFilePlace.EmbeddedFiles, 0);
                if (node.GetArray(Kids) is { } kids)
                {
                    // the nodes waiting are bounded too: a /Kids array of millions of entries is not taken whole
                    if (kids.Count + pending.Count > MaxEntries) Note(Tr.T("Namensbaum /EmbeddedFiles: mehr als {0} Knoten, Rest übergangen", MaxEntries));
                    for (var i = Math.Min(kids.Count, MaxEntries - pending.Count) - 1; i >= 0; i--)
                        if (Resolve(doc, kids[i]) is PdfDictionary kid) pending.Push((kid, depth + 1));
                }
            }
            if (entries >= MaxEntries) Note(Tr.T("Namensbaum /EmbeddedFiles: mehr als {0} Einträge, Rest übergangen", MaxEntries));
        });

        Guard(Tr.T("Seitenbaum"), () =>
        {
            for (var p = 0; p < doc.PageCount; p++)
            {
                cancel.ThrowIfCancellationRequested();
                if (doc.Pages[p].Dictionary.GetArray(Annots) is not { } annots) continue;
                for (var i = 0; i < Math.Min(annots.Count, MaxEntries); i++)
                    if (Resolve(doc, annots[i]) is PdfDictionary annot && annot.GetName(Subtype) == FileAttachment && annot.GetRaw(Fs) is { } fs)
                        Take(fs, InvoiceFilePlace.Annotation, p + 1);
            }
        });

        return new InvoiceSearch([.. files.Values.OrderBy(f => f.ObjectNumber)], Xmp(doc, catalog), notes);
    }

    /// <summary>The decoded data of a file, or null if it is larger than allowed or cannot be read (damaged, encrypted without key).</summary>
    /// <param name="doc">The document.</param>
    /// <param name="file">The file.</param>
    /// <param name="maxBytes">Largest data accepted (the XML limit).</param>
    public static byte[]? Data(PdfDocument doc, InvoiceFile file, long maxBytes)
    {
        try
        {
            if (doc.Resolve(file.StreamId) is not PdfStream stream) return null;
            var data = doc.DecodeStream(stream).Data;
            return data.LongLength <= maxBytes ? data : null;
        }
        catch (Exception e) when (e is PdfException or InvalidDataException or IOException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The invoice data of the XMP metadata, read with the hardened reader, with the PDF/A extension schemas it describes; null if it has
    /// neither. Without invoice data the namespace is empty.
    /// </summary>
    private static InvoiceXmp? Xmp(PdfDocument doc, PdfDictionary? catalog)
    {
        try
        {
            if (catalog?.GetStream(Metadata) is not { } stream) return null;
            var data = doc.DecodeStream(stream).Data;
            var xml = InvoiceXml.Read(data, new InvoiceXmlLimits { MaxBytes = 10L << 20, MaxElements = 100_000 });
            var extensions = ExtensionSchemas(xml.Root);
            foreach (var ns in XmpNamespaces)
            {
                var type = Value(xml.Root, ns, "DocumentType");
                var name = Value(xml.Root, ns, "DocumentFileName");
                var version = Value(xml.Root, ns, "Version");
                var level = Value(xml.Root, ns, "ConformanceLevel");
                if (type is not null || name is not null || version is not null || level is not null)
                    return new InvoiceXmp(ns, type, name, version, level) { Extensions = extensions };
            }
            return extensions.Count > 0 ? new InvoiceXmp("", null, null, null, null) { Extensions = extensions } : null;
        }
        catch (Exception e) when (e is PdfException or InvoiceXmlException or InvalidDataException or IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The first value of a property below <paramref name="root"/>, written as an element or as an attribute (XMP allows both).</summary>
    private static string? Value(InvoiceElement root, string ns, string local)
    {
        var pending = new Stack<InvoiceElement>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var e = pending.Pop();
            if (e.Namespace == ns && e.Local == local && e.Text is { } text) return text.Trim();
            foreach (var a in e.Attributes)
                if (a.Namespace == ns && a.Local == local) return a.Value.Trim();
            for (var i = e.Children.Count - 1; i >= 0; i--) pending.Push(e.Children[i]);
        }
        return null;
    }

    /// <summary>
    /// The PDF/A extension schemas: every node that carries a pdfaSchema:namespaceURI (as a child or an attribute) is one, with its
    /// pdfaSchema:prefix and the pdfaProperty:name of the properties below it.
    /// </summary>
    private static List<InvoiceXmpSchema> ExtensionSchemas(InvoiceElement root)
    {
        var schemas = new List<InvoiceXmpSchema>();
        var pending = new Stack<InvoiceElement>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var e = pending.Pop();
            var own = e.Attributes.Any(a => a.Namespace == PdfaSchemaNamespace && a.Local == "namespaceURI")
                || e.Children.Any(c => c.Namespace == PdfaSchemaNamespace && c.Local == "namespaceURI");
            if (own)
            {
                schemas.Add(new InvoiceXmpSchema(Own(e, "namespaceURI"), Own(e, "prefix"), Properties(e)));
                continue;   // a schema holds no further schema
            }
            for (var i = e.Children.Count - 1; i >= 0; i--) pending.Push(e.Children[i]);
        }
        return schemas;

        // a value of the schema node itself (not of a property below it)
        static string? Own(InvoiceElement e, string local)
        {
            foreach (var a in e.Attributes)
                if (a.Namespace == PdfaSchemaNamespace && a.Local == local) return a.Value.Trim();
            return e.Children.FirstOrDefault(c => c.Namespace == PdfaSchemaNamespace && c.Local == local)?.Text?.Trim();
        }

        static List<string> Properties(InvoiceElement schema)
        {
            var names = new List<string>();
            var below = new Stack<InvoiceElement>();
            below.Push(schema);
            while (below.Count > 0)
            {
                var e = below.Pop();
                foreach (var a in e.Attributes)
                    if (a.Namespace == PdfaPropertyNamespace && a.Local == "name") names.Add(a.Value.Trim());
                if (e.Namespace == PdfaPropertyNamespace && e.Local == "name" && e.Text is { } text) names.Add(text.Trim());
                for (var i = e.Children.Count - 1; i >= 0; i--) below.Push(e.Children[i]);
            }
            return names;
        }
    }

    private static PdfObject? Resolve(PdfDocument doc, PdfObject obj)
    {
        try
        {
            return obj is PdfReference r ? doc.Resolve(r.Id) : obj;
        }
        catch (PdfException)
        {
            return null;
        }
    }

    private static string? Text(PdfString? s) => s?.ToText();
}
