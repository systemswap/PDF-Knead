namespace PdfKnead.Core.Barcodes;

/// <summary>A grid of modules (true = dark), row by row from the top; x is the column, y the row.</summary>
public sealed class BitMatrix
{
    private readonly bool[] _bits;

    /// <summary>Creates an all-light matrix.</summary>
    public BitMatrix(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        _bits = new bool[checked(width * height)];
    }

    /// <summary>Columns.</summary>
    public int Width { get; }

    /// <summary>Rows.</summary>
    public int Height { get; }

    /// <summary>The module in column <paramref name="x"/>, row <paramref name="y"/>.</summary>
    public bool this[int x, int y]
    {
        get => _bits[y * Width + x];
        set => _bits[y * Width + x] = value;
    }

    /// <summary>The same matrix mirrored at its main diagonal (column ↔ row).</summary>
    public BitMatrix Transposed()
    {
        var t = new BitMatrix(Height, Width);
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                t[y, x] = this[x, y];
        return t;
    }

    /// <summary>Rotated by 90° clockwise.</summary>
    public BitMatrix Rotated()
    {
        var r = new BitMatrix(Height, Width);
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                r[Height - 1 - y, x] = this[x, y];
        return r;
    }

    /// <summary>Rows as text: '#' dark, '.' light (tests, diagnosis).</summary>
    public override string ToString()
    {
        var text = new System.Text.StringBuilder();
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++) text.Append(this[x, y] ? '#' : '.');
            text.Append('\n');
        }
        return text.ToString();
    }

    /// <summary>Reads the rows of <see cref="ToString"/> back ('#', 'X', '1' dark; anything else light).</summary>
    public static BitMatrix Parse(string text)
    {
        var rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var m = new BitMatrix(rows.Max(r => r.Length), rows.Length);
        for (var y = 0; y < rows.Length; y++)
            for (var x = 0; x < rows[y].Length; x++)
                m[x, y] = rows[y][x] is '#' or 'X' or '1';
        return m;
    }
}
