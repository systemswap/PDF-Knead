using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// JPEG 2000 probe run (not part of the normal test run; PDFKNEAD_PRUEFUNG=jpx): the conformance files of ITU-T T.803 against their
/// reference images, reporting per component the peak error and the mean squared error. Data: the archive of T.803 as the ITU publishes it,
/// in the norm folder (Normen-ISO/grundlagen/pruefdaten/jpeg2000-t803/entpackt/J2KP4files, not in Git; docs/normabgleich/M2-filter.md), or
/// the former layout %LOCALAPPDATA%\pdf-knead-build\jpx\input and …\ref. Report written to %TEMP%\pdf-knead-jpx.txt.
/// </summary>
public sealed partial class JpxConformance
{
    [Fact]
    public void Conformance_AgainstReferenceImages()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_PRUEFUNG") != "jpx") return;
        var (inputs, refDirs) = Folders();
        string? Ref(string file) => refDirs.Select(d => Path.Combine(d, file)).FirstOrDefault(File.Exists);
        // reference names: c1p0_01-0.pgx (ITU archive) or c1p0_01_0.pgx (former archive)
        string? Component(string name, int c) => Ref($"c1{name}-{c}.pgx") ?? Ref($"c1{name}_{c}.pgx");
        var report = new StringBuilder();
        var filter = Environment.GetEnvironmentVariable("PDFKNEAD_JPX");
        var failures = new List<string>();
        foreach (var file in inputs.Where(Directory.Exists).SelectMany(Directory.GetFiles).Where(f => Path.GetExtension(f) is ".j2k" or ".jp2" or ".j2c" or ".jpx").Order())
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (filter is not null && !name.Contains(filter)) continue;
            var line = new StringBuilder($"{Path.GetFileName(file),-22}");
            try
            {
                var data = File.ReadAllBytes(file);
                var watch = Stopwatch.StartNew();
                if (name.StartsWith("p0_") || name.StartsWith("p1_"))
                {
                    // Reduction as in the reference (p0_08 is decoded with one level less)
                    var full = JpxDecoder.DecodePlanes(data, 1, fixedReduce: 32);   // header only: as small as possible (without the size limit)
                    var reduce = 0;
                    if (Component(name, 0) is { } first)
                    {
                        var (rw, _, _, _, _) = ReadPgx(first);
                        var k = full.Codestream.Components[0];
                        long Width(int r) => (((long)full.Codestream.X1 + k.Dx - 1) / k.Dx + (1L << r) - 1 >> r) - (((long)full.Codestream.X0 + k.Dx - 1) / k.Dx + (1L << r) - 1 >> r);
                        while (reduce < 8 && Width(reduce) > rw) reduce++;
                    }
                    watch.Restart();
                    var planes = JpxDecoder.DecodePlanes(data, long.MaxValue, reduce);
                    var (peaks, mses) = Limits(name);
                    line.Append($"{watch.ElapsedMilliseconds,6} ms {planes.Width}×{planes.Height}{(planes.Truncated ? " ABGESCHNITTEN" : "")}");
                    for (var c = 0; c < planes.Planes.Length; c++)
                    {
                        if (Component(name, c) is not { } path) continue;
                        var (w, h, signed, depth, values) = ReadPgx(path);
                        var comp = planes.Codestream.Components[c];
                        var offset = signed ? 1 << (Math.Min(16, comp.Precision) - 1) : 0;
                        if (w != planes.PlaneWidth[c] || h != planes.PlaneHeight[c])
                        {
                            failures.Add($"{name} K{c} Größe");
                            line.Append($" | K{c}: Größe {planes.PlaneWidth[c]}×{planes.PlaneHeight[c]} statt {w}×{h}");
                            continue;
                        }
                        long peak = 0;
                        double sum = 0;
                        for (var i = 0; i < values.Length; i++)
                        {
                            var d = Math.Abs(planes.Planes[c][i] - offset - values[i]);
                            peak = Math.Max(peak, d);
                            sum += (double)d * d;
                        }
                        var mse = sum / values.Length;
                        var ok = peak <= peaks[Math.Min(c, peaks.Length - 1)] && mse <= mses[Math.Min(c, mses.Length - 1)];
                        // better than the norm demands, where reached (decided on 11.11.2026): the own bound guards the quality reached
                        var (qPeaks, qMses) = Quality(name) ?? (peaks, mses);
                        var good = peak <= qPeaks[Math.Min(c, qPeaks.Length - 1)] && mse <= qMses[Math.Min(c, qMses.Length - 1)];
                        if (!ok) failures.Add($"{name} K{c} über der Norm");
                        else if (!good) failures.Add($"{name} K{c} schlechter als bisher erreicht");
                        line.Append($" | K{c} {depth}{(signed ? "s" : "")} Spitze {peak} MSE {mse:0.###}{(ok ? good ? "" : " SCHLECHTER ALS ERREICHT" : " ZU GROSS")}");
                    }
                }
                else
                {
                    var image = JpxDecoder.Decode(data, long.MaxValue);
                    line.Append($"{watch.ElapsedMilliseconds,6} ms {image.Width}×{image.Height}×{image.Channels}{(image.Truncated ? " ABGESCHNITTEN" : "")}");
                    var path = Ref(name + ".ppm") ?? "";
                    if (filter is not null && !File.Exists(path))
                        Dump(Path.Combine(Path.GetTempPath(), $"jpx-{name}-ist.png"), image.Width, image.Height, image.Channels, i => image.Samples[i]);
                    line.Append($" Farbe {image.Color}{(image.Alpha is null ? "" : " +Deckkraft")}");
                    if (File.Exists(path))
                    {
                        var (w, h, channels, values) = ReadPnm(path);
                        if (filter is not null && channels == image.Channels)
                        {
                            Dump(Path.Combine(Path.GetTempPath(), $"jpx-{name}-ist.png"), image.Width, image.Height, image.Channels, i => image.Samples[i]);
                            Dump(Path.Combine(Path.GetTempPath(), $"jpx-{name}-soll.png"), w, h, channels, i => values[i]);
                        }
                        if (w != image.Width || h != image.Height || channels != image.Channels)
                        {
                            line.Append($" | Referenz {w}×{h}×{channels}");
                        }
                        else
                        {
                            long peak = 0;
                            double sum = 0;
                            for (var i = 0; i < values.Length; i++)
                            {
                                var d = Math.Abs(image.Samples[i] - values[i]);
                                peak = Math.Max(peak, d);
                                sum += (double)d * d;
                            }
                            line.Append($" | Spitze {peak} MSE {sum / values.Length:0.###}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                line.Append($" FEHLER {e.GetType().Name}: {e.Message}");
            }
            report.AppendLine(line.ToString());
            if (filter is not null) report.AppendLine(Describe(File.ReadAllBytes(file)));
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "pdf-knead-jpx.txt"), report.ToString());
        Assert.Empty(failures);
    }

    /// <summary>
    /// Limits of the standard (ITU-T T.803, class 1) per component: peak error and mean squared error, as in the
    /// test list of the archive; 0 for lossless files.
    /// </summary>
    private static (double[] Peak, double[] Mse) Limits(string name) => name switch
    {
        "p0_04" => ([5, 4, 6], [0.776, 0.626, 1.070]),
        "p0_05" => ([2, 2, 2, 0], [0.302, 0.307, 0.269, 0]),
        "p0_06" => ([635, 403, 378, 0], [11287, 6124, 3968, 0]),
        "p1_02" => ([5, 4, 6], [0.765, 0.616, 1.051]),
        "p1_03" => ([2, 2, 1, 0], [0.3, 0.210, 0.200, 0]),
        "p1_04" => ([624], [3080]),
        // 9.716 as in both editions of T.803 (2002 and 2024; docs/FEHLERGEDAECHTNIS.md, No. 732); the limits here are those of 2002, which the
        // 2024 edition relaxes for p0_05 and p1_03
        "p1_05" => ([40, 40, 40], [8.458, 9.716, 10.154]),
        "p1_06" => ([2, 2, 2], [0.6, 0.6, 0.6]),
        _ => ([0], [0]),
    };

    /// <summary>
    /// Own bounds below those of the norm, where PDF-Knead reaches better values (11.11.2026, ITU archive of T.803): the values reached, the peak
    /// plus one and the mean squared error plus a tenth, so that float differences between platforms do not count — but a decoder that gets
    /// worse does. Null where the norm demands exactness (lossless files) or no better value is reached.
    /// </summary>
    private static (double[] Peak, double[] Mse)? Quality(string name) => name switch
    {
        "p0_04" => ([3, 3, 3], [0.35, 0.28, 0.43]),
        "p0_05" => ([2, 2, 2, 0], [0.26, 0.27, 0.26, 0]),
        "p0_06" => ([368, 26, 187, 0], [2910, 27, 49, 0]),
        "p1_02" => ([3, 3, 3], [0.35, 0.27, 0.43]),
        "p1_03" => ([2, 2, 2, 0], [0.25, 0.22, 0.2, 0]),
        "p1_04" => ([254], [62]),
        "p1_05" => ([12, 8, 16], [0.69, 0.82, 0.93]),
        "p1_06" => ([2, 2, 2], [0.09, 0.01, 0.05]),
        _ => null,
    };

    /// <summary>Folders of the test files and of the reference images: the ITU archive in the norm folder, otherwise the former layout.</summary>
    private static (string[] Inputs, string[] Refs) Folders([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        // from this source file (the build output lies outside the project, under %LOCALAPPDATA%)
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source) ?? AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "PdfKnead.sln"))) continue;
            var itu = Path.Combine(dir.FullName, "Normen-ISO", "grundlagen", "pruefdaten", "jpeg2000-t803", "entpackt", "J2KP4files");
            if (Directory.Exists(itu))
                return ([.. new[] { "codestreams_profile0", "codestreams_profile1", "testfiles_jp2" }.Select(d => Path.Combine(itu, d))],
                    [.. new[] { "reference_class1_profile0", "reference_class1_profile1", "reference_jp2" }.Select(d => Path.Combine(itu, d))]);
            break;
        }
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pdf-knead-build", "jpx");
        return ([Path.Combine(root, "input")], [Path.Combine(root, "ref")]);
    }

    private static void Dump(string path, int w, int h, int channels, Func<int, int> value)
    {
        using var bitmap = new SkiaSharp.SKBitmap(w, h);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * channels;
            var r = (byte)value(i);
            bitmap.SetPixel(x, y, channels >= 3 ? new SkiaSharp.SKColor(r, (byte)value(i + 1), (byte)value(i + 2)) : new SkiaSharp.SKColor(r, r, r));
        }
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    /// <summary>Header data for inspection (only with PDFKNEAD_JPX).</summary>
    private static string Describe(byte[] data)
    {
        try
        {
            var box = JpxBoxes.Read(data);
            var cs = JpxCodestream.Parse(box.Data, box.Start, box.End);
            var m = cs.Main;
            var c = m.Coding!;
            var q = m.Quantization!;
            var s = new StringBuilder();
            s.Append($"  Bild {cs.X0},{cs.Y0}–{cs.X1},{cs.Y1} Kacheln {cs.TileWidth}×{cs.TileHeight} ab {cs.TileX0},{cs.TileY0} ({cs.TilesX}×{cs.TilesY})");
            s.Append($" Komp {string.Join(",", cs.Components.Select(k => $"{k.Precision}{(k.Signed ? "s" : "")}/{k.Dx}x{k.Dy}"))}");
            s.Append($"\n  Reihenfolge {m.Order} Schichten {m.Layers} MCT {m.MultipleComponentTransform} Stufen {c.Levels} Block {c.BlockWidth}/{c.BlockHeight}");
            s.Append($" Art 0x{c.Style:X2} {(c.Reversible ? "5/3" : "9/7")} Bereiche [{string.Join(",", c.Precincts.Select(p => $"{p & 15}/{p >> 4}"))}]");
            s.Append($"\n  Quant {q.Style} Schutz {q.GuardBits} Exp [{string.Join(",", q.Exponents.Take(8))}] COC {m.ComponentCoding.Count(x => x is not null)} QCC {m.ComponentQuantization.Count(x => x is not null)}");
            s.Append($" ROI [{string.Join(",", m.RoiShift.Select(r => r?.ToString() ?? "-"))}] POC {m.Progressions?.Count ?? 0}");
            var tiles = cs.Tiles.Where(t => t is not null).ToList();
            s.Append($"\n  Kachelteile {tiles.Sum(t => t!.Parts)} PPM {tiles.Sum(t => t!.PpmChunks.Count)} PPT {tiles.Sum(t => t!.Ppt.Count)}");
            s.Append($" Kachel-COD {tiles.Count(t => t!.Header.HasCod)} Kachel-QCD {tiles.Count(t => t!.Header.Quantization is not null)} Kachel-POC {tiles.Count(t => t!.Header.Progressions is not null)}");
            return s.ToString();
        }
        catch (Exception e)
        {
            return "  " + e.Message;
        }
    }

    [GeneratedRegex(@"^PG\s+(ML|LM)\s*([+-]?)\s*(\d+)\s+(\d+)\s+(\d+)\s*\n")]
    private static partial Regex PgxHeader();

    private static (int W, int H, bool Signed, int Depth, int[] Values) ReadPgx(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(64, bytes.Length));
        var m = PgxHeader().Match(head);
        if (!m.Success) throw new InvalidDataException("PGX-Kopf");
        var bigEndian = m.Groups[1].Value == "ML";
        var signed = m.Groups[2].Value == "-";
        var depth = int.Parse(m.Groups[3].Value);
        int w = int.Parse(m.Groups[4].Value), h = int.Parse(m.Groups[5].Value);
        var size = (depth + 7) / 8;
        var at = m.Length;
        var values = new int[w * h];
        for (var i = 0; i < values.Length; i++, at += size)
        {
            long v = 0;
            for (var b = 0; b < size; b++) v = bigEndian ? v << 8 | bytes[at + b] : v | (long)bytes[at + b] << (8 * b);
            if (signed && (v & (1L << (size * 8 - 1))) != 0) v -= 1L << (size * 8);
            values[i] = (int)v;
        }
        return (w, h, signed, depth, values);
    }

    private static (int W, int H, int Channels, int[] Values) ReadPnm(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var at = 0;
        string Token()
        {
            while (true)
            {
                while (at < bytes.Length && char.IsWhiteSpace((char)bytes[at])) at++;
                if (bytes[at] != '#') break;
                while (bytes[at] != '\n') at++;
            }
            var start = at;
            while (at < bytes.Length && !char.IsWhiteSpace((char)bytes[at])) at++;
            return Encoding.ASCII.GetString(bytes, start, at - start);
        }
        var magic = Token();
        int w = int.Parse(Token()), h = int.Parse(Token()), max = int.Parse(Token());
        var ascii = magic is "P2" or "P3";
        if (!ascii) at++;
        var channels = magic is "P6" or "P3" ? 3 : 1;
        var values = new int[w * h * channels];
        var wide = max > 255;
        for (var i = 0; i < values.Length; i++)
        {
            int v;
            if (ascii)
            {
                v = int.Parse(Token());
            }
            else
            {
                v = wide ? bytes[at] << 8 | bytes[at + 1] : bytes[at];
                at += wide ? 2 : 1;
            }
            // Reference with more than 8 bits: reduced to 8 bits as the decoder does
            values[i] = wide ? v >> (int.Log2(max + 1) - 8) : v * 255 / max;
        }
        return (w, h, channels, values);
    }
}
