using System.Globalization;
using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// What level a needs from the user (KONZEPT.md, section 5.21): the language of the document and, for each figure, an alternative text or
/// that it is only decoration. A figure without an answer keeps the copy from being written (unless all count as decoration).
/// </summary>
public sealed class PdfAAccessibility
{
    /// <summary>The language of the document as a language tag ("de", "en-GB"); empty if it is not known.</summary>
    public string Language { get; init; } = "";

    /// <summary>Per figure (<see cref="PdfAFigure.Key"/>): its alternative text, or null for decoration.</summary>
    public IReadOnlyDictionary<string, string?> Figures { get; init; } = new Dictionary<string, string?>();

    /// <summary>Every figure without an answer counts as decoration.</summary>
    public bool AllDecorative { get; init; }

    /// <summary>The answer for a figure: its text, null for decoration, or no answer.</summary>
    internal bool Answered(string key, out string? alt)
    {
        if (Figures.TryGetValue(key, out alt) && (alt is null || alt.Trim().Length > 0))
        {
            alt = alt?.Trim();
            return true;
        }
        alt = null;
        return AllDecorative;
    }
}

/// <summary>A figure that needs an alternative text in a tagged copy: where it is and how it is named.</summary>
/// <param name="Page">The page (from 0, in the written order).</param>
/// <param name="Key">Its name for the answers (<see cref="PdfAAccessibility.Figures"/>).</param>
/// <param name="Bounds">Where it is on the page (default user space); null if not known.</param>
public sealed record PdfAFigure(int Page, string Key, RectD? Bounds);

/// <summary>
/// Tagging for PDF/A level a (KONZEPT.md, section 5.21, „Stufe a“): a document without a structure tree gets one from the text blocks the
/// layout detection finds (<see cref="TextBlocks"/>) — headings by their size, list items by their markers, paragraphs —, in reading order
/// (columns by cuts through the white space between them), and the figures with their alternative texts. The content is marked where the
/// writer writes it anew (<see cref="ContentConformance.Rewrite"/>): the text operators of a block as marked content with its number (MCID),
/// a figure the same, a decoration as an artifact; a form that shows text counts with its whole content to the block of its first character.
/// </summary>
public static class PdfATagging
{
    /// <summary>An image smaller than this in both directions (points) is decoration (rules and dots drawn as images).</summary>
    private const double TinyImage = 3;

    /// <summary>One element of the new structure tree.</summary>
    internal sealed class Node(string type)
    {
        public string Type { get; set; } = type;
        public int Page { get; init; } = -1;
        public List<int> Mcids { get; } = [];
        public string? Alt { get; init; }
        public List<Node> Kids { get; } = [];
    }

    /// <summary>The marks of one page: what goes before an operator (by its number in the content as read) and how many EMC after it.</summary>
    internal sealed class PageMarks
    {
        public Dictionary<int, string> Before { get; } = [];
        public Dictionary<int, int> After { get; } = [];
        public int McidCount { get; set; }
    }

    /// <summary>The structure of a document: the element tree (a Document element) and the marks of each page (null for pages left untagged).</summary>
    internal sealed record Tagged(Node Document, IReadOnlyList<PageMarks?> Pages);

    /// <summary>A text block or figure on a page, with what it consists of.</summary>
    private sealed class Item
    {
        public required RectD Bounds { get; init; }
        public PageTextBlock? Block { get; init; }
        public int FigureOp { get; init; } = -1;
        public string? FigureKey { get; init; }
        public bool Decorative { get; init; }
        public string Type { get; set; } = "P";
        public Node? Node { get; set; }

        /// <summary>A heading's level (1 to 6); 0 for none.</summary>
        public int Level { get; set; }
    }

    /// <summary>The operators of a page as the interpreter reports them, by their number in the page's content (forms count with their Do).</summary>
    private sealed class Observer : IContentObserver
    {
        private int _ordinal, _formDepth, _formOp = -1;

        /// <summary>Per character (by its number in content order): the operator of the page that draws it.</summary>
        public List<int> GlyphOps { get; } = [];

        /// <summary>Operators of the page that draw characters of a hidden layer: no block has them, they stay as they are (not artifacts).</summary>
        public HashSet<int> HiddenTextOps { get; } = [];

        /// <summary>The page has invisible text (text rendering mode 3: the text recognition over a scan).</summary>
        public bool InvisibleText { get; private set; }

        /// <summary>Images drawn by operators of the page (an image in a form: its Do), with the area they cover.</summary>
        public Dictionary<int, RectD> Images { get; } = [];

        /// <summary>Operators of the page that draw a form with characters in it.</summary>
        public HashSet<int> FormsWithText { get; } = [];

        private int Top(int op) => _formDepth > 0 ? _formOp : op;

        public void Glyph(int op, int element, int start, int length, RectD cell, RectD ink, Matrix cellToPage, double advance, double fontSize,
            double horizontalScale, bool vertical, Matrix textToPage, string? text, bool hidden, bool invisible)
        {
            _ordinal++;
            GlyphOps.Add(Top(op));
            if (_formDepth > 0) FormsWithText.Add(_formOp);
            if (invisible) InvisibleText = true;
            if (hidden) HiddenTextOps.Add(Top(op));
        }

        public void Path(int op, RectD bounds, Matrix ctm, bool hidden) { }

        public void Image(int op, Matrix imageToPage, PdfStream? stream, InlineImage? inline, PdfDictionary? resources, bool hidden)
        {
            var area = imageToPage.TransformBounds(new RectD(0, 0, 1, 1));
            var top = Top(op);
            Images[top] = Images.TryGetValue(top, out var known) ? known.Union(area) : area;
        }

        public void Shading(int op, RectD bounds, Matrix ctm, bool hidden) { }

        public void BeginForm(int op, PdfStream form, Matrix formToPage, bool hidden)
        {
            if (_formDepth++ == 0) _formOp = op;
        }

        public void BeginSoftMask(int op, PdfStream group, Matrix groupToPage)
        {
            if (_formDepth++ == 0) _formOp = op;
        }

        public void EndForm() => _formDepth = Math.Max(0, _formDepth - 1);
    }

    /// <summary>The figures of the pages that need an answer (see <see cref="PdfAAccessibility"/>): in a tagged document its Figure elements
    /// without an alternative text, otherwise the images on the pages that are not decoration by themselves.</summary>
    /// <param name="pages">The pages as they are written.</param>
    /// <param name="cancel">Cancels the search.</param>
    /// <returns>The figures, page by page.</returns>
    public static IReadOnlyList<PdfAFigure> Figures(IReadOnlyList<(PdfDocument Doc, PdfPage Page)> pages, CancellationToken cancel = default)
    {
        var result = new List<PdfAFigure>();
        if (pages.Count == 0) return result;
        if (pages[0].Doc.Catalog?.GetDictionary(PdfName.Get("StructTreeRoot")) is { } root)
        {
            foreach (var (number, element) in UntextedFigures(pages[0].Doc, root))
            {
                var page = element.Get(PdfName.Get("Pg")) is PdfDictionary pg ? IndexOf(pages, pg) : -1;
                result.Add(new PdfAFigure(page, "e" + number.ToString(CultureInfo.InvariantCulture), FigureBox(element)));
            }
            return result;
        }
        for (var i = 0; i < pages.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var (doc, page) = pages[i];
            var observer = Observe(doc, page, cancel);
            foreach (var (op, area) in observer.Images.OrderBy(e => e.Key))
                if (!observer.FormsWithText.Contains(op) && !SelfDecorative(area, page, observer))
                    result.Add(new PdfAFigure(i, FigureKey(i, op), area));
        }
        return result;
    }

    private static string FigureKey(int page, int op) => $"p{page}:{op}";

    /// <summary>A small picture of a figure as the display draws its area of the page (for asking about it), at most
    /// <paramref name="maxSide"/> pixels wide or high; null without a rasterizer or area.</summary>
    /// <param name="doc">The document of the page.</param>
    /// <param name="page">The page.</param>
    /// <param name="area">The area in default user space.</param>
    /// <param name="maxSide">The longest side in pixels.</param>
    /// <param name="cancel">Cancels the drawing.</param>
    /// <returns>RGB pixels, or null.</returns>
    public static RasterArea? Thumbnail(PdfDocument doc, PdfPage page, RectD area, int maxSide, CancellationToken cancel = default)
    {
        var (width, height) = (Math.Abs(area.Width), Math.Abs(area.Height));
        if (PdfAFlattening.Rasterizer is not { } rasterizer || width < 0.5 || height < 0.5) return null;
        var box = RectD.FromLtrb(Math.Min(area.Left, area.Right), Math.Min(area.Top, area.Bottom), Math.Max(area.Left, area.Right), Math.Max(area.Top, area.Bottom));
        try
        {
            var list = PageInterpreter.Build(doc, page, cancel);
            return rasterizer.Render(list, doc, page, box, Math.Min(4, maxSide / Math.Max(width, height)), cancel);
        }
        catch (PdfException)
        {
            return null;
        }
    }

    private static int IndexOf(IReadOnlyList<(PdfDocument Doc, PdfPage Page)> pages, PdfDictionary page)
    {
        for (var i = 0; i < pages.Count; i++)
            if (ReferenceEquals(pages[i].Page.Dictionary, page)) return i;
        return -1;
    }

    /// <summary>The Figure elements of a structure tree (by object number) without /Alt or /ActualText.</summary>
    internal static IEnumerable<(int Number, PdfDictionary Element)> UntextedFigures(PdfDocument doc, PdfDictionary root)
    {
        var seen = new HashSet<int>();
        var budget = 500_000;
        var stack = new Stack<PdfObject?>();
        stack.Push(root.GetRaw(PdfName.Get("K")));
        while (stack.TryPop(out var k) && --budget > 0)
        {
            switch (k)
            {
                case PdfArray array:
                    for (var i = array.Count - 1; i >= 0; i--) stack.Push(array[i]);
                    break;
                case PdfReference r when seen.Add(r.Id.Number) && doc.GetObject(r.Id.Number) is PdfDictionary element && !element.IsType(PdfName.Get("MCR")) &&
                                         !element.IsType(PdfName.Get("OBJR")):
                    if (FigureType(element, root) && !element.ContainsKey(PdfName.Get("Alt")) && !element.ContainsKey(PdfName.Get("ActualText")))
                        yield return (r.Id.Number, element);
                    stack.Push(element.GetRaw(PdfName.Get("K")));
                    break;
            }
        }
    }

    /// <summary>An element whose standard type is Figure, directly or through the role map (<see cref="Accessibility.StandardStructure.Resolve"/>).</summary>
    private static bool FigureType(PdfDictionary element, PdfDictionary root) => Accessibility.StandardStructure.Resolve(element, root)?.Name == "Figure";

    /// <summary>The bounding box an element gives in its layout attributes (/A /BBox); null if none.</summary>
    private static RectD? FigureBox(PdfDictionary element)
    {
        foreach (var attributes in element.Get(PdfName.Get("A")) switch
                 {
                     PdfDictionary one => [one],
                     PdfArray many => Enumerable.Range(0, many.Count).Select(many.Get).OfType<PdfDictionary>(),
                     _ => [],
                 })
            if (PdfRectangle.From(attributes.Get(PdfName.Get("BBox"))) is { } box)
                return RectD.FromLtrb(box.Left, box.Bottom, box.Right, box.Top);
        return null;
    }

    private static Observer Observe(PdfDocument doc, PdfPage page, CancellationToken cancel)
    {
        var observer = new Observer();
        try
        {
            PageInterpreter.Observe(doc, page, observer, cancel);
        }
        catch (PdfException)
        {
        }
        return observer;
    }

    /// <summary>
    /// An image that is decoration by itself: smaller than <see cref="TinyImage"/> both ways, or the scan of a page whose text recognition
    /// lies over it (at least half the page, with invisible text on the page — the text is what the page says).
    /// </summary>
    private static bool SelfDecorative(RectD area, PdfPage page, Observer observer) =>
        area.Width < TinyImage && area.Height < TinyImage ||
        observer.InvisibleText && area.Width * area.Height >= 0.5 * Math.Abs(page.CropBox.Width * page.CropBox.Height);

    /// <summary>
    /// The structure of an untagged document: text blocks and figures per page in reading order, their types, and the marks for each page's
    /// content (see the class). Figures without an answer are left untagged (the export does not write such a copy).
    /// </summary>
    /// <param name="pages">The pages as they are written.</param>
    /// <param name="answers">The answers for the figures.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>The structure.</returns>
    internal static Tagged Build(IReadOnlyList<(PdfDocument Doc, PdfPage Page)> pages, PdfAAccessibility answers, CancellationToken cancel = default)
    {
        var perPage = new List<(List<Item> Items, Observer Observer, bool TextKnown)>();
        // the size of the body text: the most characters are set in it
        var sizes = new Dictionary<double, int>();
        for (var i = 0; i < pages.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var (doc, page) = pages[i];
            var observer = Observe(doc, page, cancel);
            IReadOnlyList<PageTextBlock> blocks;
            // whether the blocks cover all text of the page: not when reading failed or the page has more characters than the detection takes
            // (then text of no block is real content all the same and stays as it is — never an artifact hidden from screen readers)
            var textKnown = observer.GlyphOps.Count < TextBlocks.MaxGlyphs;
            try
            {
                blocks = TextBlocks.Find(doc, page, cancel);
            }
            catch (PdfException)
            {
                blocks = [];
                textKnown = false;
            }
            var items = new List<Item>();
            foreach (var block in blocks)
            {
                items.Add(new Item { Bounds = block.Bounds, Block = block });
                foreach (var line in block.Lines)
                {
                    var size = Math.Round(line.Size * 2) / 2;
                    sizes[size] = sizes.GetValueOrDefault(size) + line.Text.Length;
                }
            }
            foreach (var (op, area) in observer.Images.OrderBy(e => e.Key))
            {
                if (observer.FormsWithText.Contains(op)) continue;
                var key = FigureKey(i, op);
                var decorative = SelfDecorative(area, page, observer) || answers.Answered(key, out var alt) && alt is null;
                items.Add(new Item { Bounds = area, FigureOp = op, FigureKey = key, Decorative = decorative, Type = decorative ? "Artifact" : "Figure" });
            }
            perPage.Add((ReadingOrder(items), observer, textKnown));
        }
        var body = sizes.Count == 0 ? 0 : sizes.MaxBy(e => e.Value).Key;
        // headings: short blocks clearly larger than the body text (or bold and a little larger); their sizes from large to small are levels 1 to 6
        var headingSizes = new SortedSet<double>(Comparer<double>.Create((a, b) => b.CompareTo(a)));
        foreach (var (items, _, _) in perPage)
            foreach (var item in items)
                if (item.Block is { } block && Heading(block, body))
                {
                    item.Type = "H";
                    headingSizes.Add(Math.Round(block.Lines.Max(l => l.Size) * 2) / 2);
                }
                else if (item.Block is { } listed && ListItem(listed)) item.Type = "LI";
        var levels = headingSizes.Take(6).Select((s, n) => (s, n)).ToDictionary(e => e.s, e => e.n + 1);
        // the levels in reading order (docs/ABLAUFPLAN.md, 6.6 e; docs/FEHLERGEDAECHTNIS.md, No. 652): a heading numbered „4.5.6“ has the level its
        // numbering shows (ISO 14289-2, 8.2.5.12); one without starts at H1 and goes down one level at most at a time (ISO 14289-1, 7.4.2) —
        // before the sizes alone gave the levels, and a document whose first heading had the second size began with H2
        var previous = 0;
        foreach (var (items, _, _) in perPage)
            foreach (var item in items)
            {
                if (item.Type != "H" || !levels.TryGetValue(Math.Round(item.Block!.Lines.Max(l => l.Size) * 2) / 2, out var bySize)) continue;
                item.Level = NumberedLevel(item.Block.Text) ?? Math.Clamp(bySize, 1, previous + 1);
                previous = item.Level;
            }
        var document = new Node("Document");
        var marks = new List<PageMarks?>();
        for (var i = 0; i < pages.Count; i++)
        {
            var (items, observer, textKnown) = perPage[i];
            var mark = new PageMarks();
            // the operators of each item: a block by its characters (the first block a character belongs to), a figure by itself
            var opItem = new Dictionary<int, Item>();
            foreach (var item in items)
            {
                if (item.Block is { } block)
                {
                    foreach (var ordinal in block.Glyphs)
                        if (ordinal >= 0 && ordinal < observer.GlyphOps.Count) opItem.TryAdd(observer.GlyphOps[ordinal], item);
                }
                else if (item.FigureOp >= 0 && (item.Decorative || answers.Answered(item.FigureKey!, out _))) opItem.TryAdd(item.FigureOp, item);
            }
            foreach (var item in items)
                item.Node = item.Type switch
                {
                    "Artifact" => null,
                    "H" => new Node(item.Level > 0 ? "H" + item.Level.ToString(System.Globalization.CultureInfo.InvariantCulture) : "P") { Page = i },
                    "Figure" => new Node("Figure") { Page = i, Alt = answers.Answered(item.FigureKey!, out var alt) ? alt : null },
                    "LI" => new Node("LBody") { Page = i },
                    _ => new Node("P") { Page = i },
                };
            // a picture without an answer is no artifact: it stays as it is (the check asks for it); text of a hidden layer as well (shown, it
            // is real content)
            var unanswered = items.Where(it => it.FigureOp >= 0 && !opItem.ContainsKey(it.FigureOp)).Select(it => it.FigureOp).ToHashSet();
            unanswered.UnionWith(observer.HiddenTextOps);
            Mark(pages[i].Doc, pages[i].Page, opItem, mark, unanswered, textKnown);
            // in reading order, list items gathered in lists (L > LI > LBody); items without content stay out
            Node? list = null;
            foreach (var item in items)
            {
                if (item.Node is not { Mcids.Count: > 0 } node) continue;
                if (item.Type == "LI")
                {
                    if (list is null)
                    {
                        list = new Node("L") { Page = i };
                        document.Kids.Add(list);
                    }
                    var entry = new Node("LI") { Page = i };
                    entry.Kids.Add(node);
                    list.Kids.Add(entry);
                    continue;
                }
                list = null;
                document.Kids.Add(node);
            }
            marks.Add(mark.McidCount > 0 || mark.Before.Count > 0 ? mark : null);
        }
        return new Tagged(document, marks);
    }

    /// <summary>A heading: at most three lines, at most 200 characters, clearly larger than the body text — or bold, a little larger, one or two lines.</summary>
    private static bool Heading(PageTextBlock block, double body)
    {
        if (body <= 0 || block.Lines.Count > 3 || block.Text.Length > 200) return false;
        var size = block.Lines.Max(l => l.Size);
        var bold = block.Lines.SelectMany(l => l.Runs).Where(r => r.Format.Bold).Sum(r => r.Text.Length) * 2 > block.Text.Length;
        return size >= body * 1.2 || bold && size >= body * 1.05 && block.Lines.Count <= 2 && !block.Text.TrimEnd().EndsWith('.');
    }

    /// <summary>
    /// The level a heading's numbering shows: „4“ → 1, „4.5“ → 2, „4.5.6.“ → 3 (at most 6; each part at most 99, so a year is no numbering);
    /// null if it starts with none.
    /// </summary>
    private static int? NumberedLevel(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text.TrimStart(), @"^(\d{1,2})((?:\.\d{1,2})*)\.?(?=\s)");
        return match.Success ? Math.Min(6, 1 + match.Groups[2].Value.Count(c => c == '.')) : null;
    }

    /// <summary>A list item: the block starts with a list marker followed by a space or gap.</summary>
    private static bool ListItem(PageTextBlock block)
    {
        var text = block.Text.TrimStart();
        var end = text.IndexOfAny([' ', '\t', ' ']);
        return end > 0 && end <= 8 && TextBlocks.IsMarker(text[..end]);
    }

    /// <summary>
    /// Reading order by cuts through white space (XY cut): where a horizontal gap crosses all items, the upper ones come first; otherwise where a
    /// vertical gap does, the left ones; otherwise from top to bottom.
    /// </summary>
    private static List<Item> ReadingOrder(List<Item> items)
    {
        if (items.Count <= 1) return items;
        // (page space: y upwards — the upper edge is the larger y)
        static double Upper(Item i) => Math.Max(i.Bounds.Top, i.Bounds.Bottom);
        static double Lower(Item i) => Math.Min(i.Bounds.Top, i.Bounds.Bottom);
        // horizontal gap: the items above it, then those below
        var byTop = items.OrderByDescending(Upper).ToList();
        var lowest = Lower(byTop[0]);
        for (var k = 1; k < byTop.Count; k++)
        {
            if (Upper(byTop[k]) < lowest - 0.5)
                return [.. ReadingOrder(byTop[..k]), .. ReadingOrder(byTop[k..])];
            lowest = Math.Min(lowest, Lower(byTop[k]));
        }
        // vertical gap: the items left of it, then those right of it
        var byLeft = items.OrderBy(i => i.Bounds.Left).ToList();
        var rightmost = byLeft[0].Bounds.Right;
        for (var k = 1; k < byLeft.Count; k++)
        {
            if (byLeft[k].Bounds.Left > rightmost + 0.5)
                return [.. ReadingOrder(byLeft[..k]), .. ReadingOrder(byLeft[k..])];
            rightmost = Math.Max(rightmost, byLeft[k].Bounds.Right);
        }
        return [.. items.OrderByDescending(Upper).ThenBy(i => i.Bounds.Left)];
    }

    /// <summary>The operators that show text (and may stand in a run of one block within a text object).</summary>
    private static readonly HashSet<string> Shows = new(["Tj", "TJ", "'", "\""], StringComparer.Ordinal);

    /// <summary>Operators that change the text state or position and may stand inside a run.</summary>
    private static readonly HashSet<string> InRun = new(["Td", "TD", "Tm", "T*", "Tc", "Tw", "Tz", "TL", "Tf", "Tr", "Ts", "g", "G", "rg", "RG", "k", "K",
        "cs", "CS", "sc", "scn", "SC", "SCN", "d0", "d1"], StringComparer.Ordinal);

    /// <summary>
    /// The marks of a page: runs of text operators of one item within a text object (no q, Q, BT, ET, marked content or other operator between
    /// them), a figure or a form with text by itself; nothing inside an artifact the content marks already. Each run gets an MCID, which its node
    /// lists. What else paints — a path from its first construction operator to its painting operator, a shading, an image or form that is no
    /// item, text of no block — becomes an artifact of its own (docs/ABLAUFPLAN.md, 6.6 e; ISO 14289-1, 7.1): before it stayed real content
    /// without structure (allowed for PDF/A level a, against PDF/UA). Never across q, Q, BT, ET or marked content: a path lies within one.
    /// </summary>
    private static void Mark(PdfDocument doc, PdfPage page, Dictionary<int, Item> opItem, PageMarks marks, IReadOnlySet<int>? untouched = null,
        bool textKnown = true)
    {
        byte[] data;
        try
        {
            data = PageContentOf(doc, page);
        }
        catch (PdfException)
        {
            return;
        }
        var parser = new ContentParser(data);
        var index = -1;
        Item? open = null;
        var last = -1;
        var artifact = 0;
        var marked = new Stack<bool>();
        var pathStart = -1;   // the first construction operator of the path being built
        // a path or shading: layout (ISO 32000-2, 14.8.2.2.2); an image, form or text of no item: an artifact without a type
        void Artifact(int from, int to, bool layout = false)
        {
            marks.Before[from] = marks.Before.GetValueOrDefault(from) + (layout ? "/Artifact <</Type /Layout>> BDC\n" : "/Artifact BMC\n");
            marks.After[to] = marks.After.GetValueOrDefault(to) + 1;
        }
        void Close()
        {
            if (open is null) return;
            marks.After[last] = marks.After.GetValueOrDefault(last) + 1;
            open = null;
        }
        void Begin(int at, Item item)
        {
            if (item.Node is null)
            {
                marks.Before[at] = "/Artifact BMC\n";
                return;
            }
            var mcid = marks.McidCount++;
            item.Node.Mcids.Add(mcid);
            marks.Before[at] = $"/{item.Node.Type} <</MCID {mcid}>> BDC\n";
        }
        string? op;
        while ((op = parser.Next()) is not null)
        {
            index++;
            switch (op)
            {
                case "BMC" or "BDC":
                    Close();
                    var isArtifact = parser.Operands.Count > 0 && parser.Operands[0].Name?.Value == "Artifact";
                    marked.Push(isArtifact);
                    if (isArtifact) artifact++;
                    continue;
                case "EMC":
                    Close();
                    if (marked.TryPop(out var wasArtifact) && wasArtifact) artifact--;
                    continue;
            }
            var item = artifact == 0 ? opItem.GetValueOrDefault(index) : null;
            if (Shows.Contains(op) && item is not null)
            {
                if (!ReferenceEquals(open, item))
                {
                    Close();
                    Begin(index, item);
                    open = item.Node is null ? null : item;
                    if (open is null) marks.After[index] = marks.After.GetValueOrDefault(index) + 1;
                }
                last = index;
                continue;
            }
            if (open is not null && InRun.Contains(op))
            {
                last = index;
                continue;
            }
            Close();
            if (item is not null && op is "Do" or "BI")
            {
                Begin(index, item);
                marks.After[index] = marks.After.GetValueOrDefault(index) + 1;
                continue;
            }
            switch (op)
            {
                case "m" or "l" or "c" or "v" or "y" or "h" or "re":
                    if (pathStart < 0) pathStart = index;
                    break;
                case "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "S" or "s":
                    if (pathStart >= 0 && artifact == 0) Artifact(pathStart, index, layout: true);
                    pathStart = -1;
                    break;
                case "n":
                    pathStart = -1;   // a clip only: it paints nothing
                    break;
                case "W" or "W*":
                    break;
                default:
                    pathStart = -1;
                    if (artifact == 0 && item is null && untouched?.Contains(index) != true && (op is "sh" or "Do" or "BI" || textKnown && Shows.Contains(op)))
                        Artifact(index, index, layout: op == "sh");
                    break;
            }
        }
        Close();
    }

    /// <summary>The page's content as the interpreter reads it (parts joined with a line end between them).</summary>
    private static byte[] PageContentOf(PdfDocument doc, PdfPage page)
    {
        switch (page.Dictionary.Get(PdfName.Contents))
        {
            case PdfStream single:
                return doc.DecodeStream(single).Data;
            case PdfArray parts:
                var all = new MemoryStream();
                for (var i = 0; i < parts.Count && i < 10_000; i++)
                {
                    if (parts.Get(i) is not PdfStream part) continue;
                    try
                    {
                        all.Write(doc.DecodeStream(part).Data);
                    }
                    catch (PdfException)
                    {
                    }
                    all.WriteByte((byte)'\n');
                }
                return all.ToArray();
            default:
                return [];
        }
    }

    /// <summary>
    /// The language of a document's text, proposed for level a: the language whose most frequent short words appear most often in the first
    /// pages (German, English, French, Spanish, Italian, Dutch, Portuguese, Polish, Swedish, Danish); empty if none stands out.
    /// </summary>
    /// <param name="pages">The pages.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>A language tag such as "de", or empty.</returns>
    public static string ProposeLanguage(IReadOnlyList<(PdfDocument Doc, PdfPage Page)> pages, CancellationToken cancel = default)
    {
        if (pages.Count > 0 && pages[0].Doc.Catalog?.GetString(PdfName.Get("Lang"))?.ToText() is { Length: > 0 } given &&
            PdfACheck.LanguageTag(given.Trim(), 2))
            return given.Trim();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var words = 0;
        foreach (var (doc, page) in pages.Take(5))
        {
            cancel.ThrowIfCancellationRequested();
            string text;
            try
            {
                text = string.Join(' ', TextBlocks.Find(doc, page, cancel).Select(b => b.Text));
            }
            catch (PdfException)
            {
                continue;
            }
            foreach (var word in text.ToLowerInvariant().Split([' ', '\n', '\t', ',', '.', ';', ':', '!', '?', '(', ')', '"', '„', '“', '«', '»'],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                words++;
                foreach (var (language, list) in Stopwords)
                    if (list.Contains(word)) counts[language] = counts.GetValueOrDefault(language) + 1;
            }
            if (words > 5000) break;
        }
        if (counts.Count == 0) return "";
        var best = counts.MaxBy(e => e.Value);
        var second = counts.Where(e => e.Key != best.Key).Select(e => e.Value).DefaultIfEmpty(0).Max();
        return best.Value >= 3 && best.Value > second * 1.5 ? best.Key : "";
    }

    private static readonly (string Language, HashSet<string> Words)[] Stopwords =
    [
        ("de", Set("der die das und ist nicht ein eine zu den von mit sich des auf für im dem auch es an werden aus er hat dass sie nach wird bei einer um noch wie einem über einen so zum war haben nur oder aber vor zur bis mehr durch man sind")),
        ("en", Set("the and of to in is that it for was on are as with his they be at one have this from by not but what all were when we there can an your which their if do will each about how up out them")),
        ("fr", Set("le la les de des et est un une du en que qui dans pour pas sur au avec ce il elle sont par plus ne se aux ou son sa mais nous vous leur")),
        ("es", Set("el la los las de y que en un una es por con para del al lo como más pero sus su se no le ya o este sí porque esta entre cuando muy sin")),
        ("it", Set("il la le di e che è un una per non in del della con sono si da al dei gli lo ma come anche più questo nel alla delle")),
        ("nl", Set("de het een en van is dat in te op zijn voor met niet aan er die maar om ook als dan bij nog wel naar uit door over")),
        ("pt", Set("o a os as de e que em um uma é do da para com não por se na no dos das mais como mas ao ele ela seu sua")),
        ("pl", Set("i w na z się nie do że jest to o jak ale po co tak za od jego przez czy są jej tylko")),
        ("sv", Set("och i att det som en på är av för med till den har de inte om ett men var jag sig från vi så")),
        ("da", Set("og i at det som en på er af for med til den har de ikke om et men var jeg sig fra vi så")),
    ];

    private static HashSet<string> Set(string words) => new(words.Split(' '), StringComparer.Ordinal);

    /// <summary>A language tag as level a asks (<see cref="PdfACheck.LanguageTag"/>), made of what was given: „de_DE“ becomes „de-DE“; null if
    /// nothing usable remains.</summary>
    internal static string? CleanLanguage(string? given, int part)
    {
        var text = (given ?? "").Replace("\0", "", StringComparison.Ordinal).Trim().Replace('_', '-');
        return text.Length > 0 && PdfACheck.LanguageTag(text, part) ? text : null;
    }

    /// <summary>Text of a structure element as a PDF text string.</summary>
    internal static PdfString Text(string text) => new(PdfTextEncoding.Encode(text));

    /// <summary>For tests and messages: the type names of a tree, depth first.</summary>
    internal static string Outline(Node node)
    {
        var sb = new StringBuilder();
        void Walk(Node n, int depth)
        {
            sb.Append(' ', depth * 2).Append(n.Type);
            if (n.Mcids.Count > 0) sb.Append(" [").Append(string.Join(',', n.Mcids)).Append(']');
            sb.Append('\n');
            foreach (var kid in n.Kids) Walk(kid, depth + 1);
        }
        Walk(node, 0);
        return sb.ToString();
    }
}
