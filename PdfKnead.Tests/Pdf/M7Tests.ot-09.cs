namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Konformitätsabgleich M7, package ot-09 (OpenType, CFF2 table: charstring basics 8.10.3 — hints, paths, subroutines — and the structure of a
/// charstring 8.10.4): the tests the sentence-by-sentence drafts named as missing. PDF-Knead reads the CFF2 table in the default instance
/// (question 1, docs/FEHLERGEDAECHTNIS.md, No. 1158). The CFF2 builder and the assertions are in the file of package ot-08.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (package ot-09)

    /// <summary>
    /// The probes of ot-09: fonts „OTTO“ with only a CFF2 table, one glyph per charstring given (glyph 0 an empty .notdef), and what each glyph
    /// draws (hints change no outline; subroutines in place of their calls, blends with their default values).
    /// </summary>
    private static Cff2ProbeOt08 ProbeOt09(string name)
    {
        Cff2ProbeOt08 Glyphs(params string[] g) => new(Cff2FontOt08(Cff2GlyphsOt08(g), g.Length + 1), g);
        // a variable font: fvar, one region, one ItemVariationData (k = 1)
        Cff2ProbeOt08 Variable(string glyph, string drawn) => new(Cff2FontOt08(Cff2Ot08(new([[], CsOt08(glyph)], Variations: OneRegionOt08)), 2,
            extra: new Dictionary<string, byte[]> { ["fvar"] = FvarOt08() }), [drawn]);
        Cff2ProbeOt08 Global(string glyph, string? drawn, params string[] subrs) =>
            new(Cff2FontOt08(Cff2Ot08(new([[], CsOt08(glyph)], GlobalSubrs: [.. subrs.Select(CsOt08)])), 2), [drawn]);
        const string triangle = "0 0 rmoveto 100 0 rlineto 0 100 rlineto";
        return name switch
        {
            "Hstem" => Glyphs("0 80 hstem " + triangle),
            "VstemBeforeHstem" => Glyphs("10 20 vstem 0 80 hstem " + triangle),
            "HstemWithHintmask" => Glyphs("0 80 100 80 hstem hintmask 0x80 " + triangle),
            "StemsDescending" => Glyphs("300 50 -200 50 hstem " + triangle),
            "BlendHstem" => Variable("0 80 10 1 blend hstem " + triangle, "0 80 hstem " + triangle),
            "EdgeHint20" => Glyphs("700 -20 hstem " + triangle),
            "EdgeHint21" => Glyphs("121 -21 hstem " + triangle),
            "BlendEdgeWidth" => Variable("700 -20 0 5 2 blend hstem " + triangle, "700 -20 hstem " + triangle),
            "ImplicitVstem" => Glyphs("0 80 hstemhm 10 20 hintmask 0xC0 " + triangle),
            // 200 numbers before one rlineto (the stack of CFF2 holds 513): drawn as 100 lines
            "Rlineto100" => new(Cff2FontOt08(Cff2GlyphsOt08("0 0 rmoveto " + string.Concat(Enumerable.Range(0, 100).Select(i => i % 2 == 0 ? "10 0 " : "0 10 ")) + "rlineto"), 2),
                ["0 0 rmoveto " + string.Concat(Enumerable.Range(0, 100).Select(i => i % 2 == 0 ? "10 0 rlineto " : "0 10 rlineto "))]),
            "Rlineto3" => Glyphs("0 0 rmoveto 10 0 0 10 -10 0 rlineto"),
            "FixedRrcurveto" => Glyphs("10 20 rmoveto 1.5 0 0 1.5 0 1.5 rrcurveto"),
            "TwoContours" => Glyphs("0 0 rmoveto 100 0 rlineto 0 100 rlineto 50 50 rmoveto 100 0 rlineto 0 100 rlineto"),
            "Hole" => Glyphs("0 0 rmoveto 300 0 rlineto 0 300 rlineto -300 0 rlineto 100 -100 rmoveto 0 -100 rlineto 100 0 rlineto 0 100 rlineto"),
            "NestedSameDirection" => Glyphs("0 0 rmoveto 300 0 rlineto 0 300 rlineto -300 0 rlineto 100 -200 rmoveto 100 0 rlineto 0 100 rlineto -100 0 rlineto"),
            "Flex" => Glyphs("0 0 rmoveto 100 4 100 4 100 0 100 0 100 -4 100 -4 50 flex"),
            "GlobalSubrPartial" => Global("0 0 -107 callgsubr", "0 0 rmoveto 100 0 rlineto", "rmoveto 100 0 rlineto"),
            // a subroutine calling itself: beyond the nesting limit the glyph is invalid and empty
            "RecursiveSubr" => Global("-107 callgsubr", null, "-107 callgsubr"),
            "EmptyGlobalLocalSubrs" => new(Cff2FontOt08(Cff2Ot08(new([[], CsOt08("-107 callsubr")], Privates: [new([], [CsOt08(triangle)])])), 2), [triangle]),
            "Bias1131" => new(Cff2FontOt08(Cff2Ot08(new([[], CsOt08("-1131 callgsubr")],
                GlobalSubrs: [CsOt08(triangle), .. Enumerable.Range(1, 1239).Select(_ => CsOt08("0 0 rmoveto"))])), 2), [triangle]),
            "SubrLeavesOperands" => Global("0 0 rmoveto -107 callgsubr rlineto", "0 0 rmoveto 100 0 rlineto", "100 0"),
            "SubrChain" => Global("0 0 rmoveto -107 callgsubr", "0 0 rmoveto 100 0 rlineto 0 100 rlineto", "100 0 rlineto -106 callgsubr", "0 100 rlineto"),
            "FullStructure" => Glyphs("0 80 hstemhm 10 20 vstemhm hintmask 0xC0 0 0 rmoveto 100 0 rlineto hintmask 0x40 0 100 rlineto"),
            "BlendRlineto" => Variable("0 0 rmoveto 100 10 1 blend 0 rlineto 0 100 rlineto", "0 0 rmoveto 100 0 rlineto 0 100 rlineto"),
            "SubrWithHintsAndOperands" => Global("-107 callgsubr 0 rlineto 0 100 rlineto", "0 80 hstemhm 0 0 rmoveto 100 0 rlineto 0 100 rlineto",
                "0 80 hstemhm 0 0 rmoveto 100"),
            _ => throw new ArgumentException(name),
        };
    }

    // ------------------------------------------------------------ 8.10.3 and 8.10.4

    /// <summary>
    /// OT 8.10.3.1 (hints: stems, hintmask, edge hints, implicit vstem, blended stems), 8.10.3.2 (paths: operand sets, relative coordinates with
    /// 16.16 values, implicit closing, winding, overlapping contours, flex), 8.10.3.3 (local and global subroutines: partial operand sets,
    /// recursion, empty GlobalSubrINDEX, bias 1131 at 1 240 subroutines, operands left on the stack, a chain of returns) and 8.10.4 (structure of a
    /// charstring, blend in path operators, any part in a subroutine) — reader and redaction (question 1): each glyph draws what the same
    /// charstring draws as Type 2, with the subroutines in place of their calls and each blend leaving its default values; a subroutine calling
    /// itself makes the glyph empty (nesting limit); the redaction writes the glyphs as drawn into a static CFF2 table.
    /// </summary>
    [Theory]
    [InlineData("Hstem")]                    // 8.10.3.1 1–5
    [InlineData("VstemBeforeHstem")]         // 8.10.3.1 6–11
    [InlineData("HstemWithHintmask")]        // 8.10.3.1 12–19
    [InlineData("StemsDescending")]          // 8.10.3.1 20–26
    [InlineData("BlendHstem")]               // 8.10.3.1 31–32
    [InlineData("EdgeHint20")]               // 8.10.3.1 33–39
    [InlineData("EdgeHint21")]               // 8.10.3.1 40–49
    [InlineData("BlendEdgeWidth")]           // 8.10.3.1 61–62
    [InlineData("ImplicitVstem")]            // 8.10.3.1 63–65
    [InlineData("Rlineto100")]               // 8.10.3.2 1–2
    [InlineData("Rlineto3")]                 // 8.10.3.2 3–8
    [InlineData("FixedRrcurveto")]           // 8.10.3.2 9–14
    [InlineData("TwoContours")]              // 8.10.3.2 15–21
    [InlineData("Hole")]                     // 8.10.3.2 22–26
    [InlineData("NestedSameDirection")]      // 8.10.3.2 27–35
    [InlineData("Flex")]                     // 8.10.3.2 36–42
    [InlineData("GlobalSubrPartial")]        // 8.10.3.3 1–8
    [InlineData("RecursiveSubr")]            // 8.10.3.3 9–14
    [InlineData("EmptyGlobalLocalSubrs")]    // 8.10.3.3 15–18
    [InlineData("Bias1131")]                 // 8.10.3.3 19–27
    [InlineData("SubrLeavesOperands")]       // 8.10.3.3 28–30
    [InlineData("SubrChain")]                // 8.10.3.3 31–37
    [InlineData("FullStructure")]            // 8.10.4 1–10
    [InlineData("BlendRlineto")]             // 8.10.4 11–13
    [InlineData("SubrWithHintsAndOperands")] // 8.10.4 14
    public void Cff2CharStringBasics_AreRead_DefaultInstanceDrawn(string probe) => AssertCff2ReadOt08(ProbeOt09(probe));
}
