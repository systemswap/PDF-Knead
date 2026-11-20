using System.Globalization;
using System.Text;

namespace PdfKnead.Ai;

/// <summary>
/// What the description of a PP-OCRv6 model (inference.yml) says that the recognition needs: the characters of the recognition model and
/// the thresholds of the detection. Only these few entries are read, line by line — no general YAML reader.
/// </summary>
internal sealed class PaddleDescription
{
    /// <summary>The characters, in the order of the network's classes (after the blank of CTC).</summary>
    public IReadOnlyList<string> Characters { get; private init; } = [];

    /// <summary>Detection: a pixel counts as text from this probability on.</summary>
    public double Threshold { get; private init; } = 0.3;

    /// <summary>Detection: a box counts from this mean probability on.</summary>
    public double BoxThreshold { get; private init; } = 0.6;

    /// <summary>Detection: how far a box is widened (its area times this, divided by its perimeter).</summary>
    public double UnclipRatio { get; private init; } = 1.5;

    /// <summary>Detection: at most this many boxes.</summary>
    public int MaxCandidates { get; private init; } = 1000;

    /// <summary>Reads a description.</summary>
    /// <param name="path">The inference.yml.</param>
    /// <returns>The entries found; defaults for the others.</returns>
    public static PaddleDescription Read(string path) => Parse(File.ReadAllLines(path, Encoding.UTF8));

    /// <summary>Reads the lines of a description.</summary>
    internal static PaddleDescription Parse(IReadOnlyList<string> lines)
    {
        var characters = new List<string>();
        double threshold = 0.3, boxThreshold = 0.6, unclip = 1.5;
        var candidates = 1000;
        var inDictionary = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (inDictionary)
            {
                if (line.StartsWith("  - ", StringComparison.Ordinal))
                {
                    characters.Add(Scalar(line[4..]));
                    continue;
                }
                inDictionary = false;
            }
            var trimmed = line.Trim();
            if (trimmed == "character_dict:") inDictionary = true;
            else if (Value(trimmed, "thresh:") is { } t) threshold = t;
            else if (Value(trimmed, "box_thresh:") is { } b) boxThreshold = b;
            else if (Value(trimmed, "unclip_ratio:") is { } u) unclip = u;
            else if (Value(trimmed, "max_candidates:") is { } m) candidates = (int)Math.Clamp(m, 1, 100_000);
        }
        return new PaddleDescription { Characters = characters, Threshold = threshold, BoxThreshold = boxThreshold, UnclipRatio = unclip, MaxCandidates = candidates };
    }

    private static double? Value(string line, string key) =>
        line.StartsWith(key, StringComparison.Ordinal) && double.TryParse(line[key.Length..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) &&
        double.IsFinite(v) ? v : null;

    /// <summary>A scalar of a YAML list: bare, in single quotes ('' for a quote) or in double quotes (with backslash escapes).</summary>
    internal static string Scalar(string text)
    {
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'') return text[1..^1].Replace("''", "'");
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            var sb = new StringBuilder();
            for (var i = 1; i < text.Length - 1; i++)
            {
                var c = text[i];
                if (c != '\\' || i + 1 >= text.Length - 1)
                {
                    sb.Append(c);
                    continue;
                }
                var next = text[++i];
                switch (next)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u' when i + 4 < text.Length - 1 && int.TryParse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u):
                        sb.Append((char)u);
                        i += 4;
                        break;
                    case 'U' when i + 8 < text.Length - 1 && int.TryParse(text.AsSpan(i + 1, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp) &&
                                  cp is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF):
                        sb.Append(char.ConvertFromUtf32(cp));
                        i += 8;
                        break;
                    default: sb.Append(next); break;
                }
            }
            return sb.ToString();
        }
        return text;
    }
}
