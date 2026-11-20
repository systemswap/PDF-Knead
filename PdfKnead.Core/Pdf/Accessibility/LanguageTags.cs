using System.Text.RegularExpressions;

namespace PdfKnead.Core.Pdf.Accessibility;

/// <summary>
/// Language tags as PDF names them (ISO 32000-2, 14.9.2: BCP 47) — checked for their form (RFC 5646, 2.1) and for a language subtag that can be
/// registered: two letters from ISO 639-1, three letters (ISO 639-2/3, not checked further offline), private use; four letters are reserved
/// and five to eight letters are not given to any language. A name of a language instead of a tag („English“, „Deutsch“) is a frequent
/// mistake; the tag meant is proposed (docs/ABLAUFPLAN.md, Phase 6, finding of the second reference checker).
/// </summary>
public static partial class LanguageTags
{
    /// <summary>The form of a language tag (RFC 5646, 2.1, without the irregular grandfathered tags, which start with „i-“ or are accepted apart).</summary>
    [GeneratedRegex(@"^(?:(?<language>[a-z]{2,3}(?:-[a-z]{3}){0,3}|[a-z]{4}|[a-z]{5,8})(?:-[a-z]{4})?(?:-(?:[a-z]{2}|\d{3}))?(?:-(?:[a-z\d]{5,8}|\d[a-z\d]{3}))*" +
                    @"(?:-[a-wyz\d](?:-[a-z\d]{2,8})+)*(?:-x(?:-[a-z\d]{1,8})+)?|x(?:-[a-z\d]{1,8})+|i-[a-z]{2,8})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Form();

    /// <summary>The two-letter codes of ISO 639-1, with the deprecated ones the registry still lists (in, iw, ji, jw, mo, sh).</summary>
    private static readonly HashSet<string> TwoLetters = new(("aa ab ae af ak am an ar as av ay az ba be bg bh bi bm bn bo br bs ca ce ch co cr cs cu cv cy da de " +
        "dv dz ee el en eo es et eu fa ff fi fj fo fr fy ga gd gl gn gu gv ha he hi ho hr ht hu hy hz ia id ie ig ii ik io is it iu ja jv ka kg ki kj kk kl km " +
        "kn ko kr ks ku kv kw ky la lb lg li ln lo lt lu lv mg mh mi mk ml mn mr ms mt my na nb nd ne ng nl nn no nr nv ny oc oj om or os pa pi pl ps pt qu " +
        "rm rn ro ru rw sa sc sd se sg si sk sl sm sn so sq sr ss st su sv sw ta te tg th ti tk tl tn to tr ts tt tw ty ug uk ur uz ve vi vo wa wo xh yi yo " +
        "za zh zu in iw ji jw mo sh").Split(' '), StringComparer.OrdinalIgnoreCase);

    /// <summary>Names of languages written instead of a tag, with the tag meant (English, German and the language's own name).</summary>
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "en", ["englisch"] = "en", ["german"] = "de", ["deutsch"] = "de", ["french"] = "fr", ["französisch"] = "fr", ["français"] = "fr",
        ["francais"] = "fr", ["spanish"] = "es", ["spanisch"] = "es", ["español"] = "es", ["espanol"] = "es", ["italian"] = "it", ["italienisch"] = "it",
        ["italiano"] = "it", ["dutch"] = "nl", ["niederländisch"] = "nl", ["nederlands"] = "nl", ["portuguese"] = "pt", ["portugiesisch"] = "pt",
        ["português"] = "pt", ["portugues"] = "pt", ["polish"] = "pl", ["polnisch"] = "pl", ["polski"] = "pl", ["danish"] = "da", ["dänisch"] = "da",
        ["dansk"] = "da", ["swedish"] = "sv", ["schwedisch"] = "sv", ["svenska"] = "sv", ["norwegian"] = "no", ["norwegisch"] = "no", ["norsk"] = "no",
        ["finnish"] = "fi", ["finnisch"] = "fi", ["suomi"] = "fi", ["russian"] = "ru", ["russisch"] = "ru", ["czech"] = "cs", ["tschechisch"] = "cs",
        ["čeština"] = "cs", ["hungarian"] = "hu", ["ungarisch"] = "hu", ["magyar"] = "hu", ["greek"] = "el", ["griechisch"] = "el", ["turkish"] = "tr",
        ["türkisch"] = "tr", ["türkçe"] = "tr", ["arabic"] = "ar", ["arabisch"] = "ar", ["chinese"] = "zh", ["chinesisch"] = "zh", ["japanese"] = "ja",
        ["japanisch"] = "ja", ["korean"] = "ko", ["koreanisch"] = "ko", ["ukrainian"] = "uk", ["ukrainisch"] = "uk", ["romanian"] = "ro",
        ["rumänisch"] = "ro", ["croatian"] = "hr", ["kroatisch"] = "hr", ["slovak"] = "sk", ["slowakisch"] = "sk", ["slovenian"] = "sl",
        ["slowenisch"] = "sl", ["bulgarian"] = "bg", ["bulgarisch"] = "bg", ["hebrew"] = "he", ["hebräisch"] = "he", ["latin"] = "la", ["latein"] = "la",
    };

    /// <summary>Whether a tag is valid: well-formed and with a language subtag that can be registered. The empty tag (language unknown) is valid.</summary>
    /// <param name="tag">The tag as written.</param>
    /// <returns>True if valid.</returns>
    public static bool Valid(string tag)
    {
        if (tag.Length == 0) return true;
        if (Form().Match(tag) is not { Success: true } match) return false;
        if (!match.Groups["language"].Success) return true;   // private use or grandfathered
        var language = match.Groups["language"].Value.Split('-')[0];
        return language.Length switch
        {
            2 => TwoLetters.Contains(language),
            3 => true,
            _ => false,   // four letters reserved, five to eight letters given to no language
        };
    }

    /// <summary>
    /// The tag meant by an invalid one: the name of a language („English“ → „en“), an underscore for the hyphen („de_DE“ → „de-DE“); null if
    /// none is clear.
    /// </summary>
    /// <param name="tag">The tag as written.</param>
    /// <returns>The proposal, or null.</returns>
    public static string? Proposal(string tag)
    {
        var trimmed = tag.Trim();
        if (Names.TryGetValue(trimmed, out var named)) return named;
        var hyphens = trimmed.Replace('_', '-');
        if (hyphens != tag && Valid(hyphens)) return hyphens;
        return null;
    }
}
