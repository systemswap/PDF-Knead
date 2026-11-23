using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf.Filters.Jpeg;
using W = PdfKnead.Tests.Pdf.TestJpegWriter;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The JPEG decoder against data printed in ITU-T T.81 itself: the test sequence of the arithmetic coder (K.4.1) and the typical Huffman
/// tables (K.3.3). The data is read from the local norm text at run time, so that no norm data comes into Git; without the text the tests
/// pass. The text: where tools/normabgleich/zaehlen.py text t81 puts it (<see cref="T81Text"/>).
/// </summary>
public sealed class JpegNormTextTests
{
    /// <summary>The test sequence of T.81, K.4.1: from the coded bytes the binary decoder gets back the 256 decisions (one statistics bin from its initial state).</summary>
    [Fact]
    public void TestSequenceOfK4_Decoder()
    {
        if (K4() is not { } k4) return;
        Assert.Equal(k4.Decisions, JpegArithmetic.DecisionsOfOneBin(k4.Coded, k4.Decisions.Length));
    }

    /// <summary>The test writer codes the same decisions into the same bytes (with the end marker X'FFD9' the sequence of K.4.1 ends in).</summary>
    [Fact]
    public void TestSequenceOfK4_TestEncoder()
    {
        if (K4() is not { } k4) return;
        var coder = new W.Coder();
        var stats = new byte[1];
        foreach (var d in k4.Decisions) coder.Code(stats, 0, d);
        byte[] written = [.. coder.Flush(), 0xFF, 0xD9];
        // the text fills the last group of its hexadecimal sequence with a zero after the marker
        var end = k4.Coded.AsSpan().LastIndexOf([(byte)0xFF, (byte)0xD9]) + 2;
        Assert.All(k4.Coded[end..], b => Assert.Equal(0, b));
        Assert.Equal(Convert.ToHexString(k4.Coded[..end]), Convert.ToHexString(written));
    }

    /// <summary>
    /// The typical Huffman tables of the decoder (used where a DHT segment is missing, S-A A102) and of the test writer are those of T.81,
    /// K.3.3.1 and K.3.3.2: counts and symbols of Tables K.3–K.6.
    /// </summary>
    [Fact]
    public void TypicalHuffmanTables_SameAsTheNormText()
    {
        if (T81Text() is not { } text) return;
        var at = text.IndexOf("K.3.3.1", StringComparison.Ordinal);
        Assert.True(at >= 0, "K.3.3.1 nicht im Normtext");
        // per table the counts and the symbols; a long list carries an X’ at the start of each of its rows
        var lists = Regex.Matches(text[at..], @"X[’']((?:[0-9A-F]{2}|\s+|X[’'])+)[’']").Take(8)
            .Select(m => Regex.Replace(m.Groups[1].Value, @"X[’']|\s", "")).ToList();
        Assert.Equal(8, lists.Count);
        for (var t = 0; t < 4; t++)
            foreach (var (counts, symbols) in new[] { JpegHuffmanTable.TypicalSpecifications[t], W.Typical[t] })
            {
                Assert.Equal(lists[2 * t], Convert.ToHexString(counts));
                Assert.Equal(lists[2 * t + 1], Convert.ToHexString(symbols));
            }
    }

    /// <summary>
    /// T.81, D.1.5.1, Table D.3: the Qe values, the next indices after an LPS and an MPS renormalization and the Switch_MPS column of the
    /// decoder (read by reflection, the tables are private) are those printed in the norm, all 113 entries. The text extraction prints a
    /// leading 1 before the short numbers of the next indices (the first half 1–9 as 11–19, the second half every number below 100, e.g. 32 as 132); an entry
    /// counts as the same when it is equal or, for exactly those values, carries that prefix. In the first half a printed 11–19 stays
    /// ambiguous (1–9 with the prefix or 11–19 itself) — there the test sequence of K.4 and the images of libjpeg-turbo check the decoder. The
    /// table is found by its number.
    /// </summary>
    [Fact]
    public void TableD3_SameAsTheNormText()
    {
        if (T81Text() is not { } text) return;
        var value = text.IndexOf("\n_Value\n", StringComparison.Ordinal);
        var at = value < 0 ? -1 : text.LastIndexOf("Table D.3", value, StringComparison.Ordinal);
        Assert.True(at >= 0, "Tabelle D.3 nicht im Normtext");
        var lines = text[at..].Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(120).ToList();
        List<int> qe = [], lps = [], mps = [], switches = [];
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            var next = lines[i + 1];
            if (lines[i] == "_Value") qe.AddRange(Regex.Matches(next, @"X[’']([0-9A-F]{4})[’']").Select(m => Convert.ToInt32(m.Groups[1].Value, 16)));
            else if (lines[i] == "_LPS") lps.AddRange(next.Split(' ').Select(int.Parse));
            else if (lines[i] == "_MPS" && lines[i - 1] == "Switch") switches.AddRange(next.Split(' ').Select(int.Parse));
            else if (lines[i] == "_MPS") mps.AddRange(next.Split(' ').Select(int.Parse));
        }
        static T[] Field<T>(string name) =>
            (T[])typeof(JpegArithmetic).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        var ownQe = Field<ushort>("Qe");
        var ownLps = Field<byte>("NextLps");
        var ownMps = Field<byte>("NextMps");
        var ownSwitches = Field<int>("Switches");
        Assert.Equal(113, ownQe.Length);
        Assert.Equal(ownQe.Select(q => (int)q), qe);
        Assert.Equal(Enumerable.Range(0, 113).Select(i => ownSwitches.Contains(i) ? 1 : 0), switches);
        Assert.Equal(113, lps.Count);
        Assert.Equal(113, mps.Count);
        for (var i = 0; i < 113; i++)
            foreach (var (printed, own) in new[] { (lps[i], (int)ownLps[i]), (mps[i], (int)ownMps[i]) })
                Assert.True(printed == own || (i < 57 ? own < 10 && printed == own + 10 : own < 100 && printed == own + 100),
                    $"Index {i}: gedruckt {printed}, Dekoder {own}");
    }

    /// <summary>The 256 decisions of T.81, K.4.1 (the hexadecimal sequence, most significant bit first) and the coded bytes.</summary>
    private static (byte[] Decisions, byte[] Coded)? K4()
    {
        if (T81Text() is not { } text) return null;
        var at = text.IndexOf("K.4.1 ", StringComparison.Ordinal);
        Assert.True(at >= 0, "K.4.1 nicht im Normtext");
        // the first two hexadecimal values of 32 bytes after the heading: the sequence and the coded data
        var values = Regex.Matches(text[at..], @"X[’']((?:[0-9A-F]{8}\s?){8})[’']").Take(2)
            .Select(m => Convert.FromHexString(Regex.Replace(m.Groups[1].Value, @"\s", ""))).ToList();
        Assert.Equal(2, values.Count);
        var decisions = new byte[values[0].Length * 8];
        for (var i = 0; i < decisions.Length; i++) decisions[i] = (byte)(values[0][i / 8] >> (7 - i % 8) & 1);
        return (decisions, values[1]);
    }

    /// <summary>
    /// The local text of ITU-T T.81; null if there is none. Never in Git. Found where tools/normabgleich/zaehlen.py puts it: PDFKNEAD_T81 (the
    /// file), else PDFKNEAD_NORMTEXTE, else %LOCALAPPDATA% (Windows), else $XDG_CACHE_HOME or ~/.cache (Linux), each with
    /// pdf-knead-build/normtexte/text/t81.txt — on Linux the application data folder of .NET is another one, and the tests found nothing there.
    /// </summary>
    internal static string? T81Text()
    {
        var path = Environment.GetEnvironmentVariable("PDFKNEAD_T81") ?? Path.Combine(NormTexts(), "text", "t81.txt");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string NormTexts()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_NORMTEXTE") is { Length: > 0 } own) return own;
        if (Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local) return Path.Combine(local, "pdf-knead-build", "normtexte");
        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
            ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return Path.Combine(cache, "pdf-knead-build", "normtexte");
    }
}
