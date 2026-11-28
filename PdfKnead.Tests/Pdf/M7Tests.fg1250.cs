using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using PdfKnead.Core.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, FG 1250 (reader, security; found by the fuzz run fuzz-m7c): a long loca offset of 2³¹ or more was cast to a negative int, and the glyph
/// readers took a negative position in the font data — the outline stayed empty only because an exception was caught inside the font. The
/// offset is now kept beyond any glyf table, and the glyph is empty by the ordinary checks.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>
    /// OT 16 (loca, long format) with OT 19: the glyph „H“ of Liberation Sans whose loca offset is 0x80000010 has no outline, its neighbour keeps
    /// its own, and no ArgumentOutOfRangeException is thrown (and caught) in the glyph readers while the outline, the glyph data and the contour
    /// corner are read.
    /// </summary>
    [Fact]
    public void Fg1250_LongLocaOffsetAbove2Pow31_GlyphEmpty_NoNegativePosition()
    {
        var font = Ot05SansFile.Value;
        var head = Ot05Table(font, "head");
        Assert.NotEqual(0, BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(50)));   // Liberation Sans uses the long format
        var glyph = Ot05Sans.Value.GlyphForUnicode('H');
        var next = glyph + 1;
        var loca = (byte[])Ot05Table(font, "loca").Clone();
        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(glyph * 4), 0x80000010u);
        var crafted = OpenTypeFont.Parse(Ot05SansWith(("loca", loca)));

        var thrown = 0;
        var thread = Environment.CurrentManagedThreadId;
        void Count(object? sender, FirstChanceExceptionEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == thread && e.Exception is ArgumentOutOfRangeException
                && (e.Exception.StackTrace ?? "").Contains("OpenTypeFont", StringComparison.Ordinal))
                Interlocked.Increment(ref thrown);
        }
        AppDomain.CurrentDomain.FirstChanceException += Count;
        try
        {
            Assert.True(crafted.Outline(glyph).IsEmpty);
            Assert.False(crafted.HasGlyphData(glyph));
            Assert.Null(crafted.ContourCorner(glyph));
            Assert.Equal(Ot05Sans.Value.Outline(next).Bounds, crafted.Outline(next).Bounds);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Count;
        }
        Assert.Equal(0, thrown);
    }
}
