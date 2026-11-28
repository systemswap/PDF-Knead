using System.Text;

namespace PdfKnead.Core.Fonts.Layout;

/// <summary>
/// Parts of the shaper added with docs/FEHLERGEDAECHTNIS.md, No. 1220, 1221 and 1223: the optical bounds at the ends of a line (lfbd, rtbd, opbd;
/// applied by the line layout), the Arabic presentation forms shaped while U+206D ACTIVATE ARABIC FORM SHAPING is in force, and the choice among
/// the alternates of rand. Written from the OpenType feature registry and the Unicode Standard; no code of another shaper was used.
/// </summary>
public sealed partial class TextShaper
{
    // ------------------------------------------------------------ optical bounds (OpenType 66.2, 66.20, 67.16)

    /// <summary>Whether the font's GPOS has lfbd, rtbd or opbd at all (looked at once per font).</summary>
    private bool? _hasOpticalBounds;

    /// <summary>Whether the font has one of the features of optical bounds (lfbd, rtbd, opbd) in its GPOS (No. 1220).</summary>
    internal bool HasOpticalBounds => _hasOpticalBounds ??= _gpos?.HasFeature("lfbd", "rtbd", "opbd") == true;

    /// <summary>
    /// The adjustment of a glyph at an end of a horizontal line by the optical bounds of the font (No. 1220): at the left end the lookups of lfbd,
    /// at the right end those of rtbd (OpenType 66.2 sentence 7, 67.16 sentence 7) — where the font has neither of them for the script but the
    /// deprecated opbd, the lookups of opbd, which apply lfbd and rtbd at the ends of the line (66.20 sentence 10). The lookups are applied to the
    /// glyph alone (GPOS type 1: placement and advance, 66.2 sentence 5). Nothing where a feature of fixed widths applies to the glyph's run (66.2
    /// sentence 11, 67.16 sentence 11: in a run of CJK text the shaper applies chws or halt where the font has them, <see cref="PositioningFor"/>).
    /// </summary>
    /// <param name="glyph">The glyph at the end of the line.</param>
    /// <param name="left">The left end of the line (else the right).</param>
    /// <param name="script">The script of the glyph's text (ISO 15924, <see cref="ScriptOf"/>; for punctuation that of the text near it), which
    /// selects the lookups; null for none (DFLT).</param>
    /// <param name="language">The language of the text (BCP 47); null if unknown.</param>
    /// <returns>The change of horizontal placement and advance in font units; (0, 0) for none.</returns>
    internal (double XPlacement, double XAdvance) OpticalBound(int glyph, bool left, string? script, string? language)
    {
        if (glyph <= 0 || _gpos is null || !HasOpticalBounds) return (0, 0);
        var scripts = UnicodeScripts.OpenTypeTags(script ?? UnicodeScripts.Common);
        var languages = UnicodeScripts.OpenTypeLanguages(language);
        if (scripts is ["hani"] or ["kana"] or ["hang"] or ["bopo"]
            && (_gpos.LookupsFor(["chws"], scripts, languages, required: false).Length > 0 || _gpos.LookupsFor(["halt"], scripts, languages, required: false).Length > 0))
            return (0, 0);
        var lookups = _gpos.LookupsFor([left ? "lfbd" : "rtbd"], scripts, languages, required: false);
        if (lookups.Length == 0) lookups = _gpos.LookupsFor(["opbd"], scripts, languages, required: false);
        if (lookups.Length == 0) return (0, 0);
        var run = new List<ShapedGlyph>(1) { new() { Glyph = glyph, Text = "", Class = 1, Gates = GateKern } };
        var work = new Work(256, 1 << 16);
        foreach (var lookup in lookups)
            if (work.Left > 0) new Positioning(this, run, work).ApplyLookup(lookup);
        return (run[0].XOffset, run[0].XAdvance);
    }

    /// <summary>The script of the first character of a text that has a script of its own (Hiragana as Katakana); null for none.</summary>
    internal static string? ScriptOf(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var script = UnicodeScripts.Script(rune.Value);
            if (!UnicodeScripts.IsNeutral(script)) return script == "Hira" ? "Kana" : script;
        }
        return null;
    }

    // ------------------------------------------------------------ character shaping selectors (The Unicode Standard 23.3.1)

    /// <summary>U+206C INHIBIT ARABIC FORM SHAPING and U+206D ACTIVATE ARABIC FORM SHAPING.</summary>
    internal const int InhibitFormShaping = 0x206C, ActivateFormShaping = 0x206D;

    /// <summary>The nominal letters of the presentation forms looked up so far (bounded in size).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int[]?> Nominals = new();

    /// <summary>
    /// The nominal letters an Arabic presentation form (Arabic Presentation Forms-A U+FB50–U+FDFF and -B U+FE70–U+FEFF) stands for, by its
    /// compatibility decomposition composed again (NFKC: U+FE91 BEH INITIAL FORM → U+0628, U+FEFB LAM WITH ALEF ISOLATED FORM → U+0644 U+0627,
    /// U+FE81 ALEF WITH MADDA ABOVE ISOLATED FORM → U+0622); null for any other character and for one without such a decomposition (an ornate
    /// parenthesis, a noncharacter, U+FEFF). While U+206D is in force these letters are shaped in place of the presentation form, so that it takes
    /// the form its neighbours ask for (The Unicode Standard 23.3.1 sentence 27); the nominal Arabic letters are always shaped (sentence 28), and
    /// without U+206D a presentation form keeps its own glyph (sentences 25–26, the default state; No. 1221).
    /// </summary>
    internal static int[]? NominalLetters(int c)
    {
        if (c is not (>= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFE)) return null;
        if (Nominals.TryGetValue(c, out var known)) return known;
        int[]? result = null;
        try
        {
            var form = char.ConvertFromUtf32(c);
            var nominal = form.Normalize(NormalizationForm.FormKC);
            if (nominal != form && nominal.Length is > 0 and <= 32)
            {
                var letters = nominal.EnumerateRunes().Select(r => r.Value).ToArray();
                if (!letters.Any(l => l is >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF)) result = letters;
            }
        }
        catch (ArgumentException)
        {
            result = null;
        }
        if (Nominals.Count < 4096) Nominals[c] = result;
        return result;
    }

    // ------------------------------------------------------------ rand (OpenType 67.11)

    /// <summary>How many glyphs before an alternate of rand take part in its choice (the glyph's own text with that of its predecessors).</summary>
    private const int RandomContext = 16;

    /// <summary>
    /// The alternate rand takes for glyph <paramref name="i"/> among <paramref name="count"/>: a fixed function of the glyph and of the text of
    /// the glyph and the <see cref="RandomContext"/> glyphs before it in its run (a hash, FNV-1a with a final mixing) — the pseudo-random choice
    /// of the registry (OpenType 67.11 sentence 8), the same wherever and whenever the text is set again (opening, printing, saving): neither
    /// time, process nor a random number generator takes part. Equal letters at different places mostly differ in their context and so in their
    /// alternate; an edit changes the alternates of at most the following <see cref="RandomContext"/> glyphs (No. 1223).
    /// </summary>
    internal static int RandomAlternate(List<ShapedGlyph> run, int i, int count)
    {
        if (count <= 1) return 0;
        var h = 2166136261u;
        void Add(int value)
        {
            for (var b = 0; b < 4; b++)
            {
                h ^= (byte)(value >> (8 * b));
                h *= 16777619u;
            }
        }
        Add(run[i].Glyph);
        for (var k = Math.Max(0, i - RandomContext); k <= i; k++)
        {
            foreach (var c in run[k].Text ?? "") Add(c);
            Add(-1);
        }
        h ^= h >> 16;
        h *= 0x7FEB352Du;
        h ^= h >> 15;
        h *= 0x846CA68Bu;
        h ^= h >> 16;
        return (int)(h % (uint)count);
    }
}
