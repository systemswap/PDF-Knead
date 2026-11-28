using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using SkiaSharp;
using Matrix = PdfKnead.Core.Pdf.Graphics.Matrix;

namespace PdfKnead.Rendering;

/// <summary>
/// Draws a <see cref="DisplayList"/> with Skia — a whole page or a tile. Several tiles of the same list may be drawn in parallel on
/// separate surfaces.
/// </summary>
public static class SkiaRenderer
{
    /// <summary>Longest side of a rendered page in pixels.</summary>
    public const int MaxRenderSide = 1 << 16;

    /// <summary>Most pixels of a rendered page (2^28, 1 GB as RGBA).</summary>
    public const long MaxRenderPixels = 1L << 28;

    /// <summary>Background of a page (paper).</summary>
    public static readonly SKColor Paper = SKColors.White;

    /// <summary>Placeholder for images that cannot be decoded (broken or unsupported data).</summary>
    private static readonly SKColor ImagePlaceholder = new(PdfImageDecoder.Placeholder.R, PdfImageDecoder.Placeholder.G, PdfImageDecoder.Placeholder.B);
    private static readonly Core.Pdf.Objects.PdfName InterpolateName = Core.Pdf.Objects.PdfName.Get("Interpolate");

    /// <summary>Renders a whole page as a bitmap (thumbnails, export, command line), with thin-line enhancement.</summary>
    /// <param name="list">Display list of the page.</param>
    /// <param name="doc">The document the list belongs to.</param>
    /// <param name="page">The page.</param>
    /// <param name="pixelsPerPoint">Resolution (1 = 72 dpi).</param>
    /// <param name="images">Image cache.</param>
    /// <param name="cancel">Cancels rendering.</param>
    /// <param name="overprintPreview">Simulate overprinting.</param>
    /// <param name="noZoomScale">1 / zoom for annotations with NoZoom when the bitmap stands for the screen; 1 otherwise.</param>
    /// <returns>The bitmap (RGBA, premultiplied, at least 1 × 1); the caller disposes it.</returns>
    public static SKBitmap RenderPage(DisplayList list, PdfDocument doc, PdfPage page, double pixelsPerPoint, ImageCache images, CancellationToken cancel = default,
        bool overprintPreview = false, double noZoomScale = 1)
    {
        var (w, h) = PageGeometry.DeviceSize(page, pixelsPerPoint);
        // a page larger than MaxRenderSide or MaxRenderPixels (a crafted /UserUnit makes any page so) is drawn at the resolution that fits —
        // before, the size went to the bitmap as it was (docs/FEHLERGEDAECHTNIS.md, No. 1122)
        if (w > MaxRenderSide || h > MaxRenderSide || (long)w * h > MaxRenderPixels)
        {
            pixelsPerPoint *= Math.Min(Math.Min(MaxRenderSide / (double)w, MaxRenderSide / (double)h), Math.Sqrt(MaxRenderPixels / ((double)w * h)));
            (w, h) = PageGeometry.DeviceSize(page, pixelsPerPoint);
        }
        var bitmap = new SKBitmap(new SKImageInfo(Math.Max(1, w), Math.Max(1, h), SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(Paper);
        Render(canvas, list, doc, PageGeometry.PageToDevice(page, pixelsPerPoint), SKRect.Create(0, 0, w, h), images, cancel, enhanceThinLines: true, overprintPreview,
            noZoomScale: noZoomScale);
        return bitmap;
    }

    /// <summary>
    /// Draws the area <paramref name="tile"/> (device pixels). The canvas is assumed to have its top left corner at the corner of the
    /// tile, i.e. at (0, 0) — for tiles.
    /// </summary>
    /// <param name="canvas">Target canvas, as large as the tile.</param>
    /// <param name="list">Display list of the page.</param>
    /// <param name="doc">The document the list belongs to.</param>
    /// <param name="pageToDevice">Matrix from page space to device pixels of the whole page.</param>
    /// <param name="tile">The tile area in device pixels.</param>
    /// <param name="images">Image cache.</param>
    /// <param name="cancel">Cancels rendering.</param>
    /// <param name="enhanceThinLines">Strokes thinner than one pixel as full hairlines (like the "enhance thin lines" option of common readers).</param>
    /// <param name="overprintPreview">Show overprinting: where it applies, the colours mix as if printed on top of each other.</param>
    /// <param name="noZoomScale">For the screen: 1 / zoom for annotations with NoZoom (<see cref="NoZoomRange"/>); 1 elsewhere (print, export,
    /// thumbnails — there /Rect applies).</param>
    public static void RenderTile(SKCanvas canvas, DisplayList list, PdfDocument doc, Matrix pageToDevice, SKRectI tile, ImageCache images,
        CancellationToken cancel = default, bool enhanceThinLines = true, bool overprintPreview = false, double noZoomScale = 1)
    {
        var shifted = pageToDevice * Matrix.Translate(-tile.Left, -tile.Top);
        Render(canvas, list, doc, shifted, SKRect.Create(0, 0, tile.Width, tile.Height), images, cancel, enhanceThinLines, overprintPreview,
            noZoomScale: noZoomScale);
    }

    /// <summary>
    /// Draws only the entries for which <paramref name="include"/> is true (index in the display list) — compact "save as image"
    /// draws text and lines separately from the background. Otherwise like
    /// <see cref="RenderTile(SKCanvas, DisplayList, PdfDocument, Matrix, SKRectI, ImageCache, CancellationToken, bool, bool)"/>.
    /// </summary>
    /// <param name="canvas">Target canvas, as large as the tile.</param>
    /// <param name="list">Display list of the page.</param>
    /// <param name="doc">The document the list belongs to.</param>
    /// <param name="pageToDevice">Matrix from page space to device pixels of the whole page.</param>
    /// <param name="tile">The tile area in device pixels.</param>
    /// <param name="images">Image cache.</param>
    /// <param name="include">Filter by entry index.</param>
    /// <param name="cancel">Cancels rendering.</param>
    public static void RenderTile(SKCanvas canvas, DisplayList list, PdfDocument doc, Matrix pageToDevice, SKRectI tile, ImageCache images,
        Func<int, bool> include, CancellationToken cancel = default)
    {
        var shifted = pageToDevice * Matrix.Translate(-tile.Left, -tile.Top);
        Render(canvas, list, doc, shifted, SKRect.Create(0, 0, tile.Width, tile.Height), images, cancel, enhanceThinLines: true, include: include);
    }

    /// <summary>
    /// Draws onto a foreign canvas with its own matrix (UI): <paramref name="pageToCanvas"/> maps into its coordinates; drawing happens
    /// only within the canvas clip. The content blends with what is already drawn (no isolated page group).
    /// </summary>
    /// <param name="canvas">Target canvas.</param>
    /// <param name="list">Display list.</param>
    /// <param name="doc">The document the list belongs to.</param>
    /// <param name="pageToCanvas">Matrix from page space to canvas coordinates.</param>
    /// <param name="images">Image cache.</param>
    /// <param name="overprintPreview">Simulate overprinting.</param>
    public static void RenderOnCanvas(SKCanvas canvas, DisplayList list, PdfDocument doc, Matrix pageToCanvas, ImageCache images,
        bool overprintPreview = false)
    {
        var t = canvas.TotalMatrix;
        var toDevice = pageToCanvas * new Matrix(t.ScaleX, t.SkewY, t.SkewX, t.ScaleY, t.TransX, t.TransY);
        var clip = canvas.DeviceClipBounds;
        canvas.Save();
        canvas.ResetMatrix();
        Render(canvas, list, doc, toDevice, new SKRect(clip.Left, clip.Top, clip.Right, clip.Bottom), images, default, enhanceThinLines: true,
            overprintPreview, pageGroup: false);   // elements blend with the already drawn page
        canvas.Restore();
    }

    /// <summary>
    /// The hits of an area with the entries of NoZoom ranges that are drawn in it: such an entry is drawn shrunk about the anchor of its range,
    /// so where it is drawn — not where it stands — decides (docs/FEHLERGEDAECHTNIS.md, No. 589; before, parts of a NoZoom annotation that
    /// stood in another tile were missing). Takes over <paramref name="found"/> (disposes it when it makes a new list).
    /// </summary>
    private static HitList WithNoZoom(DisplayList list, HitList found, RectD visible, double scale)
    {
        var items = list.Items.Span;
        var sorted = new int[found.Count];
        for (var n = 0; n < found.Count; n++) sorted[n] = found[n];
        List<int>? extra = null;
        foreach (var range in list.NoZoom)
        {
            // the area in the range's own (unshrunk) coordinates
            var area = range.Scaled(scale).Invert().TransformBounds(visible);
            for (var i = Math.Max(range.Start, list.FirstVisible); i < Math.Min(range.End, items.Length); i++)
            {
                ref readonly var it = ref items[i];
                if (!(it.Left <= area.Right && it.Right >= area.Left && it.Top <= area.Bottom && it.Bottom >= area.Top) || list.IsHidden(i)) continue;
                if (Array.BinarySearch(sorted, i) < 0) (extra ??= []).Add(i);
            }
        }
        if (extra is null) return found;
        found.Dispose();
        var merged = System.Buffers.ArrayPool<int>.Shared.Rent(sorted.Length + extra.Count);
        sorted.CopyTo(merged, 0);
        extra.CopyTo(merged, sorted.Length);
        Array.Sort(merged, 0, sorted.Length + extra.Count);
        return new HitList(merged, sorted.Length + extra.Count);
    }

    private static void Render(SKCanvas canvas, DisplayList list, PdfDocument doc, Matrix pageToDevice, SKRect deviceArea, ImageCache images,
        CancellationToken cancel, bool enhanceThinLines, bool overprintPreview = false, Func<int, bool>? include = null, bool pageGroup = true,
        double noZoomScale = 1)
    {
        // Images evicted during this drawing are disposed by the cache only afterwards
        using var drawing = images.BeginDrawing();
        // Visible area in page space (plus a margin for hairlines and anti-aliasing)
        var inverse = pageToDevice.Invert();
        var visible = inverse.TransformBounds(RectD.FromLtrb(deviceArea.Left - 2, deviceArea.Top - 2, deviceArea.Right + 2, deviceArea.Bottom + 2));
        using var hits = noZoomScale is > 0 and < 1 && list.NoZoom.Length > 0
            ? WithNoZoom(list, list.QueryHits(visible), visible, noZoomScale)
            : list.QueryHits(visible);
        if (hits.Count == 0) return;

        var items = list.Items.Span;
        var baseLevel = canvas.Save();
        canvas.ClipRect(deviceArea);
        // The page is an isolated group on a transparent backdrop, the paper comes only afterwards (11.4.7): with blend modes on a
        // layer of its own, otherwise they would blend with the white of the paper (/Color on an empty backdrop became white,
        // issue18032). RestoreToCount(baseLevel) composites it onto the paper at the end
        if (pageGroup && list.UsesBlendModes) canvas.SaveLayer(deviceArea, null);
        var clips = new ClipStack();
        var currentClip = 0;

        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
        using var imagePaint = new SKPaint { IsAntialias = true };
        var dashCache = new Dictionary<int, SKPathEffect>();
        var path = new SKPath();
        var masks = GlyphMasks.For(list);

        // Batches: consecutive opaque strokes with the same style, matrix and clip become a single path and a single draw call. For
        // opaque colours this looks the same (overlaps do not colour twice) — and CAD drawings consist almost entirely of such runs.
        // Transparency and blend modes are never batched (PDF composites every stroke individually).
        var batch = new SKPath();
        var pixelScale = pageToDevice.MeanScale;   // page space → pixels
        int batchPaint = -1, batchMatrix = -1, batchVerbs = 0;

        // Transparency groups and masks: open layers, each with its own clip chain (clips are set with Save/Restore and must not be
        // undone across the boundary of a layer)
        var groups = list.Groups;
        var nextGroup = 0;
        var layers = groups.Length > 0 ? new Stack<(int Group, ClipStack Clips, SKRect Area)>() : null;
        var knockout = false;
        var skipUntil = 0;   // group with opacity 0: skip its entries up to here
        var boundary = groups.Length > 0 ? groups[0].Start : int.MaxValue;   // from this entry on something changes

        // NoZoom (12.5.3; docs/FEHLERGEDAECHTNIS.md, No. 576): inside such a range page space is scaled about its anchor before it goes to
        // the device; the range in which the current matrix holds ends at zoomEnd, the next one starts at zoomNext
        var noZoom = noZoomScale is > 0 and < 1 ? list.NoZoom : [];
        var basePageToDevice = pageToDevice;
        var zoomIndex = 0;
        int zoomEnd = int.MaxValue, zoomNext = noZoom.Length > 0 ? noZoom[0].Start : int.MaxValue;

        void SwitchZoom(int index)
        {
            Flush();
            // layers ending here close with the matrix they were opened with; those beginning here open with the new one (Transition)
            while (layers is { Count: > 0 } && groups[layers.Peek().Group].End <= index) CloseLayer();
            while (zoomIndex < noZoom.Length && noZoom[zoomIndex].End <= index) zoomIndex++;
            var inside = zoomIndex < noZoom.Length && noZoom[zoomIndex].Start <= index;
            var target = inside ? noZoom[zoomIndex].Scaled(noZoomScale) * basePageToDevice : basePageToDevice;
            if (target != pageToDevice)
            {
                // clips set with the old matrix: undone, the next entry sets its own again
                clips.Switch(canvas, list, 0, pageToDevice, deviceArea, path);
                currentClip = 0;
                pageToDevice = target;
            }
            zoomEnd = inside ? noZoom[zoomIndex].End : int.MaxValue;
            zoomNext = inside || zoomIndex >= noZoom.Length ? int.MaxValue : noZoom[zoomIndex].Start;
        }

        void Flush()
        {
            if (batchPaint < 0) return;
            var m = list.Matrices[batchMatrix] * pageToDevice;
            canvas.SetMatrix(ToSk(m));
            SetStroke(stroke, list, list.Paints[batchPaint], m, dashCache, enhanceThinLines);
            DrawStroke(canvas, batch, stroke, list.Paints[batchPaint]);
            batch.Rewind();   // keep the memory (Reset would release it)
            batchPaint = batchMatrix = -1;
            batchVerbs = 0;
        }

        void Transition(int index)
        {
            Flush();
            while (layers!.Count > 0 && groups[layers.Peek().Group].End <= index) CloseLayer();
            while (nextGroup < groups.Length && groups[nextGroup].Start <= index)
            {
                var g = nextGroup++;
                // a text group kept only for overprinting is no group without its simulation (docs/FEHLERGEDAECHTNIS.md, No. 1257)
                if (!groups[g].NeedsLayer || groups[g].OverprintOnly && !overprintPreview || groups[g].End <= index || groups[g].Start < skipUntil) continue;
                if (groups[g].Alpha == 0 || include is not null && !AnyIncluded(groups[g]))
                {
                    // Invisible, or (mask pass of compact "save as image") nothing of it selected: neither layer nor content —
                    // otherwise each of up to 17 passes would open all layers including masks
                    skipUntil = Math.Max(skipUntil, groups[g].End);
                    continue;
                }
                OpenLayer(g);
            }
            boundary = Math.Min(nextGroup < groups.Length ? groups[nextGroup].Start : int.MaxValue,
                layers.Count > 0 ? groups[layers.Peek().Group].End : int.MaxValue);
            knockout = layers.Count > 0 && groups[layers.Peek().Group].Knockout;
        }

        bool AnyIncluded(in GroupNode node)
        {
            for (var i = Math.Max(0, node.Start); i < Math.Min(list.Count, node.End); i++)
                if (include!(i)) return true;
            return false;
        }

        void OpenLayer(int g)
        {
            clips.Switch(canvas, list, 0, pageToDevice, deviceArea, path);
            var node = groups[g];
            // Layer only as large as the entries of the group (a layer over the whole tile per group would cost a multiple of the
            // drawing time on pages with many small groups)
            var r = pageToDevice.TransformBounds(node.Bounds);
            var area = SKRect.Intersect(deviceArea, new SKRect((float)r.Left - 2, (float)r.Top - 2, (float)r.Right + 2, (float)r.Bottom + 2));
            layers!.Push((g, clips, area));
            clips = new ClipStack();
            currentClip = 0;
            // Element of a knockout group (subgroup, mask run): composited as usual — Src would replace the whole rectangle of the
            // layer. Exact would be replacing by the shape of the group; Skia does not know it, its coverage is a good approximation.
            using var layerPaint = new SKPaint { Color = new SKColor(0, 0, 0, node.Alpha), BlendMode = Blend(node.BlendMode) };
            canvas.ResetMatrix();
            // A non-isolated group with blending inside starts with the backdrop (11.4.4): its elements mix with what lies beneath. Put back
            // with its opacity (and mask) the backdrop's share comes out again — B + αg·m·(L − B) (docs/FEHLERGEDAECHTNIS.md, No. 567). Only then, so that nothing else changes (tempo, V-13). The record's Bounds
            // reach Skia wrongly in SkiaSharp 3.119 (nothing would be drawn), so the area is set as clip before; CloseLayer restores it.
            // Without a mask the layer goes back with exactly that, αg·L + (1 − αg)·B (an arithmetic blender): drawn over B as usual it would
            // count the backdrop twice where B is not opaque — a half-transparent object under the group became more opaque within its
            // bounds (No. 591). With a mask (αg·m varies per pixel) it stays the usual compositing — exact over an opaque backdrop.
            if (StartsWithBackdrop(node))
            {
                canvas.Save();
                canvas.ClipRect(area);
                if (node.Mask < 0)
                {
                    var k = node.Alpha / 255f;
                    using var blender = SKBlender.CreateArithmetic(0, k, 1 - k, 0, true);
                    using var exact = new SKPaint { Blender = blender };
                    canvas.SaveLayer(new SKCanvasSaveLayerRec { Paint = exact, Flags = SKCanvasSaveLayerRecFlags.InitializeWithPrevious });
                }
                else canvas.SaveLayer(new SKCanvasSaveLayerRec { Paint = layerPaint, Flags = SKCanvasSaveLayerRecFlags.InitializeWithPrevious });
            }
            else
                canvas.SaveLayer(area, layerPaint);
        }

        // Masks used by several runs: rasterised once per drawing (rasterising per run made 2 000 runs under a mask with 20 000 entries
        // take over 30 s per page image)
        Dictionary<int, SKImage>? maskImages = null;
        int[]? maskUses = null;

        void ApplyMaskOf(int index, SKRect area)
        {
            var mask = list.Masks[index];
            maskUses ??= CountMaskUses(list);
            var cached = maskImages?.ContainsKey(index) == true;
            if (!cached && (maskUses[index] < 2 || (double)deviceArea.Width * deviceArea.Height > MaxMaskPixels || (maskImages?.Count ?? 0) >= MaxMaskImages))
            {
                ApplyMask(canvas, mask, pageToDevice, area, doc, images, cancel);
                return;
            }
            maskImages ??= [];
            if (!maskImages.TryGetValue(index, out var image)) maskImages[index] = image = RasterizeMask(mask, pageToDevice, deviceArea, doc, images, cancel);
            canvas.ResetMatrix();
            using var filter = MaskFilter(mask);
            using var paint = new SKPaint { BlendMode = SKBlendMode.DstIn, ColorFilter = filter };
            canvas.DrawImage(image, deviceArea.Left, deviceArea.Top, paint);
            ClearOutsideMask(canvas, mask, pageToDevice);
        }

        static bool StartsWithBackdrop(in GroupNode node) => node.InnerBlend;

        void CloseLayer()
        {
            var (g, outer, area) = layers!.Pop();
            clips.Switch(canvas, list, 0, pageToDevice, deviceArea, path);
            if (groups[g].Mask >= 0) ApplyMaskOf(groups[g].Mask, area);
            canvas.Restore();
            if (StartsWithBackdrop(groups[g])) canvas.Restore();   // the clip in place of the layer bounds
            clips = outer;
            currentClip = 0;
        }

        try
        {
            for (var n = 0; n < hits.Count; n++)
            {
                if ((n & 1023) == 0) cancel.ThrowIfCancellationRequested();
                if (hits[n] < skipUntil) continue;
                if (hits[n] >= zoomEnd || hits[n] >= zoomNext) SwitchZoom(hits[n]);
                if (hits[n] >= boundary)
                {
                    Transition(hits[n]);
                    if (hits[n] < skipUntil) continue;
                }
                if (include is not null && !include(hits[n])) continue;
                ref readonly var item = ref items[hits[n]];
                var paint = list.Paints[item.Paint];

                var batchable = item.Kind == DisplayItemKind.Stroke && paint.Stroke >> 24 == 255 && paint.BlendMode == 0 && paint.StrokeShading < 0 && !knockout
                    && !(overprintPreview && (paint.Overprint & 2) != 0);
                var fillOverprint = overprintPreview && (paint.Overprint & 1) != 0;
                // Level of detail: stroke smaller than one pixel — only one segment from the first to the last point.
                // Stays visible as a pixel (hatchings do not get lighter) but costs almost nothing.
                var tiny = batchable && Math.Max(item.Right - item.Left, item.Bottom - item.Top) * pixelScale < 1;

                if (batchable && item.Paint == batchPaint && item.Matrix == batchMatrix && item.Clip == currentClip && batchVerbs < 200_000)
                {
                    batchVerbs += tiny ? AppendCollapsed(list, item.Path, batch) : AppendPath(list, item.Path, batch);
                    continue;
                }
                Flush();

                if (item.Clip != currentClip)
                {
                    clips.Switch(canvas, list, item.Clip, pageToDevice, deviceArea, path);
                    currentClip = item.Clip;
                }

                if (batchable)
                {
                    batchVerbs = tiny ? AppendCollapsed(list, item.Path, batch) : AppendPath(list, item.Path, batch);
                    batchPaint = item.Paint;
                    batchMatrix = item.Matrix;
                    continue;
                }

                var matrix = list.Matrices[item.Matrix] * pageToDevice;
                // only filled glyphs as masks: a mask has no outline (render mode 2 or 6 is drawn as path, fill and stroke)
                if (item.Glyph && item.Kind == DisplayItemKind.Fill && paint.FillShading < 0 && !knockout)   // in a knockout group as path: a mask would replace its whole rectangle
                {
                    // Glyph as a pre-rendered mask (tinted with the fill colour), otherwise as a path below
                    fill.Color = new SKColor(paint.Fill);
                    fill.BlendMode = ItemBlend(paint.BlendMode, knockout, fillOverprint);
                    OverprintPlates(fill, fillOverprint, paint.BlendMode, paint.OverprintReplace);
                    canvas.ResetMatrix();
                    if (masks.TryDraw(canvas, list, item.Path, matrix, fill, path)) continue;
                }
                canvas.SetMatrix(ToSk(matrix));

                if (item.Kind == DisplayItemKind.Image)
                {
                    var source = list.Images[item.Image];
                    if (source.IsStencil && paint.FillShading >= 0)
                    {
                        // Stencil with shading pattern: stencil as mask into a layer, the shading only into it (SrcIn).
                        // In a knockout group the shape replaces: first clear underneath by coverage, then add the layer (Plus)
                        var replaces = ItemBlend(paint.BlendMode, knockout, fillOverprint) == SKBlendMode.Src;
                        if (replaces)
                        {
                            canvas.Save();
                            DrawImage(canvas, source, doc, images, imagePaint, paint, maskBlend: SKBlendMode.DstOut);
                            canvas.Restore();
                        }
                        using var layer = new SKPaint { BlendMode = replaces ? SKBlendMode.Plus : ItemBlend(paint.BlendMode, knockout, fillOverprint) };
                        canvas.SaveLayer(SKRect.Create(0, 0, 1, 1), layer);
                        DrawImage(canvas, source, doc, images, imagePaint, paint, maskBlend: SKBlendMode.SrcOver);
                        canvas.SetMatrix(ToSk(matrix));
                        path.Rewind();
                        path.AddRect(SKRect.Create(0, 0, 1, 1));
                        fill.BlendMode = SKBlendMode.SrcIn;
                        FillShaded(canvas, list.Shadings[paint.FillShading], path, matrix, pageToDevice, fill, doc, images, overprintPreview, cancel);
                        canvas.Restore();
                        continue;
                    }
                    DrawImage(canvas, source, doc, images, imagePaint, paint, knockout: knockout, overprint: fillOverprint);
                    continue;
                }

                path.Rewind();   // keep the memory (Reset would release it)
                AppendPath(list, item.Path, path);
                path.FillType = item.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding;

                // Fill and stroke in one operator (B) act like a knockout group (11.7.4.4): the stroke blends with the backdrop, not
                // with its own fill — with blend mode or opacity a layer of its own, in which the stroke replaces the fill (otherwise
                // yellow with Difference over the yellow fill turns black, issue17215)
                var pair = item.Kind == DisplayItemKind.FillStroke && !knockout && !overprintPreview
                           && (paint.BlendMode != 0 || paint.Fill >> 24 != 255 || paint.Stroke >> 24 != 255);
                if (pair)
                {
                    // Layer only as large as the entry (in device pixels; the entry's matrix continues to apply inside)
                    var r = pageToDevice.TransformBounds(RectD.FromLtrb(item.Left, item.Top, item.Right, item.Bottom));
                    var bounds = SKRect.Intersect(deviceArea, new SKRect((float)r.Left - 2, (float)r.Top - 2, (float)r.Right + 2, (float)r.Bottom + 2));
                    var current = canvas.TotalMatrix;
                    canvas.Save();
                    canvas.ResetMatrix();
                    using var pairPaint = new SKPaint { BlendMode = Blend(paint.BlendMode) };
                    canvas.SaveLayer(bounds, pairPaint);
                    canvas.SetMatrix(current);
                }

                if (item.Kind is DisplayItemKind.Fill or DisplayItemKind.FillStroke)
                {
                    fill.Color = new SKColor(paint.Fill);
                    fill.BlendMode = pair ? SKBlendMode.SrcOver : ItemBlend(paint.BlendMode, knockout, fillOverprint);
                    if (paint.FillShading < 0) OverprintPlates(fill, fillOverprint && !pair, paint.BlendMode, paint.OverprintReplace);
                    if (paint.FillShading >= 0)
                    {
                        FillShaded(canvas, list.Shadings[paint.FillShading], path, matrix, pageToDevice, fill, doc, images, overprintPreview, cancel);
                        cancel.ThrowIfCancellationRequested();   // a mesh can be large: do not wait for the next 1 024 entries
                    }
                    else
                    {
                        canvas.DrawPath(path, fill);
                        // Area without width or height (grid made of "x y w 0 re f", issue4260): Skia paints nothing, common PDF readers
                        // paint the pixel underneath — as the finest line. Only truly degenerate areas: narrow but real ones (small glyphs,
                        // fine hatching) became visibly bolder with an outline (measured: 13 pages worse)
                        if (!item.Glyph && Math.Min(Math.Abs(item.Right - item.Left), Math.Abs(item.Bottom - item.Top)) < 1e-6)
                        {
                            fill.Style = SKPaintStyle.Stroke;
                            fill.StrokeWidth = 0;
                            canvas.DrawPath(path, fill);
                            fill.Style = SKPaintStyle.Fill;
                        }
                    }
                }
                if (item.Kind is DisplayItemKind.Stroke or DisplayItemKind.FillStroke)
                {
                    // text filled and stroked (render mode 2 or 6, often a bold made by a printer driver with a thin outline): the outline in its
                    // real width — as a full pixel it closed the counters of small letters, and the text could not be read below ~375 %
                    // (docs/FEHLERGEDAECHTNIS.md, No. 816); outlined text without a fill stays enhanced, it is only its outline
                    SetStroke(stroke, list, paint, matrix, dashCache, enhanceThinLines && !(item.Glyph && item.Kind == DisplayItemKind.FillStroke));
                    var strokeOverprint = overprintPreview && (paint.Overprint & 2) != 0;
                    if (pair) stroke.BlendMode = SKBlendMode.Src;
                    else if (knockout || strokeOverprint) stroke.BlendMode = ItemBlend(paint.BlendMode, knockout, strokeOverprint);
                    if (!pair && paint.StrokeShading < 0) OverprintPlates(stroke, strokeOverprint, paint.BlendMode, paint.OverprintReplace >> 4);
                    // Stroke with shading or pattern (rare): as a shader in the entry's space; meshes keep the substitute colour
                    var strokeFill = paint.StrokeShading >= 0 ? list.Shadings[paint.StrokeShading] : null;
                    using var shader = strokeFill is null ? default
                        : Shader(strokeFill, strokeFill.ToPage * list.Matrices[item.Matrix].Invert(), strokeFill.ToPage * pageToDevice, doc, images, overprintPreview, cancel,
                            canvas.DeviceClipBounds);
                    if (strokeFill is not null && shader.Value is not null)
                    {
                        stroke.Shader = shader.Value;
                        // Colours opaque, opacity (CA) of the shading via the paint colour — applying it in both would count it twice (25 instead of 50 %)
                        stroke.Color = strokeFill is TilingFill tiling ? TilingPaintColor(tiling) : new SKColor(255, 255, 255, strokeFill.Alpha);
                    }
                    DrawStroke(canvas, path, stroke, paint);
                    stroke.Shader = null;
                }
                if (pair)
                {
                    canvas.Restore();   // layer
                    canvas.Restore();   // matrix
                }
            }
            Flush();
            while (layers is { Count: > 0 }) CloseLayer();
        }
        finally
        {
            canvas.RestoreToCount(baseLevel);
            path.Dispose();
            batch.Dispose();
            foreach (var effect in dashCache.Values) effect.Dispose();
            if (maskImages is not null)
                foreach (var image in maskImages.Values) image.Dispose();
        }
    }

    // ------------------------------------------------------------ Shadings

    /// <summary>
    /// Fills a path with a shading: clip to the path (in the entry's space), then draw in the shading's space — linear and radial ones
    /// as shaders over the whole area, meshes as triangles with colours per vertex; /BBox limits additionally.
    /// </summary>
    private static void FillShaded(SKCanvas canvas, ShadingFill shading, SKPath path, Matrix itemToDevice, Matrix pageToDevice, SKPaint fill,
        PdfDocument doc, ImageCache images, bool overprint, CancellationToken cancel)
    {
        var shadingToDevice = shading.ToPage * pageToDevice;
        if (!double.IsFinite(shadingToDevice.Determinant) || Math.Abs(shadingToDevice.Determinant) < 1e-12) return;
        if (shading.Background is { } background)
        {
            // /Background of a shading pattern: the whole path, the shading on top
            fill.Color = new SKColor(background).WithAlpha(shading.Alpha);
            canvas.DrawPath(path, fill);
        }
        // Colours opaque, the shading's opacity via the paint colour (substitute colour here: 0); meshes below as a layer
        fill.Color = shading switch
        {
            TilingFill tiling => TilingPaintColor(tiling),
            MeshFill => SKColors.White,
            _ => new SKColor(255, 255, 255, shading.Alpha),
        };
        canvas.Save();
        canvas.ClipPath(path, antialias: true);
        canvas.SetMatrix(ToSk(shadingToDevice));
        if (shading.BBox is { } box) canvas.ClipRect(new SKRect((float)box.Left, (float)box.Top, (float)box.Right, (float)box.Bottom), antialias: true);
        if (shading is ImageFill image)   // type 1 applies only within the /Domain (in the space of the /Domain, before the /Matrix)
        {
            canvas.SetMatrix(ToSk(image.Matrix * shadingToDevice));
            canvas.ClipRect(new SKRect((float)Math.Min(image.X0, image.X1), (float)Math.Min(image.Y0, image.Y1),
                (float)Math.Max(image.X0, image.X1), (float)Math.Max(image.Y0, image.Y1)), antialias: true);
            canvas.SetMatrix(ToSk(shadingToDevice));
        }
        switch (shading)
        {
            case TilingFill large when DrawLargeTiles(canvas, large, shadingToDevice, fill, doc, images, overprint, cancel):
                break;
            case GradientFill or TilingFill or ImageFill:
                using (var shader = Shader(shading, Matrix.Identity, shadingToDevice, doc, images, overprint, cancel, canvas.DeviceClipBounds))
                {
                    if (shader.Value is null) break;
                    fill.Shader = shader.Value;
                    canvas.DrawPaint(fill);
                    fill.Shader = null;
                }
                break;
            case MeshFill mesh:
                var vertices = MeshVertices(mesh);
                if (mesh.Alpha == 255)
                {
                    canvas.DrawVertices(vertices, SKBlendMode.Modulate, fill);
                    break;
                }
                // Semi-transparent: the mesh as a whole — overlapping areas (folded Coons meshes) cover each other
                using (var layer = new SKPaint { Color = new SKColor(0, 0, 0, mesh.Alpha), BlendMode = fill.BlendMode })
                {
                    canvas.SaveLayer(layer);
                    fill.BlendMode = SKBlendMode.SrcOver;
                    canvas.DrawVertices(vertices, SKBlendMode.Modulate, fill);
                    fill.BlendMode = layer.BlendMode;
                    canvas.Restore();
                }
                break;
        }
        canvas.Restore();
        canvas.SetMatrix(ToSk(itemToDevice));
    }

    /// <summary>
    /// Shader for a shading or a tiling pattern; null for meshes (<see cref="FillShaded"/> draws those as triangles).
    /// <paramref name="local"/>: shading space → canvas space; <paramref name="toDevice"/>: → screen (resolution of the cell).
    /// </summary>
    /// <paramref name="visible"/>: the device pixels drawn into (a function-based shading is sampled there by them).
    private static OwnedShader Shader(ShadingFill fill, Matrix local, Matrix toDevice, PdfDocument doc, ImageCache images, bool overprint,
        CancellationToken cancel, SKRect? visible = null) => fill switch
    {
        GradientFill gradient => GradientShader(gradient, local),
        TilingFill tiling => new(TilingShader(tiling, local, toDevice, doc, images, overprint, cancel), true),
        ImageFill image => new(ImageShader(image, local, toDevice, visible, cancel), true),
        _ => default,
    };

    // ------------------------------------------------------------ Tiling patterns

    /// <summary>Paint colour for the shader of a tiling pattern: coloured patterns take the opacity here; for uncoloured ones it is in the colour filter.</summary>
    private static SKColor TilingPaintColor(TilingFill tiling) => new(255, 255, 255, tiling.Color is null ? tiling.Alpha : (byte)255);

    // Recorded cells per display list of the cell and resolution level (power of two of the pixels per unit)
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DisplayList, Dictionary<int, SKPicture>> Cells = new();

    /// <summary>Largest tile in pixels (per edge) — beyond that the cell is recorded at a coarser resolution.</summary>
    private const double MaxCellPixels = 2048;

    /// <summary>
    /// Tiling pattern as a shader: the cell as a picture (SKPicture) at screen resolution (rounded to a power of two so that zooming
    /// does not re-record it constantly), repeated at the spacing XStep × YStep. If the cell extends beyond this spacing, the
    /// neighbours are included in the tile — so they overlap as PDF intends.
    /// </summary>
    private static SKShader? TilingShader(TilingFill tiling, Matrix local, Matrix toDevice, PdfDocument doc, ImageCache images, bool overprint,
        CancellationToken cancel)
    {
        var scale = toDevice.MeanScale;
        if (!double.IsFinite(scale) || scale <= 0) return null;
        var level = (int)Math.Clamp(Math.Ceiling(Math.Log2(scale)), -16, 16);
        while (level > -16 && Math.Max(tiling.XStep, tiling.YStep) * Math.Pow(2, level) > MaxCellPixels) level--;
        var s = Math.Pow(2, level);
        // The cell is recorded with the BBox corner at the origin and shifted back via the matrix: Skia anchors the tile of a picture
        // shader at (0, 0), not at the corner of the tile rectangle — patterns with a BBox away from the origin would otherwise be
        // offset (issue13561: by BBox mod spacing)
        var box = tiling.Box;
        var tile = new SKRect(0, 0, (float)(tiling.XStep * s), (float)(tiling.YStep * s));
        if (!(tile.Width > 0 && tile.Height > 0)) return null;
        var picture = CellPicture(tiling, level, s, tile, doc, images, overprint, cancel);
        var placed = Matrix.Scale(1 / s, 1 / s) * Matrix.Translate(box.Left, box.Top) * WholePixelSteps(tiling, toDevice) * local;
        SKShader shader;
        if (Reach(tile, placed * toDevice) <= MaxTileReach)
        {
            shader = SKShader.CreatePicture(picture, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, SKFilterMode.Linear, ToSk(placed), tile);
        }
        else
        {
            // A tile that spans millions of pixels on the screen (pattern matrix scaling by 10^7 in one direction): the picture shader
            // records its cell again at that scale and took minutes for one page (docs/FEHLERGEDAECHTNIS.md, No. 407). The cell is
            // drawn once at the level's resolution and repeated as an image — stretched that far, no detail of it survives either way.
            using var image = SKImage.FromPicture(picture, new SKSizeI((int)Math.Ceiling(tile.Width), (int)Math.Ceiling(tile.Height)));
            if (image is null) return null;
            shader = image.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
                ToSk(placed));
        }
        if (tiling.Color is not { } color) return shader;
        // Uncoloured: the cell only provides the shape, the colour comes from outside
        using (shader)
        using (var filter = SKColorFilter.CreateBlendMode(new SKColor(color), SKBlendMode.SrcIn))
            return shader.WithColorFilter(filter);
    }

    /// <summary>
    /// /TilingType 1 and 3 (ISO 32000-2, 8.7.3.1 sentences 26–30, 33): a change of pattern space after which the steps XStep and YStep are whole
    /// device pixels — each step vector on the device rounded to whole pixels (at least one), the origin of pattern space kept; the cell is
    /// distorted by less than a pixel per step for it. The identity for type 2 (the exact step, the cell undistorted) and where the matrix
    /// cannot be inverted. Before, every type kept the exact step (docs/FEHLERGEDAECHTNIS.md, No. 1130).
    /// </summary>
    /// <param name="tiling">The pattern.</param>
    /// <param name="toDevice">Pattern space → device.</param>
    private static Matrix WholePixelSteps(TilingFill tiling, Matrix toDevice)
    {
        if (!tiling.WholePixels || !toDevice.IsInvertible) return Matrix.Identity;
        var (ax, ay) = Whole(toDevice.TransformVector(tiling.XStep, 0));
        var (bx, by) = Whole(toDevice.TransformVector(0, tiling.YStep));
        var rounded = new Matrix(ax / tiling.XStep, ay / tiling.XStep, bx / tiling.YStep, by / tiling.YStep, toDevice.E, toDevice.F);
        return rounded.IsInvertible && rounded.IsFinite ? rounded * toDevice.Invert() : Matrix.Identity;

        // a step vector in whole pixels; one shorter than half a pixel becomes one pixel along its longer component
        static (double X, double Y) Whole((double X, double Y) v)
        {
            var (x, y) = (Math.Round(v.X), Math.Round(v.Y));
            if (x == 0 && y == 0)
            {
                if (Math.Abs(v.X) >= Math.Abs(v.Y)) x = v.X < 0 ? -1 : 1;
                else y = v.Y < 0 ? -1 : 1;
            }
            return (x, y);
        }
    }

    /// <summary>
    /// Longest edge of a tile on the screen (pixels) up to which Skia records the cell again at screen resolution: above the largest page
    /// at the highest zoom (14 400 pt at 6 400 %, about 1.2 million pixels), so that only crafted matrices take the image route.
    /// </summary>
    private const double MaxTileReach = 1 << 22;

    /// <summary>The longer of the two edges of <paramref name="tile"/> after <paramref name="toDevice"/> (its translation does not count).</summary>
    private static double Reach(SKRect tile, Matrix toDevice)
    {
        var (ax, ay) = toDevice.TransformVector(tile.Width, 0);
        var (bx, by) = toDevice.TransformVector(0, tile.Height);
        var reach = Math.Max(Math.Sqrt(ax * ax + ay * ay), Math.Sqrt(bx * bx + by * by));
        return double.IsFinite(reach) ? reach : double.MaxValue;
    }

    /// <summary>
    /// Tiling pattern with such a large spacing that the shader would have to coarsen the cell (tile over <see cref="MaxCellPixels"/>),
    /// but with only a few tiles visible: draws these directly at full resolution (tiling-pattern-large-steps: spacing 90 000 — the
    /// cell would shrink to ~90 pixels and be scaled up blurred). Expects the canvas in the pattern's space, clipped to the area;
    /// false: the ordinary shader path.
    /// </summary>
    private static bool DrawLargeTiles(SKCanvas canvas, TilingFill tiling, Matrix toDevice, SKPaint fill, PdfDocument doc, ImageCache images,
        bool overprint, CancellationToken cancel)
    {
        var scale = toDevice.MeanScale;
        if (!double.IsFinite(scale) || scale <= 0) return false;
        var level = (int)Math.Clamp(Math.Ceiling(Math.Log2(scale)), -16, 16);
        var s = Math.Pow(2, level);
        if (Math.Max(tiling.XStep, tiling.YStep) * s <= MaxCellPixels) return false;
        var box = tiling.Box;
        // Only without overlap (cell not larger than the spacing): otherwise neighbouring copies would appear twice
        // (with rounding tolerance: issue6737 has BBox 695.256 with spacing 695.255)
        if (box.Width > tiling.XStep * 1.0001 || box.Bottom - box.Top > tiling.YStep * 1.0001) return false;
        // types 1 and 3: the tiles a whole number of pixels apart (the visible area taken back into the rounded pattern space)
        var steps = WholePixelSteps(tiling, toDevice);
        var local = canvas.LocalClipBounds;
        var visible = steps.Invert().TransformBounds(RectD.FromLtrb(local.Left, local.Top, local.Right, local.Bottom));
        // Tiles whose cell extends into the visible area
        var i0 = Math.Ceiling((visible.Left - box.Right) / tiling.XStep);
        var i1 = Math.Floor((visible.Right - box.Left) / tiling.XStep);
        var j0 = Math.Ceiling((visible.Top - box.Bottom) / tiling.YStep);
        var j1 = Math.Floor((visible.Bottom - box.Top) / tiling.YStep);
        if (!(i1 - i0 < 2 && j1 - j0 < 2)) return false;   // more than 2 × 2: shader
        using var layer = new SKPaint
        {
            Color = fill.Color,
            BlendMode = fill.BlendMode,
            ColorFilter = tiling.Color is { } color ? SKColorFilter.CreateBlendMode(new SKColor(color), SKBlendMode.SrcIn) : null,
        };
        // Record the cell as for the shader (same cache, same handling of masks and groups), only without coarsening
        var tile = new SKRect(0, 0, (float)(tiling.XStep * s), (float)(tiling.YStep * s));
        var picture = CellPicture(tiling, level, s, tile, doc, images, overprint, cancel);
        canvas.SaveLayer(layer);
        for (var j = j0; j <= j1; j++)
        for (var i = i0; i <= i1; i++)
        {
            canvas.Save();
            canvas.SetMatrix(ToSk(Matrix.Scale(1 / s, 1 / s) * Matrix.Translate(box.Left + i * tiling.XStep, box.Top + j * tiling.YStep) * steps * toDevice));
            canvas.ClipRect(tile);
            canvas.DrawPicture(picture);
            canvas.Restore();
        }
        canvas.Restore();
        return true;
    }

    /// <summary>Maximum recorded entries per cell (neighbouring copies included).</summary>
    private const long MaxCellRecords = 1_000_000;

    /// <summary>
    /// Records the cell, once per resolution level and overprint setting. Cancellable (then nothing stays in the cache); neighbouring
    /// copies only as long as there are at most <see cref="MaxCellRecords"/> entries — a crafted cell (BBox 1 000, spacing 60,
    /// 100 000 areas) would otherwise record 289 copies: over 40 s and 11 GB.
    /// </summary>
    private static SKPicture CellPicture(TilingFill tiling, int level, double s, SKRect tile, PdfDocument doc, ImageCache images, bool overprint,
        CancellationToken cancel)
    {
        var pictures = Cells.GetValue(tiling.Cell, _ => []);
        var key = level * 2 + (overprint ? 1 : 0);
        lock (pictures)
        {
            if (pictures.TryGetValue(key, out var cached)) return cached;
            using var recorder = new SKPictureRecorder();
            var canvas = recorder.BeginRecording(tile);
            // Neighbours whose cell extends into this tile (at most 16 per direction; more means a crafted file)
            var box = tiling.Box;
            var fromX = Math.Max(-16, (int)Math.Floor((box.Left - box.Right) / tiling.XStep) + 1);
            var fromY = Math.Max(-16, (int)Math.Floor((box.Top - box.Bottom) / tiling.YStep) + 1);
            if ((long)tiling.Cell.Count * (1 - fromX) * (1 - fromY) > MaxCellRecords) fromX = fromY = 0;
            for (var j = fromY; j <= 0; j++)
            for (var i = fromX; i <= 0; i++)
                Render(canvas, tiling.Cell, doc, new Matrix(s, 0, 0, s, (i * tiling.XStep - box.Left) * s, (j * tiling.YStep - box.Top) * s), tile,
                    images, cancel, enhanceThinLines: false, overprint);
            var picture = recorder.EndRecording();
            if (pictures.Count >= 8) pictures.Clear();   // do not collect zoom levels without limit
            return pictures[key] = picture;
        }
    }

    // Type 1 shadings as images, once per colour array (shared by all places and tiles)
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<uint[], SKImage> FunctionImages = new();

    /// <summary>
    /// Image shader of a function-based shading: texel centre i lies on grid point i of the /Domain, bilinear. Where the coarse grid of
    /// <see cref="ImageFill.Pixels"/> lies more than a device pixel apart, the part of /Domain within <paramref name="visible"/> (device pixels) is
    /// sampled anew a pixel apart (<see cref="ImageFill.Resample"/>, within its limits), so that a jump of the function stays within a pixel
    /// (ISO 32000-2, 8.7.4.5.2; docs/FEHLERGEDAECHTNIS.md, No. 1130).
    /// </summary>
    private static SKShader ImageShader(ImageFill fill, Matrix local, Matrix toDevice, SKRect? visible, CancellationToken cancel)
    {
        if (Resampled(fill, local, toDevice, visible, cancel) is { } fine) return fine;
        const int n = ImageFill.Samples;
        if (!FunctionImages.TryGetValue(fill.Pixels, out var image))
        {
            var built = SKImage.FromPixelCopy(new SKImageInfo(n, n, SKColorType.Bgra8888, SKAlphaType.Unpremul),
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(fill.Pixels.AsSpan()));
            image = FunctionImages.GetValue(fill.Pixels, _ => built);
            if (!ReferenceEquals(image, built)) built.Dispose();
        }
        double sx = (fill.X1 - fill.X0) / (n - 1), sy = (fill.Y1 - fill.Y0) / (n - 1);
        var toDomain = new Matrix(sx, 0, 0, sy, fill.X0 - 0.5 * sx, fill.Y0 - 0.5 * sy);
        return image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
            ToSk(toDomain * fill.Matrix * local));
    }

    /// <summary>The visible part of a function-based shading sampled a device pixel apart; null where the coarse grid is fine enough.</summary>
    private static SKShader? Resampled(ImageFill fill, Matrix local, Matrix toDevice, SKRect? visible, CancellationToken cancel)
    {
        var domainToDevice = fill.Matrix * toDevice;
        if (fill.Resample is not { } resample || visible is not { Width: > 0, Height: > 0 } area || !domainToDevice.IsInvertible || !domainToDevice.IsFinite)
            return null;
        double Length(double x, double y)
        {
            var (dx, dy) = domainToDevice.TransformVector(x, y);
            return Math.Sqrt(dx * dx + dy * dy);
        }
        // the coarse grid a pixel apart or closer: enough
        const int coarse = ImageFill.Samples - 1;
        if (Length((fill.X1 - fill.X0) / coarse, 0) <= 1 && Length(0, (fill.Y1 - fill.Y0) / coarse) <= 1) return null;
        var seen = domainToDevice.Invert().TransformBounds(RectD.FromLtrb(area.Left, area.Top, area.Right, area.Bottom));
        double x0 = Math.Max(seen.Left, Math.Min(fill.X0, fill.X1)), x1 = Math.Min(seen.Right, Math.Max(fill.X0, fill.X1));
        double y0 = Math.Max(seen.Top, Math.Min(fill.Y0, fill.Y1)), y1 = Math.Min(seen.Bottom, Math.Max(fill.Y0, fill.Y1));
        if (!(x1 > x0) || !(y1 > y0) || !double.IsFinite(x1 - x0) || !double.IsFinite(y1 - y0)) return null;
        // one sample per device pixel and one more, within the limits of the fill (beyond them both axes coarser in proportion)
        var nx = (int)Math.Clamp(Math.Ceiling(Length(x1 - x0, 0)) + 1, 2, ImageFill.MaxResampleEdge);
        var ny = (int)Math.Clamp(Math.Ceiling(Length(0, y1 - y0)) + 1, 2, ImageFill.MaxResampleEdge);
        if ((long)nx * ny > ImageFill.MaxResampleSamples)
        {
            var shrink = Math.Sqrt((double)ImageFill.MaxResampleSamples / ((long)nx * ny));
            (nx, ny) = (Math.Max(2, (int)(nx * shrink)), Math.Max(2, (int)(ny * shrink)));
        }
        uint[] pixels;
        try
        {
            pixels = resample(x0, x1, y0, y1, nx, ny, cancel);
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or InvalidCastException or ArithmeticException
                                      or PdfKnead.Core.Pdf.PdfException)
        {
            return null;   // the coarse version instead
        }
        if (pixels.Length != nx * ny) return null;
        using var image = SKImage.FromPixelCopy(new SKImageInfo(nx, ny, SKColorType.Bgra8888, SKAlphaType.Unpremul),
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()));
        if (image is null) return null;
        double sx = (x1 - x0) / (nx - 1), sy = (y1 - y0) / (ny - 1);
        var toDomain = new Matrix(sx, 0, 0, sy, x0 - 0.5 * sx, y0 - 0.5 * sy);
        return image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
            ToSk(toDomain * fill.Matrix * local));
    }

    // One Skia gradient per shading (key: its colour array, which copies at other places share) — bug1721218 draws the same shading
    // 3 511 times, which would otherwise rebuild it every time
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<uint[], SKShader> Gradients = new();

    /// <summary>
    /// The shared shader; with a different matrix an own one on top of it. For the identity matrix Skia returns the same shader and
    /// SkiaSharp the same object — the caller may dispose only its own one (otherwise a crash on the next use).
    /// </summary>
    private static OwnedShader GradientShader(GradientFill g, Matrix local)
    {
        if (!Gradients.TryGetValue(g.Colors, out var shared))
        {
            var built = BuildGradient(g);
            shared = Gradients.GetValue(g.Colors, _ => built);
            if (!ReferenceEquals(shared, built)) built.Dispose();
        }
        var m = ToSk(local);
        if (m.IsIdentity) return new(shared, false);
        var own = shared.WithLocalMatrix(m);
        return new(own, !ReferenceEquals(own, shared));
    }

    /// <summary>Shader together with whether it belongs to the caller (then <see cref="Dispose"/> releases it).</summary>
    private readonly record struct OwnedShader(SKShader? Value, bool Owned) : IDisposable
    {
        public void Dispose()
        {
            if (Owned) Value?.Dispose();
        }
    }

    /// <summary>
    /// Smallest size (axis length; for radial shadings also the larger radius) built at the shading's own scale. Skia takes an axis or
    /// circles below 1/32 768 units as degenerate and paints a single colour everywhere; smaller shadings (a shading space scaled up by
    /// its matrix) are built enlarged around their start point and scaled back.
    /// </summary>
    private const double MinGradientSize = 1.0 / 64;

    private static SKShader BuildGradient(GradientFill g)
    {
        // 8.7.4.5.3, 8.7.4.5.4: a coincident axis, both radii 0 or the same circle twice paint nothing — Skia would fill the whole area
        // with the end colour where /Extend is set (docs/FEHLERGEDAECHTNIS.md, No. 760)
        if (g.PaintsNothing) return SKShader.CreateEmpty();
        var c = g.Coords;
        var length = g.Radial ? Math.Sqrt((c[3] - c[0]) * (c[3] - c[0]) + (c[4] - c[1]) * (c[4] - c[1])) : Math.Sqrt((c[2] - c[0]) * (c[2] - c[0]) + (c[3] - c[1]) * (c[3] - c[1]));
        var size = g.Radial ? Math.Max(length, Math.Max(Math.Max(0, c[2]), Math.Max(0, c[5]))) : length;
        if (!(size < MinGradientSize)) return BuildGradient(g, c);
        double x0 = c[0], y0 = c[1], k = 1 / size;
        var enlarged = g.Radial
            ? new[] { 0, 0, c[2] * k, (c[3] - x0) * k, (c[4] - y0) * k, c[5] * k }
            : [0, 0, (c[2] - x0) * k, (c[3] - y0) * k];
        var built = BuildGradient(g, enlarged);
        var placed = built.WithLocalMatrix(ToSk(Matrix.Scale(size, size) * Matrix.Translate(x0, y0)));
        if (!ReferenceEquals(placed, built)) built.Dispose();
        return placed;
    }

    private static SKShader BuildGradient(GradientFill g, double[] c)
    {
        var colors = new SKColor[g.Colors.Length];
        for (var i = 0; i < colors.Length; i++) colors[i] = new SKColor(g.Colors[i]);
        if (!g.Radial)
            return Gradient(false, new SKPoint((float)c[0], (float)c[1]), 0, new SKPoint((float)c[2], (float)c[3]), 0, colors, g.Positions, g.ExtendStart, g.ExtendEnd);
        SKPoint p0 = new((float)c[0], (float)c[1]), p1 = new((float)c[3], (float)c[4]);
        float r0 = (float)Math.Max(0, c[2]), r1 = (float)Math.Max(0, c[5]);
        var shader = Gradient(true, p0, r0, p1, r1, colors, g.Positions, g.ExtendStart, g.ExtendEnd);
        // Without /Extend at the end, where the largest value would lie beyond 1, the smaller one within the range applies (8.7.4.5.4:
        // the largest value in the range). Skia always takes the largest and otherwise leaves the place empty — for circles that do
        // not lie inside each other, almost the whole end circle would be missing (radial_gradients). Underneath, the same shading
        // reversed: its largest value is our smallest; where the upper one applies, it covers it.
        var d = SKPoint.Distance(p0, p1);
        if (g.ExtendEnd || d + Math.Min(r0, r1) <= Math.Max(r0, r1) + 1e-4f) return shader;
        var reversedColors = new SKColor[colors.Length];
        var reversedPositions = new float[colors.Length];
        for (var i = 0; i < colors.Length; i++)
        {
            reversedColors[i] = colors[^(i + 1)];
            reversedPositions[i] = 1 - g.Positions[^(i + 1)];
        }
        using var under = Gradient(true, p1, r1, p0, r0, reversedColors, reversedPositions, false, g.ExtendStart);
        var composed = SKShader.CreateCompose(under, shader, SKBlendMode.SrcOver);
        shader.Dispose();
        return composed;
    }

    private static SKShader Gradient(bool radial, SKPoint p0, float r0, SKPoint p1, float r1, SKColor[] source, float[] sourcePositions,
        bool extendStart, bool extendEnd)
    {
        // Beyond the ends only with /Extend; without it, it stays empty there. One-sided: Clamp, with a hard stop to transparent at the
        // open end (Skia takes the outermost colour beyond the ends)
        var start = extendEnd && !extendStart ? 1 : 0;
        var end = extendStart && !extendEnd ? 1 : 0;
        var colors = new SKColor[source.Length + start + end];
        var positions = new float[colors.Length];
        for (var i = 0; i < source.Length; i++)
        {
            colors[i + start] = source[i];
            positions[i + start] = sourcePositions[i];
        }
        if (start == 1) (colors[0], positions[0]) = (SKColors.Transparent, sourcePositions[0]);
        if (end == 1) (colors[^1], positions[^1]) = (SKColors.Transparent, sourcePositions[^1]);
        var mode = extendStart || extendEnd ? SKShaderTileMode.Clamp : SKShaderTileMode.Decal;
        return radial
            ? SKShader.CreateTwoPointConicalGradient(p0, r0, p1, r1, colors, positions, mode)
            : SKShader.CreateLinearGradient(p0, p1, colors, positions, mode);
    }

    // One Skia mesh per mesh (immutable, shared by all tiles and threads; copies of the same mesh at other places share the colour
    // array). Otherwise every drawing of every tile would copy the mesh anew (up to 14 MB native).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<uint[], SKVertices> Meshes = new();

    private static SKVertices MeshVertices(MeshFill mesh)
    {
        if (Meshes.TryGetValue(mesh.Colors, out var vertices)) return vertices;
        var points = new SKPoint[mesh.Colors.Length];
        var colors = new SKColor[mesh.Colors.Length];
        for (var i = 0; i < colors.Length; i++)
        {
            points[i] = new SKPoint(mesh.Points[2 * i], mesh.Points[2 * i + 1]);
            colors[i] = new SKColor(mesh.Colors[i]);
        }
        var built = SKVertices.CreateCopy(SKVertexMode.Triangles, points, null, colors);
        var shared = Meshes.GetValue(mesh.Colors, _ => built);
        if (!ReferenceEquals(shared, built)) built.Dispose();
        return shared;
    }

    private static void SetStroke(SKPaint stroke, DisplayList list, Paint paint, Matrix matrix, Dictionary<int, SKPathEffect> dashCache, bool enhanceThinLines)
    {
        stroke.Color = new SKColor(paint.Stroke);
        stroke.BlendMode = Blend(paint.BlendMode);
        // Thinner than one pixel: as a hairline (1 pixel, like common readers with "enhance thin lines")
        var deviceWidth = paint.LineWidth * matrix.MeanScale;
        // Without enhancement: real width (thin lines get paler); width 0 is always the thinnest line according to PDF
        stroke.StrokeWidth = deviceWidth < 1 && enhanceThinLines ? 0 : paint.LineWidth;
        stroke.StrokeCap = paint.Cap switch { 1 => SKStrokeCap.Round, 2 => SKStrokeCap.Square, _ => SKStrokeCap.Butt };
        stroke.StrokeJoin = paint.Join switch { 1 => SKStrokeJoin.Round, 2 => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter };
        stroke.StrokeMiter = paint.MiterLimit;
        stroke.PathEffect = paint.Dash >= 0 ? Dash(list, paint.Dash, dashCache) : null;
    }

    /// <summary>
    /// Draws a stroke. Skia omits miters at corners that almost reverse (1 + cos ≤ 1/4096, i.e. a ratio over ~90), even if the miter
    /// limit allows them (LargeMitreLimit: limit 333) — such tips are added as triangles, united with the outline of the stroke and
    /// filled once (semi-transparent strokes would otherwise cover themselves twice).
    /// </summary>
    private static void DrawStroke(SKCanvas canvas, SKPath path, SKPaint stroke, Paint paint)
    {
        // a subpath that is a single point has no direction: with projecting square caps nothing is painted (ISO 32000-2, 8.5.3.2; butt caps
        // give nothing in Skia as well, round caps a dot) — Skia drew a square (docs/FEHLERGEDAECHTNIS.md, No. 758)
        if (paint.Cap == 2 && WithoutPointSubpaths(path) is { } trimmed)
        {
            using (trimmed) DrawStroke(canvas, trimmed, stroke, paint with { Cap = 0 });
            return;
        }
        if (paint.Join != 0 || paint.MiterLimit <= 90 || paint.Dash >= 0 || stroke.StrokeWidth <= 0
            || MiterTips(path, stroke.StrokeWidth, paint.MiterLimit) is not { } tips)
        {
            canvas.DrawPath(path, stroke);
            return;
        }
        using (tips)
        using (var outline = new SKPath())
        {
            stroke.GetFillPath(path, outline);
            using var union = outline.Op(tips, SKPathOp.Union) ?? outline;
            stroke.Style = SKPaintStyle.Fill;
            canvas.DrawPath(union, stroke);
            stroke.Style = SKPaintStyle.Stroke;
        }
    }

    /// <summary>
    /// The path without its subpaths that are a single point (all their points the same); null if it has none — then nothing changes. The
    /// other subpaths are copied verb for verb, so their geometry stays exactly as it was.
    /// </summary>
    private static SKPath? WithoutPointSubpaths(SKPath path)
    {
        var verbs = new List<(SKPathVerb Verb, SKPoint[] Points, float Weight)>();
        using (var iterator = path.CreateRawIterator())
        {
            var points = new SKPoint[4];
            SKPathVerb verb;
            while ((verb = iterator.Next(points)) != SKPathVerb.Done)
                verbs.Add((verb, (SKPoint[])points.Clone(), verb == SKPathVerb.Conic ? iterator.ConicWeight() : 0));
        }
        var result = new SKPath { FillType = path.FillType };
        var removed = false;
        for (var start = 0; start < verbs.Count;)
        {
            var end = start + 1;
            while (end < verbs.Count && verbs[end].Verb != SKPathVerb.Move) end++;
            var first = verbs[start].Points[0];
            var single = true;
            for (var i = start; i < end && single; i++)
            {
                var count = verbs[i].Verb switch
                {
                    SKPathVerb.Move => 1, SKPathVerb.Line => 2, SKPathVerb.Quad or SKPathVerb.Conic => 3, SKPathVerb.Cubic => 4, _ => 0,
                };
                for (var k = 0; k < count; k++) single &= verbs[i].Points[k] == first;
            }
            if (single)
            {
                removed = true;
            }
            else
            {
                for (var i = start; i < end; i++)
                {
                    var (verb, p, weight) = verbs[i];
                    switch (verb)
                    {
                        case SKPathVerb.Move: result.MoveTo(p[0]); break;
                        case SKPathVerb.Line: result.LineTo(p[1]); break;
                        case SKPathVerb.Quad: result.QuadTo(p[1], p[2]); break;
                        case SKPathVerb.Conic: result.ConicTo(p[1], p[2], weight); break;
                        case SKPathVerb.Cubic: result.CubicTo(p[1], p[2], p[3]); break;
                        case SKPathVerb.Close: result.Close(); break;
                    }
                }
            }
            start = end;
        }
        if (removed) return result;
        result.Dispose();
        return null;
    }

    /// <summary>Tips of the miters that Skia does not draw (see <see cref="DrawStroke"/>); null if there are none.</summary>
    private static SKPath? MiterTips(SKPath path, float width, float limit)
    {
        SKPath? tips = null;
        SKPoint start = default, current = default, first = default, last = default;
        bool hasFirst = false, hasLast = false;
        var half = width / 2;

        void Join(SKPoint at, SKPoint a, SKPoint b)
        {
            var la = a.Length;
            var lb = b.Length;
            var (ax, ay, bx, by) = (a.X / la, a.Y / la, b.X / lb, b.Y / lb);
            var cos = ax * bx + ay * by;
            if (1 + cos > 1f / 4096 || 1 + cos <= 0) return;   // Skia draws it itself, or an exact reversal without a tip
            var ratio = 1 / MathF.Sqrt((1 + cos) / 2);
            if (ratio > limit) return;
            // The outside lies opposite the turning direction; the tip points along ax − bx (bisector pointing outwards)
            var side = ax * by - ay * bx > 0 ? 1 : -1;
            var o1 = new SKPoint(at.X + side * ay * half, at.Y - side * ax * half);
            var o2 = new SKPoint(at.X + side * by * half, at.Y - side * bx * half);
            var tx = ax - bx;
            var ty = ay - by;
            var lt = MathF.Sqrt(tx * tx + ty * ty);
            var tip = new SKPoint(at.X + tx / lt * ratio * half, at.Y + ty / lt * ratio * half);
            tips ??= new SKPath();
            tips.MoveTo(o1);
            tips.LineTo(tip);
            tips.LineTo(o2);
            tips.LineTo(at);
            tips.Close();
        }

        void Segment(SKPoint into, SKPoint outOf)
        {
            if (into.X == 0 && into.Y == 0) return;
            if (hasLast) Join(current, last, into);
            else (first, hasFirst) = (into, true);
            (last, hasLast) = (outOf, true);
        }

        using var iterator = path.CreateRawIterator();
        var p = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = iterator.Next(p)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    start = current = p[0];
                    hasFirst = hasLast = false;
                    break;
                case SKPathVerb.Line:
                    Segment(p[1] - current, p[1] - current);
                    current = p[1];
                    break;
                case SKPathVerb.Quad or SKPathVerb.Conic:
                    Segment(p[1] != current ? p[1] - current : p[2] - current, p[2] != p[1] ? p[2] - p[1] : p[2] - current);
                    current = p[2];
                    break;
                case SKPathVerb.Cubic:
                    Segment(p[1] != current ? p[1] - current : p[2] != current ? p[2] - current : p[3] - current,
                        p[3] != p[2] ? p[3] - p[2] : p[3] != p[1] ? p[3] - p[1] : p[3] - current);
                    current = p[3];
                    break;
                case SKPathVerb.Close:
                    Segment(start - current, start - current);
                    if (hasFirst && hasLast) Join(start, last, first);
                    current = start;
                    hasFirst = hasLast = false;
                    break;
            }
        }
        return tips;
    }

    private static SKMatrix ToSk(Matrix m) => new((float)m.A, (float)m.C, (float)m.E, (float)m.B, (float)m.D, (float)m.F, 0, 0, 1);

    /// <summary>Appends only the first and last point of the path (for strokes smaller than one pixel); returns the number of verbs.</summary>
    private static int AppendCollapsed(DisplayList list, int index, SKPath path)
    {
        var (verbStart, verbCount, pointStart) = list.Paths[index];
        var points = list.Points;
        var count = 0;
        for (var v = verbStart; v < verbStart + verbCount; v++)
            count += list.Verbs[v] switch { PathVerb.Cubic => 6, PathVerb.Close => 0, _ => 2 };
        if (count < 2) return 0;
        var last = pointStart + count - 2;
        path.MoveTo(points[pointStart], points[pointStart + 1]);
        path.LineTo(points[last], points[last + 1]);
        return 2;
    }

    /// <summary>Appends path number <paramref name="index"/> to <paramref name="path"/>; returns the number of verbs.</summary>
    internal static int AppendPath(DisplayList list, int index, SKPath path)
    {
        var (verbStart, verbCount, pointStart) = list.Paths[index];
        var p = pointStart;
        var points = list.Points;
        for (var v = verbStart; v < verbStart + verbCount; v++)
        {
            switch (list.Verbs[v])
            {
                case PathVerb.Move:
                    path.MoveTo(points[p], points[p + 1]);
                    p += 2;
                    break;
                case PathVerb.Line:
                    path.LineTo(points[p], points[p + 1]);
                    p += 2;
                    break;
                case PathVerb.Cubic:
                    path.CubicTo(points[p], points[p + 1], points[p + 2], points[p + 3], points[p + 4], points[p + 5]);
                    p += 6;
                    break;
                case PathVerb.Close:
                    path.Close();
                    break;
            }
        }
        return verbCount;
    }

    /// <summary>
    /// Applied clip chain, one canvas save per link. On a switch, only the part above the common ancestor is undone and the rest
    /// appended — reapplying the whole chain from the root every time is quadratic for content that keeps clipping further (W without
    /// q/Q): 2 800 links took 11 s for one page instead of 0.09 s. Saving only every fourth link (fewer copies of the clip mask)
    /// measured as slow as the quadratic approach, with hardly any memory difference (74 instead of 72 MB).
    /// </summary>
    private sealed class ClipStack
    {
        private readonly List<(int Clip, int Level)> _applied = [];   // Level: save before this link
        private readonly Dictionary<int, int> _position = [];
        private readonly List<int> _missing = [];

        public void Switch(SKCanvas canvas, DisplayList list, int clip, Matrix pageToDevice, SKRect deviceArea, SKPath scratch)
        {
            // Upwards to an already applied link (or to the root)
            _missing.Clear();
            var c = clip;
            while (c > 0 && !_position.ContainsKey(c))
            {
                _missing.Add(c);
                c = list.Clips[c].Parent;
            }
            // Undo what lies above the common ancestor
            var keep = c > 0 ? _position[c] + 1 : 0;
            if (keep < _applied.Count)
            {
                canvas.RestoreToCount(_applied[keep].Level);
                for (var i = keep; i < _applied.Count; i++) _position.Remove(_applied[i].Clip);
                _applied.RemoveRange(keep, _applied.Count - keep);
            }
            for (var i = _missing.Count - 1; i >= 0; i--)
            {
                var level = canvas.Save();
                Apply(canvas, list, _missing[i], pageToDevice, deviceArea, scratch);
                _position[_missing[i]] = _applied.Count;
                _applied.Add((_missing[i], level));
            }
        }

        /// <summary>Applies one link of the chain to the canvas.</summary>
        private static void Apply(SKCanvas canvas, DisplayList list, int clip, Matrix pageToDevice, SKRect deviceArea, SKPath scratch)
        {
            var node = list.Clips[clip];
            // A rectangle stays a rectangle only under an axis-aligned mapping (also rotated by 90°) — masks lie in their own, possibly
            // rotated space (issue16742: mask rotated by 10°, its /BBox would otherwise become the enclosing rectangle)
            if (node.IsRectangle && (pageToDevice is { B: 0, C: 0 } || pageToDevice is { A: 0, D: 0 }))
            {
                // Rectangle: only its sides that lie inside the clip around it cut. Its bounds are cut by that clip already; applying a side
                // the outer clip has applied once more would cover the pixels along it twice with the antialiased edge — a line lying on the
                // edge of a form's box became lighter as soon as a block inside the form clipped to the same rectangle
                // (docs/FEHLERGEDAECHTNIS.md, No. 248)
                var own = node.Bounds;
                var outer = node.Parent > 0 ? list.Clips[node.Parent].Bounds : RectD.Infinite;
                const double Same = 1e-4;   // points: rounding of the same edge reached on another way
                const double Far = 1e7;
                var cut = RectD.FromLtrb(
                    own.Left > outer.Left + Same ? own.Left : own.Left - Far,
                    own.Top > outer.Top + Same ? own.Top : own.Top - Far,
                    own.Right < outer.Right - Same ? own.Right : own.Right + Far,
                    own.Bottom < outer.Bottom - Same ? own.Bottom : own.Bottom + Far);
                if (!(own.Right > own.Left && own.Bottom > own.Top)) cut = own;   // clips everything away: as it is
                else if (cut.Left < own.Left && cut.Top < own.Top && cut.Right > own.Right && cut.Bottom > own.Bottom) return;   // cuts nothing more
                // if it contains the whole tile, it changes nothing — otherwise clip rectangularly
                var r = pageToDevice.TransformBounds(cut);
                if (r.Left <= deviceArea.Left && r.Top <= deviceArea.Top && r.Right >= deviceArea.Right && r.Bottom >= deviceArea.Bottom) return;
                canvas.SetMatrix(SKMatrix.Identity);
                canvas.ClipRect(new SKRect((float)r.Left, (float)r.Top, (float)r.Right, (float)r.Bottom), SKClipOperation.Intersect, antialias: true);
                return;
            }
            canvas.SetMatrix(ToSk(list.Matrices[node.Matrix] * pageToDevice));
            scratch.Rewind();   // keep the memory (Reset would release it)
            AppendPath(list, node.Path, scratch);
            scratch.FillType = node.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
            canvas.ClipPath(scratch, SKClipOperation.Intersect, antialias: true);
        }
    }

    private static SKPathEffect? Dash(DisplayList list, int index, Dictionary<int, SKPathEffect> cache)
    {
        if (cache.TryGetValue(index, out var effect)) return effect;
        var dash = list.Dashes[index];
        // Skia requires an even count > 0 and does not like zero lengths in both parts
        var lengths = dash.Lengths.Select(l => Math.Max(l, 0.01f)).ToArray();
        effect = SKPathEffect.CreateDash(lengths, dash.Phase);
        cache[index] = effect;
        return effect;
    }

    private static void DrawImage(SKCanvas canvas, ImageSource source, PdfDocument doc, ImageCache images, SKPaint paint, Paint style,
        SKBlendMode? maskBlend = null, bool knockout = false, bool overprint = false)
    {
        // Unit square: image row 0 lies at the top (y = 1)
        var image = images.Get(source, doc);
        canvas.Concat(new SKMatrix(1, 0, 0, 0, -1, 1, 0, 0, 1));
        var unit = SKRect.Create(0, 0, 1, 1);
        if (image is null)
        {
            using var placeholder = new SKPaint { Color = ImagePlaceholder };
            canvas.DrawRect(unit, placeholder);
            return;
        }
        var maskOnly = maskBlend is not null;
        paint.BlendMode = maskBlend ?? ItemBlend(style.BlendMode, knockout, overprint);
        if (!maskOnly && source.IsStencil) OverprintPlates(paint, overprint, style.BlendMode, style.OverprintReplace);   // a mask paints the fill colour
        // Stencils are pure coverage (1 byte per pixel): Skia paints them in the colour of the paint; otherwise the opacity of the fill
        paint.Color = maskOnly ? SKColors.Black : source.IsStencil ? new SKColor(source.StencilColor) : new SKColor(255, 255, 255, (byte)(style.Fill >> 24));
        var total = canvas.TotalMatrix;
        var upscaling = Math.Abs(total.ScaleX) > image.Width * 1.5 || Math.Abs(total.ScaleY) > image.Height * 1.5;
        var interpolate = source.Dictionary.GetBoolean(InterpolateName) ?? false;
        // Small images strongly enlarged (stencils, patterns): without smoothing, unless the file asks for it (/Interpolate)
        var sampling = upscaling && !interpolate
            ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        // Black and white strongly reduced (screened scans, fax): area-averaged version at target size instead of mipmaps (moiré).
        // Target size: length of the mapped unit vectors (also when rotated)
        var (deviceWidth, deviceHeight) = (Math.Sqrt(total.ScaleX * total.ScaleX + total.SkewY * total.SkewY), Math.Sqrt(total.SkewX * total.SkewX + total.ScaleY * total.ScaleY));
        if (!upscaling && images.ReducedFor(source, image, deviceWidth, deviceHeight) is { } reduced)
        {
            image = reduced;
            sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        }
        // Inserted images: Lanczos version (see KONZEPT.md, section 5.13); the rest down to the target size (under an eighth octave) as usual
        else if (images.SmoothFor(source, image, deviceWidth, deviceHeight) is { } smooth)
        {
            image = smooth;
            sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        }
        // Reduced much more in one direction than in the other: evened by area first, the mipmaps do the rest alike in both
        else if (!upscaling && images.EvenFor(source, image, deviceWidth, deviceHeight) is { } even)
        {
            image = even;
        }
        if (knockout && source.IsStencil && !maskOnly && paint.BlendMode == SKBlendMode.Src)
        {
            // Stencil in a knockout group: only its shape replaces what lies underneath — first clear by coverage, then add colour ×
            // coverage ((1 − c)·dst + c·src). Src alone would write the whole rectangle (boxes around glyphs).
            var color = paint.Color;
            paint.BlendMode = SKBlendMode.DstOut;
            paint.Color = SKColors.Black;
            canvas.DrawImage(image, SKRect.Create(0, 0, image.Width, image.Height), unit, sampling, paint);
            paint.BlendMode = SKBlendMode.Plus;
            paint.Color = color;
        }
        canvas.DrawImage(image, SKRect.Create(0, 0, image.Width, image.Height), unit, sampling, paint);
    }

    /// <summary>Blend mode of an entry; in a knockout group a normal entry replaces what lies underneath (Src with anti-aliasing =
    /// cross-fade by coverage).
    /// Overprint (preview): Multiply — in RGB the usual approximation for colours printed on top of each other (yellow on cyan
    /// becomes green); exact results would require colour separations.</summary>
    private static SKBlendMode ItemBlend(byte mode, bool knockout, bool overprint = false) =>
        mode != 0 ? Blend(mode) : overprint ? SKBlendMode.Multiply : knockout ? SKBlendMode.Src : SKBlendMode.SrcOver;

    /// <summary>
    /// Overprint mode 1 of a DeviceCMYK colour in the preview (ISO 32000-2, 8.6.7 sentences 21–22): the plates whose component is not 0 replace
    /// those beneath, the others stay. In RGB the plates are taken as the channels — cyan red, magenta green, yellow blue —: the channels of the
    /// replaced plates (<paramref name="replace"/>, bits 1, 2, 4) take the colour's, the others are multiplied by it as with any overprint
    /// (black, carried by every channel, darkens them). Before, all channels were multiplied: 0.2 cyan over full cyan stayed full cyan
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1130). Called after the blend mode is set; any later blend mode replaces the blender again.
    /// </summary>
    private static void OverprintPlates(SKPaint paint, bool overprint, byte blendMode, int replace)
    {
        if (!overprint || blendMode != 0 || (replace & 7) == 0 || OverprintBlender(replace & 7) is not { } blender) return;
        paint.Blender = blender;
    }

    // per mask of replaced channels (1 to 7), built once
    private static readonly SKBlender?[] OverprintBlenders = new SKBlender?[8];
    private static readonly object OverprintLock = new();

    // premultiplied: Multiply in the kept channels, source over in the replaced ones
    private const string OverprintSksl = """
        uniform half3 replaced;
        half4 main(half4 src, half4 dst) {
            half3 multiplied = src.rgb * (1 - dst.a) + dst.rgb * (1 - src.a) + src.rgb * dst.rgb;
            half3 over = src.rgb + dst.rgb * (1 - src.a);
            return half4(mix(multiplied, over, replaced), src.a + dst.a * (1 - src.a));
        }
        """;

    private static SKBlender? OverprintBlender(int replace)
    {
        lock (OverprintLock)
        {
            if (OverprintBlenders[replace] is { } built) return built;
            using var effect = SKRuntimeEffect.CreateBlender(OverprintSksl, out _);
            if (effect is null) return null;
            var uniforms = new SKRuntimeEffectUniforms(effect)
            {
                ["replaced"] = new[] { (replace & 1) != 0 ? 1f : 0f, (replace & 2) != 0 ? 1f : 0f, (replace & 4) != 0 ? 1f : 0f },
            };
            return OverprintBlenders[replace] = effect.ToBlender(uniforms);
        }
    }

    /// <summary>
    /// Applies a soft mask to the open layer: draws its content into a layer of its own (Luminosity: over the backdrop /BC), turns
    /// luminance or alpha (including /TR) into opacity and composites it with DstIn.
    /// </summary>
    private static void ApplyMask(SKCanvas canvas, SoftMask mask, Matrix pageToDevice, SKRect deviceArea, PdfDocument doc, ImageCache images,
        CancellationToken cancel)
    {
        canvas.ResetMatrix();
        using var filter = MaskFilter(mask);
        using var paint = new SKPaint { BlendMode = SKBlendMode.DstIn, ColorFilter = filter };
        canvas.SaveLayer(deviceArea, paint);
        if (mask.Luminosity) canvas.Clear(new SKColor(mask.Backdrop | 0xFF000000));
        Render(canvas, mask.Content, doc, mask.ToPage * pageToDevice, deviceArea, images, cancel, enhanceThinLines: false);
        canvas.Restore();
        ClearOutsideMask(canvas, mask, pageToDevice);
    }

    /// <summary>
    /// Alpha mask with a /TR that does not map 0 to 0: outside the BBox of its group the mask stays 0 (/TR applies only inside) — the
    /// layer is cleared there (smask_alpha_oob_transfer), as other PDF readers do.
    /// </summary>
    private static void ClearOutsideMask(SKCanvas canvas, SoftMask mask, Matrix pageToDevice)
    {
        if (mask.Luminosity || mask.Transfer is not { } transfer || transfer[0] == 0 || mask.Bounds is not { } b) return;
        canvas.Save();
        canvas.SetMatrix(ToSk(mask.ToPage * pageToDevice));
        canvas.ClipRect(SKRect.Create((float)b.Left, (float)b.Top, (float)b.Width, (float)(b.Bottom - b.Top)), SKClipOperation.Difference);
        using var clear = new SKPaint { BlendMode = SKBlendMode.Clear };
        canvas.DrawPaint(clear);
        canvas.Restore();
    }

    /// <summary>Rasterised masks: maximum area per drawing (4 million pixels, 16 MB) and maximum number.</summary>
    private const double MaxMaskPixels = 4_000_000;
    private const int MaxMaskImages = 8;

    private static int[] CountMaskUses(DisplayList list)
    {
        var uses = new int[list.Masks.Length];
        foreach (var group in list.Groups)
            if (group.Mask >= 0) uses[group.Mask]++;
        return uses;
    }

    /// <summary>Content of a mask over the whole area of the drawing (Luminosity: on /BC), without conversion — the colour matrix or
    /// /TR is added when compositing.</summary>
    private static SKImage RasterizeMask(SoftMask mask, Matrix pageToDevice, SKRect area, PdfDocument doc, ImageCache images, CancellationToken cancel)
    {
        var w = Math.Max(1, (int)Math.Ceiling(area.Width));
        var h = Math.Max(1, (int)Math.Ceiling(area.Height));
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(mask.Luminosity ? new SKColor(mask.Backdrop | 0xFF000000) : SKColors.Transparent);
        Render(surface.Canvas, mask.Content, doc, mask.ToPage * pageToDevice * Matrix.Translate(-area.Left, -area.Top), SKRect.Create(0, 0, w, h),
            images, cancel, enhanceThinLines: false);
        return surface.Snapshot();
    }

    // Luminance as in PDF (11.6.5.3 refers to the conversion to grey): 0.30 R + 0.59 G + 0.11 B becomes opacity
    private static readonly float[] Luminance = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.30f, 0.59f, 0.11f, 0, 0];
    private static readonly byte[] IdentityTable = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];

    private static SKColorFilter? MaskFilter(SoftMask mask)
    {
        var transfer = mask.Transfer is { } t ? SKColorFilter.CreateTable(t, IdentityTable, IdentityTable, IdentityTable) : null;
        if (!mask.Luminosity) return transfer;
        var luminance = SKColorFilter.CreateColorMatrix(Luminance);
        if (transfer is null) return luminance;
        using (transfer)
        using (luminance)
            return SKColorFilter.CreateCompose(transfer, luminance);
    }

    private static SKBlendMode Blend(byte mode) => mode switch
    {
        1 => SKBlendMode.Multiply,
        2 => SKBlendMode.Screen,
        3 => SKBlendMode.Overlay,
        4 => SKBlendMode.Darken,
        5 => SKBlendMode.Lighten,
        6 => SKBlendMode.ColorDodge,
        7 => SKBlendMode.ColorBurn,
        8 => SKBlendMode.HardLight,
        9 => SKBlendMode.SoftLight,
        10 => SKBlendMode.Difference,
        11 => SKBlendMode.Exclusion,
        12 => SKBlendMode.Hue,
        13 => SKBlendMode.Saturation,
        14 => SKBlendMode.Color,
        15 => SKBlendMode.Luminosity,
        _ => SKBlendMode.SrcOver,
    };
}
