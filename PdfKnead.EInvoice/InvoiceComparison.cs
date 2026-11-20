using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.EInvoice;

/// <summary>What the comparison says about a value (KONZEPT.md, section 5.24, stage 3).</summary>
public enum InvoiceMatchStatus
{
    /// <summary>Found in visible text (page and places given).</summary>
    Found,

    /// <summary>Found only rounded to two decimals (a price with more decimals in the XML).</summary>
    Rounded,

    /// <summary>Some of the words of a text found, not the whole.</summary>
    Partial,

    /// <summary>Not found in visible text — please check.</summary>
    NotFound,

    /// <summary>Cannot be checked in the PDF: a code without a form on the page, an attachment, a file without readable visible text.</summary>
    NotCheckable,

    /// <summary>Not checked: a limit of the comparison was reached before it.</summary>
    NotChecked,
}

/// <summary>Where a value was found: the page (from 1), the places on it (page space, one quadrilateral per line) and the form found.</summary>
public sealed record InvoiceMatchPlace(int Page, IReadOnlyList<TextQuad> Quads, string Form);

/// <summary>
/// The result for one value: its status, the place of the first visible find, a note (why it cannot be checked, „3 von 5 Wörtern“, „nur in
/// unsichtbarem oder verdecktem Text“ …) and whether the visibility of the page was measured completely; with <paramref name="Places"/> every
/// place found the same way (null: only <paramref name="Place"/>).
/// </summary>
public sealed record InvoiceMatch(InvoiceField Field, InvoiceMatchStatus Status, InvoiceMatchPlace? Place, string? Note, bool VisibilityChecked,
    IReadOnlyList<InvoiceMatchPlace>? Places = null)
{
    /// <summary>
    /// Not found, but a short number (a weak value) stands visibly on the page — only not beside a value of its group (its context): it may be
    /// this value or another. The box „Zahlung“ does not warn of it then (the amount may well be printed; analysis of the collections,
    /// 15.11.2026: „1,00“ in the totals of a small invoice).
    /// </summary>
    public bool VisibleOutOfContext { get; init; }

    /// <summary>
    /// With <see cref="VisibleOutOfContext"/>: where the short number stands visibly (at most <see cref="MaxPlaces"/>) — the box „Zahlung“ marks
    /// them when its amount is clicked, so that the buyer sees whether one of them is the amount due (decided on 15.11.2026). Not a place
    /// of the field: the status stays „not found“.
    /// </summary>
    public IReadOnlyList<InvoiceMatchPlace> OutOfContextPlaces { get; init; } = [];

    /// <summary>The most places of one value kept (a value on every page of a long document would fill the list).</summary>
    public const int MaxPlaces = 50;

    /// <summary>
    /// Every visible place of the value found the same way, the first being <see cref="Place"/> (at most <see cref="MaxPlaces"/>). Which of
    /// them belongs to the field the comparison does not say — it searches, it does not extract (Bauvorgabe P2); with several places the user
    /// sees them all (decided on 15.11.2026: 275,00 twice on a page).
    /// </summary>
    public IReadOnlyList<InvoiceMatchPlace> AllPlaces => Places ?? (Place is null ? [] : [Place]);
}

/// <summary>
/// The comparison of an invoice with the visible text of its PDF: one result per value, in the order of the readable form; if it was
/// cancelled, after how many pages (the values not found are then „nicht geprüft“).
/// </summary>
public sealed record InvoiceComparisonResult(IReadOnlyList<InvoiceMatch> Matches, int PagesChecked, int Pages, bool HasVisibleText, int? AbortedAfter = null)
{
    /// <summary>How many values have that status.</summary>
    public int Count(InvoiceMatchStatus status) => Matches.Count(m => m.Status == status);
}

/// <summary>
/// Stage 3 (KONZEPT.md, section 5.24, „Weg für Stufe 3“): every value of the readable invoice is searched in its usual forms in the text the
/// PDF actually shows — searched, never read out of the page (P2). All forms of all values go through each page in one pass, hits are checked
/// for word boundaries, for visibility (drawn, not on a hidden layer, not beside the page, not tiny, not covered or in the background colour —
/// the same test as „Verborgenes schwärzen“) and, for numbers, for the sign. Weak values (one or two digits, zero, a text of two characters)
/// count only on the height of a found strong value of their group. Limits keep the work bounded; whatever a limit leaves out is „nicht
/// geprüft“, never silently missing.
/// </summary>
public static class InvoiceComparison
{
    /// <summary>At most so many values are compared; the rest are not checked.</summary>
    internal const int MaxValues = 5000;

    /// <summary>At most so many characters of all forms together (the size of the search automaton).</summary>
    internal const long MaxFormCharacters = 4_000_000;

    /// <summary>At most so many pages are searched.</summary>
    internal const int MaxPages = 2000;

    /// <summary>At most so many hits are kept per value.</summary>
    internal const int MaxHitsPerValue = 64;

    /// <summary>At most so many hits per page are looked at (a page full of one short number).</summary>
    internal const int MaxHitsPerPage = 200_000;

    private sealed record Hit(int Page, TextRange Range, InvoiceForm Form, bool Visible, bool Measured, bool SignFits, double CenterY, double Height,
        PageText Text);

    /// <summary>
    /// Amounts a PDF shows with either sign: allowances (positive in the XML, EN 16931, but usually printed as a deduction „-1,00“), the paid
    /// amount and the rounding.
    /// </summary>
    private static readonly HashSet<string> EitherSign = new(StringComparer.Ordinal) { "BT-92", "BT-107", "BT-113", "BT-114", "BT-136", "BT-147" };

    /// <summary>Compares the invoice with the visible text of the document.</summary>
    public static InvoiceComparisonResult Compare(PdfDocument doc, InvoiceData invoice, CancellationToken cancel = default)
    {
        // the groups of each value (for the context of weak values)
        var groupOf = new Dictionary<InvoiceField, InvoiceGroup>(ReferenceEqualityComparer.Instance);
        var parentOf = new Dictionary<InvoiceGroup, InvoiceGroup>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<InvoiceGroup>();
        pending.Push(invoice.Root);
        while (pending.Count > 0)
        {
            var g = pending.Pop();
            foreach (var f in g.Fields) groupOf[f] = g;
            foreach (var c in g.Groups)
            {
                parentOf[c] = g;
                pending.Push(c);
            }
        }
        var fields = invoice.Root.AllFields().ToList();
        var credit = invoice.Text("BT-3")?.Trim() is "381" or "384";
        var searches = new List<InvoiceValueSearch?>(fields.Count);
        // two sets of forms: in the prepared text, and texts of several words without their spaces in the text without spaces
        var patterns = new InvoicePatterns();
        var compactPatterns = new InvoicePatterns();
        var owners = new List<List<(int Search, int Form)>>();
        var compactOwners = new List<List<(int Search, int Form)>>();
        for (var i = 0; i < fields.Count; i++)
        {
            if (i >= MaxValues || patterns.Characters + compactPatterns.Characters > MaxFormCharacters)
            {
                searches.Add(null);   // not checked: a limit
                continue;
            }
            var search = InvoiceForms.Of(fields[i]);
            searches.Add(search);
            for (var f = 0; f < search.Forms.Count; f++)
            {
                var (set, list) = search.Forms[f].Compact ? (compactPatterns, compactOwners) : (patterns, owners);
                var id = set.Add(search.Forms[f].Text);
                while (list.Count <= id) list.Add([]);
                list[id].Add((i, f));
            }
        }
        patterns.Build();
        compactPatterns.Build();
        var hits = new List<Hit>?[fields.Count];
        var pagesChecked = 0;
        var visibleText = false;
        var pageCount = Math.Min(doc.PageCount, MaxPages);
        // a cancellation (the time limit of the user interface) ends the search, the results so far stay: what was found is found, the rest
        // is „nicht geprüft“
        int? aborted = null;
        for (var p = 0; p < pageCount; p++)
        {
            if (cancel.IsCancellationRequested)
            {
                aborted = p;
                break;
            }
            try
            {
                var page = doc.Pages[p];
                TextGlyph[] glyphs;
                try
                {
                    glyphs = PageInterpreter.BuildText(doc, page, cancel);
                }
                catch (Exception e) when (e is PdfException or IOException or InvalidDataException)
                {
                    continue;   // a damaged page: its values are simply not found there
                }
                pagesChecked++;
                if (glyphs.Length == 0) continue;
                if (glyphs.Any(g => g.Visible)) visibleText = true;
                var text = PageText.Create(glyphs);
                var search = InvoiceSearchText.Of(text.Text);
                var candidates = new List<(bool Compact, int Pattern, int Start)>();
                patterns.Search(search.Text, (id, start) =>
                {
                    if (candidates.Count < MaxHitsPerPage && Bounded(search.Text, start, patterns[id].Length, space => NarrowSpace(text, search, space)))
                        candidates.Add((false, id, start));
                }, cancel);
                compactPatterns.Search(search.Compact, (id, start) =>
                {
                    // the boundaries of a hit without spaces are checked in the page text itself, where the spaces are
                    if (candidates.Count < MaxHitsPerPage && BoundedInPage(text.Text, search.CompactRange(start, compactPatterns[id].Length)))
                        candidates.Add((true, id, start));
                }, cancel);
                if (candidates.Count == 0) continue;
                var ranges = candidates.Select(c => c.Compact
                    ? search.CompactRange(c.Start, compactPatterns[c.Pattern].Length)
                    : search.Range(c.Start, patterns[c.Pattern].Length)).ToList();
                // visibility is measured by drawing only where the hits lie, not for every character of the page
                var hidden = Hidden(doc, page, [.. ranges.SelectMany(text.Quads).Select(q => q.Bounds).Select(b => new PdfRectangle(b.Left, b.Top, b.Right, b.Bottom))],
                    cancel);
                for (var k = 0; k < candidates.Count; k++)
                {
                    var (compact, id, start) = candidates[k];
                    var length = (compact ? compactPatterns : patterns)[id].Length;
                    var range = ranges[k];
                    var (visible, centerY, height) = Visibility(text, range, hidden.Rects);
                    var negative = !compact && (SignBefore(search.Text, start) || SignAfter(search.Text, start, length));
                    foreach (var (s, f) in (compact ? compactOwners : owners)[id])
                    {
                        var list = hits[s] ??= [];
                        if (list.Count >= MaxHitsPerValue) continue;
                        var number = searches[s]!.Number;
                        var signFits = number is not { } n || n == 0 || credit || EitherSign.Contains(fields[s].Id) || negative == n < 0;
                        list.Add(new Hit(p, range, searches[s]!.Forms[f], visible, hidden.Complete, signFits, centerY, height, text));
                    }
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                aborted = p;
                break;
            }
        }
        // strong values first: every visible find of them is a possible context of the weak ones (a name that two lines share is found
        // on both) — for their own group and the groups above it up to the line they belong to (the item name in BG-31 is the context of
        // the quantity in BG-25), never beyond a line
        var anchors = new Dictionary<InvoiceGroup, List<Hit>>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < fields.Count; i++)
        {
            if (searches[i] is not { Weak: false, NotCheckable: null } || hits[i] is not { } list || !groupOf.TryGetValue(fields[i], out var group)) continue;
            var found = list.Where(h => h.Visible && h.SignFits && !h.Form.Word).ToList();
            if (found.Count == 0) continue;
            for (var g = group; g is not null && g.Id != "INVOICE"; g = parentOf.GetValueOrDefault(g))
            {
                (anchors.TryGetValue(g, out var a) ? a : anchors[g] = []).AddRange(found);
                if (g.Id is "BG-25" or "BG-DEX-01") break;
            }
        }
        var matches = new List<InvoiceMatch>(fields.Count);
        for (var i = 0; i < fields.Count; i++)
            matches.Add(Resolve(fields[i], searches[i], hits[i], visibleText, groupOf, parentOf, anchors, aborted, pageCount));
        return new InvoiceComparisonResult(matches, pagesChecked, doc.PageCount, visibleText, aborted);
    }

    private static InvoiceMatch Resolve(InvoiceField field, InvoiceValueSearch? search, List<Hit>? hits, bool visibleText,
        Dictionary<InvoiceField, InvoiceGroup> groupOf, Dictionary<InvoiceGroup, InvoiceGroup> parentOf, Dictionary<InvoiceGroup, List<Hit>> anchors,
        int? aborted, int pages)
    {
        if (search is null) return new(field, InvoiceMatchStatus.NotChecked, null, Tr.T("Grenze der Prüfung erreicht"), true);
        if (search.NotCheckable is { } why) return new(field, InvoiceMatchStatus.NotCheckable, null, why, true);
        var match = ResolveFound(field, search, hits, visibleText, groupOf, parentOf, anchors);
        // cancelled: what is not found may still stand on a page not searched
        if (aborted is { } done && match.Status is InvoiceMatchStatus.NotFound or InvoiceMatchStatus.Partial or InvoiceMatchStatus.NotCheckable)
            return new(field, InvoiceMatchStatus.NotChecked, null, Tr.T("abgebrochen – {0} von {1} Seiten geprüft", done, pages), true);
        return match;
    }

    private static InvoiceMatch ResolveFound(InvoiceField field, InvoiceValueSearch search, List<Hit>? hits, bool visibleText,
        Dictionary<InvoiceField, InvoiceGroup> groupOf, Dictionary<InvoiceGroup, InvoiceGroup> parentOf, Dictionary<InvoiceGroup, List<Hit>> anchors)
    {
        if (!visibleText) return new(field, InvoiceMatchStatus.NotCheckable, null, Tr.T("kein lesbarer sichtbarer Text"), true);
        hits ??= [];
        bool InContext(Hit h)
        {
            if (!search.Weak) return true;
            for (var g = groupOf.GetValueOrDefault(field); g is not null && g.Id != "INVOICE"; g = parentOf.GetValueOrDefault(g))
                if (anchors.TryGetValue(g, out var list) && Near(h, list.Where(a => a.Page == h.Page).ToList(), LineGroup(g.Id))) return true;
            return false;
        }
        var exact = Distinct(hits.Where(h => h.Visible && h.SignFits && !h.Form.Rounded && !h.Form.Word && InContext(h)));
        if (exact.Count > 0) return Matched(field, InvoiceMatchStatus.Found, exact);
        var rounded = Distinct(hits.Where(h => h.Visible && h.SignFits && h.Form.Rounded && InContext(h)));
        if (rounded.Count > 0) return Matched(field, InvoiceMatchStatus.Rounded, rounded);
        if (search.Words > 0)
        {
            var words = hits.Where(h => h.Visible && h.Form.Word).Select(h => h.Form.Text).Distinct(StringComparer.Ordinal).Count();
            if (words > 0)
            {
                var first = hits.First(h => h.Visible && h.Form.Word);
                return new(field, InvoiceMatchStatus.Partial, Place(first), Tr.T("{0} von {1} Wörtern", words, search.Words), first.Measured);
            }
        }
        string? note = null;
        List<InvoiceMatchPlace> seen = [];
        if (hits.Any(h => !h.Visible && !h.Form.Word && h.SignFits)) note = Tr.T("nur in unsichtbarem oder verdecktem Text");
        else if (search.Weak && hits.Any(h => h.Visible && h.SignFits))
        {
            note = Tr.T("nur ohne den Zusammenhang seiner Zeile");
            seen = [.. Distinct(hits.Where(h => h.Visible && h.SignFits && !h.Form.Word)).Select(Place)];
        }
        else if (hits.Any(h => h.Visible && !h.SignFits)) note = Tr.T("nur mit anderem Vorzeichen");
        return new(field, InvoiceMatchStatus.NotFound, null, note, true) { VisibleOutOfContext = seen.Count > 0, OutOfContextPlaces = seen };
    }

    /// <summary>
    /// The groups that are one row of a table (lines, VAT breakdown, allowances and charges, price, item): a weak value of theirs stands on
    /// the row of a strong one. The other groups (totals, parties, payment) are blocks of rows.
    /// </summary>
    private static bool LineGroup(string id) => id is "BG-20" or "BG-21" or "BG-23" or "BG-25" or "BG-26" or "BG-27" or "BG-28" or "BG-29"
        or "BG-30" or "BG-31" or "BG-32" || id.StartsWith("BG-DEX-", StringComparison.Ordinal);

    /// <summary>
    /// Whether a weak hit lies on the height of a strong one of its group: in a row of a table on the same line (a strong value over two
    /// lines is twice as high and so covers both); in a block within the rows its strong values take on the page, one line above or below.
    /// </summary>
    private static bool Near(Hit hit, List<Hit> anchors, bool row)
    {
        if (anchors.Count == 0) return false;
        foreach (var a in anchors)
            if (Math.Abs(hit.CenterY - a.CenterY) <= 0.7 * Math.Max(hit.Height, a.Height)) return true;
        if (row) return false;
        var low = anchors.Min(a => a.CenterY - a.Height) - hit.Height;
        var high = anchors.Max(a => a.CenterY + a.Height) + hit.Height;
        return hit.CenterY >= low && hit.CenterY <= high;
    }

    private static InvoiceMatchPlace Place(Hit h) => new(h.Page + 1, h.Text.Quads(h.Range), h.Form.Text);

    /// <summary>A value found at these places (the first is <see cref="InvoiceMatch.Place"/>, the same object as the first of all places).</summary>
    private static InvoiceMatch Matched(InvoiceField field, InvoiceMatchStatus status, List<Hit> hits)
    {
        var places = hits.Select(Place).ToList();
        return new(field, status, places[0], null, hits[0].Measured, places.Count > 1 ? places : null);
    }

    /// <summary>
    /// The hits at distinct places, in the order found, at most <see cref="InvoiceMatch.MaxPlaces"/>: two forms of a value found at the same
    /// spot („275,00“ and „275,00 €“) are one place.
    /// </summary>
    private static List<Hit> Distinct(IEnumerable<Hit> hits)
    {
        var result = new List<Hit>();
        var seen = new HashSet<(int, int, int)>();
        foreach (var h in hits)
        {
            if (h.Text.Quads(h.Range) is not [var q, ..] || !seen.Add((h.Page, (int)Math.Round(q.X0), (int)Math.Round(q.Y0)))) continue;
            result.Add(h);
            if (result.Count >= InvoiceMatch.MaxPlaces) break;
        }
        return result;
    }

    /// <summary>A status as the display says it.</summary>
    public static string Describe(InvoiceMatchStatus status) => status switch
    {
        InvoiceMatchStatus.Found => Tr.T("gefunden"),
        InvoiceMatchStatus.Rounded => Tr.T("nur gerundet gefunden"),
        InvoiceMatchStatus.Partial => Tr.T("teilweise gefunden"),
        InvoiceMatchStatus.NotFound => Tr.T("nicht gefunden – bitte prüfen"),
        InvoiceMatchStatus.NotCheckable => Tr.T("im PDF nicht prüfbar"),
        _ => Tr.T("nicht geprüft"),
    };

    // ------------------------------------------------------------ boundaries and sign

    /// <summary>
    /// Whether a hit stands on its own: a form that begins or ends with a letter or digit must not continue a word or number — „12,50“ not in
    /// „112,50“, „1.212,50“ or „12,505“, „Stift“ not in „Bleistift“.
    /// </summary>
    internal static bool Bounded(string text, int start, int length, Func<int, bool>? narrowSpace = null)
    {
        var end = start + length;
        if (char.IsLetterOrDigit(text[start]) && start > 0)
        {
            var before = text[start - 1];
            if (char.IsLetterOrDigit(before)) return false;
            if (char.IsDigit(text[start]) && start > 1 && char.IsDigit(text[start - 2]))
            {
                // after a point, comma or apostrophe that follows a digit: the rest of a number („1.234,50“, the „10“ of „5,10“)
                if (before is '.' or ',' or '\'') return false;
                // after a space: only a group of thousands if a bare group of one to three digits stands before it and the form begins with
                // three digits („1 234,50“) — in a table, columns are single spaces too („-5,10 321,40 19 61,07“)
                if (before == ' ' && LeadingDigits(text, start) == 3 && BareGroupBefore(text, start - 1) && narrowSpace?.Invoke(start - 1) != false) return false;
            }
        }
        if (char.IsLetterOrDigit(text[end - 1]) && end < text.Length)
        {
            var after = text[end];
            if (char.IsLetterOrDigit(after)) return false;
            if (char.IsDigit(text[end - 1]))
            {
                if (after is '.' or ',' && end + 1 < text.Length && char.IsDigit(text[end + 1])) return false;
                // a bare one to three digits before a space and a group of three: the first group of a number („1 234,50“)
                if (after == ' ' && IsBareGroup(text, start, end) && ThreeDigitGroupAt(text, end + 1) && narrowSpace?.Invoke(end) != false) return false;
            }
        }
        return true;
    }

    /// <summary>How many digits a form begins with (up to its first other character).</summary>
    private static int LeadingDigits(string text, int start)
    {
        var i = start;
        while (i < text.Length && char.IsDigit(text[i])) i++;
        return i - start;
    }

    /// <summary>Whether before the space at <paramref name="space"/> stands a bare group of one to three digits — not the fraction of a number.</summary>
    private static bool BareGroupBefore(string text, int space)
    {
        var i = space - 1;
        while (i >= 0 && char.IsDigit(text[i])) i--;
        var digits = space - 1 - i;
        if (digits is < 1 or > 3) return false;
        return i < 0 || text[i] is not (',' or '.') || i == 0 || !char.IsDigit(text[i - 1]);
    }

    /// <summary>Whether the hit is one to three digits and nothing else, standing alone before it.</summary>
    private static bool IsBareGroup(string text, int start, int end) =>
        end - start is >= 1 and <= 3 && Enumerable.Range(start, end - start).All(k => char.IsDigit(text[k]))
        && (start == 0 || text[start - 1] is not (',' or '.') || start < 2 || !char.IsDigit(text[start - 2]));

    /// <summary>Whether exactly three digits begin at <paramref name="at"/> and no digit follows them.</summary>
    private static bool ThreeDigitGroupAt(string text, int at) =>
        at + 3 <= text.Length && char.IsDigit(text[at]) && char.IsDigit(text[at + 1]) && char.IsDigit(text[at + 2])
        && (at + 3 == text.Length || !char.IsDigit(text[at + 3]));

    /// <summary>Whether a range of the page text stands on its own: no letter or digit right before or after it.</summary>
    private static bool BoundedInPage(string page, TextRange range) =>
        (range.Start == 0 || !char.IsLetterOrDigit(page[range.Start - 1])) && (range.End >= page.Length || !char.IsLetterOrDigit(page[range.End]));

    /// <summary>A minus or an opening bracket before the number (currency signs and codes and spaces between them skipped).</summary>
    internal static bool SignBefore(string text, int start)
    {
        var i = start - 1;
        var letters = 0;
        while (i >= 0 && (text[i] is ' ' or '€' or '$' or '£' or '¥' || char.IsAsciiLetterUpper(text[i]) && ++letters <= 3)) i--;
        return i >= 0 && (text[i] is '-' or '−' || text[i] == '(' && ClosesAfter(text, start));
    }

    /// <summary>A minus right after the number („12,50-“).</summary>
    internal static bool SignAfter(string text, int start, int length)
    {
        var end = start + length;
        return end < text.Length && text[end] is '-' or '−' && (end + 1 >= text.Length || !char.IsDigit(text[end + 1]));
    }

    /// <summary>
    /// Whether the space at <paramref name="space"/> of the search text is narrow — the gap of a group of thousands (a quarter to a third of
    /// the font size) — and not the gap between two columns of a table or a line break: „19 684,00“ is the rate 19 and the tax 684,00 when the
    /// gap is wide, nineteen thousand when it is narrow. Measured on the characters left and right of it.
    /// </summary>
    private static bool NarrowSpace(PageText text, InvoiceSearchText search, int space)
    {
        if (space < 1 || space + 1 >= search.Text.Length) return false;
        TextGlyph? left = null, right = null;
        foreach (var g in text.GlyphsOf(search.Range(space - 1, 1))) left = g;
        foreach (var g in text.GlyphsOf(search.Range(space + 1, 1)))
        {
            right = g;
            break;
        }
        if (left is not { } a || right is not { } b) return false;
        var size = Math.Max(Math.Max(a.Size, b.Size), 0.1f);
        var ay = (a.Y0 + a.Y1 + a.Y2 + a.Y3) / 4;
        var by = (b.Y0 + b.Y1 + b.Y2 + b.Y3) / 4;
        if (Math.Abs(ay - by) > 0.5 * size) return false;   // another line
        var gap = Math.Min(b.X0, b.X3) - Math.Max(a.X1, a.X2);
        return gap < 0.5 * size;
    }

    private static bool ClosesAfter(string text, int start)
    {
        for (var i = start; i < text.Length && i < start + 40; i++)
            if (text[i] == ')') return true;
        return false;
    }

    // ------------------------------------------------------------ visibility

    /// <summary>
    /// The places of the page's hidden text (as rectangles of its lines) around the hits, and whether every character there was decided.
    /// </summary>
    private static (List<PdfRectangle> Rects, bool Complete) Hidden(PdfDocument doc, PdfPage page, IReadOnlyList<PdfRectangle> areas, CancellationToken cancel)
    {
        try
        {
            var scan = HiddenText.Scan(doc, page, areas, cancel);
            return ([.. scan.Spots.SelectMany(s => s.Lines)], scan.Complete);
        }
        catch (Exception e) when (e is PdfException or IOException or InvalidDataException)
        {
            return ([], false);
        }
    }

    /// <summary>
    /// Whether every character of a hit is drawn and lies outside the hidden places; and its vertical centre and height (for the context).
    /// A visible character over a hidden place counts as hidden — in doubt „bitte prüfen“, never „gefunden“.
    /// </summary>
    private static (bool Visible, double CenterY, double Height) Visibility(PageText text, TextRange range, List<PdfRectangle> hidden)
    {
        var visible = true;
        double low = double.MaxValue, high = double.MinValue;
        foreach (var g in text.GlyphsOf(range))
        {
            var cx = (g.X0 + g.X1 + g.X2 + g.X3) / 4.0;
            var cy = (g.Y0 + g.Y1 + g.Y2 + g.Y3) / 4.0;
            low = Math.Min(low, Math.Min(Math.Min(g.Y0, g.Y1), Math.Min(g.Y2, g.Y3)));
            high = Math.Max(high, Math.Max(Math.Max(g.Y0, g.Y1), Math.Max(g.Y2, g.Y3)));
            if (!g.Visible) visible = false;
            else
                foreach (var r in hidden)
                    if (cx >= r.Left - 0.5 && cx <= r.Right + 0.5 && cy >= r.Bottom - 0.5 && cy <= r.Top + 0.5)
                    {
                        visible = false;
                        break;
                    }
        }
        if (low > high) return (false, 0, 0);
        return (visible, (low + high) / 2, Math.Max(high - low, 1));
    }
}
