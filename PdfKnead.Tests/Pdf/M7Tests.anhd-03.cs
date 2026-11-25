using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Expert and symbol character sets (ISO 32000-2, Annex D.4 and D.5; Konformitätsabgleich M7, draft anhd-03): the codes of MacExpertEncoding,
/// read by name, and the built-in encoding of the standard font Symbol with its bundled substitute, its text and its PDF/A copy. Helpers of
/// draft anhd-01 (<see cref="AnhdD_Open"/>, <see cref="AnhdD_Type1"/>, <see cref="AnhdD_EmbeddedType1"/>).
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>Pairs „name ooo, …“ (octal codes) as (code, name).</summary>
    private static List<(int Code, string Name)> AnhdD45_Pairs(string pairs) =>
        [.. pairs.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split(' ')).Select(p => (Convert.ToInt32(p[1], 8), p[0]))];

    /// <summary>
    /// MacExpertEncoding as the name gives it has each name at its code, and an embedded Type 1 program with exactly these glyphs (and no built-in
    /// encoding) under /Encoding /MacExpertEncoding draws for each code the glyph of the name.
    /// </summary>
    private static void AnhdD4_MacExpert(string pairs)
    {
        var entries = AnhdD45_Pairs(pairs);
        var table = PdfFont.EncodingTable("MacExpertEncoding");
        Assert.Same(FontTables.MacExpertEncoding, table);
        foreach (var (code, name) in entries) Assert.Equal(name, table![code]);
        var program = AnhdD_Type1([.. entries.Select(e => e.Name)], new Dictionary<int, string>());
        var parsed = Type1Font.Parse(program.Data);
        var (doc, font) = AnhdD_Open(pdf => AnhdD_EmbeddedType1(pdf, program, PdfName.Get("MacExpertEncoding")));
        using (doc)
            foreach (var (code, name) in entries)
            {
                var outline = font.Glyph((uint)code, 1).Outline;
                Assert.False(outline.IsEmpty, name);
                Assert.Equal(parsed.Outline(name).Bounds, outline.Bounds);
            }
    }

    /// <summary>
    /// The built-in encoding of Symbol has each name at its code; a Symbol font that is not embedded and has no /Encoding draws for each code the
    /// glyph of the name in the bundled substitute (with an outline, the space without) and reads it as the glyph list says; its PDF/A copy
    /// (<see cref="StandardFontEmbedding.For"/>) names exactly these glyphs, in the order of the codes, in /Differences.
    /// </summary>
    private static void AnhdD5_Symbol(string pairs)
    {
        var entries = AnhdD45_Pairs(pairs);
        foreach (var (code, name) in entries) Assert.Equal(name, FontTables.SymbolSetEncoding[code]);
        var substitute = EmbeddedFonts.StandardCff(StandardFonts.Choose("Symbol", null).File);
        var (doc, font) = AnhdD_Open(_ => AnhdD_Standard("Type1", "Symbol", null));
        using (doc)
            foreach (var (code, name) in entries)
            {
                var gid = substitute.GlyphByName(name);
                Assert.True(gid > 0, name);
                var glyph = font.Glyph((uint)code, 1);
                Assert.True(glyph.Outline.IsEmpty == (name == "space"), name);
                Assert.Equal(substitute.Outline(gid).Bounds, glyph.Outline.Bounds);
                Assert.Equal(GlyphNames.ToUnicode(name), glyph.Text);
            }
        var used = new bool[256];
        foreach (var (code, _) in entries) used[code] = true;
        var copy = StandardFontEmbedding.For(AnhdD_Standard("Type1", "Symbol", null), null, null, null, used);
        Assert.NotNull(copy);
        var differences = copy.Font.GetDictionary(PdfName.Get("Encoding"))!.GetArray(PdfName.Get("Differences"))!;
        Assert.Equal(entries.OrderBy(e => e.Code).Select(e => e.Name), differences.OfType<PdfName>().Select(n => n.Value));
    }

    /// <summary>D.4 sentences 1T–19T: MacExpertEncoding (as <see cref="PdfFont.EncodingTable"/> gives it for the name) has AEsmall, Jsmall,
    /// Aacutesmall, Ksmall, Acircumflexsmall, Acutesmall, Lslashsmall, Lsmall, Adieresissmall at their codes, and an embedded Type 1
    /// program with these glyphs, under /Encoding /MacExpertEncoding, draws for each code the glyph of that name.</summary>
    [Fact]
    public void AnnexD4_Sentences1To19_MacExpert_AEsmallToAdieresissmall() =>
        AnhdD4_MacExpert("AEsmall 276, Jsmall 152, Aacutesmall 207, Ksmall 153, Acircumflexsmall 211, Acutesmall 047, Lslashsmall 302, "
            + "Lsmall 154, Adieresissmall 212");

    /// <summary>D.4 sentences 20T–33T: MacExpertEncoding (as <see cref="PdfFont.EncodingTable"/> gives it for the name) has Macronsmall,
    /// Agravesmall, Msmall, Aringsmall, Nsmall, Asmall, Ntildesmall at their codes, and an embedded Type 1 program with these glyphs, under
    /// /Encoding /MacExpertEncoding, draws for each code the glyph of that name.</summary>
    [Fact]
    public void AnnexD4_Sentences20To33_MacExpert_MacronsmallToNtildesmall() =>
        AnhdD4_MacExpert("Macronsmall 364, Agravesmall 210, Msmall 155, Aringsmall 214, Nsmall 156, Asmall 141, Ntildesmall 226");

    /// <summary>D.4 sentences 34T–42T: MacExpertEncoding (as <see cref="PdfFont.EncodingTable"/> gives it for the name) has Atildesmall,
    /// OEsmall, Brevesmall, Oacutesmall at their codes, and an embedded Type 1 program with these glyphs, under /Encoding
    /// /MacExpertEncoding, draws for each code the glyph of that name.</summary>
    [Fact]
    public void AnnexD4_Sentences34To42_MacExpert_AtildesmallToOacutesmall() =>
        AnhdD4_MacExpert("Atildesmall 213, OEsmall 317, Brevesmall 363, Oacutesmall 227");

    /// <summary>D.4 sentences 43T–45T: MacExpertEncoding (as <see cref="PdfFont.EncodingTable"/> gives it for the name) has Bsmall,
    /// Caronsmall, Ccedillasmall, Cedillasmall, Circumflexsmall, Csmall, Dieresissmall, Dotaccentsmall, Dsmall, Eacutesmall,
    /// Ecircumflexsmall, Edieresissmall, Egravesmall, Esmall, Ethsmall, Fsmall, Gravesmall, Gsmall, Hsmall, Hungarumlautsmall, Iacutesmall,
    /// Icircumflexsmall, Idieresissmall, Igravesmall, Ismall, Ydieresissmall, Ysmall, Zcaronsmall at their codes, and an embedded Type 1
    /// program with these glyphs, under /Encoding /MacExpertEncoding, draws for each code the glyph of that name.</summary>
    [Fact]
    public void AnnexD4_Sentences43To45_MacExpert_BsmallToZcaronsmall() =>
        AnhdD4_MacExpert("Bsmall 142, Caronsmall 256, Ccedillasmall 215, Cedillasmall 311, Circumflexsmall 136, Csmall 143, "
            + "Dieresissmall 254, Dotaccentsmall 372, Dsmall 144, Eacutesmall 216, Ecircumflexsmall 220, Edieresissmall 221, "
            + "Egravesmall 217, Esmall 145, Ethsmall 104, Fsmall 146, Gravesmall 140, Gsmall 147, Hsmall 150, "
            + "Hungarumlautsmall 042, Iacutesmall 222, Icircumflexsmall 224, Idieresissmall 225, Igravesmall 223, Ismall 151, "
            + "Ydieresissmall 330, Ysmall 171, Zcaronsmall 275");

    /// <summary>D.4 sentences 46T–60T: MacExpertEncoding (as <see cref="PdfFont.EncodingTable"/> gives it for the name) has
    /// Ocircumflexsmall, Odieresissmall, Ogoneksmall, Ogravesmall, Oslashsmall, Osmall, Otildesmall, Psmall, Qsmall, Ringsmall, Rsmall,
    /// Scaronsmall, Ssmall at their codes, and an embedded Type 1 program with these glyphs, under /Encoding /MacExpertEncoding, draws for
    /// each code the glyph of that name.</summary>
    [Fact]
    public void AnnexD4_Sentences46To60_MacExpert_OcircumflexsmallToSsmall() =>
        AnhdD4_MacExpert("Ocircumflexsmall 231, Odieresissmall 232, Ogoneksmall 362, Ogravesmall 230, Oslashsmall 277, Osmall 157, "
            + "Otildesmall 233, Psmall 160, Qsmall 161, Ringsmall 373, Rsmall 162, Scaronsmall 247, Ssmall 163");

    /// <summary>D.4 sentences 61T–75T: MacExpertEncoding (as <see cref="PdfFont.EncodingTable"/> gives it for the name) has Thornsmall,
    /// Tildesmall, Tsmall, Uacutesmall, Ucircumflexsmall, Udieresissmall, Ugravesmall, Usmall, Vsmall, Wsmall, Xsmall, Yacutesmall,
    /// fouroldstyle, foursuperior, fraction at their codes, and an embedded Type 1 program with these glyphs, under /Encoding
    /// /MacExpertEncoding, draws for each code the glyph of that name.</summary>
    [Fact]
    public void AnnexD4_Sentences61To75_MacExpert_ThornsmallToFraction() =>
        AnhdD4_MacExpert("Thornsmall 271, Tildesmall 176, Tsmall 164, Uacutesmall 234, Ucircumflexsmall 236, Udieresissmall 237, "
            + "Ugravesmall 235, Usmall 165, Vsmall 166, Wsmall 167, Xsmall 170, Yacutesmall 264, fouroldstyle 064, "
            + "foursuperior 335, fraction 057");

    /// <summary>D.4 sentences 76T–95T: MacExpertEncoding (as <see cref="PdfFont.EncodingTable"/> gives it for the name) has Zsmall,
    /// ampersandsmall, asuperior, bsuperior, centinferior, centoldstyle, centsuperior, colon, colonmonetary, comma at their codes, and an
    /// embedded Type 1 program with these glyphs, under /Encoding /MacExpertEncoding, draws for each code the glyph of that name.</summary>
    [Fact]
    public void AnnexD4_Sentences76To95_MacExpert_ZsmallToComma() =>
        AnhdD4_MacExpert("Zsmall 172, ampersandsmall 046, asuperior 201, bsuperior 365, centinferior 251, centoldstyle 043, "
            + "centsuperior 202, colon 072, colonmonetary 173, comma 054");

    /// <summary>D.4 sentences 96T–112T: MacExpertEncoding (as <see cref="PdfFont.EncodingTable"/> gives it for the name) has commainferior,
    /// commasuperior, dollarinferior, dollaroldstyle, dollarsuperior, dsuperior, eightinferior, eightoldstyle, eightsuperior at their
    /// codes, and an embedded Type 1 program with these glyphs, under /Encoding /MacExpertEncoding, draws for each code the glyph of that
    /// name.</summary>
    [Fact]
    public void AnnexD4_Sentences96To112_MacExpert_CommainferiorToEightsuperior() =>
        AnhdD4_MacExpert("commainferior 262, commasuperior 370, dollarinferior 266, dollaroldstyle 044, dollarsuperior 045, "
            + "dsuperior 353, eightinferior 245, eightoldstyle 070, eightsuperior 241");

    /// <summary>D.4 sentences 113T–131T: MacExpertEncoding (as <see cref="PdfFont.EncodingTable"/> gives it for the name) has esuperior,
    /// exclamdownsmall, exclamsmall, ff, ffi, ffl, fi, figuredash, fiveeighths at their codes, and an embedded Type 1 program with these
    /// glyphs, under /Encoding /MacExpertEncoding, draws for each code the glyph of that name.</summary>
    [Fact]
    public void AnnexD4_Sentences113To131_MacExpert_EsuperiorToFiveeighths() =>
        AnhdD4_MacExpert("esuperior 344, exclamdownsmall 326, exclamsmall 041, ff 126, ffi 131, ffl 132, fi 127, figuredash 320, "
            + "fiveeighths 114");

    /// <summary>D.4 sentences 132T–134T: MacExpertEncoding (as <see cref="PdfFont.EncodingTable"/> gives it for the name) has hyphen,
    /// hypheninferior, hyphensuperior, isuperior, lsuperior, msuperior, nineinferior, nineoldstyle, ninesuperior, nsuperior,
    /// onedotenleader, oneeighth, onefitted, onehalf, oneinferior, oneoldstyle, onequarter, onesuperior, onethird, osuperior,
    /// parenleftinferior, parenleftsuperior, parenrightinferior, parenrightsuperior, period, periodinferior, periodsuperior,
    /// questiondownsmall at their codes, and an embedded Type 1 program with these glyphs, under /Encoding /MacExpertEncoding, draws for
    /// each code the glyph of that name.</summary>
    [Fact]
    public void AnnexD4_Sentences132To134_MacExpert_HyphenToQuestiondownsmall() =>
        AnhdD4_MacExpert("hyphen 055, hypheninferior 137, hyphensuperior 321, isuperior 351, lsuperior 361, msuperior 367, "
            + "nineinferior 273, nineoldstyle 071, ninesuperior 341, nsuperior 366, onedotenleader 053, oneeighth 112, "
            + "onefitted 174, onehalf 110, oneinferior 301, oneoldstyle 061, onequarter 107, onesuperior 332, onethird 116, "
            + "osuperior 257, parenleftinferior 133, parenleftsuperior 050, parenrightinferior 135, parenrightsuperior 051, "
            + "period 056, periodinferior 263, periodsuperior 371, questiondownsmall 300");

    /// <summary>D.4 sentences 135T–137T: MacExpertEncoding (as <see cref="PdfFont.EncodingTable"/> gives it for the name) has fiveinferior,
    /// fiveoldstyle, fivesuperior, fl, fourinferior, seveninferior, sevenoldstyle, sevensuperior, sixinferior, sixoldstyle, sixsuperior,
    /// space, ssuperior, threeeighths, threeinferior, threeoldstyle, threequarters at their codes, and an embedded Type 1 program with
    /// these glyphs, under /Encoding /MacExpertEncoding, draws for each code the glyph of that name.</summary>
    [Fact]
    public void AnnexD4_Sentences135To137_MacExpert_FiveinferiorToThreequarters() =>
        AnhdD4_MacExpert("fiveinferior 260, fiveoldstyle 065, fivesuperior 336, fl 130, fourinferior 242, seveninferior 246, "
            + "sevenoldstyle 067, sevensuperior 340, sixinferior 244, sixoldstyle 066, sixsuperior 337, space 040, "
            + "ssuperior 352, threeeighths 113, threeinferior 243, threeoldstyle 063, threequarters 111");

    /// <summary>D.4 sentences 138T–140T: MacExpertEncoding (as <see cref="PdfFont.EncodingTable"/> gives it for the name) has
    /// questionsmall, rsuperior, rupiah, semicolon, seveneighths, threequartersemdash, threesuperior, tsuperior, twodotenleader,
    /// twoinferior, twooldstyle, twosuperior, twothirds, zeroinferior, zerooldstyle, zerosuperior at their codes, and an embedded Type 1
    /// program with these glyphs, under /Encoding /MacExpertEncoding, draws for each code the glyph of that name.</summary>
    [Fact]
    public void AnnexD4_Sentences138To140_MacExpert_QuestionsmallToZerosuperior() =>
        AnhdD4_MacExpert("questionsmall 077, rsuperior 345, rupiah 175, semicolon 073, seveneighths 115, threequartersemdash 075, "
            + "threesuperior 334, tsuperior 346, twodotenleader 052, twoinferior 252, twooldstyle 062, twosuperior 333, "
            + "twothirds 117, zeroinferior 274, zerooldstyle 060, zerosuperior 342");

    /// <summary>D.5 sentences 1T–3T: the built-in encoding of Symbol has Alpha, Beta, Chi, Delta, Epsilon, Eta, Euro, Gamma at their codes;
    /// a Symbol font that is not embedded and has no /Encoding draws for each code the glyph of that name from the bundled substitute and
    /// reads it as the glyph list says, and its PDF/A copy names these glyphs in /Differences.</summary>
    [Fact]
    public void AnnexD5_Sentences1To3_Symbol_AlphaToGamma() =>
        AnhdD5_Symbol("Alpha 101, Beta 102, Chi 103, Delta 104, Epsilon 105, Eta 110, Euro 240, Gamma 107");

    /// <summary>D.5 sentences 4T–6T: the built-in encoding of Symbol has arrowboth, arrowdblboth, arrowdbldown, arrowdblleft,
    /// arrowdblright, arrowdblup, arrowdown, arrowhorizex at their codes; a Symbol font that is not embedded and has no /Encoding draws for
    /// each code the glyph of that name from the bundled substitute and reads it as the glyph list says, and its PDF/A copy names these
    /// glyphs in /Differences.</summary>
    [Fact]
    public void AnnexD5_Sentences4To6_Symbol_ArrowbothToArrowhorizex() =>
        AnhdD5_Symbol("arrowboth 253, arrowdblboth 333, arrowdbldown 337, arrowdblleft 334, arrowdblright 336, arrowdblup 335, "
            + "arrowdown 257, arrowhorizex 276");

    /// <summary>D.5 sentences 7T–20T: the built-in encoding of Symbol has Ifraktur, Iota, Kappa, Lambda, Mu, Nu, Omega, Omicron, Phi, Pi,
    /// Psi at their codes; a Symbol font that is not embedded and has no /Encoding draws for each code the glyph of that name from the
    /// bundled substitute and reads it as the glyph list says, and its PDF/A copy names these glyphs in /Differences.</summary>
    [Fact]
    public void AnnexD5_Sentences7To20_Symbol_IfrakturToPsi() =>
        AnhdD5_Symbol("Ifraktur 301, Iota 111, Kappa 113, Lambda 114, Mu 115, Nu 116, Omega 127, Omicron 117, Phi 106, Pi 120, "
            + "Psi 131");

    /// <summary>D.5 sentences 21T–38T: the built-in encoding of Symbol has Rfraktur, Rho, Sigma, Tau, Theta, Upsilon, Upsilon1, Xi, Zeta at
    /// their codes; a Symbol font that is not embedded and has no /Encoding draws for each code the glyph of that name from the bundled
    /// substitute and reads it as the glyph list says, and its PDF/A copy names these glyphs in /Differences.</summary>
    [Fact]
    public void AnnexD5_Sentences21To38_Symbol_RfrakturToZeta() =>
        AnhdD5_Symbol("Rfraktur 302, Rho 122, Sigma 123, Tau 124, Theta 121, Upsilon 125, Upsilon1 241, Xi 130, Zeta 132");

    /// <summary>D.5 sentences 39T–56T: the built-in encoding of Symbol has aleph, alpha, ampersand, angle, angleleft, angleright,
    /// approxequal, circlemultiply, circleplus, club at their codes; a Symbol font that is not embedded and has no /Encoding draws for each
    /// code the glyph of that name from the bundled substitute and reads it as the glyph list says, and its PDF/A copy names these glyphs
    /// in /Differences.</summary>
    [Fact]
    public void AnnexD5_Sentences39To56_Symbol_AlephToClub() =>
        AnhdD5_Symbol("aleph 300, alpha 141, ampersand 046, angle 320, angleleft 341, angleright 361, approxequal 273, "
            + "circlemultiply 304, circleplus 305, club 247");

    /// <summary>D.5 sentences 57T–73T: the built-in encoding of Symbol has colon, comma, congruent, copyrightsans, copyrightserif, degree,
    /// delta, diamond at their codes; a Symbol font that is not embedded and has no /Encoding draws for each code the glyph of that name
    /// from the bundled substitute and reads it as the glyph list says, and its PDF/A copy names these glyphs in /Differences.</summary>
    [Fact]
    public void AnnexD5_Sentences57To73_Symbol_ColonToDiamond() =>
        AnhdD5_Symbol("colon 072, comma 054, congruent 100, copyrightsans 343, copyrightserif 323, degree 260, delta 144, diamond 250");

    /// <summary>D.5 sentences 74T–76T: the built-in encoding of Symbol has arrowleft, arrowright, arrowup, arrowvertex, asteriskmath, bar,
    /// beta, braceleft, braceright, bracelefttp, braceleftmid, braceleftbt, bracerighttp, bracerightmid, bracerightbt, braceex,
    /// bracketleft, bracketright, bracketlefttp, bracketleftex, bracketleftbt, bracketrighttp, bracketrightex, bracketrightbt, bullet,
    /// carriagereturn, chi, integralbt, intersection, iota, kappa, lambda, less, lessequal, logicaland, logicalnot, logicalor, lozenge at
    /// their codes; a Symbol font that is not embedded and has no /Encoding draws for each code the glyph of that name from the bundled
    /// substitute and reads it as the glyph list says, and its PDF/A copy names these glyphs in /Differences.</summary>
    [Fact]
    public void AnnexD5_Sentences74To76_Symbol_ArrowleftToLozenge() =>
        AnhdD5_Symbol("arrowleft 254, arrowright 256, arrowup 255, arrowvertex 275, asteriskmath 052, bar 174, beta 142, "
            + "braceleft 173, braceright 175, bracelefttp 354, braceleftmid 355, braceleftbt 356, bracerighttp 374, "
            + "bracerightmid 375, bracerightbt 376, braceex 357, bracketleft 133, bracketright 135, bracketlefttp 351, "
            + "bracketleftex 352, bracketleftbt 353, bracketrighttp 371, bracketrightex 372, bracketrightbt 373, bullet 267, "
            + "carriagereturn 277, chi 143, integralbt 365, intersection 307, iota 151, kappa 153, lambda 154, less 074, "
            + "lessequal 243, logicaland 331, logicalnot 330, logicalor 332, lozenge 340");

    /// <summary>D.5 sentences 77T–79T: the built-in encoding of Symbol has divide, dotmath, eight, element, ellipsis, emptyset, epsilon,
    /// equal, equivalence, eta, exclam, existential, five, florin, four, fraction, gamma, gradient, greater, greaterequal, heart, infinity,
    /// integral, integraltp, integralex, perpendicular, phi, phi1, pi, plus, plusminus, product, propersubset, propersuperset,
    /// proportional, psi, question, radical, radicalex, reflexsubset at their codes; a Symbol font that is not embedded and has no
    /// /Encoding draws for each code the glyph of that name from the bundled substitute and reads it as the glyph list says, and its PDF/A
    /// copy names these glyphs in /Differences.</summary>
    [Fact]
    public void AnnexD5_Sentences77To79_Symbol_DivideToReflexsubset() =>
        AnhdD5_Symbol("divide 270, dotmath 327, eight 070, element 316, ellipsis 274, emptyset 306, epsilon 145, equal 075, "
            + "equivalence 272, eta 150, exclam 041, existential 044, five 065, florin 246, four 064, fraction 244, gamma 147, "
            + "gradient 321, greater 076, greaterequal 263, heart 251, infinity 245, integral 362, integraltp 363, "
            + "integralex 364, perpendicular 136, phi 146, phi1 152, pi 160, plus 053, plusminus 261, product 325, "
            + "propersubset 314, propersuperset 311, proportional 265, psi 171, question 077, radical 326, radicalex 140, "
            + "reflexsubset 315");

    /// <summary>D.5 sentences 80T–99T: the built-in encoding of Symbol has minus, minute, mu, multiply, nine, notelement, notequal,
    /// notsubset, nu, numbersign, omega, omega1, omicron, one, parenleft, parenright, parenlefttp, parenleftex, parenleftbt, parenrighttp,
    /// parenrightex, parenrightbt, partialdiff, percent, period at their codes; a Symbol font that is not embedded and has no /Encoding
    /// draws for each code the glyph of that name from the bundled substitute and reads it as the glyph list says, and its PDF/A copy names
    /// these glyphs in /Differences.</summary>
    [Fact]
    public void AnnexD5_Sentences80To99_Symbol_MinusToPeriod() =>
        AnhdD5_Symbol("minus 055, minute 242, mu 155, multiply 264, nine 071, notelement 317, notequal 271, notsubset 313, nu 156, "
            + "numbersign 043, omega 167, omega1 166, omicron 157, one 061, parenleft 050, parenright 051, parenlefttp 346, "
            + "parenleftex 347, parenleftbt 350, parenrighttp 366, parenrightex 367, parenrightbt 370, partialdiff 266, "
            + "percent 045, period 056");

    /// <summary>D.5 sentences 100T–114T: the built-in encoding of Symbol has similar, six, slash, space, spade, suchthat, summation, tau,
    /// therefore, theta, theta1, three, trademarksans, trademarkserif, two at their codes; a Symbol font that is not embedded and has no
    /// /Encoding draws for each code the glyph of that name from the bundled substitute and reads it as the glyph list says, and its PDF/A
    /// copy names these glyphs in /Differences.</summary>
    [Fact]
    public void AnnexD5_Sentences100To114_Symbol_SimilarToTwo() =>
        AnhdD5_Symbol("similar 176, six 066, slash 057, space 040, spade 252, suchthat 047, summation 345, tau 164, therefore 134, "
            + "theta 161, theta1 112, three 063, trademarksans 344, trademarkserif 324, two 062");

    /// <summary>D.5 sentences 115T–117T: the built-in encoding of Symbol has reflexsuperset, registersans, registerserif, rho, second,
    /// semicolon, seven, sigma, sigma1 at their codes; a Symbol font that is not embedded and has no /Encoding draws for each code the
    /// glyph of that name from the bundled substitute and reads it as the glyph list says, and its PDF/A copy names these glyphs in
    /// /Differences.</summary>
    [Fact]
    public void AnnexD5_Sentences115To117_Symbol_ReflexsupersetToSigma1() =>
        AnhdD5_Symbol("reflexsuperset 312, registersans 342, registerserif 322, rho 162, second 262, semicolon 073, seven 067, "
            + "sigma 163, sigma1 126");

    /// <summary>D.5 sentences 118T–120T: the built-in encoding of Symbol has underscore, union, universal, upsilon, weierstrass, xi, zero,
    /// zeta at their codes; a Symbol font that is not embedded and has no /Encoding draws for each code the glyph of that name from the
    /// bundled substitute and reads it as the glyph list says, and its PDF/A copy names these glyphs in /Differences.</summary>
    [Fact]
    public void AnnexD5_Sentences118To120_Symbol_UnderscoreToZeta() =>
        AnhdD5_Symbol("underscore 137, union 310, universal 042, upsilon 165, weierstrass 303, xi 170, zero 060, zeta 172");

}
