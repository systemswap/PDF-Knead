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
