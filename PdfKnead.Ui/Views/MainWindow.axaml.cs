using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

public partial class MainWindow : Window
{
    /// <summary>Width of the sidebar before it was hidden (the splitter changes it).</summary>
    private GridLength _sidebarWidth = new(232);

    private MainViewModel? _vm;

    // The image last put on the clipboard (see SetClipboardImage)
    private Avalonia.Media.Imaging.WriteableBitmap? _clipboardBitmap;

    public MainWindow()
    {
        InitializeComponent();
        FitStartSize();
        VersionText.Text = $"PDF-Knead {AppVersion.Display}";
        TitleAppText.Text = $"PDF-Knead {AppVersion.Display}";

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        SetUpPageList();

        // Clicking a bookmark jumps to its page.
        OutlineTree.SelectionChanged += (_, _) =>
        {
            if (OutlineTree.SelectedItem is Core.Pdf.Document.OutlineItem item) _vm?.GoToOutlineCommand.Execute(item);
        };

        // the bar of the chosen measurement floats above it (Ablaufplan 7.6 e3)
        Viewer.View.MeasureBarPlaced += PlaceMeasureBar;
        MeasureBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && MeasureBar.IsVisible)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => PlaceMeasureBar(Viewer.View.MeasureBarArea), Avalonia.Threading.DispatcherPriority.Background);
        };

        // Floating panels on the right count as the right edge of the view, so that no page lies under them (KONZEPT.md, section 4.4)
        foreach (var host in FloatingPanels)
            host.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty) UpdateInset();
            };

        // A click into the document closes the temporarily expanded ribbon.
        BodyGrid.AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (_vm is { RibbonPeek: true }) _vm.RibbonPeek = false;
        }, RoutingStrategies.Tunnel);

        // A click outside the panel „Verborgener Inhalt“ and the flyout beside it closes the flyout (a click on another row of the
        // panel shows that row's text instead).
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (_vm?.SelectedDocument is not { IsHiddenTextOpen: true } doc || e.Source is not Visual source) return;
            if (!HiddenContentHost.IsVisualAncestorOf(source) && !HiddenTextHost.IsVisualAncestorOf(source) &&
                source != HiddenContentHost && source != HiddenTextHost)
                doc.IsHiddenTextOpen = false;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>
    /// The start size (XAML) fits the widest ribbon tab with room to spare; on a smaller screen the window opens no larger than its
    /// working area, so that the title bar and the status bar stay visible.
    /// </summary>
    private void FitStartSize()
    {
        if (Screens is not { } screens || (screens.ScreenFromWindow(this) ?? screens.Primary) is not { } screen) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        // a little room for the window frame of the desktop
        Width = Math.Max(MinWidth, Math.Min(Width, area.Width / scale - 16));
        Height = Math.Max(MinHeight, Math.Min(Height, area.Height / scale - 48));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.RichTextProperties.FocusDocument -= Viewer.FocusPage;
        }
        _vm = DataContext as MainViewModel;
        if (_vm is null) return;
        _vm.RichTextProperties.FocusDocument += Viewer.FocusPage;
        _vm.AskPassword = AskPasswordAsync;
        _vm.AskAttachmentPassword = async (name, wrong) => await new PasswordWindow(Tr.T("Dateianhänge mit Passwort"),
            Tr.T("Die Anhänge von {0} sind mit einem eigenen Passwort geschützt.", name), Tr.T("Entsperren"), wrong).ShowDialog<string?>(this);
        StartDiskWatch();
        _vm.PickSignature = async current => await new SignatureWindow(current)
        {
            CanCertify = _vm.SelectedDocument?.CanCertifyWith(current) ?? true,
            DigitalRefusal = _vm.SelectedDocument?.DigitalSigningRefusal,
            Certificates = _vm.SigningChoices(),
        }.ShowDialog<Core.Pdf.Editing.PageElement?>(this);
        _vm.ShowCertificate = async model => await new CertificateWindow(model).ShowDialog(this);
        _vm.AskOwnCertificatePassword = async wrong => await new PasswordWindow(Tr.T("Eigenes Zertifikat"),
            Tr.T("Passwort zum Unterschreiben — bis zum Beenden gemerkt"), Tr.T("Unterschreiben"), wrong).ShowDialog<string?>(this);
        _vm.AskSessionPassword = async (heading, detail, button, wrong) => await new PasswordWindow(heading, detail, button, wrong).ShowDialog<string?>(this);
        _vm.ShowTrustList = async model => await new TrustListWindow(model).ShowDialog(OwnedWindows.LastOrDefault(w => w.IsVisible) ?? this);
        _vm.ShowSignatures = async model => await new SignaturesWindow(model).ShowDialog(this);
        _vm.AskCertificateName = async name => await new FirstCertificateWindow(name).ShowDialog<string?>(this);
        _vm.AskUpdates = async () => await new FirstUpdateWindow().ShowDialog<bool?>(this);
        _vm.PickImageFile = PickImageFileAsync;
        _vm.ShowSignatureFonts = async model => await new SignatureFontsWindow(model).ShowDialog(this);
        _vm.ShowRecognizeText = async model => await new RecognizeTextWindow(model).ShowDialog<bool>(this);
        _vm.ShowOcrText = async (title, text, copy) => await new OcrTextWindow(title, text, copy, _vm.SetClipboardText).ShowDialog(this);
        _vm.ShowAbout = async model => await new AboutWindow(model).ShowDialog(this);
        _vm.ShowFolder = async folder =>
        {
            // Only a local folder in the file manager, no outside connection.
            if (Launcher is { } launcher) await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
        };
        _vm.SetClipboardText = async text =>
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        };
        _vm.ConfirmOpenAddress = async (target, warnings) => await new OpenAddressWindow(target, warnings).ShowDialog<bool?>(this) == true;
        _vm.OpenAddress = async target =>
        {
            // only after the question above; the system decides which program opens it
            if (Launcher is { } launcher && Uri.TryCreate(target, UriKind.Absolute, out var uri)) await launcher.LaunchUriAsync(uri);
        };
        _vm.SetClipboardImage = async (width, height, rgba) =>
        {
            if (Clipboard is not { } clipboard) return;
            var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Unpremul);
            using (var frame = bitmap.Lock())
                for (var y = 0; y < height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(rgba, y * width * 4, frame.Address + y * frame.RowBytes, width * 4);
            await clipboard.SetBitmapAsync(bitmap);
            // The clipboard encodes the bitmap only when another program asks for it: it stays alive until the next image replaces it
            _clipboardBitmap?.Dispose();
            _clipboardBitmap = bitmap;
        };
        _vm.CommitFocusedInput = CommitFocusedInput;
        _vm.PropertyChanged += OnViewModelChanged;
        SetUpSaving();
        ObserveDocument(_vm.SelectedDocument);
        ApplySidebar();
        ApplyCollapseIcon();
    }

    /// <summary>The panels that float over the page on the right (all 320 wide at the same place).</summary>
    private Border[] FloatingPanels =>
        [ElementPropertiesHost, CodePropertiesHost, TextPropertiesHost, RichTextPropertiesHost, ImageInfoHost, FieldFloatingHost, EditHost, HiddenContentHost, MeasureHost,
         .. OpenAreas];

    /// <summary>
    /// The wider areas that stay open until closed („Dokument bereinigen“, „Barrierefreiheit“, „Notizen“, the area of a module): they count
    /// like a tool's panel — the page fits beside them (No. 864: they lay over the page).
    /// </summary>
    private Border[] OpenAreas => [CleanupHost, AccessibilityHost, NotesHost, ModulePanelHost];

    /// <summary>
    /// Places the bar of the chosen measurement centred above it (below, where there is no room above), inside the document area and left of
    /// the panel „Messen“ (Ablaufplan 7.6 e3; docs/FEHLERGEDAECHTNIS.md, No. 586).
    /// </summary>
    private void PlaceMeasureBar(Rect? area)
    {
        // the chosen measurement not laid out (its page not shown): no bar floating where it was (No. 598)
        MeasureBarLayer.IsVisible = area is not null;
        if (area is not { } a || !MeasureBar.IsVisible || Viewer.View.TranslatePoint(a.TopLeft, MeasureBarLayer) is not { } topLeft) return;
        MeasureBar.Measure(Size.Infinity);
        var (w, h) = (MeasureBar.DesiredSize.Width, MeasureBar.DesiredSize.Height);
        var right = MeasureHost.IsVisible ? MeasureBarLayer.Bounds.Width - MeasureHost.Width - MeasureHost.Margin.Right - 8 : MeasureBarLayer.Bounds.Width - 8;
        var x = Math.Clamp(topLeft.X + a.Width / 2 - w / 2, 8, Math.Max(8, right - w));
        var y = topLeft.Y - h - 14;
        if (y < 8) y = Math.Min(topLeft.Y + a.Height + 14, Math.Max(8, MeasureBarLayer.Bounds.Height - h - 8));
        Canvas.SetLeft(MeasureBar, x);
        Canvas.SetTop(MeasureBar, y);
    }

    /// <summary>
    /// Tells the view how much the floating panels cover on the right, and whether the open panel belongs to a tool that stays on
    /// („Bearbeiten“, „Verborgenes schwärzen“, „Text einfügen“): then „Page width“ and „Whole page“ fit beside it. A panel that
    /// only shows a selection leaves the zoom as it is, so the page does not change its size with every click.
    /// </summary>
    private void UpdateInset()
    {
        // „Text einfügen“ returns to „Text markieren“ after placing a text, and the new text stays selected: its panel still counts as
        // the tool's until it closes — otherwise the page would change its size right after every inserted text
        _textPanelOfTool = TextPropertiesHost.IsVisible && (_textPanelOfTool || _vm?.Tool == ViewTool.InsertText);
        // in „Bearbeiten“ every panel at that place belongs to the tool (the chosen text's, element's or code's): the page must not change its
        // size with every click between them
        var anyPanel = FloatingPanels.Any(p => p.IsVisible);
        // as wide as the widest panel open (the areas are wider than the panels of a selection)
        var width = FloatingPanels.Where(p => p.IsVisible).Select(p => p.Width + p.Margin.Right + 8).DefaultIfEmpty(0).Max();
        Viewer.View.SetRightPanel(width,
            EditHost.IsVisible || HiddenContentHost.IsVisible || MeasureHost.IsVisible || OpenAreas.Any(a => a.IsVisible) || _textPanelOfTool
            || anyPanel && _vm?.Tool == ViewTool.Edit);
    }

    // The „Text“ panel was opened by the „Text einfügen“ tool (see UpdateInset)
    private bool _textPanelOfTool;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Tool):
                UpdateInset();
                break;
            case nameof(MainViewModel.SidebarVisible):
                ApplySidebar();
                break;
            case nameof(MainViewModel.RibbonCollapsed):
                ApplyCollapseIcon();
                break;
            case nameof(MainViewModel.SelectedDocument):
                ObserveDocument(_vm?.SelectedDocument);
                break;
        }
    }

    /// <summary>Sets the sidebar column to 0 when hidden, and back to its previous width afterwards.</summary>
    private void ApplySidebar()
    {
        if (_vm is null) return;
        var column = BodyGrid.ColumnDefinitions[0];
        if (_vm.SidebarVisible)
        {
            column.MinWidth = 160;
            column.Width = _sidebarWidth;
        }
        else
        {
            if (column.Width.Value > 0) _sidebarWidth = column.Width;
            column.MinWidth = 0;
            column.Width = new GridLength(0);
        }
    }

    private void ApplyCollapseIcon()
    {
        if (_vm is null) return;
        var key = _vm.RibbonCollapsed ? "Icon.ChevronDown" : "Icon.ChevronUp";
        if (this.TryFindResource(key, out var icon) && icon is Geometry g) CollapseButton.Icon = g;
    }

    // ------------------------------------------------------------ Keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _vm is null) return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.P when ctrl:
                _vm.PrintCommand.Execute(null);
                break;
            case Key.N when ctrl:
                _vm.NewDocumentCommand.Execute(null);
                break;
            case Key.O when ctrl:
                _ = OpenWithDialogAsync();
                break;
            case Key.S when ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                if (_vm.HasSelection) _vm.ShowBackstageCommand.Execute(BackstagePage.SaveAs);
                break;
            case Key.S when ctrl:
                _vm.SaveCommand.Execute(null);
                break;
            case Key.Z when ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
            case Key.Y when ctrl:
                _vm.RedoCommand.Execute(null);
                break;
            case Key.Z when ctrl:
                _vm.UndoCommand.Execute(null);
                break;
            case Key.W when ctrl:
                _vm.CloseDocumentCommand.Execute(null);
                break;
            case Key.F1 when ctrl:
                _vm.ToggleRibbonCommand.Execute(null);
                break;
            case Key.D when e.KeyModifiers == KeyModifiers.Alt:
                _vm.ShowBackstageCommand.Execute(BackstagePage.Open);
                break;
            case Key.F4 when e.KeyModifiers == KeyModifiers.None:
                _vm.ToggleSidebarCommand.Execute(null);
                break;
            case Key.F11:
                ToggleFullScreen();
                break;
            case Key.F when ctrl && _vm.HasSelection:
                OpenSearch();
                break;
            // „Bearbeiten“: the chosen objects as one vector object, pasted into this or another document (B7)
            case Key.C when ctrl && _vm.Tool == ViewTool.Edit && _vm.SelectedDocument is { HasSelectedImages: true }:
                _vm.CopyEditedObjectsCommand.Execute(null);
                break;
            case Key.V when ctrl && _vm.Tool == ViewTool.Edit && _vm is { CopiedObjects: not null, SelectedDocument: { RichEditor: null } }:
                _vm.PasteObjectsCommand.Execute(null);
                break;
            case Key.E when ctrl && _vm.HasSelection:
                _vm.ToggleEditCommand.Execute(null);
                break;
            case Key.C when ctrl && _vm.SelectedDocument is { HasTextSelection: true }:
                _vm.CopyTextCommand.Execute(null);
                break;
            case Key.F3 when _vm.SelectedDocument is { } doc:
                if (!doc.IsSearchOpen) OpenSearch();
                else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) doc.FindPrevious();
                else doc.FindNext();
                break;
            // Eyedropper (focus still on its button): Esc cancels.
            case Key.Escape when _vm.SelectedDocument is { IsPickingColor: true } picking:
                picking.EndColorPick(null);
                break;
            case Key.Escape when _vm.BackstageOpen:
                _vm.BackstageOpen = false;
                break;
            case Key.Escape when _vm.RibbonPeek:
                _vm.RibbonPeek = false;
                break;
            // The flyout with the whole hidden text closes before the tool ends.
            case Key.Escape when _vm.SelectedDocument is { IsHiddenTextOpen: true } shown:
                shown.IsHiddenTextOpen = false;
                break;
            // The details of an image shown while reading close with Esc, wherever the focus is.
            case Key.Escape when _vm.SelectedDocument is { IsReadingImageInfo: true } reading:
                reading.EndImageInfo();
                break;
            // Focus not in the document (e.g. after clicking the button): Esc still ends the redaction tools and „Bearbeiten“ (as long as nothing is chosen there).
            case Key.Escape when _vm.Tool is ViewTool.InsertRedaction or ViewTool.RedactText or ViewTool.RedactHidden
                                 || _vm.Tool == ViewTool.Edit && _vm.SelectedDocument is { EditTarget: EditTarget.None, SelectedElement: null }:
                _vm.Tool = ViewTool.SelectText;
                break;
            // Likewise “Edit form” (e.g. right after “Edit fields” in the Home tab), as long as no field is selected.
            case Key.Escape when _vm.Tool == ViewTool.DesignForm && _vm.SelectedDocument is { FieldSelection.Count: 0, ShowTabOrder: false }:
                _vm.EndModeCommand.Execute(null);
                break;
            // Likewise „Bearbeiten“, as long as nothing is chosen there (see KONZEPT.md, section 5.22).
            case Key.Escape when _vm.IsEditTool && _vm.SelectedDocument is { EditTarget: EditTarget.None, EditingElement: null, SelectedElement: null }:
                _vm.EndModeCommand.Execute(null);
                break;
            case Key.Escape when WindowState == WindowState.FullScreen:
                ToggleFullScreen();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------ Search

    /// <summary>Opens the search bar and selects the search term so that it can be overwritten.</summary>
    private void OpenSearch()
    {
        _vm?.OpenSearchCommand.Execute(null);
        // Let it become visible first, then set the focus.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    private void OnSearchClick(object? sender, RoutedEventArgs e) => OpenSearch();

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm?.SelectedDocument is not { } doc) return;
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                doc.FindPrevious();
                break;
            case Key.Enter:
                doc.FindNext();
                break;
            case Key.Escape:
                doc.CloseSearch();
                Viewer.Focus();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void ToggleFullScreen() =>
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;

    private void OnFullScreenClick(object? sender, RoutedEventArgs e) => ToggleFullScreen();

    // ------------------------------------------------------------ Opening

    private void OnOpenClick(object? sender, RoutedEventArgs e) => _ = OpenWithDialogAsync();

    private async Task OpenWithDialogAsync()
    {
        if (_vm is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Tr.T("PDF öffnen"),
            AllowMultiple = true,
            FileTypeFilter =
            [
                // Linux dialogs are case-sensitive.
                new FilePickerFileType(Tr.T("PDF-Dokumente"))
                {
                    Patterns = ["*.pdf", "*.PDF"],
                    MimeTypes = ["application/pdf"],
                },
                FilePickerFileTypes.All,
            ],
        });

        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0) await _vm.OpenAsync(paths);
    }

    private async Task<string?> PickImageFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Tr.T("Bild wählen"),
            FileTypeFilter =
            [
                new FilePickerFileType(Tr.T("Bilder")) { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif", "*.PNG", "*.JPG", "*.JPEG"] },
                FilePickerFileTypes.All,
            ],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> AskPasswordAsync(string fileName, bool wrongPassword) =>
        await new PasswordWindow(fileName, wrongPassword).ShowDialog<string?>(this);

    // ------------------------------------------------------------ Drag & Drop

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        // Over the sidebar: insert (insertion marker), otherwise open.
        DropOverlay.IsVisible = hasFiles && !DragOverPageList(e);
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        DropMarker.IsVisible = false;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        var paths = e.DataTransfer.TryGetFiles()?
            .Select(item => item.TryGetLocalPath())
            .OfType<string>()
            .ToList();
        if (paths is not { Count: > 0 } || _vm is null) return;
        if (!await DropOnPageList(e, paths)) await _vm.OpenAsync(paths);
    }

    // ------------------------------------------------------------ Appearance, language

    private void OnThemeClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not MenuItem { Tag: string tag } || !Enum.TryParse<ThemeChoice>(tag, out var choice)) return;
        _vm.Settings.SelectedTheme = SettingsViewModel.ThemeOptions.First(o => o.Choice == choice);
    }

    private void OnTemplateGermanClick(object? sender, RoutedEventArgs e) => _ = ExportTemplateAsync(fromGerman: true);

    private void OnTemplateEnglishClick(object? sender, RoutedEventArgs e) => _ = ExportTemplateAsync(fromGerman: false);

    private async Task ExportTemplateAsync(bool fromGerman)
    {
        if (_vm is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Tr.T("Vorlage zum Übersetzen speichern"),
            SuggestedFileName = Languages.TemplateFileName(fromGerman),
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"], MimeTypes = ["application/json"] }],
        });
        if (file?.TryGetLocalPath() is { } path) _vm.Settings.ExportTemplate(fromGerman, path);
    }

    private void OnReloadLanguagesClick(object? sender, RoutedEventArgs e) => _vm?.Settings.ReloadLanguages();

    // Redaction codes (wish of 4.11.2026): edited in the system's text editor, added from and saved to files

    private async void OnEditCodesClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.Settings.CodesFileToEdit() is { } path && Launcher is { } launcher) await launcher.LaunchFileInfoAsync(new FileInfo(path));
    }

    private void OnReloadCodesClick(object? sender, RoutedEventArgs e) => _vm?.Settings.ReloadCodeSets();

    private async void OnCodesFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        Directory.CreateDirectory(_vm.Settings.CodesFolder);
        if (_vm.ShowFolder is { } show) await show(_vm.Settings.CodesFolder);
    }

    private async void OnImportCodesClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Tr.T("Code-Datei hinzufügen"),
            FileTypeFilter = [new FilePickerFileType(Tr.T("Textdatei")) { Patterns = ["*.txt"], MimeTypes = ["text/plain"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) _vm.Settings.ImportCodes(path);
    }

    private async void OnExportCodesClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.Settings.SelectedCodeSet is not { } set) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Tr.T("Code-Datei exportieren"),
            SuggestedFileName = set.Id + ".txt",
            DefaultExtension = "txt",
            FileTypeChoices = [new FilePickerFileType(Tr.T("Textdatei")) { Patterns = ["*.txt"], MimeTypes = ["text/plain"] }],
        });
        if (file?.TryGetLocalPath() is { } path) _vm.Settings.ExportCodes(path);
    }

    private void OnRemoveOwnCodesClick(object? sender, RoutedEventArgs e) => _vm?.Settings.RemoveOwnCodes();
}
