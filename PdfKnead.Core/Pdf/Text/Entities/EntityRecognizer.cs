using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfKnead.Core.Pdf.Text.Entities;

/// <summary>
/// Finds persons, companies and the parts of addresses in a text — without AI, with fixed rules and open lists (KONZEPT.md, section 5.23;
/// decided on 13.11.2026: the program shall still run in 25 years without depending on a model). The rules are those of the
/// measured prototype (<c>tools/ner-messung/regeln.py</c>) and keep the building rules of the patent check (docs/PATENTE.md, B1–B8):
/// words carry no type — every rule asks the lists it needs (B7); the common words are never taken out of a list, they are weighed where a
/// word stands (B5); what is found as a whole (a company, a street, a place) is not looked at again word by word (B2); patterns are exact
/// regular expressions over characters, an address is put together in code from neighbouring finds, not by a parser over word types (B7, B8).
/// </summary>
/// <remarks>
/// Hits: P person (form of address, title or field before capitalised words; a given name followed by a family name of the lists;
/// „Surname, Given“; never after an article), O company (capitalised words before a legal form, a legal form before a quoted name, an
/// organisation keyword with a proper name beside it), the street and its house number, the postal code (only next to a place or after a
/// street), the place (after the postal code, after a preposition, after a street and a comma), the country (after a comma or at the start of
/// a line), the post office box. A name found with certainty is found again elsewhere in the text. The text is page text — foreign input:
/// every pattern has a time limit (<see cref="Timeout"/>); a rule that runs out of time is skipped for this text.
/// </remarks>
public sealed partial class EntityRecognizer
{
    /// <summary>Time limit of every pattern match (the page text is foreign input).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private const string Upper = "[A-ZÄÖÜÀ-ÞĀ-ŽА-ЯЁ]";
    private const string Word = "[A-ZÄÖÜÀ-ÞĀ-ŽА-ЯЁ][\\w'’.-]*";
    /// <summary>A house number: digits, a letter after them only as a word of its own (7a, 7 a — not the B of „14 Bradley“), a range or a fraction.</summary>
    private const string Number = "\\d{1,5}(?:\\s?[a-zA-Z](?!\\w))?(?:\\s?[-–/]\\s?\\d{1,5}(?:\\s?[a-zA-Z](?!\\w))?)?";

    internal static readonly string[] Salutations =
    [
        "Herr", "Herrn", "Frau", "Fräulein", "Hr.", "Fr.", "Mr.", "Mr", "Mrs.", "Mrs", "Ms.", "Ms", "Mx.", "Mx", "Miss", "Sir", "Dame",
        "Dr.", "Prof.", "Dipl.-Ing.", "Dipl.-Kfm.", "Dipl.-Kff.", "Mag.", "Ing.", "Dr. h. c.", "Dr. h.c.", "M.", "Mme", "Mme.", "Mlle",
        "Monsieur", "Madame", "Sig.", "Sig.ra", "Signor", "signor", "Signora", "signora", "Dott.", "Dott.ssa", "Señor", "señor", "Señora",
        "señora", "Sr.", "Sra.", "Senhor", "senhor", "Senhora", "Pan", "Pani", "De heer", "de heer", "Mevrouw", "Dhr.", "Mevr.",
        "Господин", "господин", "Госпожа", "г-н", "г-жа", "Bay", "Bayan",
    ];

    private static readonly string[] Fields =
    [
        "Name:", "Vorname:", "Nachname:", "Ansprechpartner:", "Ansprechpartnerin:", "Kontakt:", "Kunde:", "Kundin:", "Unterschrift:",
        "Unterzeichner:", "Sachbearbeiter:", "Sachbearbeiterin:", "Mit freundlichen Grüßen", "Freundliche Grüße", "Viele Grüße",
        "Best regards", "Kind regards", "Regards,", "Sincerely", "Yours sincerely", "Contact:", "Attn:", "Attention:", "Patient:",
        "Employee:", "Reviewer:", "Signed by", "Prepared by", "Cordialement", "Distinti saluti", "Atentamente", "Ship to:", "Bill to:",
        "In Vertretung für", "i. A.", "i. V.", "gez.", "Signature:",
    ];

    private static readonly string[] Particles = ["von", "van", "der", "den", "de", "del", "della", "di", "da", "du", "le", "la", "bin", "ibn", "ter", "zu", "y", "dos", "das"];

    private static readonly HashSet<string> ParticleSet = new(Particles, StringComparer.Ordinal);

    private static readonly HashSet<string> Articles = new(StringComparer.Ordinal)
    {
        "der", "die", "das", "dem", "den", "des", "ein", "eine", "einen", "the", "a", "an", "le", "la", "les", "il", "lo", "el", "los",
        "las", "un", "una", "het", "een",
    };

    /// <summary>Capitalised words that begin sentences or headings and never start a name (to keep „Max Die …“ or „Kunde Nummer“ out).</summary>
    private static readonly HashSet<string> NotNames = new(StringComparer.Ordinal)
    {
        "Die", "Der", "Das", "Ein", "Eine", "Und", "Oder", "Mit", "Von", "Für", "Bei", "Am", "Im", "In", "An", "Auf", "Aus", "Nach",
        "The", "And", "Of", "For", "With", "To", "At", "On", "Dear", "Telefon", "Phone", "Tel", "Fax", "E-Mail", "Email", "Kundennummer",
        "Rechnung", "Invoice", "Head", "Sales", "Geschäftsführerin", "Geschäftsführer", "Prokurist", "Prokuristin",
    };

    private const string StreetSuffix = "(?:straße|strasse|str\\.|weg|gasse|platz|allee|ring|damm|ufer|chaussee|steig|pfad|markt|graben|zeile|kai|straat|laan|plein|" +
                                        "gracht|kade|singel|gatan|vägen|gata|väg|gate|veien|vej|gade|tie|katu)";

    private const string StreetPrefix = "(?:Rue|rue|Avenue|avenue|Boulevard|Bd|Allée|Place|Chemin|Impasse|Via|Viale|Piazza|Corso|Largo|Calle|Avenida|Av\\.|Paseo|" +
                                        "Plaza|Carrer|Rua|Travessa|ul\\.|al\\.|os\\.|Am|An der|An den|Im|In der|Auf der|Zum|Zur)";

    /// <summary>The street suffixes of the US postal service (Publication 28, appendix C1, public domain) and British ones.</summary>
    private static readonly string[] UsSuffixes = (
        "Alley Annex Arcade Avenue Ave Bayou Beach Bend Bluff Bluffs Bottom Boulevard Blvd Branch Bridge Brook Brooks Burg Burgs Bypass " +
        "Camp Canyon Cape Causeway Center Centers Circle Circles Cliff Cliffs Club Common Commons Corner Corners Course Court Courts Cove " +
        "Coves Creek Crescent Crest Crossing Crossroad Curve Dale Dam Divide Drive Drives Estate Estates Expressway Extension Extensions " +
        "Fall Falls Ferry Field Fields Flat Flats Ford Fords Forest Forge Forges Fork Forks Fort Freeway Garden Gardens Gateway Glen Glens " +
        "Green Greens Grove Groves Harbor Harbors Haven Heights Highway Hill Hills Hollow Inlet Island Islands Isle Junction Junctions Key " +
        "Keys Knoll Knolls Lake Lakes Land Landing Lane Light Lights Loaf Lock Locks Lodge Loop Mall Manor Manors Meadow Meadows Mews Mill " +
        "Mills Mission Motorway Mount Mountain Mountains Neck Orchard Oval Overpass Park Parks Parkway Parkways Pass Passage Path Pike " +
        "Pine Pines Place Plain Plains Plaza Point Points Port Ports Prairie Radial Ramp Ranch Rapid Rapids Rest Ridge Ridges River Road " +
        "Roads Route Row Rue Run Shoal Shoals Shore Shores Skyway Spring Springs Spur Spurs Square Squares Station Stravenue Stream " +
        "Street Streets Summit Terrace Throughway Trace Track Trafficway Trail Trailer Tunnel Turnpike Underpass Union Unions Valley " +
        "Valleys Via Viaduct View Views Village Villages Ville Vista Walk Walks Wall Way Ways Well Wells Close Rise Wynd Entrance Amble " +
        "Laneway Roadside Subway St Rd Ln Dr Ct Pl Pkwy Hwy Ter Cir Sq").Split(' ');

    /// <summary>The abbreviations among <see cref="UsSuffixes"/> (only they take a dot).</summary>
    private static readonly HashSet<string> UsAbbreviations = new(["Ave", "Blvd", "St", "Rd", "Ln", "Dr", "Ct", "Pl", "Pkwy", "Hwy", "Ter", "Cir", "Sq"], StringComparer.Ordinal);

    private static readonly string[] Postcodes =
    [
        "\\d{5}(?:-\\d{4})?", "\\d{4}\\s?[A-Z]{2}", "\\d{2}-\\d{3}", "\\d{3}\\s\\d{2}", "\\d{4}-\\d{3}", "\\d{4}",
        "[A-Z]{1,2}\\d[A-Z\\d]?\\s?\\d[A-Z]{2}", "[A-Z]\\d[A-Z]\\s?\\d[A-Z]\\d",
    ];

    private const string PostBox = "(?:Postfach|P\\.\\s?O\\.\\s?Box|PO Box|BP|Casella postale|Apartado(?: de correos)?|Skrytka pocztowa|Postbus)\\s+\\d[\\d ]*\\d";
    private const string Prepositions = "(?:in|aus|nach|bei|from|to|à|a|en|w|we|v|ve|i|im|zu)";

    /// <summary>Legal forms written before a quoted name (ООО «…») and after a name in Chinese, Japanese, Korean.</summary>
    private static readonly string[] PrefixForms = ["ООО", "ОАО", "ЗАО", "ПАО", "АО", "ИП", "株式会社", "有限会社", "合同会社", "주식회사", "유한회사"];

    private static readonly string[] CjkSuffixForms = ["股份有限公司", "有限责任公司", "有限公司", "株式会社", "有限会社", "合同会社", "주식회사", "유한회사"];

    /// <summary>Chinese, Japanese and Korean family names the lists lack in their short forms (the 20 most common Chinese ones, a few others).</summary>
    private static readonly HashSet<string> CjkFamily = new(StringComparer.Ordinal)
    {
        "王", "李", "张", "刘", "陈", "杨", "黄", "赵", "吴", "周", "徐", "孙", "马", "朱", "胡", "郭", "何", "高", "林", "罗",
        "山田", "田中", "佐藤", "鈴木", "高橋", "渡辺", "伊藤", "中村", "小林", "홍", "김", "이", "박",
    };

    private static readonly string[] OrgKeywords =
    [
        "Sparkasse", "Volksbank", "Raiffeisenbank", "Bank", "Versicherung", "Stiftung", "Verein", "Verband", "Universität",
        "Hochschule", "Klinikum", "Krankenhaus", "Ministerium", "Landratsamt", "Finanzamt", "Amtsgericht", "Landgericht",
        "Oberlandesgericht", "Arbeitsgericht", "Stadtwerke", "Stadtverwaltung", "Gemeinde", "Kreisverwaltung", "Jobcenter",
        "University", "College", "Hospital", "Foundation", "Association", "Agency", "Ministry", "Council", "Court",
        "Université", "Banque", "Fondation", "Università", "Banca", "Fondazione", "Universidad", "Banco", "Fundación",
    ];

    /// <summary>Last words that make a „name“ a street or place (Main Street, Charles Village) or no name (Due Date).</summary>
    private static readonly HashSet<string> StreetWords = new(UsSuffixes.Concat(["Village", "City", "Town", "Straße", "Weg", "Gasse", "Platz", "Allee", "Date", "Policy", "Number"]), StringComparer.Ordinal);

    /// <summary>First words that make a „name“ a place or no name (North Sean, Lake Charles, Due Date).</summary>
    private static readonly HashSet<string> Directions = new(StringComparer.Ordinal)
    {
        "North", "South", "East", "West", "Lake", "Port", "New", "Old", "Mount", "Fort", "Saint", "Due", "Return", "Total",
    };

    private static readonly string[] Connectors = ["&", "und", "and", "et", "e", "y", "i"];

    private readonly Lexicon _lexicon;
    private readonly EntityOptions _options;

    private readonly Regex _salutation, _field, _titles, _nameWords, _nameEnding, _inverted, _personPair, _singleName, _thoroughRun, _cjkPerson, _koreanPerson;
    private readonly Regex _form, _prefixForm, _cjkCompany, _orgKeyword, _orgAfter, _orgBefore;
    private readonly Regex _streetSuffix, _streetPrefix, _streetNumberFirst, _streetAdjective, _postBox, _postcode, _cityAfter, _cityBefore, _placeWords;
    private readonly Regex? _streetSuffixLp, _streetTypeLp, _streetNumberTypeLp, _postBoxLp;
    private readonly Regex _emailName = new("([A-Za-zÀ-ÿ]+)[._-]([A-Za-zÀ-ÿ]+)@", RegexOptions.CultureInvariant, Timeout);
    private readonly Regex _commaOrLineEnd = new("[,\\n]\\s*$", RegexOptions.CultureInvariant, Timeout);
    private readonly Regex _afterComma = new(",\\s*$", RegexOptions.CultureInvariant, Timeout);
    private readonly Regex _preposition = new($"(?<!\\w){Prepositions}\\s+$", RegexOptions.CultureInvariant, Timeout);
    private readonly Regex _initialAtEnd = new("(?<!\\w)\\w\\.$", RegexOptions.CultureInvariant, Timeout);

    /// <param name="lexicon">The lists.</param>
    /// <param name="options">What to look for besides persons, companies and addresses.</param>
    public EntityRecognizer(Lexicon lexicon, EntityOptions? options = null)
    {
        _lexicon = lexicon;
        _options = options ?? new EntityOptions();
        static string Alternatives(IEnumerable<string> words) =>
            string.Join("|", words.Distinct(StringComparer.Ordinal).OrderByDescending(w => w.Length).ThenBy(w => w, StringComparer.Ordinal).Select(Regex.Escape));
        Regex Make(string pattern, RegexOptions extra = RegexOptions.None) => new(pattern, RegexOptions.CultureInvariant | extra, Timeout);

        _salutation = Make($"(?<!\\w)(?:(?:{Alternatives(Salutations)})\\s+)+");
        _field = Make($"(?<!\\w)(?:{Alternatives(Fields)})[ \\t,]*");
        var titles = lexicon.Entries(LexiconList.Titles);
        _titles = Make(titles.Count > 0 ? $"(?<!\\w)(?:(?:{Alternatives(titles)})\\.?\\s+)+" : "(?!)");
        _nameWords = Make($"\\G\\s*((?:{Word}|{Upper}\\.)(?:[ \\t]+(?:(?:{string.Join("|", Particles)})[ \\t]+)?{Word}){{0,3}})");
        var endings = lexicon.Entries(LexiconList.NameEndings);
        _nameEnding = Make(endings.Count > 0 ? $"\\G(?:,?[ \\t]+(?:{Alternatives(endings)})(?![\\w]))+" : "(?!)");
        const string particles = "(?:van den|van der|von der|von dem|de la|de los|de las|del|della|van|von|de|di|da|du|le|la|bin|ibn|ter|zu|dos|das)";
        // „Surname, Given“ as in lists and forms (CLDR's sorting order)
        _inverted = Make($"(?<![\\w-])((?:{particles}[ \\t]+)?{Word}(?:[ \\t]+{Word})?),[ \\t]*({Word}(?:[ \\t]+{Word}){{0,3}})((?:[ \\t]+{particles})?)");
        _personPair = Make($"(?<![\\w-])({Word})((?:[ \\t]+{Word}){{1,3}})");
        _singleName = Make($"(?<![\\w-])({Upper}[\\w'’]+(?:-{Upper}[\\w'’]+)?)(?![\\w-])");
        _thoroughRun = Make($"(?<![\\w-])({Upper}[\\w'’]+(?:[-‐]{Upper}[\\w'’]+)?(?:[ \\t]+{Upper}[\\w'’]+(?:[-‐]{Upper}[\\w'’]+)?){{0,2}})(?![\\w-])");
        _cjkPerson = Make("(?<![一-鿿])([一-鿿]{1,2})([一-鿿]{1,2})(?=[^一-鿿]|在|签|は|が|の|$)");
        _koreanPerson = Make("([가-힣])([가-힣]{2})(?=\\s|씨|님|$)");

        var forms = lexicon.Entries(LexiconList.LegalForms);
        _form = Make(forms.Count > 0 ? $"(?<![\\w.])(?:{Alternatives(forms)})(?![\\w])" : "(?!)");
        _prefixForm = Make($"(?:{string.Join("|", PrefixForms.Select(Regex.Escape))})\\s*[«\"„“]([^»\"“”]{{1,60}})[»\"“”]");
        _cjkCompany = Make($"([぀-ヿ一-鿿가-힯]{{1,12}}?)\\s?(?:{string.Join("|", CjkSuffixForms.Select(Regex.Escape))})");
        _orgKeyword = Make($"(?<![\\w-])({string.Join("|", OrgKeywords)})(?![\\w-])");
        _orgAfter = Make($"\\G(?:[ \\t]+(?:of|für|de|di|der|des|du)?[ \\t]*{Upper}[\\w'’.&-]*){{1,4}}");
        _orgBefore = Make($"((?:{Upper}[\\w'’.&-]*[ \\t]+){{1,3}})$");

        // the street words of the US postal service; a dot only after an abbreviation („Rd.“ — the dot of „Road.“ ends the sentence)
        static string Choice(IEnumerable<string> words) => string.Join("|", words.SelectMany(x => new[] { x, x.ToLowerInvariant() }).Distinct(StringComparer.Ordinal)
            .OrderByDescending(w => w.Length).ThenBy(w => w, StringComparer.Ordinal));
        var usType = $"(?:(?:{Choice(UsSuffixes.Where(w => !UsAbbreviations.Contains(w)))})|(?:{Choice(UsAbbreviations)})\\.?)";
        _streetSuffix = Make($"(?<![\\w-])((?:{Upper}[\\w'’.-]*?|{Upper}[\\w.-]*\\s){StreetSuffix}|{Upper}[\\w'’-]+-{Upper}[\\w'’-]*?{StreetSuffix})\\s+({Number})(?!\\d)");
        _streetPrefix = Make($"(?<!\\w)({StreetPrefix}\\s+(?:de\\s+l[’']|de\\s+la\\s+|du\\s+|des\\s+)?{Upper}[\\w'’-]*(?:\\s+{Upper}[\\w'’-]*){{0,3}})\\s+({Number})(?!\\d)");
        _streetNumberFirst = Make($"(?<![\\w/])(\\d{{1,5}}[a-zA-Z]?(?:/\\d{{1,5}})?),?\\s+((?:rue|avenue|boulevard|place|chemin|allée|impasse)\\s+[^,\\n\\d]{{2,40}}|{Upper}[\\w'’-]*(?:\\s+{Upper}[\\w'’-]*){{0,3}}\\s+{usType}(?!\\w)(?:,?\\s+(?:Apt\\.|Suite|Unit|Ste\\.)\\s*\\d+)?)");
        _streetAdjective = Make($"(?<=Adresa:\\s)({Upper}[\\w'’-]+)\\s+(\\d{{1,5}}/\\d{{1,5}})");
        var suffixes = lexicon.Entries(LexiconList.StreetSuffixes);
        if (suffixes.Count > 0)
            _streetSuffixLp = Make($"(?<![\\w-])({Upper}[\\w'’-]*?(?i:{Alternatives(suffixes)})\\.?)\\s+({Number})(?!\\d)");
        // street words before a name: at least two letters (a single letter with a dot is mostly an initial: „Anna T. Jackson“)
        var typesBefore = lexicon.Entries(LexiconList.StreetTypesBefore).Where(w => w.Count(char.IsLetter) >= 2).ToList();
        if (typesBefore.Count > 0)
        {
            var alt = Alternatives(typesBefore);
            const string link = "(?:(?:de|del|della|di|da|des|du|de la|de l[’']|von|van)\\s+)?";
            _streetTypeLp = Make($"(?<!\\w)((?i:{alt})\\.?\\s+{link}{Upper}[\\w'’-]*(?:\\s+{Upper}[\\w'’-]*){{0,3}})[ \\t,]+({Number})(?!\\d)");
            _streetNumberTypeLp = Make($"(?<![\\w/])({Number}),?\\s+((?i:{alt})\\.?\\s+{link}{Upper}[\\w'’-]*(?:\\s+{Upper}[\\w'’-]*){{0,3}})");
        }
        var offices = lexicon.Entries(LexiconList.PostOffices);
        if (offices.Count > 0) _postBoxLp = Make($"(?<!\\w)(?:{Alternatives(offices)})\\.?\\s*:?\\s*\\d[\\d ]*\\d?", RegexOptions.IgnoreCase);
        _postBox = Make(PostBox);
        // postal code formats of all countries (Google's address data) beside the general ones; one .NET cannot read is left out
        var formats = Postcodes.Concat(lexicon.Entries(LexiconList.PostcodeFormats)).Where(Valid).Distinct(StringComparer.Ordinal)
            .OrderByDescending(p => p.Length).ThenBy(p => p, StringComparer.Ordinal);
        _postcode = Make("(?<![\\w-])(?:[A-Z]{1,2}-)?(" + string.Join("|", formats.Select(p => $"(?:{p})")) + ")(?![\\w-])");
        _cityAfter = Make($"\\G[ \\t]+({Upper}[\\w'’.-]*(?:[ \\t]+(?:an der|am|an|in der|im|bei|ob der|sur|sous|upon|on)?[ \\t]*{Upper}[\\w'’.-]*){{0,3}})");
        _cityBefore = Make($"({Upper}[\\w'’.-]+),?[ \\t]+$");
        _placeWords = Make($"(?<!\\w)({Upper}[\\w'’.-]+(?:[ \\t]{Upper}[\\w'’.-]+)?)");
    }

    private static bool Valid(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, Timeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static readonly Lazy<EntityRecognizer?> DefaultRecognizer = new(() => Lexicon.Default is { } l ? new EntityRecognizer(l) : null);
    private static readonly Lazy<EntityRecognizer?> PlacesRecognizer = new(() => Lexicon.Default is { } l ? new EntityRecognizer(l, new EntityOptions(PlacesEverywhere: true)) : null);
    private static readonly Lazy<EntityRecognizer?> ThoroughRecognizer = new(() => Lexicon.Default is { } l ? new EntityRecognizer(l, new EntityOptions(Thorough: true)) : null);
    private static readonly Lazy<EntityRecognizer?> BothRecognizer = new(() => Lexicon.Default is { } l ? new EntityRecognizer(l, new EntityOptions(true, true)) : null);

    /// <summary>A recognizer with the built-in lexicon (made once per option set); null if the program has no lexicon.</summary>
    public static EntityRecognizer? For(EntityOptions options) => (options.PlacesEverywhere, options.Thorough) switch
    {
        (false, false) => DefaultRecognizer.Value,
        (true, false) => PlacesRecognizer.Value,
        (false, true) => ThoroughRecognizer.Value,
        _ => BothRecognizer.Value,
    };

    // ---------------------------------------------------------------- helpers

    private bool Given(string word) => _lexicon.Contains(LexiconList.GivenNames, word);
    private bool Family(string word) => _lexicon.Contains(LexiconList.FamilyNames, word);
    private bool CommonLower(string word) => _lexicon.Contains(LexiconList.CommonLower, word.ToLowerInvariant());

    /// <summary>Any common word, also one written with a capital only (both lists, each asked on its own).</summary>
    private bool CommonAll(string word)
    {
        var lower = word.ToLowerInvariant();
        return _lexicon.Contains(LexiconList.CommonLower, lower) || _lexicon.Contains(LexiconList.CommonOther, lower);
    }

    /// <summary>A place of GeoNames, big or small (both lists, each asked on its own).</summary>
    private bool Place(string word) => _lexicon.Contains(LexiconList.BigPlaces, word) || _lexicon.Contains(LexiconList.SmallPlaces, word);

    private static string Strip(string word, string chars) => word.Trim(chars.ToCharArray());

    private static string[] Split(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The last whitespace-separated word before a position (empty if there is none).</summary>
    private static string LastWordBefore(string text, int pos)
    {
        var end = pos;
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        var start = end;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        return text[start..end];
    }

    /// <summary>The last whitespace-separated word of the text from <paramref name="from"/> to <paramref name="pos"/>.</summary>
    private static string LastWordBefore(string text, int from, int pos)
    {
        var end = pos;
        while (end > from && char.IsWhiteSpace(text[end - 1])) end--;
        var start = end;
        while (start > from && !char.IsWhiteSpace(text[start - 1])) start--;
        return text[start..end];
    }

    /// <summary>
    /// Stretches of a text, for the questions the rules ask many times — does a position lie inside one, does a stretch lie within one, does
    /// one end shortly before a position — each answered by a binary search (a page with thousands of finds must not compare them pair by pair).
    /// </summary>
    private sealed class Spans
    {
        private readonly int[] _starts, _maxEnds, _ends;

        public Spans(IEnumerable<(int Start, int End)> spans)
        {
            var sorted = spans.OrderBy(s => s.Start).ToArray();
            _starts = [.. sorted.Select(s => s.Start)];
            _maxEnds = new int[sorted.Length];
            var max = int.MinValue;
            for (var i = 0; i < sorted.Length; i++) _maxEnds[i] = max = Math.Max(max, sorted[i].End);
            _ends = [.. sorted.Select(s => s.End).Order()];
        }

        /// <summary>Index of the last stretch starting at or before the position (-1: none).</summary>
        private int LastFrom(int pos)
        {
            int lo = 0, hi = _starts.Length - 1, found = -1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >>> 1;
                if (_starts[mid] <= pos)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else hi = mid - 1;
            }
            return found;
        }

        /// <summary>Whether a stretch holds the position.</summary>
        public bool Inside(int pos) => LastFrom(pos) is var i and >= 0 && _maxEnds[i] > pos;

        /// <summary>Whether a stretch holds the whole of start to end.</summary>
        public bool Within(int start, int end) => LastFrom(start) is var i and >= 0 && _maxEnds[i] >= end;

        /// <summary>Whether a stretch ends between the two positions (both included).</summary>
        public bool EndsBetween(int from, int to)
        {
            int lo = 0, hi = _ends.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (_ends[mid] < from) lo = mid + 1;
                else hi = mid;
            }
            return lo < _ends.Length && _ends[lo] <= to;
        }
    }

    /// <summary>Word characters as the prototype's patterns see them (Python: letters, digits, underscore).</summary>
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || char.IsNumber(c);

    /// <summary>
    /// A hit while the rules run. Types as in the prototype: P person, O company, S street, N house number, B post office box, Z postal code,
    /// L place, C country.
    /// </summary>
    private readonly record struct Raw(int Start, int End, char Type, EntityDoubt Doubt = EntityDoubt.None);
}
