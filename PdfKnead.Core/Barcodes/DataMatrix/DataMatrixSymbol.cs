namespace PdfKnead.Core.Barcodes.DataMatrix;

/// <summary>
/// One ECC 200 symbol size (ISO/IEC 16022:2006, table 7): modules, data regions, data and check codewords, interleaved blocks.
/// </summary>
internal sealed record DataMatrixSymbol(int Rows, int Columns, int RegionRows, int RegionColumns, int DataCodewords, int CheckCodewords, int Blocks)
{
    // rows × columns: region rows × columns (modules of one data region), data codewords, check codewords, blocks
    public static readonly DataMatrixSymbol[] All =
    [
        new(10, 10, 8, 8, 3, 5, 1), new(12, 12, 10, 10, 5, 7, 1), new(14, 14, 12, 12, 8, 10, 1), new(16, 16, 14, 14, 12, 12, 1),
        new(18, 18, 16, 16, 18, 14, 1), new(20, 20, 18, 18, 22, 18, 1), new(22, 22, 20, 20, 30, 20, 1), new(24, 24, 22, 22, 36, 24, 1),
        new(26, 26, 24, 24, 44, 28, 1), new(32, 32, 14, 14, 62, 36, 1), new(36, 36, 16, 16, 86, 42, 1), new(40, 40, 18, 18, 114, 48, 1),
        new(44, 44, 20, 20, 144, 56, 1), new(48, 48, 22, 22, 174, 68, 1), new(52, 52, 24, 24, 204, 84, 2), new(64, 64, 14, 14, 280, 112, 2),
        new(72, 72, 16, 16, 368, 144, 4), new(80, 80, 18, 18, 456, 192, 4), new(88, 88, 20, 20, 576, 224, 4), new(96, 96, 22, 22, 696, 272, 4),
        new(104, 104, 24, 24, 816, 336, 6), new(120, 120, 18, 18, 1050, 408, 6), new(132, 132, 20, 20, 1304, 496, 8),
        new(144, 144, 22, 22, 1558, 620, 10),
        new(8, 18, 6, 16, 5, 7, 1), new(8, 32, 6, 14, 10, 11, 1), new(12, 26, 10, 24, 16, 14, 1), new(12, 36, 10, 16, 22, 18, 1),
        new(16, 36, 14, 16, 32, 24, 1), new(16, 48, 14, 22, 49, 28, 1),
    ];

    /// <summary>The size with these modules; null if there is none.</summary>
    public static DataMatrixSymbol? Of(int rows, int columns) => All.FirstOrDefault(s => s.Rows == rows && s.Columns == columns);

    /// <summary>Regions vertically.</summary>
    public int RegionsDown => Rows / (RegionRows + 2);

    /// <summary>Regions horizontally.</summary>
    public int RegionsAcross => Columns / (RegionColumns + 2);

    /// <summary>Rows of the mapping matrix (all data regions without their finder and timing patterns).</summary>
    public int MappingRows => RegionsDown * RegionRows;

    /// <summary>Columns of the mapping matrix.</summary>
    public int MappingColumns => RegionsAcross * RegionColumns;

    /// <summary>Data codewords of block <paramref name="block"/> (only the largest symbol has blocks of two sizes: 8 × 156, 2 × 155).</summary>
    public int DataInBlock(int block) => Rows == 144 ? (block < 8 ? 156 : 155) : DataCodewords / Blocks;

    /// <summary>Square symbol.</summary>
    public bool Square => Rows == Columns;
}

/// <summary>
/// Placement of the codewords in the mapping matrix (ISO/IEC 16022:2006, annex F): the "utah"-shaped 8-module pieces in diagonal
/// sweeps, the four corner cases, and the fixed pattern in the lower right corner when space is left.
/// </summary>
internal static class DataMatrixPlacement
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), (int Codeword, int Bit)[]> Maps = new();

    /// <summary><see cref="Map"/>, computed once per size (it is fixed by the size).</summary>
    public static (int Codeword, int Bit)[] Cached(int rows, int columns) => Maps.GetOrAdd((rows, columns), k => Map(k.Item1, k.Item2));

    /// <summary>For every module of the mapping matrix (row-major) the codeword (1-based) and bit (1 = most significant) it carries;
    /// codeword 0 marks the fixed corner pattern (bit 1 dark, bit 0 light).</summary>
    public static (int Codeword, int Bit)[] Map(int rows, int columns)
    {
        var map = new (int, int)[rows * columns];
        var set = new bool[rows * columns];
        void Module(int row, int col, int chr, int bit)
        {
            if (row < 0)
            {
                row += rows;
                col += 4 - (rows + 4) % 8;
            }
            if (col < 0)
            {
                col += columns;
                row += 4 - (columns + 4) % 8;
            }
            map[row * columns + col] = (chr, bit);
            set[row * columns + col] = true;
        }
        void Utah(int row, int col, int chr)
        {
            Module(row - 2, col - 2, chr, 1);
            Module(row - 2, col - 1, chr, 2);
            Module(row - 1, col - 2, chr, 3);
            Module(row - 1, col - 1, chr, 4);
            Module(row - 1, col, chr, 5);
            Module(row, col - 2, chr, 6);
            Module(row, col - 1, chr, 7);
            Module(row, col, chr, 8);
        }
        void Corner(int chr, (int R, int C)[] cells)
        {
            for (var i = 0; i < 8; i++) Module(cells[i].R, cells[i].C, chr, i + 1);
        }
        int n = rows, m = columns;
        var c = 1;
        int r = 4, k = 0;
        do
        {
            if (r == n && k == 0)
                Corner(c++, [(n - 1, 0), (n - 1, 1), (n - 1, 2), (0, m - 2), (0, m - 1), (1, m - 1), (2, m - 1), (3, m - 1)]);
            if (r == n - 2 && k == 0 && m % 4 != 0)
                Corner(c++, [(n - 3, 0), (n - 2, 0), (n - 1, 0), (0, m - 4), (0, m - 3), (0, m - 2), (0, m - 1), (1, m - 1)]);
            if (r == n - 2 && k == 0 && m % 8 == 4)
                Corner(c++, [(n - 3, 0), (n - 2, 0), (n - 1, 0), (0, m - 2), (0, m - 1), (1, m - 1), (2, m - 1), (3, m - 1)]);
            if (r == n + 4 && k == 2 && m % 8 == 0)
                Corner(c++, [(n - 1, 0), (n - 1, m - 1), (0, m - 3), (0, m - 2), (0, m - 1), (1, m - 3), (1, m - 2), (1, m - 1)]);
            // sweep up and to the right
            do
            {
                if (r < n && k >= 0 && !set[r * m + k]) Utah(r, k, c++);
                r -= 2;
                k += 2;
            } while (r >= 0 && k < m);
            r += 1;
            k += 3;
            // sweep down and to the left
            do
            {
                if (r >= 0 && k < m && !set[r * m + k]) Utah(r, k, c++);
                r += 2;
                k -= 2;
            } while (r < n && k >= 0);
            r += 3;
            k += 1;
        } while (r < n || k < m);
        // unused lower right corner: fixed pattern, dark on the diagonal
        if (!set[n * m - 1])
        {
            map[n * m - 1] = (0, 1);
            map[n * m - m - 2] = (0, 1);
            map[n * m - 2] = (0, 0);
            map[n * m - m - 1] = (0, 0);
        }
        return map;
    }
}
