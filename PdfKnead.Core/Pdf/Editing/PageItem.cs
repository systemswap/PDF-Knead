using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// A page in the edited document: a page of a source file (the opened document, an inserted file or a blank page) with its
/// rotation and the elements placed on it. Immutable: editing creates new entries, and undo restores the old ones.
/// </summary>
public sealed class PageItem
{
    private static int _lastId;

    /// <summary>
    /// Creates the entry for a page of a file, with the editable elements PDF Knead stored there (<see cref="ElementStore"/>,
    /// <see cref="ContentElements"/>) and the annotations of other programs (<see cref="ForeignAnnotations"/>).
    /// </summary>
    /// <param name="source">The document.</param>
    /// <param name="page">The page.</param>
    public PageItem(PdfDocument source, PdfPage page) : this(Load(source, page), Interlocked.Increment(ref _lastId)) { }

    private PageItem((PdfDocument Source, PdfPage Page, IReadOnlyList<PageElement> Elements, IReadOnlySet<int> Replaced) loaded, int id)
        : this(loaded.Source, loaded.Page, loaded.Elements, id) => ReplacedObjects = loaded.Replaced;

    /// <summary>
    /// Page with its elements; stored edited text (<see cref="ContentElements"/>) is taken out of the content and becomes a block
    /// again — the page without it is then held in memory.
    /// </summary>
    private static (PdfDocument, PdfPage, IReadOnlyList<PageElement>, IReadOnlySet<int>) Load(PdfDocument source, PdfPage page)
    {
        var elements = ElementsOf(source, page);
        if (ContentElements.Extract(source, page) is not { } found) return (source, page, elements, new HashSet<int>());
        var stripped = new PdfPage(page.Index, found.Page.Page, page.Id, found.Page.Resources, page.MediaBox, page.CropBox, page.Rotate);
        return (source, stripped, [.. elements, .. found.Elements], found.Page.ReplacedObjects);
    }

    /// <summary>Annotations of other programs (at the bottom, in their order), the own elements above them.</summary>
    private static IReadOnlyList<PageElement> ElementsOf(PdfDocument source, PdfPage page)
    {
        var foreign = ForeignAnnotations.For(source, page);
        var own = ElementStore.For(source, page).Elements;
        return foreign.Count == 0 ? own : own.Count == 0 ? foreign : [.. foreign, .. own];
    }

    private PageItem(PdfDocument source, PdfPage page, IReadOnlyList<PageElement> elements, int id)
    {
        Source = source;
        Page = page;
        Elements = elements;
        Id = id;
    }

    /// <summary>The source document of the page.</summary>
    public PdfDocument Source { get; }

    /// <summary>Page of the source; <see cref="PdfPage.Rotate"/> is the rotation with which it is saved.</summary>
    public PdfPage Page { get; }

    /// <summary>Elements placed on the page (text, signatures …), in drawing order; do not modify.</summary>
    public IReadOnlyList<PageElement> Elements { get; }

    private bool? _tiled;
    private IReadOnlyList<PdfDictionary>? _tiledAnnotations;
    private IReadOnlyList<PageElement>? _overlay;

    /// <summary>Page with many annotations of other programs: the unchanged ones are part of the tiles (<see cref="ForeignAnnotations.TiledFrom"/>).</summary>
    public bool HasTiledAnnotations => _tiled ??= ForeignAnnotations.Tiled(Source, Page);

    /// <summary>Determines whether an element is part of the tiles (an unchanged annotation of another program on a page with many).</summary>
    /// <param name="element">The element.</param>
    /// <returns><see langword="true"/> if the tiles draw it.</returns>
    public bool InTiles(PageElement element) => element is ForeignAnnotation { IsChanged: false } && HasTiledAnnotations;

    /// <summary>Annotations of other programs that the tiles draw, in their order; null if all are drawn above the tiles.</summary>
    public IReadOnlyList<PdfDictionary>? TiledAnnotations =>
        !HasTiledAnnotations ? null : _tiledAnnotations ??= [.. Elements.Where(InTiles).Select(e => ((ForeignAnnotation)e).Original)];

    /// <summary>Elements the display draws above the tiles (all except those in the tiles).</summary>
    public IReadOnlyList<PageElement> OverlayElements =>
        !HasTiledAnnotations ? Elements : _overlay ??= [.. Elements.Where(e => !InTiles(e))];

    /// <summary>
    /// Key for tiles and thumbnails: the same identifier means the same appearance of the page itself. A rotated page gets a new
    /// one; changed elements do not, because the view draws them above the tiles (otherwise every text change would re-render a
    /// whole large plan). See docs/FEHLERGEDAECHTNIS.md, No. 27.
    /// </summary>
    public int Id { get; }

    /// <summary>Returns the page rotated clockwise by <paramref name="degrees"/>, with a new identifier.</summary>
    /// <param name="degrees">A multiple of 90; negative values rotate anticlockwise.</param>
    /// <returns>The rotated page.</returns>
    public PageItem Rotated(int degrees) =>
        new(Source, Page.WithRotate(((Page.Rotate + degrees) % 360 + 360) % 360), Elements, Interlocked.Increment(ref _lastId))
        {
            Replaces = Replaces,
            ReplacedObjects = ReplacedObjects,
            AlternateTextMcids = AlternateTextMcids,
            ObjectEdits = ObjectEdits,
        };

    /// <summary>Returns the page with another page dictionary (its scales, /VP), with a new identifier.</summary>
    /// <param name="dictionary">The new page dictionary; content, resources and boxes stay.</param>
    /// <returns>The new entry.</returns>
    public PageItem WithPageDictionary(PdfDictionary dictionary) =>
        new(Source, Page.WithDictionary(dictionary), Elements, Interlocked.Increment(ref _lastId))
        {
            Replaces = Replaces,
            ReplacedObjects = ReplacedObjects,
            AlternateTextMcids = AlternateTextMcids,
            ObjectEdits = ObjectEdits,
        };

    /// <summary>Returns the page with other elements and the same identifier.</summary>
    /// <param name="elements">The new elements.</param>
    /// <returns>The new entry.</returns>
    /// <remarks>Replaced originals and the replaced page are carried over; otherwise even the first typed character would drop
    /// them, and the old text could remain in the file through shared resources.</remarks>
    public PageItem WithElements(IReadOnlyList<PageElement> elements) =>
        new(Source, Page, elements, Id) { Replaces = Replaces, ReplacedObjects = ReplacedObjects, AlternateTextMcids = AlternateTextMcids, ObjectEdits = ObjectEdits };

    /// <summary>
    /// Edits of images in the page content (see KONZEPT.md, section 5.20; <see cref="ContentObjectEditor"/>); null if there are none. Other
    /// changes of the content (edited text, redaction) take them in: the page they produce is the new starting point.
    /// </summary>
    public ContentObjectEdits? ObjectEdits { get; private init; }

    /// <summary>The same page with its current content as the new starting point of image edits (after an image was replaced: it is an ordinary image now).</summary>
    internal PageItem Rebased() =>
        new(Source, Page, Elements, Id)
        {
            Replaces = Replaces,
            ReplacedObjects = ReplacedObjects,
            AlternateTextMcids = AlternateTextMcids,
            ObjectEdits = new ContentObjectEdits(Page, new Dictionary<ContentKey, Graphics.Matrix>()),
        };

    /// <summary>The page with a content of its own (objects pasted into it): a new identifier, the image edits start from it.</summary>
    /// <param name="dict">The new page dictionary.</param>
    /// <param name="resources">Its resources.</param>
    /// <returns>The new entry.</returns>
    internal PageItem WithContent(PdfDictionary dict, PdfDictionary resources)
    {
        var page = new PdfPage(Page.Index, dict, Page.Id, resources, Page.MediaBox, Page.CropBox, Page.Rotate);
        return new PageItem(Source, page, Elements, Interlocked.Increment(ref _lastId))
        {
            Replaces = Replaces,
            ReplacedObjects = ReplacedObjects,
            AlternateTextMcids = AlternateTextMcids,
        }.Rebased();
    }

    /// <summary>The page with these image edits applied to their starting page: new content and a new identifier, so the tiles show it.</summary>
    /// <param name="edits">The edits (all of them, from <see cref="ContentObjectEdits.Base"/>).</param>
    /// <returns>The new entry.</returns>
    public PageItem WithObjectEdits(ContentObjectEdits edits)
    {
        var (dict, resources, dropped) = ContentObjectEditor.Apply(Source, edits);
        var page = new PdfPage(Page.Index, dict, Page.Id, resources, Page.MediaBox, Page.CropBox, Page.Rotate);
        return new PageItem(Source, page, Elements, Interlocked.Increment(ref _lastId))
        {
            Replaces = Replaces,
            // deleted images: kept in the file only if another page still draws them
            ReplacedObjects = dropped.Count == 0 ? ReplacedObjects : new HashSet<int>(ReplacedObjects.Concat(dropped)),
            AlternateTextMcids = AlternateTextMcids,
            ObjectEdits = edits,
        };
    }

    /// <summary>
    /// Makes this page replace <paramref name="original"/> on saving (for example when saved as an image): references to the
    /// old page (bookmarks, links, structure) then point to it. Without elements, because they are already part of the image.
    /// </summary>
    /// <param name="original">The page being replaced.</param>
    /// <returns>The new entry with a new identifier.</returns>
    public PageItem StandingFor(PageItem original) =>
        new(Source, Page, [], Interlocked.Increment(ref _lastId))
        {
            Replaces = original.Page.Id is { } id ? (original.Source, id) : null,
            ReplacedObjects = original.ReplacedObjects,
            AlternateTextMcids = original.AlternateTextMcids,
        };

    /// <summary>Page that this one replaces on saving (see <see cref="StandingFor"/>); null = none.</summary>
    public (PdfDocument Source, PdfObjectId Id)? Replaces { get; private init; }

    /// <summary>
    /// Edited text (see KONZEPT.md, section 5.14): the page without the characters of a block (<see cref="Redactor.RemoveGlyphs"/>),
    /// with the new block among the elements — new content and a new identifier, so that the tiles no longer show the old text.
    /// </summary>
    /// <param name="removed">The page content without the old text.</param>
    /// <param name="elements">The elements including the new block.</param>
    /// <returns>The new entry.</returns>
    public PageItem WithEditedPage(Redactor.HiddenContentResult removed, IReadOnlyList<PageElement> elements)
    {
        var old = Page;
        var page = new PdfPage(old.Index, removed.Page, old.Id, removed.Resources, old.MediaBox, old.CropBox, old.Rotate);
        return new PageItem(Source, page, elements, Interlocked.Increment(ref _lastId))
        {
            Replaces = Replaces,
            ReplacedObjects = ReplacedObjects.Count == 0 ? removed.ReplacedObjects : new HashSet<int>(ReplacedObjects.Concat(removed.ReplacedObjects)),
            AlternateTextMcids = AlternateTextMcids.Count == 0 ? removed.AlternateTextMcids : new HashSet<int>(AlternateTextMcids.Concat(removed.AlternateTextMcids)),
        };
    }

    /// <summary>
    /// Replaced code (see KONZEPT.md, section 5.19): the page with an area emptied as by <see cref="Redactor.Apply"/> (white), and the
    /// elements — without annotations of other programs that went with the area (a link over the old code would lead to its old address).
    /// </summary>
    /// <param name="emptied">The page with the area emptied.</param>
    /// <param name="elements">The elements including the new code.</param>
    /// <returns>The new entry.</returns>
    public PageItem WithEmptiedArea(RedactionResult emptied, IReadOnlyList<PageElement> elements)
    {
        var removed = new Redactor.HiddenContentResult(emptied.Page, emptied.Resources, emptied.RemovedGlyphs, emptied.RemovedPaths)
        {
            ReplacedObjects = emptied.ReplacedObjects,
            TouchedFonts = emptied.TouchedFonts,
        };
        return WithEditedPage(removed, [.. elements.Where(e => !(e is ForeignAnnotation foreign && emptied.DroppedAnnotations.Contains(foreign.Original)))]);
    }

    /// <summary>Originals replaced by edited text (forms, images; object numbers of the source): kept only if something still draws them.</summary>
    public IReadOnlySet<int> ReplacedObjects { get; private init; } = new HashSet<int>();

    /// <summary>Marked content (MCID) with edited text: its alternate texts in the structure tree are dropped on saving.</summary>
    public IReadOnlySet<int> AlternateTextMcids { get; private init; } = new HashSet<int>();

    /// <summary>Redacted page (on saving, see <see cref="Redactor"/>): new content, new identifier.</summary>
    /// <param name="page">The redacted page.</param>
    /// <param name="elements">The remaining elements.</param>
    /// <param name="alternateTextMcids">Marked content that lost characters: its alternate texts in the structure tree are dropped too.</param>
    internal PageItem WithRedactedPage(PdfPage page, IReadOnlyList<PageElement> elements, IReadOnlySet<int>? alternateTextMcids = null) =>
        new(Source, page, elements, Interlocked.Increment(ref _lastId))
        {
            Replaces = Replaces,
            ReplacedObjects = ReplacedObjects,
            AlternateTextMcids = alternateTextMcids is not { Count: > 0 } ? AlternateTextMcids
                : AlternateTextMcids.Count == 0 ? alternateTextMcids : new HashSet<int>(AlternateTextMcids.Concat(alternateTextMcids)),
        };

    /// <summary>
    /// After saving: the same page from the newly written file, with its still editable elements. If all elements stayed
    /// editable, the page itself looks as before: same identifier, and the finished tiles remain valid. If one became fixed
    /// (signature, flattening), it is now part of the content: new identifier, and the tiles show it.
    /// </summary>
    /// <param name="source">The written document.</param>
    /// <param name="page">The page in it.</param>
    /// <returns>The new entry.</returns>
    public PageItem Reloaded(PdfDocument source, PdfPage page)
    {
        var loaded = Load(source, page);
        return new PageItem(loaded, loaded.Item3.Count == Elements.Count ? Id : Interlocked.Increment(ref _lastId));
    }

    /// <summary>
    /// After redacting in the document (<see cref="Writing.InDocumentRedaction"/>): the page from the redacted state with its
    /// annotations of other programs and the own elements <paramref name="own"/>.
    /// </summary>
    /// <param name="source">The redacted document.</param>
    /// <param name="page">The page in it.</param>
    /// <param name="own">The own elements.</param>
    /// <param name="sameLook">The page itself looks as before (no redaction, nothing written in): same identifier, and the finished tiles remain valid.</param>
    /// <param name="foreignBefore">Number of annotations of other programs the page had before: if there are fewer now (sanitised), it looks different.</param>
    /// <returns>The new entry.</returns>
    public PageItem InRedactedDocument(PdfDocument source, PdfPage page, IReadOnlyList<PageElement> own, bool sameLook, int foreignBefore)
    {
        var foreign = ForeignAnnotations.For(source, page);
        sameLook &= foreign.Count == foreignBefore;
        IReadOnlyList<PageElement> elements = foreign.Count == 0 ? own : own.Count == 0 ? foreign : [.. foreign, .. own];
        return new PageItem(source, page, elements, sameLook ? Id : Interlocked.Increment(ref _lastId));
    }

    /// <summary>Creates entries for all pages of a document in their order.</summary>
    /// <param name="doc">The document.</param>
    /// <returns>One entry per page.</returns>
    public static PageItem[] From(PdfDocument doc)
    {
        var items = new PageItem[doc.PageCount];
        for (var i = 0; i < items.Length; i++) items[i] = new PageItem(doc, doc.Pages[i]);
        return items;
    }
}
