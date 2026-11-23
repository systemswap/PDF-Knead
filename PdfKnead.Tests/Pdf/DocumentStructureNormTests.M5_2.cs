using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>Document structure, 7.10 to 7.12 and Annex E: functions, file specifications, extensions, sentence by sentence.</summary>
public sealed partial class DocumentStructureNormTests
{
    /// <summary>Functions, attachments and keys built for the tests of 7.10 to 7.12 and Annex E below.</summary>
    private static class FunctionAndFileObjects
    {
        /// <summary>The function of a one-page file whose object 4 is <paramref name="function"/> (a dictionary, or a stream with its data).</summary>
        internal static PdfFunction? Create(string function)
        {
            using var doc = PdfDocument.Open(FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>", function));
            return PdfFunction.Create(doc.GetObject(4), doc);
        }

        /// <summary>A function stream: the dictionary entries and the data, with its /Length.</summary>
        internal static string Stream(string entries, byte[] data) =>
            $"<< {entries} /Length {data.Length} >>\nstream\n{Encoding.Latin1.GetString(data)}\nendstream";

        /// <summary>A PostScript calculator function with the given inputs and outputs, each in −1000 … 1000.</summary>
        internal static PdfFunction? Calculator(string program, int inputs = 1, int outputs = 1) => Create(Stream(
            $"/FunctionType 4 /Domain [{string.Join(" ", Enumerable.Repeat("-1000 1000", inputs))}] /Range [{string.Join(" ", Enumerable.Repeat("-1000 1000", outputs))}]",
            Encoding.ASCII.GetBytes(program)));

        /// <summary>The outputs of a function at the given inputs.</summary>
        internal static double[] At(PdfFunction function, params double[] input)
        {
            var output = new double[function.Outputs];
            function.Evaluate(input, output);
            return output;
        }

        /// <summary>Samples of <paramref name="bits"/> bits each, high-order bit first, without padding between them; the last byte filled with 0.</summary>
        internal static byte[] Samples(int bits, params ulong[] samples)
        {
            var bytes = new byte[(samples.Length * bits + 7) / 8];
            var at = 0;
            foreach (var sample in samples)
                for (var b = bits - 1; b >= 0; b--, at++)
                    if ((sample >> b & 1) != 0) bytes[at >> 3] |= (byte)(0x80 >> (at & 7));
            return bytes;
        }

        /// <summary>A function of type 2 with the given entries and domain [0 1].</summary>
        internal static PdfFunction? Exponential(string entries) => Create($"<< /FunctionType 2 /Domain [0 1] {entries} >>");

        /// <summary>
        /// A one-page file with one attachment (data „ABC“) whose file specification (object 4) has the further entries
        /// <paramref name="specEntries"/> and whose embedded file stream the further entries <paramref name="streamEntries"/>; it stands in
        /// /Names /EmbeddedFiles and in the catalogue's /AF.
        /// </summary>
        internal static byte[] WithAttachment(string specEntries = "", string streamEntries = "") => FileOf(
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(daten.txt) 4 0 R] >> >> /AF [4 0 R] >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Contents 6 0 R >>",
            $"<< /Type /Filespec /F (daten.txt) /UF (daten.txt) /EF << /F 5 0 R >> {specEntries} >>",
            $"<< /Type /EmbeddedFile {streamEntries} /Length 3 >>\nstream\nABC\nendstream",
            "<< /Length 20 >>\nstream\n0 0 1 rg 9 9 9 9 re f\nendstream");

        /// <summary>The file specification of the attachment of a saved <see cref="WithAttachment"/>.</summary>
        internal static PdfDictionary Spec(PdfDocument saved) => saved.Catalog!.GetArray(PdfName.Get("AF"))!.Get(0)!.AsDictionary()!;

        /// <summary>Every key of every dictionary of a file (objects, nested and stream dictionaries) and of its trailer.</summary>
        internal static HashSet<string> Keys(PdfDocument doc)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            void Walk(PdfObject? o, int depth)
            {
                if (o is null || depth > 12) return;
                switch (o)
                {
                    case PdfStream st when seen.Add(st):
                        Walk(st.Dictionary, depth + 1);
                        break;
                    case PdfDictionary d when seen.Add(d):
                        foreach (var (k, v) in d.Entries)
                        {
                            keys.Add(k.Value);
                            if (v is not PdfReference) Walk(v, depth + 1);
                        }
                        break;
                    case PdfArray a when seen.Add(a):
                        for (var i = 0; i < a.Count; i++) if (a[i] is not PdfReference) Walk(a[i], depth + 1);
                        break;
                }
            }
            for (var n = 1; n <= doc.HighestObjectNumber; n++) Walk(doc.GetObject(n), 0);
            Walk(doc.Trailer, 0);
            return keys;
        }

        /// <summary>A one-page file saved with an own shape on its page (PDF-Knead's own keys in it).</summary>
        internal static byte[] SavedWithOwnElement(byte[] file, string version)
        {
            using var doc = PdfDocument.Open(file);
            var items = PageItem.From(doc).ToArray();
            items[0] = items[0].WithElements([.. items[0].Elements, new ShapeElement { Rect = new PdfRectangle(50, 50, 150, 150) }]);
            using var output = new MemoryStream();
            PdfWriter.Write(doc, items, output, new PdfSaveOptions { Version = version });
            return output.ToArray();
        }
    }

    // ---- 7.10 Functions

    /// <summary>
    /// 7.10.1, sentence 7 (m inputs, n outputs), with the bound of the implementation (PdfLimits.MaxComponents, 7.10.2 sentence 6): up to 32
    /// inputs and 32 outputs a function is read, with 33 it is refused — a type 2 with 32 and 33 outputs, a type 4 with 32 and 33 inputs.
    /// </summary>
    [Theory]
    [InlineData(2, 32, true)]
    [InlineData(2, 33, false)]
    [InlineData(4, 32, true)]
    [InlineData(4, 33, false)]
    public void Function_UpTo32InputsAndOutputs(int type, int count, bool readable)
    {
        var function = type == 2
            ? FunctionAndFileObjects.Exponential($"/C0 [{string.Join(" ", Enumerable.Repeat("0", count))}] /C1 [{string.Join(" ", Enumerable.Repeat("1", count))}] /N 1")
            : FunctionAndFileObjects.Calculator($"{{ {string.Join(" ", Enumerable.Repeat("pop", count - 1))} }}", inputs: count);
        Assert.Equal(readable, function is not null);
        if (function is not null) Assert.Equal(count, type == 2 ? function.Outputs : function.Inputs);
    }

    /// <summary>
    /// 7.10.1, Table 38, /FunctionType (sentences 19, 23, 26 to 29): only 0, 2, 3 and 4 are function types — 1 and 5 make no function; 2 with the
    /// same entries does.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(5, false)]
    public void Function_OnlyTypes0234(int type, bool readable) =>
        Assert.Equal(readable, FunctionAndFileObjects.Create($"<< /FunctionType {type} /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>") is not null);

    /// <summary>
    /// 7.10.1, sentence 42: the number of outputs follows from other entries except for types 0 and 4, where /Range is required — without it
    /// they make no function; a type 2 without /Range has as many outputs as /C0 and /C1 have values (here 3).
    /// </summary>
    [Fact]
    public void Function_RangeRequiredForTypes0And4_Type2CountsItsOutputs()
    {
        Assert.Null(FunctionAndFileObjects.Create(FunctionAndFileObjects.Stream("/FunctionType 0 /Domain [0 1] /Size [2] /BitsPerSample 8", [0, 255])));
        Assert.Null(FunctionAndFileObjects.Create(FunctionAndFileObjects.Stream("/FunctionType 4 /Domain [0 1]", "{ 0.5 }"u8.ToArray())));
        Assert.NotNull(FunctionAndFileObjects.Create(FunctionAndFileObjects.Stream("/FunctionType 4 /Domain [0 1] /Range [0 1]", "{ 0.5 }"u8.ToArray())));
        var exponential = FunctionAndFileObjects.Exponential("/C0 [0 0 0] /C1 [1 1 1] /N 1")!;
        Assert.Equal(3, exponential.Outputs);
        Assert.Equal([0.5, 0.5, 0.5], FunctionAndFileObjects.At(exponential, 0.5));
    }

    /// <summary>
    /// 7.10.2, Table 39, /BitsPerSample (sentence 22): 1, 2, 16, 24 and 32 bits per sample — three samples 0, 1 and the largest value of the
    /// width, read without padding between them: at the ends 0 and 1.0, in the middle 1 / (2^bits − 1); a width of 3 makes no function.
    /// </summary>
    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(2, 0.3333333333333333)]
    [InlineData(16, 1.5259021896696422e-05)]
    [InlineData(24, 5.960464832810452e-08)]
    [InlineData(32, 2.3283064370807974e-10)]
    [InlineData(3, null)]
    public void SampledFunction_BitsPerSample(int bits, double? middle)
    {
        var largest = (1UL << bits) - 1;
        var function = FunctionAndFileObjects.Create(FunctionAndFileObjects.Stream(
            $"/FunctionType 0 /Domain [0 1] /Range [0 1] /Size [3] /BitsPerSample {bits}", FunctionAndFileObjects.Samples(bits, 0, 1, largest)));
        if (middle is null)
        {
            Assert.Null(function);
            return;
        }
        Assert.NotNull(function);
        Assert.Equal(0, FunctionAndFileObjects.At(function, 0)[0]);
        Assert.Equal(middle.Value, FunctionAndFileObjects.At(function, 0.5)[0], 1e-15);
        Assert.Equal(1.0, FunctionAndFileObjects.At(function, 1)[0]);
    }

    /// <summary>
    /// 7.10.2, Table 39, /Decode (sentences 34, 35, 37): default the same as /Range — with /Range [−1 1] and no /Decode the samples 0 and 255
    /// give −1 and 1, half way 0 (with /Range [0 1] every default would give the same).
    /// </summary>
    [Fact]
    public void SampledFunction_DecodeDefaultIsTheRange()
    {
        var function = FunctionAndFileObjects.Create(FunctionAndFileObjects.Stream("/FunctionType 0 /Domain [0 1] /Range [-1 1] /Size [2] /BitsPerSample 8", [0, 255]))!;
        Assert.Equal(-1, FunctionAndFileObjects.At(function, 0)[0]);
        Assert.Equal(0, FunctionAndFileObjects.At(function, 0.5)[0], 1e-12);
        Assert.Equal(1, FunctionAndFileObjects.At(function, 1)[0]);
    }

    /// <summary>
    /// 7.10.2, sentence 38: the other entries of the function's stream dictionary (length, filters) are those of every stream — the samples are
    /// read after the filter: /ASCIIHexDecode data „00FF&gt;“ are the samples 0 and 255, giving 0, 0.25 and 1.
    /// </summary>
    [Fact]
    public void SampledFunction_SamplesReadAfterTheFilter()
    {
        var function = FunctionAndFileObjects.Create(FunctionAndFileObjects.Stream(
            "/FunctionType 0 /Domain [0 1] /Range [0 1] /Size [2] /BitsPerSample 8 /Filter /ASCIIHexDecode", "00FF>"u8.ToArray()))!;
        Assert.Equal(0, FunctionAndFileObjects.At(function, 0)[0]);
        Assert.Equal(0.25, FunctionAndFileObjects.At(function, 0.25)[0], 1e-12);
        Assert.Equal(1, FunctionAndFileObjects.At(function, 1)[0]);
    }

    /// <summary>
    /// 7.10.2, the third step (sentences 49 to 56): e′ = min(max(e, 0), Size − 1) — with /Encode [−2 4] over three samples (0, 128, 255) the
    /// encoded value runs from −2 to 4: x = 0 and x = 0.2 (e = −2 and −0.8) take the first sample, x = 0.5 (e = 1) the second, x = 0.75 and 1
    /// (e = 2.5 and 4) the last.
    /// </summary>
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.2, 0.0)]
    [InlineData(0.5, 128 / 255.0)]
    [InlineData(0.75, 1.0)]
    [InlineData(1.0, 1.0)]
    public void SampledFunction_EncodedValueClippedToTheTable(double x, double expected)
    {
        var function = FunctionAndFileObjects.Create(FunctionAndFileObjects.Stream(
            "/FunctionType 0 /Domain [0 1] /Range [0 1] /Size [3] /Encode [-2 4] /BitsPerSample 8", [0, 128, 255]))!;
        Assert.Equal(expected, FunctionAndFileObjects.At(function, x)[0], 1e-12);
    }

    /// <summary>
    /// 7.10.2, sentence 75: with several inputs the first dimension varies fastest — samples 0 0 255 255 of a table of 2 × 2 are, in order,
    /// (0, 0), (1, 0), (0, 1), (1, 1): x = (0, 1) gives 1, x = (1, 0) gives 0 (linear interpolation; the cubic way: SampledFunction_Order3_CubicSpline).
    /// </summary>
    [Theory]
    [InlineData(0.0, 0.0, 0.0)]
    [InlineData(1.0, 0.0, 0.0)]
    [InlineData(0.0, 1.0, 1.0)]
    [InlineData(1.0, 1.0, 1.0)]
    [InlineData(0.5, 0.25, 0.25)]
    public void SampledFunction_FirstInputVariesFastest(double x0, double x1, double expected)
    {
        var function = FunctionAndFileObjects.Create(FunctionAndFileObjects.Stream(
            "/FunctionType 0 /Domain [0 1 0 1] /Range [0 1] /Size [2 2] /BitsPerSample 8", [0, 0, 255, 255]))!;
        Assert.Equal(expected, FunctionAndFileObjects.At(function, x0, x1)[0], 1e-12);
    }

    /// <summary>
    /// 7.10.3, Table 40, /C0 and /C1 (sentences 4, 6 to 8, 10, 11): default [0.0] and [1.0] — without /C0 the function starts at 0, without /C1
    /// it ends at 1.0 (its value at x = 1); with N = 1 linear in between.
    /// </summary>
    [Theory]
    [InlineData("/C1 [1]", 0.0, 0.0)]
    [InlineData("/C1 [1]", 0.4, 0.4)]
    [InlineData("/C0 [0.5]", 0.0, 0.5)]
    [InlineData("/C0 [0.5]", 0.5, 0.75)]
    [InlineData("/C0 [0.5]", 1.0, 1.0)]
    public void ExponentialFunction_C0AndC1Defaults(string entries, double x, double expected) =>
        Assert.Equal(expected, FunctionAndFileObjects.At(FunctionAndFileObjects.Exponential(entries + " /N 1")!, x)[0], 1e-12);

    /// <summary>
    /// 7.10.4, sentence 1 (with 7.10.3): type 2 with N = 1 interpolates linearly between C0 and C1 — C0 [0.2], C1 [0.6]: x = 0.25 gives 0.3,
    /// x = 0.5 gives 0.4.
    /// </summary>
    [Theory]
    [InlineData(0.25, 0.3)]
    [InlineData(0.5, 0.4)]
    public void ExponentialFunction_N1_Linear(double x, double expected) =>
        Assert.Equal(expected, FunctionAndFileObjects.At(FunctionAndFileObjects.Exponential("/C0 [0.2] /C1 [0.6] /N 1")!, x)[0], 1e-12);

    /// <summary>
    /// 7.10.4, sentences 22 to 28, the intervals of a stitching function: k = 1 — one interval [D0, D1], every x to the only function; k = 3 with
    /// D0 &lt; B0 &lt; B1 &lt; D1 — [D0, B0), [B0, B1), [B1, D1], x = B0 and x = B1 to the interval on their right; k = 3 with D0 = B0 —
    /// [D0, B0], (B0, B1), [B1, D1], x = D0 to the first function, just above it to the second (k = 2: StitchingFunction_IntervalsAndTheirEnds).
    /// </summary>
    [Theory]
    [InlineData("", 0.0, 0.0)]
    [InlineData("", 0.7, 0.7)]
    [InlineData("", 1.0, 1.0)]
    [InlineData("0.3 0.6", 0.29, 1)]
    [InlineData("0.3 0.6", 0.3, 2)]
    [InlineData("0.3 0.6", 0.59, 2)]
    [InlineData("0.3 0.6", 0.6, 3)]
    [InlineData("0.3 0.6", 1.0, 3)]
    [InlineData("0 0.5", 0.0, 1)]
    [InlineData("0 0.5", 0.01, 2)]
    [InlineData("0 0.5", 0.5, 3)]
    public void StitchingFunction_OneAndThreeIntervals(string bounds, double x, double expected)
    {
        static string Constant(int v) => $"<< /FunctionType 2 /Domain [0 1] /C0 [{v}] /C1 [{v}] /N 1 >>";
        var functions = bounds.Length == 0 ? "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>" : $"{Constant(1)} {Constant(2)} {Constant(3)}";
        var encode = bounds.Length == 0 ? "0 1" : "0 1 0 1 0 1";
        var function = FunctionAndFileObjects.Create($"<< /FunctionType 3 /Domain [0 1] /Functions [{functions}] /Bounds [{bounds}] /Encode [{encode}] >>")!;
        Assert.Equal(expected, FunctionAndFileObjects.At(function, x)[0], 1e-12);
    }

    /// <summary>
    /// 7.10.5.1, sentences 5 to 7: no composite data (strings, arrays), no procedures but the blocks of if and ifelse, no names or variables —
    /// such a program makes no function; the same without them does.
    /// </summary>
    [Theory]
    [InlineData("{ (a) pop 0.5 }", false)]
    [InlineData("{ [1] pop 0.5 }", false)]
    [InlineData("{ { 1 } pop }", false)]
    [InlineData("{ /x 0.5 def }", false)]
    [InlineData("{ pop 0.5 }", true)]
    public void PostScriptFunction_NoCompositeDataProceduresOrNames(string program, bool readable) =>
        Assert.Equal(readable, FunctionAndFileObjects.Calculator(program) is not null);

    /// <summary>
    /// 7.10.5.2, Table 42, the arithmetic operators, each with values of its own: abs, cvr, cvi (towards zero), atan (degrees 0 to 360 of
    /// numerator over denominator), div, ceiling, exp (base to the exponent), cos (degrees), floor, idiv (towards zero), ln, log (base 10), mod
    /// (sign of the dividend), neg, round (half up), sin (degrees), sqrt, truncate.
    /// </summary>
    [Theory]
    [InlineData("{ abs }", -0.25, 0.25)]
    [InlineData("{ abs }", 0.25, 0.25)]
    [InlineData("{ cvr }", 0.3, 0.3)]
    [InlineData("{ cvi }", 2.7, 2)]
    [InlineData("{ cvi }", -2.7, -2)]
    [InlineData("{ pop 1 1 atan }", 0, 45)]
    [InlineData("{ pop 1 -1 atan }", 0, 135)]
    [InlineData("{ pop -1 0 atan }", 0, 270)]
    [InlineData("{ 4 div }", 1, 0.25)]
    [InlineData("{ ceiling }", 1.2, 2)]
    [InlineData("{ ceiling }", -1.5, -1)]
    [InlineData("{ pop 2 3 exp }", 0, 8)]
    [InlineData("{ 360 mul cos }", 0.25, 0)]
    [InlineData("{ 360 mul cos }", 0.5, -1)]
    [InlineData("{ floor }", -1.5, -2)]
    [InlineData("{ floor }", 1.5, 1)]
    [InlineData("{ pop 7 2 idiv }", 0, 3)]
    [InlineData("{ pop -7 2 idiv }", 0, -3)]
    [InlineData("{ pop 2.718281828459045 ln }", 0, 1)]
    [InlineData("{ pop 100 log }", 0, 2)]
    [InlineData("{ pop 7 3 mod }", 0, 1)]
    [InlineData("{ pop -7 3 mod }", 0, -1)]
    [InlineData("{ neg }", 0.3, -0.3)]
    [InlineData("{ pop 2.5 round }", 0, 3)]
    [InlineData("{ pop -2.5 round }", 0, -2)]
    [InlineData("{ pop 30 sin }", 0, 0.5)]
    [InlineData("{ sqrt }", 0.25, 0.5)]
    [InlineData("{ pop -2.7 truncate }", 0, -2)]
    [InlineData("{ pop 2.7 truncate }", 0, 2)]
    public void PostScriptFunction_ArithmeticOperators(string program, double x, double expected) =>
        Assert.Equal(expected, FunctionAndFileObjects.At(FunctionAndFileObjects.Calculator(program)!, x)[0], 1e-9);

    /// <summary>
    /// 7.10.5.2, Table 42, the relational, boolean and bitwise operators, each with values of its own: and, or, xor (bitwise on integers,
    /// logical on booleans), true, false, eq, ne, ge, lt, le, bitshift (left for a positive shift, right for a negative one); a boolean result
    /// counts 1 for true, 0 for false.
    /// </summary>
    [Theory]
    [InlineData("{ pop 12 10 and }", 0, 8)]
    [InlineData("{ pop true false and { 1 } { 0 } ifelse }", 0, 0)]
    [InlineData("{ pop 12 10 or }", 0, 14)]
    [InlineData("{ pop true false or { 1 } { 0 } ifelse }", 0, 1)]
    [InlineData("{ pop 12 10 xor }", 0, 6)]
    [InlineData("{ pop true { 1 } { 0 } ifelse }", 0, 1)]
    [InlineData("{ pop false { 1 } { 0 } ifelse }", 0, 0)]
    [InlineData("{ pop 1 2 eq { 1 } { 0 } ifelse }", 0, 0)]
    [InlineData("{ 0.5 ne { 1 } { 0 } ifelse }", 0.5, 0)]
    [InlineData("{ 0.5 ne { 1 } { 0 } ifelse }", 0.4, 1)]
    [InlineData("{ 0.5 ge { 1 } { 0 } ifelse }", 0.5, 1)]
    [InlineData("{ 0.5 ge { 1 } { 0 } ifelse }", 0.4, 0)]
    [InlineData("{ 0.5 lt { 1 } { 0 } ifelse }", 0.4, 1)]
    [InlineData("{ 0.5 lt { 1 } { 0 } ifelse }", 0.5, 0)]
    [InlineData("{ 0.5 le { 1 } { 0 } ifelse }", 0.5, 1)]
    [InlineData("{ 0.5 le { 1 } { 0 } ifelse }", 0.6, 0)]
    [InlineData("{ pop 1 3 bitshift }", 0, 8)]
    [InlineData("{ pop 8 -2 bitshift }", 0, 2)]
    public void PostScriptFunction_RelationalBooleanAndBitwiseOperators(string program, double x, double expected) =>
        Assert.Equal(expected, FunctionAndFileObjects.At(FunctionAndFileObjects.Calculator(program)!, x)[0]);

    /// <summary>
    /// 7.10.5.2, Table 42, if (sentence 25): a false condition passes the block over — 0.25 stays; a true one runs it (0.75).
    /// </summary>
    [Theory]
    [InlineData("{ pop 0.25 1 2 eq { pop 0.75 } if }", 0.25)]
    [InlineData("{ pop 0.25 2 2 eq { pop 0.75 } if }", 0.75)]
    public void PostScriptFunction_IfRunsItsBlockOnlyWhenTrue(string program, double expected) =>
        Assert.Equal(expected, FunctionAndFileObjects.At(FunctionAndFileObjects.Calculator(program)!, 0)[0]);

    /// <summary>
    /// 7.10.5.2, Table 42, the stack operators copy (the topmost n values once more), index (a copy of the n-th value below the count) and roll
    /// (n values by j places, upwards for a positive j, downwards for a negative one), each with values of its own.
    /// </summary>
    [Theory]
    [InlineData("{ 2 copy add add add }", 2, 1, "0.6")]
    [InlineData("{ 0.5 1 index add add }", 1, 1, "1")]
    [InlineData("{ 0.5 0.75 2 index }", 1, 4, "0.25 0.5 0.75 0.25")]
    [InlineData("{ 0.5 0 index }", 1, 3, "0.25 0.5 0.5")]
    [InlineData("{ pop 1 2 3 3 1 roll }", 1, 3, "3 1 2")]
    [InlineData("{ pop 1 2 3 3 -1 roll }", 1, 3, "2 3 1")]
    [InlineData("{ pop 1 2 3 4 3 2 roll }", 1, 4, "1 3 4 2")]
    public void PostScriptFunction_StackOperators(string program, int inputs, int outputs, string expected)
    {
        var function = FunctionAndFileObjects.Calculator(program, inputs, outputs)!;
        var result = FunctionAndFileObjects.At(function, inputs == 2 ? new[] { 0.1, 0.2 } : new[] { 0.25 });
        var values = expected.Split(' ').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(values.Length, result.Length);
        for (var i = 0; i < values.Length; i++) Assert.Equal(values[i], result[i], 1e-12);
    }

    // ---- 7.11 File specifications

    /// <summary>
    /// 7.11.2.1 (sentences 1, 2) and 7.11.2.2 (sentences 15 to 18): a file specification string is made of components separated by solidi, „..“
    /// going one level up — PDF-Knead neither builds a path of a platform from it nor resolves relative ones nor opens the file: it is shown as
    /// written, and saved the string stays byte for byte (/UF where one is made: the same text).
    /// </summary>
    [Theory]
    [InlineData("Ordner/Unterordner/Anhang.pdf")]
    [InlineData("../../Anhang.pdf")]
    public void FileSpecificationString_ComponentsAndParents_ShownAndSavedAsWritten(string path)
    {
        var file = FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            $"<< /Type /Annot /Subtype /Link /Rect [10 10 100 30] /A << /S /GoToR /F ({path}) /D [0 /Fit] >> >>");
        using var doc = PdfDocument.Open(file);
        var link = Assert.IsType<OtherLink>(Assert.Single(PageLinks.Of(doc, 0)).Target);
        Assert.Equal("GoToR", link.Kind);
        Assert.Equal(path, link.Detail);
        using var saved = Saved(file);
        var spec = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0)!.AsDictionary()!.GetDictionary(PdfName.Get("A"))!.Get(PdfName.Get("F"));
        switch (spec)
        {
            case PdfString s:
                Assert.Equal(Encoding.ASCII.GetBytes(path), s.Bytes);
                break;
            case PdfDictionary d:
                Assert.Equal(Encoding.ASCII.GetBytes(path), d.GetString(PdfName.Get("F"))!.Bytes);
                if (d.GetString(PdfName.Get("UF")) is { } uf) Assert.Equal(path, uf.ToText());
                break;
            default:
                Assert.Fail($"no file specification: {spec}");
                break;
        }
        Assert.Equal(path, Assert.IsType<OtherLink>(Assert.Single(PageLinks.Of(saved, 0)).Target).Detail);
    }

    /// <summary>
    /// 7.11.3, Table 43, /EP (sentences 50, 71 to 73; PDF 2.0): PDF-Knead makes no wrappers for encrypted payloads, and the /EP of a foreign one
    /// stays in the copy as it was.
    /// </summary>
    [Fact]
    public void FileSpecification_EncryptedPayloadDictionary_KeptAsWritten()
    {
        var file = FunctionAndFileObjects.WithAttachment("/AFRelationship /EncryptedPayload /EP << /Type /EncryptedPayload /Subtype /Musterverfahren /Version (1.0) >>");
        using var saved = Saved(file, new PdfSaveOptions { Version = "2.0" });
        var ep = FunctionAndFileObjects.Spec(saved).GetDictionary(PdfName.Get("EP"));
        Assert.NotNull(ep);
        Assert.Equal("EncryptedPayload", ep.GetName(PdfName.Type)!.Value);
        Assert.Equal("Musterverfahren", ep.GetName(PdfName.Subtype)!.Value);
        Assert.Equal("1.0", ep.GetString(PdfName.Get("Version"))!.ToText());
    }

    /// <summary>
    /// 7.11.3, Table 43, /AFRelationship (sentences 76, 79 to 100; PDF 2.0): each of the values Source, Data, Alternative, Supplement,
    /// EncryptedPayload, FormData, Schema and Unspecified stays as written — saved as PDF 2.0 and as PDF/A-3 (which adds Unspecified only where
    /// the entry is missing).
    /// </summary>
    [Theory]
    [InlineData("Source")]
    [InlineData("Data")]
    [InlineData("Alternative")]
    [InlineData("Supplement")]
    [InlineData("EncryptedPayload")]
    [InlineData("FormData")]
    [InlineData("Schema")]
    [InlineData("Unspecified")]
    public void FileSpecification_AFRelationship_KeptAsWritten(string relationship)
    {
        var file = FunctionAndFileObjects.WithAttachment($"/AFRelationship /{relationship} /Desc (Musterdaten)");
        using (var saved = Saved(file, new PdfSaveOptions { Version = "2.0" }))
            Assert.Equal(relationship, FunctionAndFileObjects.Spec(saved).GetName(PdfName.Get("AFRelationship"))!.Value);
        var three = PdfATests.Write(file, PdfAConformance.A3b);
        Assert.True(three.Conforms, PdfATests.Describe(three.Violations));
        using var archived = PdfDocument.Open(three.Bytes);
        Assert.Equal(relationship, FunctionAndFileObjects.Spec(archived).GetName(PdfName.Get("AFRelationship"))!.Value);
    }

    /// <summary>
    /// 7.11.4.1, Table 45, /Size (integer, the uncompressed size), /CreationDate (date) and /Mac (dictionary, deprecated in 2.0) of the embedded
    /// file parameters: PDF-Knead does not change the data of an attachment, and these values stay as written.
    /// </summary>
    [Fact]
    public void EmbeddedFile_ParamsSizeCreationDateAndMac_KeptAsWritten()
    {
        var file = FunctionAndFileObjects.WithAttachment(streamEntries: "/Params << /Size 3 /CreationDate (D:20260101000000Z) /Mac << /Creator (ABCD) /Subtype (TEXT) >> >>");
        using var saved = Saved(file, new PdfSaveOptions { Version = "1.7" });
        var stream = Assert.IsType<PdfStream>(FunctionAndFileObjects.Spec(saved).GetDictionary(PdfName.Get("EF"))!.Get(PdfName.Get("F")));
        var parameters = stream.Dictionary.GetDictionary(PdfName.Get("Params"))!;
        Assert.Equal(3, Assert.IsType<PdfInteger>(parameters.Get(PdfName.Size)).Value);
        Assert.Equal("D:20260101000000Z", parameters.GetString(PdfName.Get("CreationDate"))!.ToText());
        var mac = parameters.GetDictionary(PdfName.Get("Mac"))!;
        Assert.Equal("ABCD", mac.GetString(PdfName.Get("Creator"))!.ToText());
        Assert.Equal("TEXT", mac.GetString(PdfName.Subtype)!.ToText());
        Assert.Equal("ABC", Encoding.ASCII.GetString(saved.GetStreamData(stream)));
    }

    /// <summary>
    /// 7.11.6, Table 47, collection subitem (sentences 15, 18, 22, 23, 25 to 28): /Type, /D (the data) and /P (a prefix that does not count when
    /// sorting) — PDF-Knead shows and sorts no collection (12.3.5, M10) and keeps the subitem as written.
    /// </summary>
    [Fact]
    public void CollectionSubitem_KeptAsWritten()
    {
        var file = FunctionAndFileObjects.WithAttachment("/CI << /Feld << /Type /CollectionSubitem /D (Wert) /P (Vorsatz) >> >>");
        using var saved = Saved(file);
        var item = FunctionAndFileObjects.Spec(saved).GetDictionary(PdfName.Get("CI"))!.GetDictionary(PdfName.Get("Feld"))!;
        Assert.Equal("CollectionSubitem", item.GetName(PdfName.Type)!.Value);
        Assert.Equal("Wert", item.GetString(PdfName.Get("D"))!.ToText());
        Assert.Equal("Vorsatz", item.GetString(PdfName.Get("P"))!.ToText());
    }

    // ---- 7.12 Extensions

    /// <summary>
    /// 7.12.2, Table 48, /Type (name, Extensions): it is no declaration — saved as 1.7, /Type /Extensions stays beside the /ADBE entry; an
    /// /Extensions of nothing but /Type and an ISO_ entry (which 1.7 cannot carry) goes whole.
    /// </summary>
    [Fact]
    public void Extensions_TypeIsNoDeclaration()
    {
        static byte[] With(string extensions) => FileOf($"<< /Type /Catalog /Pages 2 0 R /Extensions {extensions} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");
        using (var saved = Saved(With("<< /Type /Extensions /ADBE << /BaseVersion /1.7 /ExtensionLevel 8 >> >>"), new PdfSaveOptions { Version = "1.7" }))
        {
            var extensions = saved.Catalog!.GetDictionary(ExtensionDeclarations.Key)!;
            Assert.Equal("Extensions", extensions.GetName(PdfName.Type)!.Value);
            Assert.Equal(8, ExtensionDeclarations.LevelOf(extensions.Get(PdfName.Get("ADBE"))));
        }
        using var dropped = Saved(With("<< /Type /Extensions /ISO_ [<< /BaseVersion /2.0 /ExtensionLevel 32001 >>] >>"), new PdfSaveOptions { Version = "1.7" });
        Assert.Null(dropped.Catalog!.GetRaw(ExtensionDeclarations.Key));
    }

    /// <summary>
    /// 7.12.3, Table 49, /Type (name, DeveloperExtensions): PDF-Knead writes none itself; a foreign one stays in its developer extensions
    /// dictionary, which stands directly in /Extensions (7.12.1).
    /// </summary>
    [Fact]
    public void DeveloperExtensions_TypeKept_Direct()
    {
        using var saved = Saved(FileOf("<< /Type /Catalog /Pages 2 0 R /Extensions << /ADBE 4 0 R >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /DeveloperExtensions /BaseVersion /1.7 /ExtensionLevel 5 >>"), new PdfSaveOptions { Version = "1.7" });
        var entry = Assert.IsType<PdfDictionary>(saved.Catalog!.GetDictionary(ExtensionDeclarations.Key)!.GetRaw(PdfName.Get("ADBE")));
        Assert.Equal("DeveloperExtensions", entry.GetName(PdfName.Type)!.Value);
        Assert.Equal(5, ExtensionDeclarations.LevelOf(entry));
    }

    // ---- Annex E

    /// <summary>
    /// Annex E.1, sentence 9: private data may go into any dictionary as further keys, except the trailer — a file saved with an own element
    /// (PDF-Knead's PDFK_ keys in it) has none in its trailer: only the keys of Table 15 (and of a cross-reference stream, 7.5.8.2).
    /// </summary>
    [Theory]
    [InlineData("1.4")]
    [InlineData("1.7")]
    public void Writer_TrailerWithoutPrivateKeys(string version)
    {
        var bytes = FunctionAndFileObjects.SavedWithOwnElement(FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"), version);
        using var saved = PdfDocument.Open(bytes);
        Assert.Contains(FunctionAndFileObjects.Keys(saved), k => k.StartsWith("PDFK_", StringComparison.Ordinal));
        string[] allowed = ["Size", "Root", "Info", "Encrypt", "ID", "AuthCode", "Prev", "Type", "W", "Index", "Filter", "DecodeParms", "Length"];
        Assert.All(saved.Trailer.Entries.Select(e => e.Key.Value), k => Assert.Contains(k, allowed));
    }

    /// <summary>
    /// Annex E.2, sentences 26 and 30: third-class names (XX) are not registered and are for files that never leave their maker — PDF-Knead
    /// writes none of its own (its keys are second-class, PDFK_), while a foreign /XXPrivat of the catalogue stays as an unknown key.
    /// </summary>
    [Fact]
    public void Writer_NoThirdClassNamesOfItsOwn_ForeignOnesKept()
    {
        var bytes = FunctionAndFileObjects.SavedWithOwnElement(FileOf("<< /Type /Catalog /Pages 2 0 R /XXPrivat (Musterwert) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"), "1.7");
        using var saved = PdfDocument.Open(bytes);
        var keys = FunctionAndFileObjects.Keys(saved);
        Assert.Contains("PDFK_Element", keys);
        Assert.Equal(["XXPrivat"], keys.Where(k => k.StartsWith("XX", StringComparison.Ordinal)));
        Assert.Equal("Musterwert", saved.Catalog!.GetString(PdfName.Get("XXPrivat"))!.ToText());
    }
}
