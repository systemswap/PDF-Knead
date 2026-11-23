namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// The rules of ISO 19005-2, 6.2.8.3 on the inside of JPEG 2000 images: a JP2 file (strict readers take the number of channels and the bit
/// depth from its image header — a bare codestream has neither), one, three or four colour channels of one bit depth between 1 and 38, colour
/// specifications by method 1, 2 or 3, and of several exactly one marked as the one to use (APPROX 1).
/// </summary>
internal static class PdfAJpeg2000
{
    /// <summary>What breaks the rules, as a sentence for the user; null if nothing does or the data cannot be read.</summary>
    /// <param name="data">The JPEG 2000 data (a JP2 file or a bare codestream; its first megabyte is enough).</param>
    /// <returns>The first fault, or null.</returns>
    public static string? Fault(byte[] data) => Faults(data, imageColourSpace: false).FirstOrDefault().Text;

    /// <summary>
    /// The rules the data break (ISO 19005-2, 6.2.8.3), each with a sentence for the user. The colour specifications inside (several
    /// without exactly one marked, a method other than 1–3, the enumerated space CIEJab) do not count where the image dictionary gives the
    /// colour space itself (<paramref name="imageColourSpace"/>): the reader takes that one. A codestream without a JP2 wrapper counts under
    /// the channel rule (its number of channels is not given).
    /// </summary>
    /// <param name="data">The JPEG 2000 data (its first megabyte is enough).</param>
    /// <param name="imageColourSpace">The image dictionary has /ColorSpace.</param>
    /// <returns>The faults; empty if none or the data cannot be read.</returns>
    internal static List<(PdfARule Rule, string Text)> Faults(byte[] data, bool imageColourSpace)
    {
        var faults = new List<(PdfARule, string)>();
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0x4F)
        {
            faults.Add((PdfARules.JpxChannels, Tr.T("Ein JPEG-2000-Bild ist ohne JP2-Rahmen gespeichert (nur der Datenstrom).")));
            return faults;
        }
        var header = new Header();
        if (Boxes(data, 0, data.Length, 0, header) < 0 || header.Channels is not { } components) return faults;
        var colours = header.Colours;
        // the colour channels: an opacity channel of the Channel Definition box is none (docs/FEHLERGEDAECHTNIS.md, No. 996)
        var channels = header.ColourChannels ?? components;
        if (channels is not (1 or 3 or 4)) faults.Add((PdfARules.JpxChannels, Tr.T("Ein JPEG-2000-Bild hat {0} Farbkanäle (erlaubt sind 1, 3 oder 4).", channels)));
        // a bpcc box (different depths per channel) is not allowed, the one depth between 1 and 38
        if (header.Depths is not null || header.Depth is not (>= 1 and <= 38))
            faults.Add((PdfARules.JpxBitDepth, Tr.T("Die Farbkanäle eines JPEG-2000-Bilds haben verschiedene oder unzulässige Bittiefen.")));
        if (imageColourSpace) return faults;
        if (colours.Count > 1 && colours.Count(c => c.Approx == 1) != 1)
            faults.Add((PdfARules.JpxColourSpecs, Tr.T("Ein JPEG-2000-Bild hat mehrere Farbangaben, ohne genau eine als maßgeblich zu kennzeichnen.")));
        if (colours.Any(c => c.Method is < 1 or > 3)) faults.Add((PdfARules.JpxMethod, Tr.T("Ein JPEG-2000-Bild hat eine unzulässige Farbangabe.")));
        if (colours.Any(c => c.Method == 1 && c.Enumerated == 19))
            faults.Add((PdfARules.JpxJab, Tr.T("Ein JPEG-2000-Bild nutzt den Farbraum CIEJab.")));
        return faults;
    }

    /// <summary>
    /// The device colour space a JPEG 2000 image without /ColorSpace in its dictionary is drawn in for the rules on device colours (ISO 19005-2,
    /// 6.2.4.3): its colour comes from the data (ISO 32000-2, 8.9.5, /ColorSpace), and an enumerated colour specification — or none, as in a
    /// bare codestream — counts by the number of channels (1 grey, 3 RGB, 4 CMYK; with a palette its columns); an ICC profile inside (method 2
    /// or 3) is device-independent. The specification read is the one marked with APPROX 1 where there are several, else the first. Before
    /// such an image counted as no colour at all: a CMYK image in a file with an RGB output intent got no /DefaultCMYK, and the copy broke
    /// 6.2.4.3 (docs/FEHLERGEDAECHTNIS.md, No. 995).
    /// </summary>
    /// <param name="data">The JPEG 2000 data (its first megabyte is enough).</param>
    /// <returns>„DeviceGray“, „DeviceRGB“ or „DeviceCMYK“; null where the space is device-independent or the data cannot be read.</returns>
    internal static string? DeviceSpace(byte[] data)
    {
        static string? ByChannels(int channels) => channels switch { 1 => "DeviceGray", 3 => "DeviceRGB", 4 => "DeviceCMYK", _ => null };
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0x4F && data[2] == 0xFF && data[3] == 0x51)
            return data.Length >= 42 ? ByChannels(data[40] << 8 | data[41]) : null;
        var header = new Header();
        if (Boxes(data, 0, data.Length, 0, header) < 0 || header.Channels is not { } components) return null;
        var colours = header.Colours;
        var used = colours.Count(c => c.Approx == 1) == 1 ? colours.First(c => c.Approx == 1) : colours.FirstOrDefault();
        // an opacity channel of the Channel Definition box is no colour (No. 996)
        return colours.Count > 0 && used.Method is 2 or 3 ? null : ByChannels(header.ColourChannels ?? components);
    }

    /// <summary>
    /// A bare codestream in a JP2 file (ISO/IEC 15444-1, Annex I): signature, file type, header with the size, the number of components and
    /// their bit depth from SIZ and an enumerated colour space by the number of components (grey, sRGB, CMYK — what readers assume for a bare
    /// codestream), then the codestream unchanged. JP2 knows only sRGB, sRGB-grey and sYCC (Table I.10): four components become a JPX file
    /// (brand „jpx “, ISO/IEC 15444-2, M.2.1; minor version 1, M.8) with the Reader Requirements box right after the file type (M.11.1),
    /// naming every feature of the file beyond JP2 and a profile 0 codestream (Table M.14): the CMYK colour space (55) and the codestream as
    /// it is — Part 1 profile 1 (4) where it keeps that profile (<see cref="Profile"/>), unrestricted Part 1 (5, also the other Part 1
    /// profiles and a profile named by Rsiz but not kept), Part 2 (6); the colour specification with APPROX 1, as a JPX file must have
    /// (M.11.7.2: 0 is not allowed there). A JP2 file names a codestream that keeps profile 0 or 1 by „J2P0“ or „J2P1“ in its compatibility
    /// list (I.5.2). The colour space is taken from the number of components alone — the image dictionary does not reach the wrapper, and it
    /// writes no ICC profile —, so the image header says the colour space is not known (UnkC 1, I.5.3.1; docs/FEHLERGEDAECHTNIS.md,
    /// No. 1074). Signed components are not wrapped: no enumerated space of JP2 allows them (I.5.3.3) — before both were written as JP2
    /// (docs/FEHLERGEDAECHTNIS.md, No. 981; minor version, Reader Requirements and APPROX: No. 987). Null if it is no bare codestream, its
    /// components are not 1, 3 or 4, of different depths or signed, its Rsiz is reserved (T.800 Table A.10; No. 1075), or it uses the
    /// capabilities of ISO/IEC 15444-15. With <paramref name="opacity"/> the last component is the opacity channel /SMaskInData announces
    /// for a bare codestream (<see cref="Filters.Jpx.JpxDecoder.OpacityLast"/>): a JP2 file whose Channel Definition box (I.5.3.6) names it
    /// for the whole image — grey or sRGB for the others, so two or four components; others are not wrapped (docs/FEHLERGEDAECHTNIS.md,
    /// No. 996: before four became CMYK and two stayed bare).
    /// </summary>
    /// <param name="codestream">The JPEG 2000 data.</param>
    /// <param name="opacity">0: every component a colour; 1: the last is opacity; 2: the last is premultiplied opacity.</param>
    /// <returns>The JP2 file, or null.</returns>
    public static byte[]? Wrap(ReadOnlySpan<byte> codestream, int opacity = 0)
    {
        if (codestream.Length < 42 || codestream[0] != 0xFF || codestream[1] != 0x4F || codestream[2] != 0xFF || codestream[3] != 0x51) return null;
        static uint U32(ReadOnlySpan<byte> d, int i) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d[i..]);
        var (width, height) = (U32(codestream, 8) - U32(codestream, 16), U32(codestream, 12) - U32(codestream, 20));
        var components = codestream[40] << 8 | codestream[41];
        if (opacity is not (0 or 1 or 2) || (opacity == 0 ? components is not (1 or 3 or 4) : components is not (2 or 4)) ||
            codestream.Length < 42 + 3 * components) return null;
        var colours = opacity == 0 ? components : components - 1;
        var depth = codestream[42];
        // a bit depth of at most 38 (T.801, Table M.20: BPC 0 to 37) and an image area that is not negative — before a reserved depth was
        // copied into the image header and XOsiz above Xsiz ran the width over (M2, T.800 I.5.3.1, T.801 M.11.5.1, 23.11.2026)
        if ((depth & 0x80) != 0 || (depth & 0x7F) > 37 || U32(codestream, 16) > U32(codestream, 8) || U32(codestream, 20) > U32(codestream, 12)) return null;
        var rsiz = codestream[6] << 8 | codestream[7];
        if ((rsiz & 0xC000) == 0x4000) return null;   // ISO/IEC 15444-15 (HTJ2K): no feature of the JPX baseline
        // a reserved Rsiz (T.800 Table A.10): the codestream does not conform, and the PDF/A copy would carry it on — before it was framed
        // with feature 5 (docs/FEHLERGEDAECHTNIS.md, No. 1075)
        if (!Filters.Jpx.JpxCodestream.KnownRsiz(rsiz)) return null;
        for (var i = 1; i < components; i++)
            if (codestream[42 + 3 * i] != depth) return null;
        var profile = Profile(codestream);
        // the boxes before the codestream first (small), then one array of the final size: the codestream is copied once — before, it was
        // copied three times, 10 MB more for one image of 2 MB (docs/FEHLERGEDAECHTNIS.md, No. 997)
        var output = new MemoryStream(160);
        static void Box(Stream to, string type, ReadOnlySpan<byte> content)
        {
            var head = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(head, (uint)(8 + content.Length));
            System.Text.Encoding.ASCII.GetBytes(type, head.AsSpan(4));
            to.Write(head);
            to.Write(content);
        }
        Box(output, "jP  ", [0x0D, 0x0A, 0x87, 0x0A]);
        // JPX for CMYK, and for a codestream with the capabilities of ISO/IEC 15444-2: a JP2 reader need only read Part 1 codestreams
        // (T.801 M.6, M.6.1.3) — before a Part 2 codestream of one or three colours was framed as JP2 (decided on 23.11.2026: framed as JPX
        // with feature 6, so the image stays)
        var jpx = colours == 4 || (rsiz & 0x8000) != 0;
        if (jpx)
        {
            Box(output, "ftyp", "jpx \0\0\0\u0001jpx "u8);
            // mask length 1; each bit of the masks is one term, the terms are needed together and the features of one bit are alternatives
            // (ISO/IEC 15444-2, M.6.2.1): CMYK in bit 0x80, the codestream feature in bit 0x40, both masks 0xC0 — before both stood in 0x80, which
            // read „CMYK or the codestream“ (M2, T.801 M.6, 23.11.2026); no vendor features. The profile features only where the codestream
            // keeps the profile (Table M.14 names it by Table A.45): Rsiz 1 or 2 alone does not — before Rsiz decided (No. 1074)
            int? feature = (rsiz & 0x8000) != 0 ? 6 : profile switch { 0 => null, 1 => 4, _ => 5 };
            // one or three colours: only the codestream feature (6, Part 2), alone in bit 0x80
            byte[] features = colours != 4 ? [0, 6, 0x80]
                : feature is { } f ? [0, 55, 0x80, 0, (byte)f, 0x40] : [0, 55, 0x80];
            byte mask = colours == 4 && feature is not null ? (byte)0xC0 : (byte)0x80;
            Box(output, "rreq", [1, mask, mask, 0, (byte)(features.Length / 3), .. features, 0, 0]);
        }
        // T.800 I.5.2: „J2P0“ or „J2P1“ beside „jp2 “ where the codestream keeps profile 0 or 1 of Table A.45 (decided on 23.11.2026; No. 1074)
        else Box(output, "ftyp", profile switch { 0 => "jp2 \0\0\0\0jp2 J2P0"u8, 1 => "jp2 \0\0\0\0jp2 J2P1"u8, _ => "jp2 \0\0\0\0jp2 "u8 });
        var ihdr = new byte[14];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr, height);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(ihdr.AsSpan(8), (ushort)components);
        ihdr[10] = depth;   // bit depth − 1, the sign in the high bit, as in SIZ
        ihdr[11] = 7;       // compression: JPEG 2000
        ihdr[12] = 1;       // UnkC: the colour space is guessed from the number of components, not known (I.5.3.1; No. 1074)
        var colr = new byte[7];
        colr[0] = 1;        // method 1: enumerated
        if (jpx) colr[2] = 1;   // APPROX: accurate (JPX does not allow 0, M.11.7.2; JP2 asks for 0, I.5.3.3)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(3), colours switch { 1 => 17u, 3 => 16u, _ => 12u });
        var header = new MemoryStream();
        Box(header, "ihdr", ihdr);
        Box(header, "colr", colr);
        if (opacity != 0)
        {
            // Channel Definition (I.5.3.6): N, then per channel Cn, Typ (0 colour, 1 opacity, 2 premultiplied opacity), Asoc (the colour
            // number; 0: the whole image)
            var cdef = new byte[2 + 6 * components];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(cdef, (ushort)components);
            for (var i = 0; i < components; i++)
            {
                var entry = cdef.AsSpan(2 + 6 * i);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(entry, (ushort)i);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(entry[2..], (ushort)(i < colours ? 0 : opacity));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(entry[4..], (ushort)(i < colours ? i + 1 : 0));
            }
            Box(header, "cdef", cdef);
        }
        Box(output, "jp2h", header.ToArray());
        var before = (int)output.Length;
        if ((long)before + 8 + codestream.Length > Array.MaxLength) return null;
        var file = new byte[before + 8 + codestream.Length];
        output.GetBuffer().AsSpan(0, before).CopyTo(file);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(before), (uint)(8 + codestream.Length));
        "jp2c"u8.CopyTo(file.AsSpan(before + 4));
        codestream.CopyTo(file.AsSpan(before + 8));
        return file;
    }

    /// <summary>
    /// The profile of ITU-T T.800 Table A.45 a bare codestream keeps: 0 or 1 where Rsiz names it (1 or 2) and the codestream holds every
    /// restriction the table sets for that profile (<see cref="Filters.Jpx.JpxProfile"/>, shared with the decoder, which reports a codestream
    /// that breaks them). Null otherwise, and where the codestream cannot be walked from SIZ to EOC. Rsiz promises the restrictions without
    /// keeping them: a codestream named profile 0 or 1 by Rsiz alone gets neither „J2P0“/„J2P1“ nor the profile feature
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1074).
    /// </summary>
    /// <param name="cs">The codestream (from SOC to EOC).</param>
    /// <returns>0, 1 or null.</returns>
    internal static int? Profile(ReadOnlySpan<byte> cs) => Filters.Jpx.JpxProfile.Check(cs) switch
    {
        Filters.Jpx.JpxProfileResult.Profile0 => 0,
        Filters.Jpx.JpxProfileResult.Profile1 => 1,
        _ => null,
    };

    /// <summary>What the JP2 header says.</summary>
    private sealed class Header
    {
        public int? Channels;
        public int? Depth;
        public List<int>? Depths;
        public int? PaletteColumns;

        /// <summary>The channels the Channel Definition box names opacity or premultiplied opacity (I.5.3.6): no colour channels.</summary>
        public int Opacities;
        public List<(int Method, int Approx, uint Enumerated)> Colours { get; } = [];

        /// <summary>The colour channels: the components (or the columns of the palette) less the opacity channels.</summary>
        public int? ColourChannels => (PaletteColumns ?? Channels) - Opacities;
    }

    /// <summary>Walks the boxes; returns where the codestream begins (-1 if none is found).</summary>
    private static int Boxes(byte[] d, int pos, int end, int depth, Header header)
    {
        while (pos + 8 <= end)
        {
            long length = (uint)(d[pos] << 24 | d[pos + 1] << 16 | d[pos + 2] << 8 | d[pos + 3]);
            var type = (uint)(d[pos + 4] << 24 | d[pos + 5] << 16 | d[pos + 6] << 8 | d[pos + 7]);
            var size = 8;
            if (length == 1)
            {
                if (pos + 16 > end) return -1;
                length = (long)((ulong)(uint)(d[pos + 8] << 24 | d[pos + 9] << 16 | d[pos + 10] << 8 | d[pos + 11]) << 32 |
                                (uint)(d[pos + 12] << 24 | d[pos + 13] << 16 | d[pos + 14] << 8 | d[pos + 15]));
                size = 16;
            }
            else if (length == 0)
            {
                length = end - pos;
            }
            // compared without adding: an XLBox near 2^63 made pos + length run over, the position jumped back and the loop never ended (M2, T.800 I.4, 23.11.2026)
            if (length < size || length > end - pos) length = end - pos;
            var s = pos + size;
            var e = (int)(pos + length);
            switch (type)
            {
                case 0x6A703263:   // jp2c
                    return s;
                case 0x6A703268 when depth < 4:   // jp2h
                    if (Boxes(d, s, e, depth + 1, header) is var inner and >= 0) return inner;
                    break;
                case 0x69686472 when e - s >= 11:   // ihdr: height, width, components (2 bytes), bit depth (255: per component in bpcc)
                    header.Channels = d[s + 8] << 8 | d[s + 9];
                    if (d[s + 10] != 255) header.Depth = (d[s + 10] & 0x7F) + 1;
                    break;
                case 0x62706363:   // bpcc: one bit depth per component
                    header.Depths = [.. d.AsSpan(s, e - s).ToArray().Select(b => (b & 0x7F) + 1)];
                    break;
                case 0x636F6C72 when e - s >= 3:   // colr: method, precedence, approximation; method 1: the enumerated colour space
                    header.Colours.Add((d[s], d[s + 2], d[s] == 1 && e - s >= 7 ? (uint)(d[s + 3] << 24 | d[s + 4] << 16 | d[s + 5] << 8 | d[s + 6]) : 0));
                    break;
                case 0x70636C72 when e - s >= 3:   // pclr: entries (2 bytes), columns
                    header.PaletteColumns = d[s + 2];
                    break;
                case 0x63646566 when e - s >= 2:   // cdef: N (2 bytes), per channel Cn, Typ, Asoc (2 bytes each)
                    var definitions = Math.Min(d[s] << 8 | d[s + 1], (e - s - 2) / 6);
                    header.Opacities = Enumerable.Range(0, definitions).Count(i => (d[s + 2 + 6 * i + 2] << 8 | d[s + 2 + 6 * i + 3]) is 1 or 2);
                    break;
            }
            pos = e;
        }
        return -1;
    }
}
