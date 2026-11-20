using CommunityToolkit.Mvvm.ComponentModel;
using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Barcodes.DataMatrix;
using PdfKnead.Core.Barcodes.Qr;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;

namespace PdfKnead.ViewModels;

/// <summary>
/// Code fields in the field panel (KONZEPT.md, section 5.19, step 4): the kind and the look of the code — the same choices as for an inserted
/// code —, the content, and what the code is worth (module size, contrast, self-check). The look belongs to the field (all its widgets);
/// the next code field and the next inserted code look like it.
/// </summary>
public sealed partial class FieldPropertiesViewModel
{
    private ElementRef? _selected;
    private bool _codes, _anyCode;
    private CancellationTokenSource? _codeCheck;

    /// <summary>Whether all selected fields are code fields.</summary>
    public bool IsCode => _codes;

    private CodeLook? Look => _codes ? _spec.Code : null;

    /// <summary>The selected field's code as it is drawn (its box, rotation and value); null without a code field.</summary>
    private CodeElement? Shown => Look is { } look && _element is { } element
        ? look.Apply(new CodeElement
        {
            Rect = element.Rect,
            Rotation = element is ForeignAnnotation foreign ? ElementAppearance.WidgetRotation(foreign.Original) : element.Rotation,
            Text = DocumentViewModel.ValueOf(element),
        })
        : null;

    public bool IsCodeMatrix => Look?.IsMatrix == true;
    public bool IsCodeLinear => Look is { IsMatrix: false };
    public bool IsCodeQr => Look?.Format == BarcodeFormat.Qr;
    public bool IsCodeDataMatrix => Look?.Format == BarcodeFormat.DataMatrix;

    private void ChangeLook(Func<CodeLook, CodeLook> change, string description, Func<PageElement, FieldSpec, PageElement>? adjust = null)
    {
        if (Look is null) return;
        Change(s => s.Code is { } look ? s with { Code = change(look) } : s, true, description, adjust);
        if (Look is { } now) ElementDefaults.Code = now.Apply(ElementDefaults.Code);
    }

    public Choice<BarcodeFormat>? CodeFormat
    {
        get => Look is { } l ? CodePropertiesViewModel.Formats.FirstOrDefault(f => f.Value == l.Format) : null;
        set
        {
            if (value is null || Look is not { } look || look.Format == value.Value) return;
            // another family needs another shape: its default size around the same centre (a field keeps its size within a family)
            var family = look.IsMatrix != CodeSymbol.IsMatrix(value.Value);
            ChangeLook(l => l with { Format = value.Value }, Tr.T("Codeart ändern"), family ? Resized : null);
        }
    }

    private static PageElement Resized(PageElement element, FieldSpec spec)
    {
        var rotation = element is ForeignAnnotation foreign ? ElementAppearance.WidgetRotation(foreign.Original) : element.Rotation;
        var (w, h) = ElementAppearance.DefaultCodeSize(spec.Code!.Apply(new CodeElement { Rect = default, Text = DocumentViewModel.ValueOf(element) }));
        if (rotation is 90 or 270) (w, h) = (h, w);
        var r = element.Rect;
        double cx = (r.Left + r.Right) / 2, cy = (r.Bottom + r.Top) / 2;
        return element with { Rect = new PdfRectangle(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2) };
    }

    public bool CodeGs1
    {
        get => Look?.Gs1 == true;
        set => ChangeLook(l => l with { Gs1 = value }, Tr.T("GS1 umschalten"));
    }

    public Choice<QrErrorCorrection>? CodeLevel
    {
        get => Look is { } l ? CodePropertiesViewModel.Levels.FirstOrDefault(c => c.Value == l.Level) : null;
        set
        {
            if (value is not null) ChangeLook(l => l with { Level = value.Value }, Tr.T("Fehlerkorrektur ändern"));
        }
    }

    public Choice<DataMatrixShape>? CodeShape
    {
        get => Look is { } l ? CodePropertiesViewModel.Shapes.FirstOrDefault(c => c.Value == l.Shape) : null;
        set
        {
            if (value is not null) ChangeLook(l => l with { Shape = value.Value }, Tr.T("Form ändern"));
        }
    }

    public FieldColorOption? CodeColor
    {
        get => Look is { } l ? CodePropertiesViewModel.Colors.FirstOrDefault(o => o.Rgb == l.Color) : null;
        set
        {
            if (value?.Rgb is { } rgb) ChangeLook(l => l with { Color = rgb }, Tr.T("Farbe ändern"));
        }
    }

    public FieldColorOption? CodeBackground
    {
        get => Look is { } l ? CodePropertiesViewModel.Backgrounds.FirstOrDefault(o => o.Rgb == l.Background) : null;
        set
        {
            if (value is not null) ChangeLook(l => l with { Background = value.Rgb }, Tr.T("Hintergrund ändern"));
        }
    }

    public bool CodeQuietZone
    {
        get => Look?.QuietZone == true;
        set => ChangeLook(l => l with { QuietZone = value }, Tr.T("Ruhezone umschalten"));
    }

    public bool CodeCaption
    {
        get => Look?.Caption == true;
        set => ChangeLook(l => l with { Caption = value }, Tr.T("Klartext umschalten"));
    }

    /// <summary>
    /// The content (the field's value), taken over when the box is left; content that gives no code is refused with the reason, so the
    /// file never holds a field whose code other readers would show as a placeholder.
    /// </summary>
    public string CodeContent
    {
        get => _element is { } e && _codes ? DocumentViewModel.ValueOf(e) : "";
        set
        {
            if (_refreshing || _doc is null || _selected is not { } selected || Shown is not { } shown || value == shown.Text) return;
            if (value.Length > 0 && CodeHints.Error(shown with { Text = value }) is { } error)
            {
                _doc.Notify(error);
                OnPropertyChanged(nameof(CodeContent));
                return;
            }
            _doc.SetFieldValue(selected, value);
            Refresh(_doc);
        }
    }

    public string CodeContentHint => Look is { } l ? CodeHints.ContentHint(l.Format, l.Gs1) : "";

    /// <summary>Why the stored content gives no code (a field of a crafted file, a changed kind); null if it does or is empty.</summary>
    public string? CodeError => Shown is { Text.Length: > 0 } c ? CodeHints.Error(c) : null;

    public bool HasCodeError => CodeError is not null;

    public string CodeModuleText => Shown is { Text.Length: > 0 } c ? CodeHints.ModuleText(c) : "";

    public string? CodeModuleWarning => Shown is { Text.Length: > 0 } c ? CodeHints.ModuleWarning(c) : null;

    public string? CodeContrastWarning => Shown is { Text.Length: > 0 } c ? CodeHints.ContrastWarning(c) : null;

    [ObservableProperty]
    private string _codeCheckText = "";

    [ObservableProperty]
    private bool _codeCheckFailed;

    /// <summary>The self-check of the selected code field after a short pause, cancelled by the next change.</summary>
    private async void StartCodeCheck()
    {
        _codeCheck?.Cancel();
        if (Shown is not { Text.Length: > 0 } code || CodeHints.Error(code) is not null)
        {
            CodeCheckText = "";
            CodeCheckFailed = false;
            return;
        }
        var cancel = (_codeCheck = new CancellationTokenSource()).Token;
        CodeCheckText = Tr.T("Probe läuft …");
        CodeCheckFailed = false;
        try
        {
            await Task.Delay(150, cancel);
            var ok = await CodeHints.SelfCheck(code, cancel);
            CodeCheckText = CodeHints.CheckText(ok);
            CodeCheckFailed = !ok;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
