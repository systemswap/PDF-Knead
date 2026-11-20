using PdfKnead.Core.Ocr;

namespace PdfKnead.Ai;

/// <summary>
/// Turns images into the inputs of the networks, as the models were trained: samples in the order blue, green, red, channel by channel
/// (planes); detection normalised by the mean and spread of its training images, recognition to −1 … 1.
/// </summary>
internal static class OcrInput
{
    /// <summary>The height of a line image for the recognition network.</summary>
    public const int LineHeight = 48;

    /// <summary>The narrowest line image (lines are padded to it).</summary>
    public const int MinLineWidth = 320;

    /// <summary>The widest line image; a longer line is squeezed to it.</summary>
    public const int MaxLineWidth = 4000;

    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Spread = [0.229f, 0.224f, 0.225f];

    /// <summary>
    /// The size the detection network sees an image at: the image's own size (at least 64 on its shorter side, at most
    /// <paramref name="maxSide"/> on its longer one), each side rounded to a multiple of 32 — the network halves the size five times.
    /// </summary>
    /// <param name="width">Width of the image.</param>
    /// <param name="height">Height of the image.</param>
    /// <param name="maxSide">The longest side.</param>
    /// <returns>Width and height for the network.</returns>
    public static (int Width, int Height) DetectionSize(int width, int height, int maxSide)
    {
        var scale = 1.0;
        if (Math.Min(width, height) < 64) scale = 64.0 / Math.Min(width, height);
        if (Math.Max(width, height) * scale > maxSide) scale = (double)maxSide / Math.Max(width, height);
        static int Round(double v) => Math.Max(32, (int)Math.Round(v / 32) * 32);
        return (Round(width * scale), Round(height * scale));
    }

    /// <summary>The input of the detection network: the image scaled to the given size.</summary>
    /// <param name="image">The image.</param>
    /// <param name="width">Width for the network.</param>
    /// <param name="height">Height for the network.</param>
    /// <param name="data">Receives 3 planes of width × height values.</param>
    public static void Detection(OcrImage image, int width, int height, Span<float> data)
    {
        var plane = width * height;
        double sx = (double)image.Width / width, sy = (double)image.Height / height;
        Span<float> bgr = stackalloc float[3];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                Sample(image, (x + 0.5) * sx - 0.5, (y + 0.5) * sy - 0.5, sx, sy, bgr);
                var i = y * width + x;
                for (var c = 0; c < 3; c++) data[c * plane + i] = (bgr[c] / 255f - Mean[c]) / Spread[c];
            }
    }

    /// <summary>The width a line takes in the recognition network: its length scaled to the line height, at most the batch width.</summary>
    /// <param name="corners">The line's corners in reading order.</param>
    /// <returns>The width in pixels.</returns>
    public static int LineWidth(OcrPoint[] corners)
    {
        var (length, thickness) = Extent(corners);
        return (int)Math.Clamp(Math.Ceiling(LineHeight * length / Math.Max(thickness, 1)), 1, MaxLineWidth);
    }

    /// <summary>Length (along the reading direction) and thickness of a line's quadrilateral.</summary>
    public static (double Length, double Thickness) Extent(OcrPoint[] c) =>
        (Math.Max(Distance(c[0], c[1]), Distance(c[3], c[2])), Math.Max(Distance(c[0], c[3]), Distance(c[1], c[2])));

    /// <summary>
    /// Writes a line into a batch of the recognition network: the quadrilateral sampled straight into a line image of
    /// <see cref="LineHeight"/> × <paramref name="lineWidth"/> (the corners say which way it reads); the rest of the batch width stays 0.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="corners">The line's corners in reading order (top left, top right, bottom right, bottom left of the text).</param>
    /// <param name="lineWidth">The width of the line image (from <see cref="LineWidth"/>).</param>
    /// <param name="batch">The batch input.</param>
    /// <param name="index">The place of the line in the batch.</param>
    /// <param name="batchWidth">The width of the batch.</param>
    public static void Line(OcrImage image, OcrPoint[] corners, int lineWidth, Span<float> batch, int index, int batchWidth)
    {
        var plane = LineHeight * batchWidth;
        var offset = index * 3 * plane;
        var (length, thickness) = Extent(corners);
        double sx = length / lineWidth, sy = thickness / LineHeight;
        Span<float> bgr = stackalloc float[3];
        for (var y = 0; y < LineHeight; y++)
        {
            var v = (y + 0.5) / LineHeight;
            for (var x = 0; x < lineWidth; x++)
            {
                var u = (x + 0.5) / lineWidth;
                var p = Bilinear(corners, u, v);
                Sample(image, p.X - 0.5, p.Y - 0.5, sx, sy, bgr);
                var i = offset + y * batchWidth + x;
                for (var c = 0; c < 3; c++) batch[i + c * plane] = bgr[c] / 127.5f - 1f;
            }
        }
    }

    /// <summary>The point at (u, v) of a quadrilateral (0 … 1 along its top edge, 0 … 1 down its left edge).</summary>
    public static OcrPoint Bilinear(OcrPoint[] c, double u, double v)
    {
        double tx = c[0].X + (c[1].X - c[0].X) * u, ty = c[0].Y + (c[1].Y - c[0].Y) * u;
        double bx = c[3].X + (c[2].X - c[3].X) * u, by = c[3].Y + (c[2].Y - c[3].Y) * u;
        return new OcrPoint(tx + (bx - tx) * v, ty + (by - ty) * v);
    }

    private static double Distance(OcrPoint a, OcrPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>
    /// The colour at a point (in sample coordinates: sample i at i) as blue, green, red: interpolated between the four nearest samples, and
    /// when a target pixel covers more than two samples per direction the mean of a few points across it — shrinking text a lot by picking
    /// single samples would lose strokes. Outside the image the edge repeats.
    /// </summary>
    private static void Sample(OcrImage image, double x, double y, double footX, double footY, Span<float> bgr)
    {
        int nx = Math.Clamp((int)Math.Ceiling(footX / 2), 1, 4), ny = Math.Clamp((int)Math.Ceiling(footY / 2), 1, 4);
        float b = 0, g = 0, r = 0;
        for (var j = 0; j < ny; j++)
            for (var i = 0; i < nx; i++)
            {
                double px = x + (nx == 1 ? 0 : ((i + 0.5) / nx - 0.5) * footX), py = y + (ny == 1 ? 0 : ((j + 0.5) / ny - 0.5) * footY);
                Interpolate(image, px, py, out var rr, out var gg, out var bb);
                r += rr;
                g += gg;
                b += bb;
            }
        float n = nx * ny;
        bgr[0] = b / n;
        bgr[1] = g / n;
        bgr[2] = r / n;
    }

    private static void Interpolate(OcrImage image, double x, double y, out float r, out float g, out float b)
    {
        x = Math.Clamp(x, 0, image.Width - 1);
        y = Math.Clamp(y, 0, image.Height - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, image.Width - 1), y1 = Math.Min(y0 + 1, image.Height - 1);
        float fx = (float)(x - x0), fy = (float)(y - y0);
        var px = image.Rgba;
        int a = (y0 * image.Width + x0) * 4, bI = (y0 * image.Width + x1) * 4, c = (y1 * image.Width + x0) * 4, d = (y1 * image.Width + x1) * 4;
        float Mix(int k) => (px[a + k] * (1 - fx) + px[bI + k] * fx) * (1 - fy) + (px[c + k] * (1 - fx) + px[d + k] * fx) * fy;
        r = Mix(0);
        g = Mix(1);
        b = Mix(2);
    }
}
