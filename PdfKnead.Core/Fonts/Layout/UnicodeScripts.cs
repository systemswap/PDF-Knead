namespace PdfKnead.Core.Fonts.Layout;

/// <summary>
/// The script of a text for OpenType layout: the Script property of each character (Unicode Character Database, <see cref="Script"/>), runs of
/// one script (<see cref="Itemize"/>) and the OpenType script and language system tags that select the lookups of a font (OpenType „Script
/// tags“ and „Language system tags“, OpenType 2.4.1 and chapter 61). Before, the shaper knew only „arab“ (for a text with a joining letter of any
/// cursive script) and „latn“ for everything else (docs/FEHLERGEDAECHTNIS.md, No. 1162).
/// </summary>
internal static partial class UnicodeScripts
{
    /// <summary>ISO 15924 code of the characters shared by several scripts (punctuation, digits, symbols, spaces).</summary>
    internal const string Common = "Zyyy";

    /// <summary>ISO 15924 code of the characters that take the script of the character before them (combining marks, joiners, selectors).</summary>
    internal const string Inherited = "Zinh";

    /// <summary>ISO 15924 code of code points not assigned in the database.</summary>
    internal const string Unknown = "Zzzz";

    /// <summary>The Script property of a code point: its ISO 15924 code („Latn“, „Grek“, „Hani“ …), <see cref="Common"/>, <see cref="Inherited"/> or <see cref="Unknown"/>.</summary>
    internal static string Script(int codePoint)
    {
        int lo = 0, hi = Ranges.Length / 3 - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (codePoint < Ranges[mid * 3]) hi = mid - 1;
            else if (codePoint > Ranges[mid * 3 + 1]) lo = mid + 1;
            else return Names[Ranges[mid * 3 + 2]];
        }
        return Unknown;
    }

    /// <summary>
    /// The property Script_Extensions of a code point (generated from ScriptExtensions.txt 16.0.0, <see cref="ExtensionRanges"/>): the scripts
    /// a character is used with besides its own; null if the database lists none (the value is then its Script property).
    /// </summary>
    internal static string[]? Extensions(int codePoint)
    {
        int lo = 0, hi = ExtensionRanges.Length / 3 - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (codePoint < ExtensionRanges[mid * 3]) hi = mid - 1;
            else if (codePoint > ExtensionRanges[mid * 3 + 1]) lo = mid + 1;
            else return ExtensionSets[ExtensionRanges[mid * 3 + 2]];
        }
        return null;
    }

    /// <summary>Whether a script code names no script of its own (shared, inherited or unassigned characters).</summary>
    internal static bool IsNeutral(string script) => script is Common or Inherited or Unknown;

    /// <summary>
    /// Splits a text into runs of one script: a character of a script of its own starts or continues the run of that script; a neutral one
    /// (<see cref="IsNeutral"/>: punctuation, digits, spaces, combining marks) belongs to the run before it, and neutral characters at the
    /// start to the first script after them. A text of neutral characters only is one run of <see cref="Common"/>. Hiragana and Katakana make
    /// one run (both are written under the OpenType tag „kana“). A character of another script whose Script_Extensions name the script of
    /// the run continues the run (the Grantha nukta U+1133C in Tamil text, The Unicode Standard 12.6.3) — before, only the Script property
    /// counted and such a character started a run of its own (docs/FEHLERGEDAECHTNIS.md, No. 1183).
    /// </summary>
    /// <param name="codePoints">The code points in text order.</param>
    /// <returns>Start, length (in code points) and ISO 15924 code of each run, in text order.</returns>
    internal static List<(int Start, int Length, string Script)> Itemize(IReadOnlyList<int> codePoints)
    {
        var runs = new List<(int Start, int Length, string Script)>();
        string? current = null;
        var start = 0;
        for (var i = 0; i < codePoints.Count; i++)
        {
            var script = Script(codePoints[i]);
            if (IsNeutral(script)) continue;
            if (script == "Hira") script = "Kana";
            if (current is null) current = script;
            else if (script != current && !ExtendsTo(codePoints[i], current))
            {
                runs.Add((start, i - start, current));
                start = i;
                current = script;
            }
        }
        if (codePoints.Count > start) runs.Add((start, codePoints.Count - start, current ?? Common));
        return runs;
    }

    /// <summary>Whether the Script_Extensions of a code point name the script of a run (Hiragana and Katakana count as one).</summary>
    private static bool ExtendsTo(int codePoint, string run) =>
        Extensions(codePoint) is { } extensions && (Array.IndexOf(extensions, run) >= 0 || run == "Kana" && Array.IndexOf(extensions, "Hira") >= 0);

    /// <summary>
    /// The OpenType script tags for a script, in the order to try (OpenType chapter 61): for the Indic scripts of the second shaping model
    /// first the „…2“ tag, then the old one; otherwise the lower-case ISO code, with the tags the registry spells otherwise (Hiragana and Katakana
    /// „kana“, Lao „lao “, Yi „yi  “, N'Ko „nko “, Vai „vai “). A neutral script has no tag of its own — the font's DFLT applies (OpenType 2.4.1).
    /// </summary>
    internal static string[] OpenTypeTags(string script) => script switch
    {
        Common or Inherited or Unknown => [],
        "Deva" => ["dev2", "deva"],
        "Beng" => ["bng2", "beng"],
        "Guru" => ["gur2", "guru"],
        "Gujr" => ["gjr2", "gujr"],
        "Orya" => ["ory2", "orya"],
        "Taml" => ["tml2", "taml"],
        "Telu" => ["tel2", "telu"],
        "Knda" => ["knd2", "knda"],
        "Mlym" => ["mlm2", "mlym"],
        "Mymr" => ["mym2", "mymr"],
        "Hira" or "Kana" or "Hrkt" => ["kana"],
        "Laoo" => ["lao "],
        "Yiii" => ["yi  "],
        "Nkoo" => ["nko "],
        "Vaii" => ["vai "],
        _ => [script.ToLowerInvariant()],
    };

    /// <summary>
    /// The OpenType language system tags for a language (a language tag as in /Lang, BCP 47: „de“, „tr-TR“, „zh-Hant“), in the order to try:
    /// from the registry of language system tags by the primary subtag (<see cref="LanguageEntries"/>; a code the registry does not list has none),
    /// for Chinese by script or region (Traditional ZHT, Hong Kong ZHH, Macao ZHTM then ZHH, else Simplified ZHS) and Moldova's Romanian MOL before
    /// ROM, and for Syriac (syr, syc) the script variants by the script subtag the registry names as their equivalent (Syre: SYRE, Syrj: SYRJ,
    /// Syrn: SYRN, each before SYR; before, those three tags were never chosen, docs/FEHLERGEDAECHTNIS.md, No. 1218). Empty for none, an empty,
    /// invalid or private-use tag.
    /// </summary>
    internal static string[] OpenTypeLanguages(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Length > 64) return [];
        var parts = language.Trim().Replace('_', '-').ToLowerInvariant().Split('-');
        var primary = parts[0];
        if (primary.Length is < 2 or > 3 || !primary.All(char.IsAsciiLetterLower)) return [];
        bool Has(string subtag) => Array.IndexOf(parts, subtag, 1) > 0;
        if (primary == "zh")
            return Has("mo") ? ["ZHTM", "ZHH "] : Has("hk") ? ["ZHH "] : Has("hant") || Has("tw") ? ["ZHT "] : ["ZHS "];
        if (primary == "ro" && Has("md")) return ["MOL ", "ROM "];
        if (primary is "syr" or "syc")
        {
            if (Has("syre")) return ["SYRE", "SYR "];
            if (Has("syrj")) return ["SYRJ", "SYR "];
            if (Has("syrn")) return ["SYRN", "SYR "];
        }
        return LanguageTable.Value.TryGetValue(primary, out var tags) ? tags : [];
    }

    private static readonly Lazy<Dictionary<string, string[]>> LanguageTable = new(() =>
    {
        var table = new Dictionary<string, string[]>(LanguageEntries.Length, StringComparer.Ordinal);
        foreach (var entry in LanguageEntries)
        {
            var eq = entry.IndexOf('=');
            table[entry[..eq]] = entry[(eq + 1)..].Split(',');
        }
        return table;
    });
}
