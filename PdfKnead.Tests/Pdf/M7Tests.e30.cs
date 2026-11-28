using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Tests.Fonts;
using JoinForm = PdfKnead.Core.Fonts.Layout.ArabicJoining.Form;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package E30: the joining types Left_Joining and Right_Joining in a cursive script written from left to right (Phags-pa, the only such
/// script with a character of these types in ArabicShaping.txt 16.0.0: U+A872 SUPERFIXED LETTER RA, L). The OpenType registry (fina, init)
/// reads L in a left-to-right script as joining the character before it; The Unicode Standard (14.4, „Cursive Joining“, referred to from 9.2)
/// gives L in Phags-pa as joining the character that follows, and the bundled shaping library forms it that way — PDF-Knead too
/// (docs/normabgleich/sonderregeln.md, A286).
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>A text of the code points given.</summary>
    private static string E30Text(params int[] codePoints) => string.Concat(codePoints.Select(char.ConvertFromUtf32));

    /// <summary>
    /// Liberation Sans with a cmap for the block at <paramref name="block"/> and a GSUB under <paramref name="tag"/> whose features isol, init,
    /// medi and fina each turn every character of <paramref name="chars"/> into a glyph of its own: isol 400 + k, init 410 + k, medi 420 + k,
    /// fina 430 + k (k the index of the character).
    /// </summary>
    private static byte[] E30FormsFont(string tag, int block, int[] chars)
    {
        (int, int)[] Map(int offset) => [.. chars.Select((c, k) => (E12G(block, c), 400 + offset + k))];
        return E12Font(block, Ot49Layout([tag], [("isol", E11Sub(Map(0))), ("init", E11Sub(Map(10))), ("medi", E11Sub(Map(20))), ("fina", E11Sub(Map(30)))]));
    }

    /// <summary>The glyph <see cref="E30FormsFont"/> gives the character at index <paramref name="k"/> in the form given.</summary>
    private static int E30Form(JoinForm form, int k) => 400 + k + form switch
    {
        JoinForm.Isolated => 0, JoinForm.Initial => 10, JoinForm.Medial => 20, JoinForm.Final => 30, _ => throw new ArgumentOutOfRangeException(nameof(form)),
    };

    /// <summary>
    /// OT 65.4 sentences 1–10 (fina), OT 65.17 sentences 1–10 (init) against UNI 14.4 („Cursive Joining“) and UNI 9.2 (Table 9-3 refers to 14.4
    /// for vertically written scripts), measured against the bundled shaping library: Phags-pa superfixed ra (U+A872, Left_Joining) joins the
    /// letter that follows it (in vertical layout: the one below) — before a dual-joining letter it takes init and the letter fina, after one
    /// it stays isolated and so does the letter; never fina, as the registry's reading of Left_Joining in a left-to-right script would give.
    /// Built font under „phag“ (each character its own glyph per form) and, where installed, the Phags-pa font of the system: the same glyphs as
    /// the library in every position. Manichaean (written from right to left, heth U+10ACD Left_Joining) as the counter-check: init before a
    /// joining letter, isolated after one — there the registry, Unicode and the library agree.
    /// </summary>
    [Fact]
    public void E30_PhagsPa_LeftJoining_JoinsTheFollowingLetter_AsUnicodeAndTheLibrary()
    {
        const int ka = 0xA840, i = 0xA85E, ra = 0xA872;
        Assert.Equal('L', ArabicJoining.TypeOf(ra));
        Assert.Equal([JoinForm.Initial, JoinForm.Final], ArabicJoining.Forms([ra, ka]));
        Assert.Equal([JoinForm.Isolated, JoinForm.Isolated], ArabicJoining.Forms([ka, ra]));
        Assert.Equal([JoinForm.Isolated, JoinForm.Initial, JoinForm.Final], ArabicJoining.Forms([ka, ra, i]));
        Assert.Equal([JoinForm.Isolated, JoinForm.Isolated], ArabicJoining.Forms([ra, ra]));

        int[] chars = [ka, i, ra];
        var phag = E30FormsFont("phag", 0xA840, chars);
        E12Expect(phag, E30Text(ra), E30Form(JoinForm.Isolated, 2));
        E12Expect(phag, E30Text(ra, ka), E30Form(JoinForm.Initial, 2), E30Form(JoinForm.Final, 0));
        E12Expect(phag, E30Text(ka, ra), E30Form(JoinForm.Isolated, 0), E30Form(JoinForm.Isolated, 2));
        E12Expect(phag, E30Text(ka, ra, i), E30Form(JoinForm.Isolated, 0), E30Form(JoinForm.Initial, 2), E30Form(JoinForm.Final, 1));
        E12Expect(phag, E30Text(ka, i, ka), E30Form(JoinForm.Initial, 0), E30Form(JoinForm.Medial, 1), E30Form(JoinForm.Final, 0));
        E12Expect(phag, E30Text(ra, ra), E30Form(JoinForm.Isolated, 2), E30Form(JoinForm.Isolated, 2));

        var path = Path.Combine(@"C:\Windows\Fonts", "phagspa.ttf");
        if (File.Exists(path))
        {
            var data = File.ReadAllBytes(path);
            var font = OpenTypeFont.Parse(data);
            var shaper = TextShaper.For(font);
            int[] Own(params int[] text) => [.. shaper.Shape(E30Text(text)).Select(g => g.Glyph)];
            foreach (var sample in new int[][]
                     {
                         [ra], [ra, ka], [ka, ra], [ka, ra, ka], [ra, ra], [ra, i], [i, ra], [i, ra, i], [ka, ra, i], [0xA861, ra, 0xA861],
                         [ka, i, ka], [i, ka],
                     })
                Assert.Equal(ShaperTests.HarfBuzz(data, font.UnitsPerEm, E30Text(sample), []).Glyphs, Own(sample));
            var alone = Own(ra)[0];
            Assert.NotEqual(alone, Own(ra, ka)[0]);   // joined to the letter below: another glyph
            Assert.Equal(alone, Own(ka, ra)[1]);      // after a letter: isolated, not final
        }

        const int aleph = 0x10AC0, heth = 0x10ACD;
        Assert.Equal('L', ArabicJoining.TypeOf(heth));
        var mani = E30FormsFont("mani", 0x10AC0, [aleph, heth]);
        foreach (var (text, expected) in new (int[], int[])[]
                 {
                     ([heth, aleph], [E30Form(JoinForm.Initial, 1), E30Form(JoinForm.Final, 0)]),
                     ([aleph, heth], [E30Form(JoinForm.Isolated, 0), E30Form(JoinForm.Isolated, 1)]),
                     ([aleph, heth, aleph], [E30Form(JoinForm.Isolated, 0), E30Form(JoinForm.Initial, 1), E30Form(JoinForm.Final, 0)]),
                 })
        {
            Assert.Equal(expected, E12Own(mani, E30Text(text)));
            Assert.Equal(expected, E12Library(mani, E30Text(text)).Reverse());   // the library gives right-to-left text in visual order
        }
    }
}
