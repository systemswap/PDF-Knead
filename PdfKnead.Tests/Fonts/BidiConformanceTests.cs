using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Fonts.Layout;

namespace PdfKnead.Tests.Fonts;

/// <summary>
/// UAX #9, 4.4 (Bidi Conformance Testing): the two conformance files of Unicode 16.0.0, shipped compressed with the tests (Unicode License
/// v3) — every line of BidiTest.txt (Bidi classes, each paragraph level of its bit set) and of BidiCharacterTest.txt (code points with bracket
/// pairs, paragraph direction given or from the text): the resolved paragraph level, the level of every character not removed by X9 after
/// L1 for one line, and the visual order after L2 without the removed characters (docs/FEHLERGEDAECHTNIS.md, No. 1173).
/// </summary>
public sealed class BidiConformanceTests
{
    /// <summary>A character of each Bidi class (BidiTest.txt gives classes, not characters; none of them is a bracket).</summary>
    private static readonly Dictionary<string, int> Representative = new(StringComparer.Ordinal)
    {
        ["L"] = 'a', ["R"] = 0x05D0, ["AL"] = 0x0627, ["EN"] = '1', ["ES"] = '+', ["ET"] = '$', ["AN"] = 0x0660, ["CS"] = ',', ["NSM"] = 0x0300,
        ["BN"] = 0x00AD, ["B"] = 0x2029, ["S"] = '\t', ["WS"] = ' ', ["ON"] = '!', ["LRE"] = 0x202A, ["LRO"] = 0x202D, ["RLE"] = 0x202B,
        ["RLO"] = 0x202E, ["PDF"] = 0x202C, ["LRI"] = 0x2066, ["RLI"] = 0x2067, ["FSI"] = 0x2068, ["PDI"] = 0x2069,
    };

    private static IEnumerable<string> Lines(string name)
    {
        using var stream = typeof(BidiConformanceTests).Assembly.GetManifestResourceStream("Bidi/" + name + ".gz")
                           ?? throw new FileNotFoundException(name);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        while (reader.ReadLine() is { } line) yield return line;
    }

    /// <summary>
    /// Compares one test case; null if it passes, else a description. <paramref name="levels"/> holds null for „x“ (removed by X9).
    /// </summary>
    private static string? Check(int[] points, byte? direction, int? paragraphLevel, int?[] levels, int[] order)
    {
        var text = new StringBuilder();
        var unit = new int[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            unit[i] = text.Length;
            text.Append(char.ConvertFromUtf32(points[i]));
        }
        var s = text.ToString();
        var analysis = Bidi.Analyze(s, direction);
        if (paragraphLevel is { } p && points.Length > 0 && analysis.ParagraphLevel(0) != p) return $"Absatzebene {analysis.ParagraphLevel(0)} statt {p}";
        var line = analysis.LineLevels(0, s.Length);
        var shown = new List<byte>();
        var index = new List<int>();
        for (var i = 0; i < points.Length; i++)
        {
            var removed = analysis.IsRemoved(unit[i]);
            if (levels[i] is null != removed) return $"Zeichen {i}: entfernt {removed}, erwartet {(levels[i] is null ? "x" : levels[i])}";
            if (removed) continue;
            if (line[unit[i]] != levels[i]) return $"Zeichen {i}: Ebene {line[unit[i]]} statt {levels[i]}";
            shown.Add(line[unit[i]]);
            index.Add(i);
        }
        var visual = Bidi.VisualOrder(shown).Select(k => index[k]).ToArray();
        return visual.SequenceEqual(order) ? null : $"Folge {string.Join(' ', visual)} statt {string.Join(' ', order)}";
    }

    /// <summary>The fields of the files are separated by spaces or tabs.</summary>
    private static readonly char[] Separators = [' ', '\t'];

    private static int?[] ParseLevels(string field) =>
        [.. field.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Select(t => t == "x" ? (int?)null : int.Parse(t, CultureInfo.InvariantCulture))];

    private static int[] ParseOrder(string field) =>
        [.. field.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Select(t => int.Parse(t, CultureInfo.InvariantCulture))];

    /// <summary>Every data line of BidiTest.txt 16.0.0 with every paragraph level of its bit set (auto, LTR, RTL).</summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void BidiTest_EveryLine()
    {
        var watch = Stopwatch.StartNew();
        int?[] levels = [];
        int[] order = [];
        int cases = 0, failed = 0;
        var report = new StringBuilder();
        foreach (var raw in Lines("BidiTest.txt"))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("@Levels:", StringComparison.Ordinal)) levels = ParseLevels(line[8..]);
            else if (line.StartsWith("@Reorder:", StringComparison.Ordinal)) order = ParseOrder(line[9..]);
            else if (line[0] != '@')
            {
                var parts = line.Split(';');
                var points = parts[0].Split(Separators, StringSplitOptions.RemoveEmptyEntries).Select(c => Representative[c]).ToArray();
                var bits = int.Parse(parts[1].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                foreach (var (bit, direction) in new[] { (1, (byte?)null), (2, (byte?)0), (4, (byte?)1) })
                {
                    if ((bits & bit) == 0) continue;
                    cases++;
                    if (Check(points, direction, null, levels, order) is not { } problem) continue;
                    if (++failed <= 20) report.Append(parts[0].Trim()).Append(" (").Append(bit).Append("): ").Append(problem).Append('\n');
                }
            }
        }
        Assert.True(cases > 400_000, $"{cases} F\u00E4lle");
        Assert.True(failed == 0, $"{failed} von {cases} F\u00E4llen falsch:\n{report}");
        Assert.True(watch.Elapsed.TotalSeconds < 6 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
    }

    /// <summary>Every line of BidiCharacterTest.txt 16.0.0 (bracket pairs, canonical equivalents, paragraph direction 0, 1 or from the text).</summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void BidiCharacterTest_EveryLine()
    {
        var watch = Stopwatch.StartNew();
        int cases = 0, failed = 0;
        var report = new StringBuilder();
        foreach (var raw in Lines("BidiCharacterTest.txt"))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var f = line.Split(';');
            if (f.Length < 5) continue;
            var points = f[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(c => int.Parse(c, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
            var direction = f[1].Trim() switch { "0" => (byte?)0, "1" => (byte?)1, _ => null };
            cases++;
            if (Check(points, direction, int.Parse(f[2].Trim(), CultureInfo.InvariantCulture), ParseLevels(f[3]), ParseOrder(f[4])) is not { } problem) continue;
            if (++failed <= 20) report.Append(f[0].Trim()).Append(" (").Append(f[1].Trim()).Append("): ").Append(problem).Append('\n');
        }
        Assert.True(cases > 90_000, $"{cases} F\u00E4lle");
        Assert.True(failed == 0, $"{failed} von {cases} F\u00E4llen falsch:\n{report}");
        Assert.True(watch.Elapsed.TotalSeconds < 3 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Security (UAX #9, BD2, BD16; linear time): a paragraph of 200 000 characters with deeply nested embeddings, isolates and brackets is
    /// resolved quickly, with no level above 126.
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void LongNestedParagraph_LinearAndBounded()
    {
        var sb = new StringBuilder();
        var random = new Random(1173);
        string[] pieces = ["\u202B", "\u202A", "\u202E", "\u202D", "\u202C", "\u2066", "\u2067", "\u2068", "\u2069", "(", ")", "[", "]", "a", "\u05D0", "1", " ", "\u0627"];
        while (sb.Length < 200_000) sb.Append(pieces[random.Next(pieces.Length)]);
        var watch = Stopwatch.StartNew();
        var levels = Bidi.Levels(sb.ToString());
        var order = Bidi.VisualOrder(levels);
        Assert.True(watch.Elapsed.TotalSeconds < 1 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
        Assert.All(levels, l => Assert.True(l <= Bidi.MaxDepth + 1));
        Assert.Equal(levels.Length, order.Distinct().Count());
    }
}
