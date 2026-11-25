using PdfKnead.Core.Fonts;

namespace PdfKnead.Tests.Fonts;

/// <summary>How <see cref="PdfKnead.Core.Fonts.Layout.TextShaper"/> follows the OpenType GSUB and GPOS tables, with tables built in Liberation Sans.</summary>
public sealed class ShaperNormTests
{
    /// <summary>
    /// GSUB 21.4.6 (and its example): the records of a context rule name places in the input sequence as the records before them left
    /// it. Rule „a b c d“ with records (0: ligature a b → X) and (2: d → Y): after the ligature the sequence is „X c d“, place 2 is d.
    /// Before, the places were counted in the sequence as matched, place 2 was c, and d stayed (docs/FEHLERGEDAECHTNIS.md, No. 1142).
    /// </summary>
    [Fact]
    public void ContextRecords_CountPlacesInTheChangedSequence()
    {
        var font = OpenTypeFont.Parse(ShaperLimitTests.Liberation);
        int G(char c) => font.GlyphForUnicode(c);
        var (shaper, _) = ShaperLimitTests.With(a => ShaperLimitTests.Gsub(1, [0], t =>
        {
            t.U16(3).U16(8).U16(8 + 54).U16(8 + 54 + 32);                       // three lookups
            // lookup 0: context substitution, format 3 over a b c d, records (0 → lookup 1) and (2 → lookup 2)
            t.U16(5).U16(0).U16(1).U16(8);
            t.U16(3).U16(4).U16(2).U16(22).U16(28).U16(34).U16(40).U16(0).U16(1).U16(2).U16(2);
            foreach (var c in "abcd") t.U16(1).U16(1).U16(G(c));
            // lookup 1: ligature a b → X
            t.U16(4).U16(0).U16(1).U16(8);
            t.U16(1).U16(8).U16(1).U16(14).U16(1).U16(1).U16(G('a')).U16(1).U16(4).U16(G('X')).U16(2).U16(G('b'));
            // lookup 2: d → Y
            t.U16(1).U16(0).U16(1).U16(8);
            t.U16(2).U16(8).U16(1).U16(G('Y')).U16(1).U16(1).U16(G('d'));
        }));
        Assert.Equal([G('X'), G('c'), G('Y')], shaper.Shape("abcd").Select(g => g.Glyph).ToArray());
    }
}
