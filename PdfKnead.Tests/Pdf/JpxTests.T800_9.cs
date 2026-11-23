using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

public sealed partial class JpxTests
{
    /// <summary>Codestreams with an image area that does not start at the origin and subsampled components (ITU-T T.800 B.2, I.5.3.1.1).</summary>
    internal static class ImageArea
    {
        /// <summary>
        /// A codestream of one tile over the whole reference grid (Xsiz × Ysiz, the image area from XOsiz, YOsiz), one component of 8 bits
        /// per (XRsiz, YRsiz), no levels, every packet empty: all samples are the level shift.
        /// </summary>
        internal static byte[] Codestream(int xsiz, int ysiz, int xosiz, int yosiz, params (int Dx, int Dy)[] components)
        {
            var b = new List<byte> { 0xFF, 0x4F, 0xFF, 0x51 };
            void U16(int v) => b.AddRange([(byte)(v >> 8), (byte)v]);
            void U32(int v) => b.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
            U16(38 + 3 * components.Length);
            U16(0);
            U32(xsiz);
            U32(ysiz);
            U32(xosiz);
            U32(yosiz);
            U32(xsiz);
            U32(ysiz);
            U32(0);
            U32(0);
            U16(components.Length);
            foreach (var (dx, dy) in components) b.AddRange([7, (byte)dx, (byte)dy]);
            b.AddRange([0xFF, 0x52, 0, 12, 0, 0, 0, 1, 0, 0, 4, 4, 0, 1]);
            b.AddRange([0xFF, 0x5C, 0, 4, 0x40, 0x48]);
            b.AddRange([0xFF, 0x90, 0, 10, 0, 0]);
            U32(12 + 2 + components.Length);
            b.AddRange([0, 1, 0xFF, 0x93]);
            b.AddRange(new byte[components.Length]);
            b.AddRange([0xFF, 0xD9]);
            return [.. b];
        }
    }

    /// <summary>
    /// ITU-T T.800 I.5.3.1.1 sentence 1 with Equation B-2: where the samples of a component are used on their own, the component has
    /// ⌈Xsiz / XRsiz⌉ − ⌈XOsiz / XRsiz⌉ by ⌈Ysiz / YRsiz⌉ − ⌈YOsiz / YRsiz⌉ samples — for Xsiz 1024, Ysiz 64, XOsiz 3 and YOsiz 2 worked out
    /// here per subsampling: 1021 × 62 without, 510 × 31 for 2, 341 × 21 for 3, mixed and for 4 × 5 as well.
    /// </summary>
    [Theory]
    [InlineData(1, 1, 1021, 62)]
    [InlineData(2, 2, 510, 31)]
    [InlineData(3, 3, 341, 21)]
    [InlineData(2, 3, 510, 21)]
    [InlineData(3, 2, 341, 31)]
    [InlineData(4, 5, 255, 12)]
    public void ComponentSize_AsB2_WithOffsetAndSubsampling(int dx, int dy, int width, int height)
    {
        var planes = JpxDecoder.DecodePlanes(ImageArea.Codestream(1024, 64, 3, 2, (1, 1), (dx, dy)), long.MaxValue);
        Assert.Equal((1021, 62), (planes.PlaneWidth[0], planes.PlaneHeight[0]));
        Assert.Equal((width, height), (planes.PlaneWidth[1], planes.PlaneHeight[1]));
        Assert.Equal(width * height, planes.Planes[1].Length);
        Assert.All(planes.Planes[1], v => Assert.Equal(128, v));
    }
}
