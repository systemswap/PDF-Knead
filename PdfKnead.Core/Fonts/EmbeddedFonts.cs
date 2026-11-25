using System.Collections.Concurrent;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Fonts from the folder "Schriften/" that the build embeds into the program: substitutes for the
/// 14 standard fonts and the free handwriting fonts for signatures. The folder alone determines which
/// fonts exist. The handwriting fonts are managed by <see cref="SignatureFonts"/>, which adds those
/// from the folder "Schriften-Unterschrift" (fonts the user imported).
/// </summary>
public static class EmbeddedFonts
{
    private const string StandardPrefix = "Schriften/Standard/";

    private static readonly ConcurrentDictionary<string, object> Parsed = new(StringComparer.Ordinal);

    /// <summary>File names of the handwriting fonts (embedded ones and those from the folders), in alphabetical order.</summary>
    public static IReadOnlyList<string> Handwriting => SignatureFonts.Names;

    /// <summary>File names of the substitute fonts, in alphabetical order.</summary>
    public static IReadOnlyList<string> Standard { get; } = Names(StandardPrefix);

    /// <summary>Loads the file data of a substitute font.</summary>
    /// <param name="file">File name from <see cref="Standard"/>.</param>
    /// <returns>A new array with the font file.</returns>
    /// <exception cref="FileNotFoundException">The program contains no font of that name.</exception>
    public static byte[] LoadStandard(string file) => Load(StandardPrefix + file);

    /// <summary>Loads the file data of a handwriting font (see <see cref="SignatureFonts.Load"/>).</summary>
    /// <param name="file">File name from <see cref="Handwriting"/>.</param>
    /// <returns>The font file.</returns>
    public static byte[] LoadHandwriting(string file) => SignatureFonts.Load(file);

    /// <summary>Returns a substitute font as TrueType/OpenType (parsed once, then shared).</summary>
    /// <param name="file">File name from <see cref="Standard"/>.</param>
    /// <returns>The shared font.</returns>
    public static OpenTypeFont StandardOpenType(string file) =>
        (OpenTypeFont)Parsed.GetOrAdd(StandardPrefix + file, key => OpenTypeFont.Parse(Load(key)));

    /// <summary>
    /// Returns a font for Arabic script shipped in the program (Noto Sans Arabic, Noto Naskh Arabic; parsed once, then shared), for
    /// appearances the program builds (docs/FEHLERGEDAECHTNIS.md, No. 910).
    /// </summary>
    /// <param name="file">File name in Schriften/Arabisch.</param>
    /// <returns>The shared font.</returns>
    public static OpenTypeFont ArabicOpenType(string file) =>
        (OpenTypeFont)Parsed.GetOrAdd("Schriften/Arabisch/" + file, key => OpenTypeFont.Parse(Load(key)));

    /// <summary>Returns a handwriting font (parsed once, then shared); if it is missing, the fallback font.</summary>
    /// <param name="file">File name from <see cref="Handwriting"/>.</param>
    /// <returns>The font or its fallback.</returns>
    public static OpenTypeFont HandwritingOpenType(string file) => SignatureFonts.Get(file);

    /// <summary>Returns a substitute font in CFF format (Symbol, Dingbats), parsed once, then shared.</summary>
    /// <param name="file">File name from <see cref="Standard"/>.</param>
    /// <returns>The shared font.</returns>
    public static CffFont StandardCff(string file) =>
        (CffFont)Parsed.GetOrAdd(StandardPrefix + file, key => CffFont.Parse(Load(key)));

    private static byte[] Load(string resource)
    {
        using var stream = typeof(EmbeddedFonts).Assembly.GetManifestResourceStream(resource)
                           ?? throw new FileNotFoundException("Schrift nicht im Programm: " + resource);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static List<string> Names(string prefix) =>
        typeof(EmbeddedFonts).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => n[prefix.Length..])
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
