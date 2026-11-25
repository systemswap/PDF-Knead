using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, Adobe TN 5014 (CMap and CIDFont files), sections 6–7 (draft cid-03): the operators of rearranged fonts, which PDF does not have, and the
/// CMap operators as CMap.Parse reads them (bfchar/bfrange targets, codespace, notdef, usecmap).
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>A CMap program around <paramref name="body"/> (no codespace of its own unless the body has one).</summary>
    private static CMap Cid03Parse(string body) =>
        CMap.Parse(Encoding.ASCII.GetBytes($"/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n{body}\nendcmap CMapName currentdict /CMap defineresource pop end end"));

    /// <summary>
    /// CID 6.1 sentences 1–11, 6.2 sentences 13–17, 33–37, 38–44, 45–50, 52–63, 66 and 70, 71–77, 98–100, 7.1 sentences 7 and 23, 7.4 sentences
    /// 77–92, 93–105, 113–117: rearranged fonts (beginrearrangedfont, usematrix, usefont) do not exist in PDF. CMap.Parse of a CMap built of them —
    /// no exception, the cidchar inside still read (41 → CID 5, 42 → CID 0); the encoding CMap PdfACMaps.Encoding writes contains none of these
    /// operators.
    /// </summary>
    [Fact]
    public void Cid03_RearrangedFontOperators_AreSkipped()
    {
        var cmap = Cid03Parse("/X [/A /B] beginrearrangedfont 1 beginusematrix [1 0 0 1 0 0.15] endusematrix 1 usefont 1 begincodespacerange <00> <FF> " +
                              "endcodespacerange 1 begincidchar <41> 5 endcidchar endrearrangedfont");
        Assert.Equal(5, cmap.ToCid(0x41, 1));
        Assert.Equal(0, cmap.ToCid(0x42, 1));
        var text = Encoding.ASCII.GetString(PdfACMaps.Encoding("Probe-H", "Adobe", "Identity", 0, 0, [(0x41, 1, 5), (0x8140, 2, 700)]).RawData!);
        foreach (var word in (string[])["rearrangedfont", "usematrix", "usefont"])
            Assert.DoesNotContain(word, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// CID 7.1 sentences 15–22, 7.4 sentences 3–10 and 11–22: a bfchar or bfrange target may be a glyph name. „1 beginbfchar &lt;41&gt; /Adieresis
    /// endbfchar“ gives „Ä“; a bfrange with the array [/A /B] gives „A“ and „B“ (CMap.UnicodeOf).
    /// </summary>
    [Fact]
    public void Cid03_ToUnicode_NameTargets_GiveTheirCharacters()
    {
        Assert.Equal("Ä", Cid03Parse("1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfchar <41> /Adieresis endbfchar").ToUnicode(0x41, 1));
        var range = Cid03Parse("1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfrange <20> <21> [/A /B] endbfrange");
        Assert.Equal("A", range.ToUnicode(0x20, 1));
        Assert.Equal("B", range.ToUnicode(0x21, 1));
    }

    /// <summary>CID 7.1 sentences 24–31: a string in parentheses is a token of a CMap — „1 beginbfchar &lt;41&gt; (B) endbfchar“ gives „B“ (CMap.Tokenizer).</summary>
    [Fact]
    public void Cid03_ToUnicode_LiteralStringTarget_IsRead()
    {
        Assert.Equal("B", Cid03Parse("1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfchar <41> (B) endbfchar").ToUnicode(0x41, 1));
    }

    /// <summary>
    /// CID 7.4 sentences 3–10: at most 100 entries per beginbfchar set. ElementFace.ToUnicode with 250 glyphs writes three sets of 100, 100 and 50,
    /// and CMap.Parse reads all 250.
    /// </summary>
    [Fact]
    public void Cid03_ElementToUnicode_WritesSetsOfAtMostHundred_AllRead()
    {
        var glyphs = Enumerable.Range(1, 250).ToDictionary(g => g, g => ((char)(0x4E00 + g)).ToString());
        var stream = ElementFace.ToUnicode(glyphs);
        var text = Encoding.ASCII.GetString(stream.RawData!);
        var sets = System.Text.RegularExpressions.Regex.Matches(text, @"(\d+) beginbfchar\n(.*?)endbfchar", System.Text.RegularExpressions.RegexOptions.Singleline).ToList();
        Assert.Equal(["100", "100", "50"], sets.Select(m => m.Groups[1].Value));
        var cmap = CMap.Parse(stream.RawData!);
        foreach (var (glyph, value) in glyphs) Assert.Equal(value, cmap.ToUnicode((uint)glyph, 2));
    }

    /// <summary>
    /// CID 7.4 sentences 43–52: the codespace ranges decide the length of every code. PdfACMaps.Codespace of the codes 41 (one byte), 0102 and 0305
    /// (two bytes): the ranges 41–41, 0100–01FF and 0300–03FF.
    /// </summary>
    [Fact]
    public void Cid03_WrittenCodespace_PerLengthAndFirstByte()
    {
        var ranges = PdfACMaps.Codespace([(0x41, 1), (0x0102, 2), (0x0305, 2)]);
        Assert.Equal([(1, 0x41u, 0x41u), (2, 0x0100u, 0x01FFu), (2, 0x0300u, 0x03FFu)], ranges);
    }

    /// <summary>
    /// CID 7.4 sentences 57–59: a code outside the codespace is invalid and takes CID 0 in the text. Measured (FG 1141, finding B59): two of three
    /// reference readers show the CID a range gives such a code; PdfKnead keeps it so (noted for the special rules). Codespace 00–7F with cidrange
    /// 00–FF → 10: code 90 gives CID 154 (0x90 + 10) — today's behaviour.
    /// </summary>
    [Fact]
    public void Cid03_CodeOutsideTheCodespace_KeepsTheCidOfARange()
    {
        var cmap = Cid03Parse("1 begincodespacerange <00> <7F> endcodespacerange 1 begincidrange <00> <FF> 10 endcidrange");
        Assert.False(cmap.InCodespace(0x90, 1));
        Assert.Equal(0x90 + 10, cmap.ToCid(0x90, 1));
    }

    /// <summary>
    /// CID 7.4 sentences 63–68 and 73–76: a notdef range gives all its codes one CID. 90ms-RKSJ-H maps 00–1F by a notdef range to CID 231: code 05
    /// gives 231 (read since FG 1141; the embedded case: CMapNormTests.NotdefMappings_GiveCodesWithoutAMappingTheirCid).
    /// </summary>
    [Fact]
    public void Cid03_PredefinedCMap_NotdefRangeGivesItsCid()
    {
        var rksj = PredefinedCMaps.Get("90ms-RKSJ-H")!;
        Assert.Equal(231, rksj.ToCid(0x05, 1));
        Assert.Equal(231, rksj.ToCid(0x1F, 1));
        Assert.Equal(231, rksj.ToCid(0x20, 1));   // (the cidrange 20–7D begins with 231 as well)
    }

    /// <summary>
    /// CID 7.4 sentences 106–110 and 112: usecmap takes the mappings of another CMap, which may use another in turn. A chain of six embedded CMaps
    /// through /UseCMap (each with codespace 00–FF): if only the fifth maps 41 (to CID 5), 41 gives 5; if only the sixth maps it, CID 0 — streams
    /// are followed at most four levels deep (CompositeFont.ParseUsing).
    /// </summary>
    [Theory]
    [InlineData(5, 5)]
    [InlineData(6, 0)]
    public void Cid03_UseCMapChain_IsFollowedFourLevelsDeep(int mapping, int expected)
    {
        const string info = "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Muster /Encoding 5 0 R /DescendantFonts [4 0 R] >>",
            $"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Muster {info} /DW 1000 >>",
        };
        for (var level = 1; level <= 6; level++)
        {
            var body = level == mapping ? "1 begincidchar <41> 5 endcidchar " : "";
            var program = $"/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /K{level} def 1 begincodespacerange <00> <FF> " +
                          $"endcodespacerange {body}endcmap CMapName currentdict /CMap defineresource pop end end";
            var use = level < 6 ? $"/UseCMap {5 + level} 0 R " : "";
            objects.Add($"<< /Type /CMap /CMapName /K{level} {info} {use}/Length {program.Length} >>\nstream\n{program}\nendstream");
        }
        using var doc = PdfDocument.Open(DocumentStructureNormTests.FileOf([.. objects]));
        var font = PdfFont.Get((PdfDictionary)doc.GetObject(3), doc);
        Assert.Equal(expected, font.Cid(0x41, 1));
    }
}
