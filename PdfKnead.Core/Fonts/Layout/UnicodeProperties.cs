namespace PdfKnead.Core.Fonts.Layout;

/// <summary>
/// Character properties of the Unicode Character Database the shaper needs besides the script and the syllabic classes
/// (docs/FEHLERGEDAECHTNIS.md, No. 1183): Default_Ignorable_Code_Point (generated from DerivedCoreProperties.txt 16.0.0,
/// <see cref="IgnorableRanges"/>) and the prepended concatenation marks.
/// </summary>
internal static partial class UnicodeProperties
{
    /// <summary>
    /// Whether a code point has the property Default_Ignorable_Code_Point: format controls (ZWSP, ZWNJ, ZWJ, WJ, the bidirectional controls,
    /// U+FEFF), the soft hyphen, CGJ, the Hangul fillers, Khmer U+17B4/U+17B5, the variation selectors, the Mongolian selectors and MVS, the
    /// musical beam controls, the tags and the reserved code points among them — characters that are drawn invisibly when they are not
    /// supported otherwise (The Unicode Standard, chapter 23).
    /// </summary>
    internal static bool IsDefaultIgnorable(int codePoint)
    {
        int lo = 0, hi = IgnorableRanges.Length / 2 - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (codePoint < IgnorableRanges[mid * 2]) hi = mid - 1;
            else if (codePoint > IgnorableRanges[mid * 2 + 1]) lo = mid + 1;
            else return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a code point is a prepended concatenation mark (property Prepended_Concatenation_Mark: the Arabic number signs U+0600–U+0605,
    /// the end of ayah U+06DD, U+0890–U+0891, U+08E2, the Syriac abbreviation mark U+070F, the Kaithi number signs U+110BD, U+110CD): the unit
    /// it forms with the digits or letters after it ends at the first other character — even a default ignorable one breaks it (The Unicode
    /// Standard, 23.2.3). The list is the whole property in PropList.txt of Unicode 16.0.0 (compared on 27.11.2026).
    /// </summary>
    internal static bool IsPrependedConcatenationMark(int codePoint) =>
        codePoint is >= 0x0600 and <= 0x0605 or 0x06DD or 0x070F or 0x0890 or 0x0891 or 0x08E2 or 0x110BD or 0x110CD;
}
