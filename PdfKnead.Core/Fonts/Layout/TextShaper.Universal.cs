using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace PdfKnead.Core.Fonts.Layout;

/// <summary>
/// The universal shaping model (docs/FEHLERGEDAECHTNIS.md, No. 1181) for the Brahmi-derived and related scripts that have no shaping of their own:
/// the scripts with syllabic classes in IndicSyllabicCategory.txt outside the Indic scripts, Sinhala, Khmer, Myanmar, Thai, Lao and Tibetan —
/// Balinese, Javanese, Sundanese, Tai Tham, Cham, Batak, Buginese, Chakma, Grantha, Newa, Kawi and some forty more — and Miao and the Khitan Small
/// Script, which have no entries there. Each character gets a class of the model from its Indic_Syllabic_Category, its
/// Indic_Positional_Category and its general category; the run is cut into clusters by the grammar of the model (standard clusters, clusters
/// ending in a stacker, killer or sakot, number and symbol clusters; signs without a base are a broken cluster with a dotted circle as base);
/// the features apply in the stages of the model (locl, ccmp, nukt, akhn; rphf; pref; rkrf, abvf, blwf, half, pstf, vatu, cjct; isol, init,
/// medi, fina; abvs, blws, haln, pres, psts with the other substitutions; abvm, blwm, dist in GPOS); between them a formed repha moves towards
/// the end of its cluster and pre-base vowels, vowel modifiers and consonants formed by pref move before the base. Written from the OpenType
/// feature registry (pages 64–68), the Unicode Character Database 16.0.0 and the published description of the universal shaping model; no
/// code or data of another shaper was used. The results were compared with the bundled shaping library.
/// </summary>
public sealed partial class TextShaper
{
    /// <summary>
    /// The script tags shaped by the universal model: the scripts with syllabic classes in IndicSyllabicCategory.txt 16.0.0 that have no shaping
    /// of their own, and Miao and the Khitan Small Script. New Tai Lue (all its letters and signs are written in visual order and spacing, it has
    /// nothing to reorder) stays with the default shaping, as with the reference shaper. A run with a joining letter (Phags-pa) takes the joining
    /// forms of the cursive scripts instead.
    /// </summary>
    private static readonly FrozenSet<string> UniversalScripts = FrozenSet.ToFrozenSet(
    [
        "ahom", "bali", "batk", "bhks", "brah", "bugi", "buhd", "cakm", "cham", "diak", "dogr", "gong", "gonm", "gran", "gukh", "hano", "java",
        "kali", "kawi", "khar", "khoj", "krai", "kthi", "lana", "lepc", "limb", "mahj", "maka", "marc", "modi", "mtei", "mult", "nand", "newa",
        "phag", "rjng", "saur", "shrd", "sidd", "sind", "soyo", "sund", "sylo", "tagb", "takr", "tale", "tavt", "tglg", "tirh", "tutg", "zanb",
        "plrd", "kits",
    ], StringComparer.Ordinal);

    /// <summary>
    /// Whether the universal model shapes a run of one of its scripts in this font: unless the font's GSUB has no table for the script and
    /// offers its features under DFLT or latn (before dflt) instead — a font made for the default shaping keeps it, as with the reference shaper
    /// (a font without GSUB, or whose GSUB has the script or neither of those, takes the model).
    /// </summary>
    private bool UniversalApplies(string[] scripts)
    {
        if (_gsub is null || _gsub.ScriptTag(scripts) is not null) return true;
        var fallback = _gsub.ScriptTag(["DFLT"]) ?? _gsub.ScriptTag(["dflt"]) ?? _gsub.ScriptTag(["latn"]);
        return fallback is not ("DFLT" or "latn");
    }

    /// <summary>The classes of the universal model (<see cref="ShapedGlyph.Category"/> in its runs).</summary>
    private enum Use : byte
    {
        O, B, N, GB, Ignored, Sub, H, HN, Zwnj, R, CS, IS, Sk, RK,
        CMAbv, CMBlw, FAbv, FBlw, FPst, FMAbv, FMBlw, FMPst, MPre, MAbv, MBlw, MPst, VPre, VAbv, VBlw, VPst, VMPre, VMAbv, VMBlw, VMPst, SMAbv, SMBlw,
        End,
    }

    /// <summary>The kinds of clusters of the universal model (<see cref="ShapedGlyph.Position"/> in its runs).</summary>
    private enum UseCluster : byte
    {
        None,
        Virama,
        Sakot,
        Standard,
        NumberJoiner,
        Numeral,
        Symbol,
        Broken,
        NonCluster,
    }

    /// <summary>
    /// The class of a character in the universal model. Letters (general category Lo) of a dependent class (final, medial and subjoined
    /// consonants, vowels, vowel signs, bindu, avagraha) are bases — the vowels written before their consonant in visual order (Tai Viet), the
    /// spacing finals of Cham and Meetei Mayek; the vowel signs of Kirat Rai, letters too, stay vowel signs, which follow a consonant or the vowel
    /// carrier (The Unicode Standard, 13.23). ZWJ, CGJ and the variation selectors are passed over by the grammar; Tai Tham SAKOT and the
    /// Balinese musical marks have classes of their own; Miao and the Khitan Small Script are classed by their blocks.
    /// </summary>
    private static Use UniversalCategory(int cp)
    {
        switch (cp)
        {
            case 0x25CC:
                return Use.GB;
            case 0x1A60:
                return Use.Sk;
            case 0x200D or 0x034F or >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF or >= 0x180B and <= 0x180D or 0x180F:
                return Use.Ignored;
            case >= 0x1B6B and <= 0x1B73:
                return SyllableData.PositionalOf(cp) == PositionalCategory.Bottom ? Use.SMBlw : Use.SMAbv;
            // Chakma maayyaa, the visible killer, stands before the vowel signs (doubled consonants: maayyaa and a vowel sign; The Unicode
            // Standard, 13.13) — as the reference shaper classes it
            case 0x11134:
                return Use.CMAbv;
            // the Kharoshthi double ring below follows the vowel signs like a vowel modifier, as the reference shaper has it
            case 0x10A0D:
                return Use.VMBlw;
            // Miao: letters and the nasalization letter are bases, the consonant modifier bar goes with its consonant, the vowel signs follow on
            // the right, the tone marks below; the tone letters are signs of their own
            case >= 0x16F00 and <= 0x16F4A or 0x16F50:
                return Use.B;
            case 0x16F4F:
                return Use.CMBlw;
            case >= 0x16F51 and <= 0x16F87:
                return Use.VPst;
            case >= 0x16F8F and <= 0x16F92:
                return Use.VMBlw;
            case >= 0x16F93 and <= 0x16F9F:
                return Use.O;
            // Khitan Small Script: every character (and the filler) a cluster of its own; the font assembles them
            case >= 0x18B00 and <= 0x18CFF or 0x16FE4:
                return Use.B;
        }
        var syllabic = SyllableData.SyllabicOf(cp);
        var position = UniversalPosition(cp);
        var letter = Rune.IsValid(cp) && Rune.GetUnicodeCategory(new Rune(cp)) == UnicodeCategory.OtherLetter && cp is not (>= 0x16D63 and <= 0x16D6A);
        switch (syllabic)
        {
            case SyllabicCategory.Avagraha or SyllabicCategory.Bindu or SyllabicCategory.ConsonantFinal or SyllabicCategory.ConsonantMedial
                or SyllabicCategory.ConsonantSubjoined or SyllabicCategory.Vowel or SyllabicCategory.VowelDependent when letter:
                return Use.B;
        }
        return syllabic switch
        {
            SyllabicCategory.Number or SyllabicCategory.Consonant or SyllabicCategory.ConsonantDead or SyllabicCategory.ConsonantHeadLetter
                or SyllabicCategory.ToneLetter or SyllabicCategory.VowelIndependent => Use.B,
            SyllabicCategory.ConsonantPlaceholder => Use.GB,
            SyllabicCategory.BrahmiJoiningNumber => Use.N,
            SyllabicCategory.NumberJoiner => Use.HN,
            SyllabicCategory.ConsonantPrecedingRepha or SyllabicCategory.ConsonantPrefixed => Use.R,
            SyllabicCategory.ConsonantWithStacker => Use.CS,
            SyllabicCategory.InvisibleStacker => Use.IS,
            SyllabicCategory.ReorderingKiller => Use.RK,
            SyllabicCategory.Virama => Use.H,
            SyllabicCategory.NonJoiner => Use.Zwnj,
            SyllabicCategory.Joiner => Use.Ignored,
            SyllabicCategory.ConsonantSubjoined => Use.Sub,
            SyllabicCategory.ConsonantFinal or SyllabicCategory.ConsonantSucceedingRepha => position switch
            {
                PositionalCategory.Top => Use.FAbv,
                PositionalCategory.Bottom => Use.FBlw,
                _ => Use.FPst,
            },
            SyllabicCategory.SyllableModifier => position switch
            {
                PositionalCategory.Top => Use.FMAbv,
                PositionalCategory.Bottom => Use.FMBlw,
                _ => Use.FMPst,
            },
            SyllabicCategory.ConsonantMedial or SyllabicCategory.ConsonantInitialPostfixed => position switch
            {
                PositionalCategory.Left or PositionalCategory.TopAndBottomAndLeft => Use.MPre,
                PositionalCategory.Bottom or PositionalCategory.BottomAndLeft => Use.MBlw,
                PositionalCategory.Right or PositionalCategory.BottomAndRight => Use.MPst,
                _ => Use.MAbv,
            },
            SyllabicCategory.Nukta or SyllabicCategory.GeminationMark or SyllabicCategory.ConsonantKiller => position is PositionalCategory.Bottom
                or PositionalCategory.Overstruck ? Use.CMBlw : Use.CMAbv,
            SyllabicCategory.Vowel or SyllabicCategory.VowelDependent or SyllabicCategory.PureKiller => position switch
            {
                PositionalCategory.Left or PositionalCategory.TopAndLeft or PositionalCategory.TopAndLeftAndRight or PositionalCategory.LeftAndRight
                    or PositionalCategory.BottomAndLeft or PositionalCategory.TopAndBottomAndLeft or PositionalCategory.VisualOrderLeft => Use.VPre,
                PositionalCategory.Bottom or PositionalCategory.Overstruck or PositionalCategory.BottomAndRight => Use.VBlw,
                PositionalCategory.Right => Use.VPst,
                _ => Use.VAbv,
            },
            SyllabicCategory.ToneMark or SyllabicCategory.CantillationMark or SyllabicCategory.RegisterShifter or SyllabicCategory.Visarga
                or SyllabicCategory.Bindu => position switch
            {
                PositionalCategory.Left => Use.VMPre,
                PositionalCategory.Bottom or PositionalCategory.Overstruck => Use.VMBlw,
                PositionalCategory.Right => Use.VMPst,
                _ => Use.VMAbv,
            },
            _ => Use.O,
        };
    }

    /// <summary>
    /// The Indic_Positional_Category of a character as the universal model places it: as the database gives it, except for a few vowel signs the
    /// reference shaper orders otherwise (measured with fonts built for it): Cham U+AA29 AA and the Kharoshthi vowel length mark U+10A0C after
    /// the other vowel signs, Buginese U+1A18 U, the
    /// Grantha anusvara and visarga U+11302, U+11303 and the Tirhuta visarga U+114C1 with the signs above, the Kharoshthi bar above U+10A38
    /// with the signs below, and the Chakma vowel signs below the base (U, UU, the O and AU marks) before those above it.
    /// </summary>
    private static PositionalCategory UniversalPosition(int cp)
    {
        var position = SyllableData.PositionalOf(cp);
        return cp switch
        {
            0xAA29 or 0x10A0C => PositionalCategory.Right,
            0x1A18 or 0x11302 or 0x11303 or 0x114C1 => PositionalCategory.Top,
            0x10A38 => PositionalCategory.Bottom,
            >= 0x11127 and <= 0x11132 => position switch
            {
                PositionalCategory.Top => PositionalCategory.Bottom,
                PositionalCategory.Bottom => PositionalCategory.Top,
                _ => position,
            },
            _ => position,
        };
    }

    /// <summary>
    /// The marks of a run of the universal model in canonical order (<see cref="CanonicalMarkOrder"/>), as the reference shaper orders them; Tai
    /// Tham SAKOT U+1A60 keeps its place (the marks around it are ordered on either side of it, as the reference shaper keeps it after them).
    /// </summary>
    private static void UniversalCanonicalOrder(List<ShapedGlyph> run, List<int> codePoints, Work work)
    {
        for (var i = 0; i < codePoints.Count;)
        {
            bool Mark(int k) => (MarkInfo(codePoints[k]) & NonStarter) != 0 && codePoints[k] != 0x1A60;
            if (!Mark(i))
            {
                i++;
                continue;
            }
            var start = i;
            while (i < codePoints.Count && Mark(i)) i++;
            var count = i - start;
            if (count is < 2 or > MaxCanonicalMarks || !work.Spend(count) || CanonicalMarkOrder(codePoints, start, count) is not { } order) continue;
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
    /// Prepares a run of the universal model before any substitution: characters the font lacks are taken apart canonically into parts it has,
    /// parts it lacks are put together (<see cref="NormalizeUniversal"/>); the marks in canonical order; then the classes, the clusters (dotted
    /// circles before broken ones), the reach of rphf and the joining forms of the clusters.
    /// </summary>
    private void PrepareUniversal(List<ShapedGlyph> run, List<int> codePoints, Work work)
    {
        NormalizeUniversal(run, codePoints, work);
        UniversalCanonicalOrder(run, codePoints, work);
        for (var i = 0; i < run.Count; i++) run[i] = run[i] with { Category = (byte)UniversalCategory(codePoints[i]) };
        FindUniversalClusters(run, codePoints, work);
        foreach (var (start, end) in SyllableRanges(run))
        {
            var limit = (Use)run[start].Category == Use.R ? 1 : Math.Min(3, end - start);
            for (var k = start; k < start + limit; k++) run[k] = run[k] with { Mask = (ushort)(run[k].Mask | MaskRphf) };
        }
    }

    /// <summary>
    /// The canonical forms the font can draw: a character with a canonical decomposition whose parts the font all has is taken apart (the first
    /// part keeps the text) — also where the font has the character itself, as the reference shaper does in this model (measured: Balinese,
    /// Kaithi, Chakma, Grantha, Tirhuta, Siddham, Dives Akuru vowels and consonants with nukta); then two characters that compose canonically
    /// are put together where the font has the composed character and either a mark follows a letter in a run that has marks (as the
    /// reference shaper composes again) or the font lacks one of the two. So both spellings of a vowel look the same whichever the font has —
    /// the Gurung Khema and Kirat Rai vowels in two or three parts among them (The Unicode Standard, 13.22, 13.23).
    /// </summary>
    private void NormalizeUniversal(List<ShapedGlyph> run, List<int> codePoints, Work work)
    {
        // the reference shaper composes again only in a text with a mark after its first character
        var marks = codePoints.Skip(1).Any(IsMark);
        for (var i = 0; i < codePoints.Count && run.Count < MaxGlyphs; i++)
        {
            var c = codePoints[i];
            if (c < 0x80 || !Rune.IsValid(c)) continue;
            var points = CanonicalDecomposition(c);
            if (points.Length < 2 || points.Any(p => _font.GlyphForUnicode(p) <= 0) || !work.Move(run.Count - i)) continue;
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
        for (var i = 0; i + 1 < codePoints.Count; i++)
        {
            int a = codePoints[i], b = codePoints[i + 1];
            if (a < 0x80 || b < 0x80 || !Rune.IsValid(a) || !Rune.IsValid(b)) continue;
            if (CanonicalComposition(a, b) is not { } composite || _font.GlyphForUnicode(composite) is var glyph && glyph <= 0) continue;
            var runes = new[] { new Rune(composite) };
            var complete = _font.GlyphForUnicode(a) > 0 && _font.GlyphForUnicode(b) > 0;
            if (complete && !(marks && !IsMark(a) && IsMark(b)) || !work.Move(run.Count - i)) continue;
            run[i] = run[i] with
            {
                Glyph = glyph, Text = run[i].Text + run[i + 1].Text,
                Class = Rune.GetUnicodeCategory(runes[0]) == UnicodeCategory.NonSpacingMark ? (byte)3 : (byte)1,
            };
            run.RemoveAt(i + 1);
            codePoints[i] = runes[0].Value;
            codePoints.RemoveAt(i + 1);
            i--;
        }
    }

    /// <summary>
    /// The canonical decompositions of UnicodeData.txt 16.0.0 that the normalization of the runtime or of the system does not know (measured
    /// 27.11.2026: .NET 10 lacks the 20 of Todhri, Tulu-Tigalari, Gurung Khema and Kirat Rai added in Unicode 16, the Windows normalization
    /// also Dives Akuru U+11938; generated by tools/schriftdaten/unicode16_zerlegungen.py; docs/FEHLERGEDAECHTNIS.md, No. 1181 and 1188).
    /// </summary>
    internal static readonly FrozenDictionary<int, (int First, int Second)> Decompositions16 = new Dictionary<int, (int, int)>
    {
        [0x105C9] = (0x105D2, 0x307), [0x105E4] = (0x105DA, 0x307), [0x11383] = (0x11382, 0x113C9), [0x11385] = (0x11384, 0x113BB),
        [0x1138E] = (0x1138B, 0x113C2), [0x11391] = (0x11390, 0x113C9), [0x113C5] = (0x113C2, 0x113C2), [0x113C7] = (0x113C2, 0x113B8),
        [0x113C8] = (0x113C2, 0x113C9), [0x11938] = (0x11935, 0x11930), [0x16121] = (0x1611E, 0x1611E), [0x16122] = (0x1611E, 0x16129),
        [0x16123] = (0x1611E, 0x1611F), [0x16124] = (0x16129, 0x1611F), [0x16125] = (0x1611E, 0x16120), [0x16126] = (0x16121, 0x1611F),
        [0x16127] = (0x16122, 0x1611F), [0x16128] = (0x16121, 0x16120), [0x16D68] = (0x16D67, 0x16D67), [0x16D69] = (0x16D63, 0x16D67),
        [0x16D6A] = (0x16D69, 0x16D67),
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<(int First, int Second), int> Compositions16 =
        Decompositions16.ToFrozenDictionary(e => e.Value, e => e.Key);

    /// <summary>The full canonical decomposition of a character (one element: none) — from the runtime's normalization, else from <see cref="Decompositions16"/>.</summary>
    internal static int[] CanonicalDecomposition(int cp)
    {
        if (Decompositions16.TryGetValue(cp, out var pair))
            return [.. CanonicalDecomposition(pair.First), .. CanonicalDecomposition(pair.Second)];
        if (!Rune.IsValid(cp)) return [cp];
        var single = char.ConvertFromUtf32(cp);
        try
        {
            var parts = single.Normalize(NormalizationForm.FormD);
            return parts == single || parts.Length > 8 ? [cp] : [.. parts.EnumerateRunes().Select(r => r.Value)];
        }
        catch (ArgumentException)
        {
            return [cp];
        }
    }

    /// <summary>The canonical composite of two characters (null: none) — from the runtime's normalization, else from <see cref="Decompositions16"/>.</summary>
    internal static int? CanonicalComposition(int a, int b)
    {
        if (Compositions16.TryGetValue((a, b), out var composite)) return composite;
        if (!Rune.IsValid(a) || !Rune.IsValid(b)) return null;
        try
        {
            var runes = (char.ConvertFromUtf32(a) + char.ConvertFromUtf32(b)).Normalize(NormalizationForm.FormC).EnumerateRunes().ToArray();
            return runes.Length == 1 ? runes[0].Value : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsMark(int cp) => Rune.IsValid(cp)
        && Rune.GetUnicodeCategory(new Rune(cp)) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    /// <summary>
    /// Cuts the run into the clusters of the universal model, passing over ZWJ, CGJ and variation selectors (and a ZWNJ before a mark) — they
    /// belong to the cluster before them —, gives each its number and its kind, and puts a dotted circle (U+25CC, where the font has it; no text
    /// of its own) before a broken cluster, after a repha at its start. A cluster longer than <see cref="MaxSyllable"/> glyphs is cut.
    /// </summary>
    private void FindUniversalClusters(List<ShapedGlyph> run, List<int> codePoints, Work work)
    {
        // the glyphs the grammar sees
        var seen = new List<int>(run.Count);
        for (var i = 0; i < run.Count; i++)
        {
            var cat = (Use)run[i].Category;
            if (cat == Use.Ignored) continue;
            if (cat == Use.Zwnj)
            {
                var next = i + 1;
                while (next < run.Count && (Use)run[next].Category == Use.Ignored) next++;
                if (next < run.Count && IsMark(codePoints[next])) continue;
            }
            seen.Add(i);
        }
        var classes = seen.Select(k => (Use)run[k].Category).ToArray();
        var dotted = _font.GlyphForUnicode(0x25CC);
        var glyphs = new List<ShapedGlyph>(run.Count + 4);
        var points = new List<int>(run.Count + 4);
        ushort id = 0;
        var from = 0;
        // glyphs the grammar passes over at the start of the run form a group of their own (they belong to no cluster)
        if (seen.Count > 0 && seen[0] > 0)
        {
            id = 1;
            for (var k = 0; k < seen[0]; k++)
            {
                glyphs.Add(run[k] with { Syllable = id, Position = (byte)UseCluster.NonCluster });
                points.Add(codePoints[k]);
            }
            from = seen[0];
        }
        for (var p = 0; from < run.Count;)
        {
            var (length, kind) = p < classes.Length && work.Spend(1) ? ParseUniversal(classes, p) : (1, UseCluster.NonCluster);
            var end = p + length >= seen.Count ? run.Count : seen[p + length];
            if (p >= seen.Count) end = run.Count;
            if (end - from > MaxSyllable) end = from + MaxSyllable;
            while (p < seen.Count && seen[p] < end) p++;
            id = id == ushort.MaxValue ? (ushort)1 : (ushort)(id + 1);
            var circle = kind == UseCluster.Broken && dotted > 0 && run.Count + glyphs.Count - from < MaxGlyphs;
            for (var k = from; k < end; k++)
            {
                if (circle && (Use)run[k].Category != Use.R)
                {
                    glyphs.Add(new ShapedGlyph
                    {
                        Glyph = dotted, Cluster = run[k].Cluster, Text = "", Class = 1, Gates = run[k].Gates, Syllable = id,
                        Category = (byte)Use.B, Position = (byte)kind,
                    });
                    points.Add(0x25CC);
                    circle = false;
                }
                glyphs.Add(run[k] with { Syllable = id, Position = (byte)kind });
                points.Add(codePoints[k]);
            }
            // a repha alone: the dotted circle after it
            if (circle)
            {
                glyphs.Add(new ShapedGlyph
                {
                    Glyph = dotted, Cluster = run[end - 1].Cluster, Text = "", Class = 1, Gates = run[end - 1].Gates, Syllable = id,
                    Category = (byte)Use.B, Position = (byte)kind,
                });
                points.Add(0x25CC);
            }
            from = end;
        }
        run.Clear();
        run.AddRange(glyphs);
        codePoints.Clear();
        codePoints.AddRange(points);
    }

    /// <summary>
    /// The longest cluster of the universal model at <paramref name="i"/> of the classes the grammar sees, and its kind; of equally long ones the
    /// first in the order: ending in a stacker or killer, ending in sakot, standard, number joiner, numeral, symbol, a final modifier alone,
    /// broken, any other character alone. A standard cluster: an optional repha or consonant with stacker, a base, consonant modifiers (with
    /// further bases after a halant or stacker, subjoined consonants), medial consonants (pre, above, below, post — at most one each), vowel
    /// signs (pre, above, below, post — or a halant), vowel modifiers, further bases after sakot, final consonants and final modifiers; each
    /// cluster may end with a ZWNJ.
    /// </summary>
    private static (int Length, UseCluster Kind) ParseUniversal(Use[] classes, int i)
    {
        var n = classes.Length;
        Use C(int k) => k < n ? classes[k] : Use.End;
        int Star(int k, Use a, Use b = Use.End)
        {
            while (C(k) == a || C(k) == b && b != Use.End) k++;
            return k;
        }
        int Opt(int k, Use a) => C(k) == a ? k + 1 : k;
        bool IsH(int k) => C(k) is Use.H or Use.IS or Use.Sk;

        int ConsonantModifiers(int k)
        {
            k = Star(Star(k, Use.CMAbv), Use.CMBlw);
            while (true)
            {
                if (IsH(k) && C(k + 1) == Use.B) k += 2;
                else if (C(k) == Use.Sub) k++;
                else break;
                k = Star(Star(k, Use.CMAbv), Use.CMBlw);
            }
            return k;
        }
        int Medials(int k) => Opt(Opt(Opt(Opt(k, Use.MPre), Use.MAbv), Use.MBlw), Use.MPst);
        int Vowels(int k)
        {
            var v = Star(Star(Star(Star(k, Use.VPre), Use.VAbv), Use.VBlw), Use.VPst);
            return v == k && C(k) == Use.H ? k + 1 : v;
        }
        int VowelModifiers(int k) => Star(Star(Star(Star(k, Use.VMPre), Use.VMAbv), Use.VMBlw), Use.VMPst);
        int SakotBases(int k)
        {
            while (C(k) == Use.Sk && C(k + 1) == Use.B) k += 2;
            return k;
        }
        int Middle(int k) => SakotBases(VowelModifiers(Vowels(Medials(ConsonantModifiers(k)))));
        int Tail(int k)
        {
            k = Star(Star(Star(Middle(k), Use.FAbv), Use.FBlw), Use.FPst);
            return C(k) is Use.FMAbv or Use.FMBlw ? Star(Star(k, Use.FMAbv), Use.FMBlw) : Opt(k, Use.FMPst);
        }
        int Start(int k)
        {
            if (C(k) is Use.R or Use.CS && C(k + 1) is Use.B or Use.GB) return k + 2;
            // a character of no class of its own (a space, punctuation, a symbol) carries the signs after it like a base, as with the reference
            // shaper — no dotted circle after it
            return C(k) is Use.B or Use.GB or Use.O ? k + 1 : -1;
        }
        int ViramaTail(int k)
        {
            k = ConsonantModifiers(k);
            return C(k) is Use.IS or Use.RK ? k + 1 : -1;
        }
        int SakotTail(int k)
        {
            k = Middle(k);
            return C(k) == Use.Sk ? k + 1 : -1;
        }
        int Pairs(int k)
        {
            while (C(k) == Use.HN && C(k + 1) == Use.N) k += 2;
            return k;
        }
        int NumberJoinerTail(int k)
        {
            var p = Pairs(k);
            if (C(p) == Use.HN) return p + 1;
            return p > k ? p - 1 : -1;
        }
        int NumeralTail(int k) => Pairs(k) is var p && p > k ? p : -1;
        int SymbolTail(int k) => C(k) == Use.SMAbv ? Star(Star(k, Use.SMAbv), Use.SMBlw) : C(k) == Use.SMBlw ? Star(k, Use.SMBlw) : -1;
        int Then(int k, Func<int, int> next) => k < 0 ? -1 : next(k);

        var best = (End: -1, Kind: UseCluster.NonCluster);
        void Take(int end, UseCluster kind, bool zwnj = true)
        {
            if (end < 0) return;
            if (zwnj && C(end) == Use.Zwnj) end++;
            if (end > best.End && end > i) best = (end, kind);
        }
        var start = Start(i);
        Take(Then(start, ViramaTail), UseCluster.Virama);
        Take(Then(start, SakotTail), UseCluster.Sakot);
        Take(Then(start, Tail), UseCluster.Standard);
        if (C(i) == Use.N)
        {
            Take(NumberJoinerTail(i + 1), UseCluster.NumberJoiner);
            Take(Math.Max(i + 1, NumeralTail(i + 1)), UseCluster.Numeral);
        }
        if (C(i) is Use.O or Use.GB) Take(Math.Max(i + 1, SymbolTail(i + 1)), UseCluster.Symbol);
        if (C(i) == Use.FMPst) Take(i + 1, UseCluster.NonCluster, zwnj: false);
        var b = C(i) == Use.R ? i + 1 : i;
        Take(new[] { Tail(b), NumberJoinerTail(b), NumeralTail(b), SymbolTail(b), ViramaTail(b), SakotTail(b) }.Max(), UseCluster.Broken);
        Take(i + 1, UseCluster.NonCluster, zwnj: false);
        return (best.End - i, best.Kind);
    }

    /// <summary>The features of the orthographic unit shaping, applied together in each cluster.</summary>
    private static readonly string[] UniversalBasic = ["rkrf", "abvf", "blwf", "half", "pstf", "vatu", "cjct"];

    /// <summary>The features of the typographic presentation, applied with the other substitutions.</summary>
    private static readonly (string Feature, ushort Mask)[] UniversalPresentation = [("abvs", 0), ("blws", 0), ("haln", 0), ("pres", 0), ("psts", 0)];

    /// <summary>
    /// The substitutions of a run of the universal model: locl, ccmp, nukt and akhn in each cluster (with the features of the direction); rphf at
    /// the start of each cluster — the glyph it forms becomes a repha —; pref in each cluster — the glyph it forms is reordered like a pre-base
    /// vowel —; rkrf, abvf, blwf, half, pstf, vatu and cjct together in each cluster; the reordering; isol, init, medi and fina by the place of
    /// the cluster in its word; then abvs, blws, haln, pres and psts with the other substitutions over the whole run.
    /// </summary>
    private void SubstituteUniversal(List<ShapedGlyph> run, string[] scripts, RunOptions options, string[] direction, HashSet<int>? italic, Work work)
    {
        if (_gsub is not null)
        {
            var first = FirstStage(scripts, options, "nukt", "akhn");
            foreach (var (lookup, gate) in Gated(_gsub, direction, scripts, options.Language, false)) Merge(first, lookup, new StageLookup(0, false, gate));
            ApplyStage(run, first, italic, work);

            ClearSubstituted(run);
            var rphf = new SortedDictionary<int, StageLookup>();
            AddStage(rphf, [("rphf", MaskRphf)], scripts, options, perSyllable: true);
            ApplyStage(run, rphf, italic, work);
            // a glyph rphf formed at the start of a cluster is a repha
            foreach (var (start, end) in SyllableRanges(run))
                for (var k = start; k < end && (run[k].Mask & MaskRphf) != 0; k++)
                    if ((run[k].Formed & FormedSubstituted) != 0)
                    {
                        run[k] = run[k] with { Category = (byte)Use.R };
                        break;
                    }

            ClearSubstituted(run);
            var pref = new SortedDictionary<int, StageLookup>();
            AddStage(pref, [("pref", 0)], scripts, options, perSyllable: true);
            ApplyStage(run, pref, italic, work);
            // the glyph pref formed is reordered like a pre-base vowel sign
            foreach (var (start, end) in SyllableRanges(run))
                for (var k = start; k < end; k++)
                    if ((run[k].Formed & FormedSubstituted) != 0)
                    {
                        run[k] = run[k] with { Category = (byte)Use.VPre };
                        break;
                    }

            var basic = new SortedDictionary<int, StageLookup>();
            AddStage(basic, UniversalBasic.Select(f => (f, (ushort)0)), scripts, options, perSyllable: true);
            ApplyStage(run, basic, italic, work);
        }

        foreach (var (start, end) in SyllableRanges(run).ToList())
            if (work.Spend(end - start)) ReorderUniversal(run, start, end);

        if (_gsub is null) return;
        TopographicalForms(run, scripts, options, work);
        ApplyStage(run, LastStage(UniversalPresentation, false, scripts, options, []), italic, work);
    }

    /// <summary>Clears the mark of a substitution made (before rphf and pref, whose results are recorded).</summary>
    private static void ClearSubstituted(List<ShapedGlyph> run)
    {
        for (var i = 0; i < run.Count; i++)
            if ((run[i].Formed & FormedSubstituted) != 0) run[i] = run[i] with { Formed = (byte)(run[i].Formed & ~FormedSubstituted) };
    }

    /// <summary>A halant or stacker that no ligature took.</summary>
    private static bool IsUniversalHalant(ShapedGlyph g) => (Use)g.Category is Use.H or Use.IS && (g.Formed & FormedLigated) == 0;

    /// <summary>The classes that stand after the base: final consonants and modifiers, medials, vowel signs and vowel modifiers.</summary>
    private static bool IsPostBase(Use c) => c is Use.FAbv or Use.FBlw or Use.FPst or Use.FMAbv or Use.FMBlw or Use.FMPst or Use.MAbv or Use.MBlw
        or Use.MPst or Use.MPre or Use.VAbv or Use.VBlw or Use.VPst or Use.VPre or Use.VMAbv or Use.VMBlw or Use.VMPst or Use.VMPre;

    /// <summary>
    /// The reordering of a cluster of the universal model (standard, broken and those ending in a stacker, killer or sakot): a repha at its start
    /// moves towards the end — before the first glyph after the base (a vowel sign, medial, final, modifier or halant), else to the end —; then
    /// each pre-base vowel sign or vowel modifier (and the glyph pref formed; of glyphs a multiple substitution made only the first) moves to the
    /// front, behind the last halant or stacker before it that no ligature took. Pre-base medial consonants (Tai Tham U+1A55, Cham U+AA34,
    /// Gurung Khema U+1612A) stay — the model leaves them to the font, as the reference shaper does.
    /// </summary>
    private static void ReorderUniversal(List<ShapedGlyph> run, int s, int e)
    {
        if ((UseCluster)run[s].Position is not (UseCluster.Virama or UseCluster.Sakot or UseCluster.Standard or UseCluster.Broken)) return;
        if ((Use)run[s].Category == Use.R && e - s > 1)
            for (var i = s + 1; i < e; i++)
            {
                var post = IsPostBase((Use)run[i].Category) || IsUniversalHalant(run[i]);
                if (!post && i != e - 1) continue;
                Move(run, s, post ? i - 1 : i);
                break;
            }
        var j = s;
        for (var i = s; i < e; i++)
        {
            if (IsUniversalHalant(run[i])) j = i + 1;
            else if ((Use)run[i].Category is Use.VPre or Use.VMPre && j < i && !LaterOfMultiple(run, i, s)) Move(run, i, j);
        }
    }

    /// <summary>Whether a glyph is a second or later glyph of those one multiple substitution made (it has no text, its cluster is the one before).</summary>
    private static bool LaterOfMultiple(List<ShapedGlyph> run, int i, int s) => (run[i].Formed & FormedMultiplied) != 0 && i > s
        && (run[i - 1].Formed & FormedMultiplied) != 0 && run[i - 1].Cluster == run[i].Cluster && string.IsNullOrEmpty(run[i].Text);

    /// <summary>
    /// isol, init, medi and fina of the universal model by the place of each cluster in its word: a cluster after a cluster joins it (the first
    /// becomes init, or medi after another, the last fina; a cluster alone isol); a character that forms no cluster (a final modifier alone) and
    /// the run's edges break the word. The lookups of the four features in LookupList order, each at the glyphs of its form.
    /// </summary>
    private void TopographicalForms(List<ShapedGlyph> run, string[] scripts, RunOptions options, Work work)
    {
        var lookups = new SortedDictionary<int, List<ArabicJoining.Form>>();
        foreach (var (feature, form) in new[] { ("isol", ArabicJoining.Form.Isolated), ("init", ArabicJoining.Form.Initial),
                     ("medi", ArabicJoining.Form.Medial), ("fina", ArabicJoining.Form.Final) })
        {
            if (options.Disabled.Contains(feature)) continue;
            foreach (var lookup in _gsub!.LookupsFor([feature], scripts, options.Language, required: false))
            {
                if (!lookups.TryGetValue(lookup, out var forms)) lookups[lookup] = forms = [];
                forms.Add(form);
            }
        }
        if (lookups.Count == 0) return;
        var saved = run.Select(g => g.Form).ToArray();
        var ranges = SyllableRanges(run).ToList();
        var last = ArabicJoining.Form.None;
        var lastRange = -1;
        for (var r = 0; r < ranges.Count; r++)
        {
            var (start, end) = ranges[r];
            if ((UseCluster)run[start].Position is UseCluster.NonCluster or UseCluster.None)
            {
                last = ArabicJoining.Form.None;
                lastRange = r;
                continue;
            }
            var join = last is ArabicJoining.Form.Final or ArabicJoining.Form.Isolated;
            if (join && lastRange >= 0)
            {
                var previous = last == ArabicJoining.Form.Final ? ArabicJoining.Form.Medial : ArabicJoining.Form.Initial;
                for (var k = ranges[lastRange].Start; k < ranges[lastRange].End; k++) run[k] = run[k] with { Form = previous };
            }
            last = join ? ArabicJoining.Form.Final : ArabicJoining.Form.Isolated;
            for (var k = start; k < end; k++) run[k] = run[k] with { Form = last };
            lastRange = r;
        }
        foreach (var (lookup, forms) in lookups)
            foreach (var form in forms)
                if (work.Left > 0) new Substitution(this, run, work).ApplyLookup(lookup, form);
        // the joining forms of the clusters served only these features
        if (run.Count == saved.Length)
            for (var i = 0; i < run.Count; i++) run[i] = run[i] with { Form = saved[i] };
        else
            for (var i = 0; i < run.Count; i++) run[i] = run[i] with { Form = ArabicJoining.Form.None };
    }
}
