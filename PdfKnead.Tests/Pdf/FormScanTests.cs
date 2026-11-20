using System.IO.Compression;
using PdfKnead.Ai;
using PdfKnead.Core.Forms.Recognition;
using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// „Felder erkennen“ in scans and with the field detector (KONZEPT.md, section 5.9a): lines and boxes found in the pixels, labels from the
/// text recognition, fields only the detector sees, the answers of the network and the scaling of its input — with stand-ins for the models.
/// </summary>
public sealed class FormScanTests
{
    private const double PageWidth = 595.28, PageHeight = 841.89;
    private const int PixelWidth = 1654, PixelHeight = 2339;

    /// <summary>A scanned A4 page at 200 dpi: white, with what <paramref name="draw"/> blackens (in points, y upwards).</summary>
    internal static byte[] Scan(Action<Action<double, double, double, double>> draw) => Scan((fill, _) => draw(fill));

    /// <summary>A scanned A4 page: what <paramref name="draw"/> blackens with its first action and whitens with its second.</summary>
    internal static byte[] Scan(Action<Action<double, double, double, double>, Action<double, double, double, double>> draw)
    {
        var gray = new byte[PixelWidth * PixelHeight];
        Array.Fill(gray, (byte)255);
        void Paint(double left, double bottom, double right, double top, byte value)
        {
            int x0 = (int)(left / PageWidth * PixelWidth), x1 = (int)(right / PageWidth * PixelWidth);
            int y0 = (int)((PageHeight - top) / PageHeight * PixelHeight), y1 = (int)((PageHeight - bottom) / PageHeight * PixelHeight);
            for (var y = Math.Max(0, y0); y <= Math.Min(PixelHeight - 1, y1); y++)
                for (var x = Math.Max(0, x0); x <= Math.Min(PixelWidth - 1, x1); x++) gray[y * PixelWidth + x] = value;
        }
        draw((l, b, r, t) => Paint(l, b, r, t, 20), (l, b, r, t) => Paint(l, b, r, t, 255));
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Fastest)) z.Write(gray);
        using var output = new MemoryStream();
        var builder = new RasterPdfBuilder(output);
        builder.AddPage(PageWidth, PageHeight, packed.ToArray(), "FlateDecode", PixelWidth, PixelHeight, gray: true);
        builder.Finish();
        return output.ToArray();
    }

    /// <summary>A text recognition that „reads“ given words at given places (in points, y upwards: left, bottom, right, top).</summary>
    internal sealed class Reader(params (string Text, double Left, double Bottom, double Right, double Top)[] words) : ITextRecognizer
    {
        public IReadOnlyList<OcrLine> Recognize(OcrImage image, CancellationToken cancel = default)
        {
            OcrPoint P(double x, double y) => new(x / PageWidth * image.Width, (PageHeight - y) / PageHeight * image.Height);
            return [.. words.Select(w => new OcrLine([new OcrWord(w.Text, [P(w.Left, w.Top), P(w.Right, w.Top), P(w.Right, w.Bottom), P(w.Left, w.Bottom)])], 0.95))];
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A field detector that „sees“ given fields (in points, y upwards).</summary>
    internal sealed class Detector(params (DetectedFieldKind Kind, double Left, double Bottom, double Right, double Top, double Score)[] fields) : IFieldDetector
    {
        public int Calls { get; private set; }

        public IReadOnlyList<DetectedField> Detect(OcrImage image, CancellationToken cancel = default)
        {
            Calls++;
            return [.. fields.Select(f => new DetectedField(f.Left / PageWidth * image.Width, (PageHeight - f.Top) / PageHeight * image.Height,
                f.Right / PageWidth * image.Width, (PageHeight - f.Bottom) / PageHeight * image.Height, f.Kind, f.Score))];
        }

        public void Dispose()
        {
        }
    }

    private static List<FieldProposal> Recognize(byte[] data, ITextRecognizer? reader, IFieldDetector? detector)
    {
        using var doc = PdfDocument.Open(data);
        var items = PageItem.From(doc);
        var images = new ImageCache(16L << 20);
        return FormRecognizer.Recognize([.. items.Select(i => new FormRecognizer.PageInput(doc, i.Page, [],
            c => PageFormScan.Look(i, reader, detector, images, c)?.Scan))], new HashSet<string>());
    }

    [Fact]
    public void ScannedLineWithReadLabel_TextFieldNamedAfterIt()
    {
        var pdf = Scan(fill => fill(110, 697.5, 400, 698.5));
        var found = Assert.Single(Recognize(pdf, new Reader(("Name:", 72, 698, 104, 708)), null));
        Assert.Equal(FormFieldKind.Text, found.Spec.Kind);
        Assert.Equal("Name", found.Spec.Name);
        Assert.Equal(ProposalSource.Line, found.Source);
        Assert.InRange(found.Rect.Left, 108, 113);
        Assert.InRange(found.Rect.Right, 397, 402);
        Assert.InRange(found.Rect.Bottom, 697, 701);
    }

    [Fact]
    public void ScannedBox_CheckBoxWithItsLabel_ThickBarAndLettersAreNoLines()
    {
        var pdf = Scan(fill =>
        {
            // a box of 10 points with a hairline frame
            fill(72, 600, 82, 600.6);
            fill(72, 609.4, 82, 610);
            fill(72, 600, 72.6, 610);
            fill(81.4, 600, 82, 610);
            // a black bar (a heading's background): no line to write on
            fill(72, 760, 500, 780);
            // the feet of a row of letters: short pieces with gaps, no line
            for (var x = 72.0; x < 300; x += 6) fill(x, 500, x + 3, 500.6);
        });
        var found = Recognize(pdf, new Reader(("Newsletter", 86, 600, 130, 610)), null);
        var box = Assert.Single(found);
        Assert.Equal(FormFieldKind.CheckBox, box.Spec.Kind);
        Assert.Equal("Newsletter", box.Spec.Name);
    }

    [Fact]
    public void ScannedCombLine_CellsFromItsStrokes()
    {
        var pdf = Scan(fill =>
        {
            fill(110, 699.6, 393.4, 700.4);
            for (var k = 0; k <= 20; k++) fill(110 + k * 14.17 - 0.3, 700, 110 + k * 14.17 + 0.3, 702.8);
        });
        var found = Assert.Single(Recognize(pdf, new Reader(("Name*", 72, 700, 100, 708)), null));
        Assert.Equal("Name", found.Spec.Name);
        Assert.True(found.Spec.Comb);
        Assert.Equal(20, found.Spec.MaxLength);
    }

    [Fact]
    public void WhiteLettersOnADarkArea_NoLinesNoBoxes()
    {
        // white text on a dark area: rows of „letters“ (white slits) with solid dark bands between them — a band is thin and long, but dark
        // around it, no line on paper
        var pdf = Scan((fill, clear) =>
        {
            fill(300, 300, 520, 361);
            foreach (var (from, to) in new[] { (300.0, 320.0), (321.0, 340.0), (341.0, 361.0) })
                for (var x = 301.0; x < 519; x += 3) clear(x, from, x + 1.2, to);
        });
        Assert.Empty(Recognize(pdf, null, null));
    }

    [Fact]
    public void Detector_ConfirmsADrawnField_AndAddsOneNothingShows()
    {
        var pdf = Scan(fill => fill(110, 697.5, 400, 698.5));
        var detector = new Detector(
            (DetectedFieldKind.Text, 112, 699, 398, 712, 0.9),      // on the line: confirms it
            (DetectedFieldKind.Text, 150, 640, 400, 655, 0.8),      // nothing drawn there: only the detector sees it
            (DetectedFieldKind.Choice, 72, 600, 82, 610, 0.7));     // a box the scan does not show
        var found = Recognize(pdf, new Reader(("Name:", 72, 698, 104, 708), ("Ort:", 110, 643, 140, 653), ("Newsletter", 86, 601, 130, 609)), detector);
        Assert.Equal(1, detector.Calls);
        var name = found.Single(f => f.Spec.Name == "Name");
        Assert.Equal(ProposalSource.Line, name.Source);
        Assert.True(name.Confidence > 0.8);   // both saw it
        var place = found.Single(f => f.Spec.Name == "Ort");
        Assert.Equal(ProposalSource.Model, place.Source);
        Assert.InRange(place.Confidence, 0.6, 0.75);
        var box = found.Single(f => f.Spec.Name == "Newsletter");
        Assert.Equal(FormFieldKind.CheckBox, box.Spec.Kind);
        Assert.Equal(ProposalSource.Model, box.Source);
    }

    [Fact]
    public void Detector_TextFieldOverALine_StandsOnTheLine()
    {
        var pdf = Scan(fill => fill(200, 497.5, 450, 498.5));
        // the detector's box sits a little above the line and is shorter: the field takes the line
        var found = Recognize(pdf, new Reader(("Ihre Anschrift", 72, 498, 150, 508)), new Detector((DetectedFieldKind.Text, 210, 501, 430, 515, 0.8)));
        var field = Assert.Single(found);
        Assert.Equal("Ihre Anschrift", field.Spec.Name);
        Assert.InRange(field.Rect.Bottom, 497, 500);
        Assert.InRange(field.Rect.Left, 198, 203);
    }

    [Fact]
    public void Detector_Signature_BecomesASignatureField()
    {
        var pdf = Scan(_ => { });
        var found = Recognize(pdf, new Reader(("Kunde", 300, 560, 340, 570)), new Detector((DetectedFieldKind.Signature, 300, 575, 500, 610, 0.8)));
        Assert.Equal(FormFieldKind.Signature, Assert.Single(found).Spec.Kind);
    }

    [Fact]
    public void PageWithRealText_DetectorLooks_NoTextIsRead()
    {
        var reader = new CountingReader();
        var detector = new Detector();
        var pdf = TestPdf.Simple(1).Build();
        Recognize(pdf, reader, detector);
        Assert.Equal(0, reader.Calls);   // not a scan: nothing to read
        Assert.Equal(1, detector.Calls);
    }

    private sealed class CountingReader : ITextRecognizer
    {
        public int Calls { get; private set; }

        public IReadOnlyList<OcrLine> Recognize(OcrImage image, CancellationToken cancel = default)
        {
            Calls++;
            return [];
        }

        public void Dispose()
        {
        }
    }

    [Trait(TestKind.Name, TestKind.Time)]
    [Fact(Timeout = 60_000)]
    public async Task NoiseAndBlackScans_QuickWithoutException_Bounded()
    {
        await Task.Run(() =>
        {
            var random = new Random(7);
            // salt and pepper all over the page, then lines of every length and thickness, then a page that is all ink
            var noise = Scan(fill =>
            {
                for (var i = 0; i < 40_000; i++)
                {
                    double x = random.NextDouble() * PageWidth, y = random.NextDouble() * PageHeight;
                    fill(x, y, x + random.NextDouble() * (i % 7 == 0 ? 60 : 2), y + random.NextDouble() * (i % 11 == 0 ? 60 : 2));
                }
            });
            var black = Scan(fill => fill(0, 0, PageWidth, PageHeight));
            foreach (var pdf in new[] { noise, black })
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var found = Recognize(pdf, null, new Detector((DetectedFieldKind.Text, 0, 0, PageWidth, PageHeight, 0.9)));
                Assert.True(found.Count <= FormRecognizer.MaxPerPage);
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20 * TestKind.Factor), $"{watch.Elapsed}");
            }
        });
    }

    [Fact]
    public void Network_Answers_ScoredScaledAndThinned()
    {
        // four queries: a sure text field, a weaker one overlapping it, a check box, one below the threshold; 91 classes as the network gives
        const int width = 91;
        float[] boxes = [0.5f, 0.5f, 0.2f, 0.1f, 0.51f, 0.5f, 0.2f, 0.1f, 0.1f, 0.2f, 0.02f, 0.02f, 0.9f, 0.9f, 0.1f, 0.1f];
        var logits = new float[4 * width];
        Array.Fill(logits, -10f);
        logits[0 * width + 0] = 3f;     // text, 0.95
        logits[1 * width + 0] = 1f;     // text, 0.73, overlaps the first
        logits[2 * width + 1] = 2f;     // choice, 0.88
        logits[3 * width + 2] = -2f;    // signature, 0.12: too weak
        logits[3 * width + 50] = 5f;    // an unused class: ignored
        var fields = FfdetrDetector.Fields(boxes, logits, 4, width, 1000, 2000, new FfdetrOptions());
        Assert.Equal(2, fields.Count);
        Assert.Equal(DetectedFieldKind.Text, fields[0].Kind);
        Assert.Equal(400, fields[0].Left, 3);
        Assert.Equal(900, fields[0].Top, 3);
        Assert.Equal(600, fields[0].Right, 3);
        Assert.Equal(1100, fields[0].Bottom, 3);
        Assert.Equal(DetectedFieldKind.Choice, fields[1].Kind);
        // damaged answers: NaN, negative sizes and short arrays give nothing, never an exception
        float[] bad = [float.NaN, 0.5f, 0.1f, 0.1f, 0.5f, 0.5f, -0.1f, 0.1f];
        Assert.Empty(FfdetrDetector.Fields(bad, logits.AsSpan(0, 2 * width), 2, width, 100, 100, new FfdetrOptions()));
        Assert.Empty(FfdetrDetector.Fields(boxes, logits.AsSpan(0, 10), 4, width, 100, 100, new FfdetrOptions()));
    }

    [Fact]
    public void NetworkInput_Scaled_WeightsSumToOne_PlainPageStaysPlain()
    {
        foreach (var (from, to) in new[] { (2339, 1024), (1654, 1024), (500, 1024), (1, 1024), (1024, 1024) })
        {
            var (spans, weights) = FfdetrDetector.Kernel(from, to);
            Assert.Equal(to, spans.Length);
            for (var i = 0; i < to; i++)
            {
                Assert.Equal(1.0, weights[i].Sum(), 4);
                Assert.InRange(spans[i].First, 0, from - 1);
                Assert.InRange(spans[i].First + spans[i].Count, 1, from);
            }
        }
        // a white page: every value is the normalised white of its channel
        var rgba = new byte[300 * 400 * 4];
        Array.Fill(rgba, (byte)255);
        var input = FfdetrDetector.Input(new OcrImage(300, 400, rgba));
        Assert.Equal(3 * FfdetrDetector.Side * FfdetrDetector.Side, input.Length);
        Assert.All(new[] { 0, FfdetrDetector.Side * FfdetrDetector.Side + 5, input.Length - 1 }, i => Assert.InRange(input[i], 2.0f, 2.7f));
    }

    /// <summary>A printed A4 page with Helvetica.</summary>
    private static byte[] Printed(string content) => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R>>",
        [2] = "<</Type/Pages/Kids[10 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
        [4] = ("", content),
        [10] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 3 0 R>>>>>>",
    });

    /// <summary>The models of the test folder (PDFKNEAD_OCR_MODELLE, as for the text recognition); the tests never load anything.</summary>
    private static string? Models => Environment.GetEnvironmentVariable("PDFKNEAD_OCR_MODELLE") is { Length: > 0 } m &&
                                     File.Exists(Path.Combine(m, OcrModels.FieldDetector.Folder, OcrModels.FieldDetector.Name)) ? m : null;

    [Fact]
    public void RealDetector_FindsTheFieldsOfAPrintedForm()
    {
        if (Models is not { } models) return;
        using var detector = new FfdetrDetector(Path.Combine(models, OcrModels.FieldDetector.Folder, OcrModels.FieldDetector.Name));
        // a printed form: three lines with labels and two boxes
        var pdf = Printed("BT /F1 11 Tf 72 700 Td (Name:) Tj ET 0.7 w 120 698 m 450 698 l S " +
                                            "BT /F1 11 Tf 72 660 Td (Strasse:) Tj ET 0.7 w 120 658 m 450 658 l S " +
                                            "BT /F1 11 Tf 72 620 Td (Ort:) Tj ET 0.7 w 120 618 m 450 618 l S " +
                                            "0.7 w 72 570 10 10 re S BT /F1 11 Tf 88 572 Td (Ja) Tj ET 0.7 w 140 570 10 10 re S BT /F1 11 Tf 156 572 Td (Nein) Tj ET");
        using var doc = PdfDocument.Open(pdf);
        var item = PageItem.From(doc)[0];
        var list = PageInterpreter.Build(doc, item.Page, withoutOwnElements: true);
        var (image, _) = PageRecognition.Render(item, list, new ImageCache(16L << 20));
        var fields = detector.Detect(image);
        Assert.True(fields.Count(f => f.Kind == DetectedFieldKind.Text) >= 3, $"{fields.Count} Felder");
        Assert.True(fields.Count(f => f.Kind == DetectedFieldKind.Choice) >= 2, $"{fields.Count} Felder");
    }
}
