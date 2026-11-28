namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package uni-12 (The Unicode Standard 16.0.0, chapter 16, sections 16 to 16.4: Thai, Lao, Myanmar, Khmer): the rows of the draft that
/// still asked for a test - the Khmer characters not to be used are kept as typed (an encoding rule for authors), each with the glyph of the
/// font. Test inputs outside ASCII are written as escapes.
/// </summary>
public sealed partial class M7NormTests
{
    private const int Uni12Khmer = 0x1780;

    /// <summary>
    /// UNI 16.4.2 sentences 198-204: the independent vowels U+17A3 and U+17A4 are not to be used, PDF-Knead does not replace them - own text
    /// with both saved, opened again and read from the page comes back unchanged; shaped in a font for the Khmer block, each is the glyph of
    /// the cmap (no substitution, no dotted circle).
    /// </summary>
    [Fact]
    public void Uni12_KhmerDiscouragedIndependentVowels_KeptWithTheirGlyphs()
    {
        Uni10AsTyped("\u17A3\u17A4");
        var font = E17Block(Uni12Khmer, 0x80, null);
        Assert.Equal([E12G(Uni12Khmer, 0x17A3), E12G(Uni12Khmer, 0x17A4)], E12Own(font, "\u17A3\u17A4"));
    }

    /// <summary>
    /// UNI 16.4.2 sentences 210-219: the sign Beyyal U+17D8 is not to be used, the word is written out instead - own text with U+17D8 and with
    /// the written-out form (khan, lo, khan: U+17D4 U+179B U+17D4) saved, opened again and read from the page: both exactly as typed; shaped,
    /// each character is the glyph of the cmap.
    /// </summary>
    [Fact]
    public void Uni12_KhmerBeyyalAndWrittenOutForm_KeptAsTyped()
    {
        Uni10AsTyped("\u17D8");
        Uni10AsTyped("\u17D4\u179B\u17D4");
        var font = E17Block(Uni12Khmer, 0x80, null);
        Assert.Equal([E12G(Uni12Khmer, 0x17D8)], E12Own(font, "\u17D8"));
        Assert.Equal([E12G(Uni12Khmer, 0x17D4), E12G(Uni12Khmer, 0x179B), E12G(Uni12Khmer, 0x17D4)], E12Own(font, "\u17D4\u179B\u17D4"));
    }
}
