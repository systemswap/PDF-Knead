using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Tests.Ui;
using SkiaSharp;

namespace PdfKnead.Tests.Fonts;

/// <summary>
/// Type specimen of all bundled fonts, drawn directly from the glyph outlines. With
/// PDFKNEAD_SCREENSHOTS set, the test writes schriftprobe.png for visual inspection.
/// </summary>
public sealed class FontSampleTests
{
    [Fact]
    public void Sample_DrawsEveryFont()
    {
        const int width = 1400, lineHeight = 46;
        var lines = new List<(string Label, Func<int, (GlyphOutline Outline, double Advance)> Glyph, Matrix FontMatrix, string Text)>();

        foreach (var file in EmbeddedFonts.Standard.Where(f => f.EndsWith(".ttf")))
        {
            var font = EmbeddedFonts.StandardOpenType(file);
            lines.Add((file, c => Glyph(font, font.GlyphForUnicode(c)), font.FontMatrix, "Grüße aus Köln: ÄÖÜ äöü ß € 0123456789 (Max Mustermann)"));
        }
        var symbol = EmbeddedFonts.StandardCff("FoxitSymbol.cff");
        lines.Add(("FoxitSymbol.cff", c => ByName(symbol, GlyphNames.FromUnicode(c)), symbol.FontMatrix, "αβγδε ΣΩπ ∞ ≈ ≠ ∑ ∫ √"));
        var dingbats = EmbeddedFonts.StandardCff("FoxitDingbats.cff");
        lines.Add(("FoxitDingbats.cff", c => ByName(dingbats, "a" + (c - 'A' + 1)), dingbats.FontMatrix, "ABCDEFGHIJKLMNOPQRST"));
        foreach (var file in EmbeddedFonts.Handwriting)
        {
            var font = OpenTypeFont.Parse(EmbeddedFonts.LoadHandwriting(file));
            var note = ColorGlyphs.For(font) is not null ? " (Farbbilder)" : "";
            lines.Add((file + note, c => Glyph(font, font.GlyphForUnicode(c)), font.FontMatrix, "Max Mustermann – Unterschrift 15.10.2026"));
        }
        var type1 = Type1Font.Parse(Type1Builder.Build());
        lines.Add(("Type 1 (Test)", _ => (type1.Outline("A"), type1.AdvanceWidth("A")), type1.FontMatrix, "AAAA"));

        using var bitmap = new SKBitmap(width, lineHeight * lines.Count + 20);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var ink = new SKPaint { IsAntialias = true, Color = SKColors.Black };
        using var label = new SKPaint { IsAntialias = true, Color = new SKColor(0x80, 0x80, 0x80) };
        using var labelFont = new SKFont(SKTypeface.Default, 12);

        var drawn = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var (name, glyph, fontMatrix, text) = lines[i];
            var baseline = 20 + i * lineHeight + 30;
            canvas.DrawText(name, 10, baseline, labelFont, label);
            var x = 230.0;
            const double size = 28;
            foreach (var c in text)
            {
                var (outline, advance) = glyph(c);
                // font units → text space (FontMatrix) → pixels (size, y pointing down)
                var m = fontMatrix * new Matrix(size, 0, 0, -size, x, baseline);
                using var path = ToPath(outline, m);
                canvas.DrawPath(path, ink);
                if (!outline.IsEmpty) drawn++;
                x += fontMatrix.A * advance * size;
            }
        }
        Assert.True(drawn > 500);

        if (UiSession.ScreenshotFolder() is { } folder)
        {
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(folder, "schriftprobe.png"), data.ToArray());
        }
    }

    private static (GlyphOutline, double) Glyph(OpenTypeFont font, int glyph) => (font.Outline(glyph), font.AdvanceWidth(glyph));

    private static (GlyphOutline, double) ByName(CffFont font, string name)
    {
        var g = font.GlyphByName(name);
        return g > 0 ? (font.Outline(g), font.AdvanceWidth(g)) : (GlyphOutline.Empty, 500);
    }

    private static SKPath ToPath(GlyphOutline outline, Matrix m)
    {
        var path = new SKPath();
        var p = 0;
        var pts = outline.Points;
        foreach (var verb in outline.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    var (mx, my) = m.Transform(pts[p], pts[p + 1]);
                    path.MoveTo((float)mx, (float)my);
                    p += 2;
                    break;
                case PathVerb.Line:
                    var (lx, ly) = m.Transform(pts[p], pts[p + 1]);
                    path.LineTo((float)lx, (float)ly);
                    p += 2;
                    break;
                case PathVerb.Cubic:
                    var (x1, y1) = m.Transform(pts[p], pts[p + 1]);
                    var (x2, y2) = m.Transform(pts[p + 2], pts[p + 3]);
                    var (x3, y3) = m.Transform(pts[p + 4], pts[p + 5]);
                    path.CubicTo((float)x1, (float)y1, (float)x2, (float)y2, (float)x3, (float)y3);
                    p += 6;
                    break;
                case PathVerb.Close:
                    path.Close();
                    break;
            }
        }
        return path;
    }
}
