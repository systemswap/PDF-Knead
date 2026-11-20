using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Stores edited text as page content: the block is part of the page itself, so that every program prints it and search and
/// copy find it in the correct order. It is written as a form XObject carrying <see cref="ElementStore.Key"/> (text and
/// formatting); the page carries <see cref="PageKey"/>. On opening, PDF Knead takes these forms out of the page again and turns
/// them back into blocks. Flattening writes them without the marker, which makes them ordinary text.
/// </summary>
/// <remarks>See KONZEPT.md, section 5.14.</remarks>
public static class ContentElements
{
    /// <summary>
    /// Page dictionary entry marking a page that contains edited text; only such pages have their content searched for blocks
    /// on opening.
    /// </summary>
    public static readonly PdfName PageKey = PdfName.Get("PDFK_Text");

    /// <summary>The name of <see cref="PageKey"/> in files of earlier versions (ISO 32000-2, Annex E.2; No. 599); still read, never written.</summary>
    public static readonly PdfName LegacyPageKey = PdfName.Get("PDFKneadText");

    private static readonly PdfName Art = PdfName.Get("Art"), Ausrichtung = PdfName.Get("Ausrichtung"), Abstand = PdfName.Get("Abstand"),
        Grundlinie = PdfName.Get("Grundlinie"), Einzug = PdfName.Get("Einzug"), Abschnitte = PdfName.Get("Abschnitte"), TextKey = PdfName.Get("Text"),
        Familie = PdfName.Get("Familie"), Fett = PdfName.Get("Fett"), Kursiv = PdfName.Get("Kursiv"), Unterstrichen = PdfName.Get("Unterstrichen"),
        Durchgestrichen = PdfName.Get("Durchgestrichen"), Groesse = PdfName.Get("Groesse"), Farbe = PdfName.Get("Farbe"), Hoch = PdfName.Get("Hoch"),
        Breite = PdfName.Get("Breite"), Zeichenabstand = PdfName.Get("Zeichenabstand"), Schrift = PdfName.Get("Schrift"),
        NachgemachtFett = PdfName.Get("NachgemachtFett"), NachgemachtKursiv = PdfName.Get("NachgemachtKursiv"), Tabs = PdfName.Get("Tabs"), TabArten = PdfName.Get("TabArten");

    private const string Kind = "FormatierterText";

    /// <summary>Describes the text and formatting of a block for the form's <see cref="ElementStore.Key"/> entry.</summary>
    /// <param name="element">The text block.</param>
    /// <param name="originals">
    /// Fonts from the file, keyed by their resource name in the form; a span that uses one of them stores that name so that it
    /// uses the same font again after reopening. May be <see langword="null"/>.
    /// </param>
    /// <returns>The data dictionary (format version 1).</returns>
    public static PdfDictionary Describe(RichTextElement element, IReadOnlyDictionary<PdfName, OriginalFont>? originals = null)
    {
        var data = new PdfDictionary();
        data.Set(PdfName.Get("Version"), PdfInteger.Of(1));
        data.Set(Art, PdfName.Get(Kind));
        data.Set(Ausrichtung, PdfName.Get(element.Alignment.ToString()));
        data.Set(Abstand, new PdfReal(element.LineGap));
        if (element.FirstBaseline is { } baseline) data.Set(Grundlinie, new PdfReal(baseline));
        if (element.HangingIndent != 0) data.Set(Einzug, new PdfReal(element.HangingIndent));
        if (element.TabStops.Count > 0) data.Set(Tabs, new PdfArray(element.TabStops.Select(t => (PdfObject)new PdfReal(t.Position))));
        // Right- and centre-aligned tab stops; files without this entry have all tab stops left-aligned
        if (element.TabStops.Any(t => t.Alignment != BlockAlignment.Left))
            data.Set(TabArten, new PdfArray(element.TabStops.Select(t => (PdfObject)PdfName.Get(t.Alignment.ToString()))));
        var spans = new PdfArray();
        foreach (var span in element.Content.Spans)
        {
            var s = span.Style;
            var entry = new PdfDictionary();
            entry.Set(TextKey, new PdfString(PdfTextEncoding.Encode(span.Text)));
            entry.Set(Familie, new PdfString(PdfTextEncoding.Encode(s.Family)));
            if (s.Bold) entry.Set(Fett, PdfBoolean.Of(true));
            if (s.Italic) entry.Set(Kursiv, PdfBoolean.Of(true));
            if (s.Underline) entry.Set(Unterstrichen, PdfBoolean.Of(true));
            if (s.Strikeout) entry.Set(Durchgestrichen, PdfBoolean.Of(true));
            entry.Set(Groesse, new PdfReal(s.Size));
            if (s.Color != 0) entry.Set(Farbe, PdfInteger.Of(s.Color));
            if (s.Rise != 0) entry.Set(Hoch, new PdfReal(s.Rise));
            if (Math.Abs(s.Stretch - 1) > 1e-6) entry.Set(Breite, new PdfReal(s.Stretch));
            if (s.CharSpacing != 0) entry.Set(Zeichenabstand, new PdfReal(s.CharSpacing));
            // Font from the file: its name in the form's resources, so that the span uses it again after reopening
            if (s.Original is { } original && originals?.FirstOrDefault(o => o.Value.Equals(original)).Key is { } font)
            {
                entry.Set(Schrift, font);
                if (original.FakeBold) entry.Set(NachgemachtFett, PdfBoolean.Of(true));
                if (original.FakeItalic) entry.Set(NachgemachtKursiv, PdfBoolean.Of(true));
            }
            spans.Add(entry);
        }
        data.Set(Abschnitte, spans);
        return data;
    }

    /// <summary>Determines whether a form XObject is a stored text block.</summary>
    /// <param name="form">The form XObject.</param>
    /// <returns><see langword="true"/> if the form carries block data written by <see cref="Describe"/>.</returns>
    public static bool IsBlock(PdfStream form) => ElementStore.DataOf(form.Dictionary)?.GetName(Art)?.Value == Kind;

    /// <summary>
    /// Maximum number of blocks per page and of characters in all of them together, against crafted files (memory bomb, hang
    /// on opening).
    /// </summary>
    private const int MaxBlocks = 2_000, MaxChars = 2_000_000;

    /// <summary>
    /// Takes the edited text out of a page: returns the page without it (in memory) and the blocks.
    /// </summary>
    /// <param name="doc">The document that contains the page.</param>
    /// <param name="page">The page to examine.</param>
    /// <returns>
    /// The page content without the blocks and the blocks themselves; <see langword="null"/> if the page contains no edited text
    /// or it cannot be read. In that case the page stays as it is, because the text is ordinary content anyway.
    /// </returns>
    public static (Redactor.HiddenContentResult Page, IReadOnlyList<RichTextElement> Elements)? Extract(PdfDocument doc, PdfPage page)
    {
        if (page.Dictionary.GetBoolean(PageKey) != true && page.Dictionary.GetBoolean(LegacyPageKey) != true) return null;
        try
        {
            // Read the data of each form once (a form can be invoked several times), with a budget for the whole page
            var parsed = new Dictionary<PdfStream, Parsed?>(ReferenceEqualityComparer.Instance);
            var chars = 0;
            Parsed? Data(PdfStream form)
            {
                if (parsed.TryGetValue(form, out var known)) return known;
                var data = IsBlock(form) ? Parse(form, doc, MaxChars - chars) : null;
                if (data is not null) chars += data.Chars;
                return parsed[form] = data;
            }
            var found = Redactor.RemoveForms(doc, page, (form, toPage) => Data(form) is { } data && Place(form, data, toPage) is not null ? data.Visible : null, MaxBlocks);
            if (found is not { } f) return null;
            var elements = new List<RichTextElement>();
            var around = McidsAroundForms(doc, page);
            foreach (var (form, toPage) in f.Removed)
                if (Data(form) is { } data && Place(form, data, toPage) is { } element)
                    elements.Add(form.Id is { } id && around.TryGetValue(id.Number, out var mcid) ? element with { ReplacesMcids = [mcid] } : element);
            return (f.Result, elements);
        }
        catch (Exception e) when (e is PdfException or InvalidDataException or IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The marked content around the forms the page content draws at its top level: per form (object number) the innermost MCID of a BDC around its Do (a
    /// property list in the content or named in /Properties). A block written into a tagged page lies in the marked content of its element
    /// (docs/ABLAUFPLAN.md, 6.6 i); reopened, it carries that MCID, so that the next save gives its text to the same element again instead of
    /// stacking another one. A form drawn more than once keeps the first.
    /// </summary>
    private static Dictionary<int, int> McidsAroundForms(PdfDocument doc, PdfPage page)
    {
        var result = new Dictionary<int, int>();
        if (NumberTree.KeyOf(page.Dictionary.Get(PdfName.Get("StructParents"))) is null) return result;
        var xobjects = page.Resources?.GetDictionary(PdfName.Get("XObject"));
        var properties = page.Resources?.GetDictionary(PdfName.Get("Properties"));
        if (xobjects is null) return result;
        using var content = Content.PageInterpreter.PageContent(doc, page.Dictionary);
        var parser = new Content.ContentParser(content.Span.ToArray());
        var open = new Stack<int?>();
        string? op;
        for (var steps = 0; (op = parser.Next()) is not null && steps < 1_000_000; steps++)
            switch (op)
            {
                case "BMC":
                    open.Push(null);
                    break;
                case "BDC":
                    var list = parser.Operands.Count == 2 ? parser.Operands[1].Object as PdfDictionary ??
                        (parser.Operands[1].Name is { } name ? properties?.GetDictionary(name) : null) : null;
                    open.Push(list?.GetInteger(PdfName.Get("MCID")) is { } id and >= 0 and <= int.MaxValue ? (int)id : null);
                    break;
                case "EMC":
                    open.TryPop(out _);
                    break;
                case "Do" when parser.Operands.Count > 0 && parser.Operands[^1].Name is { } formName && xobjects.GetRaw(formName) is PdfReference form &&
                               open.FirstOrDefault(m => m is not null) is { } mcid:
                    result.TryAdd(form.Id.Number, mcid);
                    break;
            }
        return result;
    }

    /// <summary>Data read from a form: the block without position, the text the form must show (without white space), and the block's length in characters.</summary>
    private sealed record Parsed(RichTextElement Element, string Visible, int Chars);

    /// <summary>
    /// Puts a block in its place: position from /BBox and the form-to-page mapping, rotation from /Matrix. Only when the form is
    /// merely translated: if the page scales or rotates it from outside (another tool, n-up), font sizes and line breaks would
    /// no longer fit, so it stays content.
    /// </summary>
    private static RichTextElement? Place(PdfStream form, Parsed data, Matrix formToPage)
    {
        if (PdfRectangle.From(form.Dictionary.Get(PdfName.Get("BBox"))) is not { IsEmpty: false } box) return null;
        var m = Matrix.FromArray(form.Dictionary.GetArray(PdfName.Get("Matrix"))) ?? Matrix.Identity;
        const double tolerance = 1e-6;
        if (Math.Abs(formToPage.A - m.A) > tolerance || Math.Abs(formToPage.B - m.B) > tolerance || Math.Abs(formToPage.C - m.C) > tolerance ||
            Math.Abs(formToPage.D - m.D) > tolerance) return null;
        var rotation = (m.A, m.B, m.C, m.D) switch
        {
            (1, 0, 0, 1) => 0,
            (0, 1, -1, 0) => 90,
            (-1, 0, 0, -1) => 180,
            (0, -1, 1, 0) => 270,
            _ => -1,
        };
        if (rotation < 0) return null;
        var bounds = formToPage.TransformBounds(RectD.FromLtrb(box.Left, box.Bottom, box.Right, box.Top));
        var rect = new PdfRectangle(bounds.Left, Math.Min(bounds.Top, bounds.Bottom), bounds.Right, Math.Max(bounds.Top, bounds.Bottom));
        const double limit = 14_400;   // largest page (200 inches)
        if (!double.IsFinite(rect.Left + rect.Right + rect.Bottom + rect.Top) || Math.Abs(rect.Left) > limit * 2 || Math.Abs(rect.Right) > limit * 2 ||
            Math.Abs(rect.Bottom) > limit * 2 || Math.Abs(rect.Top) > limit * 2 || rect.Width is <= 0 or > limit || rect.Height is <= 0 or > limit) return null;
        return data.Element with { Rect = rect, Rotation = rotation };
    }

    /// <summary>Reads text and formatting from the form's data (without position); null if the data is missing or exceeds the budget of <paramref name="budget"/> characters.</summary>
    private static Parsed? Parse(PdfStream form, PdfDocument doc, int budget)
    {
        if (ElementStore.DataOf(form.Dictionary) is not { } data || data.GetArray(Abschnitte) is not { } list) return null;
        if (list.Count > 100_000) return null;
        // Check the length before decoding (entries can reference the same large string thousands of times)
        long bytes = 0;
        for (var i = 0; i < list.Count; i++)
            if (list.Get(i) is PdfDictionary entry && entry.GetString(TextKey) is { } s && (bytes += s.Bytes.Length) > 2L * budget) return null;
        var spans = new List<RichSpan>();
        var fonts = form.Dictionary.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("Font"));
        for (var i = 0; i < list.Count; i++)
        {
            if (list.Get(i) is not PdfDictionary entry) continue;
            // Font from the file: from the form's resources, with its object number
            OriginalFont? original = null;
            if (entry.GetName(Schrift) is { } fontName && fonts?.Get(fontName) is PdfDictionary fontDict)
                original = new OriginalFont(doc, fontDict, entry.GetBoolean(NachgemachtFett) ?? false, entry.GetBoolean(NachgemachtKursiv) ?? false,
                    (fonts.GetRaw(fontName) as PdfReference)?.Id);
            var style = new RichStyle
            {
                Family = entry.GetString(Familie)?.ToText() is { Length: > 0 and < 200 } family ? family : "Liberation Sans",
                Bold = entry.GetBoolean(Fett) ?? false,
                Italic = entry.GetBoolean(Kursiv) ?? false,
                Underline = entry.GetBoolean(Unterstrichen) ?? false,
                Strikeout = entry.GetBoolean(Durchgestrichen) ?? false,
                Size = Math.Clamp(entry.GetNumber(Groesse) ?? 12, 1, 500),
                Color = (int)((entry.GetInteger(Farbe) ?? 0) & 0xFFFFFF),
                Rise = Math.Clamp(entry.GetNumber(Hoch) ?? 0, -500, 500),
                Stretch = Math.Clamp(entry.GetNumber(Breite) ?? 1, 0.1, 10),
                CharSpacing = Math.Clamp(entry.GetNumber(Zeichenabstand) ?? 0, -100, 100),
                Original = original,
            };
            spans.Add(new RichSpan(entry.GetString(TextKey)?.ToText() ?? "", style));
        }
        if (spans.Count == 0) return null;
        var content = new RichText(spans);
        if (content.Length > budget) return null;
        var visible = string.Concat(content.Text.Where(c => !char.IsWhiteSpace(c)));
        return new Parsed(new RichTextElement
        {
            Rect = default,   // Place sets the position for each invocation of the form
            Content = content,
            Alignment = Enum.TryParse<BlockAlignment>(data.GetName(Ausrichtung)?.Value, out var alignment) && Enum.IsDefined(alignment) ? alignment : BlockAlignment.Left,
            LineGap = Math.Clamp(data.GetNumber(Abstand) ?? 1.15, 0.5, 5),
            FirstBaseline = data.GetNumber(Grundlinie) is { } baseline && double.IsFinite(baseline) ? Math.Clamp(baseline, 0, 1000) : null,
            HangingIndent = Math.Clamp(data.GetNumber(Einzug) ?? 0, -1000, 1000),
            TabStops = data.GetArray(Tabs) is { } tabs ? ReadTabStops(tabs, data.GetArray(TabArten)) : [],
        }, visible, content.Length);
    }

    /// <summary>Reads tab stops from the file: positions (clamped) and, if present, their alignment (unknown or justified: left).</summary>
    private static List<TabStop> ReadTabStops(PdfArray tabs, PdfArray? kinds)
    {
        var stops = new List<TabStop>();
        for (var i = 0; i < Math.Min(tabs.Count, 100); i++)
        {
            if (tabs.GetNumber(i) is not { } position || !double.IsFinite(position)) continue;
            var alignment = kinds?.Get(i) is PdfName name && Enum.TryParse<BlockAlignment>(name.Value, out var a) && a is BlockAlignment.Right or BlockAlignment.Center
                ? a
                : BlockAlignment.Left;
            stops.Add(new TabStop(Math.Clamp(position, 0, 10_000), alignment));
        }
        return [.. stops.OrderBy(s => s.Position)];
    }
}
