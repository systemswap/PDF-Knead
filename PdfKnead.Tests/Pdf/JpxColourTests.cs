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
public sealed class JpxColourTests
{
    private static string PdfJs(string name, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return Path.Combine(dir.FullName, "TestPDFs", "pdfjs", name);
        throw new DirectoryNotFoundException();
    }

    /// <summary>The bare codestream of the first JPX image of a file.</summary>
    private static byte[] Codestream(string file)
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
        // the same precedence and approximation (JP2: both 0): the first, here sYCC — converted to RGB
        var first = JpxDecoder.Decode(Jpx(cs, [Enumerated(18), Enumerated(16)]), long.MaxValue);
        Assert.Equal(JpxColor.Rgb, first.Color);
        Near(YccToRgb(plain), first.Samples);
    }

    /// <summary>
    /// An ICC profile that cannot be used gives way to the next specification (ISO 32000-2, 7.4.9): a damaged one, and one for another number of
    /// channels. Before it was taken anyway and the image drawn by the number of channels — here as RGB instead of from sYCC.
    /// </summary>
    [TestPdfsFact]
    public void UnusableIccProfile_TheNextCounts()
    {
        var cs = Rgb.Value;
        var expected = YccToRgb(Plain(cs));
        foreach (var profile in new[] { Enumerable.Repeat((byte)0x5A, 200).ToArray(), CalibratedProfile.Gray(null) })
        {
            var image = JpxDecoder.Decode(Jpx(cs, [Icc(profile, 1, 1), Enumerated(18, 0, 1)]), long.MaxValue);
            Assert.Equal(JpxColor.Rgb, image.Color);
            Assert.Null(image.IccProfile);
            Near(expected, image.Samples);
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
        Near(YccToRgb(plain), Drawn(null));
    }

    // ---- No. 735: the enumerated colour spaces of JPX baseline

    /// <summary>
    /// e-sYCC (EnumCS 24, PIMA 7667, Annex B): the colour differences stored at half their size, the way back with twice the coefficients of
    /// sYCC, shown as sRGB with what lies outside clipped. Before it was read as sYCC, its colours half as strong (docs/FEHLERGEDAECHTNIS.md,
    /// No. 944).
    /// </summary>
    [TestPdfsFact]
    public void ESycc_HalfTheChroma_TwiceTheCoefficients()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(24)]), long.MaxValue);
        Assert.Equal(JpxColor.Rgb, image.Color);
        Near(YccToRgb(plain, 2), image.Samples);
        var asSycc = YccToRgb(plain);
        Assert.Contains(Enumerable.Range(0, asSycc.Length), i => Math.Abs(asSycc[i] - image.Samples[i]) > 8);
    }

    /// <summary>
    /// e-sRGB (EnumCS 20): the extended non-linear range, shown as sRGB with what lies outside clipped. Before it was taken as sRGB.
    /// </summary>
    [TestPdfsFact]
    public void ESrgb_ExtendedRange_ShownClipped()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(20)]), long.MaxValue);
        Assert.Equal(JpxColor.Rgb, image.Color);
        // 8 bits: R′ = (C − 96) / 127.5, as 8 bits (C − 96) · 2
        Assert.Equal([.. plain.Select(v => (byte)Math.Clamp((v - 96) * 2, 0, 255))], image.Samples);
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
    private static byte[] Fixed(double v) => [(byte)Math.Floor(v), .. U16((int)Math.Round((v - Math.Floor(v)) * 65536))];

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
    /// Before the NLT segment was ignored.
    /// </summary>
    [TestPdfsFact]
    public void Part2_NonLinearity_GammaLutComplement()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);

        // gamma: inverse of y = 4.5·z below 0.018, otherwise 1.099·z^0.45 − 0.099
        var gamma = Marker(0xFF76, U16(65535), [0x07], [1], Fixed(0.45), Fixed(4.5), Fixed(0.018), Fixed(0.099), Fixed(1.099));
        var expected = plain.Select(v =>
        {
            var y = v / 255.0;
            var z = y <= 4.5 * 0.018 ? y / 4.5 : Math.Pow((y + 0.099) / 1.099, 1 / 0.45);
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
        var onlyBlue = Marker(0xFF76, U16(2), [0x07], [1], Fixed(0.45), Fixed(4.5), Fixed(0.018), Fixed(0.099), Fixed(1.099));
        var mixed = JpxDecoder.Decode(Part2(cs, 0x8200, null, onlyBlue), long.MaxValue).Samples;
        for (var i = 0; i < plain.Length; i++)
            Assert.InRange(mixed[i] - (i % 3 == 2 ? expected[i] : plain[i]), -1, 1);
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
    }
}
