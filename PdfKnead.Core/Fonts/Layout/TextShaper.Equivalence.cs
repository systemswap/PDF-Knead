using System.Globalization;
using System.Text;

namespace PdfKnead.Core.Fonts.Layout;

/// <summary>
/// Canonically equivalent spellings in Tibetan, Myanmar and Thai drawn alike (docs/FEHLERGEDAECHTNIS.md, No. 1183): the Tibetan compound vowels
/// and aspirated letters taken apart, the Myanmar vowel letter UU composed, and in Thai the consonant diacritics of the general combining marks
/// placed inside the vowel signs and tone marks of their side. Written from the Unicode Standard (3.11, 13.4, 16.1, 16.3); no code of another
/// shaper was used.
/// </summary>
public sealed partial class TextShaper
{
    /// <summary>
    /// Takes apart the Tibetan characters with a canonical decomposition, where the font has the glyphs of the parts: the compound vowel signs
    /// U+0F73, U+0F75, U+0F76, U+0F78, U+0F81 and the letters with subjoined HA or SSA (U+0F43 … U+0F69, U+0F93 … U+0FB9). All of them are
    /// excluded from composition — their normalized form is the sequence — so that a text typed either way gets the same glyphs (The Unicode
    /// Standard 13.4.1: the compound vowels may be entered as their parts and rendered appropriately; the aspirated letters decompose in every
    /// normalization form). The first part keeps the character's text, the others have none; the font's ligatures and forms then draw the
    /// sequence. The deprecated U+0F77 and U+0F79 have only compatibility decompositions and stay. Before, U+0F73 drew the glyph of the cmap and
    /// U+0F71 U+0F72 another sequence (measured with Microsoft Himalaya together with the canonical order, <see cref="CanonicalOrder"/>).
    /// </summary>
    private void DecomposeTibetan(List<ShapedGlyph> run, List<int> codePoints, Work work)
    {
        List<ShapedGlyph>? glyphs = null;
        List<int>? points = null;
        for (var i = 0; i < codePoints.Count; i++)
        {
            var c = codePoints[i];
            int[]? parts = null;
            if (c is >= 0x0F40 and <= 0x0FBC && work.Spend(1))
            {
                var single = char.ConvertFromUtf32(c);
                var decomposed = single.Normalize(NormalizationForm.FormD);
                if (decomposed != single)
                {
                    var p = decomposed.EnumerateRunes().Select(r => r.Value).ToArray();
                    if (p.Length <= 3 && p.All(x => _font.GlyphForUnicode(x) > 0)) parts = p;
                }
            }
            if (parts is null && glyphs is null) continue;
            if (glyphs is null || points is null)
            {
                // the run is built anew once the first character decomposes (inserting glyph by glyph would move the rest each time)
                glyphs = new List<ShapedGlyph>(run.Count + 8);
                points = new List<int>(run.Count + 8);
                for (var k = 0; k < i; k++)
                {
                    glyphs.Add(run[k]);
                    points.Add(codePoints[k]);
                }
            }
            if (parts is null || glyphs.Count + parts.Length >= MaxGlyphs)
            {
                glyphs.Add(run[i]);
                points.Add(c);
                continue;
            }
            var original = run[i];
            for (var k = 0; k < parts.Length; k++)
            {
                var mark = Rune.GetUnicodeCategory(new Rune(parts[k])) == UnicodeCategory.NonSpacingMark;
                glyphs.Add(original with { Glyph = _font.GlyphForUnicode(parts[k]), Text = k == 0 ? original.Text : "", Class = mark ? (byte)3 : (byte)1 });
                points.Add(parts[k]);
            }
        }
        if (glyphs is null || points is null || !work.Move(glyphs.Count)) return;
        run.Clear();
        run.AddRange(glyphs);
        codePoints.Clear();
        codePoints.AddRange(points);
    }

    /// <summary>
    /// After the canonical order in Tibetan: the vowel sign U (U+0F74, class 132) goes before the vowel signs above of class 130 (I, E, EE, O,
    /// OO, reversed I, U+0F72, U+0F7A–U+0F7D, U+0F80) that the canonical order puts before it — the signs below the stack stand next to it,
    /// as the fonts form them (Nya with U as one glyph, I above it). Marks of different classes are canonically equivalent in either order,
    /// so every equivalent spelling still gets the same glyphs; marks of one class keep their order. Measured with Microsoft Himalaya: with the
    /// plain canonical order 44 samples otherwise than the shaping library, none with this order (docs/FEHLERGEDAECHTNIS.md, No. 1183).
    /// </summary>
    private static void TibetanBelowVowelsFirst(List<ShapedGlyph> run, List<int> codePoints)
    {
        static bool AboveOf130(int c) => c is 0x0F72 or >= 0x0F7A and <= 0x0F7D or 0x0F80;
        for (var i = 1; i < codePoints.Count; i++)
        {
            if (codePoints[i] != 0x0F74 || !AboveOf130(codePoints[i - 1])) continue;
            var k = i;
            while (k > 0 && i - k < MaxCanonicalMarks && AboveOf130(codePoints[k - 1])) k--;
            Move(run, i, k);
            for (var m = i; m > k; m--) codePoints[m] = codePoints[m - 1];
            codePoints[k] = 0x0F74;
        }
    }

    /// <summary>
    /// Composes two neighbouring characters of the block <paramref name="first"/> … <paramref name="last"/> that compose canonically (NFC) into
    /// the composed character, where the font has its glyph — in Myanmar U+1025 U+102E into U+1026, so that the vowel letter UU looks the same
    /// typed either way (The Unicode Standard 3.11; before, the two spellings gave different glyphs). The composed glyph stands for the text of
    /// both characters.
    /// </summary>
    private void ComposeCanonically(List<ShapedGlyph> run, List<int> codePoints, int first, int last, Work work)
    {
        // compacted in one pass (removing glyph by glyph would move the rest of the run each time)
        var to = 0;
        for (var i = 0; i < codePoints.Count; i++, to++)
        {
            run[to] = run[i];
            codePoints[to] = codePoints[i];
            if (i + 1 >= codePoints.Count || !work.Spend(1)) continue;
            int a = codePoints[i], b = codePoints[i + 1];
            if (a < first || a > last || b < first || b > last || !Rune.IsValid(a) || !Rune.IsValid(b)) continue;
            if (Rune.GetUnicodeCategory(new Rune(b)) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)) continue;
            var composed = (char.ConvertFromUtf32(a) + char.ConvertFromUtf32(b)).Normalize(NormalizationForm.FormC).EnumerateRunes().ToArray();
            if (composed.Length != 1 || _font.GlyphForUnicode(composed[0].Value) is var glyph && glyph <= 0) continue;
            run[to] = run[i] with { Glyph = glyph, Text = run[i].Text + run[i + 1].Text, Class = 1 };
            codePoints[to] = composed[0].Value;
            i++;
        }
        if (to == run.Count) return;
        run.RemoveRange(to, run.Count - to);
        codePoints.RemoveRange(to, codePoints.Count - to);
    }

    /// <summary>Longest sequence of marks the Thai consonant diacritics are ordered in.</summary>
    private const int MaxThaiMarks = 30;

    /// <summary>
    /// Thai: a general combining mark below (canonical class 220, U+0331 COMBINING MACRON BELOW) after SARA U or SARA UU is drawn inside them,
    /// one above (class 230, U+0303 COMBINING TILDE) after a tone mark U+0E48–U+0E4B inside it — normalized text puts these marks after the Thai
    /// signs (classes 103 and 107), but SARA U and SARA UU must stand below the consonant diacritic and the tone mark above the tilde (The
    /// Unicode Standard 16.1.1). The mark moves before the first Thai sign of its side in the sequence of marks, so that the font's
    /// mark-to-mark stacks the Thai sign outside it; the consonant and its marks become one cluster with the text as typed. No other
    /// canonical reordering in Thai (fonts are built for the typed order there, docs/FEHLERGEDAECHTNIS.md, No. 1179). Before, the macron below
    /// stood under SARA U and the tilde over the tone mark (No. 1183).
    /// </summary>
    private static void ThaiConsonantDiacritics(List<ShapedGlyph> run, List<int> codePoints, Work work)
    {
        static bool Below(int c) => c is 0x0E38 or 0x0E39;
        static bool Tone(int c) => c is >= 0x0E48 and <= 0x0E4B;
        for (var i = 0; i < codePoints.Count;)
        {
            if ((MarkInfo(codePoints[i]) & NonStarter) == 0)
            {
                i++;
                continue;
            }
            var start = i;
            while (i < codePoints.Count && (MarkInfo(codePoints[i]) & NonStarter) != 0) i++;
            if (i - start is < 2 or > MaxThaiMarks || !work.Spend(i - start)) continue;
            var moved = false;
            for (var k = start + 1; k < i; k++)
            {
                var info = MarkInfo(codePoints[k]);
                Func<int, bool>? inside = codePoints[k] is >= 0x0E00 and <= 0x0E7F ? null
                    : (info & ClassBelow) != 0 ? Below : (info & ClassAbove) != 0 ? Tone : null;
                if (inside is null) continue;
                var j = start;
                while (j < k && !inside(codePoints[j])) j++;
                if (j == k) continue;
                // within the sequence only (removing and inserting would move the rest of the run)
                Move(run, k, j);
                var c = codePoints[k];
                for (var m = k; m > j; m--) codePoints[m] = codePoints[m - 1];
                codePoints[j] = c;
                moved = true;
            }
            if (moved) MergeClusters(run, start > 0 && (MarkInfo(codePoints[start - 1]) & NonStarter) == 0 ? start - 1 : start, i);
        }
    }
}
