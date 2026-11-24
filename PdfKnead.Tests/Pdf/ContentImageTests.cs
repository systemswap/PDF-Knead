using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Images of the page content moved and scaled (KONZEPT.md, section 5.20): found with the block that draws them, tiles of one picture
/// grouped, the move written into the content while the image keeps its place in the drawing order.
/// </summary>
public sealed class ContentImageTests
{
    // Two tiles of one picture as a layout program writes them (clip one level out, the image a little larger than the clip), a photo on
    // top of them, an image sharing its clip with a drawing, text above everything, an inline image, and an image without a block of its own
    private const string Content =
        "q 100 500 100 100 re W n q 110 0 0 110 95 495 cm /T1 Do Q Q\n" +
        "q 200 500 100 100 re W n q 110 0 0 110 195 495 cm /T2 Do Q Q\n" +
        "q 50 0 0 50 140 520 cm /Foto Do Q\n" +
        "q 0 0 595 400 re W n 0 0 1 rg 20 20 10 10 re f q 50 0 0 50 400 300 cm /T3 Do Q Q\n" +
        "q 30 0 0 30 450 100 cm BI /W 2 /H 2 /CS /G /BPC 8 ID \u0000\u0000\u0000\u0000 EI Q\n" +
        "BT /F1 14 Tf 110 545 Td (Titel bleibt) Tj ET\n" +
        "50 0 0 50 500 700 cm /T4 Do\n";

    internal static PdfStream Solid(TestPdf pdf, byte r, byte g, byte b, int size)
    {
        var pixels = new byte[size * size * 3];
        for (var i = 0; i < size * size; i++) (pixels[i * 3], pixels[i * 3 + 1], pixels[i * 3 + 2]) = (r, g, b);
        return TestPdf.FlateStream(pixels, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(size)), ("Height", PdfInteger.Of(size)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8))));
    }

    private static PdfDocument Open() => PdfDocument.Open(Build());

    /// <summary>The test page as a file (also for the test with the mouse).</summary>
    internal static byte[] Build()
    {
        var pdf = TestPdf.Simple(1);
        var t1 = pdf.Add(Solid(pdf, 220, 30, 30, 11));
        var t2 = pdf.Add(Solid(pdf, 30, 180, 30, 11));
        var photo = pdf.Add(Solid(pdf, 30, 30, 220, 5));
        var t3 = pdf.Add(Solid(pdf, 200, 200, 0, 5));
        var t4 = pdf.Add(Solid(pdf, 0, 200, 200, 5));
        pdf.Replace(4, TestPdf.FlateStream(Encoding.Latin1.GetBytes(Content)));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(
            ("XObject", TestPdf.Dict(("T1", TestPdf.Ref(t1)), ("T2", TestPdf.Ref(t2)), ("Foto", TestPdf.Ref(photo)), ("T3", TestPdf.Ref(t3)), ("T4", TestPdf.Ref(t4)))),
            ("Font", TestPdf.Dict(("F1", TestPdf.Ref(3))))));
        return pdf.Build();
    }

    internal static SKBitmap Render(PdfDocument doc, PdfPage page) =>
        SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache());

    internal static SKColor At(SKBitmap bitmap, PdfPage page, double x, double y)
    {
        var p = PageGeometry.PageToDevice(page, 1).Transform(x, y);
        return bitmap.GetPixel((int)p.X, (int)p.Y);
    }

    internal static bool Red(SKColor c) => c.Red > 180 && c.Green < 80 && c.Blue < 80;

    private static int Find(IReadOnlyList<ContentObject> images, double x, double y) =>
        images.Select((image, i) => (image, i)).Last(e => e.image.Bounds is var b && x >= b.Left && x <= b.Right && y >= b.Top && y <= b.Bottom).i;

    [Fact]
    public void Find_ImagesWithTheirBlocks_TilesFormOnePicture()
    {
        using var doc = Open();
        var item = PageItem.From(doc)[0];
        var all = ContentObjectEditor.ObjectsOf(item);
        // the filled square drawn beside T3 is a drawing; the rest are images
        var square = Assert.Single(all, i => i.IsDrawing);
        Assert.Equal(RectD.FromLtrb(20, 20, 30, 30), square.Visible);
        var images = all.Where(i => !i.IsDrawing).ToList();
        Assert.Equal(6, images.Count);
        Assert.All(images.Take(5), i => Assert.True(i.Block >= 0, $"Block fehlt bei {i.Key}"));
        Assert.Equal(-1, images[5].Block);   // drawn at the top level
        Assert.True(images[4].Inline);
        var t1 = Find(all, 120, 590);
        var t2 = Find(all, 290, 590);
        Assert.Equal([t1, t2], all.GroupOf(t1));
        Assert.Equal([t1, t2], all.GroupOf(t2));
        // the photo on top overlaps both widely: no tile of the picture, even with the same resolution
        var photo = Find(all, 165, 545);
        Assert.Equal([photo], all.GroupOf(photo));
    }

    /// <summary>
    /// The picture moved down by 200 pt: its clip goes with it (otherwise the old clip would cut it away), the text drawn after it stays
    /// above it, the photo stays where it was; after saving and opening again the same.
    /// </summary>
    [Fact]
    public void MovingThePicture_ClipGoesWith_TextStaysAbove()
    {
        using var doc = Open();
        var item = PageItem.From(doc)[0];
        var images = ContentObjectEditor.ObjectsOf(item);
        var group = images.GroupOf(Find(images, 120, 590));
        var moved = ContentObjectEditor.Transform(item, group.Select(g => images[g].Key), Matrix.Translate(0, -200));
        Assert.NotEqual(item.Id, moved.Id);
        Assert.Equal(100, ContentObjectEditor.ObjectsOf(item)[group[0]].Bounds.Top - 395, 3);   // the old entry is unchanged (undo)

        using var data = new MemoryStream();
        PdfWriter.Write(doc, [moved], data);
        using var saved = PdfDocument.Open(data.ToArray());
        foreach (var (d, page) in new[] { (doc, moved.Page), (saved, saved.Pages[0]) })
        {
            using var bitmap = Render(d, page);
            Assert.True(Red(At(bitmap, page, 150, 350)), $"verschoben: {At(bitmap, page, 150, 350)}");
            Assert.False(Red(At(bitmap, page, 105, 590)), "an der alten Stelle bleibt nichts");
            var photo = At(bitmap, page, 165, 525);
            Assert.True(photo.Blue > 180 && photo.Red < 80, $"Foto bleibt: {photo}");
            Assert.Contains("Titel bleibt", PageText.Create(PageInterpreter.Build(d, page).Text).Text);
        }
        var after = ContentObjectEditor.ObjectsOf(PageItem.From(saved)[0]);
        Assert.Equal(295, after[group[0]].Bounds.Top, 1);
    }

    [Fact]
    public void ScalingAndRepeatedMoves_DoNotNest()
    {
        using var doc = Open();
        var item = PageItem.From(doc)[0];
        var images = ContentObjectEditor.ObjectsOf(item);
        var all = images.Select(i => i.Key).ToList();
        var once = ContentObjectEditor.Transform(item, all, Matrix.Translate(1, 1));
        var many = once;
        for (var k = 0; k < 20; k++) many = ContentObjectEditor.Transform(many, all, Matrix.Translate(1, 1));
        many = ContentObjectEditor.Transform(many, all, Matrix.Scale(0.5, 0.5));
        static int Count(PageItem p, string op)
        {
            var parser = new ContentParser(((PdfStream)p.Page.Dictionary.Get(PdfName.Contents)).RawData!);
            var n = 0;
            while (parser.Next() is { } o) if (o == op) n++;
            return n;
        }
        Assert.Equal(Count(once, "cm"), Count(many, "cm"));
        Assert.Equal(Count(once, "q"), Count(many, "q"));
        var now = ContentObjectEditor.ObjectsOf(many);
        for (var i = 0; i < images.Count; i++)
        {
            var expected = images[i].ImageToPage * Matrix.Translate(21, 21) * Matrix.Scale(0.5, 0.5);
            Assert.Equal(expected.E, now[i].ImageToPage.E, 6);
            Assert.Equal(expected.A, now[i].ImageToPage.A, 6);
        }
    }

    /// <summary>An image without a block of its own and one sharing its clip with a drawing: each moves alone, the drawing stays.</summary>
    [Fact]
    public void ImageWithoutOwnBlock_AndSharedClip_MoveAlone()
    {
        using var doc = Open();
        var item = PageItem.From(doc)[0];
        var images = ContentObjectEditor.ObjectsOf(item);
        var t4 = Find(images, 520, 720);
        var t3 = Find(images, 420, 320);
        var moved = ContentObjectEditor.Transform(item, [images[t4].Key, images[t3].Key], Matrix.Translate(-100, -50));
        using var bitmap = Render(doc, moved.Page);
        var cyan = At(bitmap, moved.Page, 420, 670);
        Assert.True(cyan.Green > 150 && cyan.Blue > 150 && cyan.Red < 60, $"T4: {cyan}");
        var yellow = At(bitmap, moved.Page, 320, 270);
        Assert.True(yellow.Red > 150 && yellow.Green > 150 && yellow.Blue < 60, $"T3: {yellow}");
        var square = At(bitmap, moved.Page, 25, 25);
        Assert.True(square.Blue > 180 && square.Red < 60, $"Zeichnung bleibt: {square}");
    }
    /// <summary>
    /// A logo of several drawings in one block with its own clip (<c>q … re W n q cm … f Q … Q</c>, as layout programs write it): moved as a
    /// whole, its clip moves along — it stays visible at the new place, on the screen and after saving; moved alone, one piece still keeps the
    /// shared clip (docs/FEHLERGEDAECHTNIS.md, No. 398).
    /// </summary>
    [Fact]
    public void LogoOfDrawingsWithItsOwnClip_MovedAsAWhole_StaysVisible()
    {
        var pdf = new TestPdf();
        var content = "q 100 100 100 40 re W n q 1 0 0 1 105 105 cm 1 0 0 rg 0 0 30 30 re f Q q 1 0 0 1 145 105 cm 1 0 0 rg 0 0 30 30 re f Q " +
                      "q 1 0 0 1 180 110 cm 1 0 0 rg 0 0 15 20 re f Q Q";
        var stream = pdf.Add(new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(content)));
        var pages = pdf.Add(new PdfDictionary());
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pages)), ("Contents", TestPdf.Ref(stream)),
            ("MediaBox", TestPdf.Box(0, 0, 400, 600))));
        pdf.Replace(pages, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray([TestPdf.Ref(page)])), ("Count", PdfInteger.Of(1))));
        pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pages))));
        using var doc = PdfDocument.Open(pdf.Build());
        var item = PageItem.From(doc)[0];
        var found = ContentObjectEditor.ObjectsOf(item);
        Assert.Equal(3, found.Count);
        var logo = found.GroupOf(0);
        Assert.Equal(3, logo.Count);

        var moved = ContentObjectEditor.Transform(item, logo.Select(found.KeyOf), Matrix.Translate(100, 300));
        var now = ContentObjectEditor.ObjectsOf(moved);
        for (var i = 0; i < now.Count; i++) Assert.False(now.VisibleOf(i).IsEmpty, $"Teil {i} unsichtbar");
        using (var bitmap = Render(doc, moved.Page))
        {
            Assert.True(Red(At(bitmap, moved.Page, 220, 420)), "an der neuen Stelle");
            Assert.False(Red(At(bitmap, moved.Page, 120, 120)), "an der alten Stelle nichts");
        }
        using (var reopened = PdfDocument.Open(Save(doc, moved)))
        using (var bitmap = Render(reopened, reopened.Pages[0]))
            Assert.True(Red(At(bitmap, reopened.Pages[0], 260, 420)), "gespeichert an der neuen Stelle");

        // one piece alone: the shared clip stays where it was (moving the clip would show or hide the other pieces)
        var alone = ContentObjectEditor.Transform(item, [found.KeyOf(0)], Matrix.Translate(100, 300));
        var single = ContentObjectEditor.ObjectsOf(alone);
        Assert.True(single.VisibleOf(single.IndexOf(found.KeyOf(0))!.Value).IsEmpty);
    }

    /// <summary>
    /// Layers of the page content (KONZEPT.md, section 5.22 „Ebenen“): three overlapping squares, red, green, blue from back to front, under a
    /// matrix set at the outermost level. Red one forward lies over green, still under blue; red to the front over all; blue one back under
    /// green; blue to the back under red as well. The objects stay chosen at their new place, and the page draws them in the same spot; saved
    /// the order holds. Already in front, „to the front“ changes nothing.
    /// </summary>
    [Fact]
    public void Layers_OfThePageContent_ForwardBackwardFrontBack()
    {
        var pdf = new TestPdf();
        var content = "1 0 0 1 50 50 cm q 1 0 0 rg 0 0 100 100 re f Q q 0 1 0 rg 50 50 100 100 re f Q q 0 0 1 rg 100 100 100 100 re f Q";
        var stream = pdf.Add(new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(content)));
        var pages = pdf.Add(new PdfDictionary());
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pages)), ("Contents", TestPdf.Ref(stream)),
            ("MediaBox", TestPdf.Box(0, 0, 400, 400))));
        pdf.Replace(pages, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray([TestPdf.Ref(page)])), ("Count", PdfInteger.Of(1))));
        pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pages))));
        using var doc = PdfDocument.Open(pdf.Build());
        var item = PageItem.From(doc)[0];
        var found = ContentObjectEditor.ObjectsOf(item);
        Assert.Equal(3, found.Count);
        static bool Green(SKColor c) => c.Green > 180 && c.Red < 80 && c.Blue < 80;
        static bool Blue(SKColor c) => c.Blue > 180 && c.Red < 80 && c.Green < 80;
        SKColor Pixel(PageItem it, double x, double y)
        {
            using var bitmap = Render(doc, it.Page);
            return At(bitmap, it.Page, x, y);
        }
        // page space: red 50–150, green 100–200, blue 150–250; red and green overlap at (125, 125), green and blue at (175, 175)
        Assert.True(Green(Pixel(item, 125, 125)));

        var forward = ContentObjectEditor.Restack(item, [found.KeyOf(0)], ContentLayerMove.Forward, found)!.Value;
        Assert.True(Red(Pixel(forward.Item, 125, 125)), "rot vor grün");
        Assert.True(Red(Pixel(forward.Item, 75, 75)), "rot an derselben Stelle");
        var moved = ContentObjectEditor.ObjectsOf(forward.Item);
        Assert.Single(forward.Keys);
        var red = moved.IndexOf(forward.Keys[0])!.Value;
        Assert.InRange(moved.VisibleOf(red).Left, 49, 51);   // at the same place: 50 under the outer matrix
        Assert.True(Blue(Pixel(forward.Item, 175, 175)), "blau bleibt vorn");

        var front = ContentObjectEditor.Restack(item, [found.KeyOf(0)], ContentLayerMove.ToFront, found)!.Value;
        Assert.True(Red(Pixel(front.Item, 125, 125)));
        Assert.Null(ContentObjectEditor.Restack(front.Item, front.Keys, ContentLayerMove.ToFront));   // already in front

        var back = ContentObjectEditor.Restack(item, [found.KeyOf(2)], ContentLayerMove.Backward, found)!.Value;
        Assert.True(Green(Pixel(back.Item, 175, 175)), "blau hinter grün");
        var bottom = ContentObjectEditor.Restack(item, [found.KeyOf(2)], ContentLayerMove.ToBack, found)!.Value;
        Assert.True(Green(Pixel(bottom.Item, 175, 175)));
        Assert.True(Blue(Pixel(bottom.Item, 225, 225)), "blau an derselben Stelle");

        using var reopened = PdfDocument.Open(Save(doc, forward.Item));
        using var saved = Render(reopened, reopened.Pages[0]);
        Assert.True(Red(At(saved, reopened.Pages[0], 125, 125)), "gespeichert: rot vor grün");
    }

    private static byte[] Save(PdfDocument doc, PageItem item)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [item], output, new PdfSaveOptions { CompressStreams = false, ObjectStreams = false });
        return output.ToArray();
    }

    private static int ImageObjects(byte[] file) =>
        System.Text.RegularExpressions.Regex.Matches(Encoding.Latin1.GetString(file), @"/Subtype\s*/Image").Count;

    /// <summary>A deleted image leaves the content and the file; an image another part of the page still draws stays in the file.</summary>
    [Fact]
    public void Delete_TheImageLeavesTheFile_UnlessStillDrawn()
    {
        using var doc = Open();
        var item = PageItem.From(doc)[0];
        var images = ContentObjectEditor.ObjectsOf(item);
        Assert.Equal(5, ImageObjects(Save(doc, item)));
        var photo = Find(images, 165, 545);
        var without = ContentObjectEditor.Delete(item, [images[photo].Key]);
        var left = ContentObjectEditor.ObjectsOf(without);
        Assert.Equal(images.Count - 1, left.Count);
        Assert.DoesNotContain(left, i => i.Key == images[photo].Key);
        var saved = Save(doc, without);
        Assert.Equal(4, ImageObjects(saved));
        using (var reopened = PdfDocument.Open(saved))
        {
            using var bitmap = Render(reopened, reopened.Pages[0]);
            var under = At(bitmap, reopened.Pages[0], 165, 525);
            Assert.True(Red(under), $"unter dem Foto die Kachel: {under}");
            Assert.Contains("Titel bleibt", PageText.Create(PageInterpreter.Build(reopened, reopened.Pages[0]).Text).Text);
        }
        Assert.Equal(images.Count, ContentObjectEditor.ObjectsOf(item).Count);   // the old entry is unchanged (undo)

        // the same XObject drawn twice: deleting one drawing keeps the image for the other
        var pdf = TestPdf.Simple(1);
        var image = pdf.Add(Solid(pdf, 200, 0, 0, 4));
        pdf.Replace(4, TestPdf.FlateStream("q 50 0 0 50 100 100 cm /A Do Q q 50 0 0 50 300 300 cm /A Do Q"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("A", TestPdf.Ref(image))))));
        using var twice = PdfDocument.Open(pdf.Build());
        var twiceItem = PageItem.From(twice)[0];
        var both = ContentObjectEditor.ObjectsOf(twiceItem);
        var one = ContentObjectEditor.Delete(twiceItem, [both[0].Key]);
        Assert.Equal(1, ImageObjects(Save(twice, one)));
        Assert.Equal(0, ImageObjects(Save(twice, ContentObjectEditor.Delete(one, [both[1].Key]))));
    }

    /// <summary>Turned 90° anticlockwise about its centre: the box keeps its centre with width and height swapped, the left tile comes to lie at the bottom.</summary>
    [Fact]
    public void Turn90_AboutTheCentre()
    {
        using var doc = Open();
        var item = PageItem.From(doc)[0];
        var images = ContentObjectEditor.ObjectsOf(item);
        var group = images.GroupOf(Find(images, 120, 590));
        RectD Box(IReadOnlyList<ContentObject> list) => group.Select(g => list[g].Visible).Aggregate((a, b) => a.Union(b));
        var box = Box(images);
        var turned = ContentObjectEditor.Transform(item, group.Select(g => images[g].Key), ContentObjectEditor.TurnAbout(box, 90));
        var after = Box(ContentObjectEditor.ObjectsOf(turned));
        Assert.Equal((box.Left + box.Right) / 2, (after.Left + after.Right) / 2, 3);
        Assert.Equal((box.Top + box.Bottom) / 2, (after.Top + after.Bottom) / 2, 3);
        Assert.Equal(box.Width, after.Height, 3);
        Assert.Equal(box.Height, after.Width, 3);
        using var bitmap = Render(doc, turned.Page);
        Assert.True(Red(At(bitmap, turned.Page, 200, 480)), $"unten: {At(bitmap, turned.Page, 200, 480)}");
        Assert.False(Red(At(bitmap, turned.Page, 200, 620)), $"oben: {At(bitmap, turned.Page, 200, 620)}");
    }

    /// <summary>Turned by 30°, the tiles no longer lie upright — they stay one picture because they were edited together.</summary>
    [Fact]
    public void TurnedFreely_StaysOnePicture()
    {
        using var doc = Open();
        var item = PageItem.From(doc)[0];
        var images = ContentObjectEditor.ObjectsOf(item);
        var group = images.GroupOf(Find(images, 120, 590));
        var box = group.Select(g => images[g].Visible).Aggregate((a, b) => a.Union(b));
        var turned = ContentObjectEditor.Transform(item, group.Select(g => images[g].Key), ContentObjectEditor.TurnAbout(box, 30));
        var now = ContentObjectEditor.ObjectsOf(turned);
        Assert.False(now[group[0]].AxisAligned);
        Assert.Equal(group, now.GroupOf(group[1]));
        // by the tiles alone it would fall apart
        Assert.Equal([group[0]], ContentObjects.Find(doc, turned.ObjectEdits!.Base).WithEdits(turned.ObjectEdits with { Groups = [] }).GroupOf(group[0]));
    }
    /// <summary>The two tiles put together into one image for copying and saving: each at its place, cut to its visible part, without the text on top.</summary>
    [Fact]
    public void Tiles_PutTogetherIntoOneImage()
    {
        using var doc = Open();
        var item = PageItem.From(doc)[0];
        var images = ContentObjectEditor.ObjectsOf(item);
        var group = images.GroupOf(Find(images, 120, 590)).Select(g => images[g]).ToList();
        var (width, height, rgba) = ContentImageExport.Compose(doc, group, 16_000_000)!.Value;
        Assert.Equal(20, width);    // 200 × 100 pt at 7.2 dpi (11 pixels on 110 pt)
        Assert.Equal(10, height);
        SKColor Pixel(int x, int y) => new(rgba[(y * width + x) * 4], rgba[(y * width + x) * 4 + 1], rgba[(y * width + x) * 4 + 2], rgba[(y * width + x) * 4 + 3]);
        Assert.True(Red(Pixel(2, 5)), Pixel(2, 5).ToString());
        var green = Pixel(17, 5);
        Assert.True(green.Green > 150 && green.Red < 80 && green.Alpha == 255, green.ToString());
    }
    /// <summary>
    /// The picture of two tiles replaced by a square image: fitted into the box (100 × 100 pt, centred), drawn where the tiles were — the photo
    /// and the text stay above it; the tiles leave the file. The new image is an ordinary image afterwards: deleted, nothing of it stays.
    /// </summary>
    [Fact]
    public void Replace_FittedInPlace_OldTilesGone_NewOneDeletable()
    {
        using var doc = Open();
        var item = PageItem.From(doc)[0];
        var images = ContentObjectEditor.ObjectsOf(item);
        var group = images.GroupOf(Find(images, 120, 590)).Select(g => images[g].Key).ToList();
        var pixels = new byte[10 * 10 * 4];
        for (var p = 0; p < 100; p++) (pixels[p * 4], pixels[p * 4 + 1], pixels[p * 4 + 2], pixels[p * 4 + 3]) = (150, 40, 200, 255);
        var stream = ElementAppearance.ImageStream(new Core.Imaging.RgbaImage(10, 10, pixels));
        var replaced = ContentObjectEditor.Replace(item, group, stream);
        var now = ContentObjectEditor.ObjectsOf(replaced);
        var added = Assert.Single(now, i => ReferenceEquals(i.Source?.Stream, stream));
        Assert.Equal(RectD.FromLtrb(150, 500, 250, 600), added.Visible);
        Assert.Equal(images.Count - 1, now.Count);
        using (var bitmap = Render(doc, replaced.Page))
        {
            var purple = At(bitmap, replaced.Page, 230, 590);
            Assert.True(purple.Red > 120 && purple.Blue > 170 && purple.Green < 80, $"neu: {purple}");
            Assert.False(Red(At(bitmap, replaced.Page, 120, 590)), "die Kachel ist fort");
            var photo = At(bitmap, replaced.Page, 165, 525);
            Assert.True(photo.Blue > 180 && photo.Red < 80, $"Foto darüber: {photo}");
        }
        Assert.Equal(4, ImageObjects(Save(doc, replaced)));
        Assert.Contains("Titel bleibt", PageText.Create(PageInterpreter.Build(doc, replaced.Page).Text).Text);

        var deleted = ContentObjectEditor.Delete(replaced, [added.Key]);
        var saved = Save(doc, deleted);
        Assert.Equal(3, ImageObjects(saved));
        Assert.DoesNotContain("PKImg", Encoding.Latin1.GetString(saved));
    }
    /// <summary>
    /// The picture of two tiles cut to 150…250 × 520…580: each tile keeps only the pixels reaching into that area (the rest leaves the file),
    /// shown exactly within it; the photo and the text stay; an image entirely outside the area would be deleted.
    /// </summary>
    [Fact]
    public void Crop_TilesKeepOnlyTheirPixelsInside()
    {
        using var doc = Open();
        var item = PageItem.From(doc)[0];
        var images = ContentObjectEditor.ObjectsOf(item);
        var group = images.GroupOf(Find(images, 120, 590)).Select(g => images[g].Key).ToList();
        var keep = RectD.FromLtrb(150, 520, 250, 580);
        var (cropped, streams) = ContentObjectEditor.Crop(item, group, keep, SkiaImageCodec.Instance);
        Assert.Equal(2, streams.Count);
        var now = ContentObjectEditor.ObjectsOf(cropped);
        var tiles = now.Where(i => streams.Any(st => ReferenceEquals(st, i.Source?.Stream))).ToList();
        Assert.Equal(keep, tiles.Select(t => t.Visible).Aggregate((a, b) => a.Union(b)));
        Assert.All(tiles, t => Assert.True(t.PixelWidth < 11 && t.PixelHeight < 11, $"{t.PixelWidth} × {t.PixelHeight}"));
        using (var bitmap = Render(doc, cropped.Page))
        {
            Assert.True(Red(At(bitmap, cropped.Page, 160, 575)), At(bitmap, cropped.Page, 160, 575).ToString());
            Assert.False(Red(At(bitmap, cropped.Page, 145, 575)), "links abgeschnitten");
            Assert.False(Red(At(bitmap, cropped.Page, 160, 590)), "oben abgeschnitten");
            var photo = At(bitmap, cropped.Page, 165, 525);
            Assert.True(photo.Blue > 180 && photo.Red < 80, $"Foto bleibt: {photo}");
        }
        Assert.Equal(5, ImageObjects(Save(doc, cropped)));   // two tiles replaced by their cut versions

        // the cut picture is chosen again as one: its two new tiles meet at the seam
        var index = now.ToList().FindIndex(i => ReferenceEquals(i.Source?.Stream, streams[0]));
        Assert.Equal(2, now.GroupOf(index).Count);

        // cut to an area beside a tile: it goes
        var (onlyRight, _) = ContentObjectEditor.Crop(item, group, RectD.FromLtrb(220, 520, 280, 580), SkiaImageCodec.Instance);
        Assert.Equal(images.Count - 1, ContentObjectEditor.ObjectsOf(onlyRight).Count);
    }

    /// <summary>A JPEG cut stays a JPEG; a stencil mask is refused with a reason.</summary>
    [Fact]
    public void Crop_JpegStaysJpeg_StencilRefused()
    {
        var pdf = TestPdf.Simple(1);
        var rgb = new byte[40 * 40 * 3];
        for (var p = 0; p < 1600; p++) (rgb[p * 3], rgb[p * 3 + 1], rgb[p * 3 + 2]) = ((byte)(p % 40 * 6), 120, 60);
        var jpeg = SkiaImageCodec.Instance.EncodeJpeg(40, 40, 3, rgb, 85)!;
        var photo = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(40)),
            ("Height", PdfInteger.Of(40)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8)), ("Filter", TestPdf.N("DCTDecode"))), jpeg));
        var mask = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(8)),
            ("Height", PdfInteger.Of(8)), ("ImageMask", PdfBoolean.True)), new byte[8]));
        pdf.Replace(4, TestPdf.FlateStream("q 200 0 0 200 100 100 cm /P Do Q 0 0 1 rg q 80 0 0 80 400 400 cm /M Do Q"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("P", TestPdf.Ref(photo)), ("M", TestPdf.Ref(mask))))));
        using var doc = PdfDocument.Open(pdf.Build());
        var item = PageItem.From(doc)[0];
        var images = ContentObjectEditor.ObjectsOf(item);
        var (cropped, streams) = ContentObjectEditor.Crop(item, [images[0].Key], RectD.FromLtrb(100, 100, 200, 300), SkiaImageCodec.Instance);
        var cut = Assert.Single(streams);
        Assert.Equal("DCTDecode", cut.Dictionary.GetName(PdfName.Filter)?.Value);
        Assert.Equal(20, cut.Dictionary.GetInteger(PdfName.Get("Width")));
        Assert.Equal(RectD.FromLtrb(100, 100, 200, 300), ContentObjectEditor.ObjectsOf(cropped).Single(i => ReferenceEquals(i.Source?.Stream, cut)).Visible);
        Assert.Throws<PdfException>(() => ContentObjectEditor.Crop(item, [images[1].Key], RectD.FromLtrb(400, 400, 440, 480), SkiaImageCodec.Instance));
    }
    /// <summary>
    /// Drawings (KONZEPT.md, section 5.20, B6): a line of text in outlines — three paths in one block — is one graphic; moved, it goes as a whole
    /// and the text above stays; deleted, its paths leave the content. A path that also clips keeps its clip when deleted. A block of drawings
    /// covering most of the page is no graphic (a plan drawn in one block would be one).
    /// </summary>
    [Fact]
    public void Drawings_GroupedByTheirBlock_MovedAndDeleted()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(Encoding.ASCII.GetBytes(
            "q 0.8 0 0 rg 100 700 m 120 700 l 120 730 l h f 130 700 20 30 re f 160 700 m 180 730 l 175 700 l f Q\n" +   // a „line of text“
            "q 0 0 1 rg 300 300 50 50 re W f 0 1 0 rg 290 290 100 100 re f Q\n" +                                      // a clipping square, then a fill under the clip
            "q 0 g 1 w 10 10 m 585 830 l S 20 20 m 570 820 l S Q\n" +                                                   // two lines across the whole page
            "BT /F1 12 Tf 100 680 Td (Bleibt) Tj ET")));
        using var doc = PdfDocument.Open(pdf.Build());
        var item = PageItem.From(doc)[0];
        var all = ContentObjectEditor.ObjectsOf(item);
        Assert.Equal(7, all.Count);   // three of the line, the clipping square, the green fill, two diagonals
        Assert.All(all, d => Assert.True(d.IsDrawing));
        // the smallest under the point, as the tool chooses (the diagonals' boxes cover it too)
        var hit = all.Select((d, i) => (d, i)).Where(e => e.d.Visible is var v && 140 >= v.Left && 140 <= v.Right && 715 >= v.Top && 715 <= v.Bottom)
            .MinBy(e => e.d.Visible.Width * e.d.Visible.Height).i;
        var line = all.GroupOf(hit);
        Assert.Equal(3, line.Count);
        Assert.Equal(RectD.FromLtrb(100, 700, 180, 730), line.Select(g => all[g].Visible).Aggregate((a, b) => a.Union(b)));
        var diagonal = all.Select((d, i) => (d, i)).First(e => e.d.Visible.Width > 500).i;
        Assert.Single(all.GroupOf(diagonal));   // the whole page: no graphic

        var moved = ContentObjectEditor.Transform(item, line.Select(g => all[g].Key), Matrix.Translate(0, -400));
        using (var bitmap = Render(doc, moved.Page))
        {
            Assert.True(Red(At(bitmap, moved.Page, 140, 315)), At(bitmap, moved.Page, 140, 315).ToString());
            Assert.False(Red(At(bitmap, moved.Page, 140, 715)), "an der alten Stelle bleibt nichts");
        }
        Assert.Contains("Bleibt", PageText.Create(PageInterpreter.Build(doc, moved.Page).Text).Text);

        // deleted: the paths leave the content; the clipping square keeps clipping the green fill after it
        var clipping = all.Select((d, i) => (d, i)).Single(e => e.d.AlsoClips).i;
        var without = ContentObjectEditor.Delete(item, [.. line.Select(g => all[g].Key), all[clipping].Key]);
        Assert.Equal(3, ContentObjectEditor.ObjectsOf(without).Count);
        using var after = Render(doc, without.Page);
        Assert.False(Red(At(after, without.Page, 140, 715)));
        var green = At(after, without.Page, 325, 325);
        Assert.True(green.Green > 180 && green.Blue < 80, $"im Clip: {green}");
        Assert.Equal(SKColors.White, At(after, without.Page, 295, 380));   // outside the clip: still cut away
    }
    /// <summary>
    /// A logo whose letters a layout program wrote each in a block of its own, not from left to right: the letters on one line and close
    /// together form one graphic; a letter on another line, or far away, does not join.
    /// </summary>
    [Fact]
    public void Letters_EachInABlock_JoinIntoOneGraphic()
    {
        var pdf = TestPdf.Simple(1);
        string Letter(double x, double y) => $"q 0 g {x} {y} 10 14 re f Q\n";
        pdf.Replace(4, TestPdf.FlateStream(Encoding.ASCII.GetBytes(
            Letter(126, 100) + Letter(100, 100) + Letter(139, 100) + Letter(113, 102) +   // a word, drawn out of order
            Letter(100, 60) +                                                             // a line below
            Letter(300, 100))));                                                          // far to the right
        using var doc = PdfDocument.Open(pdf.Build());
        var item = PageItem.From(doc)[0];
        var all = ContentObjectEditor.ObjectsOf(item);
        Assert.Equal(6, all.Count);
        var word = all.GroupOf(1);
        Assert.Equal([0, 1, 2, 3], word);
        Assert.Equal([4], all.GroupOf(4));
        Assert.Equal([5], all.GroupOf(5));
    }
    /// <summary>
    /// A logo placed as a form, used twice on page 1 and once on page 2: its drawings are found inside the form and form one graphic per
    /// invocation; moving the first moves only it — the form is copied for the page, the second invocation and page 2 stay as they were,
    /// also after saving.
    /// </summary>
    [Fact]
    public void Form_EachInvocationEditedAlone()
    {
        var pdf = TestPdf.Simple(2);
        var logo = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 40, 20)),
            ("Resources", new PdfDictionary())), "0.8 0 0 rg 0 0 15 20 re f 20 0 20 20 re f"u8.ToArray()));
        foreach (var page in new[] { 5, 7 })
            ((PdfDictionary)pdf.Get(page)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Logo", TestPdf.Ref(logo))))));
        pdf.Replace(4, TestPdf.FlateStream("q 1 0 0 1 100 700 cm /Logo Do Q q 1 0 0 1 300 700 cm /Logo Do Q"u8.ToArray()));
        pdf.Replace(6, TestPdf.FlateStream("q 1 0 0 1 100 100 cm /Logo Do Q"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var items = PageItem.From(doc);
        var all = ContentObjectEditor.ObjectsOf(items[0]);
        Assert.Equal(4, all.Count);
        Assert.All(all, o => Assert.NotEqual("", o.Key.Scope));   // inside the form
        var first = all.GroupOf(0);
        Assert.Equal(2, first.Count);
        Assert.Equal(RectD.FromLtrb(100, 700, 140, 720), first.Select(g => all[g].Visible).Aggregate((a, b) => a.Union(b)));

        items[0] = ContentObjectEditor.Transform(items[0], first.Select(g => all[g].Key), Matrix.Translate(0, -300));
        using var data = new MemoryStream();
        PdfWriter.Write(doc, items, data);
        using var saved = PdfDocument.Open(data.ToArray());
        using (var one = Render(saved, saved.Pages[0]))
        {
            Assert.True(Red(At(one, saved.Pages[0], 105, 405)), "verschoben");
            Assert.False(Red(At(one, saved.Pages[0], 105, 705)), "an der alten Stelle nichts");
            Assert.True(Red(At(one, saved.Pages[0], 305, 705)), "der zweite Aufruf bleibt");
        }
        using (var two = Render(saved, saved.Pages[1])) Assert.True(Red(At(two, saved.Pages[1], 105, 105)), "Seite 2 bleibt");
    }

    /// <summary>A gradient painted with "sh" inside a clip is a drawing: moved with its clip, deleted with it. Content on a hidden layer is not offered.</summary>
    [Fact]
    public void Shading_InAClip_IsADrawing_HiddenLayerIsNot()
    {
        var pdf = TestPdf.Simple(1);
        var function = TestPdf.Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", TestPdf.Box(0, 1)), ("C0", TestPdf.Box(0.9, 0, 0)),
            ("C1", TestPdf.Box(0.9, 0, 0)), ("N", PdfInteger.Of(1)));
        var shading = pdf.Add(TestPdf.Dict(("ShadingType", PdfInteger.Of(2)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("Coords", TestPdf.Box(0, 0, 600, 0)),
            ("Function", function), ("Extend", new PdfArray([PdfBoolean.True, PdfBoolean.True]))));
        var layer = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", new PdfString("Aus"u8.ToArray()))));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("OCProperties"), TestPdf.Dict(("OCGs", new PdfArray([TestPdf.Ref(layer)])),
            ("D", TestPdf.Dict(("OFF", new PdfArray([TestPdf.Ref(layer)]))))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("Shading", TestPdf.Dict(("Sh0", TestPdf.Ref(shading)))),
            ("Properties", TestPdf.Dict(("L0", TestPdf.Ref(layer))))));
        pdf.Replace(4, TestPdf.FlateStream("q 100 600 100 100 re W n /Sh0 sh Q /OC /L0 BDC 0 0 1 rg 300 300 50 50 re f EMC"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var item = PageItem.From(doc)[0];
        var gradient = Assert.Single(ContentObjectEditor.ObjectsOf(item));   // the square on the hidden layer is not offered
        Assert.Equal(ContentKind.Shading, gradient.Kind);
        Assert.Equal(RectD.FromLtrb(100, 600, 200, 700), gradient.Visible);
        var moved = ContentObjectEditor.Transform(item, [gradient.Key], Matrix.Translate(200, -300));
        using (var bitmap = Render(doc, moved.Page))
        {
            Assert.True(Red(At(bitmap, moved.Page, 350, 350)), "der Verlauf samt Beschneidung verschoben");
            Assert.False(Red(At(bitmap, moved.Page, 150, 650)), "an der alten Stelle nichts");
        }
        var without = ContentObjectEditor.Delete(item, [gradient.Key]);
        using var after = Render(doc, without.Page);
        Assert.False(Red(At(after, without.Page, 150, 650)));
    }
    /// <summary>
    /// Hits by the real shape: a diagonal line on the line (with its width), not anywhere in its box; a ring filled by the even-odd rule not in
    /// its hole (by the non-zero rule there); an image turned by 45° not in the corners of its box.
    /// </summary>
    [Fact]
    public void Hits_ByTheRealShape()
    {
        var pdf = TestPdf.Simple(1);
        var image = pdf.Add(Solid(pdf, 200, 0, 0, 4));
        pdf.Replace(4, TestPdf.FlateStream(Encoding.ASCII.GetBytes(
            "q 0 g 4 w 100 100 m 300 300 l S Q\n" +                                   // a diagonal, 4 pt wide
            "q 400 100 100 100 re 425 125 50 50 re f* Q\n" +                           // a ring by even-odd
            "q 400 400 100 100 re 425 425 50 50 re f Q\n" +                            // the same by non-zero: no hole
            "q 70.71 70.71 -70.71 70.71 200 500 cm /Im Do Q")));                        // an image turned by 45°
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im", TestPdf.Ref(image))))));
        using var doc = PdfDocument.Open(pdf.Build());
        var all = ContentObjectEditor.ObjectsOf(PageItem.From(doc)[0]);
        Assert.Equal(4, all.Count);
        var (line, ring, solid, turned) = (all[0], all[1], all[2], all[3]);
        Assert.True(line.Hits(200, 200, 0.5));
        Assert.True(line.Hits(201.5, 198.5, 0.5));        // within half the width
        Assert.False(line.Hits(150, 250, 0.5));           // in its box, but far from the line
        Assert.True(ring.Hits(410, 150, 0.5));
        Assert.False(ring.Hits(450, 150, 0.5));           // the hole
        Assert.True(solid.Hits(450, 450, 0.5));
        Assert.True(turned.Hits(200, 550, 0.5));          // the middle
        Assert.False(turned.Hits(135, 505, 0.5));         // a corner of its box, outside the turned square
    }

    /// <summary>
    /// A pattern lies in the space of the content stream (the page, a form), not in the CTM: a moved area filled with a pattern takes its
    /// pattern along — a path alone, a path in a moved block (the pattern set outside it), a pattern without colours of its own (the colour
    /// from the operands), and a path inside a form. What comes after a moved path still paints with the original pattern.
    /// </summary>
    [Fact]
    public void PatternFill_MovesWithItsShape()
    {
        var pdf = TestPdf.Simple(1);
        // a cell 20 wide: red on the left half, blue on the right; the uncoloured one paints only the left half
        PdfStream Cell(int paintType, string content) => new(TestPdf.Dict(("Type", TestPdf.N("Pattern")), ("PatternType", PdfInteger.Of(1)),
            ("PaintType", PdfInteger.Of(paintType)), ("TilingType", PdfInteger.Of(1)), ("BBox", TestPdf.Box(0, 0, 20, 20)),
            ("XStep", PdfInteger.Of(20)), ("YStep", PdfInteger.Of(20)), ("Resources", new PdfDictionary())), Encoding.ASCII.GetBytes(content));
        var coloured = pdf.Add(Cell(1, "1 0 0 rg 0 0 10 20 re f 0 0 1 rg 10 0 10 20 re f"));
        var uncoloured = pdf.Add(Cell(2, "0 0 10 20 re f"));
        var form = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 50, 50)),
            ("Matrix", TestPdf.Box(2, 0, 0, 2, 0, 0)), ("Resources", TestPdf.Dict(("Pattern", TestPdf.Dict(("P0", TestPdf.Ref(coloured))))))),
            "/Pattern cs /P0 scn 0 0 50 50 re f"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(
            ("Pattern", TestPdf.Dict(("P0", TestPdf.Ref(coloured)), ("P1", TestPdf.Ref(uncoloured)))),
            ("ColorSpace", TestPdf.Dict(("CS0", new PdfArray([TestPdf.N("Pattern"), TestPdf.N("DeviceRGB")])))),
            ("XObject", TestPdf.Dict(("F", TestPdf.Ref(form))))));
        pdf.Replace(4, TestPdf.FlateStream(Encoding.ASCII.GetBytes(
            "/Pattern cs /P0 scn 100 600 100 100 re f\n" +                    // alone
            "q 100 400 100 100 re W n 0 0 595 842 re f Q\n" +                  // in a block, the pattern set before it
            "/CS0 cs 0 0.8 0 /P1 scn 100 100 100 100 re f\n" +                 // uncoloured
            "450 100 50 50 re f\n" +                                          // after it, stays
            "q 1 0 0 1 300 250 cm /F Do Q")));                                // in a form drawn twice as large
        using var doc = PdfDocument.Open(pdf.Build());
        var item = PageItem.From(doc)[0];
        var all = ContentObjectEditor.ObjectsOf(item);
        // by where it shows: the path of the block covers the page, its clip only a square
        int Under(double x, double y) => all.Select((o, i) => (v: o.Visible, i)).Where(e => x >= e.v.Left && x <= e.v.Right && y >= e.v.Top && y <= e.v.Bottom)
            .MinBy(e => e.v.Width * e.v.Height).i;
        int alone = Under(150, 650), block = Under(150, 450), green = Under(150, 150), inForm = Under(350, 300);
        item = ContentObjectEditor.Transform(item, [all[alone].Key], Matrix.Translate(10, 0));
        item = ContentObjectEditor.Transform(item, [all[block].Key], Matrix.Translate(10, 0));
        item = ContentObjectEditor.Transform(item, [all[green].Key], Matrix.Translate(10, 0));
        item = ContentObjectEditor.Transform(item, [all[inForm].Key], Matrix.Translate(20, 0));

        static bool Blue(SKColor c) => c.Blue > 180 && c.Red < 80 && c.Green < 80;
        static bool Green(SKColor c) => c.Green > 150 && c.Red < 80 && c.Blue < 80;
        using var bitmap = Render(doc, item.Page);
        foreach (var y in new[] { 650, 450 })
        {
            // moved by 10: the cell starts at 110, red up to 120, blue up to 130 (where it stayed, the other way round)
            Assert.True(Red(At(bitmap, item.Page, 115, y)), $"rot bei 115/{y}");
            Assert.True(Blue(At(bitmap, item.Page, 125, y)), $"blau bei 125/{y}");
        }
        Assert.True(Green(At(bitmap, item.Page, 115, 150)), "ungefärbtes Muster mitgewandert");
        Assert.False(Green(At(bitmap, item.Page, 125, 150)));
        Assert.True(Green(At(bitmap, item.Page, 465, 125)), "danach das ursprüngliche Muster");
        Assert.False(Green(At(bitmap, item.Page, 475, 125)));
        // in the form a cell is 40 wide; moved by 20 it starts at 320: red up to 340, blue up to 360
        Assert.True(Red(At(bitmap, item.Page, 325, 300)), "im Formular rot");
        Assert.True(Blue(At(bitmap, item.Page, 345, 300)), "im Formular blau");
    }

    /// <summary>
    /// A plan: every one of its paths can be chosen, also beyond the 20,000 that were listed at most — found by the grid at their place,
    /// grouped by their block, and moved with the objects found once (no second reading of the page) to the same result as without them.
    /// </summary>
    [Fact]
    public void Plan_EveryPathChoosable_EditsWithoutReadingAgain()
    {
        var content = new StringBuilder();
        // 30,000 short lines in blocks of 1,000 (each block one graphic), a free line at the end
        for (var b = 0; b < 30; b++)
        {
            content.Append("q 0.2 w\n");
            for (var k = 0; k < 1000; k++)
            {
                var (x, y) = (20 + k % 50 * 11, 20 + b * 26 + k / 50 * 1.2);
                content.Append(System.FormattableString.Invariant($"{x} {y} m {x + 8} {y} l S\n"));
            }
            content.Append("Q\n");
        }
        content.Append("1 w 100 800 m 300 800 l S\n");
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(Encoding.ASCII.GetBytes(content.ToString())));
        using var doc = PdfDocument.Open(pdf.Build());
        var item = PageItem.From(doc)[0];
        var all = ContentObjectEditor.ObjectsOf(item);
        Assert.Equal(30_001, all.Count);
        var last = Assert.IsType<int>(all.At(200, 800, 0.5));
        Assert.Equal(30_000, last);
        Assert.Equal([last], all.GroupOf(last));
        var inBlock = Assert.IsType<int>(all.At(24, 20 + 29 * 26, 0.5));
        Assert.Equal(1000, all.GroupOf(inBlock).Count);
        Assert.Null(all.At(590, 500, 0.5));   // beside the lines

        var keys = all.GroupOf(inBlock).Select(all.KeyOf).ToList();
        var withFound = ContentObjectEditor.Transform(item, keys, Matrix.Translate(5, 3), all);
        var withoutFound = ContentObjectEditor.Transform(item, keys, Matrix.Translate(5, 3));
        Assert.Equal(1000, withFound.ObjectEdits!.Originals.Count);
        Assert.Empty(withoutFound.ObjectEdits!.Originals);
        static byte[] Body(PageItem i) => i.Page.Dictionary.GetStream(PdfName.Contents)!.RawData!;
        Assert.Equal(Body(withoutFound), Body(withFound));
        // the view of the moved state: the block where it lies now
        var now = all.WithEdits(withFound.ObjectEdits);
        Assert.Equal(inBlock, now.At(29, 23 + 29 * 26, 0.5));
    }

    private static bool Blue(SKColor c) => c.Blue > 180 && c.Red < 80 && c.Green < 80;

    /// <summary>
    /// A drawing moved out of the box of its form (docs/FEHLERGEDAECHTNIS.md, No. 247): the box grows, so the moved drawing is seen in full — and
    /// the tool shows it where the content now draws it (it showed it cut by the old box, an empty frame). A neighbour in the same form that
    /// reaches beyond the box keeps the old box: what the box hid stays hidden.
    /// </summary>
    [Fact]
    public void MovedOutOfAFormsBox_SeenInFull_NeighbourStaysCut()
    {
        var pdf = TestPdf.Simple(1);
        var form = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(100, 100, 200, 200)),
            ("Resources", new PdfDictionary())), "1 0 0 rg 110 110 30 30 re f 0 0 1 rg 180 160 80 20 re f"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("F", TestPdf.Ref(form))))));
        pdf.Replace(4, TestPdf.FlateStream("q /F Do Q"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var item = PageItem.From(doc)[0];
        var all = ContentObjectEditor.ObjectsOf(item);
        Assert.Equal(2, all.Count);
        var moved = ContentObjectEditor.Transform(item, [all.KeyOf(0)], Matrix.Translate(150, 0), all);

        // the tool shows it where the new content draws it
        var shown = all.WithEdits(moved.ObjectEdits).VisibleOf(0);
        var drawn = ContentObjects.Find(doc, moved.Page).VisibleOf(0);
        Assert.Equal(RectD.FromLtrb(260, 110, 290, 140), drawn);
        Assert.Equal(drawn, shown);
        using var bitmap = Render(doc, moved.Page);
        Assert.True(Red(At(bitmap, moved.Page, 275, 125)), "verschoben und ganz zu sehen");
        Assert.True(Blue(At(bitmap, moved.Page, 190, 170)), "der Nachbar in der Box");
        Assert.Equal(SKColors.White, At(bitmap, moved.Page, 230, 170));   // what the box hid of it stays hidden
    }

    /// <summary>
    /// A shading painted with "sh" and no clip of its own fills what the clips around it leave: moved, the area it showed moves with it (cut
    /// to it), not only its colours — as the tool shows it. Its area comes from the clip at "sh" (it was the clip of what was painted before).
    /// </summary>
    [Fact]
    public void ShadingWithoutClipOfItsOwn_TheAreaItShowedMoves()
    {
        var pdf = TestPdf.Simple(1);
        var function = TestPdf.Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", TestPdf.Box(0, 1)), ("C0", TestPdf.Box(0.9, 0, 0)),
            ("C1", TestPdf.Box(0.9, 0, 0)), ("N", PdfInteger.Of(1)));
        var shading = pdf.Add(TestPdf.Dict(("ShadingType", PdfInteger.Of(2)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("Coords", TestPdf.Box(0, 0, 600, 0)),
            ("Function", function), ("Extend", new PdfArray([PdfBoolean.True, PdfBoolean.True]))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("Shading", TestPdf.Dict(("Sh0", TestPdf.Ref(shading))))));
        // a clipped square before it: its clip ends with its block and must not count for the shading
        pdf.Replace(4, TestPdf.FlateStream("q 10 10 20 20 re W n 0 0 1 rg 0 0 50 50 re f Q q /Sh0 sh Q"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var item = PageItem.From(doc)[0];
        var all = ContentObjectEditor.ObjectsOf(item);
        var gradient = all.Single(o => o.Kind == ContentKind.Shading);
        Assert.Equal(RectD.FromLtrb(0, 0, 595.28, 841.89), gradient.Visible);   // the page: no clip
        var moved = ContentObjectEditor.Transform(item, [gradient.Key], Matrix.Translate(100, 100), all);
        using var bitmap = Render(doc, moved.Page);
        Assert.Equal(SKColors.White, At(bitmap, moved.Page, 50, 200));    // left of where it now lies
        Assert.True(Red(At(bitmap, moved.Page, 300, 300)));
        Assert.Equal(RectD.FromLtrb(100, 100, 695.28, 941.89), all.WithEdits(moved.ObjectEdits).VisibleOf(all.IndexOf(gradient.Key)!.Value));
    }

    /// <summary>
    /// A path that paints and also clips ("W f") without a block of its own: moved, its shape moves and its clip stays — what follows is cut as
    /// before. It was not moved at all, while the tool showed it moved.
    /// </summary>
    [Fact]
    public void PathThatAlsoClips_ShapeMoves_ClipStays()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("1 0 0 rg 100 100 50 50 re W f 0 0 1 rg 0 0 595 842 re f"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var item = PageItem.From(doc)[0];
        var all = ContentObjectEditor.ObjectsOf(item);
        var square = all.Single(o => o.AlsoClips);
        var moved = ContentObjectEditor.Transform(item, [square.Key], Matrix.Translate(200, 0), all);
        using var bitmap = Render(doc, moved.Page);
        Assert.True(Red(At(bitmap, moved.Page, 325, 125)), "die Form ist verschoben");
        Assert.True(Blue(At(bitmap, moved.Page, 125, 125)), "das Folgende bleibt auf die alte Stelle beschnitten");
        Assert.Equal(SKColors.White, At(bitmap, moved.Page, 125, 300));
        Assert.Equal(ContentObjects.Find(doc, moved.Page).Single(o => o.Kind == ContentKind.Path && Red(At(bitmap, moved.Page, 325, 125)) && o.Visible.Left > 250).Visible,
            all.WithEdits(moved.ObjectEdits).VisibleOf(all.IndexOf(square.Key)!.Value));
    }

    /// <summary>
    /// A large image cut keeps its resolution (docs/FEHLERGEDAECHTNIS.md, No. 252): 4,800 × 4,000 pixels, cut to its left half, gives 2,400
    /// pixels across — the display reduces images over 16 megapixels, and cutting took them from there (1,200). One over 64 megapixels is
    /// refused with a notice, not reduced.
    /// </summary>
    [Fact]
    public void Crop_LargeImageKeepsItsResolution_HugeOneRefused()
    {
        var pdf = TestPdf.Simple(1);
        const int w = 4800, h = 4000;
        var pixels = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                (pixels[(y * w + x) * 3], pixels[(y * w + x) * 3 + 1], pixels[(y * w + x) * 3 + 2]) = ((byte)(x * 255 / w), (byte)(y * 255 / h), 90);
        var large = pdf.Add(TestPdf.FlateStream(pixels, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(w)), ("Height", PdfInteger.Of(h)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8)))));
        // 9,000 × 8,000 announced; refused before anything is read
        var huge = pdf.Add(TestPdf.FlateStream(new byte[30], TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(9000)), ("Height", PdfInteger.Of(8000)), ("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8)))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("L", TestPdf.Ref(large)), ("H", TestPdf.Ref(huge))))));
        pdf.Replace(4, TestPdf.FlateStream("q 480 0 0 400 50 300 cm /L Do Q q 90 0 0 80 50 100 cm /H Do Q"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var item = PageItem.From(doc)[0];
        var all = ContentObjectEditor.ObjectsOf(item);
        var photo = all.Single(o => o.PixelWidth == w);
        var (_, images) = ContentObjectEditor.Crop(item, [photo.Key], RectD.FromLtrb(50, 300, 290, 700), SkiaImageCodec.Instance, all);
        var cut = Assert.Single(images);
        Assert.Equal(2400, cut.Dictionary.GetInteger(PdfName.Get("Width")));
        Assert.Equal(4000, cut.Dictionary.GetInteger(PdfName.Get("Height")));

        var tooLarge = all.Single(o => o.PixelWidth == 9000);
        var refused = Assert.Throws<PdfException>(() => ContentObjectEditor.Crop(item, [tooLarge.Key], RectD.FromLtrb(50, 100, 95, 180), SkiaImageCodec.Instance, all));
        Assert.Contains("72", refused.Message);
    }

    /// <summary>
    /// 8.5.2.1: after h the current point is the start of the subpath — a following l begins its segment there, as the display draws it: a
    /// click on the line from (100, 100) to (100, 300) hits, one on the far end of an imagined segment from the closing point does not. Before,
    /// the hit test began a subpath at the point of the l itself and missed the line the page shows (M6 draft c-85; docs/FEHLERGEDAECHTNIS.md,
    /// No. 1125).
    /// </summary>
    [Fact]
    public void Hits_LineAfterClose_BeginsAtTheStartOfTheSubpath()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(Encoding.ASCII.GetBytes("q 0 G 2 w 100 100 m 300 100 l 300 200 l h 100 300 l S Q")));
        using var doc = PdfDocument.Open(pdf.Build());
        var path = Assert.Single(ContentObjectEditor.ObjectsOf(PageItem.From(doc)[0]));
        Assert.True(path.Hits(100, 200, 0.5));   // on the segment the l draws from the start (100, 100)
    }
}
