using System.Text;

namespace PdfKnead.Core.Barcodes.DataMatrix;

/// <summary>What a Data Matrix symbol holds and how it is built.</summary>
public sealed record DataMatrixContent
{
    /// <summary>The text (GS1 group separators as U+001D).</summary>
    public required string Text { get; init; }

    /// <summary>Rows of modules.</summary>
    public int Rows { get; init; }

    /// <summary>Columns of modules.</summary>
    public int Columns { get; init; }

    /// <summary>Encodation schemes used, in order ("ASCII", "C40", "Text", "X12", "EDIFACT", "Base 256").</summary>
    public IReadOnlyList<string> Schemes { get; init; } = [];

    /// <summary>Character set of byte data, with "(ECI)" when the symbol names it; null when everything is ASCII.</summary>
    public string? Charset { get; init; }

    /// <summary>GS1 data (FNC1 in the first position).</summary>
    public bool Gs1 { get; init; }

    /// <summary>Message envelope "[)>RS05GS" or "[)>RS06GS" (macro 05/06, ISO/IEC 15434).</summary>
    public string? Macro { get; init; }

    /// <summary>Part of a chain (structured append): position, count.</summary>
    public (int Index, int Count)? Chain { get; init; }

    /// <summary>Codewords the error correction repaired.</summary>
    public int Corrected { get; init; }

    /// <summary>Check codewords and the most that can be repaired.</summary>
    public (int Check, int Repairable) Capacity { get; init; }
}

/// <summary>Shape of a Data Matrix symbol to write.</summary>
public enum DataMatrixShape
{
    /// <summary>Square symbols only.</summary>
    Square,

    /// <summary>Rectangular symbols only (8 × 18 … 16 × 48).</summary>
    Rectangular,

    /// <summary>The smallest of all; with equal capacity the square one.</summary>
    Any,
}

/// <summary>Reads and writes ECC 200 Data Matrix symbols (ISO/IEC 16022:2006).</summary>
public static class DataMatrixCodec
{
    // ------------------------------------------------------------ symbol ↔ mapping matrix

    private static bool[] ToMapping(BitMatrix m, DataMatrixSymbol s)
    {
        var mapping = new bool[s.MappingRows * s.MappingColumns];
        for (var r = 0; r < s.MappingRows; r++)
            for (var c = 0; c < s.MappingColumns; c++)
                mapping[r * s.MappingColumns + c] = m[c + 2 * (c / s.RegionColumns) + 1, r + 2 * (r / s.RegionRows) + 1];
        return mapping;
    }

    private static BitMatrix FromMapping(bool[] mapping, DataMatrixSymbol s)
    {
        var m = new BitMatrix(s.Columns, s.Rows);
        int blockH = s.RegionRows + 2, blockW = s.RegionColumns + 2;
        for (var y = 0; y < s.Rows; y++)
            for (var x = 0; x < s.Columns; x++)
            {
                int by = y % blockH, bx = x % blockW;
                // finder: left column and bottom row of every region solid; timing: top row and right column alternating
                m[x, y] = bx == 0 || by == blockH - 1 || (by == 0 ? bx % 2 == 0 : bx == blockW - 1 && by % 2 == 1);
            }
        for (var r = 0; r < s.MappingRows; r++)
            for (var c = 0; c < s.MappingColumns; c++)
                m[c + 2 * (c / s.RegionColumns) + 1, r + 2 * (r / s.RegionRows) + 1] = mapping[r * s.MappingColumns + c];
        return m;
    }

    // ------------------------------------------------------------ decoding

    /// <summary>Decodes a symbol from its modules (finder at the left and bottom); also tries the other three orientations.</summary>
    /// <param name="modules">Exactly the symbol, without quiet zone.</param>
    /// <returns>The content; null if it cannot be read.</returns>
    public static DataMatrixContent? Decode(BitMatrix modules)
    {
        var m = modules;
        for (var turn = 0; turn < 4; turn++, m = m.Rotated())
            if (DataMatrixSymbol.Of(m.Height, m.Width) is { } s && HasFinder(m) && TryDecode(m, s) is { } content)
                return content;
        return null;
    }

    private static bool HasFinder(BitMatrix m)
    {
        // most modules of the left column and the bottom row dark (damage tolerated)
        var dark = 0;
        for (var y = 0; y < m.Height; y++) if (m[0, y]) dark++;
        for (var x = 0; x < m.Width; x++) if (m[x, m.Height - 1]) dark++;
        return dark >= (m.Width + m.Height) * 0.8;
    }

    private static DataMatrixContent? TryDecode(BitMatrix m, DataMatrixSymbol s)
    {
        var mapping = ToMapping(m, s);
        var map = DataMatrixPlacement.Cached(s.MappingRows, s.MappingColumns);
        var total = s.DataCodewords + s.CheckCodewords;
        var codewords = new byte[total];
        for (var i = 0; i < map.Length; i++)
        {
            var (chr, bit) = map[i];
            if (chr == 0 || chr > total || !mapping[i]) continue;
            codewords[chr - 1] |= (byte)(0x80 >> (bit - 1));
        }
        // de-interleave: codeword i belongs to block i mod blocks (5.7.2)
        var blocks = s.Blocks;
        var checkPer = s.CheckCodewords / blocks;
        var data = new byte[s.DataCodewords];
        var corrected = 0;
        for (var b = 0; b < blocks; b++)
        {
            var size = s.DataInBlock(b);
            var block = new byte[size + checkPer];
            for (var i = 0; i < size; i++) block[i] = codewords[b + i * blocks];
            for (var i = 0; i < checkPer; i++) block[size + i] = codewords[s.DataCodewords + b + i * blocks];
            var fixedCount = ReedSolomon.DataMatrix.Correct(block, checkPer);
            if (fixedCount < 0) return null;
            corrected += fixedCount;
            for (var i = 0; i < size; i++) data[b + i * blocks] = block[i];
        }
        return Interpret(data) is { } content
            ? content with { Rows = s.Rows, Columns = s.Columns, Corrected = corrected, Capacity = (s.CheckCodewords, checkPer / 2 * blocks) }
            : null;
    }

    private static readonly string C40Shift2 = "!\"#$%&'()*+,-./:;<=>?@[\\]^_";

    /// <summary>The data codewords in their encodation schemes (5.2).</summary>
    private static DataMatrixContent? Interpret(byte[] data)
    {
        var bytes = new List<byte>();   // characters as bytes (for the character set), GS = 0x1D
        var text = new StringBuilder();
        var schemes = new List<string>();
        Encoding? eci = null;
        string? charset = null;
        bool gs1 = false, upper = false;
        string? macro = null;
        (int, int)? chain = null;
        void Flush()
        {
            if (bytes.Count == 0) return;
            if (eci is not null) text.Append(eci.GetString([.. bytes]));
            else if (bytes.Any(b => b >= 0x80))
            {
                var (t, guessed) = Eci.Guess([.. bytes]);
                text.Append(t);
                charset ??= guessed;
            }
            else text.Append(Encoding.ASCII.GetString([.. bytes]));
            bytes.Clear();
        }
        void Add(int value)
        {
            bytes.Add((byte)(upper ? value + 128 : value));
            upper = false;
        }
        void Scheme(string name)
        {
            if (schemes.Count == 0 || schemes[^1] != name) schemes.Add(name);
        }

        var i = 0;
        while (i < data.Length)
        {
            int c = data[i++];
            Scheme("ASCII");
            if (c == 129) break;   // padding: end of data
            if (c is >= 1 and <= 128) Add(c - 1);
            else if (c is >= 130 and <= 229)
            {
                var pair = (c - 130).ToString("00");
                Add(pair[0]);
                Add(pair[1]);
            }
            else switch (c)
            {
                case 230 or 239:   // C40, Text
                {
                    var isText = c == 239;
                    Scheme(isText ? "Text" : "C40");
                    var shift = 0;
                    while (i + 1 < data.Length && data[i] != 254)
                    {
                        var v = data[i] * 256 + data[i + 1] - 1;
                        i += 2;
                        // crafted pairs: values beyond the three of 0 … 39 (from 64 000 on, or −1 from two zeros) are no characters
                        if (v is < 0 or >= 64000) continue;
                        foreach (var value in new[] { v / 1600, v / 40 % 40, v % 40 })
                        {
                            switch (shift)
                            {
                                case 0:
                                    if (value < 3) shift = value + 1;
                                    else if (value == 3) Add(' ');
                                    else if (value < 14) Add('0' + value - 4);
                                    else if (value < 40) Add((isText ? 'a' : 'A') + value - 14);
                                    continue;
                                case 1:
                                    // shift 1: the control characters 0 … 31; larger values are invalid and skipped
                                    if (value < 32) Add(value);
                                    break;
                                case 2:
                                    if (value < C40Shift2.Length) Add(C40Shift2[value]);
                                    else if (value == 27) Add(0x1D);   // FNC1 as a separator
                                    else if (value == 30) upper = true;
                                    break;
                                default:
                                    // shift 3: 0 … 31 only
                                    if (value < 32) Add(isText ? value == 0 ? '`' : value < 27 ? 'A' + value - 1 : "{|}~\u007f"[value - 27] : 96 + value);
                                    break;
                            }
                            shift = 0;
                        }
                    }
                    if (i < data.Length && data[i] == 254) i++;
                    break;
                }
                case 238:   // X12
                    Scheme("X12");
                    while (i + 1 < data.Length && data[i] != 254)
                    {
                        var v = data[i] * 256 + data[i + 1] - 1;
                        i += 2;
                        if (v is < 0 or >= 64000) continue;
                        foreach (var value in new[] { v / 1600, v / 40 % 40, v % 40 })
                            Add(value switch { 0 => '\r', 1 => '*', 2 => '>', 3 => ' ', < 14 => '0' + value - 4, _ => 'A' + value - 14 });
                    }
                    if (i < data.Length && data[i] == 254) i++;
                    break;
                case 240:   // EDIFACT: four 6-bit values per three codewords
                {
                    // 011111 returns to ASCII from the next codeword on (the rest of the one it ends in is padding); with fewer than three
                    // codewords left in the symbol, EDIFACT ends without it and the rest is ASCII (5.2.8.2)
                    Scheme("EDIFACT");
                    var done = false;
                    while (!done && i + 3 <= data.Length)
                    {
                        var bits = data[i] << 16 | data[i + 1] << 8 | data[i + 2];
                        var used = 3;
                        for (var k = 0; k < 4; k++)
                        {
                            var v = bits >> (18 - 6 * k) & 0x3F;
                            if (v == 0x1F)
                            {
                                done = true;
                                used = (6 * (k + 1) + 7) / 8;
                                break;
                            }
                            Add(v < 32 ? v | 0x40 : v);
                        }
                        i += used;
                    }
                    break;
                }
                case 231:   // Base 256, randomised with the 255-state algorithm (annex B)
                {
                    Scheme("Base 256");
                    int Unrandomise(int position) => (data[position] - (149 * (position + 1) % 255 + 1) + 256) % 256;
                    if (i >= data.Length) return null;
                    var length = Unrandomise(i++);
                    if (length == 0) length = data.Length - i;
                    else if (length >= 250)
                    {
                        if (i >= data.Length) return null;
                        length = 250 * (length - 249) + Unrandomise(i++);
                    }
                    if (i + length > data.Length) return null;
                    Flush();
                    for (var k = 0; k < length; k++) bytes.Add((byte)Unrandomise(i++));
                    break;
                }
                case 232:   // FNC1: first position GS1, later a group separator
                    if (text.Length == 0 && bytes.Count == 0) gs1 = true;
                    else Add(0x1D);
                    break;
                case 233:   // structured append: position/count, then a file identification
                    if (i + 2 >= data.Length) return null;
                    chain = ((data[i] >> 4) + 1, 17 - (data[i] & 0x0F));
                    i += 3;
                    break;
                case 234:   // reader programming
                    break;
                case 235:
                    upper = true;
                    break;
                case 236 or 237:
                    macro = c == 236 ? "05" : "06";
                    Add('['); Add(')'); Add('>'); Add(0x1E); Add('0'); Add(c == 236 ? '5' : '6'); Add(0x1D);
                    break;
                case 241:   // ECI: 1 … 3 codewords (5.4.1)
                {
                    if (i >= data.Length) return null;
                    int a = data[i++], number;
                    if (a <= 127) number = a - 1;
                    else if (a <= 191)
                    {
                        if (i >= data.Length) return null;
                        number = (a - 128) * 254 + data[i++] - 1 + 127;
                    }
                    else
                    {
                        if (i + 1 >= data.Length) return null;
                        number = (a - 192) * 64516 + (data[i] - 1) * 254 + data[i + 1] - 1 + 16383;
                        i += 2;
                    }
                    Flush();
                    eci = Eci.Charset(number);
                    charset = Eci.Name(number) + " (ECI)";
                    break;
                }
                default:
                    return null;
            }
        }
        if (macro is not null)
        {
            Add(0x1E);
            Add(0x04);
        }
        Flush();
        return new DataMatrixContent { Text = text.ToString(), Schemes = schemes, Charset = charset, Gs1 = gs1, Macro = macro, Chain = chain };
    }

    // ------------------------------------------------------------ encoding

    /// <summary>
    /// Encodes a text in ASCII (digit pairs packed, characters above 127 with Upper Shift; text outside ISO-8859-1 as UTF-8 with ECI 26) into the
    /// smallest symbol that holds it.
    /// </summary>
    /// <param name="text">The content.</param>
    /// <param name="shape">Which symbols may be used.</param>
    /// <param name="gs1">GS1 data: FNC1 first, group separators (U+001D) as FNC1.</param>
    /// <returns>The modules, without quiet zone.</returns>
    /// <exception cref="ArgumentException">The text is too long for the largest symbol.</exception>
    public static BitMatrix Encode(string text, DataMatrixShape shape = DataMatrixShape.Square, bool gs1 = false)
    {
        var codewords = new List<byte>();
        if (gs1) codewords.Add(232);
        byte[] bytes;
        if (text.Any(ch => ch > 0xFF))
        {
            codewords.Add(241);
            codewords.Add(27);   // ECI 26 (UTF-8)
            bytes = Encoding.UTF8.GetBytes(text);
        }
        else bytes = Encoding.Latin1.GetBytes(text);
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (char.IsAsciiDigit((char)b) && i + 1 < bytes.Length && char.IsAsciiDigit((char)bytes[i + 1]))
            {
                codewords.Add((byte)(130 + (b - '0') * 10 + bytes[i + 1] - '0'));
                i++;
            }
            else if (gs1 && b == 0x1D) codewords.Add(232);
            else if (b >= 128)
            {
                codewords.Add(235);
                codewords.Add((byte)(b - 128 + 1));
            }
            else codewords.Add((byte)(b + 1));
        }
        var symbol = DataMatrixSymbol.All
            .Where(s => shape == DataMatrixShape.Any || s.Square == (shape == DataMatrixShape.Square))
            .Where(s => s.DataCodewords >= codewords.Count)
            .MinBy(s => s.DataCodewords) ?? throw new ArgumentException(Tr.T("Der Inhalt passt in keinen Data-Matrix-Code dieser Form."));
        // padding: 129, then randomised 253-state values (5.2.3)
        if (codewords.Count < symbol.DataCodewords) codewords.Add(129);
        while (codewords.Count < symbol.DataCodewords)
        {
            var position = codewords.Count + 1;
            var pad = 129 + 149 * position % 253 + 1;
            codewords.Add((byte)(pad <= 254 ? pad : pad - 254));
        }
        return Place([.. codewords], symbol);
    }

    /// <summary>
    /// A symbol from data codewords as they are (with check codewords and placement) — also data that no writer would produce, to check
    /// the reader against crafted symbols.
    /// </summary>
    internal static BitMatrix Place(byte[] codewords, DataMatrixSymbol symbol)
    {
        var all = new byte[symbol.DataCodewords + symbol.CheckCodewords];
        codewords.AsSpan(0, Math.Min(codewords.Length, symbol.DataCodewords)).CopyTo(all);
        var blocks = symbol.Blocks;
        var checkPer = symbol.CheckCodewords / blocks;
        for (var b = 0; b < blocks; b++)
        {
            var size = symbol.DataInBlock(b);
            var block = new byte[size];
            for (var i = 0; i < size; i++) block[i] = all[b + i * blocks];
            var check = ReedSolomon.DataMatrix.Encode(block, checkPer);
            for (var i = 0; i < checkPer; i++) all[symbol.DataCodewords + b + i * blocks] = check[i];
        }
        var map = DataMatrixPlacement.Cached(symbol.MappingRows, symbol.MappingColumns);
        var mapping = new bool[map.Length];
        for (var i = 0; i < map.Length; i++)
        {
            var (chr, bit) = map[i];
            mapping[i] = chr == 0 ? bit == 1 : (all[chr - 1] >> (8 - bit) & 1) != 0;
        }
        return FromMapping(mapping, symbol);
    }
}
