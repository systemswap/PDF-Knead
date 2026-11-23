namespace PdfKnead.Core.Pdf.Filters.Jpx;

/// <summary>
/// The Reader Requirements box of a JPX file (ISO/IEC 15444-2, M.6, M.11.1), tested against what the decoder can do (M.6.3): the masks of the
/// features it provides are combined with OR and compared with the Display Contents mask; a column of that mask no provided feature covers
/// names the features one of which is missing. Decided on 23.11.2026: evaluated and reported, the image still shown as far as it reads.
/// </summary>
internal static class JpxRequirements
{
    /// <summary>A vendor feature (VFi, a UUID) in <see cref="Missing"/>.</summary>
    public const int Vendor = -1;

    /// <summary>A column of the Display Contents mask that no listed feature covers (no reader can meet it) in <see cref="Missing"/>.</summary>
    public const int Unlisted = -2;

    /// <summary>
    /// Standard features of Table M.14 the decoder provides for drawing the image as PDF has it (ISO 32000-2, 7.4.9: the first compositing
    /// layer, its first codestream): no extensions (1); several layers, of which the first is drawn (2); Part 1 profile 1 and unrestricted
    /// (4, 5) and Part 2 codestreams (6, 72 — capabilities of Part 2 the decoder lacks are refused by the codestream itself); opacity, plain
    /// and premultiplied (9, 10); a chroma key, which PDF passes over (11); fragments in the file, in order or not (13, 14); an ICC profile
    /// (44); the enumerated spaces bi-level 1 and 2, YCbCr 1 to 3, YCCK, CMY, CMYK, CIELab, e-sRGB, ROMM-RGB, e-sYCC, YPbPr(1125/60) and
    /// YPbPr(1250/50) (47–51, 53–57, 60, 61, 71, 73, 74; JpxDecoder.Choose — before 73 and 74 were reported as a colour space PDF-Knead does
    /// not draw, although it draws them, M2, T.801 Table M.14, 23.11.2026); non-square samples, which the image matrix of the page places (62);
    /// the pixel formats fixed point, floating point, mantissa and exponent (80–82). scRGB (83) only where the decoder draws it
    /// (<see cref="ScRgb"/>): the box alone does not say whether the channels have an encoding of scRGB.
    /// </summary>
    private static readonly HashSet<int> Provided = [1, 2, 4, 5, 6, 9, 10, 11, 13, 14, 44, 47, 48, 49, 50, 51, 53, 54, 55, 56, 57, 60, 61, 62, 71, 72, 73, 74, 80, 81, 82];

    /// <summary>
    /// The scRGB colour space (Table M.14, value 83): JpxDecoder.Choose draws it only from channels of 16-bit unsigned integers or fixed or
    /// floating point numbers, the encodings the ICC Colour Encoding Registry fixes; for other channels it passes the specification over. So
    /// it counts as provided when the caller knows the image is drawn as scRGB, and as missing otherwise.
    /// </summary>
    public const int ScRgb = 83;

    /// <summary>Deprecated features of Table M.14: readers ignore their contribution (M.11.1) — they never count as missing.</summary>
    private static readonly HashSet<int> Deprecated = [3, 8, 12, 18, 19, 20, 22, 24, 27, 29, 31, 42, 43, 45, 46, 63, 64, 65, 66, 70];

    /// <summary>
    /// The features one of which is missing per column of the Display Contents mask that no provided feature covers (codes of Table M.14,
    /// <see cref="Vendor"/> for vendor features, <see cref="Unlisted"/> for a column no feature covers), in ascending order; empty if the decoder can display the file, or the box is damaged or has a
    /// mask length other than 1, 2, 4 or 8 (Table M.15).
    /// </summary>
    /// <param name="d">The file.</param>
    /// <param name="s">Start of the box contents.</param>
    /// <param name="e">End of the box contents.</param>
    /// <param name="scRgbDrawn">The image is drawn as scRGB (<see cref="ScRgb"/>).</param>
    public static int[] Missing(byte[] d, int s, int e, bool scRgbDrawn = false) => Evaluate(d, s, e, scRgbDrawn).Display;

    /// <summary>
    /// The same for the Fully Understand Aspects mask (M.6.3: the reader compares with both masks): what the decoder lacks to understand the
    /// file completely — metadata, signatures and the like beyond what drawing the image needs. Shown in the image information, not reported
    /// as a problem (decided on 23.11.2026).
    /// </summary>
    public static int[] NotUnderstood(byte[] d, int s, int e, bool scRgbDrawn = false) => Evaluate(d, s, e, scRgbDrawn).Understanding;

    /// <summary>Both comparisons of M.6.3 from one reading of the box; empty for a damaged box or a reserved mask length.</summary>
    private static (int[] Display, int[] Understanding) Evaluate(byte[] d, int s, int e, bool scRgbDrawn)
    {
        if (e - s < 1) return ([], []);
        int ml = d[s];
        if (ml is not (1 or 2 or 4 or 8)) return ([], []);
        var p = s + 1;
        if (!Mask(d, ref p, e, ml, out var understanding) || !Mask(d, ref p, e, ml, out var display)) return ([], []);
        var features = new List<(int Code, ulong Mask)>();
        if (p + 2 > e) return ([], []);
        var standard = d[p] << 8 | d[p + 1];
        p += 2;
        for (var i = 0; i < standard; i++)
        {
            if (p + 2 > e) return ([], []);
            var code = d[p] << 8 | d[p + 1];
            p += 2;
            if (!Mask(d, ref p, e, ml, out var mask)) return ([], []);
            features.Add((code, mask));
        }
        if (p + 2 > e) return ([], []);
        var vendors = d[p] << 8 | d[p + 1];
        p += 2;
        for (var i = 0; i < vendors; i++)
        {
            if (e - p < 16) return ([], []);
            p += 16;
            if (!Mask(d, ref p, e, ml, out var mask)) return ([], []);
            features.Add((Vendor, mask));
        }
        // the reader's compatibility mask: the OR of the masks of what it provides (M.6.3); a feature it provides that the file does not list is
        // not needed and adds nothing
        var covered = features.Where(f => Provided.Contains(f.Code) || Deprecated.Contains(f.Code) || scRgbDrawn && f.Code == ScRgb)
            .Aggregate(0UL, (m, f) => m | f.Mask);
        var listed = features.Aggregate(0UL, (m, f) => m | f.Mask);
        return (Open(display), Open(understanding));

        int[] Open(ulong expression)
        {
            var open = expression & ~covered;
            if (open == 0) return [];
            IEnumerable<int> missing = features.Where(f => (f.Mask & open) != 0).Select(f => f.Code);
            if ((open & ~listed) != 0) missing = missing.Append(Unlisted);
            return [.. missing.Distinct().Order()];
        }
    }

    /// <summary>A big-endian mask of <paramref name="ml"/> bytes at <paramref name="p"/>; false if it runs past <paramref name="e"/>.</summary>
    private static bool Mask(byte[] d, ref int p, int e, int ml, out ulong mask)
    {
        mask = 0;
        if (e - p < ml) return false;
        for (var i = 0; i < ml; i++) mask = mask << 8 | d[p + i];
        p += ml;
        return true;
    }

    /// <summary>The colour spaces of Table M.14 the decoder does not draw (the group „a colour space“ of <see cref="Describe"/>).</summary>
    private static readonly HashSet<int> ColourSpaces = [52, 58, 59, 73, 74, 83, 85];

    /// <summary>
    /// The missing features without the colour spaces where the colour of the file does not count (<paramref name="colourOfTheFile"/> false:
    /// the PDF gives the colour space, ISO 32000-2, 7.4.9) — a colour space the image does not use is nothing it lacks.
    /// </summary>
    public static int[] WithoutColourSpaces(int[] missing, bool colourOfTheFile) =>
        colourOfTheFile ? missing : missing.Where(code => !ColourSpaces.Contains(code)).ToArray();

    /// <summary>The missing features in words, grouped (for the problem list of the document).</summary>
    public static string Describe(IEnumerable<int> codes)
    {
        var groups = new List<string>();
        foreach (var code in codes)
        {
            var group = code switch
            {
                Vendor => Tr.T("Herstellermerkmale"),
                0 => Tr.T("eine nicht vollständig verstandene Datei"),
                7 => Tr.T("ein JPEG-Codestream"),
                15 or 16 => Tr.T("Teile in anderen Dateien oder im Netz"),
                17 or 21 or 23 or 32 or 33 => Tr.T("das Zusammensetzen mehrerer Ebenen oder Codestreams"),
                25 or 26 or 28 or 30 => Tr.T("Animation"),
                >= 34 and <= 38 or 67 => Tr.T("Metadaten"),
                39 or 40 => Tr.T("Signaturen oder Prüfsummen"),
                41 => Tr.T("eine Wiedergabe für die Druckvorstufe"),
                52 or 58 or 59 or 73 or 74 or 83 or 85 => Tr.T("ein Farbraum, den PDF-Knead nicht zeichnet"),
                68 or 69 or 84 or 86 => Tr.T("Erweiterungen des Codestreams"),
                >= 75 and <= 79 => Tr.T("ein JPEG-XR-Codestream"),
                _ => Tr.T("unbekannte Merkmale"),
            };
            if (!groups.Contains(group)) groups.Add(group);
        }
        return string.Join(", ", groups);
    }
}
