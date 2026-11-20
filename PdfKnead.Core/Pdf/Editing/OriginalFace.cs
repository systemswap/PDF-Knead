using System.Runtime.CompilerServices;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// The file's font for edited text: new text is laid out with it as far as it has the characters, so that it looks like the
/// original (a subset contains only the characters used; what is missing is drawn by the fallback font, scaled to its width).
/// Drawing and saving use the font's own codes and the file's font dictionary.
/// </summary>
/// <remarks>See KONZEPT.md, section 5.14.</remarks>
public sealed class OriginalFace
{
    private static readonly ConditionalWeakTable<PdfDictionary, OriginalFace> Faces = new();
    private readonly ConditionalWeakTable<ElementFace, object> _stretch = new();

    private OriginalFace(OriginalFont source, PdfFont font)
    {
        Source = source;
        Font = font;
        Codes = font.CodesByUnicode();
        Ascent = font.Ascent;
        Descent = font.Descent;
    }

    /// <summary>The font dictionary with its document (for the font, saving and data).</summary>
    public OriginalFont Source { get; }

    /// <summary>The parsed font.</summary>
    public PdfFont Font { get; }

    /// <summary>Code and code length in bytes per Unicode code point.</summary>
    public IReadOnlyDictionary<int, (uint Code, int Length)> Codes { get; }

    /// <summary>Ascender in em units (from the descriptor).</summary>
    public double Ascent { get; }

    /// <summary>Descender in em units (from the descriptor).</summary>
    public double Descent { get; }

    /// <summary>Gets the font for a font dictionary, created once per dictionary.</summary>
    /// <param name="source">The font dictionary with its document.</param>
    /// <returns>The font; <see langword="null"/> if it is unusable (vertical, no mapped characters) or unreadable. A Type 3 font is used as
    /// well: its widths and heights are already in text space (phase 8.1).</returns>
    public static OriginalFace? For(OriginalFont source)
    {
        if (Faces.TryGetValue(source.Font, out var known)) return known.Codes.Count > 0 ? known : null;
        OriginalFace face;
        try
        {
            var font = PdfFont.Get(source.Font, source.Document);
            if (font.Vertical) return null;
            face = new OriginalFace(source, font);
        }
        catch (Exception e) when (e is PdfException or InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
        Faces.AddOrUpdate(source.Font, face);
        return face.Codes.Count > 0 ? face : null;
    }

    /// <summary>Determines whether the font has a character.</summary>
    /// <param name="codePoint">The Unicode code point.</param>
    /// <returns><see langword="true"/> if a code maps to the character.</returns>
    public bool Has(int codePoint) => Codes.ContainsKey(codePoint);

    /// <summary>
    /// A font of drawn characters (Type 3): it draws a block only as a whole — if it lacks one character, the whole block takes the fallback
    /// font, since pixel or coloured glyphs beside outline glyphs look patched (KONZEPT.md, section 5.14, T4).
    /// </summary>
    public bool WholeBlockOnly => Font.IsType3;

    /// <summary>
    /// Whether the font can set a character: one it has, or for a font of drawn glyphs (<see cref="WholeBlockOnly"/>) also a space or tab it
    /// lacks — TeX writes such fonts without a space glyph and sets words apart by shifts; new text does the same (No. 611).
    /// </summary>
    /// <param name="codePoint">The Unicode code point.</param>
    /// <returns><see langword="true"/> if the character can be set with this font.</returns>
    public bool Sets(int codePoint) => Has(codePoint) || WholeBlockOnly && codePoint is ' ' or '\t';

    /// <summary>Advance of a space set as a shift (<see cref="Sets"/>), in em units: a third of an em, as between words when redacting.</summary>
    public const double ShiftedSpace = 1.0 / 3;

    /// <summary>Gets the advance of a code.</summary>
    /// <param name="code">The character code.</param>
    /// <param name="length">Length of the code in bytes.</param>
    /// <returns>The advance in em units.</returns>
    public double Width(uint code, int length) => Font.Glyph(code, length).Width;

    /// <summary>
    /// Gets the width ratio of this font to the fallback font (over the characters both have), so that a narrow font such as
    /// "Arial90" gets an equally narrow fallback for missing characters.
    /// </summary>
    /// <param name="fallback">The fallback font.</param>
    /// <returns>The horizontal scaling for the fallback, 0.5 … 2; 1 if the difference is below 3 %. Cached per fallback.</returns>
    public double StretchFor(ElementFace fallback)
    {
        if (_stretch.TryGetValue(fallback, out var known)) return (double)known;
        double original = 0, other = 0;
        foreach (var (codePoint, (code, length)) in Codes)
        {
            if (codePoint <= ' ' || fallback.Glyph(codePoint) is var glyph && glyph == 0) continue;
            var w = Width(code, length);
            if (w <= 0) continue;
            original += w;
            other += fallback.Advance(glyph);
        }
        var ratio = other > 0 && original > 0 ? Math.Clamp(original / other, 0.5, 2) : 1;
        if (Math.Abs(ratio - 1) < 0.03) ratio = 1;
        _stretch.AddOrUpdate(fallback, ratio);
        return ratio;
    }
}
