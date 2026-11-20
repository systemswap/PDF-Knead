using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Rendering;

namespace PdfKnead.ViewModels;

/// <summary>A page in the page panel: number, format, aspect ratio for the placeholder.</summary>
/// <summary>The symbol at the top right of a tile.</summary>
public enum PageBadge
{
    /// <summary>No symbol.</summary>
    None,
    /// <summary>The page is straightened (the symbol discards it).</summary>
    Applied,
    /// <summary>The straightening is discarded (the symbol applies it again).</summary>
    Discarded,
}

public sealed partial class PageViewModel(PageItem item, int index) : ObservableObject
{
    /// <summary>Width of the placeholder in the page panel; the height follows the aspect ratio.</summary>
    public const double ThumbnailWidth = 112;

    /// <summary>Entry in the history; it is replaced when only the page's elements change (the ID stays the same).</summary>
    [ObservableProperty] public partial PageItem Item { get; set; } = item;

    public PdfPage Page => Item.Page;

    /// <summary>Key for tiles and thumbnails (stays the same when the page is moved).</summary>
    public int Key => Item.Id;

    /// <summary>Position in the document, from 0 (changes when pages are moved or deleted).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Number))]
    public partial int Index { get; set; } = index;

    /// <summary>Whether the page is selected in the page panel (several are possible).</summary>
    [ObservableProperty] public partial bool IsSelected { get; set; }

    /// <summary>Coloured marks below the tile (parts when splitting); null = none.</summary>
    [ObservableProperty] public partial IReadOnlyList<Avalonia.Media.Color>? Marks { get; set; }

    /// <summary>The symbol at the top right of the tile (scanning: straightened, or straightening discarded); clicking it raises <see cref="Controls.PageGrid.BadgeClicked"/>.</summary>
    [ObservableProperty] public partial PageBadge Badge { get; set; }

    /// <summary>What the symbol at the top right does, shown when the pointer rests on it; null: no tooltip.</summary>
    public string? BadgeTip { get; init; }

    /// <summary>
    /// The page starts a group of its own in the tiles (scanning: a stack), with this hint above it after a small gap; null: no new group.
    /// Set when the tiles are built.
    /// </summary>
    public string? GroupHeader { get; init; }

    public int Number => Index + 1;

    public string Format => PageFormat.Describe(Page.Width, Page.Height);

    public double ThumbnailHeight => Math.Clamp(ThumbnailWidth * Page.Height / Page.Width, 24, ThumbnailWidth * 3);

    /// <summary>Placeholder size in the document view: portrait pages are 420 high, landscape and square pages 560 wide.</summary>
    public double PreviewWidth => Page.Width >= Page.Height ? 560 : 420 * Page.Width / Page.Height;

    public double PreviewHeight => Page.Width >= Page.Height ? 560 * Page.Height / Page.Width : 420;
}

/// <summary>A row in the properties: label (German text, shown translated) and value.</summary>
public sealed record PropertyRow(string Label, string Value);

/// <summary>
/// An open document (one tab). Keeps the <see cref="PdfDocument"/> open until the
/// tab is closed.
/// </summary>
public sealed partial class DocumentViewModel : ObservableObject, IDisposable
{
    /// <summary>Creates the view model for an opened or new document.</summary>
    /// <param name="path">Path of the file; for new documents only the name.</param>
    /// <param name="document">The opened document; the view model takes ownership.</param>
    /// <param name="password">Password with which the file was opened (for reopening after saving).</param>
    /// <param name="untitled">New document (File → New): <paramref name="path"/> is only the name; it is saved via „Speichern unter“.</param>
    public DocumentViewModel(string path, PdfDocument document, string? password = null, bool untitled = false)
    {
        Path = path;
        IsUntitled = untitled;
        Document = document;
        _password = password;
        _fileDocument = document;
        History = new PageHistory(PageItem.From(document), document);
        Pages = new ObservableCollection<PageViewModel>(History.Pages.Select((p, i) => new PageViewModel(p, i)));
        RememberDiskState();
        CurrentPage = Pages.FirstOrDefault();
        Renderer = new DocumentRenderer
        {
            EnhanceThinLines = Services.UserSettings.Current.EnhanceThinLines,
            OverprintPreview = Services.UserSettings.Current.OverprintPreview,
            HideOwnElements = true,
        };
        Renderer.SetPages(RenderPages());
        History.Changed += OnHistoryChanged;
        ResetDescription();
        KeepTitleOnCleanup = IsTagged;
        _ = CheckSignaturesAsync();
        _ = CheckFakeRedactionsAsync();
    }

    /// <summary>The open file (catalogue, bookmarks and metadata are taken from it when saving).</summary>
    public PdfDocument Document { get; private set; }

    private string? _password;

    /// <summary>The document of the file last opened or saved; null once it has been released.</summary>
    private PdfDocument? _fileDocument;

    /// <summary>Renders tiles and thumbnails in the background.</summary>
    public DocumentRenderer Renderer { get; }

    // ------------------------------------------------------------ View: zoom and rotation

    public const double MinZoom = 0.05;
    public const double MaxZoom = 64;

    /// <summary>
    /// The next step of the zoom buttons and of Ctrl+Plus/Ctrl+Minus: the next multiple of 5 % above or below the zoom as shown (whole percent,
    /// as <see cref="ZoomText"/> rounds) — 74 % → 75 % → 80 %, 74 % → 70 % → 65 % (decided on 13.11.2026). The mouse wheel still zooms
    /// smoothly.
    /// </summary>
    /// <param name="zoom">The current zoom factor.</param>
    /// <param name="direction">Positive: larger; otherwise smaller.</param>
    /// <returns>The zoom factor of the step, within <see cref="MinZoom"/> and <see cref="MaxZoom"/>.</returns>
    public static double ZoomStep(double zoom, int direction)
    {
        var shown = Math.Round(zoom * 100, MidpointRounding.AwayFromZero);
        var next = direction > 0 ? (Math.Floor(shown / 5) + 1) * 5 : (Math.Ceiling(shown / 5) - 1) * 5;
        return Math.Clamp(next / 100, MinZoom, MaxZoom);
    }

    /// <summary>Zoom factor; 1 = 100 % (one inch of paper = one inch on screen at 96 dpi).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomPercent), nameof(ZoomText))]
    public partial double Zoom { get; set; } = 1;

    /// <summary>Zoom mode; on opening the whole page is visible (A0 plans are scaled down as well).</summary>
    [ObservableProperty] public partial ZoomMode ZoomMode { get; set; } = ZoomMode.FitPage;

    /// <summary>Continuous, single page or two pages.</summary>
    [ObservableProperty] public partial PageLayout PageLayout { get; set; } = PageLayout.Continuous;

    private IReadOnlyList<OutlineItem>? _outline;

    /// <summary>Bookmarks (table of contents), read on first access.</summary>
    public IReadOnlyList<OutlineItem> Outline => _outline ??= PdfOutline.Read(Document);

    public bool HasOutline => Outline.Count > 0;

    /// <summary>Additional clockwise rotation of the view (0, 90, 180, 270 degrees); does not change the file.</summary>
    [ObservableProperty] public partial int Rotation { get; set; }

    /// <summary>Zoom in percent for the slider in the status bar; setting it switches to <see cref="ZoomMode.Custom"/>.</summary>
    public double ZoomPercent
    {
        get => Zoom * 100;
        set
        {
            ZoomMode = ZoomMode.Custom;
            Zoom = Math.Clamp(value / 100, MinZoom, MaxZoom);
        }
    }

    public string ZoomText => (Zoom * 100).ToString("0", Tr.Culture) + " %";

    /// <summary>Chooses „Ganze Seite“ or „Seitenbreite“; if the mode is already active, refits to the page in the middle.</summary>
    /// <param name="mode">The zoom mode.</param>
    public void Fit(ZoomMode mode)
    {
        if (ZoomMode == mode) FitRequested?.Invoke();
        else ZoomMode = mode;
    }

    /// <summary>Requests a refit although the mode does not change.</summary>
    public event Action? FitRequested;

    /// <summary>Asks the view to jump to a page (e.g. a click in the page panel).</summary>
    public event Action<int>? ScrollToPageRequested;

    /// <summary>Raises <see cref="ScrollToPageRequested"/>.</summary>
    /// <param name="index">Page index.</param>
    public void ScrollToPage(int index) => ScrollToPageRequested?.Invoke(index);

    /// <summary>Asks the view to show a destination with its view (place and zoom) on the page at a position of the view.</summary>
    public event Action<int, PdfDestination>? ScrollToDestinationRequested;

    /// <summary>Shows a destination (bookmark, link) with its view on the page at <paramref name="index"/> (a position of the view).</summary>
    public void ScrollTo(int index, PdfDestination target)
    {
        if (ScrollToDestinationRequested is { } handler) handler(index, target);
        else ScrollToPage(index);
    }

    /// <summary>Location of the file; changes with „Speichern unter“.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName), nameof(Folder), nameof(Title))]
    public partial string Path { get; private set; }

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Whether the document was created with File → New and never saved; „Speichern“ then asks for the location.</summary>
    public bool IsUntitled { get; private set; }

    /// <summary>For the tab and window title: modified documents are marked with a dot.</summary>
    public string Title => IsModified ? "● " + FileName : FileName;

    public string Folder => IsUntitled ? "" : System.IO.Path.GetDirectoryName(Path) ?? "";

    public string PdfVersion => Document.Version;

    /// <summary>Pages in their current order (a new list after large rearrangements).</summary>
    [ObservableProperty] public partial ObservableCollection<PageViewModel> Pages { get; private set; }

    public int PageCount => Pages.Count;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageStatus))]
    public partial PageViewModel? CurrentPage { get; set; }

    /// <summary>„Seite 3 von 12“ for the status bar.</summary>
    public string PageStatus => CurrentPage is { } p ? Tr.T("Seite {0} von {1}", p.Number, PageCount) : Tr.T("Seite – von –");

    public string Details => PageCount == 1
        ? Tr.T("PDF {0} · 1 Seite · {1}", PdfVersion, FormatSize(Document.FileSize))
        : Tr.T("PDF {0} · {1} Seiten · {2}", PdfVersion, PageCount, FormatSize(Document.FileSize));

    /// <summary>Notices from reading the file (file repaired …), one per line; empty = none.</summary>
    public string Problems => string.Join("\n", Document.Problems);

    /// <summary>Whether the notices have been dismissed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial bool ProblemsDismissed { get; set; }

    public bool HasProblems => !ProblemsDismissed && Document.Problems.Count > 0;

    /// <summary>All property labels (German texts, for the translation check).</summary>
    public static readonly IReadOnlyList<string> PropertyLabels =
    [
        "Titel", "Autor", "Thema", "Stichwörter", "Erstellt mit", "PDF erzeugt von", "Erstellt am", "Geändert am",
        "PDF-Version", "Seiten", "Seitengröße", "Dateigröße", "Speicherort", "Schutz", "Integrität (MAC)", "Erlaubt", "Gesperrt", "Rechte", "Zustand",
        "Kennzeichnungen",
    ];

    /// <summary>The claims of the document as read (once per document object; docs/ABLAUFPLAN.md, 6.1 step 6).</summary>
    private (Core.Pdf.Document.PdfDocument Document, Core.Pdf.Writing.ConformanceClaims? Claims)? _claims;

    private Core.Pdf.Writing.ConformanceClaims? Claims()
    {
        if (_claims is not { } known || !ReferenceEquals(known.Document, Document)) _claims = known = (Document, Core.Pdf.Writing.ConformanceClaims.Read(Document));
        return known.Claims;
    }

    /// <summary>Rows for „Eigenschaften“ in the File area.</summary>
    public IReadOnlyList<PropertyRow> Properties
    {
        get
        {
            var info = Document.Info;
            var rows = new List<PropertyRow>();
            void Add(string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) rows.Add(new PropertyRow(label, value));
            }

            Add("Titel", info.Title);
            Add("Autor", info.Author);
            Add("Thema", info.Subject);
            Add("Stichwörter", info.Keywords);
            Add("Erstellt mit", info.Creator);
            Add("PDF erzeugt von", info.Producer);
            Add("Erstellt am", info.Created?.ToLocalTime().ToString("g", Tr.Culture));
            Add("Geändert am", info.Modified?.ToLocalTime().ToString("g", Tr.Culture));
            Add("PDF-Version", PdfVersion);
            // what the file says it conforms to — its own statements, not checked (PDF/A, PDF/UA, declarations)
            if (Claims() is { } claims) Add("Kennzeichnungen", Tr.T("{0} — Angaben der Datei, nicht geprüft", claims.Summary));
            Add("Seiten", PageCount.ToString(Tr.Culture));
            if (Pages.Count > 0)
            {
                // Size of the first page; if other pages differ, that is noted
                var first = Pages[0].Format;
                Add("Seitengröße", Pages.Any(p => p.Format != first) ? Tr.T("{0} (andere Seiten abweichend)", first) : first);
            }
            if (!IsUntitled) Add("Dateigröße", FormatSize(Document.FileSize));
            Add("Speicherort", Folder);
            Add("Schutz", Protection());
            if (Integrity() is { } integrity) Add("Integrität (MAC)", integrity);
            if (Document.Security is not null)
            {
                // what is allowed and what is locked, both named (wish of 4.11.2026); what the file grants, also when opened as owner
                var rights = Rights;
                Add("Erlaubt", rights.All(r => !r.Allowed) ? Tr.T("nichts") : string.Join(", ", rights.Where(r => r.Allowed).Select(r => r.Label)));
                Add("Gesperrt", rights.All(r => r.Allowed) ? Tr.T("nichts") : string.Join(", ", rights.Where(r => !r.Allowed).Select(r => r.Label)));
                Add("Rechte", RightsNote);
            }
            if (Document.WasRepaired) Add("Zustand", Tr.T("beschädigt, beim Öffnen repariert"));
            return rows;
        }
    }

    /// <summary>The line for the integrity protection (ISO/TS 32004); null where the file has none and asks for none (No. 556).</summary>
    private string? Integrity()
    {
        if (Document.Security is null) return null;
        var check = Document.Integrity;
        var text = check.State switch
        {
            MacState.Valid => Tr.T("bestätigt"),
            MacState.Invalid => Tr.T("VERLETZT — {0}", check.Detail ?? Tr.T("die Datei wurde nach dem Schützen verändert")),
            MacState.MissingButRequired => Tr.T("fehlt, obwohl die Datei ihn verlangt"),
            MacState.NotCheckable => Tr.T("nicht prüfbar — {0}", check.Detail ?? ""),
            _ => null,
        };
        if (text is not null && check.EmptyUserPassword)
            text += " " + Tr.T("(die Datei öffnet ohne Passwort, der Schutz hält deshalb nichts ab)");
        return text;
    }

    private string Protection() => Document.Security is not { } s
        ? Tr.T("keiner")
        : s.AttachmentsLocked
        ? Tr.T("nur Dateianhänge, mit Passwort")
        : Tr.T("{0}, {1} Bit, geöffnet als {2}",
            s.StreamCipher switch
            {
                PdfCipher.Aes256Gcm => "AES-GCM",
                PdfCipher.Aes256 => "AES",
                PdfCipher.Aes128 => "AES",
                PdfCipher.Rc4 => "RC4",
                _ => "—",
            },
            s.KeyBits,
            s.IsOwner ? Tr.T("Besitzer") : Tr.T("Benutzer"));

    /// <summary>Formats a file size readably, in the number format of the selected language.</summary>
    /// <param name="bytes">Size in bytes.</param>
    /// <returns>Text such as „1,5 MB“ (units of 1024).</returns>
    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = unit == 0 || value >= 100 ? "0" : "0.#";
        return value.ToString(format, Tr.Culture) + " " + units[unit];
    }

    public void Dispose()
    {
        History.Changed -= OnHistoryChanged;
        _hiddenScan?.Cancel();
        EndCodePrefetch();
        DisposeText();
        Renderer.Dispose();
        foreach (var source in AllSources()) source.Dispose();
    }
}

/// <summary>Arrangement of the pages in the view.</summary>
public enum PageLayout
{
    Continuous,
    Single,
    TwoPages,
}

/// <summary>How the zoom is determined: fixed or fitted to the window.</summary>
public enum ZoomMode
{
    Custom,
    FitWidth,
    FitPage,
}
