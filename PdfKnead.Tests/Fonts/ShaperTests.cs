using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using Xunit.Abstractions;
using HB = HarfBuzzSharp;

namespace PdfKnead.Tests.Fonts;

/// <summary>
/// Compares the built-in shaper with HarfBuzz as the reference. HarfBuzz ships with Avalonia anyway and
/// serves only for comparison here; the program shapes text with its own code.
/// </summary>
public sealed class ShaperTests(ITestOutputHelper output)
{
    private static readonly string[] Samples =
    [
        "Max Mustermann",
        "Musterstraße 12, 12345 Musterstadt",
        "The quick brown fox jumps over the lazy dog",
        "office affinity fifty ffl ffi",
        "AVATAR Toyota WAVE Tr. Yo. P.",
        "Herzliche Grüße – Ihr Max",
        "Äpfel Öfen Übel äöüß ÄÖÜ",
        "minimum illumination",
    ];

    internal static (int[] Glyphs, int[] Advances, int[] XOffsets, int[] YOffsets) HarfBuzz(byte[] data, int unitsPerEm, string text, string[] features)
    {
        // HarfBuzz copies the font data immediately (Duplicate). With Blob.FromStream it reads the data only while shaping, and under
        // load on Linux it once returned glyph 0 for an entire text (AmaticSC) while the built-in shaper was correct
        HB.Blob blob;
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(data, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            blob = new HB.Blob(pinned.AddrOfPinnedObject(), data.Length, HB.MemoryMode.Duplicate);
        }
        finally
        {
            pinned.Free();
        }
        using var _ = blob;
        using var face = new HB.Face(blob, 0);
        using var font = new HB.Font(face);
        font.SetScale(unitsPerEm, unitsPerEm);
        using var buffer = new HB.Buffer();
        buffer.AddUtf16(text);
        buffer.GuessSegmentProperties();
        font.Shape(buffer, features.Select(HB.Feature.Parse).ToArray());
        var infos = buffer.GlyphInfos;
        var positions = buffer.GlyphPositions;
        return ([.. infos.Select(i => (int)i.Codepoint)], [.. positions.Select(p => p.XAdvance)],
            [.. positions.Select(p => p.XOffset)], [.. positions.Select(p => p.YOffset)]);
    }

    public static IEnumerable<object[]> Fonts() =>
        EmbeddedFonts.Handwriting.Where(n => n.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
            .Select(n => new object[] { n, true })
            .Concat(new[] { "LiberationSans-Regular.ttf", "LiberationSerif-Regular.ttf", "LiberationMono-Regular.ttf" }.Select(n => new object[] { n, false }));

    [Theory]
    [MemberData(nameof(Fonts))]
    public void SameGlyphsAndPositionsAsHarfBuzz(string file, bool handwriting)
    {
        var data = handwriting ? EmbeddedFonts.LoadHandwriting(file) : EmbeddedFonts.LoadStandard(file);
        var font = OpenTypeFont.Parse(data);
        var shaper = TextShaper.For(font);
        // For Latin script HarfBuzz also applies rclt, rvrn and others; they do not affect these fonts
        var mismatches = new List<string>();
        foreach (var text in Samples)
        {
            var own = shaper.Shape(text);
            var hb = HarfBuzz(data, font.UnitsPerEm, text, []);
            var glyphs = own.Select(g => g.Glyph).ToArray();
            if (!glyphs.SequenceEqual(hb.Glyphs))
            {
                mismatches.Add($"„{text}“ Glyphen: eigen {string.Join(" ", glyphs)} | HarfBuzz {string.Join(" ", hb.Glyphs)}");
                continue;
            }
            // Compare the position of every glyph (pen plus offset): HarfBuzz may, for example, split the legacy kern table
            // between both glyphs of a pair, but the resulting positions are the same
            double penOwn = 0, penHb = 0;
            for (var i = 0; i < own.Length; i++)
            {
                double xOwn = penOwn + own[i].XOffset, xHb = penHb + hb.XOffsets[i];
                if (Math.Abs(xOwn - xHb) > 1 || Math.Abs(own[i].YOffset - hb.YOffsets[i]) > 1)
                {
                    mismatches.Add($"„{text}“ Glyphe {i} ({own[i].Glyph}): eigen x {xOwn}/y {own[i].YOffset} | HarfBuzz x {xHb}/y {hb.YOffsets[i]}");
                    break;
                }
                penOwn += own[i].XAdvance;
                penHb += hb.Advances[i];
            }
            if (Math.Abs(penOwn - penHb) > 1) mismatches.Add($"„{text}“ Breite: eigen {penOwn} | HarfBuzz {penHb}");
        }
        foreach (var m in mismatches) output.WriteLine(m);
        Assert.True(mismatches.Count == 0, $"{file}: {mismatches.Count} Abweichungen\n{string.Join("\n", mismatches)}");
    }

    /// <summary>
    /// Cursive attachment as the OpenType specification describes it (GPOS lookup type 3; written from it on 16.11.2026): the exit anchor of
    /// a glyph meets the entry anchor of the next — along the line through the advance of the first glyph, across it through the placement of
    /// the second; with the RIGHT_TO_LEFT flag of the first, and the last glyph of the connected sequence keeps its place.
    /// </summary>
    [Fact]
    public void CursiveJoin_AnchorsMeet_AsTheSpecificationSays()
    {
        static double ExitX(List<ShapedGlyph> run, int i, int x) => Enumerable.Range(0, i).Sum(k => run[k].XAdvance) + run[i].XOffset + x;
        var run = new List<ShapedGlyph>
        {
            new() { Glyph = 1, XAdvance = 500, XOffset = 5, YOffset = 2 },
            new() { Glyph = 2, XAdvance = 400, XOffset = 3 },
        };
        var chain = new List<int>();
        TextShaper.Join(run, 0, 1, (450, 100), (20, 30), rightToLeft: false, chain);
        Assert.Equal(ExitX(run, 0, 450), ExitX(run, 1, 20));                   // along the line: the anchors meet
        Assert.Equal(run[0].YOffset + 100, run[1].YOffset + 30);               // across it: the second moved
        Assert.Equal(2, run[0].YOffset);
        Assert.Equal(400, run[1].XAdvance);                                    // only the advance of the first changes

        // RIGHT_TO_LEFT: the first moves across the line, those joined before it move with it, the last keeps its place
        run = [new() { Glyph = 1, XAdvance = 300 }, new() { Glyph = 2, XAdvance = 300 }, new() { Glyph = 3, XAdvance = 300 }];
        chain.Clear();
        TextShaper.Join(run, 0, 1, (280, 50), (10, 10), rightToLeft: true, chain);
        TextShaper.Join(run, 1, 2, (290, 60), (5, 0), rightToLeft: true, chain);
        Assert.Equal(0, run[2].YOffset);
        Assert.Equal(run[0].YOffset + 50, run[1].YOffset + 10);
        Assert.Equal(run[1].YOffset + 60, run[2].YOffset + 0);
        Assert.Equal(ExitX(run, 1, 290), ExitX(run, 2, 5));
    }

    /// <summary>A positioning feature switched on goes to GPOS: kerning switched off and on again applies (docs/FEHLERGEDAECHTNIS.md, No. 271).</summary>
    [Fact]
    public void PositioningFeature_CanBeSwitchedOn()
    {
        var shaper = TextShaper.For(OpenTypeFont.Parse(EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf")));
        static double Width(ShapedGlyph[] run) => run.Sum(g => g.XAdvance);
        var kerned = Width(shaper.Shape("AVAV"));
        Assert.NotEqual(kerned, Width(shaper.Shape("AVAV", ["-kern"])));
        Assert.Equal(kerned, Width(shaper.Shape("AVAV", ["-kern", "kern"])));
    }

    [Fact]
    public void Ligatures_KeepTheirText_AndCanBeSwitchedOff()
    {
        const string text = "Griffin fliegt";
        var font = OpenTypeFont.Parse(EmbeddedFonts.LoadHandwriting("PatrickHand-Regular.ttf"));
        var shaper = TextShaper.For(font);
        var shaped = shaper.Shape(text);
        Assert.Equal(text, string.Concat(shaped.Select(g => g.Text)));
        Assert.True(shaped.Length < text.Length);   // ligatures
        var plain = shaper.Shape(text, ["-liga", "-calt", "-kern"]);
        Assert.Equal(text.Length, plain.Length);
        Assert.Equal(text.Select(c => font.GlyphForUnicode(c)), plain.Select(g => g.Glyph));
    }
}
