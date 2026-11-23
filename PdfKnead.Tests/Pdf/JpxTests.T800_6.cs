using System.Globalization;
using PdfKnead.Core.Pdf.Filters.Jbig2;
using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// ITU-T T.800, Annex D (coefficient bit modelling) and Annex E (quantisation). Block tests: a block is coded with the coder written below from
/// the text of Annex D (not from the decoder), decoded with <see cref="JpxBlockDecoder"/> and compared coefficient by coefficient — if the
/// decoder picks another context, pass or order than the standard, coder and decoder drift apart and the values differ. Codestream tests:
/// one code-block in a small codestream written by hand, decoded with <see cref="JpxTile"/>.
/// </summary>
public sealed partial class JpxTests
{
    // ------------------------------------------------------------ T.800 D: bit planes and passes

    /// <summary>
    /// T.800 D (bit planes from the top down): a 1 × 1 block with the coefficient 8 and top plane 3 — after the first pass (cleanup of plane 3)
    /// the value is 3 × 2³ (magnitude doubled, with the half step), then one refinement bit 0 per plane down to plane 0 (17 = 2 · 8 + 1); passes
    /// announced beyond plane 0 are not run (the work count stops at 10).
    /// </summary>
    [Theory]
    [InlineData(1, 24, 1)]
    [InlineData(3, 20, 3)]
    [InlineData(4, 20, 4)]
    [InlineData(7, 18, 7)]
    [InlineData(10, 17, 10)]
    [InlineData(13, 17, 10)]
    public void BitPlanes_DecodedFromTheTopPlaneDownToZero_D(int passes, int at, int work)
    {
        var coder = new AnnexDE.Coder(1, 1, [8], top: 3).Encode();
        Assert.Equal("S0:1 G9:0 R14:0 R16:0 R16:0", coder.LogText);
        var decoder = new JpxBlockDecoder();
        Assert.Equal(work, decoder.Decode(1, 1, [(coder.Segments[0].Data, passes)], 0, 0, 3));
        Assert.Equal(at, decoder.At(0, 0));
    }

    /// <summary>
    /// T.800 D (three passes per plane, each bit in exactly one): random 4 × 4 blocks over two bit planes — the coder determines every bit of every
    /// coefficient in exactly one pass per plane, and the decoder, stopped after each pass, has exactly the values of the coder (the symbol sequence
    /// comes out even: a bit read twice or left out would shift all later symbols).
    /// </summary>
    [Fact]
    public void CodingPasses_EveryBitInExactlyOnePass_D()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var coder = new AnnexDE.Coder(4, 4, AnnexDE.RandomBlock(seed, 16, 1, 40), top: 1).Encode();
            for (var plane = 0; plane <= 1; plane++)
                for (var i = 0; i < 16; i++)
                    Assert.Equal(1, coder.Coverage.GetValueOrDefault((plane, i)));
            for (var passes = 1; passes <= 4; passes++)
                Assert.Equal(coder.After[passes - 1], coder.Decoded(segments: coder.Prefix(passes)));
        }
    }

    /// <summary>
    /// T.800 D.1 (scan pattern): a 3 × 6 block (height not divisible by 4), one cleanup pass — the first stripe column by column, in column 0 a run
    /// length 1 pointing at row 2 (UNIFORM 1, 0); the second stripe has two rows and is read without run length as (0, 4), (0, 5), (1, 4), (1, 5),
    /// (2, 4), (2, 5). The symbol sequence was worked out by hand from D.1 and D.3.
    /// </summary>
    [Fact]
    public void ScanPattern_StripesOfFourRowsColumnByColumn_D1()
    {
        int[] values =
        [
            0, 0, 0,
            0, 0, 0,
            1, 0, 0,
            0, 0, 0,
            1, 0, -1,
            0, -1, 1,
        ];
        var coder = new AnnexDE.Coder(3, 6, values, top: 0).Encode();
        Assert.Equal("L17:1 U18:1 U18:0 G9:0 S3:0 S0:0 S1:0 S5:0 S1:0 L17:0 S0:1 G9:0 S3:0 S5:0 S1:1 G9:1 S1:1 G9:1 S7:1 G13:1", coder.LogText);
        Assert.Equal("(0,4) (0,5) (1,4) (1,5) (2,4) (2,5)",
            string.Join(' ', coder.Log.Where(s => s.Y >= 4 && s.Kind == 'S').Select(s => $"({s.X},{s.Y})")));
        Assert.Equal([0, 0, 0, 0, 0, 0, 3, 0, 0, 0, 0, 0, 3, 0, -3, 0, -3, 3], coder.Decoded());
    }

    // ------------------------------------------------------------ T.800 D.2

    /// <summary>
    /// T.800 D.2.1 (sign bit and magnitude bits): a 1 × 1 block, top plane 2, magnitude bits 1, 0, 1 — with sign bit 1 the coefficient is −11
    /// (magnitude 5 doubled with the half step), with sign bit 0 it is 11.
    /// </summary>
    [Theory]
    [InlineData(-5, "S0:1 G9:1 R14:0 R16:1", -11)]
    [InlineData(5, "S0:1 G9:0 R14:0 R16:1", 11)]
    public void SignAndMagnitudeBits_GiveTheSignedCoefficient_D21(int value, string log, int at)
    {
        var coder = new AnnexDE.Coder(1, 1, [value], top: 2).Encode();
        Assert.Equal(log, coder.LogText);
        Assert.Equal([at], coder.Decoded());
    }

    /// <summary>
    /// T.800 D.2.1 (Nb counts the zero bit planes of the packet header, B.10.5): the same code-block data with 0 and with 2 zero bit planes in the
    /// packet header — the reconstructed values differ by the factor 4 (Mb 5, Δb 8, the block coded from plane 2).
    /// </summary>
    [Fact]
    public void ZeroBitPlanesOfThePacketHeader_CountInNb_D21()
    {
        var coder = new AnnexDE.Coder(4, 1, [5, -3, 0, 6], top: 2).Encode();
        float[] Decoded(int zeroPlanes) => AnnexDE.Tile(AnnexDE.Codestream(4, 1, 8, 0, false, 0, 1, 2, [(5, 0)],
            AnnexDE.Body(coder.Segments, zeroPlanes, 7))).Component.Data;
        Assert.Equal([44f, -28f, 0f, 52f], Decoded(2));
        Assert.Equal([176f, -112f, 0f, 208f], Decoded(0));
    }

    /// <summary>
    /// T.800 D.2.2 (ROI only with RGN, H.1): the same code-block (coefficients 7, 8 and 48, coded from plane 5, one zero bit plane) in a codestream
    /// without RGN (Mb 7) keeps its values; with RGN (Maxshift 3, Mb 4, so the same top plane) the coefficient below 2³ stays, those from 2³ on are
    /// moved down by 3 planes.
    /// </summary>
    [Fact]
    public void RoiShift_OnlyWithRgnAndOnlyForRegionCoefficients_D22()
    {
        var coder = new AnnexDE.Coder(3, 1, [7, 8, 48], top: 5).Encode();
        var body = AnnexDE.Body(coder.Segments, 1, 16);
        var without = AnnexDE.Tile(AnnexDE.Codestream(3, 1, 8, 0, true, 0, 1, 0, [(7, 0)], body)).Component.Data;
        var with = AnnexDE.Tile(AnnexDE.Codestream(3, 1, 8, 0, true, 0, 1, 0, [(4, 0)], body, roi: 3)).Component.Data;
        Assert.Equal([7f, 8f, 48f], without);
        Assert.Equal([7f, 1f, 6f], with);
    }

    // ------------------------------------------------------------ T.800 D.3

    /// <summary>
    /// T.800 D.3 (significance state): a 1 × 1 block, coefficient 5, top plane 2 — significant after the cleanup pass of plane 2 (12), after that
    /// only refinement bits 0 and 1 (10, then 11) and no further significance symbol.
    /// </summary>
    [Theory]
    [InlineData(1, 12)]
    [InlineData(3, 10)]
    [InlineData(4, 10)]
    [InlineData(6, 11)]
    [InlineData(7, 11)]
    public void SignificanceState_SetOnTheFirstOneBit_D3(int passes, int at)
    {
        var coder = new AnnexDE.Coder(1, 1, [5], top: 2).Encode();
        Assert.Equal("S0:1 G9:0 R14:0 R16:1", coder.LogText);
        Assert.Single(coder.Log, s => s.Kind == 'S');
        Assert.Equal([at], coder.Decoded(segments: coder.Prefix(passes)));
    }

    /// <summary>
    /// T.800 D.3 (neighbours outside the block are insignificant): one decoder for two blocks — after a 4 × 4 block whose coefficients are all
    /// significant, a 3 × 4 block with one coefficient at the right edge: its first significance pass reads exactly the five neighbours inside the
    /// block and nothing left over from the first block.
    /// </summary>
    [Fact]
    public void NeighboursOutsideTheBlock_AreInsignificant_D3()
    {
        var decoder = new JpxBlockDecoder();
        var first = new AnnexDE.Coder(4, 4, [3, -2, 2, 3, -3, 2, 3, -2, 2, 3, -2, 3, 3, -3, 2, 2], top: 1).Encode();
        Assert.Equal(first.After[^1], first.Decoded(decoder));

        var second = new AnnexDE.Coder(3, 4, [0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0], top: 1).Encode();
        Assert.Equal("(1,0) (1,1) (1,2) (2,0) (2,2)", string.Join(' ', second.Log.Where(s => s.Pass == 1).Select(s => $"({s.X},{s.Y})")));
        Assert.Equal(second.After[^1], second.Decoded(decoder));
    }

    /// <summary>
    /// T.800 D.3 (four context rules in three passes): random 16 × 16 blocks over five planes in all four kinds of sub-band — the contexts are
    /// trained differently by the symbols before, each rule (significance, sign, refinement, cleanup with run length) codes in its own contexts;
    /// the decoder reproduces every coefficient only if it picks the same contexts.
    /// </summary>
    [Fact]
    public void ContextRules_EachCodingOperationItsOwnContexts_D3()
    {
        for (var orientation = 0; orientation < 4; orientation++)
        for (var seed = 0; seed < 3; seed++)
        {
            var coder = new AnnexDE.Coder(16, 16, AnnexDE.RandomBlock(100 + seed, 256, 4, 50), orientation, top: 4).Encode();
            foreach (var kind in "SGRLU") Assert.Contains(coder.Log, s => s.Kind == kind);
            Assert.Equal(coder.After[^1], coder.Decoded());
        }
    }

    /// <summary>
    /// T.800 D.3 (the first plane has only the cleanup pass, the others three): random 4 × 4 blocks, four passes in one segment — pass 1 is the
    /// cleanup of the top plane, passes 2 to 4 significance, refinement and cleanup of the next plane; after each pass the decoder has the
    /// values of the coder.
    /// </summary>
    [Fact]
    public void FirstPlaneCleanupOnly_ThenThreePassesPerPlane_D3()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var coder = new AnnexDE.Coder(4, 4, AnnexDE.RandomBlock(200 + seed, 16, 1, 30), top: 1).Encode();
            Assert.All(coder.Log, s => Assert.Contains(s.Kind, s.Pass switch { 0 or 3 => "SGLU", 1 => "SG", _ => "R" }));
            Assert.All(coder.Log, s => Assert.Equal(s.Pass == 0 ? 1 : 0, s.Plane));
            for (var passes = 1; passes <= 4; passes++)
                Assert.Equal(coder.After[passes - 1], coder.Decoded(segments: coder.Prefix(passes)));
        }
    }

    // ------------------------------------------------------------ T.800 D.3.1

    /// <summary>
    /// T.800 D.3.1 (the contexts depend on the sub-band): the same neighbour pattern — only the horizontal neighbour to the left significant —
    /// gives context 5 in LL and LH, 3 in HL and 1 in HH; in front of it a random area trains the contexts differently, so the block comes out
    /// right only with the table of its own sub-band.
    /// </summary>
    [Theory]
    [InlineData(0, 5)]
    [InlineData(2, 5)]
    [InlineData(1, 3)]
    [InlineData(3, 1)]
    public void ZeroCodingContext_DependsOnTheSubBand_D31(int orientation, int context)
    {
        for (var seed = 0; seed < 5; seed++)
        {
            var random = AnnexDE.RandomBlock(300 + seed, 12 * 8, 3, 20);
            var values = new int[16 * 8];
            for (var i = 0; i < random.Length; i++) values[i / 12 * 16 + i % 12] = random[i];
            values[1 * 16 + 13] = 8;   // (13, 1) significant on the top plane, columns 12, 14 and 15 otherwise empty
            var coder = new AnnexDE.Coder(16, 8, values, orientation, top: 3).Encode();
            Assert.Equal(context, coder.Log.First(s => s is { Kind: 'S', X: 14, Y: 1 }).Context);
            Assert.Equal(coder.After[^1], coder.Decoded());
        }
    }

    /// <summary>
    /// T.800 D.3.1 (significance pass): in a 4 × 4 block (0, 0) becomes significant in the first cleanup pass; the significance pass of the next plane
    /// reads only insignificant coefficients with a context other than 0 — (0, 1) with bit 1 and its sign, then (0, 2), which became a candidate
    /// only through (0, 1) in the same pass (bit 1 and sign), then (0, 3) and column 1 with the newest states; columns 2 and 3 have context 0 and are
    /// skipped, (3, 3) becomes significant only in the cleanup pass (run length to row 3). Symbol sequence worked out by hand.
    /// </summary>
    [Fact]
    public void SignificancePass_OnlyInsignificantWithNonZeroContext_D31()
    {
        var values = new int[16];
        values[0] = 2;
        values[4] = 1;
        values[8] = 1;
        values[15] = 1;
        var coder = new AnnexDE.Coder(4, 4, values, top: 1).Encode();
        Assert.Equal("L17:1 U18:0 U18:0 G9:0 S3:0 S0:0 S0:0 S5:0 S1:0 S0:0 S0:0 L17:0 L17:0 "
            + "S3:1 G10:0 S3:1 G10:0 S3:0 S6:0 S6:0 S6:0 S1:0 R15:0 L17:0 L17:1 U18:1 U18:1 G9:0", coder.LogText);
        Assert.Equal("(0,1) (0,2) (0,3) (1,0) (1,1) (1,2) (1,3)",
            string.Join(' ', coder.Log.Where(s => s is { Pass: 1, Kind: 'S' }).Select(s => $"({s.X},{s.Y})")));
        Assert.Equal([5, 0, 0, 0, 3, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 3], coder.Decoded());
    }

    // ------------------------------------------------------------ T.800 D.3.2

    /// <summary>
    /// T.800 D.3.2 (neighbours outside the block insignificant for the sign too): a 2 × 3 block with a coefficient at the left edge without
    /// significant neighbours in the block — the sign in context 9 with XOR bit 0 (symbol sequence worked out by hand); then, in the same decoder
    /// after a 5 × 3 block with negative coefficients, a 2 × 6 block with three such left-edge coefficients (negative, positive, negative): each sign
    /// in context 9 with XOR bit 0, nothing of the block before counts as a neighbour.
    /// </summary>
    [Fact]
    public void SignContext_NeighboursOutsideTheBlockInsignificant_D32()
    {
        var single = new AnnexDE.Coder(2, 3, [0, 0, -1, 0, 0, 0], top: 0).Encode();
        Assert.Equal("S0:0 S0:1 G9:1 S3:0 S1:0 S5:0 S1:0", single.LogText);
        Assert.Equal([0, 0, -3, 0, 0, 0], single.Decoded());

        var decoder = new JpxBlockDecoder();
        var first = new AnnexDE.Coder(5, 3, [-1, 0, 0, 0, -1, 0, -1, 0, 0, 0, 0, 0, -1, 0, 0], top: 0).Encode();
        Assert.Equal(first.After[^1], first.Decoded(decoder));
        int[] values = [0, 0, -1, 0, 0, 0, 1, 0, 0, 0, -1, 0];
        var second = new AnnexDE.Coder(2, 6, values, top: 0).Encode();
        var signs = second.Log.Where(s => s.Kind == 'G').ToList();
        Assert.Equal("(0,1) (0,3) (0,5)", string.Join(' ', signs.Select(s => $"({s.X},{s.Y})")));
        Assert.All(signs, s => Assert.Equal((9, values[s.Y * 2] < 0 ? 1 : 0), (s.Context, s.Bit)));
        Assert.Equal([0, 0, -3, 0, 0, 0, 3, 0, 0, 0, -3, 0], second.Decoded(decoder));
    }

    /// <summary>
    /// T.800 D.3.2 (Equation D-1): with the left neighbour significant (H ±1, V 0: context 12) the decoded bit D gives the sign only together with
    /// the XOR bit of Table D.3 — negative neighbour (XOR bit 1): D 0 negative, D 1 positive; positive neighbour (XOR bit 0) the other way round.
    /// </summary>
    [Theory]
    [InlineData(-1, -1, 0)]
    [InlineData(-1, 1, 1)]
    [InlineData(1, -1, 1)]
    [InlineData(1, 1, 0)]
    public void SignBit_IsTheDecodedBitXorTheBitOfTableD3_D32(int left, int current, int d)
    {
        var coder = new AnnexDE.Coder(2, 1, [left, current], top: 0).Encode();
        Assert.Equal($"S0:1 G9:{(left < 0 ? 1 : 0)} S5:1 G12:{d}", coder.LogText);
        Assert.Equal([3 * left, 3 * current], coder.Decoded());
    }

    // ------------------------------------------------------------ T.800 D.3.3

    /// <summary>
    /// T.800 D.3.3 (refinement pass): in a 2 × 1 block A becomes significant in the cleanup pass of plane 2, B in the significance pass of plane 1 —
    /// the refinement pass of plane 1 reads only A (B has just become significant); the bit returned is A's bit in plane 1: 1 gives 14, 0 gives 10.
    /// </summary>
    [Theory]
    [InlineData(6, 1, 14)]
    [InlineData(4, 0, 10)]
    public void RefinementPass_OnlyCoefficientsSignificantBefore_D33(int a, int bit, int at)
    {
        var coder = new AnnexDE.Coder(2, 1, [a, 2], top: 2).Encode(4);
        Assert.Equal($"S0:1 G9:0 S5:0 S5:1 G12:0 R15:{bit}", coder.LogText);
        Assert.Equal("(0,0)", string.Join(' ', coder.Log.Where(s => s.Pass == 2).Select(s => $"({s.X},{s.Y})")));
        Assert.Equal([at, 6], coder.Decoded());
    }

    /// <summary>
    /// T.800 D.3.3, Table D.4 (rows as data): the first refinement of a coefficient without significant neighbours in context 14, with at least one in
    /// context 15 — also when the neighbour became significant in the significance pass just before (A at (7, 3), B at (7, 2)) — and every later
    /// refinement in context 16. The 8 × 4 block mixes all three cases with different bits (isolated: 1, with neighbours: 0), so a context of the
    /// wrong row makes the decoder drift.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 2, 14)]
    [InlineData(2, 2, 2, 14)]
    [InlineData(4, 0, 2, 15)]
    [InlineData(5, 1, 2, 15)]
    [InlineData(7, 3, 2, 15)]
    [InlineData(7, 2, 1, 15)]
    [InlineData(0, 0, 1, 16)]
    [InlineData(4, 1, 0, 16)]
    [InlineData(7, 3, 1, 16)]
    public void RefinementContexts_AsTableD4(int x, int y, int plane, int context)
    {
        int[] values =
        [
            13, 0, -14, 0, 9, 10, 0, 0,
            0, 0, 0, 0, 8, -11, 0, 0,
            12, 0, 15, 0, 0, 0, 0, -5,
            0, 0, 0, 0, 0, 0, 0, 9,
        ];
        var coder = new AnnexDE.Coder(8, 4, values, top: 3).Encode();
        Assert.Equal(context, coder.Log.Single(s => s.Kind == 'R' && s.X == x && s.Y == y && s.Plane == plane).Context);
        Assert.Equal(coder.After[^1], coder.Decoded());
    }

    // ------------------------------------------------------------ T.800 D.3.4

    /// <summary>
    /// T.800 D.3.4 (cleanup pass with the contexts of Table D.1 and their state): random 8 × 8 blocks — the contexts 1 to 8 trained in a significance
    /// pass are used again with their state in the cleanup pass of the same plane, a coefficient with context 0 is read only in the cleanup pass.
    /// </summary>
    [Fact]
    public void CleanupPass_SharesTheSignificanceContextsAndTheirStates_D34()
    {
        var shared = 0;
        for (var seed = 0; seed < 10; seed++)
        {
            var coder = new AnnexDE.Coder(8, 8, AnnexDE.RandomBlock(400 + seed, 64, 3, 70), top: 3).Encode();
            // context 0 never in a significance pass
            Assert.DoesNotContain(coder.Log, s => s is { Kind: 'S', Context: 0 } && s.Pass % 3 == 1);
            // a context of the significance pass used again in the cleanup pass of the same plane
            shared += coder.Log.Count(s => s.Kind == 'S' && s.Pass % 3 == 1
                && coder.Log.Any(t => t.Kind == 'S' && t.Pass == s.Pass + 2 && t.Context == s.Context));
            Assert.Equal(coder.After[^1], coder.Decoded());
        }
        Assert.True(shared > 0);
    }

    /// <summary>
    /// T.800 D.3.4 (run length): run-length symbol 0 leaves the column at 0 and no further symbol is read for it (the next column follows at
    /// once); symbol 1 with UNIFORM 1, 0 makes row 2 significant, then come its sign and only row 3 with a context of Table D.1.
    /// </summary>
    [Fact]
    public void CleanupRunLength_ZeroOrUniformPosition_D34()
    {
        var zero = new AnnexDE.Coder(2, 4, [0, 1, 0, 0, 0, 0, 0, 0], top: 0).Encode();
        Assert.Equal("L17:0 L17:1 U18:0 U18:0 G9:0 S3:0 S0:0 S0:0", zero.LogText);
        Assert.Equal([0, 3, 0, 0, 0, 0, 0, 0], zero.Decoded());

        var two = new AnnexDE.Coder(1, 4, [0, 0, 1, 0], top: 0).Encode();
        Assert.Equal("L17:1 U18:1 U18:0 G9:0 S3:0", two.LogText);
        Assert.Equal([0, 0, 3, 0], two.Decoded());
    }

    /// <summary>
    /// T.800 D.3.4, Table D.5 (columns as data): run length 0 (none to decode); run length 1 with UNIFORM 00, 01, 10, 11 — the first significant
    /// coefficient in row 0 to 3, then 3, 2, 1, 0 coefficients of the column decoded; a column with a context other than 0 without a run-length
    /// symbol (the whole column).
    /// </summary>
    [Theory]
    [InlineData(1, "0 0 0 0", "L17:0", "0 0 0 0", 0, 0)]
    [InlineData(1, "1 0 0 0", "L17:1 U18:0 U18:0 G9:0 S3:0 S0:0 S0:0", "3 0 0 0", 0, 3)]
    [InlineData(1, "0 -1 0 1", "L17:1 U18:0 U18:1 G9:1 S3:0 S0:1 G9:0", "0 -3 0 3", 0, 2)]
    [InlineData(1, "0 0 1 1", "L17:1 U18:1 U18:0 G9:0 S3:1 G10:0", "0 0 3 3", 0, 1)]
    [InlineData(1, "0 0 0 -1", "L17:1 U18:1 U18:1 G9:1", "0 0 0 -3", 0, 0)]
    [InlineData(2, "0 0 1 0 0 1 0 0", "L17:1 U18:0 U18:1 G9:0 S3:0 S0:0 S1:0 S5:0 S1:1 G9:0 S3:0", "0 0 3 0 0 3 0 0", 1, 4)]
    public void CleanupRunLength_AsTableD5(int width, string values, string log, string at, int column, int decoded)
    {
        var coder = new AnnexDE.Coder(width, 4, AnnexDE.Ints(values), top: 0).Encode();
        Assert.Equal(log, coder.LogText);
        Assert.Equal(decoded, coder.Log.Count(s => s.Kind == 'S' && s.X == column));
        Assert.Equal(AnnexDE.Ints(at), coder.Decoded());
    }

    /// <summary>
    /// T.800 D.3.4 (fewer than four rows: no run length; significance changes at once): a 1 × 3 block — three symbols with contexts of Table D.1 and
    /// no run-length symbol; the coefficient that became significant gives its lower neighbour context 3 in the same pass.
    /// </summary>
    [Fact]
    public void CleanupPass_NoRunLengthBelowFourRows_D34()
    {
        var coder = new AnnexDE.Coder(1, 3, [1, 1, 0], top: 0).Encode();
        Assert.Equal("S0:1 G9:0 S3:1 G10:0 S3:0", coder.LogText);
        Assert.Equal([3, 3, 0], coder.Decoded());
    }

    // ------------------------------------------------------------ T.800 D.4

    /// <summary>
    /// T.800 D.4, Table D.8 (both patterns as data): without termination on each pass all passes of the block in one segment, with it (style bit 4)
    /// each pass a segment with its own length in the packet header; the tile decodes the coefficients either way.
    /// </summary>
    [Theory]
    [InlineData(0, "7")]
    [InlineData(4, "1,1,1,1,1,1,1")]
    public void Termination_AsTableD8(int style, string layout)
    {
        var coder = new AnnexDE.Coder(4, 4, AnnexDE.RandomBlock(500, 16, 2, 30), style: style, top: 2).Encode();
        Assert.Equal(layout, coder.Layout);
        var data = AnnexDE.Tile(AnnexDE.Codestream(4, 4, 8, 0, true, style, 1, 0, [(3, 0)], AnnexDE.Body(coder.Segments, 0, 7))).Component.Data;
        Assert.Equal(AnnexDE.Reversible(coder.After[^1]), data);
    }

    /// <summary>
    /// T.800 D.4, Table D.8 (block test): seven passes, each coded and terminated on its own and decoded as seven segments, give the same
    /// coefficients as the passes coded in one segment.
    /// </summary>
    [Fact]
    public void TerminationOnEveryPass_SameCoefficientsAsOneSegment_D4()
    {
        var values = AnnexDE.RandomBlock(501, 16, 2, 30);
        var each = new AnnexDE.Coder(4, 4, values, style: 4, top: 2).Encode();
        var once = new AnnexDE.Coder(4, 4, values, style: 0, top: 2).Encode();
        Assert.Equal(7, each.Segments.Count);
        Assert.Single(once.Segments);
        Assert.Equal(once.After[^1], each.Decoded());
        Assert.Equal(once.After[^1], once.Decoded());
    }

    /// <summary>
    /// T.800 D.4 (B.10.7: the length of each terminated segment in the packet header): with style bit 4 and two passes the packet header carries two
    /// lengths and the data is split between the two segments; read as one length (without style bit 4) the same packet does not give the block.
    /// </summary>
    [Fact]
    public void TerminatedSegments_EachLengthInThePacketHeader_D4()
    {
        var coder = new AnnexDE.Coder(4, 4, AnnexDE.RandomBlock(502, 16, 1, 30), style: 4, top: 1).Encode(2);
        Assert.Equal("1,1", coder.Layout);
        var body = AnnexDE.Body(coder.Segments, 0, 2);
        var data = AnnexDE.Tile(AnnexDE.Codestream(4, 4, 8, 0, true, 4, 1, 0, [(2, 0)], body)).Component.Data;
        Assert.Equal(AnnexDE.Reversible(coder.After[1]), data);
        var (other, truncated) = AnnexDE.Tile(AnnexDE.Codestream(4, 4, 8, 0, true, 0, 1, 0, [(2, 0)], body), check: false);
        Assert.True(truncated || !AnnexDE.Reversible(coder.After[1]).SequenceEqual(other.Data));
    }

    /// <summary>
    /// T.800 D.4.1 (the decoder extends the segment with 0xFF): symbols coded with the MQ coder, the closing marker cut off — decoded over exactly
    /// this length they come out as with two 0xFF bytes appended; the bytes of a second segment behind it are not read (read along, they would
    /// change the symbols).
    /// </summary>
    [Fact]
    public void MqDecoder_ExtendsTheSegmentWithFF_D41()
    {
        var random = new Random(41);
        var bits = Enumerable.Range(0, 400).Select(k => random.Next(10) < (k % 3 == 0 ? 8 : 3) ? 1 : 0).ToArray();
        var encoder = new MqEncoder();
        var cx = new byte[3];
        for (var k = 0; k < bits.Length; k++) encoder.Encode(cx, k % 3, bits[k]);
        var full = encoder.Finish();
        Assert.Equal(0xAC, full[^1]);
        var cut = AnnexDE.WithoutMarker(full);

        int[] Decode(byte[] data, int end)
        {
            var mq = new MqDecoder(data, 0, end);
            var contexts = new byte[3];
            return [.. Enumerable.Range(0, bits.Length).Select(k => mq.Decode(contexts, k % 3))];
        }

        Assert.Equal(bits, Decode(cut, cut.Length));
        byte[] padded = [.. cut, 0xFF, 0xFF];
        Assert.Equal(bits, Decode(padded, padded.Length));
        byte[] joined = [.. cut, 0x00, 0x00, 0x00, 0x00];
        Assert.Equal(bits, Decode(joined, cut.Length));

        // A segment shorter than its symbols need: the decoder reads 0xFF behind its end, not the bytes of the next segment (which would
        // change the symbols)
        var shorter = cut.Length / 2;
        byte[] next = [.. cut[..shorter], 0x00, 0x00, 0x00, 0x00];
        byte[] extended = [.. cut[..shorter], 0xFF, 0xFF];
        Assert.Equal(Decode(extended, extended.Length), Decode(next, shorter));
        Assert.NotEqual(Decode(next, next.Length), Decode(next, shorter));
    }

    // ------------------------------------------------------------ T.800 D.5

    /// <summary>
    /// T.800 D.5 (segmentation symbol, style bit 32): after each cleanup pass the coder writes 1, 0, 1, 0 in the UNIFORM context, then the next
    /// plane follows — decoded with style bit 32 the block comes out right; the same data without style bit 32 decodes differently.
    /// </summary>
    [Fact]
    public void SegmentationSymbol_ReadAfterEachCleanupPass_D5()
    {
        var coder = new AnnexDE.Coder(4, 4, AnnexDE.RandomBlock(600, 16, 2, 30), style: 32, top: 2).Encode();
        Assert.Equal(3, coder.Log.Count(s => s.Kind == 'M') / 4);
        Assert.All(coder.Log.Where(s => s.Kind == 'M').Chunk(4), m => Assert.Equal("M18:1 M18:0 M18:1 M18:0", string.Join(' ', m)));
        Assert.Equal(coder.After[^1], coder.Decoded());
        Assert.NotEqual(coder.After[^1], coder.Decoded(style: 0));
    }

    // ------------------------------------------------------------ T.800 D.6

    /// <summary>
    /// T.800 D.6 (bypass, style bit 1): 13 passes — passes 1 to 10 arithmetic in one segment, 11 and 12 (significance and refinement of the fifth
    /// plane) raw in one segment, 13 (cleanup) arithmetic: the decoder reproduces the block; reading passes 11 and 12 arithmetically gives
    /// something else.
    /// </summary>
    [Fact]
    public void Bypass_RawFromTheEleventhPass_D6()
    {
        var coder = new AnnexDE.Coder(4, 4, AnnexDE.RandomBlock(700, 16, 4, 20), style: 1, top: 4).Encode();
        Assert.Equal("10,2,1", coder.Layout);
        Assert.All(coder.Log, s => Assert.Equal(s.Pass is 10 or 11, s.Kind is 'r' or 'g' or 'f'));
        Assert.Contains(coder.Log, s => s.Pass == 11);
        Assert.Equal(coder.After[^1], coder.Decoded());
        Assert.NotEqual(coder.After[^1], coder.Decoded(style: 0));
    }

    /// <summary>
    /// T.800 D.6, Table D.9 (as data): with bypass the first segment holds the passes up to the fourth cleanup pass (10), then alternately two raw
    /// passes (terminated after the refinement pass) and one arithmetic cleanup pass, each with its length in the packet header.
    /// </summary>
    [Theory]
    [InlineData(10, "10")]
    [InlineData(11, "10,1")]
    [InlineData(12, "10,2")]
    [InlineData(13, "10,2,1")]
    [InlineData(14, "10,2,1,1")]
    [InlineData(16, "10,2,1,2,1")]
    public void Bypass_SegmentsAsTableD9(int passes, string layout)
    {
        var coder = new AnnexDE.Coder(4, 4, AnnexDE.RandomBlock(701, 16, 5, 20), style: 1, top: 5).Encode(passes);
        Assert.Equal(layout, coder.Layout);
        var data = AnnexDE.Tile(AnnexDE.Codestream(4, 4, 8, 0, true, 1, 1, 0, [(6, 0)], AnnexDE.Body(coder.Segments, 0, passes))).Component.Data;
        Assert.Equal(AnnexDE.Reversible(coder.After[^1]), data);
    }

    /// <summary>
    /// T.800 D.6 (raw bits): an 8 × 4 block whose 32 coefficients are significant from the top plane; the raw refinement pass of plane 0 reads one
    /// bit per coefficient from the bytes 0xFF, 0x7F, 0x80 — highest bit first, eight ones, after the 0xFF only seven (the stuffed bit is thrown
    /// out), then 1 and seven zeros, beyond the end ones (D.4.1). The coder packs the same bits as 0xFF 0x7F 0x80 0xFF 0x40 (a 0 stuffed after
    /// each 0xFF).
    /// </summary>
    [Fact]
    public void Bypass_RawBitsUnstuffedAndExtendedWithOnes_D6()
    {
        var random = new Random(702);
        var values = new int[32];
        for (var k = 0; k < 32; k++)
        {
            var bit = k < 16 || k >= 23 ? 1 : 0;   // bit of plane 0 in scan order
            var magnitude = 16 | random.Next(8) << 1 | bit;
            values[k % 4 * 8 + k / 4] = k % 3 == 0 ? -magnitude : magnitude;
        }
        var coder = new AnnexDE.Coder(8, 4, values, style: 1, top: 4).Encode(12);
        Assert.Equal("10,2", coder.Layout);
        Assert.Equal([0xFF, 0x7F, 0x80, 0xFF, 0x40], coder.Segments[1].Data);
        Assert.Equal(coder.After[^1], coder.Decoded(segments: [coder.Segments[0], ([0xFF, 0x7F, 0x80], 2)]));
    }

    /// <summary>
    /// T.800 D.6 (bypass with termination on each pass): style bits 1 and 4 — every segment one pass, also both raw passes, each read from its own
    /// first byte; the cleanup passes stay arithmetic.
    /// </summary>
    [Fact]
    public void BypassWithTerminationOnEachPass_EverySegmentOnePass_D6()
    {
        var coder = new AnnexDE.Coder(4, 4, AnnexDE.RandomBlock(703, 16, 4, 20), style: 5, top: 4).Encode();
        Assert.Equal(string.Join(',', Enumerable.Repeat(1, 13)), coder.Layout);
        Assert.All(coder.Log.Where(s => s.Pass == 12), s => Assert.Contains(s.Kind, "SGLU"));
        Assert.Equal(coder.After[^1], coder.Decoded());
        var data = AnnexDE.Tile(AnnexDE.Codestream(4, 4, 8, 0, true, 5, 1, 0, [(5, 0)], AnnexDE.Body(coder.Segments, 0, 13))).Component.Data;
        Assert.Equal(AnnexDE.Reversible(coder.After[^1]), data);
    }

    /// <summary>
    /// T.800 D.6 (Equation D-2): in the raw significance pass the sign is the raw bit — bits 1, 1 make the coefficient significant and negative,
    /// bits 1, 0 significant and positive.
    /// </summary>
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    public void Bypass_RawSignBit_D6(int value, int sign)
    {
        var coder = new AnnexDE.Coder(2, 1, [21, value], style: 1, top: 4).Encode(12);
        Assert.Equal($"r:1 g:{sign} f:1", string.Join(' ', coder.Log.Where(s => s.Pass >= 10)));
        Assert.Equal([43, 3 * value], coder.Decoded());
    }

    /// <summary>
    /// T.800 D.6 (B.10.7: lengths of the terminated segments and of the unterminated rest): bypass, 12 passes in two layers — the first packet carries
    /// two lengths (the 10 arithmetic passes and the first raw pass), the second the rest of the raw segment, which the tile continues.
    /// </summary>
    [Fact]
    public void Bypass_SegmentLengthsAndContinuationInTheNextLayer_D6()
    {
        var coder = new AnnexDE.Coder(4, 4, AnnexDE.RandomBlock(704, 16, 4, 20), style: 1, top: 4).Encode(12);
        Assert.Equal("10,2", coder.Layout);
        Assert.True(coder.Segments[1].Data.Length >= 2);
        var data = AnnexDE.Tile(AnnexDE.Codestream(4, 4, 8, 0, true, 1, 1, 0, [(5, 0)], AnnexDE.Body(coder.Segments, 0, 11, 1), layers: 2)).Component.Data;
        Assert.Equal(AnnexDE.Reversible(coder.After[^1]), data);
    }

    // ------------------------------------------------------------ T.800 D.7

    /// <summary>
    /// T.800 D.7 (vertically causal, style bit 8): a 2 × 8 block (two stripes), (0, 4) significant from the top plane, (0, 3) one plane later,
    /// two coefficients lower in column 1.
    /// Without style bit 8 (0, 3) is read in the significance pass, its sign in context 10 and its refinement in context 15; with it the row below the
    /// stripe counts as insignificant — (0, 3) waits for the cleanup pass (run length to row 3), sign in context 9, refinement in context 14.
    /// </summary>
    [Theory]
    [InlineData(false, 10, 15)]
    [InlineData(true, 9, 14)]
    public void VerticallyCausal_IgnoresTheNextStripe_D7(bool causal, int signContext, int refinementContext)
    {
        var values = new int[16];
        values[3 * 2] = -3;
        values[4 * 2] = 7;   // its refinement in plane 1 (bit 1) trains context 14 before (0, 3) is refined in plane 0
        values[6 * 2 + 1] = -2;
        values[7 * 2 + 1] = 3;
        var coder = new AnnexDE.Coder(2, 8, values, style: causal ? 8 : 0, top: 2).Encode();
        Assert.Equal(!causal, coder.Log.Any(s => s is { Pass: 1, Kind: 'S', X: 0, Y: 3 }));
        Assert.Equal(signContext, coder.Log.Single(s => s is { Kind: 'G', X: 0, Y: 3 }).Context);
        Assert.Equal(refinementContext, coder.Log.Single(s => s is { Kind: 'R', X: 0, Y: 3 }).Context);
        if (causal) Assert.Contains("L17:1 U18:1 U18:1 G9:1", string.Join(' ', coder.Log.Where(s => s.Pass == 3)));
        Assert.Equal(coder.After[^1], coder.Decoded());
        Assert.NotEqual(coder.After[^1], coder.Decoded(style: causal ? 0 : 8));
    }

    // ------------------------------------------------------------ T.800 D.8

    /// <summary>
    /// T.800 D.8, Table D.10 (one row per decision): for each decision D0 to D12 a small block in which both answers occur in the coder (written
    /// along the flow chart); the decoder reproduces the block.
    /// </summary>
    [Theory]
    [InlineData("D0", 1, 1, 1, "3")]
    [InlineData("D1", 3, 1, 1, "2 0 0")]
    [InlineData("D2", 3, 1, 1, "2 0 0")]
    [InlineData("D3", 3, 1, 1, "2 1 0")]
    [InlineData("D4", 3, 1, 1, "2 1 0")]
    [InlineData("D5", 3, 1, 1, "2 1 0")]
    [InlineData("D6", 3, 1, 1, "2 1 0")]
    [InlineData("D7", 3, 1, 1, "2 1 0")]
    [InlineData("D8", 2, 4, 0, "0 0 0 0 1 0 0 1")]
    [InlineData("D9", 3, 1, 1, "2 0 0")]
    [InlineData("D10", 3, 4, 0, "0 0 0 1 0 0 0 0 0 0 0 1")]
    [InlineData("D11", 2, 4, 0, "0 1 0 0 0 0 0 0")]
    [InlineData("D12", 2, 4, 0, "0 1 0 0 0 0 0 0")]
    public void FlowDecisions_AsTableD10(string decision, int width, int height, int top, string values)
    {
        var coder = new AnnexDE.Coder(width, height, AnnexDE.Ints(values), top: top).Encode();
        Assert.Contains((decision, true), coder.Trace);
        Assert.Contains((decision, false), coder.Trace);
        Assert.Equal(coder.After[^1], coder.Decoded());
    }

    /// <summary>
    /// T.800 D.8, Table D.11 (one row per code and symbol value): C0 no symbol (on to the next coefficient), C1 significance bit in a context of
    /// Table D.1, C2 sign bit in a context of Table D.3, C3 refinement bit in a context of Table D.4, C4 run length (0 four zeros, 1 not), C5 UNIFORM
    /// 00 to 11 (first, second, third, fourth coefficient) — symbol sequence worked out by hand, effect on the decoded coefficients.
    /// </summary>
    [Theory]
    [InlineData("C0", 3, 1, 1, "2 0 0", "S0:1 G9:0 S5:0 S0:0 S5:0 R14:0 S0:0", "5 0 0")]
    [InlineData("C1", 1, 1, 0, "1", "S0:1 G9:0", "3")]
    [InlineData("C1", 2, 1, 0, "1 0", "S0:1 G9:0 S5:0", "3 0")]
    [InlineData("C2", 1, 1, 0, "-1", "S0:1 G9:1", "-3")]
    [InlineData("C2", 2, 1, 0, "-1 -1", "S0:1 G9:1 S5:1 G12:0", "-3 -3")]
    [InlineData("C3", 1, 1, 1, "3", "S0:1 G9:0 R14:1", "7")]
    [InlineData("C3", 1, 1, 1, "2", "S0:1 G9:0 R14:0", "5")]
    [InlineData("C4", 1, 4, 0, "0 0 0 0", "L17:0", "0 0 0 0")]
    [InlineData("C4", 1, 4, 0, "0 0 0 1", "L17:1 U18:1 U18:1 G9:0", "0 0 0 3")]
    [InlineData("C5", 1, 4, 0, "1 0 0 0", "L17:1 U18:0 U18:0 G9:0 S3:0 S0:0 S0:0", "3 0 0 0")]
    [InlineData("C5", 1, 4, 0, "0 1 0 0", "L17:1 U18:0 U18:1 G9:0 S3:0 S0:0", "0 3 0 0")]
    [InlineData("C5", 1, 4, 0, "0 0 -1 1", "L17:1 U18:1 U18:0 G9:1 S3:1 G10:1", "0 0 -3 3")]
    [InlineData("C5", 1, 4, 0, "0 0 0 -1", "L17:1 U18:1 U18:1 G9:1", "0 0 0 -3")]
    public void CodingOperations_AsTableD11(string code, int width, int height, int top, string values, string log, string at)
    {
        Assert.StartsWith("C", code);
        var coder = new AnnexDE.Coder(width, height, AnnexDE.Ints(values), top: top).Encode();
        Assert.Equal(log, coder.LogText);
        Assert.Equal(AnnexDE.Ints(at), coder.Decoded());
    }

    // ------------------------------------------------------------ T.800 E.1

    /// <summary>
    /// T.800 E.1 (Equation E-1): reversible, Mb 3, all bits decoded — the coefficients come into the buffer of the tile with their sign and the
    /// magnitude of their bits (−5 from the bits 1, 0, 1 and sign bit 1).
    /// </summary>
    [Fact]
    public void DecodedCoefficient_AsEquationE1()
    {
        int[] values = [-5, 5, 3, 0, 7, -1];
        var coder = new AnnexDE.Coder(6, 1, values, top: 2).Encode();
        var data = AnnexDE.Tile(AnnexDE.Codestream(6, 1, 8, 0, true, 0, 1, 0, [(3, 0)], AnnexDE.Body(coder.Segments, 0, 7))).Component.Data;
        Assert.Equal(values.Select(v => (float)v), data);
    }

    /// <summary>
    /// T.800 E.1 (Equation E-2): Mb = G + εb − 1 with G and εb from QCD — the same block (coded from plane 2, six zero bit planes) under G 1, 2 and 3:
    /// the top plane of the block moves with Mb, every guard bit doubles the values (irreversible, Δb 0.5).
    /// </summary>
    [Theory]
    [InlineData(1, 9, 2.75f)]
    [InlineData(2, 10, 5.5f)]
    [InlineData(3, 11, 11f)]
    public void MagnitudeBitPlanes_AsEquationE2(int guard, int magnitudeBits, float value)
    {
        var coder = new AnnexDE.Coder(1, 1, [5], top: 2).Encode();
        var tc = AnnexDE.Tile(AnnexDE.Codestream(1, 1, 8, 0, false, 0, guard, 2, [(9, 0)], AnnexDE.Body(coder.Segments, 6, 7))).Component;
        Assert.Equal(magnitudeBits, tc.Resolutions[0].Bands[0].MagnitudeBits);
        Assert.Equal([value], tc.Data);
    }

    /// <summary>
    /// T.800 E.1.1.1 (Equation E-3): Δb = 2^(Rb − εb) · (1 + μb / 2¹¹) with RI from SIZ — LL with RI 8, εb 8, μb 0 gives 1; εb 10, μb 1024 gives 0.375;
    /// RI 12 gives 16; μb 2047 almost doubles the step.
    /// </summary>
    [Theory]
    [InlineData(8, 8, 0, 1.0)]
    [InlineData(8, 10, 1024, 0.375)]
    [InlineData(12, 8, 0, 16.0)]
    [InlineData(8, 9, 2047, 0.999755859375)]
    public void StepSize_AsEquationE3(int precision, int exponent, int mantissa, double step)
    {
        var tc = AnnexDE.Tile(AnnexDE.Codestream(4, 4, precision, 0, false, 0, 1, 2, [(exponent, mantissa)], [0x00])).Component;
        Assert.Equal(step, tc.Resolutions[0].Bands[0].Step, 6);
    }

    /// <summary>
    /// T.800 E.1.1.1, Table E.1 (columns as data): the gain of the sub-band — log2 0 for LL, 1 for HL and LH, 2 for HH — enters Rb = RI + log2(gainb)
    /// (Equation E-4): with the same εb, μb and RI the steps stand 1 : 2 : 2 : 4.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    public void SubBandGain_AsTableE1(int orientation, int log2Gain)
    {
        var tc = AnnexDE.Tile(AnnexDE.Codestream(16, 16, 8, 1, false, 0, 1, 2, [(10, 512), (10, 512), (10, 512), (10, 512)], [0x00, 0x00])).Component;
        var band = orientation == 0 ? tc.Resolutions[0].Bands[0] : tc.Resolutions[1].Bands[orientation - 1];
        Assert.Equal(orientation, band.Orientation);
        Assert.Equal(Math.Pow(2, 8 + log2Gain - 10) * 1.25, band.Step, 6);
    }

    /// <summary>
    /// T.800 E.1.1.1 (Equation E-5): derived quantisation from (εo, μo) = (10, 5) with NL 3 — εb = εo − NL + nb, μb = μo: nb 3 → (10, 5), nb 2 →
    /// (9, 5), nb 1 → (8, 5); expounded quantisation: each sub-band its own pair.
    /// </summary>
    [Theory]
    [InlineData(1, 0, 3, 10, 5)]
    [InlineData(1, 1, 3, 10, 5)]
    [InlineData(1, 4, 2, 9, 5)]
    [InlineData(1, 7, 1, 8, 5)]
    [InlineData(2, 0, 3, 11, 1)]
    [InlineData(2, 2, 3, 10, 3)]
    [InlineData(2, 9, 1, 7, 10)]
    public void QuantisationPairs_DerivedOrExpounded_E5(int style, int band, int decompositionLevel, int exponent, int mantissa)
    {
        var quantisation = style == 1
            ? new JpxQuantization { Style = 1, Exponents = [10], Mantissas = [5] }
            : new JpxQuantization { Style = 2, Exponents = [11, 10, 10, 9, 9, 9, 9, 8, 8, 7], Mantissas = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10] };
        Assert.Equal((exponent, mantissa), quantisation.Band(band, 3, decompositionLevel));
    }

    /// <summary>
    /// T.800 E.1.1.1 (Equation E-5 in the tile): derived quantisation (G 1, εo 10, μo 5) with three levels — nb counts the levels from the
    /// tile-component to the sub-band: LL and resolution 1 nb 3, resolution 2 nb 2, resolution 3 nb 1; Mb and Δb follow from the derived εb.
    /// </summary>
    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 10)]
    [InlineData(2, 9)]
    [InlineData(3, 8)]
    public void DerivedQuantisation_PerResolutionInTheTile_E5(int resolution, int exponent)
    {
        var tc = AnnexDE.Tile(AnnexDE.Codestream(16, 16, 8, 3, false, 0, 1, 1, [(10, 5)], [0x00, 0x00, 0x00, 0x00])).Component;
        foreach (var band in tc.Resolutions[resolution].Bands)
        {
            var log2Gain = band.Orientation == 0 ? 0 : band.Orientation == 3 ? 2 : 1;
            Assert.Equal(1 + exponent - 1, band.MagnitudeBits);
            Assert.Equal(Math.Pow(2, 8 + log2Gain - exponent) * (1 + 5 / 2048.0), band.Step, 6);
        }
    }

    /// <summary>
    /// T.800 E.1.1.2 (Equation E-6, r = 1/2): irreversible, Δb 0.5, Mb 9 with five zero bit planes (the block from plane 3) — with the last plane not
    /// decoded (Nb 8) 11 and 10 both give qb 10 and (10 + 1) · 0.5 = 5.5, negative −5.5, 1 gives qb 0 and 0; all planes decoded 11.5 · 0.5 = 5.75,
    /// 10.5 · 0.5 = 5.25 and 1.5 · 0.5 = 0.75.
    /// </summary>
    [Theory]
    [InlineData(7, "5.5 -5.5 0 5.5 0")]
    [InlineData(10, "5.75 -5.75 0 5.25 0.75")]
    public void IrreversibleReconstruction_AsEquationE6(int passes, string expected)
    {
        var coder = new AnnexDE.Coder(5, 1, [11, -11, 0, 10, 1], top: 3).Encode(passes);
        var data = AnnexDE.Tile(AnnexDE.Codestream(5, 1, 8, 0, false, 0, 1, 2, [(9, 0)], AnnexDE.Body(coder.Segments, 5, passes))).Component.Data;
        Assert.Equal(expected.Split(' ').Select(s => float.Parse(s, CultureInfo.InvariantCulture)), data);
    }

    /// <summary>
    /// T.800 E.1.2.1 (reversible: step 1): with the 5-3 filter every sub-band has Δb = 1, whatever exponents QCD gives; the same QCD with the 9-7
    /// filter gives the steps of Equation E-3.
    /// </summary>
    [Fact]
    public void ReversibleStepSize_IsOne_E121()
    {
        (int, int)[] bands = [(9, 0), (10, 0), (10, 0), (11, 0)];
        var reversible = AnnexDE.Tile(AnnexDE.Codestream(16, 16, 8, 1, true, 0, 1, 0, bands, [0x00, 0x00])).Component;
        var irreversible = AnnexDE.Tile(AnnexDE.Codestream(16, 16, 8, 1, false, 0, 1, 0, bands, [0x00, 0x00])).Component;
        var all = reversible.Resolutions.SelectMany(r => r.Bands).ToList();
        Assert.Equal(4, all.Count);
        Assert.All(all, b => Assert.Equal(1f, b.Step));
        Assert.All(irreversible.Resolutions.SelectMany(r => r.Bands), b => Assert.Equal(0.5f, b.Step));
    }

    /// <summary>
    /// T.800 E.1.2.2 (Equations E-7 and E-8, r = 1/2): reversible, Mb 4 — all planes decoded: qb itself (11, −11, 0, 1, 10); Nb 3: qb + 1 (11, −11, 0,
    /// 11; 1 has qb 0 and stays 0); Nb 2: qb + 2 (10, −10, 0, 0, 10).
    /// </summary>
    [Theory]
    [InlineData(10, "11 -11 0 1 10")]
    [InlineData(7, "11 -11 0 0 11")]
    [InlineData(4, "10 -10 0 0 10")]
    public void ReversibleReconstruction_AsEquationsE7AndE8(int passes, string expected)
    {
        var coder = new AnnexDE.Coder(5, 1, [11, -11, 0, 1, 10], top: 3).Encode(passes);
        var data = AnnexDE.Tile(AnnexDE.Codestream(5, 1, 8, 0, true, 0, 1, 0, [(4, 0)], AnnexDE.Body(coder.Segments, 0, passes))).Component.Data;
        Assert.Equal(AnnexDE.Ints(expected).Select(v => (float)v), data);
    }

    // ------------------------------------------------------------ helpers

    /// <summary>Tools of the tests of T.800 Annexes D and E: block coder, codestream and packet writer.</summary>
    private static class AnnexDE
    {
        // Code-block style (Table A.19)
        private const int Bypass = 1, Reset = 2, TerminateAll = 4, Causal = 8, Segmentation = 32;

        // Table D.1, rows as data: (H, V, D, context) for LL and LH, -1 = any, 10 + n = at least n (HL: H and V swapped); HH: (H + V, D, context)
        private static readonly int[][] LowRows =
            [[2, -1, -1, 8], [1, 11, -1, 7], [1, 0, 11, 6], [1, 0, 0, 5], [0, 2, -1, 4], [0, 1, -1, 3], [0, 0, 12, 2], [0, 0, 1, 1], [0, 0, 0, 0]];
        private static readonly int[][] DiagonalRows =
            [[-1, 13, 8], [11, 2, 7], [0, 2, 6], [12, 1, 5], [1, 1, 4], [0, 1, 3], [12, 0, 2], [1, 0, 1], [0, 0, 0]];

        private static bool Fits(int want, int value) => want == -1 || (want >= 10 ? value >= want - 10 : value == want);

        /// <summary>Table D.1 for the sub-band (0 LL, 1 HL, 2 LH, 3 HH).</summary>
        public static int ZeroContext(int orientation, int h, int v, int d) => orientation switch
        {
            1 => LowRows.First(r => Fits(r[0], v) && Fits(r[1], h) && Fits(r[2], d))[3],
            3 => DiagonalRows.First(r => Fits(r[0], h + v) && Fits(r[1], d))[2],
            _ => LowRows.First(r => Fits(r[0], h) && Fits(r[1], v) && Fits(r[2], d))[3],
        };

        /// <summary>Table D.2: the contribution of two neighbours (1 positive, −1 negative, 0 insignificant).</summary>
        private static int Contribution(int a, int b) => (a, b) switch
        {
            (1, 1) or (1, 0) or (0, 1) => 1,
            (-1, -1) or (-1, 0) or (0, -1) => -1,
            _ => 0,
        };

        /// <summary>Table D.3: context and XOR bit from the horizontal and vertical contribution.</summary>
        private static (int Context, int Xor) SignContext(int h, int v) => (h, v) switch
        {
            (1, 1) => (13, 0), (1, 0) => (12, 0), (1, -1) => (11, 0), (0, 1) => (10, 0), (0, 0) => (9, 0),
            (0, -1) => (10, 1), (-1, 1) => (11, 1), (-1, 0) => (12, 1), _ => (13, 1),
        };

        public static int[] Ints(string s) => [.. s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)];

        /// <summary>The value a reversible tile holds for the doubled magnitude: E-7 and E-8 with r = 1/2.</summary>
        public static float[] Reversible(int[] at) => [.. at.Select(v => (float)(v < 0 ? -(-v >> 1) : v >> 1))];

        /// <summary>Random coefficients below 2^(top + 1), some of them 0, at least one with a one bit in the top plane.</summary>
        public static int[] RandomBlock(int seed, int count, int top, int zeroPercent)
        {
            var random = new Random(seed);
            var values = new int[count];
            for (var i = 0; i < count; i++)
                values[i] = random.Next(100) < zeroPercent ? 0 : random.Next(1, 1 << (top + 1)) * (random.Next(2) == 0 ? 1 : -1);
            values[random.Next(count)] = 1 << top | random.Next(1 << top);
            return values;
        }

        /// <summary>The MQ data without the marker 0xFF 0xAC the encoder closes with (the decoder extends the segment with 0xFF, D.4.1).</summary>
        public static byte[] WithoutMarker(byte[] data)
        {
            var n = data.Length - 1;
            if (n > 0 && data[n - 1] == 0xFF) n--;
            return data[..n];
        }

        /// <summary>
        /// Coder of one code-block after Annex D: the passes of D.3 in the scan order of D.1, contexts of Tables D.1, D.3 and D.4, run length and
        /// UNIFORM of D.3.4, segmentation symbol (D.5), raw bits (D.6), vertically causal contexts (D.7), the segment boundaries of Tables D.8 and
        /// D.9. It records every symbol, every decision of Table D.10, which bits each pass determines, and after each pass the value a decoder must
        /// have (Equation E-6 with r = 1/2, doubled: magnitude × 2 with half a step of the last plane coded for the coefficient).
        /// </summary>
        public sealed class Coder
        {
            public readonly record struct Symbol(int Pass, int Plane, char Kind, int Context, int Bit, int X, int Y)
            {
                public override string ToString() => Context < 0 ? $"{Kind}:{Bit}" : $"{Kind}{Context}:{Bit}";
            }

            private readonly int _w, _h, _orientation, _style, _top;
            private readonly int[] _v;
            private readonly bool[] _sig, _neg, _refined, _inSig;
            private readonly int[] _last;
            private byte[] _cx = JpxBlockDecoder.InitialContexts();
            private int _pass, _plane;

            /// <summary>Symbols: S significance, G sign, R refinement, L run length, U UNIFORM, M segmentation; raw r, g, f.</summary>
            public readonly List<Symbol> Log = [];

            public readonly List<(string Decision, bool Outcome)> Trace = [];
            public readonly List<int[]> After = [];
            public readonly List<(byte[] Data, int Passes)> Segments = [];

            /// <summary>How often the bit of a coefficient in a plane was determined.</summary>
            public readonly Dictionary<(int Plane, int Index), int> Coverage = [];

            public Coder(int width, int height, int[] values, int orientation = 0, int style = 0, int top = 0)
            {
                (_w, _h, _v, _orientation, _style, _top) = (width, height, values, orientation, style, top);
                Assert.Equal(width * height, values.Length);
                _sig = new bool[values.Length];
                _neg = new bool[values.Length];
                _refined = new bool[values.Length];
                _inSig = new bool[values.Length];
                _last = new int[values.Length];
            }

            public string LogText => string.Join(' ', Log);

            public string Layout => string.Join(',', Segments.Select(s => s.Passes));

            /// <summary>The first segment with fewer passes announced (one segment only).</summary>
            public IReadOnlyList<(byte[] Data, int Passes)> Prefix(int passes) => [(Assert.Single(Segments).Data, passes)];

            public int[] Decoded(JpxBlockDecoder? decoder = null, IReadOnlyList<(byte[] Data, int Passes)>? segments = null, int? style = null)
            {
                decoder ??= new JpxBlockDecoder();
                decoder.Decode(_w, _h, segments ?? Segments, _orientation, style ?? _style, _top);
                var values = new int[_w * _h];
                for (var y = 0; y < _h; y++)
                    for (var x = 0; x < _w; x++) values[y * _w + x] = decoder.At(x, y);
                return values;
            }

            public Coder Encode(int passes = -1)
            {
                if (passes < 0) passes = 1 + 3 * _top;
                MqEncoder? mq = null;
                RawWriter? raw = null;
                var count = 0;
                var type = 2;
                _plane = _top;
                for (_pass = 0; _pass < passes; _pass++)
                {
                    if (type == 2 && _pass == 0) Trace.Add(("D0", true));
                    if (type == 0) Trace.Add(("D0", false));
                    var isRaw = (_style & Bypass) != 0 && _pass >= 10 && type != 2;
                    Assert.True(isRaw ? mq is null : raw is null, "a segment is either arithmetic or raw");
                    if (isRaw) raw ??= new RawWriter();
                    else mq ??= new MqEncoder();
                    switch (type)
                    {
                        case 0:
                            Significance(mq, raw);
                            break;
                        case 1:
                            Refinement(mq, raw);
                            break;
                        default:
                            Cleanup(mq!);
                            if ((_style & Segmentation) != 0)
                                for (var k = 3; k >= 0; k--) Code(mq!, 'M', 18, 0b1010 >> k & 1, -1, -1);
                            break;
                    }
                    if ((_style & Reset) != 0) _cx = JpxBlockDecoder.InitialContexts();
                    count++;
                    After.Add(Expected());
                    if (_pass == passes - 1 || Ends(type))
                    {
                        Segments.Add((raw is not null ? raw.Finish() : WithoutMarker(mq!.Finish()), count));
                        (mq, raw, count) = (null, null, 0);
                    }
                    if (type == 2)
                    {
                        type = 0;
                        _plane--;
                    }
                    else
                    {
                        type++;
                    }
                }
                return this;
            }

            /// <summary>Tables D.8 and D.9: termination after each pass, or with bypass after the fourth cleanup pass, each raw refinement pass and each cleanup pass after it.</summary>
            private bool Ends(int type)
            {
                if ((_style & TerminateAll) != 0) return true;
                if ((_style & Bypass) != 0) return _pass >= 9 && type != 0;
                return false;
            }

            private bool Decide(string decision, bool outcome)
            {
                Trace.Add((decision, outcome));
                return outcome;
            }

            private int BitOf(int i) => Math.Abs(_v[i]) >> _plane & 1;

            private void Cover(int i) => Coverage[(_plane, i)] = Coverage.GetValueOrDefault((_plane, i)) + 1;

            private void Code(MqEncoder mq, char kind, int context, int bit, int x, int y)
            {
                mq.Encode(_cx, context, bit);
                Log.Add(new Symbol(_pass, _plane, kind, context, bit, x, y));
            }

            private void Raw(RawWriter raw, char kind, int bit, int x, int y)
            {
                raw.Bit(bit);
                Log.Add(new Symbol(_pass, _plane, kind, -1, bit, x, y));
            }

            private IEnumerable<(int X, int Y)> Scan()
            {
                for (var y0 = 0; y0 < _h; y0 += 4)
                    for (var x = 0; x < _w; x++)
                        for (var y = y0; y < Math.Min(y0 + 4, _h); y++) yield return (x, y);
            }

            /// <summary>Significance of the neighbour (x, y) for a coefficient in row cy: outside the block insignificant (D.3); vertically causal,
            /// the row below the last row of a stripe insignificant (D.7).</summary>
            private bool Significant(int x, int y, int cy)
            {
                if (x < 0 || y < 0 || x >= _w || y >= _h) return false;
                if ((_style & Causal) != 0 && cy % 4 == 3 && y == cy + 1) return false;
                return _sig[y * _w + x];
            }

            private int Neighbour(int x, int y, int cy) => Significant(x, y, cy) ? 1 : 0;

            private int SignOf(int x, int y, int cy) => !Significant(x, y, cy) ? 0 : _neg[y * _w + x] ? -1 : 1;

            private (int H, int V, int D) Sums(int x, int y) =>
                (Neighbour(x - 1, y, y) + Neighbour(x + 1, y, y), Neighbour(x, y - 1, y) + Neighbour(x, y + 1, y),
                    Neighbour(x - 1, y - 1, y) + Neighbour(x + 1, y - 1, y) + Neighbour(x - 1, y + 1, y) + Neighbour(x + 1, y + 1, y));

            private int ZeroContextAt(int x, int y)
            {
                var (h, v, d) = Sums(x, y);
                return ZeroContext(_orientation, h, v, d);
            }

            private void Sign(int x, int y, MqEncoder? mq, RawWriter? raw)
            {
                var i = y * _w + x;
                var s = _v[i] < 0 ? 1 : 0;
                if (raw is not null)
                {
                    Raw(raw, 'g', s, x, y);   // Equation D-2
                }
                else
                {
                    var (context, xor) = SignContext(Contribution(SignOf(x - 1, y, y), SignOf(x + 1, y, y)), Contribution(SignOf(x, y - 1, y), SignOf(x, y + 1, y)));
                    Code(mq!, 'G', context, s ^ xor, x, y);   // Equation D-1
                }
                _sig[i] = true;
                _neg[i] = s == 1;
                _last[i] = _plane;
            }

            private void Significance(MqEncoder? mq, RawWriter? raw)
            {
                var first = true;
                foreach (var (x, y) in Scan())
                {
                    if (!first) Trace.Add(("D4", true));
                    first = false;
                    var i = y * _w + x;
                    if (Decide("D1", _sig[i])) continue;
                    var context = ZeroContextAt(x, y);
                    if (Decide("D2", context == 0)) continue;
                    _inSig[i] = true;
                    var bit = BitOf(i);
                    Cover(i);
                    if (raw is not null) Raw(raw, 'r', bit, x, y);
                    else Code(mq!, 'S', context, bit, x, y);
                    if (Decide("D3", bit == 1)) Sign(x, y, mq, raw);
                }
                Trace.Add(("D4", false));
            }

            private void Refinement(MqEncoder? mq, RawWriter? raw)
            {
                var first = true;
                foreach (var (x, y) in Scan())
                {
                    if (!first) Trace.Add(("D7", true));
                    first = false;
                    var i = y * _w + x;
                    if (Decide("D5", !_sig[i])) continue;
                    if (Decide("D6", _inSig[i])) continue;
                    var bit = BitOf(i);
                    Cover(i);
                    if (raw is not null)
                    {
                        Raw(raw, 'f', bit, x, y);
                    }
                    else
                    {
                        var (h, v, d) = Sums(x, y);
                        Code(mq!, 'R', _refined[i] ? 16 : h + v + d > 0 ? 15 : 14, bit, x, y);   // Table D.4
                    }
                    _refined[i] = true;
                    _last[i] = _plane;
                }
                Trace.Add(("D7", false));
            }

            private void Cleanup(MqEncoder mq)
            {
                var first = true;
                for (var y0 = 0; y0 < _h; y0 += 4)
                {
                    var rows = Math.Min(4, _h - y0);
                    for (var x = 0; x < _w; x++)
                    {
                        if (!first) Trace.Add(("D12", true));
                        first = false;
                        var y = y0;
                        var run = rows == 4 && Enumerable.Range(y0, 4).All(r => !_sig[r * _w + x] && !_inSig[r * _w + x] && ZeroContextAt(x, r) == 0);
                        if (Decide("D8", run))
                        {
                            var one = Enumerable.Range(0, 4).FirstOrDefault(r => BitOf((y0 + r) * _w + x) == 1, -1);
                            Code(mq, 'L', 17, one < 0 ? 0 : 1, x, y0);
                            for (var r = 0; r <= (one < 0 ? 3 : one); r++) Cover((y0 + r) * _w + x);
                            if (Decide("D11", one < 0)) continue;
                            Code(mq, 'U', 18, one >> 1, x, y0);
                            Code(mq, 'U', 18, one & 1, x, y0);
                            Sign(x, y0 + one, mq, null);
                            y = y0 + one + 1;
                            Decide("D10", y < y0 + 4);
                        }
                        for (; y < y0 + rows; y++)
                        {
                            var i = y * _w + x;
                            if (Decide("D9", _sig[i] || _inSig[i])) continue;
                            var bit = BitOf(i);
                            Cover(i);
                            Code(mq, 'S', ZeroContextAt(x, y), bit, x, y);
                            if (bit == 1) Sign(x, y, mq, null);
                        }
                    }
                }
                Trace.Add(("D12", false));
                Array.Clear(_inSig);
            }

            private int[] Expected()
            {
                var e = new int[_v.Length];
                for (var i = 0; i < e.Length; i++)
                {
                    if (!_sig[i]) continue;
                    var low = _last[i];
                    var q = Math.Abs(_v[i]) >> low << low;
                    var m = 2 * q + (1 << low);
                    e[i] = _neg[i] ? -m : m;
                }
                return e;
            }
        }

        /// <summary>Bits packed highest first; after a byte 0xFF a 0 bit is stuffed into the next one (D.6 for raw passes, B.10.1 for packet headers).</summary>
        public sealed class RawWriter
        {
            private readonly List<byte> _bytes = [];
            private int _current, _count, _limit = 8;

            public void Bit(int bit)
            {
                _current = _current << 1 | bit;
                if (++_count < _limit) return;
                _bytes.Add((byte)_current);
                _limit = _current == 0xFF ? 7 : 8;
                _current = 0;
                _count = 0;
            }

            public void Bits(int value, int n)
            {
                for (var k = n - 1; k >= 0; k--) Bit(value >> k & 1);
            }

            /// <summary>Pads to the byte boundary with zeros; a packet header ending in 0xFF gets the stuffed byte after it.</summary>
            public byte[] Finish(bool header = false)
            {
                if (_count > 0) _bytes.Add((byte)(_current << (_limit - _count)));
                if (header && _bytes.Count > 0 && _bytes[^1] == 0xFF) _bytes.Add(0);
                return [.. _bytes];
            }
        }

        private static int FloorLog2(int v) => 31 - int.LeadingZeroCount(v);

        private static int BitLength(int v) => 32 - int.LeadingZeroCount(v);

        /// <summary>Table B.4: the codeword of the number of coding passes.</summary>
        private static void PassCount(RawWriter w, int n)
        {
            if (n == 1) w.Bit(0);
            else if (n == 2) w.Bits(0b10, 2);
            else if (n <= 5) w.Bits(0b1100 | (n - 3), 4);
            else if (n <= 36) w.Bits(0b1111 << 5 | (n - 6), 9);
            else w.Bits(0b111111111 << 7 | (n - 37), 16);
        }

        /// <summary>
        /// The packets of one code-block in the only precinct of a tile without decomposition levels (B.10): one packet per layer with the given
        /// number of passes, the segments of the coder split between the layers, a length per codeword segment (B.10.7).
        /// </summary>
        public static byte[] Body(IReadOnlyList<(byte[] Data, int Passes)> segments, int zeroPlanes, params int[] layerPasses)
        {
            var body = new List<byte>();
            int segment = 0, donePasses = 0, doneBytes = 0, lblock = 3;
            for (var layer = 0; layer < layerPasses.Length; layer++)
            {
                var pieces = new List<(int Passes, byte[] Data)>();
                for (var left = layerPasses[layer]; left > 0;)
                {
                    var (data, passes) = segments[segment];
                    var take = Math.Min(passes - donePasses, left);
                    var end = donePasses + take == passes ? data.Length : data.Length * (donePasses + take) / passes;
                    pieces.Add((take, data[doneBytes..end]));
                    left -= take;
                    donePasses += take;
                    doneBytes = end;
                    if (donePasses == passes) (segment, donePasses, doneBytes) = (segment + 1, 0, 0);
                }
                var w = new RawWriter();
                w.Bit(1);   // packet not empty
                w.Bit(1);   // included: tag tree with one leaf, value 0 (first layer) — later one bit
                if (layer == 0)
                {
                    for (var z = 0; z < zeroPlanes; z++) w.Bit(0);
                    w.Bit(1);
                }
                PassCount(w, layerPasses[layer]);
                var need = pieces.Max(p => BitLength(p.Data.Length) - FloorLog2(p.Passes));
                for (; lblock < need; lblock++) w.Bit(1);
                w.Bit(0);
                foreach (var (passes, data) in pieces) w.Bits(data.Length, lblock + FloorLog2(passes));
                body.AddRange(w.Finish(header: true));
                foreach (var (_, data) in pieces) body.AddRange(data);
            }
            return [.. body];
        }

        /// <summary>
        /// A codestream with one component, one tile and one tile-part: SIZ (unsigned, the given bit depth), COD (LRCP, 64 × 64 code-blocks, the
        /// given style and filter), QCD (style 0 none, 1 derived, 2 expounded, with G guard bits), RGN if given, then the body.
        /// </summary>
        public static byte[] Codestream(int width, int height, int precision, int levels, bool reversible, int style, int guard, int quantStyle,
            (int Exponent, int Mantissa)[] bands, byte[] body, int layers = 1, int? roi = null)
        {
            var b = new List<byte>();
            void U8(int v) => b.Add((byte)v);
            void U16(int v)
            {
                b.Add((byte)(v >> 8));
                b.Add((byte)v);
            }
            void U32(int v)
            {
                U16(v >> 16);
                U16(v & 0xFFFF);
            }
            U16(0xFF4F);
            U16(0xFF51); U16(41); U16(0); U32(width); U32(height); U32(0); U32(0); U32(width); U32(height); U32(0); U32(0);
            U16(1); U8(precision - 1); U8(1); U8(1);
            U16(0xFF52); U16(12); U8(0); U8(0); U16(layers); U8(0); U8(levels); U8(4); U8(4); U8(style); U8(reversible ? 1 : 0);
            U16(0xFF5C);
            if (quantStyle == 0)
            {
                U16(3 + bands.Length);
                U8(guard << 5);
                foreach (var (e, _) in bands) U8(e << 3);
            }
            else
            {
                U16(3 + 2 * bands.Length);
                U8(guard << 5 | quantStyle);
                foreach (var (e, m) in bands) U16(e << 11 | m);
            }
            if (roi is { } shift)
            {
                U16(0xFF5E); U16(5); U8(0); U8(0); U8(shift);
            }
            U16(0xFF90); U16(10); U16(0); U32(14 + body.Length); U8(0); U8(1);
            U16(0xFF93);
            b.AddRange(body);
            U16(0xFFD9);
            return [.. b];
        }

        /// <summary>Decodes the only tile; the coefficients of the tile-component before the level shift (no levels: no wavelet transform).</summary>
        public static (JpxTileComponent Component, bool Truncated) Tile(byte[] codestream, bool check = true)
        {
            var cs = JpxCodestream.Parse(codestream, 0, codestream.Length);
            var state = new JpxDecodeState(1L << 40, 1L << 40, 1 << 24);
            var tile = new JpxTile(cs, 0, cs.Tiles[0]!, 0, state);
            tile.Decode();
            var truncated = state.Truncated || cs.Truncated;
            if (check) Assert.False(truncated);
            return (tile.Components[0], truncated);
        }
    }
}
