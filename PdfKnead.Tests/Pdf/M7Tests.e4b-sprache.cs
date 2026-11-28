using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, decisions on own text (docs/FEHLERGEDAECHTNIS.md, No. 1162), the parts with a new interface: the table of the Unicode Script property,
/// the runs of one script, the language tags of /Lang as OpenType language system tags, and the language stored with an edited block.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>
    /// The script table (Unicode 16.0.0, generated from Scripts.txt): the Script property of every code point the bundled shaping library knows
    /// (its Unicode 15.1; characters assigned since then it calls Unknown) equals the one there; runs: Hiragana and
    /// Katakana in one run, neutral characters with the run before them (at the start with the one after them), neutral text alone one run of
    /// Common; the OpenType tags of Hiragana („kana“) and Myanmar (new tag first).
    /// </summary>
    [Fact]
    public void E4b_ScriptTable_AsTheShapingLibrary_RunsAsDescribed()
    {
        var functions = HarfBuzzSharp.UnicodeFunctions.Default;
        var wrong = new List<string>();
        for (var cp = 0; cp <= 0x10FFFF && wrong.Count < 20; cp++)
        {
            var theirs = functions.GetScript((uint)cp).ToString();
            if (theirs != UnicodeScripts.Unknown && UnicodeScripts.Script(cp) != theirs) wrong.Add($"U+{cp:X4}: {UnicodeScripts.Script(cp)}");
        }
        Assert.Empty(wrong);
        Assert.Equal("Arab", UnicodeScripts.Script(0x0897));   // assigned in Unicode 16.0
        static List<int> Cps(string s) => [.. s.EnumerateRunes().Select(r => r.Value)];
        Assert.Equal([(0, 4, "Latn"), (4, 3, "Grek")], UnicodeScripts.Itemize(Cps("ab, αβ.")));
        Assert.Equal([(0, 3, "Grek")], UnicodeScripts.Itemize(Cps("(αβ")));
        Assert.Equal([(0, 2, "Hani"), (2, 2, "Kana")], UnicodeScripts.Itemize(Cps("漢字かカ")));
        Assert.Equal([(0, 2, UnicodeScripts.Common)], UnicodeScripts.Itemize(Cps("1.")));
        Assert.Equal(["kana"], UnicodeScripts.OpenTypeTags("Hira"));
        Assert.Equal(["mym2", "mymr"], UnicodeScripts.OpenTypeTags("Mymr"));
        Assert.Empty(UnicodeScripts.OpenTypeTags(UnicodeScripts.Common));
    }

    /// <summary>The language tags of /Lang (BCP 47) map to the OpenType language system tags to try; an invalid or empty one to none.</summary>
    [Theory]
    [InlineData("tr", "TRK ")]
    [InlineData("tr-TR", "TRK ")]
    [InlineData("de_DE", "DEU ")]
    [InlineData("ur-PK", "URD ")]
    [InlineData("hy", "HYE ")]           // the registry of OpenType 1.9.1 has HYE only (no HYE0)
    [InlineData("zh-Hant", "ZHT ")]
    [InlineData("zh-TW", "ZHT ")]
    [InlineData("zh-HK", "ZHH ")]
    [InlineData("zh-MO", "ZHTM,ZHH ")]
    [InlineData("zh", "ZHS ")]
    [InlineData("ro-MD", "MOL ,ROM ")]
    [InlineData("afr", "AFK ")]          // Afrikaans — not „AFR“, which the registry gives to Afar
    [InlineData("aar", "AFR ")]
    [InlineData("qaa", "")]              // a code the registry does not list: no tag
    [InlineData("x-unknown", "")]
    [InlineData("", "")]
    public void E4b_LanguageTags_ToOpenTypeLanguageSystems(string language, string tags) =>
        Assert.Equal(tags, string.Join(",", UnicodeScripts.OpenTypeLanguages(language)));

    [System.Runtime.InteropServices.DllImport("libHarfBuzzSharp")]
    private static extern IntPtr hb_language_from_string(byte[] str, int len);

    [System.Runtime.InteropServices.DllImport("libHarfBuzzSharp")]
    private static extern void hb_ot_tags_from_script_and_language(uint script, IntPtr language, ref uint scriptCount, IntPtr scriptTags, ref uint languageCount, uint[] languageTags);

    /// <summary>
    /// The language system tags come from the registry „Language system tags“ (OpenType 62, with the ISO 639 codes it lists; two-letter codes
    /// through the code table of ISO 639-3): for every primary language subtag of two letters (as /Lang carries them) for which both give tags, the first tag
    /// the bundled shaping library chooses is one of those of the registry (the library picks one where the registry names several, and gives
    /// codes the registry does not list a tag of their capitals — PDF-Knead none).
    /// </summary>
    [Fact]
    public void E4b_LanguageTags_FromTheRegistry_TheLibraryAgrees()
    {
        var wrong = new List<string>();
        var tags = new uint[8];
        foreach (var code in Enumerable.Range(0, 26 * 26).Select(n => $"{(char)('a' + n / 26)}{(char)('a' + n % 26)}"))
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(code);
            uint scripts = 0, count = (uint)tags.Length;
            hb_ot_tags_from_script_and_language(0, hb_language_from_string(bytes, bytes.Length), ref scripts, IntPtr.Zero, ref count, tags);
            var theirs = tags.Take((int)count).Select(t => new string([(char)(t >> 24), (char)((t >> 16) & 0xFF), (char)((t >> 8) & 0xFF), (char)(t & 0xFF)])).ToArray();
            var ours = UnicodeScripts.OpenTypeLanguages(code);
            // „hy“: the library prefers HYE0, a tag the registry of OpenType 1.9.1 no longer lists
            if (theirs.Length > 0 && ours.Length > 0 && !ours.Contains(theirs[0]) && code != "hy" && wrong.Count < 40)
                wrong.Add($"{code}: {string.Join(",", ours)} / {string.Join(",", theirs)}");
        }
        Assert.Empty(wrong);
    }

    /// <summary>
    /// The language of an edited block: <see cref="RichTextElement.Language"/> is stored with the block (entry „Sprache“ of its data), so that the
    /// text keeps its glyphs after reopening; a block without a language has no entry.
    /// </summary>
    [Fact]
    public void E4b_BlockLanguage_StoredWithTheBlock()
    {
        var element = new RichTextElement { Rect = new Core.Pdf.Document.PdfRectangle(0, 0, 100, 20), Content = new RichText("Probe", new RichStyle()), Language = "tr-TR" };
        Assert.Equal("tr-TR", ContentElements.Describe(element).GetString(PdfName.Get("Sprache"))?.ToText());
        Assert.Null(ContentElements.Describe(element with { Language = null }).GetString(PdfName.Get("Sprache")));
    }
}
