using System.Diagnostics;
using System.Runtime.CompilerServices;
using PdfKnead.Core.Fonts;
using Xunit.Abstractions;

namespace PdfKnead.Tests.Fonts;

/// <summary>
/// Robustness probe for CFF subsetting (not part of the normal test run; enabled with PDFKNEAD_PRUEFUNG=cff, number of rounds via
/// PDFKNEAD_RUNDEN): CFF fonts are corrupted at random (bytes, ranges, numbers) and then subset. The only permitted failure is the
/// deliberate abort (<see cref="FontFormatException"/>); every call must finish within 2 s (the largest allocation is reported, not
/// asserted), and a successful subset must parse again. The same holds for scrubbing for redaction (<see cref="CffScrubber"/>).
/// </summary>
public sealed class CffSubsetProbe(ITestOutputHelper output)
{
    private static string Repo([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    [Fact]
    public void MutatedCff_SubsetIsSafe()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_PRUEFUNG") != "cff") return;
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("PDFKNEAD_RUNDEN"), out var n) ? n : 20_000;
        var sources = new List<(string Name, byte[] Data)>
        {
            ("FoxitSymbol", File.ReadAllBytes(Path.Combine(Repo(), "Schriften", "Standard", "FoxitSymbol.cff"))),
            ("FoxitDingbats", File.ReadAllBytes(Path.Combine(Repo(), "Schriften", "Standard", "FoxitDingbats.cff"))),
        };
        // Noto CJK (CID-keyed, subroutines in many font DICTs): only the CFF table, and fewer rounds (15 MB per copy)
        var noto = OpenTypeFont.Parse(File.ReadAllBytes(Path.Combine(Repo(), "Schriften", "CJK", "NotoSansCJK-Regular.ttc")));
        var table = noto.Table("CFF ")!.Value;
        sources.Add(("NotoCJK", noto.FileData.AsSpan(table.Offset, table.Length).ToArray()));

        var random = new Random(4711);
        var failures = new List<string>();
        long worstMs = 0, worstBytes = 0, subsets = 0, scrubs = 0, aborted = 0;
        for (var round = 0; round < rounds; round++)
        {
            var (name, original) = sources[round % 10 == 9 ? 2 : round % 2];
            if (name == "NotoCJK" && round % 100 != 99) (name, original) = sources[round % 2];
            var data = (byte[])original.Clone();
            Mutate(data, random);
            var glyphs = Enumerable.Range(0, 40).Select(_ => random.Next(-5, name == "NotoCJK" ? 66_000 : 300)).ToList();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            try
            {
                var subset = CffSubsetter.Subset(data, glyphs);
                subsets++;
                // Success: the subset must parse again (individual glyph outlines may come out empty)
                var reread = CffFont.Parse(subset);
                for (var g = 0; g < Math.Min(reread.GlyphCount, 50); g++) _ = reread.Outline(g);
            }
            catch (FontFormatException)
            {
                aborted++;
            }
            catch (Exception e)
            {
                if (failures.Count < 20) failures.Add($"{name} Runde {round}: {e.GetType().Name}: {e.Message}");
            }
            // the same font scrubbed for redaction: as safe, and the result parses again
            try
            {
                var (scrubbed, _) = CffScrubber.Scrub(data, glyphs);
                scrubs++;
                var reread = CffFont.Parse(scrubbed);
                for (var g = 0; g < Math.Min(reread.GlyphCount, 50); g++) _ = reread.Outline(g);
            }
            catch (FontFormatException)
            {
                aborted++;
            }
            catch (Exception e)
            {
                if (failures.Count < 20) failures.Add($"{name} Runde {round} (bereinigen): {e.GetType().Name}: {e.Message}");
            }
            var ms = watch.ElapsedMilliseconds;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            worstMs = Math.Max(worstMs, ms);
            if (name != "NotoCJK") worstBytes = Math.Max(worstBytes, bytes);
            if (ms > 2000 && failures.Count < 20) failures.Add($"{name} Runde {round}: {ms} ms");
        }
        output.WriteLine($"{rounds} Runden: {subsets} Teilmengen, {scrubs} bereinigt, {aborted} gewollt abgebrochen, langsamste {worstMs} ms, meiste Belegung {worstBytes >> 10} KB (ohne Noto)");
        foreach (var f in failures) output.WriteLine(f);
        Assert.Empty(failures);
    }

    /// <summary>Applies a few random changes: single bytes, flipped bits, subroutine calls or number bytes, copied ranges.</summary>
    private static void Mutate(byte[] data, Random random)
    {
        var count = random.Next(1, 8);
        for (var i = 0; i < count; i++)
        {
            var at = random.Next(data.Length);
            switch (random.Next(4))
            {
                case 0:
                    data[at] = (byte)random.Next(256);
                    break;
                case 1:
                    data[at] ^= (byte)(1 << random.Next(8));
                    break;
                case 2:
                    // A subroutine call (callsubr/callgsubr) or a number byte
                    data[at] = (byte)(random.Next(2) == 0 ? (random.Next(2) == 0 ? 10 : 29) : random.Next(32, 255));
                    break;
                default:
                    var length = Math.Min(random.Next(1, 64), data.Length - at);
                    var from = random.Next(data.Length - length);
                    Array.Copy(data, from, data, at, length);
                    break;
            }
        }
    }
}
