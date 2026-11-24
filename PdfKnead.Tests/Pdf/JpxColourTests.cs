using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The JP2/JPX wrapper as far as PDF needs it (ISO 32000-2, 7.4.9: the JPX baseline set of ISO/IEC 15444-2, M.9.2; Konformitätsabgleich M2,
/// docs/normabgleich/M2-filter.md): colour specifications by precedence, the enumerated colour spaces, the first compositing layer, opacity,
/// fragmented codestreams. The codestreams come from small files of the public test collection; the boxes around them are built here after
/// the standard, so every rule is checked on its own against what the codestream alone gives.
/// </summary>
public sealed partial class JpxColourTests
{
    private static string PdfJs(string name, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return Path.Combine(dir.FullName, "TestPDFs", "pdfjs", name);
        throw new DirectoryNotFoundException();
    }

    /// <summary>The bare codestream of the first JPX image of a file.</summary>
    internal static byte[] Codestream(string file)
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(PdfJs(file)));
        foreach (var page in doc.Pages)
            if (page.Resources?.GetDictionary(PdfName.Get("XObject")) is { } xobjects)
                foreach (var key in xobjects.Keys)
                    if (xobjects.Get(key) is PdfStream s && s.Dictionary.Get(PdfName.Filter) is PdfName { Value: "JPXDecode" })
                    {
                        var data = doc.DecodeStream(s).Data;
                        var boxes = JpxBoxes.Read(data);
                        return boxes.Data[boxes.Start..boxes.End];
                    }
        throw new InvalidDataException(file);
    }

    /// <summary>The stored data of stream object <paramref name="number"/> of a file.</summary>
    internal static byte[] StoredStream(string file, int number)
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(PdfJs(file)));
        return doc.GetRawStreamData((PdfStream)doc.GetObject(number)).ToArray();
    }

    /// <summary>40 × 27 pixels, three components of 8 bits.</summary>
    private static readonly Lazy<byte[]> Rgb = new(() => Codestream("jp2k-resetprob.pdf"));

    /// <summary>128 × 64 pixels, one component, all 0.</summary>
    private static readonly Lazy<byte[]> Gray = new(() => Codestream("bug_jpx.pdf"));

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static byte[] Box(string type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        return [.. U32((uint)(8 + body.Length)), .. Encoding.ASCII.GetBytes(type), .. body];
    }

    private static byte[] Enumerated(uint space, sbyte precedence = 0, byte approx = 0, byte[]? ep = null) =>
        Box("colr", [1, (byte)precedence, approx], U32(space), ep ?? []);

    private static byte[] Icc(byte[] profile, sbyte precedence = 0, byte approx = 0) => Box("colr", [2, (byte)precedence, approx], profile);

    /// <summary>A JP2/JPX file: signature, file type, JP2 Header box with <paramref name="header"/>, <paramref name="rest"/>, codestream.</summary>
    private static byte[] Jpx(byte[] codestream, byte[][] header, params byte[][] rest) =>
    [
        .. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]),
        .. Box("ftyp", Encoding.ASCII.GetBytes("jpx "), U32(0), Encoding.ASCII.GetBytes("jpx jp2 jpxb")),
        .. Box("jp2h", header),
        .. rest.SelectMany(r => r),
        .. Box("jp2c", codestream),
    ];

    /// <summary>The samples of the codestream alone: no colour specification, stretched to 8 bits (here: as they are).</summary>
    private static byte[] Plain(byte[] codestream) => JpxDecoder.Decode(codestream, long.MaxValue).Samples;

    private static byte[] YccToRgb(byte[] s, double chroma = 1)
    {
        var o = new byte[s.Length];
        for (var i = 0; i + 2 < s.Length; i += 3)
        {
            double y = s[i], cb = chroma * (s[i + 1] - 128), cr = chroma * (s[i + 2] - 128);
            o[i] = (byte)Math.Clamp(Math.Round(y + 1.402 * cr), 0, 255);
            o[i + 1] = (byte)Math.Clamp(Math.Round(y - 0.344136 * cb - 0.714136 * cr), 0, 255);
            o[i + 2] = (byte)Math.Clamp(Math.Round(y + 1.772 * cb), 0, 255);
        }
        return o;
    }

    private static void Near(byte[] expected, byte[] actual, int tolerance = 1)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            if (Math.Abs(expected[i] - actual[i]) > tolerance) Assert.Fail($"Wert {i}: erwartet {expected[i]}, gelesen {actual[i]}");
    }

    // ---- No. 734: which colour specification counts

    /// <summary>
    /// Several colour specifications (ISO 32000-2, 7.4.9; ISO/IEC 15444-2, M.11.7.2): the supported one of the highest precedence, among equal
    /// precedence the best approximation. Before only the first counted — here sYCC instead of sRGB, converted colours.
    /// </summary>
    [TestPdfsFact]
    public void Precedence_TheHighestSupportedCounts()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        var byPrecedence = JpxDecoder.Decode(Jpx(cs, [Enumerated(18, 0, 1), Enumerated(16, 5, 1)]), long.MaxValue);
        Assert.Equal(plain, byPrecedence.Samples);
        var byApproximation = JpxDecoder.Decode(Jpx(cs, [Enumerated(18, 0, 3), Enumerated(16, 0, 1)]), long.MaxValue);
        Assert.Equal(plain, byApproximation.Samples);
        // the same precedence and approximation (JP2: both 0): the first, here sYCC — converted (in the wide working space, where its colours
        // leave sRGB: docs/FEHLERGEDAECHTNIS.md, No. 1072)
        var first = JpxDecoder.Decode(Jpx(cs, [Enumerated(18), Enumerated(16)]), long.MaxValue);
        NearSycc(plain, first);
    }

    /// <summary>
    /// An ICC profile that cannot be used gives way to the next specification (ISO 32000-2, 7.4.9): a damaged one, and one for another number of
    /// channels. Before it was taken anyway and the image drawn by the number of channels — here as RGB instead of from sYCC.
    /// </summary>
    [TestPdfsFact]
    public void UnusableIccProfile_TheNextCounts()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        foreach (var profile in new[] { Enumerable.Repeat((byte)0x5A, 200).ToArray(), CalibratedProfile.Gray(null) })
        {
            var image = JpxDecoder.Decode(Jpx(cs, [Icc(profile, 1, 1), Enumerated(18, 0, 1)]), long.MaxValue);
            NearSycc(plain, image);
            Assert.NotEqual(profile, image.IccProfile);
        }
        // a usable profile counts
        var srgb = CalibratedProfile.Srgb();
        var icc = JpxDecoder.Decode(Jpx(cs, [Icc(srgb, 1, 1), Enumerated(18, 0, 1)]), long.MaxValue);
        Assert.Equal(JpxColor.Icc, icc.Color);
        Assert.Equal(srgb, icc.IccProfile);
    }

    /// <summary>
    /// The colour group of the first compositing layer replaces the specifications of the JP2 Header box (ISO/IEC 15444-2, M.11.7, M.9.2.2).
    /// Before the layer was not read.
    /// </summary>
    [TestPdfsFact]
    public void FirstCompositingLayer_ColourGroupCounts()
    {
        var cs = Rgb.Value;
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(18)], Box("jplh", Box("cgrp", Enumerated(16)))), long.MaxValue);
        Assert.Equal(Plain(cs), image.Samples);
        // only the first layer counts
        var second = JpxDecoder.Decode(Jpx(cs, [Enumerated(16)], Box("jplh", Box("lbl ", "erste"u8.ToArray())), Box("jplh", Box("cgrp", Enumerated(18)))),
            long.MaxValue);
        Assert.Equal(Plain(cs), second.Samples);
    }

    /// <summary>
    /// A /ColorSpace of the image that fits the channels determines them; the colour specifications of the data are ignored (ISO 32000-2,
    /// 7.4.9). Before an sYCC specification was converted to RGB anyway; without /ColorSpace it still is.
    /// </summary>
    [TestPdfsFact]
    public void DeclaredColorSpace_TheSpecificationsOfTheDataAreIgnored()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        var file = Jpx(cs, [Enumerated(18)]);
        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        byte[] Drawn(PdfObject? space)
        {
            var dict = TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(40)),
                ("Height", PdfInteger.Of(27)), ("BitsPerComponent", PdfInteger.Of(8)), ("Filter", TestPdf.N("JPXDecode")));
            if (space is not null) dict.Set(PdfName.Get("ColorSpace"), space);
            var decoded = PdfImageDecoder.Decode(new ImageSource(new PdfStream(dict, file), null, null, false, 0), doc);
            Assert.NotNull(decoded);
            return [.. decoded.Rgba.Where((_, i) => i % 4 != 3)];
        }
        Assert.Equal(plain, Drawn(TestPdf.N("DeviceRGB")));
        // without: converted — its colours leave sRGB, so it comes in the wide working space (No. 1072) and is drawn through its profile; the
        // pixels inside sRGB as before
        NearSyccDrawn(plain, Drawn(null));
    }

    // ---- No. 735: the enumerated colour spaces of JPX baseline

    /// <summary>
    /// e-sYCC (EnumCS 24, PIMA 7667, Annex B): the colour differences stored at half their size, the way back with twice the coefficients of
    /// sYCC. Before it was read as sYCC, its colours half as strong (docs/FEHLERGEDAECHTNIS.md, No. 944). The image has colours beyond sRGB,
    /// so since 23.11.2026 it comes out in the wide working space (No. 1056); drawn through its profile to sRGB, the pixels inside sRGB show
    /// what they showed before (to ΔE76 1.5; those beyond sRGB are not clipped in sRGB any more).
    /// </summary>
    [TestPdfsFact]
    public void ESycc_HalfTheChroma_TwiceTheCoefficients()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(24)]), long.MaxValue);
        Assert.Equal(JpxColor.Icc, image.Color);
        var drawn = DrawnSrgb(image);
        NearInsideSrgb(YccToRgb(plain, 2), drawn);
        var asSycc = YccToRgb(plain);
        Assert.Contains(Enumerable.Range(0, asSycc.Length), i => Math.Abs(asSycc[i] - drawn[i]) > 8);
    }

    /// <summary>
    /// The pixels of <paramref name="clipped"/> without a channel at 0 or 255 — inside sRGB — against <paramref name="drawn"/> as CIELab, to
    /// ΔE76 1.5: the 8-bit steps of the wide space, carried back through its matrix, move a small channel beside large ones by a few codes
    /// (red 21 beside green 173 came back as 15), which is a small difference in colour. A tenth of the pixels at least are such.
    /// </summary>
    private static void NearInsideSrgb(byte[] clipped, byte[] drawn)
    {
        var inside = 0;
        var xyz = new float[3];
        (float, float, float) Lab(byte[] s, int i)
        {
            IccColor.SrgbToXyz(s[i] / 255f, s[i + 1] / 255f, s[i + 2] / 255f, xyz);
            return IccColor.XyzToLab(xyz[0], xyz[1], xyz[2]);
        }
        for (var i = 0; i < clipped.Length; i += 3)
        {
            if (clipped.AsSpan(i, 3).IndexOfAny((byte)0, (byte)255) >= 0) continue;
            inside++;
            var (l1, a1, b1) = Lab(clipped, i);
            var (l2, a2, b2) = Lab(drawn, i);
            var delta = Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
            Assert.True(delta <= 1.5, $"Pixel {i / 3}: ΔE {delta:F2}");
        }
        Assert.True(inside * 10 >= clipped.Length / 3, $"{inside} Pixel innerhalb von sRGB");
    }

    /// <summary>The samples of an image with a profile drawn to sRGB (8 bits).</summary>
    private static byte[] DrawnSrgb(JpxImage image)
    {
        var transform = new IccTransform(IccProfile.Parse(image.IccProfile!)!);
        var drawn = new byte[image.Samples.Length];
        var rgb = new float[3];
        for (var i = 0; i < drawn.Length; i += 3)
        {
            transform.ToSrgb([image.Samples[i] / 255f, image.Samples[i + 1] / 255f, image.Samples[i + 2] / 255f], rgb);
            for (var k = 0; k < 3; k++) drawn[i + k] = (byte)Math.Round(rgb[k] * 255);
        }
        return drawn;
    }

    /// <summary>
    /// e-sRGB (EnumCS 20): the extended non-linear range. Before it was taken as sRGB. Until 23.11.2026 what lay outside sRGB was clipped; the
    /// image has such colours, so it comes out in the wide working space (docs/FEHLERGEDAECHTNIS.md, No. 1056) — drawn through its profile to
    /// sRGB the pixels inside sRGB show what they showed before (to ΔE76 1.5).
    /// </summary>
    [TestPdfsFact]
    public void ESrgb_ExtendedRange_InTheWideSpace()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(20)]), long.MaxValue);
        Assert.Equal(JpxColor.Icc, image.Color);
        // 8 bits: R′ = (C − 96) / 127.5, as 8 bits (C − 96) · 2
        NearInsideSrgb([.. plain.Select(v => (byte)Math.Clamp((v - 96) * 2, 0, 255))], DrawnSrgb(image));
    }

    /// <summary>
    /// ROMM-RGB (EnumCS 21, ISO 22028-2): an ICC profile built from its definition — primaries under D50, tone curve E′/16 below 0.03125,
    /// otherwise E′^1.8. Before it was taken as sRGB (colours too pale). Writing the image anew carries the same profile.
    /// </summary>
    [TestPdfsFact]
    public void RommRgb_ProfileFromItsDefinition()
    {
        var cs = Rgb.Value;
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(21)]), long.MaxValue);
        Assert.Equal(JpxColor.Icc, image.Color);
        Assert.Equal(Plain(cs), image.Samples);
        var profile = IccProfile.Parse(image.IccProfile!);
        Assert.NotNull(profile);
        Assert.Equal(3, profile.Channels);

        // the colorants against the matrix published for ROMM RGB (D50), to three places
        var (r, g, b) = CalibratedProfile.Colorants((0.7347, 0.2653), (0.1596, 0.8404), (0.0366, 0.0001), [0.9642f, 1f, 0.8249f]);
        double[][] published = [[0.7977, 0.2880, 0.0000], [0.1352, 0.7119, 0.0000], [0.0313, 0.0001, 0.8249]];
        foreach (var (column, expected) in new[] { r, g, b }.Zip(published))
            for (var i = 0; i < 3; i++) Assert.Equal(expected[i], column[i], 0.002);

        // writing the image anew (blacking out, cutting): /ICCBased with that profile
        var dict = TestPdf.Dict(("Width", PdfInteger.Of(40)), ("Height", PdfInteger.Of(27)), ("Filter", TestPdf.N("JPXDecode")));
        var written = ImageBlanker.DecodeJpx(new PdfStream(dict, Jpx(cs, [Enumerated(21)])), Jpx(cs, [Enumerated(21)]), 0);
        Assert.NotNull(written);
        var space = Assert.IsType<PdfArray>(written.Value.OwnSpace!.Value.Space);
        Assert.Equal("ICCBased", ((PdfName)space.Get(0)).Value);
    }

    /// <summary>
    /// CIELab (EnumCS 14, ISO/IEC 15444-2, M.11.7.4.1): without parameters the ranges 100, 170, 200 with the offsets 0, 2^(n−1),
    /// 2^(n−2) + 2^(n−3) under D50; with parameters those and the illuminant. Written as a PDF /Lab colour space. Before drawn as RGB.
    /// </summary>
    [TestPdfsFact]
    public void CieLab_DefaultsAndParameters()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        static byte[] Encode(byte[] raw, double[] range, double[] offset)
        {
            var o = new byte[raw.Length];
            for (var i = 0; i < raw.Length; i++)
            {
                var k = i % 3;
                var v = (raw[i] - offset[k]) * range[k] / 255;
                o[i] = (byte)Math.Clamp(Math.Round(k == 0 ? v * 255 / 100 : v + 128), 0, 255);
            }
            return o;
        }

        var defaults = JpxDecoder.Decode(Jpx(cs, [Enumerated(14)]), long.MaxValue);
        Assert.Equal(JpxColor.Lab, defaults.Color);
        Assert.Equal(Encode(plain, [100, 170, 200], [0, 128, 96]), defaults.Samples);
        Assert.Equal(0.82521, defaults.WhitePoint[2], 0.0001);

        // the encoding of ICC profiles (RL 100, RA and RB 255, offsets 0 and 128) under D65
        byte[] ep = [.. U32(100), .. U32(0), .. U32(255), .. U32(128), .. U32(255), .. U32(128), .. U32(0x00443635)];
        var given = JpxDecoder.Decode(Jpx(cs, [Enumerated(14, ep: ep)]), long.MaxValue);
        Assert.Equal(Encode(plain, [100, 255, 255], [0, 128, 128]), given.Samples);
        Assert.Equal(1.08883, given.WhitePoint[2], 0.0001);

        var space = Assert.IsType<PdfArray>(given.SpaceObject());
        Assert.Equal("Lab", ((PdfName)space.Get(0)).Value);
        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        Assert.Equal(3, PdfImageDecoder.JpxSpace(null, given, doc, ColorSpace.RelativeColorimetric, ColorSpace.BpcDefault).Components);
    }

    /// <summary>
    /// CIELab with signed channels (ISO/IEC 15444-2, M.11.7.4.1 sentence 59, Tables M.24bis and M.29bis): without parameters the offsets are 0
    /// and MAX is 2^(n−1) − 1, so L* = N·100/127, a* = N·170/127, b* = N·200/127 for 8 bits. The codestream marked signed (Ssiz) holds the same
    /// samples less 128. Before signed channels were read with the offsets and MAX of unsigned ones (docs/FEHLERGEDAECHTNIS.md, No. 974).
    /// </summary>
    [TestPdfsFact]
    public void CieLab_SignedChannels_OffsetsZero()
    {
        var cs = (byte[])Rgb.Value.Clone();
        var siz = 2;
        while (!(cs[siz] == 0xFF && cs[siz + 1] == 0x51)) siz += 2 + (cs[siz + 2] << 8 | cs[siz + 3]);
        var components = cs[siz + 38] << 8 | cs[siz + 39];                    // Csiz after Lsiz, Rsiz and the eight sizes
        for (var c = 0; c < components; c++) cs[siz + 40 + 3 * c] |= 0x80;   // Ssiz: signed, same depth
        var plain = Plain(cs);
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(14)]), long.MaxValue);
        var expected = new byte[plain.Length];
        for (var i = 0; i < plain.Length; i++)
        {
            var k = i % 3;
            var v = (plain[i] - 128) * (k == 0 ? 100 : k == 1 ? 170 : 200) / 127.0;
            expected[i] = (byte)Math.Clamp(Math.Round(k == 0 ? v * 255 / 100 : v + 128), 0, 255);
        }
        Near(expected, image.Samples);
    }

    /// <summary>
    /// Signed channels with an ICC profile (ITU-T T.800, I.5.3.3 sentences 36 and 37): clipped to the range of the curve before the profile,
    /// negative values to 0, the positive half stretched over the curve — before they were shifted by half the range as for display
    /// (decided on 20.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 979).
    /// </summary>
    [TestPdfsFact]
    public void IccProfile_SignedChannels_NegativeToZero()
    {
        var cs = (byte[])Rgb.Value.Clone();
        var siz = 2;
        while (!(cs[siz] == 0xFF && cs[siz + 1] == 0x51)) siz += 2 + (cs[siz + 2] << 8 | cs[siz + 3]);
        var components = cs[siz + 38] << 8 | cs[siz + 39];
        for (var c = 0; c < components; c++) cs[siz + 40 + 3 * c] |= 0x80;
        var plain = Plain(cs);
        var image = JpxDecoder.Decode(Jpx(cs, [Icc(CalibratedProfile.Srgb())]), long.MaxValue);
        Assert.Equal(JpxColor.Icc, image.Color);
        Near(plain.Select(v => (byte)Math.Round(Math.Clamp(v - 128, 0, 127) * 255 / 127.0)).ToArray(), image.Samples);
    }

    /// <summary>
    /// The bi-level spaces (EnumCS 0: 1 is black, EnumCS 15: 1 is white). Before EnumCS 0 was not known and its samples were drawn as grey the
    /// wrong way round.
    /// </summary>
    [TestPdfsFact]
    public void BiLevel_ZeroIsWhiteOrBlack()
    {
        var cs = Gray.Value;
        Assert.All(Plain(cs), v => Assert.Equal(0, v));
        Assert.All(JpxDecoder.Decode(Jpx(cs, [Enumerated(0)]), long.MaxValue).Samples, v => Assert.Equal(255, v));
        Assert.All(JpxDecoder.Decode(Jpx(cs, [Enumerated(15)]), long.MaxValue).Samples, v => Assert.Equal(0, v));
    }

    // ---- No. 736: opacity

    /// <summary>
    /// ISO 32000-2, 7.4.9 (Bericht M2, Zeilen 45, 49): the opacity in the JPX data counts only with /SMaskInData 1 and without a /SMask of the
    /// image — with /SMaskInData 0 the image is opaque, with a /SMask beside it the /SMask gives the opacity (here 128 everywhere); the image takes the
    /// size of the data, also where /Width and /Height say another (S-A A76).
    /// </summary>
    [TestPdfsFact]
    public void Pdf_SMaskInDataAndTheSizeOfTheData()
    {
        var cs = Rgb.Value;
        var jpx = Jpx(cs, [], Box("jplh", Box("opct", [0])));
        var direct = JpxDecoder.Decode(jpx, long.MaxValue);
        var alpha = Assert.IsType<byte[]>(direct.Alpha);
        Assert.Contains(alpha, a => a < 255);
        DecodedImage Draw(int inData, bool sMask, int widthOffset = 0)
        {
            var pdf = new TestPdf();
            var dict = TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(direct.Width + widthOffset)),
                ("Height", PdfInteger.Of(direct.Height)), ("Filter", TestPdf.N("JPXDecode")), ("SMaskInData", PdfInteger.Of(inData)));
            if (sMask)
                dict.Set(PdfName.Get("SMask"), TestPdf.Ref(pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
                    ("Width", PdfInteger.Of(1)), ("Height", PdfInteger.Of(1)), ("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8))), [128]))));
            var number = pdf.Add(new PdfStream(dict, jpx));
            pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Pages")),
                ("Kids", new PdfArray()), ("Count", PdfInteger.Of(0))))))));
            using var doc = PdfDocument.Open(pdf.Build());
            return PdfImageDecoder.Decode(new ImageSource((PdfStream)doc.GetObject(number), null, null, false, 0), doc)!;
        }
        // (an opaque grey image comes as one byte per pixel: the two colour channels left beside the opacity are drawn as grey, No. 996)
        static byte[] Opacity(DecodedImage image) =>
            [.. Enumerable.Range(0, image.Width * image.Height).Select(i => image.Format == ImagePixels.Gray ? (byte)255 : image.Pixels[i * 4 + 3])];
        var withOpacity = Draw(1, false);
        Assert.Equal(ImagePixels.Rgba, withOpacity.Format);
        Assert.Equal(alpha, Opacity(withOpacity));
        Assert.All(Opacity(Draw(0, false)), a => Assert.Equal(255, a));
        Assert.All(Opacity(Draw(1, true)), a => Assert.Equal(128, a));   // the /SMask, not the data
        var other = Draw(1, false, widthOffset: 7);
        Assert.Equal((direct.Width, direct.Height), (other.Width, other.Height));
    }

    /// <summary>
    /// Bare codestreams, which carry no channel definition, in an image dictionary that does not fit their channels (ISO 32000-2, 7.4.9): with
    /// /SMaskInData and one channel more than the colour, the last is the opacity (grey and opacity; RGB and opacity with /DeviceRGB), with as
    /// many channels as the colour none; channels no colour fits — two without /ColorSpace, five — the first as grey or the first four as
    /// CMYK. Before such images came out in stripes, and four channels with /DeviceRGB and /SMaskInData as CMYK without opacity
    /// (docs/FEHLERGEDAECHTNIS.md, No. 996). The codestreams of the file of the public test collection that checks exactly this.
    /// </summary>
    [TestPdfsFact]
    public void BareCodestream_SMaskInDataAndChannelsNoColourFits()
    {
        static byte[] Stored(int number) => StoredStream("jpx_numcomps.pdf", number);
        var (twoA, three, twoB, five) = (Stored(11), Stored(14), Stored(15), Stored(13));
        var four = JpxRobustTests.Codestream(0, 8, 8, 4, 1, 0, 1, 0);
        DecodedImage Draw(byte[] data, int width, int height, string filter, PdfObject? space, int inData, int bits = 0)
        {
            var pdf = new TestPdf();
            var dict = TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(width)),
                ("Height", PdfInteger.Of(height)));
            if (filter.Length > 0) dict.Set(PdfName.Filter, TestPdf.N(filter));
            if (space is not null) dict.Set(PdfName.Get("ColorSpace"), space);
            if (inData != 0) dict.Set(PdfName.Get("SMaskInData"), PdfInteger.Of(inData));
            if (bits != 0) dict.Set(PdfName.Get("BitsPerComponent"), PdfInteger.Of(bits));
            var number = pdf.Add(new PdfStream(dict, data));
            pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Pages")),
                ("Kids", new PdfArray()), ("Count", PdfInteger.Of(0))))))));
            using var doc = PdfDocument.Open(pdf.Build());
            return PdfImageDecoder.Decode(new ImageSource((PdfStream)doc.GetObject(number), null, null, false, 0), doc)!;
        }
        static byte[] Rgba(DecodedImage image) => image.Format == ImagePixels.Gray
            ? [.. image.Pixels.Take(image.Width * image.Height).SelectMany(g => new[] { g, g, g, (byte)255 })]
            : image.Pixels[..(image.Width * image.Height * 4)];
        static byte[] Channel(byte[] samples, int channels, int k) => [.. Enumerable.Range(0, samples.Length / channels).Select(i => samples[i * channels + k])];
        static byte[] Every(byte[] rgba, int k) => Channel(rgba, 4, k);

        // grey and opacity: /DeviceGray, /SMaskInData 1, two channels
        var plain = JpxDecoder.Decode(twoB, long.MaxValue);
        Assert.Equal(2, plain.Channels);
        var greyOpacity = Rgba(Draw(twoB, 32, 32, "JPXDecode", TestPdf.N("DeviceGray"), 1));
        Assert.Equal(Channel(plain.Samples, 2, 0), Every(greyOpacity, 0));
        Assert.Equal(Every(greyOpacity, 0), Every(greyOpacity, 2));
        Assert.Equal(Channel(plain.Samples, 2, 1), Every(greyOpacity, 3));
        Assert.Contains(Every(greyOpacity, 3), a => a < 255);

        // two channels without /ColorSpace: the first as grey, opaque
        var plainA = JpxDecoder.Decode(twoA, long.MaxValue);
        var grey = Rgba(Draw(twoA, 32, 32, "JPXDecode", null, 0));
        Assert.Equal(Channel(plainA.Samples, 2, 0), Every(grey, 0));
        Assert.Equal(Every(grey, 0), Every(grey, 1));
        Assert.All(Every(grey, 3), a => Assert.Equal(255, a));

        // three channels, /DeviceRGB, /SMaskInData 1: as many channels as the colour — no opacity
        var rgb = Rgba(Draw(three, 32, 32, "JPXDecode", TestPdf.N("DeviceRGB"), 1));
        var plainThree = JpxDecoder.Decode(three, long.MaxValue).Samples;
        for (var k = 0; k < 3; k++) Assert.Equal(Channel(plainThree, 3, k), Every(rgb, k));
        Assert.All(Every(rgb, 3), a => Assert.Equal(255, a));

        // five channels without /ColorSpace: the first four as CMYK, as the same samples in an uncompressed CMYK image
        var plainFive = JpxDecoder.Decode(five, long.MaxValue);
        Assert.Equal(5, plainFive.Channels);
        byte[] firstFour = [.. Enumerable.Range(0, plainFive.Samples.Length / 5).SelectMany(i => plainFive.Samples.Skip(i * 5).Take(4))];
        Assert.Equal(Rgba(Draw(firstFour, 32, 32, "", TestPdf.N("DeviceCMYK"), 0, bits: 8)), Rgba(Draw(five, 32, 32, "JPXDecode", null, 0)));

        // four channels, /DeviceRGB, /SMaskInData 1 and 2: RGB and (premultiplied) opacity, not CMYK
        var plainFour = JpxDecoder.Decode(four, long.MaxValue).Samples;
        var rgbOpacity = Rgba(Draw(four, 8, 1, "JPXDecode", TestPdf.N("DeviceRGB"), 1));
        Assert.Equal(Channel(plainFour, 4, 3), Every(rgbOpacity, 3));
        for (var k = 0; k < 3; k++) Assert.Equal(Channel(plainFour, 4, k), Every(rgbOpacity, k));
        Assert.Equal(Channel(plainFour, 4, 3), Every(Rgba(Draw(four, 8, 1, "JPXDecode", TestPdf.N("DeviceRGB"), 2)), 3));
        // without /SMaskInData the four channels stay CMYK by their number (7.4.9)
        Assert.All(Every(Rgba(Draw(four, 8, 1, "JPXDecode", null, 0)), 3), a => Assert.Equal(255, a));
        Assert.True(JpxDecoder.OpacityLast(4, 3, []) && !JpxDecoder.OpacityLast(4, null, []) && JpxDecoder.OpacityLast(2, null, []) &&
                    JpxDecoder.OpacityLast(5, null, []) && !JpxDecoder.OpacityLast(3, 3, []) && JpxDecoder.OpacityLast(2, 4, []));
    }

    /// <summary>
    /// The Opacity box of the first compositing layer (ISO/IEC 15444-2, M.11.7.6, M.9.2.8): the last channel is opacity (type 0) or
    /// premultiplied opacity (type 1); a chroma key (type 2) is ignored in PDF (ISO 32000-2, 7.4.9). Before the box was not read and the
    /// opacity drawn as a colour.
    /// </summary>
    [TestPdfsFact]
    public void OpacityBox_LastChannelIsOpacity()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        var count = plain.Length / 3;
        foreach (var type in new byte[] { 0, 1 })
        {
            var image = JpxDecoder.Decode(Jpx(cs, [], Box("jplh", Box("opct", [type]))), long.MaxValue);
            Assert.Equal(2, image.Channels);
            Assert.Equal(type == 1, image.PremultipliedAlpha);
            Assert.Equal([.. Enumerable.Range(0, count).Select(i => plain[i * 3 + 2])], Assert.IsType<byte[]>(image.Alpha));
            Assert.Equal([.. Enumerable.Range(0, count).SelectMany(i => new[] { plain[i * 3], plain[i * 3 + 1] })], image.Samples);
        }
        var key = JpxDecoder.Decode(Jpx(cs, [], Box("jplh", Box("opct", [2, 3, 0, 0, 0]))), long.MaxValue);
        Assert.Equal(3, key.Channels);
        Assert.Null(key.Alpha);
    }

    // ---- No. 739: /Decode

    private static PdfStream Image(byte[] data, string filter, int width, int height, PdfObject? space, PdfObject? decode, PdfStream? softMask = null)
    {
        var dict = TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(width)),
            ("Height", PdfInteger.Of(height)), ("BitsPerComponent", PdfInteger.Of(8)), ("Filter", TestPdf.N(filter)));
        if (space is not null) dict.Set(PdfName.Get("ColorSpace"), space);
        if (decode is not null) dict.Set(PdfName.Get("Decode"), decode);
        if (softMask is not null) dict.Set(PdfName.Get("SMask"), softMask);
        return new PdfStream(dict, data);
    }

    private static PdfArray Inverted => new([PdfInteger.Of(1), PdfInteger.Of(0)]);

    private static byte[] GrayDrawn(PdfDocument doc, PdfStream image)
    {
        var decoded = PdfImageDecoder.Decode(new ImageSource(image, null, null, false, 0), doc);
        Assert.NotNull(decoded);
        return decoded.Format == ImagePixels.Gray ? decoded.Pixels : [.. decoded.Rgba.Where((_, i) => i % 4 == 0)];
    }

    /// <summary>
    /// /Decode with JPEG 2000 (ISO 32000-2, 7.4.9): counts with a /ColorSpace of the image, ignored without one. Before it was always ignored —
    /// here an inverted grey image drawn black instead of white.
    /// </summary>
    [TestPdfsFact]
    public void Decode_CountsOnlyWithAColorSpace()
    {
        var cs = Gray.Value;   // 128 × 64, all 0
        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        Assert.All(GrayDrawn(doc, Image(cs, "JPXDecode", 128, 64, TestPdf.N("DeviceGray"), Inverted)), v => Assert.Equal(255, v));
        Assert.All(GrayDrawn(doc, Image(cs, "JPXDecode", 128, 64, null, Inverted)), v => Assert.Equal(0, v));
        Assert.All(GrayDrawn(doc, Image(cs, "JPXDecode", 128, 64, TestPdf.N("DeviceGray"), null)), v => Assert.Equal(0, v));
    }

    /// <summary>
    /// Blacking out a JPEG 2000 image: the new image keeps the /Decode where it counted (with a /ColorSpace of the image) and leaves it out where
    /// it was ignored (the colour space of the data) — the part beside the bar looks as before. Before /Decode was always dropped, which with
    /// a /ColorSpace turned that part the other way round.
    /// </summary>
    [TestPdfsFact]
    public void Decode_BlackingOut_AsWhenDrawing()
    {
        foreach (var (space, white) in new (PdfObject?, bool)[] { (TestPdf.N("DeviceGray"), true), (null, false) })
        {
            var pdf = TestPdf.Simple(pages: 1, title: null);
            var image = pdf.Add(Image(Gray.Value, "JPXDecode", 128, 64, space, Inverted));
            var content = pdf.Add(TestPdf.FlateStream("q 256 0 0 128 100 400 cm /Im0 Do Q"u8.ToArray()));
            var page = (PdfDictionary)pdf.Get(5);
            page.Set(PdfName.Get("Contents"), TestPdf.Ref(content));
            page.Set(PdfName.Get("Resources"), TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image))))));
            using var doc = PdfDocument.Open(pdf.Build());
            using var after = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(100, 400, 228, 528))));
            var xobjects = after.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!;
            var blanked = Assert.IsType<PdfStream>(xobjects.Get(xobjects.Keys.Single()));
            Assert.Equal(white, blanked.Dictionary.ContainsKey(PdfName.Get("Decode")));
            // the image may come back on a finer grid (bar edges between its pixels): the right part, beside the bar, by its own size
            var pixels = GrayDrawn(after, blanked);
            var (w, h) = ((int)blanked.Dictionary.GetInteger(PdfName.Get("Width"))!, (int)blanked.Dictionary.GetInteger(PdfName.Get("Height"))!);
            for (var y = 0; y < h; y++)
                for (var x = w * 9 / 16; x < w; x++)
                    Assert.Equal(white ? 255 : 0, pixels[y * w + x]);
        }
    }

    /// <summary>
    /// Cutting a JPEG or JPEG 2000 image with a mask: where the cut keeps the samples in the image's own colour space, its /Decode keeps
    /// counting. Before it was dropped — an inverted grey JPEG came out the other way round.
    /// </summary>
    [TestPdfsFact]
    public void Decode_KeptWhenCutting()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        var mask = Image(Enumerable.Repeat((byte)255, 64 * 32).ToArray(), "FlateDecode", 64, 32, TestPdf.N("DeviceGray"), null);
        mask.Dictionary.Remove(PdfName.Filter);
        var samples = Enumerable.Range(0, 64 * 32).Select(i => (byte)(i % 64 * 4)).ToArray();
        var jpeg = Rendering.SkiaImageCodec.Instance.EncodeJpeg(64, 32, 1, samples, 95)!;
        foreach (var (data, filter) in new[] { (jpeg, "DCTDecode"), (Gray.Value, "JPXDecode") })
        {
            var (w, h) = filter == "DCTDecode" ? (64, 32) : (128, 64);
            var withMask = Image(Enumerable.Repeat((byte)255, w * h).ToArray(), "FlateDecode", w, h, TestPdf.N("DeviceGray"), null);
            withMask.Dictionary.Remove(PdfName.Filter);
            var image = Image(data, filter, w, h, TestPdf.N("DeviceGray"), Inverted, withMask);
            var cut = ImageCut.CutEncoded(doc, image, null, 0.5, 0, 1, 1, Rendering.SkiaImageCodec.Instance, 95);
            Assert.NotNull(cut);
            Assert.Equal("[1 0]", cut.Dictionary.Get(PdfName.Get("Decode"))?.ToString()?.Replace(" ]", "]").Replace("[ ", "["));
            var before = GrayDrawn(doc, image);
            var after = GrayDrawn(doc, cut);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w / 2; x++)
                    Assert.InRange(after[y * (w / 2) + x] - before[y * w + w / 2 + x], -6, 6);
        }
    }

    // ---- No. 740: the codestream extensions of ISO/IEC 15444-2 that JPX baseline needs

    private static byte[] Marker(ushort code, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        return [(byte)(code >> 8), (byte)code, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2), .. body];
    }

    private static byte[] U16(int v) => [(byte)(v >> 8), (byte)v];

    private static byte[] F32(params float[] values) => [.. values.SelectMany(v =>
    {
        var b = new byte[4];
        BinaryPrimitives.WriteSingleBigEndian(b, v);
        return b;
    })];

    /// <summary>An 8-bit integer and a 16-bit fraction (ISO/IEC 15444-2, Table A.45).</summary>
    private static byte[] Fixed(double v) => [(byte)Math.Floor(v), .. U16((int)Math.Round((v - Math.Floor(v)) * 65535))];

    /// <summary>
    /// The codestream with the capabilities <paramref name="rsiz"/> of ISO/IEC 15444-2 in Rsiz, the multiple component field of COD set to
    /// <paramref name="mct"/> (null: as it is), and <paramref name="markers"/> at the end of the main header.
    /// </summary>
    private static byte[] Part2(byte[] cs, int rsiz, int? mct, params byte[][] markers)
    {
        var d = (byte[])cs.Clone();
        d[6] |= (byte)(rsiz >> 8);
        d[7] |= (byte)rsiz;
        var pos = 2;
        while ((d[pos] << 8 | d[pos + 1]) != 0xFF90)
        {
            if ((d[pos] << 8 | d[pos + 1]) == 0xFF52 && mct is { } field) d[pos + 8] = (byte)field;
            pos += 2 + (d[pos + 2] << 8 | d[pos + 3]);
        }
        return [.. d.AsSpan(0, pos), .. markers.SelectMany(m => m), .. d.AsSpan(pos)];
    }

    /// <summary>
    /// CBD, two MCT arrays (decorrelation, offsets; 32-bit floating point), one MCC collection over components 0–2 and an MCO with that
    /// one stage: the inverse of the reversible component transformation of ISO/IEC 15444-1 (G.2), linear, with red and blue swapped, and the
    /// DC level as offsets.
    /// </summary>
    private static byte[][] SwappedRct(bool reversibleFlag = false) =>
    [
        Marker(0xFF78, U16(0x8003), [0x07]),                                                   // CBD: three components of 8 bits
        Marker(0xFF74, U16(0), U16(0x0901), U16(0),                                            // MCT 1: decorrelation, float32
            F32(1, 0.75f, -0.25f,                                                              //   output 0 = blue
                1, -0.25f, -0.25f,                                                             //   output 1 = green
                1, -0.25f, 0.75f)),                                                            //   output 2 = red
        Marker(0xFF74, U16(0), U16(0x0A01), U16(0), F32(128, 128, 128)),                     // MCT 1: offsets
        Marker(0xFF75, U16(0), [0], U16(0), U16(1),                                            // MCC 0, one collection:
            [0x01], U16(3), [0, 1, 2], U16(3), [0, 1, 2], [(byte)(reversibleFlag ? 1 : 0), 0x01, 0x01]),   // decorrelation, arrays 1, 1
        Marker(0xFF77, [1, 0]),                                                                // MCO: one stage, MCC 0
    ];

    /// <summary>
    /// ISO/IEC 15444-2, A.3.7 and A.3.8: an MCT or MCC series in several segments — the first (Z = 0) names the last index Y, the parts are joined
    /// in the order of Z, also when they stand in another order. A series with a part missing or a part twice is refused (before joined as it
    /// was; docs/FEHLERGEDAECHTNIS.md, No. 984). An MCT without an MCC that uses it changes nothing (Bericht M2, Zeilen 269, 534).
    /// </summary>
    [TestPdfsFact]
    public void Part2_SeriesInSeveralSegments()
    {
        var cs = Rgb.Value;
        var whole = JpxDecoder.Decode(Part2(cs, 0x8100, 2, SwappedRct()), long.MaxValue).Samples;
        var parts = SwappedRct();
        var matrix = F32(1, 0.75f, -0.25f, 1, -0.25f, -0.25f, 1, -0.25f, 0.75f);
        var mctFirst = Marker(0xFF74, U16(0), U16(0x0901), U16(1), matrix[..20]);
        var mctSecond = Marker(0xFF74, U16(1), U16(0x0901), matrix[20..]);
        byte[] collection = [0x01, .. U16(3), 0, 1, 2, .. U16(3), 0, 1, 2, 0, 0x01, 0x01];
        var mccFirst = Marker(0xFF75, U16(0), [0], U16(1), U16(1), collection[..5]);
        var mccSecond = Marker(0xFF75, U16(1), [0], collection[5..]);
        byte[][] Build(params byte[][] mct) => [parts[0], .. mct, parts[2], mccSecond, mccFirst, parts[4]];
        Assert.Equal(whole, JpxDecoder.Decode(Part2(cs, 0x8100, 2, Build(mctSecond, mctFirst)), long.MaxValue).Samples);
        Assert.Contains("MCT unvollständig", Assert.Throws<JpxException>(() => JpxDecoder.Decode(Part2(cs, 0x8100, 2, Build(mctFirst)), long.MaxValue)).Message);
        Assert.Contains("MCT unvollständig",
            Assert.Throws<JpxException>(() => JpxDecoder.Decode(Part2(cs, 0x8100, 2, Build(mctFirst, mctSecond, mctSecond)), long.MaxValue)).Message);
        byte[][] mccMissing = [parts[0], parts[1], parts[2], mccFirst, parts[4]];
        Assert.Contains("MCC unvollständig", Assert.Throws<JpxException>(() => JpxDecoder.Decode(Part2(cs, 0x8100, 2, mccMissing), long.MaxValue)).Message);
        // an MCT array no collection uses: the transformation is the one of the MCC
        byte[][] spare = [.. parts[..4], Marker(0xFF74, U16(0), U16(0x0907), U16(0), F32(9, 9, 9, 9, 9, 9, 9, 9, 9)), parts[4]];
        Assert.Equal(whole, JpxDecoder.Decode(Part2(cs, 0x8100, 2, spare), long.MaxValue).Samples);
    }

    /// <summary>
    /// The array-based multiple component transformation (ISO/IEC 15444-2, J.3.1.1.1, required for JPX baseline by M.9.2.3): W = T·C + o,
    /// the components after CBD, without the DC level shift. Before the field was taken for the transformation of ISO/IEC 15444-1 and the
    /// marker segments were ignored — here red and blue stayed where they were.
    /// </summary>
    [TestPdfsFact]
    public void Part2_Decorrelation_MatrixAndOffsets()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        var swapped = new byte[plain.Length];
        for (var i = 0; i < plain.Length; i += 3) (swapped[i], swapped[i + 1], swapped[i + 2]) = (plain[i + 2], plain[i + 1], plain[i]);
        // the reversible transformation rounds down; the linear inverse lies within one step of it
        Near(swapped, JpxDecoder.Decode(Part2(cs, 0x8100, 2, SwappedRct()), long.MaxValue).Samples);
        // no stage (Nmco = 0): no transformation, the level shift as usual
        byte[][] none = [.. SwappedRct()[..4], Marker(0xFF77, [0])];
        Assert.Equal(plain, JpxDecoder.Decode(Part2(cs, 0x8100, 3, none), long.MaxValue).Samples);
        // a reversible decorrelation is not required for JPX baseline and not implemented: refused, not drawn wrong
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(Part2(cs, 0x8100, 2, SwappedRct(reversibleFlag: true)), long.MaxValue));
        // without the capability in Rsiz the field keeps its meaning of ISO/IEC 15444-1 and the segments mean nothing
        Assert.Equal(plain, JpxDecoder.Decode(Part2(cs, 0, 1, SwappedRct()), long.MaxValue).Samples);
    }

    /// <summary>
    /// The non-linearity (ISO/IEC 15444-2, Annex K, required for JPX baseline by M.9.2.3): gamma style with the curve of ITU-R BT.709 that the
    /// standard gives as its example (K.2.1), LUT style (K.2.2), and the binary complement, which leaves unsigned values as they are (K.2.3).
    /// Before the NLT segment was ignored. The gamma parameters as Equations K-1 and K-2 name them: y = A·z^E − B above z = T/S, so for
    /// BT.709 A = 1.099, B = 0.099, T = 4.5 · 0.018 = 0.081 (before this test had A and B swapped and the threshold of z, as the decoder had
    /// it; the conformance stream nlt1 of T.803, Annex H.2 showed it, docs/FEHLERGEDAECHTNIS.md, No. 971).
    /// </summary>
    [TestPdfsFact]
    public void Part2_NonLinearity_GammaLutComplement()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);

        // gamma: inverse of y = 4.5·z below z = 0.018 (y = 0.081), otherwise 1.099·z^0.45 − 0.099
        var gamma = Marker(0xFF76, U16(65535), [0x07], [1], Fixed(0.45), Fixed(4.5), Fixed(0.081), Fixed(1.099), Fixed(0.099));
        var expected = plain.Select(v =>
        {
            var y = v / 255.0;
            var z = y <= 0.081 ? y / 4.5 : Math.Pow((y + 0.099) / 1.099, 1 / 0.45);
            return (byte)Math.Clamp(Math.Round(z * 255), 0, 255);
        }).ToArray();
        Near(expected, JpxDecoder.Decode(Part2(cs, 0x8200, null, gamma), long.MaxValue).Samples);

        // LUT: three points 0, 0.25, 1 over the whole input range
        var lut = Marker(0xFF76, U16(65535), [0x07], [2], U16(2), [0, 0, 0, 0], [0xFF, 0xFF, 0xFF, 0xFF], [16], U16(0), U16(16384), U16(65535));
        var table = plain.Select(v =>
        {
            var y = v / 255.0;
            var z = y < 0.5 ? 16384 / 65535.0 * (y / 0.5) : 16384 / 65535.0 + (1 - 16384 / 65535.0) * ((y - 0.5) / 0.5);
            return (byte)Math.Clamp(Math.Round(z * 255), 0, 255);
        }).ToArray();
        Near(table, JpxDecoder.Decode(Part2(cs, 0x8200, null, lut), long.MaxValue).Samples);

        // binary complement on unsigned values: unchanged; only one component (2) with a non-linearity of its own
        var complement = Marker(0xFF76, U16(65535), [0x07], [3]);
        Assert.Equal(plain, JpxDecoder.Decode(Part2(cs, 0x8200, null, complement), long.MaxValue).Samples);
        var onlyBlue = Marker(0xFF76, U16(2), [0x07], [1], Fixed(0.45), Fixed(4.5), Fixed(0.081), Fixed(1.099), Fixed(0.099));
        var mixed = JpxDecoder.Decode(Part2(cs, 0x8200, null, onlyBlue), long.MaxValue).Samples;
        for (var i = 0; i < plain.Length; i++)
            Assert.InRange(mixed[i] - (i % 3 == 2 ? expected[i] : plain[i]), -1, 1);
    }

    /// <summary>
    /// ISO/IEC 15444-2, K.1.3 and A.3.10: a non-linearity in a tile-part header with a depth of its own — here a LUT that keeps the values
    /// (0 to 1), with BDnlt 4 bits, where the main header has no NLT and the output keeps the 8 bits of SIZ: its values are scaled to the 8 bits,
    /// the image stays as it is. Before the 4-bit values were taken as 8-bit ones, the image came out dark (docs/FEHLERGEDAECHTNIS.md, No. 986).
    /// </summary>
    [TestPdfsFact]
    public void Part2_NonLinearityInTheTilePartHeader_ScaledToTheOutputDepth()
    {
        var cs = Part2(Rgb.Value, 0x8200, null);
        var lut = Marker(0xFF76, U16(65535), [0x03], [2], U16(1), [0, 0, 0, 0], [0xFF, 0xFF, 0xFF, 0xFF], [16], U16(0), U16(65535));
        var sot = cs.AsSpan().IndexOf([(byte)0xFF, (byte)0x90]);
        var psot = BinaryPrimitives.ReadUInt32BigEndian(cs.AsSpan(sot + 6));
        byte[] withTile = [.. cs[..(sot + 12)], .. lut, .. cs[(sot + 12)..]];
        BinaryPrimitives.WriteUInt32BigEndian(withTile.AsSpan(sot + 6), psot + (uint)lut.Length);
        Near(Plain(Rgb.Value), JpxDecoder.Decode(withTile, long.MaxValue).Samples, 2);
    }

    // ---- No. 741: crafted sizes (decided on 11.11.2026: security findings of the conformity run fixed at once)

    /// <summary>
    /// A crafted fragment list that names the same range of the file a thousand times: the fragments of a codestream are parts of the file,
    /// together never larger than it — refused. Before they were put together (up to 2 GB from a small file).
    /// </summary>
    [TestPdfsFact]
    public void Crafted_FragmentsLargerThanTheFile_Refused()
    {
        var cs = Rgb.Value;
        byte[] head =
        [
            .. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]),
            .. Box("ftyp", Encoding.ASCII.GetBytes("jpx "), U32(0), Encoding.ASCII.GetBytes("jpx jpxb")),
            .. Box("jp2h", Enumerated(16)),
            .. Box("free", new byte[5000]),
        ];
        var at = head.Length + 8;
        var entries = Enumerable.Range(0, 1000).SelectMany(_ => (byte[])[.. U32(0), .. U32((uint)at), .. U32((uint)cs.Length), 0, 0]).ToArray();
        byte[] file = [.. head, .. Box("mdat", cs), .. Box("ftbl", Box("flst", [0x03, 0xE8], entries))];
        Assert.Contains("kein Datenstrom", Assert.Throws<JpxException>(() => JpxDecoder.Decode(file, long.MaxValue)).Message);
    }

    /// <summary>
    /// A crafted component collection with 30 000 output components on a tile of 40 000 samples: refused before anything is allocated (it would
    /// take 4.8 GB). The limit on multiplications alone did not catch it (one input: 1.2 billion).
    /// </summary>
    [Fact]
    public void Crafted_Part2CollectionTooLarge_RefusedBeforeAllocating()
    {
        var header = new JpxPart2Header();
        var outputs = Enumerable.Range(0, 30000).SelectMany(i => new[] { (byte)(i >> 8), (byte)i }).ToArray();
        byte[] mcc = [0, 0, 0, 0, 0, 0, 1, 0x01, 0, 1, 0, 0x80 | (30000 >> 8), 30000 & 0xFF, .. outputs, 0, 0, 0];
        JpxPart2.ReadMcc(header, mcc, 0, mcc.Length);
        header.Order = [0];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Contains("zu groß", Assert.Throws<JpxException>(() => JpxPart2.Transform(header, new JpxPart2Header(), [new float[40000]], 3)).Message);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"{watch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// A crafted order of 255 stages, each the same collection of 200 inputs and outputs (A.3.9, J.2; PLAN §6 point 8, probe of T.801,
    /// 23.11.2026, B16): every stage within the limit alone, together 255 times it — the stages of a tile share the limit, refused at the second
    /// stage. The identity (no array) keeps the test fast: the work is counted as for a full matrix.
    /// </summary>
    [Fact]
    public void Crafted_Part2ManyStagesOfOneCollection_SharedLimit()
    {
        var header = new JpxPart2Header();
        var indices = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
        byte[] mcc = [0, 0, 0, 0, 0, 0, 1, 0x01, 0, 200, .. indices, 0, 200, .. indices, 0, 0, 0];
        JpxPart2.ReadMcc(header, mcc, 0, mcc.Length);
        header.Order = [.. Enumerable.Repeat(0, 255)];
        var input = Enumerable.Range(0, 200).Select(_ => new float[40000]).ToArray();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Contains("zu groß", Assert.Throws<JpxException>(() => JpxPart2.Transform(header, new JpxPart2Header(), input, 200)).Message);
        Assert.True(watch.ElapsedMilliseconds < 5000, $"{watch.ElapsedMilliseconds} ms");
        // one stage alone stays allowed
        header.Order = [0];
        Assert.Equal(200, JpxPart2.Transform(header, new JpxPart2Header(), input, 200).Length);
    }

    /// <summary>
    /// Bit depths above 38 are reserved in CBD (Table A.31) and NLT (Table A.43): the segment counts as damaged — before up to 128 bits were
    /// taken and the shifts of the decoder ran over without a word (probe of T.801, 23.11.2026, B17). 38 bits stay allowed.
    /// </summary>
    [Fact]
    public void ReservedBitDepths_CbdAndNlt_Refused()
    {
        Assert.Null(JpxPart2.ReadCbd([0x80, 1, 0x7F], 0, 3));
        Assert.Null(JpxPart2.ReadCbd([0x80, 1, 38], 0, 3));
        Assert.Equal(38, JpxPart2.ReadCbd([0x80, 1, 37], 0, 3)![0].Bits);
        var header = new JpxPart2Header();
        JpxPart2.ReadNlt(header, [0xFF, 0xFF, 0x7F, 3], 0, 4);
        JpxPart2.ReadNlt(header, [0, 1, 38, 3], 0, 4);
        Assert.Empty(header.Nlt);
        JpxPart2.ReadNlt(header, [0, 2, 0x80 | 37, 3], 0, 4);
        Assert.Equal((38, true), (header.Nlt[2].Bits, header.Nlt[2].Signed));
    }

    /// <summary>
    /// A crafted collection without inputs and 32 767 outputs, all component 0, on a tile of a million samples (A.3.8, J.2; PLAN §6 point 8,
    /// T.801 J, 23.11.2026): every output counts as at least one operation per sample — before m · n counted 0 and the outputs named twice kept
    /// the stage small, 32 767 arrays of 4 MB each were made. Refused before allocating.
    /// </summary>
    [Fact]
    public void Crafted_Part2CollectionWithoutInputs_Counted()
    {
        var header = new JpxPart2Header();
        byte[] mcc = [0, 0, 0, 0, 0, 0, 1, 0x01, 0, 0, 0x80 | (32767 >> 8), 32767 & 0xFF, .. new byte[2 * 32767], 0, 0, 0];
        JpxPart2.ReadMcc(header, mcc, 0, mcc.Length);
        header.Order = [0];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Contains("zu groß", Assert.Throws<JpxException>(() => JpxPart2.Transform(header, new JpxPart2Header(), [new float[1 << 20]], 1)).Message);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"{watch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// A crafted SIZ of 16 384 components in 65 535 tiles (T.800 A.5.1; PLAN §6 point 8, 23.11.2026): every tile is set up with all its components,
    /// so their product is limited — refused while SIZ is read. Before each tile got three arrays of 16 384 entries as its first tile-part was
    /// read: about 25 GB from a header of about a megabyte.
    /// </summary>
    [Fact]
    public void Crafted_ManyComponentsInManyTiles_Refused()
    {
        const int components = 16384;
        var siz = new List<byte> { 0xFF, 0x4F, 0xFF, 0x51 };
        siz.AddRange(U16(38 + 3 * components));
        siz.AddRange(U16(0));
        siz.AddRange([.. U32(65535), .. U32(1), .. U32(0), .. U32(0), .. U32(1), .. U32(1), .. U32(0), .. U32(0)]);
        siz.AddRange(U16(components));
        for (var c = 0; c < components; c++) siz.AddRange([7, 1, 1]);
        byte[] cs = [.. siz, 0xFF, 0xD9];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Contains("Kacheln", Assert.Throws<JpxException>(() => JpxCodestream.Parse(cs, 0, cs.Length)).Message);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"{watch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// T.800 I.4, Table I.1 (PLAN §6 point 8, 23.11.2026): a box with LBox 1 and XLBox 0x7FFFFFFFFFFFFFEC behind the file type box — position plus
    /// length ran over, the position jumped back to the file type box and the box readers of the decoder, the PDF/A check and the image
    /// information went round for ever (the last one threw). Now the box counts as running to the end: no codestream, no hang.
    /// </summary>
    [Fact]
    public void Crafted_HugeExtendedBoxLength_NoEndlessLoop()
    {
        byte[] file =
        [
            .. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]),
            .. Box("ftyp", Encoding.ASCII.GetBytes("jp2 "), U32(0), Encoding.ASCII.GetBytes("jp2 ")),
            .. U32(1), .. Encoding.ASCII.GetBytes("xxxx"), .. U32(0x7FFFFFFF), .. U32(0xFFFFFFEC), .. new byte[16],
        ];
        Assert.Equal(32, file.AsSpan().IndexOf("xxxx"u8) - 4);
        void Within(Action action)
        {
            // a thread of its own, not one of the pool: under the full load of the whole test run the pool gave none within the limit, and
            // the waiting was measured instead of the reader (the reader alone takes milliseconds)
            var run = new Thread(() =>
            {
                try { action(); }
                catch (JpxException) { }
            }) { IsBackground = true };
            run.Start();
            Assert.True(run.Join(TimeSpan.FromSeconds(30)), "the box reader did not finish");
        }
        Within(() => JpxDecoder.Decode(file, long.MaxValue));
        Within(() => PdfKnead.Core.Pdf.Writing.PdfAJpeg2000.Fault(file));
        Within(() => PdfKnead.Core.Imaging.ImageMetadata.FromJpx(file));
    }

    /// <summary>
    /// Removing the metadata boxes of a JPX file whose codestream lies in fragments (T.800 I.7; T.801 M.11.3, M.11.4): the fragment offsets count
    /// from the start of the file — behind a removed box they move back by its length, and a box a fragment points into (here an XML box holding
    /// the colour specification a Cross-Reference box stands for) stays. Before the boxes were cut out with the offsets unchanged and the image
    /// was destroyed (M2, 23.11.2026). The XML box nobody points into goes.
    /// </summary>
    [TestPdfsFact]
    public void MetadataRemoved_FragmentOffsetsMoved_ReferencedBoxKept()
    {
        var cs = Rgb.Value;
        var colour = Enumerated(16)[8..];
        byte[] Flst(long offset, int length) => Box("flst", [0, 1], U32((uint)(offset >> 32)), U32((uint)offset), U32((uint)length), [0, 0]);
        byte[] Build(long colourAt, long codestreamAt) =>
        [
            .. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]),
            .. Box("ftyp", Encoding.ASCII.GetBytes("jpx "), U32(0), Encoding.ASCII.GetBytes("jpx jpxb")),
            .. Box("xml ", Encoding.ASCII.GetBytes("<x>geheim</x>")),
            .. Box("jp2h", Enumerated(18)),
            .. Box("xml ", [.. Encoding.ASCII.GetBytes("<y/>"), .. colour]),
            .. Box("jplh", Box("cgrp", Box("cref", Encoding.ASCII.GetBytes("colr"), Flst(colourAt, colour.Length)))),
            .. Box("mdat", cs),
            .. Box("ftbl", Flst(codestreamAt, cs.Length)),
        ];
        var draft = Build(0, 0);
        var file = Build(draft.AsSpan().IndexOf("<y/>"u8) + 4, draft.AsSpan().IndexOf("mdat"u8) + 4);
        Assert.Equal(Plain(cs), JpxDecoder.Decode(file, long.MaxValue).Samples);

        var cleaned = PdfKnead.Core.Pdf.Writing.ObjectMetadata.Jpx(file);
        Assert.NotNull(cleaned);
        Assert.Equal(-1, cleaned.AsSpan().IndexOf("geheim"u8));
        Assert.NotEqual(-1, cleaned.AsSpan().IndexOf("<y/>"u8));
        Assert.Equal(Plain(cs), JpxDecoder.Decode(cleaned, long.MaxValue).Samples);
    }

    // ---- No. 737: fragmented codestream

    /// <summary>
    /// A Cross-Reference box in the first compositing layer (ISO/IEC 15444-2, M.11.4, M.9.2.6): it stands for the box its fragments make up —
    /// here a colour group with sRGB, kept in a Media Data box before the codestream, which replaces the sYCC of the JP2 Header box. Before the
    /// box was not read and the colours came out as sYCC (docs/FEHLERGEDAECHTNIS.md, No. 949). A fragment in another file and a cross-reference
    /// to a cross-reference are not followed: the JP2 Header box counts.
    /// </summary>
    [TestPdfsFact]
    public void CrossReference_StandsForTheBoxItsFragmentsMakeUp()
    {
        var cs = Rgb.Value;
        var srgb = Enumerated(16);
        byte[] Build(string referenced, ushort reference)
        {
            // the same length whatever the offset: built once to find where the media data lie, then with that offset
            byte[] With(long offset) => Jpx(cs, [Enumerated(18)], Box("mdat", srgb),
                Box("jplh", Box("cref", Encoding.ASCII.GetBytes(referenced),
                    Box("flst", [0, 1], U32((uint)(offset >> 32)), U32((uint)offset), U32((uint)srgb.Length), [(byte)(reference >> 8), (byte)reference]))));
            return With(With(0).AsSpan().IndexOf("mdat"u8) + 4);
        }
        var plain = Plain(cs);
        var ycc = JpxDecoder.Decode(Jpx(cs, [Enumerated(18)]), long.MaxValue).Samples;
        Assert.NotEqual(plain, ycc);
        Assert.Equal(plain, JpxDecoder.Decode(Build("cgrp", 0), long.MaxValue).Samples);
        Assert.Equal(ycc, JpxDecoder.Decode(Build("cgrp", 1), long.MaxValue).Samples);
        Assert.Equal(ycc, JpxDecoder.Decode(Build("cref", 0), long.MaxValue).Samples);
    }

    /// <summary>
    /// A Cross-Reference box inside the colour group of the first compositing layer (ISO/IEC 15444-2, M.5.2.2, M.11.4): it stands for a
    /// Colour Specification box — here sRGB, its contents in a Media Data box — which replaces the sYCC of the JP2 Header box. The conformance
    /// file reboxing.jpx of T.803 (V3, Annex H.3) has one. Before only direct Colour Specification boxes of the group were read
    /// (docs/FEHLERGEDAECHTNIS.md, No. 975).
    /// </summary>
    [TestPdfsFact]
    public void CrossReference_InsideTheColourGroup()
    {
        var cs = Rgb.Value;
        var contents = Enumerated(16)[8..];
        byte[] With(long offset) => Jpx(cs, [Enumerated(18)], Box("mdat", contents),
            Box("jplh", Box("cgrp", Box("cref", Encoding.ASCII.GetBytes("colr"),
                Box("flst", [0, 1], U32((uint)(offset >> 32)), U32((uint)offset), U32((uint)contents.Length), [0, 0])))));
        var file = With(With(0).AsSpan().IndexOf("mdat"u8) + 4);
        Assert.Equal(Plain(cs), JpxDecoder.Decode(file, long.MaxValue).Samples);
    }

    /// <summary>
    /// A Cross-Reference box inside a colour group that itself comes from a Cross-Reference box (M.11.4): the offsets of its fragments count
    /// from the start of the file (M.11.3). Before they were read in the copied bytes of the group, the sRGB was not found and the sYCC of the
    /// JP2 Header box counted (probe of T.801, 23.11.2026, B15).
    /// </summary>
    [TestPdfsFact]
    public void CrossReference_InsideAGroupFromACrossReference_OffsetsInTheFile()
    {
        var cs = Rgb.Value;
        var contents = Enumerated(16)[8..];
        byte[] Flst(long offset, int length) => Box("flst", [0, 1], U32((uint)(offset >> 32)), U32((uint)offset), U32((uint)length), [0, 0]);
        byte[] Inner(long offset) => Box("cref", Encoding.ASCII.GetBytes("colr"), Flst(offset, contents.Length));
        byte[] With(long colour, long group) => Jpx(cs, [Enumerated(18)], Box("mdat", contents), Box("mdat", Inner(colour)),
            Box("jplh", Box("cref", Encoding.ASCII.GetBytes("cgrp"), Flst(group, Inner(0).Length))));
        var draft = With(0, 0);
        var first = draft.AsSpan().IndexOf("mdat"u8) + 4;
        var second = first + draft.AsSpan(first).IndexOf("mdat"u8) + 4;
        Assert.Equal(Plain(cs), JpxDecoder.Decode(With(first, second), long.MaxValue).Samples);
    }

    /// <summary>
    /// A crafted colour group with 2 000 Cross-Reference boxes over the same range of 400 KB (M.11.4; PLAN §6 point 8, probe of T.801,
    /// 23.11.2026, B15): all boxes resolved in a file together are never larger than it. Before each was copied — 800 MB from a file of
    /// half a megabyte. The Colour Specification box of the group still counts.
    /// </summary>
    [TestPdfsFact]
    public void Crafted_ManyCrossReferencesOverOneRange_BoundedByTheFile()
    {
        var cs = Rgb.Value;
        var filler = new byte[400_000];
        byte[] With(long offset)
        {
            var cref = Box("cref", Encoding.ASCII.GetBytes("free"),
                Box("flst", [0, 1], U32((uint)(offset >> 32)), U32((uint)offset), U32((uint)filler.Length), [0, 0]));
            return Jpx(cs, [Enumerated(18)], Box("mdat", filler), Box("jplh", Box("cgrp", [Enumerated(16), .. Enumerable.Repeat(cref, 2000)])));
        }
        var file = With(With(0).AsSpan().IndexOf("mdat"u8) + 4);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var samples = JpxDecoder.Decode(file, long.MaxValue).Samples;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(Plain(cs), samples);
        Assert.True(allocated < 100_000_000, $"{allocated >> 20} MB allocated");
    }

    /// <summary>
    /// A codestream in fragments (ISO/IEC 15444-2, M.11.3, M.9.2.5): put together in the order of the Fragment List box. Before there was no
    /// codestream to be found. A fragment in another file (data reference not 0) or outside the data: no codestream, an error, no crash.
    /// </summary>
    [TestPdfsFact]
    public void FragmentTable_CodestreamPutTogether()
    {
        var cs = Rgb.Value;
        var half = cs.Length / 2;
        byte[] Build(ushort reference, long shift)
        {
            byte[] head =
            [
                .. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]),
                .. Box("ftyp", Encoding.ASCII.GetBytes("jpx "), U32(0), Encoding.ASCII.GetBytes("jpx jpxb")),
                .. Box("jp2h", Enumerated(16)),
                .. Box("free", new byte[5000]),   // the fragments beyond where a search for the codestream would look
            ];
            var first = head.Length + 8;                       // contents of the first media box
            var second = first + half + 8;                     // contents of the second
            byte[] Entry(long offset, int length) =>
                [.. U32((uint)(offset >> 32)), .. U32((uint)offset), .. U32((uint)length), (byte)(reference >> 8), (byte)reference];
            return
            [
                .. head,
                .. Box("mdat", cs[..half]),
                .. Box("mdat", cs[half..]),
                .. Box("ftbl", Box("flst", [0, 2], Entry(first + shift, half), Entry(second + shift, cs.Length - half))),
            ];
        }
        var image = JpxDecoder.Decode(Build(0, 0), long.MaxValue);
        Assert.False(image.Truncated);
        Assert.Equal(Plain(cs), image.Samples);
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(Build(1, 0), long.MaxValue));
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(Build(0, 1L << 40), long.MaxValue));
        // the first codestream cannot be put together: a later codestream does not take its place (M.9.2.2 sentence 5; before it was shown,
        // docs/FEHLERGEDAECHTNIS.md, No. 985)
        Assert.Throws<JpxException>(() => JpxDecoder.Decode([.. Build(1, 0), .. Box("jp2c", cs)], long.MaxValue));
    }

    // ---- ITU-T T.800 I.5.3.4 to I.5.4 one by one (rows of I.5 in docs/normabgleich/M2-filter.md, split on 20.11.2026)

    /// <summary>
    /// I.5.3.6: the Channel Definition box says which channel holds which colour — here the channels in the order blue, green, red, and with
    /// an opacity channel. Readers use it to find the colours (Satz 115), not the order of the components.
    /// </summary>
    [TestPdfsFact]
    public void ChannelDefinition_ColoursByAssociation_OpacitySeparate()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        var count = plain.Length / 3;
        // N = 3; per channel: Cn, Typ (0 colour, 1 opacity), Asoc (colour number, 0 = whole image)
        var reversed = JpxDecoder.Decode(Jpx(cs, [Enumerated(16), Box("cdef", [0, 3, 0, 0, 0, 0, 0, 3, 0, 1, 0, 0, 0, 2, 0, 2, 0, 0, 0, 1])]), long.MaxValue);
        Assert.Equal(3, reversed.Channels);
        Assert.Equal([.. Enumerable.Range(0, count).SelectMany(i => new[] { plain[i * 3 + 2], plain[i * 3 + 1], plain[i * 3] })], reversed.Samples);
        var opacity = JpxDecoder.Decode(Jpx(cs, [Box("cdef", [0, 3, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 2, 0, 2, 0, 1, 0, 0])]), long.MaxValue);
        Assert.Equal(2, opacity.Channels);
        Assert.False(opacity.PremultipliedAlpha);
        Assert.Equal([.. Enumerable.Range(0, count).Select(i => plain[i * 3 + 2])], Assert.IsType<byte[]>(opacity.Alpha));
        Assert.Equal([.. Enumerable.Range(0, count).SelectMany(i => new[] { plain[i * 3], plain[i * 3 + 1] })], opacity.Samples);
    }

    /// <summary>
    /// I.5.3.4, I.5.3.5: a palette with its component mapping turns the one index component into three channels — every pixel of the grey
    /// image (all 0) takes the colour of entry 0; entries of 10 bits stand in the low bits of 16 (Satz 27, 28) and are stretched to 8 bits.
    /// </summary>
    [TestPdfsFact]
    public void Palette_WithComponentMapping_IndexBecomesColour()
    {
        var cs = Gray.Value;
        // NE = 2, NPC = 3, B = 8, 8, 10 bits; entry 0: 200, 100, 1023; entry 1: 0, 0, 0
        byte[] palette = [0, 2, 3, 7, 7, 9, 200, 100, 0x03, 0xFF, 0, 0, 0, 0];
        // per channel: CMP (component 0), MTYP 1 (palette), PCOL (column)
        byte[] mapping = [0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 1, 2];
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(16), Box("pclr", palette), Box("cmap", mapping)]), long.MaxValue);
        Assert.Equal(3, image.Channels);
        Assert.Equal(128 * 64 * 3, image.Samples.Length);
        for (var i = 0; i < image.Samples.Length; i += 3) Assert.Equal(new byte[] { 200, 100, 255 }, image.Samples[i..(i + 3)]);
    }

    /// <summary>I.5.4: a reader shows only the first codestream of the file; a second Contiguous Codestream box is passed over.</summary>
    [TestPdfsFact]
    public void ContiguousCodestream_OnlyTheFirstCounts()
    {
        // the helper puts the codestream last: the box in "rest" comes first
        var first = JpxDecoder.Decode(Jpx(Rgb.Value, [], Box("jp2c", Gray.Value)), long.MaxValue);
        Assert.Equal(1, first.Channels);
        Assert.Equal(Plain(Gray.Value), first.Samples);
    }

    /// <summary>
    /// ITU-T T.801 M.5.2.1 with M.9.2: the Codestream Header box of the first codestream adds its boxes to those of the JP2 Header box — here
    /// the palette and the component mapping, while the colour specification stands in the JP2 Header box. Before, the box was passed over
    /// and the image shown as grey indices (docs/FEHLERGEDAECHTNIS.md, No. 968).
    /// </summary>
    [TestPdfsFact]
    public void CodestreamHeader_OfTheFirstCodestream_AddsThePalette()
    {
        var cs = Gray.Value;
        byte[] palette = [0, 1, 3, 7, 7, 7, 10, 20, 30];          // NE = 1, NPC = 3, 8 bits each, entry 0: 10, 20, 30
        byte[] mapping = [0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 1, 2];
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(16)], Box("jpch", Box("pclr", palette), Box("cmap", mapping))), long.MaxValue);
        Assert.Equal(3, image.Channels);
        for (var i = 0; i < image.Samples.Length; i += 3) Assert.Equal(new byte[] { 10, 20, 30 }, image.Samples[i..(i + 3)]);
    }

    /// <summary>
    /// ITU-T T.801 M.11.7.8, Table M.35bis: a Pixel Format box makes a channel's bit patterns fixed or floating point numbers; 1.0 is the full
    /// intensity (M.2.8), values outside are clamped. Expected values from the formulas of the table, worked out here apart from the decoder:
    /// fixed point with f = 7 (the 8-bit integer divided by 128), a floating point number of 8 bits (sign, four exponent bits with bias 7,
    /// three mantissa bits — subnormal, normal, infinity and not-a-number all occur in 0 to 255); in the JP2 Header box as in the first
    /// Compositing Layer Header box. Format 0 keeps the image; a reserved format and a floating point format without exponent bits are
    /// refused (docs/FEHLERGEDAECHTNIS.md, No. 969; before: every format other than integers refused).
    /// </summary>
    [TestPdfsFact]
    public void PixelFormat_FixedAndFloatingPoint_ReadAsTheirNumbers()
    {
        var cs = Gray.Value;
        var info = JpxCodestream.Parse(cs, 0, cs.Length);
        Assert.Equal((1, 8, false), (info.Output.Length, info.Output[0].Precision, info.Output[0].Signed));
        static byte[] Format(int f) => [0, 1, 0, 0, (byte)(f >> 8), (byte)f];   // N = 1: channel 0, format f
        static byte Level(double v) => double.IsNaN(v) || v <= 0 ? (byte)0 : v >= 1 ? (byte)255 : (byte)Math.Round(v * 255);
        static double Minifloat(int b)
        {
            int sign = b >> 7, exponent = b >> 3 & 15, mantissa = b & 7;
            var v = exponent == 15 ? (mantissa == 0 ? double.PositiveInfinity : double.NaN)
                : exponent == 0 ? mantissa / 8.0 * Math.Pow(2, -6)
                : (1 + mantissa / 8.0) * Math.Pow(2, exponent - 7);
            return sign == 1 ? -v : v;
        }
        var plain = Plain(cs);
        Assert.Equal(plain, JpxDecoder.Decode(Jpx(cs, [Box("pxfm", Format(0))]), long.MaxValue).Samples);
        Assert.Equal(plain.Select(v => Level(v / 128.0)).ToArray(), JpxDecoder.Decode(Jpx(cs, [Box("pxfm", Format(0x3007))]), long.MaxValue).Samples);
        Assert.Equal(plain.Select(v => Level(v / 128.0)).ToArray(),
            JpxDecoder.Decode(Jpx(cs, [], Box("jplh", Box("pxfm", Format(0x3007)))), long.MaxValue).Samples);
        Assert.Equal(plain.Select(v => Level(Minifloat(v))).ToArray(), JpxDecoder.Decode(Jpx(cs, [Box("pxfm", Format(0x4003))]), long.MaxValue).Samples);
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(Jpx(cs, [Box("pxfm", Format(0x5000))]), long.MaxValue));   // reserved
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(Jpx(cs, [Box("pxfm", Format(0x4007))]), long.MaxValue));   // no exponent bit left
    }

    /// <summary>
    /// ITU-T T.800 I.5.3.4: palette entries have Bi bits. A faulty file (the JP2 image of a file of the public test collection, issue12213) declares 4 bits and
    /// stores whole bytes up to 0xFF — the same bytes as the /Indexed palette of that PDF, so the colours meant: PDF-Knead now reads the stored
    /// bytes; before a value above 15 was stretched as if it had 4 bits and ran over into wrong colours (found with the colour comparison of
    /// lesetoleranz.py, A11bref, 20.11.2026; the three reference readers show yet other colours). Entries within the 4 bits stay stretched
    /// (S-A A113).
    /// </summary>
    [TestPdfsFact]
    public void Palette_EntriesAboveTheirDepth_ReadAsStored()
    {
        var cs = Gray.Value;
        var plain = Plain(cs);
        byte[] mapping = [0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 1, 2];
        byte[] faulty = [1, 0, 3, 3, 3, 3, .. Enumerable.Range(0, 256).SelectMany(i => (byte[])[(byte)i, (byte)(255 - i), 0xBF])];
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(16), Box("pclr", faulty), Box("cmap", mapping)]), long.MaxValue);
        Assert.Equal(plain.SelectMany(v => (byte[])[v, (byte)(255 - v), 0xBF]).ToArray(), image.Samples);
        byte[] valid = [1, 0, 3, 3, 3, 3, .. Enumerable.Range(0, 256).SelectMany(i => (byte[])[(byte)(i % 16), (byte)(15 - i % 16), 15])];
        image = JpxDecoder.Decode(Jpx(cs, [Enumerated(16), Box("pclr", valid), Box("cmap", mapping)]), long.MaxValue);
        Assert.Equal(plain.SelectMany(v => (byte[])[(byte)(v % 16 * 17), (byte)((15 - v % 16) * 17), 255]).ToArray(), image.Samples);
    }

    /// <summary>
    /// ITU-T T.801 M.11.7.8, Table M.35bis: a palette column with a floating point format — 16-bit signed entries read as half precision
    /// numbers (checked against .NET's own <see cref="Half"/>), the index i giving i/255 and every fourth entry negative (clamped to 0).
    /// </summary>
    [TestPdfsFact]
    public void PixelFormat_PaletteColumnAsHalfFloat()
    {
        var cs = Gray.Value;
        var values = Enumerable.Range(0, 256).Select(i => (Half)(i % 4 == 3 ? -i / 255.0 : i / 255.0)).ToArray();
        byte[] palette = [0, 1, 1, 0x8F, .. values.SelectMany(h => U16(BitConverter.HalfToUInt16Bits(h)))];
        byte[] mapping = [0, 0, 1, 0];
        byte[] format = [0, 1, 0, 0, 0x40, 0x0A];
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(17), Box("pclr", palette), Box("cmap", mapping), Box("pxfm", format)]), long.MaxValue);
        var expected = Plain(cs).Select(v => (byte)(Math.Round(Math.Clamp((double)values[v], 0, 1) * 65535) / 256)).ToArray();
        Assert.Equal(expected, image.Samples);
    }

    /// <summary>
    /// ITU-T T.801 M.11.7.8, Table M.35bis: a number split over a mantissa channel and an exponent channel, V = mantissa · 2^(exponent − 136);
    /// the Channel Definition box associates them (the exponent with the whole image), the exponent channel is no colour; a third channel
    /// stays opacity. Without a Channel Definition box the pair cannot be formed and the image is refused.
    /// </summary>
    [TestPdfsFact]
    public void PixelFormat_MantissaAndExponent_Combined()
    {
        var cs = Rgb.Value;
        var info = JpxCodestream.Parse(cs, 0, cs.Length);
        Assert.True(info.Output.Take(3).All(c => c.Precision == 8 && !c.Signed));
        byte[] definitions = [0, 3, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 2, 0, 1, 0, 0];   // 0 colour of colour 1, 1 exponent (whole image), 2 opacity
        byte[] formats = [0, 3, 0, 0, 0x10, 0, 0, 1, 0x20, 0, 0, 2, 0, 0];
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(17), Box("cdef", definitions), Box("pxfm", formats)]), long.MaxValue);
        var plain = Plain(cs);
        var n = plain.Length / 3;
        var expected = new byte[n];
        var alpha = new byte[n];
        for (var i = 0; i < n; i++)
        {
            var v = plain[3 * i] * Math.Pow(2, plain[3 * i + 1] - 136);
            expected[i] = (byte)(Math.Round(Math.Clamp(v, 0, 1) * 65535) / 256);
            alpha[i] = plain[3 * i + 2];
        }
        Assert.Equal(1, image.Channels);
        Assert.Equal(expected, image.Samples);
        Assert.Equal(alpha, image.Alpha);
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(Jpx(cs, [Enumerated(17), Box("pxfm", formats)]), long.MaxValue));
    }

    /// <summary>
    /// ITU-T T.801 M.11.25: the boxes of a Grouping box count as if they stood at its level — the Codestream Header box of the first codestream
    /// inside a Grouping box at the top level still gives the palette (docs/FEHLERGEDAECHTNIS.md, No. 968).
    /// </summary>
    [TestPdfsFact]
    public void GroupingBox_ItsBoxesCountAtItsLevel()
    {
        var cs = Gray.Value;
        byte[] palette = [0, 1, 3, 7, 7, 7, 10, 20, 30];
        byte[] mapping = [0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 1, 2];
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(16)], Box("grp ", Box("jpch", Box("pclr", palette), Box("cmap", mapping)))), long.MaxValue);
        Assert.Equal(3, image.Channels);
        Assert.Equal(new byte[] { 10, 20, 30 }, image.Samples[..3]);
    }

    /// <summary>
    /// ITU-T T.800 I.5.1, I.5.2: a JP2/JPX file begins with the signature box and the file type box. Without either the three reference readers
    /// show no image (measurement of 20.11.2026, lesetoleranzen_m2c.py, A112a and A112b); PDF-Knead read the boxes anyway — now no image as
    /// well. The full file reads as before (S-A A112; docs/FEHLERGEDAECHTNIS.md, No. 970).
    /// </summary>
    [TestPdfsFact]
    public void WithoutSignatureOrFileType_NoImage_AsTheReadersHaveIt()
    {
        var cs = Gray.Value;
        var full = Jpx(cs, [Enumerated(17)]);
        Assert.Equal(Plain(cs), JpxDecoder.Decode(full, long.MaxValue).Samples);
        var fileType = BinaryPrimitives.ReadInt32BigEndian(full.AsSpan(12));
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(full[12..], long.MaxValue));
        Assert.Throws<JpxException>(() => JpxDecoder.Decode([.. full[..12], .. full[(12 + fileType)..]], long.MaxValue));
    }

    // ---- rows of T.800 Annex I and T.801 Annex M, A.3, J, K split on 20.11.2026 (the check for collective rows): each rule its own test

    private static bool Refused(byte[] data)
    {
        try
        {
            return JpxDecoder.Decode(data, long.MaxValue).Truncated;
        }
        catch (JpxException)
        {
            return true;
        }
    }

    /// <summary>A box with the extended length (LBox 1, XLBox the whole length) and a last box with length 0 (up to the end).</summary>
    private static byte[] WideBox(string type, byte[] content) =>
        [0, 0, 0, 1, .. Encoding.ASCII.GetBytes(type), .. U32(0), .. U32((uint)(16 + content.Length)), .. content];

    /// <summary>
    /// T.800 I.4: a box may give its length as LBox 1 with an 8-byte XLBox, and the last box of the file may give 0 — it reaches to the end
    /// (Bericht M2, Zeile 500). I.5.2: a file type box with a minor version other than 0 is read all the same (Zeile 501).
    /// </summary>
    [TestPdfsFact]
    public void Boxes_ExtendedLengthLastBoxZero_MinorVersion()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        byte[] Head(uint minor) =>
        [
            .. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]),
            .. Box("ftyp", Encoding.ASCII.GetBytes("jp2 "), U32(minor), Encoding.ASCII.GetBytes("jp2 ")),
            .. Box("jp2h", Enumerated(16)),
        ];
        Assert.Equal(plain, JpxDecoder.Decode([.. Head(0), .. WideBox("jp2c", cs)], long.MaxValue).Samples);
        Assert.Equal(plain, JpxDecoder.Decode([.. Head(0), 0, 0, 0, 0, .. "jp2c"u8, .. cs], long.MaxValue).Samples);
        Assert.Equal(plain, JpxDecoder.Decode([.. Head(7), .. Box("jp2c", cs)], long.MaxValue).Samples);
    }

    /// <summary>
    /// Boxes that carry no image data are passed over and leave the image as it is, at the top level and inside the JP2 Header box: the
    /// bits per component box (T.800 I.5.3.2, Zeile 558), intellectual property, UUID and UUID info with its list and URL (I.6, I.7; Zeilen
    /// 259–259c), XML and MPEG-7 (T.801 M.11.18, M.11.19; Zeilen 512, 320), an association with a number list and a label, also in the
    /// codestream header of the first codestream (M.11.11–M.11.13; Zeile 315).
    /// </summary>
    [TestPdfsFact]
    public void BoxesWithoutImageData_PassedOver()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        var uuid = new byte[16];
        uuid[0] = 0x42;
        byte[][] others =
        [
            Box("jp2i", "<rights/>"u8.ToArray()),
            Box("uuid", uuid, "vendor"u8.ToArray()),
            Box("uinf", Box("ulst", [0, 1], uuid), Box("url ", [0, 0, 0, 0], "https://example.org/"u8.ToArray(), [0])),
            Box("xml ", "<a/>"u8.ToArray()),
            Box("mp7b", "<Mpeg7/>"u8.ToArray()),
            Box("asoc", Box("nlst", U32(0x01000000)), Box("lbl ", "Bild"u8.ToArray())),
        ];
        Assert.Equal(plain, JpxDecoder.Decode(Jpx(cs, [Enumerated(16), Box("bpcc", [7, 7, 7]), .. others], others), long.MaxValue).Samples);
        Assert.Equal(plain, JpxDecoder.Decode(Jpx(cs, [Enumerated(16)], Box("jpch", Box("asoc", Box("nlst", U32(0x01000000)), Box("lbl ", "x"u8.ToArray()))))
            , long.MaxValue).Samples);
    }

    /// <summary>
    /// T.801 M.11.7.2, M.9.2.4: a colour specification by the vendor method (4) is not understood and passed over, also with the higher
    /// precedence — the one by any ICC profile (method 3) counts (Zeile 568).
    /// </summary>
    [TestPdfsFact]
    public void ColourSpecification_VendorPassedOver_AnyIccTaken()
    {
        var cs = Rgb.Value;
        var vendor = Box("colr", [4, 5, 1, .. new byte[16], 1, 2, 3]);
        var any = Box("colr", [3, 0, 0], CalibratedProfile.Srgb());
        var image = JpxDecoder.Decode(Jpx(cs, [vendor, any]), long.MaxValue);
        Assert.Equal(JpxColor.Icc, image.Color);
        Assert.NotNull(image.IccProfile);
    }

    /// <summary>
    /// T.801 M.11.7.5, T.800 I.5.3.6: the channel definition may name a palette column as opacity — the opacity comes through the palette like
    /// the colour (column 1: 255 − index), the colour from column 0 (Zeile 524).
    /// </summary>
    [TestPdfsFact]
    public void OpacityFromAPaletteColumn()
    {
        var cs = Gray.Value;
        var plain = Plain(cs);
        byte[] palette = [1, 0, 2, 7, 7, .. Enumerable.Range(0, 256).SelectMany(i => (byte[])[(byte)i, (byte)(255 - i)])];
        byte[] mapping = [0, 0, 1, 0, 0, 0, 1, 1];
        byte[] definitions = [0, 2, 0, 0, 0, 0, 0, 1, 0, 1, 0, 1, 0, 0];
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(17), Box("pclr", palette), Box("cmap", mapping), Box("cdef", definitions)]), long.MaxValue);
        Assert.Equal(1, image.Channels);
        Assert.Equal(plain, image.Samples);
        Assert.Equal(plain.Select(v => (byte)(255 - v)).ToArray(), image.Alpha);
    }

    /// <summary>
    /// T.801 K.1.1, M.11.7.2: the non-linearity comes before the colour conversion — a gamma NLT with the colour space sYCC gives sYCC → RGB of
    /// the values after the curve (Zeile 517).
    /// </summary>
    [TestPdfsFact]
    public void NonLinearity_BeforeTheColourConversion()
    {
        var gamma = Marker(0xFF76, U16(65535), [0x07], [1], Fixed(0.45), Fixed(4.5), Fixed(0.081), Fixed(1.099), Fixed(0.099));
        var cs = Part2(Rgb.Value, 0x8200, null, gamma);
        var curved = Plain(cs);
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(18)]), long.MaxValue);
        NearSycc(curved, image, 2);
    }

    /// <summary>
    /// T.801 J.2, J.3: a multiple component transformation does not work on subsampled components — refused (Zeile 273); an output index no
    /// collection fills is a component of zeros (Zeile 536); transform and offset index 0 mean the identity and no offset (Zeile 274); the
    /// dependency transformation (Xmcc 0) is not implemented and refused (Zeile 538).
    /// </summary>
    [TestPdfsFact]
    public void Part2_CollectionsSubsamplingOutputsIdentityDependency()
    {
        var cs = Rgb.Value;
        var parts = SwappedRct();
        var subsampled = Part2(cs, 0x8100, 2, parts);
        subsampled[42 + 3 + 1] = 2;   // XRsiz of component 1
        Assert.True(Refused(subsampled));

        var identity = Marker(0xFF74, U16(0), U16(0x0901), U16(0), F32(1, 0, 0, 0, 1, 0, 0, 0, 1));
        var offsets = Marker(0xFF74, U16(0), U16(0x0A01), U16(0), F32(128, 128, 128));
        var three = Marker(0xFF75, U16(0), [0], U16(0), U16(1), [0x01], U16(3), [0, 1, 2], U16(3), [0, 1, 2], [0, 0x01, 0x01]);
        var full = JpxDecoder.Decode(Part2(cs, 0x8100, 2, parts[0], identity, offsets, three, parts[4]), long.MaxValue).Samples;
        var identity2 = Marker(0xFF74, U16(0), U16(0x0902), U16(0), F32(1, 0, 0, 0, 1, 0));
        var offsets2 = Marker(0xFF74, U16(0), U16(0x0A02), U16(0), F32(128, 128));
        var two = Marker(0xFF75, U16(0), [0], U16(0), U16(1), [0x01], U16(3), [0, 1, 2], U16(2), [0, 1], [0, 0x02, 0x02]);
        var gap = JpxDecoder.Decode(Part2(cs, 0x8100, 2, parts[0], identity2, offsets2, two, parts[4]), long.MaxValue).Samples;
        for (var i = 0; i < full.Length; i += 3)
        {
            Assert.Equal((full[i], full[i + 1]), (gap[i], gap[i + 1]));
            Assert.Equal(0, gap[i + 2]);
        }

        var zeroOffsets = Marker(0xFF74, U16(0), U16(0x0A01), U16(0), F32(0, 0, 0));
        var explicitIdentity = JpxDecoder.Decode(Part2(cs, 0x8100, 2, parts[0], identity, zeroOffsets, three, parts[4]), long.MaxValue).Samples;
        var indexZero = Marker(0xFF75, U16(0), [0], U16(0), U16(1), [0x01], U16(3), [0, 1, 2], U16(3), [0, 1, 2], [0, 0x00, 0x00]);
        Assert.Equal(explicitIdentity, JpxDecoder.Decode(Part2(cs, 0x8100, 2, parts[0], indexZero, parts[4]), long.MaxValue).Samples);

        var dependency = Marker(0xFF75, U16(0), [0], U16(0), U16(1), [0x00], U16(3), [0, 1, 2], U16(3), [0, 1, 2], [0, 0x01, 0x01]);
        Assert.True(Refused(Part2(cs, 0x8100, 2, parts[0], identity, offsets, dependency, parts[4])));
    }

    /// <summary>
    /// T.801 A.3.7–A.3.9 with A.4: the MCT, MCC and MCO of the first tile-part header come before those of the main header — here an MCO without
    /// a stage in the tile: the tile is not transformed, as without any (Zeile 271). The same MCO in a second, later tile-part of the tile is passed
    /// over (J.2: a series wholly in the main or the first tile-part header; A.4: these segments only in the first): the tile is transformed as
    /// by the main header.
    /// </summary>
    [TestPdfsFact]
    public void Part2_TileHeaderBeforeMainHeader()
    {
        var cs = Rgb.Value;
        byte[][] none = [.. SwappedRct()[..4], Marker(0xFF77, [0])];
        var untransformed = JpxDecoder.Decode(Part2(cs, 0x8100, 2, none), long.MaxValue).Samples;
        var main = Part2(cs, 0x8100, 2, SwappedRct());
        var noStage = Marker(0xFF77, [0]);
        var sot = main.AsSpan().IndexOf([(byte)0xFF, (byte)0x90]);
        byte[] tile = [.. main[..(sot + 12)], .. noStage, .. main[(sot + 12)..]];
        BinaryPrimitives.WriteUInt32BigEndian(tile.AsSpan(sot + 6), BinaryPrimitives.ReadUInt32BigEndian(main.AsSpan(sot + 6)) + (uint)noStage.Length);
        Assert.Equal(untransformed, JpxDecoder.Decode(tile, long.MaxValue).Samples);

        // a second tile-part (TPsot 1) with only the MCO and an empty body, before EOC
        Assert.Equal(0xFFD9, BinaryPrimitives.ReadUInt16BigEndian(main.AsSpan(main.Length - 2)));
        byte[] secondPart = [0xFF, 0x90, 0, 10, 0, 0, .. U32((uint)(12 + noStage.Length + 2)), 1, 0, .. noStage, 0xFF, 0x93];
        byte[] later = [.. main[..^2], .. secondPart, 0xFF, 0xD9];
        var transformed = JpxDecoder.Decode(main, long.MaxValue).Samples;
        Assert.NotEqual(untransformed, transformed);
        Assert.Equal(transformed, JpxDecoder.Decode(later, long.MaxValue).Samples);
    }

    /// <summary>
    /// T.801 J.3.2, Table A.8: the wavelet-based multiple component transformation (4 in the field of SGcod) is refused also where only the COD
    /// of the tile-part header names it — before the tile was decoded the ordinary way, without a transformation and without a word (M2,
    /// 23.11.2026). The array-based one (2) there transforms the tile as the same value in the main header does.
    /// </summary>
    [TestPdfsFact]
    public void Part2_WaveletTransformInTheTilePartCod_Refused()
    {
        var cs = Rgb.Value;
        var main = Part2(cs, 0x8100, 0, SwappedRct());
        byte[] WithTileCod(int field)
        {
            var at = 2;
            while (BinaryPrimitives.ReadUInt16BigEndian(main.AsSpan(at)) != 0xFF52) at += 2 + BinaryPrimitives.ReadUInt16BigEndian(main.AsSpan(at + 2));
            var cod = main[at..(at + 2 + BinaryPrimitives.ReadUInt16BigEndian(main.AsSpan(at + 2)))];
            cod[8] = (byte)field;   // Scod, progression, layers, then the multiple component field
            var sot = main.AsSpan().IndexOf([(byte)0xFF, (byte)0x90]);
            byte[] file = [.. main[..(sot + 12)], .. cod, .. main[(sot + 12)..]];
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(sot + 6), BinaryPrimitives.ReadUInt32BigEndian(main.AsSpan(sot + 6)) + (uint)cod.Length);
            return file;
        }
        Assert.Contains("Wavelet", Assert.Throws<JpxException>(() => JpxDecoder.Decode(WithTileCod(4), long.MaxValue)).Message);
        Assert.Equal(JpxDecoder.Decode(Part2(cs, 0x8100, 2, SwappedRct()), long.MaxValue).Samples, JpxDecoder.Decode(WithTileCod(2), long.MaxValue).Samples);
    }

    /// <summary>
    /// T.801 K.2.2 (Equations K-6 to K-8): a LUT over Dmin 0.25 to Dmax 0.75 clips the input to that range before the table — below it the first
    /// value, above it the last (Zeile 540). K.2.3: the binary complement of a signed value maps −1 to −2^(b−1) and back, keeps the positive
    /// ones (Zeile 541).
    /// </summary>
    [Fact]
    public void NonLinearity_LutRangeAndBinaryComplement()
    {
        // a gamma curve over a signed component of one bit (found by fuzzing, FG 994): no positive range — finite values instead of NaN,
        // on which Math.Sign threw
        var gamma = new JpxNonLinearity(8, false, 1, [2.2, 1, 0.01, 1, 0], null, 0, 1);
        foreach (var y in new[] { -1.0, 0, 1 })
            Assert.True(double.IsFinite(JpxPart2.Apply(gamma, y, 1, true)));
        var lut = new JpxNonLinearity(8, false, 2, [], [0, 1], 0.25, 0.75);
        Assert.Equal(0, JpxPart2.Apply(lut, 0, 8, false), 6);
        Assert.Equal(0, JpxPart2.Apply(lut, 50, 8, false), 6);
        Assert.Equal(255, JpxPart2.Apply(lut, 255, 8, false), 6);
        Assert.Equal(127.5, JpxPart2.Apply(lut, 127.5, 8, false), 6);
        var complement = new JpxNonLinearity(8, true, 3, [], null, 0, 1);
        Assert.Equal(-128, JpxPart2.Apply(complement, -1, 8, true));
        Assert.Equal(-1, JpxPart2.Apply(complement, -128, 8, true));
        Assert.Equal(5, JpxPart2.Apply(complement, 5, 8, true));
    }

    /// <summary>
    /// PIMA 7667, 4.4.3 (cited by T.801, Table M.25): e-sRGB with n bits, R′ = (C − 3·2^(n−3)) / (255·2^(n−9)) — for 16 bits 24576 is 0 and
    /// 57216 is 255 (the grey values of the table there), for 12 bits 1536 and 3576. The 8-bit sRGB step of one pixel clips what lies outside;
    /// an image with such values is drawn in the wide working space instead (FG 1056, <see cref="ESrgb_ExtendedRange_InTheWideSpace"/>; row 278).
    /// </summary>
    [Fact]
    public void ESrgb_TheValuesOfThePimaTable()
    {
        Assert.Equal(0, ESrgbLevel(24576, 16));
        Assert.Equal(255, ESrgbLevel(57216, 16));
        Assert.Equal(128, ESrgbLevel(24576 + 128 * 128, 16));
        Assert.Equal(0, ESrgbLevel(1536, 12));
        Assert.Equal(255, ESrgbLevel(3576, 12));
        Assert.Equal(0, ESrgbLevel(0, 16));
        Assert.Equal(255, ESrgbLevel(65535, 16));
    }

    /// <summary>
    /// ISO 32000-2, 7.4.1, Table 6 (column „Parameters“: no for JPXDecode): a /DecodeParms beside JPXDecode, even one naming a predictor,
    /// changes nothing — the decoder takes none (docs/normabgleich/M2-filter.md, row 611).
    /// </summary>
    [TestPdfsFact]
    public void Table6_JpxDecode_TakesNoParameters()
    {
        var cs = Gray.Value;   // 128 × 64
        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        var plain = GrayDrawn(doc, Image(cs, "JPXDecode", 128, 64, null, null));
        var with = Image(cs, "JPXDecode", 128, 64, null, null);
        with.Dictionary.Set(PdfName.DecodeParms, TestPdf.Dict(("Predictor", PdfInteger.Of(12)), ("Columns", PdfInteger.Of(3))));
        Assert.Equal(plain, GrayDrawn(doc, with));
    }
}
