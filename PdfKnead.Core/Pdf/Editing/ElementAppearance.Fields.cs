using System.Text;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Appearances of form fields built from their properties (12.7.4.3): new fields (<see cref="FieldElement"/>) and fields of
/// other programs whose properties have changed. Drawn the way common PDF editors draw them — border in the chosen style, round radio
/// buttons, check marks as paths (no font; other programs redraw them from /MK /CA with ZapfDingbats), and text with an
/// embedded subset as when filling in.
/// </summary>
public sealed partial class ElementAppearance
{
    /// <summary>
    /// Appearance states of a check box or radio button (/AP /N): the on state and "Off"; <see langword="null"/> for other
    /// elements.
    /// </summary>
    public IReadOnlyDictionary<string, PdfStream>? States { get; private init; }

    /// <summary>Gets the name of the on state of a check box or radio button.</summary>
    /// <param name="spec">The field properties.</param>
    /// <returns>The export value, or "Ja" if the export value is empty or "Off"; never empty and never "Off".</returns>
    public static string OnStateOf(FieldSpec spec) => spec.ExportValue is { Length: > 0 } e && e != "Off" ? e : "Ja";

    /// <summary>Gets the current value of a field of another program: the filled-in value or the one from the file.</summary>
    /// <param name="element">The field's widget on the page.</param>
    /// <param name="field">The form field.</param>
    /// <returns>The filled-in value if there is one; otherwise the state of the widget for check boxes and radio buttons, and the stored value for all other fields.</returns>
    public static string ValueOf(ForeignAnnotation element, FormField field) =>
        element.Value ?? (field.Kind is FormFieldKind.CheckBox or FormFieldKind.Radio ? field.State : field.StoredValue);

    /// <summary>
    /// Determines whether a field of another program needs a new appearance. Only a change of its look or its size requires one;
    /// a new name or "required" leaves the other program's appearance as it is.
    /// </summary>
    /// <param name="foreign">The field's widget.</param>
    /// <returns><see langword="true"/> if the appearance must be rebuilt.</returns>
    public static bool NeedsNewLook(ForeignAnnotation foreign)
    {
        if (foreign.Spec is not { } spec || FieldSpec.Of(foreign) is not { } original) return false;
        return foreign.IsResized || !spec.SameLook(original);
    }

    /// <summary>Gets the rotation of a widget from /MK /R.</summary>
    /// <param name="widget">The widget annotation dictionary.</param>
    /// <returns>0, 90, 180 or 270 degrees; 0 if the entry is missing or not a multiple of 90.</returns>
    public static int WidgetRotation(PdfDictionary widget)
    {
        var r = (int)Math.Clamp(widget.GetDictionary(PdfName.Get("MK"))?.GetNumber(PdfName.Get("R")) ?? 0, -3600, 3600);
        r = ((r % 360) + 360) % 360;
        return r % 90 == 0 ? r : 0;
    }

    /// <summary>
    /// Gets a field's text as its appearance lays it out (font, size — fitted automatically —, colour, alignment, rotation); also
    /// used for the input box when filling in new or changed fields.
    /// </summary>
    /// <param name="element">The field element or the widget of another program's field.</param>
    /// <param name="spec">The field properties.</param>
    /// <param name="shown">The text to show.</param>
    /// <returns>The text element with its final font size.</returns>
    public static TextElement FieldText(PageElement element, FieldSpec spec, string shown)
    {
        var rotation = element is ForeignAnnotation foreign ? WidgetRotation(foreign.Original) : element.Rotation;
        var geometry = element.Rotation == rotation ? element : element with { Rotation = rotation };
        var bw = spec.BorderColor is null ? 0 : Math.Clamp(spec.BorderWidth, 0, 12);
        return SpecText(geometry, spec, shown, bw).Text;
    }

    private static (TextElement Text, int Comb) SpecText(PageElement element, FieldSpec spec, string shown, double bw)
    {
        var multiline = spec.Kind == FormFieldKind.Text && spec.Multiline;
        var text = new TextElement
        {
            Rect = element.Rect,
            Rotation = element.Rotation,
            Text = multiline ? shown.ReplaceLineEndings("\n") : shown.ReplaceLineEndings(" "),
            Font = spec.Font,
            FontSize = spec.FontSize > 0 ? Math.Min(spec.FontSize, 500) : 12,
            Color = spec.TextColor,
            Alignment = spec.Kind == FormFieldKind.PushButton ? ElementAlignment.Center : spec.Alignment,
            Multiline = multiline,
        };
        var comb = spec.Kind == FormFieldKind.Text && spec.Comb && spec.MaxLength > 0 && !spec.Multiline && !spec.Password ? spec.MaxLength : 0;
        if (spec.FontSize <= 0) text = AutoSize(text, bw, comb > 0);
        else if (spec.ShrinkToFit) text = Shrink(text, bw, comb > 0);   // starting size, smaller if the text does not fit
        return (text, comb);
    }

    /// <summary>Builds the appearance from the properties; <paramref name="rotation"/> rotates the content (/MK /R for fields of other programs).</summary>
    private static ElementAppearance BuildField(PageElement owner, FieldSpec spec, string value, int rotation)
    {
        // Size and rotation of the appearance; the appearance still belongs to the element itself
        var element = owner.Rotation == rotation ? owner : owner with { Rotation = rotation };
        var bg = spec.FillColor is { } f ? Rgb(f) : null;
        var bc = spec.BorderColor is { } b ? Rgb(b) : null;
        var bw = bc is null ? 0 : Math.Clamp(spec.BorderWidth, 0, 12);
        if (spec is { Kind: FormFieldKind.Text, Code: { } look }) return BuildCodeField(owner, element, look, value);
        switch (spec.Kind)
        {
            case FormFieldKind.CheckBox or FormFieldKind.Radio:
                return BuildCheck(owner, element, spec, value, bg, bc, bw);
            case FormFieldKind.ListBox:
                return BuildList(owner, element, spec, value, bg, bc, bw);
            case FormFieldKind.Text or FormFieldKind.ComboBox or FormFieldKind.PushButton:
                var shown = spec.Kind switch
                {
                    FormFieldKind.PushButton => spec.Caption,
                    FormFieldKind.ComboBox => spec.Options.FirstOrDefault(o => o.Export == value)?.Display ?? value,
                    _ when spec.Password => new string('*', value.Length),
                    _ => spec.Format.Display(value),
                };
                var (text, comb) = SpecText(element, spec, shown, bw);
                return DrawFieldText(owner, text, bg, bc, bw, comb, spec.BorderStyle, middle: spec.Kind == FormFieldKind.PushButton);
            default:
                // Signature field: border and background
                var (w, h) = element.UprightSize;
                var sb = new StringBuilder("q\n");
                AppendFrame(sb, w, h, bg, bc, bw, spec.BorderStyle, round: false);
                sb.Append("Q\n");
                return new ElementAppearance(owner, FormStream(element, new PdfDictionary(), Encoding.ASCII.GetBytes(sb.ToString())), null, NoGlyphs);
        }
    }

    /// <summary>Check box or radio button: one appearance each for the on state and "Off"; the one matching the value is shown.</summary>
    private static ElementAppearance BuildCheck(PageElement owner, PageElement element, FieldSpec spec, string value, string? bg, string? bc, double bw)
    {
        var (w, h) = element.UprightSize;
        var round = spec.Kind == FormFieldKind.Radio;
        var off = new StringBuilder("q\n");
        AppendFrame(off, w, h, bg, bc, bw, spec.BorderStyle, round);
        off.Append("Q\n");
        var on = new StringBuilder(off.ToString()).Append("q\n");
        var size = spec.FontSize > 0 ? Math.Min(spec.FontSize, Math.Min(w, h)) : Math.Max(1, (Math.Min(w, h) - 2 * bw) * 0.8);
        AppendSymbol(on, spec.CheckStyle, w / 2, h / 2, size, Rgb(spec.TextColor));
        on.Append("Q\n");
        var onState = OnStateOf(spec);
        var onForm = FormStream(element, new PdfDictionary(), Encoding.ASCII.GetBytes(on.ToString()));
        var offForm = FormStream(element, new PdfDictionary(), Encoding.ASCII.GetBytes(off.ToString()));
        var states = new Dictionary<string, PdfStream>(StringComparer.Ordinal) { [onState] = onForm, ["Off"] = offForm };
        return new ElementAppearance(owner, value == onState ? onForm : offForm, null, NoGlyphs) { States = states };
    }

    /// <summary>List box: the entries one below the other, starting at the top, selected ones highlighted (as in common PDF editors).</summary>
    private static ElementAppearance BuildList(PageElement owner, PageElement element, FieldSpec spec, string value, string? bg, string? bc, double bw)
    {
        var (w, h) = element.UprightSize;
        var face = ElementFace.For(spec.Font);
        var size = spec.FontSize > 0 ? Math.Min(spec.FontSize, 500) : 12;
        var lineHeight = size * LineSpacing;
        var chosen = value.Split('\n').ToHashSet(StringComparer.Ordinal);
        var glyphs = new Dictionary<int, string>();
        var sb = new StringBuilder("q\n");
        AppendFrame(sb, w, h, bg, bc, bw, spec.BorderStyle, round: false);
        sb.Append("Q\n/Tx BMC\nq\n");
        sb.Append(ElementFace.Number(bw)).Append(' ').Append(ElementFace.Number(bw)).Append(' ')
          .Append(ElementFace.Number(Math.Max(0, w - 2 * bw))).Append(' ').Append(ElementFace.Number(Math.Max(0, h - 2 * bw))).Append(" re W n\n");
        var top = h - bw - Padding / 2;
        var lines = 0;
        foreach (var option in spec.Options)
        {
            if (top - lineHeight * lines < -lineHeight || lines >= 500) break;   // only what is visible
            var y = top - lineHeight * (lines + 1);
            if (chosen.Contains(option.Export))
                sb.Append("0.6 0.757 0.855 rg ").Append(ElementFace.Number(bw)).Append(' ').Append(ElementFace.Number(y)).Append(' ')
                  .Append(ElementFace.Number(Math.Max(0, w - 2 * bw))).Append(' ').Append(ElementFace.Number(lineHeight)).Append(" re f\n");
            var line = Layout(new TextElement { Rect = element.Rect, Text = option.Display.Length > 500 ? option.Display[..500] : option.Display, Font = spec.Font, FontSize = size, Multiline = false }, face)[0];
            if (line.Glyphs.Count > 0)
            {
                var x = spec.Alignment switch
                {
                    ElementAlignment.Center => (w - line.Width) / 2,
                    ElementAlignment.Right => w - bw - Padding - line.Width,
                    _ => bw + Padding,
                };
                sb.Append("BT /F1 ").Append(ElementFace.Number(size)).Append(" Tf ").Append(Rgb(spec.TextColor)).Append(" rg 1 0 0 1 ")
                  .Append(ElementFace.Number(x)).Append(' ').Append(ElementFace.Number(y + (lineHeight - (face.Ascent - face.Descent) * size) / 2 - face.Descent * size))
                  .Append(" Tm ");
                AppendShowText(sb, face, line, size);
                sb.Append("ET\n");
                for (var i = 0; i < line.Glyphs.Count; i++) glyphs.TryAdd(line.Glyphs[i], line.Text[i]);
            }
            lines++;
        }
        sb.Append("Q\nEMC\n");
        var fonts = new PdfDictionary();
        fonts.Set(PdfName.Get("F1"), face.DisplayFont);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("Font"), fonts);
        return new ElementAppearance(owner, FormStream(element, resources, Encoding.ASCII.GetBytes(sb.ToString())), face, glyphs);
    }

    /// <summary>Formats "r g b" operands for rg/RG.</summary>
    private static string Rgb(int rgb) =>
        $"{ElementFace.Number(((rgb >> 16) & 0xFF) / 255.0)} {ElementFace.Number(((rgb >> 8) & 0xFF) / 255.0)} {ElementFace.Number((rgb & 0xFF) / 255.0)}";

    /// <summary>
    /// Draws background and border in the given style (12.5.4): solid, dashed, bevelled (light at top left, dark at bottom right),
    /// inset (reversed) or underline only; radio buttons round.
    /// </summary>
    private static void AppendFrame(StringBuilder sb, double w, double h, string? background, string? border, double width, FieldBorderStyle style, bool round)
    {
        var r = Math.Min(w, h) / 2;
        if (background is { } bg)
        {
            sb.Append(bg).Append(" rg ");
            if (round) AppendCircle(sb, w / 2, h / 2, r).Append(" f\n");
            else sb.Append("0 0 ").Append(ElementFace.Number(w)).Append(' ').Append(ElementFace.Number(h)).Append(" re f\n");
        }
        if (border is not { } bc || width <= 0) return;
        sb.Append(bc).Append(" RG ").Append(ElementFace.Number(width)).Append(" w ");
        if (style == FieldBorderStyle.Dashed) sb.Append("[3] 0 d ");
        if (style == FieldBorderStyle.Underline)
        {
            sb.Append("0 ").Append(ElementFace.Number(width / 2)).Append(" m ").Append(ElementFace.Number(w)).Append(' ')
              .Append(ElementFace.Number(width / 2)).Append(" l S\n");
            return;
        }
        if (round) AppendCircle(sb, w / 2, h / 2, Math.Max(0, r - width / 2)).Append(" s\n");
        else
            sb.Append(ElementFace.Number(width / 2)).Append(' ').Append(ElementFace.Number(width / 2)).Append(' ')
              .Append(ElementFace.Number(Math.Max(0, w - width))).Append(' ').Append(ElementFace.Number(Math.Max(0, h - width))).Append(" re S\n");
        if (style == FieldBorderStyle.Dashed) sb.Append("[] 0 d\n");
        if (style is not (FieldBorderStyle.Beveled or FieldBorderStyle.Inset) || round || w <= 4 * width || h <= 4 * width) return;
        // Inner bevel: a band of the border width, with different brightness at top left and bottom right
        var (light, dark) = style == FieldBorderStyle.Beveled ? ("1 g", "0.5 g") : ("0.5 g", "0.75 g");
        double a = width, b = 2 * width;
        sb.Append(light).Append(' ');
        AppendPolygon(sb, [(a, a), (a, h - a), (w - a, h - a), (w - b, h - b), (b, h - b), (b, b)]).Append(" f\n");
        sb.Append(dark).Append(' ');
        AppendPolygon(sb, [(a, a), (w - a, a), (w - a, h - a), (w - b, h - b), (w - b, b), (b, b)]).Append(" f\n");
    }

    /// <summary>Draws a check box symbol as a path around (cx, cy) with size <paramref name="size"/>.</summary>
    private static void AppendSymbol(StringBuilder sb, CheckStyle style, double cx, double cy, double size, string color)
    {
        string P(double x, double y) => $"{ElementFace.Number(cx + x * size)} {ElementFace.Number(cy + y * size)}";
        switch (style)
        {
            case CheckStyle.Check:
                sb.Append(color).Append(" RG ").Append(ElementFace.Number(size * 0.14)).Append(" w 1 J 1 j ")
                  .Append(P(-0.36, 0.02)).Append(" m ").Append(P(-0.12, -0.26)).Append(" l ").Append(P(0.38, 0.32)).Append(" l S\n");
                break;
            case CheckStyle.Cross:
                sb.Append(color).Append(" RG ").Append(ElementFace.Number(size * 0.14)).Append(" w 1 J ")
                  .Append(P(-0.3, -0.3)).Append(" m ").Append(P(0.3, 0.3)).Append(" l ")
                  .Append(P(-0.3, 0.3)).Append(" m ").Append(P(0.3, -0.3)).Append(" l S\n");
                break;
            case CheckStyle.Circle:
                sb.Append(color).Append(" rg ");
                AppendCircle(sb, cx, cy, size * 0.25).Append(" f\n");
                break;
            case CheckStyle.Diamond:
                sb.Append(color).Append(" rg ").Append(P(0, 0.38)).Append(" m ").Append(P(0.38, 0)).Append(" l ")
                  .Append(P(0, -0.38)).Append(" l ").Append(P(-0.38, 0)).Append(" l h f\n");
                break;
            case CheckStyle.Square:
                sb.Append(color).Append(" rg ").Append(P(-0.28, -0.28)).Append(' ').Append(ElementFace.Number(size * 0.56)).Append(' ')
                  .Append(ElementFace.Number(size * 0.56)).Append(" re f\n");
                break;
            case CheckStyle.Star:
                sb.Append(color).Append(" rg ");
                for (var i = 0; i < 10; i++)
                {
                    var angle = Math.PI / 2 + i * Math.PI / 5;
                    var radius = i % 2 == 0 ? 0.42 : 0.17;
                    sb.Append(P(Math.Cos(angle) * radius, Math.Sin(angle) * radius - 0.03)).Append(i == 0 ? " m " : " l ");
                }
                sb.Append("h f\n");
                break;
        }
    }

    /// <summary>Appends a circle made of four Bézier curves (without a fill or stroke operator).</summary>
    private static StringBuilder AppendCircle(StringBuilder sb, double cx, double cy, double r)
    {
        const double k = 0.5523;
        string P(double x, double y) => $"{ElementFace.Number(cx + x * r)} {ElementFace.Number(cy + y * r)}";
        return sb.Append(P(1, 0)).Append(" m ")
            .Append(P(1, k)).Append(' ').Append(P(k, 1)).Append(' ').Append(P(0, 1)).Append(" c ")
            .Append(P(-k, 1)).Append(' ').Append(P(-1, k)).Append(' ').Append(P(-1, 0)).Append(" c ")
            .Append(P(-1, -k)).Append(' ').Append(P(-k, -1)).Append(' ').Append(P(0, -1)).Append(" c ")
            .Append(P(k, -1)).Append(' ').Append(P(1, -k)).Append(' ').Append(P(1, 0)).Append(" c h");
    }

    private static StringBuilder AppendPolygon(StringBuilder sb, (double X, double Y)[] points)
    {
        for (var i = 0; i < points.Length; i++)
            sb.Append(ElementFace.Number(points[i].X)).Append(' ').Append(ElementFace.Number(points[i].Y)).Append(i == 0 ? " m " : " l ");
        return sb.Append('h');
    }
}
