using System.Diagnostics;
using System.Runtime.ExceptionServices;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M8, FG 1252 (security; found in the sentence-by-sentence draft of ISO 32000-2, 10.6.5.6): the PDF/A check and the PDF/A writer went down
/// into the colourants of a type 5 halftone that are themselves of type 5 — up to depth 4 with 64 entries each and without noticing an object
/// already visited. One type 5 halftone whose 64 colourants name itself made about 64⁵ ≈ 1.07 billion calls that could not be cancelled. A
/// type 5 halftone within a type 5 halftone is not allowed (10.6.5.6): only the colourants of the halftone of the graphics state are looked at.
/// </summary>
public sealed class M8Fg1252Tests
{
    /// <summary>A page whose graphics state names a type 5 halftone (without a transfer function, as the halftone of the graphics state) whose
    /// 64 colourants are a second type 5 halftone, which names itself as its 64 colourants (with a transfer function, as non-primary
    /// colourants) — every halftone met is allowed by itself, so the old check and writer went on to depth 4.</summary>
    private static PdfDocument SelfNamingHalftone()
    {
        var pdf = new TestPdf();
        var cat = TestPdf.Dict(("Type", TestPdf.N("Catalog")));
        var catNumber = pdf.Add(cat);
        var pagesNumber = pdf.Add(new PdfDictionary());
        var innerNumber = pdf.Add(new PdfDictionary());
        var inner = TestPdf.Dict(("Type", TestPdf.N("Halftone")), ("HalftoneType", PdfInteger.Of(5)), ("TransferFunction", TestPdf.N("Identity")));
        for (var i = 0; i < 64; i++) inner.Set(PdfName.Get($"Ink{i:00}"), TestPdf.Ref(innerNumber));
        pdf.Replace(innerNumber, inner);
        var outer = TestPdf.Dict(("Type", TestPdf.N("Halftone")), ("HalftoneType", PdfInteger.Of(5)));
        for (var i = 0; i < 64; i++) outer.Set(PdfName.Get($"Ink{i:00}"), TestPdf.Ref(innerNumber));
        var halftoneNumber = pdf.Add(outer);
        var state = TestPdf.Dict(("Type", TestPdf.N("ExtGState")), ("HT", TestPdf.Ref(halftoneNumber)));
        var content = pdf.Add(TestPdf.FlateStream("/GS0 gs 0.5 g 20 20 160 160 re f"u8.ToArray()));
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pagesNumber)), ("MediaBox", TestPdf.Box(0, 0, 200, 200)),
            ("Resources", TestPdf.Dict(("ExtGState", TestPdf.Dict(("GS0", state))))), ("Contents", TestPdf.Ref(content))));
        pdf.Replace(pagesNumber, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray([TestPdf.Ref(page)])), ("Count", PdfInteger.Of(1))));
        cat.Set(PdfName.Pages, TestPdf.Ref(pagesNumber));
        pdf.RootNumber = catNumber;
        return PdfDocument.Open(pdf.Build());
    }

    /// <summary>Runs <paramref name="action"/> on a thread of its own and fails when it takes longer than <paramref name="limitMs"/> (the
    /// thread pool may be busy in the full run; the old state would run for hours, the thread is left behind as a background thread).</summary>
    private static void Within(string what, int limitMs, Action action)
    {
        ExceptionDispatchInfo? error = null;
        var watch = Stopwatch.StartNew();
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { error = ExceptionDispatchInfo.Capture(e); }
        }) { IsBackground = true };
        thread.Start();
        var done = thread.Join(limitMs);
        Assert.True(done, $"{what}: not finished after {limitMs} ms");
        error?.Throw();
        Assert.True(watch.ElapsedMilliseconds < limitMs, $"{what}: {watch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// 10.6.5.6 sentences 5–7 (a colourant of a type 5 halftone is not itself of type 5), checker and writer: the halftones are checked and
    /// written within seconds (before: about 64⁴ calls each, hours); the writer leaves the halftone out (a type 5 colourant is not allowed).
    /// </summary>
    [Fact]
    public void Fg1252_Type5HalftoneNamingItself_CheckAndPdfAWriterFinishQuickly()
    {
        using var doc = SelfNamingHalftone();
        Within("PDF/A check", 5000, () => _ = PdfACheck.Check(doc, PdfAConformance.A2b));

        byte[] written = [];
        Within("PDF/A writer", 10000, () => written = PdfAExport.Write(doc, PageItem.From(doc), PdfAConformance.A2b).Bytes);
        using var copy = PdfDocument.Open(written);
        var states = copy.Pages[0].Resources?.GetDictionary(PdfName.Get("ExtGState"));
        Assert.NotNull(states);
        foreach (var (key, _) in states.Entries)
            Assert.False((states.Get(key) as PdfDictionary)?.ContainsKey(PdfName.Get("HT")) ?? false);
    }
}
