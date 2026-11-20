using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Determines which images, forms and patterns a content stream actually invokes — for redaction: replaced originals may only
/// remain in the file if something still draws them (otherwise any image extraction tool could read them, for example through
/// resources that pages share). Forms, tiling patterns, Type 3 glyphs and soft-mask groups without their own /Resources use those of the
/// page they are drawn on, wherever they are invoked (ISO 32000-2, 7.8.3, sentence 18; docs/FEHLERGEDAECHTNIS.md, No. 791), and are
/// included; depth and work are limited.
/// </summary>
internal static class ResourceUsage
{
    private static readonly PdfName XObject = PdfName.Get("XObject");
    private static readonly PdfName Pattern = PdfName.Get("Pattern");
    private const int MaxDepth = 16;
    private const long MaxOperations = 20_000_000;

    /// <summary>
    /// Names (XObject, pattern) that <paramref name="body"/> invokes in <paramref name="resources"/>, including what draws with them for want
    /// of its own. <c>Patterns</c> are the patterns something is painted with; <c>Selected</c> also those only chosen as a colour ("/P1 scn"
    /// whose path was dropped) — their name has to stay valid, or the content refers to a resource that is not there. A form, tiling pattern,
    /// Type 3 glyph or soft-mask group without resources of its own draws with those of the page (<paramref name="page"/>; ISO 32000-2, 7.8.3,
    /// sentence 18) — also inside a form that has its own: such content is searched through for them (docs/FEHLERGEDAECHTNIS.md, No. 791).
    /// </summary>
    /// <param name="page">The resources of the page; null: <paramref name="resources"/> are the page's.</param>
    public static (HashSet<PdfName> XObjects, HashSet<PdfName> Patterns, HashSet<PdfName> Selected) Names(PdfDocument doc, byte[] body, PdfDictionary resources,
        PdfDictionary? page = null)
    {
        var xobjects = new HashSet<PdfName>();
        var patterns = new HashSet<PdfName>();
        var selected = new HashSet<PdfName>();
        var visited = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        long budget = MaxOperations;
        CollectNames(doc, body, body.Length, resources, resources, page ?? resources, xobjects, patterns, selected, visited, 0, ref budget);
        return (xobjects, patterns, selected);
    }

    /// <param name="current">The resources this content names its resources in.</param>
    /// <param name="target">The resources whose names are collected: names count only in content that names its resources in them.</param>
    /// <param name="page">The resources of the page: those of content without its own.</param>
    private static void CollectNames(PdfDocument doc, byte[] body, int length, PdfDictionary current, PdfDictionary target, PdfDictionary page,
        HashSet<PdfName> xobjects, HashSet<PdfName> patterns, HashSet<PdfName> selected, HashSet<PdfStream> visited, int depth, ref long budget)
    {
        var counting = ReferenceEquals(current, target);
        var parser = new ContentParser(body, length);
        var paint = new PatternState();
        var inner = new List<(PdfStream Stream, PdfDictionary Resources)>();   // searched afterwards, each with the resources it draws with
        void Follow(PdfObject? value, PdfDictionary? own)
        {
            if (depth < MaxDepth && value is PdfStream stream && visited.Add(stream)) inner.Add((stream, own ?? page));
        }
        while (parser.Next() is { } op && --budget > 0)
        {
            var operands = parser.Operands;
            paint.Apply(op, operands);
            // A pattern only counts as used when something is painted with it ("/P1 scn" stays even if the path was dropped); a tiling pattern
            // has content of its own
            foreach (var painted in (ReadOnlySpan<PdfName?>)[paint.FillPattern(op), paint.StrokePattern(op)])
            {
                if (painted is null) continue;
                if (counting) patterns.Add(painted);
                if (current.GetDictionary(Pattern)?.Get(painted) is PdfStream tiling) Follow(tiling, tiling.Dictionary.GetDictionary(PdfName.Resources));
            }
            if (counting && op is "scn" or "SCN" or "sc" or "SC" && operands.Count >= 1 && operands[^1].Name is { } chosen) selected.Add(chosen);
            if (op == "Do" && operands.Count >= 1 && operands[0].Name is { } name)
            {
                if (counting) xobjects.Add(name);
                if (current.GetDictionary(XObject)?.Get(name) is PdfStream { } form && IsForm(form)) Follow(form, form.Dictionary.GetDictionary(PdfName.Resources));
            }
            // Likewise the glyphs of a Type 3 font and the group of a soft mask (otherwise images and patterns that only they invoke would be
            // dropped)
            else if (op == "Tf" && operands.Count >= 2 && operands[0].Name is { } fontName &&
                     current.GetDictionary(FontName)?.Get(fontName) is PdfDictionary font && font.GetName(PdfName.Subtype)?.Value == "Type3" &&
                     font.GetDictionary(CharProcs) is { } procs)
            {
                foreach (var (key, _) in procs.Entries) Follow(procs.Get(key), font.GetDictionary(PdfName.Resources));
            }
            else if (op == "gs" && operands.Count >= 1 && operands[0].Name is { } stateName &&
                     current.GetDictionary(ExtGState)?.GetDictionary(stateName)?.GetDictionary(SMask)?.Get(Group) is PdfStream group)
            {
                Follow(group, group.Dictionary.GetDictionary(PdfName.Resources));
            }
        }

        foreach (var (stream, resources) in inner)
        {
            using var content = Data(doc, stream);
            CollectNames(doc, content.Array, content.Length, resources, target, page, xobjects, patterns, selected, visited, depth + 1, ref budget);
        }
    }

    /// <summary>
    /// The character codes each font shows in <paramref name="body"/> (single bytes of the shown strings), keyed by the font dictionary —
    /// the same font object is the same key in the resources of the page, of a form and of a copy made while rewriting. Followed are
    /// forms (also those with resources of their own; text without its own Tf uses the caller's font), the glyphs of Type 3 fonts and
    /// the groups of soft masks. Null if that cannot be told for certain (a font set by a graphics state, text shown without any font,
    /// a form shown with different fonts, the work limit).
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="body">The content.</param>
    /// <param name="resources">Its resources.</param>
    /// <returns>Font dictionary → bytes shown with it; a font that shows nothing is missing.</returns>
    /// <param name="page">The resources of the page (content without its own draws with them, 7.8.3, sentence 18; No. 791); null:
    /// <paramref name="resources"/> are the page's.</param>
    public static Dictionary<PdfDictionary, HashSet<byte>>? FontCodes(PdfDocument doc, byte[] body, PdfDictionary resources, PdfDictionary? page = null)
    {
        var codes = new Dictionary<PdfDictionary, HashSet<byte>>(ReferenceEqualityComparer.Instance);
        var scanned = new Dictionary<PdfStream, PdfDictionary?>(ReferenceEqualityComparer.Instance);
        long[] budget = [MaxOperations];
        return CollectCodes(doc, body, body.Length, resources, page ?? resources, codes, scanned, null, 0, budget) ? codes : null;
    }

    private static bool CollectCodes(PdfDocument doc, byte[] body, int length, PdfDictionary resources, PdfDictionary page,
        Dictionary<PdfDictionary, HashSet<byte>> codes, Dictionary<PdfStream, PdfDictionary?> scanned, PdfDictionary? font, int depth, long[] budget)
    {
        var parser = new ContentParser(body, length);
        var saved = new Stack<PdfDictionary?>();
        while (parser.Next() is { } op)
        {
            if (--budget[0] < 0) return false;
            var operands = parser.Operands;
            switch (op)
            {
                // every text operator read as the display reads it (docs/FEHLERGEDAECHTNIS.md, No. 379 for FontUsage, No. 413 here): q up
                // to 512 levels, Tf from two operands, Tj and ' with their first operand
                case "q":
                    if (saved.Count < 512) saved.Push(font);
                    break;
                case "Q" when saved.Count > 0:
                    font = saved.Pop();
                    break;
                case "Tf" when operands.Count >= 2:
                    font = operands[0].Name is { } name ? resources.GetDictionary(FontName)?.Get(name) as PdfDictionary : null;
                    break;
                case "gs" when operands.Count >= 1 && operands[0].Name is { } state:
                    var gs = resources.GetDictionary(ExtGState)?.GetDictionary(state);
                    // a font set by the state, read as the display reads it: only with a size does it change the font (No. 427); from then on
                    // its codes count as with Tf (before, it could not be told; docs/FEHLERGEDAECHTNIS.md, No. 700)
                    if (gs?.Get(FontName) is PdfArray { Count: >= 2 } gsFont)
                    {
                        if (gsFont.GetNumber(1) is not null) font = gsFont.Get(0) as PdfDictionary;
                    }
                    else if (gs?.ContainsKey(FontName) == true) return false;   // not as the norm has it: unknown
                    if (gs?.GetDictionary(SMask)?.Get(Group) is PdfStream group && !Inside(group, null)) return false;
                    break;
                case "Tj" or "'" when operands.Count >= 1:
                    if (!Shown(operands[0].Object)) return false;
                    break;
                case "\"" when operands.Count >= 3:
                    if (!Shown(operands[2].Object)) return false;
                    break;
                case "TJ" when operands.Count >= 1 && operands[0].Object is PdfArray parts:
                    for (var i = 0; i < parts.Count; i++)
                        if (!Shown(parts.Get(i))) return false;
                    break;
                case "Do" when operands.Count >= 1 && operands[0].Name is { } x:
                    if (resources.GetDictionary(XObject)?.Get(x) is PdfStream form && IsForm(form) && !Inside(form, font)) return false;
                    break;
            }
        }
        return true;

        // a string shown with the current font; without one, the font is unknown
        bool Shown(PdfObject? value)
        {
            if (value is not PdfString text || text.Bytes.Length == 0) return true;
            if (font is null) return false;
            if (!codes.TryGetValue(font, out var set)) codes[font] = set = [];
            foreach (var b in text.Bytes) set.Add(b);
            // the glyphs of a Type 3 font are content themselves (with the font's resources, or these)
            if (font.GetDictionary(CharProcs) is { } procs)
                foreach (var (key, _) in procs.Entries)
                    if (procs.Get(key) is PdfStream glyph && !Inside(glyph, null, font.GetDictionary(PdfName.Resources))) return false;
            return true;
        }

        // content of a form, glyph or mask group: once; shown again with another current font, its text could count for either; without
        // resources of its own it draws with the page's (No. 791)
        bool Inside(PdfStream stream, PdfDictionary? current, PdfDictionary? own = null)
        {
            if (scanned.TryGetValue(stream, out var first)) return ReferenceEquals(first, current) || !ShowsWithoutTf(stream);
            if (depth >= MaxDepth) return false;
            scanned[stream] = current;
            using var content = Data(doc, stream);
            var inner = own ?? stream.Dictionary.GetDictionary(PdfName.Resources) ?? page;
            return CollectCodes(doc, content.Array, content.Length, inner, page, codes, scanned, current, depth + 1, budget);
        }

        // conservative: any text operator counts (whether a Tf comes first is not tracked here)
        bool ShowsWithoutTf(PdfStream stream)
        {
            using var content = Data(doc, stream);
            var p = new ContentParser(content.Array, content.Length);
            while (p.Next() is { } o)
            {
                if (--budget[0] < 0) return true;
                if (o == "Tf") return false;
                if (o is "Tj" or "TJ" or "'" or "\"") return true;
            }
            return false;
        }
    }

    private static readonly PdfName FontName = PdfName.Get("Font"), CharProcs = PdfName.Get("CharProcs"), ExtGState = PdfName.Get("ExtGState"),
        SMask = PdfName.Get("SMask"), Group = PdfName.Get("G");

    private static readonly PdfName ColorSpaceName = PdfName.Get("ColorSpace"), ShadingName = PdfName.Get("Shading"), Properties = PdfName.Get("Properties");

    /// <summary>Names an inline image gives its colour space by without a resource (ISO 32000-2, 8.9.7, Table 91 and 92).</summary>
    private static readonly HashSet<string> InlineSpaces = ["G", "RGB", "CMYK", "I", "DeviceGray", "DeviceRGB", "DeviceCMYK", "Indexed", "Pattern"];

    /// <summary>
    /// The names the content of a page takes from each category of its resources (ISO 32000-2, 7.8.3, Table 34): fonts (Tf), XObjects (Do),
    /// graphics states (gs), colour spaces (cs, CS and inline images), patterns (scn, SCN — also one only chosen, its name has to stay valid),
    /// shadings (sh), property lists (BDC, DP) — in the page's content and in what draws with the page's resources for want of its own: forms,
    /// tiling patterns, the glyphs of Type 3 fonts, the groups of soft masks. Null where that cannot be told for certain — a content stream that
    /// does not decode (another reader may read it), nesting deeper than 16, the work limit: the caller keeps every resource then
    /// (docs/FEHLERGEDAECHTNIS.md, No. 788).
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page as written (a redacted page with its new content and resources).</param>
    /// <returns>Category → the names used in it; a category nothing uses is missing.</returns>
    public static Dictionary<PdfName, HashSet<PdfName>>? UsedNames(PdfDocument doc, PdfPage page)
    {
        var used = new Dictionary<PdfName, HashSet<PdfName>>();
        if (page.Resources is not { } resources) return used;
        var parts = page.Dictionary.Get(PdfName.Contents) switch
        {
            PdfStream single => [single],
            PdfArray array => Enumerable.Range(0, Math.Min(array.Count, 10_000)).Select(array.Get).ToList(),
            _ => [],
        };
        var visited = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        long budget = MaxOperations;
        // the parts of the content in one: an operator may be split between two (7.8.2) — each part on its own could misread its names
        var body = new List<byte>();
        foreach (var part in parts)
        {
            if (part is not PdfStream stream || Decoded(doc, stream) is not { } data) return null;
            body.AddRange(data);
            body.Add((byte)'\n');
        }
        var bytes = body.ToArray();
        return UsedNames(doc, bytes, bytes.Length, resources, resources, used, visited, 0, ref budget) ? used : null;
    }

    /// <summary>The decoded data of a stream; null if it does not decode whole (an error, or data cut short and read as far as it went).</summary>
    private static byte[]? Decoded(PdfDocument doc, PdfStream stream)
    {
        try
        {
            return doc.DecodeStream(stream) is { Truncated: false } result ? result.Data : null;
        }
        catch (PdfException)
        {
            return null;
        }
    }

    /// <param name="resources">The resources this content names its resources in.</param>
    /// <param name="page">The resources of the page: names count only in content drawn with them; content with resources of its own is searched
    /// through for what draws with the page's for want of its own — also inside it (7.8.3, sentence 18; No. 791).</param>
    private static bool UsedNames(PdfDocument doc, byte[] body, int length, PdfDictionary resources, PdfDictionary page,
        Dictionary<PdfName, HashSet<PdfName>> used, HashSet<PdfStream> visited, int depth, ref long budget)
    {
        if (depth > MaxDepth) return false;
        var counting = ReferenceEquals(resources, page);
        var inherited = new List<(PdfStream Stream, PdfDictionary Resources)>();
        void Use(PdfName category, PdfName name)
        {
            if (!counting) return;
            if (!used.TryGetValue(category, out var names)) used[category] = names = [];
            names.Add(name);
        }
        // what it draws: a form, tiling pattern, glyph or group with its own resources, or with the page's for want of its own
        void Follow(PdfObject? value, PdfDictionary? own = null)
        {
            if (value is PdfStream stream && visited.Add(stream)) inherited.Add((stream, own ?? stream.Dictionary.GetDictionary(PdfName.Resources) ?? page));
        }
        void InlineSpace(PdfObject? space)
        {
            switch (space)
            {
                case PdfName name when !InlineSpaces.Contains(name.Value):
                    Use(ColorSpaceName, name);
                    break;
                case PdfArray { Count: >= 2 } indexed when indexed.Get(1) is PdfName baseSpace && !InlineSpaces.Contains(baseSpace.Value):
                    Use(ColorSpaceName, baseSpace);
                    break;
            }
        }
        var parser = new ContentParser(body, length);
        while (parser.Next() is { } op)
        {
            if (--budget <= 0) return false;
            var operands = parser.Operands;
            switch (op)
            {
                case "Tf" when operands.Count >= 2 && operands[0].Name is { } font:
                    Use(FontName, font);
                    if (resources.GetDictionary(FontName)?.Get(font) is PdfDictionary type3 && type3.GetName(PdfName.Subtype)?.Value == "Type3" &&
                        type3.GetDictionary(CharProcs) is { } procs)
                        foreach (var (glyph, _) in procs.Entries) Follow(procs.Get(glyph), type3.GetDictionary(PdfName.Resources) ?? page);
                    break;
                case "Do" when operands.Count >= 1 && operands[0].Name is { } xobject:
                    Use(XObject, xobject);
                    if (resources.GetDictionary(XObject)?.Get(xobject) is PdfStream form && IsForm(form)) Follow(form);
                    break;
                case "gs" when operands.Count >= 1 && operands[0].Name is { } state:
                    Use(ExtGState, state);
                    Follow(resources.GetDictionary(ExtGState)?.GetDictionary(state)?.GetDictionary(SMask)?.Get(Group));
                    break;
                case "cs" or "CS" when operands.Count >= 1 && operands[0].Name is { } space:
                    Use(ColorSpaceName, space);
                    break;
                // a pattern chosen also with sc/SC (not allowed there; read as widely used readers read it, like CollectNames)
                case "scn" or "SCN" or "sc" or "SC" when operands.Count >= 1 && operands[^1].Name is { } pattern:
                    Use(Pattern, pattern);
                    Follow(resources.GetDictionary(Pattern)?.Get(pattern));
                    break;
                case "sh" when operands.Count >= 1 && operands[0].Name is { } shading:
                    Use(ShadingName, shading);
                    break;
                case "BDC" or "DP" when operands.Count >= 2 && operands[1].Name is { } list:
                    Use(Properties, list);
                    break;
                case "BI" when parser.InlineImage is { } image:
                    InlineSpace(image.Dictionary.Get(ColorSpaceName));
                    // both spellings (Table 91): an inline image that has the abbreviation and the full name with different values is read
                    // one way by some readers and the other way by others — the resource the overridden full name gives stays too
                    // (docs/FEHLERGEDAECHTNIS.md, No. 885; before, it went, and a reader taking the full name drew nothing there)
                    if (image.Overridden?.Get(ColorSpaceName) is { } overridden and not PdfNull) InlineSpace(overridden);
                    break;
            }
        }
        foreach (var (stream, own) in inherited)
        {
            if (Decoded(doc, stream) is not { } data || !UsedNames(doc, data, data.Length, own, page, used, visited, depth + 1, ref budget)) return false;
        }
        return true;
    }

    /// <summary>
    /// Current pattern for fill and stroke (with q/Q): set with "/Name scn", cancelled by any other colour or colour space.
    /// <see cref="FillPattern"/>/<see cref="StrokePattern"/>: the pattern this operator paints with; <see cref="Colour"/>: how it was set
    /// (colour space and the colour values of a pattern without colours of its own), to set it again.
    /// </summary>
    internal sealed class PatternState
    {
        private Paint _fill, _stroke;
        private readonly Stack<(Paint Fill, Paint Stroke)> _saved = new();

        /// <summary>A colour: the name of its colour space (null for g, rg, k …), and with a pattern its name and the values before it.</summary>
        internal readonly record struct Paint(PdfName? Space, PdfName? Pattern, Operand[] Values);

        /// <summary>The colours of fill and stroke (the ones q saved stay as they are): to go back to them when operators are read again.</summary>
        public (Paint Fill, Paint Stroke) Colours
        {
            get => (_fill, _stroke);
            set => (_fill, _stroke) = value;
        }

        public void Apply(string op, IReadOnlyList<Operand> operands)
        {
            switch (op)
            {
                case "q":
                    if (_saved.Count < 512) _saved.Push((_fill, _stroke));
                    break;
                case "Q":
                    if (_saved.Count > 0) (_fill, _stroke) = _saved.Pop();
                    break;
                // "sc" with a pattern name paints with the pattern as "scn" does — the display reads them alike in a pattern colour space;
                // taken for clearing it, the pattern counted as unused and left the page while it was still painted
                // (docs/FEHLERGEDAECHTNIS.md, No. 412)
                case "scn" or "sc":
                    _fill = Set(_fill, operands);
                    break;
                case "SCN" or "SC":
                    _stroke = Set(_stroke, operands);
                    break;
                case "cs":
                    _fill = new Paint(operands.Count > 0 ? operands[0].Name : null, null, []);
                    break;
                case "CS":
                    _stroke = new Paint(operands.Count > 0 ? operands[0].Name : null, null, []);
                    break;
                case "g" or "rg" or "k":
                    _fill = default;
                    break;
                case "G" or "RG" or "K":
                    _stroke = default;
                    break;
            }
        }

        private static Paint Set(Paint current, IReadOnlyList<Operand> operands) =>
            operands.Count > 0 && operands[^1].Name is { } name
                ? current with { Pattern = name, Values = operands.Take(operands.Count - 1).Where(o => o.IsNumber).Take(32).ToArray() }
                : current with { Pattern = null };

        public PdfName? FillPattern(string op) =>
            op is "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "Tj" or "TJ" or "'" or "\"" or "Do" or "BI" ? _fill.Pattern : null;

        public PdfName? StrokePattern(string op) => op is "S" or "s" or "B" or "B*" or "b" or "b*" or "Tj" or "TJ" or "'" or "\"" ? _stroke.Pattern : null;

        /// <summary>
        /// The current pattern colour of fill or stroke: its colour space (null if none was set in this content: then a pattern with
        /// colours of its own, as "/Pattern"), the colour values before the name (a pattern without colours), and the pattern's name.
        /// </summary>
        public (PdfName? Space, IReadOnlyList<Operand> Values, PdfName? Pattern) Colour(bool stroke)
        {
            var paint = stroke ? _stroke : _fill;
            return (paint.Space, paint.Values ?? [], paint.Pattern);
        }
    }

    /// <summary>
    /// Object numbers of the source that the content draws (recursively through forms and patterns): images, forms, patterns, and of
    /// Type 3 fonts the glyphs of the characters shown — only those: a font shared by pages keeps the glyphs of all its characters,
    /// and a glyph redacted on one page would otherwise stay in the file through a page that shows other characters of it
    /// (docs/FEHLERGEDAECHTNIS.md, No. 155). For redacted pages, call it with their new content and their new resources.
    /// </summary>
    public static void Objects(PdfDocument doc, byte[] body, PdfDictionary? resources, HashSet<int> used)
    {
        var visited = new HashSet<(PdfStream, PdfDictionary?)>();
        long budget = MaxOperations;
        CollectObjects(doc, body, body.Length, resources, resources, null, used, visited, 0, ref budget);
    }

    /// <summary>As <see cref="Objects(PdfDocument, byte[], PdfDictionary?, HashSet{int})"/> for a stream with its own resources (appearance stream).</summary>
    public static void Objects(PdfDocument doc, PdfStream stream, PdfDictionary? inherited, HashSet<int> used)
    {
        var visited = new HashSet<(PdfStream, PdfDictionary?)> { (stream, null) };
        long budget = MaxOperations;
        using var content = Data(doc, stream);
        CollectObjects(doc, content.Array, content.Length, stream.Dictionary.GetDictionary(PdfName.Resources) ?? inherited, inherited, null, used, visited, 0, ref budget);
    }

    /// <summary>
    /// Forms, tiling patterns, soft-mask groups and Type 3 fonts the page draws that have no resources of their own: each (its dictionary as
    /// read) with the resources it is drawn with — merged where it is drawn with several. PDF 2.0 asks writers to give them (ISO 32000-2,
    /// 7.8.3; docs/FEHLERGEDAECHTNIS.md, No. 549). Pages that hold none such are not decoded (<see cref="MayBorrow"/>).
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page as written (a redacted page with its new content and resources).</param>
    /// <param name="borrowed">Receives the owners and their resources; compared by reference.</param>
    /// <param name="ambiguous">Where given: receives the owners drawn with resources that give one name different things (merged, the first
    /// wins — the owner looks different where it was drawn with the others; docs/FEHLERGEDAECHTNIS.md, No. 782).</param>
    /// <param name="givens">Where given: receives, per owner, every resources dictionary it is drawn with (distinct by reference, in the order
    /// met) — the writer gives an owner of <paramref name="ambiguous"/> one copy per set that fits together (No. 895).</param>
    public static void Borrowed(PdfDocument doc, PdfPage page, Dictionary<PdfDictionary, PdfDictionary> borrowed, HashSet<PdfDictionary>? ambiguous = null,
        Dictionary<PdfDictionary, List<PdfDictionary>>? givens = null)
    {
        if (!MayBorrow(doc, page)) return;
        using var content = PageContent(doc, page.Dictionary);
        var visited = new HashSet<(PdfStream, PdfDictionary?)>();
        long budget = MaxOperations;
        CollectObjects(doc, content.Array, content.Length, page.Resources, page.Resources, null, [], visited, 0, ref budget, borrowed, ambiguous, givens);
    }

    /// <summary>
    /// Whether two resource dictionaries give one name different things: a name in the same category (/Font, /XObject …) naming another
    /// object — not the same object by reference, nor the same instance.
    /// </summary>
    internal static bool Collide(PdfDictionary first, PdfDictionary second)
    {
        foreach (var (key, _) in second.Entries)
        {
            if (first.Get(key) is not PdfDictionary a || second.Get(key) is not PdfDictionary b) continue;
            foreach (var (name, entry) in b.Entries)
            {
                if (a.GetRaw(name) is not { } other) continue;
                var same = other is PdfReference ra && entry is PdfReference rb ? ra.Id == rb.Id : ReferenceEquals(a.Get(name), b.Get(name));
                if (!same) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Resources that two callers give one owner: those of the first, completed by those of the second (where names collide, the first
    /// wins) — a form drawn with other things of the same name on two pages looks like on the first.
    /// </summary>
    internal static PdfDictionary Merged(PdfDictionary first, PdfDictionary second)
    {
        var merged = new PdfDictionary { Resolver = first.Resolver };
        foreach (var (key, value) in first.Entries) merged.Set(key, value);
        foreach (var (key, value) in second.Entries)
        {
            if (!merged.ContainsKey(key))
            {
                merged.Set(key, value);
                continue;
            }
            if (merged.Get(key) is PdfDictionary a && second.Get(key) is PdfDictionary b)
            {
                var category = new PdfDictionary { Resolver = a.Resolver };
                foreach (var (name, entry) in a.Entries) category.Set(name, entry);
                foreach (var (name, entry) in b.Entries)
                    if (!category.ContainsKey(name)) category.Set(name, entry);
                merged.Set(key, category);
            }
        }
        return merged;
    }

    /// <summary>
    /// Whether the resources of the page reach a form, tiling pattern, soft-mask group or Type 3 font without resources of its own, without
    /// decoding content (as <see cref="MayUse"/>).
    /// </summary>
    private static bool MayBorrow(PdfDocument doc, PdfPage page)
    {
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(PdfDictionary Resources, int Depth)>();
        if (page.Resources is { } top) pending.Push((top, 0));
        // true for a stream with content and without resources; otherwise its resources are looked into
        bool Lacks(PdfObject? value, int depth, bool hasContent)
        {
            if ((value is PdfReference r ? doc.Resolve(r.Id) : value) is not PdfStream s) return false;
            if (!(hasContent || IsForm(s) || s.Dictionary.ContainsKey(PdfName.Get("PatternType")))) return false;
            if (s.Dictionary.GetDictionary(PdfName.Resources) is not { } inner) return true;
            if (depth < MaxDepth) pending.Push((inner, depth + 1));
            return false;
        }
        while (pending.Count > 0 && seen.Count < 10_000)
        {
            var (resources, depth) = pending.Pop();
            if (!seen.Add(resources)) continue;
            foreach (var kind in (ReadOnlySpan<PdfName>)[XObject, Pattern])
                if (resources.GetDictionary(kind) is { } entries)
                    foreach (var (_, value) in entries.Entries)
                        if (Lacks(value, depth, false)) return true;
            if (resources.GetDictionary(ExtGState) is { } states)
                foreach (var (key, _) in states.Entries)
                    if (Lacks(states.GetDictionary(key)?.GetDictionary(SMask)?.GetRaw(Group), depth, true)) return true;
            if (resources.GetDictionary(FontName) is { } fonts)
                foreach (var (key, _) in fonts.Entries)
                {
                    if (fonts.Get(key) is not PdfDictionary font || font.GetName(PdfName.Subtype)?.Value != "Type3") continue;
                    if (font.GetDictionary(PdfName.Resources) is not { } own) return true;
                    if (depth < MaxDepth) pending.Push((own, depth + 1));
                }
        }
        return false;
    }

    /// <param name="font">The current font when the content starts (a form shows text with its caller's font until its own Tf).</param>
    /// <param name="borrowed">Where given: owners without resources of their own and those they are drawn with (<see cref="Borrowed"/>).</param>
    /// <param name="ambiguous">Where given: owners whose resources collide (<see cref="Borrowed"/>).</param>
    /// <param name="page">The resources of the page: a form, pattern, glyph or group without its own draws with them, wherever it is invoked
    /// (7.8.3, sentence 18; No. 791).</param>
    private static void CollectObjects(PdfDocument doc, byte[] body, int length, PdfDictionary? resources, PdfDictionary? page, PdfDictionary? font,
        HashSet<int> used, HashSet<(PdfStream, PdfDictionary?)> visited, int depth, ref long budget, Dictionary<PdfDictionary, PdfDictionary>? borrowed = null,
        HashSet<PdfDictionary>? ambiguous = null, Dictionary<PdfDictionary, List<PdfDictionary>>? givens = null)
    {
        if (resources is null) return;
        page ??= resources;
        var parser = new ContentParser(body, length);
        var paint = new PatternState();
        var fonts = new Stack<PdfDictionary?>();
        var known = true;   // the current font is known (not set by a graphics state)
        while (parser.Next() is { } op && --budget > 0)
        {
            var operands = parser.Operands;
            paint.Apply(op, operands);
            if (paint.FillPattern(op) is { } fill) Use(resources.GetDictionary(Pattern)?.GetRaw(fill), page, false, ref budget);
            if (paint.StrokePattern(op) is { } stroke) Use(resources.GetDictionary(Pattern)?.GetRaw(stroke), page, false, ref budget);
            switch (op)
            {
                case "q":
                    if (fonts.Count < 512) fonts.Push(known ? font : null);   // as the display (No. 413)
                    break;
                case "Q" when fonts.Count > 0:
                    font = fonts.Pop();
                    break;
                case "Tf" when operands.Count >= 2:
                    font = operands[0].Name is { } fontName ? resources.GetDictionary(FontName)?.Get(fontName) as PdfDictionary : null;
                    known = true;
                    break;
                case "Do" when operands.Count >= 1 && operands[0].Name is { } name:
                    Use(resources.GetDictionary(XObject)?.GetRaw(name), page, false, ref budget);
                    break;
                // The group of a soft mask draws as well (otherwise a replaced original that only it invokes would be dropped)
                case "gs" when operands.Count >= 1 && operands[0].Name is { } stateName:
                    var state = resources.GetDictionary(ExtGState)?.GetDictionary(stateName);
                    if (state?.GetDictionary(SMask)?.GetRaw(Group) is { } group) Use(group, page, true, ref budget);
                    if (state?.ContainsKey(FontName) == true) (font, known) = (null, false);
                    break;
                case "Tj" or "'" when operands.Count >= 1:
                    Shown(operands[0].Object, ref budget);
                    break;
                case "\"" when operands.Count >= 3:
                    Shown(operands[2].Object, ref budget);
                    break;
                case "TJ" when operands.Count >= 1 && operands[0].Object is PdfArray parts:
                    for (var i = 0; i < parts.Count; i++) Shown(parts.Get(i), ref budget);
                    break;
            }
        }

        // The glyphs of a Type 3 font draw: those of the characters shown; with an unknown font, those of every Type 3 font at hand
        void Shown(PdfObject? value, ref long budget)
        {
            if (value is not PdfString { Bytes.Length: > 0 } text) return;
            if (!known || font is null)
            {
                if (resources.GetDictionary(FontName) is not { } all) return;
                foreach (var (key, _) in all.Entries)
                    if (all.Get(key) is PdfDictionary candidate) Glyphs(candidate, null, ref budget);
                return;
            }
            Glyphs(font, text.Bytes, ref budget);
        }

        void Glyphs(PdfDictionary type3, byte[]? codes, ref long budget)
        {
            if (type3.GetName(PdfName.Subtype)?.Value != "Type3" || type3.GetDictionary(CharProcs) is not { } procs) return;
            var names = codes is null ? null : Type3Glyphs(type3, codes);
            if (type3.GetDictionary(PdfName.Resources) is null) Lend(type3, page);
            foreach (var (key, _) in procs.Entries)
                if (names is null || names.Contains(key)) Use(procs.GetRaw(key), type3.GetDictionary(PdfName.Resources) ?? page, true, ref budget);
        }

        // hasContent: is drawn even without /Subtype /Form (glyph of a Type 3 font, group of a mask)
        void Use(PdfObject? raw, PdfDictionary inheritedResources, bool hasContent, ref long budget)
        {
            if (raw is PdfReference reference) used.Add(reference.Id.Number);
            var resolved = raw is PdfReference r ? doc.Resolve(r.Id) : raw;
            // a form is followed once per font it starts with (its text without Tf uses that font); a glyph or group starts without one
            var start = hasContent ? null : known ? font : null;
            if (depth >= MaxDepth || resolved is not PdfStream stream || !visited.Add((stream, start))) return;
            // Forms and tiling patterns have content: what it draws is used as well (images stand alone)
            if (hasContent || IsForm(stream) || stream.Dictionary.ContainsKey(PdfName.Get("PatternType")))
            {
                // a glyph procedure goes with its font (noted there), a form, pattern or group on its own
                if ((IsForm(stream) || !hasContent) && stream.Dictionary.GetDictionary(PdfName.Resources) is null) Lend(stream.Dictionary, inheritedResources);
                using var content = Data(doc, stream);
                CollectObjects(doc, content.Array, content.Length, stream.Dictionary.GetDictionary(PdfName.Resources) ?? inheritedResources, page, start, used,
                    visited, depth + 1, ref budget, borrowed, ambiguous, givens);
            }
        }

        void Lend(PdfDictionary owner, PdfDictionary given)
        {
            if (borrowed is null) return;
            if (givens is not null)
            {
                if (!givens.TryGetValue(owner, out var all)) givens[owner] = all = [];
                if (!all.Exists(known => ReferenceEquals(known, given))) all.Add(given);
            }
            if (!borrowed.TryGetValue(owner, out var known)) borrowed[owner] = given;
            else if (!ReferenceEquals(known, given))
            {
                if (ambiguous is not null && Collide(known, given)) ambiguous.Add(owner);
                borrowed[owner] = Merged(known, given);
            }
        }
    }

    /// <summary>
    /// The glyph names (keys of /CharProcs) of the codes in a Type 3 font, through /Differences; null if the font maps codes through a
    /// base encoding only — then every glyph may be shown.
    /// </summary>
    internal static HashSet<PdfName>? Type3Glyphs(PdfDictionary font, IEnumerable<byte> codes)
    {
        if (font.Get(PdfName.Get("Encoding")) is not PdfDictionary encoding || encoding.GetArray(PdfName.Get("Differences")) is not { } differences) return null;
        var wanted = new bool[256];
        foreach (var c in codes) wanted[c] = true;
        var names = new HashSet<PdfName>();
        var code = 0;
        for (var i = 0; i < differences.Count; i++)
        {
            if (differences.Get(i).AsNumber() is { } number) code = (int)number;
            else if (differences.Get(i) is PdfName name)
            {
                if (code is >= 0 and < 256 && wanted[code]) names.Add(name);
                code++;
            }
        }
        return names;
    }

    /// <summary>
    /// Whether the page can draw any of the objects at all. Checks only the resources (including those of forms, patterns, soft-mask
    /// groups and Type 3 glyphs in them), without decoding content, so that <see cref="PageObjects"/> need not read pages that reach none of them.
    /// </summary>
    public static bool MayUse(PdfDocument doc, PdfPage page, IReadOnlySet<int> objects)
    {
        if (page.Dictionary.ContainsKey(PdfName.Get("Annots"))) return true;   // appearance streams: check exactly
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(PdfDictionary Resources, int Depth)>();
        if (page.Resources is { } top) pending.Push((top, 0));
        while (pending.Count > 0 && seen.Count < 10_000)
        {
            var (resources, depth) = pending.Pop();
            if (!seen.Add(resources)) continue;
            foreach (var kind in (ReadOnlySpan<PdfName>)[XObject, Pattern])
            {
                if (resources.GetDictionary(kind) is not { } entries) continue;
                foreach (var (_, value) in entries.Entries)
                {
                    if (value is PdfReference r && objects.Contains(r.Id.Number)) return true;
                    if (depth < MaxDepth && (value is PdfReference rr ? doc.Resolve(rr.Id) : value) is PdfStream s &&
                        s.Dictionary.GetDictionary(PdfName.Resources) is { } inner)
                        pending.Push((inner, depth + 1));
                }
            }
            // the groups of soft masks draw as well: a shadow drawn only through its mask reaches its image that way alone
            // (docs/FEHLERGEDAECHTNIS.md, No. 171)
            if (resources.GetDictionary(ExtGState) is { } states)
                foreach (var (key, _) in states.Entries)
                {
                    if (states.GetDictionary(key)?.GetDictionary(SMask)?.GetRaw(Group) is not { } group) continue;
                    if (group is PdfReference g && objects.Contains(g.Id.Number)) return true;
                    if (depth < MaxDepth && (group is PdfReference gr ? doc.Resolve(gr.Id) : group) is PdfStream form &&
                        form.Dictionary.GetDictionary(PdfName.Resources) is { } inner)
                        pending.Push((inner, depth + 1));
                }
            // the glyphs of Type 3 fonts (shared by pages: dropped on one page, still drawn on another)
            if (resources.GetDictionary(FontName) is { } fonts)
                foreach (var (key, _) in fonts.Entries)
                {
                    if (fonts.Get(key) is not PdfDictionary font || font.GetDictionary(CharProcs) is not { } procs) continue;
                    foreach (var (_, proc) in procs.Entries)
                        if (proc is PdfReference glyph && objects.Contains(glyph.Id.Number)) return true;
                    if (depth < MaxDepth && font.GetDictionary(PdfName.Resources) is { } own) pending.Push((own, depth + 1));
                }
        }
        return false;
    }

    /// <summary>What a page draws: content (also new content in memory) and the appearance streams of its annotations.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="used">Receives the object numbers.</param>
    /// <param name="replaced">Objects that the new file writes in place of the original ones (an appearance stream without its
    /// invisible text, for example); null = the originals.</param>
    public static void PageObjects(PdfDocument doc, PdfPage page, HashSet<int> used, IReadOnlyDictionary<int, PdfObject>? replaced = null)
    {
        PdfObject? Resolve(PdfReference r) => replaced?.GetValueOrDefault(r.Id.Number) ?? doc.Resolve(r.Id);
        using (var content = PageContent(doc, page.Dictionary))
        {
            var visited = new HashSet<(PdfStream, PdfDictionary?)>();
            long budget = MaxOperations;
            CollectObjects(doc, content.Array, content.Length, page.Resources, page.Resources, null, used, visited, 0, ref budget);
        }
        if (page.Dictionary.Get(PdfName.Get("Annots")) is not PdfArray annots) return;
        for (var i = 0; i < annots.Count && i < 10_000; i++)
        {
            if (annots.Get(i) is not PdfDictionary annot || annot.GetDictionary(PdfName.Get("AP")) is not { } ap) continue;
            foreach (var (_, value) in ap.Entries)
            {
                // /N, /R, /D: a stream or a dictionary of states
                var resolved = value is PdfReference r ? Resolve(r) : value;
                if (value is PdfReference direct) used.Add(direct.Id.Number);
                if (resolved is PdfStream appearance) Objects(doc, appearance, null, used);
                else if (resolved is PdfDictionary states)
                    foreach (var (_, state) in states.Entries)
                    {
                        if (state is PdfReference sr) used.Add(sr.Id.Number);
                        if ((state is PdfReference s2 ? Resolve(s2) : state) is PdfStream stateStream) Objects(doc, stateStream, null, used);
                    }
            }
        }
    }

    private static bool IsForm(PdfStream stream) => stream.Dictionary.GetName(PdfName.Subtype)?.Value == "Form";

    /// <summary>Decodes into a pooled buffer (like the interpreter), so that a check does not make a new copy of every content stream.</summary>
    private static PooledBytes Data(PdfDocument doc, PdfStream stream)
    {
        try
        {
            return doc.DecodeStreamPooled(stream);
        }
        catch (PdfException)
        {
            return new PooledBytes([], 0, pooled: false);
        }
    }

    /// <summary>Page content (one stream or several concatenated) in a pooled buffer.</summary>
    private static PooledBytes PageContent(PdfDocument doc, PdfDictionary page)
    {
        switch (page.Get(PdfName.Contents))
        {
            case PdfStream single:
                return Data(doc, single);
            case PdfArray parts:
                var pieces = new List<PooledBytes>();
                try
                {
                    for (var i = 0; i < parts.Count; i++)
                        if (parts.Get(i) is PdfStream part) pieces.Add(Data(doc, part));
                    var total = (int)Math.Min(int.MaxValue / 2, pieces.Sum(p => (long)p.Length + 1));
                    var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(total, 1));
                    var length = 0;
                    foreach (var piece in pieces)
                    {
                        if (length + piece.Length + 1 > buffer.Length) break;
                        piece.Span.CopyTo(buffer.AsSpan(length));
                        length += piece.Length;
                        buffer[length++] = (byte)'\n';
                    }
                    return new PooledBytes(buffer, length, pooled: true);
                }
                finally
                {
                    foreach (var piece in pieces) piece.Dispose();
                }
            default:
                return new PooledBytes([], 0, pooled: false);
        }
    }
}
