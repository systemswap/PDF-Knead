using System.Text;

namespace PdfKnead.Core.Barcodes.Qr;

/// <summary>
/// Writes QR codes (ISO/IEC 18004:2015, clause 7): the text in the most compact single mode (numeric, alphanumeric, otherwise bytes in
/// UTF-8 — with ECI 26 when it contains characters outside ASCII, so that every reader knows the character set), the smallest version that
/// holds it, check codewords, interleaving, placement and the mask with the lowest penalty (7.8.3).
/// </summary>
public static class QrEncoder
{
    internal const string Alphanumeric = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    /// <summary>Encodes a text.</summary>
    /// <param name="text">The content.</param>
    /// <param name="level">Error correction level.</param>
    /// <param name="minimumVersion">Smallest version to use (1 … 40).</param>
    /// <param name="mask">Data mask 0 … 7, or −1 for the best one.</param>
    /// <param name="gs1">GS1 data (FNC1 in the first position, 7.4.8.2); group separators (U+001D) end variable-length fields.</param>
    /// <returns>The modules, without quiet zone.</returns>
    /// <exception cref="ArgumentException">The text does not fit into version 40 at this level.</exception>
    public static BitMatrix Encode(string text, QrErrorCorrection level = QrErrorCorrection.M, int minimumVersion = 1, int mask = -1, bool gs1 = false)
    {
        var (bits, version) = Data(text, level, minimumVersion, gs1);
        var data = new byte[bits.Count / 8];
        for (var i = 0; i < bits.Count; i++)
            if (bits[i]) data[i >> 3] |= (byte)(0x80 >> (i & 7));
        return Place(data, version, level, mask);
    }

    /// <summary>
    /// A symbol from data codewords as they are (with check codewords, interleaving, placement and mask) — also data that no writer
    /// would produce, to check the reader against crafted symbols.
    /// </summary>
    internal static BitMatrix Place(byte[] data, QrVersion version, QrErrorCorrection level, int mask = -1)
    {
        var codewords = Interleave(data, version, level);
        var order = QrLayout.Structure(version).Order;
        var best = (Matrix: (BitMatrix?)null, Penalty: int.MaxValue);
        for (var m = 0; m < 8; m++)
        {
            if (mask >= 0 && m != mask) continue;
            var matrix = new BitMatrix(version.Dimension, version.Dimension);
            QrLayout.DrawFunctionPatterns(matrix, version, level, m);
            var i = 0;
            foreach (var (x, y) in order)
            {
                var dark = i < codewords.Length * 8 && (codewords[i >> 3] >> (7 - (i & 7)) & 1) != 0;
                matrix[x, y] = dark ^ QrLayout.Masked(m, y, x);
                i++;
            }
            var penalty = mask >= 0 ? 0 : Penalty(matrix);
            if (penalty < best.Penalty) best = (matrix, penalty);
        }
        return best.Matrix!;
    }

    private enum Mode { Numeric = 1, Alphanumeric = 2, Byte = 4 }

    private static (List<bool> Bits, QrVersion Version) Data(string text, QrErrorCorrection level, int minimumVersion, bool gs1)
    {
        var mode = text.All(char.IsAsciiDigit) ? Mode.Numeric : text.All(c => Alphanumeric.Contains(c)) && !gs1 ? Mode.Alphanumeric : Mode.Byte;
        // GS1 in byte mode: group separators stay as 0x1D (7.4.8.2)
        var bytes = Encoding.UTF8.GetBytes(text);
        var eci = mode == Mode.Byte && bytes.Any(b => b >= 0x80);
        for (var v = Math.Clamp(minimumVersion, 1, 40); v <= 40; v++)
        {
            var version = QrVersion.Get(v);
            var bits = new List<bool>();
            void Put(int value, int count)
            {
                for (var i = count - 1; i >= 0; i--) bits.Add((value >> i & 1) != 0);
            }
            if (gs1) Put(0b0101, 4);
            if (eci)
            {
                Put(0b0111, 4);
                Put(26, 8);   // UTF-8
            }
            var group = v <= 9 ? 0 : v <= 26 ? 1 : 2;
            Put((int)mode, 4);
            switch (mode)
            {
                case Mode.Numeric:
                    Put(text.Length, new[] { 10, 12, 14 }[group]);
                    for (var i = 0; i < text.Length; i += 3)
                    {
                        var chunk = text.Substring(i, Math.Min(3, text.Length - i));
                        Put(int.Parse(chunk), chunk.Length * 3 + 1);
                    }
                    break;
                case Mode.Alphanumeric:
                    Put(text.Length, new[] { 9, 11, 13 }[group]);
                    for (var i = 0; i < text.Length; i += 2)
                        if (i + 1 < text.Length) Put(Alphanumeric.IndexOf(text[i]) * 45 + Alphanumeric.IndexOf(text[i + 1]), 11);
                        else Put(Alphanumeric.IndexOf(text[i]), 6);
                    break;
                default:
                    Put(bytes.Length, new[] { 8, 16, 16 }[group]);
                    foreach (var b in bytes) Put(b, 8);
                    break;
            }
            var capacity = version.DataCodewords(level) * 8;
            if (bits.Count > capacity) continue;
            // terminator, byte boundary, pad codewords 11101100 00010001 (7.4.10)
            for (var i = 0; i < 4 && bits.Count < capacity; i++) bits.Add(false);
            while (bits.Count % 8 != 0) bits.Add(false);
            for (var pad = 0; bits.Count < capacity; pad++) Put(pad % 2 == 0 ? 0xEC : 0x11, 8);
            return (bits, version);
        }
        throw new ArgumentException(Tr.T("Der Inhalt passt in keinen QR-Code dieser Stufe."));
    }

    /// <summary>Data split into blocks, check codewords per block, both interleaved (7.6).</summary>
    private static byte[] Interleave(byte[] data, QrVersion version, QrErrorCorrection level)
    {
        var check = version.CheckPerBlock(level);
        var blocks = new List<(byte[] Data, byte[] Check)>();
        var at = 0;
        foreach (var g in version.Groups(level))
            for (var b = 0; b < g.Count; b++)
            {
                var d = data.AsSpan(at, g.DataCodewords).ToArray();
                at += g.DataCodewords;
                blocks.Add((d, ReedSolomon.Qr.Encode(d, check)));
            }
        var result = new List<byte>(version.TotalCodewords);
        var longest = blocks.Max(b => b.Data.Length);
        for (var i = 0; i < longest; i++)
            foreach (var b in blocks)
                if (i < b.Data.Length) result.Add(b.Data[i]);
        for (var i = 0; i < check; i++)
            foreach (var b in blocks) result.Add(b.Check[i]);
        return [.. result];
    }

    /// <summary>Penalty of a masked symbol (7.8.3.1): runs of the same colour, 2 × 2 blocks, finder-like patterns, dark proportion.</summary>
    private static int Penalty(BitMatrix m)
    {
        var n = m.Width;
        var penalty = 0;
        for (var pass = 0; pass < 2; pass++)
            for (var a = 0; a < n; a++)
            {
                var run = 1;
                for (var b = 1; b <= n; b++)
                {
                    var same = b < n && Get(a, b) == Get(a, b - 1);
                    if (same)
                    {
                        run++;
                        continue;
                    }
                    if (run >= 5) penalty += 3 + run - 5;
                    run = 1;
                }
                // 1:1:3:1:1 with four light modules on one side
                for (var b = 0; b + 10 < n; b++)
                {
                    bool P(int k, bool dark) => Get(a, b + k) == dark;
                    if (P(0, true) && P(1, false) && P(2, true) && P(3, true) && P(4, true) && P(5, false) && P(6, true)
                        && P(7, false) && P(8, false) && P(9, false) && P(10, false))
                        penalty += 40;
                    if (P(0, false) && P(1, false) && P(2, false) && P(3, false) && P(4, true) && P(5, false) && P(6, true)
                        && P(7, true) && P(8, true) && P(9, false) && P(10, true))
                        penalty += 40;
                }
                bool Get(int line, int pos) => pass == 0 ? m[pos, line] : m[line, pos];
            }
        for (var y = 0; y + 1 < n; y++)
            for (var x = 0; x + 1 < n; x++)
            {
                var c = m[x, y];
                if (m[x + 1, y] == c && m[x, y + 1] == c && m[x + 1, y + 1] == c) penalty += 3;
            }
        var dark = 0;
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
                if (m[x, y]) dark++;
        var percent = dark * 100 / (n * n);
        penalty += Math.Abs(percent - 50) / 5 * 10;
        return penalty;
    }
}
