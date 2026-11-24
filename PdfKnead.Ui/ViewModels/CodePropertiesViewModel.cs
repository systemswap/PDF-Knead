using CommunityToolkit.Mvvm.ComponentModel;
using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Barcodes.DataMatrix;
using PdfKnead.Core.Barcodes.Qr;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Rendering;

namespace PdfKnead.ViewModels;

/// <summary>
/// Properties of the selected code (KONZEPT.md, section 5.19) in the panel on the right: kind, content, options of the kind, colours,
/// quiet zone, alternative text. Below them what the code is worth: why the content does not fit, the module size against the smallest
/// that scanners read reliably, the contrast, and the self-check — the code drawn as on the page and read back. Typing the content is one
/// undo step; every other change is one step and also applies to the next code.
/// </summary>
public sealed partial class CodePropertiesViewModel : ObservableObject
{
    private DocumentViewModel? _doc;
    private ElementRef? _selected;
    private CancellationTokenSource? _check;

    public static IReadOnlyList<Choice<BarcodeFormat>> Formats { get; } =
    [
        new(BarcodeFormat.Qr, "QR-Code"), new(BarcodeFormat.DataMatrix, "Data Matrix"), new(BarcodeFormat.Ean13, "EAN-13"), new(BarcodeFormat.Ean8, "EAN-8"),
        new(BarcodeFormat.UpcA, "UPC-A"), new(BarcodeFormat.UpcE, "UPC-E"), new(BarcodeFormat.Code128, "Code 128"), new(BarcodeFormat.Gs1_128, "GS1-128"),
        new(BarcodeFormat.Code39, "Code 39"), new(BarcodeFormat.Itf, "Interleaved 2 of 5 (ITF)"),
    ];

    public static IReadOnlyList<Choice<QrErrorCorrection>> Levels { get; } =
    [
        new(QrErrorCorrection.L, "Niedrig (7 %)"), new(QrErrorCorrection.M, "Mittel (15 %)"), new(QrErrorCorrection.Q, "Hoch (25 %)"),
        new(QrErrorCorrection.H, "Sehr hoch (30 %)"),
    ];

    public static IReadOnlyList<Choice<DataMatrixShape>> Shapes { get; } =
        [new(DataMatrixShape.Square, "Quadratisch"), new(DataMatrixShape.Rectangular, "Rechteckig")];

    /// <summary>Colours of modules and bars: the fixed ones without „Keine“.</summary>
    public static IReadOnlyList<FieldColorOption> Colors { get; } = [.. ElementPropertiesViewModel.Colors.Where(c => c.Rgb is not null && c.Label != "Eigene Farbe")];

    /// <summary>Background: the fixed colours; „Keine“ = transparent.</summary>
    public static IReadOnlyList<FieldColorOption> Backgrounds { get; } = [.. ElementPropertiesViewModel.Colors.Where(c => c.Label != "Eigene Farbe")];

    /// <summary>German labels of the lists that need a translation (for the translation check).</summary>
    public static IEnumerable<string> AllLabels => Levels.Select(l => l.Label).Concat(Shapes.Select(s => s.Label)).Append("QR-Code");

    /// <summary>Asks the view to put the cursor into the content box (a new code without content).</summary>
    public event EventHandler? ContentFocusRequested;

    /// <summary>Rereads the properties (selection changed, undo, another document).</summary>
    /// <param name="doc">The current document, or null.</param>
    public void Refresh(DocumentViewModel? doc)
    {
        var previous = _selected?.Element;
        _doc = doc;
        _selected = doc is { IsFieldDesign: false, SelectedElement: { Element: CodeElement } selected } ? selected : null;
        OnPropertyChanged(string.Empty);
        StartCheck();
        if (_selected is { Element: CodeElement { Text.Length: 0 } } && !ReferenceEquals(previous, _selected?.Element)) ContentFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    private CodeElement? Code => _selected?.Element as CodeElement;

    /// <summary>Whether the panel is visible: a code is selected.</summary>
    public bool IsVisible => _selected is not null;

    /// <summary>Whether changes are allowed: a code is saved as an annotation (bit 6, „Kommentieren“; <see cref="DocumentViewModel.CanChange"/>).</summary>
    public bool CanEdit => _doc is { } doc && _selected is { } selected && doc.CanChange(selected.Element);

    public string Title => Code is { } c ? CodeSymbol.Name(c.Format) : "";

    public bool IsMatrix => Code?.IsMatrix == true;

    public bool IsLinear => Code is { IsMatrix: false };

    public bool IsQr => Code?.Format == BarcodeFormat.Qr;

    public bool IsDataMatrix => Code?.Format == BarcodeFormat.DataMatrix;

    public Choice<BarcodeFormat>? Format
    {
        get => Code is { } c ? Formats.FirstOrDefault(f => f.Value == c.Format) : null;
        set
        {
            if (value is null || Code is not { } c || c.Format == value.Value) return;
            // another kind needs another shape: its default size around the same centre
            var changed = c with { Format = value.Value };
            var (w, h) = ElementAppearance.DefaultCodeSize(changed);
            if (c.Rotation is 90 or 270) (w, h) = (h, w);
            double cx = (c.Rect.Left + c.Rect.Right) / 2, cy = (c.Rect.Bottom + c.Rect.Top) / 2;
            Change(_ => changed with { Rect = new PdfRectangle(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2) }, Tr.T("Codeart ändern"));
        }
    }

    /// <summary>
    /// The content; each keystroke changes the code, all of them together are one undo step (the history joins a change to the last
    /// step only if that step made this very code with the same description).
    /// </summary>
    public string Text
    {
        get => Code?.Text ?? "";
        set
        {
            if (Code is { } c && c.Text != value) Change(code => code with { Text = value }, Tr.T("Inhalt ändern"), continueLast: true, remember: false);
        }
    }

    public bool Gs1
    {
        get => Code?.Gs1 == true;
        set => Change(c => c with { Gs1 = value }, Tr.T("GS1 umschalten"));
    }

    /// <summary>The content is GS1 data in brackets (GS1-128, or a 2D symbol with GS1).</summary>
    public bool UsesGs1 => Code is { } c && CodeSymbol.UsesGs1(c.Format, c.Gs1);

    public Choice<QrErrorCorrection>? Level
    {
        get => Code is { } c ? Levels.FirstOrDefault(l => l.Value == c.Level) : null;
        set
        {
            if (value is not null) Change(c => c with { Level = value.Value }, Tr.T("Fehlerkorrektur ändern"));
        }
    }

    public Choice<DataMatrixShape>? Shape
    {
        get => Code is { } c ? Shapes.FirstOrDefault(s => s.Value == c.Shape) : null;
        set
        {
            if (value is not null) Change(c => c with { Shape = value.Value }, Tr.T("Form ändern"));
        }
    }

    public FieldColorOption? Color
    {
        get => Code is { } c ? Colors.FirstOrDefault(o => o.Rgb == c.Color) : null;
        set
        {
            if (value?.Rgb is { } rgb) Change(c => c with { Color = rgb }, Tr.T("Farbe ändern"));
        }
    }

    public FieldColorOption? Background
    {
        get => Code is { } c ? Backgrounds.FirstOrDefault(o => o.Rgb == c.Background) : null;
        set
        {
            if (value is not null) Change(c => c with { Background = value.Rgb }, Tr.T("Hintergrund ändern"));
        }
    }

    public bool QuietZone
    {
        get => Code?.QuietZone == true;
        set => Change(c => c with { QuietZone = value }, Tr.T("Ruhezone umschalten"));
    }

    public bool Caption
    {
        get => Code?.Caption == true;
        set => Change(c => c with { Caption = value }, Tr.T("Klartext umschalten"));
    }

    public string AltText
    {
        get => Code?.AltText ?? "";
        set
        {
            if (Code is { } c && c.AltText != value) Change(code => code with { AltText = value }, Tr.T("Alternativtext ändern"), remember: false);
        }
    }

    /// <summary>What screen readers get when the alternative text is left empty.</summary>
    public string AltTextDefault => Code is { } c ? ElementAppearance.CodeAltText(c with { AltText = "" }) : "";

    /// <summary>How to write the content of this kind.</summary>
    public string ContentHint => Code is { } c ? CodeHints.ContentHint(c.Format, c.Gs1) : "";

    /// <summary>
    /// A changed code of the page: text on the page that still shows a piece of the old content (the old IBAN on an invoice, an address) —
    /// it may no longer fit the new code. Null if there is none; worked out with the self-check (the page text may still be read then).
    /// </summary>
    [ObservableProperty]
    private string? _besideHint;

    /// <summary>The hint for <see cref="BesideHint"/> from the characters of the page.</summary>
    private static string? Beside(CodeElement code, IReadOnlyList<Core.Pdf.Graphics.TextGlyph> text)
    {
        if (code.Replaces is not { } old || code.Text == old) return null;
        static string Bare(string s) => string.Concat(s.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        var onPage = Bare(string.Concat(text.Select(g => g.Text)));
        var now = Bare(code.Text);
        // pieces of the old content (words, numbers; spaces in them as on paper do not count) that the new one no longer has
        foreach (var piece in System.Text.RegularExpressions.Regex.Split(old, @"[^\p{L}\p{N} ]+").Select(p => p.Trim()).Where(p => Bare(p).Length >= 5))
            if (!now.Contains(Bare(piece), StringComparison.Ordinal) && onPage.Contains(Bare(piece), StringComparison.Ordinal))
                return Tr.T("Auf der Seite steht noch „{0}“ aus dem alten Inhalt — passt der Text noch zum neuen Code?", BarcodeInterpreter.Safe(piece));
        return null;
    }

    /// <summary>Why the content gives no code; null if it does.</summary>
    public string? Error => Code is { } c ? CodeHints.Error(c) : null;

    public bool HasError => Error is not null;

    /// <summary>„Modulbreite 0,42 mm“.</summary>
    public string ModuleText => Code is { } c ? CodeHints.ModuleText(c) : "";

    /// <summary>Modules smaller than scanners read reliably.</summary>
    public string? ModuleWarning => Code is { } c ? CodeHints.ModuleWarning(c) : null;

    /// <summary>Too little contrast, or light modules on a dark background.</summary>
    public string? ContrastWarning => Code is { } c ? CodeHints.ContrastWarning(c) : null;

    [ObservableProperty]
    private string _checkText = "";

    [ObservableProperty]
    private bool _checkFailed;

    private void Change(Func<CodeElement, CodeElement> change, string description, bool continueLast = false, bool remember = true)
    {
        if (_doc is null || _selected is not { Element: CodeElement code } selected || !CanEdit) return;
        var updated = change(code);
        if (Same(updated, code)) return;
        // the next code looks like this one (its content and alternative text are its own)
        if (remember) ElementDefaults.Code = updated with { Rect = default, Rotation = 0, Text = "", AltText = "" };
        _doc.ReplaceElement(selected, updated, description, continueLast);
    }

    private static bool Same(CodeElement a, CodeElement b) =>
        a.Format == b.Format && a.Text == b.Text && a.Gs1 == b.Gs1 && a.Level == b.Level && a.Shape == b.Shape && a.Color == b.Color
        && a.Background == b.Background && a.QuietZone == b.QuietZone && a.Caption == b.Caption && a.AltText == b.AltText && a.Rect == b.Rect;

    /// <summary>
    /// The self-check and the hint on old text after a short pause (typing changes the code with every key), cancelled by the next change.
    /// </summary>
    private async void StartCheck()
    {
        _check?.Cancel();
        if (Code is not { } code)
        {
            CheckText = "";
            CheckFailed = false;
            BesideHint = null;
            return;
        }
        var cancel = (_check = new CancellationTokenSource()).Token;
        var doc = _doc;
        var page = _selected?.Page ?? -1;
        CheckText = HasError ? "" : Tr.T("Probe läuft …");
        CheckFailed = false;
        try
        {
            await Task.Delay(150, cancel);
            if (code.Replaces is not null && doc is not null && (uint)page < (uint)doc.Pages.Count)
            {
                var id = doc.Pages[page].Item.Id;
                var list = doc.Renderer.TryGetDisplayList(id) ?? await doc.Renderer.GetDisplayList(id);
                if (cancel.IsCancellationRequested) return;
                BesideHint = list is null ? null : Beside(code, list.Text);
            }
            else BesideHint = null;
            if (HasError) return;
            var ok = await CodeHints.SelfCheck(code, cancel);
            CheckText = CodeHints.CheckText(ok);
            CheckFailed = !ok;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
