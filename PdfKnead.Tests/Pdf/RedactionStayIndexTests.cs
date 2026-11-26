using System.Diagnostics;
using System.Reflection;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The search of the last check after redacting (Redactor.StayIndex; docs/FEHLERGEDAECHTNIS.md, No. 999, 1157): a page of many characters in
/// the very same place — a crafted page (fuzzing found 143 000 stacked characters) — is checked in bounded work.
/// </summary>
public sealed class RedactionStayIndexTests
{
    private const int Count = 30_000;

    private static (Func<Redactor.Seen, double, double, double, int> Find, Action<int> Take, List<Redactor.Seen> Seen) Index(bool sameShape)
    {
        var seen = Enumerable.Range(0, Count).Select(_ => new Redactor.Seen("a", 100, 100, new RectD(100, 100, 106, 110), 10, 0, true, false, Present: true)).ToList();
        var present = Enumerable.Range(0, Count).ToList();
        var type = typeof(Redactor).GetNestedType("StayIndex", BindingFlags.NonPublic)!;
        var index = Activator.CreateInstance(type, BindingFlags.NonPublic | BindingFlags.Instance, null,
            [seen, present, (Func<Redactor.Seen, (double, double)>)(g => (g.X, g.Y)), (Func<Redactor.Seen, int>)(_ => 0),
                (Func<Redactor.Seen, Redactor.Seen, bool>)((_, _) => sameShape), CancellationToken.None], null)!;
        var find = type.GetMethod("Find", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var take = type.GetMethod("Take", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((g, u, v, along) => (int)find.Invoke(index, [g, u, v, along])!, i => take.Invoke(index, [i]), seen);
    }

    /// <summary>Every one of 30 000 characters in one place finds its counterpart, in linear work (before: each search tried all the rest).</summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void StackedCharacters_AllFound_InBoundedTime()
    {
        var (find, take, seen) = Index(sameShape: true);
        var watch = Stopwatch.StartNew();
        foreach (var g in seen)
        {
            var best = find(g, g.X, g.Y, 0.6);
            Assert.True(best >= 0);
            take(best);
        }
        Assert.True(watch.ElapsedMilliseconds < 2000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
    }

    /// <summary>When no candidate has the same shape, the work is bounded by a budget: the searches end with „not found“ (the check fails and nothing is saved).</summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void StackedCharactersOfAnotherShape_SearchesEndWithinTheBudget()
    {
        var (find, _, seen) = Index(sameShape: false);
        var watch = Stopwatch.StartNew();
        foreach (var g in seen) Assert.Equal(-1, find(g, g.X, g.Y, 0.6));
        Assert.True(watch.ElapsedMilliseconds < 4000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
    }
}
