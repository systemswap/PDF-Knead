using PdfKnead.Core.Fonts.Layout;

namespace PdfKnead.Tests.Pdf;

/// <summary>M7, package uni-11: the scripts new in Unicode 16.0 (chapter 13) have their own run and OpenType tag.</summary>
public sealed partial class M7NormTests
{
    /// <summary>
    /// UNI 13.11.1, 13.21.1, 13.22.1, 13.23.1 sentence 1: Ol Onal (U+1E5D0–U+1E5FF), Sunuwar (U+11BC0–U+11BFF), Gurung Khema (U+16100–U+1613F)
    /// and Kirat Rai (U+16D40–U+16D7F) are scripts of their own (Scripts.txt 16.0.0) with the tags onao, sunu, gukh, krai — the comparison with the
    /// shaping library (M7NormTests.E4b_ScriptTable_AsTheShapingLibrary_RunsAsDescribed) covers characters up to Unicode 15.1 only.
    /// </summary>
    [Theory]
    [InlineData(0x1E5D0, 0x1E5EE, "Onao", "onao")]
    [InlineData(0x11BC0, 0x11BF0, "Sunu", "sunu")]
    [InlineData(0x16100, 0x1612D, "Gukh", "gukh")]
    [InlineData(0x16D40, 0x16D63, "Krai", "krai")]
    public void Uni11_ScriptsOfUnicode16_OwnRunAndTag(int first, int second, string script, string tag)
    {
        Assert.Equal(script, UnicodeScripts.Script(first));
        Assert.Equal(script, UnicodeScripts.Script(second));
        Assert.Contains(tag, UnicodeScripts.OpenTypeTags(script));
    }
}
