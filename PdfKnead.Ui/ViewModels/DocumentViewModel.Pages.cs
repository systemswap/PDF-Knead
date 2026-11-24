using System.Collections.ObjectModel;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;

namespace PdfKnead.ViewModels;

/// <summary>Editing and saving pages.</summary>
public sealed partial class DocumentViewModel
{
    /// <summary>Page list with undo/redo.</summary>
    public PageHistory History { get; }

    /// <summary>Files from which pages were inserted, and blank pages; they are closed together with the document.</summary>
    private readonly List<PdfDocument> _insertedSources = [];

    /// <summary>Whether the document differs from the saved state.</summary>
    public bool IsModified => History.IsModified || FlattenOnSave || PendingProtection is not null || RasterOnSave is not null || DescriptionOnSave is not null ||
                              RemoveClaimOnSave || NameLayersOnSave;

    public bool CanUndo => History.CanUndo && !IsWorking;

    public bool CanRedo => History.CanRedo && !IsWorking;

    /// <summary>
    /// Whether the page sequence may be changed — inserting, rotating, deleting pages, bookmarks, thumbnails (ISO 32000-2, 7.6.4.2, Table 22):
    /// bit 4 („Ändern“), from revision 3 also bit 11 („Seiten zusammenstellen“) alone; with the owner password everything is allowed.
    /// </summary>
    public bool CanEditPages => CanModifyContent || Granted(PdfPermissions.Assemble);

    /// <summary>Raised when the page list has changed (the view re-arranges).</summary>
    public event Action? PagesChanged;

    private IEnumerable<RenderPage> RenderPages() => History.Pages.Select(p => new RenderPage(p.Id, p.Source, p.Page, p.TiledAnnotations));

    /// <summary>Raised when only elements have changed (pages, order and rotation unchanged); redrawing is enough.</summary>
    public event Action? ElementsChanged;

    private void OnHistoryChanged()
    {
        // Redacting within the document, and undoing past it, switch the main document
        if (History.Main is { } main && !ReferenceEquals(main, Document)) SwitchDocument(main);
        if (_redactedDocuments.Count > 0 || _fileDocument is not null && !ReferenceEquals(_fileDocument, Document)) _ = ReleaseRedactedAsync();
        if (UpdateElementsOnly())
        {
            ValidateElementSelection();
            ElementsChanged?.Invoke();
            NotifyHistoryState();
            NoteSignedChange();
            HiddenHistoryChanged();
            CodesHistoryChanged();
            ImageEditHistoryChanged();
            NotesHistoryChanged();
            MeasureHistoryChanged();   // dimensions changed, inserted, deleted (No. 605)
            AccessibilityDocumentChanged();
            return;
        }
        var current = CurrentPage;
        SyncPages();
        Renderer.SetPages(RenderPages());
        ValidateElementSelection();
        PagesChanged?.Invoke();
        // Selection and hits refer to page positions, so they are reset after rearranging
        ClearSelection();
        NotesHistoryChanged();
        if (IsSearchOpen) RestartSearch();
        CurrentPage = current is not null && Pages.Contains(current)
            ? current
            : Pages.Count == 0 ? null : Pages[Math.Clamp(current?.Index ?? 0, 0, Pages.Count - 1)];
        NotifyHistoryState();
        NoteSignedChange();
        HiddenHistoryChanged();
        CodesHistoryChanged();
        ImageEditHistoryChanged();
        MeasureHistoryChanged();
        AccessibilityDocumentChanged();
    }

    /// <summary>
    /// If the new state differs only in the elements (same ID, page and source at every position), the
    /// entries receive their new <see cref="PageViewModel.Item"/> without a new page list, new layout,
    /// cleared text selection or restarted search (the elements are not part of the page text).
    /// Otherwise, with 3000 pages, every element change would cost about 30 ms and 1 MB.
    /// </summary>
    private bool UpdateElementsOnly()
    {
        var target = History.Pages;
        if (target.Count != Pages.Count) return false;
        for (var i = 0; i < target.Count; i++)
        {
            var (now, next) = (Pages[i].Item, target[i]);
            if (!ReferenceEquals(now, next) && (now.Id != next.Id || !ReferenceEquals(now.Page, next.Page) || !ReferenceEquals(now.Source, next.Source)))
                return false;
        }
        var tiled = false;
        for (var i = 0; i < target.Count; i++)
            if (!ReferenceEquals(Pages[i].Item, target[i]))
            {
                tiled |= target[i].HasTiledAnnotations;
                Pages[i].Item = target[i];
            }
        // Page with many foreign annotations: the tiles draw the unchanged ones too, so the renderer needs the new state
        if (tiled) Renderer.SetPages(RenderPages());
        return true;
    }

    private void NotifyHistoryState()
    {
        ClearModulePlace();   // the spot of a module belongs to the file as saved: after a change it may stand elsewhere
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageStatus));
        OnPropertyChanged(nameof(Details));
    }

    /// <summary>
    /// Adapts the page panel to the new state with few changes (entries are kept, visible thumbnails
    /// do not flicker): first remove what is gone, then move and insert.
    /// </summary>
    private void SyncPages()
    {
        var target = History.Pages;
        var existing = new Dictionary<PageItem, PageViewModel>(ReferenceEqualityComparer.Instance);
        foreach (var vm in Pages) existing[vm.Item] = vm;
        var keep = new HashSet<PageItem>(target, ReferenceEqualityComparer.Instance);
        // Much rearranged (reversing, new file after saving): a new list instead of thousands of single steps
        var changed = Math.Abs(Pages.Count - target.Count);
        for (var i = 0; i < Math.Min(Pages.Count, target.Count); i++)
            if (!ReferenceEquals(Pages[i].Item, target[i])) changed++;
        if (changed > 64)
        {
            var selected = Pages.Where(p => p.IsSelected).Select(p => p.Item).ToHashSet(ReferenceEqualityComparer.Instance);
            Pages = new ObservableCollection<PageViewModel>(target.Select((item, i) =>
            {
                var vm = existing.GetValueOrDefault(item) ?? new PageViewModel(item, i);
                vm.Index = i;
                vm.IsSelected = selected.Contains(item);
                return vm;
            }));
            return;
        }
        for (var i = Pages.Count - 1; i >= 0; i--)
            if (!keep.Contains(Pages[i].Item)) Pages.RemoveAt(i);
        for (var i = 0; i < target.Count; i++)
        {
            if (i < Pages.Count && ReferenceEquals(Pages[i].Item, target[i])) continue;
            var at = -1;
            for (var k = i + 1; k < Pages.Count; k++)
                if (ReferenceEquals(Pages[k].Item, target[i]))
                {
                    at = k;
                    break;
                }
            if (at >= 0) Pages.Move(at, i);
            else Pages.Insert(i, existing.GetValueOrDefault(target[i]) ?? new PageViewModel(target[i], i));
        }
        while (Pages.Count > target.Count) Pages.RemoveAt(Pages.Count - 1);
        for (var i = 0; i < Pages.Count; i++) Pages[i].Index = i;
    }

    // ------------------------------------------------------------ Selection

    /// <summary>Returns the selected pages; without a selection, the current page.</summary>
    /// <returns>The page positions.</returns>
    public List<int> SelectedIndices()
    {
        var selected = Pages.Where(p => p.IsSelected).Select(p => p.Index).ToList();
        if (selected.Count == 0 && CurrentPage is { } current) selected.Add(current.Index);
        return selected;
    }

    /// <summary>Finds the current position of a page of the opened file (for bookmarks).</summary>
    /// <param name="documentPageIndex">Page index in the opened file.</param>
    /// <returns>The position, or null if the page has been deleted.</returns>
    public int? PositionOf(int documentPageIndex)
    {
        for (var i = 0; i < Pages.Count; i++)
            if (ReferenceEquals(Pages[i].Item.Source, Document) && Pages[i].Page.Index == documentPageIndex) return i;
        return null;
    }

    private void Select(IEnumerable<int> indices)
    {
        var set = indices.ToHashSet();
        foreach (var page in Pages) page.IsSelected = set.Contains(page.Index);
    }

    // ------------------------------------------------------------ Editing

    /// <summary>Rotates the selected pages (or the current one) and keeps them selected.</summary>
    /// <param name="degrees">Rotation in degrees.</param>
    public void RotateSelected(int degrees)
    {
        var selected = SelectedIndices();
        History.Rotate(selected, degrees);
        Select(selected);
    }

    /// <summary>Deletes the selected pages (or the current one).</summary>
    /// <returns>false if that would delete all pages.</returns>
    public bool DeleteSelected() => History.Delete(SelectedIndices());

    /// <summary>Moves the selected pages in front of the page at position <paramref name="insertBefore"/> (dragging in the page panel).</summary>
    /// <param name="insertBefore">Target position.</param>
    public void MoveSelected(int insertBefore) => Move(SelectedIndices(), insertBefore);

    /// <summary>Moves pages in front of the page at position <paramref name="insertBefore"/>, then selects them.</summary>
    /// <param name="indices">Positions of the pages to move.</param>
    /// <param name="insertBefore">Target position.</param>
    public void Move(IReadOnlyCollection<int> indices, int insertBefore)
    {
        var first = History.Move(indices, insertBefore);
        if (first < 0) return;
        Select(Enumerable.Range(first, indices.Count));
        CurrentPage = Pages[first];
    }

    /// <summary>
    /// Applies a new order from the sort window: the same pages, arranged differently, as one undo step.
    /// </summary>
    /// <param name="order">The pages in the new order.</param>
    /// <returns>false if nothing has changed or the pages are no longer the same (edited in the meantime).</returns>
    public bool ApplyOrder(IReadOnlyList<PageItem> order)
    {
        var pages = History.Pages;
        if (order.Count != pages.Count || order.SequenceEqual(pages)) return false;
        var same = new HashSet<PageItem>(pages, ReferenceEqualityComparer.Instance);
        if (!order.All(same.Contains) || new HashSet<PageItem>(order, ReferenceEqualityComparer.Instance).Count != order.Count) return false;
        History.Apply(Tr.T("Seiten sortieren"), [.. order]);
        return true;
    }

    /// <summary>Reverses the order of the selected pages (with fewer than two selected: all pages).</summary>
    public void ReverseSelected()
    {
        var selected = Pages.Where(p => p.IsSelected).Select(p => p.Index).Order().ToList();
        if (selected.Count < 2) selected = [.. Enumerable.Range(0, Pages.Count)];
        var pages = History.Pages.ToArray();
        var reversed = selected.Select(i => pages[i]).Reverse().ToList();
        for (var k = 0; k < selected.Count; k++) pages[selected[k]] = reversed[k];
        History.Apply(Tr.T("Reihenfolge umkehren"), pages);
        Select(selected);
    }

    /// <summary>Inserts a blank page the size of the current page after it (A4 if there is no current page).</summary>
    public void InsertBlankPage()
    {
        var reference = CurrentPage?.Page;
        var (w, h) = reference is null ? (595.28, 841.89) : (reference.Width, reference.Height);
        var blank = BlankPage.Create(w, h);
        _insertedSources.Add(blank.Source);
        var at = (CurrentPage?.Index ?? -1) + 1;
        History.Insert(at, [blank], Tr.T("Leerseite einfügen"));
        Select([at]);
        CurrentPage = Pages[at];
    }

    /// <summary>Inserts blank pages in the chosen format (one undo step): before or after the current page, or at the end.</summary>
    /// <param name="setup">Format and number of pages.</param>
    /// <param name="position">Where to insert them.</param>
    public void InsertBlankPages(PageSetup setup, BlankPagePosition position)
    {
        var (w, h) = setup.Points;
        var source = BlankPage.CreateDocument(w, h, setup.Pages, "1.4");
        _insertedSources.Add(source);
        var current = CurrentPage?.Index;
        var at = position switch
        {
            BlankPagePosition.Before => current ?? 0,
            BlankPagePosition.After => (current ?? Pages.Count - 1) + 1,
            _ => Pages.Count,
        };
        at = Math.Clamp(at, 0, Pages.Count);
        var items = source.Pages.Select(p => new PageItem(source, p)).ToList();
        History.Insert(at, items, items.Count == 1 ? Tr.T("Leerseite einfügen") : Tr.T("Leerseiten einfügen"));
        Select(Enumerable.Range(at, items.Count));
        CurrentPage = Pages[at];
        ScrollToPage(at);   // visible even at the end of a long document
    }

    /// <summary>Inserts pages of another file (merging). The document takes ownership of the file.</summary>
    /// <param name="at">Insert position, clamped to the page count.</param>
    /// <param name="source">The other file.</param>
    /// <param name="sourceIndices">Page indices in <paramref name="source"/>.</param>
    public void InsertPages(int at, PdfDocument source, IReadOnlyList<int> sourceIndices)
    {
        if (!_insertedSources.Contains(source)) _insertedSources.Add(source);
        if (sourceIndices.Count == 0) return;
        var items = sourceIndices.Select(i => new PageItem(source, source.Pages[i])).ToList();
        at = Math.Clamp(at, 0, Pages.Count);
        History.Insert(at, items, sourceIndices.Count == 1 ? Tr.T("Seite einfügen") : Tr.T("Seiten einfügen"));
        Select(Enumerable.Range(at, items.Count));
        CurrentPage = Pages[at];
    }

    /// <summary>Undoes the last step.</summary>
    public void Undo() => History.Undo();

    /// <summary>Redoes the last undone step.</summary>
    public void Redo() => History.Redo();

    // ------------------------------------------------------------ Saving

    /// <summary>
    /// Whether saving only appends to the signed file, so that its intact signatures stay valid: only filled-in fields, new comments
    /// and signature fields, signatures still to be signed — and nothing scheduled that changes the whole file (compression,
    /// protection, flattening, as images). See KONZEPT.md, section 5.17, step S5.
    /// </summary>
    /// <param name="compression">The compression chosen for this save; null = none.</param>
    /// <returns>true if appending is enough.</returns>
    public bool AppendsToSigned(CompressionSettings? compression) => AppendsToSigned(compression, out _);

    /// <inheritdoc cref="AppendsToSigned(CompressionSettings?)"/>
    /// <param name="reason">If a restriction of the signatures (certification, locked fields) is what prevents appending: why; otherwise null.</param>
    public bool AppendsToSigned(CompressionSettings? compression, out string? reason)
    {
        reason = null;
        return compression is null && !FlattenOnSave && PendingProtection is null && !RestrictionsLifted && RasterOnSave is null && DescriptionOnSave is null && !RemoveClaimOnSave && !NameLayersOnSave && !IsUntitled
               && ReferenceEquals(History.Main ?? Document, Document) && SignedSaving.AppendsOnly(Document, History.SavedPages, History.Pages, out reason);
    }

    /// <summary>
    /// Version for „Speichern“: that of the file, or the newer one of a file whose pages were inserted (ISO 32000-2, I.2, sentence 17) — at
    /// least 1.4, as PDF-Knead writes nothing older; an unknown newer version becomes 2.0 (7.5.2, sentence 15).
    /// </summary>
    public string SaveVersion => PdfWriter.Versions.Contains(NewestSourceVersion) ? NewestSourceVersion
        : PdfWriter.CompareVersions(NewestSourceVersion, "1.4") < 0 ? "1.4" : "2.0";

    /// <summary>The newest version of the file and of the files its pages come from (<see cref="PdfWriter.NewestVersion"/>).</summary>
    public string NewestSourceVersion =>
        PdfWriter.NewestVersion(History.Pages.Select(p => p.Source).Prepend(Document).Distinct().Select(d => d.Version));

    /// <summary>
    /// Saves all pages to <paramref name="path"/> (also over the opened file) and opens the
    /// result: the document then shows the saved file and the history starts afresh.
    /// </summary>
    /// <param name="path">Target file.</param>
    /// <param name="options">Save options (see <see cref="SaveOptions"/>).</param>
    /// <param name="certificate">Signs the digital signatures still to be signed (see <see cref="SignedSaving"/>); null = there are none.</param>
    /// <param name="append">Append to the signed file instead of writing it anew (see <see cref="AppendsToSigned"/>).</param>
    /// <param name="timestamps">A time-stamping authority for the signatures (PAdES B-T); null for none.</param>
    /// <returns>The result of the save.</returns>
    /// <exception cref="IOException">The file was saved but could not be reopened.</exception>
    /// <exception cref="TimestampException">A time stamp could not be obtained; nothing was written.</exception>
    /// <param name="revocation">Where to ask for the revocation status of the new signatures (PAdES B-LT); null for none.</param>
    /// <exception cref="CertificateRevokedException">A certificate of a new signature is revoked; nothing was written.</exception>
    /// <param name="archive">After the validation data, a document time stamp (PAdES B-LTA); only with <paramref name="timestamps"/>.</param>
    /// <param name="certificateFor">The certificate of a signature that names its own (chosen for it alone); null: <paramref name="certificate"/> for all.</param>
    public async Task<PdfSaveResult> SaveAsync(string path, PdfSaveOptions options, Core.Platform.SigningCertificate? certificate = null, bool append = false,
        ITimestampAuthority? timestamps = null, IRevocationSource? revocation = null, bool archive = false,
        Func<Core.Pdf.Editing.DigitalSigning, Core.Platform.SigningCertificate?>? certificateFor = null)
    {
        var full = System.IO.Path.GetFullPath(path);
        var pages = History.Pages.ToArray();
        var main = Document;
        var raster = RasterOnSave;
        var password = PasswordAfterSaving(options);
        // Write and reopen as work (history locked); taking over follows while still locked, so no change is lost
        var (result, _) = await WorkAsync<(PdfSaveResult, PdfDocument)>(async () =>
        {
            var written = await Task.Run(() => raster is not null ? SaveAsImages(main, pages, full, options, raster)
                : certificate is not null || certificateFor is not null || append
                    ? SignedSaving.Save(main, pages, full, options, append, certificate, DateTimeOffset.Now, timestamps: timestamps, revocation: revocation, archive: archive,
                        certificateFor: certificateFor)
                : PdfFileSaver.Save(main, pages, full, options));
            try
            {
                return (written, await Task.Run(() => PdfDocument.Open(full, password)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PdfException)
            {
                // Already saved; only reopening failed (e.g. a virus scanner briefly holds the new file). Say so instead of
                // „Speichern fehlgeschlagen“; the document keeps showing the state before saving
                throw new IOException(Tr.T("Gespeichert, aber die Datei ließ sich danach nicht wieder öffnen: {0}", e.Message), e);
            }
        }, async done =>
        {
            var saved = done.Item2;
            if (_closed)
            {
                saved.Dispose();
                return;
            }
            _password = password;
            // the saved file is the document as checked: answers of the human check stay (unless saved as images or flattened)
            var keepAnswers = raster is null && !FlattenOnSave;
            if (keepAnswers) _keepAnswers++;
            try
            {
                // Saved as images or images compressed lossily: new pages (old tiles and display lists would still show the old content)
                // Signed: the signatures are now fields of the file, drawn with the page
                await AdoptAsync(saved, full, pages, keepKeys: raster is null && certificate is null && !append && options.Compression is not { Lossy: true });
                RasterOnSave = null;
                FlattenOnSave = false;   // the saved file is flat
                PendingProtection = null;   // the protection is now in the file
                DescriptionOnSave = null;   // as is the description
                RemoveClaimOnSave = false;   // and the claim removed
                NameLayersOnSave = false;   // and the layer configurations named
            }
            finally
            {
                if (keepAnswers) _keepAnswers--;
            }
            ResetDescription();
            RestrictionsLifted = false;   // as are the full permissions
            OnPropertyChanged(nameof(IsProtected));
            OnPropertyChanged(nameof(CanChangeProtection));
        });
        return result;
    }

    /// <summary>
    /// Appends validation data and a document time stamp to the signed file (<see cref="SignedSaving.AddArchiveTimestamp"/>) and takes over the
    /// extended file, as after saving.
    /// </summary>
    /// <param name="timestamps">The time-stamping authority.</param>
    /// <param name="revocation">Where to ask for the revocation status; null for the time stamp alone.</param>
    /// <returns>The result.</returns>
    public async Task<PdfSaveResult> AddArchiveTimestampAsync(ITimestampAuthority timestamps, IRevocationSource? revocation)
    {
        var full = System.IO.Path.GetFullPath(Path);
        var pages = History.Pages.ToArray();
        var main = Document;
        var password = _password;
        var (result, _) = await WorkAsync<(PdfSaveResult, PdfDocument)>(async () =>
        {
            var written = await Task.Run(() => SignedSaving.AddArchiveTimestamp(main, full, timestamps, revocation));
            return (written, await Task.Run(() => PdfDocument.Open(full, password)));
        }, async done =>
        {
            if (_closed)
            {
                done.Item2.Dispose();
                return;
            }
            await AdoptAsync(done.Item2, full, pages, keepKeys: false);
        });
        await CheckSignaturesAsync();
        return result;
    }

    /// <summary>
    /// „Flachlegen als Bild“: the pages, as displayed, become images in an intermediate file in memory, from which the final file is
    /// written (version, password and permissions as chosen). No intermediate file is created on disk.
    /// </summary>
    private static PdfSaveResult SaveAsImages(PdfDocument main, PageItem[] pages, string path, PdfSaveOptions options, Rendering.RasterSettings raster)
    {
        using var images = AsImages(pages, raster);
        return PdfFileSaver.Save(images, PageItem.From(images), path, AsImagesOptions(main, options));
    }

    /// <summary>
    /// The pages as displayed, as an image PDF in memory; owned by the caller. Redactions were applied beforehand („Schwärzen zuerst“);
    /// a mark left over is refused, since the red mark and the content underneath would end up in the image.
    /// </summary>
    internal static PdfDocument AsImages(IReadOnlyList<PageItem> pages, Rendering.RasterSettings raster, CancellationToken cancel = default)
    {
        InDocumentRedaction.EnsureApplied(pages);
        using var buffer = new Core.Pdf.IO.MemoryPdf();
        Rendering.RasterExport.Write(pages, buffer, raster, cancel: cancel);
        return buffer.OpenDocument();
    }

    /// <summary>Options for writing the image PDF from <see cref="AsImages"/>.</summary>
    internal static PdfSaveOptions AsImagesOptions(PdfDocument main, PdfSaveOptions options) =>
        // Existing protection stays (it belongs to the original, not to the intermediate file); redaction has already been done
        options with { EncryptionFrom = main, Flatten = false, Cleanup = CleanupItems.None, ApplyRedactions = false, NewFileId = true };

    /// <summary>Takes over the saved file: pages keep their keys (tiles stay), old files are closed.</summary>
    private async Task AdoptAsync(PdfDocument saved, string path, PageItem[] pages, bool keepKeys = true)
    {
        var old = AllSources();
        var items = keepKeys && saved.PageCount == pages.Length
            ? pages.Select((p, i) => p.Reloaded(saved, saved.Pages[i])).ToArray()
            : PageItem.From(saved);
        var current = CurrentPage?.Index ?? 0;
        Document = saved;
        _fileDocument = saved;
        IsUntitled = false;   // before Path: its notification also updates Folder
        Path = path;
        RememberDiskState();   // our own save is no change from outside
        _insertedSources.Clear();
        _outlinesLeftOut.Clear();   // the saved file has its bookmarks now
        _redactedDocuments.Clear();   // released below together with the other old files
        ResetText();
        _outline = null;
        History.Reset(items, saved);   // calls OnHistoryChanged: new pages, the renderer reads only the new file
        if (Pages.Count > 0) CurrentPage = Pages[Math.Clamp(current, 0, Pages.Count - 1)];
        OnPropertyChanged(nameof(Outline));
        OnPropertyChanged(nameof(HasOutline));
        OnPropertyChanged(nameof(PdfVersion));
        OnPropertyChanged(nameof(Properties));
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(HasProblems));
        // The saved file: its signatures anew (an appended one, or none left after rewriting), the hint again at the next change
        _signedEditHintShown = false;
        _ = CheckSignaturesAsync();
        _ = CheckFakeRedactionsAsync();
        old.Remove(saved);
        await Renderer.WaitUntilUnused(old);
        foreach (var doc in old) doc.Dispose();
        PdfFileSaver.DeleteLeftovers();   // the old version is no longer memory-mapped
    }

    /// <summary>All open files of the document (opened, inserted, and those the history still needs).</summary>
    private HashSet<PdfDocument> AllSources()
    {
        var set = History.Sources();
        set.Add(Document);
        foreach (var doc in _insertedSources) set.Add(doc);
        return set;
    }

    /// <summary>Closes the document: waits for running work and until no background reader remains, then releases the files.</summary>
    public async Task CloseAsync()
    {
        _closed = true;
        CancelSignatureCheck();
        _hiddenScan?.Cancel();   // the search for hidden text stops (it would otherwise run through the remaining pages)
        await WhenIdle();
        History.Changed -= OnHistoryChanged;
        DisposeText();
        var sources = AllSources();
        Renderer.SetPages([]);
        Renderer.Dispose();   // cancels display lists being built
        await Renderer.WaitUntilUnused(sources);
        foreach (var source in sources) source.Dispose();
        _redactedDocuments.Clear();
        PdfFileSaver.DeleteLeftovers();
    }
}

/// <summary>Where „Leere Seiten einfügen“ puts the pages.</summary>
public enum BlankPagePosition
{
    Before,
    After,
    End,
}

/// <summary>Result of the „Neues Dokument“ / „Leere Seiten einfügen“ window.</summary>
public sealed record BlankPagesChoice(PageSetup Setup, BlankPagePosition Position, string? Title = null, string? Language = null);
