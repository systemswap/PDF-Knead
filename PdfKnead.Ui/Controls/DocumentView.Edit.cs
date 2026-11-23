using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Rendering;
using PdfKnead.ViewModels;

namespace PdfKnead.Controls;

/// <summary>
/// „Bearbeiten“ (KONZEPT.md, section 5.22): one tool for the text, images and codes of the page content. A click chooses what lies
/// there, in the order own elements → code → text block → image; Alt+click walks on to what lies underneath (at the end the single
/// tile of a picture made of tiles); a picture that fills the page (a scan) is chosen by double-click only. The parts — text editing
/// (<see cref="TextEditPressed"/>), image editing (<see cref="ImageEditPressed"/>), the code reader — keep their own handling; this
/// part only decides which of them a click belongs to. Codes are read at the clicked spot, and the clicked page's codes are searched
/// in the background for the clicks after it (never for all visible pages ahead).
/// </summary>
public sealed partial class DocumentView
{
    private bool IsEditTool => _doc?.Tool == ViewTool.Edit;

    /// <summary>The left button is down (set on press, cleared on release): a choice that arrives after a code was read starts dragging only then.</summary>
    private bool _leftDown;

    /// <summary>Something at a point of the page that „Bearbeiten“ can choose; Alt+click walks the list.</summary>
    private abstract record EditCandidate
    {
        public sealed record Own(ElementRef Element) : EditCandidate;
        public sealed record Code(PageBarcode Found) : EditCandidate;
        public sealed record Block(PageTextBlock Found) : EditCandidate;
        public sealed record Picture(IReadOnlyList<int> Group) : EditCandidate;
    }

    /// <summary>Left button in „Bearbeiten“: what is handled at once (handles, dragging the chosen picture, the block being edited), otherwise the choice at the spot. true = handled.</summary>
    private bool EditPressed(PointerPressedEventArgs e, Point position)
    {
        if (!IsEditTool || _doc is not { } doc) return false;
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return false;
        _leftDown = true;
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        // the handles and the frame of the chosen picture, cutting: image editing as it is
        if (doc.IsCroppingImages || doc.SelectedImages is not null && ImageHandleAt(position) is not null) return ImageEditPressed(e, position);
        // own elements first: the block being edited and other formatted text go to text editing, the rest to the elements (handled after this)
        if (doc.SelectedElement is not null && HandleAt(position) is not null) return false;
        // form fields and their labels: chosen, moved and resized as in „Formular bearbeiten“ (Ctrl or Shift adds to the chosen fields)
        if (!alt && FieldAt(position) is not null)
        {
            doc.EditTarget = EditTarget.None;
            return DesignPressed(e, position, doc);
        }
        if (!alt && ElementAt(position) is { } own)
            return own.Element is RichTextElement && TextEditPressed(e, position);
        // dragging the chosen picture from inside it, Shift + frame, Ctrl + click: image editing as it is
        if (!alt && doc.SelectedImages is not null && SelectedImagesViewRect() is { } chosen && chosen.Contains(position) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return ImageEditPressed(e, position);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return ImageEditPressed(e, position);   // a frame: on release blocks it touches, else the objects inside
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            // Ctrl: a block is added to the chosen blocks, a picture to the chosen pictures
            if (BlockAt(position) is not null) return TextEditPressed(e, position);
            return ImageEditPressed(e, position);
        }
        if (PageAt(position) is not { } page) return false;
        var (x, y) = ToPage(page, position);
        _ = ChooseAtGuardedAsync(doc, page, x, y, alt, e.ClickCount, e.Pointer);
        return true;
    }

    /// <summary>A failure while choosing (a damaged page, a lost document) is logged, never left as an unobserved exception.</summary>
    private async Task ChooseAtGuardedAsync(DocumentViewModel doc, int page, double x, double y, bool alt, int clickCount, IPointer? pointer)
    {
        try
        {
            await ChooseAtAsync(doc, page, x, y, alt, clickCount, pointer);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Core.Platform.AppLog.Write("Bearbeiten", e.ToString());
        }
    }

    /// <summary>
    /// Chooses what lies at the spot: a code (known from the page's codes, or read there), a text block, a picture. Alt walks on from
    /// what is chosen now. A picture that fills the page needs a double-click. Beside everything the choice is let go of.
    /// A plain click chooses at once from what is known, so that dragging a picture starts without delay; a code read at the spot
    /// afterwards takes the picture's place unless the mouse has moved on meanwhile. Alt+click waits for the code first.
    /// Works in page space: the view may re-fit the page while a panel opens (from the menu, or when the choice changes the panel).
    /// </summary>
    private async Task ChooseAtAsync(DocumentViewModel doc, int page, double x, double y, bool alt, int clickCount, IPointer? pointer)
    {
        var candidates = new List<EditCandidate>();
        if (alt)
        {
            var position = PagePointToView(page, x, y);
            var elements = doc.ElementsOn(page);
            for (var i = elements.Count - 1; i >= 0; i--)
                if (elements[i] is not RedactionElement { Hidden: true } && ViewRect(page, elements[i].Rect).Inflate(2).Contains(position))
                    candidates.Add(new EditCandidate.Own(new ElementRef(page, elements[i])));
        }
        // the blocks of the page are detected in the background (the view asked for them when it drew the page; from the menu the tool
        // has just been switched on): a click waits for them, otherwise text would count as nothing or as the picture behind it
        if (doc.BlocksOn(page) is null)
        {
            Cursor = new Cursor(StandardCursorType.Wait);
            try
            {
                var until = DateTime.UtcNow.AddSeconds(20);
                while (doc.BlocksOn(page) is null && DateTime.UtcNow < until && ReferenceEquals(doc, _doc) && doc.Tool == ViewTool.Edit)
                    await Task.Delay(25);
            }
            finally
            {
                if (ReferenceEquals(doc, _doc)) Cursor = HandCursor;
            }
            if (!ReferenceEquals(doc, _doc) || doc.Tool != ViewTool.Edit) return;
        }
        var block = BlockAtPage(page, x, y);
        // a code cannot lie in an ordinary text block: only where there is none, or the block is a bar code font, is one looked for
        var codeHere = block is null || block.Value.Block.Locked is not null;
        var known = doc.KnownCodes(page);
        Task<PageBarcode?>? reading = null;
        if (codeHere && known is not null)
        {
            if (known.FirstOrDefault(c => DocumentViewModel.Inside(c.Corners, x, y)) is { } code) candidates.Add(new EditCandidate.Code(code));
        }
        else if (codeHere)
        {
            reading = doc.CodeAtAsync(page, x, y);
            if (alt)
            {
                Cursor = new Cursor(StandardCursorType.Wait);
                try
                {
                    if (await reading is { } code) candidates.Add(new EditCandidate.Code(code));
                }
                finally
                {
                    if (ReferenceEquals(doc, _doc)) Cursor = HandCursor;
                }
                reading = null;
                if (!ReferenceEquals(doc, _doc) || doc.Tool != ViewTool.Edit) return;
            }
        }
        if (block is { } b) candidates.Add(new EditCandidate.Block(b.Block));
        var objects = doc.ContentObjectsNow(page);
        var seen = new HashSet<int>();
        foreach (var index in objects.AllAt(x, y, HitTolerance))
        {
            var group = objects.GroupOf(index);
            if (group.Count > 1 && seen.Add(-1 - group[0])) candidates.Add(new EditCandidate.Picture(group));
            if (seen.Add(index)) candidates.Add(new EditCandidate.Picture([index]));
        }
        if (!ReferenceEquals(doc, _doc) || doc.Tool != ViewTool.Edit) return;
        _codeReadPending = reading is not null;
        _fontCodeBlock = null;
        var chosen = false;
        var start = alt && candidates.Count > 0 ? (CurrentCandidate(doc, candidates) + 1) % candidates.Count : 0;
        for (var n = 0; n < candidates.Count; n++)
        {
            var candidate = candidates[(start + n) % candidates.Count];
            if (candidate is EditCandidate.Picture { Group: var group } && !alt && clickCount < 2 && doc.FillsPage(page, group))
            {
                doc.NoteScanPassedBy();
                continue;   // a scan: a single click passes it by
            }
            Choose(doc, page, x, y, candidate, pointer);
            chosen = true;
            break;
        }
        if (!chosen)
        {
            doc.EditTarget = EditTarget.None;
            doc.SelectedElement = null;
            InvalidateVisual();
        }
        _codeReadPending = false;
        if (reading is null) return;
        // the code read at the spot comes first — unless the picture chosen meanwhile is being dragged
        PageBarcode? found;
        try
        {
            found = await reading;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Core.Platform.AppLog.Write("Code lesen", e.ToString());
            return;
        }
        if (found is null && _fontCodeBlock is { } fontCode && ReferenceEquals(doc, _doc) && doc.Tool == ViewTool.Edit)
        {
            // a code in a bar code font that could not be read: from its text, or to be typed anew (phase 8.2)
            _fontCodeBlock = null;
            doc.ShowFontCode(fontCode.Page, fontCode.Block);
            InvalidateVisual();
            return;
        }
        if (found is null || !ReferenceEquals(doc, _doc) || doc.Tool != ViewTool.Edit || _imageDrag is { Moved: true } || doc.RichEditor is not null) return;
        if (_imageDrag is not null)
        {
            _imageDrag = null;
            pointer?.Capture(null);
        }
        Choose(doc, page, x, y, new EditCandidate.Code(found), pointer);
    }

    /// <summary>Whether the code at the clicked spot is still being read (a block in a bar code font then waits for it).</summary>
    private bool _codeReadPending;

    /// <summary>A block in a bar code font chosen while the code at the spot was being read: shown if no code is found there.</summary>
    private (int Page, PageTextBlock Block)? _fontCodeBlock;

    /// <summary>Index of the candidate chosen now; −1 if none of them is.</summary>
    private static int CurrentCandidate(DocumentViewModel doc, List<EditCandidate> candidates)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            var current = candidates[i] switch
            {
                EditCandidate.Own own => doc.SelectedElement is { } s && ReferenceEquals(s.Element, own.Element.Element),
                EditCandidate.Code code => doc.InspectedBarcode is { } read && ReferenceEquals(read.Code, code.Found),
                EditCandidate.Picture picture => doc.SelectedImages is { } images && images.Page < doc.Pages.Count &&
                    doc.ContentObjectsOn(images.Page) is { } objects && images.Keys.SequenceEqual(picture.Group.Select(objects.KeyOf)),
                _ => false,
            };
            if (current) return i;
        }
        return -1;
    }

    /// <summary>Makes the choice: selects the element, shows the code, edits the block or chooses the picture (and starts dragging it while the button is still down).</summary>
    private void Choose(DocumentViewModel doc, int page, double x, double y, EditCandidate candidate, IPointer? pointer)
    {
        var position = page < _pageRects.Count ? PagePointToView(page, x, y) : default;
        switch (candidate)
        {
            case EditCandidate.Own own:
                doc.EditTarget = EditTarget.None;
                doc.SelectedElement = own.Element;
                break;
            case EditCandidate.Code code:
                doc.SelectedElement = null;
                doc.ShowCode(page, code.Found);
                break;
            case EditCandidate.Block { Found.CodeFont: true } codeBlock:
                // a bar code font goes to the code tool (phase 8.2): the code read at the spot if there is one, otherwise the block's
                doc.SelectedElement = null;
                if (_codeReadPending) _fontCodeBlock = (page, codeBlock.Found);
                else doc.ShowFontCode(page, codeBlock.Found);
                break;
            case EditCandidate.Block block:
                if (block.Found.Locked is { } reason)
                {
                    doc.Notify(reason);
                    break;
                }
                if (!doc.CanEditElements)
                {
                    doc.Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
                    break;
                }
                doc.SelectedElement = null;
                EditBlock(doc, page, block.Found, position);
                break;
            case EditCandidate.Picture picture:
                doc.SelectedElement = null;
                var objects = doc.ContentObjectsNow(page);
                if (picture.Group.Any(g => g >= objects.Count)) break;
                doc.SelectedImages = new ImageSelection(page, picture.Group.Select(objects.KeyOf).ToList());
                var first = objects[picture.Group[0]];
                _ = doc.InspectImageAsync(page, x, y, first.Kind == ContentKind.Image ? first.Source : null);
                if (pointer is not null && _leftDown && SelectedImagesViewRect() is { } rect)
                {
                    BeginImageDrag(page, ToDocument(position), rect, -1);
                    pointer.Capture(this);
                }
                break;
        }
        InvalidateVisual();
    }

    /// <summary>Mouse moved in „Bearbeiten“: a drag in progress goes to its part; otherwise the block or picture under the mouse is highlighted, with the matching cursor. false = not in this mode.</summary>
    private bool EditMoved(Point position, bool shift)
    {
        if (!IsEditTool || _doc is not { } doc) return false;
        if (_imageDrag is not null) return ImageEditMoved(position, shift);
        if (_blockBand is not null) return TextEditMoved(position);
        if (doc.SelectedImages is not null && ImageHandleAt(position) is { } handle)
        {
            ClearHover();
            Cursor = HandleCursors[handle];
            return true;
        }
        // the block under the mouse comes before the picture under it, as with a click
        var block = BlockAt(position);
        if (block != _hoverBlock)
        {
            _hoverBlock = block;
            InvalidateVisual();
        }
        (int, IReadOnlyList<int>)? hover = null;
        if (block is null && PageAt(position) is { } page)
        {
            var (x, y) = ToPage(page, position);
            if (doc.ContentObjectAt(page, x, y, HitTolerance) is { } index) hover = (page, doc.ContentObjectGroup(page, index));
        }
        if (hover?.Item1 != _hoverImages?.Page || !(hover?.Item2 ?? []).SequenceEqual(_hoverImages?.Group ?? []))
        {
            _hoverImages = hover;
            InvalidateVisual();
        }
        Cursor = block is { Block.Locked: null } ? TextCursor : block is not null ? CrossCursor
            : hover is not null || SelectedImagesViewRect() is { } chosen && chosen.Contains(position) ? MoveCursor : HandCursor;
        return true;
    }

    private void ClearHover()
    {
        if (_hoverBlock is null && _hoverImages is null) return;
        _hoverBlock = null;
        _hoverImages = null;
        InvalidateVisual();
    }

    /// <summary>Esc in „Bearbeiten“: lets go of the code that is shown (the tool stays on). true = handled.</summary>
    private bool EditKey(KeyEventArgs e)
    {
        if (!IsEditTool || _doc is not { InspectedBarcode: not null } doc || e.Key != Key.Escape) return false;
        doc.EndBarcode();
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// Right-click in the reading tools on text, a picture or a code of the page: a menu with „Bearbeiten“, which switches the tool on and
    /// chooses what was clicked. true = the menu is shown.
    /// </summary>
    private bool EditMenu(Point position)
    {
        if (_doc is not { Tool: ViewTool.SelectText or ViewTool.Pan } doc || PageAt(position) is not { } page || !doc.CanEditElements) return false;
        var (x, y) = ToPage(page, position);
        var onContent = TextAt(position) is not null || doc.ContentObjectsOn(page) is { } objects && objects.At(x, y, HitTolerance) is not null;
        if (!onContent) return false;
        var edit = new Avalonia.Controls.MenuItem { Header = Tr.T("Bearbeiten") };
        if (this.TryFindResource("Icon.Edit", out var data) && data is Avalonia.Media.Geometry geometry)
            edit.Icon = new Avalonia.Controls.PathIcon { Data = geometry, Width = 16, Height = 16 };
        edit.Click += (_, _) =>
        {
            if (!ReferenceEquals(doc, _doc)) return;
            // „Bearbeiten“ can be switched off by the function file of the organization, also from this menu
            if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main
                && !main.EnsureAllowed("start.edit")) return;
            doc.Tool = ViewTool.Edit;
            // the tool's panel opens and the page fits beside it: the choice waits for that layout and finds the spot on the page again
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(doc, _doc) || doc.Tool != ViewTool.Edit || page >= _pageRects.Count) return;
                _ = ChooseAtGuardedAsync(doc, page, x, y, alt: false, clickCount: 2, pointer: null);   // from the menu: no dragging
            }, Avalonia.Threading.DispatcherPriority.Background);
        };
        var menu = new Avalonia.Controls.ContextMenu { ItemsSource = new List<Avalonia.Controls.Control> { edit }, Placement = Avalonia.Controls.PlacementMode.Pointer };
        menu.Open(this);
        return true;
    }


    private static readonly IBrush LayerLabelBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x3D, 0x7E, 0xFF));
    private static readonly IBrush LayerLabelBackground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));

    /// <summary>
    /// The small label „Ebene 2 von 3“ above the frame of what is chosen in „Bearbeiten“ (like the names of fields in „Formular bearbeiten“):
    /// where it lies among what overlaps it, so that „nach vorn“ and „nach hinten“ can be followed.
    /// </summary>
    private void DrawLayerLabel(Avalonia.Media.DrawingContext context)
    {
        if (!IsEditTool || _doc is not { } doc || _imageDrag is { Moved: true } || doc.LayerLabel() is not { } label) return;
        Rect? frame = doc.SelectedImages is not null && doc.SelectedElement is null ? SelectedImagesViewRect()
            : doc.SelectedElement is { } selected && selected.Page < _pageRects.Count && _shown[selected.Page] ? ViewRect(selected.Page, selected.Element.Rect) : null;
        if (frame is not { } rect) return;
        var size = Math.Clamp(9 * Math.Sqrt(Scale), 8, 12);
        var text = new Avalonia.Media.FormattedText(label, System.Globalization.CultureInfo.CurrentCulture, Avalonia.Media.FlowDirection.LeftToRight,
            new Avalonia.Media.Typeface(Avalonia.Media.FontFamily.Default), size, LayerLabelBrush);
        // above the frame; at the top of the view below it
        var top = rect.Top - text.Height - 3 >= 0 ? rect.Top - text.Height - 3 : rect.Bottom + 3;
        var back = new Rect(rect.Left, top, text.Width + 6, text.Height + 2);
        context.FillRectangle(LayerLabelBackground, back);
        context.DrawText(text, back.TopLeft + new Point(3, 1));
    }
}
