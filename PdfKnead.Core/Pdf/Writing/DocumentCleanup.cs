using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>What document sanitising removes (when saving, see <see cref="PdfSaveOptions.Cleanup"/>).</summary>
[Flags]
public enum CleanupItems
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Document properties (author, title, application, date …) and XMP metadata, also on images and fonts.</summary>
    Metadata = 1 << 0,

    /// <summary>Embedded files, file attachment annotations, associated files (/AF).</summary>
    Attachments = 1 << 1,

    /// <summary>JavaScript and actions that run on opening, clicking or printing, launch programs or send data.</summary>
    Scripts = 1 << 2,

    /// <summary>Page thumbnails (they show the old state, for example before redaction).</summary>
    Thumbnails = 1 << 3,

    /// <summary>Private data of other applications (/PieceInfo, for example the complete file of a drawing or image-editing program).</summary>
    PrivateData = 1 << 4,

    /// <summary>Comments and markups of other applications (links, form fields and the program's own elements stay).</summary>
    Comments = 1 << 5,

    /// <summary>Bookmarks (titles can reveal names and content).</summary>
    Bookmarks = 1 << 6,

    /// <summary>Structure tree: tags with alternative and replacement texts; the document is less accessible to screen readers afterwards.</summary>
    Structure = 1 << 7,

    /// <summary>Content of hidden layers (it is readable in the file although it is not displayed).</summary>
    HiddenLayers = 1 << 8,

    /// <summary>Invisible text (text rendering mode 3; mode 7 is a clip and stays), such as the OCR layer of a scan; the document is no longer searchable afterwards.</summary>
    InvisibleText = 1 << 9,

    /// <summary>
    /// What nobody sees: hidden form fields and annotations, those without area or beside the page (unless something can show
    /// them; those stay and are reported), content entirely outside the page or entirely clipped away, text with font size 0,
    /// and images, forms and patterns that the page content never uses.
    /// </summary>
    HiddenContent = 1 << 10,

    /// <summary>Text that is probably invisible (white on an empty background, covered by a filled area); only a guess, hence not preselected.</summary>
    SuspiciousText = 1 << 11,

    /// <summary>
    /// Further document data that no page shows (docs/RECHERCHE-VERSTECKEN.md, B-2): origin of web pages (/SpiderInfo, /IDS, /URLS),
    /// article threads with their titles and beads (/Threads, /B), slide shows (/AlternatePresentations), document parts (/DPartRoot,
    /// /DPart), the portfolio view (/Collection) and further layer configurations (/Configs).
    /// </summary>
    OtherData = 1 << 12,

    /// <summary>
    /// Copyright, licence and other free texts in font programs and colour profiles (docs/RECHERCHE-VERSTECKEN.md, B-3): they can carry
    /// anything and cannot be checked without reading them. Overwritten with spaces of the same length (<see cref="ObjectMetadata"/>);
    /// not preselected, because many font licences require their notices to stay.
    /// </summary>
    Notices = 1 << 13,

    /// <summary>
    /// The parts of images that nobody sees (docs/RECHERCHE-VERSTECKEN.md, B-11): a frame (clip) or the edge of the page hides them, but they are
    /// in the file. Each such image is cut to what can be seen of it (<see cref="ContentObjectEditor.CutToVisible"/>); the page looks the same.
    /// Not preselected: JPEG images are written anew.
    /// </summary>
    HiddenImageParts = 1 << 14,

    /// <summary>
    /// Form fields whose appearance shows something other than their stored value (docs/RECHERCHE-VERSTECKEN.md, B-7): exporting,
    /// processing and a digital signature use the value nobody sees. Each such field is drawn anew from its value (<see cref="FieldMismatch"/>);
    /// not preselected, because the page then shows something else.
    /// </summary>
    FieldValueMismatch = 1 << 15,
}

/// <summary>A finding of <see cref="DocumentCleanup.Inspect"/>.</summary>
/// <param name="Item">What was found.</param>
/// <param name="Count">How many (the unit depends on the item, for example entries, pages or annotations).</param>
/// <param name="Detail">Short description, already localised to the current UI language; may be empty.</param>
/// <param name="Pages">Where it is: the pages (index in the list given to <see cref="DocumentCleanup.Inspect"/>), ascending; null for
/// findings of the whole document.</param>
public sealed record CleanupFinding(CleanupItems Item, int Count, string Detail, IReadOnlyList<int>? Pages = null);

/// <summary>Cleanup items that apply only to some pages (<see cref="PdfSaveOptions.ScopedCleanup"/>).</summary>
/// <param name="Items">The items; only those that belong to pages count (<see cref="DocumentCleanup.PageItems"/>).</param>
/// <param name="Pages">The pages (index in the written order).</param>
public sealed record CleanupScope(CleanupItems Items, IReadOnlySet<int> Pages);

/// <summary>Where a finding of <see cref="DocumentCleanup.Inspect"/> lies on a page (<see cref="DocumentCleanup.Places"/>), in page space.</summary>
/// <param name="Bounds">Bounds of all lines.</param>
/// <param name="Lines">The areas: one per line of text, or the whole object.</param>
/// <param name="Text">What hidden text says, one line per area (lines separated by line breaks) — it cannot be read on the page; empty for
/// anything else.</param>
/// <param name="Value">The stored value of a form field that shows something else; null for anything else.</param>
public sealed record CleanupPlace(PdfRectangle Bounds, IReadOnlyList<PdfRectangle> Lines, string Text, string? Value = null);

/// <summary>
/// A place of a finding that stays when cleaning up („nicht löschen“, the „x“ on its frame in „Dokument bereinigen“, KONZEPT.md, section 5.18):
/// what of that item lies in the area stays — hidden text whose character cell has its centre in it, an unseen annotation, an image or a form
/// field whose visible part has its centre in it.
/// </summary>
/// <param name="Item">The item (<see cref="CleanupItems.InvisibleText"/>, <see cref="CleanupItems.SuspiciousText"/>,
/// <see cref="CleanupItems.HiddenContent"/>, <see cref="CleanupItems.HiddenImageParts"/> or <see cref="CleanupItems.FieldValueMismatch"/>).</param>
/// <param name="Page">The page (index in the written order).</param>
/// <param name="Area">The place as <see cref="CleanupPlace.Bounds"/> gave it (page space).</param>
public sealed record CleanupKeep(CleanupItems Item, int Page, PdfRectangle Area)
{
    /// <summary>Whether a point (page space) lies in the place (a little tolerance at its edge).</summary>
    public bool Holds(double x, double y) =>
        x >= Area.Left - 0.5 && x <= Area.Right + 0.5 && y >= Area.Bottom - 0.5 && y <= Area.Top + 0.5;

    /// <summary>Whether the centre of an area (page space, <see cref="RectD"/>: Top is the lower y) lies in the place.</summary>
    public bool Holds(RectD area) => Holds((area.Left + area.Right) / 2, (area.Top + area.Bottom) / 2);

    /// <summary>Whether the centre of a rectangle lies in the place.</summary>
    public bool Holds(PdfRectangle area) => Holds((area.Left + area.Right) / 2, (area.Bottom + area.Top) / 2);
}

/// <summary>
/// Finds hidden data and leaves it out when saving. Removal happens while copying: keys are dropped from the
/// dictionaries, and objects that are then no longer referenced (attachments, XMP streams, scripts) are not written at
/// all. Earlier revisions (incremental updates) are dropped anyway, because PDF Knead always writes a complete new file.
/// </summary>
public static class DocumentCleanup
{
    private static readonly PdfName Metadata = PdfName.Get("Metadata"), PieceInfo = PdfName.Get("PieceInfo"), Thumb = PdfName.Get("Thumb"),
        EmbeddedFiles = PdfName.Get("EmbeddedFiles"), AF = PdfName.Get("AF"), JavaScript = PdfName.Get("JavaScript"), Js = PdfName.Get("JS"), AA = PdfName.Get("AA"),
        A = PdfName.Get("A"), OpenAction = PdfName.Get("OpenAction"), S = PdfName.Get("S"), Next = PdfName.Get("Next"),
        StructTreeRoot = PdfName.Get("StructTreeRoot"), MarkInfo = PdfName.Get("MarkInfo"), StructParents = PdfName.Get("StructParents"),
        StructParent = PdfName.Get("StructParent"), Outlines = PdfName.Get("Outlines"), Annots = PdfName.Get("Annots"), Names = PdfName.Get("Names");

    /// <summary>
    /// Actions that execute something or pass data to the outside (links to web pages and to pages stay). GoToR/GoToE open
    /// other files (on a share \\server\…, Windows discloses login credentials in the process).
    /// </summary>
    private static readonly HashSet<string> RiskyActions = ["JavaScript", "Launch", "SubmitForm", "ImportData", "Rendition", "Sound", "Movie", "RichMediaExecute",
        "GoToR", "GoToE"];

    private static readonly PdfName Xfa = PdfName.Get("XFA"), EmbeddedFile = PdfName.Get("EF"), Uri = PdfName.Get("URI");

    /// <summary>
    /// What counts as a comment (not: links, fields, pop-ups of fields). The writer rewrites the program's own elements
    /// anyway; a bare /PDFKneadElement key does not make a foreign comment an own element, otherwise a comment with an empty
    /// key would survive the sanitising. See docs/FEHLERGEDAECHTNIS.md, No. 37.
    /// </summary>
    private static bool IsComment(PdfDictionary annot) => annot.GetName(PdfName.Subtype)?.Value is not ("Link" or "Widget" or null);

    /// <summary>Checks whether an annotation is dropped by the sanitising (comment or file attachment).</summary>
    /// <param name="annot">The annotation dictionary.</param>
    /// <param name="items">The selected items.</param>
    /// <param name="kept">Embedded files that stay (their data streams): a file attachment naming one stays; null: none.</param>
    /// <param name="doc">The document, for resolving references (needed with <paramref name="kept"/>).</param>
    /// <returns>True if the annotation is dropped.</returns>
    public static bool DropsAnnotation(PdfDictionary annot, CleanupItems items, IReadOnlySet<PdfObjectId>? kept = null, PdfDocument? doc = null) =>
        (items.HasFlag(CleanupItems.Comments) && IsComment(annot)) ||
        (items.HasFlag(CleanupItems.Attachments) && annot.GetName(PdfName.Subtype)?.Value == "FileAttachment"
         && !(doc is not null && KeepsFile(annot.Get(PdfName.Get("FS")), kept, doc)));

    /// <summary>
    /// Whether a file specification names an embedded file that stays although „Anhänge“ is cleaned up: its data stream (/EF /F or /UF)
    /// among <paramref name="kept"/> (KONZEPT.md, section 5.24, decision (4) of 15.11.2026).
    /// </summary>
    public static bool KeepsFile(PdfObject? spec, IReadOnlySet<PdfObjectId>? kept, PdfDocument doc)
    {
        if (kept is not { Count: > 0 } || spec is null || Resolve(spec, doc) is not PdfDictionary dict) return false;
        if (dict.Get(EmbeddedFile) is not { } efValue || Resolve(efValue, doc) is not PdfDictionary ef) return false;
        return ef.GetRaw(PdfName.Get("F")) is PdfReference f && kept.Contains(f.Id) || ef.GetRaw(PdfName.Get("UF")) is PdfReference uf && kept.Contains(uf.Id);
    }

    /// <summary>
    /// The data streams of every embedded file reachable from the catalogue and the pages kept — in the name tree, /AF, file attachment
    /// annotations, actions, media — as „Anhänge“ would remove them (for keeping all but some, KONZEPT.md, section 5.24, decision (4)).
    /// </summary>
    public static HashSet<PdfObjectId> EmbeddedFileStreams(PdfDocument doc, IReadOnlyList<PageItem> pages)
    {
        var result = new HashSet<PdfObjectId>();
        var roots = new List<(PdfDocument, PdfObject)>();
        if (doc.Catalog is { } catalog) roots.Add((doc, catalog));
        foreach (var item in pages) roots.Add((item.Source, item.Page.Dictionary));
        var live = new HashSet<PdfDictionary>(pages.Select(i => i.Page.Dictionary), ReferenceEqualityComparer.Instance);
        ReachableObjects.Visit(roots, live, (source, obj) =>
        {
            if (!ReferenceEquals(source, doc) || obj is not PdfDictionary spec || !ReachableObjects.IsFileSpecification(spec)) return;
            if (spec.Get(EmbeddedFile) is not { } efValue || Resolve(efValue, doc) is not PdfDictionary ef) return;
            foreach (var key in (ReadOnlySpan<string>)["F", "UF"])
                if (ef.GetRaw(PdfName.Get(key)) is PdfReference r) result.Add(r.Id);
        });
        return result;
    }

    /// <summary>The entries of an /AF array that name kept files (a new array); null if none.</summary>
    private static PdfArray? KeptOf(PdfObject value, IReadOnlySet<PdfObjectId> kept, PdfDocument doc)
    {
        if (Resolve(value, doc) is not PdfArray list) return null;
        var result = new PdfArray { Resolver = list.Resolver };
        for (var i = 0; i < list.Count && i < 10_000; i++)
            if (KeepsFile(list[i], kept, doc)) result.Add(list[i]);
        return result.Count > 0 ? result : null;
    }

    /// <summary>The name tree /EmbeddedFiles with the entries that name kept files only (one flat node); null if none.</summary>
    private static PdfDictionary? KeptTree(PdfObject value, IReadOnlySet<PdfObjectId> kept, PdfDocument doc)
    {
        if (Resolve(value, doc) is not PdfDictionary root) return null;
        var pairs = new List<(PdfString Name, PdfObject Value)>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance) { root };
        var stack = new Stack<(PdfDictionary Node, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && seen.Count <= 100_000)
        {
            var (node, depth) = stack.Pop();
            if (node.GetArray(Names) is { } names)
                for (var i = 0; i + 1 < names.Count && pairs.Count < 10_000; i += 2)
                    if (names.Get(i) is PdfString name && KeepsFile(names[i + 1], kept, doc)) pairs.Add((name, names[i + 1]));
            if (depth >= 32 || node.GetArray(PdfName.Kids) is not { } kids) continue;
            for (var i = 0; i < kids.Count; i++)
                if (kids.Get(i).AsDictionary() is { } kid && seen.Add(kid)) stack.Push((kid, depth + 1));
        }
        if (pairs.Count == 0) return null;
        // a name tree keeps its keys sorted (ISO 32000-2, 7.9.6)
        var array = new PdfArray();
        foreach (var (name, entry) in pairs.OrderBy(p => p.Name.Bytes, ByteOrder.Instance))
        {
            array.Add(name);
            array.Add(entry);
        }
        var tree = new PdfDictionary();
        tree.Set(Names, array);
        return tree;
    }

    /// <summary>Byte strings in byte order (the order of the keys of a name tree).</summary>
    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static readonly ByteOrder Instance = new();

        public int Compare(byte[]? x, byte[]? y) => (x ?? []).AsSpan().SequenceCompareTo(y ?? []);
    }

    /// <summary>Checks whether a key is dropped from a dictionary by the sanitising; applies to every dictionary.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value (for actions, to decide whether they are risky).</param>
    /// <param name="items">The selected items.</param>
    /// <param name="doc">The document, for resolving references.</param>
    /// <returns>True if the key is dropped.</returns>
    public static bool DropsKey(PdfName key, PdfObject value, CleanupItems items, PdfDocument doc)
    {
        if (items.HasFlag(CleanupItems.Metadata) && key == Metadata) return true;
        // further metadata of objects (docs/RECHERCHE-VERSTECKEN.md, B-3): the date of the last change by another program, another image
        // to be used instead (a high-resolution one for printing) and the reference to the original of an image (OPI)
        if (items.HasFlag(CleanupItems.Metadata) && key.Value is "LastModified" or "Alternates" or "OPI") return true;
        if (items.HasFlag(CleanupItems.PrivateData) && (key == PieceInfo || IsEmbeddingInfo(key))) return true;
        if (items.HasFlag(CleanupItems.Thumbnails) && key == Thumb) return true;
        // Also /EF in file specifications outside /EmbeddedFiles (actions, media, GoToE)
        if (items.HasFlag(CleanupItems.Attachments) && (key == EmbeddedFiles || key == AF || key == EmbeddedFile)) return true;
        // the portfolio view of the catalogue presents the attachments: without them its /D would name a file that is gone (ISO 32000-2,
        // 12.3.5; docs/FEHLERGEDAECHTNIS.md, No. 1104)
        if (items.HasFlag(CleanupItems.Attachments) && key == CollectionKey) return true;
        if (items.HasFlag(CleanupItems.Scripts))
        {
            if (key == JavaScript || key == AA || key == Xfa) return true;   // XFA forms carry scripts
            // the script of a JavaScript or rendition action wherever such a dictionary sits — in a damaged file it can be a page as
            // well (the page tree pointing at the action, read as a page; docs/FEHLERGEDAECHTNIS.md, No. 246)
            if (key == Js || key == PdfName.Get("S") && value is PdfName { Value: "JavaScript" }) return true;
            if ((key == A || key == OpenAction) && IsRisky(Resolve(value, doc), doc)) return true;
            // page templates (/Names /Templates): pages outside the page tree that only a script can show — without scripts they are
            // data nobody sees, and neither search nor redaction reaches them (docs/RECHERCHE-VERSTECKEN.md, B-1)
            if (key == Templates) return true;
        }
        if (items.HasFlag(CleanupItems.OtherData))
        {
            if (OtherDataKeys.Contains(key.Value)) return true;
            // beads of article threads on a page (/B): only an array of beads, /B means other things elsewhere
            if (key.Value == "B" && Resolve(value, doc) is PdfArray beads && beads.Count > 0 &&
                beads.Get(0) is PdfDictionary bead && (bead.GetName(PdfName.Type)?.Value == "Bead" || bead.ContainsKey(PdfName.Get("T")) && bead.ContainsKey(PdfName.Get("V"))))
                return true;
        }
        if (items.HasFlag(CleanupItems.Structure) &&
            (key == StructTreeRoot || key == MarkInfo || key == StructParents || key == StructParent))
            return true;
        if (items.HasFlag(CleanupItems.Bookmarks) && key == Outlines) return true;
        return false;
    }

    private static readonly PdfName Templates = PdfName.Get("Templates");
    private static readonly PdfName CollectionKey = PdfName.Get("Collection");

    /// <summary>Keys of <see cref="CleanupItems.OtherData"/>.</summary>
    private static readonly HashSet<string> OtherDataKeys =
        new(["SpiderInfo", "IDS", "URLS", "Threads", "AlternatePresentations", "DPartRoot", "DPart", "Collection", "Configs"], StringComparer.Ordinal);

    /// <summary>What of <see cref="CleanupItems.OtherData"/> the document has (catalogue, /Names, layers), localised for the window.</summary>
    private static List<string> OtherData(PdfDocument doc)
    {
        var found = new List<string>();
        if (doc.Catalog is not { } catalog) return found;
        var names = catalog.GetDictionary(Names);
        if (catalog.ContainsKey(PdfName.Get("SpiderInfo")) || names?.ContainsKey(PdfName.Get("IDS")) == true || names?.ContainsKey(PdfName.Get("URLS")) == true)
            found.Add(Tr.T("Herkunft aus dem Web"));
        if (catalog.ContainsKey(PdfName.Get("Threads"))) found.Add(Tr.T("Artikel"));
        if (names?.ContainsKey(PdfName.Get("AlternatePresentations")) == true || catalog.ContainsKey(PdfName.Get("AlternatePresentations")))
            found.Add(Tr.T("Diashows"));
        if (catalog.ContainsKey(PdfName.Get("DPartRoot"))) found.Add(Tr.T("Dokumentteile"));
        if (catalog.ContainsKey(PdfName.Get("Collection"))) found.Add(Tr.T("Portfolio-Ansicht"));
        if (catalog.GetDictionary(PdfName.Get("OCProperties"))?.ContainsKey(PdfName.Get("Configs")) == true) found.Add(Tr.T("weitere Ebenen-Einstellungen"));
        return found;
    }

    /// <summary>
    /// Page templates (/Names /Templates): pages outside the page tree that a script can show (docs/RECHERCHE-VERSTECKEN.md, B-1); for the
    /// search of redacted text elsewhere. At most 1000, each once.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <returns>The template pages, with their own resources and boxes.</returns>
    public static List<PdfPage> TemplatePages(PdfDocument doc)
    {
        var result = new List<PdfPage>();
        if (doc.Catalog?.GetDictionary(Names)?.GetDictionary(Templates) is not { } root) return result;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance) { root };
        var stack = new Stack<(PdfDictionary Node, int Depth)>();
        stack.Push((root, 0));
        while (stack.TryPop(out var entry) && result.Count < 1000)
        {
            if (entry.Node.GetArray(Names) is { } pairs)
                for (var i = 1; i < pairs.Count && result.Count < 1000; i += 2)
                    if (pairs.Get(i) is PdfDictionary page && seen.Add(page))
                    {
                        var media = PdfRectangle.From(page.Get(PdfName.MediaBox)) ?? PdfPage.DefaultMediaBox;
                        result.Add(new PdfPage(result.Count, page, (pairs[i] as PdfReference)?.Id, page.GetDictionary(PdfName.Resources), media,
                            PdfRectangle.From(page.Get(PdfName.Get("CropBox"))), (int)(page.GetInteger(PdfName.Rotate) ?? 0)));
                    }
            if (entry.Depth < 32 && entry.Node.GetArray(PdfName.Kids) is { } kids)
                for (var i = 0; i < kids.Count && i < 10_000; i++)
                    if (kids.Get(i) is PdfDictionary kid && seen.Add(kid)) stack.Push((kid, entry.Depth + 1));
        }
        return result;
    }

    /// <summary>
    /// What a typesetting program writes into a form that embeds another PDF page (/PTEX.FileName with the path of the embedded file,
    /// /PTEX.InfoDict with its author, title and dates, /PTEX.PageNumber): private data of the producing program.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>True for such a key.</returns>
    public static bool IsEmbeddingInfo(PdfName key) => key.Value.StartsWith("PTEX.", StringComparison.Ordinal);

    /// <summary>Returns a dictionary, stream or array without the keys that are dropped, applied recursively to direct
    /// children (not through references).</summary>
    /// <param name="obj">The object.</param>
    /// <param name="items">The selected items.</param>
    /// <param name="doc">The document, for resolving references.</param>
    /// <param name="depth">Current recursion depth; deeper than 16 levels, objects are returned unchanged.</param>
    /// <param name="kept">With „Anhänge“: embedded files that stay (their data streams) — their file specifications keep /EF, the name tree
    /// and /AF keep their entries; null: all go.</param>
    /// <returns>A filtered copy, or the same object if nothing is dropped.</returns>
    public static PdfObject Filter(PdfObject obj, CleanupItems items, PdfDocument doc, int depth = 0, IReadOnlySet<PdfObjectId>? kept = null)
    {
        if (items == CleanupItems.None || depth > 16) return obj;
        switch (obj)
        {
            case PdfStream stream:
                var dict = (PdfDictionary)Filter(stream.Dictionary, items, doc, depth, kept);
                if (ReferenceEquals(dict, stream.Dictionary)) return stream;
                return stream.RawData is { } data ? new PdfStream(dict, data) : new PdfStream(dict, stream.DataOffset, stream.DataLength, stream.Id);
            case PdfDictionary d:
                PdfDictionary? copy = null;
                // annotations of other programs: who wrote them and when (/T is the author there; on fields it is the name and stays)
                var markup = items.HasFlag(CleanupItems.Metadata) && IsMarkup(d);
                foreach (var (key, value) in d.Entries)
                {
                    var drop = DropsKey(key, value, items, doc) || markup && key.Value is "T" or "CreationDate" or "M";
                    PdfObject? replaced = null;
                    // „Anhänge“ with files that stay: their file specification keeps /EF, /AF and the name tree keep their entries
                    if (drop && kept is { Count: > 0 } && items.HasFlag(CleanupItems.Attachments))
                    {
                        if (key == EmbeddedFile && KeepsFile(d, kept, doc)) drop = false;
                        else if (key == AF && KeptOf(value, kept, doc) is { } keptAf) (drop, replaced) = (false, keptAf);
                        else if (key == EmbeddedFiles && KeptTree(value, kept, doc) is { } keptTree) (drop, replaced) = (false, keptTree);
                        else if (key == CollectionKey) drop = false;   // files stay: so does their portfolio view (No. 1104)
                    }
                    var filtered = replaced ?? (drop || value is not (PdfDictionary or PdfArray) ? value : Filter(value, items, doc, depth + 1, kept));
                    if (!drop && ReferenceEquals(filtered, value) && copy is null) continue;
                    if (copy is null)
                    {
                        copy = new PdfDictionary(d.Count) { Resolver = d.Resolver };   // resolves references like the original
                        foreach (var (k, v) in d.Entries)
                        {
                            if (k == key) break;
                            copy.Set(k, v);
                        }
                    }
                    if (!drop) copy.Set(key, filtered);
                }
                return copy ?? d;
            case PdfArray a:
                PdfArray? array = null;
                for (var i = 0; i < a.Count; i++)
                {
                    var filtered = a[i] is PdfDictionary or PdfArray ? Filter(a[i], items, doc, depth + 1, kept) : a[i];
                    if (array is null && ReferenceEquals(filtered, a[i])) continue;
                    if (array is null)
                    {
                        array = new PdfArray { Resolver = a.Resolver };
                        for (var k = 0; k < i; k++) array.Add(a[k]);
                    }
                    array.Add(filtered);
                }
                return array ?? a;
            default:
                return obj;
        }
    }

    private static PdfObject Resolve(PdfObject obj, PdfDocument doc) => obj is PdfReference r ? doc.GetObject(r.Id.Number) : obj;

    /// <summary>An annotation that is not a field (its /T is the author) — also one without /Type, recognised by /Subtype and /Rect.</summary>
    private static bool IsMarkup(PdfDictionary d) =>
        d.GetName(PdfName.Subtype)?.Value is { } subtype && subtype is not ("Widget" or "Link" or "Popup") && d.ContainsKey(PdfName.Get("Rect")) &&
        (d.GetName(PdfName.Type)?.Value is null or "Annot") && !d.ContainsKey(PdfName.Get("FT"));

    /// <summary>http, https, mailto or tel, or no scheme ("www.example.com"), but not as a path (\\server, //server).</summary>
    private static bool IsWebLink(string target)
    {
        if (target.StartsWith(@"\\", StringComparison.Ordinal) || target.StartsWith("//", StringComparison.Ordinal)) return false;
        var colon = target.IndexOf(':');
        var scheme = colon > 0 && target[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '.' or '-') ? target[..colon] : null;
        return scheme is null || scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
               scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase) || scheme.Equals("tel", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Action (including follow-up actions in /Next) that executes something.</summary>
    private static bool IsRisky(PdfObject action, PdfDocument doc) =>
        IsRisky(action, doc, 0, new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance));

    /// <summary>Each action once: otherwise follow-up actions that point to the same one several times result in 32^8 calls.</summary>
    private static bool IsRisky(PdfObject action, PdfDocument doc, int depth, HashSet<PdfDictionary> seen)
    {
        if (depth > 8 || action.AsDictionary() is not { } dict || !seen.Add(dict)) return false;
        if (dict.GetName(S)?.Value is { } type && RiskyActions.Contains(type)) return true;
        // Links that lead neither to the web nor to an e-mail address (file:, smb:, javascript:, \\server …): treated like GoToR
        if (dict.GetName(S)?.Value == "URI" && dict.GetString(Uri)?.ToText().TrimStart() is { } target && !IsWebLink(target))
            return true;
        return dict.Get(Next) switch
        {
            PdfArray list => Enumerable.Range(0, Math.Min(list.Count, 32)).Any(i => IsRisky(list.Get(i), doc, depth + 1, seen)),
            PdfDictionary next => IsRisky(next, doc, depth + 1, seen),
            _ => false,
        };
    }

    /// <summary>
    /// The items that belong to pages — what lies in a page's content, annotations and fields — as opposed to those of the whole document
    /// (metadata, attachments, bookmarks …): only these can be limited to some pages (<see cref="CleanupScope"/>).
    /// </summary>
    public const CleanupItems PageItems = CleanupItems.HiddenLayers | CleanupItems.InvisibleText | CleanupItems.HiddenContent |
                                          CleanupItems.SuspiciousText | CleanupItems.HiddenImageParts | CleanupItems.FieldValueMismatch;

    // ------------------------------------------------------------ Inspection

    /// <summary>Finds what the sanitising would remove in these pages and the main document (for the selection in the window).</summary>
    /// <param name="doc">The main document (catalogue, /Info, outline, layers).</param>
    /// <param name="pages">The pages to inspect; each page dictionary is inspected once.</param>
    /// <returns>One finding per item that occurs.</returns>
    /// <remarks>Interprets the page contents for hidden and suspicious content, so the cost grows with the pages.</remarks>
    public static List<CleanupFinding> Inspect(PdfDocument doc, IReadOnlyList<PageItem> pages)
    {
        var found = new List<CleanupFinding>();
        var catalog = doc.Catalog;
        var info = doc.Trailer.GetDictionary(PdfName.Info);
        var infoKeys = info is null ? [] : info.Entries.Select(e => e.Key.Value).Where(k => k != "PDFKneadSignatures").ToList();
        var xmp = catalog?.GetStream(Metadata) is not null;
        // what „Titel behalten“ leaves (docs/FEHLERGEDAECHTNIS.md, No. 492): the title in /Info and an XMP packet with the title alone — the
        // file is clean; the title can be changed or cleared in „Eigenschaften“
        if (infoKeys.All(k => k == "Title") && (!xmp || TitleOnlyXmp()))
        {
            infoKeys.Clear();
            xmp = false;
        }
        bool TitleOnlyXmp()
        {
            try
            {
                return catalog?.GetStream(Metadata) is { DataLength: <= 1 << 20 } packet && XmpEditor.TitleOnly(doc.GetStreamData(packet));
            }
            catch (Exception e) when (e is PdfException or InvalidDataException)
            {
                return false;
            }
        }
        // metadata in objects (docs/RECHERCHE-VERSTECKEN.md, B-3): in the data of images and profiles, author and date of comments
        var inObjects = ObjectMetadata.Count(doc);
        var authored = 0;
        var annotationsSeen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var item in pages)
            if (item.Page.Dictionary.Get(Annots) is PdfArray list)
                for (var i = 0; i < list.Count && i < 10_000; i++)
                    if (list.Get(i) is PdfDictionary a && annotationsSeen.Add(a) && IsMarkup(a) && !ElementStore.IsOwn(doc, item.Page, a) &&
                        (a.ContainsKey(PdfName.Get("T")) || a.ContainsKey(PdfName.Get("CreationDate")) || a.ContainsKey(PdfName.Get("M"))))
                        authored++;
        // Files associated only through /AF (catalogue, page, image, structure element, marked content …) and the XMP of objects: found
        // wherever they hang, each counted once — before, only the name tree, attachment annotations and the catalogue's XMP were offered,
        // and „Vor dem Signieren“ said nothing about the rest (docs/FEHLERGEDAECHTNIS.md, No. 446)
        var counted = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var named = new List<PdfObject?>();
        var attachments = 0;
        if (catalog?.GetDictionary(Names) is { } nameTrees) attachments += CountNameTree(nameTrees.GetDictionary(EmbeddedFiles), named);
        foreach (var value in named)
            if (value?.AsDictionary() is { } spec) counted.Add(spec);
        foreach (var item in pages)
            if (item.Page.Dictionary.Get(Annots) is PdfArray list)
                for (var i = 0; i < list.Count && i < 10_000; i++)
                    if (list.Get(i) is PdfDictionary a && a.GetName(PdfName.Subtype)?.Value == "FileAttachment" && a.GetDictionary(PdfName.Get("FS")) is { } fs)
                        counted.Add(fs);
        int associated = 0, objectXmp = 0;
        var catalogXmp = catalog?.GetStream(Metadata);
        var roots = new List<(PdfDocument, PdfObject)>();
        if (catalog is not null) roots.Add((doc, catalog));
        foreach (var item in pages) roots.Add((item.Source, item.Page.Dictionary));
        ReachableObjects.Visit(roots, new HashSet<PdfDictionary>(pages.Select(p => p.Page.Dictionary), ReferenceEqualityComparer.Instance), (_, obj) =>
        {
            switch (obj)
            {
                case PdfStream meta when meta.Dictionary.IsType(Metadata) && !ReferenceEquals(meta, catalogXmp):
                    objectXmp++;
                    break;
                case PdfDictionary spec when spec.ContainsKey(EmbeddedFile) && counted.Add(spec):
                    associated++;
                    break;
            }
        });
        var objectParts = inObjects.Jpeg + inObjects.Jpx + inObjects.Icc + authored + objectXmp;
        if (infoKeys.Count > 0 || xmp || objectParts > 0)
        {
            var parts = new List<string>();
            foreach (var (key, label) in (ReadOnlySpan<(string, string)>)
                     [("Author", Tr.T("Autor")), ("Title", Tr.T("Titel")), ("Subject", Tr.T("Thema")), ("Keywords", Tr.T("Stichwörter")),
                      ("Creator", Tr.T("Erstellt mit")), ("Producer", Tr.T("Erzeugt von"))])
                if (info?.GetString(PdfName.Get(key))?.ToText() is { Length: > 0 } text) parts.Add($"{label}: {Shorten(text)}");
            if (xmp) parts.Add("XMP");
            if (inObjects.Jpeg > 0) parts.Add(Tr.T("EXIF und Ähnliches in {0} JPEG-Bildern", inObjects.Jpeg));
            if (inObjects.Jpx > 0) parts.Add(Tr.T("Angaben in {0} JPEG-2000-Bildern", inObjects.Jpx));
            if (inObjects.Icc > 0) parts.Add(Tr.T("Gerätedaten in {0} Farbprofilen", inObjects.Icc));
            if (authored > 0) parts.Add(Tr.T("Autor und Datum an {0} Kommentaren", authored));
            if (objectXmp > 0) parts.Add(Tr.T("XMP an {0} Objekten (Seiten, Bilder, Kommentare …)", objectXmp));
            found.Add(new CleanupFinding(CleanupItems.Metadata, infoKeys.Count + (xmp ? 1 : 0) + objectParts, string.Join(" · ", parts)));
        }
        if (inObjects.FontNotices + inObjects.IccNotices > 0)
        {
            var parts = new List<string>();
            if (inObjects.FontNotices > 0) parts.Add(Tr.T("in {0} Schriften", inObjects.FontNotices));
            if (inObjects.IccNotices > 0) parts.Add(Tr.T("in {0} Farbprofilen", inObjects.IccNotices));
            found.Add(new CleanupFinding(CleanupItems.Notices, inObjects.FontNotices + inObjects.IccNotices, string.Join(" · ", parts)));
        }

        int scripts = 0, thumbnails = 0, pieces = 0, comments = 0, templates = 0;
        // the preview in the document's XMP (an image of the first page; docs/FEHLERGEDAECHTNIS.md, No. 446)
        var xmpPreview = catalogXmp is not null && HasXmpPreview(doc, catalogXmp);
        if (xmpPreview) thumbnails++;
        if (catalog?.GetDictionary(Names) is { } names)
        {
            scripts += CountNameTree(names.GetDictionary(JavaScript));
            templates = CountNameTree(names.GetDictionary(Templates));
        }
        if (catalog is not null && (catalog.ContainsKey(AA) || (catalog.Get(OpenAction) is { } open && IsRisky(open, doc)))) scripts++;
        if (catalog?.ContainsKey(PieceInfo) == true) pieces++;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var item in pages)
        {
            var page = item.Page.Dictionary;
            if (!seen.Add(page)) continue;
            if (page.ContainsKey(Thumb)) thumbnails++;
            if (page.ContainsKey(PieceInfo)) pieces++;
            pieces += EmbeddedForms(item.Page.Resources).Count(f => f.Dictionary.Entries.Any(e => IsEmbeddingInfo(e.Key)));
            if (page.ContainsKey(AA)) scripts++;
            if (page.Get(Annots) is not PdfArray annots) continue;
            for (var i = 0; i < annots.Count; i++)
            {
                if (annots.Get(i) is not PdfDictionary annot) continue;
                if (annot.GetName(PdfName.Subtype)?.Value == "FileAttachment") attachments++;
                else if (IsComment(annot) && annot.GetName(PdfName.Subtype)?.Value != "Popup" && !ElementStore.IsOwn(doc, item.Page, annot)) comments++;
                if (annot.ContainsKey(AA) || (annot.Get(A) is { } action && IsRisky(action, doc))) scripts++;
            }
        }
        attachments += associated;
        if (attachments > 0)
            found.Add(new CleanupFinding(CleanupItems.Attachments, attachments, associated > 0 ? Tr.T("davon {0} zugeordnete Dateien (/AF)", associated) : ""));
        if (scripts + templates > 0)
            found.Add(new CleanupFinding(CleanupItems.Scripts, scripts + templates, templates switch
            {
                0 => "",
                1 => Tr.T("darunter 1 versteckte Seite (Vorlage, die nur ein Skript zeigt)"),
                _ => Tr.T("darunter {0} versteckte Seiten (Vorlagen, die nur ein Skript zeigt)", templates),
            }));
        if (OtherData(doc) is { Count: > 0 } other)
            found.Add(new CleanupFinding(CleanupItems.OtherData, other.Count, string.Join(" · ", other)));
        if (thumbnails > 0) found.Add(new CleanupFinding(CleanupItems.Thumbnails, thumbnails, xmpPreview ? Tr.T("Vorschaubild in den Metadaten") : ""));
        if (pieces > 0) found.Add(new CleanupFinding(CleanupItems.PrivateData, pieces, ""));
        if (comments > 0) found.Add(new CleanupFinding(CleanupItems.Comments, comments, ""));
        var bookmarks = PdfOutline.Read(doc).Count;
        if (bookmarks > 0) found.Add(new CleanupFinding(CleanupItems.Bookmarks, bookmarks, ""));
        if (catalog?.ContainsKey(StructTreeRoot) == true) found.Add(new CleanupFinding(CleanupItems.Structure, 1, ""));

        // Hidden layers: show the names (they often already reveal what the layer contains)
        var hiddenLayers = HiddenLayerNames(doc);
        if (hiddenLayers.Count > 0)
            found.Add(new CleanupFinding(CleanupItems.HiddenLayers, hiddenLayers.Count, string.Join(" · ", hiddenLayers.Take(4).Select(Shorten)) + (hiddenLayers.Count > 4 ? " …" : "")));
        var invisible = PagesWithInvisibleText(pages);
        if (invisible.Count > 0) found.Add(new CleanupFinding(CleanupItems.InvisibleText, invisible.Count, "", invisible));
        if (Unseen(pages) is { } unseen) found.Add(unseen);
        if (Suspicious(pages) is { } suspicious) found.Add(suspicious);
        if (PartlyHiddenImages(pages) is { } cut) found.Add(cut);
        if (MismatchedFields(pages) is { } fields) found.Add(fields);
        return found;
    }

    /// <summary>
    /// Where the finding of an item lies on one page, to show it there (the page buttons of „Dokument bereinigen“): hidden text with its text,
    /// annotations nobody sees, partly hidden images (the part that can be seen), fields with their stored value. Only what lies on the visible
    /// page has a place — content beside the page, content that is never drawn (unused images, forms and patterns) and the hidden drawings of
    /// „Was niemand sieht“ have none.
    /// </summary>
    /// <param name="item">The page.</param>
    /// <param name="what">The item: <see cref="CleanupItems.InvisibleText"/>, <see cref="CleanupItems.SuspiciousText"/>,
    /// <see cref="CleanupItems.HiddenContent"/>, <see cref="CleanupItems.HiddenImageParts"/> or <see cref="CleanupItems.FieldValueMismatch"/>;
    /// other items have no places.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>The places, in content order; empty if none can be shown.</returns>
    /// <remarks>Interprets the page content (hidden text is measured by drawing), so it belongs off the UI thread.</remarks>
    public static List<CleanupPlace> Places(PageItem item, CleanupItems what, CancellationToken cancel = default)
    {
        var places = new List<CleanupPlace>();
        switch (what)
        {
            case CleanupItems.InvisibleText or CleanupItems.SuspiciousText or CleanupItems.HiddenContent:
                foreach (var spot in HiddenText.Find(item.Source, item.Page, cancel))
                {
                    var match = what switch
                    {
                        CleanupItems.InvisibleText => spot.Kind == HiddenTextKind.Invisible,
                        CleanupItems.SuspiciousText => spot.Kind == HiddenTextKind.Suspicious,
                        _ => spot.Kind is HiddenTextKind.Outside or HiddenTextKind.Tiny,
                    };
                    if (match && !spot.OutsidePage) places.Add(new CleanupPlace(spot.Bounds, spot.Lines, spot.Text));
                }
                if (what == CleanupItems.HiddenContent)
                {
                    var crop = HiddenContent.VisibleBox(item.Page);
                    foreach (var annot in HiddenContent.Annotations(item.Source, item.Page, scriptsRemoved: false).Unseen)
                        if (Content.AnnotationAppearance.RectOf(annot) is { } rect && Overlaps(rect, crop)) places.Add(new CleanupPlace(rect, [rect], ""));
                }
                break;
            case CleanupItems.HiddenImageParts:
            {
                var area = VisibleArea(item.Page);
                var found = ContentObjectEditor.ObjectsOf(item, cancel);
                foreach (var index in ContentObjectEditor.PartlyHidden(found, area))
                {
                    var seen = found[index].Visible.Intersect(area);
                    var rect = new PdfRectangle(seen.Left, seen.Top, seen.Right, seen.Bottom);
                    places.Add(new CleanupPlace(rect, [rect], ""));
                }
                break;
            }
            case CleanupItems.FieldValueMismatch:
                foreach (var (widget, field) in FieldMismatch.Of(item))
                    places.Add(new CleanupPlace(widget.Rect, [widget.Rect], "", field.StoredValue));
                break;
        }
        return places;

        static bool Overlaps(PdfRectangle a, PdfRectangle b) => a.Left < b.Right && b.Left < a.Right && a.Bottom < b.Top && b.Bottom < a.Top;
    }

    /// <summary>Images of which a frame or the edge of the page hides a part (<see cref="CleanupItems.HiddenImageParts"/>). Count: the images.</summary>
    private static CleanupFinding? PartlyHiddenImages(IReadOnlyList<PageItem> pages)
    {
        var images = 0;
        var onPages = new List<int>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < pages.Count; index++)
        {
            var item = pages[index];
            if (!seen.Add(item.Page.Dictionary)) continue;
            try
            {
                var count = ContentObjectEditor.PartlyHidden(ContentObjectEditor.ObjectsOf(item), VisibleArea(item.Page)).Count;
                images += count;
                if (count > 0) onPages.Add(index);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // damaged page: nothing to count; saving writes it as it is
            }
        }
        if (images == 0) return null;
        return new CleanupFinding(CleanupItems.HiddenImageParts, images, onPages.Count == 1 ? Tr.T("auf 1 Seite") : Tr.T("auf {0} Seiten", onPages.Count), onPages);
    }

    /// <summary>Form fields that show something other than their value (<see cref="CleanupItems.FieldValueMismatch"/>). Count: the fields; detail: their names.</summary>
    private static CleanupFinding? MismatchedFields(IReadOnlyList<PageItem> pages)
    {
        var names = new List<string>();
        var fields = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var where = new List<int>();
        for (var index = 0; index < pages.Count; index++)
        {
            if (!seen.Add(pages[index].Page.Dictionary)) continue;
            var here = FieldMismatch.Of(pages[index]);
            if (here.Count > 0) where.Add(index);
            foreach (var (_, field) in here)
                if (fields.Add(field.Field) && field.Name.Length > 0) names.Add(field.Name);
        }
        if (fields.Count == 0) return null;
        return new CleanupFinding(CleanupItems.FieldValueMismatch, fields.Count,
            string.Join(" · ", names.Take(4).Select(Shorten)) + (names.Count > 4 ? " …" : ""), where);
    }

    /// <summary>The visible area of a page in page space (see <see cref="HiddenContent.VisibleBox"/>).</summary>
    internal static Graphics.RectD VisibleArea(PdfPage page)
    {
        var box = HiddenContent.VisibleBox(page);
        return Graphics.RectD.FromLtrb(box.Left, box.Bottom, box.Right, box.Top);
    }

    /// <summary>
    /// What nobody sees: annotations and fields (those that stay, with the reason), content entirely outside, clipped away or
    /// without size, unused images and forms. Count: annotations that are dropped plus affected pages; if only items stay, 0.
    /// </summary>
    private static CleanupFinding? Unseen(IReadOnlyList<PageItem> pages)
    {
        int annotations = 0, contentPages = 0, unusedPages = 0;
        var kept = new List<HiddenContent.Kept>();
        var where = new List<int>();
        // on several pages at once, summed up in page order (No. 704)
        var perPage = OnEachPage(pages, item =>
        {
            var scan = HiddenContent.Annotations(item.Source, item.Page, scriptsRemoved: false);
            var (content, unused) = (false, false);
            try
            {
                var (glyphs, items, unusedHere) = Redactor.CountUnseen(item.Source, item.Page);
                (content, unused) = (glyphs + items > 0, unusedHere);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // damaged page: nothing to count; saving writes it as it is
            }
            return new UnseenOnPage(scan, content, unused);
        });
        for (var index = 0; index < perPage.Length; index++)
        {
            if (perPage[index] is not { } here) continue;
            annotations += here.Scan.Unseen.Count;
            kept.AddRange(here.Scan.Kept);
            if (here.Content) contentPages++;
            if (here.Unused) unusedPages++;
            if (here.Scan.Unseen.Count + here.Scan.Kept.Count > 0 || here.Content || here.Unused) where.Add(index);
        }
        if (annotations + contentPages + unusedPages == 0 && kept.Count == 0) return null;
        var parts = new List<string>();
        if (annotations > 0) parts.Add(annotations == 1 ? Tr.T("1 ausgeblendetes Feld oder 1 Anmerkung") : Tr.T("{0} ausgeblendete Felder und Anmerkungen", annotations));
        if (contentPages > 0)
            parts.Add(contentPages == 1 ? Tr.T("Inhalte außerhalb der Seite, weg-geclippt oder ohne Größe auf 1 Seite")
                : Tr.T("Inhalte außerhalb der Seite, weg-geclippt oder ohne Größe auf {0} Seiten", contentPages));
        if (unusedPages > 0)
            parts.Add(unusedPages == 1 ? Tr.T("ungenutzte Bilder und Formen auf 1 Seite") : Tr.T("ungenutzte Bilder und Formen auf {0} Seiten", unusedPages));
        if (kept.Count > 0)
        {
            parts.Add(Tr.T("bleiben: {0}", string.Join(", ", kept.Take(3).Select(k => $"{Shorten(k.Name)} ({k.Reason})")) + (kept.Count > 3 ? " …" : "")));
            if (kept.Any(k => k.ByScript)) parts.Add(Tr.T("mit „JavaScript und Aktionen“ gehen die von Skripten abhängigen mit"));
        }
        return new CleanupFinding(CleanupItems.HiddenContent, annotations + contentPages + unusedPages, string.Join(" · ", parts), where);
    }

    /// <summary>
    /// Text that is painted but not to be seen (white on white, covered, transparent …; measured by drawing, <see cref="TextVisibility"/>):
    /// count of pages; the detail gives the number of characters and an excerpt.
    /// </summary>
    private static CleanupFinding? Suspicious(IReadOnlyList<PageItem> pages)
    {
        var glyphs = 0;
        var onPages = new List<int>();
        var sample = "";
        // each page measured by drawing — on several pages at once (No. 704), summed up in page order as before
        var perPage = OnEachPage(pages, item =>
        {
            try
            {
                var found = HiddenText.Find(item.Source, item.Page).Where(s => s.Kind == HiddenTextKind.Suspicious).ToList();
                return found.Count == 0 ? ((int Glyphs, string Sample)?)null : (found.Sum(s => s.Glyphs), found[0].Text.Replace('\n', ' '));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return null;
            }
        });
        for (var index = 0; index < perPage.Length; index++)
        {
            if (perPage[index] is not { } here) continue;
            glyphs += here.Glyphs;
            onPages.Add(index);
            if (sample.Length == 0) sample = here.Sample;
        }
        if (glyphs == 0) return null;
        var detail = glyphs == 1 ? Tr.T("1 Zeichen") : Tr.T("{0} Zeichen", glyphs);
        return new CleanupFinding(CleanupItems.SuspiciousText, onPages.Count, sample.Length > 0 ? $"{detail}: „{Shorten(sample)}“" : detail, onPages);
    }

    /// <summary>What a page has of <see cref="Unseen"/>: its annotations nobody sees, content beside or under it, unused images and forms.</summary>
    private sealed record UnseenOnPage(HiddenContent.AnnotationScan Scan, bool Content, bool Unused);

    /// <summary>Pages looked at side by side at most: each measurement by drawing may hold up to half a gigabyte of decoded images.</summary>
    private static int PageWorkers => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>
    /// <paramref name="work"/> for each page dictionary once (the first page that has it), on several pages at once (docs/FEHLERGEDAECHTNIS.md,
    /// No. 704: the analysis of a scan of 82 pages took 17 s one page after another); the results by page index, default for repeated pages.
    /// Reading a document from several threads is allowed (<see cref="PdfDocument"/>).
    /// </summary>
    private static T?[] OnEachPage<T>(IReadOnlyList<PageItem> pages, Func<PageItem, T?> work)
    {
        var results = new T?[pages.Count];
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var first = new List<int>();
        for (var index = 0; index < pages.Count; index++)
            if (seen.Add(pages[index].Page.Dictionary)) first.Add(index);
        try
        {
            Parallel.ForEach(first, new ParallelOptions { MaxDegreeOfParallelism = PageWorkers }, index => results[index] = work(pages[index]));
        }
        catch (AggregateException e) when (e.Flatten().InnerExceptions is [var inner, ..])
        {
            // the error of the document as it comes one page after another (a PdfLimitException the caller handles), not wrapped by the
            // threads (docs/FEHLERGEDAECHTNIS.md, No. 935)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(inner);
        }
        return results;
    }

    /// <summary>
    /// The forms and images of a resource dictionary and of the forms in it (up to four levels, at most 10,000): where embedded pages
    /// and pictures keep data of their own (/PTEX.InfoDict, /Metadata).
    /// </summary>
    /// <param name="resources">The resources of a page.</param>
    /// <returns>The XObjects, each once.</returns>
    public static IEnumerable<PdfStream> EmbeddedForms(PdfDictionary? resources)
    {
        var seen = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(PdfDictionary Resources, int Depth)>();
        if (resources is not null) pending.Push((resources, 0));
        while (pending.Count > 0 && seen.Count < 10_000)
        {
            var (current, depth) = pending.Pop();
            if (current.GetDictionary(PdfName.Get("XObject")) is not { } xobjects) continue;
            foreach (var (key, _) in xobjects.Entries)
            {
                if (xobjects.Get(key) is not PdfStream x || !seen.Add(x)) continue;
                yield return x;
                if (depth < 4 && x.Dictionary.GetDictionary(PdfName.Resources) is { } inner) pending.Push((inner, depth + 1));
            }
        }
    }

    /// <summary>Names of the layers that are hidden when the document is opened and not printed either (a layer shown only on paper is not hidden).</summary>
    internal static List<string> HiddenLayerNames(PdfDocument doc)
    {
        var names = new List<string>();
        if (doc.Catalog?.GetDictionary(PdfName.Get("OCProperties"))?.GetArray(PdfName.Get("OCGs")) is not { } groups) return names;
        var visibility = Content.OptionalContent.NeverShown(doc);
        for (var i = 0; i < groups.Count && i < 10_000; i++)
            if (groups.Get(i) is PdfDictionary group && visibility.IsHidden(group, null))
                names.Add(group.GetString(PdfName.Name)?.ToText() is { Length: > 0 } name ? name : "?");
        // a membership rule can hide content with every layer on ([/Not A], /P /AllOff): named once (docs/FEHLERGEDAECHTNIS.md, No. 561, 590)
        if (HidesByRule(doc, visibility)) names.Add(Tr.T("durch eine Sichtbarkeitsregel verborgene Inhalte"));
        return names;
    }

    /// <summary>
    /// Whether a membership dictionary hides content by its rule (/VE, or /P with its layers) although none of its layers is hidden itself —
    /// wherever it is used: in the /Properties of a page or of the forms it draws, as /OC of an image or form, as /OC of an annotation (at most
    /// 10 000 pages, the forms as <see cref="EmbeddedForms"/>). Before only the /Properties of the page were looked at, so an image with
    /// /OC &lt;&lt;/VE [/Not A]&gt;&gt; stayed in the file when cleaning (docs/FEHLERGEDAECHTNIS.md, No. 590).
    /// </summary>
    private static bool HidesByRule(PdfDocument doc, Content.OptionalContent visibility)
    {
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        bool Hides(PdfObject? candidate)
        {
            if (candidate is not PdfDictionary ocmd || ocmd.GetName(PdfName.Type)?.Value != "OCMD" || !seen.Add(ocmd)) return false;
            if (!visibility.IsHidden(ocmd, null)) return false;
            // hidden because a layer of it is hidden: that layer is named already
            if (ocmd.Get(PdfName.Get("OCGs")) is PdfDictionary single) return !visibility.IsHidden(single, null);
            if (ocmd.Get(PdfName.Get("OCGs")) is PdfArray groups)
                for (var i = 0; i < groups.Count && i < 1_000; i++)
                    if (groups.Get(i) is PdfDictionary group && visibility.IsHidden(group, null)) return false;
            return true;
        }
        bool InProperties(PdfDictionary? resources)
        {
            if (resources?.GetDictionary(PdfName.Get("Properties")) is not { } properties) return false;
            foreach (var (key, _) in properties.Entries)
                if (Hides(properties.Get(key))) return true;
            return false;
        }
        for (var p = 0; p < doc.PageCount && p < 10_000; p++)
        {
            var page = doc.Pages[p];
            if (InProperties(page.Resources)) return true;
            foreach (var x in EmbeddedForms(page.Resources))
                if (Hides(x.Dictionary.Get(PdfName.Get("OC"))) || InProperties(x.Dictionary.GetDictionary(PdfName.Resources))) return true;
            if (page.Dictionary.Get(PdfName.Get("Annots")) is PdfArray annots)
                for (var i = 0; i < annots.Count && i < 10_000; i++)
                    if (annots.Get(i) is PdfDictionary annot && Hides(annot.Get(PdfName.Get("OC")))) return true;
        }
        return false;
    }

    /// <summary>
    /// The layers named in visibility expressions (/VE) of membership dictionaries the document uses (as <see cref="HidesByRule"/> finds
    /// them): they stay in the file when hidden layers are dropped — without them the expression would turn invalid and its content would
    /// show (docs/FEHLERGEDAECHTNIS.md, No. 590).
    /// </summary>
    internal static HashSet<PdfDictionary> LayersInExpressions(PdfDocument doc)
    {
        var layers = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var budget = 100_000;
        void Walk(PdfObject? expression, int depth)
        {
            if (depth > 32 || --budget < 0) return;
            if (expression is PdfDictionary group) layers.Add(group);
            else if (expression is PdfArray array)
                for (var i = 1; i < array.Count; i++) Walk(array.Get(i), depth + 1);
        }
        void Take(PdfObject? candidate)
        {
            if (candidate is PdfDictionary ocmd && ocmd.GetName(PdfName.Type)?.Value == "OCMD" && seen.Add(ocmd)) Walk(ocmd.Get(PdfName.Get("VE")), 0);
        }
        void InProperties(PdfDictionary? resources)
        {
            if (resources?.GetDictionary(PdfName.Get("Properties")) is not { } properties) return;
            foreach (var (key, _) in properties.Entries) Take(properties.Get(key));
        }
        for (var p = 0; p < doc.PageCount && p < 10_000; p++)
        {
            var page = doc.Pages[p];
            InProperties(page.Resources);
            foreach (var x in EmbeddedForms(page.Resources))
            {
                Take(x.Dictionary.Get(PdfName.Get("OC")));
                InProperties(x.Dictionary.GetDictionary(PdfName.Resources));
            }
            if (page.Dictionary.Get(PdfName.Get("Annots")) is PdfArray annots)
                for (var i = 0; i < annots.Count && i < 10_000; i++)
                    if (annots.Get(i) is PdfDictionary annot) Take(annot.Get(PdfName.Get("OC")));
        }
        return layers;
    }

    /// <summary>
    /// Pages with invisible text (text rendering mode 3), usually the OCR layer of a scan: in the page content and in the forms it
    /// draws — OCR programs often put the text layer into a form of its own over the page image, which the page content only calls.
    /// </summary>
    /// <param name="pages">The pages; each page dictionary is looked at once.</param>
    /// <returns>Their indices, ascending.</returns>
    /// <remarks>Interprets the content as the display does (<see cref="ShowsInvisibleText"/>).</remarks>
    public static List<int> PagesWithInvisibleText(IReadOnlyList<PageItem> pages)
    {
        // on several pages at once (No. 704); the appearance streams of the annotations count too: invisible text there is removed as well
        var perPage = OnEachPage(pages, item => ShowsInvisibleText(item.Source, item.Page) || InvisibleTextAppearances(item.Source, item.Page).Count > 0);
        var result = new List<int>();
        for (var index = 0; index < perPage.Length; index++)
            if (perPage[index]) result.Add(index);
        return result;
    }

    /// <summary>
    /// Appearance streams of the page's annotations (/AP, all of /N, /R, /D and their states) that show invisible text, by object
    /// number — appearance streams are always indirect objects. Each stream once, at most 10 000 annotations.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <returns>Object number → appearance stream.</returns>
    internal static Dictionary<int, PdfStream> InvisibleTextAppearances(PdfDocument doc, PdfPage page)
    {
        var result = new Dictionary<int, PdfStream>();
        if (page.Dictionary.Get(PdfName.Get("Annots")) is not PdfArray annots) return result;
        var seen = new HashSet<int>();
        void Check(PdfObject? raw)
        {
            if (raw is not PdfReference reference || !seen.Add(reference.Id.Number) || doc.Resolve(reference.Id) is not PdfStream stream) return;
            if (ShowsInvisibleText(doc, AsPage(page, stream))) result[reference.Id.Number] = stream;
        }
        for (var i = 0; i < annots.Count && i < 10_000; i++)
        {
            if (annots.Get(i) is not PdfDictionary annot || annot.GetDictionary(PdfName.Get("AP")) is not { } ap) continue;
            foreach (var (key, _) in ap.Entries)
                if (ap.Get(key) is PdfDictionary states)
                    foreach (var (state, _) in states.Entries) Check(states.GetRaw(state));
                else
                    Check(ap.GetRaw(key));
        }
        return result;
    }

    /// <summary>
    /// An appearance stream as the content of a page of its own: its resources, its box (as the writer cleans it, so that what is found
    /// here is what <see cref="Redactor.RemoveHidden"/> removes there).
    /// </summary>
    internal static PdfPage AsPage(PdfPage page, PdfStream stream)
    {
        var box = PdfRectangle.From(stream.Dictionary.Get(PdfName.Get("BBox"))) ?? page.MediaBox;
        var dict = new PdfDictionary { Resolver = stream.Dictionary.Resolver };
        dict.Set(PdfName.Contents, stream);
        return new PdfPage(page.Index, dict, null, stream.Dictionary.Get(PdfName.Resources) as PdfDictionary, box, null, 0);
    }

    /// <summary>
    /// Whether the content shows a character in text rendering mode 3, as the display interprets it: the mode saved by q and restored by Q,
    /// inherited by forms, its operand clamped to 0…7 and truncated (3.5 is 3) — the same characters <see cref="Redactor.RemoveHidden"/> removes.
    /// Interpreted in full, with no reading of the bytes of its own (about a millisecond per page).
    /// </summary>
    /// <remarks>A content that cannot be read shows nothing, here as on the screen.</remarks>
    private static bool ShowsInvisibleText(PdfDocument doc, PdfPage page)
    {
        var finder = new InvisibleTextFinder();
        try
        {
            Content.PageInterpreter.Observe(doc, page, finder);
        }
        catch (PdfException)
        {
        }
        return finder.Found;
    }

    /// <summary>Notes whether a character in text rendering mode 3 is shown (<see cref="ShowsInvisibleText"/>).</summary>
    private sealed class InvisibleTextFinder : Content.IContentObserver
    {
        public bool Found;

        public void Glyph(int op, int element, int start, int length, RectD cell, RectD ink, Matrix cellToPage, double advance, double fontSize,
            double horizontalScale, bool vertical, Matrix textToPage, string? text, bool hidden, bool invisible) => Found |= invisible;

        public void Path(int op, RectD bounds, Matrix ctm, bool hidden)
        {
        }

        public void Image(int op, Matrix imageToPage, PdfStream? stream, Content.InlineImage? inline, PdfDictionary? resources, bool hidden)
        {
        }

        public void Shading(int op, RectD bounds, Matrix ctm, bool hidden)
        {
        }

        public void BeginForm(int op, PdfStream form, Matrix formToPage, bool hidden)
        {
        }

        public void BeginSoftMask(int op, PdfStream group, Matrix groupToPage)
        {
        }

        public void EndForm()
        {
        }
    }

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..39] + "…";

    /// <summary>
    /// Entries of a name tree (/Names, /Kids): each node once, with a total limit. A depth limit alone is not enough: a node
    /// that points to itself in /Kids results in exponentially many calls.
    /// </summary>
    /// <summary>
    /// The attachments of the whole document: entries of the name tree and files associated with the catalogue (/AF, such as the source of
    /// the document), each counted once — what a file of some pages leaves behind unless the user wants it (docs/FEHLERGEDAECHTNIS.md,
    /// No. 447).
    /// </summary>
    public static int DocumentAttachments(PdfDocument doc)
    {
        var catalog = doc.Catalog;
        var named = new List<PdfObject?>();
        var count = catalog?.GetDictionary(Names) is { } names ? CountNameTree(names.GetDictionary(EmbeddedFiles), named) : 0;
        var counted = new HashSet<PdfDictionary>(named.Select(v => v?.AsDictionary()).OfType<PdfDictionary>(), ReferenceEqualityComparer.Instance);
        if (catalog?.GetArray(AF) is { } af)
            for (var i = 0; i < af.Count && i < 10_000; i++)
                if (af.Get(i) is PdfDictionary spec && counted.Add(spec)) count++;
        return count;
    }

    /// <summary>Whether the document's XMP holds a preview image (xmp:Thumbnails or an image of the image namespace).</summary>
    private static bool HasXmpPreview(PdfDocument doc, PdfStream xmp)
    {
        if (xmp.DataLength > 16 << 20) return false;
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(doc.GetStreamData(xmp));
            return System.Text.RegularExpressions.Regex.IsMatch(text, @"<\w+:Thumbnails\b|<xmpGImg:image\b");
        }
        catch (PdfException)
        {
            return false;
        }
    }

    /// <param name="values">Collects the values of the entries (at most what is counted); null: not collected.</param>
    private static int CountNameTree(PdfDictionary? root, List<PdfObject?>? values = null)
    {
        if (root is null) return 0;
        var count = 0;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance) { root };
        var stack = new Stack<(PdfDictionary Node, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && seen.Count <= 100_000)
        {
            var (node, depth) = stack.Pop();
            if (node.GetArray(Names) is { } names)
            {
                count += names.Count / 2;
                if (values is not null)
                    for (var i = 1; i < names.Count && values.Count < 100_000; i += 2) values.Add(names.Get(i));
            }
            if (depth >= 32 || node.GetArray(PdfName.Kids) is not { } kids) continue;
            for (var i = 0; i < kids.Count; i++)
                if (kids.Get(i).AsDictionary() is { } kid && seen.Add(kid)) stack.Push((kid, depth + 1));
        }
        return count;
    }
}
