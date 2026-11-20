namespace PdfKnead.Core.Pdf.Filters.Jbig2;

/// <summary>Reads bits from segment data, most significant first (Huffman-coded parts of JBIG2).</summary>
internal sealed class Jbig2BitReader(byte[] data, int start, int end)
{
    private long _pos = (long)start * 8;

    // The end is at most the end of the data (a segment can state a greater length than the file has; found by fuzzing)
    private readonly long _limit = (long)Math.Clamp(end, 0, data.Length) * 8;

    public int BytePosition => (int)((_pos + 7) >> 3);

    public int ReadBit()
    {
        if (_pos >= _limit || _pos < 0) throw new Jbig2Exception("JBIG2: Daten zu kurz");
        var bit = (data[(int)(_pos >> 3)] >> (7 - (int)(_pos & 7))) & 1;
        _pos++;
        return bit;
    }

    public long ReadBits(int n)
    {
        var v = 0L;
        for (var i = 0; i < n; i++) v = (v << 1) | (uint)ReadBit();
        return v;
    }

    public void Align() => _pos = (_pos + 7) & ~7L;

    /// <summary>Aligns and then skips <paramref name="bytes"/> bytes (they contained arithmetically coded data).</summary>
    public void SkipBytes(int bytes) => _pos = Math.Min(_limit, ((_pos + 7) & ~7L) + bytes * 8L);
}

/// <summary>
/// Huffman table (ITU-T T.88, Annex B): lines of prefix length, range length and range start,
/// plus the lower/upper range lines and OOB. The codes are assigned in order as in B.3 (canonical).
/// </summary>
internal sealed class Jbig2Huffman
{
    private enum Kind : byte { Normal, Lower, Upper, Oob }

    private readonly record struct Line(int PrefixLength, int RangeLength, long Low, Kind Kind);

    // Binary tree: children per node; negative = leaf (−1 − line index), 0 = empty (so far)
    private readonly List<int> _children = [0, 0];
    private readonly Line[] _lines;

    private Jbig2Huffman(List<Line> lines)
    {
        _lines = [.. lines];
        var maxLength = 0;
        foreach (var l in _lines) maxLength = Math.Max(maxLength, l.PrefixLength);
        if (maxLength > 32) throw new Jbig2Exception("JBIG2: Huffman-Tabelle");
        var counts = new int[maxLength + 1];
        foreach (var l in _lines) counts[l.PrefixLength]++;
        counts[0] = 0;
        var firstCode = 0L;
        for (var length = 1; length <= maxLength; length++)
        {
            firstCode = (firstCode + counts[length - 1]) << 1;
            var code = firstCode;
            for (var i = 0; i < _lines.Length; i++)
            {
                if (_lines[i].PrefixLength != length) continue;
                Insert(code++, length, i);
            }
        }
    }

    private void Insert(long code, int length, int line)
    {
        var node = 0;
        for (var bit = length - 1; bit >= 0; bit--)
        {
            var slot = node * 2 + (int)((code >> bit) & 1);
            if (bit == 0)
            {
                if (_children[slot] != 0) throw new Jbig2Exception("JBIG2: Huffman-Tabelle");
                _children[slot] = -1 - line;
                return;
            }
            if (_children[slot] < 0) throw new Jbig2Exception("JBIG2: Huffman-Tabelle");
            if (_children[slot] == 0)
            {
                _children[slot] = _children.Count / 2;
                _children.Add(0);
                _children.Add(0);
            }
            node = _children[slot];
        }
    }

    /// <summary>Decodes the next value; null = OOB.</summary>
    public int? Decode(Jbig2BitReader reader)
    {
        var node = 0;
        while (true)
        {
            var next = _children[node * 2 + reader.ReadBit()];
            if (next == 0) throw new Jbig2Exception("JBIG2: ungültiger Huffman-Code");
            if (next > 0)
            {
                node = next;
                continue;
            }
            var line = _lines[-1 - next];
            if (line.Kind == Kind.Oob) return null;
            var offset = reader.ReadBits(line.RangeLength);
            var value = line.Kind == Kind.Lower ? line.Low - offset : line.Low + offset;
            return (int)Math.Clamp(value, int.MinValue / 2, int.MaxValue / 2);
        }
    }

    /// <summary>Like <see cref="Decode"/>, but OOB is an error here.</summary>
    public int Value(Jbig2BitReader reader) => Decode(reader) ?? throw new Jbig2Exception("JBIG2: unerwartetes OOB");

    // ------------------------------------------------------------ Building tables

    /// <summary>Builds a table from code lengths per value (symbol IDs, run-length codes): value i has length lengths[i].</summary>
    public static Jbig2Huffman FromLengths(IReadOnlyList<int> lengths)
    {
        var lines = new List<Line>(lengths.Count);
        for (var i = 0; i < lengths.Count; i++) lines.Add(new Line(lengths[i], 0, i, Kind.Normal));
        return new Jbig2Huffman(lines);
    }

    /// <summary>Builds a custom table from a table segment (ITU-T T.88, 7.4.13, B.2).</summary>
    public static Jbig2Huffman FromSegment(byte[] data, int start, int end)
    {
        var flags = Jbig2Read.U8(data, start, end);
        var hasOob = (flags & 1) != 0;
        var prefixBits = ((flags >> 1) & 7) + 1;
        var rangeBits = ((flags >> 4) & 7) + 1;
        long low = Jbig2Read.I32(data, start + 1, end), high = Jbig2Read.I32(data, start + 5, end);
        var reader = new Jbig2BitReader(data, start + 9, end);
        var lines = new List<Line>();
        for (var current = low; current < high;)
        {
            var prefix = (int)reader.ReadBits(prefixBits);
            var range = (int)reader.ReadBits(rangeBits);
            lines.Add(new Line(prefix, range, current, Kind.Normal));
            current += 1L << range;
            if (lines.Count > 1 << 16) throw new Jbig2Exception("JBIG2: Huffman-Tabelle");
        }
        lines.Add(new Line((int)reader.ReadBits(prefixBits), 32, low - 1, Kind.Lower));
        lines.Add(new Line((int)reader.ReadBits(prefixBits), 32, high, Kind.Upper));
        if (hasOob) lines.Add(new Line((int)reader.ReadBits(prefixBits), 0, 0, Kind.Oob));
        return new Jbig2Huffman(lines);
    }

    /// <summary>Standard tables B.1–B.15.</summary>
    public static Jbig2Huffman Standard(int number) => StandardTables[number - 1].Value;

    private static readonly Lazy<Jbig2Huffman>[] StandardTables = Enumerable.Range(1, 15)
        .Select(n => new Lazy<Jbig2Huffman>(() => new Jbig2Huffman(StandardLines(n)))).ToArray();

    // Per line: range start, prefix length, range length; L = lower, U = upper range line (start and prefix length),
    // O = OOB (prefix length only). Order as in the standard, because the codes depend on it.
    private static List<Line> StandardLines(int number)
    {
        var spec = number switch
        {
            1 => "0 1 4|16 2 8|272 3 16|U65808 3",
            2 => "0 1 0|1 2 0|2 3 0|3 4 3|11 5 6|U75 6|O6",
            3 => "-256 8 8|0 1 0|1 2 0|2 3 0|3 4 3|11 5 6|L-257 8|U75 7|O6",
            4 => "1 1 0|2 2 0|3 3 0|4 4 3|12 5 6|U76 5",
            5 => "-255 7 8|1 1 0|2 2 0|3 3 0|4 4 3|12 5 6|L-256 7|U76 6",
            6 => "-2048 5 10|-1024 4 9|-512 4 8|-256 4 7|-128 5 6|-64 5 5|-32 4 5|0 2 7|128 3 7|256 3 8|512 4 9|1024 4 10|L-2049 6|U2048 6",
            7 => "-1024 4 9|-512 3 8|-256 4 7|-128 5 6|-64 5 5|-32 4 5|0 4 5|32 5 5|64 5 6|128 4 7|256 3 8|512 3 9|1024 3 10|L-1025 5|U2048 5",
            8 => "-15 8 3|-7 9 1|-5 8 1|-3 9 0|-2 7 0|-1 4 0|0 2 1|2 5 0|3 6 0|4 3 4|20 6 1|22 4 4|38 4 5|70 5 6|134 5 7|262 6 7|390 7 8|646 6 10|L-16 9|U1670 9|O2",
            9 => "-31 8 4|-15 9 2|-11 8 2|-7 9 1|-5 7 1|-3 4 1|-1 3 1|1 3 1|3 5 1|5 6 1|7 3 5|39 6 2|43 4 5|75 4 6|139 5 7|267 5 8|523 6 8|779 7 9|1291 6 11|L-32 9|U3339 9|O2",
            10 => "-21 7 4|-5 8 0|-4 7 0|-3 5 0|-2 2 2|2 5 0|3 6 0|4 7 0|5 8 0|6 2 6|70 5 5|102 6 5|134 6 6|198 6 7|326 6 8|582 6 9|1094 6 10|2118 7 11|L-22 8|U4166 8|O2",
            11 => "1 1 0|2 2 1|4 4 0|5 4 1|7 5 1|9 5 2|13 6 2|17 7 2|21 7 3|29 7 4|45 7 5|77 7 6|U141 7",
            12 => "1 1 0|2 2 0|3 3 1|5 5 0|6 5 1|8 6 1|10 7 0|11 7 1|13 7 2|17 7 3|25 7 4|41 8 5|U73 8",
            13 => "1 1 0|2 3 0|3 4 0|4 5 0|5 4 1|7 3 3|15 6 1|17 6 2|21 6 3|29 6 4|45 6 5|77 7 6|U141 7",
            14 => "-2 3 0|-1 3 0|0 1 0|1 3 0|2 3 0",
            _ => "-24 7 4|-8 6 2|-4 5 1|-2 4 0|-1 3 0|0 1 0|1 3 0|2 4 0|3 5 1|5 6 2|9 7 4|L-25 7|U25 7",
        };
        var lines = new List<Line>();
        foreach (var part in spec.Split('|'))
        {
            var kind = part[0] switch { 'L' => Kind.Lower, 'U' => Kind.Upper, 'O' => Kind.Oob, _ => Kind.Normal };
            var n = (kind == Kind.Normal ? part : part[1..]).Split(' ').Select(s => long.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            lines.Add(kind switch
            {
                Kind.Oob => new Line((int)n[0], 0, 0, kind),
                Kind.Normal => new Line((int)n[1], (int)n[2], n[0], kind),
                _ => new Line((int)n[1], 32, n[0], kind),
            });
        }
        return lines;
    }
}
