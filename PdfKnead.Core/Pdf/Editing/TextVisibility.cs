using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Which painted characters of a page nobody sees (KONZEPT.md, section 5.18; docs/FEHLERGEDAECHTNIS.md, No. 131): measured by drawing
/// (<see cref="IVisibilityProbe"/>), not guessed from colours and rectangles — white on white, the colour of the background, opacity 0,
/// under an image or any later content, outside a clip of any shape, painting nothing at all (colour space /None, clipped away). Only
/// characters for which there is any doubt are drawn: an opaque character in normal blending, inside rectangular clips, with nothing of
/// similar colour beneath and nothing over it, is visible without measuring — so a normal text page costs no drawing.
/// </summary>
public static class TextVisibility
{
    /// <summary>
    /// The probe used when none is passed — set once at program start by whoever has a renderer (program, command line, tests). Without
    /// a probe, doubtful characters count as visible and the result is marked incomplete.
    /// </summary>
    public static IVisibilityProbe? DefaultProbe { get; set; }

    /// <summary>Hidden characters by their number (<see cref="DisplayList.GlyphTags"/>).</summary>
    /// <param name="Hidden">Per character number: painted, but nobody sees it.</param>
    /// <param name="Complete">Every character was decided; false if the list is truncated, no probe was available or a work limit was reached.</param>
    internal sealed record Result(bool[] Hidden, bool Complete)
    {
        public bool IsHidden(int serial) => (uint)serial < (uint)Hidden.Length && Hidden[serial];

        /// <summary>Whether the character was part of the measured list (characters beyond a truncated list are not).</summary>
        public bool Knows(int serial) => (uint)serial < (uint)Hidden.Length;

        /// <summary>Characters in pattern cells that nobody sees (they have no number, <see cref="MeasureCells"/>).</summary>
        public IReadOnlySet<Content.CellGlyph> HiddenCells { get; init; } = new HashSet<Content.CellGlyph>();

        public bool IsHiddenCell(Content.CellGlyph glyph) => HiddenCells.Contains(glyph);

        /// <summary>Hidden in both (on the screen and when printed).</summary>
        public Result And(Result other)
        {
            var both = new bool[Hidden.Length];
            for (var i = 0; i < both.Length; i++) both[i] = Hidden[i] && other.IsHidden(i);
            return new Result(both, Complete && other.Complete) { HiddenCells = HiddenCells.Where(other.HiddenCells.Contains).ToHashSet() };
        }
    }

    /// <summary>
    /// Measures the characters of a page on the screen and, where printing can look different (layers, annotations), also as printed: a
    /// character counts as hidden only if nobody sees it in either — a cover that is not printed hides nothing on paper
    /// (docs/FEHLERGEDAECHTNIS.md, No. 145).
    /// </summary>
    /// <param name="only">If given, only characters touching one of these areas are measured; all others count as visible (the check
    /// after bars, which asks only about the characters at the bars).</param>
    internal static Result MeasurePage(PdfDocument doc, PdfPage page, IVisibilityProbe? probe, CancellationToken cancel, IReadOnlyList<RectD>? only = null)
    {
        var cells = CellCandidates(doc, page, cancel, only, out var allCollected);
        var screenList = Content.PageInterpreter.Build(doc, page, cancel, withoutOwnElements: true, tagGlyphs: true);
        var screen = MeasureCells(Measure(screenList, doc, page, probe, cancel, only), screenList, cells, allCollected, doc, page, probe, cancel, forPrint: false);
        if (!screen.Hidden.Any(h => h) && screen.HiddenCells.Count == 0 || !PrintsDifferently(doc, page)) return screen;
        var printedList = Content.PageInterpreter.Build(doc, page, cancel, withoutOwnElements: true, forPrint: true, tagGlyphs: true);
        var printed = MeasureCells(Measure(printedList, doc, page, probe, cancel, only), printedList, [.. cells.Where(c => screen.HiddenCells.Contains(c.Glyph))],
            true, doc, page, probe, cancel, forPrint: true);
        return screen.And(printed);
    }

    // ------------------------------------------------------------ Characters in pattern cells

    /// <summary>At most this many characters in pattern cells per page; beyond it they count as visible and the result as incomplete.</summary>
    private const int MaxCellGlyphs = 2000;

    /// <summary>At most this many rounds of the joint check for characters of pattern cells (each builds the page once more).</summary>
    private const int MaxCellRounds = 4;

    /// <summary>
    /// The painted characters in the cells of tiling patterns (KONZEPT.md, section 5.18; docs/FEHLERGEDAECHTNIS.md, No. 233): cells are drawn
    /// from a list of their own, so their characters have no number in the page's order. They are collected as the redaction sees them —
    /// a coloured pattern whose painted part lies in one tile — with where they lie on the page. Invisible ones (mode 3) and those on
    /// hidden layers are decided without drawing.
    /// </summary>
    private static List<(Content.CellGlyph Glyph, RectD Bounds)> CellCandidates(PdfDocument doc, PdfPage page, CancellationToken cancel,
        IReadOnlyList<RectD>? only, out bool complete)
    {
        var collector = new CellCollector(only);
        if (page.Resources?.ContainsKey(Objects.PdfName.Get("Pattern")) != true && !HasPatternsInForms(page))
        {
            complete = true;
            return [];
        }
        Content.PageInterpreter.Observe(doc, page, collector, cancel);
        complete = !collector.Truncated;
        return collector.Found;
    }

    // Patterns can also come from forms (their own resources): only a page without any /Pattern in its resource tree is skipped
    private static bool HasPatternsInForms(PdfPage page)
    {
        var seen = new HashSet<Objects.PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(Objects.PdfDictionary Resources, int Depth)>();
        if (page.Resources is { } top) stack.Push((top, 0));
        while (stack.TryPop(out var entry))
        {
            if (!seen.Add(entry.Resources) || seen.Count > 2000) return true;   // too many to look at: count as possible
            if (entry.Resources.ContainsKey(Objects.PdfName.Get("Pattern"))) return true;
            if (entry.Depth >= 12 || entry.Resources.GetDictionary(Objects.PdfName.Get("XObject")) is not { } xobjects) continue;
            foreach (var (key, _) in xobjects.Entries)
                if (xobjects.Get(key) is Objects.PdfStream { Dictionary: var form } && form.GetDictionary(Objects.PdfName.Resources) is { } inner)
                    stack.Push((inner, entry.Depth + 1));
        }
        return false;
    }

    /// <summary>
    /// Measures the characters of pattern cells as <see cref="IVisibilityProbe.Measure"/> measures the others: the page built without all
    /// of them is compared with the page as it is, and a character where nothing differs is hidden. Then the joint check: the page without
    /// only the hidden ones may differ nowhere — at the characters or wherever a pattern paints (the cell is shared by every place that
    /// paints with it); a hidden one at a difference counts as visible again, and if a difference lies at none of them, none is hidden.
    /// </summary>
    private static Result MeasureCells(Result result, DisplayList list, List<(Content.CellGlyph Glyph, RectD Bounds)> cells, bool collected,
        PdfDocument doc, PdfPage page, IVisibilityProbe? probe, CancellationToken cancel, bool forPrint)
    {
        var complete = result.Complete && collected;
        if (cells.Count == 0) return result with { Complete = complete };
        if ((probe ?? DefaultProbe) is not { } measure) return result with { Complete = false };
        DisplayList Without(IEnumerable<Content.CellGlyph> glyphs) =>
            Content.PageInterpreter.Build(doc, page, cancel, withoutOwnElements: true, forPrint: forPrint, tagGlyphs: true, withoutCellGlyphs: glyphs.ToHashSet());

        var hidden = new HashSet<Content.CellGlyph>();
        if (measure.Differences(list, Without(cells.Select(c => c.Glyph)), doc, page, [.. cells.Select(c => c.Bounds)], joint: false, cancel) is not { } own)
            return result with { Complete = false };
        for (var k = 0; k < cells.Count; k++)
            if (!own[k]) hidden.Add(cells[k].Glyph);

        var tiling = TilingAreas(list);
        for (var round = 0; hidden.Count > 0; round++)
        {
            var members = cells.Where(c => hidden.Contains(c.Glyph)).ToList();
            var areas = new List<RectD>(members.Select(c => c.Bounds));
            areas.AddRange(tiling);
            if (round >= MaxCellRounds || measure.Differences(list, Without(hidden), doc, page, areas, joint: true, cancel) is not { } joint)
            {
                hidden.Clear();
                complete = false;
                break;
            }
            if (!joint.Any(d => d)) break;
            var revealed = members.Where((_, k) => joint[k]).Select(c => c.Glyph).ToList();
            // a difference where a pattern paints but at none of the hidden characters: it cannot be told which one, so none counts
            if (revealed.Count == 0) hidden.Clear();
            else hidden.ExceptWith(revealed);
        }
        return result with { Complete = complete, HiddenCells = hidden };
    }

    /// <summary>Where tiling patterns paint on the page (bounds of the entries that fill or stroke with one).</summary>
    private static List<RectD> TilingAreas(DisplayList list)
    {
        var result = new List<RectD>();
        foreach (ref readonly var item in list.Items.Span)
        {
            if (item.Kind == DisplayItemKind.Image || item.Paint < 0) continue;
            var paint = list.Paints[item.Paint];
            if (paint.FillShading >= 0 && list.Shadings[paint.FillShading] is TilingFill || paint.StrokeShading >= 0 && list.Shadings[paint.StrokeShading] is TilingFill)
                result.Add(new RectD(item.Left, item.Top, item.Right, item.Bottom));
        }
        // very many: their bounds as one area (the probe keeps to its pixel budget)
        if (result.Count > 256)
        {
            var all = result[0];
            foreach (var r in result) all = all.Union(r);
            result = [all];
        }
        return result;
    }

    /// <summary>Collects the painted characters in pattern cells (the cell's own content, not its forms or Type 3 glyphs).</summary>
    private sealed class CellCollector(IReadOnlyList<RectD>? areas) : Content.IContentObserver
    {
        private readonly AreaIndex? only = areas is null ? null : new AreaIndex([.. areas]);
        private readonly Stack<Objects.PdfStream?> _open = new();   // per open form, mask or cell: the pattern if it is a cell

        public List<(Content.CellGlyph Glyph, RectD Bounds)> Found { get; } = [];

        public bool Truncated { get; private set; }

        public bool HiddenOnlyIfUnprinted => true;

        public bool WantsPatternCell(RectD painted) => only is null || Touches(only, painted);

        public void BeginPatternCell(int op, bool stroke, Objects.PdfStream pattern, Matrix cellToPage) => _open.Push(pattern);

        public void Glyph(int op, int element, int start, int length, RectD cell, RectD ink, Matrix cellToPage, double advance, double fontSize,
            double horizontalScale, bool vertical, Matrix textToPage, string? text, bool hidden, bool invisible)
        {
            if (hidden || invisible || !_open.TryPeek(out var pattern) || pattern is null) return;
            if (Found.Count >= MaxCellGlyphs)
            {
                Truncated = true;
                return;
            }
            Found.Add((new Content.CellGlyph(pattern, op, start), cellToPage.TransformBounds(cell.Union(ink))));
        }

        public void Path(int op, RectD bounds, Matrix ctm, bool hidden) { }

        public void Image(int op, Matrix imageToPage, Objects.PdfStream? stream, Content.InlineImage? inline, Objects.PdfDictionary? resources, bool hidden) { }

        public void Shading(int op, RectD bounds, Matrix ctm, bool hidden) { }

        public void BeginForm(int op, Objects.PdfStream form, Matrix formToPage, bool hidden) => _open.Push(null);

        public void BeginSoftMask(int op, Objects.PdfStream group, Matrix groupToPage) => _open.Push(null);

        public void EndForm() => _open.TryPop(out _);
    }

    /// <summary>The printed page can differ from the screen: the document has layers, or the page has annotations.</summary>
    private static bool PrintsDifferently(PdfDocument doc, PdfPage page) =>
        doc.Catalog?.ContainsKey(Objects.PdfName.Get("OCProperties")) == true || page.Dictionary.ContainsKey(Objects.PdfName.Get("Annots"));

    /// <summary>Work limit for the search for overlaps (hits looked at per page); after it, every remaining character is measured.</summary>
    private const long MaxHits = 20_000_000;

    /// <summary>Measures the characters of a page.</summary>
    /// <param name="list">Display list of the page, built with tagging.</param>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="probe">Measures by drawing; null = <see cref="DefaultProbe"/>.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>The hidden characters.</returns>
    /// <param name="only">If given, only characters touching one of these areas are measured; all others count as visible.</param>
    internal static Result Measure(DisplayList list, PdfDocument doc, PdfPage page, IVisibilityProbe? probe, CancellationToken cancel, IReadOnlyList<RectD>? only = null)
    {
        var count = list.GlyphCount;
        var hidden = new bool[count];
        if (list.GlyphTags is not { } tags) return new Result(hidden, false);
        var complete = !list.Truncated;
        foreach (var serial in list.SilentGlyphs)
            if ((uint)serial < (uint)count) hidden[serial] = true;
        if (count == 0) return new Result(hidden, complete);

        // Entries of each character: first, last, bounds, and whether any of them raises doubt by itself
        var items = list.Items.Span;
        var first = new int[count];
        var last = new int[count];
        var bounds = new RectD[count];
        var colour = new uint[count];
        var doubtful = new bool[count];
        Array.Fill(first, -1);
        var inGroup = GroupMembership(list.Groups, items.Length);
        var onlyIndex = only is null ? null : new AreaIndex([.. only]);
        for (var i = 0; i < items.Length && i < tags.Length; i++)
        {
            var t = tags[i];
            if ((uint)t >= (uint)count) continue;
            ref readonly var item = ref items[i];
            if (first[t] < 0)
            {
                first[t] = i;
                bounds[t] = new RectD(item.Left, item.Top, item.Right, item.Bottom);
                colour[t] = Colour(list, item);
            }
            else
            {
                var b = bounds[t];
                bounds[t] = new RectD(Math.Min(b.Left, item.Left), Math.Min(b.Top, item.Top), Math.Max(b.Right, item.Right), Math.Max(b.Bottom, item.Bottom));
            }
            last[t] = i;
            if (!doubtful[t] && Doubtful(list, item, i, inGroup)) doubtful[t] = true;
        }

        // The edge of the page clips like any clip: a character reaching beyond it may show only a sliver, so it is measured — otherwise
        // it counted as visible until something else made it doubtful, and the same character was judged differently before and after
        // redacting next to it (docs/FEHLERGEDAECHTNIS.md, No. 157). A character whose entries all lie beside the page paints nothing on
        // it, even if its cell reaches onto the page (No. 158) — its entries include the width of a stroke
        var box = Writing.HiddenContent.VisibleBox(page);
        for (var t = 0; t < count; t++)
        {
            if (first[t] < 0 || hidden[t]) continue;
            var b = bounds[t];
            var touches = b.Right > box.Left && b.Left < box.Right && b.Bottom > box.Bottom && b.Top < box.Top;
            if (!touches) hidden[t] = true;
            else if (!(b.Left >= box.Left && b.Right <= box.Right && b.Top >= box.Bottom && b.Bottom <= box.Top)) doubtful[t] = true;
        }

        // A character that adds to the clip is visible through what is painted inside it, even if its own fill is not: leaving out its
        // fill changes nothing, but removing it would take the clip — and all content shown through it — away (docs/FEHLERGEDAECHTNIS.md,
        // No. 143)
        var clipping = new bool[count];
        foreach (var serial in list.ClippingGlyphs)
            if ((uint)serial < (uint)count)
            {
                clipping[serial] = true;
                hidden[serial] = false;
            }
        var candidates = new List<VisibilityCandidate>();
        var budget = MaxHits;
        for (var t = 0; t < count; t++)
        {
            if (first[t] < 0 || hidden[t] || clipping[t]) continue;
            if (onlyIndex is not null && !Touches(onlyIndex, bounds[t])) continue;
            if (doubtful[t] || budget <= 0 || Overlapped(list, tags, t, first[t], colour[t], bounds[t], ref budget))
                candidates.Add(new VisibilityCandidate(t, bounds[t]));
        }
        if (candidates.Count == 0) return new Result(hidden, complete);
        if ((probe ?? DefaultProbe) is not { } measure) return new Result(hidden, false);
        var result = measure.Measure(list, doc, page, candidates, cancel);
        for (var k = 0; k < candidates.Count; k++)
            if (result.Hidden[k]) hidden[candidates[k].Serial] = true;
        return new Result(hidden, complete && result.Complete);
    }

    /// <summary>
    /// Whether a character lies at one of the areas: its drawn bounds widened by their own height, since the check before (in the content)
    /// counts its cell, which reaches below and above the drawn shape — a letter whose descender cell touched a bar, its shape not, was left
    /// out and stayed (issue20513). The areas in an index: the search for hidden text asks with one area per undecided character, and each
    /// character tested against all of them grew with the square of their number — 112 000 characters in one string took 36 s
    /// (docs/FEHLERGEDAECHTNIS.md, No. 914).
    /// </summary>
    private static bool Touches(AreaIndex areas, RectD bounds)
    {
        var margin = Math.Max(bounds.Height, bounds.Width);
        var near = bounds.Inflate(margin);
        return areas.Any(bounds, margin, a => a.Intersects(near));
    }

    /// <summary>Colour of an entry as ARGB (fill, or stroke for stroked text, or the colour of a stencil mask).</summary>
    private static uint Colour(DisplayList list, in DisplayItem item)
    {
        if (item.Kind == DisplayItemKind.Image) return item.Image >= 0 && list.Images[item.Image].IsStencil ? list.Images[item.Image].StencilColor : 0;
        var paint = list.Paints[item.Paint];
        return item.Kind == DisplayItemKind.Stroke ? paint.Stroke : paint.Fill;
    }

    /// <summary>
    /// The entry by itself gives reason to measure: beneath an opaque entry over the page, in a transparency group, translucent, not
    /// in normal blending, painted with a shading or pattern, (nearly) white, an image, or inside a clip that is not a rectangle
    /// containing it.
    /// </summary>
    private static bool Doubtful(DisplayList list, in DisplayItem item, int index, bool[] inGroup)
    {
        if (list.IsHidden(index) || inGroup[index]) return true;
        if (item.Kind == DisplayItemKind.Image)
        {
            if (item.Image < 0 || !list.Images[item.Image].IsStencil) return true;
            if (Faint(list.Images[item.Image].StencilColor)) return true;
        }
        else
        {
            var paint = list.Paints[item.Paint];
            if (paint.BlendMode != 0) return true;
            var fills = item.Kind is DisplayItemKind.Fill or DisplayItemKind.FillStroke;
            var strokes = item.Kind is DisplayItemKind.Stroke or DisplayItemKind.FillStroke;
            if (fills && (paint.FillShading >= 0 || Faint(paint.Fill))) return true;
            if (strokes && (paint.StrokeShading >= 0 || Faint(paint.Stroke))) return true;
        }
        for (var c = item.Clip; c > 0; c = list.Clips[c].Parent)
        {
            var clip = list.Clips[c];
            if (!clip.IsRectangle || !(clip.Bounds.Left <= item.Left && clip.Bounds.Top <= item.Top && clip.Bounds.Right >= item.Right && clip.Bounds.Bottom >= item.Bottom))
                return true;
        }
        return false;
    }

    /// <summary>Translucent or nearly white (at least 230 per channel): may vanish against the paper.</summary>
    private static bool Faint(uint argb) =>
        argb >> 24 != 255 || (argb >> 16 & 0xFF) >= 230 && (argb >> 8 & 0xFF) >= 230 && (argb & 0xFF) >= 230;

    /// <summary>
    /// Something else at the place of the character gives reason to measure: beneath it something that is not a plain colour clearly
    /// different from it (an image, a shading, a similar colour); over it later content — also later characters — covering most of it
    /// together; or another character on at least half of it (a character exactly over another).
    /// </summary>
    private static bool Overlapped(DisplayList list, int[] tags, int serial, int first, uint colour, RectD bounds, ref long budget)
    {
        var items = list.Items.Span;
        var area = Math.Max(bounds.Width * bounds.Height, 1e-6);
        var covered = 0.0;
        using var hits = list.QueryHits(bounds);
        // the work limit counts every hit the query collected, also those the loop no longer looks at: 159 000 characters drawn on top
        // of each other in a damaged file made each query collect 100 000 hits while the loop stopped after a few, and the search took
        // 35 s (docs/FEHLERGEDAECHTNIS.md, No. 187)
        budget -= hits.Count;
        if (budget < 0) return true;
        for (var h = 0; h < hits.Count; h++)
        {
            var j = hits[h];
            var tag = j < tags.Length ? tags[j] : -1;
            if (tag == serial) continue;
            ref readonly var other = ref items[j];
            var w = Math.Min(bounds.Right, other.Right) - Math.Max(bounds.Left, other.Left);
            var hgt = Math.Min(bounds.Bottom, other.Bottom) - Math.Max(bounds.Top, other.Top);
            if (w < 0 || hgt < 0) continue;
            var overlap = w * hgt;
            if (tag >= 0)
            {
                // another character on most of it, or later characters that together cover it (rows of small squares over a word)
                if (overlap >= 0.5 * area || j > first && (covered += overlap) >= 0.9 * area) return true;
            }
            else if (j < first)
            {
                if (Background(list, other, colour)) return true;
            }
            else if ((covered += overlap) >= 0.9 * area)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>An earlier entry beneath the character against which it may not stand out: anything but a plain colour clearly different from it.</summary>
    private static bool Background(DisplayList list, in DisplayItem item, uint colour)
    {
        if (item.Kind == DisplayItemKind.Image) return true;
        var paint = list.Paints[item.Paint];
        var fills = item.Kind is DisplayItemKind.Fill or DisplayItemKind.FillStroke;
        if (fills && paint.FillShading >= 0 || !fills && paint.StrokeShading >= 0) return true;
        var under = fills ? paint.Fill : paint.Stroke;
        var difference = Math.Max(Math.Abs((int)(under >> 16 & 0xFF) - (int)(colour >> 16 & 0xFF)),
            Math.Max(Math.Abs((int)(under >> 8 & 0xFF) - (int)(colour >> 8 & 0xFF)), Math.Abs((int)(under & 0xFF) - (int)(colour & 0xFF))));
        return difference < 64;
    }

    /// <summary>Is the entry inside a transparency group? Computed once for all entries (searching all groups per entry would be quadratic).</summary>
    internal static bool[] GroupMembership(GroupNode[] groups, int count)
    {
        var delta = new int[count + 1];
        foreach (var g in groups)
        {
            int start = Math.Clamp(g.Start, 0, count), end = Math.Clamp(g.End, 0, count);
            if (end <= start) continue;
            delta[start]++;
            delta[end]--;
        }
        var result = new bool[count];
        var depth = 0;
        for (var i = 0; i < count; i++)
        {
            depth += delta[i];
            result[i] = depth > 0;
        }
        return result;
    }
}
