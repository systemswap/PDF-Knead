using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-36 (OpenType specification, overview of font variations 41–41.9): the tests the sentence-by-sentence drafts named as missing.
/// PDF-Knead draws every variable font in its default instance, where every normalized coordinate is 0; the regions of a CFF2 VariationStore
/// are not evaluated, and a CFF2 font for new text is saved as a CID-keyed CFF. The CFF2 builders are in the files of the packages ot-08 and
/// ot-11.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (package ot-36)

    /// <summary>
    /// The probes of ot-36: the CFF2 table shaped like the example of 8.13 (both charstrings call the subroutine that draws the rectangle
    /// 50 … 550 × 0 … 500 with blended x values, k = 2), with an fvar beside it, but with two regions that break the rules of a valid region.
    /// </summary>
    private static Cff2ProbeOt08 ProbeOt36(string name)
    {
        Cff2VariationsOt08 regions = name switch
        {
            // start 0.5 above the peak 0.2 on the one axis; the second region runs from −1 to +1 across zero with its peak at 1
            "StartAbovePeak" => new([(0.5, 0.2, 1), (-1, 1, 1)], [[0, 1]]),
            // both regions across zero: −1 … +1 peaking at −0.5 and at 1
            "AcrossZero" => new([(-1, -0.5, 1), (-1, 1, 1)], [[0, 1]]),
            _ => throw new ArgumentException(name),
        };
        var font = Cff2FontOt08(ExampleCff2Ot11(regions), 2, extra: new Dictionary<string, byte[]> { ["fvar"] = FvarOt08() });
        return new(font, [ExampleRectOt11], ExampleRectOt11);
    }

    // ------------------------------------------------------------ 41.7 interpolation

    /// <summary>
    /// OT 41.7 sentences 17–19 (reader): a valid region has start ≤ peak ≤ end on every axis and does not reach across zero — PDF-Knead does not
    /// evaluate regions in the default instance. The CFF2 table of the example with one region whose start (0.5) lies above its peak (0.2) and
    /// one from −1 to +1 across zero draws the default values (the rectangle 50 … 550 × 0 … 500 in both glyphs), without an exception, and is
    /// scrubbed into a static CFF2 table as with valid regions.
    /// </summary>
    [Theory]
    [InlineData("StartAbovePeak")]
    [InlineData("AcrossZero")]
    public void Cff2InvalidRegions_DefaultInstanceDrawn(string probe) => AssertCff2ReadOt08(ProbeOt36(probe));

    // ------------------------------------------------------------ 41.9 instances

    /// <summary>
    /// OT 41.9 sentences 10–12 (writer): an instance of a CFF2 font written as a table „CFF “ is CID-keyed with the ROS Adobe-Identity-0 when it
    /// has several Font DICTs, and may be either name-keyed or CID-keyed with one — PDF-Knead always writes it CID-keyed: a handwriting font
    /// whose glyf is replaced by a CFF2 table (one Font DICT) used for new text is saved as a CFF whose Top DICT names the ROS (Adobe,
    /// Identity, 0), with the same CIDSystemInfo in the CIDFont, and whose CID of „M“ is its glyph number and draws the same outline.
    /// </summary>
    [Fact]
    public void Cff2Font_ForNewText_SavedCidKeyedWithAdobeIdentity0()
    {
        var source = EmbeddedFonts.LoadHandwriting("PatrickHand-Regular.ttf");
        var plain = OpenTypeFont.Parse(source);
        var data = ColorFontBuilder.WithTables(source, new Dictionary<string, byte[]?> { ["CFF2"] = RectanglesCff2Ot11(plain.GlyphCount), ["glyf"] = null, ["loca"] = null });
        var face = ElementFace.For("M7-ot-36-CFF2-" + Guid.NewGuid().ToString("N"), () => OpenTypeFont.Parse(data), false, false, false);
        var m = face.Font.GlyphForUnicode('M');
        Assert.True(m > 0);
        var cidFont = Iso03Descendant(face.SavedFont(new Dictionary<int, string> { [m] = "M" }));
        var info = cidFont.GetDictionary(PdfName.Get("CIDSystemInfo"))!;
        Assert.Equal("Adobe", info.GetString(PdfName.Get("Registry"))!.ToText());
        Assert.Equal("Identity", info.GetString(PdfName.Get("Ordering"))!.ToText());
        Assert.Equal(0L, info.GetInteger(PdfName.Get("Supplement")));
        var program = cidFont.GetDictionary(PdfName.Get("FontDescriptor"))!.GetStream(PdfName.Get("FontFile3"))!;
        var cff = Iso03CffProgram(program);
        var top = Iso03CffTop(cff);
        Assert.Equal(("Adobe", "Identity", 0), (top.Registry, top.Ordering, top.Supplement));
        var parsed = CffFont.Parse(cff);
        Assert.True(parsed.IsCidKeyed);
        SameOutlineOt08(face.Font.Outline(m), parsed.Outline(parsed.GlyphByCid(m)), "M gespeichert");
    }
}
