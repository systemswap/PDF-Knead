using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;

namespace PdfKnead.ViewModels;

/// <summary>Images of the page content chosen with „Bilder bearbeiten“: the page and the operators of the images (one picture can be many tiles).</summary>
/// <param name="Page">Page index.</param>
/// <param name="Keys">The images and drawings (see <see cref="ContentObject.Key"/>).</param>
public sealed record ImageSelection(int Page, IReadOnlyList<ContentKey> Keys);

/// <summary>Objects copied with „Bilder bearbeiten“ as one vector object (B7).</summary>
/// <param name="Source">The document the form refers to.</param>
/// <param name="Form">The form XObject (<see cref="ContentObjectEditor.Copy"/>).</param>
/// <param name="Standalone">The same without references, for pasting into another document (<see cref="ObjectCopy.Standalone"/>).</param>
/// <param name="Box">Where the objects lay (page space).</param>
/// <param name="Page">The page they came from (index).</param>
public sealed record CopiedObjects(Core.Pdf.Document.PdfDocument Source, PdfStream Form, PdfStream Standalone, RectD Box, int Page);

/// <summary>
/// „Bilder bearbeiten“ (see KONZEPT.md, section 5.20): images drawn by the page content are chosen with a click — a picture cut into tiles
/// as a whole, with Alt a single tile — and moved or resized; each change is a step of the history (the page with a new content).
/// </summary>
public sealed partial class DocumentViewModel
{
    private const double PointsPerMillimetre = 72 / 25.4;

    // The objects of a page content, found in the background: per page before its edits (moving does not change it), the latest few pages
    private readonly Lock _objectGate = new();
    private readonly Dictionary<PdfDictionary, (Task<PageObjects> Task, CancellationTokenSource Cancel)> _objectTasks = new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<PdfDictionary> _objectOrder = new();
    private CancellationTokenSource _objectCancel = new();
    // per state of a page entry (page dictionary): those objects with its edits
    private readonly ConditionalWeakTable<PdfDictionary, PageObjects> _objectViews = [];

    /// <summary>Pages whose objects are kept (a plan's take tens of megabytes).</summary>
    private const int MaxObjectPages = 4;

    /// <summary>Raised when the objects of a page have been found (the view draws their frames).</summary>
    public event Action? ContentObjectsReady;

    /// <summary>The chosen images; null if none.</summary>
    public ImageSelection? SelectedImages
    {
        get;
        set
        {
            if (Equals(field, value)) return;
            field = value;
            _turnPivot = null;
            IsCroppingImages = false;
            NotifyImageSelection();
            if (value is not null) EditTarget = EditTarget.Image;
            else if (EditTarget == EditTarget.Image) EditTarget = EditTarget.None;
        }
    }

    /// <summary>Raised when the chosen images or their place changed (the view redraws the frame).</summary>
    public event Action? ImageSelectionChanged;

    /// <summary>The images and drawings of page <paramref name="page"/> as they are now; starts finding them in the background if necessary.</summary>
    /// <param name="page">Page index.</param>
    /// <returns>The objects; null while they are being found (<see cref="ContentObjectsReady"/> follows); none outside the document.</returns>
    public PageObjects? ContentObjectsOn(int page)
    {
        if ((uint)page >= (uint)History.Count) return PageObjects.Empty;
        var item = History.Pages[page];
        if (_objectViews.TryGetValue(item.Page.Dictionary, out var known)) return known;
        var task = ObjectsTask(item);
        return task.IsCompletedSuccessfully ? View(item, task.Result) : null;
    }

    /// <summary>The same, waiting for them (a click, a change of the page): found here if not yet started.</summary>
    /// <param name="page">Page index.</param>
    /// <returns>The objects; none outside the document or if finding them was cancelled.</returns>
    public PageObjects ContentObjectsNow(int page)
    {
        if (ContentObjectsOn(page) is { } ready) return ready;
        var item = History.Pages[page];
        try
        {
            return View(item, ObjectsTask(item).GetAwaiter().GetResult());
        }
        catch (OperationCanceledException)
        {
            return PageObjects.Empty;
        }
    }

    /// <summary>The same, without blocking.</summary>
    /// <param name="page">Page index.</param>
    /// <returns>The objects; none outside the document or if finding them was cancelled.</returns>
    public async Task<PageObjects> ContentObjectsOnAsync(int page)
    {
        if (ContentObjectsOn(page) is { } ready) return ready;
        var item = History.Pages[page];
        try
        {
            return View(item, await ObjectsTask(item));
        }
        catch (OperationCanceledException)
        {
            return PageObjects.Empty;
        }
    }

    private PageObjects View(PageItem item, PageObjects found)
    {
        var view = found.WithEdits(item.ObjectEdits);
        _objectViews.AddOrUpdate(item.Page.Dictionary, view);
        return view;
    }

    /// <summary>Finding the objects of the page <paramref name="item"/> is based on: done, running, or started now.</summary>
    private Task<PageObjects> ObjectsTask(PageItem item)
    {
        var key = ContentObjectEditor.BaseOf(item).Dictionary;
        lock (_objectGate)
        {
            if (_objectTasks.TryGetValue(key, out var known))
            {
                _objectOrder.Remove(key);
                _objectOrder.AddFirst(key);
                return known.Task;
            }
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(_objectCancel.Token);
            var token = cancel.Token;
            var task = Task.Run(() =>
            {
                try
                {
                    using var use = Renderer.Use(item.Source);
                    return use is null ? PageObjects.Empty : ContentObjects.Find(item.Source, ContentObjectEditor.BaseOf(item), token);
                }
                catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
                {
                    Core.Platform.AppLog.Write("Bilder der Seite", e.ToString());   // a damaged page offers no objects; everything else carries on
                    return PageObjects.Empty;
                }
            }, CancellationToken.None);
            _ = task.ContinueWith(done =>
            {
                if (!done.IsCompletedSuccessfully) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (done.Result.Incomplete)
                        Notify(Tr.T("Diese Seite hat mehr Teile, als der Speicher fassen kann — die zuletzt gezeichneten lassen sich nicht wählen."));
                    ContentObjectsReady?.Invoke();
                    ImageEditHistoryChanged();
                });
            }, TaskScheduler.Default);
            _objectTasks[key] = (task, cancel);
            _objectOrder.AddFirst(key);
            while (_objectOrder.Count > MaxObjectPages && _objectOrder.Last is { } oldest)
            {
                if (_objectTasks.Remove(oldest.Value, out var gone)) gone.Cancel.Cancel();
                _objectOrder.RemoveLast();
            }
            return task;
        }
    }

    /// <summary>Forgets the objects found (the tool is off: a plan's take tens of megabytes) and cancels finding them.</summary>
    private void ResetContentObjects()
    {
        lock (_objectGate)
        {
            _objectCancel.Cancel();
            _objectCancel.Dispose();
            _objectCancel = new CancellationTokenSource();
            _objectTasks.Clear();
            _objectOrder.Clear();
        }
        _objectViews.Clear();
    }

    /// <summary>
    /// The image or drawing under the point (page space), by its real shape — a diagonal line is hit on the line, not anywhere in its
    /// box —, as an index into <see cref="ContentObjectsOn"/>; of several, the smallest (the title over a picture, the photo over a
    /// background); null if none or while the objects are being found.
    /// </summary>
    /// <param name="page">Page index.</param>
    /// <param name="x">x in page space.</param>
    /// <param name="y">y in page space.</param>
    /// <param name="tolerance">Allowance in page units (the view gives a few screen pixels).</param>
    /// <returns>The index, or null.</returns>
    public int? ContentObjectAt(int page, double x, double y, double tolerance = 0.5) => ContentObjectsOn(page)?.At(x, y, tolerance);

    /// <summary>
    /// Chooses the picture or graphic under the point — all its parts, or with <paramref name="single"/> only the part; with
    /// <paramref name="extend"/> it is added to the chosen ones on the same page, or taken out if already chosen.
    /// </summary>
    /// <param name="page">Page index.</param>
    /// <param name="x">x in page space.</param>
    /// <param name="y">y in page space.</param>
    /// <param name="single">Only the part under the point (Alt).</param>
    /// <param name="extend">Add to or take out of the chosen ones (Ctrl, as in other programs; decided on 29.10.2026, before it was Shift).</param>
    /// <param name="tolerance">Allowance in page units.</param>
    /// <returns>Whether something was there.</returns>
    public bool SelectImageAt(int page, double x, double y, bool single, bool extend = false, double tolerance = 0.5)
    {
        var objects = ContentObjectsNow(page);
        if (objects.At(x, y, tolerance) is not { } index)
        {
            if (!extend) SelectedImages = null;
            return false;
        }
        var keys = (single ? [index] : objects.GroupOf(index)).Select(objects.KeyOf).ToList();
        if (extend && SelectedImages is { } chosen && chosen.Page == page)
        {
            var now = chosen.Keys.Contains(objects.KeyOf(index)) ? chosen.Keys.Except(keys).ToList() : chosen.Keys.Union(keys).ToList();
            SelectedImages = now.Count == 0 ? null : new ImageSelection(page, now);
            return true;
        }
        SelectedImages = new ImageSelection(page, keys);
        return true;
    }

    /// <summary>
    /// Chooses every picture part and graphic that lies wholly inside <paramref name="area"/> (Shift + dragging a frame, B7); with
    /// <paramref name="extend"/> (Ctrl) they are added to the chosen ones on the same page.
    /// </summary>
    /// <param name="page">Page index.</param>
    /// <param name="area">The frame in page space.</param>
    /// <param name="extend">Add to the chosen ones.</param>
    /// <returns>How many objects lie inside.</returns>
    public int SelectImagesIn(int page, RectD area, bool extend = false)
    {
        var objects = ContentObjectsNow(page);
        var inside = new List<ContentKey>();
        for (var i = 0; i < objects.Count; i++)
            if (objects.VisibleOf(i) is { IsEmpty: false } v && v.Left >= area.Left && v.Right <= area.Right && v.Top >= area.Top && v.Bottom <= area.Bottom)
                inside.Add(objects.KeyOf(i));
        var keys = extend && SelectedImages is { } chosen && chosen.Page == page ? chosen.Keys.Union(inside).ToList() : inside;
        SelectedImages = keys.Count == 0 ? null : new ImageSelection(page, keys);
        return inside.Count;
    }

    /// <summary>Only images are chosen (no drawings): they can be cut, copied and saved as images.</summary>
    public bool SelectedAreImages => SelectedImageList is { Count: > 0 } list && list.All(i => !i.IsDrawing);

    /// <summary>The images forming one picture with image <paramref name="index"/> of the page (the group it was edited with, or its tiles).</summary>
    /// <param name="page">Page index.</param>
    /// <param name="index">Index into <see cref="ContentObjectsOn"/>.</param>
    /// <returns>Indices into <see cref="ContentObjectsOn"/>; none while the objects are being found.</returns>
    public IReadOnlyList<int> ContentObjectGroup(int page, int index) => ContentObjectsOn(page)?.GroupOf(index) ?? [];

    /// <summary>The chosen images as they are now (empty if none, or while the objects of their page are being found).</summary>
    public IReadOnlyList<ContentObject> SelectedImageList =>
        SelectedImages is { } s && ContentObjectsOn(s.Page) is { } objects ? objects.Of(s.Keys) : [];

    /// <summary>The box around the visible parts of the chosen images (page space); null if none.</summary>
    public RectD? SelectedImagesBox
    {
        get
        {
            var list = SelectedImageList;
            return list.Count == 0 ? null : list.Select(i => i.Visible).Aggregate((a, b) => a.Union(b));
        }
    }

    /// <summary>Transforms the chosen images in page space (one step; with <paramref name="continueLast"/> joined with the last one).</summary>
    /// <param name="transform">The transformation in page space.</param>
    /// <param name="description">Translated description for undo/redo.</param>
    /// <param name="continueLast">Joins the last step if it changed these images with the same description (arrow keys).</param>
    /// <returns>Whether the page changed (on a very large page: whether the step was started, see <see cref="EditStep"/>).</returns>
    public bool TransformSelectedImages(Matrix transform, string description, bool continueLast = false) => WhenFree(null, () =>
    {
        if (SelectedImages is not { } selection || (uint)selection.Page >= (uint)History.Count) return false;
        if (!CanModifyContent)
        {
            Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
            return false;
        }
        return EditStep(selection, (item, found) => ContentObjectEditor.Transform(item, selection.Keys, transform, found), description, continueLast);
    });

    /// <summary>Deletes the chosen images: cut out of the page content, their data leave the file if no other page draws them.</summary>
    /// <returns>Whether the page changed (on a very large page: whether the step was started).</returns>
    public bool DeleteSelectedImages() => WhenFree(null, () =>
    {
        if (SelectedImages is not { } selection || (uint)selection.Page >= (uint)History.Count) return false;
        if (!CanModifyContent)
        {
            Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
            return false;
        }
        return EditStep(selection, (item, found) => ContentObjectEditor.Delete(item, selection.Keys, found), Tr.T("Bild löschen"), false, () =>
        {
            if (ReferenceEquals(SelectedImages, selection)) SelectedImages = null;
        });
    });

    /// <summary>
    /// Pages with this many objects or more (a plan) are edited in the background: a step takes a moment there (an A0 plan with 780,000
    /// objects: 0.4 s, docs/FEHLERGEDAECHTNIS.md, No. 255), and the window must not stand still meanwhile.
    /// </summary>
    internal const int BackgroundObjects = 100_000;

    // The step running in the background on such a page, and what is asked for meanwhile: done after it, one after the other, each on the
    // state the one before left. Arrow keys pressed meanwhile become one move; of values typed or set with the slider, the last one counts
    private Task? _runningStep;
    private readonly List<WaitingStep> _waitingSteps = [];

    private sealed class WaitingStep(string? kind, Func<WaitingStep, bool> run)
    {
        public string? Kind { get; } = kind;   // the same kind waiting right before: replaced (null: never)
        public Func<WaitingStep, bool> Run { get; set; } = run;
        public double Dx { get; set; }   // arrow keys: the moves joined
        public double Dy { get; set; }
    }

    /// <summary>Whether a step on a very large page is running in the background.</summary>
    public bool ImageStepRunning => _runningStep is not null;

    /// <summary>Does <paramref name="step"/> now, or after the step running in the background (one of the same kind waiting last is replaced).</summary>
    private bool WhenFree(string? kind, Func<bool> step)
    {
        if (_runningStep is null) return step();
        if (kind is not null && _waitingSteps is [.., var last] && last.Kind == kind) last.Run = _ => step();
        else _waitingSteps.Add(new WaitingStep(kind, _ => step()));
        return true;
    }

    /// <summary>
    /// Applies <paramref name="edit"/> to the page of <paramref name="selection"/> as one step of the history: right away, or on a page with
    /// very many objects in the background (<see cref="BackgroundObjects"/>) — then true says only that the step was started.
    /// </summary>
    /// <param name="applied">Done right before the new page goes into the history.</param>
    private bool EditStep(ImageSelection selection, Func<PageItem, PageObjects, PageItem> edit, string description, bool continueLast, Action? applied = null)
    {
        var item = History.Pages[selection.Page];
        var found = ContentObjectsNow(selection.Page);
        if (found.Count < BackgroundObjects)
        {
            var (now, error) = Edited(item, found, edit);
            if (error is not null) ReportEditError(error);
            if (now is null || ReferenceEquals(now, item)) return false;
            applied?.Invoke();
            return History.ReplacePage(selection.Page, item, now, description, continueLast);
        }
        var task = Task.Run(() => Edited(item, found, edit));
        _runningStep = task;
        OnPropertyChanged(nameof(ImageStepRunning));
        _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            _runningStep = null;
            if (_closed) return;
            var (now, error) = task.Result;
            if (error is not null) ReportEditError(error);
            var current = (uint)selection.Page < (uint)History.Count && ReferenceEquals(History.Pages[selection.Page], item);
            if (current && now is not null && !ReferenceEquals(now, item))
            {
                applied?.Invoke();
                History.ReplacePage(selection.Page, item, now, description, continueLast);
            }
            // failed, or the page changed meanwhile (undo, another edit): what waited was meant for the state before
            if (error is not null || !current) _waitingSteps.Clear();
            while (_runningStep is null && _waitingSteps.Count > 0)
            {
                var next = _waitingSteps[0];
                _waitingSteps.RemoveAt(0);
                next.Run(next);
            }
            if (_runningStep is null) OnPropertyChanged(nameof(ImageStepRunning));
        }), TaskScheduler.Default);
        return true;
    }

    /// <summary>The page entry edited (null if the document was closed meanwhile), or the error that stopped it.</summary>
    private (PageItem? Item, Exception? Error) Edited(PageItem item, PageObjects found, Func<PageItem, PageObjects, PageItem> edit)
    {
        try
        {
            using var use = Renderer.Use(item.Source);
            return use is null ? (null, null) : (edit(item, found), null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return (null, e);
        }
    }

    private void ReportEditError(Exception e)
    {
        Core.Platform.AppLog.Write("Bild bearbeiten", e.ToString());
        Notify(Tr.T("Dieses Bild lässt sich nicht bearbeiten: {0}", e.Message));
    }

    // Where the chosen images turn about: the centre of their box when turning began — fixed, so that turning step by step (the slider)
    // does not wander as the box of the turned picture changes; moving or resizing sets a new one
    private (ImageSelection Selection, double X, double Y)? _turnPivot;

    /// <summary>Turns the chosen images about the centre of their box (fixed while they are only turned).</summary>
    /// <param name="degrees">Angle; positive turns anticlockwise (as seen on the page), negative clockwise.</param>
    /// <param name="continueLast">Joins the last step if it turned these images too (the slider: one undo step).</param>
    /// <returns>Whether the page changed.</returns>
    public bool TurnSelectedImages(double degrees, bool continueLast = false) => WhenFree(null, () =>
    {
        if (SelectedImages is not { } selection || SelectedImagesBox is not { } box || !double.IsFinite(degrees) || Math.Abs(degrees % 360) < 1e-9) return false;
        if (_turnPivot is not { } pivot || !ReferenceEquals(pivot.Selection, selection))
            _turnPivot = pivot = (selection, (box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2);
        var turn = Matrix.Translate(-pivot.X, -pivot.Y) * Matrix.Rotate(degrees) * Matrix.Translate(pivot.X, pivot.Y);
        return TransformSelectedImages(turn, Tr.T("Bild drehen"), continueLast);
    });

    /// <summary>
    /// Replaces the chosen picture by <paramref name="image"/>: fitted into its box with its own aspect ratio, saved as inserted images are
    /// (at most 300 dpi at that size, reduced with Lanczos — in the background). The new image is chosen afterwards.
    /// </summary>
    /// <param name="image">The new pixels.</param>
    /// <returns>Whether the page changed.</returns>
    public async Task<bool> ReplaceSelectedImagesAsync(Core.Imaging.RgbaImage image)
    {
        if (SelectedImages is not { } selection || SelectedImagesBox is not { } box || (uint)selection.Page >= (uint)History.Count) return false;
        if (!CanModifyContent)
        {
            Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
            return false;
        }
        var item = History.Pages[selection.Page];
        var found = await ContentObjectsOnAsync(selection.Page);
        var fitted = ContentObjectEditor.FittedBox(box, image.Width, image.Height);
        var element = new ImageElement { Rect = new Core.Pdf.Document.PdfRectangle(fitted.Left, fitted.Top, fitted.Right, fitted.Bottom), Image = image };
        var stream = await Task.Run(() => ElementAppearance.SavedImageStream(element));
        // changed in the meantime (undo, another edit): do not apply to a different state
        if (!ReferenceEquals(SelectedImages, selection) || (uint)selection.Page >= (uint)History.Count || !ReferenceEquals(History.Pages[selection.Page], item)) return false;
        PageItem replaced;
        using (var use = Renderer.Use(item.Source))
        {
            if (use is null) return false;
            replaced = ContentObjectEditor.Replace(item, selection.Keys, stream, found);
        }
        if (ReferenceEquals(replaced, item) || !History.ReplacePage(selection.Page, item, replaced, Tr.T("Bild ersetzen"))) return false;
        // the page is based on the new state now: its objects are found anew
        var now = await ContentObjectsOnAsync(selection.Page);
        if (!ReferenceEquals(History.Pages[selection.Page], replaced)) return true;
        SelectedImages = now.Images.Select(i => now[i]).FirstOrDefault(i => ReferenceEquals(i.Source?.Stream, stream)) is { } added
            ? new ImageSelection(selection.Page, [added.Key]) : null;
        return true;
    }

    /// <summary>
    /// „Zuschneiden“ on: a frame over the chosen picture marks what stays — the handles make it smaller, dragging moves it, the whole picture
    /// stays visible around it (darkened). Nothing is cut until <see cref="ApplyCropAsync"/>; switched off, the frame is dropped.
    /// </summary>
    public bool IsCroppingImages
    {
        get;
        set
        {
            if (value && SelectedImagesBox is null) value = false;
            if (field == value) return;
            field = value;
            CropFrame = value ? SelectedImagesBox : null;
            OnPropertyChanged();
            ImageSelectionChanged?.Invoke();
        }
    }

    /// <summary>What stays when cutting (page space, within the box of the chosen picture); null when not cutting.</summary>
    public RectD? CropFrame
    {
        get;
        set
        {
            if (Nullable.Equals(field, value)) return;
            field = value is { } frame && SelectedImagesBox is { } box ? frame.Intersect(box) : value;
            ImageSelectionChanged?.Invoke();
        }
    }

    /// <summary>Cuts the chosen picture to <see cref="CropFrame"/> and ends cutting.</summary>
    /// <returns>Whether the page changed.</returns>
    public async Task<bool> ApplyCropAsync()
    {
        if (CropFrame is not { } frame) return false;
        var done = await CropSelectedImagesAsync(frame);
        IsCroppingImages = false;
        return done;
    }

    /// <summary>„Zuschneiden anwenden“ in the panel (Enter does the same).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task ApplyCrop() => ApplyCropAsync();

    /// <summary>
    /// Cuts the chosen picture to <paramref name="keep"/> (page space, within its box): the pixels outside leave the file (see
    /// <see cref="ContentObjectEditor.Crop"/>), in the background. The picture stays chosen.
    /// </summary>
    /// <param name="keep">The area to keep.</param>
    /// <returns>Whether the page changed.</returns>
    public async Task<bool> CropSelectedImagesAsync(RectD keep)
    {
        if (SelectedImages is not { } selection || SelectedImagesBox is not { } box || (uint)selection.Page >= (uint)History.Count) return false;
        if (!CanModifyContent)
        {
            Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
            return false;
        }
        keep = keep.Intersect(box);
        if (keep.IsEmpty || keep.Width < 1 || keep.Height < 1) return false;
        var item = History.Pages[selection.Page];
        var found = await ContentObjectsOnAsync(selection.Page);
        (PageItem Item, IReadOnlyList<Core.Pdf.Objects.PdfStream> Images) cropped;
        try
        {
            cropped = await Task.Run(() =>
            {
                using var use = Renderer.Use(item.Source);
                return use is null ? (item, []) : ContentObjectEditor.Crop(item, selection.Keys, keep, SkiaImageCodec.Instance, found);
            });
        }
        catch (Core.Pdf.PdfException e)
        {
            Notify(e.Message);
            return false;
        }
        // changed in the meantime (undo, another edit): do not apply to a different state
        if (ReferenceEquals(cropped.Item, item) || !ReferenceEquals(SelectedImages, selection) || (uint)selection.Page >= (uint)History.Count ||
            !History.ReplacePage(selection.Page, item, cropped.Item, Tr.T("Bild zuschneiden")))
            return false;
        // the picture stays chosen: its tiles, now new images of the page (found anew: the page is based on the new state)
        var now = await ContentObjectsOnAsync(selection.Page);
        if (!ReferenceEquals(History.Pages[selection.Page], cropped.Item)) return true;
        var first = now.Images.Select(i => (image: now[i], i)).FirstOrDefault(e => cropped.Images.Any(c => ReferenceEquals(c, e.image.Source?.Stream)) ||
            e.image.Visible.Intersect(keep) is { IsEmpty: false } v && v.Width > 1 && v.Height > 1);
        SelectedImages = first.image is null ? null : new ImageSelection(selection.Page, [.. now.GroupOf(first.i).Select(now.KeyOf)]);
        return true;
    }

    /// <summary>Turns the chosen images 90° anticlockwise.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void TurnImagesLeft() => TurnSelectedImages(90);

    /// <summary>Turns the chosen images 90° clockwise.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void TurnImagesRight() => TurnSelectedImages(-90);

    /// <summary>Deletes the chosen images (button in the panel).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void DeleteImages() => DeleteSelectedImages();

    /// <summary>
    /// Angle of the chosen picture on the page in degrees as people read it: 0 = upright, positive clockwise (to the right), negative
    /// anticlockwise, −180 … 180; typing a value turns it to that angle.
    /// </summary>
    public string ImageAngle
    {
        get => SelectedImageList is [var first, ..] ? Shown(Angle(first)).ToString("0.#", CultureInfo.CurrentCulture) : "";
        set
        {
            if (double.TryParse((value ?? "").Replace("°", "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var wanted))
                TurnTo(wanted, continueLast: false);
        }
    }

    /// <summary>The same angle for the slider (0 in the middle, 180° to either side); moving it turns the picture in one undo step.</summary>
    public double ImageAngleDegrees
    {
        get => SelectedImageList is [var first, ..] ? Shown(Angle(first)) : 0;
        set => TurnTo(value, continueLast: true);
    }

    /// <summary>Turns the chosen picture to <paramref name="wanted"/> degrees clockwise (the shortest way); less than a twentieth of a degree is no turn.</summary>
    private void TurnTo(double wanted, bool continueLast) => WhenFree("Winkel", () =>
    {
        if (SelectedImageList is not [var first, ..] || !double.IsFinite(wanted)) return false;
        var turn = Angle(first) - wanted;   // clockwise as read: the page turns the other way round
        turn = (turn % 360 + 540) % 360 - 180;
        return Math.Abs(turn) >= 0.05 && TurnSelectedImages(turn, continueLast);
    });

    /// <summary>Clockwise angle of an image's x axis on the page in degrees (not rounded: turning step by step must not add up roundings).</summary>
    private static double Angle(ContentObject image)
    {
        var (x, y) = image.ImageToPage.TransformVector(1, 0);
        return -Math.Atan2(y, x) * 180 / Math.PI;
    }

    /// <summary>An angle as shown: −180 … 180 (−180 shown as 180), to a tenth of a degree.</summary>
    private static double Shown(double degrees)
    {
        var shown = Math.Round((degrees % 360 + 540) % 360 - 180, 1);
        return shown <= -180 ? 180 : shown;
    }

    /// <summary>Moves the chosen images by (dx, dy) points in page space.</summary>
    /// <param name="dx">Shift to the right.</param>
    /// <param name="dy">Shift upwards.</param>
    /// <param name="continueLast">Joins the last step (arrow keys).</param>
    /// <returns>Whether the page changed.</returns>
    public bool MoveSelectedImages(double dx, double dy, bool continueLast = false)
    {
        if (dx == 0 && dy == 0) return false;
        if (_runningStep is not null && continueLast)
        {
            // arrow keys while a step runs: joined into one move after it
            if (_waitingSteps is not [.., { Kind: "Pfeile" } last])
                _waitingSteps.Add(last = new WaitingStep("Pfeile", w => MoveSelectedImages(w.Dx, w.Dy, continueLast: true)));
            (last.Dx, last.Dy) = (last.Dx + dx, last.Dy + dy);
            return true;
        }
        return WhenFree(null, () =>
        {
            _turnPivot = null;
            return TransformSelectedImages(Matrix.Translate(dx, dy), Tr.T("Bild verschieben"), continueLast);
        });
    }

    /// <summary>Fits the box of the chosen images into <paramref name="target"/> (page space): moves and scales them together.</summary>
    /// <param name="target">The new box.</param>
    /// <returns>Whether the page changed.</returns>
    public bool FitSelectedImages(RectD target) => WhenFree("Größe", () =>
    {
        if (SelectedImagesBox is not { } box || box.Width <= 1e-6 || box.Height <= 1e-6 || target.Width < 1 || target.Height < 1) return false;
        _turnPivot = null;
        var transform = Matrix.Translate(-box.Left, -box.Top) * Matrix.Scale(target.Width / box.Width, target.Height / box.Height) * Matrix.Translate(target.Left, target.Top);
        return TransformSelectedImages(transform, Tr.T("Bildgröße ändern"));
    });

    // ------------------------------------------------------------ Copying and saving

    /// <summary>Largest picture put together from tiles for saving (64 megapixels, 256 MB in memory); larger ones are reduced evenly.</summary>
    private const long MaxComposedPixels = 64_000_000;

    /// <summary>
    /// The chosen images where the page content places them (not moved): put together from there, tiles meet exactly as the page drew them.
    /// </summary>
    private (PageItem Item, IReadOnlyList<ContentObject> Images)? ImagesForExport()
    {
        if (SelectedImages is not { } s || (uint)s.Page >= (uint)History.Count) return null;
        var item = History.Pages[s.Page];
        var images = ContentObjectsNow(s.Page).Original.Of(s.Keys);
        return images.Count == 0 ? null : (item, images);
    }

    /// <summary>What saving the chosen images will write: one image in its original quality, a picture of several tiles as PNG.</summary>
    /// <returns>The plan; null if nothing is chosen or the document was closed.</returns>
    /// <exception cref="Core.Pdf.PdfException">The image cannot be saved.</exception>
    public Task<ImageExportPlan?> PlanSelectedImagesAsync()
    {
        if (ImagesForExport() is not var (item, images)) return Task.FromResult<ImageExportPlan?>(null);
        if (images.Count > 1) return Task.FromResult<ImageExportPlan?>(new ImageExportPlan(ImageExportFormat.Png, ".png", false, false, false));
        return Task.Run(() =>
        {
            using var use = Renderer.Use(item.Source);
            return use is null || images[0].Source is not { } source ? null : ImageExport.Plan(item.Source, source);
        });
    }

    /// <summary>
    /// Saves the chosen images: one image as „Bildinfo“ does (original quality), a picture of several tiles put together as PNG without
    /// loss. In the background, through a temporary file.
    /// </summary>
    /// <param name="path">The image file.</param>
    /// <param name="maskPath">The file for an opacity that does not fit into the image file.</param>
    /// <returns>What was written and whether the mask file was written; null if nothing was written.</returns>
    /// <exception cref="Core.Pdf.PdfException">The image data cannot be read.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public Task<(ImageExportPlan Plan, bool Mask)?> SaveSelectedImagesAsync(string path, string maskPath)
    {
        if (ImagesForExport() is not var (item, images)) return Task.FromResult<(ImageExportPlan, bool)?>(null);
        return Task.Run(() => WriteImageFile(path, maskPath, (output, openMask) =>
        {
            using var use = Renderer.Use(item.Source);
            if (use is null) return null;
            if (images.Count == 1)
                return images[0].Source is { } source ? ImageExport.Write(item.Source, source, output, openMask, ImageExport.DrawnSize(images[0].ImageToPage)) : null;
            if (ContentImageExport.Compose(item.Source, images, MaxComposedPixels) is not var (width, height, rgba))
                throw new Core.Pdf.PdfException(Tr.T("Das Bild lässt sich nicht lesen."));
            var exported = ImageExport.Export(new Core.Imaging.RgbaImage(width, height, rgba));
            output.Write(exported.Data);
            return new ImageExportPlan(exported.Format, exported.Extension, false, false, false);
        }));
    }

    /// <summary>The chosen images as drawn, for the clipboard (RGBA without premultiplied alpha; a picture of tiles put together, at most 16 megapixels).</summary>
    /// <returns>Width, height and pixels; null if nothing can be read.</returns>
    public Task<(int Width, int Height, byte[] Rgba)?> SelectedImagesPixelsAsync()
    {
        if (ImagesForExport() is not var (item, images)) return Task.FromResult<(int, int, byte[])?>(null);
        return Task.Run<(int, int, byte[])?>(() =>
        {
            using var use = Renderer.Use(item.Source);
            if (use is null) return null;
            if (images.Count == 1)
                return images[0].Source is { } source && PdfImageDecoder.Decode(source, item.Source) is { } decoded
                    ? (decoded.Width, decoded.Height, decoded.Rgba) : null;
            return ContentImageExport.Compose(item.Source, images, PdfImageDecoder.MaxFullPixels);
        });
    }

    /// <summary>
    /// Copies the chosen objects as one object, as a vector (B7: lines stay sharp at any size, images keep their quality), in the background —
    /// with a picture of them for other programs (none if only images are chosen: then their own pixels go to the clipboard).
    /// </summary>
    /// <returns>The copy and the picture; null if nothing is chosen, the document was closed or the objects cover no area.</returns>
    /// <exception cref="Core.Pdf.PdfException">The objects refer to too much.</exception>
    public Task<(CopiedObjects Copy, (int Width, int Height, byte[] Rgba)? Picture)?> CopySelectedObjectsAsync()
    {
        if (SelectedImages is not { } s || (uint)s.Page >= (uint)History.Count) return Task.FromResult<(CopiedObjects, (int, int, byte[])?)?>(null);
        var item = History.Pages[s.Page];
        var found = ContentObjectsNow(s.Page);
        var picture = !SelectedAreImages;
        return Task.Run<(CopiedObjects, (int, int, byte[])?)?>(() =>
        {
            using var use = Renderer.Use(item.Source);
            if (use is null || ContentObjectEditor.Copy(item, s.Keys, found) is not { } copied) return null;
            var (form, box) = copied;
            var copy = new CopiedObjects(item.Source, form, (PdfStream)ObjectCopy.Standalone(item.Source, form), box, s.Page);
            return (copy, picture ? ContentImageExport.Picture(item.Source, ContentObjectEditor.PageOf(form, box), 2400) : null);
        });
    }

    /// <summary>
    /// Pastes copied objects (B7) as one object on top of the page with the chosen objects, otherwise the current page: on the page they came
    /// from a little offset (so that the copy is seen), elsewhere at the same place — onto the middle of the page if that lies beside it. The
    /// pasted objects are chosen afterwards and can be moved at once.
    /// </summary>
    /// <param name="copied">The copy.</param>
    /// <returns>Whether they were pasted.</returns>
    public async Task<bool> PasteObjectsAsync(CopiedObjects copied)
    {
        var page = SelectedImages?.Page ?? (CurrentPage is { } current ? current.Number - 1 : 0);
        if ((uint)page >= (uint)History.Count) return false;
        if (!CanModifyContent)
        {
            Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
            return false;
        }
        var item = History.Pages[page];
        var same = ReferenceEquals(item.Source, copied.Source);
        var box = copied.Box;
        var (dx, dy) = same && page == copied.Page ? (10.0, -10.0) : (0.0, 0.0);
        var crop = item.Page.CropBox;
        if (box.Right + dx < crop.Left || box.Left + dx > crop.Right || box.Bottom + dy < crop.Bottom || box.Top + dy > crop.Top)
            (dx, dy) = ((crop.Left + crop.Right - box.Left - box.Right) / 2, (crop.Bottom + crop.Top - box.Top - box.Bottom) / 2);
        (PageItem Item, int Op) pasted;
        try
        {
            pasted = await Task.Run(() =>
            {
                using var use = Renderer.Use(item.Source);
                return use is null ? (item, -1) : ContentObjectEditor.Insert(item, same ? copied.Form : copied.Standalone, Matrix.Translate(dx, dy));
            });
        }
        catch (Exception e) when (e is Core.Pdf.PdfException or ArgumentException)
        {
            Notify(e.Message);
            return false;
        }
        if (pasted.Op < 0 || (uint)page >= (uint)History.Count || !History.ReplacePage(page, item, pasted.Item, Tr.T("Objekte einfügen"))) return false;
        // chosen: everything in the new object (found anew: the page is based on the new state)
        var now = await ContentObjectsOnAsync(page);
        if ((uint)page >= (uint)History.Count || !ReferenceEquals(History.Pages[page], pasted.Item)) return true;
        var form = pasted.Op.ToString(CultureInfo.InvariantCulture);
        var keys = Enumerable.Range(0, now.Count).Select(now.KeyOf)
            .Where(k => k.Scope == form || k.Scope.StartsWith(form + "/", StringComparison.Ordinal)).ToList();
        SelectedImages = keys.Count == 0 ? null : new ImageSelection(page, keys);
        return true;
    }

    /// <summary>Why the chosen images may not be copied or saved (rights of the file, a redaction not yet applied over them); null if allowed.</summary>
    /// <returns>The translated reason, or null.</returns>
    public string? SelectedImagesExportBlocked() =>
        SelectedImages is not { } s ? Tr.T("Kein Bild ausgewählt.") : ExportBlocked(s.Page, SelectedImagesBox, ownElement: false);

    /// <summary>Suggested file name for saving the chosen images (without extension).</summary>
    public string SelectedImagesFileName => SelectedImages is { } s
        ? Tr.T("{0} Seite {1} Bild", System.IO.Path.GetFileNameWithoutExtension(FileName), s.Page + 1)
        : System.IO.Path.GetFileNameWithoutExtension(FileName);

    // ------------------------------------------------------------ Panel on the right

    /// <summary>How many tiles the chosen picture consists of (shown when more than one).</summary>
    public string ImagePartsText => SelectedImages is { Keys.Count: > 1 } s
        ? Tr.T("Gewählt sind {0} Teile — sie werden zusammen bearbeitet; Alt+Klick wählt ein einzelnes Teil, Strg+Klick nimmt weitere dazu oder heraus.", s.Keys.Count)
        : "";

    /// <summary>Whether images are chosen (the panel shows their place and size).</summary>
    public bool HasSelectedImages => SelectedImages is not null;

    /// <summary>Chain closed: width and height change together, also at the handles (Shift reverses it while dragging).</summary>
    public bool ImageKeepAspect
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            OnPropertyChanged();
        }
    } = true;

    /// <summary>Distance of the left edge from the left edge of the page, in millimetres.</summary>
    public string ImageLeftMm
    {
        get => SelectedImagesBox is { } b && Page() is { } p ? Mm(b.Left - p.CropBox.Left) : "";
        // from the box as it is when the step is done (a step running in the background moves it first)
        set => WhenFree("Links", () => SelectedImagesBox is { } b && Page() is { } p && ParseMm(value, allowZero: true) is { } mm &&
                                       TransformSelectedImages(Matrix.Translate(p.CropBox.Left + mm * PointsPerMillimetre - b.Left, 0), Tr.T("Bild verschieben")));
    }

    /// <summary>Distance of the upper edge from the upper edge of the page, in millimetres.</summary>
    public string ImageTopMm
    {
        get => SelectedImagesBox is { } b && Page() is { } p ? Mm(p.CropBox.Top - b.Bottom) : "";
        set => WhenFree("Oben", () => SelectedImagesBox is { } b && Page() is { } p && ParseMm(value, allowZero: true) is { } mm &&
                                      TransformSelectedImages(Matrix.Translate(0, p.CropBox.Top - mm * PointsPerMillimetre - b.Bottom), Tr.T("Bild verschieben")));
    }

    /// <summary>Width in millimetres; with the chain closed the height changes too (the upper left corner stays).</summary>
    public string ImageWidthMm
    {
        get => SelectedImagesBox is { } b ? Mm(b.Width) : "";
        set => WhenFree("Breite", () =>
        {
            if (SelectedImagesBox is not { } b || ParseMm(value, allowZero: false) is not { } mm) return false;
            var width = mm * PointsPerMillimetre;
            var height = ImageKeepAspect ? width * b.Height / b.Width : b.Height;
            return FitSelectedImages(RectD.FromLtrb(b.Left, b.Bottom - height, b.Left + width, b.Bottom));
        });
    }

    /// <summary>Height in millimetres; with the chain closed the width changes too (the upper left corner stays).</summary>
    public string ImageHeightMm
    {
        get => SelectedImagesBox is { } b ? Mm(b.Height) : "";
        set => WhenFree("Höhe", () =>
        {
            if (SelectedImagesBox is not { } b || ParseMm(value, allowZero: false) is not { } mm) return false;
            var height = mm * PointsPerMillimetre;
            var width = ImageKeepAspect ? height * b.Width / b.Height : b.Width;
            return FitSelectedImages(RectD.FromLtrb(b.Left, b.Bottom - height, b.Left + width, b.Bottom));
        });
    }

    private Core.Pdf.Document.PdfPage? Page() =>
        SelectedImages is { } s && (uint)s.Page < (uint)History.Count ? History.Pages[s.Page].Page : null;

    private static string Mm(double points) => (points / PointsPerMillimetre).ToString("0.#", CultureInfo.CurrentCulture);

    private static double? ParseMm(string? text, bool allowZero) =>
        double.TryParse((text ?? "").Trim().Replace("mm", "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) &&
        double.IsFinite(v) && Math.Abs(v) < 100_000 && (allowZero ? v >= -100_000 : v > 0) ? v : null;

    private void NotifyImageSelection()
    {
        OnPropertyChanged(nameof(SelectedImages));
        OnPropertyChanged(nameof(HasSelectedImages));
        OnPropertyChanged(nameof(SelectedAreImages));
        OnPropertyChanged(nameof(ImagePartsText));
        OnPropertyChanged(nameof(ImageLeftMm));
        OnPropertyChanged(nameof(ImageTopMm));
        OnPropertyChanged(nameof(ImageWidthMm));
        OnPropertyChanged(nameof(ImageHeightMm));
        OnPropertyChanged(nameof(ImageAngle));
        OnPropertyChanged(nameof(ImageAngleDegrees));
        ImageSelectionChanged?.Invoke();
    }

    // ------------------------------------------------------------ Tool and history

    /// <summary>
    /// After a step of the history (and when the objects of a page are found): the chosen images keep their selection while they exist (undo of
    /// another change), the panel follows their place. While the objects of their page are being found, the selection waits for them.
    /// </summary>
    private void ImageEditHistoryChanged()
    {
        if (SelectedImages is not { } s) return;
        if ((uint)s.Page >= (uint)History.Count) SelectedImages = null;
        else if (ContentObjectsOn(s.Page) is not { } objects) return;
        else if (!s.Keys.All(key => objects.IndexOf(key) is not null)) SelectedImages = null;
        else NotifyImageSelection();
    }
}
