using PdfKnead.Core.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, packages ot-45 (OpenType: STAT) and ot-48 (OpenType: the feature tags). Every writing path makes a variable font static and leaves STAT
/// out together with fvar (docs/FEHLERGEDAECHTNIS.md, No. 1159; the TrueType rebuild in
/// M7NormTests.VariationTables_LeftOutBySubsetCompressionScrubAndRebuild); the test here adds the CFF2 branch of the scrubber. Feature tags are
/// compared byte for byte, and a tag a vendor registered for itself is applied only when the caller names it — PDF-Knead names none.
/// Fonts: Liberation Sans with a GSUB or GPOS built by hand, and a CFF2 font built by hand.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>
    /// OT 57.1 sentence 18: every variable font has a STAT table — the CFF2 font with fvar and STAT, scrubbed keeping all its glyphs or only
    /// glyph 0, has neither fvar nor STAT any more: it is the static default instance.
    /// </summary>
    [Fact]
    public void Ot45_Cff2FontWithFvarAndStat_ScrubbedWithoutBoth()
    {
        var font = Cff2FontOt08(Cff2GlyphsOt08("100 0 rmoveto 200 0 rlineto 0 200 rlineto"), 2,
            extra: new Dictionary<string, byte[]> { ["fvar"] = FvarOt08(), ["STAT"] = Ot18Stat() });
        var parsed = OpenTypeFont.Parse(font);
        Assert.True(parsed.HasTable("fvar"));
        Assert.True(parsed.HasTable("STAT"));
        foreach (var keep in new[] { new[] { 0, 1 }, new[] { 0 } })
        {
            var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(font, keep));
            Assert.False(scrubbed.HasTable("fvar"), $"fvar, behalten {string.Join(",", keep)}");
            Assert.False(scrubbed.HasTable("STAT"), $"STAT, behalten {string.Join(",", keep)}");
            Assert.True(scrubbed.HasTable("CFF2"));
        }
    }

    /// <summary>
    /// OT 63 sentences 6–7: tags are four ASCII characters, registered feature tags four lower-case letters by convention — a lookup a→x under
    /// „LIGA“ is not the feature „liga“: „a“ stays a; the same lookup under „liga“ makes it x.
    /// </summary>
    [Fact]
    public void Ot48_FeatureTag_ComparedWithCase()
    {
        var upper = Ot03Shaper(Ot03Header([Ot03Single(('a', 'x'))], [("LIGA", [0])]));
        Assert.Equal(Ot03Glyphs("a"), Ot03Shape(upper, "a"));
        var lower = Ot03Shaper(Ot03Header([Ot03Single(('a', 'x'))], [("liga", [0])]));
        Assert.Equal(Ot03Glyphs("x"), Ot03Shape(lower, "a"));
    }

    /// <summary>
    /// OT 63 sentences 9–12: tags of four capitals are kept for vendors (example PKRN) — a single adjustment of −50 for „A“ under „PKRN“ is not
    /// applied by default; only when the caller names the tag is the advance 50 smaller.
    /// </summary>
    [Fact]
    public void Ot48_VendorFeature_OnlyWhenNamed()
    {
        var a = Ot03G('A');
        var single = Ot03Lookup(1, 0, new Ot03Part().U16(1).Off(Ot03Cov(a)).U16(4, -50));
        var shaper = Ot03GposShaper(Ot03Header([single], [("PKRN", [0])]));
        Assert.Equal(Ot03Font.AdvanceWidth(a), shaper.Shape("A")[0].XAdvance);
        Assert.Equal(Ot03Font.AdvanceWidth(a) - 50, shaper.Shape("A", ["PKRN"])[0].XAdvance);
    }
}
