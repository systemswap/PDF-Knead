using System.Globalization;
using System.Text;

namespace PdfKnead.Core.Fonts.Layout;

/// <summary>Indic_Syllabic_Category of the Unicode Character Database (IndicSyllabicCategory.txt): the role of a character in a syllable.</summary>
internal enum SyllabicCategory : byte
{
    Other,
    Avagraha,
    Bindu,
    BrahmiJoiningNumber,
    CantillationMark,
    Consonant,
    ConsonantDead,
    ConsonantFinal,
    ConsonantHeadLetter,
    ConsonantInitialPostfixed,
    ConsonantKiller,
    ConsonantMedial,
    ConsonantPlaceholder,
    ConsonantPrecedingRepha,
    ConsonantPrefixed,
    ConsonantSubjoined,
    ConsonantSucceedingRepha,
    ConsonantWithStacker,
    GeminationMark,
    InvisibleStacker,
    Joiner,
    ModifyingLetter,
    NonJoiner,
    Nukta,
    Number,
    NumberJoiner,
    PureKiller,
    RegisterShifter,
    SyllableModifier,
    ToneLetter,
    ToneMark,
    Virama,
    Visarga,
    Vowel,
    VowelDependent,
    VowelIndependent,
    /// <summary>New in Unicode 16.0 (Batak pangolat and panongonan; No. 1181).</summary>
    ReorderingKiller,
}

/// <summary>Indic_Positional_Category of the Unicode Character Database (IndicPositionalCategory.txt): where a dependent sign stands at its base.</summary>
internal enum PositionalCategory : byte
{
    NA,
    Bottom,
    BottomAndLeft,
    BottomAndRight,
    Left,
    LeftAndRight,
    Overstruck,
    Right,
    Top,
    TopAndBottom,
    TopAndBottomAndLeft,
    TopAndBottomAndRight,
    TopAndLeft,
    TopAndLeftAndRight,
    TopAndRight,
    VisualOrderLeft,
}

/// <summary>Hangul_Syllable_Type of the Unicode Character Database (HangulSyllableType.txt).</summary>
internal enum HangulSyllableType : byte
{
    NotApplicable,
    LeadingJamo,
    VowelJamo,
    TrailingJamo,
    LvSyllable,
    LvtSyllable,
}

/// <summary>
/// The character properties the shaper of the syllabic scripts needs (docs/FEHLERGEDAECHTNIS.md, No. 1175): Indic_Syllabic_Category,
/// Indic_Positional_Category and Hangul_Syllable_Type, read from the files of the Unicode Character Database 16.0.0 that ship as resources
/// (Fonts/Data). Each property is a sorted list of ranges, searched by halves; a code point not listed has the default value (Other, NA, not
/// applicable), as the files say.
/// </summary>
internal static class SyllableData
{
    private sealed class Ranges(int[] starts, int[] ends, byte[] values)
    {
        public byte Of(int codePoint)
        {
            int lo = 0, hi = starts.Length - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                if (codePoint < starts[mid]) hi = mid - 1;
                else if (codePoint > ends[mid]) lo = mid + 1;
                else return values[mid];
            }
            return 0;
        }
    }

    private static readonly Lazy<Ranges> Syllabic = new(() => Load("Fonts/IndicSyllabicCategory.txt", typeof(SyllabicCategory)));

    private static readonly Lazy<Ranges> Positional = new(() => Load("Fonts/IndicPositionalCategory.txt", typeof(PositionalCategory)));

    private static readonly Lazy<Ranges> Hangul = new(() => Load("Fonts/HangulSyllableType.txt", typeof(HangulSyllableType),
        new Dictionary<string, string> { ["L"] = "LeadingJamo", ["V"] = "VowelJamo", ["T"] = "TrailingJamo", ["LV"] = "LvSyllable", ["LVT"] = "LvtSyllable" }));

    /// <summary>The Indic_Syllabic_Category of a code point.</summary>
    public static SyllabicCategory SyllabicOf(int codePoint) => (SyllabicCategory)Syllabic.Value.Of(codePoint);

    /// <summary>The Indic_Positional_Category of a code point.</summary>
    public static PositionalCategory PositionalOf(int codePoint) => (PositionalCategory)Positional.Value.Of(codePoint);

    /// <summary>The Hangul_Syllable_Type of a code point.</summary>
    public static HangulSyllableType HangulOf(int codePoint) => (HangulSyllableType)Hangul.Value.Of(codePoint);

    /// <summary>
    /// Reads a property file of the database: lines „0900..0902 ; Bindu # …“ or „0903 ; Visarga # …“; the value names of the file without
    /// underscores are the names of <paramref name="values"/> (or come from <paramref name="aliases"/>). A missing resource gives no ranges.
    /// </summary>
    private static Ranges Load(string resource, Type values, Dictionary<string, string>? aliases = null)
    {
        var list = new List<(int Start, int End, byte Value)>();
        using var stream = typeof(SyllableData).Assembly.GetManifestResourceStream(resource);
        if (stream is not null)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                var hash = line.IndexOf('#');
                var content = hash >= 0 ? line[..hash] : line;
                var semicolon = content.IndexOf(';');
                if (semicolon < 0) continue;
                var range = content[..semicolon].Trim();
                var name = content[(semicolon + 1)..].Trim();
                if (aliases is not null && aliases.TryGetValue(name, out var alias)) name = alias;
                if (!Enum.TryParse(values, name.Replace("_", ""), ignoreCase: true, out var parsed)) continue;
                var dots = range.IndexOf("..", StringComparison.Ordinal);
                if (!int.TryParse(dots < 0 ? range : range[..dots], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var start)
                    || !int.TryParse(dots < 0 ? range : range[(dots + 2)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var end)) continue;
                list.Add((start, end, Convert.ToByte(parsed, CultureInfo.InvariantCulture)));
            }
        }
        list.Sort((a, b) => a.Start.CompareTo(b.Start));
        return new Ranges([.. list.Select(r => r.Start)], [.. list.Select(r => r.End)], [.. list.Select(r => r.Value)]);
    }
}
