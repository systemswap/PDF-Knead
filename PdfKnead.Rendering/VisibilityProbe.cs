using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using SkiaSharp;

namespace PdfKnead.Rendering;

/// <summary>
/// Measures by drawing whether characters can be seen (<see cref="IVisibilityProbe"/>; KONZEPT.md, section 5.18). The page is drawn in
/// tiles around the candidates, once complete and once without all candidates; where nothing differs around a candidate, it is hidden,
/// where its own pixels differ, it is visible. A candidate whose difference lies only where other candidates overlap it counts as hidden
/// for now. Then the hidden ones are left out together and compared with the complete page: wherever that still differs (a visible
/// character over a hidden one, a white character that hides another one), the uppermost of them that paints there by itself counts as
/// visible again, until leaving out all hidden ones changes nothing. A time limit per page keeps crafted pages short; beyond it, what is undecided counts as
/// visible and the result as incomplete.
/// </summary>
public sealed class VisibilityProbe : IVisibilityProbe
{
    /// <summary>The shared instance (it has no state).</summary>
    public static readonly VisibilityProbe Instance = new();

    /// <summary>A pixel differs when a colour channel changes by at least this much (about a tenth: fainter than that nobody sees).</summary>
    public const int Threshold = 24;

    /// <summary>
    /// Stricter for the joint check: leaving out all hidden characters together may change no channel by this much, so that removing
    /// them does not even soften the edge of visible text (a white character grazing a black one).
    /// </summary>
    public const int JointThreshold = 8;

    private const double Resolution = 2;          // pixels per point (a 4 pt character is 8 pixels high)
    private const double MinResolution = 0.5;
    private const long MaxPixels = 16_000_000;    // area of all candidates; beyond it the resolution drops
    private const int TileSize = 512;
    private const int MaxRounds = 6;              // rounds of the joint check
    // Work per measurement, instead of a time: the same page is judged the same on every computer and under any load. One unit is an entry of
    // the display list in a drawn tile, 64 drawn pixels or 32 decoded image pixels. The largest measurement over the public test collection took about a million
    // units (some seven seconds of work on a fast computer lie in this budget; docs/FEHLERGEDAECHTNIS.md, No. 405).
    private const long MaxWork = 8_000_000;
    // Decoded images per measurement: the images of a tile are all alive while it is drawn anyway; a smaller budget only made every
    // drawing of the same tile decode them again (forty blacked-out copies of 12 MB each: 18 s instead of 6, docs/FEHLERGEDAECHTNIS.md, No. 409)
    private const long ImageBudget = 512L << 20;
    private const long MaxKeptBytes = 64L << 20;  // complete drawings of the tiles kept for the joint check
    private const long MaxPaintBytes = 64L << 20; // where candidates paint by themselves, kept for the joint check

    /// <inheritdoc/>
    public VisibilityMeasure Measure(DisplayList list, PdfDocument doc, PdfPage page, IReadOnlyList<VisibilityCandidate> candidates, CancellationToken cancel)
    {
        var hidden = new bool[candidates.Count];
        if (candidates.Count == 0 || list.GlyphTags is not { } tags) return new VisibilityMeasure(hidden, true);

        // Resolution: as fine as possible within the pixel budget for the area of all candidates
        double area = 0;
        foreach (var c in candidates)
        {
            // only what lies on the page: one character with a broken matrix, far larger than the page, lowered it for all
            var b = c.Bounds.Intersect(list.PageBox);
            area += Math.Max(b.Width, 0) * Math.Max(b.Height, 0);
        }
        var scale = Math.Max(MinResolution, Math.Min(Resolution, Math.Sqrt(MaxPixels / Math.Max(area * page.UserUnit * page.UserUnit, 1))));
        var pageToDevice = PageGeometry.PageToDevice(page, scale);
        var (width, height) = PageGeometry.DeviceSize(page, scale);
        var images = new ImageCache(ImageBudget);
        // the complete drawing of each tile is kept for the joint check (it does not change), within a memory budget
        var keptTiles = new Dictionary<SKRectI, SKBitmap>();
        try
        {
            var context = new Context(list, doc, pageToDevice, images, tags, cancel);
            var rects = new SKRectI[candidates.Count];
            var isCandidate = new Dictionary<int, int>(candidates.Count);   // serial → candidate index
            for (var k = 0; k < candidates.Count; k++)
            {
                var r = pageToDevice.TransformBounds(candidates[k].Bounds);
                // a pixel around for anti-aliasing
                rects[k] = SKRectI.Intersect(new SKRectI((int)Math.Floor(r.Left) - 1, (int)Math.Floor(r.Top) - 1, (int)Math.Ceiling(r.Right) + 1,
                    (int)Math.Ceiling(r.Bottom) + 1), new SKRectI(0, 0, width, height));
                isCandidate[candidates[k].Serial] = k;
            }
            var tiles = Tiles(rects);
            var work = new Work(pageToDevice, list, images);

            // 1. Complete page against the page without all candidates
            var own = new long[candidates.Count];   // differing pixels in the candidate's area where no other candidate lies
            long keptBytes = 0;
            foreach (var (tile, members) in tiles)
            {
                cancel.ThrowIfCancellationRequested();
                if (work.Done > MaxWork) return new VisibilityMeasure(new bool[candidates.Count], false);
                work.Charge(tile, 2);
                var all = context.Render(tile, null);
                if (keptBytes < MaxKeptBytes)
                {
                    keptTiles[tile] = all;
                    keptBytes += all.ByteCount;
                }
                using var drop = keptTiles.ContainsKey(tile) ? null : all;
                using var without = context.Render(tile, i => tags[i] < 0 || !isCandidate.ContainsKey(tags[i]));
                var diff = Differences(all, without);
                var cover = new int[(tile.Width + 1) * (tile.Height + 1)];
                foreach (var k in members) Mark(cover, tile, rects[k], 1);
                Accumulate(cover, tile.Width + 1, tile.Height + 1);
                var single = Integral(diff, tile.Width, tile.Height, cover);
                foreach (var k in members) own[k] += Count(single, tile, rects[k]);
            }

            // 2. Decide: no difference — hidden; a difference of its own — visible; a difference only where others overlap it — hidden
            // for now, the joint check decides
            var complete = true;
            for (var k = 0; k < candidates.Count; k++)
            {
                if (rects[k].IsEmpty) continue;   // not on the drawn page (broken crop box): counts as visible
                hidden[k] = own[k] < Needed(rects[k]);
            }

            // 3. Joint check: leaving out all hidden ones together must change nothing
            var paints = new Dictionary<int, byte[]?>();   // per candidate: where it paints by itself (alpha), over its rectangle
            long paintBytes = 0;
            for (var round = 0; ; round++)
            {
                var changed = false;
                var last = round >= MaxRounds - 1;
                foreach (var (tile, members) in tiles)
                {
                    if (!members.Any(k => hidden[k])) continue;
                    cancel.ThrowIfCancellationRequested();
                    if (work.Done > MaxWork)
                    {
                        // out of work: what is not confirmed counts as visible
                        foreach (var k in members) hidden[k] = false;
                        complete = false;
                        changed = true;
                        continue;
                    }
                    work.Charge(tile, keptTiles.ContainsKey(tile) ? 1 : 2);
                    var all = keptTiles.TryGetValue(tile, out var cached) ? cached : context.Render(tile, null);
                    using var drop = cached is null ? all : null;
                    using var without = context.Render(tile, i => tags[i] < 0 || !(isCandidate.TryGetValue(tags[i], out var c) && hidden[c]));
                    var diff = Differences(all, without, JointThreshold);
                    if (Total(diff) == 0) continue;
                    var revealed = new HashSet<int>();
                    if (!last)
                    {
                        // per differing pixel, the uppermost (latest) hidden candidate that paints there by itself becomes visible again —
                        // not merely one whose rectangle lies there: a transparent character over visible text would be revealed instead
                        // of the text and stay in the file (docs/FEHLERGEDAECHTNIS.md, No. 144); where none paints, the uppermost one there
                        var top = new int[tile.Width * tile.Height];
                        var any = new int[tile.Width * tile.Height];
                        Array.Fill(top, -1);
                        Array.Fill(any, -1);
                        foreach (var k in members.Where(k => hidden[k]).OrderBy(k => candidates[k].Serial))
                        {
                            var r = SKRectI.Intersect(rects[k], tile);
                            if (r.IsEmpty) continue;
                            if (!paints.TryGetValue(k, out var alpha))
                            {
                                alpha = paintBytes < MaxPaintBytes && work.Done < MaxWork ? context.Alpha(rects[k], candidates[k].Serial) : null;
                                if (alpha is not null) work.Charge(rects[k], 1);
                                paints[k] = alpha;
                                paintBytes += alpha?.Length ?? 0;
                            }
                            for (var y = r.Top; y < r.Bottom; y++)
                            {
                                var row = (y - tile.Top) * tile.Width - tile.Left;
                                Array.Fill(any, k, row + r.Left, r.Width);
                                for (var x = r.Left; x < r.Right; x++)
                                    if (alpha is null || alpha[(y - rects[k].Top) * rects[k].Width + x - rects[k].Left] > 0) top[row + x] = k;
                            }
                        }
                        for (var p = 0; p < diff.Length; p++)
                            if (diff[p] != 0 && (top[p] >= 0 || any[p] >= 0)) revealed.Add(top[p] >= 0 ? top[p] : any[p]);
                    }
                    else
                    {
                        // last round: all hidden candidates where it still differs (a decision, not a gap: hundreds of copies of a
                        // character on one spot darken its edges a little, each one counts as visible)
                        var sum = Integral(diff, tile.Width, tile.Height, null);
                        foreach (var k in members)
                            if (hidden[k] && Count(sum, tile, rects[k]) > 0) revealed.Add(k);
                    }
                    foreach (var k in revealed) hidden[k] = false;
                    changed |= revealed.Count > 0;
                }
                // after the last kind of round, keep checking until nothing changes: revealing can itself uncover something
                if (!changed || round > MaxRounds + 8) break;
            }
            return new VisibilityMeasure(hidden, complete);
        }
        finally
        {
            foreach (var bitmap in keptTiles.Values) bitmap.Dispose();
            images.Detach();
        }
    }

    /// <summary>Differing pixels a candidate needs to count as visible: two, or one for a candidate of at most nine pixels.</summary>
    /// <inheritdoc/>
    public bool[]? Differences(DisplayList before, DisplayList after, PdfDocument doc, PdfPage page, IReadOnlyList<RectD> areas, bool joint,
        CancellationToken cancel)
    {
        var result = new bool[areas.Count];
        // Resolution as for Measure, within the pixel budget for the areas
        double area = 0;
        foreach (var a in areas)
        {
            var b = a.Intersect(before.PageBox);
            area += Math.Max(b.Width, 0) * Math.Max(b.Height, 0);
        }
        if (area <= 0) return result;   // nothing of it on the page
        var scale = Math.Max(MinResolution, Math.Min(Resolution, Math.Sqrt(MaxPixels / Math.Max(area * page.UserUnit * page.UserUnit, 1))));
        var pageToDevice = PageGeometry.PageToDevice(page, scale);
        var (width, height) = PageGeometry.DeviceSize(page, scale);
        var images = new ImageCache(ImageBudget);
        try
        {
            var rects = new SKRectI[areas.Count];
            for (var k = 0; k < areas.Count; k++)
            {
                var r = pageToDevice.TransformBounds(areas[k]);
                rects[k] = SKRectI.Intersect(new SKRectI((int)Math.Floor(r.Left) - 1, (int)Math.Floor(r.Top) - 1, (int)Math.Ceiling(r.Right) + 1,
                    (int)Math.Ceiling(r.Bottom) + 1), new SKRectI(0, 0, width, height));
            }
            var work = new Work(pageToDevice, before, images);
            var workAfter = new Work(pageToDevice, after, null);
            var one = new Context(before, doc, pageToDevice, images, before.GlyphTags ?? [], cancel);
            var two = new Context(after, doc, pageToDevice, images, after.GlyphTags ?? [], cancel);
            foreach (var (tile, members) in Tiles(rects))
            {
                cancel.ThrowIfCancellationRequested();
                if (work.Done + workAfter.Done > MaxWork) return null;
                work.Charge(tile, 1);
                workAfter.Charge(tile, 1);
                using var a = one.Render(tile, null);
                using var b = two.Render(tile, null);
                var sum = Integral(Differences(a, b, joint ? JointThreshold : Threshold), tile.Width, tile.Height, null);
                foreach (var k in members)
                    if (Count(sum, tile, rects[k]) >= (joint ? 1 : Needed(rects[k]))) result[k] = true;
            }
            return result;
        }
        finally
        {
            images.Detach();
        }
    }

    private static int Needed(SKRectI rect) => rect.Width * rect.Height <= 9 ? 1 : 2;

    /// <summary>Tiles of at most <see cref="TileSize"/> pixels with the candidates lying in them, each shrunk to the area of its candidates.</summary>
    private static List<(SKRectI Tile, List<int> Members)> Tiles(SKRectI[] rects)
    {
        var grid = new Dictionary<(int, int), List<int>>();
        for (var k = 0; k < rects.Length; k++)
        {
            var r = rects[k];
            if (r.IsEmpty) continue;
            for (var ty = r.Top / TileSize; ty <= (r.Bottom - 1) / TileSize; ty++)
            for (var tx = r.Left / TileSize; tx <= (r.Right - 1) / TileSize; tx++)
            {
                if (!grid.TryGetValue((tx, ty), out var members)) grid[(tx, ty)] = members = [];
                members.Add(k);
            }
        }
        var tiles = new List<(SKRectI, List<int>)>(grid.Count);
        foreach (var ((tx, ty), members) in grid)
        {
            var cell = new SKRectI(tx * TileSize, ty * TileSize, (tx + 1) * TileSize, (ty + 1) * TileSize);
            var used = SKRectI.Empty;
            foreach (var k in members) used = used.IsEmpty ? rects[k] : SKRectI.Union(used, rects[k]);
            tiles.Add((SKRectI.Intersect(cell, used), members));
        }
        return tiles;
    }

    /// <summary>1 where the two bitmaps differ by at least <paramref name="threshold"/> in a channel, otherwise 0.</summary>
    private static byte[] Differences(SKBitmap a, SKBitmap b, int threshold = Threshold)
    {
        var w = a.Width;
        var h = a.Height;
        var result = new byte[w * h];
        var pa = a.GetPixelSpan();
        var pb = b.GetPixelSpan();
        int rowA = a.RowBytes, rowB = b.RowBytes;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            int ia = y * rowA + x * 4, ib = y * rowB + x * 4;
            var d = Math.Max(Math.Abs(pa[ia] - pb[ib]), Math.Max(Math.Abs(pa[ia + 1] - pb[ib + 1]), Math.Abs(pa[ia + 2] - pb[ib + 2])));
            if (d >= threshold) result[y * w + x] = 1;
        }
        return result;
    }

    private static long Total(byte[] diff)
    {
        long n = 0;
        foreach (var d in diff) n += d;
        return n;
    }

    /// <summary>Adds <paramref name="value"/> over the part of <paramref name="rect"/> inside the tile (difference array, see <see cref="Accumulate"/>).</summary>
    private static void Mark(int[] cover, SKRectI tile, SKRectI rect, int value)
    {
        var r = SKRectI.Intersect(rect, tile);
        if (r.IsEmpty) return;
        var stride = tile.Width + 1;
        int x0 = r.Left - tile.Left, y0 = r.Top - tile.Top, x1 = r.Right - tile.Left, y1 = r.Bottom - tile.Top;
        cover[y0 * stride + x0] += value;
        cover[y0 * stride + x1] -= value;
        cover[y1 * stride + x0] -= value;
        cover[y1 * stride + x1] += value;
    }

    /// <summary>Turns the difference array into the number of rectangles over each pixel.</summary>
    private static void Accumulate(int[] cover, int stride, int rows)
    {
        for (var y = 0; y < rows; y++)
        for (var x = 1; x < stride; x++)
            cover[y * stride + x] += cover[y * stride + x - 1];
        for (var y = 1; y < rows; y++)
        for (var x = 0; x < stride; x++)
            cover[y * stride + x] += cover[(y - 1) * stride + x];
    }

    /// <summary>Summed-area table of the differences (only where exactly one candidate lies, if <paramref name="cover"/> is given).</summary>
    private static long[] Integral(byte[] diff, int w, int h, int[]? cover)
    {
        var table = new long[(w + 1) * (h + 1)];
        for (var y = 0; y < h; y++)
        {
            long row = 0;
            for (var x = 0; x < w; x++)
            {
                if (diff[y * w + x] != 0 && (cover is null || cover[y * (w + 1) + x] == 1)) row++;
                table[(y + 1) * (w + 1) + x + 1] = table[y * (w + 1) + x + 1] + row;
            }
        }
        return table;
    }

    /// <summary>Differing pixels inside the part of <paramref name="rect"/> in the tile, from the summed-area table.</summary>
    private static long Count(long[] table, SKRectI tile, SKRectI rect)
    {
        var r = SKRectI.Intersect(rect, tile);
        if (r.IsEmpty) return 0;
        var stride = tile.Width + 1;
        int x0 = r.Left - tile.Left, y0 = r.Top - tile.Top, x1 = r.Right - tile.Left, y1 = r.Bottom - tile.Top;
        return table[y1 * stride + x1] - table[y0 * stride + x1] - table[y1 * stride + x0] + table[y0 * stride + x0];
    }

    /// <summary>What every drawing of one measurement shares.</summary>
    private sealed class Context(DisplayList list, PdfDocument doc, Matrix pageToDevice, ImageCache images, int[] tags, CancellationToken cancel)
    {
        /// <summary>Draws the area on paper, all entries or those for which <paramref name="include"/> is true.</summary>
        public SKBitmap Render(SKRectI area, Func<int, bool>? include)
        {
            var bitmap = new SKBitmap(new SKImageInfo(Math.Max(1, area.Width), Math.Max(1, area.Height), SKColorType.Rgba8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SkiaRenderer.Paper);
            if (include is null) SkiaRenderer.RenderTile(canvas, list, doc, pageToDevice, area, images, cancel);
            else SkiaRenderer.RenderTile(canvas, list, doc, pageToDevice, area, images, i => i >= tags.Length || include(i), cancel);
            return bitmap;
        }

        /// <summary>Where the character <paramref name="serial"/> paints by itself: its entries alone on a transparent ground, alpha per pixel.</summary>
        public byte[] Alpha(SKRectI area, int serial)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(Math.Max(1, area.Width), Math.Max(1, area.Height), SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                SkiaRenderer.RenderTile(canvas, list, doc, pageToDevice, area, images, i => i < tags.Length && tags[i] == serial, cancel);
            }
            var pixels = bitmap.GetPixelSpan();
            var alpha = new byte[area.Width * area.Height];
            for (var y = 0; y < area.Height; y++)
                for (var x = 0; x < area.Width; x++)
                    alpha[y * area.Width + x] = pixels[y * bitmap.RowBytes + x * 4 + 3];
            return alpha;
        }
    }

    /// <summary>
    /// The work of a measurement, counted instead of timed: per drawing of an area, the entries of the display list in it (an estimate from
    /// the spatial index) and one unit per 64 pixels.
    /// </summary>
    private sealed class Work(Matrix pageToDevice, DisplayList list, ImageCache? images)
    {
        private readonly Matrix _deviceToPage = pageToDevice.IsInvertible ? pageToDevice.Invert() : Matrix.Identity;
        private long _drawn;

        /// <summary>Units counted so far: the drawn tiles, and one unit per 32 decoded pixels of the images (decoding a pixel costs about
        /// twice drawing one; a soft mask of 150 million pixels took three seconds and was not counted, docs/FEHLERGEDAECHTNIS.md,
        /// No. 409).</summary>
        public long Done => _drawn + (images?.DecodedPixels ?? 0) / 32;

        /// <summary>Counts <paramref name="drawings"/> drawings of <paramref name="area"/> (device pixels).</summary>
        public void Charge(SKRectI area, int drawings)
        {
            var page = _deviceToPage.TransformBounds(new RectD(area.Left, area.Top, area.Right, area.Bottom));
            _drawn += drawings * ((long)list.Index.EstimateCount(page) + (long)area.Width * area.Height / 64);
        }
    }
}
