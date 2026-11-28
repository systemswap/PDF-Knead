using System.Globalization;
using System.Text;

namespace PdfKnead.Core.Fonts.Layout;

/// <summary>
/// The syllabic shaping of Khmer, Myanmar, Tibetan, Thai and Lao and the composition of Hangul jamo (docs/FEHLERGEDAECHTNIS.md, No. 1175).
/// Written from the OpenType feature registry (abvf, blwf, cfar, pref, pstf, rphf, ljmo/vjmo/tjmo …) and the Unicode Standard (chapters 3.12,
/// 16); no code or data of another shaper was used.
/// </summary>
public sealed partial class TextShaper
{
    // ------------------------------------------------------------ Khmer

    /// <summary>The class of a Khmer character: consonants (Ro of its own), independent vowels, coeng, robat, register shifters, the dependent
    /// vowels by their side, the signs.</summary>
    private static Cat KhmerCategory(int cp)
    {
        if (cp == 0x25CC) return Cat.DottedCircle;
        if (cp == 0x179A) return Cat.Ra;
        return SyllableData.SyllabicOf(cp) switch
        {
            SyllabicCategory.Consonant => Cat.Consonant,
            SyllabicCategory.VowelIndependent => Cat.Vowel,
            SyllabicCategory.ConsonantPlaceholder => Cat.Placeholder,
            SyllabicCategory.InvisibleStacker => Cat.Halant,
            SyllabicCategory.ConsonantSucceedingRepha => Cat.Robat,
            SyllabicCategory.RegisterShifter => Cat.RegisterShifter,
            SyllabicCategory.VowelDependent => SyllableData.PositionalOf(cp) switch
            {
                PositionalCategory.Left => Cat.VowelPre,
                PositionalCategory.Bottom => Cat.VowelBelow,
                PositionalCategory.Right or PositionalCategory.LeftAndRight => Cat.VowelPost,
                _ => Cat.VowelAbove,
            },
            SyllabicCategory.Bindu or SyllabicCategory.Visarga or SyllabicCategory.SyllableModifier or SyllabicCategory.ConsonantKiller
                or SyllabicCategory.PureKiller or SyllabicCategory.ToneMark or SyllabicCategory.Nukta => Cat.Modifier,
            SyllabicCategory.Joiner => Cat.Joiner,
            SyllabicCategory.NonJoiner => Cat.NonJoiner,
            _ => Cat.Other,
        };
    }

    /// <summary>The Khmer vowels written in two parts, one of them before the base (OpenType 64.2: the program puts the pre-base part in place).</summary>
    private static bool IsKhmerSplitVowel(int cp) => cp is 0x17BE or 0x17BF or 0x17C0 or 0x17C4 or 0x17C5;

    /// <summary>
    /// Prepares a Khmer run: a vowel written in two parts gets its pre-base part (U+17C1, without a text of its own) before it, where the font
    /// has it — abvf and pstf then form the other part from the vowel itself; then the classes and the syllables.
    /// </summary>
    private void PrepareKhmer(List<ShapedGlyph> run, List<int> codePoints, Work work)
    {
        var pre = _font.GlyphForUnicode(0x17C1);
        for (var i = 0; i < codePoints.Count && pre > 0 && run.Count < MaxGlyphs; i++)
        {
            if (!IsKhmerSplitVowel(codePoints[i]) || !work.Move(run.Count - i)) continue;
            run.Insert(i, run[i] with { Glyph = pre, Text = "", Class = 1 });
            codePoints.Insert(i, 0x17C1);
            i++;
        }
        for (var i = 0; i < run.Count; i++) run[i] = run[i] with { Category = (byte)KhmerCategory(codePoints[i]) };
        FindSyllables(run, codePoints, ParseKhmer, work);
    }

    /// <summary>A Khmer syllable: a base (consonant, independent vowel, placeholder) with subscript consonants after coeng, robat, register
    /// shifters, vowels and signs; signs without a base are broken.</summary>
    private static (int Length, bool Broken) ParseKhmer(List<ShapedGlyph> run, int i)
    {
        var n = Math.Min(run.Count, i + MaxSyllable);
        Cat C(int k) => k < n ? (Cat)run[k].Category : Cat.Other;
        static bool IsBase(Cat c) => c is Cat.Consonant or Cat.Ra or Cat.Vowel or Cat.Placeholder or Cat.DottedCircle;
        var hasBase = IsBase(C(i));
        var k = hasBase ? i + 1 : i;
        while (k < n)
        {
            var c = C(k);
            if (c == Cat.Halant && C(k + 1) is Cat.Consonant or Cat.Ra or Cat.Vowel) k += 2;
            else if (c is Cat.Halant or Cat.Robat or Cat.RegisterShifter or Cat.VowelPre or Cat.VowelAbove or Cat.VowelBelow or Cat.VowelPost
                     or Cat.Modifier or Cat.Joiner or Cat.NonJoiner) k++;
            else break;
        }
        return hasBase ? (k - i, false) : k > i && C(i) is not (Cat.Joiner or Cat.NonJoiner) ? (k - i, true) : (1, false);
    }

    /// <summary>
    /// The reordering of a Khmer syllable (OpenType 64.2, 64.14, 67.7): every glyph after the base may take blwf, abvf and pstf; the first coeng
    /// with Ro among the first two subscripts moves before the base and takes pref, the glyphs after it cfar; a pre-base vowel moves to the
    /// front of the syllable.
    /// </summary>
    private static void ReorderKhmer(List<ShapedGlyph> run, int s, int e)
    {
        for (var i = s + 1; i < e; i++) run[i] = run[i] with { Mask = (ushort)(run[i].Mask | MaskBlwf | MaskAbvf | MaskPstf) };
        var coengs = 0;
        for (var i = s + 1; i < e; i++)
        {
            var cat = (Cat)run[i].Category;
            if (cat == Cat.Halant && i + 1 < e && coengs < 2)
            {
                coengs++;
                if ((Cat)run[i + 1].Category != Cat.Ra) continue;
                for (var k = i + 2; k < e; k++) run[k] = run[k] with { Mask = (ushort)(run[k].Mask | MaskCfar) };
                run[i] = run[i] with { Mask = (ushort)(run[i].Mask | MaskPref) };
                run[i + 1] = run[i + 1] with { Mask = (ushort)(run[i + 1].Mask | MaskPref) };
                Move(run, i, s);
                Move(run, i + 1, s + 1);
                coengs = 2;
                i++;
            }
            else if (cat == Cat.VowelPre) Move(run, i, s);
        }
    }

    /// <summary>
    /// The substitutions of a Khmer run: the reordering of each syllable first; then locl, ccmp, pref, blwf, abvf, pstf and cfar together in one
    /// stage, each syllable on its own and each feature at the glyphs of its mask; then pres, abvs, blws and psts with the other features.
    /// </summary>
    private void SubstituteKhmer(List<ShapedGlyph> run, string[] scripts, RunOptions options, string[] direction, HashSet<int>? italic, Work work)
    {
        foreach (var (start, end) in SyllableRanges(run).ToList())
            if (work.Spend(end - start)) ReorderKhmer(run, start, end);
        var first = FirstStage(scripts, options);
        AddStage(first, [("pref", MaskPref), ("blwf", MaskBlwf), ("abvf", MaskAbvf), ("pstf", MaskPstf), ("cfar", MaskCfar)], scripts, options, perSyllable: true);
        ApplyStage(run, first, italic, work);
        // the presentation forms pass over a ZWJ in their input: a ZWJ before a vowel sign asks for its ligature with the consonant in a
        // muul font (The Unicode Standard 16.4.2; docs/FEHLERGEDAECHTNIS.md, No. 1183) — the basic forms above keep the joiners as glyphs
        var last = LastStage([("pres", 0), ("abvs", 0), ("blws", 0), ("psts", 0)], false, scripts, options, direction);
        foreach (var lookup in last.Keys.ToList()) last[lookup] = last[lookup] with { Manual = false };
        ApplyStage(run, last, italic, work);
    }

    // ------------------------------------------------------------ Myanmar

    /// <summary>The class of a Myanmar character: Nga and Ra (which begin a kinzi), asat, virama, the medials, the dependent vowels by their side,
    /// anusvara and AI, dot below, the other signs, variation selectors; digits serve as bases.</summary>
    private static Cat MyanmarCategory(int cp)
    {
        switch (cp)
        {
            case 0x25CC:
                return Cat.DottedCircle;
            case 0x1004 or 0x101B or 0x105A:
                return Cat.Ra;
            case 0x103A:
                return Cat.Asat;
            case 0x1039:
                return Cat.Halant;
            case 0x103B:
                return Cat.MedialYa;
            case 0x103C:
                return Cat.MedialRa;
            case 0x103D or 0x1082:
                return Cat.MedialWa;
            case 0x103E:
                return Cat.MedialHa;
            case 0x1032 or 0x1036:
                return Cat.Anusvara;
            case 0x1037:
                return Cat.DotBelow;
            case >= 0xFE00 and <= 0xFE0F:
                return Cat.Selector;
        }
        return SyllableData.SyllabicOf(cp) switch
        {
            SyllabicCategory.Consonant => Cat.Consonant,
            SyllabicCategory.VowelIndependent => Cat.Vowel,
            SyllabicCategory.ConsonantPlaceholder or SyllabicCategory.Number => Cat.Placeholder,
            SyllabicCategory.ConsonantMedial => SyllableData.PositionalOf(cp) == PositionalCategory.Right ? Cat.MedialYa : Cat.MedialWa,
            SyllabicCategory.VowelDependent => SyllableData.PositionalOf(cp) switch
            {
                PositionalCategory.Left => Cat.VowelPre,
                PositionalCategory.Bottom => Cat.VowelBelow,
                PositionalCategory.Right => Cat.VowelPost,
                _ => Cat.VowelAbove,
            },
            SyllabicCategory.Bindu => Cat.Anusvara,
            SyllabicCategory.Visarga or SyllabicCategory.ToneMark or SyllabicCategory.SyllableModifier or SyllabicCategory.PureKiller
                or SyllabicCategory.Nukta => Cat.Modifier,
            SyllabicCategory.Joiner => Cat.Joiner,
            SyllabicCategory.NonJoiner => Cat.NonJoiner,
            _ => Cat.Other,
        };
    }

    private static bool IsMyanmarBase(Cat c) => c is Cat.Consonant or Cat.Ra or Cat.Vowel or Cat.Placeholder or Cat.DottedCircle;

    private void PrepareMyanmar(List<ShapedGlyph> run, List<int> codePoints, Work work)
    {
        for (var i = 0; i < run.Count; i++) run[i] = run[i] with { Category = (byte)MyanmarCategory(codePoints[i]) };
        FindSyllables(run, codePoints, ParseMyanmar, work);
    }

    /// <summary>A Myanmar syllable: an optional kinzi (Nga, asat, virama), a base, consonants stacked after virama, then medials, vowels and signs;
    /// signs without a base are broken.</summary>
    private static (int Length, bool Broken) ParseMyanmar(List<ShapedGlyph> run, int i)
    {
        var n = Math.Min(run.Count, i + MaxSyllable);
        Cat C(int k) => k < n ? (Cat)run[k].Category : Cat.Other;
        var k = i;
        if (C(k) == Cat.Ra && C(k + 1) == Cat.Asat && C(k + 2) == Cat.Halant && IsMyanmarBase(C(k + 3))) k += 3;
        var hasBase = IsMyanmarBase(C(k));
        if (hasBase)
        {
            k++;
            if (C(k) == Cat.Selector) k++;
            while (C(k) == Cat.Halant && IsMyanmarBase(C(k + 1)))
            {
                k += 2;
                if (C(k) == Cat.Selector) k++;
            }
        }
        while (k < n && C(k) is Cat.Asat or Cat.Halant or Cat.MedialYa or Cat.MedialRa or Cat.MedialWa or Cat.MedialHa or Cat.VowelPre or Cat.VowelAbove
                   or Cat.VowelBelow or Cat.VowelPost or Cat.Anusvara or Cat.DotBelow or Cat.Modifier or Cat.Joiner or Cat.NonJoiner or Cat.Selector) k++;
        return hasBase ? (k - i, false) : k > i && C(i) is not (Cat.Joiner or Cat.NonJoiner) ? (k - i, true) : (1, false);
    }

    /// <summary>
    /// The reordering of a Myanmar syllable (OpenType 67.7, 67.22: kinzi and medial ra): the base is the first consonant after a kinzi; the kinzi
    /// goes behind the base, medial ra and the pre-base vowel before it (the vowel first), the signs below the base before those after it, and a
    /// variation selector stays with the glyph before it.
    /// </summary>
    private static void ReorderMyanmar(List<ShapedGlyph> run, int s, int e)
    {
        var kinzi = s + 3 <= e && (Cat)run[s].Category == Cat.Ra && (Cat)run[s + 1].Category == Cat.Asat && (Cat)run[s + 2].Category == Cat.Halant;
        var limit = kinzi ? s + 3 : s;
        var basePos = limit;
        for (var i = limit; i < e; i++)
            if (IsMyanmarBase((Cat)run[i].Category))
            {
                basePos = i;
                break;
            }
        var k = s;
        for (; k < limit; k++) run[k] = At(run[k], Pos.AfterMain);
        for (; k < basePos; k++) run[k] = At(run[k], Pos.PreConsonant);
        if (k < e)
        {
            run[k] = At(run[k], Pos.BaseConsonant);
            k++;
        }
        var place = Pos.AfterMain;
        for (; k < e; k++)
        {
            var cat = (Cat)run[k].Category;
            Pos own;
            if (cat == Cat.MedialRa) own = Pos.PreConsonant;
            else if (cat == Cat.VowelPre) own = Pos.PreMatra;
            else if (cat == Cat.Selector) own = (Pos)run[k - 1].Position;
            else if (place == Pos.AfterMain && cat == Cat.VowelBelow) own = place = Pos.BelowConsonant;
            else if (place == Pos.BelowConsonant && cat == Cat.Anusvara) own = Pos.BeforeSub;
            else if (place == Pos.BelowConsonant && cat == Cat.VowelBelow) own = place;
            else if (place == Pos.BelowConsonant) own = place = Pos.AfterSub;
            else own = place;
            run[k] = At(run[k], own);
        }
        SortByPosition(run, s, e);
    }

    /// <summary>
    /// The substitutions of a Myanmar run: locl and ccmp in each syllable, the reordering, then rphf, pref, blwf and pstf each as a stage of its
    /// own (in the whole syllable), then pres, abvs, blws and psts with the other features.
    /// </summary>
    private void SubstituteMyanmar(List<ShapedGlyph> run, string[] scripts, RunOptions options, string[] direction, HashSet<int>? italic, Work work)
    {
        ApplyStage(run, FirstStage(scripts, options), italic, work);
        foreach (var (start, end) in SyllableRanges(run).ToList())
            if (work.Spend(end - start)) ReorderMyanmar(run, start, end);
        foreach (var feature in new[] { "rphf", "pref", "blwf", "pstf" })
        {
            var stage = new SortedDictionary<int, StageLookup>();
            AddStage(stage, [(feature, 0)], scripts, options, perSyllable: true);
            ApplyStage(run, stage, italic, work);
        }
        ApplyStage(run, LastStage([("pres", 0), ("abvs", 0), ("blws", 0), ("psts", 0)], false, scripts, options, direction), italic, work);
    }

    // ------------------------------------------------------------ Tibetan

    /// <summary>Whether a Tibetan character belongs to the base before it (subjoined consonants, vowel signs, marks, joiners).</summary>
    private static bool IsTibetanDependent(int cp) => SyllableData.SyllabicOf(cp) is SyllabicCategory.ConsonantSubjoined or SyllabicCategory.VowelDependent
        or SyllabicCategory.Bindu or SyllabicCategory.Visarga or SyllabicCategory.Nukta or SyllabicCategory.PureKiller or SyllabicCategory.Virama
        or SyllabicCategory.SyllableModifier or SyllabicCategory.CantillationMark or SyllabicCategory.ToneMark or SyllabicCategory.GeminationMark
        or SyllabicCategory.RegisterShifter or SyllabicCategory.ConsonantKiller or SyllabicCategory.Joiner or SyllabicCategory.NonJoiner;

    private void PrepareTibetan(List<ShapedGlyph> run, List<int> codePoints, Work work)
    {
        for (var i = 0; i < run.Count; i++)
        {
            var cp = codePoints[i];
            var cat = cp == 0x25CC ? Cat.DottedCircle
                : IsTibetanDependent(cp) ? (SyllableData.SyllabicOf(cp) is SyllabicCategory.Joiner ? Cat.Joiner
                    : SyllableData.SyllabicOf(cp) is SyllabicCategory.NonJoiner ? Cat.NonJoiner : Cat.Dependent)
                : SyllableData.SyllabicOf(cp) is SyllabicCategory.Consonant or SyllabicCategory.ConsonantHeadLetter or SyllabicCategory.VowelIndependent
                    or SyllabicCategory.ConsonantPlaceholder or SyllabicCategory.Vowel ? Cat.Consonant : Cat.Other;
            run[i] = run[i] with { Category = (byte)cat };
        }
        FindSyllables(run, codePoints, ParseTibetan, work);
    }

    /// <summary>A Tibetan syllable (a stack): a base with its subjoined consonants, vowel signs and marks; marks at the start of the run are broken.</summary>
    private static (int Length, bool Broken) ParseTibetan(List<ShapedGlyph> run, int i)
    {
        var n = Math.Min(run.Count, i + MaxSyllable);
        // a character of no class of its own (a space, an unassigned code point) carries the marks after it, as with the reference shaper
        var hasBase = (Cat)run[i].Category is Cat.Consonant or Cat.DottedCircle or Cat.Other;
        var k = hasBase ? i + 1 : i;
        while (k < n && (Cat)run[k].Category is Cat.Dependent or Cat.Joiner or Cat.NonJoiner) k++;
        return hasBase ? (k - i, false) : k > i && (Cat)run[i].Category == Cat.Dependent ? (k - i, true) : (1, false);
    }

    /// <summary>
    /// The substitutions of a Tibetan run (stacks need no reordering): locl, ccmp, nukt and akhn; rphf; pref; rkrf, abvf, blwf, half, pstf,
    /// vatu and cjct — each stage in each stack on its own; then abvs, blws, haln, pres and psts with the other features.
    /// </summary>
    private void SubstituteTibetan(List<ShapedGlyph> run, string[] scripts, RunOptions options, string[] direction, HashSet<int>? italic, Work work)
    {
        ApplyStage(run, FirstStage(scripts, options, "nukt", "akhn"), italic, work);
        foreach (var features in new[] { new[] { "rphf" }, ["pref"], ["rkrf", "abvf", "blwf", "half", "pstf", "vatu", "cjct"] })
        {
            var stage = new SortedDictionary<int, StageLookup>();
            AddStage(stage, features.Select(f => (f, (ushort)0)), scripts, options, perSyllable: true);
            ApplyStage(run, stage, italic, work);
        }
        ApplyStage(run, LastStage([("abvs", 0), ("blws", 0), ("haln", 0), ("pres", 0), ("psts", 0)], false, scripts, options, direction), italic, work);
    }

    // ------------------------------------------------------------ Thai and Lao

    /// <summary>Longest run of above-base marks SARA AM's nikhahit moves across.</summary>
    private const int MaxAboveMarks = 16;

    /// <summary>
    /// Thai and Lao need no reordering of their own: their marks are set by mark and mkmk. Only SARA AM (U+0E33, Lao U+0EB3), which the Unicode
    /// Standard decomposes for compatibility into NIKHAHIT and SARA AA, is taken apart where the font has both: the nikhahit goes before the
    /// above-base marks (tone marks, above vowels) typed before SARA AM, so that it sits under them on the base, as fonts and common shaping
    /// engines expect; the glyphs from the base's marks to the sara aa become one cluster.
    /// </summary>
    private void PrepareThai(List<ShapedGlyph> run, List<int> codePoints, Work work)
    {
        for (var i = 0; i < codePoints.Count && run.Count < MaxGlyphs; i++)
        {
            var cp = codePoints[i];
            if (cp is not (0x0E33 or 0x0EB3)) continue;
            var lao = cp == 0x0EB3;
            int nikhahit = lao ? 0x0ECD : 0x0E4D, aa = lao ? 0x0EB2 : 0x0E32;
            int gn = _font.GlyphForUnicode(nikhahit), ga = _font.GlyphForUnicode(aa);
            if (gn <= 0 || ga <= 0 || !work.Move(run.Count - i)) continue;
            var original = run[i];
            run[i] = original with { Glyph = gn, Class = 3 };
            run.Insert(i + 1, original with { Glyph = ga, Text = "", Class = 1 });
            codePoints[i] = nikhahit;
            codePoints.Insert(i + 1, aa);
            var j = i;
            while (j > 0 && i - j < MaxAboveMarks && SyllableData.PositionalOf(codePoints[j - 1]) == PositionalCategory.Top
                   && Rune.IsValid(codePoints[j - 1]) && Rune.GetUnicodeCategory(new Rune(codePoints[j - 1])) == UnicodeCategory.NonSpacingMark) j--;
            if (j < i)
            {
                Move(run, i, j);
                var moved = codePoints[i];
                codePoints.RemoveAt(i);
                codePoints.Insert(j, moved);
                MergeClusters(run, j, i + 2);
            }
            i++;
        }
    }

    // ------------------------------------------------------------ Hangul

    /// <summary>
    /// Composes conjoining jamo into precomposed syllables where the font has the syllable (The Unicode Standard, 3.12: modern leading consonant
    /// U+1100–1112 and vowel U+1161–1175, with a trailing consonant U+11A8–11C2 when the font has that syllable; a precomposed LV syllable with
    /// a trailing consonant) — only when no further jamo of the same syllable (an old leading consonant before, a second vowel, a trailing
    /// consonant that does not compose) would be left over. A syllable the font lacks stays as jamo for ljmo, vjmo and tjmo — and a precomposed LV syllable before a trailing
    /// consonant whose LVT syllable the font lacks is taken apart into its jamo when the font has them and ljmo, so that the jamo features form
    /// the whole syllable. The composed glyph stands for the text of all its jamo.
    /// </summary>
    private void ComposeHangul(List<ShapedGlyph> run, List<int> codePoints, string[] scripts, string[] language, Work work)
    {
        static bool L(int c) => c is >= 0x1100 and <= 0x1112;
        static bool V(int c) => c is >= 0x1161 and <= 0x1175;
        static bool T(int c) => c is >= 0x11A8 and <= 0x11C2;
        static bool Lv(int c) => c is >= 0xAC00 and <= 0xD7A3 && (c - 0xAC00) % 28 == 0;
        HangulSyllableType Kind(int k) => k >= 0 && k < codePoints.Count ? SyllableData.HangulOf(codePoints[k]) : HangulSyllableType.NotApplicable;
        DecomposeMissingSyllables(run, codePoints, scripts, language, work);
        for (var i = 0; i < codePoints.Count && work.Spend(1); i++)
        {
            var c = codePoints[i];
            int composed = -1, length = 0;
            // only a whole syllable: not when further leading consonants, vowels or trailing consonants of the same syllable stand around it
            if (L(c) && i + 1 < codePoints.Count && V(codePoints[i + 1]) && Kind(i - 1) != HangulSyllableType.LeadingJamo
                && Kind(i + 2) != HangulSyllableType.VowelJamo)
            {
                var lv = 0xAC00 + ((c - 0x1100) * 21 + (codePoints[i + 1] - 0x1161)) * 28;
                var t = i + 2 < codePoints.Count && T(codePoints[i + 2]) ? codePoints[i + 2] : 0;
                var syllable = t > 0 ? lv + t - 0x11A7 : lv;
                var whole = t > 0 ? Kind(i + 3) != HangulSyllableType.TrailingJamo : Kind(i + 2) != HangulSyllableType.TrailingJamo;
                if (whole && _font.GlyphForUnicode(syllable) > 0) (composed, length) = (syllable, t > 0 ? 3 : 2);
            }
            else if (Lv(c) && i + 1 < codePoints.Count && T(codePoints[i + 1]) && Kind(i + 2) != HangulSyllableType.TrailingJamo)
            {
                var lvt = c + codePoints[i + 1] - 0x11A7;
                if (_font.GlyphForUnicode(lvt) > 0) (composed, length) = (lvt, 2);
                else
                {
                    int l = 0x1100 + (c - 0xAC00) / (21 * 28), v = 0x1161 + (c - 0xAC00) % (21 * 28) / 28;
                    if (_font.GlyphForUnicode(l) > 0 && _font.GlyphForUnicode(v) > 0 && _font.GlyphForUnicode(codePoints[i + 1]) > 0
                        && _gsub?.LookupsFor(["ljmo"], scripts, language, required: false).Length > 0 && run.Count < MaxGlyphs && work.Move(run.Count - i))
                    {
                        var original = run[i];
                        run[i] = original with { Glyph = _font.GlyphForUnicode(l) };
                        run.Insert(i + 1, original with { Glyph = _font.GlyphForUnicode(v), Text = "" });
                        codePoints[i] = l;
                        codePoints.Insert(i + 1, v);
                        i += 2;
                    }
                }
            }
            if (composed < 0 || !work.Move(run.Count - i)) continue;
            var first = run[i];
            var text = string.Concat(Enumerable.Range(i, length).Select(k => run[k].Text));
            run[i] = first with { Glyph = _font.GlyphForUnicode(composed), Text = text, Class = 1 };
            run.RemoveRange(i + 1, length - 1);
            codePoints[i] = composed;
            codePoints.RemoveRange(i + 1, length - 1);
        }
    }

    /// <summary>
    /// A precomposed Hangul syllable the font lacks is taken apart (its canonical decomposition, The Unicode Standard 3.12): into its jamo where
    /// the font has them and forms them (ljmo), else into the LV syllable and the trailing consonant where the font has both, else into the jamo
    /// the font has; the first part keeps the syllable's text. In one pass (inserting part by part would move the rest of the run each time).
    /// Before, such a syllable was drawn as glyph 0 (docs/FEHLERGEDAECHTNIS.md, No. 1183).
    /// </summary>
    private void DecomposeMissingSyllables(List<ShapedGlyph> run, List<int> codePoints, string[] scripts, string[] language, Work work)
    {
        if (!codePoints.Exists(c => c is >= 0xAC00 and <= 0xD7A3 && _font.GlyphForUnicode(c) <= 0)) return;
        var jamoFeatures = _gsub?.LookupsFor(["ljmo"], scripts, language, required: false).Length > 0;
        var glyphs = new List<ShapedGlyph>(run.Count + 8);
        var points = new List<int>(run.Count + 8);
        for (var i = 0; i < run.Count; i++)
        {
            var c = codePoints[i];
            int[]? parts = null;
            if (c is >= 0xAC00 and <= 0xD7A3 && glyphs.Count + 3 < MaxGlyphs && work.Spend(1) && _font.GlyphForUnicode(c) <= 0)
            {
                var s = c - 0xAC00;
                int l = 0x1100 + s / (21 * 28), v = 0x1161 + s % (21 * 28) / 28, t = s % 28;
                int[] jamo = t == 0 ? [l, v] : [l, v, 0x11A7 + t];
                var hasJamo = jamo.All(p => _font.GlyphForUnicode(p) > 0);
                parts = hasJamo && jamoFeatures ? jamo
                    : t > 0 && _font.GlyphForUnicode(c - t) > 0 && _font.GlyphForUnicode(0x11A7 + t) > 0 ? [c - t, 0x11A7 + t]
                    : hasJamo ? jamo : null;
            }
            if (parts is null)
            {
                glyphs.Add(run[i]);
                points.Add(c);
                continue;
            }
            for (var k = 0; k < parts.Length; k++)
            {
                glyphs.Add(run[i] with { Glyph = _font.GlyphForUnicode(parts[k]), Text = k == 0 ? run[i].Text : "" });
                points.Add(parts[k]);
            }
        }
        if (!work.Move(glyphs.Count)) return;
        run.Clear();
        run.AddRange(glyphs);
        codePoints.Clear();
        codePoints.AddRange(points);
    }

    /// <summary>Longest Hangul syllable (in jamo) a tone mark looks back across.</summary>
    private const int MaxToneSyllable = 32;

    /// <summary>
    /// The Hangul tone marks U+302E and U+302F (Middle Korean) belong to the syllable before them — a precomposed syllable or conjoining jamo
    /// (L+ V+ T*, LV V* T*, LVT T*; The Unicode Standard, 3.12): a tone mark with an advance is drawn before its syllable (to its left, where
    /// it is written), the two one cluster; a tone mark without an advance stays after it, for the font to place. Without a syllable before it
    /// the dotted circle U+25CC (where the font has it; no text of its own) becomes its base, after a tone mark with an advance, before one
    /// without. Before, the tone mark always stood after its syllable and had no base without one (docs/FEHLERGEDAECHTNIS.md, No. 1179).
    /// </summary>
    private void HangulToneMarks(List<ShapedGlyph> run, List<int> codePoints, Work work)
    {
        if (!codePoints.Exists(c => c is 0x302E or 0x302F)) return;
        var dotted = _font.GlyphForUnicode(0x25CC);
        // built anew in one pass: a tone mark moves back across its syllable only (at most MaxToneSyllable glyphs), dotted circles are added
        // without moving the rest of the run
        var glyphs = new List<ShapedGlyph>(run.Count + 4);
        var points = new List<int>(run.Count + 4);
        HangulSyllableType Kind(int k) => SyllableData.HangulOf(points[k]);
        // the start of the syllable that ends at the end of the glyphs so far, -1 for none: trailing jamo, then an LVT syllable, or vowels and an
        // LV syllable or leading jamo
        int SyllableStart()
        {
            var k = points.Count - 1;
            var floor = Math.Max(0, points.Count - MaxToneSyllable);
            while (k >= floor && Kind(k) == HangulSyllableType.TrailingJamo) k--;
            if (k >= floor && Kind(k) == HangulSyllableType.LvtSyllable) return k;
            var vowels = 0;
            for (; k >= floor && Kind(k) == HangulSyllableType.VowelJamo; k--) vowels++;
            if (k >= floor && Kind(k) == HangulSyllableType.LvSyllable) return k;
            if (vowels == 0 || k < floor || Kind(k) != HangulSyllableType.LeadingJamo) return -1;
            while (k > floor && Kind(k - 1) == HangulSyllableType.LeadingJamo) k--;
            return k;
        }
        // the last glyph of a syllable that already took a tone mark (a second one has no syllable)
        var toned = -1;
        for (var i = 0; i < run.Count; i++)
        {
            var c = codePoints[i];
            if (c is not (0x302E or 0x302F) || glyphs.Count >= MaxGlyphs || !work.Spend(1))
            {
                glyphs.Add(run[i]);
                points.Add(c);
                continue;
            }
            var end = glyphs.Count;
            var start = end - 1 == toned ? -1 : SyllableStart();
            var spacing = _font.AdvanceWidth(run[i].Glyph) != 0;
            if (start >= 0 && spacing)
            {
                glyphs.Insert(start, run[i]);
                points.Insert(start, c);
                MergeClusters(glyphs, start, end + 1);
                toned = end;
                continue;
            }
            if (start >= 0 || dotted <= 0)
            {
                glyphs.Add(run[i]);
                points.Add(c);
                continue;
            }
            var circle = run[i] with { Glyph = dotted, Text = "", Class = 1 };
            glyphs.Add(spacing ? run[i] : circle);
            glyphs.Add(spacing ? circle : run[i]);
            points.Add(spacing ? c : 0x25CC);
            points.Add(spacing ? 0x25CC : c);
        }
        run.Clear();
        run.AddRange(glyphs);
        codePoints.Clear();
        codePoints.AddRange(points);
    }
}
