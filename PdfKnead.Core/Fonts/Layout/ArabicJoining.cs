using System.Globalization;
using System.Text;

namespace PdfKnead.Core.Fonts.Layout;

/// <summary>
/// Cursive joining of Arabic script (The Unicode Standard, chapter 9.2 „Arabic“, „Cursive Joining“, with the joining types of the Unicode
/// Character Database, ArabicShaping.txt 16.0.0, shipped in Fonts/Data): the form each letter takes in a text — isolated, initial, medial or
/// final — which selects the GSUB features isol, init, medi and fina of the OpenType Arabic script („Developing OpenType Fonts for Arabic
/// Script“). Written from the standard and the specification; no code of another shaper was used (docs/FEHLERGEDAECHTNIS.md, No. 910).
/// </summary>
internal static class ArabicJoining
{
    /// <summary>
    /// The form of a letter (<see cref="None"/>: no form feature, for a mark, a joining-causing or a non-joining character). The last three are
    /// the forms of the Syriac letter Alaph (The Unicode Standard 9.3, rules S1–S3; OpenType fin2, fin3, med2): at the end of a word after a
    /// letter that does not join (Afn, fin2), after Dalath or Rish (Afx, fin3), and inside a word joined to the letter before it (med2).
    /// </summary>
    internal enum Form : byte { None, Isolated, Initial, Medial, Final, Final2, Final3, Medial2 }

    /// <summary>The Joining_Group values the shaper needs (ArabicShaping.txt, field 4).</summary>
    internal enum Group : byte { Other, Alaph, DalathRish }

    private static readonly Lazy<(Dictionary<int, char> Types, Dictionary<int, Group> Groups)> Data = new(Load);

    /// <summary>The joining types (field 3: R, L, D, C, U, T) and the joining groups Alaph and Dalath_Rish (field 4) listed in ArabicShaping.txt.</summary>
    private static (Dictionary<int, char>, Dictionary<int, Group>) Load()
    {
        var types = new Dictionary<int, char>();
        var groups = new Dictionary<int, Group>();
        using var stream = typeof(ArabicJoining).Assembly.GetManifestResourceStream("Fonts/ArabicShaping.txt");
        if (stream is null) return (types, groups);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            var fields = line.Split(';');
            if (fields.Length < 3 || !int.TryParse(fields[0].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp)) continue;
            var type = fields[2].Trim();
            if (type.Length == 1) types[cp] = type[0];
            if (fields.Length < 4) continue;
            var group = fields[3].Trim() switch { "ALAPH" => Group.Alaph, "DALATH RISH" => Group.DalathRish, _ => Group.Other };
            if (group != Group.Other) groups[cp] = group;
        }
        return (types, groups);
    }

    /// <summary>The joining group of a character, as far as the shaper needs it (Alaph, Dalath_Rish), else <see cref="Group.Other"/>.</summary>
    internal static Group GroupOf(int codePoint) => Data.Value.Groups.TryGetValue(codePoint, out var group) ? group : Group.Other;

    /// <summary>
    /// The joining type of a character: as listed; otherwise T (transparent) for nonspacing and enclosing marks and format characters
    /// (except ZERO WIDTH NON-JOINER, which breaks the joining: U), else U (non-joining) — the defaults of the standard.
    /// </summary>
    internal static char TypeOf(int codePoint)
    {
        if (Data.Value.Types.TryGetValue(codePoint, out var type)) return type;
        if (codePoint == 0x200C) return 'U';
        return CharUnicodeInfo.GetUnicodeCategory(codePoint) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format
            ? 'T' : 'U';
    }

    /// <summary>Whether a character is a letter of Arabic script that joins (types R, L, D) — the text then needs Arabic shaping.</summary>
    internal static bool Joins(int codePoint) => TypeOf(codePoint) is 'R' or 'L' or 'D';

    /// <summary>
    /// The form of each character (in text order): a letter joins its neighbour before it when it can join on that side (R, D, C) and the
    /// neighbour on its other side (D, L, C); transparent characters (marks) are skipped and take no form. Both sides joined: medial; only
    /// the one before: final; only the one after: initial; none: isolated. The sides are those of the text order in every script: L joins
    /// the character after it, R the one before — also in Phags-pa, written from left to right, where The Unicode Standard (14.4, „Cursive
    /// Joining“) gives L as joining the character below, the following one, and the bundled shaping library forms it so; the feature
    /// registry's reading of L and R in left-to-right scripts (fina, init) is not followed (docs/normabgleich/sonderregeln.md, A286). The
    /// Syriac Alaph (Joining_Group Alaph) takes the forms of rules S1–S3 of The Unicode Standard (9.3) and the OpenType features fin2, fin3,
    /// med2: joined to the letter before it, final at the end of a word (a
    /// non-joining character or the end of the text after it) and <see cref="Form.Medial2"/> inside a word; after a right-joining letter that
    /// does not join it, <see cref="Form.Final3"/> at the end of a word when that letter is of the group Dalath_Rish, else
    /// <see cref="Form.Final2"/>; inside a word, or with no letter before it (a space, a non-joining character, the start), isolated. Before,
    /// the Alaph took only the four general forms (docs/FEHLERGEDAECHTNIS.md, No. 1174).
    /// </summary>
    internal static Form[] Forms(IReadOnlyList<int> codePoints)
    {
        var n = codePoints.Count;
        var types = new char[n];
        for (var i = 0; i < n; i++) types[i] = TypeOf(codePoints[i]);
        var forms = new Form[n];
        for (var i = 0; i < n; i++)
        {
            var t = types[i];
            if (t is not ('R' or 'L' or 'D')) continue;
            var before = i - 1;
            while (before >= 0 && types[before] == 'T') before--;
            var after = i + 1;
            while (after < n && types[after] == 'T') after++;
            var joinsBefore = t is 'R' or 'D' && before >= 0 && types[before] is 'D' or 'L' or 'C';
            var joinsAfter = t is 'D' or 'L' && after < n && types[after] is 'R' or 'D' or 'C';
            if (GroupOf(codePoints[i]) == Group.Alaph)
            {
                var wordEnd = after >= n || types[after] == 'U';
                forms[i] = joinsBefore ? (wordEnd ? Form.Final : Form.Medial2)
                    : !wordEnd || before < 0 || types[before] != 'R' ? Form.Isolated
                    : GroupOf(codePoints[before]) == Group.DalathRish ? Form.Final3 : Form.Final2;
                continue;
            }
            forms[i] = (joinsBefore, joinsAfter) switch
            {
                (true, true) => Form.Medial,
                (true, false) => Form.Final,
                (false, true) => Form.Initial,
                _ => Form.Isolated,
            };
        }
        return forms;
    }
}
