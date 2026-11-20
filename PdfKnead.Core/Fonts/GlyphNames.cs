using System.Globalization;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Maps glyph names to Unicode and back using the Adobe Glyph List (Data/glyphlist.txt, BSD licence
/// from Adobe) and the rules of the "AGL Specification": "uni0041", "u1F600", ligature names
/// joined with "_", suffixes after "." (e.g. "a.sc"). ZapfDingbats has names of its own (a1 …).
/// </summary>
public static class GlyphNames
{
    private static readonly Lazy<(Dictionary<string, string> ToText, Dictionary<int, string> ToName)> Agl = new(() => Load("glyphlist.txt"));
    private static readonly Lazy<(Dictionary<string, string> ToText, Dictionary<int, string> ToName)> Dingbats = new(() => Load("zapfdingbats.txt"));

    /// <summary>Returns the Unicode text for a glyph name.</summary>
    /// <param name="name">Glyph name.</param>
    /// <param name="zapfDingbats">Whether the ZapfDingbats names (a1 …) are looked up first.</param>
    /// <returns>The text (may be several characters for ligatures), or null if the name is unknown or ".notdef".</returns>
    public static string? ToUnicode(string name, bool zapfDingbats = false)
    {
        if (string.IsNullOrEmpty(name) || name == ".notdef") return null;
        if (zapfDingbats && Dingbats.Value.ToText.TryGetValue(name, out var d)) return d;
        if (Agl.Value.ToText.TryGetValue(name, out var text)) return text;

        // Strip the suffix: "a.sc" → "a"
        var dot = name.IndexOf('.');
        if (dot > 0) return ToUnicode(name[..dot], zapfDingbats);
        if (dot == 0) return null;

        // Ligatures made of components: "f_f_i"
        if (name.Contains('_'))
        {
            var parts = name.Split('_');
            var sb = new System.Text.StringBuilder();
            foreach (var part in parts)
            {
                if (ToUnicode(part, zapfDingbats) is not { } p) return null;
                sb.Append(p);
            }
            return sb.ToString();
        }

        // "uni" + multiples of 4 upper-case hex digits (BMP)
        if (name.Length >= 7 && name.StartsWith("uni", StringComparison.Ordinal) && (name.Length - 3) % 4 == 0)
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 3; i < name.Length; i += 4)
            {
                if (!int.TryParse(name.AsSpan(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var cp)
                    || !IsValid(cp) || name.AsSpan(i, 4).ContainsAny("abcdef")) return null;
                sb.Append((char)cp);
            }
            return sb.ToString();
        }

        // "u" + 4 to 6 hex digits
        if (name.Length is >= 5 and <= 7 && name[0] == 'u'
            && int.TryParse(name.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var u) && IsValid(u))
            return char.ConvertFromUtf32(u);

        return null;
    }

    /// <summary>Returns the glyph name for a Unicode character.</summary>
    /// <param name="codePoint">Unicode code point.</param>
    /// <returns>The AGL name if there is one, otherwise "uniXXXX" (BMP) or "uXXXXX" (above U+FFFF).</returns>
    public static string FromUnicode(int codePoint) =>
        Agl.Value.ToName.TryGetValue(codePoint, out var name) ? name
        : codePoint <= 0xFFFF ? "uni" + codePoint.ToString("X4", CultureInfo.InvariantCulture)
        : "u" + codePoint.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>Whether a name is one of the Adobe Glyph List exactly (no suffix, no ligature, no „uniXXXX“).</summary>
    /// <param name="name">Glyph name.</param>
    /// <returns>True if the list has it.</returns>
    public static bool InList(string name) => Agl.Value.ToText.ContainsKey(name);

    /// <summary>Returns the single Unicode character for a glyph name, for looking it up in a cmap.</summary>
    /// <param name="name">Glyph name.</param>
    /// <param name="zapfDingbats">Whether the ZapfDingbats names (a1 …) are looked up first.</param>
    /// <returns>The code point, or -1 if the name is unknown or stands for more than one character.</returns>
    public static int CodePoint(string name, bool zapfDingbats = false)
    {
        var text = ToUnicode(name, zapfDingbats);
        if (string.IsNullOrEmpty(text)) return -1;
        var cp = char.ConvertToUtf32(text, 0);
        return text.Length == char.ConvertFromUtf32(cp).Length ? cp : -1;
    }

    private static bool IsValid(int cp) => cp is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF);

    private static (Dictionary<string, string>, Dictionary<int, string>) Load(string file)
    {
        var toText = new Dictionary<string, string>(StringComparer.Ordinal);
        var toName = new Dictionary<int, string>();
        using var stream = typeof(GlyphNames).Assembly.GetManifestResourceStream("Fonts/" + file)
                           ?? throw new InvalidOperationException("Ressource fehlt: " + file);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var semi = line.IndexOf(';');
            if (semi <= 0) continue;
            var name = line[..semi];
            var sb = new System.Text.StringBuilder();
            foreach (var hex in line[(semi + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var cp) && IsValid(cp))
                    sb.Append(char.ConvertFromUtf32(cp));
            if (sb.Length == 0) continue;
            var text = sb.ToString();
            toText[name] = text;
            // Reverse direction: the first name per character (the list gives the usual names first)
            if (text.Length == char.ConvertFromUtf32(char.ConvertToUtf32(text, 0)).Length)
                toName.TryAdd(char.ConvertToUtf32(text, 0), name);
        }
        return (toText, toName);
    }
}
