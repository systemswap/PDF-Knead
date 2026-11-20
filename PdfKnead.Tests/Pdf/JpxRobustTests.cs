using System.Diagnostics;
using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// JPEG 2000 against crafted data streams: small files must neither hang nor allocate gigabytes.
/// The measurements are taken in the probe run <see cref="ViewerRobustProbe"/>; this class checks only the limits.
/// </summary>
[Collection(nameof(LimitCollection))]   // measures the allocations of all threads, so no tests may run in parallel
[Trait(TestKind.Name, TestKind.Time)]
public sealed class JpxRobustTests
{
    /// <summary>Builds the header of a JPEG 2000 codestream; components with equal subsampling, one tile-part with little data.</summary>
    internal static byte[] Codestream(uint x0, uint x1, uint tile, int components, byte dx, byte levels, ushort layers, byte order, int body = 1)
    {
        var b = new List<byte> { 0xFF, 0x4F, 0xFF, 0x51 };
        void U16(int v) => b.AddRange([(byte)(v >> 8), (byte)v]);
        void U32(uint v) => b.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        U16(38 + 3 * components);
        U16(0);
        U32(x1); U32(1); U32(x0); U32(0); U32(tile); U32(tile); U32(0); U32(0);
        U16(components);
        for (var c = 0; c < components; c++) b.AddRange([0x07, dx, 1]);
        b.AddRange([0xFF, 0x52]);
        U16(12);
        b.AddRange([0x00, order]);
        U16(layers);
        b.AddRange([0x00, levels, 0x04, 0x04, 0x00, 0x01]);
        b.AddRange([0xFF, 0x5C, 0x00, 0x04, 0x40, 0x40]);
        b.AddRange([0xFF, 0x90, 0x00, 0x0A, 0x00, 0x00]);
        U32((uint)(14 + body));
        b.AddRange([0x00, 0x01, 0xFF, 0x93]);
        for (var i = 0; i < body; i++) b.Add(0x80);
        b.AddRange([0xFF, 0xD9]);
        return [.. b];
    }

    /// <summary>Checks that the action finishes in time and with little memory; exceptions of the decoder count as a clean rejection.</summary>
    private static void Bounded(Action action, int seconds = 5, long maxBytes = 1L << 30)
    {
        var before = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        var task = Task.Run(() =>
        {
            try
            {
                action();
            }
            catch (JpxException)
            {
            }
        });
        Assert.True(task.Wait(TimeSpan.FromSeconds(seconds * 4 * TestKind.Factor)), "hängt");
        task.GetAwaiter().GetResult();
        Assert.True(watch.Elapsed.TotalSeconds < seconds * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
        Assert.True(GC.GetTotalAllocatedBytes(true) - before < maxBytes, $"{(GC.GetTotalAllocatedBytes(true) - before) >> 20} MB belegt");
    }

    [Fact]
    public void ManyLayersWithoutPrecincts_DoNotLoop()
    {
        // 2000 components, 33 resolutions almost without precincts, 65 535 layers; without the limit this took over 20 s and 14.7 GB
        Bounded(() => JpxDecoder.Decode(Codestream(1, 2, 2, 2000, 255, 32, 65535, 0), 16_000_000));
        Bounded(() => JpxDecoder.Decode(Codestream(1, 2, 2, 2000, 255, 32, 65535, 1), 16_000_000));
    }

    [Fact]
    public void ManyComponents_WideImage_StayBounded()
    {
        // 8000 components, 1 million pixels wide; one column table per component took over 20 s and 19.9 GB
        Bounded(() => JpxDecoder.Decode(Codestream(0, 1_000_000, 1_000_000, 8000, 255, 0, 1, 0), 16_000_000));
    }

    private static byte[] Box(string type, byte[] body) =>
        [(byte)((body.Length + 8) >> 24), (byte)((body.Length + 8) >> 16), (byte)((body.Length + 8) >> 8), (byte)(body.Length + 8),
         .. System.Text.Encoding.ASCII.GetBytes(type), .. body];

    [Fact]
    public void ChannelCount_IsLimited()
    {
        // More colour channels than a colour space can have, and opacity only without colour: rejected cleanly instead of
        // allocating gigabytes in one array or throwing IndexOutOfRange when drawn as a stencil mask
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(Codestream(0, 64, 64, 40, 1, 0, 1, 0), 16_000_000));
        var onlyAlpha = Box("cdef", [0, 1, 0, 0, 0, 1, 0, 0]);
        var jp2 = (byte[])[.. Box("jp2h", onlyAlpha), .. Box("jp2c", Codestream(0, 64, 64, 1, 1, 0, 1, 0))];
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(jp2, 16_000_000));
        var image = JpxDecoder.Decode(Codestream(0, 64, 64, 4, 1, 0, 1, 0), 16_000_000);   // an ordinary channel count still works
        Assert.Equal(4, image.Channels);
    }

    /// <summary>
    /// 7.4.9: the data of JPXDecode are a codestream or a JP2/JPX file. Bytes before the codestream, or a wrapper whose codestream box has a
    /// damaged type, give no image — as the three reference readers have it; before, the first 4 KB were searched for the start of a codestream,
    /// and PDF-Knead showed an image no other reader showed (S-A A11; docs/FEHLERGEDAECHTNIS.md, No. 939). The same data read as they should.
    /// </summary>
    [Fact]
    public void NoCodestreamBehindOtherBytesOrADamagedWrapper()
    {
        var code = Codestream(0, 64, 64, 1, 1, 0, 1, 0);
        Assert.Equal(1, JpxDecoder.Decode(code, 16_000_000).Channels);
        Assert.Throws<JpxException>(() => JpxDecoder.Decode([.. new byte[16], .. code], 16_000_000));
        var signature = Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]);
        Assert.Equal(1, JpxDecoder.Decode([.. signature, .. Box("jp2c", code)], 16_000_000).Channels);
        Assert.Throws<JpxException>(() => JpxDecoder.Decode([.. signature, .. Box("jp2x", code)], 16_000_000));
    }
}
