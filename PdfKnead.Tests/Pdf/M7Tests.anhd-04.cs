using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The ZapfDingbats character set (ISO 32000-2, Annex D.6; Konformitätsabgleich M7, draft anhd-04): the built-in encoding of the standard font
/// ZapfDingbats, the characters its names stand for, the glyphs of the bundled substitute, and the symbols of new check boxes. Helpers of draft
/// anhd-01 (<see cref="AnhdD_Open"/>, <see cref="AnhdD_Standard"/>).
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>
    /// The built-in encoding of ZapfDingbats has the name at the code, the ZapfDingbats list reads the name as the character of the table, and a
    /// ZapfDingbats font that is not embedded and has no /Encoding draws the code with the glyph of that name in the bundled substitute (with an
    /// outline, the space without) and reads it as that character.
    /// </summary>
    private static void AnhdD6_Dingbat(int code, string name, int unicode)
    {
        Assert.Equal(name, FontTables.ZapfDingbatsEncoding[code]);
        var character = char.ConvertFromUtf32(unicode);
        Assert.Equal(character, GlyphNames.ToUnicode(name, zapfDingbats: true));
        var substitute = EmbeddedFonts.StandardCff(StandardFonts.Choose("ZapfDingbats", null).File);
        var gid = substitute.GlyphByName(name);
        Assert.True(gid > 0, name);
        var (doc, font) = AnhdD_Open(_ => AnhdD_Standard("Type1", "ZapfDingbats", null));
        using (doc)
        {
            var glyph = font.Glyph((uint)code, 1);
            Assert.Equal(name == "space", glyph.Outline.IsEmpty);
            Assert.Equal(substitute.Outline(gid).Bounds, glyph.Outline.Bounds);
            Assert.Equal(character, glyph.Text);
        }
    }

    /// <summary>
    /// D.6 sentences 40T–58T (0x48), 79T–97T (0x6C), 98T–117T (0x6E, 0x75) and 138T–156T (0x34, 0x38): a new check box with one of the six
    /// symbols writes the ZapfDingbats character of that symbol in /MK /CA — its code is the one of the table, under the name of the table.
    /// </summary>
    [Theory]
    [InlineData(CheckStyle.Star, 0x48, "a35")]
    [InlineData(CheckStyle.Circle, 0x6C, "a71")]
    [InlineData(CheckStyle.Square, 0x6E, "a73")]
    [InlineData(CheckStyle.Diamond, 0x75, "a78")]
    [InlineData(CheckStyle.Check, 0x34, "a20")]
    [InlineData(CheckStyle.Cross, 0x38, "a24")]
    public void AnnexD6_NewCheckBox_WritesTheZapfDingbatsCharacterOfItsSymbol(CheckStyle style, int code, string name)
    {
        Assert.Equal(name, FontTables.ZapfDingbatsEncoding[code]);
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var item = PageItem.From(doc)[0];
        var box = new FieldElement
        {
            Rect = new PdfRectangle(72, 660, 86, 674), Value = "Ja",
            Spec = new FieldSpec { Kind = FormFieldKind.CheckBox, Name = "Probe", CheckStyle = style, BorderColor = 0 },
        };
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [item.WithElements([box])], output);
        using var saved = PdfDocument.Open(output.ToArray());
        var annots = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
        var widget = Enumerable.Range(0, annots.Count).Select(i => (PdfDictionary)annots.Get(i))
            .Single(a => a.GetName(PdfName.Subtype)?.Value == "Widget");
        Assert.Equal([(byte)code], widget.GetDictionary(PdfName.Get("MK"))!.GetString(PdfName.Get("CA"))!.Bytes.ToArray());
    }

    /// <summary>D.6 sentences 1T–19T: the built-in encoding of ZapfDingbats has space, a1, a2, a202, a3, a4, a5, a119, a118, a117 at their
    /// codes, the ZapfDingbats list reads each name as the character of the table, and a ZapfDingbats font that is not embedded and has no
    /// /Encoding draws each code with a glyph of the bundled substitute (the glyph of that name) and reads it as that character.</summary>
    [Theory]
    [InlineData(0x20, "space", 0x0020)]
    [InlineData(0x21, "a1", 0x2701)]
    [InlineData(0x22, "a2", 0x2702)]
    [InlineData(0x23, "a202", 0x2703)]
    [InlineData(0x24, "a3", 0x2704)]
    [InlineData(0x25, "a4", 0x260E)]
    [InlineData(0x26, "a5", 0x2706)]
    [InlineData(0x27, "a119", 0x2707)]
    [InlineData(0x28, "a118", 0x2708)]
    [InlineData(0x29, "a117", 0x2709)]
    public void AnnexD6_Sentences1To19_ZapfDingbats_SpaceToA117(int code, string name, int unicode) => AnhdD6_Dingbat(code, name, unicode);

    /// <summary>D.6 sentences 20T–39T: the built-in encoding of ZapfDingbats has a11, a12, a13, a14, a15, a16, a105, a17, a18, a19 at their
    /// codes, the ZapfDingbats list reads each name as the character of the table, and a ZapfDingbats font that is not embedded and has no
    /// /Encoding draws each code with a glyph of the bundled substitute (the glyph of that name) and reads it as that character.</summary>
    [Theory]
    [InlineData(0x2A, "a11", 0x261B)]
    [InlineData(0x2B, "a12", 0x261E)]
    [InlineData(0x2C, "a13", 0x270C)]
    [InlineData(0x2D, "a14", 0x270D)]
    [InlineData(0x2E, "a15", 0x270E)]
    [InlineData(0x2F, "a16", 0x270F)]
    [InlineData(0x30, "a105", 0x2710)]
    [InlineData(0x31, "a17", 0x2711)]
    [InlineData(0x32, "a18", 0x2712)]
    [InlineData(0x33, "a19", 0x2713)]
    public void AnnexD6_Sentences20To39_ZapfDingbats_A11ToA19(int code, string name, int unicode) => AnhdD6_Dingbat(code, name, unicode);

    /// <summary>D.6 sentences 40T–58T: the built-in encoding of ZapfDingbats has a30, a31, a32, a33, a34, a35, a36, a37, a38 at their
    /// codes, the ZapfDingbats list reads each name as the character of the table, and a ZapfDingbats font that is not embedded and has no
    /// /Encoding draws each code with a glyph of the bundled substitute (the glyph of that name) and reads it as that character.</summary>
    [Theory]
    [InlineData(0x43, "a30", 0x2723)]
    [InlineData(0x44, "a31", 0x2724)]
    [InlineData(0x45, "a32", 0x2725)]
    [InlineData(0x46, "a33", 0x2726)]
    [InlineData(0x47, "a34", 0x2727)]
    [InlineData(0x48, "a35", 0x2605)]
    [InlineData(0x49, "a36", 0x2729)]
    [InlineData(0x4A, "a37", 0x272A)]
    [InlineData(0x4B, "a38", 0x272B)]
    public void AnnexD6_Sentences40To58_ZapfDingbats_A30ToA38(int code, string name, int unicode) => AnhdD6_Dingbat(code, name, unicode);

    /// <summary>D.6 sentences 59T–78T: the built-in encoding of ZapfDingbats has a39, a40, a41, a42, a43, a44, a45, a46, a47, a48 at their
    /// codes, the ZapfDingbats list reads each name as the character of the table, and a ZapfDingbats font that is not embedded and has no
    /// /Encoding draws each code with a glyph of the bundled substitute (the glyph of that name) and reads it as that character.</summary>
    [Theory]
    [InlineData(0x4C, "a39", 0x272C)]
    [InlineData(0x4D, "a40", 0x272D)]
    [InlineData(0x4E, "a41", 0x272E)]
    [InlineData(0x4F, "a42", 0x272F)]
    [InlineData(0x50, "a43", 0x2730)]
    [InlineData(0x51, "a44", 0x2731)]
    [InlineData(0x52, "a45", 0x2732)]
    [InlineData(0x53, "a46", 0x2733)]
    [InlineData(0x54, "a47", 0x2734)]
    [InlineData(0x55, "a48", 0x2735)]
    public void AnnexD6_Sentences59To78_ZapfDingbats_A39ToA48(int code, string name, int unicode) => AnhdD6_Dingbat(code, name, unicode);

    /// <summary>D.6 sentences 79T–97T: the built-in encoding of ZapfDingbats has a49, a65, a66, a67, a68, a69, a70, a71, a72 at their
    /// codes, the ZapfDingbats list reads each name as the character of the table, and a ZapfDingbats font that is not embedded and has no
    /// /Encoding draws each code with a glyph of the bundled substitute (the glyph of that name) and reads it as that character.</summary>
    [Theory]
    [InlineData(0x56, "a49", 0x2736)]
    [InlineData(0x66, "a65", 0x2746)]
    [InlineData(0x67, "a66", 0x2747)]
    [InlineData(0x68, "a67", 0x2748)]
    [InlineData(0x69, "a68", 0x2749)]
    [InlineData(0x6A, "a69", 0x274A)]
    [InlineData(0x6B, "a70", 0x274B)]
    [InlineData(0x6C, "a71", 0x25CF)]
    [InlineData(0x6D, "a72", 0x274D)]
    public void AnnexD6_Sentences79To97_ZapfDingbats_A49ToA72(int code, string name, int unicode) => AnhdD6_Dingbat(code, name, unicode);

    /// <summary>D.6 sentences 98T–117T: the built-in encoding of ZapfDingbats has a73, a74, a203, a75, a204, a76, a77, a78, a79, a81 at
    /// their codes, the ZapfDingbats list reads each name as the character of the table, and a ZapfDingbats font that is not embedded and
    /// has no /Encoding draws each code with a glyph of the bundled substitute (the glyph of that name) and reads it as that
    /// character.</summary>
    [Theory]
    [InlineData(0x6E, "a73", 0x25A0)]
    [InlineData(0x6F, "a74", 0x274F)]
    [InlineData(0x70, "a203", 0x2750)]
    [InlineData(0x71, "a75", 0x2751)]
    [InlineData(0x72, "a204", 0x2752)]
    [InlineData(0x73, "a76", 0x25B2)]
    [InlineData(0x74, "a77", 0x25BC)]
    [InlineData(0x75, "a78", 0x25C6)]
    [InlineData(0x76, "a79", 0x2756)]
    [InlineData(0x77, "a81", 0x25D7)]
    public void AnnexD6_Sentences98To117_ZapfDingbats_A73ToA81(int code, string name, int unicode) => AnhdD6_Dingbat(code, name, unicode);

    /// <summary>D.6 sentences 118T–137T: the built-in encoding of ZapfDingbats has a82, a83, a109, a120, a121, a122, a123, a124, a125,
    /// a126, a127, a128, a129, a130, a131, a132 at their codes, the ZapfDingbats list reads each name as the character of the table, and a
    /// ZapfDingbats font that is not embedded and has no /Encoding draws each code with a glyph of the bundled substitute (the glyph of
    /// that name) and reads it as that character.</summary>
    [Theory]
    [InlineData(0x78, "a82", 0x2758)]
    [InlineData(0x79, "a83", 0x2759)]
    [InlineData(0xAB, "a109", 0x2660)]
    [InlineData(0xAC, "a120", 0x2460)]
    [InlineData(0xAD, "a121", 0x2461)]
    [InlineData(0xAE, "a122", 0x2462)]
    [InlineData(0xAF, "a123", 0x2463)]
    [InlineData(0xB0, "a124", 0x2464)]
    [InlineData(0xB1, "a125", 0x2465)]
    [InlineData(0xB2, "a126", 0x2466)]
    [InlineData(0xB3, "a127", 0x2467)]
    [InlineData(0xB4, "a128", 0x2468)]
    [InlineData(0xB5, "a129", 0x2469)]
    [InlineData(0xB6, "a130", 0x2776)]
    [InlineData(0xB7, "a131", 0x2777)]
    [InlineData(0xB8, "a132", 0x2778)]
    public void AnnexD6_Sentences118To137_ZapfDingbats_A82ToA132(int code, string name, int unicode) => AnhdD6_Dingbat(code, name, unicode);

    /// <summary>D.6 sentences 138T–156T: the built-in encoding of ZapfDingbats has a133, a134, a135, a136, a137, a138, a20, a21, a22, a23,
    /// a24, a25 at their codes, the ZapfDingbats list reads each name as the character of the table, and a ZapfDingbats font that is not
    /// embedded and has no /Encoding draws each code with a glyph of the bundled substitute (the glyph of that name) and reads it as that
    /// character.</summary>
    [Theory]
    [InlineData(0xB9, "a133", 0x2779)]
    [InlineData(0xBA, "a134", 0x277A)]
    [InlineData(0xBB, "a135", 0x277B)]
    [InlineData(0xBC, "a136", 0x277C)]
    [InlineData(0xBD, "a137", 0x277D)]
    [InlineData(0xBE, "a138", 0x277E)]
    [InlineData(0x34, "a20", 0x2714)]
    [InlineData(0x35, "a21", 0x2715)]
    [InlineData(0x36, "a22", 0x2716)]
    [InlineData(0x37, "a23", 0x2717)]
    [InlineData(0x38, "a24", 0x2718)]
    [InlineData(0x39, "a25", 0x2719)]
    public void AnnexD6_Sentences138To156_ZapfDingbats_A133ToA25(int code, string name, int unicode) => AnhdD6_Dingbat(code, name, unicode);

    /// <summary>D.6 sentences 157T–174T: the built-in encoding of ZapfDingbats has a26, a27, a28, a6, a7, a8, a9, a10, a29 at their codes,
    /// the ZapfDingbats list reads each name as the character of the table, and a ZapfDingbats font that is not embedded and has no
    /// /Encoding draws each code with a glyph of the bundled substitute (the glyph of that name) and reads it as that character.</summary>
    [Theory]
    [InlineData(0x3A, "a26", 0x271A)]
    [InlineData(0x3B, "a27", 0x271B)]
    [InlineData(0x3C, "a28", 0x271C)]
    [InlineData(0x3D, "a6", 0x271D)]
    [InlineData(0x3E, "a7", 0x271E)]
    [InlineData(0x3F, "a8", 0x271F)]
    [InlineData(0x40, "a9", 0x2720)]
    [InlineData(0x41, "a10", 0x2721)]
    [InlineData(0x42, "a29", 0x2722)]
    public void AnnexD6_Sentences157To174_ZapfDingbats_A26ToA29(int code, string name, int unicode) => AnhdD6_Dingbat(code, name, unicode);

    /// <summary>D.6 sentences 175T–194T: the built-in encoding of ZapfDingbats has a154, a155, a156, a157, a158, a159, a160, a161, a163,
    /// a164 at their codes, the ZapfDingbats list reads each name as the character of the table, and a ZapfDingbats font that is not
    /// embedded and has no /Encoding draws each code with a glyph of the bundled substitute (the glyph of that name) and reads it as that
    /// character.</summary>
    [Theory]
    [InlineData(0xCE, "a154", 0x278E)]
    [InlineData(0xCF, "a155", 0x278F)]
    [InlineData(0xD0, "a156", 0x2790)]
    [InlineData(0xD1, "a157", 0x2791)]
    [InlineData(0xD2, "a158", 0x2792)]
    [InlineData(0xD3, "a159", 0x2793)]
    [InlineData(0xD4, "a160", 0x2794)]
    [InlineData(0xD5, "a161", 0x2192)]
    [InlineData(0xD6, "a163", 0x2194)]
    [InlineData(0xD7, "a164", 0x2195)]
    public void AnnexD6_Sentences175To194_ZapfDingbats_A154ToA164(int code, string name, int unicode) => AnhdD6_Dingbat(code, name, unicode);

    /// <summary>D.6 sentences 195T–213T: the built-in encoding of ZapfDingbats has a196, a165, a50, a51, a52, a53, a54, a55, a56 at their
    /// codes, the ZapfDingbats list reads each name as the character of the table, and a ZapfDingbats font that is not embedded and has no
    /// /Encoding draws each code with a glyph of the bundled substitute (the glyph of that name) and reads it as that character.</summary>
    [Theory]
    [InlineData(0xD8, "a196", 0x2798)]
    [InlineData(0xD9, "a165", 0x2799)]
    [InlineData(0x57, "a50", 0x2737)]
    [InlineData(0x58, "a51", 0x2738)]
    [InlineData(0x59, "a52", 0x2739)]
    [InlineData(0x5A, "a53", 0x273A)]
    [InlineData(0x5B, "a54", 0x273B)]
    [InlineData(0x5C, "a55", 0x273C)]
    [InlineData(0x5D, "a56", 0x273D)]
    public void AnnexD6_Sentences195To213_ZapfDingbats_A196ToA56(int code, string name, int unicode) => AnhdD6_Dingbat(code, name, unicode);

    /// <summary>D.6 sentences 214T–233T: the built-in encoding of ZapfDingbats has a57, a58, a59, a60, a61, a62, a63, a64, a192, a166 at
    /// their codes, the ZapfDingbats list reads each name as the character of the table, and a ZapfDingbats font that is not embedded and
    /// has no /Encoding draws each code with a glyph of the bundled substitute (the glyph of that name) and reads it as that
    /// character.</summary>
    [Theory]
    [InlineData(0x5E, "a57", 0x273E)]
    [InlineData(0x5F, "a58", 0x273F)]
    [InlineData(0x60, "a59", 0x2740)]
    [InlineData(0x61, "a60", 0x2741)]
    [InlineData(0x62, "a61", 0x2742)]
    [InlineData(0x63, "a62", 0x2743)]
    [InlineData(0x64, "a63", 0x2744)]
    [InlineData(0x65, "a64", 0x2745)]
    [InlineData(0xDA, "a192", 0x279A)]
    [InlineData(0xDB, "a166", 0x279B)]
    public void AnnexD6_Sentences214To233_ZapfDingbats_A57ToA166(int code, string name, int unicode) => AnhdD6_Dingbat(code, name, unicode);

    /// <summary>D.6 sentences 234T–253T: the built-in encoding of ZapfDingbats has a167, a168, a169, a170, a171, a172, a173, a162, a174,
    /// a175 at their codes, the ZapfDingbats list reads each name as the character of the table, and a ZapfDingbats font that is not
    /// embedded and has no /Encoding draws each code with a glyph of the bundled substitute (the glyph of that name) and reads it as that
    /// character.</summary>
    [Theory]
    [InlineData(0xDC, "a167", 0x279C)]
    [InlineData(0xDD, "a168", 0x279D)]
    [InlineData(0xDE, "a169", 0x279E)]
    [InlineData(0xDF, "a170", 0x279F)]
    [InlineData(0xE0, "a171", 0x27A0)]
    [InlineData(0xE1, "a172", 0x27A1)]
    [InlineData(0xE2, "a173", 0x27A2)]
    [InlineData(0xE3, "a162", 0x27A3)]
    [InlineData(0xE4, "a174", 0x27A4)]
    [InlineData(0xE5, "a175", 0x27A5)]
    public void AnnexD6_Sentences234To253_ZapfDingbats_A167ToA175(int code, string name, int unicode) => AnhdD6_Dingbat(code, name, unicode);

    /// <summary>D.6 sentences 254T–272T: the built-in encoding of ZapfDingbats has a84, a97, a98, a99, a100, a101, a102, a103, a104 at
    /// their codes, the ZapfDingbats list reads each name as the character of the table, and a ZapfDingbats font that is not embedded and
    /// has no /Encoding draws each code with a glyph of the bundled substitute (the glyph of that name) and reads it as that
    /// character.</summary>
    [Theory]
    [InlineData(0x7A, "a84", 0x275A)]
    [InlineData(0x7B, "a97", 0x275B)]
    [InlineData(0x7C, "a98", 0x275C)]
    [InlineData(0x7D, "a99", 0x275D)]
    [InlineData(0x7E, "a100", 0x275E)]
    [InlineData(0xA1, "a101", 0x2761)]
    [InlineData(0xA2, "a102", 0x2762)]
    [InlineData(0xA3, "a103", 0x2763)]
    [InlineData(0xA4, "a104", 0x2764)]
    public void AnnexD6_Sentences254To272_ZapfDingbats_A84ToA104(int code, string name, int unicode) => AnhdD6_Dingbat(code, name, unicode);

    /// <summary>D.6 sentences 273T–292T: the built-in encoding of ZapfDingbats has a106, a107, a108, a112, a111, a110, a176, a177, a178,
    /// a179 at their codes, the ZapfDingbats list reads each name as the character of the table, and a ZapfDingbats font that is not
    /// embedded and has no /Encoding draws each code with a glyph of the bundled substitute (the glyph of that name) and reads it as that
    /// character.</summary>
    [Theory]
    [InlineData(0xA5, "a106", 0x2765)]
    [InlineData(0xA6, "a107", 0x2766)]
    [InlineData(0xA7, "a108", 0x2767)]
    [InlineData(0xA8, "a112", 0x2663)]
    [InlineData(0xA9, "a111", 0x2666)]
    [InlineData(0xAA, "a110", 0x2665)]
    [InlineData(0xE6, "a176", 0x27A6)]
    [InlineData(0xE7, "a177", 0x27A7)]
    [InlineData(0xE8, "a178", 0x27A8)]
    [InlineData(0xE9, "a179", 0x27A9)]
    public void AnnexD6_Sentences273To292_ZapfDingbats_A106ToA179(int code, string name, int unicode) => AnhdD6_Dingbat(code, name, unicode);

    /// <summary>D.6 sentences 293T–308T: the built-in encoding of ZapfDingbats has a193, a180, a199, a181, a200, a182, a201, a183 at their
    /// codes, the ZapfDingbats list reads each name as the character of the table, and a ZapfDingbats font that is not embedded and has no
    /// /Encoding draws each code with a glyph of the bundled substitute (the glyph of that name) and reads it as that character.</summary>
    [Theory]
    [InlineData(0xEA, "a193", 0x27AA)]
    [InlineData(0xEB, "a180", 0x27AB)]
    [InlineData(0xEC, "a199", 0x27AC)]
    [InlineData(0xED, "a181", 0x27AD)]
    [InlineData(0xEE, "a200", 0x27AE)]
    [InlineData(0xEF, "a182", 0x27AF)]
    [InlineData(0xF1, "a201", 0x27B1)]
    [InlineData(0xF2, "a183", 0x27B2)]
    public void AnnexD6_Sentences293To308_ZapfDingbats_A193ToA183(int code, string name, int unicode) => AnhdD6_Dingbat(code, name, unicode);

    /// <summary>D.6 sentences 309T–327T: the built-in encoding of ZapfDingbats has a139, a140, a141, a142, a143, a144, a145, a146, a147,
    /// a148 at their codes, the ZapfDingbats list reads each name as the character of the table, and a ZapfDingbats font that is not
    /// embedded and has no /Encoding draws each code with a glyph of the bundled substitute (the glyph of that name) and reads it as that
    /// character.</summary>
    [Theory]
    [InlineData(0xBF, "a139", 0x277F)]
    [InlineData(0xC0, "a140", 0x2780)]
    [InlineData(0xC1, "a141", 0x2781)]
    [InlineData(0xC2, "a142", 0x2782)]
    [InlineData(0xC3, "a143", 0x2783)]
    [InlineData(0xC4, "a144", 0x2784)]
    [InlineData(0xC5, "a145", 0x2785)]
    [InlineData(0xC6, "a146", 0x2786)]
    [InlineData(0xC7, "a147", 0x2787)]
    [InlineData(0xC8, "a148", 0x2788)]
    public void AnnexD6_Sentences309To327_ZapfDingbats_A139ToA148(int code, string name, int unicode) => AnhdD6_Dingbat(code, name, unicode);

    /// <summary>D.6 sentences 328T–347T: the built-in encoding of ZapfDingbats has a149, a150, a151, a152, a153, a184, a197, a185, a194,
    /// a198 at their codes, the ZapfDingbats list reads each name as the character of the table, and a ZapfDingbats font that is not
    /// embedded and has no /Encoding draws each code with a glyph of the bundled substitute (the glyph of that name) and reads it as that
    /// character.</summary>
    [Theory]
    [InlineData(0xC9, "a149", 0x2789)]
    [InlineData(0xCA, "a150", 0x278A)]
    [InlineData(0xCB, "a151", 0x278B)]
    [InlineData(0xCC, "a152", 0x278C)]
    [InlineData(0xCD, "a153", 0x278D)]
    [InlineData(0xF3, "a184", 0x27B3)]
    [InlineData(0xF4, "a197", 0x27B4)]
    [InlineData(0xF5, "a185", 0x27B5)]
    [InlineData(0xF6, "a194", 0x27B6)]
    [InlineData(0xF7, "a198", 0x27B7)]
    public void AnnexD6_Sentences328To347_ZapfDingbats_A149ToA198(int code, string name, int unicode) => AnhdD6_Dingbat(code, name, unicode);

    /// <summary>D.6 sentences 348T–361T: the built-in encoding of ZapfDingbats has a186, a195, a187, a188, a189, a190, a191 at their codes,
    /// the ZapfDingbats list reads each name as the character of the table, and a ZapfDingbats font that is not embedded and has no
    /// /Encoding draws each code with a glyph of the bundled substitute (the glyph of that name) and reads it as that character.</summary>
    [Theory]
    [InlineData(0xF8, "a186", 0x27B8)]
    [InlineData(0xF9, "a195", 0x27B9)]
    [InlineData(0xFA, "a187", 0x27BA)]
    [InlineData(0xFB, "a188", 0x27BB)]
    [InlineData(0xFC, "a189", 0x27BC)]
    [InlineData(0xFD, "a190", 0x27BD)]
    [InlineData(0xFE, "a191", 0x27BE)]
    public void AnnexD6_Sentences348To361_ZapfDingbats_A186ToA191(int code, string name, int unicode) => AnhdD6_Dingbat(code, name, unicode);

}
