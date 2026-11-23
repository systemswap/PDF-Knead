using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// An image in the wide working space drawn as exactly as without it (docs/FEHLERGEDAECHTNIS.md, No. 1092): the probe run against T.803 found
/// files 2 and 3 (sYCC) up to 7 steps from their sRGB reference, where Table G.1 allows 4 — the 8-bit steps of the wide space are coarser than
/// those of sRGB, and a colour beyond the wide primaries too was clipped in them and changed its hue.
/// </summary>
public sealed partial class JpxColourTests
{
    /// <summary>
    /// sYCC of 8 bits whose colours leave sRGB (and, in the third row, the BT.2020 primaries as well): in the wide working space, with its
    /// colours at 16 bits besides the 8-bit samples, and drawn on a page every pixel at most one step from R′G′B′ clipped to sRGB — what the
    /// reference images of T.803 show (IEC 61966-2-1, Annex G). From the 8-bit samples of the wide space alone, through the same profile, some
    /// pixels lie further away.
    /// </summary>
    [Fact]
    public void Sycc_WideSpace_DrawnFromSixteenBits_AsClippedInSrgb()
    {
        const int Width = 256, Height = 4;
        int[] y = new int[Width * Height], cb = new int[Width * Height], cr = new int[Width * Height];
        for (var x = 0; x < Width; x++)
        {
            // two ramps of Y with a little colour, then the corners of Cb and Cr over the ramp
            (y[x], cb[x], cr[x]) = (x, 96, 168);
            (y[Width + x], cb[Width + x], cr[Width + x]) = (x, 168, 92);
            (y[2 * Width + x], cb[2 * Width + x], cr[2 * Width + x]) = (x, (x & 1) == 0 ? 0 : 255, (x & 2) == 0 ? 0 : 255);
            // a ramp of Y with the colour differences at their ends: its red leaves BT.2020 as Y grows
            (y[3 * Width + x], cb[3 * Width + x], cr[3 * Width + x]) = (x, 0, 255);
        }
        var codes = AnnexI.Interleaved(y, cb, cr);
        var file = Jpx(JpxTests.AnnexG.Lossless(Width, Height, [(8, false), (8, false), (8, false)], [y, cb, cr]), [Enumerated(18)]);

        var image = JpxDecoder.Decode(file, long.MaxValue);
        Assert.Equal(JpxColor.Icc, image.Color);
        Assert.Equal(JpxParameterized.WideProfile(), image.IccProfile);
        Assert.NotNull(image.Fine);
        Assert.Equal(image.Samples.Length * 2, image.Fine.Length);

        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        var dict = TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(Width)),
            ("Height", PdfInteger.Of(Height)), ("BitsPerComponent", PdfInteger.Of(8)), ("Filter", TestPdf.N("JPXDecode")));
        var decoded = PdfImageDecoder.Decode(new ImageSource(new PdfStream(dict, file), null, null, false, 0), doc);
        Assert.NotNull(decoded);
        byte[] drawn = [.. decoded.Rgba.Where((_, i) => i % 4 != 3)];
        var expected = YccToRgb(codes);
        Assert.Equal(expected.Length, drawn.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(drawn[i] - expected[i]) <= 1, $"Pixel {i / 3} Kanal {i % 3}: {drawn[i]} statt {expected[i]}");

        // across the edge of the wide space the light of the ramp changes smoothly: no step between neighbours above 0.05 (its own steps stay
        // below 0.025; clipped in the own primaries outright, red fell from 1 to 0.68 there). Measured in light: near black the sRGB curve is
        // steep, and a smooth rise from 0 takes several 8-bit steps at once
        for (var x = 1; x < Width; x++)
            for (var k = 0; k < 3; k++)
            {
                double Light(int p) => SrgbLinear((image.Fine[(p * 3 + k) * 2] << 8 | image.Fine[(p * 3 + k) * 2 + 1]) / 65535.0);
                var step = Math.Abs(Light(3 * Width + x) - Light(3 * Width + x - 1));
                Assert.True(step <= 0.05, $"Verlauf bei {x}, Kanal {k}: Sprung {step:F3}");
            }

        // the 8-bit samples of the wide space alone through the same profile: further away somewhere
        var transform = new IccTransform(IccProfile.Parse(image.IccProfile!)!);
        Span<float> device = stackalloc float[3];
        Span<float> rgb = stackalloc float[3];
        var coarse = 0;
        for (var p = 0; p < Width * Height; p++)
        {
            for (var k = 0; k < 3; k++) device[k] = image.Samples[3 * p + k] / 255f;
            transform.ToSrgb(device, rgb);
            for (var k = 0; k < 3; k++) coarse = Math.Max(coarse, Math.Abs((int)Math.Round(Math.Clamp(rgb[k], 0, 1) * 255) - expected[3 * p + k]));
        }
        Assert.True(coarse > 1, $"8 Bit im weiten Raum: höchstens {coarse}");
    }
}
