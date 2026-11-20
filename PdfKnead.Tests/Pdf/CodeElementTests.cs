using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Barcodes.DataMatrix;
using PdfKnead.Core.Barcodes.Qr;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Inserted codes (KONZEPT.md, section 5.19, step 3): every kind drawn as vector graphics and read back by the own reader from the drawn
/// page, saved as an annotation that comes back as an element with all settings, flattened into the page content, GS1 data in brackets.
/// </summary>
public sealed class CodeElementTests
{
    private static PdfDocument Save(PdfDocument main, IReadOnlyList<PageItem> pages, PdfSaveOptions? options = null)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(main, pages, output, options);
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { Length: > 0 } folder)
            File.WriteAllBytes(Path.Combine(folder, $"codes-{(options?.Flatten == true ? "flach" : "anmerkungen")}.pdf"), output.ToArray());
        return PdfDocument.Open(output.ToArray());
    }

    /// <summary>A code in its default size with the lower left corner at (x, y).</summary>
    private static CodeElement At(CodeElement code, double x, double y, int rotation = 0)
    {
        var (w, h) = ElementAppearance.DefaultCodeSize(code);
        return code with { Rect = rotation is 90 or 270 ? new PdfRectangle(x, y, x + h, y + w) : new PdfRectangle(x, y, x + w, y + h), Rotation = rotation };
    }

    public static TheoryData<BarcodeFormat, string, string> Kinds() => new()
    {
        { BarcodeFormat.Qr, "https://example.org/rechnung?nr=4711", "https://example.org/rechnung?nr=4711" },
        { BarcodeFormat.DataMatrix, "Charge 2026/17", "Charge 2026/17" },
        { BarcodeFormat.Ean13, "400638133393", "4006381333931" },          // the check digit is added
        { BarcodeFormat.Ean8, "9638507", "96385074" },
        { BarcodeFormat.UpcA, "03600029145", "036000291452" },
        { BarcodeFormat.UpcE, "0123456", "01234565" },
        { BarcodeFormat.Code128, "RE-2026-0815", "RE-2026-0815" },
        { BarcodeFormat.Gs1_128, "(01)0401234567890(10)A1", "010401234567890110A1" },
        { BarcodeFormat.Code39, "PDF-KNEAD 42", "PDF-KNEAD 42" },
        { BarcodeFormat.Itf, "12345678901231", "12345678901231" },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EveryKind_DrawnOnThePage_ReadBack(BarcodeFormat format, string text, string expected)
    {
        var symbol = CodeSymbol.Create(format, text);
        Assert.Equal(expected, symbol.Encoded);
        using var doc = BlankPage.CreateDocument(595, 842, 1);
        var codes = new List<PageElement> { At(new CodeElement { Format = format, Text = text, Rect = default }, 100, 500) };
        // and turned with the page reading direction
        codes.Add(At(new CodeElement { Format = format, Text = text, Rect = default }, 100, 150, 90));
        using var saved = Save(doc, [PageItem.From(doc)[0].WithElements(codes)]);
        var page = saved.Pages[0];
        var found = PageBarcodes.FindAll(saved, page, PageInterpreter.Build(saved, page), [], new ImageCache());
        Assert.Equal(2, found.Count(f => f.Code.Text == expected && f.Code.Format == format));
    }

    [Fact]
    public void Saved_ComesBackWithAllSettings_AltTextForScreenReaders()
    {
        // an empty page: a transparent code needs light paper beneath it
        using var doc = BlankPage.CreateDocument(595, 842, 1);
        CodeElement[] codes =
        [
            At(new CodeElement { Rect = default, Text = "Lager 17", Level = QrErrorCorrection.H, Color = 0x1F4E9A, Background = null, AltText = "Lagerplatz" }, 60, 600),
            At(new CodeElement { Rect = default, Format = BarcodeFormat.DataMatrix, Text = "(01)04012345678901(17)261231", Gs1 = true, Shape = DataMatrixShape.Rectangular }, 300, 600),
            At(new CodeElement { Rect = default, Format = BarcodeFormat.Ean13, Text = "4006381333931", Caption = false, QuietZone = false }, 60, 400, 90),
        ];
        using var saved = Save(doc, [PageItem.From(doc)[0].WithElements([.. codes])]);
        var annots = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
        Assert.Equal("Lagerplatz", annots.Get(0).AsDictionary()!.GetString(PdfName.Contents)!.ToText());
        Assert.Equal("Data Matrix: (01)04012345678901(17)261231", annots.Get(1).AsDictionary()!.GetString(PdfName.Contents)!.ToText());
        var back = PageItem.From(saved)[0].Elements;
        Assert.Equal(codes.Length, back.Count);
        for (var i = 0; i < codes.Length; i++)
        {
            var (a, b) = (codes[i], Assert.IsType<CodeElement>(back[i]));
            Assert.Equal((a.Format, a.Text, a.Gs1, a.Level, a.Shape, a.Color, a.Background, a.QuietZone, a.Caption, a.AltText, a.Rotation),
                (b.Format, b.Text, b.Gs1, b.Level, b.Shape, b.Color, b.Background, b.QuietZone, b.Caption, b.AltText, b.Rotation));
            Assert.Equal(a.Rect.Left, b.Rect.Left, 3);
            Assert.Equal(a.Rect.Top, b.Rect.Top, 3);
        }

        // flattened: part of the page content, still readable, no element left
        using var flat = Save(saved, PageItem.From(saved), new PdfSaveOptions { Flatten = true });
        Assert.Empty(PageItem.From(flat)[0].Elements);
        var found = PageBarcodes.FindAll(flat, flat.Pages[0], PageInterpreter.Build(flat, flat.Pages[0]), [], new ImageCache());
        Assert.Contains(found, f => f.Code.Text == "Lager 17");
        Assert.Contains(found, f => f.Code.Text == "4006381333931");
        Assert.Contains(found, f => f.Code.Format == BarcodeFormat.DataMatrix && f.Code.Text.StartsWith("0104012345678901", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(BarcodeFormat.Ean13, "4006381333932", "Prüfziffer")]   // 13 digits, wrong check digit
    [InlineData(BarcodeFormat.Ean13, "40063813339", "12 oder 13")]
    [InlineData(BarcodeFormat.Itf, "123", "")]
    [InlineData(BarcodeFormat.Qr, "", "Inhalt")]
    [InlineData(BarcodeFormat.Gs1_128, "0104012345678901", "Klammern")]
    public void InvalidContent_Placeholder_AndAMessage(BarcodeFormat format, string text, string message)
    {
        var code = new CodeElement { Format = format, Text = text, Rect = new PdfRectangle(0, 0, 100, 60) };
        var (symbol, error) = ElementAppearance.CodeSymbolOf(code);
        Assert.Null(symbol);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Contains(message, error);
        Assert.Equal(0, ElementAppearance.CodeModuleMm(code));
        _ = ElementAppearance.For(code);   // the placeholder draws without error
    }

    [Fact]
    public void Gs1_Brackets_ToElementString_AndBack()
    {
        // fixed lengths without separator, variable data followed by one unless it ends the string; the GTIN check digit is added
        var gtin = "0401234567890" + Core.Barcodes.Linear.LinearCodes.Gs1CheckDigit("0401234567890");
        Assert.Equal("01" + gtin + "17261231" + "10A1\u001D" + "21XYZ",
            CodeSymbol.Create(BarcodeFormat.Qr, "(01)0401234567890(17)261231(10)A1(21)XYZ", gs1: true).Encoded);
        var symbol = CodeSymbol.Create(BarcodeFormat.Gs1_128, "(01)04012345678901(3103)001250(10)LOT 7");
        Assert.Equal("01040123456789013103001250" + "10LOT 7", symbol.Encoded);
        Assert.Equal("(01)04012345678901(3103)001250(10)LOT 7", symbol.Captions[0].Text);
        Assert.Throws<ArgumentException>(() => CodeSymbol.Create(BarcodeFormat.Gs1_128, "(01)04012345678902"));   // wrong check digit
        Assert.Throws<ArgumentException>(() => CodeSymbol.Create(BarcodeFormat.Gs1_128, "(17)2612"));             // date too short
        Assert.Throws<ArgumentException>(() => CodeSymbol.Create(BarcodeFormat.Gs1_128, "(10)"));
    }

    [Fact]
    public void ModuleSize_DefaultSize_AndSquareMatrix()
    {
        var qr = new CodeElement { Text = "PDF-Knead", Rect = default };
        var (w, h) = ElementAppearance.DefaultCodeSize(qr);
        Assert.Equal(w, h, 3);
        var placed = qr with { Rect = new PdfRectangle(0, 0, w, h) };
        Assert.InRange(ElementAppearance.CodeModuleMm(placed), 0.49, 0.75);   // 0.5 mm, or more where the 15 mm minimum applies
        // in a wide rectangle the symbol stays square: the height decides
        Assert.Equal(ElementAppearance.CodeModuleMm(placed), ElementAppearance.CodeModuleMm(qr with { Rect = new PdfRectangle(0, 0, 3 * w, h) }), 6);
        var ean = new CodeElement { Format = BarcodeFormat.Ean13, Text = "4006381333931", Rect = default };
        var (ew, eh) = ElementAppearance.DefaultCodeSize(ean);
        Assert.Equal(0.33, ElementAppearance.CodeModuleMm(ean with { Rect = new PdfRectangle(0, 0, ew, eh) }), 3);
        Assert.Equal(25.9, eh / 72 * 25.4, 1);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void SelfCheck_ReadsWhatWasDrawn(BarcodeFormat format, string text, string expected)
    {
        var code = At(new CodeElement { Format = format, Text = text, Rect = default }, 50, 50);
        var read = CodeCheck.Read(code);
        Assert.NotNull(read);
        Assert.Equal((format, expected), (read.Format, read.Text));
        Assert.Equal(expected, CodeCheck.Read(code with { Rotation = 90, Rect = new PdfRectangle(0, 0, code.Rect.Height, code.Rect.Width) })?.Text);
    }

    [Fact]
    public void SelfCheck_FailsWhereAScannerWould()
    {
        var code = At(new CodeElement { Text = "https://example.org", Rect = default }, 0, 0);
        Assert.NotNull(CodeCheck.Read(code));
        Assert.Null(CodeCheck.Read(code with { Color = 0x202020, Background = 0x000000 }));   // dark on dark
        // contrast as the standards grade it: black on white 100 %, light grey far below 40 %, light on dark negative
        Assert.Equal(1, ElementAppearance.CodeContrast(code), 3);
        Assert.True(ElementAppearance.CodeContrast(code with { Color = 0xD0D0D0 }) < 0.4);   // 37 %
        Assert.True(ElementAppearance.CodeContrast(code with { Color = 0x1F4E9A }) > 0.4);
        Assert.True(ElementAppearance.CodeContrast(code with { Color = 0xFFFFFF, Background = 0x000000 }) < 0);
        Assert.Null(CodeCheck.Read(code with { Text = "" }));                           // no symbol at all
    }

    /// <summary>
    /// Crafted settings of a code in a file (security, KONZEPT.md, section 5.19): an unknown kind, a content far beyond any symbol, absurd
    /// values — nothing throws; what is unusable stays an ordinary annotation, the rest becomes an element that draws (at worst as the
    /// placeholder).
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void CraftedSettings_InAFile_NeverThrow()
    {
        var pdf = TestPdf.Simple(1);
        PdfDictionary Data(string kind, string content, params (string Key, PdfObject Value)[] more)
        {
            var data = TestPdf.Dict(("Art", TestPdf.N("Code")), ("Codeart", TestPdf.N(kind)), ("Inhalt", new PdfString(System.Text.Encoding.Latin1.GetBytes(content))));
            foreach (var (key, value) in more) data.Set(PdfName.Get(key), value);
            return data;
        }
        PdfDictionary[] crafted =
        [
            Data("Unsinn", "x"),                                                                        // unknown kind: no element
            Data("4711", "x"),                                                                          // a number is no kind either
            Data("Qr", new string('A', 20_000)),                                                        // longer than the limit: no element
            Data("Qr", new string('A', 9_000), ("Stufe", TestPdf.N("H"))),                              // too long for any QR: placeholder
            Data("Ean13", "12345", ("Farbe", PdfInteger.Of(-1)), ("Hintergrund", PdfInteger.Of(long.MaxValue))),
            Data("Code128", new string('\u0001', 5_000), ("Klartext", PdfBoolean.True)),
            Data("DataMatrix", "(01)", ("GS1", PdfBoolean.True), ("Gestalt", TestPdf.N("Kreis"))),
            Data("Itf", new string('9', 8_000)),
        ];
        var annots = new PdfArray();
        foreach (var data in crafted)
            annots.Add(TestPdf.Ref(pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Stamp")), ("Rect", TestPdf.Box(10, 10, 300, 200)),
                ("PDFKneadElement", data)))));
        // a bar code with its digits in a box only 4 pt high: the text cannot get its smallest size
        annots.Add(TestPdf.Ref(pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Stamp")), ("Rect", TestPdf.Box(10, 300, 300, 304)),
            ("PDFKneadElement", Data("Ean13", "4006381333931", ("Klartext", PdfBoolean.True)))))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), annots);
        ((PdfDictionary)pdf.Get(1)).Set(ElementStore.CatalogKey, PdfBoolean.True);
        using var doc = PdfDocument.Open(pdf.Build());
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var codes = PageItem.From(doc)[0].Elements.OfType<CodeElement>().ToList();
        Assert.Equal(6, codes.Count);
        foreach (var code in codes)
        {
            _ = ElementAppearance.For(code);
            _ = CodeCheck.Read(code);
        }
        Assert.True(watch.ElapsedMilliseconds < 10_000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
        Assert.Contains(codes, c => c.Format == BarcodeFormat.DataMatrix && c.Shape == DataMatrixShape.Square);   // unknown shape: square
        Assert.All(codes, c => Assert.InRange(c.Color, 0, 0xFFFFFF));
        Assert.Null(ElementAppearance.CodeSymbolOf(codes.Single(c => c.Format == BarcodeFormat.Qr)).Symbol);
        // thousands of bars would take a minute to draw: bar codes stop at 80 characters
        Assert.Null(ElementAppearance.CodeSymbolOf(codes.Single(c => c.Format == BarcodeFormat.Itf)).Symbol);
    }

    [Fact]
    public void SmallCode_WithoutQuietZone_FoundOnThePage()
    {
        // modules of 0.25 mm (the smallest recommended), the box exactly around the symbol, every level and direction, as element and
        // flattened: about two pixels per module at the page search's resolution — a clip on the code's edge must not lighten its outer
        // modules, and where the finder patterns do not show this coarse, the area is drawn finer as a suspect
        var mm = 72 / 25.4;
        var missed = new List<string>();
        foreach (var level in Enum.GetValues<QrErrorCorrection>())
            foreach (var rotation in new[] { 0, 90, 180, 270 })
                foreach (var flatten in new[] { false, true })
                {
                    var code = new CodeElement { Text = "https://example.org/r?n=87576371", QuietZone = false, Level = level, Rect = default };
                    var side = ElementAppearance.CodeSymbolOf(code).Symbol!.Matrix!.Width * 0.25 * mm;
                    code = code with { Rect = new PdfRectangle(297 - side / 2, 421 - side / 2, 297 + side / 2, 421 + side / 2), Rotation = rotation };
                    using var blank = BlankPage.CreateDocument(595, 842, 1);
                    using var doc = Save(blank, [PageItem.From(blank)[0].WithElements([code])], new PdfSaveOptions { Flatten = flatten });
                    var found = PageBarcodes.FindAll(doc, doc.Pages[0], PageInterpreter.Build(doc, doc.Pages[0]), [], new ImageCache());
                    if (found.Count != 1 || found[0].Code.Text != code.Text) missed.Add($"{level} {rotation}° {(flatten ? "flach" : "Element")}");
                }
        Assert.Empty(missed);
    }
}
