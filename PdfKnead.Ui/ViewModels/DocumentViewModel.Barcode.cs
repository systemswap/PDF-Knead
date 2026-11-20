using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Rendering;

namespace PdfKnead.ViewModels;

/// <summary>The code chosen in „Bearbeiten“: page, what was found, its meaning and the sections for the panel.</summary>
public sealed record InspectedBarcode(int Page, PageBarcode Code, BarcodeMeaning Meaning, IReadOnlyList<ImageInfoSection> Sections)
{
    /// <summary>The warnings of the meaning, one text.</summary>
    public string Warnings => string.Join("\n", Meaning.Warnings);

    /// <summary>Whether there are warnings.</summary>
    public bool HasWarnings => Meaning.Warnings.Count > 0;

    /// <summary>Text of the button that opens the address; empty if there is nothing to open.</summary>
    public string ActionLabel => Meaning.Action?.Label ?? "";

    /// <summary>
    /// A code set in a bar code font that could not be read (phase 8.2): the block, its content told from the text or empty. „Code ändern …“
    /// then replaces the block (<see cref="CodeReplacement.ForFontBlock"/>).
    /// </summary>
    public PageTextBlock? FontBlock { get; init; }

    /// <summary>Whether something can be opened.</summary>
    public bool HasAction => Meaning.Action is not null;
}

/// <summary>A code the redaction question reports (KONZEPT.md, section 5.19).</summary>
/// <param name="Page">Page index.</param>
/// <param name="Code">The code.</param>
/// <param name="Partial">A bar covers only part of it: with its error correction the content may still be readable.</param>
/// <param name="Phrase">A redacted piece of text its content contains; null if none.</param>
public sealed record RedactionCode(int Page, PageBarcode Code, bool Partial, string? Phrase);

/// <summary>Codes of the page content, read for „Bearbeiten“ (KONZEPT.md, sections 5.19 and 5.22). Reading changes nothing on the page.</summary>
public sealed partial class DocumentViewModel
{
    // codes found per page: valid while the page and its elements (apart from redaction marks) stay the same
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (PageElement[] Elements, IReadOnlyList<PageBarcode> Codes)> _pageCodes = [];

    // searching the codes ahead, while the user is still marking (see CodesHistoryChanged)
    private CancellationTokenSource? _codePrefetch;

    /// <summary>
    /// After every change: once there are bars, the codes of all pages are searched in the background — one page at a time, at low
    /// priority —, so that „Schwärzungen anwenden“ need not wait for them. Pages with bars first.
    /// </summary>
    private void CodesHistoryChanged()
    {
        if (_codePrefetch is not null || _closedForCodes) return;
        if (!Pages.Any(p => p.Item.Elements.Any(e => e is RedactionElement { Hidden: false }))) return;
        var pages = new List<(PageItem Item, PageElement[] Elements, bool Bars)>();
        for (var i = 0; i < Pages.Count; i++)
        {
            var item = Pages[i].Item;
            var elements = item.Elements.Where(e => e is not RedactionElement).ToArray();
            if (Cached(item, elements) is not null) continue;
            pages.Add((item, elements, item.Elements.Any(e => e is RedactionElement { Hidden: false })));
        }
        if (pages.Count == 0) return;
        var cancel = _codePrefetch = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var (item, elements, _) in pages.OrderByDescending(p => p.Bars))
                {
                    if (cancel.IsCancellationRequested) return;
                    using var use = Renderer.Use(item.Source);
                    if (use is null) return;
                    var list = Renderer.TryGetDisplayList(item.Id) ?? Renderer.GetDisplayList(item.Id).GetAwaiter().GetResult();
                    if (list is null) continue;
                    _pageCodes[item.Id] = (elements, PageBarcodes.FindAll(item.Source, item.Page, list, elements, Renderer.Images, cancel.Token));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                AppLog.Write("Codes vorab suchen", e.ToString());
            }
            finally
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_codePrefetch, cancel)) _codePrefetch = null;
                    cancel.Dispose();
                });
            }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Codes vorab" };
        thread.Start();
    }

    private bool _closedForCodes;

    /// <summary>Pages whose codes are known (searched ahead or for a question); for tests and measurements.</summary>
    internal int CodePagesKnown => Pages.Count(p => Cached(p.Item, [.. p.Item.Elements.Where(e => e is not RedactionElement)]) is not null);

    /// <summary>Whether the search ahead is running; for tests and measurements.</summary>
    internal bool SearchingCodesAhead => _codePrefetch is not null;

    /// <summary>Stops the search ahead (closing the document).</summary>
    private void EndCodePrefetch()
    {
        _closedForCodes = true;
        _codePrefetch?.Cancel();
    }

    private IReadOnlyList<PageBarcode>? Cached(PageItem item, PageElement[] elements) =>
        _pageCodes.TryGetValue(item.Id, out var cached) && cached.Elements.SequenceEqual(elements, ReferenceEqualityComparer.Instance) ? cached.Codes : null;

    /// <summary>
    /// Pages searched at the same time when the question waits: half the processors, at most four, and only as many as the free memory
    /// allows (a page takes about 120 MB while it is searched).
    /// </summary>
    private static int CodeWorkers()
    {
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var byMemory = (int)Math.Max(1, available / 4 / (120L << 20));
        return Math.Clamp(Math.Min(Environment.ProcessorCount / 2, byMemory), 1, 4);
    }

    /// <summary>Longest search for codes on one page before the redaction question (<see cref="FindCodesToRedactAsync"/>).</summary>
    internal static TimeSpan CodePageTime { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Longest search for codes over all pages before the redaction question.</summary>
    internal static TimeSpan CodeSearchTime { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Codes the redaction must not leave behind: those a bar covers only in part (QR and Data Matrix repair up to 30 % of their
    /// codewords), and those whose content holds a redacted piece of text (an IBAN in a GiroCode). Pages with bars are searched always,
    /// all pages when there is redacted text; fully covered codes go with their bar and are not reported. A page that takes longer than
    /// <see cref="CodePageTime"/> to search, or is still waiting when <see cref="CodeSearchTime"/> has passed, is left unchecked and
    /// named: a crafted or damaged page must not hold up the redaction for ever.
    /// </summary>
    /// <param name="progress">Reports „page n of m“ while searching; may be null.</param>
    /// <param name="uncheckedPages">Receives the pages (index) whose codes could not be checked in time; may be null.</param>
    /// <param name="cancel">Cancels the search.</param>
    /// <returns>The codes to report.</returns>
    public async Task<List<RedactionCode>> FindCodesToRedactAsync(IProgress<(int Done, int Of)>? progress = null, ICollection<int>? uncheckedPages = null,
        CancellationToken cancel = default)
    {
        var phrases = await RedactedPhrasesAsync();
        // compared without spaces as well: "DE89 3704 0044" on the page is "DE89370400440532013000" in the code
        var compact = phrases.Select(p => (Phrase: p, Bare: string.Concat(p.Where(c => !char.IsWhiteSpace(c))))).Where(p => p.Bare.Length >= 4).ToList();
        var pages = new List<(int Index, PageItem Item, PageElement[] Elements, List<RectD> Bars)>();
        for (var i = 0; i < Pages.Count; i++)
        {
            var bars = MarkAreas(i, withHidden: false);
            if (bars.Count == 0 && compact.Count == 0) continue;
            var item = Pages[i].Item;
            pages.Add((i, item, item.Elements.Where(e => e is not RedactionElement).ToArray(), bars));
        }
        // the search ahead stops here; what it has found is kept
        _codePrefetch?.Cancel();
        var known = new Dictionary<int, IReadOnlyList<PageBarcode>>();
        foreach (var p in pages)
            if (Cached(p.Item, p.Elements) is { } cached)
                known[p.Index] = cached;
        var done = known.Count;
        progress?.Report((done, pages.Count));
        var timedOut = new System.Collections.Concurrent.ConcurrentDictionary<int, bool>();
        var found = await WorkAsync(() => Task.Run(() =>
        {
            var result = new System.Collections.Concurrent.ConcurrentDictionary<int, IReadOnlyList<PageBarcode>>(known);
            var options = new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = CodeWorkers() };
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            overall.CancelAfter(CodeSearchTime);
            Parallel.ForEach(pages.Where(p => !known.ContainsKey(p.Index)), options, p =>
            {
                IReadOnlyList<PageBarcode> codes = [];
                using var page = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                page.CancelAfter(CodePageTime);
                try
                {
                    page.Token.ThrowIfCancellationRequested();
                    // the display list only for this page (holding all of them would defeat the renderer's memory budget)
                    var list = Renderer.TryGetDisplayList(p.Item.Id) ?? Renderer.GetDisplayList(p.Item.Id).GetAwaiter().GetResult();
                    if (list is not null) codes = PageBarcodes.FindAll(p.Item.Source, p.Item.Page, list, p.Elements, Renderer.Images, page.Token);
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    timedOut[p.Index] = true;   // out of time: unchecked, said so in the question — not taken as "no codes"
                }
                catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
                {
                    AppLog.Write("Codes suchen", e.ToString());
                }
                result[p.Index] = codes;
                progress?.Report((Interlocked.Increment(ref done), pages.Count));
            });
            return result;
        }));
        var report = new List<RedactionCode>();
        foreach (var p in pages)
        {
            var codes = found[p.Index];
            if (timedOut.ContainsKey(p.Index))
            {
                uncheckedPages?.Add(p.Index);
                continue;
            }
            _pageCodes[p.Item.Id] = (p.Elements, codes);
            foreach (var code in codes)
            {
                var covered = Coverage(code.Corners, p.Bars);
                if (covered >= 0.999) continue;   // goes with its bar
                var bare = string.Concat(code.Code.Text.Where(c => !char.IsWhiteSpace(c)));
                var phrase = compact.FirstOrDefault(c => code.Code.Text.Contains(c.Phrase, StringComparison.OrdinalIgnoreCase)
                                                         || bare.Contains(c.Bare, StringComparison.OrdinalIgnoreCase)).Phrase;
                if (covered > 0 || phrase is not null) report.Add(new RedactionCode(p.Index, code, covered > 0, phrase));
            }
        }
        return report;
    }

    /// <summary>Share of the code under the bars, sampled on an 11 × 11 grid over its quadrilateral.</summary>
    private static double Coverage((double X, double Y)[] q, List<RectD> bars)
    {
        if (bars.Count == 0) return 0;
        var inside = 0;
        for (var i = 0; i <= 10; i++)
            for (var j = 0; j <= 10; j++)
            {
                double u = i / 10.0, v = j / 10.0;
                double tx = q[0].X + (q[1].X - q[0].X) * u, ty = q[0].Y + (q[1].Y - q[0].Y) * u;
                double bx = q[3].X + (q[2].X - q[3].X) * u, by = q[3].Y + (q[2].Y - q[3].Y) * u;
                double x = tx + (bx - tx) * v, y = ty + (by - ty) * v;
                if (bars.Any(b => x >= b.Left - 0.01 && x <= b.Right + 0.01 && y >= b.Top - 0.01 && y <= b.Bottom + 0.01)) inside++;
            }
        return inside / 121.0;
    }

    /// <summary>Marks the codes for redaction: a bar over each whole code (half a module beyond its edge), all in one undo step.</summary>
    /// <param name="codes">The codes.</param>
    /// <returns>The number of marks added.</returns>
    public int RedactCodes(IEnumerable<RedactionCode> codes)
    {
        var byPage = new Dictionary<int, IReadOnlyList<PageElement>>();
        foreach (var group in codes.GroupBy(c => c.Page))
            byPage[group.Key] = group.Select(c =>
            {
                var q = c.Code.Corners;
                var m = c.Code.ModulePoints / 2;
                return (PageElement)RedactionElement.FromPageAreas([new Core.Pdf.Document.PdfRectangle(q.Min(p => p.X) - m, q.Min(p => p.Y) - m, q.Max(p => p.X) + m, q.Max(p => p.Y) + m)]);
            }).ToList();
        var count = byPage.Values.Sum(v => v.Count);
        if (count > 0) History.AddElements(byPage, Tr.T("Codes schwärzen"));
        return count;
    }

    /// <summary>The code that was read; null if none is selected or none was found at the clicked spot.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarcodeHint))]
    public partial InspectedBarcode? InspectedBarcode { get; set; }

    /// <summary>Whether the last click found no code.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarcodeHint))]
    public partial bool BarcodeMissed { get; set; }

    /// <summary>Whether a code is being read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarcodeHint))]
    public partial bool BarcodeReading { get; set; }

    /// <summary>Hint in the panel while no code is shown.</summary>
    public string BarcodeHint => InspectedBarcode is not null ? ""
        : BarcodeReading ? Tr.T("Lese den Code …")
        : BarcodeMissed ? Tr.T("An dieser Stelle ist kein lesbarer QR- oder Barcode.")
        : Tr.T("Auf einen QR- oder Barcode in der Seite klicken.");

    private int _barcodeStamp;

    /// <summary>Reads the code at a spot of the page (in the background).</summary>
    /// <param name="page">Page index.</param>
    /// <param name="x">X in page space.</param>
    /// <param name="y">Y in page space.</param>
    public async Task InspectBarcodeAsync(int page, double x, double y)
    {
        if (page < 0 || page >= Pages.Count) return;
        var stamp = ++_barcodeStamp;
        var item = Pages[page].Item;
        // marks for redaction are not drawn: their hatching would hide the code (copying it is refused separately)
        var elements = ElementsOn(page).Where(e => e is not RedactionElement).ToList();
        BarcodeReading = true;
        PageBarcode? found;
        try
        {
            var list = Renderer.TryGetDisplayList(item.Id) ?? await Renderer.GetDisplayList(item.Id);
            if (stamp != _barcodeStamp) return;
            found = list is null ? null : await Task.Run(() =>
            {
                using var use = Renderer.Use(item.Source);
                return use is null ? null : PageBarcodes.ReadAt(item.Source, item.Page, list, elements, Renderer.Images, x, y);
            });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // a damaged page must not abort the tool; it then shows that nothing was found
            AppLog.Write("Code lesen", e.ToString());
            found = null;
        }
        if (stamp != _barcodeStamp) return;
        BarcodeReading = false;
        BarcodeMissed = found is null;
        InspectedBarcode = found is null ? null : Describe(page, found);
        if (found is not null) EditTarget = EditTarget.Code;
    }

    /// <summary>The codes of a page, if they are known (searched ahead, for the redaction question or by <see cref="FindCodesOnPageAsync"/>).</summary>
    /// <param name="page">Page index.</param>
    /// <returns>The codes; null if the page has not been searched.</returns>
    public IReadOnlyList<PageBarcode>? KnownCodes(int page)
    {
        if ((uint)page >= (uint)Pages.Count) return [];
        var item = Pages[page].Item;
        return Cached(item, [.. item.Elements.Where(e => e is not RedactionElement)]);
    }

    private readonly HashSet<int> _codePagesSearching = [];

    /// <summary>
    /// Searches the codes of one page in the background — the first click on a page in „Bearbeiten“ (KONZEPT.md, section 5.22: the codes
    /// are searched per clicked page, not for all visible pages ahead) —, within <see cref="CodePageTime"/>; afterwards
    /// <see cref="KnownCodes"/> has them.
    /// </summary>
    /// <param name="page">Page index.</param>
    /// <returns>The codes; null if the page is outside, already being searched, or the search ran out of time.</returns>
    public async Task<IReadOnlyList<PageBarcode>?> FindCodesOnPageAsync(int page)
    {
        if ((uint)page >= (uint)Pages.Count) return null;
        if (KnownCodes(page) is { } known) return known;
        if (!_codePagesSearching.Add(page)) return null;
        var item = Pages[page].Item;
        var elements = item.Elements.Where(e => e is not RedactionElement).ToArray();
        try
        {
            var list = Renderer.TryGetDisplayList(item.Id) ?? await Renderer.GetDisplayList(item.Id);
            if (list is null || _closedForCodes) return null;
            using var cancel = new CancellationTokenSource(CodePageTime);
            var codes = await Task.Run(() =>
            {
                using var use = Renderer.Use(item.Source);
                return use is null ? null : PageBarcodes.FindAll(item.Source, item.Page, list, elements, Renderer.Images, cancel.Token);
            });
            if (codes is not null) _pageCodes[item.Id] = (elements, codes);
            return codes;
        }
        catch (OperationCanceledException)
        {
            return null;   // out of time: the next click reads at its spot again
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            AppLog.Write("Codes der Seite suchen", e.ToString());
            return null;
        }
        finally
        {
            _codePagesSearching.Remove(page);
        }
    }

    /// <summary>
    /// The code at a spot of the page, for „Bearbeiten“: from the page's known codes, otherwise read at the spot (the page's codes are
    /// searched in the background meanwhile, for the clicks after this one).
    /// </summary>
    /// <param name="page">Page index.</param>
    /// <param name="x">X in page space.</param>
    /// <param name="y">Y in page space.</param>
    /// <returns>The code; null if none is there.</returns>
    public async Task<PageBarcode?> CodeAtAsync(int page, double x, double y)
    {
        if ((uint)page >= (uint)Pages.Count) return null;
        if (KnownCodes(page) is { } known) return known.FirstOrDefault(c => Inside(c.Corners, x, y));
        _ = FindCodesOnPageAsync(page);
        var item = Pages[page].Item;
        var elements = ElementsOn(page).Where(e => e is not RedactionElement).ToList();
        try
        {
            var list = Renderer.TryGetDisplayList(item.Id) ?? await Renderer.GetDisplayList(item.Id);
            if (list is null) return null;
            return await Task.Run(() =>
            {
                using var use = Renderer.Use(item.Source);
                return use is null ? null : PageBarcodes.ReadAt(item.Source, item.Page, list, elements, Renderer.Images, x, y);
            });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            AppLog.Write("Code lesen", e.ToString());
            return null;
        }
    }

    /// <summary>Whether a point lies in the quadrilateral (its corners in order round the edge).</summary>
    internal static bool Inside((double X, double Y)[] corners, double x, double y)
    {
        if (corners.Length < 3) return false;
        var sign = 0;
        for (var i = 0; i < corners.Length; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % corners.Length];
            var cross = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
            var s = cross > 1e-9 ? 1 : cross < -1e-9 ? -1 : 0;
            if (s == 0) continue;
            if (sign == 0) sign = s;
            else if (sign != s) return false;
        }
        return true;
    }

    /// <summary>Shows a code chosen in „Bearbeiten“ in the panel (its properties and meaning).</summary>
    /// <param name="page">Page index.</param>
    /// <param name="code">The code.</param>
    public void ShowCode(int page, PageBarcode code)
    {
        _barcodeStamp++;
        BarcodeReading = false;
        BarcodeMissed = false;
        InspectedBarcode = Describe(page, code);
        EditTarget = EditTarget.Code;
    }

    /// <summary>
    /// Shows a block set in a bar code font whose code could not be read (phase 8.2): the kind from the font name, the content from the text
    /// where it is unambiguous (Code 39, EAN/UPC with check digit), otherwise empty — with a warning that says which.
    /// </summary>
    /// <param name="page">Page index.</param>
    /// <param name="block">The block (<see cref="PageTextBlock.CodeFont"/>).</param>
    public void ShowFontCode(int page, PageTextBlock block)
    {
        _barcodeStamp++;
        BarcodeReading = false;
        BarcodeMissed = false;
        InspectedBarcode = DescribeFontCode(page, block);
        EditTarget = EditTarget.Code;
    }

    /// <summary>The panel's sections for a block in a bar code font (<see cref="ShowFontCode"/>).</summary>
    internal static InspectedBarcode DescribeFontCode(int page, PageTextBlock block)
    {
        var fontName = block.Lines.SelectMany(l => l.Runs).Select(r => r.Format.FontName).FirstOrDefault(CodeFonts.IsCodeFont) ?? "";
        var format = CodeFonts.FormatOf(fontName) ?? BarcodeFormat.Code128;
        // only the characters in the code font: a human-readable line in the same block is not part of the content
        var codeText = string.Join("", block.Lines.SelectMany(l => l.Runs).Where(r => CodeFonts.IsCodeFont(r.Format.FontName)).Select(r => r.Text));
        var content = CodeFonts.Derive(format, codeText) ?? "";
        var q = block.Quad;
        (double X, double Y)[] corners = [(q.X3, q.Y3), (q.X2, q.Y2), (q.X1, q.Y1), (q.X0, q.Y0)];
        var found = new PageBarcode(new FoundBarcode { Format = format, Text = content, Corners = corners }, corners, 0);
        var warning = content.Length > 0
            ? Tr.T("Aus dem Text der Code-Schrift abgeleitet, nicht gelesen — vor dem Ändern prüfen.")
            : Tr.T("Code lässt sich nicht lesen — Inhalt neu eingeben: „Code ändern …“ setzt an seine Stelle einen neuen Code, dessen Inhalt im Bereich eingetragen wird.");
        var meaning = content.Length > 0 ? BarcodeInterpreter.Interpret(format, content, false) : new BarcodeMeaning(FormatName(format), [], null, []);
        meaning = meaning with { Warnings = [warning, .. meaning.Warnings] };
        var culture = Tr.Culture;
        var (width, height) = found.Size;
        var rows = new List<(string, string)>
        {
            (Tr.T("Art"), FormatName(format)),
            (Tr.T("Schrift"), fontName),
            (Tr.T("Maße"), Tr.T("{0} × {1} mm", (width / 72 * 25.4).ToString("N1", culture), (height / 72 * 25.4).ToString("N1", culture))),
        };
        var sections = new List<ImageInfoSection> { new(Tr.T("Code"), rows) };
        if (content.Length > 0)
        {
            sections.Add(new(Tr.T("Bedeutung: {0}", meaning.Kind), meaning.Rows.Select(r => (r.Label, r.Value))));
            sections.Add(new(Tr.T("Inhalt"), [(Tr.T("Zeichen"), content.Length.ToString(culture)), (Tr.T("Roh"), BarcodeInterpreter.Safe(content))]));
        }
        return new InspectedBarcode(page, found, meaning, sections.Where(s => s.Rows.Count > 0).ToList()) { FontBlock = block };
    }

    /// <summary>Tool switched off: clears the code and the frame.</summary>
    public void EndBarcode()
    {
        _barcodeStamp++;
        InspectedBarcode = null;
        BarcodeMissed = false;
        BarcodeReading = false;
        if (EditTarget == EditTarget.Code) EditTarget = EditTarget.None;
    }

    /// <summary>Why the content of the code must not be copied — null if it may: the file forbids extracting content, or a bar not yet applied covers the code.</summary>
    public string? BarcodeCopyBlocked()
    {
        if (InspectedBarcode is not { } code) return Tr.T("Kein Code ausgewählt.");
        if (code.Code.Code.Text.Length == 0) return Tr.T("Der Code ließ sich nicht lesen — es gibt keinen Inhalt zu kopieren.");
        if (!CopyAllowed)
            return Tr.T("Dieses Dokument erlaubt kein Kopieren von Text (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen.");
        if (UnderBar(code)) return Tr.T("Über diesem Code liegt eine Schwärzung, die noch nicht angewandt ist — sein Inhalt wird mit ihr entfernt.");
        return null;
    }

    private bool UnderBar(InspectedBarcode code)
    {
        var c = code.Code.Corners;
        double left = c.Min(p => p.X), right = c.Max(p => p.X), bottom = c.Min(p => p.Y), top = c.Max(p => p.Y);
        return ElementsOn(code.Page).OfType<RedactionElement>().Any(r => !r.Hidden && r.Rect.Left < right && r.Rect.Right > left && r.Rect.Bottom < top && r.Rect.Top > bottom);
    }

    /// <summary>The sections of the panel: the code itself and what it means.</summary>
    private static InspectedBarcode Describe(int page, PageBarcode found)
    {
        var c = found.Code;
        var meaning = BarcodeInterpreter.Interpret(c.Format, c.Text, c.Qr?.Gs1 == true || c.DataMatrix?.Gs1 == true);
        var culture = Tr.Culture;
        string N(double v, int decimals) => v.ToString("N" + decimals, culture);
        var rows = new List<(string, string)> { (Tr.T("Art"), FormatName(c.Format)) };
        if (c.Qr is { } q)
        {
            rows.Add((Tr.T("Größe"), Tr.T("Version {0} ({1} × {1} Module)", q.Version, 17 + 4 * q.Version)));
            rows.Add((Tr.T("Fehlerkorrektur"), q.Level switch
            {
                Core.Barcodes.Qr.QrErrorCorrection.L => Tr.T("L (etwa 7 % wiederherstellbar)"),
                Core.Barcodes.Qr.QrErrorCorrection.M => Tr.T("M (etwa 15 % wiederherstellbar)"),
                Core.Barcodes.Qr.QrErrorCorrection.Q => Tr.T("Q (etwa 25 % wiederherstellbar)"),
                _ => Tr.T("H (etwa 30 % wiederherstellbar)"),
            }));
            rows.Add((Tr.T("Datenarten"), string.Join(", ", q.Modes)));
            if (q.Charset is { } cs) rows.Add((Tr.T("Zeichensatz"), cs));
            rows.Add((Tr.T("Maske"), q.Mask.ToString(culture)));
            rows.Add((Tr.T("Repariert"), Tr.T("{0} von höchstens {1} Codewörtern", q.Corrected, q.Capacity.Repairable)));
            if (q.Chain is { } chain) rows.Add((Tr.T("Verkettet"), Tr.T("Teil {0} von {1}", chain.Index, chain.Count)));
            if (q.Mirrored) rows.Add((Tr.T("Gespiegelt"), Tr.T("ja")));
        }
        if (c.DataMatrix is { } d)
        {
            rows.Add((Tr.T("Größe"), Tr.T("{0} × {1} Module", d.Rows, d.Columns)));
            rows.Add((Tr.T("Kodierung"), string.Join(", ", d.Schemes)));
            if (d.Charset is { } cs) rows.Add((Tr.T("Zeichensatz"), cs));
            if (d.Macro is { } macro) rows.Add((Tr.T("Umschlag"), "[)> " + macro));
            rows.Add((Tr.T("Repariert"), Tr.T("{0} von höchstens {1} Codewörtern", d.Corrected, d.Capacity.Repairable)));
            if (d.Chain is { } chain) rows.Add((Tr.T("Verkettet"), Tr.T("Teil {0} von {1}", chain.Index, chain.Count)));
        }
        if (c.Linear is { } l)
        {
            rows.Add((Tr.T("Prüfzeichen"), l.CheckValid == true ? Tr.T("vorhanden und richtig") : Tr.T("keines")));
            if (l.Sets.Count > 0) rows.Add((Tr.T("Zeichensätze"), string.Join(", ", l.Sets)));
        }
        var (width, height) = found.Size;
        rows.Add((Tr.T("Maße"), Tr.T("{0} × {1} mm", N(width / 72 * 25.4, 1), N(height / 72 * 25.4, 1))));
        rows.Add((Tr.T("Modulgröße"), Tr.T("{0} mm", N(found.ModuleMillimetres, 2))));
        var needed = c.Format switch { BarcodeFormat.Qr => 4, BarcodeFormat.DataMatrix => 1, BarcodeFormat.Ean13 or BarcodeFormat.UpcA => 7, _ => 10 };
        rows.Add((Tr.T("Ruhezone"), c.QuietZone >= 12 ? Tr.T("ausreichend") : c.QuietZone >= needed
            ? Tr.T("{0} Module (ausreichend)", N(Math.Floor(c.QuietZone), 0))
            : Tr.T("{0} Module (zu knapp, empfohlen {1})", N(Math.Floor(c.QuietZone), 0), needed)));
        if (found.Rotation != 0) rows.Add((Tr.T("Gedreht"), found.Rotation.ToString(culture) + "°"));
        var sections = new List<ImageInfoSection>
        {
            new(Tr.T("Code"), rows),
            new(Tr.T("Bedeutung: {0}", meaning.Kind), meaning.Rows.Select(r => (r.Label, r.Value))),
            new(Tr.T("Inhalt"), [(Tr.T("Zeichen"), c.Text.Length.ToString(culture)), (Tr.T("Roh"), BarcodeInterpreter.Safe(c.Text))]),
        };
        return new InspectedBarcode(page, found, meaning, sections.Where(s => s.Rows.Count > 0).ToList());
    }

    /// <summary>
    /// „Code ändern“ (KONZEPT.md, section 5.19, step 3) for the code read with the tool: an own code element is simply selected; a code of
    /// the page goes as when redacting — with a white bar, the human-readable text below it and a link over it included — and a new code
    /// element with the content read takes its place, to be selected for the panel. One undo step; a hint explains why it is not possible.
    /// </summary>
    /// <returns>The code element to change; null if there is none.</returns>
    public async Task<ElementRef?> ReplaceInspectedCodeAsync()
    {
        if (InspectedBarcode is not { } inspected || (uint)inspected.Page >= (uint)History.Count) return null;
        if (!CanEditElements)
        {
            Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
            return null;
        }
        var page = inspected.Page;
        var item = History.Pages[page];
        // an own code (inserted or changed before): it is an element already
        double cx = inspected.Code.Corners.Average(c => c.X), cy = inspected.Code.Corners.Average(c => c.Y);
        if (item.Elements.OfType<CodeElement>().LastOrDefault(e => cx >= e.Rect.Left && cx <= e.Rect.Right && cy >= e.Rect.Bottom && cy <= e.Rect.Top) is { } own)
            return new ElementRef(page, own);
        CodeReplacementPlan? plan;
        Core.Pdf.Editing.RedactionResult? emptied = null;
        string? refusal;
        try
        {
            (plan, emptied, refusal) = await WorkAsync(() => Task.Run(() =>
            {
                using var use = Renderer.Use(item.Source);
                if (use is null) return (null, null, null);
                CodeReplacementPlan? planned;
                string? why;
                if (inspected.FontBlock is { } block)
                {
                    // a code in a bar code font that could not be read: the block goes, a new code takes its place (phase 8.2)
                    // the blocks of the page, for the human-readable line below the code; detected here if they are not known yet — with the
                    // block alone, the old line stayed beside the new code (No. 611)
                    var blocks = BlocksOn(page) ?? TextBlocks.Find(item.Source, item.Page);
                    planned = CodeReplacement.ForFontBlock(block, blocks, inspected.Code.Code.Format, inspected.Code.Code.Text, out why);
                }
                else
                {
                    var list = Renderer.TryGetDisplayList(item.Id) ?? Renderer.GetDisplayList(item.Id).GetAwaiter().GetResult();
                    if (list is null) return ((CodeReplacementPlan?)null, (Core.Pdf.Editing.RedactionResult?)null, (string?)null);
                    planned = CodeReplacement.Plan(item.Source, item.Page, list, inspected.Code, out why, Renderer.Images);
                }
                return planned is null
                    ? (null, null, why)
                    : (planned, Redactor.Apply(item.Source, item.Page, [planned.Area], paper: true), null);
            }));
        }
        catch (Exception e) when (e is Core.Pdf.PdfException or InvalidOperationException)
        {
            Core.Platform.AppLog.Write("Code ändern", e.ToString());
            Notify(Tr.T("Dieser Code lässt sich nicht ändern: {0}", e.Message));
            return null;
        }
        if (refusal is not null)
        {
            Notify(refusal);
            return null;
        }
        // changed in the meantime (undo, another step): not onto a different state
        if (plan is null || emptied is null || (uint)page >= (uint)History.Count || !ReferenceEquals(History.Pages[page], item)) return null;
        var pages = History.Pages.ToArray();
        pages[page] = item.WithEmptiedArea(emptied, [.. item.Elements, plan.Element]);
        History.Apply(Tr.T("Code ändern"), pages);
        return new ElementRef(page, plan.Element);
    }

    /// <summary>Display name of a kind of code.</summary>
    public static string FormatName(BarcodeFormat format) => CodeSymbol.Name(format);
}
