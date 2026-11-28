using System.Globalization;
using System.Text;
using PdfKnead.Core.Fonts.Layout;

namespace PdfKnead.Core.Pdf.Text;

/// <summary>
/// The folding the search runs over, the same for the page text and for the term (docs/FEHLERGEDAECHTNIS.md, No. 355, 1176, 1184), so that
/// what the Unicode Standard treats as the same text is found in both directions — on the page as well as in „Suchen und schwärzen“:
/// <list type="bullet">
/// <item>canonically equivalent text is the same (NFC per unit of a starter and the characters that combine with it): compatibility
/// ideographs with a canonical decomposition (U+F900 as U+8C48; the twelve unified ideographs of the block, U+FA0E …, have none and stay),
/// singletons (U+2126, U+212B), composition exclusions (U+0958, U+FB2A), Hangul syllables and jamo, marks outside the Basic Multilingual Plane
/// (Chakma, Gurung Khema, Kirat Rai — the last two from the figures of the standard, as the platform does not know Unicode 16.0.0 yet);</item>
/// <item>format characters are passed over (Unicode 23.2.2, 23.2.4, 23.9.1): directional formatting characters, soft hyphen, ZWSP, WJ,
/// ZWNBSP, CGJ, tags, MVS, invisible operators — ZWJ and ZWNJ too, except after a virama or invisible stacker, where they keep the
/// distinction of the Indic orthographies (23.2.2);</item>
/// <item>Arabic presentation forms as the letters they show (NFKC, No. 1176);</item>
/// <item>the spellings the standard asks to treat as equivalent: Tamil shri with sha or sa (12.6.3), Malayalam ṉṯa and the chillus of
/// Unicode 5.0 (12.9.3; the ambiguous na, virama, ZWJ, rra stays), Balinese surang, cecek and bisah as ra, nga and ha (17.3.1), Tibetan
/// U+0F35/U+0F37 left out (13.4.1);</item>
/// <item>the Kangxi radicals and the CJK radicals of the supplement as the unified ideographs they are equivalent to (18.1.14; the property
/// Equivalent_Unified_Ideograph of the UCD 16.0.0, which for the Kangxi radicals is their compatibility decomposition) — producers write them
/// into /ToUnicode for the ideograph they look like (No. 1194).</item>
/// </list>
/// The text read from the page is not changed — only what the search compares. Each folded character knows the place in the source it came
/// from, so a hit marks exactly the glyphs whose folded text it covers.
/// </summary>
internal static partial class SearchFolding
{
    /// <summary>
    /// Folds a text for the search.
    /// </summary>
    /// <param name="text">The page text or the search term.</param>
    /// <param name="page">Page text: line breaks as spaces, a hyphen at the end of a line before a small letter joins the word (No. 355).</param>
    /// <returns>The folded text and, for each of its characters, its place in <paramref name="text"/> (and one more entry: the end).</returns>
    public static (string Text, int[] Map) Fold(string text, bool page)
    {
        var sb = new StringBuilder(text.Length);
        var map = new List<int>(text.Length + 1);
        var piece = new StringBuilder();
        var previous = -1;   // the last code point kept, for the joiners after a virama
        for (var i = 0; i < text.Length;)
        {
            var cp = CodePointAt(text, i, out var n);
            if (page && cp == 0x00AD && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i += 2;   // a soft hyphen at the end of a line (drawn as a hyphen where the line broke): the word goes on (No. 1185)
                continue;
            }
            if (Ignorable(cp) && !KeptJoiner(cp, previous))
            {
                i += n;
                continue;
            }
            if (page && cp == '-' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                // a hyphen at the end of a line: joins the word before a small letter, otherwise stays without the break (No. 355)
                if (!(i > 0 && char.IsLetter(text[i - 1]) && i + 2 < text.Length && char.IsLower(text[i + 2])))
                {
                    sb.Append('-');
                    map.Add(i);
                    previous = '-';
                }
                i += 2;
                continue;
            }
            // the unit: the character and what combines with it (marks, Hangul vowels and trailing consonants, the second parts of the
            // composed vowels); format characters within it are passed over. At most MaxUnit code points (the stream-safe limit of UAX #15):
            // a crafted run of marks is normalised in pieces, the work stays linear
            var last = cp;
            var j = i + n;
            var count = 1;
            while (j < text.Length && count++ < MaxUnit)
            {
                var next = CodePointAt(text, j, out var m);
                if (next is 0x200C or 0x200D && IsVirama(last)) break;
                if (page && next == 0x00AD && j + 1 < text.Length && text[j + 1] == '\n') break;
                if (!Continues(next) && !Ignorable(next)) break;
                if (!Ignorable(next)) last = next;
                j += m;
            }
            var folded = FoldUnit(text, i, j, piece);
            foreach (var c in folded)
            {
                sb.Append(page && c == '\n' ? ' ' : c);
                map.Add(i);
            }
            if (folded.Length > 0) previous = Core.Fonts.Layout.TextRunes.Last(folded).Value;
            i = j;
        }
        map.Add(text.Length);
        return Spellings(sb, map);
    }

    /// <summary>The most code points of one unit normalised together (30 non-starters after a starter, UAX #15 stream-safe format).</summary>
    internal const int MaxUnit = 32;

    /// <summary>
    /// Whether a text needs no folding: in NFC and without any character the folding changes — then the search runs over the text itself.
    /// </summary>
    public static bool Unchanged(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c < 0x00AD) continue;
            if (char.IsSurrogate(c)) return false;   // marks and format characters outside the BMP, unpaired surrogates
            if (Ignorable(c) || c is '\u200C' or '\u200D' || IsMark(c) || IsPresentationForm(c) || IsRadical(c) || c is >= 'ᄀ' and <= 'ᇿ'
                or >= 'ힰ' and <= '퟿' or >= '஀' and <= '௿' or >= '\u0D00' and <= 'ൿ' or >= '\u1B00' and <= '᭿'
                or >= '豈' and <= '﫿')
                return false;
        }
        try
        {
            return text.IsNormalized(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The folded text of one unit (see <see cref="Fold"/>).</summary>
    private static string FoldUnit(string text, int start, int end, StringBuilder piece)
    {
        piece.Clear();
        var valid = true;
        for (var i = start; i < end;)
        {
            var cp = CodePointAt(text, i, out var n);
            if (n == 1 && char.IsSurrogate(text[i])) valid = false;
            i += n;
            if (i - n > start && Ignorable(cp)) continue;   // format characters within the unit (the first one is a kept joiner)
            if (cp is 0x0F35 or 0x0F37) continue;            // Tibetan marks of emphasis, passed over in comparisons (13.4.1)
            if (cp <= 0xFFFF && IsPresentationForm((char)cp))
                piece.Append(Folded((char)cp));
            else if (Radical(cp) is var ideograph && ideograph > 0)
                piece.Append(char.ConvertFromUtf32(ideograph));
            else
                piece.Append(text, i - n, n);
        }
        if (piece.Length == 1 && piece[0] < 0x0300) return piece[0].ToString();
        var result = piece.ToString();
        if (valid)
        {
            try
            {
                result = result.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                // (not normalised: the text stays as it is)
            }
            result = Unicode16.Composed(result);
        }
        // Balinese: surang as ra, cecek as nga, bisah as ha for searching (17.3.1)
        if (result.AsSpan().IndexOfAnyInRange('\u1B02', '\u1B04') >= 0)
            result = result.Replace('\u1B02', 'ᬗ').Replace('\u1B03', 'ᬭ').Replace('\u1B04', 'ᬳ');
        return result;
    }

    /// <summary>
    /// The spellings the standard asks to treat as the same, folded to one of them: Tamil shri with sha as with sa (12.6.3); the Malayalam
    /// chillus of Unicode 5.0 (consonant, virama, ZWJ) as the atomic chillus U+0D7A–U+0D7E, ṉṯa written na, virama, rra as chillu-n, virama, rra
    /// (12.9.3); na, virama, ZWJ, rra is ambiguous (stacked or side by side) and stays.
    /// </summary>
    private static (string Text, int[] Map) Spellings(StringBuilder sb, List<int> map)
    {
        var length = sb.Length;
        var changed = false;
        for (var k = 0; k + 2 < length && !changed; k++)
            if (sb[k] is 'ஶ' or 'ണ' or 'ന' or 'ര' or 'ല' or 'ള') changed = true;
        if (!changed) return (sb.ToString(), [.. map]);
        var outText = new StringBuilder(length);
        var outMap = new List<int>(map.Count);
        for (var k = 0; k < length; k++)
        {
            var c = sb[k];
            if (c == 'ஶ' && k + 3 < length && sb[k + 1] == '\u0BCD' && sb[k + 2] == 'ர' && sb[k + 3] == '\u0BC0')
                c = 'ஸ';
            else if (c is 'ണ' or 'ന' or 'ര' or 'ല' or 'ള' && k + 2 < length && sb[k + 1] == '\u0D4D' && sb[k + 2] == '\u200D'
                     && !(c == 'ന' && k + 3 < length && sb[k + 3] == 'റ'))
            {
                outText.Append(c switch { 'ണ' => 'ൺ', 'ന' => 'ൻ', 'ര' => 'ർ', 'ല' => 'ൽ', _ => 'ൾ' });
                outMap.Add(map[k]);
                k += 2;
                continue;
            }
            else if (c == 'ന' && k + 2 < length && sb[k + 1] == '\u0D4D' && sb[k + 2] == 'റ')
            {
                c = 'ൻ';
            }
            outText.Append(c);
            outMap.Add(map[k]);
        }
        outMap.Add(map[^1]);
        return (outText.ToString(), [.. outMap]);
    }

    /// <summary>The code point at a place; an unpaired surrogate as itself.</summary>
    private static int CodePointAt(string text, int i, out int length)
    {
        if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
        {
            length = 2;
            return char.ConvertToUtf32(text[i], text[i + 1]);
        }
        length = 1;
        return text[i];
    }

    /// <summary>Whether a character combines with the one before it (a mark, a Hangul vowel or trailing consonant, Kirat Rai E).</summary>
    private static bool Continues(int cp) => IsMark(cp) || cp is >= 0x1160 and <= 0x11FF or >= 0xD7B0 and <= 0xD7FF || Unicode16.IsSecond(cp);

    /// <summary>Whether a code point is a mark (Mn, Mc, Me), also outside the Basic Multilingual Plane.</summary>
    internal static bool IsMark(int cp) => cp is < 0xD800 or > 0xDFFF && CharUnicodeInfo.GetUnicodeCategory(cp) is UnicodeCategory.NonSpacingMark
        or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    /// <summary>
    /// Format characters the search passes over (Unicode 23.2, 23.9.1): directional formatting characters (No. 1173), soft hyphen, CGJ,
    /// Mongolian vowel separator, ZWSP, ZWNJ, ZWJ, WJ, the invisible operators, the deprecated format characters, ZWNBSP, the shorthand format
    /// controls and the tags.
    /// </summary>
    internal static bool Ignorable(int cp) => Bidi.IsControl(cp) || cp is 0x00AD or 0x034F or 0x180E or >= 0x200B and <= 0x200D
        or >= 0x2060 and <= 0x2064 or >= 0x206A and <= 0x206F or 0xFEFF or >= 0x1BCA0 and <= 0x1BCA3 or 0xE0001 or >= 0xE0020 and <= 0xE007F;

    /// <summary>ZWNJ and ZWJ after a virama or invisible stacker keep the distinction of the Indic orthographies (23.2.2).</summary>
    private static bool KeptJoiner(int cp, int previous) => cp is 0x200C or 0x200D && IsVirama(previous);

    private static bool IsVirama(int cp) => cp > 0 && SyllableData.SyllabicOf(cp) is SyllabicCategory.Virama or SyllabicCategory.InvisibleStacker;

    /// <summary>Whether a character is an Arabic presentation form (U+FB50–U+FDFF, U+FE70–U+FEFF; Unicode 9.2.11, 9.2.12).</summary>
    internal static bool IsPresentationForm(char c) => c is >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻾';

    /// <summary>Whether a character lies in the blocks of the CJK radicals (Supplement U+2E80–U+2EFF, Kangxi U+2F00–U+2FDF).</summary>
    private static bool IsRadical(char c) => c is >= (char)FirstRadical and <= (char)LastRadical;

    /// <summary>
    /// The unified ideograph a Kangxi radical or a CJK radical of the supplement is equivalent to (Equivalent_Unified_Ideograph, UCD 16.0.0;
    /// generated table <see cref="RadicalIdeographs"/>); 0 for any other character and for a radical without one (U+2E80). Only the search folds
    /// them (The Unicode Standard 18.1.14: radicals and ideographs are different characters; the text read stays the radical; No. 1194).
    /// </summary>
    internal static int Radical(int cp) => cp is >= FirstRadical and <= LastRadical ? RadicalIdeographs[cp - FirstRadical] : 0;

    /// <summary>The folded presentation forms, from U+FB50 (U+FB50–U+FDFF, then U+FE70–U+FEFF), made on first use.</summary>
    private static readonly Lazy<string[]> FoldedForms = new(() =>
    {
        var forms = new string[(0xFDFF - 0xFB50 + 1) + (0xFEFF - 0xFE70 + 1)];
        for (var i = 0; i < forms.Length; i++)
        {
            var c = (char)(i <= 0xFDFF - 0xFB50 ? 0xFB50 + i : 0xFE70 + i - (0xFDFF - 0xFB50 + 1));
            string folded;
            try
            {
                folded = c.ToString().Normalize(NormalizationForm.FormKC);
            }
            catch (ArgumentException)
            {
                folded = c.ToString();   // (no decomposition known to the platform: the character stays)
            }
            // the spacing forms of the marks (U+FE70–U+FE7F) decompose to a space or a tatweel that carries the mark: the mark alone, as it
            // follows its letter in typed text
            if (c is >= 'ﹰ' and <= 'ﹿ' && folded.Length == 2 && folded[0] is ' ' or 'ـ') folded = folded[1..];
            forms[i] = folded;
        }
        return forms;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// A presentation form as the letters it shows, for the search (Unicode 9.2.11, 9.2.12: compatibility characters; their compatibility
    /// decomposition, NFKC): „ﻻ“ U+FEFB as lam and alef, the final beh U+FE90 as beh (docs/FEHLERGEDAECHTNIS.md, No. 1176).
    /// </summary>
    internal static string Folded(char c) => FoldedForms.Value[c <= '﷿' ? c - 0xFB50 : c - 0xFE70 + (0xFDFF - 0xFB50 + 1)];

    /// <summary>
    /// Canonical decompositions the normalisation of the runtime or of the system does not know (measured 27.11.2026: Todhri, Tulu-Tigalari,
    /// Gurung Khema, Kirat Rai of Unicode 16, on Windows also Dives Akuru): the one table of the shaper, generated from UnicodeData.txt 16.0.0
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1188; before, the search had its own table of the 11 of Gurung Khema and Kirat Rai). None is excluded
    /// from composition, so NFC holds the composed form.
    /// </summary>
    internal static class Unicode16
    {
        /// <summary>Composed character → its two parts.</summary>
        internal static IReadOnlyDictionary<int, (int First, int Second)> Decompositions => TextShaper.Decompositions16;

        private static readonly Dictionary<(int, int), int> Compositions = Decompositions.ToDictionary(p => p.Value, p => p.Key);

        /// <summary>The second parts that are not marks (Kirat Rai E is a letter): they still combine with the character before them.</summary>
        private static readonly HashSet<int> SecondLetters = [.. Decompositions.Values.Select(p => p.Second).Where(cp => !IsMark(cp))];

        /// <summary>Whether a code point is the second part of one of the compositions and not a mark.</summary>
        internal static bool IsSecond(int cp) => SecondLetters.Contains(cp);

        /// <summary>The text with these characters in NFC: decomposed fully, then composed from the left.</summary>
        internal static string Composed(string text)
        {
            if (!text.Any(char.IsHighSurrogate)) return text;   // (all these characters lie outside the Basic Multilingual Plane)
            var decomposed = new List<int>(text.Length);
            foreach (var rune in text.EnumerateRunes()) Decompose(rune.Value, decomposed);
            var composed = new List<int>(decomposed.Count);
            foreach (var cp in decomposed)
                if (composed.Count > 0 && Compositions.TryGetValue((composed[^1], cp), out var both))
                    composed[^1] = both;
                else
                    composed.Add(cp);
            var sb = new StringBuilder(text.Length);
            foreach (var cp in composed) sb.Append(char.ConvertFromUtf32(cp));
            return sb.ToString();
        }

        private static void Decompose(int cp, List<int> into)
        {
            if (Decompositions.TryGetValue(cp, out var parts))
            {
                Decompose(parts.First, into);
                into.Add(parts.Second);
            }
            else
            {
                into.Add(cp);
            }
        }
    }
}
