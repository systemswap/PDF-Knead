using System.Text;
using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using PdfKnead.ViewModels;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Text in bar code fonts goes to the code tool (phase 8.2, docs/FEHLERGEDAECHTNIS.md, No. 607): the block is marked for it, its content comes
/// from reading the code or — where unambiguous — from its text, and „Code ändern …“ makes the code anew with check digit and quiet zone.
/// Symbol fonts stay locked.
/// </summary>
public sealed class CodeFontTests
{
    /// <summary>A page with <paramref name="content"/>; font /C a non-embedded Type 1 font named <paramref name="fontName"/>, /F1 Helvetica.</summary>
    private static PdfDocument Page(string fontName, string content)
    {
        var pdf = new TestPdf();
        PdfDictionary Font(string name) => TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N(name)),
            ("Encoding", TestPdf.N("WinAnsiEncoding")));
        var stream = pdf.Add(TestPdf.FlateStream(Encoding.Latin1.GetBytes(content)));
        var pagesNumber = pdf.Add(new PdfDictionary());
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pagesNumber)), ("Contents", TestPdf.Ref(stream)),
            ("MediaBox", TestPdf.Box(0, 0, 595, 842)), ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("C", Font(fontName)), ("F1", Font("Helvetica"))))))));
        pdf.Replace(pagesNumber, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray { TestPdf.Ref(page) }), ("Count", PdfInteger.Of(1))));
        pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pagesNumber))));
        return PdfDocument.Open(pdf.Build());
    }

    private static byte[] Save(PdfDocument doc, IReadOnlyList<PageItem> pages)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(doc, pages, output);
        return output.ToArray();
    }

    /// <summary>Replaces the code of a block in a bar code font as „Code ändern …“ does, with <paramref name="changed"/> as the new content.</summary>
    private static PdfDocument Replace(PdfDocument doc, PageTextBlock block, IReadOnlyList<PageTextBlock> blocks, string changed, out CodeElement planned)
    {
        var inspected = DocumentViewModel.DescribeFontCode(0, block);
        var plan = CodeReplacement.ForFontBlock(block, blocks, inspected.Code.Code.Format, inspected.Code.Code.Text, out var refusal);
        Assert.Null(refusal);
        Assert.NotNull(plan);
        planned = plan.Element;
        var emptied = Redactor.Apply(doc, doc.Pages[0], [plan.Area], paper: true);
        var item = PageItem.From(doc)[0];
        return PdfDocument.Open(Save(doc, [item.WithEmptiedArea(emptied, [.. item.Elements, planned with { Text = changed }])]));
    }

    private static string PageTextOf(PdfDocument doc) => string.Concat(PageInterpreter.Build(doc, doc.Pages[0]).Text.Select(g => g.Text));

    /// <summary>A block in a bar code font is marked for the code tool (before: locked as „Barcode- oder Symbolschrift“).</summary>
    [Fact]
    public void BarcodeFontBlock_MarkedForTheCodeTool()
    {
        using var doc = TextBlockTests.Page("BT /F5 24 Tf 72 760 Td (*123*) Tj ET BT /F1 10 Tf 72 650 Td (Frei) Tj ET");
        var blocks = TextBlocks.Find(doc, doc.Pages[0]);
        var code = Assert.Single(blocks, b => b.Text == "*123*");
        Assert.Contains("Code-Werkzeug", code.Locked);
        Assert.True(code.CodeFont);
        Assert.Null(Assert.Single(blocks, b => b.Text == "Frei").Locked);
    }

    /// <summary>
    /// A label and a code in a bar code font on one line („Artikel: *A123*“): two blocks — the label stays editable text, only the code goes
    /// to the code tool (before: one block, the label locked with the code and whited out by „Code ändern …“).
    /// </summary>
    [Fact]
    public void LabelBesideACode_IsABlockOfItsOwn()
    {
        using var doc = TextBlockTests.Page("BT /F1 12 Tf 72 700 Td (Artikel: ) Tj /F5 24 Tf (*A123*) Tj ET");
        var blocks = TextBlocks.Find(doc, doc.Pages[0]);
        var label = Assert.Single(blocks, b => b.Text.StartsWith("Artikel", StringComparison.Ordinal));
        Assert.Null(label.Locked);
        Assert.DoesNotContain("A123", label.Text);
        var code = Assert.Single(blocks, b => b.CodeFont);
        Assert.Equal("*A123*", code.Text.Trim());
    }

    /// <summary>A symbol font stays locked, and is no code font (protection).</summary>
    [Fact]
    public void WingdingsBlock_StaysLocked()
    {
        using var doc = Page("Wingdings-Regular", "BT /C 20 Tf 72 700 Td (abc) Tj ET");
        var block = Assert.Single(TextBlocks.Find(doc, doc.Pages[0]));
        Assert.Contains("Symbolschrift", block.Locked);
        Assert.False(block.CodeFont);
    }

    /// <summary>
    /// The Thai fonts of Windows carry „UPC“ in their name (AngsanaUPC, BrowalliaUPC, CordiaUPC …): no code font, their text is editable
    /// (before: locked as „Barcode- oder Symbolschrift“; found in a public test collection).
    /// </summary>
    [Fact]
    public void ThaiUpcFont_IsNoCodeFont_Editable()
    {
        Assert.Null(CodeFonts.FormatOf("ABCDEE+AngsanaUPC"));
        using var doc = Page("AngsanaUPC", "BT /C 20 Tf 72 700 Td (Hallo) Tj ET");
        var block = Assert.Single(TextBlocks.Find(doc, doc.Pages[0]));
        Assert.Null(block.Locked);
        Assert.False(block.CodeFont);
    }

    /// <summary>The kind from the font name (not from ordinary names that contain „ean“), the content from the text where it is unambiguous.</summary>
    [Fact]
    public void KindFromTheName_ContentFromTheText()
    {
        Assert.Equal(BarcodeFormat.Code39, CodeFonts.FormatOf("Barcode3of9"));
        Assert.Equal(BarcodeFormat.Code39, CodeFonts.FormatOf("LibreBarcode39-Regular"));
        Assert.Equal(BarcodeFormat.Code128, CodeFonts.FormatOf("Code128Test"));
        Assert.Equal(BarcodeFormat.Ean13, CodeFonts.FormatOf("EAN13Test"));
        Assert.Null(CodeFonts.FormatOf("OceanSans-Bold"));
        Assert.Null(CodeFonts.FormatOf("Helvetica"));
        Assert.Null(CodeFonts.FormatOf("PDF417Barcode"));   // no encoder for PDF417: a locked symbol font
        Assert.Equal("123", CodeFonts.Derive(BarcodeFormat.Code39, "*123*"));
        Assert.Equal("PDF-KNEAD 42", CodeFonts.Derive(BarcodeFormat.Code39, "*PDF-KNEAD 42*"));
        Assert.Null(CodeFonts.Derive(BarcodeFormat.Code39, "*abc*"));   // lower case is not Code 39
        Assert.Equal("4006381333931", CodeFonts.Derive(BarcodeFormat.Ean13, "4 006381 333931"));
        Assert.Equal("400638133393", CodeFonts.Derive(BarcodeFormat.Ean13, "400638133393"));   // without check digit: the encoder adds it
        Assert.Null(CodeFonts.Derive(BarcodeFormat.Ean13, "4006381333932"));   // wrong check digit
        Assert.Null(CodeFonts.Derive(BarcodeFormat.Code128, "Ìabc~Î"));   // Code 128 fonts: only by reading
    }

    /// <summary>
    /// A Code 39 block that cannot be read (the font is not embedded): its content from the text, the code made anew where it was — the new
    /// content reads back, the old text and its human-readable line below are gone, text beside it stays.
    /// </summary>
    [Fact]
    public void Code39FontBlock_Changed_ReadsBackWithTheNewContent()
    {
        using var doc = TextBlockTests.Page("BT /F5 24 Tf 72 700 Td (*123*) Tj ET BT /F1 8 Tf 84 686 Td (123) Tj ET BT /F1 10 Tf 72 600 Td (Bleibt stehen) Tj ET");
        var blocks = TextBlocks.Find(doc, doc.Pages[0]);
        var block = Assert.Single(blocks, b => b.CodeFont);
        var inspected = DocumentViewModel.DescribeFontCode(0, block);
        Assert.Equal((BarcodeFormat.Code39, "123"), (inspected.Code.Code.Format, inspected.Code.Code.Text));
        Assert.Contains("abgeleitet", inspected.Warnings);
        using var saved = Replace(doc, block, blocks, "456", out var planned);
        Assert.True(planned.Caption);
        Assert.InRange(planned.Rect.Left, block.Left - 1, block.Left + 1);
        var after = PageBarcodes.FindAll(saved, saved.Pages[0], PageInterpreter.Build(saved, saved.Pages[0]), [], new ImageCache());
        Assert.Equal("456", Assert.Single(after).Code.Text);
        var text = PageTextOf(saved);
        Assert.DoesNotContain("123", text);
        Assert.Contains("Bleibt stehen", text);
    }

    /// <summary>EAN-13 from twelve digits: the new code carries the computed check digit.</summary>
    [Fact]
    public void Ean13FontBlock_Changed_CheckDigitComputed()
    {
        using var doc = Page("EAN13Test", "BT /C 30 Tf 72 700 Td (400638133393) Tj ET");
        var blocks = TextBlocks.Find(doc, doc.Pages[0]);
        var block = Assert.Single(blocks);
        using var saved = Replace(doc, block, blocks, "400638133393", out _);
        var after = PageBarcodes.FindAll(saved, saved.Pages[0], PageInterpreter.Build(saved, saved.Pages[0]), [], new ImageCache());
        var found = Assert.Single(after).Code;
        Assert.Equal((BarcodeFormat.Ean13, "4006381333931"), (found.Format, found.Text));
    }

    /// <summary>
    /// A Code 128 font whose code cannot be read and whose content cannot be told from the text: the panel says to enter the content anew,
    /// and „Code ändern …“ puts an empty code (placeholder) in the block's place.
    /// </summary>
    [Fact]
    public void BarcodeFontWithoutReadableCode_AsksForContent()
    {
        using var doc = Page("Code128Test", "BT /C 30 Tf 72 700 Td (Ìabc~Î) Tj ET");
        var blocks = TextBlocks.Find(doc, doc.Pages[0]);
        var block = Assert.Single(blocks);
        var inspected = DocumentViewModel.DescribeFontCode(0, block);
        Assert.Equal((BarcodeFormat.Code128, ""), (inspected.Code.Code.Format, inspected.Code.Code.Text));
        Assert.Contains("Inhalt neu eingeben", inspected.Warnings);
        var plan = CodeReplacement.ForFontBlock(block, blocks, BarcodeFormat.Code128, "", out var refusal);
        Assert.Null(refusal);
        Assert.Equal("", plan!.Element.Text);
        Assert.NotNull(ElementAppearance.For(plan.Element));   // the placeholder until the content is typed
    }
}
