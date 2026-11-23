using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// ITU-T T.800 Annex F, the inverse discrete wavelet transform, step by step: the extent of a tile-component and of its sub-bands, the order of
/// the procedures IDWT, 2D_SR, 2D_INTERLEAVE, HOR_SR, VER_SR, 1D_SR, the periodic symmetric extension with the minimum widths of Tables F.2 and
/// F.3, the lifting steps of 1D_FILTR5-3R (F-5, F-6) and 1D_FILTR9-7I (F-7), and the derivation of the parameters in Tables F.4 to F.7.
/// Expected values are computed from the equations of the annex (an own reference in exact integers and doubles, and <see cref="AnnexF"/>).
/// </summary>
public sealed partial class JpxTests
{
    // ------------------------------------------------------------ F.1, F.2.2, F.3.1: extent and sub-bands of a tile-component

    /// <summary>
    /// T.800 F.1 (F-1 with the bounds of B-12): the tile-component of a component with XRsiz = 2 on the grid 3 ≤ x &lt; 10 spans
    /// ⌈3/2⌉ = 2 ≤ x &lt; ⌈10/2⌉ = 5 — the inverse transform delivers exactly 3 × 2 samples, and the plane of the image is completely written.
    /// </summary>
    [Fact]
    public void TileComponent_ExtentAsF1_SubsampledAndOffset()
    {
        var file = AnnexF.Codestream(3, 10, 0, 2, [2], levels: 1);
        var tc = Assert.Single(AnnexF.DecodeTile(file).Components);
        Assert.Equal((2, 0, 5, 2), (tc.X0, tc.Y0, tc.X1, tc.Y1));
        var top = tc.Resolutions[1];
        Assert.Equal((2, 0, 5, 2), (top.X0, top.Y0, top.X1, top.Y1));
        Assert.Equal(6, tc.Data.Length);

        var planes = JpxDecoder.DecodePlanes(file, long.MaxValue);
        Assert.Equal((3, 2), (planes.PlaneWidth[0], planes.PlaneHeight[0]));
        Assert.All(planes.Planes[0], v => Assert.Equal(128, v));   // every sample came from the tile (empty data, level shift 2^7)
    }

    /// <summary>
    /// T.800 F.2.2: NL from the COD gives NL + 1 resolutions — resolution 0 with the single band NLLL (orientation 0), every other with HL, LH,
    /// HH (orientations 1, 2, 3); more than 32 decomposition levels are refused.
    /// </summary>
    [Fact]
    public void DecompositionLevels_ResolutionsAndBandsAsF22()
    {
        var file = AnnexF.Codestream(0, 64, 0, 64, [1], levels: 3);
        Assert.Equal(3, JpxCodestream.Parse(file, 0, file.Length).Main.Coding!.Levels);
        var tc = Assert.Single(AnnexF.DecodeTile(file).Components);
        Assert.Equal(4, tc.Resolutions.Length);
        Assert.Equal([0], tc.Resolutions[0].Bands.Select(b => b.Orientation));
        for (var r = 1; r <= 3; r++) Assert.Equal([1, 2, 3], tc.Resolutions[r].Bands.Select(b => b.Orientation));

        var refused = AnnexF.Codestream(0, 64, 0, 64, [1], levels: 33);
        Assert.Throws<JpxException>(() => JpxCodestream.Parse(refused, 0, refused.Length));
        var limit = AnnexF.Codestream(0, 64, 0, 64, [1], levels: 32);
        Assert.Equal(32, JpxCodestream.Parse(limit, 0, limit.Length).Main.Coding!.Levels);
    }

    /// <summary>
    /// T.800 F.3.1 (sentences on the input of the IDWT): NL comes from the COD, or for one component from its COC — component 0 with NL = 2 has
    /// three resolutions, component 1 with NL = 0 one (no transform). The IDWT yields the tile-component before the DC level shift: with empty
    /// data its values stay 0; only the image planes carry the shift by 2^(Ssiz − 1) = 128.
    /// </summary>
    [Fact]
    public void Idwt_LevelsFromCodOrCoc_ResultBeforeLevelShift()
    {
        var file = AnnexF.Codestream(0, 8, 0, 8, [1, 1], levels: 2, coc: [(1, 0, 1)]);
        var tile = AnnexF.DecodeTile(file);
        Assert.Equal(3, tile.Components[0].Resolutions.Length);
        Assert.Single(tile.Components[1].Resolutions);
        Assert.Equal(2, tile.Components[0].Keep);
        Assert.Equal(0, tile.Components[1].Keep);
        Assert.All(tile.Components, tc => Assert.All(tc.Data, v => Assert.Equal(0f, v)));
        var planes = JpxDecoder.DecodePlanes(file, long.MaxValue);
        Assert.All(planes.Planes, p => Assert.All(p, v => Assert.Equal(128, v)));
    }

    /// <summary>
    /// T.800 F.3.1 (names of the sub-bands): LL is low-pass in both directions, HL high-pass horizontally, LH high-pass vertically, HH both —
    /// one level of 5-3 on a 4 × 4 tile-component at (0, 0) with a single band set to a constant: LL 10 gives a flat field, HL 4 alternates
    /// along each row, LH 4 along each column, HH 4 like a chess board.
    /// </summary>
    [Theory]
    [InlineData(0, 10f, new float[] { 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10 })]
    [InlineData(1, 4f, new float[] { -2, 2, -2, 2, -2, 2, -2, 2, -2, 2, -2, 2, -2, 2, -2, 2 })]
    [InlineData(2, 4f, new float[] { -2, -2, -2, -2, 2, 2, 2, 2, -2, -2, -2, -2, 2, 2, 2, 2 })]
    [InlineData(3, 4f, new float[] { 1, -1, 1, -1, -1, 1, -1, 1, 1, -1, 1, -1, -1, 1, -1, 1 })]
    public void SubBandNames_LowAndHighPassPerDirection(int orientation, float value, float[] expected)
    {
        var tc = AnnexF.TileComponent(0, 0, 4, 4, levels: 1, reversible: true);
        AnnexF.Place(tc, orientation == 0 ? 0 : 1, orientation, AnnexF.Grid(2, [value, value, value, value]));
        JpxWavelet.Inverse(tc);
        Assert.Equal(expected, tc.Data);
    }

    /// <summary>
    /// T.800 F.3.1, Table F.1 (one row per sub-band): with NL = 3 the sub-bands come as 3LL, then 3HL, 3LH, 3HH, 2HL … 1HH; the level nb of a
    /// band is NL for 3LL and the bands of resolution 1, and falls by one per resolution — on a 64 × 64 tile-component each band of level nb is
    /// 64 / 2^nb wide and high (B-15).
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0, 3, 8)]
    [InlineData(1, 0, 1, 3, 8)]
    [InlineData(1, 1, 2, 3, 8)]
    [InlineData(1, 2, 3, 3, 8)]
    [InlineData(2, 0, 1, 2, 16)]
    [InlineData(2, 1, 2, 2, 16)]
    [InlineData(2, 2, 3, 2, 16)]
    [InlineData(3, 0, 1, 1, 32)]
    [InlineData(3, 1, 2, 1, 32)]
    [InlineData(3, 2, 3, 1, 32)]
    public void SubBandLevels_AsTableF1(int resolution, int index, int orientation, int nb, int size)
    {
        var tc = Assert.Single(AnnexF.DecodeTile(AnnexF.Codestream(0, 64, 0, 64, [1], levels: 3)).Components);
        var band = tc.Resolutions[resolution].Bands[index];
        Assert.Equal(orientation, band.Orientation);
        Assert.Equal(64 >> nb, size);
        Assert.Equal((size, size), (band.X1 - band.X0, band.Y1 - band.Y0));
    }

    /// <summary>
    /// T.800 F.3.1 (F-2 with the bounds of B-15): the coefficients of a sub-band run from tbx0 to tbx1 − 1 — for the tile-component 3 ≤ x &lt; 10,
    /// 0 ≤ y &lt; 2 with NL = 1, 1LL spans ⌈3/2⌉ = 2 to ⌈10/2⌉ = 5, 1HL ⌈(3 − 1)/2⌉ = 1 to ⌈(10 − 1)/2⌉ = 5; together the seven columns.
    /// Vertically LL and HL span 0 to 1, LH and HH ⌈−1/2⌉ = 0 to ⌈1/2⌉ = 1.
    /// </summary>
    [Fact]
    public void SubBandExtent_AsF2AndB15()
    {
        var tc = Assert.Single(AnnexF.DecodeTile(AnnexF.Codestream(3, 10, 0, 2, [1], levels: 1)).Components);
        var ll = tc.Resolutions[0].Bands[0];
        var (hl, lh, hh) = (tc.Resolutions[1].Bands[0], tc.Resolutions[1].Bands[1], tc.Resolutions[1].Bands[2]);
        Assert.Equal((2, 0, 5, 1), (ll.X0, ll.Y0, ll.X1, ll.Y1));
        Assert.Equal((1, 0, 5, 1), (hl.X0, hl.Y0, hl.X1, hl.Y1));
        Assert.Equal((2, 0, 5, 1), (lh.X0, lh.Y0, lh.X1, lh.Y1));
        Assert.Equal((1, 0, 5, 1), (hh.X0, hh.Y0, hh.X1, hh.Y1));
        Assert.Equal(tc.X1 - tc.X0, (ll.X1 - ll.X0) + (hl.X1 - hl.X0));
    }

    /// <summary>
    /// T.800 F.3.1 (Figure F.3): the IDWT runs 2D_SR from lev = NL down to 1, each time on the LL just produced — two levels of 5-3 on a
    /// 4 × 4 tile-component (values computed by hand from F-5 and F-6). Stopped after the first level (Keep = 1, decoding at reduced size on
    /// request of the caller) the result is 1LL.
    /// </summary>
    [Fact]
    public void Idwt_AllLevelsFromNlDownToOne()
    {
        var tc = AnnexF.TileComponent(0, 0, 4, 4, levels: 2, reversible: true);
        AnnexF.Place(tc, 0, 0, AnnexF.Grid(1, [100]));
        AnnexF.Place(tc, 1, 1, AnnexF.Grid(1, [4]));
        AnnexF.Place(tc, 1, 2, AnnexF.Grid(1, [-2]));
        AnnexF.Place(tc, 1, 3, AnnexF.Grid(1, [1]));
        AnnexF.Place(tc, 2, 1, AnnexF.Grid(2, [1, -1, 2, 0]));
        AnnexF.Place(tc, 2, 2, AnnexF.Grid(2, [0, 3, -2, 1]));
        AnnexF.Place(tc, 2, 3, AnnexF.Grid(2, [1, 0, 0, -1]));
        JpxWavelet.Inverse(tc);
        Assert.Equal([98f, 100, 101, 100, 96, 101, 103, 102, 96, 99, 99, 99, 94, 98, 100, 99], tc.Data);

        var reduced = AnnexF.TileComponent(0, 0, 4, 4, levels: 2, reversible: true, keep: 1);
        AnnexF.Place(reduced, 0, 0, AnnexF.Grid(1, [100]));
        AnnexF.Place(reduced, 1, 1, AnnexF.Grid(1, [4]));
        AnnexF.Place(reduced, 1, 2, AnnexF.Grid(1, [-2]));
        AnnexF.Place(reduced, 1, 3, AnnexF.Grid(1, [1]));
        JpxWavelet.Inverse(reduced);
        Assert.Equal([99f, 103, 96, 101], reduced.Data);
    }

    // ------------------------------------------------------------ F.3.2 to F.3.5: 2D_SR, 2D_INTERLEAVE, HOR_SR, VER_SR

    /// <summary>
    /// T.800 F.3.2 (2D_SR): first interleave, then HOR_SR on all rows, then VER_SR on all columns — one level of 5-3 on a 4 × 4 tile-component
    /// at (0, 0). Columns first would give 10 in the top left corner instead of 9, so the test shows the order.
    /// </summary>
    [Fact]
    public void TwoDimensionalReconstruction_RowsThenColumns()
    {
        var tc = AnnexF.TileComponent(0, 0, 4, 4, levels: 1, reversible: true);
        AnnexF.PlaceExample(tc);
        JpxWavelet.Inverse(tc);
        Assert.Equal([9f, 14, 18, 17, 18, 27, 31, 30, 30, 36, 38, 38, 28, 35, 39, 38], tc.Data);
    }

    /// <summary>
    /// T.800 F.3.2 (2D_SR, number of coefficients): (lev − 1)LL has as many coefficients as the four sub-bands of level lev together — for every
    /// level of a 7 × 5 tile-component at (3, 1) with NL = 2, whose bands differ in size.
    /// </summary>
    [Fact]
    public void TwoDimensionalReconstruction_CoefficientCountOfTheFourBands()
    {
        var tc = Assert.Single(AnnexF.DecodeTile(AnnexF.Codestream(3, 10, 1, 6, [1], levels: 2)).Components);
        for (var r = 1; r <= 2; r++)
        {
            var lower = tc.Resolutions[r - 1];
            var bands = tc.Resolutions[r].Bands.Sum(b => (b.X1 - b.X0) * (b.Y1 - b.Y0));
            Assert.Equal(tc.Resolutions[r].Width * tc.Resolutions[r].Height, lower.Width * lower.Height + bands);
        }
        Assert.Equal(35, tc.Resolutions[2].Width * tc.Resolutions[2].Height);
    }

    /// <summary>
    /// T.800 F.3.3 (2D_INTERLEAVE with u0, v0 of (lev − 1)LL after B-15): low-pass coefficients go to even, high-pass to odd absolute positions —
    /// the same bands as in <see cref="TwoDimensionalReconstruction_RowsThenColumns"/> on a tile-component at (1, 1): the first column and row
    /// now come from the high-pass bands.
    /// </summary>
    [Fact]
    public void Interleave_OddOrigin_HighPassFirst()
    {
        var tc = AnnexF.TileComponent(1, 1, 5, 5, levels: 1, reversible: true);
        AnnexF.PlaceExample(tc);
        JpxWavelet.Inverse(tc);
        Assert.Equal([12f, 10, 15, 22, 11, 10, 14, 19, 19, 18, 22, 30, 32, 30, 35, 39], tc.Data);
    }

    /// <summary>
    /// T.800 F.3.4 (HOR_SR): 1D_SR on every row, the result stored back in the row — a tile-component of one row 4 × 1 with LL = [10, 12] and
    /// HL = [3, −2]; the columns of length 1 at an even position stay as they are.
    /// </summary>
    [Fact]
    public void HorizontalReconstruction_EachRow()
    {
        var tc = AnnexF.TileComponent(0, 0, 4, 1, levels: 1, reversible: true);
        AnnexF.Place(tc, 0, 0, AnnexF.Grid(2, [10, 12]));
        AnnexF.Place(tc, 1, 1, AnnexF.Grid(2, [3, -2]));
        JpxWavelet.Inverse(tc);
        Assert.Equal([8f, 13, 12, 10], tc.Data);
    }

    /// <summary>
    /// T.800 F.3.5 (VER_SR): 1D_SR on every column, the result stored back in the column — a tile-component of one column 1 × 4 with LL = [10, 12]
    /// and LH = [3, −2].
    /// </summary>
    [Fact]
    public void VerticalReconstruction_EachColumn()
    {
        var tc = AnnexF.TileComponent(0, 0, 1, 4, levels: 1, reversible: true);
        AnnexF.Place(tc, 0, 0, AnnexF.Grid(1, [10, 12]));
        AnnexF.Place(tc, 1, 2, AnnexF.Grid(1, [3, -2]));
        JpxWavelet.Inverse(tc);
        Assert.Equal([8f, 13, 12, 10], tc.Data);
    }

    /// <summary>
    /// T.800 F.3.2 to F.3.5 against the reference of the annex: one level on a 37 × 5 tile-component at (0, 1) with pseudo-random coefficients,
    /// both filters — the columns are reconstructed in strips (32 wide, then 5), which must give the same as 2D_INTERLEAVE, HOR_SR and VER_SR
    /// after the equations.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TwoDimensionalReconstruction_WideOddField_AsReference(bool reversible)
    {
        var tc = AnnexF.TileComponent(0, 1, 37, 6, levels: 1, reversible: reversible);
        var random = new Random(15444);
        var bands = new double[4][,];
        for (var o = 0; o < 4; o++)
        {
            var (w, h) = o == 0 ? (tc.Resolutions[0].Width, tc.Resolutions[0].Height) : AnnexF.BandSize(tc, o);
            bands[o] = new double[h, w];
            for (var j = 0; j < h; j++)
            for (var i = 0; i < w; i++) bands[o][j, i] = random.Next(-60, 61);
            AnnexF.Place(tc, o == 0 ? 0 : 1, o, AnnexF.ToFloat(bands[o]));
        }
        var expected = AnnexF.Sr2d(bands[0], bands[1], bands[2], bands[3], 0, 37, 1, 6, reversible);
        JpxWavelet.Inverse(tc);
        for (var v = 0; v < 5; v++)
        for (var u = 0; u < 37; u++)
            AnnexF.Close(expected[v, u], tc.Data[v * 37 + u], $"({u}, {v + 1})");
    }

    // ------------------------------------------------------------ F.3.6: 1D_SR

    /// <summary>
    /// T.800 F.3.6: 1D_SR takes Y on i0 ≤ i &lt; i1 and returns X on the same indices; from length 2 on, extension and filter — 5-3, length 4,
    /// Y = [10, 3, 12, −2]: at an even i0 the even positions are low-pass, [8, 13, 12, 10]; at an odd i0 the roles change, [7, −3, 6, −8].
    /// </summary>
    [Theory]
    [InlineData(0, new float[] { 8, 13, 12, 10 })]
    [InlineData(1, new float[] { 7, -3, 6, -8 })]
    public void OneDimensionalReconstruction_SameIndices(int i0, float[] expected) =>
        Assert.Equal(expected, AnnexF.Lift([10, 3, 12, -2], i0, reversible: true));

    /// <summary>
    /// T.800 F.3.6 to F.3.8 for all lengths 2 to 13 and starts 0 to 5, both filters: <see cref="JpxWavelet.Lift"/> equals the reference built
    /// from F-3 to F-7 with exactly the extension of Tables F.2 and F.3 and the steps only over their ranges of n.
    /// </summary>
    [Fact]
    public void OneDimensionalReconstruction_AllLengthsAndStarts_AsReference()
    {
        var random = new Random(800);
        foreach (var reversible in new[] { true, false })
        for (var n = 2; n <= 13; n++)
        for (var i0 = 0; i0 <= 5; i0++)
        {
            var y = Enumerable.Range(0, n).Select(_ => (double)random.Next(-100, 101)).ToArray();
            var expected = AnnexF.Sr1d(y, i0, reversible);
            var actual = AnnexF.Lift([.. y.Select(v => (float)v)], i0, reversible);
            for (var k = 0; k < n; k++) AnnexF.Close(expected[k], actual[k], $"{(reversible ? "5-3" : "9-7")} n = {n}, i0 = {i0}, k = {k}");
        }
    }

    /// <summary>
    /// T.800 F.3.6 (Figure F.13, signal of length 1): at an even position X = Y for both filters, at an odd position X = Y / 2 — only values for
    /// which Y / 2 is the same in both transforms (the case of an odd value in the reversible transform is a separate question).
    /// </summary>
    [Theory]
    [InlineData(0, true, 7f, 7f)]
    [InlineData(0, false, 7f, 7f)]
    [InlineData(1, false, 7f, 3.5f)]
    [InlineData(1, true, 8f, 4f)]
    [InlineData(1, true, -8f, -4f)]
    public void SingleSample_EvenKeptOddHalved(int i0, bool reversible, float y, float expected) =>
        Assert.Equal([expected], AnnexF.Lift([y], i0, reversible));

    /// <summary>
    /// T.800 F.3.6, the case left open above: an odd value at an odd position in the reversible transform. A conforming encoder writes 2·X
    /// there (F.4.8.2), so the value is always even; an odd one only comes from a damaged file. It is halved towards zero like the integer
    /// steps of the 5-3 transform, the result stays an integer (decided on 23.11.2026: kept so, not Y / 2 exactly).
    /// </summary>
    [Theory]
    [InlineData(7f, 3f)]
    [InlineData(-7f, -3f)]
    [InlineData(1f, 0f)]
    public void SingleSample_ReversibleOddValue_HalvedTowardsZero(float y, float expected) =>
        Assert.Equal([expected], AnnexF.Lift([y], 1, reversible: true));

    /// <summary>
    /// T.800 F.3.6 (Figure F.13) in HOR_SR and VER_SR: a tile-component of a single sample — at (0, 0) it is LL and stays, at (1, 0) a row of
    /// length 1 at an odd position (HL), at (0, 1) a column of length 1 at an odd position (LH), each halved.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0, true, 7f, 7f)]
    [InlineData(1, 0, 1, false, 7f, 3.5f)]
    [InlineData(1, 0, 1, true, 8f, 4f)]
    [InlineData(0, 1, 2, false, 7f, 3.5f)]
    [InlineData(0, 1, 2, true, 8f, 4f)]
    public void SingleSample_InRowsAndColumns_EvenKeptOddHalved(int x0, int y0, int orientation, bool reversible, float y, float expected)
    {
        var tc = AnnexF.TileComponent(x0, y0, x0 + 1, y0 + 1, levels: 1, reversible: reversible);
        AnnexF.Place(tc, orientation == 0 ? 0 : 1, orientation, AnnexF.Grid(1, [y]));
        JpxWavelet.Inverse(tc);
        Assert.Equal([expected], tc.Data);
    }

    /// <summary>
    /// T.800 F.3.6 (the filter is signalled in COD or COC): COD with transform 0 (9-7), COC for component 1 with transform 1 (5-3) — component 0
    /// reconstructs with 9-7 (not integers), component 1 with 5-3 (integers after F-5, F-6), from the same coefficients.
    /// </summary>
    [Fact]
    public void FilterChoice_FromCodOrCoc()
    {
        var tile = AnnexF.DecodeTile(AnnexF.Codestream(0, 4, 0, 1, [1, 1], levels: 1, transform: 0, coc: [(1, 1, 1)]));
        Assert.False(tile.Components[0].Coding.Reversible);
        Assert.True(tile.Components[1].Coding.Reversible);
        foreach (var tc in tile.Components)
        {
            Array.Clear(tc.Data);
            AnnexF.Place(tc, 0, 0, AnnexF.Grid(2, [10, 12]));
            AnnexF.Place(tc, 1, 1, AnnexF.Grid(2, [3, -2]));
            JpxWavelet.Inverse(tc);
        }
        Assert.Equal([8f, 13, 12, 10], tile.Components[1].Data);
        var irreversible = AnnexF.Sr1d([10, 3, 12, -2], 0, reversible: false);
        for (var k = 0; k < 4; k++) AnnexF.Close(irreversible[k], tile.Components[0].Data[k], $"k = {k}");
        Assert.Contains(tile.Components[0].Data, v => Math.Abs(v - MathF.Round(v)) > 0.01);
    }

    // ------------------------------------------------------------ F.3.7: 1D_EXTR

    /// <summary>
    /// T.800 F.3.7 (F-3, F-4): periodic symmetric extension — mirrored at the first and the last value, periodic with 2(i1 − i0 − 1), also when
    /// the extension is longer than the signal. The source indices (relative to i0) of the values left of i0 and right of i1 − 1 are computed by
    /// hand from F-4; the reference built from exactly these values equals <see cref="JpxWavelet.Lift"/>.
    /// </summary>
    [Theory]
    [InlineData(3, 1, false, new[] { 1, 2, 1, 0 }, new[] { 1, 0, 1, 2 })]
    [InlineData(3, 1, true, new[] { 1, 2 }, new[] { 1, 0 })]
    [InlineData(2, 1, false, new[] { 1, 0, 1, 0 }, new[] { 0, 1, 0 })]
    [InlineData(2, 2, false, new[] { 1, 0, 1 }, new[] { 0, 1, 0, 1 })]
    [InlineData(4, 0, true, new[] { 1 }, new[] { 2, 1 })]
    public void PeriodicSymmetricExtension_AsF3AndF4(int n, int i0, bool reversible, int[] left, int[] right)
    {
        var i1 = i0 + n;
        for (var k = 1; k <= left.Length; k++) Assert.Equal(i0 + left[k - 1], AnnexF.Pse(i0 - k, i0, i1));
        for (var k = 1; k <= right.Length; k++) Assert.Equal(i0 + right[k - 1], AnnexF.Pse(i1 - 1 + k, i0, i1));
        double[] y = [.. Enumerable.Range(0, n).Select(k => 7.0 * k * k - 13 * k + 5)];
        var expected = AnnexF.Sr1d(y, i0, reversible, leftSources: left, rightSources: right);
        var actual = AnnexF.Lift([.. y.Select(v => (float)v)], i0, reversible);
        for (var k = 0; k < n; k++) AnnexF.Close(expected[k], actual[k], $"k = {k}");
    }

    /// <summary>
    /// T.800 F.3.7, Tables F.2 and F.3 (one row per value): ileft is 1 (5-3) or 3 (9-7) at an even i0 and one more at an odd i0; iright 1 or 3 at
    /// an odd i1 and one more at an even i1. With exactly that extension (no value beyond it) the steps over their ranges of n give the output of
    /// <see cref="JpxWavelet.Lift"/>; one value less leaves an output undefined (the value is the minimum), more values change nothing.
    /// </summary>
    [Theory]
    [InlineData(true, true, 4, 6, 1)]
    [InlineData(true, true, 5, 6, 2)]
    [InlineData(false, true, 4, 6, 3)]
    [InlineData(false, true, 5, 6, 4)]
    [InlineData(true, false, 4, 7, 1)]
    [InlineData(true, false, 4, 6, 2)]
    [InlineData(false, false, 4, 7, 3)]
    [InlineData(false, false, 4, 6, 4)]
    public void ExtensionWidths_AsTablesF2AndF3(bool reversible, bool leftSide, int i0, int n, int width)
    {
        var i1 = i0 + n;
        var other = leftSide ? AnnexF.Right(reversible, i1) : AnnexF.Left(reversible, i0);
        Assert.Equal(width, leftSide ? AnnexF.Left(reversible, i0) : AnnexF.Right(reversible, i1));
        double[] y = [.. Enumerable.Range(0, n).Select(k => (double)((k * 37 + i0 * 11) % 23 - 11))];
        double[] With(int w) => leftSide ? AnnexF.Sr1d(y, i0, reversible, left: w, right: other) : AnnexF.Sr1d(y, i0, reversible, left: other, right: w);

        var exact = With(width);
        var actual = AnnexF.Lift([.. y.Select(v => (float)v)], i0, reversible);
        for (var k = 0; k < n; k++) AnnexF.Close(exact[k], actual[k], $"k = {k}");
        Assert.Contains(With(width - 1), double.IsNaN);
        var wider = With(width + 2);
        for (var k = 0; k < n; k++) Assert.Equal(exact[k], wider[k], 12);
    }

    // ------------------------------------------------------------ F.3.8: 1D_FILTR

    /// <summary>
    /// T.800 F.3.8: the reversible procedure works in integers, the irreversible one without rounding — length 2 at an even i0, Y = [2, 3]:
    /// 5-3 gives [0, 3], 9-7 gives [0.5, 3.5].
    /// </summary>
    [Fact]
    public void Filter_ReversibleInIntegers_IrreversibleWithoutRounding()
    {
        Assert.Equal([0f, 3f], AnnexF.Lift([2, 3], 0, reversible: true));
        var irreversible = AnnexF.Lift([2, 3], 0, reversible: false);
        Assert.Equal(0.5, irreversible[0], 1e-5);
        Assert.Equal(3.5, irreversible[1], 1e-5);
    }

    /// <summary>
    /// T.800 F.3.8.1: F-5 first for all n, then F-6 with the values of F-5 — Y = [10, 3, 12, −2] at an even i0 gives [8, 13, 12, 10]; F-6 with
    /// the values of Y instead of those of F-5 would give X(1) = 14.
    /// </summary>
    [Fact]
    public void ReversibleFilter_F6UsesTheValuesOfF5() =>
        Assert.Equal([8f, 13, 12, 10], AnnexF.Lift([10, 3, 12, -2], 0, reversible: true));

    /// <summary>
    /// T.800 F.3.8.1, F-5 (even positions, rounded down): Y = [0, −2, 0] at an even i0 — ⌊(−2 − 2 + 2) / 4⌋ = −1, so [1, −1, 1]; truncating would
    /// give [0, −2, 0]. In a row (<see cref="JpxWavelet.Lift"/>) and in a column (the strip of VER_SR).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReversibleFilter_F5_RoundsDown(bool column) =>
        Assert.Equal([1f, -1, 1], AnnexF.Reconstruct53([0, -2, 0], column));

    /// <summary>
    /// T.800 F.3.8.1, F-6 (odd positions, rounded down): Y = [−1, 0, −2] at an even i0 — X(0) = −1 − ⌊(0 + 0 + 2) / 4⌋ = −1, X(2) = −2 −
    /// ⌊(0 + 0 + 2) / 4⌋ = −2, X(1) = 0 + ⌊−3 / 2⌋ = −2; truncating would give −1. In a row and in a column.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReversibleFilter_F6_RoundsDown(bool column) =>
        Assert.Equal([-1f, -2, -2], AnnexF.Reconstruct53([-1, 0, -2], column));

    /// <summary>
    /// T.800 F.3.8.1: the reversible transform is exact in integers for every magnitude of the coefficients. Y = [2^24 + 2, 1, 2^24 + 2] at an
    /// even i0 gives X = [2^24 + 1, 2^24 + 2, 2^24 + 1] after F-5 and F-6 — values a 32-bit float cannot hold, so the decoder lifts tile-components
    /// with many bit-planes in double precision: in a row and in a column (<see cref="JpxWavelet.Inverse"/> on the exact store). Before 5-3
    /// was lifted in single precision only and gave 2^24 three times (docs/FEHLERGEDAECHTNIS.md, No. 1045).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReversibleFilter_LargeCoefficients_ExactIntegers(bool column)
    {
        const double big = 16777216;   // 2^24
        Assert.Equal([big + 1, big + 2, big + 1], AnnexF.Reconstruct53Exact([big + 2, 1, big + 2], column));
    }

    /// <summary>
    /// T.800 F.3.8.1 with F.3.2 (2D_SR over two levels) on the samples of a 28-bit component: the tile-component 1 ≤ x &lt; 8, 1 ≤ y &lt; 7
    /// (odd start, so both phases of the extension occur), each of its seven sub-bands with one coefficient near ±2^27 and the others up to
    /// ±2^23. Reconstructed bit for bit as an own reference of F-5 and F-6 in exact integers computes it (up to 3 · 10^8, beyond 2^28); a lifting
    /// in single precision differs at 37 of the 42 samples. The decoder takes this path for a reversible tile-component of more than 23
    /// magnitude bit-planes (docs/FEHLERGEDAECHTNIS.md, No. 1045).
    /// </summary>
    [Fact]
    public void ReversibleFilter_28BitSamples_TwoLevels_BitExact()
    {
        var tc = AnnexF.TileComponent(1, 1, 8, 7, levels: 2, reversible: true);
        tc.Exact = new double[tc.Data.Length];
        tc.Data = [];
        AnnexF.PlaceExact(tc, 0, 0, 1, [134217725]);
        AnnexF.PlaceExact(tc, 1, 1, 2, [-134217723, -1762260]);
        AnnexF.PlaceExact(tc, 1, 2, 1, [134217721, -6349686]);
        AnnexF.PlaceExact(tc, 1, 3, 2, [-134217717, -6435048, -2445967, 5928790]);
        AnnexF.PlaceExact(tc, 2, 1, 4, [134217715, 2933956, -6574163, -6376734, -2148749, -7116496, -1009239, -451794, -1259825, 8251484, -7329947, 8371258]);
        AnnexF.PlaceExact(tc, 2, 2, 3, [-134217711, 3092040, -2042655, -6462970, -8077113, 2942708, 3464733, -6449006, -8215965]);
        AnnexF.PlaceExact(tc, 2, 3, 4, [134217709, -3784935, -480290, -851521, -4579188, 7517141, 6268906, -2098469, 3062136, 3651665, -7841610, -2528841]);
        JpxWavelet.Inverse(tc);
        double[] expected =
        [
            141201795, -94823998, 102785580, 303963221, 288711594, 290463182, 283972424,
            173809991, 72001907, 187904295, 299804875, 287967562, 292172884, 286533647,
            89888553, 44448442, 128460699, 205346468, 210449962, 211653024, 207087453,
            29520408, 31289894, 72703905, 133935311, 130018076, 127332966, 128038000,
            25869863, 25087916, 75967811, 123027423, 112505225, 121691451, 124332966,
            12522480, 15313373, 75543626, 122922575, 121700466, 127296641, 136932319,
        ];
        Assert.Equal(expected, tc.Exact);
    }

    /// <summary>
    /// T.800 F.3.8.2 (F-7): lifting without rounding, length 2, Y = [2, 3] — at an even i0 [0.5, 3.5], at an odd i0 [4, 2].
    /// </summary>
    [Theory]
    [InlineData(0, 0.5, 3.5)]
    [InlineData(1, 4.0, 2.0)]
    public void IrreversibleFilter_AsF7(int i0, double x0, double x1)
    {
        var x = AnnexF.Lift([2, 3], i0, reversible: false);
        Assert.Equal(x0, x[0], 1e-5);
        Assert.Equal(x1, x[1], 1e-5);
    }

    /// <summary>
    /// T.800 F.3.8.2, F-7, one row per step (1, 2 scaling, 3 to 6 lifting, in this order, each over its range of n): for Y = [2, 3] at an even i0
    /// the reference reaches the value computed by hand after this step (2K, 3/K, 0.297206, 1.913866, 0.5, 3.5); on a longer signal at an odd
    /// i0 the full reference equals <see cref="JpxWavelet.Lift"/>, and the reference without this step does not (the signal detects the step).
    /// </summary>
    [Theory]
    [InlineData(1, 0, 2.460348)]
    [InlineData(2, 1, 2.438679)]
    [InlineData(3, 0, 0.297206)]
    [InlineData(4, 1, 1.913866)]
    [InlineData(5, 0, 0.5)]
    [InlineData(6, 1, 3.5)]
    public void IrreversibleFilter_EachStepOfF7(int step, int index, double value)
    {
        Assert.Equal(value, AnnexF.Sr1d([2, 3], 0, reversible: false, lastStep: step)[index], 1e-6);

        double[] y = [3, -1, 4, 1, -5, 9, 2, -6];
        var full = AnnexF.Sr1d(y, 5, reversible: false);
        var actual = AnnexF.Lift([.. y.Select(v => (float)v)], 5, reversible: false);
        for (var k = 0; k < y.Length; k++) AnnexF.Close(full[k], actual[k], $"k = {k}");
        var without = AnnexF.Sr1d(y, 5, reversible: false, omitStep: step);
        Assert.Contains(Enumerable.Range(0, y.Length), k => Math.Abs(without[k] - actual[k]) > 0.01);
    }

    // ------------------------------------------------------------ F.3.8.2.1: Tables F.4 to F.7

    /// <summary>
    /// T.800 F.3.8.2.1, Table F.4 (closed expressions): α = −g4/g3, β = g3/r1, γ = r1/s0, δ = s0/t0, K = 1/t0 from the values derived from Table
    /// F.7 — equal to the approximate values of the table to 1e−14, and as float exactly the constants of the decoder.
    /// </summary>
    [Theory]
    [InlineData("alpha", -1.586134342059924)]
    [InlineData("beta", -0.052980118572961)]
    [InlineData("gamma", 0.882911075530934)]
    [InlineData("delta", 0.443506852043971)]
    [InlineData("K", 1.230174104914001)]
    public void LiftingParameters_ClosedExpressionsOfTableF4(string name, double approximate)
    {
        var p = AnnexF.Parameters();
        Assert.Equal(approximate, p[name], 1e-14);
        var constant = name switch
        {
            "alpha" => JpxWavelet.Alpha, "beta" => JpxWavelet.Beta, "gamma" => JpxWavelet.Gamma, "delta" => JpxWavelet.Delta, _ => JpxWavelet.K,
        };
        Assert.Equal((float)p[name], constant);
    }

    /// <summary>
    /// T.800 F.3.8.2.1 (the derivation as a whole): from Table F.7 over the coefficients gn of Table F.5 and r0, r1, s0, t0 of Table F.6 to the
    /// parameters of Table F.4 in double precision — every one of the nineteen values equal to its approximation to 1e−14, and the end of the
    /// chain the constants of the decoder (1/K included).
    /// </summary>
    [Fact]
    public void FilterParameters_DerivedFromTableF7()
    {
        var p = AnnexF.Parameters();
        Assert.Equal(AnnexF.Approximations.Count, p.Count);
        foreach (var (name, value) in AnnexF.Approximations) Assert.True(Math.Abs(p[name] - value) < 1e-14, $"{name}: {p[name]:R} statt {value:R}");
        Assert.Equal(
            [JpxWavelet.Alpha, JpxWavelet.Beta, JpxWavelet.Gamma, JpxWavelet.Delta, JpxWavelet.K, JpxWavelet.InvK],
            new[] { p["alpha"], p["beta"], p["gamma"], p["delta"], p["K"], p["t0"] }.Select(v => (float)v));
    }

    /// <summary>
    /// T.800 F.3.8.2.1, Tables F.5, F.6 and F.7 (one row per value): each value from its closed expression (with the values computed before it)
    /// equals the approximation of the table to 1e−14; taken as given, the approximation leads through the rest of the chain to the constants
    /// of the decoder.
    /// </summary>
    [Theory]
    [InlineData("A", 0.128030244703494)]
    [InlineData("B", -0.303747672895197)]
    [InlineData("x1", -0.342384094858369)]
    [InlineData("Re x2", -0.078807952570815)]
    [InlineData("|x2|^2", 0.146034820982800)]
    [InlineData("g0", -0.602949018236360)]
    [InlineData("g1", 0.266864118442875)]
    [InlineData("g2", 0.078223266528990)]
    [InlineData("g3", -0.016864118442875)]
    [InlineData("g4", -0.026748757410810)]
    [InlineData("r0", 1.449513704087943)]
    [InlineData("r1", 0.318310318985991)]
    [InlineData("s0", 0.360523644801462)]
    [InlineData("t0", 0.812893066115961)]
    public void FilterParameters_TablesF5ToF7(string name, double approximate)
    {
        Assert.Equal(approximate, AnnexF.Parameters()[name], 1e-14);
        var p = AnnexF.Parameters(name, approximate);
        Assert.Equal(
            [JpxWavelet.Alpha, JpxWavelet.Beta, JpxWavelet.Gamma, JpxWavelet.Delta, JpxWavelet.K],
            new[] { p["alpha"], p["beta"], p["gamma"], p["delta"], p["K"] }.Select(v => (float)v));
    }

    /// <summary>Helpers for Annex F: a reference of the procedures after the equations, hand-built tile-components, small codestreams.</summary>
    private static class AnnexF
    {
        /// <summary>The margin on each side that <see cref="JpxWavelet.Lift"/> expects around the signal.</summary>
        private const int Margin = 4;

        /// <summary>Approximate values of Tables F.4 to F.7.</summary>
        public static readonly IReadOnlyDictionary<string, double> Approximations = new Dictionary<string, double>
        {
            ["A"] = 0.128030244703494, ["B"] = -0.303747672895197, ["x1"] = -0.342384094858369, ["Re x2"] = -0.078807952570815,
            ["|x2|^2"] = 0.146034820982800,
            ["g0"] = -0.602949018236360, ["g1"] = 0.266864118442875, ["g2"] = 0.078223266528990, ["g3"] = -0.016864118442875,
            ["g4"] = -0.026748757410810,
            ["r0"] = 1.449513704087943, ["r1"] = 0.318310318985991, ["s0"] = 0.360523644801462, ["t0"] = 0.812893066115961,
            ["alpha"] = -1.586134342059924, ["beta"] = -0.052980118572961, ["gamma"] = 0.882911075530934, ["delta"] = 0.443506852043971,
            ["K"] = 1.230174104914001,
        };

        /// <summary>
        /// The chain of Tables F.7, F.5, F.6, F.4 in double precision; <paramref name="given"/> replaces the computed value of that name by
        /// <paramref name="value"/> before the later ones are computed.
        /// </summary>
        public static Dictionary<string, double> Parameters(string? given = null, double value = 0)
        {
            var p = new Dictionary<string, double>();
            double Set(string name, double v) => p[name] = name == given ? value : v;
            var root15 = Math.Sqrt(15);
            var a = Set("A", Math.Cbrt((63 - 14 * root15) / (1080 * root15)));
            var b = Set("B", Math.Cbrt(-(63 + 14 * root15) / (1080 * root15)));
            var x1 = Set("x1", a + b - 1.0 / 6);
            var re = Set("Re x2", -(a + b) / 2 - 1.0 / 6);
            var abs2 = Set("|x2|^2", re * re + 3 * (a - b) * (a - b) / 4);
            var g0 = Set("g0", 5 * x1 * (48 * abs2 - 16 * re + 3) / 32);
            var g1 = Set("g1", -5 * x1 * (8 * abs2 - re) / 8);
            var g2 = Set("g2", 5 * x1 * (4 * abs2 + 4 * re - 1) / 16);
            var g3 = Set("g3", -5 * x1 * re / 8);
            var g4 = Set("g4", 5 * x1 / 64);
            var r0 = Set("r0", -g0 + 2 * g1 * g4 / g3);
            var r1 = Set("r1", -g2 + g4 + g1 * g4 / g3);
            var s0 = Set("s0", g1 - g3 - g3 * r0 / r1);
            var t0 = Set("t0", r0 - 2 * r1);
            Set("alpha", -g4 / g3);
            Set("beta", g3 / r1);
            Set("gamma", r1 / s0);
            Set("delta", s0 / t0);
            Set("K", 1 / t0);
            return p;
        }

        /// <summary>Minimum ileft of Table F.2: 5-3 1 at an even i0, 2 at an odd one; 9-7 3 and 4.</summary>
        public static int Left(bool reversible, int i0) => (reversible ? 1 : 3) + (i0 & 1);

        /// <summary>Minimum iright of Table F.3: 5-3 1 at an odd i1, 2 at an even one; 9-7 3 and 4.</summary>
        public static int Right(bool reversible, int i1) => (reversible ? 1 : 3) + 1 - (i1 & 1);

        /// <summary>PSEO of F-4.</summary>
        public static int Pse(int i, int i0, int i1)
        {
            var period = 2 * (i1 - i0 - 1);
            var m = ((i - i0) % period + period) % period;
            return i0 + Math.Min(m, period - m);
        }

        /// <summary>
        /// 1D_SR after F.3.6 to F.3.8 in double precision: Yext over exactly i0 − ileft ≤ i &lt; i1 + iright from F-3 (or from the given source
        /// indices relative to i0), nothing beyond (NaN); X starts undefined (NaN) and every step runs only over its range of n — a value the
        /// annex does not define shows as NaN. <paramref name="lastStep"/> stops F-7 after that step; <paramref name="omitStep"/> leaves a lifting
        /// step out, or a scaling step as a plain copy.
        /// </summary>
        public static double[] Sr1d(double[] y, int i0, bool reversible, int? left = null, int? right = null, int[]? leftSources = null,
            int[]? rightSources = null, int lastStep = 6, int omitStep = 0)
        {
            int n = y.Length, i1 = i0 + n;
            if (n == 1) return [(i0 & 1) == 0 ? y[0] : y[0] / 2];
            var l = leftSources?.Length ?? left ?? Left(reversible, i0);
            var r = rightSources?.Length ?? right ?? Right(reversible, i1);
            const int border = 12;
            var ext = new double[n + 2 * border];
            var x = new double[n + 2 * border];
            Array.Fill(ext, double.NaN);
            Array.Fill(x, double.NaN);
            int S(int i) => i - i0 + border;
            for (var i = i0; i < i1; i++) ext[S(i)] = y[i - i0];
            for (var k = 1; k <= l; k++) ext[S(i0 - k)] = y[leftSources is null ? Pse(i0 - k, i0, i1) - i0 : leftSources[k - 1]];
            for (var k = 1; k <= r; k++) ext[S(i1 - 1 + k)] = y[rightSources is null ? Pse(i1 - 1 + k, i0, i1) - i0 : rightSources[k - 1]];

            if (reversible)
            {
                // F-5, then F-6 with the values of F-5
                for (var m = i0 / 2; m < i1 / 2 + 1; m++)
                    x[S(2 * m)] = ext[S(2 * m)] - Math.Floor((ext[S(2 * m - 1)] + ext[S(2 * m + 1)] + 2) / 4);
                for (var m = i0 / 2; m < i1 / 2; m++)
                    x[S(2 * m + 1)] = ext[S(2 * m + 1)] + Math.Floor((x[S(2 * m)] + x[S(2 * m + 2)]) / 2);
            }
            else
            {
                const double alpha = -1.586134342059924, beta = -0.052980118572961, gamma = 0.882911075530934, delta = 0.443506852043971;
                const double kappa = 1.230174104914001;
                (int Odd, int From, int To, double Factor)[] steps =
                [
                    (0, i0 / 2 - 1, i1 / 2 + 2, kappa), (1, i0 / 2 - 2, i1 / 2 + 2, 1 / kappa), (0, i0 / 2 - 1, i1 / 2 + 2, delta),
                    (1, i0 / 2 - 1, i1 / 2 + 1, gamma), (0, i0 / 2, i1 / 2 + 1, beta), (1, i0 / 2, i1 / 2, alpha),
                ];
                for (var s = 1; s <= lastStep; s++)
                {
                    var (odd, from, to, factor) = steps[s - 1];
                    for (var m = from; m < to; m++)
                    {
                        var k = 2 * m + odd;
                        if (s <= 2) x[S(k)] = (s == omitStep ? 1 : factor) * ext[S(k)];
                        else if (s != omitStep) x[S(k)] -= factor * (x[S(k - 1)] + x[S(k + 1)]);
                    }
                }
            }
            return x[S(i0)..S(i1)];
        }

        /// <summary>
        /// One 2D_SR after F.3.2 and F.3.3 in double precision: the four sub-bands (local [row, column] from their first index after B-15)
        /// interleaved by the absolute position in u0 ≤ u &lt; u1, v0 ≤ v &lt; v1 (even: low-pass, odd: high-pass), then 1D_SR on every row, then
        /// on every column.
        /// </summary>
        public static double[,] Sr2d(double[,] ll, double[,] hl, double[,] lh, double[,] hh, int u0, int u1, int v0, int v1, bool reversible)
        {
            static int CeilHalf(int v) => (int)Math.Ceiling(v / 2.0);
            int w = u1 - u0, h = v1 - v0;
            var a = new double[h, w];
            for (var v = v0; v < v1; v++)
            for (var u = u0; u < u1; u++)
            {
                var band = (u & 1, v & 1) switch { (0, 0) => ll, (1, 0) => hl, (0, 1) => lh, _ => hh };
                var bx = (u >> 1) - ((u & 1) == 0 ? CeilHalf(u0) : CeilHalf(u0 - 1));
                var by = (v >> 1) - ((v & 1) == 0 ? CeilHalf(v0) : CeilHalf(v0 - 1));
                a[v - v0, u - u0] = band[by, bx];
            }
            for (var v = 0; v < h; v++)
            {
                var row = Sr1d([.. Enumerable.Range(0, w).Select(u => a[v, u])], u0, reversible);
                for (var u = 0; u < w; u++) a[v, u] = row[u];
            }
            for (var u = 0; u < w; u++)
            {
                var column = Sr1d([.. Enumerable.Range(0, h).Select(v => a[v, u])], v0, reversible);
                for (var v = 0; v < h; v++) a[v, u] = column[v];
            }
            return a;
        }

        /// <summary>
        /// <see cref="JpxWavelet.Lift"/> on a signal starting at the absolute index <paramref name="i0"/>; the margins hold NaN before, so a value
        /// the extension did not set would show in the output.
        /// </summary>
        public static float[] Lift(float[] y, int i0, bool reversible)
        {
            var x = new float[y.Length + 2 * Margin];
            Array.Fill(x, float.NaN);
            y.CopyTo(x, Margin);
            JpxWavelet.Lift(x, y.Length, i0 & 1, reversible);
            return x[Margin..(Margin + y.Length)];
        }

        /// <summary>
        /// 5-3 on a signal of length 3 at i0 = 0: in a row (<see cref="JpxWavelet.Lift"/>) or through <see cref="JpxWavelet.Inverse"/> on a
        /// tile-component one sample wide (the column strips of VER_SR); Y interleaved, low-pass at 0 and 2.
        /// </summary>
        public static float[] Reconstruct53(float[] y, bool column)
        {
            if (!column) return Lift(y, 0, reversible: true);
            var tc = TileComponent(0, 0, 1, 3, levels: 1, reversible: true);
            Place(tc, 0, 0, Grid(1, [y[0], y[2]]));
            Place(tc, 1, 2, Grid(1, [y[1]]));
            JpxWavelet.Inverse(tc);
            return tc.Data;
        }

        /// <summary>
        /// <see cref="Reconstruct53"/> on the exact store: in a row (<see cref="JpxWavelet.Lift53"/>, margins NaN as in <see cref="Lift"/>) or
        /// through <see cref="JpxWavelet.Inverse"/> on a tile-component one sample wide whose coefficients lie in <see cref="JpxTileComponent.Exact"/>.
        /// </summary>
        public static double[] Reconstruct53Exact(double[] y, bool column)
        {
            if (!column)
            {
                var x = new double[y.Length + 2 * Margin];
                Array.Fill(x, double.NaN);
                y.CopyTo(x, Margin);
                JpxWavelet.Lift53(x, y.Length, 0);
                return x[Margin..(Margin + y.Length)];
            }
            var tc = TileComponent(0, 0, 1, 3, levels: 1, reversible: true);
            tc.Exact = new double[tc.Data.Length];
            tc.Data = [];
            PlaceExact(tc, 0, 0, 1, [y[0], y[2]]);
            PlaceExact(tc, 1, 2, 1, [y[1]]);
            JpxWavelet.Inverse(tc);
            return tc.Exact;
        }

        /// <summary><see cref="Place"/> for the exact store: the values row by row in a field of the given width.</summary>
        public static void PlaceExact(JpxTileComponent tc, int r, int orientation, int width, double[] values)
        {
            var stride = tc.Resolutions[tc.Keep].Width;
            var ox = r > 0 && (orientation & 1) != 0 ? tc.Resolutions[r - 1].Width : 0;
            var oy = r > 0 && (orientation & 2) != 0 ? tc.Resolutions[r - 1].Height : 0;
            for (var k = 0; k < values.Length; k++) tc.Exact![(oy + k / width) * stride + ox + k % width] = values[k];
        }

        /// <summary>
        /// A tile-component x0 ≤ x &lt; x1, y0 ≤ y &lt; y1 built by hand: resolutions after B-14, the buffer of resolution <paramref name="keep"/>
        /// (sub-bands in quadrants as the decoder places them: low-pass left and top, high-pass right and bottom).
        /// </summary>
        public static JpxTileComponent TileComponent(int x0, int y0, int x1, int y1, int levels, bool reversible, int? keep = null)
        {
            var tc = new JpxTileComponent
            {
                X0 = x0, Y0 = y0, X1 = x1, Y1 = y1,
                Coding = new JpxCoding { Levels = levels, Reversible = reversible },
                Keep = keep ?? levels,
                Resolutions = new JpxResolution[levels + 1],
            };
            for (var r = 0; r <= levels; r++)
            {
                var scale = 1L << (levels - r);
                tc.Resolutions[r] = new JpxResolution
                {
                    X0 = (int)JpxTile.CeilDiv(x0, scale), Y0 = (int)JpxTile.CeilDiv(y0, scale),
                    X1 = (int)JpxTile.CeilDiv(x1, scale), Y1 = (int)JpxTile.CeilDiv(y1, scale),
                };
            }
            var top = tc.Resolutions[tc.Keep];
            tc.Data = new float[top.Width * top.Height];
            return tc;
        }

        /// <summary>Width and height of sub-band <paramref name="orientation"/> of resolution 1 (B-15 with nb = NL).</summary>
        public static (int Width, int Height) BandSize(JpxTileComponent tc, int orientation)
        {
            var full = 1L << tc.Coding.Levels;
            var half = full / 2;
            long xo = orientation & 1, yo = orientation >> 1;
            return ((int)(JpxTile.CeilDiv(tc.X1 - half * xo, full) - JpxTile.CeilDiv(tc.X0 - half * xo, full)),
                (int)(JpxTile.CeilDiv(tc.Y1 - half * yo, full) - JpxTile.CeilDiv(tc.Y0 - half * yo, full)));
        }

        /// <summary>Writes a sub-band of resolution <paramref name="r"/> into its quadrant of the buffer (resolution 0: the LL band).</summary>
        public static void Place(JpxTileComponent tc, int r, int orientation, float[,] values)
        {
            var stride = tc.Resolutions[tc.Keep].Width;
            var ox = r > 0 && (orientation & 1) != 0 ? tc.Resolutions[r - 1].Width : 0;
            var oy = r > 0 && (orientation & 2) != 0 ? tc.Resolutions[r - 1].Height : 0;
            for (var j = 0; j < values.GetLength(0); j++)
            for (var i = 0; i < values.GetLength(1); i++)
                tc.Data[(oy + j) * stride + ox + i] = values[j, i];
        }

        /// <summary>The four sub-bands of the 2D_SR examples: LL = [[10, 20], [30, 40]], HL = [[1, −1], [2, 0]], LH = [[0, 3], [−2, 1]], HH = [[1, 0], [0, −1]].</summary>
        public static void PlaceExample(JpxTileComponent tc)
        {
            Place(tc, 0, 0, Grid(2, [10, 20, 30, 40]));
            Place(tc, 1, 1, Grid(2, [1, -1, 2, 0]));
            Place(tc, 1, 2, Grid(2, [0, 3, -2, 1]));
            Place(tc, 1, 3, Grid(2, [1, 0, 0, -1]));
        }

        /// <summary>Values row by row into a field of the given width.</summary>
        public static float[,] Grid(int width, float[] values)
        {
            var grid = new float[values.Length / width, width];
            for (var k = 0; k < values.Length; k++) grid[k / width, k % width] = values[k];
            return grid;
        }

        public static float[,] ToFloat(double[,] values)
        {
            var grid = new float[values.GetLength(0), values.GetLength(1)];
            for (var j = 0; j < values.GetLength(0); j++)
            for (var i = 0; i < values.GetLength(1); i++) grid[j, i] = (float)values[j, i];
            return grid;
        }

        /// <summary>Equal within the precision of single-precision lifting (exact for the integers of 5-3).</summary>
        public static void Close(double expected, float actual, string where)
        {
            Assert.False(double.IsNaN(expected), $"{where}: Referenz undefiniert");
            Assert.True(Math.Abs(expected - actual) <= 1e-4 * Math.Max(1, Math.Abs(expected)), $"{where}: {actual} statt {expected}");
        }

        /// <summary>
        /// A codestream of one tile over x0 ≤ x &lt; x1, y0 ≤ y &lt; y1 with empty packets: SIZ (8 bits, horizontal subsampling per component),
        /// COD (LRCP, one layer, <paramref name="levels"/>, code-blocks 64 × 64, <paramref name="transform"/>), optional COC per component
        /// (levels, transform), QCD without quantisation.
        /// </summary>
        public static byte[] Codestream(int x0, int x1, int y0, int y1, byte[] dx, byte levels, byte transform = 1,
            (int Component, byte Levels, byte Transform)[]? coc = null)
        {
            var b = new List<byte> { 0xFF, 0x4F, 0xFF, 0x51 };
            void U16(int v) => b.AddRange([(byte)(v >> 8), (byte)v]);
            void U32(int v) => b.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
            U16(38 + 3 * dx.Length);
            U16(0);
            U32(x1); U32(y1); U32(x0); U32(y0); U32(x1); U32(y1); U32(0); U32(0);
            U16(dx.Length);
            foreach (var d in dx) b.AddRange([0x07, d, 0x01]);
            b.AddRange([0xFF, 0x52, 0x00, 0x0C, 0x00, 0x00, 0x00, 0x01, 0x00, levels, 0x04, 0x04, 0x00, transform]);
            foreach (var (component, cocLevels, cocTransform) in coc ?? Array.Empty<(int, byte, byte)>())
                b.AddRange([0xFF, 0x53, 0x00, 0x09, (byte)component, 0x00, cocLevels, 0x04, 0x04, 0x00, cocTransform]);
            b.AddRange([0xFF, 0x5C, 0x00, 0x04, 0x40, 0x40]);
            const int body = 64;   // one byte per empty packet, more than enough
            b.AddRange([0xFF, 0x90, 0x00, 0x0A, 0x00, 0x00]);
            U32(14 + body);
            b.AddRange([0x00, 0x01, 0xFF, 0x93]);
            b.AddRange(new byte[body]);
            b.AddRange([0xFF, 0xD9]);
            return [.. b];
        }

        /// <summary>Parses the codestream and decodes its single tile (structure from Setup, packets, blocks, inverse transform).</summary>
        public static JpxTile DecodeTile(byte[] codestream)
        {
            var cs = JpxCodestream.Parse(codestream, 0, codestream.Length);
            var tile = new JpxTile(cs, 0, cs.Tiles[0]!, 0, new JpxDecodeState(long.MaxValue, long.MaxValue, long.MaxValue));
            tile.Decode();
            return tile;
        }
    }
}
