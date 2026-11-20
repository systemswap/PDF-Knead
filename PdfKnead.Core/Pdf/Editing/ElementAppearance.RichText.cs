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
        var sb = new StringBuilder("q\nBT\n");
        var decorations = new StringBuilder();
        var spans = element.Content.Spans;
        // right-to-left text (Arabic, Hebrew): the levels of the Unicode Bidirectional Algorithm (No. 910)
        var levels = Core.Fonts.Layout.Bidi.HasRightToLeft(element.Content.Text) ? Core.Fonts.Layout.Bidi.Levels(element.Content.Text) : null;
        var lines = layout.Lines.Select(line => (line.Baseline, Glyphs: levels is null ? line.Glyphs : RightToLeft(line.Glyphs, levels))).ToList();
        // the text each glyph of a font stands for in ToUnicode: the first it is drawn with; a glyph drawn later for another text (the body
        // beh, teh and yeh share without their dots) gets that text as /ActualText of its own (ISO 32000-2, 14.9.4) — a glyph of a font can
        // stand for one character in ToUnicode only (No. 910)
        var firstText = new Dictionary<(ElementFace Face, int Glyph), string>();
        foreach (var (_, laid) in lines)
            foreach (var g in laid)
                if (g.Face is { } f && g.Original is null && g.Text is { Length: > 0 } t && t != "\t") firstText.TryAdd((f, g.Glyph), t);
        bool Conflict(LaidGlyph g) =>
            g.Face is { } f && g.Original is null && g.Text is { Length: > 0 } t && t != "\t" && firstText.TryGetValue((f, g.Glyph), out var known) && known != t;
        foreach (var (baseline, glyphs) in lines)
        {
            var y = h - baseline;
            // Spaces at the end of a line are invisible; if drawn, copying would yield them twice across the line break ("Hallo  Welt")
            var count = glyphs.Count;
            while (count > 0 && glyphs[count - 1].Text is " " or "\t") count--;
            for (var i = 0; i < count;)
            {
                // Same font, span and scaling: one text operator (glyphs with an offset, such as accents, stand alone; so does a glyph with a
                // text of its own)
                var start = i;
                var first = glyphs[i];
                var own = Conflict(first);
                i++;
                while (!own && i < count && glyphs[i].Span == first.Span && ReferenceEquals(glyphs[i].Face, first.Face) && ReferenceEquals(glyphs[i].Original, first.Original) &&
                       glyphs[i].CodeLength == first.CodeLength && glyphs[i].FakeBold == first.FakeBold && glyphs[i].FakeItalic == first.FakeItalic &&
                       glyphs[i].Stretch == first.Stretch && glyphs[i].XOffset == 0 && glyphs[i].YOffset == 0 && first.XOffset == 0 && first.YOffset == 0 &&
                       !Conflict(glyphs[i])) i++;
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
                    // every glyph drawn — also one without a text of its own (the dots a font splits off a letter, marks): the saved subset holds
                    // the glyphs of this list, and without it a dot was not in the copy (No. 910); ToUnicode leaves out the empty texts
                    for (var k = start; k < i; k++)
                    {
                        var text = firstText.GetValueOrDefault((first.Face!, glyphs[k].Glyph), glyphs[k].Text is { Length: > 0 } t && t != "\t" ? t : "");
                        if (!use.Glyphs.TryGetValue(glyphs[k].Glyph, out var had) || had.Length == 0 && text.Length > 0) use.Glyphs[glyphs[k].Glyph] = text;
                    }
                    name = use.Name;
                }
                if (own) sb.Append("/Span <</ActualText ").Append(TextStringHex(first.Text)).Append(">> BDC\n");
                AppendRun(sb, name, style, glyphs, start, i, y, first.FakeBold, first.FakeItalic);
                if (own) sb.Append("EMC\n");
                // the extent of the run as drawn (a mark of a right-to-left run may stand left of the glyph before it)
                double left = double.MaxValue, right = double.MinValue;
                for (var k = start; k < i; k++)
                {
                    left = Math.Min(left, glyphs[k].X);
                    right = Math.Max(right, glyphs[k].X + glyphs[k].Advance);
                }
                if ((style.Underline || style.Strikeout) && right > left) AppendDecoration(decorations, style, left, y, right - left);
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
    /// A line with right-to-left text placed as the Unicode Bidirectional Algorithm orders it (UAX #9, L2): the glyphs in visual order from
    /// the line's left edge — as PDF producers write such text and readers expect it, restoring the reading order themselves; a mark stays at
    /// its base (<see cref="LaidGlyph.AttachBack"/>); brackets in a right-to-left run are mirrored (L4) where the font has the counterpart.
    /// Before, an appearance with such text was not built at all (docs/FEHLERGEDAECHTNIS.md, No. 910).
    /// </summary>
    private static IReadOnlyList<LaidGlyph> RightToLeft(IReadOnlyList<LaidGlyph> glyphs, byte[] levels)
    {
        var n = glyphs.Count;
        if (n == 0) return glyphs;
        var glyphLevels = new byte[n];
        var odd = false;
        for (var k = 0; k < n; k++)
        {
            var at = glyphs[k].CharStart;
            glyphLevels[k] = at >= 0 && at < levels.Length ? levels[at] : (byte)0;
            odd |= glyphLevels[k] % 2 == 1;
        }
        if (!odd) return glyphs;
        var order = Core.Fonts.Layout.Bidi.VisualOrder(glyphLevels);
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
        foreach (var k in order)
        {
            var g = glyphs[k] with { X = x[k] };
            if (glyphLevels[k] % 2 == 1 && g.Face is { } face && g.Text is { Length: > 0 } text && char.ConvertToUtf32(text, 0) is var c &&
                Core.Fonts.Layout.Bidi.Mirrored(c) is var mirrored && mirrored != c && face.Glyph(mirrored) is var glyph and not 0)
                g = g with { Glyph = glyph, Text = char.ConvertFromUtf32(mirrored) };
            result.Add(g);
        }
        return result;
    }

    /// <summary>A text string as a hexadecimal string in UTF-16BE with its byte order mark (7.9.2.2).</summary>
    private static string TextStringHex(string text)
    {
        var hex = new StringBuilder("<FEFF", 6 + text.Length * 4);
        foreach (var c in text) hex.Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
        return hex.Append('>').ToString();
    }

    private static void AppendRun(StringBuilder sb, string font, RichStyle style, IReadOnlyList<LaidGlyph> glyphs, int start, int end,
        double y, bool fakeBold, bool fakeItalic)
    {
        var size = Math.Max(0.5, style.Size);
        var stretch = glyphs[start].Stretch > 0.01 ? glyphs[start].Stretch : 1;
        var (r, g, b) = RgbParts(style.Color);
        sb.Append('/').Append(font).Append(' ').Append(ElementFace.Number(size)).Append(" Tf ")
          .Append(r).Append(' ').Append(g).Append(' ').Append(b).Append(" rg ");
        if (fakeBold)
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

    /// <summary>Underline or strikeout: a filled rectangle in the span's colour (position and thickness as in office programs, relative to the font size).</summary>
    private static void AppendDecoration(StringBuilder sb, RichStyle style, double x, double y, double width)
    {
        var (r, g, b) = RgbParts(style.Color);
        var thickness = Math.Max(0.4, style.Size * 0.05);
        sb.Append(r).Append(' ').Append(g).Append(' ').Append(b).Append(" rg\n");
        if (style.Underline)
            sb.Append(ElementFace.Number(x)).Append(' ').Append(ElementFace.Number(y + style.Rise - style.Size * 0.12 - thickness / 2)).Append(' ')
              .Append(ElementFace.Number(width)).Append(' ').Append(ElementFace.Number(thickness)).Append(" re f\n");
        if (style.Strikeout)
            sb.Append(ElementFace.Number(x)).Append(' ').Append(ElementFace.Number(y + style.Rise + style.Size * 0.28 - thickness / 2)).Append(' ')
              .Append(ElementFace.Number(width)).Append(' ').Append(ElementFace.Number(thickness)).Append(" re f\n");
    }

    private static (string R, string G, string B) RgbParts(int color) =>
        (ElementFace.Number(((color >> 16) & 0xFF) / 255.0), ElementFace.Number(((color >> 8) & 0xFF) / 255.0), ElementFace.Number((color & 0xFF) / 255.0));
}
