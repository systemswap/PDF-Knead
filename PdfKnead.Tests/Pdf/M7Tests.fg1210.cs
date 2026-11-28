using System.Diagnostics;
using System.Globalization;
using PdfKnead.Core.Pdf.Editing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, FG 1210 (security): the caret stops of the editor (extended grapheme clusters, UAX #29) are found once per state of the text, so that an
/// arrow key or a click costs at most a pass over the text — also in a grapheme cluster of hundreds of thousands of UTF-16 units from a file
/// (an emoji ZWJ sequence, a run of Hangul vowel jamo, a letter with joiners); regional indicators are paired from the start of the text at any
/// length.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>A text of about 240 000 UTF-16 units with one long grapheme cluster between „ab “ and „ cd“: (text, start, end of the cluster).</summary>
    private static (string Text, int Start, int End) Fg1210Cluster(string cluster) => ("ab " + cluster + " cd", 3, 3 + cluster.Length);

    private static string Fg1210Cp(int cp) => char.ConvertFromUtf32(cp);

    /// <summary>The long clusters: man ZWJ man … (an emoji ZWJ sequence), choseong kiyeok with jungseong a … (Hangul jamo), a with ZWNJ … (Extend).</summary>
    private static IEnumerable<(string Name, string Cluster)> Fg1210Clusters()
    {
        yield return ("emoji ZWJ", string.Concat(Enumerable.Repeat(Fg1210Cp(0x1F468) + Fg1210Cp(0x200D), 80_000)) + Fg1210Cp(0x1F468));
        yield return ("jamo", Fg1210Cp(0x1100) + new string((char)0x1161, 240_000));
        yield return ("ZWNJ", "a" + new string((char)0x200C, 240_000));
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a thread of its own and fails if it takes longer than <paramref name="limitMs"/> (times the factor of
    /// the time limits). Not through the thread pool: in the full run the other tests keep the pool busy, and a task could wait seconds before it
    /// starts — the limit then measured the wait, not the work (the full run of 27.11.2026: „not done after 4004 ms“, alone 12 s for all five).
    /// </summary>
    private static void Fg1210Within(string what, double limitMs, Action action)
    {
        var limit = TimeSpan.FromMilliseconds(limitMs * TestKind.Factor);
        Exception? failure = null;
        var started = new ManualResetEventSlim();
        var watch = new Stopwatch();
        var thread = new Thread(() =>
        {
            watch.Start();
            started.Set();
            try { action(); }
            catch (Exception e) { failure = e; }
        }) { IsBackground = true };
        thread.Start();
        started.Wait();
        Assert.True(thread.Join(limit), $"{what}: not done after {watch.ElapsedMilliseconds} ms");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>
    /// The arrow keys over a grapheme cluster of about 240 000 units (an emoji ZWJ sequence, Hangul jamo of one syllable, a letter with ZWNJ):
    /// from its start, from its middle and from its end the caret goes over the whole cluster, each step within a fixed limit (after the first,
    /// which finds the stops of the text). Before, every stop looked for the end of the cluster again: an arrow key took about a second at
    /// 24 000 units and grew with the square of the length.
    /// </summary>
    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void Fg1210_EditUnits_LongCluster_ArrowKeysBounded()
    {
        foreach (var (name, cluster) in Fg1210Clusters())
        {
            var (text, start, end) = Fg1210Cluster(cluster);
            Fg1210Within(name + " first step", 400, () => Assert.Equal(end, EditUnits.Next(text, start)));
            Fg1210Within(name + " steps", 100, () =>
            {
                Assert.Equal(end, EditUnits.Next(text, start));
                Assert.Equal(end, EditUnits.Next(text, start + cluster.Length / 2));
                Assert.Equal(start, EditUnits.Previous(text, end));
                Assert.Equal(start, EditUnits.Previous(text, start + cluster.Length / 2));
                Assert.Equal(end + 1, EditUnits.Next(text, end));
                Assert.Equal(start - 1, EditUnits.Previous(text, start));
                Assert.False(EditUnits.IsStop(text, start + cluster.Length / 2));
            });
        }
    }

    /// <summary>
    /// A click into a line with a grapheme cluster of about 240 000 units (and arrow up/down, which use the same search): the insertion point
    /// is found within a fixed limit and lies before or after the cluster. Before, every candidate place of the line looked for the end of the
    /// cluster again.
    /// </summary>
    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void Fg1210_Click_LongCluster_Bounded()
    {
        foreach (var (name, cluster) in Fg1210Clusters())
        {
            var (text, start, end) = Fg1210Cluster(cluster);
            var layout = E20Layout(text, 100_000_000);
            var line = Assert.Single(layout.Lines);
            Fg1210Within(name + " click", 200, () =>
            {
                var middle = (RichLayout.XAt(line, start) + RichLayout.XAt(line, end)) / 2;
                Assert.Contains(layout.CaretInLine(line, middle), new[] { start, end });
                Assert.Equal(0, layout.CaretInLine(line, -5));
            });
        }
    }

    /// <summary>
    /// A click into a line of 200 000 letters (every place a stop): the insertion point nearest to the click is found within a fixed limit — the
    /// glyph of each place is looked for from that of the place before. Before, from the start of the line for every place (quadratic).
    /// </summary>
    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void Fg1210_Click_LongLine_Bounded()
    {
        var text = new string('i', 200_000);
        var layout = E20Layout(text, 10_000_000);
        var line = Assert.Single(layout.Lines);
        var target = RichLayout.XAt(line, 150_000);
        Fg1210Within("click", 500, () =>
        {
            Assert.Equal(150_000, layout.CaretInLine(line, target));
            Assert.Equal(200_000, layout.CaretInLine(line, line.X + line.Width + 50));
        });
    }

    /// <summary>
    /// Regional indicators are paired from the start of the text, also deep inside a run of 4 000 of them after a letter (16 000 units, beyond
    /// the 1 024 units a boundary was looked for before): the stops lie after every second indicator. Before, beyond the first 1 024 units
    /// every indicator was a stop — the pairs were counted from 1 024 units back.
    /// </summary>
    [Fact]
    public void Fg1210_EditUnits_RegionalIndicators_PairedAtAnyLength()
    {
        var text = "a" + string.Concat(Enumerable.Repeat(Fg1210Cp(0x1F1E9) + Fg1210Cp(0x1F1EA), 2_000));
        foreach (var pair in new[] { 0, 1, 500, 1_000, 1_999 })
        {
            var at = 1 + 4 * pair;
            Assert.Equal(at + 4, EditUnits.Next(text, at));
            Assert.False(EditUnits.IsStop(text, at + 2));
            if (pair > 0) Assert.Equal(at - 4, EditUnits.Previous(text, at));
        }
    }

    /// <summary>
    /// The stops of mixed texts (letters, combining marks, viramas and joiners, emoji with ZWJ and modifiers, regional indicators, Hangul jamo,
    /// CR LF, unpaired surrogates; 2 000 random texts): every stop is a boundary of the grapheme clusters the runtime counts from the start of the
    /// text, and the arrow keys go to the nearest stop on either side, as a search place by place finds it.
    /// </summary>
    [Fact]
    public void Fg1210_EditUnits_StopsAsCountedFromTheStart()
    {
        int[] pool =
        [
            'a', 'b', ' ', '1', 0x0301, 0x0308, 0x0915, 0x094D, 0x093F, 0x200D, 0x200C, 0x1F468, 0x1F469, 0x1F3FB, 0x1F1E9, 0x1F1EA,
            0x1100, 0x1161, 0x11A8, 0xAC00, 0x0E01, 0x0E31, 0x1780, 0x17D2, '\r', '\n', 0x0600, 0xD800, 0xDC00, 0x4E00,
        ];
        var random = new Random(1210);
        for (var k = 0; k < 2_000; k++)
        {
            var parts = new System.Text.StringBuilder();
            var length = random.Next(1, 24);
            for (var j = 0; j < length; j++)
            {
                var cp = pool[random.Next(pool.Length)];
                parts.Append(cp is >= 0xD800 and <= 0xDFFF ? ((char)cp).ToString() : Fg1210Cp(cp));
            }
            var text = parts.ToString();
            var boundaries = new HashSet<int> { 0, text.Length };
            for (var p = 0; p < text.Length;)
            {
                boundaries.Add(p);
                p += Math.Max(1, StringInfo.GetNextTextElementLength(text.AsSpan(p)));
            }
            var stops = Enumerable.Range(0, text.Length + 1).Where(i => EditUnits.IsStop(text, i)).ToList();
            Assert.All(stops, s => Assert.Contains(s, boundaries));
            for (var i = 0; i <= text.Length; i++)
            {
                Assert.Equal(i >= text.Length ? text.Length : stops.First(s => s > i), EditUnits.Next(text, i));
                Assert.Equal(i <= 0 ? 0 : stops.Last(s => s < i), EditUnits.Previous(text, i));
            }
        }
    }
}
