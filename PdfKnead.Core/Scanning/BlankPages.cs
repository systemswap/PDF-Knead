namespace PdfKnead.Core.Scanning;

/// <summary>
/// Finds blank scanned pages (KONZEPT §5.25, „Leere Seiten weglassen“): the share of the page covered with ink, measured inside a margin
/// against the brightness of the paper itself.
/// </summary>
/// <remarks>
/// <para>The paper level is a bright percentile of the page, not pure white: recycled, grey or yellowish paper and a scanner set darker still
/// give blank pages. A sample counts as ink when it is clearly darker than the paper (below <see cref="InkLevel"/> of it); what shines through
/// from the back of thin paper and the grain of the paper stay above that.</para>
/// <para>A margin of each side is left out: the edges of the glass, the shadow of the lid and the edge of a sheet in the feeder are dark on
/// otherwise blank pages.</para>
/// <para>A dark page (a photo, a black sheet) is never blank: its paper level is low.</para>
/// </remarks>
public static class BlankPages
{
    /// <summary>The share in percent up to which a page counts as blank by default: about a page number or a few specks.</summary>
    public const double DefaultLimit = 0.15;

    /// <summary>The smallest share that can be set.</summary>
    public const double MinLimit = 0.01;

    /// <summary>The largest share that can be set: a short line of text still counts as content.</summary>
    public const double MaxLimit = 2.0;

    /// <summary>A sample darker than this part of the paper level counts as ink.</summary>
    public const double InkLevel = 0.7;

    /// <summary>The share of each side left out.</summary>
    public const double Margin = 0.02;

    // a paper level below this is no paper: a dark page
    private const int DarkPaper = 100;

    /// <summary>Whether a page is blank.</summary>
    /// <param name="page">The scanned page.</param>
    /// <param name="limit">Up to which share of the page in percent it counts as blank.</param>
    /// <returns>true if at most <paramref name="limit"/> percent of the page is covered.</returns>
    public static bool IsBlank(ScannedImage page, double limit = DefaultLimit) => Coverage(page) <= limit;

    /// <summary>The share of the page covered with ink, in percent (100 for a dark page).</summary>
    /// <param name="page">The scanned page.</param>
    /// <returns>0 … 100.</returns>
    public static double Coverage(ScannedImage page)
    {
        var (w, h, c) = (page.Width, page.Height, page.Channels);
        if (w <= 0 || h <= 0 || c is not (1 or 3) || page.Pixels.Length < (long)w * h * c) return 100;
        var left = (int)(w * Margin);
        var top = (int)(h * Margin);
        var right = Math.Max(left + 1, w - left);
        var bottom = Math.Max(top + 1, h - top);
        var histogram = new long[256];
        var pixels = page.Pixels;
        for (var y = top; y < bottom; y++)
        {
            var at = ((long)y * w + left) * c;
            for (var x = left; x < right; x++, at += c)
                histogram[c == 1 ? pixels[at] : Luma(pixels[at], pixels[at + 1], pixels[at + 2])]++;
        }
        var total = (long)(right - left) * (bottom - top);
        // the paper: the brightness that 90 % of the samples reach or stay below (the brightest ten per cent may be glare)
        var paper = 255;
        for (long seen = 0; paper > 0; paper--)
        {
            seen += histogram[paper];
            if (seen >= total / 10) break;
        }
        if (paper < DarkPaper) return 100;
        var ink = 0L;
        var limit = (int)(paper * InkLevel);
        for (var level = 0; level < limit; level++) ink += histogram[level];
        return ink * 100.0 / total;
    }

    // ITU-R BT.601 weights in 8-bit fixed point
    private static int Luma(byte r, byte g, byte b) => (r * 77 + g * 150 + b * 29) >> 8;
}

/// <summary>
/// Both sides with a feeder that reads only the fronts (KONZEPT §5.25, „Beidseitig manuell“): the fronts are scanned, the stack is turned
/// over and the backs are scanned. Which back comes first depends on the paper path of the device, not on the user: a feeder that lays the
/// sheets face down in its tray gives the last sheet's back first (turned over, 6 4 2); one that lays them face up gives the first sheet's
/// back first (2 4 6). Neither driver interface reports it, so the window offers to swap it and remembers the choice in the profile.
/// </summary>
public static class ManualDuplexOrder
{
    /// <summary>
    /// Puts fronts and backs into the order of the sheets: front 1, back 1, front 2, back 2 … With <paramref name="lastFirst"/> the j-th back
    /// read belongs to the last but j front, otherwise to the j-th. When fewer backs were read (the scan was stopped), they belong to the last
    /// sheets resp. the first; more backs than fronts go to the end.
    /// </summary>
    /// <typeparam name="T">The type of the pages.</typeparam>
    /// <param name="fronts">The fronts in the order read.</param>
    /// <param name="backs">The backs in the order read.</param>
    /// <param name="lastFirst">Whether the last sheet's back came first (the usual case: a feeder that lays the sheets face down).</param>
    /// <returns>The pages in the order of the sheets.</returns>
    public static List<T> Interleave<T>(IReadOnlyList<T> fronts, IReadOnlyList<T> backs, bool lastFirst = true)
    {
        var result = new List<T>(fronts.Count + backs.Count);
        var n = fronts.Count;
        for (var i = 0; i < n; i++)
        {
            result.Add(fronts[i]);
            var j = lastFirst ? n - 1 - i : i;
            if (j < backs.Count) result.Add(backs[j]);
        }
        for (var j = n; j < backs.Count; j++) result.Add(backs[j]);
        return result;
    }

    /// <summary>The back that belongs to the <paramref name="front"/>-th front (0-based), or −1 if it was not read.</summary>
    public static int BackOf(int front, int fronts, int backs, bool lastFirst = true)
    {
        var j = lastFirst ? fronts - 1 - front : front;
        return j >= 0 && j < backs ? j : -1;
    }
}
