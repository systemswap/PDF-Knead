using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Printing;
using PdfKnead.Rendering;
using PdfKnead.Services;
using SkiaSharp;

namespace PdfKnead.ViewModels;

/// <summary>A choice in a list of the print window (value and translated text).</summary>
public sealed record PrintChoice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Target in the print window: a printer or „Als PDF speichern“.</summary>
public sealed record PrintTarget(string Name, string Label, bool IsPdf)
{
    public override string ToString() => Label;
}

/// <summary>A further printer option (on Linux from CUPS, e.g. stapling).</summary>
public sealed partial class PrinterOptionViewModel(PrinterOption option, string? chosen) : ObservableObject
{
    public string Key => option.Key;
    public string Label => option.Label;
    public IReadOnlyList<PrinterChoice> Choices => option.Choices;

    [ObservableProperty]
    public partial PrinterChoice? Selected { get; set; } =
        option.Choices.FirstOrDefault(c => c.Value == chosen) ?? option.Choices.FirstOrDefault(c => c.Value == option.Default) ?? option.Choices.FirstOrDefault();
}

/// <summary>
/// Print window: the options on the left, including everything the printer offers, and on the right the preview of the actual
/// sheet. Every change rebuilds the sheets (in the background, debounced); exactly what the preview shows is printed. See
/// KONZEPT.md, section 5.7.
/// </summary>
public sealed partial class PrintViewModel : ObservableObject, IDisposable
{
    /// <summary>Longest edge of the preview image in pixels.</summary>
    private const int PreviewPixels = 1600;

    private readonly PdfDocument _main;
    private readonly PageItem[] _items;
    private readonly int _current;
    private readonly List<int> _selected;
    /// <summary>The pages to print (only those with elements, fields or annotations flattened), prepared once in the background.</summary>
    private readonly Task<PrintPages> _prepare;

    /// <summary>Registrations as a reader of the documents: closing and saving wait until printing no longer reads from them.</summary>
    private readonly IDisposable[] _uses;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private PrintJobSheets? _sheets;
    private List<PrintSheet> _layout = [];
    private Booklet? _booklet;
    private Poster? _poster;
    /// <summary>Pages of the document in print order (belonging to the sheets).</summary>
    private IReadOnlyList<int> _indices = [];
    private CancellationTokenSource? _rebuild;
    private PrinterCapabilities _caps = PrinterCapabilities.Unknown;
    private double _margin = 12;
    private byte[]? _driverData;
    private bool _loading = true;
    private CancellationTokenSource? _printing;
    private TaskCompletionSource<bool?>? _turn;

    /// <summary>Creates the print window state for a document; starts preparing the pages and loading the printers.</summary>
    /// <param name="doc">The document to print.</param>
    public PrintViewModel(DocumentViewModel doc)
    {
        _main = doc.Document;
        _items = [.. doc.History.Pages];
        _current = doc.CurrentPage?.Index ?? 0;
        _selected = doc.SelectedIndices();
        FileName = doc.FileName;
        var security = _main.Security;
        PrintAllowed = doc.HasAllRights || security!.Permissions.HasFlag(PdfPermissions.Print);
        // From revision 3 on, bit 12 („high quality“) is separate from „print“ (Table 22)
        LowResolutionOnly = !doc.HasAllRights && security is { Revision: >= 3 } s && !s.Permissions.HasFlag(PdfPermissions.PrintHighQuality);
        PdfAllowed = doc.CanChangeProtection;

        Ranges =
        [
            new(PrintRange.All, Tr.T("Alle Seiten ({0})", _items.Length)),
            new(PrintRange.Current, Tr.T("Aktuelle Seite ({0})", _current + 1)),
            new(PrintRange.Selected, _selected.Count == 1 ? Tr.T("Markierte Seite ({0})", PageRange.Format(_selected))
                : Tr.T("Markierte Seiten ({0})", PageRange.Format(_selected))),
            new(PrintRange.Custom, Tr.T("Bereich")),
        ];
        Subsets = [new(PrintSubset.All, Tr.T("Alle")), new(PrintSubset.Odd, Tr.T("Nur ungerade")), new(PrintSubset.Even, Tr.T("Nur gerade"))];
        Scalings =
        [
            new(PrintScaling.Fit, Tr.T("An den druckbaren Bereich anpassen")),
            new(PrintScaling.ActualSize, Tr.T("Tatsächliche Größe")),
            new(PrintScaling.ShrinkLarge, Tr.T("Nur zu große Seiten verkleinern")),
            new(PrintScaling.Custom, Tr.T("Eigene Größe")),
        ];
        Orientations = [new(PrintOrientation.Auto, Tr.T("Automatisch")), new(PrintOrientation.Portrait, Tr.T("Hochformat")), new(PrintOrientation.Landscape, Tr.T("Querformat"))];
        Kinds = [new(PrintKind.Normal, Tr.T("Normal")), new(PrintKind.Booklet, Tr.T("Broschüre")), new(PrintKind.Poster, Tr.T("Poster"))];
        PerSheetChoices = [.. PrintLayoutOptions.PagesPerSheetChoices.Select(n => new PrintChoice<int>(n, n == 1 ? Tr.T("1 Seite") : Tr.T("{0} Seiten", n)))];
        PosterSizes = [.. PosterFormats.Select(f => new PrintChoice<PosterOptions?>(new PosterOptions(f.Width, f.Height), Tr.T("{0} ({1} × {2} mm)", f.Name,
                Math.Round(f.Width / PageFormat.PointsPerMillimeter).ToString("0", Tr.Culture), Math.Round(f.Height / PageFormat.PointsPerMillimeter).ToString("0", Tr.Culture)))),
            new PrintChoice<PosterOptions?>(null, Tr.T("Eigene Größe (Prozent)"))];
        Orders = [new(SheetOrder.Horizontal, Tr.T("Zeilenweise")), new(SheetOrder.Vertical, Tr.T("Spaltenweise"))];
        Bindings = [new(false, Tr.T("Links (wie ein Buch)")), new(true, Tr.T("Rechts"))];

        var prefs = UserSettings.Current.Print ?? new PrintPreferences();
        SelectedRange = Ranges[_selected.Count > 1 ? 2 : 0];
        SelectedSubset = Subsets[0];
        SelectedScaling = Scalings.FirstOrDefault(c => c.Value == prefs.Scaling) ?? Scalings[0];
        Percent = (decimal)Math.Clamp(prefs.Percent, 1, 1000);
        SelectedOrientation = Orientations.FirstOrDefault(c => c.Value == prefs.Orientation) ?? Orientations[0];
        SelectedKind = Kinds.First(k => k.Value == prefs.Kind);
        SelectedPerSheet = PerSheetChoices.FirstOrDefault(c => c.Value == prefs.PagesPerSheet) ?? PerSheetChoices[0];
        var poster = prefs.PosterSettings ?? new PosterOptions();
        SelectedPosterSize = poster.Percent > 0 ? PosterSizes[^1]
            : PosterSizes.FirstOrDefault(s => s.Value is { } o && Math.Abs(o.TargetWidth - Math.Min(poster.TargetWidth, poster.TargetHeight)) < 1) ?? PosterSizes[1];
        PosterPercent = (decimal)Math.Clamp(poster.Percent > 0 ? poster.Percent : 200, PosterOptions.MinPercent, PosterOptions.MaxPercent);
        Overlap = (decimal)Math.Round(Math.Clamp(poster.Overlap, 0, PosterOptions.MaxOverlap) / PageFormat.PointsPerMillimeter, 1);
        CutMarks = poster.CutMarks;
        RegistrationMarks = poster.RegistrationMarks;
        TileLabels = poster.Labels;
        SelectedOrder = Orders.FirstOrDefault(c => c.Value == prefs.Order) ?? Orders[0];
        Borders = prefs.Borders;
        var booklet = prefs.BookletSettings ?? new BookletOptions(Gutter: 0);
        SelectedBinding = Bindings[booklet.RightBinding ? 1 : 0];
        Gutter = (decimal)Math.Round(Math.Clamp(booklet.Gutter, 0, 100) / PageFormat.PointsPerMillimeter, 1);
        SheetsPerBooklet = Math.Clamp(booklet.SheetsPerBooklet, 0, BookletOptions.MaxSheetsPerBooklet);
        BlanksBeforeBack = booklet.BlanksBeforeBack;
        Creep = (decimal)Math.Round(Math.Clamp(booklet.Creep, 0, 10) / PageFormat.PointsPerMillimeter, 2);
        Collate = prefs.Collate;
        CustomRange = PageRange.Format(_selected.Count > 0 ? _selected : [_current]);

        var pdf = new PrintTarget(PrintPreferences.PdfTarget, Tr.T("Als PDF speichern"), true);
        Targets.Add(pdf);
        SelectedTarget = pdf;

        // Pages as they are printed (only those with elements, fields or redactions flattened): once, in the background, cancellable
        var main = _main;
        var items = _items;
        var closing = _closing.Token;
        _uses = [.. items.Select(i => i.Source).Append(main).Distinct().Select(d => doc.Renderer.Use(d)).OfType<IDisposable>()];
        _prepare = Task.Run(() => PrintPages.Prepare(main, items, closing), closing);
        _ = LoadPrintersAsync(prefs.Printer);
    }

    public string FileName { get; }

    /// <summary>Whether the file's permissions allow printing (without the owner password only if the permission is set).</summary>
    public bool PrintAllowed { get; }

    /// <summary>Whether printing is limited to low resolution (permission „print“ without „high quality“): 150 dpi.</summary>
    public bool LowResolutionOnly { get; }

    /// <summary>Whether „Als PDF speichern“ is allowed: only if the protection may be changed (no encryption, owner password, or restrictions removed), otherwise an unprotected copy would be created; Save As keeps the protection.</summary>
    public bool PdfAllowed { get; }

    // ------------------------------------------------------------ Printers

    public ObservableCollection<PrintTarget> Targets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPdf), nameof(IsPrinter), nameof(PrimaryText), nameof(HasDriverDialog), nameof(PdfWithoutProtection))]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand))]
    public partial PrintTarget? SelectedTarget { get; set; }

    public bool IsPdf => SelectedTarget?.IsPdf != false;

    /// <summary>
    /// „Als PDF speichern“ from a protected file (possible only with the owner password): the sheets are saved without a password,
    /// and the window says so instead of doing it silently.
    /// </summary>
    public bool PdfWithoutProtection => IsPdf && _main.Security is not null;

    /// <summary>Checks whether a file is open in a tab (set by the main window); if so, it is not saved to.</summary>
    public Func<string, bool>? IsOpen { get; set; }
    public bool IsPrinter => !IsPdf;
    public bool HasDriverDialog => IsPrinter && Printers.HasDriverDialog;
    public string PrimaryText => IsPdf ? Tr.T("Speichern …") : Tr.T("Drucken");

    [ObservableProperty] public partial bool LoadingPrinters { get; set; } = true;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PaperChoices), nameof(SelectedPaperChoice))] public partial IReadOnlyList<PaperSize> Papers { get; set; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SelectedPaperChoice))] public partial PaperSize? SelectedPaper { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasTrays))] public partial IReadOnlyList<PrinterChoice> Trays { get; set; } = [];
    [ObservableProperty] public partial PrinterChoice? SelectedTray { get; set; }
    public bool HasTrays => Trays.Count > 1 && IsPrinter;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasQualities))] public partial IReadOnlyList<PrinterChoice> Qualities { get; set; } = [];
    [ObservableProperty] public partial PrinterChoice? SelectedQuality { get; set; }
    public bool HasQualities => Qualities.Count > 1 && IsPrinter;
    [ObservableProperty] public partial IReadOnlyList<PrintChoice<DuplexMode>> DuplexModes { get; set; } = [];
    [ObservableProperty] public partial PrintChoice<DuplexMode>? SelectedDuplex { get; set; }
    [ObservableProperty] public partial bool ColorAvailable { get; set; } = true;
    [ObservableProperty] public partial bool Grayscale { get; set; }
    [ObservableProperty] public partial decimal? Copies { get; set; } = 1;
    [ObservableProperty] public partial bool Collate { get; set; }
    public ObservableCollection<PrinterOptionViewModel> ExtraOptions { get; } = [];
    public bool HasExtraOptions => ExtraOptions.Count > 0 && IsPrinter;

    // ------------------------------------------------------------ Pages and layout

    public IReadOnlyList<PrintChoice<PrintRange>> Ranges { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsCustomRange))] public partial PrintChoice<PrintRange> SelectedRange { get; set; }
    public bool IsCustomRange => SelectedRange.Value == PrintRange.Custom;
    [ObservableProperty] public partial string CustomRange { get; set; } = "";
    public IReadOnlyList<PrintChoice<PrintSubset>> Subsets { get; }
    [ObservableProperty] public partial PrintChoice<PrintSubset> SelectedSubset { get; set; }
    [ObservableProperty] public partial bool Reverse { get; set; }

    public IReadOnlyList<PrintChoice<PrintScaling>> Scalings { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsCustomScaling))] public partial PrintChoice<PrintScaling> SelectedScaling { get; set; }
    public bool IsCustomScaling => SelectedScaling.Value == PrintScaling.Custom && IsSingle;
    [ObservableProperty] public partial decimal? Percent { get; set; }
    public IReadOnlyList<PrintChoice<PrintOrientation>> Orientations { get; }
    [ObservableProperty] public partial PrintChoice<PrintOrientation> SelectedOrientation { get; set; }
    /// <summary>Print type: normal, booklet, poster; below it only the options of the chosen type are shown.</summary>
    public IReadOnlyList<PrintChoice<PrintKind>> Kinds { get; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMultiple), nameof(IsCustomScaling), nameof(IsSingle), nameof(IsNormal), nameof(IsBooklet), nameof(IsNotBooklet),
        nameof(IsPoster), nameof(ShowBook), nameof(ShowSheets), nameof(ShowPosterOverview), nameof(PaperChoices), nameof(SelectedPaperChoice), nameof(SheetInfo))]
    [NotifyCanExecuteChangedFor(nameof(PreviousSheetCommand), nameof(NextSheetCommand))]
    public partial PrintChoice<PrintKind> SelectedKind { get; set; }

    public IReadOnlyList<PrintChoice<int>> PerSheetChoices { get; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMultiple), nameof(IsCustomScaling), nameof(IsSingle))]
    public partial PrintChoice<int> SelectedPerSheet { get; set; }

    public bool IsNormal => SelectedKind.Value == PrintKind.Normal;
    public bool IsMultiple => IsNormal && SelectedPerSheet.Value > 1;
    public bool IsSingle => IsNormal && SelectedPerSheet.Value == 1;
    public bool IsBooklet => SelectedKind.Value == PrintKind.Booklet;
    public bool IsNotBooklet => !IsBooklet;
    public bool IsPoster => SelectedKind.Value == PrintKind.Poster;

    // ------------------------------------------------------------ Poster (KONZEPT.md, section 5.11)

    /// <summary>Target formats in portrait (the orientation follows the page).</summary>
    private static readonly (string Name, double Width, double Height)[] PosterFormats =
        [.. new[] { "A3", "A2", "A1", "A0" }.Select(n => PageFormat.Millimeters(n) is { } mm
            ? (n, mm.Width * PageFormat.PointsPerMillimeter, mm.Height * PageFormat.PointsPerMillimeter) : (n, 0.0, 0.0))];

    /// <summary>Poster size: target format or (null) custom size in percent.</summary>
    public IReadOnlyList<PrintChoice<PosterOptions?>> PosterSizes { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsPosterPercent))] public partial PrintChoice<PosterOptions?> SelectedPosterSize { get; set; }
    public bool IsPosterPercent => SelectedPosterSize.Value is null;

    /// <summary>Enlargement in percent (with „Eigene Größe“).</summary>
    [ObservableProperty] public partial decimal? PosterPercent { get; set; }

    /// <summary>Overlap (glue margin) in mm.</summary>
    [ObservableProperty] public partial decimal? Overlap { get; set; }

    [ObservableProperty] public partial bool CutMarks { get; set; } = true;
    [ObservableProperty] public partial bool RegistrationMarks { get; set; } = true;
    [ObservableProperty] public partial bool TileLabels { get; set; } = true;

    /// <summary>„3 × 2 = 6 Blätter · 420 × 594 mm“ for the poster of the current sheet.</summary>
    [ObservableProperty] public partial string PosterInfo { get; private set; } = "";

    /// <summary>Whether the preview is an overview: the whole page with the sheet grid (otherwise the single sheet).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowPosterOverview), nameof(ShowSheets))] public partial bool PosterOverview { get; set; } = true;
    public bool ShowPosterOverview => IsPoster && PosterOverview && PosterGrid is not null;

    /// <summary>Grid of the poster the current sheet belongs to.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowPosterOverview), nameof(ShowSheets))] public partial PosterGrid? PosterGrid { get; private set; }

    /// <summary>The enlarged page for the overview (owned by the view model).</summary>
    [ObservableProperty] public partial SKImage? PosterImage { get; private set; }

    /// <summary>Tile of the current sheet in the grid (column + row · columns); -1 if none.</summary>
    public int PosterTile => _poster?.TileOf(SheetIndex) is { } t ? t.Row * t.Grid.Columns + t.Column : -1;

    [RelayCommand]
    private void ShowPosterOverviewMode() => PosterOverview = true;

    [RelayCommand]
    private void ShowPosterSheetMode() => PosterOverview = false;

    /// <summary>Click on a tile of the overview: shows this sheet large.</summary>
    /// <param name="column">Column in the grid, from 0.</param>
    /// <param name="row">Row in the grid, from 0.</param>
    public void OpenTile(int column, int row)
    {
        if (PosterGrid is not { } grid || column < 0 || row < 0 || column >= grid.Columns || row >= grid.Rows) return;
        SheetIndex = grid.FirstSheet + row * grid.Columns + column;
        PosterOverview = false;
    }

    private PosterOptions PosterSettings() =>
        (SelectedPosterSize.Value ?? new PosterOptions(Percent: (double)Math.Clamp(PosterPercent ?? 200, (decimal)PosterOptions.MinPercent, (decimal)PosterOptions.MaxPercent)))
        with
        {
            Overlap = (double)Math.Clamp(Overlap ?? 0, 0, 50) * PageFormat.PointsPerMillimeter,
            CutMarks = CutMarks,
            RegistrationMarks = RegistrationMarks,
            Labels = TileLabels,
        };

    partial void OnSelectedPosterSizeChanged(PrintChoice<PosterOptions?> value) => Rebuild();
    partial void OnPosterPercentChanged(decimal? value) => Rebuild();
    partial void OnOverlapChanged(decimal? value) => Rebuild();
    partial void OnCutMarksChanged(bool value) => Rebuild();
    partial void OnRegistrationMarksChanged(bool value) => Rebuild();
    partial void OnTileLabelsChanged(bool value) => Rebuild();

    // ------------------------------------------------------------ Booklet

    public IReadOnlyList<PrintChoice<bool>> Bindings { get; }
    [ObservableProperty] public partial PrintChoice<bool> SelectedBinding { get; set; }

    /// <summary>Gutter in mm (gap between the pages at the fold).</summary>
    [ObservableProperty] public partial decimal? Gutter { get; set; }

    /// <summary>Sheets per signature; 0 = everything in one signature.</summary>
    [ObservableProperty] public partial decimal? SheetsPerBooklet { get; set; }

    [ObservableProperty] public partial bool BlanksBeforeBack { get; set; } = true;

    /// <summary>Creep in mm per sheet (paper thickness); 0 = off.</summary>
    [ObservableProperty] public partial decimal? Creep { get; set; }

    /// <summary>Paper as booklet format, e.g. „A5 (auf A4 quer)“: half the size of each printer paper.</summary>
    public IReadOnlyList<PrintChoice<PaperSize>> PaperChoices => IsBooklet
        ? [.. Papers.Select(p => new PrintChoice<PaperSize>(p, Tr.T("{0} (auf {1} quer)", ShortName(p.Height / 2, p.Width), p.Label)))]
        : [.. Papers.Select(p => new PrintChoice<PaperSize>(p, p.Label))];

    /// <summary>„A5“ or „120 × 170 mm“.</summary>
    private static string ShortName(double w, double h) => PageFormat.Detect(w, h) is { } f ? f.Name
        : Tr.T("{0} × {1} mm", Math.Round(w / PageFormat.PointsPerMillimeter).ToString("0", Tr.Culture), Math.Round(h / PageFormat.PointsPerMillimeter).ToString("0", Tr.Culture));

    public PrintChoice<PaperSize>? SelectedPaperChoice
    {
        get => PaperChoices.FirstOrDefault(c => c.Value == SelectedPaper);
        set
        {
            if (value is not null && value.Value != SelectedPaper) SelectedPaper = value.Value;
        }
    }

    /// <summary>Booklet hint: missing large paper, booklet function of the driver.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasBookletHint))] public partial string BookletHint { get; private set; } = "";
    public bool HasBookletHint => BookletHint.Length > 0 && IsBooklet;

    /// <summary>Whether the preview shows a book (otherwise the printed sheets).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowBook), nameof(ShowSheets), nameof(SheetInfo))]
    [NotifyCanExecuteChangedFor(nameof(PreviousSheetCommand), nameof(NextSheetCommand))] public partial bool BookView { get; set; } = true;
    public bool ShowBook => IsBooklet && BookView && BookPages is not null;
    public bool ShowSheets => !ShowBook && !ShowPosterOverview;

    /// <summary>Pages of the booklet for the book preview.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowBook), nameof(ShowSheets))]
    [NotifyPropertyChangedFor(nameof(SheetInfo))] [NotifyCanExecuteChangedFor(nameof(PreviousSheetCommand), nameof(NextSheetCommand))]
    public partial BookPageCache? BookPages { get; private set; }

    /// <summary>Open spread of the book preview.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SheetInfo))]
    [NotifyCanExecuteChangedFor(nameof(PreviousSheetCommand), nameof(NextSheetCommand))]
    public partial int Spread { get; set; }

    private int SpreadCount => BookPages is { } pages ? Controls.BookletPreview.SpreadCount(pages.PageCount) : 0;

    [RelayCommand]
    private void ShowBookView() => BookView = true;

    [RelayCommand]
    private void ShowSheetView() => BookView = false;
    public IReadOnlyList<PrintChoice<SheetOrder>> Orders { get; }
    [ObservableProperty] public partial PrintChoice<SheetOrder> SelectedOrder { get; set; }
    [ObservableProperty] public partial bool Borders { get; set; }

    // ------------------------------------------------------------ Preview

    /// <summary>Image of the sheet (owned by the view model; the view only draws it).</summary>
    [ObservableProperty] public partial SKImage? PreviewImage { get; private set; }
    [ObservableProperty] public partial double SheetWidth { get; private set; } = 595.28;
    [ObservableProperty] public partial double SheetHeight { get; private set; } = 841.89;

    /// <summary>Non-printable margin in points (dashed in the preview); 0 for „Als PDF speichern“.</summary>
    [ObservableProperty] public partial double PrintableMargin { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SheetInfo))]
    [NotifyCanExecuteChangedFor(nameof(PreviousSheetCommand), nameof(NextSheetCommand))]
    public partial int SheetIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SheetInfo))]
    [NotifyCanExecuteChangedFor(nameof(PreviousSheetCommand), nameof(NextSheetCommand), nameof(PrintCommand))]
    public partial int SheetCount { get; private set; }

    public string SheetInfo => ShowBook ? BookInfo() : SheetCount == 0 ? ""
        : IsPoster && _poster?.TileOf(SheetIndex) is { } tile ? Tr.T("Blatt {0} von {1} · {2}", SheetIndex + 1, SheetCount, PosterGrid.Label(tile.Column, tile.Row))
        : Tr.T("Blatt {0} von {1}", SheetIndex + 1, SheetCount);

    /// <summary>„Titelseite“, „Seiten 2–3 von 8“, „Rückseite“.</summary>
    private string BookInfo()
    {
        if (BookPages is not { } pages) return "";
        var (left, right) = Controls.BookletPreview.PagesOf(Spread, pages.PageCount);
        return left < 0 ? Tr.T("Titelseite") : right < 0 ? Tr.T("Rückseite")
            : Tr.T("Seiten {0}–{1} von {2}", left + 1, right + 1, pages.PageCount);
    }

    /// <summary>Warning or error below the options (invalid range, clipped …); empty = none.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasProblem))] public partial string Problem { get; private set; } = "";
    public bool HasProblem => Problem.Length > 0;

    /// <summary>Brief summary of what is printed, such as „3 Blatt Papier · 2 Kopien“.</summary>
    [ObservableProperty] public partial string Summary { get; private set; } = "";

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(PrintCommand))] public partial bool Building { get; private set; } = true;

    // ------------------------------------------------------------ Printing

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChange))]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand))]
    public partial bool IsPrinting { get; private set; }

    public bool CanChange => !IsPrinting;
    [ObservableProperty] public partial string Progress { get; private set; } = "";

    /// <summary>Manual duplex: the fronts have been printed and the window asks for the stack to be turned over.</summary>
    [ObservableProperty] public partial bool AwaitingTurn { get; private set; }
    [ObservableProperty] public partial bool ReverseBacks { get; set; }

    /// <summary>Lets the user choose a save location (title, suggestion, folder); set by the view.</summary>
    public Func<string, string, string?, Task<string?>>? PickSavePath { get; set; }

    /// <summary>Dialogue of the printer manufacturer (Windows); set by the view, as it needs the window handle.</summary>
    public Func<string, PrinterSettings, byte[]?>? OpenDriverDialog { get; set; }

    /// <summary>Raised when printing or saving has finished, with the text for the main window's message; the window closes.</summary>
    public event Action<string>? Finished;

    // ------------------------------------------------------------ Loading

    private async Task LoadPrintersAsync(string? remembered)
    {
        IReadOnlyList<PrinterInfo> printers;
        try
        {
            printers = await Task.Run(Printers.List).WaitAsync(TimeSpan.FromSeconds(20), _closing.Token);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            printers = [];
        }
        if (_closing.IsCancellationRequested) return;
        foreach (var p in printers) Targets.Add(new PrintTarget(p.Name, p.ToString(), false));
        LoadingPrinters = false;
        var choice = Targets.FirstOrDefault(t => t.Name == remembered && (!t.IsPdf || PdfAllowed))
                     ?? Targets.FirstOrDefault(t => !t.IsPdf && printers.Any(p => p.IsDefault && p.Name == t.Name))
                     ?? Targets.FirstOrDefault(t => !t.IsPdf) ?? Targets[0];
        if (choice == SelectedTarget) _targetChange = TargetChangedAsync();
        else SelectedTarget = choice;
        await _targetChange;
        _loading = false;
        Rebuild();
    }

    partial void OnSelectedTargetChanged(PrintTarget? value)
    {
        OnPropertyChanged(nameof(HasTrays));
        OnPropertyChanged(nameof(HasQualities));
        OnPropertyChanged(nameof(HasExtraOptions));
        _targetChange = TargetChangedAsync();
    }

    /// <summary>The running printer change (querying capabilities takes time).</summary>
    private Task _targetChange = Task.CompletedTask;

    /// <summary>Different printer: what it can do, its remembered settings.</summary>
    private async Task TargetChangedAsync()
    {
        var target = SelectedTarget;
        if (target is null) return;
        _driverData = null;
        var prefs = (UserSettings.Current.Print ?? new PrintPreferences()).For(target.Name);
        PrinterCapabilities caps;
        if (target.IsPdf)
        {
            caps = PdfCapabilities();
        }
        else
        {
            Building = true;
            try
            {
                caps = await Task.Run(() => Printers.Capabilities(target.Name)).WaitAsync(TimeSpan.FromSeconds(20), _closing.Token);
            }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException)
            {
                caps = PrinterCapabilities.Unknown;
            }
            if (SelectedTarget != target) return;
        }
        _caps = caps;
        _applying = true;
        Papers = caps.Papers.Count > 0 ? caps.Papers : PdfCapabilities().Papers;
        SelectedPaper = Papers.FirstOrDefault(p => p.Value == prefs.Paper) ?? Papers.FirstOrDefault(p => p.Value == caps.DefaultPaper)
                        ?? Papers.FirstOrDefault(p => Math.Abs(p.Width - 595.28) < 2 && Math.Abs(p.Height - 841.89) < 2) ?? Papers.FirstOrDefault();
        Trays = caps.Trays;
        SelectedTray = Trays.FirstOrDefault(t => t.Value == prefs.Tray) ?? Trays.FirstOrDefault(t => t.Value == caps.DefaultTray) ?? Trays.FirstOrDefault();
        Qualities = caps.Qualities;
        SelectedQuality = Qualities.FirstOrDefault(q => q.Value == prefs.Quality) ?? Qualities.FirstOrDefault(q => q.Value == caps.DefaultQuality)
                          ?? Qualities.FirstOrDefault();
        var modes = new List<PrintChoice<DuplexMode>> { new(DuplexMode.OneSided, Tr.T("Einseitig")) };
        if (!target.IsPdf)
        {
            if (caps.Duplex != false)
            {
                modes.Add(new(DuplexMode.LongEdge, Tr.T("Beidseitig, an der langen Kante wenden")));
                modes.Add(new(DuplexMode.ShortEdge, Tr.T("Beidseitig, an der kurzen Kante wenden")));
            }
            modes.Add(new(DuplexMode.Manual, Tr.T("Beidseitig von Hand (Stapel umdrehen)")));
        }
        DuplexModes = modes;
        SelectedDuplex = modes.FirstOrDefault(m => m.Value == prefs.Duplex) ?? modes[0];
        ColorAvailable = caps.Color != false && !target.IsPdf;
        Grayscale = !target.IsPdf && (caps.Color == false || prefs.Grayscale);
        ExtraOptions.Clear();
        if (!target.IsPdf)
            foreach (var option in caps.Extra)
            {
                var vm = new PrinterOptionViewModel(option, prefs.Extra?.GetValueOrDefault(option.Key));
                // Booklet function of the printer on/off: update the hint
                vm.PropertyChanged += (_, _) => UpdateBookletHint();
                ExtraOptions.Add(vm);
            }
        OnPropertyChanged(nameof(HasExtraOptions));
        OnPropertyChanged(nameof(HasTrays));
        OnPropertyChanged(nameof(HasQualities));
        _applying = false;
        await UpdateMarginAsync();
        Rebuild();
    }

    /// <summary>Set while the printer change sets many values, so that the sheets are not rebuilt for each value.</summary>
    private bool _applying;

    /// <summary>„Als PDF speichern“: the usual formats, without margin.</summary>
    private static PrinterCapabilities PdfCapabilities() => PrinterCapabilities.Unknown with
    {
        Papers = [.. new[] { "A3", "A4", "A5", "Letter", "Legal" }.Select(n =>
        {
            var (w, h) = PageFormat.Millimeters(n)!.Value;
            return new PaperSize(n, n, w * PageFormat.PointsPerMillimeter, h * PageFormat.PointsPerMillimeter);
        })],
        DefaultPaper = "A4",
    };

    private async Task UpdateMarginAsync()
    {
        if (SelectedTarget is not { IsPdf: false } target)
        {
            _margin = 0;
            PrintableMargin = 0;
            return;
        }
        var settings = Settings();
        double? margin = null;
        try
        {
            margin = await Task.Run(() => Printers.Margin(target.Name, settings)).WaitAsync(TimeSpan.FromSeconds(10), _closing.Token);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
        }
        // Another printer chosen in the meantime: its margin applies
        if (SelectedTarget != target) return;
        // Unknown (CUPS): about 4 mm, which almost all printers leave free
        _margin = Math.Clamp(margin ?? 12, 0, 72);
        PrintableMargin = _margin;
    }

    // ------------------------------------------------------------ Changes

    partial void OnSelectedPaperChanged(PaperSize? value)
    {
        if (_applying || _loading) return;
        _ = PaperChangedAsync();
    }

    private async Task PaperChangedAsync()
    {
        await UpdateMarginAsync();
        Rebuild();
    }

    partial void OnSelectedTrayChanged(PrinterChoice? value) => UpdateSummary();
    partial void OnGrayscaleChanged(bool value)
    {
        _ = RenderPreviewAsync();
        if (BookPages is { } pages) pages.Grayscale = value;
        _posterImageFor = null;
        UpdatePosterGrid();
    }
    partial void OnCopiesChanged(decimal? value) => UpdateSummary();
    partial void OnSelectedDuplexChanged(PrintChoice<DuplexMode>? value) => UpdateSummary();
    partial void OnSelectedRangeChanged(PrintChoice<PrintRange> value) => Rebuild();
    partial void OnCustomRangeChanged(string value) => Rebuild();
    partial void OnSelectedSubsetChanged(PrintChoice<PrintSubset> value) => Rebuild();
    partial void OnReverseChanged(bool value) => Rebuild();
    partial void OnSelectedScalingChanged(PrintChoice<PrintScaling> value) => Rebuild();
    partial void OnPercentChanged(decimal? value) => Rebuild();
    partial void OnSelectedOrientationChanged(PrintChoice<PrintOrientation> value) => Rebuild();
    partial void OnSelectedPerSheetChanged(PrintChoice<int> value) => Rebuild();

    partial void OnSelectedKindChanged(PrintChoice<PrintKind> value)
    {
        if (value.Value == PrintKind.Booklet && !_applying && !_loading) PrepareBooklet();
        if (value.Value == PrintKind.Poster) PosterOverview = true;
        Rebuild();
    }

    partial void OnSelectedBindingChanged(PrintChoice<bool> value) => Rebuild();
    partial void OnGutterChanged(decimal? value) => Rebuild();
    partial void OnSheetsPerBookletChanged(decimal? value) => Rebuild();
    partial void OnBlanksBeforeBackChanged(bool value) => Rebuild();
    partial void OnCreepChanged(decimal? value) => Rebuild();

    /// <summary>
    /// Booklet chosen: duplex over the short edge (otherwise manual), preview as a book; starting from A4 paper this gives an A5
    /// booklet, and the paper stays as it is.
    /// </summary>
    private void PrepareBooklet()
    {
        BookView = true;
        if (IsPrinter)
            SelectedDuplex = DuplexModes.FirstOrDefault(m => m.Value == DuplexMode.ShortEdge) ?? DuplexModes.FirstOrDefault(m => m.Value == DuplexMode.Manual)
                             ?? SelectedDuplex;
    }

    /// <summary>Booklet settings in points.</summary>
    private BookletOptions BookletSettings() => new(
        SelectedBinding.Value,
        (double)Math.Clamp(Gutter ?? 0, 0, 100) * PageFormat.PointsPerMillimeter,
        (int)Math.Clamp(SheetsPerBooklet ?? 0, 0, BookletOptions.MaxSheetsPerBooklet),
        BlanksBeforeBack,
        (double)Math.Clamp(Creep ?? 0, 0, 2) * PageFormat.PointsPerMillimeter);

    /// <summary>
    /// Hint: which large paper is missing (an A4 booklet needs A3) and that the driver's booklet function must stay off.
    /// </summary>
    private void UpdateBookletHint()
    {
        var hints = new List<string>();
        if (IsPrinter && !Papers.Any(p => p.Height >= 1190 - 6))
            hints.Add(Tr.T("Für eine A4-Broschüre braucht der Drucker A3-Papier — dieser bietet es nicht an. „Als PDF speichern“ geht immer (z. B. für den Copyshop)."));
        if (IsPrinter && ExtraOptions.Any(o => o.Key.Contains("booklet", StringComparison.OrdinalIgnoreCase) &&
                                          o.Selected?.Value is { } v && !v.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                                          !v.Equals("Off", StringComparison.OrdinalIgnoreCase) && !v.Equals("False", StringComparison.OrdinalIgnoreCase)))
            hints.Add(Tr.T("Die Broschürenfunktion des Druckers ist eingeschaltet — bitte ausschalten, PDF-Knead ordnet die Seiten schon an."));
        BookletHint = string.Join("\n", hints);
        OnPropertyChanged(nameof(HasBookletHint));
    }
    partial void OnSelectedOrderChanged(PrintChoice<SheetOrder> value) => Rebuild();
    partial void OnBordersChanged(bool value) => Rebuild();
    partial void OnSheetIndexChanged(int value)
    {
        OnPropertyChanged(nameof(PosterTile));
        UpdatePosterGrid();
        _ = RenderPreviewAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void PreviousSheet()
    {
        if (ShowBook) Spread--;
        else SheetIndex--;
    }

    private bool CanGoBack => ShowBook ? Spread > 0 : SheetIndex > 0;

    [RelayCommand(CanExecute = nameof(CanGoOn))]
    private void NextSheet()
    {
        if (ShowBook) Spread++;
        else SheetIndex++;
    }

    private bool CanGoOn => ShowBook ? Spread < SpreadCount - 1 : SheetIndex < SheetCount - 1;

    /// <summary>Manufacturer's dialogue; afterwards the window's own controls show what was set there.</summary>
    [RelayCommand]
    private async Task DriverDialog()
    {
        if (SelectedTarget is not { IsPdf: false } target || OpenDriverDialog is null) return;
        if (OpenDriverDialog(target.Name, Settings()) is not { } data) return;
        _driverData = data;
        var read = Printers.FromDriverData(data, Settings());
        _applying = true;
        SelectedPaper = Papers.FirstOrDefault(p => p.Value == read.Paper) ?? SelectedPaper;
        SelectedTray = Trays.FirstOrDefault(t => t.Value == read.Tray) ?? SelectedTray;
        SelectedQuality = Qualities.FirstOrDefault(q => q.Value == read.Quality) ?? SelectedQuality;
        SelectedDuplex = DuplexModes.FirstOrDefault(d => d.Value == read.Duplex) ?? SelectedDuplex;
        if (ColorAvailable) Grayscale = read.Grayscale;
        Copies = read.Copies;
        _applying = false;
        await UpdateMarginAsync();
        Rebuild();
    }

    /// <summary>Printer settings as currently chosen.</summary>
    private PrinterSettings Settings() => new()
    {
        Paper = SelectedPaper?.Value,
        Tray = SelectedTray?.Value,
        Quality = SelectedQuality?.Value,
        Duplex = SelectedDuplex?.Value is DuplexMode.Manual ? DuplexMode.OneSided : SelectedDuplex?.Value ?? DuplexMode.OneSided,
        Grayscale = Grayscale,
        Copies = (int)Math.Clamp(Copies ?? 1, 1, 999),
        Collate = Collate,
        Extra = ExtraOptions.Where(o => o.Selected is not null).ToDictionary(o => o.Key, o => o.Selected!.Value),
        DriverData = _driverData,
    };

    // ------------------------------------------------------------ Building sheets

    /// <summary>Rebuilds the sheets (debounced: several changes in quick succession build once).</summary>
    private async void Rebuild()
    {
        if (_applying || _loading || _closing.IsCancellationRequested) return;
        _rebuild?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _rebuild = cts;
        Building = true;
        try
        {
            await Task.Delay(120, cts.Token);
            var pages = await _prepare;
            var indices = PrintSelection.Pages(pages.Count, SelectedRange.Value, _current, CustomRange, _selected, SelectedSubset.Value, Reverse);
            if (indices is null || indices.Count == 0)
            {
                await ReplaceAsync(null, []);
                Problem = indices is null
                    ? Tr.T("Seitenbereich ungültig — z. B. „1-3, 7“ (Seiten 1 bis {0}).", pages.Count)
                    : Tr.T("Keine Seiten zu drucken.");
                return;
            }
            var paper = SelectedPaper;
            var options = new PrintLayoutOptions
            {
                PaperWidth = paper?.Width ?? 595.28,
                PaperHeight = paper?.Height ?? 841.89,
                Margin = _margin,
                Scaling = SelectedScaling.Value,
                Percent = (double)(Percent ?? 100),
                Orientation = SelectedOrientation.Value,
                PagesPerSheet = IsNormal ? SelectedPerSheet.Value : 1,
                Order = SelectedOrder.Value,
                Borders = Borders,
            };
            var bookletOptions = IsBooklet ? BookletSettings() : null;
            var posterOptions = IsPoster ? PosterSettings() : null;
            var pageLabel = Tr.T("Seite {0}");
            // Only a new arrangement, no file and no copy (not a whole print PDF for every change)
            var (layout, booklet, poster, sheets) = await Task.Run(() =>
            {
                List<(double, double)> sizes = [.. indices.Select(pages.Size)];
                var bound = bookletOptions is null ? null : Booklet.Arrange(sizes, options, bookletOptions);
                var tiled = posterOptions is null ? null : Poster.Arrange(sizes, options, posterOptions, pageLabel);
                var arranged = bound?.Sheets ?? tiled?.Sheets ?? PrintLayout.Arrange(sizes, options);
                return (arranged, bound, tiled, new PrintJobSheets(pages, arranged, indices));
            }, cts.Token);
            if (cts.IsCancellationRequested) return;
            _indices = indices;
            await ReplaceAsync(sheets, layout, booklet, poster);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Problem = Tr.T("Die Druckvorschau ließ sich nicht erstellen: {0}", e.Message);
        }
        finally
        {
            if (_rebuild == cts)
            {
                _rebuild = null;
                Building = false;
            }
            cts.Dispose();
        }
    }

    /// <summary>Takes over new sheets (cheap: the pages themselves stay the same).</summary>
    private async Task ReplaceAsync(PrintJobSheets? sheets, List<PrintSheet> layout, Booklet? booklet = null, Poster? poster = null)
    {
        var old = _sheets;
        var oldPages = BookPages;
        _sheets = sheets;
        _layout = layout;
        _booklet = booklet;
        _poster = poster;
        _posterImageFor = null;   // pages or size may have changed
        // Book preview: same spread as long as it still exists
        BookPages = sheets is not null && booklet is not null ? new BookPageCache(sheets, booklet, _gate, Grayscale) : null;
        oldPages?.Dispose();
        if (BookPages is { } bookPages) Spread = Math.Min(Spread, Controls.BookletPreview.SpreadCount(bookPages.PageCount) - 1);
        if (old is not null && !IsPrinting) old.Dispose();   // only the drawing surface of the strips
        SheetCount = layout.Count;
        if (SheetIndex >= SheetCount) SheetIndex = Math.Max(0, SheetCount - 1);
        UpdatePosterGrid();
        OnPropertyChanged(nameof(PosterTile));
        OnPropertyChanged(nameof(SheetInfo));
        UpdateSummary();
        await RenderPreviewAsync();
    }

    /// <summary>Grid and details of the poster of the current sheet; the page overview is redrawn only for a different page.</summary>
    private void UpdatePosterGrid()
    {
        var grid = _poster?.TileOf(SheetIndex)?.Grid;
        PosterGrid = grid;
        PosterInfo = grid is null ? "" : Tr.T("{0} × {1} = {2} Blätter · {3} × {4} mm", grid.Columns, grid.Rows, grid.SheetCount,
            Math.Round(grid.Width * grid.Scale / PageFormat.PointsPerMillimeter).ToString("0", Tr.Culture),
            Math.Round(grid.Height * grid.Scale / PageFormat.PointsPerMillimeter).ToString("0", Tr.Culture));
        if (grid is not null && _posterImageFor != grid.Page) _ = RenderPosterImageAsync(grid);
        else if (grid is null) SetPosterImage(null);
    }

    /// <summary>Page of the overview for which <see cref="PosterImage"/> is drawn (position in the print pages).</summary>
    private int? _posterImageFor;

    /// <summary>Renders the poster's page as an image for the overview (in the background, like the preview).</summary>
    private async Task RenderPosterImageAsync(PosterGrid grid)
    {
        _posterImageFor = grid.Page;
        if (_sheets is not { } sheets || grid.Page >= _indices.Count) return;
        var page = _indices[grid.Page];
        var gray = Grayscale;
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_posterImageFor != grid.Page) return;
            var image = await Task.Run(() =>
            {
                var sheet = new PrintSheet(grid.Width, grid.Height, [new SheetPlacement(page, 0, 0, 1, grid.Width, grid.Height)]);
                using var bitmap = sheets.Pages.Render(sheet, PreviewPixels / Math.Max(grid.Width, grid.Height), _closing.Token);
                if (gray) ToGray(bitmap);
                return SKImage.FromBitmap(bitmap);
            });
            if (_posterImageFor == grid.Page) SetPosterImage(image);
            else image.Dispose();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetPosterImage(null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SetPosterImage(SKImage? image)
    {
        var old = PosterImage;
        PosterImage = image;
        if (old is not null && old != image) DisposeLater(old);
    }

    private void UpdateSummary()
    {
        if (_layout.Count == 0)
        {
            Summary = "";
            if (_poster is { TooLarge: true })
                Problem = Tr.T("Das Poster bräuchte mehr als {0} Blätter je Seite — bitte kleiner wählen.", PosterOptions.MaxSheetsPerPage);
            return;
        }
        var copies = (int)Math.Clamp(Copies ?? 1, 1, 999);
        var sheets = _layout.Count;
        var sides = SelectedDuplex?.Value is DuplexMode.LongEdge or DuplexMode.ShortEdge or DuplexMode.Manual || IsBooklet ? (sheets + 1) / 2 : sheets;
        var text = IsPdf ? (sheets == 1 ? Tr.T("1 Seite") : Tr.T("{0} Seiten", sheets))
            : sides == 1 ? Tr.T("1 Blatt Papier") : Tr.T("{0} Blatt Papier", sides);
        if (!IsPdf && copies > 1) text += " · " + Tr.T("{0} Kopien", copies);
        Summary = text;
        var clipped = _layout.Count(s => s.Clips(_margin));
        Problem = clipped == 0 ? "" : IsPdf
            ? Tr.T("Seiten ragen über das Papier hinaus und werden abgeschnitten.")
            : Tr.T("Seiten ragen über den druckbaren Bereich hinaus und werden abgeschnitten.");
        if (IsBooklet && _booklet is { CreepLimited: true } && Problem.Length == 0)
            Problem = Tr.T("Der Bundzuwachs ist größer als der freie Rand — die Seiten rücken nur bis an die Falz.");
        if (IsBooklet && IsPrinter && Problem.Length == 0)
            Problem = SelectedDuplex?.Value switch
            {
                DuplexMode.OneSided => Tr.T("Eine Broschüre wird beidseitig gedruckt — „an der kurzen Kante wenden“ oder „von Hand“ wählen."),
                DuplexMode.LongEdge => Tr.T("Für Broschüren an der kurzen Kante wenden — sonst stehen die Rückseiten auf dem Kopf."),
                _ => "",
            };
        UpdateBookletHint();
    }

    /// <summary>Renders the preview of the current sheet (in the background; in greyscale if it is printed that way).</summary>
    private async Task RenderPreviewAsync()
    {
        // Debounced: a request overtaken while waiting does not draw (otherwise pressing Page Down ten times would draw the last sheet ten times)
        var wanted = ++_previewWanted;
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (wanted != _previewWanted) return;
            if (_sheets is not { } sheets || SheetIndex >= sheets.SheetCount)
            {
                SetPreview(null);
                return;
            }
            var index = SheetIndex;
            var gray = Grayscale;
            var (w, h) = sheets.SheetSize(index);
            var image = await Task.Run(() =>
            {
                using var bitmap = sheets.Render(index, PreviewPixels / Math.Max(w, h), _closing.Token);
                if (gray) ToGray(bitmap);
                return SKImage.FromBitmap(bitmap);
            });
            (SheetWidth, SheetHeight) = (w, h);
            SetPreview(image);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetPreview(null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private int _previewWanted;

    /// <summary>
    /// Sets a new preview image. The old one is released only after a short delay, as the view may still be drawing with it; merely
    /// dropping the reference would hold many MB of native memory until the next GC.
    /// </summary>
    private void SetPreview(SKImage? image)
    {
        var old = PreviewImage;
        PreviewImage = image;
        if (old is not null && old != image) DisposeLater(old);
    }

    /// <summary>Releases an image once the view is certainly no longer drawing with it (after 2 seconds, on the UI thread).</summary>
    /// <param name="image">The image to release.</param>
    internal static void DisposeLater(IDisposable image) =>
        Avalonia.Threading.DispatcherTimer.RunOnce(image.Dispose, TimeSpan.FromSeconds(2));

    /// <summary>Converts to greyscale in place (like the printer: luminance).</summary>
    /// <param name="bitmap">A 32-bit RGBA or BGRA bitmap.</param>
    internal static void ToGray(SKBitmap bitmap)
    {
        var pixels = bitmap.GetPixelSpan();
        var (r, b) = bitmap.ColorType == SKColorType.Bgra8888 ? (2, 0) : (0, 2);
        for (var y = 0; y < bitmap.Height; y++)
        {
            var row = pixels.Slice(y * bitmap.RowBytes, bitmap.Width * 4);
            for (var x = 0; x < row.Length; x += 4)
                row[x] = row[x + 1] = row[x + 2] = (byte)((row[x + r] * 77 + row[x + 1] * 150 + row[x + b] * 29) >> 8);
        }
    }

    // ------------------------------------------------------------ Printing and saving

    /// <summary>Print only once the sheets match the settings (otherwise Enter right after a change would print the old ones).</summary>
    private bool CanPrint => !IsPrinting && !Building && SheetCount > 0 && (IsPdf ? PdfAllowed : PrintAllowed);

    [RelayCommand(CanExecute = nameof(CanPrint))]
    private async Task Print()
    {
        if (!CanPrint || _sheets is not { } sheets || SelectedTarget is not { } target) return;
        // Check the permission again: the button alone is not enough (switching to „Als PDF speichern“ could leave it enabled)
        if (target.IsPdf ? !PdfAllowed : !PrintAllowed) return;
        Remember();
        _printing = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        var cancel = _printing.Token;
        var task = target.IsPdf ? SaveAsPdfAsync(sheets, cancel) : PrintAsync(target, sheets, cancel);
        _job = task;
        await task;
    }

    private async Task PrintAsync(PrintTarget target, PrintJobSheets sheets, CancellationToken cancel)
    {
        var settings = Settings();
        var dpi = LowResolutionOnly ? 150 : Math.Clamp(int.TryParse(SelectedQuality?.Value.Split('x')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var q) ? q : 300, 150, 600);
        var all = Enumerable.Range(0, sheets.SheetCount).ToList();
        IsPrinting = true;
        try
        {
            if (SelectedDuplex?.Value == DuplexMode.Manual && all.Count > 1)
            {
                // Fronts 1, 3, 5 …; the backs afterwards, with a blank one for the last sheet if the count is odd
                var fronts = all.Where(i => i % 2 == 0).ToList();
                var backs = all.Where(i => i % 2 == 1).ToList();
                if (backs.Count < fronts.Count) backs.Add(-1);
                await RunJobAsync(target, sheets, fronts, settings, dpi, Tr.T("Vorderseiten"), cancel);
                _turn = new TaskCompletionSource<bool?>();
                AwaitingTurn = true;
                Progress = "";
                var reverse = await _turn.Task;
                AwaitingTurn = false;
                if (reverse is null) return;
                if (reverse == true) backs.Reverse();
                await RunJobAsync(target, sheets, backs, settings, dpi, Tr.T("Rückseiten"), cancel);
            }
            else
            {
                await RunJobAsync(target, sheets, all, settings, dpi, null, cancel);
            }
            Finished?.Invoke(all.Count == 1 ? Tr.T("1 Blatt an „{0}“ gesendet", target.Name) : Tr.T("{0} Blätter an „{1}“ gesendet", all.Count, target.Name));
        }
        catch (OperationCanceledException)
        {
            Problem = Tr.T("Drucken abgebrochen.");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Problem = Tr.T("Drucken fehlgeschlagen: {0}", e.Message);
        }
        finally
        {
            IsPrinting = false;
            AwaitingTurn = false;
            Progress = "";
            _turn = null;
        }
    }

    private async Task RunJobAsync(PrintTarget target, PrintJobSheets sheets, List<int> order, PrinterSettings settings, int dpi, string? part,
        CancellationToken cancel)
    {
        // The print path reports how many sheets it actually sends (including copies that PDF-Knead makes itself)
        var progress = new Progress<(int Done, int Total)>(n =>
            Progress = part is null ? Tr.T("Blatt {0} von {1} …", n.Done, n.Total) : $"{part}: " + Tr.T("Blatt {0} von {1} …", n.Done, n.Total));
        Progress = Tr.T("Wird vorbereitet …");
        var job = new PrintJob(target.Name, FileName, sheets, order, settings, dpi, LowResolution: LowResolutionOnly);
        await Task.Run(() => Printers.Print(job, progress, cancel), cancel);
    }

    /// <summary>Manual duplex: the stack has been turned over (<see cref="ReverseBacks"/>: backs in reverse order).</summary>
    [RelayCommand]
    private void ContinueBacks() => _turn?.TrySetResult(ReverseBacks);

    /// <summary>Cancels printing or saving (also while waiting for the stack to be turned over).</summary>
    [RelayCommand]
    private void CancelPrinting()
    {
        _turn?.TrySetResult(null);
        _printing?.Cancel();
    }

    /// <summary>The running print or save job (the window waits for it when closing).</summary>
    private Task _job = Task.CompletedTask;

    /// <summary>Waits until printing or saving has ended (quickly after <see cref="CancelPrinting"/>).</summary>
    /// <returns>A task that completes when the job has ended; it never faults.</returns>
    public Task WaitForJobAsync() => _job.ContinueWith(_ => { }, TaskScheduler.Default);

    private async Task SaveAsPdfAsync(PrintJobSheets sheets, CancellationToken cancel)
    {
        if (PickSavePath is null) return;
        var name = Path.GetFileNameWithoutExtension(FileName) + " " + Tr.T("(Druck)") + ".pdf";
        if (await PickSavePath(Tr.T("Als PDF speichern"), name, null) is not { } path) return;
        // Do not overwrite an open file (its tab would then read foreign content)
        if (IsOpen?.Invoke(Path.GetFullPath(path)) == true)
        {
            Problem = Tr.T("{0} ist geöffnet — zum Speichern einen anderen Namen wählen.", Path.GetFileName(path));
            return;
        }
        IsPrinting = true;
        Progress = Tr.T("Wird gespeichert …");
        try
        {
            var all = Enumerable.Range(0, sheets.SheetCount).ToList();
            await Task.Run(() =>
            {
                var temp = $"{path}.{Guid.NewGuid().ToString("N")[..8]}.pdfknead-tmp";   // unique, so that no file of the same name is overwritten
                try
                {
                    using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                        sheets.WritePdf(all, file, cancel);
                    File.Move(temp, path, overwrite: true);
                }
                catch
                {
                    try { File.Delete(temp); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    throw;
                }
            }, cancel);
            Finished?.Invoke(Tr.T("Gespeichert: {0}", path));
        }
        catch (OperationCanceledException)
        {
            Problem = Tr.T("Speichern abgebrochen.");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Problem = Tr.T("Speichern fehlgeschlagen: {0}", e.Message);
        }
        finally
        {
            IsPrinting = false;
            Progress = "";
        }
    }

    /// <summary>Remembers the settings (per printer those of the printer, plus the layout and the printer used last).</summary>
    private void Remember()
    {
        if (SelectedTarget is not { } target) return;
        var printer = new PrinterPreferences(SelectedPaper?.Value, SelectedTray?.Value, SelectedQuality?.Value,
            SelectedDuplex?.Value ?? DuplexMode.OneSided, Grayscale,
            ExtraOptions.Where(o => o.Selected is not null).ToDictionary(o => o.Key, o => o.Selected!.Value));
        UserSettings.Update(s => s with
        {
            Print = ((s.Print ?? new PrintPreferences()) with
            {
                Scaling = SelectedScaling.Value,
                Percent = (double)(Percent ?? 100),
                Orientation = SelectedOrientation.Value,
                PagesPerSheet = SelectedPerSheet.Value,
                Order = SelectedOrder.Value,
                Borders = Borders,
                Collate = Collate,
                Booklet = IsBooklet,
                BookletSettings = BookletSettings(),
                Poster = IsPoster,
                PosterSettings = PosterSettings(),
            }).With(target.Name, printer),
        });
    }

    /// <summary>For tests: whether the sheets match the settings.</summary>
    internal bool IsReady => !Building && !_loading && !LoadingPrinters;

    /// <summary>For tests: the current sheets.</summary>
    internal IReadOnlyList<PrintSheet> Layout => _layout;

    /// <summary>For tests: the pages to print.</summary>
    internal Task<PrintPages> Prepared => _prepare;

    /// <summary>For tests: completes when everything has been released (pages and the registration as a reader of the documents).</summary>
    internal Task Released { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Window closed: cancels everything; resources are released in the background once preparing and printing have really ended,
    /// and only then does the window unregister as a reader of the documents (closing and saving the document wait for that).
    /// </summary>
    public void Dispose()
    {
        _closing.Cancel();
        _rebuild?.Cancel();
        CancelPrinting();
        BookPages?.Dispose();
        SetPreview(null);
        SetPosterImage(null);
        var job = _job;
        Released = Task.Run(async () =>
        {
            try { await job.ConfigureAwait(false); }
            catch (Exception e) when (e is not OutOfMemoryException) { }
            PrintPages? pages = null;
            try { pages = await _prepare.ConfigureAwait(false); }
            catch (Exception e) when (e is not OutOfMemoryException) { }
            if (pages is not null)
            {
                pages.Dispose();
                await pages.Closed.ConfigureAwait(false);
            }
            foreach (var use in _uses) use.Dispose();
        });
    }
}


/// <summary>
/// Pages of the booklet for the book preview: each sheet side is drawn once and cut into its two halves, so the preview shows
/// exactly the print data. At most <see cref="MaxImages"/> images; evicted ones are released shortly afterwards (the view may
/// still be drawing with them).
/// </summary>
public sealed class BookPageCache : Controls.IBookPages, IDisposable
{
    private const int MaxImages = 24;

    /// <summary>Height of a page image (and of its sheet) in pixels.</summary>
    private const int Pixels = 1100;

    private readonly PrintJobSheets _sheets;
    private readonly SemaphoreSlim _gate;
    private readonly Dictionary<int, SKImage> _images = [];
    private readonly LinkedList<int> _order = [];
    private readonly HashSet<int> _loading = [];

    /// <summary>Pages (in reading order) per sheet side, so that a loaded sheet need not search all pages.</summary>
    private readonly Dictionary<int, List<(int Page, bool Right)>> _bySheet = [];
    private readonly IReadOnlyList<BookletPage> _pages;
    private volatile bool _disposed;
    private bool _gray;
    private int _version;

    /// <summary>Creates the cache for a booklet.</summary>
    /// <param name="sheets">The printed sheets.</param>
    /// <param name="booklet">The booklet arrangement.</param>
    /// <param name="gate">Lock shared with the print preview, so that only one rendering runs at a time.</param>
    /// <param name="grayscale">Whether the images are drawn in greyscale.</param>
    public BookPageCache(PrintJobSheets sheets, Booklet booklet, SemaphoreSlim gate, bool grayscale)
    {
        _sheets = sheets;
        _gate = gate;
        _gray = grayscale;
        _pages = booklet.Pages;
        for (var i = 0; i < _pages.Count; i++)
        {
            if (!_bySheet.TryGetValue(_pages[i].Sheet, out var list)) _bySheet[_pages[i].Sheet] = list = [];
            list.Add((i, _pages[i].RightHalf));
        }
    }

    public int PageCount => _pages.Count;

    public (double Width, double Height) PageSize =>
        _sheets.SheetCount > 0 ? (_sheets.SheetSize(0).Width / 2, _sheets.SheetSize(0).Height) : (419.53, 595.28);

    public event Action? Changed;

    public bool Grayscale
    {
        get => _gray;
        set
        {
            if (_gray == value) return;
            _gray = value;
            Drop();
            Changed?.Invoke();
        }
    }

    /// <summary>Returns the image of a booklet page, or starts loading it.</summary>
    /// <param name="page">Page index in reading order.</param>
    /// <returns>The image, or null while it is being loaded (<see cref="Changed"/> follows).</returns>
    public SKImage? Get(int page)
    {
        if (_disposed || page < 0 || page >= PageCount) return null;
        int version;
        var sheet = _pages[page].Sheet;
        lock (_images)
        {
            if (_images.TryGetValue(page, out var image))
            {
                _order.Remove(page);
                _order.AddFirst(page);
                return image;
            }
            if (!_loading.Add(sheet)) return null;
            version = _version;
        }
        _ = LoadAsync(sheet, version);
        return null;
    }

    private async Task LoadAsync(int sheet, int version)
    {
        var dropped = new List<SKImage>();
        try
        {
            await _gate.WaitAsync();
            try
            {
                if (_disposed || version != _version) return;
                var (_, h) = _sheets.SheetSize(sheet);
                var gray = _gray;
                var (left, right) = await Task.Run(() =>
                {
                    using var bitmap = _sheets.Render(sheet, Pixels / h);
                    if (gray) PrintViewModel.ToGray(bitmap);
                    return (Half(bitmap, false), Half(bitmap, true));
                });
                lock (_images)
                {
                    if (version != _version || _disposed)
                    {
                        dropped.Add(left);
                        dropped.Add(right);
                        return;
                    }
                    foreach (var (page, isRight) in _bySheet.GetValueOrDefault(sheet) ?? [])
                    {
                        if (_images.Remove(page, out var old)) dropped.Add(old);
                        _images[page] = isRight ? right : left;
                        _order.Remove(page);
                        _order.AddFirst(page);
                    }
                    while (_order.Count > MaxImages)
                    {
                        var last = _order.Last!.Value;
                        _order.RemoveLast();
                        if (_images.Remove(last, out var old) && !_images.ContainsValue(old)) dropped.Add(old);
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // The preview is then missing for this page; printing still works
        }
        finally
        {
            lock (_images) _loading.Remove(sheet);
            foreach (var image in dropped.Distinct()) PrintViewModel.DisposeLater(image);
            // Also when discarded (greyscale switched): let the view ask again, otherwise the page would stay empty
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Changed?.Invoke());
        }
    }

    private static SKImage Half(SKBitmap bitmap, bool right)
    {
        var area = right ? new SKRectI(bitmap.Width / 2, 0, bitmap.Width, bitmap.Height) : new SKRectI(0, 0, bitmap.Width / 2, bitmap.Height);
        using var part = new SKBitmap();
        bitmap.ExtractSubset(part, area);
        return SKImage.FromBitmap(part);
    }

    /// <summary>Discards all images (released later).</summary>
    private void Drop()
    {
        List<SKImage> all;
        lock (_images)
        {
            all = [.. _images.Values.Distinct()];
            _images.Clear();
            _order.Clear();
            _version++;
        }
        foreach (var image in all) PrintViewModel.DisposeLater(image);
    }

    public void Dispose()
    {
        _disposed = true;
        Drop();
    }
}
