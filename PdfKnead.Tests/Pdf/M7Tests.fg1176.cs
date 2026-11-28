using System.Reflection;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, the family model of the installed fonts and the search in Arabic presentation forms (docs/FEHLERGEDAECHTNIS.md, No. 1176): the font
/// list offers the families of name ID 1 with their regular, bold and italic faces, so every other style of a typographic family is a family
/// of its own (OpenType 57.2); the search folds Arabic presentation forms in the page text and in the term to the letters they show (NFKC),
/// and a hit marks exactly the glyphs whose folded text it covers.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (FG 1176)

    /// <summary>The index of the system fonts over one folder holding <paramref name="fonts"/> (file name → font file).</summary>
    private static (Dictionary<string, string> Paths, string[] Families) Fg1176Index(params (string File, byte[] Data)[] fonts)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"m7-fg1176-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (file, data) in fonts) File.WriteAllBytes(Path.Combine(dir, file), data);
            var build = typeof(SystemFonts).GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Static, [typeof(IEnumerable<string>)]);
            Assert.NotNull(build);
            var (paths, families) = ((Dictionary<string, string>, string[]))build.Invoke(null, [new[] { dir }])!;
            return (paths.ToDictionary(e => e.Key, e => Path.GetFileName(e.Value)), families);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Liberation Sans with the name records given and fsSelection set to <paramref name="selection"/>.</summary>
    private static byte[] Fg1176Font(int selection, params (int Id, string Text)[] names)
    {
        var os2 = Ot27Table(Ot05SansFile.Value, "OS/2");
        Ot30Set(os2, 62, selection);
        return Ot05SansWith(("name", Ot05Name([.. names.Select(n => (3, 1, 0x0409, n.Id, n.Text))])), ("OS/2", os2));
    }

    /// <summary>A line of glyphs, one per character, 6 units apart (as the search tests of UAX #9).</summary>
    private static PageText Fg1176Line(string text)
    {
        var glyphs = new List<TextGlyph>();
        float x = 0;
        foreach (var c in text)
        {
            glyphs.Add(new TextGlyph(c.ToString(), x, 0, x + 6, 0, x + 6, 10, x, 10, 10, true));
            x += 6;
        }
        return PageText.Create([.. glyphs]);
    }

    /// <summary>The texts of the glyphs a hit marks.</summary>
    private static string[] Fg1176Marked(PageText text, TextRange hit) => [.. text.GlyphsOf(hit).Select(g => g.Text)];

    // ------------------------------------------------------------ question 47: family model

    /// <summary>
    /// OT 57.2 sentences 1–14 and 32–35, OT 30.4 sentences 11–14 (reader, installed fonts): in the model regular/bold/italic/bold italic the
    /// family is name ID 1 — a font folder with „Muster“ (ID 1 „Muster“), „Muster Light“ (ID 1 „Muster Light“, ID 2 „Regular“, ID 16 „Muster“,
    /// ID 17 „Light“) and „Muster Light Italic“ (ID 1 „Muster Light“, ID 2 „Italic“, fsSelection italic) offers the two families „Muster“ and
    /// „Muster Light“ (before only „Muster“, so the light faces could not be chosen); the names from the file still find each face through the
    /// typographic names (keys „musterlight“, „musterlightitalic“).
    /// </summary>
    [Fact]
    public void Ot45_FamilyListInTheModelRegularBoldItalic_LightIsAFamilyOfItsOwn()
    {
        var (paths, families) = Fg1176Index(
            ("Muster-Regular.ttf", Fg1176Font(0x0040, (1, "Muster"), (2, "Regular"), (4, "Muster"), (6, "Muster-Regular"))),
            ("Muster-Light.ttf", Fg1176Font(0x0040, (1, "Muster Light"), (2, "Regular"), (4, "Muster Light"), (6, "Muster-Light"), (16, "Muster"),
                (17, "Light"))),
            ("Muster-LightItalic.ttf", Fg1176Font(0x0001, (1, "Muster Light"), (2, "Italic"), (4, "Muster Light Italic"), (6, "Muster-LightItalic"),
                (16, "Muster"), (17, "Light Italic"))));
        Assert.Equal(["Muster", "Muster Light"], families);
        Assert.Equal("Muster-Light.ttf", paths["musterlight"]);
        Assert.Equal("Muster-LightItalic.ttf", paths["musterlightitalic"]);
        Assert.Equal("Muster-Regular.ttf", paths["muster"]);
    }

    /// <summary>
    /// OT 57.2 sentences 32–33 (reader): a font without name ID 1 gets its family in the model regular/bold/italic/bold italic from the
    /// typographic names, with the parts of the subfamily other than Regular, Bold, Italic and Oblique moved into the family — ID 16 „Selawik“
    /// with ID 17 „Condensed Bold“ is the family „Selawik Condensed“ (before „Selawik“).
    /// </summary>
    [Fact]
    public void Ot45_WithoutId1_FamilyProjectedFromTheTypographicNames()
    {
        var (_, family, _) = Ot05SystemFontNames(Fg1176Font(0x0020, (16, "Selawik"), (17, "Condensed Bold"), (6, "Selawik-CondensedBold")));
        Assert.Equal("Selawik Condensed", family);
    }

    /// <summary>
    /// OT 57.2 sentences 7–9 (reader, installed fonts on Windows): the families of name ID 1 are offered and drawn with their own faces —
    /// „Arial Narrow“ and „Calibri Light“ are families of their own in the font list; typed text in „Arial Narrow“ bold takes the file of „Arial
    /// Narrow Bold“, in „Calibri Light“ italic that of „Calibri Light Italic“, in „Arial“ bold that of „Arial Bold“ — none of them faked (before
    /// the exact name found the regular face first and bold or italic was faked).
    /// </summary>
    [Fact]
    public void Ot45_InstalledFamiliesOfId1_DrawnWithTheirOwnFaces()
    {
        var fonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        if (!OperatingSystem.IsWindows() || !File.Exists(Path.Combine(fonts, "ARIALNB.TTF")) || !File.Exists(Path.Combine(fonts, "calibrili.ttf"))
            || !File.Exists(Path.Combine(fonts, "arialbd.ttf")))
            return;
        var families = RichFonts.Families();
        Assert.Contains("Arial Narrow", families);
        Assert.Contains("Calibri Light", families);
        foreach (var (family, bold, italic, postScript) in new[]
                 {
                     ("Arial Narrow", true, false, "ArialNarrow-Bold"), ("Calibri Light", false, true, "Calibri-LightItalic"),
                     ("Arial", true, false, "Arial-BoldMT"), ("Calibri Light", false, false, "Calibri-Light"),
                 })
        {
            var resolved = RichFonts.For(new RichStyle { Family = family, Bold = bold, Italic = italic });
            Assert.True(resolved.IsSystemFont, family);
            Assert.Equal(postScript, resolved.Face.BaseName);
            Assert.False(resolved.FakeBold, family);
            Assert.False(resolved.FakeItalic, family);
        }
    }

    // ------------------------------------------------------------ question 49: search in presentation forms

    /// <summary>
    /// UNI 9.2.11 sentences 1–8 and 9.2.12 sentences 1–4 (reader, search): presentation forms are folded to the letters they show, in the page
    /// text and in the term — „سلام“ typed in letters finds it read as initial seen, final lam-alef and isolated meem (U+FEB3 U+FEFC U+FEE1),
    /// the hit marking exactly these three glyphs; the term in presentation forms finds the word in letters; „لا“ marks the lam-alef glyph
    /// alone, and a lam alone too (it is part of what that glyph shows); FindAll gives the same hits. The lines are drawn in visual order, as
    /// files write right-to-left text, and read back in logical order (docs/FEHLERGEDAECHTNIS.md, No. 1177).
    /// </summary>
    [Fact]
    public void Uni05_SearchInPresentationForms_FoldedToTheLetters_HitOnTheirGlyphs()
    {
        var forms = Fg1176Line("x ﻡﻼﺳ y");
        var hit = Assert.Single(forms.Find("سلام"));
        Assert.Equal(["ﺳ", "ﻼ", "ﻡ"], Fg1176Marked(forms, hit));
        Assert.Equal([hit], forms.FindAll([PageText.NormalizeQuery("سلام")])[0]);
        Assert.Equal(["ﻼ"], Fg1176Marked(forms, Assert.Single(forms.Find("لا"))));
        Assert.Equal(["ﻼ"], Fg1176Marked(forms, Assert.Single(forms.Find("ل"))));
        Assert.Equal("سلام", PageText.NormalizeQuery("ﺳﻼﻡ"));
        var letters = Fg1176Line("x مالس y");
        Assert.Equal(["س", "ل", "ا", "م"], Fg1176Marked(letters, Assert.Single(letters.Find("ﺳﻼﻡ"))));
    }

    /// <summary>
    /// UNI 9.2.12 sentences 1–4 (reader, search): the spacing forms of the marks (U+FE70–U+FE7F) fold to the mark alone — beh in its initial
    /// form with the medial fatha form (U+FE91 U+FE77) is found as „بU+064E“; and a letter searched without the mark composed with it in the text
    /// (beh, fatha) marks the glyph of that letter (before the hit had no characters and marked nothing). Drawn in visual order (No. 1177).
    /// </summary>
    [Fact]
    public void Uni05_SearchInPresentationForms_MarksFoldedAlone_LetterWithoutItsMarkMarksItsGlyph()
    {
        var forms = Fg1176Line("ﹷﺑ");
        Assert.Equal(["ﺑ", "ﹷ"], Fg1176Marked(forms, Assert.Single(forms.Find("ب\u064E"))));
        var marked = Fg1176Line("ا\u064Eب");
        var hit = Assert.Single(marked.Find("ب"));
        Assert.False(hit.IsEmpty);
        Assert.Contains("ب", Fg1176Marked(marked, hit));
    }

    /// <summary>
    /// UNI 9.2.12 sentences 1–4 („Suchen und schwärzen“): a page whose font reads as presentation forms through /ToUnicode (a Type 3 font,
    /// codes 1–3 as U+FEB3 U+FEFC U+FEE1 between „x“ and „y“) — the term „سلام“ in letters finds the word, its quadrilateral covers the three
    /// glyphs, and redacting it removes these three and no other (before the term was not found and nothing was redacted). The word is drawn
    /// in visual order, as files write it, and read back in logical order (No. 1177).
    /// </summary>
    [Fact]
    public void Uni05_SearchAndRedact_PresentationFormsFromToUnicode()
    {
        var file = MisreadTextTests.File(["ﺳ", "ﻼ", "ﻡ", "x", "y", "z"], "0403020105");
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        var (glyphs, unreadable, misread) = PageInterpreter.BuildTextAndUnreadable(doc, page);
        var text = PageText.Create(glyphs, unreadable, misread);
        var hit = Assert.Single(text.FindAll([PageText.NormalizeQuery("سلام")])[0]);
        Assert.Equal(["ﺳ", "ﻼ", "ﻡ"], Fg1176Marked(text, hit));
        var areas = text.Quads(hit).Select(q => q.Bounds).Select(b => new PdfRectangle(b.Left, b.Top, b.Right, b.Bottom)).ToList();
        var redacted = Redactor.Apply(doc, page, areas);
        Assert.Equal(3, redacted.RemovedGlyphs);
        Assert.Equal("ﻡﻼﺳ", redacted.RemovedText.Replace(" ", ""));   // in the order the content draws them
    }
}
