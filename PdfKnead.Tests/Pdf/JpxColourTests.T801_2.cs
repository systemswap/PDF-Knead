using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// T.801 (23.11.2026): the inverse multiple component transformation of Annex J — stages
/// by their MCC index, collections of a stage, input and output lists, null components, offsets, the reconstructed components after CBD.
/// The planes are compared exactly with values worked out here from the spatially reconstructed components (the builders of
/// <c>JpxColourTests.T801_1.cs</c>, <see cref="Part2Segments"/>).
/// </summary>
public sealed partial class JpxColourTests
{
    /// <summary>
    /// T.801 J.2, J.2.1, J.2.2: the stages run in the order of the MCO, each with the MCC its Imco names (not the one of its number), each on the
    /// intermediate components the stage before made. MCC 5 swaps components 0 and 2 and adds 512, MCC 0 doubles component 0: the order 5, 0
    /// and the order 0, 5 give different planes, MCO 5 alone the swap only.
    /// </summary>
    [TestPdfsFact]
    public void Part2_StagesByTheirMccIndex_EachOnTheResultOfThePrevious()
    {
        var raw = Part2Segments.Raw();
        byte[][] stages = [Part2Segments.Cbd(0x8003, 0x0B), Part2Segments.Matrix(1, Part2Segments.Swap), Part2Segments.Matrix(2, 2, 0, 0, 0, 1, 0, 0, 0, 1), Part2Segments.Offsets(4, 512, 512, 512),
            Part2Segments.Mcc(5, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 1, 4)), Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 2, 0))];
        Part2Segments.Equal([Part2Segments.Of(raw[2], 1024, 2), Part2Segments.Of(raw[1], 512), Part2Segments.Of(raw[0], 512)], Part2Segments.Planes(Part2Segments.File([.. stages, Part2Segments.Mco(5, 0)])));
        Part2Segments.Equal([Part2Segments.Of(raw[2], 512), Part2Segments.Of(raw[1], 512), Part2Segments.Of(raw[0], 512, 2)], Part2Segments.Planes(Part2Segments.File([.. stages, Part2Segments.Mco(0, 5)])));
        Part2Segments.Equal([Part2Segments.Of(raw[2], 512), Part2Segments.Of(raw[1], 512), Part2Segments.Of(raw[0], 512)], Part2Segments.Planes(Part2Segments.File([.. stages, Part2Segments.Mco(5)])));
    }

    /// <summary>
    /// T.801 J.2.1: CBD applies also where the transformation is switched off (Nmco 0) — the components of the codestream with the level shift of
    /// SIZ, as the signed components of 10 bits CBD names (shown around the middle of their range, 512 up): the same planes as the identity
    /// with the offsets 128 into those components, and stretched to 8 bits from 10. With the 8 unsigned bits of SIZ they would lie 512 lower.
    /// </summary>
    [TestPdfsFact]
    public void Part2_CbdWithoutStages_TheComponentsInTheDepthOfCbd()
    {
        var off = Part2Segments.File(Part2Segments.Cbd(0x8003, 0x89), Part2Segments.Mco());
        Assert.All(JpxCodestream.Parse(off, 0, off.Length).Output, c => Assert.Equal((10, true), (c.Precision, c.Signed)));
        var identity = Part2Segments.File(Part2Segments.Cbd(0x8003, 0x89), Part2Segments.Offsets(1, 128, 128, 128),
            Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 0, 1)), Part2Segments.Mco(0));
        var planes = Part2Segments.Planes(off);
        Part2Segments.Equal(Part2Segments.Planes(identity), planes);
        var raw = Part2Segments.Raw();
        Part2Segments.Equal([.. raw.Select(r => Part2Segments.Of(r, 128 + 512))], planes);
        Assert.Equal(planes[0].Select(v => (byte)(v >> 2)), JpxDecoder.Decode(off, long.MaxValue).Samples.Where((_, i) => i % 3 == 0));
    }

    /// <summary>
    /// T.801 J.2.1: the number of reconstructed components may differ from the components of the codestream; CBD names them — four of 12 bits from
    /// three (the fourth the sum of components 0 and 2), or two; each worked out here.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(4)]
    [InlineData(2)]
    public void Part2_OutputsOtherThanTheCodestreamComponents_AsCbdNames(int count)
    {
        var raw = Part2Segments.Raw();
        float[] rows = [0, 0, 1, 1, 0, 0, 0, 1, 0, 1, 0, 1];
        int[][] expected = [Part2Segments.Of(raw[2], 1024), Part2Segments.Of(raw[0], 1024), Part2Segments.Of(raw[1], 1024), [.. raw[0].Zip(raw[2], (a, b) => a + b + 1024)]];
        var file = Part2Segments.File(Part2Segments.Cbd(0x8000 | count, 0x0B), Part2Segments.Matrix(1, rows[..(3 * count)]), Part2Segments.Offsets(1, [.. Enumerable.Repeat(1024f, count)]),
            Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [.. Enumerable.Range(0, count)], 1, 1)), Part2Segments.Mco(0));
        var info = JpxCodestream.Parse(file, 0, file.Length);
        Assert.Equal(3, info.Components.Length);
        Assert.Equal(Enumerable.Repeat((12, false), count), info.Output.Select(c => (c.Precision, c.Signed)));
        Part2Segments.Equal(expected[..count], Part2Segments.Planes(file));
        Assert.Equal(count, JpxDecoder.Decode(file, long.MaxValue).Channels);
    }

    /// <summary>
    /// T.801 J.2.2: the collections of a stage do not depend on each other — collection 1 copies component 0 into 1, collection 2 copies component
    /// 1 into 0: the second reads the component 1 the stage began with, not what the first wrote.
    /// </summary>
    [TestPdfsFact]
    public void Part2_CollectionsOfOneStage_ReadTheInputsOfTheStage()
    {
        var raw = Part2Segments.Raw();
        var file = Part2Segments.File(Part2Segments.Cbd(0x8003, 0x0B), Part2Segments.Offsets(1, 1024),
            Part2Segments.Mcc(0, Part2Segments.Collection([0], [1], 0, 1), Part2Segments.Collection([1], [0], 0, 1), Part2Segments.Collection([2], [2], 0, 1)), Part2Segments.Mco(0));
        Part2Segments.Equal([Part2Segments.Of(raw[1], 1024), Part2Segments.Of(raw[0], 1024), Part2Segments.Of(raw[2], 1024)], Part2Segments.Planes(file));
    }

    /// <summary>
    /// T.801 J.2.2, Equation J-1: the intermediate components of the first stage are the Csiz components of the codestream, the last are the Ncbd
    /// reconstructed ones — an input index beyond them (3 of three) is read as zeros; a last stage with more outputs than CBD names gives only
    /// the first Ncbd.
    /// </summary>
    [TestPdfsFact]
    public void Part2_InputBeyondTheComponents_Zeros_OutputsBeyondNcbd_LeftOut()
    {
        var raw = Part2Segments.Raw();
        byte[][] Stage(int[] inputs) => [Part2Segments.Offsets(1, 1024, 1024, 1024), Part2Segments.Mcc(0, Part2Segments.Collection(inputs, [0, 1, 2], 0, 1)), Part2Segments.Mco(0)];
        Part2Segments.Equal([Part2Segments.Of(raw[0], 1024), Part2Segments.Of(raw[1], 1024), [.. Enumerable.Repeat(1024, raw[2].Length)]],
            Part2Segments.Planes(Part2Segments.File([Part2Segments.Cbd(0x8003, 0x0B), .. Stage([0, 1, 3])])));
        Part2Segments.Equal([Part2Segments.Of(raw[0], 1024), Part2Segments.Of(raw[1], 1024)], Part2Segments.Planes(Part2Segments.File([Part2Segments.Cbd(0x8002, 0x0B), .. Stage([0, 1, 2])])));
    }

    /// <summary>
    /// T.801 J.2.3, J.2.3.1, J.2.3.3: input j of a collection is the intermediate component its j-th Cmcc names, output j goes to the one its j-th
    /// Wmcc names — with the identity, the lists 2, 1, 0 on either side swap components 0 and 2, on both sides they cancel out.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("012", "012")]
    [InlineData("210", "012")]
    [InlineData("012", "210")]
    [InlineData("210", "210")]
    public void Part2_InputAndOutputLists_ReorderTheComponents(string inputs, string outputs)
    {
        var raw = Part2Segments.Raw();
        int[] cmcc = [.. inputs.Select(ch => ch - '0')], wmcc = [.. outputs.Select(ch => ch - '0')];
        var file = Part2Segments.File(Part2Segments.Cbd(0x8003, 0x0B), Part2Segments.Offsets(1, 1024, 1024, 1024), Part2Segments.Mcc(0, Part2Segments.Collection(cmcc, wmcc, 0, 1)), Part2Segments.Mco(0));
        var expected = new int[3][];
        for (var j = 0; j < 3; j++) expected[wmcc[j]] = Part2Segments.Of(raw[cmcc[j]], 1024);
        Part2Segments.Equal(expected, Part2Segments.Planes(file));
    }

    /// <summary>
    /// T.801 J.2.3.1: a null component — an intermediate component the stage before left out (here the first stage fills only 0 and 2) — is read
    /// as zeros by the next stage: component 1 comes out as the offset alone.
    /// </summary>
    [TestPdfsFact]
    public void Part2_NullComponentOfTheStageBefore_ReadAsZeros()
    {
        var raw = Part2Segments.Raw();
        var file = Part2Segments.File(Part2Segments.Cbd(0x8003, 0x0B), Part2Segments.Matrix(1, 1, 0, 0, 0, 0, 1), Part2Segments.Offsets(2, 1024, 1024, 1024),
            Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 2], 1, 0)), Part2Segments.Mcc(1, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 0, 2)), Part2Segments.Mco(0, 1));
        Part2Segments.Equal([Part2Segments.Of(raw[0], 1024), [.. Enumerable.Repeat(1024, raw[1].Length)], Part2Segments.Of(raw[2], 1024)], Part2Segments.Planes(file));
    }

    /// <summary>
    /// T.801 J.3.1.1.1: the offset array holds o0 to oM−1, one for each output in its order — 1000, 1024, 1050 with the identity shift each
    /// component by its own.
    /// </summary>
    [TestPdfsFact]
    public void Part2_OffsetsInTheirOrder_OneForEachOutput()
    {
        var raw = Part2Segments.Raw();
        var file = Part2Segments.File(Part2Segments.Cbd(0x8003, 0x0B), Part2Segments.Offsets(1, 1000, 1024, 1050), Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 0, 1)),
            Part2Segments.Mco(0));
        Part2Segments.Equal([Part2Segments.Of(raw[0], 1000), Part2Segments.Of(raw[1], 1024), Part2Segments.Of(raw[2], 1050)], Part2Segments.Planes(file));
    }

    /// <summary>
    /// T.801 J.3.2 with Table A.8: the wavelet-based transformation (4 in the multiple component field of the COD of the main header) is not
    /// needed by JPX baseline and refused while the main header is read — not decoded the ordinary way. The wavelet-based collections (Xmcc 3)
    /// are refused as in <see cref="Mcc_KindOtherThanDecorrelation_Refused"/>.
    /// </summary>
    [TestPdfsFact]
    public void Part2_WaveletBasedTransformationInTheMainHeaderCod_Refused()
    {
        var file = Part2(Rgb.Value, 0x8100, 4, Part2Segments.Cbd(0x8003, 0x0B), Part2Segments.Offsets(1, 1024, 1024, 1024),
            Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 0, 1)), Part2Segments.Mco(0));
        Assert.Contains("Wavelet", Assert.Throws<JpxException>(() => JpxCodestream.Parse(file, 0, file.Length)).Message);
        Assert.Contains("Wavelet", Assert.Throws<JpxException>(() => JpxDecoder.Decode(file, long.MaxValue)).Message);
    }
}
