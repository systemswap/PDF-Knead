namespace PdfKnead.Core.Fonts.Layout;

/// <summary>
/// The vowel sequences the Unicode Standard says not to use (chapters 12 and 13: the tables of the vowel letters that can be analyzed, for
/// Devanagari, Bengali, Gurmukhi, Gujarati, Oriya, Tamil, Telugu, Kannada, Malayalam and Sinhala): a vowel letter written as another vowel
/// letter and a vowel sign (or as Ra, virama and a vowel letter), and a few vowel signs written as two signs. Such a sequence is shaped like a
/// broken syllable — a dotted circle (U+25CC, where the font has it, without a text of its own) before the part that should not follow, so
/// that the reader sees the misspelling — instead of looking like the vowel letter it imitates. Taken from the tables of the standard in own
/// words as code points; the Bengali vowel letters of Kokborok (U+0985 U+09D7, U+0989 U+09BE) and the Gurmukhi bindi before the vowel sign
/// II are allowed sequences and not listed. Before, these sequences were drawn without any mark (docs/FEHLERGEDAECHTNIS.md, No. 1183).
/// </summary>
public sealed partial class TextShaper
{
    /// <summary>One sequence not to use: its code points, the index of the code point the dotted circle goes before, the character to use.</summary>
    internal readonly record struct DiscouragedSequence(int[] Sequence, int Circle, int Instead);

    private static DiscouragedSequence D(int instead, params int[] sequence) => new(sequence, 1, instead);

    /// <summary>The sequences not to use, by script in the order of the tables of the standard.</summary>
    internal static readonly DiscouragedSequence[] DiscouragedSequences =
    [
        // Devanagari (The Unicode Standard 12.1.2, table of the vowel letters)
        D(0x0904, 0x0905, 0x0946), D(0x0906, 0x0905, 0x093E),
        // Ra, virama and the vowel letter I: the circle before the vowel letter (Ra and virama make a reph on the circle)
        new([0x0930, 0x094D, 0x0907], 2, 0x0908),
        D(0x090A, 0x0909, 0x0941), D(0x090D, 0x090F, 0x0945), D(0x090E, 0x090F, 0x0946), D(0x0910, 0x090F, 0x0947),
        D(0x0911, 0x0905, 0x0949), D(0x0911, 0x0906, 0x0945), D(0x0912, 0x0905, 0x094A), D(0x0912, 0x0906, 0x0946),
        D(0x0913, 0x0905, 0x094B), D(0x0913, 0x0906, 0x0947), D(0x0914, 0x0905, 0x094C), D(0x0914, 0x0906, 0x0948),
        D(0x0972, 0x0905, 0x0945), D(0x0973, 0x0905, 0x093A), D(0x0974, 0x0905, 0x093B), D(0x0974, 0x0906, 0x093A),
        D(0x0975, 0x0905, 0x094F), D(0x0976, 0x0905, 0x0956), D(0x0977, 0x0905, 0x0957),
        // Bengali (12.2.1)
        D(0x0986, 0x0985, 0x09BE), D(0x09E0, 0x098B, 0x09C3), D(0x09E1, 0x098C, 0x09E2),
        // Gurmukhi (12.3.1)
        D(0x0A06, 0x0A05, 0x0A3E), D(0x0A07, 0x0A72, 0x0A3F), D(0x0A08, 0x0A72, 0x0A40), D(0x0A09, 0x0A73, 0x0A41),
        D(0x0A0A, 0x0A73, 0x0A42), D(0x0A0F, 0x0A72, 0x0A47), D(0x0A10, 0x0A05, 0x0A48), D(0x0A13, 0x0A73, 0x0A4B),
        D(0x0A14, 0x0A05, 0x0A4C),
        // Gujarati (12.4.1); the three-part spellings of O and AU begin with A and AA, which is marked already
        D(0x0A86, 0x0A85, 0x0ABE), D(0x0A8D, 0x0A85, 0x0AC5), D(0x0A8F, 0x0A85, 0x0AC7), D(0x0A90, 0x0A85, 0x0AC8),
        D(0x0A91, 0x0A85, 0x0AC9), D(0x0A93, 0x0A85, 0x0ACB), D(0x0A93, 0x0A85, 0x0ABE, 0x0AC5), D(0x0A94, 0x0A85, 0x0ACC),
        D(0x0A94, 0x0A85, 0x0ABE, 0x0AC8), D(0x0AC9, 0x0AC5, 0x0ABE),
        // Oriya (12.5.1)
        D(0x0B06, 0x0B05, 0x0B3E), D(0x0B10, 0x0B0F, 0x0B57), D(0x0B14, 0x0B13, 0x0B57),
        // Tamil (12.6.2)
        D(0x0B86, 0x0B85, 0x0BC2),
        // Telugu (12.7.1)
        D(0x0C13, 0x0C12, 0x0C55), D(0x0C14, 0x0C12, 0x0C4C), D(0x0C40, 0x0C3F, 0x0C55), D(0x0C47, 0x0C46, 0x0C55),
        D(0x0C4B, 0x0C4A, 0x0C55),
        // Kannada (12.8.2)
        D(0x0C8A, 0x0C89, 0x0CBE), D(0x0C94, 0x0C92, 0x0CCC), D(0x0CE0, 0x0C8B, 0x0CBE),
        // Malayalam (12.9.1)
        D(0x0D08, 0x0D07, 0x0D57), D(0x0D0A, 0x0D09, 0x0D57), D(0x0D10, 0x0D0E, 0x0D46), D(0x0D13, 0x0D12, 0x0D3E),
        D(0x0D14, 0x0D12, 0x0D57),
        // Sinhala (13.2.1)
        D(0x0D86, 0x0D85, 0x0DCF), D(0x0D87, 0x0D85, 0x0DD0), D(0x0D88, 0x0D85, 0x0DD1), D(0x0D8C, 0x0D8B, 0x0DDF),
        D(0x0D8E, 0x0D8D, 0x0DD8), D(0x0D90, 0x0D8F, 0x0DDF), D(0x0D92, 0x0D91, 0x0DCA), D(0x0D93, 0x0D91, 0x0DD9),
        D(0x0D96, 0x0D94, 0x0DDF),
    ];

    /// <summary>The sequences by their first code point, the longer ones first.</summary>
    private static readonly Lazy<Dictionary<int, DiscouragedSequence[]>> DiscouragedByFirst = new(() =>
        DiscouragedSequences.GroupBy(d => d.Sequence[0]).ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.Sequence.Length).ToArray()));

    /// <summary>The first code point of the canonical decomposition of an Indic character (-1 if it has none).</summary>
    private static int FirstOfDecomposition(int c)
    {
        if (c is < 0x0900 or > 0x0DFF) return -1;
        var single = char.ConvertFromUtf32(c);
        var decomposed = single.Normalize(System.Text.NormalizationForm.FormD);
        return decomposed == single ? -1 : char.ConvertToUtf32(decomposed, 0);
    }

    /// <summary>
    /// Puts a dotted circle before the part of each sequence not to use (<see cref="DiscouragedSequences"/>), where the font has the glyph: a
    /// glyph without a text of its own, in the cluster of the character after it; the syllables then make it the base of that part. In the
    /// characters as typed (before the vowel signs written in parts are decomposed), in one pass.
    /// </summary>
    private void MarkDiscouragedSequences(List<ShapedGlyph> run, List<int> codePoints, Work work)
    {
        var dotted = _font.GlyphForUnicode(0x25CC);
        if (dotted <= 0) return;
        List<int>? at = null;
        for (var i = 0; i + 1 < codePoints.Count; i++)
        {
            if (!DiscouragedByFirst.Value.TryGetValue(codePoints[i], out var candidates) || !work.Spend(candidates.Length)) continue;
            foreach (var d in candidates)
            {
                var n = d.Sequence.Length;
                if (i + n > codePoints.Count) continue;
                var k = 1;
                while (k < n && codePoints[i + k] == d.Sequence[k]) k++;
                // the last part also as the start of a vowel sign written in parts (Sinhala E with the sign EE: U+0D91 U+0DDA is canonically
                // U+0D91 U+0DD9 U+0DCA), so that canonically equivalent spellings are marked alike
                if (k == n - 1 && FirstOfDecomposition(codePoints[i + k]) == d.Sequence[k]) k++;
                if (k < n) continue;
                (at ??= []).Add(i + d.Circle);
                i += n - 1;
                break;
            }
        }
        if (at is null || run.Count + at.Count > MaxGlyphs || !work.Move(run.Count)) return;
        var glyphs = new List<ShapedGlyph>(run.Count + at.Count);
        var points = new List<int>(run.Count + at.Count);
        var next = 0;
        for (var i = 0; i < run.Count; i++)
        {
            if (next < at.Count && at[next] == i)
            {
                glyphs.Add(new ShapedGlyph { Glyph = dotted, Cluster = run[i].Cluster, Text = "", Class = 1, Gates = run[i].Gates });
                points.Add(0x25CC);
                next++;
            }
            glyphs.Add(run[i]);
            points.Add(codePoints[i]);
        }
        run.Clear();
        run.AddRange(glyphs);
        codePoints.Clear();
        codePoints.AddRange(points);
    }
}
