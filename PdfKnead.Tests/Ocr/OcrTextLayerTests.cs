using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using PdfKnead.Tests.Pdf;
using Xunit;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Ocr;

/// <summary>The recognised text as an invisible layer: readable and searchable, the page looks the same, real text is not doubled.</summary>
public class OcrTextLayerTests
{
    /// <summary>A recognition that "reads" fixed lines, in the pixels of the image it gets.</summary>
    private sealed class Fixed(Func<OcrImage, IReadOnlyList<OcrLine>> lines) : ITextRecognizer
    {
        public int Calls { get; private set; }

        public IReadOnlyList<OcrLine> Recognize(OcrImage image, CancellationToken cancel = default)
        {
            Calls++;
            return lines(image);
        }

        public void Dispose()
        {
        }
    }

    private static OcrWord Word(string text, double x, double y, double w, double h) =>
        new(text, [new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)]);

    /// <summary>A grey image over the whole A4 page, and optionally real text.</summary>
    private static byte[] ScanPage(string text = "")
    {
        var image = new PdfStream(Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(4)), ("Height", PdfInteger.Of(4)),
            ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", N("DeviceGray"))), Enumerable.Repeat((byte)230, 16).ToArray());
        return ImageInfoTests.OnePage("q 595.28 0 0 841.89 0 0 cm /Im Do Q " + text, ("Im", image));
    }

    /// <summary>Recognises the page with the fixed lines and writes the layer; the new document.</summary>
    private static PdfDocument WithLayer(byte[] pdf, Fixed recognizer, out IReadOnlyList<OcrLine>? lines)
    {
        using var doc = PdfDocument.Open(pdf);
        var items = PageItem.From(doc);
        var found = PageRecognition.Recognize(items[0], recognizer, new ImageCache());
        lines = found?.Lines;
        var layers = new Dictionary<int, PdfStream>();
        if (found is var (l, box) && OcrTextLayer.Form(l, box) is { } form) layers[0] = form;
        var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, new PdfSaveOptions { TextLayers = layers });
        return PdfDocument.Open(output.ToArray());
    }

    [Fact]
    public void Layer_TextReadableAndInvisible_AtItsPlace_PageLooksTheSame()
    {
        var recognizer = new Fixed(image =>
        {
            // two lines at 200 dpi: "Rechnung Nr. 4711" at the top left, "Grüße" below
            var s = image.Width / 595.28;
            return
            [
                new OcrLine([Word("Rechnung", 100 * s, 100 * s, 80 * s, 14 * s), Word("Nr.", 185 * s, 100 * s, 20 * s, 14 * s), Word("4711", 210 * s, 100 * s, 30 * s, 14 * s)], 0.98),
                new OcrLine([Word("Grüße", 100 * s, 130 * s, 50 * s, 14 * s)], 0.95),
            ];
        });
        using var doc = WithLayer(ScanPage(), recognizer, out var lines);
        Assert.Equal(1, recognizer.Calls);
        Assert.Equal(2, lines!.Count);
        var text = PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[0]));
        Assert.Equal("Rechnung Nr. 4711\nGrüße", text.Text);
        Assert.All(Enumerable.Range(0, text.Text.Length).Where(i => !char.IsWhiteSpace(text.Text[i])), i => Assert.True(text.IsInvisible(i)));
        // "4711" lies where the recognition saw it: 210 … 240 pt from the left, 100 … 114 pt from the top
        var bounds = text.Quads(text.Find("4711")[0]).Select(q => q.Bounds).ToList();
        Assert.Equal(210, bounds.Min(b => b.Left), 0);
        Assert.Equal(240, bounds.Max(b => b.Right), 0);
        Assert.InRange(bounds.Min(b => b.Top), 841.89 - 114 - 3, 841.89 - 114 + 0.5);   // readers put the lower edge at the baseline or a little below
        Assert.InRange(bounds.Max(b => b.Bottom), 841.89 - 100 - 0.5, 841.89 - 100 + 0.5);
        // the layer is marked, and the page looks as before
        var xobjects = doc.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!;
        Assert.Contains(xobjects.Entries, e => xobjects.Get(e.Key) is PdfStream { Dictionary: var d } && d.ContainsKey(OcrTextLayer.Marker));
        // its font as ISO 32000-2, 9.8.1 asks: /Descent negative (docs/FEHLERGEDAECHTNIS.md, No. 773)
        var layer = xobjects.Entries.Select(e => xobjects.Get(e.Key)).OfType<PdfStream>().First(s => s.Dictionary.ContainsKey(OcrTextLayer.Marker));
        var fonts = layer.Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!;
        var type0 = fonts.Get(fonts.Entries.First().Key)!.AsDictionary()!;
        var descriptor = type0.GetArray(PdfName.Get("DescendantFonts"))!.Get(0)!.AsDictionary()!.GetDictionary(PdfName.Get("FontDescriptor"))!;
        Assert.True(descriptor.GetNumber(PdfName.Get("Descent")) < 0);
        using var before = PdfDocument.Open(ScanPage());
        Assert.Equal(Pixels(before), Pixels(doc));
    }

    private static byte[] Pixels(PdfDocument doc)
    {
        using var bitmap = RasterExport.Render(PageItem.From(doc)[0], 0.5, new ImageCache());
        return bitmap.Bytes;
    }

    [Fact]
    public void Layer_OverRealText_TheWordOnItDropped()
    {
        // real text "Seite 3" at (500, 40) in page space, 10 pt: the recognition reads it as well, and a word beside it
        var recognizer = new Fixed(image =>
        {
            var s = image.Width / 595.28;
            return [new OcrLine([Word("Anhang", 400 * s, (841.89 - 50) * s, 60 * s, 12 * s), Word("Seite", 500 * s, (841.89 - 50) * s, 25 * s, 12 * s)], 0.9)];
        });
        using var doc = WithLayer(ScanPage("BT /F1 10 Tf 500 40 Td (Seite) Tj ET"), recognizer, out var lines);
        var line = Assert.Single(lines!);
        Assert.Equal("Anhang", Assert.Single(line.Words).Text);
    }

    [Fact]
    public void PageWithoutImage_NotRead()
    {
        var recognizer = new Fixed(_ => [new OcrLine([Word("x", 1, 1, 5, 5)], 1)]);
        using var doc = PdfDocument.Open(ImageInfoTests.OnePage("BT /F1 10 Tf 100 100 Td (Text) Tj ET"));
        Assert.Null(PageRecognition.Recognize(PageItem.From(doc)[0], recognizer, new ImageCache()));
        Assert.Equal(0, recognizer.Calls);
    }

    [Fact]
    public void OldLayerOfThePage_ReplacedByTheNewOne()
    {
        var first = new Fixed(image => [new OcrLine([Word("alt", 100, 100, 100, 30)], 0.9)]);
        using var once = WithLayer(ScanPage(), first, out _);
        var second = new Fixed(image => [new OcrLine([Word("neu", 100, 100, 100, 30)], 0.9)]);
        var items = PageItem.From(once);
        var lines = PageRecognition.Recognize(items[0], second, new ImageCache())!.Value.Lines;
        var output = new MemoryStream();
        PdfWriter.Write(once, items, output, new PdfSaveOptions
        {
            TextLayers = new Dictionary<int, PdfStream> { [0] = OcrTextLayer.Form(lines, PageInterpreter.Build(once, once.Pages[0]).PageBox)! },
            ScopedCleanup = new CleanupScope(CleanupItems.InvisibleText, new HashSet<int> { 0 }),
        });
        using var twice = PdfDocument.Open(output.ToArray());
        Assert.Equal("neu", PageText.Create(PageInterpreter.BuildText(twice, twice.Pages[0])).Text);
    }

    [Fact]
    public void Layer_CharactersBeyondTheBasicPlane_AndCombiningMarks_ComeBackAsWritten()
    {
        // "été" with a combining accent on the first e (one character for the reader), and two letters beyond the basic plane
        const string decomposed = "e\u0301t\u00E9", beyond = "\U0001D504\U0001D505";
        var recognizer = new Fixed(_ => [new OcrLine([Word(decomposed, 100, 100, 200, 40), Word(beyond, 320, 100, 100, 40)], 0.9)]);
        using var doc = WithLayer(ScanPage(), recognizer, out _);
        Assert.Equal("\u00E9t\u00E9 " + beyond, PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[0])).Text.Normalize(NormalizationForm.FormC));
    }

    [Fact]
    public void Layer_DegenerateOrEmptyWords_Skipped()
    {
        Assert.Null(OcrTextLayer.Form([new OcrLine([Word("   ", 1, 1, 5, 5), Word("a", 1, 1, 0, 0), new OcrWord("b", [])], 0.9)], new(0, 0, 100, 100)));
    }

    [Fact]
    public void GlyphlessFont_ParsesWithTwoEmptyGlyphs()
    {
        var font = OpenTypeFont.Parse(OcrTextLayer.Glyphless);
        Assert.Equal(2, font.GlyphCount);
        Assert.Equal(1024, font.UnitsPerEm);   // a power of two (docs/FEHLERGEDAECHTNIS.md, No. 1166)
    }
}
