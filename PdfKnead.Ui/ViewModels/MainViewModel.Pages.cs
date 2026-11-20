using PdfKnead.Core.Pdf.Signatures;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Platform;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>Answer to „Unterschrift festschreiben?“ (or flattening): cancel, save, or save as images at the same time.</summary>
public enum FixingReply
{
    Cancel,
    Save,
    SaveAsImage,
}

/// <summary>Answer to „Änderungen speichern?“.</summary>
public enum SaveChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>A choice of PDF version for Save As.</summary>
public sealed record VersionOption(string Version, string Label)
{
    /// <summary>All choices; the labels are German texts (shown translated).</summary>
    public static readonly IReadOnlyList<VersionOption> All =
    [
        new("1.4", "PDF 1.4 — für sehr alte Programme (ohne Objekt-Streams, größer)"),
        new("1.7", "PDF 1.7 — empfohlen, öffnet praktisch jedes Programm"),
        new("2.0", "PDF 2.0 — aktuelle Norm, für neue Programme"),
    ];
}

/// <summary>A compression choice for Save As; <see cref="Preset"/> null = do not compress.</summary>
public sealed record CompressionOption(PdfKnead.Core.Pdf.Writing.CompressionPreset? Preset, string Label)
{
    /// <summary>All choices; the labels are German texts (shown translated).</summary>
    public static readonly IReadOnlyList<CompressionOption> All =
    [
        new(null, "Nicht komprimieren"),
        new(PdfKnead.Core.Pdf.Writing.CompressionPreset.Lossless, "Nur verlustfrei — Aussehen bleibt gleich"),
        new(PdfKnead.Core.Pdf.Writing.CompressionPreset.Print, "Druck — Bilder auf 300 dpi"),
        new(PdfKnead.Core.Pdf.Writing.CompressionPreset.EBook, "E-Book — Bilder auf 150 dpi"),
        new(PdfKnead.Core.Pdf.Writing.CompressionPreset.Screen, "Bildschirm — Bilder auf 120 dpi, am kleinsten"),
    ];

    public PdfKnead.Core.Pdf.Writing.CompressionSettings? Settings => Preset is { } p ? PdfKnead.Core.Pdf.Writing.CompressionSettings.For(p) : null;
}

/// <summary>Saving and editing pages.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Asks whether unsaved changes should be saved (set by the view); takes the file name.</summary>
    public Func<string, Task<SaveChoice>>? AskSaveChanges { get; set; }

    /// <summary>Lets the user choose a save location (set by the view): takes title, suggested name and folder; returns the path, or null.</summary>
    public Func<string, string, string?, Task<string?>>? PickSavePath { get; set; }

    /// <summary>Short feedback in the status bar („Gespeichert“); disappears by itself after 5 seconds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string Notice { get; set; } = "";

    public bool HasNotice => Notice.Length > 0;

    private CancellationTokenSource? _noticeTimer;

    private void ShowNotice(string text)
    {
        Notice = text;
        _noticeTimer?.Cancel();
        var timer = _noticeTimer = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(5), timer.Token).ContinueWith(_ =>
        {
            if (!timer.IsCancellationRequested) Avalonia.Threading.Dispatcher.UIThread.Post(() => Notice = "");
        }, TaskScheduler.Default);
    }

    /// <summary>Versions for „Speichern unter“.</summary>
    public IReadOnlyList<VersionOption> VersionOptions => VersionOption.All;

    [ObservableProperty] public partial VersionOption SelectedVersion { get; set; } = VersionOption.All[1];

    /// <summary>Compression choices for „Speichern unter“.</summary>
    public IReadOnlyList<CompressionOption> CompressionOptions => CompressionOption.All;

    [ObservableProperty] public partial CompressionOption SelectedCompression { get; set; } = CompressionOption.All[0];

    private bool CanUndo => SelectedDocument?.CanUndo == true;

    private bool CanRedo => SelectedDocument?.CanRedo == true;

    private bool CanEditPages => SelectedDocument is { CanEditPages: true, IsWorking: false };

    private bool CanDeletePages => CanEditPages && SelectedDocument!.PageCount > 1;

    private void NotifyPageCommands()
    {
        foreach (var command in new IRelayCommand[]
                 {
                     UndoCommand, RedoCommand, SaveCommand, SaveAsCommand, RotatePagesLeftCommand, RotatePagesRightCommand,
                     DeletePagesCommand, InsertBlankPageCommand, InsertBlankPageAsCommand, InsertBlankPagesCommand, ReversePagesCommand, MergeFilesCommand, InsertFromFileCommand, SortPagesCommand,
                     ExtractPagesCommand, SplitCommand, EditProtectionCommand, LiftRestrictionsCommand, CleanDocumentCommand, ToggleRasterCommand,
                     RedactTextCommand, RedactHiddenCommand, SearchRedactCommand, ApplyRedactionsCommand, ExportImagePdfCommand, ExportPdfACommand, ExportImagesCommand,
                     RecognizeTextCommand,
                 })
            command.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoTip));
        OnPropertyChanged(nameof(RedoTip));
    }

    /// <summary>Checks whether the pages may be rearranged; if the protected file forbids it, says why nothing happens.</summary>
    private bool Editable(DocumentViewModel doc)
    {
        if (doc.CanEditPages) return true;
        Message = Tr.T("Dieses Dokument erlaubt kein Ändern der Seiten (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen.");
        return false;
    }

    // ------------------------------------------------------------ Undo

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => SelectedDocument?.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => SelectedDocument?.Redo();

    /// <summary>Tooltip: what undo reverts.</summary>
    public string UndoTip => SelectedDocument?.History.UndoDescription is { } d ? Tr.T("Rückgängig: {0} (Strg+Z)", d) : Tr.T("Rückgängig (Strg+Z)");

    public string RedoTip => SelectedDocument?.History.RedoDescription is { } d ? Tr.T("Wiederholen: {0} (Strg+Y)", d) : Tr.T("Wiederholen (Strg+Y)");

    // ------------------------------------------------------------ Pages

    [RelayCommand(CanExecute = nameof(CanEditPages))]
    private void RotatePagesLeft()
    {
        if (SelectedDocument is { } doc && Editable(doc)) doc.RotateSelected(-90);
    }

    [RelayCommand(CanExecute = nameof(CanEditPages))]
    private void RotatePagesRight()
    {
        if (SelectedDocument is { } doc && Editable(doc)) doc.RotateSelected(90);
    }

    [RelayCommand(CanExecute = nameof(CanDeletePages))]
    private void DeletePages()
    {
        if (SelectedDocument is not { } doc || !Editable(doc)) return;
        if (!doc.DeleteSelected()) Message = Tr.T("Mindestens eine Seite muss bleiben.");
    }

    [RelayCommand(CanExecute = nameof(CanEditPages))]
    private void InsertBlankPage()
    {
        if (SelectedDocument is { } doc && Editable(doc)) doc.InsertBlankPage();
    }

    [RelayCommand(CanExecute = nameof(CanEditPages))]
    private void ReversePages()
    {
        if (SelectedDocument is { } doc && Editable(doc)) doc.ReverseSelected();
    }

    // ------------------------------------------------------------ Merging, inserting, extracting

    /// <summary>Lets the user choose pages of a file (window with check boxes): takes document and file name; returns the indices, or null.</summary>
    public Func<PdfKnead.Core.Pdf.Document.PdfDocument, string, Task<IReadOnlyList<int>?>>? ChoosePages { get; set; }

    /// <summary>Lets the user choose PDF files: takes the title; returns the paths (empty = cancelled).</summary>
    public Func<string, Task<IReadOnlyList<string>>>? PickOpenPaths { get; set; }

    /// <summary>
    /// Pages of a protected file are taken over only with the „Kopieren“ permission (or the owner password): they leave their file and
    /// then fall under the permissions of the target document. „Seiten zusammenstellen“ alone is not enough, otherwise the content of
    /// a file without copy permission could be copied, printed and saved without a password via another document.
    /// </summary>
    internal static bool MayTakePages(PdfKnead.Core.Pdf.Document.PdfDocument source) =>
        source.Security is not { } s || s.IsOwner || s.Permissions.HasFlag(Core.Pdf.Security.PdfPermissions.Copy);

    /// <summary>
    /// Inserts pages from files (merging): the selection window for each file, then insertion at position
    /// <paramref name="at"/>. Several files are inserted one after another.
    /// </summary>
    /// <param name="paths">The files.</param>
    /// <param name="at">Insert position; null = at the end.</param>
    public async Task InsertFilesAsync(IEnumerable<string> paths, int? at = null)
    {
        if (SelectedDocument is not { } doc || !Editable(doc)) return;
        var position = at ?? doc.PageCount;
        var problems = new List<string>();
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            var name = Path.GetFileName(full);
            PdfKnead.Core.Pdf.Document.PdfDocument? source;
            string? sourcePassword;
            Busy = Tr.T("Öffne {0} …", name);
            try
            {
                (source, sourcePassword) = await LoadAsync(full);
            }
            catch (Exception ex) when (Failure.Handled(ex))
            {
                problems.Add(Tr.T("{0}: {1}", name, Failure.Text(ex)));
                continue;
            }
            finally
            {
                Busy = null;
            }
            if (source is null) continue;   // password prompt cancelled
            if (source.PageCount == 0 || !MayTakePages(source))
            {
                problems.Add(source.PageCount == 0 ? Tr.T("{0}: keine Seiten", name)
                    : Tr.T("{0}: Die Datei erlaubt kein Übernehmen ihrer Seiten (Rechte der Datei).", name));
                source.Dispose();
                continue;
            }
            IReadOnlyList<int>? chosen = ChoosePages is null ? [.. Enumerable.Range(0, source.PageCount)] : await ChoosePages(source, name);
            if (chosen is not { Count: > 0 } || !Documents.Contains(doc))
            {
                source.Dispose();
                continue;
            }
            doc.InsertPages(Math.Min(position, doc.PageCount), source, chosen);
            position = Math.Min(position, doc.PageCount) + chosen.Count;
            ShowNotice(chosen.Count == 1 ? Tr.T("1 Seite aus {0} eingefügt", name) : Tr.T("{0} Seiten aus {1} eingefügt", chosen.Count, name));
            // Source protected with an open password but this document not: say clearly that the protection does not apply here
            if (sourcePassword is { Length: > 0 } && !doc.IsProtected)
                problems.Add(Tr.T("{0} war mit einem Passwort geschützt — dieses Dokument nicht. Bei Bedarf unter „Sicher“ → „Passwort und Rechte“ einen Schutz vergeben.", name));
        }
        if (problems.Count > 0) Message = string.Join("  ·  ", problems);
    }

    /// <summary>Shows the split window (set by the view); returns true if parts were saved.</summary>
    public Func<SplitViewModel, Task<bool>>? ShowSplit { get; set; }

    /// <summary>Splits the document into several files (name parts, assign pages).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Split()
    {
        if (SelectedDocument is not { } doc || ShowSplit is null) return;
        if (!await RedactFirstAsync(doc, Tr.T("Teilen"))) return;
        var split = new SplitViewModel(doc) { AskDocumentAttachments = () => KeepDocumentAttachmentsAsync(doc) };
        if (!await ShowSplit(split)) return;
        var files = split.SavedFiles;
        ShowNotice(files.Count == 1 ? Tr.T("Gespeichert: {0}", files[0]) : Tr.T("{0} Dateien gespeichert: {1}", files.Count, string.Join(", ", files)));
    }

    /// <summary>
    /// A file of some pages (extracting, splitting) takes only what belongs to those pages; the attachments of the whole document (its
    /// source, for instance) only if the user says so — asked only when there are any (decided on 3.11.2026;
    /// docs/FEHLERGEDAECHTNIS.md, No. 447).
    /// </summary>
    /// <returns>Whether they go along; null if the user cancelled.</returns>
    private async Task<bool?> KeepDocumentAttachmentsAsync(DocumentViewModel doc)
    {
        int count;
        try
        {
            count = await doc.WorkAsync(() => Task.Run(() => DocumentCleanup.DocumentAttachments(doc.Document)));
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            return false;   // not readable: nothing goes along
        }
        if (count == 0 || AskChoice is null) return false;
        var answer = await AskChoice(Tr.T("Dateianhänge des Dokuments"),
            count == 1
                ? Tr.T("Das Dokument hat einen Dateianhang, der zum ganzen Dokument gehört (etwa seine Quelldatei). Soll er mit in die neue Datei?")
                : Tr.T("Das Dokument hat {0} Dateianhänge, die zum ganzen Dokument gehören (etwa seine Quelldatei). Sollen sie mit in die neue Datei?", count),
            Tr.T("Weglassen"), Tr.T("Mitgeben"));
        return answer switch { 1 => false, 2 => true, _ => null };
    }

    /// <summary>Shows the sort window (set by the view); returns the new order, or null if cancelled.</summary>
    public Func<SortViewModel, Task<IReadOnlyList<PageItem>?>>? ShowSort { get; set; }

    /// <summary>Sorts pages in a tile view; „Übernehmen“ rearranges the page panel in one step.</summary>
    [RelayCommand(CanExecute = nameof(CanEditPages))]
    private async Task SortPages()
    {
        if (SelectedDocument is not { } doc || ShowSort is null || !Editable(doc)) return;
        if (await ShowSort(new SortViewModel(doc)) is not { } order) return;
        if (doc.ApplyOrder(order)) ShowNotice(Tr.T("Seiten neu sortiert — Strg+Z nimmt es zurück."));
    }

    /// <summary>„Zusammenführen“: the window „Seiten zusammenführen“; also without an open document (the result becomes a new file).</summary>
    [RelayCommand(CanExecute = nameof(CanMerge))]
    private Task MergeFiles() => MergeAsync(selectCurrentPage: false);

    /// <summary>„Aus Datei“: the same window, with the current page selected in the result so that pages go after it.</summary>
    [RelayCommand(CanExecute = nameof(CanEditPages))]
    private Task InsertFromFile() => MergeAsync(selectCurrentPage: true);

    private bool CanMerge => SelectedDocument is null || CanEditPages;

    /// <summary>Saves the selected pages as a new file; the document stays as it is.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ExtractPages()
    {
        if (SelectedDocument is not { } doc || PickSavePath is null) return;
        var indices = doc.SelectedIndices().Order().ToList();
        if (indices.Count == 0) return;
        if (!await RedactFirstAsync(doc, Tr.T("Seiten extrahieren"))) return;
        var suggested = $"{Path.GetFileNameWithoutExtension(doc.FileName)} ({Tr.T("Seiten {0}", PdfKnead.Core.Pdf.Editing.PageRange.Format(indices))}).pdf";
        var path = await PickSavePath(Tr.T("Seiten extrahieren"), suggested, SaveFolder(doc));
        if (path is null) return;
        var full = Path.GetFullPath(path);
        if (Documents.Any(d => SamePath(d.Path, full)))
        {
            Message = Tr.T("{0} ist geöffnet — zum Extrahieren einen anderen Namen wählen.", Path.GetFileName(full));
            return;
        }
        var items = indices.Select(i => doc.History.Pages[i]).ToList();
        if (await KeepDocumentAttachmentsAsync(doc) is not { } keep) return;
        var options = doc.SaveOptions(doc.SaveVersion) with { PartOfDocument = true, KeepDocumentAttachments = keep };
        Busy = Tr.T("Speichere {0} …", Path.GetFileName(full));
        try
        {
            InDocumentRedaction.EnsureApplied(items);   // applied above by the gate; a mark left over refuses saving
            var result = await doc.WorkAsync(() => Task.Run(() => PdfFileSaver.Save(doc.Document, items, full, options)));
            ShowNotice(Tr.T("Gespeichert: {0} (PDF {1}, {2})", Path.GetFileName(full), result.Version, DocumentViewModel.FormatSize(result.Length)));
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Message = Tr.T("Speichern fehlgeschlagen: {0}", Failure.Text(ex));
        }
        finally
        {
            Busy = null;
        }
    }

    // ------------------------------------------------------------ Saving

    private bool CanSave => SelectedDocument is { IsWorking: false };

    /// <summary>Saves over the opened file, in its version; a new document asks for the location first.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        if (SelectedDocument is not { } doc) return;
        if (doc.IsUntitled) await SaveUntitledAsync(doc);
        else await SaveDocumentAsync(doc, doc.Path, doc.SaveVersion);
    }

    /// <summary>Save As: choose the location, version from <see cref="SelectedVersion"/>; the original stays untouched.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAs()
    {
        if (SelectedDocument is not { } doc || PickSavePath is null) return;
        if (!await RedactFirstAsync(doc, Tr.T("Speichern unter"))) return;
        var path = await PickSavePath(Tr.T("Speichern unter"), doc.FileName, SaveFolder(doc));
        if (path is null) return;
        if (await SaveDocumentAsync(doc, path, SelectedVersion.Version, SelectedCompression.Settings)) BackstageOpen = false;
    }

    /// <summary>Lets the user choose password and permissions (set by the view): takes whether the file is encrypted, the current permissions and the version; returns the change, or null.</summary>
    public Func<bool, PdfKnead.Core.Pdf.Security.PdfPermissions, string, Task<ProtectionChange?>>? PickProtection { get; set; }

    /// <summary>„Sicher“ tab → „Passwort und Rechte“: applies from the next save (counts as a modification).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditProtection()
    {
        if (SelectedDocument is not { } doc || PickProtection is null) return;
        if (!doc.CanChangeProtection)
        {
            Message = Tr.T("Den Schutz ändern kann nur, wer die Datei mit dem Rechte-Passwort geöffnet hat — oder vorher Sicher → Einschränkungen entfernen.");
            return;
        }
        if (await PickProtection(doc.IsProtected, doc.CurrentPermissions, doc.SaveVersion) is not { } change) return;
        // Removal for a file without protection: nothing to schedule
        doc.PendingProtection = change.Protection is null && doc.Document.Security is null ? null : change;
        ShowNotice(change.Protection is null
            ? Tr.T("Der Schutz wird beim nächsten Speichern entfernt.")
            : Tr.T("Passwort und Rechte gelten ab dem nächsten Speichern."));
    }

    /// <summary>Asks „Einschränkungen entfernen“ (set by the view): takes the file, what is locked, whether there is an open password and a check for the owner password; returns the reply, or null.</summary>
    public Func<string, string, bool, Func<string, bool>, Task<RestrictionsReply?>>? PickRestrictions { get; set; }

    private bool CanLiftRestrictions => SelectedDocument is { HasRestrictions: true, IsWorking: false };

    /// <summary>
    /// „Sicher“ tab → „Einschränkungen entfernen“: when opened with the owner password no question is asked (an open password stays),
    /// otherwise only with the owner password or explicit confirmation. Takes effect immediately, in the file from the next save.
    /// See KONZEPT.md, section 5.11.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLiftRestrictions))]
    private async Task LiftRestrictions()
    {
        if (SelectedDocument is not { HasRestrictions: true } doc || doc.Document.Security is not { } security) return;
        var openPassword = doc.HasOpenPassword;
        // Only attachments encrypted and locked: without their password the file could not be rewritten correctly. Likewise when all
        // permissions cannot be granted while the file has an open password
        if (security.AttachmentsLocked || !security.CanGrantAllPermissions && openPassword)
        {
            Message = Tr.T("Die Rechte dieser Datei lassen sich nicht ändern.");
            return;
        }
        var keepOpen = true;
        if (!security.IsOwner)
        {
            if (PickRestrictions is null ||
                await PickRestrictions(doc.FileName, doc.LockedRightsText, openPassword, doc.IsOwnerPassword) is not { } reply)
                return;
            keepOpen = reply.KeepOpenPassword;
        }
        doc.LiftRestrictions(keepOpen);
        // the message that sent here no longer holds (it stayed above the notice and still said no change was allowed)
        if (Message == Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen.")) Message = "";
        ShowNotice(openPassword && keepOpen
            ? Tr.T("Die Einschränkungen sind aufgehoben; in der Datei ab dem nächsten Speichern. Das Passwort zum Öffnen bleibt.")
            : Tr.T("Die Einschränkungen sind aufgehoben; in der Datei ab dem nächsten Speichern."));
    }

    // ------------------------------------------------------------ Redaction

    /// <summary>Shows „Suchen und schwärzen“ (set by the view): takes the search function; returns the chosen hits with their reason, or null.</summary>
    public Func<Func<IReadOnlyList<string>, RedactionPatterns, bool, Task<List<RedactionHit>>>, Task<RedactionPick?>>? PickRedactionHits { get; set; }

    /// <summary>
    /// Switches the „Text schwärzen“ tool on and off: text already selected becomes a redaction mark immediately, after that every
    /// text selection on release, until the tool is switched off or Esc is pressed.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RedactText()
    {
        if (SelectedDocument is not { } doc) return;
        if (Tool == ViewTool.RedactText)
        {
            Tool = ViewTool.SelectText;
            return;
        }
        if (!CheckCanEdit(doc)) return;
        doc.RedactSelection();
        Tool = ViewTool.RedactText;
    }

    /// <summary>
    /// Switches „Verborgenes schwärzen“ on and off (KONZEPT.md, section 5.18): the hidden text of all pages is framed and listed on the
    /// right, a click marks it for redaction; removed only by „Schwärzungen anwenden“.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RedactHidden()
    {
        if (SelectedDocument is not { } doc) return;
        if (Tool == ViewTool.RedactHidden)
        {
            Tool = ViewTool.SelectText;
            return;
        }
        if (!CheckCanEdit(doc)) return;
        Tool = ViewTool.RedactHidden;
    }

    /// <summary>Whether „Verborgenes schwärzen“ is on, so the „Verborgener Inhalt“ panel on the right is visible.</summary>
    public bool IsHiddenTool => Tool == ViewTool.RedactHidden;

    /// <summary>Searches the whole document for words and patterns and marks the chosen hits for redaction.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SearchRedact()
    {
        if (SelectedDocument is not { } doc || PickRedactionHits is null || !CheckCanEdit(doc)) return;
        if (await PickRedactionHits((words, patterns, matchCase) => doc.FindForRedactionAsync(words, patterns, matchCase)) is not { Hits.Count: > 0 } pick) return;
        // a hit under a mark already is not marked again (report of 4.11.2026)
        var count = await doc.RedactHitsAsync(pick.Hits.Where(h => !h.Marked), reasonOf: pick.For);
        if (pick.Reason is { Code: null, Reason: var own }) ElementPropertiesViewModel.RememberReason(own);
        ShowNotice(count == 1 ? Tr.T("1 Stelle zum Schwärzen markiert.") : Tr.T("{0} Stellen zum Schwärzen markiert.", count));
    }

    /// <summary>
    /// The hits chosen in „Suchen und schwärzen“ and the reason for all of them (null: none; wish of 4.11.2026); with <paramref name="Auto"/>
    /// a hit of a pattern (E-Mail, IBAN …) gets the code its set names for that kind instead.
    /// </summary>
    public sealed record RedactionPick(List<RedactionHit> Hits, Core.Pdf.Editing.ReasonChoice? Reason, Core.Pdf.Editing.RedactionCodeSet? Auto = null)
    {
        /// <summary>The reason of a hit: its kind's code if chosen so, else the reason for all.</summary>
        public Core.Pdf.Editing.ReasonChoice? For(RedactionHit hit) =>
            Auto?.For(hit.Kind) is { } code ? Core.Pdf.Editing.ReasonChoice.Of(code, Reason?.InBar ?? true) : Reason;
    }

    private bool CheckCanEdit(DocumentViewModel doc)
    {
        if (doc.CanEditElements) return true;
        Message = Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen.");
        return false;
    }

    /// <summary>Lets the user choose resolution and format for „Flachlegen als Bild“ (set by the view).</summary>
    public Func<Task<PdfKnead.Rendering.RasterSettings?>>? PickRaster { get; set; }

    /// <summary>Whether the next save saves the pages as images (shown by the button in the „Sicher“ tab).</summary>
    public bool RasterOnSave => SelectedDocument?.RasterOnSave is not null;

    /// <summary>Schedules „Flachlegen als Bild“ (with settings) or withdraws it again.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ToggleRaster()
    {
        if (SelectedDocument is not { } doc) return;
        if (doc.RasterOnSave is not null)
        {
            doc.RasterOnSave = null;
            ShowNotice(Tr.T("Die Seiten bleiben beim Speichern, wie sie sind."));
        }
        else if (PickRaster is not null && await PickRaster() is { } settings)
        {
            doc.RasterOnSave = settings;
            ShowNotice(Tr.T("Alle Seiten werden beim nächsten Speichern als Bild gesichert."));
        }
        OnPropertyChanged(nameof(RasterOnSave));   // also after cancelling: the button shows the actual state again
    }

    // ------------------------------------------------------------ Exporting

    /// <summary>Lets the user choose resolution and format for the export (set by the view).</summary>
    public Func<RasterPurpose, Task<PdfKnead.Rendering.RasterSettings?>>? PickExportRaster { get; set; }

    /// <summary>Lets the user choose a folder (set by the view): takes the title; returns the folder, or null.</summary>
    public Func<string, Task<string?>>? PickFolder { get; set; }

    /// <summary>Commits the input box that has the focus (the right-hand panel binds on focus loss, and Ctrl+S leaves the focus there; set by the view).</summary>
    public Action? CommitFocusedInput { get; set; }

    /// <summary>Before saving, exporting, redacting: commits pending input, first that in the right-hand panel, then that on the page.</summary>
    private void CommitInputs(DocumentViewModel doc)
    {
        CommitFocusedInput?.Invoke();
        doc.CommitPendingEdit();
    }

    /// <summary>File → Export → PDF as images: a new file; the tab keeps showing the opened one.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task ExportImagePdf()
    {
        if (SelectedDocument is not { } doc || PickExportRaster is null || PickSavePath is null) return;
        CommitInputs(doc);
        if (!await RedactFirstAsync(doc, Tr.T("Exportieren"))) return;
        if (await PickExportRaster(RasterPurpose.ExportPdf) is not { } settings) return;
        var suggested = $"{Path.GetFileNameWithoutExtension(doc.FileName)} ({Tr.T("Bild")}).pdf";
        var path = await PickSavePath(Tr.T("PDF als Bild exportieren"), suggested, SaveFolder(doc));
        if (path is null) return;
        if (Documents.Any(d => SamePath(d.Path, Path.GetFullPath(path))))
        {
            Message = Tr.T("{0} ist geöffnet — zum Exportieren einen anderen Namen wählen.", Path.GetFileName(path));
            return;
        }
        Busy = Tr.T("Exportiere {0} …", Path.GetFileName(path));
        try
        {
            var result = await doc.ExportImagePdfAsync(path, settings);
            BackstageOpen = false;
            ShowNotice(Tr.T("Exportiert: {0} (PDF {1}, {2})", Path.GetFileName(path), result.Version, DocumentViewModel.FormatSize(result.Length)));
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Message = Tr.T("Exportieren fehlgeschlagen: {0}", Failure.Text(ex));
        }
        finally
        {
            Busy = null;
        }
    }

    /// <summary>Shows „Als PDF/A speichern“ (set by the view): returns the conforming copy, or null.</summary>
    public Func<PdfAViewModel, Task<PdfAResult?>>? PickPdfA { get; set; }

    /// <summary>
    /// File → Export → „Als PDF/A speichern …“ (KONZEPT.md, section 5.21): a copy written and checked for PDF/A; only a conforming one is
    /// saved, otherwise the window names the reasons. The tab keeps showing the opened file.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task ExportPdfA()
    {
        if (SelectedDocument is not { } doc || PickPdfA is null || PickSavePath is null) return;
        // PDF/A allows no encryption: an unprotected copy only where the protection could be changed anyway (as for „Speichern unter“)
        if (!doc.CanChangeProtection)
        {
            Message = Tr.T("Dieses Dokument ist geschützt, PDF/A erlaubt keinen Schutz — erst mit dem Passwort für die Rechte öffnen oder Sicher → Einschränkungen entfernen.");
            return;
        }
        CommitInputs(doc);
        if (!await RedactFirstAsync(doc, Tr.T("Exportieren"))) return;
        var notes = new List<string>();
        if (doc.IsProtected) notes.Add(Tr.T("Der Passwortschutz entfällt — PDF/A erlaubt keine Verschlüsselung."));
        if (doc.HasSignatures) notes.Add(Tr.T("Die digitalen Unterschriften gelten in der Kopie nicht mehr."));
        // a module may advise a part (e.g. one that keeps the attachments it looks after)
        var advice = await ModulePdfAAdviceAsync(doc);
        if (await PickPdfA(new PdfAViewModel(doc.WritePdfAAsync, notes, doc.PdfAFiguresAsync, doc.HasAttachments, doc.Document.FileSize, advice)) is not { } result) return;
        var suggested = $"{Path.GetFileNameWithoutExtension(doc.FileName)} (PDF-A).pdf";
        var path = await PickSavePath(Tr.T("Als PDF/A speichern"), suggested, SaveFolder(doc));
        if (path is null) return;
        if (Documents.Any(d => SamePath(d.Path, Path.GetFullPath(path))))
        {
            Message = Tr.T("{0} ist geöffnet — zum Exportieren einen anderen Namen wählen.", Path.GetFileName(path));
            return;
        }
        try
        {
            await Task.Run(() => PdfFileSaver.Save(path, output =>
            {
                output.Write(result.Bytes);
                return new PdfSaveResult("1.7", result.Bytes.Length, 0, false);
            }));
            BackstageOpen = false;
            // saved anyway (the user's choice in the window): said plainly — the file carries the identification but breaks the rules
            var saved = result.Conforms
                ? Tr.T("Als PDF/A gespeichert: {0} ({1})", Path.GetFileName(path), DocumentViewModel.FormatSize(result.Bytes.Length))
                : Tr.T("Gespeichert, erfüllt PDF/A aber nicht ({0} Verstöße): {1}", result.Violations.Count, Path.GetFileName(path));
            // fonts that were not in the file: embedded as PDF-Knead shows them — said, since the original font may look different; PDF/A-1:
            // what was translucent is an image
            if (result.SubstitutedFonts.Count > 0) saved += " — " + Tr.T("ersetzt, wie PDF-Knead sie zeigt: {0}", string.Join(", ", result.SubstitutedFonts));
            if (result.FlattenedPages.Count > 0)
                saved += " — " + (result.FlattenedPages.Count == 1 ? Tr.T("Durchscheinendes auf 1 Seite als Bild")
                    : Tr.T("Durchscheinendes auf {0} Seiten als Bild", result.FlattenedPages.Count));
            // claims of accessibility: left out after a break, declarations PDF/A-2/3 cannot carry (docs/ABLAUFPLAN.md, 6.1 step 7)
            if (result.AccessibilityClaimRemoved)
                saved += " — " + Tr.T("Die Datei war als barrierefrei gekennzeichnet (PDF/UA). Nach diesen Änderungen gilt das nicht mehr ({0}); die Kennzeichnung wurde entfernt.",
                    string.Join(", ", result.AccessibilityBreaks));
            if (result.DeclarationsLeftOut.Count > 0)
                saved += " — " + Tr.T("Erklärungen der Datei weggelassen (in dieser PDF/A-Stufe nicht möglich): {0}", string.Join(", ", result.DeclarationsLeftOut));
            ShowNotice(saved);
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Message = Tr.T("Exportieren fehlgeschlagen: {0}", Failure.Text(ex));
        }
    }

    /// <summary>
    /// Chooses how pages are saved as images (<see cref="PageImagesWindow"/>): the last settings, the number of all and of the selected pages;
    /// null if cancelled.
    /// </summary>
    public Func<PdfKnead.Core.Imaging.PageImageSettings, int, int, Task<PdfKnead.Core.Imaging.PageImageSettings?>>? PickPageImages { get; set; }

    /// <summary>
    /// File → Export → pages as images (KONZEPT §5.25, „Als Bild speichern“): all or the selected pages of the page panel as PNG, JPEG,
    /// TIFF (also in one file) or WebP in a folder.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task ExportImages()
    {
        if (SelectedDocument is not { } doc || PickPageImages is null || PickFolder is null) return;
        // Unprotected images from a document that does not permit copying (extracting text and graphics): refused, as for copying
        if (!doc.CopyAllowed)
        {
            Message = Tr.T("Dieses Dokument erlaubt kein Herauslösen von Inhalten als Bild (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen.");
            return;
        }
        CommitInputs(doc);
        if (!await RedactFirstAsync(doc, Tr.T("Exportieren"))) return;
        var selected = doc.SelectedIndices().Order().ToList();
        var start = UserSettings.Current.PageImages ?? new PdfKnead.Core.Imaging.PageImageSettings();
        if (await PickPageImages(start, doc.History.Pages.Count, selected.Count) is not { } settings) return;
        UserSettings.Update(s => s with { PageImages = settings });
        if (await PickFolder(Tr.T("Ordner für die Bilder")) is not { } folder) return;
        Busy = Tr.T("Exportiere {0} …", doc.FileName);
        try
        {
            var files = await doc.ExportImagesAsync(folder, settings, settings.SelectedOnly && selected.Count > 0 ? selected : null);
            BackstageOpen = false;
            ShowNotice(files.Count == 1 ? Tr.T("1 Bild exportiert nach {0}", folder) : Tr.T("{0} Bilder exportiert nach {1}", files.Count, folder));
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Message = Tr.T("Exportieren fehlgeschlagen: {0}", Failure.Text(ex));
        }
        finally
        {
            Busy = null;
        }
    }

    /// <summary>
    /// What a digital signature covers without anybody seeing it: content of the pages nobody sees and data beside them that can act or
    /// be shown (not the document properties, bookmarks or comments — they are no part of what the pages show).
    /// </summary>
    private static readonly CleanupItems[] HiddenBeforeSigning =
    [
        CleanupItems.InvisibleText, CleanupItems.SuspiciousText, CleanupItems.HiddenLayers, CleanupItems.HiddenContent, CleanupItems.HiddenImageParts,
        CleanupItems.Scripts, CleanupItems.Attachments, CleanupItems.PrivateData, CleanupItems.FieldValueMismatch,
    ];

    /// <summary>
    /// Before signing digitally (KONZEPT.md, section 5.17; wish of 29.10.2026, „sign what you see“): what the signature would cover without
    /// anybody seeing it — found as „Dokument bereinigen“ finds it — is named, with „Erst bereinigen“ (its window, then signing), „Trotzdem
    /// signieren“ or cancel. Nothing found: no question.
    /// </summary>
    /// <returns>Whether to go on signing.</returns>
    private async Task<bool> ConfirmHiddenBeforeSigningAsync(DocumentViewModel doc)
    {
        if (AskChoice is null) return true;
        var pages = doc.History.Pages.ToArray();
        IReadOnlyList<CleanupFinding> found;
        try
        {
            found = await doc.WorkAsync(() => Task.Run(() => DocumentCleanup.Inspect(doc.Document, pages)));
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            return true;   // the check itself failed (a damaged part): signing goes on as before
        }
        // „Was niemand sieht“ without a count: only hidden fields that stay (something can show them) — nothing hidden in the signature
        var hidden = found.Where(f => HiddenBeforeSigning.Contains(f.Item) && !(f.Item == CleanupItems.HiddenContent && f.Count <= 0)).ToList();
        if (hidden.Count == 0) return true;
        var list = string.Join("\n", hidden.Select(f => "• " + CleanupLabels.Of(f.Item)));
        var answer = await AskChoice(Tr.T("Das Dokument enthält Verborgenes"),
            Tr.T("Die digitale Signatur deckt auch ab, was niemand sieht:\n{0}\n\nAm sichersten erst bereinigen, dann signieren.", list),
            Tr.T("Erst bereinigen"), Tr.T("Trotzdem signieren"));
        if (answer == 2) return true;
        if (answer != 1 || PickCleanup is null || !Documents.Contains(doc)) return false;
        if (await PickCleanup(found, CleanupItems.None) is not { } chosen || chosen == CleanupItems.None || !Documents.Contains(doc)) return false;
        Busy = Tr.T("Bereinige {0} …", doc.FileName);
        try
        {
            await doc.CleanAsync(chosen);
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Message = Tr.T("Bereinigen fehlgeschlagen: {0}", Failure.Text(ex));
            return false;
        }
        finally
        {
            Busy = null;
        }
        return true;
    }

    /// <summary>Selection for cleanup (set by the view): takes the findings and what is already scheduled; returns the chosen items, or null.</summary>
    public Func<IReadOnlyList<CleanupFinding>, CleanupItems, Task<CleanupItems?>>? PickCleanup { get; set; }

    /// <summary>
    /// „Sicher“ tab → „Dokument bereinigen“: the area on the right with what was found (KONZEPT.md, section 5.18); a second click closes it.
    /// Removing happens with „Bereinigen“ in the area (<see cref="ApplyCleanupCommand"/>).
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CleanDocument()
    {
        if (SelectedDocument is not { } doc) return;
        if (doc.IsCleanupOpen)
        {
            doc.CloseCleanup();
            return;
        }
        // one area at a time on the right: tools with an area of their own end
        if (Tool is not ViewTool.SelectText and not ViewTool.Pan) Tool = ViewTool.SelectText;
        if (doc.IsNotesOpen) doc.CloseNotes();
        try
        {
            await doc.OpenCleanupAsync(ModuleCleanupFinders());
        }
        catch (OperationCanceledException)
        {
            // the tab has been closed in the meantime
        }
    }

    /// <summary>„Bereinigen“ in the area: removes what is ticked right away within the document, except the places kept (Ctrl+Z reverts it).</summary>
    [RelayCommand]
    private async Task ApplyCleanup()
    {
        if (SelectedDocument is not { IsCleanupOpen: true } doc) return;
        // the rows of modules (an e-invoice): which embedded files stay (decision (4) of 15.11.2026)
        var (chosen, keptFiles, metadataStays) = doc.CleanupChoice;
        if (chosen == CleanupItems.None)
        {
            doc.CloseCleanup();
            return;
        }
        var keep = doc.CleanupKept.Where(k => chosen.HasFlag(k.Item)).ToList();
        Busy = Tr.T("Bereinige {0} …", doc.FileName);
        NotifyPageCommands();
        try
        {
            await doc.CleanAsync(chosen, keep: keep, keptAttachments: keptFiles, attachedMetadataStays: metadataStays);
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Message = Tr.T("Bereinigen fehlgeschlagen: {0}", Failure.Text(ex));
            return;
        }
        finally
        {
            Busy = null;
            NotifyPageCommands();
        }
        doc.CloseCleanup();
        ShowNotice(keep.Count == 0
            ? Tr.T("Bereinigt. Strg+Z nimmt es zurück — endgültig entfernt wird es beim Speichern.")
            : Tr.T("Bereinigt, {0} Stellen bleiben wie gewählt. Strg+Z nimmt es zurück — endgültig entfernt wird es beim Speichern.", keep.Count));
    }

    /// <summary>Closes the area „Dokument bereinigen“ without removing anything.</summary>
    [RelayCommand]
    private void CloseCleanup() => SelectedDocument?.CloseCleanup();

    /// <summary>
    /// Asks for confirmation that something becomes permanent (set by the view): takes title, notice and whether to offer „Auch als
    /// Bild sichern“; returns cancel, save or save as images.
    /// </summary>
    public Func<string, string, bool, Task<FixingReply>>? ConfirmFixing { get; set; }

    /// <summary>Title of the question for <see cref="FixingNotice"/>.</summary>
    /// <param name="doc">The document.</param>
    /// <returns>The title.</returns>
    public static string FixingTitle(DocumentViewModel doc) =>
        doc.RasterOnSave is not null ? Tr.T("Flachlegen als Bild?") : doc.FlattenOnSave ? Tr.T("Flachlegen?") : Tr.T("Unterschrift festschreiben?");

    /// <summary>
    /// Notice before saving if something is written permanently into the page and can no longer be changed
    /// afterwards: signatures always, all elements when flattening, all pages when saving as images.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <returns>The notice, or null if nothing becomes permanent.</returns>
    public static string? FixingNotice(DocumentViewModel doc)
    {
        if (doc.RasterOnSave is not null)
            return Tr.T("Beim Speichern werden alle Seiten zu Bildern: Text lässt sich danach nicht mehr kopieren, durchsuchen oder ändern; Lesezeichen, Formularfelder, Kommentare und Metadaten entfallen.");
        var elements = doc.History.Pages.SelectMany(p => p.Elements).Where(e => e is not ForeignAnnotation).ToList();
        if (doc.FlattenOnSave && elements.Count > 0)
            return Tr.T("Beim Flachlegen werden alle eingefügten Elemente fest in die Seiten geschrieben. Danach lassen sie sich nicht mehr verschieben, ändern oder löschen.");
        // Edited text is written into the page but stays editable in PDF-Knead (see KONZEPT.md, section 5.14), so no question
        return elements.Count(e => !ElementStore.StaysEditable(e) && e is not RichTextElement) switch
        {
            0 => null,
            1 => Tr.T("Die Unterschrift wird beim Speichern fest in die Seite geschrieben. Danach lässt sie sich nicht mehr verschieben, ändern oder löschen."),
            var n => Tr.T("Die {0} Unterschriften werden beim Speichern fest in die Seiten geschrieben. Danach lassen sie sich nicht mehr verschieben, ändern oder löschen.", n),
        };
    }

    /// <summary>Asks „Jetzt zuerst vollständig schwärzen oder weiter markieren?“ (set by the view).</summary>
    public Func<RedactionQuestion, Task<RedactionReply>>? ConfirmRedaction { get; set; }

    /// <summary>Question when saving over the file after redaction (set by the view).</summary>
    public Func<RedactedSaveQuestion, Task<RedactedSaveAnswer>>? ConfirmRedactedSave { get; set; }

    /// <summary>
    /// The gate before everything that leaves the document („Schwärzen zuerst“): if redactions are marked but not applied, the
    /// question „Jetzt zuerst vollständig schwärzen oder weiter markieren?“ appears. Redacting applies them within the open document
    /// (one undo step) and then continues; keeping marking cancels. There is no way out without redacting. Beforehand the document is
    /// searched for redacted text that still appears elsewhere; on request those occurrences are redacted too.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="action">What happens afterwards (translated, e.g. „Drucken“); null = only redact („Schwärzungen anwenden“ button).</param>
    /// <returns>true: it may continue (nothing marked, or redacted now).</returns>
    private async Task<bool> RedactFirstAsync(DocumentViewModel doc, string? action)
    {
        CommitInputs(doc);
        if (doc.RedactionCount == 0) return true;
        List<RedactionHit> unredacted;
        List<RedactionElsewhere> elsewhere;
        List<RedactionCode> codes;
        List<int> ocrPages;
        HashSet<string> phrases;
        var uncheckedCodes = new List<int>();
        // attachments with a password of their own: asked for first, so that they are searched too (cancelled: reported as not searchable)
        if (doc.Document.Security is { AttachmentsLocked: true }) await UnlockAttachmentsAsync(doc);
        Busy = Tr.T("Prüfe, ob geschwärzter Text noch woanders steht …");
        try
        {
            unredacted = await doc.FindUnredactedAsync();
            elsewhere = await doc.FindElsewhereAsync();
            // places a module knows of (e.g. an e-invoice: named always, its XML writes the values otherwise; decided on 15.11.2026)
            elsewhere.AddRange(await ModuleRedactionElsewhereAsync(doc));
            var pages = doc.History.Pages.ToArray();
            ocrPages = await doc.WorkAsync(() => Task.Run(() => DocumentCleanup.PagesWithInvisibleText(pages)));
            phrases = await doc.RedactedPhrasesAsync();
            // QR and bar codes: covered only in part (still readable thanks to their error correction?) or holding redacted text
            var progress = new Progress<(int Done, int Of)>(p => Busy = Tr.T("Prüfe QR- und Barcodes … Seite {0} von {1}", Math.Min(p.Done + 1, p.Of), p.Of));
            codes = await doc.FindCodesToRedactAsync(progress, uncheckedCodes);
        }
        catch (OperationCanceledException)
        {
            return false;   // the tab has been closed in the meantime
        }
        finally
        {
            Busy = null;
        }
        var question = new RedactionQuestion(doc.RedactionCount, doc.RedactedPageCount, action, CanChooseImages: doc.RasterOnSave is null)
        {
            Unredacted = unredacted,
            Elsewhere = elsewhere,
            Codes = codes,
            UncheckedCodePages = uncheckedCodes,
            OcrPages = ocrPages,
            RedactedPages = doc.History.Pages.Select((p, i) => (p, i)).Where(x => x.p.Elements.Any(e => e is RedactionElement)).Select(x => x.i).ToHashSet(),
            ShowHit = doc.ShowRedactionHitAsync,
            OcrSize = UserSettings.Current.OcrSize,
            OwnReasons = doc.History.Pages.Sum(p => p.Elements.Count(e => e is RedactionElement { Hidden: false, Reason: not null })),
            CodedReasons = doc.History.Pages.Sum(p => p.Elements.Count(e => e is RedactionElement { Hidden: false, Reason: not null, ReasonCode: not null })),
            ReasonSuggestions = [.. RedactionReasons.Suggestions.Concat(UserSettings.Current.RedactionReasons ?? []).Distinct()],
            RedactionName = UserSettings.Current.RedactionName ?? "",
            RedactedPhrases = phrases,
            BarsWithoutReason = [.. doc.History.Pages.SelectMany(p => p.Elements.OfType<RedactionElement>())
                .Where(m => m is { Hidden: false, Reason: null } && m.PageAreas.Any())
                .Select(m => m.PageAreas.MaxBy(a => a.Width * a.Height))],
        };
        // Without a view (tests without an answer) never silently unredacted: redact
        var reply = ConfirmRedaction is { } confirm ? await confirm(question) : new RedactionReply(RedactionAnswer.Apply);
        await doc.ShowRedactionHitAsync(null);
        if (reply.Answer == RedactionAnswer.KeepMarking) return false;
        RememberReasons(reply.Reasons);
        if (!Documents.Contains(doc)) return false;
        // Chosen hits: visible text gets a bar. Invisible text only goes, without a bar — it often lies beside what it reads, and a bar there
        // would cover something else; what goes with the old text recognition anyway needs no mark.
        var chosenHits = unredacted.Where(h => h.Whole ? reply.AlsoUnredacted : reply.AlsoWords).ToList();
        if (chosenHits.Any(h => !h.Invisible)) await doc.RedactHitsAsync(chosenHits.Where(h => !h.Invisible));
        if (reply.Codes is { Count: > 0 } chosen) doc.RedactCodes(chosen);
        // Locations outside the page text: remove them at the same time via „Dokument bereinigen“
        var cleanup = CleanupItems.None;
        if (reply.CleanElsewhere)
            foreach (var place in elsewhere) cleanup |= place.Cleanup;
        // What nobody sees and the old text recognition go along — in the whole document or only on the pages with redactions
        // (KONZEPT.md, section 5.6, „Schwärzen-Umbau“); vaguely invisible text is left to „Verborgenes schwärzen“
        var hidden = (reply.CleanHidden ? CleanupItems.HiddenContent | CleanupItems.HiddenLayers | CleanupItems.HiddenImageParts : CleanupItems.None)
                     | (reply.RemoveOcr && ocrPages.Count > 0 ? CleanupItems.InvisibleText : CleanupItems.None);
        CleanupScope? scoped = null;
        if (reply.WholeDocument) cleanup |= hidden;
        else if (hidden != CleanupItems.None)
            scoped = new CleanupScope(hidden, doc.History.Pages.Select((p, i) => (p, i)).Where(x => x.p.Elements.Any(e => e is RedactionElement))
                .Select(x => x.i).ToHashSet());
        // the pages for a new text recognition (KONZEPT.md, section 5.6, step c): whose old one goes, and those that become images
        var redactedPages = doc.History.Pages.Select((p, i) => (p, i)).Where(x => x.p.Elements.Any(e => e is RedactionElement)).Select(x => x.i).ToHashSet();
        var recognise = new SortedSet<int>();
        if (reply.NewOcr is not null)
        {
            if (reply.RemoveOcr) recognise.UnionWith(reply.WholeDocument ? ocrPages : ocrPages.Where(redactedPages.Contains));
            if (reply.Answer == RedactionAnswer.ApplyAsImages) recognise.UnionWith(redactedPages);
        }
        // after the pages above: a mark of invisible text does not make a page one „mit Schwärzung“
        var invisibleHits = chosenHits.Where(h => h.Invisible && !question.GoesAlong(h, reply.RemoveOcr, reply.WholeDocument)).ToList();
        if (invisibleHits.Count > 0) await doc.RedactHitsAsync(invisibleHits, hiddenOnly: true);
        Busy = Tr.T("Schwärze {0} …", doc.FileName);
        NotifyPageCommands();
        try
        {
            await doc.ApplyRedactionsAsync(reply.Answer == RedactionAnswer.ApplyAsImages, cleanup, reply.HideLengths, scoped: scoped, reasons: reply.Reasons);
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Message = Tr.T("Schwärzen fehlgeschlagen: {0}", Failure.Text(ex));
            return false;
        }
        finally
        {
            Busy = null;
            NotifyPageCommands();
        }
        ShowNotice(Tr.T("Geschwärzt. Strg+Z nimmt es zurück — endgültig gelöscht wird der Inhalt erst beim Speichern."));
        if (reply.NewOcr is { } size && recognise.Count > 0 && Documents.Contains(doc))
        {
            UserSettings.Update(s => s with { OcrSize = size });
            // from the redacted pages: the recognition sees only what stays visible; what follows (saving, printing …) waits for it
            await RecognizeAsync(doc, [.. recognise], size, Tr.T("Neue Texterkennung"));
        }
        return true;
    }

    /// <summary>
    /// The name for the protocol is remembered as entered (cleared, it is forgotten), and an own reason goes into the list for next time
    /// (KONZEPT.md, section 5.6, B-14). Without reasons nothing changes.
    /// </summary>
    private static void RememberReasons(RedactionReasonSettings? reasons)
    {
        if (reasons is null) return;
        UserSettings.Update(s => s with
        {
            RedactionName = reasons.Protocol != RedactionProtocolPlace.None || reasons.Notes ? reasons.Name : s.RedactionName,
            RedactionReasons = reasons.DefaultReason is { } own && !RedactionReasons.Suggestions.Contains(own)
                ? [own, .. (s.RedactionReasons ?? []).Where(r => r != own).Take(19)]
                : s.RedactionReasons,
        });
    }

    /// <summary>
    /// „Sicher“ tab → „Schwärzungen anwenden“: redacts within the open document (Ctrl+Z reverts it); saving works as usual, over the
    /// file only after a confirmation.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task ApplyRedactions()
    {
        if (SelectedDocument is not { } doc) return;
        CommitInputs(doc);
        if (doc.RedactionCount == 0)
        {
            Message = Tr.T("Es ist nichts zum Schwärzen markiert — erst mit „Bereich schwärzen“, „Text schwärzen“ oder „Suchen und schwärzen“ markieren.");
            return;
        }
        await RedactFirstAsync(doc, null);
    }

    /// <summary>
    /// Saving over the opened file after redaction within the document: as a new file (default), overwrite, or cancel.
    /// <c>Proceed</c>: save to <paramref name="path"/> as requested; otherwise it is done, either cancelled or saved as a new file
    /// (<c>Saved</c>).
    /// </summary>
    private async Task<(bool Proceed, bool Saved)> ConfirmOverwriteRedactedAsync(DocumentViewModel doc, string path,
        PdfKnead.Core.Pdf.Writing.CompressionSettings? compression)
    {
        if (!doc.HasAppliedRedactions || !SavesOverOpenFile(doc, path)) return (true, false);
        // Without a view (tests without an answer): overwrite nothing
        var answer = ConfirmRedactedSave is { } confirm
            ? await confirm(new RedactedSaveQuestion(doc.FileName, SyncFolder.Contains(path)))
            : RedactedSaveAnswer.Cancel;
        switch (answer)
        {
            case RedactedSaveAnswer.Overwrite:
                return (true, false);
            case RedactedSaveAnswer.SaveAsNew when PickSavePath is not null:
                var suggested = $"{Path.GetFileNameWithoutExtension(doc.FileName)} ({Tr.T("geschwärzt")}).pdf";
                if (await PickSavePath(Tr.T("Speichern unter"), suggested, SaveFolder(doc)) is not { } target) return (false, false);
                // The same file chosen after all: that is the decision to overwrite
                return SamePath(doc.Path, Path.GetFullPath(target))
                    ? (true, false)
                    : (false, await SaveDocumentAsync(doc, target, doc.SaveVersion, compression, confirmed: true));
            default:
                return (false, false);
        }
    }

    /// <summary>Whether <paramref name="path"/> is the file the document was opened from (a new document has none).</summary>
    private static bool SavesOverOpenFile(DocumentViewModel doc, string path) => !doc.IsUntitled && SamePath(doc.Path, Path.GetFullPath(path));

    /// <summary>Asks before a save that invalidates digital signatures (set by the view); without a view nothing is saved.</summary>
    public Func<SignedSaveQuestion, Task<SignedSaveAnswer>>? ConfirmSignedSave { get; set; }

    /// <summary>
    /// Saving rewrites the whole file, so the signed bytes change and every intact digital signature becomes invalid (KONZEPT.md,
    /// section 5.17, step S0). Asks first; over the open file the default is to save as a new file and keep the signed original.
    /// </summary>
    /// <returns>Whether to continue with this save, and whether the document was saved elsewhere instead.</returns>
    /// <param name="failure">Why appending failed after all (then the file is written anew if confirmed); null = asked beforehand.</param>
    private async Task<(bool Proceed, bool Saved)> ConfirmSignedSaveAsync(DocumentViewModel doc, string path,
        PdfKnead.Core.Pdf.Writing.CompressionSettings? compression, string? failure = null)
    {
        var count = PdfKnead.Core.Pdf.Signatures.DigitalSignatures.CountIntact(doc.Document);
        if (count == 0) return (true, false);
        var overwrites = SavesOverOpenFile(doc, path);
        // Without a view (tests without an answer): save nothing
        var answer = ConfirmSignedSave is { } confirm
            ? await confirm(new SignedSaveQuestion(doc.FileName, count, overwrites,
                doc.History.Pages.Any(p => p.Elements.Any(e => e.Digital is null && e is SignatureElement or InkElement or ImageElement { IsSignature: true })),
                failure ?? (doc.AppendsToSigned(compression, out var reason) ? null : reason)))
            : SignedSaveAnswer.Cancel;
        switch (answer)
        {
            case SignedSaveAnswer.Save:
                return (true, false);
            case SignedSaveAnswer.SaveAsNew when PickSavePath is not null:
                var suggested = $"{Path.GetFileNameWithoutExtension(doc.FileName)} ({Tr.T("bearbeitet")}).pdf";
                if (await PickSavePath(Tr.T("Speichern unter"), suggested, SaveFolder(doc)) is not { } target) return (false, false);
                // The same file chosen after all: that is the decision to overwrite
                return SamePath(doc.Path, Path.GetFullPath(target))
                    ? (true, false)
                    : (false, await SaveDocumentAsync(doc, target, doc.SaveVersion, compression, confirmed: true, rewrite: failure is not null));
            default:
                return (false, false);
        }
    }

    /// <summary>Saves with progress in the status bar; errors appear in the message bar.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="path">Target file.</param>
    /// <param name="version">PDF version to write.</param>
    /// <returns>true if saved.</returns>
    public Task<bool> SaveDocumentAsync(DocumentViewModel doc, string path, string version) => SaveDocumentAsync(doc, path, version, null);

    /// <param name="compression">Compression (Save As); null = none.</param>
    /// <param name="confirmed">The questions about overwriting (after redaction, signed documents) have already been answered.</param>
    /// <param name="rewrite">Write anew even if the changes look appendable (appending failed before).</param>
    private async Task<bool> SaveDocumentAsync(DocumentViewModel doc, string path, string version,
        PdfKnead.Core.Pdf.Writing.CompressionSettings? compression, bool confirmed = false, bool rewrite = false)
    {
        CommitInputs(doc);
        // A new file is a copy that leaves the document: marked redactions are applied first, on every way there (Save As, a new
        // document, „Als neue Datei“ after redacting or with signatures). Only over the opened file do marks stay marks.
        if (!SavesOverOpenFile(doc, path) && !await RedactFirstAsync(doc, Tr.T("Speichern unter"))) return false;
        var signing = PdfKnead.Core.Pdf.Signatures.SignedSaving.HasPending(doc.History.Pages);
        if (signing && doc.DigitalSigningRefusal is { } refusal)
        {
            // said before any certificate or password is asked for; encrypted files are signed otherwise (No. 543)
            Message = refusal;
            return false;
        }
        if (signing && doc.History.Pages.Sum(p => p.Elements.Count(e => e.Digital is not null)) > 1
            && doc.History.Pages.Any(p => p.Elements.Any(e => e.Digital?.Certify == PdfKnead.Core.Pdf.Signatures.CertificationLevel.NoChanges)))
        {
            // the further signature would itself be a change the certification forbids
            Message = Tr.T("Eine Unterschrift zertifiziert mit „keine Änderungen“ — weitere Unterschriften sind dann nicht möglich. Bitte eine andere Stufe wählen oder die übrigen Unterschriften entfernen.");
            return false;
        }
        if (signing && doc.RasterOnSave is not null)
        {
            // An image of the page cannot carry the signature field; signing afterwards works
            Message = Tr.T("Digitale Signaturen und „Flachlegen als Bild“ gehen nicht zusammen: erst als Bild speichern, dann signieren.");
            return false;
        }
        // sign what you see: hidden content first (cleaned here it counts for the questions below)
        if (signing && !await ConfirmHiddenBeforeSigningAsync(doc)) return false;
        // Only appending to a signed file (filled-in fields, comments, signatures): its signatures stay valid, so no question about them
        var appendOnly = !rewrite && doc.AppendsToSigned(compression);
        // „Flachlegen als Bild“ scheduled: the pages become images and thus leave the document, so apply the marks first
        if (doc.RasterOnSave is not null && !await RedactFirstAsync(doc, Tr.T("Flachlegen als Bild"))) return false;
        if (!confirmed)
        {
            var (proceed, saved) = await ConfirmOverwriteRedactedAsync(doc, path, compression);
            if (!proceed) return saved;
            if (!appendOnly)
            {
                (proceed, saved) = await ConfirmSignedSaveAsync(doc, path, compression);
                if (!proceed) return saved;
            }
        }
        var imageChosen = false;
        if (FixingNotice(doc) is { } notice && ConfirmFixing is { } confirm)
        {
            switch (await confirm(FixingTitle(doc), notice, doc.RasterOnSave is null && !signing))
            {
                case FixingReply.Cancel:
                    return false;
                case FixingReply.SaveAsImage:
                    // Like „Flachlegen als Bild“ in the „Sicher“ tab (default 300 dpi), only for this save; marked redactions first
                    doc.RasterOnSave = new PdfKnead.Rendering.RasterSettings();
                    imageChosen = true;
                    if (!await RedactFirstAsync(doc, Tr.T("Flachlegen als Bild")))
                    {
                        doc.RasterOnSave = null;
                        return false;
                    }
                    break;
            }
        }
        // The same file in another tab: do not overwrite what is displayed there
        if (Documents.Any(d => !ReferenceEquals(d, doc) && SamePath(d.Path, Path.GetFullPath(path))))
        {
            Message = Tr.T("{0} ist in einem anderen Reiter geöffnet — erst dort schließen.", Path.GetFileName(path));
            return false;
        }
        SigningCertificate? certificate = null;
        // certificates chosen for single signatures in the signature window (docs/ABLAUFPLAN.md, 9.4.0, step 2; No. 722): each one opened once
        var chosenFor = new Dictionary<SigningCertificateChoice, SigningCertificate>();
        void ReleaseChosen()
        {
            foreach (var chosen in chosenFor.Values) chosen.Dispose();
            chosenFor.Clear();
        }
        try
        {
            if (signing)
            {
                var digital = doc.History.Pages.SelectMany(p => p.Elements).Select(e => e.Digital).OfType<Core.Pdf.Editing.DigitalSigning>().ToList();
                foreach (var choice in digital.Select(d => d.With).OfType<SigningCertificateChoice>().Distinct())
                {
                    if (await SigningCertificateAsync(choice) is not { } opened)
                    {
                        ReleaseChosen();
                        return false;
                    }
                    chosenFor[choice] = opened;
                }
                if (digital.Any(d => d.With is null)) certificate = await SigningCertificateAsync();
            }
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            ReleaseChosen();
            Message = Tr.T("Das Zertifikat lässt sich nicht lesen: {0}", e.Message);
            return false;
        }
        if (signing && certificate is null && chosenFor.Count == 0) return false;
        if (signing && certificate is null && doc.History.Pages.SelectMany(p => p.Elements).Any(e => e.Digital is { With: null }))
        {
            ReleaseChosen();
            return false;
        }
        // an expired or unsuitable certificate is told before anything is asked or written (No. 526)
        foreach (var check in chosenFor.Values.Prepend(certificate).OfType<SigningCertificate>())
            if (SigningCertificates.Unusable(check.Certificate, DateTimeOffset.Now) is { } unusable)
            {
                Message = Tr.T("Mit diesem Zertifikat lässt sich nicht unterschreiben: {0}", unusable);
                certificate?.Dispose();
                ReleaseChosen();
                return false;
            }
        Func<Core.Pdf.Editing.DigitalSigning, SigningCertificate?>? certificateFor = chosenFor.Count == 0 ? null
            : d => d.With is { } with && chosenFor.TryGetValue(with, out var found) ? found : null;
        // a time stamp only when switched on with a service entered (the only connection of the signature)
        ITimestampAuthority? timestamps = null;
        if (signing)
        {
            var (go, authority) = await SigningTimestampsAsync();
            if (!go)
            {
                certificate?.Dispose();
                ReleaseChosen();
                return false;
            }
            timestamps = authority;
        }
        var archive = Services.UserSettings.Current.SignatureArchive || ArchiveForTests;
        // an archive time stamp follows the validation data, never the bare signature (B-LTA needs B-LT; No. 527)
        var revocation = signing ? Revocation() ?? (archive && timestamps is not null ? ArchiveRevocation() : null) : null;
        Busy = Tr.T("Speichere {0} …", Path.GetFileName(path));
        NotifyPageCommands();
        try
        {
            // Redactions still marked stay marks (work in progress); whatever leaves the document has passed through the gate
            var options = doc.SaveOptions(version) with { Compression = compression };
            // without the service's answer nothing is written: ask whether to sign without a time stamp
            async Task<PdfSaveResult?> Save(bool append)
            {
                try
                {
                    return await doc.SaveAsync(path, options, certificate, append, timestamps, revocation, archive, certificateFor);
                }
                catch (PdfKnead.Core.Pdf.PdfAttachmentsLockedException) when (AskAttachmentPassword is not null)
                {
                    // an attachment with a password of its own cannot be carried over without it: asked for, then saved again (No. 459)
                    Busy = null;
                    if (!await UnlockAttachmentsAsync(doc)) throw;
                    Busy = Tr.T("Speichere {0} …", Path.GetFileName(path));
                    return await Save(append);
                }
                catch (TimestampException ex) when (timestamps is not null)
                {
                    if (ex is TimestampLoginException) ForgetTimestampLogin();   // asked again next time
                    Busy = null;
                    if (AskChoice is null || await AskChoice(Tr.T("Kein Zeitstempel"),
                            ex.Message + " " + Tr.T("Ohne Zeitstempel ist die Unterschrift gültig, aber ihre Zeit bestätigt kein Dritter."),
                            Tr.T("Ohne Zeitstempel signieren"), null) != 1)   // the other answer is „Abbrechen“: nothing written
                        return null;
                    timestamps = null;
                    Busy = Tr.T("Speichere {0} …", Path.GetFileName(path));
                    return await doc.SaveAsync(path, options, certificate, append, revocation: revocation, certificateFor: certificateFor);
                }
            }
            PdfSaveResult? written;
            try
            {
                written = await Save(appendOnly);
            }
            catch (NotAppendableException ex) when (appendOnly)
            {
                // Appending failed after all (nothing written): as with a change that rewrites the file — ask, then write anew
                Busy = null;
                var (proceed, saved) = await ConfirmSignedSaveAsync(doc, path, compression, ex.Message);
                if (!proceed) return saved;
                Busy = Tr.T("Speichere {0} …", Path.GetFileName(path));
                written = await Save(append: false);
            }
            if (written is not { } result) return false;
            UserSettings.Update(s => s.WithRecent(doc.Path));
            LoadRecent();
            ShowNotice((result.VersionRaised
                ? result.RaisedFor is { } raisedFor
                    ? Tr.T("Gespeichert als PDF {0} (der Inhalt braucht mindestens diese Version: {1}).", result.Version, raisedFor)
                    : Tr.T("Gespeichert als PDF {0} (die Verschlüsselung braucht mindestens diese Version).", result.Version)
                : Tr.T("Gespeichert: {0} (PDF {1}, {2})", doc.FileName, result.Version, DocumentViewModel.FormatSize(result.Length))) +
                (result.TabOrderByStructure ? " — " + Tr.T("In getaggten Dateien folgt die Tab-Reihenfolge für Bildschirmleser der Struktur.") : "") +
                // declarations of extensions PDF {0} cannot carry went (docs/FEHLERGEDAECHTNIS.md, No. 488)
                (result.DroppedExtensions.Count > 0
                    ? " — " + Tr.T("Ankündigungen von Erweiterungen entfernt, die PDF {0} nicht tragen kann: {1}", result.Version, string.Join(", ", result.DroppedExtensions))
                    : "") +
                // the document parts no longer fitting the pages written went (docs/FEHLERGEDAECHTNIS.md, No. 790)
                (result.DocumentPartsDropped ? " — " + Tr.T("Die Dokumentteile passten nicht mehr zur geänderten Seitenfolge und wurden entfernt.") : "") +
                // the XMP of the document unreadable: its date of change not updated (docs/FEHLERGEDAECHTNIS.md, No. 552)
                (result.XmpNotUpdated ? " — " + Tr.T("Die XMP-Metadaten sind nicht lesbar; das Änderungsdatum darin wurde nicht nachgeführt.") : ""));
            // a file identified as PDF/A that the copy is not any more: the identification went, said and kept on screen (No. 554)
            if (result.PdfAClaimRemovedFor is { } broken)
                Message = Tr.T("Die Datei war als PDF/A gekennzeichnet; mit {0} ist sie es nicht mehr. Die Kennung wurde entfernt. Für eine gültige PDF/A-Datei „Als PDF/A speichern“ wählen.", broken);
            // a file claimed as accessible that the copy is not any more: the claims went, said and kept on screen (docs/ABLAUFPLAN.md, 6.1)
            if (result.AccessibilityClaimRemoved)
                Message = Tr.T("Die Datei war als barrierefrei gekennzeichnet (PDF/UA). Nach diesen Änderungen gilt das nicht mehr ({0}); die Kennzeichnung wurde entfernt.",
                    string.Join(", ", result.AccessibilityBreaks));
            // a file protected against changes by a MAC (ISO/TS 32004): written without it, said and kept on screen (No. 546)
            if (result.IntegrityProtectionDropped)
                Message = result.Security is not null
                    ? Tr.T("Gespeichert. Die Datei war zusätzlich gegen Veränderung geschützt (Integritätsschutz nach ISO/TS 32004). Diesen Schutz kann PDF-Knead noch nicht erneuern — die Datei ist weiter verschlüsselt, aber ohne ihn.")
                    : Tr.T("Gespeichert. Die Datei war zusätzlich gegen Veränderung geschützt (Integritätsschutz nach ISO/TS 32004). Die Kopie ist ohne Verschlüsselung und ohne diesen Schutz.");
            // what the long-term validation could not obtain stays on screen: the signature is valid, but checkable later only with a connection
            if (result.LongTermMissing.Count > 0)
                Message = Tr.T("Unterschrieben, aber ohne vollständige Langzeitprüfung: für {0} war kein Sperrstatus zu bekommen (keine Verbindung oder die Zertifizierungsstelle antwortet nicht).",
                    string.Join(", ", result.LongTermMissing));
            OnPropertyChanged(nameof(WindowTitle));
            return true;
        }
        catch (CertificateRevokedException ex)
        {
            Message = Tr.T("Nicht unterschrieben: {0}", ex.Message);
            return false;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // The key refused (for example a smart card or a certificate from the system store); nothing was written
            Message = Tr.T("Signieren fehlgeschlagen: {0}", ex.Message);
            return false;
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Message = Tr.T("Speichern fehlgeschlagen: {0}", Failure.Text(ex));
            if (imageChosen) doc.RasterOnSave = null;   // chosen only for this save
            return false;
        }
        finally
        {
            certificate?.Dispose();
            ReleaseChosen();
            Busy = null;
            NotifyPageCommands();
        }
    }

    /// <summary>Before closing: save or discard unsaved changes, or cancel.</summary>
    /// <param name="doc">The document.</param>
    /// <returns>true = may close.</returns>
    public async Task<bool> ConfirmDiscardAsync(DocumentViewModel doc)
    {
        CommitInputs(doc);
        if (!doc.IsModified || AskSaveChanges is null) return true;
        SelectedDocument = doc;
        return await AskSaveChanges(doc.FileName) switch
        {
            SaveChoice.Save => doc.IsUntitled ? await SaveUntitledAsync(doc) : await SaveDocumentAsync(doc, doc.Path, doc.SaveVersion),
            SaveChoice.Discard => true,
            _ => false,
        };
    }

    /// <summary>Closing the window: asks for every modified document.</summary>
    /// <returns>false = cancelled.</returns>
    public async Task<bool> ConfirmCloseAllAsync()
    {
        foreach (var doc in Documents.ToList())
            if (!await ConfirmDiscardAsync(doc)) return false;
        return true;
    }
}
