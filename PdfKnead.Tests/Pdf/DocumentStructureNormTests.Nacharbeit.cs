using PdfKnead.Core.Pdf.Document;

namespace PdfKnead.Tests.Pdf;

/// <summary>Findings of the sentence-by-sentence assignment of M5 (23.11.2026), fixed and tested here.</summary>
public sealed partial class DocumentStructureNormTests
{
    /// <summary>A PostScript calculator function with one input and outputs in −1000 … 1000, or null where it is refused.</summary>
    private static double? Calculate(string program, double input)
    {
        using var doc = PdfDocument.Open(FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            $"<< /FunctionType 4 /Domain [-1000 1000] /Range [-1000 1000] /Length {program.Length} >>\nstream\n{program}\nendstream"));
        if (Core.Pdf.Graphics.PdfFunction.Create(doc.GetObject(4), doc) is not { } function) return null;
        Span<double> output = stackalloc double[1];
        function.Evaluate([input], output);
        return output[0];
    }

    /// <summary>
    /// 7.10.5.1: a comment (% to the end of its line) is white space in a PostScript calculator function — before the function was refused for
    /// the unknown character (M5, 23.11.2026).
    /// </summary>
    [Fact]
    public void PostScriptFunction_Comment_ReadAsWhiteSpace()
    {
        Assert.Equal(0.5, Calculate("{ % halve the input\n 2 div % done\r}", 1));
        Assert.Equal(0.5, Calculate("{ pop%\n0.5 }", 7));
    }

    /// <summary>
    /// Table 42: the operator names exactly as they stand, in small letters — before any case and lists with commas were taken („Index,Jump“
    /// named an operator that does not exist; M5, 23.11.2026).
    /// </summary>
    [Theory]
    [InlineData("{ 1 add }", true)]
    [InlineData("{ 1 ADD }", false)]
    [InlineData("{ 1 Add }", false)]
    [InlineData("{ 0 Index,Jump }", false)]
    [InlineData("{ 1 add,mul }", false)]
    [InlineData("{ jump }", false)]
    public void PostScriptFunction_OperatorNamesExactly(string program, bool readable) =>
        Assert.Equal(readable, Calculate(program, 1) is not null);

    /// <summary>
    /// Table 42: and, or, xor and not work logically on booleans and bitwise on integers — before 0 and 1 counted as booleans whatever they were:
    /// „1 not“ gave 0 instead of −2 (M5, 23.11.2026). Booleans come from comparisons and true/false and stay booleans through the stack
    /// operators; true is 1, false 0 in the result.
    /// </summary>
    [Theory]
    [InlineData("{ pop 1 not }", -2)]
    [InlineData("{ pop 0 not }", -1)]
    [InlineData("{ pop 5 not }", -6)]
    [InlineData("{ pop true not }", 0)]
    [InlineData("{ pop false not }", 1)]
    [InlineData("{ 2 eq not }", 1)]
    [InlineData("{ pop 6 3 and }", 2)]
    [InlineData("{ pop 6 3 or }", 7)]
    [InlineData("{ pop 6 3 xor }", 5)]
    [InlineData("{ pop true false or }", 1)]
    [InlineData("{ pop true true xor }", 0)]
    [InlineData("{ pop 1 2 lt 3 4 lt and }", 1)]
    [InlineData("{ pop true dup not and }", 0)]
    [InlineData("{ pop true 1 exch not }", 0)]
    [InlineData("{ pop true 1 2 1 roll pop not }", -2)]
    [InlineData("{ pop false 7 1 index not }", 1)]
    public void PostScriptFunction_BooleansApartFromIntegers(string program, double expected) =>
        Assert.Equal(expected, Calculate(program, 1));

    /// <summary>
    /// Tables 38 to 40: /Domain is required for every function type, /BitsPerSample for type 0, /N for type 2 — a function without them is
    /// refused, as all three reference readers do (measured 23.11.2026); before [0 1], 8 and 1 were taken. With the entries each one is read.
    /// </summary>
    [Theory]
    [InlineData("<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>", null, true)]
    [InlineData("<< /FunctionType 2 /C0 [0] /C1 [1] /N 1 >>", null, false)]
    [InlineData("<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] >>", null, false)]
    [InlineData("<< /FunctionType 0 /Domain [0 1] /Range [0 1] /Size [2] /BitsPerSample 8", "\x00\xFF", true)]
    [InlineData("<< /FunctionType 0 /Range [0 1] /Size [2] /BitsPerSample 8", "\x00\xFF", false)]
    [InlineData("<< /FunctionType 0 /Domain [0 1] /Range [0 1] /Size [2]", "\x00\xFF", false)]
    [InlineData("<< /FunctionType 3 /Domain [0 1] /Functions [<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>] /Bounds [] /Encode [0 1] >>", null, true)]
    [InlineData("<< /FunctionType 3 /Functions [<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>] /Bounds [] /Encode [0 1] >>", null, false)]
    [InlineData("<< /FunctionType 4 /Domain [0 1] /Range [0 1]", "{ }", true)]
    [InlineData("<< /FunctionType 4 /Range [0 1]", "{ }", false)]
    public void Functions_RequiredEntriesMissing_Refused(string dict, string? data, bool readable)
    {
        var body = data is null ? dict : $"{dict} /Length {data.Length} >>\nstream\n{data}\nendstream";
        using var doc = PdfDocument.Open(FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>", body));
        Assert.Equal(readable, Core.Pdf.Graphics.PdfFunction.Create(doc.GetObject(4), doc) is not null);
    }

    /// <summary>
    /// Read tolerance S-A A122 (decided on 23.11.2026): a sampled function whose /Encode holds fewer than two values per input (Table 39) is
    /// read with the default [0 Size − 1] — the same colours as without /Encode, as two of three reference readers draw it (measured).
    /// </summary>
    [Theory]
    [InlineData("/Encode [0]")]
    [InlineData("/Encode []")]
    public void SampledFunction_EncodeTooShort_ReadWithTheDefault(string encode)
    {
        double[] Evaluate(string extra)
        {
            const string data = "\x00\x55\xAA\xFF";
            using var doc = PdfDocument.Open(FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
                $"<< /FunctionType 0 /Domain [0 1] /Range [0 1] /Size [4] /BitsPerSample 8 {extra} /Length {data.Length} >>\nstream\n{data}\nendstream"));
            var function = Core.Pdf.Graphics.PdfFunction.Create(doc.GetObject(4), doc)!;
            return [.. new[] { 0.0, 0.2, 0.5, 0.9, 1.0 }.Select(x => { Span<double> o = stackalloc double[1]; function.Evaluate([x], o); return o[0]; })];
        }
        var plain = Evaluate("");
        Assert.Equal(plain, Evaluate(encode));
        Assert.NotEqual(plain, Evaluate("/Encode [3 0]"));   // a complete /Encode counts
    }

    /// <summary>
    /// Read tolerance S-A A123 (decided on 23.11.2026): div, idiv and mod by 0 in a PostScript calculator function (an error, 7.10.5) give 0,
    /// and the program goes on — the reference readers differ (two go on as if the operator were missing, one gives 0 like PDF-Knead).
    /// </summary>
    [Theory]
    [InlineData("{ 0 div }")]
    [InlineData("{ pop 7 0 idiv }")]
    [InlineData("{ pop 7 0 mod }")]
    public void PostScriptFunction_DivisionByZero_GivesZero(string program) =>
        Assert.Equal(0, Calculate(program, 5));
}
