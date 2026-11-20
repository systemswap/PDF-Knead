using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using SkiaSharp;

namespace PdfKnead.Rendering;

/// <summary>Why text in a file only looks redacted.</summary>
public enum FakeRedactionKind
{
    /// <summary>Text lies under a dark area: nobody sees it, but it is still in the file and can be copied or extracted.</summary>
    Covered,

    /// <summary>A redaction mark (Redact annotation, ISO 32000-2:2020, 12.5.6.23) of another program that was never applied: the text under it is still there.</summary>
    UnappliedMark,
}

/// <summary>A place where text only looks redacted.</summary>
/// <param name="Kind">Why.</param>
/// <param name="Areas">The areas (page space): the lines of the covered text, or the areas of the mark.</param>
/// <param name="Text">The text that can still be read out there.</param>
public sealed record FakeRedactionSpot(FakeRedactionKind Kind, IReadOnlyList<PdfRectangle> Areas, string Text);

/// <summary>
/// Finds redactions that are none (KONZEPT.md, section 5.6, „Vorgetäuschte Schwärzung“): text that nobody sees because a dark area lies over
/// it — measured by drawing, as for „Verborgenes schwärzen“ (<see cref="HiddenText"/>), and then whether the spot on the drawn page is dark —,
/// and redaction marks of other programs that were never applied. Both look like a redaction, but the text is still in the file: copying,
/// searching or any text extraction reveals it (the redaction failures in published documents, see docs/RECHERCHE-VERSTECKEN.md).
/// Nothing is changed; the caller offers to redact properly.
/// </summary>
public static class FakeRedactions
{
    /// <summary>Resolution of the drawing that decides „dark“ (2 pixels per point).</summary>
    private const double PixelsPerPoint = 2;

    /// <summary>A pixel counts as dark below this luminance (0–255); black, dark grey, dark blue bars all are.</summary>
    private const int DarkLevel = 90;

    /// <summary>Share of dark pixels a covered line needs to look blacked out.</summary>
    public const double DarkShare = 0.8;

    /// <summary>At most this many pixels per drawn line area (larger areas are drawn coarser).</summary>
    private const long MaxPixels = 4_000_000;

    /// <summary>The places on a page where text only looks redacted.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="images">Image cache for drawing.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>The places, covered text first, then marks.</returns>
    public static List<FakeRedactionSpot> Find(PdfDocument doc, PdfPage page, ImageCache images, CancellationToken cancel = default)
    {
        var found = new List<FakeRedactionSpot>();
        DisplayList? list = null;
        TextGlyph[]? glyphs = null;
        var suspicious = HiddenText.Scan(doc, page, cancel, VisibilityProbe.Instance).Spots
            .Where(s => s.Kind == HiddenTextKind.Suspicious && !s.OutsidePage && s.Text.Any(char.IsLetterOrDigit)).ToList();
        foreach (var spot in suspicious)
        {
            cancel.ThrowIfCancellationRequested();
            list ??= PageInterpreter.Build(doc, page, cancel);
            if (spot.Lines.Count > 0 && spot.Lines.All(line => Dark(list, doc, page, line, images, cancel)))
                found.Add(new FakeRedactionSpot(FakeRedactionKind.Covered, spot.Lines, spot.Text));
        }
        // Dark annotations of other programs (a filled rectangle as a comment): the measurement above leaves them out, because the program
        // edits them as elements of their own — the text of the page under them is checked here
        foreach (var (annot, area) in Annotations(page).Where(a => a.Annot.GetName(PdfName.Subtype)?.Value is not ("Redact" or "Popup" or "Link" or "Widget")))
        {
            cancel.ThrowIfCancellationRequested();
            glyphs ??= PageInterpreter.BuildText(doc, page, cancel, withoutOwnElements: true);
            var under = glyphs.Where(g => g.Visible && Inside(area, g)).ToList();
            if (!under.Any(g => g.Text.Any(char.IsLetterOrDigit))) continue;
            var covered = Bounds(under);
            if (found.Any(f => f.Areas.Any(a => Overlap(a, covered)))) continue;
            list ??= PageInterpreter.Build(doc, page, cancel);
            if (Dark(list, doc, page, covered, images, cancel))
                found.Add(new FakeRedactionSpot(FakeRedactionKind.Covered, [covered], string.Concat(under.Select(g => g.Text)).Trim()));
        }
        found.AddRange(UnappliedMarks(doc, page, cancel, glyphs));
        return found;
    }

    /// <summary>Whether the drawn page is dark over the inner part of an area (the edges of a bar may not quite reach the line's ends).</summary>
    private static bool Dark(DisplayList list, PdfDocument doc, PdfPage page, PdfRectangle area, ImageCache images, CancellationToken cancel)
    {
        var scale = PixelsPerPoint;
        var toDevice = PageGeometry.PageToDevice(page, scale);
        var box = toDevice.TransformBounds(RectD.FromLtrb(area.Left, area.Bottom, area.Right, area.Top));
        var pixels = box.Width * box.Height;
        if (pixels > MaxPixels)
        {
            scale *= Math.Sqrt(MaxPixels / pixels);
            toDevice = PageGeometry.PageToDevice(page, scale);
            box = toDevice.TransformBounds(RectD.FromLtrb(area.Left, area.Bottom, area.Right, area.Top));
        }
        // the inner 80 % of the line in both directions
        var insetX = box.Width * 0.1;
        var insetY = box.Height * 0.1;
        var tile = new SKRectI((int)Math.Floor(box.Left + insetX), (int)Math.Floor(box.Top + insetY), (int)Math.Ceiling(box.Right - insetX),
            (int)Math.Ceiling(box.Bottom - insetY));
        if (tile.Width <= 0 || tile.Height <= 0) return false;
        using var bitmap = new SKBitmap(new SKImageInfo(tile.Width, tile.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SkiaRenderer.Paper);
            SkiaRenderer.RenderTile(canvas, list, doc, toDevice, tile, images, cancel);
        }
        var data = bitmap.GetPixelSpan();
        long dark = 0, all = 0;
        for (var i = 0; i + 3 < data.Length; i += 4)
        {
            all++;
            if (data[i] * 299 + data[i + 1] * 587 + data[i + 2] * 114 < DarkLevel * 1000) dark++;
        }
        return all > 0 && dark >= all * DarkShare;
    }

    /// <summary>The annotations of other programs on a page (not PDF-Knead's own) with their area; at most 10 000.</summary>
    private static IEnumerable<(PdfDictionary Annot, PdfRectangle Area)> Annotations(PdfPage page)
    {
        if (page.Dictionary.GetArray(PdfName.Get("Annots")) is not { } annots) yield break;
        for (var i = 0; i < Math.Min(annots.Count, 10_000); i++)
            if (annots.Get(i) is PdfDictionary annot && !ElementStore.HasData(annot) && annot.Get(PdfName.Get("Rect")) is { } rect
                && PdfRectangle.From(rect) is { } area)
                yield return (annot, area);
    }

    private static PdfRectangle Bounds(List<TextGlyph> glyphs) => new(
        glyphs.Min(g => Math.Min(Math.Min(g.X0, g.X1), Math.Min(g.X2, g.X3))), glyphs.Min(g => Math.Min(Math.Min(g.Y0, g.Y1), Math.Min(g.Y2, g.Y3))),
        glyphs.Max(g => Math.Max(Math.Max(g.X0, g.X1), Math.Max(g.X2, g.X3))), glyphs.Max(g => Math.Max(Math.Max(g.Y0, g.Y1), Math.Max(g.Y2, g.Y3))));

    private static bool Overlap(PdfRectangle a, PdfRectangle b) => a.Left < b.Right && b.Left < a.Right && a.Bottom < b.Top && b.Bottom < a.Top;

    /// <summary>Redact annotations of other programs (not PDF-Knead's own marks, which are applied when saving) with text under them.</summary>
    private static IEnumerable<FakeRedactionSpot> UnappliedMarks(PdfDocument doc, PdfPage page, CancellationToken cancel, TextGlyph[]? glyphs)
    {
        foreach (var (annot, _) in Annotations(page))
        {
            if (annot.GetName(PdfName.Subtype)?.Value != "Redact") continue;
            var areas = Areas(annot);
            if (areas.Count == 0) continue;
            glyphs ??= PageInterpreter.BuildText(doc, page, cancel, withoutOwnElements: true);
            var under = string.Concat(glyphs.Where(g => areas.Any(a => Inside(a, g))).Select(g => g.Text)).Trim();
            if (under.Any(char.IsLetterOrDigit)) yield return new FakeRedactionSpot(FakeRedactionKind.UnappliedMark, areas, under);
        }
    }

    /// <summary>The areas of a mark: its QuadPoints (groups of four corners), otherwise its /Rect.</summary>
    private static List<PdfRectangle> Areas(PdfDictionary annot)
    {
        var areas = new List<PdfRectangle>();
        if (annot.GetArray(PdfName.Get("QuadPoints")) is { Count: >= 8 } quads)
        {
            for (var q = 0; q + 7 < Math.Min(quads.Count, 8 * 1000); q += 8)
            {
                var values = Enumerable.Range(q, 8).Select(quads.GetNumber).ToArray();
                if (values.Any(v => v is not { } n || !double.IsFinite(n))) continue;
                var xs = new[] { values[0]!.Value, values[2]!.Value, values[4]!.Value, values[6]!.Value };
                var ys = new[] { values[1]!.Value, values[3]!.Value, values[5]!.Value, values[7]!.Value };
                areas.Add(new PdfRectangle(xs.Min(), ys.Min(), xs.Max(), ys.Max()));
            }
        }
        if (areas.Count == 0 && annot.Get(PdfName.Get("Rect")) is { } rect && PdfRectangle.From(rect) is { } r) areas.Add(r);
        return areas;
    }

    /// <summary>Whether the middle of a character lies in an area.</summary>
    private static bool Inside(PdfRectangle area, TextGlyph glyph)
    {
        var x = (glyph.X0 + glyph.X1 + glyph.X2 + glyph.X3) / 4;
        var y = (glyph.Y0 + glyph.Y1 + glyph.Y2 + glyph.Y3) / 4;
        return x >= area.Left && x <= area.Right && y >= area.Bottom && y <= area.Top;
    }
}
