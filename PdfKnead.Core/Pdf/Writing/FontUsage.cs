using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// Which glyphs of embedded TrueType fonts a document uses (for compression): page contents, forms and annotation
/// appearances are scanned, and for each text operator the codes are converted to glyphs through the font
/// (<see cref="PdfFont.TrueTypeGlyphs"/>, a superset). Whatever cannot be determined with certainty stays whole
/// (<see cref="Whole"/>): fonts of the form (/DR; other programs use them to write new characters when filling in) — for redaction a
/// subset among them keeps only what the pages show and what the values of the fields need (<see cref="FormFont"/>) — and everything if a
/// content stream is unreadable. A font set by a graphics state (gs with /Font) counts like one set by Tf (docs/FEHLERGEDAECHTNIS.md,
/// No. 700).
/// </summary>
internal sealed class FontUsage(PdfDocument doc, IReadOnlyDictionary<int, PdfObject>? replaced = null)
{
    private static readonly PdfName Font = PdfName.Get("Font"), XObject = PdfName.Get("XObject"), ExtGState = PdfName.Get("ExtGState"),
        DescendantFonts = PdfName.Get("DescendantFonts"), FontDescriptor = PdfName.Get("FontDescriptor"), FontFile2 = PdfName.Get("FontFile2"),
        FontName = PdfName.Get("FontName"), Form = PdfName.Get("Form"), Annots = PdfName.Get("Annots"), AP = PdfName.Get("AP"),
        AcroForm = PdfName.Get("AcroForm"), DR = PdfName.Get("DR");

    /// <summary>Font file (object number of the FontFile2 stream) → glyphs used.</summary>
    public Dictionary<int, HashSet<int>> Glyphs { get; } = [];

    /// <summary>Font files that must stay whole.</summary>
    public HashSet<int> Whole { get; } = [];

    /// <summary>Codes (and their length) shown with each font, of any kind — what redaction may keep of a font (<see cref="FontScrub"/>).</summary>
    public Dictionary<PdfDictionary, HashSet<(uint Code, int Length)>> Codes { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Fonts that must stay whole: those of the form (/DR) that are not a subset with a ToUnicode map (<see cref="FormFont"/>).</summary>
    public HashSet<PdfDictionary> WholeFonts { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The object number of each font dictionary found as an indirect object — redaction rewrites a font whose program cannot be scrubbed
    /// (<see cref="FontScrub"/>; docs/FEHLERGEDAECHTNIS.md, No. 1164).
    /// </summary>
    public Dictionary<PdfDictionary, int> Numbers { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>A content stream was unreadable: nothing is subset in this document then.</summary>
    public bool Failed { get; private set; }

    private readonly HashSet<(PdfStream, PdfDictionary?, PdfDictionary?)> _forms = [];
    private readonly HashSet<(object, PdfDictionary?)> _pages = [];
    private readonly Dictionary<PdfDictionary, (PdfFont Font, int File, PdfDictionary Dict)?> _fonts = new(ReferenceEqualityComparer.Instance);

    /// <summary>Scans the pages and the form of the document.</summary>
    public FontUsage Collect(IEnumerable<PdfPage> pages, CancellationToken cancel)
    {
        try
        {
            if (doc.Catalog?.GetDictionary(AcroForm) is { } acroForm && acroForm.GetDictionary(DR)?.GetDictionary(Font) is { } formFonts)
            {
                string? values = null;
                foreach (var (_, value) in formFonts.Entries)
                    if (Resolve(value) is { } entry)
                    {
                        if (entry.File >= 0) Whole.Add(entry.File);
                        FormFont(entry.Dict, values ??= FieldValues(acroForm));
                    }
            }
            foreach (var page in pages)
            {
                cancel.ThrowIfCancellationRequested();
                _pageResources = page.Resources;
                // The same content with the same resources (templates, identical pages): once is enough
                if (page.Dictionary.GetRaw(PdfName.Contents) is not { } contents || _pages.Add((contents is PdfReference r ? r.Id : contents, page.Resources)))
                    Scan(PageContent(page.Dictionary), page.Resources, 0);
                if (page.Dictionary.Get(Annots) is PdfArray annots)
                    for (var i = 0; i < annots.Count && i < 10_000; i++)
                        if (annots.Get(i) is PdfDictionary annot && annot.GetDictionary(AP) is { } ap)
                            foreach (var (_, look) in ap.Entries)
                                switch (look)
                                {
                                    case PdfReference or PdfStream when Deref(look) is PdfStream stream:
                                        ScanForm(stream, null, 1);
                                        break;
                                    case PdfReference or PdfDictionary when Deref(look) is PdfDictionary states:
                                        foreach (var (_, state) in states.Entries)
                                            if (Deref(state) is PdfStream s) ScanForm(s, null, 1);
                                        break;
                                }
            }
        }
        catch (Exception e) when (e is PdfException or InvalidDataException or IndexOutOfRangeException or ArgumentException or Core.Fonts.FontFormatException)
        {
            Failed = true;
        }
        return this;
    }

    /// <summary>
    /// A font of the form (/DR): other programs write new values with it. A complete font stays whole — it tells nothing about the text, all its
    /// glyphs are there anyway. A subset („ABCDEF+Name“, ISO 32000-2, 9.9.2) holds what was written with it, and so the letters of redacted
    /// text: it keeps what the pages show and every code whose text occurs in the values of the fields (read through its ToUnicode map); without
    /// that map it stays whole (docs/FEHLERGEDAECHTNIS.md, No. 700; before, every form font stayed whole, No. 169).
    /// </summary>
    private void FormFont(PdfDictionary font, string values)
    {
        var name = (font.GetArray(DescendantFonts) is { Count: >= 1 } kids && kids.Get(0) is PdfDictionary kid ? kid : font).GetName(PdfName.Get("BaseFont"))?.Value ?? "";
        var subset = name.Length > 7 && name[6] == '+' && name.AsSpan(0, 6).IndexOfAnyExceptInRange('A', 'Z') < 0;
        if (!subset || PdfFont.ReadToUnicode(font, doc) is not { } cmap)
        {
            WholeFonts.Add(font);
            return;
        }
        if (!Codes.TryGetValue(font, out var codes)) Codes[font] = codes = [];
        foreach (var (code, length) in cmap.UnicodeCodes().Take(70_000))
            if (cmap.ToUnicode(code, length) is { Length: > 0 } text && text.All(c => char.IsWhiteSpace(c) || values.Contains(c)))
                codes.Add((code, length));
    }

    /// <summary>The text of the values of all fields (/V, /DV, the options /Opt), as one string — the characters a form font must keep.</summary>
    private string FieldValues(PdfDictionary acroForm)
    {
        var text = new System.Text.StringBuilder();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        void Add(PdfObject? value)
        {
            switch (Deref(value))
            {
                case PdfString s:
                    text.Append(s.ToText());
                    break;
                case PdfName n:
                    text.Append(n.Value);
                    break;
                case PdfArray a:
                    for (var i = 0; i < a.Count && i < 10_000; i++) Add(a[i]);
                    break;
            }
        }
        void Field(PdfObject? node, int depth)
        {
            if (depth > 32 || seen.Count > 100_000 || Deref(node) is not PdfDictionary field || !seen.Add(field)) return;
            Add(field.Get(PdfName.Get("V")));
            Add(field.Get(PdfName.Get("DV")));
            Add(field.Get(PdfName.Get("Opt")));
            if (field.GetArray(PdfName.Kids) is { } kids)
                for (var i = 0; i < kids.Count; i++) Field(kids[i], depth + 1);
        }
        if (acroForm.GetArray(PdfName.Get("Fields")) is { } fields)
            for (var i = 0; i < fields.Count; i++) Field(fields[i], 0);
        return text.ToString();
    }

    // Objects the new file writes in place of the originals (an appearance stream without its invisible text) count instead of them
    private PdfObject? Deref(PdfObject? obj) => obj is PdfReference r ? replaced?.GetValueOrDefault(r.Id.Number) ?? doc.Resolve(r.Id) : obj;

    private byte[] PageContent(PdfDictionary page)
    {
        switch (page.Get(PdfName.Contents))
        {
            case PdfStream s:
                return doc.GetStreamData(s);
            case PdfArray parts:
                // bounded together as one stream (No. 1002), counted while joined (No. 1003)
                {
                    var all = new PdfKnead.Core.Pdf.Content.ContentJoiner(parts.Count);
                    for (var i = 0; i < parts.Count; i++)
                        if (parts.Get(i) is PdfStream part) all.Add(doc.GetStreamData(part));
                    return all.ToArray();
                }
            default:
                return [];
        }
    }

    // the resources of the page scanned: what has none of its own draws with them (ISO 32000-2, 7.8.3, sentence 18; docs/FEHLERGEDAECHTNIS.md,
    // No. 791) — as the display reads it
    private PdfDictionary? _pageResources;

    /// <summary>
    /// Scans a form; <paramref name="font"/> is the font it inherits (a form may show text without its own Tf). Without resources of its own it
    /// draws with those of the page — an appearance scanned on its own (no caller) with none, as the display draws it.
    /// </summary>
    private void ScanForm(PdfStream form, PdfDictionary? inherited, int depth, (PdfFont Font, int File, PdfDictionary Dict)? font = null)
    {
        var resources = form.Dictionary.GetDictionary(PdfName.Resources) ?? (inherited is null ? null : _pageResources ?? inherited);
        if (depth > 32 || !_forms.Add((form, resources, font?.Dict))) return;
        Scan(doc.GetStreamData(form), resources, depth, font);
    }

    private readonly HashSet<PdfDictionary> _resources = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// What can show text even without a text operator in the content: tiling patterns, soft masks (groups), Type 3 glyphs.
    /// Once per resource dictionary.
    /// </summary>
    private void ScanExtras(PdfDictionary? resources, int depth)
    {
        if (resources is null || depth > 32 || !_resources.Add(resources)) return;
        if (resources.GetDictionary(PdfName.Get("Pattern")) is { } patterns)
            foreach (var (_, value) in patterns.Entries)
                if (Deref(value) is PdfStream pattern) ScanForm(pattern, resources, depth + 1);
        if (resources.GetDictionary(ExtGState) is { } states)
            foreach (var (_, value) in states.Entries)
                if (Deref(value) is PdfDictionary state && state.GetDictionary(PdfName.Get("SMask"))?.Get(PdfName.Get("G")) is PdfStream group)
                    ScanForm(group, resources, depth + 1);
        if (resources.GetDictionary(Font) is { } fonts)
            foreach (var (_, value) in fonts.Entries)
                if (Deref(value) is PdfDictionary font && font.GetDictionary(PdfName.Get("CharProcs")) is { } procs)
                    foreach (var (_, proc) in procs.Entries)
                        if (Deref(proc) is PdfStream glyph) ScanForm(glyph, font.GetDictionary(PdfName.Resources) ?? _pageResources ?? resources, depth + 1);
    }

    private void Scan(byte[] content, PdfDictionary? resources, int depth, (PdfFont Font, int File, PdfDictionary Dict)? current = null)
    {
        ScanExtras(resources, depth);
        var parser = new ContentParser(content);
        // The font belongs to the graphics state: q saves it, Q restores it. Every operator below is read exactly as the display reads it
        // (PageInterpreter): a font this counted differently from the display lost the widths of characters still shown, and the rest of
        // their line slid under a bar (docs/FEHLERGEDAECHTNIS.md, No. 379) — a Tf with one operand left the display's font as it was
        var saved = new Stack<(PdfFont Font, int File, PdfDictionary Dict)?>();
        // Tiling patterns and soft masks run with the font of the state they paint in, as forms do: text in them without a Tf of their own
        // is counted with it (docs/FEHLERGEDAECHTNIS.md, No. 427)
        var paint = new Editing.ResourceUsage.PatternState();
        while (parser.Next() is { } op)
        {
            var operands = parser.Operands;
            paint.Apply(op, operands);
            if (current is not null)
            {
                if (paint.FillPattern(op) is { } fill) ScanPattern(fill, resources, depth, current);
                if (paint.StrokePattern(op) is { } stroke) ScanPattern(stroke, resources, depth, current);
            }
            switch (op)
            {
                case "q":
                    // as the display: states nested deeper than it saves are not saved
                    if (saved.Count < SavedStates) saved.Push(current);
                    break;
                case "Q" when saved.Count > 0:
                    current = saved.Pop();
                    break;
                case "Tf" when operands.Count >= 2:
                    current = operands[0].Name is { } name && resources?.GetDictionary(Font) is { } fonts && fonts.Get(name) is PdfDictionary font
                        ? Resolve(font, fonts.GetRaw(name)) : null;
                    break;
                case "Tj" or "'" when operands.Count >= 1 && operands[0].Object is PdfString shown:
                    Add(current, shown);
                    break;
                case "\"" when operands.Count >= 3 && operands[2].Object is PdfString quoted:
                    Add(current, quoted);
                    break;
                case "TJ" when operands.Count >= 1 && operands[0].Object is PdfArray parts:
                    for (var i = 0; i < parts.Count; i++) Add(current, parts.Get(i));
                    break;
                case "Do" when operands.Count >= 1 && operands[0].Name is { } xname &&
                               resources?.GetDictionary(XObject)?.Get(xname) is PdfStream form && form.Dictionary.GetName(PdfName.Subtype) == Form:
                    ScanForm(form, resources, depth + 1, current);
                    break;
                case "gs" when operands.Count >= 1 && operands[0].Name is { } gname && resources?.GetDictionary(ExtGState)?.GetDictionary(gname) is { } state:
                    // Font from a graphics state, read as the display reads it (PageInterpreter): only a font with a size changes it — with
                    // one entry the font shown stays the one before, and its characters must still count (docs/FEHLERGEDAECHTNIS.md, No. 427).
                    // From then on its codes count as with Tf (before it was kept whole, No. 700)
                    if (state.GetArray(Font) is { Count: >= 2 } gsFont && gsFont.GetNumber(1) is not null && Resolve(gsFont[0]) is { } entry)
                        current = entry;
                    if (current is not null && state.GetDictionary(PdfName.Get("SMask"))?.Get(PdfName.Get("G")) is PdfStream group)
                        ScanForm(group, resources, depth + 1, current);
                    break;
            }
        }
    }

    /// <summary>A tiling pattern painted with <paramref name="font"/> as the current font.</summary>
    private void ScanPattern(PdfName name, PdfDictionary? resources, int depth, (PdfFont Font, int File, PdfDictionary Dict)? font)
    {
        if (Deref(resources?.GetDictionary(PdfName.Get("Pattern"))?.GetRaw(name)) is PdfStream pattern) ScanForm(pattern, resources, depth + 1, font);
    }

    /// <summary>Graphics states the display saves at most (PageInterpreter, q).</summary>
    private const int SavedStates = 512;

    /// <summary>Codes already converted per font (code and length): each only once. Converting every character costs seconds
    /// and gigabytes for long texts (200 pages with 10 million characters: 12 s, 7 GB allocated).</summary>
    private readonly Dictionary<PdfFont, HashSet<long>> _codes = new(ReferenceEqualityComparer.Instance);

    private void Add((PdfFont Font, int File, PdfDictionary Dict)? font, PdfObject? text)
    {
        if (font is not { } f || text is not PdfString s) return;
        var bytes = s.Bytes;
        if (!Codes.TryGetValue(f.Dict, out var codes)) Codes[f.Dict] = codes = [];
        HashSet<int>? set = null;
        if (f.File >= 0 && !Glyphs.TryGetValue(f.File, out set)) Glyphs[f.File] = set = [];
        if (!_codes.TryGetValue(f.Font, out var seen)) _codes[f.Font] = seen = [];
        for (var pos = 0; pos < bytes.Length;)
        {
            var length = Math.Max(1, f.Font.ReadCode(bytes, pos, out var code));
            if (seen.Add((long)code << 4 | (uint)Math.Min(length, 15)))
            {
                codes.Add((code, length));
                if (set is not null)
                    foreach (var glyph in f.Font.TrueTypeGlyphs(code, length)) set.Add(glyph);
            }
            pos += length;
        }
    }

    /// <summary>The font of a dictionary; <c>File</c> is the object number of its embedded TrueType file (FontFile2) if that is not yet
    /// subset (for compression), otherwise -1.</summary>
    /// <param name="raw">The entry as written, where <paramref name="fontObject"/> is already resolved (its object number is noted).</param>
    private (PdfFont Font, int File, PdfDictionary Dict)? Resolve(PdfObject fontObject, PdfObject? raw = null)
    {
        if (Deref(fontObject) is not PdfDictionary dict) return null;
        if ((raw ?? fontObject) is PdfReference indirect) Numbers.TryAdd(dict, indirect.Id.Number);
        if (_fonts.TryGetValue(dict, out var known)) return known;
        var file = -1;
        var descriptorHolder = dict.GetArray(DescendantFonts) is { Count: >= 1 } kids && kids.Get(0) is PdfDictionary cid ? cid : dict;
        if (descriptorHolder.GetDictionary(FontDescriptor) is { } descriptor && descriptor.GetRaw(FontFile2) is PdfReference reference)
        {
            var name = descriptor.GetName(FontName)?.Value ?? "";
            var subset = name.Length > 7 && name[6] == '+';   // "ABCDEF+Name": already subset
            if (!subset) file = reference.Id.Number;
        }
        (PdfFont, int, PdfDictionary)? result = (PdfFont.Get(dict, doc), file, dict);
        _fonts[dict] = result;
        return result;
    }
}
