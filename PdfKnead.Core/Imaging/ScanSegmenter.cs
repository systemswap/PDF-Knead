namespace PdfKnead.Core.Imaging;

/// <summary>
/// Settings of the separation.
/// </summary>
/// <param name="Sensitivity">0 … 1; higher values find paler text.</param>
/// <param name="MaxColors">Maximum number of text colours (1 … 8).</param>
/// <param name="BackgroundScale">Size of the background as a fraction of the scan (0.33 = one third in each direction).</param>
/// <param name="MaskScale">Masks at single or double resolution (2: smooth edges).</param>
/// <param name="Ppi">Resolution of the scan in ppi (for dust and thick areas).</param>
/// <param name="FlattenPaper">Replace pixels close to the paper tone in the background by the smoothed paper tone (smaller background, no JPEG blocks).</param>
public sealed record ScanOptions(double Sensitivity = 0.5, int MaxColors = 4, double BackgroundScale = 1, int MaskScale = 1, double Ppi = 300,
    bool FlattenPaper = false);

/// <summary>One text colour: the colour (R = G = B for grey) and its mask; rows padded to whole bytes, 0 = ink, 1 = free (as in /ImageMask).</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
/// <param name="Bits">The mask, 1 bit per pixel.</param>
public sealed record ScanMask(byte R, byte G, byte B, byte[] Bits);

/// <summary>
/// A scan in layers: masks of the text colours at full size and the background (the paper without text) at
/// <see cref="BackgroundWidth"/> × <see cref="BackgroundHeight"/>. The masks have
/// <see cref="Width"/> · <see cref="MaskScale"/> × <see cref="Height"/> · <see cref="MaskScale"/> pixels.
/// </summary>
/// <param name="Width">Width of the scan in px.</param>
/// <param name="Height">Height of the scan in px.</param>
/// <param name="Masks">One mask per text colour.</param>
/// <param name="Background">Background samples, 1 (grey) or 3 (RGB) bytes per pixel.</param>
/// <param name="BackgroundWidth">Width of the background in px.</param>
/// <param name="BackgroundHeight">Height of the background in px.</param>
/// <param name="Gray">Whether the layers are greyscale.</param>
/// <param name="InkShare">Share of ink in all pixels.</param>
/// <param name="MaskScale">1 or 2: resolution of the masks relative to the scan.</param>
public sealed record ScanLayers(int Width, int Height, IReadOnlyList<ScanMask> Masks, byte[] Background, int BackgroundWidth, int BackgroundHeight,
    bool Gray, double InkShare, int MaskScale = 1);

/// <summary>
/// Separates a text scan into text and paper (MRC; see KONZEPT.md, section 5.12), for letters, forms and certificates with stamps and signatures:
/// <list type="number">
/// <item>Estimate the paper per tile (light values of the tile, smoothed over its neighbours), so that paper tone, yellowing and shadows do not interfere.</item>
/// <item>Ink: clearly darker than the paper there, or clearly coloured (ballpoint pen, stamp). The thresholds depend on the sensitivity.</item>
/// <item>Connected spots: dust (too small) is dropped; large, solid areas (photos, bars) stay in the background; if most of the ink is
/// surfaces rather than strokes, the image is a photo or a graphic, not a text scan.</item>
/// <item>Colours per spot, merged into a few text colours; one mask per colour.</item>
/// <item>Background: the ink (with a one-pixel margin against dark fringes) is replaced by the paper colour.</item>
/// </list>
/// The result is null if the image is not a text scan (hardly any paper, too much or no ink); the image then takes the usual path.
/// </summary>
public static class ScanSegmenter
{
    /// <summary>At least this much paper (pixels at most <see cref="PaperTolerance"/> levels from the paper tone), otherwise it is more likely a photo.</summary>
    private const double MinPaperShare = 0.5;

    /// <summary>At most this share of intermediate tones (neither paper nor ink).</summary>
    private const double MaxMiddleShare = 0.25;

    /// <summary>A pixel this close to the paper tone counts as paper (scanner noise, JPEG).</summary>
    private const int PaperTolerance = 16;

    /// <summary>Flattening the paper: a pixel this close to the paper tone becomes paper (smaller than <see cref="PaperTolerance"/>, so pale fields remain).</summary>
    private const int FlattenTolerance = 12;

    /// <summary>At least this share of the tiles has a paper level at most <see cref="PaperSpread"/> levels from the paper of the page (median).</summary>
    private const double MinEvenPaper = 0.7;

    private const int PaperSpread = 32;

    /// <summary>Paper is light: the paper of the page (median of the tiles) is at least this light, including recycled paper and yellowed pages.</summary>
    private const int MinPaperLevel = 150;

    /// <summary>The paper's colour channels may differ by this much on average for it to count as neutral (processed as grey).</summary>
    private const double TintedPaper = 3;

    /// <summary>At most one provisional spot per this many pixels (an A4 page at 300 dpi: 136 000; a letter has a few thousand).</summary>
    private const int PixelsPerSpot = 64, MinSpotLimit = 10_000;

    /// <summary>Ink: at least / at most this share (otherwise an empty page, or a photo or graphic).</summary>
    private const double MinInkShare = 0.0008, MaxInkShare = 0.30;

    /// <summary>
    /// A spot whose mean stroke (twice its area over its edge pixels; a line of width w gives w, a filled square of side s gives s/2) is wider
    /// than this (in mm) is a surface, not writing: body text has 0.2 … 0.5 mm, bold headings and solid letters about 1 mm; a jacket, a
    /// tree or a rendered object in a photo has more.
    /// </summary>
    private const double ThickStrokeMm = 1.5;

    /// <summary>
    /// At most this share of the kept ink in surfaces (<see cref="ThickStrokeMm"/>), otherwise it is a photo or a graphic with a light, even
    /// area (sky, white studio background): as masks it would be posterised into a few flat colours (phase 9.1, all images of the public
    /// test collections that took the MRC path: text scans at most 7 %, photos and rendered graphics 70 … 99 %).
    /// </summary>
    private const double MaxSurfaceShare = 0.5;

    /// <summary>Separates a scan into text masks and background.</summary>
    /// <param name="pixels">Samples; <paramref name="stride"/> bytes per pixel: 1 (grey), 3 (RGB) or 4 (RGBA, alpha is ignored).</param>
    /// <param name="width">Width in px (at least 64).</param>
    /// <param name="height">Height in px (at least 64).</param>
    /// <param name="stride">Bytes per pixel.</param>
    /// <param name="options">Settings of the separation.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>The layers, or null if the input is unsuitable or the image is not a text scan.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was triggered.</exception>
    public static ScanLayers? Split(byte[] pixels, int width, int height, int stride, ScanOptions options, CancellationToken cancel = default)
    {
        if (stride is not (1 or 3 or 4) || width < 64 || height < 64 || pixels.Length < (long)width * height * stride) return null;
        // Ink and background in bands on all cores (otherwise about 0.3 s per page)
        var parallel = new ParallelOptions { CancellationToken = cancel };
        var count = width * height;
        var sens = Math.Clamp(options.Sensitivity, 0, 1);
        var luma = new byte[count];
        var gray = stride == 1;
        long colored = 0;
        for (var i = 0; i < count; i++)
        {
            if (stride == 1)
            {
                luma[i] = pixels[i];
                continue;
            }
            int r = pixels[i * stride], g = pixels[i * stride + 1], b = pixels[i * stride + 2];
            luma[i] = (byte)((r * 77 + g * 150 + b * 29) >> 8);
            if (Math.Abs(r - g) > 24 || Math.Abs(r - b) > 24) colored++;
        }
        if (!gray && colored < count / 2000) gray = true;   // colour scan of a black-and-white page: process as grey
        cancel.ThrowIfCancellationRequested();

        // 1. Paper per tile: the value above which the lightest 8 % lie, and the mean colour of these light pixels
        var block = Math.Max(16, Math.Min(width, height) / 80);
        int bw = (width + block - 1) / block, bh = (height + block - 1) / block;
        var paper = new int[bw * bh];
        var paperColor = new int[bw * bh * 3];
        var histogram = new int[256];
        for (var by = 0; by < bh; by++, cancel.ThrowIfCancellationRequested())
            for (var bx = 0; bx < bw; bx++)
            {
                Array.Clear(histogram);
                int x0 = bx * block, y0 = by * block, x1 = Math.Min(width, x0 + block), y1 = Math.Min(height, y0 + block);
                for (var y = y0; y < y1; y++)
                    for (var x = x0; x < x1; x++) histogram[luma[y * width + x]]++;
                var n = (x1 - x0) * (y1 - y0);
                int level = 255, seen = 0;
                for (; level > 0; level--)
                {
                    seen += histogram[level];
                    if (seen * 100 >= n * 8) break;
                }
                paper[by * bw + bx] = level;
                long sr = 0, sg = 0, sb = 0, k = 0;
                for (var y = y0; y < y1; y++)
                    for (var x = x0; x < x1; x++)
                    {
                        var i = y * width + x;
                        if (luma[i] < level - 8) continue;
                        if (stride == 1) sr += pixels[i];
                        else
                        {
                            sr += pixels[i * stride]; sg += pixels[i * stride + 1]; sb += pixels[i * stride + 2];
                        }
                        k++;
                    }
                k = Math.Max(1, k);
                var c = (by * bw + bx) * 3;
                (paperColor[c], paperColor[c + 1], paperColor[c + 2]) = stride == 1
                    ? ((int)(sr / k), (int)(sr / k), (int)(sr / k))
                    : ((int)(sr / k), (int)(sg / k), (int)(sb / k));
            }
        // Smooth (3 × 3 median): otherwise a tile full of text (a large bold word) would get a "paper" that is too dark
        paper = Median(paper, bw, bh);
        // Black-and-white page on tinted paper (slightly bluish, yellowed): process in colour, otherwise the paper would turn grey
        if (gray && stride != 1)
        {
            long pr = 0, pg = 0, pb = 0;
            for (var t = 0; t < bw * bh; t++) (pr, pg, pb) = (pr + paperColor[t * 3], pg + paperColor[t * 3 + 1], pb + paperColor[t * 3 + 2]);
            double tiles = bw * bh, mr = pr / tiles, mg = pg / tiles, mb = pb / tiles;
            if (Math.Max(Math.Abs(mr - mg), Math.Max(Math.Abs(mg - mb), Math.Abs(mr - mb))) > TintedPaper) gray = false;
        }
        // Paper is almost equally light across the page (yellowing and shadows change it only slowly); if the "paper" follows the image, it is a photo
        var sorted = (int[])paper.Clone();
        Array.Sort(sorted);
        var pageLevel = sorted[sorted.Length / 2];
        var even = 0;
        foreach (var level in paper)
            if (Math.Abs(level - pageLevel) <= PaperSpread) even++;
        if (pageLevel < MinPaperLevel || even < paper.Length * MinEvenPaper) return null;
        cancel.ThrowIfCancellationRequested();

        // 2. Ink
        var relative = 0.36 - 0.22 * sens;   // this much darker than the paper (fraction)
        var absolute = 44 - 26 * sens;   // and at least this many levels
        var chroma = 120 - 60 * sens;   // or this far from the paper in colour
        var ink = new byte[count];
        long paperPixels = 0, inkPixelsRaw = 0;
        var paperRow = new PaperRows(paper, bw, bh, block, width);
        var tileOf = new int[width];
        for (var x = 0; x < width; x++) tileOf[x] = Math.Min(bw - 1, x / block);
        Parallel.For(0, (height + Band - 1) / Band, parallel, band =>
        {
            var levels = new int[width];
            long paperHere = 0, inkHere = 0;
            for (int y = band * Band, end = Math.Min(height, y + Band); y < end; y++)
            {
                var row = Math.Min(bh - 1, y / block) * bw;
                paperRow.At(y, levels);
                for (var x = 0; x < width; x++)
                {
                    var i = y * width + x;
                    var p = levels[x];
                    var contrast = p - luma[i];
                    if (Math.Abs(contrast) < PaperTolerance && Math.Abs(p - pageLevel) <= PaperSpread) paperHere++;
                    var isInk = contrast >= Math.Max(absolute, relative * p);
                    if (!isInk && !gray && contrast >= 8)
                    {
                        var b = (row + tileOf[x]) * 3;
                        int dr = pixels[i * stride] - paperColor[b], dg = pixels[i * stride + 1] - paperColor[b + 1], db = pixels[i * stride + 2] - paperColor[b + 2];
                        isInk = dr * dr + dg * dg + db * db >= chroma * chroma;
                    }
                    if (isInk)
                    {
                        ink[i] = 1;
                        inkHere++;
                    }
                }
            }
            Interlocked.Add(ref paperPixels, paperHere);
            Interlocked.Add(ref inkPixelsRaw, inkHere);
        });
        // Text scan: much paper close to the paper tone, few intermediate tones (with text almost only the edges). A photo has many light
        // areas that look like paper, but also many intermediate tones (without this check a wave pattern would pass as a text scan)
        var middle = count - paperPixels - inkPixelsRaw;
        if (paperPixels < count * MinPaperShare || middle > count * MaxMiddleShare) return null;

        // 3. Spots (8-connectivity, union-find in two passes)
        var labels = new int[count];
        var maxSpots = Math.Max(MinSpotLimit, count / PixelsPerSpot);
        var parent = new List<int> { 0 };
        int Find(int a)
        {
            while (parent[a] != a) a = parent[a] = parent[parent[a]];
            return a;
        }
        void Union(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
        }
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                if (ink[i] == 0) continue;
                int label = 0;
                void Join(int other)
                {
                    if (other == 0) return;
                    if (label == 0) label = other;
                    else if (other != label) Union(label, other);
                }
                if (x > 0) Join(labels[i - 1]);
                if (y > 0)
                {
                    if (x > 0) Join(labels[i - width - 1]);
                    Join(labels[i - width]);
                    if (x + 1 < width) Join(labels[i - width + 1]);
                }
                if (label == 0)
                {
                    // A text scan has few spots; a dot screen (crafted or halftone) has millions; then it is not a text scan, and the
                    // arrays per spot (about 100 bytes) would take gigabytes
                    if (parent.Count >= maxSpots) return null;
                    label = parent.Count;
                    parent.Add(label);
                }
                labels[i] = label;
            }
            if ((y & 255) == 0) cancel.ThrowIfCancellationRequested();
        }
        var roots = parent.Count;
        var area = new int[roots];
        var minX = new int[roots]; var maxX = new int[roots]; var minY = new int[roots]; var maxY = new int[roots];
        var sum = new long[roots * 3];
        var core = new long[roots * 4];   // only the darker pixels: the colour without the lighter edge pixels
        var edge = new int[roots];   // pixels with a free 4-neighbour (or at the image border): with the area, the mean stroke width
        Array.Fill(minX, int.MaxValue); Array.Fill(minY, int.MaxValue);
        var rowLevels = new int[width];
        for (var y = 0; y < height; y++)
        {
            if ((y & 255) == 0) cancel.ThrowIfCancellationRequested();
            int[]? levels = null;
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                if (labels[i] == 0) continue;
                levels ??= paperRow.At(y, rowLevels);
                var root = labels[i] = Find(labels[i]);
                area[root]++;
                minX[root] = Math.Min(minX[root], x); maxX[root] = Math.Max(maxX[root], x);
                minY[root] = Math.Min(minY[root], y); maxY[root] = Math.Max(maxY[root], y);
                if (x == 0 || y == 0 || x == width - 1 || y == height - 1 || ink[i - 1] == 0 || ink[i + 1] == 0 || ink[i - width] == 0 || ink[i + width] == 0)
                    edge[root]++;
                int r = stride == 1 ? pixels[i] : pixels[i * stride], g = stride == 1 ? pixels[i] : pixels[i * stride + 1], bl = stride == 1 ? pixels[i] : pixels[i * stride + 2];
                sum[root * 3] += r; sum[root * 3 + 1] += g; sum[root * 3 + 2] += bl;
                var p = levels[x];
                if (p - luma[i] >= 2 * Math.Max(absolute, relative * p) || luma[i] < 90)
                {
                    core[root * 4] += r; core[root * 4 + 1] += g; core[root * 4 + 2] += bl; core[root * 4 + 3]++;
                }
            }
        }

        // Drop dust, move large solid areas to the background
        // Dust: smaller than a dot of 0.17 mm (4 pixels at 300 dpi), measured by resolution, not by image width (otherwise decimal points
        // would disappear from A0 plans)
        var ppi = options.Ppi > 0 ? options.Ppi : 300;
        var minArea = Math.Max(2, (int)Math.Round(4 * (ppi / 300) * (ppi / 300)));
        var thick = ppi * 0.12;   // 3 mm
        var keep = new bool[roots];
        long inkPixels = 0, surfacePixels = 0;
        var thickStroke = ThickStrokeMm * ppi / 25.4;
        for (var root = 1; root < roots; root++)
        {
            if (area[root] == 0) continue;
            int w = maxX[root] - minX[root] + 1, h = maxY[root] - minY[root] + 1;
            // Large solid areas (bars, photos) go to the background, but only if they are also thick: thin long lines (signature line,
            // underline, form frame) stay sharp in the mask instead of ending up blurred in the background
            var big = (w > width / 5 || h > height / 5) && area[root] > 0.4 * w * h && Math.Min(w, h) > thick;
            var blob = area[root] > count / 50 && area[root] > 0.3 * w * h;
            if (area[root] < minArea || big || blob) continue;
            keep[root] = true;
            inkPixels += area[root];
            if (2.0 * area[root] / Math.Max(1, edge[root]) > thickStroke) surfacePixels += area[root];
        }
        var inkShare = (double)inkPixels / count;
        if (inkShare < MinInkShare || inkShare > MaxInkShare) return null;
        // Mostly surfaces instead of strokes: a photo or a graphic, not writing (phase 9.1: a photo with a light sky passed every check above)
        if (surfacePixels > inkPixels * MaxSurfaceShare) return null;
        cancel.ThrowIfCancellationRequested();

        // 4. Text colours: k-means over the spot colours (weighted by area), close colours merged
        var spots = new List<(double R, double G, double B, int Area, int Root)>();
        for (var root = 1; root < roots; root++)
        {
            if (!keep[root]) continue;
            double r, g, bl;
            if (core[root * 4 + 3] > 0) (r, g, bl) = (core[root * 4] / (double)core[root * 4 + 3], core[root * 4 + 1] / (double)core[root * 4 + 3], core[root * 4 + 2] / (double)core[root * 4 + 3]);
            else (r, g, bl) = (sum[root * 3] / (double)area[root], sum[root * 3 + 1] / (double)area[root], sum[root * 3 + 2] / (double)area[root]);
            if (gray) r = g = bl = (r + g + bl) / 3;
            spots.Add((r, g, bl, area[root], root));
        }
        var centers = Cluster(spots, Math.Clamp(options.MaxColors, 1, 8));
        var colorOf = new int[roots];
        foreach (var s in spots) colorOf[s.Root] = Nearest(centers, s.R, s.G, s.B);

        // Masks (1 = free, 0 = ink); from here on ink means "kept ink". A pixel of a spot goes into the mask only if it is closer to
        // the colour of its ink than to the paper: otherwise the light edge pixels (the scanner's grey levels) would stay black and the text
        // would become bolder ("bleeding"). The edges fall into the background, which replaces one pixel around the ink with paper anyway.
        var rowBytes = (width + 7) / 8;
        var masks = new byte[centers.Count][];
        for (var k = 0; k < centers.Count; k++) Array.Fill(masks[k] = new byte[rowBytes * height], (byte)0xFF);
        var spotColor = new (double R, double G, double B)[roots];
        foreach (var s in spots) spotColor[s.Root] = (s.R, s.G, s.B);
        var maskPixels = new int[roots];   // pixels of each spot in its mask (at mask resolution)
        var classRow = new int[width];
        for (var y = 0; y < height; y++)
        {
            if ((y & 255) == 0) cancel.ThrowIfCancellationRequested();
            int[]? levels = null;
            var row = Math.Min(bh - 1, y / block) * bw;
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                var root = labels[i];
                var kept = root != 0 && keep[root];
                ink[i] = (byte)(kept ? 1 : 0);
                if (!kept) continue;
                var (ir, ig, ib) = spotColor[root];
                bool inside;
                if (gray)
                {
                    levels ??= paperRow.At(y, classRow);
                    double inkLuma = (ir * 77 + ig * 150 + ib * 29) / 256;
                    inside = luma[i] <= (levels[x] + inkLuma) / 2;
                }
                else
                {
                    var b = (row + tileOf[x]) * 3;
                    int r = pixels[i * stride], g = pixels[i * stride + 1], bl = pixels[i * stride + 2];
                    double toInk = (r - ir) * (r - ir) + (g - ig) * (g - ig) + (bl - ib) * (bl - ib);
                    double toPaper = (r - paperColor[b]) * (r - paperColor[b]) + (g - paperColor[b + 1]) * (g - paperColor[b + 1])
                                     + (bl - paperColor[b + 2]) * (bl - paperColor[b + 2]);
                    inside = toInk <= toPaper;
                }
                if (inside)
                {
                    masks[colorOf[root]][y * rowBytes + (x >> 3)] &= (byte)~(0x80 >> (x & 7));
                    maskPixels[root]++;
                }
            }
        }
        var taken = Taken(pixels, luma, stride, width, height, gray, ink, labels, roots, paperRow, paperColor, bw, bh, block, tileOf, cancel);

        // 5. Background: ink and a one-pixel margin are replaced by the nearest free pixels to the left and right in the row (averaged), so
        // a tinted area stays tinted; only where there is no free pixel on either side, the paper colour of the tile. Averaged directly to
        // the size of the background (area average as when scaling images down), without a full-size intermediate image
        var components = gray ? 1 : 3;
        var scale = Math.Clamp(options.BackgroundScale, 0.01, 1);
        var (bgWidth, bgHeight) = (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        var background = new byte[bgWidth * bgHeight * components];
        var binX = Bins(width, bgWidth);
        // Flatten the paper (optional): what lies close to the paper tone (noise, show-through from the reverse side) becomes the smoothed
        // paper tone, so the background becomes almost empty, smaller and free of JPEG blocks; what stands out clearly (stamps, tinted fields) remains
        PaperRows[]? paperColors = null;
        if (options.FlattenPaper && !gray)
        {
            paperColors = new PaperRows[3];
            for (var c = 0; c < 3; c++)
            {
                var grid = new int[bw * bh];
                for (var t = 0; t < grid.Length; t++) grid[t] = paperColor[t * 3 + c];
                paperColors[c] = new PaperRows(Median(grid, bw, bh), bw, bh, block, width);
            }
        }
        Parallel.For(0, bgHeight, parallel, () => new Scratch(width, bgWidth, components), (ty, _, s) =>
        {
            for (int y = (int)((long)ty * height / bgHeight), end = (int)((long)(ty + 1) * height / bgHeight); y < end; y++)
            {
                if (options.FlattenPaper)
                {
                    paperRow.At(y, s.Level);
                    if (paperColors is not null)
                        for (var c = 0; c < 3; c++) paperColors[c].At(y, s.Paper[c]);
                }
                // One-pixel margin: OR over three rows, then over three columns
                for (var x = 0; x < width; x++)
                {
                    var i = y * width + x;
                    s.Columns[x] = (byte)(ink[i] | (y > 0 ? ink[i - width] : 0) | (y + 1 < height ? ink[i + width] : 0));
                }
                for (var x = 0; x < width; x++)
                {
                    s.Near[x] = (s.Columns[x] | (x > 0 ? s.Columns[x - 1] : 0) | (x + 1 < width ? s.Columns[x + 1] : 0)) != 0;
                    var i = y * width + x;
                    var flat = options.FlattenPaper && Math.Abs(luma[i] - s.Level[x]) < FlattenTolerance;
                    for (var c = 0; c < components; c++)
                        s.Source[x * components + c] = flat ? (gray ? s.Level[x] : s.Paper[c][x])
                            : stride == 1 ? pixels[i] : gray ? luma[i] : pixels[i * stride + c];
                }
                // From the left: the last free value
                var seen = false;
                for (var x = 0; x < width; x++)
                {
                    if (!s.Near[x])
                    {
                        seen = true;
                        for (var c = 0; c < components; c++) s.Left[x * components + c] = s.Source[x * components + c];
                    }
                    else if (seen)
                    {
                        for (var c = 0; c < components; c++) s.Left[x * components + c] = s.Left[(x - 1) * components + c];
                    }
                    s.HasLeft[x] = seen;
                }
                // From the right: average with the left one
                var hasRight = false;
                var row = Math.Min(bh - 1, y / block);
                for (var x = width - 1; x >= 0; x--)
                {
                    var o = x * components;
                    if (!s.Near[x])
                    {
                        hasRight = true;
                        for (var c = 0; c < components; c++) s.Line[o + c] = s.Right[c] = s.Source[o + c];
                        continue;
                    }
                    var b = row * bw + Math.Min(bw - 1, x / block);
                    for (var c = 0; c < components; c++)
                        s.Line[o + c] = s.HasLeft[x] && hasRight ? (s.Left[o + c] + s.Right[c] + 1) / 2
                            : s.HasLeft[x] ? s.Left[o + c]
                            : hasRight ? s.Right[c]
                            : gray ? Interpolate(paper, bw, bh, block, x, y) : paperColor[b * 3 + c];
                }
                // Accumulate into the background row
                for (var x = 0; x < width; x++)
                {
                    var t = binX[x];
                    for (var c = 0; c < components; c++) s.Sums[t * components + c] += s.Line[x * components + c];
                    s.Counts[t]++;
                }
            }
            var target = ty * bgWidth * components;
            for (var t = 0; t < bgWidth; t++)
                for (var c = 0; c < components; c++)
                    background[target + t * components + c] = (byte)((s.Sums[t * components + c] + s.Counts[t] / 2) / Math.Max(1, s.Counts[t]));
            Array.Clear(s.Sums);
            Array.Clear(s.Counts);
            return s;
        }, _ => { });

        // 6. Finer masks (double resolution): the colour between the scan pixels is interpolated bilinearly, then decided as above, "closer to
        // the ink or to the paper", so the edges become smooth as in the original instead of jagged. Only pixels near kept ink are computed.
        var maskScale = options.MaskScale == 2 ? 2 : 1;
        if (maskScale == 2)
        {
            int fw = width * 2, fh = height * 2, fineRow = (fw + 7) / 8;
            var fine = new byte[centers.Count][];
            for (var k = 0; k < centers.Count; k++) Array.Fill(fine[k] = new byte[fineRow * fh], (byte)0xFF);
            var finePixels = new int[roots];
            Parallel.For(0, (fh + Band - 1) / Band, parallel, band =>
            {
                var levels0 = new int[width];
                var levels1 = new int[width];
                for (int fy = band * Band, end = Math.Min(fh, fy + Band); fy < end; fy++)
                {
                    var sy = Math.Clamp((fy + 0.5) / 2 - 0.5, 0, height - 1);
                    int y0 = (int)sy, y1 = Math.Min(height - 1, y0 + 1);
                    var ty = sy - y0;
                    var paperLoaded = false;
                    for (var fx = 0; fx < fw; fx++)
                    {
                        var sx = Math.Clamp((fx + 0.5) / 2 - 0.5, 0, width - 1);
                        int x0 = (int)sx, x1 = Math.Min(width - 1, x0 + 1);
                        // Spot: the nearest kept one among the four neighbours
                        int i00 = y0 * width + x0, i01 = y0 * width + x1, i10 = y1 * width + x0, i11 = y1 * width + x1;
                        if ((ink[i00] | ink[i01] | ink[i10] | ink[i11]) == 0) continue;
                        var tx = sx - x0;
                        var nearest = tx < 0.5 ? (ty < 0.5 ? i00 : i10) : (ty < 0.5 ? i01 : i11);
                        var root = ink[nearest] != 0 ? labels[nearest] : ink[i00] != 0 ? labels[i00] : ink[i01] != 0 ? labels[i01] : ink[i10] != 0 ? labels[i10] : labels[i11];
                        var (ir, ig, ib) = spotColor[root];
                        double w00 = (1 - tx) * (1 - ty), w01 = tx * (1 - ty), w10 = (1 - tx) * ty, w11 = tx * ty;
                        bool inside;
                        if (gray)
                        {
                            if (!paperLoaded)
                            {
                                paperRow.At(y0, levels0);
                                paperRow.At(y1, levels1);
                                paperLoaded = true;
                            }
                            var v = luma[i00] * w00 + luma[i01] * w01 + luma[i10] * w10 + luma[i11] * w11;
                            var p = levels0[x0] * (1 - ty) + levels1[x0] * ty;
                            inside = v <= (p + (ir * 77 + ig * 150 + ib * 29) / 256) / 2;
                        }
                        else
                        {
                            double Mix(int c) => pixels[i00 * stride + c] * w00 + pixels[i01 * stride + c] * w01 + pixels[i10 * stride + c] * w10 + pixels[i11 * stride + c] * w11;
                            double r = Mix(0), g = Mix(1), bl = Mix(2);
                            var b = (Math.Min(bh - 1, y0 / block) * bw + tileOf[x0]) * 3;
                            double toInk = (r - ir) * (r - ir) + (g - ig) * (g - ig) + (bl - ib) * (bl - ib);
                            double toPaper = (r - paperColor[b]) * (r - paperColor[b]) + (g - paperColor[b + 1]) * (g - paperColor[b + 1])
                                             + (bl - paperColor[b + 2]) * (bl - paperColor[b + 2]);
                            inside = toInk <= toPaper;
                        }
                        if (inside)
                        {
                            fine[colorOf[root]][fy * fineRow + (fx >> 3)] &= (byte)~(0x80 >> (fx & 7));
                            Interlocked.Increment(ref finePixels[root]);
                        }
                    }
                }
            });
            masks = fine;
            maskPixels = finePixels;
        }

        // 7. Tone: each text colour carries as much ink as the scan had where its masks are — the spots and the one-pixel margin the
        // background replaces with paper (step 5). The colour of the darker pixels alone is lighter than the ink of small print (a scan's
        // strokes are blurred, their darkest pixels few), and the edges beyond the mask are lost with the margin: a dense form of small print
        // came out a tenth lighter (darkest pixels 22, the mask 43; docs/FEHLERGEDAECHTNIS.md, No. 876). Never darker than the darkest pixels
        // of its spots (on average), so grey text does not become black where its mask is thin.
        centers = ToneTrue(centers, spots, colorOf, maskPixels, maskScale * maskScale, taken);

        var result = new List<ScanMask>(centers.Count);
        for (var k = 0; k < centers.Count; k++)
        {
            var (r, g, bl) = centers[k];
            result.Add(new ScanMask((byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(bl), masks[k]));
        }
        return new ScanLayers(width, height, result, background, bgWidth, bgHeight, gray, inkShare, maskScale);
    }

    /// <summary>Rows per band for the parallel passes.</summary>
    private const int Band = 64;

    /// <summary>Row buffers of one worker when building the background.</summary>
    private sealed class Scratch(int width, int bgWidth, int components)
    {
        public readonly byte[] Columns = new byte[width];
        public readonly bool[] Near = new bool[width], HasLeft = new bool[width];
        public readonly int[] Line = new int[width * components], Left = new int[width * components], Source = new int[width * components], Right = new int[components];
        public readonly long[] Sums = new long[bgWidth * components];
        public readonly int[] Counts = new int[bgWidth];
        public readonly int[] Level = new int[width];
        public readonly int[][] Paper = [new int[width], new int[width], new int[width]];
    }

    /// <summary>
    /// Quick pre-check on a small version (e.g. one eighth, which libjpeg delivers almost for free): is there even, light paper
    /// on most of the page? Only then are full decoding and <see cref="Split"/> worthwhile. At that size the text is blurred, so
    /// only paper tone, evenness and paper share are checked, with some margin (a text scan must never fail this check).
    /// </summary>
    /// <param name="pixels">Samples of the small version.</param>
    /// <param name="width">Width in px.</param>
    /// <param name="height">Height in px.</param>
    /// <param name="stride">Bytes per pixel: 1, 3 or 4.</param>
    /// <returns>False only if the image is certainly not a text scan; true for unsuitable input, so that the full check decides.</returns>
    public static bool MayBeTextScan(byte[] pixels, int width, int height, int stride)
    {
        if (stride is not (1 or 3 or 4) || width < 16 || height < 16 || pixels.Length < (long)width * height * stride) return true;
        var count = width * height;
        var luma = new byte[count];
        for (var i = 0; i < count; i++)
            luma[i] = stride == 1 ? pixels[i] : (byte)((pixels[i * stride] * 77 + pixels[i * stride + 1] * 150 + pixels[i * stride + 2] * 29) >> 8);
        var block = Math.Max(8, Math.Min(width, height) / 40);
        int bw = (width + block - 1) / block, bh = (height + block - 1) / block;
        var paper = new int[bw * bh];
        var histogram = new int[256];
        for (var by = 0; by < bh; by++)
            for (var bx = 0; bx < bw; bx++)
            {
                Array.Clear(histogram);
                int x0 = bx * block, y0 = by * block, x1 = Math.Min(width, x0 + block), y1 = Math.Min(height, y0 + block);
                for (var y = y0; y < y1; y++)
                    for (var x = x0; x < x1; x++) histogram[luma[y * width + x]]++;
                int n = (x1 - x0) * (y1 - y0), level = 255, seen = 0;
                for (; level > 0; level--)
                    if ((seen += histogram[level]) * 100 >= n * 8) break;
                paper[by * bw + bx] = level;
            }
        paper = Median(paper, bw, bh);
        var sorted = (int[])paper.Clone();
        Array.Sort(sorted);
        var pageLevel = sorted[sorted.Length / 2];
        var even = paper.Count(level => Math.Abs(level - pageLevel) <= PaperSpread);
        if (pageLevel < MinPaperLevel || even < paper.Length * (MinEvenPaper - 0.1)) return false;
        long paperPixels = 0;
        for (var i = 0; i < count; i++)
            if (Math.Abs(luma[i] - pageLevel) < PaperTolerance * 2) paperPixels++;
        return paperPixels >= count * (MinPaperShare - 0.15);
    }

    /// <summary>For every pixel (0 … <paramref name="size"/>) its cell at the smaller size, with the same boundaries as the area average in the writer.</summary>
    private static int[] Bins(int size, int target)
    {
        var bins = new int[size];
        for (var t = 0; t < target; t++)
            for (int i = (int)((long)t * size / target), end = (int)((long)(t + 1) * size / target); i < end; i++) bins[i] = t;
        return bins;
    }

    /// <summary>Paper lightness of a whole row, like <see cref="Interpolate"/>, but with the column weights computed only once (computing them per pixel was the most expensive part).</summary>
    /// <summary>
    /// What the layers take out of the scan, per spot: its pixels and the one-pixel margin around the kept ink, which the background replaces
    /// with paper (a margin pixel counts for the first kept neighbour found).
    /// </summary>
    /// <param name="Dark">Per spot and channel: the sum of (paper − value) over those pixels.</param>
    /// <param name="Paper">Per spot and channel: the sum of the paper there.</param>
    /// <param name="Pixels">Per spot: the number of those pixels.</param>
    /// <param name="Darkest">Per spot and channel: the darkest value of its ink.</param>
    private sealed record TakenInk(double[] Dark, double[] Paper, int[] Pixels, byte[] Darkest);

    private static TakenInk Taken(byte[] pixels, byte[] luma, int stride, int width, int height, bool gray, byte[] ink, int[] labels, int roots,
        PaperRows paperRow, int[] paperColor, int bw, int bh, int block, int[] tileOf, CancellationToken cancel)
    {
        var dark = new double[roots * 3];
        var paperSum = new double[roots * 3];
        var count = new int[roots];
        var darkest = new byte[roots * 3];
        Array.Fill(darkest, (byte)255);
        var levels = new int[width];
        var columns = new byte[width];
        for (var y = 0; y < height; y++)
        {
            if ((y & 255) == 0) cancel.ThrowIfCancellationRequested();
            // as the background (step 5): OR over three rows, then over three columns
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                columns[x] = (byte)(ink[i] | (y > 0 ? ink[i - width] : 0) | (y + 1 < height ? ink[i + width] : 0));
            }
            var loaded = false;
            var row = Math.Min(bh - 1, y / block) * bw;
            for (var x = 0; x < width; x++)
            {
                if ((columns[x] | (x > 0 ? columns[x - 1] : 0) | (x + 1 < width ? columns[x + 1] : 0)) == 0) continue;
                var i = y * width + x;
                var root = ink[i] != 0 ? labels[i] : 0;
                for (var dy = -1; dy <= 1 && root == 0; dy++)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= height) continue;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx;
                        if (nx < 0 || nx >= width || ink[ny * width + nx] == 0) continue;
                        root = labels[ny * width + nx];
                        break;
                    }
                }
                if (root == 0) continue;
                if (gray && !loaded)
                {
                    paperRow.At(y, levels);
                    loaded = true;
                }
                count[root]++;
                for (var c = 0; c < 3; c++)
                {
                    int p = gray ? levels[x] : paperColor[(row + tileOf[x]) * 3 + c];
                    int v = gray ? luma[i] : pixels[i * stride + c];
                    dark[root * 3 + c] += p - v;
                    paperSum[root * 3 + c] += p;
                    if (ink[i] != 0 && v < darkest[root * 3 + c]) darkest[root * 3 + c] = (byte)v;
                }
            }
        }
        return new TakenInk(dark, paperSum, count, darkest);
    }

    /// <summary>
    /// The text colours anew from what their masks take out of the scan (see <see cref="Taken"/>): mask pixels × (paper − colour) = the ink
    /// taken, summed over the spots of a colour — not darker than the darkest pixels of those spots on average, not lighter than paper white.
    /// </summary>
    /// <param name="perPixel">Mask pixels per scan pixel (4 for masks at double resolution).</param>
    private static List<(double R, double G, double B)> ToneTrue(List<(double R, double G, double B)> centers,
        List<(double R, double G, double B, int Area, int Root)> spots, int[] colorOf, int[] maskPixels, int perPixel, TakenInk taken)
    {
        var k = centers.Count;
        var sums = new double[k * 3];
        var floors = new double[k * 3];
        var weights = new double[k];
        foreach (var s in spots)
        {
            var root = s.Root;
            var n = maskPixels[root] / (double)perPixel;
            if (n <= 0 || taken.Pixels[root] == 0) continue;
            var c = colorOf[root];
            weights[c] += n;
            for (var ch = 0; ch < 3; ch++)
            {
                sums[c * 3 + ch] += n * taken.Paper[root * 3 + ch] / taken.Pixels[root] - taken.Dark[root * 3 + ch];
                floors[c * 3 + ch] += n * taken.Darkest[root * 3 + ch];
            }
        }
        var result = new List<(double R, double G, double B)>(k);
        for (var c = 0; c < k; c++)
        {
            if (weights[c] <= 0)
            {
                result.Add(centers[c]);
                continue;
            }
            var w = weights[c];
            double Channel(int ch) => Math.Clamp(sums[c * 3 + ch] / w, floors[c * 3 + ch] / w, 255);
            result.Add((Channel(0), Channel(1), Channel(2)));
        }
        return result;
    }

    private sealed class PaperRows
    {
        private readonly int[] _grid, _x0, _x1;
        private readonly double[] _tx;
        private readonly int _bw, _bh, _block;

        public PaperRows(int[] grid, int bw, int bh, int block, int width)
        {
            (_grid, _bw, _bh, _block) = (grid, bw, bh, block);
            (_x0, _x1, _tx) = (new int[width], new int[width], new double[width]);
            for (var x = 0; x < width; x++)
            {
                var fx = Math.Clamp((x + 0.5) / block - 0.5, 0, bw - 1);
                _x0[x] = (int)fx;
                _x1[x] = Math.Min(bw - 1, _x0[x] + 1);
                _tx[x] = fx - _x0[x];
            }
        }

        /// <summary>Fills <paramref name="row"/> (one row wide; each band uses its own array, so this can run concurrently).</summary>
        public int[] At(int y, int[] row)
        {
            var fy = Math.Clamp((y + 0.5) / _block - 0.5, 0, _bh - 1);
            int y0 = (int)fy, y1 = Math.Min(_bh - 1, y0 + 1);
            double ty = fy - y0;
            int top = y0 * _bw, bottom = y1 * _bw;
            for (var x = 0; x < row.Length; x++)
            {
                double tx = _tx[x];
                var a = _grid[top + _x0[x]] * (1 - tx) + _grid[top + _x1[x]] * tx;
                var b = _grid[bottom + _x0[x]] * (1 - tx) + _grid[bottom + _x1[x]] * tx;
                row[x] = (int)Math.Round(a * (1 - ty) + b * ty);
            }
            return row;
        }
    }

    /// <summary>Paper lightness at one pixel: linear between the tile centres.</summary>
    private static int Interpolate(int[] grid, int bw, int bh, int block, int x, int y)
    {
        var fx = Math.Clamp((x + 0.5) / block - 0.5, 0, bw - 1);
        var fy = Math.Clamp((y + 0.5) / block - 0.5, 0, bh - 1);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(bw - 1, x0 + 1), y1 = Math.Min(bh - 1, y0 + 1);
        double tx = fx - x0, ty = fy - y0;
        var top = grid[y0 * bw + x0] * (1 - tx) + grid[y0 * bw + x1] * tx;
        var bottom = grid[y1 * bw + x0] * (1 - tx) + grid[y1 * bw + x1] * tx;
        return (int)Math.Round(top * (1 - ty) + bottom * ty);
    }

    private static int[] Median(int[] grid, int w, int h)
    {
        var result = new int[grid.Length];
        var window = new List<int>(9);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                window.Clear();
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx >= 0 && yy >= 0 && xx < w && yy < h) window.Add(grid[yy * w + xx]);
                    }
                window.Sort();
                // Paper is the light part: with an even count take the upper (lighter) of the two middle values
                result[y * w + x] = window[window.Count / 2];
            }
        return result;
    }

    /// <summary>Weighted k-means, starting at the area-weighted mean colour and then adding the farthest colour each time (weighted by the square root of its area); close centres are merged.</summary>
    private static List<(double R, double G, double B)> Cluster(List<(double R, double G, double B, int Area, int Root)> spots, int k)
    {
        var centers = new List<(double R, double G, double B)>();
        if (spots.Count == 0) return centers;
        double tw = 0, mr = 0, mg = 0, mb = 0;
        foreach (var s in spots) { tw += s.Area; mr += s.R * s.Area; mg += s.G * s.Area; mb += s.B * s.Area; }
        centers.Add((mr / tw, mg / tw, mb / tw));
        while (centers.Count < k)
        {
            double best = 0;
            (double, double, double)? far = null;
            foreach (var s in spots)
            {
                var d = Distance(centers[Nearest(centers, s.R, s.G, s.B)], s.R, s.G, s.B);
                if (d < 60 * 60) continue;   // not significantly different
                var score = d * Math.Sqrt(s.Area);
                if (score > best) { best = score; far = (s.R, s.G, s.B); }
            }
            if (far is not { } f) break;
            centers.Add(f);
        }
        for (var round = 0; round < 8; round++)
        {
            var acc = new double[centers.Count * 4];
            foreach (var s in spots)
            {
                var c = Nearest(centers, s.R, s.G, s.B);
                acc[c * 4] += s.R * s.Area; acc[c * 4 + 1] += s.G * s.Area; acc[c * 4 + 2] += s.B * s.Area; acc[c * 4 + 3] += s.Area;
            }
            for (var c = 0; c < centers.Count; c++)
                if (acc[c * 4 + 3] > 0) centers[c] = (acc[c * 4] / acc[c * 4 + 3], acc[c * 4 + 1] / acc[c * 4 + 3], acc[c * 4 + 2] / acc[c * 4 + 3]);
        }
        // Merge what hardly differs
        for (var a = 0; a < centers.Count; a++)
            for (var b = centers.Count - 1; b > a; b--)
                if (Distance(centers[a], centers[b].R, centers[b].G, centers[b].B) < 48 * 48) centers.RemoveAt(b);
        return centers;
    }

    private static double Distance((double R, double G, double B) c, double r, double g, double b) =>
        (c.R - r) * (c.R - r) + (c.G - g) * (c.G - g) + (c.B - b) * (c.B - b);

    private static int Nearest(List<(double R, double G, double B)> centers, double r, double g, double b)
    {
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var c = 0; c < centers.Count; c++)
        {
            var d = Distance(centers[c], r, g, b);
            if (d < bestDistance) (bestDistance, best) = (d, c);
        }
        return best;
    }
}
