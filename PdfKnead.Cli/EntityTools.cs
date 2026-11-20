using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Text.Entities;

namespace PdfKnead.Cli;

/// <summary>
/// The recognition of persons, companies and addresses on the command line (KONZEPT.md, section 5.23): building the lexicon from the source
/// lists, recognising a text, and measuring as <c>tools/ner-messung/messen.py</c> does (same sentences, same counting), so the C# rules can be
/// compared number for number with the prototype.
/// </summary>
internal static class EntityTools
{
    /// <summary>Legal forms of the own list (beside those of rigour; the ISO 20275 list is left out while its licence is unclear).</summary>
    private static readonly string[] ExtraForms =
    [
        "GmbH", "GmbH & Co. KG", "AG", "KG", "OHG", "UG (haftungsbeschränkt)", "e. V.", "e.V.", "e. K.", "e.K.", "mbH", "Ltd.", "Ltd",
        "Limited", "Inc.", "Inc", "Corp.", "Corporation", "LLC", "L.L.C.", "PLC", "plc", "SARL", "S.A.R.L.", "EURL", "SAS", "SA", "S.A.",
        "SNC", "S.r.l.", "S.p.A.", "S.L.", "S.L.U.", "Ltda.", "B.V.", "N.V.", "AB", "AS", "ASA", "A/S", "ApS", "Oy", "Oyj", "A.Ş.",
        "s.r.o.", "a.s.", "Sp. z o.o.", "sp. z o.o.", "S.A.S.", "SE", "KGaA", "GesmbH", "Ges.m.b.H.",
    ];

    private static readonly string[] ExtraCountries =
    [
        "Deutschland", "Österreich", "Schweiz", "Frankreich", "Italien", "Spanien", "Niederlande", "Polen", "Tschechien",
        "Schweden", "Norwegen", "Finnland", "Türkei", "Russland", "USA", "UK", "Suisse", "Svizzera", "España", "Italia",
        "France", "Polska", "Česko", "Sverige", "Norge", "Suomi", "Türkiye", "Россия", "United Kingdom", "United States",
    ];

    private static readonly string[] ExtraEndings =
    [
        "jr.", "jr", "Jr.", "Jr", "sen.", "Sen.", "Sr.", "II", "III", "IV", "MdB", "MdL", "MP", "MD", "DDS", "M.D.", "Ph.D.", "PhD",
        "MBA", "M.A.", "B.A.", "M.Sc.", "B.Sc.", "LL.M.", "Esq.",
    ];

    /// <summary>
    /// "pdf-knead lexikon &lt;listen&gt; &lt;ausgabe.br&gt;": reads the source lists (layout as in <c>tools/ner-messung/LIESMICH.md</c>) and writes
    /// the packed lexicon; prints the size of every list.
    /// </summary>
    /// <summary>„--plz-formate“: take the postal code formats of all countries into the lexicon (measured worse; for trying only).</summary>
    public static bool WithPostcodeFormats { get; set; }

    public static int BuildLexicon(string folder, string output)
    {
        var lists = new Dictionary<LexiconList, IEnumerable<string>>();
        string Path_(params string[] parts) => Path.Combine([folder, .. parts]);
        IEnumerable<string> Lines(string path) => File.ReadLines(path, Encoding.UTF8).Where(l => l.Trim().Length > 0);

        lists[LexiconList.GivenNames] = Lines(Path_("wikidata", "vornamen.txt")).ToList();
        lists[LexiconList.FamilyNames] = Lines(Path_("wikidata", "nachnamen.txt")).ToList();

        // common words: single words only (docs/PATENTE.md, B1) — the pattern takes no spaces
        var lower = new HashSet<string>(StringComparer.Ordinal);
        var all = new HashSet<string>(StringComparer.Ordinal);
        var lemma = new Regex("^\"([^\" ]+)\"@", RegexOptions.CultureInvariant);
        foreach (var line in File.ReadLines(Path_("wikidata", "woerter.tsv"), Encoding.UTF8))
            if (lemma.Match(line) is { Success: true } m)
            {
                var word = m.Groups[1].Value;
                all.Add(word.ToLowerInvariant());
                if (char.IsLower(word[0])) lower.Add(word.ToLowerInvariant());
            }
        // each word once: the small-letter words, and the others (written with a capital only) apart from them
        lists[LexiconList.CommonLower] = lower;
        all.ExceptWith(lower);
        lists[LexiconList.CommonOther] = all;

        var places = new HashSet<string>(StringComparer.Ordinal);
        var big = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(Path_("geonames", "cities1000.txt"), Encoding.UTF8))
        {
            var p = line.Split('\t');
            if (p.Length < 15) continue;
            var names = new HashSet<string>(StringComparer.Ordinal) { p[1], p[2] };
            if ((int.TryParse(p[14], NumberStyles.Integer, CultureInfo.InvariantCulture, out var people) ? people : 0) >= 15000)
            {
                foreach (var a in p[3].Split(',')) if (a.Length > 0) names.Add(a);
                big.UnionWith(names);
            }
            places.UnionWith(names);
        }
        places.ExceptWith(big);   // each name once: the big places, and the small ones apart from them
        lists[LexiconList.SmallPlaces] = places;
        lists[LexiconList.BigPlaces] = big;

        var countries = new HashSet<string>(ExtraCountries, StringComparer.Ordinal);
        foreach (var line in File.ReadLines(Path_("geonames", "countryInfo.txt"), Encoding.UTF8))
            if (!line.StartsWith('#') && line.Split('\t') is { Length: > 4 } parts) countries.Add(parts[4]);
        // country names in many languages (Unicode CLDR); regions with numbers and alternative forms stay out
        if (Directory.Exists(Path_("cldr")))
            foreach (var file in Directory.EnumerateFiles(Path_("cldr"), "territories-*.json").Order(StringComparer.Ordinal))
            {
                using var json = JsonDocument.Parse(File.ReadAllBytes(file));
                foreach (var locale in json.RootElement.GetProperty("main").EnumerateObject())
                    foreach (var t in locale.Value.GetProperty("localeDisplayNames").GetProperty("territories").EnumerateObject())
                        if (t.Name.Length == 2 && t.Name.All(char.IsLetter) && !t.Name.Contains("-alt-") && t.Value.GetString() is { Length: > 0 } name)
                            countries.Add(name);
            }
        lists[LexiconList.Countries] = countries;

        // postal code formats of all countries (Google's address data) only with --plz-formate: measured on 13.11.2026 they make the recognition
        // much worse — some formats fit nearly every word (DocPII: false places 220 → 9 494, persons found 1 143 → 369); the recognizer's
        // own eight formats cover Europe, the US, the UK and Canada (KONZEPT.md, section 5.23)
        var formats = new List<string>();
        var info = Path_("libaddressinput", "countryinfo.txt");
        if (WithPostcodeFormats && File.Exists(info))
            foreach (var line in File.ReadLines(info, Encoding.UTF8))
                if (line.Contains("\"zip\":\"") && line.StartsWith("data/", StringComparison.Ordinal) && line.IndexOf('=') is > 5 and var eq && line[..eq].Count(c => c == '/') == 1)
                {
                    using var json = JsonDocument.Parse(line[(line.IndexOf('=') + 1)..]);
                    if (json.RootElement.TryGetProperty("zip", out var zip) && zip.GetString() is { Length: > 0 } pattern) formats.Add(pattern);
                }
        lists[LexiconList.PostcodeFormats] = formats;

        // legal forms: the own list and those of OpenSanctions rigour (display form and aliases, with their capitals as written)
        var forms = new HashSet<string>(ExtraForms, StringComparer.Ordinal);
        var orgTypes = Path_("rigour", "names", "org_types.yml");
        var item = new Regex("^\\s*(?:- display:|-)\\s+(.+?)\\s*$", RegexOptions.CultureInvariant);
        if (File.Exists(orgTypes))
            foreach (var line in File.ReadLines(orgTypes, Encoding.UTF8))
                if (!line.TrimStart().StartsWith('#') && item.Match(line) is { Success: true } m)
                {
                    var value = m.Groups[1].Value.Trim('"', '\'');
                    if (value.Length is >= 2 and <= 40 && (value.Contains('.') || value.Count(char.IsUpper) >= 2 || value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 2))
                        forms.Add(value);
                }
        lists[LexiconList.LegalForms] = forms;

        // libpostal's dictionaries (about 60 languages): titles, name endings, street words, post office words
        var lp = Path_("libpostal");
        HashSet<string> LpWords(string kind, ISet<string>? languages = null)
        {
            var words = new HashSet<string>(StringComparer.Ordinal);
            if (!Directory.Exists(lp)) return words;
            foreach (var dir in Directory.EnumerateDirectories(lp).Select(Path.GetFileName).Order(StringComparer.Ordinal))
            {
                var path = Path.Combine(lp, dir!, kind);
                if ((languages is null || languages.Contains(dir!)) && File.Exists(path))
                    foreach (var line in File.ReadAllText(path, Encoding.UTF8).Split('\n'))
                        foreach (var w in line.TrimEnd('\r').Split('|'))
                            if (w.Trim().Length >= 2) words.Add(w.Trim());
            }
            return words;
        }
        var compounding = new HashSet<string>(["de", "nl", "sv", "da", "nb", "fi", "gsw", "lb", "et", "is", "af"], StringComparer.Ordinal);
        lists[LexiconList.StreetSuffixes] = LpWords("concatenated_suffixes_separable.txt", compounding).Where(w => w.All(char.IsLetter) && w.Length >= 3).ToList();
        lists[LexiconList.StreetTypesBefore] = LpWords("street_types.txt").Except(LpWords("street_types.txt", new HashSet<string>(["en"]))).Where(w => w.Length >= 2).ToList();
        // every title stays (docs/PATENTE.md, B5: nothing is taken out of a list because it is also a common word)
        lists[LexiconList.Titles] = LpWords("personal_titles.txt").Where(w => w.Length >= 2).Select(w => char.ToUpperInvariant(w[0]) + w[1..]).ToList();
        var endings = new HashSet<string>(ExtraEndings, StringComparer.Ordinal);
        foreach (var w in LpWords("personal_suffixes.txt").Concat(LpWords("academic_degrees.txt")))
            if (w.Contains('.') || w.Length <= 4)
            {
                endings.Add(w);
                endings.Add(w.ToUpperInvariant());
                endings.Add(char.ToUpperInvariant(w[0]) + w[1..]);
            }
        lists[LexiconList.NameEndings] = endings;
        lists[LexiconList.PostOffices] = LpWords("post_office.txt").Where(w => w.Length >= 2).ToList();

        var fetched = File.Exists(Path_("wikidata", "abgerufen.txt")) ? File.ReadAllText(Path_("wikidata", "abgerufen.txt")).Trim() : "";
        lists[LexiconList.Sources] =
        [
            $"1 Wikidata: Vornamen, Nachnamen, Wörter (Lexeme) — CC0 1.0 — {fetched}",
            $"2 GeoNames: cities1000, countryInfo — CC BY 4.0 — {File.GetLastWriteTimeUtc(Path_("geonames", "cities1000.txt")):yyyy-MM-dd}",
            "3 Unicode CLDR: Ländernamen — Unicode License v3",
            "4 OpenSanctions rigour: Rechtsformen — MIT",
            "5 libpostal: Titel, Namenszusätze, Straßenwörter, Postfach — MIT",
            .. (WithPostcodeFormats ? ["6 Google libaddressinput: Postleitzahlformate — CC BY 4.0"] : Array.Empty<string>()),
        ];

        var raw = Lexicon.Write(lists);
        var packed = Lexicon.Pack(raw);
        File.WriteAllBytes(output, packed);
        var check = Lexicon.Read(raw);
        foreach (var list in check.Lists.Order()) Console.WriteLine($"{list,-18} {check.Count(list),9:N0}");
        Console.WriteLine(Tr.T("Nachschlagedatei: {0:N0} Bytes, gepackt {1:N0} Bytes", raw.Length, packed.Length));
        return 0;
    }

    /// <summary>"pdf-knead namen &lt;datei.txt&gt; [--orte-ueberall] [--gruendlich]": the hits in a text file, one per line.</summary>
    public static int Recognize(string file, EntityOptions options)
    {
        if (Lexicon.Default is not { } lexicon)
        {
            Console.Error.WriteLine(Tr.T("Das Programm hat keine Nachschlagedatei."));
            return 1;
        }
        var text = File.ReadAllText(file, Encoding.UTF8);
        foreach (var hit in new EntityRecognizer(lexicon, options).Find(text))
            Console.WriteLine($"{hit.Start}\t{hit.Kind}\t{(hit.Sure ? "sicher" : hit.Doubt.ToString())}\t{text.Substring(hit.Start, hit.Length).ReplaceLineEndings(" ")}");
        return 0;
    }

    /// <summary>
    /// "pdf-knead namen-messen &lt;saetze.txt|docpii.json&gt; [--sicher] [--gruendlich] [--orte-ueberall] [--einzeln]": counts as messen.py does
    /// (<see cref="EntityMeasurement"/>) and prints it in the same form.
    /// </summary>
    public static int Measure(string? sentences, string? docPii, bool sureOnly, EntityOptions options, bool single)
    {
        if (Lexicon.Default is not { } lexicon)
        {
            Console.Error.WriteLine(Tr.T("Das Programm hat keine Nachschlagedatei."));
            return 1;
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var recognizer = new EntityRecognizer(lexicon, options);
        Console.WriteLine($"Erkenner aufgebaut in {watch.ElapsedMilliseconds} ms");
        var rows = docPii is not null ? EntityMeasurement.ReadDocPii(File.ReadAllBytes(docPii))
            : EntityMeasurement.ReadSentences(File.ReadLines(sentences ?? Path.Combine("tools", "ner-messung", "pruefsaetze.txt"), Encoding.UTF8));
        var scores = EntityMeasurement.Score(recognizer, rows, sureOnly);
        Console.WriteLine("Regeln (ohne KI), C#");
        var names = new Dictionary<char, string> { ['P'] = "Personen", ['O'] = "Firmen", ['A'] = "Straßen", ['Z'] = "Postleitzahlen", ['L'] = "Orte" };
        foreach (var t in EntityMeasurement.Types)
        {
            var c = scores.Types[t];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {names[t],-15} gefunden {c.Found,3}, verpasst {c.Missed,3}, Fehltreffer {c.False,3} — Trefferquote {Percent(c.Recall)}, Genauigkeit {Percent(c.Precision)}, F2 {Percent(c.F2)}, je Wort {Percent(c.PerWord)}"));
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Zeit je Satz {scores.Seconds / Math.Max(1, rows.Count) * 1000:0.00} ms, gesamt {scores.Seconds:0.00} s"));
        if (single)
        {
            Console.WriteLine("  verpasst: " + string.Join("; ", scores.Misses));
            Console.WriteLine("  Fehltreffer: " + string.Join("; ", scores.Falses));
        }
        return 0;
    }

    private static string Percent(double value) => double.IsNaN(value) ? "nan%" : Math.Round(value * 100, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture) + "%";
}
