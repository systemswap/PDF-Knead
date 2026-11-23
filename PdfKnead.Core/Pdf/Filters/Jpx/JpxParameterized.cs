using PdfKnead.Core.Pdf.Graphics.Icc;

namespace PdfKnead.Core.Pdf.Filters.Jpx;

/// <summary>
/// The Parameterized colour specification (METH 5; ISO/IEC 15444-2, M.11.7.2, M.11.7.3.4, Table M.28bis): COLPRIMS, TRANSFC and MATCOEFFS
/// (16 bits each) and VIDFRNG (the top bit of the seventh byte) carry ColourPrimaries, TransferCharacteristics, MatrixCoefficients and
/// VideoFullRangeFlag of ITU-T H.273. The samples are read back to linear light of the indicated primaries in double precision — the
/// quantization of H.273, 8.3 undone (full or limited range at the bit depth of each component), the matrix inverted, the transfer
/// characteristic inverted as Table 3 gives it — and written once, at the end, as 8 bits with the sRGB tone curve; the image carries an ICC
/// profile made of the primaries and that curve (primaries 1 are those of sRGB: plain RGB). One component is luma alone: drawn as grey with
/// the luminance of the pixel. A code that is unspecified or reserved (H.273, 3.6 and 3.8) makes the whole specification not applicable: the
/// next one counts, as for any colour specification that is not supported (docs/FEHLERGEDAECHTNIS.md, No. 1044).
/// <para>
/// PQ and HLG (TransferCharacteristics 16 and 18) carry light above the reference white of an SDR picture: it is brought onto the page with
/// the reference white of ITU-R BT.2408 and the highlight roll-off of its Annex 5 (see <see cref="ToPage"/>); ICtCp (MatrixCoefficients 14)
/// is read with them and with the other characteristics (docs/FEHLERGEDAECHTNIS.md, No. 1055). Light beyond the primaries — the extended
/// characteristics 11, 12 and 13 with MatrixCoefficients other than 0, and sYCC, e-sRGB and e-sYCC — goes into a wider working space where an
/// image has such colours (<see cref="WideProfile"/>; No. 1056, No. 1072). The enumerated YCbCr and YPbPr spaces, sYCC, e-sYCC, e-sRGB,
/// YCCK and scRGB of JPX (<see cref="Enumerated"/>) are read by the same arithmetic, from the full codes to a single 8-bit step (No. 1057,
/// 1071, 1072, 1078, 1079); their signed channels from the value of no light (<see cref="FromZero"/>; No. 1066), as those of the
/// Parameterized method (No. 1076).
/// </para>
/// </summary>
internal sealed class JpxParameterized
{
    /// <summary>ColourPrimaries (H.273, Table 2): x, y of red, green, blue and white. 2 is unspecified; 0, 3, 13–21, 23–255 are reserved.</summary>
    private static readonly Dictionary<int, (double X, double Y)[]> PrimaryTable = new()
    {
        [1] = [(0.640, 0.330), (0.300, 0.600), (0.150, 0.060), (0.3127, 0.3290)],
        [4] = [(0.67, 0.33), (0.21, 0.71), (0.14, 0.08), (0.310, 0.316)],
        [5] = [(0.64, 0.33), (0.29, 0.60), (0.15, 0.06), (0.3127, 0.3290)],
        [6] = [(0.630, 0.340), (0.310, 0.595), (0.155, 0.070), (0.3127, 0.3290)],
        [7] = [(0.630, 0.340), (0.310, 0.595), (0.155, 0.070), (0.3127, 0.3290)],
        [8] = [(0.681, 0.319), (0.243, 0.692), (0.145, 0.049), (0.310, 0.316)],
        [9] = [(0.708, 0.292), (0.170, 0.797), (0.131, 0.046), (0.3127, 0.3290)],
        // CIE 1931 XYZ: X, Y, Z as primaries, the centre white 1/3, 1/3
        [10] = [(1.0, 0.0), (0.0, 1.0), (0.0, 0.0), (1.0 / 3, 1.0 / 3)],
        [11] = [(0.680, 0.320), (0.265, 0.690), (0.150, 0.060), (0.314, 0.351)],
        [12] = [(0.680, 0.320), (0.265, 0.690), (0.150, 0.060), (0.3127, 0.3290)],
        [22] = [(0.630, 0.340), (0.295, 0.605), (0.155, 0.077), (0.3127, 0.3290)],
    };

    /// <summary>TransferCharacteristics read (H.273, Table 3). Passed over: 0, 3, 19–255 reserved, 2 unspecified.</summary>
    private static readonly HashSet<int> Transfers = [1, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18];

    /// <summary>MatrixCoefficients read (H.273, Table 4, equations 23–87). Passed over: 3, 18–255 reserved, 2 unspecified.</summary>
    private static readonly HashSet<int> Matrices = [0, 1, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17];

    // Segment constants of Table 3, derived as 8.2 defines them: α and β give continuity of value and slope where the segments meet
    private static readonly (double Alpha, double Beta) Bt709 = Continuous(4.5, 0.45);
    private static readonly (double Alpha, double Beta) Smpte240 = Continuous(4.0, 0.45);
    private static readonly (double Alpha, double Beta) Srgb = Continuous(12.92, 1 / 2.4);

    // PQ (H.273, Table 3, value 16; ITU-R BT.2100): the constants as the table gives them
    private const double PqC1 = 107.0 / 128, PqC2 = 2413.0 / 128, PqC3 = 2392.0 / 128, PqM = 2523.0 / 32, PqN = 1305.0 / 8192;

    // HLG (H.273, Table 3, value 18)
    private const double HlgA = 0.17883277, HlgB = 0.28466892, HlgC = 0.55991073;

    /// <summary>PQ: Lo = 1 is 10 000 cd/m² (H.273, Table 3, value 16).</summary>
    private const double PqPeak = 10000;

    /// <summary>
    /// HDR Reference White, 203 cd/m² (ITU-R BT.2408, 2.1 and Table 1: a 100 % reflectance white card, diffuse white and graphics white on a
    /// PQ display or a 1 000 cd/m² HLG display — 58 % PQ, 75 % HLG). It is the peak of the page: the page white has its luminance.
    /// </summary>
    private const double ReferenceWhite = 203;

    /// <summary>
    /// The common peak luminance of PQ and HLG, 1 000 cd/m² (ITU-R BT.2408, 6.2: the industry consensus for converting between them). HLG is
    /// displayed at this nominal peak (BT.2408, 2.1 defines the reference white for it); PQ light above it is first rolled off to it as 6.4,
    /// method 2 describes (a static EETF), so that PQ and HLG of the same picture come onto the page alike.
    /// </summary>
    private const double ReferencePeak = 1000;

    /// <summary>
    /// The system gamma of the HLG OOTF at the nominal peak of 1 000 cd/m²: γ = 1.2 + 0.42·log10(LW / 1 000) = 1.2 (ITU-R BT.2390, 6.2;
    /// ITU-R BT.2408, 3.2, Table 3), for the reference viewing environment (no surround adjustment, BT.2408 Table 5: grading suite 0.00).
    /// </summary>
    private const double HlgGamma = 1.2;

    // The PQ values of the luminances the roll-off works with (EOTF⁻¹ of BT.2408, Annex 5; black LB = 0 for both stages, 6.2 and Annex 5)
    private static readonly double PqBlack = PqSignal(0);
    private static readonly double PqFull = PqSignal(PqPeak);
    private static readonly double PqReferencePeak = PqSignal(ReferencePeak);
    private static readonly double PqReferenceWhite = PqSignal(ReferenceWhite);

    /// <summary>The thresholds of 8-bit sRGB-encoded output in linear light: value i + 1 begins where the encoded value reaches (i + 0.5) / 255.</summary>
    private static readonly double[] OutputSteps = [.. Enumerable.Range(0, 255).Select(i => SrgbDecode((i + 0.5) / 255))];

    /// <summary>
    /// Light beyond these limits changes the 8-bit output when clipped (more than half a step of the sRGB curve below 0 or above 1): only then
    /// is an image taken into the wide working space.
    /// </summary>
    private static readonly double GamutLow = -0.5 / 255 / 12.92, GamutHigh = Math.Pow((255.5 / 255 + 0.055) / 1.055, 2.4);

    /// <summary>
    /// The wide working space: the primaries of ITU-R BT.2020 (H.273, Table 2, value 9) under D65, with the sRGB tone curve. Chosen over
    /// ROMM RGB (ISO 22028-2), whose primaries reach further but whose 1.8 power curve gives coarser 8-bit steps: over colours inside sRGB
    /// written as 8 bits, adjacent codes differ by up to 3.0 CIEDE2000 in ROMM RGB (99 % below 1.6) against 1.7 here (99 % below 1.2; sRGB
    /// itself 1.2 and 0.8). The primaries of BT.2020 lie on the spectral locus and hold the colours of real surfaces and of today's wide-gamut
    /// displays and printers; what lies beyond them is clipped there (docs/FEHLERGEDAECHTNIS.md, No. 1056).
    /// </summary>
    private const int WidePrimaries = 9;

    /// <summary>The description of <see cref="WideProfile"/>.</summary>
    internal const string WideName = "Wide gamut RGB: ITU-R BT.2020 primaries, sRGB tone curve";

    private static readonly Lazy<byte[]> WideBytes = new(() =>
    {
        var p = PrimaryTable[WidePrimaries];
        return CalibratedProfile.Primaries(p[0], p[1], p[2], p[3], WideName);
    });

    private readonly double _kr, _kb;
    private readonly double[] _luminance;
    private readonly double[,]? _lmsToRgb, _iptToLms;
    private readonly double _nb, _pb, _nr, _pr;

    /// <summary>The domain of V for TransferCharacteristics 12: the values of L = −0.25 and L = 1.33.</summary>
    private readonly double _extendedLow, _extendedHigh;

    /// <summary>E′ may lie beyond 0 … 1 (H.273, 8.3: TransferCharacteristics 11, 12, and 13 unless MatrixCoefficients is 0; e-sRGB, e-sYCC).</summary>
    private readonly bool _extended;

    /// <summary>The colour differences stored at 1/<c>_chroma</c> of their size (e-sYCC: 2, PIMA 7667, B.1, equation B3).</summary>
    private readonly double _chroma = 1;

    /// <summary>The components are e-sRGB codes (PIMA 7667, 4.4.3, equations 3–5) in the order R, G, B.</summary>
    private readonly bool _esrgb;

    /// <summary>R′, G′, B′ are sRGB values themselves (the enumerated spaces of JPX): written as 8 bits without going through linear light.</summary>
    private readonly bool _direct;

    /// <summary>Linear light of the primaries → linear light of the wide working space; null where it is not needed.</summary>
    private readonly double[,]? _toWide;

    /// <summary>YCCK (T.801 Table M.25, value 13): Y, Cb, Cr of the inverted C, M, Y as YCbCr(2), and K.</summary>
    private readonly bool _ycck;

    /// <summary>scRGB (T.801 Table M.25, value 25): R, G, B as 16-bit codes of linear light, (C − 4096) / 8192 (<see cref="Enumerated"/>).</summary>
    private readonly bool _scrgb;

    /// <summary>Whether this is scRGB (T.801 Table M.25, value 25): what the Reader Requirements box calls feature 83 (JpxRequirements.ScRgb).</summary>
    public bool IsScRgb => _scrgb;

    private JpxParameterized(int primaries, int transfer, int matrix, bool fullRange, bool? extended = null, double chroma = 1, bool esrgb = false,
        bool direct = false, bool fromZero = false, bool ycck = false, bool scrgb = false)
    {
        MapsSigned = fromZero;
        _ycck = ycck;
        _scrgb = scrgb;
        Primaries = primaries;
        Transfer = transfer;
        Matrix = matrix;
        FullRange = fullRange;
        _extended = extended ?? (transfer is 11 or 12 || transfer == 13 && matrix != 0);
        _chroma = chroma;
        _esrgb = esrgb;
        _direct = direct;
        var p = PrimaryTable[primaries];
        // RGB → XYZ with the white at Y = 1; its middle row is the luminance of each primary
        var toXyz = CalibratedProfile.ToXyz(p[0], p[1], p[2], White(p[3]));
        _luminance = [toXyz[1, 0], toXyz[1, 1], toXyz[1, 2]];
        (_kr, _kb) = matrix switch
        {
            1 => (0.2126, 0.0722),
            4 => (0.30, 0.11),
            5 or 6 => (0.299, 0.114),
            7 => (0.212, 0.087),
            9 or 10 => (0.2627, 0.0593),
            // chromaticity-derived (equations 39–44): the luminance of red and blue — the same as the middle row of RGB → XYZ
            12 or 13 => (_luminance[0], _luminance[2]),
            _ => (0, 0),
        };
        if (matrix is 10 or 13)
        {
            // equations 72–75: the limits of the colour differences, through the transfer characteristic
            _nb = Forward(1 - _kb);
            _pb = 1 - Forward(_kb);
            _nr = Forward(1 - _kr);
            _pr = 1 - Forward(_kr);
        }
        if (transfer == 12) (_extendedLow, _extendedHigh) = (Forward(-0.25), Forward(1.33));
        if (matrix == 15)
        {
            // IPT-C2: equations 17–19 (RGB → LMS) and 85–87 (L′M′S′ → I, P, T), both inverted
            _lmsToRgb = CalibratedProfile.Invert(Scaled(new double[,] { { 1747, 2169, 180 }, { 673, 3029, 394 }, { 50, 207, 3839 } }));
            _iptToLms = CalibratedProfile.Invert(Scaled(new double[,] { { 1638, 1638, 820 }, { 18248, -19870, 1622 }, { 3300, 1463, -4763 } }));
        }
        else if (matrix == 14)
        {
            // ICtCp: equations 14–16 (RGB → LMS); L′M′S′ → I, CT, CP by equations 82–84 for HLG, otherwise 79–81 — H.273 applies these to
            // every other characteristic, so ICtCp with an SDR characteristic is read with them too (designed for PQ). Both inverted
            _lmsToRgb = CalibratedProfile.Invert(Scaled(new double[,] { { 1688, 2146, 262 }, { 683, 2951, 462 }, { 99, 309, 3688 } }));
            _iptToLms = CalibratedProfile.Invert(Scaled(transfer == 18
                ? new double[,] { { 2048, 2048, 0 }, { 3625, -7465, 3840 }, { 9500, -9212, -288 } }
                : new double[,] { { 2048, 2048, 0 }, { 6610, -13613, 7003 }, { 17933, -17390, -543 } }));
        }
        if (_extended && primaries != WidePrimaries)
        {
            // linear light of the primaries → XYZ → Bradford to the white of the wide space (D65) → its linear light; Bradford as the profiles
            // adapt to D50, so a colour inside both spaces is drawn the same through either profile
            var w = PrimaryTable[WidePrimaries];
            var wideToXyz = CalibratedProfile.ToXyz(w[0], w[1], w[2], White(w[3]));
            _toWide = CalibratedProfile.Multiply(CalibratedProfile.Invert(wideToXyz),
                CalibratedProfile.Multiply(CalibratedProfile.Bradford(White(p[3]), White(w[3])), toXyz));
        }
    }

    public int Primaries { get; }

    public int Transfer { get; }

    public int Matrix { get; }

    public bool FullRange { get; }

    /// <summary>PQ or HLG: light above the reference white, brought onto the page by <see cref="ToPage"/>.</summary>
    private bool HighDynamicRange => Transfer is 16 or 18;

    /// <summary>
    /// Signed channels are mapped from the value of no light (<see cref="FromZero"/>): the enumerated spaces of T.801 M.11.7.2 whose colour of
    /// no light has unique values, and the Parameterized method — „for all other colourspaces“ of M.11.7.2 covers it as well (decided on
    /// 23.11.2026; before its signed components were read as the offset binary codes of H.273 they arrive in, docs/FEHLERGEDAECHTNIS.md,
    /// No. 1076). False for YCCK, whose colour of no light has no unique values.
    /// </summary>
    public bool MapsSigned { get; }

    /// <summary>
    /// Fixed and floating point channels are linear light themselves (scRGB): the assembly gives them as <see cref="NumberCode"/> of their
    /// numbers as they are, not as levels of 0 … 1.
    /// </summary>
    public bool ReadsNumbers => _scrgb;

    /// <summary>
    /// The linear light of a fixed or floating point channel of scRGB as the 16-bit code that stands for it, C = 8192·R + 4096, not rounded —
    /// within the range of the codes (R −0.5 … 7.4999, the range of the ICC Colour Encoding Registry); not a number as 0, no light.
    /// </summary>
    public static double NumberCode(double linear) => double.IsNaN(linear) ? ScRgbOffset : Math.Clamp(ScRgbScale * linear + ScRgbOffset, 0, 65535);

    /// <summary>scRGB: the code of linear light 0, and the codes per unit of linear light (ICC Colour Encoding Registry: −0.5 at code 0).</summary>
    private const double ScRgbOffset = 4096, ScRgbScale = 8192;

    /// <summary>
    /// The METHDAT of a Parameterized colour specification (Table M.28bis): seven bytes at least, big-endian; the seven reserved bits of the last
    /// byte are not looked at. Null when shorter, or when a code is not applicable (unspecified, reserved, beyond the 0–255 of H.273).
    /// </summary>
    public static JpxParameterized? Read(byte[]? methdat)
    {
        if (methdat is not { Length: >= 7 }) return null;
        int primaries = methdat[0] << 8 | methdat[1], transfer = methdat[2] << 8 | methdat[3], matrix = methdat[4] << 8 | methdat[5];
        var fullRange = (methdat[6] & 0x80) != 0;
        if (!PrimaryTable.ContainsKey(primaries) || !Transfers.Contains(transfer) || !Matrices.Contains(matrix)) return null;
        return new JpxParameterized(primaries, transfer, matrix, fullRange, fromZero: true);
    }

    /// <summary>
    /// The enumerated spaces of JPX (ISO/IEC 15444-2, Table M.25) that are luma and colour differences or offset-coded RGB, as the same
    /// arithmetic: sYCC (18; IEC 61966-2-1, Annex G) and YCbCr(2) (3) — full range with the matrix of BT.601, K_R 0.299 and K_B 0.114
    /// (MatrixCoefficients 5); YCbCr(1) and YCbCr(3) (1, 4) — the same in the limited range of BT.601; e-sYCC (24; PIMA 7667, Annex B) — the
    /// colour differences at half their size (B3), the luma clipped to 0 … 1 (B5), R′G′B′ beyond 0 … 1 (B4: about −0.75 … 1.25); e-sRGB (20;
    /// PIMA 7667, 4.4.3 and 4.5) — R′ = (C − 2^(n−2) − 2^(n−3)) / (255·2^(n−9)). R′G′B′ are sRGB values (the sRGB primaries and curve);
    /// for e-sYCC and e-sRGB the curve extends beyond 0 … 1 as PIMA 7667, equations 6a–6c give it (odd around 0, the power above 1 too).
    /// Before, Y, Cb and Cr were first written as 8 bits each and converted from there (docs/FEHLERGEDAECHTNIS.md, No. 1057). Null for
    /// any other space.
    /// <para>
    /// sYCC is an extended encoding (IEC 61966-2-1, Annex G, as T.801 Table M.25 names it): its R′G′B′ reach beyond 0 … 1, the sRGB curve
    /// continued; colours beyond sRGB go into the wide working space as with e-sYCC — an image without them stays plain RGB, bit for bit as
    /// before (decided on 23.11.2026: the larger working space for extended spaces; before they were clipped at the edge of sRGB, No. 1072).
    /// YCbCr(2) has the range of BT.601 and stays within sRGB.
    /// </para>
    /// <para>
    /// YPbPr(1125/60) (22) and YPbPr(1250/50) (23): the HDTV systems of ITU-R BT.709-3, Part I, which Table M.25 names, in the limited range of
    /// its digital representation for 8 bits (item 6.10: Y black 16, peak 235; Pb, Pr achromatic 128, 16 … 240), at other depths scaled by
    /// 2^(n−8) — the quantization of H.273, equations 30–32; for 1125/60 BT.709-3 adds two bits to the 8-bit words for 10 bits (item 6.11),
    /// for 1250/50 it leaves 10 bits under study, and they are read the same way. Both systems share the transfer characteristic (item 1.2) and
    /// the primaries with D65 (items 1.3, 1.4: R 0.640/0.330, G 0.300/0.600, B 0.150/0.060) — those of BT.709 (H.273 values 1 of Tables 2 and
    /// 3); they differ in the luma equation (item 4.2): 1125/60 with K_R 0.2126, K_B 0.0722 (H.273 value 1 of Table 4), 1250/50 with K_R 0.299,
    /// K_B 0.114 (H.273 value 5), and the colour differences of item 4.3 follow from them. Source: BT.709-3 Part I, items 1.2, 1.3, 4.2. Before
    /// 23.11.2026 1250/50 was read with the primaries of H.273 value 5 (green 0.29/0.60), an assumption made while BT.709-3 was not at hand
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1071, corrected by No. 1078).
    /// </para>
    /// <para>
    /// scRGB (25), linear: source the ICC Colour Encoding Registry (scRGB entry), not the text of IEC 61966-2-2, which is not at hand — to be
    /// checked once the standard is. The registry gives the primaries of sRGB (BT.709) under D65, a linear characteristic, the range of linear
    /// RGB −0.5 to 7.4999, 16 bits, scene-referred; so a 16-bit code C stands for R = (C − 4096) / 8192 (0 → −0.5, 65535 → 7.49987). Fixed and
    /// floating point channels carry the linear value itself (<see cref="NumberCode"/>). Other bit depths the registry does not fix: such
    /// channels are passed over (JpxDecoder.Choose), as is scRGB grey (26), whose encoding it does not register. Light below 0 and colours
    /// beyond sRGB go into the wide working space like those of the extended characteristics; light above 1 (above the reference white of
    /// the scene-referred encoding) is clipped per channel there, as with the extended characteristics (decided on 23.11.2026; before passed
    /// over, No. 1071, No. 1079).
    /// </para>
    /// <para>
    /// YCCK (13): Y, Cb, Cr as YCbCr(2) back to R′, G′, B′, these inverted to C, M, Y, K as it is (Table M.25) — from the full codes, without the
    /// 8-bit fixed point step of before (decided on 23.11.2026: accuracy everywhere; No. 1072).
    /// </para>
    /// </summary>
    public static JpxParameterized? Enumerated(uint enumCs) => enumCs switch
    {
        18 => new JpxParameterized(1, 13, 5, true, extended: true, direct: true, fromZero: true),
        3 => new JpxParameterized(1, 13, 5, true, extended: false, direct: true, fromZero: true),
        1 or 4 => new JpxParameterized(1, 13, 5, false, extended: false, direct: true, fromZero: true),
        24 => new JpxParameterized(1, 13, 5, true, extended: true, chroma: 2, direct: true, fromZero: true),
        20 => new JpxParameterized(1, 13, 0, true, extended: true, esrgb: true, direct: true, fromZero: true),
        22 => new JpxParameterized(1, 1, 1, false, fromZero: true),
        23 => new JpxParameterized(1, 1, 5, false, fromZero: true),
        25 => new JpxParameterized(1, 8, 0, true, extended: true, scrgb: true),
        13 => new JpxParameterized(1, 13, 5, true, extended: false, direct: true, ycck: true),
        _ => null,
    };

    /// <summary>
    /// The code a signed channel stands for (T.801 M.11.7.2: d = Dzero + (Dmax − Dzero)·x / MAX, MAX = 2^(n−1) − 1, Table M.24bis), in the
    /// codes of n bits the unsigned channels are read in — so Dmax is the top code 2^n − 1, as an unsigned channel's top value is. Dzero is the
    /// code of no light (<see cref="NoLight"/>). <paramref name="code"/>: the sample shifted by half its range (Store), with its
    /// <paramref name="bits"/>. Before luma and e-sRGB were read shifted by half the range, 0 as mid-grey (docs/FEHLERGEDAECHTNIS.md,
    /// No. 1066), and the signed components of the Parameterized method as offset binary codes (No. 1076).
    /// </summary>
    public double FromZero(int k, int channels, double code, int bits)
    {
        bits = Math.Clamp(bits, 1, 38);
        var half = Math.ScaleB(1.0, bits - 1);
        var zero = NoLight(k, channels, bits);
        return zero + (2 * half - 1 - zero) * (code - half) / Math.Max(1, half - 1);
    }

    /// <summary>
    /// Dzero of channel <paramref name="k"/> (T.801 M.11.7.2: the value of the channel for the colour of no scene radiance, or the achromatic
    /// level), as a code of <paramref name="bits"/> bits. Colour differences — Cb and Cr, Cg and Co, CT and CP, P and T — the achromatic
    /// 2^(n−1) (NOTE 2 of M.11.7.2 for YCbCr(2)). Luma, I, and R, G, B of the identity matrix (MatrixCoefficients 0): the code of the black
    /// signal, E′ of linear light 0 through the transfer characteristic — 0 for every characteristic but PQ, whose 7·10⁻⁷ lies far below a code
    /// step — quantized as H.273, equations 27–38 (full range E′·(2^n − 1), limited (219·E′ + 16)·2^(n−8): the black of BT.601 and BT.709,
    /// 16·2^(n−8)); for YCgCo-Re and -Ro in the codes of R, G, B, two and one bit shallower than Y (equations 23–26), as Y of a grey is its
    /// R = G = B. e-sRGB: 2^(n−2) + 2^(n−3) (R′ = 0, PIMA 7667, equation 4).
    /// </summary>
    private double NoLight(int k, int channels, int bits)
    {
        if (_esrgb) return Math.ScaleB(1.0, bits - 2) + Math.ScaleB(1.0, bits - 3);
        if (channels >= 3 && k > 0 && Matrix != 0) return Math.ScaleB(1.0, bits - 1);
        var black = Forward(0);
        var depth = Matrix switch { 16 => bits - 2, 17 => bits - 1, _ => bits };
        return FullRange ? black * (Math.ScaleB(1.0, depth) - 1) : (219 * black + 16) * Math.ScaleB(1.0, depth - 8);
    }

    /// <summary>Three components (luma and two colour differences, or three colours), or one (luma alone, drawn as grey).</summary>
    public static bool Fits(int channels) => channels is 1 or 3;

    /// <summary>
    /// The colour of the image: grey; RGB for the primaries of sRGB; otherwise an ICC profile of the primaries; with <paramref name="wide"/>
    /// the wide working space.
    /// </summary>
    public (JpxColor Color, byte[]? Icc) Space(int channels, bool wide = false)
    {
        if (channels == 1) return (JpxColor.Gray, null);
        if (wide) return (JpxColor.Icc, WideProfile());
        if (Primaries == 1) return (JpxColor.Rgb, null);
        var p = PrimaryTable[Primaries];
        return (JpxColor.Icc, CalibratedProfile.Primaries(p[0], p[1], p[2], p[3], $"ITU-T H.273 ColourPrimaries {Primaries}"));
    }

    /// <summary>The profile of the wide working space (see <see cref="WidePrimaries"/>).</summary>
    /// <returns>A new copy of the profile.</returns>
    public static byte[] WideProfile() => (byte[])WideBytes.Value.Clone();

    /// <summary>Whether a pixel of <paramref name="channels"/> channels can have colours beyond the primaries that the wide space keeps.</summary>
    public bool MayLeaveGamut(int channels) => channels == 3 && _toWide is not null;

    /// <summary>Whether the light of this pixel lies beyond the primaries by more than the 8-bit output rounds away.</summary>
    public bool Outside(ReadOnlySpan<double> codes, ReadOnlySpan<int> bits)
    {
        Span<double> rgb = stackalloc double[3];
        Linear(codes, bits, rgb);
        for (var i = 0; i < 3; i++)
            if (!(rgb[i] >= GamutLow && rgb[i] <= GamutHigh)) return true;
        return false;
    }

    /// <summary>
    /// Writes one pixel as 8 bits per channel: <paramref name="codes"/> of the components in their order (Y, Cb, Cr; for MatrixCoefficients 0
    /// G, B, R; e-sRGB R, G, B) with their <paramref name="bits"/>; one component: luma, its colour differences neutral, written as the luminance.
    /// <paramref name="wide"/>: in the wide working space. YCCK: four codes and four outputs, C, M, Y and K.
    /// </summary>
    public void Pixel(ReadOnlySpan<double> codes, ReadOnlySpan<int> bits, Span<byte> output, bool wide = false)
    {
        Span<double> rgb = stackalloc double[3];
        if (_ycck && codes.Length >= 4 && bits.Length >= 4 && output.Length >= 4)
        {
            // C, M, Y = 1 − R′, G′, B′, each rounded once (as 255 less the rounded R′, G′, B′, the way the 8-bit inversion went before); K from
            // its code
            Signal(codes[..3], bits[..3], rgb);
            for (var i = 0; i < 3; i++) output[i] = (byte)(255 - Direct(rgb[i]));
            output[3] = Direct(Finite(codes[3]) / (Math.ScaleB(1.0, Math.Clamp(bits[3], 1, 38)) - 1));
            return;
        }
        var signal = Signal(codes, bits, rgb);
        if (signal && _direct && !wide && output.Length == 3)
        {
            // R′G′B′ are the sRGB values: going to linear light and back with the sRGB curve would give the same step
            for (var i = 0; i < 3; i++) output[i] = Direct(rgb[i]);
            return;
        }
        ToLight(signal, rgb);
        if (output.Length == 1)
        {
            output[0] = Encode(_luminance[0] * rgb[0] + _luminance[1] * rgb[1] + _luminance[2] * rgb[2]);
            return;
        }
        if (wide && _toWide is not null)
        {
            Span<double> light = stackalloc double[3];
            ToWide(rgb, light);
            for (var i = 0; i < 3; i++) output[i] = Encode(light[i]);
            return;
        }
        for (var i = 0; i < 3; i++) output[i] = Encode(rgb[i]);
    }

    /// <summary>
    /// One pixel of three channels in the wide working space (<see cref="MayLeaveGamut"/>): at 8 bits as <see cref="Pixel"/> writes it, and
    /// the same light at 16 bits (<see cref="Encode16"/>), from which it is drawn (docs/FEHLERGEDAECHTNIS.md, No. 1092).
    /// </summary>
    public void WidePixel(ReadOnlySpan<double> codes, ReadOnlySpan<int> bits, Span<byte> output, Span<ushort> fine)
    {
        Span<double> rgb = stackalloc double[3];
        Span<double> light = stackalloc double[3];
        ToLight(Signal(codes, bits, rgb), rgb);
        ToWide(rgb, light);
        for (var i = 0; i < 3; i++)
        {
            output[i] = Encode(light[i]);
            fine[i] = Encode16(light[i]);
        }
    }

    /// <summary>
    /// The linear light of the image's own primaries in the wide working space. A colour beyond the wide primaries as well goes to the point
    /// where the line from the colour clipped in its own primaries (inside, as they lie inside BT.2020) to the colour itself leaves the wide
    /// space: as far out as the space holds it, without a step at its edge, and clipped in the own primaries again — as the display does — it is
    /// exactly that clipped colour. Clipped in the wide primaries instead, the colour changed its hue: T.803 file 3 came out with one pixel
    /// 7 steps from its reference, which clips in sRGB (Table G.1 allows 4); clipped in the own primaries outright, a gradient across the edge
    /// jumped (docs/FEHLERGEDAECHTNIS.md, No. 1092).
    /// </summary>
    private void ToWide(Span<double> rgb, Span<double> light)
    {
        var m = _toWide!;
        for (var i = 0; i < 3; i++) light[i] = m[i, 0] * rgb[0] + m[i, 1] * rgb[1] + m[i, 2] * rgb[2];
        if (light[0] is >= 0 and <= 1 && light[1] is >= 0 and <= 1 && light[2] is >= 0 and <= 1) return;
        Span<double> inside = stackalloc double[3];
        for (var i = 0; i < 3; i++)
        {
            var c = double.IsNaN(rgb[i]) ? 0 : Math.Clamp(rgb[i], 0, 1);
            inside[i] = c;
        }
        Span<double> start = stackalloc double[3];
        for (var i = 0; i < 3; i++) start[i] = Math.Clamp(m[i, 0] * inside[0] + m[i, 1] * inside[1] + m[i, 2] * inside[2], 0, 1);
        // the largest share of the way from start to the colour that stays inside 0 … 1 in every channel
        var t = 1.0;
        for (var i = 0; i < 3; i++)
        {
            var d = light[i] - start[i];
            if (double.IsNaN(light[i])) { t = 0; break; }
            if (light[i] > 1 && d > 0) t = Math.Min(t, (1 - start[i]) / d);
            else if (light[i] < 0 && d < 0) t = Math.Min(t, -start[i] / d);
        }
        t = Math.Clamp(t, 0, 1);
        for (var i = 0; i < 3; i++) light[i] = Math.Clamp(start[i] + t * (light[i] - start[i]), 0, 1);
    }

    /// <summary>The linear light of the three primaries of one pixel (not yet clipped); for PQ and HLG on the page (<see cref="ToPage"/>).</summary>
    internal void Linear(ReadOnlySpan<double> codes, ReadOnlySpan<int> bits, Span<double> rgb) => ToLight(Signal(codes, bits, rgb), rgb);

    /// <summary>E′ of R, G, B (<paramref name="signal"/>) through the inverse characteristic; PQ and HLG then onto the page.</summary>
    private void ToLight(bool signal, Span<double> rgb)
    {
        if (signal)
            for (var i = 0; i < 3; i++) rgb[i] = Inverse(rgb[i]);
        if (HighDynamicRange) ToPage(rgb);
    }

    /// <summary>
    /// The components of one pixel read back: true when <paramref name="rgb"/> holds E′ of R, G, B (still to be inverted), false when the
    /// matrix already led to linear light (constant luminance, ICtCp, IPT-C2).
    /// </summary>
    private bool Signal(ReadOnlySpan<double> codes, ReadOnlySpan<int> bits, Span<double> rgb)
    {
        var grey = codes.Length < 3;
        // whole codes, except those a signed channel stands for (FromZero); not a number cannot come from the planes,
        // and would be read as 0
        var c0 = Finite(codes[0]);
        int b0 = Math.Clamp(bits[0], 1, 38), b1 = grey ? b0 : Math.Clamp(bits[1], 1, 38), b2 = grey ? b0 : Math.Clamp(bits[2], 1, 38);
        // one component: the colour differences at their neutral value — for the coded RGB of 0, 8, 16 and 17 that makes all three the luma
        var c1 = grey ? (Matrix is 0 ? c0 : Math.ScaleB(1.0, b1 - 1)) : Finite(codes[1]);
        var c2 = grey ? (Matrix is 0 ? c0 : Math.ScaleB(1.0, b2 - 1)) : Finite(codes[2]);
        double r, g, b;
        if (_scrgb)
        {
            // scRGB: linear light of each channel from its 16-bit code (ICC Colour Encoding Registry: −0.5 at 0, 7.4999 at the top)
            rgb[0] = (c0 - ScRgbOffset) / ScRgbScale;
            rgb[1] = (c1 - ScRgbOffset) / ScRgbScale;
            rgb[2] = (c2 - ScRgbOffset) / ScRgbScale;
            return false;
        }
        if (_esrgb)
        {
            // PIMA 7667, equation 5, each channel with its own bits
            rgb[0] = ESrgbSignal(c0, b0);
            rgb[1] = ESrgbSignal(c1, b1);
            rgb[2] = ESrgbSignal(c2, b2);
            return true;
        }
        if (Matrix is 0 or 8 or 16 or 17)
        {
            // equations 23–26: the bit depth of the coded R, G, B
            var depth = Math.Max(1, Matrix switch { 16 => b0 - 2, 17 => b0 - 1, _ => b0 });
            var max = (1L << depth) - 1;
            long ri, gi, bi;
            // the coded RGB and YCgCo come as whole codes (the integer arithmetic of H.273, 5.3); a signed channel mapped from its value of no
            // light (FromZero) to the nearest one
            long l0 = Whole(c0), l1 = Whole(c1), l2 = Whole(c2);
            switch (Matrix)
            {
                case 0:
                    // equations 48–50: Y = G, Cb = B, Cr = R
                    (gi, bi, ri) = (l0, l1, l2);
                    break;
                case 8 when b1 == b0 && b2 == b0:
                {
                    // YCgCo, equations 54–57
                    long mid1 = 1L << (b1 - 1), mid2 = 1L << (b2 - 1), maxY = (1L << b0) - 1;
                    var t = l0 - (l1 - mid1);
                    gi = Math.Clamp(l0 + (l1 - mid1), 0, maxY);
                    bi = Math.Clamp(t - (l2 - mid2), 0, maxY);
                    ri = Math.Clamp(t + (l2 - mid2), 0, maxY);
                    break;
                }
                default:
                {
                    // YCgCo-R, -Re, -Ro, equations 62–65 (>> rounds towards minus infinity, as in H.273, 5.3)
                    long mid1 = 1L << (b1 - 1), mid2 = 1L << (b2 - 1);
                    var t = l0 - ((l1 - mid1) >> 1);
                    gi = Math.Clamp(t + (l1 - mid1), 0, max);
                    bi = Math.Clamp(t - ((l2 - mid2) >> 1), 0, max);
                    ri = Math.Clamp(bi + (l2 - mid2), 0, max);
                    break;
                }
            }
            rgb[0] = CodedRgb(ri, depth, max);
            rgb[1] = CodedRgb(gi, depth, max);
            rgb[2] = CodedRgb(bi, depth, max);
            return true;
        }

        // equations 30–32 (limited) and 36–38 (full) undone: E′Y, E′PB, E′PR
        double y, pb, pr;
        if (FullRange)
        {
            y = c0 / (Math.ScaleB(1.0, b0) - 1);
            pb = (c1 - Math.ScaleB(1.0, b1 - 1)) / (Math.ScaleB(1.0, b1) - 1);
            pr = (c2 - Math.ScaleB(1.0, b2 - 1)) / (Math.ScaleB(1.0, b2) - 1);
        }
        else
        {
            // H.273 drafts these for 8 bits and more; below, 2^(n−8) is taken as the real number it is
            y = (c0 / Math.ScaleB(1.0, b0 - 8) - 16) / 219;
            pb = (c1 / Math.ScaleB(1.0, b1 - 8) - 128) / 224;
            pr = (c2 / Math.ScaleB(1.0, b2 - 8) - 128) / 224;
        }
        // the nominal ranges (8.3, after equation 47): E′Y 0 … 1, E′PB and E′PR −0.5 … 0.5 unless TransferCharacteristics is 11 or 12 — also
        // for 13, whose wider range H.273 grants E′R, E′G, E′B only (after equation 13; before 23.11.2026 E′Y, E′PB, E′PR were left unclipped for
        // 13 as well, docs/FEHLERGEDAECHTNIS.md, No. 1056). E-sYCC keeps to the same limits in its own units: luma 0 … 1 (PIMA 7667, B5),
        // chroma −0.5 … 0.5 (B.1). H.273 states the range of the colour differences for equations 45–47 (and 68–71 keep to it); CT, CP of
        // ICtCp (79–84) and P, T of IPT-C2 (85–87) have no stated range and reach beyond it (ICtCp with an SDR characteristic: CT of blue
        // about 0.86), so there only the codes limit them
        if (Transfer is not (11 or 12))
        {
            y = Math.Clamp(y, 0, 1);
            if (Matrix is not (14 or 15))
            {
                pb = Math.Clamp(pb, -0.5, 0.5);
                pr = Math.Clamp(pr, -0.5, 0.5);
            }
        }
        // e-sYCC: the colour differences come at half their size (PIMA 7667, B3; the way back B9 with twice the coefficients of BT.601 —
        // its printed 0.0003 is the rounding of the inverse and stays below 0.04 of an 8-bit step)
        pb *= _chroma;
        pr *= _chroma;

        switch (Matrix)
        {
            case 10 or 13:
            {
                // constant luminance, equations 66–75 inverted: E′B and E′R from their difference to E′Y, then EG from EY, ER, EB
                var eb = y + 2 * (pb <= 0 ? _nb : _pb) * pb;
                var er = y + 2 * (pr <= 0 ? _nr : _pr) * pr;
                double ly = Inverse(y), lb = Inverse(eb), lr = Inverse(er);
                rgb[0] = lr;
                rgb[1] = (ly - _kr * lr - _kb * lb) / (1 - _kr - _kb);
                rgb[2] = lb;
                return false;
            }
            case 11:
                // Y′D′ZD′X, equations 76–78 inverted
                r = 2 * pr + 0.991902 * y;
                g = y;
                b = (2 * pb + y) / 0.986566;
                break;
            case 14 or 15:
            {
                // ICtCp and IPT-C2: I and the two colour differences → L′M′S′ (79–87 inverted) → LMS (the transfer characteristic inverted) →
                // RGB (14–19 inverted)
                var m = _iptToLms!;
                Span<double> lms = stackalloc double[3];
                for (var i = 0; i < 3; i++) lms[i] = Inverse(m[i, 0] * y + m[i, 1] * pb + m[i, 2] * pr);
                var n = _lmsToRgb!;
                for (var i = 0; i < 3; i++) rgb[i] = n[i, 0] * lms[0] + n[i, 1] * lms[1] + n[i, 2] * lms[2];
                return false;
            }
            default:
                // equations 45–47 inverted
                r = y + 2 * (1 - _kr) * pr;
                b = y + 2 * (1 - _kb) * pb;
                g = (y - _kr * r - _kb * b) / (1 - _kr - _kb);
                break;
        }
        rgb[0] = r;
        rgb[1] = g;
        rgb[2] = b;
        return true;
    }

    /// <summary>A coded R, G or B (equations 27–29 limited, 33–35 full, undone) as E′.</summary>
    private double CodedRgb(long value, int depth, long max) =>
        FullRange ? value / (double)max : (value / Math.ScaleB(1.0, depth - 8) - 16) / 219;

    /// <summary>A code as the nearest whole code (halves away from zero, as INT of ITU-R BT.709 rounds them), within the range of a long.</summary>
    private static long Whole(double v) => (long)Math.Round(Math.Clamp(v, -(double)(1L << 52), 1L << 52), MidpointRounding.AwayFromZero);

    /// <summary>A code as a finite number (not a number and ±∞ as 0).</summary>
    private static double Finite(double v) => double.IsFinite(v) ? v : 0;

    /// <summary>An e-sRGB code of n bits as R′ (PIMA 7667, equations 4 and 5): offset 2^(n−2) + 2^(n−3), 255·2^(n−9) to the unit.</summary>
    private static double ESrgbSignal(double value, int bits) =>
        (value - Math.ScaleB(1.0, bits - 2) - Math.ScaleB(1.0, bits - 3)) / (255 * Math.ScaleB(1.0, bits - 9));

    /// <summary>
    /// PQ and HLG onto the page (docs/FEHLERGEDAECHTNIS.md, No. 1055). First the light the picture shows: PQ as displayed light, 10 000 cd/m²
    /// at Lo = 1; HLG through its OOTF at the nominal peak of 1 000 cd/m² (ITU-R BT.2100, Table 5, as ITU-R BT.2390, 6.2 states it:
    /// F_D = L_W·Y_S^(γ−1)·E, Y_S the luminance of the scene light with the luminance of the primaries, γ = 1.2, black 0 — BT.2408, 6.2).
    /// Then the EETF of ITU-R BT.2408, Annex 5 on each of R, G, B (option 4, R′G′B′: it gives no colours outside the target volume, Table A5-1,
    /// and treats grey and every matrix alike), the mastering black and the page black 0 (b = 0, no lift): PQ from its full range to the
    /// common peak of 1 000 cd/m² (6.4, method 2), then — PQ and HLG alike — from 1 000 cd/m² to the reference white of 203 cd/m², which
    /// is the peak of the page (target maximum Lmax). The knee KS = 1.5·maxLum − 0.5 lies at 88 cd/m² (43 % of the page white): all light
    /// below it stays exactly as it is; above, the Hermite spline rolls off to 1 000 cd/m² at page white, the reference white itself coming to
    /// 78 % of it (90 % in the sRGB curve) — as BT.2408, 7.1.3 and 7.6.4 describe for down-mapping, the reference white a little below the
    /// SDR peak so that the highlights keep some detail. Light above the source peak, and negative light, are clipped first.
    /// </summary>
    private void ToPage(Span<double> rgb)
    {
        if (Transfer == 18)
        {
            double r = Math.Max(0, rgb[0]), g = Math.Max(0, rgb[1]), b = Math.Max(0, rgb[2]);
            var luminance = _luminance[0] * r + _luminance[1] * g + _luminance[2] * b;
            var gain = luminance > 0 ? ReferencePeak * Math.Pow(luminance, HlgGamma - 1) : 0;
            rgb[0] = Tone(gain * r, false);
            rgb[1] = Tone(gain * g, false);
            rgb[2] = Tone(gain * b, false);
            return;
        }
        for (var i = 0; i < 3; i++) rgb[i] = Tone(PqPeak * Math.Max(0, rgb[i]), true);
    }

    /// <summary>
    /// Displayed light in cd/m² → light on the page (1 = page white = the reference white of 203 cd/m²), by the EETF of ITU-R BT.2408, Annex 5:
    /// for PQ (<paramref name="fromPq"/>) first from 10 000 to 1 000 cd/m², then from 1 000 to 203 cd/m².
    /// </summary>
    internal static double Tone(double luminance, bool fromPq)
    {
        if (!(luminance > 0)) return 0;   // also NaN
        var e = PqSignal(Math.Min(luminance, PqPeak));
        if (fromPq) e = Eetf(e, PqFull, PqReferencePeak);
        e = Eetf(e, PqReferencePeak, PqReferenceWhite);
        return PqLight(e) / ReferenceWhite;
    }

    /// <summary>
    /// The EETF of ITU-R BT.2408, Annex 5, in PQ values: the source range black … <paramref name="sourcePeak"/> normalized (step 1, LB = 0),
    /// the target maximum <paramref name="targetPeak"/> as maxLum, the minimum 0 (minLum = b = 0, so step 3 adds nothing); 1:1 below
    /// KS = 1.5·maxLum − 0.5 (step 2), above it the Hermite spline P (step 4) from KS to maxLum; normalized back (step 5).
    /// </summary>
    private static double Eetf(double e, double sourcePeak, double targetPeak)
    {
        var range = sourcePeak - PqBlack;
        var e1 = Math.Clamp((e - PqBlack) / range, 0, 1);
        var maxLum = (targetPeak - PqBlack) / range;
        var ks = 1.5 * maxLum - 0.5;
        var e2 = e1;
        if (e1 >= ks)
        {
            var t = (e1 - ks) / (1 - ks);
            double t2 = t * t, t3 = t2 * t;
            e2 = (2 * t3 - 3 * t2 + 1) * ks + (t3 - 2 * t2 + t) * (1 - ks) + (-2 * t3 + 3 * t2) * maxLum;
        }
        return e2 * range + PqBlack;
    }

    /// <summary>The PQ value of a luminance in cd/m² (the inverse EOTF; H.273, Table 3, value 16, with Lo = L / 10 000).</summary>
    private static double PqSignal(double luminance)
    {
        var p = Math.Pow(Math.Max(0, luminance) / PqPeak, PqN);
        return Math.Pow((PqC1 + PqC2 * p) / (1 + PqC3 * p), PqM);
    }

    /// <summary>The luminance in cd/m² of a PQ value (the EOTF).</summary>
    private static double PqLight(double v) => PqPeak * PqInverse(v);

    /// <summary>PQ: V → Lo (H.273, Table 3, value 16, inverted); V clipped to 0 … 1. The denominator c2 − c3·p stays above c2 − c3 > 0.</summary>
    private static double PqInverse(double v)
    {
        var p = Math.Pow(Math.Clamp(v, 0, 1), 1 / PqM);
        return Math.Pow(Math.Max(p - PqC1, 0) / (PqC2 - PqC3 * p), 1 / PqN);
    }

    /// <summary>Linear light 0 … 1 → 8 bits with the sRGB tone curve (IEC 61966-2-1), rounded in the encoded value; beyond 0 … 1 clipped.</summary>
    internal static byte Encode(double linear)
    {
        if (!(linear > OutputSteps[0])) return 0;   // also NaN
        int low = 0, high = OutputSteps.Length;   // the number of steps at or below the value
        while (low < high)
        {
            var mid = (low + high) >> 1;
            if (OutputSteps[mid] <= linear) low = mid + 1;
            else high = mid;
        }
        return (byte)low;
    }

    /// <summary>An sRGB value 0 … 1 → 8 bits, rounded as <see cref="Encode"/> rounds (half up); beyond 0 … 1 clipped, NaN 0.</summary>
    private static byte Direct(double value) =>
        !(value > 0) ? (byte)0 : value >= 1 ? (byte)255 : (byte)Math.Min(255, Math.Floor(value * 255 + 0.5));

    /// <summary>Linear light 0 … 1 → the sRGB curve at 16 bits, rounded half up; beyond 0 … 1 clipped, NaN 0.</summary>
    internal static ushort Encode16(double linear)
    {
        if (!(linear > 0)) return 0;
        if (linear >= 1) return 65535;
        var v = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (ushort)Math.Min(65535, Math.Floor(v * 65535 + 0.5));
    }

    private static double SrgbDecode(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    /// <summary>The white of x, y as XYZ with Y = 1.</summary>
    private static double[] White((double X, double Y) w) => [w.X / w.Y, 1, (1 - w.X - w.Y) / w.Y];

    /// <summary>
    /// The transfer characteristic of Table 3: linear light L → V. Used for the constants of constant luminance (equations 72–75); the image
    /// itself goes the other way (<see cref="Inverse"/>).
    /// </summary>
    internal double Forward(double l)
    {
        double Power((double Alpha, double Beta) k, double slope, double exponent, double x) =>
            x >= k.Beta ? k.Alpha * Math.Pow(x, exponent) - (k.Alpha - 1) : slope * x;
        return Transfer switch
        {
            1 or 6 or 14 or 15 => Power(Bt709, 4.5, 0.45, Math.Max(0, l)),
            4 => Math.Pow(Math.Max(0, l), 1 / 2.2),
            5 => Math.Pow(Math.Max(0, l), 1 / 2.8),
            7 => Power(Smpte240, 4.0, 0.45, Math.Max(0, l)),
            8 => l,
            9 => l >= 0.01 ? 1 + Math.Log10(l) / 2 : 0,
            10 => l >= Math.Sqrt(10) / 1000 ? 1 + Math.Log10(l) / 2.5 : 0,
            11 => Math.Sign(l) * Power(Bt709, 4.5, 0.45, Math.Abs(l)),
            12 => l >= -Bt709.Beta / 4 ? Power(Bt709, 4.5, 0.45, l) : -(Power(Bt709, 4.5, 0.45, -4 * l)) / 4,
            13 when !_extended => Power(Srgb, 12.92, 1 / 2.4, Math.Max(0, l)),
            13 => Math.Sign(l) * Power(Srgb, 12.92, 1 / 2.4, Math.Abs(l)),
            16 => PqSignal(l * PqPeak),
            17 => Math.Pow(48 * Math.Max(0, l) / 52.37, 1 / 2.6),
            18 => l > 1.0 / 12 ? HlgA * Math.Log(12 * l - HlgB) + HlgC : Math.Sqrt(3 * Math.Max(0, l)),
            _ => l,
        };
    }

    /// <summary>
    /// The transfer characteristic of Table 3 inverted: V → linear light, as the table states it — for 1, 6, 14 and 15 the inverse of the
    /// reference opto-electronic function, not a display function (8.2, NOTE 1); for 16 (PQ) the displayed light Lo, for 18 (HLG) the scene
    /// light Lc. Where several L give the same V (9 and 10: V = 0 for all light below the range) the smallest, 0. V outside the domain of the
    /// function is first clipped to it, so that neither a power of a negative number nor a logarithm of one arises.
    /// </summary>
    internal double Inverse(double v)
    {
        if (!double.IsFinite(v)) return 0;
        double Power((double Alpha, double Beta) k, double slope, double exponent, double x) =>
            x >= slope * k.Beta ? Math.Pow((x + k.Alpha - 1) / k.Alpha, 1 / exponent) : x / slope;
        switch (Transfer)
        {
            case 1 or 6 or 14 or 15: return Power(Bt709, 4.5, 0.45, Math.Clamp(v, 0, 1));
            case 4: return Math.Pow(Math.Clamp(v, 0, 1), 2.2);
            case 5: return Math.Pow(Math.Clamp(v, 0, 1), 2.8);
            case 7: return Power(Smpte240, 4.0, 0.45, Math.Clamp(v, 0, 1));
            case 8: return Math.Clamp(v, 0, 1);
            case 9: return v > 0 ? Math.Pow(10, 2 * (Math.Min(v, 1) - 1)) : 0;
            case 10: return v > 0 ? Math.Pow(10, 2.5 * (Math.Min(v, 1) - 1)) : 0;
            case 11:
            {
                // odd around 0, for all light (IEC 61966-2-4); far beyond the range clipped, so that the power stays finite
                var x = Math.Clamp(v, -4, 4);
                return Math.Sign(x) * Power(Bt709, 4.5, 0.45, Math.Abs(x));
            }
            case 12:
            {
                // −0.25 ≤ L < 1.33; below −β/4 the negative branch at a quarter of the scale (γ = β/4, where the segments meet)
                var x = Math.Clamp(v, _extendedLow, _extendedHigh);
                return x >= -4.5 * Bt709.Beta / 4 ? Power(Bt709, 4.5, 0.45, x) : -Power(Bt709, 4.5, 0.45, -4 * x) / 4;
            }
            case 13 when !_extended: return Power(Srgb, 12.92, 1 / 2.4, Math.Clamp(v, 0, 1));
            case 13:
            {
                // odd around 0 (IEC 61966-2-1, Annex G; PIMA 7667, equations 6a–6c), the power continued above 1
                var x = Math.Clamp(v, -4, 4);
                return Math.Sign(x) * Power(Srgb, 12.92, 1 / 2.4, Math.Abs(x));
            }
            case 16: return PqInverse(v);
            case 17: return 52.37 / 48 * Math.Pow(Math.Clamp(v, 0, 1), 2.6);
            case 18:
            {
                // V = √3·Lc^0.5 up to Lc = 1/12, where V = 0.5; above V = a·ln(12·Lc − b) + c
                var x = Math.Clamp(v, 0, 1);
                return x <= 0.5 ? x * x / 3 : (Math.Exp((x - HlgC) / HlgA) + HlgB) / 12;
            }
            default: return Math.Clamp(v, 0, 1);
        }
    }

    /// <summary>
    /// α and β of a power curve α·L^p − (α − 1) above β meeting the line s·L below it with the same value and slope (8.2): from the slope
    /// α = s·β^(1−p) / p, then β where the values meet, by bisection — the curve lies above the line for β too small, below for β too large.
    /// </summary>
    private static (double Alpha, double Beta) Continuous(double slope, double exponent)
    {
        double Alpha(double beta) => slope * Math.Pow(beta, 1 - exponent) / exponent;
        double Gap(double beta) => Alpha(beta) * Math.Pow(beta, exponent) - (Alpha(beta) - 1) - slope * beta;
        double low = 1e-6, high = 0.2;
        for (var i = 0; i < 200; i++)
        {
            var mid = (low + high) / 2;
            if (Gap(mid) > 0) low = mid;
            else high = mid;
        }
        var b = (low + high) / 2;
        return (Alpha(b), b);
    }

    /// <summary>A matrix of H.273 given in 4096ths, as real numbers.</summary>
    private static double[,] Scaled(double[,] m)
    {
        var a = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++) a[i, j] = m[i, j] / 4096;
        return a;
    }
}
