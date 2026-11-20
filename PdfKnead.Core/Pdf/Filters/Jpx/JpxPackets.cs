namespace PdfKnead.Core.Pdf.Filters.Jpx;

/// <summary>Bits of the packet headers (B.10.1): after 0xFF the next byte carries only 7 bits. Zeros beyond the end.</summary>
internal sealed class JpxBits(byte[] data, int position, int end)
{
    private int _byte;
    private int _count;

    public int Position { get; private set; } = position;

    /// <summary>Whether reading went beyond the end.</summary>
    public bool Ended { get; private set; }

    public void Reset(int position)
    {
        Position = position;
        _byte = 0;
        _count = 0;
    }

    public int Bit()
    {
        if (_count == 0)
        {
            var previous = _byte;
            if (Position < end)
            {
                _byte = data[Position];
            }
            else
            {
                _byte = 0;
                Ended = true;
            }
            Position++;
            _count = previous == 0xFF ? 7 : 8;
        }
        _count--;
        return _byte >> _count & 1;
    }

    public int Bits(int n)
    {
        var v = 0;
        for (var i = 0; i < n; i++) v = v << 1 | Bit();
        return v;
    }

    /// <summary>End of header: aligns to whole bytes; if 0xFF was the last byte read, the following stuffing byte is skipped.</summary>
    public void Align()
    {
        if (_byte == 0xFF) Position++;
        _byte = 0;
        _count = 0;
    }

    /// <summary>Skips the marker (e.g. EPH 0xFF92) if it follows.</summary>
    public void SkipMarker(int marker)
    {
        if (Position + 1 < end && data[Position] == 0xFF && data[Position + 1] == (marker & 0xFF)) Position += 2;
    }
}

/// <summary>
/// A tag tree (ITU-T T.800 | ISO/IEC 15444-1, B.10.2), written from the description of the standard: a grid of non-negative integers with
/// coarser levels above it, each node the minimum of the up to four below it. Coding is a series of questions from the top node down to the
/// leaf: every node has a current value, starting at zero — a 0 bit raises it by one, a 1 bit fixes it as the node's value; a node is asked
/// only once the one above it is fixed, and the asking stops as soon as the answer is known. What earlier questions fixed is not asked again
/// (the causality of B.10.2). No code of another decoder was used (the loop taken over before was replaced on 16.11.2026;
/// docs/LIZENZPRUEFUNG.md).
/// </summary>
internal sealed class JpxTagTree
{
    /// <summary>The nodes of all levels, the finest (the grid itself) first, each level row by row.</summary>
    private readonly int[] _current;
    private readonly bool[] _fixed;

    /// <summary>Where each level begins in the node arrays and how many nodes a row of it has; level 0 is the grid, the last the top node.</summary>
    private readonly int[] _start;
    private readonly int[] _rowLength;

    public JpxTagTree(int width, int height)
    {
        var starts = new List<int>();
        var rows = new List<int>();
        int columns = Math.Max(1, width), lines = Math.Max(1, height), nodes = 0;
        // each coarser level halves the grid, rounding up, until a single node is left
        while (true)
        {
            starts.Add(nodes);
            rows.Add(columns);
            nodes += columns * lines;
            if (columns == 1 && lines == 1) break;
            columns = (columns + 1) / 2;
            lines = (lines + 1) / 2;
        }
        _start = [.. starts];
        _rowLength = [.. rows];
        _current = new int[nodes];
        _fixed = new bool[nodes];
    }

    /// <summary>The node above the grid point (<paramref name="x"/>, <paramref name="y"/>) on <paramref name="level"/>.</summary>
    private int Node(int level, int x, int y) => _start[level] + (y >> level) * _rowLength[level] + (x >> level);

    /// <summary>
    /// Whether the value at grid point (<paramref name="x"/>, <paramref name="y"/>) is less than <paramref name="threshold"/>; reads only the
    /// bits this answer needs. From the top node down: a node starts no lower than the value of the node above it (that is the minimum of
    /// all below it); while it is not fixed and its current value is below the threshold, one bit is read. A node left unfixed at the
    /// threshold has a value of at least the threshold — and so has every node below it: the answer is no, nothing more is read.
    /// </summary>
    public bool Decode(JpxBits bits, int x, int y, int threshold)
    {
        var above = 0;
        for (var level = _start.Length - 1; level >= 0; level--)
        {
            var node = Node(level, x, y);
            if (_current[node] < above) _current[node] = above;
            while (!_fixed[node] && _current[node] < threshold)
            {
                if (bits.Bit() == 1) _fixed[node] = true;
                else _current[node]++;
            }
            if (!_fixed[node]) return false;
            above = _current[node];
        }
        return above < threshold;
    }

    /// <summary>Decodes the value at the leaf completely (zero bit planes); for damaged data, decoding fails once the value exceeds <paramref name="limit"/>.</summary>
    public int Value(JpxBits bits, int x, int y, int limit)
    {
        var threshold = 1;
        while (!Decode(bits, x, y, threshold))
        {
            if (++threshold > limit) throw new JpxException("JPX: Tag-Baum");
        }
        return threshold - 1;
    }
}
