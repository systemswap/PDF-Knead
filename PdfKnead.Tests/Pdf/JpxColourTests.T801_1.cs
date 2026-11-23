using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// T.801 (23.11.2026): the marker segments of the multiple component transformation and the
/// non-linearity (A.3.6–A.3.10: CBD, MCT, MCC, MCO, NLT) and Annex M up to M.4 — each syntax rule, table value and precedence its own test.
/// Most tests decode into components of 12 bits (CBD) and compare the raw planes, so every value is checked exactly against the spatially
/// reconstructed components of the codestream (<see cref="Part2Segments.Raw"/>), without the clamping of 8 bits.
/// </summary>
public sealed partial class JpxColourTests
{
    /// <summary>Builders for the marker segments of ISO/IEC 15444-2, A.3.6–A.3.9, on the 40 × 27 RGB codestream (three components of 8 bits).</summary>
    private static class Part2Segments
    {
        /// <summary>The offset that keeps the transformed values of the tests positive in 12 bits.</summary>
        public const int Level = 1024;

        public static byte[] Cbd(int ncbd, params byte[] depths) => Marker(0xFF78, U16(ncbd), depths);

        /// <summary>An MCT segment in one part (Zmct 0, Ymct 0) with <paramref name="imct"/> as given, the values as 32-bit floating point.</summary>
        public static byte[] Mct(int imct, params float[] values) => Marker(0xFF74, U16(0), U16(imct), U16(0), F32(values));

        /// <summary>A decorrelation array (array type 1, 32-bit floating point), M rows of N coefficients.</summary>
        public static byte[] Matrix(int index, params float[] values) => Mct(0x0900 | index, values);

        /// <summary>An offset array (array type 2, 32-bit floating point), one value per output.</summary>
        public static byte[] Offsets(int index, params float[] values) => Mct(0x0A00 | index, values);

        /// <summary>The values of an array in element type <paramref name="type"/> (Table A.33: 16- and 32-bit integers, 32- and 64-bit floating point).</summary>
        public static byte[] Elements(int type, params float[] values) => [.. values.SelectMany(v =>
        {
            var b = new byte[type switch { 0 => 2, 3 => 8, _ => 4 }];
            switch (type)
            {
                case 0: BinaryPrimitives.WriteInt16BigEndian(b, (short)v); break;
                case 1: BinaryPrimitives.WriteInt32BigEndian(b, (int)v); break;
                case 2: BinaryPrimitives.WriteSingleBigEndian(b, v); break;
                default: BinaryPrimitives.WriteDoubleBigEndian(b, v); break;
            }
            return b;
        })];

        /// <summary>One component collection of an MCC segment: Xmcc, Nmcc and Cmcc, Mmcc and Wmcc, Tmcc (offset index, matrix index), Omcc.</summary>
        public static byte[] Collection(int[] inputs, int[] outputs, int matrix, int offsets, int kind = 1, bool wideInputs = false, bool wideOutputs = false,
            byte[]? omcc = null) =>
            [(byte)kind, .. Indices(inputs, wideInputs), .. Indices(outputs, wideOutputs), 0, (byte)offsets, (byte)matrix, .. omcc ?? []];

        private static byte[] Indices(int[] list, bool wide) =>
            [.. U16(list.Length | (wide ? 0x8000 : 0)), .. list.SelectMany(i => wide ? U16(i) : new[] { (byte)i })];

        /// <summary>An MCC segment in one part (Zmcc 0, Ymcc 0) with Qmcc the number of collections given.</summary>
        public static byte[] Mcc(int index, params byte[][] collections) => Mcc(index, collections.Length, collections);

        /// <summary>An MCC segment in one part whose Qmcc is <paramref name="count"/>, whatever collections follow.</summary>
        public static byte[] Mcc(int index, int count, params byte[][] collections) =>
            Marker(0xFF75, [.. U16(0), (byte)index, .. U16(0), .. U16(count), .. collections.SelectMany(c => c)]);

        public static byte[] Mco(params int[] stages) => Marker(0xFF77, [(byte)stages.Length, .. stages.Select(s => (byte)s)]);

        /// <summary>The RGB codestream with the multiple component capability in Rsiz, the array-based transformation in COD (2) and the segments.</summary>
        public static byte[] File(params byte[][] segments) => Part2(Rgb.Value, 0x8100, 2, segments);

        /// <summary>The decoded components as raw values in their bit depth (up to 16 bits).</summary>
        public static int[][] Planes(byte[] file) => [.. JpxDecoder.DecodePlanes(file, long.MaxValue).Planes.Select(p => p.Select(v => (int)v).ToArray())];

        /// <summary>
        /// The spatially reconstructed components of the codestream as integers: no transformation stage (Nmco 0) with a CBD of three signed
        /// components of 12 bits — the level shift of SIZ (128) and the half range of the signed output (2048) taken off again. No collection and
        /// no array is involved, so the tests of the transformation compare against values the transformation does not make itself.
        /// </summary>
        public static int[][] Raw()
        {
            Assert.Single(JpxCodestream.Parse(Rgb.Value, 0, Rgb.Value.Length).Tiles);
            return [.. Planes(File(Cbd(0x8003, 0x8B), Mco())).Select(p => p.Select(v => v - 128 - 2048).ToArray())];
        }

        /// <summary>a · component + b for every sample.</summary>
        public static int[] Of(int[] component, int b, int a = 1) => [.. component.Select(v => a * v + b)];

        public static void Equal(int[][] expected, int[][] planes)
        {
            Assert.Equal(expected.Length, planes.Length);
            for (var c = 0; c < expected.Length; c++) Assert.Equal(expected[c], planes[c]);
        }

        /// <summary>The segments at the start of the first tile-part header, its Psot grown by their length.</summary>
        public static byte[] InFirstTilePart(byte[] cs, params byte[][] segments)
        {
            var pos = 2;
            while (BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(pos)) != 0xFF90) pos += 2 + BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(pos + 2));
            var add = segments.SelectMany(s => s).ToArray();
            var psot = BinaryPrimitives.ReadUInt32BigEndian(cs.AsSpan(pos + 6));
            byte[] file = [.. cs[..(pos + 12)], .. add, .. cs[(pos + 12)..]];
            if (psot != 0) BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(pos + 6), psot + (uint)add.Length);
            return file;
        }

        /// <summary>A second tile-part of tile 0 (TPsot 1) with only the segments and an empty body, before EOC.</summary>
        public static byte[] InSecondTilePart(byte[] cs, params byte[][] segments)
        {
            Assert.Equal(0xFFD9, BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(cs.Length - 2)));
            var add = segments.SelectMany(s => s).ToArray();
            byte[] part = [0xFF, 0x90, 0, 10, 0, 0, .. U32((uint)(12 + add.Length + 2)), 1, 0, .. add, 0xFF, 0x93];
            return [.. cs[..^2], .. part, 0xFF, 0xD9];
        }

        /// <summary>Decorrelation that swaps components 0 and 2 (rows: output 0 from input 2, output 1 from 1, output 2 from 0).</summary>
        public static readonly float[] Swap = [0, 0, 1, 0, 1, 0, 1, 0, 0];

        public static readonly float[] Identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];
    }

    /// <summary>The decoded value of the gamma curve of ITU-R BT.709 (the parameters of <see cref="T801Gamma"/>), as 8 bits.</summary>
    private static byte T801Inverse709(byte v)
    {
        var y = v / 255.0;
        var z = y <= 0.081 ? y / 4.5 : Math.Pow((y + 0.099) / 1.099, 1 / 0.45);
        return (byte)Math.Clamp(Math.Round(z * 255), 0, 255);
    }

    /// <summary>An NLT of the gamma style (Tnlt 1) for <paramref name="component"/> (65 535: all), 8 bits unsigned, the curve of ITU-R BT.709.</summary>
    private static byte[] T801Gamma(int component) =>
        Marker(0xFF76, U16(component), [0x07], [1], Fixed(0.45), Fixed(4.5), Fixed(0.081), Fixed(1.099), Fixed(0.099));

    /// <summary>An NLT that only names its type (Tnlt 0 or 3, or a reserved one) for <paramref name="component"/>, 8 bits unsigned.</summary>
    private static byte[] T801Nlt(int component, int type) => Marker(0xFF76, U16(component), [0x07], [(byte)type]);

    /// <summary>An NLT of the LUT style for <paramref name="component"/>: Npoints + 1 values of <paramref name="ptval"/> bits over Dmin to Dmax.</summary>
    private static byte[] T801Lut(int component, uint dmin, uint dmax, int ptval, params ulong[] table)
    {
        var size = ptval <= 8 ? 1 : ptval <= 16 ? 2 : 4;
        var values = table.SelectMany(v => Enumerable.Range(0, size).Select(b => (byte)(v >> (8 * (size - 1 - b))))).ToArray();
        return Marker(0xFF76, U16(component), [0x07], [2], U16(table.Length - 1), U32(dmin), U32(dmax), [(byte)ptval], values);
    }

    // ---- A.3.6 CBD

    /// <summary>
    /// T.801 A.3.6 (Tables A.29 to A.31): a CBD (marker 0xFF78) of two components of 12 bits beside a SIZ of three components of 8 bits — the
    /// decoder hands out the two reconstructed components CBD names, in its bit depth, not those of SIZ; the same with the depth given once for
    /// all (Ncbd with its top bit). The collection makes output 0 from codestream component 2 and output 1 from component 0.
    /// </summary>
    [TestPdfsFact]
    public void Cbd_OtherNumberAndDepthThanSiz_TheReconstructedComponents()
    {
        var raw = Part2Segments.Raw();
        foreach (var cbd in new[] { Part2Segments.Cbd(0x0002, 0x0B, 0x0B), Part2Segments.Cbd(0x8002, 0x0B) })
        {
            var file = Part2Segments.File(cbd, Part2Segments.Matrix(1, 0, 0, 1, 1, 0, 0), Part2Segments.Offsets(1, Part2Segments.Level, Part2Segments.Level),
                Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 1], 1, 1)), Part2Segments.Mco(0));
            var info = JpxCodestream.Parse(file, 0, file.Length);
            Assert.Equal(3, info.Components.Length);
            Assert.Equal(new[] { (12, false), (12, false) }, info.Output.Select(c => (c.Precision, c.Signed)));
            Part2Segments.Equal([Part2Segments.Of(raw[2], Part2Segments.Level), Part2Segments.Of(raw[0], Part2Segments.Level)], Part2Segments.Planes(file));
            Assert.Equal(2, JpxDecoder.Decode(file, long.MaxValue).Channels);
        }
    }

    /// <summary>
    /// T.801 A.3.6: CBD belongs in the main header only — a CBD of two components of 12 bits in the first tile-part header changes nothing; the
    /// output keeps the components of SIZ, as with a CBD equal to SIZ in the main header.
    /// </summary>
    [TestPdfsFact]
    public void Cbd_InTheTilePartHeader_PassedOver()
    {
        byte[][] stage = [Part2Segments.Offsets(1, 128, 128, 128), Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 0, 1)), Part2Segments.Mco(0)];
        var asSiz = Part2Segments.File([Part2Segments.Cbd(0x8003, 0x07), .. stage]);
        var inTile = Part2Segments.InFirstTilePart(Part2Segments.File(stage), Part2Segments.Cbd(0x0002, 0x0B, 0x0B));
        var info = JpxCodestream.Parse(inTile, 0, inTile.Length);
        Assert.Equal(info.Components, info.Output);
        Assert.Equal(JpxDecoder.Decode(asSiz, long.MaxValue).Samples, JpxDecoder.Decode(inTile, long.MaxValue).Samples);
    }

    /// <summary>
    /// T.801 A.3.6: at most one CBD in the main header — of two (a duty of the writer) the reader takes the last: three components of 8 bits
    /// after two of 12 bits give three of 8 bits, the other order two of 12 bits.
    /// </summary>
    [TestPdfsFact]
    public void Cbd_TwiceInTheMainHeader_TheLastCounts()
    {
        byte[][] stage = [Part2Segments.Offsets(1, 128, 128, 128), Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 0, 1)), Part2Segments.Mco(0)];
        var two = Part2Segments.Cbd(0x0002, 0x0B, 0x0B);
        var three = Part2Segments.Cbd(0x8003, 0x07);
        var last3 = Part2Segments.File([two, three, .. stage]);
        var last2 = Part2Segments.File([three, two, .. stage]);
        Assert.Equal(new[] { (8, false), (8, false), (8, false) }, JpxCodestream.Parse(last3, 0, last3.Length).Output.Select(c => (c.Precision, c.Signed)));
        Assert.Equal(new[] { (12, false), (12, false) }, JpxCodestream.Parse(last2, 0, last2.Length).Output.Select(c => (c.Precision, c.Signed)));
    }

    /// <summary>
    /// T.801 A.3.6: CBD is required with the multiple component transformation (a duty of the writer); a codestream without it is read with the
    /// components of SIZ — the same image as with a CBD equal to SIZ.
    /// </summary>
    [TestPdfsFact]
    public void Cbd_MissingWithTheTransformation_TheComponentsOfSiz()
    {
        byte[][] stage = [Part2Segments.Matrix(1, Part2Segments.Swap), Part2Segments.Offsets(1, 128, 128, 128), Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 1, 1)), Part2Segments.Mco(0)];
        var without = Part2Segments.File(stage);
        var info = JpxCodestream.Parse(without, 0, without.Length);
        Assert.Equal(info.Components, info.Reconstructed);
        Assert.Equal(info.Components, info.Output);
        Assert.Equal(JpxDecoder.Decode(Part2Segments.File([Part2Segments.Cbd(0x8003, 0x07), .. stage]), long.MaxValue).Samples, JpxDecoder.Decode(without, long.MaxValue).Samples);
    }

    /// <summary>
    /// T.801 A.3.6, Equation A-5: Lcbd is 5 when one depth stands for all components, otherwise 4 + Ncbd — a segment of three components with only
    /// two depths is damaged (no CBD), with three it gives them in their order; one depth with the top bit of Ncbd stands for all three.
    /// </summary>
    [Fact]
    public void Cbd_LengthAfterEquationA5()
    {
        Assert.Null(JpxPart2.ReadCbd([0x00, 0x03, 0x07, 0x0B], 0, 4));
        Assert.Equal(new[] { (8, false), (12, false), (16, false) }, JpxPart2.ReadCbd([0x00, 0x03, 0x07, 0x0B, 0x0F], 0, 5)!.Select(d => (d.Bits, d.Signed)));
        Assert.Equal(new[] { (12, false), (12, false), (12, false) }, JpxPart2.ReadCbd([0x80, 0x03, 0x0B], 0, 3)!.Select(d => (d.Bits, d.Signed)));
    }

    /// <summary>
    /// T.801 A.3.6, Table A.30: the low 15 bits of Ncbd are the number of components, 1 to 16 384, with the top bit one depth for all; 0 and
    /// numbers above 16 384 are reserved — the segment counts as damaged. Depths given per component keep their own values.
    /// </summary>
    [Theory]
    [InlineData(0x8000, 1, null)]
    [InlineData(0xC001, 1, null)]
    [InlineData(0x4001, 16385, null)]
    [InlineData(0x4000, 16384, 16384)]
    [InlineData(0xC000, 1, 16384)]
    [InlineData(0x8001, 1, 1)]
    public void Cbd_NumberOfComponents_TableA30(int ncbd, int depths, int? components)
    {
        byte[] segment = [.. U16(ncbd), .. Enumerable.Repeat((byte)0x07, depths)];
        Assert.Equal(components, JpxPart2.ReadCbd(segment, 0, segment.Length)?.Length);
        Assert.Equal(new[] { 8, 12 }, JpxPart2.ReadCbd([0x00, 0x02, 0x07, 0x0B], 0, 4)!.Select(d => d.Bits));
    }

    /// <summary>
    /// T.801 A.3.6, Table A.31: the top bit of BDcbd makes the component signed. Signed components are shown around the middle of their range:
    /// a signed CBD of 12 bits without offsets gives the same planes as an unsigned one with the offsets 2048.
    /// </summary>
    [TestPdfsFact]
    public void Cbd_SignBit_SignedComponents()
    {
        Assert.Equal((8, true), JpxPart2.ReadCbd([0x80, 1, 0x87], 0, 3)!.Select(d => (d.Bits, d.Signed)).Single());
        Assert.Equal((8, false), JpxPart2.ReadCbd([0x80, 1, 0x07], 0, 3)!.Select(d => (d.Bits, d.Signed)).Single());
        var raw = Part2Segments.Raw();
        var signed = Part2Segments.File(Part2Segments.Cbd(0x8003, 0x8B), Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 0, 0)), Part2Segments.Mco(0));
        Assert.All(JpxCodestream.Parse(signed, 0, signed.Length).Output, c => Assert.True(c.Signed));
        Part2Segments.Equal([.. raw.Select(r => Part2Segments.Of(r, 2048))], Part2Segments.Planes(signed));
        var unsigned = Part2Segments.File(Part2Segments.Cbd(0x8003, 0x0B), Part2Segments.Offsets(1, 2048, 2048, 2048),
            Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 0, 1)), Part2Segments.Mco(0));
        Part2Segments.Equal(Part2Segments.Planes(unsigned), Part2Segments.Planes(signed));
    }

    // ---- A.3.7 MCT

    /// <summary>
    /// T.801 A.3.7: an MCT of the first tile-part header takes the place of the main header's for the tile when index and array type (the low ten
    /// bits of Imct) are the same — also in another element type; one of another index leaves the main header's in force, one of the offset type
    /// replaces only the offsets. An MCT in a later tile-part of the tile is passed over.
    /// </summary>
    [TestPdfsFact]
    public void Mct_OfTheFirstTilePart_ReplacesTheMainHeaderOneOfTheSameIndexAndType()
    {
        var raw = Part2Segments.Raw();
        const int l = Part2Segments.Level;
        var main = Part2Segments.File(Part2Segments.Cbd(0x8003, 0x0B), Part2Segments.Matrix(1, Part2Segments.Swap), Part2Segments.Offsets(1, l, l, l),
            Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 1, 1)), Part2Segments.Mco(0));
        int[][] swapped = [Part2Segments.Of(raw[2], l), Part2Segments.Of(raw[1], l), Part2Segments.Of(raw[0], l)];
        int[][] kept = [.. raw.Select(r => Part2Segments.Of(r, l))];
        Part2Segments.Equal(swapped, Part2Segments.Planes(main));
        Part2Segments.Equal(kept, Part2Segments.Planes(Part2Segments.InFirstTilePart(main, Part2Segments.Matrix(1, Part2Segments.Identity))));
        var float64 = Marker(0xFF74, U16(0), U16(0x0D01), U16(0), Part2Segments.Elements(3, Part2Segments.Identity));
        Part2Segments.Equal(kept, Part2Segments.Planes(Part2Segments.InFirstTilePart(main, float64)));
        Part2Segments.Equal(swapped, Part2Segments.Planes(Part2Segments.InFirstTilePart(main, Part2Segments.Matrix(2, Part2Segments.Identity))));
        Part2Segments.Equal([Part2Segments.Of(raw[2], 1000), Part2Segments.Of(raw[1], 1000), Part2Segments.Of(raw[0], 1000)],
            Part2Segments.Planes(Part2Segments.InFirstTilePart(main, Part2Segments.Offsets(1, 1000, 1000, 1000))));
        Part2Segments.Equal(swapped, Part2Segments.Planes(Part2Segments.InSecondTilePart(main, Part2Segments.Matrix(1, Part2Segments.Identity))));
    }

    /// <summary>
    /// T.801 A.3.7, Table A.32: Zmct and Imct of 16 bits, Ymct only with Zmct 0 — an MCT of only Zmct (Lmct 4) and one with Zmct 0 but without
    /// Ymct are too short and passed over; the collection that names the array then finds none.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("Lmct 4")]
    [InlineData("without Ymct")]
    public void Mct_ShorterThanItsFields_PassedOver(string form)
    {
        var shortMct = form == "Lmct 4" ? Marker(0xFF74, U16(0)) : Marker(0xFF74, U16(0), U16(0x0901));
        var file = Part2Segments.File(Part2Segments.Cbd(0x8003, 0x0B), shortMct, Part2Segments.Offsets(1, 1024, 1024, 1024),
            Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 1, 1)), Part2Segments.Mco(0));
        Assert.Contains("MCT fehlt", Assert.Throws<JpxException>(() => JpxDecoder.Decode(file, long.MaxValue)).Message);
    }

    /// <summary>
    /// T.801 A.3.7, Table A.33: the element type of Imct (bits 10–11) — 16- and 32-bit signed integers, 32- and 64-bit floating point. A matrix
    /// of whole numbers (with a negative coefficient) and its offsets in each of the four types give the same planes, the values worked out
    /// here from the components: output 0 = input 2, output 1 = 2048 − input 1, output 2 = input 0 + input 2 (offsets 1024, 2048, 1024).
    /// </summary>
    [TestPdfsTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Mct_ElementTypes_SameValues(int type)
    {
        var raw = Part2Segments.Raw();
        var matrix = Marker(0xFF74, U16(0), U16(0x0101 | type << 10), U16(0), Part2Segments.Elements(type, 0, 0, 1, 0, -1, 0, 1, 0, 1));
        var offsets = Marker(0xFF74, U16(0), U16(0x0201 | type << 10), U16(0), Part2Segments.Elements(type, 1024, 2048, 1024));
        var file = Part2Segments.File(Part2Segments.Cbd(0x8003, 0x0B), matrix, offsets, Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 1, 1)), Part2Segments.Mco(0));
        Part2Segments.Equal([Part2Segments.Of(raw[2], 1024), Part2Segments.Of(raw[1], 2048, -1), [.. raw[0].Zip(raw[2], (a, b) => a + b + 1024)]], Part2Segments.Planes(file));
    }

    /// <summary>
    /// T.801 A.3.7, Table A.33: array type 3 (bits 8–9 of Imct) is reserved — an MCT of that type under the index the collection names is no
    /// decorrelation array: the matrix counts as missing.
    /// </summary>
    [TestPdfsFact]
    public void Mct_ReservedArrayType_NotTaken()
    {
        var file = Part2Segments.File(Part2Segments.Cbd(0x8003, 0x0B), Part2Segments.Mct(0x0B01, Part2Segments.Identity), Part2Segments.Offsets(1, 1024, 1024, 1024),
            Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 1, 1)), Part2Segments.Mco(0));
        Assert.Contains("MCT fehlt", Assert.Throws<JpxException>(() => JpxDecoder.Decode(file, long.MaxValue)).Message);
    }

    // ---- A.3.8 MCC

    /// <summary>
    /// T.801 A.3.8: Qmcc collections in one MCC, all applied — collection 1 swaps components 0 and 2 (a 2 × 2 matrix), collection 2 passes
    /// component 1 through (identity); the same bytes with Qmcc 1 apply only the first, and component 1 stays zero.
    /// </summary>
    [TestPdfsFact]
    public void Mcc_SeveralCollections_AllApplied()
    {
        var raw = Part2Segments.Raw();
        const int l = Part2Segments.Level;
        var first = Part2Segments.Collection([0, 2], [0, 2], 1, 1);
        var second = Part2Segments.Collection([1], [1], 0, 2);
        byte[] File(int count) => Part2Segments.File(Part2Segments.Cbd(0x8003, 0x0B), Part2Segments.Matrix(1, 0, 1, 1, 0), Part2Segments.Offsets(1, l, l), Part2Segments.Offsets(2, l),
            Part2Segments.Mcc(0, count, first, second), Part2Segments.Mco(0));
        Part2Segments.Equal([Part2Segments.Of(raw[2], l), Part2Segments.Of(raw[1], l), Part2Segments.Of(raw[0], l)], Part2Segments.Planes(File(2)));
        Part2Segments.Equal([Part2Segments.Of(raw[2], l), new int[raw[1].Length], Part2Segments.Of(raw[0], l)], Part2Segments.Planes(File(1)));
    }

    /// <summary>
    /// T.801 A.3.8: an MCC of the first tile-part header with the same Imcc takes the place of the main header's for the tile (here its collection
    /// names the identity of an MCT of its own header); one with another Imcc leaves the main header's in force; an MCT of the first tile-part
    /// header comes before the main header's for the collection of the main header.
    /// </summary>
    [TestPdfsFact]
    public void Mcc_OfTheFirstTilePart_ReplacesTheMainHeaderOneOfTheSameIndex()
    {
        var raw = Part2Segments.Raw();
        const int l = Part2Segments.Level;
        var main = Part2Segments.File(Part2Segments.Cbd(0x8003, 0x0B), Part2Segments.Matrix(1, Part2Segments.Swap), Part2Segments.Offsets(1, l, l, l),
            Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 1, 1)), Part2Segments.Mco(0));
        int[][] swapped = [Part2Segments.Of(raw[2], l), Part2Segments.Of(raw[1], l), Part2Segments.Of(raw[0], l)];
        int[][] kept = [.. raw.Select(r => Part2Segments.Of(r, l))];
        Part2Segments.Equal(kept, Part2Segments.Planes(Part2Segments.InFirstTilePart(main, Part2Segments.Matrix(2, Part2Segments.Identity),
            Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 2, 1)))));
        Part2Segments.Equal(swapped, Part2Segments.Planes(Part2Segments.InFirstTilePart(main, Part2Segments.Matrix(2, Part2Segments.Identity),
            Part2Segments.Mcc(5, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 2, 1)))));
        Part2Segments.Equal(kept, Part2Segments.Planes(Part2Segments.InFirstTilePart(main, Part2Segments.Matrix(1, Part2Segments.Identity))));
    }

    /// <summary>
    /// T.801 A.3.8, Table A.34: Lmcc at least 5 (Zmcc and Imcc) — an MCC of only Zmcc (Lmcc 4) is passed over, the stage finds no MCC. The stage
    /// is 255: read as more than Zmcc, the segment would take its index from beyond itself (the 0xFF of the next marker).
    /// </summary>
    [TestPdfsFact]
    public void Mcc_ShorterThanFiveBytes_PassedOver()
    {
        var file = Part2Segments.File(Part2Segments.Cbd(0x8003, 0x0B), Part2Segments.Offsets(1, 1024, 1024, 1024), Marker(0xFF75, U16(0)), Part2Segments.Mco(255));
        Assert.Contains("MCC fehlt", Assert.Throws<JpxException>(() => JpxDecoder.Decode(file, long.MaxValue)).Message);
    }

    /// <summary>
    /// T.801 A.3.8, Tables A.36 and A.37: Cmcc lists the inputs in the order the transformation takes them — 2, 1, 0 with the identity swaps
    /// components 0 and 2, 0, 1, 2 keeps them; with the top bit of Nmcc (Mmcc) the input (output) indices have 16 bits, with the same result
    /// as 8 bits.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("012", false, false)]
    [InlineData("210", false, false)]
    [InlineData("210", true, false)]
    [InlineData("210", false, true)]
    [InlineData("210", true, true)]
    public void Mcc_IndexLists_OrderAndWidth(string inputs, bool wideInputs, bool wideOutputs)
    {
        var raw = Part2Segments.Raw();
        const int l = Part2Segments.Level;
        int[] order = [.. inputs.Select(ch => ch - '0')];
        var file = Part2Segments.File(Part2Segments.Cbd(0x8003, 0x0B), Part2Segments.Offsets(1, l, l, l),
            Part2Segments.Mcc(0, Part2Segments.Collection(order, [0, 1, 2], 0, 1, wideInputs: wideInputs, wideOutputs: wideOutputs)), Part2Segments.Mco(0));
        Part2Segments.Equal([.. order.Select(i => Part2Segments.Of(raw[i], l))], Part2Segments.Planes(file));
    }

    /// <summary>
    /// T.801 A.3.8, Table A.35: of the kinds of Xmcc only the decorrelation (1) is implemented; the wavelet-based kind (3, with its Omcc) and the
    /// reserved one (2) are refused, not drawn wrong. The refused collection comes first and a decorrelation follows it: its Omcc (0xFFFFFFFF)
    /// is passed over — read as the next collection, it would name 32 767 inputs and the MCC would be incomplete.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(2)]
    [InlineData(3)]
    public void Mcc_KindOtherThanDecorrelation_Refused(int kind)
    {
        var refused = Part2Segments.Collection([0, 1, 2], [0, 1, 2], 0, 1, kind: kind, omcc: kind == 3 ? [0xFF, 0xFF, 0xFF, 0xFF] : null);
        var file = Part2Segments.File(Part2Segments.Cbd(0x8003, 0x0B), Part2Segments.Offsets(1, 1024, 1024, 1024),
            Part2Segments.Mcc(0, refused, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 0, 1)), Part2Segments.Mco(0));
        Assert.Contains("Komponenten-Transformation nicht unterstützt", Assert.Throws<JpxException>(() => JpxDecoder.Decode(file, long.MaxValue)).Message);
    }

    /// <summary>
    /// T.801 A.3.8, Table A.38: Tmcc of an array-based collection — bits 0–7 the index of the decorrelation array, bits 8–15 the index of the offset
    /// array. Matrix under index 1 and offsets 1000, 1024, 1050 under index 2: Tmcc 0x000201 gives the swapped components with their offsets;
    /// the indices the other way round name arrays that do not exist.
    /// </summary>
    [TestPdfsFact]
    public void Mcc_TmccArrayIndices_MatrixAndOffsetsApart()
    {
        var raw = Part2Segments.Raw();
        byte[] File(int matrix, int offsets) => Part2Segments.File(Part2Segments.Cbd(0x8003, 0x0B), Part2Segments.Matrix(1, Part2Segments.Swap), Part2Segments.Offsets(2, 1000, 1024, 1050),
            Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 1, 2], matrix, offsets)), Part2Segments.Mco(0));
        Part2Segments.Equal([Part2Segments.Of(raw[2], 1000), Part2Segments.Of(raw[1], 1024), Part2Segments.Of(raw[0], 1050)], Part2Segments.Planes(File(1, 2)));
        Assert.Contains("MCT fehlt", Assert.Throws<JpxException>(() => JpxDecoder.Decode(File(2, 1), long.MaxValue)).Message);
    }

    // ---- A.3.9 MCO

    /// <summary>
    /// T.801 A.3.9: the MCO gives the order of the stages — MCC 0 swaps components 0 and 2, MCC 1 doubles component 0 and adds the offsets 1024;
    /// the order 0, 1 and the order 1, 0 give different planes, each as worked out here. Of two MCO in the main header (a duty of the writer) the
    /// last counts, and the codestream is reported (A.3.9: at most one per header; Mco_LengthNotAsEquationA6_Reported).
    /// </summary>
    [TestPdfsFact]
    public void Mco_OrderOfTheStages_TheLastSegmentCounts()
    {
        var raw = Part2Segments.Raw();
        const int l = Part2Segments.Level;
        byte[][] stages = [Part2Segments.Cbd(0x8003, 0x0B), Part2Segments.Matrix(1, Part2Segments.Swap), Part2Segments.Matrix(2, 2, 0, 0, 0, 1, 0, 0, 0, 1), Part2Segments.Offsets(3, l, l, l),
            Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 1, 0)), Part2Segments.Mcc(1, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 2, 3))];
        int[][] swapFirst = [Part2Segments.Of(raw[2], l, 2), Part2Segments.Of(raw[1], l), Part2Segments.Of(raw[0], l)];
        int[][] doubleFirst = [Part2Segments.Of(raw[2], l), Part2Segments.Of(raw[1], l), Part2Segments.Of(raw[0], l, 2)];
        Part2Segments.Equal(swapFirst, Part2Segments.Planes(Part2Segments.File([.. stages, Part2Segments.Mco(0, 1)])));
        Part2Segments.Equal(doubleFirst, Part2Segments.Planes(Part2Segments.File([.. stages, Part2Segments.Mco(1, 0)])));
        var two = Part2Segments.File([.. stages, Part2Segments.Mco(0, 1), Part2Segments.Mco(1, 0)]);
        Part2Segments.Equal(doubleFirst, Part2Segments.Planes(two));
        Assert.True(JpxDecoder.DecodePlanes(two, long.MaxValue).Truncated);
        Assert.False(JpxDecoder.DecodePlanes(Part2Segments.File([.. stages, Part2Segments.Mco(1, 0)]), long.MaxValue).Truncated);
    }

    /// <summary>
    /// T.801 A.3.9, Equation A-6: Lmco = 3 + Nmco — an MCO with Nmco 2 but only one Imco (Lmco 4) gives the one stage it has; the byte beyond the
    /// segment (the 0xFF of the next marker) is no stage. The codestream counts as damaged (as with NLT; before the short segment went without
    /// a word).
    /// </summary>
    [TestPdfsFact]
    public void Mco_FewerIndicesThanNmco_TheStagesGiven()
    {
        var raw = Part2Segments.Raw();
        const int l = Part2Segments.Level;
        var file = Part2Segments.File(Part2Segments.Cbd(0x8003, 0x0B), Part2Segments.Matrix(2, 2, 0, 0, 0, 1, 0, 0, 0, 1), Part2Segments.Offsets(3, l, l, l),
            Part2Segments.Mcc(1, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 2, 3)), Marker(0xFF77, [2, 1]));
        Part2Segments.Equal([Part2Segments.Of(raw[0], l, 2), Part2Segments.Of(raw[1], l), Part2Segments.Of(raw[2], l)], Part2Segments.Planes(file));
        Assert.True(JpxDecoder.DecodePlanes(file, long.MaxValue).Truncated);
    }

    // ---- A.3.10 NLT

    /// <summary>
    /// T.801 A.3.10: which NLT a component takes — the tile-part NLT of the component, then the tile-part default (65 535), then the main header
    /// NLT of the component, then the main header default. Main header: gamma for all, the binary complement (no change on unsigned values) for
    /// component 2 → 0 and 1 with gamma, 2 unchanged. A tile-part default (a LUT that inverts) comes before both; a tile-part NLT of component 0
    /// before the tile-part default.
    /// </summary>
    [TestPdfsFact]
    public void Nlt_Precedence_TileComponentTileDefaultMainComponentMainDefault()
    {
        var plain = Plain(Rgb.Value);
        var main = Part2(Rgb.Value, 0x8200, null, T801Gamma(65535), T801Nlt(2, 3));
        Near([.. plain.Select((v, i) => i % 3 == 2 ? v : T801Inverse709(v))], JpxDecoder.Decode(main, long.MaxValue).Samples);
        var invert = T801Lut(65535, 0, 0xFFFFFFFF, 16, 65535, 0);
        Near([.. plain.Select(v => (byte)(255 - v))], JpxDecoder.Decode(Part2Segments.InFirstTilePart(main, invert), long.MaxValue).Samples);
        Near([.. plain.Select((v, i) => i % 3 == 0 ? v : (byte)(255 - v))],
            JpxDecoder.Decode(Part2Segments.InFirstTilePart(main, invert, T801Nlt(0, 3)), long.MaxValue).Samples);
    }

    /// <summary>T.801 A.3.10: NLT only with the capabilities of ISO/IEC 15444-2 in Rsiz — in a codestream with Rsiz 0 the segment means nothing.</summary>
    [TestPdfsFact]
    public void Nlt_WithoutTheCapabilitiesOfPart2_PassedOver()
    {
        Assert.Equal(Plain(Rgb.Value), JpxDecoder.Decode(Part2(Rgb.Value, 0, null, T801Gamma(65535)), long.MaxValue).Samples);
    }

    /// <summary>T.801 A.3.10: NLT in the main header and the first tile-part header only — a gamma NLT in a later tile-part of the tile is passed over.</summary>
    [TestPdfsFact]
    public void Nlt_InALaterTilePart_PassedOver()
    {
        var cs = Part2(Rgb.Value, 0x8200, null);
        Assert.Equal(Plain(Rgb.Value), JpxDecoder.Decode(Part2Segments.InSecondTilePart(cs, T801Gamma(65535)), long.MaxValue).Samples);
        Assert.NotEqual(Plain(Rgb.Value), JpxDecoder.Decode(Part2Segments.InFirstTilePart(cs, T801Gamma(65535)), long.MaxValue).Samples);
    }

    /// <summary>
    /// T.801 A.3.10, Equation A-7 and Table A.46: the table values of a LUT take 1, 2 or 4 bytes for PTval 1–8, 9–16 and 17–32. Three points
    /// 0, 1/3, 1 (whole numbers of 2^PTval − 1) give the same curve in every width, worked out here: linear from 0 to 1/3 below the middle
    /// of the input, from 1/3 to 1 above it.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(32)]
    public void Nlt_LutTableWidthAfterPtval_SameCurve(int ptval)
    {
        var max = (1UL << ptval) - 1;
        var lut = T801Lut(65535, 0, 0xFFFFFFFF, ptval, 0, max / 3, max);
        var plain = Plain(Rgb.Value);
        var expected = plain.Select(v =>
        {
            var y = v / 255.0;
            var z = y < 0.5 ? y / 0.5 / 3 : 1 / 3.0 + 2 / 3.0 * ((y - 0.5) / 0.5);
            return (byte)Math.Round(z * 255);
        }).ToArray();
        Near(expected, JpxDecoder.Decode(Part2(Rgb.Value, 0x8200, null, lut), long.MaxValue).Samples);
    }

    /// <summary>
    /// T.801 A.3.10, Equation A-7 and Table A.46: an NLT shorter than its form (gamma with 14 bytes of parameters, a LUT with a table too short),
    /// with the reserved PTval 0 or 33, or a LUT of one point (Npoints 0), is passed over — component 0 takes the default of the main header
    /// (gamma), as if the segment were not there.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("gamma of 14 bytes")]
    [InlineData("table too short")]
    [InlineData("PTval 0")]
    [InlineData("PTval 33")]
    [InlineData("one point")]
    public void Nlt_DamagedOrReserved_PassedOver(string form)
    {
        var broken = form switch
        {
            "gamma of 14 bytes" => Marker(0xFF76, U16(0), [0x07], [1], Fixed(0.45), Fixed(4.5), Fixed(0.081), Fixed(1.099), Fixed(0.099)[..2]),
            "table too short" => Marker(0xFF76, U16(0), [0x07], [2], U16(2), U32(0), U32(0xFFFFFFFF), [16], U16(0), U16(65535)),
            "PTval 0" => T801Lut(0, 0, 0xFFFFFFFF, 0, 0, 1),
            "PTval 33" => T801Lut(0, 0, 0xFFFFFFFF, 33, 0, 1),
            _ => T801Lut(0, 0, 0xFFFFFFFF, 16, 65535),
        };
        var gammaOnly = JpxDecoder.Decode(Part2(Rgb.Value, 0x8200, null, T801Gamma(65535)), long.MaxValue).Samples;
        Assert.NotEqual(Plain(Rgb.Value), gammaOnly);
        Assert.Equal(gammaOnly, JpxDecoder.Decode(Part2(Rgb.Value, 0x8200, null, T801Gamma(65535), broken), long.MaxValue).Samples);
    }

    /// <summary>
    /// T.801 A.3.10, Table A.46: Dmin and Dmax of a LUT are 32-bit values divided by 2^32 − 1 — 0x40000000 and 0xBFFFFFFF (about 0.25 and 0.75).
    /// A table of two points 0 and 1 then maps the input range between them onto 0 to 1 and clips outside, worked out here.
    /// </summary>
    [TestPdfsFact]
    public void Nlt_LutRange_DminAndDmaxFromTheSegment()
    {
        var lut = T801Lut(65535, 0x40000000, 0xBFFFFFFF, 16, 0, 65535);
        double dmin = 0x40000000 / 4294967295.0, dmax = 0xBFFFFFFF / 4294967295.0;
        var expected = Plain(Rgb.Value).Select(v => (byte)Math.Round((Math.Clamp(v / 255.0, dmin, dmax) - dmin) / (dmax - dmin) * 255)).ToArray();
        Near(expected, JpxDecoder.Decode(Part2(Rgb.Value, 0x8200, null, lut), long.MaxValue).Samples);
    }

    /// <summary>
    /// T.801 A.3.10, Table A.44: Tnlt 0 is no non-linearity — an NLT of component 1 with Tnlt 0 switches off the gamma default of the main header
    /// for it; Tnlt 4 is reserved — such an NLT is passed over, component 1 keeps the default.
    /// </summary>
    [TestPdfsFact]
    public void Nlt_Type0SwitchesOff_ReservedTypePassedOver()
    {
        var plain = Plain(Rgb.Value);
        Near([.. plain.Select((v, i) => i % 3 == 1 ? v : T801Inverse709(v))],
            JpxDecoder.Decode(Part2(Rgb.Value, 0x8200, null, T801Gamma(65535), T801Nlt(1, 0)), long.MaxValue).Samples);
        Assert.Equal(JpxDecoder.Decode(Part2(Rgb.Value, 0x8200, null, T801Gamma(65535)), long.MaxValue).Samples,
            JpxDecoder.Decode(Part2(Rgb.Value, 0x8200, null, T801Gamma(65535), T801Nlt(1, 4)), long.MaxValue).Samples);
    }

    // ---- Annex M

    /// <summary>
    /// T.801 M.2.6 with M.11.23: codestreams in a Multiple Codestream box ('j2cx', with its info box 'j2ci') are not the image — the first
    /// codestream of the top level is, also where the Multiple Codestream box stands before it (which the writer should not do).
    /// </summary>
    [TestPdfsFact]
    public void MultipleCodestreamBox_NotTheImage()
    {
        var bundle = Box("j2cx", Box("j2ci", U32(1), U32(0)), Box("jp2c", Gray.Value));
        var expected = JpxDecoder.Decode(Jpx(Rgb.Value, [Enumerated(16)]), long.MaxValue);
        var before = JpxDecoder.Decode(Jpx(Rgb.Value, [Enumerated(16)], bundle), long.MaxValue);
        Assert.Equal(3, before.Channels);
        Assert.Equal(expected.Samples, before.Samples);
        var after = JpxDecoder.Decode([.. Jpx(Rgb.Value, [Enumerated(16)]), .. bundle], long.MaxValue);
        Assert.Equal(expected.Samples, after.Samples);
    }

    /// <summary>
    /// T.801 M.2.8 with M.11.5.2: channels of different depth (Bits Per Component box, here from a CBD of 16, 8 and 8 bits) — each channel is read
    /// in its own depth: channel 0 as a half precision number (the pattern of 0.5 in every sample, 16 bits), channels 1 and 2 as integers of
    /// 8 bits (green and red of the swapped inverse colour transformation).
    /// </summary>
    [TestPdfsFact]
    public void PixelFormat_ChannelsOfDifferentDepth_EachInItsOwn()
    {
        var half = BitConverter.HalfToUInt16Bits((Half)0.5);
        var cs = Part2(Rgb.Value, 0x8100, 2,
            Marker(0xFF78, U16(0x0003), [0x0F, 0x07, 0x07]),
            Part2Segments.Matrix(1, 0, 0, 0, 1, -0.25f, -0.25f, 1, -0.25f, 0.75f),
            Part2Segments.Offsets(1, half, 128, 128),
            Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 1, 1)), Part2Segments.Mco(0));
        byte[] formats = [0, 3, 0, 0, 0x40, 0x0A, 0, 1, 0, 0, 0, 2, 0, 0];
        byte[] ihdr = [.. U32(27), .. U32(40), .. U16(3), 0xFF, 7, 0, 0];
        var image = JpxDecoder.Decode(Jpx(cs, [Box("ihdr", ihdr), Box("bpcc", [0x0F, 0x07, 0x07]), Enumerated(16), Box("pxfm", formats)]), long.MaxValue);
        var plain = Plain(Rgb.Value);
        var level = (byte)(Math.Round(0.5 * 65535) / 256);
        Near([.. plain.Select((v, i) => i % 3 == 0 ? level : i % 3 == 1 ? v : plain[i - 2])], image.Samples);
    }

    /// <summary>
    /// T.801 M.2.9, Table M.1bis: a JPX file that names a JPEG XR codestream in its compatibility list ('jxrc', 'jxr0' to 'jxr3') and holds one
    /// (here made-up bytes in the style of its header, without the SOC of ISO/IEC 15444-1) shows no image: ISO 32000-2, 7.4.9 takes JPEG 2000
    /// only.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("jxrc")]
    [InlineData("jxr0")]
    [InlineData("jxr1")]
    [InlineData("jxr2")]
    [InlineData("jxr3")]
    public void JpegXrCodestream_InJpx_NoImage(string brand)
    {
        byte[] file =
        [
            .. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]),
            .. Box("ftyp", Encoding.ASCII.GetBytes("jpx "), U32(0), Encoding.ASCII.GetBytes("jpx " + brand)),
            .. Box("jp2h", Enumerated(16)),
            .. Box("jp2c", Encoding.ASCII.GetBytes("WMPHOTO\0"), [0x11, 0x45, 0xC0, 0x71, 0x00, 0x27, 0x00, 0x1A]),
        ];
        Assert.Contains("kein Datenstrom", Assert.Throws<JpxException>(() => JpxDecoder.Decode(file, long.MaxValue)).Message);
    }
}
