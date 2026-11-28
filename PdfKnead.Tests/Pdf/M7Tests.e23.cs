using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package E23: line breaking of own texts after UAX #14, revision 53, with LineBreak.txt 16.0.0 — the classes, the rules LB1–LB31, the
/// emergency break, the tailorings from the Unicode chapters and the layouts of formatted text (<see cref="RichLayout"/>) and of plain text,
/// field values and free text (<see cref="ElementAppearance.Layout"/>; docs/FEHLERGEDAECHTNIS.md, No. 1196). The conformance file runs in
/// <c>LineBreakConformanceTests</c>. In the expected strings „÷“ marks a break opportunity and „¦“ separates the lines.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (E23)

    /// <summary>The text with „÷“ at every break opportunity between its code points (mandatory breaks too; not at the start and the end).</summary>
    private static string E23Marks(string text)
    {
        var breaks = LineBreaking.Analyze(text);
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (i > 0 && !(char.IsLowSurrogate(text[i]) && char.IsHighSurrogate(text[i - 1])) && breaks.Allowed(i)) sb.Append('÷');
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    /// <summary>A line as compared: tabs as spaces, without the spaces at its end.</summary>
    private static string E23Norm(string line) => line.Replace('\t', ' ').TrimEnd(' ');

    /// <summary>The lines of formatted text (Liberation Sans 10) in the width of <paramref name="widthText"/> (its visible width).</summary>
    private static string[] E23Rich(string text, string widthText, RichStyle? style = null)
    {
        var width = E20Layout(widthText, 100000, style: style).Lines[0].Width + 0.01;
        return [.. E20LineTexts(E20Layout(text, width, style: style)).Select(E23Norm)];
    }

    private static readonly ElementFace E23Face = ElementFace.For(ElementFont.Sans);

    private static TextElement E23Element(string text, double width) =>
        new() { Rect = new PdfRectangle(0, 0, width + 2 * ElementAppearance.Padding, 400), Text = text, Multiline = true };

    private static double E23PlainWidth(string text) => ElementAppearance.Layout(E23Element(text, 100000), E23Face)[0].Width;

    /// <summary>The lines of plain text (multiline, Liberation Sans 12) in the width of <paramref name="widthText"/> (its visible width).</summary>
    private static string[] E23Plain(string text, string widthText) =>
        [.. ElementAppearance.Layout(E23Element(text, E23PlainWidth(widthText) + 0.01), E23Face).Select(l => E23Norm(string.Concat(l.Text)))];

    /// <summary>Both layouts give the lines of <paramref name="expected"/> (separated by „¦“).</summary>
    private static void E23Lines(string text, string widthText, string expected)
    {
        var lines = expected.Split('¦').Select(E23Norm).ToArray();
        Assert.Equal(lines, E23Rich(text, widthText));
        Assert.Equal(lines, E23Plain(text, widthText));
    }

    private static IEnumerable<string> E23Resource(string name)
    {
        using var stream = typeof(M7NormTests).Assembly.GetManifestResourceStream("Bidi/" + name + ".gz") ?? throw new FileNotFoundException(name);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        while (reader.ReadLine() is { } line) yield return line;
    }

    // ------------------------------------------------------------ 1. classes (LineBreak.txt 16.0.0, LB1)

    /// <summary>
    /// UAX #14 2 sentences 12–17 and 44–103 (table 1), 5 sentences 8, 15–26, 5.1 (the members of every class), 6 sentence 11, 6.1 sentences
    /// 2–10 (LB1): the class of every code point 0–U+10FFFF is the one of LineBreak.txt 16.0.0 (shipped compressed with the tests, @missing
    /// default XX, unassigned code points with the defaults the file lists); samples from the draft rows; LB1 resolves AI, SG, XX to AL, SA to
    /// CM (Mn, Mc) or AL, CJ to NS; East_Asian_Width F, W, H for U+FF08, U+300C, not for „(“ and U+201C (A).
    /// </summary>
    [Fact]
    public void E23_LineBreakClasses_EveryCodePointAsLineBreakTxt()
    {
        var expected = new string[0x110000];
        Array.Fill(expected, "XX");
        foreach (var raw in E23Resource("LineBreak.txt"))
        {
            var body = raw.StartsWith("# @missing:", StringComparison.Ordinal) ? raw["# @missing:".Length..] : raw.Split('#')[0];
            if (body.Trim().Length == 0) continue;
            var fields = body.Split(';');
            var range = fields[0].Trim().Split("..");
            var first = int.Parse(range[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var last = range.Length > 1 ? int.Parse(range[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : first;
            for (var cp = first; cp <= last; cp++) expected[cp] = fields[1].Trim();
        }
        var wrong = Enumerable.Range(0, 0x110000).Where(cp => LineBreaking.ClassOf(cp).ToString() != expected[cp]).Take(10).ToList();
        Assert.True(wrong.Count == 0, string.Join(", ", wrong.Select(cp => $"U+{cp:X4} {LineBreaking.ClassOf(cp)} statt {expected[cp]}")));
        // samples named in the draft rows
        foreach (var (cp, cls) in new (int, LineBreakClass)[]
                 {
                     (0x0F0B, LineBreakClass.BA), (0x180E, LineBreakClass.GL), (0x3000, LineBreakClass.BA), (0xFFFC, LineBreakClass.CB),
                     (0x1B05, LineBreakClass.AK), (0x1BF2, LineBreakClass.VF), (0xE000, LineBreakClass.XX), (0x2A6E0, LineBreakClass.ID),
                     (0x21, LineBreakClass.EX), (0x2D, LineBreakClass.HY), (0xA0, LineBreakClass.GL), (0x200B, LineBreakClass.ZW),
                     (0x2060, LineBreakClass.WJ), (0x3002, LineBreakClass.CL), (0x0E01, LineBreakClass.SA), (0xAC00, LineBreakClass.H2),
                     (0xAC01, LineBreakClass.H3), (0x001C, LineBreakClass.CM), (0x0378, LineBreakClass.XX), (0x11F02, LineBreakClass.AP),
                     (0x11003, LineBreakClass.AP), (0xAA00, LineBreakClass.AS), (0x1B50, LineBreakClass.AS), (0x0600, LineBreakClass.NU),
                     (0x06DD, LineBreakClass.NU), (0x110BD, LineBreakClass.NU), (0x070F, LineBreakClass.AL), (0x2061, LineBreakClass.AL),
                     (0x2E17, LineBreakClass.BA), (0x1680, LineBreakClass.BA), (0x0964, LineBreakClass.BA), (0xAA5D, LineBreakClass.BA),
                     (0xAA40, LineBreakClass.BA), (0x00B4, LineBreakClass.BB), (0x02C8, LineBreakClass.BB), (0x0F04, LineBreakClass.BB),
                     (0x1806, LineBreakClass.BB), (0x2014, LineBreakClass.B2), (0x3041, LineBreakClass.CJ), (0x30FC, LineBreakClass.CJ),
                     (0xFF67, LineBreakClass.CJ), (0x0301, LineBreakClass.CM), (0x094D, LineBreakClass.CM), (0x20E4, LineBreakClass.CM),
                     (0x1F466, LineBreakClass.EB), (0x1F3FB, LineBreakClass.EM), (0x4E00, LineBreakClass.ID), (0x20000, LineBreakClass.ID),
                     (0x2FFFD, LineBreakClass.ID), (0x1100, LineBreakClass.JL), (0x1161, LineBreakClass.JV), (0x11A8, LineBreakClass.JT),
                     (0x05D0, LineBreakClass.HL), (0x3005, LineBreakClass.NS), (0x0030, LineBreakClass.NU), (0xFF10, LineBreakClass.ID),
                     (0x0025, LineBreakClass.PO), (0x2103, LineBreakClass.PO), (0x0024, LineBreakClass.PR), (0x20AC, LineBreakClass.PR),
                     (0x20C1, LineBreakClass.PR), (0x201D, LineBreakClass.QU), (0x1F1E6, LineBreakClass.RI), (0x2024, LineBreakClass.IN),
                     (0x002F, LineBreakClass.SY), (0x2460, LineBreakClass.AI), (0x00A7, LineBreakClass.AI), (0x50005, LineBreakClass.XX),
                     (0xA4FF, LineBreakClass.BA),
                 })
            Assert.Equal(cls, LineBreaking.ClassOf(cp));
        // LB1
        Assert.Equal(LineBreakClass.AL, LineBreaking.Resolved(0x2460));
        Assert.Equal(LineBreakClass.AL, LineBreaking.Resolved(0xE000));
        Assert.Equal(LineBreakClass.AL, LineBreaking.Resolved(0x0E01));
        Assert.Equal(LineBreakClass.CM, LineBreaking.Resolved(0x0E31));
        Assert.Equal(LineBreakClass.NS, LineBreaking.Resolved(0x3041));
        // $EastAsian (EastAsianWidth.txt 16.0.0)
        Assert.True(LineBreaking.IsEastAsian(0xFF08));
        Assert.True(LineBreaking.IsEastAsian(0x300C));
        Assert.False(LineBreaking.IsEastAsian('('));
        Assert.False(LineBreaking.IsEastAsian(0x201C));
    }

    // ------------------------------------------------------------ 2. the rules (font-free)

    /// <summary>
    /// UAX #14 2 sentences 7–9, 21–32, 44–103, 3.1, 5.1, 5.3–5.7, 6.1, 6.2 (LB4–LB31): the break opportunities of the rules for the inputs of
    /// the draft rows, one case per rule or class — hard breaks, ZW, ZWJ, combining sequences, WJ, GL, the opening and closing classes even after
    /// spaces, quotation marks (Pi, Pf, East Asian context), CB, hyphens, BB, B2, IN, numbers (LB25), Korean syllable blocks, orthographic
    /// syllables of Brahmic scripts (LB28a), East Asian brackets (LB30), regional indicators, emoji modifiers, and the scripts of the chapters
    /// (Tibetan, Cham, Ethiopic, Mongolian, Runic, Ogham, Devanagari, Lisu). „÷“ marks every opportunity the analysis gives.
    /// </summary>
    [Theory]
    // LB4–LB6: hard line breaks (mandatory breaks count as opportunities); none between CR and LF, none before a hard break
    [InlineData("a\u000B÷b")]
    [InlineData("a\u000C÷b")]
    [InlineData("a\r\n÷b")]
    [InlineData("a\r÷b")]
    [InlineData("a\u0085÷b")]
    [InlineData("a\u2028÷b")]
    [InlineData("aaaa\n÷bb")]
    // LB7, LB8: not before SP or ZW; after ZW even after spaces
    [InlineData("aaaa ÷bbbb")]
    [InlineData("aaaa\u200B÷bbbb")]
    [InlineData("aaaa\u200B  ÷bbbb")]
    [InlineData("aaaa\u200B÷\u2060bbbb")]
    // LB8a, LB9, LB10: ZWJ sequences, combining sequences after a tab and a soft hyphen, a lone mark as AL
    [InlineData("\U0001F469\u200D\U0001F4BB÷\U0001F469\u200D\U0001F4BB")]
    [InlineData("xx ÷a\t\u0301÷bb")]
    [InlineData("xx ÷a\u00AD\u0301÷bb")]
    [InlineData("xx ÷\u0301(bb)")]
    [InlineData("aaaa\u200Ebbbb ÷cc")]
    [InlineData("aaaa\u034Fbbbb ÷cc")]
    [InlineData("aaaa ÷o\u0361u ÷bbbb")]
    [InlineData("aaaa ÷n\uFE20g\uFE21 ÷bbbb")]
    [InlineData("aaaa !\u20E4")]
    // LB11, LB12, LB12a: WJ also after a space; GL; before GL only after SP, BA, HY
    [InlineData("aaaa \u2060bbbb")]
    [InlineData("ᚠᚢ᛫\u2060ᚦ")]
    [InlineData("漢\u2060字")]
    [InlineData("aaaa\u00A0bbbb")]
    [InlineData("E‑Mail ÷ab")]
    [InlineData("1\u2007000\u2007000 ÷ab")]
    [InlineData("aaaa ÷\u00A0bbbb")]
    [InlineData("aa-÷\u00A0bb")]
    [InlineData("xx ÷aaaa ÷‑bbbb")]
    // LB13–LB15d: closing, exclamation, solidus even after spaces; opening even before spaces; quotation marks; decimal mark after a space
    [InlineData("xx ÷aaaa !")]
    [InlineData("aaaa ?")]
    [InlineData("aaaa )")]
    [InlineData("aaaa 」÷bb")]
    [InlineData("漢÷字÷漢÷字。÷漢÷字")]
    [InlineData("xx ÷( aaaa")]
    [InlineData("¿Qué?")]
    [InlineData("xx ÷« aaaa")]
    [InlineData("“ aa")]
    [InlineData("xx ÷aaaa »")]
    [InlineData("aaaa ” ÷bb")]
    [InlineData("aaaa ÷.5")]
    [InlineData("xx ÷aaaa ;")]
    [InlineData("aaaa : ÷bb")]
    // LB16, LB17: closing before a nonstarter, B2 pairs, even with spaces
    [InlineData("xx ÷aa) 々")]
    [InlineData("」 々")]
    [InlineData("aaaa ÷々")]
    [InlineData("xx ÷— —")]
    [InlineData("aaaa÷—÷bbbb")]
    [InlineData("aaaa÷——÷bbbb")]
    [InlineData("aa ÷—÷bb ÷cc÷— ÷dd")]
    // LB19, LB19a: ambiguous quotation marks; between East Asian characters breaks before an opening and after a closing one
    [InlineData("a\"b")]
    [InlineData("xx ÷a\t\"bb")]
    [InlineData("中÷文÷“中÷文”÷中÷文")]
    [InlineData("a“b”c")]
    // LB20, LB20a, LB21, LB21a, LB21b, LB22
    [InlineData("aaaa÷￼÷bbbb")]
    [InlineData("xx ÷-aaaa")]
    [InlineData("xx ÷‐aaaa")]
    [InlineData("Donau-÷Dampfschiff")]
    [InlineData("aaaa‐÷bbbb")]
    [InlineData("aaaa–÷bbbb")]
    [InlineData("aaaa֊÷bbbb")]
    [InlineData("aaaa‒÷bbbb")]
    [InlineData("aaa‧÷bbb")]
    [InlineData("aaa|÷bbb")]
    [InlineData("xx ÷a\t-÷bb")]
    [InlineData("あ÷い÷う々÷え÷お")]
    [InlineData("あ÷い÷うぇ")]
    [InlineData("きゃ÷きゃ")]
    [InlineData("aaaa÷´bbbb")]
    [InlineData("pro÷ˈnʌn÷ˌsieɪ")]
    [InlineData("א־ab")]
    [InlineData("אב-abc")]
    [InlineData("abc-÷def")]
    [InlineData("xx ÷א\tbbbb")]
    [InlineData("a/אב")]
    [InlineData("中÷文÷中÷文……")]
    [InlineData("Kapitel․․․․․ ÷5")]
    // LB23–LB25: letters and digits, prefixes and postfixes, numbers
    [InlineData("xx ÷a1b2c3")]
    [InlineData("中÷文÷中%")]
    [InlineData("￥中÷文")]
    [InlineData("xx ÷$abc")]
    [InlineData("xx ÷abc%")]
    [InlineData("xx ÷$(12.35)")]
    [InlineData("xx ÷12.54¢")]
    [InlineData("xx ÷-1/12")]
    [InlineData("xx ÷(12)¢")]
    [InlineData("xx ÷.50")]
    [InlineData("xx ÷₹1,00,000.00")]
    [InlineData("12:59 ÷100.00 ÷10,000 ÷06/07/99 ÷1/2")]
    [InlineData("ab ÷١٢٫٥٠ ÷cd")]
    [InlineData("ab ÷20℃ ÷cd")]
    [InlineData("ab ÷€12 ÷cd")]
    [InlineData("aaaa ÷-5")]
    [InlineData("und/÷oder")]
    [InlineData("www.beispiel.de/÷pfad/÷datei")]
    [InlineData("a/÷1")]
    // LB26, LB27: Korean syllable blocks as ID
    [InlineData("각÷각")]
    [InlineData("가÷나÷다%")]
    [InlineData("ㄱ÷ㅏ÷ㄴ÷ㅏ")]
    // LB28, LB28a: letters; orthographic syllables of Brahmic scripts
    [InlineData("aaaa@bbbb ÷cc")]
    [InlineData("ᬓ\u1B44ᬓ÷ᬓ\u1B44ᬓ")]
    [InlineData("ᬓ÷ᬓ÷ᬓ")]
    [InlineData("᭑÷᭒")]
    [InlineData("\U00011F02\U00011F12÷\U00011F02\U00011F12")]
    [InlineData("ᯖ\u1BEAᯇ\u1BF2÷ᯖ\u1BEAᯇ\u1BF2")]
    [InlineData("ꨀ÷ꨁ÷ꨂ")]
    [InlineData("ꨀꩀ÷ꨁ")]
    [InlineData("ꨀ꩝÷ꨁ")]
    // LB29, LB30: „e.g.“, brackets with letters; East Asian brackets
    [InlineData("xx ÷e.g.")]
    [InlineData("xx ÷person(s)")]
    [InlineData("(s)he")]
    [InlineData("aaaaG÷「ユ」")]
    [InlineData("abc÷（d")]
    [InlineData("abc(d")]
    // LB30a, LB30b, LB31
    [InlineData("\U0001F1E9\U0001F1EA÷\U0001F1EB\U0001F1F7÷\U0001F1EE\U0001F1F9")]
    [InlineData("\U0001F44D\U0001F3FD÷\U0001F44D\U0001F3FD")]
    [InlineData("\U0001F466\U0001F3FB÷\U0001F466")]
    [InlineData("\U0001FC00\U0001F3FB")]
    [InlineData("⌚÷⌚")]
    [InlineData("漢÷字 ÷漢÷字")]
    // the scripts of the chapters: Tibetan, Ethiopic, Mongolian, Runic, Ogham, Devanagari, Cham, Lisu (tailored), Thai without a dictionary
    [InlineData("ཀཁ་÷གང")]
    [InlineData("ཀཁ༌གང")]
    [InlineData("ང་།")]
    [InlineData("ཀཁ÷༄ག")]
    [InlineData("ཀ\u0F7F÷ཁ")]
    [InlineData("ཀ་÷ཁ་÷「ག་÷ང་」÷ཅ་÷ཆ་。÷ཇ་÷ཉ་")]
    [InlineData("ሀሁ፡÷ሂ")]
    [InlineData("ሀ፠ሁ")]
    [InlineData("ᠠᠡ᠄÷ᠢᠣ")]
    [InlineData("ᠠᠡ÷᠆ᠢᠣ")]
    [InlineData("ᠭᠡᠷ\u180Eᠡᠴᠡ")]
    [InlineData("ᚠᚢ᛫÷ᚦᚨ")]
    [InlineData("\U00010380\U0001039F÷\U00010381")]
    [InlineData("ᚁᚂᚃ\u1680÷ᚄᚅᚆ")]
    [InlineData("कक।÷कक")]
    [InlineData("xx ÷ꓡꓲ ꓿")]
    [InlineData("กขคงจฉ")]
    [InlineData("xx ÷aa⸗÷bb")]
    [InlineData("ab ÷①②③④")]
    [InlineData("½¼ ÷ab")]
    [InlineData("1⁄2 ÷ab")]
    [InlineData("ab ÷\u070Fܐܒܓ ÷cd")]
    [InlineData("ab ÷\u0600١٢٣ ÷cd")]
    [InlineData(" ÷ab")]
    [InlineData("〳〵")]
    [InlineData("aaaa ÷\U00013000\U00013430\U00013001 ÷bb")]
    [InlineData("aaaa ÷\U00011066\U0001107F\U00011067 ÷bb")]
    public void E23_BreakOpportunities_AfterTheRules(string marked) => Assert.Equal(marked, E23Marks(marked.Replace("÷", "")));

    // ------------------------------------------------------------ 3. both layouts

    /// <summary>
    /// UAX #14 2 sentences 7–9, 21–32 and 44–103, 3.1, 4 sentences 1–4, 5.1, 5.3, 5.7, 6.1, 6.2 (own texts): formatted text and plain text (field
    /// values, free text) end their lines at the last break opportunity that fits — no line begins with „!“, „;“, „»“, „)“, U+3002 or a
    /// nonstarter, none ends with „(“ or „«“, lines break after hyphens, dashes, ZWSP, U+FFFC, U+1680 (not shown at the line end), between
    /// ideographs and Hangul syllables, after tsheg, Cham syllables and dandas, never between a tab and the mark after it, a space and WJ, the
    /// joined letters of Thai (only the emergency break between clusters). Before both broke only at spaces (plain text: U+0020 only) and else
    /// hard at the width.
    /// </summary>
    [Theory]
    [InlineData("xxxxxx aaaa !", "xxxxxx aaaa ", "xxxxxx ¦aaaa !")]
    [InlineData("xxxxxx aaaa ;", "xxxxxx aaaa ", "xxxxxx ¦aaaa ;")]
    [InlineData("xxxxxx aaaa )", "xxxxxx aaaa ", "xxxxxx ¦aaaa )")]
    [InlineData("xxxxxx aaaa »", "xxxxxx aaaa ", "xxxxxx ¦aaaa »")]
    [InlineData("xxxxxx ( aaaa", "xxxxxx ( a", "xxxxxx ¦( aaaa")]
    [InlineData("xxxxxx « aaaa", "xxxxxx « a", "xxxxxx ¦« aaaa")]
    [InlineData("aaaa .5", "aaaa .", "aaaa ¦.5")]
    [InlineData("xxxxxx aaaa : bb", "xxxxxx aaaa ", "xxxxxx ¦aaaa : bb")]
    [InlineData("xxxxxx — —", "xxxxxx — ", "xxxxxx ¦— —")]
    [InlineData("xxxxxx a\t\"bb", "xxxxxx a\t", "xxxxxx ¦a\t\"bb")]
    [InlineData("xxxxxx -aaaa", "xxxxxx -a", "xxxxxx ¦-aaaa")]
    [InlineData("xxxxxx ‑bbbb", "xxxxxx ‑b", "xxxxxx ¦‑bbbb")]
    [InlineData("Donau-Dampfschiff", "Donau-Damp", "Donau-¦Dampfschiff")]
    [InlineData("dispara\u00AD‑te", "dispara-", "dispara\u00AD¦‑te")]
    [InlineData("aaaa‐bbbb", "aaaa‐bb", "aaaa‐¦bbbb")]
    [InlineData("aaaa–bbbb", "aaaa–bb", "aaaa–¦bbbb")]
    [InlineData("aaaa—bbbb", "aaaa—b", "aaaa—¦bbbb")]
    [InlineData("aaaa´bbbb", "aaaa´b", "aaaa¦´bbbb")]
    [InlineData("aaaa￼bbbb", "aaaa￼b", "aaaa￼¦bbbb")]
    [InlineData("aaaa\u2003bbbb", "aaaa\u2003b", "aaaa\u2003¦bbbb")]
    [InlineData("aaa|bbb", "aaa|b", "aaa|¦bbb")]
    [InlineData("aaa‧bbb", "aaa‧b", "aaa‧¦bbb")]
    [InlineData("www.beispiel.de/pfad/datei", "www.beispiel.de/pfad/", "www.beispiel.de/pfad/¦datei")]
    [InlineData("xxxxxx aaaa \u2060bb", "xxxxxx aaaa \u2060b", "xxxxxx ¦aaaa \u2060bb")]
    [InlineData("xxxxxx aaaa\t\u0301bb", "xxxxxx aaaa\t\u0301", "xxxxxx aaaa\t\u0301¦bb")]
    [InlineData("aaaa@bbbb cc", "aaaa@bbbbc", "aaaa@bbbb¦cc")]
    [InlineData("aaaa\u034Fbbbb cc", "aaaabbbbc", "aaaa\u034Fbbbb¦cc")]
    [InlineData("1\u2007000\u2007000 ab", "1\u2007000\u2007000 a", "1\u2007000\u2007000¦ab")]
    [InlineData("Kapitel․․․․․ 5", "Kapitel․․․․․ ", "Kapitel․․․․․¦5")]
    [InlineData("ab $(100.00) cd", "$(100.00)", "ab¦$(100.00)¦cd")]
    [InlineData("ab (12.00)% cd", "(12.00)%", "ab¦(12.00)%¦cd")]
    [InlineData("xxxxxx person(s)", "xxxxxx person(", "xxxxxx ¦person(s)")]
    [InlineData("xxxxxx e.g.", "xxxxxx e.", "xxxxxx ¦e.g.")]
    [InlineData("xx ", "", "xx¦")]
    [InlineData("漢字漢字。漢字", "漢字漢字", "漢字漢¦字。漢字")]
    [InlineData("漢字\u3000漢字", "漢字\u3000", "漢字\u3000¦漢字")]
    [InlineData("あいう々えお", "あいう", "あい¦う々え¦お")]
    [InlineData("가 나다라마바", "가 나다라", "가 나다라¦마바")]
    [InlineData("กขคงจฉ", "กขค", "กขค¦งจฉ")]
    [InlineData("ᚁᚂᚃ\u1680ᚄᚅᚆ", "ᚁᚂᚃ\u1680ᚄ", "ᚁᚂᚃ¦ᚄᚅᚆ")]
    [InlineData("ᚠᚢ᛫ᚦᚨ", "ᚠᚢ᛫", "ᚠᚢ᛫¦ᚦᚨ")]
    [InlineData("कक।कक", "कक।", "कक।¦कक")]
    [InlineData("ཀཁ་གང", "ཀཁ་", "ཀཁ་¦གང")]
    [InlineData("ཀ་ཁ་ག།", "ཀ་ཁ་ག", "ཀ་ཁ་¦ག།")]
    [InlineData("ሀሁ፡ሂ", "ሀሁ፡", "ሀሁ፡¦ሂ")]
    [InlineData("ᠠᠡ᠄ᠢᠣ", "ᠠᠡ᠄", "ᠠᠡ᠄¦ᠢᠣ")]
    [InlineData("ᠠᠡ᠆ᠢᠣ", "ᠠᠡ᠆", "ᠠᠡ¦᠆ᠢᠣ")]
    [InlineData("ꨀꨁꨂ", "ꨀꨁ", "ꨀꨁ¦ꨂ")]
    [InlineData("ꨀꩀꨁ", "ꨀꩀ", "ꨀꩀ¦ꨁ")]
    [InlineData("ꨀ꩝ꨁ", "ꨀ꩝", "ꨀ꩝¦ꨁ")]
    [InlineData("xxxxxx ꓡꓲ ꓿", "xxxxxx ꓡꓲ ", "xxxxxx ¦ꓡꓲ ꓿")]
    [InlineData("\U0001F1E9\U0001F1EA\U0001F1EB\U0001F1F7\U0001F1EE\U0001F1F9", "\U0001F1E9\U0001F1EA\U0001F1EB",
        "\U0001F1E9\U0001F1EA¦\U0001F1EB\U0001F1F7¦\U0001F1EE\U0001F1F9")]
    public void E23_Layout_BreaksAtTheOpportunitiesOfUax14(string text, string widthText, string expected) => E23Lines(text, widthText, expected);

    // ------------------------------------------------------------ 4. deviations of the plain text (before UAX #14)

    /// <summary>
    /// UAX #14 6.1 sentences 34–39 (LB7, LB8), 3 sentences 24–26, 5.1 sentences 127–132 and 772–780, 5.4 sentences 1–2 (plain text, field values,
    /// free text): a word that fits up to the edge stays in its line though the space after it does not („aaaa bb“ → „aaaa“, „bb“; before „ bb“);
    /// „a b“ in the width of „a“ gives two lines, not three; „aa bbbb cc“ in the width of „aa bbbb“ keeps „aa bbbb“ (before „aa “); no line
    /// begins with a space of a run; ZWSP is a break opportunity; a soft hyphen breaks with the hyphen of the font, unbroken it shows nothing;
    /// the emergency break falls between clusters only — „e“ U+0301 twenty times: no line begins with the mark (before the plain text broke
    /// only after U+0020, at the glyph that no longer fitted, docs/FEHLERGEDAECHTNIS.md, No. 1196).
    /// </summary>
    [Fact]
    public void E23_PlainText_BreaksLikeFormattedText()
    {
        Assert.Equal(["aaaa", "bb"], E23Plain("aaaa bb", "aaaa"));
        Assert.Equal(["a", "b"], E23Plain("a b", "a"));
        Assert.Equal(["aa bbbb", "cc"], E23Plain("aa bbbb cc", "aa bbbb"));
        var space = E23PlainWidth("a a") - E23PlainWidth("aa");
        var spaced = ElementAppearance.Layout(E23Element("aaaa  bbbb", E23PlainWidth("aaaa") + 1.5 * space), E23Face);
        Assert.Equal(["aaaa", "bbbb"], spaced.Select(l => string.Concat(l.Text)));
        Assert.Equal(["aaaa\u200B", "bbbb"], E23Plain("aaaa\u200Bbbbb", "aaaab"));
        // the soft hyphen: with the hyphen where the line breaks after it
        var lines = ElementAppearance.Layout(E23Element("Donau\u00ADdampf", E23PlainWidth("Donau-") + 0.01), E23Face);
        Assert.Equal(2, lines.Count);
        Assert.Equal(E23Face.Glyph('-'), lines[0].Glyphs[^1]);
        Assert.True(lines[0].Advances[^1] > 0);
        Assert.Equal("dampf", string.Concat(lines[1].Text));
        var unbroken = Assert.Single(ElementAppearance.Layout(E23Element("Donau\u00ADdampf", 1000), E23Face));
        Assert.Equal(E23PlainWidth("Donaudampf"), unbroken.Width, 3);
        // the emergency break: between clusters only
        var marks = ElementAppearance.Layout(E23Element(string.Concat(Enumerable.Repeat("e\u0301", 20)), E23PlainWidth("e") * 3.5), E23Face);
        Assert.True(marks.Count > 1);
        Assert.All(marks, l => Assert.NotEqual("\u0301", l.Text[0]));
    }

    /// <summary>
    /// UAX #14 6.1 sentences 22–33 (LB4–LB6), 5.1 sentences 323–338, UNI 23.1.2 sentences 18–20, 23.2.1 sentences 49–63 (formatted text, also a
    /// free text that comes without the editor): CR, CR LF, NEL end a paragraph (the next line with the first-line indent), VT and FF a line of
    /// the same paragraph (without it), each drawn as no glyph; CR LF is one break; „aaaa\nbb“ in the width of „aaaa“ gives two lines and no empty
    /// one. Before formatted text broke only at LF, U+2028 and U+2029.
    /// </summary>
    [Fact]
    public void E23_RichLayout_HardLineBreaks_ParagraphOrLine()
    {
        foreach (var separator in new[] { "\r", "\r\n", "\u0085", "\n", "\u2029" })
        {
            var layout = E20Layout("a" + separator + "b", 1000, hangingIndent: -10);
            Assert.Equal(["a", "b"], E20LineTexts(layout));
            Assert.Equal(10, layout.Lines[1].X, 3);
        }
        foreach (var separator in new[] { "\u000B", "\u000C", "\u2028" })
        {
            var layout = E20Layout("a" + separator + "b", 1000, hangingIndent: -10);
            Assert.Equal(["a", "b"], E20LineTexts(layout));
            Assert.Equal(0, layout.Lines[1].X, 3);
            Assert.True(layout.Lines[0].EndsParagraph);
        }
        Assert.Equal(["aaaa", "bb"], E23Rich("aaaa\nbb", "aaaa"));
        Assert.Equal(["aaaa", "bb"], E23Plain("aaaa\nbb", "aaaa"));
        // the editor takes a typed FF as the line separator: no first-line indent after it
        Assert.Equal(0, E20Layout(EditUnits.Clean("a\u000Cb"), 1000, hangingIndent: -10).Lines[1].X, 3);
    }

    /// <summary>
    /// UAX #14 6.1 sentences 11–21 (LB2, LB3): every line holds at least one character — „ab“ in a width of one point begins its first line with
    /// „a“, no empty line before; the last line ends at the end of the text („aa bb“ wide: one line to 5, ending the paragraph).
    /// </summary>
    [Fact]
    public void E23_StartAndEndOfText()
    {
        var narrow = E20Layout("ab", 1);
        Assert.Equal(0, narrow.Lines[0].Start);
        Assert.Equal("a", E20LineTexts(narrow)[0]);
        var wide = Assert.Single(E20Layout("aa bb", 1000).Lines);
        Assert.Equal(5, wide.End);
        Assert.True(wide.EndsParagraph);
        var plain = ElementAppearance.Layout(E23Element("ab", 1), E23Face);
        Assert.Equal("a", string.Concat(plain[0].Text));
    }

    // ------------------------------------------------------------ 5. emergency break and tailorings

    /// <summary>
    /// UAX #14 3 sentences 24–26, 6.2 sentences 115–120, 133–138, 8 sentences 13–14, UNI 13.5.1 sentences 172–174, 16.13.1 sentence 32 (own
    /// texts): where a word is wider than its line, the emergency break keeps extended grapheme clusters, pairs of regional indicators, an emoji
    /// with its modifier, ZWJ sequences and orthographic syllables together — in the width of one glyph the lines are whole pairs (they run over
    /// the edge rather than cut one); a Mongolian suffix broken off goes with the vowel separator U+180E at the start of the new line.
    /// </summary>
    [Fact]
    public void E23_EmergencyBreak_KeepsPairsModifiersAndSuffixes()
    {
        const string flags = "\U0001F1E9\U0001F1EA\U0001F1EB\U0001F1F7";
        Assert.Equal(["\U0001F1E9\U0001F1EA", "\U0001F1EB\U0001F1F7"], E23Rich(flags, "\U0001F1E9"));
        Assert.Equal(["\U0001F1E9\U0001F1EA", "\U0001F1EB\U0001F1F7"], E23Plain(flags, "\U0001F1E9"));
        const string thumbs = "\U0001F44D\U0001F3FD\U0001F44D\U0001F3FD\U0001F44D\U0001F3FD";
        Assert.All(E23Rich(thumbs, "\U0001F44D"), l => Assert.Equal("\U0001F44D\U0001F3FD", l));
        Assert.All(E23Plain(thumbs, "\U0001F44D"), l => Assert.Equal("\U0001F44D\U0001F3FD", l));
        const string coder = "\U0001F469\u200D\U0001F4BB";
        Assert.All(E23Rich(coder + coder + coder, "\U0001F469"), l => Assert.Equal(coder, l));
        // the Mongolian vowel separator begins the new line
        const string suffix = "ᠭᠡᠷ\u180Eᠡᠴᠡ";
        Assert.Equal(["ᠭᠡᠷ", "\u180Eᠡᠴᠡ"], E23Rich(suffix, "ᠭᠡᠷ"));
        Assert.Equal(["ᠭᠡᠷ", "\u180Eᠡᠴᠡ"], E23Plain(suffix, "ᠭᠡᠷ"));
        // LB28a also in the emergency break: Kawi U+11F02 before its aksara
        const string kawi = "\U00011F02\U00011F12\U00011F02\U00011F12";
        Assert.Equal(["\U00011F02\U00011F12", "\U00011F02\U00011F12"], E23Rich(kawi, "\U00011F02"));
    }

    /// <summary>
    /// UAX #14 5.4 sentences 15–16, 22–27 (own texts): the hyphen a soft hyphen shows where the line breaks after it is the one of the script —
    /// ARMENIAN HYPHEN U+058A after Armenian letters, MONGOLIAN TODO SOFT HYPHEN U+1806 after Mongolian ones (fonts that have them), U+002D else
    /// (M7NormTests.E20_SoftHyphen_BreakWithHyphen_OtherwiseInvisible); in Arabic the letters keep the forms of the unbroken word and the hyphen
    /// is added after them. Before always U+002D.
    /// </summary>
    [Fact]
    public void E23_SoftHyphen_HyphenOfTheScript()
    {
        foreach (var (block, length, word, hyphen) in new[]
                 {
                     (0x0531, 0x5A, "ԱԲԳ\u00ADԴԵԶ", 0x058A),
                     (0x1800, 0xAA, "ᠠᠡᠢ\u00ADᠣᠤᠥ", 0x1806),
                 })
        {
            var family = Uni14Family(E17Font(null, null, (block, block + length - 1, E12Base), ('-', '-', Ot02G('-'))));
            var style = new RichStyle { Family = family, Size = 10 };
            var width = Assert.Single(Uni14Layout(family, word[..4].Replace("\u00AD", "") + char.ConvertFromUtf32(hyphen), 100000).Lines).Width + 0.01;
            var lines = RichLayout.Layout(new RichText(word, style), width, BlockAlignment.Left, 1.15, null, 0).Lines;
            Assert.True(lines.Count >= 2);
            Assert.Equal(4, lines[1].Start);
            var last = lines[0].Glyphs[^1];
            Assert.Equal("\u00AD", last.Text);
            Assert.Equal(last.Face!.Glyph(hyphen), last.Glyph);
        }
        // Arabic: the forms of the unbroken word
        const string arabic = "سلم\u00ADسلم";
        var unbroken = E20Layout(arabic, 100000).Lines[0].Glyphs;
        var whole = unbroken.Where(g => g.Text != "\u00AD").Select(g => g.Glyph).ToArray();
        var face = unbroken[0].Face!;
        var cut = E20Layout(arabic, unbroken.Take(3).Sum(g => g.Advance) + face.Font.AdvanceWidth(face.Glyph('-')) * 10.0 / face.Font.UnitsPerEm + 0.01);
        Assert.Equal(2, cut.Lines.Count);
        Assert.Equal(whole, cut.Lines.SelectMany(l => l.Glyphs).Where(g => g.Text != "\u00AD").Select(g => g.Glyph));
    }

    /// <summary>
    /// UAX #14 5.1 sentences 102–120 (own texts, justified): in a justified line U+2003 keeps its advance, only U+0020 widens.
    /// </summary>
    [Fact]
    public void E23_Justified_FixedSpaceKeepsItsAdvance()
    {
        var text = new RichText("aa\u2003bb cc dd ee ff gg hh ii jj", E20Style);
        var natural = RichLayout.Layout(text, 100000, BlockAlignment.Left, 1.15, null, 0).Lines[0].Glyphs.First(g => g.Text == "\u2003").Advance;
        var justified = RichLayout.Layout(text, E20Width("aa\u2003bb cc dd ee"), BlockAlignment.Justify, 1.15, null, 0);
        Assert.True(justified.Lines.Count > 1);
        Assert.Equal(natural, justified.Lines[0].Glyphs.First(g => g.Text == "\u2003").Advance, 6);
    }

    /// <summary>
    /// UAX #14 5.1 sentence 496 (own texts): NON-BREAKING HYPHEN looks like HYPHEN — Liberation Sans lacks U+2011, which is drawn with its hyphen
    /// (same advance; before the missing glyph was drawn); plain text likewise.
    /// </summary>
    [Fact]
    public void E23_NonBreakingHyphen_LooksLikeTheHyphen() =>
        Assert.Equal((E20Width("a‐b"), E23PlainWidth("a‐b")), (E20Width("a‑b"), E23PlainWidth("a‑b")));

    /// <summary>
    /// UNI 13.4.1 sentences 133–152 (own texts): Tibetan with U+00A0 between the phrases breaks after a tsheg — „bkra shis bde legs“ three times,
    /// in the width of three tsek-bars: every line but the last ends with U+0F0B, none inside a tsek-bar (before the text broke hard).
    /// </summary>
    [Fact]
    public void E23_Tibetan_BreaksAfterTsheg()
    {
        const string phrase = "བཀ\u0FB2་ཤ\u0F72ས་བད\u0F7A་ལ\u0F7Aགས།";
        var text = string.Join("\u00A0", Enumerable.Repeat(phrase, 3));
        var layout = E20Layout(text, E20Width("བཀ\u0FB2་ཤ\u0F72ས་བད\u0F7A་") + 0.01);
        Assert.True(layout.Lines.Count > 2);
        Assert.All(layout.Lines.SkipLast(1), l => Assert.Equal('་', text[l.End - 1]));
    }

    /// <summary>
    /// UNI 18.9.1 sentences 67–70 (own texts; tailoring of UAX #14 8.1): Lisu punctuation after a space never begins a line — „… ꓡꓲ ꓿“ with the edge
    /// before U+A4FF: it goes with the syllable before it (LineBreak.txt gives BA, which allows the break after a space; tailored to EX).
    /// </summary>
    [Fact]
    public void E23_Lisu_PunctuationNotAtTheLineStart()
    {
        Assert.Equal(["xxxxxx", "ꓡꓲ ꓿"], E23Rich("xxxxxx ꓡꓲ ꓿", "xxxxxx ꓡꓲ "));
        Assert.Equal(LineBreakClass.BA, LineBreaking.ClassOf(0xA4FF));
    }

    /// <summary>
    /// UAX #14 8 sentences 1–4, 8.2 sentences 11–13, 6.2 sentences 97–111, 3.1 sentences 8–13 (own texts): Korean after the standard — Hangul
    /// syllables (H2, H3) and compatibility jamo (ID) break between each other also without spaces, in flush and in justified text; a syllable
    /// block of conjoining jamo stays whole (LB26).
    /// </summary>
    [Fact]
    public void E23_Korean_BreaksBetweenSyllables_AsTheStandard()
    {
        Assert.Equal("가÷나÷다÷라 ÷마÷바÷사÷아", E23Marks("가나다라 마바사아"));
        foreach (var alignment in new[] { BlockAlignment.Left, BlockAlignment.Justify })
        {
            var lines = RichLayout.Layout(new RichText("가나다라 마바사아", E20Style), E20Width("가나다") + 0.01, alignment,
                1.15, null, 0).Lines;
            Assert.Equal(0, lines[1].Start - 3);
        }
        Assert.Equal("ㄱ÷ㅏ÷ㄴ÷ㅏ", E23Marks("ㄱㅏㄴㅏ"));
        var jamo = string.Concat(Enumerable.Repeat("ᄀᅶᆨ", 6));
        var blocks = E20Layout(jamo, E20Width("ᄀᅶᆨ") * 2.5);
        Assert.True(blocks.Lines.Count > 1);
        Assert.All(blocks.Lines, l => Assert.Equal('ᄀ', jamo[l.Start]));
    }

    /// <summary>
    /// UAX #14 4 sentences 5–10, 4.1 sentences 7–14 (UAX14-C2), 8.1 (disclosure): the tailorings and higher-level overrides of PDF-Knead hold in
    /// the code — (1) single-line fields and elements break at hard breaks only; (2) SA without a dictionary is the LB1 default (no opportunity
    /// in a Thai word); (3) Lisu punctuation as EX; (4) the emergency break keeps grapheme clusters, LB28a syllables, regional indicator pairs,
    /// emoji modifiers, not after U+180E, not before a Pau Cin Hau tone mark; (5) U+1680 is not shown at the end of a broken line; (6) the hyphen of
    /// the script at a soft hyphen; (7) the editor turns CR, NEL, FS, GS, RS, U+2029 into paragraph breaks, VT and FF into U+2028, US into a tab;
    /// (8) FS, GS, RS end no line in plain text (class CM as the file gives it); (9) U+2E17 breaks after it as the file gives (BA).
    /// </summary>
    [Fact]
    public void E23_Uax14_DisclosedTailorings_HoldInTheCode()
    {
        var single = ElementAppearance.Layout(E23Element("aaaa bbbb cccc", 10) with { Multiline = false }, E23Face);
        Assert.Single(single);
        Assert.Equal("กขคง", E23Marks("กขคง"));
        Assert.Equal("xx ÷ꓡꓲ ꓿", E23Marks("xx ꓡꓲ ꓿"));
        var breaks = LineBreaking.Analyze("\U0001F1E9\U0001F1EAᠭ\u180Eᠡ\U00011AC0\U00011AF0e\u0301");
        Assert.False(breaks.EmergencyAllowed(2));    // inside the pair of regional indicators
        Assert.True(breaks.EmergencyAllowed(4));     // after the pair
        Assert.True(breaks.EmergencyAllowed(5));     // before U+180E
        Assert.False(breaks.EmergencyAllowed(6));    // after U+180E
        Assert.False(breaks.EmergencyAllowed(9));    // before the Pau Cin Hau tone mark
        Assert.False(breaks.EmergencyAllowed(12));   // before the combining mark
        Assert.Equal(["ᚁᚂᚃ", "ᚄᚅᚆ"], E23Rich("ᚁᚂᚃ\u1680ᚄᚅᚆ", "ᚁᚂᚃ\u1680ᚄ"));
        Assert.Equal("a\nb\nc\nd\u2028e\u2028f\tg", EditUnits.Clean("a\rb\u001Cc\u0085d\u000Be\u000Cf\u001Fg"));
        Assert.Single(ElementAppearance.Layout(E23Element("a\u001Db", 300), E23Face));
        Assert.Equal("xx ÷aa⸗÷bb", E23Marks("xx aa⸗bb"));
    }

    /// <summary>
    /// Security (FG 1137, 1192): the analysis reads unpaired surrogates as U+FFFD and needs linear time — 200 000 combining marks after one
    /// letter, 100 000 regional indicators and 100 000 spaces are analysed well within the time limit; the opportunities around a lone
    /// surrogate are those of U+FFFD (AL).
    /// </summary>
    [Fact]
    public void E23_Security_UnpairedSurrogatesAndLongRuns()
    {
        var (high, low) = ((char)0xD800, (char)0xDC00);
        Assert.Equal("a" + high + "b ÷c" + low, E23Marks("a" + high + "b c" + low));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var text in new[] { "a" + new string((char)0x0301, 200000), string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x1F1E9), 100000)), new string(' ', 100000) + "a" })
        {
            var breaks = LineBreaking.Analyze(text);
            Assert.True(breaks.Allowed(text.Length));
        }
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
    }

    /// <summary>
    /// UAX #14 6.2 sentences 112–114, 3 sentences 4–9 (own texts; no loss for Latin): a Latin paragraph without hyphens or special punctuation
    /// breaks exactly where it broke before UAX #14 — in widths from 40 to 300 points every line but the last ends after a space and the next
    /// word would no longer fit (the greedy choice at the spaces); the same for plain text.
    /// </summary>
    [Fact]
    public void E23_Latin_SameLinesAsBefore()
    {
        const string text = "Der schnelle braune Fuchs springt über den faulen Hund und läuft dann weiter durch den grünen Wald bis zum Abend";
        var words = text.Split(' ');
        var widest = words.Max(w => E23PlainWidth(w));   // (plain text is set larger; no word is broken in an emergency)
        for (var width = widest + 1; width <= 300; width += 7)
        {
            var lines = E20LineTexts(E20Layout(text, width)).Select(l => l.TrimEnd(' ')).ToList();
            Assert.Equal(text, string.Join(' ', lines));
            var at = 0;
            for (var i = 0; i < lines.Count - 1; i++)
            {
                at += lines[i].Split(' ').Length;
                Assert.True(E20Width(lines[i] + " " + words[at]) > width - 1e-6, $"{width}: „{lines[i]}“ hätte „{words[at]}“ aufnehmen können");
            }
            var plain = ElementAppearance.Layout(E23Element(text, width), E23Face).Select(l => string.Concat(l.Text)).ToList();
            Assert.Equal(text, string.Join(' ', plain));
            at = 0;
            for (var i = 0; i < plain.Count - 1; i++)
            {
                at += plain[i].Split(' ').Length;
                Assert.True(E23PlainWidth(plain[i] + " " + words[at]) > width - 1e-6);
            }
        }
    }
}
