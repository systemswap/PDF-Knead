using System.Runtime.CompilerServices;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Appearance of a page element as in-memory PDF objects: a form XObject (/BBox in reading orientation, rotated by
/// /Matrix) and an annotation with /Rect and /AP. Display (through the page interpreter, like any annotation) and saving
/// (as a field or fixed in the page content) use the same objects, so that the saved result looks as displayed.
/// </summary>
public sealed partial class ElementAppearance
{
    /// <summary>Padding between text and border in points.</summary>
    public const double Padding = 2;

    /// <summary>Line spacing as a multiple of the font size.</summary>
    public const double LineSpacing = 1.15;

    private static readonly ConditionalWeakTable<PageElement, ElementAppearance> Cache = new();
    private static readonly IReadOnlyDictionary<int, string> NoGlyphs = new Dictionary<int, string>();
    private static PdfDocument? _document;

    private ElementAppearance(PageElement element, PdfStream form, ElementFace? face, IReadOnlyDictionary<int, string> glyphs, ElementAppearance? shape = null)
    {
        Element = element;
        Form = form;
        Face = face;
        Glyphs = glyphs;
        FaceUses = face is null ? [] : [(face, glyphs)];
        Shape = shape ?? this;
        var annotation = new PdfDictionary();
        annotation.Set(PdfName.Type, PdfName.Get("Annot"));
        annotation.Set(PdfName.Subtype, PdfName.Get("Widget"));
        annotation.Set(PdfName.Get("Rect"), Rectangle(element.Rect));
        annotation.Set(PdfName.F, PdfInteger.Of(4));   // Print
        var ap = new PdfDictionary();
        ap.Set(PdfName.N, form);
        annotation.Set(PdfName.Get("AP"), ap);
        Annotation = annotation;
    }

    // Annotation of another program: the annotation itself (for filled-in check boxes and radio buttons a copy with the new
    // state), read in the context of its document
    private ElementAppearance(ForeignAnnotation element, PdfStream form, ElementAppearance? shape, PdfDictionary? annotation = null)
    {
        Element = element;
        Form = form;
        Glyphs = NoGlyphs;
        Shape = shape ?? this;
        Annotation = annotation ?? element.Original;
        Source = element.Document;
    }

    /// <summary>
    /// Document in whose context the annotation is read: <see cref="Document"/>, or the annotation's own file for annotations of
    /// other programs and for text that uses fonts from the file.
    /// </summary>
    public PdfDocument Source { get; private init; } = Document;

    /// <summary>
    /// Document in whose context the objects (fonts, images) are read: empty and unencrypted, because the elements belong to no
    /// file.
    /// </summary>
    public static PdfDocument Document => _document ??= BlankPage.Create(100, 100).Source;

    /// <summary>The element this appearance belongs to.</summary>
    public PageElement Element { get; }

    /// <summary>Annotation with /Rect, /F and /AP (without /P, /T …, which the writer sets).</summary>
    public PdfDictionary Annotation { get; }

    /// <summary>Form XObject: /BBox [0 0 width height] in reading orientation, /Matrix with the rotation.</summary>
    public PdfStream Form { get; }

    /// <summary>
    /// For a filled-in field of another program whose appearance was updated (<see cref="FieldFrame"/>, ISO 32000-2, 12.7.4.3, sentences 46–47):
    /// its old appearance without the <c>/Tx</c> section, read in <see cref="Source"/> — <see cref="Form"/> draws it as the XObject
    /// <see cref="FrameName"/> under the new text. The writer maps it to the copy as an object of the file, the rest as built here.
    /// </summary>
    public PdfStream? Frame { get; private init; }

    /// <summary>Name of <see cref="Frame"/> in the XObject resources of <see cref="Form"/>.</summary>
    public static readonly PdfName FrameName = PdfName.Get("Frame");

    /// <summary>
    /// The appearance of a filled-in field of another program as its old one updated (<see cref="FieldFrame"/>): it stands for the old one
    /// entirely — drawn instead of it, never over it (with or without <see cref="Frame"/>).
    /// </summary>
    public bool UpdatesAppearance { get; private init; }

    /// <summary>Font used (<see langword="null"/> without text).</summary>
    public ElementFace? Face { get; }

    /// <summary>Used glyphs mapped to their text (for the subset on saving).</summary>
    public IReadOnlyDictionary<int, string> Glyphs { get; }

    /// <summary>
    /// All fonts used, with their glyphs (formatted text can have several, see KONZEPT.md, section 5.14; otherwise at most
    /// <see cref="Face"/>); the writer embeds one subset per font.
    /// </summary>
    public IReadOnlyList<(ElementFace Face, IReadOnlyDictionary<int, string> Glyphs)> FaceUses { get; private init; } = [];

    /// <summary>
    /// The appearance first built with the same form XObject (for moved elements that of the original, otherwise this one): the
    /// display attaches its display list to it and draws it translated.
    /// </summary>
    public ElementAppearance Shape { get; }

    /// <summary>Ballpoint texture in the appearance: its source, its resolution (pixels per unit) and the resolution this size would need.</summary>
    internal sealed record TextureUse(InkTextures.Source Source, double Scale, double Needed)
    {
        public bool Coarse => Scale < Needed * 0.999;
    }

    internal TextureUse? Texture { get; private set; }

    /// <summary>
    /// Gets the appearance of an element, built once because elements are immutable. With a ballpoint texture it is coarse at
    /// first; once the sharper texture is ready (<see cref="InkTextures.Improved"/>), it is rebuilt.
    /// </summary>
    /// <param name="element">The page element.</param>
    /// <returns>The cached or newly built appearance.</returns>
    /// <exception cref="NotSupportedException">The element type has no appearance.</exception>
    public static ElementAppearance For(PageElement element)
    {
        var appearance = Cache.GetValue(element, Build);
        // Still coarse: is the sharper texture ready? (Get requests it again in case the request was discarded)
        if (appearance.Texture is { Coarse: true } use && InkTextures.Get(use.Source, use.Needed) is { } best && best.Scale > use.Scale * 1.001)
        {
            appearance = Build(element);
            Cache.AddOrUpdate(element, appearance);
        }
        return appearance;
    }

    /// <summary>
    /// Gets the appearance for saving and exporting: the ballpoint texture at full resolution (computed now if it is missing).
    /// </summary>
    /// <param name="element">The page element.</param>
    /// <returns>The final appearance; for inserted images the downsampled and sharpened version.</returns>
    public static ElementAppearance Final(PageElement element)
    {
        // Inserted images: downsampled to the saved resolution and sharpened (see KONZEPT.md, section 5.13)
        if (element is ImageElement { IsSignature: false } picture) return SavedPicture(picture);
        var appearance = For(element);
        if (appearance.Texture is not { Coarse: true } use) return appearance;
        InkTextures.Final(use.Source, use.Needed);
        appearance = Build(element);
        Cache.AddOrUpdate(element, appearance);
        return appearance;
    }

    /// <summary>
    /// Moves an element to another place. With the same size the form XObject, which depends only on content and size, is
    /// reused, so that dragging and arrow keys lay out nothing again and images stay in the cache. If the size changes, a new
    /// element is created as with <c>with</c>.
    /// </summary>
    /// <param name="element">The element to move.</param>
    /// <param name="rect">The new rectangle in page space.</param>
    /// <returns>The moved element.</returns>
    public static PageElement Moved(PageElement element, PdfRectangle rect)
    {
        var old = element.Rect;
        static bool Same(double a, double b) => Math.Abs(a - b) <= 1e-6 * Math.Max(1, Math.Abs(b));
        if (!Same(rect.Width, old.Width) || !Same(rect.Height, old.Height)) return element with { Rect = rect };
        // Keep the size exactly (the conversion through the view rounds); otherwise it would grow by fractions with every step
        var moved = element with { Rect = new PdfRectangle(rect.Left, rect.Bottom, rect.Left + old.Width, rect.Bottom + old.Height) };
        var original = For(element);
        Cache.AddOrUpdate(moved, new ElementAppearance(moved, original.Form, original.Face, original.Glyphs, original.Shape)
            { Texture = original.Texture, Source = original.Source, States = original.States, FaceUses = original.FaceUses, OriginalFonts = original.OriginalFonts,
              Frame = original.Frame, UpdatesAppearance = original.UpdatesAppearance });
        return moved;
    }

    /// <summary>Gets the mapping from form to page (as when drawing an annotation, 12.5.5), for "cm … Do" when flattening.</summary>
    /// <returns>The translation that puts the transformed /BBox at the element's rectangle.</returns>
    public Matrix FormToPage()
    {
        // Based on the form's own /BBox and /Matrix: for fields with /MK /R the form is rotated, but the element (of another program) is not
        var box = PdfRectangle.From(Form.Dictionary.Get(PdfName.Get("BBox"))) ?? new PdfRectangle(0, 0, Element.UprightSize.Width, Element.UprightSize.Height);
        var matrix = Matrix.FromArray(Form.Dictionary.GetArray(PdfName.Get("Matrix"))) ?? Matrix.Identity;
        var bounds = matrix.TransformBounds(RectD.FromLtrb(box.Left, box.Bottom, box.Right, box.Top));
        var rect = Element.Rect;
        return Matrix.Translate(rect.Left - bounds.Left, rect.Bottom - bounds.Top);
    }

    /// <summary>
    /// Gets the counter-clockwise rotation of the content in page space; the page itself is displayed rotated clockwise.
    /// </summary>
    /// <param name="degrees">0, 90, 180 or 270; any other value gives the identity.</param>
    /// <returns>The rotation matrix.</returns>
    public static Matrix Rotation(int degrees) => degrees switch
    {
        90 => new Matrix(0, 1, -1, 0, 0, 0),
        180 => new Matrix(-1, 0, 0, -1, 0, 0),
        270 => new Matrix(0, -1, 1, 0, 0, 0),
        _ => Matrix.Identity,
    };

    private static ElementAppearance Build(PageElement element) => element switch
    {
        TextElement text => BuildText(text),
        RichTextElement rich => BuildRich(rich),
        SignatureFieldElement field => BuildSignatureField(field),
        RedactionElement redaction => BuildRedaction(redaction),
        SignatureElement signature => BuildSignature(signature),
        InkElement ink => BuildInk(ink),
        ImageElement image => BuildImage(image),
        FieldElement field => BuildField(field, field.Spec, field.Value, field.Rotation),
        ShapeElement shape => BuildShape(shape),
        CodeElement code => BuildCode(code),
        NoteElement note => BuildNote(note),
        DimensionElement dimension => BuildDimension(dimension),
        ForeignAnnotation foreign => BuildForeign(foreign),
        _ => throw new NotSupportedException(element.GetType().Name),
    };

    // Annotations of other programs: one form per appearance stream and size, at the original place of the first annotation; the
    // display draws the others (and moved ones) translated (Shape). The appearance depends only on the stream and the size of the
    // rectangle, so shared appearances (check boxes, stamps) are read once, not per annotation (otherwise a crafted file with one
    // expensive appearance for 1 000 annotations stalled the display)
    private static readonly ConditionalWeakTable<PdfStream, System.Collections.Concurrent.ConcurrentDictionary<(double, double), ElementAppearance>> SharedForeignShapes = new();
    private static readonly ConditionalWeakTable<PdfDictionary, ElementAppearance> ForeignShapes = new();

    private static ElementAppearance BuildForeign(ForeignAnnotation foreign)
    {
        try
        {
            return BuildForeignCore(foreign);
        }
        catch (PdfException)
        {
            // A damaged object behind the annotation (nested too deeply, unreadable; found by fuzzing while moving a field): it draws
            // nothing, as one without an appearance, instead of breaking off whatever asked for its look
            var at = foreign.IsMoved ? foreign with { Rect = foreign.OriginalRect } : foreign;
            var empty = ForeignShapes.GetValue(foreign.Original, _ => new ElementAppearance(at, new PdfStream(new PdfDictionary(), []), null));
            return ReferenceEquals(empty.Element, foreign) ? empty : new ElementAppearance(foreign, empty.Form, empty);
        }
    }

    private static ElementAppearance BuildForeignCore(ForeignAnnotation foreign)
    {
        // Properties changed: new appearance from the properties, with the current value
        if (foreign.Spec is { } spec && NeedsNewLook(foreign) && FormField.Of(foreign) is { } edited)
        {
            var current = ValueOf(foreign, edited);
            if (spec.Kind is FormFieldKind.CheckBox or FormFieldKind.Radio) current = current == "Off" ? "Off" : OnStateOf(spec);
            return BuildField(foreign, spec, current, WidgetRotation(foreign.Original));
        }

        // a check box, radio button or push button without an appearance of its own: drawn from its properties (frame, symbol, caption) as
        // readers draw it — before, an empty appearance (docs/FEHLERGEDAECHTNIS.md, No. 889)
        if (foreign.Value is { } bareValue && FormField.Of(foreign) is { Kind: FormFieldKind.CheckBox or FormFieldKind.Radio or FormFieldKind.PushButton } &&
            Writing.FieldAppearance.MissingFieldValue(foreign.Original) is not null && FieldSpec.Of(foreign) is { } bareSpec)
            return BuildField(foreign, bareSpec, bareValue, WidgetRotation(foreign.Original));

        // Filled in: text and choice fields with a new appearance, check boxes and radio buttons in the chosen state
        if (foreign.Value is { } value && FormField.Of(foreign) is { } field)
            switch (field.Kind)
            {
                case FormFieldKind.Text when FieldSpec.Of(foreign) is { Code: not null } codeSpec:
                    return BuildField(foreign, codeSpec, value, WidgetRotation(foreign.Original));   // code field: the value as its code
                case FormFieldKind.ListBox when FieldSpec.Of(foreign) is { } listSpec:
                    // list with the selected entries highlighted; an existing appearance keeps what it draws around them (12.7.4.3, sentences 46–47)
                    if (FieldFrame.Of(foreign.Document, foreign.Original) is { } listUpdate)
                    {
                        var listElement = foreign.Rotation == WidgetRotation(foreign.Original) ? (PageElement)foreign : foreign with { Rotation = WidgetRotation(foreign.Original) };
                        var listBorder = listSpec.BorderColor is null ? 0 : Math.Clamp(listSpec.BorderWidth, 0, 12);
                        return WithFrame(BuildList(foreign, listElement, listSpec, value, null, null, listBorder), listUpdate.Frame, foreign);
                    }
                    return BuildField(foreign, listSpec, value, WidgetRotation(foreign.Original));
                case FormFieldKind.Text or FormFieldKind.ComboBox or FormFieldKind.ListBox:
                    // an existing appearance is updated: only its /Tx section gets the new text, background and border stay as they are drawn
                    // there (12.7.4.3, sentences 46–47; docs/FEHLERGEDAECHTNIS.md, No. 815); otherwise built from /MK and /DA
                    return FieldFrame.Of(foreign.Document, foreign.Original) is { } update
                        ? WithFrame(BuildFieldText(foreign, field, value, withFrame: false), update.Frame, foreign)
                        : BuildFieldText(foreign, field, value);
                case FormFieldKind.CheckBox or FormFieldKind.Radio:
                    var switched = new PdfDictionary(foreign.Original.Count) { Resolver = foreign.Original.Resolver };
                    foreach (var (key, _) in foreign.Original.Entries) switched.Set(key, foreign.Original.GetRaw(key)!);
                    switched.Set(PdfName.Get("AS"), PdfName.Get(value));
                    var stateForm = ForeignAnnotations.Appearance(switched);
                    var stateAt = foreign with { Rect = foreign.OriginalRect, Value = null };
                    var stateShape = stateForm is not null
                        ? SharedForeignShapes.GetValue(stateForm, _ => new()).GetOrAdd(
                            (Math.Round(foreign.OriginalRect.Width, 3), Math.Round(foreign.OriginalRect.Height, 3)),
                            _ => new ElementAppearance(stateAt, stateForm, null, switched))
                        : new ElementAppearance(stateAt, new PdfStream(new PdfDictionary(), []), null, switched);   // state without appearance: empty
                    return new ElementAppearance(foreign, stateShape.Form, stateShape);
            }

        var at = foreign.IsMoved ? foreign with { Rect = foreign.OriginalRect } : foreign;
        var shape = ForeignAnnotations.Appearance(foreign.Original) is { } form
            ? SharedForeignShapes.GetValue(form, _ => new()).GetOrAdd(
                (Math.Round(foreign.OriginalRect.Width, 3), Math.Round(foreign.OriginalRect.Height, 3)), _ => new ElementAppearance(at, form, null))
            : ForeignShapes.GetValue(foreign.Original, _ => new ElementAppearance(at, new PdfStream(new PdfDictionary(), []), null));
        return ReferenceEquals(shape.Element, foreign) ? shape : new ElementAppearance(foreign, shape.Form, shape);
    }

    /// <summary>
    /// New appearance of a filled-in text or choice field, as other programs create it (12.7.4.3): background and border from
    /// /MK and /BS, font, size and colour from /DA (size 0: fitted), alignment from /Q; single-line text vertically centred,
    /// multi-line text at the top; comb fields one character per cell; password fields as asterisks.
    /// </summary>
    /// <param name="foreign">The field's widget.</param>
    /// <param name="field">The form field.</param>
    /// <param name="value">The value.</param>
    /// <param name="withFrame">Background and border from /MK; without them for an updated appearance, which keeps its own
    /// (<see cref="WithFrame"/>) — the text stays inside the border width all the same.</param>
    private static ElementAppearance BuildFieldText(ForeignAnnotation foreign, FormField field, string value, bool withFrame = true)
    {
        var shown = field.Password ? new string('*', value.Length) : field.Kind == FormFieldKind.Text ? FieldFormat.Of(field).Display(value) : field.DisplayText(value);
        var (text, background, borderColor, borderWidth) = FieldText(foreign, field, shown);
        return withFrame
            ? DrawFieldText(foreign, text, background, borderColor, borderWidth, field.Comb ? field.MaxLength : 0, FieldBorderStyle.Solid)
            : DrawFieldText(foreign, text, null, null, borderWidth, field.Comb ? field.MaxLength : 0, FieldBorderStyle.Solid);
    }

    /// <summary>
    /// A text appearance with a box under it: the inner rectangle (the form's /BBox less <paramref name="inset"/>: left, top, right, bottom)
    /// filled with <paramref name="fill"/> and its border stroked with <paramref name="stroke"/> at <paramref name="width"/> (inside the
    /// rectangle, dashed with <paramref name="dash"/>). Colours as arrays of 1, 3 or 4 components (gray, RGB, CMYK); null: no fill, no border.
    /// For free text without an appearance of its own (docs/FEHLERGEDAECHTNIS.md, No. 892).
    /// </summary>
    internal static ElementAppearance Boxed(ElementAppearance text, double[]? fill, double[]? stroke, double width, double[]? dash, double[] inset)
    {
        if (fill is null && (stroke is null || width <= 0)) return text;
        if (PdfRectangle.From(text.Form.Dictionary.Get(PdfName.Get("BBox"))) is not { } box) return text;
        double left = box.Left + inset[0], top = box.Top - inset[1], right = box.Right - inset[2], bottom = box.Bottom + inset[3];
        if (right <= left || top <= bottom) (left, top, right, bottom) = (box.Left, box.Top, box.Right, box.Bottom);
        var content = new StringBuilder("q\n");
        if (fill is not null)
            content.Append(Colour(fill, stroking: false)).Append(' ').Append(Rect(left, bottom, right - left, top - bottom)).Append(" re f\n");
        if (stroke is not null && width > 0 && width * 2 < Math.Min(right - left, top - bottom))
        {
            content.Append(Colour(stroke, stroking: true)).Append(' ').Append(ElementFace.Number(width)).Append(" w ");
            if (dash is { Length: > 0 }) content.Append('[').AppendJoin(' ', dash.Select(ElementFace.Number)).Append("] 0 d ");
            var half = width / 2;
            content.Append(Rect(left + half, bottom + half, right - left - width, top - bottom - width)).Append(" re S\n");
        }
        content.Append("Q\n");
        var head = Encoding.ASCII.GetBytes(content.ToString());
        var body = text.Form.RawData ?? [];
        var data = new byte[head.Length + body.Length];
        head.CopyTo(data, 0);
        body.CopyTo(data, head.Length);
        var dict = new PdfDictionary(text.Form.Dictionary.Count) { Resolver = text.Form.Dictionary.Resolver };
        foreach (var (key, value) in text.Form.Dictionary.Entries) dict.Set(key, value);   // built here: its data is not encoded (as in WithFrame)
        return new ElementAppearance(text.Element, new PdfStream(dict, data), text.Face, text.Glyphs) { Source = text.Source, FaceUses = text.FaceUses };

        static string Rect(double x, double y, double w, double h) =>
            $"{ElementFace.Number(x)} {ElementFace.Number(y)} {ElementFace.Number(w)} {ElementFace.Number(h)}";

        static string Colour(double[] c, bool stroking) => string.Join(' ', c.Select(ElementFace.Number)) + ' ' + (c.Length switch
        {
            1 => stroking ? "G" : "g",
            3 => stroking ? "RG" : "rg",
            _ => stroking ? "K" : "k",
        });
    }

    /// <summary>
    /// The new text of a filled-in field over what stays of its old appearance (<see cref="FieldFrame"/>): the old one as the XObject
    /// <see cref="FrameName"/>, placed so that it stands exactly where it stood on its own — both forms are mapped onto the widget's rectangle
    /// (12.5.5, Algorithm 8.1), the text form's own /Tx section after it. Read in the document of the field. Without a frame (the old appearance
    /// was only its section) the text alone.
    /// </summary>
    private static ElementAppearance WithFrame(ElementAppearance text, PdfStream? frame, ForeignAnnotation foreign)
    {
        var alone = new ElementAppearance(text.Element, text.Form, text.Face, text.Glyphs) { FaceUses = text.FaceUses, UpdatesAppearance = true };
        if (frame is null) return alone;
        var rect = foreign.Rect;
        // the frame on its own: its /Matrix, then the mapping A onto the rectangle; inside the text form: its /Matrix, cm, the text form's
        // /Matrix and mapping — so cm = A(frame) × (Matrix(text) × A(text))⁻¹ (a frame that cannot be placed so is left out, as a damaged one)
        if (Mapping(frame, rect) is not { } frameMapping || Mapping(text.Form, rect) is not { } textMapping) return alone;
        var textMatrix = Matrix.FromArray(text.Form.Dictionary.GetArray(PdfName.Get("Matrix"))) ?? Matrix.Identity;
        var outer = textMatrix * textMapping;
        if (!outer.IsInvertible) return alone;
        var cm = frameMapping * outer.Invert();
        if (!cm.IsFinite) return alone;
        var content = new StringBuilder("q ")
            .Append(ElementFace.Number(cm.A)).Append(' ').Append(ElementFace.Number(cm.B)).Append(' ').Append(ElementFace.Number(cm.C)).Append(' ')
            .Append(ElementFace.Number(cm.D)).Append(' ').Append(ElementFace.Number(cm.E)).Append(' ').Append(ElementFace.Number(cm.F))
            .Append(" cm /").Append(FrameName.Value).Append(" Do Q\n");
        var head = Encoding.ASCII.GetBytes(content.ToString());
        var body = text.Form.RawData ?? [];
        var data = new byte[head.Length + body.Length];
        head.CopyTo(data, 0);
        body.CopyTo(data, head.Length);
        var dict = new PdfDictionary(text.Form.Dictionary.Count) { Resolver = foreign.Document.Catalog?.Resolver };
        foreach (var (key, value) in text.Form.Dictionary.Entries) dict.Set(key, value);
        var resources = new PdfDictionary();
        if (text.Form.Dictionary.GetDictionary(PdfName.Resources) is { } own)
            foreach (var (key, value) in own.Entries) resources.Set(key, value);
        var xobjects = new PdfDictionary();
        xobjects.Set(FrameName, frame);
        resources.Set(PdfName.Get("XObject"), xobjects);
        dict.Set(PdfName.Resources, resources);
        return new ElementAppearance(text.Element, new PdfStream(dict, data), text.Face, text.Glyphs)
            { Source = foreign.Document, Frame = frame, FaceUses = text.FaceUses, UpdatesAppearance = true };

        // the mapping A of Algorithm 8.1 (without the form's own /Matrix): its transformed box onto the rectangle
        static Matrix? Mapping(PdfStream form, PdfRectangle rect)
        {
            if (PdfRectangle.From(form.Dictionary.Get(PdfName.Get("BBox"))) is not { } box) return null;
            var matrix = Matrix.FromArray(form.Dictionary.GetArray(PdfName.Get("Matrix"))) ?? Matrix.Identity;
            var bounds = matrix.TransformBounds(RectD.FromLtrb(box.Left, box.Bottom, box.Right, box.Top));
            if (!(bounds.Width > 0) || !(bounds.Height > 0) || !(rect.Width > 0) || !(rect.Height > 0)) return null;
            return Matrix.Translate(-bounds.Left, -bounds.Top) * Matrix.Scale(rect.Width / bounds.Width, rect.Height / bounds.Height)
                   * Matrix.Translate(rect.Left, rect.Bottom);
        }
    }

    /// <summary>
    /// Appearance of a field with text (text field, combo box, push button): background, border in the given style, text as in
    /// <paramref name="text"/> — single-line (or with <paramref name="middle"/>) vertically centred, multi-line at the top, comb
    /// fields one character per cell.
    /// </summary>
    private static ElementAppearance DrawFieldText(PageElement owner, TextElement text, string? background, string? borderColor, double borderWidth,
        int comb, FieldBorderStyle style, bool middle = false, string? extra = null)
    {
        var (w, h) = text.UprightSize;
        var inset = borderWidth + Padding;
        var face = ElementFace.For(text.Font);
        var size = text.FontSize;
        var color = text.Color;

        var lines = Layout(text, face);
        var glyphs = new Dictionary<int, string>();
        var sb = new StringBuilder("/Tx BMC\nq\n");
        AppendFrame(sb, w, h, background, borderColor, borderWidth, style, round: false);
        if (extra is not null) sb.Append(extra);
        sb.Append(ElementFace.Number(borderWidth)).Append(' ').Append(ElementFace.Number(borderWidth)).Append(' ')
          .Append(ElementFace.Number(Math.Max(0, w - 2 * borderWidth))).Append(' ').Append(ElementFace.Number(Math.Max(0, h - 2 * borderWidth))).Append(" re W n\n");
        sb.Append("BT\n/F1 ").Append(ElementFace.Number(size)).Append(" Tf\n");
        sb.Append(ElementFace.Number(((color >> 16) & 0xFF) / 255.0)).Append(' ').Append(ElementFace.Number(((color >> 8) & 0xFF) / 255.0)).Append(' ')
          .Append(ElementFace.Number((color & 0xFF) / 255.0)).Append(" rg\n");
        var lineHeight = (face.Ascent - face.Descent) * size;
        var y = text.Multiline && !middle ? h - inset - face.Ascent * size : (h - lineHeight) / 2 - face.Descent * size;
        foreach (var line in lines)
        {
            if (line.Glyphs.Count > 0)
            {
                if (comb > 0)
                {
                    // One cell of width w / MaxLen per character, the character centred in it
                    var cell = w / comb;
                    for (var i = 0; i < Math.Min(line.Glyphs.Count, comb); i++)
                    {
                        var x = i * cell + (cell - line.Advances[i]) / 2;
                        sb.Append("1 0 0 1 ").Append(ElementFace.Number(x)).Append(' ').Append(ElementFace.Number(y)).Append(" Tm ");
                        AppendShowText(sb, face, new TextLine([line.Glyphs[i]], [line.Text[i]], [line.Advances[i]], line.Advances[i]), size);
                        glyphs.TryAdd(line.Glyphs[i], line.Text[i]);
                    }
                }
                else
                {
                    var x = text.Alignment switch
                    {
                        ElementAlignment.Center => (w - line.Width) / 2,
                        ElementAlignment.Right => w - inset - line.Width,
                        _ => inset,
                    };
                    sb.Append("1 0 0 1 ").Append(ElementFace.Number(x)).Append(' ').Append(ElementFace.Number(y)).Append(" Tm ");
                    AppendShowText(sb, face, line, size);
                    for (var i = 0; i < line.Glyphs.Count; i++) glyphs.TryAdd(line.Glyphs[i], line.Text[i]);
                }
            }
            y -= size * LineSpacing;
        }
        sb.Append("ET\nQ\nEMC\n");

        var fonts = new PdfDictionary();
        fonts.Set(PdfName.Get("F1"), face.DisplayFont);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("Font"), fonts);
        return new ElementAppearance(owner, FormStream(text, resources, Encoding.ASCII.GetBytes(sb.ToString())), face, glyphs);
    }

    /// <summary>
    /// Gets the text of a filled-in field as a text element, as its appearance lays it out (font, size — fitted for "0 Tf" —,
    /// colour, alignment, rotation), together with background and border from /MK. Also used for the input box when filling in.
    /// </summary>
    /// <param name="foreign">The field's widget.</param>
    /// <param name="field">The form field.</param>
    /// <param name="shown">The text to show.</param>
    /// <returns>
    /// The text element, background and border colour as "r g b" operands (<see langword="null"/> if absent), and the border
    /// width in points (0 without a border colour).
    /// </returns>
    public static (TextElement Text, string? Background, string? Border, double BorderWidth) FieldText(ForeignAnnotation foreign, FormField field, string shown)
    {
        var widget = foreign.Original;
        var acroForm = foreign.Document.Catalog?.GetDictionary(PdfName.Get("AcroForm"));
        var (font, size, color) = Writing.FieldAppearance.ParseDefaultAppearance(field.DefaultAppearance(acroForm));
        var mk = widget.GetDictionary(PdfName.Get("MK"));
        var rotation = (int)(mk?.GetNumber(PdfName.Get("R")) ?? 0);
        rotation = ((rotation % 360) + 360) % 360 is var r && r % 90 == 0 ? r : 0;
        var background = FieldColor(mk?.Get(PdfName.Get("BG")));
        var borderColor = FieldColor(mk?.Get(PdfName.Get("BC")));
        var borderWidth = borderColor is null ? 0 : Math.Clamp(widget.GetDictionary(PdfName.Get("BS"))?.GetNumber(PdfName.Get("W")) ?? 1, 0, 20);
        if (!field.Multiline) shown = shown.ReplaceLineEndings(" ");
        var text = new TextElement
        {
            Rect = foreign.Rect,
            Text = shown,
            Font = font,
            FontSize = size > 0 ? size : 12,
            Color = color,
            Alignment = field.Quadding(acroForm) switch { 1 => ElementAlignment.Center, 2 => ElementAlignment.Right, _ => ElementAlignment.Left },
            Multiline = field.Multiline,
            Rotation = rotation,
        };
        // Saved by PDF Knead with "shrink font automatically": start from the starting size; otherwise as common PDF editors do (4 to 12 points)
        if (size <= 0 && FieldSpec.StartSize(widget) is { } start && double.IsFinite(start) && start >= 1)
            text = Shrink(text with { FontSize = Math.Min(start, 500) }, borderWidth, field.Comb);
        else if (size <= 0) text = AutoSize(text, borderWidth, field.Comb);
        return (text, background, borderColor, borderWidth);
    }

    /// <summary>
    /// Shrinks the font automatically: starting from <see cref="TextElement.FontSize"/>, the size only becomes smaller if the
    /// text does not fit — single-line by height and width, multi-line until all lines fit; never below 4 points. Comb fields
    /// stay unchanged.
    /// </summary>
    /// <param name="text">The text with its starting size.</param>
    /// <param name="borderWidth">Border width in points.</param>
    /// <param name="comb">Whether the field is a comb field.</param>
    /// <returns>The text with the fitted font size.</returns>
    public static TextElement Shrink(TextElement text, double borderWidth, bool comb)
    {
        if (comb) return text;
        var face = ElementFace.For(text.Font);
        var (w, h) = text.UprightSize;
        var inset = borderWidth + Padding;
        if (!text.Multiline)
        {
            var byHeight = (h - 2 * inset) / (face.Ascent - face.Descent);
            text = text with { FontSize = Math.Max(4, Math.Min(text.FontSize, byHeight)) };
            var width = Layout(text, face).Sum(l => l.Width);
            if (width > w - 2 * inset && width > 0) text = text with { FontSize = Math.Max(4, text.FontSize * (w - 2 * inset) / width) };
            return text;
        }
        while (text.FontSize > 4 && NeededHeight(text) > h) text = text with { FontSize = Math.Max(4, text.FontSize - 0.5) };
        return text;
    }

    /// <summary>"0 Tf": as large as fits — single-line by height and width, multi-line until all lines fit (4 to 12 points).</summary>
    private static TextElement AutoSize(TextElement text, double borderWidth, bool comb)
    {
        var face = ElementFace.For(text.Font);
        var (w, h) = text.UprightSize;
        var inset = borderWidth + Padding;
        var fit = Math.Clamp((h - 2 * inset) / (face.Ascent - face.Descent), 4, 12);
        text = text with { FontSize = fit };
        if (!text.Multiline && !comb)
        {
            var width = Layout(text, face).Sum(l => l.Width);
            if (width > w - 2 * inset && width > 0) text = text with { FontSize = Math.Max(4, fit * (w - 2 * inset) / width) };
        }
        else if (text.Multiline)
            while (text.FontSize > 4 && NeededHeight(text) > h) text = text with { FontSize = Math.Max(4, text.FontSize - 0.5) };
        return text;
    }

    /// <summary>Colour from /MK (/BG, /BC): grey, RGB or CMYK as operands for "rg"/"RG"; null means none (empty or missing).</summary>
    private static string? FieldColor(PdfObject? obj)
    {
        if (obj is not PdfArray { Count: 1 or 3 or 4 } a) return null;
        var v = new double[a.Count];
        for (var i = 0; i < a.Count; i++) v[i] = Math.Clamp(a.Get(i)?.AsNumber() ?? 0, 0, 1);
        var (r, g, b) = a.Count switch
        {
            1 => (v[0], v[0], v[0]),
            3 => (v[0], v[1], v[2]),
            _ => ((1 - v[0]) * (1 - v[3]), (1 - v[1]) * (1 - v[3]), (1 - v[2]) * (1 - v[3])),
        };
        return $"{ElementFace.Number(r)} {ElementFace.Number(g)} {ElementFace.Number(b)}";
    }

    /// <summary>Gets the aspect ratio of the content, used for the size when an element is placed with a single click.</summary>
    /// <param name="element">The page element.</param>
    /// <returns>Width divided by height; 3 for elements without a natural aspect ratio.</returns>
    public static double Aspect(PageElement element) => element switch
    {
        SignatureElement s => SignatureAspect(s),
        ImageElement i => (double)i.Image.Width / Math.Max(1, i.Image.Height),
        InkElement ink when InkBounds(ink) is { Width: > 0, Height: > 0 } b => b.Width / b.Height,
        CodeElement code when DefaultCodeSize(code) is var (w, h) => w / h,
        _ => 3,
    };

    /// <summary>Image fitted into the rectangle (aspect ratio kept): centred, or left-aligned like a signature.</summary>
    /// <param name="stretch">The image fills the whole rectangle (aspect lock open, see KONZEPT.md, section 5.13) instead of being fitted.</param>
    /// <param name="opacity">Opacity of the image (inserted images).</param>
    private static ElementAppearance BuildPicture(PageElement element, PdfStream image, int width, int height, bool centered, bool ink = false,
        bool stretch = false, double opacity = 1)
    {
        var (w, h) = element.UprightSize;
        var (y0, ch) = ContentArea(element, h);
        var pad = centered ? 0 : Padding;
        var s = Math.Min(Math.Max(1, w - 2 * pad) / width, Math.Max(1, ch - 2 * pad) / height);
        var (iw, ih) = stretch ? (w, ch) : (width * s, height * s);
        var x = centered ? (w - iw) / 2 : pad;
        var state = ink ? "/GM gs " : OpacityPrefix(opacity);
        var content = $"q {state}{ElementFace.Number(iw)} 0 0 {ElementFace.Number(ih)} {ElementFace.Number(x)} {ElementFace.Number(ContentBottom(element, y0, ch, ih))} cm /Im1 Do Q\n";
        return Finish(element, new StringBuilder(content), ink ? Resources(image, ink) : Resources(image, opacity));
    }

    // One resource dictionary per image: the display's image cache identifies an image by stream and resources, so new
    // resources on every size change would make it decode the image again each time
    private static readonly ConditionalWeakTable<PdfStream, PdfDictionary> ImageResources = new();

    /// <summary>Resources with the image as /Im1; for ink also /GM (multiply blend mode: translucent like real ink).</summary>
    private static PdfDictionary Resources(PdfStream image, bool ink = false) => ImageResources.GetValue(image, image =>
    {
        var images = new PdfDictionary();
        images.Set(PdfName.Get("Im1"), image);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("XObject"), images);
        if (ink)
        {
            var multiply = new PdfDictionary();
            multiply.Set(PdfName.Get("BM"), PdfName.Get("Multiply"));
            var states = new PdfDictionary();
            states.Set(PdfName.Get("GM"), multiply);
            resources.Set(PdfName.Get("ExtGState"), states);
        }
        return resources;
    });

    // ------------------------------------------------------------ Ballpoint texture

    /// <summary>
    /// With ballpoint texture: the ink density image, fitted like the vector form (left-aligned, vertically centred), at the
    /// resolution that is currently ready (saving uses <see cref="InkTextures.Dpi"/>). If none is ready yet, the smooth form
    /// <paramref name="smooth"/> in the texture's middle tone; the appearance is rebuilt as soon as the texture is available.
    /// </summary>
    private static ElementAppearance BuildTextured(PageElement element, InkTextures.Source source, int color, double strength, Func<int, ElementAppearance> smooth)
    {
        var (w, h) = element.UprightSize;
        var (y0, ch) = ContentArea(element, h);
        var bounds = source.Bounds;
        var scale = Math.Min(Math.Max(1, w - 2 * Padding) / bounds.Width, Math.Max(1, ch - 2 * Padding) / bounds.Height);
        var needed = source.Limit(scale * InkTextures.Dpi / 72);
        if (InkTextures.Get(source, needed) is not { } texture)
        {
            var placeholder = smooth(MiddleTone(color, strength));
            placeholder.Texture = new TextureUse(source, 0, needed);
            return placeholder;
        }
        var k = scale / texture.Scale;   // points per pixel
        var top = ContentBottom(element, y0, ch, bounds.Height * scale) + bounds.Height * scale + texture.Pad * k;
        var (iw, ih) = (texture.Width * k, texture.Height * k);
        var content = $"q /GM gs {ElementFace.Number(iw)} 0 0 {ElementFace.Number(ih)} {ElementFace.Number(Padding - texture.Pad * k)} {ElementFace.Number(top - ih)} cm /Im1 Do Q\n";
        var appearance = Finish(element, new StringBuilder(content), Resources(InkImage(texture, color, strength), ink: true));
        appearance.Texture = new TextureUse(source, texture.Scale, needed);
        return appearance;
    }

    /// <summary>Ink colour at medium density (for the smooth form until the texture is available).</summary>
    private static int MiddleTone(int color, double strength)
    {
        var palette = Imaging.InkColor.Palette(color, Imaging.PenTexture.Levels, strength);
        var at = (int)Math.Round(Imaging.InkColor.Median * (Imaging.PenTexture.Levels - 1)) * 3;
        return (palette[at] << 16) | (palette[at + 1] << 8) | palette[at + 2];
    }

    private static readonly ConditionalWeakTable<InkTexture, Dictionary<(int Color, double Strength), PdfStream>> InkImages = new();

    /// <summary>
    /// Builds ink as an indexed image: one colour per density level and fine-grain level
    /// (<see cref="Imaging.InkColor.GrainPalette"/>: white up to the chosen colour), drawn with the multiply blend mode, so that
    /// white leaves everything underneath visible and the ink is translucent. The image data (level · 4 + grain) exists once per
    /// texture; another colour or ink strength only changes the palette.
    /// </summary>
    /// <param name="texture">The ink density texture.</param>
    /// <param name="color">Ink colour as 0xRRGGBB.</param>
    /// <param name="strength">Ink strength (1 = default).</param>
    /// <returns>The image XObject, cached per texture, colour and strength.</returns>
    public static PdfStream InkImage(InkTexture texture, int color, double strength = 1)
    {
        var byColor = InkImages.GetValue(texture, static _ => []);
        lock (byColor)
        {
            if (byColor.TryGetValue((color, strength), out var known)) return known;
            var palette = Imaging.InkColor.GrainPalette(color, Imaging.PenTexture.Levels, strength);
            var dict = ImageDictionary(texture.Width, texture.Height, "DeviceRGB");
            dict.Set(PdfName.Get("ColorSpace"), new PdfArray([PdfName.Get("Indexed"), PdfName.Get("DeviceRGB"),
                PdfInteger.Of(Imaging.PenTexture.Levels * Imaging.InkColor.GrainClasses - 1), new PdfString(palette)]));
            return byColor[(color, strength)] = new PdfStream(dict, GrainIndices.GetValue(texture, WithGrain));
        }
    }

    private static readonly ConditionalWeakTable<InkTexture, byte[]> GrainIndices = new();

    /// <summary>Image values with fine grain: density level · 4 + grain level of the pixel.</summary>
    private static byte[] WithGrain(InkTexture texture)
    {
        var levels = texture.Levels;
        var indices = new byte[levels.Length];
        for (var y = 0; y < texture.Height; y++)
            for (var x = 0; x < texture.Width; x++)
            {
                var i = y * texture.Width + x;
                indices[i] = (byte)(levels[i] * Imaging.InkColor.GrainClasses + Imaging.InkColor.GrainClass(x, y));
            }
        return indices;
    }

    // ------------------------------------------------------------ Origin line

    /// <summary>Area for the content (in reading orientation): somewhat lower when an origin line sits below it.</summary>
    private static (double Bottom, double Height) ContentArea(PageElement element, double height)
    {
        if (element.OriginLine is not { Length: > 0 }) return (0, height);
        var line = OriginHeight(height);
        return (line, Math.Max(1, height - line));
    }

    /// <summary>Gets the total height for content of the given height: padding and, for sealed signatures, the origin line.</summary>
    /// <param name="element">The element (its seal or digital signing decides whether an origin line is added).</param>
    /// <param name="contentHeight">Height of the content in points.</param>
    /// <returns>The total height in points.</returns>
    public static double HeightFor(PageElement element, double contentHeight)
    {
        var height = contentHeight + 2 * Padding;
        if (element.OriginLine is not { Length: > 0 }) return height;
        // Line = 20 % of the total height (5–10 points): total = content + line
        return height + Math.Clamp(height * 0.25, 5, 10);
    }

    /// <summary>Height of the origin line: one fifth of the height, between 5 and 10 points.</summary>
    private static double OriginHeight(double height) => Math.Clamp(height * 0.2, 5, 10);

    /// <summary>
    /// Bottom edge of the content (height <paramref name="contentHeight"/>): centred in the content area; with an origin line,
    /// close above it (15 % of the gap it would have when centred), so that the line visibly belongs to the signature.
    /// </summary>
    private static double ContentBottom(PageElement element, double y0, double ch, double contentHeight)
    {
        var centered = y0 + (ch - contentHeight) / 2;
        if (element.OriginLine is not { Length: > 0 } origin) return centered;
        var (w, h) = element.UprightSize;
        var (face, _, size, lineHeight) = OriginLayout(origin, w, h);
        var textTop = lineHeight * 0.3 + face.Ascent * size;
        return centered > textTop ? textTop + (centered - textTop) * 0.15 : centered;
    }

    /// <summary>Lays out the origin line: font, glyphs, size (fits into the width) and line height.</summary>
    private static (ElementFace Face, Core.Fonts.Layout.ShapedGlyph[] Glyphs, double Size, double LineHeight) OriginLayout(string origin, double w, double h)
    {
        var face = ElementFace.For(ElementFont.Sans);
        var shaped = Core.Fonts.Layout.TextShaper.For(face.Font).Shape(origin);
        var width1 = shaped.Sum(g => g.XAdvance) / face.Font.UnitsPerEm;   // width at 1 point
        var lineHeight = OriginHeight(h);
        var size = Math.Min(lineHeight * 0.72, width1 > 0 ? (w - 2 * Padding) / width1 : lineHeight);
        return (face, shaped, size, lineHeight);
    }

    /// <summary>
    /// Completes the form; for sealed signatures the origin (name, date, device identifier) is added below in small grey type,
    /// as text, so that it is readable and searchable.
    /// </summary>
    private static ElementAppearance Finish(PageElement element, StringBuilder content, PdfDictionary resources)
    {
        if (element.OriginLine is not { Length: > 0 } origin)
            return new ElementAppearance(element, FormStream(element, resources, Encoding.ASCII.GetBytes(content.ToString())), null, NoGlyphs);
        var (w, h) = element.UprightSize;
        var (face, shaped, size, lineHeight) = OriginLayout(origin, w, h);
        var units = face.Font.UnitsPerEm;
        var line = new TextLine([.. shaped.Select(g => g.Glyph)], [.. shaped.Select(g => g.Text)], [.. shaped.Select(g => g.XAdvance * size / units)],
            shaped.Sum(g => g.XAdvance) * size / units);
        content.Append("q 0.42 g BT /FO ").Append(ElementFace.Number(size)).Append(" Tf 1 0 0 1 ")
            .Append(ElementFace.Number(Padding)).Append(' ').Append(ElementFace.Number(lineHeight * 0.3)).Append(" Tm ");
        AppendShowText(content, face, line, size);
        content.Append("ET Q\n");
        var fonts = new PdfDictionary();
        fonts.Set(PdfName.Get("FO"), face.DisplayFont);
        resources.Set(PdfName.Get("Font"), fonts);
        var glyphs = new Dictionary<int, string>();
        for (var i = 0; i < line.Glyphs.Count; i++) glyphs.TryAdd(line.Glyphs[i], line.Text[i]);
        return new ElementAppearance(element, FormStream(element, resources, Encoding.ASCII.GetBytes(content.ToString())), face, glyphs);
    }


    // ------------------------------------------------------------ Signature field

    /// <summary>Empty signature field: thin grey border, a line to sign on, and the label in small type.</summary>
    private static ElementAppearance BuildSignatureField(SignatureFieldElement field)
    {
        var (w, h) = field.UprightSize;
        var sb = new StringBuilder("q 0.6 G 0.5 w ");
        sb.Append("0.25 0.25 ").Append(ElementFace.Number(Math.Max(0, w - 0.5))).Append(' ').Append(ElementFace.Number(Math.Max(0, h - 0.5))).Append(" re S\n");
        var label = field.Label.Trim();
        var size = Math.Clamp(h * 0.16, 4, 8);
        var lineY = label.Length > 0 ? size * 1.8 : h * 0.25;
        sb.Append("0.75 G ").Append(ElementFace.Number(Padding * 2)).Append(' ').Append(ElementFace.Number(lineY)).Append(" m ")
            .Append(ElementFace.Number(w - Padding * 2)).Append(' ').Append(ElementFace.Number(lineY)).Append(" l S Q\n");
        if (label.Length == 0) return new ElementAppearance(field, FormStream(field, new PdfDictionary(), Encoding.ASCII.GetBytes(sb.ToString())), null, NoGlyphs);

        var face = ElementFace.For(ElementFont.Sans);
        var shaped = Core.Fonts.Layout.TextShaper.For(face.Font).Shape(label);
        var units = face.Font.UnitsPerEm;
        var line = new TextLine([.. shaped.Select(g => g.Glyph)], [.. shaped.Select(g => g.Text)], [.. shaped.Select(g => g.XAdvance * size / units)],
            shaped.Sum(g => g.XAdvance) * size / units);
        sb.Append("q 0.5 g BT /FL ").Append(ElementFace.Number(size)).Append(" Tf 1 0 0 1 ").Append(ElementFace.Number(Padding * 2)).Append(' ')
            .Append(ElementFace.Number(size * 0.5)).Append(" Tm ");
        AppendShowText(sb, face, line, size);
        sb.Append("ET Q\n");
        var fonts = new PdfDictionary();
        fonts.Set(PdfName.Get("FL"), face.DisplayFont);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("Font"), fonts);
        var glyphs = new Dictionary<int, string>();
        for (var i = 0; i < line.Glyphs.Count; i++) glyphs.TryAdd(line.Glyphs[i], line.Text[i]);
        return new ElementAppearance(field, FormStream(field, resources, Encoding.ASCII.GetBytes(sb.ToString())), face, glyphs);
    }
    // ------------------------------------------------------------ Redaction

    /// <summary>
    /// Mark of a redaction not yet applied: light red fill, so that the content stays readable, a fine red hatching and a border of a
    /// white line under red dashes — so the mark stands out on any ground: on white paper, over black and over coloured images, where
    /// the tint alone could hardly be seen (docs/FEHLERGEDAECHTNIS.md, No. 191). Hatching and border are hairlines (width 0: always one
    /// screen pixel); lines measured in points would cover, at high zoom, the edges of the characters the redaction is being aligned to.
    /// </summary>
    private static ElementAppearance BuildRedaction(RedactionElement redaction)
    {
        var (w, h) = redaction.UprightSize;
        var areas = redaction.Areas.Count == 0 ? [new PdfRectangle(0, 0, 1, 1)] : redaction.Areas;
        var rects = new StringBuilder();
        foreach (var a in areas)
            rects.Append(ElementFace.Number(a.Left * w)).Append(' ').Append(ElementFace.Number(a.Bottom * h)).Append(' ')
                .Append(ElementFace.Number(a.Width * w)).Append(' ').Append(ElementFace.Number(a.Height * h)).Append(" re\n");
        var sb = new StringBuilder("q /GS0 gs 0.851 0.176 0.125 rg\n").Append(rects).Append("f Q\n");
        // Hatching: diagonal lines 6 pt apart, clipped to the areas (wider apart on huge marks, at most about 2 000 lines)
        var spacing = Math.Max(6, (w + h) / 2000);
        sb.Append("q ").Append(rects).Append("W n /GS1 gs 0.851 0.176 0.125 RG 0 w\n");
        for (var x = -h; x < w; x += spacing)
            sb.Append(ElementFace.Number(x)).Append(" 0 m ").Append(ElementFace.Number(x + h)).Append(' ').Append(ElementFace.Number(h)).Append(" l\n");
        sb.Append("S Q\n");
        // Border exactly on the edge: white underneath, red dashes over it
        sb.Append("q 0 w 1 1 1 RG\n").Append(rects).Append("S 0.851 0.176 0.125 RG [3 2] 0 d\n").Append(rects).Append("S Q\n");
        var tint = new PdfDictionary();
        tint.Set(PdfName.Get("ca"), new PdfReal(0.18));
        var hatch = new PdfDictionary();
        hatch.Set(PdfName.Get("CA"), new PdfReal(0.6));
        var states = new PdfDictionary();
        states.Set(PdfName.Get("GS0"), tint);
        states.Set(PdfName.Get("GS1"), hatch);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("ExtGState"), states);
        return new ElementAppearance(redaction, FormStream(redaction, resources, Encoding.ASCII.GetBytes(sb.ToString())), null, NoGlyphs);
    }

    // ------------------------------------------------------------ Freehand

    /// <summary>Gets the bounds of the strokes (including half the stroke width) in the space of the stroke points.</summary>
    /// <param name="ink">The freehand element.</param>
    /// <returns>The bounds (y downwards); <see cref="RectD.Empty"/> without strokes.</returns>
    public static RectD InkBounds(InkElement ink)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var stroke in ink.Strokes)
            for (var i = 0; i + 1 < stroke.Length; i += 2)
            {
                left = Math.Min(left, stroke[i]);
                right = Math.Max(right, stroke[i]);
                top = Math.Min(top, stroke[i + 1]);
                bottom = Math.Max(bottom, stroke[i + 1]);
            }
        if (left > right) return RectD.Empty;
        var half = ink.StrokeWidth / 2;
        return RectD.FromLtrb(left - half, top - half, right + half, bottom + half);
    }

    /// <summary>
    /// Smooths strokes as in the vector form (Catmull-Rom) and returns them as polylines.
    /// </summary>
    /// <param name="strokes">Strokes as interleaved x, y coordinates in the space of the stroke points.</param>
    /// <param name="tolerance">Approximate maximum deviation from the curve, in the same units; each curve segment uses at most 64 steps.</param>
    /// <returns>One array of interleaved x, y per non-empty stroke; a stroke of a single point stays a single point.</returns>
    public static List<double[]> InkPolylines(IReadOnlyList<float[]> strokes, double tolerance)
    {
        var lines = new List<double[]>(strokes.Count);
        var points = new List<double>();
        foreach (var stroke in strokes)
        {
            var n = stroke.Length / 2;
            if (n == 0) continue;
            points.Clear();
            points.Add(stroke[0]);
            points.Add(stroke[1]);
            for (var k = 0; k + 1 < n; k++)
            {
                int a = Math.Max(0, k - 1) * 2, b = k * 2, d = (k + 1) * 2, e = Math.Min(n - 1, k + 2) * 2;
                double x0 = stroke[b], y0 = stroke[b + 1], x3 = stroke[d], y3 = stroke[d + 1];
                double x1 = x0 + (x3 - stroke[a]) / 6, y1 = y0 + (y3 - stroke[a + 1]) / 6;
                double x2 = x3 - (stroke[e] - x0) / 6, y2 = y3 - (stroke[e + 1] - y0) / 6;
                var dd = Math.Max(Math.Abs(x0 - 2 * x1 + x2) + Math.Abs(y0 - 2 * y1 + y2), Math.Abs(x1 - 2 * x2 + x3) + Math.Abs(y1 - 2 * y2 + y3));
                var steps = (int)Math.Clamp(Math.Ceiling(Math.Sqrt(dd * 0.75 / Math.Max(tolerance, 1e-6))), 1, 64);
                for (var s = 1; s <= steps; s++)
                {
                    double t = (double)s / steps, u = 1 - t;
                    points.Add(u * u * u * x0 + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x3);
                    points.Add(u * u * u * y0 + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y3);
                }
            }
            lines.Add([.. points]);
        }
        return lines;
    }

    /// <summary>
    /// Strokes as paths with round caps and joins, smoothed (Catmull-Rom as Bézier curves), fitted into the rectangle,
    /// left-aligned and vertically centred.
    /// </summary>
    private static ElementAppearance BuildInk(InkElement ink) =>
        ink.Textured && InkTextures.For(ink) is { } source
            ? BuildTextured(ink, source, ink.Color, ink.InkStrength, rgb => BuildStrokes(ink, rgb))
            : BuildStrokes(ink, ink.Color);

    private static ElementAppearance BuildStrokes(InkElement ink, int c)
    {
        var (w, h) = ink.UprightSize;
        var (y0, ch) = ContentArea(ink, h);
        var bounds = InkBounds(ink);
        var sb = new StringBuilder();
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            var s = Math.Min(Math.Max(1, w - 2 * Padding) / bounds.Width, Math.Max(1, ch - 2 * Padding) / bounds.Height);
            var tx = Padding - bounds.Left * s;
            var height = bounds.Height * s;
            var bottomGap = ContentBottom(ink, y0, ch, height);
            // Drawing space with y downwards → PDF with y upwards
            (double X, double Y) P(float[] stroke, int i) => (tx + stroke[i] * s, bottomGap + (bounds.Bottom - stroke[i + 1]) * s);
            sb.Append("q 1 J 1 j ").Append(ElementFace.Number(Math.Max(0.1, ink.StrokeWidth * s))).Append(" w ")
                .Append(ElementFace.Number(((c >> 16) & 0xFF) / 255.0)).Append(' ').Append(ElementFace.Number(((c >> 8) & 0xFF) / 255.0)).Append(' ')
                .Append(ElementFace.Number((c & 0xFF) / 255.0)).Append(" RG\n");
            foreach (var stroke in ink.Strokes)
            {
                var n = stroke.Length / 2;
                if (n == 0) continue;
                var p0 = P(stroke, 0);
                sb.Append(ElementFace.Number(p0.X)).Append(' ').Append(ElementFace.Number(p0.Y)).Append(" m\n");
                if (n == 1)
                {
                    // Dot: a short line whose round caps make it round
                    sb.Append(ElementFace.Number(p0.X + 0.01)).Append(' ').Append(ElementFace.Number(p0.Y)).Append(" l\n");
                    continue;
                }
                for (var k = 0; k + 1 < n; k++)
                {
                    var a = P(stroke, Math.Max(0, k - 1) * 2);
                    var b = P(stroke, k * 2);
                    var d = P(stroke, (k + 1) * 2);
                    var e = P(stroke, Math.Min(n - 1, k + 2) * 2);
                    // Catmull-Rom (tension 0.5) → cubic Bézier curve from b to d
                    sb.Append(ElementFace.Number(b.X + (d.X - a.X) / 6)).Append(' ').Append(ElementFace.Number(b.Y + (d.Y - a.Y) / 6)).Append(' ')
                        .Append(ElementFace.Number(d.X - (e.X - b.X) / 6)).Append(' ').Append(ElementFace.Number(d.Y - (e.Y - b.Y) / 6)).Append(' ')
                        .Append(ElementFace.Number(d.X)).Append(' ').Append(ElementFace.Number(d.Y)).Append(" c\n");
                }
            }
            sb.Append("S Q\n");
        }
        return Finish(ink, sb, new PdfDictionary());
    }

    // ------------------------------------------------------------ Signature

    /// <summary>Gets the outlines of a laid-out name in em units (baseline at 0), with their bounds.</summary>
    /// <param name="element">The signature.</param>
    /// <returns>The glyph outlines and their bounds; empty lists and <see cref="RectD.Empty"/> if name or font is missing.</returns>
    public static (List<(PathVerb[] Verbs, double[] Points)> Glyphs, RectD Bounds) SignatureOutlines(SignatureElement element)
    {
        var glyphs = new List<(PathVerb[], double[])>();
        if (element.Name.Trim().Length == 0 || element.FontFile.Length == 0) return (glyphs, RectD.Empty);
        var font = Core.Fonts.EmbeddedFonts.HandwritingOpenType(element.FontFile);
        var shaped = Core.Fonts.Layout.TextShaper.For(font).Shape(element.Name.Trim());
        var em = 1.0 / font.UnitsPerEm;
        var toEm = font.FontMatrix;
        double pen = 0, left = double.MaxValue, bottom = double.MaxValue, right = double.MinValue, top = double.MinValue;
        foreach (var g in shaped)
        {
            var outline = font.Outline(g.Glyph);
            if (!outline.IsEmpty)
            {
                var place = toEm * Matrix.Translate((pen + g.XOffset) * em, g.YOffset * em);
                var points = new double[outline.Points.Count];
                for (var i = 0; i + 1 < points.Length; i += 2)
                {
                    var (x, y) = place.Transform(outline.Points[i], outline.Points[i + 1]);
                    points[i] = x;
                    points[i + 1] = y;
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    bottom = Math.Min(bottom, y);
                    top = Math.Max(top, y);
                }
                glyphs.Add(([.. outline.Verbs], points));
            }
            pen += g.XAdvance;
        }
        return (glyphs, left > right ? RectD.Empty : RectD.FromLtrb(left, bottom, right, top));
    }

    /// <summary>Gets the aspect ratio of the laid-out name.</summary>
    /// <param name="element">The signature.</param>
    /// <returns>Width divided by height of the colour-glyph image or the outlines; 3 if the name is empty.</returns>
    public static double SignatureAspect(SignatureElement element)
    {
        if (SignatureImage(element) is { } image) return (double)image.Width / image.Height;
        var (_, bounds) = SignatureOutlines(element);
        return bounds.Width > 0 && bounds.Height > 0 ? bounds.Width / bounds.Height : 3;
    }

    /// <summary>
    /// Adapts the width to the aspect ratio of the content (after a change of font or name). Height and centre stay, so that
    /// the signature keeps its size and place.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <returns>The element with the adapted rectangle.</returns>
    public static PageElement FitToAspect(PageElement element)
    {
        var (_, height) = element.UprightSize;
        var width = Math.Max(1, ContentArea(element, height).Height - 2 * Padding) * Aspect(element) + 2 * Padding;
        var r = element.Rect;
        double cx = (r.Left + r.Right) / 2, cy = (r.Bottom + r.Top) / 2;
        var (rw, rh) = element.Rotation is 90 or 270 ? (height, width) : (width, height);
        return element with { Rect = new PdfRectangle(cx - rw / 2, cy - rh / 2, cx + rw / 2, cy + rh / 2) };
    }

    // Most recently built image signatures (the elements themselves keep their image through the form), limited by bytes: while
    // typing, every intermediate state ("M", "Ma", "Max" …) creates an image of about 0.3 MB (1 byte per pixel)
    private const long SignatureImageBudget = 2L << 20;
    private static readonly Dictionary<(string Font, string Name), LinkedListNode<((string, string) Key, InkTexture? Value)>> SignatureImageIndex = [];
    private static readonly LinkedList<((string, string) Key, InkTexture? Value)> SignatureImageOrder = new();
    private static long _signatureImageBytes;

    /// <summary>
    /// Gets the image signature for fonts with colour glyphs (sbix, CBDT or SVG with PNG images) as ink density: shown as it is without
    /// ballpoint texture, and the form for the texture with it; the colour is chosen freely (<see cref="InkImage"/>). Built once per
    /// font and name and shared by all elements, so moving creates no new image.
    /// </summary>
    /// <param name="element">The signature.</param>
    /// <returns>The ink density texture; <see langword="null"/> if name or font is missing or the font has no colour glyphs.</returns>
    /// <remarks>Thread-safe; the cache holds about 2 MB and evicts the least recently used entries.</remarks>
    public static InkTexture? SignatureImage(SignatureElement element)
    {
        var name = element.Name.Trim();
        if (name.Length == 0 || element.FontFile.Length == 0) return null;
        var font = Core.Fonts.EmbeddedFonts.HandwritingOpenType(element.FontFile);
        if (Core.Fonts.ColorGlyphs.For(font) is not { } color) return null;
        var key = (element.FontFile, name);
        lock (SignatureImageOrder)
        {
            if (SignatureImageIndex.TryGetValue(key, out var hit))
            {
                SignatureImageOrder.Remove(hit);
                SignatureImageOrder.AddFirst(hit);
                return hit.Value.Value;
            }
        }
        var shaped = Core.Fonts.Layout.TextShaper.For(font).Shape(name);
        // Trim the transparent margin of the glyph images; otherwise the origin line would appear far away
        var built = color.Compose(shaped, font.UnitsPerEm) is { } composed && Imaging.ImageTools.Trim(composed, 2) is { } image
            ? new InkTexture(image.Width, image.Height, Imaging.InkColor.Density(image, Imaging.PenTexture.Levels), 1, 0) : null;
        lock (SignatureImageOrder)
        {
            if (SignatureImageIndex.TryGetValue(key, out var other)) return other.Value.Value;   // another thread was faster
            SignatureImageIndex[key] = SignatureImageOrder.AddFirst((key, built));
            _signatureImageBytes += built?.Levels.Length ?? 0;
            while (_signatureImageBytes > SignatureImageBudget && SignatureImageOrder.Count > 1 && SignatureImageOrder.Last is { } oldest)
            {
                SignatureImageOrder.RemoveLast();
                SignatureImageIndex.Remove(oldest.Value.Key);
                _signatureImageBytes -= oldest.Value.Value?.Levels.Length ?? 0;
            }
        }
        return built;
    }

    private static readonly ConditionalWeakTable<Imaging.RgbaImage, PdfStream> ImageStreams = new();

    /// <summary>Converts an image into an image XObject (RGB, alpha as /SMask only if needed).</summary>
    /// <param name="image">The RGBA image.</param>
    /// <returns>The image stream, built once per image and shared.</returns>
    public static PdfStream ImageStream(Imaging.RgbaImage image) => ImageStreams.GetValue(image, static image =>
    {
        var pixels = image.Width * image.Height;
        var rgb = new byte[pixels * 3];
        // Alpha channel only if the image has transparent areas (photos without a removed background have none)
        var alpha = Imaging.ImageTools.HasTransparency(image) ? new byte[pixels] : null;
        for (var i = 0; i < pixels; i++)
        {
            rgb[i * 3] = image.Pixels[i * 4];
            rgb[i * 3 + 1] = image.Pixels[i * 4 + 1];
            rgb[i * 3 + 2] = image.Pixels[i * 4 + 2];
            if (alpha is not null) alpha[i] = image.Pixels[i * 4 + 3];
        }
        var dict = ImageDictionary(image.Width, image.Height, "DeviceRGB");
        if (alpha is not null)
            dict.Set(PdfName.Get("SMask"), new PdfStream(ImageDictionary(image.Width, image.Height, "DeviceGray"), alpha));
        return new PdfStream(dict, rgb);
    });

    private static PdfDictionary ImageDictionary(int width, int height, string colorSpace)
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Type, PdfName.Get("XObject"));
        dict.Set(PdfName.Subtype, PdfName.Get("Image"));
        dict.Set(PdfName.Get("Width"), PdfInteger.Of(width));
        dict.Set(PdfName.Get("Height"), PdfInteger.Of(height));
        dict.Set(PdfName.Get("ColorSpace"), PdfName.Get(colorSpace));
        dict.Set(PdfName.Get("BitsPerComponent"), PdfInteger.Of(8));
        dict.Set(PdfName.Get("Interpolate"), PdfBoolean.True);
        return dict;
    }

    /// <summary>
    /// Name as filled outlines, fitted into the rectangle (aspect ratio kept, left-aligned, vertically centred). Not as text, so
    /// that the file needs no font and nobody can copy the name out.
    /// </summary>
    private static ElementAppearance BuildSignature(SignatureElement element)
    {
        // Colour glyphs: with texture over the form of their images, otherwise the images themselves as ink density in the chosen colour,
        // fitted like the outlines
        if (SignatureImage(element) is { } picture)
        {
            ElementAppearance Images(int rgb) => BuildPicture(element, InkImage(picture, rgb, element.InkStrength), picture.Width, picture.Height, centered: false, ink: true);
            return element.Textured && InkTextures.For(element, picture) is { } form
                ? BuildTextured(element, form, element.Color, element.InkStrength, Images)
                : Images(element.Color);
        }
        // Lay out the outlines once: for the texture and for the smooth form (until the texture is ready)
        var (glyphs, bounds) = SignatureOutlines(element);
        return element.Textured && InkTextures.For(element, glyphs, bounds) is { } source
            ? BuildTextured(element, source, element.Color, element.InkStrength, rgb => BuildOutlines(element, rgb, glyphs, bounds))
            : BuildOutlines(element, element.Color, glyphs, bounds);
    }

    private static ElementAppearance BuildOutlines(SignatureElement element, int c, List<(PathVerb[] Verbs, double[] Points)> glyphs, RectD bounds)
    {
        var (w, h) = element.UprightSize;
        var (y0, ch) = ContentArea(element, h);
        var sb = new StringBuilder();
        if (glyphs.Count > 0 && bounds.Width > 0 && bounds.Height > 0)
        {
            var scale = Math.Min(Math.Max(1, w - 2 * Padding) / bounds.Width, Math.Max(1, ch - 2 * Padding) / bounds.Height);
            var tx = Padding - bounds.Left * scale;
            var ty = ContentBottom(element, y0, ch, bounds.Height * scale) - bounds.Top * scale;
            sb.Append("q\n").Append(ElementFace.Number(((c >> 16) & 0xFF) / 255.0)).Append(' ')
                .Append(ElementFace.Number(((c >> 8) & 0xFF) / 255.0)).Append(' ').Append(ElementFace.Number((c & 0xFF) / 255.0)).Append(" rg\n");
            foreach (var (verbs, points) in glyphs)
            {
                var p = 0;
                foreach (var verb in verbs)
                {
                    switch (verb)
                    {
                        case PathVerb.Move or PathVerb.Line:
                            Point(sb, points, p, scale, tx, ty).Append(verb == PathVerb.Move ? "m\n" : "l\n");
                            p += 2;
                            break;
                        case PathVerb.Cubic:
                            for (var k = 0; k < 3; k++) Point(sb, points, p + 2 * k, scale, tx, ty);
                            sb.Append("c\n");
                            p += 6;
                            break;
                        case PathVerb.Close:
                            sb.Append("h\n");
                            break;
                    }
                }
            }
            sb.Append("f\nQ\n");
        }
        return Finish(element, sb, new PdfDictionary());
    }

    private static StringBuilder Point(StringBuilder sb, double[] points, int at, double scale, double tx, double ty) =>
        sb.Append(ElementFace.Number(tx + points[at] * scale)).Append(' ').Append(ElementFace.Number(ty + points[at + 1] * scale)).Append(' ');

    // ------------------------------------------------------------ Text

    /// <summary>A laid-out line: glyphs, their text, advance per glyph and width (in points).</summary>
    public sealed record TextLine(List<int> Glyphs, List<string> Text, List<double> Advances, double Width);

    /// <summary>
    /// Lays out lines (own shaper: kerning, ligatures) and breaks them: always at line breaks, and with
    /// <see cref="TextElement.Multiline"/> also at the available width.
    /// </summary>
    /// <param name="element">The text element.</param>
    /// <param name="face">The font to lay out with.</param>
    /// <returns>The lines; trailing spaces do not count towards a wrapped line's width.</returns>
    public static List<TextLine> Layout(TextElement element, ElementFace face)
    {
        var (width, _) = element.UprightSize;
        var available = Math.Max(1, width - 2 * Padding);
        var scale = element.FontSize / face.Font.UnitsPerEm;
        var shaper = Core.Fonts.Layout.TextShaper.For(face.Font);
        var lines = new List<TextLine>();
        foreach (var paragraph in element.Text.Replace("\r\n", "\n").Split('\n'))
        {
            var shaped = shaper.Shape(paragraph.Replace('\t', ' '));
            var glyphs = new List<int>(shaped.Length);
            var texts = new List<string>(shaped.Length);
            var widths = new List<double>(shaped.Length);
            foreach (var s in shaped)
            {
                glyphs.Add(s.Glyph);
                texts.Add(s.Text);
                widths.Add(s.XAdvance * scale);
            }
            if (!element.Multiline)
            {
                lines.Add(new TextLine(glyphs, texts, widths, widths.Sum()));
                continue;
            }
            // Greedy line breaking: after the last space that still fits; a word that is too long is broken hard
            var start = 0;
            while (true)
            {
                double used = 0;
                var end = start;
                var lastSpace = -1;
                while (end < glyphs.Count && (used + widths[end] <= available || end == start))
                {
                    if (texts[end] == " ") lastSpace = end;
                    used += widths[end++];
                }
                if (end < glyphs.Count && lastSpace > start) end = lastSpace + 1;
                // Spaces at the end of a line do not count towards the width
                var visibleEnd = end;
                while (visibleEnd > start && texts[visibleEnd - 1] == " ") visibleEnd--;
                double lineWidth = 0;
                for (var i = start; i < visibleEnd; i++) lineWidth += widths[i];
                lines.Add(new TextLine(glyphs[start..visibleEnd], texts[start..visibleEnd], widths[start..visibleEnd], lineWidth));
                if (end >= glyphs.Count) break;
                start = end;
            }
        }
        return lines;
    }

    /// <summary>Gets the height (in reading orientation) the text needs for all its lines.</summary>
    /// <param name="element">The text element.</param>
    /// <returns>The height in points, including padding.</returns>
    public static double NeededHeight(TextElement element)
    {
        var face = ElementFace.For(element.Font);
        var lines = Math.Max(1, Layout(element, face).Count);
        return 2 * Padding + (face.Ascent - face.Descent) * element.FontSize + (lines - 1) * element.FontSize * LineSpacing;
    }

    /// <summary>Enlarges the height so that the whole text fits, downwards in reading orientation; it never shrinks.</summary>
    /// <param name="element">The text element.</param>
    /// <returns>The element with the enlarged rectangle, or the element itself if the text already fits.</returns>
    public static TextElement FitHeight(TextElement element)
    {
        var (_, height) = element.UprightSize;
        var grow = NeededHeight(element) - height;
        if (grow <= 0.01) return element;
        var r = element.Rect;
        return element with
        {
            Rect = element.Rotation switch
            {
                90 => r with { Right = r.Right + grow },
                180 => r with { Top = r.Top + grow },
                270 => r with { Left = r.Left - grow },
                _ => r with { Bottom = r.Bottom - grow },
            },
        };
    }

    private static ElementAppearance BuildText(TextElement element)
    {
        var face = ElementFace.For(element.Font);
        var (w, h) = element.UprightSize;
        var size = Math.Max(0.5, element.FontSize);
        var lines = Layout(element, face);
        var glyphs = new Dictionary<int, string>();
        var sb = new StringBuilder();
        if (element.IsField) sb.Append("/Tx BMC\n");
        sb.Append("q\n0 0 ").Append(ElementFace.Number(w)).Append(' ').Append(ElementFace.Number(h)).Append(" re W n\n");
        var r = ((element.Color >> 16) & 0xFF) / 255.0;
        var g = ((element.Color >> 8) & 0xFF) / 255.0;
        var b = (element.Color & 0xFF) / 255.0;
        sb.Append("BT\n/F1 ").Append(ElementFace.Number(size)).Append(" Tf\n");
        sb.Append(ElementFace.Number(r)).Append(' ').Append(ElementFace.Number(g)).Append(' ').Append(ElementFace.Number(b)).Append(" rg\n");
        var y = h - Padding - face.Ascent * size;
        foreach (var line in lines)
        {
            var x = element.Alignment switch
            {
                ElementAlignment.Center => (w - line.Width) / 2,
                ElementAlignment.Right => w - Padding - line.Width,
                _ => Padding,
            };
            if (line.Glyphs.Count > 0)
            {
                sb.Append("1 0 0 1 ").Append(ElementFace.Number(x)).Append(' ').Append(ElementFace.Number(y)).Append(" Tm ");
                AppendShowText(sb, face, line, size);
                for (var i = 0; i < line.Glyphs.Count; i++) glyphs.TryAdd(line.Glyphs[i], line.Text[i]);
            }
            y -= size * LineSpacing;
        }
        sb.Append("ET\nQ\n");
        if (element.IsField) sb.Append("EMC\n");

        var fonts = new PdfDictionary();
        fonts.Set(PdfName.Get("F1"), face.DisplayFont);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("Font"), fonts);
        var form = FormStream(element, resources, Encoding.ASCII.GetBytes(sb.ToString()));
        return new ElementAppearance(element, form, face, glyphs);
    }

    /// <summary>
    /// Writes a line as TJ: where the shaper deviates from the glyph's default advance (/W) — kerning —, the difference is
    /// inserted as a number (thousandths of the font size, positive = to the left).
    /// </summary>
    private static void AppendShowText(StringBuilder sb, ElementFace face, TextLine line, double size)
    {
        sb.Append('[');
        var pending = new List<int>();
        for (var i = 0; i < line.Glyphs.Count; i++)
        {
            pending.Add(line.Glyphs[i]);
            var adjust = face.Width1000(line.Glyphs[i]) - line.Advances[i] / size * 1000;
            if (Math.Abs(adjust) <= 0.05 || i + 1 == line.Glyphs.Count) continue;
            ElementFace.AppendHex(sb, pending);
            pending.Clear();
            sb.Append(ElementFace.Number(adjust));
        }
        if (pending.Count > 0) ElementFace.AppendHex(sb, pending);
        sb.Append("] TJ\n");
    }

    private static PdfStream FormStream(PageElement element, PdfDictionary resources, byte[] content)
    {
        var (w, h) = element.UprightSize;
        var dict = new PdfDictionary();
        dict.Set(PdfName.Type, PdfName.Get("XObject"));
        dict.Set(PdfName.Subtype, PdfName.Get("Form"));
        dict.Set(PdfName.Get("BBox"), new PdfArray([PdfInteger.Of(0), PdfInteger.Of(0), new PdfReal(w), new PdfReal(h)]));
        if (element.Rotation != 0)
        {
            var m = Rotation(element.Rotation);
            dict.Set(PdfName.Get("Matrix"), new PdfArray([PdfInteger.Of((long)m.A), PdfInteger.Of((long)m.B), PdfInteger.Of((long)m.C),
                PdfInteger.Of((long)m.D), PdfInteger.Of(0), PdfInteger.Of(0)]));
        }
        dict.Set(PdfName.Resources, resources);
        return new PdfStream(dict, content);
    }

    private static PdfArray Rectangle(PdfRectangle r) =>
        new([new PdfReal(r.Left), new PdfReal(r.Bottom), new PdfReal(r.Right), new PdfReal(r.Top)]);
}
