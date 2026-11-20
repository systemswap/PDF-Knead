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
        var channels = header.PaletteColumns ?? components;
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
    /// A bare codestream in a JP2 file (ISO/IEC 15444-1, Annex I): signature, file type, header with the size, the number of components and
    /// their bit depth from SIZ and an enumerated colour space by the number of components (grey, sRGB, CMYK — what readers assume for a bare
    /// codestream), then the codestream unchanged. Null if it is no bare codestream, its components are not 1, 3 or 4 or of different depths.
    /// </summary>
    /// <param name="codestream">The JPEG 2000 data.</param>
    /// <returns>The JP2 file, or null.</returns>
    public static byte[]? Wrap(byte[] codestream)
    {
        if (codestream.Length < 42 || codestream[0] != 0xFF || codestream[1] != 0x4F || codestream[2] != 0xFF || codestream[3] != 0x51) return null;
        static uint U32(byte[] d, int i) => (uint)(d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3]);
        var (width, height) = (U32(codestream, 8) - U32(codestream, 16), U32(codestream, 12) - U32(codestream, 20));
        var components = codestream[40] << 8 | codestream[41];
        if (components is not (1 or 3 or 4) || codestream.Length < 42 + 3 * components) return null;
        var depth = codestream[42];
        for (var i = 1; i < components; i++)
            if (codestream[42 + 3 * i] != depth) return null;
        var output = new MemoryStream(codestream.Length + 80);
        static void Box(Stream to, string type, ReadOnlySpan<byte> content)
        {
            var head = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(head, (uint)(8 + content.Length));
            System.Text.Encoding.ASCII.GetBytes(type, head.AsSpan(4));
            to.Write(head);
            to.Write(content);
        }
        Box(output, "jP  ", [0x0D, 0x0A, 0x87, 0x0A]);
        Box(output, "ftyp", "jp2 \0\0\0\0jp2 "u8);
        var ihdr = new byte[14];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr, height);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(ihdr.AsSpan(8), (ushort)components);
        ihdr[10] = depth;   // bit depth − 1, the sign in the high bit, as in SIZ
        ihdr[11] = 7;       // compression: JPEG 2000
        var colr = new byte[7];
        colr[0] = 1;        // method 1: enumerated
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(3), components switch { 1 => 17u, 3 => 16u, _ => 12u });
        var header = new MemoryStream();
        Box(header, "ihdr", ihdr);
        Box(header, "colr", colr);
        Box(output, "jp2h", header.ToArray());
        Box(output, "jp2c", codestream);
        return output.ToArray();
    }

    /// <summary>What the JP2 header says.</summary>
    private sealed class Header
    {
        public int? Channels;
        public int? Depth;
        public List<int>? Depths;
        public int? PaletteColumns;
        public List<(int Method, int Approx, uint Enumerated)> Colours { get; } = [];
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
            if (length < size || pos + length > end) length = end - pos;
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
            }
            pos = e;
        }
        return -1;
    }
}
