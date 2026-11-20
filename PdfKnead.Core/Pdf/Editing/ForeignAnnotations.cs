using System.Runtime.CompilerServices;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Annotations and fields of other programs as elements: everything visible on the page with its own appearance — not
/// popups (they appear only on click), links (invisible areas), hidden annotations or own elements. They are drawn above the
/// tiles like own elements and can be moved and deleted.
/// </summary>
public static class ForeignAnnotations
{
    private static readonly PdfName Annots = PdfName.Get("Annots"), Rect = PdfName.Get("Rect"), Ap = PdfName.Get("AP"),
        As = PdfName.Get("AS"), F = PdfName.Get("F"), Oc = PdfName.Get("OC"), Popup = PdfName.Get("Popup"), Irt = PdfName.Get("IRT"),
        Parent = PdfName.Get("Parent"), InkList = PdfName.Get("InkList"), QuadPoints = PdfName.Get("QuadPoints"),
        Vertices = PdfName.Get("Vertices"), L = PdfName.Get("L"), Cl = PdfName.Get("CL");

    private sealed class PageAnnotations(List<ForeignAnnotation> list)
    {
        public readonly List<ForeignAnnotation> List = list;
        public readonly HashSet<PdfDictionary> Originals = new(list.Select(a => a.Original), ReferenceEqualityComparer.Instance);
    }

    private static readonly ConditionalWeakTable<PdfDictionary, PageAnnotations> Pages = new();

    /// <summary>Gets the page's annotations of other programs as elements, in the order of /Annots (read once per page).</summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <returns>The elements; empty if there are none.</returns>
    public static IReadOnlyList<ForeignAnnotation> For(PdfDocument doc, PdfPage page) => Of(doc, page).List;

    /// <summary>Determines whether an annotation is an element of the page from another program (it is then not drawn in the tiles).</summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="annotation">The annotation dictionary.</param>
    /// <returns><see langword="true"/> if the annotation is one of the elements from <see cref="For"/>.</returns>
    public static bool IsElement(PdfDocument doc, PdfPage page, PdfDictionary annotation) =>
        Of(doc, page).Originals.Contains(annotation);

    private static PageAnnotations Of(PdfDocument doc, PdfPage page) =>
        Pages.GetValue(page.Dictionary, _ => new PageAnnotations(Read(doc, page)));

    /// <summary>
    /// From this number of annotations of other programs on a page, the unchanged ones are part of the tiles (like page
    /// content, free when scrolling) and only moved ones are drawn above them; drawn individually above the tiles, 1 000 of
    /// them already cost about 9 ms per frame.
    /// </summary>
    public const int TiledFrom = 200;

    /// <summary>Determines whether the page has so many annotations of other programs that the unchanged ones are part of the tiles.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <returns><see langword="true"/> from <see cref="TiledFrom"/> annotations.</returns>
    public static bool Tiled(PdfDocument doc, PdfPage page) => For(doc, page).Count >= TiledFrom;

    private static List<ForeignAnnotation> Read(PdfDocument doc, PdfPage page)
    {
        var list = new List<ForeignAnnotation>();
        if (page.Dictionary.Get(Annots) is not PdfArray annots) return list;
        var optional = OptionalContent.For(doc);
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < annots.Count; i++)
        {
            try
            {
                if (annots.Get(i) is not PdfDictionary annot || !IsCandidate(doc, page, annot, optional)) continue;
                if (!seen.Add(annot)) continue;   // broken file: the same annotation twice in /Annots — one element
                if (PdfRectangle.From(annot.Get(Rect)) is not { } rect || !Sane(rect)) continue;
                list.Add(new ForeignAnnotation
                {
                    Rect = rect,
                    Document = doc,
                    Original = annot,
                    OriginalRect = rect,
                    Kind = annot.GetName(PdfName.Subtype)?.Value ?? "",
                });
            }
            catch (Exception e) when (e is PdfException or InvalidDataException or IOException or ArgumentException)
            {
                // unreadable: stays in the page as it is
            }
        }
        return list;
    }

    /// <summary>Visible annotation of another program with an appearance (as the display draws it).</summary>
    private static bool IsCandidate(PdfDocument doc, PdfPage page, PdfDictionary annot, OptionalContent optional)
    {
        if (ElementStore.IsOwn(doc, page, annot)) return false;
        var kind = annot.GetName(PdfName.Subtype)?.Value;
        if (kind is null or "Popup" or "Link") return false;
        if (((annot.GetInteger(F) ?? 0) & (2 | 32)) != 0) return false;   // Hidden, NoView
        if (optional.IsHidden(annot.Get(Oc), null)) return false;
        // With an appearance; form fields also without one (check box without an appearance for "off", forms with NeedAppearances)
        return Appearance(annot) is not null || kind == "Widget" && FormField.Of(annot) is not null;
    }

    /// <summary>
    /// Gets the text of the page's annotations of other programs (free text, field values …) at their current position. The
    /// display draws them above the tiles or in a separate layer, so they are missing from the page's display list. Annotations
    /// with the same appearance and size are read once.
    /// </summary>
    /// <param name="item">The page with its elements.</param>
    /// <param name="cancel">Cancels the reading.</param>
    /// <param name="maxGlyphs">At most this many characters (together with the page text no more than the page limit,
    /// <see cref="DisplayListBuilder.MaxTextGlyphs"/>): a shared appearance with many characters, copied for 1 000 annotations,
    /// otherwise produced billions (found by fuzzing: out of memory).</param>
    /// <returns>The glyphs in page space; an unreadable annotation contributes none.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was triggered.</exception>
    public static TextGlyph[] Text(PageItem item, CancellationToken cancel = default, int maxGlyphs = DisplayListBuilder.MaxTextGlyphs)
    {
        List<TextGlyph>? all = null;
        Dictionary<ElementAppearance, TextGlyph[]>? read = null;
        foreach (var element in item.Elements)
        {
            if (element is not ForeignAnnotation foreign) continue;
            cancel.ThrowIfCancellationRequested();
            var shape = ElementAppearance.For(foreign).Shape;
            read ??= new Dictionary<ElementAppearance, TextGlyph[]>(ReferenceEqualityComparer.Instance);
            if (!read.TryGetValue(shape, out var glyphs))
            {
                try
                {
                    glyphs = PageInterpreter.BuildAnnotations(shape.Source, item.Page, [shape.Annotation], cancel, PageInterpreter.MaxAnnotationOperations).Text;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    glyphs = [];   // broken annotation: the rest of the page's text stays
                }
                read[shape] = glyphs;
            }
            if (glyphs.Length == 0) continue;
            var from = shape.Element.Rect;
            var (dx, dy) = ((float)(foreign.Rect.Left - from.Left), (float)(foreign.Rect.Bottom - from.Bottom));
            all ??= [];
            foreach (var g in glyphs)
            {
                if (all.Count >= maxGlyphs) return [.. all];
                all.Add(g with { X0 = g.X0 + dx, Y0 = g.Y0 + dy, X1 = g.X1 + dx, Y1 = g.Y1 + dy, X2 = g.X2 + dx, Y2 = g.Y2 + dy, X3 = g.X3 + dx, Y3 = g.Y3 + dy });
            }
        }
        return all is null ? [] : [.. all];
    }

    /// <summary>Gets the normal appearance (for appearance states the current one, /AS).</summary>
    /// <param name="annot">The annotation dictionary.</param>
    /// <returns>The appearance stream; <see langword="null"/> if there is none.</returns>
    public static PdfStream? Appearance(PdfDictionary annot)
    {
        var normal = annot.GetDictionary(Ap)?.Get(PdfName.N);
        if (normal is PdfDictionary states)
            normal = annot.GetName(As) is { } state ? states.Get(state) : null;
        return normal as PdfStream;
    }

    private static bool Sane(PdfRectangle r) =>
        Math.Abs(r.Left) <= 1e6 && Math.Abs(r.Right) <= 1e6 && Math.Abs(r.Bottom) <= 1e6 && Math.Abs(r.Top) <= 1e6;

    /// <summary>
    /// Creates a copy of the annotation moved by (<paramref name="dx"/>, <paramref name="dy"/>): /Rect and all points
    /// (/InkList, /QuadPoints, /Vertices, /L, /CL); everything else — appearance, references — stays the same.
    /// </summary>
    /// <param name="annot">The annotation dictionary.</param>
    /// <param name="dx">Horizontal offset in points.</param>
    /// <param name="dy">Vertical offset in points.</param>
    /// <returns>The shallow copy; arrays that contain anything other than numbers stay unchanged.</returns>
    public static PdfDictionary Moved(PdfDictionary annot, double dx, double dy)
    {
        var copy = new PdfDictionary(annot.Count) { Resolver = annot.Resolver };
        foreach (var (key, _) in annot.Entries) copy.Set(key, annot.GetRaw(key)!);
        foreach (var key in new[] { Rect, QuadPoints, Vertices, L, Cl })
            if (annot.Get(key) is PdfArray points && Shifted(points, dx, dy) is { } shifted) copy.Set(key, shifted);
        if (annot.Get(InkList) is PdfArray strokes)
        {
            var moved = new PdfArray { Resolver = strokes.Resolver };
            for (var s = 0; s < strokes.Count; s++)
                moved.Add(strokes.Get(s) is PdfArray stroke && Shifted(stroke, dx, dy) is { } shifted ? shifted : strokes[s]);
            copy.Set(InkList, moved);
        }
        return copy;
    }

    /// <summary>Number pairs (x, y) shifted; null if not all entries are numbers.</summary>
    private static PdfArray? Shifted(PdfArray points, double dx, double dy)
    {
        var result = new PdfArray();
        for (var i = 0; i < points.Count; i++)
        {
            double v;
            switch (points.Get(i))
            {
                case PdfInteger n: v = n.Value; break;
                case PdfReal r: v = r.Value; break;
                default: return null;
            }
            result.Add(new PdfReal(v + (i % 2 == 0 ? dx : dy)));
        }
        return result;
    }

    /// <summary>
    /// Determines what is removed together with deleted annotations: their popups and replies (/IRT), including replies to
    /// replies.
    /// </summary>
    /// <param name="annots">The page's /Annots array.</param>
    /// <param name="deleted">The annotations to delete.</param>
    /// <returns>The deleted annotations and their companions (at most 16 levels deep).</returns>
    public static HashSet<PdfDictionary> WithCompanions(PdfArray annots, IEnumerable<PdfDictionary> deleted)
    {
        var all = new HashSet<PdfDictionary>(deleted, ReferenceEqualityComparer.Instance);
        for (var round = 0; round < 16; round++)
        {
            var added = false;
            foreach (var a in all.ToList())
                if (a.Get(Popup) is PdfDictionary popup && all.Add(popup)) added = true;
            for (var i = 0; i < annots.Count; i++)
                if (annots.Get(i) is PdfDictionary a && !all.Contains(a) &&
                    (a.Get(Irt) is PdfDictionary irt && all.Contains(irt) ||
                     a.GetName(PdfName.Subtype)?.Value == "Popup" && a.Get(Parent) is PdfDictionary p && all.Contains(p)))
                {
                    all.Add(a);
                    added = true;
                }
            if (!added) break;
        }
        return all;
    }

    /// <summary>Gets the popup of an annotation (it moves along when the annotation is moved).</summary>
    /// <param name="annot">The annotation dictionary.</param>
    /// <returns>The popup; <see langword="null"/> if there is none.</returns>
    public static PdfDictionary? PopupOf(PdfDictionary annot) => annot.Get(Popup) as PdfDictionary;

    /// <summary>Gets the reference to the popup (for replacing it on saving).</summary>
    /// <param name="annot">The annotation dictionary.</param>
    /// <returns>The reference; <see langword="null"/> if the popup is missing or direct.</returns>
    public static PdfReference? PopupReference(PdfDictionary annot) => annot.GetRaw(Popup) as PdfReference;
}
