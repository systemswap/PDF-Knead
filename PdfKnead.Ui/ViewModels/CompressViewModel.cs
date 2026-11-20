using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using PdfKnead.Services;
using SkiaSharp;

namespace PdfKnead.ViewModels;

/// <summary>
/// File → Compress: all settings to try out on the left, the page before/after on the right with a movable divider and zoom, plus
/// the sizes before → after, broken down. Every change starts a real trial run into memory in the background, so sizes and the
/// after image come from exactly the file that would be saved.
/// </summary>
public sealed partial class CompressViewModel : ObservableObject, IDisposable
{
    /// <summary>Longest edge of the page images in pixels (whole-page view; when zooming in, the visible region is redrawn).</summary>
    private const int BasePixels = 1400;

    private readonly DocumentViewModel _doc;
    private readonly PdfDocument _main;
    private readonly PageItem[] _items;
    private readonly RasterSettings? _raster;

    /// <summary>What is written: the document itself or, with „Flachlegen als Bild“ scheduled, its page images (computed once).</summary>
    private PdfDocument? _asImages;
    private readonly IDisposable[] _uses;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly ImageCache _images = new(CachePool.Images);

    /// <summary>What the trial runs need not recompute (image resolutions, font usage, repacked streams …), also used for saving.</summary>
    private readonly CompressionCache _cache = new();
    private CancellationTokenSource? _rebuild;
    private PdfDocument? _result;
    private Core.Pdf.IO.MemoryPdf? _resultBuffer;
    private bool _applying;
    private DisplayList? _beforeList, _afterList;
    private int _listsPage = -1;
    private readonly Task _start;

    public CompressViewModel(DocumentViewModel doc)
    {
        _doc = doc;
        _main = doc.Document;
        _items = [.. doc.History.Pages];
        FileName = doc.FileName;
        _raster = doc.RasterOnSave;
        PageCount = _items.Length;
        _uses = [.. _items.Select(i => i.Source).Append(_main).Distinct().Select(d => doc.Renderer.Use(d)).OfType<IDisposable>()];
        Presets =
        [
            new(CompressionPreset.Screen, Tr.T("Bildschirm — Mail, Ansehen (120 dpi)")),
            new(CompressionPreset.EBook, Tr.T("E-Book — Tablet, noch druckbar (150 dpi)")),
            new(CompressionPreset.Print, Tr.T("Druck — gute Druckqualität (300 dpi)")),
            new(CompressionPreset.Lossless, Tr.T("Nur verlustfrei — Aussehen bleibt gleich")),
            new(CompressionPreset.Custom, Tr.T("Eigene")),
        ];
        // Methods per image type (see KONZEPT.md, section 5.12): each list is created once, since the selection compares objects
        PhotoMethods = [CodecCatalog.Jpeg, CodecCatalog.JpegXl];
        GraphicsMethods = [CodecCatalog.Flate, CodecCatalog.Palette];
        BilevelMethods = [CodecCatalog.Ccitt, CodecCatalog.Jbig2];
        ScanMethods = [CodecCatalog.AsPhotos, CodecCatalog.Mrc];
        PackMethods = [CodecCatalog.FlatePack, CodecCatalog.FlateStrong, CodecCatalog.Brotli];
        _applying = true;
        SelectedPhotoMethod = PhotoMethods[0];
        SelectedBilevelMethod = BilevelMethods[0];
        SelectedScanMethod = ScanMethods[0];
        SelectedGraphicsMethod = GraphicsMethods[1];
        _applying = false;
        // A fixed preset is always taken fresh (the stored values are those from when it was saved; after an update „Druck“ would
        // otherwise compute e.g. with MRC and palette while „Speichern unter“ uses the new values); only „Eigene“ is used as stored
        var stored = (UserSettings.Current.Compression ?? CompressionSettings.Default).Checked();
        var settings = stored.Preset == CompressionPreset.Custom ? stored : CompressionSettings.For(stored.Preset) with { Brotli = stored.Brotli, StrongFlate = stored.StrongFlate };
        Apply(settings);
        _applying = true;
        SelectedPreset = Presets.FirstOrDefault(p => p.Value == settings.Preset) ?? Presets.First(p => p.Value == CompressionSettings.Default.Preset);
        _applying = false;
        _start = StartAsync();
    }

    public string FileName { get; }

    /// <summary>Whether „Flachlegen als Bild“ is scheduled: the copy is then made from page images, as when saving, and then compressed.</summary>
    public bool AsImages => _raster is not null;

    public IReadOnlyList<PrintChoice<CompressionPreset>> Presets { get; }

    [ObservableProperty] public partial PrintChoice<CompressionPreset> SelectedPreset { get; set; }

    /// <summary>Whether images are downsampled to a target resolution (otherwise they stay as they are).</summary>
    [ObservableProperty] public partial bool Downsample { get; set; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DpiText))] public partial double Dpi { get; set; } = 150;
    public string DpiText => Tr.T("{0} dpi", Math.Round(Dpi));

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(QualityText))] public partial double Quality { get; set; } = 80;
    public string QualityText => Tr.T("{0} %", Math.Round(Quality));

    [ObservableProperty] public partial bool Bilevel { get; set; }
    [ObservableProperty] public partial bool SubsetFonts { get; set; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SelectedPackMethod))] public partial bool Brotli { get; set; }

    /// <summary>„Flate – stark (langsamer)“: own compressor, about a tenth smaller, saving takes about five times as long.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SelectedPackMethod))] public partial bool StrongFlate { get; set; }

    // ------------------------------------------------------------ Methods per image type (KONZEPT.md, section 5.12)

    /// <summary>Photos: JPEG; JPEG XL is listed greyed out (the PDF standard for it has not yet been published).</summary>
    public IReadOnlyList<CodecInfo> PhotoMethods { get; }
    [ObservableProperty] public partial CodecInfo SelectedPhotoMethod { get; set; }

    /// <summary>Graphics and screenshots: Flate with predictor, or palette.</summary>
    public IReadOnlyList<CodecInfo> GraphicsMethods { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsPalette), nameof(IsFlate))] public partial CodecInfo SelectedGraphicsMethod { get; set; }
    public bool IsPalette => SelectedGraphicsMethod == GraphicsMethods[1];
    public bool IsFlate => !IsPalette;

    public IReadOnlyList<CodecInfo> BilevelMethods { get; }
    [ObservableProperty] public partial CodecInfo SelectedBilevelMethod { get; set; }

    public IReadOnlyList<CodecInfo> ScanMethods { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsMrc), nameof(IsAsPhotos))] public partial CodecInfo SelectedScanMethod { get; set; }
    public bool IsMrc => SelectedScanMethod == ScanMethods[1];
    public bool IsAsPhotos => !IsMrc;

    // MRC: background resolution and quality come from the preset, so that there are only a few clear controls; the text only as a
    // 600 / 300 dpi switch (600 by default, changeable by hand)
    private int _mrcTextDpi = 600, _mrcBackgroundDpi = 100, _mrcBackgroundQuality = 60;

    /// <summary>MRC: smooth text; masks at 600 dpi (twice as fine as a 300 dpi scan: smooth edges), otherwise 300 dpi.</summary>
    public bool MrcSmoothText
    {
        get => _mrcTextDpi >= 600;
        set
        {
            if (value == MrcSmoothText) return;
            _mrcTextDpi = value ? 600 : 300;
            OnPropertyChanged();
            Changed();
        }
    }

    /// <summary>MRC: flatten paper; noise and show-through from the reverse side in the background become an even paper tone.</summary>
    [ObservableProperty] public partial bool MrcFlattenPaper { get; set; } = true;

    /// <summary>MRC: text detection sensitivity (percent).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MrcSensitivityText))] public partial double MrcSensitivity { get; set; } = 50;
    public string MrcSensitivityText => Tr.T("{0} %", Math.Round(MrcSensitivity));

    /// <summary>MRC: maximum number of text colours.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MrcColorsText))] public partial double MrcColors { get; set; } = 4;
    public string MrcColorsText => Math.Round(MrcColors).ToString("0", Tr.Culture);

    /// <summary>Compression of text, fonts and drawings: Flate, strong Flate or Brotli (Brotli with a warning).</summary>
    public IReadOnlyList<CodecInfo> PackMethods { get; }
    public CodecInfo SelectedPackMethod
    {
        get => PackMethods[Brotli ? 2 : StrongFlate ? 1 : 0];
        set
        {
            if (value is null) return;
            var index = PackMethods.ToList().IndexOf(value);
            if (index < 0 || value == SelectedPackMethod) return;
            // Both switches at once, then one trial run
            var applying = _applying;
            _applying = true;
            StrongFlate = index == 1;
            Brotli = index == 2;
            _applying = applying;
            if (!_applying) Rebuild();
        }
    }

    /// <summary>Palette: maximum number of colours.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PaletteColorsText))] public partial double PaletteColors { get; set; } = 256;
    public string PaletteColorsText => Math.Round(PaletteColors).ToString("0", Tr.Culture);

    /// <summary>Palette: dithering against banding in gradients (percent).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PaletteDitherText))] public partial double PaletteDither { get; set; }
    public string PaletteDitherText => Tr.T("{0} %", Math.Round(PaletteDither));

    /// <summary>Palette: minimum quality; below it the image is stored as it would be without a palette.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PaletteQualityText))] public partial double PaletteQuality { get; set; } = 80;
    public string PaletteQualityText => Tr.T("{0} %", Math.Round(PaletteQuality));

    // ------------------------------------------------------------ Settings

    /// <summary>Takes over values from settings without this counting as a change of its own.</summary>
    private void Apply(CompressionSettings settings)
    {
        _applying = true;
        Downsample = settings.ImageDpi is not null;
        Dpi = settings.ImageDpi ?? Dpi;
        Quality = settings.JpegQuality;
        Bilevel = settings.BilevelScans;
        SubsetFonts = settings.SubsetFonts;
        Brotli = settings.Brotli;
        StrongFlate = settings.StrongFlate;
        SelectedGraphicsMethod = GraphicsMethods[settings.Graphics == GraphicsMethod.Palette ? 1 : 0];
        PaletteColors = settings.PaletteColors;
        PaletteDither = settings.PaletteDither;
        PaletteQuality = settings.PaletteQuality;
        SelectedScanMethod = ScanMethods[settings.Scans == ScanMethod.Mrc ? 1 : 0];
        SelectedBilevelMethod = BilevelMethods[settings.BilevelCodec == BilevelMethod.Jbig2 ? 1 : 0];
        (_mrcTextDpi, _mrcBackgroundDpi, _mrcBackgroundQuality) = (settings.MrcTextDpi, settings.MrcBackgroundDpi, settings.MrcBackgroundQuality);
        OnPropertyChanged(nameof(MrcSmoothText));
        MrcFlattenPaper = settings.MrcFlattenPaper;
        MrcSensitivity = settings.MrcSensitivity;
        MrcColors = settings.MrcColors;
        _applying = false;
    }

    /// <summary>Modification date of the last trial run; saving uses it, so that the predicted size is exact.</summary>
    private DateTimeOffset _stamp = DateTimeOffset.Now;

    /// <summary>Returns the current settings.</summary>
    /// <returns>The settings, checked.</returns>
    public CompressionSettings Settings() => new CompressionSettings(SelectedPreset.Value, Downsample ? (int)Math.Round(Dpi) : null,
        (int)Math.Round(Quality), Bilevel, SubsetFonts, Brotli, IsPalette ? GraphicsMethod.Palette : GraphicsMethod.Flate,
        (int)Math.Round(PaletteColors), (int)Math.Round(PaletteDither), (int)Math.Round(PaletteQuality), IsMrc ? ScanMethod.Mrc : ScanMethod.AsPhotos,
        _mrcTextDpi, _mrcBackgroundDpi, _mrcBackgroundQuality, (int)Math.Round(MrcSensitivity),
        (int)Math.Round(MrcColors), SelectedBilevelMethod == BilevelMethods[1] ? BilevelMethod.Jbig2 : BilevelMethod.G4, MrcFlattenPaper, StrongFlate).Checked();

    partial void OnSelectedPresetChanged(PrintChoice<CompressionPreset> value)
    {
        if (_applying) return;
        if (value.Value != CompressionPreset.Custom)
            Apply(CompressionSettings.For(value.Value) with { Brotli = Brotli, StrongFlate = StrongFlate });
        else if (UserSettings.Current.Compression is { Preset: CompressionPreset.Custom } custom)
            Apply(custom);
        Rebuild();
    }

    /// <summary>
    /// Resets everything to the default (E-Book), including Brotli and the methods. The stored „Eigene“ values remain until the next
    /// save. A trial run only if something changes.
    /// </summary>
    [RelayCommand]
    private void Reset()
    {
        var before = Settings();
        Apply(CompressionSettings.Default);
        _applying = true;
        SelectedPreset = Presets.First(p => p.Value == CompressionSettings.Default.Preset);
        SelectedPhotoMethod = PhotoMethods[0];
        _applying = false;
        if (Settings() != before) Rebuild();
    }

    /// <summary>A control was moved: the preset becomes „Eigene“, then a trial run starts.</summary>
    private void Changed()
    {
        if (_applying) return;
        if (SelectedPreset.Value != CompressionPreset.Custom)
        {
            _applying = true;
            SelectedPreset = Presets[^1];
            _applying = false;
        }
        Rebuild();
    }

    partial void OnDownsampleChanged(bool value) => Changed();
    partial void OnDpiChanged(double value) => Changed();
    partial void OnQualityChanged(double value) => Changed();
    partial void OnBilevelChanged(bool value) => Changed();
    partial void OnSubsetFontsChanged(bool value) => Changed();
    partial void OnMrcFlattenPaperChanged(bool value) => Changed();
    partial void OnSelectedGraphicsMethodChanged(CodecInfo value) => Changed();

    // Methods not yet available are listed greyed out; should one be chosen anyway (keyboard), the available one stays
    partial void OnSelectedPhotoMethodChanged(CodecInfo value)
    {
        if (value is { Available: false }) SelectedPhotoMethod = PhotoMethods[0];
    }

    partial void OnSelectedBilevelMethodChanged(CodecInfo value)
    {
        if (value is { Available: false }) SelectedBilevelMethod = BilevelMethods[0];
        else Changed();
    }

    partial void OnSelectedScanMethodChanged(CodecInfo value)
    {
        if (value is { Available: false }) SelectedScanMethod = ScanMethods[0];
        else Changed();
    }

    partial void OnMrcSensitivityChanged(double value) => Changed();
    partial void OnMrcColorsChanged(double value) => Changed();
    partial void OnPaletteColorsChanged(double value) => Changed();
    partial void OnPaletteDitherChanged(double value) => Changed();
    partial void OnPaletteQualityChanged(double value) => Changed();

    /// <summary>The compression method depends not on the preset but on the readers or on patience, so it stays as chosen.</summary>
    partial void OnBrotliChanged(bool value)
    {
        if (!_applying) Rebuild();
    }

    // ------------------------------------------------------------ Sizes

    [ObservableProperty] public partial SizeReport? Before { get; private set; }
    [ObservableProperty] public partial SizeReport? After { get; private set; }
    [ObservableProperty] public partial long PageImagesBefore { get; private set; }
    [ObservableProperty] public partial long PageImagesAfter { get; private set; } = -1;

    /// <summary>Total such as „12,4 MB → 2,1 MB (−83 %)“.</summary>
    public string TotalText => Before is { } b && After is { } a
        ? Tr.T("{0} → {1} ({2})", DocumentViewModel.FormatSize(b.Total), DocumentViewModel.FormatSize(a.Total), Percent(b.Total, a.Total))
        : Before is { } only ? DocumentViewModel.FormatSize(only.Total) : "";

    public IReadOnlyList<SizeRow> Rows => Before is not { } b ? [] :
    [
        new(Tr.T("Bilder"), b.Images, After?.Images),
        new(Tr.T("Schriften"), b.Fonts, After?.Fonts),
        new(Tr.T("Seiteninhalt"), b.Content, After?.Content),
        new(Tr.T("Sonstiges"), b.Other, After?.Other),
    ];

    /// <summary>Text such as „Bilder dieser Seite: 1,2 MB → 180 KB“.</summary>
    public string PageText => PageImagesAfter < 0
        ? Tr.T("Bilder dieser Seite: {0}", DocumentViewModel.FormatSize(PageImagesBefore))
        : Tr.T("Bilder dieser Seite: {0} → {1}", DocumentViewModel.FormatSize(PageImagesBefore), DocumentViewModel.FormatSize(PageImagesAfter));

    internal static string Percent(long before, long after) =>
        before <= 0 ? "" : after <= before ? $"−{Math.Floor(100.0 * (before - after) / before)} %" : $"+{Math.Round(100.0 * (after - before) / before)} %";

    partial void OnBeforeChanged(SizeReport? value) => SizesChanged();
    partial void OnAfterChanged(SizeReport? value) => SizesChanged();
    partial void OnPageImagesBeforeChanged(long value) => OnPropertyChanged(nameof(PageText));
    partial void OnPageImagesAfterChanged(long value) => OnPropertyChanged(nameof(PageText));

    private void SizesChanged()
    {
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(Rows));
    }

    [ObservableProperty] public partial bool Busy { get; private set; } = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasProblem))] public partial string Problem { get; private set; } = "";
    public bool HasProblem => Problem.Length > 0;

    // ------------------------------------------------------------ Pages and images

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageInfo))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial int PageIndex { get; set; }

    public int PageCount { get; }
    public string PageInfo => Tr.T("Seite {0} von {1}", PageIndex + 1, PageCount);
    public double PageWidth => _items[PageIndex].Page.Width;
    public double PageHeight => _items[PageIndex].Page.Height;

    [ObservableProperty] public partial SKImage? BeforeImage { get; private set; }
    [ObservableProperty] public partial SKImage? AfterImage { get; private set; }

    /// <summary>Region at full resolution when zoomed in (area in points), otherwise null.</summary>
    [ObservableProperty] public partial ZoomRegion? Region { get; private set; }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void PreviousPage() => PageIndex--;

    private bool CanGoBack => PageIndex > 0;

    [RelayCommand(CanExecute = nameof(CanGoOn))]
    private void NextPage() => PageIndex++;

    private bool CanGoOn => PageIndex < PageCount - 1;

    partial void OnPageIndexChanged(int value)
    {
        SetRegion(null);
        _ = RenderPageAsync();
    }

    // ------------------------------------------------------------ Trial run

    private async Task StartAsync()
    {
        try
        {
            Before = await Task.Run(() => SizeReport.Of(_main, _closing.Token));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Before = new SizeReport(_main.FileSize, 0, 0, 0);
        }
        if (_raster is { } raster)
            try
            {
                _asImages = await Task.Run(() => DocumentViewModel.AsImages(_items, raster, _closing.Token));
                _sourceItems = PageItem.From(_asImages);
                SaveCommand.NotifyCanExecuteChanged();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Problem = Tr.T("Die Vorschau ließ sich nicht berechnen: {0}", e.Message);
                Busy = false;
                return;
            }
        Rebuild();
    }

    private PageItem[]? _sourceItems;

    /// <summary>
    /// Options for writing; the trial run uses the same ones as saving, including encryption, so that the predicted size is exact
    /// (without encryption it would be too small for protected files by 3.6 % on average and by up to 55 % for small ones). The trial
    /// run is opened with the writer's key.
    /// </summary>
    private PdfSaveOptions Options(CompressionSettings settings)
    {
        // Redactions were applied before the window opened („Schwärzen zuerst“); a mark left over refuses the trial run and saving
        InDocumentRedaction.EnsureApplied(_items);
        var options = _doc.SaveOptions(_doc.SaveVersion) with { Compression = settings, CompressionCache = _cache, ModificationTime = _stamp };
        if (_raster is not null) options = DocumentViewModel.AsImagesOptions(_main, options);
        return options;
    }

    /// <summary>What is written (document and pages).</summary>
    private (PdfDocument Doc, PageItem[] Items) Source() => _asImages is { } images ? (images, _sourceItems!) : (_main, _items);

    /// <summary>The running trial run (completed if none is running).</summary>
    private Task _running = Task.CompletedTask;

    /// <summary>Starts a new trial run (debounced; the previous computation is cancelled).</summary>
    private async void Rebuild()
    {
        if (_closing.IsCancellationRequested) return;
        _rebuild?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _rebuild = cts;
        Busy = true;
        var done = new TaskCompletionSource();
        try
        {
            await Task.Delay(300, cts.Token);
            // Only one trial run at a time: the previous one ends first (on cancellation), then this one starts; otherwise fast
            // adjustments would run several writers side by side, each with its whole file in memory
            var previous = _running;
            _running = done.Task;
            await previous;
            cts.Token.ThrowIfCancellationRequested();
            _stamp = DateTimeOffset.Now;
            var options = Options(Settings());
            var (source, items) = Source();
            var (buffer, result, report) = await Task.Run(() =>
            {
                var memory = new Core.Pdf.IO.MemoryPdf();
                PdfDocument? doc = null;
                try
                {
                    var written = PdfWriter.Write(source, items, memory, options, cts.Token);
                    doc = written.Security is { } key ? memory.OpenDocument(key) : memory.OpenDocument();
                    return (memory, doc, SizeReport.Of(doc, cts.Token));
                }
                catch
                {
                    // Also when cancelled while measuring: otherwise the opened result would belong to nobody
                    doc?.Dispose();
                    memory.Dispose();
                    throw;
                }
            }, cts.Token);
            if (cts.IsCancellationRequested)
            {
                result.Dispose();
                buffer.Dispose();
                return;
            }
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                _result?.Dispose();
                _resultBuffer?.Dispose();
                (_result, _resultBuffer) = (result, buffer);
                _afterList = null;
            }
            finally
            {
                _gate.Release();
            }
            After = report;
            Problem = "";
            SetRegion(null);
            await RenderPageAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Problem = Tr.T("Die Vorschau ließ sich nicht berechnen: {0}", e.Message);
        }
        finally
        {
            done.TrySetResult();
            if (_rebuild == cts)
            {
                _rebuild = null;
                Busy = false;
            }
            cts.Dispose();
        }
    }

    /// <summary>Renders both whole-page images, plus the image sizes of the page.</summary>
    private async Task RenderPageAsync()
    {
        var index = PageIndex;
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (index != PageIndex || _closing.IsCancellationRequested) return;
            var item = _items[index];
            var ppp = BasePixels / Math.Max(item.Page.Width, item.Page.Height);
            var result = _result;
            var (before, after, imagesBefore, imagesAfter) = await Task.Run(() =>
            {
                EnsureLists(index);
                var b = RenderBefore(item, ppp, null);
                try
                {
                    var a = result is not null && _afterList is not null ? RenderAfter(result, index, ppp, null) : null;
                    return (b, a, SizeReport.PageImages(item.Source, item.Page),
                        result is not null && index < result.PageCount ? SizeReport.PageImages(result, result.Pages[index]) : -1);
                }
                catch
                {
                    b.Dispose();
                    throw;
                }
            });
            if (index != PageIndex || _closing.IsCancellationRequested)
            {
                // Page changed or window closed in the meantime: nobody shows the images
                before.Dispose();
                after?.Dispose();
                return;
            }
            SetImage(before, after);
            PageImagesBefore = imagesBefore;
            PageImagesAfter = imagesAfter;
            OnPropertyChanged(nameof(PageWidth));
            OnPropertyChanged(nameof(PageHeight));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Problem = Tr.T("Die Vorschau ließ sich nicht berechnen: {0}", e.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Display lists of the page: before once per page (a new trial run does not change it; 0.4 s for an A0 plan), after per page and
    /// trial run. Called under the lock.
    /// </summary>
    private void EnsureLists(int index)
    {
        if (_listsPage != index || _beforeList is null)
        {
            var item = _items[index];
            _beforeList = PageInterpreter.Build(item.Source, item.Page, _closing.Token, withoutOwnElements: true);
            _afterList = null;
            _listsPage = index;
        }
        if (_afterList is null && _result is { } result && index < result.PageCount)
            _afterList = PageInterpreter.Build(result, result.Pages[index], _closing.Token);
    }

    /// <summary>Before: the page as displayed (elements on top); <paramref name="area"/> in device pixels, null = whole page.</summary>
    private SKImage RenderBefore(PageItem item, double ppp, SKRectI? area)
    {
        var (w, h) = PageGeometry.DeviceSize(item.Page, ppp);
        var tile = area ?? new SKRectI(0, 0, w, h);
        using var bitmap = new SKBitmap(new SKImageInfo(Math.Max(1, tile.Width), Math.Max(1, tile.Height), SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SkiaRenderer.Paper);
            SkiaRenderer.RenderTile(canvas, _beforeList!, item.Source, PageGeometry.PageToDevice(item.Page, ppp), tile, _images, _closing.Token);
            // Elements (including foreign annotations) into the region: the offset belongs in the matrix, because the overload taking
            // pixels per point resets the canvas, so a preceding Translate would be lost and the elements would be offset when zooming.
            // See docs/FEHLERGEDAECHTNIS.md, No. 82.
            if (item.Elements.Count > 0)
                ElementOverlay.Draw(canvas, item.Page, item.Elements,
                    PageGeometry.PageToDevice(item.Page, ppp) * Core.Pdf.Graphics.Matrix.Translate(-tile.Left, -tile.Top), _images);
        }
        return SKImage.FromBitmap(bitmap);
    }

    private SKImage RenderAfter(PdfDocument result, int index, double ppp, SKRectI? area)
    {
        var page = result.Pages[index];
        var (w, h) = PageGeometry.DeviceSize(page, ppp);
        var tile = area ?? new SKRectI(0, 0, w, h);
        using var bitmap = new SKBitmap(new SKImageInfo(Math.Max(1, tile.Width), Math.Max(1, tile.Height), SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SkiaRenderer.Paper);
            SkiaRenderer.RenderTile(canvas, _afterList!, result, PageGeometry.PageToDevice(page, ppp), tile, _images, _closing.Token);
        }
        return SKImage.FromBitmap(bitmap);
    }

    private void SetImage(SKImage? before, SKImage? after)
    {
        var (oldBefore, oldAfter) = (BeforeImage, AfterImage);
        (BeforeImage, AfterImage) = (before, after);
        if (oldBefore is not null) PrintViewModel.DisposeLater(oldBefore);
        if (oldAfter is not null) PrintViewModel.DisposeLater(oldAfter);
    }

    /// <summary>Sets the region; the previous one is released (up to 40 MP per image).</summary>
    private void SetRegion(ZoomRegion? region)
    {
        var old = Region;
        Region = region;
        if (old is null) return;
        PrintViewModel.DisposeLater(old.Before);
        PrintViewModel.DisposeLater(old.After);
    }

    private int _regionWanted;

    /// <summary>
    /// The view zooms: redraws this area of the page (points, origin top left like the view) at <paramref name="ppp"/>, so that
    /// artefacts become truly visible. Debounced: only the most recently requested area is drawn.
    /// </summary>
    /// <param name="left">Left edge in points.</param>
    /// <param name="top">Top edge in points.</param>
    /// <param name="width">Width in points.</param>
    /// <param name="height">Height in points.</param>
    /// <param name="ppp">Pixels per point; capped at 1200 dpi, and areas over 40 MP are skipped.</param>
    public async void RequestRegion(double left, double top, double width, double height, double ppp)
    {
        var wanted = ++_regionWanted;
        await Task.Delay(120);
        if (wanted != _regionWanted || _closing.IsCancellationRequested) return;
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (wanted != _regionWanted || _closing.IsCancellationRequested || _result is not { } result) return;
            var index = PageIndex;
            if (index >= result.PageCount) return;
            var item = _items[index];
            ppp = Math.Min(ppp, 1200 / 72.0);
            var area = new SKRectI((int)Math.Floor(left * ppp), (int)Math.Floor(top * ppp), (int)Math.Ceiling((left + width) * ppp), (int)Math.Ceiling((top + height) * ppp));
            if (area.Width <= 0 || area.Height <= 0 || (long)area.Width * area.Height > 40_000_000) return;
            var (before, after) = await Task.Run(() =>
            {
                // The lists of this page (after changing pages, those of the previous page would otherwise remain until it is redrawn)
                EnsureLists(index);
                var b = RenderBefore(item, ppp, area);
                try
                {
                    return (b, RenderAfter(result, index, ppp, area));
                }
                catch
                {
                    b.Dispose();
                    throw;
                }
            });
            if (wanted != _regionWanted || index != PageIndex || _closing.IsCancellationRequested)
            {
                before.Dispose();
                after.Dispose();
                return;
            }
            SetRegion(new ZoomRegion(area.Left / ppp, area.Top / ppp, area.Width / ppp, area.Height / ppp, before, after));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // The region is then missing; the whole page remains visible
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------ Saving

    /// <summary>Lets the user choose a save location (title, suggestion, folder); set by the view.</summary>
    public Func<string, string, string?, Task<string?>>? PickSavePath { get; set; }

    /// <summary>Asks before a new file whose digital signatures the rewrite breaks (set by the view); without it nothing is saved.</summary>
    public Func<SignedSaveQuestion, Task<SignedSaveAnswer>>? ConfirmSignedSave { get; set; }

    /// <summary>Raised after saving with a message for the main window; the window closes.</summary>
    public event Action<string>? Finished;

    /// <summary>Set if the opened file was chosen as the target: the main window saves it with these settings.</summary>
    public CompressionSettings? ReplacesOpenFile { get; private set; }

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))] public partial bool Saving { get; private set; }

    /// <summary>With „Flachlegen als Bild“ scheduled, only once the page images are ready.</summary>
    private bool CanSave => !Saving && (_raster is null || _asImages is not null);

    /// <summary>Saves as a new file „Name (komprimiert).pdf“; the opened file stays as it is.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        if (PickSavePath is null) return;
        var settings = Settings();
        UserSettings.Update(s => s with { Compression = settings });
        var name = Path.GetFileNameWithoutExtension(FileName) + " " + Tr.T("(komprimiert)") + ".pdf";
        if (await PickSavePath(Tr.T("Komprimiert speichern"), name, _doc.IsUntitled ? null : _doc.Folder) is not { } path) return;
        if (!_doc.IsUntitled && MainViewModel.SamePath(Path.GetFullPath(path), _doc.Path))
        {
            // The opened file itself: like „Speichern“ with compression, so that the document then shows the compressed file;
            // otherwise the next Ctrl+S would write the uncompressed version back. The main window saves once this window is closed.
            ReplacesOpenFile = settings;
            Finished?.Invoke("");
            return;
        }
        // A new file is written anew, so its digital signatures are broken: asked first as with „Speichern unter“ (the original stays
        // signed); without a view nothing is saved (phase 9.1)
        if (Core.Pdf.Signatures.DigitalSignatures.CountIntact(_doc.Document) is > 0 and var intact)
        {
            var answer = ConfirmSignedSave is { } confirm
                ? await confirm(new SignedSaveQuestion(_doc.FileName, intact, OverwritesOriginal: false))
                : SignedSaveAnswer.Cancel;
            if (answer != SignedSaveAnswer.Save) return;
        }
        Saving = true;
        string? message = null;
        try
        {
            // Protection and permissions stay (as with normal saving)
            var options = Options(settings);
            var (source, items) = Source();
            var result = await Task.Run(() => PdfFileSaver.Save(source, items, path, options, _closing.Token));
            message = Tr.T("Komprimiert gespeichert: {0} ({1})", Path.GetFileName(path),
                Before is { } b ? Tr.T("{0} → {1}", DocumentViewModel.FormatSize(b.Total), DocumentViewModel.FormatSize(result.Length)) : DocumentViewModel.FormatSize(result.Length));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Problem = Tr.T("Speichern fehlgeschlagen: {0}", e.Message);
        }
        finally
        {
            Saving = false;
        }
        // Report only after saving: while it runs, the window cannot be closed
        if (message is not null) Finished?.Invoke(message);
    }

    /// <summary>For tests: whether the trial run has finished.</summary>
    internal bool IsReady => !Busy && After is not null && AfterImage is not null;

    /// <summary>After <see cref="Dispose"/>: completes when everything has been released (tests; saving over the opened file waits for it).</summary>
    internal Task Released { get; private set; } = Task.CompletedTask;

    /// <summary>Cancels running work and releases images, results and readers in the background (see <see cref="Released"/>).</summary>
    public void Dispose()
    {
        _closing.Cancel();
        _rebuild?.Cancel();
        SetImage(null, null);
        SetRegion(null);
        Released = Task.Run(async () =>
        {
            await _start;   // catches its own exceptions; ends on cancellation
            // A running trial run ends on cancellation (wait for it first, as it needs the lock once more); after that nothing reads any more
            while (_rebuild is not null) await Task.Delay(20);
            await _running;   // also an older, cancelled run still inside the writer
            await _gate.WaitAsync();
            _result?.Dispose();
            _resultBuffer?.Dispose();
            _asImages?.Dispose();
            (_result, _resultBuffer, _asImages, _beforeList, _afterList) = (null, null, null, null, null);
            _images.Clear();
            _images.Detach();
            foreach (var use in _uses) use.Dispose();
            // Anyone still waiting afterwards (rendering, region) sees the cancellation and ends
            _gate.Release();
        });
    }
}

/// <summary>A row of the size preview.</summary>
public sealed record SizeRow(string Label, long Before, long? After)
{
    public string BeforeText => DocumentViewModel.FormatSize(Before);
    public string AfterText => After is { } a ? DocumentViewModel.FormatSize(a) : "…";
    public string ChangeText => After is { } a ? CompressViewModel.Percent(Before, a) : "";
}

/// <summary>A redrawn region (points, origin top left) with both images.</summary>
public sealed record ZoomRegion(double Left, double Top, double Width, double Height, SKImage Before, SKImage After);
