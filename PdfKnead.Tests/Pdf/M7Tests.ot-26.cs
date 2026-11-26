using PdfKnead.Core.Fonts.Layout;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-26: OpenType GSUB 21.5 (examples 1–10), the sentences with a requirement of their own, checked on <see cref="TextShaper"/>
/// with GSUB tables like the examples built into Liberation Sans (parts of the ot-03 tests).
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>
    /// OT 21.5.3 sentence 5: the order of the glyphs in the coverage matches the order of the substitute array — the substitute of coverage
    /// index k stands at place k. A lookup like example 3 (type 1 format 2, coverage format 1 of the four glyphs of „( ) [ ]“, the substitutes
    /// of „A B C D“ in the order of the coverage) under „vert“ switched on gives exactly the substitutes in array order. The draft's table was
    /// built through the subset writer; here it goes into Liberation Sans as the other GSUB tests do.
    /// </summary>
    [Fact]
    public void Ot26_SingleFormat2_SubstitutesInTheOrderOfTheCoverage()
    {
        var inputs = Ot03Glyphs("()[]");
        var substitutes = Ot03Glyphs("ABCD");
        var byGlyph = inputs.Zip(substitutes).OrderBy(p => p.First).ToArray();
        var sub = new Ot03Part().U16(2).Off(Ot03Cov(inputs)).U16(4).U16([.. byGlyph.Select(p => p.Second)]);
        var shaper = Ot03Shaper(Ot03Header([Ot03Lookup(1, 0, sub)], [("vert", [0])]));
        Assert.Equal(Ot03Glyphs("ABCD"), Ot03Shape(shaper, "()[]", ["vert"]));
        Assert.Equal(Ot03Glyphs("()[]"), Ot03Shape(shaper, "()[]"));
    }

    /// <summary>
    /// A GSUB like example 7: lookup 0 (in „liga“) is a context of format 1 with the rule sets for space (rule: space, hyphen; record at place
    /// <paramref name="spaceRecord"/>) and hyphen (rule: hyphen, space; record at place 1), both naming lookup 1, which replaces the space by
    /// the thin space (U+2009) and is reached only through the records.
    /// </summary>
    private static TextShaper Ot26FrenchSpacing(int spaceRecord = 0)
    {
        int space = Ot03G(' '), hyphen = Ot03G('-');
        Assert.True(space < hyphen);
        var sub = Ot03ContextF1(Ot03Cov(space, hyphen), Ot03Set(Ot03Rule([hyphen], (spaceRecord, 1))), Ot03Set(Ot03Rule([space], (1, 1))));
        return Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, sub), Ot03Single((' ', ' '))));
    }

    /// <summary>
    /// OT 21.5.7 sentence 10: the record of the first rule names the place in the sequence (0) and the lookup that replaces the space there.
    /// „space hyphen“ → thin space, hyphen; with the record at place 5, outside the sequence, nothing changes.
    /// </summary>
    [Fact]
    public void Ot26_ContextFormat1_RecordNamesPlaceAndLookup()
    {
        Assert.Equal(Ot03Glyphs(" -"), Ot03Shape(Ot26FrenchSpacing(), " -"));
        Assert.Equal(Ot03Glyphs(" -"), Ot03Shape(Ot26FrenchSpacing(5), " -"));
    }

    /// <summary>
    /// OT 21.5.7 sentence 13: the record of the second rule names its place (1) and the same lookup. „hyphen space“ → hyphen, thin space;
    /// „space hyphen space“ → thin space, hyphen, space: after the first rule the shaper goes on behind its input, so the hyphen cannot start
    /// the second rule (the continuation rule of the common formats, which the example's text overlooks).
    /// </summary>
    [Fact]
    public void Ot26_ContextFormat1_SecondRuleSameLookup_GoesOnBehindTheInput()
    {
        Assert.Equal(Ot03Glyphs("- "), Ot03Shape(Ot26FrenchSpacing(), "- "));
        Assert.Equal(Ot03Glyphs(" - "), Ot03Shape(Ot26FrenchSpacing(), " - "));
    }
}
