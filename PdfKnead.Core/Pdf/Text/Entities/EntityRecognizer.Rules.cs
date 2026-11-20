using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfKnead.Core.Pdf.Text.Entities;

public sealed partial class EntityRecognizer
{
    /// <summary>
    /// The persons, companies and parts of addresses in a text, sorted by position; overlapping hits of one kind are merged (the widest
    /// wins, a sure one before a suggestion). Positions refer to <paramref name="text"/> as given, also where it is not in normal form C.
    /// </summary>
    /// <param name="text">The text (page text, any length; a rule whose pattern runs out of time is skipped).</param>
    public List<EntityHit> Find(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var (normal, map) = Normal(text);
        var result = new List<EntityHit>();
        foreach (var raw in FindRaw(normal))
        {
            var start = map is null ? raw.Start : map[raw.Start];
            var end = map is null ? raw.End : map[raw.End];
            if (end > start) result.Add(new EntityHit(start, end - start, KindOf(raw.Type), raw.Doubt));
        }
        return result;
    }

    private static EntityKind KindOf(char type) => type switch
    {
        'P' => EntityKind.Person,
        'O' => EntityKind.Organisation,
        'S' => EntityKind.Street,
        'N' => EntityKind.HouseNumber,
        'Z' => EntityKind.PostalCode,
        'L' => EntityKind.Place,
        'C' => EntityKind.Country,
        _ => EntityKind.PostBox,
    };

    /// <summary>
    /// The text in normal form C (the lists are), with a map from every position of it (and its end) to the position in the text; null if the
    /// text is in normal form already. Normalised piece by piece — a character with the marks that follow it — so positions stay exact.
    /// </summary>
    private static (string Text, int[]? Map) Normal(string text)
    {
        try
        {
            if (text.IsNormalized(NormalizationForm.FormC)) return (text, null);
        }
        catch (ArgumentException)
        {
            // an unpaired surrogate (foreign input): normalised piece by piece below, the broken piece kept as it is
        }
        var sb = new StringBuilder(text.Length);
        var map = new List<int>(text.Length + 1);
        var i = 0;
        while (i < text.Length)
        {
            var j = i + 1;
            if (char.IsHighSurrogate(text[i]) && j < text.Length && char.IsLowSurrogate(text[j])) j++;
            while (j < text.Length && IsMark(text[j])) j++;
            string piece;
            try
            {
                piece = text[i..j].Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                piece = text[i..j];   // an unpaired surrogate stays as it is
            }
            foreach (var _ in piece) map.Add(i);
            sb.Append(piece);
            i = j;
        }
        // a position maps to the start of the piece it lies in; the end of the text to the end (an end at a piece boundary is so the end of
        // the piece before)
        map.Add(text.Length);
        return (sb.ToString(), map.ToArray());
    }

    private static bool IsMark(char c) => CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private List<Raw> FindRaw(string text)
    {
        var others = new List<Raw>();
        var streets = new List<(int Start, int End)>();   // whole streets with their numbers, and post office boxes
        Run(() => Companies(text, others));
        Run(() => Organisations(text, others));
        Run(() => Addresses(text, others, streets));
        // a person inside a company name is the company's (Mustermann-Milch GmbH), inside a street or place its name (Rue Victor Hugo); the
        // persons' rules do not look inside them at all (docs/PATENTE.md, B2)
        var wholes = new Spans(others.Where(h => h.Type is 'O' or 'L' or 'C').Select(h => (h.Start, h.End)).Concat(streets));
        var persons = new List<Raw>();
        Run(() => Persons(text, wholes, persons));
        persons.RemoveAll(h => wholes.Within(h.Start, h.End));
        var sure = persons.Where(h => h.Doubt == EntityDoubt.None).ToList();
        var found = new List<Raw>(persons);
        found.AddRange(others);
        Run(() =>
        {
            foreach (var h in Propagated(text, sure))
                if (!wholes.Within(h.Start, h.End)) found.Add(h);
        });
        // overlapping hits of one type merged: the widest wins, sure before suggestion
        var result = new List<Raw>();
        foreach (var h in found.OrderBy(h => h.Type).ThenBy(h => h.Start).ThenByDescending(h => h.End - h.Start).ThenBy(h => h.Doubt == EntityDoubt.None ? 0 : 1))
        {
            if (result.Count > 0 && result[^1].Type == h.Type && h.Start < result[^1].End)
            {
                var last = result[^1];
                if (h.End > last.End || (h.Doubt == EntityDoubt.None && last.Doubt != EntityDoubt.None))
                    result[^1] = last with { End = Math.Max(h.End, last.End), Doubt = h.Doubt == EntityDoubt.None ? EntityDoubt.None : last.Doubt };
                continue;
            }
            result.Add(h);
        }
        result.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End != b.End ? a.End.CompareTo(b.End) : a.Type.CompareTo(b.Type));
        return result;
    }

    /// <summary>Runs one rule; a pattern that runs out of time on this text skips the rule (what it found so far stays).</summary>
    private static void Run(Action rule)
    {
        try
        {
            rule();
        }
        catch (RegexMatchTimeoutException)
        {
        }
    }

    // ---------------------------------------------------------------- persons

    /// <summary>End of a name of 1–4 capitalised words (particles allowed) from <paramref name="pos"/>; null if there is none.</summary>
    private (int Start, int End)? NameAfter(string text, int pos, bool needList = false)
    {
        var m = _nameWords.Match(text, pos);
        if (!m.Success) return null;
        var g = m.Groups[1];
        var tokens = new List<(int Start, int End)>();
        foreach (Match t in WhitespaceRuns.Matches(g.Value)) tokens.Add((t.Index, t.Index + t.Length));
        // „Erika Musterfrau. Gestern …“: the name ends with the sentence — unless a form of address follows („Farf. Dr. Bala …“)
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            var next = g.Value[tokens[i + 1].Start..tokens[i + 1].End];
            if (EndsSentence(g.Value[tokens[i].Start..tokens[i].End]) && !Salutations.Contains(next) && !_lexicon.Contains(LexiconList.Titles, next))
            {
                tokens.RemoveRange(i + 1, tokens.Count - i - 1);
                break;
            }
        }
        while (tokens.Count > 0 && (NotNames.Contains(Strip(g.Value[tokens[^1].Start..tokens[^1].End], ".,;")) || g.Value[tokens[^1].Start..tokens[^1].End].EndsWith(':')))
            tokens.RemoveAt(tokens.Count - 1);
        if (tokens.Count == 0 || NotNames.Contains(Strip(g.Value[tokens[0].Start..tokens[0].End], ".,;"))) return null;
        if (needList && !tokens.Any(t => Family(Strip(g.Value[t.Start..t.End], ".,;")) || Given(Strip(g.Value[t.Start..t.End], ".,;")))) return null;
        int start = g.Index + tokens[0].Start, end = g.Index + tokens[^1].End;
        while (end > start + 2 && text[end - 1] is ',' or ';' or ':') end--;
        if (text[end - 1] == '.' && !_initialAtEnd.IsMatch(text[start..end])) end--;   // a sentence end, not an initial
        return (start, end);
    }

    private static readonly Regex WhitespaceRuns = new("\\S+", RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether a word ends a sentence: it ends with a dot after at least four letters („Musterfrau.“) — not an initial („M.“), not a short
    /// abbreviation („Dr.“, „St.“), not a form of address, title or name ending („Prof.“, „Dipl.-Ing.“, „Ph.D.“).
    /// </summary>
    private bool EndsSentence(string word)
    {
        var w = word.TrimEnd(',', ';', ':');
        if (!w.EndsWith('.') || w[..^1].Contains('.')) return false;
        var letters = 0;
        for (var i = w.Length - 2; i >= 0 && char.IsLetter(w[i]); i--) letters++;
        if (letters < 4) return false;
        var bare = w.TrimEnd('.');
        return !(Salutations.Contains(w) || _lexicon.Contains(LexiconList.Titles, w) || _lexicon.Contains(LexiconList.Titles, bare) ||
                 _lexicon.Contains(LexiconList.NameEndings, w));
    }

    /// <summary>The end of a run of words, cut after the first word that ends a sentence (without its dot).</summary>
    private int SentenceCut(string text, int start, int end)
    {
        var i = start;
        while (i < end)
        {
            while (i < end && char.IsWhiteSpace(text[i])) i++;
            var j = i;
            while (j < end && !char.IsWhiteSpace(text[j])) j++;
            if (j > i && EndsSentence(text[i..j])) return j - 1;
            i = j;
        }
        return end;
    }

    /// <summary>A title run that holds a common word without a dot („Bruder“, „Pater“): judged where it stands (B5).</summary>
    private bool TitleIsWord(string title) => Split(title).Any(w => !w.EndsWith('.') && CommonLower(w));

    private void Persons(string text, Spans covered, List<Raw> hits)
    {
        foreach (var rx in new[] { _salutation, _titles })
            foreach (Match m in rx.Matches(text))
            {
                if (covered.Inside(m.Index)) continue;
                if (rx == _titles && TitleIsWord(m.Value))
                {
                    // a title that is also a word (Saint, San, General, Prince): the name on the same line and of the lists; sure only with a
                    // given and a family name of the lists, otherwise a suggestion (Saint Lucia, São Tomé are places)
                    if (m.Value.Contains('\n') || NameAfter(text, m.Index + m.Length, needList: true) is not { } t) continue;
                    var words = Split(text[t.Start..t.End]).Select(w => Strip(w, ".,;:")).ToArray();
                    var full = words.Length >= 2 && Given(words[0]) && Family(words[^1]);
                    hits.Add(new Raw(m.Index, t.End, 'P', full ? EntityDoubt.None : EntityDoubt.TitleIsAWord));
                    continue;
                }
                if (NameAfter(text, m.Index + m.Length) is { } n) hits.Add(new Raw(m.Index, n.End, 'P'));
            }
        foreach (Match m in _field.Matches(text))
            // a field ending with a colon names the person that follows (Name:, Kunde:); a greeting needs a name of the lists
            if (NameAfter(text, m.Index + m.Length, needList: !m.Value.TrimEnd().EndsWith(':')) is { } n && !covered.Inside(n.Start))
                hits.Add(new Raw(n.Start, n.End, 'P'));
        foreach (Match m in _personPair.Matches(text))
        {
            if (covered.Inside(m.Index)) continue;
            var words = Split(m.Groups[1].Value + m.Groups[2].Value).Select(w => Strip(w, ".,;:")).ToArray();
            if (!Given(words[0]) || NotNames.Contains(words[0])) continue;
            if (Articles.Contains(LastWordBefore(text, Math.Max(0, m.Index - 12), m.Index).ToLowerInvariant())) continue;
            // given names, then a family name of the list (also a double name whose parts are in it)
            var k = 1;
            while (k < words.Length - 1 && Given(words[k]) && !Family(words[k])) k++;
            var last = words[k];
            if (StreetWords.Contains(last) || Directions.Contains(words[0])) continue;   // Main Street, Charles Village, North Sean
            if (Family(last) || last.Split('-').Where(p => p.Length > 0).All(Family))
            {
                var at = text.IndexOf(last, m.Index, StringComparison.Ordinal);
                if (at < 0) continue;
                // both words also common words in small letters (Due Date, Mark Smith): only a suggestion
                var weak = CommonLower(words[0]) && CommonLower(last);
                hits.Add(new Raw(m.Index, at + last.Length, 'P', weak ? EntityDoubt.AlsoAWord : EntityDoubt.None));
            }
        }
        foreach (Match m in _inverted.Matches(text))
        {
            if (covered.Inside(m.Index)) continue;
            var family = Strip(Split(m.Groups[1].Value)[^1], ".,");
            var given = Strip(Split(m.Groups[2].Value)[0], ".,");
            if (Family(family) && Given(given) && !NotNames.Contains(family) && !NotNames.Contains(given)) hits.Add(new Raw(m.Index, m.Index + m.Length, 'P'));
        }
        // a name goes on over its generation and degrees (jr., MdB, Ph.D.)
        for (var i = 0; i < hits.Count; i++)
            if (_nameEnding.Match(text, hits[i].End) is { Success: true } e) hits[i] = hits[i] with { End = e.Index + e.Length };
        // suggestions (unticked): a single given or family name of the lists in the middle of a sentence, not a common word in small letters,
        // not after an article — „Parsons hat angerufen“, „mit Thomas besprochen“
        var taken = new Spans(hits.Select(h => (h.Start, h.End)));
        foreach (Match m in _singleName.Matches(text))
        {
            var word = m.Groups[1].Value;
            if (covered.Inside(m.Index) || taken.Inside(m.Index) || NotNames.Contains(word) || CommonLower(word)) continue;
            if (!(Family(word) || Given(word) || word.Split('-').All(Family))) continue;
            var b = m.Index;
            while (b > 0 && text[b - 1] is ' ' or '\t') b--;
            if (b == 0 || ".!?:\n\"„“".Contains(text[b - 1]) || Articles.Contains(LastWordBefore(text, b).ToLowerInvariant())) continue;
            hits.Add(new Raw(m.Index, m.Index + m.Length, 'P', EntityDoubt.SingleName));
        }
        if (_options.Thorough)
        {
            // thorough (Philter's whitelist turned round): every capitalised word that is no known word of any language and no name part
            // already found is a suggestion — also names the lists do not know; runs of them stay together
            taken = new Spans(hits.Select(h => (h.Start, h.End)));
            foreach (Match m in _thoroughRun.Matches(text))
            {
                var words = Split(m.Groups[1].Value);
                if (covered.Inside(m.Index) || taken.Inside(m.Index) || words.Any(NotNames.Contains)) continue;
                if (words.Any(w => !Given(w) && !Family(w) && CommonAll(w))) continue;
                if (Articles.Contains(LastWordBefore(text, m.Index).ToLowerInvariant())) continue;
                hits.Add(new Raw(m.Index, m.Index + m.Length, 'P', EntityDoubt.Unknown));
            }
        }
        foreach (Match m in _cjkPerson.Matches(text))
            if (CjkFamily.Contains(m.Groups[1].Value)) hits.Add(new Raw(m.Index, m.Index + m.Length, 'P'));
        foreach (Match m in _koreanPerson.Matches(text))
            if (CjkFamily.Contains(m.Groups[1].Value)) hits.Add(new Raw(m.Index, m.Index + m.Length, 'P'));
    }

    // ---------------------------------------------------------------- companies

    private void Companies(string text, List<Raw> hits)
    {
        foreach (Match m in _form.Matches(text))
        {
            // the name: capitalised words (and connectors) right before the legal form, at most six
            int? start = null;
            var count = 0;
            var pos = m.Index;
            while (count < 6)
            {
                while (pos > 0 && (char.IsWhiteSpace(text[pos - 1]) || text[pos - 1] == ',')) pos--;
                if (pos == 0) break;
                var tokenEnd = pos;
                while (pos > 0 && !char.IsWhiteSpace(text[pos - 1]) && text[pos - 1] != ',') pos--;
                var word = text[pos..tokenEnd];
                if (Connectors.Contains(word) && start is not null)
                {
                    start = pos;
                    count++;
                    continue;
                }
                if ((char.IsUpper(word[0]) || char.IsDigit(word[0])) && !NotNames.Contains(word) && !Salutations.Contains(word.TrimEnd('.')))
                {
                    start = pos;
                    count++;
                    continue;
                }
                break;
            }
            if (EndsWithDigit(text, m.Index)) continue;   // right after digits: part of a postal code (1234 AB), not a company
            if (start is not { } s || text[s..m.Index].Trim().Length == 0) continue;
            // a connector cannot begin the name (the words read one by one — never the whole rest of the text)
            while (true)
            {
                var a = s;
                while (a < text.Length && char.IsWhiteSpace(text[a])) a++;
                var b = a;
                while (b < text.Length && !char.IsWhiteSpace(text[b])) b++;
                if (a >= text.Length || !Connectors.Contains(text[a..b])) break;
                var c = b;
                while (c < text.Length && char.IsWhiteSpace(text[c])) c++;
                if (c >= text.Length) break;
                s = c;
            }
            hits.Add(new Raw(s, m.Index + m.Length, 'O'));
        }
        foreach (Match m in _prefixForm.Matches(text)) hits.Add(new Raw(m.Index, m.Index + m.Length, 'O'));
        foreach (Match m in _cjkCompany.Matches(text)) hits.Add(new Raw(m.Index, m.Index + m.Length, 'O'));
    }

    /// <summary>Whether the text before a position ends with a digit, perhaps with one space after it (also before a last line break).</summary>
    private static bool EndsWithDigit(string text, int pos)
    {
        static bool At(string t, int e) => (e >= 1 && char.IsDigit(t[e - 1])) || (e >= 2 && char.IsWhiteSpace(t[e - 1]) && char.IsDigit(t[e - 2]));
        return At(text, pos) || (pos >= 1 && text[pos - 1] == '\n' && At(text, pos - 1));
    }

    /// <summary>
    /// Organisations named by a keyword (Sparkasse Musterstadt, Amtsgericht Beispielhausen, University of Example): the keyword with the
    /// capitalised words right before or after it — never the keyword alone („das Institut“).
    /// </summary>
    private void Organisations(string text, List<Raw> hits)
    {
        foreach (Match m in _orgKeyword.Matches(text))
        {
            var after = _orgAfter.Match(text, m.Index + m.Length);
            var from = Math.Max(0, m.Index - 60);
            var before = _orgBefore.Match(text[from..m.Index]);
            int start = m.Index, end = m.Index + m.Length;
            if (after.Success && SentenceCut(text, after.Index, after.Index + after.Length) is var afterEnd && afterEnd > m.Index + m.Length &&
                !NotNames.Contains(Split(text[(m.Index + m.Length)..afterEnd])[^1]))
                end = afterEnd;   // „Sparkasse Musterstadt. Postfach …“: not over the end of the sentence
            if (before.Success && Split(before.Groups[1].Value) is [var firstWord, ..] && !NotNames.Contains(firstWord) && !Salutations.Contains(firstWord.TrimEnd('.')))
                start = m.Index - before.Groups[1].Length;
            if (start != m.Index || end != m.Index + m.Length) hits.Add(new Raw(start, end, 'O'));
        }
    }

    // ---------------------------------------------------------------- addresses and places

    private void Addresses(string text, List<Raw> hits, List<(int Start, int End)> streets)
    {
        var own = hits.Count;   // the address rules weigh only their own finds (as in the prototype), not the companies found before
        void Street(Group name, Group number, int wholeStart, int wholeEnd)
        {
            hits.Add(new Raw(name.Index, Trimmed(text, name.Index, name.Index + name.Length), 'S'));
            hits.Add(new Raw(number.Index, Trimmed(text, number.Index, number.Index + number.Length), 'N'));
            streets.Add((wholeStart, wholeEnd));
        }
        foreach (var rx in new[] { _streetSuffix, _streetPrefix, _streetAdjective, _streetSuffixLp, _streetTypeLp })
            if (rx is not null)
                foreach (Match m in rx.Matches(text)) Street(m.Groups[1], m.Groups[2], m.Index, m.Groups[2].Index + m.Groups[2].Length);
        foreach (var rx in new[] { _streetNumberFirst, _streetNumberTypeLp })
            if (rx is not null)
                foreach (Match m in rx.Matches(text)) Street(m.Groups[2], m.Groups[1], m.Index, m.Index + m.Length);
        var boxes = new List<(int Start, int End)>();
        foreach (var rx in new[] { _postBox, _postBoxLp })
            if (rx is not null)
                foreach (Match m in rx.Matches(text))
                {
                    hits.Add(new Raw(m.Index, m.Index + m.Length, 'B'));
                    boxes.Add((m.Index, m.Index + m.Length));
                }
        var near = new Spans(streets.Concat(boxes));
        foreach (Match m in _postcode.Matches(text))
        {
            var code = m.Groups[1];
            int codeStart = code.Index, codeEnd = code.Index + code.Length;
            var after = _cityAfter.Match(text, codeEnd);
            var from = Math.Max(0, codeStart - 40);
            var beforeCity = _cityBefore.Match(text[from..codeStart]);
            var nearStreet = near.EndsBetween(codeStart - 6, codeStart);
            (int Start, int End)? city = null;
            if (after.Success && !_lexicon.Contains(LexiconList.Countries, Split(after.Groups[1].Value)[0]))
                city = (after.Groups[1].Index, after.Groups[1].Index + after.Groups[1].Length);
            if (!(city is not null || nearStreet || (beforeCity.Success && Place(beforeCity.Groups[1].Value)))) continue;
            hits.Add(new Raw(codeStart, codeEnd, 'Z'));
            if (city is { } c) hits.Add(new Raw(c.Start, c.End, 'L'));
        }
        // places: the finds so far, asked by position (a place found here lies within its own match, so later matches never start in it)
        var placed = new Spans(hits.Skip(own).Select(h => (h.Start, h.End)));
        foreach (Match m in _placeWords.Matches(text))
        {
            var word = m.Groups[1].Value.TrimEnd('.', ',');
            var from = Math.Max(0, m.Index - 6);
            var before = text[from..m.Index];
            if (_lexicon.Contains(LexiconList.Countries, word) && _commaOrLineEnd.IsMatch(before))
            {
                hits.Add(new Raw(m.Index, m.Index + word.Length, 'C'));
                continue;
            }
            var preposition = _preposition.IsMatch(before);
            var known = _lexicon.Contains(LexiconList.BigPlaces, word) || (preposition && Place(word));
            var afterCommaStreet = _afterComma.IsMatch(before) && near.EndsBetween(m.Index - 3, m.Index);
            if (known && (_options.PlacesEverywhere || preposition || afterCommaStreet) && !placed.Inside(m.Index))
                hits.Add(new Raw(m.Index, m.Index + word.Length, 'L'));
        }
    }

    /// <summary>The end of a stretch without the whitespace at its end.</summary>
    private static int Trimmed(string text, int start, int end)
    {
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return end;
    }

    // ---------------------------------------------------------------- names across the text

    /// <summary>
    /// Names found once with certainty, found again elsewhere in the text — the whole name and the family name alone („Herr Max Mustermann …
    /// Mustermann bestätigt“), also the parts of an e-mail address (max.mustermann@…); never after an article.
    /// </summary>
    private IEnumerable<Raw> Propagated(string text, List<Raw> persons)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in persons)
        {
            var words = Split(text[p.Start..p.End]).Select(w => Strip(w, ".,;:"))
                .Where(w => w.Length > 0 && !Salutations.Contains(w.TrimEnd('.')) && !ParticleSet.Contains(w) && w.Length > 2).ToList();
            if (words.Count == 0) continue;
            known.Add(words[^1]);
            if (words.Count > 1) known.Add(string.Join(' ', words));
        }
        foreach (Match m in _emailName.Matches(text))
        {
            string given = Capitalize(m.Groups[1].Value), family = Capitalize(m.Groups[2].Value);
            if (Given(given) && Family(family))
            {
                known.Add(family);
                known.Add(given + " " + family);
            }
        }
        foreach (var name in known)
        {
            var at = text.IndexOf(name, StringComparison.Ordinal);
            while (at >= 0)
            {
                var end = at + name.Length;
                // a whole word only; the next try one character on (as a pattern search would), after a hit behind it
                if ((at > 0 && (IsWordChar(text[at - 1]) || text[at - 1] == '-')) || (end < text.Length && (IsWordChar(text[end]) || text[end] == '-')))
                {
                    at = at + 1 < text.Length ? text.IndexOf(name, at + 1, StringComparison.Ordinal) : -1;
                    continue;
                }
                if (!Articles.Contains(LastWordBefore(text, Math.Max(0, at - 12), at).ToLowerInvariant())) yield return new Raw(at, end, 'P');
                at = end < text.Length ? text.IndexOf(name, end, StringComparison.Ordinal) : -1;
            }
        }
    }

    private static string Capitalize(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
}
