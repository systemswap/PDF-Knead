using System.Runtime.CompilerServices;

namespace PdfKnead.Core.Fonts.Layout;

/// <summary>
/// The text a reader derives for a glyph of a font that has no ToUnicode: the inverted cmap (glyph → Unicode, code points U+0020 to U+2FFF, the
/// lowest code point first) and, for a font with Arabic, the joining forms and lam-alef ligatures its shaping puts in place of letters. The
/// display reads such fonts so (PdfFont), and formatted text built by PDF-Knead gives a glyph that reads otherwise its text as /ActualText
/// (ElementAppearance.RichText) — the shared display font of formatted text has no ToUnicode (docs/FEHLERGEDAECHTNIS.md, No. 920).
/// </summary>
internal static class GlyphTexts
{
    private static readonly ConditionalWeakTable<OpenTypeFont, Dictionary<int, string>> Cache = new();

    /// <summary>The text per glyph, built once per font.</summary>
    /// <param name="font">The font.</param>
    /// <returns>Glyph → text; a glyph not in it reads as nothing.</returns>
    public static IReadOnlyDictionary<int, string> Of(OpenTypeFont font) => Cache.GetValue(font, Build);

    private static Dictionary<int, string> Build(OpenTypeFont ot)
    {
        var reverse = new Dictionary<int, string>();
        if (!(ot.HasCmap(3, 1) || ot.HasCmap(0, 3) || ot.HasCmap(3, 10))) return reverse;
        for (var cp = 0x20; cp < 0x3000; cp++)
            if (ot.GlyphForUnicode(cp) is > 0 and var g) reverse.TryAdd(g, char.ConvertFromUtf32(cp));
        ArabicForms(ot, reverse);
        return reverse;
    }

    /// <summary>
    /// The joining forms of the Arabic letters and the forms of lam and alef together, as the shaping draws them (<see cref="TextShaper"/>):
    /// each letter shaped alone, at the start, in the middle and at the end (a tatweel beside it), and lam with each alef alone and after a
    /// joining letter; every glyph gets the letter of its place, a ligature also the letters after it that have no glyph of their own (a font
    /// may draw lam-alef as one glyph or as two special forms). The cmap knows only the base forms — a free text in Arabic read as a few
    /// scattered letters in the display while its saved copy read in full (No. 920). The first text of a glyph wins, as for the cmap.
    /// </summary>
    private static void ArabicForms(OpenTypeFont ot, Dictionary<int, string> reverse)
    {
        const char Tatweel = '\u0640';
        if (ot.GlyphForUnicode(0x0627) <= 0 || ot.GlyphForUnicode(Tatweel) <= 0) return;
        var shaper = TextShaper.For(ot);
        void Shape(string text)
        {
            var shaped = shaper.Shape(text);
            for (var at = 0; at < text.Length; at++)
            {
                if (text[at] == Tatweel) continue;
                var own = shaped.Where(g => g.Cluster == at).ToList();
                if (own.Count != 1 || own[0].Glyph <= 0) continue;
                var end = at + 1;
                while (end < text.Length && text[end] != Tatweel && !shaped.Any(g => g.Cluster == end)) end++;
                reverse.TryAdd(own[0].Glyph, text[at..end]);
            }
        }
        foreach (var (first, last) in (ReadOnlySpan<(int, int)>)[(0x0620, 0x06FF), (0x0750, 0x077F)])
            for (var cp = first; cp <= last; cp++)
            {
                if (ot.GlyphForUnicode(cp) <= 0 || !ArabicJoining.Joins(cp)) continue;
                var c = (char)cp;
                Shape(c.ToString());
                Shape($"{c}{Tatweel}");
                Shape($"{Tatweel}{c}{Tatweel}");
                Shape($"{Tatweel}{c}");
            }
        foreach (var alef in "\u0622\u0623\u0625\u0627")
        {
            Shape($"\u0644{alef}");
            Shape($"{Tatweel}\u0644{alef}");
        }
    }
}
