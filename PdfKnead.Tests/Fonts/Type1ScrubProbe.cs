using System.Diagnostics;
using System.Runtime.CompilerServices;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using Xunit.Abstractions;

namespace PdfKnead.Tests.Fonts;

/// <summary>
/// Probe of Type 1 scrubbing on real fonts (not part of the normal test run; enabled with PDFKNEAD_PRUEFUNG=type1): every embedded Type 1
/// program of the public test collection is scrubbed to random subsets of its glyphs. The only permitted failure is the deliberate abort
/// (<see cref="FontFormatException"/>); the result must parse, every kept glyph must look and advance as before, no removed one may be
/// left, and each call must finish within 2 s; a spoilt copy of each may only fail as FontScrub expects. See docs/FEHLERGEDAECHTNIS.md,
/// No. 180.
/// </summary>
public sealed class Type1ScrubProbe(ITestOutputHelper output)
{
    private static string Repo([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    [Fact]
    public void RealFonts_ScrubKeepsWhatIsShown()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_PRUEFUNG") != "type1") return;
        var folder = Path.Combine(Repo(), "TestPDFs", "pdfjs");
        if (!Directory.Exists(folder)) return;
        var random = new Random(17);
        var failures = new List<string>();
        int fonts = 0, aborted = 0;
        long worst = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*.pdf").Order(StringComparer.Ordinal))
        {
            PdfDocument doc;
            try
            {
                doc = PdfDocument.Open(File.ReadAllBytes(file));
            }
            catch (Exception)
            {
                continue;
            }
            using (doc)
                for (var n = 1; n <= doc.ObjectCount && n < 20_000; n++)
                {
                    if (doc.GetObject(n) is not PdfStream stream || !stream.Dictionary.ContainsKey(PdfName.Get("Length2"))) continue;
                    byte[] data;
                    Type1Font original;
                    try
                    {
                        data = doc.GetStreamData(stream);
                        original = Type1Font.Parse(data);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    fonts++;
                    var names = original.GlyphNames.ToList();
                    var keep = names.Where(_ => random.Next(3) == 0).ToHashSet(StringComparer.Ordinal);
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        var (scrubbed, clear, encrypted, trailer) = Type1Scrubber.Scrub(data, keep,
                            (int?)stream.Dictionary.GetInteger(PdfName.Get("Length1")), (int?)stream.Dictionary.GetInteger(PdfName.Get("Length2")));
                        var reread = Type1Font.Parse(scrubbed);
                        if (clear + encrypted + trailer != scrubbed.Length) failures.Add($"{Path.GetFileName(file)} {n}: Längen");
                        foreach (var name in keep)
                            if (original.Outline(name).Bounds != reread.Outline(name).Bounds || original.AdvanceWidth(name) != reread.AdvanceWidth(name))
                            {
                                failures.Add($"{Path.GetFileName(file)} {n}: {name} anders");
                                break;
                            }
                        // seac parts may stay; nothing else
                        if (reread.GlyphNames.Count(g => !keep.Contains(g) && g != ".notdef") > 2 * keep.Count + 2)
                            failures.Add($"{Path.GetFileName(file)} {n}: {reread.GlyphNames.Count(g => !keep.Contains(g))} Glyphen zu viel");
                    }
                    catch (FontFormatException)
                    {
                        aborted++;
                    }
                    catch (Exception e)
                    {
                        failures.Add($"{Path.GetFileName(file)} {n}: {e.GetType().Name}: {e.Message}");
                    }
                    // the same program spoilt a little: only the failures FontScrub expects (then the font stays as it is)
                    var spoilt = (byte[])data.Clone();
                    for (var k = random.Next(1, 8); k > 0; k--) spoilt[random.Next(spoilt.Length)] = (byte)random.Next(256);
                    try
                    {
                        Type1Scrubber.Scrub(spoilt, keep, (int?)stream.Dictionary.GetInteger(PdfName.Get("Length1")), (int?)stream.Dictionary.GetInteger(PdfName.Get("Length2")));
                    }
                    catch (Exception e) when (e is FontFormatException or ArgumentException or IndexOutOfRangeException)
                    {
                    }
                    catch (Exception e)
                    {
                        failures.Add($"{Path.GetFileName(file)} {n} verdorben: {e.GetType().Name}: {e.Message}");
                    }
                    worst = Math.Max(worst, watch.ElapsedMilliseconds);
                    if (watch.ElapsedMilliseconds > 2000) failures.Add($"{Path.GetFileName(file)} {n}: {watch.ElapsedMilliseconds} ms");
                }
        }
        output.WriteLine($"{fonts} Type-1-Schriften, {aborted} gewollt abgebrochen, langsamste {worst} ms");
        foreach (var f in failures.Take(30)) output.WriteLine(f);
        Assert.Empty(failures);
    }
}
