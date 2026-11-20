using System.Diagnostics;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Cli;

/// <summary>
/// <c>pdf-knead speichertest &lt;folder&gt; --bilder</c> („Bilder bearbeiten“, KONZEPT.md, section 5.20): per page, every picture and graphic
/// (each group as the tool chooses it) is moved — the new content, read again by the interpreter of the display, must draw the same
/// objects in the same order, each where the edit put it —, moved back (the page then looks as before, pixel for pixel), saved and
/// reopened (the same as in memory); one group deleted (exactly its objects gone) and one picture cut to its left half (what stays lies
/// in the frame, the cut away pixels have left the image).
/// </summary>
internal static class ImageEditCheck
{
    /// <summary>The move: not on a whole point, so that nothing lands on its old place by chance.</summary>
    private static readonly Matrix Move = Matrix.Translate(37.5, -23.25);

    /// <summary>At most this many groups of a page are moved (spread over the page's objects).</summary>
    private const int MaxGroups = 60;

    /// <summary>Allowance for positions in points (bounds from the interpreter, rounding of the written matrices).</summary>
    private const double Tolerance = 0.05;

    public static int Run(List<string> files, Dictionary<string, string> passwords, int maxPages, string? keep)
    {
        int ok = 0, failed = 0, skipped = 0, pages = 0, objects = 0;
        var watch = Stopwatch.StartNew();
        foreach (var file in files)
        {
            PdfDocument doc;
            try
            {
                doc = PdfDocument.Open(file, passwords.GetValueOrDefault(Path.GetFileName(file)));
                if (doc.PageCount == 0) throw new PdfException("keine Seiten");
            }
            catch (Exception)
            {
                skipped++;
                continue;
            }
            using (doc)
            {
                var problems = new List<string>();
                try
                {
                    var items = PageItem.From(doc);
                    var moved = (PageItem[])items.Clone();
                    for (var p = 0; p < Math.Min(items.Length, maxPages); p++)
                    {
                        var page = CheckPage(doc, items[p], p, problems);
                        if (page is null) continue;
                        moved[p] = page.Value.Moved;
                        pages++;
                        objects += page.Value.Objects;
                    }
                    CheckSaved(doc, items, moved, maxPages, keep, file, passwords.GetValueOrDefault(Path.GetFileName(file)), problems);
                }
                catch (Exception e)
                {
                    problems.Add($"{e.GetType().Name}: {e.Message}");
                }
                if (problems.Count == 0) ok++;
                else
                {
                    failed++;
                    Console.WriteLine($"{Path.GetFileName(file)}: {string.Join("; ", problems.Take(6))}{(problems.Count > 6 ? $" … ({problems.Count})" : "")}");
                }
            }
        }
        Console.WriteLine($"Dateien: {ok + failed} (bestanden {ok}, Abweichungen {failed}), übersprungen (unlesbar): {skipped}; " +
                          $"Seiten mit Objekten: {pages}, Objekte: {objects}, Zeit {watch.Elapsed.TotalSeconds:0.0} s");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Moves, moves back, deletes and cuts on one page; null if the page has no objects.</summary>
    private static (PageItem Moved, int Objects)? CheckPage(PdfDocument doc, PageItem item, int index, List<string> problems)
    {
        var name = $"S. {index + 1}";
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var found = ContentObjectEditor.ObjectsOf(item, cancel.Token);
        if (found.Count == 0) return null;

        // the groups as the tool chooses them, spread over the page
        var groups = new List<IReadOnlyList<int>>();
        var seen = new HashSet<int>();
        var step = Math.Max(1, found.Count / MaxGroups);
        for (var i = 0; i < found.Count && groups.Count < MaxGroups; i += step)
        {
            if (seen.Contains(i)) continue;
            var group = found.GroupOf(i);
            seen.UnionWith(group);
            groups.Add(group);
        }
        var chosen = groups.SelectMany(g => g).Distinct().Order().ToList();
        var keys = chosen.Select(found.KeyOf).ToList();
        var chosenSet = chosen.ToHashSet();

        // moved: the new content, read again, draws the same objects in the same order, each where the edit put it
        var moved = ContentObjectEditor.Transform(item, keys, Move, found);
        // what could not be seen before (outside every clip) may land anywhere; nobody chooses it
        var unseen = Enumerable.Range(0, found.Count).Select(i => !ContentTable.Valid(found.VisibleOf(i))).ToList();
        var expected = Enumerable.Range(0, found.Count).Select(i => chosenSet.Contains(i) ? found[i].Moved(Move).Visible : found.VisibleOf(i)).ToList();
        var pageBox = PageBox(item.Page);
        var kinds = Enumerable.Range(0, found.Count).Select(i => found[i].Kind).ToList();
        CompareObjects(ContentObjects.Find(doc, moved.Page, cancel.Token), expected, unseen, kinds, pageBox, $"{name} verschoben", problems, chosenSet);
        // the view of the moved state (what the tool shows) agrees with that
        var view = found.WithEdits(moved.ObjectEdits);
        CompareObjects(view, expected, unseen, kinds, pageBox, $"{name} Sicht", problems);

        // moved back: the page as before
        var back = ContentObjectEditor.Transform(moved, keys, Move.Invert(), found);
        using (var a = Render(doc, item.Page))
        using (var b = Render(doc, back.Page))
            if (a is not null && b is not null && SaveCheck.Differing(a, b) is var differing and > 0)
                problems.Add($"{name} zurück: {differing} Pixel anders");

        // one group deleted: exactly its objects gone, the others where they were
        var gone = groups[0].ToHashSet();
        var deleted = ContentObjectEditor.Delete(item, groups[0].Select(found.KeyOf), found);
        var kept = Enumerable.Range(0, found.Count).Where(i => !gone.Contains(i)).ToList();
        CompareObjects(ContentObjects.Find(doc, deleted.Page, cancel.Token), [.. kept.Select(found.VisibleOf)], [.. kept.Select(i => unseen[i])],
            [.. kept.Select(i => kinds[i])], pageBox, $"{name} gelöscht", problems);

        CheckCrop(doc, item, found, groups, name, problems);
        return (moved, found.Count);
    }

    /// <summary>
    /// The first picture of upright images that are no stencils, cut to its left half: what stays lies in that half, and each new image
    /// has fewer pixels than its old one.
    /// </summary>
    private static void CheckCrop(PdfDocument doc, PageItem item, PageObjects found, List<IReadOnlyList<int>> groups, string name, List<string> problems)
    {
        // images that can be read, not too large to cut (those are refused with a notice)
        var picture = groups.FirstOrDefault(g => g.All(i => !found.IsDrawingAt(i) && found[i] is { AxisAligned: true, Source: { IsStencil: false } source } o &&
                                                            o.Visible.Width > 4 && o.Visible.Height > 4 && PdfImageDecoder.FullSize(source) <= ContentObjectEditor.MaxCropPixels &&
                                                            PdfImageDecoder.Decode(source, doc) is not null));
        if (picture is null) return;
        var box = picture.Select(found.VisibleOf).Aggregate((a, b) => a.Union(b));
        var keep = RectD.FromLtrb(box.Left, box.Top, (box.Left + box.Right) / 2, box.Bottom);
        (PageItem Item, IReadOnlyList<Core.Pdf.Objects.PdfStream> Images) cut;
        try
        {
            cut = ContentObjectEditor.Crop(item, [.. picture.Select(found.KeyOf)], keep, SkiaImageCodec.Instance, found);
        }
        catch (PdfException e)
        {
            problems.Add($"{name} zuschneiden: {e.Message}");
            return;
        }
        var after = ContentObjects.Find(doc, cut.Item.Page);
        foreach (var image in after.Images.Select(i => after[i]))
        {
            if (!cut.Images.Any(s => ReferenceEquals(s, image.Source?.Stream))) continue;
            var v = image.Visible;
            if (v.Left < keep.Left - Tolerance || v.Right > keep.Right + Tolerance || v.Top < keep.Top - Tolerance || v.Bottom > keep.Bottom + Tolerance)
                problems.Add($"{name} zuschneiden: Bild außerhalb des Rahmens ({v.Left:0.#}…{v.Right:0.#} statt bis {keep.Right:0.#})");
        }
        // the new images at most as large as the old ones at their full size (an image with a finer mask is cut at the mask's size)
        var before = picture.Sum(i => PdfImageDecoder.FullSize(found[i].Source!));
        var now = cut.Images.Sum(s => (long)(s.Dictionary.GetInteger(Core.Pdf.Objects.PdfName.Get("Width")) ?? 0) * (s.Dictionary.GetInteger(Core.Pdf.Objects.PdfName.Get("Height")) ?? 0));
        if (cut.Images.Count > 0 && now >= before) problems.Add($"{name} zuschneiden: nicht kleiner ({before} → {now} Pixel)");
    }

    /// <summary>The page box: what lies outside cannot be seen (the interpreter cuts the area of a shading there).</summary>
    private static RectD PageBox(PdfPage page) => RectD.FromLtrb(page.CropBox.Left, page.CropBox.Bottom, page.CropBox.Right, page.CropBox.Top);

    /// <summary>The objects found against what was expected: as many, in the same order, at the same place on the page.</summary>
    private static void CompareObjects(PageObjects found, IReadOnlyList<RectD> expected, IReadOnlyList<bool> unseen, IReadOnlyList<ContentKind> kinds,
        RectD pageBox, string name, List<string> problems, IReadOnlySet<int>? moved = null)
    {
        if (found.Count != expected.Count)
        {
            // a shading moved entirely out of the clips that stay around it is not seen any more, and not listed: it may be missing
            var shown = Enumerable.Range(0, expected.Count).Where(i => kinds[i] != ContentKind.Shading || unseen[i] || ContentTable.Valid(expected[i].Intersect(pageBox))).ToList();
            if (found.Count != shown.Count)
            {
                problems.Add($"{name}: {expected.Count} Objekte erwartet ({shown.Count} sichtbar), {found.Count} gefunden");
                return;
            }
            expected = [.. shown.Select(i => expected[i])];
            unseen = [.. shown.Select(i => unseen[i])];
            if (moved is not null) moved = shown.Select((i, k) => (i, k)).Where(e => moved.Contains(e.i)).Select(e => e.k).ToHashSet();
        }
        var wrong = 0;
        var first = -1;
        for (var i = 0; i < found.Count; i++)
        {
            var (a, b) = (found.VisibleOf(i).Intersect(pageBox), expected[i].Intersect(pageBox));
            // an object outside every clip or the page is not drawn: its place does not matter
            if (unseen[i] || !ContentTable.Valid(a) && !ContentTable.Valid(b)) continue;
            if (Math.Abs(a.Left - b.Left) > Tolerance || Math.Abs(a.Top - b.Top) > Tolerance || Math.Abs(a.Right - b.Right) > Tolerance ||
                Math.Abs(a.Bottom - b.Bottom) > Tolerance)
            {
                wrong++;
                if (first < 0) first = i;
            }
        }
        if (wrong > 0)
        {
            var (a, b) = (found.VisibleOf(first), expected[first]);
            var o = found[first];
            problems.Add($"{name}: {wrong} Objekte an falscher Stelle, z. B. Nr. {first} ({o.Kind}{(o.Block >= 0 ? ", Block" : "")}{(o.AlsoClips ? ", beschneidet" : "")}" +
                         $"{(o.Key.Scope.Length > 0 ? ", Formular" : "")}{(moved is null ? "" : moved.Contains(first) ? ", bewegt" : ", unbewegt")}) {Box(a)} statt {Box(b)}");
        }
    }

    private static string Box(RectD r) => $"[{r.Left:0.##} {r.Top:0.##} {r.Right:0.##} {r.Bottom:0.##}]";

    /// <summary>The moved pages saved and reopened: they look as in memory.</summary>
    private static void CheckSaved(PdfDocument doc, PageItem[] items, PageItem[] moved, int maxPages, string? keep, string file, string? password,
        List<string> problems)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(doc, moved, output, new PdfSaveOptions { ImageCodec = SkiaImageCodec.Instance, Version = SaveCheck.Version });
        if (keep is not null) File.WriteAllBytes(Path.Combine(keep, Path.GetFileName(file)), output.ToArray());
        using var copy = PdfDocument.Open(output.ToArray(), password);
        if (copy.PageCount != items.Length)
        {
            problems.Add($"gespeichert: Seiten {items.Length} → {copy.PageCount}");
            return;
        }
        for (var p = 0; p < Math.Min(items.Length, maxPages); p++)
        {
            if (ReferenceEquals(moved[p], items[p])) continue;
            using var a = Render(doc, moved[p].Page);
            using var b = Render(copy, copy.Pages[p]);
            if (a is not null && b is not null && SaveCheck.Differing(a, b) is var differing and > 0)
                problems.Add($"S. {p + 1} gespeichert: {differing} Pixel anders als im Speicher");
        }
    }

    private static SKBitmap? Render(PdfDocument doc, PdfPage page)
    {
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var list = PageInterpreter.Build(doc, page, cancel.Token);
            var scale = Math.Min(0.5, 1500 / Math.Max(page.Width, page.Height));
            return SkiaRenderer.RenderPage(list, doc, page, scale, new ImageCache());
        }
        catch (Exception)
        {
            return null;
        }
    }
}
