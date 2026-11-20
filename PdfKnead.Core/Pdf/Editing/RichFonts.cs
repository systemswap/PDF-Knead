using System.Buffers.Binary;
using PdfKnead.Core.Fonts;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Decides which font draws a span of formatted text: the system font of the same name if it is installed, its licence permits
/// embedding and it is a single file (not a collection) — otherwise the matching style of Liberation (Sans, Serif or Mono;
/// Liberation Sans has the same widths as Arial/Helvetica). If the bold or italic style is missing, it is faked (outline or
/// slant).
/// </summary>
/// <remarks>See KONZEPT.md, section 5.14. Thread-safe.</remarks>
public static class RichFonts
{
    /// <summary>The chosen font.</summary>
    /// <param name="Face">The font.</param>
    /// <param name="FakeBold">The bold style is missing and is faked.</param>
    /// <param name="FakeItalic">The italic style is missing and is faked.</param>
    /// <param name="IsSystemFont">The font is an installed system font, not a font shipped with the program.</param>
    public readonly record struct Resolved(ElementFace Face, bool FakeBold, bool FakeItalic, bool IsSystemFont);

    // Only the choice (key of the font and style), not the font itself: ElementFace keeps that within a budget
    private static readonly Dictionary<(string Family, bool Bold, bool Italic), (string Face, bool FakeBold, bool FakeItalic, bool IsSystemFont)> Cache = [];
    private static readonly Lock Gate = new();

    /// <summary>For tests only: no system fonts (always Liberation).</summary>
    [ThreadStatic] internal static bool SystemFontsOff;

    /// <summary>Chooses the font for a style; the choice is cached per family and style.</summary>
    /// <param name="style">The span's style.</param>
    /// <returns>The font and whether bold or italic must be faked.</returns>
    public static Resolved For(RichStyle style)
    {
        var key = (style.Family, style.Bold, style.Italic);
        if (!SystemFontsOff)
            lock (Gate)
                if (Cache.TryGetValue(key, out var known) && ElementFace.Cached(known.Face) is { } face)
                    return new Resolved(face, known.FakeBold, known.FakeItalic, known.IsSystemFont);
        var resolved = (SystemFontsOff ? null : System(style.Family, style.Bold, style.Italic)) ?? Liberation(style.Family, style.Bold, style.Italic);
        if (!SystemFontsOff)
            lock (Gate)
            {
                if (Cache.Count >= 4096) Cache.Clear();
                Cache[key] = (resolved.Face.Key!, resolved.FakeBold, resolved.FakeItalic, resolved.IsSystemFont);
            }
        return resolved;
    }

    /// <summary>
    /// Gets the families to choose from: the three Liberation fonts (always in the program), then the system fonts that new text
    /// may use (single file, embedding permitted).
    /// </summary>
    /// <returns>The family names; only Liberation if the font folders cannot be read.</returns>
    /// <remarks>The first call searches the font folders; do not call it on the UI thread.</remarks>
    public static IReadOnlyList<string> Families()
    {
        string[] own = ["Liberation Sans", "Liberation Serif", "Liberation Mono"];
        if (SystemFontsOff) return own;
        IReadOnlyList<string> system;
        try
        {
            system = SystemFonts.Families();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            system = [];
        }
        return [.. own, .. system.Where(f => !f.StartsWith("Liberation", StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// Gets the fallback for Chinese, Japanese and Korean characters that neither the font nor Liberation has: Noto Sans/Serif
    /// CJK from the folder "Schriften-CJK" (<see cref="CjkFonts"/>). The language variant follows the character (Hangul, kana)
    /// and, for Han characters, the UI language, because the variants differ in the shape of some characters.
    /// </summary>
    /// <param name="codePoint">The character.</param>
    /// <param name="style">The span's style (serif or sans; bold and italic are faked).</param>
    /// <returns>The font; <see langword="null"/> if the folder or the font is missing or the font lacks the character.</returns>
    public static Resolved? Cjk(int codePoint, RichStyle style)
    {
        var language = global::System.Globalization.CultureInfo.CurrentUICulture.Name;
        var ordering = codePoint switch
        {
            >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F => "Korea1",
            >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F => "Japan1",
            _ when language is "zh-TW" or "zh-HK" or "zh-MO" || language.StartsWith("zh-Hant", StringComparison.Ordinal) => "CNS1",
            _ when language.StartsWith("zh", StringComparison.Ordinal) => "GB1",
            _ when language.StartsWith("ko", StringComparison.Ordinal) => "Korea1",
            _ => "Japan1",
        };
        var serif = Kind(style.Family).Serif;
        if (CjkFonts.Get(ordering, serif) is not { } font) return null;
        var face = ElementFace.For($"cjk:{ordering}:{(serif ? "serif" : "sans")}", () => font, false, serif, false);
        return face.Glyph(codePoint) != 0 ? new Resolved(face, style.Bold, style.Italic, false) : null;
    }

    /// <summary>
    /// Gets the fallback for characters of Arabic script that neither the font nor Liberation has: Noto Naskh Arabic for serif families, Noto
    /// Sans Arabic otherwise (shipped in the program, OFL; bold as its own style, italic faked). Before, such text was not drawn — an appearance
    /// built for a free text on Arabic stayed empty (test file freetext_no_appearance; docs/FEHLERGEDAECHTNIS.md, No. 910).
    /// </summary>
    /// <param name="codePoint">The character.</param>
    /// <param name="style">The span's style.</param>
    /// <returns>The font; <see langword="null"/> if the character is not of Arabic script or the font lacks it.</returns>
    public static Resolved? Arabic(int codePoint, RichStyle style)
    {
        if (codePoint is not (>= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0x0870 and <= 0x08FF or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF))
            return null;
        var serif = Kind(style.Family).Serif;
        var file = (serif ? "NotoNaskhArabic-" : "NotoSansArabic-") + (style.Bold ? "Bold" : "Regular") + ".ttf";
        var face = ElementFace.For("arabic:" + file, () => EmbeddedFonts.ArabicOpenType(file), false, serif, false);
        return face.Glyph(codePoint) != 0 ? new Resolved(face, false, style.Italic, false) : null;
    }

    /// <summary>Gets the name of the fallback font for a family ("Liberation Serif" for "Times New Roman").</summary>
    /// <param name="family">The family name.</param>
    /// <returns>Liberation Sans, Serif or Mono.</returns>
    public static string Fallback(string family)
    {
        var (serif, mono) = Kind(family);
        return mono ? "Liberation Mono" : serif ? "Liberation Serif" : "Liberation Sans";
    }

    /// <summary>Classifies a family for the fallback: serif or fixed pitch (otherwise sans serif), judged from its name.</summary>
    /// <param name="family">The family name.</param>
    /// <returns>Whether the family is a serif or a monospaced font.</returns>
    public static (bool Serif, bool Mono) Kind(string family)
    {
        var key = SystemFonts.FamilyKey(family);
        string[] mono = ["courier", "consolas", "mono", "lucidaconsole", "menlo", "inconsolata", "typewriter", "sourcecode"];
        string[] serif = ["times", "georgia", "garamond", "cambria", "bookman", "palatino", "century", "serif", "roman", "minion", "baskerville",
            "didot", "constantia", "bodoni", "caslon", "charter", "utopia"];
        if (mono.Any(key.Contains)) return (false, true);
        return (serif.Any(key.Contains) && !key.Contains("sans"), false);
    }

    private static Resolved Liberation(string family, bool bold, bool italic)
    {
        var (serif, mono) = Kind(family);
        var name = mono ? "LiberationMono" : serif ? "LiberationSerif" : "LiberationSans";
        var style = (bold, italic) switch
        {
            (true, true) => "BoldItalic",
            (true, false) => "Bold",
            (false, true) => "Italic",
            _ => "Regular",
        };
        var file = $"{name}-{style}.ttf";
        var face = ElementFace.For("lib:" + file, () => EmbeddedFonts.StandardOpenType(file), mono, serif, italic);
        return new Resolved(face, false, false, false);
    }

    private static Resolved? System(string family, bool bold, bool italic)
    {
        // The program's own fonts are always built in (an installed Liberation changes nothing)
        if (family.StartsWith("Liberation", StringComparison.OrdinalIgnoreCase)) return null;
        OpenTypeFont? font;
        try
        {
            font = SystemFonts.Find(family, bold, italic);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FontFormatException)
        {
            return null;
        }
        if (font is null || !Embeddable(font) || IsCollection(font)) return null;
        var (fontBold, fontItalic) = StyleOf(font);
        var (serif, mono) = Kind(family);
        var face = ElementFace.For("sys:" + font.PostScriptName + "|" + font.FileData.Length, () => font, mono, serif, fontItalic);
        return new Resolved(face, bold && !fontBold, italic && !fontItalic, true);
    }

    /// <summary>
    /// Whether the licence permits embedding (OS/2 fsType). Not for "restricted licence" (only the local computer may use it),
    /// "bitmap embedding only" and "no subsetting" (only the characters used are embedded). "Preview and print" and "editable"
    /// are permitted.
    /// </summary>
    internal static bool Embeddable(OpenTypeFont font)
    {
        var os2 = font.TableData("OS/2");
        if (os2.Length < 10) return true;   // no entry: installable
        var fsType = BinaryPrimitives.ReadUInt16BigEndian(os2[8..]);
        var restricted = (fsType & 0x000F) == 0x0002;
        var noSubset = (fsType & 0x0100) != 0;
        var bitmapOnly = (fsType & 0x0200) != 0;
        return !restricted && !noSubset && !bitmapOnly;
    }

    private static bool IsCollection(OpenTypeFont font) => font.FileData.Length >= 4 && font.FileData.AsSpan(0, 4).SequenceEqual("ttcf"u8);

    /// <summary>Style of the font according to OS/2 (weight, fsSelection): a search for a missing bold variant returns the regular one.</summary>
    private static (bool Bold, bool Italic) StyleOf(OpenTypeFont font)
    {
        var os2 = font.TableData("OS/2");
        if (os2.Length < 64) return (false, false);
        var weight = BinaryPrimitives.ReadUInt16BigEndian(os2[4..]);
        var selection = BinaryPrimitives.ReadUInt16BigEndian(os2[62..]);
        return (weight >= 600 || (selection & 0x20) != 0, (selection & 0x01) != 0 || (selection & 0x200) != 0);
    }
}
