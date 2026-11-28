using System.Globalization;
using System.Text;

namespace PdfKnead.Core.Fonts.Layout;

/// <summary>
/// Fractions with U+2044 FRACTION SLASH (docs/FEHLERGEDAECHTNIS.md, No. 1183). Written from the Unicode Standard (23.2.3) and the OpenType feature
/// registry (frac, numr, dnom); no code of another shaper was used.
/// </summary>
public sealed partial class TextShaper
{
    /// <summary>
    /// U+2044 between two unbroken sequences of decimal digits (general category Nd) forms a fraction (The Unicode Standard 23.2.3): the
    /// digits before it take numr, the digits after it dnom, all of them and the slash frac — each where the font has the feature (OpenType
    /// numr, dnom, frac), applied in LookupList order with the other features, only at the glyphs of the fraction (<see cref="GateNumr"/> …).
    /// Any other character between the digits, even a default ignorable one such as WJ, ends the sequence. A feature the caller switches on
    /// applies everywhere; one switched off does not apply. Before, the digits and the slash kept their nominal glyphs.
    /// </summary>
    /// <returns>The options of the run with numr, dnom and frac switched on where a fraction was found.</returns>
    private static RunOptions MarkFractions(List<ShapedGlyph> run, List<int> codePoints, RunOptions options)
    {
        foreach (var (feature, gate) in new[] { ("numr", GateNumr), ("dnom", GateDnom), ("frac", GateFrac) })
            if (options.Substitutions.Contains(feature) || options.Positioning.Contains(feature))
                for (var i = 0; i < run.Count; i++) run[i] = run[i] with { Gates = (byte)(run[i].Gates | gate) };
        if (!codePoints.Contains(0x2044)) return options;
        static bool Digit(int c) => Rune.IsValid(c) && Rune.GetUnicodeCategory(new Rune(c)) == UnicodeCategory.DecimalDigitNumber;
        void Add(int i, byte gate) => run[i] = run[i] with { Gates = (byte)(run[i].Gates | gate) };
        var found = false;
        for (var i = 1; i + 1 < codePoints.Count; i++)
        {
            if (codePoints[i] != 0x2044 || !Digit(codePoints[i - 1]) || !Digit(codePoints[i + 1])) continue;
            for (var k = i - 1; k >= 0 && Digit(codePoints[k]); k--) Add(k, GateNumr | GateFrac);
            Add(i, GateFrac);
            var after = i + 1;
            for (; after < codePoints.Count && Digit(codePoints[after]); after++) Add(after, GateDnom | GateFrac);
            found = true;
            i = after - 1;
        }
        if (!found) return options;
        var substitutions = new HashSet<string>(options.Substitutions, StringComparer.Ordinal);
        foreach (var feature in new[] { "numr", "dnom", "frac" })
            if (!options.Disabled.Contains(feature)) substitutions.Add(feature);
        return options with { Substitutions = substitutions };
    }
}
