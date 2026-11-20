using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Barcodes.DataMatrix;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Changing a code that is part of the page (KONZEPT.md, section 5.19, step 3): the plan puts the new code exactly where the old one is
/// (place, size, turn, settings), the old one and its text go as when redacting (white), and afterwards only the new code is on the page.
/// </summary>
public sealed class CodeReplacementTests
{
    private static byte[] Save(PdfDocument doc, IReadOnlyList<PageItem> pages, PdfSaveOptions? options = null)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(doc, pages, output, options);
        return output.ToArray();
    }

    public static TheoryData<BarcodeFormat, string, bool, int> Cases()
    {
        var data = new TheoryData<BarcodeFormat, string, bool, int>();
        foreach (var rotation in new[] { 0, 90, 180, 270 })
        {
            data.Add(BarcodeFormat.Qr, "https://example.org/alt", false, rotation);
            data.Add(BarcodeFormat.DataMatrix, "(01)04012345678901(17)261231", true, rotation);
            data.Add(BarcodeFormat.Ean13, "4006381333931", false, rotation);
            data.Add(BarcodeFormat.Code128, "RE-2026-0815", false, rotation);
            data.Add(BarcodeFormat.Ean8, "52364228", false, rotation);
            data.Add(BarcodeFormat.UpcA, "036000291452", false, rotation);
            data.Add(BarcodeFormat.UpcE, "01234565", false, rotation);
            data.Add(BarcodeFormat.Gs1_128, "(01)04012345678901(10)A1", false, rotation);
            data.Add(BarcodeFormat.Code39, "PDF-KNEAD 42", false, rotation);
            data.Add(BarcodeFormat.Itf, "12345678901231", false, rotation);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Replaced_SamePlaceSizeAndTurn_OnlyTheNewOneLeft(BarcodeFormat format, string text, bool gs1, int rotation)
    {
        // a code in the page content: an inserted one, flattened
        var original = new CodeElement { Format = format, Text = text, Gs1 = gs1, Rect = default, Shape = DataMatrixShape.Rectangular };
        var (w, h) = ElementAppearance.DefaultCodeSize(original);
        if (rotation is 90 or 270) (w, h) = (h, w);
        original = original with { Rect = new PdfRectangle(150, 400, 150 + w, 400 + h), Rotation = rotation };
        using var blank = BlankPage.CreateDocument(595, 842, 1);
        // text beside it that must stay
        using var doc = PdfDocument.Open(Save(blank, [PageItem.From(blank)[0].WithElements([original,
            new TextElement { Rect = new PdfRectangle(150, 300, 450, 330), Text = "Bleibt stehen" }])], new PdfSaveOptions { Flatten = true }));
        var page = doc.Pages[0];
        var list = PageInterpreter.Build(doc, page);
        var found = Assert.Single(PageBarcodes.FindAll(doc, page, list, [], new ImageCache()));

        var plan = CodeReplacement.Plan(doc, page, list, found, out var refusal);
        Assert.Null(refusal);
        Assert.NotNull(plan);
        var planned = plan.Element;
        Assert.Equal((format, text, gs1, rotation), (planned.Format, planned.Text, planned.Gs1, planned.Rotation));
        Assert.Equal(original.Caption && !original.IsMatrix, planned.Caption);
        Assert.Equal(text, planned.Replaces);
        foreach (var (a, b) in new[] { (original.Rect.Left, planned.Rect.Left), (original.Rect.Bottom, planned.Rect.Bottom), (original.Rect.Right, planned.Rect.Right),
                     (original.Rect.Top, planned.Rect.Top) })
            Assert.InRange(b, a - 1.5, a + 1.5);

        // replaced with another content (a valid one of the same kind)
        var changed = format switch
        {
            BarcodeFormat.Qr => "https://example.org/neu",
            BarcodeFormat.DataMatrix => "(01)04012345678901(17)271231",
            BarcodeFormat.Ean13 => "4012345678901",
            BarcodeFormat.Ean8 => "96385074",
            BarcodeFormat.UpcA => "012345678905",
            BarcodeFormat.UpcE => "04252614",
            BarcodeFormat.Gs1_128 => "(01)04012345678901(10)B2",
            BarcodeFormat.Code39 => "GEAENDERT 7",
            BarcodeFormat.Itf => "98765432109876",
            _ => "RE-2027-0001",
        };
        var emptied = Redactor.Apply(doc, page, [plan.Area], paper: true);
        var item = PageItem.From(doc)[0];
        using var saved = PdfDocument.Open(Save(doc, [item.WithEmptiedArea(emptied, [.. item.Elements, planned with { Text = changed }])]));
        var after = PageBarcodes.FindAll(saved, saved.Pages[0], PageInterpreter.Build(saved, saved.Pages[0]), [], new ImageCache());
        var expected = CodeSymbol.Create(format, changed, gs1, shape: DataMatrixShape.Rectangular).Encoded;
        Assert.Equal(expected, Assert.Single(after).Code.Text);
        // the text beside it stays; the old human-readable digits of the bar code are gone
        var pageText = string.Concat(PageInterpreter.Build(saved, saved.Pages[0]).Text.Select(g => g.Text));
        Assert.Contains("Bleibt stehen", pageText);
        if (format == BarcodeFormat.Ean13) Assert.DoesNotContain("006381", pageText.Replace(" ", ""));
    }

    [Fact]
    public void Turned_Askew_Refused()
    {
        // a QR code at 30° in the page content
        var qr = Core.Barcodes.Qr.QrEncoder.Encode("Schräg", Core.Barcodes.Qr.QrErrorCorrection.M);
        var content = new System.Text.StringBuilder("q 0.866 0.5 -0.5 0.866 250 400 cm 0 g\n");
        for (var y = 0; y < qr.Height; y++)
            for (var x = 0; x < qr.Width; x++)
                if (qr[x, y]) content.Append(FormattableString.Invariant($"{x * 3} {(qr.Height - 1 - y) * 3} 3 3 re\n"));
        content.Append("f Q\n");
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(content.ToString())));
        using var doc = PdfDocument.Open(pdf.Build());
        var list = PageInterpreter.Build(doc, doc.Pages[0]);
        var found = PageBarcodes.FindAll(doc, doc.Pages[0], list, [], new ImageCache()).Single(f => f.Code.Text == "Schräg");
        Assert.Null(CodeReplacement.Plan(doc, doc.Pages[0], list, found, out var refusal));
        Assert.Contains("30", refusal);
    }

    [Fact]
    public void ImageCode_WithLinkOverIt_PixelsAndLinkGo()
    {
        // a QR code as a grey image (three pixels per module), a link to its address over it, text beside it
        var qr = Core.Barcodes.Qr.QrEncoder.Encode("https://example.org/alt", Core.Barcodes.Qr.QrErrorCorrection.M);
        const int k = 3, q = 4;
        int size = (qr.Width + 2 * q) * k;
        var pixels = new byte[size * size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                int mx = x / k - q, my = y / k - q;
                pixels[y * size + x] = mx >= 0 && my >= 0 && mx < qr.Width && my < qr.Height && qr[mx, my] ? (byte)0 : (byte)255;
            }
        var pdf = TestPdf.Simple(1);
        var image = pdf.Add(TestPdf.FlateStream(pixels, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", Core.Pdf.Objects.PdfInteger.Of(size)), ("Height", Core.Pdf.Objects.PdfInteger.Of(size)), ("BitsPerComponent", Core.Pdf.Objects.PdfInteger.Of(8)),
            ("ColorSpace", TestPdf.N("DeviceGray")))));
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes("q 100 0 0 100 300 120 cm /Q Do Q BT /F1 12 Tf 300 90 Td (Bleibt stehen) Tj ET")));
        var link = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Link")), ("Rect", TestPdf.Box(300, 120, 400, 220)),
            ("A", TestPdf.Dict(("S", TestPdf.N("URI")), ("URI", new Core.Pdf.Objects.PdfString("https://example.org/alt"u8.ToArray()))))));
        var pageDict = (Core.Pdf.Objects.PdfDictionary)pdf.Get(5);
        pageDict.Set(Core.Pdf.Objects.PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))), ("XObject", TestPdf.Dict(("Q", TestPdf.Ref(image))))));
        pageDict.Set(Core.Pdf.Objects.PdfName.Get("Annots"), new Core.Pdf.Objects.PdfArray([TestPdf.Ref(link)]));
        using var doc = PdfDocument.Open(pdf.Build());
        var page = doc.Pages[0];
        var list = PageInterpreter.Build(doc, page);
        var found = Assert.Single(PageBarcodes.FindAll(doc, page, list, [], new ImageCache()));
        var plan = CodeReplacement.Plan(doc, page, list, found, out _)!;
        var emptied = Redactor.Apply(doc, page, [plan.Area], paper: true);
        Assert.Single(emptied.DroppedAnnotations);
        var item = PageItem.From(doc)[0];
        var saved = Save(doc, [item.WithEmptiedArea(emptied, [.. item.Elements, plan.Element with { Text = "https://example.org/neu" }])]);
        using var after = PdfDocument.Open(saved);
        var afterList = PageInterpreter.Build(after, after.Pages[0]);
        Assert.Equal("https://example.org/neu", Assert.Single(PageBarcodes.FindAll(after, after.Pages[0], afterList, [], new ImageCache())).Code.Text);
        Assert.Contains("Bleibt stehen", string.Concat(afterList.Text.Select(g => g.Text)));
        // the old address is nowhere in the file any more (the link went, the image is a new one without the code)
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(saved, ["example.org/alt"], null, out _));
    }

    /// <summary>
    /// Bars drawn in three pieces one above the other with hairline gaps (as some typesetting programs do): the whole height is replaced,
    /// not only the piece in the middle — the old code would stay readable above and below the new one.
    /// </summary>
    [Fact]
    public void BarsInPieces_WholeHeightReplaced()
    {
        var bars = Core.Barcodes.Linear.LinearCodes.Encode(Core.Barcodes.Linear.LinearSymbology.Ean13, "9780111193341");
        var content = new System.Text.StringBuilder("0 g\n");
        const double m = 1.1, x0 = 400, y0 = 100;
        foreach (var (bottom, top) in new[] { (y0, y0 + 25), (y0 + 25.3, y0 + 50), (y0 + 50.3, y0 + 75) })
            for (var i = 0; i < bars.Length; i++)
                if (bars[i]) content.Append(FormattableString.Invariant($"{x0 + i * m:0.###} {bottom:0.###} {m:0.###} {top - bottom:0.###} re\n"));
        content.Append("f\n");
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(content.ToString())));
        using var doc = PdfDocument.Open(pdf.Build());
        var page = doc.Pages[0];
        var list = PageInterpreter.Build(doc, page);
        var found = PageBarcodes.FindAll(doc, page, list, [], new ImageCache()).Single(f => f.Code.Text == "9780111193341");
        var plan = CodeReplacement.Plan(doc, page, list, found, out _)!;
        Assert.InRange(plan.Area.Bottom, y0 - 2, y0 + 1);
        Assert.InRange(plan.Area.Top, y0 + 74, y0 + 77);
        var emptied = Redactor.Apply(doc, page, [plan.Area], paper: true);
        var item = PageItem.From(doc)[0];
        using var saved = PdfDocument.Open(Save(doc, [item.WithEmptiedArea(emptied, [.. item.Elements, plan.Element with { Text = "4012345678901" }])]));
        var after = PageBarcodes.FindAll(saved, saved.Pages[0], PageInterpreter.Build(saved, saved.Pages[0]), [], new ImageCache());
        Assert.Contains(after, f => f.Code.Text == "4012345678901");
        Assert.DoesNotContain(after, f => f.Code.Text == "9780111193341");
    }
}
