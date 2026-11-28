using System.Runtime.CompilerServices;

namespace PdfKnead.Core.Fonts.Layout;

/// <summary>
/// One shaped glyph: glyph index, origin in the text (index of its first UTF-16 code unit) and position in
/// font units, with advance and offset as in common shaping engines.
/// </summary>
public struct ShapedGlyph
{
    /// <summary>Glyph index in the font.</summary>
    public int Glyph;

    /// <summary>Index of the first UTF-16 code unit of the text this glyph comes from.</summary>
    public int Cluster;

    /// <summary>Horizontal advance in font units.</summary>
    public double XAdvance;

    /// <summary>Vertical advance in font units.</summary>
    public double YAdvance;

    /// <summary>Horizontal offset from the pen position in font units.</summary>
    public double XOffset;

    /// <summary>Vertical offset from the pen position in font units.</summary>
    public double YOffset;

    /// <summary>Text the glyph stands for (several characters for ligatures, empty for further glyphs of a multiple substitution), for ToUnicode.</summary>
    public string Text;

    /// <summary>
    /// For a mark attached by GPOS (to a base, a ligature or a mark): how many glyphs back its base stands (0: none) — a line set right to left
    /// places the mark at its base again (docs/FEHLERGEDAECHTNIS.md, No. 910).
    /// </summary>
    internal int AttachBack;

    /// <summary>The joining form of the character in Arabic script (<see cref="ArabicJoining"/>): it selects isol, init, medi or fina.</summary>
    internal ArabicJoining.Form Form;

    /// <summary>
    /// The glyph class the shaper derives when the font has no GlyphClassDef (OpenType 18.1): 3 (mark) for a character of the general category
    /// Mn, 2 for a ligature formed by GSUB, else 1 (base). Not used when the font has the classes.
    /// </summary>
    internal byte Class;

    /// <summary>
    /// Ligature bookkeeping for mark-to-ligature attachment (OpenType 20.4.6): a ligature glyph carries its own id here, a mark the id of the
    /// ligature it belongs to (0: none).
    /// </summary>
    internal int LigatureId;

    /// <summary>For a mark: the component (counted from 1) of ligature <see cref="LigatureId"/> it belongs to; 0 for none.</summary>
    internal int LigatureComponent;

    /// <summary>For a ligature glyph formed by GSUB: the number of its components (0 for other glyphs).</summary>
    internal int LigatureComponents;

    /// <summary>
    /// The features of restricted reach that apply at this glyph (bits of <see cref="TextShaper"/>: kern, cpsp, rtlm): kern everywhere except at
    /// glyphs palt covers, cpsp only in words of capitals, rtlm only at mirrored characters (docs/FEHLERGEDAECHTNIS.md, No. 1174).
    /// </summary>
    internal byte Gates;

    /// <summary>Whether a lookup of the feature ital replaced the glyph (an italic form from the font: the caller does not slant it as well).</summary>
    internal bool Italic;

    /// <summary>
    /// The features of a syllabic script restricted to some glyphs of a syllable that apply at this glyph (bits of <see cref="TextShaper"/>:
    /// rphf, pref, blwf, abvf, half, pstf, init, cfar; docs/FEHLERGEDAECHTNIS.md, No. 1175).
    /// </summary>
    internal ushort Mask;

    /// <summary>The syllable of a syllabic script the glyph belongs to (neighbouring syllables differ; 0: none).</summary>
    internal ushort Syllable;

    /// <summary>The shaping category of the character in its syllable (<see cref="TextShaper"/>, No. 1175).</summary>
    internal byte Category;

    /// <summary>The place of the glyph in its syllable for the reordering (<see cref="TextShaper"/>, No. 1175).</summary>
    internal byte Position;

    /// <summary>What GSUB did to the glyph (bits: 1 substituted, 2 formed as a ligature, 4 from a multiple substitution; No. 1175).</summary>
    internal byte Formed;

    /// <summary>
    /// Whether the glyph stands for a joiner (<see cref="TextShaper.JoinerZwnj"/> U+200C, <see cref="TextShaper.JoinerZwj"/> U+200D; 0: none):
    /// positioning passes over both as if they were not there, the context of a substitution over ZWJ — the joiners only ask for or prevent
    /// joining and ligatures between their neighbours (docs/FEHLERGEDAECHTNIS.md, No. 1179). Since No. 1183 also the other default ignorable
    /// characters (<see cref="TextShaper.JoinerFormat"/>: WJ, CGJ, ZWSP, U+FEFF, tags …), which ligatures and positioning pass over unless they
    /// name them, the variation selectors (<see cref="TextShaper.JoinerSelector"/>: passed over by ligatures and positioning, not by the context of
    /// a substitution, which may ask for them), and <see cref="TextShaper.JoinerHeld"/> for one within the unit of a prepended concatenation mark;
    /// a default ignorable character the font has no glyph for (and the soft hyphen) is drawn as nothing.
    /// </summary>
    internal byte Joiner;

    /// <summary>
    /// For a mark attached in GPOS: its offsets are still relative to its base (the anchors' difference plus later adjustments of the mark
    /// itself); the base's final place is added after the last GPOS lookup (OpenType 20.4.5, No. 1179).
    /// </summary>
    internal bool RelativeToBase;
}

/// <summary>
/// Text shaper following OpenType. The text is split into runs of one script, each shaped with the lookups of its script tag and of the language
/// system of the text's language (<see cref="UnicodeScripts"/>, No. 1162); cursive scripts — since No. 910 — with their joining forms isol,
/// init, medi, fina applied each where the letter takes that form (<see cref="ArabicJoining"/>; marks on ligatures). Characters → glyphs via
/// the cmap, then GSUB (ccmp, locl, rlig, liga, clig, calt — contextual forms of connected script fonts) and the legacy kern table (when GPOS
/// has no kerning for the run) and GPOS (kern, curs, mark, mkmk, dist, cpsp). Since No. 1174 also rvrn, rclt, the direction features, the
/// Syriac Alaph forms, stch, chws or halt for CJK text, ital on request, and the transient reordering of Arabic marks. Since No. 1175 the
/// syllables of the Indic scripts, Khmer, Myanmar and Tibetan with their reordering and stages, SARA AM of Thai and Lao, and Hangul jamo
/// composed into syllables (<see cref="SubstituteSyllables"/>). Since No. 1179 Sinhala, the Hangul tone marks, the joiners passed over in
/// positioning (and ZWJ in the context of a substitution), attached marks placed after the last GPOS lookup. Since No. 1183 the vowel
/// sequences not to use marked with a dotted circle, default ignorable characters drawn as nothing and passed over by ligatures and
/// positioning, ZWJ asking for a ligature, canonically equivalent spellings in Tibetan, Myanmar, Thai and the Indic scripts drawn alike,
/// Script_Extensions and fractions around U+2044. Works like common shaping engines (the line is set right to left by the caller, No. 910).
/// </summary>
/// <remarks>
/// One shaper is shared per <see cref="OpenTypeFont"/>; <see cref="Shape"/> keeps its state per call and is safe to call from
/// several threads. Not supported: GSUB type 3 outside the stages of the syllabic scripts and rand (there is no choice of alternates; the glyph
/// of the cmap stays — in those stages the first alternate, No. 1175; under rand the one of <see cref="RandomAlternate"/>, No. 1223) and device
/// tables. Contextual
/// positioning (GPOS 7, 8), reverse chaining substitution (GSUB 8), the component of a mark on a ligature and glyph classes derived from
/// Unicode without GlyphClassDef since docs/FEHLERGEDAECHTNIS.md, No. 1161. The work per call is bounded (lookup attempts and matching
/// steps, No. 1137).
/// </remarks>
public sealed partial class TextShaper
{
    private static readonly ConditionalWeakTable<OpenTypeFont, TextShaper> Shapers = new();

    /// <summary>
    /// Substitution features in the usual order (they are applied in LookupList order); rclt cannot be switched off (OpenType 67.12). Besides
    /// them rvrn always applies as a stage of its own before all others (67.20), and the features of the direction of the run (66.7, 66.8, 67.17,
    /// 67.18) — before, neither rvrn, rclt nor the direction features were applied (docs/FEHLERGEDAECHTNIS.md, No. 1174). Since No. 1223 also
    /// rand, which should be on where the font has it (67.11 sentence 9): its alternates are chosen by a fixed function of the text
    /// (<see cref="RandomAlternate"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSubstitutions = ["ccmp", "locl", "rlig", "liga", "clig", "calt", "rclt", "rand"];

    /// <summary>
    /// Positioning features enabled by default; cpsp only in words of capitals (OpenType 64.19), kern not at glyphs palt covers while palt is off
    /// (66.1), and in a run of CJK text chws or halt instead of kern (64.15, 65.10; No. 1174).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultPositioning = ["kern", "curs", "mark", "mkmk", "dist", "cpsp"];

    private readonly OpenTypeFont _font;
    private readonly LayoutTable? _gsub;
    private readonly LayoutTable? _gpos;
    private readonly GlyphDefinitions? _gdef;
    private readonly TableData? _kern;

    private TextShaper(OpenTypeFont font)
    {
        _font = font;
        // only tables of major version 1: a reader does not use a major version it does not know (OpenType 1.3) — before, GSUB and GPOS of
        // version 2 were applied (docs/FEHLERGEDAECHTNIS.md, No. 1149)
        static bool Major1(ReadOnlySpan<byte> t) => t[0] == 0 && t[1] == 1;
        // a variable font is drawn in its default instance, whose FeatureVariations count (No. 1154); fvar names its axes at offset 8
        var axes = font.TableData("fvar") is { Length: >= 10 } fvar ? fvar[8] << 8 | fvar[9] : 0;
        if (font.TableData("GSUB") is { Length: > 10 } gsub && Major1(gsub)) _gsub = new LayoutTable(gsub.ToArray(), 7, axes);
        if (font.TableData("GPOS") is { Length: > 10 } gpos && Major1(gpos)) _gpos = new LayoutTable(gpos.ToArray(), 9, axes);
        if (font.TableData("GDEF") is { Length: > 10 } gdef && Major1(gdef)) _gdef = new GlyphDefinitions(gdef.ToArray());
        if (font.TableData("kern") is { Length: > 8 } kern) _kern = new TableData(kern.ToArray());
    }

    /// <summary>Returns the shaper of a font.</summary>
    /// <param name="font">The font.</param>
    /// <returns>The shaper, created once per font and then shared.</returns>
    public static TextShaper For(OpenTypeFont font) => Shapers.GetValue(font, f => new TextShaper(f));

    /// <summary>Describes the features and lookup types of the font (command line "schrift").</summary>
    /// <returns>One line per entry.</returns>
    public IEnumerable<string> Describe()
    {
        if (_gsub is not null)
        {
            yield return "GSUB:";
            foreach (var line in _gsub.Describe()) yield return "  " + line;
        }
        if (_gpos is not null)
        {
            yield return "GPOS:";
            foreach (var line in _gpos.Describe()) yield return "  " + line;
        }
        if (_kern is not null) yield return "kern-Tabelle (alt)";
        if (_gdef is not null) yield return "GDEF";
    }

    /// <summary>Shapes a text without a known language (the default language system of each script applies).</summary>
    /// <param name="text">The text; a tab is shaped as a space.</param>
    /// <param name="features">Features to switch on in addition (tag; it applies to substitution and positioning, the font's tables decide
    /// where it exists) or off ("-liga"), in this order; null for the defaults.</param>
    /// <returns>The glyphs in visual order (the same as text order, since there is no reordering).</returns>
    public ShapedGlyph[] Shape(string text, IEnumerable<string>? features = null) => Shape(text, features, null, null);

    /// <summary>Shapes a text in the language given, each run in the direction of its first strong character.</summary>
    /// <param name="text">The text; a tab is shaped as a space.</param>
    /// <param name="features">Features to switch on in addition or off ("-liga"), in this order; null for the defaults.</param>
    /// <param name="language">The language of the text as a language tag (/Lang, BCP 47); null if unknown.</param>
    /// <returns>The glyphs in text order.</returns>
    public ShapedGlyph[] Shape(string text, IEnumerable<string>? features, string? language) => Shape(text, features, language, null);

    /// <summary>
    /// Shapes a text: split into runs of one script (<see cref="UnicodeScripts.Itemize"/>), each run shaped with the lookups of its script
    /// and of the language system of <paramref name="language"/> (OpenType 2.4.1, 2.11.2). Before, the whole text was shaped under „latn“, or
    /// under „arab“ as soon as one letter of a cursive script occurred (docs/FEHLERGEDAECHTNIS.md, No. 1162).
    /// </summary>
    /// <param name="text">The text; a tab is shaped as a space.</param>
    /// <param name="features">Features to switch on in addition or off ("-liga"), in this order; null for the defaults.</param>
    /// <param name="language">The language of the text as a language tag (/Lang, BCP 47: „tr“, „ur-PK“); null if unknown.</param>
    /// <param name="rightToLeft">The direction of the text (the caller's embedding level: odd is right to left; its characters at odd levels
    /// are mirrored already, UAX #9 L4); null: each run in the direction of its first strong character.</param>
    /// <returns>The glyphs in text order (marks of Arabic script in the order of the transient reordering, no other reordering);
    /// <see cref="ShapedGlyph.Cluster"/> counts in the whole text.</returns>
    public ShapedGlyph[] Shape(string text, IEnumerable<string>? features, string? language, bool? rightToLeft) =>
        Shape(text, features, language, rightToLeft, swappingInhibited: false, shapePresentationForms: false);

    /// <summary>
    /// Shapes a text as <see cref="Shape(string, IEnumerable{string}?, string?, bool?)"/> with the states of two deprecated format characters
    /// in force at the start of the text (The Unicode Standard, 23.3.1): where symmetric swapping is inhibited (after U+206A up to U+206B or
    /// the end of the paragraph) a mirrored character is not given rtlm either and keeps the glyph its name describes (docs/FEHLERGEDAECHTNIS.md,
    /// No. 1215); while Arabic form shaping is active (after U+206D up to U+206C) the Arabic presentation forms are shaped as their nominal
    /// letters (<see cref="NominalLetters"/>, No. 1221). Within the text the characters switch the states.
    /// </summary>
    /// <param name="swappingInhibited">Whether symmetric swapping is inhibited at the start of <paramref name="text"/> (by a U+206A before it
    /// in a longer text, <see cref="Bidi.Analysis.SwappingInhibitedBefore"/>).</param>
    /// <param name="shapePresentationForms">Whether a U+206D before the text (and no U+206C after it) is in force.</param>
    internal ShapedGlyph[] Shape(string text, IEnumerable<string>? features, string? language, bool? rightToLeft, bool swappingInhibited,
        bool shapePresentationForms = false)
    {
        var substitutions = new HashSet<string>(DefaultSubstitutions, StringComparer.Ordinal);
        var positioning = new HashSet<string>(DefaultPositioning, StringComparer.Ordinal);
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in features ?? [])
        {
            if (f.StartsWith('-'))
            {
                substitutions.Remove(f[1..]);
                positioning.Remove(f[1..]);
                disabled.Add(f[1..]);
            }
            else
            {
                // a feature can be a substitution or a positioning one (or both); before, it went to GSUB only and a positioning feature
                // could not be switched on (docs/FEHLERGEDAECHTNIS.md, No. 271)
                substitutions.Add(f);
                positioning.Add(f);
                disabled.Remove(f);
            }
        }
        // required contextual alternates cannot be switched off (OpenType 67.12), nor can rvrn (67.20, its own stage in ShapeRun)
        substitutions.Add("rclt");
        disabled.Remove("rclt");

        var run = new List<ShapedGlyph>(text.Length);
        var codePoints = new List<int>(text.Length);
        var index = 0;
        // within the unit of a prepended concatenation mark (the mark and the digits or letters after it) a default ignorable character breaks
        // the unit instead of being passed over (The Unicode Standard, 23.2.3; No. 1183)
        var concatenation = false;
        var unswapped = swappingInhibited;
        var forms = shapePresentationForms;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.IsBmp) unswapped = Bidi.SwappingInhibitedAfter(unswapped, (char)rune.Value);
            // the character shaping selectors switch the shaping of presentation forms; they do not nest (The Unicode Standard 23.3.1, No. 1221)
            if (rune.Value is InhibitFormShaping or ActivateFormShaping) forms = rune.Value == ActivateFormShaping;
            else if (forms && NominalLetters(rune.Value) is { } nominal && run.Count + nominal.Length <= MaxGlyphs)
            {
                // a presentation form while the shaping is active: the glyphs of its nominal letters, shaped with their neighbours; the first carries
                // the character as typed, the others no text of their own (one cluster)
                var nominalGlyphs = Array.ConvertAll(nominal, c => _font.GlyphForUnicode(c));
                if (Array.TrueForAll(nominalGlyphs, g => g > 0))
                {
                    for (var k = 0; k < nominal.Length; k++)
                    {
                        var nominalCategory = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(nominal[k]);
                        run.Add(new ShapedGlyph
                        {
                            Glyph = nominalGlyphs[k], Cluster = index, Text = k == 0 ? rune.ToString() : "", Gates = GateKern,
                            Class = nominalCategory == System.Globalization.UnicodeCategory.NonSpacingMark ? (byte)3 : (byte)1,
                        });
                        codePoints.Add(nominal[k]);
                    }
                    index += rune.Utf16SequenceLength;
                    concatenation = false;
                    continue;
                }
            }
            // a variation selector after a character: the glyph of the sequence from cmap format 14 (OpenType 9.4.10), and the selector
            // itself without a glyph of its own unless the font maps it — before it became glyph 0, a box (docs/FEHLERGEDAECHTNIS.md, No. 1160).
            // A selector the font maps stays a glyph of its own also after a variation sequence of format 14, so that a ligature of the font
            // that names it (the keycap „#“ U+FE0F U+20E3) can form; it is passed over by ligatures and positioning that do not name it
            // (The Unicode Standard, 23.4; before, it went into the text of the glyph before it and such a ligature could not form, No. 1183)
            if (OpenTypeFont.IsVariationSelector(rune.Value) && run.Count > 0)
            {
                var previous = run[^1];
                var variant = _font.GlyphForVariation(codePoints[^1], rune.Value);
                if (variant > 0) run[^1] = previous = previous with { Glyph = variant };
                if (_font.GlyphForUnicode(rune.Value) <= 0)
                {
                    run[^1] = previous with { Text = previous.Text + rune };
                    index += rune.Utf16SequenceLength;
                    continue;
                }
            }
            var glyph = rune.Value == '\t' ? _font.GlyphForUnicode(' ') : _font.GlyphForUnicode(rune.Value);
            // the class used without a GlyphClassDef: a non-spacing mark is a mark, every other character a base (No. 1161)
            var category = System.Text.Rune.GetUnicodeCategory(rune);
            var cls = category == System.Globalization.UnicodeCategory.NonSpacingMark ? (byte)3 : (byte)1;
            var ignorable = UnicodeProperties.IsDefaultIgnorable(rune.Value);
            run.Add(new ShapedGlyph
            {
                Glyph = glyph, Cluster = index, Text = rune.ToString(), Class = cls, Gates = unswapped ? (byte)(GateKern | GateUnswapped) : GateKern,
                Joiner = rune.Value == 0x200D ? JoinerZwj : rune.Value == 0x200C ? JoinerZwnj : !ignorable ? (byte)0
                    : concatenation ? JoinerHeld : OpenTypeFont.IsVariationSelector(rune.Value) ? JoinerSelector : JoinerFormat,
            });
            codePoints.Add(rune.Value);
            index += rune.Utf16SequenceLength;
            concatenation = UnicodeProperties.IsPrependedConcatenationMark(rune.Value) || concatenation && !ignorable
                && (System.Text.Rune.IsLetterOrDigit(rune) || category is System.Globalization.UnicodeCategory.NonSpacingMark);
        }
        // Work limit as in common shaping engines (an operation budget): a damaged or malicious font can nest lookups into each
        // other until shaping hangs; once the limit is reached the rest stays unshaped. Besides the lookup attempts the matching steps
        // count (see Work), each glyph of the input with a generous share (No. 1137); one budget for all runs of the text
        var work = new Work(Math.Max(16_384, run.Count * 64), Math.Max(1L << 22, run.Count * 4096L));
        var lang = UnicodeScripts.OpenTypeLanguages(language);
        var runs = UnicodeScripts.Itemize(codePoints);
        var options = new RunOptions(substitutions, positioning, disabled, lang);
        if (runs.Count == 1)
        {
            ShapeRun(run, codePoints, UnicodeScripts.OpenTypeTags(runs[0].Script), options, rightToLeft ?? StartsRightToLeft(codePoints), work);
            return [.. run];
        }
        var result = new List<ShapedGlyph>(run.Count);
        foreach (var (start, length, script) in runs)
        {
            var piece = run.GetRange(start, length);
            var points = codePoints.GetRange(start, length);
            ShapeRun(piece, points, UnicodeScripts.OpenTypeTags(script), options, rightToLeft ?? StartsRightToLeft(points), work);
            result.AddRange(piece);
        }
        return [.. result];
    }

    /// <summary>
    /// Shapes one run of a single script: GSUB, advances, the legacy kern table and GPOS, each with the lookups of the language system that
    /// <paramref name="scripts"/> and the language select. A run with a letter of a cursive script (Arabic, Syriac, N'Ko, Mongolian, Phags-pa,
    /// Mandaic, Manichaean, Hanifi Rohingya, Sogdian, Psalter Pahlavi, Chorasmian, Old Uyghur, Adlam — the joining types of ArabicShaping.txt)
    /// takes its joining forms and the stages of Arabic shaping under the tag of its own script, not „arab“ (No. 910, No. 1162), and the Syriac
    /// forms of Alaph (fin2, fin3, med2); Arabic marks in any run the order of the transient reordering (UAX #53). First of all the stage of rvrn;
    /// the features of the run's direction with the others; after GPOS the stretching of stch (No. 1174). A run of a syllabic script is cut into
    /// syllables and shaped stage by stage (No. 1175).
    /// </summary>
    private void ShapeRun(List<ShapedGlyph> run, List<int> codePoints, string[] scripts, RunOptions options, bool rightToLeft, Work work)
    {
        // fractions with U+2044 between digits take numr, dnom and frac (No. 1183)
        options = MarkFractions(run, codePoints, options);
        var language = options.Language;
        var substitutions = options.Substitutions;
        // the lookups of rand in this run's language system (No. 1223)
        work.RandomLookups = _gsub is not null && substitutions.Contains("rand") && _gsub.LookupsFor(["rand"], scripts, language, required: false) is { Length: > 0 } rand
            ? [.. rand] : null;
        var joining = codePoints.Exists(ArabicJoining.Joins);
        var complex = joining ? Complex.None : ComplexOf(scripts);
        // a font made for the default shaping of a script of the universal model keeps it (No. 1181)
        if (complex == Complex.Universal && !UniversalApplies(scripts)) complex = Complex.None;
        // the Arabic marks in canonical order and then as the transient reordering puts them (UAX #53, No. 1174), in every run with Arabic marks —
        // before only in runs with a connecting letter (No. 1175): canonically equivalent texts look the same; each glyph keeps its own character
        // (the text stays as typed)
        if (complex == Complex.None)
        {
            ReorderArabicMarks(run, codePoints, work);
            // then what composes canonically composed where the font has the composed letter (UAX #53, 3.2; No. 1193, 1197) — not in the runs
            // of a syllabic script whose font is made for the default shaping (they keep their own rules, No. 1181)
            ComposeForDisplay(run, codePoints, work, sequences: joining || ComplexOf(scripts) == Complex.None);
        }
        if (joining)
        {
            var forms = ArabicJoining.Forms(codePoints);
            for (var i = 0; i < run.Count; i++) run[i] = run[i] with { Form = forms[i] };
        }
        else MarkCapitalWords(run, codePoints);

        // the features of the direction of the run (OpenType 66.7, 66.8, 67.17, 67.18): ltra and ltrm left to right, rtla and rtlm right to
        // left — rtlm only at mirrored characters that the caller did not replace by their mirror image (no counterpart, or not in the font)
        string[] direction = [.. (rightToLeft ? new[] { "rtla", "rtlm" } : ["ltra", "ltrm"]).Where(f => !options.Disabled.Contains(f))];
        if (rightToLeft && direction.Contains("rtlm")) MarkMirrored(run, codePoints);

        // the syllables of an Indic or South-East Asian script, the composition of Hangul jamo (docs/FEHLERGEDAECHTNIS.md, No. 1175): before all
        // substitutions, as the characters are prepared for shaping
        if (complex != Complex.None) PrepareSyllables(run, codePoints, complex, scripts, work);
        else if (!joining && scripts is ["hang", ..])
        {
            ComposeHangul(run, codePoints, scripts, options.Language, work);
            HangulToneMarks(run, codePoints, work);
        }

        // required variation alternates first, a stage of their own and only their single substitutions (OpenType 67.20, sentences 26, 30, 34)
        if (_gsub is not null)
            foreach (var lookup in _gsub.LookupsFor(["rvrn"], scripts, language, required: false))
                if (_gsub.Lookup(lookup).Type == 1) new Substitution(this, run, work).ApplyLookup(lookup);

        // conjoining Hangul jamo: leading, vowel and trailing jamo of a syllable take ljmo, vjmo and tjmo (OpenType 66.4, 67.33, 68.8); the
        // forms are kept in the joining form of the glyph (Initial, Medial, Final), as for the cursive scripts (No. 1162)
        var jamo = !joining && scripts.Length > 0 && scripts[0] == "hang" && HangulJamo(run, codePoints);
        var italic = _gsub is not null && substitutions.Contains("ital") ? new HashSet<int>(_gsub.LookupsFor(["ital"], scripts, language, required: false)) : null;
        if (complex is Complex.Indic or Complex.Khmer or Complex.Myanmar or Complex.Tibetan or Complex.Universal)
            SubstituteSyllables(run, complex, scripts, options, direction, italic, work);
        else if (_gsub is not null && jamo)
        {
            // every lookup in LookupList order; one of a jamo feature (that is not also one of the others) only at the jamo of its kind; a ZWJ
            // passed over in the input of a ligature (No. 1183)
            work.AutoZwj = true;
            var general = Gated(_gsub, [.. substitutions, .. direction], scripts, language, true);
            var kinds = new Dictionary<int, ArabicJoining.Form>();
            foreach (var (feature, form) in new[] { ("ljmo", ArabicJoining.Form.Initial), ("vjmo", ArabicJoining.Form.Medial), ("tjmo", ArabicJoining.Form.Final) })
                foreach (var lookup in _gsub.LookupsFor([feature], scripts, language, required: false))
                    if (!general.ContainsKey(lookup)) kinds.TryAdd(lookup, form);
            foreach (var lookup in general.Keys.Concat(kinds.Keys).Order())
                new Substitution(this, run, work, general.GetValueOrDefault(lookup), italic?.Contains(lookup) == true)
                    .ApplyLookup(lookup, kinds.TryGetValue(lookup, out var only) ? only : null);
        }
        else if (_gsub is not null && !joining)
        {
            // a ZWJ in the input of a ligature asks for it (The Unicode Standard, 23.2.2): passed over (No. 1183)
            work.AutoZwj = true;
            foreach (var (lookup, gate) in Gated(_gsub, [.. substitutions, .. direction], scripts, language, true))
                new Substitution(this, run, work, gate, italic?.Contains(lookup) == true).ApplyLookup(lookup);
        }
        else if (_gsub is not null)
        {
            // OpenType Arabic: composition, local forms and the direction features first, then each joining feature only where the letter
            // takes that form (the Syriac Alaph forms fin2, fin3, med2 among them, No. 1174), then the required and other ligatures and the
            // remaining features (the specification's order of stages); ccmp leads only here, as with the reference shaper (special rule)
            string[] first = ["ccmp", "locl", .. direction];
            foreach (var (lookup, gate) in Gated(_gsub, first, scripts, language, true))
                new Substitution(this, run, work, gate).ApplyLookup(lookup);
            foreach (var (feature, form) in new[] { ("isol", ArabicJoining.Form.Isolated), ("fina", ArabicJoining.Form.Final),
                         ("fin2", ArabicJoining.Form.Final2), ("fin3", ArabicJoining.Form.Final3), ("medi", ArabicJoining.Form.Medial),
                         ("med2", ArabicJoining.Form.Medial2), ("init", ArabicJoining.Form.Initial) })
                foreach (var lookup in _gsub.LookupsFor([feature], scripts, language, required: false))
                    new Substitution(this, run, work).ApplyLookup(lookup, form);
            var rest = substitutions.Where(f => !first.Contains(f)).ToList();
            foreach (var (lookup, gate) in Gated(_gsub, rest, scripts, language, false))
                new Substitution(this, run, work, gate, italic?.Contains(lookup) == true).ApplyLookup(lookup);
        }
        work.AutoZwj = false;

        for (var i = 0; i < run.Count; i++)
        {
            var g = run[i];
            g.XAdvance = _font.AdvanceWidth(g.Glyph);
            // Marks (GDEF class 3, or derived from Unicode without a GlyphClassDef) do not advance — except in Indic and Khmer text, where the
            // font's advance of a mark stays, as with the reference shaper (No. 1175)
            if (ClassOf(g) == 3 && _gpos is not null && complex is not (Complex.Indic or Complex.Khmer)) g.XAdvance = 0;
            run[i] = g;
        }

        var positioning = PositioningFor(run, scripts, options, joining, work);
        // above- and below-base marks of the syllabic scripts (OpenType 64.3, 64.9; No. 1175)
        if (complex != Complex.None)
            positioning = [.. positioning, .. new[] { "abvm", "blwm", "dist" }.Where(f => !options.Disabled.Contains(f))];
        // Without kern lookups in the resolved language system of GPOS the kern table applies first, then the other GPOS features (OpenType
        // 3.15) — before it came after them, and a mark attached by GPOS moved by the kerning of its base (docs/FEHLERGEDAECHTNIS.md, No. 1162)
        var kernInGpos = _gpos is not null && positioning.Contains("kern") && _gpos.LookupsFor(["kern"], scripts, language).Length > 0;
        if (!kernInGpos && positioning.Contains("kern") && _kern is { } kern) LegacyKerning(kern, run, work);
        if (_gpos is not null)
        {
            foreach (var (lookup, gate) in Gated(_gpos, [.. positioning, .. direction], scripts, language, true))
                new Positioning(this, run, work, gate).ApplyLookup(lookup);
            ResolveAttachments(run);
        }
        HideIgnorables(run);

        if (!options.Disabled.Contains("stch")) Stretch(run, scripts, language, rightToLeft, work);
    }

    /// <summary>
    /// A default ignorable character the font has no glyph for (glyph 0 after GSUB, or one a ligature passed over) is drawn as nothing: the
    /// glyph of the space without an advance or offset, its text kept (The Unicode Standard, chapter 23: invisible unless supported otherwise);
    /// always the soft hyphen, which shows only at the end of a line (23.2.1), and a variation selector, which only chooses the form of the
    /// character before it (23.4) and stays a glyph only for the font's ligatures. A font without a space glyph: the text goes to the glyph
    /// before (or after) it and the glyph is left out. A glyph the font substituted (a ZWJ it replaced by a form of its own) stays. Before,
    /// such characters were drawn as the box of glyph 0 with its advance and the soft hyphen as a hyphen in the middle of a word
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1183).
    /// </summary>
    private void HideIgnorables(List<ShapedGlyph> run)
    {
        // the character shaping and numeric shape selectors are interpreted, so never drawn (23.3.1, No. 1221)
        static bool Always(string text) => text is "\u00AD" or "\u206C" or "\u206D" or "\u206E" or "\u206F"
            || text.Length is 1 or 2 && System.Text.Rune.TryGetRuneAt(text, 0, out var r) && r.Utf16SequenceLength == text.Length
                && OpenTypeFont.IsVariationSelector(r.Value);
        static bool Hidden(ShapedGlyph g) => g.Joiner != 0 && (g.Glyph == 0 || Always(g.Text));
        if (!run.Exists(Hidden)) return;
        var space = _font.GlyphForUnicode(' ');
        if (space > 0)
        {
            for (var i = 0; i < run.Count; i++)
                if (Hidden(run[i])) run[i] = run[i] with { Glyph = space, XAdvance = 0, YAdvance = 0, XOffset = 0, YOffset = 0, RelativeToBase = false };
            return;
        }
        // in one pass (removing glyph by glyph would move the rest of the run each time)
        var kept = new List<ShapedGlyph>(run.Count);
        var pending = "";
        var pendingCluster = -1;
        foreach (var g in run)
        {
            if (!Hidden(g))
            {
                kept.Add(pending.Length == 0 ? g : g with { Text = pending + g.Text, Cluster = Math.Min(g.Cluster, pendingCluster) });
                pending = "";
                continue;
            }
            if (kept.Count > 0) kept[^1] = kept[^1] with { Text = kept[^1].Text + g.Text };
            else
            {
                if (pending.Length == 0) pendingCluster = g.Cluster;
                pending += g.Text;
            }
        }
        if (kept.Count == 0) return;   // nothing but such characters: they stay as they are
        run.Clear();
        run.AddRange(kept);
    }

    /// <summary>
    /// Marks the conjoining jamo of Hangul syllables written as jamo sequences (The Unicode Standard, 3.12): one or more leading consonants
    /// (U+1100–115F, U+A960–A97C), one or more vowels (U+1160–11A7, U+D7B0–D7C6) and any trailing consonants (U+11A8–11FF, U+D7CB–D7FB) — the
    /// leading ones as Initial (ljmo), the vowels as Medial (vjmo), the trailing ones as Final (tjmo). A jamo outside such a sequence keeps its
    /// glyph of the cmap. The precomposed syllables (U+AC00–D7A3) need no forms; jamo the font has as a syllable are composed before
    /// (<see cref="ComposeHangul"/>, No. 1175). The kinds come from HangulSyllableType.txt.
    /// </summary>
    /// <returns>Whether a jamo was marked.</returns>
    private static bool HangulJamo(List<ShapedGlyph> run, List<int> codePoints)
    {
        static int Kind(int c) => SyllableData.HangulOf(c) switch
        {
            HangulSyllableType.LeadingJamo => 1,
            HangulSyllableType.VowelJamo => 2,
            HangulSyllableType.TrailingJamo => 3,
            _ => 0,
        };
        var marked = false;
        for (var i = 0; i < codePoints.Count;)
        {
            var start = i;
            while (i < codePoints.Count && Kind(codePoints[i]) == 1) i++;
            var vowels = i;
            while (i < codePoints.Count && Kind(codePoints[i]) == 2) i++;
            if (vowels == start || i == vowels)
            {
                i = Math.Max(i, start + 1);
                continue;
            }
            while (i < codePoints.Count && Kind(codePoints[i]) == 3) i++;
            for (var k = start; k < i; k++)
                run[k] = run[k] with { Form = k < vowels ? ArabicJoining.Form.Initial : Kind(codePoints[k]) == 2 ? ArabicJoining.Form.Medial : ArabicJoining.Form.Final };
            marked = true;
        }
        return marked;
    }

    /// <summary>
    /// After the last GPOS lookup: each attached mark takes the final place of its base — the base's offsets added, the advances from the base
    /// to the mark taken off (in the order of the run, so that a mark attached to a mark follows the place that mark has already taken; OpenType
    /// 20.4.5 sentence 10, 20.4.7; docs/FEHLERGEDAECHTNIS.md, No. 1179).
    /// </summary>
    private static void ResolveAttachments(List<ShapedGlyph> run)
    {
        for (var i = 0; i < run.Count; i++)
        {
            var g = run[i];
            if (!g.RelativeToBase) continue;
            var b = i - g.AttachBack;
            g.RelativeToBase = false;
            if (b >= 0 && b < i)
            {
                double advance = 0;
                for (var k = b; k < i; k++) advance += run[k].XAdvance;
                g.XOffset += run[b].XOffset - advance;
                g.YOffset += run[b].YOffset;
            }
            run[i] = g;
        }
    }

    // ------------------------------------------------------------ Shared

    /// <summary>Maximum number of glyphs produced by shaping (multiple substitutions would otherwise multiply them).</summary>
    private const int MaxGlyphs = 100_000;

    /// <summary>The RIGHT_TO_LEFT flag of a lookup (bit 0 of its lookupFlag).</summary>
    private const int RightToLeft = 1;

    /// <summary>
    /// Aligns the exit anchor of glyph <paramref name="i"/> with the entry anchor of glyph <paramref name="j"/>, as the specification asks
    /// (horizontal layout). In the direction of the line the advance of the first glyph (in logical order) is adjusted: that moves the
    /// second so that the anchors meet. Across the line the placement of one glyph is adjusted: of the second if the RIGHT_TO_LEFT flag
    /// is clear; of the first if it is set — then the last glyph of the connected sequence keeps its place and the ones before it, joined
    /// already (<paramref name="chain"/>), move with the first.
    /// </summary>
    internal static void Join(List<ShapedGlyph> run, int i, int j, (int X, int Y) exit, (int X, int Y) entry, bool rightToLeft, List<int> chain)
    {
        var first = run[i];
        var second = run[j];
        // the exit anchor lies at pen + offset + exit of the first glyph, the entry anchor at the next pen + offset + entry of the second
        first.XAdvance = first.XOffset + exit.X - (second.XOffset + entry.X);
        run[i] = first;
        if (!rightToLeft)
        {
            second.YOffset = first.YOffset + exit.Y - entry.Y;
            run[j] = second;
            chain.Clear();
            return;
        }
        // a new connected sequence unless the first glyph ends the one so far
        if (chain.Count == 0 || chain[^1] != i)
        {
            chain.Clear();
            chain.Add(i);
        }
        var move = second.YOffset + entry.Y - exit.Y - first.YOffset;
        foreach (var k in chain)
        {
            var g = run[k];
            g.YOffset += move;
            run[k] = g;
        }
        chain.Add(j);
    }

    /// <summary>
    /// Remaining work of one <see cref="Shape"/> call (one instance per call, hence thread-safe): the lookup attempts at a position, and
    /// the matching steps — each subtable, rule, ligature and substitution record tried, each glyph a context, a ligature or a mark
    /// passes on its way, and the glyphs moved when the run changes length. Counting the attempts alone let a font whose lookups,
    /// rule sets and ligature sets are filled up to 65 535 entries make one attempt cost billions of steps, and allocate a position
    /// array as long as the rule claimed (No. 1137).
    /// </summary>
    private sealed class Work(int ops, long steps)
    {
        public int Left = ops;

        private long _steps = steps;

        public bool Take() => --Left >= 0;

        /// <summary>Spends matching steps; once they are used up, no further lookup is applied (the rest stays unshaped).</summary>
        public bool Spend(long n)
        {
            if ((_steps -= n) >= 0) return true;
            Left = 0;
            return false;
        }

        /// <summary>Glyphs moved in the run (insertions, removals): cheap block copies, one step per 64.</summary>
        public bool Move(int glyphs) => Spend(1 + glyphs / 64);

        /// <summary>
        /// Whether the substitution being applied passes over a ZWJ in its input (<see cref="Passable"/>): set for the lookups of the features of
        /// general reach in a script that does not join, clear for those of the syllabic stages and of the joining scripts (No. 1183).
        /// </summary>
        public bool AutoZwj;

        /// <summary>The lookups of rand in the language system of the run being shaped (null: rand is off or the font has none; No. 1223).</summary>
        public HashSet<int>? RandomLookups;

        /// <summary>Whether the lookup being applied (and the lookups it calls from a context) is one of rand: an alternate substitution then
        /// chooses among the alternates (<see cref="RandomAlternate"/>; No. 1223).</summary>
        public bool RandomActive;

        /// <summary>Ligatures formed so far in this call: the next one's id (<see cref="ShapedGlyph.LigatureId"/>).</summary>
        public int NextLigatureId() => ++_ligatures;

        private int _ligatures;
    }

    /// <summary>The class of a glyph: from the GlyphClassDef of GDEF, or — without one — the class derived from Unicode and GSUB (No. 1161).</summary>
    private int ClassOf(ShapedGlyph g) => _gdef is { HasGlyphClasses: true } gdef ? gdef.GlyphClass(g.Glyph) : g.Class;

    /// <summary>
    /// Whether a lookup passes over a glyph: by its flags (GDEF classes, mark filtering) — and a GPOS lookup over the joiners, which only ask
    /// for or prevent joining and ligatures between their neighbours (The Unicode Standard, 23.2): kerning, distances, cursive joins and marks see
    /// the glyphs on either side of a joiner as neighbours (in a context see <see cref="MatchContext"/>). Before, a joiner broke a pair of kerning
    /// (measured with Nirmala UI: 586 Gujarati and 804 Telugu samples with ZWJ or ZWNJ placed otherwise than by the shaping library;
    /// docs/FEHLERGEDAECHTNIS.md, No. 1179).
    /// </summary>
    private bool Skips(Lookup lookup, ShapedGlyph g) => SkipsByFlags(lookup, g) || lookup.Positioning && g.Joiner is > 0 and < JoinerHeld;

    private bool SkipsByFlags(Lookup lookup, ShapedGlyph g) => GlyphDefinitions.Skips(_gdef, lookup, g.Glyph, ClassOf(g));

    /// <summary>
    /// Whether a context may pass over a joiner that does not match it: in GPOS ZWJ and ZWNJ everywhere, in GSUB a ZWJ in backtrack and lookahead
    /// (a ZWJ asks for a joining or ligature of its neighbours and does not stand between them for the context of a substitution — the half form
    /// chosen by the consonant after the ZWJ that asked for it; measured with Nirmala UI: 30 Gujarati samples otherwise than the shaping library);
    /// a ZWNJ, which prevents joining, and the joiners in the input of a substitution stay glyphs of their own. A joiner the context names
    /// matches (fonts ask for the ZWJ after Ra and virama in the lookahead of the eyelash Ra; No. 1179). Since No. 1183 every other default
    /// ignorable character everywhere (WJ, CGJ and the other format characters do not break a ligature or kerning, The Unicode Standard 23.2.1,
    /// 23.2.4) — not the variation selectors, which the contexts of a font may name to choose a form (the Mongolian free variation selectors:
    /// a context of the letter without the selector must not reach past it) —, and a ZWJ also in the input of a substitution where
    /// <paramref name="autoZwj"/> (a ligature of liga, clig, dlig … in a
    /// script that does not join: the ZWJ asks for it, 23.2.2 — not in Arabic and the other joining scripts, where it keeps a letter joined
    /// without the ligature, nor in the stages of the syllabic scripts, whose forms the font asks for with the ZWJ in their input).
    /// </summary>
    private static bool Passable(Lookup lookup, ShapedGlyph g, bool input, bool autoZwj) => lookup.Positioning ? g.Joiner is > 0 and < JoinerHeld
        : g.Joiner == JoinerFormat || g.Joiner == JoinerZwj && (!input || autoZwj);

    /// <summary>The next glyph of a context after <paramref name="i"/> by the lookup's flags, past a joiner that may be passed and does not match
    /// place <paramref name="k"/> of the sequence (<see cref="Passable"/>); -1 for none or when the steps are used up.</summary>
    private int NextInContext(List<ShapedGlyph> run, Lookup lookup, int i, Work work, Func<int, int, bool> match, int k, bool input)
    {
        for (var j = i + 1; j < run.Count; j++)
        {
            if (!work.Spend(1)) return -1;
            if (SkipsByFlags(lookup, run[j]) || Passable(lookup, run[j], input, work.AutoZwj) && !match(k, run[j].Glyph)) continue;
            return j;
        }
        return -1;
    }

    /// <summary>The glyph of a context before <paramref name="i"/>, as <see cref="NextInContext"/>.</summary>
    private int PreviousInContext(List<ShapedGlyph> run, Lookup lookup, int i, Work work, Func<int, int, bool> match, int k)
    {
        for (var j = i - 1; j >= 0; j--)
        {
            if (!work.Spend(1)) return -1;
            if (SkipsByFlags(lookup, run[j]) || Passable(lookup, run[j], false, work.AutoZwj) && !match(k, run[j].Glyph)) continue;
            return j;
        }
        return -1;
    }

    /// <summary>Values of <see cref="ShapedGlyph.Joiner"/>: ZWNJ, ZWJ, another default ignorable character, one within the unit of a prepended
    /// concatenation mark (not passed over; No. 1183).</summary>
    internal const byte JoinerZwnj = 1, JoinerZwj = 2, JoinerFormat = 3, JoinerSelector = 4, JoinerHeld = 5;

    /// <summary>
    /// The next component of a ligature after <paramref name="i"/>: the next glyph the lookup's flags do not skip, past a default ignorable
    /// character that is not the component itself (and past a ZWJ where <see cref="Work.AutoZwj"/>; <see cref="Passable"/>); -1 for none or
    /// when the steps are used up (No. 1183).
    /// </summary>
    private int NextComponent(List<ShapedGlyph> run, Lookup lookup, int i, int component, Work work)
    {
        for (var j = i + 1; j < run.Count; j++)
        {
            if (!work.Spend(1)) return -1;
            if (SkipsByFlags(lookup, run[j]) || (run[j].Joiner == JoinerSelector || Passable(lookup, run[j], true, work.AutoZwj)) && run[j].Glyph != component)
                continue;
            return j;
        }
        return -1;
    }

    /// <summary>Next position after <paramref name="i"/> that the lookup does not skip; -1 = none.</summary>
    private int Next(List<ShapedGlyph> run, Lookup lookup, int i)
    {
        for (var j = i + 1; j < run.Count; j++)
            if (!Skips(lookup, run[j])) return j;
        return -1;
    }

    private int Previous(List<ShapedGlyph> run, Lookup lookup, int i)
    {
        for (var j = i - 1; j >= 0; j--)
            if (!Skips(lookup, run[j])) return j;
        return -1;
    }

    /// <summary><see cref="Next(List{ShapedGlyph}, Lookup, int)"/>, the glyphs passed counted as steps; -1 also when the steps are used up.</summary>
    private int Next(List<ShapedGlyph> run, Lookup lookup, int i, Work work)
    {
        var j = Next(run, lookup, i);
        return work.Spend(j < 0 ? run.Count - i : j - i) ? j : -1;
    }

    private int Previous(List<ShapedGlyph> run, Lookup lookup, int i, Work work)
    {
        var j = Previous(run, lookup, i);
        return work.Spend(j < 0 ? i + 1 : i - j) ? j : -1;
    }

    /// <summary>
    /// Matches a context (GSUB 5/6): input from <paramref name="start"/>, backtrack (backwards) and
    /// lookahead. The match functions receive the position within their sequence and the glyph.
    /// </summary>
    private bool MatchContext(Work work, List<ShapedGlyph> run, Lookup lookup, int start,
        int backtrack, Func<int, int, bool> backtrackMatch,
        int input, Func<int, int, bool> inputMatch,
        int lookahead, Func<int, int, bool> lookaheadMatch,
        out int[] positions)
    {
        positions = [];
        var length = Math.Max(1, input);
        // A rule can match only glyphs that are there: one whose input and lookahead are longer than the rest of the run, or whose
        // backtrack is longer than the glyphs before it, never matches — decided before the positions are allocated (No. 1137)
        if (!work.Spend(1) || length + lookahead > run.Count - start || backtrack > start) return false;
        positions = new int[length];
        positions[0] = start;
        var j = start;
        for (var k = 1; k < input; k++)
        {
            j = NextInContext(run, lookup, j, work, inputMatch, k, input: true);
            if (j < 0 || !inputMatch(k, run[j].Glyph)) return false;
            positions[k] = j;
        }
        var after = j;
        for (var k = 0; k < lookahead; k++)
        {
            after = NextInContext(run, lookup, after, work, lookaheadMatch, k, input: false);
            if (after < 0 || !lookaheadMatch(k, run[after].Glyph)) return false;
        }
        var before = start;
        for (var k = 0; k < backtrack; k++)
        {
            before = PreviousInContext(run, lookup, before, work, backtrackMatch, k);
            if (before < 0 || !backtrackMatch(k, run[before].Glyph)) return false;
        }
        return true;
    }

    // ------------------------------------------------------------ GSUB

    /// <param name="gate">The bits of <see cref="ShapedGlyph.Gates"/> one of which a glyph must carry for the lookup to apply at it (0: everywhere).</param>
    /// <param name="italic">Whether the lookup is one of the feature ital (the glyphs it replaces are marked <see cref="ShapedGlyph.Italic"/>).</param>
    /// <param name="mask">The bit of <see cref="ShapedGlyph.Mask"/> a glyph must carry for the lookup to apply at it (0: everywhere; No. 1175).</param>
    /// <param name="firstAlternate">Whether an alternate substitution (type 3) takes the first alternate — in the stages of a syllabic script, whose
    /// fonts use it as the form of a required feature, as the reference shaper does (No. 1175); elsewhere the glyph of the cmap stays.</param>
    private readonly struct Substitution(TextShaper shaper, List<ShapedGlyph> run, Work work, byte gate = 0, bool italic = false, ushort mask = 0,
        bool firstAlternate = false)
    {
        private TableData T => shaper._gsub!.T;

        /// <summary>Whether the lookup may apply at a glyph (its gate, No. 1174, and its mask in a syllable, No. 1175).</summary>
        private bool Allowed(ShapedGlyph g) => (gate == 0 || (g.Gates & gate) != 0) && (mask == 0 || (g.Mask & mask) != 0);

        /// <param name="index">The lookup.</param>
        /// <param name="only">Only at glyphs of this joining form (Arabic isol, init, medi, fina …); null: everywhere.</param>
        public void ApplyLookup(int index, ArabicJoining.Form? only = null)
        {
            // a lookup of rand chooses among alternates, also in the lookups it calls (No. 1223)
            work.RandomActive = work.RandomLookups?.Contains(index) == true;
            var lookup = shaper._gsub!.Lookup(index);
            if (lookup.Type == 8)
            {
                // reverse chaining single substitution runs from the end of the run to its start (OpenType 21.4.9)
                for (var i = run.Count - 1; i >= 0 && work.Left > 0; i--)
                    if (!(only is { } form && run[i].Form != form) && Allowed(run[i]) && !shaper.Skips(lookup, run[i])) ApplyAt(lookup, i, 0, out _);
                return;
            }
            for (var i = 0; i < run.Count && run.Count < MaxGlyphs && work.Left > 0;)
            {
                if (only is { } form && run[i].Form != form || !Allowed(run[i]) || shaper.Skips(lookup, run[i]) || !ApplyAt(lookup, i, 0, out var next)) i++;
                else i = Math.Max(next, i + 1);
            }
        }

        /// <summary>Applies a lookup at exactly one position (also nested from a context); next = position after it.</summary>
        private bool ApplyAt(Lookup lookup, int i, int depth, out int next)
        {
            next = i + 1;
            if (depth > 8 || !work.Take()) return false;
            foreach (var sub in lookup.Subtables)
                if (!work.Spend(1)) return false;
                else if (ApplySubtable(lookup, sub, i, depth, out next)) return true;
            return false;
        }

        private bool ApplySubtable(Lookup lookup, int sub, int i, int depth, out int next)
        {
            next = i + 1;
            var t = T;
            var glyph = run[i].Glyph;
            var format = t.U16(sub);
            switch (lookup.Type)
            {
                case 1:
                {
                    var c = t.Coverage(sub + t.U16(sub + 2), glyph);
                    // format 2: the coverage index names an entry of the substitute array (GSUB 21.4.2); other formats are not defined
                    if (c < 0 || format is not (1 or 2) || format == 2 && c >= t.U16(sub + 4)) return false;
                    Replace(i, format == 1 ? (glyph + t.I16(sub + 4)) & 0xFFFF : t.U16(sub + 6 + c * 2));
                    return true;
                }
                case 2:
                {
                    var c = t.Coverage(sub + t.U16(sub + 2), glyph);
                    if (c < 0 || c >= t.U16(sub + 4)) return false;
                    var seq = sub + t.U16(sub + 6 + c * 2);
                    var count = t.U16(seq);
                    // A count of 0 (deleting the glyph, not allowed by the specification) would otherwise skip the next glyph
                    if (count == 0 || run.Count + count > MaxGlyphs || !work.Move(count + run.Count - i)) return false;
                    var original = run[i];
                    // the glyphs of a ligature taken apart are no ligatures (No. 1161)
                    if (original.LigatureComponents > 0) original = original with { LigatureComponents = 0, Class = 1 };
                    var glyphs = new ShapedGlyph[count];
                    var formed = (byte)(original.Formed | FormedSubstituted | FormedMultiplied);
                    // a joiner the font replaces by glyphs of its own is no longer passed over in GPOS (No. 1179)
                    for (var k = 0; k < count; k++)
                        glyphs[k] = original with { Glyph = t.U16(seq + 2 + k * 2), Text = k == 0 ? original.Text : "", Formed = formed, Joiner = 0 };
                    // in one block: inserting glyph by glyph moved the rest of the run once per glyph (No. 1137)
                    run[i] = glyphs[0];
                    run.InsertRange(i + 1, glyphs[1..]);
                    next = i + count;
                    return true;
                }
                case 3 when firstAlternate || work.RandomActive:
                {
                    // AlternateSubstFormat1 (OpenType 21.4.4): the first glyph of the alternate set of the coverage index — under rand the one
                    // the fixed function of the text chooses (OpenType 67.11 sentences 8, 10; No. 1223)
                    var c = t.Coverage(sub + t.U16(sub + 2), glyph);
                    if (c < 0 || format != 1 || c >= t.U16(sub + 4) || t.U16(sub + 6 + c * 2) == 0) return false;
                    var set = sub + t.U16(sub + 6 + c * 2);
                    var count = t.U16(set);
                    if (count == 0) return false;
                    var pick = work.RandomActive ? RandomAlternate(run, i, count) : 0;
                    Replace(i, t.U16(set + 2 + pick * 2));
                    return true;
                }
                case 4:
                    return Ligature(lookup, sub, i, out next);
                case 5:
                    return Context(lookup, sub, i, depth, chained: false, out next);
                case 6:
                    return Context(lookup, sub, i, depth, chained: true, out next);
                case 8:
                    // only as a lookup of its own: it cannot be nested in a context (it runs backwards over the whole run)
                    return depth == 0 && ReverseChain(lookup, sub, i);
                default:
                    return false;   // 3 (alternates to choose from) outside rand: there is no choice, the glyph of the cmap stays
            }
        }

        /// <summary>
        /// Reverse chaining contextual single substitution (lookup type 8, ReverseChainSingleSubstFormat1; OpenType 21.4.9): the input glyph in
        /// its coverage, the backtrack (from the glyph before it towards the start) and the lookahead (from the glyph after it towards the end)
        /// each matching one coverage per place; then the substitute of its coverage index. The run is processed from its end, so the lookahead
        /// already holds the substitutes made (docs/FEHLERGEDAECHTNIS.md, No. 1161; before passed over).
        /// </summary>
        private bool ReverseChain(Lookup lookup, int sub, int i)
        {
            var t = T;
            if (t.U16(sub) != 1 || t.U16(sub + 2) == 0) return false;
            var c = t.Coverage(sub + t.U16(sub + 2), run[i].Glyph);
            if (c < 0) return false;
            var backtrack = t.U16(sub + 4);
            var lookahead = sub + 6 + backtrack * 2;
            var lookaheadCount = t.U16(lookahead);
            var substitutes = lookahead + 2 + lookaheadCount * 2;
            if (c >= t.U16(substitutes)) return false;
            var tt = t;
            if (!shaper.MatchContext(work, run, lookup, i,
                    backtrack, (k, g) => Covers(tt, sub, sub + 6 + k * 2, g),
                    1, None,
                    lookaheadCount, (k, g) => Covers(tt, sub, lookahead + 2 + k * 2, g), out _))
                return false;
            Replace(i, t.U16(substitutes + 2 + c * 2));
            return true;
        }

        private void Replace(int i, int glyph)
        {
            var g = run[i];
            g.Glyph = glyph;
            g.Formed |= FormedSubstituted;
            g.Joiner = 0;   // a joiner the font replaces by a glyph of its own is positioned like any glyph (No. 1179)
            if (italic) g.Italic = true;
            run[i] = g;
        }

        private bool Ligature(Lookup lookup, int sub, int i, out int next)
        {
            next = i + 1;
            var t = T;
            var c = t.Coverage(sub + t.U16(sub + 2), run[i].Glyph);
            if (c < 0 || c >= t.U16(sub + 4)) return false;
            var set = sub + t.U16(sub + 6 + c * 2);
            for (var l = 0; l < t.U16(set); l++)
            {
                if (!work.Spend(1)) return false;
                var lig = set + t.U16(set + 2 + l * 2);
                var components = t.U16(lig + 2);
                // damaged: a ligature made of nothing; one of more glyphs than the rest of the run never matches (No. 1137)
                if (components == 0 || components > run.Count - i) continue;
                var positions = new int[components];
                positions[0] = i;
                var ok = true;
                for (var k = 1; k < components && ok; k++)
                {
                    var component = t.U16(lig + 4 + (k - 1) * 2);
                    positions[k] = shaper.NextComponent(run, lookup, positions[k - 1], component, work);
                    ok = positions[k] >= 0 && run[positions[k]].Glyph == component && Allowed(run[positions[k]]);
                }
                if (!ok || !work.Move(components * (run.Count - i))) continue;
                // the text of a joiner or format character passed over within the ligature goes into it, in text order (the glyph stays,
                // without a text of its own, and is drawn as nothing; No. 1183)
                var text = run[i].Text;
                for (var k = 1; k < components; k++)
                {
                    for (var p = positions[k - 1] + 1; p < positions[k]; p++)
                        if (run[p].Joiner is JoinerZwj or JoinerFormat or JoinerSelector && !shaper.SkipsByFlags(lookup, run[p]))
                        {
                            text += run[p].Text;
                            run[p] = run[p] with { Text = "", Glyph = 0 };
                        }
                    text += run[positions[k]].Text;
                }
                var (id, total) = shaper.TrackComponents(run, positions, work);
                var first = run[i];
                first.Glyph = t.U16(lig);
                first.Text = text;
                first.Formed |= FormedSubstituted | FormedLigated;
                first.Joiner = 0;
                if (id > 0)
                {
                    first.LigatureId = id;
                    first.LigatureComponent = 0;
                    first.LigatureComponents = total;
                    first.Class = 2;
                }
                run[i] = first;
                for (var k = components - 1; k >= 1; k--) run.RemoveAt(positions[k]);
                return true;
            }
            return false;
        }

        /// <summary>Contextual substitution (type 5) and chained contextual substitution (type 6), formats 1–3 each (<see cref="MatchContextSubtable"/>).</summary>
        private bool Context(Lookup lookup, int sub, int i, int depth, bool chained, out int next)
        {
            next = i + 1;
            return shaper.MatchContextSubtable(T, work, run, lookup, sub, i, chained, out var positions, out var records, out var count)
                && Apply(positions, records, count, depth, out next);
        }

        /// <summary>
        /// Applies the nested lookups at the input positions (SubstLookupRecords). A record names its place in the input sequence as the
        /// records before it left it (OpenType GSUB, 21.4.6): glyphs a multiple substitution adds after a place are places of their own,
        /// those a ligature joins into its first component are gone. Before, the places were counted in the sequence as matched, and a
        /// record after a ligature hit the wrong glyph (docs/FEHLERGEDAECHTNIS.md, No. 1142).
        /// </summary>
        private bool Apply(int[] matched, int records, int count, int depth, out int next)
        {
            var positions = new List<int>(matched);
            var end = matched[^1] + 1;
            for (var r = 0; r < count && work.Spend(1); r++)
            {
                var seq = T.U16(records + r * 4);
                var lookupIndex = T.U16(records + r * 4 + 2);
                if (seq >= positions.Count) continue;
                var before = run.Count;
                shaper.ApplyNested(this, lookupIndex, positions[seq], depth + 1);
                var delta = run.Count - before;
                if (delta == 0) continue;
                end += delta;
                work.Spend(positions.Count - seq + Math.Max(0, delta));
                if (delta > 0)
                {
                    // the new glyphs follow the place where they came from
                    for (var k = seq + 1; k < positions.Count; k++) positions[k] += delta;
                    positions.InsertRange(seq + 1, Enumerable.Range(positions[seq] + 1, delta));
                }
                else
                {
                    // joined into the glyph at this place: the places after it that it took are gone, the later ones move up
                    var gone = Math.Min(-delta, positions.Count - seq - 1);
                    positions.RemoveRange(seq + 1, gone);
                    for (var k = seq + 1; k < positions.Count; k++) positions[k] = Math.Max(positions[seq] + 1, positions[k] + delta);
                }
            }
            next = Math.Max(matched[0] + 1, end);
            return true;
        }

        internal void Nested(int lookupIndex, int position, int depth)
        {
            if (position < 0 || position >= run.Count) return;
            var lookup = shaper._gsub!.Lookup(lookupIndex);
            ApplyAt(lookup, position, depth, out _);
        }
    }

    private void ApplyNested(Substitution substitution, int lookupIndex, int position, int depth) =>
        substitution.Nested(lookupIndex, position, depth);

    // ------------------------------------------------------------ GPOS

    /// <param name="gate">The bits of <see cref="ShapedGlyph.Gates"/> one of which a glyph must carry for the lookup to apply at it (0: everywhere);
    /// for a pair or a cursive join both glyphs.</param>
    private readonly struct Positioning(TextShaper shaper, List<ShapedGlyph> run, Work work, byte gate = 0)
    {
        private TableData T => shaper._gpos!.T;

        /// <summary>The glyphs joined so far by cursive attachment with the RIGHT_TO_LEFT flag (one connected sequence; see <see cref="Join"/>).</summary>
        private readonly List<int> _chain = [];

        /// <summary>Whether the lookup may apply at a glyph (its gate, No. 1174).</summary>
        private bool Allowed(ShapedGlyph g) => gate == 0 || (g.Gates & gate) != 0;

        public void ApplyLookup(int index)
        {
            var lookup = shaper._gpos!.Lookup(index);
            for (var i = 0; i < run.Count && work.Left > 0;)
            {
                if (!Allowed(run[i]) || shaper.Skips(lookup, run[i]) || !ApplyAt(lookup, i, 0, out var next)) i++;
                else i = Math.Max(next, i + 1);
            }
        }

        private bool ApplyAt(Lookup lookup, int i, int depth, out int next)
        {
            next = i + 1;
            if (depth > 8 || !work.Take()) return false;
            foreach (var sub in lookup.Subtables)
                if (!work.Spend(1)) return false;
                else if (ApplySubtable(lookup, sub, i, depth, out next)) return true;
            return false;
        }

        private bool ApplySubtable(Lookup lookup, int sub, int i, int depth, out int next)
        {
            next = i + 1;
            var t = T;
            var glyph = run[i].Glyph;
            var format = t.U16(sub);
            switch (lookup.Type)
            {
                case 1:
                {
                    var c = t.Coverage(sub + t.U16(sub + 2), glyph);
                    // format 2: the coverage index names a value record (GPOS 20.4.2) — before, an index beyond them read what follows (No. 1144)
                    if (c < 0 || format is not (1 or 2) || format == 2 && c >= t.U16(sub + 6)) return false;
                    var valueFormat = t.U16(sub + 4);
                    var value = format == 1 ? sub + 6 : sub + 8 + c * ValueSize(valueFormat);
                    AddValue(i, sub, value, valueFormat);
                    return true;
                }
                case 2:
                {
                    var c = t.Coverage(sub + t.U16(sub + 2), glyph);
                    if (c < 0) return false;
                    var j = shaper.Next(run, lookup, i);
                    if (j < 0 || !Allowed(run[j])) return false;
                    int vf1 = t.U16(sub + 4), vf2 = t.U16(sub + 6);
                    int size1 = ValueSize(vf1), size2 = ValueSize(vf2);
                    var second = run[j].Glyph;
                    if (format == 1)
                    {
                        if (c >= t.U16(sub + 8)) return false;
                        var set = sub + t.U16(sub + 10 + c * 2);
                        var record = 2 + size1 + size2;
                        int lo = 0, hi = t.U16(set) - 1;
                        while (lo <= hi)
                        {
                            var mid = (lo + hi) >> 1;
                            var at = set + 2 + mid * record;
                            var g = t.U16(at);
                            if (g < second) lo = mid + 1;
                            else if (g > second) hi = mid - 1;
                            else
                            {
                                AddValue(i, set, at + 2, vf1);
                                AddValue(j, set, at + 2 + size1, vf2);
                                next = vf2 != 0 ? j + 1 : j;
                                return true;
                            }
                        }
                        return false;
                    }
                    if (format == 2)
                    {
                        var class1 = t.Class(t.U16(sub + 8) == 0 ? -1 : sub + t.U16(sub + 8), glyph);
                        var class2 = t.Class(t.U16(sub + 10) == 0 ? -1 : sub + t.U16(sub + 10), second);
                        int count1 = t.U16(sub + 12), count2 = t.U16(sub + 14);
                        if (class1 >= count1 || class2 >= count2) return false;
                        var at = sub + 16 + (class1 * count2 + class2) * (size1 + size2);
                        AddValue(i, sub, at, vf1);
                        AddValue(j, sub, at + size1, vf2);
                        next = vf2 != 0 ? j + 1 : j;
                        return true;
                    }
                    return false;
                }
                case 3:
                    return Cursive(lookup, sub, i);
                case 4 or 6:
                    return MarkToBase(lookup, sub, i, toMark: lookup.Type == 6);
                case 5:
                    return MarkToLigature(sub, i);
                case 7 or 8:
                    return Context(lookup, sub, i, depth, chained: lookup.Type == 8, out next);
                default:
                    return false;
            }
        }

        /// <summary>
        /// Contextual positioning (type 7) and chained contextual positioning (type 8), formats 1–3 each as in GSUB (<see
        /// cref="MatchContextSubtable"/>): the nested GPOS lookups of the records at their places in the input sequence. Positioning does not
        /// change the run, so a place stays where the match found it (unlike GSUB, No. 1142); nesting and work are bounded as for GSUB
        /// (No. 1137). Before, both types were passed over — the bundled Liberation fonts use type 8 for marks under „hebr“
        /// (docs/FEHLERGEDAECHTNIS.md, No. 1161).
        /// </summary>
        private bool Context(Lookup lookup, int sub, int i, int depth, bool chained, out int next)
        {
            next = i + 1;
            var t = T;
            if (!shaper.MatchContextSubtable(t, work, run, lookup, sub, i, chained, out var positions, out var records, out var count)) return false;
            for (var r = 0; r < count && work.Spend(1); r++)
            {
                var place = t.U16(records + r * 4);
                if (place >= positions.Length) continue;
                ApplyAt(shaper._gpos!.Lookup(t.U16(records + r * 4 + 2)), positions[place], depth + 1, out _);
            }
            next = positions[^1] + 1;
            return true;
        }

        private static int ValueSize(int format) => 2 * System.Numerics.BitOperations.PopCount((uint)(format & 0xFF));

        /// <summary>Adds a ValueRecord (offset and advance) to a glyph; device tables are ignored.</summary>
        private void AddValue(int i, int parent, int at, int format)
        {
            var t = T;
            var g = run[i];
            var k = 0;
            if ((format & 1) != 0) g.XOffset += t.I16(at + 2 * k++);
            if ((format & 2) != 0) g.YOffset += t.I16(at + 2 * k++);
            if ((format & 4) != 0) g.XAdvance += t.I16(at + 2 * k++);
            if ((format & 8) != 0) g.YAdvance += t.I16(at + 2 * k);
            run[i] = g;
        }

        /// <summary>An anchor (GPOS 20.4.11.2, formats 1–3: x and y; device and contour point data are not used); null for NULL or another format
        /// (before read as coordinates, No. 1144).</summary>
        private (int X, int Y)? Anchor(int at) => at == 0 || T.U16(at) is not (1 or 2 or 3) ? null : (T.I16(at + 2), T.I16(at + 4));

        /// <summary>
        /// Cursive attachment (lookup type 3, CursivePosFormat1): the exit anchor of the glyph aligned with the entry anchor of the following
        /// glyph the lookup does not skip, both from the EntryExit record of their coverage index; a NULL anchor on either side: no
        /// adjustment. Written from the OpenType specification (GPOS, „Lookup type 3 subtable: cursive attachment positioning“); no code of
        /// another shaper was used (the calculation taken over before was replaced on 16.11.2026; docs/LIZENZPRUEFUNG.md).
        /// </summary>
        private bool Cursive(Lookup lookup, int sub, int i)
        {
            var t = T;
            var cov = sub + t.U16(sub + 2);
            var c = t.Coverage(cov, run[i].Glyph);
            if (c < 0) return false;
            var j = shaper.Next(run, lookup, i);
            if (j < 0 || !Allowed(run[j])) return false;
            var cj = t.Coverage(cov, run[j].Glyph);
            if (cj < 0 || c >= t.U16(sub + 4) || cj >= t.U16(sub + 4)) return false;
            var exitAt = t.U16(sub + 6 + c * 4 + 2);
            var entryAt = t.U16(sub + 6 + cj * 4);
            if (Anchor(exitAt == 0 ? 0 : sub + exitAt) is not { } exit || Anchor(entryAt == 0 ? 0 : sub + entryAt) is not { } entry) return false;
            // a connected sequence moves as a whole at each join (No. 1137)
            if (!work.Spend(_chain.Count)) return false;
            Join(run, i, j, exit, entry, (lookup.Flag & RightToLeft) != 0, _chain);
            return true;
        }

        /// <summary>
        /// Attaches a mark to a base glyph (type 4) or to a preceding mark (type 6). The base is the nearest preceding glyph that is not a mark;
        /// mark2 of mark-to-mark the glyph before the mark that the lookup's flags do not skip (OpenType 20.4.7) — before always the glyph right
        /// before it, so a mark the flags pass over in between broke the attachment — and only a mark of the same base or of the same ligature
        /// component (docs/FEHLERGEDAECHTNIS.md, No. 1161).
        /// </summary>
        private bool MarkToBase(Lookup lookup, int sub, int i, bool toMark)
        {
            var t = T;
            var markIndex = t.Coverage(sub + t.U16(sub + 2), run[i].Glyph);
            if (markIndex < 0) return false;
            int b;
            if (toMark)
            {
                b = shaper.Previous(run, lookup, i, work);
                if (b < 0 || !SameComponent(run[i], run[b])) return false;
            }
            else
            {
                // the nearest glyph before the mark that is neither a mark nor a joiner (No. 1179)
                b = i - 1;
                while (b >= 0 && (shaper.ClassOf(run[b]) == 3 || run[b].Joiner != 0)) b--;
            }
            // the marks passed and, in Attach, the advances summed (No. 1137)
            if (b < 0 || !work.Spend(2 * (i - b))) return false;
            var baseIndex = t.Coverage(sub + t.U16(sub + 4), run[b].Glyph);
            if (baseIndex < 0) return false;
            var classCount = t.U16(sub + 6);
            var markArray = sub + t.U16(sub + 8);
            var baseArray = sub + t.U16(sub + 10);
            if (markIndex >= t.U16(markArray) || baseIndex >= t.U16(baseArray)) return false;
            var markRecord = markArray + 2 + markIndex * 4;
            var cls = t.U16(markRecord);
            if (cls >= classCount) return false;
            var markAnchor = t.U16(markRecord + 2) is var markOffset and > 0 ? Anchor(markArray + markOffset) : null;
            var baseAnchorOffset = t.U16(baseArray + 2 + (baseIndex * classCount + cls) * 2);
            if (markAnchor is not { } m || baseAnchorOffset == 0 || Anchor(baseArray + baseAnchorOffset) is not { } ba) return false;
            Attach(i, b, ba, m);
            return true;
        }

        /// <summary>
        /// Whether two marks may stack (mark-to-mark): both on the same base (no ligature), on the same component of one ligature, or one of them
        /// a ligature of marks itself — as common shaping engines decide, so that a mark of one component does not sit on a mark of another.
        /// </summary>
        private static bool SameComponent(ShapedGlyph mark1, ShapedGlyph mark2)
        {
            if (mark1.LigatureId == mark2.LigatureId) return mark1.LigatureId == 0 || mark1.LigatureComponent == mark2.LigatureComponent;
            return mark1.LigatureId > 0 && mark1.LigatureComponent == 0 || mark2.LigatureId > 0 && mark2.LigatureComponent == 0;
        }

        /// <summary>
        /// Attaches mark <paramref name="i"/> to glyph <paramref name="b"/> before it: its offsets become the difference of the anchors, relative
        /// to the base, and <see cref="ResolveAttachments"/> adds the base's final place after the last GPOS lookup — the mark is placed with
        /// respect to the final position of its base (OpenType 20.4.5), so a later lookup that moves or widens the base (dist, kern) moves the
        /// mark with it. Before, the base's place at the time of the attachment counted (docs/FEHLERGEDAECHTNIS.md, No. 1179).
        /// </summary>
        private void Attach(int i, int b, (int X, int Y) baseAnchor, (int X, int Y) markAnchor)
        {
            var g = run[i];
            g.XOffset = baseAnchor.X - markAnchor.X;
            g.YOffset = baseAnchor.Y - markAnchor.Y;
            g.AttachBack = i - b;   // for a line set right to left (No. 910)
            g.RelativeToBase = true;
            run[i] = g;
        }

        /// <summary>
        /// Attaches a mark to a ligature (lookup type 5, MarkLigPosFormat1; OpenType GPOS, „Lookup type 5 subtable: mark-to-ligature
        /// attachment positioning“): the nearest preceding glyph that is not a mark, with the anchor of the mark's class in the component the
        /// mark belongs to — tracked through GSUB (<see cref="TrackComponents"/>): a mark the ligature passed over takes the component before
        /// it, a mark after the ligature the last one. Before, every mark took the last component (No. 910; docs/FEHLERGEDAECHTNIS.md, No. 1161).
        /// </summary>
        private bool MarkToLigature(int sub, int i)
        {
            var t = T;
            var markIndex = t.Coverage(sub + t.U16(sub + 2), run[i].Glyph);
            if (markIndex < 0) return false;
            var b = i - 1;
            while (b >= 0 && (shaper.ClassOf(run[b]) == 3 || run[b].Joiner != 0)) b--;
            if (b < 0 || !work.Spend(2 * (i - b))) return false;
            var ligatureIndex = t.Coverage(sub + t.U16(sub + 4), run[b].Glyph);
            if (ligatureIndex < 0) return false;
            var classCount = t.U16(sub + 6);
            var markArray = sub + t.U16(sub + 8);
            var ligatureArray = sub + t.U16(sub + 10);
            if (classCount == 0 || markIndex >= t.U16(markArray) || ligatureIndex >= t.U16(ligatureArray)) return false;
            var markRecord = markArray + 2 + markIndex * 4;
            var cls = t.U16(markRecord);
            if (cls >= classCount) return false;
            var attach = ligatureArray + t.U16(ligatureArray + 2 + ligatureIndex * 2);
            var components = t.U16(attach);
            if (components == 0) return false;
            var mark = run[i];
            var component = mark.LigatureId != 0 && mark.LigatureId == run[b].LigatureId && mark.LigatureComponent > 0
                ? Math.Min(components, mark.LigatureComponent) - 1
                : components - 1;
            var anchorOffset = t.U16(attach + 2 + (component * classCount + cls) * 2);
            if (t.U16(markRecord + 2) == 0 || Anchor(markArray + t.U16(markRecord + 2)) is not { } m || anchorOffset == 0 || Anchor(attach + anchorOffset) is not { } la) return false;
            Attach(i, b, la, m);
            return true;
        }
    }

    /// <summary>
    /// Legacy kern table (OpenType 27, Microsoft version 0), used when GPOS has no "kern" lookups for the text: the horizontal subtables with
    /// kerning values of format 0 (sorted pairs) and format 2 (classes and a two-dimensional array, OpenType 27.2), in the order of the table;
    /// the values of a pair add up over the subtables, and a subtable with the override bit 3 replaces the sum so far. Passed over: vertical
    /// subtables (bit 0 clear — the text is set horizontally), subtables of minimum values (bit 1; there is no tracking they could limit) and
    /// cross-stream kerning (bit 2). A pair is two glyphs in a row with marks between them passed over (they sit on the first one). Each subtable
    /// passing the run counts as steps: the table can hold 65 535 overlapping subtables claiming 65 535 pairs each (No. 1137). Before, a
    /// subtable of minimum values was added as kerning, the override bit was not looked at and format 2 was not read
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1162).
    /// </summary>
    private void LegacyKerning(TableData kern, List<ShapedGlyph> run, Work work)
    {
        if (kern.U16(0) != 0) return;   // only the Microsoft version
        // the pairs: each glyph that is not a mark with the next such glyph
        var pairs = new List<(int First, int Second)>();
        for (var i = 0; i < run.Count; i++)
        {
            // marks sit on the glyph before them; joiners stand between neighbours that still form a pair (No. 1179)
            if (ClassOf(run[i]) == 3 || run[i].Joiner != 0) continue;
            var j = i + 1;
            while (j < run.Count && (ClassOf(run[j]) == 3 || run[j].Joiner != 0)) j++;
            // not at glyphs palt covers while palt is off (OpenType 66.1, No. 1174)
            if (j < run.Count && (run[i].Gates & GateKern) != 0 && (run[j].Gates & GateKern) != 0) pairs.Add((i, j));
            if (!work.Spend(1 + j - i)) return;
        }
        if (pairs.Count == 0) return;
        var values = new int[pairs.Count];
        var tables = kern.U16(2);
        var at = 4;
        for (var s = 0; s < tables && at < kern.Length; s++)
        {
            var length = kern.U16(at + 2);
            var coverage = kern.U16(at + 4);
            if (!work.Spend(1)) return;
            var format = coverage >> 8;
            if ((coverage & 0x7) == 1 && format is 0 or 2)
            {
                var replace = (coverage & 8) != 0;
                if (format == 0)
                {
                    var count = kern.U16(at + 6);
                    // a binary search per pair of glyphs
                    if (!work.Spend((long)pairs.Count * (1 + System.Numerics.BitOperations.Log2((uint)count + 1)))) return;
                    for (var p = 0; p < pairs.Count; p++)
                        if (Format0(kern, at, count, run[pairs[p].First].Glyph, run[pairs[p].Second].Glyph) is { } value)
                            values[p] = replace ? value : values[p] + value;
                }
                else
                {
                    if (!work.Spend(pairs.Count)) return;
                    // the length of a subtable is 16 bits: the last one (a large array) reaches to the end of the table
                    var end = s == tables - 1 ? kern.Length : Math.Min(kern.Length, at + Math.Max(length, 6));
                    for (var p = 0; p < pairs.Count; p++)
                        if (Format2(kern, at, end, run[pairs[p].First].Glyph, run[pairs[p].Second].Glyph) is { } value)
                            values[p] = replace ? value : values[p] + value;
                }
            }
            at += Math.Max(length, 6);
        }
        for (var p = 0; p < pairs.Count; p++)
        {
            var g = run[pairs[p].First];
            g.XAdvance += values[p];
            run[pairs[p].First] = g;
        }
    }

    /// <summary>The value of a pair in a format 0 subtable at <paramref name="at"/> (binary search over the sorted pairs); null if it has none.</summary>
    private static int? Format0(TableData kern, int at, int count, int left, int right)
    {
        var key = ((uint)left << 16) | (uint)right;
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var p = at + 14 + mid * 6;
            var k = ((uint)kern.U16(p) << 16) | (uint)kern.U16(p + 2);
            if (k < key) lo = mid + 1;
            else if (k > key) hi = mid - 1;
            else return kern.I16(p + 4);
        }
        return null;
    }

    /// <summary>
    /// The value of a pair in a format 2 subtable at <paramref name="at"/> (OpenType 27.2): the class values of both glyphs (left: a row offset,
    /// right: a column offset, both premultiplied) added to the start of the subtable — the left values include the offset of the array, as the
    /// fonts with such subtables and common shaping engines have it; a sum that falls before the array is taken from the array's start (the
    /// other reading of the specification's words). A glyph outside a class range is in class 0 (no kerning); a cell outside the subtable or
    /// beyond <paramref name="end"/> has no value. Null for no value.
    /// </summary>
    private static int? Format2(TableData kern, int at, int end, int left, int right)
    {
        int Class(int table, int glyph)
        {
            var first = kern.U16(table);
            var count = kern.U16(table + 2);
            return glyph >= first && glyph < first + count ? kern.U16(table + 4 + (glyph - first) * 2) : 0;
        }
        var array = kern.U16(at + 12);
        var l = Class(at + kern.U16(at + 8), left);
        var r = Class(at + kern.U16(at + 10), right);
        if (l == 0 && r == 0) return null;
        var cell = l + r < array ? array + l + r : l + r;
        if (array == 0 || at + cell + 2 > end) return null;
        return kern.I16(at + cell);
    }
}
