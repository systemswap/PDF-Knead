using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Places where content can stick (Verzeichnis L, docs/normabgleich/leck-orte.md) that the modules M5–M7 of the Konformitätsabgleich changed or
/// added: redaction and the search for hidden text must reach them. Nachweise „RP.“ in the directory.
/// </summary>
public sealed class RedactionPlacesTests
{
    private const string Secret = "GEHEIMWORT";

    /// <summary>A page of 400 × 400: <paramref name="secret"/> (marked content or text objects) and „Sichtbar“ above it; objects 5 ff. as given.</summary>
    private static PdfDocument Page(string catalogExtra, string secret, params string[] more)
    {
        var content = $"{secret} BT /F1 12 Tf 40 300 Td (Sichtbar) Tj ET";
        return PdfDocument.Open(DocumentStructureNormTests.FileOf([
            $"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources << /Font << /F1 6 0 R >> /Properties << /L 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream", .. more]));
    }

    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";

    /// <summary>The bar over the secret at (40, 200), 20 pt high.</summary>
    private static readonly PdfRectangle Bar = new(30, 190, 300, 225);

    private static void RedactedAway(PdfDocument doc)
    {
        var data = RedactionTests.WithBars(doc, (0, Bar));
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(data, [Secret], null, out _));
        using var after = PdfDocument.Open(data);
        Assert.Contains("Sichtbar", RedactionTests.Read(after, after.Pages[0]).Text);
    }

    /// <summary>
    /// L20 with 8.11.4 (FG 767): a layer off on the screen but on when printed (/AS with /Print) or when exported (/AS with /Export), and a layer
    /// of another intent that has no effect — the text on it under the bar leaves the file whatever state shows it. The search for hidden text
    /// counts what is printed as visible and what only an export shows as on a hidden layer.
    /// </summary>
    [Theory]
    [InlineData("Print", "/Usage << /Print << /PrintState /ON >> >>", null)]
    [InlineData("Export", "/Usage << /Export << /ExportState /ON >> >>", HiddenTextKind.HiddenLayer)]
    [InlineData("View", "/Intent /Design", null)]
    public void Layers_ShownOnlyInPrintOrExport_RedactedUnderTheBar(string evt, string layer, HiddenTextKind? kind)
    {
        using var doc = Page(
            $"/OCProperties << /OCGs [5 0 R] /D << /OFF [5 0 R] /AS [<< /Event /{evt} /OCGs [5 0 R] /Category [/{evt}] >>] >> >>",
            $"/OC /L BDC BT /F1 20 Tf 40 200 Td ({Secret}) Tj ET EMC",
            $"<< /Type /OCG /Name (Ebene) {layer} >>", Helvetica);
        var spot = HiddenText.Find(doc, doc.Pages[0]).FirstOrDefault(s => s.Text.Contains(Secret));
        Assert.Equal(kind, spot?.Kind);
        RedactedAway(doc);
    }

    /// <summary>
    /// L33 with 9.3.1 (FG 768): text of size 0 or with horizontal scaling 0 draws nothing but is read as text; the search for hidden text
    /// finds it as tiny, and under the bar it leaves the file.
    /// </summary>
    [Theory]
    [InlineData("BT /F1 0 Tf 40 200 Td")]
    [InlineData("BT /F1 20 Tf 0 Tz 40 200 Td")]
    public void TextOfSizeZero_FoundAsTiny_RedactedUnderTheBar(string start)
    {
        using var doc = Page("", $"{start} ({Secret}) Tj ET", "<< /Type /OCG /Name (Leer) >>", Helvetica);
        var spot = Assert.Single(HiddenText.Find(doc, doc.Pages[0]), s => s.Text.Contains(Secret));
        Assert.Equal(HiddenTextKind.Tiny, spot.Kind);
        RedactedAway(doc);
    }

    /// <summary>
    /// L05 with 8.9.5.4 (FG 763): an image hidden by its layer shows its alternate. Under the bar both go — the image is cut in its own values
    /// and its /Alternates, which hold whole images, leave the copy; no stream of the alternate's samples stays in the file.
    /// </summary>
    [Fact]
    public void HiddenImageWithVisibleAlternate_BothGoUnderTheBar()
    {
        const string content = "q 100 0 0 100 50 50 cm /Im0 Do Q BT /F1 12 Tf 40 300 Td (Sichtbar) Tj ET";
        const string image = "/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 3";
        using var doc = PdfDocument.Open(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [6 0 R] /D << /OFF [6 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources << /XObject << /Im0 5 0 R >> /Font << /F1 8 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< {image} /OC 6 0 R /Alternates [<< /Image 7 0 R >>] >>\nstream\n\xff\x00\x00\nendstream",
            "<< /Type /OCG /Name (Ebene) >>",
            $"<< {image} >>\nstream\n\x12\x34\x56\nendstream",
            Helvetica));
        var data = RedactionTests.WithBars(doc, (0, new PdfRectangle(40, 40, 160, 160)));
        using var after = PdfDocument.Open(data);
        for (var n = 1; n <= after.ObjectCount; n++)
            if (after.GetObject(n) is PdfStream { Dictionary: var dict } stream && dict.GetName(PdfName.Subtype)?.Value == "Image")
            {
                Assert.False(dict.ContainsKey(PdfName.Get("Alternates")));
                Assert.NotEqual(new byte[] { 0x12, 0x34, 0x56 }, after.DecodeStream(stream).Data);
            }
        Assert.Contains("Sichtbar", RedactionTests.Read(after, after.Pages[0]).Text);
    }

    /// <summary>
    /// 8.9.5.4 d) (FG 925): printed, an image without /OC shows its alternate marked /DefaultForPrinting. Under a bar over its left half both are
    /// blacked out at the same place: the copy keeps the blacked-out print alternate as its only alternate (the right half still blue), and no
    /// stream of the unredacted samples stays. Before, the alternates left the copy and it printed the screen image — changed outside the bar.
    /// </summary>
    [Fact]
    public void VisibleImageWithPrintAlternate_BlackedOutAtTheSamePlace()
    {
        const string content = "q 100 0 0 100 50 50 cm /Im0 Do Q BT /F1 12 Tf 40 300 Td (Sichtbar) Tj ET";
        const string image = "/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 6";
        using var doc = PdfDocument.Open(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources << /XObject << /Im0 5 0 R >> /Font << /F1 7 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< {image} /Alternates [<< /Image 6 0 R /DefaultForPrinting true >>] >>\nstream\n\xff\x00\x00\xff\x00\x00\nendstream",
            $"<< {image} >>\nstream\n\x12\x34\xfe\x00\x00\xff\nendstream",
            Helvetica));
        var data = RedactionTests.WithBars(doc, (0, new PdfRectangle(40, 40, 100, 160)));
        using var after = PdfDocument.Open(data);
        var shown = Assert.IsType<PdfStream>(after.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Entries.Single().Value is PdfReference r
            ? after.GetObject(r.Id.Number) : null);
        var entry = Assert.IsType<PdfDictionary>(Assert.Single(shown.Dictionary.GetArray(PdfName.Get("Alternates"))!));
        Assert.True(entry.GetBoolean(PdfName.Get("DefaultForPrinting")));
        var printed = Assert.IsType<PdfStream>(entry.Get(PdfName.Get("Image")));
        var samples = after.DecodeStream(printed).Data;
        Assert.Equal(new byte[] { 0, 0, 0 }, samples[..3]);                    // left: blacked out
        Assert.Equal(new byte[] { 0, 0, 0xff }, samples[^3..]);               // right: the blue of the print alternate
        for (var n = 1; n <= after.ObjectCount; n++)
            if (after.GetObject(n) is PdfStream { Dictionary: var dict } stream && dict.GetName(PdfName.Subtype)?.Value == "Image")
                Assert.NotEqual(new byte[] { 0x12, 0x34, 0xfe, 0, 0, 0xff }, after.DecodeStream(stream).Data);
        Assert.Contains("Sichtbar", RedactionTests.Read(after, after.Pages[0]).Text);
    }

    /// <summary>
    /// L06 with 9.10.3 (FG 772): a /ToUnicode map takes the mappings of another through /UseCMap — here „A“ reads as „Ä“ only through the base
    /// map. When the only „A“ is redacted, the map written for the font names the codes still shown only and no base map; the mapping of the
    /// redacted code is nowhere in the file.
    /// </summary>
    [Fact]
    public void ToUnicodeThroughUseCMap_RedactedCodeLeavesBothMaps()
    {
        static string Map(string name, string entries) =>
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /" + name + " def 1 begincodespacerange <00> <FF> endcodespacerange " +
            entries + " endcmap CMapName currentdict /CMap defineresource pop end end";
        var own = Map("Teil", "2 beginbfchar <42> <0042> <53> <0053> endbfchar");
        var basis = Map("Basis", "1 beginbfchar <41> <00C4> endbfchar");
        const string content = "BT /F1 20 Tf 40 200 Td (A) Tj ET BT /F1 12 Tf 40 300 Td (BS) Tj ET";
        using var doc = PdfDocument.Open(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 6 0 R >>",
            $"<< /Length {own.Length} /UseCMap 7 0 R >>\nstream\n{own}\nendstream",
            $"<< /Length {basis.Length} >>\nstream\n{basis}\nendstream"));
        Assert.StartsWith("Ä", RedactionTests.Read(doc, doc.Pages[0]).Text);
        var data = RedactionTests.WithBars(doc, (0, Bar));
        using var after = PdfDocument.Open(data);
        Assert.Equal("BS", RedactionTests.Read(after, after.Pages[0]).Text.Trim());
        Assert.DoesNotContain("00C4", RedactionTests.Everything(after));
    }

    /// <summary>
    /// L05 with S-A A86: an inline image naming JBIG2Decode (7.4.7, 8.9.7: not allowed there) is read like the same image as an XObject — and
    /// redaction reads it alike: under the bar its data does not stay in the content (the tolerance applies everywhere, S-A rule).
    /// </summary>
    [Fact]
    public void InlineImageWithJbig2_ReadAlike_CutUnderTheBar()
    {
        var bits = new byte[2 * 16];   // 16 × 16, left half black
        for (var y = 0; y < 16; y++) bits[2 * y + 1] = 0xFF;
        var jbig2 = System.Text.Encoding.Latin1.GetString(Core.Pdf.Filters.Jbig2.Jbig2Encoder.Generic(bits, 16, 16, 2));
        var content = $"q 100 0 0 100 50 50 cm BI /W 16 /H 16 /BPC 1 /CS /G /F /JBIG2Decode /L {jbig2.Length} ID {jbig2} EI Q " +
                      "BT /F1 12 Tf 40 300 Td (Sichtbar) Tj ET";
        using var doc = PdfDocument.Open(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream", Helvetica));
        var data = RedactionTests.WithBars(doc, (0, new PdfRectangle(40, 40, 160, 160)));
        using var after = PdfDocument.Open(data);
        var contents = after.Pages[0].Dictionary.Get(PdfName.Contents);
        var streams = contents is PdfArray array ? Enumerable.Range(0, array.Count).Select(array.Get).OfType<PdfStream>() : [(PdfStream)contents!];
        var written = string.Concat(streams.Select(s => System.Text.Encoding.Latin1.GetString(after.GetStreamData(s))));
        Assert.DoesNotContain(jbig2, written);
        Assert.Contains("Sichtbar", RedactionTests.Read(after, after.Pages[0]).Text);
    }
}
