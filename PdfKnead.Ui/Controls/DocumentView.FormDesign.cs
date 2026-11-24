using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.ViewModels;

namespace PdfKnead.Controls;

/// <summary>
/// “Edit form” mode: a click selects a field (instead of filling it in), Ctrl+click and a dragged frame select several,
/// dragging moves all selected ones, and the handles change the size, also for fields of other programs (their appearance
/// is redrawn when saving). All fields show their frame and their name.
/// </summary>
public sealed partial class DocumentView
{
    private static readonly IBrush DesignBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0x3D, 0x7E, 0xFF));
    private static readonly IPen DesignPen = new Pen(DesignBrush, 1);
    private static readonly IPen LabelPen = new Pen(DesignBrush, 1, new DashStyle([2, 2], 0));
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0x46, 0xE5));
    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x4F, 0x46, 0xE5));
    private static readonly IPen SelectedPen = new Pen(AccentBrush, 1.5);
    private static readonly IPen PrimaryPen = new Pen(AccentBrush, 2.5);
    private static readonly IPen HaloPen = new Pen(Brushes.White, 5);
    private static readonly IPen CornerPen = new Pen(Brushes.White, 1.5);
    private static readonly IBrush NameBackground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
    private static readonly Typeface NameFace = new(FontFamily.Default);

    private static readonly IBrush MarkBrush = new SolidColorBrush(Color.FromArgb(0xC8, 0x3D, 0x5A, 0x8C));
    private static readonly IPen MarkPen = new Pen(MarkBrush, 1.2);

    /// <summary>Kinds of marks drawn at fields on screen.</summary>
    private enum FieldMarkKind
    {
        ComboBox,
        Calendar,

        /// <summary>Empty code field with a QR or Data Matrix code: the QR symbol in its middle.</summary>
        MatrixCode,

        /// <summary>Empty code field with a bar code: the bar code symbol in its middle.</summary>
        BarCode,
    }

    /// <summary>
    /// Mark at the field, on screen only (not printed, not in the file): a triangle for combo boxes, a small calendar for date
    /// fields, a code symbol in an empty code field (its appearance is blank until it holds content); other programs show such marks only
    /// inside the field, and the appearance does not contain them. Null for none.
    /// </summary>
    private static FieldMarkKind? FieldMark(DocumentViewModel doc, PageElement element) => doc.SpecOf(element) switch
    {
        { Kind: FormFieldKind.ComboBox } => FieldMarkKind.ComboBox,
        { Kind: FormFieldKind.Text, Code: { } look } => DocumentViewModel.ValueOf(element).Length > 0 ? null
            : look.IsMatrix ? FieldMarkKind.MatrixCode : FieldMarkKind.BarCode,
        { Kind: FormFieldKind.Text, Format.Kind: FieldFormatKind.Date } => FieldMarkKind.Calendar,
        _ => null,
    };

    /// <summary>Rotation of the field content (foreign fields: /MK /R).</summary>
    private static int MarkRotation(PageElement element) =>
        element is ForeignAnnotation foreign ? ElementAppearance.WidgetRotation(foreign.Original) : element.Rotation;

    /// <summary>Draws the triangle or calendar at the end of the field in reading direction (<paramref name="turn"/>: clockwise rotation).</summary>
    private static void DrawFieldMark(DrawingContext context, Rect field, FieldMarkKind kind, int turn)
    {
        if (kind is FieldMarkKind.MatrixCode or FieldMarkKind.BarCode)
        {
            // the symbol of the ribbon (20 units) in the middle of the field, upright for the reader
            if (Avalonia.Application.Current?.TryGetResource(kind == FieldMarkKind.MatrixCode ? "Icon.QrCode" : "Icon.Barcode", null, out var icon) != true ||
                icon is not Geometry symbol) return;
            var side = Math.Clamp(Math.Min(field.Width, field.Height) * 0.5, 10, 48);
            using var placed = context.PushTransform(Matrix.CreateTranslation(-10, -10) * Matrix.CreateScale(side / 20, side / 20)
                                                     * Matrix.CreateRotation(turn * Math.PI / 180) * Matrix.CreateTranslation(field.Center.X, field.Center.Y));
            context.DrawGeometry(MarkBrush, null, symbol);
            return;
        }
        var across = turn is 90 or 270 ? field.Width : field.Height;
        var size = Math.Clamp(across * 0.55, 6, 14);
        if (size > (turn is 90 or 270 ? field.Height : field.Width) * 0.5) return;   // too narrow
        // Centre of the mark: at the end of the field in reading direction.
        var center = turn switch
        {
            90 => new Point(field.Center.X, field.Bottom - across / 2),
            180 => new Point(field.Left + across / 2, field.Center.Y),
            270 => new Point(field.Center.X, field.Top + across / 2),
            _ => new Point(field.Right - across / 2, field.Center.Y),
        };
        using var _ = context.PushTransform(Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateRotation(turn * Math.PI / 180)
                                           * Matrix.CreateTranslation(center.X, center.Y));
        var h = size / 2;
        if (kind == FieldMarkKind.ComboBox)
        {
            var arrow = new StreamGeometry();
            using (var g = arrow.Open())
            {
                g.BeginFigure(new Point(center.X - h * 0.8, center.Y - h * 0.35), true);
                g.LineTo(new Point(center.X + h * 0.8, center.Y - h * 0.35));
                g.LineTo(new Point(center.X, center.Y + h * 0.5));
                g.EndFigure(true);
            }
            context.DrawGeometry(MarkBrush, null, arrow);
            return;
        }
        // Calendar: frame with a header bar and two rings.
        var box = new Rect(center.X - h, center.Y - h * 0.85, size, size * 0.85);
        context.DrawRectangle(null, MarkPen, box, 1.5, 1.5);
        context.FillRectangle(MarkBrush, new Rect(box.Left, box.Top, box.Width, box.Height * 0.28));
        context.DrawLine(MarkPen, new Point(box.Left + box.Width * 0.3, box.Top - 1.5), new Point(box.Left + box.Width * 0.3, box.Top + 1.5));
        context.DrawLine(MarkPen, new Point(box.Right - box.Width * 0.3, box.Top - 1.5), new Point(box.Right - box.Width * 0.3, box.Top + 1.5));
    }

    /// <summary>Topmost form field (or field label) under a point of the view.</summary>
    private ElementRef? FieldAt(Point viewPoint)
    {
        if (_doc is null || PageAt(viewPoint) is not { } page) return null;
        var elements = _doc.ElementsOn(page);
        for (var i = elements.Count - 1; i >= 0; i--)
            if ((DocumentViewModel.IsFormField(elements[i]) || DocumentViewModel.IsFieldLabel(elements[i])) && ViewRect(page, elements[i].Rect).Inflate(2).Contains(viewPoint))
                return new ElementRef(page, elements[i]);
        return null;
    }

    private static bool Same(ElementRef a, ElementRef b) => a.Page == b.Page && ReferenceEquals(a.Element, b.Element);

    /// <summary>Left button in “Edit form” mode: handle, field (Ctrl or Shift: add to the selection) or dragging a selection frame.</summary>
    private bool DesignPressed(PointerPressedEventArgs e, Point position, DocumentViewModel doc)
    {
        var add = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (!add && doc.FieldSelection.Count == 1 && HandleAt(position) is { } handle && doc.SelectedElement is { } selected && doc.CanDesignForms)
        {
            BeginDrag(DragMode.Resize, selected, position, handle);
            e.Pointer.Capture(this);
            return true;
        }
        if (FieldAt(position) is { } hit)
        {
            // Double-click on a label: edit its text.
            if (e.ClickCount >= 2 && DocumentViewModel.IsFieldLabel(hit.Element) && doc.CanDesignForms)
            {
                _elementDrag = null;
                doc.BeginEdit(hit, isNew: false);
                return true;
            }
            if (doc.ShowTabOrder)
            {
                doc.PlaceInTabOrder(hit);   // click by click 1, 2, 3 …
                return true;
            }
            if (add)
            {
                doc.ToggleFieldSelection(hit);
                return true;
            }
            var selection = doc.FieldSelection;
            // On a field that is already selected: the selection stays (all of them move along), and it becomes the primary field.
            if (selection.Any(r => Same(r, hit))) doc.SelectFields([.. selection.Where(r => !Same(r, hit)), hit]);
            else doc.SelectedElement = hit;
            if (doc.CanDesignForms)
            {
                BeginDrag(DragMode.Move, hit, position, 0);
                e.Pointer.Capture(this);
            }
            // Say why nothing moves; silence would look like a fault of the button.
            else doc.Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
            return true;
        }
        if (!add) doc.SelectFields([]);
        if (PageAt(position) is { } page)
        {
            var start = ToDocument(position);
            _elementDrag = new ElementDrag { Mode = DragMode.Select, Page = page, Start = start, Current = new Rect(start, start) };
            e.Pointer.Capture(this);
        }
        return true;
    }

    /// <summary>
    /// Keys in “Edit form” mode: Del deletes the selection, Ctrl+D duplicates, arrows move all of them (Shift: 10 points),
    /// Esc clears the selection or ends “Insert field”, and without a selection ends the mode. true = handled.
    /// </summary>
    private bool DesignKey(KeyEventArgs e, DocumentViewModel doc)
    {
        var selection = doc.FieldSelection;
        switch (e.Key)
        {
            case Key.Escape when doc.ShowTabOrder:
                doc.SetShowTabOrder(false);
                return true;
            case Key.Escape when doc.Tool == ViewTool.InsertFormField:
                doc.Tool = ViewTool.DesignForm;   // the field placed last stays selected
                return true;
            case Key.Escape when selection.Count > 0:
                doc.SelectFields([]);
                return true;
            // Without a selection, Esc ends the mode (quick corrections from the Home tab, then continue filling in).
            case Key.Escape when _elementDrag is null && doc.IsFormDesign:
                doc.Tool = ViewTool.SelectText;
                return true;
            case Key.D when e.KeyModifiers.HasFlag(KeyModifiers.Control) && selection.Count > 0:
                doc.DuplicateSelectedFields();
                return true;
            case Key.Delete or Key.Back when selection.Count > 0 && doc.CanDesignForms:
                doc.DeleteSelectedFields();
                return true;
            case Key.Left or Key.Right or Key.Up or Key.Down when selection.Count > 0 && doc.CanDesignForms:
                var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : Math.Clamp(1 / Scale, 0.05, 1);
                var (sx, sy) = e.Key switch { Key.Left => (-step, 0.0), Key.Right => (step, 0.0), Key.Up => (0.0, -step), _ => (0.0, step) };
                // Screen direction converted into page space (whether the page is rotated or not), using the primary field.
                var primary = selection[^1];
                var view = ViewRect(primary.Page, primary.Element.Rect).Translate(new Vector(sx * Scale, sy * Scale));
                var moved = PageRect(primary.Page, view);
                doc.MoveSelectedFields(moved.Left - primary.Element.Rect.Left, moved.Bottom - primary.Element.Rect.Bottom, Tr.T("Felder verschieben"), continueLast: true);
                return true;
        }
        return false;
    }

    /// <summary>Frame released: all fields of the page that it touches are added to the selection.</summary>
    private void SelectInFrame(DocumentViewModel doc, ElementDrag drag)
    {
        if (!drag.Moved) return;
        var frame = ToViewRect(drag.Current);
        var chosen = doc.FieldSelection.Where(r => r.Page == drag.Page).ToList();
        foreach (var element in doc.ElementsOn(drag.Page))
            if (DocumentViewModel.IsFormField(element) && ViewRect(drag.Page, element.Rect).Intersects(frame) &&
                !chosen.Any(r => ReferenceEquals(r.Element, element)))
                chosen.Add(new ElementRef(drag.Page, element));
        doc.SelectFields(chosen);
    }

    /// <summary>Moving the whole selection released: one undo step for all of them.</summary>
    private void MoveFieldSelection(DocumentViewModel doc, ElementDrag drag)
    {
        if (drag.Original is not { } original || drag.Preview is not { } preview) return;
        var dx = preview.Rect.Left - original.Element.Rect.Left;
        var dy = preview.Rect.Bottom - original.Element.Rect.Bottom;
        doc.MoveSelectedFields(dx, dy, Tr.T("Felder verschieben"));
    }

    /// <summary>Inserts a field: dragged, or on a click in the default size (check boxes and radio buttons centred on the spot, other fields with their left edge at the spot, vertically centred).</summary>
    private void CreateFormField(DocumentViewModel doc, ElementDrag drag)
    {
        var scale = Scale;
        var box = drag.Current;
        if (!drag.Moved || box.Width < 6 * scale || box.Height < 6 * scale)
        {
            var (w, h) = DocumentViewModel.DefaultFieldSize(doc.FieldPreset);
            var square = doc.FieldPreset is FieldPreset.CheckBox or FieldPreset.Radio;
            box = new Rect(drag.Start.X - (square ? w * scale / 2 : 0), drag.Start.Y - h * scale / 2, w * scale, h * scale);
        }
        doc.CreateField(drag.Page, PageRect(drag.Page, ToViewRect(box)), DisplayRotation(drag.Page));
    }

    private static readonly IBrush TabBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x7E, 0xFF));
    private static readonly Typeface TabFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
    private static readonly Dictionary<int, FormattedText> TabTexts = [];

    /// <summary>Draws the tab order number as a badge at the top left of the field.</summary>
    private static void DrawTabNumber(DrawingContext context, Rect field, int number)
    {
        if (number <= 0) return;
        // Lay out the digits once (text with fixed font and size), not again for every frame and field; at most 5,000 are kept.
        if (!TabTexts.TryGetValue(number, out var text))
        {
            text = new FormattedText(number.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, TabFace, 11, Brushes.White);
            if (TabTexts.Count < 5000) TabTexts[number] = text;
        }
        var size = Math.Max(16, text.Width + 8);
        var badge = new Rect(field.Left - size / 2, field.Top - size / 2, size, 16);
        context.DrawRectangle(TabBrush, null, badge, 8, 8);
        context.DrawText(text, new Point(badge.Center.X - text.Width / 2, badge.Center.Y - text.Height / 2));
    }

    // Layer numbers (see KONZEPT.md, section 5.13): dark grey, top right; the tab numbers are at the top left.
    private static readonly IBrush LayerBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x4B, 0x5A));
    private static readonly Dictionary<int, FormattedText> LayerTexts = [];

    /// <summary>Draws the layer number at every element of the visible pages (1 = at the very back), if switched on.</summary>
    private void DrawLayerNumbers(DrawingContext context)
    {
        if (_doc is not { ShowLayers: true } doc) return;
        var screen = new Rect(Bounds.Size);
        for (var page = 0; page < _pageRects.Count; page++)
        {
            if (!_shown[page] || !ToViewRect(_pageRects[page]).Intersects(screen)) continue;
            var elements = doc.ElementsOn(page);
            for (var i = 0; i < elements.Count; i++)
            {
                var rect = ViewRect(page, elements[i].Rect);
                if (!rect.Inflate(12).Intersects(screen)) continue;
                var number = i + 1;
                if (!LayerTexts.TryGetValue(number, out var text))
                {
                    text = new FormattedText(number.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, TabFace, 11, Brushes.White);
                    if (LayerTexts.Count < 5000) LayerTexts[number] = text;
                }
                var width = Math.Max(16, text.Width + 8);
                var badge = new Rect(rect.Right - width / 2, rect.Top - 8, width, 16);
                context.DrawRectangle(LayerBrush, null, badge, 3, 3);   // squarer than the round tab numbers
                context.DrawText(text, new Point(badge.Center.X - text.Width / 2, badge.Center.Y - text.Height / 2));
            }
        }
    }

    // the chain of linked fields (Fluent UI System Icons, Link 20 Regular — the same path as Icon.Link)
    private static Geometry? _linkMark;

    private static Geometry? LinkMark => _linkMark ??= Application.Current?.TryGetResource("Icon.Link", null, out var icon) == true ? icon as Geometry : null;

    /// <summary>Draws the frames and names of all fields of the visible pages, with the selected ones highlighted.</summary>
    private void DrawDesignFrames(DrawingContext context)
    {
        if (_doc is not { FieldsMovable: true } doc) return;
        var screen = new Rect(Bounds.Size);
        var size = Math.Clamp(9 * Math.Sqrt(Scale), 8, 12);
        // Tab order numbers at every field (including boxes and at small zoom), drawn last so that no name covers them.
        // Counted along instead of looked up per field; otherwise they appeared only at fields wide enough for their name.
        var badges = doc.ShowTabOrder ? new List<(Rect, int)>() : null;
        for (var page = 0; page < _pageRects.Count; page++)
        {
            if (!_shown[page] || !ToViewRect(_pageRects[page]).Intersects(screen)) continue;
            var number = 0;
            foreach (var element in doc.ElementsOn(page))
            {
                if (DocumentViewModel.IsFieldLabel(element))
                {
                    // Label: subtly dashed (movable, belongs to the box).
                    if (ViewRect(page, element.Rect) is var labelRect && labelRect.Intersects(screen)) context.DrawRectangle(null, LabelPen, labelRect);
                    continue;
                }
                if (!DocumentViewModel.IsFormField(element)) continue;
                number++;
                var rect = ViewRect(page, element.Rect);
                if (!rect.Intersects(screen)) continue;
                context.DrawRectangle(null, DesignPen, rect);
                badges?.Add((rect, number));
                var name = doc.FieldNameOf(element);
                if (name.Length == 0 || rect.Width < 20) continue;
                var text = new FormattedText(name, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, NameFace, size, DesignBrush)
                {
                    MaxTextWidth = Math.Max(20, rect.Width + 80),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                // linked fields (the same name and kind, one value): a chain after the name
                var linked = doc.IsLinkedField(element);
                var label = new Rect(rect.Left, rect.Top - text.Height - 1, text.Width + 4 + (linked ? text.Height : 0), text.Height);
                context.FillRectangle(NameBackground, label);
                context.DrawText(text, label.TopLeft + new Point(2, 0));
                if (linked && LinkMark is { } mark)
                {
                    var scale = text.Height / 20;
                    using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(label.Left + text.Width + 3, label.Top)))
                        context.DrawGeometry(DesignBrush, null, mark);
                }
            }
        }
        if (badges is not null)
            foreach (var (rect, n) in badges) DrawTabNumber(context, rect, n);
        // Multiple selection: every selected field tinted with a strong frame; the reference field (chosen last, used for
        // alignment) thicker and with corners. Resize handles exist only for a single field.
        if (doc.FieldSelection.Count > 1)
        {
            var selection = doc.FieldSelection;
            for (var i = 0; i < selection.Count; i++)
            {
                var selected = selection[i];
                if (selected.Page >= _pageRects.Count || !_shown[selected.Page]) continue;
                var element = _elementDrag is { Previews: { } previews } && previews.TryGetValue(selected.Element, out var moved) ? moved
                    : _elementDrag is { Original: { } original, Preview: { } preview } && ReferenceEquals(original.Element, selected.Element) ? preview
                    : selected.Element;
                var rect = ViewRect(selected.Page, element.Rect).Inflate(2);
                var primary = i == selection.Count - 1;
                // White halo under the frame: visible on any background (including areas in the accent colour).
                context.DrawRectangle(SelectedBrush, HaloPen, rect, 2, 2);
                context.DrawRectangle(null, primary ? PrimaryPen : SelectedPen, rect, 2, 2);
                if (!primary) continue;
                foreach (var corner in (Point[])[rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft])
                {
                    var square = new Rect(corner.X - 3.5, corner.Y - 3.5, 7, 7);
                    context.DrawRectangle(AccentBrush, CornerPen, square);
                }
            }
        }
    }
}
