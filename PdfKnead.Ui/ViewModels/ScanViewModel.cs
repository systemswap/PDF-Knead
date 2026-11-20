using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Platform;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Scanning;
using PdfKnead.Core.Scanning.Escl;
using PdfKnead.Core.Scanning.Processing;
using PdfKnead.Rendering;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>What the scan window hands over: the pages in their order and how to take them over.</summary>
/// <param name="Pages">The scanned pages, processed and packed as PDF pages in their order; the receiver owns the document.</param>
/// <param name="NewDocument">true: as a new document; false: appended to the open document.</param>
/// <param name="Recognize">Whether to run the text recognition on the taken-over pages.</param>
public sealed record ScanResult(PdfDocument Pages, bool NewDocument, bool Recognize);

/// <summary>A colour mode with its name for the user.</summary>
/// <param name="Mode">The mode.</param>
/// <param name="DisplayName">The name.</param>
public sealed record ColorModeChoice(ScanColorMode Mode, string DisplayName)
{
    /// <summary>The choice for a mode.</summary>
    public static ColorModeChoice For(ScanColorMode mode) => new(mode, mode switch
    {
        ScanColorMode.Color => Tr.T("Farbe"),
        ScanColorMode.Gray => Tr.T("Graustufen"),
        _ => Tr.T("Schwarzweiß"),
    });
}

/// <summary>
/// One scanned page in the window: the raw scan, its processing (a recipe — the raw scan stays), and the small preview document behind its
/// tile, which shows the processed page.
/// </summary>
public sealed class ScanPage : IDisposable
{
    /// <summary>The longest edge of the tile image.</summary>
    internal const int TileEdge = 1000;

    /// <param name="image">The scanned page.</param>
    /// <param name="adjustments">Its processing; null: none.</param>
    public ScanPage(ScannedImage image, ScanAdjustments? adjustments = null)
    {
        Adjustments = adjustments ?? ScanAdjustments.None;
        SetImage(image);
    }

    // counts every change that alters the tile of this page (or of its other side): a tile computed in the background from an older state
    // is not shown (Fehlergedächtnis 860)
    private int _version;
    private ScanAdjustments _adjustments = ScanAdjustments.None;
    private bool _straightenOff;
    private double? _manualAngle;
    private ScanPage? _partner;

    /// <summary>The raw scan as it is now (turned).</summary>
    public ScannedImage Image { get; private set; } = null!;

    /// <summary>The processing of the page.</summary>
    public ScanAdjustments Adjustments
    {
        get => _adjustments;
        set => (_adjustments, _version) = (value, _version + 1);
    }

    /// <summary>Whether the straightening of this page is discarded (the page as scanned, the symbol at the tile turns grey).</summary>
    public bool StraightenOff
    {
        get => _straightenOff;
        set => (_straightenOff, _version) = (value, _version + 1);
    }

    /// <summary>An angle turned by hand („Frei drehen“), replacing what the method found; null: the method decides.</summary>
    public double? ManualAngle
    {
        get => _manualAngle;
        set => (_manualAngle, _version) = (value, _version + 1);
    }

    /// <summary>The other side of the same sheet (both sides scanned); its print helps to take out show-through.</summary>
    public ScanPage? Partner
    {
        get => _partner;
        set => (_partner, _version) = (value, _version + 1);
    }

    /// <summary>What the tile is computed from, as a stamp (UI thread): equal stamps, equal tile.</summary>
    internal (int Version, ScanPage? Partner, int PartnerVersion) TileState => (_version, _partner, _partner?._version ?? 0);

    /// <summary>
    /// The stack the page came with (a run of the feeder, both sides by turning as one): the tiles show stacks as groups while each lies
    /// together; a page from the flatbed joins the stack it was put into.
    /// </summary>
    public int Stack { get; set; }

    /// <summary>The other side, straightened, at the size of a copy of this page reduced to <paramref name="edge"/> (null: full size).</summary>
    private ScannedImage? Back(int? edge, CancellationToken cancel) => Partner is { } other
        ? other.Straightened(edge is { } e ? ScanPdf.Reduce(other.Image, e) : other.Image, cancel)
        : null;

    private readonly object _gate = new();
    private PageStraightening? _geometry;
    private (ScannedImage, string, bool, double?) _geometryKey;

    /// <summary>
    /// What straightening does to this page (found once on a reduced copy, again only when the image, the method, „Ränder abschneiden“ or
    /// the hand-set angle change). Any thread.
    /// </summary>
    public PageStraightening Geometry(CancellationToken cancel = default)
    {
        var key = (Image, Adjustments.Straighten, Adjustments.CropBorders, ManualAngle);
        lock (_gate)
            if (_geometry is { } known && _geometryKey == key) return known;
        var found = PageStraightening.Find(key.Image, key.Straighten, key.CropBorders, key.ManualAngle, cancel);
        lock (_gate) (_geometry, _geometryKey) = (found, key);
        return found;
    }

    /// <summary>Whether straightening changes this page (known once the geometry was found).</summary>
    public bool IsStraightened
    {
        get
        {
            lock (_gate) return _geometry?.ChangesPage == true;
        }
    }

    /// <summary>The page straightened (unless discarded), at the resolution of <paramref name="source"/> (the image or a reduced copy of it).</summary>
    public ScannedImage Straightened(ScannedImage source, CancellationToken cancel = default) =>
        StraightenOff ? source : Geometry(cancel).Apply(source, cancel);

    /// <summary>The one-page preview document (reduced, processed copy) shown in the tiles.</summary>
    public PdfDocument Preview { get; private set; } = null!;

    /// <summary>The page of <see cref="Preview"/>; its id keys the tiles of the renderer.</summary>
    public PageItem Item { get; private set; } = null!;

    /// <summary>Replaces the image (e.g. turned) and builds a new preview with a new id, so the renderer draws the tile anew.</summary>
    /// <param name="image">The new image.</param>
    /// <returns>The preview document that was replaced (dispose it when the renderer no longer reads it), or null.</returns>
    public PdfDocument? SetImage(ScannedImage image)
    {
        Image = image;
        _version++;
        return Use(BuildPreview());
    }

    /// <summary>Builds a preview document from the image and the processing (any thread; the page itself does not change).</summary>
    /// <param name="cancel">Stops the work.</param>
    /// <returns>The new preview document; hand it to <see cref="Use"/>.</returns>
    public PdfDocument BuildPreview(CancellationToken cancel = default)
    {
        var small = ScanPdf.Reduce(Image, TileEdge);
        return ScanPdf.Preview(ScanProcessor.Apply(Straightened(small, cancel), Adjustments, Back(TileEdge, cancel), cancel));
    }

    /// <summary>Shows a new preview document in the tile, with a new id (the renderer draws the tile anew).</summary>
    /// <param name="preview">The document from <see cref="BuildPreview"/>.</param>
    /// <returns>The preview document that was replaced, or null.</returns>
    public PdfDocument? Use(PdfDocument preview) => Use(preview, TileState);

    /// <summary>Shows a new preview document computed from <paramref name="state"/>.</summary>
    internal PdfDocument? Use(PdfDocument preview, (int Version, ScanPage? Partner, int PartnerVersion) state)
    {
        var old = Preview;
        Preview = preview;
        Item = new PageItem(Preview, Preview.Pages[0]);   // every page item gets an id of its own
        PreviewState = state;
        return old;
    }

    /// <summary>The <see cref="TileState"/> the tile shown was computed from (tests: equal once every tile is current).</summary>
    internal (int Version, ScanPage? Partner, int PartnerVersion) PreviewState { get; private set; }

    /// <summary>The page processed at full resolution (for taking over).</summary>
    public ScannedImage Processed(CancellationToken cancel = default) =>
        ScanProcessor.Apply(Straightened(Image, cancel), Adjustments, Back(null, cancel), cancel);

    /// <summary>The page for the preview: reduced, straightened (unless turned by hand) and processed; also the straightened copy before the processing.</summary>
    public (ScannedImage Before, ScannedImage After) PreviewImages(int edge, bool asScanned, CancellationToken cancel)
    {
        var small = ScanPdf.Reduce(Image, edge);
        var straight = asScanned ? small : Straightened(small, cancel);
        return (straight, ScanProcessor.Apply(straight, Adjustments, Back(edge, cancel), cancel));
    }

    /// <summary>Closes the preview document.</summary>
    public void Dispose() => Preview.Dispose();
}

/// <summary>
/// The window „Scannen“ (KONZEPT §5.25): choose a scanner and its settings on the left, scan, order the pages as tiles, look at one page
/// below them, take everything over into the program. Device, source, resolution, colour, format; profiles of these settings; both sides by
/// turning the stack; leaving out blank pages; turning and deleting pages.
/// </summary>
public sealed partial class ScanViewModel : ObservableObject
{
    // the devices last found per scanner interface, for this run of the program: a search of network scanners takes seconds, opening a
    // device found before is instant — the window shows them at once when it opens again
    private static readonly Dictionary<string, IReadOnlyList<ScanDevice>> Found = new(StringComparer.Ordinal);

    private readonly IScanBackend _backend;
    private readonly List<ScanPage> _pages = [];
    private readonly List<PdfDocument> _retired = [];
    private IScanSession? _session;
    private CancellationTokenSource? _scan;
    // how long the last sheets took: the progress of the next is estimated from it while the scanner sends nothing (network scanners send a
    // sheet only when it is read completely)
    private readonly List<TimeSpan> _sheetTimes = [];
    private bool _applyingCapabilities;
    private readonly ScanProfileStore _profiles;
    // the settings wanted: the chosen profile or the settings last used; applied when its device opens
    private ScanProfile _wish;
    private bool _loadingProfiles;
    // both sides by turning the stack: the fronts as read, while the backs are awaited; their stack; the backs to replace (scanning them anew)
    private List<ScanPage>? _fronts;
    private int _frontsStack;
    private List<ScanPage>? _replacing;
    // the stacks so far (the last given out) and those scanned on both sides (the hint above their group says so)
    private int _lastStack;
    private readonly HashSet<int> _duplexStacks = [];
    // the processing new pages get: the values last set
    private ScanAdjustments _next;
    private bool _loadingAdjustments;
    private CancellationTokenSource? _previewWork;
    private CancellationTokenSource? _tileWork;
    private readonly HashSet<ScanPage> _tilesToRefresh = [];

    /// <param name="backend">The scanner interface of the system.</param>
    /// <param name="canAppend">Whether an open document can take pages (then „an das offene Dokument anhängen“ is offered).</param>
    /// <param name="profiles">The scan profiles; null: those beside the settings file.</param>
    public ScanViewModel(IScanBackend backend, bool canAppend, ScanProfileStore? profiles = null)
    {
        _backend = backend;
        CanAppend = canAppend;
        _profiles = profiles ?? ScanProfileStore.Beside(UserSettings.File);
        _wish = UserSettings.Current.Scan ?? new ScanProfile();
        ReloadProfiles(UserSettings.Current.ScanProfileName);
        if (SelectedProfile is { } chosen) _wish = chosen;
        (ManualDuplex, SkipBlank, BlankLimit, BacksTurned, BacksInFeedOrder) = (_wish.ManualDuplex, _wish.SkipBlank, _wish.BlankLimit, _wish.BacksTurned, _wish.BacksInFeedOrder);
        _next = _wish.Adjustments ?? new ScanAdjustments();
        LoadAdjustments(_next);
        IsAvailable = backend.IsAvailable(out var hint);
        InstallHint = hint;
        InitNetwork();
        Renderer = new DocumentRenderer
        {
            EnhanceThinLines = UserSettings.Current.EnhanceThinLines,
            OverprintPreview = false,
        };
        Status = IsAvailable ? null : hint;
    }

    /// <summary>The renderer of the tiles.</summary>
    public DocumentRenderer Renderer { get; }

    /// <summary>Whether the scanner interface is installed.</summary>
    public bool IsAvailable { get; }

    /// <summary>How to install it, if it is missing.</summary>
    public string? InstallHint { get; }

    /// <summary>Whether the pages can be appended to the open document.</summary>
    public bool CanAppend { get; }

    [ObservableProperty]
    public partial IReadOnlyList<ScanDevice> Devices { get; set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(SaveProfileCommand))]
    public partial ScanDevice? SelectedDevice { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand), nameof(SearchNetworksCommand), nameof(ScanCommand), nameof(TakeOverNewCommand), nameof(TakeOverAppendCommand), nameof(SaveImagesCommand))]
    [NotifyPropertyChangedFor(nameof(ShowsProgress), nameof(ProgressUnknown), nameof(ProgressText))]
    public partial bool IsBusy { get; set; }

    /// <summary>Whether the device search runs (the device box then says so).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DevicePlaceholder))]
    public partial bool IsSearching { get; set; }

    /// <summary>What the empty device box says.</summary>
    public string DevicePlaceholder => IsSearching ? Tr.T("Suche Scanner …") : Tr.T("Kein Scanner");

    /// <summary>Whether the status bar shows that something happens (searching, connecting); the progress of a sheet is shown between tiles and preview.</summary>
    public bool ShowsProgress => IsBusy && !IsScanning;

    /// <summary>The page being read, for the status bar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial int ScanningPage { get; set; }

    /// <summary>„Blatt 2 wird gescannt … 45 %“ while scanning (shown between tiles and preview); empty otherwise.</summary>
    public string ProgressText => !IsScanning ? ""
        : Phase switch
        {
            ScanPhase.Waiting => Tr.T("Der Scanner ist noch beschäftigt — PDF-Knead wartet auf ihn …"),
            ScanPhase.Finishing => Tr.T("Der Scanner schließt den Auftrag ab …"),
            ScanPhase.Processing => Tr.T("Blatt {0} wird verarbeitet …", ScanningPage),
            ScanPhase.Transferring when Progress < 0 => Tr.T("Blatt {0} wird übertragen … {1:0.0} MB", ScanningPage, TransferredBytes / 1048576.0),
            _ when ScanningPage == 0 => "",
            _ when Progress < 0 => Tr.T("Blatt {0} wird gescannt … {1} s", ScanningPage, (int)_phaseClock.Elapsed.TotalSeconds),
            _ when ProgressEstimated => Tr.T("Blatt {0} wird gescannt … etwa {1:0} %", ScanningPage, Progress * 100),
            _ => Tr.T("Blatt {0} wird gescannt … {1:0} %", ScanningPage, Progress * 100),
        };

    /// <summary>What the job is doing (network scanners report the phases; the system's scanner interfaces only reading).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial ScanPhase Phase { get; set; }

    /// <summary>The bytes of the page received so far, while it is transferred.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial long TransferredBytes { get; set; }

    // since when the job is in its phase: the seconds shown while the scanner reads and says nothing
    private readonly System.Diagnostics.Stopwatch _phaseClock = new();

    partial void OnPhaseChanged(ScanPhase value) => _phaseClock.Restart();

    /// <summary>Whether <see cref="Progress"/> is estimated from the time the last sheets took (the scanner has sent nothing yet).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial bool ProgressEstimated { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualDuplexPossible))]
    public partial IReadOnlyList<ScanSource> Sources { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualDuplexPossible))]
    public partial ScanSource? SelectedSource { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<int> Resolutions { get; set; } = [];

    [ObservableProperty]
    public partial int? SelectedResolution { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ColorModeChoice> ColorModes { get; set; } = [];

    [ObservableProperty]
    public partial ColorModeChoice? SelectedColorMode { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ScanFormat> Formats { get; set; } = ScanFormats.All;

    [ObservableProperty]
    public partial ScanFormat? SelectedFormat { get; set; }

    /// <summary>The tiles, in the order of the pages.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageCountText), nameof(HasPages))]
    [NotifyCanExecuteChangedFor(nameof(TakeOverNewCommand), nameof(TakeOverAppendCommand), nameof(SaveImagesCommand))]
    public partial IReadOnlyList<PageViewModel> Pages { get; set; } = [];

    /// <summary>The page shown in the preview.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentText))]
    [NotifyCanExecuteChangedFor(nameof(RotateLeftCommand), nameof(RotateRightCommand), nameof(DeleteCommand), nameof(StartFreeRotateCommand),
        nameof(SplitAtSpineCommand))]
    [NotifyPropertyChangedFor(nameof(CurrentStraightened), nameof(StraightenToggleText))]
    public partial ScanPage? Current { get; set; }

    /// <summary>The preview of <see cref="Current"/>.</summary>
    [ObservableProperty]
    public partial Bitmap? Preview { get; set; }

    /// <summary>A message for the user (search, progress, errors).</summary>
    [ObservableProperty]
    public partial string? Status { get; set; }

    /// <summary>Whether <see cref="Status"/> reports an error.</summary>
    [ObservableProperty]
    public partial bool StatusIsError { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(CancelCommand), nameof(TakeOverNewCommand), nameof(TakeOverAppendCommand), nameof(SaveImagesCommand),
        nameof(SaveProfileCommand))]
    [NotifyPropertyChangedFor(nameof(ShowsProgress), nameof(ProgressUnknown), nameof(ProgressText))]
    public partial bool IsScanning { get; set; }

    /// <summary>Progress of the page being read, 0 … 1; negative while the length is unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressUnknown), nameof(ProgressText))]
    public partial double Progress { get; set; }

    /// <summary>Whether the bar only shows that something happens: searching, connecting, or a page whose length the driver does not tell.</summary>
    public bool ProgressUnknown => Progress < 0;

    // ---------------------------------------------------------------- profiles, both sides, blank pages

    /// <summary>The saved profiles, by name.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ScanProfile> Profiles { get; set; } = [];

    /// <summary>The chosen profile; null: the settings last used.</summary>
    [ObservableProperty]
    public partial ScanProfile? SelectedProfile { get; set; }

    /// <summary>Whether the box for the name of a profile to save is open.</summary>
    [ObservableProperty]
    public partial bool IsNamingProfile { get; set; }

    /// <summary>The name for the profile to save.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmProfileCommand))]
    public partial string NewProfileName { get; set; } = "";

    /// <summary>Whether a profile of that name exists and the next „Ersetzen“ replaces it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmProfileText))]
    public partial bool OverwritePending { get; set; }

    /// <summary>„Speichern“, or „Ersetzen“ when the name is taken.</summary>
    public string ConfirmProfileText => OverwritePending ? Tr.T("Ersetzen") : Tr.T("Speichern");

    /// <summary>Both sides by turning the stack (feeder without duplex): fronts first, then the backs.</summary>
    [ObservableProperty]
    public partial bool ManualDuplex { get; set; }

    /// <summary>
    /// Whether the source reads one side from the feeder, so that both sides can be had by turning the stack — not when the device reads
    /// both sides itself: then „Einzug beidseitig“ in „Quelle“ is the way, and the box would only be a second, worse one (design decision).
    /// </summary>
    public bool ManualDuplexPossible => SelectedSource?.Kind == ScanSourceKind.Feeder && !Sources.Any(s => s.Kind == ScanSourceKind.FeederDuplex);

    /// <summary>Whether the fronts are scanned and the backs are awaited.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanButtonText), nameof(BacksText))]
    public partial bool AwaitingBacks { get; set; }

    /// <summary>„Scannen“, or „Rückseiten“ while the backs are awaited (short: the button in the foot is narrow; the card above says it in full).</summary>
    public string ScanButtonText => AwaitingBacks ? Tr.T("Rückseiten") : Tr.T("Scannen");

    /// <summary>What to do for the backs, step by step (the same for every device: the stack as it lies in the tray, turned over); with the stack's number.</summary>
    public string BacksText => _fronts is not { } fronts ? ""
        : _replacing is not null
            ? Tr.T("Stapel {0}: Rückseiten neu scannen — den Stapel so in den Einzug legen wie zuletzt für die Rückseiten (Rückseiten nach oben, Oberkante voran). Dann „Rückseiten scannen“ drücken; die bisherigen Rückseiten bleiben, bis die neuen da sind.", StackNumber(_frontsStack))
            : Tr.T("Stapel {0}: {1} Vorderseiten gescannt. Jetzt die Rückseiten: den Stapel aus der Ablage nehmen, ohne die Reihenfolge zu ändern, wie eine Buchseite umdrehen und so in den Einzug legen wie vorher — Oberkante voran. Dann „Rückseiten scannen“ drücken.", StackNumber(_frontsStack), fronts.Count);

    /// <summary>The button that ends waiting for the backs: without backs, or (scanning them anew) keeping those scanned before.</summary>
    public string EndDuplexText => _replacing is null ? Tr.T("Ohne Rückseiten") : Tr.T("Bisherige behalten");

    /// <summary>
    /// „Lage-Korrektur“, remembered: whether the backs lie the other way round than the fronts (half a turn) — that comes from the paper path
    /// of the device and how the stack is turned; a stack laid upside down as a whole (both turned) does not change it.
    /// </summary>
    [ObservableProperty]
    public partial bool BacksTurned { get; set; }

    // the last stack by turning: whether its fronts and its backs are turned by half a turn now (pressed again, the buttons turn them back)
    private bool _frontsTurnedNow, _backsTurnedNow;

    /// <summary>„Lage-Korrektur“: the backs come in the order of the sheets (2, 4, 6) instead of the last first (remembered).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SwapBacksText))]
    public partial bool BacksInFeedOrder { get; set; }

    /// <summary>
    /// Whether the backs are being put between the fronts — from the end of the scan until their tiles are computed (turned, show-through):
    /// a hint between tiles and preview says so (Festlegung: otherwise one goes on without seeing that the pages still change).
    /// </summary>
    [ObservableProperty]
    public partial bool IsSorting { get; set; }

    // sorting waits for the tiles still to be computed
    private bool _sortingTiles;

    /// <summary>Whether the bar „Lage-Korrektur manueller Duplex-Scan“ is shown: after the backs were put between the fronts.</summary>
    [ObservableProperty]
    public partial bool CanCorrectDuplex { get; set; }

    /// <summary>The button that turns the backs; always the same words, pressed again it turns them back (Festlegung: a changing text confuses).</summary>
    public string TurnBacksText => Tr.T("Rückseiten um 180° drehen");

    /// <summary>The button that turns the fronts (the stack was laid upside down); always the same words.</summary>
    public string TurnFrontsText => Tr.T("Vorderseiten um 180° drehen");

    /// <summary>The button that puts the backs in the other order.</summary>
    public string SwapBacksText => Tr.T("Rückseiten andersherum zuordnen");

    /// <summary>
    /// The fronts and backs of the last stack by turning, as read (blank ones included), and its stack: the correction pairs them anew. Only
    /// this stack is corrected; with the next scan it is done with — its pages turn only by hand then.
    /// </summary>
    private List<ScanPage>? _duplexFronts, _duplexBacks;
    private int _duplexStack;

    /// <summary>Whether blank pages are left out.</summary>
    [ObservableProperty]
    public partial bool SkipBlank { get; set; }

    /// <summary>Up to which share of the page in percent it counts as blank.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlankLimitText))]
    public partial double BlankLimit { get; set; } = BlankPages.DefaultLimit;

    /// <summary>„Leer bis 0,15 % bedeckt“.</summary>
    public string BlankLimitText => Tr.T("Leer bis {0:0.##} % bedeckt", BlankLimit);

    // ---------------------------------------------------------------- processing („Nachbearbeitung“)

    /// <summary>„Automatisch verbessern“.</summary>
    [ObservableProperty]
    public partial bool AutoEnhance { get; set; }

    /// <summary>Helligkeit −100 … 100.</summary>
    [ObservableProperty]
    public partial double Brightness { get; set; }

    /// <summary>Luminanz −100 … 100.</summary>
    [ObservableProperty]
    public partial double Luminance { get; set; }

    /// <summary>Kontrast −100 … 100.</summary>
    [ObservableProperty]
    public partial double Contrast { get; set; }

    /// <summary>Schärfen 0 … 100.</summary>
    [ObservableProperty]
    public partial double Sharpen { get; set; }

    /// <summary>Durchscheinen verhindern 0 … 100.</summary>
    [ObservableProperty]
    public partial double ShowThrough { get; set; }

    /// <summary>Sättigung −100 … 100.</summary>
    [ObservableProperty]
    public partial double Saturation { get; set; }

    /// <summary>Dynamik −100 … 100.</summary>
    [ObservableProperty]
    public partial double Vibrance { get; set; }

    /// <summary>Graustufen: the colour is taken out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColourAdjustable))]
    public partial bool Grayscale { get; set; }

    /// <summary>Whether saturation and vibrance can change anything (not for greyscale or black and white).</summary>
    public bool ColourAdjustable => !Grayscale && !BlackWhite;

    /// <summary>Schwarzweiß.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColourAdjustable))]
    public partial bool BlackWhite { get; set; }

    /// <summary>Schwelle für Schwarzweiß 0 … 100.</summary>
    [ObservableProperty]
    public partial double Threshold { get; set; } = 50;

    /// <summary>The straightening methods („Begradigen“).</summary>
    public IReadOnlyList<IStraightener> StraightenMethods => Straighteners.All;

    /// <summary>The chosen straightening method.</summary>
    [ObservableProperty]
    public partial IStraightener SelectedStraightener { get; set; } = Straighteners.For(Straighteners.Skew);

    /// <summary>„Ränder abschneiden“.</summary>
    [ObservableProperty]
    public partial bool CropBorders { get; set; } = true;

    /// <summary>Whether the shown page is straightened (then its straightening can be discarded).</summary>
    public bool CurrentStraightened => Current is { } c && (c.IsStraightened || c.StraightenOff);

    /// <summary>„Begradigung verwerfen“ or „Begradigung wieder anwenden“ for the shown page.</summary>
    public string StraightenToggleText => Current?.StraightenOff == true ? Tr.T("Begradigung wieder anwenden") : Tr.T("Begradigung verwerfen");

    /// <summary>Whether the shown page is being turned by hand.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewRotation))]
    public partial bool IsFreeRotating { get; set; }

    /// <summary>The angle turned by hand, clockwise, −20 … 20 degrees.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FreeAngleText), nameof(PreviewRotation))]
    public partial double FreeAngle { get; set; }

    /// <summary>The angle the preview turns the page by (only while turning by hand).</summary>
    public double PreviewRotation => IsFreeRotating ? FreeAngle : 0;

    /// <summary>Whether the preview compares before and after with a line.</summary>
    [ObservableProperty]
    public partial bool Compare { get; set; }

    /// <summary>The shown page before the processing (straightened only), for the comparison.</summary>
    [ObservableProperty]
    public partial Bitmap? PreviewBefore { get; set; }

    partial void OnCompareChanged(bool value) => _ = UpdatePreviewAsync();

    /// <summary>„1,25°“.</summary>
    public string FreeAngleText => Tr.T("{0:0.00}°", FreeAngle);

    /// <summary>What the processing applies to: the shown page, the selected pages, or (without pages) the next scans.</summary>
    public string AdjustmentsTarget
    {
        get
        {
            var selected = Pages.Count(p => p.IsSelected);
            return Current is null ? Tr.T("für neue Scans")
                : selected > 1 ? Tr.T("{0} Seiten gewählt", selected)
                : Tr.T("Seite {0}", _pages.IndexOf(Current) + 1);
        }
    }

    partial void OnAutoEnhanceChanged(bool value) => Change(a => a with { AutoEnhance = value });

    partial void OnBrightnessChanged(double value) => Change(a => a with { Brightness = value });

    partial void OnLuminanceChanged(double value) => Change(a => a with { Luminance = value });

    partial void OnContrastChanged(double value) => Change(a => a with { Contrast = value });

    partial void OnSharpenChanged(double value) => Change(a => a with { Sharpen = value });

    partial void OnShowThroughChanged(double value) => Change(a => a with { ShowThrough = value });

    partial void OnBlackWhiteChanged(bool value) => Change(a => a with { BlackWhite = value });

    partial void OnThresholdChanged(double value) => Change(a => a with { Threshold = value });

    partial void OnSaturationChanged(double value) => Change(a => a with { Saturation = value });

    partial void OnVibranceChanged(double value) => Change(a => a with { Vibrance = value });

    partial void OnGrayscaleChanged(bool value) => Change(a => a with { Grayscale = value });

    partial void OnSelectedStraightenerChanged(IStraightener value) => Change(a => a with { Straighten = value.Key });

    partial void OnCropBordersChanged(bool value) => Change(a => a with { CropBorders = value });

    partial void OnFreeAngleChanged(double value)
    {
        var clamped = Math.Clamp(Math.Round(value, 2), -SkewDetector.MaxDegrees, SkewDetector.MaxDegrees);
        if (clamped != value) FreeAngle = clamped;
    }

    /// <summary>Discards the straightening of the shown page, or applies it again.</summary>
    [RelayCommand]
    private void ToggleStraighten()
    {
        if (Current is { } page) ToggleStraighten(page);
    }

    /// <summary>Discards the straightening of a page or applies it again (the symbol at its tile, the button at the preview).</summary>
    public void ToggleStraighten(ScanPage page)
    {
        page.StraightenOff = !page.StraightenOff;
        _tilesToRefresh.Add(page);
        _ = RefreshAsync();
        OnPropertyChanged(nameof(StraightenToggleText));
    }

    /// <summary>The symbol at a tile was clicked.</summary>
    public void BadgeClicked(int index)
    {
        if ((uint)index < (uint)_pages.Count) ToggleStraighten(_pages[index]);
    }

    /// <summary>Turns the shown page by hand: the preview shows it as scanned, turned by the angle, under a grid.</summary>
    [RelayCommand(CanExecute = nameof(HasCurrent))]
    private async Task StartFreeRotate()
    {
        if (Current is not { } page) return;
        var auto = page.StraightenOff ? 0 : await Task.Run(() => page.Geometry().Plan?.Degrees ?? 0);
        FreeAngle = page.ManualAngle ?? auto;
        IsFreeRotating = true;
        _ = UpdatePreviewAsync();
    }

    /// <summary>Keeps the angle turned by hand for the shown page.</summary>
    [RelayCommand]
    private void ConfirmFreeRotate()
    {
        if (Current is { } page)
        {
            page.ManualAngle = FreeAngle;
            page.StraightenOff = false;
            _tilesToRefresh.Add(page);
        }
        IsFreeRotating = false;
        _ = RefreshAsync();
        _ = UpdatePreviewAsync();
    }

    /// <summary>Leaves turning by hand without changing anything.</summary>
    [RelayCommand]
    private void CancelFreeRotate()
    {
        IsFreeRotating = false;
        _ = UpdatePreviewAsync();
    }

    /// <summary>Gives the shown page back to the method (the angle turned by hand is forgotten).</summary>
    [RelayCommand]
    private void AutoAngle()
    {
        if (Current is { } page)
        {
            page.ManualAngle = null;
            _tilesToRefresh.Add(page);
        }
        IsFreeRotating = false;
        _ = RefreshAsync();
        _ = UpdatePreviewAsync();
    }

    /// <summary>The values the sliders show.</summary>
    private ScanAdjustments Shown() =>
        new(AutoEnhance, Brightness, Luminance, Contrast, Sharpen, ShowThrough, BlackWhite, Threshold, Saturation, Vibrance, Grayscale,
            SelectedStraightener.Key, CropBorders);

    /// <summary>Shows values in the sliders without applying them.</summary>
    private void LoadAdjustments(ScanAdjustments a)
    {
        _loadingAdjustments = true;
        try
        {
            (AutoEnhance, Brightness, Luminance, Contrast, Sharpen, ShowThrough, BlackWhite, Threshold) =
                (a.AutoEnhance, a.Brightness, a.Luminance, a.Contrast, a.Sharpen, a.ShowThrough, a.BlackWhite, a.Threshold);
            (Saturation, Vibrance, Grayscale) = (a.Saturation, a.Vibrance, a.Grayscale);
            (SelectedStraightener, CropBorders) = (Straighteners.For(a.Straighten), a.CropBorders);
        }
        finally
        {
            _loadingAdjustments = false;
        }
    }

    /// <summary>
    /// A slider moved: only that value changes on the selected pages (or the shown one) — their other values stay; new pages get the values
    /// shown. Tiles and preview are computed anew in the background, shortly after the slider rests.
    /// </summary>
    private void Change(Func<ScanAdjustments, ScanAdjustments> change)
    {
        if (_loadingAdjustments) return;
        _next = Shown();
        foreach (var page in SelectedOrCurrent())
        {
            page.Adjustments = change(page.Adjustments);
            _tilesToRefresh.Add(page);
        }
        _ = RefreshAsync();
    }

    /// <summary>Takes the processing of the selected pages back to the raw scan.</summary>
    [RelayCommand]
    private void ResetAdjustments()
    {
        LoadAdjustments(ScanAdjustments.None);
        _next = ScanAdjustments.None;
        foreach (var page in SelectedOrCurrent())
        {
            page.Adjustments = ScanAdjustments.None;
            _tilesToRefresh.Add(page);
        }
        _ = RefreshAsync();
    }

    /// <summary>Gives all pages the processing shown.</summary>
    [RelayCommand]
    private void ApplyAdjustmentsToAll()
    {
        var shown = Shown();
        _next = shown;
        foreach (var page in _pages)
        {
            page.Adjustments = shown;
            _tilesToRefresh.Add(page);
        }
        _ = RefreshAsync();
        Report(Tr.T("Nachbearbeitung für alle {0} Seiten übernommen.", _pages.Count));
    }

    /// <summary>Tests only: awaited after the tiles were computed and before they are shown (to change a page in between).</summary>
    internal static Func<Task>? TilesBuiltForTests;

    /// <summary>
    /// Computes the changed tiles and the preview anew, 120 ms after the last change (a slider being dragged does not start a computation for
    /// every step); a newer change stops an older computation.
    /// </summary>
    private async Task RefreshAsync()
    {
        _tileWork?.Cancel();
        var work = _tileWork = new CancellationTokenSource();
        try
        {
            await Task.Delay(120, work.Token);
            _ = UpdatePreviewAsync();
            var pages = _tilesToRefresh.Where(_pages.Contains).ToList();
            var built = new List<(ScanPage Page, PdfDocument Preview, (int, ScanPage?, int) State)>();
            foreach (var page in pages)
            {
                var state = page.TileState;
                var preview = await Task.Run(() => page.BuildPreview(work.Token), work.Token);
                if (work.IsCancellationRequested)
                {
                    _retired.Add(preview);
                    return;
                }
                built.Add((page, preview, state));
            }
            if (TilesBuiltForTests is { } pause) await pause();
            // the selection by page, before the tiles get new ids
            var selected = Pages.Where(p => p.IsSelected).Select(PageOf).OfType<ScanPage>().ToList();
            var shown = 0;
            foreach (var (page, preview, state) in built)
            {
                // the page (or its other side) changed while its tile was computed — turned, say: this tile is old; the page stays to be
                // computed anew (Fehlergedächtnis 860: an old tile replaced the one just turned)
                if (page.TileState != state)
                {
                    _retired.Add(preview);
                    continue;
                }
                _tilesToRefresh.Remove(page);
                if (page.Use(preview, state) is { } old) _retired.Add(old);
                shown++;
            }
            if (shown > 0) Rebuild(Current, selected, keepPreview: true);
            if (shown < built.Count) _ = RefreshAsync();
            else if (_sortingTiles && !_tilesToRefresh.Any(_pages.Contains)) (_sortingTiles, IsSorting) = (false, false);
        }
        catch (OperationCanceledException)
        {
            // a newer change computes them
        }
    }

    /// <summary>Computes the preview of the shown page with its processing in the background (the old one stays until then).</summary>
    private async Task UpdatePreviewAsync()
    {
        _previewWork?.Cancel();
        var work = _previewWork = new CancellationTokenSource();
        if (Current is not { } page)
        {
            Preview = null;
            return;
        }
        var (image, adjustments, free, compare) = (page.Image, page.Adjustments, IsFreeRotating, Compare);
        try
        {
            // turning by hand: the page as scanned, the window turns it by the angle over a fixed grid
            var (before, processed) = await Task.Run(() => page.PreviewImages(PreviewEdge, free, work.Token), work.Token);
            if (!work.IsCancellationRequested && ReferenceEquals(Current, page))
            {
                Preview = ToBitmap(processed);
                PreviewBefore = compare && !free ? ToBitmap(before) : null;
            }
        }
        catch (OperationCanceledException)
        {
            // another page or newer values
        }
    }

    /// <summary>The longest edge of the preview: enough for details when zoomed in (an A4 page at 300 dpi stays as scanned).</summary>
    private const int PreviewEdge = 4000;

    /// <summary>„Texterkennung nach der Übernahme starten“.</summary>
    [ObservableProperty]
    public partial bool RecognizeAfter { get; set; }

    /// <summary>Whether there are pages.</summary>
    public bool HasPages => _pages.Count > 0;

    /// <summary>„Gescannte Seiten (n)“.</summary>
    public string PageCountText => Tr.T("Gescannte Seiten ({0})", _pages.Count);

    /// <summary>„Seite n von m“ for the preview.</summary>
    public string CurrentText => Current is null ? "" : Tr.T("Seite {0} von {1}", _pages.IndexOf(Current) + 1, _pages.Count);

    /// <summary>The pages (for tests).</summary>
    internal IReadOnlyList<ScanPage> ScanPages => _pages;

    /// <summary>Raised when the window should close with a result.</summary>
    public event Action<ScanResult>? TakeOverRequested;

    // ---------------------------------------------------------------- devices

    /// <summary>
    /// When the window opens: the devices found earlier in this run at once (the remembered one is opened without waiting), otherwise a search.
    /// </summary>
    public async Task StartAsync()
    {
        if (!IsAvailable) return;
        if (!Found.TryGetValue(_backend.Name, out var known) || known.Count == 0)
        {
            await RefreshDevicesAsync();
            return;
        }
        known = Visible(known);
        if (known.Count == 0)
        {
            await RefreshDevicesAsync();
            return;
        }
        Devices = known;
        Select(_wish.FindDevice(known) ?? known[0]);
    }

    /// <summary>
    /// „Scanner suchen“: looks for scanners again; a scanner that could not be opened is tried again (only on this request or when the user
    /// chooses it — never by itself, Fehlergedächtnis 927).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    public async Task SearchAsync()
    {
        _unreachable.Clear();
        await RefreshDevicesAsync();
        // the same scanner still chosen and not open: the list did not change it, so it is tried here (a new choice opens by itself)
        if (SelectedDevice is { } device && _session is null && !IsOpening) await OpenDeviceAsync(device);
    }

    /// <summary>
    /// Scanners that could not be opened in this window (Fehlergedächtnis 927): not opened again by themselves — before, a failed open
    /// searched the list again, the search chose the same scanner, and it was opened again and again.
    /// </summary>
    private readonly HashSet<string> _unreachable = new(StringComparer.Ordinal);

    /// <summary>Whether the window chooses the scanner itself (a search, a removed entry) rather than the user.</summary>
    private bool _choosingInCode;

    /// <summary>Chooses a scanner from the code (not the user's choice: a scanner that could not be opened is not tried again by this).</summary>
    private void Select(ScanDevice? device)
    {
        _choosingInCode = true;
        try
        {
            SelectedDevice = device;
        }
        finally
        {
            _choosingInCode = false;
        }
    }

    /// <summary>A scanner is being opened (the search stays possible meanwhile, and stops the opening).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    public partial bool IsOpening { get; set; }

    private CancellationTokenSource? _openCancel;

    /// <summary>Looks for scanners and selects the remembered one (or the first) — also from the window itself (opening, settings).</summary>
    private async Task RefreshDevicesAsync()
    {
        if (!IsAvailable || IsBusy) return;
        _openCancel?.Cancel();
        IsBusy = true;
        IsSearching = true;
        Report(Tr.T("Suche Scanner … Netzwerkscanner brauchen dafür einige Sekunden."));
        try
        {
            var all = await _backend.FindDevicesAsync();
            Found[_backend.Name] = all;
            var devices = Visible(all);
            Devices = devices;
            var current = SelectedDevice is { } c && devices.Contains(c) ? c : null;
            Select(_wish.FindDevice(devices) ?? current ?? devices.FirstOrDefault());
            Report(devices.Count == 0 ? Tr.T("Kein Scanner gefunden. Ist er eingeschaltet und angeschlossen bzw. im selben Netzwerk?") : null,
                error: devices.Count == 0);
        }
        catch (ScanException e)
        {
            Report(e.Message, error: true);
        }
        catch (Exception e) when (Failure.Handled(e))
        {
            Unexpected(e);
        }
        finally
        {
            IsSearching = false;
            IsBusy = false;
        }
    }

    private bool CanSearch() => IsAvailable && !IsBusy && !IsScanning;

    partial void OnSelectedDeviceChanged(ScanDevice? value)
    {
        // the user chose it: try it again even if it could not be opened before
        if (!_choosingInCode && value is not null) _unreachable.Remove(value.Id);
        _ = OpenDeviceAsync(value);
    }

    private async Task OpenDeviceAsync(ScanDevice? device, bool retried = false)
    {
        _openCancel?.Cancel();
        StopFeederWatch();
        if (_session is { } old)
        {
            _session = null;
            await old.DisposeAsync();
        }
        if (device is null) return;
        if (_unreachable.Contains(device.Id))
        {
            Report(Tr.T("„{0}“ ist nicht erreichbar. „Scanner suchen“ oder die Auswahl des Scanners versucht es erneut.", device.DisplayName), error: true);
            return;
        }
        using var cancel = _openCancel = new CancellationTokenSource();
        IsOpening = true;
        Report(Tr.T("Verbinde mit {0} …", device.DisplayName));
        var reopen = false;
        try
        {
            var session = await _backend.OpenAsync(device, cancel.Token);
            if (SelectedDevice != device || cancel.IsCancellationRequested)
            {
                await session.DisposeAsync();
                return;
            }
            _session = session;
            WatchFeeder(session);
            // the wished settings when they are for this device (or for none); otherwise what is chosen now
            var wish = _wish.DeviceId is null || _wish.FindDevice([device]) is not null ? _wish
                : CurrentSettings(device) ?? new ScanProfile();
            var adjusted = await ApplyAsync(session, wish);
            Report(adjusted is null ? Tr.T("Bereit: {0}", device.DisplayName) : Tr.T("Bereit: {0} — {1}", device.DisplayName, adjusted));
        }
        catch (EsclCertificateException e) when (!retried)
        {
            reopen = await HandleCertificateAsync(device, e);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // another scanner was chosen, or a search started: this opening is no longer wanted
        }
        catch (ScanException e) when (!IsSearching && Found.Remove(_backend.Name))
        {
            // a device from the earlier list is gone (switched off, other address): search again — once; the search does not open it again
            _unreachable.Add(device.Id);
            Report(e.Message, error: true);
            IsOpening = false;
            await RefreshDevicesAsync();
            if (SelectedDevice is { } chosen && _unreachable.Contains(chosen.Id)) Report(e.Message, error: true);
        }
        catch (ScanException e)
        {
            _unreachable.Add(device.Id);
            Report(e.Message, error: true);
        }
        catch (Exception e) when (Failure.Handled(e))
        {
            Unexpected(e);
        }
        finally
        {
            if (ReferenceEquals(_openCancel, cancel)) _openCancel = null;
            IsOpening = false;
            ScanCommand.NotifyCanExecuteChanged();
        }
        if (reopen && SelectedDevice == device) await OpenDeviceAsync(device, retried: true);
    }

    /// <summary>
    /// Sets the device's choices to the wished settings: the source by name, else the source of the same kind; resolution, colour and format
    /// as close as the source allows.
    /// </summary>
    /// <returns>What could not be had as wished, for the user; null if everything could.</returns>
    private async Task<string?> ApplyAsync(IScanSession session, ScanProfile wish)
    {
        var caps = session.Capabilities;
        if (wish.Source is { } name && caps.Source?.Name != name)
        {
            var kind = ScanSource.FromName(name).Kind;
            if ((caps.Sources.FirstOrDefault(x => x.Name == name) ?? caps.Sources.FirstOrDefault(x => x.Kind == kind && kind != ScanSourceKind.Other)) is { } source
                && source.Name != caps.Source?.Name)
                caps = await session.SelectSourceAsync(source.Name);
        }
        ApplyCapabilities(caps, wish.Dpi, wish.Mode, wish.Format);
        return SelectedResolution is { } dpi && dpi != wish.Dpi && caps.Resolutions.Count > 0
            ? Tr.T("{0} dpi bietet diese Quelle nicht an, {1} dpi gewählt.", wish.Dpi, dpi)
            : null;
    }

    /// <summary>The settings as chosen now; null without a device.</summary>
    private ScanProfile? CurrentSettings(ScanDevice? device = null) => (device ?? SelectedDevice) is { } d
        ? new ScanProfile("", d.Id, d.BaseName, SelectedSource?.Name, SelectedResolution ?? 300, SelectedColorMode?.Mode ?? ScanColorMode.Color,
            SelectedFormat?.Key ?? "A4", ManualDuplex, SkipBlank, BlankLimit, _next, BacksTurned, BacksInFeedOrder)
        : null;

    /// <summary>Fills the choices from what the device offers, keeping the wished values where possible.</summary>
    private void ApplyCapabilities(ScanCapabilities caps, int dpi, ScanColorMode mode, string format)
    {
        _applyingCapabilities = true;
        try
        {
            Sources = caps.Sources;
            SelectedSource = caps.Source is { } src ? caps.Sources.FirstOrDefault(s => s.Name == src.Name) ?? src : null;
            Resolutions = caps.Resolutions;
            SelectedResolution = caps.Resolutions.Count == 0 ? null : caps.ClosestResolution(dpi);
            ColorModes = [.. (caps.ColorModes.Count == 0 ? [ScanColorMode.Color] : caps.ColorModes).Select(ColorModeChoice.For)];
            SelectedColorMode = ColorModes.FirstOrDefault(c => c.Mode == mode) ?? ColorModes[0];
            Formats = ScanFormats.Fitting(caps.MaxWidthMm, caps.MaxHeightMm);
            SelectedFormat = Formats.FirstOrDefault(f => f.Key == format) ?? Formats.FirstOrDefault(f => f.Key == "A4") ?? Formats[0];
        }
        finally
        {
            _applyingCapabilities = false;
        }
    }

    partial void OnSelectedSourceChanged(ScanSource? value)
    {
        if (_applyingCapabilities || value is null || _session is null) return;
        _ = SelectSourceAsync(value);
    }

    private async Task SelectSourceAsync(ScanSource source)
    {
        if (_session is not { } session) return;
        IsBusy = true;
        try
        {
            var caps = await session.SelectSourceAsync(source.Name);
            // the feeder often offers less resolution than the glass: the closest allowed value is chosen
            ApplyCapabilities(caps, SelectedResolution ?? 300, SelectedColorMode?.Mode ?? ScanColorMode.Color, SelectedFormat?.Key ?? "A4");
        }
        catch (ScanException e)
        {
            Report(e.Message, error: true);
        }
        catch (Exception e) when (Failure.Handled(e))
        {
            Unexpected(e);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------------------------------------------------------------- scanning

    /// <summary>
    /// Scans with the current settings; new pages come after the selected page (otherwise at the end). With „beidseitig von Hand“ the first
    /// run reads the fronts and the second the backs, which are then put between the fronts. Blank pages are left out if wished — with both
    /// sides only after fronts and backs are paired, so that leaving out a blank back does not shift the pairs.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    public async Task ScanAsync()
    {
        if (_session is not { } session) return;
        var settings = new ScanSettings(SelectedSource?.Name, SelectedResolution ?? 300, SelectedColorMode?.Mode ?? ScanColorMode.Color,
            SelectedFormat?.WidthMm, SelectedFormat?.HeightMm);
        if (CurrentSettings(session.Device) is { } current)
        {
            _wish = current with { Name = SelectedProfile?.Name ?? "" };
            UserSettings.Update(s => s with { Scan = current });
        }
        var duplex = ManualDuplex && ManualDuplexPossible;
        // a feeder reading both sides delivers front and back of each sheet one after the other
        var sheetPairs = SelectedSource?.Kind == ScanSourceKind.FeederDuplex;
        var backs = duplex && _fronts is not null;
        // a new scan: the last stack by turning is done with — the correction no longer reaches it, its pages turn only by hand from now on
        CanCorrectDuplex = false;
        if (!backs) (_duplexFronts, _duplexBacks) = (null, null);
        // where the pages go, and their stack: the backs to their fronts; a run of the feeder is a stack of its own, after the stack of the
        // selected page (never into another stack); a page from the flatbed joins the stack of the selected page, after it
        var (at, stack) = backs ? (BacksIndex(), _frontsStack)
            : SelectedSource?.Kind is ScanSourceKind.Feeder or ScanSourceKind.FeederDuplex ? (AfterSelectedStack(), ++_lastStack)
            : IntoSelectedStack();
        if (sheetPairs) _duplexStacks.Add(stack);
        var skip = SkipBlank;
        var limit = BlankLimit;
        var skipped = 0;
        var scanned = new List<ScanPage>();
        var failed = false;
        _scan = new CancellationTokenSource();
        IsScanning = true;
        ScanningPage = 1;
        Phase = ScanPhase.Reading;
        _phaseClock.Restart();
        Progress = -1;
        var count = 0;
        var sheet = System.Diagnostics.Stopwatch.StartNew();
        var real = -1.0;   // what the driver reports for the current sheet
        if (_sheetTimes.Count > 0 && _lastSettings != settings) _sheetTimes.Clear();   // another resolution or source takes another time
        _lastSettings = settings;
        Report(Tr.T("Scanne …"));
        using var estimating = new CancellationTokenSource();
        var estimate = EstimateAsync(estimating.Token);
        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                if (p.Page != ScanningPage) _phaseClock.Restart();
                ScanningPage = p.Page;
                Phase = p.Phase;
                TransferredBytes = p.Bytes;
                real = p.Fraction;
                if (real > 0 || _sheetTimes.Count == 0)
                {
                    ProgressEstimated = false;
                    Progress = real;
                }
                Report(count == 0 ? Tr.T("Scanne Seite {0} …", p.Page) : Tr.T("Scanne Seite {0} … ({1} fertig)", p.Page, count));
            });
            await foreach (var image in session.ScanAsync(settings, progress, _scan.Token))
            {
                _sheetTimes.Add(sheet.Elapsed);
                count++;
                // without both sides a blank page is left out at once; with both sides only after pairing
                if (skip && !duplex && !sheetPairs && await Task.Run(() => BlankPages.IsBlank(image, limit))) skipped++;
                else
                {
                    var adjustments = _next;
                    var page = await Task.Run(() => new ScanPage(image, adjustments) { Stack = stack });
                    _pages.Insert(at + scanned.Count, page);
                    scanned.Add(page);
                    Rebuild(page);
                }
                // the next sheet: unknown until the scanner sends, estimated from the sheets before
                sheet.Restart();
                real = -1;
                ScanningPage = count + 1;
                Phase = ScanPhase.Reading;
                _phaseClock.Restart();
                Progress = -1;
                ProgressEstimated = false;
            }
            Report(count switch
            {
                0 => Tr.T("Keine Seite gescannt."),
                1 => Tr.T("1 Seite gescannt."),
                _ => Tr.T("{0} Seiten gescannt.", count),
            });
        }
        catch (OperationCanceledException)
        {
            failed = true;
            Report(count == 0 ? Tr.T("Abgebrochen.") : Tr.T("Abgebrochen — {0} Seiten behalten.", count));
        }
        catch (ScanException e)
        {
            failed = true;
            Report(count == 0 ? e.Message : Tr.T("{0} — {1} Seiten behalten.", e.Message, count), error: true);
            // the helper with the driver has crashed or hung: connect again (a new helper), so the next scan works without asking
            if (e.Kind == ScanErrorKind.DriverCrashed) _reconnect = true;
        }
        catch (Exception e) when (Failure.Handled(e))
        {
            failed = true;
            Unexpected(e);
        }
        finally
        {
            estimating.Cancel();
            await estimate;
            if (_reconnect)
            {
                _reconnect = false;
                _session = null;   // gone with the helper
                _ = OpenDeviceAsync(SelectedDevice);
            }
            IsScanning = false;
            ScanningPage = 0;
            ProgressEstimated = false;
            Progress = 0;
            _scan.Dispose();
            _scan = null;
        }
        if (sheetPairs && scanned.Count > 1)
        {
            for (var i = 0; i + 1 < scanned.Count; i += 2) Link(scanned[i], scanned[i + 1]);
            if (skip)
            {
                var blank = await Task.Run(() => scanned.Where(p => BlankPages.IsBlank(p.Image, limit)).ToHashSet());
                foreach (var page in blank)
                {
                    Unlink(page);
                    _pages.Remove(page);
                    _retired.Add(page.Preview);
                }
                skipped += blank.Count;
                Rebuild(_pages.LastOrDefault());
            }
            _ = RefreshAsync();
        }
        if (backs && scanned.Count > 0) skipped += await PairAsync(scanned, skip, limit);
        else if (duplex && !backs && scanned.Count > 0 && !failed)
        {
            // the fronts are in: wait for the backs
            (_fronts, _frontsStack, _replacing) = (scanned, stack, null);
            (_frontsTurnedNow, _backsTurnedNow) = (false, false);
            AwaitingBacks = true;
            OnPropertyChanged(nameof(EndDuplexText));
            OnPropertyChanged(nameof(BacksText));
            Report(BacksText);
        }
        if (skipped > 0)
            Report($"{Status} " + (skipped == 1 ? Tr.T("1 leere Seite weggelassen.") : Tr.T("{0} leere Seiten weggelassen.", skipped)), StatusIsError);
    }

    /// <summary>
    /// Puts the backs between the fronts (in the place of the first front still there) and leaves out blank pages if wished; ends waiting
    /// for the backs.
    /// </summary>
    /// <returns>The number of blank pages left out.</returns>
    private async Task<int> PairAsync(List<ScanPage> backs, bool skip, double limit)
    {
        // the fronts as read: one deleted since stays away, but keeps its place, so that no back moves to another sheet
        var fronts = _fronts ?? [];
        var (stack, replacing) = (_frontsStack, _replacing);
        IsSorting = true;
        EndWaiting();
        // scanning the backs anew: those scanned before go now that the new ones are there
        foreach (var old in (replacing ?? []).Where(_pages.Contains).ToList())
        {
            Unlink(old);
            _pages.Remove(old);
            _retired.Add(old.Preview);
        }
        var scanned = backs.ToHashSet();
        bool There(ScanPage p) => scanned.Contains(p) || _pages.Contains(p);
        var start = fronts.Concat(backs).Select(p => _pages.IndexOf(p)).Where(i => i >= 0).DefaultIfEmpty(_pages.Count).Min();
        // the order the backs came in, as remembered (the last first, or in the order of the sheets)
        var lastFirst = !BacksInFeedOrder;
        var paired = ManualDuplexOrder.Interleave(fronts, backs, lastFirst).Where(There).ToList();
        // the sides of a sheet know each other
        for (var i = 0; i < fronts.Count; i++)
            if (ManualDuplexOrder.BackOf(i, fronts.Count, backs.Count, lastFirst) is var j and >= 0 && There(fronts[i])) Link(fronts[i], backs[j]);
        foreach (var page in paired) _pages.Remove(page);
        var blank = skip ? await Task.Run(() => paired.Where(p => BlankPages.IsBlank(p.Image, limit)).ToHashSet()) : [];
        foreach (var page in blank)
        {
            Unlink(page);
            _retired.Add(page.Preview);
        }
        _pages.InsertRange(Math.Min(start, _pages.Count), paired.Where(p => !blank.Contains(p)));
        (_duplexFronts, _duplexBacks, _duplexStack) = (fronts, backs, stack);
        _duplexStacks.Add(stack);
        // the backs as remembered against the fronts (and with the fronts, if those were turned): turned before they are shown
        _backsTurnedNow = BacksTurned ^ _frontsTurnedNow;
        if (_backsTurnedNow) await TurnPagesAsync([.. backs.Where(_pages.Contains)], 2);
        CanCorrectDuplex = fronts.Count > 0 && backs.Count > 0;
        // the last page of the sheets is shown and selected, as after scanning: a new stack comes after this one
        Rebuild(paired.LastOrDefault(p => !blank.Contains(p)) ?? (_pages.Count > 0 ? _pages[Math.Min(start, _pages.Count - 1)] : null));
        // the sides of each sheet know each other now: their tiles take out the show-through; the hint goes when they are computed
        _sortingTiles = true;
        _ = RefreshAsync();
        var number = StackNumber(stack);
        Report(fronts.Count == backs.Count
            ? Tr.T("Stapel {0}: {1} Blätter beidseitig gescannt.", number, fronts.Count)
            : Tr.T("Stapel {0}: {1} Vorderseiten, aber {2} Rückseiten — bitte die Reihenfolge prüfen oder „Rückseiten neu scannen“.", number, fronts.Count, backs.Count),
            error: fronts.Count != backs.Count);
        return blank.Count;
    }

    /// <summary>The number of a stack as the user sees it: its place among the stacks in the tiles (by its first page).</summary>
    private int StackNumber(int stack)
    {
        var order = _pages.Select(p => p.Stack).Distinct().ToList();
        var place = order.IndexOf(stack);
        return place >= 0 ? place + 1 : order.Count + 1;
    }

    /// <summary>
    /// The hints above the groups of the tiles, one per stack at its first page — only while there is more than one stack and each lies
    /// together; once pages of different stacks are mixed, the tiles show no groups (Festlegung).
    /// </summary>
    private string?[] GroupHeaders()
    {
        var headers = new string?[_pages.Count];
        var runs = new List<(int Stack, int Start, int Count)>();
        for (var i = 0; i < _pages.Count; i++)
            if (runs.Count > 0 && runs[^1].Stack == _pages[i].Stack) runs[^1] = runs[^1] with { Count = runs[^1].Count + 1 };
            else runs.Add((_pages[i].Stack, i, 1));
        if (runs.Count < 2 || runs.Select(r => r.Stack).Distinct().Count() != runs.Count) return headers;
        for (var k = 0; k < runs.Count; k++)
        {
            var (stack, start, count) = runs[k];
            headers[start] = (_duplexStacks.Contains(stack), count == 1) switch
            {
                (true, true) => Tr.T("Stapel {0} · beidseitig · 1 Seite", k + 1),
                (true, false) => Tr.T("Stapel {0} · beidseitig · {1} Seiten", k + 1, count),
                (false, true) => Tr.T("Stapel {0} · 1 Seite", k + 1),
                _ => Tr.T("Stapel {0} · {1} Seiten", k + 1, count),
            };
        }
        return headers;
    }

    /// <summary>The selected page furthest back (-1: none).</summary>
    private int SelectedIndex() => Pages.Where(p => p.IsSelected).Select(p => p.Index).DefaultIfEmpty(-1).Max();

    /// <summary>Where a new stack goes: after the stack of the selected page (its pages lying together around it); nothing selected: at the end.</summary>
    private int AfterSelectedStack()
    {
        var index = SelectedIndex();
        if (index < 0 || index >= _pages.Count) return _pages.Count;
        var stack = _pages[index].Stack;
        while (index + 1 < _pages.Count && _pages[index + 1].Stack == stack) index++;
        return index + 1;
    }

    /// <summary>
    /// Where a page from the flatbed goes, and its stack: after the selected page (after the other side when it is the front of a sheet), into
    /// its stack; nothing selected: at the end, into the last stack; no pages yet: a stack of its own.
    /// </summary>
    private (int At, int Stack) IntoSelectedStack()
    {
        if (_pages.Count == 0) return (0, ++_lastStack);
        var index = SelectedIndex();
        if (index < 0 || index >= _pages.Count) return (_pages.Count, _pages[^1].Stack);
        var page = _pages[index];
        if (index + 1 < _pages.Count && ReferenceEquals(page.Partner, _pages[index + 1]) && ReferenceEquals(_pages[index + 1].Partner, page)) index++;
        return (index + 1, page.Stack);
    }

    /// <summary>Makes two pages the sides of one sheet (and the tiles of both are computed anew with the other side).</summary>
    private void Link(ScanPage front, ScanPage back)
    {
        (front.Partner, back.Partner) = (back, front);
        _tilesToRefresh.Add(front);
        _tilesToRefresh.Add(back);
    }

    /// <summary>A page leaves its sheet (deleted, left out); the other side's tile is computed anew without it.</summary>
    private void Unlink(ScanPage page)
    {
        if (page.Partner is { } other && ReferenceEquals(other.Partner, page))
        {
            other.Partner = null;
            _tilesToRefresh.Add(other);
        }
        page.Partner = null;
    }

    // ---------------------------------------------------------------- „Lage-Korrektur manueller Duplex-Scan“

    /// <summary>The pages selected in the tiles (kept by page: turned pages get new tile ids).</summary>
    private List<ScanPage> SelectedPages() => [.. Pages.Where(p => p.IsSelected).Select(PageOf).OfType<ScanPage>()];

    /// <summary>
    /// Turns the backs of the last stack by turning by half a turn (again: back). Remembered in the settings and the profile is how the backs
    /// lie against the fronts.
    /// </summary>
    [RelayCommand]
    private async Task TurnBacksAsync()
    {
        if (!CanCorrectDuplex) return;
        var backs = (_duplexBacks ?? []).Where(_pages.Contains).ToList();
        if (backs.Count == 0) return;
        var selected = SelectedPages();
        _backsTurnedNow = !_backsTurnedNow;
        await TurnPagesAsync(backs, 2);
        RememberDuplexCorrection();
        Rebuild(Current, selected);
    }

    /// <summary>
    /// Turns the fronts of the last stack by turning by half a turn (again: back) — the stack was laid upside down. Offered already while the
    /// backs are awaited (Festlegung): the backs, laid the same way, then come turned by themselves. A stack laid upside down as a
    /// whole (fronts and backs turned) leaves what is remembered as it was; while the backs are awaited nothing is remembered (how they lie
    /// against the fronts is not known yet).
    /// </summary>
    [RelayCommand]
    private async Task TurnFrontsAsync()
    {
        var fronts = (AwaitingBacks ? _fronts : CanCorrectDuplex ? _duplexFronts : null) is { } read ? read.Where(_pages.Contains).ToList() : [];
        if (fronts.Count == 0) return;
        var selected = SelectedPages();
        _frontsTurnedNow = !_frontsTurnedNow;
        await TurnPagesAsync(fronts, 2);
        if (CanCorrectDuplex) RememberDuplexCorrection();
        Rebuild(Current, selected);
    }

    /// <summary>
    /// Scans the backs of the last stack by turning anew (a jam, turned the wrong way, a sheet missing): the fronts stay, the backs scanned before
    /// stay until the new ones are there (or are kept with „Bisherige behalten“).
    /// </summary>
    [RelayCommand]
    private void RescanBacks()
    {
        if (!CanCorrectDuplex || _duplexFronts is not { } fronts || _duplexBacks is not { } backs) return;
        (_fronts, _frontsStack, _replacing) = (fronts, _duplexStack, backs);
        CanCorrectDuplex = false;
        AwaitingBacks = true;
        OnPropertyChanged(nameof(BacksText));
        OnPropertyChanged(nameof(EndDuplexText));
        Report(BacksText);
    }

    /// <summary>
    /// Puts the backs of the last scan by turning in the other order (the last first, or in the order of the sheets), and remembers it. The
    /// pairing is made anew from all sides read (blank ones included), so a page left out never shifts the others; pages deleted since stay away.
    /// </summary>
    [RelayCommand]
    private void SwapBacks()
    {
        if (!CanCorrectDuplex || _duplexFronts is not { } fronts || _duplexBacks is not { } backs) return;
        var present = fronts.Concat(backs).Where(_pages.Contains).ToList();
        if (present.Count == 0) return;
        BacksInFeedOrder = !BacksInFeedOrder;
        var lastFirst = !BacksInFeedOrder;
        var start = present.Min(p => _pages.IndexOf(p));
        foreach (var page in present)
        {
            Unlink(page);
            _pages.Remove(page);
        }
        for (var i = 0; i < fronts.Count; i++)
            if (ManualDuplexOrder.BackOf(i, fronts.Count, backs.Count, lastFirst) is var j and >= 0 && present.Contains(fronts[i]) && present.Contains(backs[j]))
                Link(fronts[i], backs[j]);
        _pages.InsertRange(Math.Min(start, _pages.Count), ManualDuplexOrder.Interleave(fronts, backs, lastFirst).Where(present.Contains));
        RememberDuplexCorrection();
        Rebuild(Current, SelectedPages());
        // the sides of each sheet are others now: the tiles take out the show-through of the right back
        _ = RefreshAsync();
    }

    /// <summary>Hides the bar; the pages stay as they are.</summary>
    [RelayCommand]
    private void HideDuplexCorrection() => CanCorrectDuplex = false;

    /// <summary>
    /// Keeps the correction for the next scan by turning: in the settings last used and in the selected profile (Festlegung: „im Profil
    /// merken“ — whoever lays the stack the same way corrects once).
    /// </summary>
    private void RememberDuplexCorrection()
    {
        // how the backs lie against the fronts: a stack laid upside down as a whole changes nothing for the next one
        BacksTurned = _backsTurnedNow ^ _frontsTurnedNow;
        if (CurrentSettings() is { } current)
        {
            _wish = current with { Name = SelectedProfile?.Name ?? "" };
            UserSettings.Update(s => s with { Scan = current });
        }
        if (SelectedProfile is { } profile)
        {
            var saved = _profiles.Save(profile with { BacksTurned = BacksTurned, BacksInFeedOrder = BacksInFeedOrder });
            ReloadProfiles(saved.Name);
        }
    }

    /// <summary>Turns pages by quarter turns; the other side of each sheet gets a new tile too (its show-through comes from the turned page).</summary>
    private async Task TurnPagesAsync(IReadOnlyList<ScanPage> pages, int turns)
    {
        foreach (var page in pages)
        {
            var turned = await Task.Run(() => ScanImageOps.Rotate(page.Image, turns));
            if (await Task.Run(() => page.SetImage(turned)) is { } old) _retired.Add(old);
            if (page.Partner is { } other) _tilesToRefresh.Add(other);
        }
        _ = RefreshAsync();
    }

    /// <summary>Where the backs go while they are read: after the last front (or back to be replaced) still there.</summary>
    private int BacksIndex() => (_fronts ?? []).Concat(_replacing ?? []).Select(p => _pages.IndexOf(p)).Where(i => i >= 0)
        .DefaultIfEmpty(_pages.Count - 1).Max() + 1;

    /// <summary>
    /// Stops waiting for the backs: the fronts stay as they are; when scanning the backs anew, the backs scanned before stay and the correction
    /// of the stack is offered again.
    /// </summary>
    [RelayCommand]
    private void EndDuplex()
    {
        var replacing = _replacing is not null;
        EndWaiting();
        if (replacing) CanCorrectDuplex = _duplexFronts is not null;
    }

    private void EndWaiting()
    {
        (_fronts, _replacing) = (null, null);
        AwaitingBacks = false;
        OnPropertyChanged(nameof(EndDuplexText));
    }

    partial void OnManualDuplexChanged(bool value)
    {
        if (!value) EndDuplex();
    }

    // ---------------------------------------------------------------- profiles

    /// <summary>Reads the profiles again and selects one by name, without applying it.</summary>
    private void ReloadProfiles(string? select)
    {
        _loadingProfiles = true;
        try
        {
            Profiles = _profiles.Load();
            SelectedProfile = select is null ? null : Profiles.FirstOrDefault(p => p.Name == select);
        }
        finally
        {
            _loadingProfiles = false;
        }
    }

    partial void OnSelectedProfileChanged(ScanProfile? value)
    {
        if (_loadingProfiles || value is null) return;
        UserSettings.Update(s => s with { ScanProfileName = value.Name });
        _ = ApplyProfileAsync(value);
    }

    /// <summary>
    /// Applies a profile: its device if it is there (opening it applies the rest), otherwise the rest to the device open now. A device that
    /// is not there is chosen as soon as a search finds it.
    /// </summary>
    private async Task ApplyProfileAsync(ScanProfile profile)
    {
        _wish = profile;
        (ManualDuplex, SkipBlank, BlankLimit, BacksTurned, BacksInFeedOrder) = (profile.ManualDuplex, profile.SkipBlank, profile.BlankLimit, profile.BacksTurned, profile.BacksInFeedOrder);
        _next = profile.Adjustments ?? new ScanAdjustments();
        if (Current is null) LoadAdjustments(_next);
        var device = profile.FindDevice(Devices);
        if (device is not null && device != SelectedDevice)
        {
            SelectedDevice = device;   // opening applies the profile
            return;
        }
        var missing = device is null && profile.DeviceId is not null
            ? Tr.T("Der Scanner des Profils ist nicht da ({0}) — die übrigen Einstellungen gelten.", profile.DeviceName ?? profile.DeviceId)
            : null;
        if (_session is not { } session)
        {
            Report(missing, error: missing is not null);
            return;
        }
        IsBusy = true;
        try
        {
            var adjusted = await ApplyAsync(session, profile);
            var text = string.Join(" ", new[] { missing, adjusted }.OfType<string>());
            Report(text.Length > 0 ? text : Tr.T("Profil „{0}“ gewählt.", profile.Name), error: missing is not null);
        }
        catch (ScanException e)
        {
            Report(e.Message, error: true);
        }
        catch (Exception e) when (Failure.Handled(e))
        {
            Unexpected(e);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Opens the box for the name of the profile to save (the chosen profile's name to begin with).</summary>
    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private void SaveProfile()
    {
        NewProfileName = SelectedProfile?.Name ?? "";
        OverwritePending = false;
        IsNamingProfile = true;
    }

    private bool CanSaveProfile() => SelectedDevice is not null && !IsScanning;

    /// <summary>Saves the settings under the name; a name already there is replaced only on the second press („Ersetzen“).</summary>
    [RelayCommand(CanExecute = nameof(HasProfileName))]
    private void ConfirmProfile()
    {
        var name = ScanProfile.CleanName(NewProfileName);
        if (name.Length == 0 || CurrentSettings() is not { } current) return;
        if (_profiles.Exists(name) && !OverwritePending)
        {
            OverwritePending = true;
            Report(Tr.T("Ein Profil „{0}“ gibt es schon. „Ersetzen“ ersetzt es.", name));
            return;
        }
        try
        {
            var saved = _profiles.Save(current with { Name = name });
            _wish = saved;
            ReloadProfiles(saved.Name);
            UserSettings.Update(s => s with { ScanProfileName = saved.Name });
            IsNamingProfile = false;
            OverwritePending = false;
            Report(Tr.T("Profil „{0}“ gespeichert.", saved.Name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Report(Tr.T("Das Profil ließ sich nicht speichern: {0}", e.Message), error: true);
        }
    }

    private bool HasProfileName() => ScanProfile.CleanName(NewProfileName).Length > 0;

    partial void OnNewProfileNameChanged(string value) => OverwritePending = false;

    /// <summary>Closes the box for the name without saving.</summary>
    [RelayCommand]
    private void CancelProfile()
    {
        IsNamingProfile = false;
        OverwritePending = false;
    }

    private bool CanScan() => _session is not null && !IsScanning && !IsBusy && !IsOpening;

    private ScanSettings? _lastSettings;
    private bool _reconnect;

    // While the driver reports nothing for the sheet, the bar moves on by the time the last sheets took (up to 95 %, then it waits)
    async Task EstimateAsync(CancellationToken stop)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var page = ScanningPage;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(200, stop);
                if (ScanningPage != page)
                {
                    page = ScanningPage;
                    watch.Restart();
                }
                // the seconds of a phase without a share go on
                if (Progress < 0) OnPropertyChanged(nameof(ProgressText));
                // estimated only while the scanner reads: waiting, transferring with rows, unpacking and closing have their own display
                if (Phase != ScanPhase.Reading || _sheetTimes.Count == 0 || (Progress > 0 && !ProgressEstimated)) continue;
                var average = TimeSpan.FromTicks((long)_sheetTimes.TakeLast(3).Average(t => t.Ticks));
                if (average <= TimeSpan.Zero) continue;
                ProgressEstimated = true;
                Progress = Math.Min(0.95, watch.Elapsed / average);
            }
        }
        catch (OperationCanceledException)
        {
            // the scan has ended
        }
    }

    /// <summary>Stops the scanner; pages already read stay.</summary>
    [RelayCommand(CanExecute = nameof(IsScanning))]
    public void Cancel() => _scan?.Cancel();

    // ---------------------------------------------------------------- pages

    /// <summary>Called by the tiles when the selection changes: the last selected page goes to the preview.</summary>
    public void SelectionChanged()
    {
        var selected = Pages.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0) return;
        var page = Current is { } c && selected.Any(p => ReferenceEquals(p.Item, c.Item)) && selected.Count > 1 ? c : PageOf(selected[^1]);
        if (page is not null && page != Current) Show(page);
        OnPropertyChanged(nameof(AdjustmentsTarget));
    }

    /// <summary>Moves the selected pages in front of the page at <paramref name="insertBefore"/> (dragging in the tiles).</summary>
    public void MoveSelected(int insertBefore)
    {
        var selected = Pages.Where(p => p.IsSelected).Select(PageOf).OfType<ScanPage>().ToList();
        if (selected.Count == 0) return;
        var before = _pages.Take(Math.Clamp(insertBefore, 0, _pages.Count)).Count(p => !selected.Contains(p));
        var rest = _pages.Where(p => !selected.Contains(p)).ToList();
        rest.InsertRange(before, selected);
        if (rest.SequenceEqual(_pages)) return;
        _pages.Clear();
        _pages.AddRange(rest);
        Rebuild(Current, selected);
    }

    [RelayCommand(CanExecute = nameof(HasCurrent))]
    private Task RotateLeft() => RotateAsync(-1);

    [RelayCommand(CanExecute = nameof(HasCurrent))]
    private Task RotateRight() => RotateAsync(1);

    /// <summary>Turns the selected pages (or the shown one) by a quarter turn.</summary>
    private async Task RotateAsync(int turns)
    {
        var targets = SelectedOrCurrent();
        await TurnPagesAsync(targets, turns);
        // the turned pages have new tile ids: the selection is kept by page, not by id
        Rebuild(Current, targets);
    }

    /// <summary>
    /// Splits the selected pages (or the shown one) into two pages each: at the spine of a double page where one is found, otherwise in the
    /// middle. Both halves keep the processing of the page.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasCurrent))]
    private async Task SplitAtSpine()
    {
        var targets = SelectedOrCurrent();
        if (targets.Count == 0) return;
        var halves = new List<ScanPage>();
        var atSpine = 0;
        foreach (var page in targets)
        {
            var image = page.Image;
            var (left, right, found) = await Task.Run(() =>
            {
                var spine = BookStraightening.FindSpine(ScanPdf.Reduce(image, PageStraightening.FindEdge));
                var at = Math.Clamp((int)Math.Round(image.Width * (spine ?? 0.5)), 1, image.Width - 1);
                return (new ScanPage(ImageGeometry.Crop(image, 0, 0, image.Width - at, 0), page.Adjustments) { Stack = page.Stack },
                    new ScanPage(ImageGeometry.Crop(image, at, 0, 0, 0), page.Adjustments) { Stack = page.Stack }, spine is not null);
            });
            if (found) atSpine++;
            var index = _pages.IndexOf(page);
            if (index < 0)
            {
                left.Dispose();
                right.Dispose();
                continue;
            }
            Unlink(page);
            _pages[index] = left;
            _pages.Insert(index + 1, right);
            _retired.Add(page.Preview);
            halves.Add(left);
            halves.Add(right);
        }
        Rebuild(halves.FirstOrDefault(), halves);
        _ = RefreshAsync();   // the other side of a split page, without its show-through
        Report(atSpine == targets.Count ? Tr.T("Am Bund geteilt: {0} Seiten.", halves.Count)
            : Tr.T("Geteilt: {0} Seiten — bei {1} Seiten war kein Bund zu erkennen, dort in der Mitte.", halves.Count, targets.Count - atSpine));
    }

    /// <summary>Deletes the selected pages (or the shown one).</summary>
    [RelayCommand(CanExecute = nameof(HasCurrent))]
    private void Delete()
    {
        var targets = SelectedOrCurrent();
        if (targets.Count == 0) return;
        var index = Current is { } c ? _pages.IndexOf(c) : 0;
        foreach (var page in targets)
        {
            Unlink(page);
            _pages.Remove(page);
            _retired.Add(page.Preview);
        }
        var next = _pages.Count == 0 ? null : _pages[Math.Clamp(index, 0, _pages.Count - 1)];
        Rebuild(next);
        _ = RefreshAsync();   // the other side of a deleted page, without its show-through
    }

    private bool HasCurrent() => Current is not null;

    private List<ScanPage> SelectedOrCurrent()
    {
        var selected = Pages.Where(p => p.IsSelected).Select(PageOf).OfType<ScanPage>().ToList();
        return selected.Count > 0 ? selected : Current is { } c ? [c] : [];
    }

    private ScanPage? PageOf(PageViewModel tile) => _pages.FirstOrDefault(p => ReferenceEquals(p.Item, tile.Item));

    /// <summary>Rebuilds the tiles after a change; <paramref name="show"/> becomes the shown page.</summary>
    /// <param name="show">The page for the preview; null for none.</param>
    /// <param name="selected">The pages to select; null: only <paramref name="show"/>.</param>
    /// <param name="keepPreview">Whether the preview stays (only the tiles changed).</param>
    private void Rebuild(ScanPage? show, IReadOnlyCollection<ScanPage>? selected = null, bool keepPreview = false)
    {
        Renderer.SetPages(_pages.Select(p => new RenderPage(p.Item.Id, p.Preview, p.Item.Page)));
        var headers = GroupHeaders();
        Pages = [.. _pages.Select((p, i) => new PageViewModel(p.Item, i)
        {
            IsSelected = selected is { Count: > 0 } ? selected.Contains(p) : ReferenceEquals(p, show),
            Badge = p.StraightenOff ? PageBadge.Discarded : p.IsStraightened ? PageBadge.Applied : PageBadge.None,
            BadgeTip = p.StraightenOff ? Tr.T("Begradigen für diese Seite verworfen — klicken, um sie wieder zu begradigen")
                : p.IsStraightened ? Tr.T("Diese Seite ist begradigt — klicken, um sie wie gescannt zu lassen") : null,
            GroupHeader = headers[i],
        })];
        if (show is null)
        {
            Current = null;
            Preview = null;
            LoadAdjustments(_next);
        }
        else if (!keepPreview || !ReferenceEquals(show, Current)) Show(show);
        OnPropertyChanged(nameof(CurrentText));
        OnPropertyChanged(nameof(AdjustmentsTarget));
        OnPropertyChanged(nameof(CurrentStraightened));
        OnPropertyChanged(nameof(StraightenToggleText));
    }

    /// <summary>Shows a page: its processing in the sliders, its preview (computed in the background).</summary>
    private void Show(ScanPage page)
    {
        IsFreeRotating = false;
        Current = page;
        LoadAdjustments(page.Adjustments);
        OnPropertyChanged(nameof(CurrentText));
        OnPropertyChanged(nameof(AdjustmentsTarget));
        _ = UpdatePreviewAsync();
    }

    // ---------------------------------------------------------------- taking over

    /// <summary>Takes the pages over as a new document.</summary>
    [RelayCommand(CanExecute = nameof(CanTakeOver))]
    private Task TakeOverNew() => TakeOverAsync(newDocument: true);

    /// <summary>Appends the pages to the open document.</summary>
    [RelayCommand(CanExecute = nameof(CanAppendNow))]
    private Task TakeOverAppend() => TakeOverAsync(newDocument: false);

    /// <summary>
    /// Processes the pages at full resolution (in the background, with progress) and packs each into the PDF right away, so only one processed
    /// page is in memory at a time (all of them at once ran out of memory with 20 colour pages at 300 dpi under a limit of 1.1 GB; one by one
    /// they fit into 1 GB — PRUEFUNGEN §6z); then hands the document over.
    /// </summary>
    private async Task TakeOverAsync(bool newDocument)
    {
        IsBusy = true;
        try
        {
            var pages = _pages.ToList();
            Report(Tr.T("Bereite Seite {0} von {1} auf …", 1, pages.Count));
            var progress = new Progress<int>(i => Report(Tr.T("Bereite Seite {0} von {1} auf …", i + 1, pages.Count)));
            IProgress<int> report = progress;
            var document = await Task.Run(() => ScanPdf.Build(pages.Count, i =>
            {
                report.Report(i);
                return pages[i].Processed();
            }));
            Report(null);
            TakeOverRequested?.Invoke(new ScanResult(document, newDocument, RecognizeAfter));
        }
        catch (Exception e) when (Failure.Handled(e))
        {
            Unexpected(e);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Chooses how the pages are saved as images: the last settings, the number of all and of the selected pages; null if cancelled.</summary>
    public Func<PageImageSettings, int, int, Task<PageImageSettings?>>? PickPageImages { get; set; }

    /// <summary>Chooses the folder for the images; null if cancelled.</summary>
    public Func<string, Task<string?>>? PickFolder { get; set; }

    /// <summary>
    /// „Als Bild speichern …“ (KONZEPT §5.25): all or the selected pages, processed at full resolution as they would be taken over, as image
    /// files in a folder — named „Scan_Datum_Uhrzeit_001“, in the resolution of the scan.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTakeOver))]
    private async Task SaveImages()
    {
        if (PickPageImages is null || PickFolder is null) return;
        var selected = Pages.Where(p => p.IsSelected).Select(PageOf).OfType<ScanPage>().ToList();
        var start = UserSettings.Current.PageImages ?? new PageImageSettings();
        if (await PickPageImages(start, _pages.Count, selected.Count) is not { } settings) return;
        UserSettings.Update(s => s with { PageImages = settings });
        if (await PickFolder(Tr.T("Ordner für die Bilder")) is not { } folder) return;
        var pages = settings.SelectedOnly && selected.Count > 0 ? _pages.Where(selected.Contains).ToList() : _pages.ToList();
        var baseName = "Scan_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm", System.Globalization.CultureInfo.InvariantCulture);
        IsBusy = true;
        try
        {
            Report(Tr.T("Speichere Seite {0} von {1} …", 1, pages.Count));
            var progress = new Progress<int>(done => Report(Tr.T("Speichere Seite {0} von {1} …", Math.Min(pages.Count, done + 1), pages.Count)));
            var files = await Task.Run(() => PageImageWriter.Write(pages.Count, i => pages[i].Processed(), folder, baseName, settings, progress));
            Report(files.Count == 1 ? Tr.T("1 Bild gespeichert in {0}", folder) : Tr.T("{0} Bilder gespeichert in {1}", files.Count, folder));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Report(Tr.T("Speichern fehlgeschlagen: {0}", e.Message), error: true);
        }
        catch (Exception e) when (Failure.Handled(e))
        {
            Unexpected(e);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanTakeOver() => _pages.Count > 0 && !IsScanning && !IsBusy;

    private bool CanAppendNow() => CanAppend && CanTakeOver();

    /// <summary>Releases the device, the renderer and the preview documents (after the window has closed).</summary>
    public async Task ReleaseAsync()
    {
        _scan?.Cancel();
        StopFeederWatch();
        if (_session is { } session)
        {
            _session = null;
            try { await session.DisposeAsync(); }
            catch (ScanException) { /* the device is gone; nothing to release */ }
        }
        var documents = _pages.Select(p => p.Preview).Concat(_retired).ToList();
        Renderer.SetPages([]);
        Renderer.Dispose();
        await Renderer.WaitUntilUnused(documents);
        foreach (var document in documents) document.Dispose();
        _pages.Clear();
        _retired.Clear();
    }

    /// <summary>An error the scanner interface did not describe (a driver delivering nonsense): logged, told briefly, the program goes on.</summary>
    private void Unexpected(Exception e)
    {
        AppLog.Write("Scannen", e.ToString());
        Report(Tr.T("Unerwarteter Fehler beim Scannen — Einzelheiten stehen im Protokoll."), error: true);
    }

    private void Report(string? message, bool error = false)
    {
        Status = message;
        StatusIsError = error && message is not null;
    }

    /// <summary>
    /// The page for the preview, reduced to at most 4000 pixels on its longest edge: enough to look at details when zoomed in (an A4 page at
    /// 300 dpi stays as scanned), and at most about 60 MB for one page shown.
    /// </summary>
    internal static Bitmap ToBitmap(ScannedImage image)
    {
        var small = ScanPdf.Reduce(image, PreviewEdge);
        var bitmap = new WriteableBitmap(new PixelSize(small.Width, small.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var target = bitmap.Lock();
        var row = new byte[small.Width * 4];
        for (var y = 0; y < small.Height; y++)
        {
            for (var x = 0; x < small.Width; x++)
            {
                var at = ((long)y * small.Width + x) * small.Channels;
                byte r, g, b;
                if (small.Channels == 1) r = g = b = small.Pixels[at];
                else (r, g, b) = (small.Pixels[at], small.Pixels[at + 1], small.Pixels[at + 2]);
                row[x * 4] = b;
                row[x * 4 + 1] = g;
                row[x * 4 + 2] = r;
                row[x * 4 + 3] = 255;
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, target.Address + y * target.RowBytes, row.Length);
        }
        return bitmap;
    }
}
