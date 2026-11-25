using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

public sealed partial class M6NormTests
{
    /// <summary>
    /// Check point A67 for M6 (ISO 32000-2, 7.3.9: an entry whose reference leads nowhere counts as absent): the keys of chapter 8 that the
    /// PDF/A check asks about — /Alternates and /OPI of an image, /OPI and /Ref of a form, /OCProperties of the catalogue (PDF/A-1) — count as
    /// absent when they point into the void, so the check reports nothing for them; with a real value it reports each (before, an entry into
    /// the void counted as present; docs/FEHLERGEDAECHTNIS.md, No. 1135).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A67_Chapter8KeysIntoTheVoid_CountAsAbsentInThePdfACheck(bool intoTheVoid)
    {
        string Value(string real, int missing) => intoTheVoid ? $"{missing} 0 R" : real;
        var file = TestFiles.Build(new Dictionary<int, object>
        {
            [1] = $"<</Type/Catalog/Pages 2 0 R/OCProperties {Value("<</OCGs[]/D<<>>>>", 95)}>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            [3] = "<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]/Contents 4 0 R/Resources<</XObject<</Im1 5 0 R/Fm1 6 0 R>>>>>>",
            [4] = ("", "q 100 0 0 100 50 50 cm /Im1 Do Q /Fm1 Do"),
            [5] = ($"/Type/XObject/Subtype/Image/Width 2/Height 1/ColorSpace/DeviceGray/BitsPerComponent 8/Alternates {Value("[]", 99)}" +
                   $"/OPI {Value("<</Version 2.0>>", 98)}", "\x40\xC0"),
            [6] = ($"/Type/XObject/Subtype/Form/BBox[0 0 10 10]/OPI {Value("<</Version 2.0>>", 97)}/Ref {Value("<</F(x.pdf)/Page 0>>", 96)}",
                   "0 0 5 5 re f"),
        });
        using var doc = PdfDocument.Open(file);
        var found = PdfACheck.Check(doc, PdfAConformance.A1b).Select(v => v.Id).ToHashSet();
        string?[] rules =
        [
            PdfARules.ImageAlternates.Id(PdfAConformance.A1b), PdfARules.ImageOpi.Id(PdfAConformance.A1b),
            PdfARules.NoReferenceXObject.Id(PdfAConformance.A1b), PdfARules.NoOptionalContent.Id(PdfAConformance.A1b),
        ];
        foreach (var rule in rules.Select(r => Assert.IsType<string>(r)))
            Assert.Equal(!intoTheVoid, found.Contains(rule));
    }
}
