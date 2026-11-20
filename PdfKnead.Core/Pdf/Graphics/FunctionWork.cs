using PdfKnead.Core.Localization;

namespace PdfKnead.Core.Pdf.Graphics;

/// <summary>
/// The work of costly function evaluations (cubic splines, many inputs) within one drawn object — the colour conversion of an image — bounded
/// by <see cref="ObjectBudget"/> samples: beyond it the object is not shown, with a notice. Per object, not per function: a function used
/// on several pages or by several images is computed the same way each time, whatever was drawn before (decided on 17.11.2026, /Order 3
/// always cubic; before, one budget per function made the spline go linear once spent; docs/FEHLERGEDAECHTNIS.md, No. 785, 908). Outside a
/// scope (a single fill colour, a shading of one or two inputs) nothing is counted: one evaluation takes at most the samples of its table.
/// </summary>
internal static class FunctionWork
{
    /// <summary>Samples one drawn object may take in costly evaluations (a few seconds).</summary>
    internal const long ObjectBudget = 1_000_000_000;

    internal sealed class Counter(long limit, PdfKnead.Core.Pdf.Document.PdfDocument doc)
    {
        public long Used;
        public readonly long Limit = limit;
        public readonly PdfKnead.Core.Pdf.Document.PdfDocument Doc = doc;
    }

    // flows into the tasks a scope starts (Parallel.For over the rows of an image)
    private static readonly AsyncLocal<Counter?> Current = new();

    /// <summary>Starts counting for one object of <paramref name="doc"/> (the notice goes there); disposing ends it.</summary>
    internal static Scope Begin(PdfKnead.Core.Pdf.Document.PdfDocument doc, long limit = ObjectBudget)
    {
        var previous = Current.Value;
        Current.Value = new Counter(limit, doc);
        return new Scope(previous);
    }

    /// <summary>Counts <paramref name="samples"/>; beyond the bound of the scope the object cannot be computed.</summary>
    /// <exception cref="PdfLimitException">The bound of the scope is passed.</exception>
    internal static void Charge(long samples)
    {
        if (Current.Value is not { } counter || Interlocked.Add(ref counter.Used, samples) <= counter.Limit) return;
        var message = Tr.T("Ein Bild ist zu aufwendig zu berechnen und wird nicht gezeigt.");
        // said once per object (the rows of an image computed in parallel pass the bound one after another)
        if (Interlocked.Exchange(ref counter.Used, long.MaxValue / 2) < long.MaxValue / 2) counter.Doc.AddProblem(message);
        throw new PdfLimitException(message);
    }

    /// <summary>Ends a scope.</summary>
    internal readonly struct Scope(Counter? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
