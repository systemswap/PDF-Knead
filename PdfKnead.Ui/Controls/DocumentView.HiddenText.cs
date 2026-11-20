using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.ViewModels;

namespace PdfKnead.Controls;

/// <summary>
/// „Verborgenes schwärzen“ (KONZEPT.md, section 5.18): while the tool is on, every hidden spot of the visible pages gets a subtle dashed
/// frame labelled „Verborgener Inhalt“, with its text readable in orange-grey (enlarged if it is tiny); a click marks the spot for
/// redaction or takes the mark back. Marked spots have a red frame. Spots beside the page are listed on the right; the one chosen in the
/// list is framed where it lies, or — if that is beyond what can be scrolled to — named in a badge at the edge of the view that points
/// towards it. With the tool off, nothing of this is drawn and the marks cannot be clicked (they lie behind the text).
/// </summary>
public sealed partial class DocumentView
{
    private static readonly IBrush HiddenTextBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0x7E, 0x55));
    private static readonly IBrush HiddenLabelBrush = new SolidColorBrush(Color.FromRgb(0x8C, 0x56, 0x2C));
    private static readonly IBrush HiddenMarkedLabelBrush = new SolidColorBrush(Color.FromRgb(0xB4, 0x23, 0x18));
    private static readonly IBrush HiddenVeil = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush HiddenLabelBack = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
    private static readonly IPen HiddenPen = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xB4, 0x7A, 0x4A)), 1, new DashStyle([4, 3], 0));
    private static readonly IPen HiddenHoverPen = new Pen(new SolidColorBrush(Color.FromRgb(0xB4, 0x7A, 0x4A)), 2, new DashStyle([4, 3], 0));
    private static readonly IPen HiddenMarkedPen = new Pen(new SolidColorBrush(Color.FromRgb(0xD9, 0x2D, 0x20)), 1.5);
    private static readonly IPen HiddenCurrentPen = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0x3D, 0x7E, 0xFF)), 5);
    private static readonly Typeface HiddenLabelFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    // Frames of the last drawing (control coordinates), for the hit test
    private readonly List<(int Page, HiddenTextSpot Spot, Rect Frame)> _hiddenFrames = [];
    private HiddenTextSpot? _hoverHidden;
    private static readonly IBrush CleanupArea = new SolidColorBrush(Color.FromArgb(0x2A, 0xD9, 0x2D, 0x20));
    // Frames of the places of the last drawing (control coordinates; for tests), and their „x“ buttons with the place each belongs to
    private readonly List<Rect> _cleanupFrames = [];
    private readonly List<(Rect Button, CleanupPlace Place)> _cleanupButtons = [];
    private static readonly IBrush CleanupKeptArea = new SolidColorBrush(Color.FromArgb(0x22, 0x60, 0x60, 0x60));
    private static readonly IPen CleanupKeptPen = new Pen(new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70)), 1.5, new DashStyle([4, 3], 0));
    private static readonly IBrush CleanupKeptLabelBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));

    /// <summary>The „x“ buttons on the frames in the last drawing (control coordinates; for tests).</summary>
    internal IReadOnlyList<(Rect Button, CleanupPlace Place)> CleanupButtons => _cleanupButtons;

    /// <summary>The frames of the places of a cleanup finding in the last drawing (control coordinates; for tests).</summary>
    internal IReadOnlyList<Rect> CleanupFrames => _cleanupFrames;

    private bool IsHiddenTool => _doc?.Tool == ViewTool.RedactHidden;

    /// <summary>The frames of the last drawing (control coordinates; for tests).</summary>
    internal IReadOnlyList<(int Page, HiddenTextSpot Spot, Rect Frame)> HiddenFrames => _hiddenFrames;

    /// <summary>Draws the frames, readable texts and labels of the hidden spots on the visible pages.</summary>
    private void DrawHiddenSpots(DrawingContext context, Rect view)
    {
        _hiddenFrames.Clear();
        if (!IsHiddenTool || _doc is not { } doc) return;
        var screen = new Rect(Bounds.Size);
        for (var i = 0; i < _pageRects.Count; i++)
        {
            if (!_shown[i] || !_pageRects[i].Intersects(view) || doc.HiddenOn(i) is not { } spots) continue;
            foreach (var spot in spots)
            {
                var current = doc.SelectedHiddenRow is { } row && row.Page == i && ReferenceEquals(row.Spot, spot);
                if (spot.OutsidePage && !current) continue;   // listed on the right; shown once chosen there
                var (frame, texts) = HiddenLayout(i, spot.Lines, spot.Text);
                var marked = doc.IsHiddenMarked(i, spot);
                if (!frame.Intersects(screen))
                {
                    if (spot.OutsidePage) DrawOutsideBadge(context, i, spot, frame, screen, marked);
                    continue;
                }
                _hiddenFrames.Add((i, spot, frame));
                if (current) context.DrawRectangle(null, HiddenCurrentPen, frame.Inflate(2));
                // A light ground only behind the readable text: what lies elsewhere in the frame stays visible, and covered text
                // (on a black box) can still be read
                foreach (var (text, at) in texts)
                {
                    context.FillRectangle(HiddenVeil, new Rect(at, new Size(text.Width, text.Height)));
                    context.DrawText(text, at);
                }
                context.DrawRectangle(null, marked ? HiddenMarkedPen : ReferenceEquals(_hoverHidden, spot) ? HiddenHoverPen : HiddenPen, frame);
                var caption = spot.OutsidePage ? Tr.T("Verborgener Inhalt außerhalb des Blattes") : Tr.T("Verborgener Inhalt");
                var label = new FormattedText(marked ? caption + " — " + Tr.T("wird geschwärzt") : caption,
                    Tr.Culture, FlowDirection.LeftToRight, HiddenLabelFace, 11, marked ? HiddenMarkedLabelBrush : HiddenLabelBrush);
                var labelRect = new Rect(frame.Left, frame.Top - label.Height - 1, label.Width + 6, label.Height);
                context.FillRectangle(HiddenLabelBack, labelRect);
                context.DrawText(label, labelRect.TopLeft + new Point(3, 0));
            }
        }
    }

    /// <summary>
    /// The chosen spot beside the page lies beyond what the view can scroll to: a badge at the edge of the view, on the side where it lies,
    /// with an arrow towards it and the beginning of its text. A click on the badge marks the spot or takes the mark back, like a frame.
    /// </summary>
    private void DrawOutsideBadge(DrawingContext context, int page, HiddenTextSpot spot, Rect frame, Rect screen, bool marked)
    {
        var dx = frame.Center.X - screen.Center.X;
        var dy = frame.Center.Y - screen.Center.Y;
        var arrow = (Math.Abs(dx) > 2 * Math.Abs(dy), Math.Abs(dy) > 2 * Math.Abs(dx)) switch
        {
            (true, _) => dx < 0 ? "←" : "→",
            (_, true) => dy < 0 ? "↑" : "↓",
            _ => dx < 0 ? (dy < 0 ? "↖" : "↙") : dy < 0 ? "↗" : "↘",
        };
        var words = spot.Text.Length > 40 ? spot.Text[..40] + " …" : spot.Text;
        var caption = arrow + " " + Tr.T("Verborgener Inhalt außerhalb des Blattes") + (marked ? " — " + Tr.T("wird geschwärzt") : "") + ": "
                      + words.ReplaceLineEndings(" ");
        var text = new FormattedText(caption, Tr.Culture, FlowDirection.LeftToRight, HiddenLabelFace, 12, marked ? HiddenMarkedLabelBrush : HiddenLabelBrush)
        {
            MaxTextWidth = Math.Max(120, screen.Width - 40),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        var size = new Size(text.Width + 16, text.Height + 10);
        // on the side where the spot lies, as near to it as the view allows
        var x = Math.Clamp(frame.Center.X - size.Width / 2, screen.Left + 8, Math.Max(screen.Left + 8, screen.Right - size.Width - 8));
        var y = Math.Clamp(frame.Center.Y - size.Height / 2, screen.Top + 8, Math.Max(screen.Top + 8, screen.Bottom - size.Height - 8));
        var badge = new Rect(new Point(x, y), size);
        _hiddenFrames.Add((page, spot, badge));
        context.FillRectangle(HiddenLabelBack, badge, 4);
        context.DrawRectangle(null, HiddenCurrentPen, badge.Inflate(2), 4, 4);
        context.DrawRectangle(null, marked ? HiddenMarkedPen : HiddenHoverPen, badge, 4, 4);
        context.DrawText(text, badge.TopLeft + new Point(8, 5));
    }

    /// <summary>
    /// The places of a finding of „Dokument bereinigen“ (<see cref="DocumentViewModel.CleanupPlaces"/>), whatever tool is on: a light red
    /// area with a red frame, hidden text readable in it as with „Verborgenes schwärzen“, and a label naming the finding (for a form field
    /// with its stored value).
    /// </summary>
    private void DrawCleanupPlaces(DrawingContext context, Rect view)
    {
        _cleanupFrames.Clear();
        _cleanupButtons.Clear();
        if (_doc?.CleanupPlaces is not { } shown || (uint)shown.Page >= (uint)_pageRects.Count || !_shown[shown.Page]
            || !_pageRects[shown.Page].Intersects(view)) return;
        var screen = new Rect(Bounds.Size);
        // the „x“ (keep this place) only while the area is open: with the window of the question before signing there is nothing to keep
        var keepable = _doc.IsCleanupOpen;
        foreach (var place in shown.Places)
        {
            var (frame, texts) = HiddenLayout(shown.Page, place.Lines, place.Text);
            if (!frame.Intersects(screen)) continue;
            var kept = keepable && _doc.IsCleanupKept(shown.Page, shown.Item, place);
            _cleanupFrames.Add(frame);
            context.FillRectangle(kept ? CleanupKeptArea : CleanupArea, frame);
            foreach (var (text, at) in texts)
            {
                context.FillRectangle(HiddenVeil, new Rect(at, new Size(text.Width, text.Height)));
                context.DrawText(text, at);
            }
            context.DrawRectangle(null, kept ? CleanupKeptPen : HiddenMarkedPen, frame);
            if (keepable)
            {
                // top right on the frame: × removes the place from the cleaning (it stays), ↺ takes that back
                var button = new Rect(frame.Right - 9, frame.Top - 9, 18, 18);
                _cleanupButtons.Add((button, place));
                context.DrawEllipse(Brushes.White, kept ? CleanupKeptPen : HiddenMarkedPen, button.Center, 9, 9);
                var sign = new FormattedText(kept ? "↺" : "×", Tr.Culture, FlowDirection.LeftToRight, HiddenLabelFace, 13,
                    kept ? CleanupKeptLabelBrush : HiddenMarkedLabelBrush);
                context.DrawText(sign, button.Center - new Point(sign.Width / 2, sign.Height / 2));
            }
            var caption = kept ? Tr.T("Bleibt (nicht löschen)") : CleanupCaption(shown.Item);
            if (place.Value is { } value)
            {
                value = value.ReplaceLineEndings(" ").Trim();
                caption += " · " + Tr.T("Gespeicherter Wert: {0}", value.Length > 60 ? value[..60] + " …" : value);
            }
            var label = new FormattedText(caption, Tr.Culture, FlowDirection.LeftToRight, HiddenLabelFace, 11, kept ? CleanupKeptLabelBrush : HiddenMarkedLabelBrush);
            var labelRect = new Rect(frame.Left, frame.Top - label.Height - 1, label.Width + 6, label.Height);
            context.FillRectangle(HiddenLabelBack, labelRect);
            context.DrawText(label, labelRect.TopLeft + new Point(3, 0));
        }
    }

    /// <summary>
    /// The place chosen in the question „Jetzt schwärzen?“ or the area „Notizen“ (<see cref="DocumentViewModel.ShownPlace"/>): a light red area with a
    /// red frame, invisible text named above it — on the page there is nothing to see of it.
    /// </summary>
    private void DrawShownPlace(DrawingContext context, Rect view)
    {
        if (_doc?.ShownPlace is not { } shown || (uint)shown.Page >= (uint)_pageRects.Count || !_shown[shown.Page]
            || !_pageRects[shown.Page].Intersects(view)) return;
        Rect? first = null;
        foreach (var area in shown.Areas)
        {
            var frame = ViewRect(shown.Page, area);
            first ??= frame;
            context.FillRectangle(CleanupArea, frame);
            context.DrawRectangle(null, HiddenMarkedPen, frame);
        }
        if (shown.Hidden is not { } hidden || first is not { } at) return;
        var label = new FormattedText(Tr.T("Versteckt: {0}", hidden), Tr.Culture, FlowDirection.LeftToRight, HiddenLabelFace, 11, HiddenMarkedLabelBrush);
        var labelRect = new Rect(at.Left, at.Top - label.Height - 1, label.Width + 6, label.Height);
        context.FillRectangle(HiddenLabelBack, labelRect);
        context.DrawText(label, labelRect.TopLeft + new Point(3, 0));
    }

    private static readonly IPen ModulePlacePen = new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0x8C, 0x00)), 2);

    /// <summary>
    /// The spot a module brought into view (<see cref="DocumentViewModel.ModulePlace"/>, e.g. a value of the comparison of an e-invoice): in the
    /// colour of the current search hit, framed a little around it, so that it is seen at once — not red, which here means a fault or redaction.
    /// </summary>
    private void DrawModulePlace(DrawingContext context, Rect view)
    {
        if (_doc?.ModulePlace is not { } current) return;
        bool Shown(int page) => (uint)page < (uint)_pageRects.Count && _shown[page] && _pageRects[page].Intersects(view);
        // the other spots of the same thing in the colour of the other search hits (decided on 15.11.2026)
        foreach (var other in _doc.ModulePlaces)
            if (other != current && Shown(other.Page)) context.DrawGeometry(MatchBrush, null, QuadGeometry(other.Quad, PageToView(other.Page)));
        if (!Shown(current.Page)) return;
        var geometry = QuadGeometry(current.Quad, PageToView(current.Page));
        context.DrawGeometry(CurrentMatchBrush, null, geometry);
        context.DrawRectangle(null, ModulePlacePen, geometry.Bounds.Inflate(3), 2, 2);
    }

    private static readonly IBrush RecognitionArea = new SolidColorBrush(Color.FromArgb(0x48, 0x12, 0xA0, 0x5C));
    private static readonly IPen RecognitionPen = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x12, 0x80, 0x5C)), 2);

    /// <summary>
    /// Where the text recognition is (<see cref="DocumentViewModel.RecognitionPlace"/>; only on the screen): a green frame around the page being read,
    /// the lines being read in light green.
    /// </summary>
    private void DrawRecognitionPlace(DrawingContext context, Rect view)
    {
        if (_doc?.RecognitionPlace is not { } place || (uint)place.Page >= (uint)_pageRects.Count || !_shown[place.Page]
            || !_pageRects[place.Page].Intersects(view)) return;
        context.DrawRectangle(null, RecognitionPen, _pageRects[place.Page].Translate(-_offset));
        foreach (var line in place.Lines)
            context.FillRectangle(RecognitionArea, ViewRect(place.Page, new PdfRectangle(line.Left, line.Top, line.Right, line.Bottom)), 2);
    }

    private bool _followingRecognition;

    /// <summary>Whether the page being read is in view.</summary>
    private bool RecognitionPageInView() =>
        _doc?.RecognitionPlace is { } place && (uint)place.Page < (uint)_pageRects.Count && _shown[place.Page] &&
        _pageRects[place.Page].Intersects(new Rect(_offset.X, _offset.Y, Bounds.Width, Bounds.Height));

    /// <summary>Brings the place being read into view; this is not the user scrolling.</summary>
    private void FollowRecognition(int page, Core.Pdf.Graphics.RectD? line)
    {
        _followingRecognition = true;
        try
        {
            if (line is { } l) Reveal(page, new Core.Pdf.Text.TextQuad(l.Left, l.Top, l.Right, l.Top, l.Right, l.Bottom, l.Left, l.Bottom));
            else if (!RecognitionPageInView()) ScrollToPage(page);
        }
        finally
        {
            _followingRecognition = false;
        }
    }

    /// <summary>The short name of a finding over its places.</summary>
    private static string CleanupCaption(CleanupItems item) => item switch
    {
        CleanupItems.InvisibleText => Tr.T("Unsichtbarer Text"),
        CleanupItems.SuspiciousText => Tr.T("Vermutlich unsichtbarer Text"),
        CleanupItems.HiddenContent => Tr.T("Was niemand sieht"),
        CleanupItems.HiddenImageParts => Tr.T("Bild mit verdeckten Teilen"),
        CleanupItems.FieldValueMismatch => Tr.T("Feld zeigt etwas anderes als seinen Wert"),
        _ => Tr.T("Fund"),
    };

    /// <summary>
    /// Frame and readable lines of a spot: each line at its place in at least 10 and at most 18 pixels (tiny text is enlarged), the
    /// frame around lines and texts, at least 24 × 16 pixels so that it can be hit.
    /// </summary>
    private (Rect Frame, List<(FormattedText Text, Point At)> Texts) HiddenLayout(int page, IReadOnlyList<PdfRectangle> areas, string text)
    {
        // Only the lines drawn are split off the text (it is whole and can be long; the last piece holds the rest and is not drawn)
        var lines = text.Split('\n', Math.Min(areas.Count, 200) + 1);
        var texts = new List<(FormattedText, Point)>();
        Rect? frame = null;
        for (var k = 0; k < areas.Count && k < 200; k++)
        {
            var line = ViewRect(page, areas[k]);
            frame = frame is { } f ? f.Union(line) : line;
            if (k >= lines.Length || lines[k].Length == 0) continue;
            var shown = new FormattedText(lines[k], Tr.Culture, FlowDirection.LeftToRight, Typeface.Default, Math.Clamp(line.Height * 0.8, 10, 18), HiddenTextBrush)
            {
                MaxTextWidth = Math.Max(line.Width, 260),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            var at = new Point(line.Left, line.Center.Y - shown.Height / 2);
            texts.Add((shown, at));
            frame = frame.Value.Union(new Rect(at, new Size(shown.Width, shown.Height)));
        }
        var r = (frame ?? default).Inflate(3);
        double w = Math.Max(r.Width, 24), h = Math.Max(r.Height, 16);
        return (new Rect(r.Center.X - w / 2, r.Center.Y - h / 2, w, h), texts);
    }

    /// <summary>The spot under the point (the smallest if frames overlap), from the last drawing.</summary>
    private (int Page, HiddenTextSpot Spot)? HiddenSpotAt(Point position)
    {
        (int, HiddenTextSpot)? best = null;
        var area = double.MaxValue;
        foreach (var (page, spot, frame) in _hiddenFrames)
            if (frame.Contains(position) && frame.Width * frame.Height < area)
            {
                best = (page, spot);
                area = frame.Width * frame.Height;
            }
        return best;
    }

    /// <summary>
    /// Click on the „x“ of a framed place of „Dokument bereinigen“: the place stays although its item is removed — or, clicked again, goes
    /// after all. false elsewhere.
    /// </summary>
    private bool CleanupPressed(PointerPressedEventArgs e, Point position)
    {
        if (_doc is not { IsCleanupOpen: true, CleanupPlaces: { } shown } doc || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return false;
        foreach (var (button, place) in _cleanupButtons)
            if (button.Inflate(2).Contains(position))
            {
                doc.ToggleCleanupKept(shown.Page, shown.Item, place);
                InvalidateVisual();
                return true;
            }
        return false;
    }

    /// <summary>Click with the tool on a frame: marks the spot or takes the mark back. false elsewhere (the view pans).</summary>
    private bool HiddenPressed(PointerPressedEventArgs e, Point position)
    {
        if (!IsHiddenTool || _doc is not { } doc || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || HiddenSpotAt(position) is not { } hit) return false;
        doc.ToggleHiddenMark(hit.Page, hit.Spot);
        doc.ChooseHiddenSpot(hit.Page, hit.Spot);
        InvalidateVisual();
        return true;
    }

    /// <summary>Mouse moved with the tool: highlights the frame under it. false = not in this mode.</summary>
    private bool HiddenMoved(Point position)
    {
        if (!IsHiddenTool) return false;
        var hit = HiddenSpotAt(position)?.Spot;
        if (!ReferenceEquals(hit, _hoverHidden))
        {
            _hoverHidden = hit;
            InvalidateVisual();
        }
        Cursor = hit is not null ? HandCursor : Cursor.Default;
        return true;
    }
}
