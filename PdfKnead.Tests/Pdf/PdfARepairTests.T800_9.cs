using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

public sealed partial class PdfARepairTests
{
    /// <summary>The boxes <see cref="PdfAJpeg2000.Wrap"/> writes, read back (ITU-T T.800 I.5).</summary>
    internal static class WrappedBoxes
    {
        /// <summary>A bare codestream header (the wrapper reads only SIZ): Xsiz, Ysiz, XOsiz, YOsiz and the Ssiz of each component.</summary>
        internal static byte[] Codestream(uint xsiz, uint ysiz, uint xosiz, uint yosiz, params byte[] ssiz)
        {
            var cs = JpxRobustTests.Codestream(0, 64, 64, ssiz.Length, 1, 0, 1, 0);
            BinaryPrimitives.WriteUInt32BigEndian(cs.AsSpan(8), xsiz);
            BinaryPrimitives.WriteUInt32BigEndian(cs.AsSpan(12), ysiz);
            BinaryPrimitives.WriteUInt32BigEndian(cs.AsSpan(16), xosiz);
            BinaryPrimitives.WriteUInt32BigEndian(cs.AsSpan(20), yosiz);
            for (var c = 0; c < ssiz.Length; c++) cs[42 + 3 * c] = ssiz[c];
            return cs;
        }

        /// <summary>The boxes at the top level and inside the JP2 Header box: type, start of the contents, length of the contents.</summary>
        internal static List<(string Type, int Start, int Length)> Children(byte[] d, int start, int end)
        {
            var list = new List<(string, int, int)>();
            for (var at = start; at + 8 <= end;)
            {
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at));
                list.Add((Encoding.ASCII.GetString(d, at + 4, 4), at + 8, length - 8));
                at += length;
            }
            return list;
        }

        /// <summary>The contents of the box of <paramref name="type"/> inside the JP2 Header box.</summary>
        internal static byte[] Header(byte[] file, string type)
        {
            var jp2h = Children(file, 0, file.Length).Single(b => b.Type == "jp2h");
            var box = Children(file, jp2h.Start, jp2h.Start + jp2h.Length).Single(b => b.Type == type);
            return file[box.Start..(box.Start + box.Length)];
        }
    }

    /// <summary>
    /// ITU-T T.800 I.5.3.1 sentences 14 to 16 and 24 to 26: HEIGHT and WIDTH are the height and width of the image area, Ysiz − YOsiz and
    /// Xsiz − XOsiz, as four bytes each, the most significant first — for a codestream of Xsiz = Ysiz = 1024 with XOsiz 3 and YOsiz 2 the
    /// wrapper writes 1022 and 1021. An offset beyond the size (no image area) is not wrapped.
    /// </summary>
    [Fact]
    public void Wrap_ImageHeader_HeightAndWidthOfTheImageArea()
    {
        var wrapped = PdfAJpeg2000.Wrap(WrappedBoxes.Codestream(1024, 1024, 3, 2, 7, 7, 7))!;
        var ihdr = WrappedBoxes.Header(wrapped, "ihdr");
        Assert.Equal(new byte[] { 0, 0, 0x03, 0xFE, 0, 0, 0x03, 0xFD }, ihdr[..8]);
        Assert.Equal((1022u, 1021u), (BinaryPrimitives.ReadUInt32BigEndian(ihdr), BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(4))));
        Assert.Null(PdfAJpeg2000.Wrap(WrappedBoxes.Codestream(1024, 1024, 1025, 0, 7, 7, 7)));
        Assert.Null(PdfAJpeg2000.Wrap(WrappedBoxes.Codestream(1024, 1024, 0, 1025, 7, 7, 7)));
    }

    /// <summary>
    /// ITU-T T.800 I.5.3.1 sentences 68 to 74 (Table I.6): BPC is the depth less one in the lower seven bits, 0 to 37 for 1 to 38 bits — the
    /// wrapper copies Ssiz of 1, 8 and 38 bits (0, 7, 37), and the PDF/A check reads the file it made without a fault; a reserved depth
    /// (Ssiz 38 or 127) and a signed one (the high bit; no enumerated space of JP2 allows it) are not wrapped.
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(7, true)]
    [InlineData(37, true)]
    [InlineData(38, false)]
    [InlineData(127, false)]
    [InlineData(0x87, false)]
    public void Wrap_ImageHeader_BitDepthOfTableI6(int ssiz, bool wrapped)
    {
        var file = PdfAJpeg2000.Wrap(WrappedBoxes.Codestream(64, 1, 0, 0, (byte)ssiz));
        if (!wrapped)
        {
            Assert.Null(file);
            return;
        }
        Assert.NotNull(file);
        Assert.Equal(ssiz, WrappedBoxes.Header(file, "ihdr")[10]);
        Assert.Null(PdfAJpeg2000.Fault(file));
    }

    /// <summary>
    /// ITU-T T.800 I.5.3.3 sentences 62 to 69 and 71 (Table I.10, value 17): one component is wrapped as greyscale — the colour specification
    /// by method 1 with EnumCS 17, precedence and approximation 0, in a JP2 file (brand „jp2 “).
    /// </summary>
    [Fact]
    public void Wrap_OneComponent_EnumeratedGreyscale()
    {
        var file = PdfAJpeg2000.Wrap(WrappedBoxes.Codestream(64, 1, 0, 0, 7))!;
        Assert.Equal("jp2 ", Encoding.ASCII.GetString(file, 20, 4));
        Assert.Equal(new byte[] { 1, 0, 0, 0, 0, 0, 17 }, WrappedBoxes.Header(file, "colr"));
    }

    /// <summary>
    /// ITU-T T.800 I.5.3.6 sentences 31 to 38 (Table I.16): with premultiplied opacity (/SMaskInData 2) the wrapper names the last channel type
    /// 2 for the whole image (association 0), the colours 1 to 3 of type 0 — and with plain opacity (/SMaskInData 1) type 1.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Wrap_Opacity_ChannelTypeOneOrTwo(int opacity)
    {
        var file = PdfAJpeg2000.Wrap(WrappedBoxes.Codestream(64, 1, 0, 0, 7, 7, 7, 7), opacity)!;
        Assert.Equal(new byte[] { 0, 4, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 2, 0, 2, 0, 0, 0, 3, 0, 3, 0, (byte)opacity, 0, 0 }, WrappedBoxes.Header(file, "cdef"));
    }

    /// <summary>
    /// ITU-T T.800 I.5.3.7.1 sentences 8, 9 and 11: cropping or scaling changes the capture resolution — PDF-Knead does neither to JPEG 2000 data
    /// and writes no Resolution box: the JP2 Header box of the wrapper is 45 bytes long, the image header (22) and the colour specification
    /// (15), nothing else.
    /// </summary>
    [Fact]
    public void Wrap_HeaderBox_NoResolutionBox()
    {
        var file = PdfAJpeg2000.Wrap(WrappedBoxes.Codestream(64, 1, 0, 0, 7, 7, 7))!;
        var jp2h = WrappedBoxes.Children(file, 0, file.Length).Single(b => b.Type == "jp2h");
        Assert.Equal(45, jp2h.Length + 8);
        Assert.Equal(["ihdr", "colr"], WrappedBoxes.Children(file, jp2h.Start, jp2h.Start + jp2h.Length).Select(b => b.Type));
        Assert.Equal(-1, file.AsSpan().IndexOf("res "u8));
    }
}
