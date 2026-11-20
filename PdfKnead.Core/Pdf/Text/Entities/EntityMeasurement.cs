using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PdfKnead.Core.Pdf.Text.Entities;

/// <summary>
/// Measuring the recognition of persons, companies and addresses as <c>tools/ner-messung/messen.py</c> does (KONZEPT.md, section 5.23), so
/// the rules in C# can be compared number for number with the prototype and the tests can hold lower bounds: found (an entity fully covered
/// by a hit of its type), missed, false hits (a hit that touches nothing of its type), and the share of words covered (a word left standing
/// is a leak, i2b2 2014). Types: P person, O company, A street with house number (a street and the number beside it count as one hit),
/// Z postal code, L place.
/// </summary>
public static class EntityMeasurement
{
    /// <summary>A text with its entities (positions in the text, type letter).</summary>
    public sealed record Row(string Language, string Text, IReadOnlyList<(int Start, int End, char Type)> Gold);

    /// <summary>The counts of one type.</summary>
    public sealed class Counts
    {
        public int Found, Missed, False, Words, WordsCovered;

        public double Recall => Found + Missed > 0 ? (double)Found / (Found + Missed) : double.NaN;
        public double Precision => Found + False > 0 ? (double)Found / (Found + False) : double.NaN;

        /// <summary>F2: recall counts twice (a name overlooked weighs more than a false hit when redacting).</summary>
        public double F2 => Precision + Recall > 0 ? 5 * Precision * Recall / (4 * Precision + Recall) : double.NaN;

        public double PerWord => Words > 0 ? (double)WordsCovered / Words : double.NaN;
    }

    /// <summary>The result: counts per type, and every miss and false hit for reading.</summary>
    public sealed record Scores(IReadOnlyDictionary<char, Counts> Types, IReadOnlyList<string> Misses, IReadOnlyList<string> Falses, double Seconds);

    /// <summary>The measured types, in their order.</summary>
    public const string Types = "POAZL";

    private static readonly Regex Marker = new("\\[\\[([POAZL]):(.+?)\\]\\]", RegexOptions.CultureInvariant);

    // Python's \w (letters, numbers, underscore — without the marks .NET's \w takes), so words are counted as messen.py counts them
    private static readonly Regex GlinerWord = new("[\\p{L}\\p{N}_]+(?:[-_][\\p{L}\\p{N}_]+)*|\\S", RegexOptions.CultureInvariant);
    private static readonly Regex AlnumRun = new("[\\p{L}\\p{N}_]+", RegexOptions.CultureInvariant);

    /// <summary>Test sentences: „language TAB text“, entities marked as [[P:Max Mustermann]], „\n“ for a line break, # for comments.</summary>
    public static List<Row> ReadSentences(IEnumerable<string> lines)
    {
        var rows = new List<Row>();
        foreach (var raw in lines)
        {
            if (raw.Trim().Length == 0 || raw.StartsWith('#')) continue;
            var line = raw.Replace("\\n", "\n");
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var body = line[(tab + 1)..];
            var text = new StringBuilder();
            var gold = new List<(int, int, char)>();
            var pos = 0;
            foreach (Match m in Marker.Matches(body))
            {
                text.Append(body, pos, m.Index - pos);
                gold.Add((text.Length, text.Length + m.Groups[2].Length, m.Groups[1].Value[0]));
                text.Append(m.Groups[2].Value);
                pos = m.Index + m.Length;
            }
            text.Append(body, pos, body.Length - pos);
            rows.Add(new Row(line[..tab], text.ToString(), gold));
        }
        return rows;
    }

    /// <summary>
    /// The texts of the DocPII benchmark (synthetic English texts, MIT; persons = name/first_name/last_name, companies = company_name, streets
    /// = street_address/address, postal codes = postcode, places = city; positions found by searching the given strings).
    /// </summary>
    public static List<Row> ReadDocPii(byte[] json)
    {
        var mapping = new Dictionary<string, char>
        {
            ["name"] = 'P', ["first_name"] = 'P', ["last_name"] = 'P', ["company_name"] = 'O', ["street_address"] = 'A', ["address"] = 'A',
            ["postcode"] = 'Z', ["city"] = 'L',
        };
        using var document = JsonDocument.Parse(json);
        var rows = new List<Row>();
        var entitiesOf = document.RootElement.GetProperty("entities");
        foreach (var t in document.RootElement.GetProperty("text").EnumerateObject())
        {
            var text = t.Value.GetString() ?? "";
            var entities = entitiesOf.GetProperty(t.Name);
            using var parsed = entities.ValueKind == JsonValueKind.String ? JsonDocument.Parse(entities.GetString()!) : null;
            var list = parsed?.RootElement ?? entities;
            var gold = new List<(int, int, char)>();
            foreach (var e in list.EnumerateArray())
            {
                var kind = e.TryGetProperty("types", out var ty) && ty.ValueKind == JsonValueKind.Array && ty.GetArrayLength() > 0 ? ty[0].GetString() ?? "" : "";
                if (!mapping.TryGetValue(kind, out var type) || !e.TryGetProperty("entity", out var en) || en.GetString() is not { Length: > 0 } entity) continue;
                for (var at = text.IndexOf(entity, StringComparison.Ordinal); at >= 0; at = text.IndexOf(entity, at + entity.Length, StringComparison.Ordinal))
                    gold.Add((at, at + entity.Length, type));
            }
            rows.Add(new Row("en", text, gold));
        }
        return rows;
    }

    /// <summary>Recognises every text and counts.</summary>
    /// <param name="recognizer">The recognizer.</param>
    /// <param name="rows">The texts.</param>
    /// <param name="sureOnly">Only the sure hits, without the suggestions a user would have to tick.</param>
    public static Scores Score(EntityRecognizer recognizer, IEnumerable<Row> rows, bool sureOnly = false)
    {
        var counts = Types.ToDictionary(t => t, _ => new Counts());
        var misses = new List<string>();
        var falses = new List<string>();
        var watch = new System.Diagnostics.Stopwatch();
        foreach (var (language, text, gold) in rows)
        {
            watch.Start();
            var hits = Long(recognizer, text, sureOnly);
            watch.Stop();
            foreach (var t in Types)
            {
                var c = counts[t];
                foreach (var (a, b, g) in gold)
                {
                    if (g != t) continue;
                    foreach (Match m in AlnumRun.Matches(text[a..b]))
                    {
                        c.Words++;
                        if (hits.Any(h => h.Type == t && h.Start <= a + m.Index && h.End >= a + m.Index + m.Length)) c.WordsCovered++;
                    }
                    if (hits.Any(h => h.Type == t && h.Start <= a && h.End >= b)) c.Found++;
                    else
                    {
                        c.Missed++;
                        misses.Add($"{language} {t}: {text[a..b]}");
                    }
                }
                foreach (var h in hits)
                    if (h.Type == t && !gold.Any(g => g.Type == t && !(h.End <= g.Start || h.Start >= g.End)))
                    {
                        c.False++;
                        falses.Add($"{language} {t} ({(h.Sure ? "1.00" : "0.50")}): {text[h.Start..h.End]}");
                    }
            }
        }
        return new Scores(counts, misses, falses, watch.Elapsed.TotalSeconds);
    }

    private readonly record struct Scored(int Start, int End, char Type, bool Sure);

    /// <summary>Hits in measuring types. Long texts in pieces of 200 words, as messen.py does (it was written for a model that reads 384 tokens).</summary>
    private static List<Scored> Long(EntityRecognizer recognizer, string text, bool sureOnly)
    {
        var tokens = GlinerWord.Matches(text);
        if (tokens.Count <= 200) return Measured(recognizer, text, 0, sureOnly);
        var result = new List<Scored>();
        for (var i = 0; i < tokens.Count; i += 200)
        {
            var a = tokens[i].Index;
            var last = tokens[Math.Min(i + 200, tokens.Count) - 1];
            result.AddRange(Measured(recognizer, text[a..(last.Index + last.Length)], a, sureOnly));
        }
        return result;
    }

    private static List<Scored> Measured(EntityRecognizer recognizer, string text, int offset, bool sureOnly)
    {
        var hits = recognizer.Find(text).Where(h => !sureOnly || h.Sure).ToList();
        var result = new List<Scored>();
        foreach (var h in hits)
        {
            var type = h.Kind switch
            {
                EntityKind.Person => 'P',
                EntityKind.Organisation => 'O',
                EntityKind.PostalCode => 'Z',
                EntityKind.Place => 'L',
                _ => '\0',
            };
            if (type != '\0') result.Add(new Scored(h.Start + offset, h.End + offset, type, h.Sure));
        }
        // a street with the house number right beside it (either side, only spaces and a comma between) is one address
        foreach (var s in hits.Where(h => h.Kind == EntityKind.Street))
        {
            int start = s.Start, end = s.End;
            foreach (var n in hits.Where(h => h.Kind == EntityKind.HouseNumber))
            {
                if (n.Start >= s.End && n.Start - s.End <= 3 && text[s.End..n.Start].All(c => char.IsWhiteSpace(c) || c == ',')) end = Math.Max(end, n.End);
                else if (s.Start >= n.End && s.Start - n.End <= 3 && text[n.End..s.Start].All(c => char.IsWhiteSpace(c) || c == ',')) start = Math.Min(start, n.Start);
            }
            result.Add(new Scored(start + offset, end + offset, 'A', s.Sure));
        }
        return result;
    }
}
