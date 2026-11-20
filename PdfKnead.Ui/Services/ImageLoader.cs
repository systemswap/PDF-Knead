using System.Runtime.InteropServices;
using PdfKnead.Core.Imaging;
using SkiaSharp;

namespace PdfKnead.Services;

/// <summary>
/// Loads image files (PNG, JPEG, WebP, BMP, GIF) for signatures and stamps, using Skia, which the
/// user interface brings along anyway; photos are rotated according to their EXIF orientation and scaled down.
/// Takes noticeable time for large photos: do not call it on the UI thread.
/// </summary>
public static class ImageLoader
{
    /// <summary>Maximum length of the longer side in pixels (a signature needs no more, and the file stays small).</summary>
    public const int MaxSide = 1600;

    /// <summary>
    /// Maximum longer side in pixels for inserted pictures (see KONZEPT.md, section 5.13): nearly full resolution, because saving scales
    /// down with Lanczos to 300 dpi at the final size and enough pixels must be available for that (A3 at 300 dpi: about 5,000 pixels).
    /// Not larger, to limit memory.
    /// </summary>
    public const int MaxPictureSide = 6000;

    /// <summary>Loads an image file, scaled down to <paramref name="maxSide"/> and oriented according to EXIF.</summary>
    /// <param name="path">Path of the image file.</param>
    /// <param name="maxSide">Maximum length of the longer side in pixels.</param>
    /// <returns>The image as unpremultiplied RGBA, or null if Skia cannot open or decode the file.</returns>
    public static RgbaImage? Load(string path, int maxSide = MaxSide)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null) return null;
        // Decode large photos at a reduced size straight away (JPEG in eighths, at almost no extra cost): the smallest step
        // that still has at least maxSide; Downscale does the rest with good smoothing. Skia rounds a requested
        // step down, hence the eighths are tried from the bottom up.
        var size = codec.Info.Size;
        if (Math.Max(size.Width, size.Height) > maxSide)
            for (var eighths = 1; eighths < 8; eighths++)
                if (codec.GetScaledDimensions(eighths / 8f) is var scaled && Math.Max(scaled.Width, scaled.Height) >= maxSide)
                {
                    size = scaled;
                    break;
                }
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new byte[info.BytesSize];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var result = codec.GetPixels(info, handle.AddrOfPinnedObject());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;
        }
        finally
        {
            handle.Free();
        }
        // Scale down first, then rotate: fewer pixels to move.
        return Orient(ImageTools.Downscale(new RgbaImage(info.Width, info.Height, pixels), maxSide), codec.EncodedOrigin);
    }

    /// <summary>
    /// Applies the EXIF orientation (phone photos would otherwise appear sideways or mirrored), all eight cases. For each case, the
    /// source of the target pixel (x, y): sx = ax·x + bx·y + cx, sy = ay·x + by·y + cy.
    /// </summary>
    internal static RgbaImage Orient(RgbaImage image, SKEncodedOrigin origin)
    {
        int ws = image.Width - 1, hs = image.Height - 1;
        (int Ax, int Bx, int Cx, int Ay, int By, int Cy)? map = origin switch
        {
            SKEncodedOrigin.TopRight => (-1, 0, ws, 0, 1, 0),      // mirrored
            SKEncodedOrigin.BottomRight => (-1, 0, ws, 0, -1, hs), // 180°
            SKEncodedOrigin.BottomLeft => (1, 0, 0, 0, -1, hs),    // mirrored upside down
            SKEncodedOrigin.LeftTop => (0, 1, 0, 1, 0, 0),         // mirrored at the main diagonal
            SKEncodedOrigin.RightTop => (0, 1, 0, -1, 0, hs),      // 90° clockwise
            SKEncodedOrigin.RightBottom => (0, -1, ws, -1, 0, hs), // mirrored at the anti-diagonal
            SKEncodedOrigin.LeftBottom => (0, -1, ws, 1, 0, 0),    // 270°
            _ => null,
        };
        if (map is not { } m) return image;
        var turned = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (w, h) = turned ? (image.Height, image.Width) : (image.Width, image.Height);
        var result = RgbaImage.Blank(w, h);
        var source = MemoryMarshal.Cast<byte, int>(image.Pixels.AsSpan());
        var target = MemoryMarshal.Cast<byte, int>(result.Pixels.AsSpan());
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                target[y * w + x] = source[(m.Ay * x + m.By * y + m.Cy) * image.Width + m.Ax * x + m.Bx * y + m.Cx];
        return result;
    }
}
