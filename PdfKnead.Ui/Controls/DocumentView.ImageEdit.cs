using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.ViewModels;

namespace PdfKnead.Controls;

/// <summary>
/// Image editing (see KONZEPT.md, section 5.20; part of „Bearbeiten“, section 5.22): the picture or graphic under the mouse is highlighted
/// (all its parts); a click chooses it (Ctrl adds or takes out), Shift + dragging chooses everything inside a frame (with Ctrl added),
/// dragging moves it, the handles resize it (chain from the panel, Shift reverses it),
/// arrow keys move it by a point (Shift: ten), Del deletes it. The change is applied on release — the page is written anew once, not at every mouse move.
/// </summary>
public sealed partial class DocumentView
{
    private static readonly IBrush ImageHoverBrush = new SolidColorBrush(Color.FromArgb(0x18, 0x3D, 0x7E, 0xFF));
    private static readonly IPen ImageHoverPen = new Pen(new SolidColorBrush(Color.FromRgb(0x3D, 0x7E, 0xFF)), 1.5);
    private static readonly IBrush ImageDragBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x3D, 0x7E, 0xFF));
    private static readonly IBrush ImageCropShade = new SolidColorBrush(Color.FromArgb(0x90, 0x20, 0x20, 0x20));
    private static readonly IPen ImageCropHandlePen = new Pen(Brushes.Black, 2);

    /// <summary>Drag in progress: moving (handle −1) or resizing; rectangles in document coordinates (independent of scrolling).</summary>
    private sealed class ImageDrag
    {
        public int Page;
        public int Handle = -1;
        public Point Start;
        public Rect StartRect;
        public Rect Current;
        public bool Moved;
        public Rect? Within;   // cutting: the frame stays within the picture's box (document coordinates)
        public bool Band;      // Shift + dragging: a frame that chooses everything inside it
    }

    private ImageDrag? _imageDrag;
    private (int Page, IReadOnlyList<int> Group)? _hoverImages;

    private bool IsImageEditTool => _doc?.Tool == ViewTool.Edit;

    /// <summary>Three screen pixels in page units: thin lines can be hit at any zoom.</summary>
    private double HitTolerance => 3 / Math.Max(Scale, 1e-6);

    /// <summary>Box of images (page space) in the view.</summary>
    private Rect ImageViewRect(int page, RectD box) => ViewRect(page, new PdfRectangle(box.Left, box.Top, box.Right, box.Bottom));

    /// <summary>The chosen images' box in the view; null if none is chosen on a shown page.</summary>
    private Rect? SelectedImagesViewRect()
    {
        if (_doc is not { SelectedImages: { } s, SelectedImagesBox: { } box } || s.Page >= _pageRects.Count || !_shown[s.Page]) return null;
        return ImageViewRect(s.Page, box);
    }

    /// <summary>The frame of what stays when cutting, in the view; null when not cutting.</summary>
    private Rect? CropFrameViewRect() =>
        _doc is { SelectedImages: { } s, CropFrame: { } frame } && s.Page < _pageRects.Count && _shown[s.Page] ? ImageViewRect(s.Page, frame) : null;

    /// <summary>The rectangle the handles belong to: the frame when cutting, otherwise the chosen images' box.</summary>
    private Rect? HandleViewRect() => CropFrameViewRect() ?? SelectedImagesViewRect();

    /// <summary>The handle (0–7, clockwise from the top left) of the chosen images (or the cutting frame) under the point; null if none.</summary>
    private int? ImageHandleAt(Point viewPoint)
    {
        if (HandleViewRect() is not { } rect) return null;
        var reach = rect.Contains(viewPoint) ? Math.Min(HandleSize, Math.Min(rect.Width, rect.Height) / 4) : HandleSize;
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

    /// <summary>The highlighted picture under the mouse, the chosen one with handles, and the dragged box (only the text blocks are framed in this mode; pictures show themselves under the mouse).</summary>
    private void DrawContentImages(DrawingContext context, Rect view)
    {
        if (!IsImageEditTool || _doc is not { } doc) return;
        for (var i = 0; i < _pageRects.Count; i++)
        {
            if (!_shown[i] || !_pageRects[i].Intersects(view)) continue;
            if (_hoverImages is not { } hoverHere || hoverHere.Page != i || doc.ContentObjectsOn(i) is not { } objects) continue;
            if (_hoverImages is { } hover && hover.Page == i && _imageDrag is null && hover.Group.All(g => g < objects.Count) &&
                (doc.SelectedImages is not { } s || s.Page != i || !s.Keys.SequenceEqual(hover.Group.Select(objects.KeyOf))))
                foreach (var g in hover.Group) context.DrawRectangle(ImageHoverBrush, ImageHoverPen, ImageViewRect(i, objects.VisibleOf(g)));
        }
        // cutting: the whole picture stays visible, what would go is darkened, the frame of what stays has dark handles
        if (doc.IsCroppingImages && SelectedImagesViewRect() is { } picture && HandleViewRect() is { } frame)
        {
            var kept = _imageDrag is { Moved: true } cropDrag ? ToViewRect(cropDrag.Current) : frame;
            kept = kept.Intersect(picture);
            context.DrawGeometry(ImageCropShade, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(picture), new RectangleGeometry(kept)));
            context.DrawRectangle(null, ImageHoverPen, kept);
            foreach (var h in Handles(kept))
                context.DrawRectangle(Brushes.Black, ImageCropHandlePen, new Rect(h.X - HandleSize / 2, h.Y - HandleSize / 2, HandleSize, HandleSize));
            return;
        }
        if (_imageDrag is { Moved: true } drag)
        {
            context.DrawRectangle(ImageDragBrush, FramePen, ToViewRect(drag.Current));
            return;
        }
        if (SelectedImagesViewRect() is not { } rect) return;
        context.DrawRectangle(null, ImageHoverPen, rect);
        foreach (var h in Handles(rect))
            context.DrawRectangle(Brushes.White, HandlePen, new Rect(h.X - HandleSize / 2, h.Y - HandleSize / 2, HandleSize, HandleSize));
    }

    /// <summary>Left button in this mode: a handle starts resizing, the chosen picture or another image starts moving (and chooses it). true = handled.</summary>
    private bool ImageEditPressed(PointerPressedEventArgs e, Point position)
    {
        if (!IsImageEditTool || _doc is not { } doc || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return false;
        // cutting: the handles shape the frame, a press inside the picture moves the frame — the picture itself stays
        if (doc is { IsCroppingImages: true, SelectedImages: { } cropped } && SelectedImagesViewRect() is { } picture && CropFrameViewRect() is { } frame &&
            (ImageHandleAt(position) is not null || picture.Contains(position)))
        {
            BeginImageDrag(cropped.Page, ToDocument(position), frame, ImageHandleAt(position) ?? -1);
            _imageDrag!.Within = picture.Translate(_offset);
            e.Pointer.Capture(this);
            return true;
        }
        if (ImageHandleAt(position) is { } handle && doc.SelectedImages is { } chosen && SelectedImagesViewRect() is { } box)
        {
            BeginImageDrag(chosen.Page, ToDocument(position), box, handle);
            e.Pointer.Capture(this);
            return true;
        }
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        // Shift + dragging: a frame, everything inside it is chosen (B7) — also on a picture, so that one can start anywhere
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && PageAt(position) is { } bandPage)
        {
            BeginImageDrag(bandPage, ToDocument(position), new Rect(position, position), -1);
            _imageDrag!.Band = true;
            e.Pointer.Capture(this);
            return true;
        }
        if (SelectedImagesViewRect() is { } current && current.Contains(position) && doc.SelectedImages is { } selection &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Alt) && !ctrl)
        {
            BeginImageDrag(selection.Page, ToDocument(position), current, -1);
            e.Pointer.Capture(this);
            return true;
        }
        if (PageAt(position) is not { } page) return false;
        var (x, y) = ToPage(page, position);
        var extend = ctrl;
        if (!doc.SelectImageAt(page, x, y, single: e.KeyModifiers.HasFlag(KeyModifiers.Alt), extend, HitTolerance))
        {
            InvalidateVisual();
            return false;   // beside the images: dragging pans the view as usual
        }
        if (extend)
        {
            InvalidateVisual();
            return true;   // Ctrl adds or takes out; it does not start dragging
        }
        if (SelectedImagesViewRect() is { } chosenRect) BeginImageDrag(page, ToDocument(position), chosenRect, -1);
        e.Pointer.Capture(this);
        InvalidateVisual();
        return true;
    }

    private void BeginImageDrag(int page, Point start, Rect viewRect, int handle) =>
        _imageDrag = new ImageDrag { Page = page, Handle = handle, Start = start, StartRect = viewRect.Translate(_offset), Current = viewRect.Translate(_offset) };

    /// <summary>Mouse moved in this mode: drags, or highlights the picture under the mouse with a matching cursor. false = not in this mode.</summary>
    private bool ImageEditMoved(Point position, bool shift)
    {
        if (!IsImageEditTool || _doc is not { } doc) return false;
        if (_imageDrag is { } drag)
        {
            var delta = ToDocument(position) - drag.Start;
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 3) drag.Moved = true;
            if (!drag.Moved) return true;
            var r = drag.StartRect;
            if (drag.Band)
            {
                var end = ToDocument(position);
                drag.Current = new Rect(new Point(Math.Min(drag.Start.X, end.X), Math.Min(drag.Start.Y, end.Y)),
                    new Point(Math.Max(drag.Start.X, end.X), Math.Max(drag.Start.Y, end.Y)));
            }
            else if (drag.Handle < 0 && drag.Within is { } area)
            {
                // the frame moves within the picture
                var dx = Math.Clamp(delta.X, area.Left - r.Left, area.Right - r.Right);
                var dy = Math.Clamp(delta.Y, area.Top - r.Top, area.Bottom - r.Bottom);
                drag.Current = r.Translate(new Vector(dx, dy));
            }
            else if (drag.Handle < 0) drag.Current = r.Translate(delta);
            else
            {
                var min = 4.0;
                double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
                if (drag.Handle is 0 or 1 or 2) top = Math.Min(r.Top + delta.Y, bottom - min);
                if (drag.Handle is 4 or 5 or 6) bottom = Math.Max(r.Bottom + delta.Y, top + min);
                if (drag.Handle is 0 or 6 or 7) left = Math.Min(r.Left + delta.X, right - min);
                if (drag.Handle is 2 or 3 or 4) right = Math.Max(r.Right + delta.X, left + min);
                if (!doc.IsCroppingImages && doc.ImageKeepAspect != shift && r.Width > 0 && r.Height > 0)
                    (left, top, right, bottom) = KeepAspect(r, drag.Handle, left, top, right, bottom);
                drag.Current = new Rect(new Point(left, top), new Point(right, bottom));
                if (drag.Within is { } limit) drag.Current = drag.Current.Intersect(limit);
            }
            InvalidateVisual();
            return true;
        }
        if (ImageHandleAt(position) is { } handle)
        {
            Cursor = HandleCursors[handle];
            return true;
        }
        (int, IReadOnlyList<int>)? hover = null;
        if (PageAt(position) is { } page)
        {
            var (x, y) = ToPage(page, position);
            if (doc.ContentObjectAt(page, x, y, HitTolerance) is { } index) hover = (page, doc.ContentObjectGroup(page, index));
        }
        if (hover?.Item1 != _hoverImages?.Page || !(hover?.Item2 ?? []).SequenceEqual(_hoverImages?.Group ?? []))
        {
            _hoverImages = hover;
            InvalidateVisual();
        }
        Cursor = hover is not null || SelectedImagesViewRect() is { } r2 && r2.Contains(position) ? MoveCursor : HandCursor;
        return true;
    }

    /// <summary>Release in this mode: applies the move or the new size (one step). true = handled.</summary>
    private bool ImageEditReleased(PointerReleasedEventArgs e)
    {
        if (_imageDrag is not { } drag || _doc is not { } doc) return false;
        _imageDrag = null;
        e.Pointer.Capture(null);
        if (drag.Band && drag.Page < _pageRects.Count)
        {
            var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            if (drag.Moved)
            {
                var band = PageRect(drag.Page, ToViewRect(drag.Current));
                var area = RectD.FromLtrb(band.Left, band.Bottom, band.Right, band.Top);
                // a frame over text blocks edits them as one; only a frame that touches none chooses the objects inside (unless it adds to chosen ones)
                if (ctrl || doc.SelectedImages is not null || !EditTouchedBlocks(doc, drag.Page, area, e.GetPosition(this)))
                    doc.SelectImagesIn(drag.Page, area, extend: ctrl);
            }
            else
            {
                // Shift without dragging: an ordinary click
                var (x, y) = ToPage(drag.Page, ToViewRect(new Rect(drag.Start, drag.Start)).TopLeft);
                doc.SelectImageAt(drag.Page, x, y, single: false, extend: ctrl, HitTolerance);
            }
        }
        else if (drag.Moved && drag.Page < _pageRects.Count)
        {
            var from = PageRect(drag.Page, ToViewRect(drag.StartRect));
            var to = PageRect(drag.Page, ToViewRect(drag.Current));
            if (drag.Within is not null) doc.CropFrame = RectD.FromLtrb(to.Left, to.Bottom, to.Right, to.Top);   // cutting: only the frame
            else if (drag.Handle < 0) doc.MoveSelectedImages(to.Left - from.Left, to.Bottom - from.Bottom);
            else doc.FitSelectedImages(RectD.FromLtrb(to.Left, to.Bottom, to.Right, to.Top));
        }
        InvalidateVisual();
        return true;
    }

    /// <summary>Keys in this mode: arrows move the chosen picture (a point, Shift ten; in the direction on screen), Esc lets go of it. true = handled.</summary>
    private bool ImageEditKey(KeyEventArgs e)
    {
        if (!IsImageEditTool || _doc is not { SelectedImages: { } selection } doc) return false;
        // cutting: Enter cuts, Esc drops the frame (the picture stays chosen)
        if (doc.IsCroppingImages && e.Key is Key.Enter or Key.Escape)
        {
            if (e.Key == Key.Enter) _ = doc.ApplyCropAsync();
            else doc.IsCroppingImages = false;
            InvalidateVisual();
            return true;
        }
        switch (e.Key)
        {
            case Key.Escape:
                doc.SelectedImages = null;
                InvalidateVisual();
                return true;
            case Key.Delete or Key.Back:
                doc.DeleteSelectedImages();
                InvalidateVisual();
                return true;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                var step = (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1) * Scale;
                var (vx, vy) = e.Key switch { Key.Left => (-step, 0.0), Key.Right => (step, 0.0), Key.Up => (0.0, -step), _ => (0.0, step) };
                var (dx, dy) = PageToView(selection.Page).Invert().TransformVector(vx, vy);
                doc.MoveSelectedImages(dx, dy, continueLast: true);
                InvalidateVisual();
                return true;
        }
        return false;
    }

    /// <summary>Mouse capture lost while dragging: nothing is applied.</summary>
    private void ImageEditCaptureLost() => _imageDrag = null;
}
