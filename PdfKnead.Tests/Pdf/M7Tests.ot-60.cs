using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-60 (OpenType specification, TrueType instructions 79.13–79.17, graphics state 80, mirroring pairs 81, comparison of glyf, CFF
/// and CFF2 82): the tests the sentence-by-sentence drafts named as missing — the fill rule of CFF2 glyphs and the operators CFF2 removed from
/// the Type 2 charstrings. The CFF2 builders are in the files of the packages ot-08 and ot-11, the page helpers in that of ot-55.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ 82.1 CFF2 against CFF

    /// <summary>
    /// OT 82.1 sentences 9–10 (reader): a CFF2 glyph is always filled with the nonzero winding rule. A CFF2 font whose glyph is two overlapping
    /// squares of the same direction (0 … 600 and 300 … 900), drawn as text on a page, fills the overlap (even-odd would leave it empty), and
    /// both squares outside it.
    /// </summary>
    [Fact]
    public void Cff2Glyph_OverlappingSameDirectionSquares_OverlapFilledOnThePage()
    {
        var glyph = "0 0 rmoveto 600 0 rlineto 0 600 rlineto -600 0 rlineto 300 -300 rmoveto 600 0 rlineto 0 600 rlineto -600 0 rlineto";
        var data = Cff2FontOt08(Cff2GlyphsOt08(glyph), 2);
        var outline = OpenTypeFont.Parse(data).Outline(1);
        Assert.Equal(new RectD(0, 0, 900, 900), outline.Bounds);
        Assert.Equal(2, outline.Verbs.Count(v => v == PathVerb.Move));
        using var bitmap = RenderOt55(data, "BT /F 100 Tf 20 20 Td <0001> Tj ET");
        // the glyph covers 20 … 110 on the page; the overlap 50 … 80
        Assert.True(DarkOt55(bitmap, 65, 65), "Überlappung nicht gefüllt");
        Assert.True(DarkOt55(bitmap, 25, 25), "erstes Quadrat nicht gefüllt");
        Assert.True(DarkOt55(bitmap, 105, 105), "zweites Quadrat nicht gefüllt");
        Assert.False(DarkOt55(bitmap, 25, 105), "außerhalb gefüllt");
    }

    /// <summary>
    /// OT 82.1 sentence 13 (reader): CFF2 removed endchar and return and every logic, storage and arithmetic operator of Type 2. The charstring
    /// „0 0 rmoveto 10 20 add 0 rlineto“ (add = 12 10) in a CFF2 table: add only clears the stack, so no line to (30, 0) is drawn — the same
    /// bytes in a CFF (version 1) Type 2 charstring add 10 and 20 and draw the line to (30, 0).
    /// </summary>
    [Fact]
    public void Cff2CharString_ArithmeticOperatorAdd_ClearsTheStack()
    {
        const string code = "0 0 rmoveto 10 20 0x0C 0x0A 0 rlineto";
        var cff2 = OpenTypeFont.Parse(Cff2FontOt08(Cff2GlyphsOt08(code), 2)).Outline(1);
        Assert.DoesNotContain(cff2.Points.ToArray().Chunk(2), p => p[0] == 30);
        var type2 = CffFont.Parse(OneGlyphCffOt11(CsOt08(code + " endchar"))).Outline(1);
        Assert.Contains(type2.Points.ToArray().Chunk(2), p => p[0] == 30 && p[1] == 0);
    }
}
