using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using SkiaSharp;

namespace PdfKnead.Rendering;

/// <summary>
/// Redaction areas over a scan, grown to the ink (KONZEPT.md, section 5.6): the characters of a text recognition stand in boxes that need
/// not enclose what is printed — the round of a „b“, the tip of an ascender, a descender reach beyond them, and a bar along the box leaves
/// them visible, enough to read the first letter of a redacted word (docs/FEHLERGEDAECHTNIS.md, No. 351). The images of the page are drawn
/// around each area (the scan; text and drawings of the page itself do not count); the ink touching the area — every dark mark connected to dark pixels inside it — is followed out of it, and the area grows to
/// enclose it, by at most <see cref="ReachFactor"/> of its height on each side (a table rule or an underline through the word does not
/// pull the bar across the page). Marks of neighbouring words that do not touch the area stay as they are. For whole words the bar also
/// takes in marks of the same line closer to it than a quarter of the height of the ink — less than a space between words: a letter whose recognised
/// box lies beside its ink (or that the recognition left out) goes as well. A part of a word keeps to the ink it touches.
/// </summary>
public static class InkSnap
{
    /// <summary>Pixels per point of the drawing that decides what is ink.</summary>
    private const double PixelsPerPoint = 4;

    /// <summary>At most this many pixels per drawn window (larger windows are drawn coarser).</summary>
    private const long MaxPixels = 4_000_000;

    /// <summary>How far ink may lead the area beyond itself, as a share of its height (at least <see cref="MinReach"/> points).</summary>
    public const double ReachFactor = 0.6;

    private const double MinReach = 2;

    /// <summary>
    /// Margin around the ink found, in points: the soft edge of a scan pixel is lighter than ink and not counted, but it still shows the
    /// shape — one point is about a pixel of a scan at 72 dpi and more (docs/FEHLERGEDAECHTNIS.md, No. 351).
    /// </summary>
    private const double Margin = 1;

    /// <summary>The areas, each grown to enclose the ink connected to it.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="list">Its display list.</param>
    /// <param name="areas">The areas in page space.</param>
    /// <param name="images">Image cache for drawing.</param>
    /// <param name="wholeWords">The areas cover whole words: marks of the same line closer than a space between words go along.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>The areas, in the same order; never smaller than given.</returns>
    public static List<PdfRectangle> Grow(PdfDocument doc, PdfPage page, DisplayList list, IReadOnlyList<PdfRectangle> areas, ImageCache images,
        bool wholeWords = false, CancellationToken cancel = default)
    {
        var result = new List<PdfRectangle>(areas.Count);
        var marks = new List<PdfRectangle>();
        foreach (var area in areas)
        {
            cancel.ThrowIfCancellationRequested();
            result.Add(GrowOne(doc, page, list, area, images, wholeWords, marks, cancel));
        }
        // each mark found also by its own box: a slanted line is covered by thin strips, and a letter found by one strip reaches into the
        // next, which need not have grown as far (the foot of an „m“ beside a strip that found only the tail of a „g“)
        foreach (var mark in marks)
            if (!result.Any(r => r.Left <= mark.Left && r.Right >= mark.Right && r.Bottom <= mark.Bottom && r.Top >= mark.Top) &&
                !result.Contains(mark))
                result.Add(mark);
        return result;
    }

    private static PdfRectangle GrowOne(PdfDocument doc, PdfPage page, DisplayList list, PdfRectangle area, ImageCache images, bool wholeWords,
        List<PdfRectangle> marks, CancellationToken cancel)
    {
        double width = area.Right - area.Left, height = area.Top - area.Bottom;
        if (width <= 0 || height <= 0) return area;
        var reach = Math.Max(MinReach, ReachFactor * Math.Min(width, height));
        var window = RectD.FromLtrb(area.Left - reach, area.Bottom - reach, area.Right + reach, area.Top + reach);
        var scale = PixelsPerPoint;
        var toDevice = PageGeometry.PageToDevice(page, scale);
        var box = toDevice.TransformBounds(window);
        if (box.Width * box.Height > MaxPixels)
        {
            scale *= Math.Sqrt(MaxPixels / (box.Width * box.Height));
            toDevice = PageGeometry.PageToDevice(page, scale);
            box = toDevice.TransformBounds(window);
        }
        var tile = new SKRectI((int)Math.Floor(box.Left), (int)Math.Floor(box.Top), (int)Math.Ceiling(box.Right), (int)Math.Ceiling(box.Bottom));
        if (tile.Width < 3 || tile.Height < 3) return area;
        using var bitmap = new SKBitmap(new SKImageInfo(tile.Width, tile.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SkiaRenderer.Paper);
            SkiaRenderer.RenderTile(canvas, list, doc, toDevice, tile, images, OnlyImages(list), cancel);
        }
        int w = tile.Width, h = tile.Height;
        var data = bitmap.GetPixelSpan();
        var luminance = new byte[w * h];
        var histogram = new int[256];
        for (var i = 0; i < luminance.Length; i++)
        {
            var l = (byte)((data[i * 4] * 299 + data[i * 4 + 1] * 587 + data[i * 4 + 2] * 114) / 1000);
            luminance[i] = l;
            histogram[l]++;
        }
        // the paper: the level nine in ten pixels of the window reach (a scan's paper is not white); ink is clearly darker than it
        var paper = 255;
        for (int level = 255, count = 0; level >= 0; level--)
        {
            count += histogram[level];
            if (count >= luminance.Length / 10)
            {
                paper = level;
                break;
            }
        }
        var inkBelow = paper - Math.Max(60, paper * 0.3);
        if (inkBelow <= 0) return area;

        // the marks of the window: connected dark pixels, each with its bounds
        var label = new int[w * h];
        var bounds = new List<(int MinX, int MinY, int MaxX, int MaxY)>();
        var queue = new Queue<int>();
        for (var start = 0; start < label.Length; start++)
        {
            if (label[start] != 0 || luminance[start] >= inkBelow) continue;
            var id = bounds.Count + 1;
            label[start] = id;
            queue.Enqueue(start);
            int bMinX = int.MaxValue, bMinY = int.MaxValue, bMaxX = -1, bMaxY = -1;
            while (queue.Count > 0)
            {
                var at = queue.Dequeue();
                int x = at % w, y = at / w;
                bMinX = Math.Min(bMinX, x);
                bMaxX = Math.Max(bMaxX, x);
                bMinY = Math.Min(bMinY, y);
                bMaxY = Math.Max(bMaxY, y);
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                        var next = ny * w + nx;
                        if (label[next] != 0 || luminance[next] >= inkBelow) continue;
                        label[next] = id;
                        queue.Enqueue(next);
                    }
            }
            bounds.Add((bMinX, bMinY, bMaxX, bMaxY));
        }

        // the marks with ink inside the area itself
        var inside = toDevice.TransformBounds(RectD.FromLtrb(area.Left, area.Bottom, area.Right, area.Top));
        int ix0 = Math.Max(0, (int)Math.Floor(inside.Left) - tile.Left), ix1 = Math.Min(w, (int)Math.Ceiling(inside.Right) - tile.Left);
        int iy0 = Math.Max(0, (int)Math.Floor(inside.Top) - tile.Top), iy1 = Math.Min(h, (int)Math.Ceiling(inside.Bottom) - tile.Top);
        var taken = new HashSet<int>();
        for (var y = iy0; y < iy1; y++)
            for (var x = ix0; x < ix1; x++)
                if (label[y * w + x] is var id and > 0) taken.Add(id);
        if (taken.Count == 0) return area;
        (int MinX, int MinY, int MaxX, int MaxY) found = (int.MaxValue, int.MaxValue, -1, -1);
        foreach (var id in taken) found = Union(found, bounds[id - 1]);
        if (wholeWords)
        {
            // marks of the same line, closer than a space between words: a quarter of the height of the ink found (the area itself may be
            // one of the thin strips that cover a slanted line)
            var gap = Math.Max(1, (int)Math.Round(0.25 * Math.Max(inside.Bottom - inside.Top, found.MaxY - found.MinY + 1)));
            for (var added = true; added;)
            {
                added = false;
                for (var id = 1; id <= bounds.Count; id++)
                {
                    if (taken.Contains(id)) continue;
                    var b = bounds[id - 1];
                    var overlap = Math.Min(b.MaxY, found.MaxY) - Math.Max(b.MinY, found.MinY);
                    var horizontal = Math.Max(b.MinX - found.MaxX, found.MinX - b.MaxX);
                    if (overlap < 0.5 * Math.Min(b.MaxY - b.MinY + 1, found.MaxY - found.MinY + 1) || horizontal > gap) continue;
                    taken.Add(id);
                    found = Union(found, b);
                    added = true;
                }
            }
        }
        var (minX, minY, maxX, maxY) = found;
        // the ink found, with a margin, back in page space
        var toPage = toDevice.Invert();
        foreach (var id in taken)
        {
            var b = bounds[id - 1];
            var markBox = toPage.TransformBounds(RectD.FromLtrb(tile.Left + b.MinX, tile.Top + b.MinY, tile.Left + b.MaxX + 1, tile.Top + b.MaxY + 1));
            marks.Add(new PdfRectangle(markBox.Left - Margin, markBox.Top - Margin, markBox.Right + Margin, markBox.Bottom + Margin));
        }
        var ink = toPage.TransformBounds(RectD.FromLtrb(tile.Left + minX, tile.Top + minY, tile.Left + maxX + 1, tile.Top + maxY + 1));
        return new PdfRectangle(Math.Min(area.Left, ink.Left - Margin), Math.Min(area.Bottom, ink.Top - Margin), Math.Max(area.Right, ink.Right + Margin),
            Math.Max(area.Top, ink.Bottom + Margin));
    }

    /// <summary>
    /// Whether an image of the page shows ink in an area (marks clearly darker than the paper around it, at least a fiftieth of the area):
    /// recognised text over a scan stands for what is printed there — it is not hidden, a redaction must black out the ink (docs/FEHLERGEDAECHTNIS.md, No. 352).
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="list">Its display list.</param>
    /// <param name="area">The area in page space.</param>
    /// <param name="images">Image cache for drawing.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>true if ink shows there.</returns>
    public static bool HasInk(PdfDocument doc, PdfPage page, DisplayList list, PdfRectangle area, ImageCache images, CancellationToken cancel = default)
    {
        double width = area.Right - area.Left, height = area.Top - area.Bottom;
        if (width <= 0 || height <= 0) return false;
        var reach = Math.Max(MinReach, ReachFactor * Math.Min(width, height));
        var window = RectD.FromLtrb(area.Left - reach, area.Bottom - reach, area.Right + reach, area.Top + reach);
        var scale = 2.0;
        var toDevice = PageGeometry.PageToDevice(page, scale);
        var box = toDevice.TransformBounds(window);
        if (box.Width * box.Height > MaxPixels)
        {
            scale *= Math.Sqrt(MaxPixels / (box.Width * box.Height));
            toDevice = PageGeometry.PageToDevice(page, scale);
            box = toDevice.TransformBounds(window);
        }
        var tile = new SKRectI((int)Math.Floor(box.Left), (int)Math.Floor(box.Top), (int)Math.Ceiling(box.Right), (int)Math.Ceiling(box.Bottom));
        if (tile.Width < 3 || tile.Height < 3) return false;
        using var bitmap = new SKBitmap(new SKImageInfo(tile.Width, tile.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SkiaRenderer.Paper);
            SkiaRenderer.RenderTile(canvas, list, doc, toDevice, tile, images, OnlyImages(list), cancel);
        }
        var data = bitmap.GetPixelSpan();
        int w = tile.Width, h = tile.Height;
        var histogram = new int[256];
        var luminance = new byte[w * h];
        for (var i = 0; i < luminance.Length; i++)
        {
            luminance[i] = (byte)((data[i * 4] * 299 + data[i * 4 + 1] * 587 + data[i * 4 + 2] * 114) / 1000);
            histogram[luminance[i]]++;
        }
        var paper = 255;
        for (int level = 255, count = 0; level >= 0; level--)
        {
            count += histogram[level];
            if (count >= luminance.Length / 10)
            {
                paper = level;
                break;
            }
        }
        var inkBelow = paper - Math.Max(60, paper * 0.3);
        var inside = toDevice.TransformBounds(RectD.FromLtrb(area.Left, area.Bottom, area.Right, area.Top));
        int ix0 = Math.Max(0, (int)Math.Floor(inside.Left) - tile.Left), ix1 = Math.Min(w, (int)Math.Ceiling(inside.Right) - tile.Left);
        int iy0 = Math.Max(0, (int)Math.Floor(inside.Top) - tile.Top), iy1 = Math.Min(h, (int)Math.Ceiling(inside.Bottom) - tile.Top);
        int ink = 0, all = 0;
        for (var y = iy0; y < iy1; y++)
            for (var x = ix0; x < ix1; x++)
            {
                all++;
                if (luminance[y * w + x] < inkBelow) ink++;
            }
        return all > 0 && ink >= Math.Max(4, all / 50);
    }

    /// <summary>
    /// Only the images of the page are drawn: recognised text stands for the ink of a scan — text or drawings of the page itself under
    /// invisible text are something else (text hidden over a visible word must go without a bar, which would cover that word).
    /// </summary>
    private static Func<int, bool> OnlyImages(DisplayList list)
    {
        var items = list.Items;
        return i => (uint)i < (uint)items.Length && items.Span[i].Kind == DisplayItemKind.Image;
    }

    /// <summary>Whether the characters from <paramref name="start"/> to <paramref name="end"/> begin and end at the edges of words (no letter or
    /// digit just before or after them).</summary>
    /// <param name="text">The text.</param>
    /// <param name="start">First character.</param>
    /// <param name="end">Position after the last one.</param>
    /// <returns>true for whole words.</returns>
    public static bool WholeWords(string text, int start, int end) =>
        (start <= 0 || !char.IsLetterOrDigit(text[start - 1])) && (end >= text.Length || !char.IsLetterOrDigit(text[end]));

    private static (int MinX, int MinY, int MaxX, int MaxY) Union((int MinX, int MinY, int MaxX, int MaxY) a, (int MinX, int MinY, int MaxX, int MaxY) b) =>
        (Math.Min(a.MinX, b.MinX), Math.Min(a.MinY, b.MinY), Math.Max(a.MaxX, b.MaxX), Math.Max(a.MaxY, b.MaxY));
}
