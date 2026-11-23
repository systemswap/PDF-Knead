using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The JBIG2 test files of SerenityOS as PDF (TestPDFs/pdfium/third_party/jbig2, BSD-2-Clause, with an expected image each): generic regions in
/// every template, MMR, refinement regions of the page and of intermediate results, halftone with and without skipped cells, symbol
/// dictionaries and text regions — Huffman and arithmetic, with refinement and aggregation, custom tables, retained contexts, transposed and
/// every reference corner —, striping, unknown sizes, end markers. Every page is drawn at 72 dpi and compared with its expected image
/// (Bericht M2, the rows of T.88 split on 20.11.2026: the features no conformance stream of T.88 Annex K covers). Without the test files the
/// test passes; the report goes to %TEMP%\pdf-knead-jbig2-erwartet.txt. Checked by putting errors in on 20.11.2026, each found: table B.5 with one
/// bit less (symhuffB5B3), contexts not reused (context-reuse), the text region's refinement AT unsigned (textrefine, texthuffrefine), the
/// custom tables of FS and DS swapped (symhuffcustom-texthuffcustom).
/// </summary>
public sealed class Jbig2ExpectedImages
{
    [TestPdfsFact]
    public void TestFilesOfSerenityOs_AsTheirExpectedImages()
    {
        var folder = Folder();
        var files = Directory.GetFiles(folder, "*.pdf").Order(StringComparer.Ordinal).ToList();
        Assert.True(files.Count > 100, $"{files.Count} Dateien in {folder}");
        var report = new System.Text.StringBuilder();
        var failures = new List<string>();
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var expectedPath = Path.Combine(folder, name + "_expected.pdf.0.png");
            if (!File.Exists(expectedPath)) continue;
            var expected = PngDecoder.Decode(File.ReadAllBytes(expectedPath));
            using var doc = PdfDocument.Open(file);
            using var drawn = ContentImageTests.Render(doc, doc.Pages[0]);
            if (drawn.Width != expected.Width || drawn.Height != expected.Height)
            {
                failures.Add($"{name}: Größe {drawn.Width}×{drawn.Height} statt {expected.Width}×{expected.Height}");
                report.AppendLine(failures[^1]);
                continue;
            }
            var wrong = 0;
            for (var y = 0; y < drawn.Height; y++)
                for (var x = 0; x < drawn.Width; x++)
                {
                    var c = drawn.GetPixel(x, y);
                    var e = (y * expected.Width + x) * 4;
                    var grey = (c.Red + c.Green + c.Blue) / 3;
                    var expectedGrey = (expected.Pixels[e] + expected.Pixels[e + 1] + expected.Pixels[e + 2]) / 3;
                    if (Math.Abs(grey - expectedGrey) > 64) wrong++;
                }
            report.AppendLine($"{name,-60} {drawn.Width}×{drawn.Height} {(wrong == 0 ? "gleich" : $"{wrong} Bildpunkte anders")}");
            if (wrong > 0) failures.Add($"{name}: {wrong} Bildpunkte anders");
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "pdf-knead-jbig2-erwartet.txt"), report.ToString());
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// T.88 7.4.7.5 and 8.2 (Bericht M2, Zeilen 411, 440): an immediate refinement region without a reference refines the page and replaces
    /// what is there, also where the page clears its bit 6 („might contain regions that override the default operator“) and has OR as the
    /// default — a stream that sets the region's REPLACE without the bit. The test file bitmap-refine-page sets the bit; here it is cleared and
    /// the default left at OR: the page is still the expected image. The refinement there clears pixels the generic region set, so drawing it
    /// with OR would leave them (checked: without the rule for refinements of the page 291 pixels differ).
    /// </summary>
    [TestPdfsFact]
    public void RefinementOfThePage_WithoutOverrideBit_Replaces()
    {
        var folder = Folder();
        var (data, width, height) = Jbig2Of(Path.Combine(folder, "bitmap-refine-page.pdf"));
        var expected = PngDecoder.Decode(File.ReadAllBytes(Path.Combine(folder, "bitmap-refine-page_expected.pdf.0.png")));
        Assert.Equal(48, data[4] & 0x3F);                 // the first segment is the page information,
        Assert.Equal(0x40, data[27]);                     // its flags byte after the 11 header bytes and 16 bytes of size and resolution
        Assert.Equal(0, Different(Decode(data, width, height), expected));
        data[27] = 0;                                     // bit 6 cleared, default operator OR
        Assert.Equal(0, Different(Decode(data, width, height), expected));
        var generic = data[..(data.Length - 11 - 73)];    // without the refinement segment (11 header bytes, 73 data bytes)
        var genericImage = Decode(generic, width, height);
        Assert.True(Different(genericImage, expected) > 0, "die Verfeinerung ändert nichts");

        // 8.2 sentence 65 (Zeile 415): the immediate segments after the refinement are drawn after it — the generic region once more as
        // segment 3 behind the refinement (OR): the page is the refined image with the generic one drawn over it, not the refined image alone
        Assert.Equal(30 + 11 + 265, data.Length - 11 - 73);
        byte[] again = [.. data, 0, 0, 0, 3, .. data[34..306]];
        var refined = Decode(data, width, height);
        var both = refined.Zip(genericImage, (r, g) => (byte)(r & g)).ToArray();   // bit 1 white: black where either is black
        Assert.NotEqual(refined, both);
        Assert.Equal(both, Decode(again, width, height));
    }

    /// <summary>The 1-bit image of a JBIG2 decoded page: bit 1 white, as the filter gives it to PDF.</summary>
    private static byte[] Decode(byte[] data, int width, int height)
    {
        var truncated = false;
        var image = PdfKnead.Core.Pdf.Filters.Jbig2.Jbig2Decoder.Decode(data, null, width, height, ref truncated);
        Assert.False(truncated);
        return image;
    }

    private static int Different(byte[] image, RgbaImage expected)
    {
        var rowBytes = (expected.Width + 7) / 8;
        var wrong = 0;
        for (var y = 0; y < expected.Height; y++)
            for (var x = 0; x < expected.Width; x++)
            {
                var white = (image[y * rowBytes + x / 8] >> (7 - x % 8) & 1) == 1;
                if (white != expected.Pixels[(y * expected.Width + x) * 4] > 127) wrong++;
            }
        return wrong;
    }

    private static (byte[] Data, int Width, int Height) Jbig2Of(string file)
    {
        using var doc = PdfDocument.Open(file);
        foreach (var number in doc.ObjectNumbers)
            if (doc.GetObject(number) is PdfStream stream && stream.Dictionary.GetName(PdfName.Get("Filter"))?.Value == "JBIG2Decode")
                return (doc.GetRawStreamData(stream), (int)((PdfInteger)stream.Dictionary.Get(PdfName.Get("Width"))).Value,
                    (int)((PdfInteger)stream.Dictionary.Get(PdfName.Get("Height"))).Value);
        throw new InvalidDataException(file);
    }

    private static string Folder([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return Path.Combine(dir.FullName, "TestPDFs", "pdfium", "third_party", "jbig2");
        throw new DirectoryNotFoundException();
    }
}
