using System.Runtime.CompilerServices;
using System.Text;
using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// QR, Data Matrix and bar codes (KONZEPT.md, section 5.19): modules and bars as one filled path of rectangles — a single fill has no
/// seams between neighbouring modules, which separately filled rectangles would show with anti-aliasing —, the human-readable text below
/// bar codes in the element's sans-serif font, the background over the whole rectangle. A content that does not fit its kind is drawn
/// as a grey placeholder; the panel shows why.
/// </summary>
public sealed partial class ElementAppearance
{
    private const double PointsPerMm = 72 / 25.4;

    private static readonly ConditionalWeakTable<CodeElement, Tuple<CodeSymbol?, string?>> Symbols = new();

    /// <summary>The symbol of a code element, or why there is none.</summary>
    /// <param name="code">The element.</param>
    /// <returns>The symbol, or null and the message for the user.</returns>
    public static (CodeSymbol? Symbol, string? Error) CodeSymbolOf(CodeElement code)
    {
        var known = Symbols.GetValue(code, static c =>
        {
            try
            {
                return Tuple.Create<CodeSymbol?, string?>(CodeSymbol.Create(c.Format, c.Text, c.Gs1, c.Level, c.Shape), null);
            }
            catch (ArgumentException e)
            {
                return Tuple.Create<CodeSymbol?, string?>(null, e.Message);
            }
        });
        return (known.Item1, known.Item2);
    }

    /// <summary>Where the modules go in the element's box (reading orientation, points).</summary>
    /// <param name="Module">Size of a module.</param>
    /// <param name="Left">Left edge of the first module or bar.</param>
    /// <param name="Bottom">2D: lower edge of the last row; bar codes: lower edge of the bars.</param>
    /// <param name="LongBottom">Bar codes: lower edge of the bars reaching down.</param>
    /// <param name="FontSize">Bar codes: size of the human-readable text (0 without).</param>
    private readonly record struct CodeLayout(double Module, double Left, double Bottom, double LongBottom, double FontSize);

    private static CodeLayout LayoutCode(CodeElement code, CodeSymbol symbol)
    {
        var (w, h) = code.UprightSize;
        w = Math.Max(w, 0);
        h = Math.Max(h, 0);
        if (symbol.Matrix is { } m)
        {
            var q = code.QuietZone ? symbol.QuietLeft : 0;
            var module = Math.Min(w / (m.Width + 2 * q), h / (m.Height + 2 * q));
            return new CodeLayout(module, (w - m.Width * module) / 2, (h - m.Height * module) / 2, 0, 0);
        }
        var n = symbol.Bars!.Length;
        // without quiet zone the text beside EAN/UPC still needs its room
        var captions = code.Caption ? symbol.Captions : [];
        double left = code.QuietZone ? symbol.QuietLeft : Math.Max(0, -captions.Select(c => c.From).DefaultIfEmpty(0).Min());
        double right = code.QuietZone ? symbol.QuietRight : Math.Max(0, captions.Select(c => c.To - n).DefaultIfEmpty(0).Max());
        var mod = w / (left + n + right);
        // the text about as high as on retail packs (9 modules for the nominal size), at least 3 pt, but never more than a quarter of the
        // height — in a very flat box (from a crafted file, too) the quarter wins (Fehlergedächtnis 218)
        var size = captions.Count == 0 ? 0 : Math.Min(Math.Max(9 * mod, 3), h / 4);
        var bottom = captions.Count == 0 ? 0 : size * 0.95;
        var longBottom = symbol.LongBars.Count == 0 ? bottom : Math.Max(0, bottom - 5 * mod);
        return new CodeLayout(mod, left * mod, bottom, longBottom, size);
    }

    /// <summary>Module size of a code element in millimetres; 0 without a symbol.</summary>
    /// <param name="code">The element.</param>
    /// <returns>The width of one module.</returns>
    public static double CodeModuleMm(CodeElement code) =>
        CodeSymbolOf(code).Symbol is { } symbol ? LayoutCode(code, symbol).Module / PointsPerMm : 0;

    /// <summary>
    /// Size for a code placed with a click, in points (reading orientation): 2D symbols with modules of 0.5 mm, at least 15 and at most
    /// 60 mm; bar codes at the nominal 0.33 mm per module with quiet zones, EAN/UPC as high as on retail packs, the others 15 mm.
    /// </summary>
    /// <param name="code">The element (its content decides the number of modules).</param>
    /// <returns>Width and height.</returns>
    public static (double Width, double Height) DefaultCodeSize(CodeElement code)
    {
        var symbol = CodeSymbolOf(code).Symbol;
        var nominal = CodeSymbol.NominalModuleMm(code.Format) * PointsPerMm;
        if (code.IsMatrix)
        {
            if (symbol is null) return (25 * PointsPerMm, 25 * PointsPerMm);
            var across = Math.Clamp(symbol.ModulesAcross * nominal, 15 * PointsPerMm, 60 * PointsPerMm);
            return (across, across * symbol.ModulesDown / symbol.ModulesAcross);
        }
        var width = symbol is null ? 40 * PointsPerMm : symbol.ModulesAcross * nominal;
        var height = code.Format is BarcodeFormat.Ean13 or BarcodeFormat.Ean8 or BarcodeFormat.UpcA or BarcodeFormat.UpcE ? 25.9 * PointsPerMm : 15 * PointsPerMm;
        return (width, height);
    }

    private static ElementAppearance BuildCode(CodeElement code) => BuildCode(code, code, blankWhenEmpty: false);

    /// <summary>
    /// Code field (KONZEPT.md, section 5.19, step 4): the value drawn as the code of the field's look, in the field's box and rotation; an
    /// empty field shows only its background (the display marks it on screen).
    /// </summary>
    private static ElementAppearance BuildCodeField(PageElement owner, PageElement element, CodeLook look, string value) =>
        BuildCode(owner, look.Apply(new CodeElement { Rect = element.Rect, Rotation = element.Rotation, Text = value }), blankWhenEmpty: true);

    private static ElementAppearance BuildCode(PageElement owner, CodeElement code, bool blankWhenEmpty)
    {
        var (w, h) = code.UprightSize;
        // no clip to the box: everything stays inside it anyway, and a clip on the code's edge (without quiet zone) lightened the outer
        // modules with its own anti-aliasing — small codes were no longer found on the page
        var sb = new StringBuilder("q\n");
        var resources = new PdfDictionary();
        var glyphs = new Dictionary<int, string>();
        ElementFace? face = null;
        if (code.Background is { } background) sb.Append(Rgb(background)).Append(" rg 0 0 ").Append(ElementFace.Number(w)).Append(' ').Append(ElementFace.Number(h)).Append(" re f\n");
        if (CodeSymbolOf(code).Symbol is not { } symbol)
        {
            if (blankWhenEmpty && code.Text.Length == 0)
            {
                sb.Append("Q\n");
                return new ElementAppearance(owner, FormStream(code, resources, Encoding.ASCII.GetBytes(sb.ToString())), null, NoGlyphs);
            }
            // placeholder: grey frame with a cross — nothing a scanner could take for a code
            sb.Append("0.6 0.6 0.6 RG 1 w [3 2] 0 d 0.5 0.5 ").Append(P(w - 1, h - 1)).Append(" re S [] 0 d 0.5 0.5 m ").Append(P(w - 0.5, h - 0.5))
              .Append(" l 0.5 ").Append(ElementFace.Number(h - 0.5)).Append(" m ").Append(ElementFace.Number(w - 0.5)).Append(" 0.5 l S\n");
        }
        else
        {
            var layout = LayoutCode(code, symbol);
            var mod = layout.Module;
            sb.Append(Rgb(code.Color)).Append(" rg\n");
            if (symbol.Matrix is { } m)
            {
                // runs of dark modules per row, all in one path
                for (var y = 0; y < m.Height; y++)
                {
                    var bottom = layout.Bottom + (m.Height - 1 - y) * mod;
                    for (var x = 0; x < m.Width; x++)
                    {
                        if (!m[x, y]) continue;
                        var start = x;
                        while (x + 1 < m.Width && m[x + 1, y]) x++;
                        AppendRect(sb, layout.Left + start * mod, bottom, (x + 1 - start) * mod, mod);
                    }
                }
            }
            else
            {
                var bars = symbol.Bars!;
                // ITF-14: bearer bars of twice the narrow bar above and below the bars, across them (No. 608)
                var bearer = symbol.BearerBars ? 2 * mod : 0;
                var top = h - bearer;
                for (var x = 0; x < bars.Length; x++)
                {
                    if (!bars[x]) continue;
                    var start = x;
                    while (x + 1 < bars.Length && bars[x + 1]) x++;
                    // a bar reaches down if it lies in a guard pattern (they consist of whole bars)
                    var reaches = symbol.LongBars.Any(g => start >= g.Start && start < g.Start + g.Length);
                    var bottom = (reaches ? layout.LongBottom : layout.Bottom) + bearer;
                    AppendRect(sb, layout.Left + start * mod, bottom, (x + 1 - start) * mod, top - bottom);
                }
                if (bearer > 0)
                {
                    AppendRect(sb, layout.Left, layout.Bottom, bars.Length * mod, bearer);
                    AppendRect(sb, layout.Left, top, bars.Length * mod, bearer);
                }
            }
            sb.Append("f\n");
            if (layout.FontSize > 0)
            {
                face = ElementFace.For(ElementFont.Sans);
                sb.Append("BT\n");
                foreach (var caption in symbol.Captions)
                {
                    var ids = new List<int>();
                    var width = 0.0;
                    foreach (var rune in caption.Text.EnumerateRunes())
                    {
                        var glyph = face.Glyph(rune.Value);
                        ids.Add(glyph);
                        glyphs.TryAdd(glyph, rune.ToString());
                        width += face.Advance(glyph);
                    }
                    // narrower slot than the text (long Code 128 content): smaller, never wider than its bars
                    var slot = (caption.To - caption.From) * mod;
                    var size = Math.Min(layout.FontSize, width > 0 ? slot / width : layout.FontSize);
                    var x = layout.Left + (caption.From + caption.To) / 2 * mod - width * size / 2;
                    sb.Append("/F1 ").Append(ElementFace.Number(size)).Append(" Tf 1 0 0 1 ").Append(P(x, Math.Max(0, layout.Bottom - layout.FontSize * 0.9))).Append(" Tm [");
                    ElementFace.AppendHex(sb, ids);
                    sb.Append("] TJ\n");
                }
                sb.Append("ET\n");
                var fonts = new PdfDictionary();
                fonts.Set(PdfName.Get("F1"), face.DisplayFont);
                resources.Set(PdfName.Get("Font"), fonts);
            }
        }
        sb.Append("Q\n");
        return new ElementAppearance(owner, FormStream(code, resources, Encoding.ASCII.GetBytes(sb.ToString())), face, face is null ? NoGlyphs : glyphs);
    }

    private static void AppendRect(StringBuilder sb, double x, double y, double width, double height) =>
        sb.Append(ElementFace.Number(x)).Append(' ').Append(ElementFace.Number(y)).Append(' ').Append(ElementFace.Number(width)).Append(' ')
          .Append(ElementFace.Number(height)).Append(" re\n");

    /// <summary>
    /// Symbol contrast of a code: reflectance of the background (white paper where it is transparent) minus that of the modules, from
    /// their relative luminance (sRGB). ISO/IEC 15415 and 15416 grade a contrast of 40 % or more as acceptable (C), below 20 % as failed;
    /// a negative value means light modules on a dark background, which many scanners cannot read.
    /// </summary>
    /// <param name="code">The element.</param>
    /// <returns>The contrast from −1 to 1.</returns>
    public static double CodeContrast(CodeElement code) => Luminance(code.Background ?? 0xFFFFFF) - Luminance(code.Color);

    private static double Luminance(int rgb)
    {
        static double Linear(int c) => c / 255.0 <= 0.04045 ? c / 255.0 / 12.92 : Math.Pow((c / 255.0 + 0.055) / 1.055, 2.4);
        return 0.2126 * Linear((rgb >> 16) & 0xFF) + 0.7152 * Linear((rgb >> 8) & 0xFF) + 0.0722 * Linear(rgb & 0xFF);
    }

    /// <summary>The alternative text of a code: its own, or the kind and the content (shortened).</summary>
    /// <param name="code">The element.</param>
    /// <returns>The text for screen readers.</returns>
    public static string CodeAltText(CodeElement code)
    {
        if (code.AltText.Trim().Length > 0) return code.AltText.Trim();
        var content = code.Text.Length > 500 ? code.Text[..500] + "…" : code.Text;
        return CodeSymbol.Name(code.Format) + ": " + content;
    }
}
