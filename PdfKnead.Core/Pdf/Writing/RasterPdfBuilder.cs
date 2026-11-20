using System.Globalization;
using System.Text;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// Simple PDF made only of page images ("save as image"): one image per page, no text, no fonts, no metadata. The pages
/// are written to the stream one after another (never all images in memory at once); the actual <see cref="PdfWriter"/>
/// then turns this into the finished file (version, encryption).
/// </summary>
/// <remarks>Usage: <see cref="AddPage"/> or <see cref="AddLayeredPage"/> for each page, then <see cref="Finish"/> once.
/// The output stream is not disposed.</remarks>
public sealed class RasterPdfBuilder
{
    private readonly Stream _output;
    private readonly List<long> _offsets = [-1, -1];   // 1 = catalogue, 2 = page tree (written at the end)
    private readonly List<int> _pages = [];
    private long _position;

    /// <summary>Starts a new file and writes the header.</summary>
    /// <param name="output">The stream to write to; written sequentially, no seeking needed.</param>
    public RasterPdfBuilder(Stream output)
    {
        _output = output;
        Write("%PDF-1.7\n");
        Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);   // binary marker
    }

    /// <summary>Number of pages added so far.</summary>
    public int PageCount => _pages.Count;

    /// <summary>
    /// Adds a page made of a single, already encoded image that fills the page.
    /// </summary>
    /// <param name="width">Page width in points.</param>
    /// <param name="height">Page height in points.</param>
    /// <param name="data">The encoded image data.</param>
    /// <param name="filter">"DCTDecode" (JPEG) or "FlateDecode" (RGB or grey, 8 bits per component).</param>
    /// <param name="pixelWidth">Image width in pixels.</param>
    /// <param name="pixelHeight">Image height in pixels.</param>
    /// <param name="gray">True for a greyscale image, false for RGB.</param>
    public void AddPage(double width, double height, ReadOnlySpan<byte> data, string filter, int pixelWidth, int pixelHeight, bool gray = false)
    {
        var image = NewObject();
        Write($"{image} 0 obj\n<</Type/XObject/Subtype/Image/Width {pixelWidth}/Height {pixelHeight}/ColorSpace/{(gray ? "DeviceGray" : "DeviceRGB")}" +
              $"/BitsPerComponent 8/Filter/{filter}/Length {data.Length}>>\nstream\n");
        Write(data);
        Write("\nendstream\nendobj\n");

        var content = NewObject();
        var draw = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"q {width:0.###} 0 0 {height:0.###} 0 0 cm /Im0 Do Q\n"));
        Write($"{content} 0 obj\n<</Length {draw.Length}>>\nstream\n");
        Write(draw);
        Write("\nendstream\nendobj\n");

        var page = NewObject();
        Write(string.Create(CultureInfo.InvariantCulture,
            $"{page} 0 obj\n<</Type/Page/Parent 2 0 R/MediaBox[0 0 {width:0.###} {height:0.###}]/Resources<</XObject<</Im0 {image} 0 R>>>>/Contents {content} 0 R>>\nendobj\n"));
        _pages.Add(page);
    }

    /// <summary>A 1-bit mask in one colour (JBIG2-encoded, lossless, 0 = paint); covers the whole page.</summary>
    /// <param name="Red">Red component 0–255.</param>
    /// <param name="Green">Green component 0–255.</param>
    /// <param name="Blue">Blue component 0–255.</param>
    /// <param name="Data">The JBIG2-encoded mask.</param>
    /// <param name="PixelWidth">Mask width in pixels.</param>
    /// <param name="PixelHeight">Mask height in pixels.</param>
    public sealed record ColorMask(byte Red, byte Green, byte Blue, byte[] Data, int PixelWidth, int PixelHeight);

    /// <summary>
    /// Adds a page made of layers (MRC): a background image (JPEG; null = white) and on top of it text and lines as 1-bit
    /// masks per colour (/ImageMask, JBIG2, lossless). Still only images: no text, nothing to copy.
    /// </summary>
    /// <param name="width">Page width in points.</param>
    /// <param name="height">Page height in points.</param>
    /// <param name="background">The background JPEG with its size in pixels and whether it is greyscale; null for none.</param>
    /// <param name="masks">The colour masks, painted in this order.</param>
    public void AddLayeredPage(double width, double height, (byte[] Jpeg, int PixelWidth, int PixelHeight, bool Gray)? background,
        IReadOnlyList<ColorMask> masks)
    {
        var names = new StringBuilder();
        var draw = new StringBuilder();
        string Size() => string.Create(CultureInfo.InvariantCulture, $"{width:0.###} 0 0 {height:0.###} 0 0 cm");
        if (background is { } bg)
        {
            var image = NewObject();
            Write($"{image} 0 obj\n<</Type/XObject/Subtype/Image/Width {bg.PixelWidth}/Height {bg.PixelHeight}/ColorSpace/{(bg.Gray ? "DeviceGray" : "DeviceRGB")}" +
                  $"/BitsPerComponent 8/Filter/DCTDecode/Length {bg.Jpeg.Length}>>\nstream\n");
            Write(bg.Jpeg);
            Write("\nendstream\nendobj\n");
            names.Append($"/Bg {image} 0 R");
            draw.Append($"q {Size()} /Bg Do Q\n");
        }
        for (var i = 0; i < masks.Count; i++)
        {
            var mask = masks[i];
            var image = NewObject();
            Write($"{image} 0 obj\n<</Type/XObject/Subtype/Image/Width {mask.PixelWidth}/Height {mask.PixelHeight}/ImageMask true/BitsPerComponent 1" +
                  $"/Filter/JBIG2Decode/Length {mask.Data.Length}>>\nstream\n");
            Write(mask.Data);
            Write("\nendstream\nendobj\n");
            names.Append($"/M{i} {image} 0 R");
            draw.Append(string.Create(CultureInfo.InvariantCulture,
                $"{mask.Red / 255.0:0.####} {mask.Green / 255.0:0.####} {mask.Blue / 255.0:0.####} rg q {Size()} /M{i} Do Q\n"));
        }
        var content = NewObject();
        var bytes = Encoding.ASCII.GetBytes(draw.ToString());
        Write($"{content} 0 obj\n<</Length {bytes.Length}>>\nstream\n");
        Write(bytes);
        Write("\nendstream\nendobj\n");
        var page = NewObject();
        Write(string.Create(CultureInfo.InvariantCulture,
            $"{page} 0 obj\n<</Type/Page/Parent 2 0 R/MediaBox[0 0 {width:0.###} {height:0.###}]/Resources<</XObject<<{names}>>>>/Contents {content} 0 R>>\nendobj\n"));
        _pages.Add(page);
    }

    /// <summary>Writes the page tree, catalogue, cross-reference table and trailer, and flushes the stream.</summary>
    public void Finish()
    {
        _offsets[1] = _position;
        Write($"2 0 obj\n<</Type/Pages/Count {_pages.Count}/Kids[{string.Join(' ', _pages.Select(p => $"{p} 0 R"))}]>>\nendobj\n");
        _offsets[0] = _position;
        Write("1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n");
        var xref = _position;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(_offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in _offsets) sb.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<</Size ").Append(_offsets.Count + 1).Append("/Root 1 0 R>>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        Write(sb.ToString());
        _output.Flush();
    }

    private int NewObject()
    {
        _offsets.Add(_position);
        return _offsets.Count;
    }

    private void Write(string text) => Write(Encoding.ASCII.GetBytes(text));

    private void Write(ReadOnlySpan<byte> bytes)
    {
        _output.Write(bytes);
        _position += bytes.Length;
    }
}
