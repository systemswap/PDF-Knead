using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// JPEG 2000 under a bar is blacked out in its own channels (docs/FEHLERGEDAECHTNIS.md, No. 287): at its full size, in its colour space — grey
/// stays grey, a palette its indices, RGB with its opacity — instead of going through the display, converted to RGB. Beside the bar the page
/// looks as before.
/// </summary>
public sealed class RedactJpxTests
{
    private static string PdfJs(string name, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return Path.Combine(dir.FullName, "TestPDFs", "pdfjs", name);
        throw new DirectoryNotFoundException();
    }

    [Theory]
    [InlineData("bug_jpx.pdf", 1)]
    [InlineData("issue19326.pdf", 1)]
    [InlineData("issue12213.pdf", 1)]
    [InlineData("S2.pdf", 3)]
    public void UnderABar_OwnChannelsAndSize_RestLooksTheSame(string file, int components)
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(PdfJs(file)));
        var page = doc.Pages[0];
        var list = PageInterpreter.Build(doc, page);
        static bool IsJpx(PdfStream? s) => s?.Dictionary.Get(PdfName.Filter) switch
        {
            PdfName name => name.Value == "JPXDecode",
            PdfArray { Count: > 0 } filters => filters.Get(filters.Count - 1) is PdfName { Value: "JPXDecode" },
            _ => false,
        };
        var image = list.Items.ToArray().First(i => i.Kind == DisplayItemKind.Image && IsJpx(list.Images[i.Image].Stream));
        var source = list.Images[image.Image].Stream!;
        var jpx = Core.Pdf.Filters.Jpx.JpxDecoder.Decode(doc.DecodeStream(source).Data, long.MaxValue, applyPalette: false);
        // the left half of the image under a bar (display list: page space, Top the lower edge)
        var box = page.CropBox;
        double left = image.Left, right = image.Left + (image.Right - image.Left) / 2;
        var bar = new PdfRectangle(left, image.Top, right, image.Bottom);
        using var after = PdfDocument.Open(RedactionTests.WithBars(doc, (0, bar)));

        var written = PageInterpreter.Build(after, after.Pages[0]);
        var item = written.Items.ToArray().First(i => i.Kind == DisplayItemKind.Image && Math.Abs(i.Left - image.Left) < 1 && Math.Abs(i.Top - image.Top) < 1);
        var blanked = written.Images[item.Image].Stream!;
        Assert.Equal((jpx.Width, jpx.Height), ((int)blanked.Dictionary.GetInteger(PdfName.Get("Width"))!, (int)blanked.Dictionary.GetInteger(PdfName.Get("Height"))!));
        var space = ColorSpace.Create(blanked.Dictionary.Get(PdfName.Get("ColorSpace")), after.Pages[0].Resources, after);
        Assert.Equal(components, space.Components);

        using var before = ContentImageTests.Render(doc, page);
        using var now = ContentImageTests.Render(after, after.Pages[0]);
        var differ = 0;
        var scale = before.Width / box.Width;
        // device rows from the upper edge of the image (page y of its Bottom) to its lower edge
        for (var y = (int)((box.Top - image.Bottom) * scale) + 1; y < (int)((box.Top - image.Top) * scale) - 1; y++)
            for (var x = (int)(right * scale) + 2; x < (int)(image.Right * scale) - 1; x++)
            {
                SKColor a = before.GetPixel(x, y), b = now.GetPixel(x, y);
                if (Math.Max(Math.Abs(a.Red - b.Red), Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue))) > 24) differ++;
            }
        Assert.True(differ <= 4, $"{differ} Pixel neben dem Balken anders");
    }

    /// <summary>
    /// Images that are not blacked out as JPEG in their own samples — JPEG 2000 and CMYK JPEG — leave no piece of their original data in the
    /// file: the whole image is written anew from what was decoded, also the part beside the bar (docs/RECHERCHE-VERSTECKEN.md,
    /// „Wiederherstellungswege“, JPEG blocks at the edge of a bar).
    /// </summary>
    [Theory]
    [InlineData("bug_jpx.pdf", "JPXDecode")]
    [InlineData("S2.pdf", "JPXDecode")]
    [InlineData("cmykjpeg.pdf", "DCTDecode")]
    public void UnderABar_NoPieceOfTheOriginalDataStays(string file, string filter)
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(PdfJs(file)));
        var page = doc.Pages[0];
        var list = PageInterpreter.Build(doc, page);
        var image = list.Items.ToArray().First(i => i.Kind == DisplayItemKind.Image && list.Images[i.Image].Stream?.Dictionary.Get(PdfName.Filter) switch
        {
            PdfName name => name.Value == filter,
            PdfArray { Count: > 0 } filters => filters.Get(filters.Count - 1) is PdfName n && n.Value == filter,
            _ => false,
        });
        var raw = doc.GetRawStreamData(list.Images[image.Image].Stream!);
        var bar = new PdfRectangle(image.Left, image.Top, image.Left + (image.Right - image.Left) / 2, image.Bottom);
        var saved = RedactionTests.WithBars(doc, (0, bar));
        foreach (var at in new[] { raw.Length / 4, raw.Length / 2, raw.Length * 3 / 4 })
            Assert.True(saved.AsSpan().IndexOf(raw.AsSpan(at, Math.Min(48, raw.Length - at))) < 0, $"Rohdaten ab {at} noch in der Datei");
    }
}
