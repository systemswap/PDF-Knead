using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// A bare codestream with an Rsiz value that T.800 Table A.10 does not assign is not wrapped by PdfAJpeg2000.Wrap
/// (docs/FEHLERGEDAECHTNIS.md, No. 1075).
/// </summary>
public sealed partial class PdfARepairTests
{
    /// <summary>
    /// T.800 Table A.10: a reserved Rsiz (0x0008, a capability bit of Part 1 not assigned, 0x1000, level 12 of a broadcast profile, 0x010C,
    /// 0x0FFE) makes a codestream that does not conform — the decoder reads it as unrestricted Part 1 and reports it (S-A A125). Wrapped, the
    /// PDF/A copy would carry it on: like a reserved bit depth it is not wrapped, of one, three or four colours (before it was framed, with
    /// feature 5 for CMYK). The same codestream with Rsiz 0 is wrapped. For the PDF/A copy the image then goes the way of every image that
    /// cannot be wrapped: drawn as the display decodes it (8 bits, Flate) — the copy conforms and keeps the image's size.
    /// </summary>
    [Theory]
    [InlineData(0x0008)]
    [InlineData(0x1000)]
    [InlineData(0x010C)]
    [InlineData(0x0FFE)]
    public void Jpeg2000Codestream_ReservedRsiz_NotWrapped(int rsiz)
    {
        foreach (var components in new[] { 1, 3, 4 })
        {
            var plain = JpxRobustTests.Codestream(0, 64, 64, components, 1, 0, 1, 0);
            var reserved = (byte[])plain.Clone();
            reserved[6] = (byte)(rsiz >> 8);
            reserved[7] = (byte)rsiz;
            Assert.NotNull(PdfAJpeg2000.Wrap(plain));
            Assert.Null(PdfAJpeg2000.Wrap(reserved));
        }

        foreach (var (data, filter) in new[] { (Rgb(0), "JPXDecode"), (Rgb(rsiz), "FlateDecode") })
        {
            var pdf = Simple(1, title: null);
            var image = pdf.Add(new PdfStream(Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(64)),
                ("Height", PdfInteger.Of(1)), ("Filter", N("JPXDecode"))), data));
            ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, Dict(("Font", Dict(("F1", Ref(3)))), ("XObject", Dict(("Jpx", Ref(image))))));
            pdf.Replace(4, new PdfStream(new PdfDictionary(), "q 64 0 0 8 20 100 cm /Jpx Do Q"u8.ToArray()));
            var result = Write(pdf.Build());
            Assert.True(result.Conforms, string.Join("\n", result.Violations.Select(v => $"{v.Rule} {v.Text}")));
            using var written = PdfDocument.Open(result.Bytes);
            var copy = (PdfStream)written.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Jpx"))!;
            Assert.Equal(filter, copy.Dictionary.GetName(PdfName.Filter)!.Value);
            Assert.Equal((64, 1), (copy.Dictionary.GetInteger(PdfName.Get("Width")), copy.Dictionary.GetInteger(PdfName.Get("Height"))));
        }

        static byte[] Rgb(int value)
        {
            var cs = JpxRobustTests.Codestream(0, 64, 64, 3, 1, 0, 1, 0);
            (cs[6], cs[7]) = ((byte)(value >> 8), (byte)value);
            return cs;
        }
    }
}
