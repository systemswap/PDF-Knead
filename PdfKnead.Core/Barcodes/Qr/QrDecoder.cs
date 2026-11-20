using System.Text;

namespace PdfKnead.Core.Barcodes.Qr;

/// <summary>What a QR symbol holds and how it is built.</summary>
public sealed record QrContent
{
    /// <summary>The text (all segments joined; GS1 group separators as U+001D).</summary>
    public required string Text { get; init; }

    /// <summary>Version 1 … 40.</summary>
    public int Version { get; init; }

    /// <summary>Error correction level.</summary>
    public QrErrorCorrection Level { get; init; }

    /// <summary>Data mask 0 … 7.</summary>
    public int Mask { get; init; }

    /// <summary>Modes used, in order, translated (numeric, alphanumeric, bytes, Kanji).</summary>
    public IReadOnlyList<string> Modes { get; init; } = [];

    /// <summary>Character set of the byte segments ("UTF-8" …), with "(ECI)" when the code names it itself; null without bytes.</summary>
    public string? Charset { get; init; }

    /// <summary>GS1 data (FNC1 in the first position).</summary>
    public bool Gs1 { get; init; }

    /// <summary>Part of a chain of symbols (structured append, 7.4.6): position (1-based), count and parity.</summary>
    public (int Index, int Count, int Parity)? Chain { get; init; }

    /// <summary>Codewords the error correction repaired.</summary>
    public int Corrected { get; init; }

    /// <summary>Error correction codewords in total and the most that can be repaired.</summary>
    public (int Check, int Repairable) Capacity { get; init; }

    /// <summary>The symbol was mirrored (some generators write it that way).</summary>
    public bool Mirrored { get; init; }
}

/// <summary>Reads a QR symbol from its module grid (ISO/IEC 18004:2015, clause 12, steps 4 … 11).</summary>
public static class QrDecoder
{
    /// <summary>Decodes the modules; also tries the mirrored symbol.</summary>
    /// <param name="modules">The grid, exactly the symbol (17 + 4·v modules per side), without quiet zone.</param>
    /// <returns>The content; null if it cannot be read.</returns>
    public static QrContent? Decode(BitMatrix modules)
    {
        if (modules.Width != modules.Height || QrVersion.ForDimension(modules.Width) is null) return null;
        return TryDecode(modules) ?? (TryDecode(modules.Transposed()) is { } m ? m with { Mirrored = true } : null);
    }

    private static QrContent? TryDecode(BitMatrix m)
    {
        var n = m.Width;
        var version = QrVersion.ForDimension(n)!;
        if (version.Number >= 7 && ReadVersion(m) is { } v && v != version.Number) return null;
        if (ReadFormat(m) is not var (level, mask)) return null;

        var raw = new byte[version.TotalCodewords];
        var i = 0;
        foreach (var (x, y) in QrLayout.Structure(version).Order)
        {
            if (i >= raw.Length * 8) break;
            if (m[x, y] ^ QrLayout.Masked(mask, y, x)) raw[i >> 3] |= (byte)(0x80 >> (i & 7));
            i++;
        }

        // undo the interleaving, correct every block
        var check = version.CheckPerBlock(level);
        var groups = version.Groups(level);
        var sizes = groups.SelectMany(g => Enumerable.Repeat(g.DataCodewords, g.Count)).ToArray();
        var blocks = sizes.Select(s => new byte[s + check]).ToArray();
        var at = 0;
        for (var k = 0; k < sizes.Max(); k++)
            for (var b = 0; b < blocks.Length; b++)
                if (k < sizes[b]) blocks[b][k] = raw[at++];
        for (var k = 0; k < check; k++)
            for (var b = 0; b < blocks.Length; b++)
                blocks[b][sizes[b] + k] = raw[at++];
        var corrected = 0;
        var data = new List<byte>();
        for (var b = 0; b < blocks.Length; b++)
        {
            var fixedCount = ReedSolomon.Qr.Correct(blocks[b], check);
            if (fixedCount < 0) return null;
            corrected += fixedCount;
            data.AddRange(blocks[b].AsSpan(0, sizes[b]).ToArray());
        }
        if (Segments([.. data], version.Number) is not { } content) return null;
        return content with
        {
            Version = version.Number, Level = level, Mask = mask, Corrected = corrected,
            Capacity = (check * blocks.Length, check / 2 * blocks.Length),
        };
    }

    /// <summary>Format information from both copies: the valid code nearest to either (at most 3 bits off, 7.9.2).</summary>
    private static (QrErrorCorrection Level, int Mask)? ReadFormat(BitMatrix m)
    {
        var positions = QrLayout.FormatPositions(m.Width);
        int first = 0, second = 0;
        for (var i = 0; i < 15; i++)
        {
            if (m[positions[i].First.X, positions[i].First.Y]) first |= 1 << i;
            if (m[positions[i].Second.X, positions[i].Second.Y]) second |= 1 << i;
        }
        (QrErrorCorrection, int)? best = null;
        var bestDistance = 4;
        foreach (var level in Enum.GetValues<QrErrorCorrection>())
            for (var mask = 0; mask < 8; mask++)
            {
                var code = QrLayout.FormatBits(level, mask);
                var d = Math.Min(System.Numerics.BitOperations.PopCount((uint)(code ^ first)), System.Numerics.BitOperations.PopCount((uint)(code ^ second)));
                if (d >= bestDistance) continue;
                bestDistance = d;
                best = (level, mask);
            }
        return best;
    }

    /// <summary>Version information (versions 7 and up) from either copy, at most 3 bits off; null if neither is readable.</summary>
    private static int? ReadVersion(BitMatrix m)
    {
        var n = m.Width;
        int a = 0, b = 0;
        for (var i = 0; i < 18; i++)
        {
            int p = n - 11 + i % 3, q = i / 3;
            if (m[p, q]) a |= 1 << i;
            if (m[q, p]) b |= 1 << i;
        }
        int? best = null;
        var bestDistance = 4;
        for (var v = 7; v <= 40; v++)
        {
            var code = QrLayout.VersionBits(v);
            var d = Math.Min(System.Numerics.BitOperations.PopCount((uint)(code ^ a)), System.Numerics.BitOperations.PopCount((uint)(code ^ b)));
            if (d >= bestDistance) continue;
            bestDistance = d;
            best = v;
        }
        return best;
    }

    /// <summary>The data segments (7.4): mode indicator, character count, data; until the terminator or the end.</summary>
    private static QrContent? Segments(byte[] data, int version)
    {
        var reader = new BitReader(data);
        var text = new StringBuilder();
        var modes = new List<string>();
        string? charset = null;
        Encoding? eci = null;
        var gs1 = false;
        (int, int, int)? chain = null;
        var group = version <= 9 ? 0 : version <= 26 ? 1 : 2;
        var bytes = new List<byte>();
        void FlushBytes()
        {
            if (bytes.Count == 0) return;
            if (eci is not null) text.Append(eci.GetString([.. bytes]));
            else
            {
                var (s, guessed) = Eci.Guess([.. bytes]);
                text.Append(s);
                charset ??= guessed;
            }
            bytes.Clear();
        }
        while (reader.Available >= 4)
        {
            var mode = reader.Read(4);
            if (mode == 0) break;
            switch (mode)
            {
                case 0b0111:   // ECI
                    FlushBytes();
                    var first = reader.Read(8);
                    var number = (first & 0x80) == 0 ? first
                        : (first & 0xC0) == 0x80 ? (first & 0x3F) << 8 | reader.Read(8)
                        : (first & 0x1F) << 16 | reader.Read(16);
                    eci = Eci.Charset(number);
                    charset = Eci.Name(number) + " (ECI)";
                    break;
                case 0b0101:   // FNC1, first position: GS1
                    gs1 = true;
                    break;
                case 0b1001:   // FNC1, second position: application indicator
                    reader.Read(8);
                    break;
                case 0b0011:   // structured append
                    var position = reader.Read(4);
                    var total = reader.Read(4);
                    chain = (position + 1, total + 1, reader.Read(8));
                    break;
                case 0b0001:
                {
                    FlushBytes();
                    modes.Add(Tr.T("Ziffern"));
                    var count = reader.Read(new[] { 10, 12, 14 }[group]);
                    for (; count >= 3; count -= 3)
                    {
                        var v = reader.Read(10);
                        if (v > 999) return null;
                        text.Append(v.ToString("000"));
                    }
                    if (count == 2) text.Append(reader.Read(7).ToString("00"));
                    else if (count == 1) text.Append(reader.Read(4).ToString("0"));
                    break;
                }
                case 0b0010:
                {
                    FlushBytes();
                    modes.Add(Tr.T("Alphanumerisch"));
                    var count = reader.Read(new[] { 9, 11, 13 }[group]);
                    var start = text.Length;
                    for (; count >= 2; count -= 2)
                    {
                        var v = reader.Read(11);
                        if (v >= 45 * 45) return null;
                        text.Append(QrEncoder.Alphanumeric[v / 45]).Append(QrEncoder.Alphanumeric[v % 45]);
                    }
                    if (count == 1)
                    {
                        var v = reader.Read(6);
                        if (v >= 45) return null;
                        text.Append(QrEncoder.Alphanumeric[v]);
                    }
                    // GS1: "%" stands for the group separator, "%%" for a percent sign (7.4.8.2)
                    if (gs1)
                    {
                        var part = text.ToString(start, text.Length - start).Replace("%%", "\u0001").Replace('%', '\u001D').Replace('\u0001', '%');
                        text.Length = start;
                        text.Append(part);
                    }
                    break;
                }
                case 0b0100:
                {
                    if (!modes.Contains(Tr.T("Bytes"))) modes.Add(Tr.T("Bytes"));
                    var count = reader.Read(new[] { 8, 16, 16 }[group]);
                    if (count * 8 > reader.Available) return null;
                    for (var k = 0; k < count; k++) bytes.Add((byte)reader.Read(8));
                    break;
                }
                case 0b1000:
                {
                    FlushBytes();
                    modes.Add(Tr.T("Kanji"));
                    var count = reader.Read(new[] { 8, 10, 12 }[group]);
                    if (count * 13 > reader.Available) return null;
                    var sjis = new byte[count * 2];
                    for (var k = 0; k < count; k++)
                    {
                        var v = reader.Read(13);
                        var code = (v / 0xC0) << 8 | v % 0xC0;
                        code += code < 0x1F00 ? 0x8140 : 0xC140;
                        sjis[2 * k] = (byte)(code >> 8);
                        sjis[2 * k + 1] = (byte)code;
                    }
                    text.Append((Eci.Charset(20) ?? Encoding.Latin1).GetString(sjis));
                    break;
                }
                default:
                    return null;
            }
            if (reader.Overrun) return null;
        }
        FlushBytes();
        if (reader.Overrun) return null;
        return new QrContent { Text = text.ToString(), Modes = modes, Charset = charset, Gs1 = gs1, Chain = chain };
    }
}

/// <summary>Reads bits from the most significant end; reading past the end gives zeros and sets <see cref="Overrun"/>.</summary>
internal sealed class BitReader(byte[] data)
{
    private int _position;

    public int Available => Math.Max(0, data.Length * 8 - _position);

    public bool Overrun { get; private set; }

    public int Read(int count)
    {
        var value = 0;
        for (var i = 0; i < count; i++, _position++)
        {
            var bit = _position < data.Length * 8 && (data[_position >> 3] >> (7 - (_position & 7)) & 1) != 0;
            if (_position >= data.Length * 8) Overrun = true;
            value = value << 1 | (bit ? 1 : 0);
        }
        return value;
    }
}
