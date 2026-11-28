using PdfKnead.Core.Fonts.Layout;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, FG 1188: the canonical decompositions the runtime's normalization does not know (Unicode 16 and, on Windows, Dives Akuru) are known to
/// the shaper — for the scripts of the universal model, both spellings look the same in a font that has only one of them.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>
    /// UnicodeData.txt 16.0.0: Tulu-Tigalari U+11383 (an independent vowel) is U+11382 U+113C9 — a font with only the composed glyph draws the
    /// parts typed as it, a font with only the parts draws the composed character as them. Before, the table of the shaper held only Gurung
    /// Khema and Kirat Rai, and the missing spelling was drawn with the empty glyph.
    /// </summary>
    [Fact]
    public void Fg1188_TuluTigalari_BothSpellingsLookTheSame()
    {
        const int block = 0x11380, first = 0x11382, composed = 0x11383, second = 0x113C9;
        int G(int cp) => E12G(block, cp);
        string S(params int[] cps) => string.Concat(cps.Select(char.ConvertFromUtf32));
        // only the composed glyph: the second part is left out of the cmap
        var composedOnly = E17Font(null, null, (block, second - 1, E12Base), (second + 1, block + 0x7F, E12Base + (second + 1 - block)));
        Assert.Equal([G(composed)], E12Own(composedOnly, S(composed)));
        Assert.Equal([G(composed)], E12Own(composedOnly, S(first, second)));
        // only the parts: the composed character is left out of the cmap
        var partsOnly = E17Font(null, null, (block, composed - 1, E12Base), (composed + 1, block + 0x7F, E12Base + (composed + 1 - block)));
        Assert.Equal([G(first), G(second)], E12Own(partsOnly, S(first, second)));
        Assert.Equal([G(first), G(second)], E12Own(partsOnly, S(composed)));
    }

    /// <summary>
    /// The 21 canonical decompositions of UnicodeData.txt 16.0.0 that .NET 10 or the Windows normalization does not know (Todhri, Tulu-Tigalari,
    /// Dives Akuru, Gurung Khema, Kirat Rai; values written down here from the file, independent of the table of the shaper): the shaper
    /// decomposes each into its two parts and composes the two parts into it.
    /// </summary>
    [Theory]
    [InlineData(0x105C9, 0x105D2, 0x307)]
    [InlineData(0x105E4, 0x105DA, 0x307)]
    [InlineData(0x11383, 0x11382, 0x113C9)]
    [InlineData(0x11385, 0x11384, 0x113BB)]
    [InlineData(0x1138E, 0x1138B, 0x113C2)]
    [InlineData(0x11391, 0x11390, 0x113C9)]
    [InlineData(0x113C5, 0x113C2, 0x113C2)]
    [InlineData(0x113C7, 0x113C2, 0x113B8)]
    [InlineData(0x113C8, 0x113C2, 0x113C9)]
    [InlineData(0x11938, 0x11935, 0x11930)]
    [InlineData(0x16121, 0x1611E, 0x1611E)]
    [InlineData(0x16122, 0x1611E, 0x16129)]
    [InlineData(0x16123, 0x1611E, 0x1611F)]
    [InlineData(0x16124, 0x16129, 0x1611F)]
    [InlineData(0x16125, 0x1611E, 0x16120)]
    [InlineData(0x16126, 0x16121, 0x1611F)]
    [InlineData(0x16127, 0x16122, 0x1611F)]
    [InlineData(0x16128, 0x16121, 0x16120)]
    [InlineData(0x16D68, 0x16D67, 0x16D67)]
    [InlineData(0x16D69, 0x16D63, 0x16D67)]
    [InlineData(0x16D6A, 0x16D69, 0x16D67)]
    public void Fg1188_DecompositionsUnknownToTheRuntime_KnownToTheShaper(int composed, int first, int second)
    {
        Assert.Equal([.. TextShaper.CanonicalDecomposition(first), .. TextShaper.CanonicalDecomposition(second)],
            TextShaper.CanonicalDecomposition(composed));
        Assert.Equal(composed, TextShaper.CanonicalComposition(first, second));
    }

    /// <summary>The search uses the same table (No. 1188): Todhri and Tulu-Tigalari, composed on the page, are found with their parts and the other way.</summary>
    [Fact]
    public void Fg1188_Search_TodhriAndTuluTigalari_ComposedAndDecomposedFindEachOther()
    {
        foreach (var (composed, decomposed) in new[] { ("\U000105C9", "\U000105D2\u0307"), ("\U00011383", "\U00011382\U000113C9") })
        {
            var a = E20Text("x" + composed + "y");
            Assert.NotEmpty(E20Marked(a, E20Hit(a, decomposed)));
            var b = E20Text("x" + decomposed + "y");
            Assert.NotEmpty(E20Marked(b, E20Hit(b, composed)));
        }
    }
}
