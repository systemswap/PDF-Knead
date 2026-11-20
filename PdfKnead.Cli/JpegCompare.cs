using System.Diagnostics;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters.Jpeg;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;

namespace PdfKnead.Cli;

/// <summary>
/// "pdf-knead jpegvergleich &lt;folder or file&gt;": the own JPEG decoder against the codec of the display (libjpeg-turbo in Skia) over
/// every DCT image of the files — the differential test the decision of 12.11.2026 asks for before the display switches to the
/// own decoder. Per image the largest difference of a sample and how many samples differ by more than one step; images one side reads
/// and the other does not are listed. Grey and RGB are compared directly, CMYK through the conversion the codec applies without a profile
/// (inverted CMYK: R = C × K / 255).
/// </summary>
internal static class JpegCompare
{
    public static int Run(string path, int show)
    {
        var files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [path];
        int images = 0, same = 0, close = 0, apart = 0, onlyOwn = 0, onlyCodec = 0, neither = 0, damaged = 0;
        long ownTicks = 0, codecTicks = 0;
        var worst = new List<(int Max, long Over, string Where)>();
        var histogram = new long[256];
        foreach (var file in files)
        {
            var name = Path.GetRelativePath(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? ".", file);
            PdfDocument doc;
            try
            {
                doc = PdfDocument.Open(file);
            }
            catch (Exception)
            {
                continue;
            }
            using (doc)
            {
                foreach (var n in doc.ObjectNumbers.ToList())
                {
                    PdfStream? stream;
                    byte[] data;
                    PdfDictionary? parms;
                    try
                    {
                        if (doc.GetObject(n) is not PdfStream s || s.Dictionary.GetName(PdfName.Subtype)?.Value != "Image") continue;
                        var decoded = doc.DecodeStream(s);
                        if (decoded.ImageFilter?.Value != "DCTDecode") continue;
                        (stream, data, parms) = (s, decoded.Data, decoded.ImageParms);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    images++;
                    var where = $"{name} Objekt {n}";
                    Console.Error.WriteLine(where);   // progress, to find an image that takes long
                    var height = stream.Dictionary.GetInteger(PdfName.Get("Height")) ?? 0;

                    JpegImage? own = null;
                    string? ownError = null;
                    var t0 = Stopwatch.GetTimestamp();
                    try
                    {
                        own = JpegDecoder.Decode(data, new JpegOptions((int?)parms?.GetInteger(PdfName.Get("ColorTransform")), height));
                    }
                    catch (Exception e) when (e is JpegException or IndexOutOfRangeException or ArgumentException or OverflowException or InvalidOperationException)
                    {
                        ownError = $"{e.GetType().Name}: {e.Message}";
                    }
                    ownTicks += Stopwatch.GetTimestamp() - t0;
                    t0 = Stopwatch.GetTimestamp();
                    // The codec reads neither /ColorTransform nor a line count the PDF gives beside a SOF with 0 lines: it gets the data
                    // with both put in, as it got them before the own decoder took over (JpegHeader, FG 457) — otherwise such images drop out
                    // of the comparison (bug_86459: 13.11.2026, only the own decoder read it), and /ColorTransform would be compared unequally
                    var codec = SkiaImageCodec.Instance.Decode(Core.Pdf.Graphics.JpegHeader.Prepare(data, parms, height), "DCTDecode", long.MaxValue);
                    codecTicks += Stopwatch.GetTimestamp() - t0;

                    if (own is null || codec is not { } c)
                    {
                        if (own is null && codec is null) neither++;
                        else if (own is null)
                        {
                            onlyCodec++;
                            Console.WriteLine($"{where}: nur der Codec liest es ({ownError})");
                        }
                        else
                        {
                            onlyOwn++;
                            Console.WriteLine($"{where}: nur der eigene Dekoder liest es ({own.Width} × {own.Height}, {own.Channels} Kanäle{(own.Damaged ? ", beschädigt" : "")})");
                        }
                        continue;
                    }
                    if (own.Damaged) damaged++;
                    if (own.Width != c.Width || own.Height != c.Height)
                    {
                        apart++;
                        Console.WriteLine($"{where}: Größe {own.Width} × {own.Height} gegen {c.Width} × {c.Height}");
                        continue;
                    }
                    var (max, over) = Difference(own, c.Components, c.Samples, histogram);
                    if (max == 0) same++;
                    else if (max <= 1) close++;
                    else apart++;
                    worst.Add((max, over, $"{where} ({own.Width} × {own.Height}, {own.Channels} Kanäle{(own.Damaged ? ", beschädigt" : "")})"));
                }
            }
        }
        foreach (var (max, over, where) in worst.Where(w => w.Max > 1).OrderByDescending(w => w.Max).ThenByDescending(w => w.Over).Take(show))
            Console.WriteLine($"{where}: größter Unterschied {max}, über 1 Stufe: {over} Abtastwerte");
        var total = histogram.Sum();
        Console.WriteLine($"Verteilung der Unterschiede: 0: {Share(histogram[0], total)}, 1: {Share(histogram[1], total)}, 2: {Share(histogram[2], total)}, " +
                          $"3–7: {Share(histogram[3..8].Sum(), total)}, ab 8: {Share(histogram[8..].Sum(), total)}");
        Console.WriteLine($"Bilder: {images}, gleich: {same}, höchstens 1 Stufe: {close}, mehr: {apart}, nur eigener Dekoder: {onlyOwn}, nur Codec: {onlyCodec}, " +
                          $"keiner: {neither}, davon beschädigt gemeldet: {damaged}, Zeit eigener Dekoder {Stopwatch.GetElapsedTime(0, ownTicks).TotalSeconds:0.0} s, " +
                          $"Codec {Stopwatch.GetElapsedTime(0, codecTicks).TotalSeconds:0.0} s");
        return onlyCodec > 0 ? 1 : 0;
    }

    private static string Share(long part, long total) => total == 0 ? "–" : $"{100.0 * part / total:0.####} %";

    /// <summary>Largest difference and samples differing by more than one step.</summary>
    private static (int Max, long Over) Difference(JpegImage own, int codecComponents, byte[] codec, long[] histogram)
    {
        int max = 0, n = own.Channels;
        long over = 0;
        var pixels = (long)own.Width * own.Height;
        void Count(int a, int b)
        {
            var d = Math.Abs(a - b);
            histogram[d]++;
            if (d > max) max = d;
            if (d > 1) over++;
        }
        for (long p = 0; p < pixels; p++)
        {
            switch (n, codecComponents)
            {
                case (1, 1):
                    Count(own.Samples[p], codec[p]);
                    break;
                case (1, 4):
                    Count(own.Samples[p], codec[p * 4]);
                    break;
                case (3, 4):
                    for (var i = 0; i < 3; i++) Count(own.Samples[p * 3 + i], codec[p * 4 + i]);
                    break;
                case (4, 4):
                {
                    int k = own.Samples[p * 4 + 3];
                    for (var i = 0; i < 3; i++) Count(MulDiv255(own.Samples[p * 4 + i], k), codec[p * 4 + i]);
                    break;
                }
                default:
                    return (255, pixels);
            }
        }
        return (max, over);
    }

    private static int MulDiv255(int a, int b)
    {
        var x = a * b + 128;
        return (x + (x >> 8)) >> 8;
    }
}
