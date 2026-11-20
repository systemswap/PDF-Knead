using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The time a sampled function may take (PLAN §6 point 8: time overflows) and the decision of 17.11.2026 (/Order 3 always cubic):
/// a spline stays cubic, above 8 inputs as well, whatever was computed before; the work is bounded per drawn object (<see cref="FunctionWork"/>)
/// — beyond it the object is not shown, with a notice, never drawn linearly instead. Before, one budget per function made the spline go linear
/// once spent, and above 8 inputs or 4 096 samples per evaluation it was linear from the start (docs/FEHLERGEDAECHTNIS.md, No. 785, 908).
/// </summary>
public sealed class FunctionBudgetTests
{
    private static (SampledFunction Function, PdfDocument Doc) Function(string dict, string data)
    {
        var doc = PdfDocument.Open(DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /FunctionType 0 {dict} /BitsPerSample 8 /Length {data.Length} >>\nstream\n{data}\nendstream"));
        return ((SampledFunction)PdfFunction.Create(doc.GetObject(3), doc)!, doc);
    }

    private static double At(PdfFunction f, params double[] x)
    {
        Span<double> output = stackalloc double[1];
        f.Evaluate(x, output);
        return output[0];
    }

    // halfway between 64 and 255 of 255 with 0 before and 64 after: the spline, not the line (0.625)
    private const double Spline = 0.5 * 64 / 255 + 0.5625;

    /// <summary>
    /// A spline stays cubic however often it was evaluated; within an object's bound it is cubic, beyond it the object cannot be computed — a
    /// limit error and one notice — and after it the same function is cubic again (the bound belongs to the object, not to the function).
    /// </summary>
    [Fact]
    public void CubicSpline_BoundPerObject_NeverLinear()
    {
        var (cubic, doc) = Function("/Domain [0 1] /Range [0 1] /Size [5] /Order 3", "\x00\x40\xFF\x40\x00");
        for (var i = 0; i < 1000; i++) Assert.Equal(Spline, At(cubic, 0.375), 9);
        using (FunctionWork.Begin(doc, limit: 40))
        {
            Assert.Equal(Spline, At(cubic, 0.375), 9);   // 4 samples
            Assert.Throws<PdfLimitException>(() => { for (var i = 0; i < 20; i++) At(cubic, 0.375); });
            Assert.Throws<PdfLimitException>(() => At(cubic, 0.375));
        }
        Assert.Single(doc.Problems, p => p.Contains("zu aufwendig", StringComparison.Ordinal));
        Assert.Equal(Spline, At(cubic, 0.375), 9);
    }

    /// <summary>/Order 3 with nine inputs: cubic in the input with at least 4 samples (before, above 8 inputs always linear; the counter-check: DocumentStructureNormTests.SampledFunction_Order3_NineInputs_Cubic).</summary>
    [Fact]
    public void CubicSpline_NineInputs_StillCubic()
    {
        byte[] pattern = [0x00, 0x40, 0xFF, 0x40];
        var data = new string([.. Enumerable.Range(0, 4 * 256).Select(i => (char)pattern[i % 4])]);
        var (f, _) = Function($"/Domain [{string.Join(" ", Enumerable.Repeat("0 1", 9))}] /Range [0 1] /Size [4 {string.Join(" ", Enumerable.Repeat("2", 8))}] /Order 3", data);
        Assert.Equal(Spline, At(f, [0.5, .. Enumerable.Repeat(0.0, 8)]), 9);
    }

    /// <summary>Nine inputs between samples: multilinear over 512 corners — for a linear table the value at the middle.</summary>
    [Fact]
    public void ManyInputs_Multilinear()
    {
        var data = new string([.. Enumerable.Range(0, 512).Select(i => (char)(int)Math.Round(System.Numerics.BitOperations.PopCount((uint)i) * 255.0 / 9))]);
        var (f, _) = Function($"/Domain [{string.Join(" ", Enumerable.Repeat("0 1", 9))}] /Range [0 1] /Size [{string.Join(" ", Enumerable.Repeat("2", 9))}]", data);
        Assert.Equal(0.5, At(f, [.. Enumerable.Repeat(0.5, 9)]), 9);
    }
}
