using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package uni-10 (The Unicode Standard 16.0.0, chapter 13, sections 13 to 13.5: Sinhala, Newa, Tibetan, Mongolian): the rows of the draft
/// that still asked for a test. Own text keeps the sequence typed (encoding rules for authors are not applied by PDF-Knead), Tibetan stacks
/// on the fixed-form Ra, the shad signs, own Mongolian text in horizontal lines, a free variation selector after a ZWJ, and no line break at
/// the Mongolian vowel separator or the narrow no-break space. Test inputs outside ASCII are written as escapes.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (uni-10)

    /// <summary>Asserts that own text saved and opened again comes back as typed, as the block and as the text read from the page.</summary>
    private static void Uni10AsTyped(string text)
    {
        var (block, page) = Uni04RoundTrip(text);
        Assert.Equal(text, block);
        Assert.Equal(text, page);
    }

    /// <summary>The glyphs drawn of a shaped text (the joiners and glyphs drawn as nothing left out).</summary>
    private static int[] Uni10Drawn(ShapedGlyph[] shaped) =>
        [.. shaped.Where(g => g.Glyph is not (E12Zwnj or E12Zwj) && !(g.Glyph == Ot02G(' ') && g.XAdvance == 0)).Select(g => g.Glyph)];

    private const int Uni10Tibetan = 0x0F00, Uni10Mongolian = 0x1800;

    // ------------------------------------------------------------ 13.2.1 Sinhala, 13.3.1 Newa

    /// <summary>
    /// UNI 13.2.1 sentence 86: the Sinhala syllables k.ru and k.ruu are an encoding rule for authors - own text with Ka, virama, ZWJ, Ra and the
    /// vowel sign U (U+0DD4), and the same with the look-alike sign AE (U+0DD0), saved, opened again as a block and read from the page: each
    /// comes back exactly as typed, PDF-Knead neither swaps nor normalises the sign.
    /// </summary>
    [Fact]
    public void Uni10_SinhalaKruAndKrae_KeptAsTyped()
    {
        Uni10AsTyped("\u0D9A\u0DCA\u200D\u0DBB\u0DD4");
        Uni10AsTyped("\u0D9A\u0DCA\u200D\u0DBB\u0DD0");
    }

    /// <summary>
    /// UNI 13.3.1 sentences 22-32: the aspirated sonorants of Newa are letters of their own, the similar Sanskrit clusters are written with
    /// consonant, virama, Ha - own text with the letter NGHA (U+11413) and with NGA, virama, HA (U+11412 U+11442 U+11434) saved, opened again
    /// and read from the page: each as typed, neither turned into the other.
    /// </summary>
    [Fact]
    public void Uni10_NewaAspiratedSonorant_LetterAndClusterKeptAsTyped()
    {
        Uni10AsTyped("\U00011413");
        Uni10AsTyped("\U00011412\U00011442\U00011434");
    }

    // ------------------------------------------------------------ 13.4.1 Tibetan

    /// <summary>
    /// UNI 13.4.1 sentences 107-115: the fixed-form Ra U+0F6A is the author's choice - own text with fixed Ra and subjoined Ya (U+0F6A U+0FB1)
    /// and with the ordinary Ra (U+0F62 U+0FB1) come back as typed; shaped in a font for the Tibetan block, U+0F6A is the base of its stack
    /// (its glyph and the subjoined Ya, no dotted circle), as with the shaping library.
    /// </summary>
    [Fact]
    public void Uni10_TibetanFixedFormRa_BaseOfAStack_KeptAsTyped()
    {
        Uni10AsTyped("\u0F6A\u0FB1");
        Uni10AsTyped("\u0F62\u0FB1");
        var font = E17Block(Uni10Tibetan, 0x100, null);
        E12Expect(font, "\u0F6A\u0FB1", E12G(Uni10Tibetan, 0x0F6A), E12G(Uni10Tibetan, 0x0FB1));
        E12Expect(font, "\u0F62\u0FB1", E12G(Uni10Tibetan, 0x0F62), E12G(Uni10Tibetan, 0x0FB1));
        Assert.DoesNotContain(E12Dotted, E12Own(font, "\u0F6A\u0FB1"));
    }

    /// <summary>
    /// UNI 13.4.1 sentences 161-163: two shad U+0F0D in a row are allowed beside the double shad U+0F0E - own text "ka shad shad" and "ka
    /// double shad" come back as typed; shaped, the first gives two shad glyphs, the second the one glyph of U+0F0E (the spacing is the font's).
    /// </summary>
    [Fact]
    public void Uni10_TibetanTwoShadOrDoubleShad_KeptAsTyped()
    {
        Uni10AsTyped("\u0F40\u0F0D\u0F0D");
        Uni10AsTyped("\u0F40\u0F0E");
        var font = E17Block(Uni10Tibetan, 0x100, null);
        int ka = E12G(Uni10Tibetan, 0x0F40), shad = E12G(Uni10Tibetan, 0x0F0D), nyis = E12G(Uni10Tibetan, 0x0F0E);
        Assert.Equal([ka, shad, shad], E12Own(font, "\u0F40\u0F0D\u0F0D"));
        Assert.Equal([ka, nyis], E12Own(font, "\u0F40\u0F0E"));
    }

    // ------------------------------------------------------------ 13.5.1 Mongolian

    /// <summary>
    /// UNI 13.5.1 sentences 54-55 (today's behaviour): Mongolian is written in vertical columns, own text is laid out in horizontal lines only -
    /// two Mongolian words in a wide block give one line, its glyphs on one baseline (no vertical offset) with x growing in text order.
    /// </summary>
    [Fact]
    public void Uni10_MongolianOwnText_OneHorizontalLineToday()
    {
        const string text = "\u182E\u1823\u1829\u182D\u1823\u182F \u182A\u1822\u1834\u1822\u182D";
        var layout = E20Layout(text, 10000);
        var line = Assert.Single(layout.Lines);
        Assert.Equal(text, string.Concat(line.Glyphs.Select(g => g.Text)));
        Assert.All(line.Glyphs, g => Assert.Equal(0, g.YOffset));
        for (var i = 1; i < line.Glyphs.Count; i++)
        {
            Assert.True(line.Glyphs[i].CharStart > line.Glyphs[i - 1].CharStart);
            Assert.True(line.Glyphs[i].X >= line.Glyphs[i - 1].X);
        }
        Assert.True(line.Glyphs[^1].X > line.Glyphs[0].X);
    }

    /// <summary>
    /// UNI 13.5.1 sentences 121-123 (the older reading is optional, not taken over): in a font whose cmap format 14 has the sequence Ga FVS1
    /// (U+182D U+180B), Ga FVS1 ZWJ gives the variant glyph; Ga ZWJ FVS1 gives no variant - Ga takes its form before the ZWJ (the initial
    /// form of the font's init lookup), the selector after the ZWJ chooses nothing.
    /// </summary>
    [Fact]
    public void Uni10_MongolianZwjBeforeFvs_NoVariant()
    {
        const int variant = 600;
        int ga = E12G(Uni10Mongolian, 0x182D);
        var format12 = E11Cmap((' ', ' ', Ot02G(' ')), (Uni10Mongolian, Uni10Mongolian + 0xAF, E12Base), (0x200C, 0x200D, E12Zwnj))[12..];
        var format14 = BeOt11((14, 2), (30, 4), (1, 4), (0x180B, 3), (0, 4), (21, 4), (1, 4), (0x182D, 3), (variant, 2));
        var gsub = E17Layout("mong", [("init", [0])], E11Sub((ga, E12F(1))));
        var font = ColorFontBuilder.WithTables(ShaperLimitTests.Liberation, new Dictionary<string, byte[]?>
        {
            ["cmap"] = CmapOt11((0, 5, format14), (3, 10, format12)), ["GSUB"] = gsub, ["GPOS"] = null, ["GDEF"] = null, ["kern"] = null,
        });
        var older = Uni10Drawn(E12Shape(font, "\u182D\u180B\u200D"));
        Assert.Equal(variant, older[0]);
        var swapped = Uni10Drawn(E12Shape(font, "\u182D\u200D\u180B"));
        Assert.Equal(E12F(1), swapped[0]);
        Assert.DoesNotContain(variant, swapped);
    }

    /// <summary>
    /// UNI 13.5.1 sentences 160-171: no line break before a detached suffix - a Mongolian word, a space and a word with a suffix after MVS
    /// (U+180E), and the same with U+202F, in a width for the whole second word but not for the rest of the first line: the line breaks at the
    /// space before the word, never at the MVS or the narrow no-break space.
    /// </summary>
    [Fact]
    public void Uni10_MongolianSuffix_NoBreakAtMvsOrNarrowNoBreakSpace()
    {
        const string first = "\u182D\u1821\u1837";
        foreach (var separator in new[] { "\u180E", "\u202F" })
        {
            var word = first + separator + "\u1821\u1834\u1821";
            var width = Math.Max(E20Width(word), E20Width(first + " " + first + separator)) + 1;
            Assert.True(width < E20Width(first + " " + word));
            var lines = E20LineTexts(E20Layout(first + " " + word, width));
            Assert.Equal(2, lines.Length);
            Assert.Equal(first, lines[0].TrimEnd());
            Assert.Equal(word, lines[1]);
        }
    }
}
