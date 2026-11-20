using System.Diagnostics;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Core.Printing;
using SkiaSharp;

namespace PdfKnead.Rendering;

/// <summary>
/// The pages of a print job: per page its source — directly from its file or, if something is placed on it, flattened
/// (<see cref="SheetPdf.NeedsFlattening"/>; so large scans get no second copy in memory). Display lists per page survive option
/// changes (in the shared pool <see cref="CachePool.Lists"/>), images live in the shared image pool. Thread-safe.
/// </summary>
/// <remarks>
/// Released only when nobody reads from it any more (<see cref="TryAcquire"/>) — releasing it under a reader would terminate the
/// program (reading from a file that is already unmapped).
/// </remarks>
public sealed class PrintPages : ICacheUser, IDisposable
{
    private sealed class Entry(Lazy<DisplayList> list)
    {
        public Lazy<DisplayList> List { get; } = list;
        public long Bytes;
        public LinkedListNode<int>? Node;
    }

    private readonly PrintPage[] _pages;
    private readonly PdfDocument? _flat;
    private readonly ImageCache _images = new(CachePool.Images);
    private readonly Dictionary<int, Entry> _lists = [];
    private readonly LinkedList<int> _order = [];
    private readonly Lock _lock = new();
    /// <summary>Cancels display lists in progress when the pages are released (not the caller's token: a list the preview has started
    /// is needed by the print job right afterwards).</summary>
    private readonly CancellationTokenSource _close = new();
    private long _lastActive = Stopwatch.GetTimestamp();
    private int _users;
    private bool _closing;
    private bool _disposed;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the pages are really released (no reader left).</summary>
    public Task Closed => _closed.Task;

    private PrintPages(PrintPage[] pages, PdfDocument? flat, int flattened)
    {
        _pages = pages;
        _flat = flat;
        Flattened = flattened;
        CachePool.Lists.Register(this);
    }

    /// <summary>
    /// Prepares the pages: only those with elements, fields, annotations or redactions are flattened (one file in memory), the others
    /// come from their file. Cancellable.
    /// </summary>
    /// <param name="main">The document the pages belong to.</param>
    /// <param name="items">The pages to print, in print order.</param>
    /// <param name="cancel">Cancels flattening.</param>
    /// <returns>The prepared pages; the caller disposes them.</returns>
    public static PrintPages Prepare(PdfDocument main, IReadOnlyList<PageItem> items, CancellationToken cancel = default)
    {
        var flatten = new List<int>();
        for (var i = 0; i < items.Count; i++)
            if (SheetPdf.NeedsFlattening(items[i])) flatten.Add(i);
        var flat = flatten.Count > 0 ? SheetPdf.Flatten(main, [.. flatten.Select(i => items[i])], SkiaImageCodec.Instance, cancel) : null;
        var pages = new PrintPage[items.Count];
        for (var i = 0; i < items.Count; i++) pages[i] = new PrintPage(items[i].Source, items[i].Page);
        for (var k = 0; k < flatten.Count && flat is not null && k < flat.PageCount; k++) pages[flatten[k]] = new PrintPage(flat, flat.Pages[k]);
        return new PrintPages(pages, flat, flatten.Count);
    }

    /// <summary>Number of pages.</summary>
    public int Count => _pages.Length;

    /// <summary>The pages with their source document.</summary>
    public IReadOnlyList<PrintPage> Pages => _pages;

    /// <summary>Number of pages that were flattened (for measurements and tests).</summary>
    public int Flattened { get; }

    /// <summary>Size of the page as it appears (rotated), in points.</summary>
    /// <param name="page">Zero-based index in <see cref="Pages"/>.</param>
    /// <returns>Width and height in points.</returns>
    public (double Width, double Height) Size(int page) => (_pages[page].Page.Width, _pages[page].Page.Height);

    // ------------------------------------------------------------ Lifetime

    /// <summary>Starts reading; always call <see cref="Release"/> afterwards if it returns true.</summary>
    /// <returns>False if the pages are already being released.</returns>
    public bool TryAcquire()
    {
        lock (_lock)
        {
            if (_closing) return false;
            _users++;
            return true;
        }
    }

    /// <summary>Ends reading started with <see cref="TryAcquire"/>; the last reader completes a pending release.</summary>
    public void Release()
    {
        bool dispose;
        lock (_lock) dispose = --_users == 0 && _closing;
        if (dispose) DisposeNow();
    }

    /// <summary>Releases the pages as soon as nobody reads any more (immediately if nobody is reading); cancels display lists in progress.</summary>
    public void Dispose()
    {
        bool now;
        lock (_lock)
        {
            _closing = true;
            now = _users == 0;
        }
        _close.Cancel();
        if (now) DisposeNow();
    }

    private void DisposeNow()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        CachePool.Lists.Unregister(this);
        long bytes;
        lock (_lists)
        {
            bytes = _lists.Values.Sum(e => e.Bytes);
            _lists.Clear();
            _order.Clear();
        }
        CachePool.Lists.Add(-bytes);
        _images.Clear();
        _images.Detach();
        _flat?.Dispose();
        _closed.TrySetResult();
    }

    // ------------------------------------------------------------ Display lists

    /// <inheritdoc/>
    public long LastActive => Interlocked.Read(ref _lastActive);

    /// <summary>Display list of the page as printed (layers in their print state).</summary>
    private DisplayList ListFor(int index, CancellationToken cancel)
    {
        Interlocked.Exchange(ref _lastActive, Stopwatch.GetTimestamp());
        Entry entry;
        lock (_lists)
        {
            if (!_lists.TryGetValue(index, out entry!))
            {
                var page = _pages[index];
                entry = new Entry(new Lazy<DisplayList>(() => PageInterpreter.Build(page.Document, page.Page, _close.Token, forPrint: true),
                    LazyThreadSafetyMode.ExecutionAndPublication));
                _lists[index] = entry;
            }
            if (entry.Node is not null) _order.Remove(entry.Node);
            entry.Node = _order.AddFirst(index);
        }
        DisplayList list;
        try
        {
            list = entry.List.Value;
        }
        catch
        {
            // Cancelled or broken: do not remember, build anew next time
            lock (_lists)
                if (_lists.TryGetValue(index, out var e) && e == entry)
                {
                    _lists.Remove(index);
                    if (entry.Node is not null) _order.Remove(entry.Node);
                }
            throw;
        }
        var added = 0L;
        lock (_lists)
            if (entry.Bytes == 0 && _lists.ContainsKey(index))
            {
                entry.Bytes = Math.Max(1, list.EstimatedBytes);
                added = entry.Bytes;
            }
        if (added > 0)
        {
            CachePool.Lists.Add(added);
            CachePool.Lists.Enforce(this);
        }
        return list;
    }

    /// <inheritdoc/>
    public void Free(CachePool pool, long bytes)
    {
        if (!ReferenceEquals(pool, CachePool.Lists)) return;
        long freed = 0;
        lock (_lists)
            while (freed < bytes && _order.Last is { } last)
            {
                _order.RemoveLast();
                if (_lists.Remove(last.Value, out var entry))
                {
                    entry.Node = null;
                    freed += entry.Bytes;
                }
            }
        pool.Add(-freed);
    }

    // ------------------------------------------------------------ Rendering

    /// <summary>
    /// Draws the sheet into the area <paramref name="area"/> (device pixels at <paramref name="pixelsPerPoint"/>); the canvas is as large
    /// as the area and already white. Each page goes to its place, clipped to its box — as in the print PDF.
    /// <see cref="SheetPlacement.Page"/> is the index in <see cref="Pages"/> here.
    /// </summary>
    /// <param name="canvas">Target canvas; its origin corresponds to the top left corner of <paramref name="area"/>.</param>
    /// <param name="sheet">The sheet.</param>
    /// <param name="pixelsPerPoint">Resolution (1 = 72 dpi).</param>
    /// <param name="area">Area of the sheet in device pixels, origin at the top left of the sheet.</param>
    /// <param name="cancel">Cancels rendering.</param>
    /// <exception cref="OperationCanceledException">The pages are already being released, or rendering was cancelled.</exception>
    public void Draw(SKCanvas canvas, PrintSheet sheet, double pixelsPerPoint, SKRectI area, CancellationToken cancel)
    {
        if (!TryAcquire()) throw new OperationCanceledException();
        try
        {
            foreach (var p in sheet.Placements)
            {
                cancel.ThrowIfCancellationRequested();
                if (p.Page < 0 || p.Page >= _pages.Length) continue;
                var x = p.X * pixelsPerPoint;
                var y = (sheet.Height - p.Top) * pixelsPerPoint;
                var place = new SKRect((float)x, (float)y, (float)(x + p.Width * p.Scale * pixelsPerPoint), (float)(y + p.Height * p.Scale * pixelsPerPoint));
                // Poster: only the tile
                if (p.Clip is { } clip)
                    place = SKRect.Intersect(place, new SKRect((float)(clip.Left * pixelsPerPoint), (float)((sheet.Height - clip.Top) * pixelsPerPoint),
                        (float)(clip.Right * pixelsPerPoint), (float)((sheet.Height - clip.Bottom) * pixelsPerPoint)));
                var visible = SKRect.Intersect(place, new SKRect(area.Left, area.Top, area.Right, area.Bottom));
                if (visible.IsEmpty) continue;
                var (doc, page) = _pages[p.Page];
                var list = ListFor(p.Page, cancel);
                var pageToDevice = PageGeometry.PageToDevice(page, p.Scale * pixelsPerPoint) * Matrix.Translate(x, y);
                canvas.Save();
                visible.Offset(-area.Left, -area.Top);
                canvas.ClipRect(visible);
                SkiaRenderer.RenderTile(canvas, list, doc, pageToDevice, area, _images, cancel);
                canvas.Restore();
            }
            if (sheet.Borders)
            {
                using var pen = new SKPaint { Color = new SKColor(102, 102, 102), StrokeWidth = (float)Math.Max(1, 0.5 * pixelsPerPoint), IsStroke = true, IsAntialias = true };
                foreach (var p in sheet.Placements)
                    canvas.DrawRect((float)(p.X * pixelsPerPoint - area.Left), (float)((sheet.Height - p.Top) * pixelsPerPoint - area.Top),
                        (float)(p.Width * p.Scale * pixelsPerPoint), (float)(p.Height * p.Scale * pixelsPerPoint), pen);
            }
            if (sheet.Marks is { Count: > 0 } marks) DrawMarks(canvas, sheet, marks, pixelsPerPoint, area);
        }
        finally
        {
            Release();
        }
    }

    /// <summary>Draws marks (poster): hairlines, registration marks and text, black — as in the print PDF (<see cref="SheetPdf"/>).</summary>
    private static void DrawMarks(SKCanvas canvas, PrintSheet sheet, IReadOnlyList<SheetMark> marks, double pixelsPerPoint, SKRectI area)
    {
        float X(double x) => (float)(x * pixelsPerPoint - area.Left);
        float Y(double y) => (float)((sheet.Height - y) * pixelsPerPoint - area.Top);
        using var pen = new SKPaint { Color = SKColors.Black, StrokeWidth = (float)Math.Max(1, SheetMarkStroke * pixelsPerPoint), IsStroke = true, IsAntialias = true };
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        foreach (var m in marks)
            switch (m.Kind)
            {
                case SheetMarkKind.Line:
                    canvas.DrawLine(X(m.X), Y(m.Y), X(m.X2), Y(m.Y2), pen);
                    break;
                case SheetMarkKind.Cross:
                    var half = (float)(m.Size / 2 * pixelsPerPoint);
                    canvas.DrawCircle(X(m.X), Y(m.Y), half * 0.6f, pen);
                    canvas.DrawLine(X(m.X) - half, Y(m.Y), X(m.X) + half, Y(m.Y), pen);
                    canvas.DrawLine(X(m.X), Y(m.Y) - half, X(m.X), Y(m.Y) + half, pen);
                    break;
                case SheetMarkKind.Text when m.Text is { Length: > 0 } text:
                    using (var font = new SKFont(SKTypeface.Default, (float)(m.Size * pixelsPerPoint)))
                        canvas.DrawText(text, X(m.X), Y(m.Y), SKTextAlign.Left, font, ink);
                    break;
            }
    }

    /// <summary>Line width of the marks in points (as in the print PDF).</summary>
    public const double SheetMarkStroke = 0.4;

    /// <summary>Renders the whole sheet as an image (preview).</summary>
    /// <param name="sheet">The sheet.</param>
    /// <param name="pixelsPerPoint">Resolution (1 = 72 dpi).</param>
    /// <param name="cancel">Cancels rendering.</param>
    /// <returns>The bitmap; the caller disposes it.</returns>
    public SKBitmap Render(PrintSheet sheet, double pixelsPerPoint, CancellationToken cancel = default)
    {
        var w = Math.Max(1, (int)Math.Ceiling(sheet.Width * pixelsPerPoint));
        var h = Math.Max(1, (int)Math.Ceiling(sheet.Height * pixelsPerPoint));
        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        try
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SkiaRenderer.Paper);
            Draw(canvas, sheet, pixelsPerPoint, new SKRectI(0, 0, w, h), cancel);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
}

/// <summary>
/// The sheets of a job (an arrangement of the <see cref="PrintPages"/>): preview, bands for Windows, PDF for CUPS and "save as PDF".
/// Lightweight — an option change only builds a new arrangement, no file. Not thread-safe (one band surface per instance).
/// </summary>
public sealed class PrintJobSheets : ISheetSource, IDisposable
{
    private readonly PrintPages _pages;
    private readonly List<PrintSheet> _sheets;
    private SKBitmap? _band;

    /// <summary>Creates the arrangement.</summary>
    /// <param name="pages">The prepared pages; not owned.</param>
    /// <param name="layout">Sheets; <see cref="SheetPlacement.Page"/> is the position in <paramref name="indices"/>.</param>
    /// <param name="indices">Indices into <paramref name="pages"/> in print order.</param>
    public PrintJobSheets(PrintPages pages, IReadOnlyList<PrintSheet> layout, IReadOnlyList<int> indices)
    {
        _pages = pages;
        _sheets = [.. layout.Select(s => s with { Placements = [.. s.Placements.Select(p => p with { Page = p.Page >= 0 && p.Page < indices.Count ? indices[p.Page] : -1 })] })];
    }

    /// <summary>The prepared pages.</summary>
    public PrintPages Pages => _pages;

    /// <summary>The sheets; <see cref="SheetPlacement.Page"/> is the index in <see cref="PrintPages.Pages"/> here.</summary>
    public IReadOnlyList<PrintSheet> Sheets => _sheets;

    /// <inheritdoc/>
    public int SheetCount => _sheets.Count;

    /// <inheritdoc/>
    public (double Width, double Height) SheetSize(int sheet) => (_sheets[sheet].Width, _sheets[sheet].Height);

    /// <summary>Renders the whole sheet as an image (preview).</summary>
    /// <param name="sheet">Zero-based sheet index.</param>
    /// <param name="pixelsPerPoint">Resolution (1 = 72 dpi).</param>
    /// <param name="cancel">Cancels rendering.</param>
    /// <returns>The bitmap; the caller disposes it.</returns>
    public SKBitmap Render(int sheet, double pixelsPerPoint, CancellationToken cancel = default) => _pages.Render(_sheets[sheet], pixelsPerPoint, cancel);

    /// <inheritdoc/>
    public void RenderBand(int sheet, double pixelsPerPoint, int top, int width, int rows, Span<byte> pixels, int stride, bool gray, CancellationToken cancel)
    {
        // One drawing surface for all bands of the job (instead of a new one per band)
        if (_band is null || _band.Width != width || _band.Height < rows)
        {
            _band?.Dispose();
            _band = new SKBitmap(new SKImageInfo(width, rows, SKColorType.Rgba8888, SKAlphaType.Premul));
        }
        using (var canvas = new SKCanvas(_band))
        {
            canvas.Clear(SkiaRenderer.Paper);
            canvas.ClipRect(new SKRect(0, 0, width, rows));
            _pages.Draw(canvas, _sheets[sheet], pixelsPerPoint, new SKRectI(0, top, width, top + rows), cancel);
        }
        Convert(_band, width, rows, pixels, stride, gray);
    }

    /// <summary>RGBA top row first → BGR (or grey, 1 byte) bottom row first, like a Windows DIB.</summary>
    private static unsafe void Convert(SKBitmap band, int width, int rows, Span<byte> target, int stride, bool gray)
    {
        var source = (byte*)band.GetPixels();
        var rowBytes = band.RowBytes;
        fixed (byte* output = target)
            for (var y = 0; y < rows; y++)
            {
                var s = source + (long)y * rowBytes;
                var t = output + (long)(rows - 1 - y) * stride;
                if (gray)
                    for (var x = 0; x < width; x++, s += 4)
                        t[x] = (byte)((s[0] * 77 + s[1] * 150 + s[2] * 29) >> 8);
                else
                    for (var x = 0; x < width; x++, s += 4, t += 3)
                    {
                        t[0] = s[2];
                        t[1] = s[1];
                        t[2] = s[0];
                    }
            }
    }

    /// <inheritdoc/>
    /// <exception cref="OperationCanceledException">The pages are already being released, or writing was cancelled.</exception>
    public void WritePdf(IReadOnlyList<int> sheets, Stream output, CancellationToken cancel)
    {
        if (!_pages.TryAcquire()) throw new OperationCanceledException();
        try
        {
            SheetPdf.Write(_pages.Pages, _sheets, sheets, output, cancel);
        }
        finally
        {
            _pages.Release();
        }
    }

    /// <summary>Writes the sheets as images in a PDF (permission "low resolution only" with CUPS): JPEG at <paramref name="dpi"/>, quality 90.</summary>
    /// <param name="sheets">Sheet indices in output order (−1 = blank page of the size of the preceding sheet).</param>
    /// <param name="dpi">Image resolution in dots per inch.</param>
    /// <param name="output">Target stream.</param>
    /// <param name="cancel">Cancels writing.</param>
    public void WriteRasterPdf(IReadOnlyList<int> sheets, int dpi, Stream output, CancellationToken cancel)
    {
        var builder = new RasterPdfBuilder(output);
        var last = _sheets.Count > 0 ? _sheets[0] : null;
        foreach (var index in sheets)
        {
            cancel.ThrowIfCancellationRequested();
            var sheet = index >= 0 && index < _sheets.Count ? _sheets[index] : (last ?? new PrintSheet(595.28, 841.89, []))  with { Placements = [] };
            if (index >= 0) last = sheet;
            using var bitmap = _pages.Render(sheet, dpi / 72.0, cancel);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            builder.AddPage(sheet.Width, sheet.Height, data.AsSpan(), "DCTDecode", bitmap.Width, bitmap.Height);
        }
        builder.Finish();
    }

    /// <summary>Releases the band surface; the pages are not disposed.</summary>
    public void Dispose()
    {
        _band?.Dispose();
        _band = null;
    }
}
