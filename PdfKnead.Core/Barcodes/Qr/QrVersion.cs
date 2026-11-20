namespace PdfKnead.Core.Barcodes.Qr;

/// <summary>Error correction level of a QR code (ISO/IEC 18004:2015, 5.3.8): recovers about 7, 15, 25 or 30 % of the codewords.</summary>
public enum QrErrorCorrection
{
    /// <summary>About 7 %.</summary>
    L,

    /// <summary>About 15 %.</summary>
    M,

    /// <summary>About 25 %.</summary>
    Q,

    /// <summary>About 30 %.</summary>
    H,
}

/// <summary>One group of error correction blocks of the same size.</summary>
/// <param name="Count">Number of blocks.</param>
/// <param name="DataCodewords">Data codewords per block.</param>
internal readonly record struct QrBlockGroup(int Count, int DataCodewords);

/// <summary>
/// The structure of a QR version 1 … 40 (ISO/IEC 18004:2015, tables 9 and E.1): size, error correction blocks per level and the
/// positions of the alignment patterns.
/// </summary>
internal sealed class QrVersion
{
    // per version: per level L, M, Q, H "check codewords per block, blocks, data codewords[, blocks, data codewords]" (table 9)
    private static readonly string[] Blocks =
    [
        "7,1,19|10,1,16|13,1,13|17,1,9",
        "10,1,34|16,1,28|22,1,22|28,1,16",
        "15,1,55|26,1,44|18,2,17|22,2,13",
        "20,1,80|18,2,32|26,2,24|16,4,9",
        "26,1,108|24,2,43|18,2,15,2,16|22,2,11,2,12",
        "18,2,68|16,4,27|24,4,19|28,4,15",
        "20,2,78|18,4,31|18,2,14,4,15|26,4,13,1,14",
        "24,2,97|22,2,38,2,39|22,4,18,2,19|26,4,14,2,15",
        "30,2,116|22,3,36,2,37|20,4,16,4,17|24,4,12,4,13",
        "18,2,68,2,69|26,4,43,1,44|24,6,19,2,20|28,6,15,2,16",
        "20,4,81|30,1,50,4,51|28,4,22,4,23|24,3,12,8,13",
        "24,2,92,2,93|22,6,36,2,37|26,4,20,6,21|28,7,14,4,15",
        "26,4,107|22,8,37,1,38|24,8,20,4,21|22,12,11,4,12",
        "30,3,115,1,116|24,4,40,5,41|20,11,16,5,17|24,11,12,5,13",
        "22,5,87,1,88|24,5,41,5,42|30,5,24,7,25|24,11,12,7,13",
        "24,5,98,1,99|28,7,45,3,46|24,15,19,2,20|30,3,15,13,16",
        "28,1,107,5,108|28,10,46,1,47|28,1,22,15,23|28,2,14,17,15",
        "30,5,120,1,121|26,9,43,4,44|28,17,22,1,23|28,2,14,19,15",
        "28,3,113,4,114|26,3,44,11,45|26,17,21,4,22|26,9,13,16,14",
        "28,3,107,5,108|26,3,41,13,42|30,15,24,5,25|28,15,15,10,16",
        "28,4,116,4,117|26,17,42|28,17,22,6,23|30,19,16,6,17",
        "28,2,111,7,112|28,17,46|30,7,24,16,25|24,34,13",
        "30,4,121,5,122|28,4,47,14,48|30,11,24,14,25|30,16,15,14,16",
        "30,6,117,4,118|28,6,45,14,46|30,11,24,16,25|30,30,16,2,17",
        "26,8,106,4,107|28,8,47,13,48|30,7,24,22,25|30,22,15,13,16",
        "28,10,114,2,115|28,19,46,4,47|28,28,22,6,23|30,33,16,4,17",
        "30,8,122,4,123|28,22,45,3,46|30,8,23,26,24|30,12,15,28,16",
        "30,3,117,10,118|28,3,45,23,46|30,4,24,31,25|30,11,15,31,16",
        "30,7,116,7,117|28,21,45,7,46|30,1,23,37,24|30,19,15,26,16",
        "30,5,115,10,116|28,19,47,10,48|30,15,24,25,25|30,23,15,25,16",
        "30,13,115,3,116|28,2,46,29,47|30,42,24,1,25|30,23,15,28,16",
        "30,17,115|28,10,46,23,47|30,10,24,35,25|30,19,15,35,16",
        "30,17,115,1,116|28,14,46,21,47|30,29,24,19,25|30,11,15,46,16",
        "30,13,115,6,116|28,14,46,23,47|30,44,24,7,25|30,59,16,1,17",
        "30,12,121,7,122|28,12,47,26,48|30,39,24,14,25|30,22,15,41,16",
        "30,6,121,14,122|28,6,47,34,48|30,46,24,10,25|30,2,15,64,16",
        "30,17,122,4,123|28,29,46,14,47|30,49,24,10,25|30,24,15,46,16",
        "30,4,122,18,123|28,13,46,32,47|30,48,24,14,25|30,42,15,32,16",
        "30,20,117,4,118|28,40,47,7,48|30,43,24,22,25|30,10,15,67,16",
        "30,19,118,6,119|28,18,47,31,48|30,34,24,34,25|30,20,15,61,16",
    ];

    // row/column coordinates of the alignment pattern centres for versions 2 … 40 (annex E, table E.1)
    private static readonly string[] Alignment =
    [
        "6,18", "6,22", "6,26", "6,30", "6,34", "6,22,38", "6,24,42", "6,26,46", "6,28,50", "6,30,54", "6,32,58", "6,34,62",
        "6,26,46,66", "6,26,48,70", "6,26,50,74", "6,30,54,78", "6,30,56,82", "6,30,58,86", "6,34,62,90", "6,28,50,72,94",
        "6,26,50,74,98", "6,30,54,78,102", "6,28,54,80,106", "6,32,58,84,110", "6,30,58,86,114", "6,34,62,90,118",
        "6,26,50,74,98,122", "6,30,54,78,102,126", "6,26,52,78,104,130", "6,30,56,82,108,134", "6,34,60,86,112,138",
        "6,30,58,86,114,142", "6,34,62,90,118,146", "6,30,54,78,102,126,150", "6,24,50,76,102,128,154", "6,28,54,80,106,132,158",
        "6,32,58,84,110,136,162", "6,26,54,82,110,138,166", "6,30,58,86,114,142,170",
    ];

    private static readonly QrVersion[] All = Enumerable.Range(1, 40).Select(v => new QrVersion(v)).ToArray();

    private readonly (int CheckPerBlock, QrBlockGroup[] Groups)[] _levels;

    private QrVersion(int number)
    {
        Number = number;
        AlignmentCenters = number == 1 ? [] : Alignment[number - 2].Split(',').Select(int.Parse).ToArray();
        _levels = Blocks[number - 1].Split('|').Select(level =>
        {
            var v = level.Split(',').Select(int.Parse).ToArray();
            var groups = new List<QrBlockGroup> { new(v[1], v[2]) };
            if (v.Length > 3) groups.Add(new QrBlockGroup(v[3], v[4]));
            return (v[0], groups.ToArray());
        }).ToArray();
    }

    /// <summary>Version 1 … 40.</summary>
    public static QrVersion Get(int number) => All[number - 1];

    /// <summary>The version whose size is <paramref name="dimension"/> modules; null if there is none.</summary>
    public static QrVersion? ForDimension(int dimension) =>
        dimension >= 21 && dimension <= 177 && (dimension - 17) % 4 == 0 ? All[(dimension - 17) / 4 - 1] : null;

    /// <summary>1 … 40.</summary>
    public int Number { get; }

    /// <summary>Modules per side: 17 + 4 × version.</summary>
    public int Dimension => 17 + 4 * Number;

    /// <summary>Row and column coordinates of the alignment pattern centres (each pair, except those on the finder patterns).</summary>
    public int[] AlignmentCenters { get; }

    /// <summary>Codewords of the symbol (data and check), from the modules left for them (ISO/IEC 18004:2015, table 1).</summary>
    public int TotalCodewords
    {
        get
        {
            var v = Number;
            var modules = (16 * v + 128) * v + 64;
            if (v >= 2)
            {
                var a = v / 7 + 2;
                modules -= (25 * a - 10) * a - 55;
                if (v >= 7) modules -= 36;
            }
            return modules / 8;
        }
    }

    /// <summary>Check codewords per block at a level.</summary>
    public int CheckPerBlock(QrErrorCorrection level) => _levels[(int)level].CheckPerBlock;

    /// <summary>The block groups at a level.</summary>
    public QrBlockGroup[] Groups(QrErrorCorrection level) => _levels[(int)level].Groups;

    /// <summary>Data codewords of the symbol at a level.</summary>
    public int DataCodewords(QrErrorCorrection level) => Groups(level).Sum(g => g.Count * g.DataCodewords);
}
