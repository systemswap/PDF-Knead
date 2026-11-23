using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Graphics.Icc;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// ITU-T T.801 M.11.7.2 and M.11.7.3.4 (Table M.28bis): the Parameterized colour specification (METH 5) with the codes of ITU-T H.273 —
/// ColourPrimaries (Table 2), TransferCharacteristics (Table 3), MatrixCoefficients and VideoFullRangeFlag (Table 4, 8.3). Read since
/// 23.11.2026 (docs/FEHLERGEDAECHTNIS.md, No. 1044); before such a specification was never chosen. The components of every case were written
/// forward by the equations of H.273 from chosen linear colours, and the expected output computed apart from the decoder: the light read back,
/// written with the sRGB tone curve as 8 bits (for the profile cases also the colour drawn through the primaries: XYZ, Bradford to D50, sRGB).
/// </summary>
public sealed partial class JpxColourTests
{
    /// <summary>One case: the codes, the bit depth and the components per pixel (4 × 2), the expected samples and, with a profile, the drawn sRGB.</summary>
    private sealed record H273Case(int Primaries, int Transfer, int Matrix, bool FullRange, int[] Bits, int[][] Components, int[] Expected,
        int[]? Drawn = null);

    /// <summary>
    /// The cases. Colours: red, green, blue, white, black, three mixed ones; for the extended characteristics two of them beyond 0 … 1; for the
    /// profiles colours inside sRGB; for CIE XYZ (primaries 10) values of X, Y, Z. Keys: the code group they test.
    /// </summary>
    private static readonly Dictionary<string, H273Case> H273Cases = new()
    {
        ["tc1"] = new(1, 1, 1, false, [10, 10, 10],
            [[250, 691, 127, 940, 64, 565, 410, 422], [409, 167, 960, 512, 512, 376, 625, 512], [960, 105, 471, 512, 512, 627, 393, 512]],
            [255, 0, 0, 0, 255, 1, 0, 0, 255, 255, 255, 255, 0, 0, 0, 203, 149, 89, 63, 124, 170, 118, 118, 118]),
        ["tc4"] = new(1, 4, 1, false, [10, 10, 10],
            [[250, 691, 127, 940, 64, 596, 455, 466], [409, 167, 960, 512, 512, 388, 615, 512], [960, 105, 471, 512, 512, 617, 404, 512]],
            [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 203, 149, 89, 63, 124, 170, 118, 118, 118]),
        ["tc5"] = new(1, 5, 1, false, [10, 10, 10],
            [[250, 691, 127, 940, 64, 655, 526, 539], [409, 167, 960, 512, 512, 399, 605, 512], [960, 105, 471, 512, 512, 603, 407, 512]],
            [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 204, 149, 89, 63, 124, 169, 118, 118, 118]),
        ["tc6"] = new(1, 6, 1, false, [10, 10, 10],
            [[250, 691, 127, 940, 64, 565, 410, 422], [409, 167, 960, 512, 512, 376, 625, 512], [960, 105, 471, 512, 512, 627, 393, 512]],
            [255, 0, 0, 0, 255, 1, 0, 0, 255, 255, 255, 255, 0, 0, 0, 203, 149, 89, 63, 124, 170, 118, 118, 118]),
        ["tc7"] = new(1, 7, 1, false, [10, 10, 10],
            [[250, 691, 127, 940, 64, 561, 404, 416], [409, 167, 960, 512, 512, 375, 626, 512], [960, 105, 471, 512, 512, 628, 392, 512]],
            [255, 0, 0, 0, 255, 1, 0, 0, 255, 255, 255, 255, 0, 0, 0, 203, 149, 89, 63, 124, 170, 118, 118, 118]),
        ["tc8"] = new(1, 8, 1, false, [10, 10, 10],
            [[250, 691, 127, 940, 64, 370, 224, 222], [409, 167, 960, 512, 512, 392, 617, 512], [960, 105, 471, 512, 512, 655, 437, 512]],
            [255, 0, 0, 1, 255, 4, 0, 0, 255, 255, 255, 255, 0, 0, 0, 204, 149, 89, 64, 124, 170, 118, 118, 118]),
        ["tc9"] = new(1, 9, 1, false, [10, 10, 10],
            [[250, 691, 127, 940, 64, 724, 587, 614], [409, 167, 960, 512, 512, 390, 610, 512], [960, 105, 471, 512, 512, 589, 371, 512]],
            [255, 0, 0, 25, 255, 26, 0, 0, 255, 255, 255, 255, 0, 0, 0, 203, 149, 89, 63, 123, 169, 118, 118, 118]),
        ["tc10"] = new(1, 10, 1, false, [10, 10, 10],
            [[250, 691, 127, 940, 64, 767, 658, 679], [409, 167, 960, 512, 512, 414, 591, 512], [960, 105, 471, 512, 512, 574, 399, 512]],
            [255, 0, 0, 10, 255, 10, 0, 0, 255, 255, 255, 255, 0, 0, 0, 204, 149, 89, 63, 124, 170, 118, 118, 118]),
        ["tc14"] = new(1, 14, 1, false, [10, 10, 10],
            [[250, 691, 127, 940, 64, 565, 410, 422], [409, 167, 960, 512, 512, 376, 625, 512], [960, 105, 471, 512, 512, 627, 393, 512]],
            [255, 0, 0, 0, 255, 1, 0, 0, 255, 255, 255, 255, 0, 0, 0, 203, 149, 89, 63, 124, 170, 118, 118, 118]),
        ["tc15"] = new(1, 15, 1, false, [10, 10, 10],
            [[250, 691, 127, 940, 64, 565, 410, 422], [409, 167, 960, 512, 512, 376, 625, 512], [960, 105, 471, 512, 512, 627, 393, 512]],
            [255, 0, 0, 0, 255, 1, 0, 0, 255, 255, 255, 255, 0, 0, 0, 203, 149, 89, 63, 124, 170, 118, 118, 118]),
        ["tc11"] = new(1, 11, 1, false, [10, 10, 10],
            [[250, 691, 565, 940, 64, 496, 851, 422], [409, 167, 376, 512, 512, 614, 0, 512], [960, 105, 627, 512, 512, 66, 624, 512]],
            [255, 0, 0, 0, 255, 1, 203, 149, 89, 255, 255, 255, 0, 0, 0, 0, 187, 187, 255, 243, 0, 118, 118, 118]),
        ["tc12"] = new(1, 12, 1, false, [10, 10, 10],
            [[250, 691, 565, 940, 64, 521, 856, 422], [409, 167, 376, 512, 512, 601, 23, 512], [960, 105, 627, 512, 512, 126, 620, 512]],
            [255, 0, 0, 0, 255, 1, 203, 149, 89, 255, 255, 255, 0, 0, 0, 0, 187, 188, 255, 244, 0, 118, 118, 118]),
        ["tc13sycc"] = new(1, 13, 5, true, [12, 12, 12],
            [
                [1224, 2404, 2543, 4095, 0, 1683, 3506, 1889],
                [1357, 691, 1420, 2048, 2048, 2797, 0, 2048],
                [4095, 333, 2564, 2048, 2048, 0, 2712, 2048],
            ],
            [255, 0, 0, 0, 255, 0, 203, 149, 89, 255, 255, 255, 0, 0, 0, 0, 180, 187, 255, 233, 0, 118, 118, 118]),
        ["tc13srgb"] = new(1, 13, 0, true, [8, 8, 8],
            [[0, 255, 0, 255, 0, 149, 124, 118], [0, 0, 255, 255, 0, 89, 170, 118], [255, 0, 0, 255, 0, 203, 63, 118]],
            [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 203, 149, 89, 63, 124, 170, 118, 118, 118]),
        ["tc8full16"] = new(1, 8, 1, true, [16, 16, 16],
            [
                [13933, 46871, 4732, 65535, 0, 22894, 11963, 11796],
                [25260, 7509, 65535, 32768, 32768, 23962, 40448, 32768],
                [65535, 3005, 29763, 32768, 32768, 43199, 27252, 32768],
            ],
            [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 203, 149, 89, 63, 124, 170, 118, 118, 118]),
        ["tc17xyz"] = new(10, 17, 0, true, [12, 12, 12],
            [
                [3960, 2492, 2784, 3960, 0, 1251, 2492, 3452],
                [4093, 1633, 2492, 3960, 0, 2784, 2492, 2132],
                [3883, 2132, 3033, 3960, 0, 1633, 2492, 3634],
            ],
            [249, 255, 255, 124, 149, 89, 187, 170, 149, 255, 255, 255, 0, 0, 0, 89, 63, 170, 149, 149, 149, 231, 218, 124]),
        ["mc1"] = new(1, 1, 1, false, [10, 10, 10],
            [[250, 691, 127, 940, 64, 565, 410, 422], [409, 167, 960, 512, 512, 376, 625, 512], [960, 105, 471, 512, 512, 627, 393, 512]],
            [255, 0, 0, 0, 255, 1, 0, 0, 255, 255, 255, 255, 0, 0, 0, 203, 149, 89, 63, 124, 170, 118, 118, 118]),
        ["mc4"] = new(1, 1, 4, false, [10, 10, 10],
            [[327, 581, 160, 940, 64, 575, 398, 422], [361, 215, 960, 512, 512, 365, 637, 512], [960, 134, 442, 512, 512, 634, 387, 512]],
            [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 203, 149, 89, 63, 124, 170, 118, 118, 118]),
        ["mc5"] = new(1, 1, 5, false, [10, 10, 10],
            [[326, 578, 164, 940, 64, 574, 398, 422], [361, 215, 960, 512, 512, 365, 637, 512], [960, 137, 439, 512, 512, 635, 387, 512]],
            [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 204, 149, 89, 63, 123, 170, 118, 118, 118]),
        ["mc6"] = new(1, 1, 6, false, [10, 10, 10],
            [[326, 578, 164, 940, 64, 574, 398, 422], [361, 215, 960, 512, 512, 365, 637, 512], [960, 137, 439, 512, 512, 635, 387, 512]],
            [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 204, 149, 89, 63, 123, 170, 118, 118, 118]),
        ["mc7"] = new(1, 1, 7, false, [10, 10, 10],
            [[250, 678, 140, 940, 64, 562, 413, 422], [408, 168, 960, 512, 512, 376, 625, 512], [960, 113, 463, 512, 512, 629, 392, 512]],
            [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 203, 149, 89, 63, 124, 170, 118, 118, 118]),
        ["mc9"] = new(1, 1, 9, false, [10, 10, 10],
            [[294, 658, 116, 940, 64, 578, 397, 422], [387, 189, 960, 512, 512, 371, 630, 512], [960, 100, 476, 512, 512, 626, 394, 512]],
            [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 203, 149, 89, 63, 124, 169, 118, 118, 118]),
        ["mc12"] = new(11, 1, 12, false, [10, 10, 10],
            [[248, 696, 124, 940, 64, 565, 410, 422], [411, 165, 960, 512, 512, 377, 624, 512], [960, 103, 473, 512, 512, 627, 394, 512]],
            [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 204, 149, 89, 63, 123, 169, 118, 118, 118]),
        ["mc13"] = new(12, 1, 13, false, [10, 10, 10],
            [[473, 793, 285, 940, 64, 580, 424, 422], [294, 124, 960, 512, 512, 373, 642, 512], [960, 88, 383, 512, 512, 668, 398, 512]],
            [255, 0, 0, 0, 255, 1, 0, 0, 255, 255, 255, 255, 0, 0, 0, 204, 149, 89, 63, 124, 169, 118, 118, 118]),
        ["mc10"] = new(9, 14, 10, false, [10, 10, 10],
            [[505, 786, 247, 940, 64, 590, 414, 422], [280, 132, 960, 512, 512, 369, 642, 512], [960, 83, 403, 512, 512, 668, 401, 512]],
            [255, 1, 1, 1, 255, 1, 0, 0, 255, 255, 255, 255, 0, 0, 0, 203, 149, 89, 63, 124, 170, 118, 118, 118]),
        ["mc11"] = new(10, 17, 11, true, [12, 12, 12],
            [
                [3960, 2492, 2784, 3960, 0, 1251, 2492, 3452],
                [2087, 1608, 1885, 2021, 2048, 2796, 2031, 1374],
                [2025, 1878, 2184, 2064, 2048, 2244, 2058, 2153],
            ],
            [249, 255, 255, 124, 149, 89, 188, 170, 149, 255, 255, 255, 0, 0, 0, 89, 63, 170, 149, 149, 149, 231, 218, 124]),
        ["mc15"] = new(1, 1, 15, true, [12, 12, 12],
            [
                [2566, 2065, 1674, 4095, 0, 2479, 2145, 3029],
                [2363, 1563, 2048, 2048, 2048, 1882, 2875, 1975],
                [2544, 2100, 2048, 2048, 2048, 1479, 2595, 2656],
            ],
            [187, 170, 149, 124, 149, 137, 118, 118, 118, 255, 255, 255, 0, 0, 0, 149, 160, 188, 179, 137, 124, 203, 203, 170]),
        ["mc15wide"] = new(1, 1, 15, false, [12, 12, 12],
            [
                [1749, 2611, 1572, 3760, 256, 2178, 1793, 1689],
                [4095, 0, 1201, 2048, 2048, 2903, 1271, 2048],
                [4095, 4095, 0, 2048, 2048, 3310, 1068, 2048],
            ],
            [219, 96, 0, 129, 236, 60, 0, 83, 204, 255, 255, 255, 0, 0, 0, 203, 149, 89, 63, 124, 170, 118, 118, 118]),
        ["mc0limited"] = new(1, 1, 0, false, [10, 10, 10],
            [[64, 940, 64, 940, 64, 537, 444, 422], [64, 64, 940, 940, 64, 319, 615, 422], [940, 64, 64, 940, 64, 742, 227, 422]],
            [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 203, 149, 89, 63, 124, 170, 118, 118, 118]),
        ["mc8"] = new(1, 13, 8, true, [8, 8, 8],
            [[64, 128, 64, 255, 0, 148, 120, 118], [64, 255, 64, 128, 128, 129, 132, 128], [255, 128, 0, 128, 128, 185, 75, 128]],
            [255, 0, 1, 1, 255, 1, 0, 0, 255, 255, 255, 255, 0, 0, 0, 204, 149, 90, 63, 124, 169, 118, 118, 118]),
        ["mc8r"] = new(1, 13, 8, true, [8, 9, 9],
            [[63, 127, 63, 255, 0, 147, 120, 118], [129, 511, 129, 256, 256, 259, 264, 256], [511, 256, 1, 256, 256, 370, 149, 256]],
            [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 203, 149, 89, 63, 124, 170, 118, 118, 118]),
        ["mc16"] = new(1, 13, 16, true, [10, 10, 10],
            [[63, 127, 63, 255, 0, 147, 120, 118], [385, 767, 385, 512, 512, 515, 520, 512], [767, 512, 257, 512, 512, 626, 405, 512]],
            [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 203, 149, 89, 63, 124, 170, 118, 118, 118]),
        ["mc17"] = new(1, 13, 17, true, [9, 10, 10],
            [[63, 127, 63, 255, 0, 147, 120, 118], [385, 767, 385, 512, 512, 515, 520, 512], [767, 512, 257, 512, 512, 626, 405, 512]],
            [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 203, 149, 89, 63, 124, 170, 118, 118, 118]),
        ["bits20"] = new(1, 1, 1, false, [20, 20, 20],
            [
                [256243, 707088, 130301, 962560, 65536, 578569, 419886, 432283],
                [419168, 170656, 983040, 524288, 524288, 385259, 639755, 524288],
                [983040, 107601, 482223, 524288, 524288, 642161, 402640, 524288],
            ],
            [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 203, 149, 89, 63, 124, 170, 118, 118, 118]),
        ["bits4"] = new(1, 1, 1, true, [4, 4, 4],
            [[3, 11, 1, 15, 0, 9, 6, 6], [6, 2, 15, 8, 8, 6, 10, 8], [15, 1, 7, 8, 8, 10, 6, 8]],
            [240, 5, 0, 0, 255, 0, 0, 8, 240, 255, 255, 255, 0, 0, 0, 212, 154, 104, 64, 124, 174, 116, 116, 116]),
        ["grey709"] = new(1, 1, 1, false, [10],
            [[250, 691, 127, 940, 64, 565, 410, 422]],
            [70, 190, 34, 255, 0, 156, 114, 118]),
        ["greyxyz"] = new(10, 17, 11, true, [12],
            [[3960, 2492, 2784, 3960, 0, 1251, 2492, 3452]],
            [255, 149, 170, 255, 0, 63, 149, 218]),
        ["greycl"] = new(9, 14, 10, false, [10],
            [[505, 786, 247, 940, 64, 590, 414, 422]],
            [140, 215, 69, 255, 0, 163, 115, 118]),
        ["greyidentity"] = new(1, 13, 0, true, [8],
            [[0, 255, 0, 255, 0, 149, 124, 118]],
            [0, 255, 0, 255, 0, 149, 124, 118]),
        ["greyycgco"] = new(1, 13, 16, true, [10],
            [[63, 127, 63, 255, 0, 147, 120, 118]],
            [63, 127, 63, 255, 0, 147, 120, 118]),
        ["p9"] = new(9, 1, 9, false, [10, 10, 10],
            [[628, 510, 422, 940, 64, 573, 531, 735], [463, 503, 512, 512, 512, 571, 464, 447], [550, 466, 512, 512, 512, 487, 594, 517]],
            [188, 170, 149, 124, 149, 137, 118, 118, 118, 255, 255, 255, 0, 0, 0, 149, 160, 187, 179, 137, 123, 203, 204, 170],
            [200, 168, 146, 105, 152, 136, 118, 118, 118, 255, 255, 255, 0, 0, 0, 139, 161, 190, 201, 131, 120, 205, 204, 165]),
        ["p4"] = new(4, 6, 4, false, [10, 10, 10],
            [[626, 504, 422, 940, 64, 577, 535, 728], [461, 506, 512, 512, 512, 572, 460, 447], [553, 468, 512, 512, 512, 483, 596, 522]],
            [188, 169, 149, 124, 149, 137, 118, 118, 118, 255, 255, 255, 0, 0, 0, 149, 160, 187, 179, 137, 124, 203, 203, 170],
            [198, 167, 147, 111, 149, 137, 118, 118, 118, 255, 255, 255, 0, 0, 0, 140, 162, 189, 196, 135, 121, 205, 201, 167]),
        ["p8"] = new(8, 6, 6, false, [10, 10, 10],
            [[626, 504, 422, 940, 64, 577, 534, 728], [461, 506, 512, 512, 512, 572, 460, 447], [553, 468, 512, 512, 512, 483, 596, 523]],
            [188, 170, 149, 124, 149, 137, 118, 118, 118, 255, 255, 255, 0, 0, 0, 149, 160, 187, 179, 137, 124, 204, 203, 170],
            [194, 169, 147, 114, 150, 136, 118, 118, 118, 255, 255, 255, 0, 0, 0, 145, 160, 189, 191, 135, 122, 205, 204, 167]),
        ["p11"] = new(11, 13, 0, true, [10, 10, 10],
            [[680, 597, 472, 1023, 0, 641, 549, 816], [597, 549, 472, 1023, 0, 752, 496, 680], [752, 496, 472, 1023, 0, 597, 718, 816]],
            [187, 170, 149, 124, 149, 137, 118, 118, 118, 255, 255, 255, 0, 0, 0, 149, 160, 187, 179, 137, 124, 203, 203, 170],
            [190, 169, 146, 119, 150, 136, 118, 118, 118, 255, 255, 255, 0, 0, 0, 147, 160, 189, 185, 135, 122, 203, 203, 166]),
        ["p12"] = new(12, 13, 0, true, [10, 10, 10],
            [[680, 597, 472, 1023, 0, 641, 549, 816], [597, 549, 472, 1023, 0, 752, 496, 680], [752, 496, 472, 1023, 0, 597, 718, 816]],
            [187, 170, 149, 124, 149, 137, 118, 118, 118, 255, 255, 255, 0, 0, 0, 149, 160, 187, 179, 137, 124, 203, 203, 170],
            [191, 169, 146, 117, 150, 136, 118, 118, 118, 255, 255, 255, 0, 0, 0, 146, 160, 190, 187, 135, 121, 203, 203, 166]),
        ["p5"] = new(5, 6, 5, false, [10, 10, 10],
            [[626, 504, 422, 940, 64, 577, 534, 728], [461, 506, 512, 512, 512, 572, 460, 447], [553, 468, 512, 512, 512, 483, 596, 523]],
            [188, 170, 149, 124, 149, 137, 118, 118, 118, 255, 255, 255, 0, 0, 0, 149, 160, 187, 179, 137, 124, 204, 203, 170],
            [189, 170, 149, 123, 149, 137, 118, 118, 118, 255, 255, 255, 0, 0, 0, 148, 160, 187, 181, 137, 124, 204, 203, 170]),
        ["p6"] = new(6, 7, 7, false, [10, 10, 10],
            [[619, 509, 416, 940, 64, 574, 517, 729], [464, 500, 512, 512, 512, 571, 468, 446], [551, 466, 512, 512, 512, 485, 596, 519]],
            [188, 170, 149, 124, 149, 137, 118, 118, 118, 255, 255, 255, 0, 0, 0, 149, 160, 188, 179, 137, 124, 203, 204, 170],
            [187, 170, 149, 126, 148, 137, 118, 118, 118, 255, 255, 255, 0, 0, 0, 150, 160, 188, 177, 138, 124, 203, 203, 170]),
        // ColourPrimaries 7 has the same values as 6 (H.273, Table 2): the same samples, the same drawn colours
        ["p7"] = new(7, 7, 7, false, [10, 10, 10],
            [[619, 509, 416, 940, 64, 574, 517, 729], [464, 500, 512, 512, 512, 571, 468, 446], [551, 466, 512, 512, 512, 485, 596, 519]],
            [188, 170, 149, 124, 149, 137, 118, 118, 118, 255, 255, 255, 0, 0, 0, 149, 160, 188, 179, 137, 124, 203, 204, 170],
            [187, 170, 149, 126, 148, 137, 118, 118, 118, 255, 255, 255, 0, 0, 0, 150, 160, 188, 177, 138, 124, 203, 203, 170]),
        ["p22"] = new(22, 1, 1, false, [10, 10, 10],
            [[623, 514, 422, 940, 64, 576, 523, 733], [465, 500, 512, 512, 512, 570, 468, 447], [550, 466, 512, 512, 512, 487, 594, 518]],
            [187, 170, 149, 123, 149, 137, 118, 118, 118, 255, 255, 255, 0, 0, 0, 149, 160, 187, 179, 137, 123, 203, 203, 170],
            [187, 170, 149, 122, 148, 137, 118, 118, 118, 255, 255, 255, 0, 0, 0, 149, 161, 187, 180, 138, 123, 203, 202, 170]),
        ["p10"] = new(10, 17, 0, true, [12, 12, 12],
            [
                [2913, 2323, 2048, 3355, 0, 2492, 2644, 3452],
                [2644, 2492, 2048, 3033, 0, 3033, 2492, 3254],
                [3033, 2132, 2048, 3254, 0, 2492, 2784, 3452],
            ],
            [187, 179, 160, 124, 137, 149, 118, 118, 118, 203, 211, 187, 0, 0, 0, 149, 149, 187, 170, 160, 149, 218, 218, 203],
            [210, 170, 157, 76, 149, 150, 118, 118, 118, 196, 218, 182, 0, 0, 0, 125, 151, 192, 193, 149, 148, 225, 217, 201]),
    };

    /// <summary>A Colour Specification box of the Parameterized method: METH 5, PREC, APPROX, COLPRIMS, TRANSFC, MATCOEFFS, VIDFRNG with the
    /// seven reserved bits.</summary>
    private static byte[] Parameterized(int colPrims, int transfer, int matrix, bool fullRange, sbyte precedence = 0, byte approx = 0, int reserved = 0) =>
        Box("colr", [5, (byte)precedence, approx], U16(colPrims), U16(transfer), U16(matrix), [(byte)((fullRange ? 0x80 : 0) | reserved)]);

    /// <summary>
    /// A file of brand „jp2 “ that lists „jpx “ among its compatible brands (T.800 I.5.2; T.801 M.2.1): the Parameterized method of T.801 is
    /// allowed in it. Before these tests framed it as a pure JP2 file, where METH 5 is reserved and has been reported since
    /// docs/FEHLERGEDAECHTNIS.md, No. 1061 — the files counted as damaged.
    /// </summary>
    private static byte[] JpxFile(byte[] codestream, byte[][] header) => Jp2HeaderBoxes.File(codestream, header, "jp2 jpx ");

    private static byte[] H273Codestream(H273Case c) => JpxTests.AnnexG.Lossless(4, 2, [.. c.Bits.Select(b => (b, false))], c.Components);

    /// <summary>The case decoded with its own Parameterized specification and <paramref name="others"/> behind it.</summary>
    private static JpxImage DecodeH273(H273Case c, params byte[][] others) =>
        JpxDecoder.Decode(JpxFile(H273Codestream(c), [Parameterized(c.Primaries, c.Transfer, c.Matrix, c.FullRange), .. others]), long.MaxValue);

    private static byte[] Bytes(int[] values) => [.. values.Select(v => (byte)v)];

    /// <summary>The ten-bit components of the case "mc1" as they are, stretched to 8 bits: what is shown when the specification is passed over.</summary>
    private static byte[] Mc1Plain => [.. Enumerable.Range(0, 8).SelectMany(i => H273Cases["mc1"].Components.Select(c => (byte)(c[i] >> 2)))];

    /// <summary>
    /// H.273, Table 3: every transfer characteristic read is inverted as the table states it (BT.709 and the same curves 1, 6, 14, 15 as the
    /// inverse of the opto-electronic function; 4 and 5 as display gamma 2.2 and 2.8; 7 with α, β of continuous value and slope at the slope
    /// 4; 8 linear; 9 and 10 logarithmic; 13 with MatrixCoefficients 0 as sRGB; 17 for CIE XYZ). The light comes back to one step of the
    /// computed output, written with the sRGB curve; the primaries of sRGB give plain RGB. The cases of 11, 12 and 13 with sYCC beyond 0 … 1
    /// have colours outside sRGB and since 23.11.2026 come out in the wide working space (JpxColourTests.Farbe.cs,
    /// <c>Wide_ExtendedCharacteristics_ColoursBeyondSrgbKept</c>; docs/FEHLERGEDAECHTNIS.md, No. 1056).
    /// </summary>
    [Theory]
    [InlineData("tc1")]
    [InlineData("tc4")]
    [InlineData("tc5")]
    [InlineData("tc6")]
    [InlineData("tc7")]
    [InlineData("tc8")]
    [InlineData("tc8full16")]
    [InlineData("tc9")]
    [InlineData("tc10")]
    [InlineData("tc13srgb")]
    [InlineData("tc14")]
    [InlineData("tc15")]
    [InlineData("tc17xyz")]
    public void Parameterized_TransferCharacteristics_InvertedToLight(string key)
    {
        var c = H273Cases[key];
        var image = DecodeH273(c);
        Assert.False(image.Truncated);
        Assert.Equal(c.Primaries == 1 ? JpxColor.Rgb : JpxColor.Icc, image.Color);
        Assert.Equal(3, image.Channels);
        Near(Bytes(c.Expected), image.Samples);
    }

    /// <summary>
    /// The transfer characteristics themselves (H.273, Table 3), over their whole domain — also where the image cannot show it (light below 0
    /// or above 1 is clipped at the end): the function at chosen L against values computed apart, and its inverse back to L. α and β come from
    /// continuity of value and slope (8.2; for 1, 6, 14, 15 the 1.0992968… and 0.0180539… given there, for 7 and 13 the same rule). For 9 and
    /// 10 the V = 0 of all light below the range goes back to 0.
    /// </summary>
    [Theory]
        [InlineData(1, 1, new[] { 0.0, 0.005, 0.018, 0.02, 0.18, 0.5, 1.0 }, new[] { 0, 0.0225, 0.081, 0.0897539526917177, 0.408848108891223, 0.705435553055618, 1 })]
        [InlineData(4, 1, new[] { 0.0, 0.005, 0.18, 0.5, 1.0 }, new[] { 0, 0.0899657644784967, 0.458656446864381, 0.729740052840723, 1 })]
        [InlineData(5, 1, new[] { 0.0, 0.005, 0.18, 0.5, 1.0 }, new[] { 0, 0.150731344490633, 0.542033208007511, 0.78070918215571, 1 })]
        [InlineData(6, 1, new[] { 0.0, 0.01, 0.5, 1.0 }, new[] { 0, 0.045, 0.705435553055618, 1 })]
        [InlineData(7, 1, new[] { 0.0, 0.005, 0.0228, 0.03, 0.18, 1.0 }, new[] { 0, 0.02, 0.0912, 0.117853596144576, 0.402246973067109, 1 })]
        [InlineData(8, 1, new[] { 0.0, 0.18, 1.0 }, new[] { 0, 0.18, 1 })]
        [InlineData(9, 1, new[] { 0.01, 0.05, 0.18, 1.0 }, new[] { 0, 0.349485002168009, 0.627636252551653, 1 })]
        [InlineData(10, 1, new[] { 0.00317, 0.01, 0.18, 1.0 }, new[] { 0.00042370488710064, 0.2, 0.702109002041322, 1 })]
        [InlineData(11, 1, new[] { -1.5, -0.5, -0.01, 0.0, 0.01, 0.18, 1.0, 1.5 }, new[] { -1.22004108089709, -0.705435553055618, -0.045, 0, 0.045, 0.408848108891223, 1, 1.22004108089709 })]
        [InlineData(12, 1, new[] { -0.25, -0.1, -0.004, 0.0, 0.01, 0.18, 1.0, 1.3 }, new[] { -0.25, -0.157138328538507, -0.018, 0, 0.045, 0.408848108891223, 1, 1.13775952309332 })]
        [InlineData(13, 0, new[] { 0.0, 0.002, 0.18, 1.0 }, new[] { 0, 0.02584, 0.461350656803365, 1 })]
        [InlineData(13, 5, new[] { -1.0, -0.002, 0.0, 0.002, 0.5, 1.0, 1.5 }, new[] { -1, -0.02584, 0, 0.02584, 0.735354294242376, 1, 1.1941785075416 })]
        [InlineData(14, 1, new[] { 0.02, 0.5 }, new[] { 0.0897539526917177, 0.705435553055618 })]
        [InlineData(15, 1, new[] { 0.02, 0.5 }, new[] { 0.0897539526917177, 0.705435553055618 })]
        [InlineData(17, 0, new[] { 0.0, 0.01, 0.18, 1.0 }, new[] { 0, 0.164518549018473, 0.500048337717235, 0.967042675317934 })]
    public void Parameterized_TransferCharacteristic_FunctionAndInverse(int transfer, int matrix, double[] light, double[] values)
    {
        var parameterized = JpxParameterized.Read([0, 1, (byte)(transfer >> 8), (byte)transfer, (byte)(matrix >> 8), (byte)matrix, 0x80])!;
        Assert.NotNull(parameterized);
        for (var i = 0; i < light.Length; i++)
        {
            Assert.Equal(values[i], parameterized.Forward(light[i]), 1e-9);
            Assert.Equal(transfer is 9 or 10 && values[i] <= 0 ? 0 : light[i], parameterized.Inverse(values[i]), 1e-9);
        }
    }

    /// <summary>
    /// H.273, Table 4 with equations 45–47 (K_R and K_B of the table: 1, 4, 5, 6, 7, 9; derived from the primaries by equations 39–44: 12),
    /// constant luminance (66–75: 10, and 13 derived), Y′D′ZD′X (76–78: 11), IPT-C2 (85–87 with 17–19: 15 — its colour differences beyond
    /// ±0.5 kept as the codes give them), the identity in limited range (27–29: 0). Each inverted back to one step of the computed output.
    /// </summary>
    [Theory]
    [InlineData("mc1")]
    [InlineData("mc4")]
    [InlineData("mc5")]
    [InlineData("mc6")]
    [InlineData("mc7")]
    [InlineData("mc9")]
    [InlineData("mc12")]
    [InlineData("mc10")]
    [InlineData("mc13")]
    [InlineData("mc11")]
    [InlineData("mc15")]
    [InlineData("mc15wide")]
    [InlineData("mc0limited")]
    public void Parameterized_MatrixCoefficients_Inverted(string key)
    {
        var c = H273Cases[key];
        var image = DecodeH273(c);
        Assert.Equal(3, image.Channels);
        Near(Bytes(c.Expected), image.Samples);
    }

    /// <summary>
    /// MatrixCoefficients 0 (equations 48–50): the first component is G, the second B, the third R. With full range at 8 bits and the sRGB
    /// characteristic (13 with 0) the samples are the codes again, in the order R, G, B.
    /// </summary>
    [Fact]
    public void Parameterized_Identity_ComponentsInTheOrderGbr()
    {
        var c = H273Cases["tc13srgb"];
        var image = DecodeH273(c);
        Assert.Equal(JpxColor.Rgb, image.Color);
        var rgb = Enumerable.Range(0, 8).SelectMany(i => new[] { (byte)c.Components[2][i], (byte)c.Components[0][i], (byte)c.Components[1][i] }).ToArray();
        Near(rgb, image.Samples);
    }

    /// <summary>
    /// YCgCo (MatrixCoefficients 8 at equal depths, equations 54–57), YCgCo-R (8 with chroma one bit deeper, 62–65), YCgCo-Re and -Ro (16, 17:
    /// the coded R, G, B two and one bit shallower than Y, 23–26): integer transforms, read back to exactly the R, G, B they were made of —
    /// except where equation 53 gives 2^BitDepthC, which no component holds (H.273, 8.3, NOTE 3): red at 8 bits keeps Cr 255, and the inverse
    /// gives the blue 1 of that code.
    /// </summary>
    [Theory]
    [InlineData("mc8")]
    [InlineData("mc8r")]
    [InlineData("mc16")]
    [InlineData("mc17")]
    public void Parameterized_YCgCo_IntegerInverseExact(string key)
    {
        var c = H273Cases[key];
        Assert.Equal(Bytes(c.Expected), DecodeH273(c).Samples);
    }

    /// <summary>
    /// VideoFullRangeFlag, the top bit of the last METHDAT byte (Table M.28bis): the full-range codes of a case read as limited range give other
    /// colours; the seven reserved bits beside it change nothing.
    /// </summary>
    [Fact]
    public void Parameterized_VideoFullRangeFlag_TopBitOnly()
    {
        var c = H273Cases["mc15"];
        byte[] File(bool full, int reserved) =>
            JpxFile(H273Codestream(c), [Parameterized(c.Primaries, c.Transfer, c.Matrix, full, reserved: reserved)]);
        Near(Bytes(c.Expected), JpxDecoder.Decode(File(true, 0x7F), long.MaxValue).Samples);
        var limited = JpxDecoder.Decode(File(false, 0), long.MaxValue).Samples;
        Assert.True(Bytes(c.Expected).Zip(limited).Count(p => Math.Abs(p.First - p.Second) > 3) >= 8);
    }

    /// <summary>
    /// Bit depths beyond the 8 and more H.273 drafts its equations for: 20 bits (the decoder keeps 16 of them; the codes go back to their
    /// depth), and 4 bits in the full range (2^n − 1 to the unit). The limited range below 8 bits, with 2^(n−8) taken as a fraction:
    /// JpxColourTests.Farbe.cs, <c>Parameterized_LimitedRange_BelowEightBits</c>.
    /// </summary>
    [Theory]
    [InlineData("bits20")]
    [InlineData("bits4")]
    public void Parameterized_BitDepths_AboveSixteenAndBelowEight(string key)
    {
        var c = H273Cases[key];
        Near(Bytes(c.Expected), DecodeH273(c).Samples);
    }

    /// <summary>
    /// One component: luma alone, its colour differences neutral — drawn as grey with the luminance of the pixel (for Y′D′ZD′X and CIE XYZ the
    /// Y of the pixel, for constant luminance and YCgCo-Re the same rules as with three components).
    /// </summary>
    [Theory]
    [InlineData("grey709")]
    [InlineData("greyxyz")]
    [InlineData("greycl")]
    [InlineData("greyidentity")]
    [InlineData("greyycgco")]
    public void Parameterized_OneComponent_GreyOfTheLuminance(string key)
    {
        var c = H273Cases[key];
        var file = JpxFile(JpxTests.AnnexG.Lossless(4, 2, [(c.Bits[0], false)], c.Components),
            [Parameterized(c.Primaries, c.Transfer, c.Matrix, c.FullRange)]);
        var image = JpxDecoder.Decode(file, long.MaxValue);
        Assert.False(image.Truncated);
        Assert.Equal(JpxColor.Gray, image.Color);
        Assert.Equal(1, image.Channels);
        Assert.Equal(Bytes(c.Expected), image.Samples);
    }

    /// <summary>
    /// H.273, Table 2: primaries other than those of sRGB give an ICC profile of them — white D65, C, the white of value 11 and the centre white
    /// of CIE XYZ adapted to D50 with Bradford. The samples are the light of the primaries with the sRGB curve; drawn through the profile they
    /// give the sRGB colours computed apart (to one step).
    /// </summary>
    [Theory]
    [InlineData("p4")]
    [InlineData("p5")]
    [InlineData("p6")]
    [InlineData("p7")]
    [InlineData("p8")]
    [InlineData("p9")]
    [InlineData("p10")]
    [InlineData("p11")]
    [InlineData("p12")]
    [InlineData("p22")]
    public void Parameterized_Primaries_ProfileDrawsTheirColours(string key)
    {
        var c = H273Cases[key];
        var image = DecodeH273(c);
        Assert.Equal(JpxColor.Icc, image.Color);
        Near(Bytes(c.Expected), image.Samples);
        var transform = new IccTransform(IccProfile.Parse(image.IccProfile!)!);
        var drawn = new byte[image.Samples.Length];
        var rgb = new float[3];
        for (var i = 0; i < drawn.Length; i += 3)
        {
            transform.ToSrgb([image.Samples[i] / 255f, image.Samples[i + 1] / 255f, image.Samples[i + 2] / 255f], rgb);
            for (var k = 0; k < 3; k++) drawn[i + k] = (byte)Math.Round(rgb[k] * 255);
        }
        Near(Bytes(c.Drawn!), drawn);
    }

    /// <summary>
    /// Equations 39–44 derive K_R and K_B from the primaries: for those of BT.2020 (9) they are the 0.2627 and 0.0593 of MatrixCoefficients 9,
    /// so MatrixCoefficients 12 reads the same components to the same colours.
    /// </summary>
    [Fact]
    public void Parameterized_ChromaticityDerived_EqualsTheTableForBt2020()
    {
        var c = H273Cases["p9"];
        Assert.Equal(DecodeH273(c).Samples, DecodeH273(c with { Matrix = 12 }).Samples);
        Assert.NotEqual(DecodeH273(c).Samples, DecodeH273(c with { Matrix = 1 }).Samples);
    }

    /// <summary>
    /// Codes that are not applicable — unspecified (2), reserved (H.273, 3.6), beyond 0 … 255 (257 has a valid low byte: the 16 bits are read)
    /// — make the specification not applicable: the next one counts, here sRGB of lower precedence, which shows the codes as they are. PQ 16,
    /// HLG 18 and ICtCp 14 were among them until 23.11.2026 and are read since (docs/FEHLERGEDAECHTNIS.md, No. 1055).
    /// </summary>
    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(2, 1, 1)]
    [InlineData(3, 1, 1)]
    [InlineData(13, 1, 1)]
    [InlineData(21, 1, 1)]
    [InlineData(23, 1, 1)]
    [InlineData(255, 1, 1)]
    [InlineData(257, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 2, 1)]
    [InlineData(1, 3, 1)]
    [InlineData(1, 19, 1)]
    [InlineData(1, 255, 1)]
    [InlineData(1, 257, 1)]
    [InlineData(1, 1, 2)]
    [InlineData(1, 1, 3)]
    [InlineData(1, 1, 18)]
    [InlineData(1, 1, 257)]
    [InlineData(1, 1, 255)]
    [InlineData(1, 1, 65535)]
    public void Parameterized_CodeNotApplicable_NextSpecificationCounts(int colPrims, int transfer, int matrix)
    {
        var c = H273Cases["mc1"];
        var image = JpxDecoder.Decode(JpxFile(H273Codestream(c),
            [Parameterized(colPrims, transfer, matrix, false, precedence: 1), Enumerated(16)]), long.MaxValue);
        Assert.False(image.Truncated);
        Assert.Equal(JpxColor.Rgb, image.Color);
        Assert.Equal(Mc1Plain, image.Samples);
    }

    /// <summary>METHDAT of seven bytes at least (Table M.28bis): one byte less is not understood — the next specification counts; bytes beyond
    /// the seven do not hinder reading it.</summary>
    [Fact]
    public void Parameterized_MethdatLength_SevenBytesAtLeast()
    {
        var c = H273Cases["mc1"];
        var full = Parameterized(1, 1, 1, false, precedence: 1);
        byte[] shortBox = [0, 0, 0, (byte)(full.Length - 1), .. full[4..^1]];
        var image = JpxDecoder.Decode(JpxFile(H273Codestream(c), [shortBox, Enumerated(16)]), long.MaxValue);
        Assert.Equal(Mc1Plain, image.Samples);
        byte[] longBox = [0, 0, 0, (byte)(full.Length + 2), .. full[4..], 0xAB, 0xCD];
        Near(Bytes(c.Expected), JpxDecoder.Decode(JpxFile(H273Codestream(c), [longBox, Enumerated(16)]), long.MaxValue).Samples);
    }

    /// <summary>
    /// Precedence (T.801 M.11.7.2): the Parameterized specification against an enumerated one (sRGB) — the higher precedence counts either
    /// way, at equal precedence the better approximation (APPROX 1 before 2).
    /// </summary>
    [Fact]
    public void Parameterized_Precedence_AgainstEnumerated()
    {
        var c = H273Cases["mc1"];
        var cs = H273Codestream(c);
        JpxImage Decode(params byte[][] header) => JpxDecoder.Decode(JpxFile(cs, header), long.MaxValue);
        var expected = Bytes(c.Expected);
        Near(expected, Decode(Enumerated(16, 0, 1), Parameterized(1, 1, 1, false, 5, 1)).Samples);
        Assert.Equal(Mc1Plain, Decode(Parameterized(1, 1, 1, false, 0, 1), Enumerated(16, 5, 1)).Samples);
        Near(expected, Decode(Enumerated(16, 0, 2), Parameterized(1, 1, 1, false, 0, 1)).Samples);
        Assert.Equal(Mc1Plain, Decode(Parameterized(1, 1, 1, false, 0, 2), Enumerated(16, 0, 1)).Samples);
    }

    /// <summary>
    /// Four colour channels fit no Parameterized specification (luma alone, or luma and two colour differences): the next one counts, here
    /// CMYK.
    /// </summary>
    [Fact]
    public void Parameterized_FourChannels_NotFitting()
    {
        var cs = JpxTests.AnnexG.Lossless8(2, 1, [10, 20], [30, 40], [50, 60], [70, 80]);
        var image = JpxDecoder.Decode(JpxFile(cs, [Parameterized(1, 1, 1, true, 1), Enumerated(12)]), long.MaxValue);
        Assert.False(image.Truncated);
        Assert.Equal(JpxColor.Cmyk, image.Color);
        Assert.Equal(new byte[] { 10, 30, 50, 70, 20, 40, 60, 80 }, image.Samples);
    }

    /// <summary>
    /// Through a palette (pclr with three columns of 10 bits, cmap): the entries are the codes, with the depth the palette gives them — the
    /// same colours as the components themselves.
    /// </summary>
    [Fact]
    public void Parameterized_ThroughPalette_EntriesAtTheirDepth()
    {
        var c = H273Cases["mc1"];
        var entries = Enumerable.Range(0, 8).SelectMany(i => c.Components.SelectMany(k => U16(k[i]))).ToArray();
        byte[][] header =
        [
            Box("pclr", U16(8), [3, 9, 9, 9], entries),
            Box("cmap", [0, 0, 1, 0], [0, 0, 1, 1], [0, 0, 1, 2]),
            Parameterized(1, 1, 1, false),
        ];
        var image = JpxDecoder.Decode(JpxFile(JpxTests.AnnexG.Lossless8(4, 2, [0, 1, 2, 3, 4, 5, 6, 7]), header), long.MaxValue);
        Near(Bytes(c.Expected), image.Samples);
    }
}
