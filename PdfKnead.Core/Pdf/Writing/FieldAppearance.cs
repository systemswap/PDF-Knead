using System.Globalization;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// Appearance for form fields that bring none (or an outdated one); otherwise their value would silently disappear
/// when flattening. Text fields and choice fields with a value are set like a text element (font, size and colour
/// from /DA, alignment from /Q, multiline from /Ff). 12.7.4.3, 12.7.5.
/// </summary>
internal static class FieldAppearance
{
    private static readonly PdfName Parent = PdfName.Get("Parent");
    private static readonly PdfName FT = PdfName.Get("FT");
    private static readonly PdfName V = PdfName.Get("V");
    private static readonly PdfName DA = PdfName.Get("DA");
    private static readonly PdfName Q = PdfName.Get("Q");
    private static readonly PdfName Ff = PdfName.Get("Ff");
    private static readonly PdfName AP = PdfName.Get("AP");
    private static readonly PdfName MK = PdfName.Get("MK");
    private static readonly PdfName R = PdfName.Get("R");

    /// <summary>
    /// Text of a text or choice field with a value, if its appearance does not show it: the appearance is missing, or
    /// (with <paramref name="needAppearances"/>) it is only border and background without text; the text is then drawn
    /// over it (<c>OverAppearance</c>). Otherwise null: the existing appearance applies.
    /// </summary>
    public static (TextElement Text, bool OverAppearance)? Replacement(PdfDocument doc, PdfDictionary widget, PdfDictionary? acroForm, bool needAppearances)
    {
        var normal = widget.GetDictionary(AP)?.Get(PdfName.N);
        var hasAppearance = HasNormal(widget);
        if (hasAppearance && !(needAppearances && normal is PdfStream stream && !ShowsText(doc, stream))) return null;
        // a code field draws its value as the code (without an appearance from the value as well), never as text over it
        if (hasAppearance && FormField.Of(widget) is { } field && FieldSpec.IsCodeField(field)) return null;
        return Text(widget, acroForm) is { } text ? (text, hasAppearance) : null;
    }

    /// <summary>
    /// Typeset text of an annotation whose appearance does not show it, the same for display and flattening: field values
    /// without /AP or with NeedAppearances as when filling in (border from /MK, comb fields one character per cell), free text
    /// without /AP from /Contents. <c>OverAppearance</c>: drawn over the existing appearance. Null if there is nothing to set
    /// or the font lacks characters (better nothing than boxes).
    /// </summary>
    public static (ElementAppearance Appearance, bool OverAppearance)? TextAppearance(PdfDocument doc, PdfDictionary annot)
    {
        var subtype = annot.GetName(PdfName.Subtype)?.Value;
        TextElement? text;
        var over = false;
        if (subtype == "FreeText") text = annot.GetDictionary(AP)?.Get(PdfName.N) is PdfStream or PdfDictionary ? null : FreeText(annot);
        else if (subtype == "Widget")
        {
            var acroForm = doc.Catalog?.GetDictionary(PdfName.Get("AcroForm"));
            // the form asks for every field's appearance to be constructed: drawn instead of the stored one (No. 890)
            if (ConstructedValue(annot, acroForm) is { } constructed && PdfRectangle.From(annot.Get(PdfName.Get("Rect"))) is { Width: > 0, Height: > 0 } box)
                return (ElementAppearance.For(new ForeignAnnotation { Rect = box, Document = doc, Original = annot, OriginalRect = box, Kind = "Widget", Value = constructed }), false);
            var replacement = Replacement(doc, annot, acroForm, acroForm?.GetBoolean(PdfName.Get("NeedAppearances")) == true);
            if (replacement is null) return MissingFieldLook(doc, annot) is { } missing ? (missing, false) : null;
            (text, over) = replacement is { } r ? (r.Text, r.OverAppearance) : (null, false);
        }
        else return null;
        if (text is null) return null;
        ElementAppearance appearance;
        if (!ElementFace.For(text.Font).Covers(text.Text))
        {
            // free text the font of /DA lacks characters of (Arabic): set with a font per character as formatted text (No. 910); still
            // nothing where no font has them (better nothing than boxes)
            if (subtype != "FreeText" || WithFallbackFonts(text) is not { } rich) return null;
            appearance = ElementAppearance.For(rich);
        }
        else
            appearance = subtype == "Widget" && FormField.Of(annot) is { } field
                ? ElementAppearance.For(new ForeignAnnotation { Rect = text.Rect, Document = doc, Original = annot, OriginalRect = text.Rect, Kind = "Widget", Value = field.StoredValue })
                : ElementAppearance.For(text);
        if (subtype == "FreeText") appearance = FreeTextBox(annot, appearance, text.Color);
        // an updated appearance holds what stays of the old one: drawn instead of it (12.7.4.3, sentences 46–47; docs/FEHLERGEDAECHTNIS.md, No. 815)
        return (appearance, over && !appearance.UpdatesAppearance);
    }

    /// <summary>
    /// The appearance of a field without one that the display and a PDF 2.0 or PDF/A copy draw as readers do (12.5.5, 12.5.6.19 Table 192,
    /// 12.7.5.2): a text or choice field without a value whose /MK asks for a border or a background (/BC, /BG) gets that frame (test file
    /// issue12750; docs/FEHLERGEDAECHTNIS.md, No. 888); a check box or radio button its frame and, in its on state, its symbol (/MK /CA, else
    /// the check mark; checkbox_no_appearance); a push button its caption (/MK /CA) and frame (resetform_parent; No. 889). Before, these
    /// copies got an empty appearance and the display drew nothing. Null otherwise.
    /// </summary>
    /// <param name="doc">The document of the field.</param>
    /// <param name="widget">The widget annotation.</param>
    /// <returns>The appearance of the field, or null.</returns>
    public static ElementAppearance? MissingFieldLook(PdfDocument doc, PdfDictionary widget) =>
        MissingFieldValue(widget) is { } value && PdfRectangle.From(widget.Get(PdfName.Get("Rect"))) is { Width: > 0, Height: > 0 } rect
            ? ElementAppearance.For(new ForeignAnnotation { Rect = rect, Document = doc, Original = widget, OriginalRect = rect, Kind = "Widget", Value = value })
            : null;

    /// <summary>
    /// The value a text or choice field is drawn with when its form asks for the appearance of every field to be constructed (/NeedAppearances
    /// true; ISO 32000-2, 12.7.3 Table 224, 12.7.4.3): its stored value, "" without one. The appearance is then the existing one updated — only
    /// its <c>/Tx</c> section gets the value (12.7.4.3, sentences 46–47, <see cref="FieldFrame"/>) — or, without one to update, built from /MK
    /// and /DA, the same in the display, a PDF 2.0 or PDF/A copy (which drop the flag) and when flattening. Before, a field whose appearance
    /// showed text or that had no value kept its stored appearance: a stale text or background stayed where readers show the value (test file
    /// bug1796741, text_field_own_canvas_calc; docs/FEHLERGEDAECHTNIS.md, No. 890). Null: no such form, not a text or choice field, a code
    /// field (drawn as its code), or a value the font of /DA cannot show — then the stored appearance stays (better it than boxes).
    /// </summary>
    /// <param name="widget">The widget annotation.</param>
    /// <param name="acroForm">The interactive form dictionary of its document.</param>
    /// <returns>The value, or null.</returns>
    public static string? ConstructedValue(PdfDictionary widget, PdfDictionary? acroForm)
    {
        if (acroForm?.GetBoolean(PdfName.Get("NeedAppearances")) != true ||
            FormField.Of(widget) is not { Kind: FormFieldKind.Text or FormFieldKind.ComboBox or FormFieldKind.ListBox } field || FieldSpec.IsCodeField(field))
            return null;
        var value = field.StoredValue;
        if (value.Length > 0 && Text(widget, acroForm) is { } text && !ElementFace.For(text.Font).Covers(text.Text)) return null;
        return value;
    }

    /// <summary>
    /// Has the widget a normal appearance that can be drawn: a form XObject with its bounding box (ISO 32000-2, 8.10.2 Table 93: /BBox
    /// required), or for appearance states a dictionary whose every state is one (12.5.5 Table 170). A field without one is drawn from its
    /// properties like one without any (<see cref="MissingFieldValue"/>, <see cref="Replacement"/>): a state that is a name instead of a stream,
    /// or a stream without /BBox, drew nothing in the display and stayed so in a PDF 2.0 copy, while readers that construct the field drew it
    /// (test file checkbox-bad-appearance; docs/FEHLERGEDAECHTNIS.md, No. 891).
    /// </summary>
    /// <param name="widget">The widget annotation.</param>
    /// <returns>True if its normal appearance can be drawn.</returns>
    public static bool HasNormal(PdfDictionary widget)
    {
        return widget.GetDictionary(AP)?.Get(PdfName.N) switch
        {
            PdfStream form => Drawable(form),
            PdfDictionary { Count: > 0 } states => states.Entries.All(e => states.Get(e.Key) is PdfStream state && Drawable(state)),
            _ => false,
        };

        static bool Drawable(PdfStream form) =>
            form.Dictionary.Get(PdfName.Get("BBox")) is PdfArray { Count: 4 } box && Enumerable.Range(0, 4).All(i => box.GetNumber(i) is { } v && double.IsFinite(v));
    }

    /// <summary>The value the field is drawn with by <see cref="MissingFieldLook"/>, or null if it gets no such appearance.</summary>
    /// <param name="widget">The widget annotation.</param>
    /// <returns>"" for an empty text or choice field and a push button, the state for a check box or radio button; null otherwise.</returns>
    public static string? MissingFieldValue(PdfDictionary widget)
    {
        if (HasNormal(widget) || FormField.Of(widget) is not { } field || FieldSpec.IsCodeField(field))
            return null;
        var mk = widget.GetDictionary(MK);
        var frame = mk is not null && (HasColour(mk.Get(PdfName.Get("BC"))) || HasColour(mk.Get(PdfName.Get("BG"))));
        return field.Kind switch
        {
            FormFieldKind.Text or FormFieldKind.ComboBox or FormFieldKind.ListBox => string.IsNullOrEmpty(field.StoredValue) && frame ? "" : null,
            FormFieldKind.CheckBox or FormFieldKind.Radio => field.State,
            FormFieldKind.PushButton => frame || mk?.Get(PdfName.Get("CA")) is PdfString { Bytes.Length: > 0 } ? "" : null,
            _ => null,
        };

        static bool HasColour(PdfObject? value) => value is PdfArray { Count: 1 or 3 or 4 };
    }

    /// <summary>
    /// A text as formatted text with a font per character (<see cref="RichLayout"/>: Liberation of the font of /DA, else Noto Arabic or Noto
    /// CJK), right-to-left runs ordered by the Unicode Bidirectional Algorithm; null if a character is in none of these fonts. For free text on
    /// Arabic without an appearance of its own — before, nothing was drawn (test file freetext_no_appearance; docs/FEHLERGEDAECHTNIS.md, No. 910).
    /// </summary>
    private static RichTextElement? WithFallbackFonts(TextElement text)
    {
        var style = new RichStyle
        {
            Family = text.Font switch { ElementFont.Serif => "Liberation Serif", ElementFont.Mono => "Liberation Mono", _ => "Liberation Sans" },
            Size = text.FontSize,
            Color = text.Color,
        };
        var liberation = ElementFace.For(text.Font);
        foreach (var rune in text.Text.EnumerateRunes())
            if (!System.Text.Rune.IsWhiteSpace(rune) && !System.Text.Rune.IsControl(rune) && liberation.Glyph(rune.Value) == 0 &&
                RichFonts.Arabic(rune.Value, style) is null && RichFonts.Cjk(rune.Value, style) is null)
                return null;
        return new RichTextElement
        {
            Rect = text.Rect,
            Content = new RichText(text.Text, style),
            Alignment = text.Alignment switch { ElementAlignment.Center => Pdf.Text.BlockAlignment.Center, ElementAlignment.Right => Pdf.Text.BlockAlignment.Right, _ => Pdf.Text.BlockAlignment.Left },
        };
    }

    /// <summary>
    /// The box readers draw around free text without an appearance of its own: the background from /C (as the three reference readers and
    /// common editors use it for free text), the border from /BS /W, else /Border (12.5.4, Table 166: default width 1; 0: none), dashed with
    /// /BS /S /D or the dash array of /Border, in the colour of the text, around the inner rectangle /RD (Table 177). Before, only the text was
    /// drawn, in the display and in a PDF 2.0 copy (test file annotation_freetext; docs/FEHLERGEDAECHTNIS.md, No. 892).
    /// </summary>
    private static ElementAppearance FreeTextBox(PdfDictionary annot, ElementAppearance text, int textColor)
    {
        var fill = Numbers(annot.Get(PdfName.Get("C"))) is { Length: 1 or 3 or 4 } c && c.All(v => v is >= 0 and <= 1) ? c : null;
        var bs = annot.GetDictionary(PdfName.Get("BS"));
        var border = annot.Get(PdfName.Get("Border")) as PdfArray;
        var width = bs?.GetNumber(PdfName.Get("W")) ?? border?.GetNumber(2) ?? 1;
        if (!double.IsFinite(width) || width < 0) width = 0;
        var dash = bs is not null
            ? bs.GetName(PdfName.Get("S"))?.Value == "D" ? Numbers(bs.Get(PdfName.Get("D"))) is { Length: > 0 } d ? d : [3] : null
            : Numbers(border?.Get(3)) is { Length: > 0 } borderDash ? borderDash : null;
        if (dash is not null && !dash.All(v => v >= 0 && double.IsFinite(v)) || dash?.All(v => v == 0) == true) dash = null;
        var inset = Numbers(annot.Get(PdfName.Get("RD"))) is { Length: 4 } rd && rd.All(v => v >= 0 && double.IsFinite(v)) ? rd : [0, 0, 0, 0];
        double[] stroke = [((textColor >> 16) & 0xFF) / 255.0, ((textColor >> 8) & 0xFF) / 255.0, (textColor & 0xFF) / 255.0];
        return ElementAppearance.Boxed(text, fill, stroke, Math.Min(width, 12), dash, inset);

        static double[]? Numbers(PdfObject? value) =>
            value is PdfArray array && Enumerable.Range(0, array.Count).Select(array.GetNumber).ToArray() is var all && all.All(v => v is not null)
                ? [.. all.Select(v => v!.Value)]
                : null;
    }

    /// <summary>
    /// Free text annotation without an appearance (test file bug1865341): /Contents with font, size and colour from /DA,
    /// alignment from /Q, multiline, as common PDF editors set it when opening. Null without text.
    /// </summary>
    public static TextElement? FreeText(PdfDictionary annot)
    {
        var text = (annot.Get(PdfName.Contents) as PdfString)?.ToText();
        if (string.IsNullOrWhiteSpace(text) || PdfRectangle.From(annot.Get(PdfName.Get("Rect"))) is not { } rect || rect.Width <= 0 || rect.Height <= 0)
            return null;
        var (font, size, color) = ParseDefaultAppearance((annot.Get(DA) as PdfString)?.ToText() ?? "");
        return new TextElement
        {
            Rect = rect,
            Text = text.Replace("\r\n", "\n").Replace('\r', '\n'),
            Font = font,
            FontSize = size > 0 ? size : 12,
            Color = color,
            Alignment = (annot.GetNumber(Q) ?? 0) switch { 1 => ElementAlignment.Center, 2 => ElementAlignment.Right, _ => ElementAlignment.Left },
            Multiline = true,
        };
    }

    /// <summary>Does the content show text? If it calls another XObject, yes, as a precaution.</summary>
    private static bool ShowsText(PdfDocument doc, PdfStream stream)
    {
        try
        {
            var parser = new Content.ContentParser(doc.GetStreamData(stream));
            for (var n = 0; n < 100_000 && parser.Next() is { } op; n++)
                if (op is "Tj" or "TJ" or "'" or "\"" or "Do") return true;
            return false;
        }
        catch (Exception e) when (e is PdfException or IOException or InvalidDataException)
        {
            return true;
        }
    }

    private static TextElement? Text(PdfDictionary widget, PdfDictionary? acroForm)
    {
        var type = Inherited(widget, FT)?.AsName();
        if (type is not ("Tx" or "Ch")) return null;
        var flags = Inherited(widget, Ff)?.AsNumber() is { } f ? (long)f : 0;
        if (type == "Tx" && (flags & (1 << 13)) != 0) return null;   // password field: never show the value
        var text = Inherited(widget, V) switch
        {
            PdfString s => s.ToText(),
            PdfArray { Count: > 0 } list when list.Get(0) is PdfString first => first.ToText(),
            _ => null,
        };
        if (string.IsNullOrEmpty(text) || PdfRectangle.From(widget.Get(PdfName.Get("Rect"))) is not { } rect ||
            rect.Width <= 0 || rect.Height <= 0) return null;

        var da = (Inherited(widget, DA) as PdfString ?? acroForm?.GetString(DA))?.ToText() ?? "";
        var (font, size, color) = ParseDefaultAppearance(da);
        var multiline = type == "Tx" && (flags & (1 << 12)) != 0;
        if (size <= 0) size = multiline ? 12 : Math.Clamp((rect.Height - 2 * ElementAppearance.Padding) * 0.7, 4, 12);   // "0 Tf": automatic size
        var rotation = (int)(widget.GetDictionary(MK)?.GetNumber(R) ?? 0);
        return new TextElement
        {
            Rect = rect,
            Text = multiline ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text.ReplaceLineEndings(" "),
            Font = font,
            FontSize = size,
            Color = color,
            Alignment = (Inherited(widget, Q)?.AsNumber() ?? acroForm?.GetNumber(Q) ?? 0) switch
            {
                1 => ElementAlignment.Center,
                2 => ElementAlignment.Right,
                _ => ElementAlignment.Left,
            },
            Multiline = multiline,
            Rotation = ((rotation % 360) + 360) % 360 is var r && r % 90 == 0 ? r : 0,
        };
    }

    /// <summary>Field attribute, also inherited from the parent field (12.7.4.1); depth limited against cycles.</summary>
    private static PdfObject? Inherited(PdfDictionary field, PdfName key)
    {
        var current = field;
        for (var depth = 0; depth < 32 && current is not null; depth++)
        {
            if (current.Get(key) is not PdfNull and var value) return value;
            current = current.GetDictionary(Parent);
        }
        return null;
    }

    private static string? AsName(this PdfObject obj) => (obj as PdfName)?.Value;

    /// <summary>"/Helv 10 Tf 0 0 1 rg": font family (roughly by name), size, colour as 0xRRGGBB.</summary>
    internal static (ElementFont Font, double Size, int Color) ParseDefaultAppearance(string da)
    {
        var tokens = da.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var font = ElementFont.Sans;
        double size = 0;
        var color = 0;
        double Num(int i) => i >= 0 && double.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : 0;
        static int Byte(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
        for (var i = 0; i < tokens.Length; i++)
            switch (tokens[i])
            {
                case "Tf" when i >= 2:
                    size = Math.Clamp(Num(i - 1), 0, 200);
                    var name = tokens[i - 2].TrimStart('/');
                    font = name.Contains("Cour", StringComparison.OrdinalIgnoreCase) || name.Contains("Mono", StringComparison.OrdinalIgnoreCase)
                        ? ElementFont.Mono
                        : name.StartsWith("Ti", StringComparison.OrdinalIgnoreCase) || name.Contains("Times", StringComparison.OrdinalIgnoreCase) ||
                          name.Contains("Serif", StringComparison.OrdinalIgnoreCase) && !name.Contains("Sans", StringComparison.OrdinalIgnoreCase)
                            ? ElementFont.Serif
                            : ElementFont.Sans;
                    break;
                case "g" when i >= 1:
                    color = Byte(Num(i - 1)) * 0x010101;
                    break;
                case "rg" when i >= 3:
                    color = (Byte(Num(i - 3)) << 16) | (Byte(Num(i - 2)) << 8) | Byte(Num(i - 1));
                    break;
                case "k" when i >= 4:
                    var black = 1 - Num(i - 1);
                    color = (Byte((1 - Num(i - 4)) * black) << 16) | (Byte((1 - Num(i - 3)) * black) << 8) | Byte((1 - Num(i - 2)) * black);
                    break;
            }
        return (font, size, color);
    }
}
