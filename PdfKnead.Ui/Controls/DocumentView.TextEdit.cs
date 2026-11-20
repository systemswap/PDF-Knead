using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.ViewModels;

namespace PdfKnead.Controls;

/// <summary>
/// Edit text (see KONZEPT.md, section 5.14; part of „Bearbeiten“, section 5.22): in this mode, all text blocks of the visible pages get a
/// thin frame, and the one under the mouse is highlighted. Locked blocks (barcode, invisible text …) are dashed grey; a click on one explains why. Several blocks can be
/// joined for one edit („Blöcke zusammenfassen“): Ctrl+click chooses blocks (a notice says how to go on), Enter or a plain click on a
/// chosen block edits them as one, Esc drops the choice; Shift and dragging a frame edits every block the frame touches as one.
/// </summary>
public sealed partial class DocumentView
{
    // Frame slightly outside the glyphs (in points), so that it does not touch ascenders and descenders.
    private const double BlockMargin = 2.5;

    private static readonly IPen BlockPen = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0x3D, 0x7E, 0xFF)), 1);
    private static readonly IPen BlockHoverPen = new Pen(FrameBrush, 1.5);
    private static readonly IBrush BlockHoverBrush = new SolidColorBrush(Color.FromArgb(0x14, 0x3D, 0x7E, 0xFF));
    private static readonly IPen LockedPen = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0x80, 0x80, 0x80)), 1, new DashStyle([3, 3], 0));

    private (int Page, PageTextBlock Block)? _hoverBlock;

    private static readonly IPen BlockChosenPen = new Pen(FrameBrush, 1.5, new DashStyle([4, 2], 0));
    private static readonly IBrush BlockChosenBrush = new SolidColorBrush(Color.FromArgb(0x28, 0x3D, 0x7E, 0xFF));

    /// <summary>Blocks chosen with Ctrl+click (one page), to be edited as one.</summary>
    private (int Page, List<PageTextBlock> Blocks)? _chosenBlocks;

    /// <summary>Shift + dragging: the frame (document coordinates) that chooses every block it touches.</summary>
    private (int Page, Point Start, Rect Current, bool Moved)? _blockBand;

    private bool IsTextEditTool => _doc?.Tool == ViewTool.Edit;

    /// <summary>Draws the frames of all blocks of the visible pages; blocks not yet detected are requested (the view redraws afterwards).</summary>
    private void DrawTextBlocks(DrawingContext context, Rect view)
    {
        if (!IsTextEditTool || _doc is not { } doc)
        {
            _chosenBlocks = null;
            return;
        }
        var visible = new List<int>();
        for (var i = 0; i < _pageRects.Count; i++)
            if (_shown[i] && _pageRects[i].Intersects(view)) visible.Add(i);
        doc.ShowingBlocks(visible);
        foreach (var i in visible)
        {
            if (doc.BlocksOn(i) is not { } blocks) continue;
            var toView = PageToView(i);
            foreach (var block in blocks)
            {
                var hover = _hoverBlock is { } h && h.Page == i && ReferenceEquals(h.Block, block);
                var chosen = _chosenBlocks is { } c && c.Page == i && c.Blocks.Contains(block);
                var geometry = QuadGeometry(block.Outline(BlockMargin), toView);
                if (block.Locked is not null) context.DrawGeometry(null, LockedPen, geometry);
                else if (chosen) context.DrawGeometry(BlockChosenBrush, hover ? BlockHoverPen : BlockChosenPen, geometry);
                else if (hover) context.DrawGeometry(BlockHoverBrush, BlockHoverPen, geometry);
                else context.DrawGeometry(null, BlockPen, geometry);
            }
        }
        if (_blockBand is { Moved: true } band) context.DrawRectangle(BlockHoverBrush, BlockHoverPen, ToViewRect(band.Current));
    }

    /// <summary>Block under a point of the view (the smallest if frames overlap); null outside blocks or if not yet detected.</summary>
    private (int Page, PageTextBlock Block)? BlockAt(Point viewPoint)
    {
        if (PageAt(viewPoint) is not { } page) return null;
        var (x, y) = ToPage(page, viewPoint);
        return BlockAtPage(page, x, y);
    }

    /// <summary>The same for a point in page space.</summary>
    private (int Page, PageTextBlock Block)? BlockAtPage(int page, double x, double y)
    {
        if (_doc is not { } doc || doc.BlocksOn(page) is not { } blocks) return null;
        var margin = BlockMargin + 2 / Math.Max(Scale, 1e-6);   // two screen pixels of tolerance
        PageTextBlock? best = null;
        foreach (var block in blocks)
            if (block.Contains(x, y, margin) && (best is null || block.Width * (block.Top - block.Bottom) < best.Width * (best.Top - best.Bottom)))
                best = block;
        return best is null ? null : (page, best);
    }

    /// <summary>Mouse moved in this mode: highlights the block, text cursor over editable blocks. false = not in this mode.</summary>
    private bool TextEditMoved(Point position)
    {
        if (!IsTextEditTool) return false;
        if (_blockBand is { } band)
        {
            var end = ToDocument(position);
            var moved = band.Moved || Math.Abs(end.X - band.Start.X) + Math.Abs(end.Y - band.Start.Y) > 3;
            _blockBand = (band.Page, band.Start, new Rect(new Point(Math.Min(band.Start.X, end.X), Math.Min(band.Start.Y, end.Y)),
                new Point(Math.Max(band.Start.X, end.X), Math.Max(band.Start.Y, end.Y))), moved);
            InvalidateVisual();
            return true;
        }
        var hit = BlockAt(position);
        if (hit?.Block != _hoverBlock?.Block)
        {
            _hoverBlock = hit;
            InvalidateVisual();
        }
        Cursor = hit is { Block.Locked: null } ? TextCursor : hit is not null ? NoCursor : HandCursor;
        return true;
    }

    /// <summary>
    /// Click in this mode: on a block already being edited, editing continues there; on a block of the page, editing begins (the page
    /// without its text, the block as formatted text, the caret at the click position); on a locked block, the reason is shown.
    /// true = handled (otherwise dragging pans the view as usual).
    /// </summary>
    private bool TextEditPressed(PointerPressedEventArgs e, Point position)
    {
        if (!IsTextEditTool || _doc is not { } doc || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return false;
        // Handles and border of a block: change the width and move it as with other elements.
        if (HandleAt(position) is not null && doc.SelectedElement is { Element: RichTextElement }) return false;
        var own = ElementAt(position) is { Element: RichTextElement } ownHit ? ownHit : (ElementRef?)null;
        if (own is { } bordered && OnElementBorder(bordered, position)) return false;
        var hit = own is null ? BlockAt(position) : null;
        // Shift: a frame that chooses every block it touches (edited as one when released)
        if (own is null && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && PageAt(position) is { } bandPage)
        {
            _blockBand = (bandPage, ToDocument(position), new Rect(ToDocument(position), new Size()), false);
            e.Pointer.Capture(this);
            return true;
        }
        if (own is null && hit is null) return false;
        if (hit?.Block.Locked is { } reason)
        {
            doc.Notify(reason);
            return true;
        }
        // Ctrl: choose the block (or drop it from the choice); blocks of one page only
        if (hit is { } toggled && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            if (_chosenBlocks is not { } chosen || chosen.Page != toggled.Page) _chosenBlocks = chosen = (toggled.Page, []);
            if (!chosen.Blocks.Remove(toggled.Block)) chosen.Blocks.Add(toggled.Block);
            // how to go on: nothing on the screen said it (a plain click on a chosen block was not found)
            if (chosen.Blocks.Count >= 2)
                doc.Notify(Tr.T("{0} Blöcke gewählt — Enter oder ein Klick auf einen von ihnen bearbeitet sie als einen, Esc hebt die Wahl auf.", chosen.Blocks.Count));
            InvalidateVisual();
            return true;
        }
        if (!doc.CanEditElements)
        {
            doc.Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
            return true;
        }
        if (own is { } ownRef)
        {
            PlaceCaret(doc.BeginRichEdit(ownRef, 0), position);
            return true;
        }
        var (page, block) = hit!.Value;
        EditBlock(doc, page, block, position);
        return true;
    }

    /// <summary>A plain click on a block edits it; on one of the chosen blocks, the chosen blocks become one.</summary>
    private void EditBlock(DocumentViewModel doc, int page, PageTextBlock block, Point position)
    {
        if (_chosenBlocks is { } choice && choice.Page == page && choice.Blocks.Contains(block) && choice.Blocks.Count > 1)
        {
            _chosenBlocks = null;
            if (PageTextBlock.Joined(choice.Blocks, out var why) is { } joined) _ = BeginBlockEdit(doc, page, joined, position);
            else if (why is not null) doc.Notify(why);
            return;
        }
        _chosenBlocks = null;
        _ = BeginBlockEdit(doc, page, block, position);
    }

    /// <summary>Keys in this mode with chosen blocks: Enter edits them as one, Esc drops the choice. true = handled.</summary>
    private bool TextEditKey(KeyEventArgs e)
    {
        if (!IsTextEditTool || _doc is not { } doc || _chosenBlocks is not { } choice || e.KeyModifiers != KeyModifiers.None) return false;
        if (e.Key == Key.Escape)
        {
            _chosenBlocks = null;
            InvalidateVisual();
            return true;
        }
        if (e.Key != Key.Enter) return false;
        _chosenBlocks = null;
        InvalidateVisual();
        if (choice.Blocks.Count == 0) return true;
        var first = choice.Blocks.OrderByDescending(b => b.Top).First();
        var place = PagePointToView(choice.Page, first.Bounds.Left + 1, first.Bounds.Bottom - 1);   // the caret at the start of the top block
        if (choice.Blocks.Count == 1) _ = BeginBlockEdit(doc, choice.Page, first, place);
        else if (PageTextBlock.Joined(choice.Blocks, out var why) is { } joined) _ = BeginBlockEdit(doc, choice.Page, joined, place);
        else if (why is not null) doc.Notify(why);
        return true;
    }

    /// <summary>Shift + dragging released: every block the frame touches is edited as one (a click without dragging chooses nothing).</summary>
    private bool TextEditReleased(PointerReleasedEventArgs e)
    {
        if (_blockBand is not { } band || _doc is not { } doc) return false;
        _blockBand = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
        if (!band.Moved) return true;
        var frame = PageRect(band.Page, ToViewRect(band.Current));
        EditTouchedBlocks(doc, band.Page, RectD.FromLtrb(frame.Left, frame.Bottom, frame.Right, frame.Top), e.GetPosition(this));
        return true;
    }

    /// <summary>Edits every block the frame touches as one. false = the frame touches no block (or the blocks are not detected yet).</summary>
    private bool EditTouchedBlocks(DocumentViewModel doc, int page, RectD area, Point position)
    {
        if (doc.BlocksOn(page) is not { } blocks) return false;
        var touched = blocks.Where(b => b.Bounds.Intersects(area)).ToList();
        if (touched.Count == 0) return false;
        if (touched.FirstOrDefault(b => b.Locked is not null) is { } locked)
        {
            doc.Notify(locked.Locked!);
            return true;
        }
        if (!doc.CanEditElements)
        {
            doc.Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
            return true;
        }
        if (touched.Count == 1) _ = BeginBlockEdit(doc, page, touched[0], position);
        else if (PageTextBlock.Joined(touched, out var why) is { } joined) _ = BeginBlockEdit(doc, page, joined, position);
        else if (why is not null) doc.Notify(why);
        return true;
    }

    /// <summary>
    /// Converts the page's block (in the background), then places the caret at the click position. The position is kept in page
    /// space: the view may scroll or change its zoom meanwhile (a panel on the right opens or closes), and the caret must land where the
    /// click was on the page (docs/FEHLERGEDAECHTNIS.md, No. 396).
    /// </summary>
    private async Task BeginBlockEdit(DocumentViewModel doc, int page, PageTextBlock block, Point position)
    {
        var (x, y) = ToPage(page, position);
        Cursor = new Cursor(StandardCursorType.Wait);
        try
        {
            if (await doc.EditBlockAsync(page, block) is { } editor && ReferenceEquals(doc, _doc) && page < _pageRects.Count)
                PlaceCaret(editor, PagePointToView(page, x, y));
        }
        finally
        {
            Cursor = TextCursor;
        }
    }

    /// <summary>Places the caret at the given point of the view.</summary>
    private void PlaceCaret(RichTextEditor editor, Point position)
    {
        if (ViewToRich(editor, position) is { } p) editor.SetCaret(editor.Layout.HitTest(p.X, p.Y));
    }

    private static readonly Cursor NoCursor = new(StandardCursorType.No);
}
