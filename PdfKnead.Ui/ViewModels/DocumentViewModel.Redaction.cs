using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.IO;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.ViewModels;

/// <summary>Patterns for „Suchen und schwärzen“.</summary>
[Flags]
public enum RedactionPatterns
{
    None = 0,
    Email = 1,
    Phone = 2,
    Iban = 4,

    /// <summary>Card numbers (13 to 19 digits) with a valid Luhn check digit (<see cref="NumberChecks.CardValid"/>).</summary>
    Card = 8,

    /// <summary>Dates: 24.12.2026, 24/12/26, 2026-12-24, 24. Dezember 2026, December 24, 2026.</summary>
    Date = 16,

    /// <summary>Persons, recognised by rules and lists without AI (KONZEPT.md, section 5.23; <see cref="Core.Pdf.Text.Entities.EntityRecognizer"/>).</summary>
    Person = 32,

    /// <summary>Companies and organisations.</summary>
    Organisation = 64,

    // the parts of an address, each ticked on its own
    Street = 128,
    HouseNumber = 256,
    PostalCode = 512,
    Place = 1024,
    Country = 2048,
    PostBox = 4096,

    /// <summary>„Orte (überall)“: known places also outside addresses.</summary>
    PlacesEverywhere = 8192,

    /// <summary>„Gründlich“: every capitalised word that is no known word, as a suggestion (perhaps a name the lists do not know).</summary>
    Thorough = 16384,

    /// <summary>All parts of an address.</summary>
    Address = Street | HouseNumber | PostalCode | Place | Country | PostBox,

    /// <summary>Everything the recognition of persons, companies and addresses finds.</summary>
    Entities = Person | Organisation | Address | PlacesEverywhere | Thorough,
}

/// <summary>
/// A hit for „Suchen und schwärzen“: page, range in the page text, the found text and an excerpt for display. <c>InPlace</c>: the range is one of
/// the text read in the order of the places along the lines (<see cref="PageText.InPlace"/>), on a page that draws a line out of order.
/// </summary>
public sealed record RedactionHit(int Page, TextRange Range, string Found, string Context, bool InPlace = false)
{
    /// <summary>
    /// All its characters lie under a mark already („Suchen und schwärzen“ a second time; report of 4.11.2026: the places were marked twice):
    /// offered as „schon markiert“, not ticked and not marked again.
    /// </summary>
    public bool Marked { get; init; }

    /// <summary>What kind of pattern found it (E-Mail, IBAN …); None for a word searched for, or one found by several.</summary>
    public Core.Pdf.Editing.RedactionFinds Kind { get; init; }

    /// <summary>All its characters are invisible (text rendering modes 3 and 7, usually a text recognition): nobody sees it, a bar would cover what lies there.</summary>
    public bool Invisible { get; init; }

    /// <summary>In <see cref="DocumentViewModel.FindUnredactedAsync"/>: exactly a redacted piece of text; false: only a word of one („Einrichtung“ of „Einrichtung der B“).</summary>
    public bool Whole { get; init; } = true;

    /// <summary>Where the hit stands in <see cref="Context"/> (to set it off); length 0 if it is not there.</summary>
    public TextRange InContext { get; init; }

    /// <summary>Why the hit may be none (an IBAN whose check digits do not fit); null for a sure one. Doubtful hits are offered, not ticked.</summary>
    public string? Doubt { get; init; }

    /// <summary>What the recognition of persons, companies and addresses found here (shown beside the hit); null for other hits.</summary>
    public Core.Pdf.Text.Entities.EntityKind? Entity { get; init; }
}

/// <summary>Marking redactions: area, selected text, search and redact.</summary>
public sealed partial class DocumentViewModel
{
    private static readonly Regex EmailPattern = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);

    // Prefix +, 00 or 0, then at least seven characters that start and end with a digit; separators: space, hyphen, slash, parentheses. Not
    // the rest of a longer number after a hyphen, slash or dot — „2026-05015352“, an offer number, is no phone number (report of 5.11.2026,
    // docs/FEHLERGEDAECHTNIS.md, No. 531)
    private static readonly Regex PhonePattern = new(@"(?<![\w])(?<!\d[\-/.])(?:\+|00|0)\d[\d \-/()]{5,}\d(?![\w])", RegexOptions.Compiled);

    // Country code, check digits, then 2 to 7 groups of four characters and an optional shorter last group (with or without spaces)
    private static readonly Regex IbanPattern = new(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){2,7}(?: ?[A-Z0-9]{1,4})?\b", RegexOptions.Compiled);

    // 13 to 19 digits, single spaces or hyphens between them; not part of a longer number or a word (the digits of an IBAN)
    private static readonly Regex CardPattern = new(@"(?<![\w-])(?<!\d[ -])(?:\d[ -]?){12,18}\d(?![ -]?\d)(?!\w)", RegexOptions.Compiled);

    // Dates: day.month.year (also / and -), year-month-day, „24. Dezember 2026“, „December 24, 2026“
    private static readonly Regex DatePattern = new(
        @"\b(?:(?:0?[1-9]|[12]\d|3[01])[./-](?:0?[1-9]|1[0-2])[./-](?:\d{4}|\d{2})(?!\d)|\d{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])(?!\d)" +
        @"|(?:0?[1-9]|[12]\d|3[01])\.?\s+(?:Januar|Jänner|Februar|März|Maerz|April|Mai|Juni|Juli|August|September|Oktober|November|Dezember|January|February|March|May|June|July|October|December|Jan|Feb|Mär|Mar|Apr|Jun|Jul|Aug|Sep|Sept|Okt|Oct|Nov|Dez|Dec)\.?\s+\d{4}\b" +
        @"|(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+(?:0?[1-9]|[12]\d|3[01]),?\s+\d{4}\b)",
        RegexOptions.Compiled);

    /// <summary>Areas of a text range in page space (<see cref="PageText.RedactionAreas"/>).</summary>
    /// <param name="text">The page text.</param>
    /// <param name="range">The range in <paramref name="text"/>.</param>
    /// <returns>The rectangles; empty if the range has no visible glyphs.</returns>
    public static List<PdfRectangle> AreasOf(PageText text, TextRange range) => text.RedactionAreas(range);

    /// <summary>
    /// Areas of a text range for a redaction bar: <see cref="AreasOf"/>, and over a scan — the characters are the invisible text of a
    /// recognition — grown to the ink they stand for (<see cref="PdfKnead.Rendering.InkSnap"/>, docs/FEHLERGEDAECHTNIS.md, No. 351).
    /// </summary>
    /// <param name="page">The page (position in the document).</param>
    /// <param name="text">Its text.</param>
    /// <param name="range">The range.</param>
    /// <param name="lists">Display lists already built, by page (a search redacts many hits of one page).</param>
    /// <returns>The rectangles; empty if the range has no glyphs.</returns>
    public List<PdfRectangle> BarAreasOf(int page, PageText text, TextRange range, Dictionary<int, Core.Pdf.Graphics.DisplayList>? lists = null)
    {
        var areas = AreasOf(text, range);
        if (areas.Count == 0 || !AllInvisible(text, range) || (uint)page >= (uint)History.Count) return areas;
        var item = History.Pages[page];
        if (lists is null || !lists.TryGetValue(page, out var list))
        {
            list = Core.Pdf.Content.PageInterpreter.Build(item.Source, item.Page);
            if (lists is not null) lists[page] = list;
        }
        return PdfKnead.Rendering.InkSnap.Grow(item.Source, item.Page, list, areas, Renderer.Images,
            PdfKnead.Rendering.InkSnap.WholeWords(text.Text, range.Start, range.End));
    }

    /// <summary>Redacts the selected text (one redaction, one area per line).</summary>
    /// <returns>false without a selection.</returns>
    public bool RedactSelection()
    {
        if (!HasTextSelection || TryGetText(SelectionPage) is not { } text || BarAreasOf(SelectionPage, text, Selection) is not { Count: > 0 } areas) return false;
        var page = SelectionPage;
        ClearSelection();
        AddElement(page, RedactionElement.FromPageAreas(areas), Tr.T("Text schwärzen"));
        return true;
    }

    /// <summary>Searches all pages for words and patterns (page text as for the search).</summary>
    /// <param name="words">The search terms.</param>
    /// <param name="patterns">The patterns to look for as well.</param>
    /// <param name="matchCase">true = case-sensitive (applies to the words).</param>
    /// <param name="cancel">Cancels the search.</param>
    /// <returns>The hits, with overlapping ones merged.</returns>
    public Task<List<RedactionHit>> FindForRedactionAsync(IReadOnlyList<string> words, RedactionPatterns patterns, bool matchCase,
        CancellationToken cancel = default) => SearchAsync(words, patterns, matchCase, null, cancel);

    /// <summary>
    /// Pages that draw text no search can find, because it cannot be read (docs/RECHERCHE-VERSTECKEN.md, B-6): shown in „Suchen und schwärzen“,
    /// there it must be redacted by area.
    /// </summary>
    /// <param name="cancel">Cancels the check.</param>
    /// <returns>The pages (index), ascending.</returns>
    public async Task<IReadOnlyList<int>> UnreadablePagesAsync(CancellationToken cancel = default)
    {
        var pages = new List<int>();
        for (var page = 0; page < Pages.Count; page++)
        {
            cancel.ThrowIfCancellationRequested();
            if ((await GetTextAsync(page)).UnreadableGlyphs > 0) pages.Add(page);
        }
        return pages;
    }

    /// <summary>
    /// Pages with text that reads suspiciously (docs/RECHERCHE-VERSTECKEN.md, B-5): many different characters of a font read the same; a search may
    /// not find what they show. Shown in „Suchen und schwärzen“.
    /// </summary>
    /// <param name="cancel">Cancels the check.</param>
    /// <returns>The pages (index), ascending.</returns>
    public async Task<IReadOnlyList<int>> MisreadPagesAsync(CancellationToken cancel = default)
    {
        var pages = new List<int>();
        for (var page = 0; page < Pages.Count; page++)
        {
            cancel.ThrowIfCancellationRequested();
            if ((await GetTextAsync(page)).MisreadGlyphs > 0) pages.Add(page);
        }
        return pages;
    }

    /// <summary>Like <see cref="FindForRedactionAsync"/>; <paramref name="keep"/> filters the hits straight away, using the text of the same page.</summary>
    private async Task<List<RedactionHit>> SearchAsync(IReadOnlyList<string> words, RedactionPatterns patterns, bool matchCase,
        Func<int, PageText, TextRange, bool>? keep, CancellationToken cancel)
    {
        var hits = new List<RedactionHit>();
        var queries = words.Select(PageText.NormalizeQuery).Where(w => w.Length > 0).Distinct().ToList();
        // each pattern with its check: null = every match counts; otherwise the match is skipped (false) or offered with a doubt (a text)
        var regexes = new List<(Regex Pattern, Func<string, (bool Keep, string? Doubt)>? Check, Core.Pdf.Editing.RedactionFinds Kind)>();
        if (patterns.HasFlag(RedactionPatterns.Email)) regexes.Add((EmailPattern, null, Core.Pdf.Editing.RedactionFinds.Email));
        if (patterns.HasFlag(RedactionPatterns.Phone)) regexes.Add((PhonePattern, null, Core.Pdf.Editing.RedactionFinds.Phone));
        // an IBAN with wrong check digits may be a typo of a real one: offered, not ticked — nothing that may be one is left out
        if (patterns.HasFlag(RedactionPatterns.Iban))
            regexes.Add((IbanPattern, m => NumberChecks.IbanValid(m) ? (true, null) : (true, Tr.T("Prüfziffer passt nicht")), Core.Pdf.Editing.RedactionFinds.Iban));
        // a card number counts only with a valid check digit: otherwise every long order or customer number would be one
        if (patterns.HasFlag(RedactionPatterns.Card)) regexes.Add((CardPattern, m => (NumberChecks.CardValid(m), null), Core.Pdf.Editing.RedactionFinds.Card));
        if (patterns.HasFlag(RedactionPatterns.Date)) regexes.Add((DatePattern, null, Core.Pdf.Editing.RedactionFinds.Date));
        // persons, companies and addresses (KONZEPT.md, section 5.23): the recognizer is made on first use, off the window's thread
        var entities = (patterns & RedactionPatterns.Entities) != 0
            ? await Task.Run(() => Core.Pdf.Text.Entities.EntityRecognizer.For(new Core.Pdf.Text.Entities.EntityOptions(
                patterns.HasFlag(RedactionPatterns.PlacesEverywhere), patterns.HasFlag(RedactionPatterns.Thorough))), cancel)
            : null;
        var lists = new Dictionary<int, Core.Pdf.Graphics.DisplayList>();
        for (var page = 0; page < Pages.Count; page++)
        {
            cancel.ThrowIfCancellationRequested();
            var text = await GetTextAsync(page);
            if (text.IsEmpty) continue;
            var marks = MarkAreas(page);
            var visibleMarks = marks.Count == 0 ? marks : MarkAreas(page, withHidden: false);   // a mark of hidden content covers no visible text
            // the text in content order, and on a page that draws a line out of order also the text in the order of the places
            // (docs/FEHLERGEDAECHTNIS.md, No. 267): a hit there counts if its characters are not those of a hit already found
            var taken = new HashSet<Core.Pdf.Graphics.TextGlyph>();
            foreach (var (source, inPlace) in text.InPlace is { } placed ? [(text, false), (placed, true)] : new[] { (text, false) })
            {
                var ranges = new List<TextRange>();
                var doubts = new Dictionary<TextRange, string>();
                var kinds = new Dictionary<TextRange, Core.Pdf.Editing.RedactionFinds>();   // what a pattern found (a word searched for: none)
                foreach (var found in source.FindAll(queries, matchCase)) ranges.AddRange(found);   // all terms in one pass
                foreach (var (regex, check, kind) in regexes)
                    foreach (Match m in regex.Matches(source.Text))
                    {
                        var (counts, doubt) = check?.Invoke(m.Value) ?? (true, null);
                        if (!counts) continue;
                        var found = new TextRange(m.Index, m.Length);
                        ranges.Add(found);
                        kinds.TryAdd(found, kind);
                        if (doubt is not null) doubts[found] = doubt;
                    }
                var entityKinds = new Dictionary<TextRange, Core.Pdf.Text.Entities.EntityKind>();
                if (entities is not null)
                    foreach (var e in await Task.Run(() => FindEntities(entities, source.Text, patterns, cancel), cancel))
                    {
                        var found = new TextRange(e.Start, e.Length);
                        ranges.Add(found);
                        kinds.TryAdd(found, FindOf(e.Kind));
                        entityKinds.TryAdd(found, e.Kind);
                        if (DoubtOf(e.Doubt) is { } doubt) doubts[found] = doubt;
                    }
                // Merge overlapping hits (a name can be part of an e-mail address); a merged hit is doubtful only if all its parts are
                foreach (var (range, parts) in Merge(ranges))
                {
                    var doubtOfHit = parts.All(doubts.ContainsKey) ? parts.Select(r => doubts[r]).FirstOrDefault() : null;
                    var glyphs = source.GlyphsOf(range).ToList();
                    if (inPlace && glyphs.All(taken.Contains)) continue;
                    if (keep is not null && !keep(page, source, range)) continue;
                    taken.UnionWith(glyphs);
                    var (context, inContext) = Context(source, range, marks);
                    hits.Add(new RedactionHit(page, range, source.Text.Substring(range.Start, range.Length).ReplaceLineEndings(" "), context, inPlace)
                    {
                        // recognised text over a scan is not hidden: the ink it stands for shows (docs/FEHLERGEDAECHTNIS.md, No. 352)
                        Invisible = AllInvisible(source, range) && !InkUnder(page, source, range, lists),
                        InContext = inContext,
                        Doubt = doubtOfHit,
                        // the kind of hit, for its code (wish of 4.11.2026): that of its longest part — an IBAN in which the phone
                        // pattern also found a run of digits is an IBAN (report of 4.11.2026: no code at IBANs); a word searched for none
                        Kind = KindOf(parts, kinds),
                        Marked = visibleMarks.Count > 0 && Covered(source, range, visibleMarks),
                        // what the recognition found, when the hit is one of its finds alone (merged with others: that of its longest part)
                        Entity = parts.Where(entityKinds.ContainsKey).OrderByDescending(p => p.Length).Select(p => (Core.Pdf.Text.Entities.EntityKind?)entityKinds[p]).FirstOrDefault(),
                    });
                }
            }
        }
        return hits;
    }

    /// <summary>Longest stretch of page text the recognition reads at once; a longer text is read in pieces cut at line ends.</summary>
    internal const int EntityChunk = 50_000;

    /// <summary>
    /// The finds of the recognition of persons, companies and addresses in a page text that are asked for: each part of an address only if its
    /// box is ticked, places everywhere with „Orte (überall)“, the unknown words of „Gründlich“ with it. A very long text is read in pieces.
    /// </summary>
    internal static List<Core.Pdf.Text.Entities.EntityHit> FindEntities(Core.Pdf.Text.Entities.EntityRecognizer recognizer, string text,
        RedactionPatterns patterns, CancellationToken cancel)
    {
        var result = new List<Core.Pdf.Text.Entities.EntityHit>();
        for (var start = 0; start < text.Length;)
        {
            cancel.ThrowIfCancellationRequested();
            var end = Math.Min(text.Length, start + EntityChunk);
            if (end < text.Length && text.LastIndexOf('\n', end - 1, end - start) is var cut and > 0 && cut > start) end = cut + 1;
            foreach (var hit in recognizer.Find(text[start..end]))
                if (Wanted(hit, patterns)) result.Add(hit with { Start = hit.Start + start });
            start = end;
        }
        return result;
    }

    private static bool Wanted(Core.Pdf.Text.Entities.EntityHit hit, RedactionPatterns patterns) => hit.Kind switch
    {
        Core.Pdf.Text.Entities.EntityKind.Person => patterns.HasFlag(RedactionPatterns.Person) ||
                                                   (hit.Doubt == Core.Pdf.Text.Entities.EntityDoubt.Unknown && patterns.HasFlag(RedactionPatterns.Thorough)),
        Core.Pdf.Text.Entities.EntityKind.Organisation => patterns.HasFlag(RedactionPatterns.Organisation),
        Core.Pdf.Text.Entities.EntityKind.Street => patterns.HasFlag(RedactionPatterns.Street),
        Core.Pdf.Text.Entities.EntityKind.HouseNumber => patterns.HasFlag(RedactionPatterns.HouseNumber),
        Core.Pdf.Text.Entities.EntityKind.PostalCode => patterns.HasFlag(RedactionPatterns.PostalCode),
        Core.Pdf.Text.Entities.EntityKind.Place => patterns.HasFlag(RedactionPatterns.Place) || patterns.HasFlag(RedactionPatterns.PlacesEverywhere),
        Core.Pdf.Text.Entities.EntityKind.Country => patterns.HasFlag(RedactionPatterns.Country),
        _ => patterns.HasFlag(RedactionPatterns.PostBox),
    };

    private static Core.Pdf.Editing.RedactionFinds FindOf(Core.Pdf.Text.Entities.EntityKind kind) => kind switch
    {
        Core.Pdf.Text.Entities.EntityKind.Person => Core.Pdf.Editing.RedactionFinds.Person,
        Core.Pdf.Text.Entities.EntityKind.Organisation => Core.Pdf.Editing.RedactionFinds.Organisation,
        _ => Core.Pdf.Editing.RedactionFinds.Address,
    };

    /// <summary>Why a find of the recognition is only a suggestion (shown beside it); null for a sure one.</summary>
    internal static string? DoubtOf(Core.Pdf.Text.Entities.EntityDoubt doubt) => doubt switch
    {
        Core.Pdf.Text.Entities.EntityDoubt.AlsoAWord => Tr.T("Name ist auch ein Wort"),
        Core.Pdf.Text.Entities.EntityDoubt.SingleName => Tr.T("nur ein einzelner Name aus der Liste"),
        Core.Pdf.Text.Entities.EntityDoubt.TitleIsAWord => Tr.T("Titel ist auch ein Wort"),
        Core.Pdf.Text.Entities.EntityDoubt.Unknown => Tr.T("unbekanntes Wort, vielleicht ein Name"),
        _ => null,
    };

    /// <summary>What a find of the recognition is, as shown beside it.</summary>
    internal static string EntityLabel(Core.Pdf.Text.Entities.EntityKind kind) => kind switch
    {
        Core.Pdf.Text.Entities.EntityKind.Person => Tr.T("Person"),
        Core.Pdf.Text.Entities.EntityKind.Organisation => Tr.T("Firma"),
        Core.Pdf.Text.Entities.EntityKind.Street => Tr.T("Straße"),
        Core.Pdf.Text.Entities.EntityKind.HouseNumber => Tr.T("Hausnummer"),
        Core.Pdf.Text.Entities.EntityKind.PostalCode => Tr.T("PLZ"),
        Core.Pdf.Text.Entities.EntityKind.Place => Tr.T("Ort"),
        Core.Pdf.Text.Entities.EntityKind.Country => Tr.T("Land"),
        _ => Tr.T("Postfach"),
    };

    /// <summary>
    /// The kind of a hit merged from overlapping parts: that of its longest part; among parts of the same length one found by a pattern, the
    /// more specific first (IBAN, card, e-mail, phone, date). None if the longest part is a word searched for.
    /// </summary>
    internal static Core.Pdf.Editing.RedactionFinds KindOf(IReadOnlyList<TextRange> parts, IReadOnlyDictionary<TextRange, Core.Pdf.Editing.RedactionFinds> kinds)
    {
        static int Rank(Core.Pdf.Editing.RedactionFinds k) => k switch
        {
            Core.Pdf.Editing.RedactionFinds.Iban => 0,
            Core.Pdf.Editing.RedactionFinds.Card => 1,
            Core.Pdf.Editing.RedactionFinds.Email => 2,
            Core.Pdf.Editing.RedactionFinds.Phone => 3,
            Core.Pdf.Editing.RedactionFinds.Date => 4,
            Core.Pdf.Editing.RedactionFinds.Person => 5,
            Core.Pdf.Editing.RedactionFinds.Organisation => 6,
            Core.Pdf.Editing.RedactionFinds.Address => 7,
            _ => 8,
        };
        if (parts.Count == 0) return Core.Pdf.Editing.RedactionFinds.None;
        var longest = parts.Max(p => p.Length);
        return parts.Where(p => p.Length == longest).Select(p => kinds.GetValueOrDefault(p)).OrderBy(Rank).First();
    }

    /// <summary>Whether ink shows under the characters of a range (recognised text over a scan; <see cref="PdfKnead.Rendering.InkSnap.HasInk"/>).</summary>
    private bool InkUnder(int page, PageText text, TextRange range, Dictionary<int, Core.Pdf.Graphics.DisplayList> lists)
    {
        if ((uint)page >= (uint)History.Count) return false;
        var item = History.Pages[page];
        if (!lists.TryGetValue(page, out var list)) lists[page] = list = Core.Pdf.Content.PageInterpreter.Build(item.Source, item.Page);
        return AreasOf(text, range).Any(a => PdfKnead.Rendering.InkSnap.HasInk(item.Source, item.Page, list, a, Renderer.Images));
    }

    /// <summary>Whether all characters of the range are invisible (spaces do not count).</summary>
    private static bool AllInvisible(PageText text, TextRange range)
    {
        var any = false;
        for (var c = range.Start; c < range.End; c++)
        {
            if (char.IsWhiteSpace(text.Text[c])) continue;
            if (!text.IsInvisible(c)) return false;
            any = true;
        }
        return any;
    }

    private static IEnumerable<(TextRange Range, List<TextRange> Parts)> Merge(List<TextRange> ranges)
    {
        // one pass over the sorted hits: the parts of a merged hit are the hits taken into it (a hit of the next one starts after its end),
        // so a page with thousands of hits is not compared with itself pair by pair
        TextRange? open = null;
        var parts = new List<TextRange>();
        foreach (var r in ranges.OrderBy(r => r.Start))
        {
            if (open is { } o && r.Start <= o.End)
            {
                open = new TextRange(o.Start, Math.Max(o.End, r.End) - o.Start);
                parts.Add(r);
                continue;
            }
            if (open is { } done) yield return (done, parts);
            open = r;
            parts = [r];
        }
        if (open is { } last) yield return (last, parts);
    }

    /// <summary>
    /// A piece of text around the hit: up to 30 characters on each side, from the hit's own line only. Invisible characters and characters
    /// under a mark are left out („…“ in their place): the page text runs in content order, and across lines the piece showed hidden and
    /// marked text of other places (docs/FEHLERGEDAECHTNIS.md, No. 189).
    /// </summary>
    /// <param name="text">The page text.</param>
    /// <param name="range">The hit.</param>
    /// <param name="marks">Areas of the redaction marks of the page.</param>
    /// <returns>The piece and where the hit stands in it.</returns>
    private static (string Text, TextRange Hit) Context(PageText text, TextRange range, List<Core.Pdf.Graphics.RectD> marks)
    {
        var s = text.Text;
        var lineStart = text.LineAt(range.Start).Start;
        var lineEnd = text.LineAt(Math.Max(range.Start, range.End - 1)).End;
        var start = Math.Max(lineStart, range.Start - 30);
        var end = Math.Min(lineEnd, range.End + 30);
        var sb = new StringBuilder(end - start + 4);
        if (start > lineStart) sb.Append('…');
        var gap = false;
        for (var c = start; c < end; c++)
        {
            var inHit = c >= range.Start && c < range.End;
            if (!inHit && (text.IsInvisible(c) || UnderMark(c)))
            {
                gap = true;
                continue;
            }
            if (gap && !inHit && char.IsWhiteSpace(s[c])) continue;   // the spaces around a gap go with it
            if (gap) Gap();
            gap = false;
            if (c == range.Start) sb.Append(HitStart);
            sb.Append(s[c]);
            if (c == range.End - 1) sb.Append(HitEnd);
        }
        if (gap) Gap();
        else if (end < lineEnd) sb.Append('…');
        var piece = sb.ToString().Trim().ReplaceLineEndings(" ");
        var from = piece.IndexOf(HitStart);
        var to = piece.IndexOf(HitEnd);
        if (from < 0 || to < from) return (piece.Replace(HitStart.ToString(), "").Replace(HitEnd.ToString(), "").Trim(), default);
        return (piece.Remove(to, 1).Remove(from, 1).Trim(), new TextRange(from, to - from - 1));

        // „ … “ in place of left-out characters (once, also right after a cut at the start)
        void Gap()
        {
            while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
            if (sb.Length == 0 || sb[^1] != '…') sb.Append(sb.Length == 0 ? "…" : " …");
            sb.Append(' ');
        }

        bool UnderMark(int c)
        {
            if (marks.Count == 0 || text.Quads(new TextRange(c, 1)) is not [var quad]) return false;
            var b = quad.Bounds;
            double x = (b.Left + b.Right) / 2, y = (b.Top + b.Bottom) / 2;
            return marks.Any(m => x >= m.Left && x <= m.Right && y >= m.Top && y <= m.Bottom);
        }
    }

    /// <summary>Marks the hit in a piece of text while it is built (not a character of any text).</summary>
    private const char HitStart = '\uE000', HitEnd = '\uE001';

    /// <summary>Redacts the chosen hits, all together in one step (a single undo removes them again).</summary>
    /// <param name="hits">The hits.</param>
    /// <param name="hiddenOnly">Marks of hidden content: only what nobody sees goes there, without a bar (<see cref="RedactionElement.Hidden"/>).</param>
    /// <returns>The number of redactions added.</returns>
    public async Task<int> RedactHitsAsync(IEnumerable<RedactionHit> hits, bool hiddenOnly = false, Func<RedactionHit, Core.Pdf.Editing.ReasonChoice?>? reasonOf = null)
    {
        var byPage = new Dictionary<int, IReadOnlyList<PageElement>>();
        var count = 0;
        foreach (var group in hits.GroupBy(h => h.Page))
        {
            var text = await GetTextAsync(group.Key);   // from the cache or read again (never silently skipped)
            // a bar over the ink a scan's recognised text stands for; marks of hidden text keep the characters' own areas (no bar)
            var lists = new Dictionary<int, Core.Pdf.Graphics.DisplayList>();
            var elements = group.Select(h => (Hit: h, Areas: hiddenOnly ? AreasOf(h.InPlace ? text.InPlace ?? text : text, h.Range)
                    : BarAreasOf(group.Key, h.InPlace ? text.InPlace ?? text : text, h.Range, lists))).Where(a => a.Areas.Count > 0)
                .Select(a => (PageElement)(reasonOf?.Invoke(a.Hit) is { } why
                    ? why.On(RedactionElement.FromPageAreas(a.Areas) with { Hidden = hiddenOnly })
                    : RedactionElement.FromPageAreas(a.Areas) with { Hidden = hiddenOnly })).ToList();
            byPage[group.Key] = elements;
            count += elements.Count;
        }
        if (count > 0) History.AddElements(byPage, Tr.T("Suchen und schwärzen"));
        return count;
    }

    /// <summary>
    /// The bar each hit would get (the largest of its areas, as the reason is written into it; <see cref="Core.Pdf.Editing.RedactionReasons.Bars"/>):
    /// for the warning in „Suchen und schwärzen“ where a code does not fit (wish of 4.11.2026). Hits without an area are left out.
    /// </summary>
    public async Task<Dictionary<RedactionHit, PdfRectangle>> BarsOfAsync(IEnumerable<RedactionHit> hits)
    {
        var bars = new Dictionary<RedactionHit, PdfRectangle>();
        foreach (var group in hits.GroupBy(h => h.Page))
        {
            var text = await GetTextAsync(group.Key);
            var lists = new Dictionary<int, Core.Pdf.Graphics.DisplayList>();
            foreach (var hit in group)
            {
                var areas = BarAreasOf(group.Key, hit.InPlace ? text.InPlace ?? text : text, hit.Range, lists);
                if (areas.Count > 0) bars[hit] = RedactionElement.FromPageAreas(areas).PageAreas.MaxBy(a => a.Width * a.Height);
            }
        }
        return bars;
    }

    /// <summary>
    /// Finds text under the redactions that still appears unredacted elsewhere (a name redacted on page 1 but not on page 3):
    /// the search covers the redacted pieces of text and their words of 4 or more characters, case-insensitively and as whole
    /// words only; hits already under a redaction do not count.
    /// </summary>
    /// <param name="cancel">Cancels the search.</param>
    /// <returns>The unredacted occurrences.</returns>
    public async Task<List<RedactionHit>> FindUnredactedAsync(CancellationToken cancel = default)
    {
        var (phrases, pieces) = await RedactedTextAsync();
        if (phrases.Count == 0) return [];
        // Filter during the search, with the text of the same page; a second pass fetching the text again per hit would, in large
        // documents, find it evicted from the cache and read the page again
        List<Core.Pdf.Graphics.RectD>? areas = null;
        var areasPage = -1;
        var hits = await SearchAsync([.. phrases], RedactionPatterns.None, matchCase: false, (page, text, range) =>
        {
            // Whole words only („Max“ not inside „Maximal“)
            var before = range.Start > 0 ? text.Text[range.Start - 1] : ' ';
            var after = range.End < text.Text.Length ? text.Text[range.End] : ' ';
            if (char.IsLetterOrDigit(before) || char.IsLetterOrDigit(after)) return false;
            if (areasPage != page) (areas, areasPage) = (MarkAreas(page, withHidden: false), page);
            return areas!.Count == 0 || !Covered(text, range, areas);
        }, cancel);
        return [.. hits.Select(h => h with { Whole = pieces.Contains(OneLine(h.Found)) })];
    }

    /// <summary>The text with its spaces and line ends as single spaces, to compare a hit with a redacted piece.</summary>
    private static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The place framed on the page, from the question „Jetzt schwärzen?“ or the area „Notizen“: page, areas and, for invisible text, the text itself; null if none is shown.</summary>
    public (int Page, IReadOnlyList<PdfRectangle> Areas, string? Hidden)? ShownPlace { get; private set; }

    private void ClearShownPlace()
    {
        ShownPlace = null;
        HiddenSpotsChanged?.Invoke();
    }

    private int _shownHitAsked;

    /// <summary>
    /// Shows a hit of the question „Jetzt schwärzen?“: the viewer jumps there and frames it for as long as its line is chosen (wish of 30.10.2026;
    /// the question stays a window, the page can be read beside it).
    /// </summary>
    /// <param name="hit">The hit; null takes the frame away.</param>
    public async Task ShowRedactionHitAsync(RedactionHit? hit)
    {
        var asked = ++_shownHitAsked;
        if (hit is null || (uint)hit.Page >= (uint)Pages.Count)
        {
            ClearShownPlace();
            return;
        }
        var page = await GetTextAsync(hit.Page);
        if (asked != _shownHitAsked) return;   // another line chosen in the meantime
        var source = hit.InPlace ? page.InPlace ?? page : page;
        ShownPlace = (hit.Page, AreasOf(source, hit.Range), hit.Invisible ? hit.Found : null);
        HiddenSpotsChanged?.Invoke();
        ScrollToPage(hit.Page);
        if (source.Quads(hit.Range) is [var quad, ..]) RevealRequested?.Invoke(hit.Page, quad);
    }

    /// <summary>
    /// Finds redacted text outside the page text: document properties (Info, XMP), bookmarks, comments, field values, own texts,
    /// structure tree, attachments, layer names and named destinations; a bar does not help there. Each location is reported once,
    /// with the „Dokument bereinigen“ item that removes it (<see cref="CleanupItems.None"/>: change by hand). Searched for are the
    /// redacted text under bars and the hidden text under marks of hidden content (not invisible OCR text: it repeats the visible scan).
    /// </summary>
    /// <returns>The locations found.</returns>
    public async Task<List<RedactionElsewhere>> FindElsewhereAsync()
    {
        var phrases = await RedactedPhrasesAsync();
        var found = new List<RedactionElsewhere>();
        bool Has(string? text) => text is { Length: > 0 } && phrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
        var searched = new Dictionary<Core.Pdf.Objects.PdfDictionary, List<HiddenTextSpot>>(_hidden, ReferenceEqualityComparer.Instance);   // taken here, on the UI thread

        // Reads from the document, so it runs as work (closing waits for it)
        await WorkAsync(() => Task.Run(() =>
        {
            phrases.UnionWith(HiddenPhrases(searched));
            if (phrases.Count == 0) return;
            var info = Document.Trailer.GetDictionary(Core.Pdf.Objects.PdfName.Info);
            var inInfo = info is not null && info.Entries.Any(e => info.Get(e.Key) is Core.Pdf.Objects.PdfString s && Has(s.ToText()));
            // XMP as XML (entities, attribute values, UTF-16), not as raw text; too large or unreadable: said so (No. 443)
            var unsearchedMetadata = false;
            var catalogXmp = Document.Catalog?.Get(Core.Pdf.Objects.PdfName.Get("Metadata")) as Core.Pdf.Objects.PdfStream;
            var inXmp = catalogXmp is not null && XmpHas(Document, catalogXmp, Has, ref unsearchedMetadata);
            if (inInfo || inXmp) found.Add(new RedactionElsewhere(CleanupItems.Metadata, Tr.T("Dokumenteigenschaften")));
            if (Flatten(Outline).Any(o => Has(o.Title))) found.Add(new RedactionElsewhere(CleanupItems.Bookmarks, Tr.T("Lesezeichen")));

            bool comments = false, fields = false, own = false;
            foreach (var item in History.Pages)
            {
                own |= item.Elements.OfType<TextElement>().Any(t => Has(t.Text));
                if (item.Page.Dictionary.Get(Core.Pdf.Objects.PdfName.Get("Annots")) is not Core.Pdf.Objects.PdfArray annots) continue;
                for (var i = 0; i < annots.Count && i < 10_000; i++)
                {
                    if (annots.Get(i) is not Core.Pdf.Objects.PdfDictionary annot || ElementStore.IsOwn(item.Source, item.Page, annot)) continue;
                    var widget = annot.GetName(Core.Pdf.Objects.PdfName.Subtype)?.Value == "Widget";
                    // Also default and rich text values, the mapping name and the option list (/DV, /RV, /TM, /Opt), on the field or its parent field
                    var parent = annot.GetDictionary(Core.Pdf.Objects.PdfName.Parent);
                    var texts = new[] { "Contents", "T", "RC", "V", "TU", "TM", "DV", "RV" }
                        .SelectMany(k => new[] { annot.Get(Core.Pdf.Objects.PdfName.Get(k)), parent?.Get(Core.Pdf.Objects.PdfName.Get(k)) })
                        .Concat(Options(annot)).Concat(parent is null ? [] : Options(parent))
                        .OfType<Core.Pdf.Objects.PdfString>();
                    if (!texts.Any(s => Has(s.ToText()))) continue;
                    if (widget) fields = true;
                    else comments = true;
                }
            }
            // Embedded pages and pictures: data of the file they came from (/PTEX.InfoDict, /PTEX.FileName); their XMP below with all objects
            var embedded = false;
            foreach (var item in History.Pages)
                foreach (var x in DocumentCleanup.EmbeddedForms(item.Page.Resources))
                    foreach (var (key, _) in x.Dictionary.Entries)
                        if (DocumentCleanup.IsEmbeddingInfo(key) && StringsHave(x.Dictionary.Get(key), Has, 0)) embedded = true;
            if (embedded) found.Add(new RedactionElsewhere(CleanupItems.PrivateData, Tr.T("Angaben eingebundener Seiten")));
            if (comments) found.Add(new RedactionElsewhere(CleanupItems.Comments, Tr.T("Kommentare")));
            if (fields) found.Add(new RedactionElsewhere(CleanupItems.None, Tr.T("Formularfelder")));
            if (own) found.Add(new RedactionElsewhere(CleanupItems.None, Tr.T("eingefügte Texte")));

            // What page redaction does not reach: alternate texts in the structure tree, attachments, layer names, named destinations
            var catalog = Document.Catalog;
            if (catalog?.GetDictionary(Core.Pdf.Objects.PdfName.Get("StructTreeRoot")) is { } root && StructureHas(root, Has))
                found.Add(new RedactionElsewhere(CleanupItems.Structure, Tr.T("Strukturbaum (Ersatztexte)")));
            // Attachments: their content searched as what it is (office files, PDF, UTF-16 text); what cannot be searched is reported
            // all the same, never counted as clean (docs/FEHLERGEDAECHTNIS.md, No. 443)
            var unsearched = new List<string>();
            var named = new HashSet<Core.Pdf.Objects.PdfDictionary>(ReferenceEqualityComparer.Instance);
            var names = catalog?.GetDictionary(Core.Pdf.Objects.PdfName.Get("Names"));
            var inTree = false;
            if (names?.GetDictionary(Core.Pdf.Objects.PdfName.Get("EmbeddedFiles")) is { } files)
                inTree = NameTreeHas(files, Has, (value, has) =>
                {
                    // every entry is visited (not only up to the first hit): those not in the tree are searched below
                    if (value is Core.Pdf.Objects.PdfDictionary spec) named.Add(spec);
                    if (!inTree) inTree = Attachment(Document, value, has, unsearched) == AttachmentSearch.Hit;
                    return false;
                }) || inTree;
            if (inTree) found.Add(new RedactionElsewhere(CleanupItems.Attachments, Tr.T("Dateianhänge")));
            // Everything reachable from the catalogue and the pages kept, wherever it hangs (ISO 32000-2, 14.3.2 and 14.13): files associated
            // with the catalogue, pages, marked content, structure elements, forms, patterns and annotations, the file of an attachment
            // annotation, and the XMP of every object — not only of the places listed before (docs/FEHLERGEDAECHTNIS.md, No. 434 and 443)
            bool associated = false, objectXmp = false;
            var roots = new List<(Core.Pdf.Document.PdfDocument, Core.Pdf.Objects.PdfObject)>();
            if (catalog is not null) roots.Add((Document, catalog));
            foreach (var item in History.Pages) roots.Add((item.Source, item.Page.Dictionary));
            var live = new HashSet<Core.Pdf.Objects.PdfDictionary>(History.Pages.Select(i => i.Page.Dictionary), ReferenceEqualityComparer.Instance);
            ReachableObjects.Visit(roots, live, (doc, obj) =>
            {
                switch (obj)
                {
                    case Core.Pdf.Objects.PdfStream meta when !objectXmp && meta.Dictionary.IsType(Core.Pdf.Objects.PdfName.Metadata) && !ReferenceEquals(meta, catalogXmp):
                        objectXmp = XmpHas(doc, meta, Has, ref unsearchedMetadata);
                        break;
                    case Core.Pdf.Objects.PdfDictionary spec when !associated && !named.Contains(spec) && ReachableObjects.IsFileSpecification(spec):
                        associated = Attachment(doc, spec, Has, unsearched) == AttachmentSearch.Hit;
                        break;
                }
            });
            if (objectXmp) found.Add(new RedactionElsewhere(CleanupItems.Metadata, Tr.T("Metadaten an Objekten")));
            if (unsearchedMetadata) found.Add(new RedactionElsewhere(CleanupItems.Metadata, Tr.T("Metadaten, die nicht durchsucht werden konnten")));
            if (associated) found.Add(new RedactionElsewhere(CleanupItems.Attachments, Tr.T("zugeordnete Dateien")));
            if (unsearched.Count > 0)
            {
                var list = unsearched.Distinct().ToList();
                found.Add(new RedactionElsewhere(CleanupItems.Attachments, Tr.T("Dateianhänge, die nicht durchsucht werden konnten: {0}",
                    string.Join(", ", list.Take(5)) + (list.Count > 5 ? " …" : ""))));
            }
            if (catalog?.GetDictionary(Core.Pdf.Objects.PdfName.Get("OCProperties"))?.GetArray(Core.Pdf.Objects.PdfName.Get("OCGs")) is { } groups &&
                Enumerable.Range(0, Math.Min(groups.Count, 10_000)).Any(i => groups.Get(i) is Core.Pdf.Objects.PdfDictionary g && Has((g.Get(Core.Pdf.Objects.PdfName.Get("Name")) as Core.Pdf.Objects.PdfString)?.ToText())))
                found.Add(new RedactionElsewhere(CleanupItems.None, Tr.T("Ebenennamen")));
            // pages outside the page tree that a script can show (templates): their text is not on any page (docs/RECHERCHE-VERSTECKEN.md, B-1)
            if (DocumentCleanup.TemplatePages(Document).Any(t => Has(Core.Pdf.Text.PageText.Create(Core.Pdf.Content.PageInterpreter.BuildText(Document, t)).Text)))
                found.Add(new RedactionElsewhere(CleanupItems.Scripts, Tr.T("versteckte Seiten (Vorlagen)")));
            if (names?.GetDictionary(Core.Pdf.Objects.PdfName.Get("Dests")) is { } dests && NameTreeHas(dests, Has, null) ||
                catalog?.GetDictionary(Core.Pdf.Objects.PdfName.Get("Dests")) is { } oldDests && oldDests.Entries.Any(e => Has(e.Key.Value)))
                found.Add(new RedactionElsewhere(CleanupItems.None, Tr.T("benannte Ziele")));
            // further places with readable text the bars do not reach (6.9; docs/FEHLERGEDAECHTNIS.md, No. 695): the prefixes of page labels
            // (ISO 32000-2, 12.4.2), the information of articles (12.4.3, /I), table summaries in the structure (14.8.5.7) and scripts — the
            // document's, those of pages, annotations and fields, of the outline (12.6.4.17) — searched as text, never run
            if (catalog?.Get(Core.Pdf.Objects.PdfName.Get("PageLabels")) is Core.Pdf.Objects.PdfDictionary labels && LabelsHave(labels, Has))
                found.Add(new RedactionElsewhere(CleanupItems.None, Tr.T("Seitenbeschriftungen")));
            if (catalog?.Get(Core.Pdf.Objects.PdfName.Get("Threads")) is Core.Pdf.Objects.PdfArray threads &&
                Enumerable.Range(0, Math.Min(threads.Count, 10_000)).Any(i => threads.Get(i) is Core.Pdf.Objects.PdfDictionary t && StringsHave(t.Get(Core.Pdf.Objects.PdfName.Get("I")), Has, 0)))
                found.Add(new RedactionElsewhere(CleanupItems.OtherData, Tr.T("Artikel")));
            if (catalog?.GetDictionary(Core.Pdf.Objects.PdfName.Get("StructTreeRoot")) is { } summaryRoot && SummariesHave(summaryRoot, Has))
                found.Add(new RedactionElsewhere(CleanupItems.Structure, Tr.T("Strukturbaum (Zusammenfassungen von Tabellen)")));
            if (ScriptsHave(Document, Has))
                found.Add(new RedactionElsewhere(CleanupItems.Scripts, Tr.T("Skripte")));
        }));
        return found;

        static IEnumerable<OutlineItem> Flatten(IEnumerable<OutlineItem> items) => items.SelectMany(i => Flatten(i.Children).Prepend(i));
    }

    /// <summary>Page labels: whether the prefix (/P) of a range contains redacted text (the number tree through /Kids, at most 10,000 nodes).</summary>
    private static bool LabelsHave(Core.Pdf.Objects.PdfDictionary tree, Func<string?, bool> has)
    {
        var stack = new Stack<(Core.Pdf.Objects.PdfDictionary Node, int Depth)>([(tree, 0)]);
        for (var visited = 0; stack.Count > 0 && visited < 10_000; visited++)
        {
            var (node, depth) = stack.Pop();
            if (node.GetArray(Core.Pdf.Objects.PdfName.Get("Nums")) is { } nums)
                for (var i = 1; i < nums.Count && i < 20_000; i += 2)
                    if (nums.Get(i) is Core.Pdf.Objects.PdfDictionary label && has((label.Get(Core.Pdf.Objects.PdfName.Get("P")) as Core.Pdf.Objects.PdfString)?.ToText()))
                        return true;
            if (depth < 16 && node.GetArray(Core.Pdf.Objects.PdfName.Get("Kids")) is { } kids)
                for (var i = 0; i < kids.Count && i < 10_000; i++)
                    if (kids.Get(i) is Core.Pdf.Objects.PdfDictionary kid) stack.Push((kid, depth + 1));
        }
        return false;
    }

    /// <summary>Structure tree: whether the /Summary of an element's attributes (an attribute object or an array of them) contains redacted text.</summary>
    private static bool SummariesHave(Core.Pdf.Objects.PdfDictionary root, Func<string?, bool> has)
    {
        bool InAttributes(Core.Pdf.Objects.PdfObject? attributes) => attributes switch
        {
            Core.Pdf.Objects.PdfDictionary a => has((a.Get(Core.Pdf.Objects.PdfName.Get("Summary")) as Core.Pdf.Objects.PdfString)?.ToText()),
            Core.Pdf.Objects.PdfArray list => Enumerable.Range(0, Math.Min(list.Count, 1000)).Any(i => list.Get(i) is Core.Pdf.Objects.PdfDictionary a &&
                has((a.Get(Core.Pdf.Objects.PdfName.Get("Summary")) as Core.Pdf.Objects.PdfString)?.ToText())),
            _ => false,
        };
        var queue = new Queue<Core.Pdf.Objects.PdfObject?>([root.Get(Core.Pdf.Objects.PdfName.Get("K"))]);
        var seen = new HashSet<Core.Pdf.Objects.PdfDictionary>(ReferenceEqualityComparer.Instance);
        while (queue.Count > 0 && seen.Count < 50_000)
            switch (queue.Dequeue())
            {
                case Core.Pdf.Objects.PdfArray kids:
                    for (var i = 0; i < kids.Count && i < 10_000; i++) queue.Enqueue(kids.Get(i));
                    break;
                case Core.Pdf.Objects.PdfDictionary element when seen.Add(element):
                    if (InAttributes(element.Get(Core.Pdf.Objects.PdfName.Get("A")))) return true;
                    queue.Enqueue(element.Get(Core.Pdf.Objects.PdfName.Get("K")));
                    break;
            }
        return false;
    }

    /// <summary>Scripts as text (never run): <see cref="Core.Pdf.Document.DocumentScripts"/>.</summary>
    private static bool ScriptsHave(Core.Pdf.Document.PdfDocument doc, Func<string?, bool> has) => Core.Pdf.Document.DocumentScripts.Any(doc, has);

    /// <summary>Whether a string in the object (directly, or in a dictionary or array up to three levels deep) contains redacted text.</summary>
    private static bool StringsHave(Core.Pdf.Objects.PdfObject? value, Func<string?, bool> has, int depth) => value switch
    {
        Core.Pdf.Objects.PdfString s => has(s.ToText()),
        Core.Pdf.Objects.PdfDictionary d when depth < 3 => d.Entries.Take(1000).Any(e => StringsHave(d.Get(e.Key), has, depth + 1)),
        Core.Pdf.Objects.PdfArray a when depth < 3 => Enumerable.Range(0, Math.Min(a.Count, 1000)).Any(i => StringsHave(a.Get(i), has, depth + 1)),
        _ => false,
    };

    /// <summary>Entries of a field's option list (/Opt: text or [export value, text]), at most 10,000.</summary>
    private static IEnumerable<Core.Pdf.Objects.PdfObject?> Options(Core.Pdf.Objects.PdfDictionary field)
    {
        if (field.Get(Core.Pdf.Objects.PdfName.Get("Opt")) is not Core.Pdf.Objects.PdfArray options) yield break;
        for (var i = 0; i < options.Count && i < 10_000; i++)
            if (options.Get(i) is Core.Pdf.Objects.PdfArray pair) { yield return pair.Get(0); if (pair.Count > 1) yield return pair.Get(1); }
            else yield return options.Get(i);
    }

    /// <summary>Alternate texts in the structure tree (/Alt, /ActualText, /E, /T); at most 50,000 elements are visited.</summary>
    private static bool StructureHas(Core.Pdf.Objects.PdfDictionary root, Func<string?, bool> has)
    {
        var queue = new Queue<Core.Pdf.Objects.PdfObject?>([root.Get(Core.Pdf.Objects.PdfName.Get("K"))]);
        var seen = new HashSet<Core.Pdf.Objects.PdfDictionary>(ReferenceEqualityComparer.Instance);
        while (queue.Count > 0 && seen.Count < 50_000)
            switch (queue.Dequeue())
            {
                case Core.Pdf.Objects.PdfArray kids:
                    for (var i = 0; i < kids.Count && i < 10_000; i++) queue.Enqueue(kids.Get(i));
                    break;
                case Core.Pdf.Objects.PdfDictionary element when seen.Add(element):
                    foreach (var key in (ReadOnlySpan<string>)["Alt", "ActualText", "E", "T"])
                        if (has((element.Get(Core.Pdf.Objects.PdfName.Get(key)) as Core.Pdf.Objects.PdfString)?.ToText())) return true;
                    queue.Enqueue(element.Get(Core.Pdf.Objects.PdfName.Get("K")));
                    break;
            }
        return false;
    }

    /// <summary>Name tree: whether a name (or, with <paramref name="value"/>, its value) contains redacted text; at most 10,000 entries and 16 levels.</summary>
    private static bool NameTreeHas(Core.Pdf.Objects.PdfDictionary tree, Func<string?, bool> has, Func<Core.Pdf.Objects.PdfObject?, Func<string?, bool>, bool>? value)
    {
        var stack = new Stack<(Core.Pdf.Objects.PdfDictionary Node, int Depth)>([(tree, 0)]);
        var count = 0;
        while (stack.Count > 0 && count < 10_000)
        {
            var (node, depth) = stack.Pop();
            if (node.GetArray(Core.Pdf.Objects.PdfName.Get("Names")) is { } pairs)
                for (var i = 0; i + 1 < pairs.Count && count < 10_000; i += 2, count++)
                    if (has((pairs.Get(i) as Core.Pdf.Objects.PdfString)?.ToText()) || value?.Invoke(pairs.Get(i + 1), has) == true) return true;
            if (depth < 16 && node.GetArray(Core.Pdf.Objects.PdfName.Kids) is { } kids)
                for (var i = 0; i < kids.Count && i < 10_000; i++)
                    if (kids.Get(i) is Core.Pdf.Objects.PdfDictionary kid) stack.Push((kid, depth + 1));
        }
        return false;
    }

    /// <summary>
    /// Attachment: whether the file name, description or content of any of its files contains redacted text (content by
    /// <see cref="AttachmentText"/>). Not searchable (too large, locked, an unknown binary kind): the name is added to
    /// <paramref name="unsearched"/>.
    /// </summary>
    /// <param name="doc">The document the file specification belongs to (pages of other files bring their own).</param>
    private static AttachmentSearch Attachment(Core.Pdf.Document.PdfDocument doc, Core.Pdf.Objects.PdfObject? value, Func<string?, bool> has, List<string> unsearched)
    {
        if (value is not Core.Pdf.Objects.PdfDictionary spec) return AttachmentSearch.NoHit;
        foreach (var key in (ReadOnlySpan<string>)["F", "UF", "Desc"])
            if (has((spec.Get(Core.Pdf.Objects.PdfName.Get(key)) as Core.Pdf.Objects.PdfString)?.ToText())) return AttachmentSearch.Hit;
        if (spec.GetDictionary(Core.Pdf.Objects.PdfName.Get("EF")) is not { } ef) return AttachmentSearch.NoHit;
        var result = AttachmentSearch.NoHit;
        var seen = new HashSet<Core.Pdf.Objects.PdfStream>(ReferenceEqualityComparer.Instance);
        foreach (var (key, _) in ef.Entries.Take(10))   // F, UF and the old platform entries: each can hold a file of its own
        {
            if (ef.Get(key) is not Core.Pdf.Objects.PdfStream file || !seen.Add(file)) continue;
            AttachmentSearch found;
            try
            {
                found = file.DataLength > AttachmentText.MaxStored ? AttachmentSearch.NotSearchable : AttachmentText.Search(doc.GetStreamData(file), has);
            }
            catch (Core.Pdf.PdfException)
            {
                found = AttachmentSearch.NotSearchable;   // locked with a password of its own, or damaged
            }
            if (found == AttachmentSearch.Hit) return found;
            if (found == AttachmentSearch.NotSearchable) result = found;
        }
        if (result == AttachmentSearch.NotSearchable)
        {
            var name = ((spec.Get(Core.Pdf.Objects.PdfName.Get("UF")) ?? spec.Get(Core.Pdf.Objects.PdfName.Get("F"))) as Core.Pdf.Objects.PdfString)?.ToText();
            unsearched.Add(string.IsNullOrWhiteSpace(name) ? Tr.T("ohne Namen") : name.Length > 60 ? name[..60] + "…" : name);
        }
        return result;
    }

    /// <summary>Whether an XMP packet contains redacted text, read as XML (up to 16 MB); larger or unreadable: <paramref name="unsearched"/>.</summary>
    private static bool XmpHas(Core.Pdf.Document.PdfDocument doc, Core.Pdf.Objects.PdfStream xmp, Func<string?, bool> has, ref bool unsearched)
    {
        if (xmp.DataLength > 16 << 20)
        {
            unsearched = true;
            return false;
        }
        try
        {
            return AttachmentText.XmpHas(doc.GetStreamData(xmp), has);
        }
        catch (Core.Pdf.PdfException)
        {
            unsearched = true;
            return false;
        }
    }

    /// <summary>Redacted pieces of text and their words (see <see cref="AddPhrases"/>), case-insensitive.</summary>
    internal async Task<HashSet<string>> RedactedPhrasesAsync() => (await RedactedTextAsync()).Phrases;

    /// <summary>The redacted text as search terms, and which of them are whole pieces (not only a word of one).</summary>
    private async Task<(HashSet<string> Phrases, HashSet<string> Pieces)> RedactedTextAsync()
    {
        var phrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pieces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 0; page < Pages.Count; page++)
        {
            var areas = MarkAreas(page, withHidden: false);
            if (areas.Count == 0) continue;
            var text = await GetTextAsync(page);
            var run = new System.Text.StringBuilder();
            var pending = new System.Text.StringBuilder();   // spaces without an area of their own: included if the run continues
            for (var c = 0; c <= text.Text.Length; c++)
            {
                if (c < text.Text.Length)
                {
                    var range = new TextRange(c, 1);
                    if (text.Quads(range).Count == 0)
                    {
                        if (run.Length > 0) pending.Append(text.Text[c]);
                        continue;
                    }
                    if (Covered(text, range, areas))
                    {
                        run.Append(pending).Append(text.Text[c]);
                        pending.Clear();
                        continue;
                    }
                }
                AddPhrases(run.ToString(), phrases, pieces);
                run.Clear();
                pending.Clear();
            }
        }
        return (phrases, pieces);
    }

    /// <summary>
    /// The text of the hidden spots under marks of hidden content, as search terms (docs/FEHLERGEDAECHTNIS.md, No. 146): the same name
    /// may also stand in the document properties or a bookmark. Invisible text (OCR) does not count — it repeats what the page shows.
    /// </summary>
    /// <param name="searched">Spots already found by the tool, per page (searched again where missing).</param>
    private HashSet<string> HiddenPhrases(Dictionary<Core.Pdf.Objects.PdfDictionary, List<HiddenTextSpot>> searched)
    {
        var phrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pages = History.Pages;
        for (var page = 0; page < pages.Count; page++)
        {
            var marks = pages[page].Elements.OfType<RedactionElement>().Where(r => r.Hidden).ToList();
            if (marks.Count == 0) continue;
            var item = pages[page];
            List<HiddenTextSpot> spots;
            if (!searched.TryGetValue(item.Page.Dictionary, out var known))
            {
                using var use = Renderer.Use(item.Source);
                if (use is null) continue;
                try
                {
                    spots = HiddenText.Find(item.Source, item.Page);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    continue;   // not searchable: the mark itself still removes what it finds
                }
            }
            else
            {
                spots = known;
            }
            foreach (var spot in spots)
                if (spot.Kind != HiddenTextKind.Invisible && spot.Lines.All(line => marks.Any(m => Contains(m, line))))
                    AddPhrases(spot.Text.ReplaceLineEndings(" "), phrases);
        }
        return phrases;
    }

    private static string Trimmed(string text) => text.Trim().Trim(',', '.', ';', ':', '(', ')', '"', '\'', '-');

    /// <summary>A redacted piece of text as search terms: the piece itself (3 or more characters) and its words of 4 or more characters.</summary>
    private static void AddPhrases(string run, HashSet<string> phrases, HashSet<string>? pieces = null)
    {
        var trimmed = Trimmed(run.ReplaceLineEndings(" "));
        if (trimmed.Length >= 3)
        {
            phrases.Add(trimmed);
            pieces?.Add(OneLine(trimmed));
        }
        // marks on consecutive lines read as one run: each line is a piece as well, or „Einrichtung der B“ would only be looked for by its words
        foreach (var line in run.ReplaceLineEndings("\n").Split('\n'))
        {
            var part = Trimmed(line);
            if (part.Length < 3 || part.Length == trimmed.Length) continue;
            phrases.Add(part);
            pieces?.Add(OneLine(part));
        }
        foreach (var word in trimmed.Split([' ', ',', ';', ':', '(', ')', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            var w = word.Trim('.', '-', '"', '\'');
            if (w.Length >= 4) phrases.Add(w);
        }
    }

    /// <summary>
    /// Areas of the redaction marks of a page. <paramref name="withHidden"/> false: without marks of hidden content — they remove only
    /// what nobody sees, so the visible text in their area is neither redacted text to search for elsewhere nor covered by them.
    /// </summary>
    private List<Core.Pdf.Graphics.RectD> MarkAreas(int page, bool withHidden = true) =>
        [.. History.Pages[page].Elements.OfType<RedactionElement>().Where(r => withHidden || !r.Hidden).SelectMany(r => r.PageAreas)
            .Select(a => Core.Pdf.Graphics.RectD.FromLtrb(a.Left, a.Bottom, a.Right, a.Top))];

    /// <summary>Whether all characters of the range lie under a redaction (as far as redaction would remove them).</summary>
    private static bool Covered(PageText text, TextRange range, List<Core.Pdf.Graphics.RectD> areas)
    {
        // Per character as in redaction: covered to 30 % in width and height (characters without an area, such as spaces, do not count)
        var any = false;
        for (var c = range.Start; c < range.End; c++)
        {
            var quads = text.Quads(new TextRange(c, 1));
            if (quads.Count == 0) continue;
            any = true;
            if (!quads.All(q => Redactor.CoversCharacter(areas, q.Bounds))) return false;
        }
        return any;
    }

    // ------------------------------------------------------------ Applying

    /// <summary>Number of marked redactions in the whole document.</summary>
    public int RedactionCount => History.Pages.Sum(p => p.Elements.Count(e => e is RedactionElement));

    /// <summary>Number of pages with redactions.</summary>
    public int RedactedPageCount => History.Pages.Count(p => p.Elements.Any(e => e is RedactionElement));

    // ------------------------------------------------------------ Redacting and cleaning up within the document („Schwärzen zuerst“)

    /// <summary>
    /// Main documents from <see cref="ApplyRedactionsAsync"/> and <see cref="CleanAsync"/> (in memory), as long as the history needs
    /// them: whether each contains unsaved redactions (also after a cleanup on top), and what has been cleaned up in it (this still
    /// applies when saving, otherwise the writer would put back e.g. producer and date).
    /// </summary>
    private readonly Dictionary<PdfDocument, (bool Redacted, CleanupItems Cleaned)> _redactedDocuments = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// States of the document whose attached files described in the metadata were kept when cleaning up (an e-invoice not ticked, decision (4)
    /// of 15.11.2026): „Metadaten“ cleaned again on saving keeps what they need (<see cref="PdfSaveOptions.AttachedFileMetadataStays"/>).
    /// </summary>
    private readonly HashSet<PdfDocument> _attachedMetadataKept = new(ReferenceEqualityComparer.Instance);

    /// <summary>Whether the current state keeps the metadata of attached files that stayed (see <see cref="_attachedMetadataKept"/>).</summary>
    internal bool AttachedMetadataKept => _attachedMetadataKept.Contains(Document);

    /// <summary>What has already been cleaned up in the current state (see <see cref="CleanAsync"/>).</summary>
    internal CleanupItems Cleaned => _redactedDocuments.TryGetValue(Document, out var state) ? state.Cleaned : CleanupItems.None;

    /// <summary>
    /// Whether redaction has been applied within the document and not yet saved: saving over the file then deletes the content
    /// underneath there for good (asked when saving). false again after undoing past the redaction.
    /// </summary>
    public bool HasAppliedRedactions => _redactedDocuments.TryGetValue(Document, out var state) && state.Redacted;

    /// <summary>
    /// Applies the marked redactions within the open document as one step that Ctrl+Z undoes. Afterwards the view shows the
    /// redacted state, and everything that leaves the document comes from it. The intermediate states exist only in memory.
    /// </summary>
    /// <param name="asImages">Additionally turn the redacted pages into images (no hidden remnants, no text left on them).</param>
    /// <param name="cleanup">Clean up at the same time (e.g. the redacted name in the title).</param>
    /// <param name="hideLengths">Widen the gaps of redacted words to fixed steps, so that their bars do not tell their length
    /// (docs/RECHERCHE-VERSTECKEN.md, B-13); pages where the line has too little room for that are reported.</param>
    /// <param name="cancel">Cancels the operation.</param>
    /// <param name="reasons">What to do with the reasons of the redactions (in the bars, notes, protocol pages); null: nothing.</param>
    public Task ApplyRedactionsAsync(bool asImages, CleanupItems cleanup = CleanupItems.None, bool hideLengths = false, CancellationToken cancel = default,
        CleanupScope? scoped = null, RedactionReasonSettings? reasons = null) =>
        InMemoryStepAsync(Tr.T("Schwärzungen anwenden"), redact: true, cleanup, asImages, cancel, hideLengths, scoped, reasons: reasons);

    /// <summary>
    /// Cleans up the document immediately within the open document rather than on the next save, as one undoable step; this way it
    /// also applies to printing, copying and every other output (if it were only pending, „Drucken als PDF“ and copying would still
    /// include what is to be removed). Marked redactions stay marks.
    /// </summary>
    /// <param name="items">What to clean up.</param>
    /// <param name="cancel">Cancels the operation.</param>
    /// <param name="keep">Places that stay although their item is removed (the „x“ in the area „Dokument bereinigen“); null: none.</param>
    /// <param name="keptAttachments">With „Anhänge“: embedded files that stay (a module's row not ticked, e.g. an e-invoice); null: none.</param>
    /// <param name="attachedMetadataStays">The metadata describing attached files stay (the files they describe stay).</param>
    public Task CleanAsync(CleanupItems items, CancellationToken cancel = default, IReadOnlyList<CleanupKeep>? keep = null,
        IReadOnlySet<Core.Pdf.Objects.PdfObjectId>? keptAttachments = null, bool attachedMetadataStays = false) =>
        InMemoryStepAsync(Tr.T("Dokument bereinigen"), redact: false, items, asImages: false, cancel, keep: keep, keptAttachments: keptAttachments,
            attachedMetadataStays: attachedMetadataStays);

    private Task InMemoryStepAsync(string description, bool redact, CleanupItems cleanup, bool asImages, CancellationToken cancel, bool hideLengths = false,
        CleanupScope? scoped = null, IReadOnlyList<CleanupKeep>? keep = null, RedactionReasonSettings? reasons = null,
        IReadOnlySet<Core.Pdf.Objects.PdfObjectId>? keptAttachments = null, bool attachedMetadataStays = false)
    {
        var file = FileName;
        // an item with places that stay is not remembered as cleaned: saving would clean it again, those places included
        var remembered = keep is { Count: > 0 } ? cleanup & ~keep.Aggregate(CleanupItems.None, (all, k) => all | k.Item) : cleanup;
        // likewise „Anhänge“ with files that stay (an e-invoice not ticked): saving would remove them after all
        if (keptAttachments is { Count: > 0 }) remembered &= ~CleanupItems.Attachments;
        // the metadata of attached files kept stay — also in this step if an earlier one kept them and this one removes no attachment
        var metadataStays = attachedMetadataStays || AttachedMetadataKept && !cleanup.HasFlag(CleanupItems.Attachments);
        var idle = new List<int>();   // pages whose marks of hidden content found nothing left to remove
        var partly = new List<int>(); // pages where the length of a redacted word could be hidden only in part
        var imagesKept = new List<int>(); // pages with images whose hidden parts could not be cut away
        return WorkAsync(() =>
        {
            // Take the state only here: any previous work has finished by then, and the history stays locked until the end
            var pages = History.Pages.ToArray();
            var main = Document;
            // what was cleaned on some pages only is not remembered as cleaned: a later save must not clean the other pages as well
            // redacting with the metadata cleaned: the title goes as well — it may hold what was redacted (docs/FEHLERGEDAECHTNIS.md, No. 492)
            var options = SaveOptions(SaveVersion) with { Cleanup = cleanup | Cleaned, ScopedCleanup = scoped, HideRedactedLengths = redact && hideLengths, KeepPlaces = keep,
                KeepTitle = KeepTitleOnCleanup && !redact, KeptAttachments = keptAttachments, AttachedFileMetadataStays = metadataStays };
            var password = _password;
            idle.Clear();
            partly.Clear();
            imagesKept.Clear();
            return Task.Run(() =>
            {
                var (applied, adopted) = InDocumentRedaction.Apply(main, pages, options, password, cancel, redact, r =>
                {
                    idle.AddRange(r.IdleHiddenMarks);
                    partly.AddRange(r.LengthsPartlyShown);
                    imagesKept.AddRange(r.ImagePartsKept);
                });
                var result = (applied, adopted);
                if (redact && asImages && pages.Any(p => p.Elements.Any(e => e is RedactionElement)))
                    using (applied) result = RedactedAsImages(applied, pages, options, password, cancel);
                // the reasons: in the bars, as notes, on protocol pages (KONZEPT.md, section 5.6, B-14)
                if (redact && reasons is { Any: true } && RedactionReasons.Apply(result.Item1, pages, reasons, file, options, password, cancel) is { } reasoned)
                {
                    result.Item1.Dispose();
                    result = reasoned;
                }
                return result;
            }, cancel);
        }, result =>
        {
            var (doc, items) = result;
            if (_closed)
            {
                doc.Dispose();   // the tab has been closed in the meantime
                return Task.CompletedTask;
            }
            _redactedDocuments[doc] = (redact || HasAppliedRedactions, remembered | Cleaned);
            if (metadataStays) _attachedMetadataKept.Add(doc);
            History.ReplaceDocument(description, doc, items);
            TrimMemoryStates();
            if (idle.Count > 0)
            {
                var numbers = string.Join(", ", idle.Take(5).Select(i => i + 1)) + (idle.Count > 5 ? " …" : "");
                Notify(idle.Count == 1
                    ? Tr.T("Auf Seite {0} lag unter der Markierung kein verborgener Text mehr — dort wurde nichts entfernt.", numbers)
                    : Tr.T("Auf {0} Seiten ({1}) lag unter den Markierungen kein verborgener Text mehr — dort wurde nichts entfernt.", idle.Count, numbers));
            }
            if (partly.Count > 0)
            {
                var numbers = string.Join(", ", partly.Take(5).Select(i => i + 1)) + (partly.Count > 5 ? " …" : "");
                Notify(partly.Count == 1
                    ? Tr.T("Auf Seite {0} war in einer Zeile zu wenig Platz: dort lässt der Balken die Länge des geschwärzten Worts noch ungefähr erkennen.", numbers)
                    : Tr.T("Auf {0} Seiten ({1}) war in einer Zeile zu wenig Platz: dort lässt der Balken die Länge des geschwärzten Worts noch ungefähr erkennen.", partly.Count, numbers));
            }
            if (imagesKept.Count > 0)
            {
                var numbers = string.Join(", ", imagesKept.Take(5).Select(i => i + 1)) + (imagesKept.Count > 5 ? " …" : "");
                Notify(imagesKept.Count == 1
                    ? Tr.T("Auf Seite {0} ließ sich ein Bild nicht zuschneiden (Schablone, zu groß oder nicht lesbar) — sein verdeckter Teil ist noch in der Datei.", numbers)
                    : Tr.T("Auf {0} Seiten ({1}) ließen sich Bilder nicht zuschneiden (Schablone, zu groß oder nicht lesbar) — ihre verdeckten Teile sind noch in der Datei.", imagesKept.Count, numbers));
            }
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Turns the redacted pages of <paramref name="applied"/> into images: images in memory replace the pages (bookmarks and links
    /// then point to them), and the new state is built from that. Foreign annotations of these pages are part of the image; own
    /// elements are placed on top again.
    /// </summary>
    private static (PdfDocument, PageItem[]) RedactedAsImages(PdfDocument applied, PageItem[] before, PdfSaveOptions options, string? password,
        CancellationToken cancel)
    {
        var redacted = Enumerable.Range(0, before.Length).Where(i => before[i].Elements.Any(e => e is RedactionElement)).ToList();
        var pages = PageItem.From(applied);
        using var images = new MemoryPdf();
        Rendering.RasterExport.Write([.. redacted.Select(i => pages[i])], images, new Rendering.RasterSettings(), cancel: cancel);
        using var imageDoc = images.OpenDocument();
        for (var k = 0; k < redacted.Count; k++) pages[redacted[k]] = new PageItem(imageDoc, imageDoc.Pages[k]).StandingFor(pages[redacted[k]]);
        var doc = InDocumentRedaction.Write(applied, pages, options with
        {
            ApplyRedactions = false,
            Flatten = false,
            Protection = null,
            KeepEncryption = true,
            Cleanup = CleanupItems.None,
            Compression = null,
            CompressionCache = null,
        }, password, cancel);
        try
        {
            return (doc, InDocumentRedaction.Adopt(doc, before, redacted.ToHashSet()));
        }
        catch
        {
            doc.Dispose();
            throw;
        }
    }

    /// <summary>Gives every redaction mark without a reason this one, as one undo step (KONZEPT.md, section 5.6, B-14).</summary>
    /// <param name="reason">The reason.</param>
    public void SetRedactionReasons(Core.Pdf.Editing.ReasonChoice reason)
    {
        var changes = new List<(int Page, PageElement Old, PageElement Updated)>();
        for (var p = 0; p < History.Pages.Count; p++)
            foreach (var mark in History.Pages[p].Elements.OfType<RedactionElement>())
                if (!mark.Hidden && mark.Reason is null) changes.Add((p, mark, reason.On(mark)));
        if (changes.Count == 0) return;
        var selected = SelectedElement;
        History.ReplaceElements(changes, Tr.T("Schwärzungsgrund für alle"));
        // the selected mark stays selected (with its successor if it was one of them)
        if (selected is { } s && changes.FirstOrDefault(c => ReferenceEquals(c.Old, s.Element)) is { Updated: { } now }) SelectedElement = new ElementRef(s.Page, now);
    }

    /// <summary>Budget for intermediate states in memory: a quarter of the available memory, 256 MB to 4 GB.</summary>
    private static long MemoryStateBudget => Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4, 256L << 20, 4L << 30);

    /// <summary>
    /// Every in-memory step holds a whole file (up to 200 copies; 92 MB per step for a scan). Above the budget the oldest undo steps
    /// are dropped (never the current state), and whatever no step needs any more is released.
    /// </summary>
    private void TrimMemoryStates()
    {
        long Used() => _redactedDocuments.Keys.Where(d => ReferenceEquals(d, Document) || History.UsesMain(d)).Sum(d => d.FileSize);
        var dropped = false;
        while (Used() > MemoryStateBudget && History.DropOldest()) dropped = true;
        if (!dropped) return;
        OnPropertyChanged(nameof(CanUndo));
        _ = ReleaseRedactedAsync();
    }

    /// <summary>Switches the main document (redacting within the document, undoing past it): text, bookmarks and properties are reread.</summary>
    private void SwitchDocument(PdfDocument main)
    {
        Document = main;
        ResetText();
        _outline = null;
        OnPropertyChanged(nameof(Outline));
        OnPropertyChanged(nameof(HasOutline));
        OnPropertyChanged(nameof(PdfVersion));
        OnPropertyChanged(nameof(Properties));
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(IsProtected));
        OnPropertyChanged(nameof(CanChangeProtection));
        OnPropertyChanged(nameof(HasAppliedRedactions));
    }

    /// <summary>
    /// Releases redacted states that no step of the history needs any more (after undo and a new change) as soon as no background
    /// reader uses them; each holds a whole file in memory.
    /// </summary>
    private async Task ReleaseRedactedAsync()
    {
        var unused = _redactedDocuments.Keys.Where(d => !ReferenceEquals(d, Document) && !History.UsesMain(d)).ToList();
        // Also the opened file once no step needs it any more (more than 200 steps later); otherwise it would stay memory-mapped until
        // the program exits, and its old version, renamed away when overwriting, could not be deleted
        if (_fileDocument is { } file && !ReferenceEquals(file, Document) && !History.UsesMain(file) && !History.Sources().Contains(file))
        {
            unused.Add(file);
            _fileDocument = null;
        }
        if (unused.Count == 0) return;
        foreach (var doc in unused) _redactedDocuments.Remove(doc);
        await Renderer.WaitUntilUnused(unused);
        foreach (var doc in unused) doc.Dispose();
    }

    /// <summary>
    /// Selected text for copying, without the characters under redactions (they must not leave the document).
    /// </summary>
    /// <returns>The text, and <c>Omitted</c> = true if something was left out.</returns>
    public (string Text, bool Omitted) CopyableSelectedText()
    {
        if (!HasTextSelection || TryGetText(SelectionPage) is not { } text) return ("", false);
        var areas = MarkAreas(SelectionPage);
        if (areas.Count == 0) return (text.GetText(Selection), false);
        var result = new System.Text.StringBuilder();
        var omitted = false;
        var start = Selection.Start;
        for (var c = Selection.Start; c <= Selection.End; c++)
        {
            if (c < Selection.End && !Covered(text, new TextRange(c, 1), areas)) continue;
            // Character c lies under a redaction (or the end is reached): take the section before it
            if (c > start) result.Append(text.GetText(new TextRange(start, c - start)));
            if (c < Selection.End) omitted = true;
            start = c + 1;
        }
        return (result.ToString(), omitted);
    }
}

/// <summary>
/// The question „Jetzt zuerst vollständig schwärzen oder weiter markieren?“, asked before everything that leaves the document
/// (printing, exporting, save as …) and for the „Schwärzungen anwenden“ button.
/// </summary>
/// <param name="Marks">Number of marked redactions.</param>
/// <param name="Pages">Number of pages with redactions.</param>
/// <param name="Action">What happens afterwards (translated, e.g. „Drucken“); null = only redact.</param>
/// <param name="CanChooseImages">Whether to offer „Zusätzlich als Bild“ (not if all pages become images anyway).</param>
public sealed record RedactionQuestion(int Marks, int Pages, string? Action, bool CanChooseImages)
{
    /// <summary>Pages (index) with invisible text, usually the text recognition (OCR) of a scan: offers „Alte Texterkennung entfernen“.</summary>
    public IReadOnlyList<int> OcrPages { get; init; } = [];

    /// <summary>Redacted text that still appears unredacted elsewhere (see <see cref="DocumentViewModel.FindUnredactedAsync"/>).</summary>
    public IReadOnlyList<RedactionHit> Unredacted { get; init; } = [];

    /// <summary>Pages (index) with marks at the time of the question: there the old text recognition goes with „nur auf Seiten mit Schwärzung“.</summary>
    public IReadOnlySet<int> RedactedPages { get; init; } = new HashSet<int>();

    /// <summary>Jumps to a hit and frames it (null: takes the frame away); null without a view.</summary>
    public Func<RedactionHit?, Task>? ShowHit { get; init; }

    /// <summary>Whether an invisible hit goes anyway with the old text recognition, whatever is ticked for the hits.</summary>
    /// <param name="hit">The hit.</param>
    /// <param name="removeOcr">„Alte Texterkennung entfernen“ is ticked.</param>
    /// <param name="wholeDocument">„im ganzen Dokument“ is chosen.</param>
    public bool GoesAlong(RedactionHit hit, bool removeOcr, bool wholeDocument) =>
        hit.Invisible && removeOcr && OcrPages.Contains(hit.Page) && (wholeDocument || RedactedPages.Contains(hit.Page));

    /// <summary>Redacted text outside the page text (see <see cref="DocumentViewModel.FindElsewhereAsync"/>).</summary>
    public IReadOnlyList<RedactionElsewhere> Elsewhere { get; init; } = [];

    /// <summary>Codes covered only in part or holding redacted text (see <see cref="DocumentViewModel.FindCodesToRedactAsync"/>).</summary>
    public IReadOnlyList<RedactionCode> Codes { get; init; } = [];

    /// <summary>Pages (index) whose codes could not be checked in time (see <see cref="DocumentViewModel.FindCodesToRedactAsync"/>).</summary>
    public IReadOnlyList<int> UncheckedCodePages { get; init; } = [];

    /// <summary>The sizes of the text recognition that are there (both come with the program; tests give their own).</summary>
    public Func<IReadOnlyList<Core.Ocr.OcrModelSize>> InstalledOcr { get; init; } = () => Core.Ocr.OcrModels.Installed;

    /// <summary>The size of the text recognition chosen last time (small unless chosen otherwise).</summary>
    public Core.Ocr.OcrModelSize OcrSize { get; init; } = Core.Ocr.OcrModelSize.Small;

    /// <summary>How many marks have a reason of their own (KONZEPT.md, section 5.6, B-14).</summary>
    public int OwnReasons { get; init; }

    /// <summary>How many of them carry a redaction code (written into the bar as set when marking; wish of 4.11.2026).</summary>
    public int CodedReasons { get; init; }

    /// <summary>The reasons offered for the whole document.</summary>
    public IReadOnlyList<string> ReasonSuggestions { get; init; } = [];

    /// <summary>The name or initials entered last time for the protocol; may be empty.</summary>
    public string RedactionName { get; init; } = "";

    /// <summary>What is redacted (words and phrases): a reason or name containing them is warned about.</summary>
    public IReadOnlyCollection<string> RedactedPhrases { get; init; } = [];

    /// <summary>
    /// The bars of the marks without a reason of their own (page space, the largest area of each): the reason for all goes into them, and
    /// where it does not fit the window says so (No. 529).
    /// </summary>
    public IReadOnlyList<Core.Pdf.Document.PdfRectangle> BarsWithoutReason { get; init; } = [];
}

/// <summary>A location outside the page text and the „Dokument bereinigen“ item that removes it (None: by hand).</summary>
/// <param name="Cleanup">The item that removes it; None: by hand.</param>
/// <param name="Where">Where (in the list „Geschwärzter Text steht auch in: …“), or with <paramref name="Sentence"/> a sentence of its own.</param>
/// <param name="KeepWarning">Said in the warning colour when „Gleich mit entfernen“ is unticked (what stays then); null: nothing.</param>
/// <param name="Sentence">A module's entry, named whatever the search found (e.g. an e-invoice, whose XML writes values otherwise): shown as
/// a sentence of its own, not in the list of places where the text was found.</param>
public sealed record RedactionElsewhere(CleanupItems Cleanup, string Where, string? KeepWarning = null, bool Sentence = false);

/// <summary>
/// Reply: redact or keep marking, whether to redact the unredacted occurrences too, whether to clean up the locations outside, which of
/// the reported codes get a bar over the whole code, whether to hide the length of redacted words, whether to remove what nobody sees and
/// the old text recognition along with it, and where (KONZEPT.md, section 5.6, „Schwärzen-Umbau“). The safe choices are the defaults.
/// </summary>
/// <param name="AlsoUnredacted">Redact the other places of exactly the marked text too.</param>
/// <param name="AlsoWords">Redact the places of single words of it too (not chosen at first: a word may stand there for something else).</param>
/// <param name="NewOcr">Recognise the text anew on the pages whose old text recognition was removed or that become images, from what stays
/// visible, with this size; null: not.</param>
/// <param name="Reasons">The reasons of the redactions: for all marks without their own, and where they go (bars, notes, protocol); null:
/// none.</param>
public sealed record RedactionReply(RedactionAnswer Answer, bool AlsoUnredacted = false, bool CleanElsewhere = false, IReadOnlyList<RedactionCode>? Codes = null,
    bool HideLengths = true, bool CleanHidden = true, bool RemoveOcr = true, bool WholeDocument = true, Core.Ocr.OcrModelSize? NewOcr = null,
    RedactionReasonSettings? Reasons = null, bool AlsoWords = false);

/// <summary>Answer to <see cref="RedactionQuestion"/>.</summary>
public enum RedactionAnswer
{
    /// <summary>Keep marking: nothing is redacted, and what was to follow (printing …) does not happen.</summary>
    KeepMarking,

    /// <summary>Apply the redactions.</summary>
    Apply,

    /// <summary>Apply the redactions and turn the redacted pages into images.</summary>
    ApplyAsImages,
}

/// <summary>Question when saving over the file after redaction has been applied within the document.</summary>
/// <param name="FileName">Name of the file.</param>
/// <param name="InSyncFolder">The file lies in a sync folder (OneDrive …): overwritten versions remain in the version history.</param>
public sealed record RedactedSaveQuestion(string FileName, bool InSyncFolder);

/// <summary>Question before a save that invalidates digital signatures.</summary>
/// <param name="FileName">Name of the file.</param>
/// <param name="Count">Number of intact digital signatures.</param>
/// <param name="OverwritesOriginal">The save would replace the signed file itself (otherwise it writes a new file).</param>
/// <param name="FixedSignature">The reason is a signature written fixed into the page; signed digitally it would be appended instead.</param>
/// <param name="Reason">If a restriction of the signatures (certification, locked fields) is the reason: which (localised); otherwise null.</param>
public sealed record SignedSaveQuestion(string FileName, int Count, bool OverwritesOriginal, bool FixedSignature = false, string? Reason = null);

/// <summary>Answer to <see cref="SignedSaveQuestion"/>.</summary>
public enum SignedSaveAnswer
{
    Cancel,
    SaveAsNew,
    Save,
}

/// <summary>Answer to <see cref="RedactedSaveQuestion"/>.</summary>
public enum RedactedSaveAnswer
{
    Cancel,
    SaveAsNew,
    Overwrite,
}
