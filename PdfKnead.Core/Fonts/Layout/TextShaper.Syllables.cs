using System.Globalization;
using System.Text;

namespace PdfKnead.Core.Fonts.Layout;

/// <summary>
/// The shaper of the syllabic scripts (docs/FEHLERGEDAECHTNIS.md, No. 1175): the text is cut into syllables after the classes of its characters
/// (Indic_Syllabic_Category and Indic_Positional_Category of the Unicode Character Database, <see cref="SyllableData"/>), the characters of a
/// syllable are put into the order the font expects (reph, pre-base matras, pre-base reordering consonants; Khmer coeng ro and pre-base vowels;
/// Myanmar kinzi and medial ra), and the features of the OpenType registry apply in their stages, each at the glyphs of its place in the
/// syllable. This part holds what all these scripts share, the Indic scripts of the second shaping model (dev2 … mlm2, the old tags as
/// fallback) and Sinhala (sinh, since No. 1179). Written from the OpenType feature registry, the Unicode Standard and the published
/// descriptions of the Indic shaping model; no code or data of another shaper was used.
/// </summary>
public sealed partial class TextShaper
{
    /// <summary>The syllabic shaping a run needs.</summary>
    private enum Complex : byte
    {
        None,
        Indic,
        Khmer,
        Myanmar,
        Thai,
        Tibetan,
        /// <summary>The universal model (<see cref="UniversalScripts"/>, No. 1181).</summary>
        Universal,
    }

    /// <summary>The role of a character in its syllable, derived from the Unicode classes (and a few script rules) per script.</summary>
    private enum Cat : byte
    {
        Other,
        Consonant,
        Ra,
        Vowel,
        Nukta,
        Halant,
        NonJoiner,
        Joiner,
        Matra,
        Modifier,
        Vedic,
        Placeholder,
        DottedCircle,
        RegisterShifter,
        Repha,
        Medial,
        ConsonantStacker,
        Symbol,
        Robat,
        Asat,
        MedialYa,
        MedialRa,
        MedialWa,
        MedialHa,
        VowelPre,
        VowelAbove,
        VowelBelow,
        VowelPost,
        Anusvara,
        DotBelow,
        Selector,
        Dependent,
    }

    /// <summary>The places in a syllable, in the order the reordering sorts them.</summary>
    private enum Pos : byte
    {
        Start,
        RaToBecomeReph,
        PreMatra,
        PreConsonant,
        BaseConsonant,
        AfterMain,
        AboveConsonant,
        BeforeSub,
        BelowConsonant,
        AfterSub,
        BeforePost,
        PostConsonant,
        AfterPost,
        FinalConsonant,
        Smvd,
        End,
    }

    /// <summary>Bits of <see cref="ShapedGlyph.Mask"/>: the features of restricted reach in a syllable.</summary>
    internal const ushort MaskRphf = 1, MaskPref = 2, MaskBlwf = 4, MaskAbvf = 8, MaskHalf = 16, MaskPstf = 32, MaskInit = 64, MaskCfar = 128;

    /// <summary>Bits of <see cref="ShapedGlyph.Formed"/>.</summary>
    internal const byte FormedSubstituted = 1, FormedLigated = 2, FormedMultiplied = 4;

    /// <summary>Longest syllable in glyphs: a longer one is cut (the reordering is quadratic in its length).</summary>
    private const int MaxSyllable = 64;

    /// <summary>The syllabic shaping of a run by its script tags (the first is the tag of its script).</summary>
    private static Complex ComplexOf(string[] scripts) => scripts.Length == 0 ? Complex.None : scripts[0] switch
    {
        "dev2" or "bng2" or "gur2" or "gjr2" or "ory2" or "tml2" or "tel2" or "knd2" or "mlm2" or "sinh" => Complex.Indic,
        "khmr" => Complex.Khmer,
        "mym2" => Complex.Myanmar,
        "thai" or "lao " => Complex.Thai,
        "tibt" => Complex.Tibetan,
        var tag when UniversalScripts.Contains(tag) => Complex.Universal,
        _ => Complex.None,
    };

    /// <summary>
    /// Prepares the characters of a run of a syllabic script before any substitution: decomposes the vowels written in parts, gives each
    /// character its class, cuts the run into syllables and puts a dotted circle (U+25CC, where the font has it) before a sign without a base.
    /// Thai and Lao only take apart SARA AM.
    /// </summary>
    private void PrepareSyllables(List<ShapedGlyph> run, List<int> codePoints, Complex complex, string[] scripts, Work work)
    {
        switch (complex)
        {
            case Complex.Indic:
            {
                var script = IndicScriptOf(scripts[0]);
                MarkDiscouragedSequences(run, codePoints, work);
                DecomposeIndic(run, codePoints, work);
                CanonicalOrder(run, codePoints, work);
                for (var i = 0; i < run.Count; i++)
                {
                    var (cat, pos) = IndicCategory(codePoints[i], script);
                    // the Oriya overline after a vowel sign stands over that sign and moves with it (The Unicode Standard 12.5.1; No. 1183)
                    if (codePoints[i] == 0x0B55 && i > 0 && (Cat)run[i - 1].Category == Cat.Matra) pos = (Pos)run[i - 1].Position;
                    // the Gurmukhi bindi written before the vowel sign II stays before it, at its place (12.3.1)
                    if (codePoints[i] == 0x0A02 && i + 1 < codePoints.Count && codePoints[i + 1] == 0x0A40) pos = IndicCategory(0x0A40, script).Pos;
                    run[i] = run[i] with { Category = (byte)cat, Position = (byte)pos };
                }
                FindSyllables(run, codePoints, (r, i) => ParseIndic(r, i, script, codePoints), work);
                break;
            }
            case Complex.Khmer:
                PrepareKhmer(run, codePoints, work);
                break;
            case Complex.Myanmar:
                ComposeCanonically(run, codePoints, 0x1000, 0x109F, work);
                CanonicalOrder(run, codePoints, work);
                PrepareMyanmar(run, codePoints, work);
                break;
            case Complex.Tibetan:
                DecomposeTibetan(run, codePoints, work);
                CanonicalOrder(run, codePoints, work);
                TibetanBelowVowelsFirst(run, codePoints);
                PrepareTibetan(run, codePoints, work);
                break;
            case Complex.Thai:
                if (scripts[0] == "lao ") CanonicalOrder(run, codePoints, work);
                else ThaiConsonantDiacritics(run, codePoints, work);
                PrepareThai(run, codePoints, work);
                break;
            case Complex.Universal:
                PrepareUniversal(run, codePoints, work);
                break;
        }
    }

    /// <summary>Longest sequence of marks put into canonical order (longer ones stay as typed; the stream-safe text format allows 30).</summary>
    private const int MaxCanonicalMarks = 30;

    /// <summary>
    /// Puts each sequence of marks of a canonical combining class other than 0 into canonical order (The Unicode Standard, 3.11), so that
    /// canonically equivalent texts look the same — in Myanmar and Lao runs: Myanmar dot below (class 7) before asat (9), Lao U+0EBA (9) before
    /// U+0EB8 (118). The glyphs move with their characters, each keeps its text. Before, the marks stayed as typed and the shaping library gave
    /// other glyphs (measured: 33 Myanmar samples with Myanmar Text, 55 Lao samples with Leelawadee UI). Not in Thai and Khmer: there the
    /// reference shaper keeps orders fonts are built for that differ from the canonical one (measured in Thai: SARA U before PHINTHU, which the
    /// canonical order puts after it — 56 samples otherwise with all four Thai fonts); docs/FEHLERGEDAECHTNIS.md, No. 1179. Since No. 1183 also
    /// in Tibetan runs (after the compound vowels are taken apart: the vowel signs in their canonical order, measured with Microsoft Himalaya:
    /// 1 782 of 11 988 samples otherwise than the shaping library before, 330 after — all of them dotted circles of the library's own rules) and
    /// in the Indic runs (nukta before virama, the Vedic marks: measured with Nirmala UI 154 samples fewer otherwise than the library, none more;
    /// The Unicode Standard 12.8.3: the nukta precedes the halant).
    /// </summary>
    private static void CanonicalOrder(List<ShapedGlyph> run, List<int> codePoints, Work work)
    {
        for (var i = 0; i < codePoints.Count;)
        {
            if ((MarkInfo(codePoints[i]) & NonStarter) == 0)
            {
                i++;
                continue;
            }
            var start = i;
            while (i < codePoints.Count && (MarkInfo(codePoints[i]) & NonStarter) != 0) i++;
            var count = i - start;
            if (count is < 2 or > MaxCanonicalMarks || !work.Spend(count) || CanonicalMarkOrder(codePoints, start, count) is not { } order)
                continue;
            var glyphs = order.Select(k => run[start + k]).ToArray();
            var points = order.Select(k => codePoints[start + k]).ToArray();
            for (var k = 0; k < order.Length; k++)
            {
                run[start + k] = glyphs[k];
                codePoints[start + k] = points[k];
            }
        }
    }

    /// <summary>
    /// The canonical order of the marks <paramref name="start"/> … (indices into them), taken from the normalization of the runtime (NFD after a
    /// base that does not decompose); null if it stays or a mark decomposes.
    /// </summary>
    private static int[]? CanonicalMarkOrder(List<int> codePoints, int start, int count)
    {
        var sb = new StringBuilder("a");
        for (var k = 0; k < count; k++) sb.Append(char.ConvertFromUtf32(codePoints[start + k]));
        string normalized;
        try
        {
            normalized = sb.ToString().Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            return null;
        }
        var order = new List<int>(count);
        var used = new bool[count];
        foreach (var rune in normalized.EnumerateRunes().Skip(1))
        {
            var k = 0;
            while (k < count && (used[k] || codePoints[start + k] != rune.Value)) k++;
            if (k == count) return null;
            used[k] = true;
            order.Add(k);
        }
        if (order.Count != count) return null;
        for (var k = 0; k < count; k++)
            if (order[k] != k) return [.. order];
        return null;
    }

    /// <summary>The substitutions of a run of a syllabic script, stage by stage, and the clusters of reordered syllables merged.</summary>
    private void SubstituteSyllables(List<ShapedGlyph> run, Complex complex, string[] scripts, RunOptions options, string[] direction, HashSet<int>? italic, Work work)
    {
        switch (complex)
        {
            case Complex.Indic:
                SubstituteIndic(run, scripts, options, direction, italic, work);
                break;
            case Complex.Khmer:
                SubstituteKhmer(run, scripts, options, direction, italic, work);
                break;
            case Complex.Myanmar:
                SubstituteMyanmar(run, scripts, options, direction, italic, work);
                break;
            case Complex.Tibetan:
                SubstituteTibetan(run, scripts, options, direction, italic, work);
                break;
            case Complex.Universal:
                SubstituteUniversal(run, scripts, options, direction, italic, work);
                break;
        }
        MergeReorderedClusters(run);
    }

    // ------------------------------------------------------------ syllables and stages (shared)

    /// <summary>
    /// Cuts the run into syllables with <paramref name="parse"/> (length of the syllable at a position, whether it lacks its base) and numbers
    /// them; a syllable without a base gets a dotted circle as its base (after a repha), where the font has the glyph — the glyph has no text
    /// of its own.
    /// </summary>
    private void FindSyllables(List<ShapedGlyph> run, List<int> codePoints, Func<List<ShapedGlyph>, int, (int Length, bool Broken)> parse, Work work)
    {
        var dotted = _font.GlyphForUnicode(0x25CC);
        ushort id = 0;
        for (var i = 0; i < run.Count;)
        {
            var (length, broken) = work.Spend(1) ? parse(run, i) : (1, false);
            length = Math.Clamp(length, 1, Math.Min(MaxSyllable, run.Count - i));
            id = id == ushort.MaxValue ? (ushort)1 : (ushort)(id + 1);
            if (broken && dotted > 0 && run.Count < MaxGlyphs && work.Move(run.Count - i))
            {
                var at = (Cat)run[i].Category == Cat.Repha ? i + 1 : i;
                run.Insert(at, new ShapedGlyph
                {
                    Glyph = dotted, Cluster = run[i].Cluster, Text = "", Class = 1, Gates = run[i].Gates,
                    Category = (byte)Cat.DottedCircle, Position = (byte)Pos.BaseConsonant,
                });
                codePoints.Insert(at, 0x25CC);
                length++;
            }
            for (var k = i; k < i + length; k++) run[k] = run[k] with { Syllable = id };
            i += length;
        }
    }

    /// <summary>The ranges of the syllables of the run, in order (a glyph without a syllable is a range of its own).</summary>
    private static IEnumerable<(int Start, int End)> SyllableRanges(List<ShapedGlyph> run)
    {
        for (var i = 0; i < run.Count;)
        {
            var id = run[i].Syllable;
            var end = i + 1;
            while (id != 0 && end < run.Count && run[end].Syllable == id) end++;
            yield return (i, end);
            i = end;
        }
    }

    /// <summary>One lookup of a stage: the mask a glyph needs, whether it stays within a syllable, the gate of a feature of restricted reach, and
    /// whether it is one of the script's own features, which keep the joiners in their input (a ZWJ the other features pass over; No. 1183).</summary>
    private readonly record struct StageLookup(ushort Mask, bool PerSyllable, byte Gate, bool Manual = true);

    /// <summary>Adds the lookups of features to a stage (a lookup already there keeps the wider reach).</summary>
    private void AddStage(SortedDictionary<int, StageLookup> stage, IEnumerable<(string Feature, ushort Mask)> features, string[] scripts, RunOptions options,
        bool perSyllable)
    {
        if (_gsub is null) return;
        foreach (var (feature, mask) in features)
        {
            if (options.Disabled.Contains(feature)) continue;
            foreach (var lookup in _gsub.LookupsFor([feature], scripts, options.Language, required: false))
                Merge(stage, lookup, new StageLookup(mask, perSyllable, 0));
        }
    }

    private static void Merge(SortedDictionary<int, StageLookup> stage, int lookup, StageLookup add)
    {
        if (!stage.TryGetValue(lookup, out var known))
        {
            stage[lookup] = add;
            return;
        }
        var mask = known.Mask == 0 || add.Mask == 0 ? (ushort)0 : (ushort)(known.Mask | add.Mask);
        var gate = known.Gate == 0 || add.Gate == 0 ? (byte)0 : (byte)(known.Gate | add.Gate);
        stage[lookup] = new StageLookup(mask, known.PerSyllable && add.PerSyllable, gate, known.Manual || add.Manual);
    }

    /// <summary>The first stage: the required feature, locl and ccmp (and further features given), each syllable on its own.</summary>
    private SortedDictionary<int, StageLookup> FirstStage(string[] scripts, RunOptions options, params string[] more)
    {
        var stage = new SortedDictionary<int, StageLookup>();
        if (_gsub is null) return stage;
        foreach (var lookup in _gsub.LookupsFor([], scripts, options.Language, required: true)) Merge(stage, lookup, new StageLookup(0, true, 0));
        AddStage(stage, new[] { "locl", "ccmp" }.Concat(more).Select(f => (f, (ushort)0)), scripts, options, perSyllable: true);
        return stage;
    }

    /// <summary>The last stage: the features of the script given (<paramref name="perSyllable"/> each syllable on its own) together with the
    /// other substitutions switched on (rlig, liga, clig, calt, rclt …) and the features of the run's direction, in LookupList order.</summary>
    private SortedDictionary<int, StageLookup> LastStage(IEnumerable<(string Feature, ushort Mask)> script, bool perSyllable, string[] scripts, RunOptions options,
        string[] direction)
    {
        var stage = new SortedDictionary<int, StageLookup>();
        if (_gsub is null) return stage;
        AddStage(stage, script, scripts, options, perSyllable);
        var general = options.Substitutions.Where(f => f is not ("locl" or "ccmp")).Concat(direction).ToList();
        foreach (var (lookup, gate) in Gated(_gsub, general, scripts, options.Language, false)) Merge(stage, lookup, new StageLookup(0, false, gate, false));
        return stage;
    }

    /// <summary>
    /// Applies the lookups of a stage in LookupList order, each only at glyphs with its mask; a lookup of a feature that stays within a syllable
    /// sees each syllable on its own (its contexts end at the syllable's edges). An alternate substitution takes its first alternate.
    /// </summary>
    private void ApplyStage(List<ShapedGlyph> run, SortedDictionary<int, StageLookup> stage, HashSet<int>? italic, Work work)
    {
        if (_gsub is null) return;
        var slice = new List<ShapedGlyph>();
        foreach (var (lookup, how) in stage)
        {
            if (work.Left <= 0) break;
            var ital = italic?.Contains(lookup) == true;
            work.AutoZwj = !how.Manual;
            if (!how.PerSyllable)
            {
                new Substitution(this, run, work, how.Gate, ital, how.Mask, firstAlternate: true).ApplyLookup(lookup);
                continue;
            }
            for (var i = 0; i < run.Count && work.Left > 0 && run.Count < MaxGlyphs;)
            {
                var id = run[i].Syllable;
                var end = i + 1;
                while (id != 0 && end < run.Count && run[end].Syllable == id) end++;
                slice.Clear();
                for (var k = i; k < end; k++) slice.Add(run[k]);
                new Substitution(this, slice, work, how.Gate, ital, how.Mask, firstAlternate: true).ApplyLookup(lookup);
                if (slice.Count == end - i)
                    for (var k = 0; k < slice.Count; k++) run[i + k] = slice[k];
                else
                {
                    if (!work.Move(run.Count - i)) break;
                    run.RemoveRange(i, end - i);
                    run.InsertRange(i, slice);
                }
                i += slice.Count;
            }
        }
        work.AutoZwj = false;
    }

    /// <summary>
    /// Whether one of the lookups would substitute exactly this glyph sequence where it stands alone (single and multiple substitution for one
    /// glyph, a ligature of exactly these glyphs, a context whose input is these glyphs — without backtrack and lookahead where
    /// <paramref name="zeroContext"/>) — the test of the shaping model for the forms a consonant takes (below-base, post-base, reph, pre-base
    /// reordering), on the glyphs themselves, without skipping.
    /// </summary>
    private bool WouldSubstitute(int[] lookups, Work work, bool zeroContext, params int[] glyphs)
    {
        if (_gsub is null || glyphs.Length == 0) return false;
        var t = _gsub.T;
        foreach (var index in lookups)
        {
            var lookup = _gsub.Lookup(index);
            foreach (var sub in lookup.Subtables)
            {
                if (!work.Spend(1)) return false;
                var hit = lookup.Type switch
                {
                    1 or 2 => glyphs.Length == 1 && t.Coverage(sub + t.U16(sub + 2), glyphs[0]) >= 0,
                    4 => WouldLigate(t, sub, glyphs, work),
                    5 or 6 => WouldMatchInput(t, sub, lookup.Type == 6, glyphs, zeroContext, work),
                    _ => false,
                };
                if (hit) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether a context subtable (formats 1–3) has a rule whose input is exactly these glyphs — with <paramref name="zeroContext"/> only a rule
    /// without backtrack and lookahead, else whatever they ask for (the old shaping model and Malayalam look only at the input).
    /// </summary>
    private static bool WouldMatchInput(TableData t, int sub, bool chained, int[] glyphs, bool zeroContext, Work work)
    {
        var n = glyphs.Length;
        var format = t.U16(sub);
        if (format == 3)
        {
            int backtrack = chained ? t.U16(sub + 2) : 0, input = chained ? sub + 4 + backtrack * 2 : sub + 2;
            var count = t.U16(input);
            var coverages = chained ? input + 2 : sub + 6;
            if (count != n || zeroContext && (backtrack != 0 || chained && t.U16(input + 2 + count * 2) != 0)) return false;
            for (var k = 0; k < n; k++)
                if (!Covers(t, sub, coverages + k * 2, glyphs[k])) return false;
            return true;
        }
        if (format is not (1 or 2) || t.Coverage(sub + t.U16(sub + 2), glyphs[0]) is var c && c < 0) return false;
        var classDef = format == 2 ? ClassDef(t, sub, chained ? sub + 6 : sub + 4) : -1;
        var sets = format == 1 ? sub + 4 : chained ? sub + 10 : sub + 6;
        var which = format == 1 ? c : t.Class(classDef, glyphs[0]);
        if (which >= t.U16(sets) || t.U16(sets + 2 + which * 2) == 0) return false;
        var set = sub + t.U16(sets + 2 + which * 2);
        for (var r = 0; r < t.U16(set); r++)
        {
            if (!work.Spend(1)) return false;
            if (t.U16(set + 2 + r * 2) == 0) continue;
            var rule = set + t.U16(set + 2 + r * 2);
            int count, inputAt;
            if (chained)
            {
                var (_, inp, la, _, _) = ChainParts(t, rule);
                count = t.U16(inp - 2);
                inputAt = inp;
                if (zeroContext && (t.U16(rule) != 0 || t.U16(la - 2) != 0)) continue;
            }
            else
            {
                count = t.U16(rule);
                inputAt = rule + 4;
            }
            if (count != n) continue;
            var k = 1;
            while (k < n && t.U16(inputAt + (k - 1) * 2) == (format == 1 ? glyphs[k] : t.Class(classDef, glyphs[k]))) k++;
            if (k == n) return true;
        }
        return false;
    }

    /// <summary>Whether a ligature subtable has a ligature of exactly these glyphs.</summary>
    private static bool WouldLigate(TableData t, int sub, int[] glyphs, Work work)
    {
        var c = t.Coverage(sub + t.U16(sub + 2), glyphs[0]);
        if (c < 0 || c >= t.U16(sub + 4)) return false;
        var set = sub + t.U16(sub + 6 + c * 2);
        var count = t.U16(set);
        for (var l = 0; l < count; l++)
        {
            if (!work.Spend(1)) return false;
            var lig = set + t.U16(set + 2 + l * 2);
            if (t.U16(lig + 2) != glyphs.Length) continue;
            var k = 1;
            while (k < glyphs.Length && t.U16(lig + 4 + (k - 1) * 2) == glyphs[k]) k++;
            if (k == glyphs.Length) return true;
        }
        return false;
    }

    /// <summary>Moves the glyph at <paramref name="from"/> to <paramref name="to"/>, the glyphs between shifted by one (within a syllable).</summary>
    private static void Move(List<ShapedGlyph> run, int from, int to)
    {
        var g = run[from];
        if (from < to)
            for (var k = from; k < to; k++) run[k] = run[k + 1];
        else
            for (var k = from; k > to; k--) run[k] = run[k - 1];
        run[to] = g;
    }

    /// <summary>Sorts the glyphs of a syllable by their place (<see cref="ShapedGlyph.Position"/>), glyphs of the same place in their order.</summary>
    private static void SortByPosition(List<ShapedGlyph> run, int start, int end)
    {
        for (var i = start + 1; i < end; i++)
        {
            var g = run[i];
            var k = i;
            while (k > start && run[k - 1].Position > g.Position)
            {
                run[k] = run[k - 1];
                k--;
            }
            run[k] = g;
        }
    }

    /// <summary>
    /// A syllable whose glyphs no longer follow the text (a reordered matra, reph or consonant) becomes one cluster: its first glyph stands for the
    /// whole text of the syllable, the others for none — so that the text read back from the glyphs comes in the order typed, as common shaping
    /// engines merge such clusters.
    /// </summary>
    private static void MergeReorderedClusters(List<ShapedGlyph> run)
    {
        foreach (var (start, end) in SyllableRanges(run))
        {
            var ordered = true;
            for (var k = start + 1; k < end && ordered; k++) ordered = run[k].Cluster >= run[k - 1].Cluster;
            if (!ordered) MergeClusters(run, start, end);
        }
    }

    /// <summary>Makes glyphs <paramref name="start"/> … <paramref name="end"/> (exclusive) one cluster: the texts in text order on the first.</summary>
    private static void MergeClusters(List<ShapedGlyph> run, int start, int end)
    {
        var order = Enumerable.Range(start, end - start).OrderBy(k => run[k].Cluster).ThenBy(k => k);
        var text = string.Concat(order.Select(k => run[k].Text));
        var cluster = Enumerable.Range(start, end - start).Min(k => run[k].Cluster);
        for (var k = start; k < end; k++) run[k] = run[k] with { Cluster = cluster, Text = k == start ? text : "" };
    }

    private static bool IsJoiner(ShapedGlyph g) => (Cat)g.Category is Cat.Joiner or Cat.NonJoiner;

    /// <summary>A halant that did not become part of a ligature (a half form or a conjunct took it otherwise).</summary>
    private static bool IsHalant(ShapedGlyph g) => (Cat)g.Category == Cat.Halant && (g.Formed & FormedLigated) == 0;

    private static bool LigatedOnly(ShapedGlyph g) => (g.Formed & FormedLigated) != 0 && (g.Formed & FormedMultiplied) == 0;

    private static ShapedGlyph At(ShapedGlyph g, Pos position) => g with { Position = (byte)position };

    // ------------------------------------------------------------ Indic scripts

    /// <summary>Where the reph goes in the final reordering.</summary>
    private enum RephPlace : byte
    {
        AfterMain,
        BeforeSub,
        AfterSub,
        BeforePost,
        AfterPost,
    }

    /// <summary>How a reph is written: Ra and halant (implicit), Ra, halant and ZWJ (explicit), or a character of its own (logical repha).</summary>
    private enum RephMode : byte
    {
        Implicit,
        Explicit,
        Logical,
    }

    /// <summary>The rules of one Indic script: its virama, its Ra, the place and the writing of the reph, whether below-base forms also apply
    /// before the base, and the ISO 15924 code (for the places of its matras). Sinhala has a single shaping model (only the tag sinh), finds its
    /// base without asking the font (the last consonant without a ZWJ before it) and places its pre-base vowel sign and reph by the halants no
    /// form took (No. 1179).</summary>
    private sealed record IndicScript(string Code, int Virama, int[] Ra, RephPlace Reph, RephMode Mode, bool BelowBeforeBase)
    {
        public bool Sinhala => Code == "Sinh";
    }

    private static readonly IndicScript[] IndicScripts =
    [
        new("Deva", 0x094D, [0x0930], RephPlace.BeforePost, RephMode.Implicit, true),
        new("Beng", 0x09CD, [0x09B0, 0x09F0], RephPlace.AfterSub, RephMode.Implicit, true),
        new("Guru", 0x0A4D, [0x0A30], RephPlace.BeforeSub, RephMode.Implicit, true),
        new("Gujr", 0x0ACD, [0x0AB0], RephPlace.BeforePost, RephMode.Implicit, true),
        new("Orya", 0x0B4D, [0x0B30], RephPlace.AfterMain, RephMode.Implicit, true),
        new("Taml", 0x0BCD, [0x0BB0], RephPlace.AfterPost, RephMode.Implicit, true),
        new("Telu", 0x0C4D, [0x0C30], RephPlace.AfterPost, RephMode.Explicit, false),
        new("Knda", 0x0CCD, [0x0CB0], RephPlace.AfterPost, RephMode.Implicit, false),
        new("Mlym", 0x0D4D, [0x0D30], RephPlace.AfterMain, RephMode.Logical, true),
        // Sinhala: the reph is Ra, al-lakuna and ZWJ (explicit) and goes behind the main consonant
        new("Sinh", 0x0DCA, [0x0DBB], RephPlace.AfterMain, RephMode.Explicit, true),
    ];

    private static IndicScript IndicScriptOf(string tag) => tag switch
    {
        "bng2" => IndicScripts[1],
        "gur2" => IndicScripts[2],
        "gjr2" => IndicScripts[3],
        "ory2" => IndicScripts[4],
        "tml2" => IndicScripts[5],
        "tel2" => IndicScripts[6],
        "knd2" => IndicScripts[7],
        "mlm2" => IndicScripts[8],
        "sinh" => IndicScripts[9],
        _ => IndicScripts[0],
    };

    /// <summary>The class and the first place of a character of an Indic script (Indic_Syllabic_Category, Indic_Positional_Category).</summary>
    private static (Cat Cat, Pos Pos) IndicCategory(int cp, IndicScript script)
    {
        switch (cp)
        {
            case 0x25CC:
                return (Cat.DottedCircle, Pos.BaseConsonant);
            // the Oriya candrabindu is ordered before the below-base forms, the Gurmukhi udaat below the base, as the descriptions of the
            // scripts give them
            case 0x0B01:
                return (Cat.Modifier, Pos.BeforeSub);
            case 0x0A51:
                return (Cat.Vedic, Pos.BelowConsonant);
        }
        return SyllableData.SyllabicOf(cp) switch
        {
            SyllabicCategory.Consonant or SyllabicCategory.ConsonantDead => (script.Ra.Contains(cp) ? Cat.Ra : Cat.Consonant, Pos.BaseConsonant),
            // digits (European and of the script) carry vowel signs and the other signs like a placeholder (The Unicode Standard 12.9.3; before
            // they had no class and a sign after them a dotted circle, No. 1183)
            SyllabicCategory.ConsonantPlaceholder or SyllabicCategory.Number => (Cat.Placeholder, Pos.BaseConsonant),
            SyllabicCategory.ConsonantPrecedingRepha => (Cat.Repha, Pos.End),
            SyllabicCategory.ConsonantWithStacker => (Cat.ConsonantStacker, Pos.BaseConsonant),
            SyllabicCategory.ConsonantMedial or SyllabicCategory.ConsonantFinal or SyllabicCategory.ConsonantSubjoined => (Cat.Medial, Pos.End),
            SyllabicCategory.Nukta => (Cat.Nukta, Pos.End),
            SyllabicCategory.Virama or SyllabicCategory.InvisibleStacker => (Cat.Halant, Pos.End),
            SyllabicCategory.VowelIndependent or SyllabicCategory.Vowel => (Cat.Vowel, Pos.BaseConsonant),
            SyllabicCategory.VowelDependent or SyllabicCategory.PureKiller => (Cat.Matra, MatraPlace(script.Code, cp, SyllableData.PositionalOf(cp))),
            SyllabicCategory.Bindu or SyllabicCategory.Visarga or SyllabicCategory.SyllableModifier or SyllabicCategory.GeminationMark
                or SyllabicCategory.ToneMark => (Cat.Modifier, Pos.Smvd),
            SyllabicCategory.CantillationMark => (Cat.Vedic, Pos.Smvd),
            SyllabicCategory.Avagraha => (Cat.Symbol, Pos.End),
            SyllabicCategory.RegisterShifter => (Cat.RegisterShifter, Pos.End),
            SyllabicCategory.Joiner => (Cat.Joiner, Pos.End),
            SyllabicCategory.NonJoiner => (Cat.NonJoiner, Pos.End),
            _ => (Cat.Other, Pos.End),
        };
    }

    /// <summary>
    /// The place of a dependent vowel sign: before the base for one standing left; otherwise after the main consonant, before or after the
    /// below-base forms or after the post-base forms, as the descriptions of the scripts order the signs above, below and right of the base.
    /// </summary>
    private static Pos MatraPlace(string script, int cp, PositionalCategory position)
    {
        var side = position switch
        {
            PositionalCategory.Left or PositionalCategory.VisualOrderLeft or PositionalCategory.LeftAndRight or PositionalCategory.TopAndLeft
                or PositionalCategory.TopAndLeftAndRight or PositionalCategory.BottomAndLeft or PositionalCategory.TopAndBottomAndLeft => 'L',
            PositionalCategory.Right or PositionalCategory.TopAndRight or PositionalCategory.BottomAndRight or PositionalCategory.TopAndBottomAndRight => 'R',
            PositionalCategory.Bottom => 'B',
            _ => 'T',
        };
        if (side == 'L') return Pos.PreMatra;
        return (script, side) switch
        {
            ("Deva", _) => Pos.AfterSub,
            ("Beng", 'R') or ("Guru", _) or ("Orya", 'R') or ("Taml", 'R' or 'B') or ("Mlym", _) or ("Gujr", 'R' or 'B') => Pos.AfterPost,
            ("Orya", 'T') => Pos.AfterMain,
            ("Telu", 'R') => cp <= 0x0C42 ? Pos.BeforeSub : Pos.AfterSub,
            ("Knda", 'R') => cp is < 0x0CC3 or > 0x0CD6 ? Pos.BeforeSub : Pos.AfterSub,
            ("Telu" or "Knda", _) => Pos.BeforeSub,
            _ => Pos.AfterSub,
        };
    }

    /// <summary>
    /// Decomposes the characters of an Indic run that have a canonical decomposition, where the font has the glyphs of the parts: a vowel sign
    /// written in two parts (Bengali O, Tamil O …) always, a consonant with nukta when its composition is excluded (U+0958 …) or the font lacks
    /// the composed glyph. The first part keeps the character's text, the second has none. Then a consonant and a mark typed apart that compose
    /// canonically are composed where the font has the composed glyph — as common shaping engines normalize the text. Bengali Ya with nukta
    /// (U+09DF, excluded from composition) is taken apart and stays apart like the other excluded nukta pairs; the Sinhala vowel signs E-E, O,
    /// O-O and AU are taken apart canonically into two or three parts, also where the font's pstf could form a second half from the sign itself,
    /// as the reference shaper does (measured with Nirmala UI, docs/FEHLERGEDAECHTNIS.md, No. 1179).
    /// </summary>
    private void DecomposeIndic(List<ShapedGlyph> run, List<int> codePoints, Work work)
    {
        for (var i = 0; i < codePoints.Count && run.Count < MaxGlyphs; i++)
        {
            var c = codePoints[i];
            if (c < 0x0900 || c > 0x0DFF || !Rune.IsValid(c)) continue;
            var single = char.ConvertFromUtf32(c);
            var parts = single.Normalize(NormalizationForm.FormD);
            if (parts == single || parts.Length > 3) continue;
            var points = parts.EnumerateRunes().Select(r => r.Value).ToArray();
            if (points.Any(p => _font.GlyphForUnicode(p) <= 0)) continue;
            var firstIsMark = Rune.GetUnicodeCategory(new Rune(points[0])) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
            var composes = parts.Normalize(NormalizationForm.FormC) == single;
            if (!firstIsMark && composes && _font.GlyphForUnicode(c) > 0) continue;
            if (!work.Move(run.Count - i)) return;
            var original = run[i];
            var glyphs = points.Select((p, k) => original with
            {
                Glyph = _font.GlyphForUnicode(p), Text = k == 0 ? original.Text : "",
                Class = Rune.GetUnicodeCategory(new Rune(p)) == UnicodeCategory.NonSpacingMark ? (byte)3 : (byte)1,
            }).ToArray();
            run[i] = glyphs[0];
            run.InsertRange(i + 1, glyphs[1..]);
            codePoints[i] = points[0];
            codePoints.InsertRange(i + 1, points[1..]);
            i += points.Length - 1;
        }
        // a consonant and a mark that compose canonically become the composed character where the font has it (U+0928 U+093C → U+0929)
        for (var i = 0; i + 1 < codePoints.Count; i++)
        {
            int a = codePoints[i], b = codePoints[i + 1];
            if (a < 0x0900 || a > 0x0DFF || b < 0x0900 || b > 0x0DFF || !Rune.IsValid(a) || !Rune.IsValid(b)
                || Rune.GetUnicodeCategory(new Rune(a)) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                || Rune.GetUnicodeCategory(new Rune(b)) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)) continue;
            var composed = (char.ConvertFromUtf32(a) + char.ConvertFromUtf32(b)).Normalize(NormalizationForm.FormC).EnumerateRunes().ToArray();
            if (composed.Length != 1 || _font.GlyphForUnicode(composed[0].Value) is var glyph && glyph <= 0 || !work.Move(run.Count - i)) continue;
            run[i] = run[i] with { Glyph = glyph, Text = run[i].Text + run[i + 1].Text, Class = 1 };
            run.RemoveAt(i + 1);
            codePoints[i] = composed[0].Value;
            codePoints.RemoveAt(i + 1);
        }
    }

    /// <summary>
    /// The syllable of an Indic script at <paramref name="i"/>: a consonant syllable (an optional repha, up to five consonants joined by halants,
    /// each with nukta and joiners, then a final halant or matras, then syllable modifiers and Vedic signs), a vowel syllable (an independent
    /// vowel instead of the first consonant), a cluster on a placeholder or dotted circle, a symbol with its modifiers, or signs without a base
    /// (broken: a dotted circle becomes their base). Anything else is a syllable of one character. Since No. 1183: in Malayalam any number of
    /// syllable modifiers (The Unicode Standard 12.9.3: several anusvaras after a vowel; elsewhere two, as the reference shaper), in Gurmukhi a
    /// bindi before the vowel sign II within the syllable (12.3.1), Ra and virama before a dotted circle a reph on it.
    /// </summary>
    private static (int Length, bool Broken) ParseIndic(List<ShapedGlyph> run, int i, IndicScript script, List<int> codePoints)
    {
        var n = Math.Min(run.Count, i + MaxSyllable);
        Cat C(int k) => k < n ? (Cat)run[k].Category : Cat.Other;
        int Cp(int k) => k < n && k < codePoints.Count ? codePoints[k] : -1;
        var modifiers = script.Code == "Mlym" ? MaxSyllable : 2;
        bool IsC(int k) => C(k) is Cat.Consonant or Cat.Ra;
        bool IsZ(int k) => C(k) is Cat.Joiner or Cat.NonJoiner;

        // a consonant with its nuktas (a ZWJ before a nukta) and a register shifter
        int Cn(int k)
        {
            k++;
            if (C(k) == Cat.Joiner && C(k + 1) == Cat.Nukta) k++;
            for (var q = 0; q < 2 && C(k) == Cat.Nukta; q++) k++;
            if (C(k) == Cat.RegisterShifter || C(k) == Cat.NonJoiner && C(k + 1) == Cat.RegisterShifter) k += C(k) == Cat.NonJoiner ? 2 : 1;
            return k;
        }
        // a halant with the joiners around it: -1 for none
        int HalantGroup(int k)
        {
            if (IsZ(k) && C(k + 1) == Cat.Halant) k++;
            if (C(k) != Cat.Halant) return -1;
            k++;
            if (C(k) == Cat.Joiner)
            {
                k++;
                if (C(k) == Cat.Nukta) k++;
            }
            return k;
        }
        // after the last consonant: a final halant, or matras (each with nukta and halant), then modifiers and Vedic signs
        int Tail(int k)
        {
            var h = HalantGroup(k);
            if (h >= 0)
            {
                k = h;
                if (C(k) == Cat.NonJoiner) k++;
            }
            else
            {
                if (C(k) == Cat.Vedic) k++;
                while (true)
                {
                    var m = k;
                    while (IsZ(m)) m++;
                    // Gurmukhi: a bindi written before the vowel sign II to stand left of it (and on iri) belongs to the syllable
                    if (script.Code == "Guru" && Cp(m) == 0x0A02 && Cp(m + 1) == 0x0A40)
                    {
                        k = m + 2;
                        continue;
                    }
                    if (C(m) is not (Cat.Matra or Cat.Medial)) break;
                    k = m + 1;
                    for (var q = 0; q < 2 && C(k) == Cat.Nukta; q++) k++;
                    if (C(k) == Cat.Halant) k++;
                }
            }
            if (IsZ(k) && C(k + 1) == Cat.Modifier) k++;
            for (var q = 0; q < modifiers && C(k) == Cat.Modifier; q++) k++;
            if (C(k) == Cat.NonJoiner && C(k - 1) == Cat.Modifier) k++;
            for (var q = 0; q < 4 && C(k) == Cat.Vedic; q++) k++;
            return k;
        }
        // further consonants joined by halants after a base
        int Joined(int k, int count)
        {
            while (count < 5 && HalantGroup(k) is var h and >= 0 && IsC(h))
            {
                k = Cn(h);
                count++;
            }
            return k;
        }

        var start = i;
        if (C(start) is Cat.Repha or Cat.ConsonantStacker && (IsC(start + 1) || C(start + 1) == Cat.Placeholder)) start++;
        // Ra and virama before a dotted circle: the reph on the circle (as the reference shaper; the discouraged Ra, virama, I)
        if (C(i) == Cat.Ra && C(i + 1) == Cat.Halant && C(i + 2) == Cat.DottedCircle) start = i + 2;
        // Ra and virama before a vowel letter: the vowel syllable below with the reph on the vowel (before, Ra and virama made a syllable of
        // their own and the reph never formed there, as the reference shaper forms it)
        else if (IsC(start) && !(C(i) == Cat.Ra && C(i + 1) == Cat.Halant && C(i + 2) == Cat.Vowel)) return (Tail(Joined(Cn(start), 1)) - i, false);
        // a vowel syllable, also after a reph written as Ra and halant
        var v = C(i) == Cat.Repha ? i + 1 : C(i) == Cat.Ra && C(i + 1) == Cat.Halant && C(i + 2) == Cat.Vowel ? i + 2 : i;
        if (C(v) == Cat.Vowel)
        {
            var k = v + 1;
            for (var q = 0; q < 2 && C(k) == Cat.Nukta; q++) k++;
            k = C(k) == Cat.Joiner ? k + 1 : Joined(k, 1);
            return (Tail(k) - i, false);
        }
        if (C(start) is Cat.Placeholder or Cat.DottedCircle)
        {
            var k = start + 1;
            for (var q = 0; q < 2 && C(k) == Cat.Nukta; q++) k++;
            if (C(k) == Cat.Vedic) k++;
            return (Tail(Joined(k, 1)) - i, false);
        }
        if (C(i) == Cat.Symbol) return (Math.Max(1, Tail(i + 1) - i), false);
        // signs without a base
        if (C(i) is Cat.Nukta or Cat.Halant or Cat.Matra or Cat.Medial or Cat.Modifier or Cat.Vedic or Cat.RegisterShifter or Cat.Repha
            || IsZ(i) && C(i + 1) is Cat.Halant or Cat.Matra or Cat.Modifier)
        {
            var k = C(i) == Cat.Repha ? i + 1 : i;
            for (var q = 0; q < 2 && C(k) == Cat.Nukta; q++) k++;
            if (C(k) == Cat.Vedic) k++;
            var end = Tail(Joined(k, 0));
            return (Math.Max(1, end - i), true);
        }
        return (1, false);
    }

    /// <summary>
    /// The context of the Indic shaping of one run: the script, whether the font follows the old shaping model (only the old tag, or neither),
    /// the lookups the reordering asks for, the virama glyph and the places of the consonants found so far.
    /// </summary>
    private sealed class IndicRun(IndicScript script, bool oldSpec, int virama, int[] rphf, int[] pref, int[] blwf, int[] pstf, int[] vatu)
    {
        public IndicScript Script { get; } = script;
        public bool OldSpec { get; } = oldSpec;
        /// <summary>Whether the tests of the forms ask for a rule without context (the second model, except Malayalam).</summary>
        public bool ZeroContext { get; } = !oldSpec && script.Code != "Mlym";
        public int Virama { get; } = virama;
        public int[] Rphf { get; } = rphf;
        public int[] Pref { get; } = pref;
        public int[] Blwf { get; } = blwf;
        public int[] Pstf { get; } = pstf;
        public int[] Vatu { get; } = vatu;
        public Dictionary<int, Pos> Places { get; } = [];
    }

    /// <summary>The features of the Indic shaping applied one by one after the first reordering, each with the mask of the glyphs it acts on (0:
    /// all of the syllable); OpenType 64.2–68.6.</summary>
    private static readonly (string Feature, ushort Mask)[] IndicBasic =
    [
        ("nukt", 0), ("akhn", 0), ("rphf", MaskRphf), ("rkrf", 0), ("pref", MaskPref), ("blwf", MaskBlwf), ("abvf", MaskAbvf), ("half", MaskHalf),
        ("pstf", MaskPstf), ("vatu", 0), ("cjct", 0),
    ];

    /// <summary>The features of the Indic shaping applied together after the final reordering, each syllable on its own.</summary>
    private static readonly (string Feature, ushort Mask)[] IndicPresentation =
        [("init", MaskInit), ("pres", 0), ("abvs", 0), ("blws", 0), ("psts", 0), ("haln", 0)];

    /// <summary>
    /// The substitutions of an Indic run: locl and ccmp; the first reordering of each syllable (reph, base consonant, places, masks); nukt, akhn,
    /// rphf, rkrf, pref, blwf, abvf, half, pstf, vatu and cjct each as a stage of its own; the final reordering (pre-base matra, reph, pre-base
    /// reordering consonant, init); then init, pres, abvs, blws, psts and haln together with the other features in LookupList order.
    /// </summary>
    private void SubstituteIndic(List<ShapedGlyph> run, string[] scripts, RunOptions options, string[] direction, HashSet<int>? italic, Work work)
    {
        var script = IndicScriptOf(scripts[0]);
        var tag = _gsub?.ScriptTag(scripts);
        int[] Lookups(string feature) => _gsub is null || options.Disabled.Contains(feature) ? [] : _gsub.LookupsFor([feature], scripts, options.Language, required: false);
        // Sinhala has one shaping model only (No. 1179)
        var context = new IndicRun(script, !script.Sinhala && (tag is null || tag[3] != '2'), _font.GlyphForUnicode(script.Virama),
            Lookups("rphf"), Lookups("pref"), Lookups("blwf"), Lookups("pstf"), Lookups("vatu"));

        ApplyStage(run, FirstStage(scripts, options), italic, work);
        foreach (var (start, end) in SyllableRanges(run).ToList())
            if (work.Spend(end - start)) InitialReorderIndic(run, start, end, context, work);
        foreach (var feature in IndicBasic)
        {
            var stage = new SortedDictionary<int, StageLookup>();
            AddStage(stage, [feature], scripts, options, perSyllable: true);
            ApplyStage(run, stage, italic, work);
        }
        foreach (var (start, end) in SyllableRanges(run).ToList())
            if (work.Spend(end - start)) FinalReorderIndic(run, start, end, context);
        ApplyStage(run, LastStage(IndicPresentation, true, scripts, options, direction), italic, work);
    }

    /// <summary>Whether a glyph counts as a consonant for the reordering (consonants, the base of a vowel or placeholder syllable).</summary>
    private static bool IsIndicConsonant(ShapedGlyph g) => (Cat)g.Category is Cat.Consonant or Cat.Ra or Cat.ConsonantStacker or Cat.Medial or Cat.Vowel
        or Cat.Placeholder or Cat.DottedCircle;

    /// <summary>
    /// The place of a consonant after the base, from the font: below the base when blwf (or vatu) would substitute it with the virama before or
    /// after it, after the base when pstf or pref would; else a consonant that can be the base.
    /// </summary>
    private Pos ConsonantPlace(IndicRun context, int glyph, Work work)
    {
        // Sinhala finds its base without the font (InitialReorderIndic)
        if (context.Script.Sinhala) return Pos.BaseConsonant;
        if (context.Places.TryGetValue(glyph, out var known)) return known;
        var v = context.Virama;
        bool Either(int[] lookups) => lookups.Length > 0
            && (WouldSubstitute(lookups, work, context.ZeroContext, v, glyph) || WouldSubstitute(lookups, work, context.ZeroContext, glyph, v));
        var place = Either(context.Blwf) || Either(context.Vatu) ? Pos.BelowConsonant
            : Either(context.Pstf) || Either(context.Pref) ? Pos.PostConsonant : Pos.BaseConsonant;
        if (context.Places.Count < 4096) context.Places[glyph] = place;
        return place;
    }

    /// <summary>
    /// The first reordering of an Indic syllable: a reph (Ra and halant at the start that rphf would substitute, with a consonant after it; or a
    /// repha character) is set aside, the base consonant is the last consonant without a below-base or post-base form (the search stops at a
    /// ZWJ after a halant), the other glyphs take their places (before the base, below, after), the syllable is sorted by place (the reph and the
    /// pre-base matras to its front), and the masks are set: rphf at the reph, half (and blwf) before the base, blwf, abvf and pstf after it, pref
    /// at the first pair after the base that pref would substitute; a ZWNJ keeps the consonant before it from its half form.
    /// </summary>
    private void InitialReorderIndic(List<ShapedGlyph> run, int s, int e, IndicRun c, Work work)
    {
        var any = false;
        for (var i = s; i < e; i++)
            if (IsIndicConsonant(run[i]) && (Pos)run[i].Position == Pos.BaseConsonant)
            {
                run[i] = At(run[i], ConsonantPlace(c, run[i].Glyph, work));
                any = true;
            }
        if (!any) return;

        // Kannada: Ra, halant, ZWJ at the start is Ra with a visible halant, not a reph — the ZWJ goes before the halant
        if (c.Script.Code == "Knda" && s + 3 <= e && (Cat)run[s].Category == Cat.Ra && (Cat)run[s + 1].Category == Cat.Halant
            && (Cat)run[s + 2].Category == Cat.Joiner)
            (run[s + 1], run[s + 2]) = (run[s + 2], run[s + 1]);

        // the reph
        var limit = s;
        var reph = false;
        var mode = c.Script.Mode;
        var basePos = e;
        if (c.Rphf.Length > 0 && s + 3 <= e
            && (mode == RephMode.Implicit && !IsJoiner(run[s + 2]) || mode == RephMode.Explicit && (Cat)run[s + 2].Category == Cat.Joiner)
            && (WouldSubstitute(c.Rphf, work, c.ZeroContext, run[s].Glyph, run[s + 1].Glyph)
                || mode == RephMode.Explicit && WouldSubstitute(c.Rphf, work, c.ZeroContext, run[s].Glyph, run[s + 1].Glyph, run[s + 2].Glyph)))
        {
            limit = s + 2;
            while (limit < e && IsJoiner(run[limit])) limit++;
            basePos = s;
            reph = true;
        }
        else if (mode == RephMode.Logical && (Cat)run[s].Category == Cat.Repha)
        {
            limit = s + 1;
            while (limit < e && IsJoiner(run[limit])) limit++;
            basePos = s;
            reph = true;
        }

        // the base: from the end, the first consonant without a below-base form (and without a post-base form unless a below-base form came after
        // it), else the first consonant reached — in Sinhala the last consonant without a ZWJ before it (a ZWJ asks for the subjoined form of
        // the consonant after it: yansaya, rakaaraansaya), the consonants after the base below it (No. 1179)
        if (c.Script.Sinhala)
        {
            if (!reph) basePos = limit;
            for (var i = limit; i < e; i++)
                if (IsIndicConsonant(run[i]))
                {
                    if (limit < i && (Cat)run[i - 1].Category == Cat.Joiner) break;
                    basePos = i;
                }
            for (var i = basePos + 1; i < e; i++)
                if (IsIndicConsonant(run[i])) run[i] = At(run[i], Pos.BelowConsonant);
        }
        else
        {
            var seenBelow = false;
            for (var i = e - 1; i >= limit; i--)
            {
                var g = run[i];
                if (IsIndicConsonant(g))
                {
                    var place = (Pos)g.Position;
                    basePos = i;
                    if (place != Pos.BelowConsonant && (place != Pos.PostConsonant || seenBelow)) break;
                    if (place == Pos.BelowConsonant) seenBelow = true;
                }
                else if (i > s && (Cat)g.Category == Cat.Joiner && (Cat)run[i - 1].Category == Cat.Halant) break;
            }
        }
        // Ra and halant without another consonant: no reph, Ra is the base
        if (reph && basePos == s && limit - basePos <= 2) reph = false;

        // the places before and at the base
        for (var i = s; i < basePos && i < e; i++)
            if ((Pos)run[i].Position > Pos.PreConsonant) run[i] = At(run[i], Pos.PreConsonant);
        if (basePos < e) run[basePos] = At(run[basePos], Pos.BaseConsonant);
        if (reph) run[s] = At(run[s], Pos.RaToBecomeReph);

        // the old shaping model: the first halant after the base moves behind the last consonant
        if (c.OldSpec && basePos < e)
        {
            var noDouble = c.Script.Code == "Knda";
            for (var i = basePos + 1; i < e; i++)
            {
                if ((Cat)run[i].Category != Cat.Halant) continue;
                var j = e - 1;
                while (j > i && !(IsIndicConsonant(run[j]) || noDouble && (Cat)run[j].Category == Cat.Halant)) j--;
                if ((Cat)run[j].Category != Cat.Halant && j > i) Move(run, i, j);
                break;
            }
        }

        // joiners, nuktas, register shifters, medials and halants go with the glyph before them (a halant after a pre-base matra not with it)
        var last = Pos.Start;
        for (var i = s; i < e; i++)
        {
            var cat = (Cat)run[i].Category;
            if (cat is Cat.Joiner or Cat.NonJoiner or Cat.Nukta or Cat.RegisterShifter or Cat.Medial or Cat.Halant)
            {
                var place = last;
                if (cat == Cat.Halant && place == Pos.PreMatra)
                    for (var j = i; j > s; j--)
                        if ((Pos)run[j - 1].Position != Pos.PreMatra)
                        {
                            place = (Pos)run[j - 1].Position;
                            break;
                        }
                run[i] = At(run[i], place);
            }
            else if ((Pos)run[i].Position != Pos.Smvd) last = (Pos)run[i].Position;
        }
        // a consonant after the base takes the signs between it and the consonant or matra before it
        if (basePos < e)
        {
            var previous = basePos;
            for (var i = basePos + 1; i < e; i++)
                if (IsIndicConsonant(run[i]))
                {
                    for (var j = previous + 1; j < i; j++)
                        if ((Pos)run[j].Position < Pos.Smvd) run[j] = At(run[j], (Pos)run[i].Position);
                    previous = i;
                }
                else if ((Cat)run[i].Category == Cat.Matra) previous = i;
        }

        SortByPosition(run, s, e);
        basePos = e;
        for (var i = s; i < e; i++)
            if ((Pos)run[i].Position == Pos.BaseConsonant)
            {
                basePos = i;
                break;
            }

        // the masks
        for (var i = s; i < e && (Pos)run[i].Position == Pos.RaToBecomeReph; i++) run[i] = run[i] with { Mask = (ushort)(run[i].Mask | MaskRphf) };
        var before = (ushort)(MaskHalf | (!c.OldSpec && c.Script.BelowBeforeBase ? MaskBlwf : 0));
        for (var i = s; i < basePos && i < e; i++) run[i] = run[i] with { Mask = (ushort)(run[i].Mask | before) };
        for (var i = basePos + 1; i < e; i++) run[i] = run[i] with { Mask = (ushort)(run[i].Mask | MaskBlwf | MaskAbvf | MaskPstf) };
        // the old model's eyelash Ra of Devanagari: Ra and halant before the base without a ZWJ take blwf
        if (c.OldSpec && c.Script.Code == "Deva")
            for (var i = s; i + 1 < basePos && basePos <= e; i++)
                if ((Cat)run[i].Category == Cat.Ra && (Cat)run[i + 1].Category == Cat.Halant && (i + 2 == basePos || (Cat)run[i + 2].Category != Cat.Joiner))
                {
                    run[i] = run[i] with { Mask = (ushort)(run[i].Mask | MaskBlwf) };
                    run[i + 1] = run[i + 1] with { Mask = (ushort)(run[i + 1].Mask | MaskBlwf) };
                }
        if (c.Pref.Length > 0 && basePos + 2 < e)
            for (var i = basePos + 1; i + 1 < e; i++)
                if (WouldSubstitute(c.Pref, work, c.ZeroContext, run[i].Glyph, run[i + 1].Glyph))
                {
                    run[i] = run[i] with { Mask = (ushort)(run[i].Mask | MaskPref) };
                    run[i + 1] = run[i + 1] with { Mask = (ushort)(run[i + 1].Mask | MaskPref) };
                    break;
                }
        // a ZWNJ after a halant: the consonant before it takes no half form
        for (var i = s + 1; i < e; i++)
        {
            if ((Cat)run[i].Category != Cat.NonJoiner) continue;
            var j = i;
            do
            {
                j--;
                run[j] = run[j] with { Mask = (ushort)(run[j].Mask & ~MaskHalf) };
            }
            while (j > s && !IsIndicConsonant(run[j]));
        }
    }

    /// <summary>
    /// The final reordering of an Indic syllable after the basic features: the pre-base matra moves behind the last halant before the base that
    /// no half form took (not past a ZWJ after it, behind a ZWNJ) — in Sinhala behind the last halant no form took before the last consonant —,
    /// a formed reph moves to the place of its script (after the main consonant, in Sinhala not past such a halant; before or after the below-base
    /// forms, before or after the post-base forms, before the syllable modifiers), a pre-base reordering consonant formed by pref moves before
    /// the base (behind such a halant), and a pre-base matra at the start of a word takes init.
    /// </summary>
    private static void FinalReorderIndic(List<ShapedGlyph> run, int s, int e, IndicRun c)
    {
        if (e - s < 2 && !((Pos)run[s].Position == Pos.PreMatra)) return;
        var code = c.Script.Code;
        var noHalfForms = code is "Mlym" or "Taml";

        // the base again
        var basePos = s;
        for (; basePos < e; basePos++)
        {
            if ((Pos)run[basePos].Position < Pos.BaseConsonant) continue;
            // Malayalam: below-base forms not formed make the consonant after them the base
            if (code == "Mlym")
                for (var i = basePos + 1; i < e; i++)
                {
                    while (i < e && IsJoiner(run[i])) i++;
                    if (i == e || !IsHalant(run[i])) break;
                    i++;
                    while (i < e && IsJoiner(run[i])) i++;
                    if (i < e && IsIndicConsonant(run[i]) && (Pos)run[i].Position == Pos.BelowConsonant)
                    {
                        basePos = i;
                        run[basePos] = At(run[basePos], Pos.BaseConsonant);
                    }
                }
            if (s < basePos && (Pos)run[basePos].Position > Pos.BaseConsonant) basePos--;
            break;
        }
        if (basePos == e && s < basePos && (Cat)run[basePos - 1].Category == Cat.Joiner) basePos--;
        if (basePos < e)
            while (s < basePos && (Cat)run[basePos].Category is Cat.Nukta or Cat.Halant) basePos--;

        // the pre-base matra
        if (s + 1 < e && s < basePos)
        {
            var target = basePos == e ? basePos - 2 : basePos - 1;
            if (c.Script.Sinhala)
            {
                // Sinhala: behind the last halant no form took before the last consonant, also before the base — a conjunct the font does
                // not form leaves the vowel sign at the consonant it belongs to, as the reference shaper places it (measured with Nirmala UI;
                // docs/FEHLERGEDAECHTNIS.md, No. 1179)
                var last = s;
                for (var i = s; i < e; i++)
                    if (IsIndicConsonant(run[i])) last = i;
                target = s;
                for (var i = s; i < last; i++)
                    if (IsHalant(run[i]) && (Pos)run[i].Position != Pos.PreMatra) target = i;
            }
            else if (!noHalfForms)
            {
                while (true)
                {
                    while (target > s && (Cat)run[target].Category is not (Cat.Matra or Cat.Halant)) target--;
                    if (IsHalant(run[target]) && (Pos)run[target].Position != Pos.PreMatra)
                    {
                        if (target + 1 < e)
                        {
                            if ((Cat)run[target + 1].Category == Cat.Joiner && target > s)
                            {
                                target--;
                                continue;
                            }
                            if ((Cat)run[target + 1].Category == Cat.NonJoiner) target++;
                        }
                    }
                    else target = s;
                    break;
                }
            }
            if (s < target && (Pos)run[target].Position != Pos.PreMatra)
                for (var i = target; i > s; i--)
                    if ((Pos)run[i - 1].Position == Pos.PreMatra)
                    {
                        var old = i - 1;
                        if (old < basePos && basePos <= target) basePos--;
                        Move(run, old, target);
                        target--;
                    }
        }

        // the reph
        if (s + 1 < e && (Pos)run[s].Position == Pos.RaToBecomeReph && ((Cat)run[s].Category == Cat.Repha ^ LigatedOnly(run[s])))
        {
            var place = c.Script.Reph;
            var target = -1;
            int AfterHalant()
            {
                var t = s + 1;
                while (t < basePos && !IsHalant(run[t])) t++;
                if (t >= basePos || !IsHalant(run[t])) return -1;
                return t + 1 < basePos && IsJoiner(run[t + 1]) ? t + 1 : t;
            }
            if (place != RephPlace.AfterPost)
            {
                target = AfterHalant();
                if (target < 0 && place == RephPlace.AfterMain)
                {
                    target = basePos;
                    // in Sinhala not past a halant no form took (the halant of E-E after the base; No. 1179)
                    while (target + 1 < e && (Pos)run[target + 1].Position <= Pos.AfterMain && !(c.Script.Sinhala && IsHalant(run[target + 1]))) target++;
                    if (target >= e) target = -1;
                }
                if (target < 0 && place == RephPlace.AfterSub)
                {
                    target = basePos;
                    while (target + 1 < e && (Pos)run[target + 1].Position is not (Pos.PostConsonant or Pos.AfterPost or Pos.Smvd)) target++;
                    if (target >= e) target = -1;
                }
            }
            if (target < 0) target = AfterHalant();
            if (target < 0)
            {
                // at the end of the syllable, before the modifiers; before a halant after a matra
                target = e - 1;
                while (target > s && (Pos)run[target].Position == Pos.Smvd) target--;
                if (IsHalant(run[target]))
                    for (var i = basePos + 1; i < target; i++)
                        if ((Cat)run[i].Category == Cat.Matra)
                        {
                            target--;
                            break;
                        }
            }
            if (target > s)
            {
                Move(run, s, target);
                if (s < basePos && basePos <= target) basePos--;
            }
        }

        // the pre-base reordering consonant formed by pref
        if (c.Pref.Length > 0 && basePos + 1 < e)
            for (var i = basePos + 1; i < e; i++)
            {
                if ((run[i].Mask & MaskPref) == 0) continue;
                if (LigatedOnly(run[i]))
                {
                    var target = basePos;
                    if (!noHalfForms)
                        while (target > s && (Cat)run[target - 1].Category is not (Cat.Matra or Cat.Halant)) target--;
                    if (target > s && IsHalant(run[target - 1]) && target < e && IsJoiner(run[target])) target++;
                    Move(run, i, target);
                    if (target <= basePos && basePos < i) basePos++;
                }
                break;
            }

        // init at a pre-base matra at the start of a word
        if ((Pos)run[s].Position == Pos.PreMatra && !AfterLetter(run, s)) run[s] = run[s] with { Mask = (ushort)(run[s].Mask | MaskInit) };
    }

    /// <summary>Whether the glyph before <paramref name="i"/> stands for a letter or a mark (the glyph at <paramref name="i"/> does not start a word).</summary>
    private static bool AfterLetter(List<ShapedGlyph> run, int i)
    {
        for (var k = i - 1; k >= 0; k--)
        {
            if (string.IsNullOrEmpty(run[k].Text)) continue;
            var t = run[k].Text;
            var r = TextRunes.Last(t);
            return Rune.IsLetter(r) || Rune.GetUnicodeCategory(r) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark;
        }
        return false;
    }
}
