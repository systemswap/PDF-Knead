using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;

namespace PdfKnead.ViewModels;

/// <summary>How to change an element's layer. See KONZEPT.md, section 5.13.</summary>
public enum LayerMove
{
    ToFront,
    Forward,
    Backward,
    ToBack,
}

/// <summary>
/// Layers: elements are stacked in the order of their list (the last one on top); the display shows them that way and the writer
/// writes /Annots in that order. Forward and backward work as in office applications: one step moves past the next shape the
/// element overlaps (otherwise past the next one). For form fields this order is also the tab order, so it changes as well (a
/// notice says so).
/// </summary>
public sealed partial class DocumentViewModel
{
    /// <summary>Whether layer numbers are shown on the elements (1 = backmost), like the tab order numbers.</summary>
    public bool ShowLayers { get; private set; }

    /// <summary>Switches the layer numbers on or off.</summary>
    /// <param name="on">true = show them.</param>
    public void SetShowLayers(bool on)
    {
        if (ShowLayers == on) return;
        ShowLayers = on;
        OnPropertyChanged(nameof(ShowLayers));
    }

    /// <summary>
    /// Selected elements: in „Formular bearbeiten“ mode all selected fields, otherwise the selected element. Form fields only count in
    /// design mode (as with moving and deleting: while filling in, fields stay where they are).
    /// </summary>
    private IReadOnlyList<ElementRef> LayerSelection =>
        IsFieldDesign && FieldSelection.Count > 0 ? FieldSelection : SelectedElement is { } selected && (FieldsMovable || !IsFormField(selected.Element)) ? [selected] : [];

    public bool CanMoveLayer => (LayerSelection.Count > 0 || ContentLayerSelection) && CanEditElements;

    /// <summary>Whether the layer buttons move chosen objects of the page content („Bearbeiten“, KONZEPT.md, section 5.22 „Ebenen“).</summary>
    private bool ContentLayerSelection => Tool == ViewTool.Edit && SelectedImages is not null && SelectedElement is null;

    /// <summary>
    /// Moves the chosen objects of the page content in the drawing order (one undo step); they stay chosen at their new place. Says why
    /// nothing changes (already in front or behind at their spot).
    /// </summary>
    /// <param name="move">Direction and extent.</param>
    /// <returns>Whether the page changed.</returns>
    public async Task<bool> RestackSelectedImagesAsync(LayerMove move)
    {
        if (SelectedImages is not { } s || (uint)s.Page >= (uint)History.Count) return false;
        if (!CanEditElements)
        {
            Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
            return false;
        }
        var item = History.Pages[s.Page];
        var found = ContentObjectsNow(s.Page);
        var to = move switch
        {
            LayerMove.ToFront => ContentLayerMove.ToFront,
            LayerMove.Forward => ContentLayerMove.Forward,
            LayerMove.Backward => ContentLayerMove.Backward,
            _ => ContentLayerMove.ToBack,
        };
        (PageItem Item, IReadOnlyList<ContentKey> Keys)? result;
        try
        {
            result = await WorkAsync(() => Task.Run(() =>
            {
                using var use = Renderer.Use(item.Source);
                return use is null ? null : ContentObjectEditor.Restack(item, s.Keys, to, found);
            }));
        }
        catch (Exception e) when (e is Core.Pdf.PdfException or ArgumentException)
        {
            Notify(e.Message);
            return false;
        }
        if (result is not { } done)
        {
            Notify(move is LayerMove.ToFront or LayerMove.Forward
                ? Tr.T("Davor liegt hier nichts mehr — es liegt schon ganz vorn im Seiteninhalt.")
                : Tr.T("Dahinter liegt hier nichts mehr — es liegt schon ganz hinten im Seiteninhalt."));
            return false;
        }
        if ((uint)s.Page >= (uint)History.Count || !History.ReplacePage(s.Page, item, done.Item, move switch
            {
                LayerMove.ToFront => Tr.T("In den Vordergrund"),
                LayerMove.Forward => Tr.T("Eine Ebene nach vorn"),
                LayerMove.Backward => Tr.T("Eine Ebene nach hinten"),
                _ => Tr.T("In den Hintergrund"),
            })) return false;
        await ContentObjectsOnAsync(s.Page);
        if ((uint)s.Page < (uint)History.Count && ReferenceEquals(History.Pages[s.Page], done.Item)) SelectedImages = new ImageSelection(s.Page, done.Keys);
        return true;
    }

    private (object? Selection, PageItem? Item, string? Text) _layerLabel;

    /// <summary>
    /// The layer of what is chosen in „Bearbeiten“ at its spot, for the small label beside the frame: „Ebene 2 von 3“ — counted among the
    /// objects of the page content and the elements that overlap it (1 = backmost; elements of PDF Knead lie in front of the page content).
    /// </summary>
    /// <returns>The text; null if nothing is chosen, or only one thing lies there.</returns>
    public string? LayerLabel()
    {
        if (Tool != ViewTool.Edit) return null;
        object? selection = SelectedImages is { } images && SelectedElement is null ? images : SelectedElement;
        var page = selection switch { ImageSelection i => i.Page, ElementRef r => r.Page, _ => -1 };
        if ((uint)page >= (uint)History.Count) return null;
        var item = History.Pages[page];
        if (ReferenceEquals(_layerLabel.Selection, selection) && ReferenceEquals(_layerLabel.Item, item)) return _layerLabel.Text;
        var objects = ContentObjectsOn(page);
        int below = 0, above = 0;
        RectD box;
        if (selection is ImageSelection chosen)
        {
            if (objects is null) return null;
            var indices = chosen.Keys.Select(objects.IndexOf).OfType<int>().ToHashSet();
            if (indices.Count == 0) return null;
            box = indices.Select(objects.VisibleOf).Aggregate((a, b) => a.Union(b));
            var top = indices.Max();
            for (var i = 0; i < objects.Count; i++)
                if (!indices.Contains(i) && objects.VisibleOf(i).Intersect(box) is { IsEmpty: false })
                    if (i < top) below++;
                    else above++;
            above += item.Elements.Count(e => e is not RedactionElement && Overlaps(e, box));
        }
        else
        {
            var element = ((ElementRef)selection!).Element;
            box = RectD.FromLtrb(element.Rect.Left, element.Rect.Bottom, element.Rect.Right, element.Rect.Top);
            var at = -1;
            for (var i = 0; i < item.Elements.Count && at < 0; i++)
                if (ReferenceEquals(item.Elements[i], element)) at = i;
            if (at < 0) return null;
            for (var i = 0; i < item.Elements.Count; i++)
                if (i != at && item.Elements[i] is not RedactionElement && Overlaps(item.Elements[i], box))
                    if (i < at) below++;
                    else above++;
            if (objects is not null)
                for (var i = 0; i < objects.Count; i++)
                    if (objects.VisibleOf(i).Intersect(box) is { IsEmpty: false }) below++;
        }
        var text = below + above == 0 ? null : Tr.T("Ebene {0} von {1}", below + 1, below + above + 1);
        _layerLabel = (selection, item, text);
        return text;
    }

    private static bool Overlaps(PageElement e, RectD box) =>
        RectD.FromLtrb(e.Rect.Left, e.Rect.Bottom, e.Rect.Right, e.Rect.Top).Intersect(box) is { IsEmpty: false };

    /// <summary>Moves the selected elements one layer, or all the way, to the front or back (one undo step).</summary>
    /// <param name="move">Direction and extent of the move.</param>
    /// <returns>false if nothing changes.</returns>
    public bool MoveLayer(LayerMove move)
    {
        // chosen objects of the page content: moved in the content's drawing order
        if (ContentLayerSelection)
        {
            _ = RestackSelectedImagesAsync(move);
            return true;
        }
        var selection = LayerSelection;
        if (selection.Count == 0 || !CanEditElements) return false;
        var pages = History.Pages.ToArray();
        var changed = false;
        var fieldMoved = false;
        foreach (var group in selection.GroupBy(r => r.Page))
        {
            if (group.Key < 0 || group.Key >= pages.Length) continue;
            var before = pages[group.Key].Elements;
            var chosen = new HashSet<PageElement>(group.Select(r => r.Element), ReferenceEqualityComparer.Instance);
            var after = Reorder(before, chosen, move);
            if (after.SequenceEqual(before, ReferenceEqualityComparer.Instance)) continue;
            pages[group.Key] = pages[group.Key].WithElements(after);
            changed = true;
            fieldMoved |= chosen.Any(IsFormField) && TabOrderChanged(before, after);
        }
        if (!changed) return false;
        History.Apply(move switch
        {
            LayerMove.ToFront => Tr.T("In den Vordergrund"),
            LayerMove.Forward => Tr.T("Eine Ebene nach vorn"),
            LayerMove.Backward => Tr.T("Eine Ebene nach hinten"),
            _ => Tr.T("In den Hintergrund"),
        }, pages);
        if (fieldMoved) Notify(Tr.T("Bei Formularfeldern ist die Ebene zugleich die Tab-Reihenfolge — sie hat sich mitgeändert (einstellen unter Formular → Tab-Reihenfolge)."));
        return true;
    }

    /// <summary>New order of a page's elements; the chosen ones keep their order among themselves.</summary>
    internal static List<PageElement> Reorder(IReadOnlyList<PageElement> elements, IReadOnlySet<PageElement> chosen, LayerMove move)
    {
        var list = elements.ToList();
        switch (move)
        {
            case LayerMove.ToFront:
                return [.. list.Where(e => !chosen.Contains(e)), .. list.Where(chosen.Contains)];
            case LayerMove.ToBack:
                return [.. list.Where(chosen.Contains), .. list.Where(e => !chosen.Contains(e))];
            case LayerMove.Forward:
                // From top to bottom: each chosen element moves past the next unchosen one it overlaps (otherwise past the next one)
                for (var i = list.Count - 2; i >= 0; i--)
                {
                    if (!chosen.Contains(list[i])) continue;
                    var target = Next(list, chosen, i, +1);
                    if (target < 0) continue;
                    var element = list[i];
                    list.RemoveAt(i);
                    list.Insert(target, element);   // after the removal the skipped element sits one index lower, so this lands directly above it
                }
                return list;
            default:
                for (var i = 1; i < list.Count; i++)
                {
                    if (!chosen.Contains(list[i])) continue;
                    var target = Next(list, chosen, i, -1);
                    if (target < 0) continue;
                    var element = list[i];
                    list.RemoveAt(i);
                    list.Insert(target, element);
                }
                return list;
        }
    }

    /// <summary>Index of the next unchosen element in direction <paramref name="step"/> that overlaps the element; otherwise the next unchosen one; −1 = none.</summary>
    private static int Next(List<PageElement> list, IReadOnlySet<PageElement> chosen, int at, int step)
    {
        var rect = list[at].Rect;
        var first = -1;
        for (var j = at + step; j >= 0 && j < list.Count; j += step)
        {
            if (chosen.Contains(list[j])) continue;
            if (first < 0) first = j;
            if (Overlaps(rect, list[j].Rect)) return j;
        }
        return first;
    }

    private static bool Overlaps(PdfRectangle a, PdfRectangle b) =>
        a.Left < b.Right && b.Left < a.Right && a.Bottom < b.Top && b.Bottom < a.Top;

    private static bool TabOrderChanged(IReadOnlyList<PageElement> before, IReadOnlyList<PageElement> after) =>
        !before.Where(IsFormField).SequenceEqual(after.Where(IsFormField), ReferenceEqualityComparer.Instance);

    /// <summary>Layer of an element on its page (1 = backmost).</summary>
    /// <param name="page">Page index.</param>
    /// <param name="element">The element.</param>
    /// <returns>The layer number, or 0 if the element is not on that page.</returns>
    public int LayerOf(int page, PageElement element)
    {
        var elements = ElementsOn(page);
        for (var i = 0; i < elements.Count; i++)
            if (ReferenceEquals(elements[i], element)) return i + 1;
        return 0;
    }
}
