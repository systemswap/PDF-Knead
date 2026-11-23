using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Pdf.Filters.Jbig2;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// JBIG2 probe run (not part of the normal test run; PDFKNEAD_PRUEFUNG=jbig2): the conformance data of ITU-T T.88 (08/2018), Annex K
/// (TT1–TT10), against their reconstructed images. Data: the software archive of T.88 as the ITU publishes it, unpacked into the norm folder
/// (Normen-ISO/grundlagen/bildformate/t88-2018-software/pruefdaten, not in Git; docs/normabgleich/M2-filter.md, paragraph M2d). Each page is
/// decoded on its own, as PDF embeds it (the page's segments and those of page 0), and compared pixel by pixel; TT8 uses the colour
/// extension of Amd. 3, which ISO 32000-2, 7.4.7 does not allow, and must be refused. Report written to %TEMP%\pdf-knead-jbig2.txt.
/// Page 1 of TT1, TT2 to TT7 and TT6 were written by the sample software of T.88 against the standard (docs/normabgleich/sonderregeln.md,
/// S-C C09): its encoder writes the export runs of every arithmetic symbol dictionary as 0, 0 (6.5.10 asks for runs over all symbols), its
/// text regions in TT2 to TT7 refer to no symbol dictionary (7.4.3.1), and it builds the symbol ID codes of a Huffman text region its own
/// way (7.4.3.1.7); its own decoder makes up for it. On those pages the generic region is checked against the reference image wherever no
/// other region is drawn over it — the extended template of Amd. 2 (TT7) included; the symbol and text features are covered by the test
/// files with expected images (Jbig2ExpectedImages).
/// </summary>
public sealed class Jbig2Conformance
{
    [Fact]
    public void Conformance_AgainstReconstructedImages()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_PRUEFUNG") != "jbig2") return;
        var folder = Folder();
        Assert.True(Directory.Exists(folder), $"Prüfdaten fehlen: {folder}");
        var report = new StringBuilder();
        var failures = new List<string>();
        var genericImages = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var files = Directory.GetFiles(folder, "*.jb2").Order().ToList();
        Assert.Equal(10, files.Count);
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var pages = Pages(File.ReadAllBytes(file));
            for (var k = 0; k < pages.Count; k++)
            {
                var label = $"{name} Seite {k + 1}";
                var reference = Path.Combine(folder, $"{name}_TT{k:00}.bmp");
                if (!File.Exists(reference))
                {
                    failures.Add($"{label}: Referenzbild fehlt");
                    continue;
                }
                var (width, height, black) = ReadBmp(File.ReadAllBytes(reference));
                var truncated = false;
                var image = Jbig2Decoder.Decode(pages[k], null, width, height, ref truncated);
                if (name.EndsWith("_TT8", StringComparison.Ordinal))
                {
                    // colour extension (Amd. 3): refused, not drawn
                    report.AppendLine($"{label,-32} {(truncated ? "abgelehnt (Farberweiterung, in PDF nicht erlaubt)" : "GELESEN — erwartet: abgelehnt")}");
                    if (!truncated) failures.Add($"{label}: Farberweiterung nicht abgelehnt");
                    continue;
                }
                var wrong = Wrong(image, black, width, height);
                var ok = wrong == 0 && !truncated;
                // the generic region alone (page information and generic region segments): where it covers the page, it checks the generic
                // region decoder apart from the symbol and text segments of the stream
                var genericOnly = Generic(pages[k]);
                var genericTruncated = false;
                var generic = genericOnly is null ? null : Jbig2Decoder.Decode(genericOnly, null, width, height, ref genericTruncated);
                var genericWrong = generic is null ? -1 : Wrong(generic, black, width, height);
                var genericText = generic is null ? "" : $"; nur generische Region: {(genericWrong == 0 && !genericTruncated ? "bitgleich" : $"{genericWrong} Pixel{(genericTruncated ? ", abgeschnitten" : "")}")}";
                var deviant = name.EndsWith("_TT1", StringComparison.Ordinal) && k == 0 || name.StartsWith("codeStreamTest1_TT", StringComparison.Ordinal) && !name.EndsWith("_TT1", StringComparison.Ordinal)
                    || name.EndsWith("_TT6", StringComparison.Ordinal);
                report.AppendLine($"{label,-32} {width}×{height} {(ok ? "bitgleich" : $"ABWEICHUNG: {wrong} Pixel{(truncated ? ", abgeschnitten" : "")}")}{genericText}"
                    + (deviant && !ok ? " (von der Beispielsoftware gegen T.88 geschrieben, S-C C09; auch keiner der drei Referenz-Leser zeichnet sie wie das Referenzbild)" : ""));
                if (!ok && !deviant) failures.Add($"{label}: {wrong} Pixel{(truncated ? ", abgeschnitten" : "")}{genericText}");
                if (deviant && generic is not null)
                {
                    // the generic region of a page the sample software wrote against the standard (S-C C09): exact wherever no other region
                    // (text, halftone, refinement) is drawn over it
                    var others = OtherRegions(pages[k]);
                    var outside = Wrong(generic, black, width, height, others);
                    report.AppendLine($"{"",-32} generische Region außerhalb von {others.Count} anderen Bereichen: {(outside == 0 && !genericTruncated ? "bitgleich" : $"{outside} Pixel")}");
                    if (outside != 0 || genericTruncated) failures.Add($"{label}: generische Region außerhalb der anderen Bereiche {outside} Pixel");
                }
                if (deviant && generic is not null && !genericTruncated && name.StartsWith("codeStreamTest1_TT", StringComparison.Ordinal) && k == 0
                    && !name.EndsWith("_TT1", StringComparison.Ordinal))
                    genericImages[name] = generic;
            }
        }
        // the generic regions of TT2 to TT7 encode the same bitmap with template 0 (TT2, TT3, TT5), template 1 (TT4) and the extended template
        // of Amd. 2 (TT7): decoded alike, they show each template read right — the extended one included (docs/FEHLERGEDAECHTNIS.md, No. 966)
        foreach (var n in new[] { "codeStreamTest1_TT2", "codeStreamTest1_TT3", "codeStreamTest1_TT4", "codeStreamTest1_TT5", "codeStreamTest1_TT7" })
            if (!genericImages.ContainsKey(n)) failures.Add($"{n}: generische Region nicht gelesen");
        if (genericImages.TryGetValue("codeStreamTest1_TT2", out var first))
            foreach (var (n, img) in genericImages)
            {
                var same = img.AsSpan().SequenceEqual(first);
                report.AppendLine($"generische Region {n,-22} {(same ? "gleich TT2" : "ANDERS als TT2")}");
                if (!same) failures.Add($"{n}: generische Region anders als in TT2");
            }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "pdf-knead-jbig2.txt"), report.ToString());
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    private static string Folder([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        // from this source file (the build output lies outside the project, under %LOCALAPPDATA%)
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source) ?? AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PdfKnead.sln")))
                return Path.Combine(dir.FullName, "Normen-ISO", "grundlagen", "bildformate", "t88-2018-software", "pruefdaten");
        }
        return "";
    }

    /// <summary>
    /// Splits a stand-alone JBIG2 file in sequential organisation (T.88, D.1) into the embedded organisation of PDF, one stream per page: the
    /// segments of page 0 and of that page in their order, without the file header and without end-of-page and end-of-file segments
    /// (ISO 32000-2, 7.4.7).
    /// </summary>
    private static List<byte[]> Pages(byte[] d)
    {
        Assert.True(d.Length > 9 && d[0] == 0x97 && (d[8] & 1) == 1, "nur die sequenzielle Organisation");
        var p = 9 + ((d[8] & 2) == 0 ? 4 : 0);
        var segments = new List<(uint Page, int Type, byte[] Bytes)>();
        while (p < d.Length)
        {
            var start = p;
            var number = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            p += 4;
            var flags = d[p++];
            var count = d[p] >> 5;
            if (count == 7)
            {
                count = (int)(BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p)) & 0x1FFFFFFF);
                p += 4 + (count + 8) / 8;
            }
            else p++;
            p += count * (number <= 256 ? 1 : number <= 65536 ? 2 : 4);
            uint page;
            if ((flags & 0x40) != 0)
            {
                page = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
                p += 4;
            }
            else page = d[p++];
            var length = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            Assert.NotEqual(0xFFFFFFFFu, length);
            p += 4 + (int)length;
            segments.Add((page, flags & 0x3F, d[start..p]));
        }
        var pageNumbers = segments.Where(s => s.Type == 48).Select(s => s.Page).ToList();
        return pageNumbers.Select(n => segments.Where(s => (s.Page == 0 || s.Page == n) && s.Type is not (49 or 51)).SelectMany(s => s.Bytes).ToArray()).ToList();
    }

    /// <summary>
    /// Pixels in which the decoder output (rows padded to bytes, 0 = black) differs from the reference (true = black); with
    /// <paramref name="skip"/> the pixels inside those rectangles are left out.
    /// </summary>
    private static int Wrong(byte[] image, bool[] black, int width, int height, List<(long X, long Y, long W, long H)>? skip = null)
    {
        var rowBytes = (width + 7) >> 3;
        var wrong = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (skip is not null && skip.Any(r => x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H)) continue;
            if ((((image[y * rowBytes + (x >> 3)] >> (7 - (x & 7))) & 1) == 0) != black[y * width + x]) wrong++;
        }
        return wrong;
    }

    /// <summary>The rectangles of the region segments other than generic regions (text, halftone, refinement; T.88, 7.4.1).</summary>
    private static List<(long X, long Y, long W, long H)> OtherRegions(byte[] d)
    {
        var regions = new List<(long, long, long, long)>();
        for (var p = 0; p < d.Length;)
        {
            var number = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            var flags = d[p + 4];
            var count = d[p + 5] >> 5;
            p += 6 + count * (number <= 256 ? 1 : number <= 65536 ? 2 : 4);
            p += (flags & 0x40) != 0 ? 4 : 1;
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            p += 4;
            if ((flags & 0x3F) is 4 or 6 or 7 or 20 or 22 or 23 or 40 or 42 or 43)
                regions.Add((BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 8)), BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 12)),
                    BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p)), BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 4))));
            p += length;
        }
        return regions;
    }

    /// <summary>The page information and the generic region segments of an embedded page stream (T.88, 7.2); null without a generic region.</summary>
    private static byte[]? Generic(byte[] d)
    {
        var kept = new List<byte>();
        var any = false;
        for (var p = 0; p < d.Length;)
        {
            var start = p;
            var number = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            var flags = d[p + 4];
            var count = d[p + 5] >> 5;
            p += 6 + count * (number <= 256 ? 1 : number <= 65536 ? 2 : 4);
            p += (flags & 0x40) != 0 ? 4 : 1;
            p += 4 + (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            var type = flags & 0x3F;
            if (type is 36 or 38 or 39) any = true;
            if (type is 48 or 36 or 38 or 39) kept.AddRange(d[start..p]);
        }
        return any ? [.. kept] : null;
    }

    /// <summary>An uncompressed BMP (1 bit with palette, or 24 bits); true = black.</summary>
    private static (int Width, int Height, bool[] Black) ReadBmp(byte[] b)
    {
        var offset = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(10));
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(14));
        var width = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(18));
        var height = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(22));
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(28));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(30)));
        var bottomUp = height > 0;
        height = Math.Abs(height);
        var stride = ((width * bits + 31) / 32) * 4;
        var black = new bool[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = offset + (bottomUp ? height - 1 - y : y) * stride;
            for (var x = 0; x < width; x++)
            {
                int blue, green, red;
                if (bits == 1)
                {
                    var index = (b[row + (x >> 3)] >> (7 - (x & 7))) & 1;
                    var entry = 14 + headerSize + 4 * index;
                    (blue, green, red) = (b[entry], b[entry + 1], b[entry + 2]);
                }
                else
                {
                    Assert.Equal(24, bits);
                    (blue, green, red) = (b[row + 3 * x], b[row + 3 * x + 1], b[row + 3 * x + 2]);
                }
                black[y * width + x] = red + green + blue < 384;
            }
        }
        return (width, height, black);
    }
}
