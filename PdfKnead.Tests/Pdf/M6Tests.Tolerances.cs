using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Read tolerances of chapter 8 that had no test of their own in the drafts (docs/normabgleich/sonderregeln.md, S-A with module M6): each
/// reads as the reference readers show it (measured with tools/normabgleich/proben/lesetoleranzen_m6.py and tools/normabgleich/lesetoleranz.py;
/// docs/FEHLERGEDAECHTNIS.md, No. 1134).
/// </summary>
public sealed partial class M6NormTests
{
    /// <summary>
    /// 8.2 sentences 26–27 (read leniently, S-A A174): an operator outside the place Figure 9 allows it acts where it stands, as with all three
    /// reference readers — RG in the middle of a path colours its stroke red, an empty BT … ET inside a path leaves the path going on, a
    /// rectangle filled inside a text object is painted.
    /// </summary>
    [Fact]
    public void A81_OperatorsOutsideTheirPlace_ActWhereTheyStand()
    {
        using (var b = B84_Render("8 w 30 60 m 170 60 l 1 0 0 RG 170 160 l S"))
        {
            Assert.True(B84_Red(b, 100, 60));
            Assert.True(B84_Red(b, 170, 110));
        }
        using (var b = B84_Render("0 0 1 RG 8 w 30 60 m BT ET 170 60 l S"))
            Assert.True(B84_Blue(b, 100, 60));
        using (var b = B84_Render("BT 0 0 1 rg 30 60 140 40 re f ET"))
            Assert.True(B84_Blue(b, 100, 80));
    }

    /// <summary>
    /// 8.5.4 with 8.5.3.1 sentence 4 (read leniently, S-A A173): W without a path waits for the next painting operator and clips by the path
    /// painted there, as with all three reference readers — the red page-sized fill after „W 0 0 1 rg 50 50 100 100 re f“ shows only inside
    /// that square.
    /// </summary>
    [Fact]
    public void C85_ClipWithoutPath_AppliesToTheNextPaintedPath()
    {
        using var b = B84_Render("W 0 0 1 rg 50 50 100 100 re f 1 0 0 rg 0 0 200 200 re f");
        Assert.True(B84_Red(b, 100, 100));
        Assert.True(B84_White(b, 20, 20));
    }

    /// <summary>
    /// 8.9.5.1 (Table 87: /BitsPerComponent and /ColorSpace required; read leniently, S-A A179 and A180): an image XObject without
    /// /BitsPerComponent is read with 8 bits (2 × 1 RGB: red, blue), as by one reference reader; one without /ColorSpace (not JPEG 2000, no
    /// mask) as DeviceGray (0, 255: black, white) — the other readers draw nothing.
    /// </summary>
    [Fact]
    public void ImageXObject_WithoutBitsPerComponentOrColourSpace_ReadAs8BitsAndGrey()
    {
        using (var b = I89_FullImage(I89_Image("/Width 2 /Height 1 /ColorSpace /DeviceRGB", "\xff\x00\x00\x00\x00\xff")))
        {
            Assert.True(I89_Red(I89_At(b, 50, 100)));
            Assert.True(I89_Blue(I89_At(b, 150, 100)));
        }
        using (var b = I89_FullImage(I89_Image("/Width 2 /Height 1 /BitsPerComponent 8", "\x00\xff")))
        {
            Assert.True(I89_At(b, 50, 100) is { Red: < 10, Green: < 10, Blue: < 10 });
            Assert.True(I89_White(I89_At(b, 150, 100)));
        }
    }

    /// <summary>Samples of <paramref name="bpc"/> bits packed from the most significant bit, the row filled up to a whole byte.</summary>
    private static string Tol_Pack(int bpc, params int[] samples)
    {
        var bytes = new byte[(samples.Length * bpc + 7) / 8];
        for (var i = 0; i < samples.Length; i++)
            for (var b = 0; b < bpc; b++)
                if ((samples[i] >> (bpc - 1 - b) & 1) != 0)
                {
                    var bit = i * bpc + b;
                    bytes[bit >> 3] |= (byte)(0x80 >> (bit & 7));
                }
        return Encoding.Latin1.GetString(bytes);
    }

    /// <summary>
    /// 8.9.5.1 (Table 87: 1, 2, 4, 8 or 16 bits; read leniently, docs/normabgleich/sonderregeln.md S-A A192): an image with a depth between the
    /// allowed ones is decoded at its depth, as one reference reader does (T08 of tools/normabgleich/proben/lesetoleranzen_m6.py; before, a
    /// grey placeholder — docs/FEHLERGEDAECHTNIS.md, No. 1134): three grey samples 0, ⌊max/2⌋, max of 2^bpc − 1 show black, mid grey, white,
    /// as an XObject and inline; samples cross byte boundaries.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(12)]
    [InlineData(15)]
    public void Image_DepthBetweenTheAllowedOnes_DecodedAtItsDepth(int bpc)
    {
        var max = (1 << bpc) - 1;
        var data = Tol_Pack(bpc, 0, max / 2, max);
        var expected = (int)Math.Round(255.0 * (max / 2) / max);
        using var xobject = I89_FullImage(I89_Image($"/Width 3 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent {bpc}", data));
        using var inline = I89_Render(I89_File("", "", $"q 200 0 0 200 0 0 cm BI /W 3 /H 1 /CS /G /BPC {bpc} ID {data} EI Q"));
        foreach (var b in new[] { xobject, inline })
        {
            Assert.True(I89_At(b, 30, 100) is { Red: < 10 }, $"{bpc}: {I89_At(b, 30, 100)}");
            Assert.InRange(I89_At(b, 100, 100).Red, expected - 3, expected + 3);
            Assert.True(I89_White(I89_At(b, 170, 100)), $"{bpc}: {I89_At(b, 170, 100)}");
        }
    }

    /// <summary>
    /// The same depths with /Decode, a colour key and Indexed (S-A A192): /Decode [1 0] of a 3-bit grey image turns it round; /Mask [0 0] leaves
    /// out the sample 0 (the red ground shows); a 3-bit Indexed image takes indices 0 … 7 as they stand (index 5 blue); 3 bits per RGB
    /// component (nine bits a pixel) give red and blue.
    /// </summary>
    [Fact]
    public void Image_DepthBetweenTheAllowedOnes_DecodeColourKeyIndexedAndRgb()
    {
        using (var b = I89_FullImage(I89_Image("/Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 3 /Decode [1 0]", Tol_Pack(3, 0, 7))))
        {
            Assert.True(I89_White(I89_At(b, 50, 100)));
            Assert.True(I89_At(b, 150, 100) is { Red: < 10 });
        }
        using (var b = I89_Render(I89_File("", "/XObject << /Im0 5 0 R >>", "1 0 0 rg 0 0 200 200 re f q 200 0 0 200 0 0 cm /Im0 Do Q",
                   I89_Image("/Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 3 /Mask [0 0]", Tol_Pack(3, 0, 7)))))
        {
            Assert.True(I89_Red(I89_At(b, 50, 100)));
            Assert.True(I89_White(I89_At(b, 150, 100)));
        }
        const string palette = "<000000 000000 000000 000000 000000 0000FF FF0000 000000>";
        using (var b = I89_FullImage(I89_Image($"/Width 2 /Height 1 /ColorSpace [/Indexed /DeviceRGB 7 {palette}] /BitsPerComponent 3", Tol_Pack(3, 5, 6))))
        {
            Assert.True(I89_Blue(I89_At(b, 50, 100)));
            Assert.True(I89_Red(I89_At(b, 150, 100)));
        }
        using (var b = I89_FullImage(I89_Image("/Width 2 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 3", Tol_Pack(3, 7, 0, 0, 0, 0, 7))))
        {
            Assert.True(I89_Red(I89_At(b, 50, 100)));
            Assert.True(I89_Blue(I89_At(b, 150, 100)));
        }
    }

    /// <summary>
    /// Saved, redacted and cut as displayed (S-A A192, „überall gleich“): a 3-bit grey image (0, 3, 7) and a 3-bit Indexed image are written
    /// with 8 bits — the grey values scaled (v × 255 / 7), the indices kept, the colour key scaled with them —, they look the same; blacked
    /// out under a bar the image keeps its samples beside it (8 bits); cut, the part has 8 bits and the same samples.
    /// </summary>
    [Fact]
    public void Image_DepthBetweenTheAllowedOnes_SavedRedactedAndCutWithAnAllowedDepth()
    {
        var file = I89_File("", "/XObject << /Im0 5 0 R /Im1 6 0 R >>", "q 200 0 0 100 0 100 cm /Im0 Do Q q 200 0 0 100 0 0 cm /Im1 Do Q",
            I89_Image("/Width 3 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 3 /Mask [3 3]", Tol_Pack(3, 0, 3, 7)),
            I89_Image("/Width 2 /Height 1 /ColorSpace [/Indexed /DeviceRGB 7 <000000 000000 000000 000000 000000 0000FF FF0000 000000>] /BitsPerComponent 3",
                Tol_Pack(3, 5, 6)));
        using var before = I89_Render(file);
        using (var saved = DocumentStructureNormTests.Saved(file))
        {
            foreach (var image in I89_Images(saved))
                Assert.Equal(8, image.Dictionary.GetInteger(PdfName.Get("BitsPerComponent")));
            var grey = I89_Images(saved).Single(i => i.Dictionary.Get(PdfName.Get("ColorSpace")) is PdfName);
            Assert.Equal(new byte[] { 0, 109, 255 }, saved.GetStreamData(grey));
            Assert.Equal(new[] { 109.0, 109.0 }, PdfFunction.Numbers(grey.Dictionary.Get(PdfName.Get("Mask")) as PdfArray));
            var indexed = I89_Images(saved).Single(i => i.Dictionary.Get(PdfName.Get("ColorSpace")) is PdfArray);
            Assert.Equal(new byte[] { 5, 6 }, saved.GetStreamData(indexed));
            using var after = SkiaRenderer.RenderPage(PageInterpreter.Build(saved, saved.Pages[0]), saved, saved.Pages[0], 1, new ImageCache());
            Assert.True(B84_Same(before, after));
        }
        using var doc = PdfDocument.Open(file);
        var source = (PdfStream)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Im0"))!;
        var blanked = ImageBlanker.TryBlank(doc, source, null, Matrix.Scale(300, 100), [RectD.FromLtrb(0, 0, 100, 100)], out var handled,
            new ImageBlanker.PageBudget { Left = 0 });
        Assert.True(handled);
        Assert.Equal(8, blanked!.Dictionary.GetInteger(PdfName.Get("BitsPerComponent")));
        var blankedData = doc.DecodeStream(blanked).Data;
        Assert.Equal(new byte[] { 109, 255 }, blankedData[^2..]);
        var cut = ImageCut.Cut(doc, source, null, 1.0 / 3, 0, 1, 1)!;
        Assert.Equal(8, cut.Dictionary.GetInteger(PdfName.Get("BitsPerComponent")));
        Assert.Equal(new byte[] { 109, 255 }, cut.RawData);
        Assert.Equal(new[] { 109.0, 109.0 }, PdfFunction.Numbers(cut.Dictionary.Get(PdfName.Get("Mask")) as PdfArray));
    }

    /// <summary>
    /// An inline image of 12 bits (S-A A192) becomes an image XObject with 16 bits when saved (8.9.7: inline images take the same depths),
    /// the values scaled (2047 → 32759, 4095 → 65535), the same appearance.
    /// </summary>
    [Fact]
    public void InlineImage_DepthBetweenTheAllowedOnes_SavedAsXObjectWith16Bits()
    {
        var file = I89_File("", "", $"q 200 0 0 200 0 0 cm BI /W 3 /H 1 /CS /G /BPC 12 ID {Tol_Pack(12, 0, 2047, 4095)} EI Q");
        using var before = I89_Render(file);
        using var saved = DocumentStructureNormTests.Saved(file);
        Assert.DoesNotContain("BI", I89_Content(saved));
        var image = Assert.Single(I89_Images(saved));
        Assert.Equal(16, image.Dictionary.GetInteger(PdfName.Get("BitsPerComponent")));
        Assert.Equal(new byte[] { 0, 0, 0x7F, 0xF7, 0xFF, 0xFF }, saved.GetStreamData(image));
        using var after = SkiaRenderer.RenderPage(PageInterpreter.Build(saved, saved.Pages[0]), saved, saved.Pages[0], 1, new ImageCache());
        Assert.True(B84_Same(before, after));
    }

    /// <summary>
    /// The version of a file (S-A A191): a file whose header names an earlier version (%PDF-1.4) is read by the rules of PDF 2.0 — an image
    /// hidden by its layer shows its first alternate without /OC (8.9.5.4; ISO 32000-1 has no such rule and shows nothing; the reference readers
    /// read no /Alternates and show nothing either: PDF-Knead shows more, not less).
    /// </summary>
    [Fact]
    public void EarlierVersion_ReadByTheRulesOf20_AlternateOfAHiddenImage()
    {
        const string content = "q 200 0 0 200 0 0 cm /Im0 Do Q";
        const string image = "/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 3";
        var file = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [6 0 R] /D << /OFF [6 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< {image} /OC 6 0 R /Alternates [<< /Image 7 0 R >>] >>\nstream\n\xff\x00\x00\nendstream",
            "<< /Type /OCG /Name (Ebene) >>",
            $"<< {image} >>\nstream\n\x00\x00\xff\nendstream")).Replace("%PDF-1.7", "%PDF-1.4"));
        using var doc = PdfDocument.Open(file);
        Assert.Equal("1.4", doc.Version);
        using var b = I89_Render(file);
        Assert.True(I89_Blue(I89_At(b, 100, 100)));
    }

    /// <summary>
    /// The version of a file (S-A A191): in a file of version 1.4, /Decode of a JPEG 2000 image with /ColorSpace applies as PDF 2.0 says
    /// (7.4.9; ISO 32000-1 passes it over), as two reference readers apply it in every version (T V1a–V1c of
    /// tools/normabgleich/proben/lesetoleranzen_m6.py) — the grey image of zeros with /Decode [1 0] is drawn white.
    /// </summary>
    [TestPdfsFact]
    public void EarlierVersion_ReadByTheRulesOf20_JpxDecodeWithColourSpace()
    {
        var jpx = Encoding.Latin1.GetString(JpxColourTests.Codestream("bug_jpx.pdf"));   // 128 × 64, all 0
        const string content = "q 200 0 0 200 0 0 cm /Im0 Do Q";
        var file = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /Type /XObject /Subtype /Image /Width 128 /Height 64 /BitsPerComponent 8 /ColorSpace /DeviceGray /Decode [1 0] /Filter /JPXDecode " +
            $"/Length {jpx.Length} >>\nstream\n{jpx}\nendstream")).Replace("%PDF-1.7", "%PDF-1.4"));
        using var b = I89_Render(file);
        Assert.True(I89_White(I89_At(b, 100, 100)));
    }
}
