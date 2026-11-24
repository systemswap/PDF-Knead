using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.ViewModels;

/// <summary>
/// Formatting of edited text, in the right-hand panel as for shapes and fields: font, size, bold/italic/underline/strikeout, colour
/// (with colour picker), alignment, line spacing, and which font actually draws the text (the file's font, a system font or a
/// substitute). While editing, a change applies to the selection or to what is typed next (like Ctrl+B); if the block is only
/// selected, it applies to the whole block. See KONZEPT.md, section 5.14.
/// </summary>
public sealed partial class RichTextPropertiesViewModel : ObservableObject
{
    private DocumentViewModel? _doc;
    private RichTextEditor? _editor;
    private ElementRef? _selected;

    /// <summary>Raised after a change made in the panel, so that the keyboard focus returns to the page (to continue typing).</summary>
    public event Action? FocusDocument;

    // ------------------------------------------------------------ Lists

    private static readonly ObservableCollection<string> FamilyList = ["Liberation Sans", "Liberation Serif", "Liberation Mono"];
    private static int _familiesLoading;
    private static readonly HashSet<string> SystemFamilies = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> RememberedFamilies = [];

    /// <summary>Fonts to choose from; the system fonts are added in the background on first use.</summary>
    public static IReadOnlyList<string> Families => FamilyList;

    public static IReadOnlyList<double> Sizes { get; } = [6, 7, 8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 60, 72];

    private static readonly ObservableCollection<FieldColorOption> ColorList =
    [
        new(0x000000, "Schwarz"),
        new(0x595959, "Dunkelgrau"),
        new(0x8C8C8C, "Grau"),
        new(0xFFFFFF, "Weiß"),
        new(0x1F4E9A, "Blau"),
        new(0xC00000, "Rot"),
        new(0xED7D31, "Orange"),
        new(0x1E7B34, "Grün"),
    ];

    private static readonly int StandardColors = ColorList.Count;

    private const string CustomLabel = "Eigene Farbe";

    public static IReadOnlyList<FieldColorOption> Colors => ColorList;

    private static readonly ObservableCollection<Choice<double>> SpacingList =
    [
        new(1.0, "Einfach (1,0)"),
        new(1.15, "1,15"),
        new(1.5, "1,5"),
        new(2.0, "Doppelt (2,0)"),
    ];

    private static readonly int StandardSpacings = SpacingList.Count;

    public static IReadOnlyList<Choice<double>> Spacings => SpacingList;

    /// <summary>All German labels of the lists (for the translation check).</summary>
    public static IEnumerable<string> AllLabels =>
        ColorList.Take(StandardColors).Select(c => c.Label).Append(CustomLabel).Concat(SpacingList.Take(StandardSpacings).Select(s => s.Label));

    // ------------------------------------------------------------ Selection

    /// <summary>Rereads the state (editing started or ended, selection, undo, another document).</summary>
    /// <param name="doc">The current document, or null.</param>
    public void Refresh(DocumentViewModel? doc)
    {
        if (!ReferenceEquals(doc, _doc))
        {
            if (_doc is not null) _doc.RichEditorUpdated -= OnEditorUpdated;
            if (doc is not null) doc.RichEditorUpdated += OnEditorUpdated;
            _doc = doc;
        }
        _editor = doc?.RichEditor;
        _selected = _editor is null && doc is { IsFieldDesign: false, SelectedElement: { Element: RichTextElement } selected } ? selected : null;
        if (_pickingDoc is { } picking && (!ReferenceEquals(picking, doc) || Element is null)) picking.EndColorPick(null);
        if (IsVisible) LoadFamilies();
        Remember();
        OnPropertyChanged(string.Empty);
    }

    private void OnEditorUpdated()
    {
        if (_doc is null) return;
        _editor = _doc.RichEditor;
        Remember();
        OnPropertyChanged(string.Empty);
    }

    private RichTextElement? Element => _editor?.Current ?? _selected?.Element as RichTextElement;

    /// <summary>Whether the panel is visible: a block is being edited or is selected.</summary>
    public bool IsVisible => Element is not null;

    /// <summary>Whether changes are allowed: edited text is page content (bit 4, „Ändern“); otherwise it can only be viewed.</summary>
    public bool CanEdit => _doc?.CanModifyContent == true;

    /// <summary>Formatting at the caret or at the start of the selection; when not editing, that of the block's start.</summary>
    private RichStyle Style => _editor?.Style ?? Element?.Content.StyleAt(0) ?? new RichStyle();

    /// <summary>Whether the property holds for everything a change would affect (selection or whole block).</summary>
    private bool All(Func<RichStyle, bool> property) =>
        _editor?.All(property) ?? Element?.Content.StylesIn(0, Element.Content.Length).All(property) ?? false;

    // ------------------------------------------------------------ Font

    public string? Family
    {
        get => Element is null ? null : Style.Family;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == Style.Family) return;
            // No focus change, so that the arrow keys can step through the list (the focus returns when the list closes)
            Format(s => s with { Family = value }, Tr.T("Schrift ändern"), focus: false);
        }
    }

    public double? Size
    {
        get => Element is null ? null : Math.Round(Style.Size, 1);
        set
        {
            if (value is not { } size || double.IsNaN(size) || size is < 1 or > 500 || Math.Abs(size - Style.Size) < 0.05) return;
            Format(s => s with { Size = size }, Tr.T("Schriftgröße ändern"));
        }
    }

    /// <summary>Font size as text, so that custom values can be typed (comma or point).</summary>
    public string SizeText
    {
        get => Size?.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) ?? "";
        set
        {
            if (double.TryParse(value?.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var size)) Size = Math.Round(size, 1);
            OnPropertyChanged();
        }
    }

    public bool Bold
    {
        get => Element is not null && All(s => s.Bold);
        set => Format(s => s with { Bold = value }, Tr.T("Fett"));
    }

    public bool Italic
    {
        get => Element is not null && All(s => s.Italic);
        set => Format(s => s with { Italic = value }, Tr.T("Kursiv"));
    }

    public bool Underline
    {
        get => Element is not null && All(s => s.Underline);
        set => Format(s => s with { Underline = value }, Tr.T("Unterstrichen"));
    }

    public bool Strikeout
    {
        get => Element is not null && All(s => s.Strikeout);
        set => Format(s => s with { Strikeout = value }, Tr.T("Durchgestrichen"));
    }

    public FieldColorOption? Color
    {
        get => Element is null ? null : ColorList.FirstOrDefault(c => c.Rgb == Style.Color) ?? new FieldColorOption(Style.Color, CustomLabel);
        set
        {
            if (value?.Rgb is not { } rgb || rgb == Style.Color) return;
            Format(s => s with { Color = rgb }, Tr.T("Textfarbe ändern"));
        }
    }

    /// <summary>Which font actually draws the text at the caret, with the reason if it is a substitute.</summary>
    public string FontNote
    {
        get
        {
            if (Element is null) return "";
            var style = Style;
            // a font of drawn glyphs (Type 3) draws the block only as a whole (KONZEPT.md, section 5.14, T4)
            if (style.Original is { } whole && Core.Pdf.Editing.RichLayout.WholeBlockFallback(Element.Content).Contains(whole.Font))
                return Tr.T("Die Schrift der Datei hat nicht alle Zeichen — Ersatzschrift für den ganzen Block: {0}.", RichFonts.Fallback(style.Family));
            if (style.Original is { } original)
                return Tr.T("Schrift der Datei: {0}. Fehlt ihr ein Zeichen, kommt es aus {1}.",
                    TextBlocks.StripSubset(original.Font.GetName(Core.Pdf.Objects.PdfName.Get("BaseFont"))?.Value ?? style.Family), RichFonts.Fallback(style.Family));
            var resolved = RichFonts.For(style);
            var cut = resolved.FakeBold || resolved.FakeItalic ? " " + Tr.T("(Schnitt nachgemacht)") : "";
            if (resolved.IsSystemFont) return Tr.T("Systemschrift: {0}", style.Family) + cut;
            if (style.Family.StartsWith("Liberation", StringComparison.OrdinalIgnoreCase)) return Tr.T("Schrift: {0}", style.Family);
            return Tr.T("Ersatzschrift: {0} — „{1}“ ist nicht installiert oder ihre Lizenz erlaubt das Einbetten nicht.", RichFonts.Fallback(style.Family), style.Family);
        }
    }

    // ------------------------------------------------------------ Paragraph

    public bool AlignLeft => Element?.Alignment == BlockAlignment.Left;

    public bool AlignCenter => Element?.Alignment == BlockAlignment.Center;

    public bool AlignRight => Element?.Alignment == BlockAlignment.Right;

    public bool AlignJustify => Element?.Alignment == BlockAlignment.Justify;

    [RelayCommand]
    private void SetAlignment(BlockAlignment alignment)
    {
        if (Element is not { } element || element.Alignment == alignment) return;
        ChangeBlock(e => e with { Alignment = alignment }, Tr.T("Ausrichtung ändern"));
    }

    public Choice<double>? Spacing
    {
        get => Element is { } e ? SpacingList.FirstOrDefault(s => Math.Abs(s.Value - e.LineGap) < 0.005) : null;
        set
        {
            if (value is null || Element is not { } element || Math.Abs(value.Value - element.LineGap) < 0.005) return;
            ChangeBlock(e => e with { LineGap = value.Value }, Tr.T("Zeilenabstand ändern"));
        }
    }

    // ------------------------------------------------------------ Colour picker

    private DocumentViewModel? _pickingDoc;

    /// <summary>Colour picker: the next click on the page picks up the text colour.</summary>
    public bool IsPicking
    {
        get => _pickingDoc is not null;
        set
        {
            _pickingDoc?.EndColorPick(null);
            if (!value || _doc is not { } doc || Element is null) return;
            _pickingDoc = doc;
            OnPropertyChanged();
            doc.BeginColorPick(rgb =>
            {
                _pickingDoc = null;
                if (rgb is { } color) Format(s => s with { Color = color }, Tr.T("Textfarbe ändern"));
                OnPropertyChanged(nameof(IsPicking));
            });
        }
    }

    // ------------------------------------------------------------ Changing

    /// <summary>Applies formatting: while editing via the editor (selection or what is typed next), otherwise to the whole block.</summary>
    private void Format(Func<RichStyle, RichStyle> change, string description, bool focus = true)
    {
        if (!CanEdit) return;
        if (_editor is { } editor)
        {
            editor.Format(change);
        }
        else if (_doc is not null && _selected is { Element: RichTextElement element } selected)
        {
            var content = element.Content.Format(0, element.Content.Length, s => change(s).Settled(s));
            var updated = RichLayout.Fitted(element with { Content = content });
            if (_doc.ReplaceElement(selected, updated, description)) _selected = new ElementRef(selected.Page, updated);
        }
        Remember();
        OnPropertyChanged(string.Empty);
        if (focus) FocusDocument?.Invoke();
    }

    /// <summary>Returns the keyboard focus to the page (font list closed).</summary>
    public void ReturnFocus()
    {
        if (Element is not null) FocusDocument?.Invoke();
    }

    /// <summary>Changes the block; alignment and line spacing always apply to the whole block.</summary>
    private void ChangeBlock(Func<RichTextElement, RichTextElement> change, string description)
    {
        if (!CanEdit) return;
        if (_editor is { } editor)
        {
            editor.SetBlock(change);
        }
        else if (_doc is not null && _selected is { Element: RichTextElement element } selected)
        {
            var updated = RichLayout.Fitted(change(element));
            if (_doc.ReplaceElement(selected, updated, description)) _selected = new ElementRef(selected.Page, updated);
        }
        Remember();
        OnPropertyChanged(string.Empty);
        FocusDocument?.Invoke();
    }

    /// <summary>Adds the block's colour, font and line spacing to the lists if they are missing there (otherwise the box would stay empty).</summary>
    private void Remember()
    {
        if (Element is not { } element) return;
        var style = Style;
        if (!ColorList.Any(c => c.Rgb == style.Color))
        {
            ColorList.Add(new FieldColorOption(style.Color, CustomLabel));
            if (ColorList.Count - StandardColors > 6) ColorList.RemoveAt(StandardColors);
        }
        if (!FamilyList.Contains(style.Family, StringComparer.OrdinalIgnoreCase))
        {
            FamilyList.Insert(3, style.Family);
            // Fonts from documents: at most eight are remembered, the oldest goes (the list is shared by all tabs)
            RememberedFamilies.Add(style.Family);
            if (RememberedFamilies.Count > 8)
            {
                var oldest = RememberedFamilies[0];
                RememberedFamilies.RemoveAt(0);
                if (!SystemFamilies.Contains(oldest)) FamilyList.Remove(oldest);
            }
        }
        if (!SpacingList.Any(s => Math.Abs(s.Value - element.LineGap) < 0.005))
        {
            if (SpacingList.Count > StandardSpacings) SpacingList.RemoveAt(StandardSpacings);
            SpacingList.Add(new Choice<double>(element.LineGap, element.LineGap.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)));
        }
    }

    /// <summary>Reads the system fonts once in the background (the first scan of the font folders takes a while).</summary>
    internal static void LoadFamilies()
    {
        if (Interlocked.Exchange(ref _familiesLoading, 1) != 0) return;
        _ = Task.Run(() =>
        {
            IReadOnlyList<string> families;
            try
            {
                families = RichFonts.Families();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Core.Platform.AppLog.Write("Schriften", e.ToString());
                return;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                foreach (var family in families)
                {
                    SystemFamilies.Add(family);
                    if (!FamilyList.Contains(family, StringComparer.OrdinalIgnoreCase)) FamilyList.Add(family);
                }
            });
        });
    }
}
