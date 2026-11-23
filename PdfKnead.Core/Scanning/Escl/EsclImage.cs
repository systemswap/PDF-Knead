using System.Buffers.Binary;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Filters.Jpeg;

namespace PdfKnead.Core.Scanning.Escl;

/// <summary>
/// Turns a page as an eSCL scanner delivers it into a <see cref="ScannedImage"/> (KONZEPT.md §5.25, S9, „Bilder“). The data is untrusted:
/// the kind is taken from the first bytes, not from the media type the device names (a difference is only noted); the size is checked before
/// any memory is taken; JPEG goes through the program's own decoder with its limits, PNG through its own reader. The resolution comes from the
/// file if it names a plausible one, else from the request.
/// </summary>
public static class EsclImage
{
    /// <summary>The largest page as it comes over the network (512 MB): far above any document scan in JPEG or PNG.</summary>
    public const int MaxBytes = 512 << 20;

    /// <summary>The most bytes of decoded samples, as for the other scanner interfaces.</summary>
    public const long MaxPixelBytes = 1_600_000_000;

    /// <summary>Decodes a page.</summary>
    /// <param name="data">The page as received.</param>
    /// <param name="contentType">The media type the device names, or null.</param>
    /// <param name="requestedDpi">The resolution asked for.</param>
    /// <param name="log">Where a difference between media type and content is noted, or null.</param>
    /// <returns>The page.</returns>
    /// <exception cref="ScanException">Not a JPEG or PNG, too large, damaged, or in a colour model a scanner page does not have.</exception>
    public static ScannedImage Decode(byte[] data, string? contentType, int requestedDpi, EsclDiagnostics? log = null)
    {
        var jpeg = data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
        var png = PngDecoder.IsPng(data);
        var named = contentType switch { "image/jpeg" or "image/jpg" => "jpeg", "image/png" => "png", _ => contentType };
        var found = jpeg ? "jpeg" : png ? "png" : null;
        if (found is not null && named != found) log?.Add("Bild", $"Inhaltstyp „{contentType}“, die Daten sind aber {found.ToUpperInvariant()}");
        if (jpeg) return Jpeg(data, requestedDpi);
        if (png) return Png(data, requestedDpi);
        log?.Add("Bild", $"unbekanntes Format („{contentType}“, {data.Length} Bytes)");
        throw new EsclProtocolException(Tr.T("Der Scanner liefert ein unbekanntes Bildformat."));
    }

    private static ScannedImage Jpeg(byte[] data, int requestedDpi)
    {
        if (JpegDecoder.ReadFrame(data) is not { } frame || frame.Width <= 0 || frame.Components is not (1 or 3))
            throw Damaged();
        // a scanner may write a placeholder for the number of lines and give the real one at the end (DNL); the width is always right
        CheckSize(frame.Width, Math.Max(1, frame.Height), frame.Components);
        // every 8 × 8 block of a complete sequential JPEG takes at least two bits (a DC difference and the end of the block), so a page needs at
        // least a byte per 256 pixels — a tiny file claiming a huge page would otherwise take its full image memory before the decoder finds
        // there is nothing (Fehlergedächtnis 958)
        if (!frame.Progressive && (long)frame.Width * Math.Max(1, frame.Height) > 256L * data.Length) throw Damaged();
        JpegImage image;
        try
        {
            image = JpegDecoder.Decode(data, new JpegOptions(MaxPixels: MaxPixelBytes / frame.Components));
        }
        catch (PdfException)
        {
            throw Damaged();
        }
        if (image.Damaged || image.Scale != 1 || image.Channels is not (1 or 3)) throw Damaged();
        CheckSize(image.Width, image.Height, image.Channels);
        var dpi = ImageMetadata.FromJpeg(data)?.StoredDpi;
        return new ScannedImage(image.Width, image.Height, image.Channels, Dpi(dpi, requestedDpi), image.Samples);
    }

    private static ScannedImage Png(byte[] data, int requestedDpi)
    {
        RgbaImage image;
        try
        {
            // a scanner page is whole: data for less than the page are damaged, refused before its memory is taken (No. 965)
            image = PngDecoder.Decode(data, complete: true);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        {
            throw Damaged();
        }
        if (image.Damaged) throw Damaged();   // read past an error: a page with wrong pixels is not taken (FG 1013)
        var pixels = image.Pixels;
        var count = image.Width * image.Height;
        var gray = true;
        var blackWhite = true;
        for (var i = 0; i < count && gray; i++)
        {
            var (r, g, b) = (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2]);
            gray = r == g && g == b;
            blackWhite &= r is 0 or 255;
        }
        var channels = gray ? 1 : 3;
        CheckSize(image.Width, image.Height, channels);
        var samples = new byte[(long)count * channels];
        for (var i = 0; i < count; i++)
        {
            if (gray) samples[i] = pixels[i * 4];
            else
            {
                samples[i * 3] = pixels[i * 4];
                samples[i * 3 + 1] = pixels[i * 4 + 1];
                samples[i * 3 + 2] = pixels[i * 4 + 2];
            }
        }
        return new ScannedImage(image.Width, image.Height, channels, Dpi(PngDpi(data), requestedDpi), samples, gray && blackWhite);
    }

    /// <summary>The resolution in the pHYs chunk of a PNG, if given in pixels per metre.</summary>
    private static (double X, double Y)? PngDpi(ReadOnlySpan<byte> data)
    {
        for (var at = 8; at + 12 <= data.Length;)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
            if (length > int.MaxValue || at + 12 + (long)length > data.Length) return null;
            var type = data.Slice(at + 4, 4);
            // nine bytes with a right CRC (RFC 2083, 4.2.5, 10.1), unit metre
            if (type.SequenceEqual("pHYs"u8) && length == 9 && data[at + 16] == 1 &&
                (Imaging.Crc32.Update(0xFFFFFFFF, data.Slice(at + 4, 13)) ^ 0xFFFFFFFF) == BinaryPrimitives.ReadUInt32BigEndian(data[(at + 17)..]))
                return (BinaryPrimitives.ReadUInt32BigEndian(data[(at + 8)..]) * 0.0254, BinaryPrimitives.ReadUInt32BigEndian(data[(at + 12)..]) * 0.0254);
            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8)) return null;
            at += 12 + (int)length;
        }
        return null;
    }

    // the file's value only if it is plausible and the same in both directions; scanners often write 72 or 96 as a mere default — those
    // count only if asked for
    private static int Dpi((double X, double Y)? stored, int requested)
    {
        if (stored is not { } d || Math.Abs(d.X - d.Y) > 0.5 || d.X is < 30 or > ResolutionChoices.Max) return requested;
        var value = (int)Math.Round(d.X);
        return value is 72 or 96 && requested != value ? requested : value;
    }

    private static void CheckSize(int width, int height, int channels)
    {
        if (width <= 0 || height <= 0 || (long)width * height * channels > MaxPixelBytes)
            throw new EsclProtocolException(Tr.T("Der Scanner hat ein unvollständiges oder beschädigtes Bild geliefert."));
    }

    private static EsclProtocolException Damaged() => new(Tr.T("Der Scanner hat ein unvollständiges oder beschädigtes Bild geliefert."));
}
