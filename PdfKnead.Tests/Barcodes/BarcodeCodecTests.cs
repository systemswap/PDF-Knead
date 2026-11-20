using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Barcodes.DataMatrix;
using PdfKnead.Core.Barcodes.Linear;
using PdfKnead.Core.Barcodes.Qr;
using Xunit;

namespace PdfKnead.Tests.Barcodes;

/// <summary>
/// QR, Data Matrix and bar codes at the level of modules and widths: the examples of the norms, symbols from a reference generator
/// (fixed below, so that the reader is not only checked against the own writer), round trips through every size, and error correction.
/// </summary>
public class BarcodeCodecTests
{
    // ------------------------------------------------------------ Reed-Solomon

    [Fact]
    public void ReedSolomon_CheckCodewordsOfTheNormExample()
    {
        // ISO/IEC 18004:2015, annex I: "01234567" as version 1-M, data codewords and the ten check codewords given there
        byte[] data = [0x10, 0x20, 0x0C, 0x56, 0x61, 0x80, 0xEC, 0x11, 0xEC, 0x11, 0xEC, 0x11, 0xEC, 0x11, 0xEC, 0x11];
        Assert.Equal(new byte[] { 0xA5, 0x24, 0xD4, 0xC1, 0xED, 0x36, 0xC7, 0x87, 0x2C, 0x55 }, ReedSolomon.Qr.Encode(data, 10));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReedSolomon_CorrectsUpToHalfTheCheckCodewords_AndRefusesMore(bool qr)
    {
        var rs = qr ? ReedSolomon.Qr : ReedSolomon.DataMatrix;
        var random = new Random(11);
        for (var t = 0; t < 2000; t++)
        {
            int check = random.Next(2, 31), length = random.Next(1, 255 - check);
            var data = new byte[length];
            random.NextBytes(data);
            byte[] block = [.. data, .. rs.Encode(data, check)];
            var errors = random.Next(0, check / 2 + 1);
            var damaged = (byte[])block.Clone();
            foreach (var p in Enumerable.Range(0, block.Length).OrderBy(_ => random.Next()).Take(errors)) damaged[p] ^= (byte)random.Next(1, 256);
            Assert.Equal(errors, rs.Correct(damaged, check));
            Assert.Equal(block, damaged);
        }
        // far too many errors: never a wrong "correction" that claims success with the original
        var sample = new byte[40];
        random.NextBytes(sample);
        byte[] full = [.. sample, .. rs.Encode(sample, 10)];
        var ruined = (byte[])full.Clone();
        for (var i = 0; i < 20; i++) ruined[i] ^= 0x5A;
        Assert.True(rs.Correct(ruined, 10) < 0 || !ruined.SequenceEqual(full));
    }

    // ------------------------------------------------------------ QR

    // "PDF-Knead 123", version 2-M, from a reference generator
    private const string ReferenceQr = """
        #######.###.#.....#######
        #.....#..#.##...#.#.....#
        #.###.#...####.##.#.###.#
        #.###.#.#.##..###.#.###.#
        #.###.#.####.##.#.#.###.#
        #.....#.#.#.####..#.....#
        #######.#.#.#.#.#.#######
        ........##....#..........
        #...#.###.#.##########..#
        .......#....#......###..#
        .####.#.##..####.#...#...
        #.##.#...##.##.###.#.###.
        ##..####..####....##..###
        ##..##.##....##.##..###.#
        ..#####.###.....##.#..#..
        ..##...#..#.#.#.......##.
        ##.#####..##.########...#
        ........#...#...#...#.###
        #######.#########.#.#....
        #.....#..###.#.##...#.#..
        #.###.#.###.##..#####.##.
        #.###.#...#..####.##.##.#
        #.###.#..#.....##.##.#.#.
        #.....#..##.#.#..#.#..##.
        #######.#.##.##.....#.###
        """;

    [Fact]
    public void Qr_SymbolOfAReferenceGenerator()
    {
        var content = QrDecoder.Decode(BitMatrix.Parse(ReferenceQr))!;
        Assert.Equal("PDF-Knead 123", content.Text);
        Assert.Equal((2, QrErrorCorrection.M, 0), (content.Version, content.Level, content.Corrected));
        Assert.Equal("UTF-8", content.Charset);
    }

    [Fact]
    public void Qr_DamagedModules_Repaired_MirroredSymbol_Read()
    {
        var m = BitMatrix.Parse(ReferenceQr);
        // a 3 × 3 blot in the data area
        for (var y = 10; y < 13; y++)
            for (var x = 12; x < 15; x++) m[x, y] = !m[x, y];
        var repaired = QrDecoder.Decode(m)!;
        Assert.Equal("PDF-Knead 123", repaired.Text);
        Assert.True(repaired.Corrected > 0);
        var mirrored = QrDecoder.Decode(BitMatrix.Parse(ReferenceQr).Transposed())!;
        Assert.Equal("PDF-Knead 123", mirrored.Text);
        Assert.True(mirrored.Mirrored);
    }

    [Fact]
    public void Qr_RoundTrip_EveryVersionAndLevel()
    {
        var random = new Random(3);
        for (var v = 1; v <= 40; v++)
            foreach (var level in Enum.GetValues<QrErrorCorrection>())
            {
                var kind = (v + (int)level) % 3;
                var capacity = QrVersion.Get(v).DataCodewords(level);
                var text = kind switch
                {
                    0 => new string(Enumerable.Range(0, capacity).Select(_ => (char)('0' + random.Next(10))).ToArray()),
                    1 => new string(Enumerable.Range(0, capacity).Select(_ => QrEncoder.Alphanumeric[random.Next(45)]).ToArray()),
                    _ => new string(Enumerable.Range(0, capacity / 4).Select(_ => "aäöü€xyz 9"[random.Next(10)]).ToArray()),
                };
                var symbol = QrEncoder.Encode(text, level, v);
                Assert.Equal(17 + 4 * v, symbol.Width);
                var content = QrDecoder.Decode(symbol)!;
                Assert.Equal(text, content.Text);
                Assert.Equal((v, level), (content.Version, content.Level));
            }
    }

    [Fact]
    public void Qr_Gs1_GroupSeparatorKept()
    {
        var text = "0104012345123456" + "10ABC" + "\u001D" + "3103000250";
        var content = QrDecoder.Decode(QrEncoder.Encode(text, gs1: true))!;
        Assert.True(content.Gs1);
        Assert.Equal(text, content.Text);
    }

    // ------------------------------------------------------------ Data Matrix

    // from a reference generator, one per encodation scheme
    [Theory]
    [InlineData("ABC123DEF456", "C40", """
        #.#.#.#.#.#.#.#.
        #.#..##..###..##
        #.##.....#.####.
        #..#####..##..##
        #..#....#.#..##.
        #.#.##..##.#####
        #.#.#....#.#.#..
        #..##..##.#..###
        #..####...#.##..
        #.###.#...#.####
        ###..#.#.#...##.
        #.########.##..#
        #...##.##.#.##..
        #..######....#.#
        ##.#.#.#..#.#.#.
        ################
        """)]
    [InlineData("hallo welt 2026", "Text", """
        #.#.#.#.#.#.#.#.
        ##..#.#...#.####
        #...#....#.###..
        ##.##..##...##.#
        #####..###.#.#..
        ###..####.#.####
        ##.#####.....#..
        #.#...###.##.###
        ##..####..#####.
        ##...###.##..###
        ####..#.##.#.#..
        ####.###.#.###.#
        ####....#..#....
        ##.###.##..#.#.#
        #...#..##.###.#.
        ################
        """)]
    [InlineData("ABC*>123 XYZ", "X12", """
        #.#.#.#.#.#.#.#.
        #.#..#...##.##.#
        #.###.##..##.##.
        #..#.#.#...#.###
        ##..##..#..#....
        #.#..#..##..####
        #.##......#.##..
        #####..#.#.#####
        #..####..####.#.
        #.###.#.####...#
        #.##.##..######.
        #...#.##..######
        #######...##.##.
        ###.###.######.#
        ####.#.#..##..#.
        ################
        """)]
    [InlineData("ABC=123:XYZ", "EDIFACT", """
        #.#.#.#.#.#.#.#.
        #.....####.....#
        #...###.###...#.
        ##.....#..##..##
        #...###.#..#....
        #.#.##..###..###
        ###.....#....##.
        ####...#..##...#
        ##..####..##.##.
        ##.####.#.#.####
        #.#...###...#...
        ###.##.....#####
        #.#..#.#.#..###.
        #####..#..####.#
        ###.##..####..#.
        ################
        """)]
    [InlineData("Größe 42", "Base 256", """
        #.#.#.#.#.#.#.#.
        #.......#.#.##.#
        ###.##..##..#.#.
        ##..#..#.###...#
        #..#.#..#.#.....
        ###..#..####.###
        ##..#......#.#..
        ###....#.###.###
        #.#..#..######..
        #.#.#.#####...##
        ###....#..###.#.
        ####..##.#.#####
        ##.#...##....##.
        ###.#....#.#.#.#
        #.###..###.##.#.
        ################
        """)]
    public void DataMatrix_SymbolsOfAReferenceGenerator_EveryScheme(string text, string scheme, string modules)
    {
        var content = DataMatrixCodec.Decode(BitMatrix.Parse(modules))!;
        Assert.Equal(text, content.Text);
        Assert.Contains(scheme, content.Schemes);
        // turned symbols as well
        Assert.Equal(text, DataMatrixCodec.Decode(BitMatrix.Parse(modules).Rotated())!.Text);
    }

    [Fact]
    public void DataMatrix_RoundTrip_EverySize_WithDamage()
    {
        var random = new Random(5);
        foreach (var size in DataMatrixSymbol.All)
        {
            // fill the symbol exactly: letters take one codeword each
            var text = new string(Enumerable.Range(0, size.DataCodewords).Select(_ => (char)random.Next('A', 'z' + 1)).ToArray());
            var symbol = DataMatrixCodec.Encode(text, size.Square ? DataMatrixShape.Square : DataMatrixShape.Rectangular);
            Assert.Equal((size.Rows, size.Columns), (symbol.Height, symbol.Width));
            // flip a few data modules, at most as many as the error correction can surely repair (each flip hits one codeword)
            var flips = Math.Clamp(size.CheckCodewords / size.Blocks / 2 - 1, 1, 3);
            for (var k = 0; k < flips; k++)
            {
                int x = 1 + random.Next(symbol.Width - 2), y = 1 + random.Next(symbol.Height - 2);
                symbol[x, y] = !symbol[x, y];
            }
            var content = DataMatrixCodec.Decode(symbol)!;
            Assert.Equal(text, content.Text);
        }
    }

    [Fact]
    public void DataMatrix_Gs1_AndUnicode()
    {
        var gs1 = "0104012345123456" + "10ABC" + "\u001D" + "3103000250";
        var content = DataMatrixCodec.Decode(DataMatrixCodec.Encode(gs1, gs1: true))!;
        Assert.True(content.Gs1);
        Assert.Equal(gs1, content.Text);
        var unicode = DataMatrixCodec.Decode(DataMatrixCodec.Encode("Preis 5 € — ok"))!;
        Assert.Equal("Preis 5 € — ok", unicode.Text);
        Assert.Equal("UTF-8 (ECI)", unicode.Charset);
    }

    // ------------------------------------------------------------ bar codes

    [Theory]
    [InlineData("0123456", "01234565", "01234500006")]   // UPC-E, number system 0, check digit of the expanded number
    public void UpcE_ExpandsAsTheNormSays(string digits, string withCheck, string upcA)
    {
        Assert.Equal(upcA, LinearCodes.ExpandUpcE(digits));
        Assert.Equal(withCheck, digits + LinearCodes.Gs1CheckDigit(upcA));
    }

    [Theory]
    [InlineData(LinearSymbology.Ean13, "400638133393", "4006381333931")]
    [InlineData(LinearSymbology.Ean8, "9638507", "96385074")]
    [InlineData(LinearSymbology.UpcA, "03600029145", "036000291452")]
    [InlineData(LinearSymbology.UpcE, "0123456", "01234565")]
    [InlineData(LinearSymbology.Code128, "PDF-Knead 2026 #1", "PDF-Knead 2026 #1")]
    [InlineData(LinearSymbology.Code128, "123456789012", "123456789012")]
    [InlineData(LinearSymbology.Gs1_128, "0104012345123456\u001D3103000250", "0104012345123456\u001D3103000250")]
    [InlineData(LinearSymbology.Code39, "PDF KNEAD-42", "PDF KNEAD-42")]
    [InlineData(LinearSymbology.Itf, "12345678901231", "12345678901231")]
    public void BarCodes_RoundTrip_WithInkSpreadAndNoise(LinearSymbology kind, string input, string expected)
    {
        var modules = LinearCodes.Encode(kind, input);
        var random = new Random(9);
        for (var trial = 0; trial < 50; trial++)
        {
            // 3 px per module, ±6 % width noise, bars 0.4 px wider (ink spread), 10 modules of quiet zone
            var runs = new List<double> { 30 };
            for (var i = 0; i < modules.Length;)
            {
                var j = i;
                while (j < modules.Length && modules[j] == modules[i]) j++;
                runs.Add((j - i) * 3.0 * (1 + (random.NextDouble() - 0.5) * 0.12) + (modules[i] ? 0.4 : -0.4));
                i = j;
            }
            runs.Add(30);
            var content = LinearCodes.Decode([.. runs])!;
            Assert.Equal(kind, content.Symbology);
            Assert.Equal(expected, content.Text);
            // read from the other end as well
            runs.Reverse();
            Assert.Equal(expected, LinearCodes.Decode([.. runs])!.Text);
        }
    }

    [Fact]
    public void BarCodes_WrongCheckDigitRefused()
    {
        Assert.Throws<ArgumentException>(() => LinearCodes.Encode(LinearSymbology.Ean13, "4006381333932"));
        // a misprinted EAN digit: the check digit no longer fits, nothing is read
        var modules = LinearCodes.Encode(LinearSymbology.Ean13, "4006381333931");
        // the ninth digit (second of the right half, modules 57 … 63) printed as 4 instead of 3: R pattern of 4 is bar 1, space 1, bar 3, space 2
        bool[] four = [true, false, true, true, true, false, false];
        Assert.NotEqual(four, modules[57..64]);
        four.CopyTo(modules, 57);
        var runs = new List<double> { 30 };
        for (var i = 0; i < modules.Length;)
        {
            var j = i;
            while (j < modules.Length && modules[j] == modules[i]) j++;
            runs.Add((j - i) * 3.0);
            i = j;
        }
        runs.Add(30);
        Assert.Null(LinearCodes.Decode([.. runs]));
    }
}
