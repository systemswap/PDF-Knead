using System.Buffers.Binary;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// Image streams the display reads only with a tolerance, written in the form ISO 32000-2 asks for — where that is possible without loss
/// (decided on 20.11.2026; docs/normabgleich/sonderregeln.md, S-A A14, A75, A76; docs/FEHLERGEDAECHTNIS.md, No. 992):
/// <list type="bullet">
/// <item>bytes before the start of a JPEG (S-A A14: the decoder passes over them within the first kilobyte) go — the JPEG itself is unchanged;</item>
/// <item>a bare JPEG 2000 codestream (S-A A75; 7.4.9 expects a file of the JPX family) gets the JP2 wrapper the PDF/A repair puts around it
/// (<see cref="PdfAJpeg2000.Wrap"/>; the codestream unchanged; with the opacity channel /SMaskInData announces named in a channel
/// definition, No. 996; where it cannot be wrapped — signed components, 2 without opacity, more than 4 — it stays);</item>
/// <item>/Width and /Height of a JPEG 2000 image become the size of its data (S-A A76: the display takes that size; 7.4.9 asks that they
/// match).</item>
/// </list>
/// Errors inside the JPEG or JPEG 2000 data stay byte for byte: written in the standard's form they could only be coded anew, with loss.
/// </summary>
internal static class ImageDataConformance
{
    private static readonly PdfName WidthKey = PdfName.Get("Width"), HeightKey = PdfName.Get("Height");

    /// <summary>
    /// The data to write and the changes made to <paramref name="dict"/> (the dictionary to be written, with /Width and /Height set where they
    /// changed); null where nothing is to be repaired.
    /// </summary>
    /// <param name="dict">The dictionary of the image as it will be written; changed in place only where the result is not null.</param>
    /// <param name="raw">The stored data (decrypted, still encoded).</param>
    /// <param name="opacity">For a bare codestream: <see cref="BareOpacity"/> of the image (-1: it is not wrapped).</param>
    public static byte[]? Repair(PdfDictionary dict, ReadOnlySpan<byte> raw, int opacity = 0)
    {
        var chain = Filters.StreamFilters.Chain(dict);
        if (chain is [("DCTDecode", _)])
        {
            if (raw.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8])) return null;
            var start = raw[..Math.Min(raw.Length, 1024)].IndexOf((ReadOnlySpan<byte>)[0xFF, 0xD8]);
            return start > 0 ? raw[start..].ToArray() : null;
        }
        if (chain is not [("JPXDecode", _)]) return null;
        byte[]? data = null;
        if (opacity >= 0 && raw.StartsWith((ReadOnlySpan<byte>)[0xFF, 0x4F, 0xFF, 0x51]) && PdfAJpeg2000.Wrap(raw, opacity) is { } wrapped) data = wrapped;
        var changed = data is not null;
        if (CodestreamSize(data ?? raw) is var (width, height) && width > 0 && height > 0)
        {
            if (dict.GetInteger(WidthKey) != width)
            {
                dict.Set(WidthKey, PdfInteger.Of(width));
                changed = true;
            }
            if (dict.GetInteger(HeightKey) != height)
            {
                dict.Set(HeightKey, PdfInteger.Of(height));
                changed = true;
            }
        }
        return changed ? data ?? raw.ToArray() : null;
    }

    /// <summary>
    /// What the last component of a bare JPEG 2000 codestream is to the display (<see cref="Filters.Jpx.JpxDecoder.OpacityLast"/>): 1 or 2 —
    /// the opacity or premultiplied opacity /SMaskInData announces —, 0 a colour; -1 where the writer cannot tell (a /ColorSpace named by a
    /// resource): wrapped without a channel definition, the copy would show what the source does not (docs/FEHLERGEDAECHTNIS.md, No. 996).
    /// </summary>
    /// <param name="image">The image dictionary as read.</param>
    /// <param name="codestream">The stored data (decrypted, still encoded).</param>
    /// <param name="doc">The document of the image (for a /ColorSpace given as an array).</param>
    internal static int BareOpacity(PdfDictionary image, ReadOnlySpan<byte> codestream, Document.PdfDocument? doc)
    {
        var smask = Graphics.PdfImageDecoder.SMaskInData(image);
        if (smask == 0 || !codestream.StartsWith((ReadOnlySpan<byte>)[0xFF, 0x4F, 0xFF, 0x51])) return 0;
        if (codestream.Length < 42) return -1;
        int? declared;
        switch (image.Get(PdfName.Get("ColorSpace")))
        {
            case null or PdfNull:
                declared = null;
                break;
            case PdfName { Value: "DeviceGray" or "G" or "CalGray" }:
                declared = 1;
                break;
            case PdfName { Value: "DeviceRGB" or "RGB" or "CalRGB" }:
                declared = 3;
                break;
            case PdfName { Value: "DeviceCMYK" or "CMYK" }:
                declared = 4;
                break;
            case PdfArray array when doc is not null:
                var space = Graphics.ColorSpace.Create(array, null, doc);
                if (space.IsIndexed) return 0;   // the palette of the page: no opacity is read from the data (PdfImageDecoder)
                declared = space.Components;
                break;
            default:
                return -1;
        }
        return Filters.Jpx.JpxDecoder.OpacityLast(codestream[40] << 8 | codestream[41], declared, []) ? smask : 0;
    }

    /// <summary>
    /// The size of the image of a JPEG 2000 codestream or JP2/JPX file (ISO/IEC 15444-1, A.5.1: Xsiz − XOsiz, Ysiz − YOsiz of the SIZ of the
    /// first codestream — at the start, or in the first contiguous codestream box at the top level); null if it cannot be found.
    /// </summary>
    internal static (long Width, long Height)? CodestreamSize(ReadOnlySpan<byte> data)
    {
        var at = 0;
        if (!data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0x4F]))
        {
            at = -1;
            var pos = 0;
            for (var boxes = 0; pos + 8 <= data.Length && boxes < 10_000; boxes++)
            {
                long length = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
                var header = 8;
                if (length == 1)
                {
                    if (pos + 16 > data.Length) break;
                    length = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(pos + 8)..]);
                    header = 16;
                }
                else if (length == 0) length = data.Length - pos;
                if (data[(pos + 4)..(pos + 8)].SequenceEqual("jp2c"u8))
                {
                    at = pos + header;
                    break;
                }
                if (length < header || length > data.Length - pos) break;
                pos += (int)length;
            }
            if (at < 0) return null;
        }
        // SOC, then SIZ: marker, Lsiz, Rsiz, Xsiz, Ysiz, XOsiz, YOsiz
        if (at + 24 > data.Length || !data[at..].StartsWith((ReadOnlySpan<byte>)[0xFF, 0x4F, 0xFF, 0x51])) return null;
        long xsiz = BinaryPrimitives.ReadUInt32BigEndian(data[(at + 8)..]), ysiz = BinaryPrimitives.ReadUInt32BigEndian(data[(at + 12)..]);
        long xo = BinaryPrimitives.ReadUInt32BigEndian(data[(at + 16)..]), yo = BinaryPrimitives.ReadUInt32BigEndian(data[(at + 20)..]);
        return xsiz > xo && ysiz > yo ? (xsiz - xo, ysiz - yo) : null;
    }
}
