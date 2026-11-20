using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Edits of images and drawings in one content stream (see KONZEPT.md, section 5.20), by operator number there: the plan
/// <see cref="ContentObjectEditor"/> makes and the rewriter of the redaction carries out — one way of rewriting content for everything, with
/// forms copied for the page where something inside them changes.
/// </summary>
internal sealed class ContentEditPlan
{
    /// <summary>Matrices to apply in the space at the operator: after a "q" (a moved block), around a painting operator, Do, BI or sh.</summary>
    public Dictionary<int, Matrix> Transforms { get; } = [];

    /// <summary>
    /// The same moves in the default space of the content stream (the page's, or a form's space), by the same operator: a pattern lies in
    /// that space, not in the CTM — a moved area takes its pattern along in a copy with this matrix appended to the pattern's.
    /// </summary>
    public Dictionary<int, Matrix> PatternMoves { get; } = [];

    /// <summary>Painting operators of paths to remove with their path (a clip the path sets stays).</summary>
    public HashSet<int> RemovedPaths { get; } = [];

    /// <summary>Do, BI and sh to remove (an image's resource goes if nothing else invokes it).</summary>
    public HashSet<int> RemovedOperators { get; } = [];

    /// <summary>New images drawn before an operator: the image, its unit square in the space there, and an optional clip (corners in that space).</summary>
    public Dictionary<int, List<(PdfStream Image, Matrix Place, (double X, double Y)[]? Clip)>> Inserts { get; } = [];

    /// <summary>
    /// Clips set right before a painting operator, in q … Q around it (corners in the space there): a moved shading is cut to the area it
    /// showed (it fills its clip, so that area moves, not the rest of the clip); content reaching beyond the box of a form that grows keeps
    /// the old box.
    /// </summary>
    public Dictionary<int, List<(double X, double Y)[]>> ClipsBefore { get; } = [];

    /// <summary>Edits inside the form XObject invoked by a Do (its copy for this page).</summary>
    public Dictionary<int, ContentEditPlan> Forms { get; } = [];

    /// <summary>For the plan of a form: the area (form space) its copy's box must hold as well — where moved or new content now lies.</summary>
    public RectD? Grow { get; set; }

    /// <summary>
    /// Copying chosen objects (B7): only these painting operators, Do, BI and sh stay, and the forms with a plan of their own (they hold
    /// chosen ones); every other one and all text go. Null: everything stays.
    /// </summary>
    public HashSet<int>? Keep { get; set; }

    /// <summary>Whether the plan changes anything.</summary>
    public bool IsEmpty => Transforms.Count == 0 && RemovedPaths.Count == 0 && RemovedOperators.Count == 0 && Inserts.Count == 0 && ClipsBefore.Count == 0 &&
                           Keep is null && Forms.Values.All(f => f.IsEmpty);
}

public static partial class Redactor
{
    /// <summary>
    /// The page with <paramref name="plan"/> carried out by the rewriter: a copy of its dictionary with new content and resources (forms
    /// with edits copied for it).
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="plan">The edits of the page content.</param>
    /// <param name="copyUnchanged">Copy the operators the plan does not name as they are (otherwise everything is written anew).</param>
    /// <returns>The page dictionary, its resources, and the objects replaced or dropped (they stay in the file only if something else draws them).</returns>
    internal static (PdfDictionary Page, PdfDictionary Resources, IReadOnlySet<int> Replaced) ApplyContentEdits(PdfDocument doc, PdfPage page, ContentEditPlan plan, bool copyUnchanged = true)
    {
        var rewriter = new Rewriter(doc, [], page.MediaBox, AlternateTexts.None, copyUnchanged: copyUnchanged);
        var resources = CopyResources(page.Resources);
        byte[] body;
        using (var content = PageContent(doc, page.Dictionary)) body = rewriter.Rewrite(content.Span, ScopeOf(plan), resources);
        var dict = new PdfDictionary(page.Dictionary.Count) { Resolver = page.Dictionary.Resolver };
        foreach (var (key, value) in page.Dictionary.Entries)
            if (key != PdfName.Contents && key != PdfName.Resources) dict.Set(key, value);
        dict.Set(PdfName.Contents, new PdfStream(new PdfDictionary(), body));
        dict.Set(PdfName.Resources, resources);
        return (dict, resources, rewriter.ReplacedObjects);
    }

    private static Scope ScopeOf(ContentEditPlan plan)
    {
        var scope = new Scope();
        foreach (var (op, matrix) in plan.Transforms) scope.Transforms[op] = matrix;
        foreach (var (op, matrix) in plan.PatternMoves) scope.PatternMoves[op] = matrix;
        foreach (var op in plan.RemovedPaths) scope.Paths[op] = null;
        scope.Dropped.UnionWith(plan.RemovedOperators);
        foreach (var (op, list) in plan.Inserts) scope.Inserts[op] = list;
        foreach (var (op, corners) in plan.ClipsBefore) scope.ClipsBefore[op] = corners;
        foreach (var (op, form) in plan.Forms)
            if (!form.IsEmpty) scope.Forms[op] = ScopeOf(form);
        scope.GrowBox = plan.Grow;
        scope.Keep = plan.Keep;
        return scope;
    }

    /// <summary>
    /// The content of <paramref name="page"/> with only what <paramref name="plan"/> keeps (<see cref="ContentEditPlan.Keep"/>; its edits
    /// applied), made compact (<see cref="Compact"/>), and the resources it uses: the content of a form for copied objects (B7).
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page (the starting page of the edits).</param>
    /// <param name="plan">The edits and what to keep.</param>
    /// <returns>The content and its resources (a copy, without what it does not use).</returns>
    internal static (byte[] Content, PdfDictionary Resources) CopyContent(PdfDocument doc, PdfPage page, ContentEditPlan plan)
    {
        var rewriter = new Rewriter(doc, [], page.MediaBox, AlternateTexts.None);
        var resources = CopyResources(page.Resources);
        byte[] body;
        using (var content = PageContent(doc, page.Dictionary)) body = rewriter.Rewrite(content.Span, ScopeOf(plan), resources, pruneUnused: true);
        return (Compact(body), resources);
    }

    private static readonly HashSet<string> Painting = ["S", "s", "f", "F", "f*", "B", "B*", "b", "b*", "Do", "BI", "sh", "Tj", "TJ", "'", "\""];

    /// <summary>A q … Q being read by <see cref="Compact"/>: its operators (byte ranges), whether something in it paints.</summary>
    private sealed class Level
    {
        public readonly List<(int Start, int End)> Ranges = [];
        public bool Painted;
        public int Marks;      // marked content opened minus closed in it: not dropped unless even
        public int LastPaint;  // ranges up to here end with painting
    }

    /// <summary>
    /// Content without what paints nothing: a q … Q with nothing painted inside (only state, clips, empty text objects), and what follows the
    /// last painting. Copied out of a plan, the chosen objects keep the state operators of hundreds of thousands of others — megabytes.
    /// Marked content stays balanced: a q … Q that opens or closes more than it closes or opens stays.
    /// </summary>
    /// <param name="content">The content.</param>
    /// <returns>The compact content (the operators kept as they are written).</returns>
    internal static byte[] Compact(byte[] content)
    {
        var parser = new ContentParser(content);
        var levels = new List<Level> { new() };
        var end = 0;
        while (parser.Next() is { } op)
        {
            var begin = end;
            end = parser.Position;
            var level = levels[^1];
            if (op == "q" && levels.Count < 4096)
            {
                var inner = new Level();
                inner.Ranges.Add((begin, end));
                levels.Add(inner);
                continue;
            }
            if (op == "Q" && levels.Count == 1) continue;   // closes nothing of this content
            level.Ranges.Add((begin, end));
            if (op == "Q")
            {
                levels.RemoveAt(levels.Count - 1);
                Close(level, levels[^1]);
                continue;
            }
            if (op is "BDC" or "BMC") level.Marks++;
            else if (op == "EMC") level.Marks--;
            if (Painting.Contains(op))
            {
                level.Painted = true;
                level.LastPaint = level.Ranges.Count;
            }
        }
        // a q left open: as if closed at the end
        while (levels.Count > 1)
        {
            var open = levels[^1];
            levels.RemoveAt(levels.Count - 1);
            Close(open, levels[^1]);
        }
        var top = levels[0];
        var output = new MemoryStream(content.Length / 4 + 64);
        var last = -1;
        var opened = 0;   // marked content opened after the last painting (dropped with its end)
        for (var i = 0; i < top.Ranges.Count; i++)
        {
            var (start, stop) = top.Ranges[i];
            // after the last painting only what closes marked content opened before it
            if (i >= top.LastPaint)
            {
                if (Is(content, start, stop, "BMC"u8) || Is(content, start, stop, "BDC"u8)) opened++;
                if (!Is(content, start, stop, "EMC"u8)) continue;
                if (opened > 0)
                {
                    opened--;
                    continue;
                }
            }
            if (last >= 0 && start != last) output.WriteByte((byte)'\n');
            output.Write(content, start, stop - start);
            last = stop;
        }
        output.WriteByte((byte)'\n');
        return output.ToArray();

        static void Close(Level level, Level outer)
        {
            if (!level.Painted && level.Marks == 0) return;
            outer.Ranges.AddRange(level.Ranges);
            outer.Marks += level.Marks;
            outer.Painted = true;
            outer.LastPaint = outer.Ranges.Count;
        }

        static bool Is(byte[] content, int start, int stop, ReadOnlySpan<byte> op) =>
            content.AsSpan(start, stop - start).TrimEnd(" \t\r\n\f\0"u8).EndsWith(op);
    }
}
