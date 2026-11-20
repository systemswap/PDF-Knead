using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Services;

namespace PdfKnead.Views;

/// <summary>
/// A handwriting font to choose from: the name written in it, as it will appear on the page. While typing, only
/// <see cref="Preview"/> changes; the list and its entries stay in place.
/// </summary>
public sealed partial class SignatureChoice(string fontFile, string family, SignatureElement preview) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string FontFile { get; } = fontFile;

    public string Family { get; } = family;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] public partial SignatureElement Preview { get; set; } = preview;
}

/// <summary>
/// Choosing a signature: written (the name in one of the available handwriting fonts, built in or from the font folders, all
/// previewed), drawn (mouse, pen, finger) or as an image (photo or scan, a white background becomes
/// transparent). Result: template for the element (without position), or null.
/// </summary>
public partial class SignatureWindow : Window
{
    private const int WriteTab = 0, DrawTab = 1, ImageTab = 2;

    private List<SignatureChoice> _choices = [];
    private readonly string? _fieldName;
    private RgbaImage? _original;
    private RgbaImage? _image;

    public SignatureWindow() => InitializeComponent();

    private Core.Platform.SigningCertificateChoice? _with;
    private IReadOnlyList<ViewModels.CertificateRow> _certificates = [];

    /// <summary>
    /// The certificates that can sign (<c>MainViewModel.SigningChoices</c>): „Unterschreiben mit … [ändern]“ offers them; the choice holds for this
    /// signature only, unless „als Standard merken“ is ticked (docs/ABLAUFPLAN.md, 9.4.0, step 2; docs/FEHLERGEDAECHTNIS.md, No. 722).
    /// </summary>
    public IReadOnlyList<ViewModels.CertificateRow> Certificates
    {
        get => _certificates;
        init
        {
            _certificates = value;
            var menu = new MenuFlyout();
            foreach (var row in value)
            {
                var item = new MenuItem { Header = row.HasDetails ? row.Name + " — " + row.Details : row.Name };
                item.Click += (_, _) => Choose(row);
                menu.Items.Add(item);
            }
            WithButton.Flyout = menu;
            WithButton.IsEnabled = value.Count > 1;
            ShowWith();
        }
    }

    /// <summary>What „Einsetzen“ returned (tests, where the window is no dialog).</summary>
    internal PageElement? LastResult { get; private set; }

    /// <summary>Chooses the certificate of a row for this signature (the menu of „ändern …“).</summary>
    internal void Choose(ViewModels.CertificateRow row)
    {
        _with = row.Choice;
        ShowWith();
    }

    /// <summary>The default certificate of the settings.</summary>
    private static Core.Platform.SigningCertificateChoice Default =>
        UserSettings.Current.SigningCertificate ?? new Core.Platform.SigningCertificateChoice(Core.Platform.SigningCertificateSource.Own);

    /// <summary>The row of the certificate that signs: the one chosen for this signature, else the default.</summary>
    private ViewModels.CertificateRow? Signing => _certificates.FirstOrDefault(r => Same(r.Choice, _with ?? Default));

    // own certificates by their file (null = the first one; there can be several, No. 733), files by their path, the others by fingerprint
    private static bool Same(Core.Platform.SigningCertificateChoice a, Core.Platform.SigningCertificateChoice b) => a.Source == b.Source && a.Source switch
    {
        Core.Platform.SigningCertificateSource.Own or Core.Platform.SigningCertificateSource.File =>
            string.Equals(a.Path, b.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
        _ => a.Fingerprint is not null && a.Fingerprint == b.Fingerprint,
    };

    /// <summary>Shows which certificate signs and its level.</summary>
    private void ShowWith()
    {
        var row = Signing;
        WithText.Text = row is null ? Tr.T("das Standard-Zertifikat") : row.Name + (row.Level is { } level ? " · " + Core.Pdf.Signatures.SignatureTexts.Level(level) : "");
        RememberBox.IsVisible = _with is { } chosen && !Same(chosen, Default);
        if (row?.Level is { } known)
            LevelText.Text = Tr.T("Stufe: {0}. {1}", Core.Pdf.Signatures.SignatureTexts.Level(known), Core.Pdf.Signatures.SignatureTexts.LevelMeaning(known));
    }

    /// <param name="current">Initial values: the last used signature or the one being changed (also determines the tab).</param>
    public SignatureWindow(PageElement current) : this()
    {
        BuildSwatches();
        SetColor(current switch { SignatureElement s => s.Color, InkElement i => i.Color, _ => SignatureElement.InkBlue }, refresh: false);
        TextureBox.IsChecked = current switch { SignatureElement s => s.Textured, InkElement i => i.Textured, _ => UserSettings.Current.SignatureTextured };
        StrengthSlider.Value = current switch { SignatureElement s => s.InkStrength, InkElement i => i.InkStrength, _ => UserSettings.Current.SignatureInkStrength };
        ShowStrength();
        NameBox.Text = (current as SignatureElement)?.Name ?? Services.UserSettings.Current.SignatureName ?? "";
        Rebuild((current as SignatureElement)?.FontFile ?? Services.UserSettings.Current.SignatureFont);
        Ink.Ink = InkBrush;

        NameBox.TextChanged += (_, _) =>
        {
            RefreshPreviews();
            UpdateOk();
        };
        Closed += (_, _) => Controls.ElementPreview.ReleaseImages();
        TextureBox.IsCheckedChanged += (_, _) =>
        {
            RefreshPreviews();
            ShowStrength();
        };
        StrengthSlider.ValueChanged += (_, _) =>
        {
            ShowStrength();
            RefreshPreviews();
        };
        FontList.SelectionChanged += (_, _) => ShowStrength();
        CustomColor.Flyout!.Opened += (_, _) => ShowCustom();
        HueSlider.ValueChanged += (_, _) => OnCustomChanged();
        SaturationSlider.ValueChanged += (_, _) => OnCustomChanged();
        LightnessSlider.ValueChanged += (_, _) => OnCustomChanged();
        FontList.SelectionChanged += (_, _) => UpdateOk();
        FontList.DoubleTapped += (_, _) => OnOk(null, new RoutedEventArgs());
        Ink.Changed += UpdateOk;
        Tabs.SelectionChanged += (_, _) => UpdateOk();
        RemoveBackground.IsCheckedChanged += (_, _) => PrepareImage();
        OriginBox.IsChecked = UserSettings.Current.SignatureOrigin;
        DigitalBox.IsChecked = current.Digital is not null || UserSettings.Current.SignatureDigital;
        RightsRow.IsVisible = LevelText.IsVisible = WithRow.IsVisible = DigitalBox.IsChecked == true;
        DigitalBox.IsCheckedChanged += (_, _) => RightsRow.IsVisible = LevelText.IsVisible = WithRow.IsVisible = DigitalBox.IsChecked == true;
        _with = current.Digital?.With;
        // the own certificate is self-issued: a simple signature; another one is shown with its level under „Zertifikat …“ (it is loaded only there)
        LevelText.Text = UserSettings.Current.SigningCertificate is null or { Source: Core.Platform.SigningCertificateSource.Own }
            ? Tr.T("Stufe: {0}. {1}", Core.Pdf.Signatures.SignatureTexts.Level(Core.Pdf.Signatures.SignatureLevel.Simple),
                Core.Pdf.Signatures.SignatureTexts.LevelMeaning(Core.Pdf.Signatures.SignatureLevel.Simple))
            : Tr.T("Welche Stufe die Signatur hat (einfach, fortgeschritten, qualifiziert), zeigt „Zertifikat“ im Reiter Unterschrift.");
        RightsBox.SelectedIndex = current.Digital?.Certify switch
        {
            Core.Pdf.Signatures.CertificationLevel.FormsAndSigning => 1,
            Core.Pdf.Signatures.CertificationLevel.FormsSigningAndComments => 2,
            Core.Pdf.Signatures.CertificationLevel.NoChanges => 3,
            _ => 0,
        };
        LockBox.IsChecked = current.Digital?.LockFields == true;
        _fieldName = current.Digital?.FieldName;   // chosen anew: it still goes into the same field
        OriginBox.IsCheckedChanged += (_, _) => UpdateOrigin();
        NameBox.TextChanged += (_, _) => UpdateOrigin();
        UpdateOrigin();

        switch (current)
        {
            case InkElement ink:
                Tabs.SelectedIndex = DrawTab;
                Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Ink.Load(ink.Strokes), Avalonia.Threading.DispatcherPriority.Loaded);
                break;
            case ImageElement picture:
                Tabs.SelectedIndex = ImageTab;
                _original = picture.Image;
                RemoveBackground.IsChecked = false;   // already prepared
                PrepareImage();
                break;
            default:
                Tabs.SelectedIndex = WriteTab;
                Opened += (_, _) =>
                {
                    ShowSelected();
                    NameBox.Focus();
                    NameBox.CaretIndex = NameBox.Text?.Length ?? 0;
                };
                break;
        }
        UpdateOk();
    }

    /// <summary>
    /// Whether this signature can certify the document: only the first signature of a document can (set by the caller; without a
    /// document, true). Otherwise the certification levels are not offered.
    /// </summary>
    public bool CanCertify
    {
        get => CertifyForms.IsEnabled;
        init
        {
            foreach (var item in (ComboBoxItem[])[CertifyForms, CertifyComments, CertifyNothing])
            {
                item.IsEnabled = value;
                ToolTip.SetTip(item, value ? null : Tr.T("Zertifizieren kann nur die erste Unterschrift eines Dokuments, und nur eine."));
            }
            if (!value) RightsBox.SelectedIndex = 0;
        }
    }

    /// <summary>Whether „Digital signieren“ can be chosen (see <see cref="DigitalRefusal"/>).</summary>
    public bool CanSignDigitally => DigitalBox.IsEnabled;

    /// <summary>
    /// Why the document cannot be signed digitally (set by the caller; an encrypted file with RC4, with a MAC, or without the right to sign):
    /// then „Digital signieren“ is offered greyed out with the reason. Null: it can.
    /// </summary>
    public string? DigitalRefusal
    {
        get => DigitalBox.IsEnabled ? null : ToolTip.GetTip(DigitalBox) as string;
        init
        {
            if (value is null) return;
            DigitalBox.IsEnabled = false;
            DigitalBox.IsChecked = false;
            ToolTip.SetTip(DigitalBox, value);
        }
    }

    private SignatureChoice? Selected => FontList.SelectedItem as SignatureChoice;

    private string SignerName => (NameBox.Text ?? "").Trim();

    private int InkColor => _color;

    private bool Textured => TextureBox.IsChecked == true;

    /// <summary>Ink strength in steps of 5 % (each step is a palette of its own).</summary>
    private double Strength => Math.Round(Math.Clamp(StrengthSlider.Value, PdfKnead.Core.Imaging.InkColor.MinStrength, PdfKnead.Core.Imaging.InkColor.MaxStrength) * 20) / 20;

    /// <summary>Shows the ink strength; the slider has an effect with ballpoint-pen texture and on the own images of colour fonts.</summary>
    private void ShowStrength()
    {
        StrengthText.Text = $"{Strength * 100:0} %";
        StrengthSlider.IsEnabled = Textured || (Selected is { } choice && ColorGlyphs.For(EmbeddedFonts.HandwritingOpenType(choice.FontFile)) is not null);
    }

    private IBrush InkBrush => new SolidColorBrush(Color.FromRgb((byte)(InkColor >> 16), (byte)(InkColor >> 8), (byte)InkColor));

    private void UpdateOk() => OkButton.IsEnabled = Tabs.SelectedIndex switch
    {
        DrawTab => !Ink.IsEmpty,
        ImageTab => _image is not null,
        _ => Selected is not null && SignerName.Length > 0,
    };

    private SignatureElement PreviewFor(string font) => new()
    {
        // Small (about 1:1 at 100 % in the list): the ballpoint-pen texture is computed at the resolution for this size.
        Rect = new PdfRectangle(100, 100, 170, 110),
        Name = SignerName.Length > 0 ? SignerName : Tr.T("Unterschrift"),
        FontFile = font,
        Color = InkColor,
        Textured = Textured,
        InkStrength = Strength,
    };

    /// <summary>Builds the list once (one entry per handwriting font) and selects the last used one.</summary>
    private void Rebuild(string? selectedFont)
    {
        _choices = EmbeddedFonts.Handwriting.Select(f => new SignatureChoice(f, Family(f), PreviewFor(f))).ToList();
        FontList.ItemsSource = _choices;
        FontList.SelectedItem = _choices.FirstOrDefault(c => c.FontFile == selectedFont) ?? _choices.FirstOrDefault();
        ShowSelected();
        if (OkButton is not null) UpdateOk();
    }

    /// <summary>Name or colour changed: renews only the previews (entries and selection stay, nothing jumps).</summary>
    private void RefreshPreviews()
    {
        foreach (var choice in _choices) choice.Preview = PreviewFor(choice.FontFile);
    }

    /// <summary>Scrolls the selected handwriting font into view (after the list has been built; before that, the entries are still missing).</summary>
    private void ShowSelected() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (FontList.SelectedItem is { } item) FontList.ScrollIntoView(item);
        }, Avalonia.Threading.DispatcherPriority.Loaded);

    private static string Family(string file)
    {
        try
        {
            return SignatureFonts.Display(EmbeddedFonts.HandwritingOpenType(file));
        }
        catch (FontFormatException)
        {
            return Path.GetFileNameWithoutExtension(file);
        }
    }

    // ------------------------------------------------------------ Colour

    private int _color = SignatureElement.InkBlue;
    private bool _syncing;
    private readonly List<(Button Button, int Rgb)> _swatches = [];

    /// <summary>Ballpoint-pen colours first (this is how the ink looks on average; full ink is darker).</summary>
    private void BuildSwatches()
    {
        (int Rgb, string Label)[] colors =
        [
            (SignatureElement.InkBlue, Tr.T("Kuli-Blau")),
            (0x2A4BC0, Tr.T("Königsblau")),
            (0x000000, Tr.T("Schwarz")),
            (0x1E6B3A, Tr.T("Grün")),
            (0xB32030, Tr.T("Rot")),
        ];
        foreach (var (rgb, label) in colors)
        {
            var button = new Button
            {
                Content = new Avalonia.Controls.Shapes.Ellipse { Width = 18, Height = 18, Fill = Brush(rgb) },
            };
            button.Classes.Add("swatch");
            ToolTip.SetTip(button, label);
            Avalonia.Automation.AutomationProperties.SetName(button, label);
            button.Click += (_, _) => SetColor(rgb);
            _swatches.Add((button, rgb));
            Swatches.Children.Add(button);
        }
    }

    /// <summary>Selects a colour: ring around the matching swatch (otherwise around “Custom”), and updates the drawing area and previews.</summary>
    private void SetColor(int rgb, bool refresh = true)
    {
        _color = rgb & 0xFFFFFF;
        var preset = false;
        foreach (var (button, value) in _swatches)
        {
            button.Classes.Set("selected", value == _color);
            preset |= value == _color;
        }
        CustomColor.Classes.Set("selected", !preset);
        CustomPreview.Background = Brush(_color);
        Ink.Ink = InkBrush;
        if (refresh) RefreshPreviews();
    }

    /// <summary>Custom colour: sets the sliders to the current colour.</summary>
    private void ShowCustom()
    {
        var (h, s, l) = ToHsl(_color);
        _syncing = true;
        HueSlider.Value = h;
        SaturationSlider.Value = s * 100;
        LightnessSlider.Value = Math.Clamp(l * 100, LightnessSlider.Minimum, LightnessSlider.Maximum);
        _syncing = false;
    }

    private void OnCustomChanged()
    {
        if (_syncing) return;
        SetColor(FromHsl(HueSlider.Value, SaturationSlider.Value / 100, LightnessSlider.Value / 100));
    }

    private static SolidColorBrush Brush(int rgb) => new(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    private static (double H, double S, double L) ToHsl(int rgb)
    {
        double r = ((rgb >> 16) & 0xFF) / 255.0, g = ((rgb >> 8) & 0xFF) / 255.0, b = (rgb & 0xFF) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max - min < 1e-9) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    private static int FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        static int Byte(double v) => (int)Math.Clamp(Math.Round(v * 255), 0, 255);
        return (Byte(r + m) << 16) | (Byte(g + m) << 8) | Byte(b + m);
    }

    // ------------------------------------------------------------ Drawing

    private void OnUndoStroke(object? sender, RoutedEventArgs e) => Ink.UndoStroke();

    private void OnClearInk(object? sender, RoutedEventArgs e) => Ink.Clear();

    // ------------------------------------------------------------ Image

    private async void OnPickImage(object? sender, RoutedEventArgs e)
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
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        ShowImage(path, await Task.Run(() => TryLoad(path)));   // large photos not on the UI thread
    }

    /// <summary>Loads an image file (also used by tests).</summary>
    internal void LoadImage(string path) => ShowImage(path, TryLoad(path));

    private static RgbaImage? TryLoad(string path)
    {
        try
        {
            return ImageLoader.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void ShowImage(string path, RgbaImage? image)
    {
        _original = image;
        ImageInfo.Text = _original is null ? Tr.T("Das Bild lässt sich nicht lesen.") : Path.GetFileName(path);
        PrepareImage();
    }

    /// <summary>If wanted, removes the white background and trims the margin; then shows the preview.</summary>
    private void PrepareImage()
    {
        _image = _original is null ? null
            : RemoveBackground.IsChecked == true ? ImageTools.Trim(ImageTools.RemoveWhiteBackground(_original)) : _original;
        ImagePreview.Element = _image is null ? null : new ImageElement
        {
            Rect = new PdfRectangle(100, 100, 100 + _image.Width, 100 + _image.Height),
            Image = _image,
        };
        UpdateOk();
    }

    // ------------------------------------------------------------ Result

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        PageElement? result = Tabs.SelectedIndex switch
        {
            DrawTab when !Ink.IsEmpty => new InkElement { Rect = default, Strokes = Ink.Strokes, StrokeWidth = Controls.InkCanvas.StrokeWidth, Color = InkColor, Textured = Textured, InkStrength = Strength },
            ImageTab when _image is not null => new ImageElement { Rect = default, Image = _image, IsSignature = true },
            WriteTab when Selected is { } choice && SignerName.Length > 0 =>
                new SignatureElement { Rect = default, Name = SignerName, FontFile = choice.FontFile, Color = InkColor, Textured = Textured, InkStrength = Strength },
            _ => null,
        };
        if (result is null) return;
        var digital = DigitalBox.IsChecked == true && DigitalBox.IsEnabled;
        UserSettings.Update(s => s with
        {
            SignatureOrigin = OriginBox.IsChecked == true, SignatureDigital = DigitalBox.IsEnabled ? digital : s.SignatureDigital, SignatureTextured = Textured, SignatureInkStrength = Strength,
            SignatureName = digital && SignerName.Length > 0 ? SignerName : s.SignatureName,   // names the own certificate when it is created
        });
        var origin = OriginBox.IsChecked == true ? Device.Origin(SignerName, DateTime.Now) : null;
        // Digitally signed: the certificate vouches for it, so no seal of the device identity (KONZEPT.md, section 5.17);
        // otherwise the seal: the origin signed with the device identity (date = now).
        if (digital)
        {
            var certify = CanCertify ? RightsBox.SelectedIndex switch
            {
                1 => Core.Pdf.Signatures.CertificationLevel.FormsAndSigning,
                2 => Core.Pdf.Signatures.CertificationLevel.FormsSigningAndComments,
                3 => Core.Pdf.Signatures.CertificationLevel.NoChanges,
                _ => (Core.Pdf.Signatures.CertificationLevel?)null,
            } : null;
            // another certificate for this signature only — or, with „als Standard merken“, the new default
            var with = _with is { } chosen && !Same(chosen, Default) ? chosen : null;
            if (with is not null && RememberBox.IsChecked == true)
            {
                UserSettings.Update(s => s with { SigningCertificate = with });
                with = null;
            }
            result = result with
            {
                Digital = new DigitalSigning(origin, _fieldName, certify, LockBox.IsChecked == true, SignerName.Trim() is { Length: > 0 } signer ? signer : null, with),
            };
        }
        else if (origin is not null) result = result with { Seal = SignatureSeal.Create(result, origin, Device.Current) };
        LastResult = result;
        Close(result);
    }

    /// <summary>Preview of the origin line below the check box.</summary>
    private void UpdateOrigin() =>
        OriginPreview.Text = OriginBox.IsChecked == true ? Device.Origin(SignerName, DateTime.Now) : "";

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
