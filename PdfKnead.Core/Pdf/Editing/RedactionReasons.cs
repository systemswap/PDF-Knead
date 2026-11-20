using System.Globalization;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.IO;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>Where the redaction protocol goes.</summary>
public enum RedactionProtocolPlace
{
    /// <summary>No protocol.</summary>
    None,

    /// <summary>Before the first page: seen when the file is opened.</summary>
    First,

    /// <summary>After the last page.</summary>
    Last,
}

/// <summary>What is done with the reasons when redacting (KONZEPT.md, section 5.6, „Schwärzungsgründe“).</summary>
/// <param name="DefaultReason">The reason for every mark without its own; null or empty: none.</param>
/// <param name="InBars">Write the reason into the bar (small, in <paramref name="TextColor"/>; left out where it does not fit).</param>
/// <param name="Notes">A note with the reason at each bar.</param>
/// <param name="Protocol">Pages with the protocol, first or last.</param>
/// <param name="Name">Who redacted (freely entered, may be empty); in the protocol and as author of the notes.</param>
/// <param name="TextColor">Colour of the reason in the bar, 0xRRGGBB.</param>
public sealed record RedactionReasonSettings(string? DefaultReason, bool InBars, bool Notes, RedactionProtocolPlace Protocol, string? Name, int TextColor = 0xFFFFFF)
{
    /// <summary>Whether anything is to be done at all.</summary>
    public bool Any => InBars || Notes || Protocol != RedactionProtocolPlace.None;
}

/// <summary>
/// A bar with its reason: page (index), area in page space, reason; the redaction code and its legal basis if the reason is one of a code set
/// (wish of 4.11.2026), and whether the code (or else the reason) is written into the bar.
/// </summary>
public sealed record ReasonedBar(int Page, PdfRectangle Area, string Reason, string? Code = null, string? Basis = null, bool InBar = true)
{
    /// <summary>What goes into the bar: the code, else the reason; null if nothing is written there.</summary>
    public string? BarText => InBar ? Code ?? Reason : null;

    /// <summary>The reason in full, as in the note and the protocol: code, reason and legal basis.</summary>
    public string Full => (Code is { } code ? code + " — " : "") + Reason + (Basis is { } basis ? " (" + basis + ")" : "");
}

/// <summary>
/// Reasons of redactions (KONZEPT.md, section 5.6, „Schwärzungsgründe und -protokoll“, B-14): written into the bars, as notes at them and on
/// protocol pages — never anything of what was redacted. The reason in a bar is drawn as outlines, not as text: nothing that can be found
/// as text lies in the area of a bar, and the bar stays as opaque as before.
/// </summary>
public static class RedactionReasons
{
    /// <summary>Reasons offered in the list (the user can add own ones).</summary>
    public static IReadOnlyList<string> Suggestions =>
    [
        Tr.T("Personenbezogene Daten (Art. 6 DSGVO)"),
        Tr.T("Besondere Kategorien personenbezogener Daten (Art. 9 DSGVO)"),
        Tr.T("Schutz personenbezogener Daten (§ 5 IFG)"),
        Tr.T("Betriebs- und Geschäftsgeheimnis (§ 6 IFG)"),
        Tr.T("Schutz Dritter"),
    ];

    /// <summary>Longest reason kept (a crafted or pasted text must not fill pages).</summary>
    public const int MaxLength = 200;

    private const string FontFile = "LiberationSans-Regular.ttf";

    /// <summary>Text size in a bar at most (pt), and the smallest that is still written.</summary>
    private const double MaxSize = 9, MinSize = 4;

    /// <summary>
    /// The reasons of the redactions put into the redacted document (KONZEPT.md, section 5.6, B-14): written into the bars (as outlines over
    /// them), as notes at them, and protocol pages before the first or after the last page. The state is built anew from that, as for pages
    /// turned into images; null if no mark has a reason.
    /// </summary>
    /// <param name="applied">The redacted document.</param>
    /// <param name="before">The pages before redacting, with their marks.</param>
    /// <param name="reasons">What to do with the reasons.</param>
    /// <param name="file">The file name (for the protocol).</param>
    /// <param name="options">The save options of the redaction.</param>
    /// <param name="password">Password that opens the written file, if it is encrypted.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>The new document (owned by the caller) and its pages; null if no mark has a reason.</returns>
    public static (PdfDocument, PageItem[])? Apply(PdfDocument applied, PageItem[] before, RedactionReasonSettings reasons, string file,
        PdfSaveOptions options, string? password, CancellationToken cancel = default)
    {
        var bars = Bars(before, reasons.DefaultReason);
        if (bars.Count == 0) return null;
        var pages = PageItem.From(applied).ToList();
        var time = DateTimeOffset.Now;
        List<PageItem> protocol = [];
        MemoryPdf? protocolFile = null;
        PdfDocument? protocolDoc = null;
        try
        {
            if (reasons.Protocol != RedactionProtocolPlace.None)
            {
                var sizes = before.Select(p => (p.Page.Width, p.Page.Height)).ToList();
                var (w, h) = sizes.Count > 0 ? sizes[0] : (595.28, 841.89);
                protocolFile = Protocol(bars, sizes, file, reasons.Name, time, w, h, Accessibility.StructureRoles.IsTagged(applied.Catalog));
                protocolDoc = protocolFile.OpenDocument();
                protocol = [.. PageItem.From(protocolDoc)];
            }
            var shift = reasons.Protocol == RedactionProtocolPlace.First ? protocol.Count : 0;
            var overlays = new Dictionary<int, IReadOnlyList<PdfStream>>();
            var notes = new Dictionary<int, IReadOnlyList<PdfDictionary>>();
            var tagged = Accessibility.StructureRoles.IsTagged(applied.Catalog);
            foreach (var group in bars.GroupBy(b => b.Page))
            {
                if ((uint)group.Key >= (uint)pages.Count) continue;
                var page = pages[group.Key].Page;
                if (reasons.InBars && Labels(group, RectD.FromLtrb(page.MediaBox.Left, page.MediaBox.Bottom,
                        page.MediaBox.Right, page.MediaBox.Top), reasons.TextColor, reasons.Notes, tagged: tagged) is { } labels)
                    overlays[group.Key + shift] = [labels];
                if (reasons.Notes) notes[group.Key + shift] = [.. group.Select(b => Note(b, reasons.Name, time))];
            }
            var all = reasons.Protocol == RedactionProtocolPlace.First ? [.. protocol, .. pages] : pages.Concat(protocol).ToList();
            var doc = InDocumentRedaction.Write(applied, all, options with
            {
                ApplyRedactions = false,
                Flatten = false,
                Protection = null,
                KeepEncryption = true,
                Cleanup = CleanupItems.None,
                ScopedCleanup = null,
                KeepPlaces = null,
                Compression = null,
                CompressionCache = null,
                Overlays = overlays,
                AddedAnnotations = notes,
                // a tagged protocol joins the document's top Document directly (6.6 i, No. 658)
                OwnParts = tagged && protocolDoc is not null ? new HashSet<PdfDocument>([protocolDoc]) : null,
            }, password, cancel);
            try
            {
                PageItem[] beforeAll = reasons.Protocol == RedactionProtocolPlace.First ? [.. protocol, .. before] : [.. before, .. protocol];
                var changed = bars.Select(b => b.Page + shift).Concat(Enumerable.Range(reasons.Protocol == RedactionProtocolPlace.First ? 0 : before.Length,
                    protocol.Count)).ToHashSet();
                return (doc, InDocumentRedaction.Adopt(doc, beforeAll, changed));
            }
            catch
            {
                doc.Dispose();
                throw;
            }
        }
        finally
        {
            protocolDoc?.Dispose();
            protocolFile?.Dispose();
        }
    }

    /// <summary>The bars of the pages with their reasons: each mark (not those over hidden content) with its own reason or the default.</summary>
    /// <param name="pages">The pages before redacting, with their marks.</param>
    /// <param name="defaultReason">The reason for marks without their own.</param>
    /// <returns>The bars that have a reason, the largest area of each mark (a mark over several lines is one bar for its reason).</returns>
    public static List<ReasonedBar> Bars(IReadOnlyList<PageItem> pages, string? defaultReason)
    {
        var bars = new List<ReasonedBar>();
        for (var p = 0; p < pages.Count; p++)
            foreach (var mark in pages[p].Elements.OfType<RedactionElement>())
            {
                if (mark.Hidden || Clean(mark.Reason ?? defaultReason) is not { } reason) continue;
                var area = mark.PageAreas.MaxBy(a => a.Width * a.Height);
                // a mark's own reason brings its code and basis; the default reason for all has none
                if (area.Width > 0 && area.Height > 0)
                    bars.Add(mark.Reason is null ? new ReasonedBar(p, area, reason)
                        : new ReasonedBar(p, area, reason, Clean(mark.ReasonCode), Clean(mark.ReasonBasis), mark.ReasonInBar));
            }
        return bars;
    }

    /// <summary>A reason as kept: trimmed, on one line, at most <see cref="MaxLength"/> characters; null if empty.</summary>
    public static string? Clean(string? reason)
    {
        if (reason is null) return null;
        var line = string.Join(' ', reason.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        line = new string([.. line.Where(c => !char.IsControl(c))]);
        return line.Length == 0 ? null : line.Length > MaxLength ? line[..MaxLength] : line;
    }

    /// <summary>
    /// The words of reasons and name that also stand in what is redacted: a reason like „Gehalt Max Mustermann“ would tell what is gone.
    /// Compared without case, whole words of at least three letters.
    /// </summary>
    /// <param name="texts">Reasons and name.</param>
    /// <param name="redacted">What is redacted.</param>
    /// <returns>The words found in both, each once.</returns>
    public static List<string> Revealing(IEnumerable<string?> texts, IEnumerable<string> redacted)
    {
        static IEnumerable<string> Words(string text) =>
            text.Split((char[])[' ', ',', ';', '.', ':', '(', ')', '/', '-', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Count(char.IsLetterOrDigit) >= 3);
        var gone = new HashSet<string>(redacted.SelectMany(Words), StringComparer.OrdinalIgnoreCase);
        return [.. texts.OfType<string>().SelectMany(Words).Where(gone.Contains).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The reasons of a page written into its bars: a form in the page's default space, the text as outlines in each bar it fits.</summary>
    /// <param name="bars">The bars of the page.</param>
    /// <param name="pageBox">The page's box (the form's /BBox).</param>
    /// <param name="color">Colour of the text, 0xRRGGBB.</param>
    /// <param name="notes">Notes lie in the bars' top left corners (<see cref="Note"/>): the text keeps clear of them.</param>
    /// <param name="tooSmall">Gets the bars whose text does not fit even at the smallest size (left out there; wish of 4.11.2026: warned about).</param>
    /// <param name="tagged">The file is tagged: the text of each bar is an artifact like the bar, with the text drawn as /ActualText (ISO 32000-2,
    /// 14.8.2.2 and 14.9.4) — screen readers that read artifacts read the reason, all others pass over it (docs/ABLAUFPLAN.md, 6.3 step 2).</param>
    /// <returns>The form; null if no reason fits into its bar.</returns>
    public static PdfStream? Labels(IEnumerable<ReasonedBar> bars, RectD pageBox, int color, bool notes = false, List<ReasonedBar>? tooSmall = null,
        bool tagged = false)
    {
        var font = EmbeddedFonts.StandardOpenType(FontFile);
        var content = new StringBuilder();
        content.Append(CultureInfo.InvariantCulture, $"{Num(((color >> 16) & 0xFF) / 255.0)} {Num(((color >> 8) & 0xFF) / 255.0)} {Num((color & 0xFF) / 255.0)} rg\n");
        var any = false;
        foreach (var bar in bars)
        {
            if (bar.BarText is not { } text) continue;
            // size after the bar's height, smaller if the text is wider than the bar; where the reason does not fit even at the smallest
            // size, the code it names in brackets (No. 529); nothing at all if that does not fit either
            var a = notes ? bar.Area with { Left = bar.Area.Left + NoteSize(bar.Area) + 1 } : bar.Area;
            if (Fitting(font, text, a) is not { } fit)
            {
                tooSmall?.Add(bar);
                continue;
            }
            var (glyphs, advance, size, drawn) = fit;
            if (advance <= 0) continue;
            if (tagged) content.Append("/Artifact <</Type /Pagination /Subtype /Redaction /ActualText ").Append(HexText(drawn)).Append(">> BDC\n");
            var x = (a.Left + a.Right) / 2 - advance * size / 2;
            var y = (a.Bottom + a.Top) / 2 - size * 0.35;
            foreach (var (verbs, points, offset) in glyphs)
            {
                var p = 0;
                foreach (var verb in verbs)
                    switch (verb)
                    {
                        case PathVerb.Move or PathVerb.Line:
                            Point(content, x + (offset + points[p]) * size, y + points[p + 1] * size).Append(verb == PathVerb.Move ? "m\n" : "l\n");
                            p += 2;
                            break;
                        case PathVerb.Cubic:
                            for (var k = 0; k < 3; k++) Point(content, x + (offset + points[p + 2 * k]) * size, y + points[p + 2 * k + 1] * size);
                            content.Append("c\n");
                            p += 6;
                            break;
                        case PathVerb.Close:
                            content.Append("h\n");
                            break;
                    }
            }
            content.Append("f\n");
            if (tagged) content.Append("EMC\n");
            any = true;
        }
        if (!any) return null;
        var form = new PdfDictionary();
        form.Set(PdfName.Type, PdfName.Get("XObject"));
        form.Set(PdfName.Subtype, PdfName.Get("Form"));
        form.Set(PdfName.Get("BBox"), new PdfArray([new PdfReal(pageBox.Left), new PdfReal(pageBox.Top), new PdfReal(pageBox.Right), new PdfReal(pageBox.Bottom)]));
        form.Set(PdfName.Resources, new PdfDictionary());
        return new PdfStream(form, Encoding.ASCII.GetBytes(content.ToString()));
    }

    /// <summary>
    /// The text that goes into a bar, shaped, with its size: the reason itself, or — where it does not fit even at the smallest size — the
    /// code it names in brackets at its end („Personenbezogene Daten (Art. 6 DSGVO)“ → „Art. 6 DSGVO“). A long reason was left out of every
    /// short bar, such as one over a phone number, though its code would have fitted (report of 5.11.2026, docs/FEHLERGEDAECHTNIS.md,
    /// No. 529). Null if neither fits.
    /// </summary>
    private static (List<(PathVerb[] Verbs, double[] Points, double Offset)> Glyphs, double Advance, double Size, string Text)? Fitting(OpenTypeFont font,
        string text, PdfRectangle area)
    {
        foreach (var candidate in (string?[])[text, ShortForm(text)])
        {
            if (candidate is null) continue;
            var (glyphs, advance) = Shape(font, candidate);
            if (advance <= 0) return (glyphs, advance, MaxSize, candidate);   // nothing to draw: counts as fitting
            var size = TextSize(area, advance);
            if (size >= MinSize) return (glyphs, advance, size, candidate);
        }
        return null;
    }

    /// <summary>A text string for a content stream: UTF-16BE with its byte order mark, as a hexadecimal string (nothing to escape).</summary>
    private static string HexText(string text)
    {
        var sb = new StringBuilder("<FEFF", 6 + text.Length * 4);
        foreach (var c in text) sb.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        return sb.Append('>').ToString();
    }

    /// <summary>
    /// The code a reason names in brackets at its end („Betriebs- und Geschäftsgeheimnis (§ 6 IFG)“ → „§ 6 IFG“), the short form for a bar the
    /// reason does not fit; null if it names none.
    /// </summary>
    /// <param name="reason">The reason.</param>
    internal static string? ShortForm(string reason)
    {
        var text = reason.TrimEnd();
        if (!text.EndsWith(')')) return null;
        var open = text.LastIndexOf('(');
        if (open <= 0) return null;
        var code = text[(open + 1)..^1].Trim();
        return code.Length is > 0 and <= 40 && !code.Contains('(') ? code : null;
    }

    /// <summary>
    /// Size of a text in a bar: after the bar's height (62 % of it, at most <see cref="MaxSize"/>), smaller if it is wider than the bar. A low
    /// bar — one over a footer in small print, 6 pt high — may give up to 80 % of its height to reach the smallest size: the text still lies
    /// within it, and before it was left out of such bars altogether, the code too (report of 5.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 530).
    /// </summary>
    private static double TextSize(PdfRectangle area, double advance)
    {
        var byHeight = Math.Min(MaxSize, area.Height * 0.62);
        if (byHeight < MinSize) byHeight = Math.Min(MinSize, area.Height * 0.8);
        return Math.Min(byHeight, (area.Width - 2) / advance);
    }

    /// <summary>
    /// Whether a text fits into a bar at the smallest size at least (<see cref="MinSize"/>) — checked while the code or reason is typed, so
    /// that the user is told at once where it will be left out (wish of 4.11.2026).
    /// </summary>
    /// <param name="text">The code or reason.</param>
    /// <param name="area">The bar in page space.</param>
    /// <param name="notes">Whether a note will lie in its top left corner.</param>
    public static bool FitsInBar(string text, PdfRectangle area, bool notes = false)
    {
        var a = notes ? area with { Left = area.Left + NoteSize(area) + 1 } : area;
        return Fitting(EmbeddedFonts.StandardOpenType(FontFile), text, a) is not null;   // with the code as short form (No. 529)
    }

    /// <summary>The outlines of a text in em (baseline at 0), each glyph with its offset along the line, and the whole advance in em.</summary>
    private static (List<(PathVerb[] Verbs, double[] Points, double Offset)> Glyphs, double Advance) Shape(OpenTypeFont font, string text)
    {
        var shaped = Core.Fonts.Layout.TextShaper.For(font).Shape(text);
        var em = 1.0 / font.UnitsPerEm;
        var glyphs = new List<(PathVerb[], double[], double)>();
        double pen = 0;
        foreach (var g in shaped)
        {
            var outline = font.Outline(g.Glyph);
            if (!outline.IsEmpty)
            {
                var points = new double[outline.Points.Count];
                for (var i = 0; i + 1 < points.Length; i += 2)
                {
                    var (px, py) = font.FontMatrix.Transform(outline.Points[i], outline.Points[i + 1]);
                    points[i] = px + g.XOffset * em;
                    points[i + 1] = py + g.YOffset * em;
                }
                glyphs.Add(([.. outline.Verbs], points, pen * em));
            }
            pen += g.XAdvance;
        }
        return (glyphs, pen * em);
    }

    private static StringBuilder Point(StringBuilder sb, double x, double y) => sb.Append(Num(x)).Append(' ').Append(Num(y)).Append(' ');

    private static string Num(double v) => PdfSerializer.FormatReal(Math.Round(v, 3));

    /// <summary>A note with the reason in the top left corner of a bar (closed, not printed): its symbol lies on the bar, not on the text beside it.</summary>
    /// <param name="bar">The bar.</param>
    /// <param name="name">Who redacted; null or empty: none.</param>
    /// <param name="time">When.</param>
    /// <returns>The annotation (without /P; the writer sets it).</returns>
    public static PdfDictionary Note(ReasonedBar bar, string? name, DateTimeOffset time)
    {
        var a = bar.Area;
        var note = new PdfDictionary();
        note.Set(PdfName.Type, PdfName.Get("Annot"));
        note.Set(PdfName.Subtype, PdfName.Get("Text"));
        var size = NoteSize(a);
        note.Set(PdfName.Get("Rect"), new PdfArray([new PdfReal(a.Left), new PdfReal(a.Top - size), new PdfReal(a.Left + size), new PdfReal(a.Top)]));
        // an appearance of its own, exactly the size of /Rect: readers draw their symbol of a note larger than the bar otherwise, over the text
        var look = new PdfDictionary();
        look.Set(PdfName.Type, PdfName.Get("XObject"));
        look.Set(PdfName.Subtype, PdfName.Get("Form"));
        look.Set(PdfName.Get("BBox"), new PdfArray([PdfInteger.Of(0), PdfInteger.Of(0), new PdfReal(size), new PdfReal(size)]));
        look.Set(PdfName.Resources, new PdfDictionary());
        var symbol = Content.AnnotationAppearance.NoteBubble(size, Content.AnnotationAppearance.NoteFill, null);
        var ap = new PdfDictionary();
        ap.Set(PdfName.N, new PdfStream(look, Encoding.ASCII.GetBytes(symbol)));
        note.Set(PdfName.Get("AP"), ap);
        note.Set(PdfName.Get("Contents"), new PdfString(PdfTextEncoding.Encode(Tr.T("Geschwärzt: {0}", bar.Full))));
        note.Set(PdfName.Get("Subj"), new PdfString(PdfTextEncoding.Encode(Tr.T("Schwärzungsgrund"))));
        if (name is { Length: > 0 }) note.Set(PdfName.Get("T"), new PdfString(PdfTextEncoding.Encode(name)));
        note.Set(PdfName.Get("Name"), PdfName.Get("Comment"));
        note.Set(PdfName.Get("Open"), PdfBoolean.False);
        note.Set(PdfName.Get("C"), new PdfArray([new PdfReal(1), new PdfReal(0.87), new PdfReal(0.45)]));
        // (the version of the file is not known yet: the writer puts it in the form of 2.0 where it writes 2.0)
        note.Set(PdfName.Get("M"), new PdfString(Encoding.ASCII.GetBytes(Document.PdfDate.Format(time))));
        return note;
    }

    /// <summary>Side of the note's square in a bar: the bar's height, 4 to 12 pt.</summary>
    private static double NoteSize(PdfRectangle bar) => Math.Clamp(Math.Min(bar.Height, bar.Width), 4, 12);


    /// <summary>
    /// The protocol pages: title, file, date and time, who (if given), then per bar its page, its place (from the top left, in mm and in
    /// words) and its reason. As many pages as needed, each as large as <paramref name="width"/> × <paramref name="height"/>.
    /// </summary>
    /// <param name="bars">The bars with reasons, in page order.</param>
    /// <param name="pageSizes">The size of each page of the document (for the place of a bar), by page index.</param>
    /// <param name="file">The file name.</param>
    /// <param name="name">Who redacted; may be empty.</param>
    /// <param name="time">When.</param>
    /// <param name="width">Page width (pt), the first page's.</param>
    /// <param name="height">Page height (pt).</param>
    /// <param name="tagged">
    /// For a tagged document: the protocol tagged as well (docs/ABLAUFPLAN.md, 6.6 i, item 3; docs/FEHLERGEDAECHTNIS.md, No. 658) — a Sect
    /// with the title as H1, the lines below as P and the bars as a Table (the head TH with /Scope /Column, repeated on a further page as an
    /// artifact), in the language of the program.
    /// </param>
    /// <returns>The protocol as a PDF in memory; the caller disposes it.</returns>
    public static MemoryPdf Protocol(IReadOnlyList<ReasonedBar> bars, IReadOnlyList<(double Width, double Height)> pageSizes, string file, string? name,
        DateTimeOffset time, double width, double height, bool tagged = false)
    {
        // portrait reading width even on landscape or tiny pages: the text is laid out on at least 420 × 595 pt and scaled into the page
        double w = Math.Max(width, 420), h = Math.Max(height, 595);
        const double Margin = 56, Line = 15, Size = 10;
        // per bar its code, reason and legal basis (wish of 4.11.2026); an own reason in full
        var rows = bars.Select(b => (Page: Tr.T("Seite {0}", b.Page + 1), Place: Place(b, pageSizes), Reason: b.Full)).ToList();
        var pages = new List<List<TextElement>>();
        var current = new List<TextElement>();
        var roles = new Dictionary<PageElement, string>();
        var y = h - Margin;
        void Add(string text, double x, double top, double right, string role, double size = Size)
        {
            var element = new TextElement
            {
                Rect = new PdfRectangle(x, top - size * 1.6, right, top), Text = text, FontSize = size, Multiline = false,
            };
            current.Add(element);
            roles[element] = role;
        }
        void Header()
        {
            Add(Tr.T("Schwärzungsprotokoll"), Margin, y, w - Margin, "H1", 16);
            y -= 30;
            Add(Tr.T("Datei: {0}", file), Margin, y, w - Margin, "P");
            y -= Line;
            Add(Tr.T("Geschwärzt am {0}", time.ToString("g", Tr.Culture)), Margin, y, w - Margin, "P");
            y -= Line;
            if (name is { Length: > 0 })
            {
                Add(Tr.T("Von: {0}", name), Margin, y, w - Margin, "P");
                y -= Line;
            }
            Add(Tr.T("{0} Schwärzungen mit Grund. Der geschwärzte Inhalt steht nicht in diesem Protokoll.", rows.Count), Margin, y, w - Margin, "P");
            y -= Line * 2;
        }
        // the head of the table: its cells the first time, an artifact where a further page repeats it
        void TableHead(bool repeated)
        {
            var role = repeated ? "Artifact" : "TH 0";
            Add(Tr.T("Seite"), Margin, y, Margin + 60, role);
            Add(Tr.T("Lage (von oben links)"), Margin + 64, y, Margin + 200, role);
            Add(Tr.T("Grund"), Margin + 204, y, w - Margin, role);
            y -= Line * 1.3;
        }
        Header();
        TableHead(false);
        var number = 0;
        foreach (var (page, place, reason) in rows)
        {
            var lines = Wrap(reason, (int)Math.Max(20, (w - Margin * 2 - 204) / (Size * 0.5)));
            if (y - Line * lines.Count < Margin)
            {
                pages.Add(current);
                current = [];
                y = h - Margin;
                TableHead(true);
            }
            var row = "TD " + (++number).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Add(page, Margin, y, Margin + 60, row);
            Add(place, Margin + 64, y, Margin + 200, row);
            foreach (var line in lines)
            {
                Add(line, Margin + 204, y, w - Margin, row);
                y -= Line;
            }
        }
        pages.Add(current);
        using var blank = BlankPage.CreateDocument(w, h, pages.Count, "1.7");
        var items = PageItem.From(blank);
        for (var i = 0; i < items.Length; i++) items[i] = items[i].WithElements([.. pages[i]]);
        var output = new MemoryPdf();
        PdfWriter.Write(blank, items, output, new PdfSaveOptions
        {
            Flatten = true,
            ElementStructure = tagged ? roles : null,
            DocumentLanguage = tagged ? Tr.Culture.Name : null,
        });
        return output;
    }

    /// <summary>The place of a bar: its top left corner from the page's top left in mm, and in words („oben links“).</summary>
    private static string Place(ReasonedBar bar, IReadOnlyList<(double Width, double Height)> sizes)
    {
        var (pw, ph) = (uint)bar.Page < (uint)sizes.Count ? sizes[bar.Page] : (595.0, 842.0);
        double x = bar.Area.Left, fromTop = ph - bar.Area.Top;
        double cx = (bar.Area.Left + bar.Area.Right) / 2 / Math.Max(pw, 1), cy = 1 - (bar.Area.Bottom + bar.Area.Top) / 2 / Math.Max(ph, 1);
        var vertical = cy < 1 / 3.0 ? Tr.T("oben") : cy < 2 / 3.0 ? Tr.T("Mitte") : Tr.T("unten");
        var horizontal = cx < 1 / 3.0 ? Tr.T("links") : cx < 2 / 3.0 ? Tr.T("mittig") : Tr.T("rechts");
        return Tr.T("{0} {1}, {2} × {3} mm", vertical, horizontal, Math.Round(x * 25.4 / 72), Math.Round(fromTop * 25.4 / 72));
    }

    /// <summary>Breaks a reason into lines of at most about <paramref name="width"/> characters at spaces.</summary>
    private static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                lines.Add(line.ToString());
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word.Length > width ? word[..width] : word);
        }
        if (line.Length > 0) lines.Add(line.ToString());
        return lines.Count == 0 ? [""] : lines;
    }
}
