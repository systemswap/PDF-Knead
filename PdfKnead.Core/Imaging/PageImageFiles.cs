using System.Buffers.Binary;
using System.Globalization;

namespace PdfKnead.Core.Imaging;

/// <summary>The file format of pages saved as images (KONZEPT §5.25, „Als Bild speichern“).</summary>
public enum PageImageFormat
{
    /// <summary>PNG: lossless; black and white with one bit per pixel.</summary>
    Png,

    /// <summary>JPEG with a quality: small, for photos and colour scans.</summary>
    Jpeg,

    /// <summary>TIFF: lossless, one file per page or all pages in one file; black and white with CCITT Group 4.</summary>
    Tiff,

    /// <summary>WebP with a quality: smaller than JPEG at the same look; holds no resolution.</summary>
    WebP,
}

/// <summary>How pages are saved as images.</summary>
/// <param name="Format">The file format.</param>
/// <param name="Quality">The quality of JPEG and WebP, 50 … 100.</param>
/// <param name="OneFile">TIFF only: all pages in one file instead of one file per page.</param>
/// <param name="SelectedOnly">Only the selected pages instead of all.</param>
/// <param name="Dpi">The resolution for pages of a document (scans keep the resolution they were scanned at).</param>
public sealed record PageImageSettings(PageImageFormat Format = PageImageFormat.Png, int Quality = 90, bool OneFile = false,
    bool SelectedOnly = false, int Dpi = 300)
{
    /// <summary>The settings with values out of range brought back into it (from a settings file).</summary>
    public PageImageSettings Normalized() => this with
    {
        Format = Enum.IsDefined(Format) ? Format : PageImageFormat.Png,
        Quality = Math.Clamp(Quality, 50, 100),
        Dpi = Math.Clamp(Dpi, 72, 1200),
    };
}

/// <summary>File names and small format details shared by every way of saving pages as images.</summary>
public static class PageImageFiles
{
    /// <summary>
    /// The profile of rendered page pixels: sRGB for colour, grey with the sRGB tone curve for grey (own profiles). Page images name it, so
    /// that their colours are exact (RFC 2083, 9.2, 9.3; decided by the user on 22.11.2026; the TIFF pages carry the same, No. 980).
    /// </summary>
    /// <param name="colour">Colour (RGB) or grey.</param>
    /// <returns>A new copy of the profile.</returns>
    public static byte[] RenderedProfile(bool colour) =>
        colour ? Pdf.Graphics.Icc.CalibratedProfile.Srgb() : Pdf.Graphics.Icc.CalibratedProfile.SrgbGray();

    /// <summary>The file extension of a format, with the dot.</summary>
    public static string Extension(PageImageFormat format) => format switch
    {
        PageImageFormat.Jpeg => ".jpg",
        PageImageFormat.Tiff => ".tif",
        PageImageFormat.WebP => ".webp",
        _ => ".png",
    };

    /// <summary>The name of page <paramref name="number"/> (1-based) of <paramref name="count"/>: „Name_001“, more digits only for 1000 pages and more.</summary>
    public static string Name(string baseName, int number, int count)
    {
        var digits = Math.Max(3, count.ToString(CultureInfo.InvariantCulture).Length);
        return baseName + "_" + number.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>A path in <paramref name="folder"/> for <paramref name="name"/> that does not exist yet: „Name.png“, else „Name (2).png“ …</summary>
    public static string Free(string folder, string name, string extension)
    {
        var path = Path.Combine(folder, name + extension);
        for (var k = 2; File.Exists(path); k++) path = Path.Combine(folder, $"{name} ({k}){extension}");
        return path;
    }

    /// <summary>
    /// A base name usable as a file name: characters no file system allows replaced by „_“, trimmed, never empty („Scan“ then).
    /// </summary>
    public static string CleanBaseName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['\\', '/', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var clean = new string((name ?? "").Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "Scan" : clean;
    }

    /// <summary>
    /// Notes the resolution in a JPEG file: the density of its JFIF segment (JFIF 1.02, APP0: units 1 = dots per inch, X and Y density);
    /// without a JFIF segment directly after the start, one is inserted.
    /// </summary>
    /// <param name="jpeg">The JPEG file.</param>
    /// <param name="dpi">The resolution.</param>
    /// <returns>The file with the resolution (the same array when changed in place).</returns>
    public static byte[] WithJpegDensity(byte[] jpeg, double dpi)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return jpeg;
        var density = (ushort)Math.Clamp(Math.Round(dpi), 1, ushort.MaxValue);
        // APP0 „JFIF\0“ right after SOI: FF E0, length, identifier, version (2), units, X density, Y density
        if (jpeg.Length >= 18 && jpeg[2] == 0xFF && jpeg[3] == 0xE0 && jpeg.AsSpan(6, 5).SequenceEqual("JFIF\0"u8))
        {
            jpeg[13] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(14), density);
            BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(16), density);
            return jpeg;
        }
        var segment = new byte[18];
        segment[0] = 0xFF;
        segment[1] = 0xE0;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), 16);
        "JFIF\0"u8.CopyTo(segment.AsSpan(4));
        segment[9] = 1;    // version 1.02
        segment[10] = 2;
        segment[11] = 1;   // dots per inch
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(12), density);
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(14), density);
        // no thumbnail (two zero bytes at 16 and 17)
        return [.. jpeg.AsSpan(0, 2), .. segment, .. jpeg.AsSpan(2)];
    }
}
