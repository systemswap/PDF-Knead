using System.Text;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Formatted text: per line, the spans with their font (Identity-H, code = glyph), size, colour, horizontal scaling (Tz) and
/// rise (Ts); kerning, character spacing and justification as adjustments in TJ. Fake bold is drawn with an outline (2 Tr),
/// fake italic as a slant (Tm). Underline and strikeout are filled rectangles in the span's colour.
/// </summary>
/// <remarks>See KONZEPT.md, section 5.14.</remarks>
public sealed partial class ElementAppearance
{
    /// <summary>Slant of fake italic (tan 12°, as in common PDF editors and office programs).</summary>
    private const double FakeItalicSkew = 0.2126;

    /// <summary>Outline width of fake bold, as a fraction of the font size.</summary>
    private const double FakeBoldStroke = 0.03;

    /// <summary>
    /// Resource names of the fonts from the file used in this appearance (edited text); the writer references them on saving.
    /// </summary>
    public IReadOnlyDictionary<PdfName, OriginalFont> OriginalFonts { get; private init; } = new Dictionary<PdfName, OriginalFont>();

    private static ElementAppearance BuildRich(RichTextElement element)
    {
        var layout = RichLayout.Of(element);
        var (_, h) = element.UprightSize;
        var faces = new Dictionary<ElementFace, (string Name, Dictionary<int, string> Glyphs)>(ReferenceEqualityComparer.Instance);
        // One resource name per font dictionary of the file and fake style: sharing one name between regular and fake bold made one of
        // the spans lose its font on saving
        var originals = new Dictionary<OriginalFont, string>();
        var alphas = new Dictionary<int, string>();
        var sb = new StringBuilder("q\nBT\n");
        var decorations = new StringBuilder();
        var spans = element.Content.Spans;
        // right-to-left text (Arabic, Hebrew): each line with the levels of the Unicode Bidirectional Algorithm the layout gave it, after L1
        // of that line (No. 910, 1173); spaces at the logical end of a line are invisible (L1 put them at the paragraph level) and left out
        // before reordering, so that a right-to-left line does not start with them
        var lines = layout.Lines.Select(line =>
        {
            var count = line.Glyphs.Count;
            while (count > 0 && line.Glyphs[count - 1].Text is " " or "\t") count--;
            var shown = count == line.Glyphs.Count ? line.Glyphs : line.Glyphs.Take(count).ToList();
            var units = TextUnits(shown);
            if (!shown.Any(g => g.Level % 2 == 1)) return (line.Baseline, Glyphs: shown, Spans: UnitSpans(shown, units, null));
            var (visual, order) = RightToLeft(shown);
            var spansOfLine = UnitSpans(visual, [.. order.Select(k => units[k])], order);
            return (line.Baseline, Glyphs: spansOfLine.Count == 0 ? visual : LettersFirst(visual, spansOfLine), Spans: spansOfLine);
        }).ToList();
        // the text each glyph of a font stands for in ToUnicode: the first it is drawn with; a glyph drawn later for another text (the body
        // beh, teh and yeh share without their dots) gets that text as /ActualText of its own (ISO 32000-2, 14.9.4) — a glyph of a font can
        // stand for one character in ToUnicode only (No. 910)
        var firstText = new Dictionary<(ElementFace Face, int Glyph), string>();
        for (var l = 0; l < lines.Count; l++)
        {
            var laid = lines[l].Glyphs;
            for (var k = 0; k < laid.Count; k++)
            {
                if (lines[l].Spans.TryGetValue(k, out var unit))
                {
                    // a glyph of a letter with its marks written within one /ActualText stands in ToUnicode for its own text — in a cluster of
                    // reordered marks, whose text is on one glyph, for the letter or mark of the cluster it is the glyph of (a reader without
                    // /ActualText then still reads the characters, in drawing order)
                    for (; k < unit.End; k++)
                    {
                        if (laid[k].Face is not { } cf) continue;
                        // (the glyph that carries the cluster text and is the glyph of none of its characters — a letter's form — stands for its letters)
                        var own = unit.Merged ? OwnCharacter(laid[k], cf, unit.Text) ?? (laid[k].Text.Length > 0 ? Letters(unit.Text) : null)
                            : UnicodeText(laid[k]) is { Length: > 0 } ownText ? ownText : null;
                        if (own is not null) firstText.TryAdd((cf, laid[k].Glyph), own);
                    }
                    k--;
                    continue;
                }
                var g = laid[k];
                if (g.Face is { } f && g.Original is null && UnicodeText(g) is { Length: > 0 } t) firstText.TryAdd((f, g.Glyph), t);
            }
        }
        // also a glyph without a text of its own (the further glyphs of a cluster: a reordered syllable, No. 1175) that stands for a text
        // elsewhere: an empty /ActualText keeps it from reading that text a second time
        bool Conflict(LaidGlyph g) =>
            g.Face is { } f && g.Original is null && UnicodeText(g) is { } t && firstText.TryGetValue((f, g.Glyph), out var known) && known != t;
        for (var l = 0; l < lines.Count; l++)
        {
            var (baseline, glyphs, units) = lines[l];
            var y = h - baseline;
            // Spaces at the end of a line are invisible (left out above); if drawn, copying would yield them twice across the line break
            // ("Hallo  Welt")
            var count = glyphs.Count;
            var clusterAt = -1;
            var clusterMerged = false;
            for (var i = 0; i < count;)
            {
                // a letter with its marks in a right-to-left run, and a cluster of reordered Arabic marks: one /ActualText with the text as
                // typed around all its glyphs (ISO 32000-2, 14.9.4) — drawn with the marks before their letter, or in the order AMTRA stacks
                // them, the glyphs would read the marks so (No. 1180, No. 1182)
                if (clusterAt < 0 && units.TryGetValue(i, out var unit))
                {
                    (clusterAt, clusterMerged) = (unit.End, unit.Merged);
                    sb.Append("/Span <</ActualText ").Append(TextStringHex(unit.Text)).Append(">> BDC\n");
                }
                // Same font, span and scaling: one text operator (glyphs with an offset, such as accents, stand alone; so does a glyph with a
                // text of its own)
                var start = i;
                var first = glyphs[i];
                // a colour glyph of COLR version 0 stands alone: drawn as its layers (No. 1168)
                var layers = ColourLayers(first);
                var own = layers is null && clusterAt < 0 && Conflict(first);
                i++;
                while (layers is null && !own && i < count && clusterAt < 0 && !units.ContainsKey(i) && glyphs[i].Span == first.Span &&
                       ReferenceEquals(glyphs[i].Face, first.Face) && ReferenceEquals(glyphs[i].Original, first.Original) &&
                       glyphs[i].CodeLength == first.CodeLength && glyphs[i].FakeBold == first.FakeBold && glyphs[i].FakeItalic == first.FakeItalic &&
                       glyphs[i].Stretch == first.Stretch && glyphs[i].XOffset == 0 && glyphs[i].YOffset == 0 && first.XOffset == 0 && first.YOffset == 0 &&
                       (clusterAt >= 0 || !Conflict(glyphs[i])) && ColourLayers(glyphs[i]) is null) i++;
                var style = spans[first.Span].Style;
                string name;
                if (first.Original is { } original)
                {
                    var key = original.Source with { FakeBold = first.FakeBold, FakeItalic = first.FakeItalic };
                    if (!originals.TryGetValue(key, out var known)) originals[key] = known = "O" + (originals.Count + 1);
                    name = known;
                }
                else
                {
                    if (!faces.TryGetValue(first.Face!, out var use)) faces[first.Face!] = use = ("F" + (faces.Count + 1), []);
                    // the layer glyphs of a colour glyph are drawn over it, without a text of their own (the colour glyph keeps its text)
                    if (layers is not null)
                        foreach (var layer in layers)
                            use.Glyphs.TryAdd(layer.Glyph, "");
                    // every glyph drawn — also one without a text of its own (the dots a font splits off a letter, marks): the saved subset holds
                    // the glyphs of this list, and without it a dot was not in the copy (No. 910); ToUnicode leaves out the empty texts
                    for (var k = start; k < i; k++)
                    {
                        var text = firstText.GetValueOrDefault((first.Face!, glyphs[k].Glyph), clusterAt >= 0 && clusterMerged ? "" : UnicodeText(glyphs[k]) ?? "");
                        if (!use.Glyphs.TryGetValue(glyphs[k].Glyph, out var had) || had.Length == 0 && text.Length > 0) use.Glyphs[glyphs[k].Glyph] = text;
                    }
                    name = use.Name;
                }
                if (layers is not null)
                {
                    AppendColourGlyph(sb, name, style, first, layers, y, alphas);
                }
                else
                {
                    if (own) sb.Append("/Span <</ActualText ").Append(TextStringHex(first.Text)).Append(">> BDC\n");
                    AppendRun(sb, name, style, glyphs, start, i, y, first.FakeBold, first.FakeItalic);
                    if (own) sb.Append("EMC\n");
                }
                if (clusterAt >= 0 && i >= clusterAt)
                {
                    sb.Append("EMC\n");
                    clusterAt = -1;
                }
                // the extent of the run as drawn (a mark of a right-to-left run may stand left of the glyph before it)
                double left = double.MaxValue, right = double.MinValue;
                for (var k = start; k < i; k++)
                {
                    left = Math.Min(left, glyphs[k].X);
                    right = Math.Max(right, glyphs[k].X + glyphs[k].Advance);
                }
                if ((style.Underline || style.Strikeout) && right > left) AppendDecoration(decorations, style, first.Face, left, y, right - left);
            }
        }
        sb.Append("ET\n").Append(decorations).Append("Q\n");

        var fonts = new PdfDictionary();
        foreach (var (face, use) in faces) fonts.Set(PdfName.Get(use.Name), face.DisplayFont);
        var originalFonts = new Dictionary<PdfName, OriginalFont>();
        foreach (var (font, name) in originals)
        {
            fonts.Set(PdfName.Get(name), font.Font);
            originalFonts[PdfName.Get(name)] = font;
        }
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("Font"), fonts);
        if (alphas.Count > 0)
        {
            // the alpha of the palette entries of colour glyphs (OpenType 11.2), as constant alpha for filling
            var states = new PdfDictionary();
            foreach (var (alpha, state) in alphas)
            {
                var gs = new PdfDictionary();
                gs.Set(PdfName.Get("ca"), new PdfReal(Math.Round(alpha / 255.0, 3)));
                states.Set(PdfName.Get(state), gs);
            }
            resources.Set(PdfName.Get("ExtGState"), states);
        }
        var form = FormStream(element, resources, Encoding.ASCII.GetBytes(sb.ToString()));
        var uses = faces.Select(f => (f.Key, (IReadOnlyDictionary<int, string>)f.Value.Glyphs)).ToList();
        return new ElementAppearance(element, form, uses.FirstOrDefault().Key, uses.Count > 0 ? uses[0].Item2 : NoGlyphs)
        {
            FaceUses = uses,
            OriginalFonts = originalFonts,
            // Fonts from the file: read them in the context of their document (font data, encryption)
            Source = originals.Count > 0 ? originals.Keys.First().Document : Document,
        };
    }

    /// <summary>
    /// A line with right-to-left text placed as the Unicode Bidirectional Algorithm orders it (UAX #9, L2, on the levels of
    /// <see cref="LaidGlyph.Level"/>): the glyphs in visual order from the line's left edge — as PDF producers write such text and readers
    /// expect it, restoring the reading order themselves; a mark stays at its base (<see cref="LaidGlyph.AttachBack"/>). The characters at odd
    /// levels were mirrored before shaping (L4, <see cref="RichLayout"/>). Before, an appearance with such text was not built at all
    /// (docs/FEHLERGEDAECHTNIS.md, No. 910, 1173).
    /// </summary>
    /// <returns>The glyphs in visual order and, per place, the index of its glyph in logical order.</returns>
    private static (IReadOnlyList<LaidGlyph> Glyphs, int[] Order) RightToLeft(IReadOnlyList<LaidGlyph> glyphs)
    {
        var n = glyphs.Count;
        if (n == 0) return (glyphs, []);
        var glyphLevels = new byte[n];
        for (var k = 0; k < n; k++) glyphLevels[k] = glyphs[k].Level;
        int[] order = [.. Core.Fonts.Layout.Bidi.VisualOrder(glyphLevels)];
        bool Attached(int k) => glyphs[k].AttachBack > 0 && k - glyphs[k].AttachBack >= 0;
        var x = new double[n];
        var pen = Enumerable.Range(0, n).Where(k => !Attached(k)).Select(k => glyphs[k].X).DefaultIfEmpty(glyphs[0].X).Min();
        foreach (var k in order)
        {
            if (Attached(k)) continue;
            x[k] = pen;
            pen += glyphs[k].Advance;
        }
        for (var k = 0; k < n; k++)
            if (Attached(k))
            {
                var b = k - glyphs[k].AttachBack;
                x[k] = x[b] + glyphs[k].X - glyphs[b].X;
            }
        var result = new List<LaidGlyph>(n);
        foreach (var k in order) result.Add(glyphs[k] with { X = x[k] });
        return (result, order);
    }

    /// <summary>A letter with its marks written within one /ActualText: the index after its last glyph (in drawing order), the text as typed and
    /// whether it is a cluster of reordered marks with its text on one glyph.</summary>
    private readonly record struct TextUnit(int End, string Text, bool Merged);

    /// <summary>No such letter with marks in a line.</summary>
    private static readonly Dictionary<int, TextUnit> NoUnits = [];

    /// <summary>
    /// The units of a line in logical order: a letter (or other base) with what is drawn for it after it — the glyphs of the same character
    /// (dots a font splits off, a cluster of reordered marks), the marks attached to it by the font (<see cref="LaidGlyph.AttachBack"/>) and the
    /// combining marks typed after it (CGJ among them), of the same span and font.
    /// </summary>
    /// <returns>Per glyph the number of its unit.</returns>
    private static int[] TextUnits(IReadOnlyList<LaidGlyph> glyphs)
    {
        var units = new int[glyphs.Count];
        for (var k = 1; k < glyphs.Count; k++)
        {
            var (g, before) = (glyphs[k], glyphs[k - 1]);
            var joins = g.Span == before.Span && ReferenceEquals(g.Face, before.Face) && ReferenceEquals(g.Original, before.Original)
                && (g.CharStart == before.CharStart || g.AttachBack > 0 || g.Text.Length > 0 && Core.Fonts.Layout.TextRunes.First(g.Text) is var r
                    && Rune.GetUnicodeCategory(r) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.EnclosingMark);
            units[k] = joins ? units[k - 1] : units[k - 1] + 1;
        }
        return units;
    }

    /// <summary>
    /// The units of a line (glyphs in drawing order, <paramref name="units"/> per glyph; <paramref name="logical"/> per glyph its index in logical
    /// order, null if the same) written within one /ActualText with their text as typed: a cluster of reordered Arabic marks — exactly one of its
    /// glyphs carries a text, the cluster text of the shaper (<see cref="Core.Fonts.Layout.TextShaper.IsMarkCluster"/>; UAX #53, No. 1180) — and,
    /// in a right-to-left run, a letter whose marks have texts of their own: drawn in visual order the marks stand before their letter (and often
    /// on another height), and the text read back put them there or on a line of their own (UAX #9, L2; No. 1182). Only glyphs of an own font.
    /// </summary>
    /// <returns>Index of each unit's first glyph → the unit.</returns>
    private static Dictionary<int, TextUnit> UnitSpans(IReadOnlyList<LaidGlyph> glyphs, int[] units, int[]? logical)
    {
        Dictionary<int, TextUnit>? result = null;
        for (var i = 0; i < glyphs.Count;)
        {
            var end = i + 1;
            while (end < glyphs.Count && units[end] == units[i]) end++;
            if (end - i >= 2 && glyphs[i].Face is not null && glyphs[i].Original is null)
            {
                var texts = 0;
                for (var k = i; k < end; k++)
                    if (glyphs[k].Text.Length > 0) texts++;
                var first = ClusterText(glyphs, i, end);
                if (texts == 1 && Core.Fonts.Layout.TextShaper.IsMarkCluster(first))
                    (result ??= [])[i] = new TextUnit(end, first, true);
                // only marks stacked on their letter (advancing nothing): marks drawn with an advance of their own (the placeholder glyph)
                // stand beside it like letters and read back in order already
                else if (texts >= 2 && glyphs[i].Level % 2 == 1 && Enumerable.Range(i, end - i).Count(k => Math.Abs(glyphs[k].Advance) > 1e-6) <= 1)
                    (result ??= [])[i] = new TextUnit(end, string.Concat(Enumerable.Range(i, end - i).OrderBy(k => logical?[k] ?? k).Select(k => UnicodeText(glyphs[k]))), false);
            }
            i = end;
        }
        return result ?? NoUnits;
    }

    /// <summary>
    /// The glyphs of a line with each unit written within one /ActualText drawn letter first (the glyph that advances, then its marks, each at its
    /// place): in visual order the marks of a right-to-left letter came before it, and a reader that spreads the replacement text over the glyphs
    /// of the sequence put the letter on a mark (No. 1182). Only the order of drawing changes, not where a glyph stands.
    /// </summary>
    private static IReadOnlyList<LaidGlyph> LettersFirst(IReadOnlyList<LaidGlyph> glyphs, Dictionary<int, TextUnit> units)
    {
        var result = glyphs.ToList();
        foreach (var (start, unit) in units)
        {
            var ordered = Enumerable.Range(start, unit.End - start).OrderBy(k => Math.Abs(glyphs[k].Advance) > 1e-6 ? 0 : 1).ThenBy(k => k).ToList();
            for (var k = 0; k < ordered.Count; k++) result[start + k] = glyphs[ordered[k]];
        }
        return result;
    }

    /// <summary>The characters of a text that are no combining marks; null if none is.</summary>
    private static string? Letters(string text)
    {
        var letters = string.Concat(text.EnumerateRunes().Where(r => Rune.GetUnicodeCategory(r) is not (System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.EnclosingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark)).Select(r => r.ToString()));
        return letters.Length > 0 ? letters : null;
    }

    /// <summary>The text of a cluster of reordered marks: that of its one glyph with a text (the first with one).</summary>
    private static string ClusterText(IReadOnlyList<LaidGlyph> glyphs, int start, int end)
    {
        for (var k = start; k < end; k++)
            if (glyphs[k].Text.Length > 0) return glyphs[k].Text;
        return "";
    }
    /// <summary>The letter or mark of a cluster text (decomposed) whose glyph in the font is the glyph of <paramref name="glyph"/>; null if none is.</summary>
    private static string? OwnCharacter(LaidGlyph glyph, ElementFace face, string clusterText)
    {
        foreach (var rune in clusterText.Normalize(NormalizationForm.FormD).EnumerateRunes())
            if (glyph.Glyph > 0 && face.Glyph(rune.Value) == glyph.Glyph) return rune.ToString();
        return null;
    }

    /// <summary>The layers of a glyph of an installed font that is a colour glyph of COLR version 0 (<see cref="Core.Fonts.ColrLayers"/>); null for any other.</summary>
    private static IReadOnlyList<Core.Fonts.ColrLayer>? ColourLayers(LaidGlyph glyph) =>
        glyph is { Face: { } face, Original: null, Glyph: > 0 } && Core.Fonts.ColrLayers.For(face.Font) is { } colour ? colour.Layers(glyph.Glyph) : null;
    /// <summary>
    /// The text a glyph stands for in ToUnicode: a tab is drawn with the space glyph and stands for a space there — before it had no text, so a
    /// block whose only white space was a tab drew a glyph with an advance and without a text, and was not the same block again after saving and
    /// opening (docs/FEHLERGEDAECHTNIS.md, No. 1178).
    /// </summary>
    private static string? UnicodeText(LaidGlyph glyph) => glyph.Text == "\t" ? " " : glyph.Text;


    /// <summary>
    /// A colour glyph of COLR version 0 as its layers (OpenType 10.2.5, 10.3.3): each layer glyph at the place of the base glyph, bottom layer first,
    /// filled with the colour of its palette entry — the text colour for entry 0xFFFF — and the alpha of the entry as constant alpha. The base glyph
    /// itself is shown invisibly (3 Tr) below them, so that copying, searching and marking find its text and place through ToUnicode; the layers have
    /// no text of their own, and /ActualText around the whole gives the text once (ISO 32000-2, 9.3.6, 14.9.4). Fake bold and fake italic are not
    /// applied (OpenType 10.1 sentences 24–26). Before, the base glyph was drawn with its outline in the text colour (docs/FEHLERGEDAECHTNIS.md,
    /// No. 1168).
    /// </summary>
    private static void AppendColourGlyph(StringBuilder sb, string font, RichStyle style, LaidGlyph glyph, IReadOnlyList<Core.Fonts.ColrLayer> layers,
        double y, Dictionary<int, string> alphas)
    {
        var text = glyph.Text is { Length: > 0 } t && t != "\t" ? t : null;
        if (text is not null) sb.Append("/Span <</ActualText ").Append(TextStringHex(text)).Append(">> BDC\n");
        AppendRun(sb, font, style, [glyph], 0, 1, y, fakeBold: false, fakeItalic: false, invisible: true);
        string State(int alpha)
        {
            if (!alphas.TryGetValue(alpha, out var name)) alphas[alpha] = name = "GA" + alpha.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return name;
        }
        var current = 255;
        foreach (var layer in layers)
        {
            if (layer.Alpha == 0) continue;
            if (layer.Alpha != current)
            {
                sb.Append('/').Append(State(layer.Alpha)).Append(" gs\n");
                current = layer.Alpha;
            }
            AppendRun(sb, font, style with { Color = layer.Rgb ?? style.Color }, [glyph with { Glyph = layer.Glyph }], 0, 1, y, fakeBold: false, fakeItalic: false);
        }
        if (current != 255) sb.Append('/').Append(State(255)).Append(" gs\n");
        if (text is not null) sb.Append("EMC\n");
    }

    /// <summary>A text string as a hexadecimal string in UTF-16BE with its byte order mark (7.9.2.2).</summary>
    private static string TextStringHex(string text)
    {
        var hex = new StringBuilder("<FEFF", 6 + text.Length * 4);
        foreach (var c in text) hex.Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
        return hex.Append('>').ToString();
    }

    private static void AppendRun(StringBuilder sb, string font, RichStyle style, IReadOnlyList<LaidGlyph> glyphs, int start, int end,
        double y, bool fakeBold, bool fakeItalic, bool invisible = false)
    {
        var size = Math.Max(0.5, style.Size);
        var stretch = glyphs[start].Stretch > 0.01 ? glyphs[start].Stretch : 1;
        var (r, g, b) = RgbParts(style.Color);
        sb.Append('/').Append(font).Append(' ').Append(ElementFace.Number(size)).Append(" Tf ")
          .Append(r).Append(' ').Append(g).Append(' ').Append(b).Append(" rg ");
        if (invisible)
            sb.Append("3 Tr ");
        else if (fakeBold)
            sb.Append(r).Append(' ').Append(g).Append(' ').Append(b).Append(" RG ").Append(ElementFace.Number(size * FakeBoldStroke)).Append(" w 2 Tr ");
        else
            sb.Append("0 Tr ");
        sb.Append(ElementFace.Number(stretch * 100)).Append(" Tz ").Append(ElementFace.Number(style.Rise)).Append(" Ts\n");
        var first = glyphs[start];
        sb.Append("1 0 ").Append(fakeItalic ? ElementFace.Number(FakeItalicSkew) : "0").Append(" 1 ")
          .Append(ElementFace.Number(first.X + first.XOffset)).Append(' ').Append(ElementFace.Number(y + first.YOffset)).Append(" Tm ");
        // TJ: where the desired advance (kerning, character spacing, justification) differs from the glyph's advance, write the difference
        sb.Append('[');
        var pending = new List<int>();
        var original = glyphs[start].Original;
        void Flush()
        {
            if (pending.Count == 0) return;
            if (original is null)
            {
                ElementFace.AppendHex(sb, pending);
            }
            else
            {
                // Codes of the file's font with their length (one or more bytes)
                sb.Append('<');
                foreach (var code in pending) sb.Append(((uint)code).ToString("X" + 2 * glyphs[start].CodeLength, System.Globalization.CultureInfo.InvariantCulture));
                sb.Append('>');
            }
            pending.Clear();
        }
        for (var k = start; k < end; k++)
        {
            if (original is not null && glyphs[k].Glyph < 0)
            {
                // a space the font of drawn glyphs lacks: a shift only (No. 611)
                Flush();
                sb.Append(ElementFace.Number(-glyphs[k].Advance / (size * stretch) * 1000));
                continue;
            }
            pending.Add(glyphs[k].Glyph);
            var natural = original is null ? glyphs[k].Face!.Width1000(glyphs[k].Glyph) : original.Width((uint)glyphs[k].Glyph, glyphs[k].CodeLength) * 1000;
            var adjust = natural - glyphs[k].Advance / (size * stretch) * 1000;
            if (Math.Abs(adjust) <= 0.05 || k + 1 == end) continue;
            Flush();
            sb.Append(ElementFace.Number(adjust));
        }
        Flush();
        sb.Append("] TJ\n");
    }

    /// <summary>Underline or strikeout: a filled rectangle in the span's colour (position and thickness from the font, scaled to the size).</summary>
    private static void AppendDecoration(StringBuilder sb, RichStyle style, ElementFace? face, double x, double y, double width)
    {
        var (r, g, b) = RgbParts(style.Color);
        // the font's underline (post) and strikeout (OS/2): position of the top of the stroke and its thickness (OpenType 31.2.14, 31.2.15,
        // 32.2); for a font from the file or one without the values the shares of the size used before (docs/FEHLERGEDAECHTNIS.md, No. 1162)
        var fallback = Math.Max(0.4, style.Size * 0.05);
        var (underTop, underThickness) = face?.Underline is { } u ? (u.Top * style.Size, u.Thickness * style.Size) : (-style.Size * 0.12 + fallback / 2, fallback);
        var (strikeTop, strikeThickness) = face?.Strikeout is { } s ? (s.Top * style.Size, s.Thickness * style.Size) : (style.Size * 0.28 + fallback / 2, fallback);
        sb.Append(r).Append(' ').Append(g).Append(' ').Append(b).Append(" rg\n");
        if (style.Underline)
            sb.Append(ElementFace.Number(x)).Append(' ').Append(ElementFace.Number(y + style.Rise + underTop - underThickness)).Append(' ')
              .Append(ElementFace.Number(width)).Append(' ').Append(ElementFace.Number(underThickness)).Append(" re f\n");
        if (style.Strikeout)
            sb.Append(ElementFace.Number(x)).Append(' ').Append(ElementFace.Number(y + style.Rise + strikeTop - strikeThickness)).Append(' ')
              .Append(ElementFace.Number(width)).Append(' ').Append(ElementFace.Number(strikeThickness)).Append(" re f\n");
    }

    private static (string R, string G, string B) RgbParts(int color) =>
        (ElementFace.Number(((color >> 16) & 0xFF) / 255.0), ElementFace.Number(((color >> 8) & 0xFF) / 255.0), ElementFace.Number((color & 0xFF) / 255.0));
}
