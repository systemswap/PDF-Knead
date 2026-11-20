using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfKnead.Core.Platform;

namespace PdfKnead.Core.Localization;

/// <summary>How the flag of a language is drawn.</summary>
public enum FlagKind
{
    /// <summary>Stripes of equal width in <see cref="Flag.Colors"/>.</summary>
    Stripes,
    /// <summary>Union Jack (built in for English).</summary>
    UnionJack,
    /// <summary>Image file (PNG) in the folder languages.</summary>
    Image,
    /// <summary>Not specified: a grey box with the language code.</summary>
    None,
}

/// <summary>Flag of a language, shown in the language selection.</summary>
/// <param name="Kind">How the flag is drawn.</param>
/// <param name="Vertical">Vertical stripes (France) instead of horizontal ones (Germany).</param>
/// <param name="Colors">Stripe colours as hexadecimal strings such as “#DD0000”, for <see cref="FlagKind.Stripes"/>.</param>
/// <param name="ImagePath">Full path of the image file, for <see cref="FlagKind.Image"/>.</param>
public sealed record Flag(FlagKind Kind, bool Vertical = false, IReadOnlyList<string>? Colors = null, string? ImagePath = null)
{
    /// <summary>No flag specified.</summary>
    public static readonly Flag None = new(FlagKind.None);
}

/// <summary>
/// A language: built in (German, English) or from a JSON file.
/// <see cref="Name"/> is written in the language itself (“Français”); together with the
/// flag, users can find their own language even while a foreign one is selected.
/// </summary>
/// <param name="Code">Language code in lower case, e.g. “fr”.</param>
/// <param name="Name">Name of the language in the language itself.</param>
/// <param name="Flag">Flag shown next to the name.</param>
/// <param name="Texts">Translations keyed by the German text; empty for German.</param>
/// <param name="File">Path of the JSON file; null for built-in languages.</param>
public sealed record Language(string Code, string Name, Flag Flag, IReadOnlyDictionary<string, string> Texts, string? File = null)
{
    /// <summary>The built-in German language; its texts are the keys, so it needs no table.</summary>
    public static readonly Language German = new("de", "Deutsch",
        new Flag(FlagKind.Stripes, Colors: ["#000000", "#DD0000", "#FFCE00"]),
        new Dictionary<string, string>());

    /// <summary>Gets the built-in English language.</summary>
    public static Language EnglishLanguage => English.Language;

    /// <summary>Gets whether this is German (code “de”).</summary>
    public bool IsGerman => Code == "de";

    /// <summary>Returns the name of the language.</summary>
    /// <returns><see cref="Name"/>.</returns>
    public override string ToString() => Name;
}

/// <summary>
/// Finds all languages: the built-in ones and the JSON files in the folder
/// <c>languages</c> next to the program.
/// </summary>
/// <remarks>
/// Structure of a language file (UTF-8), e.g. <c>languages/fr.json</c>:
/// <code>
/// {
///   "code": "fr",
///   "name": "Français",
///   "flag": { "stripes": "vertical", "colors": ["#002395", "#FFFFFF", "#ED2939"] },
///   "texts": { "Öffnen": "Ouvrir", … }
/// }
/// </code>
/// Instead of the stripes, an image file can be given: <c>"flag": "fr.png"</c>.
/// The key is always the German text; missing texts appear in English.
/// </remarks>
public static class Languages
{
    /// <summary>Name of the folder with the language files.</summary>
    public const string FolderName = "languages";

    /// <summary>Gets the folder with the language files next to the program.</summary>
    public static string Folder => Path.Combine(AppLocations.ProgramFolder, FolderName);

    /// <summary>Returns the built-in languages and those from valid files; faulty files are reported in <paramref name="problems"/>.</summary>
    /// <param name="problems">Receives one message per file that could not be loaded or that tries to replace German.</param>
    /// <param name="folder">Folder to search; null = <see cref="Folder"/>.</param>
    /// <returns>German, the built-in English unless a file replaces it, then the file languages in file name order.</returns>
    /// <remarks>
    /// Files whose name starts with an underscore (templates) are skipped. A file for English replaces the built-in English
    /// entry; German cannot be replaced.
    /// </remarks>
    public static List<Language> All(out List<string> problems, string? folder = null)
    {
        folder ??= Folder;
        problems = [];
        var list = new List<Language> { Language.German, English.Language };

        if (!Directory.Exists(folder)) return list;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(file).StartsWith('_')) continue;   // template or similar
            try
            {
                var lang = Load(file);
                // A file for a built-in language supplements or replaces its texts.
                list.RemoveAll(l => l.Code == lang.Code && l.File is null && !l.IsGerman);
                if (lang.IsGerman) problems.Add(Tr.T("{0}: Deutsch ist eingebaut und lässt sich nicht ersetzen.", Path.GetFileName(file)));
                else list.Add(lang);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return list;
    }

    /// <summary>Loads a language from a JSON file.</summary>
    /// <param name="file">Path of the language file.</param>
    /// <returns>The language; the code defaults to the file name, the name to the code. Empty texts are skipped.</returns>
    /// <exception cref="InvalidDataException">The file does not contain a JSON object.</exception>
    public static Language Load(string file)
    {
        var root = JsonNode.Parse(System.IO.File.ReadAllText(file),
                       documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                   as JsonObject ?? throw new InvalidDataException(Tr.T("kein JSON-Objekt"));

        var code = (string?)root["code"] ?? Path.GetFileNameWithoutExtension(file);
        var name = (string?)root["name"] ?? code;
        var texts = new Dictionary<string, string>();
        if (root["texts"] is JsonObject t)
            foreach (var (key, value) in t)
                if (value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0)
                    texts[key] = s;

        return new Language(code.Trim().ToLowerInvariant(), name, ReadFlag(root["flag"], Path.GetDirectoryName(file)!), texts, file);
    }

    private static Flag ReadFlag(JsonNode? node, string folder)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var image):
                var path = Path.Combine(folder, image);
                return System.IO.File.Exists(path) ? new Flag(FlagKind.Image, ImagePath: path) : Flag.None;
            case JsonObject o when o["colors"] is JsonArray colors:
                var list = colors.Select(c => (string?)c).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).ToList();
                var vertical = string.Equals((string?)o["stripes"], "vertical", StringComparison.OrdinalIgnoreCase);
                return list.Count > 0 ? new Flag(FlagKind.Stripes, vertical, list) : Flag.None;
            default:
                return Flag.None;
        }
    }

    /// <summary>
    /// Selects the language for the first start: that of the system, otherwise English
    /// (German only if the system is German).
    /// </summary>
    /// <param name="available">The available languages; must contain English.</param>
    /// <param name="culture">Culture to match by its two-letter code; null = the current UI culture.</param>
    /// <returns>The matching language, otherwise English.</returns>
    public static Language ForSystem(IReadOnlyList<Language> available, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        return available.FirstOrDefault(l => l.Code == culture.TwoLetterISOLanguageName)
               ?? available.First(l => l.Code == "en");
    }

    /// <summary>Returns the file name of the template; it starts with an underscore so that it is not loaded as a language.</summary>
    /// <param name="fromGerman">Template prefilled with German (otherwise English).</param>
    /// <returns>The file name without folder.</returns>
    public static string TemplateFileName(bool fromGerman) => fromGerman ? "_vorlage-deutsch.json" : "_vorlage-englisch.json";

    /// <summary>
    /// Writes a template for a new language: all texts, prefilled with German or
    /// English, depending on which language the translator prefers to translate from. The key is always
    /// the German text. Without <paramref name="file"/> it is written to the folder languages next to
    /// the program.
    /// </summary>
    /// <param name="fromGerman">Prefill with German (otherwise English).</param>
    /// <param name="file">Target path; null = <see cref="TemplateFileName"/> in <see cref="Folder"/>.</param>
    /// <returns>The path of the written file.</returns>
    public static string WriteTemplate(bool fromGerman, string? file = null)
    {
        file ??= Path.Combine(Folder, TemplateFileName(fromGerman));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var texts = new JsonObject();
        foreach (var (german, english) in English.Texts.OrderBy(e => e.Key, StringComparer.Ordinal))
            texts[german] = fromGerman ? german : english;

        var root = new JsonObject
        {
            ["code"] = "xx",
            ["name"] = fromGerman
                ? "Name der Sprache in der Sprache selbst, z. B. Français"
                : "Name of the language in the language itself, e.g. Français",
            ["flag"] = new JsonObject
            {
                ["stripes"] = "horizontal",
                ["colors"] = new JsonArray("#000000", "#DD0000", "#FFCE00"),
            },
            ["texts"] = texts,
        };

        System.IO.File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep umlauts readable
        }));
        return file;
    }
}
