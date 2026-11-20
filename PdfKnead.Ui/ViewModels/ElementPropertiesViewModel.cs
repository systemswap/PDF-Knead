using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfKnead.Core.Pdf.Editing;

namespace PdfKnead.ViewModels;

/// <summary>
/// Properties of the selected shape or inserted image, in the right-hand panel as for form fields: stroke, fill, width, dashed,
/// opacity; for images also size and sharpening. Each change is one step; shape changes also apply to the next shape (like the
/// appearance of new fields). See KONZEPT.md, section 5.13.
/// </summary>
public sealed partial class ElementPropertiesViewModel : ObservableObject
{
    private DocumentViewModel? _doc;
    private ElementRef? _selected;

    // Fixed colours first, then up to MaxCustom custom ones (colour picker or from the file). A mutable list: a new entry keeps the
    // selection of the combo boxes, whereas a new list would discard it
    private static readonly ObservableCollection<FieldColorOption> ColorList =
    [
        new(null, "Keine"),
        new(0x000000, "Schwarz"),
        new(0x8C8C8C, "Grau"),
        new(0xD4D4D4, "Hellgrau"),
        new(0xFFFFFF, "Weiß"),
        new(0x1F4E9A, "Blau"),
        new(0xE6EEFF, "Hellblau"),
        new(0xC00000, "Rot"),
        new(0xED7D31, "Orange"),
        new(0xFFC000, "Gelb"),
        new(0xFFF6CC, "Hellgelb"),
        new(0x1E7B34, "Grün"),
    ];

    private static readonly int StandardCount = ColorList.Count;

    private const int MaxCustom = 6;

    private const string CustomLabel = "Eigene Farbe";

    public static IReadOnlyList<FieldColorOption> Colors => ColorList;

    public static IReadOnlyList<Choice<double>> StrokeWidths { get; } =
        [.. new[] { 0.5, 1, 1.5, 2, 3, 4, 6, 8, 12 }.Select(w => new Choice<double>(w, w.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)))];

    /// <summary>All German labels of the lists (for the translation check).</summary>
    public static IEnumerable<string> AllLabels => Colors.Take(StandardCount).Select(c => c.Label).Append(CustomLabel).Concat(StrokeWidths.Select(c => c.Label));

    /// <summary>Rereads the properties (selection changed, undo, another document).</summary>
    /// <param name="doc">The current document, or null.</param>
    public void Refresh(DocumentViewModel? doc)
    {
        _doc = doc;
        // a redaction mark in the redaction tools only once picked by a click: just drawn, the panel would cover the page where the next
        // bar is drawn
        _selected = doc is { IsFieldDesign: false, SelectedElement: { Element: ShapeElement or ImageElement { IsSignature: false } } selected } ? selected
            : doc is { IsFieldDesign: false, SelectedElement: { Element: RedactionElement { Hidden: false } } mark } &&
              (doc.MarkPicked || doc.Tool is not (ViewTool.RedactText or ViewTool.InsertRedaction))
                ? mark : null;
        // Colour picker running but the shape is no longer selected (undo, other selection, other document): cancel
        if (_pickingDoc is { } picking && (!ReferenceEquals(picking, doc) || Shape is null)) picking.EndColorPick(null);
        RememberColors();
        OnPropertyChanged(string.Empty);
    }

    private ShapeElement? Shape => _selected?.Element as ShapeElement;

    private ImageElement? Picture => _selected?.Element as ImageElement;

    /// <summary>Whether the panel is visible: a shape or an inserted image is selected.</summary>
    public bool IsVisible => _selected is not null;

    /// <summary>Whether changes are allowed; protected files without the „Ändern“/„Kommentieren“ permission can only be viewed.</summary>
    public bool CanEdit => _doc?.CanEditElements == true;

    public bool IsShape => Shape is not null;

    private RedactionElement? Redaction => _selected?.Element as RedactionElement;

    /// <summary>Whether a redaction mark is selected: the panel offers its reason (KONZEPT.md, section 5.6, B-14).</summary>
    public bool IsRedaction => Redaction is not null;

    /// <summary>The reason of the selected mark; empty: the reason given when applying applies.</summary>
    public string Reason
    {
        get => Redaction?.Reason ?? "";
        set => ReasonChoice = Core.Pdf.Editing.RedactionReasons.Clean(value) is { } reason ? new Core.Pdf.Editing.ReasonChoice(reason, null, null, Redaction?.ReasonInBar ?? true) : null;
    }

    /// <summary>
    /// The reason of the selected mark with its code, legal basis and „Code im Balken“ (the block „Schwärzungsgrund“, wish of 4.11.2026);
    /// null: none — the reason given when applying applies.
    /// </summary>
    public Core.Pdf.Editing.ReasonChoice? ReasonChoice
    {
        get => Redaction is { Reason: { } reason } r ? new Core.Pdf.Editing.ReasonChoice(reason, r.ReasonCode, r.ReasonBasis, r.ReasonInBar) : null;
        set
        {
            if (_doc is null || _selected is not { Element: RedactionElement mark } selected || !CanEdit) return;
            var updated = value is { } choice ? choice.On(mark) : mark with { Reason = null, ReasonCode = null, ReasonBasis = null };
            if (updated == mark) return;
            if (_doc.ReplaceElement(selected, updated, Tr.T("Schwärzungsgrund ändern"), continueLast: true)) _selected = new ElementRef(selected.Page, updated);
            if (value is { Code: null, Reason: var own }) RememberReason(own);
            OnPropertyChanged(nameof(Reason));
            OnPropertyChanged(nameof(ReasonChoice));
        }
    }

    /// <summary>The bar of the selected mark the code goes into (its largest area), for the warning where it does not fit.</summary>
    public IReadOnlyList<Core.Pdf.Document.PdfRectangle>? RedactionBars => Redaction is { } r ? [r.PageAreas.MaxBy(a => a.Width * a.Height)] : null;

    /// <summary>The reason of the selected mark for every mark of the document that has none yet (all hits of „Suchen und schwärzen“ at once).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ReasonForAll()
    {
        if (_doc is null || ReasonChoice is not { } reason || !CanEdit) return;
        _doc.SetRedactionReasons(reason);
        Refresh(_doc);
    }

    /// <summary>An own reason goes into the list for next time (the usual ones are there anyway; at most 20).</summary>
    internal static void RememberReason(string? reason)
    {
        if (reason is null || Core.Pdf.Editing.RedactionReasons.Suggestions.Contains(reason)) return;
        Services.UserSettings.Update(s => s with
        {
            RedactionReasons = [reason, .. (s.RedactionReasons ?? []).Where(r => r != reason).Take(19)],
        });
    }

    public bool IsImage => Picture is not null;

    public bool IsLine => Shape?.IsLine == true;

    public bool HasFill => Shape is { IsLine: false };

    public string Title => Redaction is not null ? Tr.T("Schwärzung") : Picture is not null ? Tr.T("Bild") : Shape?.Kind switch
    {
        ShapeKind.Rectangle => Tr.T("Rechteck"),
        ShapeKind.Ellipse => Tr.T("Ellipse"),
        ShapeKind.Line => Tr.T("Linie"),
        ShapeKind.Arrow => Tr.T("Pfeil"),
        ShapeKind.Triangle => Tr.T("Dreieck"),
        ShapeKind.Diamond => Tr.T("Raute"),
        ShapeKind.Star => Tr.T("Stern"),
        ShapeKind.Callout => Tr.T("Sprechblase"),
        _ => "",
    };

    public Avalonia.Media.Geometry? KindIcon =>
        Avalonia.Application.Current?.TryGetResource(Redaction is not null ? "Icon.ShieldLock" : Picture is not null ? "Icon.Image" : IconKey(Shape?.Kind ?? ShapeKind.Rectangle), null,
            out var icon) == true ? icon as Avalonia.Media.Geometry : null;

    public static string IconKey(ShapeKind kind) => kind switch
    {
        ShapeKind.Ellipse => "Icon.Circle",
        ShapeKind.Line => "Icon.LineHorizontal",
        ShapeKind.Arrow => "Icon.ArrowUpRight",
        ShapeKind.Triangle => "Icon.Triangle",
        ShapeKind.Diamond => "Icon.Diamond",
        ShapeKind.Star => "Icon.Star",
        ShapeKind.Callout => "Icon.ChatEmpty",
        _ => "Icon.Square",
    };

    /// <summary>
    /// Colours for lines, without „Keine“ (the line would be invisible and could no longer be grabbed). A separate, fixed list with
    /// its own combo box: when the list of a combo box is swapped, it discards its selection (the box was empty for arrows).
    /// </summary>
    private static readonly ObservableCollection<FieldColorOption> LineColorList = [.. ColorList.Skip(1)];

    public static IReadOnlyList<FieldColorOption> LineColors => LineColorList;

    public FieldColorOption? StrokeColor
    {
        get => Option(Shape?.StrokeColor);
        set
        {
            if (value is null || IsLine && value.Rgb is null) return;
            Change(s => s with { StrokeColor = value.Rgb }, Tr.T("Rahmenfarbe ändern"));
        }
    }

    public FieldColorOption? FillColor
    {
        get => Option(Shape?.FillColor);
        set
        {
            if (value is not null) Change(s => s with { FillColor = value.Rgb }, Tr.T("Füllfarbe ändern"));
        }
    }

    public Choice<double>? StrokeWidth
    {
        get => Shape is { } s ? StrokeWidths.MinBy(w => Math.Abs(w.Value - s.StrokeWidth)) : null;
        set
        {
            if (value is not null) Change(s => s with { StrokeWidth = value.Value }, Tr.T("Rahmendicke ändern"));
        }
    }

    public bool Dashed
    {
        get => Shape?.Dashed == true;
        set => Change(s => s with { Dashed = value }, Tr.T("Strichart ändern"));
    }

    /// <summary>Opacity in percent (10 … 100), for shapes and images.</summary>
    public double Opacity
    {
        get => Math.Round((Shape?.Opacity ?? Picture?.Opacity ?? 1) * 100);
        set
        {
            var opacity = Math.Clamp(Math.Round(value) / 100, 0.1, 1);
            if (Picture is not null) ChangePicture(p => p with { Opacity = opacity }, Tr.T("Deckkraft ändern"), continueLast: true);
            else Change(s => s with { Opacity = opacity }, Tr.T("Deckkraft ändern"), continueLast: true);
        }
    }

    // ------------------------------------------------------------ Images

    private const double PointsPerMm = 72 / 25.4;

    /// <summary>Chain closed: width and height change together (also with the handles); open: independent, the image is stretched.</summary>
    public bool KeepAspect
    {
        get => Picture?.KeepAspect != false;
        set
        {
            if (Picture is not { } picture || picture.KeepAspect == value) return;
            // Closed again: fit the rectangle to the image's aspect ratio (width stays), otherwise the image would sit inside it with a margin
            ChangePicture(p => value ? WithUprightSize(p with { KeepAspect = true }, UprightWidth(p), UprightWidth(p) * p.Image.Height / Math.Max(1, p.Image.Width)) : p with { KeepAspect = false },
                value ? Tr.T("Seitenverhältnis beibehalten") : Tr.T("Seitenverhältnis frei"));
        }
    }

    /// <summary>Width in millimetres (in reading direction), in the current culture's format; when typed with the chain closed, the height changes too.</summary>
    public string WidthMm
    {
        get => Picture is { } p ? Format(UprightWidth(p) / PointsPerMm) : "";
        set
        {
            if (Picture is not { } p || Parse(value) is not { } mm) return;
            var width = mm * PointsPerMm;
            var height = p.KeepAspect ? width * UprightHeight(p) / Math.Max(1e-6, UprightWidth(p)) : UprightHeight(p);
            ChangePicture(x => WithUprightSize(x, width, height), Tr.T("Bildgröße ändern"));
        }
    }

    /// <summary>Height in millimetres; when typed with the chain closed, the width changes too.</summary>
    public string HeightMm
    {
        get => Picture is { } p ? Format(UprightHeight(p) / PointsPerMm) : "";
        set
        {
            if (Picture is not { } p || Parse(value) is not { } mm) return;
            var height = mm * PointsPerMm;
            var width = p.KeepAspect ? height * UprightWidth(p) / Math.Max(1e-6, UprightHeight(p)) : UprightWidth(p);
            ChangePicture(x => WithUprightSize(x, width, height), Tr.T("Bildgröße ändern"));
        }
    }

    /// <summary>Sharpening in percent (0 = off … 100 = strong).</summary>
    public double Sharpness
    {
        get => Math.Round((Picture?.Sharpness ?? 0) * 100);
        set => ChangePicture(p => p with { Sharpness = Math.Clamp(Math.Round(value) / 100, 0, 1) }, Tr.T("Schärfe ändern"), continueLast: true);
    }

    /// <summary>
    /// What screen readers read for the image (/Contents of its stamp; docs/ABLAUFPLAN.md, 6.4 step 3); empty: „Bild“ in the document's language.
    /// </summary>
    public string ImageAltText
    {
        get => Picture?.AltText ?? "";
        set => ChangePicture(p => p with { AltText = (value ?? "").Trim() }, Tr.T("Alternativtext ändern"));
    }

    /// <summary>Whether the image reaches beyond the edge of its page (then „Am Blattrand abschneiden“ shows; No. 622).</summary>
    public bool ImageOverhangsPage => _doc is not null && _selected is { Element: ImageElement } selected && _doc.OverhangsPage(selected);

    /// <summary>Cuts off what the image has beyond the edge of its page (one undo step).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void CropAtPageEdge()
    {
        if (_doc is null || _selected is not { Element: ImageElement } selected || !CanEdit) return;
        _doc.CropAtPageEdge(selected);
        if (_doc.SelectedElement is { } now) _selected = now;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Whether placing an image beyond the edge asks about cutting it off (the setting, here to switch it on again).</summary>
    public bool AskCropAtPageEdge
    {
        get => _doc?.AskCropAtPageEdge ?? true;
        set
        {
            if (_doc is null) return;
            _doc.AskCropAtPageEdge = value;
            OnPropertyChanged();
        }
    }

    private static double UprightWidth(ImageElement p) => p.UprightSize.Width;

    private static double UprightHeight(ImageElement p) => p.UprightSize.Height;

    /// <summary>New size in reading direction (points, clamped to 2 … 14,400); the top left corner (as displayed on an unrotated page) stays.</summary>
    private static ImageElement WithUprightSize(ImageElement p, double width, double height)
    {
        width = Math.Clamp(width, 2, 14_400);
        height = Math.Clamp(height, 2, 14_400);
        var (w, h) = p.Rotation is 90 or 270 ? (height, width) : (width, height);
        var r = p.Rect;
        return p with { Rect = new Core.Pdf.Document.PdfRectangle(r.Left, r.Top - h, r.Left + w, r.Top) };
    }

    private static string Format(double mm) => mm.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);

    private static double? Parse(string? text) =>
        double.TryParse((text ?? "").Trim().Replace("mm", "").Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) && v > 0 ? v : null;

    private void ChangePicture(Func<ImageElement, ImageElement> change, string description, bool continueLast = false)
    {
        if (_doc is null || _selected is not { Element: ImageElement picture } selected || !CanEdit) return;
        var updated = change(picture);
        if (updated.Rect == picture.Rect && updated.KeepAspect == picture.KeepAspect && updated.Opacity.Equals(picture.Opacity) &&
            updated.Sharpness.Equals(picture.Sharpness) && updated.AltText == picture.AltText) return;
        if (_doc.ReplaceElement(selected, updated, description, continueLast)) _selected = new ElementRef(selected.Page, updated);
        OnPropertyChanged(string.Empty);
    }

    private static FieldColorOption Option(int? rgb) =>
        Colors.FirstOrDefault(c => c.Rgb == rgb) ?? new FieldColorOption(rgb, CustomLabel);

    // ------------------------------------------------------------ Custom colours and colour picker

    /// <summary>Adds the colours of the selected shape to the lists if they are missing there (otherwise the combo box would stay empty).</summary>
    private void RememberColors()
    {
        if (Shape is not { } shape) return;
        Remember(shape.StrokeColor, shape);
        Remember(shape.FillColor, shape);
    }

    private static void Remember(int? rgb, ShapeElement inUse)
    {
        if (rgb is not { } color || ColorList.Any(c => c.Rgb == color)) return;
        var option = new FieldColorOption(color, CustomLabel);
        ColorList.Add(option);
        LineColorList.Add(option);
        // At most MaxCustom custom colours: the oldest goes, unless the shape is currently using it
        for (var i = StandardCount; ColorList.Count - StandardCount > MaxCustom && i < ColorList.Count;)
        {
            var old = ColorList[i];
            if (old.Rgb == inUse.StrokeColor || old.Rgb == inUse.FillColor)
            {
                i++;
                continue;
            }
            ColorList.RemoveAt(i);
            LineColorList.Remove(old);
        }
    }

    private enum ColorTarget
    {
        None,
        Stroke,
        Fill,
    }

    private ColorTarget _picking;
    private DocumentViewModel? _pickingDoc;

    /// <summary>Colour picker for the stroke or line: on = the next click on the page picks up the colour.</summary>
    public bool IsPickingStroke
    {
        get => _picking == ColorTarget.Stroke;
        set => Pick(value ? ColorTarget.Stroke : ColorTarget.None);
    }

    /// <summary>Colour picker for the fill.</summary>
    public bool IsPickingFill
    {
        get => _picking == ColorTarget.Fill;
        set => Pick(value ? ColorTarget.Fill : ColorTarget.None);
    }

    private void Pick(ColorTarget target)
    {
        _pickingDoc?.EndColorPick(null);
        if (target == ColorTarget.None || _doc is not { } doc || Shape is null || !CanEdit) return;
        _picking = target;
        _pickingDoc = doc;
        OnPropertyChanged(nameof(IsPickingStroke));
        OnPropertyChanged(nameof(IsPickingFill));
        doc.BeginColorPick(rgb =>
        {
            var picked = _picking;
            _picking = ColorTarget.None;
            _pickingDoc = null;
            if (rgb is { } stroke && picked == ColorTarget.Stroke) Change(s => s with { StrokeColor = stroke }, Tr.T("Rahmenfarbe ändern"));
            else if (rgb is { } fill && picked == ColorTarget.Fill) Change(s => s with { FillColor = fill }, Tr.T("Füllfarbe ändern"));
            OnPropertyChanged(nameof(IsPickingStroke));
            OnPropertyChanged(nameof(IsPickingFill));
        });
    }

    private void Change(Func<ShapeElement, ShapeElement> change, string description, bool continueLast = false)
    {
        if (_doc is null || _selected is not { Element: ShapeElement shape } selected || !CanEdit) return;
        var updated = change(shape);
        if (Same(updated, shape)) return;
        // The next shape looks like this one (kind, position and line ends remain its own)
        ElementDefaults.Shape = ElementDefaults.Shape with
        {
            StrokeColor = updated.IsLine && updated.StrokeColor is null ? ElementDefaults.Shape.StrokeColor : updated.StrokeColor,
            FillColor = updated.IsLine ? ElementDefaults.Shape.FillColor : updated.FillColor,
            StrokeWidth = updated.StrokeWidth,
            Dashed = updated.Dashed,
            Opacity = updated.Opacity,
        };
        if (_doc.ReplaceElement(selected, updated, description, continueLast)) _selected = new ElementRef(selected.Page, updated);
        RememberColors();
        OnPropertyChanged(string.Empty);
    }

    private static bool Same(ShapeElement a, ShapeElement b) =>
        a.StrokeColor == b.StrokeColor && a.FillColor == b.FillColor && a.StrokeWidth.Equals(b.StrokeWidth) && a.Dashed == b.Dashed && a.Opacity.Equals(b.Opacity);
}
