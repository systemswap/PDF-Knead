using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Fonts.Layout;

namespace PdfKnead.Tests.Fonts;

/// <summary>
/// UAX #14, section 10 (Testing): the conformance file LineBreakTest.txt of Unicode 16.0.0, shipped compressed with the tests (Unicode License
/// v3) — for every line the break opportunities of <see cref="LineBreaking"/> at every position between its code points must be those of the
/// file (÷ an opportunity or the end, × none). The algorithm runs without tailoring; the layouts of formatted and plain text take their
/// opportunities from it (docs/FEHLERGEDAECHTNIS.md, No. 1196).
/// </summary>
public sealed class LineBreakConformanceTests
{
    private static IEnumerable<string> Lines()
    {
        using var stream = typeof(LineBreakConformanceTests).Assembly.GetManifestResourceStream("Bidi/LineBreakTest.txt.gz")
                           ?? throw new FileNotFoundException("LineBreakTest.txt.gz");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        while (reader.ReadLine() is { } line) yield return line;
    }

    /// <summary>
    /// UAX #14 sections 4.1 (UAX14-C1), 6, 6.1, 6.2 and 10: every data line of LineBreakTest.txt 16.0.0 — 16 672 cases with the
    /// representative characters of all classes and longer sequences — gives the same break opportunities, without exception.
    /// </summary>
    [Fact]
    public void LineBreakTest_EveryLine()
    {
        var failures = new List<string>();
        var count = 0;
        foreach (var raw in Lines())
        {
            var body = raw.Split('#')[0].Trim();
            if (body.Length == 0) continue;
            count++;
            var tokens = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var text = new StringBuilder();
            var expected = new List<(int Position, bool Break)>();
            foreach (var token in tokens)
            {
                if (token is "÷" or "×") expected.Add((text.Length, token == "÷"));
                else text.Append(char.ConvertFromUtf32(int.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            }
            var breaks = LineBreaking.Analyze(text.ToString());
            foreach (var (position, isBreak) in expected)
                if (breaks.Allowed(position) != isBreak)
                {
                    failures.Add($"{body}  (Stelle {position}: {(isBreak ? "÷" : "×")} erwartet)");
                    break;
                }
        }
        Assert.Equal(16672, count);
        Assert.True(failures.Count == 0, $"{failures.Count} Zeilen weichen ab:\n" + string.Join('\n', failures.Take(40)));
    }
}
