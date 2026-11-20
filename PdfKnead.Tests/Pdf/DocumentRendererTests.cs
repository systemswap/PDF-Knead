using System.Collections.Concurrent;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>Tile management: budget in bytes, eviction, events.</summary>
public sealed class DocumentRendererTests
{
    private static void WaitIdle(DocumentRenderer renderer)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!renderer.IsIdle)
        {
            Assert.True(DateTime.UtcNow < until, "Renderer wird nicht fertig");
            Thread.Sleep(10);
        }
    }

    private static List<TileKey> AllTiles(DocumentRenderer renderer, int page, int level)
    {
        var (w, h) = renderer.SizeAt(page, level, 0);
        var keys = new List<TileKey>();
        for (var y = 0; y * DocumentRenderer.TileSize < h; y++)
        for (var x = 0; x * DocumentRenderer.TileSize < w; x++)
            keys.Add(new TileKey(page, level, x, y, 0));
        return keys;
    }

    [Fact]
    public void Tiles_StayWithinTheByteBudget_AndReportEachReadyTile()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        const long budget = 4L << 20;   // four full tiles
        using var renderer = new DocumentRenderer(doc, workers: 2, tileBudgetBytes: budget);
        var ready = new ConcurrentBag<TileKey>();
        renderer.TileReady += ready.Add;

        var keys = AllTiles(renderer, 0, 4);   // A4 at 4 px/pt: 2381 × 3368 → 5 × 7 tiles
        Assert.True(keys.Count > 8);
        renderer.Request("ansicht", keys);
        WaitIdle(renderer);

        Assert.Equal(keys.Count, ready.Count);
        Assert.Equal(keys.ToHashSet(), ready.ToHashSet());
        long held = 0;
        foreach (var key in keys)
            if (renderer.TryGetTile(key) is { } image) held += (long)image.Width * image.Height * 4;
        Assert.InRange(held, 1, budget);
    }

    [Fact]
    public void SameRequest_DoesNotRenderAgain()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        using var renderer = new DocumentRenderer(doc, workers: 1);
        var count = 0;
        renderer.TileReady += _ => Interlocked.Increment(ref count);
        var keys = AllTiles(renderer, 0, 0);
        renderer.Request("ansicht", keys);
        WaitIdle(renderer);
        var first = count;
        renderer.Request("ansicht", keys);
        renderer.Request("ansicht", [.. keys]);
        WaitIdle(renderer);
        Assert.Equal(first, count);
        Assert.All(keys, k => Assert.NotNull(renderer.TryGetTile(k)));
    }

    [Fact]
    public void ToggleThinLines_ClearsTiles_AndReportsIt()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        using var renderer = new DocumentRenderer(doc, workers: 1);
        var key = new TileKey(0, renderer.OverviewLevel(0), 0, 0, 0);
        renderer.Request("vorschau", [key]);
        WaitIdle(renderer);
        Assert.NotNull(renderer.TryGetTile(key));

        var cleared = false;
        renderer.Cleared += () => cleared = true;
        renderer.EnhanceThinLines = !renderer.EnhanceThinLines;
        Assert.True(cleared);
        Assert.Null(renderer.TryGetTile(key));

        // The same list again: the tile is missing now and is drawn again
        renderer.Request("vorschau", [key]);
        WaitIdle(renderer);
        Assert.NotNull(renderer.TryGetTile(key));
    }

    [Fact]
    public void ToggleOverprintPreview_ClearsTiles()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        using var renderer = new DocumentRenderer(doc, workers: 1);
        var key = new TileKey(0, renderer.OverviewLevel(0), 0, 0, 0);
        renderer.Request("vorschau", [key]);
        WaitIdle(renderer);
        Assert.NotNull(renderer.TryGetTile(key));
        renderer.OverprintPreview = true;
        Assert.Null(renderer.TryGetTile(key));
        renderer.OverprintPreview = true;   // unchanged: nothing to do
        renderer.Request("vorschau", [key]);
        WaitIdle(renderer);
        Assert.NotNull(renderer.TryGetTile(key));
    }
}

public sealed class CachePoolTests
{
    private sealed class User(CachePool pool, long lastActive, long held) : ICacheUser
    {
        public long Held = held;
        public List<long> Asked { get; } = [];

        public long LastActive { get; } = lastActive;

        public void Free(CachePool p, long bytes)
        {
            Asked.Add(bytes);
            var freed = Math.Min(bytes, Held);
            Held -= freed;
            pool.Add(-freed);
        }
    }

    [Fact]
    public void Enforce_TheLongestIdleGivesFirst_TheActiveLast()
    {
        var pool = new CachePool("Test", 100);
        var idle = new User(pool, lastActive: 1, held: 30);
        var older = new User(pool, lastActive: 0, held: 50);
        var active = new User(pool, lastActive: 9, held: 60);
        foreach (var u in new[] { idle, older, active })
        {
            pool.Register(u);
            pool.Add(u.Held);
        }
        pool.Enforce(active);   // 140 used, 100 allowed
        Assert.Equal([40L], older.Asked);   // idle for the longest time: gives first
        Assert.Empty(idle.Asked);           // after that it was already enough
        Assert.Empty(active.Asked);
        Assert.Equal(100, pool.Used);

        pool.Add(80);                       // the active user needs more than the others hold
        active.Held += 80;
        pool.Enforce(active);
        Assert.Equal(0, older.Held);
        Assert.Equal(0, idle.Held);
        Assert.Equal(100, pool.Used);       // the rest came from the active user itself
    }
}
