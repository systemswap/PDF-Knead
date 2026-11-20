using PdfKnead.Core.Pdf.Fonts;

namespace PdfKnead.Core.Pdf.Content;

/// <summary>
/// Notices characters whose read text is suspicious (docs/RECHERCHE-VERSTECKEN.md, B-5): a font that maps many different shapes to the same
/// text (all to „x“ or to a space), or many different shapes to the Private Use Area. A search does not find what such characters show, or
/// finds it elsewhere — a page with them is named in „Suchen und schwärzen“. Whether the shapes show what the text says is not checked
/// (that needs text recognition).
/// </summary>
/// <remarks>
/// A shape is told apart by the bounds of its outline in text space (rounded to 1/100 em) and the number of its path steps; letters of
/// the same size count once, which errs on the side of no finding. Ordinary fonts read the same text from at most two or three shapes
/// (small capitals, old-style figures, swashes); <see cref="SameText"/> and more give a finding. Bullets and icons of symbol fonts use few
/// shapes from the Private Use Area; text set in it uses many (<see cref="PrivateUse"/>). Text that stands for „no character“ (U+FFFD, U+FFFF,
/// U+0000: a broken mapping) is suspicious from the first shape.
/// </remarks>
internal sealed class TextReading
{
    /// <summary>Different shapes reading as the same text from which the text counts as suspicious.</summary>
    internal const int SameText = 4;

    /// <summary>Different shapes of one font reading as characters of the Private Use Area from which they count as suspicious.</summary>
    internal const int PrivateUse = 6;

    /// <summary>Keys kept per font and text before the shapes stop being counted (enough for the decision).</summary>
    private const int Enough = 8;

    /// <summary>The key under which all characters of the Private Use Area of a font are counted together.</summary>
    private const string PrivateUseKey = "\uE000";

    /// <summary>The key of the characters that read as „no character“ (<see cref="NoCharacter"/>); suspicious from the first shape.</summary>
    private const string NoCharacterKey = "\uFFFD";

    private readonly Dictionary<(PdfFont Font, string Text), Reading> _readings = [];

    /// <summary>The reading of each character already noted (fonts hand out the same character object for the same code): counted without
    /// working out its text and shape again.</summary>
    private readonly Dictionary<PdfGlyph, Reading> _seen = new(ReferenceEqualityComparer.Instance);

    private sealed class Reading
    {
        public readonly HashSet<(int, int, int, int, int)> Shapes = [];
        public int Glyphs;
    }

    /// <summary>Notes a visible character with its read text.</summary>
    /// <param name="font">The font.</param>
    /// <param name="glyph">The character (with outline or procedure).</param>
    /// <param name="text">Its read text (not empty).</param>
    public void Add(PdfFont font, PdfGlyph glyph, string text)
    {
        if (_seen.TryGetValue(glyph, out var known))
        {
            known.Glyphs++;
            return;
        }
        if (_seen.Count > 50_000) return;   // a page with that many different characters: no further bookkeeping
        var key = (font, NoCharacter(text) ? NoCharacterKey : PrivateUseOnly(text) ? PrivateUseKey : string.IsNullOrWhiteSpace(text) ? " " : text);
        if (!_readings.TryGetValue(key, out var reading)) _readings[key] = reading = new Reading();
        _seen[glyph] = reading;
        reading.Glyphs++;
        if (reading.Shapes.Count >= Enough) return;
        if (glyph.Procedure is { } procedure)
        {
            reading.Shapes.Add((System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(procedure), 0, 0, 0, -1));
            return;
        }
        var bounds = glyph.ToText.TransformBounds(glyph.Outline.Bounds);
        reading.Shapes.Add(((int)Math.Round(bounds.Left * 100), (int)Math.Round(bounds.Top * 100), (int)Math.Round(bounds.Right * 100),
            (int)Math.Round(bounds.Bottom * 100), glyph.Outline.VerbSpan.Length));
    }

    /// <summary>The characters whose read text is suspicious, over everything noted.</summary>
    public int Misread
    {
        get
        {
            var count = 0;
            foreach (var ((_, text), reading) in _readings)
                if (reading.Shapes.Count >= (text == NoCharacterKey ? 1 : text == PrivateUseKey ? PrivateUse : SameText)) count += reading.Glyphs;
            return count;
        }
    }

    /// <summary>Text that stands for „no character“ (U+FFFD, U+FFFF, U+0000): a broken mapping.</summary>
    private static bool NoCharacter(string text)
    {
        foreach (var c in text)
            if (c is not ('\uFFFD' or '\uFFFF' or '\0')) return false;
        return true;
    }

    private static bool PrivateUseOnly(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                if (char.ConvertToUtf32(c, text[++i]) < 0xF0000) return false;   // planes 15 and 16 are private use
                continue;
            }
            // U+F8E5 … U+F8FF: pieces of large brackets and braces and the sans-serif signs of the Symbol font, as the Adobe Glyph List
            // assigns them (formula fonts use many of them) — ordinary characters with a meaning of their own
            if (c is < '\uE000' or > '\uF8FF' or >= '\uF8E5') return false;
        }
        return text.Length > 0;
    }
}
