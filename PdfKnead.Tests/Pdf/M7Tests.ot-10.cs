namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Konformitätsabgleich M7, package ot-10 (OpenType, CFF2 table: the charstring operators 8.10.5 with 8.10.5.1–8.10.5.5): the tests the
/// sentence-by-sentence drafts named as missing. PDF-Knead reads the CFF2 table in the default instance (question 1, docs/FEHLERGEDAECHTNIS.md,
/// No. 1158). The CFF2 builder and the assertions are in the file of package ot-08.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (package ot-10)

    /// <summary>
    /// The probes of ot-10: fonts „OTTO“ with only a CFF2 table, one glyph per charstring given (glyph 0 an empty .notdef), and what each glyph
    /// draws (by default the charstring itself; subroutines in place of their calls, blends with their default values).
    /// </summary>
    private static Cff2ProbeOt08 ProbeOt10(string name)
    {
        Cff2ProbeOt08 Glyphs(params string[] g) => new(Cff2FontOt08(Cff2GlyphsOt08(g), g.Length + 1), g);
        // with fvar and a variation store, so that blend and vsindex have regions to refer to
        Cff2ProbeOt08 Variable(Cff2VariationsOt08 v, string[] glyphs, string?[] drawn, string[]? globals = null, string[]? locals = null) =>
            new(Cff2FontOt08(Cff2Ot08(new([[], .. glyphs.Select(CsOt08)], GlobalSubrs: globals?.Select(CsOt08).ToArray(),
                    Privates: [new([], locals?.Select(CsOt08).ToArray())], Variations: v)), glyphs.Length + 1,
                extra: new Dictionary<string, byte[]> { ["fvar"] = FvarOt08() }), drawn);
        const string line = "0 0 rmoveto 10 0 rlineto";
        // ascending stems: each 5 wide, 10 above the previous one
        string Stems(int count, string op) => string.Concat(Enumerable.Repeat("10 5 ", count)) + op;
        string[] paths = ["10 20 rmoveto", "10 hmoveto", "10 vmoveto", "0 0 rmoveto 10 0 rlineto", "0 0 rmoveto 10 hlineto", "0 0 rmoveto 10 vlineto",
            "0 0 rmoveto 10 0 10 10 0 10 rrcurveto", "0 0 rmoveto 10 10 10 0 hhcurveto", "0 0 rmoveto 10 10 10 10 vvcurveto",
            "0 0 rmoveto 10 10 10 10 hvcurveto", "0 0 rmoveto 10 10 10 10 vhcurveto", "0 0 rmoveto 10 0 10 10 0 10 5 5 rcurveline",
            "0 0 rmoveto 5 5 10 0 10 10 0 10 rlinecurve", "0 0 rmoveto 10 1 10 1 10 0 10 0 10 -1 10 -1 50 flex",
            "0 0 rmoveto 10 10 1 10 10 -1 10 hflex", "0 0 rmoveto 10 1 10 1 10 10 10 -1 10 hflex1",
            "0 0 rmoveto 10 1 10 1 10 0 10 -1 10 -1 10 flex1", "0 0 rmoveto 10 5 1 blend 0 rlineto"];
        return name switch
        {
            // a move alone draws nothing (the dangling move leaves the outline)
            "AllPathOperators" => Variable(OneRegionOt08, paths, [null, null, null, .. paths[3..^1], "0 0 rmoveto 10 0 rlineto"]),
            "OtherOperators" => Variable(TwoDataSetsOt08, [
                    "100 50 hstem 200 40 vstem " + line, "0 80 hstemhm 10 20 vstemhm hintmask 0xC0 cntrmask 0x40 " + line,
                    "0 vsindex 0 0 rmoveto 10 5 1 blend 0 rlineto -107 callsubr", "0 0 rmoveto -107 callgsubr rlineto"],
                ["100 50 hstem 200 40 vstem " + line, "0 80 hstemhm 10 20 vstemhm hintmask 0xC0 cntrmask 0x40 " + line,
                    "0 0 rmoveto 10 0 rlineto 0 10 rlineto", line],
                globals: ["10 0"], locals: ["0 10 rlineto"]),
            "FixedRmoveto" => Glyphs("1.5 2.25 rmoveto 10 0 rlineto"),
            "Movetos" => Glyphs("10 20 rmoveto 5 hmoveto 7 vmoveto 10 0 rlineto"),
            "Rlineto2" => Glyphs("0 0 rmoveto 10 0 0 10 rlineto"),
            "RlinetoOdd" => Glyphs("0 0 rmoveto 10 0 0 rlineto"),
            "HlinetoVlineto" => Glyphs("0 0 rmoveto 10 20 30 hlineto", "0 0 rmoveto 10 20 30 vlineto"),
            "Rrcurveto2" => Glyphs("0 0 rmoveto 10 0 10 10 0 10 -10 0 -10 -10 0 -10 rrcurveto"),
            "HhVvCurveto" => Glyphs("0 0 rmoveto 10 20 30 0 hhcurveto", "0 0 rmoveto 5 10 20 30 40 hhcurveto",
                "0 0 rmoveto 10 20 30 0 vvcurveto", "0 0 rmoveto 5 10 20 30 40 vvcurveto"),
            "HvVhCurveto" => Glyphs("0 0 rmoveto 10 10 10 10 hvcurveto", "0 0 rmoveto 10 10 10 10 5 hvcurveto",
                "0 0 rmoveto 10 10 10 10 10 10 10 10 hvcurveto", "0 0 rmoveto 10 10 10 10 10 10 10 10 5 hvcurveto",
                "0 0 rmoveto 10 10 10 10 vhcurveto", "0 0 rmoveto 10 10 10 10 5 vhcurveto",
                "0 0 rmoveto 10 10 10 10 10 10 10 10 vhcurveto", "0 0 rmoveto 10 10 10 10 10 10 10 10 5 vhcurveto"),
            "RcurvelineRlinecurve" => Glyphs("0 0 rmoveto 10 0 10 10 0 10 5 5 rcurveline", "0 0 rmoveto 5 5 10 0 10 10 0 10 rlinecurve"),
            "Flex" => Glyphs("0 0 rmoveto 100 4 100 4 100 0 100 0 100 -4 100 -4 50 flex"),
            "FlatFlex50" => Glyphs("0 0 rmoveto 100 1 100 0 100 0 100 0 100 0 100 -1 50 flex"),
            "Hflex" => Glyphs("0 0 rmoveto 100 100 4 100 100 -4 100 hflex"),
            "Hflex1" => Glyphs("0 0 rmoveto 100 2 100 2 100 100 100 -2 100 hflex1"),
            "Flex1" => Glyphs("0 0 rmoveto 100 2 100 2 100 0 100 -2 100 -2 100 flex1", "0 0 rmoveto 2 100 2 100 0 100 -2 100 -2 100 100 flex1"),
            "HstemVstem" => Glyphs("100 50 hstem 200 40 vstem " + line),
            "OverlappingHstemhm" => Glyphs("100 50 -20 50 hstemhm hintmask 0x80 " + line),
            "Hintmask17Stems" => Glyphs(Stems(17, "hstemhm") + " hintmask 0x40 0x40 0x00 " + line),
            "Cntrmask13Stems" => Glyphs(Stems(7, "hstem") + " " + Stems(6, "vstem") + " cntrmask 0xA8 0x00 " + line),
            "StemsWrongOrder" => Glyphs("200 40 vstem 300 50 -200 50 hstem " + line),
            "LocalSubr" => new(Cff2FontOt08(Cff2Ot08(new([[], CsOt08("-107 callsubr")], Privates: [new([], [CsOt08(line)])])), 2), [line]),
            "SubrLeavesOperands" => new(Cff2FontOt08(Cff2Ot08(new([[], CsOt08("0 0 rmoveto -107 callgsubr rlineto")], GlobalSubrs: [CsOt08("10 0")])), 2), [line]),
            // vsindex 1: k = 2, so „10 5 6 1 blend“ leaves 10
            "CharStringVsindex" => Variable(TwoDataSetsOt08, ["1 vsindex 10 5 6 1 blend 0 rmoveto 10 0 rlineto"], ["10 0 rmoveto 10 0 rlineto"]),
            "BlendRmoveto" => Variable(OneRegionOt08, ["10 20 5 -5 2 blend rmoveto 10 0 rlineto"], ["10 20 rmoveto 10 0 rlineto"]),
            "SubrBlendOwnVsindex" => Variable(TwoDataSetsOt08, ["1 vsindex -107 callgsubr"], ["10 0 rmoveto 10 0 rlineto"],
                globals: ["10 5 6 1 blend 0 rmoveto 10 0 rlineto"]),
            _ => throw new ArgumentException(name),
        };
    }

    // ------------------------------------------------------------ 8.10.5

    /// <summary>
    /// OT 8.10.5.1 (the operators and whether they blend and clear the stack), 8.10.5.2 (path operators: integer and 16.16 operands, the moveto,
    /// lineto and curveto families with their operand counts and optional extra operands, flex with a flat bend, hflex, hflex1, flex1 mostly
    /// horizontal and mostly vertical), 8.10.5.3 (hstem, vstem, overlapping hstemhm with hintmask, hintmask with 17 stems = three bytes, cntrmask
    /// with 13 stems = two bytes, stems in the wrong order), 8.10.5.4 (callsubr, callgsubr leaving operands) and 8.10.5.5 (vsindex before blend,
    /// blend with k = 1, a subroutine blending after the vsindex of its caller) — reader and redaction (question 1): each glyph draws the path the
    /// same charstring draws as Type 2 (subroutines in place of their calls, each blend with its default values, vsindex choosing k), and the
    /// redaction writes the glyphs as drawn into a static CFF2 table.
    /// </summary>
    [Theory]
    [InlineData("AllPathOperators")]     // 8.10.5.1 3–19
    [InlineData("OtherOperators")]       // 8.10.5.1 20–29
    [InlineData("FixedRmoveto")]         // 8.10.5.2 1
    [InlineData("Movetos")]              // 8.10.5.2 2–16
    [InlineData("Rlineto2")]             // 8.10.5.2 17–23
    [InlineData("RlinetoOdd")]           // 8.10.5.2 17–23
    [InlineData("HlinetoVlineto")]       // 8.10.5.2 24–38
    [InlineData("Rrcurveto2")]           // 8.10.5.2 39–46
    [InlineData("HhVvCurveto")]          // 8.10.5.2 47–63
    [InlineData("HvVhCurveto")]          // 8.10.5.2 64–77
    [InlineData("RcurvelineRlinecurve")] // 8.10.5.2 78–91
    [InlineData("Flex")]                 // 8.10.5.2 92–98
    [InlineData("FlatFlex50")]           // 8.10.5.2 99–105
    [InlineData("Hflex")]                // 8.10.5.2 106–116
    [InlineData("Hflex1")]               // 8.10.5.2 117–127
    [InlineData("Flex1")]                // 8.10.5.2 128–138
    [InlineData("HstemVstem")]           // 8.10.5.3 2–14
    [InlineData("OverlappingHstemhm")]   // 8.10.5.3 15–29
    [InlineData("Hintmask17Stems")]      // 8.10.5.3 30–47
    [InlineData("Cntrmask13Stems")]      // 8.10.5.3 68–83
    [InlineData("StemsWrongOrder")]      // 8.10.5.3 86
    [InlineData("LocalSubr")]            // 8.10.5.4 1–8
    [InlineData("SubrLeavesOperands")]   // 8.10.5.4 9–18
    [InlineData("CharStringVsindex")]    // 8.10.5.5 1–11
    [InlineData("BlendRmoveto")]         // 8.10.5.5 12–21
    [InlineData("SubrBlendOwnVsindex")]  // 8.10.5.5 22–29
    public void Cff2CharStringOperators_AreRead_DefaultInstanceDrawn(string probe) => AssertCff2ReadOt08(ProbeOt10(probe));
}
