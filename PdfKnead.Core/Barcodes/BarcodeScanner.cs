using PdfKnead.Core.Barcodes.DataMatrix;
using PdfKnead.Core.Barcodes.Linear;
using PdfKnead.Core.Barcodes.Qr;

namespace PdfKnead.Core.Barcodes;

/// <summary>A greyscale image, row by row from the top (0 = black, 255 = white).</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Pixels">One byte per pixel.</param>
public sealed record GrayImage(int Width, int Height, byte[] Pixels);

/// <summary>Kind of a code.</summary>
public enum BarcodeFormat
{
    /// <summary>QR code.</summary>
    Qr,

    /// <summary>Data Matrix (ECC 200).</summary>
    DataMatrix,

    /// <summary>EAN-13.</summary>
    Ean13,

    /// <summary>EAN-8.</summary>
    Ean8,

    /// <summary>UPC-A.</summary>
    UpcA,

    /// <summary>UPC-E.</summary>
    UpcE,

    /// <summary>Code 128.</summary>
    Code128,

    /// <summary>GS1-128.</summary>
    Gs1_128,

    /// <summary>Code 39.</summary>
    Code39,

    /// <summary>Interleaved 2 of 5.</summary>
    Itf,
}

/// <summary>A code found in an image.</summary>
public sealed record FoundBarcode
{
    /// <summary>The kind.</summary>
    public BarcodeFormat Format { get; init; }

    /// <summary>The content.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// Corners in image pixels in the reading orientation of the code: top left, top right, bottom right, bottom left (for bar codes the
    /// rectangle of the bars).
    /// </summary>
    public required (double X, double Y)[] Corners { get; init; }

    /// <summary>Size of one module (the narrowest bar) in pixels.</summary>
    public double Module { get; init; }

    /// <summary>Light space around the code, in modules (the smallest of the four sides; at the image edge as far as visible).</summary>
    public double QuietZone { get; init; }

    /// <summary>The QR content with its structure; null for other kinds.</summary>
    public QrContent? Qr { get; init; }

    /// <summary>The Data Matrix content with its structure; null for other kinds.</summary>
    public DataMatrixContent? DataMatrix { get; init; }

    /// <summary>The bar code content; null for 2D codes.</summary>
    public LinearContent? Linear { get; init; }

    /// <summary>The code reaches the edge of the image: a larger image may be needed to read it.</summary>
    public bool TouchesEdge { get; init; }
}

/// <summary>
/// Finds and reads QR codes, Data Matrix and bar codes in a rendered image: threshold, the area of the code around a point, its smallest
/// enclosing rectangle (codes may be turned), then the finder patterns of QR (ISO/IEC 18004, 6.3.3) or Data Matrix (ISO/IEC 16022, 5.1),
/// or scan lines across the bars. Meant for images rendered from PDF pages — flat, sharp, not distorted by perspective.
/// </summary>
public static class BarcodeScanner
{
    /// <summary>At most this many areas per image are handed on for a closer look.</summary>
    private const int MaxSuspects = 8;

    /// <summary>Reads the code at or next to a point.</summary>
    /// <param name="image">The image.</param>
    /// <param name="x">X of the point in pixels.</param>
    /// <param name="y">Y of the point in pixels.</param>
    /// <returns>The code; null if there is none there or it cannot be read.</returns>
    public static FoundBarcode? ReadAt(GrayImage image, double x, double y)
    {
        var threshold = Otsu(image);
        var dark = new bool[image.Width * image.Height];
        for (var i = 0; i < dark.Length; i++) dark[i] = image.Pixels[i] < threshold;
        int cx = Math.Clamp((int)x, 0, image.Width - 1), cy = Math.Clamp((int)y, 0, image.Height - 1);
        var (small, typical) = ModuleNear(dark, image.Width, image.Height, cx, cy);
        if (small <= 0) return null;
        // gaps of up to about four modules bridged (bar codes), or two (2D codes close to other content); with both estimates of the
        // module, as one alone misjudges some codes (2D codes have few runs of single modules, printed bars thin spaces)
        var tried = new HashSet<int>();
        foreach (var module in new[] { typical, small }.Distinct())
            foreach (var bridge in new[] { 2.3, 1.2 })
            {
                var pixels = (int)Math.Ceiling(bridge * module);
                if (!tried.Add(pixels) || Area(dark, image.Width, image.Height, cx, cy, pixels) is not { } area) continue;
                if (Read(image, dark, threshold, area, module) is { } found) return found;
            }
        return null;
    }

    /// <summary>
    /// Where the code at a point probably is, without reading it: the rectangle around the dark area there (gaps of about four
    /// modules bridged), the module size, and whether the area reaches the edge of the image — to render the right part of a page
    /// at the right resolution before reading.
    /// </summary>
    /// <returns>Corners in image pixels, module size in pixels and whether the image edge is reached; null if there is nothing dark near the point.</returns>
    public static ((double X, double Y)[] Corners, double Module, bool TouchesEdge)? Locate(GrayImage image, double x, double y)
    {
        var threshold = Otsu(image);
        var dark = new bool[image.Width * image.Height];
        for (var i = 0; i < dark.Length; i++) dark[i] = image.Pixels[i] < threshold;
        int cx = Math.Clamp((int)x, 0, image.Width - 1), cy = Math.Clamp((int)y, 0, image.Height - 1);
        var module = ModuleNear(dark, image.Width, image.Height, cx, cy).Typical;
        if (module <= 0 || Area(dark, image.Width, image.Height, cx, cy, (int)Math.Ceiling(2.3 * module)) is not { } area) return null;
        var rect = MinimumRectangle(area.Rows);
        if (rect.Length != 4) return null;
        return (rect, module, area.TouchesEdge);
    }

    /// <summary>
    /// All codes in an image (a whole page): dark areas of a plausible size, with small and with large gaps bridged (2D codes beside
    /// text, bar codes with wide spaces), each read on a cut-out of its own — so the cost follows the areas, not the image.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="cancel">Cancels the search.</param>
    /// <returns>The codes, corners in image pixels; each code once.</returns>
    public static IReadOnlyList<FoundBarcode> FindAll(GrayImage image, CancellationToken cancel = default) => Search(image, cancel).Found;

    /// <summary>
    /// <see cref="FindAll"/>, and the areas that look like a code (bars of equal height, finder patterns at three corners, the L of
    /// Data Matrix) but cannot be read at this resolution — to be looked at more closely.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="cancel">Cancels the search.</param>
    /// <returns>The codes read, and the suspect areas (rectangles in image pixels).</returns>
    public static (IReadOnlyList<FoundBarcode> Found, IReadOnlyList<(int Left, int Top, int Right, int Bottom)> Suspects) Search(GrayImage image,
        CancellationToken cancel = default)
    {
        var (found, suspects) = SearchAt(image, cancel);
        // coarse codes (modules of several points: a large QR code on a poster) fall apart at this resolution, where their light modules
        // are wider than the bridged gaps; at a quarter they are ordinary (and the search there costs a sixteenth)
        const int Coarse = 4;
        if (image.Width >= 64 * Coarse && image.Height >= 64 * Coarse)
        {
            var coarse = SearchAt(Reduced(image, Coarse), cancel).Found;
            foreach (var c in coarse)
            {
                if (found.Any(f => f.Text == c.Text)) continue;
                found.Add(c with
                {
                    Corners = [.. c.Corners.Select(p => (p.X * Coarse, p.Y * Coarse))], Module = c.Module * Coarse, QuietZone = c.QuietZone,
                });
            }
        }
        return (found, suspects);
    }

    private static (List<FoundBarcode> Found, List<(int Left, int Top, int Right, int Bottom)> Suspects) SearchAt(GrayImage image, CancellationToken cancel)
    {
        int w = image.Width, h = image.Height;
        var threshold = Otsu(image);
        var dark = new bool[w * h];
        for (var i = 0; i < dark.Length; i++) dark[i] = image.Pixels[i] < threshold;
        var found = new List<FoundBarcode>();
        var suspects = new List<(int, int, int, int)>();
        var tried = new List<(int L, int T, int R, int B)>();
        // the areas are found at half resolution (a pixel is dark if any of its four is): a quarter of the work, and the reading below
        // works on the full image
        int hw = (w + 1) / 2, hh = (h + 1) / 2;
        var half = new bool[hw * hh];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (dark[y * w + x]) half[(y >> 1) * hw + (x >> 1)] = true;
        // 2D codes beside text: small gaps; coarse 2D codes (large modules, so their light modules are wide gaps): a larger one; bar
        // codes: wide gaps (up to four modules) along the code only, so lines of text above each other stay apart (in half pixels)
        foreach (var (bridgeX, bridgeY) in new[] { (1, 1), (7, 0), (0, 7), (4, 4) })
        {
            foreach (var (hl, ht, hr, hb, _) in Components(half, hw, hh, bridgeX, bridgeY))
            {
                int left = hl * 2, top = ht * 2, right = Math.Min(w - 1, hr * 2 + 1), bottom = Math.Min(h - 1, hb * 2 + 1);
                cancel.ThrowIfCancellationRequested();
                int bw = right - left + 1, bh = bottom - top + 1;
                // too small for any code (21 modules of at least 1 pixel; bar codes at least 8 pixels high)
                if (Math.Max(bw, bh) < 20 || Math.Min(bw, bh) < 8) continue;
                // the same area again with the larger bridge
                if (tried.Any(t => t.L == left && t.T == top && t.R == right && t.B == bottom)) continue;
                // nearly empty (a frame) or nearly solid
                var fill = (double)DarkCount(dark, w, left, top, right, bottom) / ((long)bw * bh);
                if (fill is < 0.12 or > 0.93) continue;
                // cheap first: changes of light and dark through the middle in both directions (2D codes), or bars of equal height;
                // words and lines of text have few changes across them and fail here
                // the one-way bridges are for bar codes only: across columns of text they join whole paragraphs, which a 2D check would pass
                var bars = bridgeX != bridgeY;
                // rectangular Data Matrix symbols (down to 8 rows) have too few changes across; their L of two solid sides lets them in
                if (!(bars ? LooksLikeBars(dark, w, left, top, right, bottom)
                        : Textured(dark, w, left, top, right, bottom) || LooksLikeBars(dark, w, left, top, right, bottom)
                          || SolidCorner(dark, w, left, top, right, bottom))) continue;
                tried.Add((left, top, right, bottom));
                double cx = (left + right) / 2.0, cy = (top + bottom) / 2.0;
                if (found.Any(f => Inside(f.Corners, cx, cy))) continue;
                // a cut-out with room for the quiet zone
                var margin = Math.Max(12, Math.Max(bw, bh) / 4);
                int x0 = Math.Max(0, left - margin), y0 = Math.Max(0, top - margin);
                int x1 = Math.Min(w, right + 1 + margin), y1 = Math.Min(h, bottom + 1 + margin);
                var crop = new byte[(x1 - x0) * (y1 - y0)];
                for (var y = y0; y < y1; y++) Array.Copy(image.Pixels, y * w + x0, crop, (y - y0) * (x1 - x0), x1 - x0);
                // read at a dark pixel near the middle (the middle itself may be a light module)
                if (DarkNear(dark, w, h, (int)cx, (int)cy, Math.Min(bw, bh) / 2) is not var (px, py)) continue;
                if (ReadAt(new GrayImage(x1 - x0, y1 - y0, crop), px - x0 + 0.5, py - y0 + 0.5) is not { } code)
                {
                    if (LooksLikeCode(dark, w, left, top, right, bottom) || SmallSquare(dark, w, left, top, right, bottom)) suspects.Add((left, top, right, bottom));
                    continue;
                }
                var corners = code.Corners.Select(c => (c.X + x0, c.Y + y0)).ToArray();
                var c0 = At(corners, 0.5, 0.5);
                if (found.Any(f => Inside(f.Corners, c0.X, c0.Y))) continue;
                found.Add(code with
                {
                    Corners = corners,
                    TouchesEdge = corners.Any(c => c.Item1 <= 1 || c.Item2 <= 1 || c.Item1 >= w - 2 || c.Item2 >= h - 2),
                });
            }
        }
        // suspects inside a code that was read are pieces of it; the largest few are enough (each costs a closer look)
        suspects.RemoveAll(r => found.Any(f => Inside(f.Corners, (r.Item1 + r.Item3) / 2.0, (r.Item2 + r.Item4) / 2.0)));
        return (found, [.. suspects.Take(MaxSuspects)]);
    }

    /// <summary>
    /// Whether an area looks like a code although it could not be read: bars of the same height (the edges along three lines across
    /// it agree), or square with finder patterns in three corners, or two adjacent sides dark throughout (Data Matrix).
    /// </summary>
    private static bool LooksLikeCode(bool[] dark, int w, int left, int top, int right, int bottom) =>
        LooksLikeBars(dark, w, left, top, right, bottom) || LooksLike2D(dark, w, left, top, right, bottom);

    /// <summary>
    /// At least six changes of light and dark along two of three rows and two of three columns through the area (at 30, 50 and 70 %):
    /// a 2D code, or a turned bar code. The smallest QR code has 21 modules, a line through it often fewer than ten changes; a word has
    /// six at most where a line meets a letter like „E“, rarely along two.
    /// </summary>
    private static bool Textured(bool[] dark, int w, int left, int top, int right, int bottom)
    {
        int bw = right - left + 1, bh = bottom - top + 1, rows = 0, columns = 0;
        foreach (var at in new[] { 0.3, 0.5, 0.7 })
        {
            int row = top + (int)(bh * at), column = left + (int)(bw * at), across = 0, down = 0;
            for (var x = left + 1; x <= right; x++)
                if (dark[row * w + x] != dark[row * w + x - 1]) across++;
            for (var y = top + 1; y <= bottom; y++)
                if (dark[y * w + column] != dark[(y - 1) * w + column]) down++;
            if (across >= 6) rows++;
            if (down >= 6) columns++;
        }
        // two of three each way: one line may run through a finder pattern
        return rows >= 2 && columns >= 2;
    }

    /// <summary>Bars of the same height: the edges along three lines across the area agree (rows for upright bars, columns for lying ones).</summary>
    private static bool LooksLikeBars(bool[] dark, int w, int left, int top, int right, int bottom)
    {
        int bw = right - left + 1, bh = bottom - top + 1;
        bool D(int x, int y) => dark[y * w + x];
        for (var axis = 0; axis < 2; axis++)
        {
            var length = axis == 0 ? bw : bh;
            var across = axis == 0 ? bh : bw;
            if (across < 8 || length < 20) continue;
            List<int> Edges(double at)
            {
                var edges = new List<int>();
                var line = (int)(across * at);
                var last = false;
                for (var i = 0; i < length; i++)
                {
                    var d = axis == 0 ? D(left + i, top + line) : D(left + line, top + i);
                    if (d != last) edges.Add(i);
                    last = d;
                }
                return edges;
            }
            var a = Edges(0.3);
            if (a.Count < 30) continue;
            var agree = 0;
            foreach (var other in new[] { Edges(0.5), Edges(0.7) })
                if (other.Count >= a.Count * 0.8 && other.Count(e => a.BinarySearch(e) is var k && (k >= 0 || ~k < a.Count && a[~k] - e <= 2 || ~k > 0 && e - a[~k - 1] <= 2)) >= other.Count * 0.85)
                    agree++;
            if (agree < 2) continue;
            // a bar is dark along its whole length; lines of text lying above each other have matching edges too, but are not. The
            // length is that of the typical bar (the median), which must make up most of the area: below a short bar code its text
            // belongs to the area too (EAN-8 with digits: the bars cover three quarters of it)
            var lengths = new List<int>();
            for (var i = 0; i + 1 < a.Count; i += 2)
            {
                var at = (a[i] + a[i + 1]) / 2;
                var count = 0;
                for (var k = 0; k < across; k++)
                    if (axis == 0 ? D(left + at, top + k) : D(left + k, top + at)) count++;
                lengths.Add(count);
            }
            if (lengths.Count == 0) continue;
            var typical = lengths.Order().ElementAt(lengths.Count / 2);
            if (typical >= across * 0.6 && lengths.Count(l => l >= typical * 0.85) >= lengths.Count * 0.8) return true;
        }
        return false;
    }

    /// <summary>Square with finder patterns in three corners (QR), or two adjacent sides dark throughout (Data Matrix).</summary>
    private static bool LooksLike2D(bool[] dark, int w, int left, int top, int right, int bottom)
    {
        // 2D: dark on two adjacent sides throughout (Data Matrix, also rectangular), or a dark square ring in three corners (QR)
        if (SolidCorner(dark, w, left, top, right, bottom)) return true;
        int bw = right - left + 1, bh = bottom - top + 1;
        bool D(int x, int y) => dark[y * w + x];
        if (Math.Max(bw, bh) > Math.Min(bw, bh) * 1.3 || Math.Min(bw, bh) < 21) return false;
        double Side(int x0, int y0, int dx, int dy, int n)
        {
            var count = 0;
            for (var i = 0; i < n; i++)
                if (D(x0 + dx * i, y0 + dy * i)) count++;
            return (double)count / n;
        }
        bool Ring(int cx, int cy, int size) => Side(cx, cy, 1, 0, size) > 0.9 && Side(cx, cy, 0, 1, size) > 0.9;
        var finder = Math.Max(3, Math.Min(bw, bh) / 6);
        var corners = (Ring(left, top, finder) ? 1 : 0) + (Ring(right - finder + 1, top, finder) ? 1 : 0) + (Ring(left, bottom - finder + 1, finder) ? 1 : 0)
                      + (Ring(right - finder + 1, bottom - finder + 1, finder) ? 1 : 0);
        return corners >= 3;
    }

    /// <summary>
    /// The finder of a Data Matrix symbol: two adjacent sides of the area dark throughout (the L), the two others alternating (the clock
    /// track, at least four changes each) — a table corner has the L but no clock track. Square or rectangular up to 5 : 1 (the flattest
    /// symbol has 8 × 32 modules), at least 12 pixels across. Each side is looked at on its outermost line and the next one (the edge of an
    /// area found at half resolution may be a light partial row).
    /// </summary>
    private static bool SolidCorner(bool[] dark, int w, int left, int top, int right, int bottom)
    {
        int bw = right - left + 1, bh = bottom - top + 1;
        if (Math.Min(bw, bh) < 12 || Math.Max(bw, bh) > Math.Min(bw, bh) * 5) return false;
        double Side(int x0, int y0, int dx, int dy, int n)
        {
            var count = 0;
            for (var i = 0; i < n; i++)
                if (dark[(y0 + dy * i) * w + x0 + dx * i]) count++;
            return (double)count / n;
        }
        int Changes(int x0, int y0, int dx, int dy, int n)
        {
            var count = 0;
            for (var i = 1; i < n; i++)
                if (dark[(y0 + dy * i) * w + x0 + dx * i] != dark[(y0 + dy * (i - 1)) * w + x0 + dx * (i - 1)]) count++;
            return count;
        }
        // top, right, bottom, left: start, direction, length, step inwards
        (int X, int Y, int Dx, int Dy, int N, int InX, int InY)[] lines =
            [(left, top, 1, 0, bw, 0, 1), (right, top, 0, 1, bh, -1, 0), (left, bottom, 1, 0, bw, 0, -1), (left, top, 0, 1, bh, 1, 0)];
        double Solid(int k) => Math.Max(Side(lines[k].X, lines[k].Y, lines[k].Dx, lines[k].Dy, lines[k].N),
            Side(lines[k].X + lines[k].InX, lines[k].Y + lines[k].InY, lines[k].Dx, lines[k].Dy, lines[k].N));
        int Clock(int k) => Math.Max(Changes(lines[k].X, lines[k].Y, lines[k].Dx, lines[k].Dy, lines[k].N),
            Changes(lines[k].X + lines[k].InX, lines[k].Y + lines[k].InY, lines[k].Dx, lines[k].Dy, lines[k].N));
        for (var k = 0; k < 4; k++)
            if (Solid(k) > 0.9 && Solid((k + 1) % 4) > 0.9 && Clock((k + 2) % 4) >= 4 && Clock((k + 3) % 4) >= 4) return true;
        return false;
    }

    /// <summary>
    /// A small, nearly square, densely patterned area: a QR code of the smallest recommended size (modules of 0.25 mm, about two pixels at
    /// the page search's resolution), whose finder patterns this coarse do not show as rings — a suspect worth drawing finer.
    /// </summary>
    private static bool SmallSquare(bool[] dark, int w, int left, int top, int right, int bottom)
    {
        int bw = right - left + 1, bh = bottom - top + 1;
        if (Math.Max(bw, bh) > Math.Min(bw, bh) * 1.2 || Math.Min(bw, bh) < 38 || Math.Max(bw, bh) > 200) return false;
        var fill = (double)DarkCount(dark, w, left, top, right, bottom) / ((long)bw * bh);
        return fill is > 0.3 and < 0.7;
    }

    private static int DarkCount(bool[] dark, int w, int left, int top, int right, int bottom)
    {
        var count = 0;
        for (var y = top; y <= bottom; y++)
            for (var x = left; x <= right; x++)
                if (dark[y * w + x]) count++;
        return count;
    }

    private static (int X, int Y)? DarkNear(bool[] dark, int w, int h, int cx, int cy, int reach)
    {
        for (var r = 0; r <= reach; r++)
            for (var dy = -r; dy <= r; dy++)
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    int x = cx + dx, y = cy + dy;
                    if (x >= 0 && y >= 0 && x < w && y < h && dark[y * w + x]) return (x, y);
                }
        return null;
    }

    private static bool Inside((double X, double Y)[] quad, double x, double y)
    {
        // convex quadrilateral: on the same side of all four edges
        var sign = 0;
        for (var i = 0; i < 4; i++)
        {
            var (ax, ay) = quad[i];
            var (bx, by) = quad[(i + 1) % 4];
            var cross = (bx - ax) * (y - ay) - (by - ay) * (x - ax);
            var s = Math.Sign(cross);
            if (s == 0) continue;
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }

    /// <summary>Connected dark areas with gaps of up to 2 × <paramref name="bridgeX"/> pixels across and 2 × <paramref name="bridgeY"/> down closed: bounds and dark pixel count.</summary>
    private static List<(int Left, int Top, int Right, int Bottom, int Count)> Components(bool[] dark, int w, int h, int bridgeX, int bridgeY)
    {
        var grown = Grow(dark, w, h, bridgeX, bridgeY);
        var seen = new bool[dark.Length];
        var result = new List<(int, int, int, int, int)>();
        var stack = new Stack<int>();
        for (var start = 0; start < dark.Length; start++)
        {
            if (seen[start] || !grown[start]) continue;
            int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1, count = 0;
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                int x = i % w, y = i / w;
                if (dark[i])
                {
                    count++;
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
                if (x > 0 && !seen[i - 1] && grown[i - 1]) { seen[i - 1] = true; stack.Push(i - 1); }
                if (x < w - 1 && !seen[i + 1] && grown[i + 1]) { seen[i + 1] = true; stack.Push(i + 1); }
                if (y > 0 && !seen[i - w] && grown[i - w]) { seen[i - w] = true; stack.Push(i - w); }
                if (y < h - 1 && !seen[i + w] && grown[i + w]) { seen[i + w] = true; stack.Push(i + w); }
            }
            if (count > 0) result.Add((left, top, right, bottom, count));
        }
        // larger areas first: a code is read before the pieces of it
        result.Sort((a, b) => ((long)(b.Item3 - b.Item1) * (b.Item4 - b.Item2)).CompareTo((long)(a.Item3 - a.Item1) * (a.Item4 - a.Item2)));
        return result;
    }

    /// <summary>The image reduced by an integer factor, each pixel the mean of its block (to find coarse codes cheaply).</summary>
    public static GrayImage Reduced(GrayImage image, int factor)
    {
        int w = Math.Max(1, image.Width / factor), h = Math.Max(1, image.Height / factor);
        var pixels = new byte[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var sum = 0;
                for (var dy = 0; dy < factor; dy++)
                    for (var dx = 0; dx < factor; dx++) sum += image.Pixels[(y * factor + dy) * image.Width + x * factor + dx];
                pixels[y * w + x] = (byte)(sum / (factor * factor));
            }
        return new GrayImage(w, h, pixels);
    }

    // ------------------------------------------------------------ threshold, module size, area

    /// <summary>
    /// Global threshold between the two classes of the histogram (Otsu 1979). In images of almost only black and white many
    /// thresholds split alike; then the middle of them is taken — the first would count smoothed edges as light and lose thin lines.
    /// </summary>
    internal static int Otsu(GrayImage image)
    {
        var histogram = new long[256];
        foreach (var p in image.Pixels) histogram[p]++;
        long total = image.Pixels.Length, sumAll = 0;
        for (var i = 0; i < 256; i++) sumAll += i * histogram[i];
        long weightB = 0, sumB = 0;
        double best = -1;
        int first = 128, last = 128;
        for (var t = 0; t < 256; t++)
        {
            weightB += histogram[t];
            if (weightB == 0) continue;
            var weightF = total - weightB;
            if (weightF == 0) break;
            sumB += t * histogram[t];
            double meanB = (double)sumB / weightB, meanF = (double)(sumAll - sumB) / weightF;
            var between = (double)weightB * weightF * (meanB - meanF) * (meanB - meanF);
            if (between > best * (1 + 1e-9))
            {
                best = between;
                first = last = t;
            }
            else if (between >= best * (1 - 1e-9)) last = t;
        }
        return (first + last) / 2 + 1;
    }

    /// <summary>
    /// The size of one module near the point, from the runs of dark and light along the rows and columns through it: half of a dark
    /// run plus the following light one (ink spread widens bars and narrows spaces by the same amount, so such pairs keep their size
    /// where single runs do not), the short ones — pairs of single modules.
    /// </summary>
    private static (double Small, double Typical) ModuleNear(bool[] dark, int w, int h, int cx, int cy)
    {
        var runs = new List<int>();
        void Collect(int length, Func<int, bool> at)
        {
            var run = 0;
            var last = at(0);
            var previous = -1;
            for (var i = 1; i < length; i++)
            {
                var v = at(i);
                if (v == last)
                {
                    run++;
                    continue;
                }
                // a light run ends: together with the dark run before it
                if (!last && previous > 0) runs.Add((previous + run + 1) / 2);
                previous = last ? run + 1 : -1;
                run = 0;
                last = v;
            }
        }
        var reach = Math.Max(w, h);
        for (var d = -2; d <= 2; d++)
        {
            int row = Math.Clamp(cy + d * 3, 0, h - 1), col = Math.Clamp(cx + d * 3, 0, w - 1);
            int x0 = Math.Max(0, cx - reach / 2), x1 = Math.Min(w, cx + reach / 2);
            int y0 = Math.Max(0, cy - reach / 2), y1 = Math.Min(h, cy + reach / 2);
            Collect(x1 - x0, i => dark[row * w + x0 + i]);
            Collect(y1 - y0, i => dark[(y0 + i) * w + col]);
        }
        if (runs.Count < 6) return (0, 0);
        runs.Sort();
        // the lower tenth and the lower quarter: pairs of single modules; in 2D codes most pairs span one and a half to two modules,
        // so the tenth fits them, the quarter fits bar codes with their many single-module pairs better
        return (Math.Max(1, runs[runs.Count / 10]), Math.Max(1, runs[runs.Count / 4]));
    }

    /// <summary>The dark pixels grown by a square of radius <paramref name="bridge"/> (separable running maximum).</summary>
    private static bool[] Grow(bool[] dark, int w, int h, int bridge) => Grow(dark, w, h, bridge, bridge);

    /// <summary>The dark pixels grown by a rectangle of radii <paramref name="rx"/>, <paramref name="ry"/> (separable running maximum).</summary>
    private static bool[] Grow(bool[] dark, int w, int h, int rx, int ry)
    {
        var rows = new bool[dark.Length];
        for (var y = 0; y < h; y++)
        {
            var count = 0;
            for (var x = -rx; x < w + rx; x++)
            {
                if (x + rx < w && dark[y * w + x + rx]) count++;
                if (x - rx - 1 >= 0 && dark[y * w + x - rx - 1]) count--;
                if (x >= 0 && x < w) rows[y * w + x] = count > 0;
            }
        }
        var grown = new bool[dark.Length];
        for (var x = 0; x < w; x++)
        {
            var count = 0;
            for (var y = -ry; y < h + ry; y++)
            {
                if (y + ry < h && rows[(y + ry) * w + x]) count++;
                if (y - ry - 1 >= 0 && rows[(y - ry - 1) * w + x]) count--;
                if (y >= 0 && y < h) grown[y * w + x] = count > 0;
            }
        }
        return grown;
    }

    /// <summary>A connected area: for each of its rows the outermost dark pixels, how many pixels are dark, and whether one lies on the edge.</summary>
    /// <param name="Rows">Rows with dark pixels: row, leftmost and rightmost dark pixel.</param>
    /// <param name="Count">Dark pixels of the area.</param>
    /// <param name="TouchesEdge">Whether a dark pixel lies on the edge of the image.</param>
    private sealed record AreaRows(List<(int Y, int Min, int Max)> Rows, int Count, bool TouchesEdge);

    /// <summary>
    /// A connected area around the point with gaps of up to 2 × <paramref name="bridge"/> pixels closed: its dark pixels row by row (all
    /// the smallest rectangle needs). Filled span by span, so neither a list of the pixels nor a stack of them grows with the area — an
    /// almost black page took several hundred megabytes (docs/FEHLERGEDAECHTNIS.md, No. 946).
    /// </summary>
    private static AreaRows? Area(bool[] dark, int w, int h, int cx, int cy, int bridge)
    {
        var grown = Grow(dark, w, h, bridge);
        if (!grown[cy * w + cx]) return null;
        var seen = new bool[dark.Length];
        var left = new int[h];
        var right = new int[h];
        Array.Fill(left, int.MaxValue);
        Array.Fill(right, -1);
        var count = 0;
        var touches = false;
        var stack = new Stack<(int X, int Y)>();
        stack.Push((cx, cy));
        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            var row = y * w;
            if (seen[row + x]) continue;
            // the span of grown pixels through the point, not yet filled
            int l = x, r = x;
            while (l > 0 && grown[row + l - 1] && !seen[row + l - 1]) l--;
            while (r < w - 1 && grown[row + r + 1] && !seen[row + r + 1]) r++;
            for (var i = l; i <= r; i++)
            {
                seen[row + i] = true;
                if (!dark[row + i]) continue;
                count++;
                left[y] = Math.Min(left[y], i);
                right[y] = Math.Max(right[y], i);
                if (i == 0 || i == w - 1 || y == 0 || y == h - 1) touches = true;
            }
            // the rows above and below: one start for each run of grown pixels not yet filled beside the span
            for (var ny = y - 1; ny <= y + 1; ny += 2)
            {
                if (ny < 0 || ny >= h) continue;
                var next = ny * w;
                var open = false;
                for (var i = l; i <= r; i++)
                {
                    var free = grown[next + i] && !seen[next + i];
                    if (free && !open) stack.Push((i, ny));
                    open = free;
                }
            }
        }
        if (count < 16) return null;
        var rows = new List<(int Y, int Min, int Max)>();
        for (var y = 0; y < h; y++)
            if (right[y] >= 0) rows.Add((y, left[y], right[y]));
        return new AreaRows(rows, count, touches);
    }

    // ------------------------------------------------------------ geometry

    /// <summary>The smallest rectangle around the pixels of an area (rotating calipers over the convex hull): corners in order around it.</summary>
    /// <param name="rows">The rows of the area: row, leftmost and rightmost pixel.</param>
    internal static (double X, double Y)[] MinimumRectangle(List<(int Y, int Min, int Max)> rows)
    {
        // pixel squares: their corners, row by row only the outermost ones (enough for the hull)
        var points = new List<(double X, double Y)>();
        foreach (var (y, min, max) in rows)
        {
            points.Add((min, y));
            points.Add((min, y + 1));
            points.Add((max + 1, y));
            points.Add((max + 1, y + 1));
        }
        var hull = Hull(points);
        var best = (Area: double.MaxValue, Corners: Array.Empty<(double, double)>());
        for (var i = 0; i < hull.Count; i++)
        {
            var (ax, ay) = hull[i];
            var (bx, by) = hull[(i + 1) % hull.Count];
            double ex = bx - ax, ey = by - ay, length = Math.Sqrt(ex * ex + ey * ey);
            if (length < 1e-9) continue;
            ex /= length;
            ey /= length;
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            foreach (var (px, py) in hull)
            {
                double u = px * ex + py * ey, v = -px * ey + py * ex;
                minU = Math.Min(minU, u);
                maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v);
                maxV = Math.Max(maxV, v);
            }
            var area = (maxU - minU) * (maxV - minV);
            if (area >= best.Area - 1e-9) continue;
            (double, double) P(double u, double v) => (u * ex - v * ey, u * ey + v * ex);
            best = (area, [P(minU, minV), P(maxU, minV), P(maxU, maxV), P(minU, maxV)]);
        }
        return best.Corners;
    }

    private static List<(double X, double Y)> Hull(List<(double X, double Y)> points)
    {
        var p = points.Distinct().OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
        if (p.Count < 3) return p;
        static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<(double, double)>();
        foreach (var pass in new[] { p, Enumerable.Reverse(p).ToList() })
        {
            var start = hull.Count;
            foreach (var q in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], q) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(q);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull;
    }

    /// <summary>The point at (u, v) ∈ [0, 1]² of the quadrilateral top left, top right, bottom right, bottom left (bilinear).</summary>
    private static (double X, double Y) At((double X, double Y)[] q, double u, double v)
    {
        double tx = q[0].X + (q[1].X - q[0].X) * u, ty = q[0].Y + (q[1].Y - q[0].Y) * u;
        double bx = q[3].X + (q[2].X - q[3].X) * u, by = q[3].Y + (q[2].Y - q[3].Y) * u;
        return (tx + (bx - tx) * v, ty + (by - ty) * v);
    }

    private static bool DarkAt(bool[] dark, int w, int h, double x, double y)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        return ix >= 0 && iy >= 0 && ix < w && iy < h && dark[iy * w + ix];
    }

    /// <summary>The module grid of a quadrilateral: each module by its centre and the four points around it (majority).</summary>
    private static BitMatrix Sample(bool[] dark, int w, int h, (double X, double Y)[] quad, int columns, int rows)
    {
        var m = new BitMatrix(columns, rows);
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
            {
                var votes = 0;
                foreach (var (du, dv) in new[] { (0.5, 0.5), (0.3, 0.3), (0.7, 0.3), (0.3, 0.7), (0.7, 0.7) })
                {
                    var (x, y) = At(quad, (c + du) / columns, (r + dv) / rows);
                    if (DarkAt(dark, w, h, x, y)) votes++;
                }
                m[c, r] = votes >= 3;
            }
        return m;
    }

    /// <summary>The quadrilateral turned so that corner <paramref name="first"/> comes first.</summary>
    private static (double X, double Y)[] Turned((double X, double Y)[] q, int first) => [q[first % 4], q[(first + 1) % 4], q[(first + 2) % 4], q[(first + 3) % 4]];

    private static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ------------------------------------------------------------ reading

    private static FoundBarcode? Read(GrayImage image, bool[] dark, int threshold, AreaRows area, double module)
    {
        int w = image.Width, h = image.Height;
        var rect = MinimumRectangle(area.Rows);
        if (rect.Length != 4) return null;
        // clockwise in image coordinates (y down): top left, top right, bottom right, bottom left
        if ((rect[1].X - rect[0].X) * (rect[2].Y - rect[1].Y) - (rect[1].Y - rect[0].Y) * (rect[2].X - rect[1].X) < 0) rect = [rect[0], rect[3], rect[2], rect[1]];
        var touches = area.TouchesEdge;
        var result = Qr(dark, w, h, rect, module) ?? Matrix(dark, w, h, rect, module) ?? Bars(image, threshold, rect, module);
        if (result is null) return null;
        return result with { TouchesEdge = touches, QuietZone = Quiet(dark, w, h, result.Corners, result.Module) };
    }

    private static FoundBarcode? Qr(bool[] dark, int w, int h, (double X, double Y)[] rect, double module)
    {
        double a = Distance(rect[0], rect[1]), b = Distance(rect[1], rect[2]);
        if (Math.Max(a, b) > Math.Min(a, b) * 1.15) return null;   // square
        // the corner without a finder pattern is the lower right one
        var finders = new bool[4];
        var sizes = new double[4];
        for (var k = 0; k < 4; k++) (finders[k], sizes[k]) = FinderAt(dark, w, h, rect, k, module);
        if (finders.Count(f => f) < 3) return null;
        var missing = finders.Count(f => f) == 4 ? -1 : Array.IndexOf(finders, false);
        for (var turn = 0; turn < 4; turn++)
        {
            // in reading orientation the lower right corner is the third (index 2)
            if (missing >= 0 && (turn + 2) % 4 != missing) continue;
            var quad = Turned(rect, turn);
            var unit = sizes.Where((_, i) => finders[i]).Average() / 7;   // the finder pattern is 7 modules wide
            var estimate = (int)Math.Round(Distance(quad[0], quad[1]) / unit);
            var nearest = (int)Math.Round((estimate - 17) / 4.0) * 4 + 17;
            foreach (var n in new[] { nearest, nearest - 4, nearest + 4 })
            {
                if (QrVersion.ForDimension(n) is null) continue;
                var grid = Sample(dark, w, h, quad, n, n);
                if (QrDecoder.Decode(grid) is not { } content) continue;
                var side = Distance(quad[0], quad[1]) / n;
                return new FoundBarcode { Format = BarcodeFormat.Qr, Text = content.Text, Corners = content.Mirrored ? [quad[0], quad[3], quad[2], quad[1]] : quad, Module = side, Qr = content };
            }
        }
        return null;
    }

    /// <summary>A finder pattern at corner <paramref name="corner"/> of the rectangle: dark, light, dark, light, dark in 1:1:3:1:1 along the diagonal.</summary>
    private static (bool Found, double Size) FinderAt(bool[] dark, int w, int h, (double X, double Y)[] rect, int corner, double module)
    {
        var from = rect[corner];
        var opposite = rect[(corner + 2) % 4];
        var length = Distance(from, opposite);
        double dx = (opposite.X - from.X) / length, dy = (opposite.Y - from.Y) / length;
        var raw = new List<double>();
        var current = true;
        var run = 0.0;
        const double step = 0.25;
        for (var t = 0.5; t < length / 2; t += step)
        {
            var d = DarkAt(dark, w, h, from.X + dx * t, from.Y + dy * t);
            if (d == current)
            {
                run += step;
                continue;
            }
            raw.Add(run);
            current = d;
            run = step;
        }
        raw.Add(run);
        // specks at the edges of modules (noise, smoothing) split runs: runs shorter than half a module go into their neighbours
        var shortest = Math.Max(0.75, module * 0.5 * Math.Sqrt(2));
        var runs = new List<double>();
        for (var i = 0; i < raw.Count; i++)
        {
            if (raw[i] < shortest && runs.Count > 0 && i + 1 < raw.Count)
            {
                runs[^1] += raw[i] + raw[i + 1];
                i++;
                continue;
            }
            runs.Add(raw[i]);
            if (runs.Count == 6) break;
        }
        if (runs.Count < 5) return (false, 0);
        runs = runs.Take(5).ToList();
        var total = runs.Sum();
        var unit = total / 7;
        double[] expected = [1, 1, 3, 1, 1];
        for (var i = 0; i < 5; i++)
            if (Math.Abs(runs[i] / unit - expected[i]) > (i == 2 ? 1.2 : 0.7)) return (false, 0);
        return (true, total / Math.Sqrt(2));   // along the diagonal
    }

    private static FoundBarcode? Matrix(bool[] dark, int w, int h, (double X, double Y)[] rect, double module)
    {
        // the L of the finder pattern: two sides that are dark all along; the other two alternate (timing)
        var solid = new double[4];
        var transitions = new int[4];
        for (var s = 0; s < 4; s++)
        {
            var (p, q) = (rect[s], rect[(s + 1) % 4]);
            var inward = At(rect, 0.5, 0.5);
            var length = Distance(p, q);
            var samples = Math.Max(8, (int)(length * 2));
            int darkCount = 0, changes = 0;
            bool? last = null;
            for (var i = 0; i < samples; i++)
            {
                var t = (i + 0.5) / samples;
                double x = p.X + (q.X - p.X) * t, y = p.Y + (q.Y - p.Y) * t;
                // half a module inside the edge: on the middle of the outer row of modules
                var toCenter = Distance((x, y), inward);
                x += (inward.X - x) / toCenter * module * 0.5;
                y += (inward.Y - y) / toCenter * module * 0.5;
                var d = DarkAt(dark, w, h, x, y);
                if (d) darkCount++;
                if (last is { } l && l != d) changes++;
                last = d;
            }
            solid[s] = (double)darkCount / samples;
            transitions[s] = changes;
        }
        for (var s = 0; s < 4; s++)
        {
            // side s (rect[s] → rect[s+1]) and the following one are solid and meet at corner rect[s+1]: that corner is the lower
            // left in reading orientation, side s the bottom and the following one the left side
            var next = (s + 1) % 4;
            if (solid[s] < 0.8 || solid[next] < 0.8) continue;
            var quad = Turned(rect, (next + 1) % 4);   // quad[3] = rect[next]
            // modules counted on the alternating sides: the top is rect side next + 1, the right side next + 2
            int columnsGuess = transitions[(next + 1) % 4] + 1, rowsGuess = transitions[(next + 2) % 4] + 1;
            foreach (var (columns, rows) in Candidates(columnsGuess, rowsGuess, quad, module))
            {
                var grid = Sample(dark, w, h, quad, columns, rows);
                if (DataMatrixCodec.Decode(grid) is not { } content) continue;
                return new FoundBarcode
                {
                    Format = BarcodeFormat.DataMatrix, Text = content.Text, Corners = quad, Module = Distance(quad[0], quad[1]) / columns, DataMatrix = content,
                };
            }
        }
        return null;
    }

    /// <summary>
    /// Symbol sizes to try, most likely first: near the modules counted on the timing sides and those the module size gives (counting
    /// misses modules on turned, smoothed images); a wrong size fails the error correction.
    /// </summary>
    private static IEnumerable<(int Columns, int Rows)> Candidates(int columns, int rows, (double X, double Y)[] quad, double module)
    {
        double across = Distance(quad[0], quad[1]), down = Distance(quad[1], quad[2]);
        var aspect = across / Math.Max(1e-9, down);
        double byColumns = across / module, byRows = down / module;
        return DataMatrixSymbol.All
            .Select(s => (s.Columns, s.Rows))
            .Where(s => Math.Abs((double)s.Columns / s.Rows - aspect) < 0.25 * Math.Max(1, aspect))
            .OrderBy(s => Math.Min(Math.Abs(s.Columns - columns) + Math.Abs(s.Rows - rows), Math.Abs(s.Columns - byColumns) + Math.Abs(s.Rows - byRows)))
            .Take(8);
    }

    private static FoundBarcode? Bars(GrayImage image, int threshold, (double X, double Y)[] rect, double module)
    {
        // scan lines along both axes of the rectangle, several across it; the reading that most lines agree on
        for (var axis = 0; axis < 2; axis++)
        {
            var quad = Turned(rect, axis);   // scan from quad[0]→quad[1] at heights between quad[0]→quad[3]
            var length = Distance(quad[0], quad[1]);
            if (length < 20 * module) continue;
            var votes = new Dictionary<(LinearSymbology, string), (int Count, LinearContent Content)>();
            for (var k = 1; k <= 9; k++)
            {
                var v = 0.1 * k;
                var a = At(quad, 0, v);
                var b = At(quad, 1, v);
                // beyond the rectangle as far again as half its length: the area may hold only part of a code whose spaces are
                // wider than the bridged gaps
                if (ScanLine(image, threshold, a, b, Math.Max(15 * module, length / 2)) is not { } runs) continue;
                if (LinearCodes.Decode(runs) is not { } content) continue;
                var key = (content.Symbology, content.Text);
                votes[key] = votes.TryGetValue(key, out var known) ? (known.Count + 1, known.Content) : (1, content);
            }
            if (votes.Count == 0) continue;
            // at least three of the nine lines read the same: a single line that happens to give a valid check digit in text or a
            // picture (one in ten for EAN) is no code
            var winner = votes.MaxBy(v => v.Value.Count).Value;
            if (winner.Count < 3) continue;
            var c = winner.Content;
            var format = c.Symbology switch
            {
                LinearSymbology.Ean13 => BarcodeFormat.Ean13,
                LinearSymbology.Ean8 => BarcodeFormat.Ean8,
                LinearSymbology.UpcA => BarcodeFormat.UpcA,
                LinearSymbology.UpcE => BarcodeFormat.UpcE,
                LinearSymbology.Code128 => BarcodeFormat.Code128,
                LinearSymbology.Gs1_128 => BarcodeFormat.Gs1_128,
                LinearSymbology.Code39 => BarcodeFormat.Code39,
                _ => BarcodeFormat.Itf,
            };
            return new FoundBarcode { Format = format, Text = c.Text, Corners = quad, Module = length / Math.Max(1, c.Modules), Linear = c };
        }
        return null;
    }

    /// <summary>
    /// Light and dark along a line (extended by <paramref name="margin"/> on both sides for the quiet zones), with edges placed between
    /// samples where the grey value crosses the threshold; starts and ends with light.
    /// </summary>
    private static double[]? ScanLine(GrayImage image, int threshold, (double X, double Y) a, (double X, double Y) b, double margin)
    {
        var length = Distance(a, b);
        if (length < 1) return null;
        double dx = (b.X - a.X) / length, dy = (b.Y - a.Y) / length;
        const double step = 0.25;
        var start = -margin;
        var end = length + margin;
        var count = (int)((end - start) / step);
        var values = new double[count];
        for (var i = 0; i < count; i++)
        {
            var t = start + i * step;
            values[i] = Gray(image, a.X + dx * t, a.Y + dy * t);
        }
        var runs = new List<double>();
        var isDark = values[0] < threshold;
        if (isDark) runs.Add(0);   // the line begins in the code: no quiet zone seen
        var lastEdge = 0.0;
        for (var i = 1; i < count; i++)
        {
            var d = values[i] < threshold;
            if (d == isDark) continue;
            // the crossing between the two samples
            var f = (threshold - values[i - 1]) / (values[i] - values[i - 1]);
            var edge = (i - 1 + Math.Clamp(f, 0, 1)) * step;
            runs.Add(edge - lastEdge);
            lastEdge = edge;
            isDark = d;
        }
        runs.Add((count - 1) * step - lastEdge);
        if (isDark) runs.Add(0);
        return runs.Count >= 10 ? [.. runs] : null;
    }

    /// <summary>Grey value at a point, bilinear between the four pixels around it; outside the image white.</summary>
    private static double Gray(GrayImage image, double x, double y)
    {
        x -= 0.5;
        y -= 0.5;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        double P(int px, int py) => px < 0 || py < 0 || px >= image.Width || py >= image.Height ? 255 : image.Pixels[py * image.Width + px];
        return (P(x0, y0) * (1 - fx) + P(x0 + 1, y0) * fx) * (1 - fy) + (P(x0, y0 + 1) * (1 - fx) + P(x0 + 1, y0 + 1) * fx) * fy;
    }

    /// <summary>Light space around the code in modules: how far outward along each side nothing is dark (at most 12 modules are looked at).</summary>
    private static double Quiet(bool[] dark, int w, int h, (double X, double Y)[] corners, double module)
    {
        if (module <= 0) return 0;
        var centre = At(corners, 0.5, 0.5);
        var least = double.MaxValue;
        for (var s = 0; s < 4; s++)
        {
            var (p, q) = (corners[s], corners[(s + 1) % 4]);
            var mid = ((p.X + q.X) / 2, (p.Y + q.Y) / 2);
            var toMid = Distance(centre, mid);
            if (toMid < 1e-9) continue;
            double ox = (mid.Item1 - centre.X) / toMid, oy = (mid.Item2 - centre.Y) / toMid;
            var free = 12.0;
            for (var d = 0.6; d <= 12; d += 0.25)
            {
                var hit = false;
                for (var t = 0.05; t <= 0.95 && !hit; t += 0.05)
                {
                    double x = p.X + (q.X - p.X) * t + ox * d * module, y = p.Y + (q.Y - p.Y) * t + oy * d * module;
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    hit = dark[(int)y * w + (int)x];
                }
                if (!hit) continue;
                free = d - 0.6;
                break;
            }
            least = Math.Min(least, free);
        }
        return least;
    }
}
