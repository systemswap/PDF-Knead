using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>Selecting, moving and deleting annotations and fields of other programs.</summary>
public sealed class ForeignAnnotationTests
{
    private static int Appearance(TestPdf pdf, double w, double h, string color) =>
        pdf.Add(TestPdf.FlateStream(Encoding.ASCII.GetBytes($"{color} rg 0 0 {w} {h} re f"),
            TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, w, h)))));

    private static PdfDictionary Annot(TestPdf pdf, string kind, double l, double b, double r, double t, string color, params (string Key, PdfObject Value)[] more)
    {
        var entries = new List<(string, PdfObject)>
        {
            ("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N(kind)), ("Rect", TestPdf.Box(l, b, r, t)),
            ("AP", TestPdf.Dict(("N", TestPdf.Ref(Appearance(pdf, r - l, t - b, color))))),
        };
        entries.AddRange(more);
        return TestPdf.Dict([.. entries]);
    }

    /// <summary>
    /// Page with a stamp (red), a note (green) with popup and reply, an ink drawing (blue), a highlight (yellow), two
    /// text fields in the form, and a rectangle annotation stored directly; plus a link and a hidden stamp.
    /// </summary>
    /// <param name="stampFlags">/F of the stamp (e.g. 132: print and locked); null as made by the helper.</param>
    /// <param name="placeFlags">/F of the field „Ort“ (e.g. 516: print and contents locked); null as made by the helper.</param>
    internal static byte[] AnnotatedPage(int? stampFlags = null, int? placeFlags = null)
    {
        var pdf = TestPdf.Simple(1);
        const int page = 5;
        var stamp = pdf.Add(Annot(pdf, "Stamp", 400, 700, 500, 740, "1 0 0"));
        if (stampFlags is { } sf) ((PdfDictionary)pdf.Get(stamp)).Set(PdfName.Get("F"), PdfInteger.Of(sf));
        var popup = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Popup")), ("Rect", TestPdf.Box(330, 650, 480, 720))));
        var note = pdf.Add(Annot(pdf, "Text", 300, 700, 320, 720, "0 0.7 0", ("Contents", new PdfString("Notiz"u8.ToArray())), ("Popup", TestPdf.Ref(popup))));
        ((PdfDictionary)pdf.Get(popup)).Set(PdfName.Parent, TestPdf.Ref(note));
        var reply = pdf.Add(Annot(pdf, "Text", 300, 700, 320, 720, "0 0.7 0", ("Contents", new PdfString("Antwort"u8.ToArray())), ("IRT", TestPdf.Ref(note))));
        var ink = pdf.Add(Annot(pdf, "Ink", 100, 400, 200, 450, "0 0 1", ("InkList", new PdfArray([TestPdf.Box(100, 400, 150, 450, 200, 400)]))));
        var highlight = pdf.Add(Annot(pdf, "Highlight", 72, 755, 200, 780, "1 1 0", ("QuadPoints", TestPdf.Box(72, 780, 200, 780, 72, 755, 200, 755))));
        var place = pdf.Add(Annot(pdf, "Widget", 72, 100, 200, 120, "0.5 0.5 0.5", ("FT", TestPdf.N("Tx")), ("T", new PdfString("Ort"u8.ToArray())),
            ("V", new PdfString("Musterstadt"u8.ToArray()))));
        if (placeFlags is { } pf) ((PdfDictionary)pdf.Get(place)).Set(PdfName.Get("F"), PdfInteger.Of(pf));
        var name = pdf.Add(Annot(pdf, "Widget", 72, 140, 200, 160, "0.5 0.5 0.5", ("FT", TestPdf.N("Tx")), ("T", new PdfString("Name"u8.ToArray())),
            ("V", new PdfString("Max Mustermann"u8.ToArray()))));
        var link = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Link")), ("Rect", TestPdf.Box(72, 300, 100, 320))));
        var hidden = pdf.Add(Annot(pdf, "Stamp", 450, 400, 500, 450, "1 0 1", ("F", PdfInteger.Of(2))));
        var square = Annot(pdf, "Square", 250, 250, 280, 280, "0 1 1");   // directly in /Annots
        ((PdfDictionary)pdf.Get(page)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(stamp), TestPdf.Ref(note), TestPdf.Ref(popup), TestPdf.Ref(reply),
            TestPdf.Ref(ink), TestPdf.Ref(highlight), TestPdf.Ref(place), TestPdf.Ref(name), TestPdf.Ref(link), TestPdf.Ref(hidden), square]));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new PdfArray([TestPdf.Ref(place), TestPdf.Ref(name)]))));
        return pdf.Build();
    }

    private static PdfDocument Save(PageItem item)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(item.Source, [item], output);
        var doc = PdfDocument.Open(output.ToArray());
        Assert.False(doc.WasRepaired);
        return doc;
    }

    private static List<PdfDictionary> Annotations(PdfDocument doc) =>
        doc.Pages[0].Dictionary.Get(PdfName.Get("Annots")) is PdfArray a ? [.. Enumerable.Range(0, a.Count).Select(i => a.Get(i)).OfType<PdfDictionary>()] : [];

    private static PdfDictionary Of(PdfDocument doc, string kind) => Annotations(doc).First(a => a.GetName(PdfName.Subtype)?.Value == kind);

    private static double[] Numbers(PdfObject? array) =>
        array is PdfArray a ? [.. Enumerable.Range(0, a.Count).Select(i => a.Get(i) switch { PdfInteger n => n.Value, PdfReal r => r.Value, _ => double.NaN })] : [];

    private static SKColor At(SKBitmap b, double x, double y) => b.GetPixel((int)x, b.Height - 1 - (int)y);

    [Fact]
    public void Elements_AreTheVisibleForeignAnnotations()
    {
        using var doc = PdfDocument.Open(AnnotatedPage());
        var item = PageItem.From(doc)[0];
        var kinds = item.Elements.OfType<ForeignAnnotation>().Select(f => f.Kind).ToList();
        Assert.Equal(["Stamp", "Text", "Text", "Ink", "Highlight", "Widget", "Widget", "Square"], kinds);   // no popup, link or hidden stamp

        // They are missing from the tiles (the view draws them on top) but present in the full image of the page
        using var tile = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, doc.Pages[0], withoutOwnElements: true), doc, doc.Pages[0], 1, new ImageCache());
        using var full = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, doc.Pages[0]), doc, doc.Pages[0], 1, new ImageCache());
        Assert.Equal(SKColors.White, At(tile, 450, 720));
        Assert.Equal(new SKColor(255, 0, 0), At(full, 450, 720));
    }

    [Fact]
    public void Overlay_DrawsTheMovedAnnotation()
    {
        using var doc = PdfDocument.Open(AnnotatedPage());
        var item = PageItem.From(doc)[0];
        var stamp = item.Elements.OfType<ForeignAnnotation>().First(f => f.Kind == "Stamp");
        var moved = ElementAppearance.Moved(stamp, new PdfRectangle(100, 500, 200, 540));
        using var bitmap = new SKBitmap((int)doc.Pages[0].Width, (int)doc.Pages[0].Height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            ElementOverlay.Draw(canvas, doc.Pages[0], [moved], 1);
        }
        Assert.Equal(new SKColor(255, 0, 0), At(bitmap, 150, 520));
        Assert.Equal(SKColors.White, At(bitmap, 450, 720));
    }

    [Fact]
    public void Moved_OnSave_RectPointsAndPopupFollow()
    {
        using var doc = PdfDocument.Open(AnnotatedPage());
        var item = PageItem.From(doc)[0];
        PageElement Shift(PageElement e, double dx, double dy) =>
            ElementAppearance.Moved(e, new PdfRectangle(e.Rect.Left + dx, e.Rect.Bottom + dy, e.Rect.Right + dx, e.Rect.Top + dy));
        var elements = item.Elements.Select(e => e switch
        {
            ForeignAnnotation { Kind: "Ink" } => Shift(e, 50, -100),
            ForeignAnnotation { Kind: "Highlight" } => Shift(e, 0, -50),
            ForeignAnnotation { Kind: "Text" } f when f.Original.Get(PdfName.Get("Popup")) is PdfDictionary => Shift(e, 10, 10),
            ForeignAnnotation { Kind: "Square" } => Shift(e, 100, 0),
            _ => e,
        }).ToList();
        using var saved = Save(item.WithElements(elements));

        var ink = Of(saved, "Ink");
        Assert.Equal([150, 300, 250, 350], Numbers(ink.Get(PdfName.Get("Rect"))));
        Assert.Equal([150, 300, 200, 350, 250, 300], Numbers((ink.Get(PdfName.Get("InkList")) as PdfArray)?.Get(0)));
        Assert.Equal([72, 730, 200, 730, 72, 705, 200, 705], Numbers(Of(saved, "Highlight").Get(PdfName.Get("QuadPoints"))));
        var note = Annotations(saved).First(a => a.Get(PdfName.Get("Popup")) is PdfDictionary);
        Assert.Equal([310, 710, 330, 730], Numbers(note.Get(PdfName.Get("Rect"))));
        Assert.Equal([340, 660, 490, 730], Numbers(Of(saved, "Popup").Get(PdfName.Get("Rect"))));
        Assert.Equal([350, 250, 380, 280], Numbers(Of(saved, "Square").Get(PdfName.Get("Rect"))));
        Assert.Equal([400, 700, 500, 740], Numbers(Of(saved, "Stamp").Get(PdfName.Get("Rect"))));   // unchanged
        Assert.Equal(11, Annotations(saved).Count);   // nothing lost, nothing duplicated

        // Drawn at the new place
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(saved, saved.Pages[0]), saved, saved.Pages[0], 1, new ImageCache());
        Assert.Equal(new SKColor(0, 0, 255), At(bitmap, 200, 325));
        Assert.NotEqual(new SKColor(0, 0, 255), At(bitmap, 150, 425));
    }

    [Fact]
    public void Deleted_OnSave_GoneWithPopupRepliesAndField()
    {
        using var doc = PdfDocument.Open(AnnotatedPage());
        var item = PageItem.From(doc)[0];
        var elements = item.Elements.Where(e => e is not ForeignAnnotation
        {
            Kind: "Square"
        } && !(e is ForeignAnnotation { Kind: "Text" } t && t.Original.Get(PdfName.Get("Popup")) is PdfDictionary) &&
            !(e is ForeignAnnotation { Kind: "Widget" } w && w.Original.GetString(PdfName.Get("T"))?.ToText() == "Ort")).ToList();
        using var saved = Save(item.WithElements(elements));

        var kinds = Annotations(saved).Select(a => a.GetName(PdfName.Subtype)?.Value).ToList();
        Assert.Equal(["Stamp", "Ink", "Highlight", "Widget", "Link", "Stamp"], kinds);   // note, popup, reply, field "Ort" and rectangle gone
        var fields = saved.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!;
        Assert.Single(fields);   // no null entries
        Assert.Equal("Name", (fields.Get(0) as PdfDictionary)?.GetString(PdfName.Get("T"))?.ToText());
        var everything = RedactionTests.Everything(saved);
        Assert.DoesNotContain("Musterstadt", everything);
        Assert.DoesNotContain("Antwort", everything);
    }

    [Fact]
    public void Redaction_CountsMovedAnnotationsWhereTheyAreNow()
    {
        using var doc = PdfDocument.Open(AnnotatedPage());
        var item = PageItem.From(doc)[0];
        // Stamp moved out of the area, rectangle moved into it; the old place of the stamp is redacted
        var elements = item.Elements.Select(e => e switch
        {
            ForeignAnnotation { Kind: "Stamp" } => ElementAppearance.Moved(e, new PdfRectangle(100, 500, 200, 540)),
            ForeignAnnotation { Kind: "Square" } => ElementAppearance.Moved(e, new PdfRectangle(420, 710, 450, 740)),
            _ => e,
        }).Append(new RedactionElement { Rect = new PdfRectangle(390, 690, 510, 750) }).ToList();
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [item.WithElements(elements)], output, new PdfSaveOptions { ApplyRedactions = true });
        using var saved = PdfDocument.Open(output.ToArray());

        var annots = Annotations(saved);
        Assert.DoesNotContain(annots, a => a.GetName(PdfName.Subtype)?.Value == "Square");
        var stamps = annots.Where(a => a.GetName(PdfName.Subtype)?.Value == "Stamp").Select(a => Numbers(a.Get(PdfName.Get("Rect")))).ToList();
        Assert.Equal([[100, 500, 200, 540], [450, 400, 500, 450]], stamps);   // the hidden stamp stays where it was
    }

    /// <summary>
    /// The same page taken over twice (its annotations are the same objects): moving and deleting on one
    /// copy leave the other as it is.
    /// </summary>
    [Fact]
    public void SamePageTwice_ChangesStayOnTheirCopy()
    {
        using var doc = PdfDocument.Open(AnnotatedPage());
        var first = new PageItem(doc, doc.Pages[0]);
        var second = new PageItem(doc, doc.Pages[0]);
        first = first.WithElements([.. first.Elements.Select(e => e is ForeignAnnotation { Kind: "Stamp" } ? ElementAppearance.Moved(e, new PdfRectangle(100, 500, 200, 540)) : e)]);
        second = second.WithElements([.. second.Elements.Where(e => !(e is ForeignAnnotation { Kind: "Text" } t && t.Original.Get(PdfName.Get("Popup")) is PdfDictionary)
                                                              && e is not ForeignAnnotation { Kind: "Ink" })]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [first, second], output);
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.False(saved.WasRepaired);

        List<PdfDictionary> On(int page) => saved.Pages[page].Dictionary.Get(PdfName.Get("Annots")) is PdfArray a
            ? [.. Enumerable.Range(0, a.Count).Select(i => a.Get(i)).OfType<PdfDictionary>()] : [];
        double[] StampRect(int page) => Numbers(On(page).First(a => a.GetName(PdfName.Subtype)?.Value == "Stamp" && (a.GetInteger(PdfName.Get("F")) ?? 0) == 0).Get(PdfName.Get("Rect")));
        string Kinds(int page) => string.Join(",", On(page).Select(a => a.GetName(PdfName.Subtype)?.Value));

        Assert.Equal([100, 500, 200, 540], StampRect(0));   // moved only on the first
        Assert.Equal([400, 700, 500, 740], StampRect(1));
        Assert.Equal("Stamp,Text,Popup,Text,Ink,Highlight,Widget,Widget,Link,Stamp,Square", Kinds(0));   // everything present on the first
        Assert.Equal("Stamp,Highlight,Widget,Widget,Link,Stamp,Square", Kinds(1));                      // note with popup and reply, and ink, gone
        Assert.Contains("Notiz", RedactionTests.Everything(saved));   // the first page still needs it
    }

    /// <summary>Builds a page with a red stamp and 250 small blue squares (at the bottom).</summary>
    internal static byte[] ManyAnnotations()
    {
        var pdf = TestPdf.Simple(1);
        var refs = new List<PdfObject> { TestPdf.Ref(pdf.Add(Annot(pdf, "Stamp", 300, 400, 400, 450, "1 0 0"))) };
        for (var i = 0; i < 250; i++)
        {
            double x = 40 + i % 25 * 20, y = 60 + i / 25 * 20;
            refs.Add(TestPdf.Ref(pdf.Add(Annot(pdf, "Square", x, y, x + 6, y + 6, "0 0 1"))));
        }
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray(refs));
        return pdf.Build();
    }

    private static void WaitIdle(DocumentRenderer renderer)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!renderer.IsIdle)
        {
            Assert.True(DateTime.UtcNow < until, "Renderer wird nicht fertig");
            Thread.Sleep(10);
        }
    }

    private static SKColor TilePixel(SKImage tile, int x, int y)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(tile.Width, tile.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        Assert.True(tile.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0));
        return bitmap.GetPixel(x, y);
    }

    /// <summary>
    /// On pages with many foreign annotations the tiles draw the unchanged ones as well (otherwise wasted work when
    /// scrolling); a moved one is drawn on top, and the tiles are redrawn without it, with the old tiles shown until then.
    /// </summary>
    [Fact]
    public void ManyAnnotations_AreInTheTiles_MovedOnesAbove()
    {
        using var doc = PdfDocument.Open(ManyAnnotations());
        var item = PageItem.From(doc)[0];
        Assert.True(item.HasTiledAnnotations);
        Assert.Empty(item.OverlayElements);
        Assert.Equal(251, item.TiledAnnotations!.Count);

        using var renderer = new DocumentRenderer(workers: 2) { HideOwnElements = true };
        renderer.SetPages([new RenderPage(item.Id, doc, item.Page, item.TiledAnnotations)]);
        var key = new TileKey(item.Id, 0, 0, 0, 0);    // 1 px per point, top left (512 × 512)
        var below = new TileKey(item.Id, 0, 0, 1, 0);  // below it: the blue squares
        renderer.Request("ansicht", [key, below]);
        WaitIdle(renderer);
        var before = renderer.TryGetTile(key)!;
        Assert.Equal(new SKColor(255, 0, 0), TilePixel(before, 350, 842 - 425));   // stamp in the tile

        var stamp = item.Elements.First(e => e is ForeignAnnotation { Kind: "Stamp" });
        var moved = item.WithElements([.. item.Elements.Select(e => ReferenceEquals(e, stamp) ? ElementAppearance.Moved(e, new PdfRectangle(100, 600, 200, 650)) : e)]);
        Assert.Same(moved.Elements.First(e => e is ForeignAnnotation { IsMoved: true }), Assert.Single(moved.OverlayElements));
        Assert.Equal(250, moved.TiledAnnotations!.Count);

        renderer.SetPages([new RenderPage(moved.Id, doc, moved.Page, moved.TiledAnnotations)]);
        // Outdated, but visible until the new one is ready (no flicker); fetching marks it for redrawing (that it was redrawn shows the
        // new tile below — whether the renderer is still busy right here depends on how fast the workers are)
        Assert.Same(before, renderer.TryGetTile(key));
        Assert.NotNull(renderer.TryGetTile(below));
        WaitIdle(renderer);
        var after = renderer.TryGetTile(key)!;
        var afterBelow = renderer.TryGetTile(below)!;
        Assert.NotSame(before, after);
        Assert.Equal(SKColors.White, TilePixel(after, 350, 842 - 425));                       // no longer in the tile
        Assert.Equal(new SKColor(0, 0, 255), TilePixel(afterBelow, 43, 842 - 63 - 512));     // the others still in the tiles
        Assert.True(renderer.IsIdle);

        // Same state (new list, same annotations): the tiles stay
        renderer.SetPages([new RenderPage(moved.Id, doc, moved.Page, [.. moved.TiledAnnotations])]);
        Assert.Same(after, renderer.TryGetTile(key));
        Assert.Same(afterBelow, renderer.TryGetTile(below));
        Assert.True(renderer.IsIdle);
    }

    [Fact]
    public void SameAnnotationTwiceInAnnots_IsOneElement()
    {
        var pdf = TestPdf.Simple(1);
        var stamp = pdf.Add(Annot(pdf, "Stamp", 400, 700, 500, 740, "1 0 0"));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(stamp), TestPdf.Ref(stamp)]));
        using var doc = PdfDocument.Open(pdf.Build());
        var foreign = Assert.Single(PageItem.From(doc)[0].Elements);

        // Moved, it appears at the new place in both entries (no duplicate at the old place)
        var item = PageItem.From(doc)[0];
        using var saved = Save(item.WithElements([ElementAppearance.Moved(foreign, new PdfRectangle(100, 500, 200, 540))]));
        Assert.All(Annotations(saved), a => Assert.Equal([100, 500, 200, 540], Numbers(a.Get(PdfName.Get("Rect")))));
    }

    [Fact]
    public void Unchanged_SaveKeepsThemAsTheyAre()
    {
        using var doc = PdfDocument.Open(AnnotatedPage());
        using var saved = Save(PageItem.From(doc)[0]);
        Assert.Equal(11, Annotations(saved).Count);
        Assert.Equal([100, 400, 200, 450], Numbers(Of(saved, "Ink").Get(PdfName.Get("Rect"))));
        Assert.Equal(2, saved.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!.Count);
    }
}
