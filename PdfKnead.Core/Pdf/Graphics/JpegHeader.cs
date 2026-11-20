using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Graphics;

/// <summary>
/// What the PDF says about a JPEG beside its data (docs/FEHLERGEDAECHTNIS.md, No. 457, Analyse E-25 and E-34), put into a copy of it when the
/// JPEG becomes a file of its own (image export), so that other programs read it as the PDF means it. Decoding does not need it: the own decoder
/// (<see cref="Filters.Jpeg.JpegDecoder"/>, FG 805) reads /ColorTransform and the number of lines itself.
/// </summary>
internal static class JpegHeader
{
    /// <summary>The number of lines at or above which the header holds a placeholder a scanner wrote (65,535, 65,280, 60,000).</summary>
    private const int PlaceholderLines = 60_000;

    private static readonly PdfName ColorTransformName = PdfName.Get("ColorTransform");

    /// <summary>
    /// The JPEG as another program should read it:
    /// <list type="bullet">
    /// <item>The number of lines: the header may hold 0 with the number in a DNL segment after the first scan (ITU-T T.81, B.2.2 and
    /// B.2.5), or a scanner wrote a placeholder there. Many programs read no DNL; with the placeholder, a progressive JPEG expects more blocks
    /// in every scan than there are and is not shown at all (a public test file bug_86459). The height of the PDF is taken when the DNL
    /// agrees with it or there is none — never a size from the data that the PDF does not confirm.</item>
    /// <item>/ColorTransform of three or four components (ISO 32000-2, 7.4.8, Table 13): an Adobe marker in the data decides, otherwise this
    /// entry. Other programs know only the marker (and take a JFIF marker for YCbCr), so the entry goes in as a marker.</item>
    /// </list>
    /// </summary>
    /// <param name="data">The JPEG (bytes before its start are kept).</param>
    /// <param name="parms">The /DecodeParms of DCTDecode.</param>
    /// <param name="pdfHeight">/Height of the image; 0 if unknown.</param>
    /// <returns>The same array if nothing is to change, otherwise a copy.</returns>
    public static byte[] Prepare(byte[] data, PdfDictionary? parms, long pdfHeight)
    {
        var start = data.AsSpan(0, Math.Min(data.Length, 1024)).IndexOf([(byte)0xFF, (byte)0xD8]);
        if (start < 0) return data;
        int sof = -1, components = 0, adobe = -1, jfif = -1, scan = -1;
        for (var at = start + 2; at + 3 < data.Length;)
        {
            if (data[at] != 0xFF) return data;
            var marker = data[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }
            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
            {
                at += 2;
                continue;
            }
            var length = data[at + 2] << 8 | data[at + 3];
            if (length < 2 || at + 2 + length > data.Length) return data;
            // SOF0 … SOF15 except DHT (C4), JPG (C8), DAC (CC)
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC) && length >= 8 && sof < 0)
            {
                sof = at;
                components = data[at + 9];
            }
            else if (marker == 0xEE && length >= 12 && data.AsSpan(at + 4, 5).SequenceEqual("Adobe"u8))
            {
                adobe = at;
            }
            else if (marker == 0xE0 && length >= 7 && data.AsSpan(at + 4, 5).SequenceEqual("JFIF\0"u8))
            {
                jfif = at;
            }
            else if (marker == 0xDA)
            {
                scan = at + 2 + length;
                break;
            }
            at += 2 + length;
        }
        if (sof < 0 || scan < 0) return data;

        var result = data;
        var lines = data[sof + 5] << 8 | data[sof + 6];
        if (pdfHeight is > 0 and <= 0xFFFF && (lines == 0 || lines >= PlaceholderLines && lines > pdfHeight))
        {
            // the first marker after the entropy-coded data of the first scan (FF 00 is a stuffed byte, FF D0–D7 a restart, FF FF fill)
            int dnl = -1, count = 0;
            for (var p = scan; p + 1 < data.Length; p++)
            {
                if (data[p] != 0xFF) continue;
                var m = data[p + 1];
                if (m is 0x00 or 0xFF or >= 0xD0 and <= 0xD7) continue;
                if (m == 0xDC && p + 5 < data.Length && (data[p + 2] << 8 | data[p + 3]) == 4)
                {
                    dnl = p;
                    count = data[p + 4] << 8 | data[p + 5];
                }
                break;
            }
            if (dnl < 0 || count == pdfHeight)
            {
                // without the DNL segment, which only repeats the number now in the header
                result = dnl < 0 ? (byte[])data.Clone() : [.. data.AsSpan(0, dnl), .. data.AsSpan(dnl + 6)];
                result[sof + 5] = (byte)(pdfHeight >> 8);
                result[sof + 6] = (byte)pdfHeight;
            }
        }

        // Three components: the entry decides either way. Four: common decoders read them without a marker untransformed, which is the default
        // of the norm (0) — only /ColorTransform 1 needs the marker, with transform 2 (YCCK to CMYK; docs/FEHLERGEDAECHTNIS.md, No. 738).
        // The marker does not change how common decoders take the CMYK values (checked: DctNormTests). One or two components: ignored, as
        // the norm says.
        if (adobe < 0 && parms?.GetInteger(ColorTransformName) is { } transform && (components == 3 || components == 4 && transform != 0))
        {
            byte[] marker = [0xFF, 0xEE, 0x00, 0x0E, (byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e', 0x00, 0x64, 0x00, 0x00, 0x00, 0x00,
                (byte)(transform == 0 ? 0 : components == 4 ? 2 : 1)];
            result = [.. result.AsSpan(0, start + 2), .. marker, .. result.AsSpan(start + 2)];
            // a JFIF marker means YCbCr to common decoders, before the Adobe marker; the norm knows only the Adobe marker and the entry — as
            // another application marker it is passed over
            if (jfif >= 0 && transform == 0) result[jfif + marker.Length + 1] = 0xEF;
        }
        return result;
    }
}
