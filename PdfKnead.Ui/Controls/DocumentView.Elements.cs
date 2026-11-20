using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.ViewModels;
using PdfMatrix = PdfKnead.Core.Pdf.Graphics.Matrix;

namespace PdfKnead.Controls;

/// <summary>
/// Elements on the pages: drawing (above the tiles), selecting, moving, resizing,
/// dragging new ones. Calculations happen in the view, where the edges are always horizontal and
/// vertical (rotations only in quarter turns), and are converted back into page space only at the end.
/// </summary>
public sealed partial class DocumentView
{
    private const double HandleSize = 8;
    private const double MinElementPoints = 8;
    private const double MinShapePoints = 2;

    /// <summary>
    /// Smallest redaction in the view: 3 user interface units, or half a point if that is larger; at high zoom a box can thus be
    /// placed exactly over a single character or a small line (8 points as for text fields would be too coarse).
    /// </summary>
    private double MinRedactionView => Math.Max(0.5 * Scale, 3);

    private static readonly IBrush FrameBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x7E, 0xFF));
    private static readonly IPen FramePen = new Pen(FrameBrush, 1, new DashStyle([4, 3], 0));
    private static readonly IPen HandlePen = new Pen(FrameBrush, 1);
    private static readonly IBrush FieldBrush = new SolidColorBrush(Color.FromArgb(0x26, 0x3D, 0x7E, 0xFF));
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);
    private static readonly Cursor[] HandleCursors =
    [
        new(StandardCursorType.TopLeftCorner), new(StandardCursorType.TopSide), new(StandardCursorType.TopRightCorner),
        new(StandardCursorType.RightSide), new(StandardCursorType.BottomRightCorner), new(StandardCursorType.BottomSide),
        new(StandardCursorType.BottomLeftCorner), new(StandardCursorType.LeftSide),
    ];

    private enum DragMode
    {
        Create,
        Move,
        Resize,

        /// <summary>Frame for selecting several fields.</summary>
        Select,
    }

    /// <summary>Drag in progress: start rectangle in document coordinates (independent of scrolling).</summary>
    private sealed class ElementDrag
    {
        public DragMode Mode;
        public int Page;
        public ElementRef? Original;
        public Point Start;          // document coordinates
        public Rect StartRect;       // document coordinates
        public int Handle;
        public bool Moved;
        public Rect Current;         // document coordinates
        public Point End;            // dragging a shape: where the mouse is (line and arrow from the start to here), document coordinates
        public PageElement? Preview;
        public Dictionary<PageElement, PageElement>? Previews;   // multiple selection: all of them moved
    }

    private ElementDrag? _elementDrag;

    // ------------------------------------------------------------ Geometry

    /// <summary>Rotation with which a page is currently displayed (file and view).</summary>
    private int DisplayRotation(int page) => (_doc!.Pages[page].Page.Rotate + _doc.Rotation) % 360;

    /// <summary>Rectangle of an element in the view (control coordinates).</summary>
    private Rect ViewRect(int page, PdfRectangle rect)
    {
        var r = PageToView(page).TransformBounds(Core.Pdf.Graphics.RectD.FromLtrb(rect.Left, rect.Bottom, rect.Right, rect.Top));
        return new Rect(r.Left, r.Top, r.Width, r.Height);
    }

    /// <summary>Rectangle of the view to rectangle in page space.</summary>
    private PdfRectangle PageRect(int page, Rect view)
    {
        var r = PageToView(page).Invert().TransformBounds(Core.Pdf.Graphics.RectD.FromLtrb(view.Left, view.Top, view.Right, view.Bottom));
        return new PdfRectangle(r.Left, r.Top, r.Right, r.Bottom);
    }

    private Point ToDocument(Point view) => view + _offset;

    private Rect ToViewRect(Rect document) => document.Translate(-_offset);

    /// <summary>For the input box: rectangle in the view, scale (user interface units per point) and clockwise rotation of the text.</summary>
    internal (Rect Box, double Scale, int Turn)? EditorGeometry()
    {
        if (_doc?.EditingElement is not { } editing || editing.Page >= _pageRects.Count || !_shown[editing.Page]) return null;
        var turn = ((DisplayRotation(editing.Page) - editing.Element.Rotation) % 360 + 360) % 360;
        return (ViewRect(editing.Page, editing.Element.Rect), Scale, turn);
    }

    // ------------------------------------------------------------ Elements that keep their size (NoZoom)

    /// <summary>
    /// Elements that keep their size on screen above 100 %: own notes and annotations of the file with NoZoom or as a text note (ISO 32000-2,
    /// 12.5.3, 12.5.6.4; docs/FEHLERGEDAECHTNIS.md, No. 577). Annotations drawn into the tiles get it from the renderer (No. 576).
    /// </summary>
    internal static bool KeepsSize(PageElement element) =>
        element is NoteElement || element is ForeignAnnotation f && (f.Kind == "Text" || ((f.Original.GetInteger(Core.Pdf.Objects.PdfName.Get("F")) ?? 0) & 8) != 0);

    /// <summary>1 / zoom above 100 %, else 1 — as the tiles of this zoom draw annotations with NoZoom (their level; No. 589).</summary>
    private double NoZoomScale
    {
        get
        {
            var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            return PdfKnead.Rendering.DocumentRenderer.NoZoomScaleAt(DipPerPoint * scaling, Scale * scaling);
        }
    }

    /// <summary>The NoZoom scale the view uses (for tests).</summary>
    internal double NoZoomScaleForTests => NoZoomScale;

    /// <summary>The rectangle an element is shown in: for one that keeps its size scaled about the top-left corner of <paramref name="rect"/>.</summary>
    private PdfRectangle Shown(PageElement element, PdfRectangle rect) =>
        KeepsSize(element) && NoZoomScale is var s && s < 1
            ? new PdfRectangle(rect.Left, rect.Top - rect.Height * s, rect.Left + rect.Width * s, rect.Top)
            : rect;

    /// <summary>Page space → page space for drawing an element that keeps its size; null for all others.</summary>
    private PdfMatrix? KeepSizeMatrix(PageElement element) =>
        KeepsSize(element) && NoZoomScale is var s && s < 1 ? new Core.Pdf.Graphics.NoZoomRange(0, 0, element.Rect.Left, element.Rect.Top).Scaled(s) : null;

    /// <summary>Topmost element under a point of the view.</summary>
    private ElementRef? ElementAt(Point viewPoint)
    {
        if (_doc is null || PageAt(viewPoint) is not { } page) return null;
        var elements = _doc.ElementsOn(page);
        // Marks of hidden content lie behind everything: they are taken back only with the „Verborgenes schwärzen“ tool
        for (var i = elements.Count - 1; i >= 0; i--)
            if (elements[i] is not RedactionElement { Hidden: true } && ViewRect(page, Shown(elements[i], elements[i].Rect)).Inflate(2).Contains(viewPoint) &&
                (elements[i] is not ShapeElement { IsLine: true } line || NearLine(page, line, viewPoint)))
                return new ElementRef(page, elements[i]);
        return null;
    }

    /// <summary>Lines and arrows are hit only near the line (otherwise their rectangle is a large empty area over other content).</summary>
    private bool NearLine(int page, ShapeElement line, Point viewPoint)
    {
        var (start, end) = ElementAppearance.ShapeLine(line);
        var toView = ElementAppearance.For(line).UprightToPage() * PageToView(page);
        var a = toView.Transform(start.X, start.Y);
        var b = toView.Transform(end.X, end.Y);
        double dx = b.X - a.X, dy = b.Y - a.Y, lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared > 0 ? Math.Clamp(((viewPoint.X - a.X) * dx + (viewPoint.Y - a.Y) * dy) / lengthSquared, 0, 1) : 0;
        double px = a.X + t * dx - viewPoint.X, py = a.Y + t * dy - viewPoint.Y;
        var reach = Math.Max(6, (ElementAppearance.ArrowHead(line).HalfWidth * (line.Kind == ShapeKind.Arrow && t > 0.8 ? 1 : 0) + line.StrokeWidth / 2) * Scale + 3);
        return px * px + py * py <= reach * reach;
    }

    /// <summary>Handles clockwise from the top left: corners and edge midpoints.</summary>
    private static Point[] Handles(Rect r) =>
    [
        r.TopLeft, new(r.Center.X, r.Top), r.TopRight, new(r.Right, r.Center.Y),
        r.BottomRight, new(r.Center.X, r.Bottom), r.BottomLeft, new(r.Left, r.Center.Y),
    ];

    /// <summary>
    /// The handle under the point. Inside the element a handle reaches only a border of a quarter of the element: with the full reach
    /// the handles of a small element cover all of it, and it could only be reshaped, never moved
    /// (docs/FEHLERGEDAECHTNIS.md, No. 192). Outside, the full reach stays, so that small elements can still be reshaped; of the handles
    /// in reach the nearest counts.
    /// </summary>
    private int? HandleAt(Point viewPoint)
    {
        if (_doc?.SelectedElement is not { } selected || selected.Page >= _pageRects.Count || !_shown[selected.Page]) return null;
        if (!Resizable(selected.Element)) return null;
        var rect = ViewRect(selected.Page, selected.Element.Rect);
        var reach = rect.Contains(viewPoint) ? Math.Min(HandleSize, Math.Min(rect.Width, rect.Height) / 4) : HandleSize;
        // the nearest one in reach: on a small element several are (the first in order was taken, a corner or an edge middle beside it)
        var handles = Handles(rect);
        int? nearest = null;
        var best = double.MaxValue;
        for (var i = 0; i < handles.Length; i++)
        {
            double dx = Math.Abs(handles[i].X - viewPoint.X), dy = Math.Abs(handles[i].Y - viewPoint.Y);
            if (dx > reach || dy > reach || dx * dx + dy * dy >= best) continue;
            best = dx * dx + dy * dy;
            nearest = i;
        }
        return nearest;
    }

    /// <summary>Whether the size can be changed. Foreign annotations can only be moved (their appearance and their points match their size).</summary>
    private bool Resizable(PageElement element) =>
        // shown smaller (NoZoom above 100 %): moved only, the handles would not match the rectangle they change (No. 577)
        // a dimension: its points are its measurement — moved only (Ablaufplan 7.6 f)
        element is not DimensionElement && !(KeepsSize(element) && NoZoomScale < 1) && (element is not ForeignAnnotation && !FieldLocked(element) ||
        _doc is { FieldsMovable: true } && DocumentViewModel.IsFormField(element));   // fields: the appearance is redrawn

    /// <summary>
    /// Form fields stay where they are while filling in (otherwise read-only fields could be dragged, and selected ones moved with
    /// the arrow keys or deleted with Del): moving, resizing and deleting only in “Edit form” mode
    /// (also via “Edit fields” in the Home tab; Esc without a selection ends it).
    /// </summary>
    private bool FieldLocked(PageElement element) => DocumentViewModel.IsFormField(element) && _doc is { FieldsMovable: false };

    // ------------------------------------------------------------ Drawing

    /// <summary>A signature field that already holds a digital signature: nothing left to fill in, so no tint (an empty one keeps it).</summary>
    internal static bool IsSignedSignature(ForeignAnnotation widget)
    {
        var field = widget.Original;
        var type = field.GetName(Core.Pdf.Objects.PdfName.Get("FT")) ?? field.GetDictionary(Core.Pdf.Objects.PdfName.Parent)?.GetName(Core.Pdf.Objects.PdfName.Get("FT"));
        var value = field.Get(Core.Pdf.Objects.PdfName.Get("V")) as Core.Pdf.Objects.PdfDictionary
                    ?? field.GetDictionary(Core.Pdf.Objects.PdfName.Parent)?.Get(Core.Pdf.Objects.PdfName.Get("V")) as Core.Pdf.Objects.PdfDictionary;
        return type?.Value == "Sig" && value is not null;
    }

    /// <summary>How strongly what a dragged element has beyond the edge of the page is shown (design decision: pale, not gone).</summary>
    private const double PaleBeyondEdge = 0.3;

    /// <summary>Whether elements are cut at the edge of the page (for tests: the visible area of a page in the view).</summary>
    internal Rect PageEdgeInView(int page) => ToViewRect(_pageRects[page]);

    private void DrawElements(DrawingContext context, Rect view)
    {
        var doc = _doc!;
        // dragging or sizing an element: inserted pictures keep their smooth version instead of computing one per step (No. 672)
        doc.Renderer.Images.Holding = _elementDrag is { Mode: DragMode.Move or DragMode.Resize };
        var overlays = new List<ElementLayer>();
        var screen = new Rect(Bounds.Size);   // ViewRect works in control coordinates, view in content coordinates
        List<Rect>? foreignFields = null;
        List<(Rect Rect, FieldMarkKind Kind, int Turn)>? marks = null;   // triangle for combo boxes, calendar for date fields, symbol in empty code fields
        for (var i = 0; i < _pageRects.Count; i++)
        {
            if (!_shown[i] || !_pageRects[i].Intersects(view)) continue;
            var item = doc.Pages[i].Item;
            var elements = item.Elements;
            if (elements.Count == 0 && _elementDrag?.Page != i) continue;
            var drawn = new List<PageElement>(elements.Count + 1);
            var dragged = new List<PageElement>();   // shown pale beyond the edge while being dragged (No. 622)
            foreach (var element in elements)
            {
                if (doc.EditingElement is { } editing && editing.Page == i && ReferenceEquals(editing.Element, element)) continue;
                if (doc.IsBoundDimension(element)) continue;   // with „Messen“ on a measurement, drawn with its handles (No. 605)
                if (doc.FillingField is { } filling && filling.Page == i && ReferenceEquals(filling.Element, element)) continue;   // shown in the input box
                var shown = _elementDrag is { Original: { } original, Preview: { } preview } && original.Page == i && ReferenceEquals(original.Element, element)
                    ? preview
                    : _elementDrag is { Previews: { } previews } && previews.TryGetValue(element, out var other) ? other : element;
                // Form fields with a light blue background (only in the view), as in other programs. Fields of other programs
                // often have a white background, so for them the tint goes on top. Only the visible ones (pages with thousands).
                // The eyedropper picks up the page without tint and marks (those exist only on screen).
                if (!_sampling && shown is TextElement { IsField: true } or SignatureFieldElement or FieldElement && ViewRect(i, shown.Rect) is var own && own.Intersects(screen))
                    context.FillRectangle(FieldBrush, own);
                else if (!_sampling && shown is ForeignAnnotation { Kind: "Widget" } widget && !IsSignedSignature(widget) && ViewRect(i, shown.Rect) is var field && field.Intersects(screen))
                    (foreignFields ??= []).Add(field);
                if (!_sampling && DocumentViewModel.IsFormField(shown) && FieldMark(doc, shown) is { } kind && ViewRect(i, shown.Rect) is var marked && marked.Intersects(screen))
                    (marks ??= []).Add((marked, kind, ((DisplayRotation(i) - MarkRotation(shown)) % 360 + 360) % 360));
                if (ReferenceEquals(shown, element) && item.InTiles(element)) continue;   // drawn by the tiles (page with many foreign annotations)
                drawn.Add(shown);
                if (!ReferenceEquals(shown, element)) dragged.Add(shown);
            }
            // Dragging a shape: show the shape itself (on top).
            if (_elementDrag is { Mode: DragMode.Create, Moved: true } creating && creating.Page == i && doc.Tool == ViewTool.InsertShape)
            {
                var shape = DraggedShape(creating);
                drawn.Add(shape);
                dragged.Add(shape);
            }
            if (_elementDrag is { Mode: DragMode.Create, Moved: true } placing && placing.Page == i && doc.Tool == ViewTool.InsertCode)
            {
                var code = DraggedCode(placing);
                drawn.Add(code);
                dragged.Add(code);
            }
            // the visible area of the page: elements are cut there as readers show them; what is dragged shows pale beyond it (No. 622)
            var edge = ToViewRect(_pageRects[i]);
            var toView = PageToView(i);
            foreach (var moving in dragged)
                overlays.Add(new ElementLayer(doc.Pages[i].Page, [moving], (KeepSizeMatrix(moving) ?? PdfMatrix.Identity) * toView, edge, Outside: true, Opacity: PaleBeyondEdge));
            // elements that keep their size get a layer of their own, scaled about their anchor; the order stays (No. 577)
            var plain = new List<PageElement>(drawn.Count);
            foreach (var element in drawn)
            {
                if (KeepSizeMatrix(element) is not { } keep)
                {
                    plain.Add(element);
                    continue;
                }
                if (plain.Count > 0) overlays.Add(new ElementLayer(doc.Pages[i].Page, plain, toView, edge));
                plain = [];
                overlays.Add(new ElementLayer(doc.Pages[i].Page, [element], keep * toView, edge));
            }
            if (plain.Count > 0) overlays.Add(new ElementLayer(doc.Pages[i].Page, plain, toView, edge));
        }
        if (overlays.Count > 0) context.Custom(new ElementDrawing(new Rect(Bounds.Size), overlays, doc.Renderer.Images, foreignLists: doc.Renderer.TryGetElementList,
            overprintPreview: doc.Renderer.OverprintPreview));
        if (foreignFields is not null)
            foreach (var field in foreignFields) context.FillRectangle(FieldBrush, field);
        if (marks is not null)
            foreach (var (rect, kind, turn) in marks) DrawFieldMark(context, rect, kind, turn);
    }

    /// <summary>Draws the frame and handles of the selected element, and a dashed frame while dragging a new one.</summary>
    private void DrawElementFrames(DrawingContext context)
    {
        if (_doc is null) return;
        DrawDesignFrames(context);
        if (_elementDrag is { Mode: DragMode.Create or DragMode.Select } create)
        {
            // Line and arrow: nothing more (the line itself is visible); otherwise the frame.
            if (create.Mode != DragMode.Create || _doc.Tool != ViewTool.InsertShape || !ElementDefaults.Shape.IsLine || !create.Moved)
                context.DrawRectangle(null, FramePen, ToViewRect(create.Current));
            return;
        }
        if (_doc.SelectedElement is not { } selected || selected.Page >= _pageRects.Count || !_shown[selected.Page]) return;
        var rect = ViewRect(selected.Page, Shown(selected.Element,
            _elementDrag is { Original: not null } drag ? PageRect(selected.Page, ToViewRect(drag.Current)) : selected.Element.Rect));
        if (_doc is { IsFieldDesign: true } && _doc.FieldSelection.Count > 1) return;   // multiple selection: drawn by DrawDesignFrames
        context.DrawRectangle(null, FramePen, rect);
        if (!Resizable(selected.Element)) return;
        foreach (var h in Handles(rect))
            context.DrawRectangle(Brushes.White, HandlePen, new Rect(h.X - HandleSize / 2, h.Y - HandleSize / 2, HandleSize, HandleSize));
    }

    // ------------------------------------------------------------ Mouse

    /// <summary>Left button: drag a new element, or grab a handle or an element. true = handled.</summary>
    private bool ElementPressed(PointerPressedEventArgs e, Point position)
    {
        if (_doc is not { } doc) return false;
        if (doc.Tool == ViewTool.RedactHidden) return false;   // only the frames of hidden spots react, elsewhere the view pans
        if (doc.Tool == ViewTool.InsertRedaction) return RedactionPressed(e, position, doc);
        // “Redact text” and “Pan”: grab red marks, otherwise text selection or panning the view.
        if (doc.Tool is ViewTool.Pan && NoteAt(position, doc)) return true;
        if (doc.Tool is ViewTool.RedactText or ViewTool.Pan) return RedactionGrabbed(e, position, doc);
        // Insert field: on an existing field, select it (Ctrl: add) and move it; only empty space places a new one.
        if (doc.Tool == ViewTool.DesignForm || doc.Tool == ViewTool.InsertFormField && FieldAt(position) is not null) return DesignPressed(e, position, doc);
        if (doc.Tool is ViewTool.InsertText or ViewTool.InsertField or ViewTool.InsertSignature or ViewTool.InsertImage or ViewTool.InsertShape or ViewTool.InsertCode or ViewTool.InsertNote or ViewTool.InsertSignatureField or ViewTool.InsertFormField)
        {
            if (PageAt(position) is not { } page) return true;
            if (!doc.CanEditElements)
            {
                doc.Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
                return true;
            }
            var start = ToDocument(position);
            _elementDrag = new ElementDrag { Mode = DragMode.Create, Page = page, Start = start, Current = new Rect(start, start) };
            doc.SelectedElement = null;
            e.Pointer.Capture(this);
            return true;
        }

        // Second click of a double-click whose first click has just placed something: only select (do not start editing).
        var justPlaced = _justPlaced && e.ClickCount >= 2;
        _justPlaced = false;

        if (HandleAt(position) is { } handle && doc.SelectedElement is { } selected && doc.CanEditElements)
        {
            BeginDrag(DragMode.Resize, selected, position, handle);
            e.Pointer.Capture(this);
            return true;
        }
        if (ElementAt(position) is { } hit)
        {
            // Label of a box: a click ticks the box (on release), as in other programs.
            if (DocumentViewModel.IsFieldLabel(hit.Element) && doc.FieldOfLabel(hit) is { } box &&
                DocumentViewModel.IsFillable(box.Element) && doc.CanFillForms)
            {
                doc.SelectedElement = box;
                _fieldClick = box;
                e.Pointer.Capture(this);
                return true;
            }
            // Edited text (see KONZEPT.md, section 5.14): a double-click places the caret at that spot.
            if (e.ClickCount >= 2 && !justPlaced && hit.Element is RichTextElement && doc.CanEditElements)
            {
                _elementDrag = null;
                PlaceCaret(doc.BeginRichEdit(hit, 0), position);
                return true;
            }
            if (e.ClickCount >= 2 && !justPlaced && hit.Element is TextElement && doc.CanEditElements)
            {
                _elementDrag = null;
                doc.BeginEdit(hit, isNew: false);
                return true;
            }
            if (e.ClickCount == 2 && !justPlaced && hit.Element is SignatureElement or InkElement or ImageElement && doc.CanEditElements)
            {
                _elementDrag = null;
                doc.SelectedElement = hit;
                doc.RequestSignatureEdit(hit);
                return true;
            }
            // “Reset” button (without a script): a click resets the form.
            if (doc.SpecOf(hit.Element) is { Kind: FormFieldKind.PushButton, ResetForm: true })
            {
                doc.ResetForm(hit.Element);
                return true;
            }
            doc.SelectedElement = hit;
            // Form field: a click fills it in, on release; it is moved only in “Edit form” mode.
            if (DocumentViewModel.IsFillable(hit.Element))
            {
                if (doc.CanFillForms)
                {
                    _fieldClick = hit;
                    e.Pointer.Capture(this);
                }
                return true;
            }
            if (doc.CanEditElements && !FieldLocked(hit.Element))
            {
                BeginDrag(DragMode.Move, hit, position, 0);
                e.Pointer.Capture(this);
            }
            return true;
        }
        if (doc.SelectedElement is not null) doc.SelectedElement = null;
        return doc.Tool == ViewTool.SelectText && NoteAt(position, doc);
    }

    /// <summary>A note symbol at this position shows its note in the area „Notizen“.</summary>
    private bool NoteAt(Point position, DocumentViewModel doc)
    {
        if (PageAt(position) is not { } page) return false;
        var (x, y) = PageToView(page).Invert().Transform(position.X, position.Y);
        // above 100 % a note with NoZoom is drawn smaller, at the top left of /Rect (No. 576)
        return doc.ChooseNoteAt(page, x, y, NoZoomScale);
    }

    /// <summary>
    /// “Redact” tool (stays on until switched off, as in common PDF editors): reshape an existing mark at its handles
    /// or grab and move it; on text a text selection begins, which becomes a mark on release
    /// (double-click: word, triple-click: line); otherwise (empty space, images, graphics) drag an area.
    /// false = the text selection takes over.
    /// </summary>
    private bool RedactionPressed(PointerPressedEventArgs e, Point position, DocumentViewModel doc)
    {
        if (PageAt(position) is not { } page) return true;
        if (!doc.CanEditElements)
        {
            doc.Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
            return true;
        }
        if (RedactionGrabbed(e, position, doc)) return true;
        if (TextAt(position) is not null) return false;
        var start = ToDocument(position);
        _elementDrag = new ElementDrag { Mode = DragMode.Create, Page = page, Start = start, Current = new Rect(start, start) };
        e.Pointer.Capture(this);
        return true;
    }

    /// <summary>
    /// Reshapes a red mark at its handles or grabs and moves it (redaction tools and pan); other elements
    /// do not count here. false = nothing grabbed, and any selection has been cleared.
    /// </summary>
    private bool RedactionGrabbed(PointerPressedEventArgs e, Point position, DocumentViewModel doc)
    {
        if (doc.CanEditElements)
        {
            if (HandleAt(position) is { } handle && doc.SelectedElement is { Element: RedactionElement } selected)
            {
                BeginDrag(DragMode.Resize, selected, position, handle);
                e.Pointer.Capture(this);
                return true;
            }
            if (RedactionAt(position) is { } mark)
            {
                doc.SelectedElement = mark;   // its panel with the reason once released without moving it (MarkPicked)
                BeginDrag(DragMode.Move, mark, position, 0);
                e.Pointer.Capture(this);
                return true;
            }
        }
        if (doc is { MarkPicked: true, SelectedElement: { } picked })
        {
            // the picked mark's panel goes once the press has set up what follows (a new bar, a text selection, panning): then it waits
            // for the release — gone at once, it widened the page under the pointer before the bar was started
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (Equals(doc.SelectedElement, picked)) doc.SelectedElement = null;
            });
        }
        else if (doc.SelectedElement is not null) doc.SelectedElement = null;
        return false;
    }

    /// <summary>Topmost redaction mark at the spot (other elements do not count in the redaction tools).</summary>
    private ElementRef? RedactionAt(Point viewPoint)
    {
        if (_doc is null || PageAt(viewPoint) is not { } page) return null;
        var elements = _doc.ElementsOn(page);
        for (var i = elements.Count - 1; i >= 0; i--)
            if (elements[i] is RedactionElement { Hidden: false } && ViewRect(page, elements[i].Rect).Inflate(2).Contains(viewPoint)) return new ElementRef(page, elements[i]);
        return null;
    }

    // The last click placed an element (see ElementPressed: double-click while placing).
    private bool _justPlaced;

    // Form field under the pressed button: fill it in on release.
    private ElementRef? _fieldClick;

    /// <summary>
    /// Mouse lost (Alt+Tab, dialog, another window) in the middle of dragging: cancel; otherwise the element would
    /// follow the mouse afterwards without a button pressed.
    /// </summary>
    private void ElementCaptureLost()
    {
        _fieldClick = null;
        if (_elementDrag is null) return;
        _elementDrag = null;
        InvalidateVisual();
    }

    private void BeginDrag(DragMode mode, ElementRef element, Point position, int handle)
    {
        var rect = ViewRect(element.Page, element.Element.Rect).Translate(_offset);
        _elementDrag = new ElementDrag
        {
            Mode = mode, Page = element.Page, Original = element, Start = ToDocument(position), StartRect = rect, Current = rect, Handle = handle,
        };
    }

    private bool ElementMoved(Point position, bool shift = false)
    {
        if (_elementDrag is not { } drag || _doc is null) return false;
        var point = ToDocument(position);
        var delta = point - drag.Start;
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 3) drag.Moved = true;
        if (!drag.Moved) return true;
        var min = drag.Original?.Element switch
        {
            RedactionElement => MinRedactionView,
            ShapeElement => MinShapePoints * Scale,   // small shapes stay small when resized
            _ => MinElementPoints * Scale,
        };
        switch (drag.Mode)
        {
            case DragMode.Create when _doc.Tool == ViewTool.InsertCode && ElementDefaults.Code.IsMatrix:
            {
                // QR and Data Matrix symbols are square: the larger extent decides
                double dx = point.X - drag.Start.X, dy = point.Y - drag.Start.Y;
                var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
                point = new Point(drag.Start.X + Math.CopySign(side, dx), drag.Start.Y + Math.CopySign(side, dy));
                drag.End = point;
                drag.Current = new Rect(drag.Start, point).Normalize().Intersect(_pageRects[drag.Page]);
                break;
            }
            case DragMode.Create when _doc.Tool == ViewTool.InsertShape:
                // Shape: with Shift a square or circle, lines in 45° steps.
                if (shift)
                {
                    double dx = point.X - drag.Start.X, dy = point.Y - drag.Start.Y;
                    if (ElementDefaults.Shape.IsLine)
                    {
                        var angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
                        var length = Math.Sqrt(dx * dx + dy * dy);
                        point = new Point(drag.Start.X + Math.Cos(angle) * length, drag.Start.Y + Math.Sin(angle) * length);
                    }
                    else
                    {
                        var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        point = new Point(drag.Start.X + Math.CopySign(side, dx), drag.Start.Y + Math.CopySign(side, dy));
                    }
                }
                drag.End = point;
                drag.Current = new Rect(drag.Start, point).Normalize().Intersect(_pageRects[drag.Page]);
                break;
            case DragMode.Create or DragMode.Select:
                // Normalize: dragged to the left or upwards, the rectangle would have a negative width and the intersection would be empty.
                drag.Current = new Rect(drag.Start, point).Normalize().Intersect(_pageRects[drag.Page]);
                break;
            case DragMode.Move:
                // Label of a box: stays level with it and moves only in its reading direction.
                if (drag.Original is { Element: var dragged } && DocumentViewModel.IsFieldLabel(dragged))
                    delta = ((DisplayRotation(drag.Page) - dragged.Rotation) % 180 + 180) % 180 == 0 ? new Point(delta.X, 0) : new Point(0, delta.Y);
                drag.Current = drag.StartRect.Translate(delta);
                break;
            case DragMode.Resize:
                var r = drag.StartRect;
                double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
                // Handles 0–2 top, 2–4 right, 4–6 bottom, 6/7/0 left
                if (drag.Handle is 0 or 1 or 2) top = Math.Min(r.Top + delta.Y, bottom - min);
                if (drag.Handle is 4 or 5 or 6) bottom = Math.Max(r.Bottom + delta.Y, top + min);
                if (drag.Handle is 0 or 6 or 7) left = Math.Min(r.Left + delta.X, right - min);
                if (drag.Handle is 2 or 3 or 4) right = Math.Max(r.Right + delta.X, left + min);
                // Keep the aspect ratio (see KONZEPT.md, section 5.13): images with the chain closed, Shift reverses it; shapes with Shift.
                var keep = drag.Original?.Element switch
                {
                    ImageElement image => image.KeepAspect != shift,
                    ShapeElement => shift,
                    CodeElement code => code.IsMatrix || shift,   // a 2D symbol stays square (it would only get a wider margin)
                    _ => false,
                };
                if (keep && r.Width > 0 && r.Height > 0)
                    (left, top, right, bottom) = KeepAspect(r, drag.Handle, left, top, right, bottom);
                drag.Current = new Rect(new Point(left, top), new Point(right, bottom));
                break;
        }
        if (drag.Original is { } original)
            drag.Preview = drag.Mode == DragMode.Move
                ? ElementAppearance.Moved(original.Element, PageRect(drag.Page, ToViewRect(drag.Current)))
                : original.Element with { Rect = PageRect(drag.Page, ToViewRect(drag.Current)) };
        // Several fields selected: all of them move along.
        if (drag is { Mode: DragMode.Move, Original: { } first, Preview: { } moved } && _doc.IsFieldDesign && _doc.FieldSelection.Count > 1)
        {
            double dx = moved.Rect.Left - first.Element.Rect.Left, dy = moved.Rect.Bottom - first.Element.Rect.Bottom;
            drag.Previews = new Dictionary<PageElement, PageElement>(ReferenceEqualityComparer.Instance);
            foreach (var other in _doc.FieldSelection)
                if (!ReferenceEquals(other.Element, first.Element) && other.Element.Rect is var r)
                    drag.Previews[other.Element] = ElementAppearance.Moved(other.Element, new PdfRectangle(r.Left + dx, r.Bottom + dy, r.Right + dx, r.Top + dy));
        }
        // Labels of the dragged boxes move along (preview).
        if (drag is { Mode: DragMode.Move, Original: { } dragOriginal, Preview: { } dragPreview } && _doc.IsFieldDesign && !DocumentViewModel.IsFieldLabel(dragOriginal.Element))
        {
            double lx = dragPreview.Rect.Left - dragOriginal.Element.Rect.Left, ly = dragPreview.Rect.Bottom - dragOriginal.Element.Rect.Bottom;
            drag.Previews ??= new Dictionary<PageElement, PageElement>(ReferenceEqualityComparer.Instance);
            foreach (var moving in _doc.FieldSelection)
                foreach (var label in _doc.LabelsOf(moving))
                    if (label.Element.Rect is var lr && !_doc.FieldSelection.Any(s => ReferenceEquals(s.Element, label.Element)))
                        drag.Previews[label.Element] = ElementAppearance.Moved(label.Element, new PdfRectangle(lr.Left + lx, lr.Bottom + ly, lr.Right + lx, lr.Top + ly));
        }
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// Rectangle while dragging handle <paramref name="handle"/>, in the aspect ratio of <paramref name="start"/>: at the corners the
    /// direction dragged further determines the size (the opposite corner stays), at the edges the other direction grows along, centred.
    /// </summary>
    private static (double Left, double Top, double Right, double Bottom) KeepAspect(Rect start, int handle, double left, double top, double right, double bottom)
    {
        var aspect = start.Width / start.Height;
        double width = right - left, height = bottom - top;
        switch (handle)
        {
            case 1 or 5:   // top, bottom: width follows, centred
                width = height * aspect;
                left = start.Center.X - width / 2;
                right = left + width;
                break;
            case 3 or 7:   // right, left: height follows, centred
                height = width / aspect;
                top = start.Center.Y - height / 2;
                bottom = top + height;
                break;
            default:       // corners: the larger change counts, the opposite corner stays
                var scale = Math.Max(width / start.Width, height / start.Height);
                (width, height) = (start.Width * scale, start.Height * scale);
                if (handle is 0 or 6) left = right - width;
                else right = left + width;
                if (handle is 0 or 2) top = bottom - height;
                else bottom = top + height;
                break;
        }
        return (left, top, right, bottom);
    }

    private bool ElementReleased(PointerReleasedEventArgs e, Point position)
    {
        if (_fieldClick is { } field)
        {
            // Click on a form field: fill it in now.
            _fieldClick = null;
            e.Pointer.Capture(null);
            ActivateField(field);
            return true;
        }
        if (_elementDrag is not { } drag || _doc is not { } doc) return false;
        // Up to the release position (the last move event can lie before it when dragging fast).
        ElementMoved(position, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        _elementDrag = null;
        e.Pointer.Capture(null);
        switch (drag.Mode)
        {
            case DragMode.Create:
                CreateElement(doc, drag);
                _justPlaced = true;
                break;
            case DragMode.Select:
                SelectInFrame(doc, drag);
                break;
            case DragMode.Move when drag.Moved && doc.IsFieldDesign:
                MoveFieldSelection(doc, drag);
                break;
            case DragMode.Move when !drag.Moved && drag.Original is { Element: RedactionElement }
                                    && doc.Tool is ViewTool.RedactText or ViewTool.InsertRedaction or ViewTool.Pan:
                // a mark clicked, not dragged: now its panel with the reason (wish of 4.11.2026) — only on release, as appearing on the
                // press it narrowed the page and moved what was being dragged
                doc.MarkPicked = true;
                break;
            case DragMode.Resize when drag is { Moved: true, Original: { } resized, Preview: { } sized } && doc.IsFieldDesign:
                doc.ResizeField(resized, sized.Rect);
                break;
            case DragMode.Resize when drag is { Original: { } same, Preview: { } unchanged } && unchanged.Rect == same.Element.Rect:
                break;   // the size stays (a handle dragged along its own edge): no undo step
            case DragMode.Move or DragMode.Resize when drag is { Moved: true, Original: { } original, Preview: { } preview }:
                // Formatted text: the height follows the text in the new width (see KONZEPT.md, section 5.14).
                if (drag.Mode == DragMode.Resize && preview is RichTextElement rich) preview = RichLayout.Fitted(rich);
                doc.ReplaceElement(original, preview, drag.Mode == DragMode.Move ? Tr.T("Element verschieben") : Tr.T("Elementgröße ändern"));
                break;
        }
        InvalidateVisual();
        return true;
    }

    /// <summary>Creates a new element from the dragged rectangle (or, on a click, in the default size) according to the tool; a text element opens in the input box at once.</summary>
    private void CreateElement(DocumentViewModel doc, ElementDrag drag)
    {
        if (doc.Tool == ViewTool.InsertFormField)
        {
            CreateFormField(doc, drag);   // the tool stays on
            return;
        }
        if (doc.Tool is ViewTool.InsertSignature or ViewTool.InsertImage)
        {
            CreateSignature(doc, drag);
            return;
        }
        if (doc.Tool == ViewTool.InsertSignatureField)
        {
            CreateSignatureField(doc, drag);
            return;
        }
        if (doc.Tool == ViewTool.InsertShape)
        {
            CreateShape(doc, drag);
            return;
        }
        if (doc.Tool == ViewTool.InsertCode)
        {
            doc.Tool = ViewTool.SelectText;
            doc.AddElement(drag.Page, DraggedCode(drag), Tr.T("Code einfügen"));
            return;
        }
        if (doc.Tool == ViewTool.InsertNote)
        {
            // a click: the tip of the bubble at that point; back to selecting, the text goes in the area on the right
            doc.Tool = ViewTool.SelectText;
            var (x, y) = ToPage(drag.Page, drag.Start - _offset);
            doc.AddNote(drag.Page, x, y);
            return;
        }
        if (doc.Tool == ViewTool.InsertRedaction)
        {
            // Only dragged areas (a click redacts nothing); the tool stays on for further areas.
            if (!drag.Moved || drag.Current.Width < MinRedactionView || drag.Current.Height < MinRedactionView) return;
            doc.AddElement(drag.Page, new RedactionElement { Rect = PageRect(drag.Page, ToViewRect(drag.Current)) }, Tr.T("Bereich schwärzen"));
            return;
        }
        var template = ElementDefaults.Text with { Text = "", IsField = doc.Tool == ViewTool.InsertField, Rotation = DisplayRotation(drag.Page), FieldName = "" };
        var scale = Scale;
        var box = drag.Current;
        if (!drag.Moved || box.Width < MinElementPoints * scale || box.Height < MinElementPoints * scale)
        {
            // Click: one line, 200 points wide, with the click position at the left edge, vertically centred.
            var height = ElementAppearance.NeededHeight(template) * scale;
            box = new Rect(drag.Start.X, drag.Start.Y - height / 2, 200 * scale, height);
        }
        var element = template with { Rect = PageRect(drag.Page, ToViewRect(box)) };
        doc.Tool = ViewTool.SelectText;   // back to selecting after placing
        doc.BeginEdit(new ElementRef(drag.Page, element), isNew: true);
    }

    /// <summary>
    /// Places a signature or image: in the dragged rectangle, or on a click in a suitable size (signature 180 points
    /// wide and at most 90 high, image 200 × at most 200) centred on the click position. Afterwards selected, tool reset.
    /// </summary>
    private void CreateSignature(DocumentViewModel doc, ElementDrag drag)
    {
        var isImage = doc.Tool == ViewTool.InsertImage;
        doc.Tool = ViewTool.SelectText;
        if ((isImage ? ElementDefaults.Image : ElementDefaults.Signature) is not { } template) return;
        var rotation = DisplayRotation(drag.Page);
        // Click into a signature field: the signature goes exactly inside it (a field created in PDF Knead is replaced).
        if (!isImage && !drag.Moved)
        {
            var (x, y) = ToPage(drag.Page, drag.Start - _offset);
            if (doc.SignatureFieldAt(drag.Page, x, y) is { } field)
            {
                // A digital signature into an empty field of the file fills that field when signing
                var signed = template with
                {
                    Rect = field.Rect, Rotation = rotation,
                    Digital = template.Digital is { } digital ? digital with { FieldName = field.FieldName } : null,
                };
                // A digital signature into an own field that the file already has under a name: the field stays and is filled when
                // signing (in a signed document that is appended); otherwise the own field becomes the signature
                if (field.Own is { FieldName.Length: > 0 } named && signed.Digital is { } own)
                    doc.AddElement(drag.Page, signed with { Digital = own with { FieldName = named.FieldName } }, Tr.T("Unterschrift einfügen"));
                else if (field.Own is { } replaced) doc.SignInto(new ElementRef(drag.Page, replaced), signed);
                else doc.AddElement(drag.Page, signed, Tr.T("Unterschrift einfügen"));
                // a prepared field of the file: what it locks and requires, said where it is chosen (the signature window opened before)
                if (field.Own is null && signed.Digital is { FieldName: { Length: > 0 } prepared } digitalInto
                    && doc.PreparedFieldNotice(prepared, digitalInto.Certify) is { } notice)
                    doc.Notify(notice);
                return;
            }
        }
        var scale = Scale;
        var box = drag.Current;
        if (!drag.Moved || box.Width < MinElementPoints * scale || box.Height < MinElementPoints * scale)
        {
            // Signature 180 × at most 90 points, image 200 × at most 200
            var aspect = ElementAppearance.Aspect(template);
            double width = isImage ? 200 : 180, maxHeight = isImage ? 200 : 90, height = width / aspect;
            if (height > maxHeight) (height, width) = (maxHeight, maxHeight * aspect);
            height = ElementAppearance.HeightFor(template, height);   // with padding and origin line
            width += 2 * ElementAppearance.Padding;
            box = new Rect(drag.Start.X - width * scale / 2, drag.Start.Y - height * scale / 2, width * scale, height * scale);
        }
        else if (template is ImageElement { KeepAspect: true } picture)
        {
            // Chain closed (see KONZEPT.md, section 5.13): fit the dragged rectangle to the image's aspect ratio, centred within it
            // (the element is upright in the view however it is rotated, so the image's aspect ratio applies there).
            var aspect = (double)picture.Image.Width / Math.Max(1, picture.Image.Height);
            var (fw, fh) = box.Width / box.Height > aspect ? (box.Height * aspect, box.Height) : (box.Width, box.Width / aspect);
            box = new Rect(box.Center.X - fw / 2, box.Center.Y - fh / 2, fw, fh);
        }
        var element = template with { Rect = PageRect(drag.Page, ToViewRect(box)), Rotation = rotation };
        doc.AddElement(drag.Page, element, isImage ? Tr.T("Bild einfügen") : Tr.T("Unterschrift einfügen"));
    }

    /// <summary>
    /// Shape (see KONZEPT.md, section 5.13): dragged, or on a click in the default size around the click position. Line and arrow from the
    /// start to the end of the mouse movement; their rectangle has a margin for the stroke and arrowhead. Afterwards selected, tool reset.
    /// </summary>
    private void CreateShape(DocumentViewModel doc, ElementDrag drag)
    {
        doc.Tool = ViewTool.SelectText;
        doc.AddElement(drag.Page, DraggedShape(drag), Tr.T("Form einfügen"));
    }

    /// <summary>The dragged shape (also used as the preview while dragging, so the result is visible beforehand).</summary>
    private ShapeElement DraggedShape(ElementDrag drag)
    {
        var template = ElementDefaults.Shape with { Rotation = DisplayRotation(drag.Page) };
        var scale = Scale;
        Point start = drag.Start, end = drag.End;
        // Only a click (mouse hardly moved) sets the default size; when dragged, the dragged size applies, even if very small
        // (treating a shape under 8 points in one direction as a click would make small shapes impossible to draw).
        var click = !drag.Moved;
        ShapeElement shape;
        if (template.IsLine)
        {
            if (click) (start, end) = (new Point(drag.Start.X - 60 * scale, drag.Start.Y), new Point(drag.Start.X + 60 * scale, drag.Start.Y));
            // Frame around both ends with room for the stroke and arrowhead; the ends relative to it (in reading direction = as displayed).
            var margin = (Math.Max(ElementAppearance.ArrowHead(template).HalfWidth, template.StrokeWidth) + 2) * scale;
            var box = new Rect(start, end).Normalize().Inflate(margin);
            shape = template with
            {
                Rect = PageRect(drag.Page, ToViewRect(box)),
                Start = ((start.X - box.Left) / box.Width, (box.Bottom - start.Y) / box.Height),
                End = ((end.X - box.Left) / box.Width, (box.Bottom - end.Y) / box.Height),
            };
        }
        else
        {
            var box = drag.Current;
            if (click)
            {
                // Click: 100 points, wider shapes 140 × 90
                var (w, h) = template.Kind is ShapeKind.Rectangle or ShapeKind.Ellipse or ShapeKind.Callout ? (140.0, 90.0) : (100.0, 100.0);
                box = new Rect(drag.Start.X - w * scale / 2, drag.Start.Y - h * scale / 2, w * scale, h * scale);
            }
            else
            {
                // Dragged horizontally or vertically: at least 2 points, so that the shape stays visible and can be grabbed.
                box = new Rect(box.X, box.Y, Math.Max(box.Width, MinShapePoints * scale), Math.Max(box.Height, MinShapePoints * scale));
            }
            shape = template with { Rect = PageRect(drag.Page, ToViewRect(box)) };
        }
        return shape;
    }

    /// <summary>
    /// Code (see KONZEPT.md, section 5.19): in the dragged rectangle (square for QR and Data Matrix), or on a click in its default size —
    /// modules of 0.5 mm, bar codes at their nominal size — around the click position. Also the preview while dragging.
    /// </summary>
    private CodeElement DraggedCode(ElementDrag drag)
    {
        var template = ElementDefaults.Code with { Rotation = DisplayRotation(drag.Page) };
        var scale = Scale;
        var box = drag.Current;
        if (!drag.Moved || box.Width < MinElementPoints * scale || box.Height < MinElementPoints * scale)
        {
            var (w, h) = ElementAppearance.DefaultCodeSize(template);
            box = new Rect(drag.Start.X - w * scale / 2, drag.Start.Y - h * scale / 2, w * scale, h * scale);
        }
        return template with { Rect = PageRect(drag.Page, ToViewRect(box)) };
    }

    /// <summary>Empty signature field: dragged, or on a click 180 × 50 points around the click position.</summary>
    private void CreateSignatureField(DocumentViewModel doc, ElementDrag drag)
    {
        doc.Tool = ViewTool.SelectText;
        var scale = Scale;
        var box = drag.Current;
        if (!drag.Moved || box.Width < MinElementPoints * scale || box.Height < MinElementPoints * scale)
            box = new Rect(drag.Start.X - 90 * scale, drag.Start.Y - 25 * scale, 180 * scale, 50 * scale);
        var field = new SignatureFieldElement { Rect = PageRect(drag.Page, ToViewRect(box)), Rotation = DisplayRotation(drag.Page), Label = Tr.T("Unterschrift") };
        doc.AddElement(drag.Page, field, Tr.T("Unterschriftsfeld einfügen"));
    }

    /// <summary>Mouse pointer over handles and elements; null = the usual one.</summary>
    private Cursor? ElementCursor(Point position)
    {
        if (_doc is null) return null;
        if (_doc.Tool == ViewTool.InsertRedaction)
        {
            if (PageAt(position) is null) return null;
            if (HandleAt(position) is { } h && _doc.SelectedElement is { Element: RedactionElement }) return HandleCursors[h];
            if (RedactionAt(position) is not null) return MoveCursor;
            return TextAt(position) is not null ? TextCursor : CrossCursor;
        }
        if (_doc.Tool == ViewTool.InsertFormField && FieldAt(position) is not null) return MoveCursor;   // existing field: select and drag
        if (_doc.Tool is ViewTool.InsertText or ViewTool.InsertField or ViewTool.InsertSignature or ViewTool.InsertImage or ViewTool.InsertShape or ViewTool.InsertCode or ViewTool.InsertNote or ViewTool.InsertSignatureField or ViewTool.InsertFormField) return PageAt(position) is null ? null : CrossCursor;
        if (_doc.Tool is ViewTool.RedactText or ViewTool.Pan)
        {
            if (HandleAt(position) is { } h && _doc.SelectedElement is { Element: RedactionElement }) return HandleCursors[h];
            return RedactionAt(position) is not null ? MoveCursor : null;
        }
        if (_doc.Tool == ViewTool.DesignForm)
        {
            if (_doc.FieldSelection.Count == 1 && HandleAt(position) is { } h) return HandleCursors[h];
            return FieldAt(position) is not null ? MoveCursor : null;
        }
        if (HandleAt(position) is { } handle) return HandleCursors[handle];
        // Form field to fill in: text cursor or hand, as in other readers.
        if (ElementAt(position) is { } labelHit && DocumentViewModel.IsFieldLabel(labelHit.Element) && _doc.FieldOfLabel(labelHit) is not null)
            return HandCursor;
        if (ElementAt(position) is { } hit && DocumentViewModel.IsFillable(hit.Element))
            return _doc.SpecOf(hit.Element) is { Kind: FormFieldKind.Text } ? TextCursor : HandCursor;
        return ElementAt(position) is { } any && !FieldLocked(any.Element) ? MoveCursor : null;
    }

    // ------------------------------------------------------------ Right-click

    /// <summary>Right-click on an element: selects it and shows a menu with the layer commands (and delete). true = handled.</summary>
    private bool ElementMenu(Point position)
    {
        if (_doc is not { } doc || doc.EditingElement is not null) return false;
        var design = doc.IsFormDesign || doc.Tool == ViewTool.Edit && FieldAt(position) is not null;
        if ((design ? FieldAt(position) : ElementAt(position)) is not { } hit) return false;
        if (design)
        {
            if (!doc.FieldSelection.Any(r => ReferenceEquals(r.Element, hit.Element))) doc.SelectFields([hit]);
        }
        else
        {
            doc.SelectedElement = hit;
        }
        if (!doc.CanMoveLayer) return true;   // fillable field while filling in: only select
        // Shortcuts as translated text on the right (Avalonia would write “Ctrl+Shift+PageUp”, while the tooltips use the translated key names).
        static Control Header(string text, string keys) => new DockPanel
        {
            MinWidth = 300,   // the shortcuts are right-aligned
            Children =
            {
                new TextBlock { Text = keys, Margin = new Thickness(28, 0, 0, 0), Opacity = 0.6, [DockPanel.DockProperty] = Dock.Right },
                new TextBlock { Text = text },
            },
        };
        MenuItem Item(string header, LayerMove move, string keys, string icon)
        {
            var item = new MenuItem { Header = Header(header, keys) };
            if (this.TryFindResource(icon, out var data) && data is Geometry geometry) item.Icon = new PathIcon { Data = geometry, Width = 16, Height = 16 };
            item.Click += (_, _) => doc.MoveLayer(move);
            return item;
        }
        var items = new List<Control>
        {
            Item(Tr.T("In den Vordergrund"), LayerMove.ToFront, Tr.T("Strg+Umschalt+Bild↑"), "Icon.PositionToFront"),
            Item(Tr.T("Eine Ebene nach vorn"), LayerMove.Forward, Tr.T("Strg+Bild↑"), "Icon.PositionForward"),
            Item(Tr.T("Eine Ebene nach hinten"), LayerMove.Backward, Tr.T("Strg+Bild↓"), "Icon.PositionBackward"),
            Item(Tr.T("In den Hintergrund"), LayerMove.ToBack, Tr.T("Strg+Umschalt+Bild↓"), "Icon.PositionToBack"),
        };
        if (doc.CanEditElements && (design || !FieldLocked(hit.Element)))
        {
            var delete = new MenuItem { Header = Header(Tr.T("Löschen"), Tr.T("Entf")) };
            delete.Click += (_, _) =>
            {
                if (doc.IsFieldDesign) doc.DeleteSelectedFields();
                else doc.DeleteSelectedElement();
            };
            items.Add(new Separator());
            items.Add(delete);
        }
        var menu = new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer };
        menu.Open(this);
        return true;
    }

    // ------------------------------------------------------------ Keyboard

    /// <summary>Selected element: Del deletes, Enter/F2 edits, arrows move (Shift: further), Esc deselects. true = handled.</summary>
    private bool ElementKey(KeyEventArgs e)
    {
        // Layers (see KONZEPT.md, section 5.13): Ctrl+Page Up/Down one forward or backward, with Shift all the way; “]” would need AltGr on German keyboards.
        if (e.Key is Key.PageUp or Key.PageDown && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _doc is { CanMoveLayer: true } layered)
        {
            var whole = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            layered.MoveLayer(e.Key == Key.PageUp ? whole ? LayerMove.ToFront : LayerMove.Forward : whole ? LayerMove.ToBack : LayerMove.Backward);
            return true;
        }
        if (_doc is { IsFieldDesign: true } design) return DesignKey(e, design);
        if (_doc is not { SelectedElement: { } selected } doc) return false;
        // Arrow keys: 1 point, at high zoom about one screen pixel (for exact placement, e.g. of a redaction); with Shift 10 points.
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : Math.Clamp(1 / Scale, 0.05, 1);
        switch (e.Key)
        {
            case Key.Delete or Key.Back when doc.CanEditElements && !FieldLocked(selected.Element):
                doc.DeleteSelectedElement();
                return true;
            case Key.Enter or Key.F2 when selected.Element is TextElement && doc.CanEditElements:
                doc.BeginEdit(selected, isNew: false);
                return true;
            case Key.Escape:
                doc.SelectedElement = null;
                return true;
            case Key.Left or Key.Right or Key.Up or Key.Down when doc.CanEditElements && !FieldLocked(selected.Element):
                // In points on screen (whether the view is rotated or not).
                var (dx, dy) = e.Key switch { Key.Left => (-step, 0.0), Key.Right => (step, 0.0), Key.Up => (0.0, -step), _ => (0.0, step) };
                var view = ViewRect(selected.Page, selected.Element.Rect).Translate(new Vector(dx * Scale, dy * Scale));
                doc.ReplaceElement(selected, ElementAppearance.Moved(selected.Element, PageRect(selected.Page, view)), Tr.T("Element verschieben"), continueLast: true);
                return true;
        }
        return false;
    }
}
