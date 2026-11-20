namespace PdfKnead.Core.Barcodes.Qr;

/// <summary>
/// The fixed structure of a QR symbol (ISO/IEC 18004:2015, 6.3): finder, separator, timing and alignment patterns, the dark module and
/// the areas for format and version information; the order of the data modules (7.7.3), the data masks (7.8.2) and the BCH-coded
/// format and version information (7.9, 7.10).
/// </summary>
internal static class QrLayout
{
    private const int FormatMask = 0x5412;

    private static readonly Lazy<(bool[,] Function, (int X, int Y)[] Order)>[] Structures =
        [.. Enumerable.Range(1, 40).Select(v => new Lazy<(bool[,], (int, int)[])>(() =>
        {
            var function = FunctionModules(QrVersion.Get(v));
            return (function, DataOrder(function).ToArray());
        }))];

    /// <summary>Function modules and the order of the data modules of a version, computed once (both are fixed by the version).</summary>
    public static (bool[,] Function, (int X, int Y)[] Order) Structure(QrVersion version) => Structures[version.Number - 1].Value;

    /// <summary>Which modules belong to function patterns (or format/version areas) — those carry no data.</summary>
    public static bool[,] FunctionModules(QrVersion version)
    {
        var n = version.Dimension;
        var f = new bool[n, n];   // [x, y]
        void Area(int x0, int y0, int w, int h)
        {
            for (var y = Math.Max(0, y0); y < Math.Min(n, y0 + h); y++)
                for (var x = Math.Max(0, x0); x < Math.Min(n, x0 + w); x++)
                    f[x, y] = true;
        }
        // finder patterns with separators and format areas (9 × 9 top left, 8 × 9 / 9 × 8 at the other corners)
        Area(0, 0, 9, 9);
        Area(n - 8, 0, 8, 9);
        Area(0, n - 8, 9, 8);
        // timing patterns
        Area(6, 0, 1, n);
        Area(0, 6, n, 1);
        // alignment patterns, except where they would overlap a finder pattern
        var a = version.AlignmentCenters;
        foreach (var cy in a)
            foreach (var cx in a)
            {
                if (cx == 6 && cy == 6 || cx == 6 && cy == a[^1] || cx == a[^1] && cy == 6) continue;
                Area(cx - 2, cy - 2, 5, 5);
            }
        if (version.Number >= 7)
        {
            Area(n - 11, 0, 3, 6);
            Area(0, n - 11, 6, 3);
        }
        return f;
    }

    /// <summary>Draws all function patterns, format information (for <paramref name="level"/>, <paramref name="mask"/>) and version information.</summary>
    public static void DrawFunctionPatterns(BitMatrix m, QrVersion version, QrErrorCorrection level, int mask)
    {
        var n = version.Dimension;
        void Finder(int cx, int cy)
        {
            for (var dy = -4; dy <= 4; dy++)
                for (var dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= n || y >= n) continue;
                    var d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    m[x, y] = d != 2 && d != 4;
                }
        }
        Finder(3, 3);
        Finder(n - 4, 3);
        Finder(3, n - 4);
        for (var i = 8; i < n - 8; i++)
        {
            m[6, i] = i % 2 == 0;
            m[i, 6] = i % 2 == 0;
        }
        var a = version.AlignmentCenters;
        foreach (var cy in a)
            foreach (var cx in a)
            {
                if (cx == 6 && cy == 6 || cx == 6 && cy == a[^1] || cx == a[^1] && cy == 6) continue;
                for (var dy = -2; dy <= 2; dy++)
                    for (var dx = -2; dx <= 2; dx++)
                        m[cx + dx, cy + dy] = Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1;
            }
        DrawFormat(m, level, mask);
        if (version.Number >= 7)
        {
            var bits = VersionBits(version.Number);
            for (var i = 0; i < 18; i++)
            {
                var dark = (bits >> i & 1) != 0;
                int a1 = n - 11 + i % 3, b = i / 3;
                m[a1, b] = dark;
                m[b, a1] = dark;
            }
        }
    }

    /// <summary>The 15 bits of the format information (7.9.1): level and mask, BCH(15,5), XOR 101010000010010.</summary>
    public static int FormatBits(QrErrorCorrection level, int mask)
    {
        var data = LevelBits(level) << 3 | mask;
        return (data << 10 | Remainder(data << 10, 0x537, 10)) ^ FormatMask;
    }

    /// <summary>The 18 bits of the version information (7.10): version, BCH(18,6).</summary>
    public static int VersionBits(int version) => version << 12 | Remainder(version << 12, 0x1F25, 12);

    private static int Remainder(int value, int generator, int degree)
    {
        for (var bit = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)value); bit >= degree; bit--)
            if ((value >> bit & 1) != 0) value ^= generator << (bit - degree);
        return value;
    }

    /// <summary>Indicator of the level in the format information: L 01, M 00, Q 11, H 10.</summary>
    public static int LevelBits(QrErrorCorrection level) => level switch
    {
        QrErrorCorrection.L => 1,
        QrErrorCorrection.M => 0,
        QrErrorCorrection.Q => 3,
        _ => 2,
    };

    public static QrErrorCorrection LevelOf(int bits) => bits switch
    {
        1 => QrErrorCorrection.L,
        0 => QrErrorCorrection.M,
        3 => QrErrorCorrection.Q,
        _ => QrErrorCorrection.H,
    };

    /// <summary>Positions of the format bits 0 … 14 in both copies (figure 25): (x, y) pairs.</summary>
    public static ((int X, int Y) First, (int X, int Y) Second)[] FormatPositions(int n)
    {
        var p = new ((int, int), (int, int))[15];
        for (var i = 0; i < 15; i++)
        {
            (int, int) first = i < 6 ? (8, i) : i == 6 ? (8, 7) : i == 7 ? (8, 8) : i == 8 ? (7, 8) : (14 - i, 8);
            (int, int) second = i < 8 ? (n - 1 - i, 8) : (8, n - 15 + i);
            p[i] = (first, second);
        }
        return p;
    }

    private static void DrawFormat(BitMatrix m, QrErrorCorrection level, int mask)
    {
        var n = m.Width;
        var bits = FormatBits(level, mask);
        var positions = FormatPositions(n);
        for (var i = 0; i < 15; i++)
        {
            var dark = (bits >> i & 1) != 0;
            m[positions[i].First.X, positions[i].First.Y] = dark;
            m[positions[i].Second.X, positions[i].Second.Y] = dark;
        }
        m[8, n - 8] = true;   // the dark module
    }

    /// <summary>The data modules in placement order (7.7.3): two-column strips from the right, up and down in turn, column 6 skipped.</summary>
    public static IEnumerable<(int X, int Y)> DataOrder(bool[,] function)
    {
        var n = function.GetLength(0);
        for (var right = n - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            var upward = (right + 1 & 2) == 0;
            for (var v = 0; v < n; v++)
            {
                var y = upward ? n - 1 - v : v;
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    if (!function[x, y]) yield return (x, y);
                }
            }
        }
    }

    /// <summary>Whether data mask <paramref name="mask"/> inverts the module in row <paramref name="i"/>, column <paramref name="j"/> (table 10).</summary>
    public static bool Masked(int mask, int i, int j) => mask switch
    {
        0 => (i + j) % 2 == 0,
        1 => i % 2 == 0,
        2 => j % 3 == 0,
        3 => (i + j) % 3 == 0,
        4 => (i / 2 + j / 3) % 2 == 0,
        5 => i * j % 2 + i * j % 3 == 0,
        6 => (i * j % 2 + i * j % 3) % 2 == 0,
        _ => ((i + j) % 2 + i * j % 3) % 2 == 0,
    };
}
