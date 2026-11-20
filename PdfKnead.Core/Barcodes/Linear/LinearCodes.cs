using System.Text;

namespace PdfKnead.Core.Barcodes.Linear;

/// <summary>Kinds of bar codes (one row of bars).</summary>
public enum LinearSymbology
{
    /// <summary>EAN-13 (ISO/IEC 15420).</summary>
    Ean13,

    /// <summary>EAN-8 (ISO/IEC 15420).</summary>
    Ean8,

    /// <summary>UPC-A: EAN-13 with a leading 0 (ISO/IEC 15420).</summary>
    UpcA,

    /// <summary>UPC-E, the zero-suppressed UPC (ISO/IEC 15420).</summary>
    UpcE,

    /// <summary>Code 128 (ISO/IEC 15417).</summary>
    Code128,

    /// <summary>GS1-128: Code 128 with FNC1 in the first position.</summary>
    Gs1_128,

    /// <summary>Code 39 (ISO/IEC 16388).</summary>
    Code39,

    /// <summary>Interleaved 2 of 5 (ISO/IEC 16390).</summary>
    Itf,
}

/// <summary>What a bar code holds.</summary>
public sealed record LinearContent
{
    /// <summary>The kind.</summary>
    public LinearSymbology Symbology { get; init; }

    /// <summary>The content (for EAN/UPC all digits including the check digit; GS1 group separators as U+001D).</summary>
    public required string Text { get; init; }

    /// <summary>Check character: null if the symbology has none or it is optional and absent; true if it is right.</summary>
    public bool? CheckValid { get; init; }

    /// <summary>Modules from the first to the last bar.</summary>
    public int Modules { get; init; }

    /// <summary>Character sets used (Code 128: "A", "B", "C").</summary>
    public IReadOnlyList<string> Sets { get; init; } = [];
}

/// <summary>Writes and reads EAN/UPC, Code 128, Code 39 and ITF: modules (true = bar) and widths of bars and spaces.</summary>
public static class LinearCodes
{
    // EAN/UPC: widths of the L set (space, bar, space, bar), ISO/IEC 15420, table 1; the R set has the same widths with bars and spaces
    // swapped, the G set the L widths reversed
    private static readonly string[] EanL = ["3211", "2221", "2122", "1411", "1132", "1231", "1114", "1312", "1213", "3112"];
    private static readonly string[] EanParity = ["LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL"];
    private static readonly string[] UpcEParity = ["GGGLLL", "GGLGLL", "GGLLGL", "GGLLLG", "GLGGLL", "GLLGGL", "GLLLGG", "GLGLGL", "GLGLLG", "GLLGLG"];

    // Code 128 (ISO/IEC 15417, table 1): bar, space, bar, space, bar, space; 106 = stop with a final bar
    internal static readonly string[] Code128Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    ];

    // Code 39 (ISO/IEC 16388, table 1): nine elements bar first, 1 = wide
    internal const string Code39Characters = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%*";
    internal static readonly string[] Code39Patterns =
    [
        "000110100", "100100001", "001100001", "101100000", "000110001", "100110000", "001110000", "000100101", "100100100", "001100100",
        "100001001", "001001001", "101001000", "000011001", "100011000", "001011000", "000001101", "100001100", "001001100", "000011100",
        "100000011", "001000011", "101000010", "000010011", "100010010", "001010010", "000000111", "100000110", "001000110", "000010110",
        "110000001", "011000001", "111000000", "010010001", "110010000", "011010000", "010000101", "110000100", "011000100", "010101000",
        "010100010", "010001010", "000101010", "010010100",
    ];

    // ITF (ISO/IEC 16390, table 1): five elements, 1 = wide
    private static readonly string[] ItfPatterns = ["00110", "10001", "01001", "11000", "00101", "10100", "01100", "00011", "10010", "01010"];

    // ------------------------------------------------------------ check digits

    /// <summary>GS1 check digit (mod 10, weights 3 and 1 from the right) for the digits before it.</summary>
    public static int Gs1CheckDigit(ReadOnlySpan<char> digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
            sum += (digits[digits.Length - 1 - i] - '0') * (i % 2 == 0 ? 3 : 1);
        return (10 - sum % 10) % 10;
    }

    // ------------------------------------------------------------ encoding

    /// <summary>Encodes a bar code.</summary>
    /// <param name="symbology">The kind.</param>
    /// <param name="text">Content; for EAN/UPC/ITF digits (the check digit is added when it is missing, and checked when present).</param>
    /// <returns>Modules from the first to the last bar (true = bar), without quiet zones.</returns>
    /// <exception cref="ArgumentException">The content does not fit the symbology.</exception>
    public static bool[] Encode(LinearSymbology symbology, string text)
    {
        var widths = symbology switch
        {
            LinearSymbology.Ean13 or LinearSymbology.UpcA => Ean13Widths(Digits(symbology == LinearSymbology.UpcA ? "0" + text : text, 12, 13)),
            LinearSymbology.Ean8 => Ean8Widths(Digits(text, 7, 8)),
            LinearSymbology.UpcE => UpcEWidths(UpcEDigits(text)),
            LinearSymbology.Code128 or LinearSymbology.Gs1_128 => Code128Widths(text, symbology == LinearSymbology.Gs1_128),
            LinearSymbology.Code39 => Code39Widths(text),
            _ => ItfWidths(text),
        };
        var modules = new List<bool>();
        for (var i = 0; i < widths.Count; i++)
            for (var k = 0; k < widths[i]; k++) modules.Add(i % 2 == 0);
        return [.. modules];
    }

    private static string Digits(string text, int withoutCheck, int withCheck)
    {
        if (!text.All(char.IsAsciiDigit)) throw new ArgumentException(Tr.T("Nur Ziffern erlaubt."));
        if (text.Length == withoutCheck) return text + Gs1CheckDigit(text);
        if (text.Length != withCheck) throw new ArgumentException(Tr.T("{0} oder {1} Ziffern erwartet.", withoutCheck, withCheck));
        if (Gs1CheckDigit(text.AsSpan(0, withoutCheck)) != text[^1] - '0') throw new ArgumentException(Tr.T("Die Prüfziffer stimmt nicht."));
        return text;
    }

    /// <summary>UPC-E: the check digit belongs to the expanded UPC-A number.</summary>
    private static string UpcEDigits(string text)
    {
        if (!text.All(char.IsAsciiDigit) || text.Length is not (7 or 8)) throw new ArgumentException(Tr.T("7 oder 8 Ziffern erwartet."));
        return text.Length == 7 ? text + Gs1CheckDigit(ExpandUpcE(text)) : text;
    }

    private static void AddDigit(List<int> widths, int digit, char set)
    {
        var w = EanL[digit];
        if (set == 'G') w = new string(w.Reverse().ToArray());
        foreach (var c in w) widths.Add(c - '0');
    }

    // widths always start with a bar; the L/G sets begin with a space, so the guard before them ends with a bar
    private static List<int> Ean13Widths(string d)
    {
        var w = new List<int> { 1, 1, 1 };
        var parity = EanParity[d[0] - '0'];
        for (var i = 1; i <= 6; i++) AddDigit(w, d[i] - '0', parity[i - 1]);
        w.AddRange([1, 1, 1, 1, 1]);
        for (var i = 7; i <= 12; i++) AddDigit(w, d[i] - '0', 'R');
        w.AddRange([1, 1, 1]);
        return w;
    }

    private static List<int> Ean8Widths(string d)
    {
        var w = new List<int> { 1, 1, 1 };
        for (var i = 0; i < 4; i++) AddDigit(w, d[i] - '0', 'L');
        w.AddRange([1, 1, 1, 1, 1]);
        for (var i = 4; i < 8; i++) AddDigit(w, d[i] - '0', 'R');
        w.AddRange([1, 1, 1]);
        return w;
    }

    /// <summary>UPC-E: number system (0 or 1), six digits, check digit of the expanded UPC-A.</summary>
    private static List<int> UpcEWidths(string d)
    {
        if (d[0] is not ('0' or '1')) throw new ArgumentException(Tr.T("UPC-E beginnt mit 0 oder 1."));
        if (Gs1CheckDigit(ExpandUpcE(d[..7])) != d[7] - '0') throw new ArgumentException(Tr.T("Die Prüfziffer stimmt nicht."));
        var parity = UpcEParity[d[7] - '0'];
        if (d[0] == '1') parity = new string(parity.Select(c => c == 'G' ? 'L' : 'G').ToArray());
        var w = new List<int> { 1, 1, 1 };
        for (var i = 1; i <= 6; i++) AddDigit(w, d[i] - '0', parity[i - 1]);
        w.AddRange([1, 1, 1, 1, 1, 1]);
        return w;
    }

    /// <summary>The UPC-A digits (without check digit) of a UPC-E number system and six digits (ISO/IEC 15420, table 6).</summary>
    public static string ExpandUpcE(string d)
    {
        var s = d[0];
        var x = d[6];
        return x switch
        {
            '0' or '1' or '2' => $"{s}{d[1]}{d[2]}{x}0000{d[3]}{d[4]}{d[5]}",
            '3' => $"{s}{d[1]}{d[2]}{d[3]}00000{d[4]}{d[5]}",
            '4' => $"{s}{d[1]}{d[2]}{d[3]}{d[4]}00000{d[5]}",
            _ => $"{s}{d[1]}{d[2]}{d[3]}{d[4]}{d[5]}0000{x}",
        };
    }

    private static List<int> Code128Widths(string text, bool gs1)
    {
        // set C for runs of at least four digits (or all digits), otherwise B; A only for control characters
        var values = new List<int>();
        char set = ' ';
        int DigitRun(int at)
        {
            var n = 0;
            while (at + n < text.Length && char.IsAsciiDigit(text[at + n])) n++;
            return n;
        }
        void Switch(char to)
        {
            if (set == to) return;
            values.Add(set == ' ' ? to switch { 'A' => 103, 'B' => 104, _ => 105 } : to switch { 'A' => 101, 'B' => 100, _ => 99 });
            set = to;
        }
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (c > 127) throw new ArgumentException(Tr.T("Code 128 kennt nur ASCII."));
            var run = DigitRun(i);
            if (run >= 4 || run >= 2 && (run == text.Length - i || set == 'C') || set == ' ' && run >= 2 && run == text.Length)
            {
                Switch('C');
                if (gs1 && values.Count == 1) values.Add(102);
                for (; run >= 2; run -= 2, i += 2) values.Add((text[i] - '0') * 10 + text[i + 1] - '0');
                continue;
            }
            if (gs1 && c == '\u001D')
            {
                if (set == ' ') Switch('B');
                values.Add(102);
                i++;
                continue;
            }
            Switch(c < 32 ? 'A' : set == 'A' && c < 96 ? 'A' : 'B');
            if (gs1 && values.Count == 1) values.Add(102);
            values.Add(set == 'A' ? (c < 32 ? c + 64 : c - 32) : c - 32);
            i++;
        }
        if (set == ' ')
        {
            Switch('B');
            if (gs1) values.Add(102);
        }
        var check = values[0];
        for (var i = 1; i < values.Count; i++) check += values[i] * i;
        values.Add(check % 103);
        values.Add(106);
        return values.SelectMany(v => Code128Patterns[v].Select(ch => ch - '0')).ToList();
    }

    private static List<int> Code39Widths(string text)
    {
        var w = new List<int>();
        foreach (var c in text)
            if (c == '*' || Code39Characters.IndexOf(char.ToUpperInvariant(c)) < 0) throw new ArgumentException(Tr.T("Code 39 kennt das Zeichen „{0}“ nicht.", c));
        foreach (var c in "*" + text.ToUpperInvariant() + "*")
        {
            var i = Code39Characters.IndexOf(c);
            if (w.Count > 0) w.Add(1);   // gap between characters
            foreach (var e in Code39Patterns[i]) w.Add(e == '1' ? 3 : 1);
        }
        return w;
    }

    private static List<int> ItfWidths(string text)
    {
        if (!text.All(char.IsAsciiDigit) || text.Length == 0) throw new ArgumentException(Tr.T("Nur Ziffern erlaubt."));
        if (text.Length % 2 != 0) text = "0" + text;
        var w = new List<int> { 1, 1, 1, 1 };
        for (var i = 0; i < text.Length; i += 2)
        {
            string a = ItfPatterns[text[i] - '0'], b = ItfPatterns[text[i + 1] - '0'];
            for (var k = 0; k < 5; k++)
            {
                w.Add(a[k] == '1' ? 3 : 1);
                w.Add(b[k] == '1' ? 3 : 1);
            }
        }
        w.AddRange([3, 1, 1]);
        return w;
    }

    // ------------------------------------------------------------ decoding

    /// <summary>
    /// Reads a bar code from the widths along a scan line: <paramref name="runs"/> alternates light and dark, starting and ending with
    /// light (the quiet zones may be cut). Every kind is tried at every bar that follows a quiet zone, in both reading directions.
    /// </summary>
    /// <returns>The first code read in full; null if none.</returns>
    public static LinearContent? Decode(ReadOnlySpan<double> runs)
    {
        if (runs.Length < 10) return null;
        var forward = runs.ToArray();
        var backward = runs.ToArray();
        Array.Reverse(backward);
        return Scan(forward) ?? Scan(backward);
    }

    private static LinearContent? Scan(double[] r)
    {
        // r[0] is light; bars at odd indices
        for (var b = 1; b < r.Length - 3; b += 2)
        {
            // every kind starts after a quiet zone of at least 4.6 modules and with a bar of one or two: where the light before is
            // not clearly wider than the bar, no code starts (most positions of a long line end here)
            if (!(r[b - 1] >= 1.5 * r[b])) continue;
            var result = Ean(r, b) ?? Code128(r, b) ?? Code39(r, b) ?? Itf(r, b);
            if (result is not null) return result;
        }
        return null;
    }

    private static double Sum(double[] r, int from, int count)
    {
        var s = 0.0;
        for (var i = from; i < from + count; i++) s += r[i];
        return s;
    }

    /// <summary>Light before the first bar and after the last at least <paramref name="modules"/> modules (or the scan line ends there).</summary>
    private static bool Quiet(double[] r, int first, int afterLast, double module, double modules) =>
        r[first - 1] >= modules * module && (afterLast >= r.Length || r[afterLast] >= modules * module);

    /// <summary>The best match of <paramref name="count"/> widths against patterns of the same total; null if none is close.</summary>
    private static int Match(double[] r, int at, int count, int total, IReadOnlyList<string> patterns, int limit = int.MaxValue, bool reverse = false) =>
        MatchWithError(r, at, count, total, patterns, limit, reverse).Index;

    private static (int Index, double Error) MatchWithError(double[] r, int at, int count, int total, IReadOnlyList<string> patterns,
        int limit = int.MaxValue, bool reverse = false)
    {
        var sum = Sum(r, at, count);
        if (sum <= 0) return (-1, double.MaxValue);
        var best = -1;
        var bestError = 0.42 * count;   // average deviation below about 0.4 modules per element
        for (var p = 0; p < Math.Min(patterns.Count, limit); p++)
        {
            var pattern = patterns[p];
            if (pattern.Length != count) continue;
            // mostly edge to similar edge (bar + following space, the reference decode of ISO/IEC 15417 and 15420): ink spread and
            // print gain make bars wider and spaces narrower by the same amount, which cancels out in such pairs; the single widths
            // only break ties (EAN digits 1/7 and 2/8 have the same pairs)
            double edges = 0, single = 0;
            for (var k = 0; k < count; k++)
            {
                double measured = r[at + k] * total / sum, expected = pattern[reverse ? count - 1 - k : k] - '0';
                single += Math.Abs(measured - expected);
                if (k + 1 >= count) continue;
                double measuredPair = (r[at + k] + r[at + k + 1]) * total / sum;
                double expectedPair = expected + pattern[reverse ? count - 2 - k : k + 1] - '0';
                edges += Math.Abs(measuredPair - expectedPair);
            }
            var error = edges * count / Math.Max(1, count - 1) * 0.7 + single * 0.3;
            if (error >= bestError) continue;
            bestError = error;
            best = p;
        }
        return (best, bestError);
    }

    private static readonly string[] Guard3 = ["111"], Guard5 = ["11111"], Guard6 = ["111111"];

    private static LinearContent? Ean(double[] r, int b)
    {
        // EAN-13 / UPC-A: 3 + 24 + 5 + 24 + 3 = 59 elements over 95 modules; EAN-8: 43 over 67; UPC-E: 33 over 51
        foreach (var (elements, modules, kind) in new[] { (59, 95, 13), (43, 67, 8), (33, 51, 6) })
        {
            if (b + elements > r.Length) continue;
            var module = Sum(r, b, elements) / modules;
            if (!Quiet(r, b, b + elements, module, 4.6)) continue;
            if (Match(r, b, 3, 3, Guard3) != 0) continue;
            var digits = new StringBuilder();
            var parity = new StringBuilder();
            var half = kind == 13 ? 6 : kind == 8 ? 4 : 6;
            var ok = true;
            for (var i = 0; i < half && ok; i++)
            {
                var at = b + 3 + 4 * i;
                var l = Match(r, at, 4, 7, EanL);
                var g = Match(r, at, 4, 7, EanL, reverse: true);
                // EAN-8 has only L digits on the left; one that matches a G digit better is a code read backwards (its right half,
                // mirrored), which may by chance give digits with a valid check digit — the reading in the other direction is the right one
                if (kind == 8)
                {
                    if (g >= 0 && (l < 0 || Error(r, at, EanL[g], true) < Error(r, at, EanL[l], false))) ok = false;
                    g = -1;
                }
                if (!ok) break;
                if (l < 0 && g < 0) ok = false;
                else if (l >= 0 && (g < 0 || Error(r, at, EanL[l], false) <= Error(r, at, EanL[g], true)))
                {
                    digits.Append(l);
                    parity.Append('L');
                }
                else
                {
                    digits.Append(g);
                    parity.Append('G');
                }
            }
            if (!ok) continue;
            var middle = b + 3 + 4 * half;
            if (kind == 6)
            {
                if (Match(r, middle, 6, 6, Guard6) != 0) continue;
                var p = parity.ToString();
                for (var system = 0; system <= 1; system++)
                    for (var check = 0; check <= 9; check++)
                    {
                        var expected = UpcEParity[check];
                        if (system == 1) expected = new string(expected.Select(c => c == 'G' ? 'L' : 'G').ToArray());
                        if (expected != p) continue;
                        var number = $"{system}{digits}";
                        if (Gs1CheckDigit(ExpandUpcE(number)) != check) continue;
                        return new LinearContent { Symbology = LinearSymbology.UpcE, Text = number + check, CheckValid = true, Modules = 51 };
                    }
                continue;
            }
            if (Match(r, middle, 5, 5, Guard5) != 0) continue;
            for (var i = 0; i < half && ok; i++)
            {
                var d = Match(r, middle + 5 + 4 * i, 4, 7, EanL);
                if (d < 0) ok = false;
                else digits.Append(d);
            }
            if (!ok || Match(r, middle + 5 + 4 * half, 3, 3, Guard3) != 0) continue;
            string text;
            if (kind == 13)
            {
                var first = Array.IndexOf(EanParity, parity.ToString());
                if (first < 0) continue;
                text = first + digits.ToString();
            }
            else
            {
                if (parity.ToString() != "LLLL") continue;
                text = digits.ToString();
            }
            var valid = Gs1CheckDigit(text.AsSpan(0, text.Length - 1)) == text[^1] - '0';
            if (!valid) continue;   // a misread digit would otherwise pass as a code
            if (kind == 13 && text[0] == '0')
                return new LinearContent { Symbology = LinearSymbology.UpcA, Text = text[1..], CheckValid = true, Modules = 95 };
            return new LinearContent { Symbology = kind == 13 ? LinearSymbology.Ean13 : LinearSymbology.Ean8, Text = text, CheckValid = true, Modules = modules };
        }
        return null;
    }

    private static double Error(double[] r, int at, string pattern, bool reverse)
    {
        var sum = Sum(r, at, 4);
        var e = 0.0;
        for (var k = 0; k < 4; k++) e += Math.Abs(r[at + k] * 7 / sum - (pattern[reverse ? 3 - k : k] - '0'));
        return e;
    }

    private static LinearContent? Code128(double[] r, int b)
    {
        if (b + 6 + 6 + 7 > r.Length) return null;
        var start = Match(r, b, 6, 11, Code128Patterns, 106);
        if (start is not (103 or 104 or 105)) return null;
        var module = Sum(r, b, 6) / 11;
        if (r[b - 1] < 4.6 * module) return null;
        var values = new List<int> { start };
        var at = b + 6;
        while (true)
        {
            // a data character plus the next bar also spans 13 modules: the stop only where it fits better than a character
            (int Index, double Error) stop = at + 7 <= r.Length ? MatchWithError(r, at, 7, 13, Code128Patterns) : (-1, double.MaxValue);
            var (v, error) = at + 6 <= r.Length ? MatchWithError(r, at, 6, 11, Code128Patterns, 106) : (-1, double.MaxValue);
            if (stop.Index == 106 && (v < 0 || stop.Error * 6 / 7 < error))
            {
                at += 7;
                break;
            }
            if (v < 0 || v >= 103) return null;
            values.Add(v);
            at += 6;
        }
        if (values.Count < 2 || at < r.Length && r[at] < 4.6 * module) return null;
        var check = values[0];
        for (var i = 1; i < values.Count - 1; i++) check += values[i] * i;
        if (check % 103 != values[^1]) return null;
        values.RemoveAt(values.Count - 1);

        var text = new StringBuilder();
        var sets = new List<string>();
        var set = start switch { 103 => 'A', 104 => 'B', _ => 'C' };
        sets.Add(set.ToString());
        var gs1 = values.Count > 1 && values[1] == 102;
        bool shift = false, extended = false, extendedOnce = false;
        for (var i = 1; i < values.Count; i++)
        {
            var v = values[i];
            var current = shift ? (set == 'A' ? 'B' : 'A') : set;
            shift = false;
            if (current == 'C')
            {
                if (v < 100) text.Append(v.ToString("00"));
                else if (v == 102) { if (!(gs1 && i == 1)) text.Append('\u001D'); }
                else Latch(v == 100 ? 'B' : 'A');
                continue;
            }
            if (v < 96)
            {
                var c = current == 'A' ? (v < 64 ? v + 32 : v - 64) : v + 32;
                if (extended ^ extendedOnce) c += 128;
                extendedOnce = false;
                text.Append((char)c);
                continue;
            }
            switch (v)
            {
                case 98:
                    shift = true;
                    break;
                case 99:
                    Latch('C');
                    break;
                case 100 when current == 'B':
                case 101 when current == 'A':
                    // FNC4: once for the next character, twice in a row for all following ones
                    if (extendedOnce)
                    {
                        extended = !extended;
                        extendedOnce = false;
                    }
                    else extendedOnce = true;
                    break;
                case 100:
                    Latch('B');
                    break;
                case 101:
                    Latch('A');
                    break;
                case 102:
                    if (!(gs1 && i == 1)) text.Append('\u001D');
                    break;
            }
        }
        return new LinearContent
        {
            Symbology = gs1 ? LinearSymbology.Gs1_128 : LinearSymbology.Code128, Text = text.ToString(), CheckValid = true,
            Modules = (values.Count + 1) * 11 + 13, Sets = sets,
        };

        void Latch(char to)
        {
            set = to;
            if (sets[^1] != to.ToString()) sets.Add(to.ToString());
        }
    }

    /// <summary>Narrow (0) or wide (1) per element: the three widest of nine (Code 39) or two of five (ITF) are wide.</summary>
    private static string? WideNarrow(double[] r, int at, int count, int wide)
    {
        var order = Enumerable.Range(0, count).OrderByDescending(k => r[at + k]).ToArray();
        double narrowMax = r[at + order[wide]], wideMin = r[at + order[wide - 1]];
        if (wideMin < 1.7 * narrowMax) return null;
        var pattern = new char[count];
        for (var k = 0; k < count; k++) pattern[k] = r[at + k] >= wideMin ? '1' : '0';
        return new string(pattern);
    }

    /// <summary>
    /// The Code 39 character of nine elements: the pattern whose narrow and wide elements fit best — bars and spaces measured
    /// separately, since ink spread makes bars wider and spaces narrower by the same amount; −1 if none fits.
    /// </summary>
    private static int Code39Character(double[] r, int at)
    {
        var best = -1;
        var bestError = 0.9;   // about 30 % deviation per element
        Span<double> sums = stackalloc double[4];
        Span<int> counts = stackalloc int[4];
        Span<double> mean = stackalloc double[4];
        for (var p = 0; p < Code39Patterns.Length; p++)
        {
            var pattern = Code39Patterns[p];
            sums.Clear();
            counts.Clear();
            for (var k = 0; k < 9; k++)
            {
                var group = (k % 2 == 0 ? 0 : 2) + (pattern[k] - '0');   // narrow bar, wide bar, narrow space, wide space
                sums[group] += r[at + k];
                counts[group]++;
            }
            for (var g = 0; g < 4; g++) mean[g] = counts[g] > 0 ? sums[g] / counts[g] : 0;
            // wide must be clearly wider than narrow, among bars and among spaces (ISO/IEC 16388: ratio 2 … 3)
            if (counts[0] > 0 && counts[1] > 0 && mean[1] < 1.6 * mean[0]) continue;
            if (counts[2] > 0 && counts[3] > 0 && mean[3] < 1.6 * mean[2]) continue;
            var error = 0.0;
            for (var k = 0; k < 9; k++)
            {
                var group = (k % 2 == 0 ? 0 : 2) + (pattern[k] - '0');
                var d = (r[at + k] - mean[group]) / mean[group];
                error += d * d;
            }
            // a group without a counterpart (all narrow spaces): its elements must still be narrow next to the wide ones of the other kind
            if (counts[3] == 0 && counts[1] > 0 && mean[2] > 0.8 * mean[1]) continue;
            if (counts[1] == 0 && counts[3] > 0 && mean[0] > 0.8 * mean[3]) continue;
            if (error >= bestError) continue;
            bestError = error;
            best = p;
        }
        return best;
    }

    private static LinearContent? Code39(double[] r, int b)
    {
        if (b + 9 + 1 + 9 > r.Length || Code39Character(r, b) != Code39Patterns.Length - 1) return null;
        var narrow = Sum(r, b, 9) / 15;
        if (r[b - 1] < 4.6 * narrow) return null;
        var text = new StringBuilder();
        var at = b + 10;
        while (true)
        {
            if (at + 9 > r.Length) return null;
            var i = Code39Character(r, at);
            if (i < 0) return null;
            at += 9;
            if (i == Code39Patterns.Length - 1) break;
            text.Append(Code39Characters[i]);
            if (at >= r.Length || r[at] > 4.6 * narrow) return null;   // the gap between characters
            at++;
        }
        if (text.Length == 0 || at < r.Length && r[at] < 4.6 * narrow) return null;
        // the modulo-43 check character is optional: only say it is right when it is
        var sum = 0;
        for (var i = 0; i < text.Length - 1; i++) sum += Code39Characters.IndexOf(text[i]);
        var withCheck = text.Length > 1 && Code39Characters[sum % 43] == text[^1];
        return new LinearContent { Symbology = LinearSymbology.Code39, Text = text.ToString(), CheckValid = withCheck ? true : null, Modules = (text.Length + 2) * 16 - 1 };
    }

    private static LinearContent? Itf(double[] r, int b)
    {
        if (b + 4 + 10 + 3 > r.Length) return null;
        var narrow = Sum(r, b, 4) / 4;
        if (r[b - 1] < 4.6 * narrow) return null;
        for (var k = 0; k < 4; k++)
            if (r[b + k] > 1.6 * narrow || r[b + k] < 0.5 * narrow) return null;
        var text = new StringBuilder();
        var at = b + 4;
        while (true)
        {
            // stop: wide bar, narrow space, narrow bar, then the quiet zone
            if (at + 3 <= r.Length && r[at] > 1.8 * narrow && r[at + 1] < 1.6 * narrow && r[at + 2] < 1.6 * narrow
                && (at + 3 >= r.Length || r[at + 3] >= 4.6 * narrow))
                break;
            if (at + 10 > r.Length) return null;
            var bars = new double[5];
            var spaces = new double[5];
            for (var k = 0; k < 5; k++)
            {
                bars[k] = r[at + 2 * k];
                spaces[k] = r[at + 2 * k + 1];
            }
            var a = WideNarrow(bars, 0, 5, 2);
            var c = WideNarrow(spaces, 0, 5, 2);
            var da = a is null ? -1 : Array.IndexOf(ItfPatterns, a);
            var dc = c is null ? -1 : Array.IndexOf(ItfPatterns, c);
            if (da < 0 || dc < 0) return null;
            text.Append(da).Append(dc);
            at += 10;
        }
        // fewer than six digits: too easily found by chance in other bars or text (ITF-14 has fourteen, common minimum is six)
        if (text.Length < 6) return null;
        var check = Gs1CheckDigit(text.ToString().AsSpan(0, text.Length - 1)) == text[^1] - '0';
        return new LinearContent { Symbology = LinearSymbology.Itf, Text = text.ToString(), CheckValid = check ? true : null, Modules = 4 + text.Length / 2 * 18 + 5 };
    }
}
