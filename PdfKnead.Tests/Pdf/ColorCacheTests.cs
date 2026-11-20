using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Tests.Pdf;

/// <summary>The colour caches of separation and DeviceN colours: values outside 0 … 1 share no entry with others (docs/FEHLERGEDAECHTNIS.md, No. 272).</summary>
public sealed class ColorCacheTests
{
    [Fact]
    public void DeviceN_ValueOutsideTheRange_DoesNotTakeAnotherColoursEntry()
    {
        // DeviceN with two inks; the tint transform gives grey from the second one (exch pop dup dup)
        using var doc = PdfDocument.Open(CraftedFileTests.Build(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 200 200]>>",
            [4] = "<</Type/Page/Parent 2 0 R>>",
            [5] = "[/DeviceN [/A /B] /DeviceRGB 6 0 R]",
            [6] = ("/FunctionType 4/Domain[0 1 0 1]/Range[0 1 0 1 0 1]", "{ exch pop dup dup }"),
        }));
        var space = ColorSpace.Create(doc.GetObject(5)!, null, doc);
        Assert.Equal(255, space.ToRgb([0, 1.031]).R);   // the second ink clamped to 1: white
        Assert.Equal(0, space.ToRgb([0.001, 0]).R);     // black — before, the same key as above gave white
        Assert.Equal(0, space.ToRgb([0, 0]).R);
    }

    /// <summary>
    /// A colour does not depend on what was converted before it: two values in the same step of the cache key (a thousandth) give the
    /// colour of the step, whichever came first. Before, the first value of a step decided the colour of all others — and as the transform
    /// for DeviceCMYK is shared by all documents and threads, the same file was drawn slightly differently under the load of other work
    /// (docs/FEHLERGEDAECHTNIS.md, No. 400).
    /// </summary>
    [Fact]
    public void Icc_ColourOfAValue_DoesNotDependOnTheOrder()
    {
        var bytes = CmykBytes();
        var differing = 0;
        for (var step = 1; step < 1000; step += 3)
        {
            double below = (step - 0.45) / 1000, above = (step + 0.45) / 1000;
            var first = new Core.Pdf.Graphics.Icc.IccTransform(Core.Pdf.Graphics.Icc.IccProfile.Parse(bytes)!);
            first.ToRgb([0.2, below, 0.6, below]);
            var afterOther = first.ToRgb([0.2, above, 0.6, above]);
            var alone = new Core.Pdf.Graphics.Icc.IccTransform(Core.Pdf.Graphics.Icc.IccProfile.Parse(bytes)!).ToRgb([0.2, above, 0.6, above]);
            if (afterOther != alone) differing++;
        }
        Assert.Equal(0, differing);
    }

    /// <summary>
    /// The same for separation and DeviceN colours: the colour of a step of the key does not depend on which value of the step came first.
    /// </summary>
    [Fact]
    public void DeviceN_ColourOfAValue_DoesNotDependOnTheOrder()
    {
        var file = CraftedFileTests.Build(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 200 200]>>",
            [4] = "<</Type/Page/Parent 2 0 R>>",
            [5] = "[/DeviceN [/A /B] /DeviceRGB 6 0 R]",
            [6] = ("/FunctionType 4/Domain[0 1 0 1]/Range[0 1 0 1 0 1]", "{ pop dup dup }"),
        });
        var differing = 0;
        for (var step = 1; step < 1000; step += 3)
        {
            double below = (step - 0.45) / 1000, above = (step + 0.45) / 1000;
            using var one = PdfDocument.Open(file);
            var first = ColorSpace.Create(one.GetObject(5)!, null, one);
            first.ToRgb([below, 0]);
            var afterOther = first.ToRgb([above, 0]);
            using var two = PdfDocument.Open(file);
            var alone = ColorSpace.Create(two.GetObject(5)!, null, two).ToRgb([above, 0]);
            if (afterOther != alone) differing++;
        }
        Assert.Equal(0, differing);
    }

    private static byte[] CmykBytes()
    {
        using var stream = typeof(ColorSpace).Assembly.GetManifestResourceStream("Farbprofile/FOGRA39L_coated.icc")!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
