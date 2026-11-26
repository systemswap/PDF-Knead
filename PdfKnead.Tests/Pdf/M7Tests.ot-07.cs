namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-07 (OpenType specification, CFF2: header, TopDICT, FontDICTINDEX, FontDICTSelect, FontDICT): PDF-Knead does not read CFF2
/// (question 1), so every test shows today's behaviour — the font built after the section, read: no CFF, not intact, no outlines, the
/// matrix from head. The CFF2 tables come from <c>Ot06Cff2</c> (package ot-06).
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>
    /// CFF2 header, TopDICT and FontDICTs as the sections describe them, each variant read (today's behaviour, question 1): no CFF, not
    /// intact, every outline empty, the matrix from head (unitsPerEm).
    /// <list type="bullet">
    /// <item>OT 8.6 sentences 8–11: header size 7 (two bytes before the TopDICT).</item>
    /// <item>OT 8.7 sentences 1–4: TopDICT with FontMatrix 0.0005, head unitsPerEm 2000.</item>
    /// <item>OT 8.7.1 sentences 1–7: TopDICT with the operators 17, 12 36 and 12 7.</item>
    /// <item>OT 8.7.2 sentences 1–7: CharStringINDEX of three glyphs at the offset of operator 17, maxp 3.</item>
    /// <item>OT 8.7.2 sentences 8–13: a variable font (fvar, gvar) with a VariationStore (operator 24).</item>
    /// <item>OT 8.7.2 sentences 14–19: TopDICT without operator 12 36.</item>
    /// <item>OT 8.7.2 sentences 20–26: one FontDICT and still operator 12 37 with a FontDICTSelect.</item>
    /// <item>OT 8.7.2 sentences 27–40: FontMatrix 1/2048 with unitsPerEm 2048, and the uneven matrix 0.001 0 0 0.002 0 5.</item>
    /// <item>OT 8.8.1 sentences 1–6: two FontDICTs, each with its PrivateDICT and its local subroutines, and a FontDICTSelect.</item>
    /// <item>OT 8.8.2 sentences 1–7: an empty FontDICTINDEX; two FontDICTs without FontDICTSelect.</item>
    /// <item>OT 8.8.3 sentences 1–9: FontDICTSelect of the unknown format 5.</item>
    /// <item>OT 8.8.3 sentences 10–15: format 0 with the indices 0 1 0 1 for four glyphs, and with an index beyond the FontDICTs.</item>
    /// <item>OT 8.8.3 sentences 16–35: format 3 with two ranges (0–1 → 0, 2–3 → 1), and with a first range at 1, falling ranges, a sentinel
    /// other than numGlyphs.</item>
    /// <item>OT 8.8.3 sentences 36–47: format 4 with numRanges 0xFFFFFFFF in a short table, and with two valid ranges.</item>
    /// <item>OT 8.8.4 sentences 1–10: FontDICT with PrivateDICTOffset 0 0, and with a PrivateDICT reaching beyond the table.</item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData("Kopfgröße 7")]
    [InlineData("FontMatrix 1/2000")]
    [InlineData("Operatoren 17, 12 36, 12 7")]
    [InlineData("drei Glyphen")]
    [InlineData("variabel")]
    [InlineData("ohne 12 36")]
    [InlineData("eine FontDICT mit FontDICTSelect")]
    [InlineData("FontMatrix 1/2048")]
    [InlineData("FontMatrix ungleichmäßig")]
    [InlineData("zwei FontDICTs mit FontDICTSelect")]
    [InlineData("leere FontDICTINDEX")]
    [InlineData("zwei FontDICTs ohne FontDICTSelect")]
    [InlineData("FontDICTSelect Format 5")]
    [InlineData("Format 0: 0 1 0 1")]
    [InlineData("Format 0: Index zu groß")]
    [InlineData("Format 3")]
    [InlineData("Format 3: erster Bereich bei 1")]
    [InlineData("Format 3: fallende Bereiche")]
    [InlineData("Format 3: Endmarke falsch")]
    [InlineData("Format 4: numRanges 0xFFFFFFFF")]
    [InlineData("Format 4: zwei Bereiche")]
    [InlineData("Private 0 0")]
    [InlineData("Private außerhalb")]
    public void Cff2TopDictAndFontDicts_AreNotRead(string variant)
    {
        const double em2048 = 1.0 / 2048;
        var unitsPerEm = variant == "FontMatrix 1/2000" ? 2000 : 2048;
        var o = variant switch
        {
            "Kopfgröße 7" => new Ot06Cff2Options { HeaderSize = 7 },
            "FontMatrix 1/2000" => new Ot06Cff2Options { FontMatrix = [0.0005, 0, 0, 0.0005, 0, 0], UnitsPerEm = 2000 },
            "Operatoren 17, 12 36, 12 7" or "FontMatrix 1/2048" => new Ot06Cff2Options { FontMatrix = [em2048, 0, 0, em2048, 0, 0] },
            "drei Glyphen" => new Ot06Cff2Options { Glyphs = 3 },
            "variabel" => new Ot06Cff2Options { VariationStore = true },
            "ohne 12 36" => new Ot06Cff2Options { FontDictIndexOffset = false },
            "eine FontDICT mit FontDICTSelect" => new Ot06Cff2Options { FdSelect = [0, 0, 0] },
            "FontMatrix ungleichmäßig" => new Ot06Cff2Options { FontMatrix = [0.001, 0, 0, 0.002, 0, 5] },
            "zwei FontDICTs mit FontDICTSelect" => new Ot06Cff2Options { Glyphs = 4, FontDicts = 2, FdSelect = [3, 0, 2, 0, 0, 0, 0, 2, 1, 0, 4] },
            "leere FontDICTINDEX" => new Ot06Cff2Options { FontDicts = 0 },
            "zwei FontDICTs ohne FontDICTSelect" => new Ot06Cff2Options { FontDicts = 2 },
            "FontDICTSelect Format 5" => new Ot06Cff2Options { FontDicts = 2, FdSelect = [5, 0, 0] },
            "Format 0: 0 1 0 1" => new Ot06Cff2Options { Glyphs = 4, FontDicts = 2, FdSelect = [0, 0, 1, 0, 1] },
            "Format 0: Index zu groß" => new Ot06Cff2Options { Glyphs = 4, FontDicts = 2, FdSelect = [0, 0, 5, 0, 1] },
            "Format 3" => new Ot06Cff2Options { Glyphs = 4, FontDicts = 2, FdSelect = [3, 0, 2, 0, 0, 0, 0, 2, 1, 0, 4] },
            "Format 3: erster Bereich bei 1" => new Ot06Cff2Options { Glyphs = 4, FontDicts = 2, FdSelect = [3, 0, 2, 0, 1, 0, 0, 2, 1, 0, 4] },
            "Format 3: fallende Bereiche" => new Ot06Cff2Options { Glyphs = 4, FontDicts = 2, FdSelect = [3, 0, 2, 0, 2, 0, 0, 0, 1, 0, 4] },
            "Format 3: Endmarke falsch" => new Ot06Cff2Options { Glyphs = 4, FontDicts = 2, FdSelect = [3, 0, 2, 0, 0, 0, 0, 2, 1, 0, 9] },
            "Format 4: numRanges 0xFFFFFFFF" => new Ot06Cff2Options { Glyphs = 4, FontDicts = 2, FdSelect = [4, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0] },
            "Format 4: zwei Bereiche" => new Ot06Cff2Options
            {
                Glyphs = 4, FontDicts = 2, FdSelect = [4, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 1, 0, 0, 0, 4],
            },
            "Private 0 0" => new Ot06Cff2Options { EmptyPrivate = true },
            "Private außerhalb" => new Ot06Cff2Options { PrivateOutside = true },
            _ => throw new ArgumentException(variant),
        };
        var cff2 = Ot06Cff2(o);
        var file = variant == "variabel"
            ? Ot06Cff2Font(cff2, o.Glyphs, unitsPerEm, ("fvar", Ot05VariationTables(o.Glyphs).Fvar), ("gvar", Ot05VariationTables(o.Glyphs).Gvar))
            : Ot06Cff2Font(cff2, o.Glyphs, unitsPerEm);
        Assert.Equal(o.HeaderSize, cff2[2]);
        Ot06AssertCff2NotRead(file, unitsPerEm);
        Assert.Equal(o.Glyphs, PdfKnead.Core.Fonts.OpenTypeFont.Parse(file).GlyphCount);
    }
}
