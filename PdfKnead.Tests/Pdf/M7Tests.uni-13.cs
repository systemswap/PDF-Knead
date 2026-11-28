using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Text;
using JoinForm = PdfKnead.Core.Fonts.Layout.ArabicJoining.Form;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package uni-13 (The Unicode Standard 16.0.0, chapter 16, sections 16.5 to 16.14: Tai Le, New Tai Lue, Tai Tham, Tai Viet, Pahawh Hmong,
/// Nyiakeng Puachue Hmong, Pau Cin Hau, Hanifi Rohingya): the rows of the draft that still asked for a test - the scripts stored in visual
/// order keep the sequence typed (shaping, saving, the text read back and the search), the Bidi levels, the language form of the font, marks
/// at the anchors of the font as with the shaping library, the joining forms and marks of Hanifi Rohingya, and the hard break of a word too
/// long for its line. The fonts are Liberation Sans with a cmap that maps the block of the script to glyphs 100 ... (see M7Tests.e12.cs).
/// Test inputs outside ASCII are written as escapes.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (uni-13)

    /// <summary>The Bidi levels of a text, one per code point (the level of its first UTF-16 unit).</summary>
    private static byte[] Uni13Levels(string text)
    {
        var levels = Bidi.Levels(text);
        var result = new List<byte>();
        for (var i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
            result.Add(levels[i]);
        return [.. result];
    }

    /// <summary>Own text saved on a blank page: the texts of the glyphs the single hit of a search for the text itself marks.</summary>
    private static string[] Uni13FoundGlyphs(string text)
    {
        var saved = UniTestsSaved(UniTestsElement(text));
        using var doc = PdfDocument.Open(saved);
        var page = PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[0]));
        return E20Marked(page, E20Hit(page, text));
    }

    private const int Uni13TaiLe = 0x1950, Uni13NewTaiLue = 0x1980, Uni13TaiTham = 0x1A20, Uni13TaiViet = 0xAA80, Uni13Pahawh = 0x16B00,
        Uni13Nyiakeng = 0x1E100, Uni13PauCinHau = 0x11AC0, Uni13Rohingya = 0x10D00;

    // ------------------------------------------------------------ 16.5.1 Tai Le, 16.6.1 New Tai Lue

    /// <summary>
    /// UNI 16.5.1 sentences 7 and 9: a Tai Le syllable is stored as written, the tone mark last - U+1956 U+1964 U+1970 shaped in a font for the
    /// block without lookups gives the three glyphs of the cmap in this order, as the shaping library; saved and opened again, the block and the
    /// text read from the page are as typed.
    /// </summary>
    [Fact]
    public void Uni13_TaiLe_SyllableOrderAsTyped()
    {
        var font = E12Font(Uni13TaiLe, null);
        E12Expect(font, "\u1956\u1964\u1970", E12G(Uni13TaiLe, 0x1956), E12G(Uni13TaiLe, 0x1964), E12G(Uni13TaiLe, 0x1970));
        Uni10AsTyped("\u1956\u1964\u1970");
    }

    /// <summary>
    /// UNI 16.6.1 sentences 9-11: New Tai Lue runs left to right - two letters (U+1982 U+1985) are at level 0; after a Hebrew letter and a space
    /// both are at level 2.
    /// </summary>
    [Fact]
    public void Uni13_NewTaiLue_LeftToRight()
    {
        Assert.Equal(new byte[] { 0, 0 }, Bidi.Levels("\u1982\u1985"));
        Assert.Equal(new byte[] { 1, 1, 2, 2 }, Bidi.Levels("\u05D0 \u1982\u1985"));
    }

    /// <summary>
    /// UNI 16.6.1 sentences 12-22: the vowels drawn left of their consonant are stored before it - E U+19B5 then KA U+1982 shaped in a font for
    /// the block gives the glyph of E before KA, as typed and as the shaping library (no reordering, no dotted circle); saved, the block and the
    /// page text are as typed, and a search for the same sequence finds both glyphs.
    /// </summary>
    [Fact]
    public void Uni13_NewTaiLue_VisualOrderVowelStoredFirst_FoundAsTyped()
    {
        var font = E12Font(Uni13NewTaiLue, null);
        E12Expect(font, "\u19B5\u1982", E12G(Uni13NewTaiLue, 0x19B5), E12G(Uni13NewTaiLue, 0x1982));
        Uni10AsTyped("\u19B5\u1982");
        Assert.Equal(["\u19B5", "\u1982"], Uni13FoundGlyphs("\u19B5\u1982"));
    }

    /// <summary>
    /// UNI 16.6.1 sentences 25-36: two-part vowels in visual order and the tone mark last - U+19B5 U+1982 U+19C0 U+19C8 gives four glyphs in this
    /// order, the vowel U+19B6 is one glyph of its own (not twice U+19B5), as the shaping library; saved, the block and the page text are as
    /// typed.
    /// </summary>
    [Fact]
    public void Uni13_NewTaiLue_TwoPartVowelsAndToneAsTyped()
    {
        var font = E12Font(Uni13NewTaiLue, null);
        E12Expect(font, "\u19B5\u1982\u19C0\u19C8",
            E12G(Uni13NewTaiLue, 0x19B5), E12G(Uni13NewTaiLue, 0x1982), E12G(Uni13NewTaiLue, 0x19C0), E12G(Uni13NewTaiLue, 0x19C8));
        E12Expect(font, "\u19B6\u1982", E12G(Uni13NewTaiLue, 0x19B6), E12G(Uni13NewTaiLue, 0x1982));
        Uni10AsTyped("\u19B5\u1982\u19C0\u19C8");
        Uni10AsTyped("\u19B6\u1982");
    }

    // ------------------------------------------------------------ 16.7.1 Tai Tham

    /// <summary>
    /// UNI 16.7.1 sentences 16-17: the glottal consonant U+1A4B is drawn differently in Northern Thai - a font with a locl substitution of
    /// U+1A4B under "lana" in the language system "NTA ": shaped with the language "nod" the substituted form, without a language the glyph of
    /// the cmap.
    /// </summary>
    [Fact]
    public void Uni13_TaiTham_GlottalConsonant_LanguageFormByLocl()
    {
        int glottal = E12G(Uni13TaiTham, 0x1A4B);
        var gsub = Ot02Layout([new Ot02Script("lana", new Ot02Lang("", []), new Ot02Lang("NTA ", [0]))], [new Ot02Feature("locl", [0])],
            [E11Sub((glottal, E12F(0)))]);
        var shaper = TextShaper.For(OpenTypeFont.Parse(E12Font(Uni13TaiTham, gsub)));
        Assert.Equal(E12F(0), shaper.Shape("\u1A4B", null, "nod")[0].Glyph);
        Assert.Equal(glottal, shaper.Shape("\u1A4B")[0].Glyph);
    }

    /// <summary>
    /// UNI 16.7.1 sentences 44-49: Mai Sam U+1A7B is stored where the author puts it - own text U+1A32 U+1A62 U+1A60 U+1A26 U+1A7B saved, opened
    /// again and read from the page comes back as typed.
    /// </summary>
    [Fact]
    public void Uni13_TaiTham_MaiSamKeptAsTyped() => Uni10AsTyped("\u1A32\u1A62\u1A60\u1A26\u1A7B");

    // ------------------------------------------------------------ 16.8.1 Tai Viet

    /// <summary>
    /// UNI 16.8.1 sentences 10-15: Tai Viet runs left to right, the vowels drawn left of the consonant are stored before it - U+AAB5 U+AA80 U+AAB1
    /// shaped in a font for the block gives three glyphs in this order, as the shaping library; all three at level 0.
    /// </summary>
    [Fact]
    public void Uni13_TaiViet_LeftToRight_GlyphsInOrder()
    {
        var font = E12Font(Uni13TaiViet, null);
        E12Expect(font, "\uAAB5\uAA80\uAAB1", E12G(Uni13TaiViet, 0xAAB5), E12G(Uni13TaiViet, 0xAA80), E12G(Uni13TaiViet, 0xAAB1));
        Assert.Equal(new byte[] { 0, 0, 0 }, Bidi.Levels("\uAAB5\uAA80\uAAB1"));
    }

    /// <summary>
    /// UNI 16.8.1 sentences 16-18: the vowel must always be written is a spelling rule - a consonant U+AA80 alone gives its one glyph without a
    /// dotted circle, as the shaping library; saved, the block and the page text are as typed.
    /// </summary>
    [Fact]
    public void Uni13_TaiViet_ConsonantAlone_NoDottedCircle()
    {
        E12Expect(E12Font(Uni13TaiViet, null), "\uAA80", E12G(Uni13TaiViet, 0xAA80));
        Uni10AsTyped("\uAA80");
    }

    /// <summary>
    /// UNI 16.8.1 sentences 19-26: the vowel E U+AAB5 is stored before the consonant KO U+AA80 - saved and opened again, the block and the page
    /// text are as typed (the glyphs: M7NormTests.E17_TaiViet_VisualOrderVowelsAsTyped_TonesByAbvm).
    /// </summary>
    [Fact]
    public void Uni13_TaiViet_VisualOrderVowelKeptAsTyped() => Uni10AsTyped("\uAAB5\uAA80");

    // ------------------------------------------------------------ 16.11.1 Pahawh Hmong

    /// <summary>The Pahawh Hmong font: mark lookups under "hmng" for U+16B30 on U+16B16 and on U+16B1D, U+16B35 on U+16B1D.</summary>
    private static byte[] Uni13PahawhFont()
    {
        int G(int c) => E12G(Uni13Pahawh, c);
        var gpos = E17Layout("hmng", [("mark", [0, 1, 2])], E17Attach(4, G(0x16B30), G(0x16B16), (0, 0), (420, 1350)),
            E17Attach(4, G(0x16B30), G(0x16B1D), (0, 0), (380, 1500)), E17Attach(4, G(0x16B35), G(0x16B1D), (0, 0), (300, 1450)));
        return E12Font(Uni13Pahawh, null, gpos);
    }

    /// <summary>
    /// UNI 16.11.1 sentences 11-24: Pahawh Hmong is stored in visual order (vowel, tone mark, consonant, mark) and not reordered - U+16B16
    /// U+16B30 U+16B1D U+16B35 gives four glyphs in this order, each mark without advance at the anchor of its base, as the shaping library;
    /// saved, the block and the page text are as typed.
    /// </summary>
    [Fact]
    public void Uni13_PahawhHmong_VisualOrderNotReordered()
    {
        int G(int c) => E12G(Uni13Pahawh, c);
        var font = Uni13PahawhFont();
        const string text = "\U00016B16\U00016B30\U00016B1D\U00016B35";
        E12Expect(font, text, G(0x16B16), G(0x16B30), G(0x16B1D), G(0x16B35));
        var drawn = E17SamePositions(font, text);
        var advance = OpenTypeFont.Parse(font).AdvanceWidth(G(0x16B16));
        Assert.Equal((G(0x16B30), 420.0, 1350.0), drawn[1]);
        Assert.Equal((G(0x16B35), advance + 300.0, 1450.0), drawn[3]);
        Uni10AsTyped(text);
    }

    /// <summary>
    /// UNI 16.11.1 sentences 32-38: the combining marks are tone marks on vowels, U+16B30 and U+16B35 also on consonants - U+16B30 on the vowel
    /// U+16B16 and on the consonant U+16B1D, U+16B35 on the consonant U+16B1D sit at the anchor of each without advance, as the shaping library.
    /// </summary>
    [Fact]
    public void Uni13_PahawhHmong_MarkOnVowelAndConsonant()
    {
        int G(int c) => E12G(Uni13Pahawh, c);
        var font = Uni13PahawhFont();
        Assert.Equal((G(0x16B30), 420.0, 1350.0), E17SamePositions(font, "\U00016B16\U00016B30")[1]);
        Assert.Equal((G(0x16B30), 380.0, 1500.0), E17SamePositions(font, "\U00016B1D\U00016B30")[1]);
        Assert.All(E12Shape(font, "\U00016B1D\U00016B30").Skip(1), g => Assert.Equal(0, g.XAdvance));
        Assert.Equal((G(0x16B35), 300.0, 1450.0), E17SamePositions(font, "\U00016B1D\U00016B35")[1]);
        Assert.All(E12Shape(font, "\U00016B1D\U00016B35").Skip(1), g => Assert.Equal(0, g.XAdvance));
    }

    /// <summary>
    /// UNI 16.11.1 sentence 51 (a rule for fonts): the tens sign U+16B5B and the zero U+16B50 are two different glyphs of the cmap; PDF-Knead
    /// substitutes neither.
    /// </summary>
    [Fact]
    public void Uni13_PahawhHmong_TensAndZeroTwoGlyphs()
    {
        var glyphs = E12Own(E12Font(Uni13Pahawh, null), "\U00016B5B\U00016B50");
        Assert.Equal([E12G(Uni13Pahawh, 0x16B5B), E12G(Uni13Pahawh, 0x16B50)], glyphs);
        Assert.NotEqual(glyphs[0], glyphs[1]);
    }

    // ------------------------------------------------------------ 16.12.1 Nyiakeng Puachue Hmong

    /// <summary>
    /// UNI 16.12.1 sentences 5-8: Nyiakeng Puachue Hmong runs left to right - U+1E100 U+1E124 at level 0; after a Hebrew letter and a space at
    /// level 2.
    /// </summary>
    [Fact]
    public void Uni13_NyiakengPuachueHmong_LeftToRight()
    {
        Assert.Equal(new byte[] { 0, 0 }, Uni13Levels("\U0001E100\U0001E124"));
        Assert.Equal(new byte[] { 1, 1, 2, 2 }, Uni13Levels("\u05D0 \U0001E100\U0001E124"));
    }

    /// <summary>
    /// UNI 16.12.1 sentence 9 (a spelling rule): PDF-Knead neither inserts nor removes spaces - own text with one space before a syllable that
    /// begins with a vowel, saved, opened again and read from the page, comes back as typed with its one space.
    /// </summary>
    [Fact]
    public void Uni13_NyiakengPuachueHmong_SpaceKeptAsTyped() => Uni10AsTyped("\U0001E100\U0001E124 \U0001E124\U0001E100");

    /// <summary>
    /// UNI 16.12.1 sentences 17-20: the tone mark belongs to the character before it, as typed - with a mark lookup under "hmnp" for U+1E130 on
    /// the vowel U+1E124, U+1E100 U+1E124 U+1E130 U+1E100 gives the mark without advance at the anchor of the vowel, as the shaping library.
    /// </summary>
    [Fact]
    public void Uni13_NyiakengPuachueHmong_ToneMarkAtTheVowel()
    {
        int G(int c) => E12G(Uni13Nyiakeng, c);
        var gpos = E17Layout("hmnp", [("mark", [0])], E17Attach(4, G(0x1E130), G(0x1E124), (0, 0), (360, 1420)));
        var font = E12Font(Uni13Nyiakeng, null, gpos);
        const string text = "\U0001E100\U0001E124\U0001E130\U0001E100";
        E12Expect(font, text, G(0x1E100), G(0x1E124), G(0x1E130), G(0x1E100));
        var drawn = E17SamePositions(font, text);
        Assert.Equal((G(0x1E130), OpenTypeFont.Parse(font).AdvanceWidth(G(0x1E100)) + 360.0, 1420.0), drawn[2]);
        Assert.Equal(0, E12Shape(font, text)[2].XAdvance);
    }

    /// <summary>
    /// UNI 16.12.1 sentences 30-36: the further signs are drawn as typed, PDF-Knead repeats nothing - own text ending in U+1E13C comes back as
    /// typed, and U+1E13C is one glyph of the cmap.
    /// </summary>
    [Fact]
    public void Uni13_NyiakengPuachueHmong_SignOneGlyphAsTyped()
    {
        Uni10AsTyped("\U0001E100\U0001E124\U0001E13C");
        Assert.Equal([E12G(Uni13Nyiakeng, 0x1E100), E12G(Uni13Nyiakeng, 0x1E124), E12G(Uni13Nyiakeng, 0x1E13C)],
            E12Own(E12Font(Uni13Nyiakeng, null), "\U0001E100\U0001E124\U0001E13C"));
    }

    // ------------------------------------------------------------ 16.13.1 Pau Cin Hau

    /// <summary>
    /// UNI 16.13.1 sentences 14-22: all characters of Pau Cin Hau are spacing and linear - U+11AC0 U+11AD7 U+11AF0 gives three glyphs with an
    /// advance in this order, as the shaping library; saved, the block and the page text are as typed.
    /// </summary>
    [Fact]
    public void Uni13_PauCinHau_LinearGlyphsAsTyped()
    {
        var font = E12Font(Uni13PauCinHau, null);
        const string text = "\U00011AC0\U00011AD7\U00011AF0";
        E12Expect(font, text, E12G(Uni13PauCinHau, 0x11AC0), E12G(Uni13PauCinHau, 0x11AD7), E12G(Uni13PauCinHau, 0x11AF0));
        Assert.All(E12Shape(font, text), g => Assert.True(g.XAdvance > 0));
        Uni10AsTyped(text);
    }

    /// <summary>
    /// UNI 16.13.1 sentence 31: words are not hyphenated at the end of a line - Pau Cin Hau syllables (consonant, vowel, tone mark U+11AF0)
    /// repeated without spaces into a word wider than the line, in widths for two and a half and for four and a half glyphs: the word is broken
    /// into several lines, none ends in a hyphen and nothing is added to the text.
    /// </summary>
    [Fact]
    public void Uni13_PauCinHau_HardBreakWithoutHyphen()
    {
        var word = string.Concat(Enumerable.Repeat("\U00011AC0\U00011AD7\U00011AF0", 4));
        foreach (var room in new[] { 2.5, 4.5 })
        {
            var layout = E20Layout(word, E20Width("\U00011AC0") * room);
            Assert.True(layout.Lines.Count > 1);
            Assert.All(layout.Lines, l => Assert.NotEqual("-", l.Glyphs[^1].Text));
            Assert.Equal(word, string.Concat(E20LineTexts(layout)));
        }
    }

    /// <summary>
    /// UNI 16.13.1 sentence 32: no break between a tone mark and the character before it - the word of Uni13_PauCinHau_HardBreakWithoutHyphen in
    /// the same widths: no line begins with the tone mark U+11AF0 (the hard break falls before the syllable or after its tone mark).
    /// </summary>
    [Fact]
    public void Uni13_PauCinHau_HardBreakNotBeforeToneMark()
    {
        var word = string.Concat(Enumerable.Repeat("\U00011AC0\U00011AD7\U00011AF0", 4));
        foreach (var room in new[] { 2.5, 4.5 })
        {
            var lines = E20Layout(word, E20Width("\U00011AC0") * room).Lines;
            Assert.True(lines.Count > 1);
            Assert.All(lines, l => Assert.NotEqual("\U00011AF0", l.Glyphs[0].Text));
        }
    }

    // ------------------------------------------------------------ 16.14.1 Hanifi Rohingya

    /// <summary>
    /// UNI 16.14.1 sentences 7-8: Hanifi Rohingya runs right to left - two letters at level 1; own right-to-left text saved and read back gives
    /// the letters in logical order: a line of Rohingya words exactly as typed; "letters, space, a" as the block typed, the page text with the
    /// word in logical order (the Latin letter first: a line beginning left-to-right on the left is read as a left-to-right paragraph, as the
    /// reference readers do, see PageText.LogicalOrder).
    /// </summary>
    [Fact]
    public void Uni13_HanifiRohingya_RightToLeft_ReadBackInLogicalOrder()
    {
        Assert.Equal(new byte[] { 1, 1 }, Uni13Levels("\U00010D01\U00010D02"));
        Uni10AsTyped("\U00010D01\U00010D02 \U00010D03\U00010D04");
        var (block, page) = Uni04RoundTrip("\U00010D01\U00010D02 a");
        Assert.Equal("\U00010D01\U00010D02 a", block);
        Assert.Equal("a \U00010D01\U00010D02", page);
    }

    /// <summary>
    /// UNI 16.14.1 sentences 12-26: Tassi and a tone mark are stored Tassi first, the tone mark drawn left of it; vowel signs and the nasal sign
    /// join like letters - a font for U+10D00-U+10D3F with GPOS under "rohg" (Tassi U+10D27 on U+10D01 by mark, the tone mark U+10D24 on Tassi by
    /// mkmk, 300 units to the left) and init/medi/fina substitutions: U+10D01 U+10D27 U+10D24 keeps Tassi first, both without advance, the tone
    /// mark 300 units left of Tassi; U+10D01 U+10D1D U+10D23 gives the initial, medial and final form; saved, both as typed.
    /// </summary>
    [Fact]
    public void Uni13_HanifiRohingya_TassiAndToneMark_JoiningForms()
    {
        int G(int c) => E12G(Uni13Rohingya, c);
        Assert.Equal([JoinForm.Initial, JoinForm.Medial, JoinForm.Final], ArabicJoining.Forms([0x10D01, 0x10D1D, 0x10D23]));
        var gpos = E17Layout("rohg", [("mark", [0]), ("mkmk", [1])],
            E17Attach(4, G(0x10D27), G(0x10D01), (0, 0), (450, 1300)), E17Attach(6, G(0x10D24), G(0x10D27), (0, 0), (-300, 0)));
        var gsub = E17Layout("rohg", [("init", [0]), ("medi", [1]), ("fina", [2])],
            E11Sub((G(0x10D01), E12F(1))), E11Sub((G(0x10D1D), E12F(2))), E11Sub((G(0x10D23), E12F(3))));
        var font = E17Block(Uni13Rohingya, 0x40, gsub, gpos);
        var marks = E12Shape(font, "\U00010D01\U00010D27\U00010D24");
        Assert.Equal([G(0x10D01), G(0x10D27), G(0x10D24)], marks.Select(g => g.Glyph));
        Assert.Equal(0, marks[1].XAdvance);
        Assert.Equal(0, marks[2].XAdvance);
        Assert.Equal(marks[1].XOffset - 300, marks[2].XOffset);
        Assert.Equal(marks[1].YOffset, marks[2].YOffset);
        Assert.Equal([E12F(1), E12F(2), E12F(3)], E12Own(font, "\U00010D01\U00010D1D\U00010D23"));
        Uni10AsTyped("\U00010D01\U00010D27\U00010D24");
        Uni10AsTyped("\U00010D01\U00010D1D\U00010D23");
    }

    /// <summary>
    /// UNI 16.14.1 sentences 31-32: the digits U+10D30-U+10D39 run left to right within the right-to-left text (Bidi class AN) - two letters, a
    /// space and two digits get the levels 1, 1, 1, 2, 2.
    /// </summary>
    [Fact]
    public void Uni13_HanifiRohingya_DigitsLeftToRight()
    {
        Assert.Equal(BidiClass.AN, Bidi.ClassOf(0x10D31));
        Assert.Equal(new byte[] { 1, 1, 1, 2, 2 }, Uni13Levels("\U00010D01\U00010D02 \U00010D31\U00010D32"));
    }
}
