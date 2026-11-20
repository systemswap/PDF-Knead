using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>
/// Sidebar: selection, dragging to reorder (with automatic scrolling at the edge), Del; plus the
/// dialogs for saving.
/// </summary>
public partial class MainWindow
{
    private const double DragThreshold = 6;
    private const double EdgeZone = 56;

    private DocumentViewModel? _observedDoc;
    private bool _syncingSelection;

    // Dragging in the sidebar
    private Point? _pressAt;
    private PageViewModel? _pressed;
    private bool _keepSelectionOnPress;
    private bool _dragging;
    private int _dropIndex = -1;
    private Point _lastPointer;
    private bool _autoScrolling;
    private double _scrollSpeed;

    private void SetUpPageList()
    {
        PageList.AddHandler(PointerPressedEvent, OnPagePressed, RoutingStrategies.Tunnel);
        PageList.AddHandler(PointerMovedEvent, OnPageMoved, RoutingStrategies.Tunnel);
        PageList.AddHandler(PointerReleasedEvent, OnPageReleased, RoutingStrategies.Tunnel);
        PageList.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(apply: false));
        PageList.SelectionChanged += OnPageSelectionChanged;
        PageList.KeyDown += OnPageListKeyDown;
    }

    private void SetUpSaving()
    {
        if (_vm is null) return;
        _vm.AskSaveChanges = async fileName => await new SaveChangesWindow(fileName).ShowDialog<SaveChoice?>(this) ?? SaveChoice.Cancel;
        _vm.ConfirmFixing = async (title, notice, offerImage) =>
            await new FixSignaturesWindow(notice, title, offerImage).ShowDialog<FixingReply?>(this) ?? FixingReply.Cancel;
        _vm.ConfirmRedaction = async question => await new RedactionWindow(question).ShowDialog<RedactionReply?>(this) ?? new RedactionReply(RedactionAnswer.KeepMarking);
        _vm.ConfirmRedactedSave = async question => await new RedactedSaveWindow(question).ShowDialog<RedactedSaveAnswer?>(this) ?? RedactedSaveAnswer.Cancel;
        _vm.ConfirmSignedSave = async question => await new SignedSaveWindow(question).ShowDialog<SignedSaveAnswer?>(this) ?? SignedSaveAnswer.Cancel;
        _vm.PickRedactionHits = async find => await new SearchRedactWindow(find, _vm.SelectedDocument is { } doc ? () => doc.UnreadablePagesAsync() : null,
            _vm.SelectedDocument is { } misreadIn ? () => misreadIn.MisreadPagesAsync() : null,
            _vm.SelectedDocument is { } barsIn ? hits => barsIn.BarsOfAsync(hits) : null)
            .ShowDialog<MainViewModel.RedactionPick?>(this);
        _vm.PickRaster = async () => await new RasterWindow().ShowDialog<PdfKnead.Rendering.RasterSettings?>(this);
        _vm.PickExportRaster = async purpose => await new RasterWindow(purpose).ShowDialog<PdfKnead.Rendering.RasterSettings?>(this);
        _vm.PickPageImages = async (start, all, selected) =>
            await new PageImagesWindow(start, all, selected, document: true).ShowDialog<PdfKnead.Core.Imaging.PageImageSettings?>(this);
        _vm.PickPdfA = async model => await new PdfAWindow(model).ShowDialog<PdfKnead.Core.Pdf.Writing.PdfAResult?>(this);
        _vm.PickFolder = async title =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = title });
            return folders.FirstOrDefault()?.TryGetLocalPath();
        };
        _vm.PickCleanup = async (found, chosen) =>
        {
            var doc = _vm.SelectedDocument;
            try
            {
                return await new CleanupWindow(found, chosen, doc is null ? null : doc.ShowCleanupPlacesAsync).ShowDialog<PdfKnead.Core.Pdf.Writing.CleanupItems?>(this);
            }
            finally
            {
                doc?.ClearCleanupPlaces();
            }
        };
        _vm.PickProtection = async (isProtected, permissions, version) =>
            await new ProtectionWindow(isProtected, permissions, version).ShowDialog<ProtectionChange?>(this);
        _vm.PickRestrictions = async (fileName, locked, hasOpenPassword, isOwnerPassword) =>
            await new RestrictionsWindow(fileName, locked, hasOpenPassword, isOwnerPassword).ShowDialog<RestrictionsReply?>(this);
        _vm.PickSavePath = async (title, name, folder) =>
        {
            var start = folder is { Length: > 0 } ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null;
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = name,
                SuggestedStartLocation = start,
                DefaultExtension = "pdf",
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType(Tr.T("PDF-Dokumente")) { Patterns = ["*.pdf"], MimeTypes = ["application/pdf"] }],
            });
            return file?.TryGetLocalPath();
        };
        _vm.PickReportSavePath = async (title, name, folder) =>
        {
            var start = folder is { Length: > 0 } ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null;
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = name,
                SuggestedStartLocation = start,
                DefaultExtension = "html",
                ShowOverwritePrompt = true,
                FileTypeChoices =
                [
                    new FilePickerFileType(Tr.T("Webseite (HTML)")) { Patterns = ["*.html", "*.htm"], MimeTypes = ["text/html"] },
                    new FilePickerFileType(Tr.T("Text")) { Patterns = ["*.txt"], MimeTypes = ["text/plain"] },
                ],
            });
            return file?.TryGetLocalPath();
        };
        _vm.PickImageSavePath = async (title, name, folder) =>
        {
            var start = folder is { Length: > 0 } ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null;
            var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
            var type = extension switch
            {
                "jpg" => new FilePickerFileType(Tr.T("JPEG-Bilder")) { Patterns = ["*.jpg", "*.jpeg"], MimeTypes = ["image/jpeg"] },
                "jp2" or "j2k" => new FilePickerFileType(Tr.T("JPEG-2000-Bilder")) { Patterns = ["*." + extension], MimeTypes = ["image/" + (extension == "jp2" ? "jp2" : "x-jp2-codestream")] },
                "tif" => new FilePickerFileType(Tr.T("TIFF-Bilder")) { Patterns = ["*.tif", "*.tiff"], MimeTypes = ["image/tiff"] },
                _ => new FilePickerFileType(Tr.T("PNG-Bilder")) { Patterns = ["*.png"], MimeTypes = ["image/png"] },
            };
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = name,
                SuggestedStartLocation = start,
                DefaultExtension = extension,
                ShowOverwritePrompt = true,
                FileTypeChoices = [type],
            });
            return file?.TryGetLocalPath();
        };
        _vm.PickOpenPaths = async title =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType(Tr.T("PDF-Dokumente")) { Patterns = ["*.pdf", "*.PDF"], MimeTypes = ["application/pdf"] }],
            });
            return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
        };
        _vm.ScanProfiles.PickExportPath = async (title, name) =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = name,
                DefaultExtension = "json",
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType(Tr.T("Scanprofile")) { Patterns = ["*.json"], MimeTypes = ["application/json"] }],
            });
            return file?.TryGetLocalPath();
        };
        _vm.ScanProfiles.PickImportPath = async title =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                FileTypeFilter = [new FilePickerFileType(Tr.T("Scanprofile")) { Patterns = ["*.json", "*.JSON"], MimeTypes = ["application/json"] }],
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        };
        _vm.ScanProfiles.Confirm = async (heading, message, answer) =>
            await new ChoiceWindow(new ChoiceRequest(heading, message, [answer], Tr.T("Abbrechen"), Icon: "Icon.Delete")).ShowDialog<ChoiceAnswer>(this) is { Answer: 1 };
        _vm.PickPageSetup = async (setup, insert) => await new NewDocumentWindow(setup, insert).ShowDialog<BlankPagesChoice?>(this);
        _vm.ShowPrint = async print => await new PrintWindow(print).ShowDialog<string?>(this);
        _vm.ShowCompress = async compress => await new CompressWindow(compress).ShowDialog<string?>(this);
        _vm.ShowSplit = async split => await new SplitWindow(split).ShowDialog<bool?>(this) == true;
        _vm.ShowSort = async sort => await new SortWindow(sort).ShowDialog<IReadOnlyList<PdfKnead.Core.Pdf.Editing.PageItem>?>(this);
        _vm.ShowMerge = async merge => await new MergeWindow(merge).ShowDialog<int?>(this);
        _vm.ShowScan = async scan =>
        {
            var window = new ScanWindow(scan);
            var result = await window.ShowDialog<ScanResult?>(this);
            await window.Cleanup;   // the device and the preview documents are released
            return result;
        };
        _vm.ChoosePages = async (document, fileName) =>
        {
            var window = new PageSelectionWindow(document, fileName);
            var chosen = await window.ShowDialog<IReadOnlyList<int>?>(this);
            await window.Cleanup;   // only then may the file be closed
            return chosen;
        };
    }

    // ------------------------------------------------------------ Dragging a PDF into the sidebar = inserting

    /// <summary>Whether a file is being dragged over the sidebar (with an open document whose pages may be changed).</summary>
    private bool OverPageList(DragEventArgs e)
    {
        if (_observedDoc is not { CanEditPages: true } || !PageListPanel.IsEffectivelyVisible) return false;
        var p = e.GetPosition(PageList);
        return p.X >= 0 && p.Y >= 0 && p.X <= PageList.Bounds.Width && p.Y <= PageList.Bounds.Height;
    }

    /// <summary>While dragging over the sidebar: insertion marker instead of “Drop to open”.</summary>
    private bool DragOverPageList(DragEventArgs e)
    {
        if (!OverPageList(e))
        {
            DropMarker.IsVisible = false;
            return false;
        }
        UpdateDrop(e.GetPosition(PageList));
        _scrollSpeed = 0;
        return true;
    }

    private async Task<bool> DropOnPageList(DragEventArgs e, IReadOnlyList<string> paths)
    {
        var over = OverPageList(e);
        DropMarker.IsVisible = false;
        if (!over || _vm is null) return false;
        UpdateDrop(e.GetPosition(PageList));
        DropMarker.IsVisible = false;
        var at = _dropIndex;
        _dropIndex = -1;
        await _vm.InsertFilesAsync(paths, at);
        return true;
    }

    // ------------------------------------------------------------ Closing with unsaved changes

    private bool _closeConfirmed;

    /// <summary>
    /// Commits the focused text box now (panel on the right: bound on focus loss), before saving and closing; otherwise the
    /// typed property would be missing from the file and would change the document only afterwards.
    /// </summary>
    private void CommitFocusedInput()
    {
        if (FocusManager?.GetFocusedElement() is TextBox box)
            Avalonia.Data.BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        CommitFocusedInput();
        if (_vm is not null)
            foreach (var doc in _vm.Documents) doc.CommitPendingEdit();
        // Work still running (saving, export …): wait for it first, then close again; otherwise a half-written temporary file would remain.
        if (!_closeConfirmed && _vm is not null && _vm.Documents.Any(d => d.IsWorking))
        {
            e.Cancel = true;
            _ = CloseWhenIdleAsync();
            return;
        }
        if (_closeConfirmed || _vm is null || !_vm.Documents.Any(d => d.IsModified)) return;
        e.Cancel = true;
        _ = ConfirmCloseAsync();
    }

    private async Task CloseWhenIdleAsync()
    {
        if (_vm is null) return;
        await Task.WhenAll(_vm.Documents.Select(d => d.WhenIdle()));
        Close();
    }

    private async Task ConfirmCloseAsync()
    {
        if (_vm is null || !await _vm.ConfirmCloseAllAsync()) return;
        _closeConfirmed = true;
        Close();
    }

    // ------------------------------------------------------------ Selection and current page

    /// <summary>The selected document changes: observe its current page.</summary>
    private void ObserveDocument(DocumentViewModel? doc)
    {
        if (_observedDoc is not null) _observedDoc.PropertyChanged -= OnObservedDocumentChanged;
        _observedDoc = doc;
        if (doc is not null) doc.PropertyChanged += OnObservedDocumentChanged;
        FollowCurrentPage();
    }

    private void OnObservedDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.CurrentPage)) FollowCurrentPage();
    }

    /// <summary>
    /// The sidebar follows the view: if at most one page is selected, the current page is selected
    /// and scrolled into view. A multiple selection stays (it is meant for the page commands).
    /// </summary>
    private void FollowCurrentPage()
    {
        if (_observedDoc is not { CurrentPage: { } current } doc || _dragging) return;
        var selected = doc.Pages.Count(p => p.IsSelected);
        if (selected > 1) return;
        _syncingSelection = true;
        foreach (var page in doc.Pages) page.IsSelected = ReferenceEquals(page, current);
        _syncingSelection = false;
        // Scroll only after the list has been built: ScrollIntoView in the middle of building it (right after opening)
        // left an orphaned entry visible. See docs/FEHLERGEDAECHTNIS.md, No. 22.
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_observedDoc, doc) || !ReferenceEquals(doc.CurrentPage, current)) return;
            var index = doc.Pages.IndexOf(current);
            if (index >= 0) PageList.ScrollIntoView(index);
        }, DispatcherPriority.Background);
    }

    /// <summary>Click on a single page: jump there.</summary>
    private void OnPageSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || _observedDoc is not { } doc || _dragging) return;
        if (e.AddedItems is [PageViewModel added] && doc.Pages.Count(p => p.IsSelected) == 1)
            doc.CurrentPage = added;
    }

    private void OnPageListKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None)
        {
            _vm.DeletePagesCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------ Dragging to reorder

    private PageViewModel? PageAt(RoutedEventArgs e) =>
        (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as PageViewModel;

    private void OnPagePressed(object? sender, PointerPressedEventArgs e)
    {
        _pressAt = null;
        _keepSelectionOnPress = false;
        if (!e.GetCurrentPoint(PageList).Properties.IsLeftButtonPressed || _observedDoc is not { } doc) return;
        if (PageAt(e) is not { } page) return;
        _pressAt = e.GetPosition(PageList);
        _pressed = page;
        // Click on one of several selected pages: keep the selection so that all of them can be dragged.
        if (page.IsSelected && e.KeyModifiers == KeyModifiers.None && doc.Pages.Count(p => p.IsSelected) > 1)
        {
            _keepSelectionOnPress = true;
            PageList.Focus();
            e.Handled = true;
        }
    }

    private void OnPageMoved(object? sender, PointerEventArgs e)
    {
        if (_pressAt is not { } start || _observedDoc is not { } doc) return;
        var position = e.GetPosition(PageList);
        _lastPointer = position;
        if (!_dragging)
        {
            if (!e.GetCurrentPoint(PageList).Properties.IsLeftButtonPressed)
            {
                _pressAt = null;
                return;
            }
            var delta = position - start;
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) < DragThreshold) return;
            if (!doc.CanEditPages)
            {
                _pressAt = null;
                if (_vm is not null) _vm.Message = Tr.T("Dieses Dokument erlaubt kein Ändern der Seiten (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen.");
                return;
            }
            _dragging = true;
            if (_pressed is { IsSelected: false } pressed) pressed.IsSelected = true;
            e.Pointer.Capture(PageList);
            PageList.Cursor = new Cursor(StandardCursorType.DragMove);
            RunAutoScroll();
        }
        UpdateDrop(position);
        e.Handled = true;
    }

    private void OnPageReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging)
        {
            EndDrag(apply: true);
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }
        // Click without dragging on one of several selected pages: now only this one.
        if (_keepSelectionOnPress && _pressed is { } page && _observedDoc is { } doc)
        {
            foreach (var p in doc.Pages) p.IsSelected = ReferenceEquals(p, page);
            doc.CurrentPage = page;
            e.Handled = true;
        }
        _pressAt = null;
        _keepSelectionOnPress = false;
    }

    private void EndDrag(bool apply)
    {
        if (!_dragging) return;
        _dragging = false;
        _scrollSpeed = 0;
        DropMarker.IsVisible = false;
        PageList.Cursor = null;
        _pressAt = null;
        _keepSelectionOnPress = false;
        var target = _dropIndex;
        _dropIndex = -1;
        if (apply && target >= 0 && _observedDoc is { } doc) doc.MoveSelected(target);
    }

    /// <summary>Determines the insertion point under the mouse pointer and moves the marker there; sets the scroll speed at the edge.</summary>
    private void UpdateDrop(Point position)
    {
        var height = PageList.Bounds.Height;
        _scrollSpeed = position.Y < EdgeZone ? -Speed(EdgeZone - position.Y)
            : position.Y > height - EdgeZone ? Speed(position.Y - (height - EdgeZone))
            : 0;

        // Visible entries (the list is virtualised): before the first one whose middle lies below the pointer.
        var items = new List<(int Index, double Top, double Bottom)>();
        foreach (var container in PageList.GetRealizedContainers())
        {
            var index = PageList.IndexFromContainer(container);
            if (index < 0 || !container.IsVisible || container.TranslatePoint(default, PageList) is not { } top) continue;
            items.Add((index, top.Y, top.Y + container.Bounds.Height));
        }
        if (items.Count == 0) return;
        items.Sort((a, b) => a.Index.CompareTo(b.Index));
        var drop = items[^1].Index + 1;
        var markerY = items[^1].Bottom;
        foreach (var (index, top, bottom) in items)
        {
            if (position.Y < (top + bottom) / 2)
            {
                drop = index;
                markerY = top;
                break;
            }
        }
        _dropIndex = drop;
        if (PageList.TranslatePoint(new Point(0, markerY), PageListPanel) is { } at)
        {
            Canvas.SetLeft(DropMarker, 14);
            Canvas.SetTop(DropMarker, Math.Clamp(at.Y - 3, 0, PageListPanel.Bounds.Height - 3));
            DropMarker.Width = Math.Max(20, PageListPanel.Bounds.Width - 28);
            DropMarker.IsVisible = true;
        }
    }

    /// <summary>The further towards (or beyond) the edge, the faster, up to about 2,000 pixels per second.</summary>
    private static double Speed(double depth) => Math.Clamp(depth / EdgeZone, 0.1, 2) * 16;

    /// <summary>While dragging: scrolls about 60 times per second when the pointer is at the edge.</summary>
    private async void RunAutoScroll()
    {
        if (_autoScrolling) return;
        _autoScrolling = true;
        try
        {
            while (_dragging)
            {
                AutoScrollStep();
                await Task.Delay(16);
            }
        }
        finally
        {
            _autoScrolling = false;
        }
    }

    private void AutoScrollStep()
    {
        if (!_dragging || _scrollSpeed == 0) return;
        if (PageList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroll) return;
        var max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        var y = Math.Clamp(scroll.Offset.Y + _scrollSpeed, 0, max);
        if (Math.Abs(y - scroll.Offset.Y) < 0.1) return;
        scroll.Offset = new Vector(scroll.Offset.X, y);
        // After scrolling, a different page lies under the pointer.
        Dispatcher.UIThread.Post(() => UpdateDrop(_lastPointer), DispatcherPriority.Background);
    }
}
