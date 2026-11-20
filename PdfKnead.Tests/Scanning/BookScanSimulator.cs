using PdfKnead.Core.Scanning;

namespace PdfKnead.Tests.Scanning;

/// <summary>How a book page lies on the glass and how the scanner sees it.</summary>
/// <param name="LiftMm">How far the page rises from the glass at the spine, in millimetres.</param>
/// <param name="CurlMm">How far from the spine the page still rises (beyond, it lies flat), in millimetres.</param>
/// <param name="LensMm">
/// Distance of the lens from the glass for a scanner with optics, in millimetres; null for a contact sensor (no perspective). Lifted parts
/// look smaller towards the optical axis.
/// </param>
/// <param name="SpineAlongSensor">
/// Whether the spine lies along the sensor line (the book turned on the glass, as an A5 spread on A4): the perspective then acts along the
/// spine and the text lines bend; across, it acts across the spine and only compresses.
/// </param>
/// <param name="AxisAt">Where the optical axis lies along the sensor line, as a share of the image (0.5: in the middle).</param>
/// <param name="Shade">How much darker the page gets at the spine (0.4: 40 % darker).</param>
/// <param name="BlurPerMm">Blur radius in pixels per millimetre of lift (contact sensors have little depth of field).</param>
/// <param name="SkewDegrees">How askew the book lies, clockwise.</param>
public sealed record BookLie(double LiftMm, double CurlMm, double? LensMm, bool SpineAlongSensor, double AxisAt = 0.5, double Shade = 0.35,
    double BlurPerMm = 0.15, double SkewDegrees = 0);

/// <summary>
/// Makes flat pages look like book pages scanned on a flatbed: the page rises from the glass towards the spine (a smooth curl), and the
/// scanner sees it foreshortened across the spine, smaller where it is lifted (with optics), darker and blurred. The test pages of the book
/// correction are made with it — the flat page is known, so the correction can be measured.
/// </summary>
/// <remarks>
/// Model: the height over the glass at the distance u from the spine is z(u) = lift · (1 − u/curl)² up to the curl, 0 beyond. The paper
/// position of a point seen at u is the arc length s(u) = ∫₀ᵘ √(1 + z′²). With a lens at the distance f from the glass, a lifted point
/// appears at axis + (p − axis) · f / (f + z) along the sensor line.
/// </remarks>
public static class BookScanSimulator
{
    /// <summary>One page with its spine on the left or on the right.</summary>
    /// <param name="flat">The flat page (grey or colour).</param>
    /// <param name="lie">How it lies.</param>
    /// <param name="spineRight">Whether the spine is on the right (a left page of a book).</param>
    /// <returns>The page as scanned.</returns>
    public static ScannedImage Page(ScannedImage flat, BookLie lie, bool spineRight)
    {
        var page = Warp(flat, lie, spineRight);
        return lie.SkewDegrees == 0 ? page : Rotate(page, lie.SkewDegrees);
    }

    /// <summary>A double page: the left page with its spine on the right, the right page with its spine on the left, side by side.</summary>
    /// <param name="left">The flat left page.</param>
    /// <param name="right">The flat right page (same height).</param>
    /// <param name="lie">How the book lies.</param>
    /// <param name="turned">Whether the spread lies turned by a quarter on the glass (A5 spread on A4): the image comes turned, as the scanner delivers it.</param>
    /// <returns>The double page as scanned.</returns>
    public static ScannedImage Spread(ScannedImage left, ScannedImage right, BookLie lie, bool turned)
    {
        var a = Warp(left, lie, spineRight: true);
        var b = Warp(right, lie, spineRight: false);
        var c = a.Channels;
        var (w, h) = (a.Width + b.Width, Math.Max(a.Height, b.Height));
        var pixels = new byte[w * h * c];
        Array.Fill(pixels, (byte)235);
        for (var y = 0; y < h; y++)
        {
            if (y < a.Height) Array.Copy(a.Pixels, y * a.Width * c, pixels, y * w * c, a.Width * c);
            if (y < b.Height) Array.Copy(b.Pixels, y * b.Width * c, pixels, (y * w + a.Width) * c, b.Width * c);
        }
        var spread = new ScannedImage(w, h, c, a.Dpi, pixels);
        if (lie.SkewDegrees != 0) spread = Rotate(spread, lie.SkewDegrees);
        return turned ? ScanImageOps.Rotate(spread, 1) : spread;
    }

    private static ScannedImage Warp(ScannedImage flat, BookLie lie, bool spineRight)
    {
        var (w, h, c) = (flat.Width, flat.Height, flat.Channels);
        var pxPerMm = flat.Dpi / 25.4;
        double Z(double uMm) => uMm >= lie.CurlMm ? 0 : lie.LiftMm * Math.Pow(1 - uMm / lie.CurlMm, 2);

        // paper position (pixels from the spine) for every projected position u (pixels from the spine), by the arc length
        var length = w;
        var paper = new List<double> { 0 };
        for (var u = 1; paper[^1] < w && u < 4 * w; u++)
        {
            var slope = (Z(u / pxPerMm) - Z((u - 1) / pxPerMm)) * pxPerMm;   // dz/du with z in pixels, u in steps of one pixel
            paper.Add(paper[^1] + Math.Sqrt(1 + slope * slope));
            length = u;
        }
        // across the spine with optics (spine across the sensor line): a lifted point appears closer to the axis; the image keeps the projected width
        var outW = length;
        var across = new double[outW];   // output column → projected position u
        if (lie.LensMm is { } f && !lie.SpineAlongSensor)
        {
            var axis = lie.AxisAt * outW;
            var seen = new double[outW];
            for (var u = 0; u < outW; u++)
            {
                var x = spineRight ? outW - 1 - u : u;   // image column of u
                var seenX = axis + (x - axis) * f / (f + Z(u / pxPerMm));
                seen[u] = spineRight ? outW - 1 - seenX : seenX;
            }
            // invert: for each output position the u that appears there
            var k = 0;
            for (var o = 0; o < outW; o++)
            {
                while (k < outW - 2 && seen[k + 1] < o) k++;
                var t = seen[k + 1] - seen[k] is var d && Math.Abs(d) > 1e-9 ? (o - seen[k]) / d : 0;
                across[o] = Math.Clamp(k + t, 0, outW - 1);
            }
        }
        else
        {
            for (var o = 0; o < outW; o++) across[o] = o;
        }

        var result = new byte[outW * h * c];
        var axisY = lie.AxisAt * h;
        for (var ox = 0; ox < outW; ox++)
        {
            var u = across[spineRight ? outW - 1 - ox : ox];
            var z = Z(u / pxPerMm);
            var s = paper[Math.Clamp((int)u, 0, paper.Count - 1)];
            var sx = spineRight ? w - 1 - s : s;
            // along the spine with optics (spine along the sensor line): lifted parts look smaller towards the axis, the lines bend
            var scale = lie.LensMm is { } lens && lie.SpineAlongSensor ? (lens + z) / lens : 1;
            var shade = 1 - lie.Shade * (lie.LiftMm > 0 ? z / lie.LiftMm : 0);
            var blur = (int)Math.Round(lie.BlurPerMm * z);
            for (var oy = 0; oy < h; oy++)
            {
                var sy = axisY + (oy - axisY) * scale;
                for (var ch = 0; ch < c; ch++)
                {
                    double value;
                    if (sx < 0 || sx > w - 1 || sy < 0 || sy > h - 1) value = 235;
                    else value = blur > 0 ? Box(flat, sx, sy, ch, blur) : Sample(flat, sx, sy, ch);
                    result[(oy * outW + ox) * c + ch] = (byte)Math.Clamp(value * shade, 0, 255);
                }
            }
        }
        return new ScannedImage(outW, h, c, flat.Dpi, result);
    }

    private static double Sample(ScannedImage img, double x, double y, int ch)
    {
        var (x0, y0) = ((int)x, (int)y);
        var (x1, y1) = (Math.Min(x0 + 1, img.Width - 1), Math.Min(y0 + 1, img.Height - 1));
        var (fx, fy) = (x - x0, y - y0);
        double P(int px, int py) => img.Pixels[(py * img.Width + px) * img.Channels + ch];
        return (P(x0, y0) * (1 - fx) + P(x1, y0) * fx) * (1 - fy) + (P(x0, y1) * (1 - fx) + P(x1, y1) * fx) * fy;
    }

    private static double Box(ScannedImage img, double x, double y, int ch, int r)
    {
        var (sum, n) = (0.0, 0);
        for (var dy = -r; dy <= r; dy++)
            for (var dx = -r; dx <= r; dx++)
            {
                var (px, py) = (Math.Clamp((int)x + dx, 0, img.Width - 1), Math.Clamp((int)y + dy, 0, img.Height - 1));
                sum += img.Pixels[(py * img.Width + px) * img.Channels + ch];
                n++;
            }
        return sum / n;
    }

    /// <summary>Turns the page by a small angle about its middle (the corners fill with paper).</summary>
    private static ScannedImage Rotate(ScannedImage img, double degrees)
    {
        var (w, h, c) = (img.Width, img.Height, img.Channels);
        var (sin, cos) = Math.SinCos(-degrees * Math.PI / 180);
        var result = new byte[w * h * c];
        var (cx, cy) = (w / 2.0, h / 2.0);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var sx = cx + (x - cx) * cos - (y - cy) * sin;
                var sy = cy + (x - cx) * sin + (y - cy) * cos;
                for (var ch = 0; ch < c; ch++)
                    result[(y * w + x) * c + ch] = sx < 0 || sx > w - 1 || sy < 0 || sy > h - 1 ? (byte)235 : (byte)Sample(img, sx, sy, ch);
            }
        return new ScannedImage(w, h, c, img.Dpi, result);
    }
}
