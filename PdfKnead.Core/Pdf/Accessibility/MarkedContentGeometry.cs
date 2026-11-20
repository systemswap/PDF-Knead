using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Accessibility;

/// <summary>
/// Where the marked content of a page lies (docs/FEHLERGEDAECHTNIS.md, No. 633): per MCID of the page's own content the bounds of what it
/// paints (characters, paths, images, shadings — forms drawn inside it count for it; the MCIDs of a form are its own and not counted), and
/// the MCID at a point. A new annotation finds the content it sits on this way and goes into the structure next to it (ISO 14289-2,
/// 8.9.3.2); shared with the redactor's structure work.
/// </summary>
internal sealed class MarkedContentGeometry : IContentObserver
{
    private static readonly PdfName Mcid = PdfName.Get("MCID"), Artifact = PdfName.Get("Artifact");

    private readonly Dictionary<int, RectD> _bounds = [];
    private readonly List<RectD> _artifacts = [];
    private readonly List<(int? Mcid, int Depth, bool Artifact)> _marks = [];
    private readonly Stack<int> _formStarts = new();
    private int _formDepth;

    /// <summary>The bounds in page space per MCID the page's content marks (within the work limit of the interpreter).</summary>
    public static IReadOnlyDictionary<int, RectD> Of(PdfDocument doc, PdfPage page, CancellationToken cancel = default)
    {
        var geometry = new MarkedContentGeometry();
        try
        {
            PageInterpreter.Observe(doc, page, geometry, cancel);
        }
        catch (PdfException)
        {
            // what was seen until the limit or the damage
        }
        return geometry._bounds;
    }

    /// <summary>
    /// The bounds per MCID as <see cref="Of"/>, and the bounds of what is painted as an artifact (inside /Artifact marked content, also in forms;
    /// the help „Artefakte hervorheben“ of the check „barrierefrei?“, docs/ABLAUFPLAN.md, 6.7 step 5, M6).
    /// </summary>
    public static (IReadOnlyDictionary<int, RectD> Bounds, IReadOnlyList<RectD> Artifacts) Measure(PdfDocument doc, PdfPage page, CancellationToken cancel = default)
    {
        var geometry = new MarkedContentGeometry();
        try
        {
            PageInterpreter.Observe(doc, page, geometry, cancel);
        }
        catch (PdfException)
        {
            // what was seen until the limit or the damage
        }
        return (geometry._bounds, geometry._artifacts);
    }

    /// <summary>
    /// The MCID at a point: the smallest bounds holding it, else the nearest within <paramref name="reach"/> points; null if none is that near.
    /// </summary>
    public static int? At(IReadOnlyDictionary<int, RectD> bounds, double x, double y, double reach = 36)
    {
        int? best = null;
        var bestArea = double.MaxValue;
        foreach (var (mcid, r) in bounds)
            if (x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom && (r.Right - r.Left) * (r.Bottom - r.Top) < bestArea)
                (best, bestArea) = (mcid, (r.Right - r.Left) * (r.Bottom - r.Top));
        if (best is not null) return best;
        var bestDistance = reach;
        foreach (var (mcid, r) in bounds)
        {
            var dx = Math.Max(Math.Max(r.Left - x, 0), x - r.Right);
            var dy = Math.Max(Math.Max(r.Top - y, 0), y - r.Bottom);
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance <= bestDistance) (best, bestDistance) = (mcid, distance);
        }
        return best;
    }

    /// <summary>The MCID of the page's own content open now (the innermost), null if none.</summary>
    private int? Current
    {
        get
        {
            for (var i = _marks.Count - 1; i >= 0; i--)
                if (_marks[i] is { Depth: 0, Mcid: { } mcid }) return mcid;
            return null;
        }
    }

    private void Paint(RectD bounds)
    {
        if (bounds.IsEmpty || !double.IsFinite(bounds.Left + bounds.Right + bounds.Top + bounds.Bottom)) return;
        if (_marks.Any(m => m.Artifact))
        {
            // neighbouring pieces of one artifact (a line of a footer) as one area, within a budget
            if (_artifacts.Count > 0 && _artifacts[^1].Inflate(2).Intersects(bounds)) _artifacts[^1] = _artifacts[^1].Union(bounds);
            else if (_artifacts.Count < 20_000) _artifacts.Add(bounds);
            return;
        }
        if (Current is not { } mcid) return;
        _bounds[mcid] = _bounds.TryGetValue(mcid, out var known) ? known.Union(bounds) : bounds;
    }

    public void MarkedContent(int op, PdfName tag, PdfDictionary? properties) =>
        _marks.Add((properties?.Get(Mcid) is PdfInteger { Value: >= 0 and <= int.MaxValue } id ? (int)id.Value : null, _formDepth, tag == Artifact));

    public void EndMarkedContent(int op)
    {
        // an EMC closes only what its own stream opened
        if (_marks.Count > (_formStarts.Count > 0 ? _formStarts.Peek() : 0)) _marks.RemoveAt(_marks.Count - 1);
    }

    public void Glyph(int op, int element, int start, int length, RectD cell, RectD ink, Matrix cellToPage, double advance, double fontSize,
        double horizontalScale, bool vertical, Matrix textToPage, string? text, bool hidden, bool invisible) => Paint(cellToPage.TransformBounds(cell));

    public void Path(int op, RectD bounds, Matrix ctm, bool hidden) => Paint(bounds);

    public void Image(int op, Matrix imageToPage, PdfStream? stream, InlineImage? inline, PdfDictionary? resources, bool hidden) =>
        Paint(imageToPage.TransformBounds(RectD.FromLtrb(0, 0, 1, 1)));

    public void Shading(int op, RectD bounds, Matrix ctm, bool hidden) => Paint(bounds);

    public void BeginForm(int op, PdfStream form, Matrix formToPage, bool hidden) => Enter();

    public void BeginSoftMask(int op, PdfStream group, Matrix groupToPage) => Enter();

    public void BeginPatternCell(int op, bool stroke, PdfStream pattern, Matrix cellToPage) => Enter();

    public void EndForm()
    {
        if (_formStarts.Count == 0) return;
        var start = _formStarts.Pop();
        if (_marks.Count > start) _marks.RemoveRange(start, _marks.Count - start);   // what the form left open ends with it
        _formDepth--;
    }

    private void Enter()
    {
        _formStarts.Push(_marks.Count);
        _formDepth++;
    }
}
