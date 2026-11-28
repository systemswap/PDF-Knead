using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;

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

    private static readonly OpenTypeFont Font = OpenTypeFont.Parse(ShaperLimitTests.Liberation);

    private static int G(char c) => Font.GlyphForUnicode(c);

    /// <summary>
    /// GSUB 21.4.2 and Common Table Formats 2.8.2 (No. 1144): a single substitution of format 2 names its substitute by the coverage index —
    /// an index beyond glyphCount substitutes nothing (before, it read the coverage behind the array as a glyph); a context rule of no
    /// glyphs never matches (before, it matched and applied its records).
    /// </summary>
    [Fact]
    public void IndexBeyondItsArrayAndRuleOfNoGlyphs_ChangeNothing()
    {
        // lookup 0: single substitution format 2, coverage {a, b}, one substitute (X for a)
        var (single, _) = ShaperLimitTests.With(_ => ShaperLimitTests.Gsub(1, [0], t =>
        {
            t.U16(1).U16(4);
            t.U16(1).U16(0).U16(1).U16(8);
            t.U16(2).U16(8).U16(1).U16(G('X')).U16(1).U16(2).U16(G('a')).U16(G('b'));
        }));
        Assert.Equal([G('X'), G('b')], single.Shape("ab").Select(g => g.Glyph).ToArray());
        // lookup 0: context format 1 for a, one rule of glyph count 0 with the record (0 → lookup 1: a → X)
        var (context, _) = ShaperLimitTests.With(_ => ShaperLimitTests.Gsub(1, [0], t =>
        {
            t.U16(2).U16(6).U16(6 + 36);
            t.U16(5).U16(0).U16(1).U16(8);
            t.U16(1).U16(8).U16(1).U16(14).U16(1).U16(1).U16(G('a'));          // format 1, coverage at 8, one rule set at 14
            // set: one rule at +4; rule: 0 glyphs and two records — read from the wrong place, the second is (0 → 1)
            t.U16(1).U16(4).U16(0).U16(2).U16(0).U16(0).U16(1);
            t.U16(1).U16(0).U16(1).U16(8);
            t.U16(2).U16(8).U16(1).U16(G('X')).U16(1).U16(1).U16(G('a'));
        }));
        Assert.Equal([G('a')], context.Shape("a").Select(g => g.Glyph).ToArray());
    }

    /// <summary>GPOS with one lookup of the type given and its subtable (offsets from the lookup's start), under „kern“.</summary>
    private static TextShaper Gpos(int type, Action<ShaperLimitTests.Table> subtable) =>
        ShaperLimitTests.WithGpos(ShaperLimitTests.Gsub(1, [0], t =>
        {
            t.U16(1).U16(4);
            t.U16(type).U16(0).U16(1).U16(8);
            subtable(t);
        }, "kern"));

    /// <summary>
    /// GPOS 20.4.2 (No. 1144): a single positioning of format 2 names its value record by the coverage index — an index beyond valueCount
    /// changes nothing (before, it read the coverage behind the records as a value).
    /// </summary>
    [Fact]
    public void SinglePositioning_IndexBeyondValueCount_ChangesNothing()
    {
        // format 2, coverage at 10, value format 4 (XAdvance), one value (+100) for a; b is covered too
        var shaper = Gpos(1, t => t.U16(2).U16(10).U16(4).U16(1).U16(100).U16(1).U16(2).U16(G('a')).U16(G('b')));
        var shaped = shaper.Shape("ab");
        Assert.Equal(Font.AdvanceWidth(G('a')) + 100, shaped[0].XAdvance);
        Assert.Equal(Font.AdvanceWidth(G('b')), shaped[1].XAdvance);
    }

    /// <summary>
    /// GPOS 20.4.11.2 and 20.4.11.3 (No. 1144): an anchor has format 1, 2 or 3, and a mark record with anchor offset 0 has no anchor — the
    /// mark stays where it is (before, the mark array or an anchor of another format were read as coordinates and the mark moved).
    /// </summary>
    [Theory]
    [InlineData(0, 1)]   // mark anchor offset 0
    [InlineData(6, 7)]   // mark anchor of format 7
    public void MarkWithoutUsableAnchor_StaysWhereItIs(int markAnchorOffset, int markAnchorFormat)
    {
        var acute = Font.GlyphForUnicode('\u0301');
        // MarkBasePos format 1: mark coverage at 12, base coverage at 18, one class, mark array at 24, base array at 36
        var shaper = Gpos(4, t =>
        {
            t.U16(1).U16(12).U16(18).U16(1).U16(24).U16(36);
            t.U16(1).U16(1).U16(acute);                                   // mark coverage
            t.U16(1).U16(1).U16(G('x'));                                  // base coverage (x: no composed character with the acute, No. 1197)
            t.U16(1).U16(0).U16(markAnchorOffset);                       // mark array: class 0, its anchor at +6 (or NULL)
            t.U16(markAnchorFormat).U16(0).U16(500);                     // the mark anchor
            t.U16(1).U16(4);                                              // base array: one record, its anchor at +4
            t.U16(1).U16(300).U16(700);                                  // base anchor (format 1)
        });
        Assert.Equal(0, shaper.Shape("x\u0301")[1].XOffset);
    }
}
