using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>Tabs of the ribbon. „Datei“ is not a tab; it opens the File area.</summary>
public enum RibbonTab
{
    Start,
    Pages,
    Insert,
    Form,
    Signature,
    Secure,
    View,
    About,

    /// <summary>A tab of a module (<see cref="MainViewModel.SelectedModuleTab"/> tells which).</summary>
    Module,
}

/// <summary>Pages of the File area (backstage), listed on the left.</summary>
public enum BackstagePage
{
    Open,
    Save,
    SaveAs,
    Export,
    Print,
    Compress,
    ScanProfiles,
    Properties,
    Settings,
    Info,
}

/// <summary>Tabs of the left sidebar.</summary>
public enum SidebarTab
{
    Pages,
    Bookmarks,
    Fields,
}

/// <summary>An entry in „Zuletzt geöffnet“; <see cref="Exists"/> is checked when the list is loaded (network paths count as existing).</summary>
public sealed record RecentFile(string Path, bool Exists)
{
    public bool Missing => !Exists;
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

/// <summary>
/// Main window: open documents and the state of ribbon, sidebar and File area.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        var s = UserSettings.Current;
        RibbonCollapsed = s.RibbonCollapsed;
        SidebarVisible = s.SidebarVisible;
        LoadRecent();
        LoadModuleTabs();
        Settings.ModulesChanged += LoadModuleTabs;
        // linking fields by their name asks first (the question is the window's)
        FieldProperties.AskChoice = (heading, message, first, second) => AskChoice?.Invoke(heading, message, first, second) ?? Task.FromResult(1);
        Documents.CollectionChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(HasDocuments));
            OnPropertyChanged(nameof(HasNoDocuments));
            WatchFirstChanges(e);
        };
        // Window title and texts change with the language
        Tr.Instance.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(PageStatus));
        };
        // Non-embedded CJK font without the „Schriften-CJK“ folder (reported while drawing, also from background threads). Held
        // weakly: the event is static and must not keep a window alive
        var self = new WeakReference<MainViewModel>(this);
        void OnMissingCjk(Core.Pdf.Document.PdfDocument _)
        {
            if (!self.TryGetTarget(out var vm))
            {
                Core.Pdf.Document.PdfDocument.MissingCjkFontsFound -= OnMissingCjk;
                return;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => vm.Message = MissingCjkFontsMessage);
        }
        Core.Pdf.Document.PdfDocument.MissingCjkFontsFound += OnMissingCjk;
        Settings.ShowPermissions = () => ShowPermissions?.Invoke() ?? Task.CompletedTask;
        Settings.ImportPolicy = ImportPolicyAsync;
    }

    /// <summary>Message shown when a PDF needs a Chinese, Japanese or Korean font but the „Schriften-CJK“ folder is missing.</summary>
    internal static string MissingCjkFontsMessage =>
        Tr.T("Diese PDF verwendet chinesische, japanische oder koreanische Schrift, die nicht eingebettet ist. Sie kann nicht dargestellt werden, weil der Ordner „Schriften-CJK“ neben PDF-Knead fehlt.");

    public SettingsViewModel Settings { get; } = new();

    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    public ObservableCollection<RecentFile> RecentFiles { get; } = [];

    public bool HasDocuments => Documents.Count > 0;

    public bool HasNoDocuments => Documents.Count == 0;

    public bool HasRecentFiles => RecentFiles.Count > 0;

    public string WindowTitle => SelectedDocument is { } doc
        ? $"{doc.Title} – PDF-Knead {AppVersion.Display}"
        : $"PDF-Knead {AppVersion.Display}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(HasSelection))]
    public partial DocumentViewModel? SelectedDocument { get; set; }

    public bool HasSelection => SelectedDocument is not null;

    [ObservableProperty] public partial RibbonTab RibbonTab { get; set; } = RibbonTab.Start;

    /// <summary>Whether the ribbon shows only the tabs; a click on a tab expands it temporarily.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RibbonContentVisible))]
    public partial bool RibbonCollapsed { get; set; }

    /// <summary>Whether the collapsed ribbon is temporarily open (floating over the document).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RibbonContentVisible))]
    public partial bool RibbonPeek { get; set; }

    public bool RibbonContentVisible => !RibbonCollapsed || RibbonPeek;

    [ObservableProperty] public partial bool SidebarVisible { get; set; }

    [ObservableProperty] public partial SidebarTab SidebarTab { get; set; } = SidebarTab.Pages;

    [ObservableProperty] public partial bool BackstageOpen { get; set; }

    [ObservableProperty] public partial BackstagePage BackstagePage { get; set; } = BackstagePage.Open;

    /// <summary>Message in the bar below the ribbon (e.g. „keine PDF-Datei“); empty = none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string Message { get; set; } = "";

    public bool HasMessage => Message.Length > 0;

    partial void OnRibbonCollapsedChanged(bool value)
    {
        RibbonPeek = false;
        UserSettings.Update(s => s with { RibbonCollapsed = value });
    }

    partial void OnSidebarVisibleChanged(bool value) => UserSettings.Update(s => s with { SidebarVisible = value });

    // ------------------------------------------------------------ Opening

    /// <summary>
    /// Asks for the password of a file: takes the file name and whether the last one was wrong, returns
    /// the password or null (cancelled). Set by the view; without a view (tests) nothing is asked.
    /// </summary>
    public Func<string, bool, Task<string?>>? AskPassword { get; set; }

    /// <summary>
    /// Asks for the password of attachments encrypted with one of their own (the document itself is open): takes the file name and whether
    /// the last one was wrong, returns the password or null (cancelled). Set by the view; without a view (tests) nothing is asked.
    /// </summary>
    public Func<string, bool, Task<string?>>? AskAttachmentPassword { get; set; }

    /// <summary>
    /// Attachments encrypted with a password of their own: asks for it until it opens them, the question is cancelled or an attachment
    /// does not decrypt even with the right one. Saving broke off with them, and the search before redacting could only report them as
    /// not searchable (decided on 4.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 459).
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <returns>True if the attachments are open now.</returns>
    internal async Task<bool> UnlockAttachmentsAsync(DocumentViewModel doc)
    {
        if (doc.Document.Security is not { AttachmentsLocked: true }) return true;
        if (AskAttachmentPassword is null) return false;
        for (var wrong = false; ; wrong = true)
        {
            if (await AskAttachmentPassword(doc.FileName, wrong) is not { } password) return false;
            switch (doc.Document.UnlockAttachments(password))
            {
                case PdfKnead.Core.Pdf.Document.PdfDocument.AttachmentUnlock.Opened:
                    return true;
                case PdfKnead.Core.Pdf.Document.PdfDocument.AttachmentUnlock.NotDecryptable:
                    Message = Tr.T("Ein Dateianhang lässt sich auch mit dem Passwort nicht entschlüsseln — er bleibt gesperrt.");
                    return false;
            }
        }
    }

    /// <summary>Text while a file is being opened (shown in the status bar); null = idle.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageStatus))]
    public partial string? Busy { get; set; }

    /// <summary>Text on the left of the status bar.</summary>
    public string PageStatus => Busy ?? SelectedDocument?.PageStatus ?? Tr.T("Seite – von –");

    /// <summary>
    /// Opens files as tabs in the background, so even large files do not block the UI. Files
    /// already open are only selected. Encrypted files ask for the password; whatever cannot be
    /// read is reported in the message bar.
    /// </summary>
    /// <param name="paths">The files to open.</param>
    public async Task OpenAsync(IEnumerable<string> paths)
    {
        var problems = new List<string>();
        var opened = new List<DocumentViewModel>();
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            if (Documents.FirstOrDefault(d => SamePath(d.Path, full)) is { } open)
            {
                SelectedDocument = open;
                continue;
            }

            Busy = Tr.T("Öffne {0} …", Path.GetFileName(full));
            try
            {
                if (await LoadAsync(full) is ({ } document, var usedPassword, var certificates))
                {
                    var doc = new DocumentViewModel(full, document, usedPassword) { Tool = Tool, OpenCertificates = certificates };
                    Documents.Add(doc);
                    SelectedDocument = doc;
                    opened.Add(doc);
                    UserSettings.Update(s => s.WithRecent(full));
                }
            }
            catch (Exception ex) when (Failure.Handled(ex))
            {
                problems.Add(Tr.T("{0}: {1}", Path.GetFileName(full), Failure.Text(ex)));
            }
            finally
            {
                Busy = null;
            }
        }

        LoadRecent();
        Message = string.Join("  ·  ", problems);
        if (problems.Count == 0) BackstageOpen = false;
        // an unencrypted wrapper (ISO 32000-2, 7.6.7): asked after the message bar is set, so a payload that cannot be opened is said there
        foreach (var doc in opened) await OfferEncryptedPayloadAsync(doc);
    }

    /// <summary>
    /// Reads a file; if it is password-protected, asks until the password fits or the user cancels (then null); if it is encrypted for
    /// recipients and no certificate of the store opens it, asks for a certificate file (docs/FEHLERGEDAECHTNIS.md, No. 1117).
    /// </summary>
    private async Task<(PdfDocument? Document, string? Password, IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2>? Certificates)> LoadAsync(string path)
    {
        string? password = null;
        var wrong = false;
        List<System.Security.Cryptography.X509Certificates.X509Certificate2>? certificates = null;
        while (true)
        {
            try
            {
                return (await Task.Run(() => PdfDocument.Open(path, password, certificates)), password, certificates);
            }
            catch (PdfPasswordException ex)
            {
                if (AskPassword is null) throw;
                wrong = ex.WrongPassword;
                password = await AskPassword(Path.GetFileName(path), wrong);
                if (password is null) return (null, null, null);
            }
            catch (PdfRecipientException ex)
            {
                if (await AskRecipientCertificateAsync(Path.GetFileName(path), ex, again: certificates is not null) is not { } certificate)
                    return (null, null, null);
                (certificates ??= []).Add(certificate);
            }
        }
    }

    [RelayCommand]
    private async Task OpenRecent(RecentFile file)
    {
        if (!File.Exists(file.Path))
        {
            // Do not remove it automatically; perhaps only the network drive is unavailable at the moment. The × is for that
            Message = Tr.T("{0} wurde am Speicherort nicht gefunden.", file.FileName);
            LoadRecent();
            return;
        }
        await OpenAsync([file.Path]);
    }

    /// <summary>Removes an entry from the recent list; only from the list, the file stays.</summary>
    [RelayCommand]
    private void RemoveRecent(RecentFile file)
    {
        UserSettings.Update(s => s.WithoutRecent(file.Path));
        LoadRecent();
    }

    /// <summary>Clears the whole recent list; only the list, no files.</summary>
    [RelayCommand]
    private void ClearRecent()
    {
        UserSettings.Update(s => s with { RecentFiles = [] });
        LoadRecent();
    }

    /// <summary>Closes a tab; asks first if there are unsaved changes.</summary>
    [RelayCommand]
    private async Task CloseDocument(DocumentViewModel? doc)
    {
        doc ??= SelectedDocument;
        // Work still running (saving, export …): wait for it first, since it still reads from the files
        if (doc is { IsWorking: true })
        {
            Busy = Tr.T("Warte, bis {0} fertig gespeichert ist …", doc.FileName);
            try
            {
                await doc.WhenIdle();
            }
            finally
            {
                Busy = null;
            }
        }
        if (doc is null || !await ConfirmDiscardAsync(doc)) return;
        var index = Documents.IndexOf(doc);
        if (index < 0) return;
        Documents.Remove(doc);
        if (ReferenceEquals(SelectedDocument, doc) || SelectedDocument is null)
            SelectedDocument = Documents.Count == 0 ? null : Documents[Math.Min(index, Documents.Count - 1)];
        await doc.CloseAsync();
    }

    /// <summary>„Diese Anmerkung ist vom Ersteller gesperrt. Trotzdem ändern?“ — „Trotzdem ändern“ / „Abbrechen“ (No. 541).</summary>
    private async Task<bool> ConfirmLockedChangeAsync(string question) =>
        AskChoice is not { } ask || await ask(Tr.T("Gesperrte Anmerkung"), question, Tr.T("Trotzdem ändern"), null) == 1;   // without a view: as before

    partial void OnSelectedDocumentChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnDocumentChanged;
            oldValue.MessageRequested -= OnDocumentMessage;
            oldValue.SignatureEditRequested -= OnSignatureEditRequested;
            oldValue.AddressLinkActivated -= OnAddressLink;
            oldValue.FlattenSuggested -= OnFlattenSuggested;
            oldValue.FieldSelectionChanged -= OnFieldSelectionChanged;
        }
        if (newValue is not null)
        {
            newValue.PropertyChanged += OnDocumentChanged;
            newValue.MessageRequested += OnDocumentMessage;
            newValue.SignatureEditRequested += OnSignatureEditRequested;
            newValue.AddressLinkActivated += OnAddressLink;
            newValue.FlattenSuggested += OnFlattenSuggested;
            newValue.FieldSelectionChanged += OnFieldSelectionChanged;
            newValue.ConfirmLockedChange ??= ConfirmLockedChangeAsync;
            newValue.AskChoices ??= AskForDocumentAsync;
        }
        OnPropertyChanged(nameof(PageStatus));
        NotifyDocumentCommands();
        SyncFormatFromSelection();
        RefreshFormDesign();
        FlattenPromptVisible = false;
        OnPropertyChanged(nameof(FlattenOnSave));
        OnPropertyChanged(nameof(RasterOnSave));
        // Properties and Save As exist only with a document
        if (newValue is null && BackstagePage is BackstagePage.Properties or BackstagePage.SaveAs) BackstagePage = BackstagePage.Open;
        ShowModuleTabFor(newValue);
    }

    /// <summary>Re-evaluates all commands that depend on the document (different document, permissions changed).</summary>
    private void NotifyDocumentCommands()
    {
        foreach (var command in new IRelayCommand[] { ToggleNotesCommand, ZoomInCommand, ZoomOutCommand, FitWidthCommand, FitPageCommand, RotateLeftCommand, RotateRightCommand, SetLayoutCommand, SetToolCommand, OpenSearchCommand, CopyTextCommand, PrintCommand, CompressCommand, InsertSignatureCommand, InsertImageCommand, InsertShapeCommand, InsertCodeCommand, InsertNoteCommand, MeasureCommand, ToggleFormDesignCommand, ToggleEditCommand, InsertFormFieldCommand, ToggleTabOrderCommand, TestFormCommand, RecognizeFieldsCommand })
            command.NotifyCanExecuteChanged();
        NotifyPageCommands();
    }

    private void OnDocumentMessage(string message) => Message = message;

    /// <summary>
    /// A web or mail address of a link: always with the question and its warnings first (decision J); an address PDF-Knead does not open
    /// (file:, relative, javascript: …) is only named (docs/FEHLERGEDAECHTNIS.md, No. 566).
    /// </summary>
    private async void OnAddressLink(AddressLink link, string? target)
    {
        if (target is null)
        {
            Message = Tr.T("Diese Adresse öffnet PDF-Knead nicht: {0}", link.Uri);
            return;
        }
        if (ConfirmOpenAddress is null || OpenAddress is null) return;
        if (!await ConfirmOpenAddress(target, link.Check.Warnings)) return;
        await OpenAddress(target);
    }

    private void OnDocumentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DocumentViewModel.CurrentPage) or nameof(DocumentViewModel.PageStatus):
                OnPropertyChanged(nameof(PageStatus));
                break;
            case nameof(DocumentViewModel.SelectedImages):
                MoveLayerCommand.NotifyCanExecuteChanged();
                break;
            case nameof(DocumentViewModel.HasTextSelection):
                CopyTextCommand.NotifyCanExecuteChanged();
                break;
            case nameof(DocumentViewModel.HasRestrictions) or nameof(DocumentViewModel.CanLiftRestrictions) or nameof(DocumentViewModel.IsProtected):
                NotifyDocumentCommands();
                break;
            case nameof(DocumentViewModel.Title):
                OnPropertyChanged(nameof(WindowTitle));
                break;
            case nameof(DocumentViewModel.SelectedElement):
                SyncFormatFromSelection();
                ElementProperties.Refresh(SelectedDocument);
                CodeProperties.Refresh(SelectedDocument);
                RichTextProperties.Refresh(SelectedDocument);
                OnPropertyChanged(nameof(ShowTextProperties));   // after ElementProperties, which it gives way to
                MoveLayerCommand.NotifyCanExecuteChanged();
                break;
            case nameof(DocumentViewModel.MarkPicked):
                ElementProperties.Refresh(SelectedDocument);   // the mark just drawn picked by a click: now with its panel
                break;
            case nameof(DocumentViewModel.RichEditor):
                RichTextProperties.Refresh(SelectedDocument);
                OnPropertyChanged(nameof(ShowTextProperties));
                break;
            case nameof(DocumentViewModel.RasterOnSave):
                OnPropertyChanged(nameof(RasterOnSave));
                OnPropertyChanged(nameof(WindowTitle));
                break;
            case nameof(DocumentViewModel.FlattenOnSave):
                OnPropertyChanged(nameof(FlattenOnSave));
                OnPropertyChanged(nameof(WindowTitle));
                NotifyPageCommands();
                break;
            case nameof(DocumentViewModel.Tool) when sender is DocumentViewModel { Tool: var tool }:
                Tool = tool;   // the view has switched back (after an element was drawn)
                break;
            // Tab order ended (button, Esc, mode off): its hint goes too (otherwise it would stay and shift the page)
            case nameof(DocumentViewModel.ShowTabOrder) when sender is DocumentViewModel { ShowTabOrder: false } && Message == TabOrderHint:
                Message = "";
                break;
            case nameof(DocumentViewModel.IsWorking):
                NotifyPageCommands();
                break;
            case nameof(DocumentViewModel.CanUndo) or nameof(DocumentViewModel.CanRedo) or nameof(DocumentViewModel.PageCount):
                NotifyPageCommands();
                RefreshFormDesign();
                break;
        }
    }

    private void LoadRecent()
    {
        RecentFiles.Clear();
        foreach (var f in UserSettings.Current.Recent) RecentFiles.Add(new RecentFile(f, IsOnNetwork(f) || File.Exists(f)));
        OnPropertyChanged(nameof(HasRecentFiles));
    }

    /// <summary>
    /// Whether the path is on the network (\\server, mapped network drive). If so, the list does not check whether the file exists:
    /// that would set up a network connection including authentication at every start and freeze the UI when the server is missing.
    /// Whether the file is there becomes apparent on opening.
    /// </summary>
    private static bool IsOnNetwork(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return true;
        if (!OperatingSystem.IsWindows() || Path.GetPathRoot(path) is not { Length: >= 2 } root || root[1] != ':') return false;
        try
        {
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ------------------------------------------------------------ Ribbon, sidebar, File area

    [RelayCommand]
    private void SelectRibbonTab(RibbonTab tab)
    {
        // a module's tab is chosen with SelectModuleTab; without one there is nothing to show
        if (tab == RibbonTab.Module && SelectedModuleTab is null) return;
        // Collapsed: the same tab again closes the floating ribbon
        if (RibbonCollapsed) RibbonPeek = !(RibbonPeek && RibbonTab == tab);
        RibbonTab = tab;
    }

    [RelayCommand]
    private void ToggleRibbon() => RibbonCollapsed = !RibbonCollapsed;

    [RelayCommand]
    private void ToggleSidebar() => SidebarVisible = !SidebarVisible;

    [RelayCommand]
    private void ShowSidebarTab(SidebarTab tab)
    {
        SidebarTab = tab;
        SidebarVisible = true;
    }

    /// <summary>Shows the window „Über PDF-Knead“ (set by the view).</summary>
    public Func<AboutViewModel, Task>? ShowAbout { get; set; }

    /// <summary>Opens the window „Über PDF-Knead“ with the license texts.</summary>
    [RelayCommand]
    private async Task ShowLicenses()
    {
        if (ShowAbout is not null) await ShowAbout(new AboutViewModel());
    }

    [RelayCommand]
    private void ShowBackstage(BackstagePage page)
    {
        BackstagePage = page;
        BackstageOpen = true;
        RibbonPeek = false;
        if (page == BackstagePage.Settings) Settings.ReloadLanguages();
        if (page == BackstagePage.ScanProfiles) ScanProfiles.Reload();
        // Save As: default 1.7; files at PDF 2.0 or later stay 2.0 — also where pages of such a file were inserted (ISO 32000-2, I.2, sentence 17)
        if (page == BackstagePage.SaveAs && SelectedDocument is { } doc)
            SelectedVersion = PdfKnead.Core.Pdf.Writing.PdfWriter.CompareVersions(doc.NewestSourceVersion, "2.0") >= 0 ? VersionOptions[^1] : VersionOptions[1];
    }

    [RelayCommand]
    private void CloseBackstage() => BackstageOpen = false;

    [RelayCommand]
    private void DismissMessage() => Message = "";

    // ------------------------------------------------------------ View of the selected document

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ZoomIn() => SetZoom(DocumentViewModel.ZoomStep(SelectedDocument!.Zoom, 1));   // next multiple of 5 %

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ZoomOut() => SetZoom(DocumentViewModel.ZoomStep(SelectedDocument!.Zoom, -1));

    private void SetZoom(double zoom)
    {
        SelectedDocument!.ZoomMode = ZoomMode.Custom;
        SelectedDocument.Zoom = Math.Clamp(zoom, DocumentViewModel.MinZoom, DocumentViewModel.MaxZoom);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void FitWidth() => SelectedDocument!.Fit(ZoomMode.FitWidth);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void FitPage() => SelectedDocument!.Fit(ZoomMode.FitPage);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RotateLeft() => SelectedDocument!.Rotation = (SelectedDocument.Rotation + 270) % 360;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RotateRight() => SelectedDocument!.Rotation = (SelectedDocument.Rotation + 90) % 360;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SetLayout(PageLayout layout) => SelectedDocument!.PageLayout = layout;

    /// <summary>Bookmark clicked: jump to its page.</summary>
    [RelayCommand]
    private void GoToOutline(OutlineItem? item)
    {
        // Bookmarks refer to pages of the opened file; after rearranging, the page is somewhere else
        if (item?.PageIndex is not { } page || SelectedDocument is not { } doc || doc.PositionOf(page) is not { } position) return;
        // with the view of the destination (place and zoom; docs/FEHLERGEDAECHTNIS.md, No. 564)
        if (item.Target is { } target) doc.ScrollTo(position, target);
        else doc.ScrollToPage(position);
    }

    /// <summary>Whether lines thinner than a pixel are drawn as hairlines (for all documents, saved in the settings).</summary>
    public bool EnhanceThinLines
    {
        get => UserSettings.Current.EnhanceThinLines;
        set
        {
            if (value == EnhanceThinLines) return;
            UserSettings.Update(s => s with { EnhanceThinLines = value });
            foreach (var doc in Documents) doc.Renderer.EnhanceThinLines = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Overprint preview for all documents, saved in the settings.</summary>
    public bool OverprintPreview
    {
        get => UserSettings.Current.OverprintPreview;
        set
        {
            if (value == OverprintPreview) return;
            UserSettings.Update(s => s with { OverprintPreview = value });
            foreach (var doc in Documents) doc.Renderer.OverprintPreview = value;
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void DismissProblems()
    {
        if (SelectedDocument is { } doc) doc.ProblemsDismissed = true;
    }

    // ------------------------------------------------------------ Text: tool, copy, search

    /// <summary>Mouse tool, the same for all documents.</summary>
    [ObservableProperty] public partial ViewTool Tool { get; set; } = ViewTool.SelectText;

    partial void OnToolChanged(ViewTool value)
    {
        foreach (var doc in Documents)
        {
            doc.Tool = value;
            // a tool with an area of its own on the right takes the place of „Dokument bereinigen“
            if (value is not ViewTool.SelectText and not ViewTool.Pan && doc.IsCleanupOpen) doc.CloseCleanup();
            if (value is not ViewTool.SelectText and not ViewTool.Pan && doc.IsFieldProposalsOpen) doc.CloseFieldProposals();
            if (value is not ViewTool.SelectText and not ViewTool.Pan && doc.IsNotesOpen) doc.CloseNotes();
            if (value is not ViewTool.SelectText and not ViewTool.Pan && doc.IsAccessibilityOpen) doc.CloseAccessibility();
        }
        OnPropertyChanged(nameof(IsEditTool));
        OnPropertyChanged(nameof(ModeBannerVisible));
        OnPropertyChanged(nameof(IsHiddenTool));
        ElementProperties.Refresh(SelectedDocument);   // a selected redaction mark shows its reason only outside the redaction tools
        RefreshFormDesign();
        OnPropertyChanged(nameof(ActiveShape));
        DeleteElementCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Chooses a tool; a second click on the active tool switches it off (back to „Text markieren“).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SetTool(ViewTool tool)
    {
        // a tool the function file of the organization switches off: checked when it is switched on
        if (tool != Tool && PolicyIdOf(tool) is { } function && !EnsureAllowed(function)) return;
        Tool = tool == Tool ? ViewTool.SelectText : tool;
    }

    /// <summary>Whether „Bearbeiten“ is on (mode bar at the top, the tool's panel on the right). See KONZEPT.md, section 5.22.</summary>
    public bool IsEditTool => Tool == ViewTool.Edit;

    /// <summary>
    /// „Code ändern …“: the code read becomes a code element in its place (the old one goes as when redacting), selected with its panel
    /// (KONZEPT.md, section 5.19, step 3).
    /// </summary>
    [RelayCommand]
    private async Task ReplaceBarcode()
    {
        if (SelectedDocument is not { InspectedBarcode: not null } doc) return;
        if (await doc.ReplaceInspectedCodeAsync() is not { } code || !Documents.Contains(doc)) return;
        Tool = ViewTool.SelectText;
        doc.SelectedElement = code;
    }

    /// <summary>Copies the content of the code that was read, exactly as it is (control characters included).</summary>
    [RelayCommand]
    private async Task CopyBarcodeText()
    {
        if (SelectedDocument is not { InspectedBarcode: { } code } doc || SetClipboardText is not { } set) return;
        if (doc.BarcodeCopyBlocked() is { } reason)
        {
            Message = reason;
            return;
        }
        await set(code.Code.Code.Text);
        ShowNotice(Tr.T("Inhalt des Codes kopiert."));
    }

    /// <summary>Copies everything the panel shows about the code as text.</summary>
    [RelayCommand]
    private async Task CopyBarcodeInfo()
    {
        if (SelectedDocument is not { InspectedBarcode: { } code } doc || SetClipboardText is not { } set) return;
        if (doc.BarcodeCopyBlocked() is { } reason)
        {
            Message = reason;
            return;
        }
        var text = Core.Pdf.Graphics.ImageInspector.AsText(code.Sections);
        if (code.HasWarnings) text = Tr.T("Hinweise") + ":\n" + code.Warnings + "\n\n" + text;
        await set(text);
        ShowNotice(Tr.T("Angaben zum Code kopiert."));
    }

    /// <summary>Asks before an address from a code is opened: target exactly as opened, the warnings; true = open (set by the view).</summary>
    public Func<string, IReadOnlyList<string>, Task<bool>>? ConfirmOpenAddress { get; set; }

    /// <summary>Opens a web or mail address with the program of the system (set by the view).</summary>
    public Func<string, Task>? OpenAddress { get; set; }

    /// <summary>Updates (KONZEPT §5.26): the hint in the window, shared by all windows of this run.</summary>
    public Services.Updates Updates => Services.Updates.Instance;

    /// <summary>Set by the view: closes the program for „Jetzt neu starten“ once the new version took over.</summary>
    public Action? ShutDownForUpdate { get; set; }

    /// <summary>„Laden“ in the update hint.</summary>
    [RelayCommand]
    public Task DownloadUpdateAsync() => Updates.DownloadAsync();

    /// <summary>„Später“ in the update hint.</summary>
    [RelayCommand]
    public void LaterUpdate() => Updates.Later();

    /// <summary>„Download-Seite öffnen“: when the program cannot replace its own files.</summary>
    [RelayCommand]
    public async Task OpenUpdatePageAsync()
    {
        if (OpenAddress is { } open) await open(Core.Updates.UpdateClient.ReleasesPage);
    }

    /// <summary>
    /// „Jetzt neu starten“: only when no open document has unsaved changes — otherwise a request to save first; then the new version takes
    /// over and the program ends.
    /// </summary>
    [RelayCommand]
    public void RestartForUpdate()
    {
        if (Services.OpenWindows.AnyUnsaved())
        {
            Updates.Message = Tr.T("Bitte zuerst alle Änderungen speichern — dann „Jetzt neu starten“.");
            return;
        }
        if (Updates.RestartNow()) (ShutDownForUpdate ?? Services.OpenWindows.ShutDown)();
    }

    /// <summary>The page where the development of PDF-Knead can be supported (decided on 18.11.2026).</summary>
    public const string DonationAddress = "https://ko-fi.com/systemswap";

    /// <summary>
    /// Opens the donation page in the browser of the system — only after „Unterstützen“ in the donation window, nothing is sent before or by
    /// itself; the payment itself never happens inside the program, so that payment data and logins stay in the browser the user knows.
    /// </summary>
    public async Task DonateAsync()
    {
        if (OpenAddress is { } open) await open(DonationAddress);
    }

    /// <summary>
    /// The donate button at the top right (decided on 22.11.2026): it opens the donation window first — friendlier than the page straight
    /// away. „Unterstützen“ there opens the page and counts as an answer (the window waits 18 months, <see cref="Services.SupportPrompt"/>);
    /// closing it or „Vielleicht später“ leaves the timetable of the window as it is — a window opened on purpose does not move it.
    /// </summary>
    [RelayCommand]
    public async Task ShowDonationWindowAsync()
    {
        if (!DonateButtonVisible) return;
        if (ShowSupportInvitation is not { } show)
        {
            await DonateAsync();
            return;
        }
        if (await show() != true) return;
        Services.SupportPrompt.Answered(Today(), supported: true);
        await DonateAsync();
    }

    /// <summary>
    /// Whether the donate button is there: it goes with the donation window — an organization that switches the window off in its function
    /// file („Spenden-Fenster“, <see cref="Services.SupportPrompt.PolicyId"/>) sees no hint at donating at all.
    /// </summary>
    public bool DonateButtonVisible => Core.Policy.ActivePolicy.Allows(Services.SupportPrompt.PolicyId);

    /// <summary>Opens the address of the code that was read — only after asking, never by itself.</summary>
    [RelayCommand]
    private async Task OpenBarcodeAction()
    {
        if (SelectedDocument?.InspectedBarcode is not { Meaning.Action: { } action } code || ConfirmOpenAddress is null || OpenAddress is null) return;
        // only web and mail addresses; everything else is never handed to the system
        if (!(action.Target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || action.Target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
              || action.Target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)))
            return;
        if (!await ConfirmOpenAddress(action.Target, code.Meaning.Warnings)) return;
        await OpenAddress(action.Target);
    }

    /// <summary>Copies the details of the selected image as text to the clipboard.</summary>
    [RelayCommand]
    private async Task CopyImageInfo()
    {
        if (SelectedDocument?.InspectedImage is not { } image || SetClipboardText is not { } set) return;
        await set(Core.Pdf.Graphics.ImageInspector.AsText(image.Sections));
        ShowNotice(Tr.T("Angaben zum Bild kopiert."));
    }

    /// <summary>Puts text on the clipboard (set by the view).</summary>
    public Func<string, Task>? SetClipboardText { get; set; }

    /// <summary>Puts an image on the clipboard: width, height, RGBA without premultiplied alpha (set by the view).</summary>
    public Func<int, int, byte[], Task>? SetClipboardImage { get; set; }

    /// <summary>Asks where to save an image: title, suggested name (its extension gives the file type), start folder (set by the view).</summary>
    public Func<string, string, string?, Task<string?>>? PickImageSavePath { get; set; }

    /// <summary>Copies the selected image to the clipboard, as it is drawn.</summary>
    [RelayCommand]
    private Task CopyImage() =>
        SelectedDocument is { InspectedImage: not null } doc ? CopyPixelsAsync(doc.ImageExportBlocked, doc.InspectedImagePixelsAsync) : Task.CompletedTask;

    /// <summary>Objects copied with „Bilder bearbeiten“ (B7), for pasting into this or another document; null if none.</summary>
    public CopiedObjects? CopiedObjects
    {
        get;
        private set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Copies the objects chosen with „Bilder bearbeiten“ as one vector object (B7) — kept here for pasting; the clipboard gets a picture of
    /// them for other programs (images: their own pixels).
    /// </summary>
    [RelayCommand]
    private async Task CopyEditedObjects()
    {
        if (SelectedDocument is not { HasSelectedImages: true } doc) return;
        if (doc.SelectedImagesExportBlocked() is { } reason)
        {
            Message = reason;
            return;
        }
        try
        {
            if (await doc.CopySelectedObjectsAsync() is not { } result)
            {
                Message = Tr.T("Die gewählten Objekte lassen sich nicht kopieren.");
                return;
            }
            CopiedObjects = result.Copy;
            var picture = result.Picture ?? (doc.SelectedAreImages ? await doc.SelectedImagesPixelsAsync() : null);
            if (picture is { } p && SetClipboardImage is { } set) await set(p.Width, p.Height, p.Rgba);
            ShowNotice(Tr.T("Kopiert — mit Strg+V einfügen, auch in ein anderes Dokument."));
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Message = Tr.T("Kopieren fehlgeschlagen: {0}", Failure.Text(ex));
        }
    }

    /// <summary>Pastes the objects copied with „Bilder bearbeiten“ as one object (B7); they are chosen afterwards.</summary>
    [RelayCommand]
    private async Task PasteObjects()
    {
        if (SelectedDocument is not { } doc || CopiedObjects is not { } copied) return;
        try
        {
            await doc.PasteObjectsAsync(copied);
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Message = Tr.T("Einfügen fehlgeschlagen: {0}", Failure.Text(ex));
        }
    }

    /// <summary>Copies the images chosen with „Bilder bearbeiten“ (a picture of tiles put together) to the clipboard.</summary>
    [RelayCommand]
    private Task CopyEditedImage() =>
        SelectedDocument is { HasSelectedImages: true } doc ? CopyPixelsAsync(doc.SelectedImagesExportBlocked, doc.SelectedImagesPixelsAsync) : Task.CompletedTask;

    private async Task CopyPixelsAsync(Func<string?> blocked, Func<Task<(int Width, int Height, byte[] Rgba)?>> read)
    {
        if (SetClipboardImage is not { } set) return;
        if (blocked() is { } reason)
        {
            Message = reason;
            return;
        }
        try
        {
            if (await read() is not { } image)
            {
                Message = Tr.T("Das Bild lässt sich nicht lesen.");
                return;
            }
            await set(image.Width, image.Height, image.Rgba);
            ShowNotice(Tr.T("Bild kopiert."));
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Message = Tr.T("Kopieren fehlgeschlagen: {0}", Failure.Text(ex));
        }
    }

    /// <summary>Saves the selected image as a file in its original quality (JPEG and JPEG 2000 unchanged, otherwise PNG or TIFF).</summary>
    [RelayCommand]
    private Task SaveImage() =>
        SelectedDocument is { InspectedImage: { } inspected } doc
            ? SaveImageFileAsync(doc, doc.ImageExportBlocked, doc.PlanInspectedImageAsync, Tr.T("{0} Seite {1} Bild {2}", Path.GetFileNameWithoutExtension(doc.FileName), inspected.Page + 1, inspected.Number),
                doc.SaveInspectedImageAsync)
            : Task.CompletedTask;

    /// <summary>Replaces the picture chosen with „Bilder bearbeiten“ by an image file (fitted into its place).</summary>
    [RelayCommand]
    private async Task ReplaceEditedImage()
    {
        if (SelectedDocument is not { HasSelectedImages: true } doc || PickImageFile is null) return;
        if (await PickImageFile() is not { } path) return;
        var image = await Task.Run(() =>
        {
            try
            {
                return Services.ImageLoader.Load(path, Services.ImageLoader.MaxPictureSide);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        });
        if (image is null)
        {
            Message = Tr.T("Das Bild lässt sich nicht lesen.");
            return;
        }
        Busy = Tr.T("Ersetze das Bild …");
        try
        {
            await doc.ReplaceSelectedImagesAsync(image);
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Message = Tr.T("Dieses Bild lässt sich nicht bearbeiten: {0}", Failure.Text(ex));
        }
        finally
        {
            Busy = null;
        }
    }

    /// <summary>Saves the images chosen with „Bilder bearbeiten“: one image in its original quality, a picture of tiles put together as PNG.</summary>
    [RelayCommand]
    private Task SaveEditedImage() =>
        SelectedDocument is { HasSelectedImages: true } doc
            ? SaveImageFileAsync(doc, doc.SelectedImagesExportBlocked, doc.PlanSelectedImagesAsync, doc.SelectedImagesFileName, doc.SaveSelectedImagesAsync)
            : Task.CompletedTask;

    private async Task SaveImageFileAsync(DocumentViewModel doc, Func<string?> blocked, Func<Task<Core.Pdf.Graphics.ImageExportPlan?>> planned, string name,
        Func<string, string, Task<(Core.Pdf.Graphics.ImageExportPlan Plan, bool Mask)?>> save)
    {
        if (PickImageSavePath is null) return;
        if (blocked() is { } reason)
        {
            Message = reason;
            return;
        }
        Core.Pdf.Graphics.ImageExportPlan? plan;
        try
        {
            plan = await planned();
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Message = Tr.T("Das Bild lässt sich nicht speichern: {0}", Failure.Text(ex));
            return;
        }
        if (plan is null) return;
        if (await PickImageSavePath(Tr.T("Bild speichern"), name + plan.Extension, SaveFolder(doc)) is not { } path) return;
        // beside the image, never over an existing file
        var stem = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + " " + Tr.T("Maske"));
        var maskPath = stem + ".png";
        for (var i = 2; File.Exists(maskPath); i++) maskPath = $"{stem} ({i}).png";
        Busy = Tr.T("Speichere das Bild …");
        try
        {
            if (await save(path, maskPath) is not var (written, mask)) return;
            var notice = Tr.T("Bild gespeichert: {0}", Path.GetFileName(path));
            if (mask) notice += " — " + Tr.T("die Transparenz liegt daneben in {0}", Path.GetFileName(maskPath));
            if (written.ColorsConverted) notice += " — " + Tr.T("Farben in RGB umgerechnet (das Dateiformat kennt den Farbraum des Bildes nicht)");
            if (written.Stencil) notice += " — " + Tr.T("Schablone: die gemalten Stellen sind schwarz");
            ShowNotice(notice);
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Message = Tr.T("Das Bild lässt sich nicht speichern: {0}", Failure.Text(ex));
        }
        finally
        {
            Busy = null;
        }
    }

    private bool CanCopyText => SelectedDocument?.HasTextSelection == true;

    [RelayCommand(CanExecute = nameof(CanCopyText))]
    private async Task CopyText()
    {
        if (SelectedDocument is not { HasTextSelection: true } doc) return;
        if (!doc.CopyAllowed)
        {
            Message = Tr.T("Dieses Dokument erlaubt kein Kopieren von Text (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen.");
            return;
        }
        // Text under redaction marks does not leave the document
        var (text, omitted) = doc.CopyableSelectedText();
        if (SetClipboardText is { } set) await set(text);
        if (omitted) ShowNotice(Tr.T("Kopiert — ohne den Text unter den Schwärzungen."));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenSearch() => SelectedDocument!.OpenSearch();
}
